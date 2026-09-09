using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using HideBeasts.Windows;
using Lumina.Excel.Sheets;

namespace HideBeasts;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/hidebeasts";
    private const string DebugCommandName = "/hidebeastsdebug";

    // Frames between object table sweeps. Kept at 1 (every frame): the game re-asserts the
    // render flag on actively-animating pets almost every frame, so a slower sweep lets them
    // flash back into view for a frame or two before we hide them again. Iterating the object
    // table is cheap enough to do every frame.
    private const int FrameworkUpdateInterval = 1;

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("HideBeasts");
    private ConfigWindow ConfigWindow { get; init; }

    // Beasts we've disabled the draw of, so we know what to re-enable later.
    private readonly HashSet<ulong> hiddenObjectIds = new();

    // Reused each sweep to avoid per-frame allocations.
    private readonly HashSet<ulong> seenThisSweep = new();

    // Remembers, per owner object id, whether that owner is a Beastmaster. In a crowd the
    // owner frequently streams out of the object table for a sweep or two while their pet is
    // still visible; without this cache SearchById() would miss, we'd treat the pet as "not a
    // beast", briefly un-hide it, then hide it again on the next sweep - the flicker the user
    // sees. Cleared on zone change along with hiddenObjectIds.
    private readonly Dictionary<ulong, bool> ownerIsBeastmaster = new();

    private uint? beastmasterJobId;
    private long nextJobLookupRetryTick;
    private int frameCounter;

    public bool HasFoundBeastmasterJob => beastmasterJobId.HasValue;
    public int HiddenCount => hiddenObjectIds.Count;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens Hide Beasts settings."
        });
        CommandManager.AddHandler(DebugCommandName, new CommandInfo(OnDebugCommand)
        {
            HelpMessage = "Dumps every battle NPC near you to /xllog, to help identify beast companions."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUi;

        Framework.Update += OnFrameworkUpdate;
        ClientState.TerritoryChanged += OnTerritoryChanged;

        Log.Information("HideBeasts loaded.");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        ClientState.TerritoryChanged -= OnTerritoryChanged;

        RestoreAllHidden();

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUi;

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(DebugCommandName);
    }

    private void OnCommand(string command, string args) => ToggleConfigUi();

    // Logs every non-player object nearby with its kind/subkind and owner's job. Useful for
    // re-identifying beast companions if a patch changes how the game classifies them.
    private void OnDebugCommand(string command, string args)
    {
        Log.Information("HideBeasts debug dump ---");
        var count = 0;
        foreach (var obj in ObjectTable)
        {
            if (obj is IPlayerCharacter)
                continue;

            count++;
            var subKind = obj is IBattleNpc npc ? npc.SubKind.ToString() : "n/a";
            var ownerId = obj is IBattleNpc bnpc ? bnpc.OwnerId : 0UL;

            var ownerInfo = "no owner";
            if (ownerId != 0 && ObjectTable.SearchById(ownerId) is IPlayerCharacter owner)
            {
                var abbr = owner.ClassJob.IsValid ? owner.ClassJob.Value.Abbreviation.ToString() : "?";
                ownerInfo = $"owner='{owner.Name}' job={abbr} (id={owner.ClassJob.RowId})";
            }
            else if (ownerId != 0)
            {
                ownerInfo = $"ownerId={ownerId} (not a resolvable player)";
            }

            Log.Information(
                $"obj name='{obj.Name}' kind={obj.ObjectKind} subKind={subKind} baseId={obj.BaseId} {ownerInfo}");
        }

        Log.Information($"HideBeasts debug dump end --- ({count} non-player objects total)");
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();

    private void OnTerritoryChanged(uint territoryType)
    {
        // Object table gets rebuilt on zone transition; nothing to restore.
        hiddenObjectIds.Clear();
        ownerIsBeastmaster.Clear();
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        if (++frameCounter < FrameworkUpdateInterval)
            return;
        frameCounter = 0;

        // Resolve regardless of Enabled, so the settings window stays accurate and hiding
        // can start immediately once toggled on.
        var jobId = GetBeastmasterJobId();

        if (!Configuration.Enabled)
            return;

        var localPlayer = ObjectTable.LocalPlayer;
        if (!ClientState.IsLoggedIn || localPlayer == null)
            return;

        if (jobId == null)
            return;

        var localId = localPlayer.GameObjectId;
        seenThisSweep.Clear();

        foreach (var obj in ObjectTable)
        {
            if (obj is not IBattleNpc { SubKind: (byte)BattleNpcSubKind.Pet } pet)
                continue;

            var ownerId = pet.OwnerId;

            // Not somebody else's pet - always leave visible.
            if (ownerId == 0 || ownerId == localId)
            {
                Show(pet);
                continue;
            }

            bool isOthersBeast;
            if (ObjectTable.SearchById(ownerId) is IPlayerCharacter owner && owner.ClassJob.RowId != 0)
            {
                // Owner is resolvable this sweep - trust it and refresh the cache.
                isOthersBeast = owner.ClassJob.RowId == jobId.Value;
                ownerIsBeastmaster[ownerId] = isOthersBeast;
            }
            else if (ownerIsBeastmaster.TryGetValue(ownerId, out var cached))
            {
                // Owner streamed out (common in a crowd) but we've classified them before.
                isOthersBeast = cached;
            }
            else
            {
                // Never seen this owner resolvable. Don't un-hide a pet we're already
                // hiding just because the owner blipped out; otherwise leave it alone.
                if (hiddenObjectIds.Contains(pet.GameObjectId))
                {
                    seenThisSweep.Add(pet.GameObjectId);
                    Hide(pet);
                }
                continue;
            }

            if (isOthersBeast)
            {
                seenThisSweep.Add(pet.GameObjectId);
                Hide(pet);
            }
            else
            {
                Show(pet);
            }
        }

        hiddenObjectIds.IntersectWith(seenThisSweep);
    }

    private unsafe void Hide(IBattleNpc pet)
    {
        // Must run every sweep, not just once: the game keeps flipping the render flag
        // back on for actively-animating pets, so a one-shot DisableDraw() won't stick.
        hiddenObjectIds.Add(pet.GameObjectId);

        var character = (Character*)pet.Address;
        if (character != null)
            character->DisableDraw();
    }

    private unsafe void Show(IBattleNpc pet)
    {
        if (!hiddenObjectIds.Remove(pet.GameObjectId))
            return;

        var character = (Character*)pet.Address;
        if (character != null)
            character->EnableDraw();
    }

    // Re-enables drawing for every companion still tracked as hidden.
    public unsafe void RestoreAllHidden()
    {
        if (hiddenObjectIds.Count == 0)
            return;

        foreach (var obj in ObjectTable)
        {
            if (obj is IBattleNpc { SubKind: (byte)BattleNpcSubKind.Pet } pet && hiddenObjectIds.Contains(pet.GameObjectId))
            {
                var character = (Character*)pet.Address;
                if (character != null)
                    character->EnableDraw();
            }
        }

        hiddenObjectIds.Clear();
    }

    private uint? GetBeastmasterJobId()
    {
        if (beastmasterJobId.HasValue)
            return beastmasterJobId;

        // Retry on a cooldown instead of giving up after one miss, in case this races
        // Dalamud's startup before the sheet is attached.
        var now = Environment.TickCount64;
        if (now < nextJobLookupRetryTick)
            return null;

        var isFirstAttempt = nextJobLookupRetryTick == 0;
        nextJobLookupRetryTick = now + 5000;

        var sheet = DataManager.GetExcelSheet<ClassJob>();
        var row = sheet.FirstOrDefault(cj => cj.Abbreviation.ToString() == "BST");

        if (row.RowId != 0)
        {
            beastmasterJobId = row.RowId;
            Log.Information($"HideBeasts: resolved Beastmaster job id to {row.RowId}.");
        }
        else if (isFirstAttempt)
        {
            Log.Warning("HideBeasts: could not find a job with abbreviation 'BST' in game data yet, will keep retrying.");
        }

        return beastmasterJobId;
    }
}
