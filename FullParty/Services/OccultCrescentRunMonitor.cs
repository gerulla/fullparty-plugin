using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FullParty.Auth;
using FullParty.Models;

namespace FullParty.Services;

public sealed class OccultCrescentRunMonitor : IDisposable
{
    private static readonly TimeSpan AuthRetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpcomingLookahead = TimeSpan.FromMinutes(60);
    private readonly Plugin plugin;
    private readonly CancellationTokenSource cancellation = new();
    private readonly HashSet<int> openedRunIds = [];
    private readonly object gate = new();

    private Task? checkTask;
    private DateTimeOffset nextCheckAt;
    private FullPartyRun? pendingRunToOpen;

    public OccultCrescentRunMonitor(Plugin plugin)
    {
        this.plugin = plugin;

        Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
        Plugin.Framework.Update += OnFrameworkUpdate;

        ScheduleImmediateCheck();
    }

    public void Dispose()
    {
        Plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Plugin.Framework.Update -= OnFrameworkUpdate;

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        if (OccultCrescentTerritory.IsOccultCrescentTerritory(territoryId))
            ScheduleImmediateCheck();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        OpenPendingRunIfReady();

        if (DateTimeOffset.UtcNow < nextCheckAt)
            return;

        var nextInterval = TryStartCheck() ? CheckInterval : AuthRetryInterval;
        nextCheckAt = DateTimeOffset.UtcNow.Add(nextInterval);
    }

    private void OpenPendingRunIfReady()
    {
        FullPartyRun? run;
        lock (gate)
        {
            run = pendingRunToOpen;
            pendingRunToOpen = null;
        }

        if (run == null)
            return;

        if (!IsInOccultCrescent())
            return;

        lock (gate)
        {
            openedRunIds.Add(run.Id);
        }

        plugin.OpenRunWindow(run);
    }

    private bool TryStartCheck()
    {
        if (plugin.AuthService.State != AuthState.Authenticated || !IsInOccultCrescent())
            return false;

        if (checkTask is { IsCompleted: false })
            return true;

        checkTask = CheckForUpcomingRunAsync(cancellation.Token);
        return true;
    }

    private async Task CheckForUpcomingRunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var groups = await plugin.ApiClient.GetGroupsAsync(cancellationToken);
            var runs = new List<FullPartyRun>();
            foreach (var group in groups.Where(group => group.CanModerate))
            {
                runs.AddRange(await plugin.ApiClient.GetGroupRunsAsync(group.Slug, cancellationToken));
            }

            var now = GetServerTimeNow();
            var cutoff = now.Add(UpcomingLookahead);
            FullPartyRun? upcomingRun;
            lock (gate)
            {
                upcomingRun = runs
                    .Where(run => run.CanModerate == true)
                    .Where(run => run.StartsAt >= now && run.StartsAt <= cutoff)
                    .Where(run => !openedRunIds.Contains(run.Id))
                    .OrderBy(run => run.StartsAt)
                    .FirstOrDefault();
            }

            if (upcomingRun == null)
                return;

            lock (gate)
            {
                pendingRunToOpen = upcomingRun;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not check for upcoming FullParty runs in Occult Crescent.");
        }
    }

    private static DateTimeOffset GetServerTimeNow()
    {
        var frameworkUtc = Plugin.Framework.LastUpdateUTC;
        return frameworkUtc == default ? DateTimeOffset.UtcNow : frameworkUtc;
    }

    private void ScheduleImmediateCheck()
    {
        nextCheckAt = DateTimeOffset.MinValue;
    }

    private static bool IsInOccultCrescent()
    {
        return OccultCrescentTerritory.IsCurrent();
    }
}
