using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>
/// The navigation of a running game in one object, for the simulation host: the <see cref="Walkability"/> to hand to the simulation (replacing the
/// open-ground stand-in), the <see cref="Doors"/> table, and the zone load/unload calls that feed the background builder.
/// <para>Thread-safety: <see cref="Walkability"/> (<c>IsWalkable</c>, <c>GroundHeight</c>, <c>FindPath</c>) and <see cref="Doors"/> may be used from any
/// thread at any time; a zone's mesh appears in a path query when its build finishes (never half), until then the stand-in answers for that zone.
/// <see cref="LoadZone"/>, <see cref="LoadRing"/> and <see cref="UnloadZone"/> may be called from any thread and return at once.</para>
/// </summary>
public sealed class NavSystem : IDisposable
{
    readonly NavMeshService service;

    /// <summary>The buildings the player placed, which every zone build sees (<see cref="AddRuntimeBuilding"/>).</summary>
    public NavRuntimeBuildings Runtime { get; } = new();

    // Footprints for the long-range grid's obstacles, the level data's and the player's (one record cache for both).
    readonly Lazy<BuildingFootprints> footprints;
    readonly WorldLevelData levels;
    readonly GameDatabase db;
    // The level data's buildings by placement key (what the door polygons are named by), their BUILDING record ids.
    readonly Lazy<Dictionary<string, string>> levelRecords;
    readonly Dictionary<string, GameRecord?> functions = [];
    readonly object labelGate = new();
    readonly Func<double, double, float> placementHeight;
    readonly object playerRouteGate = new();

    public NavSystem(GameInstall install, GameDatabase db, WorldLevelData levels, Func<float, float, float> terrainHeight, NavBuildSettings? settings = null, NavMeshCache? cache = null)
    {
        this.levels = levels;
        this.db = db;
        levelRecords = new(() =>
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var b in levels.Buildings()) map.TryAdd(b.Key, b.BuildingId);
            return map;
        });
        placementHeight = (x, z) => terrainHeight((float)x, (float)z);
        footprints = new(() => new BuildingFootprints(install, db, levels));
        // The long-range grid reads every second heightmap sample (36 units) when it is first needed: the simulation's own grid is coarse far from the start.
        Walkability = new NavmeshWalkability(terrainHeight, routeTerrain: () =>
        {
            using var map = TerrainHeightmap.Open(install);
            var raw = map.Downsample(2, out int size);
            var grid = new HeightWindow(0, 0, 2, size, size, raw);
            return (x, z) => grid.TryHeightAt(x, z, out float h) ? h : terrainHeight(x, z);
        }, routeObstacles: () => [.. footprints.Value.Obstacles(levels.Buildings(), levels, placementHeight), .. footprints.Value.FeatureObstacles(placementHeight)]);
        service = new NavMeshService(install, db, levels, Walkability, settings, cache, Runtime);
        Walkability.KeepStanding = service.KeepAt;
        Walkability.DoorGeometry = new NavDoorGeometry(install, db);
    }

    public NavmeshWalkability Walkability { get; }

    /// <summary>Open or close a door by its building's instance id: <c>Doors.Close(id)</c>. No rebuild; the next query sees it.</summary>
    public NavDoors Doors => Walkability.Doors;

    /// <summary>Raised on the builder thread when a zone's mesh went live.</summary>
    public event Action<NavZoneReady>? ZoneReady
    {
        add => service.ZoneReady += value;
        remove => service.ZoneReady -= value;
    }

    /// <summary>Asks for a zone's mesh (from the cache, else built); the task completes when it is live. Asking twice is harmless.</summary>
    public Task<NavZoneReady> LoadZone(ZoneCoordinate zone) => service.Request(zone);

    /// <summary>Asks for a zone and its neighbours within <paramref name="radius"/> (1 = the 3 × 3 ring around the player), nearest first.</summary>
    public void LoadRing(ZoneCoordinate centre, int radius = 1)
    {
        var zones = new List<ZoneCoordinate>();
        for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                var z = new ZoneCoordinate(centre.X + dx, centre.Y + dz);
                if (z.IsInsideGrid) zones.Add(z);
            }
        foreach (var z in zones.OrderBy(z => Math.Abs(z.X - centre.X) + Math.Abs(z.Y - centre.Y))) _ = service.Request(z);
    }

    /// <summary>Drops a zone's mesh (its cache file stays). Paths already handed out stay valid as lists of points.</summary>
    public void UnloadZone(ZoneCoordinate zone) => service.Unload(zone);

    /// <summary>
    /// A building the player placed (or finished) takes part in the navmesh from the next build of the zones it reaches; the zones loaded now are built again, and
    /// <see cref="ZoneReady"/> fires for each when it goes live.
    /// </summary>
    public void AddRuntimeBuilding(BuildingPlacement placement)
    {
        Runtime.Add(placement);
        foreach (var zone in NavRuntimeBuildings.ZonesReached(placement.Position)) RebuildZone(zone);
        UpdatePlayerRoute();
    }

    /// <summary>The building is gone (dismantled, cancelled, a world replaced): the zones it shaped are built again without it.</summary>
    public void RemoveRuntimeBuilding(string instanceId)
    {
        if (Runtime.Remove(instanceId) is not { } removed) return;
        foreach (var zone in NavRuntimeBuildings.ZonesReached(removed.Position)) RebuildZone(zone);
        UpdatePlayerRoute();
    }

    /// <summary>
    /// Gives the long-range grid the player's buildings now, on the pool (their footprints may need reading). Runs one at a time, each from the overlay as it is
    /// then, so the last one leaves the newest set.
    /// </summary>
    void UpdatePlayerRoute() => Task.Run(() =>
    {
        lock (playerRouteGate) Walkability.Coarse.SetPlayerObstacles(footprints.Value.Obstacles(Runtime.All(), levels, placementHeight));
    });

    /// <summary>
    /// The gate codes of the meshes loaded now (<see cref="NavGateRegions"/>). Slow (a flood over the walled regions): call it off the simulation thread. One labelling at a
    /// time; each reads the world and the player's buildings as they are then.
    /// </summary>
    public NavGateRegions LabelGates()
    {
        lock (labelGate)
        {
            var runtime = Runtime.All().ToDictionary(p => p.Key, p => p.BuildingId, StringComparer.Ordinal);
            return NavGateRegions.Label(Walkability.World, id =>
                (runtime.TryGetValue(id, out var record) || levelRecords.Value.TryGetValue(id, out record)) && db.Find(record) is { } building
                && TownBuildings.IsGate(db, building, functions));
        }
    }

    /// <summary>Builds a zone again if its mesh is loaded (a zone that is not is built with the current buildings when it is asked for).</summary>
    public void RebuildZone(ZoneCoordinate zone)
    {
        if (Walkability.World.Find(zone) is not null) _ = service.Rebuild(zone);
    }

    public void Dispose() => service.Dispose();
}
