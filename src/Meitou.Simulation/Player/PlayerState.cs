using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Player;

/// <summary>
/// The player in the world (docs/game/ui-input.md section 7, economy.md): the faction that is theirs, their money and the characters selected.
/// Selection is world state, changed by <see cref="SelectCommand"/>, so a scripted game of selects and orders replays the same. There is no player
/// until a new game starts (<see cref="Exists"/>).
/// </summary>
public sealed class PlayerState
{
    #region Faction and options

    /// <summary>
    /// The player's faction, a position in the world's faction list (-1 before a game starts).
    /// </summary>
    public int Faction { get; set; } = -1;

    /// <summary>
    /// Whether a game has started (the player has a faction).
    /// </summary>
    public bool Exists => Faction >= 0;

    /// <summary>
    /// The player faction's name once renamed (the faction tab's name box, <see cref="RenameFactionCommand"/>; docs/game/ui-screens.md "Faction
    /// tab"); null: the faction's own name from the data ("Nameless"). Saved as the CAMERA string <c>pfaction name</c>, the GAMESTATE_FACTION name
    /// and the player platoons' faction name. Hashed only while set.
    /// </summary>
    public string? FactionName { get; set; }

    /// <summary>
    /// Money in cats (NEW_GAME_STARTOFF <c>money</c>; how it is spent is the economy stage).
    /// </summary>
    public long Money { get; set; }

    /// <summary>
    /// The squad AI options of the overview's AI tab that Meitou runs (<see cref="Meitou.Simulation.Player.SquadAi"/>); saved as CAMERA bools
    /// (<see cref="SquadAiKeys"/>).
    /// </summary>
    public SquadAi SquadAi { get; set; } = SquadAi.Defaults;

    /// <summary>
    /// The new-game option "Bandits loot the player" (global <c>142133598</c>, CAMERA bool <c>bl</c>, default on; docs/game/carry-loot.md "NPC
    /// looting"): off, NPCs loot none of the player's downed living characters. Not hashed: it changes only with a load.
    /// </summary>
    public bool BanditsLoot { get; set; } = true;

    #endregion

    #region Debug cheats

    /// <summary>
    /// The debug god mode is on (<see cref="DebugGodmode"/>). Not saved and not hashed: a loaded game starts with it off and the saved (original)
    /// stats.
    /// </summary>
    public bool Godmode { get; set; }

    /// <summary>
    /// The debug guard is on (<see cref="DebugGuard"/>): kills hostiles within <see cref="GuardRadius"/> of the lead and heals the player's
    /// characters. Not saved: a loaded game starts with it off.
    /// </summary>
    public bool Guard { get; set; }

    /// <summary>
    /// The guard's radius in metres.
    /// </summary>
    public float GuardRadius { get; set; } = 30;

    /// <summary>
    /// The guard also kills animals that do not attack a player character.
    /// </summary>
    public bool GuardAnimals { get; set; }

    #endregion

    #region Selection and inspection

    /// <summary>
    /// <see cref="Pointed"/> packed as generation and slot, read and written atomically.
    /// </summary>
    long _pointed = (long)CharacterId.None.Generation << 32 | (uint)CharacterId.None.Slot;

    /// <summary>
    /// The selected characters, in the order they were selected.
    /// </summary>
    public List<CharacterId> Selection { get; } = [];

    /// <summary>
    /// The active squad (the original's cached squad, <c>FUN_1407f5520</c>): the one the squad bar shows and recruits join. The start's squad at
    /// first; the squad tabs, Tab (<see cref="ActiveSquadCommand"/>) and selecting a character of another squad change it
    /// (<see cref="SquadManagement"/>).
    /// </summary>
    public int Squad { get; set; } = -1;

    /// <summary>
    /// The character of another faction the HUD shows (<see cref="InspectCommand"/>), or <see cref="CharacterId.None"/>. Display only: not hashed and
    /// not saved.
    /// </summary>
    public CharacterId Inspected { get; set; } = CharacterId.None;

    /// <summary>
    /// The character under the mouse pointer (the one a right click and its menu act on), or <see cref="CharacterId.None"/>: the publish gives it the
    /// menu's figures (kidnap and knock-out chances, shackles), which it skips for the crowd. Set by the host as the pointer moves, not by a command
    /// (any thread). Display only: not hashed and not saved.
    /// </summary>
    public CharacterId Pointed
    {
        get { long v = Volatile.Read(ref _pointed); return new((int)v, (int)(v >> 32)); }
        set => Volatile.Write(ref _pointed, (long)value.Generation << 32 | (uint)value.Slot);
    }

    /// <summary>
    /// The instance id of the building the HUD shows (<see cref="InspectBuildingCommand"/>), or null. Display only: not hashed and not saved.
    /// </summary>
    public string? InspectedBuilding { get; set; }

