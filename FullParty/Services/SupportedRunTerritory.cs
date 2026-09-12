namespace FullParty.Services;

internal enum RunTerritoryKind
{
    None,
    OccultCrescent,
    EurekaHydatos,
    DelubrumReginae,
    DelubrumReginaeSavage,
}

internal static class SupportedRunTerritory
{
    internal const uint SouthHornTerritoryId = 1252;
    internal const uint NorthHornTerritoryId = 1346;
    internal const uint HydatosTerritoryId = 827;
    internal const uint DelubrumTerritoryId = 936;
    internal const uint DelubrumSavageTerritoryId = 937;
    internal const string WaitingMessage = "Party sync waits for Occult Crescent, Hydatos/BA, or Delubrum Reginae.";

    public static RunTerritoryKind Current => GetKind(Plugin.ClientState.TerritoryType);
    public static bool IsCurrent() => Current != RunTerritoryKind.None;
    public static string CurrentName => GetName(Current);

    public static RunTerritoryKind GetKind(uint territoryId) => territoryId switch
    {
        SouthHornTerritoryId or NorthHornTerritoryId => RunTerritoryKind.OccultCrescent,
        HydatosTerritoryId => RunTerritoryKind.EurekaHydatos,
        DelubrumTerritoryId => RunTerritoryKind.DelubrumReginae,
        DelubrumSavageTerritoryId => RunTerritoryKind.DelubrumReginaeSavage,
        _ => RunTerritoryKind.None,
    };

    public static string GetName(RunTerritoryKind kind) => kind switch
    {
        RunTerritoryKind.OccultCrescent => "Occult Crescent",
        RunTerritoryKind.EurekaHydatos => "Eureka Hydatos / Baldesion Arsenal",
        RunTerritoryKind.DelubrumReginae => "Delubrum Reginae",
        RunTerritoryKind.DelubrumReginaeSavage => "Delubrum Reginae (Savage)",
        _ => "Outside supported instances",
    };

    // BA shares Hydatos' territory and duty; these are its interior map sheets.
    public static bool IsBaldesionArsenal(uint territoryId, uint mapId) =>
        territoryId == HydatosTerritoryId && mapId is 520 or 521 or 524 or 525 or 526 or 527;
}
