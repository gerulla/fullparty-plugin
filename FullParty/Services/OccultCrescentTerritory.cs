using System;
using System.Linq;
using Lumina.Excel.Sheets;

namespace FullParty.Services;

internal static class OccultCrescentTerritory
{
    private static readonly string[] TerritoryNameMatches = ["Occult Crescent", "South Horn"];

    public static bool IsCurrent()
    {
        return IsOccultCrescentTerritory(Plugin.ClientState.TerritoryType);
    }

    public static bool IsOccultCrescentTerritory(uint territoryId)
    {
        if (territoryId == 0)
            return false;

        if (!Plugin.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territory))
            return false;

        var placeName = territory.PlaceName.Value.Name.ToString();
        return TerritoryNameMatches.Any(match => placeName.Contains(match, StringComparison.OrdinalIgnoreCase));
    }
}
