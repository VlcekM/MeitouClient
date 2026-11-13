using System.Globalization;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Towns;

namespace Meitou.Simulation.Locks;

/// <summary>
/// The world's doors, lockable containers, cages and beds (docs/simulation.md "Cages, beds and locks"; the original's rules in docs/game/locks.md and
/// docs/game/economy.md 11.5). A town's fixtures are registered once, when the population first spawns it (<see cref="RegisterSite"/>), in building
/// order, and stay when the town unloads. Lock levels are rolled from the world seed and the fixture's id, so the order towns load in never changes
/// them. Everything here runs in serial phases (or before the first tick).
/// <list type="bullet">
/// <item><b>Locks</b>: level = owner faction's <c>doors lock level</c> + uniform int in [-r, r] (<c>lock level random</c>) + the record's <c>lock
/// level bonus</c> (a door's own, its hardness the parent's), doors at least 1, others at least 0, at most 100; locks start unlocked; the owner is
/// the placement's <c>owner faction ID</c>, else the town's.</item>
/// <item><b>Doors</b>: the squad's <c>initial door state</c> applies when its home is assigned (<see cref="ApplyInitialDoorState"/>).
/// <see cref="Doors"/> hears a building as
/// blocked for paths while every one of its doors is effectively locked and none broken (<b>engine choice</b>: the navmesh has one door state per
/// building).</item>
/// <item><b>Cages</b>: <see cref="FindCage"/> (the original's weights, cages a carrier cannot reach left out), <see cref="Imprison"/> (recorded in
/// <see cref="CagingsOf"/> and <see cref="Caged"/>),
/// <see cref="Release"/>, <see cref="GetOut"/>, <see cref="LiftOut"/>; beds <see cref="FindBed"/> (the original's bed search), <see cref="EnterBed"/>
/// / <see cref="LeaveBed"/> (orders and trips: <see cref="BedSystem"/>).</item>
/// </list>
/// </summary>
public sealed partial class FixtureRegistry
{
    #region Fixtures and lookups

    /// <summary>
    /// The cage finder's distance scale: weight clamp(1 - d^2 / 4608^2, 1e-5, 1), x100 for the carrier's faction, x100 in its home (economy.md 11.7,
    /// <b>Observed</b>).
    /// </summary>
    public const float CageSearchScale = 4608;

    /// <summary>
    /// A sentence above this bounty is <see cref="LifeSentence"/> hours (economy.md 11.5).
    /// </summary>
    public const int LifeBounty = 25000;

    /// <summary>
    /// A life sentence's length.
    /// </summary>
    public const float LifeSentence = 999999;

    /// <summary>
    /// Every fixture, in registration order.
    /// </summary>
    readonly List<Fixture> _all = [];

    /// <summary>
    /// The fixtures by id.
    /// </summary>
    readonly Dictionary<string, Fixture> _byId = new(StringComparer.Ordinal);

    /// <summary>
    /// The fixtures of each registered building, by building key.
    /// </summary>
    readonly Dictionary<string, List<Fixture>> _byBuilding = new(StringComparer.Ordinal);

    /// <summary>
    /// The registered buildings' keys, in registration order.
    /// </summary>
    readonly List<string> _buildings = [];

    /// <summary>
    /// The sites whose buildings are registered.
    /// </summary>
    readonly HashSet<int> _sites = [];

    /// <summary>
    /// The buildings whose squad's initial door state was applied.
    /// </summary>
    readonly HashSet<string> _initialApplied = new(StringComparer.Ordinal);

    /// <summary>
    /// The Open to public flag of the player's town buildings (the original's Building+0xc8, saved as <c>is public day</c>): set by the panel or
    /// read from a save.
    /// </summary>
    readonly Dictionary<string, bool> _publicFlags = new(StringComparer.Ordinal);

    /// <summary>
    /// The functionality record of each building record, by id; derived from the data.
    /// </summary>
    readonly Dictionary<string, GameRecord?> _functions = new(StringComparer.Ordinal);

    /// <summary>
    /// (cage id, faction) -> the game hour until which a walk there is known to fail (engine: the "no path" memo of FindCage). Sorted, so it
    /// hashes in a fixed order.
    /// </summary>
    readonly SortedDictionary<(string Cage, int Faction), double> _unreachable = new(Comparer<(string Cage, int Faction)>.Create((a, b) =>
        string.CompareOrdinal(a.Cage, b.Cage) is var c and not 0 ? c : a.Faction.CompareTo(b.Faction)));

    /// <summary>
    /// The cagings of the tick <see cref="_cagingsTick"/>.
    /// </summary>
    readonly List<Imprisonment> _cagings = [];

    /// <summary>
    /// The tick of <see cref="_cagings"/> (-1: none).
    /// </summary>
    long _cagingsTick = -1;

    /// <summary>
    /// The door gate of the paths (null: none), told when a building's doors block.
    /// </summary>
    IDoorGate? _doors;

    /// <summary>
    /// The number of fixtures.
    /// </summary>
    public int Count => _all.Count;

    /// <summary>
    /// Every fixture, in registration order.
    /// </summary>
    public IReadOnlyList<Fixture> All => _all;

    /// <summary>
    /// The fixture <paramref name="id"/> (null: none).
    /// </summary>
    public Fixture? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// The fixtures of a placed building (its doors, own lock, interior cages, containers and beds).
    /// </summary>
    public IReadOnlyList<Fixture> OfBuilding(string buildingInstance) => _byBuilding.TryGetValue(buildingInstance, out var l) ? l : [];

    /// <summary>
    /// Whether the site's buildings are registered.
    /// </summary>
    public bool IsRegistered(int site) => _sites.Contains(site);

    /// <summary>
    /// Navigation's door table (null: headless, nothing to tell). Setting it tells it every registered building's state.
    /// </summary>
    public IDoorGate? Doors
    {
        get => _doors;
        set
        {
            _doors = value;
            _doorPicks = null;
            _doorsByBuilding = null;
            foreach (var b in _buildings) SyncDoors(b);
        }
    }

    /// <summary>
    /// The GAMESTATE_BUILDING records of a loaded save by building instance id (<see cref="Saving.SaveLoader"/>): a building registered later takes
    /// its lock and door state from them.
    /// </summary>
    public IReadOnlyDictionary<string, FcsRecord>? Saved { get; set; }

    /// <summary>
    /// The placements of a loaded save's building lists by building instance id (<see cref="Saving.HeldSave.BuildingSpots"/>): a prisoner whose <c>in
    /// what</c> names a generated interior cage binds to the interior cage fixture of the same record at that place (<see cref="Bind"/>).
    /// </summary>
    public IReadOnlyDictionary<string, SavedSpot>? SavedSpots { get; set; }

    /// <summary>
    /// The item factory a loaded save's container contents are made with (<see cref="ContainerSave"/>); set by the loader.
    /// </summary>
    public Items.ItemFactory? Items { get; set; }

    /// <summary>
    /// The registered buildings, in registration order.
    /// </summary>
    public IReadOnlyList<string> Buildings => _buildings;

    #endregion

    #region Published spots

    /// <summary>
    /// The container spots last built (null: build them again).
    /// </summary>
    IReadOnlyList<ContainerSpot>? _containerSpots;

    /// <summary>
    /// Every cage (null: build it again).
    /// </summary>
    List<Fixture>? _cageList;

    /// <summary>
    /// Every bed (null: build it again).
    /// </summary>
    List<Fixture>? _bedList;

    /// <summary>
    /// The cage spots last built.
    /// </summary>
    IReadOnlyList<CageSpot>? _publishedCages;

    /// <summary>
    /// The key of the cages' state <see cref="_publishedCages"/> was built for.
    /// </summary>
    ulong _publishedCagesKey;

    /// <summary>
    /// The bed spots last built (null: build them again).
    /// </summary>
    IReadOnlyList<BedSpot>? _bedSpots;

    /// <summary>
    /// The training spots last built (null: build them again).
    /// </summary>
    IReadOnlyList<TrainingSpot>? _dummySpots;

    /// <summary>
    /// The mark between a building's key and a training dummy's number.
    /// </summary>
    const string DummyMark = "@dummy";

    /// <summary>
    /// The door picks last built (null: build them again).
    /// </summary>
    IReadOnlyList<DoorPick>? _doorPicks;

    /// <summary>
    /// The doors of each building last built (null: build them again).
    /// </summary>
    ILookup<string, Fixture>? _doorsByBuilding;

    /// <summary>
    /// The door views last published.
    /// </summary>
    IReadOnlyDictionary<(string Building, int Door), DoorView> _publishedDoors = new Dictionary<(string Building, int Door), DoorView>();

    /// <summary>
    /// The key of the doors' state <see cref="_publishedDoors"/> was built for.
    /// </summary>
    ulong _publishedDoorsKey;

    /// <summary>
    /// Every door (null: build it again).
    /// </summary>
    List<Fixture>? _doorList;

    /// <summary>
    /// Where the containers with an inventory stand, for the host's picking; rebuilt (serial phases) only when a building registers or goes.
    /// </summary>
    public IReadOnlyList<ContainerSpot> ContainerSpots => _containerSpots
            ??= [.. _all.Where(f => f.Contents is not null).Select(f
                    => new ContainerSpot(f.Id, f.RecordId, f.Position, f.Owner, f.Lock is { EffectivelyLocked: true }, f.Lock?.Level ?? 0, f.Yaw))];

    /// <summary>
    /// Every cage, for the host's picking; the list is rebuilt (serial phases) when a building registers or goes, the spots only when a cage's lock,
    /// occupant or owner changed.
    /// </summary>
    public IReadOnlyList<CageSpot> CageSpots(CharacterTable table)
    {
        _cageList ??= [.. _all.Where(f => f.Kind == FixtureKind.Cage)];
        ulong key = 0x43616765 ^ (ulong)_cageList.Count;
        for (int i = 0; i < _cageList.Count; i++)
        {
            var f = _cageList[i];
            var held = IsFree(table, f) ? CharacterId.None : f.Occupant;
            key = Rng.Mix(key ^ (ulong)i) ^ (uint)held.Slot ^ ((ulong)(uint)held.Generation << 32) ^ ((ulong)(uint)(f.Lock?.Level ?? 0) << 16)
                    ^ ((ulong)(uint)f.Owner << 40)
                ^ (f.Lock is { EffectivelyLocked: true } ? 1UL << 62 : 0) ^ (f.OccupantSelection ? 1UL << 63 : 0);
        }

        if (_publishedCages is not null && key == _publishedCagesKey) return _publishedCages;
        _publishedCagesKey = key;
        return _publishedCages = [.. _cageList.Select(f
                => new CageSpot(f.Id, f.RecordId, f.Position, f.Owner, f.Lock is { EffectivelyLocked: true }, IsFree(table, f) ? CharacterId.None
                        : f.Occupant, f.Lock?.Level ?? 0, f.Yaw, f.OccupantSelection))];
    }

    /// <summary>
    /// Every bed, for the host's picking; rebuilt (serial phases) only when a building registers or goes.
    /// </summary>
    public IReadOnlyList<BedSpot> BedSpots => _bedSpots
            ??= [.. _all.Where(f => f.Kind == FixtureKind.Bed).Select(f => new BedSpot(f.Id, f.RecordId, f.Position, f.Owner, f.Yaw))];

    /// <summary>
    /// Every town training dummy, for the host's picking; rebuilt (serial phases) only when a building registers or goes.
    /// </summary>
    public IReadOnlyList<TrainingSpot> TrainingSpots => _dummySpots
            ??= [.. _all.Where(f => f.Kind == FixtureKind.Training).Select(f => new TrainingSpot(f.Id, f.RecordId, f.Position, f.Owner, f.Yaw))];

    /// <summary>
    /// The fixture id of a town building's <paramref name="index"/>th training dummy (its furniture in layout order):
    /// <c>&lt;building&gt;@dummy&lt;index&gt;</c>.
    /// </summary>
    public static string DummyId(string building, int index) => string.Create(CultureInfo.InvariantCulture, $"{building}{DummyMark}{index}");

    /// <summary>
    /// Whether an id names a town training dummy (<see cref="DummyId"/>), registered or not yet.
    /// </summary>
    public static bool IsDummy(string id) => id.LastIndexOf(DummyMark, StringComparison.Ordinal) is var at and > 0 && at
            + DummyMark.Length < id.Length
        && id.AsSpan(at + DummyMark.Length).IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>
    /// Every door, for the host's picking (<see cref="PublishedDoors"/> leaves closed unlocked town doors out); rebuilt (serial phases) only when a
    /// building registers or goes.
    /// </summary>
    public IReadOnlyList<DoorPick> DoorPicks => _doorPicks
            ??= [.. _all.Where(f => f.Kind == FixtureKind.Door).Select(f
                    => new DoorPick(f.Id, f.Building, f.DoorIndex, f.Owner, DoorSpotOf(f)?.Centre ?? f.Position, DoorSpotOf(f)?.Slide
                            ?? Vector3.Zero) { HostRecord = f.HostRecordId, Origin = f.Position, Yaw = f.Yaw, Rotation = f.Rotation })];

