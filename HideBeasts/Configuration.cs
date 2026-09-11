using Dalamud.Configuration;
using System;

namespace HideBeasts;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool Enabled { get; set; } = true;

    // exceptions - keep that group's beasts visible even while hiding is on.
    public bool ShowFriendSummons { get; set; } = false;
    public bool ShowFcSummons { get; set; } = false;
    public bool ShowPartySummons { get; set; } = false;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
