using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Construction;

namespace Meitou.Data.World;

/// <summary>
/// What a spot inside a building is for: a bed, or one of the invisible <c>is node</c> markers of an interior layout, by its record's name.
/// </summary>
public enum HomeSpotKind : byte
{
    /// <summary>
    /// A bed.
    /// </summary>
    Bed = 0,
    /// <summary>
    /// A node whose name holds "shop" ("Node Shopkeeper").
    /// </summary>
    Shopkeeper = 1,
    /// <summary>
    /// A node whose name holds "guard".
    /// </summary>
    Guard = 2,
    /// <summary>
    /// A node whose name holds "bar".
    /// </summary>
    Bar = 3,
    /// <summary>
    /// A node whose name holds "throne".
    /// </summary>
    Throne = 4,
    /// <summary>
    /// Any other node.
    /// </summary>
    General = 5,
    /// <summary>
    /// A seat: interior furniture with a BF_CHAIR functionality (stools, benches, sitting pillows); <see cref="HomeSpot.SitClip"/> is how it is sat
    /// on.
    /// </summary>
    Chair = 6,
    /// <summary>
    /// A seat: furniture with a BF_THRONE functionality ("throne chair"); sat on like a <see cref="Chair"/>. Not the <see cref="Throne"/> node.
    /// </summary>
    ThroneSeat = 7,
    /// <summary>
    /// Furniture with a BF_TRAINING functionality (a training dummy): stood at, not sat on.
    /// </summary>
    Training = 8,
    /// <summary>
    /// Interior furniture with a BF_MINE, BF_RESEARCH, BF_REFINERY or BF_CRAFTING functionality: pretend-operated by a squad member staying home (43,
    /// docs/game/ai-tasks.md "Town goals").
    /// </summary>
    Workstation = 9,
    /// <summary>
    /// A node of type 3 (turret), 4 (use) or 5 (light) not named as a bar or throne: no goal stands at it (the general-node finder asks for type 1
    /// only, <c>FUN_14038c2f0</c>).
    /// </summary>
    Other = 10,
}

/// <summary>
/// A spot inside a building in world space; <paramref name="Yaw"/> as the simulation measures it (0 along +Z, turning towards +X).
/// </summary>
public readonly record struct HomeSpot(HomeSpotKind Kind, Vector3 Position, float Yaw)
{
    /// <summary>
    /// The furniture's placed rotation, before the sitter turns towards a table; collision and picking keep this facing.
    /// </summary>
    public float PlacementYaw { get; init; } = Yaw;
    /// <summary>
    /// The sitter's facing as the game has it; differs from <see cref="Yaw"/> only for a bench with its table behind
    /// (<see cref="TownBuildings.SeatYaw"/>).
    /// </summary>
    public float FaithfulYaw { get; init; } = Yaw;
    /// <summary>
    /// A node's BUILDING <c>node type</c> (NodeType: 0 NODE_SHOPKEEPER, 1 NODE_GENERAL, 2 NODE_GUARD_POST, 3 NODE_TURRET, 4 NODE_USE, 5 NODE_LIGHT);
    /// -1 for a bed.
    /// </summary>
    public int NodeType { get; init; } = -1;
    /// <summary>
    /// A chair's sitting clip: the ANIMATION record its functionality's <c>animation</c> names ("sitting chair", "sitting idle" for floor seats);
    /// null otherwise.
    /// </summary>
    public string? SitClip { get; init; }
    /// <summary>
    /// The seat's functionality <c>hunger rate</c> (chairs 0.66, sit on floor 0.75; 1 by default).
    /// </summary>
    public float HungerRate { get; init; } = 1;
    /// <summary>
    /// The BUILDING string id of a bed, seat or training dummy; null for other spots. A bed's output rate controls healing
    /// (docs/game/character-stats.md "Beds").
    /// </summary>
    public string? Record { get; init; }
    /// <summary>
    /// A bed's position marker (<see cref="WorldObjectLayout.PositionMarker"/>) when its record flags a part for it; null: the bed's own position.
    /// </summary>
    public Vector3? Marker { get; init; }
    /// <summary>
    /// A node a goal may stand at: neither a bed, a seat nor a dummy.
    /// </summary>
    public bool IsNode => Kind is not (HomeSpotKind.Bed or HomeSpotKind.Chair or HomeSpotKind.ThroneSeat or HomeSpotKind.Training
        or HomeSpotKind.Workstation or HomeSpotKind.Other);
}

