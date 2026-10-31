using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Save;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Saving;

/// <summary>
/// One character of a saved NPC platoon (docs/simulation.md "Saves"): the save's records, the character in the world while its platoon is made, and what it
/// was when the platoon last left the world (null fields: never made, the save's own state holds).
/// </summary>
public sealed class SavedMember
{
    public required SaveCharacter Source { get; init; }
    /// <summary>The character's slot in its platoon (its handle's <c>I</c>): finds it in a copy of the save.</summary>
    public int Slot => Source.Handle.I;
    /// <summary>The character while its platoon is in the world; <see cref="CharacterId.None"/> otherwise.</summary>
    public CharacterId Live { get; set; } = CharacterId.None;
    /// <summary>Its body left the world (removed after death): the next save leaves it out.</summary>
    public bool Gone { get; set; }
    /// <summary>Dead (in the save, or when its platoon left the world): never made again; a save keeps its records (with the medical state it died with).</summary>
    public bool Dead { get; set; }

    /// <summary>Where it stood and which way it faced when its platoon left the world (null: the save's place).</summary>
    public Vector3? Position { get; set; }
    public float Yaw { get; set; }
    public CharacterStats? Stats { get; set; }
    public MedicalState? Medical { get; set; }
    public Inventory? Inventory { get; set; }
    public CharacterAppearance? Look { get; set; }
    public Crime.BountyLedger? Bounties { get; set; }
}

/// <summary>
/// An NPC platoon of a loaded save that the world keeps as itself (docs/simulation.md "Saves"): a <see cref="Platoon"/> stand-in until a zone near it is active, then
/// its saved characters (not ones made again from the template), and back to a stand-in that remembers them. <see cref="Pinned"/> platoons are their town's own
/// (residents, bar squads, slaves, farm animals): they never travel, and a town holding any makes no squads of its own.
/// </summary>
public sealed class SavedPlatoon
{
    public required SavePlatoon Source { get; init; }
    public string Name => Source.Name;
    public required bool Pinned { get; init; }
    /// <summary>The world's stand-in (-1 once it is gone: its squad was wiped out).</summary>
    public int PlatoonId { get; set; } = -1;
    public List<SavedMember> Members { get; } = [];
    /// <summary>The platoon position the members' stored positions belong to: a stand-in that travelled since takes them along.</summary>
    public Vector2 Anchor { get; set; }
    /// <summary>The tick its characters last left the world (-1: never in it).</summary>
    public long StoredTick { get; set; } = -1;
    /// <summary>Despawned off screen, as the original does with a platoon it does not keep (the Faithful <see cref="PopulationSystem.KeepRoamers"/>): a save leaves it out.</summary>
    public bool Despawned { get; set; }
    /// <summary>Its characters were in the world at least once: a capture writes them.</summary>
    public bool Touched { get; set; }
    /// <summary>A wild platoon (docs/formats/save.md "Wild and nest platoons"): a nest's (<see cref="Nest"/> set) or a homeless one (a sector's); null for a town's.</summary>
    public WildKind? Wild { get; init; }
    /// <summary>The save's nest its <c>hometown</c> names (a nest platoon).</summary>
    public SavedNest? Nest { get; init; }
    /// <summary>The population's wild site it counts against (set when the wild layout is made; -1: none, e.g. a world without biome data).</summary>
    public int WildSite { get; set; } = -1;
    /// <summary>Its squad's shop when its characters last left the world (<see cref="ShopSave"/>); null: none, or never in the world (the save's is read when it is made).</summary>
    public Trading.Shop? Shop { get; set; }
    /// <summary>Its contract and <c>change AI</c>: the save's until its squad first leaves the world, then the squad's.</summary>
    public AiSave? Ai { get => aiSet ? ai : AiSave.Read(Source.Record); set { ai = value; aiSet = true; } }
    AiSave? ai;
    bool aiSet;
    /// <summary>What its squad remembered when it last left the world (<see cref="MemorySet"/>; else the record's keys are the memory, read when its squad is made).</summary>
    public Dialogue.SquadMemoryData? Memory { get; set; }
    public bool MemorySet { get; set; }
}

