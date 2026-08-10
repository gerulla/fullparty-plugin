using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using ElezenTools.UI;
using FullParty.Services;

namespace FullParty.Windows;

public sealed class ReadyCheckSimulationWindow : Window
{
    private static readonly Vector4 SuccessColor = new(0.35f, 0.92f, 0.55f, 1f);
    private static readonly Vector4 FailureColor = new(1f, 0.42f, 0.42f, 1f);
    private static readonly Vector4 WaitingColor = new(0.94f, 0.78f, 0.32f, 1f);

    private readonly Plugin plugin;
    private readonly List<SimulatedPartyResult> parties = [];
    private DateTimeOffset startedAt;
    private DateTimeOffset? completedAt;
    private bool allianceReadySoundPlayed;
    private bool windowStylePushed;

    public ReadyCheckSimulationWindow(Plugin plugin)
        : base("Party Ready Check Simulation###FullPartyReadyCheckSimulation")
    {
        this.plugin = plugin;
        Flags = ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(500f, 330f);
        SizeCondition = ImGuiCond.Appearing;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(390f, 280f),
            MaximumSize = new Vector2(700f, 500f),
        };
    }

    public void Start(bool allReady)
    {
        parties.Clear();
        var failedParties = new HashSet<int>();
        if (!allReady)
        {
            var failureCount = Random.Shared.Next(1, 7);
            foreach (var index in Enumerable.Range(0, 6)
                         .OrderBy(_ => Random.Shared.Next())
                         .Take(failureCount))
            {
                failedParties.Add(index);
            }
        }

        for (var index = 0; index < 6; index++)
        {
            var declined = failedParties.Contains(index)
                ? Random.Shared.Next(1, 4)
                : 0;
            parties.Add(new SimulatedPartyResult(
                $"Party {(char)('A' + index)}",
                declined,
                TimeSpan.FromMilliseconds(850 + (index * 720) + Random.Shared.Next(0, 220))));
        }

        startedAt = DateTimeOffset.UtcNow;
        completedAt = null;
        allianceReadySoundPlayed = false;
        IsOpen = true;
    }

    public override void PreDraw()
    {
        ModernWindowStyle.PushTitleBar();
        windowStylePushed = true;
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
        UpdateSimulation();

        using var palette = ModernWindowStyle.PushContentPalette();
        FullPartyModernPalette.SectionHeader(FontAwesomeIcon.Users, "Alliance Ready Check");
        ImGui.TextDisabled("Each party finishes independently. This preview closes shortly after the final result.");
        ImGui.Spacing();

        if (!ImGui.BeginTable(
                "##fullparty_ready_check_simulation",
                3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("Party", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Progress", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableHeadersRow();

        foreach (var party in parties)
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, 32f);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(party.Name);

            ImGui.TableNextColumn();
            if (party.IsComplete)
            {
                ImGui.TextDisabled("8 / 8 responded");
            }
            else
            {
                var elapsed = DateTimeOffset.UtcNow - startedAt;
                var progress = Math.Clamp(elapsed.TotalMilliseconds / party.CompletesAfter.TotalMilliseconds, 0d, 0.92d);
                var responses = (int)Math.Floor(8 * progress);
                ImGui.TextColored(WaitingColor, $"{responses}/8   {8 - responses} waiting");
            }

            ImGui.TableNextColumn();
            if (!party.IsComplete)
                ImGui.TextColored(WaitingColor, "Waiting");
            else if (party.Declined > 0)
                ImGui.TextColored(FailureColor, $"{party.Declined} Declined");
            else
                ImGui.TextColored(SuccessColor, "All ready");
        }

        ImGui.EndTable();
    }

    private void UpdateSimulation()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var party in parties.Where(party => !party.IsComplete && now - startedAt >= party.CompletesAfter))
        {
            party.IsComplete = true;
            ReadyCheckSoundPlayer.PlayAlliancePartyResult(plugin.Configuration, party.Declined == 0);
            Plugin.Log.Information(
                "Ready-check simulation finalized {Party}: {Result}.",
                party.Name,
                party.Declined == 0 ? "all ready" : $"{party.Declined} declined");
        }

        if (parties.Count == 0 || parties.Any(party => !party.IsComplete))
            return;

        completedAt ??= now;
        if (!allianceReadySoundPlayed &&
            parties.All(party => party.Declined == 0) &&
            now - completedAt.Value >= TimeSpan.FromSeconds(1))
        {
            allianceReadySoundPlayed = true;
            ReadyCheckSoundPlayer.PlayAllianceReady(plugin.Configuration);
            Plugin.Log.Information(
                "Ready-check simulation finalized all six parties as ready. Played alliance-ready bongos.");
        }

        if (now - completedAt >= TimeSpan.FromSeconds(5))
            IsOpen = false;
    }

    private sealed record SimulatedPartyResult(string Name, int Declined, TimeSpan CompletesAfter)
    {
        public bool IsComplete { get; set; }
    }
}
