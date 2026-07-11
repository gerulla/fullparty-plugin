using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ElezenTools.UI;
using FullParty.Models;

namespace FullParty.Windows;

internal sealed class RunMiniWindow : Window
{
    private readonly RunWindow owner;
    private bool windowStylePushed;

    public RunMiniWindow(RunWindow owner, FullPartyRun run)
        : base($"{run.Name} - Mini##FullPartyRunMini{run.Id}")
    {
        this.owner = owner;
        Size = new Vector2(200f, 520f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(180f, 220f),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        IsOpen = false;
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
        owner.DrawMiniContent();
    }
}
