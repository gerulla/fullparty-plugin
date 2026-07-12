using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Lumina.Excel.Sheets;

namespace FullParty.Services;

internal static class OccultCrescentTerritory
{
    private static readonly string[] TerritoryNameMatches = ["Occult Crescent", "South Horn"];
    private static readonly ClientLanguage[] DebugLanguages =
    [
        ClientLanguage.English,
        ClientLanguage.German,
        ClientLanguage.French,
        ClientLanguage.Japanese,
    ];

    public static bool IsCurrent()
    {
        return IsOccultCrescentTerritory(Plugin.ClientState.TerritoryType);
    }

    public static bool IsOccultCrescentTerritory(uint territoryId)
    {
        return GetDebugInfo(territoryId).IsOccultCrescent;
    }

    public static TerritoryDebugInfo GetCurrentDebugInfo()
    {
        return GetDebugInfo(Plugin.ClientState.TerritoryType);
    }

    public static TerritoryDebugInfo GetDebugInfo(uint territoryId)
    {
        var info = new TerritoryDebugInfo
        {
            TerritoryId = territoryId,
        };

        if (territoryId == 0)
            return info;

        try
        {
            if (!Plugin.DataManager.GetExcelSheet<TerritoryType>(ClientLanguage.English).TryGetRow(territoryId, out var territory))
            {
                info.Error = "Territory row was not found in TerritoryType.";
                return info;
            }

            info.PlaceNameRowId = territory.PlaceName.RowId;
            info.DefaultMapId = territory.Map.RowId;
            info.DirectPlaceName = territory.PlaceName.Value.Name.ToString();

            foreach (var language in DebugLanguages)
            {
                var placeName = GetPlaceName(info.PlaceNameRowId, language);
                info.PlaceNames.Add(language.ToString(), placeName);
            }

            var matchedName = info.PlaceNames.Values
                .Append(info.DirectPlaceName)
                .FirstOrDefault(IsOccultCrescentName);

            info.IsOccultCrescent = matchedName != null;
            info.MatchSource = matchedName == null
                ? "none"
                : $"place name \"{matchedName}\"";
        }
        catch (Exception ex)
        {
            info.Error = ex.Message;
        }

        return info;
    }

    private static string GetPlaceName(uint placeNameRowId, ClientLanguage language)
    {
        if (placeNameRowId == 0)
            return string.Empty;

        return Plugin.DataManager.GetExcelSheet<PlaceName>(language).TryGetRow(placeNameRowId, out var placeName)
            ? placeName.Name.ToString()
            : string.Empty;
    }

    private static bool IsOccultCrescentName(string? placeName)
    {
        return !string.IsNullOrWhiteSpace(placeName)
            && TerritoryNameMatches.Any(match => placeName.Contains(match, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class TerritoryDebugInfo
{
    public uint TerritoryId { get; init; }
    public uint PlaceNameRowId { get; set; }
    public uint DefaultMapId { get; set; }
    public string DirectPlaceName { get; set; } = string.Empty;
    public Dictionary<string, string> PlaceNames { get; } = [];
    public bool IsOccultCrescent { get; set; }
    public string MatchSource { get; set; } = "none";
    public string? Error { get; set; }
}
