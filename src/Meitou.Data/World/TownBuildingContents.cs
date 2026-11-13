using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using static Meitou.Data.World.TownBuildings;

namespace Meitou.Data.World;

/// <summary>
/// What a building's layouts put in it (<see cref="TownBuilding"/>'s spots, fixtures, item slots, tables, furniture, shop and bar flags).
/// </summary>
sealed class TownBuildingContents
{
    /// <summary>
    /// The beds, nodes and seats of its interior layout (<see cref="TownBuilding.Spots"/>).
    /// </summary>
    public List<HomeSpot> Spots = [];
    /// <summary>
    /// The exterior layout's <c>is shop</c> and <c>is bar</c>, and whether the interior layout placed anything (<see cref="TownBuilding.Furnished"/>).
    /// </summary>
    public bool Shop, Bar, Furnished;
    /// <summary>
    /// The cages, lockable objects and containers of its interior layout.
    /// </summary>
    public List<TownFixture> Fixtures = [];
    /// <summary>
    /// The loose-item slots of its interior layout.
    /// </summary>
    public List<TownItemSlot> ItemSlots = [];
    /// <summary>
    /// The tables, corpse disposals and refineries of its interior layout (what a free seat faces).
    /// </summary>
    public List<Vector3> Tables = [];
    /// <summary>
    /// The thrones, dummies and workstations of its interior layout, and the building itself when it stands alone as one.
    /// </summary>
    public List<HomeSpot> Furniture = [];
    /// <summary>
    /// Its interior layout's other furniture (<see cref="TownBuilding.Pieces"/>).
    /// </summary>
    public List<TownPiece> Pieces = [];

    /// <summary>
    /// The contents of a placed building's layouts, named by its placement or else its state (<see cref="BuildingPlacement.LayoutName"/>): the
    /// exterior's shop and bar flags, the interior's beds, nodes, seats, furniture, tables, fixtures and item slots. Its interior holds objects only
    /// from a named layout the data has (none named, or one the data lacks, loads nothing); a ruin holds none, as destroying it clears them
    /// (docs/game/buildings-production.md "Ruined by residents").
    /// </summary>
    public static TownBuildingContents Lay(GameDatabase db, BuildingLayouts layouts, ResolvedBuilding resolved, GameRecord state,
        Dictionary<string, bool> beds, Dictionary<string, GameRecord?> functions)
    {
        var c = new TownBuildingContents();
        var record = resolved.Record;
        var b = resolved.Placement;
        if (layouts.Find(record, b.LayoutName(state, exterior: true)) is { } exterior)
        {
            c.Shop = exterior.GetInt("is shop") != 0;
            c.Bar = exterior.GetInt("is bar") != 0;
        }

        if (resolved.Destroyed || layouts.Find(record, b.LayoutName(state, exterior: false), exterior: false) is not { } interior) return c;
        c.LayInterior(db, layouts, resolved, interior, beds, functions);
        return c;
    }

    /// <summary>
    /// The spots of a placed building's interior layout, named by its placement alone (no town or state is read); none for a ruin.
    /// </summary>
    public static IReadOnlyList<HomeSpot> LaySpots(GameDatabase db, BuildingLayouts layouts, ResolvedBuilding resolved)
    {
        var c = new TownBuildingContents();
        string layout = resolved.Placement.LayoutName(null, exterior: false);
        if (!resolved.Destroyed && layouts.Find(resolved.Record, layout, exterior: false) is { } interior)
            c.LayInterior(db, layouts, resolved, interior, new(), new());

        return c.Spots;
    }

    /// <summary>
    /// Standing alone in the town, a chair, throne or dummy is a piece of furniture itself.
    /// </summary>
    public void AddAlone(GameDatabase db, ResolvedBuilding resolved, GameRecord? functionality)
    {
        if (functionality is not null
            && FurnitureOf(db, resolved.Record, functionality, resolved.Position, YawOf(resolved.Placement.Rotation)) is { } alone)
            Furniture.Add(alone);
    }

