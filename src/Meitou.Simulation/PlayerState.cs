namespace Meitou.Simulation;

/// <summary>
/// The player in the world (docs/game/ui-input.md section 7, economy.md): the faction that is theirs, their money and the characters
/// selected. Selection is world state, changed by <see cref="SelectCommand"/>, so a scripted game of selects and orders replays the
/// same. There is no player until a new game starts (<see cref="Exists"/>).
/// </summary>
public sealed class PlayerState
{
    /// <summary>The player's faction, a position in the world's faction list (-1 before a game starts).</summary>
    public int Faction { get; set; } = -1;
    public bool Exists => Faction >= 0;
    /// <summary>
    /// The player faction's name once renamed (the faction tab's name box, <see cref="RenameFactionCommand"/>; docs/game/ui-screens.md "Faction tab"); null: the faction's own name from the data
    /// ("Nameless"). Saved as the CAMERA string <c>pfaction name</c>, the GAMESTATE_FACTION name and the player platoons' faction name. Hashed only while set.
    /// </summary>
    public string? FactionName { get; set; }
    /// <summary>Money in cats (NEW_GAME_STARTOFF <c>money</c>; how it is spent is the economy stage).</summary>
    public long Money { get; set; }
    /// <summary>The selected characters, in the order they were selected.</summary>
    public List<CharacterId> Selection { get; } = [];
    /// <summary>
    /// The active squad (the original's cached squad, <c>FUN_1407f5520</c>): the one the squad bar shows and recruits join. The start's squad at first; the squad tabs, Tab
    /// (<see cref="ActiveSquadCommand"/>) and selecting a character of another squad change it (<see cref="SquadManagement"/>).
    /// </summary>
    public int Squad { get; set; } = -1;
    /// <summary>The new game's character editor is open: the start's characters may still be remade, race and gender too (<see cref="Editing.EditCharacter"/>). Not saved.</summary>
    public bool InCreation { get; set; }
    /// <summary>The start's <c>force race</c> RACE ids the creation is limited to (empty: every playable race).</summary>
    public IReadOnlyList<string> CreationRaces { get; set; } = [];
    /// <summary>The character of another faction the HUD shows (<see cref="InspectCommand"/>), or <see cref="CharacterId.None"/>. Display only: not hashed and not saved.</summary>
    public CharacterId Inspected { get; set; } = CharacterId.None;
    /// <summary>
    /// The character under the mouse pointer (the one a right click and its menu act on), or <see cref="CharacterId.None"/>: the publish gives it the menu's figures (kidnap and knock-out chances,
    /// shackles), which it skips for the crowd. Set by the host as the pointer moves, not by a command (any thread). Display only: not hashed and not saved.
    /// </summary>
    public CharacterId Pointed
    {
        get { long v = Volatile.Read(ref pointed); return new((int)v, (int)(v >> 32)); }
        set => Volatile.Write(ref pointed, (long)value.Generation << 32 | (uint)value.Slot);
    }
    long pointed = (long)CharacterId.None.Generation << 32 | (uint)CharacterId.None.Slot;
    /// <summary>The instance id of the building the HUD shows (<see cref="InspectBuildingCommand"/>), or null. Display only: not hashed and not saved.</summary>
    public string? InspectedBuilding { get; set; }
    /// <summary>The characters the pinned inventory windows show (<see cref="ShowInventoryCommand"/>). Display only: not hashed and not saved.</summary>
    public List<CharacterId> InventoryShown { get; } = [];
    /// <summary>The container (<see cref="Locks.Fixture.Id"/>) whose window is open and the character who opened it (<see cref="Items.OpenContainerOrder"/>), and the one a character is still walking to. Display only: not hashed and not saved.</summary>
    public string? OpenContainer { get; set; }
    public CharacterId ContainerOpener { get; set; } = CharacterId.None;
    public string? OpeningContainer { get; set; }
    public CharacterId ContainerWalker { get; set; } = CharacterId.None;
    /// <summary>The downed (or the player's own) character whose inventory window is open and who opened it (<see cref="Carrying.OpenLootOrder"/>), and the one a looter is still walking to. Display only, as the container.</summary>
    public CharacterId OpenLoot { get; set; } = CharacterId.None;
    public CharacterId LootOpener { get; set; } = CharacterId.None;
    public CharacterId OpeningLoot { get; set; } = CharacterId.None;
    public CharacterId LootWalker { get; set; } = CharacterId.None;
    /// <summary>The door (<see cref="Locks.Fixture.Id"/>) a character is walking to open or close (<see cref="Locks.DoorOrder"/>), and who. Not hashed and not saved, as the container walk.</summary>
    public string? DoorTarget { get; set; }
    public CharacterId DoorWalker { get; set; } = CharacterId.None;
    /// <summary>
    /// What the player knows of each placed town (<see cref="PopulationData.Sites"/> order): discovered, explored, recently discovered (docs/game/ui-screens.md "Map").
    /// Replaced, never changed in place, so a snapshot may hold it. Not hashed: it follows from positions and nothing in the simulation reads it.
    /// </summary>
    public TownKnowledge[] Towns { get; private set; } = [];
    /// <summary>The squad AI options of the overview's AI tab that Meitou runs (<see cref="Simulation.SquadAi"/>); saved as CAMERA bools (<see cref="SquadAiKeys"/>).</summary>
    public SquadAi SquadAi { get; set; } = SquadAi.Defaults;
    /// <summary>
    /// The new-game option "Bandits loot the player" (global <c>142133598</c>, CAMERA bool <c>bl</c>, default on; docs/game/carry-loot.md "NPC looting"): off, NPCs loot none of the player's
    /// downed living characters. Not hashed: it changes only with a load.
    /// </summary>
    public bool BanditsLoot { get; set; } = true;
    /// <summary>
    /// The item on the mouse (<see cref="Items.HeldItem"/>, Items/HeldItems.cs), or null. Hashed; not saved: a save puts it back where it came from first (<see cref="World.ReturnHeldItem"/>).
    /// </summary>
    public Items.HeldItem? Held { get; set; }
    /// <summary>The <see cref="Items.HeldCommand.Serial"/> of the last held-item command applied (display only: not hashed, not saved).</summary>
    public long HeldSerial { get; set; }

