using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Construction;

namespace Meitou.Data.World;

/// <summary>What a spot inside a building is for: a bed, or one of the invisible <c>is node</c> markers of an interior layout, by its record's name.</summary>
public enum HomeSpotKind : byte
{
    Bed = 0,
    /// <summary>A node whose name holds "shop" ("Node Shopkeeper").</summary>
    Shopkeeper = 1,
    /// <summary>A node whose name holds "guard".</summary>
    Guard = 2,
    /// <summary>A node whose name holds "bar".</summary>
    Bar = 3,
    /// <summary>A node whose name holds "throne".</summary>
    Throne = 4,
    /// <summary>Any other node.</summary>
    General = 5,
    /// <summary>A seat: interior furniture with a BF_CHAIR functionality (stools, benches, sitting pillows); <see cref="HomeSpot.SitClip"/> is how it is sat on.</summary>
    Chair = 6,
    /// <summary>A seat: furniture with a BF_THRONE functionality ("throne chair"); sat on like a <see cref="Chair"/>. Not the <see cref="Throne"/> node.</summary>
    ThroneSeat = 7,
    /// <summary>Furniture with a BF_TRAINING functionality (a training dummy): stood at, not sat on.</summary>
    Training = 8,
}

/// <summary>A spot inside a building in world space; <paramref name="Yaw"/> as the simulation measures it (0 along +Z, turning towards +X).</summary>
public readonly record struct HomeSpot(HomeSpotKind Kind, Vector3 Position, float Yaw)
{
    /// <summary>A node's BUILDING <c>node type</c> (NodeType: 0 NODE_SHOPKEEPER, 1 NODE_GENERAL, 2 NODE_GUARD_POST, 3 NODE_TURRET, 4 NODE_USE, 5 NODE_LIGHT); -1 for a bed.</summary>
    public int NodeType { get; init; } = -1;
    /// <summary>A chair's sitting clip: the ANIMATION record its functionality's <c>animation</c> names ("sitting chair", "sitting idle" for floor seats); null otherwise.</summary>
    public string? SitClip { get; init; }
    /// <summary>The seat's functionality <c>hunger rate</c> (chairs 0.66, sit on floor 0.75; 1 by default).</summary>
    public float HungerRate { get; init; } = 1;
    /// <summary>The furniture's BUILDING string id for a bed or seat; null for other spots. A bed's <c>output rate</c> controls healing (docs/game/character-stats.md "Beds").</summary>
    public string? Record { get; init; }
    /// <summary>A node a goal may stand at: neither a bed, a seat nor a dummy.</summary>
    public bool IsNode => Kind is not (HomeSpotKind.Bed or HomeSpotKind.Chair or HomeSpotKind.ThroneSeat or HomeSpotKind.Training);
}

/// <summary>
/// A turret building (<c>BUILDING_FUNCTIONALITY</c> function BF_TURRET): where its mount stands and faces (0 along +Z, turning towards +X), how near its gunner must be,
/// the GUN_DATA it fires, and the <see cref="BuildingKey"/> of the building it is mounted on (<c>mounted</c> handle; null when it names none known).
/// </summary>
public readonly record struct TurretPost(Vector3 Position, float Yaw, float UseRange, string GunId, string? MountedOn);

