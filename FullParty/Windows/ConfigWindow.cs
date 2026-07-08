using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FullParty.Models;
using FullParty.Services;

namespace FullParty.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly Plugin plugin;
    private string? localCommandStatus;

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
        // Flags must be added or removed before Draw() is being called, or they won't apply
        if (configuration.IsConfigWindowMovable)
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoMove;
        }
    }

    public override void Draw()
    {
        ImGui.Text("Settings");

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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var auth = plugin.AuthService;
        var environment = plugin.Environment;
        ImGui.Text("Auth Debug");
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

        ImGui.Text("Local Command Tests");
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

        DrawStatusDebug();
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
