using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>
/// Area ids of the navmesh builder. They are Recast area ids, so where two surfaces meet in one column the larger id wins:
/// door over water over ground; <see cref="Null"/> is "not walkable" (a cutting obstacle). The original's face data are
/// 3 (water), 4 (open door), 5 (closed door) (docs/game/pathfinding.md, "Face data"); a closed door is a run-time state.
/// </summary>
public static class NavArea
{
    public const byte Null = 0;
    public const byte Ground = 1;
    /// <summary>Water by depth: shallow, waist-deep and deep, the wet water states 1..3 of a size-1 swimmer (<see cref="WetState"/>; docs/game/pathfinding.md "Water costs").</summary>
    public const byte Water = 2, WaterWaist = 4, WaterDeep = 5;
    public const byte Door = 3;

    public static bool IsWalkable(byte area) => area != Null;
    public static bool IsWater(byte area) => WetState(area) > 0;
    /// <summary>The water state 1..3 a water area stands for; 0 for any other.</summary>
    public static int WetState(byte area) => area switch { Water => 1, WaterWaist => 2, WaterDeep => 3, _ => 0 };
    public static byte OfWetState(int state) => state switch { 3 => WaterDeep, 2 => WaterWaist, _ => Water };
}

/// <summary>A convex prism: a convex polygon on the XZ plane between two heights. Used for carvers (removes the mesh) and door painters.</summary>
internal sealed class NavVolume(Vector2[] polygon, float yMin, float yMax)
{
    /// <summary>For a door painter: the <see cref="Meitou.Data.World.BuildingKey"/> of the building whose door it is (<see cref="NavDoors"/>).</summary>
    public string? Owner { get; set; }
    /// <summary>Convex polygon on (X, Z), counter-clockwise seen from above (+Y).</summary>
    public Vector2[] Polygon { get; } = polygon;
    public float YMin { get; } = yMin;
    public float YMax { get; } = yMax;
}

/// <summary>
/// A closed convex collision shape of a walkable part, as planes (unit normal outward; a point p is inside when every <c>Dot(n, p) + d</c> is below 0).
/// Recast rasterises surfaces, not volumes, so the ground inside a solid foundation would stay as a floor under it; the builder removes the walkable spans
/// inside (docs/game/pathfinding.md, "Solids").
/// </summary>
internal sealed class NavSolid(Vector4[] planes, Vector2 min, Vector2 max)
{
    public Vector4[] Planes { get; } = planes;
    /// <summary>Debug (the navmesh tool): the placement, owner and part the shape came from.</summary>
    public string? Owner { get; set; }
    /// <summary>A map feature's or foliage shape, not a building's: a feature's surface running inside it keeps its seam (<see cref="NavMeshSolid"/>).</summary>
    public bool Feature { get; set; }
    /// <summary>The shape's box on (X, Z).</summary>
    public Vector2 Min { get; } = min;
    public Vector2 Max { get; } = max;
}

/// <summary>
/// A placed collision triangle mesh (a map feature's rock), as its triangles (three points each, outward-facing). The span of a column is inside when the nearest
/// triangle above it faces up, i.e. the span sees its back: a rock sunk into the terrain is open at the bottom, so a parity count would not do; the underside of
/// an overhang faces down and leaves the ground under it (docs/game/pathfinding.md, "Map features").
/// </summary>
internal sealed class NavMeshSolid(Vector3[] triangles, Vector2 min, Vector2 max)
{
    public Vector3[] Triangles { get; } = triangles;
    /// <summary>Debug (the navmesh tool): what the mesh came from.</summary>
    public string? Owner { get; set; }
    /// <summary>The mesh's box on (X, Z).</summary>
    public Vector2 Min { get; } = min;
    public Vector2 Max { get; } = max;
}

/// <summary>Counters of what a gather found, for logs and the debug tool.</summary>
internal sealed class GatherStats
{
    public int Buildings, PartsWithCollision, Shapes, FoliageInstances, FoliageShapes, FoliageMeshes, FoliageCutters, MissingFiles;
    public int WalkableTriangles, CuttingTriangles, TerrainTriangles, WaterTriangles;
    public int Carvers, Painters, DoorSeeds, SeedsDropped, WallSeeds, Solids, MapFeatures;
}

/// <summary>
/// Everything the navmesh builder needs for one zone (docs/game/pathfinding.md, "What our builder needs"): triangles in world space (Y up,
/// counter-clockwise seen from outside, so Recast's normal test works) with a per-triangle area, carvers, door painters and seed points.
/// </summary>
internal sealed class ZoneGeometry
{
    public required ZoneCoordinate Zone { get; init; }
    /// <summary>World box of the zone (X and Z); triangles reach <see cref="Margin"/> beyond it.</summary>
    public Vector2 ZoneMin { get; init; }
    public Vector2 ZoneMax { get; init; }
    public float Margin { get; init; }
    public float MinY { get; set; } = float.MaxValue;
    public float MaxY { get; set; } = float.MinValue;

    /// <summary>x, y, z per vertex.</summary>
    public List<float> Vertices { get; } = [];
    /// <summary>Three vertex indices per triangle.</summary>
    public List<int> Indices { get; } = [];
    /// <summary>One <see cref="NavArea"/> per triangle.</summary>
    public List<byte> Areas { get; } = [];

