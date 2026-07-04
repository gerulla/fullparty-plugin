using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly FullPartyApiClient apiClient;
    private readonly object stateLock = new();
    private readonly Dictionary<string, FullPartyLiveMember> members = new(StringComparer.Ordinal);

    private CancellationTokenSource? cancellation;
    private Task? connectionTask;
    private ClientWebSocket? webSocket;

    public RealtimeRunRoomClient(int runId, FullPartyApiClient apiClient)
    {
        this.runId = runId;
        this.apiClient = apiClient;
    }

    public RealtimeRunRoomState State { get; private set; } = RealtimeRunRoomState.Disconnected;
    public string StatusMessage { get; private set; } = "Disconnected";
    public string? ChannelName { get; private set; }

    public bool IsActive => State is RealtimeRunRoomState.Connecting
        or RealtimeRunRoomState.Authorizing
        or RealtimeRunRoomState.Subscribing
        or RealtimeRunRoomState.Connected;

    public bool IsBusy => State is RealtimeRunRoomState.Connecting
        or RealtimeRunRoomState.Authorizing
        or RealtimeRunRoomState.Subscribing;

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
            ChannelName = null;
            SetStateNoLock(RealtimeRunRoomState.Disconnected, "Disconnected");
        }

        currentCancellation?.Cancel();
        currentSocket?.Dispose();
    }

    public void Dispose()
    {
        Disconnect();
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

            await ReceiveLoopAsync(socket, channelName, cancellationToken);
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

    private async Task ReceiveLoopAsync(ClientWebSocket socket, string channelName, CancellationToken cancellationToken)
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
        }
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

        return new FullPartyLiveMember(
            userId,
            GetString(user, "name") ?? GetString(userInfo, "name") ?? $"User {userId}",
            hasCharacter ? GetString(character, "name") : null,
            hasCharacter ? GetString(character, "world") : null,
            hasCharacter ? GetString(character, "datacenter") : null,
            hasCharacter ? GetString(character, "avatar_url") : GetString(user, "avatar_url"),
            slotLabels,
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
}
