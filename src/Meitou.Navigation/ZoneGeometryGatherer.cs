using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>BUILDING <c>path mode</c> (FCS PathMode).</summary>
public enum PathMode { Ignore = 0, Projected = 1, Obstacle = 2, Walkable = 3 }

/// <summary>
/// Gathers the navmesh input of one zone from the game data as the original's generator does (docs/game/pathfinding.md, "Inputs"; collision files and
/// groups in docs/formats/collision.md): terrain triangles with the water clamp, building and foliage collision placed and filtered by collision group,
/// per-triangle walkable areas, carvers, door painters and seed points. One instance per thread (it owns a heightmap handle); the collision cache may be shared.
/// </summary>
public sealed partial class ZoneGeometryGatherer : IDisposable
{
    /// <summary>Collision groups the original's generator includes (mask 0x809de40): foliage 6 and 14, building parts 9..12, stairs 15 and 16, furniture 19, unwalkable roofs 27.</summary>
    public const uint IncludedGroupMask = 0x809de40;

    /// <summary>Terrain slope limit (degrees) and the limit for walkable building triangles.</summary>
    public const float TerrainSlopeDegrees = 40, BuildingSlopeDegrees = 60;

    /// <summary>Vertical half-extent of a foliage cutter box (docs: ±100 units).</summary>
    public const float CutterHalfHeight = 100;

    /// <summary>How far (units) past the zone box the terrain, shapes and foliage reach (engine choice; the original's tiles overlap their neighbours).</summary>
    public const float DefaultMargin = 72;

    readonly GameInstall install;
    readonly GameDatabase db;
    readonly WorldLevelData levels;
    readonly CollisionCache collision;
    readonly TerrainHeightmap map;
    readonly Dictionary<ZoneCoordinate, List<BuildingPlacement>> buildingsByZone = [];
    readonly List<Vector3>[]? seedsByZone;

    public ZoneGeometryGatherer(GameInstall install, GameDatabase db, WorldLevelData levels, CollisionCache collision)
    {
        this.install = install;
        this.db = db;
        this.levels = levels;
        this.collision = collision;
        map = TerrainHeightmap.Open(install);
        foreach (var b in levels.Buildings())
        {
            if (!buildingsByZone.TryGetValue(b.Zone, out var list)) buildingsByZone[b.Zone] = list = [];
            list.Add(b);
        }
        seedsByZone = NavSeeds.Load(install);
    }

    /// <summary>Whether foliage collision is gathered (it needs the biome data and costs a foliage placement per zone).</summary>
    public bool IncludeFoliage { get; set; } = true;

    /// <summary>Add a prism carver around every convex obstacle, so the ground inside a closed building volume is removed too (the original's cutting materials).</summary>
    public bool CarveConvexObstacles { get; set; } = true;

    public void Dispose()
    {
        map.Dispose();
        foreach (var w in foliagePool) w.Dispose();
    }

    public float TerrainHeight(double x, double z) => map.HeightAt(x, z);

    /// <summary>The seed points of a zone from seeds.def (docs: absolute positions, Y −99 when none was authored).</summary>
    public IReadOnlyList<Vector3> FileSeeds(ZoneCoordinate zone) =>
        seedsByZone is not null && zone.IsInsideGrid ? seedsByZone[zone.Y * WorldLayout.ZoneCount + zone.X] : [];

    /// <summary>Milliseconds the last <see cref="Gather"/> spent per phase (debug).</summary>
    public (double Terrain, double Buildings, double Foliage) Phases { get; private set; }

    /// <summary>
    /// Gathers the zone. <paramref name="buildingHash"/> is <see cref="BuildingHash"/> of the zone when the caller has it already (it is the cache key),
    /// so that it is not computed twice.
    /// </summary>
    public ZoneGeometry Gather(ZoneCoordinate zone, float margin = DefaultMargin, uint? buildingHash = null)
    {
        var (ox, oz) = WorldLayout.ZoneOrigin(zone);
        var g = new ZoneGeometry
        {
            Zone = zone,
            ZoneMin = new((float)ox, (float)oz),
            ZoneMax = new((float)ox + WorldLayout.ZoneSize, (float)oz + WorldLayout.ZoneSize),
            Margin = margin,
        };
        var sw = Stopwatch.StartNew();
        // Foliage placement is the long pole: it runs on the pool while the terrain and buildings are gathered here.
        var foliageTask = IncludeFoliage ? Task.Run(() => FoliageOf(FoliageRing(zone))) : null;
        AddTerrain(g);
        double terrain = sw.Elapsed.TotalMilliseconds; sw.Restart();
        AddBuildings(g, buildingHash);
        double buildings = sw.Elapsed.TotalMilliseconds; sw.Restart();
        if (foliageTask is not null) AddFoliage(g, foliageTask.GetAwaiter().GetResult());
        Phases = (terrain, buildings, sw.Elapsed.TotalMilliseconds); sw.Restart();
        AddSeeds(g);
        g.Stats.MissingFiles = collision.Missing;
        return g;
    }
}
