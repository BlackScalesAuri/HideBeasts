using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using BattleNpcSubKind = Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
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
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/hidebeasts";

    // ticks between full owner-resolve sweeps. the cheap per-tick enforcement covers the gap.
    private const int ClassificationInterval = 10;

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("HideBeasts");
    private ConfigWindow ConfigWindow { get; init; }

    // hidden beasts: object id -> table slot, so enforcement can re-find them without a scan.
    private readonly Dictionary<ulong, int> hiddenPets = new();

    // beast owners -> their friend/party/fc facts, kept across sweeps so a pet holds its state
    // while the owner blips out of range. cleared on zone.
    private readonly Dictionary<ulong, OwnerInfo> ownerCache = new();

    // every visible player by id, rebuilt each sweep. lets us find a pet's owner without a
    // table scan. only held during the sweep.
    private readonly Dictionary<ulong, IPlayerCharacter> sweepPlayers = new();

    // reused every sweep so a sweep allocates nothing.
    private readonly List<(ulong Id, int Index, nint Address, ulong OwnerId)> petsThisSweep = new();
    private readonly HashSet<ulong> petIdsThisSweep = new();
    private readonly List<ulong> pendingRemovals = new();

    private uint? beastmasterJobId;
    private long nextJobLookupRetryTick;
    private int frameCounter;

    public bool HasFoundBeastmasterJob => beastmasterJobId.HasValue;
    public int HiddenCount => hiddenPets.Count;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "open configuration.\n/hidebeasts on|off|toggle switches it without the window.",
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
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "":
                ToggleConfigUi();
                break;
            case "on":
                SetEnabled(true);
                ChatGui.Print("[HideBeasts] Enabled - hiding other players' beasts.");
                break;
            case "off":
                SetEnabled(false);
                ChatGui.Print("[HideBeasts] Disabled - all beasts visible.");
                break;
            case "toggle":
                SetEnabled(!Configuration.Enabled);
                ChatGui.Print(Configuration.Enabled
                    ? "[HideBeasts] Enabled - hiding other players' beasts."
                    : "[HideBeasts] Disabled - all beasts visible.");
                break;
            default:
                ChatGui.PrintError($"[HideBeasts] Unknown subcommand '{args.Trim()}'. Use: on, off, toggle.");
                break;
        }
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();

    // shared by the config checkbox and the chat commands.
    public void SetEnabled(bool enabled)
    {
        Configuration.Enabled = enabled;
        Configuration.Save();

        if (!enabled)
            RestoreAllHidden();
    }

    private void OnTerritoryChanged(uint territoryType)
    {
        // Object table gets rebuilt on zone transition; nothing to restore.
        hiddenPets.Clear();
        ownerCache.Clear();
        frameCounter = 0;
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        // Resolve regardless of Enabled, so the settings window stays accurate and hiding
        // can start immediately once toggled on.
        var jobId = GetBeastmasterJobId();

        if (!Configuration.Enabled)
            return;

        if (!ClientState.IsLoggedIn || ObjectTable.LocalPlayer is not { } localPlayer || jobId == null)
            return;

        // the game re-enables draw on pets that move or cast, so re-hide the known ones
        // every tick. cheap - it only touches slots we already track.
        EnforceHidden();

        if (++frameCounter < ClassificationInterval)
            return;
        frameCounter = 0;

        Classify(localPlayer, jobId.Value);
    }

    // re-hide known pets whose draw the game turned back on.
    private unsafe void EnforceHidden()
    {
        foreach (var (id, index) in hiddenPets)
        {
            var obj = ObjectTable[index];
            if (obj is null || obj.GameObjectId != id)
                continue; // slot's empty or reused now; next sweep drops it

            var gameObject = (GameObject*)obj.Address;
            if (gameObject is not null && gameObject->DrawObject is not null)
                gameObject->DisableDraw();
        }
    }

    // full sweep: find nearby pets, work out whose they are, hide other Beastmasters'.
    private unsafe void Classify(IPlayerCharacter localPlayer, uint beastJobId)
    {
        var localId = localPlayer.GameObjectId;

        // FC members: the game gives us no FC id for other players, only the short tag string.
        // tags are only ~unique per world, not globally, so "same tag = same FC" is only safe
        // when the players around you are from your world - i.e. you're on your home world.
        // off-world we skip it entirely (the crowd is other worlds' players, and your FC is a
        // home-world thing anyway). also needs you to be in an FC. rare miss: a visitor to
        // your home world whose own FC happens to use your exact tag.
        var checkFc = Configuration.ShowFcSummons
                      && localPlayer.HomeWorld.RowId == localPlayer.CurrentWorld.RowId;
        var myFcTag = checkFc ? localPlayer.CompanyTag.TextValue : string.Empty;
        checkFc = myFcTag.Length > 0;

        sweepPlayers.Clear();
        petsThisSweep.Clear();
        petIdsThisSweep.Clear();

        // one pass: just index players and collect pets. the flag/tag reads happen later,
        // only for players that actually own a beast.
        foreach (var obj in ObjectTable)
        {
            switch (obj)
            {
                case IPlayerCharacter player:
                    sweepPlayers[player.GameObjectId] = player;
                    break;

                case IBattleNpc { SubKind: (byte)BattleNpcSubKind.Pet } pet:
                    petsThisSweep.Add((pet.GameObjectId, pet.ObjectIndex, pet.Address, pet.OwnerId));
                    petIdsThisSweep.Add(pet.GameObjectId);
                    break;
            }
        }

        foreach (var (petId, petIndex, petAddress, ownerId) in petsThisSweep)
        {
            bool hide;
            if (ownerId == 0 || ownerId == localId)
            {
                // unowned or ours - leave it
                hide = false;
            }
            else if (sweepPlayers.TryGetValue(ownerId, out var owner))
            {
                if (owner.ClassJob.RowId != beastJobId)
                {
                    ownerCache.Remove(ownerId);
                    hide = false;
                }
                else
                {
                    var flags = owner.StatusFlags;
                    var info = new OwnerInfo(
                        (flags & StatusFlags.Friend) != 0,
                        (flags & StatusFlags.PartyMember) != 0,
                        checkFc && owner.CompanyTag.TextValue == myFcTag);
                    ownerCache[ownerId] = info;
                    hide = !Exempt(info);
                }
            }
            else if (ownerCache.TryGetValue(ownerId, out var cached))
            {
                // owner blipped out of range - go with what we last saw
                hide = !Exempt(cached);
            }
            else
            {
                // unknown owner - keep hiding it if we already were, but don't start now
                hide = hiddenPets.ContainsKey(petId);
            }

            if (hide)
            {
                if (!hiddenPets.ContainsKey(petId))
                    SetDraw(petAddress, false);
                hiddenPets[petId] = petIndex;
            }
            else if (hiddenPets.Remove(petId))
            {
                SetDraw(petAddress, true);
            }
        }

        // drop pets that left the table entirely. they get re-checked if they come back.
        pendingRemovals.Clear();
        foreach (var id in hiddenPets.Keys)
        {
            if (!petIdsThisSweep.Contains(id))
                pendingRemovals.Add(id);
        }
        foreach (var id in pendingRemovals)
            hiddenPets.Remove(id);

        sweepPlayers.Clear(); // don't hold wrapper refs between sweeps
    }

    private static unsafe void SetDraw(nint address, bool enabled)
    {
        var gameObject = (GameObject*)address;
        if (gameObject is null)
            return;

        if (enabled)
            gameObject->EnableDraw();
        else
            gameObject->DisableDraw();
    }

    // Re-enables drawing for every companion still tracked as hidden.
    public unsafe void RestoreAllHidden()
    {
        foreach (var (id, index) in hiddenPets)
        {
            var obj = ObjectTable[index];
            if (obj is null || obj.GameObjectId != id)
                continue;

            SetDraw(obj.Address, true);
        }

        hiddenPets.Clear();
        ownerCache.Clear();
    }

    // does an enabled exception cover this owner?
    private bool Exempt(OwnerInfo o) =>
        (Configuration.ShowFriendSummons && o.IsFriend) ||
        (Configuration.ShowPartySummons && o.IsParty) ||
        (Configuration.ShowFcSummons && o.IsFcMember);

    private readonly record struct OwnerInfo(bool IsFriend, bool IsParty, bool IsFcMember);

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
