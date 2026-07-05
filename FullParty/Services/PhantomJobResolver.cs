using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Lumina.Excel.Sheets;
using LuminaStatus = Lumina.Excel.Sheets.Status;

namespace FullParty.Services;

internal static class PhantomJobResolver
{
    private static readonly ClientLanguage[] Languages =
    [
        ClientLanguage.English,
        ClientLanguage.German,
        ClientLanguage.French,
        ClientLanguage.Japanese,
    ];

    private static readonly object CacheLock = new();
    private static ResolverCache? cache;

    internal static void WarmUp()
    {
        _ = GetCache();
    }

    internal static ResolvedPhantomJob? DetectFromStatuses(IEnumerable? statuses)
    {
        if (statuses == null)
            return null;

        var resolver = GetCache();
        foreach (var statusObject in statuses)
        {
            if (!PartySnapshotBuilder.TryGetStatusId(statusObject, out var statusId))
                continue;

            if (resolver.Statuses.TryGetValue(statusId, out var statusMatch))
                return new ResolvedPhantomJob(statusMatch.SnapshotName, statusId, statusMatch.StatusName);

            foreach (var statusName in PartySnapshotBuilder.GetStatusNames(statusId))
            {
                if (TryResolveStatusName(statusName, resolver, out var snapshotName))
                    return new ResolvedPhantomJob(snapshotName, statusId, statusName);
            }
        }

        return null;
    }

    internal static string? Normalize(string? phantomJob)
    {
        if (string.IsNullOrWhiteSpace(phantomJob))
            return null;

        var resolver = GetCache();
        return TryResolveName(phantomJob, resolver, out var snapshotName)
            ? snapshotName
            : GetSnapshotPhantomJobName(phantomJob);
    }

    private static ResolverCache GetCache()
    {
        lock (CacheLock)
        {
            return cache ??= BuildCache();
        }
    }

    private static ResolverCache BuildCache()
    {
        var definitions = BuildDefinitions();
        var aliases = BuildAliases(definitions);
        var statuses = BuildStatusMappings(aliases);

        Plugin.Log.Debug(
            "Built FullParty phantom job resolver with {JobCount} jobs, {AliasCount} aliases, and {StatusCount} status mappings.",
            definitions.Count,
            aliases.ByToken.Count,
            statuses.Count);

        return new ResolverCache(statuses, aliases.ByToken, aliases.ByLength);
    }