/// <summary>
/// A turret building (<c>BUILDING_FUNCTIONALITY</c> function BF_TURRET): where its mount stands and faces (0 along +Z, turning towards +X), how near
/// its gunner must be, the GUN_DATA it fires, and the <see cref="BuildingKey"/> of the building it is mounted on (<c>mounted</c> handle; null when it
/// names none known).
/// </summary>
public readonly record struct TurretPost(Vector3 Position, float Yaw, float UseRange, string GunId, string? MountedOn);

/// <summary>
/// A placed building of a town that squads can live in, with the beds and nodes of its interior layout (docs/formats/zones.md "Building layouts") and
/// the shop and bar flags of its exterior layout. Position is the building's origin (absolute Y).
/// </summary>
public sealed record TownBuilding(string InstanceId, string BuildingId, string Name, Vector3 Position, float Yaw, bool IsShop, bool IsBar,
    IReadOnlyList<HomeSpot> Spots)
{
    /// <summary>
    /// The spots of <paramref name="kind"/>.
    /// </summary>
    public IEnumerable<HomeSpot> Of(HomeSpotKind kind) => Spots.Where(s => s.Kind == kind);
    /// <summary>
    /// How many beds the building has.
    /// </summary>
    public int Beds => Spots.Count(s => s.Kind == HomeSpotKind.Bed);
    /// <summary>
    /// Furniture of its interior layout with a BF_THRONE or BF_TRAINING functionality, in layout order, and the building itself when it is a
    /// free-standing chair, throne or dummy (its own origin and yaw): the pieces sitting around (275) can take besides the chairs in
    /// <see cref="Spots"/> (<see cref="SeatAt"/>). Kept apart from <see cref="Spots"/>, which are the home's nodes.
    /// </summary>
    public IReadOnlyList<HomeSpot> Furniture { get; init; } = [];
    /// <summary>
    /// The seats of the building: <see cref="Spots"/> then <see cref="Furniture"/>, indexed together (what a seat claim names).
    /// </summary>
    public int SeatCount => Spots.Count + Furniture.Count;
    /// <summary>
    /// The seat of <paramref name="index"/> (<see cref="SeatCount"/>).
    /// </summary>
    public HomeSpot SeatAt(int index) => index < Spots.Count ? Spots[index] : Furniture[index - Spots.Count];
    /// <summary>
    /// The placement state's <c>owner faction ID</c> (a FACTION string id); null when the state names none (the town's faction owns it).
    /// </summary>
    public string? OwnerFactionId { get; init; }
    /// <summary>
    /// The cages, lockable containers and containers with an inventory of its interior layout (docs/game/locks.md "Data"), in layout order.
    /// </summary>
    public IReadOnlyList<TownFixture> Fixtures { get; init; } = [];
    /// <summary>
    /// The loose-item slots of its interior layout (docs/game/factions-squads-towns.md "Loose items in buildings"), in layout order.
    /// </summary>
    public IReadOnlyList<TownItemSlot> ItemSlots { get; init; } = [];
    /// <summary>
    /// The zone whose building list places it (instance ids repeat between zones).
    /// </summary>
    public ZoneCoordinate Zone { get; init; }
    /// <summary>
    /// Its run-time name (<see cref="BuildingKey"/>): what fixtures, doors and the renderer's placements know it by.
    /// </summary>
    public string Key => BuildingKey.Of(Zone, InstanceId);
    /// <summary>
    /// The level placement it was resolved from (full rotation, record, state): what its indoor volume and lights are built from.
    /// </summary>
    public ResolvedBuilding? Resolved { get; init; }
    /// <summary>
    /// Whether residents can be housed in it (docs/game/factions-squads-towns.md "Town residents"): a BUILDING with <c>interior</c> parts that does not
    /// <c>links together</c> (a wall), or an <c>is wall gate</c> gate.
    /// </summary>
    public bool CanHouse { get; init; }
    /// <summary>
    /// It has <c>interior</c> parts, is not a ruin, and its named interior layout places at least one object (the original's interior holding
    /// internal buildings, docs/game/economy.md "Artifacts"; the name is its residents' since <see cref="ResidentRuins.Apply"/>). Without contents
    /// laid out: it has <c>interior</c> parts.
    /// </summary>
    public bool Furnished { get; init; }
    /// <summary>
    /// <c>is wall gate</c>: a town gate, housing only resident entries that name it in <c>building</c>.
    /// </summary>
    public bool IsGate { get; init; }
    /// <summary>
    /// <c>links together</c>: a wall.
    /// </summary>
    public bool IsWall { get; init; }
    /// <summary>
    /// The record lists <c>interior</c> references (the original's <c>Building::setup</c> makes <c>myInterior</c> only then).
    /// </summary>
    public bool HasInterior { get; init; }
    /// <summary>
    /// The level-data state's <c>destroyed</c>: a ruin, which has no doors (docs/game/locks.md "Doors").
    /// </summary>
    public bool Destroyed { get; init; }
    /// <summary>
    /// A ruin only by its residents (<see cref="ResidentRuins.Apply"/>): its level file has it standing.
    /// </summary>
    public bool RuinedByResidents { get; init; }
    /// <summary>
    /// The level data's <c>residents template</c> of its state (a SQUAD_TEMPLATE id; empty when none).
    /// </summary>
    public string ResidentsTemplate { get; init; } = "";
    /// <summary>
    /// Its level-data GAMESTATE_BUILDING (null when it has none): the lock and door state it starts with when no save has one (docs/game/locks.md
    /// "Save keys").
    /// </summary>
    public GameRecord? LevelState { get; init; }
    /// <summary>
    /// Its turret, when its record has a <c>BUILDING_FUNCTIONALITY</c> function BF_TURRET (a mounted harpoon turret or crossbow); null otherwise.
    /// </summary>
    public TurretPost? Turret { get; init; }
    /// <summary>
    /// Its interior layout's name (the one its <see cref="Spots"/> come from: its residents', <see cref="LayoutName"/>); empty when none.
    /// </summary>
    public string InteriorLayout { get; init; } = "";
    /// <summary>
    /// The interior and exterior layout names its level files give it, before any residents named theirs
    /// (<see cref="WorldLevelData.LevelLayoutNames"/>).
    /// </summary>
    public (string Interior, string Exterior) LevelLayouts { get; init; } = ("", "");
    /// <summary>
    /// The name and squad template its level files give it, before any residents named theirs (<see cref="WorldLevelData.LevelNames"/>).
    /// </summary>
    public BuildingNaming LevelNaming { get; init; } = new("", "");
    /// <summary>
    /// The name and squad template a town override's housing gave it (<see cref="TownBuildings.Relay"/>); null leaves its state's.
    /// </summary>
    public BuildingNaming? Naming { get; init; }
    /// <summary>
    /// The name of its interior or exterior layout now: its placement's (<see cref="TownBuildings.Relay"/>), else its level state's; "" for none.
    /// </summary>
    public string LayoutName(bool exterior) =>
        Resolved?.Placement.LayoutName(LevelState, exterior)
        ?? LevelState?.GetString(exterior ? ResidentRuins.ExteriorKey : ResidentRuins.InteriorKey) ?? "";
    /// <summary>
    /// The names of the interior and exterior layouts its record can take (its own and those of <c>shares interiors with</c>), case ignored.
    /// </summary>
    public IReadOnlySet<string> InteriorLayouts { get; init; } = EmptyNames;
    /// <summary>
    /// See <see cref="InteriorLayouts"/>.
    /// </summary>
    public IReadOnlySet<string> ExteriorLayouts { get; init; } = EmptyNames;
    /// <summary>
    /// Its interior layout's furniture of function BF_TABLE, BF_CORPSE_DISPOSAL or BF_REFINERY (the original's tables to sit at,
    /// docs/game/ai-tasks.md "Town goals"), in layout order.
    /// </summary>
    public IReadOnlyList<Vector3> Tables { get; init; } = [];
    /// <summary>
    /// Its own record's BUILDING_FUNCTIONALITY <c>function</c> (-1: none); a placed table is an outdoor table to sit at.
    /// </summary>
    public int Function { get; init; } = -1;
    /// <summary>
    /// How many generators it is and its interior and exterior layouts hold, ruined or not: while it stands, they power its town's beds.
    /// </summary>
    public int Generators { get; init; }
    /// <summary>
    /// A table placed on its own in the town (not inside a building): one squad's outdoor table to hang around at.
    /// </summary>
    public bool IsOutdoorTable => Function is TownBuildings.TableFunction or TownBuildings.CorpseDisposalFunction or TownBuildings.RefineryFunction
        && !(LevelState?.GetBool("is inside building") ?? false);
    /// <summary>
    /// The slot (<c>I</c>) and serial (<c>S</c>) of the <c>handle</c> its level-data state carries (-1 / 0 when it has none). A save writes a base
    /// building's state with this same handle (docs/formats/save.md "Object handles"), so a platoon can name a building whose zone was never saved.
    /// </summary>
    public int HandleSlot { get; init; } = -1;
    /// <summary>
    /// See <see cref="HandleSlot"/>.
    /// </summary>
    public int HandleSerial { get; init; }

    /// <summary>
    /// The empty name set the layout sets default to.
    /// </summary>
    static readonly IReadOnlySet<string> EmptyNames = new HashSet<string>();
}

