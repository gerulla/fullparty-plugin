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
    private static readonly Vector2 PromptSize = new(300f, 88f);
    private static readonly Vector2 ObviousPromptSize = new(440f, 154f);

    private readonly Plugin plugin;
    private bool stylePushed;
    private bool overlayWasDragged;

    public LiveRoomStatusOverlay(Plugin plugin)
        : base("FullParty Live Room Status##FullPartyLiveRoomStatusOverlay")
    {
        this.plugin = plugin;
        IsOpen = true;
        RespectCloseHotkey = false;
        Flags = ImGuiWindowFlags.NoDecoration |
                ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoFocusOnAppearing |
                ImGuiWindowFlags.NoBringToFrontOnFocus |
                ImGuiWindowFlags.NoNav |
                ImGuiWindowFlags.NoBackground;
    }

    public void Dispose()
    {
    }

    public override bool DrawConditions()
    {
        return plugin.LiveRoomManager.GetOverlayStatus() != null ||
               plugin.LiveRoomManager.GetOverlayReadyCheckPrompt() != null;
    }

    public override void PreDraw()
    {
        var status = plugin.LiveRoomManager.GetOverlayStatus();
        var prompt = plugin.LiveRoomManager.GetOverlayReadyCheckPrompt();
        if (status == null && prompt == null)
            return;

        var obviousPrompt = plugin.Configuration.ObviousReadyCheck && prompt != null;
        if (obviousPrompt)
        {
            Flags |= ImGuiWindowFlags.NoSavedSettings;
            Flags |= ImGuiWindowFlags.NoMove;
            var viewport = ImGui.GetMainViewport();
            var position = viewport.WorkPos + ((viewport.WorkSize - ObviousPromptSize) * 0.5f);
            ImGui.SetNextWindowPos(position, ImGuiCond.Always);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            stylePushed = true;
            ImGui.SetNextWindowSize(ObviousPromptSize, ImGuiCond.Always);
            return;
        }

        var statusSize = status == null ? Vector2.Zero : GetPillSize(status.Text);
        var feedbackSize = status?.Feedback == null ? Vector2.Zero : GetPillSize(status.Feedback.Text);
        var width = MathF.Max(MathF.Max(statusSize.X, feedbackSize.X), prompt == null ? 0f : PromptSize.X);
        var height = 0f;
        if (prompt != null)
            height += PromptSize.Y + BadgeGap;

        if (status?.Feedback != null)
            height += feedbackSize.Y + BadgeGap;

        if (status != null)
            height += statusSize.Y;

        var movable = plugin.Configuration.MovableLiveRoomStatus;
        if (movable)
        {
            Flags &= ~ImGuiWindowFlags.NoSavedSettings;
            Flags &= ~ImGuiWindowFlags.NoMove;
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoSavedSettings;
            Flags |= ImGuiWindowFlags.NoMove;
            var position = TryGetPartyListBounds(out var min, out _)
                ? new Vector2(min.X, MathF.Max(0f, min.Y - height - OverlayGap))
                : ImGui.GetMainViewport().WorkPos + new Vector2(16f, 140f);
            ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        }

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        stylePushed = true;
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
        var prompt = plugin.LiveRoomManager.GetOverlayReadyCheckPrompt();
        if (status == null && prompt == null)
            return;

        if (plugin.Configuration.ObviousReadyCheck && prompt != null)
        {
            DrawObviousReadyCheckPrompt(prompt);
            return;
        }

        var y = 0f;
        if (prompt != null)
        {
            DrawReadyCheckPrompt(prompt, y);
            y += PromptSize.Y + BadgeGap;
        }

        if (status?.Feedback != null)
        {
            DrawPill(status.Feedback.Text, GetFeedbackColor(status.Feedback.Kind), y);
            y += GetPillSize(status.Feedback.Text).Y + BadgeGap;
        }

        if (status == null)
            return;

        DrawPill(status.Text, GetStatusColor(status.State), y);

        var statusSize = GetPillSize(status.Text);
        var canOpenRun = status.State == RealtimeRunRoomState.Connected;
        var movable = plugin.Configuration.MovableLiveRoomStatus;
        var statusHovered = false;
        if (canOpenRun || movable)
        {
            ImGui.SetCursorPos(new Vector2(0f, y));
            var clicked = ImGui.InvisibleButton("##fullparty_live_room_overlay_interaction", statusSize);
            if (movable && ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);
                overlayWasDragged = true;
            }

            if (clicked && canOpenRun && !overlayWasDragged)
                plugin.LiveRoomManager.OpenOverlayRunWindow();

            statusHovered = ImGui.IsItemHovered();
            if (statusHovered)
                ImGui.SetMouseCursor(movable ? ImGuiMouseCursor.ResizeAll : ImGuiMouseCursor.Hand);

            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
                overlayWasDragged = false;
        }

        if (!string.IsNullOrWhiteSpace(status.Detail) &&
            (statusHovered || ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)))
        {
            var tooltip = canOpenRun
                ? $"{status.Detail}\nClick to open run window."
                : status.Detail;
            ImGui.SetTooltip(tooltip);
        }
    }

    private static Vector2 GetPillSize(string text)
    {
        var textSize = ImGui.CalcTextSize(text);
        return new Vector2(textSize.X + (HorizontalPadding * 2f) + AccentWidth, textSize.Y + (VerticalPadding * 2f));
    }

    private void DrawReadyCheckPrompt(FullPartyReadyCheckConfirmationPrompt prompt, float yOffset)
    {
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos() + new Vector2(0f, yOffset);
        var max = min + PromptSize;
        drawList.AddRectFilled(min, max, FullPartyModernPalette.Color(FullPartyModernPalette.Surface with { W = 0.94f }), 5f);
        drawList.AddRectFilled(min, new Vector2(min.X + AccentWidth, max.Y), FullPartyModernPalette.Color(new Vector4(0.92f, 0.70f, 0.24f, 1f)), 5f);
        drawList.AddRect(min, max, FullPartyModernPalette.Color(FullPartyModernPalette.BorderSoft), 5f);

        ImGui.SetCursorScreenPos(min + new Vector2(HorizontalPadding + AccentWidth, 7f));
        ImGui.TextUnformatted("Ready check confirmation");
        ImGui.SetCursorScreenPos(min + new Vector2(HorizontalPadding + AccentWidth, 28f));
        ImGui.TextDisabled($"{prompt.InitiatorName} is checking raid leads.");

        ImGui.SetCursorScreenPos(min + new Vector2(HorizontalPadding + AccentWidth, 55f));
        if (ImGui.Button("Ready##fullparty_overlay_ready_check_ready", new Vector2(92f, 24f)))
            plugin.LiveRoomManager.ConfirmOverlayReadyCheck(true);

        ImGui.SameLine();
        if (ImGui.Button("Not Ready##fullparty_overlay_ready_check_not_ready", new Vector2(106f, 24f)))
            plugin.LiveRoomManager.ConfirmOverlayReadyCheck(false);
    }

    private void DrawObviousReadyCheckPrompt(FullPartyReadyCheckConfirmationPrompt prompt)
    {
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + ObviousPromptSize;
        var flashYellow = (int)(ImGui.GetTime() / 0.38) % 2 == 0;
        var flashColor = flashYellow
            ? new Vector4(1f, 0.82f, 0.08f, 1f)
            : new Vector4(0.95f, 0.12f, 0.10f, 1f);
        var background = flashYellow
            ? new Vector4(0.34f, 0.25f, 0.02f, 0.97f)
            : new Vector4(0.32f, 0.02f, 0.03f, 0.97f);

        drawList.AddRectFilled(min, max, FullPartyModernPalette.Color(background), 8f);
        drawList.AddRect(min, max, FullPartyModernPalette.Color(flashColor), 8f, ImDrawFlags.None, 5f);
        drawList.AddRect(min + new Vector2(7f), max - new Vector2(7f), FullPartyModernPalette.Color(flashColor with { W = 0.55f }), 5f, ImDrawFlags.None, 2f);

        const string title = "RAID LEAD READY CHECK";
        ImGui.SetWindowFontScale(1.35f);
        var titleWidth = ImGui.CalcTextSize(title).X;
        ImGui.SetCursorScreenPos(new Vector2(min.X + ((ObviousPromptSize.X - titleWidth) * 0.5f), min.Y + 18f));
        ImGui.TextColored(flashColor, title);
        ImGui.SetWindowFontScale(1f);

        var message = $"{prompt.InitiatorName} is checking raid leads.";
        var messageWidth = ImGui.CalcTextSize(message).X;
        ImGui.SetCursorScreenPos(new Vector2(min.X + MathF.Max(18f, (ObviousPromptSize.X - messageWidth) * 0.5f), min.Y + 62f));
        ImGui.TextUnformatted(message);

        const float buttonWidth = 154f;
        const float buttonGap = 14f;
        var buttonsX = min.X + ((ObviousPromptSize.X - ((buttonWidth * 2f) + buttonGap)) * 0.5f);
        ImGui.SetCursorScreenPos(new Vector2(buttonsX, min.Y + 98f));
        if (ImGui.Button("READY##fullparty_obvious_ready_check_ready", new Vector2(buttonWidth, 38f)))
            plugin.LiveRoomManager.ConfirmOverlayReadyCheck(true);

        ImGui.SameLine(0f, buttonGap);
        if (ImGui.Button("NOT READY##fullparty_obvious_ready_check_not_ready", new Vector2(buttonWidth, 38f)))
            plugin.LiveRoomManager.ConfirmOverlayReadyCheck(false);
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
