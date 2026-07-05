using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using FullParty.Models;
using FullParty.Services;

namespace FullParty.Windows;

internal enum RunRosterViewMode
{
    None,
    Roster,
    Party,
    Validate,
}

internal enum ValidationState
{
    Neutral,
    Ok,
    Warning,
    Error,
}

internal sealed record ObservedSnapshotMember(
    FullPartyPartySnapshot Snapshot,
    FullPartyPartySnapshotMember Member);

internal sealed record OccultPartyAssignment(
    FullPartyPartySnapshot Snapshot,
    string GroupLabel,
    int PartyLeadCount);

internal sealed record RunCheckInSelection(
    IReadOnlyList<int> SlotIds,
    IReadOnlyList<long> CharacterIds,
    int PresentCount,
    int MissingCount);

internal sealed record RunCheckInSummary(
    int CheckedInCount,
    int MissingCount);

internal sealed record ComputedPartySnapshots(
    IReadOnlyDictionary<string, FullPartyPartySnapshot> ByParty,
    IReadOnlyList<FullPartyPartySnapshot> Snapshots,
    GamePresenceList OccultPresence,
    int OccultPartyCount,
    bool InOccult);

internal sealed record ValidationSlotResult(
    FullPartyRosterCharacter? Character,
    FullPartyRosterSlot? RosterSlot,
    string DisplayName,
    string? ClassJob,
    string? PhantomJob,
    ValidationState State,
    IReadOnlyList<string> Messages);

public sealed class RunWindow : Window, IDisposable
{
    private static readonly Dictionary<string, uint?> JobIconCache = new(StringComparer.OrdinalIgnoreCase);
    private const float RunWindowDefaultWidth = 420f;
    private const float RunWindowDefaultHeight = 560f;
    private const float RosterCompanionDefaultWidth = 980f;
    private const float RosterCompanionDefaultHeight = 560f;
    private const float RosterCompanionMinWidth = 620f;
    private const float RosterCompanionMinHeight = 320f;
    private const float PartyLeadCrownWidth = 16f;
    private const float RosterCompanionGap = 8f;

    private readonly Plugin plugin;
    private readonly CancellationTokenSource cancellation = new();
    private readonly RealtimeRunRoomClient liveRoom;
    private Task<FullPartyRunDetail?>? detailTask;
    private Task<RunCheckInSummary>? checkInTask;
    private FullPartyRunDetail? detail;
    private string? detailError;
    private string? checkInStatusMessage;
    private RunRosterViewMode rosterViewMode = RunRosterViewMode.Roster;
    private Vector2 rosterCompanionSize = new(RosterCompanionDefaultWidth, RosterCompanionDefaultHeight);
    private bool hasRosterCompanionSize;
    private bool applyRosterCompanionSizeNextDraw = true;
    private bool? lastOccultState;

    public FullPartyRun Run { get; }

    public RunWindow(FullPartyRun run, Plugin plugin)
        : base($"{run.Name} - {run.StartsAt:MMM d, yyyy} - {run.StartsAt:HH:mm}##FullPartyRun{run.Id}")
    {
        Run = run;
        this.plugin = plugin;
        liveRoom = new RealtimeRunRoomClient(run.Id, plugin);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(RunWindowDefaultWidth, RunWindowDefaultHeight);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = true;
    }

    public void Dispose()
    {
        cancellation.Cancel();
        cancellation.Dispose();
        liveRoom.Dispose();
    }

    public override void Draw()
    {
        EnsureDetailLoaded();
        ObserveCheckInTask();

        var runWindowPosition = ImGui.GetWindowPos();
        var runWindowSize = ImGui.GetWindowSize();
        var isLoading = detailTask is { IsCompleted: false };
        DrawPartyActionsSection(detail?.CanModerate == true);
        DrawSectionSeparator();
        DrawRosterControls(isLoading);
        DrawSectionSeparator();
        DrawLiveRoom();
        DrawRosterCompanion(runWindowPosition, runWindowSize, isLoading);
    }