/// <summary>
/// A lockable object or container of a building's interior layout: a cage or pole (BUILDING_FUNCTIONALITY <c>function</c> 8), a BUILDING with <c>has
/// lock</c> or <c>has inventory</c>. <paramref name="Id"/> is the parent's <see cref="BuildingKey"/>, "/", and the item's index in the layout (an
/// <b>engine</b> name: the save's ids of generated interiors are not mapped).
/// </summary>
public sealed record TownFixture(string Id, string RecordId, bool IsCage, Vector3 Position, float Yaw)
{
    /// <summary>
    /// Its BUILDING_FUNCTIONALITY <c>function</c> (-1: none).
    /// </summary>
    public int Function { get; init; } = -1;
    /// <summary>
    /// A container with its own inventory (<c>has inventory</c>): its cells (<see cref="MachineData.StorageGrid"/>); 0 x 0 otherwise.
    /// </summary>
    public int StorageWidth { get; init; }
    /// <summary>
    /// See <see cref="StorageWidth"/>.
    /// </summary>
    public int StorageHeight { get; init; }
    /// <summary>
    /// Its stock is a building shop's (functions 2 BF_RESOURCE_STORAGE, 9 BF_SHOP, 13 BF_GENERAL_STORAGE with an inventory; <c>FUN_1409562f0</c>,
    /// docs/game/economy.md "Building shops").
    /// </summary>
    public bool HoldsShopStock => StorageWidth > 0 && StorageHeight > 0
        && Function is TownBuildings.ResourceStorageFunction or TownBuildings.ShopFunction or TownBuildings.GeneralStorageFunction;
}

