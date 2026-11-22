using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Simulation.Ragdolls;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// What ragdolls fall onto in the world: the terrain heightmap, and the collision of the buildings and map features
/// (<see cref="ZoneGeometryGatherer.RagdollTriangles"/>), gathered per zone on a worker the first time the zone is asked to load (<see cref="ZoneRead"/>)
/// or a ragdoll moves near it and kept in 32-unit cells (<see cref="RagdollPhysics"/>' tiles). Until a zone is gathered its buildings are reported
/// not ready, and the tile asks again. A <see cref="Synchronous"/> geometry gathers on the asking thread instead, so what the world reads never
/// depends on a worker's timing (docs/simulation.md "Threading": determinism runs use synchronous services).
/// </summary>
public sealed class RagdollGeometry : IRagdollEnvironment, IDisposable
{
    /// <summary>
    /// Zones kept at most besides the loaded ones (<see cref="HoldZones"/>); the least recently asked for goes first (engine choice).
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
    /// Each zone's gathered triangles by 32-unit cell, buildings and roofs apart, as the task of a worker's gather; replaced once a run-time building
    /// change is gathered again (<see cref="Regather"/>), dropped by <see cref="Evict"/>.
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
    /// The buildings placed at run time, whose changes gather the zones they touch again; null: none.
    /// </summary>
    readonly NavRuntimeBuildings? _runtime;
    /// <summary>
    /// The counter behind <see cref="Generation"/>, bumped each time a run-time building change's triangles are in.
    /// </summary>
    int _generation;
    /// <summary>
    /// The loaded zones (<see cref="HoldZones"/>), which <see cref="Evict"/> leaves; replaced whole, never changed in place.
    /// </summary>
    volatile HashSet<ZoneCoordinate> _held = [];
    /// <summary>
    /// 1 when <see cref="Evict"/> has work to look at: a zone was added, the loaded zones changed, or the last eviction left a zone still being
    /// gathered. Without it every collision ask counted the dictionary under all its locks.
    /// </summary>
    int _evictDue = 1;
    /// <summary>
    /// Set by <see cref="Dispose"/> under <see cref="_gatherLock"/>: a gather again still queued then does nothing.
    /// </summary>
    bool _disposed;

    /// <summary>
    /// The terrain bodies land on.
    /// </summary>
    readonly RagdollGround _ground;

    /// <summary>
    /// Whether a zone is gathered on the thread that first asks for it, and a run-time building change at once, rather than on a worker: no zone is
    /// ever reported not ready, so the sight rays, shots and zone loading read the same collision on every run (the headless probes).
    /// </summary>
    public bool Synchronous { get; }

    /// <param name="terrainHeight">
    /// The simulation's ground height, used only where the full heightmap cannot be opened: it is the coarse world grid away from the loaded area.
    /// </param>
    /// <param name="runtime">
    /// The buildings placed at run time (built, bought or ruined houses), which the zones' collision includes as the navmesh does; a change drops the
    /// zone's gathered collision and bumps <see cref="Generation"/>.
    /// </param>
    /// <param name="synchronous">
    /// <see cref="Synchronous"/>.
    /// </param>
    public RagdollGeometry(GameInstall install, GameDatabase db, WorldLevelData levels, Func<float, float, float> terrainHeight,
        NavRuntimeBuildings? runtime = null, bool synchronous = false)
    {
        Synchronous = synchronous;
        _gatherer = new ZoneGeometryGatherer(install, db, levels, new CollisionCache(install), runtime) { IncludeFoliage = false };
        this._runtime = runtime;
        if (runtime is not null) runtime.Changed += Changed;

        _ground = new RagdollGround(install, terrainHeight);
    }

    /// <inheritdoc/>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// Gathers a changed zone again (both kinds), its old triangles answering until the new ones are in: a loaded zone never loses its collision,
    /// as the original's scene keeps a loaded zone's shapes while a new building's are made.
    /// </summary>
    void Changed(ZoneCoordinate zone)
    {
        Regather((zone, false));
        Regather((zone, true));
    }

