namespace FullParty.Services;

internal static class OccultCrescentStatusIds
{
    internal const uint DutiesAsAssigned = 4228;
    internal const uint ResurrectionRestricted = 4262;
    internal const uint ResurrectionDenied = 4263;

    internal static bool IsForkedTowerContext()
    {
        return OccultCrescentTerritory.IsInForkedTower() ||
               PartySnapshotBuilder.HasStatus(
                   Plugin.ObjectTable.LocalPlayer?.StatusList,
                   DutiesAsAssigned);
    }
}
