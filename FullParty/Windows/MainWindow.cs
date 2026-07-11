using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ElezenTools.UI;
using FullParty.Auth;
using FullParty.Models;

namespace FullParty.Windows;

public class MainWindow : Window, IDisposable
{
    private const int MaxAvatarBytes = 5 * 1024 * 1024;
    private const float SidebarWidth = 230f;
    private static readonly TimeSpan UpcomingLookahead = TimeSpan.FromMinutes(60);
    private static readonly HttpClient AvatarHttpClient = new();

    private readonly Plugin plugin;
    private readonly List<FullPartyGroup> groups = [];
    private readonly List<FullPartyUserCharacter> userCharacters = [];
    private readonly Dictionary<string, IReadOnlyList<FullPartyRun>> runsByGroupSlug = new();
    private readonly Dictionary<string, Task<IReadOnlyList<FullPartyRun>>> runLoadTasks = new();
    private readonly Dictionary<string, string> runErrors = new();
    private CancellationTokenSource avatarCancellation = new();
    private CancellationTokenSource dashboardCancellation = new();
    private Task<IReadOnlyList<FullPartyGroup>>? groupsLoadTask;
    private string? avatarUrl;
    private string? avatarPath;
    private string? avatarError;
    private string? groupsError;
    private string? charactersError;
    private Task<string?>? avatarDownloadTask;
    private long? dashboardUserId;
    private string? selectedGroupSlug;
    private Task<IReadOnlyList<FullPartyUserCharacter>>? charactersLoadTask;
    private bool isGroupBrowserOpen;
    private bool isUserPageSelected = true;
    private bool windowStylePushed;