    /// <summary>
    /// Gathers one kept zone's triangles again on a worker and puts them in place of the old ones, then bumps <see cref="Generation"/>; a zone not
    /// asked for yet is left to its first gather. The swap is made under the gather lock, so after two quick changes the later gather, which saw
    /// both, is the one kept.
    /// </summary>
    void Regather((ZoneCoordinate Zone, bool Roofs) key)
    {
        if (!_zones.ContainsKey(key)) return;

        if (Synchronous) GatherAgain(key);
        else
            _ = Task.Run(() => GatherAgain(key));
    }

    /// <summary>
    /// <see cref="Regather"/>'s gather: the new triangles in place of the old ones under the gather lock, then <see cref="Generation"/> bumped.
    /// </summary>
    void GatherAgain((ZoneCoordinate Zone, bool Roofs) key)
    {
        lock (_gatherLock)
        {
            if (_disposed || !_zones.ContainsKey(key)) return;

            _zones[key] = Task.FromResult(Gather(key.Zone, key.Roofs));
        }

        Interlocked.Increment(ref _generation);
    }

    /// <summary>
    /// The terrain at full resolution (<see cref="RagdollGround.HeightAt"/>). Called from the simulation thread only.
    /// </summary>
    public float GroundHeight(float x, float z, float nearY) => _ground.HeightAt(x, z);

    /// <inheritdoc/>
    public bool Buildings(Vector3 min, Vector3 max, List<Vector3> triangles)
    {
        var add = new Appender(triangles);
        return Collect(min, max, roofs: false, ref add);
    }

    /// <summary>
    /// The groups 23..26 triangles (<see cref="ZoneGeometryGatherer.RoofTriangles"/>), gathered and kept per zone like <see cref="Buildings"/>.
    /// </summary>
    public bool Roofs(Vector3 min, Vector3 max, List<Vector3> triangles)
    {
        var add = new Appender(triangles);
        return Collect(min, max, roofs: true, ref add);
    }

    /// <summary>
    /// Hands the visitor the gathered cells' own triangle lists, uncopied (<see cref="IRagdollEnvironment.VisitBuildings"/>): a gathered cell is
    /// never changed, a gather again replaces the zone's cells whole.
    /// </summary>
    public bool VisitBuildings<TVisitor>(Vector3 min, Vector3 max, bool roofs, ref TVisitor visitor) where TVisitor : struct, ITriangleVisitor =>
        Collect(min, max, roofs, ref visitor);

    /// <summary>
    /// Adds each run of triangles to a list (<see cref="Buildings"/>, <see cref="Roofs"/>).
    /// </summary>
    readonly struct Appender(List<Vector3> triangles) : ITriangleVisitor
    {
        /// <summary>
        /// Adds the run to the list.
        /// </summary>
        public void Visit(ReadOnlySpan<Vector3> run) => triangles.AddRange(run);
    }

    /// <summary>
    /// Hands the visitor the gathered triangles of the cells between the corners, from every zone within <see cref="Reach"/> of them, starting a
    /// gather for a zone not asked for yet; false while one of those zones is still being gathered.
    /// </summary>
    bool Collect<TVisitor>(Vector3 min, Vector3 max, bool roofs, ref TVisitor visitor) where TVisitor : struct, ITriangleVisitor
    {
        bool ready = true;
        int cx0 = Cell(min.X);
        int cx1 = Cell(max.X - 1e-3f);
        int cz0 = Cell(min.Z);
        int cz1 = Cell(max.Z - 1e-3f);
        var (first, last) = ZoneSpan(min, max);
        for (int zy = first.Y; zy <= last.Y; zy++)
        {
            for (int zx = first.X; zx <= last.X; zx++)
            {
                var zone = new ZoneCoordinate(zx, zy);
                if (!zone.IsInsideGrid) continue;

                var task = Ask(zone, roofs);
                if (!task.IsCompletedSuccessfully)
                {
                    ready &= !task.IsFaulted && task.IsCompleted;
                    continue;
                }

                VisitCells(task.Result, cx0, cx1, cz0, cz1, ref visitor);
            }
        }

        Evict();
        return ready;
    }

