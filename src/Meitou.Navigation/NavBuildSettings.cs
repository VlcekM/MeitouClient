namespace Meitou.Navigation;

/// <summary>Parameters of the zone navmesh builder (Kenshi units; docs/game/pathfinding.md, "What our builder needs").</summary>
public sealed record NavBuildSettings
{
    /// <summary>Horizontal cell size. Havok has no grid; 2 keeps the 9-unit minimum passage at 4 to 5 cells.</summary>
    public float CellSize { get; init; } = 2f;
    public float CellHeight { get; init; } = 1f;
    /// <summary>Tile edge in cells; the zone's cells per side must be a multiple of it.</summary>
    public int TileCells { get; init; } = 48;
    /// <summary>characterHeight 1.8 Havok units.</summary>
    public float AgentHeight { get; init; } = 18f;
    /// <summary>edgeMatchingParams.maxStepHeight 0.5 Havok units.</summary>
    public float MaxClimb { get; init; } = 5f;
    /// <summary>Contour simplification error in cells.</summary>
    public float MaxSimplificationError { get; init; } = 1.3f;
    /// <summary>Longest contour edge in cells (0: unlimited).</summary>
    public int MaxEdgeLength { get; init; } = 24;
    public int MinRegionArea { get; init; } = 4;
    public int MergeRegionArea { get; init; } = 40;
    public bool Watershed { get; init; }
    /// <summary>Worker threads for tiles; 0 means all but one core.</summary>
    public int Threads { get; init; }
    /// <summary>Distance (units) within which a seed claims a region (regionPruningSettings.minDistanceToSeedPoints 0.4 Havok).</summary>
    public float SeedDistance { get; init; } = 4f;
    /// <summary>Vertical slack for the seed test (a seed's Y is where it was authored).</summary>
    public float SeedHeightSlack { get; init; } = 18f;
    /// <summary>How far (in cells) a door hull is grown before it paints cells, so a thin leaf closes its doorway.</summary>
    public float DoorInflateCells { get; init; } = 1f;
}

/// <summary>Timings of one build, milliseconds.</summary>
internal sealed class NavBuildTimes
{
    public double Tiles, Stitch, Prune, Total;
    /// <summary>CPU milliseconds summed over the tiles: rasterise and filter, compact and areas, regions, contours, polygons.</summary>
    public double CpuRaster, CpuCompact, CpuRegions, CpuContours, CpuMesh;
    public int TileCount, Polygons, KeptPolygons, Vertices, InteriorCount;
    /// <summary>Milliseconds spent on the building interiors (gather, build, join); included in <see cref="Total"/>.</summary>
    public double Interiors;
}
