using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using ElezenTools.UI;
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

internal enum ReadyCheckGlyph
{
    Check,
    Cross,
    Clock,
}

internal enum RosterDataMode
{
    Off,
    Validate,
    Only,
}

internal enum RosterLayoutMode
{
    Normal,
    SplitThreeByThree,
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
    private static IReadOnlyDictionary<string, string>? phantomJobIconFileCache;
    private const float RunWindowDefaultWidth = 680f;
    private const float RunWindowDefaultHeight = 860f;
    private const float RunWindowMinWidth = 500f;
    private const float RunWindowMinHeight = 560f;
    private const float RosterCompanionDefaultWidth = 980f;
    private const float RosterCompanionDefaultHeight = 560f;
    private const float RosterCompanionMinWidth = 620f;
    private const float RosterCompanionMinHeight = 320f;
    private const float PartyLeadCrownWidth = 16f;
    private const float RosterCompanionGap = 8f;

    private readonly Plugin plugin;
    private readonly CancellationTokenSource cancellation = new();
    private readonly RealtimeRunRoomClient liveRoom;
    private readonly RunMiniWindow miniWindow;
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
    private GamePresenceList nearbyDetectionCache = GamePresenceList.Empty;
    private string? readyCheckPromptPopupRequestId;
    private bool windowStylePushed;
    private Vector2 runContentClipMin;
    private Vector2 runContentClipMax;
    private string rosterSearch = string.Empty;
    private RosterLayoutMode rosterLayoutMode;
    private RosterDataMode rosterDataMode = RosterDataMode.Off;
    private bool rosterShowEmptySlots = true;

    public FullPartyRun Run { get; }

    public RunWindow(FullPartyRun run, Plugin plugin)
        : base($"{run.Name} - {run.StartsAt:MMM d, yyyy} - {run.StartsAt:HH:mm}##FullPartyRun{run.Id}")
    {
        Run = run;
        this.plugin = plugin;
        liveRoom = plugin.LiveRoomManager.GetOrCreate(run);
        miniWindow = new RunMiniWindow(this, run);
        rosterViewMode = plugin.Configuration.RosterHiddenByDefault
            ? RunRosterViewMode.None
            : RunRosterViewMode.Roster;
        plugin.WindowSystem.AddWindow(miniWindow);
        ApplySizeConstraints();
        Size = new Vector2(RunWindowDefaultWidth, RunWindowDefaultHeight);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = true;
    }

    public void Dispose()
    {
        miniWindow.IsOpen = false;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    public override void OnClose()
    {
    }

    public override void PreDraw()
    {
        ModernWindowStyle.PushTitleBar();
        windowStylePushed = true;
        ApplySizeConstraints();
        base.PreDraw();
    }

    public override void PostDraw()
    {
        base.PostDraw();
        if (!windowStylePushed)
            return;

        ModernWindowStyle.PopTitleBar();
        windowStylePushed = false;
    }

    public override void Draw()
    {
        using var palette = ModernWindowStyle.PushContentPalette();
        EnsureDetailLoaded();
        ObserveCheckInTask();

        var runWindowPosition = ImGui.GetWindowPos();
        var runWindowSize = ImGui.GetWindowSize();
        UpdateCurrentWindowClipBounds(runWindowPosition, runWindowSize);
        var isLoading = detailTask is { IsCompleted: false };

        DrawRunStatusStrip();
        ImGui.Spacing();

        DrawPartyActionsSection(detail?.CanModerate == true, true);
        ImGui.Spacing();
        DrawRosterControls(isLoading);

        ImGui.Spacing();

        DrawLiveRoom();
        DrawReadyCheckConfirmationPopup();
        DrawRosterCompanion(runWindowPosition, runWindowSize, isLoading);
    }

    internal void DrawMiniContent()
    {
        using var palette = ModernWindowStyle.PushContentPalette();
        EnsureDetailLoaded();
        ObserveCheckInTask();

        var position = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        UpdateCurrentWindowClipBounds(position, size);

        DrawMiniPartyActions(detail?.CanModerate == true);
        ImGui.Spacing();
        DrawMiniLiveMembersSection();
        DrawReadyCheckConfirmationPopup();
    }

    private void DrawMiniPartyActions(bool canModerate)
    {
        BeginRunPanel("mini_party_actions", FontAwesomeIcon.Users, "Party Actions", 148f);

        var width = ImGui.GetContentRegionAvail().X;
        var gap = ImGui.GetStyle().ItemSpacing.X;
        var buttonWidth = (width - gap) * 0.5f;
        var canSendLiveCommand = canModerate && liveRoom.State == RealtimeRunRoomState.Connected && !liveRoom.IsIssuingCommand;
        if (!canSendLiveCommand)
            ImGui.BeginDisabled();

        if (DrawRunActionButton(FontAwesomeIcon.Search, "Leads", buttonWidth, true))
            liveRoom.SendReadyCheckLeads();

        ImGui.SameLine();
        if (DrawRunActionButton(FontAwesomeIcon.Users, "Parties", buttonWidth, true))
            liveRoom.SendReadyCheckParty();

        ImGui.Spacing();
        if (DrawRunActionButton(FontAwesomeIcon.HourglassHalf, "Countdown", buttonWidth, true))
            liveRoom.SendCountdown(20);

        if (!canSendLiveCommand)
            ImGui.EndDisabled();

        EndRunPanel();
    }

    private void DrawMiniLiveMembersSection()
    {
        var members = liveRoom.Members;
        var panelHeight = MathF.Max(142f, 76f + (members.Count * 42f));
        BeginRunPanel("mini_live_members", FontAwesomeIcon.Users, $"Members ({members.Count})", panelHeight);

        if (members.Count == 0)
        {
            ImGui.TextDisabled(liveRoom.State == RealtimeRunRoomState.Connected
                ? "No members yet."
                : "Disconnected.");
            EndRunPanel();
            return;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(4f, 6f));
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, FullPartyModernPalette.BrandSoft with { W = 0.96f });
        ImGui.PushStyleColor(ImGuiCol.TableRowBg, FullPartyModernPalette.Elevated with { W = 0.30f });
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, FullPartyModernPalette.BrandSoft with { W = 0.18f });
        ImGui.PushStyleColor(ImGuiCol.TableBorderStrong, FullPartyModernPalette.BorderSoft);
        ImGui.PushStyleColor(ImGuiCol.TableBorderLight, FullPartyModernPalette.Border);

        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##fullparty_mini_live_members", 3, flags))
        {
            ImGui.PopStyleColor(5);
            ImGui.PopStyleVar();
            EndRunPanel();
            return;
        }

