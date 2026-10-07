using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Navigation;

public sealed partial class ZoneGeometryGatherer
{
    // ---- foliage ----

    /// <summary>What the navmesh needs of one placed foliage object: where it stands and its collision file or cutter size.</summary>
    sealed record FoliageObstacle(Vector3 Position, Matrix4x4 Transform, string? Collision, bool Walkable, float Cutter);

    readonly System.Collections.Concurrent.ConcurrentDictionary<ZoneCoordinate, FoliageObstacle[]> foliageZones = new();
    readonly System.Collections.Concurrent.ConcurrentBag<FoliageWorld> foliagePool = [];
    readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), Lazy<byte[]>> overlayTiles = new();
    FoliageCatalog? foliageCatalog;
    const int MaxCachedFoliageZones = 64;

    /// <summary>How far (units) past the margin a foliage object may stand and still be used (engine choice; its collision reaches into the margin).</summary>
    const float FoliageReach = 200;

    /// <summary>The obstacle list of a zone's foliage, placed once and kept (neighbouring zones share their rings), placed in parallel for the missing ones.</summary>
    FoliageObstacle[][] FoliageOf(IReadOnlyList<ZoneCoordinate> zones)
    {
        var missing = zones.Where(z => !foliageZones.ContainsKey(z)).ToList();
        if (missing.Count > 0)
        {
            if (foliageZones.Count + missing.Count > MaxCachedFoliageZones) foliageZones.Clear();
            foliageCatalog ??= FoliageCatalog.Load(db);
            if (overlayTiles.Count > 6) overlayTiles.Clear();
            Parallel.ForEach(missing, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(missing.Count, Environment.ProcessorCount - 1)) }, z =>
            {
                if (!foliagePool.TryTake(out var world)) world = new FoliageWorld(install, db, levels, foliageCatalog, overlayTiles);
                try
                {
                    var list = new List<FoliageObstacle>();
                    foreach (var inst in world.Zone(z).Instances)
                    {
                        var rec = inst.Mesh.Record;
                        string path = rec.GetPath("collision");
                        float cutter = rec.GetFloat("navmesh cutter");
                        if (path.Length == 0) { if (cutter > 0) list.Add(new(inst.Position, inst.Transform, null, false, cutter)); }
                        else if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) list.Add(new(inst.Position, inst.Transform, path, rec.GetBool("walkable"), 0));
                    }
                    foliageZones[z] = [.. list];
                }
                finally { foliagePool.Add(world); }
            });
        }
        return [.. zones.Select(z => foliageZones.TryGetValue(z, out var a) ? a : [])];
    }

    /// <summary>The zone and its eight neighbours inside the grid: objects across the border reach into the margin.</summary>
    static List<ZoneCoordinate> FoliageRing(ZoneCoordinate zone)
    {
        var zones = new List<ZoneCoordinate>();
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var z = new ZoneCoordinate(zone.X + dx, zone.Y + dz);
                if (z.IsInsideGrid) zones.Add(z);
            }
        return zones;
    }

    void AddFoliage(ZoneGeometry g, FoliageObstacle[][] all)
    {
        float margin = g.Margin;
        foreach (var list in all)
            foreach (var o in list)
            {
                var p = o.Position;
                if (p.X < g.ZoneMin.X - margin - FoliageReach || p.X > g.ZoneMax.X + margin + FoliageReach || p.Z < g.ZoneMin.Y - margin - FoliageReach || p.Z > g.ZoneMax.Y + margin + FoliageReach) continue;
                if (o.Collision is null)
                {
                    float cutter = o.Cutter;
                    g.Stats.FoliageCutters++;
                    g.Carvers.Add(new NavVolume([new(p.X - cutter, p.Z - cutter), new(p.X + cutter, p.Z - cutter), new(p.X + cutter, p.Z + cutter), new(p.X - cutter, p.Z + cutter)],
                        p.Y - CutterHalfHeight, p.Y + CutterHalfHeight));
                    continue;
                }
                var prepared = collision.Get(o.Collision);
                if (prepared is null) continue;
                g.Stats.FoliageInstances++;
                foreach (var shape in prepared.Shapes)
                {
                    g.Stats.FoliageShapes++;
                    AddShape(g, shape, o.Transform, o.Walkable ? TerrainSlopeDegrees : 0, carve: CarveConvexObstacles && !o.Walkable);
                }
            }
    }
}
