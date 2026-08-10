using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using ElezenTools.UI;

namespace FullParty.Windows;

public sealed class DevWindow : Window
{
    private readonly Plugin plugin;
    private bool windowStylePushed;

    public DevWindow(Plugin plugin)
        : base("FullParty Dev Tools###FullPartyDevTools")
    {
        this.plugin = plugin;
        Flags = ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(430f, 360f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(350f, 300f),
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
        FullPartyModernPalette.SectionHeader(FontAwesomeIcon.Wrench, "Ready Check Previews");
        ImGui.TextWrapped("Local UI previews only. These do not connect to a live room or send anything to FullParty.");
        ImGui.Spacing();

        var buttonWidth = ImGui.GetContentRegionAvail().X;
        if (ImGui.Button("Test Native-style Ready Check", new Vector2(buttonWidth, 30f)))
            plugin.ShowReadyCheckPreview(ReadyCheckPreviewStyle.Native);

        if (ImGui.Button("Test OBVIOUS READYCHECK", new Vector2(buttonWidth, 30f)))
            plugin.ShowReadyCheckPreview(ReadyCheckPreviewStyle.Obvious);

        if (ImGui.Button("Test Small Ready Check Prompt", new Vector2(buttonWidth, 30f)))
            plugin.ShowReadyCheckPreview(ReadyCheckPreviewStyle.Small);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        FullPartyModernPalette.SectionHeader(FontAwesomeIcon.VolumeUp, "Party Result Simulation");

        if (ImGui.Button("Simulate 6 Parties: All Ready", new Vector2(buttonWidth, 30f)))
            plugin.StartReadyCheckSimulation(true);

        if (ImGui.Button("Simulate 6 Parties: Mixed Results", new Vector2(buttonWidth, 30f)))
            plugin.StartReadyCheckSimulation(false);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Dismiss Test Overlays", new Vector2(buttonWidth, 30f)))
            plugin.DismissReadyCheckPreview();
    }
}
