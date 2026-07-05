using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Lumina.Excel.Sheets;

namespace FullParty.Services;

internal static class ClassJobResolver
{
    private static readonly object CacheLock = new();
    private static Dictionary<string, string>? aliasCache;

    internal static string? GetCombatClassJobShorthand(uint rowId)
    {
        return rowId switch
        {
            1 => "GLA",
            2 => "PGL",
            3 => "MRD",
            4 => "LNC",
            5 => "ARC",
            6 => "CNJ",
            7 => "THM",
            19 => "PLD",
            20 => "MNK",
            21 => "WAR",
            22 => "DRG",
            23 => "BRD",
            24 => "WHM",
            25 => "BLM",
            26 => "ACN",
            27 => "SMN",
            28 => "SCH",
            29 => "ROG",
            30 => "NIN",
            31 => "MCH",
            32 => "DRK",
            33 => "AST",
            34 => "SAM",
            35 => "RDM",
            36 => "BLU",
            37 => "GNB",
            38 => "DNC",
            39 => "RPR",
            40 => "SGE",
            41 => "VPR",
            42 => "PCT",
            _ => null,
        };
    }

    internal static string? Normalize(string? classNameOrShorthand)
    {
        if (string.IsNullOrWhiteSpace(classNameOrShorthand))
            return null;

        var token = NormalizeToken(classNameOrShorthand);
        if (token.Length == 0)
            return null;

        var aliases = GetAliases();
        return aliases.TryGetValue(token, out var shorthand) ? shorthand : null;
    }

    private static Dictionary<string, string> GetAliases()
    {
        lock (CacheLock)
        {
            return aliasCache ??= BuildAliases();
        }
    }

    private static Dictionary<string, string> BuildAliases()
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var shorthand in new[]
                 {
                     "GLA", "PGL", "MRD", "LNC", "ARC", "CNJ", "THM", "PLD", "MNK", "WAR", "DRG", "BRD",
                     "WHM", "BLM", "ACN", "SMN", "SCH", "ROG", "NIN", "MCH", "DRK", "AST", "SAM", "RDM",
                     "BLU", "GNB", "DNC", "RPR", "SGE", "VPR", "PCT",
                 })
        {
            AddAlias(aliases, shorthand, shorthand);
        }

        foreach (var language in new[]
                 {
                     ClientLanguage.English,
                     ClientLanguage.German,
                     ClientLanguage.French,
                     ClientLanguage.Japanese,
                 })
        {
            try
            {
                foreach (var classJob in Plugin.DataManager.GetExcelSheet<ClassJob>(language))
                {
                    var shorthand = GetCombatClassJobShorthand(classJob.RowId);
                    if (shorthand == null)
                        continue;

                    AddAlias(aliases, classJob.Name.ToString(), shorthand);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug(ex, "Could not build FullParty class job aliases for {Language}.", language);
            }
        }

        return aliases;
    }

    private static void AddAlias(IDictionary<string, string> aliases, string? alias, string shorthand)
    {
        var token = NormalizeToken(alias);
        if (token.Length > 0)
            aliases[token] = shorthand;
    }

    private static string NormalizeToken(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    }
}
