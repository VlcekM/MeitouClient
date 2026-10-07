using System.Diagnostics;

namespace Meitou.Navigation;

/// <summary>Builds a zone's complete mesh: the exterior, then every building interior, joined.</summary>
public static class NavMeshPipeline
{
    /// <summary>The zone's mesh with its interiors (pruned polygons still present; use <see cref="ZoneNavMesh.WithoutPruned"/> for what is kept).</summary>
    public static ZoneNavMesh BuildZone(ZoneGeometryGatherer gatherer, ZoneGeometry geometry, NavBuildSettings settings, out NavBuildTimes times, bool interiors = true, Action<string>? log = null)
        => BuildZone(gatherer, geometry, settings, out times, out _, interiors, log);

    /// <summary>As above, also returning the separately built interior meshes (for the debug images). <paramref name="log"/> receives the debug listing of the interiors and door joins.</summary>
    public static ZoneNavMesh BuildZone(ZoneGeometryGatherer gatherer, ZoneGeometry geometry, NavBuildSettings settings, out NavBuildTimes times, out IReadOnlyList<ZoneNavMesh> interiorMeshes, bool interiors = true, Action<string>? log = null)
    {
        interiorMeshes = [];
        var exterior = ZoneNavMeshBuilder.Build(geometry, settings, out times);
        if (!interiors) return exterior;
        var watch = Stopwatch.StartNew();
        float tileWorld = settings.TileCells * settings.CellSize;
        var geometries = gatherer.InteriorSites(geometry.Zone).Select(s => gatherer.GatherInterior(s, tileWorld)).Where(g => g is not null).Select(g => g!).ToList();
        var single = settings with { Threads = 1 };
        var built = new ZoneNavMesh[geometries.Count];
        Parallel.For(0, geometries.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i =>
            built[i] = ZoneNavMeshBuilder.Build(geometries[i], single, out _));
        if (log is not null)
            for (int i = 0; i < built.Length; i++) DescribeInterior(geometries[i], built[i], log);
        var nonEmpty = built.Where(m => m.PolygonCount > 0).ToList();
        interiorMeshes = nonEmpty;
        var combined = NavInteriors.Combine(exterior, nonEmpty, log);
        times.Interiors = watch.Elapsed.TotalMilliseconds;
        times.InteriorCount = built.Length;
        times.Total += times.Interiors;
        times.Polygons = combined.PolygonCount;
        times.KeptPolygons = combined.KeptCount;
        times.Vertices = combined.Vertices.Length;
        return combined;
    }

    static void DescribeInterior(ZoneGeometry g, ZoneNavMesh m, Action<string> log)
    {
        log($"  interior {g.InteriorOf}: {m.PolygonCount} polygons, {m.KeptCount} kept, {g.Seeds.Count} seeds ({string.Join(" ", g.Seeds.Select(s => $"{s.X:0},{s.Y:0},{s.Z:0}"))}), {m.Areas.Count(a => a == NavArea.Door)} door polygons, {g.Painters.Count} painters, hull y {g.InteriorHull!.YMin:0}..{g.InteriorHull!.YMax:0}");
        if (m.DoorIds is null) return;
        for (int p = 0; p < m.PolygonCount; p++)
            if (m.Kept[p])
            {
                var c = m.Centre(p);
                log($"    kept {p}: {c.X:0},{c.Y:0},{c.Z:0} area {m.Areas[p]} door {m.DoorOf?[p]}");
            }
    }
}
