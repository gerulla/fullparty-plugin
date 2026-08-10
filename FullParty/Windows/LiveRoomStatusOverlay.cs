using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FullParty.Services;

namespace FullParty.Windows;

internal enum ReadyCheckPreviewStyle
{
    Small,
    Native,
    Obvious,
}

public sealed unsafe class LiveRoomStatusOverlay : Window, IDisposable
{
    private const float HorizontalPadding = 8f;
    private const float VerticalPadding = 4f;
    private const float AccentWidth = 3f;
    private const float OverlayGap = -10f;
    private const float BadgeGap = 3f;
    private static readonly Vector2 PromptSize = new(300f, 88f);
    private static readonly Vector2 ObviousPromptSize = new(440f, 154f);
    private static readonly Vector2 NativePromptSize = new(474f, 178f);
    private static readonly Vector2 NativeNoticeSize = new(240f, 42f);

    private readonly Plugin plugin;
    private readonly IFontHandle nativePromptFont;
    private readonly IFontHandle nativeButtonFont;
    private bool stylePushed;
    private bool overlayWasDragged;
    private string? nativePromptRequestId;
    private FullPartyReadyCheckConfirmationPrompt? previewPrompt;
    private ReadyCheckPreviewStyle? previewStyle;
    private bool nativePromptHeld;

