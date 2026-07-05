using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Game.ClientState.Party;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;

namespace FullParty.Services;

internal static unsafe class PartyListDebugDumper
{
    private const int MaxPartyMembers = 8;
    private const int MaxAllianceSlotsToProbe = 48;
    private static readonly ClientLanguage[] DebugLanguages =
    [
        ClientLanguage.English,
        ClientLanguage.German,
        ClientLanguage.French,
        ClientLanguage.Japanese,
    ];

    public static void Dump()
    {
        var lines = new List<string>
        {
            "FullParty party/alliance dump",
            $"PartyList IsAlliance={SafeValue(() => Plugin.PartyList.IsAlliance)} Length={SafeValue(() => Plugin.PartyList.Length)} PartyId={SafeValue(() => Plugin.PartyList.PartyId)} LeaderIndex={SafeValue(() => Plugin.PartyList.PartyLeaderIndex)}",
        };

        DumpLocalParty(lines);
        DumpLocalPlayerStatuses(lines);
        DumpCrossRealmParty(lines);
        DumpInfoProxyPartyMembers(lines);
        DumpAllianceMembers(lines);

        foreach (var line in lines)
        {
            Plugin.ChatGui.Print(line, "FullParty", null);
            Plugin.Log.Information("{Line}", line);
        }
    }

    private static void DumpInfoProxyPartyMembers(ICollection<string> lines)
    {
        lines.Add("InfoProxy PartyMember rows:");

        try
        {
            var infoModule = InfoModule.Instance();
            if (infoModule == null)
            {
                lines.Add("  InfoModule.Instance() returned null");
                return;
            }

            var proxy = infoModule->GetInfoProxyById(InfoProxyId.PartyMember);
            if (proxy == null)
            {
                lines.Add("  InfoProxyId.PartyMember returned null");
                return;
            }

            var partyProxy = (InfoProxyPartyMember*)proxy;
            var count = 0;
            foreach (var characterData in partyProxy->CharDataSpan)
            {
                if (characterData.ContentId == 0 && string.IsNullOrWhiteSpace(characterData.NameString))
                    continue;

                lines.Add(
                    $"  proxy[{count}] name=\"{characterData.NameString}\" world=\"{GetWorldName(characterData.HomeWorld)}\" currentWorld=\"{GetWorldName(characterData.CurrentWorld)}\" job={characterData.Job} group={characterData.Group} sort={characterData.Sort} location={characterData.Location} state={characterData.State} content={characterData.ContentId}");
                count++;
            }

            if (count == 0)
                lines.Add("  (no InfoProxy PartyMember rows found)");
        }
        catch (Exception ex)
        {
            lines.Add($"  InfoProxy PartyMember read failed: {ex.Message}");
        }
    }

    private static void DumpCrossRealmParty(ICollection<string> lines)
    {
        lines.Add("CrossRealm Party proxy:");

        try
        {
            var infoModule = InfoModule.Instance();
            if (infoModule == null)
            {
                lines.Add("  InfoModule.Instance() returned null");
                return;
            }

            var proxy = infoModule->GetInfoProxyById(InfoProxyId.CrossRealmParty);
            if (proxy == null)
            {
                lines.Add("  InfoProxyId.CrossRealmParty returned null");
                return;
            }

            var crossRealm = (InfoProxyCrossRealm*)proxy;
            lines.Add(
                $"  groupCount={crossRealm->GroupCount} localGroup={crossRealm->LocalPlayerGroupIndex} entryCount={crossRealm->EntryCount} isCrossRealm={crossRealm->IsCrossRealm} isAllianceRaid={crossRealm->IsInAllianceRaid} isInCrossRealmParty={crossRealm->IsInCrossRealmParty} isPartyLeader={crossRealm->IsPartyLeader}");

            var groups = crossRealm->CrossRealmGroups;
            var printed = 0;
            for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
            {
                var group = groups[groupIndex];
                if (group.GroupMemberCount == 0)
                    continue;

                lines.Add($"  group[{groupIndex}] memberCount={group.GroupMemberCount}");
                var memberLimit = Math.Min((int)group.GroupMemberCount, MaxPartyMembers);
                for (var memberIndex = 0; memberIndex < memberLimit; memberIndex++)
                {
                    var member = group.GroupMembers[memberIndex];
                    if (member.ContentId == 0 && string.IsNullOrWhiteSpace(member.NameString))
                        continue;

                    lines.Add(
                        $"    member[{memberIndex}] name=\"{member.NameString}\" world=\"{GetWorldName(member.HomeWorld)}\" currentWorld=\"{GetWorldName(member.CurrentWorld)}\" class={member.ClassJobId}:{GetClassJobName(member.ClassJobId)} level={member.Level} memberIndex={member.MemberIndex} groupIndex={member.GroupIndex} leader={member.IsPartyLeader} content={member.ContentId} entity={member.EntityId}");
                    printed++;
                }
            }

            if (printed == 0)
                lines.Add("  (no CrossRealm party members found)");
        }
        catch (Exception ex)
        {
            lines.Add($"  CrossRealm Party read failed: {ex.Message}");
        }
    }

    private static void DumpLocalParty(ICollection<string> lines)
    {
        lines.Add("Local party list:");

        var count = 0;
        try
        {
            foreach (var (member, index) in Plugin.PartyList.Select((member, index) => (member, index)))
            {
                if (index >= MaxPartyMembers)
                    break;

                count++;
                lines.Add($"  local[{index}] {DescribeMember(member)}");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"  local read failed: {ex.Message}");
        }

        if (count == 0)
            lines.Add("  (no local party members found)");
    }

