using System.Diagnostics;

namespace Meitou.Navigation;

/// <summary>Builds a zone's complete mesh: the exterior, then every building interior, joined.</summary>
internal static class NavMeshPipeline
{
    /// <summary>The zone's mesh with its interiors (pruned polygons still present; use <see cref="ZoneNavMesh.WithoutPruned"/> for what is kept).</summary>
    public static ZoneNavMesh BuildZone(ZoneGeometryGatherer gatherer, ZoneGeometry geometry, NavBuildSettings settings, out NavBuildTimes times, bool interiors = true, Action<string>? log = null)
        => BuildZone(gatherer, geometry, settings, out times, out _, interiors, log);

    /// <summary>As above, also returning the separately built interior meshes with their building keys (for the debug images and the join sweep). <paramref name="log"/> receives the debug listing of the interiors and door joins.</summary>
    public static ZoneNavMesh BuildZone(ZoneGeometryGatherer gatherer, ZoneGeometry geometry, NavBuildSettings settings, out NavBuildTimes times, out IReadOnlyList<InteriorMesh> interiorMeshes, bool interiors = true, Action<string>? log = null)
    {
        interiorMeshes = [];
        var exterior = ZoneNavMeshBuilder.Build(geometry, settings, out times);
        exterior.Surfaces = new NavSurfaces(NavSurfaces.From(geometry, geometry.ZoneMin, geometry.ZoneMax));
        exterior.Terrain ??= geometry.Ground;
        if (!interiors) return exterior;
        var watch = Stopwatch.StartNew();
        // An interior is one tile: a stair across a tile line inside a building came out cut (the two tiles' contours put their vertices at different heights
        // along a steep ramp, docs/game/pathfinding.md "Interiors"), and the boxes are small.
        var geometries = gatherer.InteriorSites(geometry.Zone).Select(s => gatherer.GatherInterior(s, InteriorTileStep * settings.CellSize, agentHeight: settings.AgentHeight)).Where(g => g is not null).Select(g => g!).ToList();
        var built = new ZoneNavMesh[geometries.Count];
        Parallel.For(0, geometries.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i =>
        {
            var single = settings with { Threads = 1, TileCells = (int)MathF.Round((geometries[i].ZoneMax.X - geometries[i].ZoneMin.X) / settings.CellSize) };
            built[i] = ZoneNavMeshBuilder.Build(geometries[i], single, out _);
            built[i].Surfaces = new NavSurfaces(NavSurfaces.From(geometries[i], geometries[i].ZoneMin, geometries[i].ZoneMax));
        });
        if (log is not null)
            for (int i = 0; i < built.Length; i++) DescribeInterior(geometries[i], built[i], log);
        var nonEmpty = new List<InteriorMesh>();
        for (int i = 0; i < built.Length; i++)
            if (built[i].PolygonCount > 0) nonEmpty.Add(new InteriorMesh(built[i], geometries[i].InteriorOf, geometries[i].InteriorDoorless, geometries[i].InteriorDestroyed));
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

    /// <summary>An interior's box is rounded up to a multiple of this many cells (its one tile's side).</summary>
    public const int InteriorTileStep = 8;

    internal static void DescribeInterior(ZoneGeometry g, ZoneNavMesh m, Action<string> log)
    {
        log($"  interior {g.InteriorOf}: {m.PolygonCount} polygons, {m.KeptCount} kept, {g.Seeds.Count} seeds ({string.Join(" ", g.Seeds.Select(s => $"{s.X:0},{s.Y:0},{s.Z:0}"))}), {m.Areas.Count(a => a == NavArea.Door)} door polygons, {g.Painters.Count} painters{(g.InteriorDoorless ? ", doorless" : "")}{(g.InteriorDestroyed ? ", destroyed" : "")}, hull y {g.InteriorHull!.YMin:0}..{g.InteriorHull!.YMax:0}");
        // Its connected components (neighbours only, not links), largest first: a stair or upper floor the seed did not reach shows up as an unkept one.
        var component = new int[m.PolygonCount];
        Array.Fill(component, -1);
        int next = 0;
        var stack = new Stack<int>();
        for (int p = 0; p < m.PolygonCount; p++)
        {
            if (component[p] >= 0) continue;
            component[p] = next;
            stack.Push(p);
            while (stack.Count > 0)
                foreach (int q in m.NeighboursOf(stack.Pop())) if (component[q] < 0) { component[q] = next; stack.Push(q); }
            next++;
        }
        foreach (var group in Enumerable.Range(0, m.PolygonCount).GroupBy(p => component[p]).OrderByDescending(c => c.Count()).Take(12))
        {
            var c = group.Select(p => m.Centre(p)).ToList();
            log($"    component {group.Key}: {group.Count()} polygons, {(m.Kept[group.First()] ? "kept" : "pruned")}, x {c.Min(v => v.X):0}..{c.Max(v => v.X):0} y {c.Min(v => v.Y):0}..{c.Max(v => v.Y):0} z {c.Min(v => v.Z):0}..{c.Max(v => v.Z):0}, {group.Count(p => m.Areas[p] == NavArea.Door)} door");
        }
    }
}
