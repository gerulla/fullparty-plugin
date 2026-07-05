using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.ClientState.Statuses;
using FullParty.Auth;
using FullParty.Models;
using LuminaStatus = Lumina.Excel.Sheets.Status;

namespace FullParty.Services;

internal static class PartySnapshotBuilder
{
    private static readonly Dictionary<uint, string?> StatusNameCache = new();

    public static FullPartyPartySnapshot? TryBuild(
        int runId,
        FullPartyRunDetail runDetail,
        FullPartyLiveMember currentMember,
        FullPartyUser? currentUser,
        int sequence)
    {
        var assignedSlot = GetCurrentAssignedSlot(runDetail, currentMember, currentUser);
        var partyKey = assignedSlot?.GroupKey;
        if (string.IsNullOrWhiteSpace(partyKey))
            return null;

        var rosterByName = runDetail.Slots
            .Where(slot => slot.AssignedCharacter != null)
            .GroupBy(slot => GetCharacterKey(slot.AssignedCharacter!.Name, slot.AssignedCharacter.World), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var members = BuildPartyMembers(runDetail, rosterByName);

        if (assignedSlot != null && !HasAssignedSlotMember(members, assignedSlot))
        {
            var fallbackMember = TryMapLocalPlayerFallback(assignedSlot, currentMember, runDetail);
            if (fallbackMember != null)
                members.Add(fallbackMember);
        }

        return members.Count == 0
            ? null
            : new FullPartyPartySnapshot(
                runId,
                long.TryParse(currentMember.UserId, out var userId) ? userId : 0,
                partyKey,
                sequence,
                DateTimeOffset.UtcNow,
                members);
    }

    private static List<FullPartyPartySnapshotMember> BuildPartyMembers(
        FullPartyRunDetail runDetail,
        IReadOnlyDictionary<string, FullPartyRosterSlot> rosterByName)
    {
        var members = new List<FullPartyPartySnapshotMember>();
        var position = 1;

        try
        {
            foreach (var member in Plugin.PartyList)
            {
                if (members.Count >= 8)
                    break;

                var mappedMember = TryMapMember(member, position, runDetail, rosterByName);
                if (mappedMember == null)
                    continue;

                members.Add(mappedMember);
                position++;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug("Could not enumerate the party list for a FullParty snapshot: {Message}", ex.Message);
        }

        return members;
    }

    private static FullPartyPartySnapshotMember? TryMapMember(
        IPartyMember member,
        int position,
        FullPartyRunDetail runDetail,
        IReadOnlyDictionary<string, FullPartyRosterSlot> rosterByName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(GetName(member)))
                return null;

            return MapMember(member, position, runDetail, rosterByName);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug("Could not read a party member for a FullParty snapshot: {Message}", ex.Message);
            return null;
        }
    }

