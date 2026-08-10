using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using ElezenTools.UI;

namespace FullParty.Windows;

public sealed class SettingsWindow : Window
{
    private readonly Configuration configuration;
    private bool windowStylePushed;

    public SettingsWindow(Plugin plugin)
        : base("FullParty Settings###FullPartySettings")
    {
        configuration = plugin.Configuration;
        Flags = ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(440f, 390f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360f, 300f),
            MaximumSize = new Vector2(720f, 600f),
        };
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
        using var palette = ModernWindowStyle.PushContentPalette();
        FullPartyModernPalette.SectionHeader(FontAwesomeIcon.Cog, "General");

        var movableStatus = configuration.MovableLiveRoomStatus;
        if (ImGui.Checkbox("Movable Live Room Status", ref movableStatus))
        {
            configuration.MovableLiveRoomStatus = movableStatus;
            configuration.Save();
        }

        ImGui.Spacing();

        var obviousReadyCheck = configuration.ObviousReadyCheck;
        if (ImGui.Checkbox("OBVIOUS READYCHECK (For Giki)", ref obviousReadyCheck))
        {
            configuration.ObviousReadyCheck = obviousReadyCheck;
            configuration.Save();
        }

        ImGui.Spacing();

        var rosterHidden = configuration.RosterHiddenByDefault;
        if (ImGui.Checkbox("Roster Hidden By Default", ref rosterHidden))
        {
            configuration.RosterHiddenByDefault = rosterHidden;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        FullPartyModernPalette.SectionHeader(FontAwesomeIcon.VolumeUp, "Sounds");

        var mutePartyLeadReadyCheck = configuration.MutePartyLeadReadyCheck;
        if (ImGui.Checkbox("Mute Partylead Readycheck", ref mutePartyLeadReadyCheck))
        {
            configuration.MutePartyLeadReadyCheck = mutePartyLeadReadyCheck;
            configuration.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Mutes the lead-check opening cue and each lead's ready or not-ready result.");

        ImGui.Spacing();

        var muteAllianceAllReady = configuration.MuteAllianceAllReadySound;
        if (ImGui.Checkbox("Mute Alliance Readycheck (All Ready)", ref muteAllianceAllReady))
        {
            configuration.MuteAllianceAllReadySound = muteAllianceAllReady;
            configuration.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Mutes the final bongos played after every connected party reports all ready.");

        ImGui.Spacing();

        var muteAlliancePerParty = configuration.MuteAlliancePerPartyReadyCheck;
        if (ImGui.Checkbox("Mute Alliance Readycheck (Per Party)", ref muteAlliancePerParty))
        {
            configuration.MuteAlliancePerPartyReadyCheck = muteAlliancePerParty;
            configuration.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Mutes the success or failure cue played as each party finishes its ready check.");
    }
}