    /// <summary>
    /// The doors of each building, rebuilt with <see cref="DoorPicks"/> (both in the serial phases; read in parallel ones after).
    /// </summary>
    public ILookup<string, Fixture> DoorsByBuilding => _doorsByBuilding
            ??= _all.Where(f => f.Kind == FixtureKind.Door).ToLookup(f => f.Building, StringComparer.Ordinal);

    /// <summary>
    /// The doors of the player's buildings and every other door that is not plainly closed and unlocked (open, moving, locked, set to lock or
    /// broken), for the snapshot; a new dictionary only when one of them changed.
    /// </summary>
    internal IReadOnlyDictionary<(string Building, int Door), DoorView> PublishedDoors()
    {
        _doorList ??= [.. _all.Where(f => f.Kind == FixtureKind.Door)];
        ulong key = 0x446F6F72 ^ (ulong)_doorList.Count;
        for (int i = 0; i < _doorList.Count; i++)
        {
            var f = _doorList[i];
            if (f.Kind != FixtureKind.Door || !Shown(f)) continue;
            key = Rng.Mix(key ^ (ulong)i) ^ BitConverter.SingleToUInt32Bits(f.OpenAmount) ^ ((ulong)f.Motion << 32)
                    ^ (f.Lock is { EffectivelyLocked: true } ? 1UL << 40 : 0) ^ (f.WantsLock ? 1UL << 41 : 0) ^ (f.DoorBroken ? 1UL << 42 : 0);
        }

        if (key == _publishedDoorsKey) return _publishedDoors;
        var doorViews = new Dictionary<(string Building, int Door), DoorView>();
        foreach (var f in _doorList)
        {
            if (Shown(f))
            {
                doorViews[(f.Building, f.DoorIndex)] = new DoorView(f.OpenAmount, f.Motion, f.Lock is { EffectivelyLocked: true }, f.WantsLock,
                        f.DoorBroken, DoorSpotOf(f)?.Centre ?? f.Position, f.RecordId, f.Lock?.Level ?? 0);
            }
        }

        _publishedDoorsKey = key;
        return _publishedDoors = doorViews;

        static bool Shown(Fixture f) => f.Site < 0 || f.OpenAmount > 0 || f.Motion != DoorMotion.Closed || f.WantsLock || f.DoorBroken
                || f.Lock is { EffectivelyLocked: true };
    }

    /// <summary>
    /// The panel's view of a building's doors (null for no building, or one without fixtures).
    /// </summary>
    internal BuildingDetail? DetailOf(string? instance, int faction)
    {
        if (instance is null || !_byBuilding.TryGetValue(instance, out var list)) return null;
        var doorList = list.Where(f => f.Kind == FixtureKind.Door).OrderBy(f => f.DoorIndex)
            .Select(f
                    => new DoorDetail(f.Motion is DoorMotion.Open or DoorMotion.Opening, f.Lock is { EffectivelyLocked: true }, f.Lock?.Level ?? 0,
                            f.DoorBroken, f.RecordId, f.WantsLock)).ToArray();
        return new BuildingDetail(instance, doorList, OwnsBuilding(instance, faction), PublicOf(instance))
        {
            SalePrice = SalePrice(instance, faction),
            ResidentsPublic = PublicDayOf(instance),
        };
    }

    #endregion

    #region Registration

    /// <summary>
    /// The node type of a bed's use node.
    /// </summary>
    const int UseNodeType = 4;

    /// <summary>
    /// How long a use node a failed walk marked stays skipped: 0.5 on the original's world clock (<c>DAT_1421303d0 + 0xa0</c>, game hours,
    /// <b>Observed</b>: the 5-minute rule of Task_TakeIntruderOutside reads its differences x 60).
    /// </summary>
    public const double BadNodeHours = 0.5;

    /// <summary>
    /// A use node is marked when a path to the building fails within this (squared 400) of the failing position (<c>FUN_14054d410</c>).
    /// </summary>
    const float BadNodeReach = 20;

    /// <summary>
    /// The most a generated interior cage or bed of a save may stand from Meitou's fixture of the same record and still be that one: Meitou places
    /// the interior from the same layout (<b>Observed</b>: the Rebirth Slave Shop's caged prisoner's cage at distance 0, the next cage of the room
    /// 27.8 away; the 24 saved sleepers of the Rebirth garrison's beds at distance 0, the next bed of the record 33 away).
    /// </summary>
    const float SavedSpotReach = 1;

    /// <summary>
    /// Registers the fixtures of a town's buildings (<see cref="TownSite.Buildings"/>) once, then binds the held characters whose
    /// <see cref="CharacterCold.InWhat"/> names one. Their rooms and lamps go to <see cref="World.Sight"/> with them.
    /// </summary>
    public void RegisterSite(World world, PopulationData data, int site)
    {
        if (site < 0 || site >= data.Sites.Count || !_sites.Add(site)) return;
        int townFaction = site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;
        foreach (var b in data.Sites[site].Buildings) RegisterWithSight(world, data, site, b, townFaction);
        Bind(world);
        EmptyPurchased(world, site);
    }

    /// <summary>
    /// Registers a town building and adds it to the sight's occluders.
    /// </summary>
    void RegisterWithSight(World world, PopulationData data, int site, TownBuilding b, int townFaction)
    {
        RegisterBuilding(world, data, site, b, townFaction);
        if (data.Sight is { } sight) world.Sight.Add(sight, SightBuilding(b, IsDestroyed(b)));
    }

    /// <summary>
    /// A building added to a town's list while the world runs (<see cref="TownEdits.Place"/>, the level editor): registered as
    /// <see cref="RegisterSite"/> registers a loaded one (doors, lock, beds, seats, containers, property, room and lamps). A town not registered yet
    /// takes it with the rest when it is.
    /// </summary>
    internal void RegisterAdded(World world, PopulationData data, int site, TownBuilding b)
    {
        if (!_sites.Contains(site)) return;
        RegisterWithSight(world, data, site, b, site < data.SiteFactions.Count ? data.SiteFactions[site] : -1);
        Bind(world);
    }

    /// <summary>
    /// A building taken out of a town's list (<see cref="TownEdits.Clear"/>): its fixtures, property, public flag, room and lamps go; whoever lies in
    /// its beds or sits in its cages is let out first (a cage's lock opened for it).
    /// </summary>
    internal void RemoveBuilding(World world, string building)
    {
        foreach (var f in OfBuilding(building).ToArray())
        {
            if (f.Occupant.IsNone) continue;
            if (f.Kind == FixtureKind.Bed) LeaveBed(world, f.Occupant);
            else if (f.Kind == FixtureKind.Cage)
            {
                if (f.Lock is { } cageLock) cageLock.Locked = false;
                GetOut(world, f.Occupant);
            }
        }

        Unregister(building);
        _properties.Remove(building);
        _resetBuildings.Remove(building);
        _publicFlags.Remove(building);
        _levelPublic.Remove(building);
        _nested.Remove(building);
        _ownedProperties = null;
        world.Sight.Remove(building);
    }

    /// <summary>
    /// Registers a town building: its owner (a saved owner first) and property, its fixtures, then their index and their saved or level state.
    /// A building with no fixtures, no property and no seat is not kept.
    /// </summary>
    void RegisterBuilding(World world, PopulationData data, int site, TownBuilding b, int townFaction)
    {
        if (_byBuilding.ContainsKey(b.Key)) return;

        int levelOwner = data.LevelOwnerOf(b, townFaction);
        int owner = levelOwner;
        if (Saved?.GetValueOrDefault(b.Key)?.Strings.GetValueOrDefault("owner faction ID") is { Length: > 0 } savedOwner
            && data.FactionIndex(savedOwner) is >= 0 and var savedFaction)
        {
            owner = savedFaction;
        }

        // saved as the player's: bought, and saved again as such
        RegisterProperty(world, data, site, b, owner, purchased: owner != levelOwner && owner == data.PlayerFaction);
        var list = FixturesOf(world, data, site, b, owner);
        if (list.Count == 0 && !_properties.ContainsKey(b.Key) && !_seatSpots.Values.Any(s => s.Building == b.Key))
            return;

        IndexBuilding(b.Key, list);
        _usableData ??= data;
        RestoreBuilding(world, b, list, owner);
    }

    /// <summary>
    /// A building's fixtures: its doors and own lock (none while destroyed), its lockable or storage furniture, its beds and training dummies; its
    /// seats are registered on the way.
    /// </summary>
    List<Fixture> FixturesOf(World world, PopulationData data, int site, TownBuilding b, int owner)
    {
        var db = data.Db;
        var faction = owner >= 0 && owner < data.Factions.Count ? data.Factions[owner] : null;
        var list = new List<Fixture>();
        var record = db.Find(b.BuildingId);

        // A destroyed building has no doors and no lock of its own (docs/game/locks.md "Doors"); its interior's fixtures stay.
        if (record is not null && !IsDestroyed(b))
        {
            AddDoors(world, db, record, b.Key, site, owner, faction, b.Position, b.Yaw, b.Resolved?.Placement.Rotation, list);
            if (Lockable(db, record) is { } own)
                list.Add(Make(world, db, own, b.Key, b.Key, record, site, owner, faction, b.Position, b.Yaw));
        }

        foreach (var f in b.Fixtures)
        {
            if (db.Find(f.RecordId) is { } item
                && (Lockable(db, item) ?? (f.StorageWidth > 0 && f.StorageHeight > 0 ? FixtureKind.Container : null)) is { } kind)
            {
                list.Add(Make(world, db, kind, f.Id, b.Key, item, site, owner, faction, f.Position, f.Yaw, f.StorageWidth, f.StorageHeight));
            }
        }

        RegisterSeats(b, owner);
        AddBeds(db, site, b, owner, list);
        AddDummies(site, b, owner, list);
        return list;
    }

    /// <summary>
    /// Adds each interior bed in spot order, or the standalone building's own bed when it has no interior and is intact.
    /// </summary>
    void AddBeds(GameDatabase db, int site, TownBuilding b, int owner, List<Fixture> list)
    {
        int bed = 0;
        foreach (var spot in b.Spots)
        {
            if (spot.Kind != HomeSpotKind.Bed) continue;

            list.Add(new Fixture
            {
                Id = $"{b.Key}@bed{bed.ToString(CultureInfo.InvariantCulture)}",
                Building = b.Key,
                Kind = FixtureKind.Bed,
                Site = site,
                Owner = owner,
                Position = spot.Position,
                Yaw = spot.Yaw,
                Marker = spot.Marker,
                UseRange = 15,
                RecordId = spot.Record ?? "",
                Indoors = true,
                Bed = spot.Record is { } bedRecord && db.Find(bedRecord) is { } bedItem ? BedDataOf(db, bedItem) ?? default : default,
            });
            bed++;
        }

        if (bed != 0 || b.HasInterior || IsDestroyed(b) || db.Find(b.BuildingId) is not { } record || BedDataOf(db, record) is not { } outdoorBed)
            return;

        var rotation = b.Resolved?.Placement.Rotation ?? Quaternion.CreateFromAxisAngle(Vector3.UnitY, b.Yaw);
        list.Add(BedOf(db, b.Key, record, site, owner, b.Position, rotation, outdoorBed));
    }

    /// <summary>
    /// Adds a fixture for each training dummy among the building's furniture, numbered in furniture order.
    /// </summary>
    static void AddDummies(int site, TownBuilding b, int owner, List<Fixture> list)
    {
        int dummy = 0;
        foreach (var piece in b.Furniture)
        {
            if (piece.Kind != HomeSpotKind.Training || piece.Record is not { } dummyRecord) continue;

            list.Add(new Fixture
            {
                Id = DummyId(b.Key, dummy++),
                Building = b.Key,
                Kind = FixtureKind.Training,
                Site = site,
                Owner = owner,
                Position = piece.Position,
                Yaw = piece.Yaw,
                RecordId = dummyRecord,
                HostRecordId = b.BuildingId,
            });
        }
    }

    /// <summary>
    /// Indexes the building's fixtures and drops the lists derived from them.
    /// </summary>
    void IndexBuilding(string building, List<Fixture> list)
    {
        _byBuilding[building] = list;
        _buildings.Add(building);
        foreach (var f in list)
        {
            _all.Add(f);
            _byId[f.Id] = f;
        }

        _containerSpots = null;
        _doorPicks = null;
        _doorsByBuilding = null;
        _bedSpots = null;
        _bedList = null;
        _cageList = null;
        _doorList = null;
        _publishedCages = null;
        _dummySpots = null;
    }

    /// <summary>
    /// The building's public and nested flags, then its fixtures' saved state (and a player building's own public flag), else the level's; then
    /// its doors follow.
    /// </summary>
    void RestoreBuilding(World world, TownBuilding b, List<Fixture> list, int owner)
    {
        _levelPublic[b.Key] = Saved?.GetValueOrDefault(b.Key) is { } savedState ? savedState.Bools.GetValueOrDefault("is public day", true)
                : b.LevelState?.GetBool("is public day", true) ?? true;
        if (Saved?.GetValueOrDefault(b.Key) is { } nestedState ? nestedState.Bools.GetValueOrDefault("is inside building")
                : b.LevelState?.GetBool("is inside building") ?? false)
        {
            _nested.Add(b.Key);
        }

        if (Saved?.GetValueOrDefault(b.Key) is { } state)
        {
            Restore(StateKeys.Of(state), b.Key, list, level: false);
            ReadRecord(b.Key, state);
            if (owner >= 0 && owner == world.Player.Faction && state.Bools.TryGetValue("is public day", out bool open))
                _publicFlags[b.Key] = open;
        }
        else if (b.LevelState is { } levelState)
        {
            Restore(StateKeys.Of(levelState), b.Key, list, level: true);
        }

        SyncDoors(b.Key);
    }

