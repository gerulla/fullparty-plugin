using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using ElezenTools.UI;

namespace FullParty.Windows;

internal static class FullPartyModernPalette
{
    public static readonly Vector4 Background = Hex(0x14, 0x11, 0x16);
    public static readonly Vector4 Surface = Hex(0x17, 0x14, 0x19);
    public static readonly Vector4 Elevated = Hex(0x29, 0x24, 0x2d);
    public static readonly Vector4 Border = Hex(0x31, 0x2b, 0x37);
    public static readonly Vector4 BorderSoft = Hex(0x43, 0x3b, 0x49);
    public static readonly Vector4 Text = Hex(0xec, 0xe6, 0xf4);
    public static readonly Vector4 Muted = Hex(0xa7, 0x9b, 0xb9);
    public static readonly Vector4 Brand = Hex(0x84, 0x57, 0xb0);
    public static readonly Vector4 BrandHover = Hex(0x70, 0x43, 0x9b);
    public static readonly Vector4 BrandSoft = Hex(0x2c, 0x18, 0x3d);
    public static readonly Vector4 Success = Hex(0x35, 0x9d, 0x68);
    public static readonly Vector4 Danger = Hex(0xd1, 0x55, 0x64);

    public static ModernPalette Value { get; } = new(
        Accent: Brand,
        BooleanTrue: Success,
        BooleanFalse: Danger,
        CompactBg: Background,
        CompactPanel: Surface,
        CompactPanelAlt: Elevated,
        CompactBorder: Border,
        CompactBorderSubtle: BorderSoft,
        CompactTextMuted: Muted,
        CompactOffline: Muted,
        TitleBg: BrandSoft,
        TitleBgActive: Hex(0x43, 0x29, 0x5a),
        TitleBgCollapsed: Background,
        Button: Brand,
        ButtonHovered: BrandHover,
        ButtonActive: Hex(0x5f, 0x38, 0x83),
        TabActive: Brand,
        TabHovered: BrandHover);

    public static void SectionHeader(FontAwesomeIcon icon, string title)
    {
        var drawList = ImGui.GetWindowDrawList();
        var cursor = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetTextLineHeightWithSpacing() + 8f;
        var accentWidth = 3f;

        drawList.AddRectFilled(cursor, cursor + new Vector2(width, height), Color(Elevated with { W = 0.74f }), 3f);
        drawList.AddRectFilled(cursor, cursor + new Vector2(accentWidth, height), Color(Brand), 3f);

        ImGui.SetCursorScreenPos(cursor + new Vector2(10f, 4f));
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ElezenImgui.ShowIcon(icon);
        ImGui.SameLine();
        ImGui.TextUnformatted(title);
        ImGui.PopStyleColor();
        ImGui.SetCursorScreenPos(cursor + new Vector2(0, height + 4f));
    }

    public static void SoftSeparator()
    {
        ModernSection.SoftSeparator();
    }

    public static bool IconButton(FontAwesomeIcon icon, string label, float? width = null)
    {
        return ElezenImgui.ShowIconButton(icon, label, width);
    }

    public static uint Color(Vector4 color)
    {
        return ImGui.ColorConvertFloat4ToU32(color);
    }

