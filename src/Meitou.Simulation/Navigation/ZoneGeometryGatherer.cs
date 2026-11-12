using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// BUILDING <c>path mode</c> (FCS PathMode).
/// </summary>
internal enum PathMode
{
    /// <summary>
    /// IGNORE: paths ignore the building; its shapes are left out of the navmesh and its parts are collision groups 23..26.
    /// </summary>
    Ignore = 0,
    /// <summary>
    /// The value 1; no rule of the gatherer tests for it, so it is an obstacle like any mode but IGNORE and WALKABLE.
    /// </summary>
    Projected = 1,
    /// <summary>
    /// OBSTACLE, the default of a record without one: the building's shapes cut the mesh.
    /// </summary>
    Obstacle = 2,
    /// <summary>
    /// WALKABLE: the building's shapes are walkable up to <see cref="ZoneGeometryGatherer.BuildingSlopeDegrees"/>
    /// (docs/game/pathfinding.md, "Inputs").
    /// </summary>
    Walkable = 3
}

/// <summary>
/// Gathers the navmesh input of one zone from the game data as the original's generator does (docs/game/pathfinding.md, "Inputs"; collision files and
/// groups in docs/formats/collision.md): terrain triangles with the water clamp, building and foliage collision placed and filtered by collision
/// group, per-triangle walkable areas, carvers, door painters and seed points. One instance per thread (it owns a heightmap handle); the collision
/// cache may be shared.
/// </summary>
internal sealed partial class ZoneGeometryGatherer : IDisposable
{
    #region Inputs

    /// <summary>
    /// Collision groups the original's generator includes (mask 0x809de40): foliage 6 and 14, building parts 9..12, stairs 15 and 16, furniture 19,
    /// unwalkable roofs 27.
    /// </summary>
    public const uint IncludedGroupMask = 0x809de40;

    /// <summary>
    /// Terrain slope limit (degrees) and the limit for walkable building triangles.
    /// </summary>
    public const float TerrainSlopeDegrees = 40, BuildingSlopeDegrees = 60;

    /// <summary>
    /// Vertical half-extent of a foliage cutter box (docs: ±100 units).
    /// </summary>
    public const float CutterHalfHeight = 100;

    /// <summary>
    /// How far (units) past the zone box the terrain, shapes and foliage reach (engine choice; the original's tiles overlap their neighbours).
    /// </summary>
    public const float DefaultMargin = 72;

    /// <summary>
    /// The game install the heightmap, seeds, foliage and collision files are read from.
    /// </summary>
    readonly GameInstall _install;
    /// <summary>
    /// The game data the BUILDING and part records are looked up in.
    /// </summary>
    readonly GameDatabase _db;
    /// <summary>
    /// The level data whose placed buildings, states and interiors are gathered.
    /// </summary>
    readonly WorldLevelData _levels;
    /// <summary>
    /// The collision files read so far (may be shared with other gatherers).
    /// </summary>
    readonly CollisionCache _collision;
    /// <summary>
    /// This gatherer's handle on the terrain heightmap (the reason one instance serves one thread).
    /// </summary>
    readonly TerrainHeightmap _map;
    /// <summary>
    /// The seeds.def points by zone; null when the install has no seeds file.
    /// </summary>
    readonly List<Vector3>[]? _seedsByZone;
    /// <summary>
    /// The building layouts (signs, stalls, furniture) of the level data's interiors, with its layout swaps.
    /// </summary>
    readonly BuildingLayouts _layouts;
    /// <summary>
    /// The buildings placed at run time, gathered with the level data's; null: none.
    /// </summary>
    readonly NavRuntimeBuildings? _runtime;

    /// <summary>
    /// The placed foliage of the zones gathered so far.
    /// </summary>
    readonly FoliageObstacles _foliage;

    /// <summary>
    /// The level data's buildings by zone, made again when the level data was edited (WorldLevelData.EditRevision); one gatherer is used by one
    /// thread at a time.
    /// </summary>
    Dictionary<ZoneCoordinate, List<BuildingPlacement>> _byZone = [];
    /// <summary>
    /// The <see cref="WorldLevelData.EditRevision"/> <see cref="_byZone"/> was made at; -1 before the first index.
    /// </summary>
    int _indexedRevision = -1;