    private static void DumpLocalPlayerStatuses(ICollection<string> lines)
    {
        lines.Add("Local player statuses:");

        try
        {
            var localPlayer = Plugin.ObjectTable.LocalPlayer;
            if (localPlayer == null)
            {
                lines.Add("  (local player unavailable)");
                return;
            }

            var classJobRow = localPlayer.ClassJob.RowId;
            lines.Add(
                $"  class={classJobRow}:{ClassJobResolver.GetCombatClassJobShorthand(classJobRow) ?? "unknown"} names={FormatClassJobNames(classJobRow)}");

            var count = 0;
            foreach (var status in localPlayer.StatusList)
            {
                if (status.StatusId == 0)
                    continue;

                lines.Add(
                    $"  status[{count}] id={status.StatusId} names={FormatStatusNames((uint)status.StatusId)}");
                count++;
            }

            if (count == 0)
                lines.Add("  (no active local player statuses)");
        }
        catch (Exception ex)
        {
            lines.Add($"  local status read failed: {ex.Message}");
        }
    }

    private static void DumpAllianceMembers(ICollection<string> lines)
    {
        lines.Add($"Alliance address probe 0..{MaxAllianceSlotsToProbe - 1}:");

        var count = 0;
        var emptyAddressCount = 0;
        for (var index = 0; index < MaxAllianceSlotsToProbe; index++)
        {
            IntPtr address;
            try
            {
                address = Plugin.PartyList.GetAllianceMemberAddress(index);
            }
            catch (Exception ex)
            {
                lines.Add($"  alliance[{index}] address read stopped: {ex.Message}");
                break;
            }

            if (address == IntPtr.Zero)
                continue;

            var partyNumber = (index / MaxPartyMembers) + 1;
            var slotNumber = (index % MaxPartyMembers) + 1;

            try
            {
                var member = Plugin.PartyList.CreateAllianceMemberReference(address);
                if (member == null)
                {
                    lines.Add($"  alliance[{index}] partyBlock={partyNumber} slot={slotNumber} addr=0x{address.ToInt64():X} member=null");
                    continue;
                }

                if (IsEmptyAllianceMember(member))
                {
                    emptyAddressCount++;
                    continue;
                }

                count++;
                lines.Add($"  alliance[{index}] partyBlock={partyNumber} slot={slotNumber} addr=0x{address.ToInt64():X} {DescribeMember(member)}");
            }
            catch (Exception ex)
            {
                lines.Add($"  alliance[{index}] partyBlock={partyNumber} slot={slotNumber} addr=0x{address.ToInt64():X} member read failed: {ex.Message}");
            }
        }

        lines.Add($"Alliance non-empty addresses found: {count}");
        if (emptyAddressCount > 0)
            lines.Add($"Alliance empty placeholder addresses skipped: {emptyAddressCount}");
        if (count == 0)
            lines.Add("  (no alliance members found through GetAllianceMemberAddress)");
    }

    private static string DescribeMember(IPartyMember member)
    {
        return $"name=\"{SafeValue(() => member.Name.ToString())}\" world=\"{SafeValue(() => member.World.Value.Name.ToString())}\" class={SafeValue(() => member.ClassJob.RowId)}:{SafeValue(() => member.ClassJob.Value.Name.ToString())} level={SafeValue(() => member.Level)} pos={SafeValue(() => member.Position)} content={SafeValue(() => member.ContentId)} entity={SafeValue(() => member.EntityId)}";
    }

    private static bool IsEmptyAllianceMember(IPartyMember member)
    {
        return string.IsNullOrWhiteSpace(SafeValue(() => member.Name.ToString())) &&
               SafeValue(() => member.ContentId).Equals("0", StringComparison.Ordinal) &&
               SafeValue(() => member.ClassJob.RowId).Equals("0", StringComparison.Ordinal);
    }

    private static string? GetClassJobName(byte classJobId)
    {
        if (classJobId == 0)
            return null;

        return Plugin.DataManager.GetExcelSheet<ClassJob>().TryGetRow(classJobId, out var classJob)
            ? classJob.Name.ToString()
            : null;
    }

    private static string FormatClassJobNames(uint classJobId)
    {
        var names = new List<string>();
        foreach (var language in DebugLanguages)
        {
            try
            {
                if (!Plugin.DataManager.GetExcelSheet<ClassJob>(language).TryGetRow(classJobId, out var classJob))
                    continue;

                var name = classJob.Name.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add($"{language}=\"{name}\"");
            }
            catch
            {
                names.Add($"{language}=<error>");
            }
        }

        return names.Count == 0 ? "(none)" : string.Join(", ", names);
    }

    private static string FormatStatusNames(uint statusId)
    {
        var names = PartySnapshotBuilder.GetStatusNames(statusId);
        return names.Count == 0 ? "(none)" : string.Join(" | ", names.Select(name => $"\"{name}\""));
    }

    private static string? GetWorldName(ushort worldId)
    {
        if (worldId == 0)
            return null;

        return Plugin.DataManager.GetExcelSheet<World>().TryGetRow(worldId, out var world)
            ? world.Name.ToString()
            : null;
    }

    private static string? GetWorldName(short worldId)
    {
        if (worldId <= 0)
            return null;

        return GetWorldName((ushort)worldId);
    }

    private static string SafeValue<T>(Func<T> read)
    {
        try
        {
            return read()?.ToString() ?? "null";
        }
        catch (Exception ex)
        {
            return $"<error: {ex.Message}>";
        }
    }
}