    /// <summary>
    /// The interior layout's objects, then its item slots, then the seats turned towards the tables.
    /// </summary>
    void LayInterior(GameDatabase db, BuildingLayouts layouts, ResolvedBuilding resolved, GameRecord interior, Dictionary<string, bool> beds,
        Dictionary<string, GameRecord?> functions)
    {
        var seats = LayItems(db, layouts, resolved, interior, beds, functions);
        LayItemSlots(layouts, resolved, interior);
        FaceSeats(seats);
    }

    /// <summary>
    /// The interior layout's objects in layout order: beds and nodes, chairs, other furniture, tables and fixtures, and as <see cref="Pieces"/>
    /// whatever no bed, node, seat, cage or container pick takes; returns the chairs' spot indices and functionality flags (null: none).
    /// </summary>
    List<(int Spot, int Flags)>? LayItems(GameDatabase db, BuildingLayouts layouts, ResolvedBuilding resolved, GameRecord interior,
        Dictionary<string, bool> beds, Dictionary<string, GameRecord?> functions)
    {
        var b = resolved.Placement;
        int index = 0;
        List<(int Spot, int Flags)>? seats = null;
        foreach (var item in layouts.Items(interior, b))
        {
            var (at, rotation) = BuildingLayouts.Place(resolved.Position, b.Rotation, item);
            var function = item.Building.GetBool("is node") || IsBed(db, item.Building, beds) ? null : FunctionOf(db, item.Building, functions);
            bool picked = LaySpot(db, item, at, rotation, beds, function, ref seats);

            string id = $"{b.Key}/{index}";
            if (function?.GetInt("function") is TableFunction or CorpseDisposalFunction or RefineryFunction) Tables.Add(at);
            else picked |= AddFixture(db, functions, item, id, at, rotation);

            if (!picked) Pieces.Add(new TownPiece(id, item.Building.StringId, at, rotation, item.Floor, item.Outside));
            index++;
        }
        Furnished = index > 0;
        return seats;
    }

    /// <summary>
    /// One layout object as a bed or node spot, a chair, or other furniture (<see cref="Furniture"/>); returns whether it stays out of
    /// <see cref="Pieces"/>: a bed or a seat, which have picks of their own, or a node, which has no shape to click; not a dummy or workstation.
    /// </summary>
    bool LaySpot(GameDatabase db, LayoutItem item, Vector3 at, Quaternion rotation, Dictionary<string, bool> beds, GameRecord? function,
        ref List<(int Spot, int Flags)>? seats)
    {
        bool node = item.Building.GetBool("is node");
        HomeSpotKind? kind = node ? NodeKind(item.Building) : IsBed(db, item.Building, beds) ? HomeSpotKind.Bed : null;

        // A node marker looks along its local -X (docs/game/ai-tasks.md "Town goals"), a bed along +Z.
        if (kind is { } k)
        {
            Spots.Add(new HomeSpot(k, at, YawOf(rotation) - (node ? MathF.PI / 2 : 0))
            {
                NodeType = node ? item.Building.GetInt("node type") : -1,
                Record = k == HomeSpotKind.Bed ? item.Building.StringId : null,
                Marker = k == HomeSpotKind.Bed ? WorldObjectLayout.PositionMarker(db, item.Building, at, rotation) : null,
            });
            return true;
        }

        if (function?.GetInt("function") == ChairFunction)
        {
            (seats ??= []).Add((Spots.Count, function.GetInt("flags")));
            Spots.Add(new HomeSpot(HomeSpotKind.Chair, at, YawOf(rotation))
            {
                Record = item.Building.StringId,
                SitClip = function.GetReferences("animation").Select(r => db.Find(r.TargetStringId)?.Name).FirstOrDefault(n => n is not null),
                HungerRate = function.GetFloat("hunger rate", 1),
            });
            return true;
        }

        if (function is null || FurnitureOf(db, item.Building, function, at, YawOf(rotation), inside: true) is not { } piece) return false;

        Furniture.Add(piece);
        return piece.Kind == HomeSpotKind.ThroneSeat;
    }