    /// <summary>
    /// The characters the pinned inventory windows show (<see cref="ShowInventoryCommand"/>). Display only: not hashed and not saved.
    /// </summary>
    public List<CharacterId> InventoryShown { get; } = [];

    /// <summary>
    /// Drops characters that are gone (serial phases, after the commit).
    /// </summary>
    internal void Prune(CharacterTable table)
    {
        bool hadSelection = Selection.Count > 0;
        Selection.RemoveAll(c => !table.TryResolveNext(c, out _));
        // Somebody stays selected (the original keeps one of the player's characters selected): when every selected one is gone or dead, the first
        // living one is.
        if (hadSelection && Exists && !Selection.Any(c => table.TryResolveNext(c, out int slot) && table.Cold(slot) is { DiedTick: < 0 }))
            for (int i = 0; i < table.HighWater; i++)
                if (table.Next[i].Alive && table.Cold(i) is { IsPlayer: true, DiedTick: < 0 })
                {
                    Selection.Clear();
                    Selection.Add(new CharacterId(i, table.Next[i].Generation));
                    break;
                }
        if (!Inspected.IsNone && !table.TryResolveNext(Inspected, out _)) Inspected = CharacterId.None;
        InventoryShown.RemoveAll(c => !table.TryResolveNext(c, out _));
    }

    #endregion

    #region Character creation

    /// <summary>
    /// The new game's character editor is open: the start's characters may still be remade, race and gender too
    /// (<see cref="Editing.EditCharacter"/>). Not saved.
    /// </summary>
    public bool InCreation { get; set; }

    /// <summary>
    /// The start's <c>force race</c> RACE ids the creation is limited to (empty: every playable race).
    /// </summary>
    public IReadOnlyList<string> CreationRaces { get; set; } = [];

    #endregion

    #region Open windows and the held item

    /// <summary>
    /// The container (<see cref="Locks.Fixture.Id"/>) whose window is open and the character who opened it (<see cref="Items.OpenContainerOrder"/>),
    /// and the one a character is still walking to. Display only: not hashed and not saved.
    /// </summary>
    public string? OpenContainer { get; set; }

    /// <summary>
    /// The character whose window of <see cref="OpenContainer"/> is open.
    /// </summary>
    public CharacterId ContainerOpener { get; set; } = CharacterId.None;

    /// <summary>
    /// The container a character is still walking to open (<see cref="ContainerWalker"/>).
    /// </summary>
    public string? OpeningContainer { get; set; }

    /// <summary>
    /// The character walking to <see cref="OpeningContainer"/>.
    /// </summary>
    public CharacterId ContainerWalker { get; set; } = CharacterId.None;

    /// <summary>
    /// The downed (or the player's own) character whose inventory window is open and who opened it (<see cref="Carrying.OpenLootOrder"/>), and the
    /// one a looter is still walking to. Display only, as the container.
    /// </summary>
    public CharacterId OpenLoot { get; set; } = CharacterId.None;

    /// <summary>
    /// The looter whose window of <see cref="OpenLoot"/> is open.
    /// </summary>
    public CharacterId LootOpener { get; set; } = CharacterId.None;

    /// <summary>
    /// The body a looter is still walking to (<see cref="LootWalker"/>).
    /// </summary>
    public CharacterId OpeningLoot { get; set; } = CharacterId.None;

    /// <summary>
    /// The looter walking to <see cref="OpeningLoot"/>.
    /// </summary>
    public CharacterId LootWalker { get; set; } = CharacterId.None;

    /// <summary>
    /// The door (<see cref="Locks.Fixture.Id"/>) a character is walking to open or close (<see cref="Locks.DoorOrder"/>), and who. Not hashed and not
    /// saved, as the container walk.
    /// </summary>
    public string? DoorTarget { get; set; }

    /// <summary>
    /// The character walking to <see cref="DoorTarget"/>.
    /// </summary>
    public CharacterId DoorWalker { get; set; } = CharacterId.None;

    /// <summary>
    /// The item on the mouse (<see cref="Items.HeldItem"/>, <see cref="HeldItems"/>), or null. Hashed; not saved: a save puts it back where it came from
    /// first (<see cref="World.ReturnHeldItem"/>).
    /// </summary>
    public Items.HeldItem? Held { get; set; }

    /// <summary>
    /// The <see cref="Items.HeldCommand.Serial"/> of the last held-item command applied (display only: not hashed, not saved).
    /// </summary>
    public long HeldSerial { get; set; }

    #endregion

    #region Map and factions met

    /// <summary>
    /// What the player knows of each placed town (<see cref="PopulationData.Sites"/> order): discovered, explored, recently discovered
    /// (docs/game/ui-screens.md "Map"). Replaced, never changed in place, so a snapshot may hold it. Not hashed: it follows from positions and
    /// nothing in the simulation reads it.
    /// </summary>
    public TownKnowledge[] Towns { get; private set; } = [];

