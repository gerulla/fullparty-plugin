using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FullParty.Auth;
using FullParty.Models;

namespace FullParty.Windows;

public class MainWindow : Window, IDisposable
{
    private const int MaxAvatarBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan UpcomingLookahead = TimeSpan.FromMinutes(60);
    private static readonly HttpClient AvatarHttpClient = new();

    private readonly Plugin plugin;
    private readonly List<FullPartyGroup> groups = [];
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
    private Task<string?>? avatarDownloadTask;
    private long? dashboardUserId;
    private string? selectedGroupSlug;

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

    public override void Draw()
    {
        DrawAuthenticationState();
    }

    private void DrawAuthenticationState()
    {
        var auth = plugin.AuthService;

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
        ImGui.Text("Thank you for installing the FullParty Plugin");
        ImGui.TextWrapped("Connect your FullParty.gg account once so the plugin can safely unlock in-game companion features for your profile and character.");
        ImGui.Spacing();
        ImGui.Separator();
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

        DrawProfileStrip(user);
        ImGui.Spacing();

        var available = ImGui.GetContentRegionAvail();
        var leftWidth = Math.Clamp(available.X * 0.34f, 240f, 310f);
        var upcomingRuns = GetUpcomingRuns();
        var topRowHeight = GetTopDashboardRowHeight(available.Y, upcomingRuns.Count);

        using (var leftRail = ImRaii.Child("##fullparty_left_rail", new Vector2(leftWidth, available.Y), false))
        {
            if (leftRail.Success)
            {
                DrawCharactersPanel(user, topRowHeight);
                ImGui.Spacing();
                DrawGroupsPanel();
            }
        }

        ImGui.SameLine();

        using var rightRail = ImRaii.Child("##fullparty_right_rail", new Vector2(0, available.Y), false);
        if (rightRail.Success)
        {
            if (upcomingRuns.Count > 0)
            {
                using (var upcomingPanel = ImRaii.Child("##fullparty_upcoming_panel", new Vector2(0, topRowHeight), true))
                {
                    if (upcomingPanel.Success)
                        DrawUpcomingRunsPanel(upcomingRuns);
                }

                ImGui.Spacing();
            }

            using var runsPane = ImRaii.Child("##fullparty_runs_pane", Vector2.Zero, true);
            if (runsPane.Success)
            {
                DrawRunsPanel();
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
        runsByGroupSlug.Clear();
        runLoadTasks.Clear();
        runErrors.Clear();
        groupsLoadTask = null;
        groupsError = null;
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

        DrawPanelHeader("Characters");

        var character = user?.PrimaryCharacter;
        if (character == null)
        {
            ImGui.TextDisabled("No linked characters yet.");
            return;
        }

        ImGui.Text(character.Name);
        ImGui.TextDisabled($"{character.World} - {character.Datacenter}");
        ImGui.Spacing();
        ImGui.TextDisabled("Additional linked characters will appear here.");
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
        ImGui.TextDisabled($"{group.Datacenter ?? "Any datacenter"} - {group.Role}");
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
        var height = 72f;

        if (ImGui.InvisibleButton($"##{idScope}_{run.Id}", new Vector2(width, height)))
        {
            plugin.OpenRunWindow(run);
        }

        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax() - new Vector2(0, 4f);
        var bg = hovered
            ? new Vector4(0.25f, 0.30f, 0.36f, 0.32f)
            : new Vector4(0.12f, 0.14f, 0.17f, 0.24f);
        drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(bg), 4f);
        drawList.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.10f)), 4f);

        var rightWidth = Math.Clamp(width * 0.30f, 140f, 190f);
        var leftWidth = Math.Max(80f, width - rightWidth - 28f);
        var left = min + new Vector2(10f, 9f);
        var right = new Vector2(max.X - rightWidth, min.Y + 9f);
        var textColor = ImGui.GetColorU32(ImGuiCol.Text);
        var disabledColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        var subtitle = run.ActivityType?.DisplayName ?? run.Name;
        if (!string.IsNullOrWhiteSpace(subtitlePrefix))
        {
            subtitle = $"{subtitlePrefix} - {subtitle}";
        }

        var meta = $"{run.StartsAt:HH:mm} - {FormatRunMeta(run)}";

        drawList.AddText(left, textColor, TrimToWidth(run.Title, leftWidth));
        drawList.AddText(left + new Vector2(0, 24f), disabledColor, TrimToWidth(subtitle, leftWidth));
        drawList.AddText(right, textColor, TrimToWidth($"{run.StartsAt:MMM d}", rightWidth));
        drawList.AddText(right + new Vector2(0, 24f), disabledColor, TrimToWidth(meta, rightWidth));
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
        ImGui.Text(label);
        ImGui.Separator();
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
