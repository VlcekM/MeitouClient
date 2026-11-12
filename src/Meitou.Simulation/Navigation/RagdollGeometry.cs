using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Simulation.Ragdolls;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// What ragdolls fall onto in the world: the terrain heightmap, and the collision of the buildings and map features
/// (<see cref="ZoneGeometryGatherer.RagdollTriangles"/>), gathered per zone on a worker the first time a ragdoll moves near it and kept in 32-unit
/// cells (<see cref="RagdollPhysics"/>' tiles). Until a zone is gathered its buildings are reported not ready, and the tile asks again.
/// </summary>
public sealed class RagdollGeometry : IRagdollEnvironment, IDisposable
{
    /// <summary>
    /// Zones kept at most; the least recently asked for goes first (engine choice).
    /// </summary>
    const int MaxZones = 24;

    /// <summary>
    /// How far a building's collision may reach from its placement (the navmesh gatherer's reach).
    /// </summary>
    const float Reach = 600;

    /// <summary>
    /// Gathers the zones' building and feature collision (foliage left out); used under <see cref="_gatherLock"/>.
    /// </summary>
    readonly ZoneGeometryGatherer _gatherer;
    /// <summary>
    /// Each zone's gathered triangles by 32-unit cell, buildings and roofs apart, as the task of a worker's gather; dropped by a run-time building
    /// change and by <see cref="Evict"/>.
    /// </summary>
    readonly ConcurrentDictionary<(ZoneCoordinate Zone, bool Roofs), Task<Dictionary<(int, int), List<Vector3>>>> _zones = new();
    /// <summary>
    /// One gather at a time: the gatherer is not thread-safe.
    /// </summary>
    readonly object _gatherLock = new();
    /// <summary>
    /// When each gathered zone was last asked for (a tick of <see cref="_clock"/>), for <see cref="Evict"/>.
    /// </summary>
    readonly ConcurrentDictionary<(ZoneCoordinate Zone, bool Roofs), long> _used = new();
    /// <summary>
    /// Counts the asks; the newest has the highest number.
    /// </summary>
    long _clock;
    /// <summary>
    /// The buildings placed at run time, whose changes drop the zones they touch; null: none.
    /// </summary>
    readonly NavRuntimeBuildings? _runtime;
    /// <summary>
    /// The counter behind <see cref="Generation"/>, bumped by every run-time building change.
    /// </summary>
    int _generation;

    /// <summary>
    /// The terrain bodies land on.
    /// </summary>
    readonly RagdollGround _ground;

    /// <param name="terrainHeight">
    /// The simulation's ground height, used only where the full heightmap cannot be opened: it is the coarse world grid away from the loaded area.
    /// </param>
    /// <param name="runtime">
    /// The buildings placed at run time (built, bought or ruined houses), which the zones' collision includes as the navmesh does; a change drops the
    /// zone's gathered collision and bumps <see cref="Generation"/>.
    /// </param>
    public RagdollGeometry(GameInstall install, GameDatabase db, WorldLevelData levels, Func<float, float, float> terrainHeight,
        NavRuntimeBuildings? runtime = null)
    {
        _gatherer = new ZoneGeometryGatherer(install, db, levels, new CollisionCache(install), runtime) { IncludeFoliage = false };
        this._runtime = runtime;
        if (runtime is not null) runtime.Changed += Changed;

        _ground = new RagdollGround(install, terrainHeight);
    }

    /// <inheritdoc/>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// Drops a changed zone's gathered triangles (both kinds) and bumps <see cref="Generation"/>.
    /// </summary>
    void Changed(ZoneCoordinate zone)
    {
        _zones.TryRemove((zone, false), out _);
        _zones.TryRemove((zone, true), out _);
        Interlocked.Increment(ref _generation);
    }

    /// <summary>
    /// The terrain at full resolution (<see cref="RagdollGround.HeightAt"/>). Called from the simulation thread only.
    /// </summary>
    public float GroundHeight(float x, float z, float nearY) => _ground.HeightAt(x, z);

    /// <inheritdoc/>
    public bool Buildings(Vector3 min, Vector3 max, List<Vector3> triangles) => Collect(min, max, triangles, roofs: false);

    /// <summary>
    /// The groups 23..26 triangles (<see cref="ZoneGeometryGatherer.RoofTriangles"/>), gathered and kept per zone like <see cref="Buildings"/>.
    /// </summary>
    public bool Roofs(Vector3 min, Vector3 max, List<Vector3> triangles) => Collect(min, max, triangles, roofs: true);