/// <summary>
/// A loose-item slot of a building's interior layout: an ITEM_PLACEMENT_GROUP placed at a world position and rotation, on its object's floor.
/// <paramref name="Id"/> is the parent's <see cref="BuildingKey"/>, "/item", and the slot's index among the layout's groups (an <b>engine</b> name).
/// </summary>
public sealed record TownItemSlot(string Id, string GroupId, Vector3 Position, Quaternion Rotation, int Floor);

/// <summary>
/// The buildings of each placed town, for the AI's homes, beds and nodes (docs/simulation.md "AI", Homes). A placed building belongs to a town as the
/// game's import decides (<see cref="TownBuildingBuilder.MemberOf"/>, docs/formats/zones.md "Town membership"). Buildings the level data marks
/// destroyed are kept (the game rebuilds a non-player building on import: <c>FUN_140552b70</c> keeps <c>destroyed</c> only for the player's; the
/// Hub's ruins house its squatters); node markers and resource buildings (<c>is foliage</c>) are left out. Turrets stay, with their gun and the
/// building they are mounted on (<see cref="TownBuilding.Turret"/>), the AI's turret posts. Beds are interior-layout objects with a BED or
/// SKELETON_BED functionality (docs/game/buildings-production.md, BuildingFunction 6 and 25, found by the type of the referenced records rather than
/// a field name); seats are those with a CHAIR functionality (17); nodes are its <c>is node</c> objects, their <c>node type</c> kept and their kind
/// read from it (0 shopkeeper, 2 guard post), else from the record name (<see cref="NodeKind(string)"/>).
/// </summary>
public static class TownBuildings
{
    /// <summary>
    /// BUILDING_FUNCTIONALITY <c>function</c> 6 (BED) and 25 (SKELETON_BED): beds (<see cref="IsBed"/>).
    /// </summary>
    public const int BedFunction = 6, SkeletonBedFunction = 25;
    /// <summary>
    /// BUILDING_FUNCTIONALITY <c>function</c> 8, BF_CAGE: cages, the prisoner pole, the peeler (docs/game/locks.md "Data").
    /// </summary>
    public const int CageFunction = 8;

