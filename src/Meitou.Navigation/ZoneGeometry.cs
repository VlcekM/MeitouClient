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
    public const byte Water = 2;
    public const byte Door = 3;

    public static bool IsWalkable(byte area) => area != Null;
}

/// <summary>A convex prism: a convex polygon on the XZ plane between two heights. Used for carvers (removes the mesh) and door painters.</summary>
public sealed class NavVolume(Vector2[] polygon, float yMin, float yMax)
{
    /// <summary>Convex polygon on (X, Z), counter-clockwise seen from above (+Y).</summary>
    public Vector2[] Polygon { get; } = polygon;
    public float YMin { get; } = yMin;
    public float YMax { get; } = yMax;
}

/// <summary>Counters of what a gather found, for logs and the debug tool.</summary>
public sealed class GatherStats
{
    public int Buildings, PartsWithCollision, Shapes, FoliageInstances, FoliageShapes, FoliageCutters, MissingFiles;
    public int WalkableTriangles, CuttingTriangles, TerrainTriangles, WaterTriangles;
    public int Carvers, Painters, DoorSeeds;
}

/// <summary>
/// Everything the navmesh builder needs for one zone (docs/game/pathfinding.md, "What our builder needs"): triangles in world space (Y up,
/// counter-clockwise seen from outside, so Recast's normal test works) with a per-triangle area, carvers, door painters and seed points.
/// </summary>
public sealed class ZoneGeometry
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
    /// <summary>Door hulls: walkable cells inside become <see cref="NavArea.Door"/>.</summary>
    public List<NavVolume> Painters { get; } = [];
    /// <summary>Seed points: regions of the mesh near one stay, the rest is pruned (docs/game/pathfinding.md, "Region pruning by seeds").</summary>
    public List<Vector3> Seeds { get; } = [];

    /// <summary>Hash of the zone's buildings (placement, rotation, record), the cache key of the built mesh.</summary>
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
