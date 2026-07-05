using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Party;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FullParty.Models;
using Lumina.Excel.Sheets;

namespace FullParty.Services;

internal sealed record GamePresenceMember(
    string Name,
    string? World,
    int? ClassJobId,
    int? PhantomJobId);

internal sealed record ObservedGameMember(
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
    private const int MaxPartyMembers = 8;
    private const string CurrentPartySnapshotKey = "current-party";

    public static GamePresenceList BuildLocalPartyPresence(FullPartyRunDetail runDetail)
    {
        var members = new Dictionary<string, GamePresenceMember>(StringComparer.OrdinalIgnoreCase);
        foreach (var bucket in BuildObservedPartyBuckets(GetActivePartyCount(runDetail)))
            foreach (var member in bucket.Members)
                AddPresence(members, member.Name, member.World, member.ClassJobId, member.PhantomJobId);

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
        if (parties.Count == 0)
            return [];

        var rosterByName = BuildRosterByName(runDetail);
        var observedParties = BuildObservedPartyBuckets(parties.Count);
        var observedByRosterParty = MapObservedPartiesToRosterParties(parties, observedParties);

        return parties
            .Where(party => observedByRosterParty.ContainsKey(party[0].GroupKey))
            .Select(party =>
            {
                var partyKey = party[0].GroupKey;
                var members = observedByRosterParty[partyKey].Members
                    .Select((member, index) => MapObservedMember(member, index + 1, rosterByName))
                    .ToList();

                return new FullPartyPartySnapshot(
                    runDetail.Id,
                    0,
                    partyKey,
                    0,
                    DateTimeOffset.UtcNow,
                    members);
            })
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

    private static IReadOnlyList<ObservedPartyBucket> BuildObservedPartyBuckets(int rosterPartyCount)
    {
        var crossRealmBuckets = ReadCrossRealmPartyBuckets(rosterPartyCount);
        if (crossRealmBuckets.Count > 0)
            return crossRealmBuckets;

        var buckets = new List<ObservedPartyBucket>();
        var localMembers = ReadLocalPartyMembers();
        if (localMembers.Count > 0)
            buckets.Add(new ObservedPartyBucket(0, localMembers));

        if (!Plugin.PartyList.IsAlliance)
            return buckets;

        var alliancePartyCount = Math.Max(0, rosterPartyCount - 1);
        for (var partyIndex = 0; partyIndex < alliancePartyCount; partyIndex++)
        {
            var allianceMembers = ReadAlliancePartyMembers(partyIndex);
            if (allianceMembers.Count > 0)
                buckets.Add(new ObservedPartyBucket(partyIndex + 1, allianceMembers));
        }

        return buckets;
    }

    private static IReadOnlyList<ObservedGameMember> ReadLocalPartyMembers()
    {
        try
        {
            return Plugin.PartyList
                .Where(member => !string.IsNullOrWhiteSpace(GetName(member)))
                .Take(MaxPartyMembers)
                .Select(member => new ObservedGameMember(
                    GetName(member),
                    GetWorld(member),
                    PartySnapshotBuilder.GetCombatClassJobId(member.ClassJob.RowId),
                    null))
                .ToList();
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug("Could not enumerate the party list for FullParty validation: {Message}", ex.Message);
            return [];
        }
    }

    private static unsafe IReadOnlyList<ObservedPartyBucket> ReadCrossRealmPartyBuckets(int rosterPartyCount)
    {
        try
        {
            var infoModule = InfoModule.Instance();
            if (infoModule == null)
                return [];

            var proxy = infoModule->GetInfoProxyById(InfoProxyId.CrossRealmParty);
            if (proxy == null)
                return [];

            var crossRealm = (InfoProxyCrossRealm*)proxy;
            if (!crossRealm->IsInCrossRealmParty && crossRealm->GroupCount == 0)
                return [];

            var buckets = new List<ObservedPartyBucket>();
            var groups = crossRealm->CrossRealmGroups;
            var groupLimit = Math.Min(
                groups.Length,
                Math.Max(Math.Max(rosterPartyCount, crossRealm->GroupCount), 1));

            for (var groupIndex = 0; groupIndex < groupLimit; groupIndex++)
            {
                var group = groups[groupIndex];
                var memberLimit = Math.Min((int)group.GroupMemberCount, MaxPartyMembers);
                var members = new List<ObservedGameMember>();

                for (var memberIndex = 0; memberIndex < memberLimit; memberIndex++)
                {
                    var member = group.GroupMembers[memberIndex];
                    var name = member.NameString;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    members.Add(new ObservedGameMember(
                        name,
                        GetWorldName(member.HomeWorld),
                        PartySnapshotBuilder.GetCombatClassJobId(member.ClassJobId),
                        null));
                }

                if (members.Count > 0)
                    buckets.Add(new ObservedPartyBucket(groupIndex, members));
            }

            return buckets;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug("Could not read cross-realm party data for FullParty validation: {Message}", ex.Message);
            return [];
        }
    }

    private static IReadOnlyList<ObservedGameMember> ReadAlliancePartyMembers(int partyIndex)
    {
        var members = new List<ObservedGameMember>();
        var startIndex = partyIndex * MaxPartyMembers;

        for (var slotIndex = 0; slotIndex < MaxPartyMembers; slotIndex++)
        {
            try
            {
                var address = Plugin.PartyList.GetAllianceMemberAddress(startIndex + slotIndex);
                if (address == IntPtr.Zero)
                    continue;

                var member = Plugin.PartyList.CreateAllianceMemberReference(address);
                if (member == null || string.IsNullOrWhiteSpace(GetName(member)))
                    continue;

                members.Add(new ObservedGameMember(
                    GetName(member),
                    GetWorld(member),
                    PartySnapshotBuilder.GetCombatClassJobId(member.ClassJob.RowId),
                    null));
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug("Could not read alliance member {Index} for FullParty validation: {Message}", startIndex + slotIndex, ex.Message);
            }
        }

        return members;
    }

    private static IReadOnlyDictionary<string, ObservedPartyBucket> MapObservedPartiesToRosterParties(
        IReadOnlyList<IReadOnlyList<FullPartyRosterSlot>> rosterParties,
        IReadOnlyList<ObservedPartyBucket> observedParties)
    {
        var mapped = new Dictionary<string, ObservedPartyBucket>(StringComparer.OrdinalIgnoreCase);
        var usedObservedIndexes = new HashSet<int>();

        foreach (var rosterParty in rosterParties)
        {
            var leadCharacters = rosterParty
                .Where(slot => (slot.IsHost || slot.IsRaidLeader) && slot.AssignedCharacter != null)
                .Select(slot => slot.AssignedCharacter!)
                .ToList();
            if (leadCharacters.Count == 0)
                continue;

            var bestMatch = observedParties
                .Where(observed => !usedObservedIndexes.Contains(observed.Index))
                .Select(observed => new
                {
                    Observed = observed,
                    Matches = leadCharacters.Count(lead => ContainsCharacter(observed, lead)),
                })
                .OrderByDescending(candidate => candidate.Matches)
                .FirstOrDefault(candidate => candidate.Matches > 0);

            if (bestMatch == null)
                continue;

            mapped[rosterParty[0].GroupKey] = bestMatch.Observed;
            usedObservedIndexes.Add(bestMatch.Observed.Index);
        }

        foreach (var (rosterParty, index) in rosterParties.Select((party, index) => (party, index)))
        {
            var partyKey = rosterParty[0].GroupKey;
            if (mapped.ContainsKey(partyKey))
                continue;

            var observed = observedParties.FirstOrDefault(candidate =>
                candidate.Index == index && !usedObservedIndexes.Contains(candidate.Index));
            observed ??= observedParties.FirstOrDefault(candidate => !usedObservedIndexes.Contains(candidate.Index));
            if (observed == null)
                continue;

            mapped[partyKey] = observed;
            usedObservedIndexes.Add(observed.Index);
        }

        return mapped;
    }

    private static bool ContainsCharacter(ObservedPartyBucket bucket, FullPartyRosterCharacter character)
    {
        return bucket.Members.Any(member =>
            GetCharacterKey(member.Name, member.World)
                .Equals(GetCharacterKey(character.Name, character.World), StringComparison.OrdinalIgnoreCase) ||
            NormalizeKeyPart(member.Name).Equals(NormalizeKeyPart(character.Name), StringComparison.OrdinalIgnoreCase));
    }

    private static int GetActivePartyCount(FullPartyRunDetail runDetail)
    {
        return runDetail.Slots
            .Where(slot => !IsBenchSlot(slot))
            .Select(slot => slot.GroupKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
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

    private static FullPartyPartySnapshotMember MapObservedMember(
        ObservedGameMember member,
        int position,
        IReadOnlyDictionary<string, FullPartyRosterSlot> rosterByName)
    {
        var rosterSlot = rosterByName.TryGetValue(GetCharacterKey(member.Name, member.World), out var matchedSlot)
            ? matchedSlot
            : null;
        var characterId = rosterSlot?.AssignedCharacter?.Id;

        return new FullPartyPartySnapshotMember(
            position,
            characterId,
            characterId == null ? member.Name : null,
            characterId == null ? member.World : null,
            member.ClassJobId,
            member.PhantomJobId);
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

    private static string? GetWorldName(short worldId)
    {
        if (worldId <= 0)
            return null;

        return Plugin.DataManager.GetExcelSheet<World>().TryGetRow((uint)worldId, out var world)
            ? world.Name.ToString()
            : null;
    }

    private static bool IsBenchSlot(FullPartyRosterSlot slot)
    {
        return slot.GroupKey.Contains("bench", StringComparison.OrdinalIgnoreCase) ||
               slot.GroupLabel.Contains("bench", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ObservedPartyBucket(int Index, IReadOnlyList<ObservedGameMember> Members);
}
