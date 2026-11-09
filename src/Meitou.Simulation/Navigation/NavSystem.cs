using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// The navigation of a running game in one object, for the simulation host: the <see cref="Walkability"/> to hand to the simulation (replacing the
/// open-ground stand-in), the <see cref="Doors"/> table, and the zone load/unload calls that feed the background builder.
/// <para>Thread-safety: <see cref="Walkability"/> (<c>IsWalkable</c>, <c>GroundHeight</c>, <c>FindPath</c>) and <see cref="Doors"/> may be used from any thread at
/// any time; a zone's mesh appears in a path query when its build finishes (never half), until then the stand-in answers for that zone.
/// <see cref="LoadZone"/>, <see cref="LoadRing"/> and <see cref="UnloadZone"/> may be called from any thread and return at once.</para>
/// </summary>
public sealed class NavSystem : IDisposable
{
    #region Zones

    readonly NavMeshService _service;

    // Footprints for the long-range grid's obstacles, the level data's and the player's (one record cache for both).
    readonly Lazy<BuildingFootprints> _footprints;
    readonly WorldLevelData _levels;
    readonly GameDatabase _db;
    readonly Func<double, double, float> _placementHeight;

    public NavSystem(
        GameInstall install,
        GameDatabase db,
        WorldLevelData levels,
        Func<float, float, float> terrainHeight,
        NavBuildSettings? settings = null,
        NavMeshCache? cache = null)
    {
        this._levels = levels;
        this._db = db;
        _placementHeight = (x, z) => terrainHeight((float)x, (float)z);
        _footprints = new(() => new BuildingFootprints(install, db, levels));

        // The long-range grid reads every second heightmap sample (36 units) when it is first needed: the simulation's own grid is coarse far from the start.
        Walkability = new NavmeshWalkability(
            terrainHeight,
            routeTerrain: () => ReadRouteTerrain(install, terrainHeight),
            routeObstacles: () =>
                [.. _footprints.Value.Obstacles(levels.Buildings(), levels, _placementHeight),
                .. _footprints.Value.FeatureObstacles(_placementHeight)]);
        _service = new NavMeshService(install, db, levels, Walkability, settings, cache, Runtime);
        Walkability.KeepStanding = _service.KeepAt;
        Walkability.DoorGeometry = new NavDoorGeometry(install, db);
    }

    public NavmeshWalkability Walkability { get; }

    /// <summary>
    /// A loading screen covers the game: zone builds take every core (<see cref="NavMeshService.Covered"/>).
    /// </summary>
    public bool Covered
    {
        get => _service.Covered;
        set => _service.Covered = value;
    }

    /// <summary>
    /// Open or close a door by its building's instance id: <c>Doors.Close(id)</c>. No rebuild; the next query sees it.
    /// </summary>
    public NavDoors Doors => Walkability.Doors;

    /// <summary>
    /// Raised on the builder thread when a zone's mesh went live.
    /// </summary>
    public event Action<NavZoneReady>? ZoneReady
    {
        add => _service.ZoneReady += value;
        remove => _service.ZoneReady -= value;
    }

    /// <summary>
    /// Asks for a zone's mesh (from the cache, else built); the task completes when it is live. Asking twice is harmless.
    /// </summary>
    public Task<NavZoneReady> LoadZone(ZoneCoordinate zone) => _service.Request(zone);