    private void DrawPartyActionsSection(bool canModerate)
    {
        ImGui.Text("Party Actions");
        ImGui.Spacing();

        var canSendLiveCommand = canModerate && liveRoom.State == RealtimeRunRoomState.Connected && !liveRoom.IsIssuingCommand;
        if (!canSendLiveCommand)
            ImGui.BeginDisabled();

        if (ImGui.Button("Ready Check Alliance"))
            liveRoom.SendReadyCheckAlliance();

        ImGui.SameLine();

        if (ImGui.Button("Start Countdown"))
            liveRoom.SendCountdown(20);

        if (!canSendLiveCommand)
            ImGui.EndDisabled();

        ImGui.SameLine();

        var isCheckingIn = checkInTask is { IsCompleted: false };
        var waitingForAdventurerList = OccultCrescentTerritory.IsCurrent() && plugin.AdventurerList.IsRefreshing;
        var canCheckIn = detail != null && !isCheckingIn && !waitingForAdventurerList;
        if (!canCheckIn)
            ImGui.BeginDisabled();

        if (ImGui.Button(isCheckingIn ? "Checking In..." : "Run Check-In"))
            StartRunCheckIn();

        if (!canCheckIn)
            ImGui.EndDisabled();

        if (!string.IsNullOrWhiteSpace(checkInStatusMessage))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(checkInStatusMessage);
        }
    }

    private void DrawRosterControls(bool isLoading)
    {
        ImGui.Text("Roster");
        ImGui.Spacing();

        if (isLoading)
            ImGui.BeginDisabled();

        if (ImGui.Button("Refresh Data"))
            RefreshRosterData();

        if (isLoading)
            ImGui.EndDisabled();

        ImGui.Spacing();
        DrawRosterModeButton("Show Roster", RunRosterViewMode.Roster);
        ImGui.SameLine();
        DrawRosterModeButton("Show Party View", RunRosterViewMode.Party);
        ImGui.Spacing();
        DrawRosterModeButton("Show Validation View", RunRosterViewMode.Validate);
        ImGui.SameLine();
        DrawRosterModeButton("Show None", RunRosterViewMode.None);

        if (isLoading)
            ImGui.TextDisabled("Loading roster...");
        else if (!string.IsNullOrWhiteSpace(detailError))
            ImGui.TextWrapped(detailError);
        else if (detail == null)
            ImGui.TextDisabled("No roster loaded.");
    }

    private void DrawRosterCompanion(Vector2 runWindowPosition, Vector2 runWindowSize, bool isLoading)
    {
        if (rosterViewMode == RunRosterViewMode.None)
            return;

        var companionPosition = new Vector2(
            runWindowPosition.X + runWindowSize.X + RosterCompanionGap,
            runWindowPosition.Y);
        var flags = ImGuiWindowFlags.NoCollapse |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoSavedSettings;

        ImGui.SetNextWindowPos(companionPosition, ImGuiCond.Always);
        if (applyRosterCompanionSizeNextDraw)
        {
            ImGui.SetNextWindowSize(rosterCompanionSize, ImGuiCond.Always);
        }
        else if (!hasRosterCompanionSize)
        {
            ImGui.SetNextWindowSize(
                new Vector2(RosterCompanionDefaultWidth, RosterCompanionDefaultHeight),
                ImGuiCond.FirstUseEver);
        }

        ImGui.SetNextWindowSizeConstraints(
            new Vector2(RosterCompanionMinWidth, RosterCompanionMinHeight),
            new Vector2(float.MaxValue, float.MaxValue));

        if (ImGui.Begin($"{GetRosterViewTitle(rosterViewMode)}##fullparty_roster_companion_{Run.Id}", flags))
        {
            RememberRosterCompanionSize(ImGui.GetWindowSize());
            applyRosterCompanionSizeNextDraw = false;
            DrawRosterCompanionContent(isLoading);
        }

        ImGui.End();
    }

    private void DrawLiveRoom()
    {
        ImGui.Text("Live Room");
        ImGui.Spacing();

        var isBusy = liveRoom.IsBusy;
        if (liveRoom.IsActive)
        {
            if (ImGui.Button("Disconnect Live Room"))
                liveRoom.Disconnect();
        }
        else
        {
            if (isBusy)
                ImGui.BeginDisabled();

            if (ImGui.Button("Connect To Live Room"))
                liveRoom.Connect();

            if (isBusy)
                ImGui.EndDisabled();
        }

        var statusColor = liveRoom.State switch
        {
            RealtimeRunRoomState.Connected => new Vector4(0.35f, 0.92f, 0.55f, 1f),
            RealtimeRunRoomState.Error => new Vector4(1f, 0.42f, 0.42f, 1f),
            RealtimeRunRoomState.Disconnected => new Vector4(0.65f, 0.65f, 0.70f, 1f),
            _ => new Vector4(0.90f, 0.82f, 0.50f, 1f),
        };

        ImGui.SameLine();
        ImGui.TextColored(statusColor, liveRoom.StatusMessage);

        if (!string.IsNullOrWhiteSpace(liveRoom.CommandStatusMessage))
        {
            ImGui.TextDisabled(liveRoom.CommandStatusMessage);
        }

        if (!string.IsNullOrWhiteSpace(liveRoom.PartySnapshotStatusMessage))
        {
            ImGui.TextDisabled(liveRoom.PartySnapshotStatusMessage);
        }

        var members = liveRoom.Members;
        if (!liveRoom.IsActive && members.Count == 0 && liveRoom.State != RealtimeRunRoomState.Error)
            return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (members.Count == 0)
        {
            ImGui.TextDisabled(liveRoom.State == RealtimeRunRoomState.Connected
                ? "No connected characters yet."
                : "Connect to see live characters.");
            return;
        }

        var flags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##fullparty_live_room_members", 4, flags))
            return;

        ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("User", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, 96f);
        ImGui.TableHeadersRow();

        foreach (var member in members)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawLiveMemberCharacter(member);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(member.UserName);

            ImGui.TableNextColumn();
            var commandStatus = liveRoom.GetCommandStatus(member);
            ImGui.TextColored(GetLiveCommandStatusColor(commandStatus), commandStatus);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(GetLiveMemberRole(member));
        }

        ImGui.EndTable();
    }

    private void DrawRosterCompanionContent(bool isLoading)
    {
        if (isLoading)
        {
            ImGui.TextDisabled("Loading roster...");
            return;
        }

        if (!string.IsNullOrWhiteSpace(detailError))
        {
            ImGui.TextWrapped(detailError);
            return;
        }

        if (detail == null)
        {
            ImGui.TextDisabled("No roster loaded.");
            return;
        }

        switch (rosterViewMode)
        {
            case RunRosterViewMode.Party:
                DrawPartyView(detail);
                break;
            case RunRosterViewMode.Validate:
                DrawValidationView(detail);
                break;
            default:
                DrawRosterTable(detail);
                break;
        }
    }

    private void DrawRosterModeButton(string label, RunRosterViewMode mode)
    {
        var selected = rosterViewMode == mode;
        if (selected)
            ImGui.BeginDisabled();

        if (ImGui.Button(label))
        {
            rosterViewMode = mode;
            if (mode != RunRosterViewMode.None)
                applyRosterCompanionSizeNextDraw = true;
        }

        if (selected)
            ImGui.EndDisabled();
    }

    private void RememberRosterCompanionSize(Vector2 size)
    {
        rosterCompanionSize = new Vector2(
            MathF.Max(RosterCompanionMinWidth, size.X),
            MathF.Max(RosterCompanionMinHeight, size.Y));
        hasRosterCompanionSize = true;
    }

    private static void DrawSectionSeparator()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static string GetRosterViewTitle(RunRosterViewMode mode)
    {
        return mode switch
        {
            RunRosterViewMode.Party => "Party View",
            RunRosterViewMode.Validate => "Validation View",
            _ => "Roster",
        };
    }

    private void DrawRosterTable(FullPartyRunDetail runDetail)
    {
        var parties = GetRosterParties(runDetail);
        var benchSlots = GetBenchSlots(runDetail);

        if (parties.Count == 0 && benchSlots.Count == 0)
        {
            ImGui.TextDisabled("No roster slots.");
            return;
        }

        if (parties.Count > 0)
        {
            DrawPartyRosterTable(parties, runDetail.CanModerate);
        }

        if (benchSlots.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.Text("Bench");
            ImGui.Spacing();
            DrawBenchTable(benchSlots, runDetail.CanModerate);
        }
    }

    private void DrawPartyRosterTable(IReadOnlyList<IReadOnlyList<FullPartyRosterSlot>> parties, bool canModerate)
    {
        var flags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame;
        if (!ImGui.BeginTable("##fullparty_roster", parties.Count, flags))
            return;

        foreach (var party in parties)
        {
            ImGui.TableSetupColumn(party[0].GroupLabel);
        }

        ImGui.TableHeadersRow();

        var maxRows = parties.Max(party => party.Count);
        for (var row = 0; row < maxRows; row++)
        {
            ImGui.TableNextRow();
            for (var column = 0; column < parties.Count; column++)
            {
                ImGui.TableNextColumn();
                if (row < parties[column].Count)
                {
                    DrawRosterSlot(parties[column][row], canModerate);
                }
            }
        }

        ImGui.EndTable();
    }

    private void DrawBenchTable(IReadOnlyList<FullPartyRosterSlot> benchSlots, bool canModerate)
    {
        var columnCount = Math.Clamp((int)(ImGui.GetContentRegionAvail().X / 260f), 1, 3);
        var flags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame;
        if (!ImGui.BeginTable("##fullparty_bench", columnCount, flags))
            return;

        for (var column = 0; column < columnCount; column++)
        {
            ImGui.TableSetupColumn($"##bench_column_{column}");
        }

        for (var index = 0; index < benchSlots.Count; index++)
        {
            if (index % columnCount == 0)
                ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawRosterSlot(benchSlots[index], canModerate);
        }

        ImGui.EndTable();
    }

    private void DrawPartyView(FullPartyRunDetail runDetail)
    {
        var parties = GetRosterParties(runDetail);
        if (parties.Count == 0)
        {
            ImGui.TextDisabled("No party slots.");
            return;
        }

        var computedSnapshots = BuildComputedPartySnapshots(runDetail, parties, true);
        var flags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame;
        if (!ImGui.BeginTable("##fullparty_party_view", parties.Count, flags))
            return;

        foreach (var party in parties)
            ImGui.TableSetupColumn(party[0].GroupLabel);

        ImGui.TableHeadersRow();

        var maxRows = parties.Max(party => party.Count);
        for (var row = 0; row < maxRows; row++)
        {
            ImGui.TableNextRow();
            for (var column = 0; column < parties.Count; column++)
            {
                ImGui.TableNextColumn();
                if (row >= parties[column].Count)
                    continue;

                var slot = parties[column][row];
                computedSnapshots.ByParty.TryGetValue(slot.GroupKey, out var snapshot);
                var expectedPosition = slot.PositionInGroup ?? row + 1;
                var member = snapshot?.Members.FirstOrDefault(item => item.Position == expectedPosition);
                DrawPartySnapshotSlot(runDetail, slot, member);
            }
        }

        ImGui.EndTable();

        if (computedSnapshots.Snapshots.Count == 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            if (computedSnapshots.InOccult)
            {
                ImGui.TextDisabled(liveRoom.State == RealtimeRunRoomState.Connected
                    ? "Waiting for party lead snapshots to identify live parties."
                    : "Connect to the live room to sync live parties.");
                ImGui.TextDisabled(plugin.AdventurerList.StatusMessage);
            }
            else
            {
                ImGui.TextDisabled(Plugin.PartyList.IsAlliance
                    ? "Waiting for alliance party data."
                    : "Waiting for party data.");
            }
        }
    }

    private void DrawValidationView(FullPartyRunDetail runDetail)
    {
        var parties = GetRosterParties(runDetail);
        if (parties.Count == 0)
        {
            ImGui.TextDisabled("No party slots.");
            return;
        }

        var computedSnapshots = BuildComputedPartySnapshots(runDetail, parties, true);
        var observedById = BuildObservedByCharacterId(computedSnapshots.Snapshots);
        var observedByName = BuildObservedByName(runDetail, computedSnapshots.Snapshots);
        var flags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame;
        if (!ImGui.BeginTable("##fullparty_validate_view", parties.Count, flags))
            return;

        foreach (var party in parties)
            ImGui.TableSetupColumn(party[0].GroupLabel);

        ImGui.TableHeadersRow();

        var maxRows = parties.Max(party => party.Count);
        for (var row = 0; row < maxRows; row++)
        {
            ImGui.TableNextRow();
            for (var column = 0; column < parties.Count; column++)
            {
                ImGui.TableNextColumn();
                if (row >= parties[column].Count)
                    continue;

                var slot = parties[column][row];
                computedSnapshots.ByParty.TryGetValue(slot.GroupKey, out var snapshot);
                var actualMember = FindExpectedMemberInParty(runDetail, slot, snapshot);
                var expectedObserved = FindObservedForSlot(slot, observedById, observedByName);
                DrawValidationRosterSlot(runDetail, slot, actualMember, expectedObserved, computedSnapshots.OccultPresence, computedSnapshots.InOccult, snapshot != null);
            }
        }

        ImGui.EndTable();

        DrawValidationStatusText(computedSnapshots.InOccult, computedSnapshots.Snapshots.Count, computedSnapshots.OccultPresence.Count, computedSnapshots.OccultPartyCount);
    }

    private void DrawValidationStatusText(bool inOccult, int snapshotCount, int occultPresenceCount, int occultPartyCount)
    {
        if (!inOccult && snapshotCount > 0)
            return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (inOccult)
        {
            if (plugin.AdventurerList.IsRefreshing)
                ImGui.BeginDisabled();

            if (ImGui.Button("Refresh Adventurer List"))
                plugin.AdventurerList.RequestRefresh();

            if (plugin.AdventurerList.IsRefreshing)
                ImGui.EndDisabled();

            ImGui.Spacing();
            ImGui.TextDisabled(plugin.AdventurerList.StatusMessage);
            ImGui.TextDisabled(occultPresenceCount == 0
                ? "Validate: Occult mode, waiting for Adventurer List data."
                : $"Validate: Occult mode, {occultPresenceCount} Adventurer List players known, {occultPartyCount} parties identified by party leads.");
            return;
        }

        ImGui.TextDisabled(Plugin.PartyList.IsAlliance
            ? "Validate: no alliance members detected yet."
            : "Validate: no party members detected yet.");
    }

    private void DrawValidationRosterSlot(
        FullPartyRunDetail runDetail,
        FullPartyRosterSlot plannedSlot,
        FullPartyPartySnapshotMember? actualMember,
        ObservedSnapshotMember? expectedObserved,
        GamePresenceList occultPresence,
        bool useOccultPresence,
        bool expectedPartySynced)
    {
        var result = BuildValidationSlotResult(runDetail, plannedSlot, actualMember, expectedObserved, occultPresence, useOccultPresence, expectedPartySynced);
        if (result == null)
        {
            DrawEmptyRosterSlot(plannedSlot);
            return;
        }

        var canOpenApplication = runDetail.CanModerate && result.RosterSlot?.ApplicationId != null;
        if (ImGui.InvisibleButton($"##validate_slot_{plannedSlot.Id}", new Vector2(-1f, 34f)) && canOpenApplication)
            plugin.OpenApplicationWindow(Run, result.RosterSlot!);

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(GetValidationSlotBackground(result.State, hovered)), 3f);
        drawList.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, hovered ? 0.22f : 0.10f)), 3f);

        var cursor = min + new Vector2(5f, 5f);
        if (result.Character != null)
        {
            DrawCharacterIcon(drawList, result.Character, cursor);
        }
        else
        {
            drawList.AddRectFilled(cursor, cursor + new Vector2(24, 24), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.10f)), 3f);
        }

        cursor.X += 29f;

        var iconRight = max.X - 7f;
        Vector2? phantomIconPosition = null;
        Vector2? classIconPosition = null;
        if (!string.IsNullOrWhiteSpace(result.PhantomJob))
        {
            iconRight -= 20f;
            phantomIconPosition = new Vector2(iconRight, min.Y + 7f);
            iconRight -= 4f;
        }

        if (!string.IsNullOrWhiteSpace(result.ClassJob) ||
            (!useOccultPresence && !string.IsNullOrWhiteSpace(result.RosterSlot?.CharacterClass)))
        {
            iconRight -= 20f;
            classIconPosition = new Vector2(iconRight, min.Y + 7f);
            iconRight -= 4f;
        }

        if (result.RosterSlot != null && IsValidationPartyLead(result.RosterSlot))
            DrawPartyLeadCrown(drawList, ref cursor);

        var nameWidth = Math.Max(24f, iconRight - cursor.X);
        drawList.AddText(cursor + new Vector2(0, 3f), ImGui.GetColorU32(ImGuiCol.Text), TrimToWidth(result.DisplayName, nameWidth));

        if (classIconPosition != null &&
            !DrawJobIcon(drawList, result.ClassJob, classIconPosition.Value) &&
            !DrawJobIcon(drawList, result.RosterSlot?.CharacterClass, classIconPosition.Value))
        {
            DrawIconFallback(drawList, classIconPosition.Value, result.RosterSlot?.CharacterClass, ImGui.GetColorU32(ImGuiCol.TextDisabled));
        }

        if (phantomIconPosition != null &&
            !DrawPhantomJobIconByName(drawList, runDetail, result.PhantomJob, phantomIconPosition.Value) &&
            !string.IsNullOrWhiteSpace(result.PhantomJob))
        {
            DrawIconFallback(drawList, phantomIconPosition.Value, GetPhantomJobDisplayName(runDetail, result.PhantomJob), ImGui.GetColorU32(ImGuiCol.TextDisabled));
        }

        if (hovered && result.Messages.Count > 0)
        {
            const float TooltipWidth = 320f;
            ImGui.SetNextWindowSizeConstraints(
                new Vector2(TooltipWidth, 0f),
                new Vector2(TooltipWidth, float.MaxValue));
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + TooltipWidth);
            ImGui.TextUnformatted(plannedSlot.SlotLabel);
            ImGui.Separator();
            foreach (var message in result.Messages)
                ImGui.TextUnformatted(message);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    private static ValidationSlotResult? BuildValidationSlotResult(
        FullPartyRunDetail runDetail,
        FullPartyRosterSlot plannedSlot,
        FullPartyPartySnapshotMember? actualMember,
        ObservedSnapshotMember? expectedObserved,
        GamePresenceList occultPresence,
        bool useOccultPresence,
        bool expectedPartySynced)
    {
        var expectedCharacter = plannedSlot.AssignedCharacter;
        var expectedPresence = expectedCharacter != null && useOccultPresence && occultPresence.TryFind(expectedCharacter, out var presence)
            ? presence
            : null;

        if (useOccultPresence && expectedCharacter != null && expectedPresence == null)
        {
            return new ValidationSlotResult(
                expectedCharacter,
                plannedSlot,
                expectedCharacter.Name,
                null,
                null,
                ValidationState.Error,
                ["Missing from Adventurer List."]);
        }

        if (actualMember == null)
        {
            if (expectedCharacter == null)
                return null;

            if (useOccultPresence)
            {
                var occultMessages = new List<string>();
                var occultState = ValidationState.Warning;
                string? occultClassJob = null;
                string? occultPhantomJob = null;
                if (expectedObserved != null)
                {
                    occultClassJob = NormalizeClassJob(expectedObserved.Member.ClassJob);
                    occultPhantomJob = expectedObserved.Member.PhantomJob;
                    if (expectedObserved.Snapshot.PartyKey.Equals(plannedSlot.GroupKey, StringComparison.OrdinalIgnoreCase))
                    {
                        occultState = ValidationState.Ok;
                    }
                    else
                    {
                        occultMessages.Add($"Wrong party: currently in {FormatObservedLocation(runDetail, expectedObserved, true)}.");
                    }

                    AddClassValidationMessage(plannedSlot, occultClassJob, occultMessages);
                    AddPhantomJobValidationMessage(runDetail, plannedSlot, expectedObserved.Member, occultMessages);
                }
                else
                {
                    occultMessages.Add(expectedPartySynced
                        ? $"Present in Adventurer List, but not in the synced {plannedSlot.GroupLabel} party."
                        : $"Present in Adventurer List; waiting for a party lead snapshot for {plannedSlot.GroupLabel}.");
                }

                return new ValidationSlotResult(
                    expectedCharacter,
                    plannedSlot,
                    expectedCharacter.Name,
                    occultClassJob,
                    occultPhantomJob,
                    occultMessages.Count == 0 ? occultState : ValidationState.Warning,
                    occultMessages);
            }

            var missingMessages = new List<string>();
            var state = ValidationState.Error;
            string? classJob = null;
            string? phantomJob = plannedSlot.PhantomJob;
            if (expectedObserved != null)
            {
                state = ValidationState.Warning;
                missingMessages.Add(useOccultPresence
                    ? $"Wrong place: currently in {FormatObservedLocation(runDetail, expectedObserved, false)}."
                    : $"Wrong party: currently in {FormatObservedLocation(runDetail, expectedObserved, true)}.");
                classJob = NormalizeClassJob(expectedObserved.Member.ClassJob);
                phantomJob = expectedObserved.Member.PhantomJob ?? plannedSlot.PhantomJob;
            }
            else if (expectedPresence != null)
            {
                state = ValidationState.Warning;
                classJob = NormalizeClassJob(expectedPresence.ClassJob);
                phantomJob = expectedPresence.PhantomJob ?? plannedSlot.PhantomJob;
                missingMessages.Add("Present in Adventurer List; party position has not been synced yet.");
                AddClassValidationMessage(plannedSlot, classJob, missingMessages);
                AddPresencePhantomJobValidationMessage(runDetail, plannedSlot, expectedPresence.PhantomJob, missingMessages);
            }
            else
            {
                missingMessages.Add(useOccultPresence
                    ? "Missing from Adventurer List."
                    : "Missing from alliance/party list.");
            }

            return new ValidationSlotResult(
                expectedCharacter,
                plannedSlot,
                expectedCharacter.Name,
                classJob,
                phantomJob,
                state,
                missingMessages);
        }

        var actualRosterSlot = ResolveRosterSlot(runDetail, actualMember);
        var actualCharacter = actualRosterSlot?.AssignedCharacter;
        var actualDisplayName = actualCharacter?.Name ?? actualMember.DisplayName;
        var actualClassJob = NormalizeClassJob(actualMember.ClassJob);
        var messages = new List<string>();

        if (expectedCharacter == null)
        {
            messages.Add("Unexpected player in an empty roster slot.");
            return new ValidationSlotResult(
                actualCharacter,
                actualRosterSlot,
                actualDisplayName,
                actualClassJob,
                actualMember.PhantomJob,
                ValidationState.Warning,
                messages);
        }

        if (!IsExpectedCharacter(plannedSlot, actualMember, actualRosterSlot))
        {
            messages.Add($"Expected {expectedCharacter.Name}; found {actualDisplayName}.");
            if (expectedObserved != null)
            {
                messages.Add(useOccultPresence
                    ? $"Expected player is currently in {FormatObservedLocation(runDetail, expectedObserved, false)}."
                    : $"Expected player is currently in {FormatObservedLocation(runDetail, expectedObserved, true)}.");
            }
            else if (expectedPresence != null)
            {
                messages.Add("Expected player is present in Adventurer List, but not in this slot.");
            }
            else
            {
                messages.Add(useOccultPresence
                    ? "Expected player is missing from Adventurer List."
                    : "Expected player is missing from alliance/party list.");
            }

            return new ValidationSlotResult(
                actualCharacter,
                actualRosterSlot,
                actualDisplayName,
                actualClassJob,
                actualMember.PhantomJob,
                ValidationState.Warning,
                messages);
        }

        if (useOccultPresence)
        {
            AddClassValidationMessage(plannedSlot, actualClassJob, messages);
            AddPhantomJobValidationMessage(runDetail, plannedSlot, actualMember, messages);

            return new ValidationSlotResult(
                actualCharacter ?? expectedCharacter,
                plannedSlot,
                expectedCharacter.Name,
                actualClassJob,
                actualMember.PhantomJob,
                messages.Count == 0 ? ValidationState.Ok : ValidationState.Warning,
                messages);
        }

        return new ValidationSlotResult(
            actualCharacter ?? expectedCharacter,
            plannedSlot,
            expectedCharacter.Name,
            actualClassJob,
            actualMember.PhantomJob ?? plannedSlot.PhantomJob,
            messages.Count == 0 ? ValidationState.Ok : ValidationState.Warning,
            messages);
    }

    private static bool IsExpectedCharacter(
        FullPartyRosterSlot plannedSlot,
        FullPartyPartySnapshotMember actualMember,
        FullPartyRosterSlot? actualRosterSlot)
    {
        var expectedCharacter = plannedSlot.AssignedCharacter;
        if (expectedCharacter == null)
            return false;

        if (actualMember.CharacterId != null)
            return actualMember.CharacterId.Value == expectedCharacter.Id;

        if (actualRosterSlot?.AssignedCharacter?.Id == expectedCharacter.Id)
            return true;

        var actualKey = GetCharacterKey(actualMember.Name, actualMember.World);
        return !actualKey.Equals("@", StringComparison.Ordinal) &&
               actualKey.Equals(GetCharacterKey(expectedCharacter.Name, expectedCharacter.World), StringComparison.OrdinalIgnoreCase);
    }

    private static void AddClassValidationMessage(
        FullPartyRosterSlot plannedSlot,
        string? actualClassJob,
        ICollection<string> messages)
    {
        var expectedClassJob = GetExpectedClassJob(plannedSlot);
        var expectedLabel = plannedSlot.CharacterClass ?? expectedClassJob;
        if (expectedClassJob == null)
            return;

        actualClassJob = NormalizeClassJob(actualClassJob);
        if (actualClassJob == null)
        {
            messages.Add($"Class unknown; expected {expectedLabel}.");
            return;
        }

        if (!actualClassJob.Equals(expectedClassJob, StringComparison.OrdinalIgnoreCase))
        {
            messages.Add(
                $"Wrong class: expected {expectedLabel}, " +
                $"found {actualClassJob}.");
        }
    }

    private static void AddPhantomJobValidationMessage(
        FullPartyRunDetail runDetail,
        FullPartyRosterSlot plannedSlot,
        FullPartyPartySnapshotMember actualMember,
        ICollection<string> messages)
    {
        AddPresencePhantomJobValidationMessage(runDetail, plannedSlot, actualMember.PhantomJob, messages);
    }

    private static void AddPresencePhantomJobValidationMessage(
        FullPartyRunDetail runDetail,
        FullPartyRosterSlot plannedSlot,
        string? actualPhantomJob,
        ICollection<string> messages)
    {
        var expectedPhantomJob = NormalizePhantomJob(plannedSlot.PhantomJob);
        if (expectedPhantomJob == null)
            return;

        if (NormalizePhantomJob(actualPhantomJob) is not { } normalizedActual)
        {
            messages.Add($"Phantom job unknown; expected {GetPhantomJobDisplayName(runDetail, plannedSlot.PhantomJob)}.");
            return;
        }

        if (!normalizedActual.Equals(expectedPhantomJob, StringComparison.OrdinalIgnoreCase))
        {
            messages.Add(
                $"Wrong phantom job: expected {GetPhantomJobDisplayName(runDetail, plannedSlot.PhantomJob)}, " +
                $"found {GetPhantomJobDisplayName(runDetail, actualPhantomJob)}.");
        }
    }

    private static string FormatObservedLocation(FullPartyRunDetail runDetail, ObservedSnapshotMember observed, bool partyOnly)
    {
        var groupLabel = runDetail.Slots.FirstOrDefault(slot =>
            slot.GroupKey.Equals(observed.Snapshot.PartyKey, StringComparison.OrdinalIgnoreCase))?.GroupLabel;

        if (partyOnly)
            return string.IsNullOrWhiteSpace(groupLabel) ? observed.Snapshot.PartyKey : groupLabel;

        var matchingSlot = runDetail.Slots.FirstOrDefault(slot =>
            slot.GroupKey.Equals(observed.Snapshot.PartyKey, StringComparison.OrdinalIgnoreCase) &&
            slot.PositionInGroup == observed.Member.Position);

        if (matchingSlot != null)
            return matchingSlot.SlotLabel;

        return string.IsNullOrWhiteSpace(groupLabel)
            ? $"{observed.Snapshot.PartyKey} {observed.Member.Position}"
            : $"{groupLabel} {observed.Member.Position}";
    }

    private void DrawPartySnapshotSlot(FullPartyRunDetail runDetail, FullPartyRosterSlot plannedSlot, FullPartyPartySnapshotMember? member)
    {
        if (member == null)
        {
            DrawEmptyRosterSlot(plannedSlot);
            return;
        }

        var resolvedSlot = ResolveRosterSlot(runDetail, member);
        var character = ResolveCharacter(runDetail, member);
        var canOpenApplication = runDetail.CanModerate && resolvedSlot?.ApplicationId != null;
        if (ImGui.InvisibleButton($"##party_snapshot_{plannedSlot.Id}_{member.Position}", new Vector2(-1f, 34f)) && canOpenApplication)
            plugin.OpenApplicationWindow(Run, resolvedSlot!);

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        var classJob = NormalizeClassJob(member.ClassJob);
        var role = GetRoleForClassJob(runDetail, classJob) ?? resolvedSlot?.CharacterClassRole;
        drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(GetFilledSlotBackground(role, hovered && canOpenApplication)), 3f);
        drawList.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, hovered ? 0.18f : 0.08f)), 3f);

        var cursor = min + new Vector2(5f, 5f);
        if (character != null)
        {
            DrawCharacterIcon(drawList, character, cursor);
        }
        else
        {
            drawList.AddRectFilled(cursor, cursor + new Vector2(24, 24), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.10f)), 3f);
        }

        cursor.X += 29f;

        var iconRight = max.X - 7f;
        Vector2? phantomIconPosition = null;
        Vector2? classIconPosition = null;
        if (!string.IsNullOrWhiteSpace(member.PhantomJob))
        {
            iconRight -= 20f;
            phantomIconPosition = new Vector2(iconRight, min.Y + 7f);
            iconRight -= 4f;
        }

        if (!string.IsNullOrWhiteSpace(classJob))
        {
            iconRight -= 20f;
            classIconPosition = new Vector2(iconRight, min.Y + 7f);
            iconRight -= 4f;
        }

        var textColor = ImGui.GetColorU32(ImGuiCol.Text);
        var displayName = character?.Name ?? member.DisplayName;
        if (resolvedSlot != null && IsValidationPartyLead(resolvedSlot))
            DrawPartyLeadCrown(drawList, ref cursor);

        var nameWidth = Math.Max(24f, iconRight - cursor.X);
        drawList.AddText(cursor + new Vector2(0, 3f), textColor, TrimToWidth(displayName, nameWidth));

        if (classIconPosition != null)
            DrawJobIcon(drawList, classJob, classIconPosition.Value);

        if (phantomIconPosition != null)
            DrawPhantomJobIconByName(drawList, runDetail, member.PhantomJob, phantomIconPosition.Value);
    }

    private void DrawRosterSlot(FullPartyRosterSlot slot, bool canModerate)
    {
        var character = slot.AssignedCharacter;
        if (character == null)
        {
            DrawEmptyRosterSlot(slot);
            return;
        }

        var canOpenApplication = canModerate && slot.ApplicationId != null;
        if (ImGui.InvisibleButton($"##slot_{slot.Id}", new Vector2(-1f, 34f)) && canOpenApplication)
        {
            plugin.OpenApplicationWindow(Run, slot);
        }

        var min = ImGui.GetItemRectMin();
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        var max = ImGui.GetItemRectMax();
        var bg = GetFilledSlotBackground(slot.CharacterClassRole, hovered && canOpenApplication);
        drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(bg), 3f);
        drawList.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, hovered ? 0.18f : 0.08f)), 3f);

        var textColor = ImGui.GetColorU32(ImGuiCol.Text);
        var mutedColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        var cursor = min + new Vector2(5f, 5f);
        var hasClassIcon = !string.IsNullOrWhiteSpace(slot.CharacterClass);
        var hasPhantomIcon = HasPhantomJob(slot);
        var iconRight = max.X - 7f;
        Vector2? classIconPosition = null;
        Vector2? phantomIconPosition = null;

        if (hasPhantomIcon)
        {
            iconRight -= 20f;
            phantomIconPosition = new Vector2(iconRight, min.Y + 7f);
            iconRight -= 4f;
        }

        if (hasClassIcon)
        {
            iconRight -= 20f;
            classIconPosition = new Vector2(iconRight, min.Y + 7f);
            iconRight -= 4f;
        }

        DrawCharacterIcon(drawList, character, cursor);
        cursor.X += 29f;

        if (IsValidationPartyLead(slot))
            DrawPartyLeadCrown(drawList, ref cursor);

        var nameWidth = Math.Max(24f, iconRight - cursor.X);
        drawList.AddText(cursor + new Vector2(0, 3f), textColor, TrimToWidth(character.Name, nameWidth));

        if (classIconPosition != null && !DrawJobIcon(drawList, slot.CharacterClass, classIconPosition.Value))
        {
            DrawIconFallback(drawList, classIconPosition.Value, slot.CharacterClass, mutedColor);
        }

        if (phantomIconPosition != null &&
            !DrawPhantomJobIcon(drawList, slot, phantomIconPosition.Value) &&
            string.IsNullOrWhiteSpace(slot.PhantomJobIconUrl))
        {
            DrawIconFallback(drawList, phantomIconPosition.Value, slot.PhantomJob, mutedColor);
        }
    }

    private void DrawCharacterIcon(ImDrawListPtr drawList, FullPartyRosterCharacter character, Vector2 position)
    {
        var path = plugin.ImageCache.GetImagePath(character.AvatarUrl, $"character-{character.Id}");
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var texture = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
            if (texture != null)
            {
                drawList.AddImage(texture.Handle, position, position + new Vector2(24, 24));
                return;
            }
        }

        drawList.AddRectFilled(position, position + new Vector2(24, 24), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.10f)), 3f);
    }

    private void DrawLiveMemberCharacter(FullPartyLiveMember member)
    {
        var avatarDrawn = false;
        var path = plugin.ImageCache.GetImagePath(member.AvatarUrl, $"live-member-{member.UserId}");
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var texture = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
            if (texture != null)
            {
                ImGui.Image(texture.Handle, new Vector2(24f, 24f));
                avatarDrawn = true;
            }
        }

        if (!avatarDrawn)
        {
            var cursor = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(24f, 24f));
            ImGui.GetWindowDrawList().AddRectFilled(cursor, cursor + new Vector2(24f, 24f), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.10f)), 3f);
        }

        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextUnformatted(member.DisplayName);
        if (!string.IsNullOrWhiteSpace(member.Location))
            ImGui.TextDisabled(member.Location);
        ImGui.EndGroup();
    }

    private static void DrawPartyLeadCrown(ImDrawListPtr drawList, ref Vector2 cursor)
    {
        var position = cursor + new Vector2(0f, 7f);
        var gold = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.78f, 0.22f, 0.98f));
        var darkGold = ImGui.ColorConvertFloat4ToU32(new Vector4(0.44f, 0.29f, 0.05f, 0.95f));
        var shadow = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.42f));
        var x = position.X;
        var y = position.Y;

        drawList.AddTriangleFilled(new Vector2(x + 1f, y + 8f), new Vector2(x + 3f, y + 1f), new Vector2(x + 6f, y + 8f), shadow);
        drawList.AddTriangleFilled(new Vector2(x + 5f, y + 8f), new Vector2(x + 8f, y), new Vector2(x + 11f, y + 8f), shadow);
        drawList.AddTriangleFilled(new Vector2(x + 10f, y + 8f), new Vector2(x + 13f, y + 1f), new Vector2(x + 15f, y + 8f), shadow);
        drawList.AddRectFilled(new Vector2(x + 1f, y + 8f), new Vector2(x + 15f, y + 12f), shadow, 1.5f);

        drawList.AddTriangleFilled(new Vector2(x, y + 8f), new Vector2(x + 2.5f, y + 1f), new Vector2(x + 5.5f, y + 8f), gold);
        drawList.AddTriangleFilled(new Vector2(x + 4.5f, y + 8f), new Vector2(x + 7.5f, y), new Vector2(x + 10.5f, y + 8f), gold);
        drawList.AddTriangleFilled(new Vector2(x + 9.5f, y + 8f), new Vector2(x + 12.5f, y + 1f), new Vector2(x + 15f, y + 8f), gold);
        drawList.AddRectFilled(new Vector2(x, y + 8f), new Vector2(x + 15f, y + 12f), gold, 1.5f);
        drawList.AddLine(new Vector2(x + 1.5f, y + 10.5f), new Vector2(x + 13.5f, y + 10.5f), darkGold, 1f);

        cursor.X += PartyLeadCrownWidth;
    }

    private static void DrawEmptyRosterSlot(FullPartyRosterSlot slot)
    {
        ImGui.Dummy(new Vector2(Math.Max(1f, ImGui.GetContentRegionAvail().X), 34f));
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.09f, 0.11f, 0.20f)), 3f);
        drawList.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.05f)), 3f);
        drawList.AddText(min + new Vector2(7f, 8f), ImGui.GetColorU32(ImGuiCol.TextDisabled), TrimToWidth(slot.SlotLabel, Math.Max(24f, max.X - min.X - 14f)));
    }

    private bool DrawPhantomJobIcon(ImDrawListPtr drawList, FullPartyRosterSlot slot, Vector2 position)
    {
        var iconUrls = slot.PhantomJobIconUrls.Count > 0
            ? slot.PhantomJobIconUrls
            : string.IsNullOrWhiteSpace(slot.PhantomJobIconUrl)
                ? Array.Empty<string>()
                : new[] { slot.PhantomJobIconUrl };

        for (var i = 0; i < iconUrls.Count; i++)
        {
            var path = plugin.ImageCache.GetImagePath(iconUrls[i], $"phantom-{slot.PhantomJobId?.ToString() ?? slot.PhantomJob ?? slot.Id.ToString()}-{i}");
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var texture = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
                if (texture != null)
                {
                    drawList.AddImage(texture.Handle, position, position + new Vector2(20, 20));
                    return true;
                }
            }
        }

        if (slot.PhantomJobIconId is > 0)
        {
            var gameIcon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup((uint)slot.PhantomJobIconId.Value)).GetWrapOrDefault();
            if (gameIcon != null)
            {
                drawList.AddImage(gameIcon.Handle, position, position + new Vector2(20, 20));
                return true;
            }
        }

        return false;
    }

    private static void DrawIconFallback(ImDrawListPtr drawList, Vector2 position, string? label, uint textColor)
    {
        drawList.AddRectFilled(position, position + new Vector2(20, 20), ImGui.ColorConvertFloat4ToU32(new Vector4(0.05f, 0.06f, 0.07f, 0.55f)), 3f);
        drawList.AddRect(position, position + new Vector2(20, 20), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.16f)), 3f);

        var fallback = string.IsNullOrWhiteSpace(label) ? "?" : label[..1].ToUpperInvariant();
        var textSize = ImGui.CalcTextSize(fallback);
        drawList.AddText(position + new Vector2((20f - textSize.X) * 0.5f, (20f - textSize.Y) * 0.5f), textColor, fallback);
    }

    private static bool DrawJobIcon(ImDrawListPtr drawList, string? classNameOrShorthand, Vector2 position)
    {
        var iconId = GetJobIconId(classNameOrShorthand);
        if (iconId == null)
            return false;

        var texture = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId.Value)).GetWrapOrDefault();
        if (texture == null)
            return false;

        drawList.AddImage(texture.Handle, position, position + new Vector2(20, 20));
        return true;
    }

    private bool DrawPhantomJobIconByName(ImDrawListPtr drawList, FullPartyRunDetail runDetail, string? phantomJob, Vector2 position)
    {
        var normalized = NormalizePhantomJob(phantomJob);
        if (normalized == null)
            return false;

        var sourceSlot = runDetail.Slots.FirstOrDefault(slot => NormalizePhantomJob(slot.PhantomJob) == normalized && HasPhantomJob(slot));
        return sourceSlot != null && DrawPhantomJobIcon(drawList, sourceSlot, position);
    }

    private static uint? GetJobIconId(string? classNameOrShorthand)
    {
        var classJob = NormalizeClassJob(classNameOrShorthand);
        if (classJob == null)
            return null;

        if (JobIconCache.TryGetValue(classJob, out var cached))
            return cached;

        var rowId = GetClassJobRowId(classJob);
        uint? iconId = rowId == null ? null : 62100u + rowId.Value;
        JobIconCache[classJob] = iconId;
        return iconId;
    }

    private static string? GetExpectedClassJob(FullPartyRosterSlot slot)
    {
        return NormalizeClassJob(slot.CharacterClass);
    }

    private static string? NormalizeClassJob(string? classNameOrShorthand)
    {
        if (string.IsNullOrWhiteSpace(classNameOrShorthand))
            return null;

        var token = new string(classNameOrShorthand
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

        return token switch
        {
            "GLA" or "GLADIATOR" => "GLA",
            "PGL" or "PUGILIST" => "PGL",
            "MRD" or "MARAUDER" => "MRD",
            "LNC" or "LANCER" => "LNC",
            "ARC" or "ARCHER" => "ARC",
            "CNJ" or "CONJURER" => "CNJ",
            "THM" or "THAUMATURGE" => "THM",
            "PLD" or "PALADIN" => "PLD",
            "MNK" or "MONK" => "MNK",
            "WAR" or "WARRIOR" => "WAR",
            "DRG" or "DRAGOON" => "DRG",
            "BRD" or "BARD" => "BRD",
            "WHM" or "WHITEMAGE" => "WHM",
            "BLM" or "BLACKMAGE" => "BLM",
            "ACN" or "ARCANIST" => "ACN",
            "SMN" or "SUMMONER" => "SMN",
            "SCH" or "SCHOLAR" => "SCH",
            "ROG" or "ROGUE" => "ROG",
            "NIN" or "NINJA" => "NIN",
            "MCH" or "MACHINIST" => "MCH",
            "DRK" or "DARKKNIGHT" => "DRK",
            "AST" or "ASTROLOGIAN" => "AST",
            "SAM" or "SAMURAI" => "SAM",
            "RDM" or "REDMAGE" => "RDM",
            "BLU" or "BLUEMAGE" => "BLU",
            "GNB" or "GUNBREAKER" => "GNB",
            "DNC" or "DANCER" => "DNC",
            "RPR" or "REAPER" => "RPR",
            "SGE" or "SAGE" => "SGE",
            "VPR" or "VIPER" => "VPR",
            "PCT" or "PICTOMANCER" => "PCT",
            _ => null,
        };
    }

    private static uint? GetClassJobRowId(string classJob)
    {
        return classJob switch
        {
            "GLA" => 1,
            "PGL" => 2,
            "MRD" => 3,
            "LNC" => 4,
            "ARC" => 5,
            "CNJ" => 6,
            "THM" => 7,
            "PLD" => 19,
            "MNK" => 20,
            "WAR" => 21,
            "DRG" => 22,
            "BRD" => 23,
            "WHM" => 24,
            "BLM" => 25,
            "ACN" => 26,
            "SMN" => 27,
            "SCH" => 28,
            "ROG" => 29,
            "NIN" => 30,
            "MCH" => 31,
            "DRK" => 32,
            "AST" => 33,
            "SAM" => 34,
            "RDM" => 35,
            "BLU" => 36,
            "GNB" => 37,
            "DNC" => 38,
            "RPR" => 39,
            "SGE" => 40,
            "VPR" => 41,
            "PCT" => 42,
            _ => null,
        };
    }

    private static string GetPhantomJobDisplayName(FullPartyRunDetail runDetail, string? phantomJob)
    {
        var normalized = NormalizePhantomJob(phantomJob);
        if (normalized == null)
            return "unknown";

        return runDetail.Slots.FirstOrDefault(slot => NormalizePhantomJob(slot.PhantomJob) == normalized && !string.IsNullOrWhiteSpace(slot.PhantomJob))
                   ?.PhantomJob ??
               phantomJob ??
               "unknown";
    }

    private static string? NormalizePhantomJob(string? phantomJob)
    {
        if (string.IsNullOrWhiteSpace(phantomJob))
            return null;

        var token = new string(phantomJob
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

        return token.StartsWith("PHANTOM", StringComparison.Ordinal)
            ? token["PHANTOM".Length..]
            : token;
    }

    private static IReadOnlyList<IReadOnlyList<FullPartyRosterSlot>> GetRosterParties(FullPartyRunDetail runDetail)
    {
        return runDetail.Slots
            .Where(slot => !IsBenchSlot(slot))
            .GroupBy(slot => slot.GroupKey)
            .OrderBy(group => group.Min(slot => slot.SortOrder ?? int.MaxValue))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(slot => slot.PositionInGroup ?? int.MaxValue)
                .ThenBy(slot => slot.SortOrder ?? int.MaxValue)
                .ThenBy(slot => slot.SlotKey, StringComparer.OrdinalIgnoreCase)
                .ToList())
            .ToList();
    }

    private static IReadOnlyList<FullPartyRosterSlot> GetBenchSlots(FullPartyRunDetail runDetail)
    {
        return runDetail.Slots
            .Where(IsBenchSlot)
            .OrderBy(slot => slot.PositionInGroup ?? int.MaxValue)
            .ThenBy(slot => slot.SortOrder ?? int.MaxValue)
            .ThenBy(slot => slot.SlotKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static FullPartyRosterSlot? ResolveRosterSlot(FullPartyRunDetail runDetail, FullPartyPartySnapshotMember member)
    {
        if (member.CharacterId != null)
            return runDetail.Slots.FirstOrDefault(slot => slot.AssignedCharacter?.Id == member.CharacterId.Value);

        var key = GetCharacterKey(member.Name, member.World);
        return runDetail.Slots.FirstOrDefault(slot => slot.AssignedCharacter != null &&
                                                      GetCharacterKey(slot.AssignedCharacter.Name, slot.AssignedCharacter.World)
                                                          .Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private static FullPartyRosterCharacter? ResolveCharacter(FullPartyRunDetail runDetail, FullPartyPartySnapshotMember member)
    {
        return ResolveRosterSlot(runDetail, member)?.AssignedCharacter;
    }

    private static string? GetRoleForClassJob(FullPartyRunDetail runDetail, string? classJob)
    {
        classJob = NormalizeClassJob(classJob);
        if (classJob == null)
            return null;

        return runDetail.Slots
            .FirstOrDefault(slot => NormalizeClassJob(slot.CharacterClass) == classJob && !string.IsNullOrWhiteSpace(slot.CharacterClassRole))
            ?.CharacterClassRole;
    }

    private ComputedPartySnapshots BuildComputedPartySnapshots(
        FullPartyRunDetail runDetail,
        IReadOnlyList<IReadOnlyList<FullPartyRosterSlot>> parties,
        bool requestOccultRefresh)
    {
        var inOccult = OccultCrescentTerritory.IsCurrent();
        ObserveOccultState(inOccult);
        if (inOccult && requestOccultRefresh && !plugin.AdventurerList.HasRequestedRefresh)
            plugin.AdventurerList.RequestRefresh();

        var occultPresence = inOccult
            ? plugin.AdventurerList.GetPresence(runDetail)
            : GamePresenceList.Empty;
        var sourceSnapshots = inOccult
            ? BuildOccultSourceSnapshots(runDetail)
            : RunValidationSources.BuildLocalPartySnapshots(runDetail, parties);
        var occultPartyAssignments = inOccult
            ? BuildOccultPartyAssignments(runDetail, sourceSnapshots, occultPresence)
            : new Dictionary<string, OccultPartyAssignment>(StringComparer.OrdinalIgnoreCase);
        var snapshotsByParty = inOccult
            ? occultPartyAssignments.ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot, StringComparer.OrdinalIgnoreCase)
            : sourceSnapshots.ToDictionary(snapshot => snapshot.PartyKey, StringComparer.OrdinalIgnoreCase);
        var computedSnapshots = snapshotsByParty.Values
            .GroupBy(snapshot => $"{snapshot.SenderUserId}:{snapshot.PartyKey}:{snapshot.Sequence}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        return new ComputedPartySnapshots(
            snapshotsByParty,
            computedSnapshots,
            occultPresence,
            occultPartyAssignments.Count,
            inOccult);
    }

    private void ObserveOccultState(bool inOccult)
    {
        if (lastOccultState == inOccult)
            return;

        if (inOccult)
        {
            liveRoom.ClearPartySnapshots("Entered Occult Crescent; discarded pre-Occult party sync.");
        }

        lastOccultState = inOccult;
    }

    private IReadOnlyList<FullPartyPartySnapshot> BuildOccultSourceSnapshots(FullPartyRunDetail runDetail)
    {
        var snapshots = liveRoom.PartySnapshots.ToList();
        var currentPartySnapshot = RunValidationSources.BuildCurrentPartySnapshot(runDetail);
        if (currentPartySnapshot != null)
            snapshots.Add(currentPartySnapshot);

        return snapshots;
    }

    private static IReadOnlyDictionary<string, OccultPartyAssignment> BuildOccultPartyAssignments(
        FullPartyRunDetail runDetail,
        IReadOnlyList<FullPartyPartySnapshot> snapshots,
        GamePresenceList presence)
    {
        var assignments = new Dictionary<string, OccultPartyAssignment>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            var isLocalSnapshot = snapshot.SenderUserId == 0;
            var leadSlots = new List<FullPartyRosterSlot>();
            foreach (var member in snapshot.Members)
            {
                var slot = ResolveRosterSlot(runDetail, member);
                if (slot?.AssignedCharacter == null || !IsValidationPartyLead(slot))
                    continue;

                if (!isLocalSnapshot && !presence.TryFind(slot.AssignedCharacter, out _))
                    continue;

                leadSlots.Add(slot);
            }

            var leadGroups = leadSlots
                .GroupBy(slot => slot.GroupKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    GroupKey = group.Key,
                    GroupLabel = group.First().GroupLabel,
                    PartyLeadCount = group.Count(),
                    MatchesSnapshotKey = group.Key.Equals(snapshot.PartyKey, StringComparison.OrdinalIgnoreCase),
                })
                .OrderByDescending(group => group.PartyLeadCount)
                .ThenByDescending(group => group.MatchesSnapshotKey)
                .ThenBy(group => group.GroupKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (leadGroups.Count == 0)
                continue;

            var selectedGroup = leadGroups[0];
            var isAmbiguousTie = leadGroups.Count > 1 &&
                                 leadGroups[1].PartyLeadCount == selectedGroup.PartyLeadCount &&
                                 leadGroups[1].MatchesSnapshotKey == selectedGroup.MatchesSnapshotKey;
            if (isAmbiguousTie)
                continue;

            if (!assignments.TryGetValue(selectedGroup.GroupKey, out var existing) ||
                selectedGroup.PartyLeadCount > existing.PartyLeadCount ||
                (selectedGroup.PartyLeadCount == existing.PartyLeadCount && snapshot.CapturedAt > existing.Snapshot.CapturedAt))
            {
                assignments[selectedGroup.GroupKey] = new OccultPartyAssignment(
                    snapshot with { PartyKey = selectedGroup.GroupKey },
                    selectedGroup.GroupLabel,
                    selectedGroup.PartyLeadCount);
            }
        }

        return assignments;
    }

    private static IReadOnlyDictionary<long, ObservedSnapshotMember> BuildObservedByCharacterId(IReadOnlyList<FullPartyPartySnapshot> snapshots)
    {
        var observed = new Dictionary<long, ObservedSnapshotMember>();
        foreach (var snapshot in snapshots)
        {
            foreach (var member in snapshot.Members)
            {
                if (member.CharacterId != null)
                    observed[member.CharacterId.Value] = new ObservedSnapshotMember(snapshot, member);
            }
        }

        return observed;
    }

    private static IReadOnlyDictionary<string, ObservedSnapshotMember> BuildObservedByName(
        FullPartyRunDetail runDetail,
        IReadOnlyList<FullPartyPartySnapshot> snapshots)
    {
        var observed = new Dictionary<string, ObservedSnapshotMember>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            foreach (var member in snapshot.Members)
            {
                var key = member.CharacterId == null
                    ? GetCharacterKey(member.Name, member.World)
                    : GetCharacterKey(ResolveCharacter(runDetail, member)?.Name, ResolveCharacter(runDetail, member)?.World);

                if (!string.IsNullOrWhiteSpace(key))
                    observed[key] = new ObservedSnapshotMember(snapshot, member);
            }
        }

        return observed;
    }

    private static ObservedSnapshotMember? FindObservedForSlot(
        FullPartyRosterSlot slot,
        IReadOnlyDictionary<long, ObservedSnapshotMember> observedById,
        IReadOnlyDictionary<string, ObservedSnapshotMember> observedByName)
    {
        if (slot.AssignedCharacter == null)
            return null;

        if (observedById.TryGetValue(slot.AssignedCharacter.Id, out var observed))
            return observed;

        return observedByName.TryGetValue(GetCharacterKey(slot.AssignedCharacter.Name, slot.AssignedCharacter.World), out observed)
            ? observed
            : null;
    }

    private static FullPartyPartySnapshotMember? FindExpectedMemberInParty(
        FullPartyRunDetail runDetail,
        FullPartyRosterSlot slot,
        FullPartyPartySnapshot? snapshot)
    {
        if (snapshot == null || slot.AssignedCharacter == null)
            return null;

        return snapshot.Members.FirstOrDefault(member =>
            IsExpectedCharacter(slot, member, ResolveRosterSlot(runDetail, member)));
    }

    private static ValidationState GetValidationState(int? expected, int? actual)
    {
        if (expected == null)
            return ValidationState.Neutral;

        if (actual == null)
            return ValidationState.Warning;

        return expected == actual ? ValidationState.Ok : ValidationState.Error;
    }

    private static string GetValidationText(ValidationState state)
    {
        return state switch
        {
            ValidationState.Ok => "OK",
            ValidationState.Warning => "Unknown",
            ValidationState.Error => "Wrong",
            _ => "N/A",
        };
    }

    private static void DrawValidationStatus(string text, ValidationState state)
    {
        var color = state switch
        {
            ValidationState.Ok => new Vector4(0.36f, 0.92f, 0.55f, 1f),
            ValidationState.Warning => new Vector4(0.94f, 0.78f, 0.32f, 1f),
            ValidationState.Error => new Vector4(1f, 0.42f, 0.42f, 1f),
            _ => new Vector4(0.66f, 0.66f, 0.72f, 1f),
        };

        ImGui.TextColored(color, text);
    }

    private static Vector4 GetValidationSlotBackground(ValidationState state, bool hovered)
    {
        var alpha = hovered ? 0.70f : 0.52f;
        return state switch
        {
            ValidationState.Ok => new Vector4(0.08f, 0.38f, 0.18f, alpha),
            ValidationState.Warning => new Vector4(0.58f, 0.43f, 0.06f, alpha),
            ValidationState.Error => new Vector4(0.66f, 0.27f, 0.04f, alpha),
            _ => new Vector4(0.10f, 0.12f, 0.15f, 0.28f),
        };
    }

    private static string GetCharacterKey(string? name, string? world)
    {
        return $"{NormalizeKeyPart(name)}@{NormalizeKeyPart(world)}";
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

    private static bool IsValidationPartyLead(FullPartyRosterSlot slot)
    {
        return slot.IsRaidLeader || slot.IsHost;
    }

    private static bool HasPhantomJob(FullPartyRosterSlot slot)
    {
        return slot.PhantomJobId != null ||
               !string.IsNullOrWhiteSpace(slot.PhantomJob) ||
               !string.IsNullOrWhiteSpace(slot.PhantomJobIconUrl) ||
               slot.PhantomJobIconUrls.Count > 0 ||
               slot.PhantomJobIconId is > 0;
    }

    private static string GetLiveMemberRole(FullPartyLiveMember member)
    {
        if (member.IsHost)
            return "Host";

        if (member.IsPartyLead)
            return "Party lead";

        return "Connected";
    }

    private static Vector4 GetLiveCommandStatusColor(string status)
    {
        return status switch
        {
            "Executed" => new Vector4(0.35f, 0.92f, 0.55f, 1f),
            "Received" => new Vector4(0.42f, 0.72f, 1f, 1f),
            "Waiting" => new Vector4(0.94f, 0.78f, 0.32f, 1f),
            "Failed" or "Expired" => new Vector4(1f, 0.42f, 0.42f, 1f),
            "Disabled" => new Vector4(1f, 0.62f, 0.32f, 1f),
            "Received, not target" => new Vector4(0.62f, 0.72f, 0.92f, 1f),
            "Not targeted" or "-" => new Vector4(0.65f, 0.65f, 0.70f, 1f),
            _ => new Vector4(0.80f, 0.80f, 0.86f, 1f),
        };
    }

    private static Vector4 GetFilledSlotBackground(string? role, bool hovered)
    {
        var alpha = hovered ? 0.62f : 0.42f;
        if (role?.Contains("tank", StringComparison.OrdinalIgnoreCase) == true)
            return new Vector4(0.10f, 0.24f, 0.46f, alpha);

        if (role?.Contains("heal", StringComparison.OrdinalIgnoreCase) == true)
            return new Vector4(0.10f, 0.34f, 0.18f, alpha);

        if (role?.Contains("dps", StringComparison.OrdinalIgnoreCase) == true ||
            role?.Contains("melee", StringComparison.OrdinalIgnoreCase) == true ||
            role?.Contains("ranged", StringComparison.OrdinalIgnoreCase) == true ||
            role?.Contains("caster", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new Vector4(0.42f, 0.12f, 0.12f, alpha);
        }

        return hovered
            ? new Vector4(0.25f, 0.30f, 0.36f, 0.38f)
            : new Vector4(0.10f, 0.12f, 0.15f, 0.28f);
    }

    private static string TrimToWidth(string value, float maxWidth)
    {
        if (string.IsNullOrEmpty(value) || ImGui.CalcTextSize(value).X <= maxWidth)
            return value;

        const string ellipsis = "...";
        var trimmed = value;
        while (trimmed.Length > 0 && ImGui.CalcTextSize(trimmed + ellipsis).X > maxWidth)
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Length == 0 ? ellipsis : trimmed + ellipsis;
    }

    private void RefreshRun()
    {
        detail = null;
        detailError = null;
        detailTask = plugin.ApiClient.GetRunDetailAsync(Run.Id, cancellation.Token);
    }

    private void StartRunCheckIn()
    {
        if (detail == null)
        {
            checkInStatusMessage = "Run check-in unavailable: roster is not loaded yet.";
            return;
        }

        if (OccultCrescentTerritory.IsCurrent() && !plugin.AdventurerList.HasRequestedRefresh)
        {
            plugin.AdventurerList.RequestRefresh();
            checkInStatusMessage = "Refreshing Adventurer List before check-in. Press Run Check-In again once it finishes.";
            return;
        }

        var selection = BuildRunCheckInSelection(detail);
        if (selection.PresentCount == 0)
        {
            checkInStatusMessage = $"Run check-in skipped: no present roster players found, {selection.MissingCount} missing.";
            Plugin.Log.Information(
                "FullParty run {RunId} check-in skipped: no present roster players found, {MissingCount} missing.",
                Run.Id,
                selection.MissingCount);
            return;
        }

        checkInStatusMessage = $"Submitting check-in for {selection.PresentCount} present roster players...";
        checkInTask = SubmitRunCheckInAsync(selection, cancellation.Token);
    }

    private async Task<RunCheckInSummary> SubmitRunCheckInAsync(RunCheckInSelection selection, CancellationToken cancellationToken)
    {
        await plugin.ApiClient.SubmitRunCheckInsAsync(Run.Id, selection.SlotIds, selection.CharacterIds, cancellationToken);
        return new RunCheckInSummary(selection.PresentCount, selection.MissingCount);
    }

    private void ObserveCheckInTask()
    {
        var task = checkInTask;
        if (task is not { IsCompleted: true })
            return;

        checkInTask = null;
        if (task.IsCanceled)
        {
            checkInStatusMessage = "Run check-in cancelled.";
            return;
        }

        if (task.Exception != null)
        {
            var exception = task.Exception.GetBaseException();
            Plugin.Log.Warning(exception, "FullParty run {RunId} check-in failed.", Run.Id);
            checkInStatusMessage = $"Run check-in failed: {exception.Message}";
            return;
        }

        var summary = task.Result;
        checkInStatusMessage = $"Run check-in complete: {summary.CheckedInCount} checked in, {summary.MissingCount} missing.";
        Plugin.Log.Information(
            "FullParty run {RunId} check-in complete: {CheckedInCount} checked in, {MissingCount} missing.",
            Run.Id,
            summary.CheckedInCount,
            summary.MissingCount);
        RefreshRun();
    }

    private RunCheckInSelection BuildRunCheckInSelection(FullPartyRunDetail runDetail)
    {
        var presence = OccultCrescentTerritory.IsCurrent()
            ? plugin.AdventurerList.GetPresence(runDetail)
            : RunValidationSources.BuildLocalPartyPresence(runDetail);
        var assignedSlots = runDetail.Slots
            .Where(slot => slot.AssignedCharacter != null)
            .GroupBy(slot => slot.Id)
            .Select(group => group.First())
            .ToList();
        var presentSlots = assignedSlots
            .Where(slot => presence.TryFind(slot.AssignedCharacter!, out _))
            .ToList();

        return new RunCheckInSelection(
            presentSlots.Select(slot => slot.Id).Distinct().ToList(),
            presentSlots.Select(slot => slot.AssignedCharacter!.Id).Distinct().ToList(),
            presentSlots.Count,
            Math.Max(0, assignedSlots.Count - presentSlots.Count));
    }

    private void RefreshRosterData()
    {
        RefreshRun();

        if (OccultCrescentTerritory.IsCurrent())
            plugin.AdventurerList.RequestRefresh();
    }

    private void EnsureDetailLoaded()
    {
        if (detail != null || detailError != null)
            return;

        if (detailTask == null)
        {
            detailTask = plugin.ApiClient.GetRunDetailAsync(Run.Id, cancellation.Token);
            return;
        }

        if (!detailTask.IsCompleted)
            return;

        if (detailTask.IsCompletedSuccessfully)
        {
            detail = detailTask.Result;
            if (detail == null)
            {
                detailError = "FullParty returned an empty run.";
            }
            else
            {
                liveRoom.SetRunDetail(detail);
            }
        }
        else
        {
            detailError = "Could not load this run yet.";
            Plugin.Log.Warning(detailTask.Exception, "Could not load FullParty run {RunId}", Run.Id);
        }

        detailTask = null;
    }
}