    /// <summary>Adds <paramref name="flags"/> to a town's knowledge; true when that changed it.</summary>
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

    /// <summary>The camps and nests known (<see cref="CampView"/>), by wild site. Replaced, never changed in place.</summary>
    public IReadOnlyList<CampView> Camps { get; private set; } = [];

    /// <summary>Adds <paramref name="flags"/> to a camp's knowledge (adding the camp when new) and fills in its position when it has none; true when that changed it.</summary>
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
    /// The factions the player has met (positions in the world's faction list; docs/game/factions-squads-towns.md "Factions met"): only these are listed in the faction tab. Replaced, never
    /// changed in place. Not hashed: nothing in the simulation reads it. Saved as the player GAMESTATE_FACTION's <c>known</c> list.
    /// </summary>
    public IReadOnlySet<int> KnownFactions { get; set; } = new HashSet<int>();

    /// <summary>Adds a faction to <see cref="KnownFactions"/> (none for a negative one); true when it was not known.</summary>
    public bool Meet(int faction)
    {
        if (faction < 0 || KnownFactions.Contains(faction)) return false;
        KnownFactions = new HashSet<int>(KnownFactions) { faction };
        return true;
    }

    internal void SeeDiscoveries()
    {
        if (Towns.Any(t => (t & TownKnowledge.Recent) != 0)) Towns = [.. Towns.Select(t => t & ~TownKnowledge.Recent)];
        if (Camps.Any(c => (c.Known & TownKnowledge.Recent) != 0)) Camps = [.. Camps.Select(c => c with { Known = c.Known & ~TownKnowledge.Recent })];
    }