/// <summary>
/// A nest of the save: an entry of <c>quick.save</c>'s GAMESTATE_TOWN_INSTANCE_LIST (id, TOWN record, position) and its <c>Nest state</c> GAMESTATE_TOWN, whose type-85
/// <c>hand</c> is what nest platoons name as <c>hometown</c> (docs/formats/save.md "Nests").
/// </summary>
public sealed record SavedNest(string Instance, string TownId, Vector3 Position, Hand Handle, FcsRecord State);

/// <summary>What a world loaded from a save needs to make the save's NPC platoons later (<see cref="World.Saved"/>), and the platoons themselves.</summary>
public sealed class SavedPopulation
{
    public required SaveHandles Handles { get; init; }
    public required IReadOnlyDictionary<FcsRecord, string> InstanceOf { get; init; }
    /// <summary>The instance ids of <see cref="InstanceOf"/>, as a set (made on first use).</summary>
    public IReadOnlySet<string> InstanceIds => instanceIds ??= new(InstanceOf.Values, StringComparer.Ordinal);
    HashSet<string>? instanceIds;
    public IAppearanceSource? Appearances { get; init; }
    /// <summary>The save's clock, which is world tick 0 (bounty timers are read against it).</summary>
    public SaveClock Clock { get; init; }
    public int PlayerFaction { get; init; } = -1;
    /// <summary>The squads' memories (the dialogue system's; null: none), which the platoons' <c>tags</c> keys are restored into when their squads are made.</summary>
    public Dialogue.SquadMemory? Memory { get; set; }
    /// <summary>The characters made from the save by their handle's container and slot, for the memory keys that name them.</summary>
    public Dictionary<(int C, int I), CharacterId> Spawned { get; } = [];
    /// <summary>The character a handle of the save names, when it is in the world (null otherwise).</summary>
    public CharacterId? CharacterOf(Hand h) => !h.IsNull && Spawned.TryGetValue((h.C, h.I), out var id) ? id : null;
    public List<SavedPlatoon> Platoons { get; } = [];
    /// <summary>The save's nests in list order: the wild layout takes them before rolling more (empty: none, e.g. a Meitou save from before nests were written).</summary>
    public List<SavedNest> Nests { get; } = [];
    /// <summary>The save's town states (not nests) by their placement's instance id: the towns' pools come from them (<c>pop</c>, <c>popd</c>, <c>started</c>, ...).</summary>
    public Dictionary<string, FcsRecord> TownStates { get; } = new(StringComparer.Ordinal);
    /// <summary>Platoon names to their entry.</summary>
    public Dictionary<string, SavedPlatoon> ByName { get; } = new(StringComparer.Ordinal);
    /// <summary>The save's WAR_SAVESTATEs by FACTION id: the war managers read them at the first look (<see cref="WarSave"/>).</summary>
    public Dictionary<string, FcsRecord> Wars { get; } = new(StringComparer.Ordinal);
    /// <summary>The save's <c>prosperity</c> of each faction by FACTION id: the population starts the faction's there (<see cref="PopulationSystem"/>), else at its cap.</summary>
    public Dictionary<string, float> Prosperity { get; } = new(StringComparer.Ordinal);
    /// <summary>Town handles (container and serial) to the placed town they name, for the war states' targets and homes.</summary>
    public Dictionary<(int C, int CS), int> TownSites { get; } = [];
    /// <summary>Platoon handles (container and serial) to the world's platoon made for them, for the war states' <c>p&lt;i&gt;</c> and <c>leader c&lt;i&gt;</c>.</summary>
    public Dictionary<(int C, int CS), int> PlatoonIds { get; } = [];
    /// <summary>
    /// Items made from the save whose <c>ownedby</c> names a character of another faction than their holder's (docs/game/economy.md "Fencing, stealing and smuggling"): stolen from that
    /// faction at once, and from the character itself once it is in the world (<see cref="ResolveOwners"/>).
    /// </summary>
    readonly List<(ItemInstance Item, Hand Owner, StolenMark Interim)> owners = [];

    /// <summary>Marks the items of a character just made whose saved <c>ownedby</c> names a character of the save of another faction than <paramref name="holderFaction"/>.</summary>
    internal void NoteOwners(Inventory inventory, int holderFaction, PopulationData data)
    {
        foreach (var item in inventory.All())
        {
            if (item.Saved is not { } r || Hand.Read(r, "ownedby") is not { Type: (int)FcsRecordType.CHARACTER } h || Handles.Character(h) is not { } owner) continue;
            int faction = data.FactionIndex(owner.Platoon.FactionId);
            if (faction < 0 || faction == holderFaction) continue;
            item.Mark = StolenMark.OfFaction(faction);
            lock (owners) owners.Add((item, h, item.Mark));
        }
    }