        ImGui.TableSetupColumn("Profile", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn("Party", ImGuiTableColumnFlags.WidthStretch, 0.65f);
        ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthStretch, 1f);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers, 30f);
        foreach (var label in new[] { "PROFILE", "PTY", "CMD" })
        {
            ImGui.TableNextColumn();
            ImGui.TextColored(FullPartyModernPalette.Brand with { X = 0.76f, Y = 0.58f, Z = 0.96f }, label);
        }

        foreach (var member in members)
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, 36f);

            ImGui.TableNextColumn();
            DrawMiniLiveMemberProfile(member);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(TrimToWidth(liveRoom.GetSyncedPartyLabel(member), ImGui.GetContentRegionAvail().X));

            ImGui.TableNextColumn();
            var commandText = liveRoom.TryGetReadyCheckSummary(member, out var summary)
                ? $"{summary.Ready}/{summary.Total}"
                : liveRoom.GetCommandStatus(member);
            ImGui.TextColored(
                GetLiveCommandStatusColor(commandText),
                TrimToWidth(commandText, ImGui.GetContentRegionAvail().X));
        }

        ImGui.EndTable();
        ImGui.PopStyleColor(5);
        ImGui.PopStyleVar();
        EndRunPanel();
    }

    private void DrawMiniLiveMemberProfile(FullPartyLiveMember member)
    {
        const float avatarSize = 20f;
        var avatarPosition = ImGui.GetCursorScreenPos();
        var avatarDrawn = false;
        var path = plugin.ImageCache.GetImagePath(member.AvatarUrl, $"live-member-{member.UserId}");
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var texture = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
            if (texture != null)
            {
                ImGui.Dummy(new Vector2(avatarSize, avatarSize));
                ImGui.GetWindowDrawList().AddImageRounded(
                    texture.Handle,
                    avatarPosition,
                    avatarPosition + new Vector2(avatarSize, avatarSize),
                    Vector2.Zero,
                    Vector2.One,
                    ImGui.GetColorU32(ImGuiCol.Text),
                    avatarSize * 0.5f);
                avatarDrawn = true;
            }
        }

        if (!avatarDrawn)
        {
            ImGui.Dummy(new Vector2(avatarSize, avatarSize));
            ImGui.GetWindowDrawList().AddCircleFilled(
                avatarPosition + new Vector2(avatarSize * 0.5f),
                avatarSize * 0.5f,
                FullPartyModernPalette.Color(FullPartyModernPalette.Elevated));
        }

        ImGui.SameLine(0f, 4f);
        ImGui.TextUnformatted(TrimToWidth(member.DisplayName, MathF.Max(20f, ImGui.GetContentRegionAvail().X)));
    }

    private void UpdateCurrentWindowClipBounds(Vector2 windowPosition, Vector2 windowSize)
    {
        runContentClipMin = new Vector2(
            windowPosition.X,
            windowPosition.Y + ImGui.GetFrameHeight());
        runContentClipMax = windowPosition + windowSize;
    }

    private void DrawRunStatusStrip()
    {
        const float height = 82f;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var end = start + new Vector2(width, height);
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(start, end, FullPartyModernPalette.Color(FullPartyModernPalette.Surface with { W = 0.94f }), 7f);
        drawList.AddRect(start, end, FullPartyModernPalette.Color(FullPartyModernPalette.BorderSoft), 7f);
        DrawPanelDecoration(drawList, start, end);

        var statusColor = GetLiveRoomStatusColor();
        var statusCenter = start + new Vector2(34f, height * 0.5f);
        drawList.AddCircleFilled(statusCenter, 8f, FullPartyModernPalette.Color(statusColor));
        drawList.AddCircle(statusCenter, 17f, FullPartyModernPalette.Color(statusColor with { W = 0.20f }), 24, 2f);

        var connected = liveRoom.State == RealtimeRunRoomState.Connected;
        drawList.AddText(start + new Vector2(58f, 22f), FullPartyModernPalette.Color(FullPartyModernPalette.Text), connected ? "Connected" : liveRoom.StatusMessage);
        drawList.AddText(start + new Vector2(58f, 45f), FullPartyModernPalette.Color(FullPartyModernPalette.Muted), "Live Room");

        var firstDividerX = start.X + (width * 0.31f);
        var secondDividerX = start.X + (width * 0.52f);
        DrawVerticalPanelDivider(drawList, firstDividerX, start.Y + 18f, end.Y - 18f);
        DrawVerticalPanelDivider(drawList, secondDividerX, start.Y + 18f, end.Y - 18f);

        drawList.AddText(new Vector2(firstDividerX + 28f, start.Y + 22f), FullPartyModernPalette.Color(FullPartyModernPalette.Text), DateTime.Now.ToString("HH:mm"));
        drawList.AddText(new Vector2(firstDividerX + 28f, start.Y + 45f), FullPartyModernPalette.Color(FullPartyModernPalette.Muted), "Local Time");

        var user = plugin.AuthService.User;
        var avatarPosition = new Vector2(secondDividerX + 24f, start.Y + 18f);
        DrawStatusAvatar(drawList, user?.AvatarUrl, user?.Id ?? 0, avatarPosition, 46f);
        drawList.AddText(avatarPosition + new Vector2(58f, 6f), FullPartyModernPalette.Color(FullPartyModernPalette.Text), user?.Name ?? "FullParty user");
        drawList.AddText(avatarPosition + new Vector2(58f, 28f), FullPartyModernPalette.Color(statusColor), connected ? "Connected" : "Offline");

        ImGui.SetCursorScreenPos(start);
        ImGui.InvisibleButton("##fullparty_run_status_strip", new Vector2(width, height));
    }

    private void ApplySizeConstraints()
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(RunWindowMinWidth, RunWindowMinHeight),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    private void DrawPartyActionsSection(bool canModerate, bool showMiniWindowButton)
    {
        var panelHeight = showMiniWindowButton ? 212f : 158f;
        if (!string.IsNullOrWhiteSpace(checkInStatusMessage))
            panelHeight += 28f;
        if (liveRoom.ReadyCheckConfirmationPrompt != null)
            panelHeight += 112f;

        BeginRunPanel("party_actions", FontAwesomeIcon.Users, "Party Actions", panelHeight);

        var canSendLiveCommand = canModerate && liveRoom.State == RealtimeRunRoomState.Connected && !liveRoom.IsIssuingCommand;
        if (!canSendLiveCommand)
            ImGui.BeginDisabled();

        var readyLeadsLabel = "1. Check Leads";
        var readyPartyLabel = "2. Check Parties";
        const string countdownLabel = "Start Countdown";

        var availableWidth = ImGui.GetContentRegionAvail().X;
        var gap = ImGui.GetStyle().ItemSpacing.X;
        var primaryWidth = (availableWidth - gap) * 0.5f;

        if (DrawRunActionButton(FontAwesomeIcon.Search, readyLeadsLabel, primaryWidth, true))
            liveRoom.SendReadyCheckLeads();

        ImGui.SameLine();

        if (DrawRunActionButton(FontAwesomeIcon.Users, readyPartyLabel, primaryWidth, true))
            liveRoom.SendReadyCheckParty();

        ImGui.Spacing();

        var secondaryWidth = (availableWidth - gap) * 0.5f;
        if (DrawRunActionButton(FontAwesomeIcon.HourglassHalf, countdownLabel, secondaryWidth, true))
            liveRoom.SendCountdown(20);

        if (!canSendLiveCommand)
            ImGui.EndDisabled();

        var isCheckingIn = checkInTask is { IsCompleted: false };
        var waitingForAdventurerList = OccultCrescentTerritory.IsCurrent() && plugin.AdventurerList.IsRefreshing;
        var canCheckIn = detail != null && !isCheckingIn && !waitingForAdventurerList;
        var checkInLabel = isCheckingIn
            ? "Checking In..."
            : "Run Check-In";

        ImGui.SameLine();

        if (!canCheckIn)
            ImGui.BeginDisabled();

        if (DrawRunActionButton(FontAwesomeIcon.ClipboardCheck, checkInLabel, secondaryWidth, true))
            StartRunCheckIn();

        if (!canCheckIn)
            ImGui.EndDisabled();

        if (showMiniWindowButton)
        {
            ImGui.Spacing();

            if (DrawRunActionButton(FontAwesomeIcon.ExternalLinkAlt, "Open Mini Window", availableWidth, true))
                miniWindow.IsOpen = true;
        }

        DrawReadyCheckConfirmationInline();

        if (!string.IsNullOrWhiteSpace(checkInStatusMessage))
        {
            ImGui.Spacing();
            ImGui.TextColored(FullPartyModernPalette.Muted, checkInStatusMessage);
        }

        EndRunPanel();
    }

    private void DrawReadyCheckConfirmationInline()
    {
        var prompt = liveRoom.ReadyCheckConfirmationPrompt;
        if (prompt == null)
            return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.95f, 0.82f, 0.45f, 1f), "Ready check confirmation");
        DrawReadyCheckConfirmationPrompt(prompt, false);
    }

    private void DrawReadyCheckConfirmationPopup()
    {
        const string PopupName = "Ready Check Confirmation##fullparty_ready_check_confirm";
        var prompt = liveRoom.ReadyCheckConfirmationPrompt;
        if (prompt == null)
        {
            readyCheckPromptPopupRequestId = null;
            return;
        }

        if (!prompt.RequestId.Equals(readyCheckPromptPopupRequestId, StringComparison.Ordinal))
        {
            readyCheckPromptPopupRequestId = prompt.RequestId;
            ImGui.OpenPopup(PopupName);
        }

        var popupOpen = true;
        if (!ImGui.BeginPopupModal(PopupName, ref popupOpen, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        DrawReadyCheckConfirmationPrompt(prompt, true);
        if (!popupOpen)
        {
            liveRoom.ConfirmReadyCheck(false);
            readyCheckPromptPopupRequestId = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawReadyCheckConfirmationPrompt(FullPartyReadyCheckConfirmationPrompt prompt, bool closePopupOnAction)
    {
        ImGui.TextWrapped($"{prompt.InitiatorName} wants to start an alliance ready check.");
        ImGui.TextWrapped("Confirm when you are ready for your party to receive the in-game ready check.");
        ImGui.Spacing();
        ImGui.TextDisabled($"Expires at {prompt.ExpiresAt:HH:mm:ss}.");
        ImGui.Spacing();

        if (ImGui.Button($"I'm Ready##fullparty_ready_check_confirm_ready_{(closePopupOnAction ? "popup" : "inline")}", new Vector2(110f, 0)))
        {
            liveRoom.ConfirmReadyCheck(true);
            readyCheckPromptPopupRequestId = null;
            if (closePopupOnAction)
                ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button($"Not Ready##fullparty_ready_check_confirm_not_ready_{(closePopupOnAction ? "popup" : "inline")}", new Vector2(110f, 0)))
        {
            liveRoom.ConfirmReadyCheck(false);
            readyCheckPromptPopupRequestId = null;
            if (closePopupOnAction)
                ImGui.CloseCurrentPopup();
        }
    }

    private void DrawRosterControls(bool isLoading)
    {
        var panelHeight = isLoading || !string.IsNullOrWhiteSpace(detailError) || detail == null ? 136f : 112f;
        BeginRunPanel("roster_controls", FontAwesomeIcon.User, "Roster", panelHeight);

        var availableWidth = ImGui.GetContentRegionAvail().X;
        var gap = ImGui.GetStyle().ItemSpacing.X;
        var buttonWidth = (availableWidth - gap) * 0.5f;

        if (isLoading)
            ImGui.BeginDisabled();

        if (DrawRunActionButton(FontAwesomeIcon.SyncAlt, "Refresh Data", buttonWidth, true, true))
            RefreshRosterData();

        if (isLoading)
            ImGui.EndDisabled();

        ImGui.SameLine();
        var rosterVisible = rosterViewMode != RunRosterViewMode.None;
        if (DrawRunActionButton(
                rosterVisible ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.List,
                rosterVisible ? "Hide Roster" : "Show Roster",
                buttonWidth,
                true,
                rosterVisible))
        {
            rosterViewMode = rosterVisible ? RunRosterViewMode.None : RunRosterViewMode.Roster;
            if (!rosterVisible)
                applyRosterCompanionSizeNextDraw = true;
        }

        if (isLoading)
            ImGui.TextDisabled("Loading roster...");
        else if (!string.IsNullOrWhiteSpace(detailError))
            ImGui.TextWrapped(detailError);
        else if (detail == null)
            ImGui.TextDisabled("No roster loaded.");

        EndRunPanel();
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

        ModernWindowStyle.PushTitleBar();
        var companionOpen = ImGui.Begin($"{GetRosterViewTitle(rosterViewMode)}##fullparty_roster_companion_{Run.Id}", flags);
        ModernWindowStyle.PopTitleBar();

        if (companionOpen)
        {
            using var palette = ModernWindowStyle.PushContentPalette();
            RememberRosterCompanionSize(ImGui.GetWindowSize());
            applyRosterCompanionSizeNextDraw = false;
            DrawRosterCompanionContent(isLoading);
        }

        ImGui.End();
    }

    private void DrawLiveRoom()
    {
        BeginRunPanel("live_room", FontAwesomeIcon.Cloud, "Live Room", 128f);

        var isBusy = liveRoom.IsBusy;
        var statusColor = GetLiveRoomStatusColor();
        var rowStart = ImGui.GetCursorScreenPos();
        var statusCenter = rowStart + new Vector2(28f, 31f);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddCircleFilled(statusCenter, 23f, FullPartyModernPalette.Color(statusColor with { W = 0.16f }));
        drawList.AddCircle(statusCenter, 23f, FullPartyModernPalette.Color(statusColor with { W = 0.45f }), 28, 1.5f);
        drawList.AddCircleFilled(statusCenter, 7f, FullPartyModernPalette.Color(statusColor));

        var statusTitle = liveRoom.State == RealtimeRunRoomState.Connected
            ? "Connected to live room"
            : liveRoom.StatusMessage;
        drawList.AddText(rowStart + new Vector2(66f, 11f), FullPartyModernPalette.Color(statusColor), statusTitle);
        var statusDetail = liveRoom.PartySnapshotStatusMessage ?? liveRoom.CommandStatusMessage ?? "Party sync waits for Occult Crescent.";
        drawList.AddText(rowStart + new Vector2(66f, 36f), FullPartyModernPalette.Color(FullPartyModernPalette.Muted), TrimToWidth(statusDetail, MathF.Max(80f, ImGui.GetContentRegionAvail().X - 270f)));

        var actionWidth = liveRoom.IsActive ? 150f : 178f;
        ImGui.SetCursorScreenPos(new Vector2(rowStart.X + ImGui.GetContentRegionAvail().X - actionWidth, rowStart.Y + 11f));
        if (liveRoom.IsActive)
        {
            if (DrawRunActionButton(FontAwesomeIcon.SignOutAlt, "Disconnect", actionWidth, true))
                liveRoom.Disconnect();
        }
        else
        {
            if (isBusy)
                ImGui.BeginDisabled();

            if (DrawRunActionButton(FontAwesomeIcon.Cloud, "Connect to Live Room", actionWidth, true, true))
                liveRoom.Connect();

            if (isBusy)
                ImGui.EndDisabled();
        }

        ImGui.SetCursorScreenPos(rowStart + new Vector2(0, 63f));
        ImGui.Dummy(new Vector2(1f, 1f));
        EndRunPanel();

        if (plugin.Configuration.ShowLiveRoomData)
            DrawLiveRoomDebugData();

        DrawLiveMembersSection(false);
    }

    private void DrawLiveMembersSection(bool alwaysShow)
    {
        var members = liveRoom.Members;
        if (!alwaysShow && !liveRoom.IsActive && members.Count == 0 && liveRoom.State != RealtimeRunRoomState.Error)
            return;

        ImGui.Spacing();
        var memberPanelHeight = MathF.Max(160f, 78f + (members.Count * 54f));
        BeginRunPanel("live_members", FontAwesomeIcon.Users, "Members", memberPanelHeight, $"{members.Count} {(members.Count == 1 ? "Member" : "Members")}");

        if (members.Count == 0)
        {
            ImGui.TextDisabled(liveRoom.State == RealtimeRunRoomState.Connected
                ? "No connected characters yet."
                : "Connect to see live characters.");
            EndRunPanel();
            return;
        }

        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        const int columnCount = 4;
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(10f, 9f));
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, FullPartyModernPalette.BrandSoft with { W = 0.96f });
        ImGui.PushStyleColor(ImGuiCol.TableRowBg, FullPartyModernPalette.Elevated with { W = 0.30f });
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, FullPartyModernPalette.BrandSoft with { W = 0.18f });
        ImGui.PushStyleColor(ImGuiCol.TableBorderStrong, FullPartyModernPalette.BorderSoft);
        ImGui.PushStyleColor(ImGuiCol.TableBorderLight, FullPartyModernPalette.Border);

        if (!ImGui.BeginTable("##fullparty_live_room_members", columnCount, flags))
        {
            ImGui.PopStyleColor(5);
            ImGui.PopStyleVar();
            EndRunPanel();
            return;
        }

        ImGui.TableSetupColumn("Profile", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Party", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Role / Connection", ImGuiTableColumnFlags.WidthFixed, 132f);

        DrawLiveMembersTableHeader();

        foreach (var member in members)
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, 48f);

            ImGui.TableNextColumn();
            DrawLiveMemberCharacter(member);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(liveRoom.GetSyncedPartyLabel(member));

            ImGui.TableNextColumn();
            DrawLiveCommandStatus(member);

            ImGui.TableNextColumn();
            DrawLiveMemberConnection(member);
        }

        ImGui.EndTable();
        ImGui.PopStyleColor(5);
        ImGui.PopStyleVar();
        EndRunPanel();
    }

    private static void DrawLiveMembersTableHeader()
    {
        var labels = new[] { "PROFILE", "PARTY", "COMMAND", "ROLE / CONNECTION" };

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers, 34f);
        foreach (var label in labels)
        {
            ImGui.TableNextColumn();
            ImGui.TextColored(FullPartyModernPalette.Brand with { X = 0.76f, Y = 0.58f, Z = 0.96f }, label);
        }
    }

    private void DrawLiveMemberConnection(FullPartyLiveMember member)
    {
        var connected = liveRoom.State == RealtimeRunRoomState.Connected;
        var color = connected
            ? new Vector4(0.32f, 0.92f, 0.54f, 1f)
            : FullPartyModernPalette.Muted;
        var cursor = ImGui.GetCursorScreenPos();
        var center = cursor + new Vector2(5f, ImGui.GetTextLineHeight() * 0.5f);
        ImGui.GetWindowDrawList().AddCircleFilled(center, 4f, FullPartyModernPalette.Color(color));
        ImGui.SetCursorScreenPos(cursor + new Vector2(15f, 0f));
        ImGui.TextColored(color, GetLiveMemberRole(member));
    }

    private void DrawLiveRoomDebugData()
    {
        var debug = liveRoom.PartySyncDebug;
        var incomingSnapshots = liveRoom.PartySnapshots;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        FullPartyModernPalette.SectionHeader(FontAwesomeIcon.InfoCircle, "Liveroom Data");

        if (ImGui.TreeNode("Outgoing"))
        {
            DrawLiveRoomOutgoingDebugData(debug);
            ImGui.TreePop();
        }

        if (ImGui.TreeNode("Incoming"))
        {
            DrawLiveRoomIncomingDebugData(incomingSnapshots);
            ImGui.TreePop();
        }
    }

    private void DrawLiveRoomOutgoingDebugData(FullPartyPartySyncDebug? debug)
    {
        if (debug == null)
        {
            ImGui.TextDisabled("No party-sync attempt has happened yet.");
            return;
        }

        if (ImGui.BeginTable("##fullparty_liveroom_debug_summary", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Check", ImGuiTableColumnFlags.WidthFixed, 120f);
            ImGui.TableSetupColumn("Value");

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted("Found Raidlead");
            ImGui.TableNextColumn();
            DrawYesNo(debug.FoundRaidLead);
            if (debug.FoundRaidLead)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"[{FormatNameWorld(debug.RaidLeadName, debug.RaidLeadWorld)}]");
            }

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted("Found Party");
            ImGui.TableNextColumn();
            DrawYesNo(debug.FoundParty);
            if (debug.FoundParty)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"[{FormatPartyLetter(debug.PartyKey)}]");
            }

            ImGui.EndTable();
        }

        ImGui.TextDisabled($"Last attempt: {debug.CapturedAt:HH:mm:ss} UTC");
        if (!string.IsNullOrWhiteSpace(debug.Status))
            ImGui.TextWrapped(debug.Status);

        ImGui.Spacing();
        ImGui.TextUnformatted("Outgoing party-sync payload");

        var snapshot = debug.OutgoingSnapshot;
        if (snapshot == null)
        {
            ImGui.TextDisabled("No outbound payload was built.");
            return;
        }

        DrawPartySnapshotDebugPayload(snapshot, "outgoing");
    }

    private void DrawLiveRoomIncomingDebugData(IReadOnlyList<FullPartyPartySnapshot> snapshots)
    {
        if (snapshots.Count == 0)
        {
            ImGui.TextDisabled("No party snapshots received yet.");
            return;
        }

        foreach (var snapshot in snapshots.OrderBy(snapshot => snapshot.PartyKey, StringComparer.OrdinalIgnoreCase))
        {
            var label = $"{FormatPartyKey(snapshot.PartyKey)}##fullparty_liveroom_incoming_{snapshot.PartyKey}";
            if (!ImGui.TreeNode(label))
                continue;

            DrawPartySnapshotDebugPayload(snapshot, $"incoming_{snapshot.PartyKey}");
            ImGui.TreePop();
        }
    }

    private static void DrawPartySnapshotDebugPayload(FullPartyPartySnapshot snapshot, string id)
    {
        if (ImGui.BeginTable($"##fullparty_liveroom_debug_payload_header_{id}", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Key", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Value");
            DrawDebugKeyValue("seq", snapshot.Sequence.ToString());
            DrawDebugKeyValue("party_key", snapshot.PartyKey);
            DrawDebugKeyValue("sender", snapshot.SenderUserId.ToString());
            DrawDebugKeyValue("captured", snapshot.CapturedAt.ToString("HH:mm:ss"));
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted($"members ({snapshot.Members.Count})");
        if (snapshot.Members.Count == 0)
        {
            ImGui.TextDisabled("No members in this party-sync snapshot.");
            return;
        }

        foreach (var member in snapshot.Members.OrderBy(member => member.Position))
            DrawPartySnapshotMemberDebug(member, id);
    }

    private static void DrawPartySnapshotMemberDebug(FullPartyPartySnapshotMember member, string id)
    {
        var displayName = member.CharacterId != null
            ? $"cid {member.CharacterId}"
            : FormatDebugValue(member.Name);
        var label = $"p{member.Position}: {displayName}##fullparty_liveroom_debug_member_{id}_{member.Position}_{member.CharacterId?.ToString() ?? member.Name ?? "unknown"}";

        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(1f, 1f, 1f, 0.08f));
        var open = ImGui.TreeNode(label);
        ImGui.PopStyleColor();
        if (!open)
            return;

        if (ImGui.BeginTable($"##fullparty_liveroom_debug_member_fields_{id}_{member.Position}", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Key", ImGuiTableColumnFlags.WidthFixed, 42f);
            ImGui.TableSetupColumn("Value");
            DrawDebugKeyValue("p", member.Position.ToString());
            DrawDebugKeyValue("cid", member.CharacterId?.ToString());
            DrawDebugKeyValue("n", member.Name);
            DrawDebugKeyValue("w", member.World);
            DrawDebugKeyValue("cj", member.ClassJob);
            DrawDebugKeyValue("pj", member.PhantomJob);
            DrawDebugKeyValue("r", member.ResurrectionCharges?.ToString());
            ImGui.EndTable();
        }

        ImGui.TreePop();
    }

    private static void DrawYesNo(bool value)
    {
        ImGui.TextColored(
            value ? new Vector4(0.35f, 0.92f, 0.55f, 1f) : new Vector4(1f, 0.42f, 0.42f, 1f),
            value ? "Yes" : "No");
    }

    private static void DrawDebugKeyValue(string key, string? value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextDisabled(key);
        ImGui.TableNextColumn();
        ImGui.TextWrapped(string.IsNullOrWhiteSpace(value) ? "-" : value);
    }

    private static string FormatDebugValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static string FormatNameWorld(string? name, string? world)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "-";

        return string.IsNullOrWhiteSpace(world) ? name : $"{name} @ {world}";
    }

    private static string FormatPartyLetter(string? partyKey)
    {
        if (string.IsNullOrWhiteSpace(partyKey))
            return "-";

        const string prefix = "party-";
        return partyKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && partyKey.Length > prefix.Length
            ? partyKey[prefix.Length..].ToUpperInvariant()
            : partyKey;
    }

    private static string FormatPartyKey(string partyKey)
    {
        var letter = FormatPartyLetter(partyKey);
        return letter.Length == 1 ? $"Party {letter}" : letter;
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

    private static bool DrawRunActionButton(
        FontAwesomeIcon icon,
        string label,
        float? width = null,
        bool tall = false,
        bool emphasized = false)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12f, tall ? 11f : 6f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
        ImGui.PushStyleColor(
            ImGuiCol.Button,
            emphasized
                ? FullPartyModernPalette.BrandSoft with { W = 0.98f }
                : FullPartyModernPalette.Elevated with { W = 0.82f });
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, FullPartyModernPalette.BrandSoft);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, FullPartyModernPalette.BrandHover);
        ImGui.PushStyleColor(
            ImGuiCol.Border,
            emphasized
                ? FullPartyModernPalette.Brand with { W = 0.90f }
                : FullPartyModernPalette.BorderSoft);

        var clicked = FullPartyModernPalette.IconButton(icon, label, width);

        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar(2);
        return clicked;
    }

    private void BeginRunPanel(
        string id,
        FontAwesomeIcon icon,
        string title,
        float height,
        string? badge = null)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, FullPartyModernPalette.Surface with { W = 0.88f });
        ImGui.PushStyleColor(ImGuiCol.Border, FullPartyModernPalette.BorderSoft);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f, 12f));
        ImGui.BeginChild($"##fullparty_run_panel_{id}", new Vector2(0f, height), true, ImGuiWindowFlags.NoScrollbar);
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);

        DrawRunPanelHeader(icon, title, badge);
    }

    private static void EndRunPanel()
    {
        ImGui.EndChild();
    }

    private void DrawRunPanelHeader(FontAwesomeIcon icon, string title, string? badge)
    {
        var drawList = ImGui.GetWindowDrawList();
        var windowPosition = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var start = windowPosition + new Vector2(1f, 1f);
        var width = windowSize.X - 2f;
        const float height = 42f;
        var end = start + new Vector2(width, height);

        var visibleClipMin = new Vector2(
            windowPosition.X,
            MathF.Max(windowPosition.Y, runContentClipMin.Y));
        var visibleClipMax = new Vector2(
            windowPosition.X + windowSize.X,
            MathF.Min(windowPosition.Y + windowSize.Y, runContentClipMax.Y));
        if (visibleClipMax.X > visibleClipMin.X && visibleClipMax.Y > visibleClipMin.Y)
        {
            drawList.PushClipRect(visibleClipMin, visibleClipMax, false);
            drawList.AddRectFilled(start, end, FullPartyModernPalette.Color(FullPartyModernPalette.BrandSoft));
            drawList.AddLine(
                new Vector2(start.X, end.Y),
                end,
                FullPartyModernPalette.Color(FullPartyModernPalette.BorderSoft));
            DrawPanelDecoration(drawList, start, end);
            drawList.PopClipRect();
        }

        ImGui.SetCursorScreenPos(start + new Vector2(12f, 11f));
        ImGui.PushStyleColor(ImGuiCol.Text, FullPartyModernPalette.Brand with { X = 0.72f, Y = 0.50f, Z = 0.95f });
        ElezenImgui.ShowIcon(icon);
        ImGui.SameLine(0f, 9f);
        ImGui.TextUnformatted(title.ToUpperInvariant());
        ImGui.PopStyleColor();

        if (!string.IsNullOrWhiteSpace(badge))
        {
            var badgeSize = ImGui.CalcTextSize(badge);
            var badgeWidth = badgeSize.X + 24f;
            var badgeMin = new Vector2(end.X - badgeWidth - 12f, start.Y + 9f);
            var badgeMax = badgeMin + new Vector2(badgeWidth, badgeSize.Y + 8f);
            drawList.AddRectFilled(badgeMin, badgeMax, FullPartyModernPalette.Color(FullPartyModernPalette.BrandSoft with { W = 0.80f }), 12f);
            drawList.AddText(badgeMin + new Vector2(12f, 4f), FullPartyModernPalette.Color(FullPartyModernPalette.Text), badge);
        }

        ImGui.SetCursorScreenPos(start + new Vector2(13f, height + 12f));
    }

    private static void DrawPanelDecoration(ImDrawListPtr drawList, Vector2 start, Vector2 end)
    {
        var color = FullPartyModernPalette.Color(FullPartyModernPalette.Brand with { W = 0.10f });
        var right = end.X - 18f;
        drawList.AddLine(new Vector2(right - 52f, start.Y), new Vector2(right - 24f, end.Y), color, 1.5f);
        drawList.AddLine(new Vector2(right - 30f, start.Y), new Vector2(right - 2f, end.Y), color, 1.5f);
        drawList.AddCircle(new Vector2(right, (start.Y + end.Y) * 0.5f), 6f, color, 4, 1.5f);
    }

    private static void DrawVerticalPanelDivider(ImDrawListPtr drawList, float x, float top, float bottom)
    {
        drawList.AddLine(
            new Vector2(x, top),
            new Vector2(x, bottom),
            FullPartyModernPalette.Color(FullPartyModernPalette.BorderSoft));
    }

    private Vector4 GetLiveRoomStatusColor()
    {
        return liveRoom.State switch
        {
            RealtimeRunRoomState.Connected => new Vector4(0.32f, 0.92f, 0.54f, 1f),
            RealtimeRunRoomState.Error => FullPartyModernPalette.Danger,
            RealtimeRunRoomState.Disconnected => FullPartyModernPalette.Muted,
            _ => new Vector4(0.94f, 0.78f, 0.32f, 1f),
        };
    }

    private void DrawStatusAvatar(ImDrawListPtr drawList, string? avatarUrl, long userId, Vector2 position, float size)
    {
        var path = plugin.ImageCache.GetImagePath(avatarUrl, $"run-status-user-{userId}");
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var texture = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
            if (texture != null)
            {
                drawList.AddImageRounded(
                    texture.Handle,
                    position,
                    position + new Vector2(size, size),
                    Vector2.Zero,
                    Vector2.One,
                    ImGui.GetColorU32(ImGuiCol.Text),
                    size * 0.5f);
                drawList.AddCircle(
                    position + new Vector2(size * 0.5f),
                    size * 0.5f,
                    FullPartyModernPalette.Color(FullPartyModernPalette.BorderSoft),
                    32,
                    1.5f);
                return;
            }
        }

        var center = position + new Vector2(size * 0.5f);
        drawList.AddCircleFilled(center, size * 0.5f, FullPartyModernPalette.Color(FullPartyModernPalette.Elevated));
        drawList.AddCircle(center, size * 0.5f, FullPartyModernPalette.Color(FullPartyModernPalette.BorderSoft), 32, 1.5f);
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
            RunRosterViewMode.Party => "Party",
            RunRosterViewMode.Validate => "Validation",
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

        DrawModernRosterSummary(parties, benchSlots);
        ImGui.Spacing();
        DrawModernRosterToolbar();
        ImGui.Spacing();

        DrawModernPartyRoster(parties, runDetail);

        if (benchSlots.Count > 0)
        {
            ImGui.Spacing();
            DrawModernBench(runDetail, benchSlots, runDetail.CanModerate);
        }
    }

    private void DrawModernRosterSummary(
        IReadOnlyList<IReadOnlyList<FullPartyRosterSlot>> parties,
        IReadOnlyList<FullPartyRosterSlot> benchSlots)
    {
        var partySlots = parties.SelectMany(party => party).ToList();
        var assignedPlayers = partySlots.Count(slot => slot.AssignedCharacter != null);
        var assignedParties = parties.Count(party => party.Any(slot => slot.AssignedCharacter != null));
        var assignedBench = benchSlots.Count(slot => slot.AssignedCharacter != null);
        var missing = partySlots.Count - assignedPlayers;

        if (!ImGui.BeginTable("##fullparty_roster_summary", 2, ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Title", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Stats", ImGuiTableColumnFlags.WidthFixed, 470f);
        ImGui.TableNextRow(ImGuiTableRowFlags.None, 40f);
        ImGui.TableNextColumn();
        ImGui.SetWindowFontScale(1.28f);
        ImGui.TextUnformatted("Roster");
        ImGui.SetWindowFontScale(1f);

        ImGui.TableNextColumn();
        DrawRosterStatPill("Players", $"{assignedPlayers} / {partySlots.Count}", 112f);
        ImGui.SameLine();
        DrawRosterStatPill("Parties", $"{assignedParties} / {parties.Count}", 104f);
        ImGui.SameLine();
        DrawRosterStatPill("Bench", $"{assignedBench} / {benchSlots.Count}", 96f);
        ImGui.SameLine();
        DrawRosterReadinessPill(missing);
        ImGui.EndTable();

        ImGui.Separator();
    }

    private static void DrawRosterStatPill(string label, string value, float width)
    {
        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, 30f);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(start, start + size, FullPartyModernPalette.Color(FullPartyModernPalette.Surface with { W = 0.90f }), 5f);
        drawList.AddRect(start, start + size, FullPartyModernPalette.Color(FullPartyModernPalette.Border), 5f);
        drawList.AddText(start + new Vector2(9f, 7f), FullPartyModernPalette.Color(FullPartyModernPalette.Brand with { X = 0.76f, Y = 0.58f, Z = 0.96f }), label);
        var valueWidth = ImGui.CalcTextSize(value).X;
        drawList.AddText(start + new Vector2(width - valueWidth - 9f, 7f), FullPartyModernPalette.Color(FullPartyModernPalette.Text), value);
        ImGui.Dummy(size);
    }

    private static void DrawRosterReadinessPill(int missing)
    {
        var ready = missing == 0;
        var label = ready ? "Ready" : $"{missing} Open";
        var color = ready ? FullPartyModernPalette.Success : new Vector4(0.94f, 0.62f, 0.22f, 1f);
        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(104f, 30f);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(start, start + size, FullPartyModernPalette.Color(color with { W = 0.14f }), 5f);
        drawList.AddRect(start, start + size, FullPartyModernPalette.Color(color with { W = 0.68f }), 5f);
        drawList.AddCircleFilled(start + new Vector2(13f, 15f), 4f, FullPartyModernPalette.Color(color));
        drawList.AddText(start + new Vector2(23f, 7f), FullPartyModernPalette.Color(color), label);
        ImGui.Dummy(size);
    }

    private void DrawModernRosterToolbar()
    {
        ImGui.SetNextItemWidth(230f);
        ImGui.InputTextWithHint("##fullparty_roster_search", "Search name, class, or phantom job...", ref rosterSearch, 80);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(145f);
        if (ImGui.BeginCombo("##fullparty_roster_layout", GetRosterLayoutLabel(rosterLayoutMode)))
        {
            foreach (var layout in Enum.GetValues<RosterLayoutMode>())
            {
                if (ImGui.Selectable(GetRosterLayoutLabel(layout), rosterLayoutMode == layout))
                    rosterLayoutMode = layout;
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (DrawRunActionButton(
                FontAwesomeIcon.User,
                $"Roster: {GetRosterDataModeLabel(rosterDataMode)}",
                138f,
                false,
                rosterDataMode != RosterDataMode.Off))
        {
            rosterDataMode = rosterDataMode switch
            {
                RosterDataMode.Off => RosterDataMode.Validate,
                RosterDataMode.Validate => RosterDataMode.Only,
                _ => RosterDataMode.Off,
            };
        }

        ImGui.SameLine();
        if (DrawRunActionButton(
                FontAwesomeIcon.List,
                rosterShowEmptySlots ? "Empty: On" : "Empty: Off",
                112f,
                false,
                rosterShowEmptySlots))
        {
            rosterShowEmptySlots = !rosterShowEmptySlots;
        }

        ImGui.SameLine();
        if (DrawRunActionButton(FontAwesomeIcon.SyncAlt, "Reset", 82f))
        {
            rosterSearch = string.Empty;
            rosterLayoutMode = RosterLayoutMode.Normal;
            rosterDataMode = RosterDataMode.Off;
            rosterShowEmptySlots = true;
        }
    }

    private static string GetRosterLayoutLabel(RosterLayoutMode mode)
    {
        return mode == RosterLayoutMode.SplitThreeByThree ? "3/3 Split" : "Normal";
    }

    private static string GetRosterDataModeLabel(RosterDataMode mode)
    {
        return mode switch
        {
            RosterDataMode.Off => "Off",
            RosterDataMode.Validate => "Validate",
            _ => "Only",
        };
    }

    private void DrawModernPartyRoster(
        IReadOnlyList<IReadOnlyList<FullPartyRosterSlot>> parties,
        FullPartyRunDetail runDetail)
    {
        if (parties.Count == 0)
        {
            ImGui.TextDisabled("No parties match the selected filter.");
            return;
        }

        var computedSnapshots = rosterDataMode == RosterDataMode.Only
            ? null
            : BuildComputedPartySnapshots(runDetail, parties, true);
        var observedById = computedSnapshots == null
            ? new Dictionary<long, ObservedSnapshotMember>()
            : BuildObservedByCharacterId(computedSnapshots.Snapshots);
        var observedByName = computedSnapshots == null
            ? new Dictionary<string, ObservedSnapshotMember>(StringComparer.OrdinalIgnoreCase)
            : BuildObservedByName(runDetail, computedSnapshots.Snapshots);

        if (rosterLayoutMode == RosterLayoutMode.SplitThreeByThree && parties.Count > 3)
        {
            DrawModernPartyRosterRow(parties.Take(3).ToList(), runDetail, computedSnapshots, observedById, observedByName, "abc");
            ImGui.Spacing();
            DrawModernPartyRosterRow(parties.Skip(3).Take(3).ToList(), runDetail, computedSnapshots, observedById, observedByName, "def");
            return;
        }

        DrawModernPartyRosterRow(parties, runDetail, computedSnapshots, observedById, observedByName, "normal");
    }

    private void DrawModernPartyRosterRow(
        IReadOnlyList<IReadOnlyList<FullPartyRosterSlot>> parties,
        FullPartyRunDetail runDetail,
        ComputedPartySnapshots? computedSnapshots,
        IReadOnlyDictionary<long, ObservedSnapshotMember> observedById,
        IReadOnlyDictionary<string, ObservedSnapshotMember> observedByName,
        string id)
    {

        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(8f, 8f));
        ImGui.PushStyleColor(ImGuiCol.TableBorderStrong, FullPartyModernPalette.BorderSoft);
        ImGui.PushStyleColor(ImGuiCol.TableBorderLight, FullPartyModernPalette.Border);
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame;
        if (!ImGui.BeginTable($"##fullparty_modern_roster_{id}", parties.Count, flags))
        {
            ImGui.PopStyleColor(2);
            ImGui.PopStyleVar();
            return;
        }

        foreach (var party in parties)
            ImGui.TableSetupColumn(party[0].GroupLabel);

        ImGui.TableNextRow();
        foreach (var party in parties)
        {
            ImGui.TableNextColumn();
            FullPartyPartySnapshot? snapshot = null;
            if (computedSnapshots != null)
                computedSnapshots.ByParty.TryGetValue(party[0].GroupKey, out snapshot);
            var filledCount = rosterDataMode == RosterDataMode.Only
                ? party.Count(slot => slot.AssignedCharacter != null)
                : snapshot?.Members.Count ?? 0;
            DrawModernPartyHeader(party, filledCount);

            var renderedAny = false;
            for (var row = 0; row < party.Count; row++)
            {
                var slot = party[row];
                var expectedPosition = slot.PositionInGroup ?? row + 1;
                var actualMember = snapshot?.Members.FirstOrDefault(member => member.Position == expectedPosition);

                if (!rosterShowEmptySlots &&
                    ((rosterDataMode == RosterDataMode.Only && slot.AssignedCharacter == null) ||
                     (rosterDataMode == RosterDataMode.Off && actualMember == null)))
                {
                    continue;
                }

                ImGui.Spacing();
                renderedAny = true;

                if (rosterDataMode == RosterDataMode.Only)
                {
                    DrawModernRosterSlot(runDetail, slot, runDetail.CanModerate);
                    continue;
                }

                if (rosterDataMode == RosterDataMode.Off)
                {
                    DrawModernDetectedRosterSlot(runDetail, slot, actualMember);
                    continue;
                }

                var expectedObserved = FindObservedForSlot(slot, observedById, observedByName);
                var validationMember = FindExpectedMemberInParty(runDetail, slot, snapshot);
                var result = BuildValidationSlotResult(
                    runDetail,
                    slot,
                    validationMember,
                    expectedObserved,
                    computedSnapshots?.OccultPresence ?? GamePresenceList.Empty,
                    computedSnapshots?.InOccult == true,
                    snapshot != null);

                if (result == null && !rosterShowEmptySlots)
                    continue;

                DrawModernValidationRosterSlot(runDetail, slot, result);
            }

            if (!renderedAny)
                ImGui.TextDisabled("No players");
        }

        ImGui.EndTable();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();
    }

    private static void DrawModernPartyHeader(IReadOnlyList<FullPartyRosterSlot> party, int filledCount)
    {
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        const float height = 36f;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(start, start + new Vector2(width, height), FullPartyModernPalette.Color(FullPartyModernPalette.Elevated with { W = 0.72f }), 4f);

        var letter = FormatPartyLetter(party[0].GroupKey);
        drawList.AddText(start + new Vector2(10f, 9f), FullPartyModernPalette.Color(FullPartyModernPalette.Brand with { X = 0.78f, Y = 0.58f, Z = 0.98f }), letter);
        var count = $"{filledCount}/{party.Count}";
        var countWidth = ImGui.CalcTextSize(count).X;
        drawList.AddText(start + new Vector2(width - countWidth - 10f, 9f), FullPartyModernPalette.Color(FullPartyModernPalette.Muted), count);
        ImGui.Dummy(new Vector2(width, height));
    }

    private bool IsRosterSearchMatch(
        string? displayName,
        string? classJob,
        string? phantomJob,
        FullPartyRosterSlot? rosterSlot)
    {
        if (string.IsNullOrWhiteSpace(rosterSearch))
            return false;

        var query = rosterSearch.Trim();
        var normalizedClass = NormalizeClassJob(classJob ?? rosterSlot?.CharacterClass);
        var normalizedPhantomJob = NormalizePhantomJob(phantomJob ?? rosterSlot?.PhantomJob);
        return (displayName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (classJob?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (rosterSlot?.CharacterClass?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (normalizedClass?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (phantomJob?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (rosterSlot?.PhantomJob?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (normalizedPhantomJob?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void DrawModernRosterSlot(
        FullPartyRunDetail runDetail,
        FullPartyRosterSlot slot,
        bool canModerate)
    {
        DrawModernResolvedRosterSlot(
            runDetail,
            slot,
            slot.AssignedCharacter,
            slot,
            slot.AssignedCharacter?.Name,
            slot.CharacterClass,
            slot.PhantomJob,
            null,
            slot.CharacterClassRole,
            null,
            [],
            canModerate);
    }

    private void DrawModernDetectedRosterSlot(
        FullPartyRunDetail runDetail,
        FullPartyRosterSlot plannedSlot,
        FullPartyPartySnapshotMember? member)
    {
        if (member == null)
        {
            DrawModernResolvedRosterSlot(runDetail, plannedSlot, null, null, null, null, null, null, null, null, [], runDetail.CanModerate);
            return;
        }

        var resolvedSlot = ResolveRosterSlot(runDetail, member);
        var character = ResolveCharacter(runDetail, member);
        var classJob = NormalizeClassJob(member.ClassJob);
        DrawModernResolvedRosterSlot(
            runDetail,
            plannedSlot,
            character,
            resolvedSlot,
            character?.Name ?? member.DisplayName,
            classJob,
            member.PhantomJob,
            member.ResurrectionCharges,
            GetRoleForClassJob(runDetail, classJob),
            null,
            [],
            runDetail.CanModerate);
    }

    private void DrawModernValidationRosterSlot(
        FullPartyRunDetail runDetail,
        FullPartyRosterSlot plannedSlot,
        ValidationSlotResult? result)
    {
        if (result == null)
        {
            DrawModernResolvedRosterSlot(runDetail, plannedSlot, null, null, null, null, null, null, null, null, [], runDetail.CanModerate);
            return;
        }

        DrawModernResolvedRosterSlot(
            runDetail,
            plannedSlot,
            result.Character,
            result.RosterSlot,
            result.DisplayName,
            result.ClassJob,
            result.PhantomJob,
            null,
            GetRoleForClassJob(runDetail, result.ClassJob),
            result.State,
            result.Messages,
            runDetail.CanModerate);
    }

    private void DrawModernResolvedRosterSlot(
        FullPartyRunDetail runDetail,
        FullPartyRosterSlot plannedSlot,
        FullPartyRosterCharacter? character,
        FullPartyRosterSlot? rosterSlot,
        string? displayName,
        string? classJob,
        string? phantomJob,
        int? resurrectionCharges,
        string? role,
        ValidationState? validationState,
        IReadOnlyList<string> validationMessages,
        bool canModerate)
    {
        const float height = 44f;
        var canOpenApplication = canModerate && rosterSlot?.ApplicationId != null;
        if (ImGui.InvisibleButton($"##modern_roster_slot_{plannedSlot.Id}_{rosterDataMode}", new Vector2(-1f, height)) && canOpenApplication)
            plugin.OpenApplicationWindow(Run, rosterSlot!);

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsItemHovered();
        var searchMatch = IsRosterSearchMatch(displayName, classJob, phantomJob, rosterSlot);
        var positionWidth = 24f;
        var cardMin = min + new Vector2(positionWidth + 4f, 0f);
        var number = (plannedSlot.PositionInGroup ?? 0).ToString();

        drawList.AddRectFilled(min, new Vector2(min.X + positionWidth, max.Y), FullPartyModernPalette.Color(FullPartyModernPalette.Elevated with { W = 0.72f }), 4f);
        var numberWidth = ImGui.CalcTextSize(number).X;
        drawList.AddText(new Vector2(min.X + ((positionWidth - numberWidth) * 0.5f), min.Y + 13f), FullPartyModernPalette.Color(FullPartyModernPalette.Muted), number);

        if (character == null && string.IsNullOrWhiteSpace(displayName))
        {
            drawList.AddRectFilled(cardMin, max, FullPartyModernPalette.Color(FullPartyModernPalette.Surface with { W = 0.54f }), 4f);
            drawList.AddRect(cardMin, max, FullPartyModernPalette.Color(FullPartyModernPalette.Border), 4f);
            const string empty = "Empty";
            var emptyWidth = ImGui.CalcTextSize(empty).X;
            drawList.AddText(new Vector2(cardMin.X + MathF.Max(8f, ((max.X - cardMin.X) - emptyWidth) * 0.5f), min.Y + 13f), FullPartyModernPalette.Color(FullPartyModernPalette.Muted), empty);
            return;
        }

        var background = validationState != null
            ? GetValidationSlotBackground(validationState.Value, hovered)
            : GetFilledSlotBackground(role, hovered && canOpenApplication) with { W = hovered ? 0.70f : 0.52f };
        drawList.AddRectFilled(cardMin, max, FullPartyModernPalette.Color(background), 4f);
        drawList.AddRect(cardMin, max, FullPartyModernPalette.Color(FullPartyModernPalette.BorderSoft with { W = hovered ? 0.90f : 0.56f }), 4f);

        var cursor = cardMin + new Vector2(6f, 8f);
        if (character != null)
            DrawCharacterIcon(drawList, character, cursor);
        else
            drawList.AddRectFilled(cursor, cursor + new Vector2(24f, 24f), FullPartyModernPalette.Color(FullPartyModernPalette.Elevated), 3f);
        cursor.X += 29f;

        if (IsValidationPartyLead(rosterSlot ?? plannedSlot))
            DrawPartyLeadCrown(drawList, ref cursor);

        var iconRight = max.X - 6f;
        Vector2? resurrectionIconPosition = null;
        Vector2? phantomIconPosition = null;
        Vector2? classIconPosition = null;
        if (rosterDataMode == RosterDataMode.Off && OccultCrescentStatusIds.IsForkedTowerContext())
        {
            iconRight -= 20f;
            resurrectionIconPosition = new Vector2(iconRight, min.Y + 12f);
            iconRight -= 4f;
        }

        var expectsPhantomJob = rosterSlot != null && HasPhantomJob(rosterSlot);
        if (!string.IsNullOrWhiteSpace(phantomJob) || expectsPhantomJob)
        {
            iconRight -= 20f;
            phantomIconPosition = new Vector2(iconRight, min.Y + 12f);
            iconRight -= 4f;
        }

        var expectsClassJob = !string.IsNullOrWhiteSpace(rosterSlot?.CharacterClass);
        if (!string.IsNullOrWhiteSpace(classJob) || expectsClassJob)
        {
            iconRight -= 20f;
            classIconPosition = new Vector2(iconRight, min.Y + 12f);
            iconRight -= 4f;
        }

        drawList.AddText(
            cursor + new Vector2(0f, 5f),
            FullPartyModernPalette.Color(FullPartyModernPalette.Text),
            TrimToWidth(displayName ?? character?.Name ?? "Unknown", MathF.Max(20f, iconRight - cursor.X)));

        if (classIconPosition != null)
        {
            if (string.IsNullOrWhiteSpace(classJob) || !DrawJobIcon(drawList, classJob, classIconPosition.Value))
                DrawUnknownDetectionIcon(drawList, classIconPosition.Value);
        }

        if (phantomIconPosition != null)
        {
            if (string.IsNullOrWhiteSpace(phantomJob) ||
                !DrawPhantomJobIconByName(drawList, runDetail, phantomJob, phantomIconPosition.Value))
            {
                DrawUnknownDetectionIcon(drawList, phantomIconPosition.Value);
            }
        }

        if (resurrectionIconPosition != null)
        {
            if (resurrectionCharges == null ||
                !DrawResurrectionStatusIcon(drawList, resurrectionCharges.Value, resurrectionIconPosition.Value))
            {
                DrawUnknownDetectionIcon(drawList, resurrectionIconPosition.Value);
            }
        }

        if (searchMatch)
        {
            drawList.AddRect(
                cardMin,
                max,
                ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.96f)),
                4f,
                ImDrawFlags.None,
                2f);
        }

        if (hovered && validationMessages.Count > 0)
        {
            const float TooltipWidth = 320f;
            ImGui.SetNextWindowSizeConstraints(new Vector2(TooltipWidth, 0f), new Vector2(TooltipWidth, float.MaxValue));
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + TooltipWidth);
            ImGui.TextUnformatted(plannedSlot.SlotLabel);
            ImGui.Separator();
            foreach (var message in validationMessages)
                ImGui.TextUnformatted(message);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    private void DrawModernBench(
        FullPartyRunDetail runDetail,
        IReadOnlyList<FullPartyRosterSlot> benchSlots,
        bool canModerate)
    {
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(FullPartyModernPalette.Brand with { X = 0.76f, Y = 0.58f, Z = 0.96f }, "BENCH");
        ImGui.SameLine();
        ImGui.TextDisabled($"{benchSlots.Count(slot => slot.AssignedCharacter != null)} / {benchSlots.Count}");
        ImGui.Spacing();

        var columnCount = Math.Clamp((int)(ImGui.GetContentRegionAvail().X / 250f), 1, 4);
        if (!ImGui.BeginTable("##fullparty_modern_bench", columnCount, ImGuiTableFlags.SizingStretchSame))
            return;

        for (var column = 0; column < columnCount; column++)
            ImGui.TableSetupColumn($"##modern_bench_{column}");

        var visibleBench = benchSlots
            .Where(slot => rosterShowEmptySlots || slot.AssignedCharacter != null)
            .ToList();
        for (var index = 0; index < visibleBench.Count; index++)
        {
            if (index % columnCount == 0)
                ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawModernRosterSlot(runDetail, visibleBench[index], canModerate);
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

        DrawValidationDetectedPlayers(runDetail, parties, computedSnapshots);
        DrawValidationStatusText(computedSnapshots.InOccult, computedSnapshots.Snapshots.Count, computedSnapshots.OccultPresence.Count, computedSnapshots.OccultPartyCount);
    }

    private void DrawValidationDetectedPlayers(
        FullPartyRunDetail runDetail,
        IReadOnlyList<IReadOnlyList<FullPartyRosterSlot>> parties,
        ComputedPartySnapshots computedSnapshots)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("Game detected");

        var printedAny = false;
        foreach (var party in parties)
        {
            if (party.Count == 0)
                continue;

            var partyKey = party[0].GroupKey;
            if (!computedSnapshots.ByParty.TryGetValue(partyKey, out var snapshot) || snapshot.Members.Count == 0)
                continue;

            printedAny = true;
            ImGui.TextDisabled(party[0].GroupLabel);
            ImGui.Indent(10f);
            foreach (var member in snapshot.Members.OrderBy(member => member.Position))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                ImGui.TextWrapped(FormatDetectedPlayer(runDetail, member));
                ImGui.PopStyleColor();
            }

            ImGui.Unindent(10f);
        }

        if (!printedAny)
            ImGui.TextDisabled("No detected party players yet.");
    }

    private static string FormatDetectedPlayer(FullPartyRunDetail runDetail, FullPartyPartySnapshotMember member)
    {
        var name = ResolveCharacter(runDetail, member)?.Name ?? member.DisplayName;
        var classJob = NormalizeClassJob(member.ClassJob) ?? "unknown class";
        var phantomJob = string.IsNullOrWhiteSpace(member.PhantomJob)
            ? "unknown phantom job"
            : GetPhantomJobDisplayName(runDetail, member.PhantomJob);

        return $"{name} - {classJob} - {phantomJob}";
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
                ? "Validate: Occult mode, waiting for nearby or Adventurer List players."
                : $"Validate: Occult mode, {occultPresenceCount} nearby/Adventurer List players known, {occultPartyCount} parties identified by party leads.");
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
        var hasPhantomIcon = !string.IsNullOrWhiteSpace(result.PhantomJob) ||
                             (result.RosterSlot != null && HasPhantomJob(result.RosterSlot));
        if (hasPhantomIcon)
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

        if (phantomIconPosition != null && !DrawValidationPhantomJobIcon(drawList, runDetail, result, phantomIconPosition.Value))
        {
            DrawIconFallback(
                drawList,
                phantomIconPosition.Value,
                GetPhantomJobDisplayName(runDetail, result.PhantomJob ?? result.RosterSlot?.PhantomJob),
                ImGui.GetColorU32(ImGuiCol.TextDisabled));
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
                ["Missing from nearby players and Adventurer List."]);
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
                        ? $"Present in nearby players or Adventurer List, but not in the synced {plannedSlot.GroupLabel} party."
                        : $"Present in nearby players or Adventurer List; waiting for a party lead snapshot for {plannedSlot.GroupLabel}.");
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
            string? phantomJob = null;
            if (expectedObserved != null)
            {
                state = ValidationState.Warning;
                missingMessages.Add(useOccultPresence
                    ? $"Wrong place: currently in {FormatObservedLocation(runDetail, expectedObserved, false)}."
                    : $"Wrong party: currently in {FormatObservedLocation(runDetail, expectedObserved, true)}.");
                classJob = NormalizeClassJob(expectedObserved.Member.ClassJob);
                phantomJob = expectedObserved.Member.PhantomJob;
            }
            else if (expectedPresence != null)
            {
                state = ValidationState.Warning;
                classJob = NormalizeClassJob(expectedPresence.ClassJob);
                phantomJob = expectedPresence.PhantomJob;
                missingMessages.Add("Present in nearby players or Adventurer List; party position has not been synced yet.");
                AddClassValidationMessage(plannedSlot, classJob, missingMessages);
                AddPresencePhantomJobValidationMessage(runDetail, plannedSlot, expectedPresence.PhantomJob, missingMessages);
            }
            else
            {
                missingMessages.Add(useOccultPresence
                    ? "Missing from nearby players and Adventurer List."
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
                messages.Add("Expected player is present in nearby players or Adventurer List, but not in this slot.");
            }
            else
            {
                messages.Add(useOccultPresence
                    ? "Expected player is missing from nearby players and Adventurer List."
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
            actualMember.PhantomJob,
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
        const float avatarSize = 32f;
        var avatarPosition = ImGui.GetCursorScreenPos();
        var avatarDrawn = false;
        var path = plugin.ImageCache.GetImagePath(member.AvatarUrl, $"live-member-{member.UserId}");
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var texture = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
            if (texture != null)
            {
                ImGui.Dummy(new Vector2(avatarSize, avatarSize));
                ImGui.GetWindowDrawList().AddImageRounded(
                    texture.Handle,
                    avatarPosition,
                    avatarPosition + new Vector2(avatarSize, avatarSize),
                    Vector2.Zero,
                    Vector2.One,
                    ImGui.GetColorU32(ImGuiCol.Text),
                    avatarSize * 0.5f);
                avatarDrawn = true;
            }
        }

        if (!avatarDrawn)
        {
            ImGui.Dummy(new Vector2(avatarSize, avatarSize));
            ImGui.GetWindowDrawList().AddCircleFilled(
                avatarPosition + new Vector2(avatarSize * 0.5f),
                avatarSize * 0.5f,
                FullPartyModernPalette.Color(FullPartyModernPalette.Elevated));
        }

        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextUnformatted(member.DisplayName);
        if (!string.IsNullOrWhiteSpace(member.Location))
            ImGui.TextDisabled(member.Location);
        ImGui.EndGroup();
    }

    private void DrawLiveCommandStatus(FullPartyLiveMember member)
    {
        if (liveRoom.TryGetReadyCheckSummary(member, out var summary))
        {
            DrawReadyCheckSummary(summary);
            return;
        }

        var commandStatus = liveRoom.GetCommandStatus(member);
        ImGui.TextColored(GetLiveCommandStatusColor(commandStatus), commandStatus);
    }

    private static void DrawReadyCheckSummary(ReadyCheckSummary summary)
    {
        if (summary.Total == 0)
        {
            ImGui.TextDisabled("No responses");
            return;
        }

        ImGui.BeginGroup();
        DrawReadyCheckCounter($"{summary.Ready}/{summary.Total}", ReadyCheckGlyph.Check, new Vector4(0.35f, 0.92f, 0.55f, 1f));
        ImGui.SameLine(0f, 8f);
        DrawReadyCheckCounter(summary.NotReady.ToString(), ReadyCheckGlyph.Cross, new Vector4(1f, 0.42f, 0.42f, 1f));
        ImGui.SameLine(0f, 8f);
        DrawReadyCheckCounter(summary.Pending.ToString(), ReadyCheckGlyph.Clock, new Vector4(0.94f, 0.78f, 0.32f, 1f));
        ImGui.EndGroup();

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted($"Ready: {summary.Ready}/{summary.Total}");
            ImGui.TextUnformatted($"Not ready: {summary.NotReady}");
            ImGui.TextUnformatted($"Pending: {summary.Pending}");
            if (summary.Waiting > 0)
                ImGui.TextUnformatted($"Raw waiting: {summary.Waiting}");
            if (summary.Missing > 0)
                ImGui.TextUnformatted($"Missing: {summary.Missing}");
            if (summary.Unknown > 0)
                ImGui.TextUnformatted($"Unknown: {summary.Unknown}");
            ImGui.EndTooltip();
        }
    }

    private static void DrawReadyCheckCounter(string value, ReadyCheckGlyph glyph, Vector4 color)
    {
        ImGui.TextColored(color, value);
        ImGui.SameLine(0f, 4f);
        DrawReadyCheckGlyph(glyph, color);
    }

    private static void DrawReadyCheckGlyph(ReadyCheckGlyph glyph, Vector4 color)
    {
        const float size = 14f;
        var cursor = ImGui.GetCursorScreenPos() + new Vector2(0f, 2f);
        ImGui.Dummy(new Vector2(size, size));

        var drawList = ImGui.GetWindowDrawList();
        var iconColor = ImGui.ColorConvertFloat4ToU32(color);
        var shadowColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.42f));
        var center = cursor + new Vector2(size * 0.5f, size * 0.5f);

        switch (glyph)
        {
            case ReadyCheckGlyph.Check:
                drawList.AddLine(cursor + new Vector2(2.5f, 7.5f), cursor + new Vector2(5.5f, 10.5f), shadowColor, 3f);
                drawList.AddLine(cursor + new Vector2(5.5f, 10.5f), cursor + new Vector2(11.5f, 3.5f), shadowColor, 3f);
                drawList.AddLine(cursor + new Vector2(2.5f, 7.5f), cursor + new Vector2(5.5f, 10.5f), iconColor, 2f);
                drawList.AddLine(cursor + new Vector2(5.5f, 10.5f), cursor + new Vector2(11.5f, 3.5f), iconColor, 2f);
                break;
            case ReadyCheckGlyph.Cross:
                drawList.AddLine(cursor + new Vector2(3.5f, 3.5f), cursor + new Vector2(10.5f, 10.5f), shadowColor, 3f);
                drawList.AddLine(cursor + new Vector2(10.5f, 3.5f), cursor + new Vector2(3.5f, 10.5f), shadowColor, 3f);
                drawList.AddLine(cursor + new Vector2(3.5f, 3.5f), cursor + new Vector2(10.5f, 10.5f), iconColor, 2f);
                drawList.AddLine(cursor + new Vector2(10.5f, 3.5f), cursor + new Vector2(3.5f, 10.5f), iconColor, 2f);
                break;
            case ReadyCheckGlyph.Clock:
                drawList.AddCircle(center, 5.5f, shadowColor, 16, 2.5f);
                drawList.AddCircle(center, 5.5f, iconColor, 16, 1.6f);
                drawList.AddLine(center, center + new Vector2(0f, -3.5f), iconColor, 1.6f);
                drawList.AddLine(center, center + new Vector2(3f, 1.5f), iconColor, 1.6f);
                break;
        }
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
        if (DrawPhantomJobLocalIcon(drawList, slot.PhantomJob, position))
            return true;

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
        if (DrawPhantomJobLocalIcon(drawList, phantomJob, position))
            return true;

        var normalized = NormalizePhantomJob(phantomJob);
        if (normalized == null)
            return false;

        var sourceSlot = runDetail.Slots.FirstOrDefault(slot => NormalizePhantomJob(slot.PhantomJob) == normalized && HasPhantomJob(slot));
        return sourceSlot != null && DrawPhantomJobIcon(drawList, sourceSlot, position);
    }

    private static bool DrawPhantomJobLocalIcon(ImDrawListPtr drawList, string? phantomJob, Vector2 position)
    {
        var path = GetPhantomJobIconFilePath(phantomJob);
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var texture = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
        if (texture == null)
            return false;

        drawList.AddImage(texture.Handle, position, position + new Vector2(20, 20));
        return true;
    }

    private static bool DrawResurrectionStatusIcon(ImDrawListPtr drawList, int charges, Vector2 position)
    {
        if (charges is < 0 or > 3)
            return false;

        var statusId = charges == 0
            ? OccultCrescentStatusIds.ResurrectionDenied
            : OccultCrescentStatusIds.ResurrectionRestricted;
        if (!Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>().TryGetRow(statusId, out var status) || status.Icon == 0)
            return false;

        var texture = Plugin.TextureProvider
            .GetFromGameIcon(new GameIconLookup(status.Icon))
            .GetWrapOrDefault();
        if (texture == null)
            return false;

        drawList.AddImage(texture.Handle, position, position + new Vector2(20f, 20f));
        if (charges == 0)
            return true;

        var label = charges.ToString();
        var textSize = ImGui.CalcTextSize(label);
        var textPosition = position + new Vector2(19f - textSize.X, 18f - textSize.Y);
        var outline = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 1f));
        foreach (var offset in new[]
                 {
                     new Vector2(-1f, 0f), new Vector2(1f, 0f),
                     new Vector2(0f, -1f), new Vector2(0f, 1f),
                 })
        {
            drawList.AddText(textPosition + offset, outline, label);
        }

        drawList.AddText(textPosition, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f)), label);
        return true;
    }

    private static void DrawUnknownDetectionIcon(ImDrawListPtr drawList, Vector2 position)
    {
        const float size = 20f;
        var max = position + new Vector2(size, size);
        drawList.AddRectFilled(position, max, FullPartyModernPalette.Color(FullPartyModernPalette.Elevated), 3f);
        drawList.AddRect(position, max, FullPartyModernPalette.Color(FullPartyModernPalette.BorderSoft), 3f);

        const string label = "?";
        var textSize = ImGui.CalcTextSize(label);
        drawList.AddText(
            position + ((new Vector2(size, size) - textSize) * 0.5f),
            FullPartyModernPalette.Color(FullPartyModernPalette.Muted),
            label);
    }

    private static string? GetPhantomJobIconFilePath(string? phantomJob)
    {
        var token = NormalizePhantomJob(phantomJob);
        if (token == null)
            return null;

        var icons = GetPhantomJobIconFiles();
        return icons.TryGetValue(token, out var path) ? path : null;
    }

    private static IReadOnlyDictionary<string, string> GetPhantomJobIconFiles()
    {
        if (phantomJobIconFileCache != null)
            return phantomJobIconFileCache;

        var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pluginDirectory = Plugin.PluginInterface.AssemblyLocation.Directory?.FullName ?? AppContext.BaseDirectory;
        var iconDirectory = Path.Combine(pluginDirectory, "Data");
        if (!Directory.Exists(iconDirectory))
        {
            phantomJobIconFileCache = icons;
            return phantomJobIconFileCache;
        }

        foreach (var path in Directory.EnumerateFiles(iconDirectory, "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Equals("icon", StringComparison.OrdinalIgnoreCase))
                continue;

            AddPhantomJobIconAlias(icons, name, path);
            AddPhantomJobIconAlias(icons, $"Phantom {name}", path);

            if (name.Equals("Berzerker", StringComparison.OrdinalIgnoreCase))
            {
                AddPhantomJobIconAlias(icons, "Berserker", path);
                AddPhantomJobIconAlias(icons, "Phantom Berserker", path);
            }
        }

        phantomJobIconFileCache = icons;
        return phantomJobIconFileCache;
    }

    private static void AddPhantomJobIconAlias(IDictionary<string, string> icons, string name, string path)
    {
        var token = NormalizePhantomJob(name);
        if (token != null)
            icons.TryAdd(token, path);
    }

    private bool DrawValidationPhantomJobIcon(
        ImDrawListPtr drawList,
        FullPartyRunDetail runDetail,
        ValidationSlotResult result,
        Vector2 position)
    {
        if (!string.IsNullOrWhiteSpace(result.PhantomJob) &&
            DrawPhantomJobIconByName(drawList, runDetail, result.PhantomJob, position))
        {
            return true;
        }

        if (result.RosterSlot == null || !HasPhantomJob(result.RosterSlot))
            return false;

        if (!string.IsNullOrWhiteSpace(result.PhantomJob) &&
            !PhantomJobsMatch(result.PhantomJob, result.RosterSlot.PhantomJob))
        {
            return false;
        }

        return DrawPhantomJobIcon(drawList, result.RosterSlot, position);
    }

    private static bool PhantomJobsMatch(string? left, string? right)
    {
        var normalizedLeft = NormalizePhantomJob(left);
        var normalizedRight = NormalizePhantomJob(right);
        return normalizedLeft != null &&
               normalizedRight != null &&
               normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase);
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
        return ClassJobResolver.Normalize(classNameOrShorthand);
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

        var resolvedName = PhantomJobResolver.Normalize(phantomJob) ?? phantomJob;
        var token = new string(resolvedName
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
        if (inOccult)
            UpdateNearbyDetectionCache(runDetail);

        var occultPresence = inOccult
            ? BuildOccultPresence(runDetail, requestOccultRefresh, nearbyDetectionCache)
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
        if (inOccult)
        {
            snapshotsByParty = snapshotsByParty.ToDictionary(
                pair => pair.Key,
                pair => ApplyNearbyDetection(runDetail, pair.Value, nearbyDetectionCache),
                StringComparer.OrdinalIgnoreCase);
        }
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
            plugin.AdventurerList.ResetForOccultVisit();
        }

        nearbyDetectionCache = GamePresenceList.Empty;

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

    private GamePresenceList BuildOccultPresence(
        FullPartyRunDetail runDetail,
        bool requestOccultRefresh,
        GamePresenceList nearbyPresence)
    {
        if (requestOccultRefresh &&
            !plugin.AdventurerList.HasRequestedRefresh &&
            !plugin.AdventurerList.IsRefreshing &&
            RunValidationSources.HasMissingActiveRosterPresence(runDetail, nearbyPresence))
        {
            plugin.AdventurerList.RequestRefresh();
        }

        return RunValidationSources.MergePresence(
            plugin.AdventurerList.GetPresence(runDetail),
            nearbyPresence);
    }

    private GamePresenceList UpdateNearbyDetectionCache(FullPartyRunDetail runDetail)
    {
        nearbyDetectionCache = RunValidationSources.MergePresence(
            nearbyDetectionCache,
            RunValidationSources.BuildNearbyPlayerPresence(runDetail));
        return nearbyDetectionCache;
    }

    private static FullPartyPartySnapshot ApplyNearbyDetection(
        FullPartyRunDetail runDetail,
        FullPartyPartySnapshot snapshot,
        GamePresenceList nearbyPresence)
    {
        var members = snapshot.Members
            .Select(member =>
            {
                var character = ResolveCharacter(runDetail, member);
                var found = character != null
                    ? nearbyPresence.TryFind(character, out var detection)
                    : nearbyPresence.TryFind(member.Name, member.World, out detection);
                if (!found)
                    return member;

                return member with
                {
                    ClassJob = detection.ClassJob ?? member.ClassJob,
                    PhantomJob = detection.PhantomJob ?? member.PhantomJob,
                    ResurrectionCharges = detection.ResurrectionCharges ?? member.ResurrectionCharges,
                };
            })
            .ToList();

        return snapshot with { Members = members };
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
               slot.PhantomJobIconUrls.Count > 0;
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
            "Executed" or "Ready" => new Vector4(0.35f, 0.92f, 0.55f, 1f),
            "Received" => new Vector4(0.42f, 0.72f, 1f, 1f),
            "Waiting" => new Vector4(0.94f, 0.78f, 0.32f, 1f),
            "Failed" or "Not Ready" or "Expired" => new Vector4(1f, 0.42f, 0.42f, 1f),
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
            SetCheckInStatus("Run check-in unavailable: roster is not loaded yet.", true);
            return;
        }

        if (OccultCrescentTerritory.IsCurrent() && !plugin.AdventurerList.HasRequestedRefresh)
        {
            var nearbyPresence = RunValidationSources.BuildNearbyPlayerPresence(detail);
            if (RunValidationSources.HasMissingActiveRosterPresence(detail, nearbyPresence))
            {
                plugin.AdventurerList.RequestRefresh();
                SetCheckInStatus("Nearby check still has missing roster players, so Adventurer List is refreshing. Press Run Check-In again once it finishes.");
                return;
            }
        }

        var selection = BuildRunCheckInSelection(detail);
        if (selection.PresentCount == 0)
        {
            SetCheckInStatus($"Run check-in skipped: no present roster players found, {selection.MissingCount} missing.", true);
            return;
        }

        SetCheckInStatus($"Submitting check-in for {selection.PresentCount} present roster players...");
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
            SetCheckInStatus("Run check-in cancelled.");
            return;
        }

        if (task.Exception != null)
        {
            var exception = task.Exception.GetBaseException();
            Plugin.Log.Warning(exception, "FullParty run {RunId} check-in failed.", Run.Id);
            SetCheckInStatus($"Run check-in failed: {exception.Message}", true);
            return;
        }

        var summary = task.Result;
        SetCheckInStatus($"Run check-in complete: {summary.CheckedInCount} checked in, {summary.MissingCount} missing.");
        RefreshRun();
    }

    private void SetCheckInStatus(string message, bool isError = false)
    {
        if (!isError && string.Equals(checkInStatusMessage, message, StringComparison.Ordinal))
            return;

        checkInStatusMessage = message;
        if (isError)
        {
            Plugin.Log.Warning("FullParty run {RunId} check-in: {Status}", Run.Id, message);
            Plugin.ShowErrorToast(message);
            return;
        }

        Plugin.Log.Information("FullParty run {RunId} check-in: {Status}", Run.Id, message);
    }

    private RunCheckInSelection BuildRunCheckInSelection(FullPartyRunDetail runDetail)
    {
        var presence = OccultCrescentTerritory.IsCurrent()
            ? BuildOccultPresence(runDetail, false, UpdateNearbyDetectionCache(runDetail))
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
