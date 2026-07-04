using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FullParty.Api;
using FullParty.Models;

namespace FullParty.Services;

public enum RealtimeRunRoomState
{
    Disconnected,
    Connecting,
    Authorizing,
    Subscribing,
    Connected,
    Error,
}

public sealed class RealtimeRunRoomClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly int runId;
    private readonly Plugin plugin;
    private readonly FullPartyApiClient apiClient;
    private readonly object stateLock = new();
    private readonly Dictionary<string, FullPartyLiveMember> members = new(StringComparer.Ordinal);
    private readonly HashSet<string> handledCommandIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<PendingCommandExecution> pendingCommandExecutions = new();
    private const int CommandExpirySeconds = 30;

    private CancellationTokenSource? cancellation;
    private Task? connectionTask;
    private Task? commandIssueTask;
    private ClientWebSocket? webSocket;

    public RealtimeRunRoomClient(int runId, Plugin plugin)
    {
        this.runId = runId;
        this.plugin = plugin;
        apiClient = plugin.ApiClient;

        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public RealtimeRunRoomState State { get; private set; } = RealtimeRunRoomState.Disconnected;
    public string StatusMessage { get; private set; } = "Disconnected";
    public string? CommandStatusMessage { get; private set; }
    public string? ChannelName { get; private set; }

    public bool IsActive => State is RealtimeRunRoomState.Connecting
        or RealtimeRunRoomState.Authorizing
        or RealtimeRunRoomState.Subscribing
        or RealtimeRunRoomState.Connected;

    public bool IsBusy => State is RealtimeRunRoomState.Connecting
        or RealtimeRunRoomState.Authorizing
        or RealtimeRunRoomState.Subscribing;

    public bool IsIssuingCommand => commandIssueTask is { IsCompleted: false };

    public IReadOnlyList<FullPartyLiveMember> Members
    {
        get
        {
            lock (stateLock)
            {
                return members.Values
                    .OrderBy(member => member.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(member => member.UserName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
    }

    public void Connect()
    {
        lock (stateLock)
        {
            if (connectionTask is { IsCompleted: false })
                return;

            cancellation?.Cancel();
            cancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            members.Clear();
            handledCommandIds.Clear();
            SetStateNoLock(RealtimeRunRoomState.Connecting, "Connecting to live room...");
            connectionTask = Task.Run(() => RunAsync(cancellation.Token));
        }
    }

    public void Disconnect()
    {
        CancellationTokenSource? currentCancellation;
        ClientWebSocket? currentSocket;

        lock (stateLock)
        {
            currentCancellation = cancellation;
            currentSocket = webSocket;
            cancellation = null;
            webSocket = null;
            members.Clear();
            handledCommandIds.Clear();
            ChannelName = null;
            SetStateNoLock(RealtimeRunRoomState.Disconnected, "Disconnected");
        }

        currentCancellation?.Cancel();
        currentSocket?.Dispose();
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Disconnect();
    }

    public void SendReadyCheckAlliance()
    {
        StartCommandIssue(
            "ready_check",
            "all_assigned",
            new Dictionary<string, object?>
            {
                ["message"] = "Ready check started",
            },
            "Ready check alliance");
    }

    public void SendCountdown(int seconds)
    {
        StartCommandIssue(
            "countdown",
            "party_leads",
            new Dictionary<string, object?>
            {
                ["seconds"] = seconds,
                ["label"] = "Pull timer",
            },
            $"Countdown {seconds}s");
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = await apiClient.GetRealtimeConfigAsync(cancellationToken);
            var channelName = config.GetRunChannelName(runId);
            var websocketUri = BuildWebSocketUri(config);

            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            lock (stateLock)
            {
                webSocket = socket;
                ChannelName = channelName;
                SetStateNoLock(RealtimeRunRoomState.Connecting, "Opening websocket...");
            }

            await socket.ConnectAsync(websocketUri, cancellationToken);
            var socketId = await WaitForSocketIdAsync(socket, cancellationToken);
            if (string.IsNullOrWhiteSpace(socketId))
                throw new InvalidOperationException("FullParty live room did not return a socket id.");

            SetState(RealtimeRunRoomState.Authorizing, "Authorizing live room...");
            var channelAuth = await apiClient.AuthorizeRealtimeChannelAsync(config.AuthEndpoint, socketId, channelName, cancellationToken);

            SetState(RealtimeRunRoomState.Subscribing, "Joining live room...");
            var subscriptionData = new Dictionary<string, object?>
            {
                ["channel"] = channelName,
                ["auth"] = channelAuth.Auth,
            };

            if (!string.IsNullOrWhiteSpace(channelAuth.ChannelData))
                subscriptionData["channel_data"] = channelAuth.ChannelData;

            await SendEventAsync(socket, "pusher:subscribe", subscriptionData, cancellationToken);

            await ReceiveLoopAsync(socket, channelName, config, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "FullParty live room connection failed for run {RunId}.", runId);
            lock (stateLock)
            {
                members.Clear();
                SetStateNoLock(RealtimeRunRoomState.Error, ex.Message);
            }
        }
        finally
        {
            lock (stateLock)
            {
                webSocket = null;
                if (State != RealtimeRunRoomState.Error)
                    SetStateNoLock(RealtimeRunRoomState.Disconnected, "Disconnected");
            }
        }
    }

    private async Task<string?> WaitForSocketIdAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await ReceiveMessageAsync(socket, cancellationToken);
            if (message == null)
                return null;

            var envelope = DeserializeEnvelope(message);
            if (envelope == null)
                continue;

            if (envelope.Event.Equals("pusher:connection_established", StringComparison.OrdinalIgnoreCase))
            {
                using var data = ParseDataDocument(envelope.Data);
                return data == null ? null : GetString(data.RootElement, "socket_id");
            }

            if (envelope.Event.Equals("pusher:error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(GetPusherError(envelope.Data));
        }

        return null;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, string channelName, FullPartyRealtimeConfig config, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var message = await ReceiveMessageAsync(socket, cancellationToken);
            if (message == null)
                return;

            var envelope = DeserializeEnvelope(message);
            if (envelope == null)
                continue;

            if (envelope.Event.Equals("pusher:ping", StringComparison.OrdinalIgnoreCase))
            {
                await SendEventAsync(socket, "pusher:pong", new { }, cancellationToken);
                continue;
            }

            if (envelope.Event.Equals("pusher:error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(GetPusherError(envelope.Data));

            if (!string.IsNullOrWhiteSpace(envelope.Channel) &&
                !envelope.Channel.Equals(channelName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            switch (envelope.Event)
            {
                case "pusher_internal:subscription_succeeded":
                    using (var data = ParseDataDocument(envelope.Data))
                    {
                        if (data != null)
                            ReplaceMembers(ParseSubscriptionMembers(data.RootElement));
                    }

                    SetState(RealtimeRunRoomState.Connected, "Connected to live room");
                    break;
                case "pusher_internal:member_added":
                    using (var data = ParseDataDocument(envelope.Data))
                    {
                        var member = data == null ? null : ParseMemberAdded(data.RootElement);
                        if (member != null)
                            AddMember(member);
                    }

                    break;
                case "pusher_internal:member_removed":
                    using (var data = ParseDataDocument(envelope.Data))
                    {
                        var userId = data == null ? null : GetString(data.RootElement, "user_id");
                        if (!string.IsNullOrWhiteSpace(userId))
                            RemoveMember(userId);
                    }

                    break;
            }

            if (EventMatches(envelope.Event, config.CommandEventName))
            {
                using var data = ParseDataDocument(envelope.Data);
                if (data != null)
                    HandleRunCommand(data.RootElement);
            }
        }
    }

    private void StartCommandIssue(string command, string targetType, object payload, string label)
    {
        CancellationToken token;
        lock (stateLock)
        {
            if (State != RealtimeRunRoomState.Connected)
            {
                CommandStatusMessage = "Connect to the live room first.";
                return;
            }

            if (commandIssueTask is { IsCompleted: false })
                return;

            if (cancellation == null)
            {
                CommandStatusMessage = "Live room is not connected.";
                return;
            }

            token = cancellation.Token;
            CommandStatusMessage = $"Sending {label}...";
            var idempotencyKey = CreateIdempotencyKey(command);
            commandIssueTask = Task.Run(() => SendCommandAsync(command, targetType, payload, idempotencyKey, label, token), token);
        }
    }

    private async Task SendCommandAsync(string command, string targetType, object payload, string idempotencyKey, string label, CancellationToken cancellationToken)
    {
        try
        {
            await apiClient.SendRunCommandAsync(runId, command, targetType, payload, CommandExpirySeconds, idempotencyKey, cancellationToken);
            SetCommandStatus($"{label} sent. Waiting for live command...");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not send FullParty live command {Command} for run {RunId}.", command, runId);
            SetCommandStatus($"Could not send {label}: {ex.Message}");
        }
    }

    private void HandleRunCommand(JsonElement root)
    {
        var command = ParseRunCommand(root);
        if (command == null)
            return;

        lock (stateLock)
        {
            if (!handledCommandIds.Add(command.Id))
                return;
        }

        if (command.ExpiresAt != null && DateTimeOffset.UtcNow > command.ExpiresAt.Value)
        {
            QueueAck(command.Id, "expired");
            return;
        }

        if (!IsTargeted(command))
        {
            QueueAck(command.Id, "ignored_not_targeted");
            return;
        }

        var localCommand = GetLocalCommand(command);
        if (localCommand == null)
        {
            QueueAck(command.Id, "failed");
            SetCommandStatus($"Unsupported live command: {command.Command}");
            return;
        }

        pendingCommandExecutions.Enqueue(new PendingCommandExecution(command.Id, command.Command, localCommand));
        SetCommandStatus($"Received {FormatCommandName(command.Command)}.");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        while (pendingCommandExecutions.TryDequeue(out var pending))
        {
            try
            {
                Plugin.CommandManager.ProcessCommand(pending.LocalCommand);
                QueueAck(pending.CommandId, "executed");
                SetCommandStatus($"Executed {FormatCommandName(pending.CommandName)}.");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Could not execute FullParty live command {Command} for run {RunId}.", pending.CommandName, runId);
                QueueAck(pending.CommandId, "failed");
                SetCommandStatus($"Failed to execute {FormatCommandName(pending.CommandName)}.");
            }
        }
    }

    private void QueueAck(string commandId, string status)
    {
        var token = cancellation?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await apiClient.AcknowledgeRunCommandAsync(runId, commandId, status, token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Could not acknowledge FullParty live command {CommandId} as {Status}.", commandId, status);
            }
        }, CancellationToken.None);
    }

    private bool IsTargeted(FullPartyRunCommand command)
    {
        var currentUserId = plugin.AuthService.User?.Id.ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(currentUserId))
            return false;

        FullPartyLiveMember? currentMember;
        lock (stateLock)
        {
            members.TryGetValue(currentUserId, out currentMember);
        }

        if (command.ResolvedUserIds.Count > 0)
            return command.ResolvedUserIds.Contains(currentUserId, StringComparer.OrdinalIgnoreCase);

        if (command.ResolvedSlotIds.Count > 0)
            return currentMember?.SlotIds.Any(command.ResolvedSlotIds.Contains) == true;

        return command.TargetType switch
        {
            "party_leads" => currentMember?.IsPartyLead == true,
            "hosts" => currentMember?.IsHost == true,
            "all_assigned" => currentMember?.SlotIds.Count > 0,
            _ => false,
        };
    }

    private static Uri BuildWebSocketUri(FullPartyRealtimeConfig config)
    {
        var host = config.Host.Trim();
        var path = string.IsNullOrWhiteSpace(config.Path) ? "/app" : config.Path;
        var scheme = config.ForceTls ? "wss" : "ws";
        var port = config.ForceTls ? config.WssPort : config.WsPort;

        if (Uri.TryCreate(host, UriKind.Absolute, out var hostUri))
        {
            scheme = hostUri.Scheme switch
            {
                "https" or "wss" => "wss",
                "http" or "ws" => "ws",
                _ => scheme,
            };

            host = hostUri.Host;
            if (!hostUri.IsDefaultPort)
                port = hostUri.Port;

            if (!string.IsNullOrWhiteSpace(hostUri.AbsolutePath) && hostUri.AbsolutePath != "/")
                path = hostUri.AbsolutePath;
        }
        else if (host.Contains(':', StringComparison.Ordinal) &&
                 Uri.TryCreate($"ws://{host}", UriKind.Absolute, out var hostWithPort) &&
                 !hostWithPort.IsDefaultPort)
        {
            host = hostWithPort.Host;
            port = hostWithPort.Port;
        }

        var builder = new UriBuilder(scheme, host)
        {
            Path = $"{path.TrimEnd('/')}/{Uri.EscapeDataString(config.AppKey)}",
            Query = "protocol=7&client=fullparty-dalamud&version=0.0.1&flash=false",
        };

        if (port is > 0 && !IsDefaultPort(scheme, port.Value))
            builder.Port = port.Value;

        return builder.Uri;
    }

    private static bool IsDefaultPort(string scheme, int port)
    {
        return (scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) && port == 443) ||
               (scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) && port == 80);
    }

    private static async Task SendEventAsync(ClientWebSocket socket, string eventName, object data, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["event"] = eventName,
            ["data"] = data,
        }, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(payload);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<string?> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static PusherEnvelope? DeserializeEnvelope(string message)
    {
        try
        {
            return JsonSerializer.Deserialize<PusherEnvelope>(message, JsonOptions);
        }
        catch (JsonException ex)
        {
            Plugin.Log.Warning(ex, "Could not parse FullParty live room message: {Message}", message);
            return null;
        }
    }

    private static JsonDocument? ParseDataDocument(JsonElement data)
    {
        try
        {
            if (data.ValueKind == JsonValueKind.String)
            {
                var text = data.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
            }

            if (data.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                return JsonDocument.Parse(data.GetRawText());
        }
        catch (JsonException ex)
        {
            Plugin.Log.Warning(ex, "Could not parse FullParty live room event data.");
        }

        return null;
    }

    private static string GetPusherError(JsonElement data)
    {
        using var document = ParseDataDocument(data);
        if (document != null)
        {
            var root = document.RootElement;
            var message = GetString(root, "message");
            if (!string.IsNullOrWhiteSpace(message))
                return message;

            var code = GetString(root, "code");
            if (!string.IsNullOrWhiteSpace(code))
                return $"Pusher error {code}.";
        }

        return "The FullParty live room returned an error.";
    }

    private static IReadOnlyList<FullPartyLiveMember> ParseSubscriptionMembers(JsonElement data)
    {
        if (!TryGetObject(data, "presence", out var presence) || !TryGetObject(presence, "hash", out var hash))
            return [];

        var parsed = new List<FullPartyLiveMember>();
        foreach (var property in hash.EnumerateObject())
        {
            var userId = property.Name;
            var userInfo = property.Value;
            if (TryGetObject(userInfo, "user_info", out var nestedUserInfo))
                userInfo = nestedUserInfo;

            userId = GetString(property.Value, "user_id") ?? userId;
            parsed.Add(ParseLiveMember(userId, userInfo));
        }

        return parsed;
    }

    private static FullPartyLiveMember? ParseMemberAdded(JsonElement data)
    {
        var userId = GetString(data, "user_id");
        if (string.IsNullOrWhiteSpace(userId))
            return null;

        var userInfo = TryGetObject(data, "user_info", out var nestedUserInfo) ? nestedUserInfo : data;
        return ParseLiveMember(userId, userInfo);
    }

    private static FullPartyLiveMember ParseLiveMember(string userId, JsonElement userInfo)
    {
        var user = TryGetObject(userInfo, "user", out var userObject) ? userObject : userInfo;
        var character = TryGetObject(userInfo, "assigned_character", out var assignedCharacter)
            ? assignedCharacter
            : TryGetObject(userInfo, "character", out var characterObject)
                ? characterObject
                : TryGetFirstSlotCharacter(userInfo, out var slotCharacter)
                    ? slotCharacter
                    : default;

        var hasCharacter = character.ValueKind == JsonValueKind.Object;
        var slotLabels = GetAssignedSlotLabels(userInfo);
        var slotIds = GetAssignedSlotIds(userInfo);

        return new FullPartyLiveMember(
            userId,
            GetString(user, "name") ?? GetString(userInfo, "name") ?? $"User {userId}",
            hasCharacter ? GetString(character, "name") : null,
            hasCharacter ? GetString(character, "world") : null,
            hasCharacter ? GetString(character, "datacenter") : null,
            hasCharacter ? GetString(character, "avatar_url") : GetString(user, "avatar_url"),
            slotLabels,
            slotIds,
            HasAssignedSlotFlag(userInfo, "is_host") || GetBool(userInfo, "is_host") == true,
            HasAssignedSlotFlag(userInfo, "is_raid_leader") ||
            HasAssignedSlotFlag(userInfo, "is_party_lead") ||
            GetBool(userInfo, "is_raid_leader") == true ||
            GetBool(userInfo, "is_party_lead") == true);
    }

    private static IReadOnlyList<string> GetAssignedSlotLabels(JsonElement userInfo)
    {
        if (!TryGetArray(userInfo, "assigned_slots", out var assignedSlots))
            return [];

        return assignedSlots.EnumerateArray()
            .Select(slot => GetLocalizedString(slot, "slot_label") ??
                            GetLocalizedString(slot, "label") ??
                            BuildSlotLabel(slot))
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<int> GetAssignedSlotIds(JsonElement userInfo)
    {
        if (!TryGetArray(userInfo, "assigned_slots", out var assignedSlots))
            return [];

        return assignedSlots.EnumerateArray()
            .Select(slot => GetInt(slot, "id"))
            .Where(id => id != null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
    }

    private static string? BuildSlotLabel(JsonElement slot)
    {
        var groupLabel = GetLocalizedString(slot, "group_label") ?? GetString(slot, "group_key");
        var position = GetInt(slot, "position_in_group");
        if (!string.IsNullOrWhiteSpace(groupLabel) && position != null)
            return $"{groupLabel} {position}";

        return groupLabel;
    }

    private static bool TryGetFirstSlotCharacter(JsonElement userInfo, out JsonElement character)
    {
        if (TryGetArray(userInfo, "assigned_slots", out var assignedSlots))
        {
            foreach (var slot in assignedSlots.EnumerateArray())
            {
                if (TryGetObject(slot, "assigned_character", out character))
                    return true;
            }
        }

        character = default;
        return false;
    }

    private static bool HasAssignedSlotFlag(JsonElement userInfo, string propertyName)
    {
        if (!TryGetArray(userInfo, "assigned_slots", out var assignedSlots))
            return false;

        return assignedSlots.EnumerateArray().Any(slot => GetBool(slot, propertyName) == true);
    }

    private static FullPartyRunCommand? ParseRunCommand(JsonElement root)
    {
        var commandRoot = NormalizeCommandRoot(root);
        var id = GetString(commandRoot, "id") ?? GetString(commandRoot, "command_id");
        var command = GetString(commandRoot, "command") ?? GetString(commandRoot, "type") ?? GetString(commandRoot, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(command))
            return null;

        return new FullPartyRunCommand(
            id,
            command,
            GetString(commandRoot, "idempotency_key"),
            GetTargetType(commandRoot),
            GetDateTimeOffset(commandRoot, "expires_at"),
            GetPayloadInt(commandRoot, "seconds"),
            GetStringList(commandRoot, ["resolved_user_ids", "user_ids"]),
            GetIntList(commandRoot, ["resolved_slot_ids", "slot_ids"]));
    }

    private static JsonElement NormalizeCommandRoot(JsonElement root)
    {
        if (TryGetObject(root, "data", out var data))
            root = data;

        if (TryGetObject(root, "command", out var command))
            return command;

        return root;
    }

    private static string? GetTargetType(JsonElement root)
    {
        if (TryGetObject(root, "target", out var target))
            return GetString(target, "type");

        return GetString(root, "target_type");
    }

    private static IReadOnlyList<string> GetStringList(JsonElement root, IReadOnlyList<string> propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(root, propertyName, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Array)
                return value.EnumerateArray()
                    .Select(GetJsonValueAsString)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }

        if (TryGetObject(root, "resolved", out var resolved))
            return GetStringList(resolved, propertyNames);

        if (TryGetObject(root, "targets", out var targets))
            return GetStringList(targets, propertyNames);

        return [];
    }

    private static IReadOnlyList<int> GetIntList(JsonElement root, IReadOnlyList<string> propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(root, propertyName, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Array)
                return value.EnumerateArray()
                    .Select(GetJsonValueAsInt)
                    .Where(item => item != null)
                    .Select(item => item!.Value)
                    .Distinct()
                    .ToList();
        }

        if (TryGetObject(root, "resolved", out var resolved))
            return GetIntList(resolved, propertyNames);

        if (TryGetObject(root, "targets", out var targets))
            return GetIntList(targets, propertyNames);

        return [];
    }

    private static string? GetJsonValueAsString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    private static int? GetJsonValueAsInt(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
            return number;

        return null;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement root, string propertyName)
    {
        var value = GetString(root, propertyName);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static int? GetPayloadInt(JsonElement root, string propertyName)
    {
        if (TryGetObject(root, "payload", out var payload))
            return GetInt(payload, propertyName);

        return GetInt(root, propertyName);
    }

    private static string? GetLocalCommand(FullPartyRunCommand command)
    {
        if (command.Command.Equals("ready_check", StringComparison.OrdinalIgnoreCase))
            return "/readycheck";

        if (command.Command.Equals("countdown", StringComparison.OrdinalIgnoreCase))
            return $"/countdown {Math.Clamp(command.CountdownSeconds ?? 20, 1, 99)}";

        return null;
    }

    private static string FormatCommandName(string command)
    {
        return command switch
        {
            "ready_check" => "ready check",
            "countdown" => "countdown",
            _ => command,
        };
    }

    private static bool EventMatches(string actual, string expected)
    {
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
               actual.TrimStart('.').Equals(expected.TrimStart('.'), StringComparison.OrdinalIgnoreCase);
    }

    private string CreateIdempotencyKey(string command)
    {
        var commandPrefix = command.Replace('_', '-');
        var suffix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x", CultureInfo.InvariantCulture);
        return $"{commandPrefix}-{runId}-{suffix}";
    }

    private void ReplaceMembers(IEnumerable<FullPartyLiveMember> nextMembers)
    {
        lock (stateLock)
        {
            members.Clear();
            foreach (var member in nextMembers)
                members[member.UserId] = member;
        }
    }

    private void AddMember(FullPartyLiveMember member)
    {
        lock (stateLock)
        {
            members[member.UserId] = member;
        }
    }

    private void RemoveMember(string userId)
    {
        lock (stateLock)
        {
            members.Remove(userId);
        }
    }

    private void SetState(RealtimeRunRoomState state, string statusMessage)
    {
        lock (stateLock)
        {
            SetStateNoLock(state, statusMessage);
        }
    }

    private void SetStateNoLock(RealtimeRunRoomState state, string statusMessage)
    {
        State = state;
        StatusMessage = statusMessage;
    }

    private void SetCommandStatus(string statusMessage)
    {
        lock (stateLock)
        {
            CommandStatusMessage = statusMessage;
        }
    }

    private static bool TryGetObject(JsonElement root, string propertyName, out JsonElement value)
    {
        if (TryGetProperty(root, propertyName, out value) && value.ValueKind == JsonValueKind.Object)
            return true;

        value = default;
        return false;
    }

    private static bool TryGetArray(JsonElement root, string propertyName, out JsonElement value)
    {
        if (TryGetProperty(root, propertyName, out value) && value.ValueKind == JsonValueKind.Array)
            return true;

        value = default;
        return false;
    }

    private static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    private static int? GetInt(JsonElement root, string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
            return number;

        return null;
    }

    private static bool? GetBool(JsonElement root, string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static string? GetLocalizedString(JsonElement root, string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();

        if (value.ValueKind != JsonValueKind.Object)
            return null;

        return GetString(value, "en") ?? GetString(value, "de") ?? GetString(value, "fr") ?? GetString(value, "ja");
    }

    private sealed class PusherEnvelope
    {
        [JsonPropertyName("event")]
        public string Event { get; set; } = string.Empty;

        [JsonPropertyName("channel")]
        public string? Channel { get; set; }

        [JsonPropertyName("data")]
        public JsonElement Data { get; set; }
    }

    private sealed record PendingCommandExecution(
        string CommandId,
        string CommandName,
        string LocalCommand);
}