    /// <summary>
    /// One door fixture per entry of the building's <c>doors</c> list: the door's own <c>lock level bonus</c> and motion, the parent's
    /// <c>hardness</c>.
    /// </summary>
    void AddDoors(World world, GameDatabase db, GameRecord record, string instance, int site, int owner, FactionData? faction, Vector3 at, float yaw,
            Quaternion? rotation, List<Fixture> list)
    {
        var doorRefs = record.GetReferences("doors");
        bool gate = TownBuildings.IsGate(db, record, _functions);
        for (int i = 0; i < doorRefs.Count; i++)
        {
            var door = db.Find(doorRefs[i].TargetStringId);
            string id = $"{instance}#{i.ToString(CultureInfo.InvariantCulture)}";
            list.Add(new Fixture
            {
                Id = id,
                Building = instance,
                Kind = FixtureKind.Door,
                RecordId = doorRefs[i].TargetStringId,
                Site = site,
                Owner = owner,
                Position = at,
                Yaw = yaw,
                Rotation = rotation is { } turn && turn.LengthSquared() > 1e-12f ? Quaternion.Normalize(turn) : null,
                DoorIndex = i,
                WantsLock = door?.GetBool("always locks") ?? false,
                HostRecordId = record.StringId,
                IsGate = gate,
                Leaf = door is null ? new DoorLeaf(0, 30, 1, 0) : DoorLeaf.Of(door),
                Lock = new LockState { Level = RollLevel(world.Seed, id, faction, IntOf(door, "lock level bonus"), door: true),
                        Hardness = FloatOf(record, "hardness") },
            });
        }
    }

    /// <summary>
    /// Registers the doors of a finished building of the player (<see cref="Construction.BuildSite"/>; engine: when it is complete, the lock rolled
    /// from its owner's faction) and takes their state from a loaded save; a bed the player built is a bed fixture <c>&lt;site&gt;@bed0</c> at the
    /// site's origin and facing (the ones lain in bind to it). A cage or a <c>has lock</c> building (a safe) gets its own lock as a town one does
    /// (docs/game/locks.md "Lock object": the owner's <c>doors lock level</c>, unlocked); a safe's items stay the machine's storage, so its fixture
    /// has no grid; while its machine is broken (being dismantled) that lock counts as broken. A building registered already is left as it is.
    /// </summary>
    public void RegisterPlayerBuilding(World world, PopulationData data, Construction.BuildSite site)
    {
        if (_byBuilding.ContainsKey(site.Id) || data.Db.Find(site.Record) is not { } record) return;
        var bed = BedDataOf(data.Db, record);
        var own = Lockable(data.Db, record);
        if (record.GetReferences("doors").Count == 0 && bed is null && own is null) return;
        var faction = site.Owner >= 0 && site.Owner < data.Factions.Count ? data.Factions[site.Owner] : null;
        var list = new List<Fixture>();
        float yaw = TownBuildings.YawOf(site.Rotation);
        AddDoors(world, data.Db, record, site.Id, -1, site.Owner, faction, site.Position, yaw, site.Rotation, list);
        if (own is { } kind)
        {
            var made = Make(world, data.Db, kind, site.Id, site.Id, record, -1, site.Owner, faction, site.Position, yaw, host: record.StringId);
            if (made.Lock is { } l) l.OwnerBroken = world.Machines.Find(site.Id)?.Broken == true;
            list.Add(made);
        }

        if (bed is { } b) list.Add(BedOf(data.Db, site.Id, record, -1, site.Owner, site.Position, site.Rotation, b));
        _byBuilding[site.Id] = list;
        _buildings.Add(site.Id);
        foreach (var f in list)
        {
            _all.Add(f);
            _byId[f.Id] = f;
        }

        _containerSpots = null;
        _doorPicks = null;
        _doorsByBuilding = null;
        _bedSpots = null;
        _bedList = null;
        _cageList = null;
        _doorList = null;
        _publishedCages = null;
        _dummySpots = null;
        if (Saved?.GetValueOrDefault(BuildingKey.Of(site.Zone, site.Id)) is { } state)
            Restore(StateKeys.Of(state), site.Id, list, level: false);

        SyncDoors(site.Id);
        if (bed is not null) Bind(world);
    }

    /// <summary>
    /// Registers a camp building's cage, lock or bed (<see cref="NestBuildings"/>: a Prisoner Pole of a fog nest, a bandit camp's bedroll, #362),
    /// owned by the camp's faction: a nest's is marked (<see cref="Fixture.Nest"/>), a town's has the town's <paramref name="site"/>. Then binds the
    /// held characters whose <see cref="CharacterCold.InWhat"/> names it. A piece with none is left out.
    /// </summary>
    public void RegisterNestBuilding(World world, PopulationData data, NestPiece piece, int owner, int site = -1)
    {
        if (_byBuilding.ContainsKey(piece.Id) || data.Db.Find(piece.Record) is not { } record) return;
        var kind = Lockable(data.Db, record);
        var bed = BedDataOf(data.Db, record);
        if (kind is null && bed is null) return;
        var list = new List<Fixture>();
        if (kind is { } k)
        {
            var faction = owner >= 0 && owner < data.Factions.Count ? data.Factions[owner] : null;
            var made = Make(world, data.Db, k, piece.Id, piece.Id, record, site, owner, faction, piece.Position, TownBuildings.YawOf(piece.Rotation),
                    host: record.StringId);
            made.Nest = site < 0;
            list.Add(made);
        }

        if (bed is { } b) list.Add(BedOf(data.Db, piece.Id, record, site, owner, piece.Position, piece.Rotation, b));
        _byBuilding[piece.Id] = list;
        _buildings.Add(piece.Id);
        foreach (var f in list)
        {
            _all.Add(f);
            _byId[f.Id] = f;
        }

        _containerSpots = null;
        _cageList = null;
        _publishedCages = null;
        _bedSpots = null;
        _bedList = null;
        Bind(world);
    }

    /// <summary>
    /// The bed of a building standing on its own (placed by the player or by a camp): lain in at its position marker, within 15.
    /// </summary>
    static Fixture BedOf(GameDatabase db, string id, GameRecord record, int site, int owner, Vector3 at, Quaternion rotation, BedData bed) => new()
    {
        Id = id + "@bed0",
        Building = id,
        Kind = FixtureKind.Bed,
        Site = site,
        Owner = owner,
        Position = at,
        Yaw = TownBuildings.YawOf(rotation),
        Marker = WorldObjectLayout.PositionMarker(db, record, at, rotation),
        UseRange = 15,
        RecordId = record.StringId,
        HostRecordId = record.StringId,
        Bed = bed,
    };

    /// <summary>
    /// A nest's buildings leave the world: whoever its cages hold is let out first.
    /// </summary>
    public void UnregisterNestBuildings(World world, IEnumerable<NestPiece> pieces)
    {
        foreach (var piece in pieces)
        {
            foreach (var f in OfBuilding(piece.Id))
                if (!f.Occupant.IsNone) Release(world, f.Occupant);
            Unregister(piece.Id);
        }
    }

