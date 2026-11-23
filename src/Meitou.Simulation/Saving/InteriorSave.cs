using System.Globalization;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Saving;

/// <summary>
/// A town house's interior in a save as the original keeps it (docs/formats/save.md "Zone files", serialised interiors): each object its layouts
/// place is an entry of its own in the zone's building list (id <c>&lt;n&gt;-zone&lt;X&gt;.&lt;Y&gt;-INGAME</c>) with a GAMESTATE_BUILDING and a
/// handle, each loose-item group an entry with an ITEM_PLACEMENT_GROUP, and the house's state has <c>serialised interior</c> 1 and names the
/// furniture by <c>internalbuildings-&lt;k&gt;</c>. A zone file Meitou made has none of it (the original makes the interior again from the layout
/// names), so nothing there can name a cage: a house with a prisoner in one of its cages is written whole, and the prisoner's <c>in what</c> names
/// the cage's entry (<see cref="HeldSave"/>). The machine and furniture-inventory writers after it fill the new entries as they do the original's.
/// </summary>
public sealed class InteriorSave
{
    #region The capture

    /// <summary>
    /// The suffix of an entry's GAMESTATE_BUILDING id (the hex of the record type, 35).
    /// </summary>
    const string StateSuffix = "-S23";

    /// <summary>
    /// The serial of every zone container (docs/formats/save.md "Object handles").
    /// </summary>
    const int ContainerSerial = 11111;

    /// <summary>
    /// The start of a house state's handles of the furniture inside it.
    /// </summary>
    const string InternalPrefix = "internalbuildings-";

    /// <summary>
    /// The entry id each layout object or item slot was first written with, by its id: a later capture of the session, made again from the
    /// loaded save, writes the same ids. Runtime only: a reloaded save holds the entries.
    /// </summary>
    readonly Dictionary<string, string> _entryIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Writes the interior of each house that holds a prisoner in one of its layout's cages and has none saved, then adds each such cage's entry
    /// state to <paramref name="buildings"/> under the cage's fixture id, its lock as it is now, and names it on the prisoners whose records the
    /// capture leaves as read. After the buildings' own states, before the machines' and the furniture inventories'.
    /// </summary>
    public void Write(SaveGame game, World world, PopulationData data, Dictionary<string, FcsRecord> buildings, ulong seed)
    {
        var waiting = PassedThrough(game, world);
        var cages = Cages(data, OccupiedCages(world, waiting));
        if (cages.Count == 0) return;

        string? player = data.PlayerFaction >= 0 ? data.Factions[data.PlayerFaction].Id : null;
        foreach (var b in cages.Select(c => c.House).Distinct())
            if (buildings.TryGetValue(b.Key, out var house) && CanFurnish(world, b, house, player)) Furnish(game, world, data.Db, b, house, seed);

        var saved = new SavedPieces(game);
        foreach (var (house, cage) in cages) NameCage(world, house, cage, saved, buildings);
        foreach (var prisoner in waiting) NamePrisoner(prisoner, buildings);
    }

    /// <summary>
    /// The states of the prisoners in the platoons the capture leaves as the loaded save has them (not made since, <see cref="SavedPlatoon.Touched"/>)
    /// whose cage only Meitou's keys name.
    /// </summary>
    static List<FcsRecord> PassedThrough(SaveGame game, World world)
    {
        if (world.Saved is not { } saved) return [];

        var untouched = saved.Platoons
            .Where(p => !p.Touched)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        return [.. game.Platoons
            .Where(p => untouched.Contains(p.Name))
            .SelectMany(p => p.Characters)
            .Select(c => c.GameState)
            .OfType<FcsRecord>()
            .Where(HeldUnnamed)];
    }