    /// <summary>
    /// Adds the gathered triangles of the cells between the corners, from every zone within <see cref="Reach"/> of them, starting a gather for a zone
    /// not asked for yet; false while one of those zones is still being gathered.
    /// </summary>
    bool Collect(Vector3 min, Vector3 max, List<Vector3> triangles, bool roofs)
    {
        bool ready = true;
        int cx0 = Cell(min.X);
        int cx1 = Cell(max.X - 1e-3f);
        int cz0 = Cell(min.Z);
        int cz1 = Cell(max.Z - 1e-3f);

        // A building's triangles are kept with the zone it is placed in and may reach into the next one: the zones within a building's reach of the
        // box.
        var first = WorldLayout.ZoneOf(min.X - Reach, min.Z - Reach);
        var last = WorldLayout.ZoneOf(max.X + Reach, max.Z + Reach);
        for (int zy = first.Y; zy <= last.Y; zy++)
        {
            for (int zx = first.X; zx <= last.X; zx++)
            {
                var zone = new ZoneCoordinate(zx, zy);
                if (!zone.IsInsideGrid) continue;

                _used[(zone, roofs)] = Interlocked.Increment(ref _clock);
                var task = _zones.GetOrAdd((zone, roofs), k => Task.Run(() => Gather(k.Zone, k.Roofs)));
                if (!task.IsCompletedSuccessfully)
                {
                    ready &= !task.IsFaulted && task.IsCompleted;
                    continue;
                }

                AddCells(task.Result, cx0, cx1, cz0, cz1, triangles);
            }
        }

        Evict();
        return ready;
    }

    /// <summary>
    /// Adds the triangles of the cells in the box (cell columns <paramref name="cx0"/> to <paramref name="cx1"/>, rows <paramref name="cz0"/> to
    /// <paramref name="cz1"/>) from one gathered zone.
    /// </summary>
    static void AddCells(Dictionary<(int, int), List<Vector3>> cells, int cx0, int cx1, int cz0, int cz1, List<Vector3> triangles)
    {
        for (int x = cx0; x <= cx1; x++)
        {
            for (int z = cz0; z <= cz1; z++)
            {
                if (cells.TryGetValue((x, z), out var cell)) triangles.AddRange(cell);
            }
        }
    }

    /// <summary>
    /// The 32-unit cell (<see cref="RagdollSystem.TileSize"/>) of a world X or Z.
    /// </summary>
    static int Cell(float v) => (int)MathF.Floor(v / RagdollSystem.TileSize);

    /// <summary>
    /// Gathers one zone's ragdoll or roof triangles and files each under every cell its plan bounds cover.
    /// </summary>
    Dictionary<(int, int), List<Vector3>> Gather(ZoneCoordinate zone, bool roofs)
    {
        var all = new List<Vector3>();
        lock (_gatherLock)
        {
            if (roofs)
                _gatherer.RoofTriangles(zone, all);
            else
                _gatherer.RagdollTriangles(zone, all);
        }

        var cells = new Dictionary<(int, int), List<Vector3>>();
        for (int t = 0; t + 2 < all.Count; t += 3)
        {
            Vector3 a = all[t];
            Vector3 b = all[t + 1];
            Vector3 c = all[t + 2];
            var lo = Vector3.Min(a, Vector3.Min(b, c));
            var hi = Vector3.Max(a, Vector3.Max(b, c));
            for (int x = Cell(lo.X); x <= Cell(hi.X); x++)
            {
                for (int z = Cell(lo.Z); z <= Cell(hi.Z); z++)
                {
                    if (!cells.TryGetValue((x, z), out var list)) cells[(x, z)] = list = [];

                    list.Add(a);
                    list.Add(b);
                    list.Add(c);
                }
            }
        }

        return cells;
    }

    /// <summary>
    /// Drops the least recently asked for gathered zones past <see cref="MaxZones"/> (only finished ones).
    /// </summary>
    void Evict()
    {
        if (_zones.Count <= MaxZones) return;

        foreach (var (zone, _) in _used.OrderBy(u => u.Value).Take(_zones.Count - MaxZones).ToList())
        {
            if (_zones.TryGetValue(zone, out var task) && task.IsCompleted && _zones.TryRemove(zone, out _))
                _used.TryRemove(zone, out _);
        }
    }

    /// <summary>
    /// Unhooks from the run-time buildings and waits for the gathers still running.
    /// </summary>
    public void Dispose()
    {
        if (_runtime is not null) _runtime.Changed -= Changed;

        foreach (var task in _zones.Values)
        {
            try
            {
                task.Wait();
            }
            catch (AggregateException)
            {
                // A failed gather has nothing to wait for.
            }
        }

        _ground.Dispose();
        lock (_gatherLock)
            _gatherer.Dispose();
    }
}