    /// <summary>
    /// BUILDING_FUNCTIONALITY <c>function</c> 21, BF_GATE (docs/game/buildings-production.md "Classes and functions").
    /// </summary>
    public const int GateFunction = 21;

    /// <summary>
    /// BUILDING_FUNCTIONALITY <c>function</c> 17, BF_CHAIR: stools, benches, sitting pillows and spots (docs/game/ai-tasks.md "Town goals").
    /// </summary>
    public const int ChairFunction = 17;
    /// <summary>
    /// BF_THRONE ("throne chair", sat on like a chair) and BF_TRAINING (a training dummy): the other furniture sitting around (275) takes
    /// (docs/game/ai-tasks.md "Town goals").
    /// </summary>
    public const int ThroneFunction = 24, TrainingFunction = 7;
    /// <summary>
    /// BF_MINE, BF_RESEARCH, BF_CRAFTING: with <see cref="RefineryFunction"/> and the chair and training function, the furniture staying home (43)
    /// operates in pretence (mask 0x2049a of <c>FUN_1408cbc00</c>).
    /// </summary>
    public const int MineFunction = 1, ResearchFunction = 3, CraftingFunction = 10;
    /// <summary>
    /// BF_TABLE, BF_CORPSE_DISPOSAL, BF_REFINERY: what a free seat faces (<see cref="SeatYaw"/>).
    /// </summary>
    public const int TableFunction = 16, CorpseDisposalFunction = 11, RefineryFunction = 4;
    /// <summary>
    /// BF_RESOURCE_STORAGE, BF_SHOP, BF_GENERAL_STORAGE: the containers a building shop's stock goes into (<see cref="TownFixture.HoldsShopStock"/>).
    /// </summary>
    public const int ResourceStorageFunction = 2, ShopFunction = 9, GeneralStorageFunction = 13;

    /// <summary>
    /// Whether a BF_CAGE functionality is a torture rack (the class <c>TortureBuilding</c>; <b>Verified</b>, factory <c>FUN_14057cc70</c>, function
    /// 8): its <c>produces</c> list is not empty, or its
    /// <c>flags</c> are not 0. Base data has none (the peeler, cooking pole, drill and hooks come from mods; docs/game/locks.md "Torture racks").
    /// </summary>
    public static bool IsRack(GameRecord functionality) =>
        functionality.GetInt("function") == CageFunction && (functionality.GetReferences("produces").Count > 0 || functionality.GetInt("flags") != 0);

    /// <summary>
    /// A gate, a boundary of the gate codes (docs/game/pathfinding.md "Gate codes"): a wall gate the player builds (<c>is wall gate</c>) or a
    /// building whose functionality is BF_GATE (engine reading of the original's GatewayBuilding class, which the factory picks by the
    /// function, <b>Observed</b>).
    /// </summary>
    public static bool IsGate(GameDatabase db, GameRecord building, Dictionary<string, GameRecord?> cache) =>
        building.GetBool("is wall gate") || FunctionOf(db, building, cache)?.GetInt("function") == GateFunction;