    /// <summary>Drops characters that are gone (serial phases, after the commit).</summary>
    internal void Prune(CharacterTable table)
    {
        bool hadSelection = Selection.Count > 0;
        Selection.RemoveAll(c => !table.TryResolveNext(c, out _));
        // Somebody stays selected (the original keeps one of the player's characters selected): when every selected one is gone or dead, the first living one is.
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

    internal void Hash(ref StateHasher h)
    {
        h.Add(Faction);
        h.Add(Money);
        h.Add(Squad);
        if (SquadAi != SquadAi.Defaults) h.Add(0x53714169 + (int)SquadAi);
        if (FactionName is { } renamed) { h.Add(0x464E616D); h.Add(Rng.StableHash(renamed)); }
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
}

/// <summary>
/// The faction tab's rename (<c>FUN_1404923d0</c>, docs/game/ui-screens.md "Faction tab"): a name no faction has yet becomes the player faction's name; a taken one is refused with
/// "This faction name already exists" (an empty name is refused too, <b>engine choice</b>: the original's empty-name rule is <b>Unknown</b>).
/// </summary>
public sealed record RenameFactionCommand(string Name) : SimCommand;

/// <summary>Applies the player's selection and stop commands (step 1 of the tick). Move orders are the movement system's.</summary>
public sealed partial class PlayerSystem(PopulationData? data = null) : ITickSystem
{
    readonly CharacterAssembly? assembly = data is null ? null : new(data);
    internal PopulationData? Data => data;
    /// <summary>The longest faction name taken (engine choice: the original's box limit is <b>Unknown</b>).</summary>
    public const int FactionNameLength = 64;

    /// <summary>The name the player faction goes by: the rename, else the data's.</summary>
    public string? PlayerFactionName(World world) =>
        world.Player.FactionName ?? (data is not null && world.Player.Faction >= 0 && world.Player.Faction < data.Factions.Count ? data.Factions[world.Player.Faction].Name : null);

    void RenameFaction(World world, string text)
    {
        var player = world.Player;
        string name = text.Trim();
        if (name.Length > FactionNameLength) name = name[..FactionNameLength];
        if (!player.Exists || name.Length == 0 || name == PlayerFactionName(world)) return;
        // FUN_1402e7950: any other faction already named so. (The data's names; the player's own old name is free again.)
        if (data is not null && data.Factions.Where((f, i) => i != player.Faction).Any(f => f.Name == name)) { world.Post("This faction name already exists"); return; }
        player.FactionName = name == data?.Factions[player.Faction].Name ? null : name;
    }

    /// <summary>The characters a right-click order goes to (an empty list: the selection), null for any other command.</summary>
    static IReadOnlyList<CharacterId>? Ordered(SimCommand command) => command switch
    {
        MoveOrder o => o.Characters, FollowOrder o => o.Characters, Combat.AttackOrder o => o.Attackers, Combat.ShootAtOrder o => o.Gunners,
        Carrying.PickUpOrder o => o.Carriers, Carrying.PutDownOrder o => o.Carriers, Carrying.LootOrder o => o.Looters, Carrying.OpenLootOrder o => o.Looters,
        Carrying.StealthKoOrder o => o.Attackers, FirstAid.TreatOrder o => o.Medics, FirstAid.SplintOrder o => o.Medics, Locks.PickLockOrder o => o.Characters,
        Locks.UseBedOrder o => o.Characters, Production.OperateOrder o => o.Characters, Construction.BuildOrder o => o.Characters,
        _ => null,
    };

    /// <summary>
    /// <c>playerWantsMeToGetUp</c> (Character <c>+0xec</c>, docs/game/ragdolls.md "Staying low"): the order dispatch sets it on each selected character it reaches; one playing dead gets
    /// up (staying low when sneaking), one still getting up stands rather than staying low. Internal orders (an empty selection, or none of it) leave it.
    /// </summary>
    static void WantsToGetUp(World world, IReadOnlyList<CharacterId> ordered)
    {
        var table = world.Characters;
        foreach (var id in ordered.Count > 0 ? ordered : world.Player.Selection)
        {
            if (!world.Player.Selection.Contains(id) || !table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { IsPlayer: true, Medical: { } medical } cold) continue;
            if (medical.PlayingDead)
            {
                bool low = Orders.Has(cold, CharacterOrders.Sneak);
                medical.StandUp(low);
                // Standing up (not staying low) on the order trains toughness when enemies are about.
                if (!low) world.SystemOf<BodySystem>()?.GotUpByOrder(world, slot, cold);
            }
            else if (medical.StayingLow && CharacterFacts.GettingUp(cold, world.Tick)) medical.StandUp();
        }
    }

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        var table = world.Characters;
        var player = world.Player;
        foreach (var command in commands)
        {
            if (Ordered(command) is { } ordered) WantsToGetUp(world, ordered);
            switch (command)
            {
                case SelectCommand select:
                    var selectable = select.Characters.Where(id =>
                        table.TryResolveNext(id, out int slot) && table.Cold(slot)!.IsPlayer).ToArray();
                    if (selectable.Length == 0) break;
                    // A shift-click on a selected character takes it out, never emptying the selection (it stays when it is the only one).
                    if (select.Toggle && selectable.Length == 1 && player.Selection.Contains(selectable[0]))
                    {
                        if (player.Selection.Count >= 2) player.Selection.Remove(selectable[0]);
                        break;
                    }
                    // Selecting meets the owner's faction (FUN_1407f7f20), here the player's.
                    player.Meet(player.Faction);
                    // Picking one of your own shows them: the inspected character of another faction no longer takes the HUD.
                    player.Inspected = CharacterId.None;
                    if (!select.Additive) player.Selection.Clear();
                    foreach (var id in selectable)
                        if (!player.Selection.Contains(id)) player.Selection.Add(id);
                    // Selecting someone of another squad shows that squad (engine reading of the original's cached squad).
                    if (!select.Additive && table.TryResolveNext(selectable[0], out int first) && world.Squads.Find(table.Cold(first)!.SquadId) is { } theirs
                        && theirs.Faction == player.Faction) player.Squad = theirs.Id;
                    break;
                case SquadCommand squadCommand:
                    SquadManagement.Apply(world, data, squadCommand);
                    break;
                case SeenDiscoveriesCommand:
                    player.SeeDiscoveries();
                    break;
                case InspectCommand inspect:
                    player.Inspected = inspect.Target;
                    if (table.TryResolveNext(inspect.Target, out int inspected)) player.Meet(table.Cold(inspected)!.Faction);
                    break;
                case ShowInventoryCommand show:
                    player.InventoryShown.Clear(); player.InventoryShown.AddRange(show.Targets);
                    break;
                case Items.OpenContainerOrder open:
                    OpenContainerInput(world, open);
                    break;
                case Items.CloseContainer:
                    player.OpenContainer = player.OpeningContainer = null;
                    player.OpenLoot = player.OpeningLoot = CharacterId.None;
                    break;
                case InspectBuildingCommand building:
                    player.InspectedBuilding = building.Instance;
                    // The original selects a building with a door as that door; Meitou keeps a building's owner on its doors and other fixtures.
                    if (building.Instance is { } instance && world.Fixtures.OfBuilding(instance) is var fixtures)
                        player.Meet((fixtures.FirstOrDefault(f => f.Kind == Locks.FixtureKind.Door) ?? fixtures.FirstOrDefault())?.Owner ?? -1);
                    break;
                case StopCommand stop:
                    foreach (var id in stop.Characters.Count > 0 ? stop.Characters : player.Selection.ToArray())
                    {
                        if (!table.TryResolveNext(id, out int slot) || !table.Cold(slot)!.IsPlayer) continue;
                        table.Cold(slot)!.OrderQueue.Clear();   // drops the queued orders too
                        TaskStart.End(world, slot, TaskEnd.Stopped);
                        table.Cold(slot)!.FollowOrder = CharacterId.None;
                    }
                    break;
                case Items.ReadMapItem read:
                    ReadMap(world, read);
                    break;
                case Items.HeldCommand held:
                    HeldInput(world, held);
                    break;
                case Items.QuickMoveItem quick:
                    QuickMove(world, quick);
                    break;
                case RenameFactionCommand rename:
                    RenameFaction(world, rename.Name);
                    break;
                case SquadAiCommand option:
                    player.SquadAi = option.On ? player.SquadAi | option.Option : player.SquadAi & ~option.Option;
                    break;
                case SquadFormationCommand formation:
                    SquadAiKeys.SetFormation(world, player.Faction, (int)formation.Formation, data?.Factions);
                    break;
                case OrdersCommand or RunSpeedCommand or StandUpCommand:
                    Orders.Apply(world, command);
                    break;
                case Editing.EditCharacter edit:
                    if (data is not null) Editing.CharacterEditing.Apply(world, data, edit);
                    break;
                case DebugCommand debug:
                    DebugCheats.Apply(world, data, assembly, debug);
                    break;
                case Editing.FinishCreation:
                    player.InCreation = false;
                    break;
                case Items.PickUpItemOrder pick:
                    PickUpInput(world, pick);
                    break;
                case Items.ArrangeInventory arrange:
                    if (table.TryResolveNext(arrange.Character, out int character) && table.Cold(character) is { IsPlayer: true, Inventory: { } carried } &&
                        !Items.InventoryCommands.Arrange(carried)) world.Post("Cannot arrange inventory: the items do not fit.");
                    break;
            }
        }
    }

    public void SlowWorld(World world)
    {
        SquadManagement.BuryDead(world, data?.Db);
        world.Player.Prune(world.Characters);
        SquadManagement.KeepActive(world);
        PickUpWorkers(world);
        ContainerWorkers(world);
        KeepHeld(world);
    }

    /// <summary>A map item of a player character's inventory: its towns become discovered and recently discovered, and it is used up; with nothing new it stays.</summary>
    void ReadMap(World world, Items.ReadMapItem read)
    {
        var table = world.Characters;
        if (data is null || !table.TryResolveNext(read.Character, out int owner) || table.Cold(owner) is not { IsPlayer: true, Inventory: { } inventory }) return;
        var t = read.Item;
        var item = inventory.All().ElementAtOrDefault(t.Index);
        if (item is null || item.Record != t.Record || item.Section != t.Section || item.X != t.X || item.Y != t.Y || item.Type != Meitou.Data.Fcs.FcsRecordType.MAP_ITEM
            || data.Db.Find(item.Record) is not { } record) return;
        var sites = Items.MapItems.Reveals(data, record, world.Player.Towns, world.Seed);
        if (sites.Count == 0) { world.Post("You already know all these locations, no new information can be learned."); return; }
        foreach (int site in sites) world.Player.Know(site, TownKnowledge.Discovered | TownKnowledge.Recent);
        inventory.RemoveOne(item);
        world.Post($"{sites.Count} location(s) added to the map.");
    }
}

/// <summary>
/// The squad AI options of the overview's AI tab (docs/game/ai-tasks.md "Squad AI options"): the original's bytes at <c>DAT_142134690</c> +0x110 .. +0x11a. Ditch, Sleep and Sit start
/// implicit jobs (<see cref="Production.ProductionSystem"/>); the others widen what other rules take in.
/// </summary>
[Flags]
public enum SquadAi : ushort
{
    None = 0,
    /// <summary>Ditch items (+0x117): idle characters put carried materials into storage (job 242).</summary>
    Ditch = 1,
    /// <summary>Sleep when injured (+0x116): idle injured characters go to bed until healed (jobs 153 and 257).</summary>
    Sleep = 2,
    /// <summary>Sit when idle (+0x118): idle characters at home sit on a free chair (job 275).</summary>
    Sit = 4,
    /// <summary>Heal Allies (+0x110): the medic and robotics jobs also treat characters of allied factions (<see cref="FirstAid.FirstAidSystem"/>).</summary>
    Heal = 8,
    /// <summary>Help Allies (+0x111): the player's characters count allied factions' fighters in the protect-allies finder (squared; <see cref="RetaliationSystem"/> goal 15).</summary>
    Help = 16,
    /// <summary>Rescue Allies (+0x112): the rescue job also picks up downed characters of allied factions (<see cref="Carrying.CarrySystem"/>).</summary>
    Rescue = 32,
    /// <summary>Share Food (+0x115): a hungry character with no food of its own eats from a squad mate's backpack within <see cref="SquadAiRules.ShareReach"/> (<see cref="Items.FeedSystem"/>).</summary>
    Share = 64,
    /// <summary>Feed Animals (+0x114): off, the player's animals do not eat (<see cref="Items.FeedSystem"/>).</summary>
    Feed = 128,
    /// <summary>Shoot first (+0x11a): the player's turret gunners also take dangerous neutrals (<see cref="SquadAiRules.ShootsFirstAt"/>).</summary>
    ShootFirst = 256,
    /// <summary>Heal Neutrals (Meitou-only): player medics also treat downed characters of factions that are no enemy (<see cref="FirstAid.FirstAidSystem"/> pass 2).</summary>
    HealNeutrals = 512,
    /// <summary>Heal All (Meitou-only): as <see cref="HealNeutrals"/>, and downed enemies too.</summary>
    HealAll = 1024,
    /// <summary>A new game's options (<b>Observed</b>: 511 of the owner's saves; heal and help are on in 4, rescue and shoot first in none).</summary>
    Defaults = Ditch | Sleep | Sit | Share | Feed,
}

/// <summary>Turns a squad AI option on or off.</summary>
public sealed record SquadAiCommand(SquadAi Option, bool On) : SimCommand;

/// <summary>
/// The AI tab's Follow Formation: the player faction's <c>squad formation</c> (<c>FUN_140494ce0</c> binds its combo to the FACTION record's value; the movement rebuilds the slots,
/// as the original's change handler marks every player squad).
/// </summary>
public sealed record SquadFormationCommand(SquadFormation Formation) : SimCommand;

/// <summary>The squad AI options' CAMERA bools (<b>Verified</b>, the camera record writer <c>FUN_1403eb710</c> and reader <c>FUN_1403ebf50</c>: the byte each key holds).</summary>
public static class SquadAiKeys
{
    public static readonly (SquadAi Option, string Key)[] Keys =
    [
        (SquadAi.Ditch, "ditch"), (SquadAi.Sleep, "sleep"), (SquadAi.Sit, "sit"), (SquadAi.Heal, "heal"), (SquadAi.Help, "help"), (SquadAi.Rescue, "rescue"),
        (SquadAi.Share, "share"), (SquadAi.Feed, "feed"), (SquadAi.ShootFirst, "shootfirst"),
    ];