    /// <summary>Volumes that remove the walkable mesh inside them (interior hulls, foliage cutters, convex obstacles).</summary>
    public List<NavVolume> Carvers { get; } = [];
    /// <summary>Closed convex shapes of walkable parts: the walkable spans inside them are removed.</summary>
    public List<NavSolid> Solids { get; } = [];
    /// <summary>Placed triangle meshes of map features: the walkable spans inside them are removed (<see cref="NavMeshSolid"/>).</summary>
    public List<NavMeshSolid> MeshSolids { get; } = [];
    /// <summary>Door hulls: walkable cells inside become <see cref="NavArea.Door"/>.</summary>
    public List<NavVolume> Painters { get; } = [];
    /// <summary>Exterior doors' outer markers with the height of the door's base, until every surface is gathered (then they become <see cref="Seeds"/>).</summary>
    public List<(Vector2 At, float Base)> DoorMarkers { get; } = [];
    /// <summary>Seed points: regions of the mesh near one stay, the rest is pruned (docs/game/pathfinding.md, Generator "Seeds" and `regionPruningSettings`).</summary>
    public List<Vector3> Seeds { get; } = [];
    /// <summary>
    /// For an interior mesh: its building's first shopkeeper or general node, else (no door either) the original's ray seed on its floor, the seed when
    /// <see cref="Seeds"/> (the inner door markers) find nothing (docs/game/pathfinding.md, "Interior seeds").
    /// </summary>
    public List<Vector3> NodeSeeds { get; } = [];

    /// <summary>For an interior mesh: the convex hull of the building interior mask; the mesh is clipped to it.</summary>
    public NavVolume? InteriorHull { get; init; }
    /// <summary>For an interior mesh: the <see cref="Meitou.Data.World.BuildingKey"/> of the building it belongs to.</summary>
    public string? InteriorOf { get; init; }
    /// <summary>
    /// For an interior clipped at a zone border: no seed keeps nothing (instead of the largest region); the part is kept from the neighbour's border
    /// (<see cref="ZoneKeptClosure"/>).
    /// </summary>
    public bool ClippedAtBorder { get; init; }
    /// <summary>Debug (the navmesh tool): when set, every part's triangle range is recorded in <see cref="PartRanges"/>.</summary>
    public bool TrackParts { get; init; }
    /// <summary>Debug: per part with collision, its owner and part names, collision path and the triangles [First, Last) it emitted.</summary>
    public List<(string Owner, string Part, string Path, int First, int Last)> PartRanges { get; } = [];
    /// <summary>
    /// For the interior of a destroyed building: its ground floor is kept besides what its seed reaches (<see cref="SeedPruner.DestroyedFloorBand"/>), and it is
    /// joined to the exterior along its hull (docs/game/pathfinding.md, "Destroyed interiors").
    /// </summary>
    public bool InteriorDestroyed { get; init; }
    /// <summary>
    /// For the interior of a destroyed building with a <c>destroyed boundary</c>: that wall ring's cross-section, the outline of the ruin's floor (its ruined walls
    /// have no collision); only polygons inside it are kept (docs/game/pathfinding.md, "Destroyed interiors").
    /// </summary>
    public List<(Vector2 A, Vector2 B)>? DestroyedOutline { get; init; }
    /// <summary>
    /// For the interior of an intact building without doors (an open doorway, e.g. the Hive Commune Hut), or of a ruin: joined to the exterior wherever their edges meet
    /// on the hull's line (docs/game/pathfinding.md, "Door joins").
    /// </summary>
    public bool InteriorDoorless { get; init; }
    /// <summary>For a zone's exterior mesh: its heightmap samples (safe from the tile threads); walkable spans deep under the terrain are removed.</summary>
    public HeightWindow? Ground { get; init; }

    /// <summary>The range of triangles (in triangle numbers) that came from buildings; for the seed rays.</summary>
    public int BuildingFirstTriangle, BuildingLastTriangle;
    /// <summary>The triangle ranges of the exterior's floor-0 and floor-1 building parts (groups 9 and 10), what the seed ray rule tests; in an interior, its floor-0 parts (group 9), what the origin ray seed tests.</summary>
    public List<(int First, int Last)> FloorRanges { get; } = [];
    /// <summary>Hash of the zone's buildings (placement, rotation, record), the cache key of the built mesh (<see cref="ZoneGeometryGatherer.BuildingHash"/>); 0 for an interior mesh.</summary>
    public uint BuildingHash { get; set; }
    public GatherStats Stats { get; } = new();

    public int TriangleCount => Indices.Count / 3;

    public int AddVertex(Vector3 p)
    {
        int i = Vertices.Count / 3;
        Vertices.Add(p.X); Vertices.Add(p.Y); Vertices.Add(p.Z);
        if (p.Y < MinY) MinY = p.Y;
        if (p.Y > MaxY) MaxY = p.Y;
        return i;
    }

    public void AddTriangle(int a, int b, int c, byte area)
    {
        Indices.Add(a); Indices.Add(b); Indices.Add(c);
        Areas.Add(area);
    }
}