/// <summary>
/// A placed building of a town that squads can live in, with the beds and nodes of its interior layout (docs/formats/zones.md "Building layouts") and the shop and
/// bar flags of its exterior layout. Position is the building's origin (absolute Y).
/// </summary>
public sealed record TownBuilding(string InstanceId, string BuildingId, string Name, Vector3 Position, float Yaw, bool IsShop, bool IsBar, IReadOnlyList<HomeSpot> Spots)
{
    public IEnumerable<HomeSpot> Of(HomeSpotKind kind) => Spots.Where(s => s.Kind == kind);
    public int Beds => Spots.Count(s => s.Kind == HomeSpotKind.Bed);
    /// <summary>
    /// Furniture of its interior layout with a BF_THRONE or BF_TRAINING functionality, in layout order, and the building itself when it is a free-standing chair, throne or dummy (its own
    /// origin and yaw): the pieces sitting around (275) can take besides the chairs in <see cref="Spots"/> (<see cref="SeatAt"/>). Kept apart from <see cref="Spots"/>, which are the home's nodes.
    /// </summary>
    public IReadOnlyList<HomeSpot> Furniture { get; init; } = [];
    /// <summary>The seats of the building: <see cref="Spots"/> then <see cref="Furniture"/>, indexed together (what a seat claim names).</summary>
    public int SeatCount => Spots.Count + Furniture.Count;
    public HomeSpot SeatAt(int index) => index < Spots.Count ? Spots[index] : Furniture[index - Spots.Count];
    /// <summary>The placement state's <c>owner faction ID</c> (a FACTION string id); null when the state names none (the town's faction owns it).</summary>
    public string? OwnerFactionId { get; init; }
    /// <summary>The cages, lockable containers and containers with an inventory of its interior layout (docs/game/locks.md "Data"), in layout order.</summary>
    public IReadOnlyList<TownFixture> Fixtures { get; init; } = [];
    /// <summary>The loose-item slots of its interior layout (docs/game/factions-squads-towns.md "Loose items in buildings"), in layout order.</summary>
    public IReadOnlyList<TownItemSlot> ItemSlots { get; init; } = [];
    /// <summary>The zone whose building list places it (instance ids repeat between zones).</summary>
    public ZoneCoordinate Zone { get; init; }
    /// <summary>Its run-time name (<see cref="BuildingKey"/>): what fixtures, doors and the renderer's placements know it by.</summary>
    public string Key => BuildingKey.Of(Zone, InstanceId);
    /// <summary>The level placement it was resolved from (full rotation, record, state): what its indoor volume and lights are built from.</summary>
    public ResolvedBuilding? Resolved { get; init; }
    /// <summary>
    /// Whether residents can be housed in it (docs/game/factions-squads-towns.md "Town residents"): a BUILDING with <c>interior</c> parts that does not
    /// <c>links together</c> (a wall), or an <c>is wall gate</c> gate.
    /// </summary>
    public bool CanHouse { get; init; }
    /// <summary>
    /// It has <c>interior</c> parts and its interior layout, when the level data names one, places at least one object (the original's interior holding internal buildings,
    /// docs/game/economy.md "Artifacts"; one the level data does not name the original generates on first load, and Meitou counts it).
    /// </summary>
    public bool Furnished { get; init; }
    /// <summary><c>is wall gate</c>: a town gate, housing only resident entries that name it in <c>building</c>.</summary>
    public bool IsGate { get; init; }
    /// <summary><c>links together</c>: a wall.</summary>
    public bool IsWall { get; init; }
    /// <summary>The record lists <c>interior</c> references (the original's <c>Building::setup</c> makes <c>myInterior</c> only then).</summary>
    public bool HasInterior { get; init; }
    /// <summary>The level-data state's <c>destroyed</c>: a ruin, which has no doors (docs/game/locks.md "Doors").</summary>
    public bool Destroyed { get; init; }
    /// <summary>The level data's <c>residents template</c> of its state (a SQUAD_TEMPLATE id; empty when none).</summary>
    public string ResidentsTemplate { get; init; } = "";
    /// <summary>Its level-data GAMESTATE_BUILDING (null when it has none): the lock and door state it starts with when no save has one (docs/game/locks.md "Save keys").</summary>
    public GameRecord? LevelState { get; init; }
    /// <summary>Its turret, when its record has a <c>BUILDING_FUNCTIONALITY</c> function BF_TURRET (a mounted harpoon turret or crossbow); null otherwise.</summary>
    public TurretPost? Turret { get; init; }
    /// <summary>The state's <c>interior layout name</c> (the one its <see cref="Spots"/> come from); empty when none.</summary>
    public string InteriorLayout { get; init; } = "";
    /// <summary>The names of the interior and exterior layouts its record can take (its own and those of <c>shares interiors with</c>), case ignored.</summary>
    public IReadOnlySet<string> InteriorLayouts { get; init; } = EmptyNames;
    public IReadOnlySet<string> ExteriorLayouts { get; init; } = EmptyNames;
    /// <summary>Its interior layout's furniture of function BF_TABLE, BF_CORPSE_DISPOSAL or BF_REFINERY (the original's tables to sit at, docs/game/ai-tasks.md "Town goals"), in layout order.</summary>
    public IReadOnlyList<Vector3> Tables { get; init; } = [];
    /// <summary>Its own record's BUILDING_FUNCTIONALITY <c>function</c> (-1: none); a placed table is an outdoor table to sit at.</summary>
    public int Function { get; init; } = -1;
    /// <summary>A table placed on its own in the town (not inside a building): one squad's outdoor table to hang around at.</summary>
    public bool IsOutdoorTable => Function is TownBuildings.TableFunction or TownBuildings.CorpseDisposalFunction or TownBuildings.RefineryFunction
        && !(LevelState?.GetBool("is inside building") ?? false);
    /// <summary>
    /// The slot (<c>I</c>) and serial (<c>S</c>) of the <c>handle</c> its level-data state carries (-1 / 0 when it has none). A save writes a base building's state with this
    /// same handle (docs/formats/save.md "Object handles"), so a platoon can name a building whose zone was never saved.
    /// </summary>
    public int HandleSlot { get; init; } = -1;
    public int HandleSerial { get; init; }

