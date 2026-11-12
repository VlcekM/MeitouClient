using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay.Construction;

/// <summary>
/// One material entry of a building: the ITEM record and the amount the site needs (docs/game/buildings-production.md "Construction state").
/// </summary>
public readonly record struct MaterialCost(string Item, float Required);

/// <summary>
/// The build fields of a BUILDING record (docs/simulation.md "Buildings"; meanings in docs/game/buildings-production.md "Construction" and
/// "Placement"). Shared by the simulation (sites, rate, refund) and the host (build menu, placement ghost).
/// </summary>
public sealed record BuildingBuildData
{
    /// <summary>
    /// Building Materials, the item of the <c>build materials</c> fallback entry.
    /// </summary>
    public const string BuildingMaterialsItem = "580-gamedata.base";

    /// <summary>
    /// The BUILDING's string id.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The BUILDING's name in the active language.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// <c>building category</c>, default "misc".
    /// </summary>
    public string Category { get; init; } = "misc";

    /// <summary>
    /// <c>building group</c>, default the building's own name.
    /// </summary>
    public string Group { get; init; } = "";

    /// <summary>
    /// One entry per <c>construction</c> reference (first value), else Building Materials x <c>build materials</c> when that is above 0, else none
    /// (completes at once).
    /// </summary>
    public IReadOnlyList<MaterialCost> Materials { get; init; } = [];

    /// <summary>
    /// <c>build materials</c> (the build menu's default pick within a group is the highest).
    /// </summary>
    public int BuildMaterials { get; init; }

    /// <summary>
    /// <c>build speed mult</c>: the rate is this / (1200/11) per second.
    /// </summary>
    public float BuildSpeedMult { get; init; } = 1;

    /// <summary>
    /// <c>build threshold</c> in per cent (the look's threshold is clamp(this / 100, 0, 0.95)).
    /// </summary>
    public float BuildThreshold { get; init; }

    /// <summary>
    /// <c>path mode</c> (0 ignore, 1 projected, 2 obstacle, 3 walkable).
    /// </summary>
    public int PathMode { get; init; } = 2;

    /// <summary>
    /// <c>max slope</c> (default 0; docs/game/buildings-production.md "Placement").
    /// </summary>
    public float MaxSlope { get; init; }

    /// <summary>
    /// <c>match slope</c> (default false).
    /// </summary>
    public bool MatchSlope { get; init; }

    /// <summary>
    /// <c>vertical position tolerance</c> (default 0).
    /// </summary>
    public float VerticalPositionTolerance { get; init; }

    /// <summary>
    /// The record's <c>scale</c> (a part's footprint nodes are laid out in it, <c>FUN_140429470</c>); 1 when missing or not positive.
    /// </summary>
    public float Scale { get; init; } = 1;

    /// <summary>
    /// <c>creates player town</c> (default false): kept further from towns, and placed outside one it makes an outpost
    /// (docs/game/buildings-production.md "Placement").
    /// </summary>
    public bool CreatesPlayerTown { get; init; }

    /// <summary>
    /// <c>links together</c>: a wall piece (ghost class <c>PreviewBuilding_Wall</c>); it joins a town but never makes one itself
    /// (docs/game/buildings-production.md "Placement").
    /// </summary>
    public bool LinksTogether { get; init; }

    /// <summary>
    /// <c>is interior furniture</c> (default false).
    /// </summary>
    public bool IsInteriorFurniture { get; init; }

    /// <summary>
    /// <c>is exterior furniture</c> (default false).
    /// </summary>
    public bool IsExteriorFurniture { get; init; }

    /// <summary>
    /// <c>is node</c> (default false).
    /// </summary>
    public bool IsNode { get; init; }

    /// <summary>
    /// <c>ceiling placement</c> (default false): the ghost needs a ceiling.
    /// </summary>
    public bool CeilingPlacement { get; init; }

    /// <summary>
    /// <c>is door</c>: build work x3, never completes itself, no refund (out of scope this milestone).
    /// </summary>
    public bool IsDoor { get; init; }

    /// <summary>
    /// <c>link type</c> (WallSectionLinkType): 0 normal, 1 connector, 2 lower wedge, 3 single (towers, gates), 4 short.
    /// </summary>
    public int LinkType { get; init; }

    /// <summary>
    /// <c>link length</c> x <c>scale</c>: the piece runs from its position this far along its own +Z.
    /// </summary>
    public float LinkLength { get; init; }

    /// <summary>
    /// <c>building height</c>.
    /// </summary>
    public float BuildingHeight { get; init; } = 100;

    /// <summary>
    /// <c>is wall gate</c> (default false).
    /// </summary>
    public bool IsWallGate { get; init; }