    public LiveRoomStatusOverlay(Plugin plugin)
        : base("FullParty Live Room Status##FullPartyLiveRoomStatusOverlay")
    {
        this.plugin = plugin;
        IsOpen = true;
        nativePromptFont = Plugin.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Axis18));
        nativeButtonFont = Plugin.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Axis14));
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
        nativePromptFont.Dispose();
        nativeButtonFont.Dispose();
    }

    internal void ShowReadyCheckPreview(ReadyCheckPreviewStyle style)
    {
        previewStyle = style;
        previewPrompt = new FullPartyReadyCheckConfirmationPrompt(
            $"preview-{Guid.NewGuid():N}",
            "FullParty Test",
            GetFrameworkTime() + TimeSpan.FromSeconds(30));
        nativePromptRequestId = null;
        nativePromptHeld = false;
        ReadyCheckSoundPlayer.PlayPartyLeadStarted(plugin.Configuration);
    }

    internal void DismissReadyCheckPreview()
    {
        previewPrompt = null;
        previewStyle = null;
        nativePromptRequestId = null;
        nativePromptHeld = false;
    }

    private FullPartyReadyCheckConfirmationPrompt? GetReadyCheckPrompt()
    {
        var livePrompt = plugin.LiveRoomManager.GetOverlayReadyCheckPrompt();
        if (livePrompt != null)
        {
            previewPrompt = null;
            previewStyle = null;
            return livePrompt;
        }

        if (previewPrompt != null && previewPrompt.ExpiresAt <= GetFrameworkTime())
            DismissReadyCheckPreview();

        return previewPrompt;
    }

    private bool IsPreviewPrompt(FullPartyReadyCheckConfirmationPrompt prompt)
    {
        return previewPrompt?.RequestId.Equals(prompt.RequestId, StringComparison.Ordinal) == true;
    }

    private bool ShouldDrawSmallPrompt(FullPartyReadyCheckConfirmationPrompt prompt)
    {
        return !IsPreviewPrompt(prompt) || previewStyle == ReadyCheckPreviewStyle.Small;
    }

    private void ConfirmReadyCheck(FullPartyReadyCheckConfirmationPrompt prompt, bool ready)
    {
        if (IsPreviewPrompt(prompt))
        {
            DismissReadyCheckPreview();
            return;
        }

        plugin.LiveRoomManager.ConfirmOverlayReadyCheck(ready);
    }

    private static DateTimeOffset GetFrameworkTime()
    {
        return Plugin.Framework.LastUpdateUTC == default ? DateTimeOffset.UtcNow : Plugin.Framework.LastUpdateUTC;
    }

    public override bool DrawConditions()
    {
        return plugin.LiveRoomManager.GetOverlayStatus() != null ||
               GetReadyCheckPrompt() != null;
    }

    public override void PreDraw()
    {
        var status = plugin.LiveRoomManager.GetOverlayStatus();
        var prompt = GetReadyCheckPrompt();
        if (status == null && prompt == null)
            return;

        var statusSize = status == null ? Vector2.Zero : GetPillSize(status.Text);
        var feedbackSize = status?.Feedback == null ? Vector2.Zero : GetPillSize(status.Feedback.Text);
        var showSmallPrompt = prompt != null && ShouldDrawSmallPrompt(prompt);
        var width = MathF.Max(1f, MathF.Max(MathF.Max(statusSize.X, feedbackSize.X), showSmallPrompt ? PromptSize.X : 0f));
        var height = 0f;
        if (showSmallPrompt)
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
        ImGui.SetNextWindowSize(new Vector2(width, MathF.Max(1f, height)), ImGuiCond.Always);
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
        var prompt = GetReadyCheckPrompt();
        if (status == null && prompt == null)
            return;

        if (prompt == null)
        {
            nativePromptRequestId = null;
            nativePromptHeld = false;
        }
        else if (IsPreviewPrompt(prompt))
        {
            if (previewStyle == ReadyCheckPreviewStyle.Native)
                DrawNativeReadyCheckPresentation(prompt);
            else if (previewStyle == ReadyCheckPreviewStyle.Obvious)
                DrawObviousReadyCheckWindow(prompt);
        }
        else if (plugin.Configuration.ObviousReadyCheck)
        {
            DrawObviousReadyCheckWindow(prompt);
        }

        var y = 0f;
        if (prompt != null && ShouldDrawSmallPrompt(prompt))
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
            ConfirmReadyCheck(prompt, true);

        ImGui.SameLine();
        if (ImGui.Button("Not Ready##fullparty_overlay_ready_check_not_ready", new Vector2(106f, 24f)))
            ConfirmReadyCheck(prompt, false);
    }

    private void DrawObviousReadyCheckWindow(FullPartyReadyCheckConfirmationPrompt prompt)
    {
        var viewport = ImGui.GetMainViewport();
        var position = viewport.WorkPos + ((viewport.WorkSize - ObviousPromptSize) * 0.5f);
        var flags = ImGuiWindowFlags.NoDecoration |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoNav |
                    ImGuiWindowFlags.NoFocusOnAppearing |
                    ImGuiWindowFlags.NoBackground;

        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(ObviousPromptSize, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.Begin("FullParty Obvious Ready Check##FullPartyObviousReadyCheck", flags))
            DrawObviousReadyCheckPrompt(prompt);

        ImGui.End();
        ImGui.PopStyleVar();
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
            ConfirmReadyCheck(prompt, true);

        ImGui.SameLine(0f, buttonGap);
        if (ImGui.Button("NOT READY##fullparty_obvious_ready_check_not_ready", new Vector2(buttonWidth, 38f)))
            ConfirmReadyCheck(prompt, false);
    }

    private void DrawNativeReadyCheckPresentation(FullPartyReadyCheckConfirmationPrompt prompt)
    {
        if (!prompt.RequestId.Equals(nativePromptRequestId, StringComparison.Ordinal))
        {
            nativePromptRequestId = prompt.RequestId;
            nativePromptHeld = false;
        }

        DrawNativeReadyCheckNotice(prompt);
        if (!nativePromptHeld)
            DrawNativeReadyCheckWindow(prompt);
    }

    private void DrawNativeReadyCheckWindow(FullPartyReadyCheckConfirmationPrompt prompt)
    {
        var viewport = ImGui.GetMainViewport();
        var position = viewport.WorkPos + ((viewport.WorkSize - NativePromptSize) * 0.5f);
        var flags = ImGuiWindowFlags.NoDecoration |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoNav |
                    ImGuiWindowFlags.NoFocusOnAppearing |
                    ImGuiWindowFlags.NoBackground;

        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(NativePromptSize, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.Begin("FullParty Native Ready Check##FullPartyNativeReadyCheck", flags))
            DrawNativeReadyCheckPrompt(prompt);

        ImGui.End();
        ImGui.PopStyleVar();
    }

    private void DrawNativeReadyCheckPrompt(FullPartyReadyCheckConfirmationPrompt prompt)
    {
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + NativePromptSize;
        var panelMin = min + new Vector2(3f);
        var panelMax = max - new Vector2(3f);
        var border = ImGui.ColorConvertFloat4ToU32(new Vector4(0.62f, 0.66f, 0.68f, 0.82f));
        var innerBorder = ImGui.ColorConvertFloat4ToU32(new Vector4(0.16f, 0.18f, 0.19f, 0.68f));

        drawList.AddRectFilled(
            min + new Vector2(5f, 7f),
            max + new Vector2(2f, 4f),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.28f)),
            1f);
        drawList.AddRectFilledMultiColor(
            panelMin,
            panelMax,
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.34f, 0.37f, 0.39f, 0.93f)),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.32f, 0.35f, 0.37f, 0.93f)),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.25f, 0.27f, 0.29f, 0.93f)),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.26f, 0.28f, 0.30f, 0.93f)));
        drawList.AddRect(panelMin, panelMax, border, 1f, ImDrawFlags.None, 1f);
        drawList.AddRect(panelMin + new Vector2(3f), panelMax - new Vector2(3f), innerBorder);
        drawList.AddLine(
            panelMin + new Vector2(5f, 6f),
            new Vector2(panelMax.X - 5f, panelMin.Y + 6f),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.82f, 0.84f, 0.86f, 0.16f)));

        ImGui.SetCursorScreenPos(new Vector2(panelMax.X - 30f, panelMin.Y + 8f));
        if (ImGui.InvisibleButton("##fullparty_native_ready_check_close", new Vector2(22f, 22f)))
            nativePromptHeld = true;

        var closeColor = ImGui.IsItemHovered()
            ? new Vector4(1f, 1f, 1f, 1f)
            : new Vector4(0.94f, 0.94f, 0.94f, 0.9f);
        var closeMin = new Vector2(panelMax.X - 25f, panelMin.Y + 13f);
        drawList.AddLine(closeMin, closeMin + new Vector2(12f, 12f), ImGui.ColorConvertFloat4ToU32(closeColor), 1.5f);
        drawList.AddLine(closeMin + new Vector2(12f, 0f), closeMin + new Vector2(0f, 12f), ImGui.ColorConvertFloat4ToU32(closeColor), 1.5f);

        using (nativePromptFont.Push())
        {
        ImGui.SetCursorScreenPos(panelMin + new Vector2(27f, 23f));
        ImGui.TextColored(new Vector4(0.96f, 0.96f, 0.96f, 1f), "A ready check has been commenced.");
        ImGui.SetCursorScreenPos(panelMin + new Vector2(27f, 47f));
        ImGui.TextColored(new Vector4(0.96f, 0.96f, 0.96f, 1f), "Inform your comrades that you are ready?");

        ImGui.SetCursorScreenPos(panelMin + new Vector2(27f, 76f));
        ImGui.TextColored(new Vector4(0.88f, 0.88f, 0.88f, 1f), "※You may choose to respond later by selecting");
        ImGui.SetCursorScreenPos(panelMin + new Vector2(27f, 97f));
        ImGui.TextColored(new Vector4(0.88f, 0.88f, 0.88f, 1f), "the relevant notice.");
        }

        var buttonY = panelMax.Y - 38f;
        const float buttonWidth = 113f;
        const float buttonGap = 19f;
        var buttonX = panelMin.X + ((NativePromptSize.X - ((buttonWidth * 3f) + (buttonGap * 2f))) * 0.5f);
        if (DrawNativeButton("Yes", "yes", new Vector2(buttonX, buttonY), new Vector2(buttonWidth, 28f)))
            ConfirmReadyCheck(prompt, true);

        buttonX += buttonWidth + buttonGap;
        if (DrawNativeButton("Hold", "hold", new Vector2(buttonX, buttonY), new Vector2(buttonWidth, 28f)))
            nativePromptHeld = true;

        buttonX += buttonWidth + buttonGap;
        if (DrawNativeButton("No", "no", new Vector2(buttonX, buttonY), new Vector2(buttonWidth, 28f)))
            ConfirmReadyCheck(prompt, false);
    }

    private bool DrawNativeButton(string label, string id, Vector2 position, Vector2 size)
    {
        ImGui.SetCursorScreenPos(position);
        var clicked = ImGui.InvisibleButton($"##fullparty_native_ready_check_{id}", size);
        var hovered = ImGui.IsItemHovered();
        var held = ImGui.IsItemActive();
        var drawList = ImGui.GetWindowDrawList();
        var top = held ? 0.31f : hovered ? 0.48f : 0.42f;
        var bottom = held ? 0.22f : hovered ? 0.35f : 0.29f;
        drawList.AddRectFilledMultiColor(
            position,
            position + size,
            ImGui.ColorConvertFloat4ToU32(new Vector4(top, top, top, 1f)),
            ImGui.ColorConvertFloat4ToU32(new Vector4(top, top, top, 1f)),
            ImGui.ColorConvertFloat4ToU32(new Vector4(bottom, bottom, bottom, 1f)),
            ImGui.ColorConvertFloat4ToU32(new Vector4(bottom, bottom, bottom, 1f)));
        drawList.AddRect(position, position + size, ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.13f, 0.14f, 1f)));
        drawList.AddLine(
            position + new Vector2(1f),
            new Vector2(position.X + size.X - 1f, position.Y + 1f),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.73f, 0.75f, 0.76f, 0.35f)));
        using (nativeButtonFont.Push())
        {
            var textSize = ImGui.CalcTextSize(label);
            drawList.AddText(
                position + ((size - textSize) * 0.5f),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.98f, 0.98f, 0.98f, 1f)),
                label);
        }
        return clicked;
    }

    private void DrawNativeReadyCheckNotice(FullPartyReadyCheckConfirmationPrompt prompt)
    {
        var viewport = ImGui.GetMainViewport();
        var position = TryGetNotificationBounds(out var notificationMin, out var notificationMax)
            ? new Vector2(notificationMin.X, MathF.Max(viewport.WorkPos.Y, notificationMax.Y + 4f))
            : viewport.WorkPos + new Vector2(viewport.WorkSize.X - NativeNoticeSize.X - 28f, 122f);
        var flags = ImGuiWindowFlags.NoDecoration |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoNav |
                    ImGuiWindowFlags.NoFocusOnAppearing |
                    ImGuiWindowFlags.NoBackground;

        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(NativeNoticeSize, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.Begin("FullParty Ready Check Notice##FullPartyNativeReadyCheckNotice", flags))
        {
            var min = ImGui.GetWindowPos();
            var max = min + NativeNoticeSize;
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRectFilled(
                min + new Vector2(2f, 3f),
                max + new Vector2(2f, 3f),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.42f)),
                20f);
            drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(0.45f, 0.18f, 0.21f, 0.98f)), 20f);
            drawList.PushClipRect(min, max, true);
            for (var x = min.X - NativeNoticeSize.Y; x < max.X + NativeNoticeSize.Y; x += 8f)
            {
                drawList.AddLine(
                    new Vector2(x, max.Y),
                    new Vector2(x + NativeNoticeSize.Y, min.Y),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.035f)));
            }

            drawList.PopClipRect();
            drawList.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(0.64f, 0.31f, 0.34f, 1f)), 20f);
            drawList.AddText(
                min + new Vector2(14f, 12f),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f)),
                "Ready Check");

            var now = Plugin.Framework.LastUpdateUTC == default ? DateTimeOffset.UtcNow : Plugin.Framework.LastUpdateUTC;
            var secondsText = Math.Max(0, (int)Math.Ceiling((prompt.ExpiresAt - now).TotalSeconds)).ToString();
            var secondsSize = ImGui.CalcTextSize(secondsText);
            drawList.AddText(
                new Vector2(max.X - secondsSize.X - 17f, min.Y + 12f),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f)),
                secondsText);

            ImGui.SetCursorPos(Vector2.Zero);
            if (ImGui.InvisibleButton("##fullparty_native_ready_check_notice_action", NativeNoticeSize))
                nativePromptHeld = false;

            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        ImGui.End();
        ImGui.PopStyleVar();
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

    private static bool TryGetNotificationBounds(out Vector2 min, out Vector2 max)
    {
        min = default;
        max = default;

        var uiModule = UIModule.Instance();
        if (uiModule == null)
            return false;

        var atkModule = uiModule->GetRaptureAtkModule();
        if (atkModule == null)
            return false;

        var addon = GetAddonByName(atkModule, "_Notification");
        if (addon == null || addon->RootNode == null)
            addon = GetAddonByName(atkModule, "Notification");

        if (addon == null || addon->RootNode == null)
            return false;

        var scale = addon->Scale <= 0f ? 1f : addon->Scale;
        var width = addon->RootNode->Width * scale;
        var height = addon->RootNode->Height * scale;
        if (width <= 1f || height <= 1f)
            return false;

        min = new Vector2(addon->X, addon->Y);
        max = min + new Vector2(width, height);
        return true;
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