    /// <summary>
    /// The buildings by town placement id (<see cref="TownPlacement.InstanceId"/>), each list in placement order. Without <paramref name="contents"/>
    /// their interior layouts are not laid out (no spots, fixtures, item slots, tables or furniture, no shop or bar): enough to house
    /// residents (<see cref="ResidentHomes.Assign"/>).
    /// </summary>
    public static Dictionary<string, List<TownBuilding>> Build(GameDatabase db, WorldLevelData levels, BuildingLayouts? layouts,
        Func<double, double, float> terrainHeight, bool contents = true)
    {
        var builder = new TownBuildingBuilder(db, levels, layouts, terrainHeight, contents);
        var result = new Dictionary<string, List<TownBuilding>>(StringComparer.Ordinal);
        foreach (var b in levels.Buildings())
        {
            if (builder.Build(b) is not var (town, building)) continue;
            if (!result.TryGetValue(town, out var list)) result[town] = list = [];
            list.Add(building);
        }
        return result;
    }

    /// <summary>
    /// <paramref name="b"/> laid out with other layout names (docs/game/factions-squads-towns.md "Town residents", Layouts): a town override's or a
    /// loaded save's residents name them as the level data's do not. Its contents come from them, and the names ride on its placement
    /// (<see cref="BuildingPlacement.InteriorLayout"/>) so every reader lays it out alike. A building without a level state or placement stays as
    /// it is.
    /// </summary>
    public static TownBuilding Relay(GameDatabase db, BuildingLayouts layouts, TownBuilding b, string interior, string exterior,
        BuildingNaming? naming = null)
    {
        if (b.Resolved is not { } resolved || b.LevelState is not { } state) return b;
        if (b.LayoutName(exterior: false) == interior && b.LayoutName(exterior: true) == exterior) return b with { Naming = naming ?? b.Naming };
        var placed = resolved with { Placement = resolved.Placement with { InteriorLayout = interior, ExteriorLayout = exterior } };
        var functions = new Dictionary<string, GameRecord?>(StringComparer.Ordinal);
        var c = TownBuildingContents.Lay(db, layouts, placed, state, new(StringComparer.Ordinal), functions);
        c.AddAlone(db, placed, FunctionOf(db, resolved.Record, functions));
        return b with
        {
            IsShop = c.Shop,
            IsBar = c.Bar,
            Spots = c.Spots,
            Fixtures = c.Fixtures,
            ItemSlots = c.ItemSlots,
            Tables = c.Tables,
            Furniture = c.Furniture,
            Furnished = c.Furnished,
            InteriorLayout = interior,
            Resolved = placed,
            Naming = naming ?? b.Naming,
            Generators = GeneratorCount(layouts, placed, state),
        };
    }

    /// <summary>
    /// A generator has positive power output and no power capacity; a building with capacity is a battery.
    /// </summary>
    public static bool IsGenerator(GameRecord building) => building.GetInt("power capacity") <= 0 && building.GetInt("power output") > 0;

    /// <summary>
    /// The generators a placed building is and its interior and exterior layouts hold, whether it stands or not.
    /// </summary>
    internal static int GeneratorCount(BuildingLayouts layouts, ResolvedBuilding resolved, GameRecord? state)
    {
        var record = resolved.Record;
        var placement = resolved.Placement;
        int count = IsGenerator(record) ? 1 : 0;
        if (state is null) return count;

        foreach (bool exterior in (bool[])[false, true])
            if (layouts.Find(record, placement.LayoutName(state, exterior), exterior) is { } layout)
                count += layouts.Items(layout, placement).Count(item => IsGenerator(item.Building));

        return count;
    }

    /// <summary>
    /// The kind of a node from its <c>node type</c> alone (<c>FUN_14038c2f0</c>): 0 shopkeeper, 1 general, 2 guard post, whatever the name. Types 3
    /// (turret), 4 (use) and 5 (light) are never general: the record's name only gives the engine's bar and throne stand-ins,
    /// else <see cref="HomeSpotKind.Other"/>.
    /// </summary>
    public static HomeSpotKind NodeKind(GameRecord node) => node.GetInt("node type") switch
    {
        0 => HomeSpotKind.Shopkeeper,
        1 => HomeSpotKind.General,
        2 => HomeSpotKind.Guard,
        _ => NodeKind(node.Name) is var named && named is HomeSpotKind.Bar or HomeSpotKind.Throne ? named : HomeSpotKind.Other,
    };

