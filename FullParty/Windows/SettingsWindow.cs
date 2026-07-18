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
        Size = new Vector2(440f, 220f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360f, 140f),
            MaximumSize = new Vector2(720f, 500f),
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
        FullPartyModernPalette.SectionHeader(FontAwesomeIcon.Cog, "Settings");

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
    }
}