    /// <summary>
    /// A BUILDING's bed data when one of its functionalities is a bed (<c>function</c> 6 BF_BED or 25 BF_SKELETON_BED): the function, the
    /// functionality's <c>animation</c> (the clip it is lain in) and the sum of its construction materials
    /// (<see cref="Meitou.Data.Gameplay.Construction.BuildingBuildData.Target"/>). Null for anything else.
    /// </summary>
    public static BedData? BedDataOf(GameDatabase db, GameRecord building)
    {
        foreach (var list in building.ReferenceLists)
        {
            foreach (var r in building.GetReferences(list))
            {
                if (db.Find(r.TargetStringId) is { Type: FcsRecordType.BuildingFunctionality } f && f.GetInt("function") is TownBuildings.BedFunction
                        or TownBuildings.SkeletonBedFunction)
                {
                    return new BedData(f.GetInt("function"),
                            f.GetReferences("animation").Select(a => db.Find(a.TargetStringId)?.Name).FirstOrDefault(n => n is not null),
                        Data.Gameplay.Construction.BuildingBuildData.From(building).Target, UseNodes(db, building) is { Length: >= 2 } nodes ? nodes
                                : null);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A BUILDING's NODE_USE instances (<see cref="UseNode"/>) in instance id order; null with none (a bed with fewer than 2 is lain in at its
    /// position marker, <see cref="Fixture.Marker"/>).
    /// </summary>
    static UseNode[]? UseNodes(GameDatabase db, GameRecord building)
    {
        UseNode[] nodes = [.. building.Instances.Values.Where(i => !i.IsCleared && db.Find(i.Target) is { } n && n.GetBool("is node")
                && n.GetInt("node type") == UseNodeType)
            .OrderBy(i => i.Id, StringComparer.Ordinal).Select(i => new UseNode(i.Position, TownBuildings.YawOf(i.Rotation) - MathF.PI / 2))];
        return nodes.Length > 0 ? nodes : null;
    }

    /// <summary>
    /// Where a sleeper is put in a bed and the way it faces (<c>Building::getPositionMarker</c> / <c>getDirectionMarkerQuat</c>, docs/game/locks.md
    /// "Beds"): the bed's use node nearest <paramref name="from"/>, else the bed's position marker (its origin without a flagged part) and facing.
    /// </summary>
    public static (Vector3 At, float Yaw) LieSpot(Fixture bed, Vector3 from, double now = 0) =>
        Nearest(bed, bed.Bed.Nodes, from, bed.BadNodes is { Count: > 0 } ? i => IsBad(bed, i, now) : null) ?? (bed.Marker ?? bed.Position, bed.Yaw);

    /// <summary>
    /// Whether the bed's use node failed within the last <see cref="BadNodeHours"/>.
    /// </summary>
    static bool IsBad(Fixture bed, int node, double now)
    {
        foreach (var (n, stamp) in bed.BadNodes!)
            if (n == node && now <= stamp + BadNodeHours) return true;
        return false;
    }

    /// <summary>
    /// A walk to the bed failed at <paramref name="at"/> (<c>notifyPathImpossible</c> <c>FUN_140796ed0</c> over <c>FUN_14054d410</c>,
    /// <b>Verified</b>): the use node nearest it, within 20 units, is marked bad for <see cref="BadNodeHours"/>; <see cref="LieSpot"/> skips it (and
    /// returns node 0 when every node is marked, <c>FUN_14054b850</c>). A bed with fewer than 2 nodes has nothing to mark. Serial phases only.
    /// </summary>
    public static void MarkBadNode(World world, Fixture bed, Vector3 at)
    {
        if (bed.Bed.Nodes is not { } nodes) return;
        var turn = Matrix4x4.CreateRotationY(bed.Yaw);
        int best = -1;
        float bestD = BadNodeReach * BadNodeReach;
        for (int i = 0; i < nodes.Length; i++)
        {
            float d = Vector3.DistanceSquared(bed.Position + Vector3.Transform(nodes[i].At, turn), at);
            if (d < bestD) (best, bestD) = (i, d);
        }

        if (best < 0) return;
        var list = bed.BadNodes ??= [];
        list.RemoveAll(e => e.Node == best || world.GameHours > e.Stamp + BadNodeHours);
        list.Add((best, world.GameHours));
    }

    /// <summary>
    /// Where an occupant getting out of a cage stands (the original's hold clear <c>FUN_140661f90</c>, run while it is still held, docs/game/locks.md
    /// "Cages and beds"): the cage's use node nearest the cage's own position (a Prisoner Cage's is on its door side, local +Z); null when the cage
    /// has none (the pole), where it stays.
    /// </summary>
    public static Vector3? ExitSpot(Fixture cage) => Nearest(cage, cage.UseNodes, cage.Position)?.At;

    /// <summary>
    /// <c>Building::getPositionMarker(from)</c> (vtable slot 0x470, FUN_14054b850, <b>Verified</b>) of a bed or cage by fixture id: with two or more
    /// use nodes the nearest one to <paramref name="from"/> (a bed skips its marked-bad nodes), else the building's position marker
    /// (<see cref="Fixture.Marker"/>, its position without one); null for an unknown id.
    /// </summary>
    public Vector3? PositionMarker(string fixtureId, Vector3 from, double now = 0)
    {
        if (Find(fixtureId) is not { } f) return null;
        if (f.Kind == FixtureKind.Bed) return LieSpot(f, from, now).At;
        return Nearest(f, f.UseNodes is { Length: >= 2 } nodes ? nodes : null, from)?.At ?? f.Marker ?? f.Position;
    }

    /// <summary>
    /// The use node of <paramref name="f"/> nearest <paramref name="from"/> in the world (squared 3D distance), with its facing; null with no nodes.
    /// </summary>
    static (Vector3 At, float Yaw)? Nearest(Fixture f, UseNode[]? nodes, Vector3 from, Func<int, bool>? skip = null)
    {
        if (nodes is null) return null;

        var turn = Matrix4x4.CreateRotationY(f.Yaw);
        (Vector3 At, float Yaw)? best = null;
        float bestD = float.MaxValue;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (skip?.Invoke(i) == true) continue;
            var at = f.Position + Vector3.Transform(nodes[i].At, turn);
            float d = Vector3.DistanceSquared(at, from);
            if (d < bestD) (best, bestD) = ((at, MathF.IEEERemainder(f.Yaw + nodes[i].Yaw, MathF.Tau)), d);
        }

        // every node skipped: node 0
        return best
                ?? (nodes.Length > 0 ? (f.Position + Vector3.Transform(nodes[0].At, turn), MathF.IEEERemainder(f.Yaw + nodes[0].Yaw, MathF.Tau))
                        : null);
    }

    /// <summary>
    /// Forgets a building's fixtures (a player building dismantled); its doors are opened in the navmesh.
    /// </summary>
    public void Unregister(string buildingInstance)
    {
        if (!_byBuilding.Remove(buildingInstance, out var list)) return;
        _buildings.Remove(buildingInstance);
        RemoveSeats(buildingInstance);
        foreach (var f in list)
        {
            _all.Remove(f);
            _byId.Remove(f.Id);
        }

        _containerSpots = null;
        _doorPicks = null;
        _doorsByBuilding = null;
        _bedSpots = null;
        _bedList = null;
        _cageList = null;
        _doorList = null;
        _publishedCages = null;
        _dummySpots = null;
        _initialApplied.Remove(buildingInstance);
        _doors?.SetDoorsBlocked(buildingInstance, false);
    }

    /// <summary>
    /// The fixture kind of a record: a cage by its function, a container with a lock, else none.
    /// </summary>
    FixtureKind? Lockable(GameDatabase db, GameRecord record)
    {
        if (TownBuildings.FunctionOf(db, record, _functions)?.GetInt("function") == TownBuildings.CageFunction)
            return FixtureKind.Cage;

        return record.GetBool("has lock") ? FixtureKind.Container : null;
    }

    /// <summary>
    /// The name of the first animation the functionality's <paramref name="list"/> names (null: none).
    /// </summary>
    static string? FirstClip(GameDatabase db, GameRecord functionality, string list) =>
        functionality.GetReferences(list) is [var first, ..] ? db.Find(first.TargetStringId)?.Name : null;

    /// <summary>
    /// A fixture of a record: its kind, place, owner, lock and storage as the record and its functionality say.
    /// </summary>
    Fixture Make(World world, GameDatabase db, FixtureKind kind, string id, string building, GameRecord record, int site, int owner,
            FactionData? faction, Vector3 at, float yaw, int storageWidth = 0, int storageHeight = 0, string host = "")
    {
        var functionality = TownBuildings.FunctionOf(db, record, _functions);
        bool locked = kind != FixtureKind.Container || record.GetBool("has lock");
        return new Fixture
        {
            Id = id,
            Building = building,
            Kind = kind,
            RecordId = record.StringId,
            Site = site,
            Owner = owner,
            Position = at,
            Yaw = yaw,
            HostRecordId = host,
            Clips = kind == FixtureKind.Cage && functionality is not null
                    ? new CageClips(FirstClip(db, functionality, "animation"), FirstClip(db, functionality, "animation KO"),
                            FirstClip(db, functionality, "animation dazed"), FirstClip(db, functionality, "animation lockpick")) : default,
            UseNodes = kind == FixtureKind.Cage ? UseNodes(db, record) : null,
            Rack = kind == FixtureKind.Cage && functionality is not null && TownBuildings.IsRack(functionality),
            Restrains = functionality?.GetBool("restrains movement") ?? false,
            OccupantSelection = kind == FixtureKind.Cage && (functionality?.GetBool("occupant selection") ?? false),
            UseRange = functionality is null ? 25 : FloatOf(functionality, "use range", 25),
            Contents = kind == FixtureKind.Container && storageWidth > 0 && storageHeight > 0
                    ? new Items.Inventory(storageWidth, storageHeight) { StackBonusMult = record.GetFloat("stackable bonus mult", 1) } : null,
            Lock = !locked ? null
                    : new LockState { Level = RollLevel(world.Seed, id, faction, IntOf(record, "lock level bonus"), door: false),
                            Hardness = FloatOf(record, "hardness") },
        };
    }

    /// <summary>
    /// The lock level at creation (docs/game/locks.md "Lock object", <b>Verified</b> formula; the roll is Meitou's seeded stream).
    /// </summary>
    public static int RollLevel(ulong seed, string id, FactionData? faction, int bonus, bool door)
    {
        int r = Math.Max(0, faction?.LockLevelRandom ?? 10);
        int delta = r > 0 ? Rng.Int(Rng.Hash(seed, Rng.StableHash(id), RngPurpose.Lock, 0), 2 * r + 1) - r : 0;
        int level = (faction?.DoorsLockLevel ?? 40) + delta + bonus;
        level = door ? (level < 2 ? 1 : level) : Math.Max(level, 0);
        return Math.Min(level, 100);
    }

    /// <summary>
    /// A record's integer field, or its float rounded (0: none).
    /// </summary>
    static int IntOf(GameRecord? r, string key) =>
        r is null ? 0 : r.Ints.TryGetValue(key, out int v) ? v : r.Floats.TryGetValue(key, out float f) ? (int)MathF.Round(f) : 0;

    /// <summary>
    /// A record's float field, or its integer (<paramref name="fallback"/>: none).
    /// </summary>
    static float FloatOf(GameRecord? r, string key, float fallback = 0) =>
        r is null ? fallback : r.Floats.TryGetValue(key, out float v) ? v : r.Ints.TryGetValue(key, out int n) ? n : fallback;

    /// <summary>
    /// Binds held characters to the fixtures their <see cref="CharacterCold.InWhat"/> names (a loaded prisoner whose cage registers later).
    /// </summary>
    internal void Bind(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || table.Cold(s) is not { InSomething: not HeldState.None, InWhat: { } what } c)
                continue;

            if (HeldFixture(c, what) is not { } f || !IsFree(table, f)) continue;
            f.Occupant = new CharacterId(s, next[s].Generation);
            c.InWhat = f.Id;

            // A loaded prisoner plays its cage's poses as one put in does (Imprison): stand, out-cold foetal, a pole's own clip.
            if (c.InSomething == HeldState.Imprisoned && f.Kind == FixtureKind.Cage) c.CageClips = f.Clips;

            // Matched from the original's keys: the held state is the one loaded, so they are written back as read (HeldSave.Write).
            if (c.Save is { } loaded && loaded.InWhat == what) loaded.InWhat = f.Id;

            // A loaded sleeper is put at its lie spot again (engine choice: a save from before LieSpot holds it at the bed's origin, half off the
            // mattress), still sleeping: a task other than Sleep would get it up (BedSystem). It plays the bed's clip as one laid in does (EnterBed);
            // the clip is not saved.
            if (c.InSomething == HeldState.Bed)
            {
                var (at, yaw) = LieSpot(f, next[s].Position, world.GameHours);
                table.Place(s, at, yaw, CharacterTask.Sleep);
                c.OperateClip = f.Bed.Clip;
            }
        }
    }

    /// <summary>
    /// The fixture a held character's <see cref="CharacterCold.InWhat"/> names: a fixture id, a bed the player built, or a generated interior cage or
    /// bed of the original's save.
    /// </summary>
    Fixture? HeldFixture(CharacterCold c, string what)
    {
        if (_byId.TryGetValue(what, out var f)) return f;

        // A standalone bed is saved as a building, including when it belongs to a town.
        if (c.InSomething == HeldState.Bed && _byId.TryGetValue(what + "@bed0", out f) && !f.Indoors) return f;
        if (SavedSpots?.GetValueOrDefault(what) is not { } spot) return null;
        return c.InSomething switch
        {
            HeldState.Imprisoned => InteriorCageAt(spot),
            HeldState.Bed => InteriorBedAt(spot),
            _ => null,
        };
    }

    /// <summary>
    /// The interior bed fixture with the spot's record standing at the spot (<see cref="SavedSpotReach"/>), or null.
    /// </summary>
    Fixture? InteriorBedAt(SavedSpot spot)
    {
        foreach (var b in BedSpots)
        {
            bool near = Vector3.DistanceSquared(b.Position, spot.Position) <= SavedSpotReach * SavedSpotReach;
            if (b.Record == spot.Record && near) return _byId.GetValueOrDefault(b.Id);
        }

        return null;
    }

    /// <summary>
    /// The interior cage fixture with the spot's record standing at the spot (<see cref="SavedSpotReach"/>), or null.
    /// </summary>
    Fixture? InteriorCageAt(SavedSpot spot)
    {
        foreach (var f in _cageList ??= [.. _all.Where(x => x.Kind == FixtureKind.Cage)])
        {
            bool near = Vector3.DistanceSquared(f.Position, spot.Position) <= SavedSpotReach * SavedSpotReach;
            if (f.Id != f.Building && f.RecordId == spot.Record && near) return f;
        }

        return null;
    }

    #endregion

    #region Doors

    /// <summary>
    /// Applies a squad template's <c>initial door state</c> to the doors of its home (docs/game/locks.md "Doors", <b>Verified</b>): 0 CLOSED unlocked
    /// and whole, 1 OPEN, 2 LOCKED and closed, 3 BROKEN (lock and door). Once per building (the first squad's), and not to doors restored from a
    /// save.
    /// </summary>
    public void ApplyInitialDoorState(string buildingInstance, int state)
    {
        if (!_byBuilding.TryGetValue(buildingInstance, out var list) || !_initialApplied.Add(buildingInstance)) return;
        foreach (var d in list)
        {
            if (d.Kind != FixtureKind.Door || d.Restored || d.Lock is not { } l) continue;
            switch (state)
            {
                case 1:
                    l.Locked = false;
                    l.Broken = false;
                    d.DoorBroken = false;
                    d.DoorProgress = 1;
                    d.Motion = DoorMotion.Open;
                    d.OpenAmount = 1;
                    break;
                case 2:
                    l.Locked = true;
                    l.Broken = false;
                    d.DoorBroken = false;
                    d.DoorProgress = 1;
                    d.Motion = DoorMotion.Closed;
                    d.OpenAmount = 0;
                    break;
                case 3:
                    l.Locked = false;
                    l.Broken = true;
                    d.DoorBroken = true;
                    d.DoorProgress = 0;
                    d.Motion = DoorMotion.Closed;
                    d.OpenAmount = 0;
                    break;
                default:
                    l.Locked = false;
                    l.Broken = false;
                    d.DoorBroken = false;
                    d.DoorProgress = 1;
                    d.Motion = DoorMotion.Closed;
                    d.OpenAmount = 0;
                    break;
            }
        }

        SyncDoors(buildingInstance);
    }

    /// <summary>
    /// Locks or unlocks a fixture outright (the owner's 3.3 s lock and unlock tasks end here; the timing is the caller's).
    /// </summary>
    public void SetLocked(Fixture fixture, bool locked)
    {
        if (fixture.Lock is not { } l) return;
        l.Locked = locked;
        l.PickTime = 0;
        l.NextRoll = LockState.FirstRoll;
        Touch(fixture);
    }

    /// <summary>
    /// Starts opening a closed door (the original's <c>FUN_140297040</c>, <b>Verified</b>: only from state 0; it tests no lock, its callers do).
    /// False when it was not closed.
    /// </summary>
    public static bool OpenDoor(Fixture door)
    {
        if (door.Kind != FixtureKind.Door || door.Motion != DoorMotion.Closed) return false;
        door.Motion = DoorMotion.Opening;
        return true;
    }

    /// <summary>
    /// Starts closing an open door (<c>FUN_140298c50</c>, <b>Verified</b>: only from state 1, and never a smashed door). False when it was not open.
    /// </summary>
    public static bool CloseDoor(Fixture door)
    {
        if (door.Kind != FixtureKind.Door || door.DoorBroken || door.Motion != DoorMotion.Open) return false;
        door.Motion = DoorMotion.Closing;
        door.AutoOpened = false;
        return true;
    }

    /// <summary>
    /// Moves every opening or closing door on by <c>door move speed</c> x <paramref name="dt"/> (the door's update, <c>FUN_140299030</c>,
    /// <b>Verified</b>): an opening one is open at 1, a closing one closed below 0, and then locked when it <c>wants lock</c> (<c>FUN_1402969f0</c>).
    /// </summary>
    public void StepDoors(float dt)
    {
        foreach (var f in _all)
        {
            if (f.Kind != FixtureKind.Door) continue;
            if (f.Motion == DoorMotion.Opening)
            {
                bool wasNavOpen = f.OpenAmount >= DoorLeaf.NavOpen;
                f.OpenAmount += dt * f.Leaf.Speed;
                if (f.OpenAmount >= 1)
                {
                    f.OpenAmount = 1;
                    f.Motion = DoorMotion.Open;
                }

                if (!wasNavOpen && f.OpenAmount >= DoorLeaf.NavOpen) SyncDoors(f.Building);   // the navmesh counts it open now, locked or not
            }
            else if (f.Motion == DoorMotion.Closing)
            {
                bool wasNavOpen = f.OpenAmount >= DoorLeaf.NavOpen;
                f.OpenAmount -= dt * f.Leaf.Speed;
                if (wasNavOpen && f.OpenAmount < DoorLeaf.NavOpen) SyncDoors(f.Building);
                if (f.OpenAmount >= 0) continue;
                f.OpenAmount = 0;
                f.Motion = DoorMotion.Closed;
                if (f.WantsLock && f.Lock is { } l)
                {
                    l.Locked = true;
                    Touch(f);
                }
            }
        }
    }

    /// <summary>
    /// Where a door's leaf is in the world (closed), asked of the door gate once; null without a gate or when it does not know the door.
    /// </summary>
    public DoorSpot? DoorSpotOf(Fixture door)
    {
        if (door.Kind != FixtureKind.Door) return null;
        if (door.SpotAsked) return door.Spot;
        if (_doors is null) return null;
        door.SpotAsked = true;
        if (_doors.DoorSpotOf(door.HostRecordId, door.DoorIndex) is { } local)
        {
            // The whole placement turn: a gate's leaf high on a tilted building (an Ancient Lab Ramp Gate, 231 up, placed 8-10° off level) landed
            // 30-40 units off its leaf with the yaw alone (#560). The normal stays horizontal.
            var turn = door.Rotation ?? Quaternion.CreateFromAxisAngle(Vector3.UnitY, door.Yaw);
            door.Spot = local with
            {
                Centre = door.Position + Vector3.Transform(local.Centre, turn),
                Normal = Level(Vector3.Transform(local.Normal, turn), local.Normal),
                Slide = Vector3.Transform(local.Slide, turn),
            };
        }

        return door.Spot;

        static Vector3 Level(Vector3 n, Vector3 upright)
        {
            if (n.Y == 0) return n;

            var flat = n with { Y = 0 };
            return flat.LengthSquared() > 1e-12f ? Vector3.Normalize(flat) : upright;
        }
    }

    /// <summary>
    /// Tells navigation after a door's lock changed (a pick, a break, an unlock).
    /// </summary>
    public void Touch(Fixture fixture)
    {
        if (fixture.Contents is not null) _containerSpots = null;   // its lock state is published with the spots
        if (fixture.Kind == FixtureKind.Door) SyncDoors(fixture.Building);
    }

    /// <summary>
    /// Tells the door gate whether the building's doors block paths: all closed, locked and whole.
    /// </summary>
    void SyncDoors(string building)
    {
        if (_doors is null || !_byBuilding.TryGetValue(building, out var list)) return;
        bool any = false, blocked = true;
        foreach (var d in list)
        {
            if (d.Kind != FixtureKind.Door) continue;
            any = true;
            if (d.DoorBroken || d.OpenAmount >= DoorLeaf.NavOpen || d.Lock is not { EffectivelyLocked: true })
                blocked = false;
        }

        if (any) _doors.SetDoorsBlocked(building, blocked);
    }

    /// <summary>
    /// The building is shut for paths: it has doors, every one effectively locked and none broken (the test <see cref="Doors"/> is told).
    /// </summary>
    public bool IsShut(string building)
    {
        if (!_byBuilding.TryGetValue(building, out var list)) return false;
        bool any = false;
        foreach (var d in list)
        {
            if (d.Kind != FixtureKind.Door) continue;
            any = true;
            if (d.DoorBroken || d.Lock is not { EffectivelyLocked: true }) return false;
        }

        return any;
    }

    /// <summary>
    /// The building has doors and all of them are locked (effectively or not; a broken one counts as open). Night rounds test this, not
    /// <see cref="IsShut"/>.
    /// </summary>
    public bool AllDoorsLocked(string building)
    {
        if (!_byBuilding.TryGetValue(building, out var list)) return false;
        bool any = false;
        foreach (var d in list)
        {
            if (d.Kind != FixtureKind.Door) continue;
            any = true;
            if (d.DoorBroken || d.Lock is not { Locked: true, Broken: false }) return false;
        }

        return any;
    }

    /// <summary>
    /// The building has a door open, opening or broken, or no doors (<c>Building::hasAnOpenDoor</c> as the steal roll reads it).
    /// </summary>
    public bool HasOpenDoor(string building)
    {
        if (!_byBuilding.TryGetValue(building, out var list)) return true;
        bool any = false;
        foreach (var d in list)
        {
            if (d.Kind != FixtureKind.Door) continue;
            any = true;
            if (d.DoorBroken || d.Motion is DoorMotion.Open or DoorMotion.Opening) return true;
        }

        return !any;
    }

    /// <summary>
    /// The faction that owns a building's doors (-1: no doors).
    /// </summary>
    public int DoorOwner(string building)
    {
        if (_byBuilding.TryGetValue(building, out var list))
        {
            foreach (var d in list)
                if (d.Kind == FixtureKind.Door) return d.Owner;
        }

        return -1;
    }

    /// <summary>
    /// Locks or unlocks every door of a building at once (the end of the owner's lock/unlock tasks; their 3.3 s are the caller's); locking closes an
    /// open door first (docs/game/locks.md "Doors"). False when the building has no doors.
    /// </summary>
    public bool SetDoorsLocked(string building, bool locked)
    {
        if (!_byBuilding.TryGetValue(building, out var list)) return false;
        bool any = false;
        foreach (var d in list)
        {
            if (d.Kind != FixtureKind.Door || d.Lock is not { } l) continue;
            any = true;
            if (locked && d.Motion is DoorMotion.Open or DoorMotion.Opening)
            {
                d.Motion = DoorMotion.Closing;
                d.AutoOpened = false;
            }   // the leaf slides shut; the lock holds at once
            l.Locked = locked;
            l.PickTime = 0;
            l.NextRoll = LockState.FirstRoll;
        }

        if (any) SyncDoors(building);
        return any;
    }

    #endregion

    #region Cages and beds

    /// <summary>
    /// The factor on a building's reach while the character is not moving (<c>DAT_141682ad0</c>, read from the exe).
    /// </summary>
    public const float StillReachFactor = 2;

    /// <summary>
    /// The height difference below which a character is on a cage's floor when it is not on the same floor number (<c>AI::isWithinChoppingRange</c>:
    /// 15 for a building with a containing building; the 45 of an outdoor one is not modelled).
    /// </summary>
    public const float WorkHeight = 15;

    /// <summary>
    /// How far (X/Z) the bed search looks: the original takes the beds of the character's town, or of the nearest town within this distance
    /// (<c>FUN_1408cbea0</c>: squared 1.5625e7); Meitou has no player towns, so it takes the beds within it (<b>engine choice</b>).
    /// </summary>
    public const float BedSearchRange = 3952.85f;

    /// <summary>
    /// The bed weight factor for a bed inside town walls (<c>amInsideTownWalls</c> above 0; <c>DAT_14168bf24</c> = 4.0, <b>Verified</b>,
    /// <c>FUN_1408cfe00</c>).
    /// </summary>
    public const float InsideWallsBedFactor = 4;

    /// <summary>
    /// Raised (serially) by every <see cref="Imprison"/> once the captive is held and its sentence set: where a captive's bounty is claimed
    /// (economy.md 11.4).
    /// </summary>
    public event Action<World, Imprisonment>? Caged;

    /// <summary>
    /// The bed clip Task_GetOutOfBed plays backwards before leaving (<c>FUN_140345760</c> compares the bed's clip name with it, <b>Verified</b>).
    /// </summary>
    public const string GoToBedClip = "gotobed";

    /// <summary>
    /// How far round its lie spot one getting out of bed looks for floor (engine choice: a bunk on a deck leaves a hole of 16 or more round its use
    /// node).
    /// </summary>
    const float BedFloorReach = 40;

    /// <summary>
    /// Whether a character is at a cage to use it (goal 108's <c>isAtLocation</c> for a building, <c>AI::isWithinChoppingRange</c>
    /// <c>FUN_14059c6d0</c>:114-122, docs/game/buildings-production.md "Work distance"): its flat distance to the cage's position is below the
    /// functionality's <c>use range</c>, doubled while it is not moving, and it is less than 15 above or below it (the original's same-floor test has
    /// no counterpart here: Meitou's cage has no floor of its own).
    /// </summary>
    public static bool AtCage(Fixture cage, Vector3 character, bool moving)
    {
        float reach = cage.UseRange * (moving ? 1 : StillReachFactor);
        return Vector2.Distance(new(character.X, character.Z), new(cage.Position.X, cage.Position.Z)) < reach
                && MathF.Abs(character.Y - cage.Position.Y) < WorkHeight;
    }

    /// <summary>
    /// Whether nobody is in it: no occupant, or one that is gone or no longer held there.
    /// </summary>
    public static bool IsFree(CharacterTable table, Fixture f) =>
        f.Occupant.IsNone || !table.TryResolveNext(f.Occupant, out int s) || table.Cold(s) is not { } held || held.InWhat != f.Id
                || held.InSomething == HeldState.None;

    /// <summary>
    /// The cage <paramref name="carrier"/> at <paramref name="at"/> puts a captive in (the original's finder <c>FUN_1408d0770</c>, economy.md 11.7,
    /// <b>Verified</b>): every free cage weighs clamp(1 - d^2 / 4608^2, 1e-5, 1), x100 when of the carrier's faction (any other must pass
    /// <see cref="CanUse"/> or is left out, except for a player character: <c>canIUseThisBuilding</c> belongs to the AI's Ownerships, engine
    /// reading), x100 when in <paramref name="home"/> (a building instance id), and x1e-5 for a law enforcer
    /// (<see cref="Crime.CrimeSystem.IsPoliceLike"/>) when the cage stands inside a building that is not open to the public (<see cref="IsPublic"/>);
    /// the highest wins, the first registered on a tie. <paramref name="skip"/> leaves out reserved cages, and <see cref="Reachable"/> the ones the
    /// carrier cannot get to. Null when none is free. With <paramref name="occupied"/> it is the chooser's other mode (the kill and eat goals' finder
    /// <c>FUN_1408d0d90</c> calls it with 0): the occupied cages, the same weights, no reach test.
    /// </summary>
    public Fixture? FindCage(World world, CharacterCold carrier, Vector3 at, string? home = null, Func<Fixture, bool>? skip = null,
            bool occupied = false)
    {
        Fixture? best = null;
        float bestWeight = 0;
        int faction = carrier.Faction;
        bool police = false, policeKnown = false;
        foreach (var f in _all)
        {
            if (f.Kind != FixtureKind.Cage || IsFree(world.Characters, f) == occupied || !occupied && !Reachable(world, f, faction)
                    || skip?.Invoke(f) == true)
                continue;

            bool own = faction >= 0 && f.Owner == faction;
            if (!own && !carrier.IsPlayer && !CanUse(world, carrier, f)) continue;   // an AI ownership test: the player's characters may use any cage
            float d2 = Vector2.DistanceSquared(new Vector2(at.X, at.Z), new Vector2(f.Position.X, f.Position.Z));
            float w = Math.Clamp(1 - d2 / (CageSearchScale * CageSearchScale), 1e-5f, 1);
            if (own) w *= 100;
            if (home is not null && f.Building == home) w *= 100;
            if (IsInside(f) && !IsPublic(f.Building))
            {
                if (!policeKnown)
                    (police, policeKnown) = (world.SystemOf<CrimeSystem>()?.IsPoliceLike(world, carrier) == true, true);

                if (police) w *= NotPublicPolice;
            }

            if (w > bestWeight) (best, bestWeight) = (f, w);
        }

        return best;
    }

    /// <summary>
    /// Whether a carrier of <paramref name="faction"/> can get to a fixture (<b>engine choice</b>; the original's finder tests are not decoded): not
    /// when its building is shut (<see cref="IsShut"/>) and its doors are another faction's (an owner may unlock its own on the way), and not while a
    /// walk of that faction there is known to fail (<see cref="MarkUnreachable"/>: no path query may run inside a tick, so a failed walk stands in
    /// for one).
    /// </summary>
    public bool Reachable(World world, Fixture f, int faction)
    {
        if (IsShut(f.Building) && DoorOwner(f.Building) != faction) return false;
        return _unreachable.Count == 0 || !_unreachable.TryGetValue((f.Id, faction), out double until) || until <= world.GameHours;
    }

    /// <summary>
    /// Walks of <paramref name="faction"/> to the fixture keep failing: <see cref="Reachable"/> is false for <paramref name="hours"/> game hours
    /// (engine choice). Serial phases only.
    /// </summary>
    public void MarkUnreachable(World world, Fixture f, int faction, double hours = 24)
    {
        List<(string, int)>? expired = null;
        foreach (var (key, until) in _unreachable)
            if (until <= world.GameHours) (expired ??= []).Add(key);
        if (expired is not null)
            foreach (var key in expired) _unreachable.Remove(key);
        _unreachable[(f.Id, faction)] = world.GameHours + hours;
    }

    /// <summary>
    /// The bed within <paramref name="range"/> of <paramref name="at"/>, taken or not (where a sleeper lies: an AI asleep at its home's bed spot is
    /// not held in it).
    /// </summary>
    public Fixture? BedAt(Vector3 at, float range)
    {
        Fixture? best = null;
        float bestD = range * range;
        foreach (var f in _all)
        {
            if (f.Kind != FixtureKind.Bed) continue;
            float d2 = Vector3.DistanceSquared(at, f.Position);
            if (d2 <= bestD) (best, bestD) = (f, d2);
        }

        return best;
    }

    /// <summary>
    /// The bed search of a player-faction character (the original's <c>FUN_1408cfe00</c>, docs/game/locks.md "Beds", <b>Observed</b>): the bed it
    /// lies in; else among the complete BF_BED (6; a skeleton bed is not searched) beds that are free for it (<see cref="IsFree"/>) and
    /// <see cref="Reachable"/> (standing in for the original's give-up list), a bed of its own faction weighs 0.5, one of a faction that counts it as
    /// an ally (<paramref name="relations"/>, ally(bed's faction, character's)) 0.01, any other none; x3 when the bed needs more than one
    /// construction material, x2 indoors, x4 inside town walls (<see cref="InsideWallsBedFactor"/>). The heaviest wins, the first registered on a
    /// tie. The candidates are the beds within <see cref="BedSearchRange"/> (engine, for the town); <paramref name="skip"/> leaves out beds others
    /// are on their way to. Null when none.
    /// </summary>
    public Fixture? FindBed(World world, int slot, FactionRelations? relations, Func<Fixture, bool>? skip = null)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { } c) return null;
        if (c is { InSomething: HeldState.Bed, InWhat: { } inBed } && Find(inBed) is { } own) return own;
        var at = table.Next[slot].Position;
        if (!c.IsPlayer) return FindBedNpc(world, slot, c, skip);
        Fixture? best = null;
        float bestWeight = 0, r2 = BedSearchRange * BedSearchRange;
        var regions = world.SystemOf<LockSystem>()?.Regions;
        foreach (var f in _all)
        {
            if (f.Kind != FixtureKind.Bed || f.Bed.Function != TownBuildings.BedFunction || f.Owner < 0) continue;
            float weight = f.Owner == c.Faction ? 0.5f : relations is not null && relations.Contains(f.Owner) && relations.Contains(c.Faction)
                    && relations.IsAlly(f.Owner, c.Faction) ? 0.01f : 0;
            if (weight <= 0 || Vector2.DistanceSquared(new Vector2(at.X, at.Z), new Vector2(f.Position.X, f.Position.Z)) > r2)
                continue;

            if (!IsFree(table, f) || !Reachable(world, f, c.Faction) || skip?.Invoke(f) == true) continue;
            if (f.Bed.Materials > 1) weight *= 3;
            if (f.Indoors) weight *= 2;
            if (regions?.CodeAt(f.Position) is > 0) weight *= InsideWallsBedFactor;
            if (weight > bestWeight) (best, bestWeight) = (f, weight);
        }