    static readonly IReadOnlySet<string> EmptyNames = new HashSet<string>();
}

/// <summary>
/// A lockable object or container of a building's interior layout: a cage or pole (BUILDING_FUNCTIONALITY <c>function</c> 8), a BUILDING with <c>has lock</c> or <c>has inventory</c>. <paramref name="Id"/> is
/// the parent's <see cref="BuildingKey"/>, "/", and the item's index in the layout (an <b>engine</b> name: the save's ids of generated interiors are not mapped).
/// </summary>
public sealed record TownFixture(string Id, string RecordId, bool IsCage, Vector3 Position, float Yaw)
{
    /// <summary>Its BUILDING_FUNCTIONALITY <c>function</c> (-1: none).</summary>
    public int Function { get; init; } = -1;
    /// <summary>A container with its own inventory (<c>has inventory</c>): its cells (<see cref="MachineData.StorageGrid"/>); 0 x 0 otherwise.</summary>
    public int StorageWidth { get; init; }
    public int StorageHeight { get; init; }
    /// <summary>Its stock is a building shop's (functions 2 BF_RESOURCE_STORAGE, 9 BF_SHOP, 13 BF_GENERAL_STORAGE with an inventory; <c>FUN_1409562f0</c>, docs/game/economy.md "Building shops").</summary>
    public bool HoldsShopStock => StorageWidth > 0 && StorageHeight > 0 && Function is TownBuildings.ResourceStorageFunction or TownBuildings.ShopFunction or TownBuildings.GeneralStorageFunction;
}

/// <summary>
/// A loose-item slot of a building's interior layout: an ITEM_PLACEMENT_GROUP placed at a world position and rotation, on its object's floor. <paramref name="Id"/> is the parent's
/// <see cref="BuildingKey"/>, "/item", and the slot's index among the layout's groups (an <b>engine</b> name).
/// </summary>
public sealed record TownItemSlot(string Id, string GroupId, Vector3 Position, Quaternion Rotation, int Floor);