    // We give this window a hidden ID using ##.
    // The user will see "FullParty" as window title,
    // but for ImGui the ID is "FullParty##Main".
    public MainWindow(Plugin plugin)
        : base($"FullParty v{plugin.VersionText}##Main")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 520),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
    }

    public void Dispose()
    {
        avatarCancellation.Cancel();
        avatarCancellation.Dispose();
        dashboardCancellation.Cancel();
        dashboardCancellation.Dispose();
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
        DrawAuthenticationState();
    }

    private void DrawAuthenticationState()
    {
        var auth = plugin.AuthService;
        auth.RestoreSavedSession();

        if (auth.State == AuthState.Authenticated)
        {
            DrawAuthenticated(auth.User);
            return;
        }

        DrawNotLoggedIn(auth);
    }

    private void DrawNotLoggedIn(AuthService auth)
    {
        DrawWelcomeHeader();

        var hasDeviceCode = auth.State is AuthState.WaitingForApproval or AuthState.VerifyingUser or AuthState.ReadyToFinish;
        var hasVerifiedUser = auth.State == AuthState.ReadyToFinish;

        DrawReachServerStep(auth, hasDeviceCode || hasVerifiedUser);

        if (!hasDeviceCode && !hasVerifiedUser)
            return;

        DrawConfirmationStep(auth, hasVerifiedUser);

        if (!hasVerifiedUser)
            return;

        DrawFinishStep(auth.PendingUser);
    }

    private static void DrawWelcomeHeader()
    {
        FullPartyModernPalette.SectionHeader(FontAwesomeIcon.InfoCircle, "Thank you for installing the FullParty Plugin");
        ImGui.TextWrapped("Connect your FullParty.gg account once so the plugin can safely unlock in-game companion features for your profile and character.");
        ImGui.Spacing();
        FullPartyModernPalette.SoftSeparator();
        ImGui.Spacing();
    }

    private static void DrawReachServerStep(AuthService auth, bool complete)
    {
        DrawStepHeader(1, "Reach FullParty", "Ask FullParty.gg for a temporary login code.");

        if (complete)
        {
            DrawDisabledButton("Server reached");
            return;
        }

        if (auth.State is AuthState.RequestingDeviceCode or AuthState.Refreshing)
        {
            DrawDisabledButton(auth.State == AuthState.Refreshing ? "Restoring session..." : "Reaching server...");
            return;
        }

        if (auth.State == AuthState.Error && !string.IsNullOrWhiteSpace(auth.ErrorMessage))
        {
            ImGui.TextWrapped(auth.ErrorMessage);
        }

        if (ImGui.Button("Reach to the server"))
        {
            auth.Restart();
        }
    }

    private static void DrawConfirmationStep(AuthService auth, bool complete)
    {
        DrawStepHeader(2, "Confirm in your browser", "Approve the plugin on FullParty.gg, then return to the game.");

        if (complete)
        {
            DrawDisabledButton("Confirmation received");
            return;
        }

        if (auth.State == AuthState.VerifyingUser)
        {
            DrawDisabledButton("Checking confirmation...");
            return;
        }

        ImGui.Text("Code:");
        ImGui.SameLine();
        ImGui.Text(auth.UserCode ?? string.Empty);

        if (ImGui.Button("Open confirmation page"))
        {
            auth.OpenApprovalPage();
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy code"))
        {
            ImGui.SetClipboardText(auth.UserCode ?? string.Empty);
        }

        ImGui.TextDisabled("Waiting for approval...");
    }

    private void DrawFinishStep(FullPartyUser? user)
    {
        DrawStepHeader(3, "Finish login", $"Welcome, {user?.Name ?? "FullParty user"}.");

        var character = user?.PrimaryCharacter;
        if (character != null)
        {
            ImGui.TextWrapped($"Linked character: {character.Name} - {character.World} ({character.Datacenter})");
        }

        if (ImGui.Button("Finish login process"))
        {
            plugin.AuthService.FinishLogin();
        }
    }

    private static void DrawStepHeader(int step, string title, string description)
    {
        ImGui.Spacing();
        ImGui.AlignTextToFramePadding();
        ImGui.Text($"Step {step}");
        ImGui.SameLine();
        ImGui.Text(title);
        ImGui.TextWrapped(description);
        ImGui.Spacing();
    }

    private static void DrawDisabledButton(string label)
    {
        ImGui.BeginDisabled();
        ImGui.Button(label);
        ImGui.EndDisabled();
    }

    private void DrawAuthenticated(FullPartyUser? user)
    {
        if (dashboardUserId != user?.Id)
        {
            ResetDashboardState(user?.Id);
        }

        ObserveDashboardTasks();
        EnsureGroupsLoading();
        EnsureUpcomingRunsLoading();
        if (isUserPageSelected && !isGroupBrowserOpen)
            EnsureCharactersLoading();

        var available = ImGui.GetContentRegionAvail();
        var sidebarWidth = Math.Clamp(SidebarWidth, 210f, MathF.Max(210f, available.X * 0.36f));
        var contentWidth = MathF.Max(0, available.X - sidebarWidth - ImGui.GetStyle().ItemSpacing.X);

        using (var sidebar = ImRaii.Child("##fullparty_sidebar", new Vector2(sidebarWidth, available.Y), true))
        {
            if (sidebar.Success)
            {
                DrawSidebar(user);
            }
        }

        ImGui.SameLine();

        using var content = ImRaii.Child("##fullparty_dashboard_content", new Vector2(contentWidth, available.Y), false);
        if (content.Success)
        {
            if (isGroupBrowserOpen)
            {
                using var groupBrowser = ImRaii.Child("##fullparty_group_browser", Vector2.Zero, true);
                if (groupBrowser.Success)
                    DrawGroupBrowserPanel();

                return;
            }

            if (isUserPageSelected)
            {
                using var userPage = ImRaii.Child("##fullparty_user_page", Vector2.Zero, true);
                if (userPage.Success)
                {
                    DrawCharactersContent(user);
                }

                return;
            }

            using var runsPane = ImRaii.Child("##fullparty_runs_pane", Vector2.Zero, true);
            if (runsPane.Success)
            {
                DrawRunsPanel();
            }
        }
    }

    private void DrawSidebar(FullPartyUser? user)
    {
        DrawSidebarProfile(user);
        ImGui.Spacing();
        DrawSidebarNavButton(FontAwesomeIcon.User, "User", isUserPageSelected && !isGroupBrowserOpen, () =>
        {
            isUserPageSelected = true;
            isGroupBrowserOpen = false;
        });
        DrawSidebarNavButton(FontAwesomeIcon.Cog, "Settings", false, () => plugin.ToggleSettingsUi());
        ImGui.Spacing();
        DrawSidebarSeparator("Groups");

        var footerHeight = 92f;
        var groupsHeight = MathF.Max(90f, ImGui.GetContentRegionAvail().Y - footerHeight);
        using (var groupList = ImRaii.Child("##fullparty_sidebar_groups", new Vector2(0, groupsHeight), false))
        {
            if (groupList.Success)
            {
                DrawSidebarGroups();
                DrawSidebarUpcomingRuns();
            }
        }

        ImGui.Spacing();
        DrawSidebarFooter(user);
        DrawDisconnectConfirmationPopup();
    }

    private void DrawSidebarProfile(FullPartyUser? user)
    {
        var availableWidth = ImGui.GetContentRegionAvail().X;
        const float avatarSize = 76f;
        var avatarX = MathF.Max(0, (availableWidth - avatarSize) * 0.5f);

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avatarX);
        DrawRoundUserAvatar(user, avatarSize);

        ImGui.Spacing();
        var name = user?.Name ?? "FullParty user";
        var nameWidth = ImGui.CalcTextSize(name).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (availableWidth - nameWidth) * 0.5f));
        ImGui.TextUnformatted(name);
    }

    private void DrawSidebarNavButton(FontAwesomeIcon icon, string label, bool selected, Action? onClick = null)
    {
        var width = ImGui.GetContentRegionAvail().X;
        using var color = selected
            ? ImRaii.PushColor(ImGuiCol.Button, FullPartyModernPalette.BrandSoft)
            : ImRaii.PushColor(ImGuiCol.Button, FullPartyModernPalette.Surface with { W = 0f });

        if (FullPartyModernPalette.IconButton(icon, label, width))
        {
            onClick?.Invoke();
        }
    }

    private static void DrawSidebarSeparator(string label)
    {
        var drawList = ImGui.GetWindowDrawList();
        var cursor = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var textSize = ImGui.CalcTextSize(label);
        var y = cursor.Y + (textSize.Y * 0.5f);
        var gap = 8f;
        var leftEnd = cursor.X + MathF.Max(18f, (width - textSize.X) * 0.5f - gap);
        var rightStart = cursor.X + MathF.Min(width - 18f, (width + textSize.X) * 0.5f + gap);
        var color = FullPartyModernPalette.Color(FullPartyModernPalette.BorderSoft);

        drawList.AddLine(cursor + new Vector2(0, y - cursor.Y), new Vector2(leftEnd, y), color);
        drawList.AddLine(new Vector2(rightStart, y), new Vector2(cursor.X + width, y), color);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (width - textSize.X) * 0.5f));
        ImGui.TextDisabled(label);
        ImGui.Spacing();
    }

    private void DrawSidebarGroups()
    {
        if (groupsLoadTask is { IsCompleted: false })
        {
            ImGui.TextDisabled("Loading groups...");
            return;
        }

        if (!string.IsNullOrWhiteSpace(groupsError))
        {
            ImGui.TextWrapped(groupsError);
            if (ImGui.Button("Retry groups", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
            {
                groupsError = null;
                EnsureGroupsLoading();
            }

            return;
        }

        if (groups.Count == 0)
        {
            ImGui.TextDisabled("No moderator groups found.");
            return;
        }

        var orderedGroups = GetSidebarOrderedGroups().ToList();
        foreach (var group in orderedGroups.Take(4))
        {
            var selected = !isUserPageSelected && !isGroupBrowserOpen && selectedGroupSlug == group.Slug;
            if (DrawSidebarGroupRow(group, selected))
            {
                selectedGroupSlug = group.Slug;
                isGroupBrowserOpen = false;
                isUserPageSelected = false;
            }
        }

        if (groups.Count > 4)
        {
            ImGui.Spacing();
            if (FullPartyModernPalette.IconButton(FontAwesomeIcon.List, $"Show all groups ({groups.Count})", ImGui.GetContentRegionAvail().X))
            {
                isGroupBrowserOpen = true;
                isUserPageSelected = false;
            }
        }
    }

    private IEnumerable<FullPartyGroup> GetSidebarOrderedGroups()
    {
        var favoriteSlugs = plugin.Configuration.FavoriteGroupSlugs;
        return groups
            .OrderByDescending(group => favoriteSlugs.Contains(group.Slug, StringComparer.OrdinalIgnoreCase))
            .ThenBy(group =>
            {
                var index = favoriteSlugs.FindIndex(slug => string.Equals(slug, group.Slug, StringComparison.OrdinalIgnoreCase));
                return index < 0 ? int.MaxValue : index;
            })
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase);
    }

    private void DrawSidebarUpcomingRuns()
    {
        var upcomingRun = GetUpcomingRuns().FirstOrDefault();
        if (upcomingRun == null)
            return;

        ImGui.Spacing();
        DrawSidebarSeparator("Upcoming");
        DrawSidebarUpcomingRunButton(upcomingRun);
    }

    private void DrawSidebarUpcomingRunButton(FullPartyRun run)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var height = 138f;

        if (ImGui.InvisibleButton($"##sidebar_upcoming_{run.Id}", new Vector2(width, height)))
        {
            plugin.OpenRunWindow(run);
        }

        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax() - new Vector2(0, 8f);
        var rounding = 5f;
        var bg = hovered
            ? FullPartyModernPalette.BrandSoft
            : FullPartyModernPalette.Elevated;

        drawList.AddRectFilled(min, max, FullPartyModernPalette.Color(bg), rounding);
        DrawUpcomingRunBackground(run, min, max, rounding);
        drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(0.04f, 0.03f, 0.05f, 0.33f)), rounding);
        drawList.AddRectFilled(min, new Vector2(max.X, min.Y + 34f), ImGui.ColorConvertFloat4ToU32(new Vector4(0.03f, 0.02f, 0.04f, 0.18f)), rounding);
        drawList.AddRect(min, max, FullPartyModernPalette.Color(FullPartyModernPalette.BorderSoft), rounding);

        var statusLabel = string.IsNullOrWhiteSpace(run.Status) ? "Scheduled" : char.ToUpperInvariant(run.Status[0]) + run.Status[1..];
        DrawUpcomingPill(min + new Vector2(9f, 9f), FontAwesomeIcon.CalendarAlt, statusLabel);

        var visibility = run.IsPublic ? "Public" : "Private";
        var visibilitySize = ImGui.CalcTextSize(visibility) + new Vector2(16f, 7f);
        DrawUpcomingPill(new Vector2(max.X - visibilitySize.X - 9f, min.Y + 9f), null, visibility);

        var textMin = min + new Vector2(12f, 46f);
        var lineWidth = width - 24f;
        var groupName = GetGroupName(run.GroupId) ?? "FullParty";
        var relative = FormatRelativeStart(run.StartsAt);
        var starts = $"Starts {run.StartsAt:ddd d MMM, HH:mm}".ToUpperInvariant();

        drawList.AddText(textMin, FullPartyModernPalette.Color(FullPartyModernPalette.Muted), TrimToWidth(starts, lineWidth));
        drawList.AddText(textMin + new Vector2(0, 22f), ImGui.GetColorU32(ImGuiCol.Text), TrimToWidth(run.Title, lineWidth));
        drawList.AddText(textMin + new Vector2(0, 50f), FullPartyModernPalette.Color(FullPartyModernPalette.Muted), TrimToWidth($"{groupName} · {relative}", lineWidth));
    }

    private void DrawUpcomingRunBackground(FullPartyRun run, Vector2 min, Vector2 max, float rounding)
    {
        var imageUrl = run.ActivityType?.BannerImageUrl ?? run.ActivityType?.SmallImageUrl;
        var path = plugin.ImageCache.GetImagePath(imageUrl, $"sidebar-upcoming-{run.Id}");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        var texture = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
        if (texture == null)
            return;

        ImGui.GetWindowDrawList().AddImageRounded(
            texture.Handle,
            min,
            max,
            Vector2.Zero,
            Vector2.One,
            ImGui.GetColorU32(ImGuiCol.Text),
            rounding);
    }

    private static void DrawUpcomingPill(Vector2 position, FontAwesomeIcon? icon, string label)
    {
        var drawList = ImGui.GetWindowDrawList();
        var textSize = ImGui.CalcTextSize(label);
        var iconWidth = icon.HasValue ? 15f : 0f;
        var size = new Vector2(textSize.X + iconWidth + 16f, textSize.Y + 7f);
        drawList.AddRectFilled(position, position + size, FullPartyModernPalette.Color(FullPartyModernPalette.BrandSoft with { W = 0.86f }), 4f);
        drawList.AddRect(position, position + size, FullPartyModernPalette.Color(FullPartyModernPalette.BorderSoft), 4f);

        ImGui.SetCursorScreenPos(position + new Vector2(8f, 3f));
        using (ImRaii.PushColor(ImGuiCol.Text, FullPartyModernPalette.Text))
        {
            if (icon.HasValue)
            {
                ElezenImgui.ShowIcon(icon.Value);
                ImGui.SameLine(0, 4f);
            }

            ImGui.TextUnformatted(label);
        }
    }

    private static string FormatRelativeStart(DateTimeOffset startsAt)
    {
        var delta = startsAt - GetServerTimeNow();
        if (delta <= TimeSpan.Zero)
            return "starting now";

        if (delta.TotalMinutes < 60)
            return $"in {Math.Max(1, (int)Math.Ceiling(delta.TotalMinutes))} min";

        if (delta.TotalHours < 24)
            return $"in {Math.Max(1, (int)Math.Ceiling(delta.TotalHours))} hours";

        return $"in {Math.Max(1, (int)Math.Ceiling(delta.TotalDays))} days";
    }

    private void DrawGroupBrowserPanel()
    {
        ImGui.BeginGroup();
        FullPartyModernPalette.SectionHeader(FontAwesomeIcon.List, "Groups");
        ImGui.TextDisabled("Favorite groups are prioritized in the sidebar.");
        ImGui.EndGroup();

        var closeWidth = 72f;
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - closeWidth);
        if (ImGui.Button("Close", new Vector2(closeWidth, 0)))
        {
            isGroupBrowserOpen = false;
            isUserPageSelected = true;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (groupsLoadTask is { IsCompleted: false })
        {
            ImGui.TextDisabled("Loading groups...");
            return;
        }

        if (groups.Count == 0)
        {
            ImGui.TextDisabled("No moderator groups found.");
            return;
        }

        foreach (var group in GetSidebarOrderedGroups())
        {
            DrawGroupBrowserRow(group);
        }
    }

    private void DrawGroupBrowserRow(FullPartyGroup group)
    {
        var favorite = IsFavoriteGroup(group.Slug);
        var width = ImGui.GetContentRegionAvail().X;
        var rowHeight = 62f;
        var buttonWidth = 86f;

        using var row = ImRaii.Child($"##group_browser_row_{group.Id}", new Vector2(width, rowHeight), true);
        if (!row.Success)
            return;

        ImGui.BeginGroup();
        ImGui.TextUnformatted(group.Name);
        ImGui.TextDisabled($"{group.Datacenter ?? "Any DC"} - {group.Role}");
        ImGui.EndGroup();

        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - (buttonWidth * 2f) - ImGui.GetStyle().ItemSpacing.X);
        if (ImGui.Button(favorite ? "Unfav" : "Favorite", new Vector2(buttonWidth, 0)))
        {
            ToggleFavoriteGroup(group.Slug);
        }

        ImGui.SameLine();
        if (ImGui.Button("Open", new Vector2(buttonWidth, 0)))
        {
            selectedGroupSlug = group.Slug;
            isGroupBrowserOpen = false;
            isUserPageSelected = false;
        }
    }

    private bool IsFavoriteGroup(string slug)
    {
        return plugin.Configuration.FavoriteGroupSlugs.Contains(slug, StringComparer.OrdinalIgnoreCase);
    }

    private void ToggleFavoriteGroup(string slug)
    {
        var favoriteSlugs = plugin.Configuration.FavoriteGroupSlugs;
        var index = favoriteSlugs.FindIndex(existing => string.Equals(existing, slug, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            favoriteSlugs.RemoveAt(index);
        }
        else
        {
            favoriteSlugs.Add(slug);
        }

        plugin.Configuration.Save();
    }

    private bool DrawSidebarGroupRow(FullPartyGroup group, bool selected)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var height = 54f;

        if (ImGui.InvisibleButton($"##sidebar_group_{group.Id}", new Vector2(width, height)))
            return true;

        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax() - new Vector2(0, 3f);
        var bg = selected
            ? FullPartyModernPalette.BrandSoft with { W = 0.88f }
            : hovered
                ? FullPartyModernPalette.Elevated with { W = 0.70f }
                : FullPartyModernPalette.Surface with { W = 0.20f };

        drawList.AddRectFilled(min, max, FullPartyModernPalette.Color(bg), 4f);
        if (selected)
            drawList.AddRectFilled(min, new Vector2(min.X + 3f, max.Y), FullPartyModernPalette.Color(FullPartyModernPalette.Brand), 4f);

        var iconPos = min + new Vector2(10f, 9f);
        DrawSidebarGroupIcon(group, iconPos, new Vector2(34f, 34f));

        var nameColor = ImGui.GetColorU32(ImGuiCol.Text);
        var mutedColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        var textMin = min + new Vector2(52f, 8f);
        var textWidth = MathF.Max(40f, width - 62f);
        drawList.AddText(textMin, nameColor, TrimToWidth(group.Name, textWidth));
        drawList.AddText(textMin + new Vector2(0, 21f), mutedColor, TrimToWidth($"{group.Datacenter ?? "Any DC"} - {group.Role}", textWidth));
        return false;
    }

    private void DrawSidebarGroupIcon(FullPartyGroup group, Vector2 position, Vector2 size)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(position, position + size, FullPartyModernPalette.Color(FullPartyModernPalette.Elevated), 5f);

        var path = plugin.ImageCache.GetImagePath(group.ProfilePictureUrl, $"sidebar-group-{group.Id}");
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var texture = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
            if (texture != null)
            {
                drawList.AddImageRounded(
                    texture.Handle,
                    position,
                    position + size,
                    Vector2.Zero,
                    Vector2.One,
                    ImGui.GetColorU32(ImGuiCol.Text),
                    5f);
                drawList.AddRect(position, position + size, FullPartyModernPalette.Color(FullPartyModernPalette.BorderSoft), 5f);
                return;
            }
        }

        var initials = GetGroupInitials(group.Name);
        var textSize = ImGui.CalcTextSize(initials);
        drawList.AddText(position + ((size - textSize) * 0.5f), FullPartyModernPalette.Color(FullPartyModernPalette.Muted), initials);
        drawList.AddRect(position, position + size, FullPartyModernPalette.Color(FullPartyModernPalette.BorderSoft), 5f);
    }

    private static string GetGroupInitials(string name)
    {
        var words = name
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(2)
            .Select(word => word[..1].ToUpperInvariant())
            .ToArray();

        return words.Length == 0 ? "G" : string.Concat(words);
    }

    private void DrawSidebarFooter(FullPartyUser? user)
    {
        var width = ImGui.GetContentRegionAvail().X;
        if (FullPartyModernPalette.IconButton(FontAwesomeIcon.SyncAlt, "Refresh", width))
        {
            RefreshDashboard(user?.Id);
        }

        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Button, FullPartyModernPalette.Danger with { W = 0.74f }))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, FullPartyModernPalette.Danger))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, FullPartyModernPalette.Danger with { W = 0.88f }))
        {
            if (FullPartyModernPalette.IconButton(FontAwesomeIcon.SignOutAlt, "Disconnect", width))
            {
                ImGui.OpenPopup("Disconnect FullParty?##fullparty_disconnect");
            }
        }
    }

    private void ResetDashboardState(long? userId)
    {
        dashboardCancellation.Cancel();
        dashboardCancellation.Dispose();
        dashboardCancellation = new CancellationTokenSource();
        dashboardUserId = userId;
        groups.Clear();
        userCharacters.Clear();
        runsByGroupSlug.Clear();
        runLoadTasks.Clear();
        runErrors.Clear();
        groupsLoadTask = null;
        charactersLoadTask = null;
        groupsError = null;
        charactersError = null;
        selectedGroupSlug = null;
    }

    private void EnsureGroupsLoading()
    {
        if (groups.Count > 0 || groupsLoadTask != null || groupsError != null)
            return;

        groupsLoadTask = plugin.ApiClient.GetGroupsAsync(dashboardCancellation.Token);
    }

    private void EnsureRunsLoading(FullPartyGroup group)
    {
        if (runsByGroupSlug.ContainsKey(group.Slug) || runLoadTasks.ContainsKey(group.Slug) || runErrors.ContainsKey(group.Slug))
            return;

        runLoadTasks[group.Slug] = plugin.ApiClient.GetGroupRunsAsync(group.Slug, dashboardCancellation.Token);
    }

    private void EnsureCharactersLoading()
    {
        if (userCharacters.Count > 0 || charactersLoadTask != null || charactersError != null)
            return;

        charactersLoadTask = plugin.ApiClient.GetCharactersAsync(dashboardCancellation.Token);
    }

    private void EnsureUpcomingRunsLoading()
    {
        foreach (var group in groups.Where(group => group.CanModerate))
        {
            EnsureRunsLoading(group);
        }
    }

    private void ObserveDashboardTasks()
    {
        if (groupsLoadTask is { IsCompleted: true })
        {
            if (groupsLoadTask.IsCompletedSuccessfully)
            {
                groups.Clear();
                groups.AddRange(groupsLoadTask.Result);
                selectedGroupSlug ??= groups.FirstOrDefault()?.Slug;
            }
            else
            {
                groupsError = "Could not load your FullParty groups.";
                Plugin.Log.Warning(groupsLoadTask.Exception, "Could not load FullParty groups.");
            }

            groupsLoadTask = null;
        }

        foreach (var pair in runLoadTasks.Where(pair => pair.Value.IsCompleted).ToList())
        {
            if (pair.Value.IsCompletedSuccessfully)
            {
                runsByGroupSlug[pair.Key] = pair.Value.Result;
            }
            else
            {
                runErrors[pair.Key] = "Could not load future runs for this group.";
                Plugin.Log.Warning(pair.Value.Exception, "Could not load FullParty runs for group {GroupSlug}", pair.Key);
            }

            runLoadTasks.Remove(pair.Key);
        }

        if (charactersLoadTask is { IsCompleted: true })
        {
            if (charactersLoadTask.IsCompletedSuccessfully)
            {
                userCharacters.Clear();
                userCharacters.AddRange(charactersLoadTask.Result);
            }
            else
            {
                charactersError = "Could not load your FullParty characters.";
                Plugin.Log.Warning(charactersLoadTask.Exception, "Could not load FullParty characters.");
            }

            charactersLoadTask = null;
        }
    }

    private void DrawProfileStrip(FullPartyUser? user)
    {
        using var profile = ImRaii.Child("##fullparty_profile", new Vector2(0, 96), true);
        if (!profile.Success)
            return;

        if (DrawUserAvatar(user))
        {
            ImGui.SameLine();
        }

        ImGui.BeginGroup();
        ImGui.Text(user?.Name ?? "FullParty user");

        var character = user?.PrimaryCharacter;
        if (character != null)
        {
            ImGui.TextDisabled($"{character.Name} - {character.World} ({character.Datacenter})");
        }
        else
        {
            ImGui.TextDisabled("No primary character linked.");
        }

        ImGui.EndGroup();

        var buttonWidth = 92f;
        var buttonX = ImGui.GetWindowContentRegionMax().X - buttonWidth;
        ImGui.SameLine(buttonX);
        if (ImGui.Button("Disconnect", new Vector2(buttonWidth, 0)))
        {
            ImGui.OpenPopup("Disconnect FullParty?##fullparty_disconnect");
        }

        ImGui.SetCursorPosX(buttonX);
        if (ImGui.Button("Refresh", new Vector2(buttonWidth, 0)))
        {
            RefreshDashboard(user?.Id);
        }

        DrawDisconnectConfirmationPopup();
    }

    private void DrawDisconnectConfirmationPopup()
    {
        var popupOpen = true;
        if (!ImGui.BeginPopupModal("Disconnect FullParty?##fullparty_disconnect", ref popupOpen, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.TextWrapped("Disconnect FullParty and remove the saved login token?");
        ImGui.Spacing();

        if (ImGui.Button("Cancel", new Vector2(90f, 0)))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Disconnect", new Vector2(110f, 0)))
        {
            plugin.AuthService.SignOut();
            ResetDashboardState(null);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void RefreshDashboard(long? userId)
    {
        ResetDashboardState(userId);
        EnsureGroupsLoading();
    }

    private static float GetTopDashboardRowHeight(float availableHeight, int upcomingCount)
    {
        if (upcomingCount <= 0)
            return 142f;

        var desired = 52f + (upcomingCount * 72f);
        var max = MathF.Max(142f, MathF.Min(availableHeight * 0.45f, 320f));
        return Math.Clamp(desired, 142f, max);
    }

    private void DrawCharactersPanel(FullPartyUser? user, float height)
    {
        using var panel = ImRaii.Child("##fullparty_characters", new Vector2(0, height), true);
        if (!panel.Success)
            return;

        DrawCharactersContent(user);
    }

    private void DrawCharactersContent(FullPartyUser? user)
    {
        FullPartyModernPalette.SectionHeader(FontAwesomeIcon.User, "User");

        if (charactersLoadTask is { IsCompleted: false })
        {
            ImGui.TextDisabled("Loading characters...");
            return;
        }

        if (!string.IsNullOrWhiteSpace(charactersError))
        {
            ImGui.TextWrapped(charactersError);
            if (ImGui.Button("Retry characters"))
            {
                charactersError = null;
                EnsureCharactersLoading();
            }

            ImGui.Spacing();
        }

        if (userCharacters.Count > 0)
        {
            foreach (var character in userCharacters.OrderByDescending(character => character.IsPrimary).ThenBy(character => character.Name))
            {
                DrawUserCharacterCard(character);
                ImGui.Spacing();
            }

            return;
        }

        var characters = GetUserCharacters(user).ToList();
        if (characters.Count == 0)
        {
            ImGui.TextDisabled("No linked characters yet.");
            return;
        }

        foreach (var character in characters)
        {
            DrawCharacterCard(user, character);
            ImGui.Spacing();
        }
    }

    private void DrawUserCharacterCard(FullPartyUserCharacter character)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var height = 186f;

        using var card = ImRaii.Child($"##user_character_card_{character.Id}", new Vector2(width, height), true);
        if (!card.Success)
            return;

        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        drawList.AddRectFilled(min, new Vector2(min.X + 3f, max.Y), FullPartyModernPalette.Color(FullPartyModernPalette.Brand), 0f);

        var contentStart = ImGui.GetCursorPos();
        ImGui.SetCursorPos(contentStart + new Vector2(24f, 22f));

        if (!DrawCharacterPortrait(character.Id, character.AvatarUrl, new Vector2(82f, 82f), "user-character"))
            DrawCharacterPortraitFallback(character.Id, character.Name, new Vector2(82f, 82f), "user-character");

        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12f);
        ImGui.BeginGroup();

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(character.Name);
        ImGui.SameLine();
        DrawCharacterBadge(FontAwesomeIcon.Star, "Primary", character.IsPrimary, FullPartyModernPalette.Elevated);
        ImGui.SameLine();
        DrawCharacterBadge(FontAwesomeIcon.CheckCircle, "Verified", character.IsVerified, FullPartyModernPalette.Success with { W = 0.26f }, FullPartyModernPalette.Success);

        ImGui.Spacing();
        var source = FormatAddMethod(character.AddMethod);
        ImGui.TextDisabled($"{character.Datacenter}  ·  {character.World}  ·  From {source}");
        ImGui.Spacing();

        if (!string.IsNullOrWhiteSpace(character.LodestoneId))
        {
            using (ImRaii.PushColor(ImGuiCol.Text, FullPartyModernPalette.Brand))
            {
                ImGui.TextUnformatted("View Lodestone Profile");
            }

            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            if (ImGui.IsItemClicked())
                OpenExternalUrl($"https://na.finalfantasyxiv.com/lodestone/character/{character.LodestoneId}/");
        }
        else
        {
            ImGui.TextDisabled("Lodestone profile unavailable");
        }

        ImGui.EndGroup();

        ImGui.SetCursorPos(contentStart + new Vector2(24f, 120f));
        DrawCharacterClassSummary(character.Classes);

        ImGui.SameLine();
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), width * 0.52f));
        DrawOccultSummary(character.Occult);
    }

    private void DrawCharacterClassSummary(IReadOnlyList<FullPartyCharacterClass> classes)
    {
        ImGui.BeginGroup();
        ImGui.TextDisabled("Preferred Classes");

        var preferred = classes.Where(job => job.IsPreferred).Take(4).ToList();
        if (preferred.Count == 0)
        {
            ImGui.TextDisabled("-");
        }
        else
        {
            foreach (var job in preferred)
            {
                DrawCharacterIconChip(job.FlatIconUrl ?? job.IconUrl, $"class-chip-{job.Id}", $"{job.Shorthand} {job.Level?.ToString() ?? "--"}", GetRoleColor(job.Role));
                ImGui.SameLine();
            }

            ImGui.NewLine();
        }

        ImGui.EndGroup();
    }

    private void DrawOccultSummary(FullPartyCharacterOccult? occult)
    {
        ImGui.BeginGroup();
        ImGui.TextDisabled("Occult");

        if (occult == null)
        {
            ImGui.TextDisabled("-");
            ImGui.EndGroup();
            return;
        }

        ImGui.TextUnformatted($"Knowledge {occult.KnowledgeLevel?.ToString() ?? "--"}");
        ImGui.SameLine();

        var preferredJob = occult.PhantomJobs.FirstOrDefault(job => job.IsPreferred) ?? occult.PhantomJobs.FirstOrDefault();
        if (preferredJob != null)
        {
            var level = preferredJob.CurrentLevel.HasValue && preferredJob.MaxLevel.HasValue
                ? $"{preferredJob.CurrentLevel}/{preferredJob.MaxLevel}"
                : "--";
            DrawCharacterIconChip(preferredJob.TransparentIconUrl ?? preferredJob.IconUrl, $"phantom-chip-{preferredJob.Id}", $"{preferredJob.Name} {level}", FullPartyModernPalette.BrandSoft);
        }
        else
        {
            ImGui.TextDisabled("No phantom jobs");
        }

        ImGui.EndGroup();
    }

    private void DrawCharacterIconChip(string? iconUrl, string cacheKey, string label, Vector4 background)
    {
        var textSize = ImGui.CalcTextSize(label);
        var size = new Vector2(textSize.X + 32f, 24f);
        var pos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        ImGui.InvisibleButton($"##{cacheKey}_{label}", size);

        drawList.AddRectFilled(pos, pos + size, FullPartyModernPalette.Color(background with { W = MathF.Max(background.W, 0.78f) }), 4f);
        drawList.AddRect(pos, pos + size, FullPartyModernPalette.Color(FullPartyModernPalette.BorderSoft), 4f);

        var iconPath = plugin.ImageCache.GetImagePath(iconUrl, cacheKey);
        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
        {
            var texture = Plugin.TextureProvider.GetFromFile(iconPath).GetWrapOrDefault();
            if (texture != null)
            {
                drawList.AddImage(texture.Handle, pos + new Vector2(5f, 4f), pos + new Vector2(21f, 20f));
            }
        }

        drawList.AddText(pos + new Vector2(26f, 4f), ImGui.GetColorU32(ImGuiCol.Text), TrimToWidth(label, size.X - 31f));
    }

    private static Vector4 GetRoleColor(string role)
    {
        return role.ToLowerInvariant() switch
        {
            "tank" => new Vector4(0.16f, 0.30f, 0.55f, 0.82f),
            "healer" => new Vector4(0.12f, 0.42f, 0.28f, 0.82f),
            "melee" or "physical_ranged" or "magical_ranged" or "dps" => new Vector4(0.48f, 0.16f, 0.18f, 0.82f),
            _ => FullPartyModernPalette.Elevated,
        };
    }

    private static string FormatAddMethod(string? addMethod)
    {
        if (string.IsNullOrWhiteSpace(addMethod))
            return "FullParty";

        return addMethod.Equals("xivauth", StringComparison.OrdinalIgnoreCase) ? "XIVAuth" : addMethod;
    }

    private static IEnumerable<FullPartyCharacter> GetUserCharacters(FullPartyUser? user)
    {
        if (user == null)
            yield break;

        var seen = new HashSet<long>();
        foreach (var character in user.Characters)
        {
            if (seen.Add(character.Id))
                yield return character;
        }

        if (user.PrimaryCharacter != null && seen.Add(user.PrimaryCharacter.Id))
            yield return user.PrimaryCharacter;
    }

    private void DrawCharacterCard(FullPartyUser? user, FullPartyCharacter character)
    {
        var width = ImGui.GetContentRegionAvail().X;
        const float height = 150f;

        using var card = ImRaii.Child($"##character_card_{character.Id}", new Vector2(width, height), true);
        if (!card.Success)
            return;

        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        drawList.AddRectFilled(min, new Vector2(min.X + 3f, max.Y), FullPartyModernPalette.Color(FullPartyModernPalette.Brand), 0f);

        var contentStart = ImGui.GetCursorPos();
        ImGui.SetCursorPos(contentStart + new Vector2(24f, 22f));

        var portraitDrawn = DrawCharacterPortrait(character, new Vector2(82f, 82f));
        if (!portraitDrawn)
        {
            DrawCharacterPortraitFallback(character, new Vector2(82f, 82f));
        }

        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12f);
        ImGui.BeginGroup();

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(character.Name);
        ImGui.SameLine();
        DrawCharacterBadge(FontAwesomeIcon.Star, "Primary", IsPrimaryCharacter(user, character), FullPartyModernPalette.Elevated);
        ImGui.SameLine();
        DrawCharacterBadge(FontAwesomeIcon.CheckCircle, "Verified", character.IsVerified != false, FullPartyModernPalette.Success with { W = 0.26f }, FullPartyModernPalette.Success);

        ImGui.Spacing();
        var source = string.IsNullOrWhiteSpace(character.Source) ? "FullParty" : character.Source;
        ImGui.TextDisabled($"{character.Datacenter}  ·  {character.World}  ·  From {source}");
        ImGui.Spacing();

        if (!string.IsNullOrWhiteSpace(character.LodestoneId))
        {
            using (ImRaii.PushColor(ImGuiCol.Text, FullPartyModernPalette.Brand))
            {
                ImGui.TextUnformatted("View Lodestone Profile");
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }

            if (ImGui.IsItemClicked())
            {
                OpenExternalUrl($"https://na.finalfantasyxiv.com/lodestone/character/{character.LodestoneId}/");
            }
        }
        else
        {
            ImGui.TextDisabled("Lodestone profile unavailable");
        }

        ImGui.EndGroup();
    }

    private bool DrawCharacterPortrait(FullPartyCharacter character, Vector2 size)
    {
        return DrawCharacterPortrait(character.Id, character.AvatarUrl, size, "main-character");
    }

    private bool DrawCharacterPortrait(long characterId, string? avatarUrl, Vector2 size, string cachePrefix)
    {
        var path = plugin.ImageCache.GetImagePath(avatarUrl, $"{cachePrefix}-{characterId}");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        var texture = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
        if (texture == null)
            return false;

        var position = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##character_portrait_{cachePrefix}_{characterId}", size);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(position + new Vector2(4f), position + size + new Vector2(4f), FullPartyModernPalette.Color(FullPartyModernPalette.BrandSoft), 2f);
        drawList.AddImage(texture.Handle, position, position + size);
        drawList.AddRect(position, position + size, FullPartyModernPalette.Color(FullPartyModernPalette.Brand), 2f, ImDrawFlags.None, 1.5f);
        return true;
    }

    private static void DrawCharacterPortraitFallback(FullPartyCharacter character, Vector2 size)
    {
        DrawCharacterPortraitFallback(character.Id, character.Name, size, "main-character");
    }

    private static void DrawCharacterPortraitFallback(long characterId, string characterName, Vector2 size, string cachePrefix)
    {
        var position = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##character_portrait_fallback_{cachePrefix}_{characterId}", size);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(position, position + size, FullPartyModernPalette.Color(FullPartyModernPalette.Elevated), 2f);
        drawList.AddRect(position, position + size, FullPartyModernPalette.Color(FullPartyModernPalette.BorderSoft), 2f);
        var initial = string.IsNullOrWhiteSpace(characterName) ? "?" : characterName[..1].ToUpperInvariant();
        var textSize = ImGui.CalcTextSize(initial);
        drawList.AddText(position + ((size - textSize) * 0.5f), FullPartyModernPalette.Color(FullPartyModernPalette.Muted), initial);
    }

    private static void DrawCharacterBadge(FontAwesomeIcon icon, string label, bool visible, Vector4 background, Vector4? textColor = null)
    {
        if (!visible)
            return;

        var padding = new Vector2(7f, 3f);
        var textSize = ImGui.CalcTextSize(label);
        var badgeSize = new Vector2(textSize.X + 25f, textSize.Y + (padding.Y * 2f));
        var position = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        ImGui.InvisibleButton($"##badge_{label}_{position.X}_{position.Y}", badgeSize);
        drawList.AddRectFilled(position, position + badgeSize, FullPartyModernPalette.Color(background), 4f);
        ImGui.SetCursorScreenPos(position + new Vector2(7f, 3f));
        using (ImRaii.PushColor(ImGuiCol.Text, textColor ?? FullPartyModernPalette.Text))
        {
            ElezenImgui.ShowIcon(icon);
            ImGui.SameLine(0, 4f);
            ImGui.TextUnformatted(label);
        }
    }

    private static bool IsPrimaryCharacter(FullPartyUser? user, FullPartyCharacter character)
    {
        return character.IsPrimary == true || user?.PrimaryCharacter?.Id == character.Id;
    }

    private static void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not open FullParty external URL {Url}", url);
        }
    }

    private void DrawGroupsPanel()
    {
        using var panel = ImRaii.Child("##fullparty_groups", Vector2.Zero, true);
        if (!panel.Success)
            return;

        DrawPanelHeader("Groups");

        if (groupsLoadTask is { IsCompleted: false })
        {
            ImGui.TextDisabled("Loading groups...");
            return;
        }

        if (!string.IsNullOrWhiteSpace(groupsError))
        {
            ImGui.TextWrapped(groupsError);
            if (ImGui.Button("Retry groups"))
            {
                groupsError = null;
                EnsureGroupsLoading();
            }

            return;
        }

        if (groups.Count == 0)
        {
            ImGui.TextDisabled("No moderator groups found.");
            return;
        }

        foreach (var group in groups)
        {
            var selected = selectedGroupSlug == group.Slug;
            if (ImGui.Selectable($"{group.Name}##group_{group.Id}", selected))
            {
                selectedGroupSlug = group.Slug;
            }

            ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - 72f);
            ImGui.TextDisabled(group.CanModerate ? "moderator" : group.Role);
            ImGui.TextDisabled($"{group.Datacenter ?? "Any DC"} - {group.Slug}");
            ImGui.Spacing();
        }
    }

    private void DrawRunsPanel()
    {
        if (groupsLoadTask is { IsCompleted: false })
        {
            ImGui.TextDisabled("Loading groups...");
            return;
        }

        var group = groups.FirstOrDefault(group => group.Slug == selectedGroupSlug) ?? groups.FirstOrDefault();
        if (group == null)
        {
            ImGui.TextDisabled("No groups loaded.");
            return;
        }

        selectedGroupSlug = group.Slug;
        EnsureRunsLoading(group);

        DrawPanelHeader(group.Name);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (runLoadTasks.ContainsKey(group.Slug))
        {
            ImGui.TextDisabled("Loading future runs...");
            return;
        }

        if (runErrors.TryGetValue(group.Slug, out var error))
        {
            ImGui.TextWrapped(error);
            if (ImGui.Button("Retry runs"))
            {
                runErrors.Remove(group.Slug);
                EnsureRunsLoading(group);
            }

            return;
        }

        if (!runsByGroupSlug.TryGetValue(group.Slug, out var runs) || runs.Count == 0)
        {
            ImGui.TextDisabled("No future runs scheduled.");
            return;
        }

        foreach (var run in runs.OrderBy(run => run.StartsAt))
        {
            DrawRunRow(run);
            ImGui.Dummy(new Vector2(1f, 10f));
        }
    }

    private void DrawUpcomingRunsPanel(IReadOnlyList<FullPartyRun> upcomingRuns)
    {
        DrawPanelHeader("Upcoming");
        ImGui.TextDisabled("Starts within 60 minutes (ST).");
        ImGui.Spacing();

        foreach (var run in upcomingRuns)
        {
            DrawRunRow(run, "upcoming_run", GetGroupName(run.GroupId));
        }
    }

    private List<FullPartyRun> GetUpcomingRuns()
    {
        var now = GetServerTimeNow();
        var cutoff = now.Add(UpcomingLookahead);

        return runsByGroupSlug.Values
            .SelectMany(runs => runs)
            .Where(run => run.CanModerate == true)
            .Where(run => run.StartsAt >= now && run.StartsAt <= cutoff)
            .GroupBy(run => run.Id)
            .Select(group => group.OrderBy(run => run.StartsAt).First())
            .OrderBy(run => run.StartsAt)
            .ToList();
    }

    private static DateTimeOffset GetServerTimeNow()
    {
        var frameworkUtc = Plugin.Framework.LastUpdateUTC;
        return frameworkUtc == default ? DateTimeOffset.UtcNow : frameworkUtc;
    }

    private string? GetGroupName(int groupId)
    {
        return groups.FirstOrDefault(group => group.Id == groupId)?.Name;
    }

    private void DrawRunRow(FullPartyRun run, string idScope = "run", string? subtitlePrefix = null)
    {
        var width = ImGui.GetContentRegionAvail().X;
        const float cardHeight = 136f;

        using var card = ImRaii.Child($"##{idScope}_card_{run.Id}", new Vector2(width, cardHeight), true);
        if (!card.Success)
            return;

        var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            plugin.OpenRunWindow(run);

        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        var bg = hovered ? FullPartyModernPalette.Elevated with { W = 0.92f } : FullPartyModernPalette.Surface with { W = 0.62f };
        drawList.AddRectFilled(min, max, FullPartyModernPalette.Color(bg), 4f);
        drawList.AddRect(min, max, FullPartyModernPalette.Color(FullPartyModernPalette.BorderSoft), 4f);

        var artworkWidth = 112f;
        var contentWidth = max.X - min.X;
        var scheduleWidth = Math.Clamp(contentWidth * 0.24f, 190f, 260f);
        var metaWidth = Math.Clamp(contentWidth * 0.17f, 130f, 180f);
        var mainStartX = min.X + artworkWidth + 16f;
        var scheduleStartX = max.X - scheduleWidth - metaWidth;
        var metaStartX = max.X - metaWidth;

        DrawRunArtwork(run, min + new Vector2(1f, 1f), new Vector2(artworkWidth, max.Y - min.Y - 2f));
        DrawVerticalDivider(scheduleStartX, min.Y, max.Y);
        DrawVerticalDivider(metaStartX, min.Y, max.Y);

        var left = new Vector2(mainStartX, min.Y + 20f);
        var titleWidth = MathF.Max(120f, scheduleStartX - mainStartX - 20f);
        var textColor = ImGui.GetColorU32(ImGuiCol.Text);
        var disabledColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);

        drawList.AddText(left, textColor, TrimToWidth(run.Title, titleWidth));
        drawList.AddText(left + new Vector2(0, 28f), disabledColor, TrimToWidth(GetGroupName(run.GroupId) ?? subtitlePrefix ?? "FullParty", titleWidth));
        drawList.AddText(left + new Vector2(0, 64f), disabledColor, TrimToWidth(string.IsNullOrWhiteSpace(run.Notes) ? "No description provided." : run.Notes, titleWidth));

        var schedule = new Vector2(scheduleStartX + 18f, min.Y + 40f);
        DrawRunIconText(schedule, FontAwesomeIcon.CalendarAlt, $"{FormatRunDayLabel(run.StartsAt)} · {run.StartsAt:HH:mm} ST", textColor);
        DrawRunIconText(schedule + new Vector2(0, 34f), FontAwesomeIcon.Globe, run.Datacenter ?? "Any DC", textColor);

        var meta = new Vector2(metaStartX + 18f, min.Y + 40f);
        var metaTitle = run.ApplicationCount is { } applications
            ? $"{applications} Applications"
            : run.NeedsApplication ? "Applications open" : "No applications";
        drawList.AddText(meta, textColor, TrimToWidth(metaTitle, metaWidth - 34f));
        drawList.AddText(meta + new Vector2(0, 34f), FullPartyModernPalette.Color(FullPartyModernPalette.Muted), TrimToWidth(FormatRelativeStart(run.StartsAt), metaWidth - 34f));
    }

    private void DrawRunArtwork(FullPartyRun run, Vector2 position, Vector2 size)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(position, position + size, FullPartyModernPalette.Color(FullPartyModernPalette.Elevated), 3f);

        var imageUrl = run.ActivityType?.SmallImageUrl ?? run.ActivityType?.BannerImageUrl;
        var path = plugin.ImageCache.GetImagePath(imageUrl, $"run-row-{run.Id}");
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var texture = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
            if (texture != null)
            {
                var (uv0, uv1) = GetCoverUvs(texture.Size, size);
                drawList.AddImageRounded(texture.Handle, position, position + size, uv0, uv1, ImGui.GetColorU32(ImGuiCol.Text), 3f);
            }
        }

        drawList.AddRectFilled(position, position + size, ImGui.ColorConvertFloat4ToU32(new Vector4(0.02f, 0.02f, 0.03f, 0.34f)), 3f);
        drawList.AddRect(position, position + size, FullPartyModernPalette.Color(FullPartyModernPalette.BorderSoft), 3f);

    }

    private static (Vector2 Uv0, Vector2 Uv1) GetCoverUvs(Vector2 imageSize, Vector2 targetSize)
    {
        if (imageSize.X <= 0 || imageSize.Y <= 0 || targetSize.X <= 0 || targetSize.Y <= 0)
            return (Vector2.Zero, Vector2.One);

        var imageAspect = imageSize.X / imageSize.Y;
        var targetAspect = targetSize.X / targetSize.Y;

        if (imageAspect > targetAspect)
        {
            var visibleWidth = targetAspect / imageAspect;
            var offset = (1f - visibleWidth) * 0.5f;
            return (new Vector2(offset, 0f), new Vector2(1f - offset, 1f));
        }

        var visibleHeight = imageAspect / targetAspect;
        var verticalOffset = (1f - visibleHeight) * 0.5f;
        return (new Vector2(0f, verticalOffset), new Vector2(1f, 1f - verticalOffset));
    }

    private static void DrawVerticalDivider(float x, float yMin, float yMax)
    {
        ImGui.GetWindowDrawList().AddLine(
            new Vector2(x, yMin),
            new Vector2(x, yMax),
            FullPartyModernPalette.Color(FullPartyModernPalette.Border));
    }

    private static void DrawRunIconText(Vector2 position, FontAwesomeIcon icon, string text, uint color)
    {
        ImGui.SetCursorScreenPos(position);
        using (ImRaii.PushColor(ImGuiCol.Text, FullPartyModernPalette.Muted))
        {
            ElezenImgui.ShowIcon(icon);
        }

        ImGui.GetWindowDrawList().AddText(position + new Vector2(26f, 0), color, text);
    }

    private static string FormatRunDayLabel(DateTimeOffset startsAt)
    {
        var today = GetServerTimeNow().Date;
        var date = startsAt.Date;
        if (date == today)
            return "Today";

        if (date == today.AddDays(1))
            return "Tomorrow";

        return startsAt.ToString("ddd d MMM");
    }

    private static string FormatRunMeta(FullPartyRun run)
    {
        if (run.ApplicationCount is { } applications)
            return $"{applications} applications";

        if (run.DurationMinutes is { } duration)
            return $"{duration} min";

        return run.Status;
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

    private static void DrawPanelHeader(string label)
    {
        FullPartyModernPalette.SectionHeader(FontAwesomeIcon.InfoCircle, label);
        ImGui.Spacing();
    }

    private bool DrawUserAvatar(FullPartyUser? user)
    {
        var path = GetAvatarPath(user);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            if (!string.IsNullOrWhiteSpace(user?.AvatarUrl))
            {
                ImGui.TextDisabled(avatarError ?? "Loading avatar...");
            }

            return false;
        }

        var avatar = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
        if (avatar == null)
        {
            ImGui.TextDisabled("Loading avatar...");
            return false;
        }

        var targetSize = new Vector2(52, 52);
        var avatarSize = avatar.Size;
        if (avatarSize.X <= 0 || avatarSize.Y <= 0)
        {
            ImGui.Image(avatar.Handle, targetSize);
            return true;
        }

        avatarSize *= MathF.Min(targetSize.X / avatarSize.X, targetSize.Y / avatarSize.Y);
        ImGui.Image(avatar.Handle, avatarSize);
        return true;
    }

    private bool DrawRoundUserAvatar(FullPartyUser? user, float size)
    {
        var position = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var center = position + new Vector2(size * 0.5f);
        var radius = size * 0.5f;
        var path = GetAvatarPath(user);

        ImGui.InvisibleButton("##fullparty_sidebar_avatar", new Vector2(size, size));

        drawList.AddCircleFilled(center, radius, FullPartyModernPalette.Color(FullPartyModernPalette.Elevated), 48);
        drawList.AddCircle(center, radius, FullPartyModernPalette.Color(FullPartyModernPalette.BorderSoft), 48, 1.5f);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            var initial = string.IsNullOrWhiteSpace(user?.Name) ? "F" : user.Name[..1].ToUpperInvariant();
            var textSize = ImGui.CalcTextSize(initial);
            drawList.AddText(center - (textSize * 0.5f), FullPartyModernPalette.Color(FullPartyModernPalette.Muted), initial);
            return false;
        }

        var avatar = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
        if (avatar == null)
            return false;

        drawList.AddImageRounded(
            avatar.Handle,
            position,
            position + new Vector2(size),
            Vector2.Zero,
            Vector2.One,
            ImGui.GetColorU32(ImGuiCol.Text),
            radius);
        drawList.AddCircle(center, radius, FullPartyModernPalette.Color(FullPartyModernPalette.Brand), 48, 2f);
        return true;
    }

    private string? GetAvatarPath(FullPartyUser? user)
    {
        if (string.IsNullOrWhiteSpace(user?.AvatarUrl))
        {
            ResetAvatarState();
            return null;
        }

        var resolvedAvatarUrl = plugin.AuthService.ResolveUrl(user.AvatarUrl);
        if (!string.Equals(avatarUrl, resolvedAvatarUrl, StringComparison.Ordinal))
        {
            ResetAvatarState();
            avatarUrl = resolvedAvatarUrl;
        }

        if (!string.IsNullOrWhiteSpace(avatarPath))
            return avatarPath;

        if (avatarDownloadTask == null && avatarError == null)
        {
            avatarDownloadTask = DownloadAvatarAsync(user.Id, resolvedAvatarUrl, avatarCancellation.Token);
        }

        if (avatarDownloadTask is not { IsCompleted: true })
            return null;

        if (avatarDownloadTask.IsCompletedSuccessfully)
        {
            avatarPath = avatarDownloadTask.Result;
            if (avatarPath == null)
                avatarError = "Avatar unavailable.";
        }
        else
        {
            avatarError = "Avatar unavailable.";
            Plugin.Log.Warning(avatarDownloadTask.Exception, "FullParty avatar download failed.");
        }

        avatarDownloadTask = null;
        return avatarPath;
    }

    private void ResetAvatarState()
    {
        avatarCancellation.Cancel();
        avatarCancellation.Dispose();
        avatarCancellation = new CancellationTokenSource();
        avatarUrl = null;
        avatarPath = null;
        avatarError = null;
        avatarDownloadTask = null;
    }

    private static async Task<string?> DownloadAvatarAsync(long userId, string avatarUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await AvatarHttpClient.GetAsync(avatarUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength > MaxAvatarBytes)
                throw new InvalidOperationException("FullParty avatar image is too large.");

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0 || bytes.Length > MaxAvatarBytes)
                throw new InvalidOperationException("FullParty avatar image is empty or too large.");

            var cacheDirectory = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "avatars");
            Directory.CreateDirectory(cacheDirectory);

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(avatarUrl)))[..16].ToLowerInvariant();
            var extension = GetAvatarExtension(response.Content.Headers.ContentType?.MediaType, avatarUrl);
            var filePath = Path.Combine(cacheDirectory, $"{userId}-{hash}{extension}");
            await File.WriteAllBytesAsync(filePath, bytes, cancellationToken);

            return filePath;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not download FullParty avatar from {AvatarUrl}", avatarUrl);
            return null;
        }
    }

    private static string GetAvatarExtension(string? mediaType, string avatarUrl)
    {
        return mediaType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => TryGetExtensionFromUrl(avatarUrl) ?? ".png",
        };
    }

    private static string? TryGetExtensionFromUrl(string avatarUrl)
    {
        if (!Uri.TryCreate(avatarUrl, UriKind.Absolute, out var uri))
            return null;

        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" ? extension : null;
    }
}