    /// <summary>Gives each noted item whose owner is now in the world the whole mark (the owner and its squad); one whose mark changed since is dropped from the list.</summary>
    internal void ResolveOwners(World world)
    {
        lock (owners)
            owners.RemoveAll(o =>
            {
                if (o.Item.Mark != o.Interim) return true;
                if (CharacterOf(o.Owner) is not { } id || !world.Characters.TryResolveNext(id, out int slot) || world.Characters.Cold(slot) is not { } cold) return false;
                (o.Item.Mark, o.Item.OwnerFromSave) = (StolenMark.Of(cold, id), true);
                return true;
            });
    }

    /// <summary>Lines for the host's log (each platoon made, characters left out); taken by <see cref="TakeLog"/>.</summary>
    readonly List<string> log = [];
    internal void Log(string line) { lock (log) log.Add(line); }
    public List<string> TakeLog()
    {
        lock (log)
        {
            var list = new List<string>(log);
            log.Clear();
            return list;
        }
    }
}

/// <summary>What a saved character's records give, read without touching the world (the population's build thread does it for NPC platoons).</summary>
internal sealed record SavedParts(SaveCharacter Source, GameRecord Record, RaceData? Race, CharacterStats? Stats, MedicalState? Medical, IReadOnlyList<SaveItem> Items,
    Inventory? Inventory, CharacterAppearance? Look);

/// <summary>Making characters from a save: the player's platoons at load (<see cref="SaveLoader"/>) and NPC platoons when their zone loads (<see cref="PopulationSystem"/>).</summary>
internal static class SavedCharacters
{
    /// <summary>Reads a character's race, stats, medical state, items and look. Null (with a note) when the game data lacks its record.</summary>
    public static SavedParts? Read(SaveCharacter sc, string platoonName, PopulationData data, IAppearanceSource? appearances,
        Dictionary<string, RaceData?> races, Dictionary<string, BodyPartTemplate?> parts, Action<string>? note)
    {
        var record = data.Db.Find(sc.RecordId);
        if (record is null)
        {
            note?.Invoke($"{platoonName}: character {sc.Name} is made from {sc.RecordId}, which the game data does not have; left out.");
            return null;
        }
        RaceData? race = RaceOf(sc, record, data.Db, races);
        CharacterStats? stats = sc.Stats is null ? null : CharacterStats.ReadSave(sc.Stats.Floats);
        MedicalState? medical = null;
        if (race is not null && sc.Medical is not null)
        {
            medical = MedicalState.ReadSave(race, sc.Medical.Floats, sc.Medical.Bools, sc.Medical.Strings, id => PartOf(id, data.Db, parts));
            SavedLimbs.Apply(medical, sc.Platoon.File, sc.Medical, data.Items);
        }
        // The kit: a character whose INVENTORY_STATE holds items gets them, and its look is its saved appearance wearing them. One with none (a Meitou save from before
        // inventories were written) keeps the old rule for its clothes: rolled from the handle's serial, with the inventory a new character of the look would have.
        var savedItems = sc.Inventory is { } invState && sc.Platoon.File is { } platoonFile ? SaveInventory.Read(platoonFile, invState) : [];
        var handle = sc.Handle;
        string ownerId = sc.OwnerFactionId;
        CharacterAppearance? look = null;
        int serial = handle.S & 0x7FFFFFFF;
        if (savedItems.Count > 0 && appearances is { } source && SavedKit.LoadoutOf(sc.Appearance, savedItems, data.Db, serial) is { } kit)
        {
            SavedKit.SetLimbs(kit, medical, data.Db);
            look = source.Create(sc.RecordId, kit);
        }
        look ??= appearances?.Create(sc.RecordId, ownerId.Length > 0 ? ownerId : null, serial);
        // Without items the clothes are rolled, but the body and face are still the saved ones (the CHARACTER_APPEARANCE holds the whole look).
        if (savedItems.Count == 0 && appearances is { } again && look?.Loadout is { } rolled)
        {
            var worn = SavedKit.Wear(rolled, sc.Appearance, data.Db);
            if (worn is null && medical is { LimbBits: not 0 } or { Decapitated: true }) worn = SavedKit.Copy(rolled);
            if (worn is not null)
            {
                SavedKit.SetLimbs(worn, medical, data.Db);
                look = again.Create(sc.RecordId, worn) ?? look;
            }
        }
        return new SavedParts(sc, record, race, stats, medical, savedItems, null, look);
    }