/// <summary>
/// The buildings of each placed town, for the AI's homes, beds and nodes (docs/simulation.md "AI", Homes). A placed building belongs to a town as the game's import decides
/// (<see cref="MemberOf"/>, docs/formats/zones.md "Town membership"). Buildings the level data marks destroyed are kept (the game rebuilds a non-player building on import: <c>FUN_140552b70</c> keeps <c>destroyed</c> only for the player's; the Hub's ruins house its squatters); node markers and resource buildings
/// (<c>is foliage</c>) are left out. Turrets stay, with their gun and the building they are mounted on (<see cref="TownBuilding.Turret"/>), the AI's turret posts. Beds are interior-layout objects with a BED or SKELETON_BED functionality (docs/game/buildings-production.md, BuildingFunction 6
/// and 25, found by the type of the referenced records rather than a field name); seats are those with a CHAIR functionality (17); nodes are its <c>is node</c> objects,
/// their <c>node type</c> kept and their kind read from it (0 shopkeeper, 2 guard post), else from the record name (<see cref="NodeKind(string)"/>).
/// </summary>
public static class TownBuildings
{
    public const int BedFunction = 6, SkeletonBedFunction = 25;
    /// <summary>BUILDING_FUNCTIONALITY <c>function</c> 8, BF_CAGE: cages, the prisoner pole, the peeler (docs/game/locks.md "Data").</summary>
    public const int CageFunction = 8;
    /// <summary>BUILDING_FUNCTIONALITY <c>function</c> 21, BF_GATE (docs/game/buildings-production.md "Classes and functions").</summary>
    public const int GateFunction = 21;

    /// <summary>
    /// A gate, a boundary of the gate codes (docs/game/pathfinding.md "Gate codes"): a wall gate the player builds (<c>is wall gate</c>) or a building whose functionality is
    /// BF_GATE (engine reading of the original's GatewayBuilding class, which the factory picks by the function, <b>Observed</b>).
    /// </summary>
    public static bool IsGate(GameDatabase db, GameRecord building, Dictionary<string, GameRecord?> cache) =>
        building.GetBool("is wall gate") || FunctionOf(db, building, cache)?.GetInt("function") == GateFunction;
    /// <summary>BUILDING_FUNCTIONALITY <c>function</c> 17, BF_CHAIR: stools, benches, sitting pillows and spots (docs/game/ai-tasks.md "Town goals").</summary>
    public const int ChairFunction = 17;
    /// <summary>BF_THRONE ("throne chair", sat on like a chair) and BF_TRAINING (a training dummy): the other furniture sitting around (275) takes (docs/game/ai-tasks.md "Town goals").</summary>
    public const int ThroneFunction = 24, TrainingFunction = 7;
    /// <summary>BF_TABLE, BF_CORPSE_DISPOSAL, BF_REFINERY: what a free seat faces (<see cref="SeatYaw"/>).</summary>
    public const int TableFunction = 16, CorpseDisposalFunction = 11, RefineryFunction = 4;
    /// <summary>BF_RESOURCE_STORAGE, BF_SHOP, BF_GENERAL_STORAGE: the containers a building shop's stock goes into (<see cref="TownFixture.HoldsShopStock"/>).</summary>
    public const int ResourceStorageFunction = 2, ShopFunction = 9, GeneralStorageFunction = 13;

