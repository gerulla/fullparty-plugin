using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FullParty.Models;
using Lumina.Excel.Sheets;

namespace FullParty.Services;

internal sealed unsafe class AdventurerListService : IDisposable
{
    private const int AdventurerListTabCallback = 2;
    private const int RefreshDelayFrames = 12;

    private GamePresenceList presence = GamePresenceList.Empty;
    private DateTimeOffset? lastRefreshAt;
    private int framesUntilRead;
    private bool refreshRequested;
    private bool closeAfterRead;
    private bool isRefreshing;

    public string StatusMessage { get; private set; } = "Adventurer List has not been refreshed yet.";
    public bool HasRequestedRefresh { get; private set; }
    public bool IsRefreshing => isRefreshing;
    public int Count => presence.Count;

    public void ResetForOccultVisit()
    {
        presence = GamePresenceList.Empty;
        HasRequestedRefresh = false;
        StatusMessage = "Adventurer List has not been refreshed for this Occult visit yet.";
    }

    public void RequestRefresh()
    {
        HasRequestedRefresh = true;
        refreshRequested = true;
        framesUntilRead = 0;
        isRefreshing = true;
        StatusMessage = "Opening Adventurer List...";
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public GamePresenceList GetPresence(FullPartyRunDetail runDetail)
    {
        return presence;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            if (refreshRequested)
            {
                BeginRefresh();
                refreshRequested = false;
                framesUntilRead = RefreshDelayFrames;
                return;
            }

            if (framesUntilRead > 0)
            {
                framesUntilRead--;
                if (framesUntilRead > 0)
                    return;

                ReadAndFinishRefresh();
            }
        }
        catch (Exception ex)
        {
            isRefreshing = false;
            StatusMessage = $"Adventurer List refresh failed: {ex.Message}";
            Plugin.Framework.Update -= OnFrameworkUpdate;
            Plugin.Log.Warning(ex, "Could not refresh the FullParty Adventurer List cache.");
        }
    }

    private void BeginRefresh()
    {
        var socialAddon = GetSocialAddon();
        closeAfterRead = socialAddon == null || !socialAddon->IsVisible;

        if (socialAddon == null || !socialAddon->IsVisible)
        {
            GameCommandExecutor.Execute("/friendlist");
        }

        socialAddon = GetSocialAddon();
        if (socialAddon != null && socialAddon->IsReady)
        {
            socialAddon->FireCallbackInt(AdventurerListTabCallback);
        }

        StatusMessage = "Waiting for Adventurer List data...";
    }

    private void ReadAndFinishRefresh()
    {
        var socialAddon = GetSocialAddon();
        if (socialAddon != null && socialAddon->IsReady)
            socialAddon->FireCallbackInt(AdventurerListTabCallback);

        var members = ReadMembers();
        presence = new GamePresenceList(members);
        lastRefreshAt = DateTimeOffset.Now;
        StatusMessage = members.Count == 0
            ? "Adventurer List refreshed, but no players were found."
            : $"Adventurer List refreshed: {members.Count} players at {lastRefreshAt:HH:mm:ss}.";

        if (closeAfterRead)
        {
            socialAddon = GetSocialAddon();
            if (socialAddon != null && socialAddon->IsVisible)
                socialAddon->Close(true);
        }

        isRefreshing = false;
        Plugin.Framework.Update -= OnFrameworkUpdate;
    }

    private static List<GamePresenceMember> ReadMembers()
    {
        var result = new Dictionary<string, GamePresenceMember>(StringComparer.OrdinalIgnoreCase);
        var infoModule = InfoModule.Instance();
        if (infoModule == null)
            return [];

        // InfoProxy24 backs the Social window's Adventurer List rows.
        var proxy = infoModule->GetInfoProxyById((InfoProxyId)24);
        if (proxy == null)
            return [];

        var commonList = (InfoProxyCommonList*)proxy;
        foreach (var characterData in commonList->CharDataSpan)
        {
            if (characterData.ContentId == 0)
                continue;

            var name = characterData.NameString;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var world = GetWorldName(characterData.HomeWorld);
            var key = RunValidationSources.GetCharacterKey(name, world);
            if (key.Equals("@", StringComparison.Ordinal))
                continue;

            result[key] = new GamePresenceMember(
                name,
                world,
                PartySnapshotBuilder.GetCombatClassJobShorthand(characterData.Job),
                null);
        }

        return [.. result.Values];
    }

    private static string? GetWorldName(ushort worldId)
    {
        if (worldId == 0)
            return null;

        return Plugin.DataManager.GetExcelSheet<World>().TryGetRow(worldId, out var world)
            ? world.Name.ToString()
            : null;
    }

    private static AtkUnitBase* GetSocialAddon()
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null)
            return null;

        var atkModule = uiModule->GetRaptureAtkModule();
        if (atkModule == null)
            return null;

        byte* addonName = stackalloc byte[] { (byte)'S', (byte)'o', (byte)'c', (byte)'i', (byte)'a', (byte)'l', 0 };
        return atkModule->RaptureAtkUnitManager.GetAddonByName(addonName, 1);
    }
}