    /// <summary>The Meitou-only options' CAMERA bools, written only while on.</summary>
    public static readonly (SquadAi Option, string Key)[] MeitouKeys = [(SquadAi.HealNeutrals, "meitou heal neutrals"), (SquadAi.HealAll, "meitou heal all")];

    public static void Write(Meitou.Data.Fcs.FcsRecord camera, SquadAi options)
    {
        foreach (var (option, key) in Keys) camera.Bools[key] = (options & option) != 0;
        foreach (var (option, key) in MeitouKeys)
            if ((options & option) != 0) camera.Bools[key] = true;
            else camera.Bools.Remove(key);
    }

    /// <summary>The options a CAMERA record holds; an absent key keeps the option's default (<see cref="SquadAi.Defaults"/>).</summary>
    public static SquadAi Read(Meitou.Data.Fcs.FcsRecord camera)
    {
        var options = SquadAi.Defaults;
        foreach (var (option, key) in Keys.Concat(MeitouKeys))
            if (camera.Bools.TryGetValue(key, out bool on)) options = on ? options | option : options & ~option;
        return options;
    }

    /// <summary>The CAMERA int holding the player faction's <c>squad formation</c> (<b>Verified</b>: the same writer and reader; read only when the record has <c>ditch</c>).</summary>
    public const string FormationKey = "formation";