    /// <summary>
    /// The kind of a node from its record's name (case ignored).
    /// </summary>
    public static HomeSpotKind NodeKind(string name) =>
        name.Contains("shop", StringComparison.OrdinalIgnoreCase) ? HomeSpotKind.Shopkeeper
        : name.Contains("guard", StringComparison.OrdinalIgnoreCase) ? HomeSpotKind.Guard
        : name.Contains("throne", StringComparison.OrdinalIgnoreCase) ? HomeSpotKind.Throne
        : name.Contains("bar", StringComparison.OrdinalIgnoreCase) ? HomeSpotKind.Bar
        : HomeSpotKind.General;

    /// <summary>
    /// A BUILDING with a BUILDING_FUNCTIONALITY record of function BED or SKELETON_BED in any of its reference lists (cached by id).
    /// </summary>
    public static bool IsBed(GameDatabase db, GameRecord building, Dictionary<string, bool> cache)
    {
        if (cache.TryGetValue(building.StringId, out bool known)) return known;
        bool bed = false;
        foreach (var list in building.ReferenceLists)
            foreach (var r in building.GetReferences(list))
            {
                if (db.Find(r.TargetStringId) is { Type: FcsRecordType.BuildingFunctionality } f
                    && f.GetInt("function") is BedFunction or SkeletonBedFunction)
                    bed = true;
            }
        cache[building.StringId] = bed;
        return bed;
    }

    /// <summary>
    /// The first BUILDING_FUNCTIONALITY record in any reference list of <paramref name="building"/> (its <c>function</c>, <c>restrains movement</c>,
    /// <c>use range</c>), cached by id; null for a building without one.
    /// </summary>
    public static GameRecord? FunctionOf(GameDatabase db, GameRecord building, Dictionary<string, GameRecord?> cache)
    {
        if (cache.TryGetValue(building.StringId, out var known)) return known;
        GameRecord? found = null;
        foreach (var list in building.ReferenceLists)
        {
            foreach (var r in building.GetReferences(list))
                if (db.Find(r.TargetStringId) is { Type: FcsRecordType.BuildingFunctionality } f) { found = f; break; }
            if (found is not null) break;
        }
        cache[building.StringId] = found;
        return found;
    }

    /// <summary>
    /// The way a seat is sat facing (docs/game/ai-tasks.md "Chairs"): by its functionality's <c>flags</c>, 0 its own <paramref name="yaw"/>, 1
    /// towards the nearest of <paramref name="faced"/> (the building's tables, corpse disposals and refineries), 2 its own facing when that one is in
    /// front, else the opposite one. <paramref name="faithful"/>: behind it, as the game does, the absolute look point negated (generally towards the
    /// world origin).
    /// </summary>
    public static float SeatYaw(Vector3 position, float yaw, int flags, IEnumerable<Vector3> faced, bool faithful = false)
    {
        if (flags is not (1 or 2)) return yaw;
        Vector3 nearest = position;
        float gap = float.MaxValue;
        foreach (var p in faced)
        {
            float d = Vector3.DistanceSquared(p, position);
            if (d < gap) (nearest, gap) = (p, d);
        }
        var to = new Vector2(nearest.X - position.X, nearest.Z - position.Z);
        if (to.LengthSquared() < 1e-6f) return yaw;
        if (flags == 1) return MathF.Atan2(to.X, to.Y);
        var forward = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
        if (Vector2.Dot(new Vector2(forward.X, forward.Z), to) >= 0) return yaw;
        if (!faithful) return MathF.IEEERemainder(yaw + MathF.PI, MathF.Tau);
        // The game negates the world-space look point, not the forward vector (docs/game/ai-tasks.md "Seat facing").
        var reflected = -(position + forward) - position;
        return MathF.Atan2(reflected.X, reflected.Z);
    }

    /// <summary>
    /// The simulation's yaw (0 along +Z, turning towards +X) of a rotation.
    /// </summary>
    public static float YawOf(Quaternion rotation)
    {
        var q = rotation.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(rotation);
        var forward = Vector3.Transform(Vector3.UnitZ, q);
        return MathF.Atan2(forward.X, forward.Z);
    }
}
