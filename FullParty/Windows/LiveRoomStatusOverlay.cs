using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FullParty.Services;

namespace FullParty.Windows;

public sealed unsafe class LiveRoomStatusOverlay : Window, IDisposable
{
    private const float HorizontalPadding = 8f;
    private const float VerticalPadding = 4f;
    private const float AccentWidth = 3f;
    private const float OverlayGap = -10f;
    private const float BadgeGap = 3f;

    private readonly Plugin plugin;
    private bool stylePushed;

    public LiveRoomStatusOverlay(Plugin plugin)
        : base("FullParty Live Room Status##FullPartyLiveRoomStatusOverlay")
    {
        this.plugin = plugin;
        IsOpen = true;
        RespectCloseHotkey = false;
        Flags = ImGuiWindowFlags.NoDecoration |
                ImGuiWindowFlags.NoInputs |
                ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoFocusOnAppearing |
                ImGuiWindowFlags.NoNav |
                ImGuiWindowFlags.NoBackground;
    }

    public void Dispose()
    {
    }

    public override bool DrawConditions()
    {
        return plugin.LiveRoomManager.GetOverlayStatus() != null;
    }

    public override void PreDraw()
    {
        var status = plugin.LiveRoomManager.GetOverlayStatus();
        if (status == null)
            return;

        var statusSize = GetPillSize(status.Text);
        var feedbackSize = status.Feedback == null ? Vector2.Zero : GetPillSize(status.Feedback.Text);
        var width = MathF.Max(statusSize.X, feedbackSize.X);
        var height = statusSize.Y + (status.Feedback == null ? 0f : feedbackSize.Y + BadgeGap);

        var position = TryGetPartyListBounds(out var min, out _)
            ? new Vector2(min.X, MathF.Max(0f, min.Y - statusSize.Y - OverlayGap - (status.Feedback == null ? 0f : feedbackSize.Y + BadgeGap)))
            : ImGui.GetMainViewport().WorkPos + new Vector2(16f, 140f);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        stylePushed = true;
        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, height), ImGuiCond.Always);
    }

    public override void PostDraw()
    {
        if (!stylePushed)
            return;

        ImGui.PopStyleVar();
        stylePushed = false;
    }

    public override void Draw()
    {
        var status = plugin.LiveRoomManager.GetOverlayStatus();
        if (status == null)
            return;

        var y = 0f;
        if (status.Feedback != null)
        {
            DrawPill(status.Feedback.Text, GetFeedbackColor(status.Feedback.Kind), y);
            y += GetPillSize(status.Feedback.Text).Y + BadgeGap;
        }

        DrawPill(status.Text, GetStatusColor(status.State), y);

        if (!string.IsNullOrWhiteSpace(status.Detail) && ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
        {
            ImGui.SetTooltip(status.Detail);
        }
    }

    private static Vector2 GetPillSize(string text)
    {
        var textSize = ImGui.CalcTextSize(text);
        return new Vector2(textSize.X + (HorizontalPadding * 2f) + AccentWidth, textSize.Y + (VerticalPadding * 2f));
    }

    private static Vector4 GetStatusColor(RealtimeRunRoomState state)
    {
        return state switch
        {
            RealtimeRunRoomState.Connected => FullPartyModernPalette.Success,
            RealtimeRunRoomState.Error => FullPartyModernPalette.Danger,
            RealtimeRunRoomState.Disconnected => FullPartyModernPalette.Muted,
            _ => FullPartyModernPalette.Brand,
        };
    }

    private static Vector4 GetFeedbackColor(LiveRoomFeedbackKind kind)
    {
        return kind switch
        {
            LiveRoomFeedbackKind.Success => FullPartyModernPalette.Success,
            LiveRoomFeedbackKind.Warning => new Vector4(0.92f, 0.70f, 0.24f, 1f),
            LiveRoomFeedbackKind.Error => FullPartyModernPalette.Danger,
            _ => FullPartyModernPalette.Brand,
        };
    }

    private static void DrawPill(string text, Vector4 color, float yOffset)
    {
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos() + new Vector2(0f, yOffset);
        var size = GetPillSize(text);
        var max = min + size;
        drawList.AddRectFilled(min, max, FullPartyModernPalette.Color(FullPartyModernPalette.Surface with { W = 0.88f }), 4f);
        drawList.AddRectFilled(min, new Vector2(min.X + AccentWidth, max.Y), FullPartyModernPalette.Color(color), 4f);
        drawList.AddRect(min, max, FullPartyModernPalette.Color(FullPartyModernPalette.BorderSoft), 4f);
        drawList.AddText(min + new Vector2(HorizontalPadding + AccentWidth, VerticalPadding), FullPartyModernPalette.Color(color), text);
    }

    private static AtkUnitBase* GetPartyListAddon()
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null)
            return null;

        var atkModule = uiModule->GetRaptureAtkModule();
        if (atkModule == null)
            return null;

        var addon = GetAddonByName(atkModule, "_PartyList");
        if (addon != null && addon->IsVisible)
            return addon;

        addon = GetAddonByName(atkModule, "PartyList");
        if (addon != null && addon->IsVisible)
            return addon;

        return null;
    }

    private static AtkUnitBase* GetAddonByName(RaptureAtkModule* atkModule, string name)
    {
        var bytes = stackalloc byte[name.Length + 1];
        for (var i = 0; i < name.Length; i++)
            bytes[i] = (byte)name[i];
        bytes[name.Length] = 0;

        return atkModule->RaptureAtkUnitManager.GetAddonByName(bytes, 1);
    }

    private static bool TryGetPartyListBounds(out Vector2 min, out Vector2 max)
    {
        min = default;
        max = default;

        var addon = GetPartyListAddon();
        if (addon == null || addon->RootNode == null)
            return false;

        var scale = addon->Scale <= 0f ? 1f : addon->Scale;
        var width = MathF.Max(160f, addon->RootNode->Width * scale);
        var height = MathF.Max(60f, addon->RootNode->Height * scale);
        min = new Vector2(addon->X, addon->Y);
        max = min + new Vector2(width, height);
        return true;
    }
}
