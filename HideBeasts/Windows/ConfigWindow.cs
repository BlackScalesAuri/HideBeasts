using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace HideBeasts.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin) : base("Hide Beasts###HideBeastsConfig")
    {
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize;

        this.plugin = plugin;
        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var enabled = configuration.Enabled;
        if (ImGui.Checkbox("Hide other players' beasts", ref enabled))
        {
            configuration.Enabled = enabled;
            configuration.Save();
            if (!enabled)
            {
                plugin.RestoreAllHidden();
            }
        }

        ImGui.TextWrapped(
            "When enabled, Beastmaster companions summoned by other players are hidden. " +
            "Your own beast companion is always left visible.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (!plugin.HasFoundBeastmasterJob)
        {
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.2f, 1f),
                "Could not find the Beastmaster job in game data yet. Nothing will be hidden until this resolves.");
        }
        else
        {
            ImGui.Text($"Currently hiding {plugin.HiddenCount} beast companion(s).");
        }
    }
}