    /// <summary>A member made again after its platoon left the world: what it was then, the race and record from the data.</summary>
    public static SavedParts? Again(SavedMember m, PopulationData data, Dictionary<string, RaceData?> races)
    {
        var sc = m.Source;
        if (data.Db.Find(sc.RecordId) is not { } record) return null;
        return new SavedParts(sc, record, RaceOf(sc, record, data.Db, races), m.Stats, m.Medical, [], m.Inventory, m.Look);
    }

    /// <summary>
    /// Makes the character in the world, in <paramref name="squad"/>, at <paramref name="position"/> facing <paramref name="yaw"/>: its name, faction (the save's owner,
    /// else <paramref name="fallbackFaction"/>), body (the saved stats and medical state; the inventory and fighter from its items), bounties, crime marks, the player's
    /// orders for a player character, and its bed or cage. The caller adds it to the squad.
    /// </summary>
    public static CharacterId Make(World world, SavedParts p, string platoonName, Squad squad, int fallbackFaction, int playerFaction, bool playerPlatoon, Vector3 position, float yaw,
        CharacterTask task, byte mode, PopulationData data, CharacterAssembly assembly, SavedPopulation context, Crime.BountyLedger? bounties, List<string>? notes)
    {
        var sc = p.Source;
        var race = p.Race;
        var stats = p.Stats;
        var medical = p.Medical;
        float athletics = stats?.Athletics ?? 20;
        float speed = race is null ? 45 : medical is not null && stats is not null ? Speed.Run(race, stats, medical, 1) : Speed.RunUnhurt(race, athletics);
        var hot = new CharacterHot
        {
            Position = position,
            Yaw = yaw,
            Health = 100,
            Task = (byte)task,
            Mode = mode,
            MaxSpeed = speed,
            WalkSpeed = race?.WalkSpeed ?? 15,
        };
        string ownerId = sc.OwnerFactionId;
        int faction = ownerId.Length > 0 && data.FactionIndex(ownerId) is var fi and >= 0 ? fi : fallbackFaction;
        bool player = faction == playerFaction && (playerPlatoon || playerFaction >= 0);
        var cold = new CharacterCold
        {
            Name = sc.Name.Length > 0 ? sc.Name : p.Record.Name,
            Faction = faction,
            Appearance = p.Look,
            RecordId = sc.RecordId,
            SquadId = squad.Id,
            Role = sc.SquadMemberType,
            IsPlayer = player,
            Orders = player ? Orders.PlayerDefaults : CharacterOrders.None,
            Replan = player,   // a load: every player character is decided again
            FootprintRadius = race?.PathfindFootprintRadius ?? 0,
            Save = new SavedCharacterLink
            {
                Source = sc,
                PlatoonName = platoonName,
                Slot = sc.Handle.I,
                Rotation = sc.Rotation,
                Yaw = SaveRotation.YawOf(sc.Rotation),
                Stats = stats,
                Medical = medical,
                Ragdoll = RagdollSave.Read(sc.GameState),
            },
        };
        // Bounties: the ledger keys of the character's state record (docs/simulation.md "Crimes and bounties"), or the one it had when its platoon left the world.
        cold.Bounties = bounties ?? BountySave.Read(sc.GameState, data.Relations, context.Clock, 0, world.TickSeconds);
        CrimeSave.Read(sc.GameState, cold);
        SlaveSave.Read(sc.GameState, cold, data);
        DialogueSave.Read(sc.GameState, cold);
        if (cold.IsPlayer && sc.GameState is { } orders) Orders.Read(orders, cold);
        // The loader turns the saved toggles back on through setStandingOrder, and Hold's sets the hold point to where the character stands.
        if ((cold.Orders & CharacterOrders.Hold) != 0) cold.HoldPoint = new System.Numerics.Vector2(hot.Position.X, hot.Position.Z);
        // In a bed or a cage, and the sentence (docs/simulation.md "Cages, beds and locks"); the fixture binds when its town registers.
        HeldSave.Read(sc.GameState, cold, context.Handles, context.InstanceOf);
        if (cold.InSomething == Locks.HeldState.Bed) hot.Task = (byte)CharacterTask.Sleep;   // it lies there until an order (Locks.BedSystem)
        if (cold.IsPlayer && sc.Ai is { } ai) PermaJobSave.Read(ai, sc.GameState, cold, world.Buildings, context.Handles, context.InstanceOf, context.InstanceIds);
        if (cold.IsPlayer && sc.Ai is { } queueAi) OrderSave.Read(queueAi, cold, context.Handles, context.InstanceOf);
        // The portrait camera's number: the saved one, else the handle's serial (CharacterAnimal::vfunc_18).
        cold.PortraitSerial = sc.GameState is { } portrait && portrait.Ints.TryGetValue("portrait_serial", out int serial) ? (uint)serial : (uint)sc.Handle.S;
        cold.Save.InSomething = cold.InSomething;
        cold.Save.InWhat = cold.InWhat;
        // A carry under way (FUN_140627b30): linked once both are in the world (CarrySystem.RestoreLinks).
        if (sc.GameState is { } carry)
        {
            cold.Save.CarryPartner = Hand.Read(carry, "carrying");
            cold.Save.CarryIsCarrier = carry.Bools.GetValueOrDefault("carrying");
        }
        // The body: the saved stats and medical state go to the body systems, the rest of the assembly (inventory, fighter) is made as for a new character.
        if (race is not null && stats is not null && medical is not null)
            assembly.AttachSaved(cold, race, stats, medical, p.Record, p.Inventory ?? (p.Items.Count > 0 ? SavedKit.ToInventory(p.Items, data.Items, notes, p.Record) : null),
                sc.GameState is { } aged && aged.Floats.TryGetValue("age", out float age) ? age : 1);
        else if (race is not null)
            notes?.Add($"{platoonName}: {cold.Name} has no stats or medical state that could be read; it has no body in the world.");
        // What it holds that was stolen (the save's `ownedby`).
        if (cold.Inventory is { } carried) context.NoteOwners(carried, faction, data);
        return world.Characters.Spawn(hot, cold, world.Tick);
    }