    private static FullPartyPartySnapshotMember MapMember(
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
            GetClassJobId(member),
            GetPhantomJobId(member, runDetail));
    }

    private static FullPartyPartySnapshotMember? TryMapLocalPlayerFallback(
        FullPartyRosterSlot assignedSlot,
        FullPartyLiveMember currentMember,
        FullPartyRunDetail runDetail)
    {
        try
        {
            return MapLocalPlayerFallback(assignedSlot, currentMember, runDetail);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug("Could not read the local player fallback for a FullParty snapshot: {Message}", ex.Message);
            return null;
        }
    }

    private static FullPartyRosterSlot? GetCurrentAssignedSlot(
        FullPartyRunDetail runDetail,
        FullPartyLiveMember currentMember,
        FullPartyUser? currentUser)
    {
        var activeSlots = runDetail.Slots
            .Where(slot => !IsBenchSlot(slot))
            .ToList();

        var bySlotId = activeSlots
            .Where(slot => currentMember.SlotIds.Contains(slot.Id))
            .OrderByDescending(slot => slot.IsHost || slot.IsRaidLeader)
            .ThenBy(slot => slot.PositionInGroup ?? int.MaxValue)
            .ThenBy(slot => slot.SortOrder ?? int.MaxValue)
            .FirstOrDefault();
        if (bySlotId != null)
            return bySlotId;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var characterKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCharacterKey(characterKeys, currentMember.CharacterName, currentMember.World);
        AddCharacterKey(characterKeys, currentUser?.PrimaryCharacter?.Name, currentUser?.PrimaryCharacter?.World);
        AddCharacterKey(characterKeys, localPlayer?.Name.ToString(), localPlayer?.HomeWorld.Value.Name.ToString());

        return activeSlots.FirstOrDefault(slot =>
            slot.AssignedCharacter != null &&
            ((currentUser?.PrimaryCharacter?.Id is > 0 && slot.AssignedCharacter.Id == currentUser.PrimaryCharacter.Id) ||
             characterKeys.Contains(GetCharacterKey(slot.AssignedCharacter.Name, slot.AssignedCharacter.World))));
    }

    private static bool HasAssignedSlotMember(IReadOnlyList<FullPartyPartySnapshotMember> members, FullPartyRosterSlot assignedSlot)
    {
        var character = assignedSlot.AssignedCharacter;
        if (character == null)
            return false;

        var characterKey = GetCharacterKey(character.Name, character.World);
        return members.Any(member =>
            member.CharacterId == character.Id ||
            GetCharacterKey(member.Name, member.World).Equals(characterKey, StringComparison.OrdinalIgnoreCase));
    }

    private static int? GetClassJobId(IPartyMember member)
    {
        var rowId = member.ClassJob.RowId;
        return GetCombatClassJobId(rowId);
    }

    private static FullPartyPartySnapshotMember MapLocalPlayerFallback(
        FullPartyRosterSlot assignedSlot,
        FullPartyLiveMember currentMember,
        FullPartyRunDetail runDetail)
    {
        var character = assignedSlot.AssignedCharacter;
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var localName = localPlayer?.Name.ToString();
        var localWorld = localPlayer?.HomeWorld.Value.Name.ToString();
        var characterId = character?.Id;

        return new FullPartyPartySnapshotMember(
            assignedSlot.PositionInGroup ?? 1,
            characterId,
            characterId == null ? FirstNonEmpty(localName, currentMember.CharacterName, character?.Name) : null,
            characterId == null ? FirstNonEmpty(localWorld, currentMember.World, character?.World) : null,
            GetCombatClassJobId(localPlayer?.ClassJob.RowId ?? Plugin.PlayerState.ClassJob.RowId),
            GetPhantomJobIdFromStatuses(localPlayer?.StatusList, runDetail));
    }

    internal static int? GetCombatClassJobId(uint rowId)
    {
        return IsCombatClassJobId(rowId) ? (int)rowId : null;
    }

    private static bool IsCombatClassJobId(uint rowId)
    {
        return rowId is >= 1 and <= 7 or >= 19 and <= 42;
    }

    private static int? GetPhantomJobId(IPartyMember member, FullPartyRunDetail runDetail)
    {
        return GetPhantomJobIdFromStatuses(member.Statuses, runDetail);
    }

    internal static int? GetPhantomJobIdFromStatuses(IEnumerable? statuses, FullPartyRunDetail runDetail)
    {
        var expectedJobs = runDetail.Slots
            .Where(slot => slot.PhantomJobId != null && !string.IsNullOrWhiteSpace(slot.PhantomJob))
            .GroupBy(slot => slot.PhantomJobId!.Value)
            .Select(group => (Id: group.Key, Name: NormalizeToken(group.First().PhantomJob!)))
            .Where(job => job.Name.Length > 0)
            .ToList();

        if (expectedJobs.Count == 0)
            return null;

        if (statuses == null)
            return null;

        foreach (var statusObject in statuses)
        {
            if (statusObject is not IStatus status)
                continue;

            var statusName = NormalizeToken(GetStatusName((uint)status.StatusId) ?? string.Empty);
            if (statusName.Length == 0)
                continue;

            foreach (var job in expectedJobs)
            {
                if (statusName.Contains(job.Name, StringComparison.Ordinal))
                    return job.Id;
            }
        }

        return null;
    }

    private static string? GetStatusName(uint statusId)
    {
        if (statusId == 0)
            return null;

        if (StatusNameCache.TryGetValue(statusId, out var cached))
            return cached;

        foreach (var status in Plugin.DataManager.GetExcelSheet<LuminaStatus>())
        {
            if (status.RowId != statusId)
                continue;

            var name = status.Name.ToString();
            StatusNameCache[statusId] = name;
            return name;
        }

        StatusNameCache[statusId] = null;
        return null;
    }

    private static string GetName(IPartyMember member)
    {
        return member.Name.ToString();
    }

    private static string GetWorld(IPartyMember member)
    {
        return member.World.Value.Name.ToString();
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string GetCharacterKey(string? name, string? world)
    {
        return $"{NormalizeKeyPart(name)}@{NormalizeKeyPart(world)}";
    }

    private static void AddCharacterKey(ISet<string> keys, string? name, string? world)
    {
        var key = GetCharacterKey(name, world);
        if (!key.Equals("@", StringComparison.Ordinal))
            keys.Add(key);
    }

    private static string NormalizeKeyPart(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    }

    private static string NormalizeToken(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static bool IsBenchSlot(FullPartyRosterSlot slot)
    {
        return slot.GroupKey.Contains("bench", StringComparison.OrdinalIgnoreCase) ||
               slot.GroupLabel.Contains("bench", StringComparison.OrdinalIgnoreCase);
    }
}