    /// <summary>The buildings by town placement id (<see cref="TownPlacement.InstanceId"/>), each list in placement order.</summary>
    public static Dictionary<string, List<TownBuilding>> Build(GameDatabase db, WorldLevelData levels, BuildingLayouts? layouts, Func<double, double, float> terrainHeight)
    {
        var towns = new List<(TownPlacement Placement, Gameplay.TownData Town)>();
        foreach (var t in levels.Towns())
            if (db.Find(t.TownId) is { Type: FcsRecordType.TOWN } record) towns.Add((t, Gameplay.TownData.From(record)));
        var beds = new Dictionary<string, bool>(StringComparer.Ordinal);
        var functions = new Dictionary<string, GameRecord?>(StringComparer.Ordinal);
        // The instance of each building state's own handle, so a turret's mounted handle names the building it stands on.
        var instances = new Dictionary<(int C, int I), string>();
        foreach (var b in levels.Buildings())
            if (b.StateId is not null && levels.Zones.TryGetValue(b.Zone, out var zone) && zone.Find(b.StateId) is { } state && state.GetInt("handleTYPE") == (int)FcsRecordType.BUILDING && state.Ints.ContainsKey("handleI"))
                instances[(state.GetInt("handleC"), state.GetInt("handleI"))] = b.Key;
        var names = new Dictionary<(string, bool), IReadOnlySet<string>>();
        List<TownFixture>? fixtures = null;
        List<TownItemSlot>? itemSlots = null;
        List<Vector3>? tables = null;
        List<HomeSpot>? furniture = null;
        var result = new Dictionary<string, List<TownBuilding>>(StringComparer.Ordinal);
        foreach (var b in levels.Buildings())
        {
            if (BuildingPlacements.Resolve(b, db, levels, terrainHeight) is not { } resolved) continue;
            var record = resolved.Record;
            if (record.GetBool("is node") || record.GetBool("is foliage")) continue;
            var state = b.StateId is not null && levels.Zones.TryGetValue(b.Zone, out var zoneDb) ? zoneDb.Find(b.StateId) : null;
            if (MemberOf(towns, resolved.Position, record) is not { } town) continue;
            var functionality = FunctionOf(db, record, functions);
            TurretPost? turret = functionality is { } fn && fn.GetInt("function") == ObjectRanges.TurretFunction
                ? new TurretPost(resolved.Position, YawOf(b.Rotation), fn.GetFloat("use range", 10),
                    record.GetReferences("gun turret").Select(r => r.TargetStringId).FirstOrDefault() ?? "", MountedOn(instances, state))
                : null;
            var spots = new List<HomeSpot>();
            bool shop = false, bar = false, furnished = record.GetReferences("interior").Count > 0;
            if (layouts is not null && state is not null)
            {
                if (layouts.Find(record, state.GetString("exterior layout name")) is { } exterior)
                {
                    shop = exterior.GetInt("is shop") != 0;
                    bar = exterior.GetInt("is bar") != 0;
                }
                // A named layout the data lacks gives the original an interior with no objects.
                if (state.GetString("interior layout name").Length > 0) furnished = false;
                if (layouts.Find(record, state.GetString("interior layout name"), exterior: false) is { } interior)
                {
                    int index = 0;
                    List<Vector3>? faced = null;
                    List<(int Spot, int Flags)>? seats = null;
                    foreach (var item in layouts.Items(interior))
                    {
                        var (at, rotation) = BuildingLayouts.Place(resolved.Position, b.Rotation, item);
                        bool node = item.Building.GetBool("is node");
                        HomeSpotKind? kind = node ? NodeKind(item.Building) : IsBed(db, item.Building, beds) ? HomeSpotKind.Bed : null;
                        var function = kind is null ? FunctionOf(db, item.Building, functions) : null;
                        // A node marker looks along its local -X (docs/game/ai-tasks.md "Town goals"), a bed along +Z.
                        if (kind is { } k) spots.Add(new HomeSpot(k, at, YawOf(rotation) - (node ? MathF.PI / 2 : 0))
                        {
                            NodeType = node ? item.Building.GetInt("node type") : -1,
                            Record = k == HomeSpotKind.Bed ? item.Building.StringId : null,
                        });
                        else if (function?.GetInt("function") == ChairFunction)
                        {
                            (seats ??= []).Add((spots.Count, function.GetInt("flags")));
                            spots.Add(new HomeSpot(HomeSpotKind.Chair, at, YawOf(rotation))
                            {
                                Record = item.Building.StringId,
                                SitClip = function.GetReferences("animation").Select(r => db.Find(r.TargetStringId)?.Name).FirstOrDefault(n => n is not null),
                                HungerRate = function.GetFloat("hunger rate", 1),
                            });
                        }
                        else if (function is not null && FurnitureOf(db, function, at, YawOf(rotation), item.Building.StringId) is { } piece) (furniture ??= []).Add(piece);
                        if (function?.GetInt("function") is TableFunction or CorpseDisposalFunction or RefineryFunction) (faced ??= []).Add(at);
                        else if (FunctionOf(db, item.Building, functions)?.GetInt("function") is var itemFn && (itemFn == CageFunction || item.Building.GetBool("has lock") || item.Building.GetBool("has inventory")))
                        {
                            var (storageWidth, storageHeight) = item.Building.GetBool("has inventory") ? MachineData.StorageGrid(db, item.Building) : (0, 0);
                            (fixtures ??= []).Add(new TownFixture($"{b.Key}/{index}", item.Building.StringId, itemFn == CageFunction, at, YawOf(rotation))
                            {
                                Function = itemFn ?? -1,
                                StorageWidth = storageWidth,
                                StorageHeight = storageHeight,
                            });
                        }
                        index++;
                    }
                    furnished = index > 0;
                    int slot = 0;
                    foreach (var group in layouts.ItemGroups(interior))
                    {
                        var (at, rotation) = BuildingLayouts.Place(resolved.Position, b.Rotation, group);
                        (itemSlots ??= []).Add(new TownItemSlot($"{b.Key}/item{slot++}", group.Building.StringId, at, rotation, group.Floor));
                    }
                    if (seats is not null && faced is not null)
                        foreach (var (s, flags) in seats) spots[s] = spots[s] with { Yaw = SeatYaw(spots[s], flags, faced) };
                    tables = faced;
                }
            }
            // Standing alone in the town, a chair, throne or dummy is a piece of furniture itself.
            if (functionality is not null && FurnitureOf(db, functionality, resolved.Position, YawOf(b.Rotation), record.StringId) is { } alone) (furniture ??= []).Add(alone);
            if (!result.TryGetValue(town.InstanceId, out var list)) result[town.InstanceId] = list = [];
            string? owner = state?.GetString("owner faction ID") is { Length: > 0 } o ? o : null;
            list.Add(new TownBuilding(b.InstanceId, record.StringId, record.Name, resolved.Position, YawOf(b.Rotation), shop, bar, spots)
            {
                OwnerFactionId = owner, Fixtures = fixtures ?? [], ItemSlots = itemSlots ?? [], Zone = b.Zone, Resolved = resolved,
                CanHouse = record.GetBool("is wall gate") || record.GetReferences("interior").Count > 0 && !record.GetBool("links together"),
                Furnished = furnished,
                IsGate = record.GetBool("is wall gate"),
                IsWall = record.GetBool("links together"),
                HasInterior = record.GetReferences("interior").Count > 0,
                Destroyed = resolved.Destroyed,
                Turret = turret,
                ResidentsTemplate = state?.GetString("residents template") ?? "",
                LevelState = state,
                InteriorLayout = state?.GetString("interior layout name") ?? "",
                InteriorLayouts = LayoutNames(layouts, record, exterior: false, names),
                ExteriorLayouts = LayoutNames(layouts, record, exterior: true, names),
                HandleSlot = state is not null && state.Ints.ContainsKey("handleI") && state.GetInt("handleTYPE") == (int)FcsRecordType.BUILDING ? state.GetInt("handleI") : -1,
                HandleSerial = state?.GetInt("handleS") ?? 0,
                Tables = tables ?? [],
                Furniture = furniture ?? [],
                Function = functionality?.GetInt("function") ?? -1,
            });
            fixtures = null;
            itemSlots = null;
            tables = null;
            furniture = null;
        }
        return result;
    }

