using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// The placed foliage of the zones the navmesh gatherer asks for, reduced to what the navmesh needs of each object: placed once per zone on pooled
/// foliage worlds and kept (neighbouring zones share their rings). Thread-safe: zones are placed in parallel.
/// </summary>
internal sealed class FoliageObstacles : IDisposable
{
    /// <summary>
    /// What the navmesh needs of one placed foliage object: where it stands and its collision file or cutter size.
    /// </summary>
    internal sealed record FoliageObstacle(Vector3 Position, Matrix4x4 Transform, string? Collision, bool Walkable, float Cutter);

    /// <summary>
    /// Zones kept at most; when more would be, all are dropped but the kept ones of the ring being asked for.
    /// </summary>
    const int MaxCachedFoliageZones = 64;

    /// <summary>
    /// The game install the foliage worlds read.
    /// </summary>
    readonly GameInstall _install;

    /// <summary>
    /// The game data the foliage records come from.
    /// </summary>
    readonly GameDatabase _db;

    /// <summary>
    /// The level data the foliage worlds place against.
    /// </summary>
    readonly WorldLevelData _levels;

    /// <summary>
    /// The obstacle list of each placed zone.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<ZoneCoordinate, FoliageObstacle[]> _foliageZones = new();

    /// <summary>
    /// Foliage worlds not in use, one taken per zone being placed.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentBag<FoliageWorld> _foliagePool = [];

    /// <summary>
    /// The overlay tiles the foliage worlds share.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), Lazy<byte[]>> _overlayTiles = new();

    /// <summary>
    /// The foliage catalog, loaded the first time a zone is placed.
    /// </summary>
    FoliageCatalog? _foliageCatalog;

    /// <summary>
    /// The foliage layout cache the placements read and write (the renderer's files: the same key), made with the catalog; null without one.
    /// The navmesh placed every zone it gathered again on every load (some 3 CPU-s of a save's warm-up).
    /// </summary>
    FoliageLayoutCache? _layoutCache;

    /// <summary>
    /// The player's towns the placement shares; null: none are ever added.
    /// </summary>
    readonly NavRuntimeBuildings? _runtime;

    /// <summary>
    /// The player's towns the kept zones were placed with.
    /// </summary>
    PlayerFoliageTowns _placedWith = PlayerFoliageTowns.None;

    /// <summary>
    /// The placement reads <paramref name="install"/>, <paramref name="db"/> and <paramref name="levels"/>, and keeps the towns of
    /// <paramref name="runtime"/> (null: none) away.
    /// </summary>
    public FoliageObstacles(GameInstall install, GameDatabase db, WorldLevelData levels, NavRuntimeBuildings? runtime = null)
    {
        _install = install;
        _db = db;
        _levels = levels;
        _runtime = runtime;
    }

    /// <summary>
    /// The zones' kept obstacles that a player town founded, moved or gone since they were placed can change are dropped: they are placed again
    /// with the towns as they are now (the original places a zone's foliage when it pages in, against the live town list; here the next gather of
    /// the zone is the nearest the host comes to that).
    /// </summary>
    void UseTowns(PlayerFoliageTowns towns)
    {
        if (ReferenceEquals(towns, _placedWith)) return;

        foreach (var town in PlayerFoliageTowns.Difference(_placedWith.Towns, towns.Towns))
        {
            foreach (var zone in _foliageZones.Keys)
            {
                var (min, max) = PlayerFoliageTowns.Box(zone);
                if (towns.Affects(town, min, max, 0)) _foliageZones.TryRemove(zone, out _);
            }
        }

        _placedWith = towns;
    }

    /// <summary>
    /// The obstacle list of a zone's foliage, placed once and kept (neighbouring zones share their rings), placed in parallel for the missing ones.
    /// </summary>
    public FoliageObstacle[][] Of(IReadOnlyList<ZoneCoordinate> zones)
    {
        UseTowns(_runtime?.FoliageTowns ?? PlayerFoliageTowns.None);

        // The kept lists are taken before anything is dropped: a zone of this call that was kept is not placed again, so it must not be read
        // back from a store that was emptied meanwhile.
        var lists = new FoliageObstacle[zones.Count][];
        var missing = new List<int>();
        for (int i = 0; i < zones.Count; i++)
        {
            if (_foliageZones.TryGetValue(zones[i], out var kept)) lists[i] = kept;
            else missing.Add(i);
        }

        if (missing.Count == 0) return lists;

        if (_foliageZones.Count + missing.Count > MaxCachedFoliageZones) DropAllBut(zones, lists);
        LoadCatalog();
        if (_overlayTiles.Count > 6) _overlayTiles.Clear();

        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(missing.Count, Environment.ProcessorCount - 1)) };
        Parallel.ForEach(missing, options, i => lists[i] = PlaceFoliageZone(zones[i]));
        return lists;
    }

    /// <summary>
    /// Drops every kept zone but those of <paramref name="zones"/> that have a list in <paramref name="lists"/> (the ring being asked for, which
    /// the next zone's ring shares).
    /// </summary>
    void DropAllBut(IReadOnlyList<ZoneCoordinate> zones, FoliageObstacle[][] lists)
    {
        _foliageZones.Clear();
        for (int i = 0; i < zones.Count; i++)
            if (lists[i] is { } kept) _foliageZones[zones[i]] = kept;
    }

    /// <summary>
    /// Loads the foliage catalog and opens the layout cache, the first time a zone is placed.
    /// </summary>
    void LoadCatalog()
    {
        if (_foliageCatalog is not null) return;

        _foliageCatalog = FoliageCatalog.Load(_db);
        try
        {
            _layoutCache = new FoliageLayoutCache(_install, _foliageCatalog, FoliageWorld.ReadTowns(_db, _levels));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _layoutCache = null;
        }
    }

    /// <summary>
    /// Disposes the pooled foliage worlds.
    /// </summary>
    public void Dispose()
    {
        foreach (var w in _foliagePool)
            w.Dispose();
    }

    /// <summary>
    /// Places one zone's foliage on a pooled foliage world, keeps its obstacles and returns them.
    /// </summary>
    FoliageObstacle[] PlaceFoliageZone(ZoneCoordinate z)
    {
        if (!_foliagePool.TryTake(out var world))
            world = new FoliageWorld(_install, _db, _levels, _foliageCatalog, _overlayTiles, _layoutCache);

        world.ExtraTowns = _placedWith.Towns;

        try
        {
            var list = new List<FoliageObstacle>();
            foreach (var inst in world.Zone(z).Instances)
            {
                var rec = inst.Mesh.Record;
                string path = rec.GetPath("collision");
                float cutter = rec.GetFloat("navmesh cutter");
                if (path.Length == 0)
                {
                    if (cutter > 0) list.Add(new(inst.Position, inst.Transform, null, false, cutter));
                }
                else if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(new(inst.Position, inst.Transform, path, rec.GetBool("walkable"), 0));
                }
            }

            FoliageObstacle[] obstacles = [.. list];
            _foliageZones[z] = obstacles;
            return obstacles;
        }
        finally
        {
            _foliagePool.Add(world);
        }
    }
}
