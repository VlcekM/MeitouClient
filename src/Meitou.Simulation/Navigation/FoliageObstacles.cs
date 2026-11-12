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
    /// Zones kept at most; all are dropped when more would be.
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
    /// The placement reads <paramref name="install"/>, <paramref name="db"/> and <paramref name="levels"/>.
    /// </summary>
    public FoliageObstacles(GameInstall install, GameDatabase db, WorldLevelData levels)
    {
        _install = install;
        _db = db;
        _levels = levels;
    }

    /// <summary>
    /// The obstacle list of a zone's foliage, placed once and kept (neighbouring zones share their rings), placed in parallel for the missing ones.
    /// </summary>
    public FoliageObstacle[][] Of(IReadOnlyList<ZoneCoordinate> zones)
    {
        var missing = zones.Where(z => !_foliageZones.ContainsKey(z)).ToList();
        if (missing.Count > 0)
        {
            if (_foliageZones.Count + missing.Count > MaxCachedFoliageZones) _foliageZones.Clear();

            _foliageCatalog ??= FoliageCatalog.Load(_db);
            if (_overlayTiles.Count > 6) _overlayTiles.Clear();

            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(missing.Count, Environment.ProcessorCount - 1)) };
            Parallel.ForEach(missing, options, z => PlaceFoliageZone(z));
        }

        return [.. zones.Select(z => _foliageZones.TryGetValue(z, out var a) ? a : [])];
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
    /// Places one zone's foliage on a pooled foliage world and keeps its obstacles.
    /// </summary>
    void PlaceFoliageZone(ZoneCoordinate z)
    {
        if (!_foliagePool.TryTake(out var world))
            world = new FoliageWorld(_install, _db, _levels, _foliageCatalog, _overlayTiles);

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

            _foliageZones[z] = [.. list];
        }
        finally
        {
            _foliagePool.Add(world);
        }
    }
}
