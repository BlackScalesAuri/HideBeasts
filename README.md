# Hide Beasts

**Discontinued.** The official [Visibility](https://github.com/SheepGoMeh/VisibilityPlugin) plugin (in
the main Dalamud plugin repo) now covers this - install that instead. This repo won't get further
updates and has been pulled from
[AzeraKih-Plugins](https://github.com/BlackScalesAuri/AzeraKih-Plugins), so it can no longer be
freshly installed.

Dalamud plugin for FFXIV. Hides other players' Beastmaster (BST) companions. Your own beast stays visible.

Based on [goatcorp/SamplePlugin](https://github.com/goatcorp/SamplePlugin).

## How it works

On a timer, walks the object table for `BattleNpc`s with `SubKind == Pet`. For each one,
resolves the owner and checks their current job against BST's job ID (looked up at runtime
by abbreviation, not hardcoded). If it's not you, `Character.DisableDraw()` gets called on
the pet every sweep — the game keeps re-enabling the render flag on active pets, so a one-shot
call doesn't stick.

Hidden pets are tracked so they get restored when: the setting is turned off, you zone, or
the plugin unloads.

## Building

Needs a Dalamud dev environment (defaults to `%AppData%\XIVLauncher\addon\Hooks\dev`, override
with `DALAMUD_HOME`).

```
dotnet build HideBeasts.slnx -c Release
```

Output: `HideBeasts/bin/x64/Release/HideBeasts.dll`

## Loading in-game

For development:

1. `/xlsettings` → Experimental → Dev Plugin Locations → add the folder containing the built DLL.
2. `/xlplugins` → Dev Tools tab → enable "Hide Beasts".
3. `/hidebeasts` opens settings; `/hidebeasts on|off|toggle` switches it without the window.

For everyone else, it's distributed through [AzeraKih-Plugins](https://github.com/BlackScalesAuri/AzeraKih-Plugins).

## Releasing a new version

1. Bump `<Version>` in `HideBeasts/HideBeasts.csproj` and `AssemblyVersion` in
   `HideBeasts/HideBeasts.json` to match.
2. Commit, then tag and push: `git tag v0.0.0.2 && git push --tags`.
3. `.github/workflows/release.yaml` builds it and creates a GitHub Release with the zip
   attached automatically.
4. [AzeraKih-Plugins](https://github.com/BlackScalesAuri/AzeraKih-Plugins) picks up the new
   release on its own (scheduled, or immediately if `DISPATCH_TOKEN` is configured here).
