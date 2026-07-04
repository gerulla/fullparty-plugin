using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using FullParty.Models;
using FullParty.Services;
using Lumina.Excel.Sheets;

namespace FullParty.Windows;

public sealed class RunWindow : Window, IDisposable
{
    private static readonly Dictionary<string, uint?> JobIconCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Plugin plugin;
    private readonly CancellationTokenSource cancellation = new();
    private readonly RealtimeRunRoomClient liveRoom;
    private Task<FullPartyRunDetail?>? detailTask;
    private FullPartyRunDetail? detail;
    private string? detailError;

    public FullPartyRun Run { get; }

    public RunWindow(FullPartyRun run, Plugin plugin)
        : base($"{run.Name} - {run.StartsAt:MMM d, yyyy} - {run.StartsAt:HH:mm}##FullPartyRun{run.Id}")
    {
        Run = run;
        this.plugin = plugin;
        liveRoom = new RealtimeRunRoomClient(run.Id, plugin);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        IsOpen = true;
    }

    public void Dispose()
    {
        cancellation.Cancel();
        cancellation.Dispose();
        liveRoom.Dispose();
    }

    public override void Draw()
    {
        EnsureDetailLoaded();

        var isLoading = detailTask is { IsCompleted: false };
        DrawToolbar(isLoading, detail?.CanModerate == true);

        ImGui.Spacing();
        DrawLiveRoom();
        ImGui.Spacing();

        if (isLoading)
        {
            ImGui.TextDisabled("Loading roster...");
            return;
        }

        if (!string.IsNullOrWhiteSpace(detailError))
        {
            ImGui.TextWrapped(detailError);
            return;
        }

        if (detail == null)
        {
            ImGui.TextDisabled("No roster loaded.");
            return;
        }

        ImGui.Text("Roster");
        ImGui.Spacing();
        DrawRosterTable(detail);
    }

    private void DrawToolbar(bool isLoading, bool canModerate)
    {
        if (isLoading)
        {
            ImGui.BeginDisabled();
            ImGui.Button("Refresh run");
            ImGui.EndDisabled();
        }
        else if (ImGui.Button("Refresh run"))
        {
            RefreshRun();
        }

        ImGui.SameLine();

        var canSendLiveCommand = canModerate && liveRoom.State == RealtimeRunRoomState.Connected && !liveRoom.IsIssuingCommand;
        if (!canSendLiveCommand)
            ImGui.BeginDisabled();

        if (ImGui.Button("Ready Check Alliance"))
            liveRoom.SendReadyCheckAlliance();

        ImGui.SameLine();

        if (ImGui.Button("Start Countdown"))
            liveRoom.SendCountdown(20);

        ImGui.SameLine();

        ImGui.Button("Run Check-In");

        if (!canSendLiveCommand)
            ImGui.EndDisabled();
    }

    private void DrawLiveRoom()
    {
        var isBusy = liveRoom.IsBusy;
        if (liveRoom.IsActive)
        {
            if (ImGui.Button("Disconnect Live Room"))
                liveRoom.Disconnect();
        }
        else
        {
            if (isBusy)
                ImGui.BeginDisabled();

            if (ImGui.Button("Connect To Live Room"))
                liveRoom.Connect();

            if (isBusy)
                ImGui.EndDisabled();
        }

        ImGui.SameLine();
        var statusColor = liveRoom.State switch
        {
            RealtimeRunRoomState.Connected => new Vector4(0.35f, 0.92f, 0.55f, 1f),
            RealtimeRunRoomState.Error => new Vector4(1f, 0.42f, 0.42f, 1f),
            RealtimeRunRoomState.Disconnected => new Vector4(0.65f, 0.65f, 0.70f, 1f),
            _ => new Vector4(0.90f, 0.82f, 0.50f, 1f),
        };

        ImGui.TextColored(statusColor, liveRoom.StatusMessage);

        if (!string.IsNullOrWhiteSpace(liveRoom.CommandStatusMessage))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(liveRoom.CommandStatusMessage);
        }

        var members = liveRoom.Members;
        if (!liveRoom.IsActive && members.Count == 0 && liveRoom.State != RealtimeRunRoomState.Error)
            return;

        ImGui.Spacing();
        ImGui.Text("Live Room");

        if (members.Count == 0)
        {
            ImGui.TextDisabled(liveRoom.State == RealtimeRunRoomState.Connected
                ? "No connected characters yet."
                : "Connect to see live characters.");
            return;
        }