    /// <summary>Sets <paramref name="faction"/>'s formation in <see cref="World.FactionFormations"/> (made from <paramref name="factions"/> when the population has not set it yet).</summary>
    public static void SetFormation(World world, int faction, int formation, IReadOnlyList<Meitou.Data.Gameplay.FactionData>? factions)
    {
        var list = world.FactionFormations?.ToArray() ?? factions?.Select(f => f.SquadFormation).ToArray();
        if (list is null || (uint)faction >= (uint)list.Length) return;
        list[faction] = formation;
        world.FactionFormations = list;
    }

    /// <summary>The player faction's formation as the AI tab and the save see it: the population's list, else the data's value, else RANDOM.</summary>
    public static int FormationOf(World world, int faction, IReadOnlyList<Meitou.Data.Gameplay.FactionData>? factions) =>
        world.FactionFormations is { } list && (uint)faction < (uint)list.Count ? list[faction]
        : factions is not null && (uint)faction < (uint)factions.Count ? factions[faction].SquadFormation : (int)SquadFormation.Random;
}

/// <summary>Rules of the squad AI options that other systems ask (docs/game/ai-tasks.md "Squad AI options").</summary>
public static class SquadAiRules
{
    /// <summary>How far a squad mate's backpack is reached for food with <see cref="SquadAi.Share"/> (<b>Verified</b>: squared distance 40000, <c>FUN_1405d29c0</c>).</summary>
    public const float ShareReach = 200;