    private static List<PhantomJobDefinition> BuildDefinitions()
    {
        var definitionsByRow = new Dictionary<uint, MutablePhantomJobDefinition>();

        foreach (var language in Languages)
        {
            try
            {
                foreach (var supportJob in Plugin.DataManager.GetExcelSheet<MKDSupportJob>(language))
                {
                    if (!TryGetOrCreateDefinition(definitionsByRow, supportJob, out var definition))
                        continue;

                    AddJobAlias(definition, supportJob.Name.ToString(), true);
                    AddJobAlias(definition, supportJob.NameShort.ToString(), false);
                    AddJobAlias(definition, supportJob.NameFemale.ToString(), true);
                    AddJobAlias(definition, supportJob.NameEnglish.ToString(), false);

                    definition.SnapshotName ??= GetSnapshotPhantomJobName(supportJob.NameEnglish.ToString());
                    if (language == ClientLanguage.English)
                        definition.SnapshotName ??= GetSnapshotPhantomJobName(supportJob.Name.ToString());
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug(ex, "Could not build FullParty phantom job aliases for {Language}.", language);
            }
        }

        return definitionsByRow.Values
            .Select(definition =>
            {
                definition.SnapshotName ??= GetSnapshotPhantomJobName(definition.Aliases.FirstOrDefault());
                return definition.SnapshotName == null
                    ? null
                    : new PhantomJobDefinition(
                        definition.RowId,
                        definition.SnapshotName,
                        definition.Aliases,
                        definition.StatusPrefixAliases);
            })
            .Where(definition => definition != null)
            .Select(definition => definition!)
            .ToList();
    }

    private static bool TryGetOrCreateDefinition(
        IDictionary<uint, MutablePhantomJobDefinition> definitionsByRow,
        MKDSupportJob supportJob,
        out MutablePhantomJobDefinition definition)
    {
        if (supportJob.RowId == 0)
        {
            definition = null!;
            return false;
        }

        if (definitionsByRow.TryGetValue(supportJob.RowId, out definition!))
            return true;

        var hasAnyName =
            !string.IsNullOrWhiteSpace(supportJob.Name.ToString()) ||
            !string.IsNullOrWhiteSpace(supportJob.NameShort.ToString()) ||
            !string.IsNullOrWhiteSpace(supportJob.NameFemale.ToString()) ||
            !string.IsNullOrWhiteSpace(supportJob.NameEnglish.ToString());

        if (!hasAnyName)
        {
            definition = null!;
            return false;
        }

        definition = new MutablePhantomJobDefinition(supportJob.RowId);
        definitionsByRow[supportJob.RowId] = definition;
        return true;
    }

    private static void AddJobAlias(MutablePhantomJobDefinition definition, string? alias, bool allowStatusPrefix)
    {
        if (string.IsNullOrWhiteSpace(alias))
            return;

        var trimmed = alias.Trim();
        definition.Aliases.Add(trimmed);
        if (allowStatusPrefix)
            definition.StatusPrefixAliases.Add(trimmed);
    }

    private static AliasCache BuildAliases(IEnumerable<PhantomJobDefinition> definitions)
    {
        var aliasesByToken = new Dictionary<string, AliasEntry>(StringComparer.Ordinal);
        var ambiguousTokens = new HashSet<string>(StringComparer.Ordinal);

        foreach (var definition in definitions)
        {
            foreach (var alias in definition.Aliases)
            {
                foreach (var token in GetAliasTokens(alias))
                {
                    AddAlias(aliasesByToken, ambiguousTokens, token, definition.SnapshotName, false);
                }
            }

            foreach (var alias in definition.StatusPrefixAliases)
            {
                foreach (var token in GetStatusPrefixTokens(alias))
                {
                    AddAlias(aliasesByToken, ambiguousTokens, token, definition.SnapshotName, true);
                }
            }

            foreach (var token in GetAliasTokens(definition.SnapshotName))
            {
                AddAlias(aliasesByToken, ambiguousTokens, token, definition.SnapshotName, false);
            }

            foreach (var token in GetStatusPrefixTokens(definition.SnapshotName))
            {
                AddAlias(aliasesByToken, ambiguousTokens, token, definition.SnapshotName, true);
            }
        }

        var byLength = aliasesByToken.Values
            .OrderByDescending(alias => alias.Token.Length)
            .ToList();

        return new AliasCache(aliasesByToken, byLength);
    }

    private static void AddAlias(
        IDictionary<string, AliasEntry> aliasesByToken,
        ISet<string> ambiguousTokens,
        string token,
        string snapshotName,
        bool allowStatusPrefix)
    {
        if (token.Length == 0 || ambiguousTokens.Contains(token))
            return;

        if (!aliasesByToken.TryGetValue(token, out var existing))
        {
            aliasesByToken[token] = new AliasEntry(token, snapshotName, allowStatusPrefix);
            return;
        }

        if (!existing.SnapshotName.Equals(snapshotName, StringComparison.OrdinalIgnoreCase))
        {
            aliasesByToken.Remove(token);
            ambiguousTokens.Add(token);
        }
        else if (allowStatusPrefix && !existing.AllowStatusPrefix)
        {
            aliasesByToken[token] = existing with { AllowStatusPrefix = true };
        }
    }

    private static Dictionary<uint, StatusMapping> BuildStatusMappings(AliasCache aliases)
    {
        var mappings = new Dictionary<uint, StatusMapping>();

        foreach (var language in Languages)
        {
            try
            {
                foreach (var status in Plugin.DataManager.GetExcelSheet<LuminaStatus>(language))
                {
                    var statusName = status.Name.ToString();
                    if (!TryResolveStatusName(statusName, aliases.ByToken, aliases.ByLength, out var snapshotName))
                        continue;

                    mappings.TryAdd(status.RowId, new StatusMapping(snapshotName, statusName));
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug(ex, "Could not build FullParty phantom job status mappings for {Language}.", language);
            }
        }

        return mappings;
    }

    private static bool TryResolveName(string? name, ResolverCache resolver, out string snapshotName)
    {
        return TryResolveName(name, resolver.AliasesByToken, resolver.AliasesByLength, out snapshotName);
    }

    private static bool TryResolveStatusName(string? name, ResolverCache resolver, out string snapshotName)
    {
        return TryResolveStatusName(name, resolver.AliasesByToken, resolver.AliasesByLength, out snapshotName);
    }

    private static bool TryResolveName(
        string? name,
        IReadOnlyDictionary<string, AliasEntry> aliasesByToken,
        IReadOnlyList<AliasEntry> aliasesByLength,
        out string snapshotName)
    {
        snapshotName = string.Empty;
        var tokens = GetAliasTokens(name).ToList();
        if (tokens.Count == 0)
            return false;

        foreach (var token in tokens)
        {
            if (aliasesByToken.TryGetValue(token, out var exactMatch))
            {
                snapshotName = exactMatch.SnapshotName;
                return true;
            }
        }

        foreach (var alias in aliasesByLength)
        {
            foreach (var token in tokens)
            {
                if (token.Length > alias.Token.Length && token.StartsWith(alias.Token, StringComparison.Ordinal))
                {
                    snapshotName = alias.SnapshotName;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryResolveStatusName(
        string? name,
        IReadOnlyDictionary<string, AliasEntry> aliasesByToken,
        IReadOnlyList<AliasEntry> aliasesByLength,
        out string snapshotName)
    {
        snapshotName = string.Empty;
        var tokens = GetAliasTokens(name).ToList();
        if (tokens.Count == 0)
            return false;

        foreach (var token in tokens)
        {
            if (aliasesByToken.TryGetValue(token, out var exactMatch))
            {
                snapshotName = exactMatch.SnapshotName;
                return true;
            }
        }

        foreach (var alias in aliasesByLength)
        {
            if (!alias.AllowStatusPrefix)
                continue;

            foreach (var token in tokens)
            {
                if (token.Length > alias.Token.Length && token.StartsWith(alias.Token, StringComparison.Ordinal))
                {
                    snapshotName = alias.SnapshotName;
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<string> GetAliasTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        var rawToken = NormalizeToken(value);
        if (rawToken.Length > 0)
            yield return rawToken;

        var snapshotName = GetSnapshotPhantomJobName(value);
        var snapshotToken = NormalizeToken(snapshotName);
        if (snapshotToken.Length > 0)
            yield return snapshotToken;

        var rawWithoutPhantom = StripEnglishPhantomPrefix(rawToken);
        if (rawWithoutPhantom.Length > 0)
            yield return rawWithoutPhantom;

        var snapshotWithoutPhantom = StripEnglishPhantomPrefix(snapshotToken);
        if (snapshotWithoutPhantom.Length > 0)
            yield return snapshotWithoutPhantom;
    }

    private static IEnumerable<string> GetStatusPrefixTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        var rawToken = NormalizeToken(value);
        if (rawToken.Length > 0)
            yield return rawToken;

        var snapshotToken = NormalizeToken(GetSnapshotPhantomJobName(value));
        if (snapshotToken.Length > 0)
            yield return snapshotToken;
    }

    private static string? GetSnapshotPhantomJobName(string? phantomJob)
    {
        if (string.IsNullOrWhiteSpace(phantomJob))
            return null;

        var trimmed = phantomJob.Trim();
        return trimmed.StartsWith("Phantom ", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"Phantom {trimmed}";
    }

    private static string StripEnglishPhantomPrefix(string token)
    {
        if (token.StartsWith("phantom", StringComparison.Ordinal))
            token = token["phantom".Length..];
        if (token.StartsWith("job", StringComparison.Ordinal))
            token = token["job".Length..];

        return token;
    }

    private static string NormalizeToken(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    internal sealed record ResolvedPhantomJob(string SnapshotName, uint StatusId, string StatusName);

    private sealed record PhantomJobDefinition(
        uint RowId,
        string SnapshotName,
        HashSet<string> Aliases,
        HashSet<string> StatusPrefixAliases);

    private sealed record MutablePhantomJobDefinition(uint RowId)
    {
        public string? SnapshotName { get; set; }
        public HashSet<string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> StatusPrefixAliases { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record AliasEntry(string Token, string SnapshotName, bool AllowStatusPrefix);

    private sealed record AliasCache(
        Dictionary<string, AliasEntry> ByToken,
        IReadOnlyList<AliasEntry> ByLength);

    private sealed record StatusMapping(string SnapshotName, string StatusName);

    private sealed record ResolverCache(
        IReadOnlyDictionary<uint, StatusMapping> Statuses,
        IReadOnlyDictionary<string, AliasEntry> AliasesByToken,
        IReadOnlyList<AliasEntry> AliasesByLength);
}
