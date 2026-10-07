using System.Diagnostics;
using System.Numerics;

namespace Meitou.Navigation;

/// <summary>
/// Building interiors: each building with an interior mask is built as its own small mesh (<see cref="ZoneGeometryGatherer.GatherInterior"/>) and put into
/// the zone's mesh as extra polygons, joined to the exterior along the door polygons (docs/game/pathfinding.md, "Interiors"). To the queries an interior
/// is part of the zone: the door is an ordinary link.
/// </summary>
public static class NavInteriors
{
    /// <summary>How close (units) two door edges have to be to be joined, and how far apart in height.</summary>
    public const float JoinDistance = 6, JoinHeight = 12;

    /// <summary>The zone's exterior mesh with the interior meshes appended and joined; both kept and pruned polygons are carried over.</summary>
    public static ZoneNavMesh Combine(ZoneNavMesh exterior, IReadOnlyList<ZoneNavMesh> interiors)
    {
        if (interiors.Count == 0) return exterior;
        var vertices = new List<Vector3>(exterior.Vertices);
        var polygons = new List<int[]>(exterior.Polygons);
        var neighbours = new List<int[]>(exterior.Neighbours);
        var areas = new List<byte>(exterior.Areas);
        var kept = new List<bool>(exterior.Kept);
        var links = new List<List<int>?>();
        for (int p = 0; p < exterior.PolygonCount; p++) links.Add(exterior.LinksOf(p).Length > 0 ? [.. exterior.LinksOf(p).ToArray()] : null);
        var doorIds = new List<string>(exterior.DoorIds ?? []);
        var doorOf = new List<int>(exterior.DoorOf ?? Enumerable.Repeat(-1, exterior.PolygonCount));
        // The zone keeps its own bounds: cross-zone linking finds the borders by them (an interior's padded box may reach past them).
        var lo = exterior.BoundsMin;
        var hi = exterior.BoundsMax;
        var offsets = new List<int>();

        foreach (var m in interiors)
        {
            int vo = vertices.Count, po = polygons.Count;
            offsets.Add(po);
            vertices.AddRange(m.Vertices);
            var remap = (m.DoorIds ?? []).Select(id => { int i = doorIds.IndexOf(id); if (i < 0) { i = doorIds.Count; doorIds.Add(id); } return i; }).ToArray();
            for (int p = 0; p < m.PolygonCount; p++)
            {
                polygons.Add(m.Polygons[p].Select(i => i + vo).ToArray());
                neighbours.Add(m.Neighbours[p].Select(n => n >= 0 ? n + po : -1).ToArray());
                areas.Add(m.Areas[p]);
                kept.Add(m.Kept[p]);
                var l = m.LinksOf(p);
                List<int>? list = null;
                if (l.Length > 0)
                {
                    list = [];
                    for (int i = 0; i < l.Length; i += 2) { list.Add(l[i]); list.Add(l[i + 1] + po); }
                }
                links.Add(list);
                doorOf.Add(m.DoorOf is { } d && d[p] >= 0 ? remap[d[p]] : -1);
            }
        }

        int exteriorCount = exterior.PolygonCount;
        for (int k = 0; k < interiors.Count; k++)
            Join(vertices, polygons, neighbours, areas, kept, links, doorOf, doorIds, exteriorCount, offsets[k], interiors[k].PolygonCount);

        return new ZoneNavMesh
        {
            ZoneX = exterior.ZoneX, ZoneZ = exterior.ZoneZ, Vertices = [.. vertices], Polygons = [.. polygons], Neighbours = [.. neighbours], Areas = [.. areas],
            Links = links.Select(l => l is { Count: > 0 } ? l.ToArray() : null).ToArray(), Kept = [.. kept], BoundsMin = lo, BoundsMax = hi,
            DoorIds = doorIds.Count > 0 ? [.. doorIds] : null, DoorOf = doorIds.Count > 0 ? [.. doorOf] : null,
        };
    }