    /// <summary>
    /// The interior layout's item placement groups, each a loose-item slot.
    /// </summary>
    void LayItemSlots(BuildingLayouts layouts, ResolvedBuilding resolved, GameRecord interior)
    {
        var b = resolved.Placement;
        int slot = 0;
        foreach (var group in layouts.ItemGroups(interior))
        {
            var (at, rotation) = BuildingLayouts.Place(resolved.Position, b.Rotation, group);
            ItemSlots.Add(new TownItemSlot($"{b.Key}/item{slot++}", group.Building.StringId, at, rotation, group.Floor));
        }
    }

    /// <summary>
    /// Turns each chair to the way it is sat facing (<see cref="TownBuildings.SeatYaw"/>), when there are chairs and tables.
    /// </summary>
    void FaceSeats(List<(int Spot, int Flags)>? seats)
    {
        var spots = Spots;
        if (seats is null || Tables.Count == 0) return;
        foreach (var (s, flags) in seats)
            spots[s] = spots[s] with
            {
                Yaw = SeatYaw(spots[s].Position, spots[s].Yaw, flags, Tables),
                FaithfulYaw = SeatYaw(spots[s].Position, spots[s].Yaw, flags, Tables, faithful: true),
            };
    }

    /// <summary>
    /// A cage, a lockable object or a container with an inventory of the interior layout (<see cref="TownFixture"/>); nothing for anything else.
    /// Returns whether a pick of its own selects it: a cage or a container, not a lock alone.
    /// </summary>
    bool AddFixture(GameDatabase db, Dictionary<string, GameRecord?> functions, LayoutItem item, string id, Vector3 at, Quaternion rotation)
    {
        var itemFn = FunctionOf(db, item.Building, functions)?.GetInt("function");
        if (itemFn != CageFunction && !item.Building.GetBool("has lock") && !item.Building.GetBool("has inventory")) return false;
        var (storageWidth, storageHeight) = item.Building.GetBool("has inventory") ? MachineData.StorageGrid(db, item.Building) : (0, 0);
        Fixtures.Add(new TownFixture(id, item.Building.StringId, itemFn == CageFunction, at, YawOf(rotation))
        {
            Function = itemFn ?? -1,
            StorageWidth = storageWidth,
            StorageHeight = storageHeight,
        });
        return itemFn == CageFunction || item.Building.GetBool("has inventory");
    }

    /// <summary>
    /// A seat-like piece for sitting around (275, <see cref="TownBuilding.Furniture"/>) from a functionality: BF_CHAIR and BF_THRONE are sat on (its
    /// <c>animation</c> and <c>hunger rate</c>), BF_TRAINING is stood at (and keeps its BUILDING, the dummy's stat and cap), and an interior piece of
    /// function MINE, RESEARCH, REFINERY or CRAFTING is a <see cref="HomeSpotKind.Workstation"/>; null for any other function.
    /// </summary>
    static HomeSpot? FurnitureOf(GameDatabase db, GameRecord building, GameRecord function, Vector3 at, float yaw, bool inside = false)
    {
        var kind = function.GetInt("function") switch
        {
            ChairFunction => HomeSpotKind.Chair,
            ThroneFunction => HomeSpotKind.ThroneSeat,
            TrainingFunction => HomeSpotKind.Training,
            MineFunction or ResearchFunction or RefineryFunction or CraftingFunction when inside => HomeSpotKind.Workstation,
            _ => (HomeSpotKind?)null
        };
        if (kind is not { } k) return null;
        return new HomeSpot(k, at, yaw)
        {
            Record = building.StringId,
            SitClip = k is HomeSpotKind.Training or HomeSpotKind.Workstation
                ? null
                : function.GetReferences("animation")
                    .Select(r => db.Find(r.TargetStringId)?.Name)
                    .FirstOrDefault(n => n is not null),
            HungerRate = function.GetFloat("hunger rate", 1),
        };
    }
}