    static RaceData? RaceOf(SaveCharacter sc, GameRecord record, GameDatabase db, Dictionary<string, RaceData?> cache)
    {
        string? id = sc.Appearance is { } a && a.References.TryGetValue("race", out var list) && list.Count > 0 ? list[0].TargetStringId : null;
        id ??= CharacterAssembly.RaceId(record);
        return id is null ? null : CharacterAssembly.RaceDataOf(db, id, cache);
    }

    static BodyPartTemplate? PartOf(string id, GameDatabase db, Dictionary<string, BodyPartTemplate?> cache)
    {
        if (cache.TryGetValue(id, out var part)) return part;
        part = db.Find(id) is { } rec ? BodyPartTemplate.From(rec) : null;
        cache[id] = part;
        return part;
    }

    // ---- NPC platoons ----

    /// <summary>
    /// The building of <paramref name="site"/> a saved platoon lives in: its <c>homebuilding</c> handle resolved to the zone's building state (<c>&lt;instance&gt;-S23</c>),
    /// matched by zone and instance id, or (a zone the save has no file of) to the building whose level-data state has that handle (docs/formats/save.md "NPC platoons").
    /// Null when the platoon names none or it is not one of the town's buildings.
    /// </summary>
    public static Meitou.Data.World.TownBuilding? SavedHome(World world, SavedPlatoon sp, TownSite site)
    {
        if (world.Saved is not { } context || site.Buildings.Count == 0) return null;
        var h = Hand.Read(sp.Source.Record, "homebuilding");
        if (h.IsNull || h.Type != (int)FcsRecordType.BUILDING) return null;
        int c = h.C - 1;
        var zone = new Meitou.Data.World.ZoneCoordinate(c % Meitou.Data.World.WorldLayout.ZoneCount, c / Meitou.Data.World.WorldLayout.ZoneCount);
        if (context.Handles.Building(h) is { } state)
        {
            if (!state.StringId.EndsWith("-S23", StringComparison.Ordinal)) return null;
            string instance = state.StringId[..^4];
            foreach (var b in site.Buildings)
                if (b.Zone == zone && b.InstanceId == instance) return b;
            return null;
        }
        foreach (var b in site.Buildings)
            if (b.Zone == zone && b.HandleSlot == h.I && b.HandleSerial == h.S) return b;
        return null;
    }

