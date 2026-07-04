using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FullParty.Models;

namespace FullParty.Windows;

public sealed class ApplicationWindow : Window, IDisposable
{
    private static readonly Vector4 SectionTitleColor = new(0.75f, 0.67f, 1f, 1f);
    private static readonly Vector4 GreenPill = new(0.02f, 0.30f, 0.16f, 0.95f);
    private static readonly Vector4 RedPill = new(0.32f, 0.08f, 0.11f, 0.95f);
    private static readonly Vector4 AmberPill = new(0.38f, 0.28f, 0.02f, 0.95f);

    private readonly Plugin plugin;
    private readonly CancellationTokenSource cancellation = new();
    private Task<FullPartyApplication?>? applicationTask;
    private FullPartyApplication? application;
    private string? error;
    private int transientId;

    public int RunId { get; }
    public int SlotId { get; }

    public ApplicationWindow(int runId, int slotId, string title, Plugin plugin)
        : base($"{title} Application##FullPartyApplication{runId}_{slotId}")
    {
        RunId = runId;
        SlotId = slotId;
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(840, 560),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        IsOpen = true;
    }

    public void Dispose()
    {
        cancellation.Cancel();
        cancellation.Dispose();
    }

    public override void Draw()
    {
        EnsureLoaded();

        if (applicationTask is { IsCompleted: false })
        {
            ImGui.TextDisabled("Loading application...");
            return;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            ImGui.TextWrapped(error);
            return;
        }

        if (application == null)
        {
            ImGui.TextDisabled("No application loaded.");
            return;
        }

        transientId = 0;
        DrawHeader(application);
        ImGui.Separator();
        ImGui.Spacing();

        var available = ImGui.GetContentRegionAvail();
        var rightWidth = Math.Clamp(available.X * 0.38f, 320f, 430f);
        var leftWidth = Math.Max(360f, available.X - rightWidth - ImGui.GetStyle().ItemSpacing.X);

        using (var left = ImRaii.Child("##application_left", new Vector2(leftWidth, available.Y), false))
        {
            if (left.Success)
                DrawLeftColumn(application);
        }

        ImGui.SameLine();

        using var right = ImRaii.Child("##application_right", Vector2.Zero, false);
        if (right.Success)
            DrawRightColumn(application);
    }