    /// <summary>
    /// A seat-like piece for sitting around (275, <see cref="TownBuilding.Furniture"/>) from a functionality: BF_CHAIR and BF_THRONE are sat on (its <c>animation</c> and <c>hunger rate</c>), BF_TRAINING
    /// is stood at; null for any other function.
    /// </summary>
    static HomeSpot? FurnitureOf(GameDatabase db, GameRecord function, Vector3 at, float yaw, string record)
    {
        var kind = function.GetInt("function") switch { ChairFunction => HomeSpotKind.Chair, ThroneFunction => HomeSpotKind.ThroneSeat, TrainingFunction => HomeSpotKind.Training, _ => (HomeSpotKind?)null };
        if (kind is not { } k) return null;
        return new HomeSpot(k, at, yaw)
        {
            Record = record,
            SitClip = k == HomeSpotKind.Training ? null : function.GetReferences("animation").Select(r => db.Find(r.TargetStringId)?.Name).FirstOrDefault(n => n is not null),
            HungerRate = function.GetFloat("hunger rate", 1),
        };
    }

    /// <summary>
    /// The town a level-data building belongs to (docs/formats/zones.md "Town membership", Observed: the building import <c>FUN_14057cc70</c>): the nearest town whose
    /// <see cref="Gameplay.TownData.Radius"/> holds it (X/Z, nest markers left out); else, for a record with <c>interior</c> parts, the nearest town within 10000; else none.
    /// The state's <c>town</c> handle is not used: the game keeps it only for player towns.
    /// </summary>
    static TownPlacement? MemberOf(List<(TownPlacement Placement, Gameplay.TownData Town)> towns, Vector3 position, GameRecord record)
    {
        var at = new Vector2(position.X, position.Z);
        TownPlacement? best = null;
        float bestD = float.MaxValue;
        foreach (var (p, t) in towns)
        {
            if (t.Type == Gameplay.TownType.NestMarker) continue;
            float d = Vector2.DistanceSquared(at, new Vector2(p.Position.X, p.Position.Z));
            if (d <= t.Radius * t.Radius && d < bestD) (best, bestD) = (p, d);
        }
        if (best is not null || record.GetReferences("interior").Count == 0) return best;
        bestD = InteriorReach * InteriorReach;
        foreach (var (p, _) in towns)
        {
            float d = Vector2.DistanceSquared(at, new Vector2(p.Position.X, p.Position.Z));
            if (d < bestD) (best, bestD) = (p, d);
        }
        return best;
    }

