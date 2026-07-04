using System;
using System.Collections.Generic;
using System.Globalization;

namespace FullParty.Models;

public sealed record FullPartyRealtimeConfig(
    string AppKey,
    string Host,
    string? Scheme,
    int? WsPort,
    int? WssPort,
    bool ForceTls,
    string AuthEndpoint,
    string ChannelPattern,
    string? Path,
    string CommandEventName,
    string CommandAcknowledgedEventName)
{
    public string GetRunChannelName(int runId)
    {
        var runIdText = runId.ToString(CultureInfo.InvariantCulture);
        return ChannelPattern
            .Replace("{run_id}", runIdText, StringComparison.OrdinalIgnoreCase)
            .Replace("{run}", runIdText, StringComparison.OrdinalIgnoreCase)
            .Replace("{activity}", runIdText, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record FullPartyBroadcastAuth(
    string Auth,
    string? ChannelData);

public sealed record FullPartyLiveMember(
    string UserId,
    string UserName,
    string? CharacterName,
    string? World,
    string? Datacenter,
    string? AvatarUrl,
    IReadOnlyList<string> SlotLabels,
    IReadOnlyList<int> SlotIds,
    bool IsHost,
    bool IsPartyLead)
{
    public string DisplayName => string.IsNullOrWhiteSpace(CharacterName) ? UserName : CharacterName;

    public string Location
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(World) && !string.IsNullOrWhiteSpace(Datacenter))
                return $"{World} - {Datacenter}";

            return World ?? Datacenter ?? string.Empty;
        }
    }
}

public sealed record FullPartyRunCommand(
    string Id,
    string Command,
    string? IdempotencyKey,
    string? TargetType,
    DateTimeOffset? ExpiresAt,
    int? CountdownSeconds,
    IReadOnlyList<string> ResolvedUserIds,
    IReadOnlyList<int> ResolvedSlotIds);
