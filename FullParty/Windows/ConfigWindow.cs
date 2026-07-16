using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using ElezenTools.UI;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FullParty.Models;
using FullParty.Services;
using Lumina.Excel.Sheets;

namespace FullParty.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly Plugin plugin;
    private string? localCommandStatus;
    private bool windowStylePushed;

    // We give this window a constant ID using ###.
    // This allows for labels to be dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(Plugin plugin) : base("FullParty Debug###FullPartyDebug")
    {
        Flags = ImGuiWindowFlags.NoCollapse;

        Size = new Vector2(620, 520);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        ModernWindowStyle.PushTitleBar();
        windowStylePushed = true;

        // Flags must be added or removed before Draw() is being called, or they won't apply
        if (configuration.IsConfigWindowMovable)
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoMove;
        }

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

        FullPartyModernPalette.SectionHeader(FontAwesomeIcon.Cog, "Settings");

        var movable = configuration.IsConfigWindowMovable;
        if (ImGui.Checkbox("Movable", ref movable))
        {
            configuration.IsConfigWindowMovable = movable;
            configuration.Save();
        }

        var showLiveRoomData = configuration.ShowLiveRoomData;
        if (ImGui.Checkbox("Show Liveroom Data", ref showLiveRoomData))
        {
            configuration.ShowLiveRoomData = showLiveRoomData;
            configuration.Save();
        }

        var bypassLiveCommandRequirements = configuration.BypassLiveCommandRequirements;
        if (ImGui.Checkbox("Bypass live command requirements", ref bypassLiveCommandRequirements))
        {
            configuration.BypassLiveCommandRequirements = bypassLiveCommandRequirements;
            configuration.Save();
        }

        if (configuration.BypassLiveCommandRequirements)
        {
            ImGui.TextWrapped("Debug only: while connected to a live room, Check Leads, Check Parties, and Countdown ignore local Occult/party/target detection checks.");
        }

        var auth = plugin.AuthService;
        var environment = plugin.Environment;
        FullPartyModernPalette.SectionHeader(FontAwesomeIcon.InfoCircle, "Auth Debug");
        ImGui.Text($"Debug: {environment.Debug}");
        ImGui.Text($"State: {auth.State}");
        ImGui.TextWrapped($"Base URL: {auth.BaseUrl}");
        ImGui.TextWrapped($"Client ID: {MaskClientId(auth.ClientId)}");

        if (!string.IsNullOrWhiteSpace(auth.UserCode))
        {
            ImGui.Text($"User code: {auth.UserCode}");
        }

        if (!string.IsNullOrWhiteSpace(auth.VerificationUriComplete))
        {
            ImGui.TextWrapped($"Verification URL: {auth.VerificationUriComplete}");
        }
        else if (!string.IsNullOrWhiteSpace(auth.VerificationUri))
        {
            ImGui.TextWrapped($"Verification URL: {auth.VerificationUri}");
        }

        if (auth.DeviceCodeExpiresAt is { } expiresAt)
        {
            var remaining = expiresAt - DateTimeOffset.UtcNow;
            ImGui.TextDisabled($"Device code expires in {Math.Max(0, (int)remaining.TotalSeconds)}s");
        }

        if (auth.PollIntervalSeconds > 0)
        {
            ImGui.TextDisabled($"Polling every {auth.PollIntervalSeconds}s");
        }

        if (!string.IsNullOrWhiteSpace(auth.ErrorMessage))
        {
            ImGui.TextWrapped($"Error: {auth.ErrorMessage}");
        }

        if (auth.User != null)
        {
            ImGui.TextWrapped($"Logged in as: {auth.User.Name}");
        }
        else if (auth.PendingUser != null)
        {
            ImGui.TextWrapped($"Pending user: {auth.PendingUser.Name}");
        }

        ImGui.Spacing();
        if (ImGui.Button("Restart login"))
        {
            auth.Restart();
        }

        ImGui.SameLine();
        if (ImGui.Button("Sign out"))
        {
            auth.SignOut();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        FullPartyModernPalette.SectionHeader(FontAwesomeIcon.InfoCircle, "Local Command Tests");
        ImGui.TextDisabled("Runs the vanilla game command locally without websocket.");

        if (ImGui.Button("Test /readycheck"))
        {
            RunLocalCommandTest("/readycheck", "ready check");
        }

        ImGui.SameLine();
        if (ImGui.Button("Test /countdown 20"))
        {
            RunLocalCommandTest("/countdown 20", "countdown");
        }

        if (!string.IsNullOrWhiteSpace(localCommandStatus))
        {
            ImGui.TextWrapped(localCommandStatus);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawTerritoryDebug();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawStatusDebug();
    }

    private static unsafe void DrawTerritoryDebug()
    {
        if (!ImGui.CollapsingHeader("Territory / Instance Debug", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var territory = OccultCrescentTerritory.GetCurrentDebugInfo();
        var isForkedTower = OccultCrescentStatusIds.IsForkedTowerContext();
        var forkedTowerSource = territory.IsForkedTower
            ? $"runtime map ID {Plugin.ClientState.MapId}"
            : OccultCrescentStatusIds.HasDutiesAsAssigned()
                ? $"Duties as Assigned status {OccultCrescentStatusIds.DutiesAsAssigned}"
                : "none";
        ImGui.Text($"Current territory ID: {territory.TerritoryId}");
        ImGui.Text($"Detected as Occult Crescent: {(territory.IsOccultCrescent ? "Yes" : "No")}");
        ImGui.Text($"Detected as Forked Tower: {(isForkedTower ? "Yes" : "No")}");
        ImGui.TextWrapped($"Forked Tower source: {forkedTowerSource}");
        ImGui.TextWrapped($"Match source: {territory.MatchSource}");
        ImGui.Text($"PlaceName row ID: {territory.PlaceNameRowId}");
        ImGui.Text($"Runtime map ID: {Plugin.ClientState.MapId}");
        ImGui.Text($"Territory default map row ID: {territory.DefaultMapId}");
        ImGui.TextDisabled(
            $"Forked Tower detection: territory {OccultCrescentTerritory.SouthHornTerritoryId}, runtime maps {string.Join(", ", OccultCrescentTerritory.ForkedTowerMapIds)}.");

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer != null)
        {
            var position = localPlayer.Position;
            ImGui.Text($"Player position: X={position.X:F2}, Y={position.Y:F2}, Z={position.Z:F2}");
        }
        else
        {
            ImGui.TextDisabled("Player position: unavailable");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Current duty / instance:");
        var contentFinderCondition = Plugin.DutyState.ContentFinderCondition;
        var contentFinderConditionId = contentFinderCondition.RowId;
        ImGui.Text($"ContentFinderCondition row ID: {contentFinderConditionId}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy ID##copy_content_finder_condition"))
            ImGui.SetClipboardText(contentFinderConditionId.ToString());

        ImGui.Text($"Duty started: {(Plugin.DutyState.IsDutyStarted ? "Yes" : "No")}");
        ImGui.TextDisabled("Territory and duty IDs may remain South Horn inside nested content; compare runtime map ID and position too.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Runtime content director:");
        try
        {
            var currentContentId = EventFramework.GetCurrentContentId();
            var currentContentType = EventFramework.GetCurrentContentType();
            ImGui.Text($"Current content ID: {currentContentId}");
            ImGui.Text($"Current content type: {(int)currentContentType} ({currentContentType})");

            var eventFramework = EventFramework.Instance();
            var massiveDirector = eventFramework == null ? null : eventFramework->GetMassivePcContentDirector();
            if (massiveDirector == null)
            {
                ImGui.TextDisabled("Massive PC Content director: unavailable");
            }
            else
            {
                ImGui.Text($"Massive PC director content ID: {massiveDirector->ContentId}");
                ImGui.Text($"Massive PC director sequence: {massiveDirector->Sequence}");
                ImGui.Text($"Massive PC director flags: {massiveDirector->ContentFlags}");
            }
        }
        catch (Exception ex)
        {
            ImGui.TextWrapped($"Could not read runtime content director: {ex.Message}");
        }

        if (contentFinderConditionId != 0)
        {
            try
            {
                var englishSheet = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>(ClientLanguage.English);
                var englishName = englishSheet.TryGetRow(contentFinderConditionId, out var row)
                    ? row.Name.ToString()
                    : "(row not found)";
                ImGui.TextWrapped($"English duty name: {englishName}");
            }
            catch (Exception ex)
            {
                ImGui.TextWrapped($"Could not read ContentFinderCondition: {ex.Message}");
            }
        }
        else
        {
            ImGui.TextDisabled("English duty name: (no active ContentFinderCondition)");
        }

        if (!string.IsNullOrWhiteSpace(territory.DirectPlaceName))
        {
            ImGui.TextWrapped($"Direct PlaceName.Value: {territory.DirectPlaceName}");
        }

        if (territory.PlaceNames.Count > 0)
        {
            ImGui.Text("PlaceName by language:");
            foreach (var (language, placeName) in territory.PlaceNames)
            {
                ImGui.BulletText($"{language}: {(string.IsNullOrWhiteSpace(placeName) ? "(empty)" : placeName)}");
            }
        }

        if (!string.IsNullOrWhiteSpace(territory.Error))
        {
            ImGui.TextWrapped($"Error: {territory.Error}");
        }
    }

    private static void DrawStatusDebug()
    {
        if (!ImGui.CollapsingHeader("Status Debug"))
            return;

        ImGui.TextDisabled("Active game statuses used for phantom job detection.");

        DrawLocalStatusDebug();
        DrawPartyStatusDebug();
    }

    private static void DrawLocalStatusDebug()
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var label = localPlayer == null
            ? "Local Player"
            : $"{localPlayer.Name} @ {localPlayer.HomeWorld.Value.Name}";

        DrawStatusList(label, PartySnapshotBuilder.GetStatusDebugList(localPlayer?.StatusList));
    }

    private static void DrawPartyStatusDebug()
    {
        try
        {
            foreach (var member in Plugin.PartyList)
            {
                var name = member.Name.ToString();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                DrawStatusList(
                    $"{name} @ {member.World.Value.Name}",
                    PartySnapshotBuilder.GetStatusDebugList(member.Statuses));
            }
        }
        catch (Exception ex)
        {
            ImGui.TextWrapped($"Could not read party status data: {ex.Message}");
        }
    }

    private static void DrawStatusList(string label, IReadOnlyList<FullPartyStatusDebug> statuses)
    {
        if (!ImGui.TreeNode(label))
            return;

        if (statuses.Count == 0)
        {
            ImGui.TextDisabled("No active statuses.");
            ImGui.TreePop();
            return;
        }

        foreach (var status in statuses.OrderBy(status => status.StatusId))
        {
            ImGui.TextWrapped($"{status.StatusId} - {status.StatusName}");
        }

        ImGui.TreePop();
    }

    private void RunLocalCommandTest(string command, string label)
    {
        try
        {
            GameCommandExecutor.Execute(command);
            localCommandStatus = $"Executed local {label}.";
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not execute local FullParty test command {Command}.", command);
            localCommandStatus = $"Failed to execute local {label}: {ex.Message}";
        }
    }

    private static string MaskClientId(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return "not set";

        if (clientId.Length <= 8)
            return "set";

        return $"{clientId[..8]}...{clientId[^4..]}";
    }
}