    /// <summary>A building with an interior outside every town's radius goes to the nearest town within this distance (the import's squared bound 1e8).</summary>
    const float InteriorReach = 10000;

    /// <summary>The key of the building a state's <c>mounted</c> handle names; null when the handle is null, not a building, or unknown.</summary>
    static string? MountedOn(Dictionary<(int C, int I), string> instances, GameRecord? state) =>
        state is not null && state.GetInt("mountedTYPE") == (int)FcsRecordType.BUILDING && state.Ints.ContainsKey("mountedI") &&
        instances.TryGetValue((state.GetInt("mountedC"), state.GetInt("mountedI")), out string? instance) ? instance : null;

    /// <summary>The kind of a node: by its <c>node type</c> (0 shopkeeper, 2 guard post), else by its record's name.</summary>
    public static HomeSpotKind NodeKind(GameRecord node) => node.GetInt("node type") switch
    {
        0 => HomeSpotKind.Shopkeeper,
        2 => HomeSpotKind.Guard,
        _ => NodeKind(node.Name),
    };

    /// <summary>The layout names a building record can take (case ignored), cached by record and side; empty without layouts.</summary>
    static IReadOnlySet<string> LayoutNames(BuildingLayouts? layouts, GameRecord record, bool exterior, Dictionary<(string, bool), IReadOnlySet<string>> cache)
    {
        if (layouts is null) return new HashSet<string>();
        if (!cache.TryGetValue((record.StringId, exterior), out var set))
            cache[(record.StringId, exterior)] = set = new HashSet<string>(layouts.Names(record, exterior), StringComparer.OrdinalIgnoreCase);
        return set;
    }