    private void DrawHeader(FullPartyApplication app)
    {
        var character = app.Details?.SelectedCharacter;
        var name = character?.Name ?? app.SelectedCharacter?.Name ?? app.UserName;
        var subtitle = $"{app.UserName} - {character?.World ?? app.SelectedCharacter?.World ?? "-"}";
        var avatarUrl = character?.AvatarUrl ?? app.SelectedCharacter?.AvatarUrl ?? app.UserAvatarUrl;

        if (!DrawRemoteImage(avatarUrl, $"application-header-{app.Id}", new Vector2(42f, 42f)))
        {
            ImGui.Dummy(new Vector2(42f, 42f));
            var min = ImGui.GetItemRectMin();
            ImGui.GetWindowDrawList().AddRectFilled(min, min + new Vector2(42f, 42f), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.10f)), 21f);
        }

        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.Text(name);
        ImGui.TextDisabled(subtitle);
        ImGui.EndGroup();
    }

    private void DrawLeftColumn(FullPartyApplication app)
    {
        DrawApplicantPanel(app);
        DrawPreferredClassesPanel(app);
        DrawPreferredPhantomJobsPanel(app);
        DrawPreferredRaidPositionsPanel(app);
        DrawApplicationDetailsPanel(app);
    }

    private void DrawRightColumn(FullPartyApplication app)
    {
        DrawProgressPanel(app);
        DrawUserHistoryPanel(app);
    }

    private void DrawApplicantPanel(FullPartyApplication app)
    {
        using var panel = ImRaii.Child("##applicant_panel", new Vector2(0, 154f), true);
        if (!panel.Success)
            return;

        DrawSectionTitle("Applicant");
        var details = app.Details;
        var character = details?.SelectedCharacter ?? details?.ApplicantCharacter;
        var lastChecked = character?.LodestoneLastCheckedAt;

        var left = new (string Label, string Value)[]
        {
            ("Account", app.UserName),
            ("Submitted", app.SubmittedAt.ToLocalTime().ToString("dd/MM/yyyy, HH:mm")),
            ("Datacenter", character?.Datacenter ?? app.SelectedCharacter?.Datacenter ?? "-"),
            ("Phantom Mastery", character?.PhantomMastery?.ToString() ?? "-"),
        };

        var right = new (string Label, string Value)[]
        {
            ("Character", character?.Name ?? app.SelectedCharacter?.Name ?? "-"),
            ("World", character?.World ?? app.SelectedCharacter?.World ?? "-"),
            ("Occult Level", character?.OccultLevel?.ToString() ?? "-"),
            ("Last checked", FormatRelative(lastChecked)),
        };

        DrawKeyValueColumns(left, right);
    }

    private void DrawPreferredClassesPanel(FullPartyApplication app)
    {
        var items = GetPreferredItems(app, answer => IsClassAnswer(answer)).ToList();
        DrawChipSection("Preferred Character Classes", "##preferred_classes", items, 360f, GetRoleColor);
    }

    private void DrawPreferredPhantomJobsPanel(FullPartyApplication app)
    {
        var items = GetPreferredItems(app, answer => IsPhantomJobAnswer(answer)).ToList();
        DrawChipSection("Preferred Phantom Jobs", "##preferred_phantom_jobs", items, 340f, _ => new Vector4(0.06f, 0.16f, 0.28f, 0.95f));
    }

    private void DrawPreferredRaidPositionsPanel(FullPartyApplication app)
    {
        var labels = GetPreferredRaidPositions(app).ToList();
        if (labels.Count == 0)
            return;

        var height = GetChipSectionHeight(labels.Select(label => GetChipWidth(label, false)), ImGui.GetContentRegionAvail().X, 180f);
        using var panel = ImRaii.Child("##preferred_raid_positions", new Vector2(0, height), true);
        if (!panel.Success)
            return;

        DrawSectionTitle("Preferred Raid Positions");
        DrawWrappedChips(labels.Select(label => (Label: label, IconUrl: (string?)null, Color: AmberPill)));
    }

    private void DrawApplicationDetailsPanel(FullPartyApplication app)
    {
        var cards = GetApplicationDetailCards(app).ToList();
        if (cards.Count == 0)
            return;

        var rows = (int)Math.Ceiling(cards.Count / 3f);
        using var panel = ImRaii.Child("##application_details", new Vector2(0, 74f + (rows * 94f)), true);
        if (!panel.Success)
            return;

        DrawSectionTitle("Application Details");

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var cardWidth = MathF.Max(120f, (ImGui.GetContentRegionAvail().X - (spacing * 2f)) / 3f);
        for (var i = 0; i < cards.Count; i++)
        {
            if (i % 3 != 0)
                ImGui.SameLine();

            DrawDetailCard(cards[i].Label, cards[i].Value, cards[i].Tone, cardWidth);
        }
    }

    private void DrawProgressPanel(FullPartyApplication app)
    {
        var bosses = app.Details?.SelectedCharacter?.BloodProgress?.Bosses ?? [];
        if (bosses.Count == 0)
            return;

        using var panel = ImRaii.Child("##fflogs_progress", new Vector2(0, 198f), true);
        if (!panel.Success)
            return;

        DrawSectionTitle("FF Logs Progress");
        var labels = app.Details?.ProgressMilestones.ToDictionary(milestone => milestone.Key, milestone => milestone.Label, StringComparer.OrdinalIgnoreCase) ?? [];

        foreach (var boss in bosses)
        {
            var label = labels.TryGetValue(boss.Key, out var milestoneLabel) ? milestoneLabel : FormatKeyLabel(boss.Key);
            DrawProgressRow(label, boss.Kills ?? 0, boss.ProgressPercent ?? 0);
        }
    }

    private void DrawUserHistoryPanel(FullPartyApplication app)
    {
        var stats = app.Details?.UserStats;
        if (stats == null)
            return;

        using var panel = ImRaii.Child("##user_history", new Vector2(0, 206f), true);
        if (!panel.Success)
            return;

        DrawSectionTitle("User History");
        DrawHistoryBucket("Most Played Class", stats.Class);
        ImGui.Spacing();
        DrawHistoryBucket("Most Played Phantom Job", stats.PhantomJob);
    }

    private void DrawChipSection(string title, string id, IReadOnlyList<FullPartyApplicationDisplayItem> items, float maxHeight, Func<string?, Vector4> colorSelector)
    {
        if (items.Count == 0)
            return;

        var height = GetChipSectionHeight(items.Select(item => GetChipWidth(item.Label, !string.IsNullOrWhiteSpace(GetBestIconUrl(item)))), ImGui.GetContentRegionAvail().X, maxHeight);
        using var panel = ImRaii.Child(id, new Vector2(0, height), true);
        if (!panel.Success)
            return;

        DrawSectionTitle(title);
        DrawWrappedChips(items.Select(item => (Label: item.Label, IconUrl: GetBestIconUrl(item), Color: colorSelector(item.Role))));
    }

    private static float GetChipSectionHeight(IEnumerable<float> chipWidths, float contentWidth, float maxHeight)
    {
        var widths = chipWidths.ToList();
        if (widths.Count == 0)
            return 0;

        var availableWidth = MathF.Max(140f, contentWidth - 24f);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var rows = 1;
        var lineWidth = 0f;

        foreach (var width in widths.Select(width => MathF.Min(width, availableWidth)))
        {
            if (lineWidth > 0f && lineWidth + spacing + width > availableWidth)
            {
                rows++;
                lineWidth = width;
            }
            else
            {
                lineWidth += lineWidth > 0f ? spacing + width : width;
            }
        }

        return Math.Clamp(58f + (rows * 34f), 112f, maxHeight);
    }

    private void DrawKeyValueColumns(IReadOnlyList<(string Label, string Value)> left, IReadOnlyList<(string Label, string Value)> right)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var columnWidth = (width - ImGui.GetStyle().ItemSpacing.X) * 0.5f;

        ImGui.BeginGroup();
        foreach (var item in left)
        {
            DrawKeyValue(item.Label, item.Value, columnWidth);
        }
        ImGui.EndGroup();

        ImGui.SameLine();

        ImGui.BeginGroup();
        foreach (var item in right)
        {
            DrawKeyValue(item.Label, item.Value, columnWidth);
        }
        ImGui.EndGroup();
    }

    private static void DrawKeyValue(string label, string value, float width)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine(MathF.Max(96f, width * 0.42f));
        ImGui.Text(value);
    }

    private void DrawDetailCard(string label, string value, Vector4 tone, float width)
    {
        using var card = ImRaii.Child($"##detail_card_{NextId()}", new Vector2(width, 80f), true);
        if (!card.Success)
            return;

        ImGui.TextWrapped(label);
        ImGui.Spacing();
        if (value.Length > 24)
            ImGui.TextWrapped(value);
        else
            DrawSmallPill(value, tone);
    }

    private void DrawProgressRow(string label, int kills, int percent)
    {
        ImGui.Text(label);
        var right = ImGui.GetWindowContentRegionMax().X;
        var percentText = $"{percent}%";
        var percentWidth = ImGui.CalcTextSize(percentText).X + 18f;
        var killsText = $"{kills} kills";
        var killsWidth = ImGui.CalcTextSize(killsText).X + 18f;

        ImGui.SameLine(right - percentWidth - killsWidth - 12f);
        DrawSmallPill(killsText, new Vector4(0.15f, 0.13f, 0.20f, 0.95f));
        ImGui.SameLine();
        DrawSmallPill(percentText, percent >= 100 ? GreenPill : percent > 0 ? AmberPill : RedPill);
    }

    private void DrawHistoryBucket(string label, FullPartyApplicationStatBucket? bucket)
    {
        ImGui.Text(label);
        ImGui.TextDisabled("With Group");
        ImGui.SameLine(ImGui.GetContentRegionAvail().X * 0.52f);
        ImGui.TextDisabled("Overall");

        DrawHistoryItem(bucket?.Group.FirstOrDefault());
        ImGui.SameLine(ImGui.GetContentRegionAvail().X * 0.52f);
        DrawHistoryItem(bucket?.Overall.FirstOrDefault());
    }

    private void DrawHistoryItem(FullPartyApplicationStatItem? item)
    {
        if (item == null)
        {
            ImGui.TextDisabled("-");
            return;
        }

        var iconUrl = item.TransparentIconUrl ?? item.FlatIconUrl ?? item.IconUrl;
        if (DrawRemoteImage(iconUrl, $"history-{item.Key}-{NextId()}", new Vector2(18f, 18f)))
            ImGui.SameLine();

        ImGui.Text($"{item.Label} ({item.Count})");
    }

    private void DrawSmallPill(string label, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, color + new Vector4(0.04f, 0.04f, 0.04f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, color);
        ImGui.Button($"{label}##pill_{NextId()}", new Vector2(ImGui.CalcTextSize(label).X + 18f, 24f));
        ImGui.PopStyleColor(3);
    }

    private void DrawWrappedChips(IEnumerable<(string Label, string? IconUrl, Vector4 Color)> chips)
    {
        var items = chips.ToList();
        if (items.Count == 0)
            return;

        var availableWidth = MathF.Max(120f, ImGui.GetContentRegionAvail().X);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var lineWidth = 0f;

        foreach (var item in items)
        {
            var hasIcon = !string.IsNullOrWhiteSpace(item.IconUrl);
            var width = MathF.Min(GetChipWidth(item.Label, hasIcon), availableWidth);
            if (lineWidth > 0f && lineWidth + spacing + width <= availableWidth)
            {
                ImGui.SameLine();
                lineWidth += spacing + width;
            }
            else
            {
                lineWidth = width;
            }

            DrawChip(item.Label, item.IconUrl, item.Color, width);
        }
    }

    private static float GetChipWidth(string label, bool hasIcon)
    {
        var buttonLabel = hasIcon ? $"   {label}" : label;
        return ImGui.CalcTextSize(buttonLabel).X + 20f;
    }

    private void DrawChip(string label, string? iconUrl, Vector4 color, float width)
    {
        var hasIcon = !string.IsNullOrWhiteSpace(iconUrl);
        var buttonLabel = hasIcon ? $"   {label}" : label;
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, color + new Vector4(0.04f, 0.04f, 0.04f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, color);
        ImGui.Button($"{buttonLabel}##chip_{NextId()}", new Vector2(width, 26f));
        ImGui.PopStyleColor(3);

        if (hasIcon)
        {
            var min = ImGui.GetItemRectMin();
            DrawRemoteImageAt(iconUrl, $"chip-icon-{label}-{NextId()}", min + new Vector2(6f, 5f), new Vector2(16f, 16f));
        }
    }

    private bool DrawRemoteImage(string? url, string key, Vector2 size)
    {
        var path = plugin.ImageCache.GetImagePath(url, key);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        var texture = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
        if (texture == null)
            return false;

        ImGui.Image(texture.Handle, size);
        return true;
    }

    private bool DrawRemoteImageAt(string? url, string key, Vector2 position, Vector2 size)
    {
        var path = plugin.ImageCache.GetImagePath(url, key);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        var texture = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
        if (texture == null)
            return false;

        ImGui.GetWindowDrawList().AddImage(texture.Handle, position, position + size);
        return true;
    }

    private IEnumerable<FullPartyApplicationDisplayItem> GetPreferredItems(FullPartyApplication app, Func<FullPartyApplicationDetailedAnswer, bool> predicate)
    {
        foreach (var answer in app.Details?.Answers.Where(predicate) ?? [])
        {
            if (answer.DisplayItems.Count > 0)
            {
                foreach (var item in answer.DisplayItems)
                    yield return item;
            }
            else
            {
                foreach (var value in answer.DisplayValues)
                    yield return new FullPartyApplicationDisplayItem(FormatValueLabel(value), null, null, null, null);
            }
        }
    }

    private IEnumerable<string> GetPreferredRaidPositions(FullPartyApplication app)
    {
        foreach (var answer in app.Details?.Answers.Where(IsRaidPositionAnswer) ?? [])
        {
            foreach (var value in answer.DisplayValues)
                yield return FormatValueLabel(value);
        }

        foreach (var answer in app.Answers.Where(IsSimpleRaidPositionAnswer))
        {
            if (!string.IsNullOrWhiteSpace(answer.Value))
                yield return FormatValueLabel(answer.Value);
        }
    }

    private IEnumerable<(string Label, string Value, Vector4 Tone)> GetApplicationDetailCards(FullPartyApplication app)
    {
        if (!string.IsNullOrWhiteSpace(app.Notes))
            yield return ("Notes", app.Notes, new Vector4(0.12f, 0.14f, 0.18f, 0.95f));

        foreach (var answer in app.Details?.Answers.Where(answer => !IsSpecialAnswer(answer)) ?? [])
        {
            var value = answer.DisplayValues.Count == 0 ? "-" : string.Join(", ", answer.DisplayValues.Select(FormatValueLabel));
            yield return (answer.QuestionLabel, value, GetAnswerTone(value));
        }

        foreach (var answer in app.Answers.Where(answer => !IsSimpleSpecialAnswer(answer)))
        {
            yield return (answer.QuestionLabel, FormatValueLabel(answer.Value ?? "-"), GetAnswerTone(answer.Value));
        }
    }

    private static bool IsSpecialAnswer(FullPartyApplicationDetailedAnswer answer)
    {
        return IsClassAnswer(answer) || IsPhantomJobAnswer(answer) || IsRaidPositionAnswer(answer);
    }

    private static bool IsSimpleSpecialAnswer(FullPartyApplicationAnswer answer)
    {
        return IsSimpleClassAnswer(answer) || IsSimplePhantomJobAnswer(answer) || IsSimpleRaidPositionAnswer(answer);
    }

    private static bool IsClassAnswer(FullPartyApplicationDetailedAnswer answer)
    {
        return answer.Source?.Equals("character_classes", StringComparison.OrdinalIgnoreCase) == true ||
               answer.QuestionKey.Contains("class", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPhantomJobAnswer(FullPartyApplicationDetailedAnswer answer)
    {
        return answer.Source?.Equals("phantom_jobs", StringComparison.OrdinalIgnoreCase) == true ||
               answer.QuestionKey.Contains("phantom", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRaidPositionAnswer(FullPartyApplicationDetailedAnswer answer)
    {
        return answer.QuestionKey.Contains("raid", StringComparison.OrdinalIgnoreCase) ||
               answer.QuestionKey.Contains("role", StringComparison.OrdinalIgnoreCase) ||
               answer.QuestionKey.Contains("position", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSimpleClassAnswer(FullPartyApplicationAnswer answer)
    {
        return answer.QuestionKey.Contains("class", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSimplePhantomJobAnswer(FullPartyApplicationAnswer answer)
    {
        return answer.QuestionKey.Contains("phantom", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSimpleRaidPositionAnswer(FullPartyApplicationAnswer answer)
    {
        return answer.QuestionKey.Contains("raid", StringComparison.OrdinalIgnoreCase) ||
               answer.QuestionKey.Contains("role", StringComparison.OrdinalIgnoreCase) ||
               answer.QuestionKey.Contains("position", StringComparison.OrdinalIgnoreCase);
    }

    private static Vector4 GetRoleColor(string? role)
    {
        if (role?.Contains("tank", StringComparison.OrdinalIgnoreCase) == true)
            return new Vector4(0.08f, 0.18f, 0.36f, 0.95f);

        if (role?.Contains("heal", StringComparison.OrdinalIgnoreCase) == true)
            return new Vector4(0.08f, 0.28f, 0.18f, 0.95f);

        if (role?.Contains("dps", StringComparison.OrdinalIgnoreCase) == true)
            return new Vector4(0.34f, 0.10f, 0.10f, 0.95f);

        return new Vector4(0.16f, 0.13f, 0.20f, 0.95f);
    }

    private static Vector4 GetAnswerTone(string? value)
    {
        if (value?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true)
            return GreenPill;

        if (value?.Equals("no", StringComparison.OrdinalIgnoreCase) == true)
            return RedPill;

        return AmberPill;
    }

    private static string? GetBestIconUrl(FullPartyApplicationDisplayItem item)
    {
        return item.TransparentIconUrl ?? item.FlatIconUrl ?? item.IconUrl;
    }

    private static string FormatRelative(DateTimeOffset? timestamp)
    {
        if (timestamp == null)
            return "-";

        var elapsed = DateTimeOffset.UtcNow - timestamp.Value.ToUniversalTime();
        if (elapsed.TotalMinutes < 1)
            return "just now";

        if (elapsed.TotalHours < 1)
            return $"{Math.Max(1, (int)elapsed.TotalMinutes)} minutes ago";

        if (elapsed.TotalDays < 1)
            return $"{Math.Max(1, (int)elapsed.TotalHours)} hours ago";

        return $"{Math.Max(1, (int)elapsed.TotalDays)} days ago";
    }

    private static string FormatKeyLabel(string key)
    {
        return string.Join(" ", key.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static string FormatValueLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "-";

        return FormatKeyLabel(value);
    }

    private static void DrawSectionTitle(string title)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, SectionTitleColor);
        ImGui.Text(title.ToUpperInvariant());
        ImGui.PopStyleColor();
        ImGui.Spacing();
    }

    private int NextId()
    {
        transientId++;
        return transientId;
    }

    private void EnsureLoaded()
    {
        if (application != null || error != null)
            return;

        if (applicationTask == null)
        {
            applicationTask = plugin.ApiClient.GetSlotApplicationAsync(RunId, SlotId, cancellation.Token);
            return;
        }

        if (!applicationTask.IsCompleted)
            return;

        if (applicationTask.IsCompletedSuccessfully)
        {
            application = applicationTask.Result;
            if (application == null)
                error = "FullParty returned an empty application.";
        }
        else
        {
            var exception = applicationTask.Exception?.GetBaseException();
            error = exception?.Message ?? "Could not load this application.";
            Plugin.Log.Warning(applicationTask.Exception, "Could not load FullParty application for run {RunId}, slot {SlotId}", RunId, SlotId);
        }

        applicationTask = null;
    }
}