        return best;
    }

    /// <summary>
    /// The bed search of a character that is not the player's (the other half of <c>FUN_1408cfe00</c>, docs/game/locks.md "Beds", <b>Observed</b>):
    /// the first free bed (the give-up list stands in as <see cref="Reachable"/>) of its squad's home building; with none there, those of the town's
    /// "designated" buildings of two kinds nearest it, then any BF_BED building near it. The original takes the first in its building list's order (a
    /// set of pointers, or the finder's cell order) and, when the squad's home is not the building the character is in, passes over a bed with a cost
    /// above 1 (<b>Unknown</b> field); Meitou has no designated buildings, takes the nearest free bed of its town for the last two steps, and has no
    /// cost test (engine choices).
    /// </summary>
    Fixture? FindBedNpc(World world, int slot, CharacterCold c, Func<Fixture, bool>? skip)
    {
        var table = world.Characters;
        var squad = world.Squads.Find(c.SquadId);
        var at = table.Next[slot].Position;
        bool Usable(Fixture f) => f.Kind == FixtureKind.Bed && f.Bed.Function == TownBuildings.BedFunction && IsFree(table, f)
                && Reachable(world, f, c.Faction) && skip?.Invoke(f) != true;
        if (squad?.HomeBuilding is { } home)
        {
            foreach (var f in OfBuilding(home.Key))
                if (Usable(f)) return f;
        }

        Fixture? best = null;
        float bestD = float.MaxValue;
        foreach (var f in _all)
        {
            if (!Usable(f) || squad is { Town: >= 0 } && f.Site != squad.Town) continue;
            float d = Vector2.DistanceSquared(new Vector2(at.X, at.Z), new Vector2(f.Position.X, f.Position.Z));
            if (d < bestD) (best, bestD) = (f, d);
        }

        return best;
    }

    /// <summary>
    /// The sentence in game hours a bounty earns (economy.md 11.5, <b>Observed</b>): trunc(bounty x 0.02 x <c>prison time</c>), 999999 above 25000.
    /// </summary>
    public static float SentenceHours(int bounty, float prisonTime) =>
        bounty > LifeBounty ? LifeSentence : (float)Math.Truncate(bounty * 0.02 * prisonTime);

    /// <summary>
    /// The sentence is served (start + length not after <paramref name="nowHours"/>; one without a sentence always is).
    /// </summary>
    public static bool SentenceServed(CharacterCold c, double nowHours) => !c.Kidnapped && c.SlaveState == 0
            && (c.SentenceLength <= 0 || c.SentenceStart + c.SentenceLength <= nowHours);

    /// <summary>
    /// Puts a character in a free cage (the original's enter, locks.md "Cages and beds"): imprisoned state, held at the cage's position and facing,
    /// orders and path dropped, the cage locked. A captive with a bounty for the cage owner's faction gets a sentence from now
    /// (<see cref="SentenceHours"/>); one without gets none (served at once, the engine reading of economy.md 11.5). The caller lets go of a carried
    /// captive first and names it as <paramref name="carrier"/> (None: put in by the system). False when the cage is taken or the character is held,
    /// carried or carrying. Every caging is kept for its tick (<see cref="CagingsOf"/>) and raised as <see cref="Caged"/>: the hook of the bounty
    /// claim.
    /// </summary>
    public bool Imprison(World world, CharacterId prisoner, Fixture cage, float prisonTime = 1, CharacterId? carrier = null)
    {
        var table = world.Characters;
        if (cage.Kind != FixtureKind.Cage || !IsFree(table, cage) || !table.TryResolveNext(prisoner, out int s))
            return false;

        var c = table.Cold(s)!;
        if (c.InSomething != HeldState.None || !c.CarriedBy.IsNone || !c.Carrying.IsNone) return false;

        // the task and a fight end through their handlers: a caged character works no machine, site or bed
        TaskStart.End(world, s, TaskEnd.Stopped);
        c.InSomething = HeldState.Imprisoned;
        c.InWhat = cage.Id;
        c.CageClips = cage.Clips;
        c.Escaped = false;
        cage.Occupant = prisoner;
        if (cage.Lock is { } l)
        {
            l.Locked = true;
            l.PickTime = 0;
            l.NextRoll = LockState.FirstRoll;
        }

        Hold(table, s, c, cage.Position, cage.Yaw);
        SetSentence(world, c, cage, prisonTime);

        // FUN_140330600 assigns the cage's owner handle as well as slave state 1. The resident squad stands in for that handle here.
        if (c.SlaveOwner >= 0 && cage.Owner >= 0) Enslave(world, c, cage.Owner, CageMaster(world, cage));

        NoteCaging(world, prisoner, cage, carrier);
        return true;
    }

    /// <summary>
    /// A caged character's sentence from the captor faction's booked bounty, or no sentence when it has none.
    /// </summary>
    static void SetSentence(World world, CharacterCold c, Fixture cage, float prisonTime)
    {
        if (world.SystemOf<CrimeSystem>() is { } law && c.Bounties?.BookedFor(law.Factions.Relations, cage.Owner) is { Amount: > 0 } entry)
        {
            c.SentenceStart = world.GameHours;
            c.SentenceLength = SentenceHours(entry.Amount, prisonTime);
        }
        else
        {
            c.SentenceStart = 0;
            c.SentenceLength = 0;
        }
    }

    /// <summary>
    /// Records a caging for the rest of this tick and notifies the captive and crime systems.
    /// </summary>
    void NoteCaging(World world, CharacterId prisoner, Fixture cage, CharacterId? carrier)
    {
        if (_cagingsTick != world.Tick)
        {
            _cagings.Clear();
            _cagingsTick = world.Tick;
        }

        var caging = new Imprisonment(world.Tick, carrier ?? CharacterId.None, prisoner, cage);
        _cagings.Add(caging);
        Caged?.Invoke(world, caging);
    }

    /// <summary>
    /// The resident squad that owns a cage's building; -1 when its owner has no loaded squad (the existing slave master is kept).
    /// </summary>
    static int CageMaster(World world, Fixture cage)
    {
        foreach (var squad in world.Squads.All)
        {
            if (squad.Faction == cage.Owner && (squad.Ai?.OwnHome ?? squad.HomeBuilding)?.Key == cage.Building)
                return squad.Id;
        }

        return -1;
    }

    /// <summary>
    /// The cagings of tick <paramref name="tick"/> (only the latest tick's are kept: a system placed after the caging one reads them in the same
    /// tick, as <see cref="CrimeSystem.BookingsOf"/>).
    /// </summary>
    public IReadOnlyList<Imprisonment> CagingsOf(long tick) => _cagingsTick == tick ? _cagings : [];

    /// <summary>
    /// The occupant of an unlocked cage is lifted out by the character in <paramref name="lifter"/> (locks.md "Cages and beds", <c>FUN_14034fbe0</c>,
    /// <b>Observed</b>): it is no longer held (the carry link is the caller's). Out of the cage of another faction than the lifter's, the lifter gets
    /// a pending code 7 against the cage owner and the freed one a pending Escaping Prison and the escaped flag. False when it is not imprisoned or
    /// its cage is effectively locked. <paramref name="stepOut"/>: no one carries it (a gatherer's take-out), so it stands at the cage's
    /// <see cref="ExitSpot"/>, as every hold clear but the shoulder lift puts it (<c>FUN_140330600</c> -> <c>FUN_140661f90</c>).
    /// </summary>
    public bool LiftOut(World world, CharacterId prisoner, int lifter, CrimeSystem? crimes = null, bool stepOut = false)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(prisoner, out int s) || table.Cold(s) is not { IsImprisoned: true } c || table.Cold(lifter) is not { } lc)
            return false;

        var cage = c.InWhat is { } id ? Find(id) : null;
        if (cage?.Lock is { EffectivelyLocked: true }) return false;
        bool foreign = cage is { Owner: >= 0 } && cage.Owner != lc.Faction;
        if (foreign && crimes is not null)
        {
            crimes.SetPending(world, lifter, CrimeCode.Terrorism, cage!.Owner, prisoner, CrimeSystem.MinTimer);
            crimes.SetPending(world, s, CrimeCode.EscapingPrison, cage.Owner, CharacterId.None, CrimeSystem.MinTimer);
        }

        if (cage is not null && cage.Occupant == prisoner) cage.Occupant = CharacterId.None;
        bool kidnapped = c.Kidnapped;   // a kidnap victim lifted out is no escapee (the lifter's crime stays)
        if (stepOut && cage is not null) StepOut(table, s, cage);   // the cage's middle is a pocket no path leaves
        Clear(c);
        c.Escaped = foreign && !kidnapped;
        return true;
    }

    /// <summary>
    /// Keeps every caged character at its cage's position and facing (the original's character update <c>FUN_1405cf130</c> sets both from the
    /// building every frame while the character is held in one, docs/game/locks.md "Facing in a cage"): a prisoner that came from a save or a spawn,
    /// or turned by anything else, faces the cage's local +Z (its door side) again. Only the place is written (the original touches no path or order
    /// there, <b>Verified</b>); the sleepers are held the same way (<see cref="HoldSleepers"/>). Serial phases only.
    /// </summary>
    public void HoldCaged(World world)
    {
        var table = world.Characters;
        foreach (var f in _cageList ??= [.. _all.Where(x => x.Kind == FixtureKind.Cage)])
        {
            if (IsFree(table, f) || !table.TryResolveNext(f.Occupant, out int s) || table.Cold(s) is not { InSomething: HeldState.Imprisoned } c
                    || c.InWhat != f.Id)
                continue;

            ref var n = ref table.Next[s];
            if (n.Position != f.Position || n.Yaw != f.Yaw) table.Place(s, f.Position, f.Yaw);
        }

        HoldSleepers(world);
    }

    /// <summary>
    /// Re-places every sleeper at the bed's use node nearest where it is now, each update (<c>FUN_1405cf130</c> mode 1: the bed's position and
    /// direction markers are asked with the character's current position, <c>FUN_14054b850</c>, and written to it, <b>Verified</b>): a sleeper moved
    /// off its node by anything else lies on the nearest one again.
    /// </summary>
    void HoldSleepers(World world)
    {
        var table = world.Characters;
        foreach (var f in _bedList ??= [.. _all.Where(x => x.Kind == FixtureKind.Bed)])
        {
            if (f.Occupant.IsNone || !table.TryResolveNext(f.Occupant, out int s) || table.Cold(s) is not { InSomething: HeldState.Bed } c
                    || c.InWhat != f.Id)
                continue;

            c.BedUsedHours = world.GameHours;   // Task_UseBed's update stamps the status clock (+0xc0)
            ref var n = ref table.Next[s];
            var (at, yaw) = LieSpot(f, n.Position, world.GameHours);
            if (n.Position == at && n.Yaw == yaw) continue;
            (byte task, var time) = (n.Task, n.TaskTime);
            table.Place(s, at, yaw);
            (n.Task, n.TaskTime) = (task, time);   // Place stands it; a sleeper stays asleep
        }
    }

    /// <summary>
    /// Puts a character leaving <paramref name="cage"/> at its <see cref="ExitSpot"/>, facing as it did; it stays where it is when the cage has no
    /// use node.
    /// </summary>
    static void StepOut(CharacterTable table, int slot, Fixture cage)
    {
        if (ExitSpot(cage) is { } at) table.Place(slot, at, table.Next[slot].Yaw);
    }

    /// <summary>
    /// Puts a character into its fixture: placed there, standing, its path and orders dropped.
    /// </summary>
    static void Hold(CharacterTable table, int slot, CharacterCold c, Vector3 at, float yaw)
    {
        table.Place(slot, at, yaw);
        TaskStart.Step(table, slot, CharacterTask.Idle);   // the teleport stands it
        c.Path = [];
        c.OrderQueue.Clear();
    }

    /// <summary>
    /// Frees a prisoner (Task_ReleasePrisoner, economy.md 11.5, <b>Observed</b>): the cage is unlocked and emptied, the state and sentence cleared
    /// (not the slave state: a slave released by a faction other than the player's becomes that faction's slave); when the releaser's faction books
    /// bounties its entry and the jail's (the cage owner's) are erased. A player-faction prisoner gets "{1} has been released from prison.". The
    /// prisoner is put in front of the cage (<see cref="ExitSpot"/>). False when the character is not imprisoned.
    /// </summary>
    public bool Release(World world, CharacterId prisoner, int releaserFaction = -1, CrimeSystem? crimes = null)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(prisoner, out int s) || table.Cold(s) is not { IsImprisoned: true } c) return false;
        var cage = c.InWhat is { } id ? Find(id) : null;
        if (cage is not null)
        {
            if (cage.Lock is { } l) l.Locked = false;
            if (cage.Occupant == prisoner) cage.Occupant = CharacterId.None;
            StepOut(table, s, cage);
        }

        if (releaserFaction >= 0 && crimes is not null && crimes.Factions.Books(releaserFaction) && c.Bounties is { } ledger)
        {
            if (ledger.BookedFor(crimes.Factions.Relations, releaserFaction) is { } a) ledger.Remove(a);
            if (cage is not null && ledger.BookedFor(crimes.Factions.Relations, cage.Owner) is { } b) ledger.Remove(b);
            if (ledger.Count == 0) c.Bounties = null;
        }

        Clear(c);

        // Task_ReleasePrisoner adds the leave task 133 (flag 0, which clears the escaped flag for a real cage owner, FUN_1403554b0) only for a
        // releaser whose faction books bounties or is the player's (FUN_1403570a0); another NPC's release leaves the flag as it was. Releases that
        // are no task of the jailer's (bail, trade, recruit, liberators) clear it as before.
        bool leaves = releaserFaction < 0 || releaserFaction == world.Player.Faction || crimes is null || crimes.Factions.Books(releaserFaction);
        if (leaves && !(cage is not null && crimes?.Factions.NotReal(cage.Owner) == true)) c.Escaped = false;

        // The release leaves slavery as it is (FUN_1403570a0 never sets the slave state; bail and buy-free clear it themselves, Verified); a releaser
        // that is not the player's takes a slave over (FUN_14032e430: the owner changes, the state stays).
        if (releaserFaction >= 0 && releaserFaction != world.Player.Faction && c.SlaveState != 0)
            c.SlaveOwner = releaserFaction;

        if (c.IsPlayer) world.Post(Loc.Tr("{1} has been released from prison.", c.Name));
        return true;
    }

    /// <summary>
    /// The prisoner leaves its cage by itself (Task_GetOutOfCage <c>FUN_1403554b0</c>, locks.md "Cages and beds", <b>Verified</b>): refused while the
    /// cage is effectively locked. The task flag is 1 for the player's Get out (207) and the opportunistic walk (256), so it is an escape whatever
    /// the sentence (a pending Escaping Prison against the cage owner, which the crime setter refuses for a lawless owner, and a slave in state 1
    /// becomes an escaped slave); the escaped flag <see cref="CharacterCold.Escaped"/> is written only for a cage owner that is real, a not-real
    /// owner leaves it as it was. A kidnap victim getting out is no escape (engine choice). True when it got out.
    /// </summary>
    public bool GetOut(World world, CharacterId prisoner, CrimeSystem? crimes = null)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(prisoner, out int s) || table.Cold(s) is not { IsImprisoned: true } c) return false;
        var cage = c.InWhat is { } id ? Find(id) : null;
        if (cage?.Lock is { EffectivelyLocked: true }) return false;
        bool escape = cage is not null && !c.Kidnapped;
        if (cage is not null && cage.Occupant == prisoner) cage.Occupant = CharacterId.None;
        if (cage is not null) StepOut(table, s, cage);
        Clear(c);
        if (!(cage is not null && crimes?.Factions.NotReal(cage.Owner) == true)) c.Escaped = escape;
        if (escape && c.SlaveState == 1) SetSlave(world, c, 2, c.SlaveOwner);   // a slave getting out is an escaped slave (Task_GetOutOfCage)

        // The crime is set once the hold is cleared: the setter refuses an imprisoned culprit (FUN_1403554b0 sets it after FUN_140330600,
        // FUN_140852c80).
        if (escape)
            crimes?.SetPending(world, s, CrimeCode.EscapingPrison, cage!.Owner, CharacterId.None, CrimeSystem.MinTimer);

        return true;
    }

    /// <summary>
    /// Lies a character down in a free bed (state 1, held at its <see cref="LieSpot"/>; beds have no lock; the original's <c>FUN_14032e2f0</c>): its
    /// task becomes <see cref="CharacterTask.Sleep"/> with the bed's clip (<see cref="CharacterCold.OperateClip"/>), its orders and path dropped.
    /// True when it lies there already. False when the bed is taken or the character is held elsewhere, carried or carrying.
    /// </summary>
    public bool EnterBed(World world, CharacterId sleeper, Fixture bed)
    {
        var table = world.Characters;
        if (bed.Kind != FixtureKind.Bed || !table.TryResolveNext(sleeper, out int s)) return false;
        var c = table.Cold(s)!;
        if (c.InSomething == HeldState.Bed && c.InWhat == bed.Id) return true;
        if (!IsFree(table, bed) || c.InSomething != HeldState.None || !c.CarriedBy.IsNone || !c.Carrying.IsNone)
            return false;

        c.InSomething = HeldState.Bed;
        c.InWhat = bed.Id;
        bed.Occupant = sleeper;
        TaskStart.Stand(table, s);
        var (at, yaw) = LieSpot(bed, table.Next[s].Position, world.GameHours);
        Hold(table, s, c, at, yaw);
        TaskStart.Step(table, s, CharacterTask.Sleep);
        table.Next[s].Velocity = Vector3.Zero;
        c.OperateClip = bed.Bed.Clip;
        return true;
    }

    /// <summary>
    /// Lets a character go from whatever holds it, a bed or a cage, with no lock or crime test (the carry link of a restored save,
    /// <c>FUN_1405ced90</c>, <b>Verified</b>).
    /// </summary>
    public void ReleaseHeld(World world, CharacterId held)
    {
        if (!world.Characters.TryResolveNext(held, out int s) || world.Characters.Cold(s) is not { InSomething: not HeldState.None } c)
            return;

        if (c.InWhat is { } id && Find(id) is { } fixture && fixture.Occupant == held)
            fixture.Occupant = CharacterId.None;

        Clear(c);
    }

    /// <summary>
    /// <c>Task_GetOutOfBed</c> (<c>FUN_140345760</c> start, <c>FUN_140348480</c> update, <b>Verified</b>): a bed whose clip is
    /// <see cref="GoToBedClip"/> keeps the sleeper while the clip plays backwards (<see cref="CharacterCold.LeavingBed"/>, the animation system sets
    /// <see cref="CharacterCold.BedExitDone"/> under a tenth of the clip), then it leaves; any other bed (every base one: <c>sleepinbed</c>) is left
    /// at once (<see cref="LeaveBed"/>). True once the character is out.
    /// </summary>
    public bool GetOutOfBed(World world, CharacterId sleeper)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(sleeper, out int s) || table.Cold(s) is not { InSomething: HeldState.Bed } c)
            return true;

        bool backwards = c.InWhat is { } id && Find(id) is { } bed && bed.Bed.Clip == GoToBedClip;
        if (!backwards || c.BedExitDone) return LeaveBed(world, sleeper);
        c.LeavingBed = true;
        return false;
    }

    /// <summary>
    /// Gets a character out of its bed (the original's <c>FUN_140345620</c>): the bed is free and the state cleared; it stands where it lay, or
    /// beside the bed when the bed cuts the floor away under it (<see cref="StandBeside"/>).
    /// </summary>
    public bool LeaveBed(World world, CharacterId sleeper)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(sleeper, out int s) || table.Cold(s) is not { InSomething: HeldState.Bed } c)
            return false;

        if (c.InWhat is { } id && Find(id) is { } bed && bed.Occupant == sleeper) bed.Occupant = CharacterId.None;
        Clear(c);
        StandBeside(world, s);
        return true;
    }

    /// <summary>
    /// A bed's furniture cuts a hole in the floor's navmesh, and its use node may lie well inside it: a sleeper left there would stand on the storey
    /// or ground below (engine choice; how the original's body finds the floor is not mapped). It is put on the nearest floor point at its own height
    /// instead.
    /// </summary>
    static void StandBeside(World world, int slot)
    {
        var at = world.Characters.Next[slot].Position;
        if (world.Walkability.FloorNear(at.X, at.Z, at.Y, MovementSystem.FloorSlack) is not null) return;
        if (IWalkability.ScanFloor(world.Walkability, at.X, at.Z, at.Y, MovementSystem.FloorSlack, BedFloorReach) is not { } floor)
            return;

        world.Characters.Next[slot].Position = floor;   // only the place: an order that got it up keeps its task and path
    }

    /// <summary>
    /// Makes a character <paramref name="owner"/>'s slave (state 1): "{1} has been enslaved" for one of the player's faction that was free
    /// (<c>FUN_1405a4940</c>, <b>Observed</b>).
    /// </summary>
    /// <paramref name="master"/>: the squad it obeys (<see cref="CharacterCold.SlaveMaster"/>), -1 to keep the one it has.
    public static void Enslave(World world, CharacterCold c, int owner, int master = -1)
    {
        bool handed = master >= 0 && c.SlaveState == 1 && c.SlaveMaster != master;
        if (master >= 0) c.SlaveMaster = master;
        SetSlave(world, c, 1, owner);

        // Handed to another master (engine choice: the original resets and re-adds job 187 where it chains or processes, FUN_14032e590,
        // FUN_14035aa20): its job names the new one.
        if (handed)
        {
            Kidnap.ObedientSlave.Reset(world, c);
            c.Save?.DropSlaver();   // the save's owner character is not its owner any more
        }
    }

    /// <summary>
    /// Clears a character's slave state and owner (state 0) without DA 53's "Free!" (a slave bought free, <c>FUN_1406add20</c>).
    /// </summary>
    public static void ClearSlave(World world, CharacterCold c) => SetSlave(world, c, 0, -1);

    /// <summary>
    /// <c>DA_TAG_ESCAPED_SLAVE</c> (25): the character becomes an escaped slave (state 2), keeping its owner (<c>FUN_140680560</c>).
    /// </summary>
    public static void TagEscaped(World world, CharacterCold c) => SetSlave(world, c, 2, c.SlaveOwner);

    /// <summary>
    /// <c>DA_REMOVE_SLAVE_STATUS</c> (53, <c>FUN_140680560</c>): a character in shackles or slave state is freed with "Free!" (the original floats it
    /// in green over the head) and <c>Character_Unlock_Success</c>; its slave state is cleared and its worn shackles unlocked.
    /// </summary>
    public static void FreeSlave(World world, int slot, CharacterCold c)
    {
        var shackles = LockSystem.WornShackles(c);
        if (shackles is null && c.SlaveState == 0) return;
        world.Bark(world.Characters.IdOf(slot), Loc.Tr("Free!"));
        world.EmitSound(SoundKind.Unlocked, slot, world.Characters.Next[slot].Position);
        SetSlave(world, c, 0, -1);
        if (shackles?.Lock is { } l) l.Locked = false;
    }

    /// <summary>
    /// The slave state setter (<c>FUN_1405a4940</c>, <b>Observed</b>): free to slave posts "{1} has been enslaved" for a player-faction character;
    /// becoming an escaped slave sets the recently-wanted marker to 250 (status <c>+0x110</c>), so a looker who knows recognises it at once; a change
    /// to a state above 1 stamps the game hours (status <c>+0x8</c>, <see cref="CharacterCold.SlaveSince"/>; docs/game/carry-loot.md "Escaped slaves
    /// lie low").
    /// </summary>
    static void SetSlave(World world, CharacterCold c, byte state, int owner)
    {
        byte before = c.SlaveState;
        if (c.SlaveState == 0 && state == 1 && (c.IsPlayer || world.Player.Faction >= 0 && c.Faction == world.Player.Faction))
            world.Post(Loc.Tr("{1} has been enslaved", c.Name));

        if (state == 2) c.RecentlyWanted = CrimeSystem.RecentlyWantedSeconds;
        if (state > 1 && before != state) c.SlaveSince = world.GameHours;
        c.SlaveState = state;
        c.SlaveOwner = state == 0 ? -1 : owner;
        if (state == 0) c.SlaveMaster = -1;
        if (state == 0 && before != 0) c.Save?.DropSlaver();

        // The state setter then fixes the orders object of one that changed (FUN_14032e0d0): a slave gets job 187 Obedient Slave, one that stops
        // being a slave loses it.
        if (before != state) Kidnap.ObedientSlave.StateChanged(world, c, state);
    }

    /// <summary>
    /// An escaped slave lies low (docs/game/carry-loot.md "Escaped slaves lie low", <b>Verified</b>): for a state above 1, with <c>elapsed</c> the
    /// game hours since <see cref="CharacterCold.SlaveSince"/>, a stamp in the future (elapsed below 0) frees it at once; an ex-slave (3) is free
    /// after more than 4 days, its owner cleared and its hair grown back (<c>FUN_1405a4f50</c>, <b>Verified</b>); an escaped slave (2) becomes an
    /// ex-slave after more than 12 hours, stamped again. The original writes these states directly, not through the setter, so no message, marker or
    /// job change follows; it keeps the owner on the first path, Meitou clears it (engine choice: a free character has no owner here, in the hash and
    /// in saves). Called from the crime sweep (engine choice: the original's per-character status update runs on a cadence that is <b>Unknown</b>).
    /// </summary>
    public static void LayLow(World world, CharacterCold c)
    {
        if (c.SlaveState <= 1) return;
        double elapsed = world.GameHours - c.SlaveSince;
        if (elapsed < 0) (c.SlaveState, c.SlaveOwner, c.SlaveMaster) = (0, -1, -1);
        else if (c.SlaveState == 3 && elapsed / 24 > 4)
        {
            (c.SlaveState, c.SlaveOwner, c.SlaveMaster) = (0, -1, -1);
            c.Shaved = false;   // CharacterHuman::shaveHead(false)
            world.SystemOf<Kidnap.CaptiveSystem>()?.MatchShave(c);
        }
        else if (c.SlaveState == 2 && elapsed > 12) (c.SlaveState, c.SlaveSince) = (3, world.GameHours);
    }

    /// <summary>
    /// Clears a character's hold: in nothing, no sentence, not kidnapped.
    /// </summary>
    static void Clear(CharacterCold c)
    {
        c.InSomething = HeldState.None;
        c.InWhat = null;
        c.LeavingBed = c.BedExitDone = false;
        c.SentenceStart = 0;
        c.SentenceLength = 0;
        c.Kidnapped = false;
    }

    #endregion

    #region Saves and the hash

    /// <summary>
    /// Takes a building's lock and door state from its saved GAMESTATE_BUILDING (docs/game/locks.md "Save keys"): <c>hardness</c> and <c>broken</c>
    /// always, level and <c>locked</c> only when the saved <c>lock lvl</c> is above 0; doors by suffix i: <c>state</c>, <c>doa</c>, <c>broken</c>,
    /// <c>gatecode</c>, <c>wants lock</c>, <c>lock lvl</c>, <c>locked</c>, <c>hardness</c>. Interior fixtures (whose save ids are not mapped) keep
    /// their rolls but count as restored, apart from Meitou's own <c>meitou lock</c> key (see <see cref="InteriorKey"/>). A <paramref name="level"/>
    /// state (the level data's, for a building no save has) marks restored only the doors it has a <c>state</c> for, so the squad's <c>initial door
    /// state</c> still applies to the rest.
    /// </summary>
    void Restore(StateKeys state, string building, List<Fixture> list, bool level)
    {
        foreach (var f in list)
        {
            f.Restored = !level || f.Kind == FixtureKind.Door && state.Ints.ContainsKey("state" + f.DoorIndex.ToString(CultureInfo.InvariantCulture));
            if (f.Contents is { } grid && Items is { } factory && ContainerSave.KeyOf(f, building) is { } contentsKey
                    && state.Strings.TryGetValue(contentsKey, out var saved))
                ContainerSave.Read(saved, grid, factory);

            if (f.Kind == FixtureKind.Door)
            {
                string i = f.DoorIndex.ToString(CultureInfo.InvariantCulture);
                ReadLock(state, f.Lock!, i, broken: false);
                if (state.Ints.TryGetValue("state" + i, out int motion))
                    f.Motion = (DoorMotion)Math.Clamp(motion, 0, 3);

                if (state.Floats.TryGetValue("doa" + i, out float doa)) f.OpenAmount = doa;
                if (state.Bools.TryGetValue("broken" + i, out bool b)) f.DoorBroken = b;
                if (state.Ints.TryGetValue("gatecode" + i, out int g)) f.GateCode = g;
                if (state.Bools.TryGetValue("wants lock" + i, out bool w)) f.WantsLock = w;
            }
            else if (f.Id == building && f.Lock is { } l) ReadLock(state, l, "", broken: true);
            else if (InteriorKey(f, building) is { } key && f.Lock is { } il && state.Ints.TryGetValue(key, out int packed))
            {
                il.Locked = (packed & 1) != 0;
                il.Broken = (packed & 2) != 0;
                il.Level = Math.Clamp(packed >> 2, 0, 100);
            }
        }
    }

    /// <summary>
    /// Meitou's save key for the lock of an interior cage or container, whose save ids are not mapped (the original's keys name a placed building's
    /// own lock and doors only): <c>meitou lock &lt;layout index&gt;</c>, an int of level x 4 + broken x 2 + locked, present only while the lock is
    /// locked or broken. Null for any other fixture.
    /// </summary>
    static string? InteriorKey(Fixture f, string building) =>
        f.Kind is FixtureKind.Cage or FixtureKind.Container && f.Id.Length > building.Length + 1 && f.Id[building.Length] == '/'
                && f.Id.StartsWith(building, StringComparison.Ordinal)
            ? "meitou lock " + f.Id[(building.Length + 1)..] : null;

    /// <summary>
    /// The keys of a building state, from a save's record or the level data's.
    /// </summary>
    readonly record struct StateKeys(IReadOnlyDictionary<string, int> Ints, IReadOnlyDictionary<string, float> Floats, IReadOnlyDictionary<string,
            bool> Bools,
        IReadOnlyDictionary<string, string> Strings)
    {
        /// <summary>
        /// The keys of a save's record.
        /// </summary>
        public static StateKeys Of(FcsRecord r) => new(r.Ints, r.Floats, r.Bools, r.Strings);
        /// <summary>
        /// The keys of a level-data record.
        /// </summary>
        public static StateKeys Of(GameRecord r) => new(r.Ints, r.Floats, r.Bools, r.Strings);
    }

    /// <summary>
    /// Reads a lock's saved hardness, broken flag (when <paramref name="broken"/>), level and locked flag; the keys end in <paramref name="suffix"/>.
    /// </summary>
    static void ReadLock(StateKeys state, LockState l, string suffix, bool broken)
    {
        if (state.Floats.TryGetValue("hardness" + suffix, out float h)) l.Hardness = h;
        if (broken && state.Bools.TryGetValue("broken" + suffix, out bool b)) l.Broken = b;
        if (state.Ints.TryGetValue("lock lvl" + suffix, out int level) && level > 0)
        {
            l.Level = Math.Min(level, 100);
            l.Locked = state.Bools.GetValueOrDefault("locked" + suffix);
        }
    }

    /// <summary>
    /// Whether any container of a registered building holds items (what <see cref="WriteBuilding"/> would store as container text).
    /// </summary>
    public bool HasContents(string buildingInstance) =>
        _byBuilding.TryGetValue(buildingInstance, out var list) && list.Any(f => f.Contents is { } grid && ContainerSave.Write(grid) is not null);

    /// <summary>
    /// Writes a registered building's lock and door state into its saved state record (the keys of <see cref="Restore"/>; <c>locked</c> true only
    /// when effectively locked). False when the building is not registered.
    /// </summary>
    public bool WriteBuilding(string buildingInstance, FcsRecord state)
    {
        if (!_byBuilding.TryGetValue(buildingInstance, out var list)) return false;
        WriteReset(buildingInstance, state);
        WriteProperty(buildingInstance, state);
        if (_publicFlags.TryGetValue(buildingInstance, out bool open)) state.Bools["is public day"] = open;
        WriteRecord(buildingInstance, state);
        foreach (var f in list)
        {
            if (f.Contents is { } grid && ContainerSave.KeyOf(f, buildingInstance) is { } contentsKey)
            {
                state.Strings.Remove(contentsKey);
                if (ContainerSave.Write(grid) is { } text) state.Strings[contentsKey] = text;
            }

            if (f.Lock is not { } l) continue;
            if (f.Kind == FixtureKind.Door)
            {
                string i = f.DoorIndex.ToString(CultureInfo.InvariantCulture);
                state.Ints["state" + i] = (int)f.Motion;
                state.Floats["doa" + i] = f.OpenAmount;
                state.Bools["broken" + i] = f.DoorBroken;
                state.Ints["gatecode" + i] = f.GateCode;
                state.Bools["wants lock" + i] = f.WantsLock;
                state.Bools.TryAdd("pause" + i, false);   // the original writes it with every door (Observed, 614 of 614)
                state.Ints["lock lvl" + i] = l.Level;
                state.Bools["locked" + i] = l.EffectivelyLocked;
                state.Floats["hardness" + i] = l.Hardness;
            }
            else if (f.Id == buildingInstance)
            {
                state.Ints["lock lvl"] = l.Level;
                state.Bools["locked"] = l.EffectivelyLocked;
                state.Bools["broken"] = l.Broken;
                state.Floats["hardness"] = l.Hardness;
            }
            else if (InteriorKey(f, buildingInstance) is { } key)
            {
                if (l.Locked || l.Broken) state.Ints[key] = l.Level << 2 | (l.Broken ? 2 : 0) | (l.Locked ? 1 : 0);
                else state.Ints.Remove(key);
            }
        }

        return true;
    }

    /// <summary>
    /// Gives a re-registered building's doors the state its old doors had, by door index (an upgraded or downgraded building: the original keeps its
    /// door objects and only swaps their records). The new lock keeps its own level and hardness; whether it is locked carries over.
    /// </summary>
    public void CarryDoors(string building, IReadOnlyList<Fixture> old)
    {
        if (!_byBuilding.TryGetValue(building, out var list)) return;
        foreach (var d in list)
        {
            if (d.Kind != FixtureKind.Door || old.FirstOrDefault(o => o.Kind == FixtureKind.Door && o.DoorIndex == d.DoorIndex) is not { } was)
                continue;

            d.Motion = was.Motion;
            d.OpenAmount = was.OpenAmount;
            d.DoorBroken = was.DoorBroken;
            d.DoorProgress = was.DoorProgress;
            d.WantsLock = was.WantsLock;
            d.GateCode = was.GateCode;
            if (d.Lock is { } l && was.Lock is { } wl) l.Locked = wl.Locked;
        }

        _doorPicks = null;
        _doorsByBuilding = null;
        SyncDoors(building);
    }

    /// <summary>
    /// A town building's Open to public flag as the player set it (or a save read it), null when never set.
    /// </summary>
    public bool? PublicOf(string building) => _publicFlags.TryGetValue(building, out bool open) ? open : null;

    /// <summary>
    /// Sets a registered building's Open to public flag (the panel's toggle on a town building the player owns).
    /// </summary>
    public void SetPublic(string building, bool open)
    {
        _publicFlags[building] = open;
        if (_properties.TryGetValue(building, out var p) && p.Construction is { } site) site.Public = open;
    }

    /// <summary>
    /// Hashes the properties, the fixtures, the count of initial door states applied, the records, the public flags and the unreachable cages.
    /// </summary>
    internal void Hash(ref StateHasher h)
    {
        HashProperties(ref h);
        h.Add(0x4C6F636B);
        h.Add(_all.Count);
        foreach (var f in _all) f.Hash(ref h);
        h.Add(_initialApplied.Count);
        HashRecords(ref h);
        if (_publicFlags.Count > 0)
        {
            foreach (var (b, open) in _publicFlags.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                h.Add(Rng.StableHash(b));
                h.Add(open);
            }
        }

        if (_unreachable.Count == 0) return;   // only once a walk failed, so worlds without one hash as before
        h.Add(_unreachable.Count);
        foreach (var ((cage, faction), until) in _unreachable)
        {
            h.Add(Rng.StableHash(cage));
            h.Add(faction);
            h.Add(until);
        }
    }

    #endregion
}

/// <summary>
/// One caging (<see cref="FixtureRegistry.Imprison"/>): who put whom in which cage, in which tick (<see cref="Carrier"/> None: the system did, or a
/// caller that named nobody).
/// </summary>
public readonly record struct Imprisonment(long Tick, CharacterId Carrier, CharacterId Captive, Fixture Cage);
