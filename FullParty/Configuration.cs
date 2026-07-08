using Dalamud.Configuration;
using System;

namespace FullParty;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool IsConfigWindowMovable { get; set; } = true;
    public bool ShowLiveRoomData { get; set; }
    public string? ProtectedRefreshToken { get; set; }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