    /// <summary>
    /// A gatherer over the install's terrain, seeds and foliage and <paramref name="levels"/>' buildings (and <paramref name="runtime"/>'s, null:
    /// none), sharing <paramref name="collision"/>.
    /// </summary>
    public ZoneGeometryGatherer(GameInstall install, GameDatabase db, WorldLevelData levels, CollisionCache collision,
        NavRuntimeBuildings? runtime = null)
    {
        this._install = install;
        this._db = db;
        this._levels = levels;
        this._collision = collision;
        this._runtime = runtime;
        _map = TerrainHeightmap.Open(install);
        _seedsByZone = NavSeeds.Load(install);
        _layouts = new BuildingLayouts(db, levels.Interiors);
        _layouts.UseSwaps(levels);
        _foliage = new FoliageObstacles(install, db, levels);
    }

    /// <summary>
    /// Whether foliage collision is gathered (it needs the biome data and costs a foliage placement per zone).
    /// </summary>
    public bool IncludeFoliage { get; set; } = true;

    /// <summary>
    /// Every convex obstacle is a <see cref="NavSolid"/> too, so the ground inside a closed building volume is removed (the original's cutting
    /// materials).
    /// </summary>
    public bool CarveConvexObstacles { get; set; } = true;

    /// <summary>
    /// Closes the heightmap and the foliage data.
    /// </summary>
    public void Dispose()
    {
        _map.Dispose();
        _foliage.Dispose();
    }

    /// <summary>
    /// The terrain heightmap's height at world (<paramref name="x"/>, <paramref name="z"/>).
    /// </summary>
    public float TerrainHeight(double x, double z) => _map.HeightAt(x, z);

    /// <summary>
    /// The seed points of a zone from seeds.def (docs: absolute positions, Y −99 when none was authored).
    /// </summary>
    public IReadOnlyList<Vector3> FileSeeds(ZoneCoordinate zone) =>
        _seedsByZone is not null && zone.IsInsideGrid ? _seedsByZone[zone.Y * WorldLayout.ZoneCount + zone.X] : [];

    #endregion

    #region Gathering

    /// <summary>
    /// Milliseconds the last <see cref="Gather"/> spent per phase (debug).
    /// </summary>
    public (double Terrain, double Buildings, double Foliage) Phases { get; private set; }

    /// <summary>
    /// Gathers the zone. <paramref name="buildingHash"/> is <see cref="BuildingHash"/> of the zone when the caller has it already (it is the cache
    /// key), so that it is not computed twice.
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
            Ground = ZoneHeights(zone),
        };
        var sw = Stopwatch.StartNew();

        // Foliage placement is the long pole: it runs on the pool while the terrain and buildings are gathered here.
        var foliageTask = IncludeFoliage ? Task.Run(() => _foliage.Of(FoliageRing(zone))) : null;
        AddTerrain(g);
        double terrain = sw.Elapsed.TotalMilliseconds;
        sw.Restart();

        AddBuildings(g, buildingHash);
        AddMapFeatures(g);
        double buildings = sw.Elapsed.TotalMilliseconds;
        sw.Restart();

        if (foliageTask is not null) AddFoliage(g, foliageTask.GetAwaiter().GetResult());

        Phases = (terrain, buildings, sw.Elapsed.TotalMilliseconds);
        sw.Restart();

        AddSeeds(g);
        g.Stats.MissingFiles = _collision.Missing;
        return g;
    }

    #endregion

    #region Level buildings

    /// <summary>
    /// The level data's buildings grouped by zone, indexed again only when the level data was edited.
    /// </summary>
    Dictionary<ZoneCoordinate, List<BuildingPlacement>> BuildingsByZone
    {
        get
        {
            int revision = _levels.EditRevision;
            if (_indexedRevision == revision) return _byZone;

            var map = new Dictionary<ZoneCoordinate, List<BuildingPlacement>>();
            foreach (var b in _levels.Buildings())
            {
                if (!map.TryGetValue(b.Zone, out var list)) map[b.Zone] = list = [];

                list.Add(b);
            }

            (_byZone, _indexedRevision) = (map, revision);
            return map;
        }
    }

    #endregion
}