    /// <summary>The kind of a node from its record's name (case ignored).</summary>
    public static HomeSpotKind NodeKind(string name) =>
        name.Contains("shop", StringComparison.OrdinalIgnoreCase) ? HomeSpotKind.Shopkeeper
        : name.Contains("guard", StringComparison.OrdinalIgnoreCase) ? HomeSpotKind.Guard
        : name.Contains("throne", StringComparison.OrdinalIgnoreCase) ? HomeSpotKind.Throne
        : name.Contains("bar", StringComparison.OrdinalIgnoreCase) ? HomeSpotKind.Bar
        : HomeSpotKind.General;

    /// <summary>A BUILDING with a BUILDING_FUNCTIONALITY record of function BED or SKELETON_BED in any of its reference lists (cached by id).</summary>
    public static bool IsBed(GameDatabase db, GameRecord building, Dictionary<string, bool> cache)
    {
        if (cache.TryGetValue(building.StringId, out bool known)) return known;
        bool bed = false;
        foreach (var list in building.ReferenceLists)
            foreach (var r in building.GetReferences(list))
                if (db.Find(r.TargetStringId) is { Type: FcsRecordType.BUILDING_FUNCTIONALITY } f && f.GetInt("function") is BedFunction or SkeletonBedFunction)
                    bed = true;
        cache[building.StringId] = bed;
        return bed;
    }

    /// <summary>
    /// The first BUILDING_FUNCTIONALITY record in any reference list of <paramref name="building"/> (its <c>function</c>, <c>restrains movement</c>, <c>use range</c>), cached by id; null
    /// for a building without one.
    /// </summary>
    public static GameRecord? FunctionOf(GameDatabase db, GameRecord building, Dictionary<string, GameRecord?> cache)
    {
        if (cache.TryGetValue(building.StringId, out var known)) return known;
        GameRecord? found = null;
        foreach (var list in building.ReferenceLists)
        {
            foreach (var r in building.GetReferences(list))
                if (db.Find(r.TargetStringId) is { Type: FcsRecordType.BUILDING_FUNCTIONALITY } f) { found = f; break; }
            if (found is not null) break;
        }
        cache[building.StringId] = found;
        return found;
    }

    /// <summary>
    /// The way a seat is sat facing (docs/game/ai-tasks.md "Chairs"): by its functionality's <c>flags</c>, 0 its own facing (<paramref name="seat"/>'s yaw), 1 towards
    /// the nearest of <paramref name="faced"/> (the building's tables, corpse disposals and refineries), 2 its own facing or the opposite one, whichever has that one in front.
    /// </summary>
    static float SeatYaw(HomeSpot seat, int flags, List<Vector3> faced)
    {
        if (flags is not (1 or 2)) return seat.Yaw;
        var nearest = faced.MinBy(p => Vector3.DistanceSquared(p, seat.Position));
        var to = new Vector2(nearest.X - seat.Position.X, nearest.Z - seat.Position.Z);
        if (to.LengthSquared() < 1e-6f) return seat.Yaw;
        if (flags == 1) return MathF.Atan2(to.X, to.Y);
        return Vector2.Dot(new Vector2(MathF.Sin(seat.Yaw), MathF.Cos(seat.Yaw)), to) >= 0 ? seat.Yaw : MathF.IEEERemainder(seat.Yaw + MathF.PI, MathF.Tau);
    }

    /// <summary>The simulation's yaw (0 along +Z, turning towards +X) of a rotation.</summary>
    public static float YawOf(Quaternion rotation)
    {
        var q = rotation.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(rotation);
        var forward = Vector3.Transform(Vector3.UnitZ, q);
        return MathF.Atan2(forward.X, forward.Z);
    }
}
