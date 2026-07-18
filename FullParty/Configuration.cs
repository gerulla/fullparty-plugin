using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace FullParty;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool IsConfigWindowMovable { get; set; } = true;
    public bool ShowLiveRoomData { get; set; }
    public bool BypassLiveCommandRequirements { get; set; }
    public bool MovableLiveRoomStatus { get; set; }
    public bool ObviousReadyCheck { get; set; }
    public bool RosterHiddenByDefault { get; set; }
    public string? ProtectedRefreshToken { get; set; }
    public List<string> FavoriteGroupSlugs { get; set; } = [];

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
