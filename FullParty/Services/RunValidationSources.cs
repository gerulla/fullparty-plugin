using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Party;
using FullParty.Models;

namespace FullParty.Services;

internal sealed record GamePresenceMember(
    string Name,
    string? World,
    int? ClassJobId,
    int? PhantomJobId);

internal sealed class GamePresenceList
{
    public static readonly GamePresenceList Empty = new([]);

    private readonly IReadOnlyList<GamePresenceMember> members;
    private readonly Dictionary<string, GamePresenceMember> byCharacterKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GamePresenceMember> byName = new(StringComparer.OrdinalIgnoreCase);

    public GamePresenceList(IReadOnlyList<GamePresenceMember> members)
    {
        this.members = members;
        foreach (var member in members)
        {
            var characterKey = RunValidationSources.GetCharacterKey(member.Name, member.World);
            if (!characterKey.Equals("@", StringComparison.Ordinal))
                byCharacterKey[characterKey] = member;

            var nameKey = RunValidationSources.NormalizeKeyPart(member.Name);
            if (!string.IsNullOrWhiteSpace(nameKey))
                byName[nameKey] = member;
        }
    }

    public int Count => members.Count;

    public bool TryFind(FullPartyRosterCharacter character, out GamePresenceMember member)
    {
        if (byCharacterKey.TryGetValue(RunValidationSources.GetCharacterKey(character.Name, character.World), out member!))
            return true;

        return byName.TryGetValue(RunValidationSources.NormalizeKeyPart(character.Name), out member!);
    }
}

internal static class RunValidationSources
{
    private const string CurrentPartySnapshotKey = "current-party";

    public static GamePresenceList BuildLocalPartyPresence(FullPartyRunDetail runDetail)
    {
        var members = new Dictionary<string, GamePresenceMember>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var member in Plugin.PartyList)
            {
                try
                {
                    AddPresence(
                        members,
                        GetName(member),
                        GetWorld(member),
                        PartySnapshotBuilder.GetCombatClassJobId(member.ClassJob.RowId),
                        PartySnapshotBuilder.GetPhantomJobIdFromStatuses(member.Statuses, runDetail));
                }
                catch (Exception ex)
                {
                    Plugin.Log.Debug("Could not read a party member for FullParty validation presence: {Message}", ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug("Could not enumerate the party list for FullParty validation presence: {Message}", ex.Message);
        }

        return new GamePresenceList([.. members.Values]);
    }

    public static FullPartyPartySnapshot? BuildCurrentPartySnapshot(FullPartyRunDetail runDetail)
    {
        var rosterByName = BuildRosterByName(runDetail);
        var members = new List<FullPartyPartySnapshotMember>();
        var position = 1;

        try
        {
            foreach (var member in Plugin.PartyList)
            {
                if (members.Count >= 8)
                    break;

                try
                {
                    if (string.IsNullOrWhiteSpace(GetName(member)))
                        continue;

                    members.Add(MapPartyMember(member, position, runDetail, rosterByName));
                    position++;
                }
                catch (Exception ex)
                {
                    Plugin.Log.Debug("Could not read a party member for the local FullParty party snapshot: {Message}", ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug("Could not enumerate the party list for the local FullParty party snapshot: {Message}", ex.Message);
        }

        return members.Count == 0
            ? null
            : new FullPartyPartySnapshot(
                runDetail.Id,
                0,
                CurrentPartySnapshotKey,
                0,
                DateTimeOffset.UtcNow,
                members);
    }

    public static IReadOnlyList<FullPartyPartySnapshot> BuildLocalPartySnapshots(
        FullPartyRunDetail runDetail,
        IReadOnlyList<IReadOnlyList<FullPartyRosterSlot>> parties)
    {
        var slotsByListPosition = parties
            .SelectMany(party => party.Select((slot, index) => (Slot: slot, Position: index + 1)))
            .ToList();
        if (slotsByListPosition.Count == 0)
            return [];

        var rosterByName = BuildRosterByName(runDetail);
        var groupedMembers = new Dictionary<string, List<FullPartyPartySnapshotMember>>(StringComparer.OrdinalIgnoreCase);
        var orderedPartyMembers = Plugin.PartyList
            .Where(member => !string.IsNullOrWhiteSpace(GetName(member)))
            .ToList();

        for (var i = 0; i < orderedPartyMembers.Count && i < slotsByListPosition.Count; i++)
        {
            var plannedSlot = slotsByListPosition[i].Slot;
            if (!groupedMembers.TryGetValue(plannedSlot.GroupKey, out var members))
            {
                members = [];
                groupedMembers[plannedSlot.GroupKey] = members;
            }

            members.Add(MapPartyMember(
                orderedPartyMembers[i],
                plannedSlot.PositionInGroup ?? slotsByListPosition[i].Position,
                runDetail,
                rosterByName));
        }

        return parties
            .Select(party => party[0].GroupKey)
            .Where(groupedMembers.ContainsKey)
            .Select(partyKey => new FullPartyPartySnapshot(
                runDetail.Id,
                0,
                partyKey,
                0,
                DateTimeOffset.UtcNow,
                groupedMembers[partyKey]))
            .ToList();
    }

    internal static string GetCharacterKey(string? name, string? world)
    {
        return $"{NormalizeKeyPart(name)}@{NormalizeKeyPart(world)}";
    }

    internal static string NormalizeKeyPart(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    }

    private static IReadOnlyDictionary<string, FullPartyRosterSlot> BuildRosterByName(FullPartyRunDetail runDetail)
    {
        return runDetail.Slots
            .Where(slot => slot.AssignedCharacter != null)
            .GroupBy(slot => GetCharacterKey(slot.AssignedCharacter!.Name, slot.AssignedCharacter.World), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    private static FullPartyPartySnapshotMember MapPartyMember(
        IPartyMember member,
        int position,
        FullPartyRunDetail runDetail,
        IReadOnlyDictionary<string, FullPartyRosterSlot> rosterByName)
    {
        var name = GetName(member);
        var world = GetWorld(member);
        var rosterSlot = rosterByName.TryGetValue(GetCharacterKey(name, world), out var matchedSlot)
            ? matchedSlot
            : null;
        var characterId = rosterSlot?.AssignedCharacter?.Id;

        return new FullPartyPartySnapshotMember(
            position,
            characterId,
            characterId == null ? name : null,
            characterId == null ? world : null,
            PartySnapshotBuilder.GetCombatClassJobId(member.ClassJob.RowId),
            PartySnapshotBuilder.GetPhantomJobIdFromStatuses(member.Statuses, runDetail));
    }

    private static void AddPresence(
        IDictionary<string, GamePresenceMember> members,
        string? name,
        string? world,
        int? classJobId,
        int? phantomJobId)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        members[GetCharacterKey(name, world)] = new GamePresenceMember(name, world, classJobId, phantomJobId);
    }

    private static string GetName(IPartyMember member)
    {
        return member.Name.ToString();
    }

    private static string GetWorld(IPartyMember member)
    {
        return member.World.Value.Name.ToString();
    }
}