    /// <summary>
    /// Whether a character's state holds it imprisoned by Meitou's keys alone (the original's say free).
    /// </summary>
    static bool HeldUnnamed(FcsRecord state) =>
        state.Ints.GetValueOrDefault(HeldSave.MeitouState) == (int)HeldState.Imprisoned
        && state.Ints.GetValueOrDefault("in something") == 0
        && state.Strings.GetValueOrDefault(HeldSave.MeitouWhat) is { Length: > 0 };

    /// <summary>
    /// The ids of the layout cages that hold somebody: the world's prisoners' and the <paramref name="waiting"/> ones'.
    /// </summary>
    static HashSet<string> OccupiedCages(World world, List<FcsRecord> waiting)
    {
        var cages = waiting.Select(s => s.Strings[HeldSave.MeitouWhat]).ToHashSet(StringComparer.Ordinal);
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (table.Cold(i) is not { InSomething: HeldState.Imprisoned, InWhat: { } id }) continue;
            if (world.Fixtures.Find(id) is { Kind: FixtureKind.Cage } f && f.Id != f.Building) cages.Add(f.Id);
        }

        return cages;
    }

    /// <summary>
    /// The layout object of each cage id with the town building whose layout places it (the id is the building's key, "/" and the object's place).
    /// </summary>
    static List<(TownBuilding House, TownLayoutBuilding Cage)> Cages(PopulationData data, HashSet<string> ids)
    {
        var keys = ids
            .Where(id => id.Contains('/'))
            .Select(id => id[..id.LastIndexOf('/')])
            .ToHashSet(StringComparer.Ordinal);
        var cages = new List<(TownBuilding, TownLayoutBuilding)>();
        foreach (var b in data.Sites.SelectMany(s => s.Buildings).Where(b => keys.Contains(b.Key)))
            cages.AddRange(b.LayoutBuildings.Where(p => ids.Contains(p.Id)).Select(p => (b, p)));

        return cages;
    }

    /// <summary>
    /// Whether a house's interior is written now: it stands, the save holds none for it, it was neither bought (its furniture is gone) nor
    /// furnished anew by an override (<see cref="RuinSave"/> has the original make that one from its names), its state has a handle to name, and
    /// it is not the player's (a load takes a generated entry the player's faction owns for a building the player placed,
    /// <see cref="BuildingSave"/>).
    /// </summary>
    static bool CanFurnish(World world, TownBuilding b, FcsRecord house, string? playerFactionId) =>
        !FurnitureInventorySave.Keeps(house.Ints)
        && !house.Bools.GetValueOrDefault("destroyed")
        && !world.Fixtures.IsEmptied(b.Key)
        && !world.Fixtures.IsRebuilt(b.Key)
        && Hand.IsComplete(house, "handle")
        && house.Strings.GetValueOrDefault("owner faction ID") != playerFactionId;

    /// <summary>
    /// A house being written: the world, its zone file and building list, its state, what its entries take from it, and the ids the list holds.
    /// </summary>
    sealed record Site(World World, SaveZone Zone, FcsRecord List, FcsRecord House, InteriorHouse Inside, HashSet<string> Taken);

    /// <summary>
    /// Writes a house's furniture and item groups as entries of its zone's building list and marks its state as holding them: each furniture
    /// entry takes the next slot after the zone's last and is named by the house's next <c>internalbuildings-&lt;k&gt;</c>.
    /// </summary>
    void Furnish(SaveGame game, World world, GameDatabase db, TownBuilding b, FcsRecord house, ulong seed)
    {
        if (!game.Zones.TryGetValue(b.Zone, out var zone) || zone.BuildingList is not { } list) return;

        var taken = list.Instances.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var site = new Site(world, zone, list, house, InteriorHouse.Of(house, b), taken);
        int first = LastSlot(zone) + 1;
        int slot = first - 1;
        int k = InternalCount(house);
        foreach (var piece in b.LayoutBuildings)
        {
            if (db.Find(piece.RecordId) is not { Type: FcsRecordType.Building } record || record.GetBool("is node")) continue;

            var hand = AddFurniture(site, db, record, piece, ++slot, seed);
            hand.Write(house, InternalPrefix + (k++).ToString(CultureInfo.InvariantCulture));
        }

        foreach (var itemSlot in b.ItemSlots) AddGroup(site, itemSlot);

        ListSlots(zone, first, slot);
        house.Ints[FurnitureInventorySave.SerialisedInterior] = 1;
    }

    /// <summary>
    /// Writes one layout object's entry with its state, its lock as it is now, under a new handle of slot <paramref name="slot"/>; returns the
    /// handle.
    /// </summary>
    Hand AddFurniture(Site site, GameDatabase db, GameRecord record, TownLayoutBuilding piece, int slot, ulong seed)
    {
        string id = EntryId(site.World, site.Zone.Coordinate, piece.Id, site.Taken);
        var hand = new Hand((int)FcsRecordType.Building, site.Zone.ContainerId, ContainerSerial, slot, (int)Rng.Mix(Rng.StableHash(id) ^ seed));
        var state = EntryState(db, record, piece, site.Inside);
        state.Id = SaveBuilder.NextNumber(site.Zone.File);
        state.StringId = id + StateSuffix;
        hand.Write(state, "handle");
        WriteLock(state, LockNow(site.World, site.House, piece.Id));
        Add(site, new FcsInstance { Id = id, Target = piece.RecordId, Position = piece.Position, Rotation = piece.Rotation }, state);
        return hand;
    }

    /// <summary>
    /// Writes one loose-item slot's group entry; its <c>respawn time</c> is the hour the empty slot is due (0 while it holds its item).
    /// </summary>
    void AddGroup(Site site, TownItemSlot slot)
    {
        var zone = site.Zone;
        string id = EntryId(site.World, zone.Coordinate, slot.Id, site.Taken);
        int number = SaveBuilder.NextNumber(zone.File);
        var state = GroupState(slot, site.Inside);
        state.Id = number;
        state.StringId = BuildingRegistry.IdOf(number, zone.Coordinate);
        state.Floats["respawn time"] = site.House.Floats.GetValueOrDefault(Items.ItemSlots.DueKey + slot.Id[(slot.Id.LastIndexOf('/') + 1)..]);
        Add(site, new FcsInstance { Id = id, Target = slot.GroupId, Position = GroupPosition(site.World, slot), Rotation = slot.Rotation }, state);
    }

    /// <summary>
    /// Where a loose-item slot's group entry stands: the slot's place, its height taken above the ground. The original's group adds its zone's
    /// ground height to its own when it is made with no parent building (<c>FUN_1405534f0</c>, <c>FUN_1409b3710</c>, <b>Verified</b> by code),
    /// and its saved groups stand that much under their slots (<b>Observed</b>, METARU); Meitou takes the terrain's height, at least the water
    /// level (<b>Observed</b>: within 0.5 of the saved height for 4370 of the 5627 saved groups, within 2 for 5276; docs/formats/save.md "Zone
    /// files"). A furniture entry keeps its world height.
    /// </summary>
    public static Vector3 GroupPosition(World world, TownItemSlot slot)
    {
        var at = slot.Position;
        return at with { Y = at.Y - MathF.Max(world.Walkability.TerrainHeight(at.X, at.Z), WorldWater.Height) };
    }

    /// <summary>
    /// Lists an entry with its state record.
    /// </summary>
    static void Add(Site site, FcsInstance instance, FcsRecord state)
    {
        instance.States.Add(state.StringId);
        site.Zone.File.Records.Add(state);
        site.List.Instances.Add(instance);
    }

    /// <summary>
    /// The entry id of a layout object or item slot: the one a capture of this session gave it, else a new one of the zone, which the building
    /// registry then keeps its own new ids above (a building the player places later takes another).
    /// </summary>
    string EntryId(World world, ZoneCoordinate zone, string pieceId, HashSet<string> taken)
    {
        if (_entryIds.TryGetValue(pieceId, out var known) && taken.Add(known)) return known;

        string id;
        do
        {
            id = world.Buildings.NewId(zone);
        }
        while (!taken.Add(id));

        _entryIds[pieceId] = id;
        return id;
    }

    /// <summary>
    /// The highest slot the zone's slot list or a building handle of its file holds.
    /// </summary>
    static int LastSlot(SaveZone zone)
    {
        var file = zone.File;
        int listed = file.SlotLists.Count > 0 ? file.SlotLists[0].DefaultIfEmpty(0).Max() : 0;
        int stored = zone.BuildingStates
            .Select(r => Hand.Read(r, "handle"))
            .Where(h => !h.IsNull && h.C == zone.ContainerId)
            .Select(h => h.I)
            .DefaultIfEmpty(0)
            .Max();
        return Math.Max(listed, stored);
    }

    /// <summary>
    /// Adds the slots the new entries took, <paramref name="first"/> to <paramref name="last"/>, to the zone's slot list.
    /// </summary>
    static void ListSlots(SaveZone zone, int first, int last)
    {
        var file = zone.File;
        if (file.SlotLists.Count == 0) file.SlotLists.Add([]);

        var slots = new SortedSet<int>(file.SlotLists[0]);
        for (int i = first; i <= last; i++) slots.Add(i);

        file.SlotLists[0] = [.. slots];
    }

    /// <summary>
    /// How many <c>internalbuildings-&lt;k&gt;</c> handles a house state holds, numbered from 0 (the readers stop at the first missing number).
    /// </summary>
    static int InternalCount(FcsRecord house)
    {
        int k = 0;
        while (house.Ints.ContainsKey(InternalPrefix + k.ToString(CultureInfo.InvariantCulture) + "I")) k++;
        return k;
    }

    /// <summary>
    /// Adds an occupied cage's entry state to the building states under the cage's id, with its lock as it is now, when the save holds the
    /// cage's entry and keeps it: its house keeps its saved interior, its zone does not load fresh, and the house was neither bought nor
    /// furnished anew (the later writers remove such a house's entries, <see cref="FurnitureInventorySave"/>, <see cref="RuinSave"/>).
    /// </summary>
    static void NameCage(World world, TownBuilding b, TownLayoutBuilding cage, SavedPieces saved, Dictionary<string, FcsRecord> buildings)
    {
        if (world.Fixtures.IsEmptied(b.Key) || world.Fixtures.IsRebuilt(b.Key)) return;
        if (!buildings.TryGetValue(b.Key, out var house) || !FurnitureInventorySave.Keeps(house.Ints)) return;
        if (saved.Of(cage) is not { Fresh: false, State: { } state } || !Hand.IsComplete(state, "handle")) return;

        WriteLock(state, LockNow(world, house, cage.Id));
        buildings[cage.Id] = state;
    }

    /// <summary>
    /// Names the cage's entry on a prisoner's state that only Meitou's keys held, when the save has the entry.
    /// </summary>
    static void NamePrisoner(FcsRecord prisoner, Dictionary<string, FcsRecord> buildings)
    {
        if (!buildings.TryGetValue(prisoner.Strings[HeldSave.MeitouWhat], out var cage)) return;

        prisoner.Ints["in something"] = (int)HeldState.Imprisoned;
        Hand.Read(cage, "handle").Write(prisoner, "in what");
    }

    /// <summary>
    /// A layout object's lock as it is now: its fixture's, else (a town whose fixtures are not registered) the one Meitou's own key of the house
    /// state holds (<see cref="FixtureRegistry.InteriorLockPrefix"/>: level x 4 + broken x 2 + locked); null when neither has one.
    /// </summary>
    static (int Level, bool Locked, float? Hardness)? LockNow(World world, FcsRecord house, string pieceId)
    {
        if (world.Fixtures.Find(pieceId)?.Lock is { } l) return (l.Level, l.Locked && !l.Broken, l.Hardness);

        string key = FixtureRegistry.InteriorLockPrefix + pieceId[(pieceId.LastIndexOf('/') + 1)..];
        return house.Ints.TryGetValue(key, out int packed) ? (Math.Clamp(packed >> 2, 0, 100), (packed & 3) == 1, null) : null;
    }

    #endregion

    #region Entry states

    /// <summary>
    /// The functions of the original's production classes, which save <c>resource mult</c> and <c>production amount</c> (<b>Observed</b>, METARU:
    /// every saved interior object of a farm, refinery, generator, crafting bench or battery, 322 of 322, and of no other function).
    /// </summary>
    static readonly int[] ProductionFunctions =
    [
        MachineData.MineFunction,
        MachineData.RefineryFunction,
        MachineData.GeneratorFunction,
        TownBuildings.CraftingFunction,
        MachineData.BatteryFunction,
    ];

    /// <summary>
    /// The GAMESTATE_BUILDING of a layout object's entry, without its id and handle: the keys the original writes for every building
    /// (<c>FUN_1405547f0</c> and its caller), and by its functionality a usable object's power keys, a light's mount, a lockable one's lock and a
    /// production building's idle state. A farm's own keys are its machine's (<see cref="TownMachineSave"/>).
    /// </summary>
    public static FcsRecord EntryState(GameDatabase db, GameRecord record, TownLayoutBuilding piece, InteriorHouse house)
    {
        var state = new FcsRecord { Type = (int)FcsRecordType.GamestateBuilding, Name = "0" };
        WriteCommon(state, piece.Floor, house);
        WriteConstruction(state, record);
        WriteBase(state, record, piece, house);

        var functionality = record.GetReferences("functionality") is [var link, ..] ? db.Find(link.TargetStringId) : null;
        if (functionality is null) return state;

        int function = functionality.GetInt("function", -1);
        WritePower(state);
        if (function == WorldLights.LightFunction) Hand.Null.Write(state, "mounted");
        if (function == TownBuildings.CageFunction || record.GetBool("has lock")) WriteLock(state, record);
        if (ProductionFunctions.Contains(function)) WriteIdle(state, MachineData.From(db, record)?.Inputs.Count ?? 0, function);
        return state;
    }

    /// <summary>
    /// The keys every finished object inside a house has: open to the public (<b>Observed</b>, METARU: true in all 15088, whatever the house's
    /// own), not paused, standing, with no residents of its own.
    /// </summary>
    static void WriteBase(FcsRecord state, GameRecord record, TownLayoutBuilding piece, InteriorHouse house)
    {
        state.Ints["version"] = SaveZoneBuilder.StateVersion;

        // Bit 0: drawn while the house shows its outside (the old `is outside`).
        state.Ints["flags"] = piece.InteriorOnly ? 0 : 1;
        state.Ints[FurnitureInventorySave.SerialisedInterior] = 1;
        Hand.Null.Write(state, "residents");
        state.Bools["foliage"] = false;
        state.Bools["pause"] = false;
        state.Bools["is public day"] = true;
        state.Bools["is complete"] = true;
        state.Bools["destroyed"] = false;
        state.Floats["world Y pos"] = piece.Position.Y;
        state.Floats["player furniture Y offset"] = piece.Position.Y - house.WorldY;
        state.Strings["squad template"] = "";
        state.Strings["residents template"] = "";
        state.Strings["name"] = record.Name;
        if (house.Owner is { } owner) state.Strings["owner faction ID"] = owner;
    }

    /// <summary>
    /// The ITEM_PLACEMENT_GROUP of a loose-item slot's entry, without its id: the keys of the group's own writer (<c>FUN_1405567b0</c>).
    /// </summary>
    public static FcsRecord GroupState(TownItemSlot slot, InteriorHouse house)
    {
        var state = new FcsRecord { Type = (int)FcsRecordType.ItemPlacementGroup, Name = "0" };
        WriteCommon(state, slot.Floor, house);
        state.Floats["respawn time"] = 0;
        state.Strings["base data sid"] = slot.GroupId;
        return state;
    }

    /// <summary>
    /// The keys an entry of either kind has: it stands inside the house, on a floor.
    /// </summary>
    static void WriteCommon(FcsRecord state, int floor, InteriorHouse house)
    {
        state.Bools["is inside building"] = true;
        house.Handle.Write(state, "isInsideBuilding");
        state.Ints["floornum"] = floor;
    }

    /// <summary>
    /// A finished building's construction keys: each material delivered in full and the progress at its target.
    /// </summary>
    static void WriteConstruction(FcsRecord state, GameRecord record)
    {
        var data = BuildingBuildData.From(record);
        state.Floats["construction progress"] = data.Target;
        for (int i = 0; i < data.Materials.Count; i++)
            state.Floats[i.ToString(CultureInfo.InvariantCulture) + "construction mats"] = data.Materials[i].Required;
    }

    /// <summary>
    /// The power keys of an object with a functionality (<b>Observed</b>, METARU: on all 10992 such interior objects and no other), as it stands
    /// unbroken with power; a town machine's own state is written over them (<see cref="TownMachineSave"/>).
    /// </summary>
    static void WritePower(FcsRecord state)
    {
        state.Bools["broken"] = false;
        state.Bools["power on"] = true;
        state.Bools["gets battery"] = true;
        state.Floats["batterycharge"] = 0;
    }

    /// <summary>
    /// A lockable object's lock keys before its fixture's own are written over them: no lock level, unlocked, the record's hardness.
    /// </summary>
    static void WriteLock(FcsRecord state, GameRecord record)
    {
        state.Bools["locked"] = false;
        state.Ints["lock lvl"] = 0;
        state.Floats["hardness"] = record.GetFloat("hardness");
    }

    /// <summary>
    /// A production building's keys as it stands idle (<b>Observed</b>, METARU: <c>resource mult</c> 1 and the amounts 0 in all 322, a bench's
    /// three keys 0 or false in all 81): one <c>consumption amount-&lt;k&gt;</c> per input section (<see cref="MachineData.Inputs"/>), and a
    /// crafting bench's empty queue.
    /// </summary>
    static void WriteIdle(FcsRecord state, int inputs, int function)
    {
        state.Floats["resource mult"] = 1;
        state.Floats["production amount"] = 0;
        for (int k = 0; k < inputs; k++)
            state.Floats["consumption amount-" + k.ToString(CultureInfo.InvariantCulture)] = 0;

        if (function != TownBuildings.CraftingFunction) return;

        state.Floats["num crafts"] = 0;
        state.Bools["repeat"] = false;
        state.Ints["craft partial item size"] = 0;
    }

    /// <summary>
    /// Writes a lock as it is now over an entry's lock keys (the hardness when it is known); nothing for an object without one.
    /// </summary>
    static void WriteLock(FcsRecord state, (int Level, bool Locked, float? Hardness)? now)
    {
        if (now is not var (level, locked, hardness)) return;

        state.Bools["locked"] = locked;
        state.Ints["lock lvl"] = level;
        if (hardness is { } h) state.Floats["hardness"] = h;
    }

    #endregion
}

/// <summary>
/// What an entry inside a house takes from the house (<see cref="InteriorSave"/>): its handle, the height its state saves as <c>world Y pos</c>,
/// its owner faction's id (null: none saved) and whether it is open to the public.
/// </summary>
public readonly record struct InteriorHouse(Hand Handle, float WorldY, string? Owner, bool Public)
{
    /// <summary>
    /// The house of a saved state; a state without a height has the building's.
    /// </summary>
    public static InteriorHouse Of(FcsRecord house, TownBuilding b) => new(
        Hand.Read(house, "handle"),
        house.Floats.GetValueOrDefault("world Y pos", b.Position.Y),
        house.Strings.GetValueOrDefault("owner faction ID"),
        house.Bools.GetValueOrDefault("is public day", true));
}
