using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.ClientState.Statuses;
using FullParty.Auth;
using FullParty.Models;
using LuminaStatus = Lumina.Excel.Sheets.Status;

namespace FullParty.Services;

internal static class PartySnapshotBuilder
{
    private static readonly ClientLanguage[] StatusLanguages =
    [
        ClientLanguage.English,
        ClientLanguage.German,
        ClientLanguage.French,
        ClientLanguage.Japanese,
    ];

    private static readonly Dictionary<(uint StatusId, ClientLanguage Language), string?> StatusNameCache = new();

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

    public static PartySnapshotBuildDebug BuildDebug(
        FullPartyRunDetail runDetail,
        FullPartyLiveMember currentMember,
        FullPartyUser? currentUser,
        FullPartyPartySnapshot? snapshot)
    {
        var assignedSlot = GetCurrentAssignedSlot(runDetail, currentMember, currentUser);
        var raidLeadSlot = assignedSlot?.AssignedCharacter != null && (assignedSlot.IsHost || assignedSlot.IsRaidLeader)
            ? assignedSlot
            : runDetail.Slots
                .Where(slot =>
                    slot.AssignedCharacter != null &&
                    (slot.IsHost || slot.IsRaidLeader) &&
                    assignedSlot != null &&
                    slot.GroupKey.Equals(assignedSlot.GroupKey, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(slot => slot.IsHost)
                .ThenBy(slot => slot.PositionInGroup ?? int.MaxValue)
                .ThenBy(slot => slot.SortOrder ?? int.MaxValue)
                .FirstOrDefault();

        return new PartySnapshotBuildDebug(
            raidLeadSlot?.AssignedCharacter?.Name,
            raidLeadSlot?.AssignedCharacter?.World,
            assignedSlot?.GroupKey ?? snapshot?.PartyKey);
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
        var phantomJob = GetPhantomJob(member, runDetail);
        var statusDebug = GetStatusDebugList(member.Statuses);

        return new FullPartyPartySnapshotMember(
            position,
            characterId,
            characterId == null ? name : null,
            characterId == null ? world : null,
            GetCombatClassJobShorthand(member.ClassJob.RowId),
            phantomJob?.SnapshotName,
            phantomJob?.StatusId,
            phantomJob?.StatusName,
            statusDebug,
            GetResurrectionCharges(member.Statuses));
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
        var phantomJob = GetPhantomJobDetectionFromStatuses(localPlayer?.StatusList, runDetail);
        var statusDebug = GetStatusDebugList(localPlayer?.StatusList);

        return new FullPartyPartySnapshotMember(
            assignedSlot.PositionInGroup ?? 1,
            characterId,
            characterId == null ? FirstNonEmpty(localName, currentMember.CharacterName, character?.Name) : null,
            characterId == null ? FirstNonEmpty(localWorld, currentMember.World, character?.World) : null,
            GetCombatClassJobShorthand(localPlayer?.ClassJob.RowId ?? Plugin.PlayerState.ClassJob.RowId),
            phantomJob?.SnapshotName,
            phantomJob?.StatusId,
            phantomJob?.StatusName,
            statusDebug,
            GetResurrectionCharges(localPlayer?.StatusList));
    }

    internal static int? GetCombatClassJobId(uint rowId)
    {
        return IsCombatClassJobId(rowId) ? (int)rowId : null;
    }

    private static bool IsCombatClassJobId(uint rowId)
    {
        return rowId is >= 1 and <= 7 or >= 19 and <= 42;
    }

    internal static string? GetCombatClassJobShorthand(uint rowId)
    {
        return ClassJobResolver.GetCombatClassJobShorthand(rowId);
    }

    private static PhantomJobDetection? GetPhantomJob(IPartyMember member, FullPartyRunDetail runDetail)
    {
        return GetPhantomJobDetectionFromStatuses(member.Statuses, runDetail);
    }

    internal static string? GetPhantomJobFromStatuses(IEnumerable? statuses, FullPartyRunDetail runDetail)
    {
        return GetPhantomJobDetectionFromStatuses(statuses, runDetail)?.SnapshotName;
    }

    internal static PhantomJobDetection? GetPhantomJobDetectionFromStatuses(IEnumerable? statuses, FullPartyRunDetail _)
    {
        var detection = PhantomJobResolver.DetectFromStatuses(statuses);
        return detection == null
            ? null
            : new PhantomJobDetection(detection.SnapshotName, detection.StatusId, detection.StatusName);
    }

    internal static bool HasStatus(IEnumerable? statuses, uint statusId)
    {
        if (statuses == null)
            return false;

        foreach (var statusObject in statuses)
        {
            if (TryGetStatusId(statusObject, out var detectedId) && detectedId == statusId)
                return true;
        }

        return false;
    }

    internal static int? GetResurrectionCharges(IEnumerable? statuses)
    {
        if (statuses == null)
            return null;

        int? restrictedCharges = null;
        foreach (var statusObject in statuses)
        {
            if (!TryGetStatusId(statusObject, out var statusId))
                continue;

            if (statusId == OccultCrescentStatusIds.ResurrectionDenied)
                return 0;

            if (statusId == OccultCrescentStatusIds.ResurrectionRestricted && statusObject is IStatus status)
            {
                var stacks = (int)status.Param;
                if (stacks is >= 1 and <= 3)
                    restrictedCharges = stacks;
            }
        }

        return restrictedCharges;
    }

    internal sealed record PhantomJobDetection(string SnapshotName, uint StatusId, string StatusName);

    internal sealed record PartySnapshotBuildDebug(
        string? RaidLeadName,
        string? RaidLeadWorld,
        string? PartyKey);

    internal static IReadOnlyList<FullPartyStatusDebug> GetStatusDebugList(IEnumerable? statuses)
    {
        if (statuses == null)
            return [];

        var seen = new HashSet<uint>();
        var debug = new List<FullPartyStatusDebug>();
        foreach (var statusObject in statuses)
        {
            if (!TryGetStatusId(statusObject, out var statusId) || !seen.Add(statusId))
                continue;

            var names = GetStatusNames(statusId);
            var statusName = names.Count == 0
                ? "unknown Lumina status"
                : string.Join(" / ", names);
            debug.Add(new FullPartyStatusDebug(statusId, statusName));
        }

        return debug;
    }

    internal static bool TryGetStatusId(object? statusObject, out uint statusId)
    {
        statusId = 0;
        if (statusObject == null)
            return false;

        if (statusObject is IStatus status)
        {
            statusId = (uint)status.StatusId;
            return statusId > 0;
        }

        var type = statusObject.GetType();
        foreach (var propertyName in new[] { "StatusId", "StatusID", "Id", "RowId" })
        {
            var property = type.GetProperty(propertyName);
            if (property != null && TryConvertStatusId(property.GetValue(statusObject), out statusId))
                return true;
        }

        foreach (var fieldName in new[] { "StatusId", "StatusID", "Id", "RowId" })
        {
            var field = type.GetField(fieldName);
            if (field != null && TryConvertStatusId(field.GetValue(statusObject), out statusId))
                return true;
        }

        return false;
    }

    private static bool TryConvertStatusId(object? value, out uint statusId)
    {
        statusId = 0;
        if (value == null)
            return false;

        try
        {
            statusId = value switch
            {
                byte id => id,
                ushort id => id,
                uint id => id,
                int id when id > 0 => (uint)id,
                short id when id > 0 => (uint)id,
                string text when uint.TryParse(text, out var parsed) => parsed,
                _ => Convert.ToUInt32(value),
            };
        }
        catch
        {
            return false;
        }

        return statusId > 0;
    }

    internal static IReadOnlyList<string> GetStatusNames(uint statusId)
    {
        if (statusId == 0)
            return [];

        var names = new List<string>();
        foreach (var language in StatusLanguages)
        {
            var name = GetStatusName(statusId, language);
            if (!string.IsNullOrWhiteSpace(name) &&
                !names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static string? GetStatusName(uint statusId, ClientLanguage language)
    {
        if (statusId == 0)
            return null;

        var key = (statusId, language);
        if (StatusNameCache.TryGetValue(key, out var cached))
            return cached;

        try
        {
            var name = Plugin.DataManager.GetExcelSheet<LuminaStatus>(language).TryGetRow(statusId, out var status)
                ? status.Name.ToString()
                : null;
            StatusNameCache[key] = name;
            return name;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Could not read FullParty status {StatusId} for {Language}.", statusId, language);
            StatusNameCache[key] = null;
            return null;
        }
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

    private static bool IsBenchSlot(FullPartyRosterSlot slot)
    {
        return slot.GroupKey.Contains("bench", StringComparison.OrdinalIgnoreCase) ||
               slot.GroupLabel.Contains("bench", StringComparison.OrdinalIgnoreCase);
    }
}
