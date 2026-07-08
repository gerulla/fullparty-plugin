using System;
using System.Collections.Generic;
using System.Linq;
using FullParty.Models;

namespace FullParty.Services;

public sealed record LiveRoomOverlayStatus(
    string Text,
    RealtimeRunRoomState State,
    string? Detail,
    LiveRoomFeedback? Feedback);

public sealed class LiveRoomManager : IDisposable
{
    private static readonly TimeSpan DisconnectedOverlayDuration = TimeSpan.FromSeconds(10);

    private readonly Plugin plugin;
    private readonly object stateLock = new();
    private readonly Dictionary<int, LiveRoomEntry> rooms = new();
    private int? focusedRunId;

    public LiveRoomManager(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public RealtimeRunRoomClient GetOrCreate(FullPartyRun run)
    {
        lock (stateLock)
        {
            focusedRunId = run.Id;

            if (rooms.TryGetValue(run.Id, out var existing))
            {
                existing.Run = run;
                return existing.Client;
            }

            var entry = new LiveRoomEntry(run, new RealtimeRunRoomClient(run.Id, plugin));
            rooms[run.Id] = entry;
            return entry.Client;
        }
    }

    public LiveRoomOverlayStatus? GetOverlayStatus()
    {
        LiveRoomEntry? entry;
        lock (stateLock)
        {
            entry = GetOverlayEntryNoLock();
        }

        if (entry == null)
            return null;

        var client = entry.Client;
        if (!ShouldShowOverlay(client))
            return null;

        var text = BuildStatusText(client);
        var detail = $"{entry.Run.Title} - {client.StatusMessage}";
        if (!string.IsNullOrWhiteSpace(client.PartySnapshotStatusMessage))
            detail = $"{detail}\n{client.PartySnapshotStatusMessage}";

        return new LiveRoomOverlayStatus(text, client.State, detail, client.OverlayFeedback);
    }

    public bool OpenOverlayRunWindow()
    {
        LiveRoomEntry? entry;
        lock (stateLock)
        {
            entry = GetOverlayEntryNoLock();
            if (entry != null)
                focusedRunId = entry.Run.Id;
        }

        if (entry == null)
            return false;

        plugin.OpenRunWindow(entry.Run);
        return true;
    }

    public void Dispose()
    {
        List<RealtimeRunRoomClient> clients;
        lock (stateLock)
        {
            clients = rooms.Values.Select(entry => entry.Client).ToList();
            rooms.Clear();
            focusedRunId = null;
        }

        foreach (var client in clients)
        {
            client.Dispose();
        }
    }

    private LiveRoomEntry? GetFocusedEntryNoLock()
    {
        return focusedRunId is { } runId && rooms.TryGetValue(runId, out var entry)
            ? entry
            : null;
    }

    private LiveRoomEntry? GetOverlayEntryNoLock()
    {
        return GetFocusedEntryNoLock() ?? rooms.Values.FirstOrDefault(room => room.Client.IsActive);
    }

    private static bool ShouldShowOverlay(RealtimeRunRoomClient client)
    {
        if (client.IsActive || client.OverlayFeedback != null)
            return true;

        if (!client.HasEverStarted)
            return false;

        if (client.State == RealtimeRunRoomState.Disconnected)
            return GetServerTimeNow() - client.StateUpdatedAt <= DisconnectedOverlayDuration;

        return true;
    }

    private static DateTimeOffset GetServerTimeNow()
    {
        var frameworkUtc = Plugin.Framework.LastUpdateUTC;
        return frameworkUtc == default ? DateTimeOffset.UtcNow : frameworkUtc;
    }

    private static string BuildStatusText(RealtimeRunRoomClient client)
    {
        if (client.State == RealtimeRunRoomState.Connected)
        {
            var partyKey = client.PartySyncDebug?.PartyKey;
            if (!string.IsNullOrWhiteSpace(partyKey))
                return $"FullParty: Syncing {FormatPartyKey(partyKey)}";

            return "FullParty: Live Room Connected";
        }

        return client.State switch
        {
            RealtimeRunRoomState.Connecting => "FullParty: Connecting...",
            RealtimeRunRoomState.Authorizing => "FullParty: Authorizing...",
            RealtimeRunRoomState.Subscribing => "FullParty: Joining live room...",
            RealtimeRunRoomState.Error => "FullParty: Live Room Error",
            _ => "FullParty: Disconnected",
        };
    }

    private static string FormatPartyKey(string partyKey)
    {
        var normalized = partyKey.Trim();
        var prefix = "party-";
        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = normalized[prefix.Length..];
            return string.IsNullOrWhiteSpace(suffix)
                ? "Party"
                : $"Party {suffix.ToUpperInvariant()}";
        }

        if (normalized.StartsWith("Party ", StringComparison.OrdinalIgnoreCase))
            return normalized;

        return normalized;
    }

    private sealed class LiveRoomEntry(FullPartyRun run, RealtimeRunRoomClient client)
    {
        public FullPartyRun Run { get; set; } = run;
        public RealtimeRunRoomClient Client { get; } = client;
    }
}
