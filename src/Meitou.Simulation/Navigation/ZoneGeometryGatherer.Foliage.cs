using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Simulation.Navigation;

internal sealed partial class ZoneGeometryGatherer
{
    #region Foliage

    /// <summary>
    /// What the navmesh needs of one placed foliage object: where it stands and its collision file or cutter size.
    /// </summary>
    sealed record FoliageObstacle(Vector3 Position, Matrix4x4 Transform, string? Collision, bool Walkable, float Cutter);

    readonly System.Collections.Concurrent.ConcurrentDictionary<ZoneCoordinate, FoliageObstacle[]> _foliageZones = new();
    readonly System.Collections.Concurrent.ConcurrentBag<FoliageWorld> _foliagePool = [];
    readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), Lazy<byte[]>> _overlayTiles = new();
    FoliageCatalog? _foliageCatalog;
    const int MaxCachedFoliageZones = 64;

    /// <summary>
    /// How far (units) past the margin a foliage object may stand and still be used (engine choice; its collision reaches into the margin).
    /// </summary>
    const float FoliageReach = 200;

    /// <summary>
    /// The obstacle list of a zone's foliage, placed once and kept (neighbouring zones share their rings), placed in parallel for the missing ones.
    /// </summary>
    FoliageObstacle[][] FoliageOf(IReadOnlyList<ZoneCoordinate> zones)
    {
        var missing = zones.Where(z => !_foliageZones.ContainsKey(z)).ToList();
        if (missing.Count > 0)
        {
            if (_foliageZones.Count + missing.Count > MaxCachedFoliageZones)
                _foliageZones.Clear();

            _foliageCatalog ??= FoliageCatalog.Load(_db);
            if (_overlayTiles.Count > 6)
                _overlayTiles.Clear();

            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(missing.Count, Environment.ProcessorCount - 1)) };
            Parallel.ForEach(missing, options, z => PlaceFoliageZone(z));
        }

        return [.. zones.Select(z => _foliageZones.TryGetValue(z, out var a) ? a : [])];
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
                    if (cutter > 0)
                        list.Add(new(inst.Position, inst.Transform, null, false, cutter));
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

    /// <summary>
    /// The zone and its eight neighbours inside the grid: objects across the border reach into the margin.
    /// </summary>
    static List<ZoneCoordinate> FoliageRing(ZoneCoordinate zone)
    {
        var zones = new List<ZoneCoordinate>();
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                var z = new ZoneCoordinate(zone.X + dx, zone.Y + dz);
                if (z.IsInsideGrid)
                    zones.Add(z);
            }
        }

        return zones;
    }

    void AddFoliage(ZoneGeometry g, FoliageObstacle[][] all)
    {
        float margin = g.Margin;
        foreach (var list in all)
        {
            foreach (var o in list)
                AddFoliageObstacle(g, o, margin);
        }
    }

    /// <summary>
    /// Adds one foliage object near the zone: its cutter square, or its collision shapes.
    /// </summary>
    void AddFoliageObstacle(ZoneGeometry g, FoliageObstacle o, float margin)
    {
        var p = o.Position;
        if (p.X < g.ZoneMin.X - margin - FoliageReach || p.X > g.ZoneMax.X + margin + FoliageReach
            || p.Z < g.ZoneMin.Y - margin - FoliageReach || p.Z > g.ZoneMax.Y + margin + FoliageReach)
            return;

        if (o.Collision is null)
        {
            float cutter = o.Cutter;
            g.Stats.FoliageCutters++;
            var corners = new[]
            {
                new Vector2(p.X - cutter, p.Z - cutter),
                new Vector2(p.X + cutter, p.Z - cutter),
                new Vector2(p.X + cutter, p.Z + cutter),
                new Vector2(p.X - cutter, p.Z + cutter),
            };
            g.Carvers.Add(new NavVolume(corners, p.Y - CutterHalfHeight, p.Y + CutterHalfHeight));
            return;
        }

        var prepared = _collision.Get(o.Collision);
        if (prepared is null)
            return;

        g.Stats.FoliageInstances++;
        int solids = g.Solids.Count;
        foreach (var shape in prepared.Shapes)
        {
            g.Stats.FoliageShapes++;
            if (!shape.IsConvex)
                g.Stats.FoliageMeshes++;

            // Walkable foliage meshes (swamp tree domes, open below) hold no ground under them in the original's tiles (docs/game/pathfinding.md, "Map features").
            AddShape(g, shape, o.Transform, o.Walkable ? TerrainSlopeDegrees : 0, carve: CarveConvexObstacles && !o.Walkable, meshSolid: o.Walkable);
        }

        for (int k = solids; k < g.Solids.Count; k++)
            g.Solids[k].Feature = true;
    }

    #endregion
}
