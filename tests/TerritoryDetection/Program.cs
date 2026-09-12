using FullParty;
using FullParty.Services;

var cases = new (uint Territory, RunTerritoryKind Expected)[]
{
    (1252, RunTerritoryKind.OccultCrescent),
    (1346, RunTerritoryKind.OccultCrescent),
    (827, RunTerritoryKind.EurekaHydatos),
    (936, RunTerritoryKind.DelubrumReginae),
    (937, RunTerritoryKind.DelubrumReginaeSavage),
    (0, RunTerritoryKind.None),
    (128, RunTerritoryKind.None),
    (939, RunTerritoryKind.None),
    (525, RunTerritoryKind.None),
    (639, RunTerritoryKind.None),
    (760, RunTerritoryKind.None),
    (761, RunTerritoryKind.None),
};

foreach (var (territory, expected) in cases)
{
    Plugin.ClientState.TerritoryType = territory;
    Assert(SupportedRunTerritory.Current == expected, $"Territory {territory}: expected {expected}");
    Assert(SupportedRunTerritory.IsCurrent() == (expected != RunTerritoryKind.None), $"Sync gate for territory {territory}");
    Assert(!string.IsNullOrWhiteSpace(SupportedRunTerritory.CurrentName), $"Display name for territory {territory}");
}

foreach (var map in new uint[] { 520, 521, 524, 525, 526, 527 })
{
    Assert(SupportedRunTerritory.IsBaldesionArsenal(827, map), $"BA interior map {map}");
    Assert(!SupportedRunTerritory.IsBaldesionArsenal(936, map), $"BA map {map} must also require Hydatos territory");
}

Assert(!SupportedRunTerritory.IsBaldesionArsenal(827, 515), "Hydatos surface is supported but is not the BA interior");
// Changing floors must not disable the territory-level sync gate.
foreach (var territory in new uint[] { 827, 936, 937 })
{
    Plugin.ClientState.TerritoryType = territory;
    Assert(SupportedRunTerritory.IsCurrent(), $"Instance support requires no map or localized name for {territory}");
}

Console.WriteLine("Passed 52 territory detection assertions (Occult, Hydatos/BA, DR, DRS, unsupported IDs, and BA maps).");

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

namespace FullParty
{
    internal static class Plugin
    {
        public static TestClientState ClientState { get; } = new();
    }

    internal sealed class TestClientState
    {
        public uint TerritoryType { get; set; }
    }
}
