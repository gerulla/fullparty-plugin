using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;

namespace FullParty.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly string logoImagePath;
    private readonly Plugin plugin;

    // We give this window a hidden ID using ##.
    // The user will see "FullParty" as window title,
    // but for ImGui the ID is "FullParty##Main".
    public MainWindow(Plugin plugin, string logoImagePath)
        : base("FullParty##Main", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.logoImagePath = logoImagePath;
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.Text($"The random config bool is {plugin.Configuration.SomePropertyToBeSavedAndWithADefault}");

        if (ImGui.Button("Show Settings"))
        {
            plugin.ToggleConfigUi();
        }

        ImGui.Spacing();

        // Normally a BeginChild() would have to be followed by an unconditional EndChild(),
        // ImRaii takes care of this after the scope ends.
        // This works for all ImGui functions that require specific handling, examples are BeginTable() or Indent().
        using (var child = ImRaii.Child("SomeChildWithAScrollbar", Vector2.Zero, true))
        {
            // Check if this child is drawing
            if (child.Success)
            {
                ImGui.Text("FullParty");
                var logoImage = Plugin.TextureProvider.GetFromFile(logoImagePath).GetWrapOrDefault();
                if (logoImage != null)
                {
                    var logoSize = logoImage.Size;
                    var maxLogoSize = new Vector2(
                        MathF.Min(ImGui.GetContentRegionAvail().X, 360f * ImGuiHelpers.GlobalScale),
                        180f * ImGuiHelpers.GlobalScale);

                    if (logoSize.X > maxLogoSize.X || logoSize.Y > maxLogoSize.Y)
                    {
                        logoSize *= MathF.Min(maxLogoSize.X / logoSize.X, maxLogoSize.Y / logoSize.Y);
                    }

                    var cursorX = ImGui.GetCursorPosX();
                    var centeredOffset = MathF.Max(0, (ImGui.GetContentRegionAvail().X - logoSize.X) * 0.5f);
                    ImGui.SetCursorPosX(cursorX + centeredOffset);
                    ImGui.Image(logoImage.Handle, logoSize);
                }
                else
                {
                    ImGui.Text("Image not found.");
                }

                ImGuiHelpers.ScaledDummy(20.0f);

                // Example for other services that Dalamud provides.
                // PlayerState provides a wrapper filled with information about the player character.

                var playerState = Plugin.PlayerState;
                if (!playerState.IsLoaded)
                {
                    ImGui.Text("Our local player is currently not logged in.");
                    return;
                }
                
                if (!playerState.ClassJob.IsValid)
                {
                    ImGui.Text("Our current job is currently not valid.");
                    return;
                }
                
                ImGui.AlignTextToFramePadding();
                ImGui.Text($"Current job:");
                
                // Scaling hardcoded pixel values is important, as otherwise users with HUD scales above or below 100%
                // won't be able to see everything.
                ImGui.SameLine(120 * ImGuiHelpers.GlobalScale);
                
                // Get the icon id from a known offset + the class jobs id
                var jobIconId = 62100 + playerState.ClassJob.RowId;
                var iconTexture = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(jobIconId)).GetWrapOrEmpty();
                ImGui.Image(iconTexture.Handle, new Vector2(28, 28) * ImGuiHelpers.GlobalScale);
                
                ImGui.SameLine();
                
                // If you want to see the Macro representation of this SeString use `.ToMacroString()`
                // More info about SeStrings: https://dalamud.dev/plugin-development/sestring/
                ImGui.Text(playerState.ClassJob.Value.Abbreviation.ToString());
                
                ImGui.SameLine();
                ImGui.Text($" [Level {playerState.Level}]");
                
                // Example for querying Lumina, getting the name of our current area.
                var territoryId = Plugin.ClientState.TerritoryType;
                if (Plugin.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territoryRow))
                {
                    ImGui.Text($"Current location:");
                    ImGui.SameLine(120 * ImGuiHelpers.GlobalScale);
                    ImGui.Text(territoryRow.PlaceName.Value.Name.ToString());
                }
                else
                {
                    ImGui.Text("Invalid territory.");
                }
            }
        }
    }
}
