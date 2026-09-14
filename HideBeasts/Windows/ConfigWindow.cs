using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
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
        ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "Discontinued.");
        ImGui.TextWrapped(
            "The main repo's \"Visibility\" plugin now does the same thing. Please switch to it - " +
            "Hide Beasts won't get further updates.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var enabled = configuration.Enabled;
        if (ImGui.Checkbox("Hide other players' beasts", ref enabled))
        {
            plugin.SetEnabled(enabled);
        }

        ImGui.TextWrapped("Hides other players' Beastmaster companions. Your own is always shown.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextDisabled("Keep these players' beasts visible:");
        ImGui.BeginDisabled(!configuration.Enabled);

        var changed = false;

        var friends = configuration.ShowFriendSummons;
        if (ImGui.Checkbox("Friends", ref friends))
        {
            configuration.ShowFriendSummons = friends;
            changed = true;
        }

        var fc = configuration.ShowFcSummons;
        if (ImGui.Checkbox("Free Company members", ref fc))
        {
            configuration.ShowFcSummons = fc;
            changed = true;
        }
        ImGuiComponents.HelpMarker("Only works while you're on your home world.");

        var party = configuration.ShowPartySummons;
        if (ImGui.Checkbox("Party members", ref party))
        {
            configuration.ShowPartySummons = party;
            changed = true;
        }

        ImGui.EndDisabled();

        // save after EndDisabled: Save() can throw on file i/o and we mustn't skip EndDisabled.
        if (changed)
            configuration.Save();

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