    /// <summary>
    /// Whether a player turret gunner with <see cref="SquadAi.ShootFirst"/> may take a sensed non-enemy of a faction whose fundamental type is <paramref name="fundamentalType"/> and
    /// whose own default relation is <paramref name="defaultRelation"/> (<c>FUN_1404374d0</c>, asked only when no enemy is in its sights): type 0 or 8, or a default below -30.
    /// Such a target is scored at range factor <see cref="TurretFormulas.ShootFirstRange"/> (CombatSystem.Turrets.cs).
    /// </summary>
    public static bool ShootsFirstAt(SquadAi options, int fundamentalType, float defaultRelation) =>
        (options & SquadAi.ShootFirst) != 0 && (fundamentalType is 0 or 8 || defaultRelation < -30);
}

/// <summary>What the player knows of a town (the GAMESTATE_TOWN bools <c>discovered</c>, <c>explored</c>, <c>recently discovered</c>; docs/game/ui-screens.md "Map").</summary>
[Flags]
public enum TownKnowledge : byte
{
    None = 0,
    /// <summary>On the map.</summary>
    Discovered = 1,
    /// <summary>Visited: the map names it and shows its faction's colour.</summary>
    Explored = 2,
    /// <summary>Found but the map not yet opened since: the map marks it as new.</summary>
    Recent = 4,
}

/// <summary>
/// A camp or nest the player knows (a wild nest, docs/game/ui-screens.md "Map"): its wild site, TOWN record, name, owner faction, whether that faction is <c>not real</c>
/// (the map calls it a nest, else a camp), where it is (null until it has loaded once) and what is known.
/// </summary>
public sealed record CampView(int Site, string TownId, string Name, string FactionId, bool Wildlife, System.Numerics.Vector3? Position, TownKnowledge Known);