    /// <summary>What making a saved platoon needs from the records, read on any thread: each member to make with its parts (members gone or dead are left out).</summary>
    public static List<(SavedMember Member, SavedParts Parts)> Prepare(SavedPlatoon sp, PopulationData data, IAppearanceSource? appearances, SavedPopulation context)
    {
        var races = new Dictionary<string, RaceData?>(StringComparer.Ordinal);
        var parts = new Dictionary<string, BodyPartTemplate?>(StringComparer.Ordinal);
        var list = new List<(SavedMember, SavedParts)>();
        foreach (var m in sp.Members)
        {
            if (m.Gone || m.Dead) continue;   // a corpse made once is not made again (it lies in the world, or went with its zone)
            var p = m.Position is null ? Read(m.Source, sp.Name, data, appearances, races, parts, context.Log) : Again(m, data, races);
            if (p is null) continue;
            list.Add((m, p));
        }
        return list;
    }

    /// <summary>
    /// The saved platoon's characters into the world as the squad of stand-in <paramref name="platoon"/> (serial phase): each where it stood (moved along with a
    /// stand-in that travelled), its saved self; the leader wanders (a town squad, until its AI takes it) or waits for the roaming orders, the rest follow it.
    /// A member dead in the save is marked and left out. Returns the squad (null when no one could be made).
    /// </summary>
    public static Squad? Spawn(World world, Platoon platoon, SavedPlatoon sp, List<(SavedMember Member, SavedParts Parts)> built, PopulationData data, CharacterAssembly assembly,
        Vector2 homeCentre, float homeRadius)
    {
        var context = world.Saved!;
        var walk = world.Walkability;
        var delta = platoon.Position - sp.Anchor;
        bool moved = delta.LengthSquared() > 1;
        string name = data.Db.Find(platoon.TemplateId)?.Name ?? sp.Name;
        var squad = new Squad
        {
            Id = world.Squads.NextId(),
            TemplateId = platoon.TemplateId,
            Name = name,
            Faction = platoon.Faction,
            Town = platoon.Origin,
            PlatoonId = platoon.Id,
            Pinned = sp.Pinned,
            Wild = sp.Wild is not null,
            Restored = true,
            LeftPackage = platoon.Package,
            Position = new Vector3(platoon.Position.X, walk.GroundHeight(platoon.Position.X, platoon.Position.Y), platoon.Position.Y),
            HomeCentre = homeCentre,
            HomeRadius = homeRadius,
            HomeBuilding = sp.Pinned && platoon.Origin >= 0 ? SavedHome(world, sp, data.Sites[platoon.Origin]) : null,
        };
        var notes = new List<string>();
        var made = new List<(SavedMember Member, int Role)>();
        bool hasLeader = built.Any(b => b.Parts.Source.IsLeader && b.Parts.Medical is not { Dead: true });
        // Without one the first living member that is not an animal leads (SquadRegistry.Successor).
        var alive = built.Where(b => b.Parts.Medical is not { Dead: true }).ToList();
        var successor = hasLeader ? null : alive.FirstOrDefault(b => b.Parts.Look?.Animal is null).Member ?? alive.FirstOrDefault().Member;
        foreach (var (m, p) in built)
        {
            // A member dead in the save lies where it fell, as the original makes the whole list of a platoon (FUN_1404ff410 writes the dead with it); its 12 hours start on its first update.
            bool corpse = p.Medical is { Dead: true };
            var at = m.Position ?? p.Source.Position;
            if (moved)
            {
                float x = at.X + delta.X, z = at.Z + delta.Y;
                if (!walk.IsWalkable(x, z)) (x, z) = WalkableSpots.Nearest(walk, new Vector2(x, z), out var near, 32) ? (near.X, near.Y) : (platoon.Position.X, platoon.Position.Y);
                at = new Vector3(x, walk.GroundHeight(x, z), z);
            }
            float yaw = m.Position is null ? SaveRotation.YawOf(p.Source.Rotation) : m.Yaw;
            bool leads = !corpse && (p.Source.IsLeader || ReferenceEquals(m, successor));
            var task = corpse ? CharacterTask.Idle : leads ? (sp.Pinned || sp.Wild is not null ? CharacterTask.Wander : CharacterTask.Idle) : CharacterTask.Follow;
            var id = Make(world, p, sp.Name, squad, platoon.Faction, context.PlayerFaction, false, at, yaw, task, SpeedMode.Walk, data, assembly, context, m.Bounties, notes);
            // Made again after its platoon left the world: the save's ragdoll is from before that.
            if (m.Position is not null && world.Characters.Cold(id.Slot)?.Save is { } link) link.Ragdoll = null;
            ref var hot = ref world.Characters.Next[id.Slot];
            hot.TaskTime = 0.5f + Rng.Float(Rng.Hash(world.Seed, (ulong)squad.Id << 8 | (uint)(m.Slot & 0xFF), RngPurpose.Think)) * 3;
            m.Live = id;
            context.Spawned[(m.Source.Handle.C, m.Source.Handle.I)] = id;
            if (UniqueCharacters.IsUnique(data.Db.Find(p.Source.RecordId))) world.Uniques.Bind(p.Source.RecordId, id);
            squad.Members.Add(id);
            if (leads && squad.Leader.IsNone) squad.Leader = id;
            if (corpse) continue;
            made.Add((m, p.Source.SquadMemberType));
        }
        foreach (var n in notes) context.Log(n);
        if (made.Count == 0)
        {
            // Only corpses: they lie there without a squad.
            foreach (var id in squad.Members) if (world.Characters.Cold(id.Slot) is { } lying) lying.SquadId = -1;
            return null;
        }
        if (squad.Leader.IsNone) squad.Leader = SquadRegistry.Successor(squad.Members, world.Characters);
        // Its shop as it was when it left the world, else the save's (a fresh fill when the save has none).
        squad.Shop = sp.Shop ?? ShopSave.Read(world, squad, sp.Source.Record, data);
        squad.SavedAi = sp.Ai;
        world.Squads.Add(squad);
        // What the squad remembered: the save's, else what it kept when it last left the world.
        if (context.Memory is { } memory)
        {
            memory.Restore(squad.Id, sp.MemorySet ? sp.Memory : MemorySave.Read(sp.Source.Record, context.CharacterOf));
            (sp.Memory, sp.MemorySet) = (null, false);
        }
        sp.Touched = true;
        context.ResolveOwners(world);
        context.Log($"{sp.Name} ({name}, {(sp.Pinned ? "town" : sp.Wild is { } wild ? (wild == WildKind.Nest ? "nest" : "homeless") : "roaming")}): {squad.Members.Count} saved character(s) at {world.Characters.Next[squad.Leader.Slot].Position.X:0}, {world.Characters.Next[squad.Leader.Slot].Position.Z:0}");
        return squad;
    }