    /// <summary>
    /// Adds <paramref name="flags"/> to a town's knowledge; true when that changed it.
    /// </summary>
    public bool Know(int site, TownKnowledge flags)
    {
        if (site < 0) return false;
        if (site < Towns.Length && (Towns[site] & flags) == flags) return false;
        var next = new TownKnowledge[Math.Max(Towns.Length, site + 1)];
        Towns.CopyTo(next, 0);
        next[site] |= flags;
        Towns = next;
        return true;
    }

    /// <summary>
    /// The camps and nests known (<see cref="CampView"/>), by wild site. Replaced, never changed in place.
    /// </summary>
    public IReadOnlyList<CampView> Camps { get; private set; } = [];

    /// <summary>
    /// Adds <paramref name="flags"/> to a camp's knowledge (adding the camp when new) and fills in its position when it has none; true when that
    /// changed it.
    /// </summary>
    public bool KnowCamp(CampView camp, TownKnowledge flags)
    {
        int i = -1;
        for (int k = 0; k < Camps.Count; k++) if (Camps[k].Site == camp.Site) { i = k; break; }
        var old = i >= 0 ? Camps[i] : null;
        var next = (old ?? camp) with { Known = (old?.Known ?? 0) | flags, Position = old?.Position ?? camp.Position };
        if (next == old) return false;
        var list = Camps.ToList();
        if (i >= 0) list[i] = next; else list.Add(next);
        Camps = list;
        return true;
    }

    /// <summary>
    /// The factions the player has met (positions in the world's faction list; docs/game/factions-squads-towns.md "Factions met"): only these are
    /// listed in the faction tab. Replaced, never changed in place. Not hashed: nothing in the simulation reads it. Saved as the player
    /// GAMESTATE_FACTION's <c>known</c> list.
    /// </summary>
    public IReadOnlySet<int> KnownFactions { get; set; } = new HashSet<int>();

    /// <summary>
    /// Adds a faction to <see cref="KnownFactions"/> (none for a negative one); true when it was not known.
    /// </summary>
    public bool Meet(int faction)
    {
        if (faction < 0 || KnownFactions.Contains(faction)) return false;
        KnownFactions = new HashSet<int>(KnownFactions) { faction };
        return true;
    }

    /// <summary>
    /// The map was opened: no town or camp is recently discovered any more.
    /// </summary>
    internal void SeeDiscoveries()
    {
        if (Towns.Any(t => (t & TownKnowledge.Recent) != 0)) Towns = [.. Towns.Select(t => t & ~TownKnowledge.Recent)];
        if (Camps.Any(c => (c.Known & TownKnowledge.Recent) != 0))
            Camps = [.. Camps.Select(c => c with { Known = c.Known & ~TownKnowledge.Recent })];
    }

    #endregion

    #region State hash

    /// <summary>
    /// Adds the hashed state: faction, money, squad, a changed squad AI, the rename, the held item and the selection.
    /// </summary>
    internal void Hash(ref StateHasher h)
    {
        h.Add(Faction);
        h.Add(Money);
        h.Add(Squad);
        if (SquadAi != SquadAi.Defaults) h.Add(0x53714169 + (int)SquadAi);
        if (FactionName is { } renamed)
        {
            h.Add(0x464E616D);
            h.Add(Rng.StableHash(renamed));
        }
        if (Held is { } held)
        {
            h.Add(0x48656c64);
            h.Add(Rng.StableHash(held.Item.Record));
            h.Add(held.Item.Quantity);
        }
        h.Add(Selection.Count);
        foreach (var c in Selection)
        {
            h.Add(c);
        }
    }

    #endregion
}

/// <summary>
/// What the player knows of a town (the GAMESTATE_TOWN bools <c>discovered</c>, <c>explored</c>, <c>recently discovered</c>; docs/game/ui-screens.md
/// "Map").
/// </summary>
[Flags]
public enum TownKnowledge : byte
{
    None = 0,
    /// <summary>
    /// On the map.
    /// </summary>
    Discovered = 1,
    /// <summary>
    /// Visited: the map names it and shows its faction's colour.
    /// </summary>
    Explored = 2,
    /// <summary>
    /// Found but the map not yet opened since: the map marks it as new.
    /// </summary>
    Recent = 4,
}

/// <summary>
/// A camp or nest the player knows (a wild nest, docs/game/ui-screens.md "Map"): its wild site, TOWN record, name, owner faction, whether that
/// faction is <c>not real</c> (the map calls it a nest, else a camp), where it is (null until it has loaded once) and what is known.
/// </summary>
public sealed record CampView(
    int Site,
    string TownId,
    string Name,
    string FactionId,
    bool Wildlife,
    System.Numerics.Vector3? Position,
    TownKnowledge Known);