        var flags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##fullparty_live_room_members", 4, flags))
            return;

        ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("User", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Slots", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, 96f);
        ImGui.TableHeadersRow();

        foreach (var member in members)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawLiveMemberCharacter(member);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(member.UserName);

            ImGui.TableNextColumn();
            var slots = member.SlotLabels.Count == 0 ? "-" : string.Join(", ", member.SlotLabels);
            ImGui.TextWrapped(slots);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(GetLiveMemberRole(member));
        }

        ImGui.EndTable();
    }

    private void DrawRosterTable(FullPartyRunDetail runDetail)
    {
        var parties = runDetail.Slots
            .Where(slot => !IsBenchSlot(slot))
            .GroupBy(slot => slot.GroupKey)
            .OrderBy(group => group.Min(slot => slot.SortOrder ?? int.MaxValue))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(slot => slot.PositionInGroup ?? int.MaxValue)
                .ThenBy(slot => slot.SortOrder ?? int.MaxValue)
                .ThenBy(slot => slot.SlotKey, StringComparer.OrdinalIgnoreCase)
                .ToList())
            .ToList();
        var benchSlots = runDetail.Slots
            .Where(IsBenchSlot)
            .OrderBy(slot => slot.PositionInGroup ?? int.MaxValue)
            .ThenBy(slot => slot.SortOrder ?? int.MaxValue)
            .ThenBy(slot => slot.SlotKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (parties.Count == 0 && benchSlots.Count == 0)
        {
            ImGui.TextDisabled("No roster slots.");
            return;
        }

        if (parties.Count > 0)
        {
            DrawPartyRosterTable(parties, runDetail.CanModerate);
        }

        if (benchSlots.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.Text("Bench");
            ImGui.Spacing();
            DrawBenchTable(benchSlots, runDetail.CanModerate);
        }
    }

    private void DrawPartyRosterTable(IReadOnlyList<IReadOnlyList<FullPartyRosterSlot>> parties, bool canModerate)
    {
        var flags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame;
        if (!ImGui.BeginTable("##fullparty_roster", parties.Count, flags))
            return;

        foreach (var party in parties)
        {
            ImGui.TableSetupColumn(party[0].GroupLabel);
        }

        ImGui.TableHeadersRow();

        var maxRows = parties.Max(party => party.Count);
        for (var row = 0; row < maxRows; row++)
        {
            ImGui.TableNextRow();
            for (var column = 0; column < parties.Count; column++)
            {
                ImGui.TableNextColumn();
                if (row < parties[column].Count)
                {
                    DrawRosterSlot(parties[column][row], canModerate);
                }
            }
        }

        ImGui.EndTable();
    }

    private void DrawBenchTable(IReadOnlyList<FullPartyRosterSlot> benchSlots, bool canModerate)
    {
        var columnCount = Math.Clamp((int)(ImGui.GetContentRegionAvail().X / 260f), 1, 3);
        var flags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame;
        if (!ImGui.BeginTable("##fullparty_bench", columnCount, flags))
            return;

        for (var column = 0; column < columnCount; column++)
        {
            ImGui.TableSetupColumn($"##bench_column_{column}");
        }

        for (var index = 0; index < benchSlots.Count; index++)
        {
            if (index % columnCount == 0)
                ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawRosterSlot(benchSlots[index], canModerate);
        }

        ImGui.EndTable();
    }

    private void DrawRosterSlot(FullPartyRosterSlot slot, bool canModerate)
    {
        var character = slot.AssignedCharacter;
        if (character == null)
        {
            DrawEmptyRosterSlot(slot);
            return;
        }

        var canOpenApplication = canModerate && slot.ApplicationId != null;
        if (ImGui.InvisibleButton($"##slot_{slot.Id}", new Vector2(-1f, 34f)) && canOpenApplication)
        {
            plugin.OpenApplicationWindow(Run, slot);
        }

        var min = ImGui.GetItemRectMin();
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        var max = ImGui.GetItemRectMax();
        var bg = GetFilledSlotBackground(slot.CharacterClassRole, hovered && canOpenApplication);
        drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(bg), 3f);
        drawList.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, hovered ? 0.18f : 0.08f)), 3f);

        var textColor = ImGui.GetColorU32(ImGuiCol.Text);
        var mutedColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        var cursor = min + new Vector2(5f, 5f);
        var hasClassIcon = !string.IsNullOrWhiteSpace(slot.CharacterClass);
        var hasPhantomIcon = HasPhantomJob(slot);
        var iconRight = max.X - 7f;
        Vector2? classIconPosition = null;
        Vector2? phantomIconPosition = null;

        if (hasPhantomIcon)
        {
            iconRight -= 20f;
            phantomIconPosition = new Vector2(iconRight, min.Y + 7f);
            iconRight -= 4f;
        }

        if (hasClassIcon)
        {
            iconRight -= 20f;
            classIconPosition = new Vector2(iconRight, min.Y + 7f);
            iconRight -= 4f;
        }

        DrawCharacterIcon(drawList, character, cursor);
        cursor.X += 29f;

        var nameWidth = Math.Max(24f, iconRight - cursor.X);
        drawList.AddText(cursor + new Vector2(0, 3f), textColor, TrimToWidth(character.Name, nameWidth));

        if (classIconPosition != null && !DrawJobIcon(drawList, slot.CharacterClass, classIconPosition.Value))
        {
            DrawIconFallback(drawList, classIconPosition.Value, slot.CharacterClass, mutedColor);
        }

        if (phantomIconPosition != null &&
            !DrawPhantomJobIcon(drawList, slot, phantomIconPosition.Value) &&
            string.IsNullOrWhiteSpace(slot.PhantomJobIconUrl))
        {
            DrawIconFallback(drawList, phantomIconPosition.Value, slot.PhantomJob, mutedColor);
        }
    }

    private void DrawCharacterIcon(ImDrawListPtr drawList, FullPartyRosterCharacter character, Vector2 position)
    {
        var path = plugin.ImageCache.GetImagePath(character.AvatarUrl, $"character-{character.Id}");
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var texture = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
            if (texture != null)
            {
                drawList.AddImage(texture.Handle, position, position + new Vector2(24, 24));
                return;
            }
        }

        drawList.AddRectFilled(position, position + new Vector2(24, 24), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.10f)), 3f);
    }

    private void DrawLiveMemberCharacter(FullPartyLiveMember member)
    {
        var avatarDrawn = false;
        var path = plugin.ImageCache.GetImagePath(member.AvatarUrl, $"live-member-{member.UserId}");
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var texture = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
            if (texture != null)
            {
                ImGui.Image(texture.Handle, new Vector2(24f, 24f));
                avatarDrawn = true;
            }
        }

        if (!avatarDrawn)
        {
            var cursor = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(24f, 24f));
            ImGui.GetWindowDrawList().AddRectFilled(cursor, cursor + new Vector2(24f, 24f), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.10f)), 3f);
        }

        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextUnformatted(member.DisplayName);
        if (!string.IsNullOrWhiteSpace(member.Location))
            ImGui.TextDisabled(member.Location);
        ImGui.EndGroup();
    }

    private static void DrawEmptyRosterSlot(FullPartyRosterSlot slot)
    {
        ImGui.Dummy(new Vector2(Math.Max(1f, ImGui.GetContentRegionAvail().X), 34f));
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.09f, 0.11f, 0.20f)), 3f);
        drawList.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.05f)), 3f);
        drawList.AddText(min + new Vector2(7f, 8f), ImGui.GetColorU32(ImGuiCol.TextDisabled), TrimToWidth(slot.SlotLabel, Math.Max(24f, max.X - min.X - 14f)));
    }

    private bool DrawPhantomJobIcon(ImDrawListPtr drawList, FullPartyRosterSlot slot, Vector2 position)
    {
        var iconUrls = slot.PhantomJobIconUrls.Count > 0
            ? slot.PhantomJobIconUrls
            : string.IsNullOrWhiteSpace(slot.PhantomJobIconUrl)
                ? Array.Empty<string>()
                : new[] { slot.PhantomJobIconUrl };

        for (var i = 0; i < iconUrls.Count; i++)
        {
            var path = plugin.ImageCache.GetImagePath(iconUrls[i], $"phantom-{slot.PhantomJobId?.ToString() ?? slot.PhantomJob ?? slot.Id.ToString()}-{i}");
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var texture = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
                if (texture != null)
                {
                    drawList.AddImage(texture.Handle, position, position + new Vector2(20, 20));
                    return true;
                }
            }
        }

        if (slot.PhantomJobIconId is > 0)
        {
            var gameIcon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup((uint)slot.PhantomJobIconId.Value)).GetWrapOrDefault();
            if (gameIcon != null)
            {
                drawList.AddImage(gameIcon.Handle, position, position + new Vector2(20, 20));
                return true;
            }
        }

        return false;
    }

    private static void DrawIconFallback(ImDrawListPtr drawList, Vector2 position, string? label, uint textColor)
    {
        drawList.AddRectFilled(position, position + new Vector2(20, 20), ImGui.ColorConvertFloat4ToU32(new Vector4(0.05f, 0.06f, 0.07f, 0.55f)), 3f);
        drawList.AddRect(position, position + new Vector2(20, 20), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.16f)), 3f);

        var fallback = string.IsNullOrWhiteSpace(label) ? "?" : label[..1].ToUpperInvariant();
        var textSize = ImGui.CalcTextSize(fallback);
        drawList.AddText(position + new Vector2((20f - textSize.X) * 0.5f, (20f - textSize.Y) * 0.5f), textColor, fallback);
    }

    private static bool DrawJobIcon(ImDrawListPtr drawList, string? classNameOrShorthand, Vector2 position)
    {
        var iconId = GetJobIconId(classNameOrShorthand);
        if (iconId == null)
            return false;

        var texture = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId.Value)).GetWrapOrDefault();
        if (texture == null)
            return false;

        drawList.AddImage(texture.Handle, position, position + new Vector2(20, 20));
        return true;
    }

    private static uint? GetJobIconId(string? classNameOrShorthand)
    {
        if (string.IsNullOrWhiteSpace(classNameOrShorthand))
            return null;

        if (JobIconCache.TryGetValue(classNameOrShorthand, out var cached))
            return cached;

        foreach (var job in Plugin.DataManager.GetExcelSheet<ClassJob>())
        {
            if (job.Abbreviation.ToString().Equals(classNameOrShorthand, StringComparison.OrdinalIgnoreCase) ||
                job.Name.ToString().Equals(classNameOrShorthand, StringComparison.OrdinalIgnoreCase))
            {
                var iconId = 62100u + job.RowId;
                JobIconCache[classNameOrShorthand] = iconId;
                return iconId;
            }
        }

        JobIconCache[classNameOrShorthand] = null;
        return null;
    }

    private static bool IsBenchSlot(FullPartyRosterSlot slot)
    {
        return slot.GroupKey.Contains("bench", StringComparison.OrdinalIgnoreCase) ||
               slot.GroupLabel.Contains("bench", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasPhantomJob(FullPartyRosterSlot slot)
    {
        return slot.PhantomJobId != null ||
               !string.IsNullOrWhiteSpace(slot.PhantomJob) ||
               !string.IsNullOrWhiteSpace(slot.PhantomJobIconUrl) ||
               slot.PhantomJobIconUrls.Count > 0 ||
               slot.PhantomJobIconId is > 0;
    }

    private static string GetLiveMemberRole(FullPartyLiveMember member)
    {
        if (member.IsHost)
            return "Host";

        if (member.IsPartyLead)
            return "Party lead";

        return "Connected";
    }

    private static Vector4 GetFilledSlotBackground(string? role, bool hovered)
    {
        var alpha = hovered ? 0.62f : 0.42f;
        if (role?.Contains("tank", StringComparison.OrdinalIgnoreCase) == true)
            return new Vector4(0.10f, 0.24f, 0.46f, alpha);

        if (role?.Contains("heal", StringComparison.OrdinalIgnoreCase) == true)
            return new Vector4(0.10f, 0.34f, 0.18f, alpha);

        if (role?.Contains("dps", StringComparison.OrdinalIgnoreCase) == true ||
            role?.Contains("melee", StringComparison.OrdinalIgnoreCase) == true ||
            role?.Contains("ranged", StringComparison.OrdinalIgnoreCase) == true ||
            role?.Contains("caster", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new Vector4(0.42f, 0.12f, 0.12f, alpha);
        }

        return hovered
            ? new Vector4(0.25f, 0.30f, 0.36f, 0.38f)
            : new Vector4(0.10f, 0.12f, 0.15f, 0.28f);
    }

    private static string TrimToWidth(string value, float maxWidth)
    {
        if (string.IsNullOrEmpty(value) || ImGui.CalcTextSize(value).X <= maxWidth)
            return value;

        const string ellipsis = "...";
        var trimmed = value;
        while (trimmed.Length > 0 && ImGui.CalcTextSize(trimmed + ellipsis).X > maxWidth)
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Length == 0 ? ellipsis : trimmed + ellipsis;
    }

    private void RefreshRun()
    {
        detail = null;
        detailError = null;
        detailTask = plugin.ApiClient.GetRunDetailAsync(Run.Id, cancellation.Token);
    }

    private void EnsureDetailLoaded()
    {
        if (detail != null || detailError != null)
            return;

        if (detailTask == null)
        {
            detailTask = plugin.ApiClient.GetRunDetailAsync(Run.Id, cancellation.Token);
            return;
        }

        if (!detailTask.IsCompleted)
            return;

        if (detailTask.IsCompletedSuccessfully)
        {
            detail = detailTask.Result;
            if (detail == null)
                detailError = "FullParty returned an empty run.";
        }
        else
        {
            detailError = "Could not load this run yet.";
            Plugin.Log.Warning(detailTask.Exception, "Could not load FullParty run {RunId}", Run.Id);
        }

        detailTask = null;
    }
}