    public static IDisposable PushImGuiStyle()
    {
        var colorCount = 0;
        var styleVarCount = 0;

        PushColor(ImGuiCol.Text, Text, ref colorCount);
        PushColor(ImGuiCol.TextDisabled, Muted, ref colorCount);
        PushColor(ImGuiCol.WindowBg, Background with { W = 0.94f }, ref colorCount);
        PushColor(ImGuiCol.ChildBg, Surface with { W = 0.70f }, ref colorCount);
        PushColor(ImGuiCol.PopupBg, Surface with { W = 0.98f }, ref colorCount);
        PushColor(ImGuiCol.Border, BorderSoft, ref colorCount);
        PushColor(ImGuiCol.BorderShadow, Background with { W = 0f }, ref colorCount);
        PushColor(ImGuiCol.FrameBg, Elevated, ref colorCount);
        PushColor(ImGuiCol.FrameBgHovered, BrandSoft, ref colorCount);
        PushColor(ImGuiCol.FrameBgActive, BrandHover, ref colorCount);
        PushColor(ImGuiCol.TitleBg, BrandSoft, ref colorCount);
        PushColor(ImGuiCol.TitleBgActive, Hex(0x43, 0x29, 0x5a), ref colorCount);
        PushColor(ImGuiCol.TitleBgCollapsed, Background, ref colorCount);
        PushColor(ImGuiCol.Button, BrandSoft, ref colorCount);
        PushColor(ImGuiCol.ButtonHovered, Brand, ref colorCount);
        PushColor(ImGuiCol.ButtonActive, BrandHover, ref colorCount);
        PushColor(ImGuiCol.Header, BrandSoft with { W = 0.78f }, ref colorCount);
        PushColor(ImGuiCol.HeaderHovered, Brand with { W = 0.72f }, ref colorCount);
        PushColor(ImGuiCol.HeaderActive, BrandHover, ref colorCount);
        PushColor(ImGuiCol.Separator, BorderSoft, ref colorCount);
        PushColor(ImGuiCol.SeparatorHovered, Brand, ref colorCount);
        PushColor(ImGuiCol.SeparatorActive, BrandHover, ref colorCount);
        PushColor(ImGuiCol.ResizeGrip, BrandSoft, ref colorCount);
        PushColor(ImGuiCol.ResizeGripHovered, Brand, ref colorCount);
        PushColor(ImGuiCol.ResizeGripActive, BrandHover, ref colorCount);
        PushColor(ImGuiCol.Tab, Surface, ref colorCount);
        PushColor(ImGuiCol.TabHovered, BrandSoft, ref colorCount);
        PushColor(ImGuiCol.TabActive, Brand, ref colorCount);
        PushColor(ImGuiCol.TableHeaderBg, Elevated, ref colorCount);
        PushColor(ImGuiCol.TableBorderStrong, BorderSoft, ref colorCount);
        PushColor(ImGuiCol.TableBorderLight, Border, ref colorCount);
        PushColor(ImGuiCol.TableRowBg, Background with { W = 0.14f }, ref colorCount);
        PushColor(ImGuiCol.TableRowBgAlt, Elevated with { W = 0.18f }, ref colorCount);
        PushColor(ImGuiCol.ScrollbarBg, Background with { W = 0.35f }, ref colorCount);
        PushColor(ImGuiCol.ScrollbarGrab, BorderSoft, ref colorCount);
        PushColor(ImGuiCol.ScrollbarGrabHovered, BrandSoft, ref colorCount);
        PushColor(ImGuiCol.ScrollbarGrabActive, Brand, ref colorCount);
        PushColor(ImGuiCol.CheckMark, Brand, ref colorCount);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 3f);
        styleVarCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 3f);
        styleVarCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        styleVarCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 4f);
        styleVarCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 4f);
        styleVarCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        styleVarCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        styleVarCount++;

        return new ImGuiStyleScope(colorCount, styleVarCount);
    }

    private static Vector4 Hex(int red, int green, int blue, float alpha = 1f)
    {
        return new Vector4(red / 255f, green / 255f, blue / 255f, alpha);
    }

    private static void PushColor(ImGuiCol color, Vector4 value, ref int count)
    {
        ImGui.PushStyleColor(color, value);
        count++;
    }

    private sealed class ImGuiStyleScope(int colorCount, int styleVarCount) : IDisposable
    {
        public void Dispose()
        {
            if (styleVarCount > 0)
                ImGui.PopStyleVar(styleVarCount);

            if (colorCount > 0)
                ImGui.PopStyleColor(colorCount);
        }
    }
}