    /// <summary>
    /// Whether the zone's building triangles are gathered (a failed gather counts, with none), starting the gather when it was not asked for yet.
    /// </summary>
    public bool ZoneRead(ZoneCoordinate zone) => Ask(zone, roofs: false).IsCompleted;

    /// <summary>
    /// Keeps the loaded zones' gathered triangles from <see cref="Evict"/>.
    /// </summary>
    public void HoldZones(IReadOnlyCollection<ZoneCoordinate> zones)
    {
        _held = [.. zones];
        Volatile.Write(ref _evictDue, 1);
    }

    /// <summary>
    /// The first and last zone whose buildings may reach the box: a building's triangles are kept with the zone it is placed in and may reach into
    /// the next one, so the zones within <see cref="Reach"/> of it.
    /// </summary>
    static (ZoneCoordinate First, ZoneCoordinate Last) ZoneSpan(Vector3 min, Vector3 max) =>
        (WorldLayout.ZoneOf(min.X - Reach, min.Z - Reach), WorldLayout.ZoneOf(max.X + Reach, max.Z + Reach));

    /// <summary>
    /// The zone's gather, started on a worker (done here when <see cref="Synchronous"/>) when it was not asked for yet, and marked as just used.
    /// </summary>
    Task<Dictionary<(int, int), List<Vector3>>> Ask(ZoneCoordinate zone, bool roofs)
    {
        _used[(zone, roofs)] = Interlocked.Increment(ref _clock);
        return _zones.GetOrAdd((zone, roofs), Start);
    }

    /// <summary>
    /// A new zone's gather (on the calling thread when <see cref="Synchronous"/>); the zone count grew, so <see cref="Evict"/> looks again.
    /// </summary>
    Task<Dictionary<(int, int), List<Vector3>>> Start((ZoneCoordinate Zone, bool Roofs) key)
    {
        Volatile.Write(ref _evictDue, 1);
        return Synchronous ? Task.FromResult(Gather(key.Zone, key.Roofs)) : Task.Run(() => Gather(key.Zone, key.Roofs));
    }

    /// <summary>
    /// Hands the visitor the triangles of the cells in the box (cell columns <paramref name="cx0"/> to <paramref name="cx1"/>, rows
    /// <paramref name="cz0"/> to <paramref name="cz1"/>) from one gathered zone, a cell at a time.
    /// </summary>
    static void VisitCells<TVisitor>(Dictionary<(int, int), List<Vector3>> cells, int cx0, int cx1, int cz0, int cz1, ref TVisitor visitor)
        where TVisitor : struct, ITriangleVisitor
    {
        for (int x = cx0; x <= cx1; x++)
        {
            for (int z = cz0; z <= cz1; z++)
            {
                if (cells.TryGetValue((x, z), out var cell)) visitor.Visit(CollectionsMarshal.AsSpan(cell));
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
    /// Drops the least recently asked for gathered zones past <see cref="MaxZones"/> (only finished ones, never a loaded one).
    /// </summary>
    void Evict()
    {
        if (Interlocked.Exchange(ref _evictDue, 0) == 0) return;

        var held = _held;
        int spare = _zones.Count - MaxZones - _zones.Keys.Count(k => held.Contains(k.Zone));
        if (spare <= 0) return;

        foreach (var (zone, _) in _used.Where(u => !held.Contains(u.Key.Zone)).OrderBy(u => u.Value).Take(spare).ToList())
        {
            if (_zones.TryGetValue(zone, out var task) && task.IsCompleted && _zones.TryRemove(zone, out _))
            {
                _used.TryRemove(zone, out _);
                spare--;
            }
        }

        // A zone still being gathered could not go: look again on the next ask.
        if (spare > 0) Volatile.Write(ref _evictDue, 1);
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
        {
            _disposed = true;
            _gatherer.Dispose();
        }
    }
}