    /// <summary>Links the door polygons of one interior (polygons [first, first + count)) to the exterior's door polygons of the same door.</summary>
    static void Join(List<Vector3> v, List<int[]> polygons, List<int[]> neighbours, List<byte> areas, List<bool> kept, List<List<int>?> links, List<int> doorOf, List<string> doorIds,
        int exteriorCount, int first, int count)
    {
        bool Free(int p, int e) => neighbours[p][e] < 0 && !HasLink(links[p], e);
        for (int door = 0; door < doorIds.Count; door++)
        {
            var inside = Enumerable.Range(first, count).Where(p => kept[p] && areas[p] == NavArea.Door && doorOf[p] == door).ToList();
            if (inside.Count == 0) continue;
            var outside = Enumerable.Range(0, exteriorCount).Where(p => kept[p] && areas[p] == NavArea.Door && doorOf[p] == door).ToList();
            // An edge that faces several polygons links to all of them, so which edges are open is decided before any link is added.
            var openInside = inside.SelectMany(p => Enumerable.Range(0, polygons[p].Length).Where(e => Free(p, e)).Select(e => (p, e))).ToList();
            var openOutside = outside.SelectMany(q => Enumerable.Range(0, polygons[q].Length).Where(f => Free(q, f)).Select(f => (q, f))).ToList();
            foreach (var (p, e) in openInside)
                foreach (var (q, f) in openOutside)
                    {
                        var a0 = v[polygons[p][e]]; var a1 = v[polygons[p][(e + 1) % polygons[p].Length]];
                        {
                            var b0 = v[polygons[q][f]]; var b1 = v[polygons[q][(f + 1) % polygons[q].Length]];
                            if (!Facing(a0, a1, b0, b1)) continue;
                            if (Environment.GetEnvironmentVariable("NAV_VERBOSE") is not null) Console.WriteLine($"    door join {doorIds[door]}: interior polygon {p - first} edge {e} to exterior {q} edge {f}");
                            (links[p] ??= []).AddRange([e, q]);
                            (links[q] ??= []).AddRange([f, p]);
                        }
                    }
        }
    }

    static bool HasLink(List<int>? l, int edge)
    {
        if (l is null) return false;
        for (int i = 0; i < l.Count; i += 2) if (l[i] == edge) return true;
        return false;
    }

    /// <summary>Two edges that run along each other: their midpoints and both ends within <see cref="JoinDistance"/> in plan and <see cref="JoinHeight"/> in height, overlapping.</summary>
    static bool Facing(Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1)
    {
        var ma = (a0 + a1) * 0.5f; var mb = (b0 + b1) * 0.5f;
        if (MathF.Abs(ma.Y - mb.Y) > JoinHeight) return false;
        var d = new Vector2(a1.X - a0.X, a1.Z - a0.Z);
        float len = d.Length();
        if (len < 1e-4f) return false;
        d /= len;
        float Along(Vector3 p) => (p.X - a0.X) * d.X + (p.Z - a0.Z) * d.Y;
        float Off(Vector3 p) => MathF.Abs((p.X - a0.X) * d.Y - (p.Z - a0.Z) * d.X);
        if (Off(b0) > JoinDistance || Off(b1) > JoinDistance) return false;
        float lo = Math.Min(Along(b0), Along(b1)), hi = Math.Max(Along(b0), Along(b1));
        return Math.Min(hi, len) - Math.Max(lo, 0) > 0.5f;
    }
}

/// <summary>Builds a zone's complete mesh: the exterior, then every building interior, joined.</summary>
public static class NavMeshPipeline
{
    /// <summary>The zone's mesh with its interiors (pruned polygons still present; use <see cref="ZoneNavMesh.WithoutPruned"/> for what is kept).</summary>
    public static ZoneNavMesh BuildZone(ZoneGeometryGatherer gatherer, ZoneGeometry geometry, NavBuildSettings settings, out NavBuildTimes times, bool interiors = true)
        => BuildZone(gatherer, geometry, settings, out times, out _, interiors);

    /// <summary>As above, also returning the separately built interior meshes (for the debug images).</summary>
    public static ZoneNavMesh BuildZone(ZoneGeometryGatherer gatherer, ZoneGeometry geometry, NavBuildSettings settings, out NavBuildTimes times, out IReadOnlyList<ZoneNavMesh> interiorMeshes, bool interiors = true)
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
        if (Environment.GetEnvironmentVariable("NAV_VERBOSE") is not null)
            for (int i = 0; i < built.Length; i++) {
                Console.WriteLine($"  interior {geometries[i].InteriorOf}: {built[i].PolygonCount} polygons, {built[i].KeptCount} kept, {geometries[i].Seeds.Count} seeds ({string.Join(" ", geometries[i].Seeds.Select(s => $"{s.X:0},{s.Y:0},{s.Z:0}"))}), {built[i].Areas.Count(a => a == NavArea.Door)} door polygons, {geometries[i].Painters.Count} painters, hull y {geometries[i].InteriorHull!.YMin:0}..{geometries[i].InteriorHull!.YMax:0}");
                if (built[i].DoorIds is not null) for (int p = 0; p < built[i].PolygonCount; p++) if (built[i].Kept[p]) { var c = built[i].Centre(p); Console.WriteLine($"    kept {p}: {c.X:0},{c.Y:0},{c.Z:0} area {built[i].Areas[p]} door {built[i].DoorOf?[p]}"); } }
        interiorMeshes = [.. built.Where(m => m.PolygonCount > 0)];
        var combined = NavInteriors.Combine(exterior, [.. built.Where(m => m.PolygonCount > 0)]);
        times.Interiors = watch.Elapsed.TotalMilliseconds;
        times.InteriorCount = built.Length;
        times.Total += times.Interiors;
        times.Polygons = combined.PolygonCount;
        times.KeptPolygons = combined.KeptCount;
        times.Vertices = combined.Vertices.Length;
        return combined;
    }
}
