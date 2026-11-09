using System.Diagnostics;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Builds a zone's complete mesh: the exterior, then every building interior, joined.
/// </summary>
internal static class NavMeshPipeline
{
    /// <summary>
    /// The zone's mesh with its interiors (pruned polygons still present; use <see cref="ZoneNavMesh.WithoutPruned"/> for what is kept).
    /// </summary>
    public static ZoneNavMesh BuildZone(
        ZoneGeometryGatherer gatherer,
        ZoneGeometry geometry,
        NavBuildSettings settings,
        out NavBuildTimes times,
        bool interiors = true,
        Action<string>? log = null)
        => BuildZone(gatherer, geometry, settings, out times, out _, interiors, log);

    /// <summary>
    /// As above, also returning the separately built interior meshes with their building keys (for the debug images and the join sweep).
    /// <paramref name="log"/> receives the debug listing of the interiors and door joins.
    /// </summary>
    public static ZoneNavMesh BuildZone(
        ZoneGeometryGatherer gatherer,
        ZoneGeometry geometry,
        NavBuildSettings settings,
        out NavBuildTimes times,
        out IReadOnlyList<InteriorMesh> interiorMeshes,
        bool interiors = true,
        Action<string>? log = null)
    {
        interiorMeshes = [];
        var exterior = ZoneNavMeshBuilder.Build(geometry, settings, out times);
        exterior.Surfaces = new NavSurfaces(NavSurfaces.From(geometry, geometry.ZoneMin, geometry.ZoneMax));
        exterior.Terrain ??= geometry.Ground;
        if (!interiors)
            return exterior;

        var watch = Stopwatch.StartNew();

        // An interior is one tile: a stair across a tile line inside a building came out cut (the two tiles' contours put their vertices at different heights
        // along a steep ramp, docs/game/pathfinding.md "Interiors"), and the boxes are small. Its regions stay monotone with edges of at most InteriorEdgeCells:
        // watershed regions covered fewer of the original's interior faces (World's End 26.13: 89.6 % for 92.2 %), and an interior has few polygons to save.
        var geometries = GatherInteriors(gatherer, geometry, settings);
        var built = new ZoneNavMesh[geometries.Count];
        Parallel.For(0, geometries.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i =>
        {
            built[i] = ZoneNavMeshBuilder.Build(geometries[i], InteriorSettings(settings, geometries[i]), out _);
            built[i].Surfaces = new NavSurfaces(NavSurfaces.From(geometries[i], geometries[i].ZoneMin, geometries[i].ZoneMax));
        });
        if (log is not null)
        {
            for (int i = 0; i < built.Length; i++)
                DescribeInterior(geometries[i], built[i], log);
        }

        var nonEmpty = NonEmptyInteriors(geometries, built);
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

    /// <summary>
    /// The zone's interiors, gathered as one-tile geometries.
    /// </summary>
    static List<ZoneGeometry> GatherInteriors(ZoneGeometryGatherer gatherer, ZoneGeometry geometry, NavBuildSettings settings)
    {
        var sites = gatherer.InteriorSites(geometry.Zone);
        var geometries = sites
            .Select(s => gatherer.GatherInterior(s, InteriorTileStep * settings.CellSize, agentHeight: settings.AgentHeight))
            .Where(g => g is not null)
            .Select(g => g!)
            .ToList();
        return geometries;
    }

    /// <summary>
    /// The built interiors that have polygons, with their building keys.
    /// </summary>
    static List<InteriorMesh> NonEmptyInteriors(List<ZoneGeometry> geometries, ZoneNavMesh[] built)
    {
        var nonEmpty = new List<InteriorMesh>();
        for (int i = 0; i < built.Length; i++)
        {
            if (built[i].PolygonCount > 0)
                nonEmpty.Add(new InteriorMesh(built[i], geometries[i].InteriorOf, geometries[i].InteriorDoorless, geometries[i].InteriorDestroyed));
        }

        return nonEmpty;
    }

    /// <summary>
    /// An interior's box is rounded up to a multiple of this many cells (its one tile's side).
    /// </summary>
    public const int InteriorTileStep = 8;

    /// <summary>
    /// An interior's longest contour edge in cells (the builder's default before zones went to large tiles and unlimited edges).
    /// </summary>
    const int InteriorEdgeCells = 24;

    /// <summary>
    /// The settings an interior is built with: one tile over its box, monotone regions, edges of at most <see cref="InteriorEdgeCells"/>.
    /// </summary>
    public static NavBuildSettings InteriorSettings(NavBuildSettings settings, ZoneGeometry interior)
    {
        int side = (int)MathF.Round((interior.ZoneMax.X - interior.ZoneMin.X) / settings.CellSize);
        return settings with { Threads = 1, TileCells = side, Watershed = false, MaxEdgeLength = InteriorEdgeCells };
    }

    internal static void DescribeInterior(ZoneGeometry g, ZoneNavMesh m, Action<string> log)
    {
        string seeds = string.Join(" ", g.Seeds.Select(s => $"{s.X:0},{s.Y:0},{s.Z:0}"));
        int doorPolygons = m.Areas.Count(a => a == NavArea.Door);
        string doorless = g.InteriorDoorless ? ", doorless" : "";
        string destroyed = g.InteriorDestroyed ? ", destroyed" : "";
        log($"  interior {g.InteriorOf}: {m.PolygonCount} polygons, {m.KeptCount} kept, {g.Seeds.Count} seeds ({seeds}), {doorPolygons} door polygons, " +
            $"{g.Painters.Count} painters{doorless}{destroyed}, hull y {g.InteriorHull!.YMin:0}..{g.InteriorHull!.YMax:0}");

        // Its connected components (neighbours only, not links), largest first: a stair or upper floor the seed did not reach shows up as an unkept one.
        var component = LabelComponents(m);
        foreach (var group in Enumerable.Range(0, m.PolygonCount).GroupBy(p => component[p]).OrderByDescending(c => c.Count()).Take(12))
        {
            var c = group.Select(p => m.Centre(p)).ToList();
            string kept = m.Kept[group.First()] ? "kept" : "pruned";
            int doors = group.Count(p => m.Areas[p] == NavArea.Door);
            log($"    component {group.Key}: {group.Count()} polygons, {kept}, x {c.Min(v => v.X):0}..{c.Max(v => v.X):0} " +
                $"y {c.Min(v => v.Y):0}..{c.Max(v => v.Y):0} z {c.Min(v => v.Z):0}..{c.Max(v => v.Z):0}, {doors} door");
        }
    }

    /// <summary>
    /// The connected component index of each polygon (flood fill over the neighbours), numbered from 0 in scan order.
    /// </summary>
    static int[] LabelComponents(ZoneNavMesh m)
    {
        var component = new int[m.PolygonCount];
        Array.Fill(component, -1);
        int next = 0;
        var stack = new Stack<int>();
        for (int p = 0; p < m.PolygonCount; p++)
        {
            if (component[p] >= 0)
                continue;

            component[p] = next;
            stack.Push(p);
            while (stack.Count > 0)
            {
                foreach (int q in m.NeighboursOf(stack.Pop()))
                {
                    if (component[q] < 0)
                    {
                        component[q] = next;
                        stack.Push(q);
                    }
                }
            }

            next++;
        }

        return component;
    }
}