    /// <summary>
    /// Whether the <c>interior</c> list names any part: the original then gives the building its interior object (Building <c>+0x1f0</c>,
    /// <c>FUN_140551b20</c>).
    /// </summary>
    public bool HasInterior { get; init; }

    /// <summary>
    /// The <c>wall subsections</c> (BUILDING ids): the shorter pieces, connector and wedge a line of this wall uses.
    /// </summary>
    public IReadOnlyList<string> WallSubsections { get; init; } = [];

    /// <summary>
    /// The <c>snaps to</c> list (BUILDING ids): what the ghost snaps to, taking that building's place and turn (fcs.def: "aggressively snap to
    /// others. Used for ramps and stairs"; docs/game/buildings-production.md "Snapping to a building").
    /// </summary>
    public IReadOnlyList<string> SnapsTo { get; init; } = [];

    /// <summary>
    /// The site's target: the sum of the required amounts (0 completes at once).
    /// </summary>
    public float Target
    {
        get
        {
            float sum = 0;
            foreach (var m in Materials) sum += m.Required;
            return sum;
        }
    }

    /// <summary>
    /// The progress per game second of one unit of build work: <c>build speed mult</c> / (1200/11) (docs/game/buildings-production.md "Construction
    /// state"). 0 or below completes a site at once.
    /// </summary>
    public float Rate => BuildSpeedMult / (1200f / 11f);

    /// <summary>
    /// The build window's "Estimated build time" in game hours (docs/game/buildings-production.md "Construction tick", <b>Verified</b>): <c>materials
    /// / (build speed mult x build speed x option)</c>, for a builder whose engineering factor is 1 (engineering 41.2). <paramref name="buildSpeed"/>
    /// is CONSTANTS <c>build speed</c>; the window shows whole hours. Infinity for a rate of 0 or below.
    /// </summary>
    public float EstimatedHours(float buildSpeed, float option = 1)
    {
        float perHour = BuildSpeedMult * buildSpeed * option;
        return perHour > 0 ? Target / perHour : float.PositiveInfinity;
    }

    /// <summary>
    /// Reads the build fields of a BUILDING record; the defaults are those named on each property.
    /// </summary>
    public static BuildingBuildData From(GameRecord r)
    {
        var refs = r.GetReferences("construction");
        int bulk = RecordReading.Int(r, "build materials", 0);
        IReadOnlyList<MaterialCost> materials = refs.Count > 0
            ? [.. refs.Select(c => new MaterialCost(c.TargetStringId, c.Values.Value0))]
            : bulk > 0 ? [new MaterialCost(BuildingMaterialsItem, bulk)] : [];

        return new BuildingBuildData
        {
            Id = r.StringId,
            Name = r.Name,
            Category = r.GetString("building category") is { Length: > 0 } c ? c : "misc",
            Group = r.GetString("building group") is { Length: > 0 } g ? g : r.Name,
            Materials = materials,
            BuildMaterials = bulk,
            BuildSpeedMult = RecordReading.Float(r, "build speed mult", 1),
            BuildThreshold = RecordReading.Float(r, "build threshold", 0),
            PathMode = RecordReading.Int(r, "path mode", 2),
            MaxSlope = RecordReading.Float(r, "max slope", 0),
            MatchSlope = RecordReading.Bool(r, "match slope", false),
            VerticalPositionTolerance = RecordReading.Float(r, "vertical position tolerance", 0),
            Scale = RecordReading.Float(r, "scale", 1) is > 0 and var scale ? scale : 1,
            CreatesPlayerTown = RecordReading.Bool(r, "creates player town", false),
            LinksTogether = RecordReading.Bool(r, "links together", false),
            IsInteriorFurniture = RecordReading.Bool(r, "is interior furniture", false),
            IsExteriorFurniture = RecordReading.Bool(r, "is exterior furniture", false),
            IsNode = RecordReading.Bool(r, "is node", false),
            CeilingPlacement = RecordReading.Bool(r, "ceiling placement", false),
            IsDoor = RecordReading.Bool(r, "is door", false),
            LinkType = RecordReading.Int(r, "link type", 0),
            LinkLength = RecordReading.Float(r, "link length", 0) * RecordReading.Float(r, "scale", 1),
            BuildingHeight = RecordReading.Float(r, "building height", 100),
            IsWallGate = RecordReading.Bool(r, "is wall gate", false),
            HasInterior = r.GetReferences("interior").Count > 0,
            WallSubsections = [.. r.GetReferences("wall subsections").Select(s => s.TargetStringId)],
            SnapsTo = [.. r.GetReferences("snaps to").Select(s => s.TargetStringId)],
        };
    }

    /// <summary>
    /// Every BUILDING of the database, in its order.
    /// </summary>
    public static List<BuildingBuildData> LoadAll(GameDatabase db) => [.. db.OfType(FcsRecordType.Building).Select(From)];
}