    /// <summary>
    /// Asks for a zone and its neighbours within <paramref name="radius"/> (1 = the 3 × 3 ring around the player), nearest first.
    /// </summary>
    public void LoadRing(ZoneCoordinate centre, int radius = 1)
    {
        var zones = new List<ZoneCoordinate>();
        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                var z = new ZoneCoordinate(centre.X + dx, centre.Y + dz);
                if (z.IsInsideGrid) zones.Add(z);
            }
        }

        foreach (var z in zones.OrderBy(z => Math.Abs(z.X - centre.X) + Math.Abs(z.Y - centre.Y)))
            _ = _service.Request(z);
    }

    /// <summary>
    /// Drops a zone's mesh (its cache file stays). Paths already handed out stay valid as lists of points.
    /// </summary>
    public void UnloadZone(ZoneCoordinate zone) => _service.Unload(zone);

    /// <summary>
    /// Drops several zones' meshes with one republish of the world (callable off the main thread).
    /// </summary>
    public void UnloadZones(IReadOnlyCollection<ZoneCoordinate> zones) => _service.Unload(zones);

    /// <summary>
    /// Builds a zone again if its mesh is loaded (a zone that is not is built with the current buildings when it is asked for).
    /// </summary>
    public void RebuildZone(ZoneCoordinate zone)
    {
        if (Walkability.World.Find(zone) is not null) _ = _service.Rebuild(zone);
    }

    /// <summary>
    /// The long-range grid's terrain: the heightmap downsampled by 2, read once, falling back to <paramref name="terrainHeight"/> off its window.
    /// </summary>
    static Func<float, float, float> ReadRouteTerrain(GameInstall install, Func<float, float, float> terrainHeight)
    {
        using var map = TerrainHeightmap.Open(install);
        var raw = map.Downsample(2, out int size);
        var grid = new HeightWindow(0, 0, 2, size, size, raw);
        return (x, z) => grid.TryHeightAt(x, z, out float h) ? h : terrainHeight(x, z);
    }

    #endregion

    #region Runtime buildings

    /// <summary>
    /// The buildings the player placed, which every zone build sees (<see cref="AddRuntimeBuilding"/>).
    /// </summary>
    public NavRuntimeBuildings Runtime { get; } = new();

    readonly object _playerRouteGate = new();

    /// <summary>
    /// A building the player placed (or finished) takes part in the navmesh from the next build of the zones it reaches; the zones loaded now are
    /// built again, and <see cref="ZoneReady"/> fires for each when it goes live.
    /// </summary>
    public void AddRuntimeBuilding(BuildingPlacement placement)
    {
        Runtime.Add(placement);
        foreach (var zone in NavRuntimeBuildings.ZonesReached(placement.Position))
            RebuildZone(zone);

        UpdatePlayerRoute();
    }

    /// <summary>
    /// The level data's buildings changed near <paramref name="position"/> (the level editor placed or cleared one): the zones it reaches are built
    /// again from the level data as it is now.
    /// </summary>
    public void LevelChanged(System.Numerics.Vector3 position)
    {
        foreach (var zone in NavRuntimeBuildings.ZonesReached(position))
            RebuildZone(zone);
    }

    /// <summary>
    /// The building is gone (dismantled, cancelled, a world replaced): the zones it shaped are built again without it.
    /// </summary>
    public void RemoveRuntimeBuilding(string instanceId)
    {
        if (Runtime.Remove(instanceId) is not { } removed) return;

        foreach (var zone in NavRuntimeBuildings.ZonesReached(removed.Position))
            RebuildZone(zone);

        UpdatePlayerRoute();
    }

    /// <summary>
    /// Gives the long-range grid the player's buildings now, on the pool (their footprints may need reading). Runs one at a time, each from the overlay as it is
    /// then, so the last one leaves the newest set.
    /// </summary>
    void UpdatePlayerRoute()
    {
        Task.Run(() =>
        {
            lock (_playerRouteGate)
                Walkability.Coarse.SetPlayerObstacles(_footprints.Value.Obstacles(Runtime.All(), _levels, _placementHeight));
        });
    }

    #endregion

    #region Gates

    readonly object _labelGate = new();
    readonly Dictionary<string, GameRecord?> _functions = [];

    // The level data's buildings by placement key (what the door polygons are named by), their BUILDING record ids.
    // Made on first use and again when the level data was edited (WorldLevelData.EditRevision).
    (int Revision, Dictionary<string, string> Map)? _levelRecords;

    /// <summary>
    /// The gate codes of the meshes loaded now (<see cref="NavGateRegions"/>). Slow (a flood over the walled regions): call it off the simulation
    /// thread. One labelling at a time; each reads the world and the player's buildings as they are then.
    /// </summary>
    public NavGateRegions LabelGates()
    {
        lock (_labelGate)
        {
            var runtime = Runtime.All().ToDictionary(p => p.Key, p => p.BuildingId, StringComparer.Ordinal);
            return NavGateRegions.Label(Walkability.World, id => IsGateId(runtime, id));
        }
    }

    /// <summary>
    /// Whether a door owner (a runtime or level building instance id) is a gate: its BUILDING record is one.
    /// </summary>
    bool IsGateId(Dictionary<string, string> runtime, string id)
    {
        if (!runtime.TryGetValue(id, out var record) && !LevelRecords().TryGetValue(id, out record)) return false;

        return _db.Find(record) is { } building && TownBuildings.IsGate(_db, building, _functions);
    }

    Dictionary<string, string> LevelRecords()
    {
        int revision = _levels.EditRevision;
        if (_levelRecords is { } known && known.Revision == revision) return known.Map;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var b in _levels.Buildings())
            map.TryAdd(b.Key, b.BuildingId);

        _levelRecords = (revision, map);
        return map;
    }

    #endregion

    public void Dispose() => _service.Dispose();
}