    /// <summary>The platoon leaves the world (serial phase): each member's place, body, kit and bounties are kept for the next time; one whose body is gone is marked.</summary>
    public static void Store(World world, SavedPlatoon sp, Vector2 anchor)
    {
        var table = world.Characters;
        if (sp.PlatoonId >= 0 && world.Platoons.Find(sp.PlatoonId) is { } platoon && world.Squads.Find(platoon.SquadId) is { } squad) { sp.Shop = squad.Shop; sp.Ai = AiSave.Of(squad.Ai) ?? squad.SavedAi; if (world.Saved?.Memory is { } memory) (sp.Memory, sp.MemorySet) = (memory.Take(squad.Id), true); }
        foreach (var m in sp.Members)
        {
            if (m.Live.IsNone) continue;
            if (!table.TryResolveNext(m.Live, out int slot) || table.Cold(slot) is not { } cold)
            {
                m.Gone = true;
                m.Live = CharacterId.None;
                continue;
            }
            m.Position = table.Previous[slot].Position;
            m.Yaw = table.Previous[slot].Yaw;
            m.Stats = cold.Stats;
            m.Medical = cold.Medical;
            m.Inventory = cold.Inventory;
            m.Look = cold.Appearance;
            m.Bounties = cold.Bounties;
            if (cold.DiedTick >= 0) m.Dead = true;
            m.Live = CharacterId.None;
        }
        sp.Anchor = anchor;
        sp.StoredTick = world.Tick;
    }
}
