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
    private readonly Dictionary<string, FullPartyPartySnapshot> partySnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> handledCommandIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<PendingCommandExecution> pendingCommandExecutions = new();
    private LatestCommandTracker? latestCommand;
    private const int CommandExpirySeconds = 30;
    private static readonly TimeSpan PartySnapshotInterval = TimeSpan.FromMilliseconds(2200);
    private static readonly TimeSpan ReadyCheckTrackingDuration = TimeSpan.FromSeconds(45);
    private DateTimeOffset readyCheckTrackUntil = DateTimeOffset.MinValue;
    private ReadyCheckSummary? readyCheckSummary;

    private CancellationTokenSource? cancellation;
    private Task? connectionTask;
    private Task? commandIssueTask;
    private Task? partySnapshotTask;
    private ClientWebSocket? webSocket;
    private FullPartyRunDetail? runDetail;
    private DateTimeOffset lastPartySnapshotAttemptAt = DateTimeOffset.MinValue;
    private int partySnapshotSequence;

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
    public string? PartySnapshotStatusMessage { get; private set; }
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

    public IReadOnlyList<FullPartyPartySnapshot> PartySnapshots
    {
        get
        {
            lock (stateLock)
            {
                return partySnapshots.Values
                    .OrderBy(snapshot => snapshot.PartyKey, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
    }

    public string GetCommandStatus(FullPartyLiveMember member)
    {
        lock (stateLock)
        {
            if (latestCommand == null)
                return "-";

            if (IsCurrentUser(member.UserId) &&
                latestCommand.CommandName.Equals("ready_check", StringComparison.OrdinalIgnoreCase) &&
                readyCheckSummary != null)
            {
                return readyCheckSummary.DisplayText;
            }

            if (latestCommand.StatusByUserId.TryGetValue(member.UserId, out var status))
                return FormatCommandStatus(status);

            return latestCommand.TargetUserIds.Contains(member.UserId)
                ? "Waiting"
                : "Not targeted";
        }
    }

    public void SetRunDetail(FullPartyRunDetail detail)
    {
        lock (stateLock)
        {
            runDetail = detail;
        }
    }

    public void ClearPartySnapshots(string? statusMessage = null)
    {
        lock (stateLock)
        {
            partySnapshots.Clear();
            if (!string.IsNullOrWhiteSpace(statusMessage))
                PartySnapshotStatusMessage = statusMessage;
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
            latestCommand = null;
            readyCheckSummary = null;
            readyCheckTrackUntil = DateTimeOffset.MinValue;
            PartySnapshotStatusMessage = null;
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
            latestCommand = null;
            readyCheckSummary = null;
            readyCheckTrackUntil = DateTimeOffset.MinValue;
            ChannelName = null;
            PartySnapshotStatusMessage = null;
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

            if (EventMatches(envelope.Event, config.CommandAcknowledgedEventName))
            {
                using var data = ParseDataDocument(envelope.Data);
                if (data != null)
                    HandleCommandAck(data.RootElement);
            }

            if (EventMatches(envelope.Event, config.PartySnapshotEventName))
            {
                using var data = ParseDataDocument(envelope.Data);
                if (data != null)
                    HandlePartySnapshot(data.RootElement);
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

        TrackCommand(command);

        lock (stateLock)
        {
            if (!handledCommandIds.Add(command.Id))
                return;
        }

        if (command.ExpiresAt != null && DateTimeOffset.UtcNow > command.ExpiresAt.Value)
        {
            UpdateCurrentUserCommandStatus(command.Id, "expired");
            QueueAck(command.Id, "expired");
            return;
        }

        if (!IsTargeted(command))
        {
            UpdateCurrentUserCommandStatus(command.Id, "ignored_not_targeted");
            QueueAck(command.Id, "ignored_not_targeted");
            return;
        }

        var localCommand = GetLocalCommand(command);
        if (localCommand == null)
        {
            UpdateCurrentUserCommandStatus(command.Id, "failed");
            QueueAck(command.Id, "failed");
            SetCommandStatus($"Unsupported live command: {command.Command}");
            return;
        }

        UpdateCurrentUserCommandStatus(command.Id, "received");
        QueueAck(command.Id, "received");
        pendingCommandExecutions.Enqueue(new PendingCommandExecution(command.Id, command.Command, localCommand));
        SetCommandStatus($"Received {FormatCommandName(command.Command)}.");
    }

    private void HandleCommandAck(JsonElement root)
    {
        var ack = ParseCommandAck(root);
        if (ack == null)
            return;

        lock (stateLock)
        {
            if (latestCommand == null || !latestCommand.CommandId.Equals(ack.CommandId, StringComparison.OrdinalIgnoreCase))
                return;

            if (string.IsNullOrWhiteSpace(ack.UserId))
                return;

            latestCommand.StatusByUserId[ack.UserId] = ack.Status;
        }
    }

    private void HandlePartySnapshot(JsonElement root)
    {
        if (!OccultCrescentTerritory.IsCurrent())
            return;

        var snapshot = ParsePartySnapshot(root);
        if (snapshot == null || snapshot.RunId != runId || string.IsNullOrWhiteSpace(snapshot.PartyKey))
            return;

        lock (stateLock)
        {
            if (partySnapshots.TryGetValue(snapshot.PartyKey, out var existing) &&
                existing.SenderUserId == snapshot.SenderUserId &&
                existing.Sequence > snapshot.Sequence)
            {
                return;
            }

            partySnapshots[snapshot.PartyKey] = snapshot;
            PartySnapshotStatusMessage = $"Party sync: {partySnapshots.Count} parties";
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        while (pendingCommandExecutions.TryDequeue(out var pending))
        {
            try
            {
                GameCommandExecutor.Execute(pending.LocalCommand);
                if (pending.CommandName.Equals("ready_check", StringComparison.OrdinalIgnoreCase))
                    StartReadyCheckTracking();

                UpdateCurrentUserCommandStatus(pending.CommandId, "executed");
                QueueAck(pending.CommandId, "executed");
                SetCommandStatus($"Executed {FormatCommandName(pending.CommandName)}.");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Could not execute FullParty live command {Command} for run {RunId}.", pending.CommandName, runId);
                UpdateCurrentUserCommandStatus(pending.CommandId, "failed");
                QueueAck(pending.CommandId, "failed");
                SetCommandStatus($"Failed to execute {FormatCommandName(pending.CommandName)}.");
            }
        }

        UpdateReadyCheckTracking();
        PublishPartySnapshotIfReady();
    }

    private void PublishPartySnapshotIfReady()
    {
        FullPartyRunDetail? currentRunDetail;
        FullPartyLiveMember? currentMember;
        CancellationToken token;

        if (!OccultCrescentTerritory.IsCurrent())
        {
            SetPartySnapshotStatus("Party sync waits for Occult Crescent.");
            return;
        }

        lock (stateLock)
        {
            var now = DateTimeOffset.UtcNow;

            if (State != RealtimeRunRoomState.Connected || cancellation == null)
                return;

            if (partySnapshotTask is { IsCompleted: false })
                return;

            if (now - lastPartySnapshotAttemptAt < PartySnapshotInterval)
                return;

            lastPartySnapshotAttemptAt = now;
            currentRunDetail = runDetail;
            currentMember = GetCurrentMemberNoLock();
            token = cancellation.Token;
        }

        if (currentRunDetail == null || currentMember == null)
            return;

        if (!currentRunDetail.CanModerate && !currentMember.IsHost && !currentMember.IsPartyLead)
        {
            SetPartySnapshotStatus("Party sync requires host, party lead, or moderator.");
            return;
        }

        var sequence = Interlocked.Increment(ref partySnapshotSequence);
        FullPartyPartySnapshot? snapshot;
        try
        {
            snapshot = PartySnapshotBuilder.TryBuild(runId, currentRunDetail, currentMember, plugin.AuthService.User, sequence);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not build FullParty party snapshot for run {RunId}.", runId);
            SetPartySnapshotStatus($"Party sync unavailable: {ex.Message}");
            return;
        }

        if (snapshot == null)
        {
            SetPartySnapshotStatus("Party sync waiting for assigned party.");
            return;
        }

        lock (stateLock)
        {
            partySnapshots[snapshot.PartyKey] = snapshot;
        }

        partySnapshotTask = Task.Run(() => SendPartySnapshotAsync(snapshot, token), token);
    }

    private async Task SendPartySnapshotAsync(FullPartyPartySnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            await apiClient.SendPartySnapshotAsync(runId, snapshot, cancellationToken);
            SetPartySnapshotStatus($"Synced {snapshot.PartyKey}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not send FullParty party snapshot for run {RunId}.", runId);
            SetPartySnapshotStatus($"Party sync failed: {ex.Message}");
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

        return IsMemberTargeted(command, currentUserId, currentMember);
    }

    private static bool IsMemberTargeted(FullPartyRunCommand command, string userId, FullPartyLiveMember? member)
    {
        if (command.ResolvedUserIds.Count > 0)
            return command.ResolvedUserIds.Contains(userId, StringComparer.OrdinalIgnoreCase);

        if (command.ResolvedSlotIds.Count > 0)
            return member?.SlotIds.Any(command.ResolvedSlotIds.Contains) == true;

        return command.TargetType switch
        {
            "party_leads" => member?.IsPartyLead == true,
            "hosts" => member?.IsHost == true,
            "all_assigned" => member?.SlotIds.Count > 0,
            _ => false,
        };
    }

    private void TrackCommand(FullPartyRunCommand command)
    {
        lock (stateLock)
        {
            if (latestCommand != null && latestCommand.CommandId.Equals(command.Id, StringComparison.OrdinalIgnoreCase))
                return;

            var targetUserIds = BuildTargetUserIdsNoLock(command);
            latestCommand = new LatestCommandTracker(
                command.Id,
                command.Command,
                targetUserIds,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private HashSet<string> BuildTargetUserIdsNoLock(FullPartyRunCommand command)
    {
        if (command.ResolvedUserIds.Count > 0)
            return command.ResolvedUserIds
                .Where(userId => !string.IsNullOrWhiteSpace(userId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var targetUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members.Values)
        {
            if (IsMemberTargeted(command, member.UserId, member))
                targetUserIds.Add(member.UserId);
        }

        return targetUserIds;
    }

    private void UpdateCurrentUserCommandStatus(string commandId, string status)
    {
        var currentUserId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(currentUserId))
            return;

        lock (stateLock)
        {
            if (latestCommand == null || !latestCommand.CommandId.Equals(commandId, StringComparison.OrdinalIgnoreCase))
                return;

            latestCommand.StatusByUserId[currentUserId] = status;
        }
    }

    private void StartReadyCheckTracking()
    {
        lock (stateLock)
        {
            readyCheckSummary = null;
            readyCheckTrackUntil = DateTimeOffset.UtcNow + ReadyCheckTrackingDuration;
        }
    }

    private void UpdateReadyCheckTracking()
    {
        ReadyCheckSummary? summary;
        lock (stateLock)
        {
            if (DateTimeOffset.UtcNow > readyCheckTrackUntil)
                return;
        }

        try
        {
            summary = ReadyCheckStatusReader.Read();
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Could not read the current ready-check status.");
            return;
        }

        if (summary == null)
            return;

        lock (stateLock)
        {
            if (DateTimeOffset.UtcNow > readyCheckTrackUntil)
                return;

            readyCheckSummary = summary;
            CommandStatusMessage = summary.DisplayText;
            if (summary.IsComplete)
                readyCheckTrackUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        }
    }

    private bool IsCurrentUser(string userId)
    {
        return userId.Equals(GetCurrentUserId(), StringComparison.OrdinalIgnoreCase);
    }

    private string? GetCurrentUserId()
    {
        return plugin.AuthService.User?.Id.ToString(CultureInfo.InvariantCulture);
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

    private static FullPartyRunCommandAck? ParseCommandAck(JsonElement root)
    {
        if (TryGetObject(root, "data", out var data))
            root = data;

        var ackRoot = TryGetObject(root, "ack", out var ack)
            ? ack
            : TryGetObject(root, "acknowledgement", out var acknowledgement)
                ? acknowledgement
                : root;
        var commandId = GetString(ackRoot, "command_id") ??
                        GetString(ackRoot, "commandId") ??
                        GetString(root, "command_id") ??
                        GetString(root, "commandId");
        var status = GetString(ackRoot, "status") ??
                     GetString(ackRoot, "ack_status") ??
                     GetString(ackRoot, "ackStatus") ??
                     GetString(root, "status");
        var userId = GetString(ackRoot, "user_id") ??
                     GetString(ackRoot, "userId") ??
                     GetString(ackRoot, "sender_user_id") ??
                     GetString(ackRoot, "senderUserId") ??
                     GetString(ackRoot, "acknowledged_by_user_id") ??
                     GetString(ackRoot, "acknowledgedByUserId") ??
                     GetString(root, "user_id") ??
                     GetString(root, "userId");

        if (TryGetObject(root, "command", out var commandObject))
            commandId ??= GetString(commandObject, "id") ?? GetString(commandObject, "command_id");

        if (TryGetObject(ackRoot, "user", out var userObject) || TryGetObject(root, "user", out userObject))
            userId ??= GetString(userObject, "id") ?? GetString(userObject, "user_id");

        if (TryGetObject(ackRoot, "member", out var memberObject) || TryGetObject(root, "member", out memberObject))
            userId ??= GetString(memberObject, "user_id") ?? GetString(memberObject, "id");

        if (string.IsNullOrWhiteSpace(commandId) || string.IsNullOrWhiteSpace(status))
            return null;

        return new FullPartyRunCommandAck(commandId, userId, status);
    }

    private static FullPartyPartySnapshot? ParsePartySnapshot(JsonElement root)
    {
        if (TryGetObject(root, "data", out var data))
            root = data;

        var parsedRunId = GetInt(root, "run_id") ?? GetInt(root, "runId");
        var partyKey = GetString(root, "party_key") ?? GetString(root, "partyKey");
        var sequence = GetInt(root, "seq") ?? GetInt(root, "sequence");
        var senderUserId = GetLong(root, "sender_user_id") ?? GetLong(root, "senderUserId") ?? 0;
        var timestamp = GetLong(root, "ts") ?? GetLong(root, "timestamp");

        if (parsedRunId == null || string.IsNullOrWhiteSpace(partyKey) || sequence == null)
            return null;

        if (!TryGetArray(root, "members", out var membersArray))
            return null;

        var members = membersArray.EnumerateArray()
            .Select(ParsePartySnapshotMember)
            .Where(member => member != null)
            .Select(member => member!)
            .ToList();

        return new FullPartyPartySnapshot(
            parsedRunId.Value,
            senderUserId,
            partyKey,
            sequence.Value,
            timestamp is > 0 ? DateTimeOffset.FromUnixTimeSeconds(timestamp.Value) : DateTimeOffset.UtcNow,
            members);
    }

    private static FullPartyPartySnapshotMember? ParsePartySnapshotMember(JsonElement member)
    {
        var position = GetInt(member, "p") ?? GetInt(member, "position");
        if (position == null)
            return null;

        return new FullPartyPartySnapshotMember(
            position.Value,
            GetLong(member, "cid") ?? GetLong(member, "character_id") ?? GetLong(member, "characterId"),
            GetString(member, "n") ?? GetString(member, "name"),
            GetString(member, "w") ?? GetString(member, "world"),
            GetClassJob(member),
            GetPhantomJob(member));
    }

    private static string? GetClassJob(JsonElement root)
    {
        foreach (var propertyName in new[] { "cj", "class_job", "classJob", "class_job_shorthand", "classJobShorthand" })
        {
            if (!TryGetProperty(root, propertyName, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var rowId))
                return PartySnapshotBuilder.GetCombatClassJobShorthand(rowId);

            if (value.ValueKind != JsonValueKind.String)
                continue;

            var text = value.GetString();
            if (uint.TryParse(text, out rowId))
                return PartySnapshotBuilder.GetCombatClassJobShorthand(rowId);

            return string.IsNullOrWhiteSpace(text)
                ? null
                : text.Trim().ToUpperInvariant();
        }

        return null;
    }

    private static string? GetPhantomJob(JsonElement root)
    {
        foreach (var propertyName in new[] { "pj", "phantom_job", "phantomJob", "phantom_job_name", "phantomJobName" })
        {
            if (!TryGetProperty(root, propertyName, out var value))
                continue;

            return value.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim(),
                JsonValueKind.Number => value.ToString(),
                _ => null,
            };
        }

        return null;
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

        if (TryGetObject(root, "target", out var target))
            return GetStringList(target, propertyNames);

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

        if (TryGetObject(root, "target", out var target))
            return GetIntList(target, propertyNames);

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

    private static string FormatCommandStatus(string status)
    {
        return status switch
        {
            "received" => "Received",
            "executed" => "Executed",
            "failed" => "Failed",
            "expired" => "Expired",
            "ignored_not_targeted" => "Received, not target",
            "user_disabled_auto_execute" => "Disabled",
            _ => status.Replace('_', ' '),
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

    private FullPartyLiveMember? GetCurrentMemberNoLock()
    {
        var currentUserId = plugin.AuthService.User?.Id.ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(currentUserId))
            return null;

        return members.TryGetValue(currentUserId, out var member) ? member : null;
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

    private void SetPartySnapshotStatus(string statusMessage)
    {
        lock (stateLock)
        {
            PartySnapshotStatusMessage = statusMessage;
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

    private static long? GetLong(JsonElement root, string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
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

    private sealed record FullPartyRunCommandAck(
        string CommandId,
        string? UserId,
        string Status);

    private sealed record LatestCommandTracker(
        string CommandId,
        string CommandName,
        HashSet<string> TargetUserIds,
        Dictionary<string, string> StatusByUserId);
}
