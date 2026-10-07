using System.Diagnostics;
using System.Numerics;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Recast;

namespace Meitou.Navigation;

/// <summary>Parameters of the zone navmesh builder (Kenshi units; docs/game/pathfinding.md, "What our builder needs").</summary>
public sealed record NavBuildSettings
{
    /// <summary>Horizontal cell size. Havok has no grid; 2 keeps the 9-unit minimum passage at 4 to 5 cells.</summary>
    public float CellSize { get; init; } = 2f;
    public float CellHeight { get; init; } = 1f;
    /// <summary>Tile edge in cells; the zone's cells per side must be a multiple of it.</summary>
    public int TileCells { get; init; } = 48;
    /// <summary>characterHeight 1.8 Havok units.</summary>
    public float AgentHeight { get; init; } = 18f;
    /// <summary>edgeMatchingParams.maxStepHeight 0.5 Havok units.</summary>
    public float MaxClimb { get; init; } = 5f;
    /// <summary>Contour simplification error in cells.</summary>
    public float MaxSimplificationError { get; init; } = 1.3f;
    /// <summary>Longest contour edge in cells (0: unlimited).</summary>
    public int MaxEdgeLength { get; init; } = 24;
    public int MinRegionArea { get; init; } = 4;
    public int MergeRegionArea { get; init; } = 40;
    public bool Watershed { get; init; }
    /// <summary>Worker threads for tiles; 0 means all but one core.</summary>
    public int Threads { get; init; }
    /// <summary>Distance (units) within which a seed claims a region (regionPruningSettings.minDistanceToSeedPoints 0.4 Havok).</summary>
    public float SeedDistance { get; init; } = 4f;
    /// <summary>Vertical slack for the seed test (a seed's Y is where it was authored).</summary>
    public float SeedHeightSlack { get; init; } = 18f;
    /// <summary>How far (in cells) a door hull is grown before it paints cells, so a thin leaf closes its doorway.</summary>
    public float DoorInflateCells { get; init; } = 1f;
}

/// <summary>Timings of one build, milliseconds.</summary>
public sealed class NavBuildTimes
{
    public double Tiles, Stitch, Prune, Total;
    /// <summary>CPU milliseconds summed over the tiles: rasterise and filter, compact and areas, regions, contours, polygons.</summary>
    public double CpuRaster, CpuCompact, CpuRegions, CpuContours, CpuMesh;
    public int TileCount, Polygons, KeptPolygons, Vertices, InteriorCount;
    /// <summary>Milliseconds spent on the building interiors (gather, build, join); included in <see cref="Total"/>.</summary>
    public double Interiors;
}

/// <summary>
/// Builds a zone's navmesh from its gathered geometry with Recast (DotRecast): per tile rasterise the triangles with their areas, cut carvers,
/// paint doors, partition, contour and triangulate; then stitch the tiles, and keep only the regions near a seed
/// (docs/game/pathfinding.md, "Region pruning by seeds"). Radius 0: clearance is a query-time check.
/// </summary>
public static class ZoneNavMeshBuilder
{
    public static ZoneNavMesh Build(ZoneGeometry g, NavBuildSettings s, out NavBuildTimes times)
    {
        var total = Stopwatch.StartNew();
        times = new NavBuildTimes();
        float zoneSize = g.ZoneMax.X - g.ZoneMin.X;
        int cellsPerSide = (int)MathF.Round(zoneSize / s.CellSize);
        if (cellsPerSide % s.TileCells != 0) throw new ArgumentException($"{cellsPerSide} cells per zone side is not a multiple of {s.TileCells}");
        int tilesPerSide = cellsPerSide / s.TileCells;
        float tileWorld = s.TileCells * s.CellSize;
        var verts = g.Vertices.ToArray();
        var indices = g.Indices.ToArray();
        var areas = g.Areas.Select(a => (int)a).ToArray();

        // Bucket the triangles and the volumes by tile.
        var triBuckets = new List<int>[tilesPerSide * tilesPerSide];
        var carvers = new List<int>[tilesPerSide * tilesPerSide];
        var painters = new List<int>[tilesPerSide * tilesPerSide];
        for (int i = 0; i < triBuckets.Length; i++) { triBuckets[i] = []; carvers[i] = []; painters[i] = []; }
        void Tiles(float x0, float z0, float x1, float z1, Action<int> add)
        {
            int tx0 = Math.Clamp((int)MathF.Floor((x0 - g.ZoneMin.X) / tileWorld), 0, tilesPerSide - 1), tx1 = Math.Clamp((int)MathF.Floor((x1 - g.ZoneMin.X) / tileWorld), 0, tilesPerSide - 1);
            int tz0 = Math.Clamp((int)MathF.Floor((z0 - g.ZoneMin.Y) / tileWorld), 0, tilesPerSide - 1), tz1 = Math.Clamp((int)MathF.Floor((z1 - g.ZoneMin.Y) / tileWorld), 0, tilesPerSide - 1);
            if (x1 < g.ZoneMin.X || x0 > g.ZoneMax.X || z1 < g.ZoneMin.Y || z0 > g.ZoneMax.Y) return;
            for (int tz = tz0; tz <= tz1; tz++)
                for (int tx = tx0; tx <= tx1; tx++) add(tz * tilesPerSide + tx);
        }
        for (int t = 0; t < indices.Length; t += 3)
        {
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            for (int k = 0; k < 3; k++)
            {
                float x = verts[indices[t + k] * 3], z = verts[indices[t + k] * 3 + 2];
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); z0 = Math.Min(z0, z); z1 = Math.Max(z1, z);
            }
            int tri = t / 3;
            Tiles(x0, z0, x1, z1, b => triBuckets[b].Add(tri));
        }
        for (int c = 0; c < g.Carvers.Count; c++)
        {
            var (a, b) = Bounds(g.Carvers[c]);
            int id = c;
            Tiles(a.X, a.Y, b.X, b.Y, k => carvers[k].Add(id));
        }
        for (int c = 0; c < g.Painters.Count; c++)
        {
            var (a, b) = Bounds(g.Painters[c]);
            int id = c;
            Tiles(a.X, a.Y, b.X, b.Y, k => painters[k].Add(id));
        }

        ticksRaster = ticksCompact = ticksRegions = ticksContours = ticksMesh = 0;
        var meshes = new RcPolyMesh?[tilesPerSide * tilesPerSide];
        var workers = s.Threads > 0 ? s.Threads : Math.Max(1, Environment.ProcessorCount - 1);
        var watch = Stopwatch.StartNew();
        Parallel.For(0, meshes.Length, new ParallelOptions { MaxDegreeOfParallelism = workers }, tile =>
        {
            if (triBuckets[tile].Count == 0) return;
            int tx = tile % tilesPerSide, tz = tile / tilesPerSide;
            meshes[tile] = BuildTile(g, s, verts, indices, areas, triBuckets[tile], carvers[tile], painters[tile],
                g.ZoneMin.X + tx * tileWorld, g.ZoneMin.Y + tz * tileWorld, tileWorld);
        });
        times.Tiles = watch.Elapsed.TotalMilliseconds;
        times.TileCount = meshes.Count(m => m is not null);
        double ms = 1000.0 / Stopwatch.Frequency;
        times.CpuRaster = ticksRaster * ms; times.CpuCompact = ticksCompact * ms; times.CpuRegions = ticksRegions * ms; times.CpuContours = ticksContours * ms; times.CpuMesh = ticksMesh * ms;

        watch.Restart();
        var mesh = Stitch(g, s, meshes, tilesPerSide, tileWorld);
        AssignDoors(mesh, g, s);
        times.Stitch = watch.Elapsed.TotalMilliseconds;

        watch.Restart();
        SeedPruner.Prune(mesh, g, s);
        times.Prune = watch.Elapsed.TotalMilliseconds;
        times.Total = total.Elapsed.TotalMilliseconds;
        times.Polygons = mesh.PolygonCount;
        times.KeptPolygons = mesh.KeptCount;
        times.Vertices = mesh.Vertices.Length;
        return mesh;
    }

    static long ticksRaster, ticksCompact, ticksRegions, ticksContours, ticksMesh;

    static (Vector2 Min, Vector2 Max) Bounds(NavVolume v)
    {
        var lo = new Vector2(float.MaxValue);
        var hi = new Vector2(float.MinValue);
        foreach (var p in v.Polygon) { lo = Vector2.Min(lo, p); hi = Vector2.Max(hi, p); }
        return (lo, hi);
    }

    static RcPolyMesh? BuildTile(ZoneGeometry g, NavBuildSettings s, float[] verts, int[] indices, int[] areas, List<int> tris, List<int> carvers, List<int> painters,
        float x0, float z0, float tileWorld)
    {
        var ctx = new RcContext();
        long t0 = Stopwatch.GetTimestamp();
        int walkableHeight = (int)MathF.Ceiling(s.AgentHeight / s.CellHeight);
        int walkableClimb = (int)MathF.Floor(s.MaxClimb / s.CellHeight);

        // Tile bounds in Y from the triangles that touch it.
        float yMin = float.MaxValue, yMax = float.MinValue;
        foreach (int t in tris)
            for (int k = 0; k < 3; k++)
            {
                float y = verts[indices[t * 3 + k] * 3 + 1];
                yMin = Math.Min(yMin, y); yMax = Math.Max(yMax, y);
            }
        float ch = s.CellHeight;
        if ((yMax - yMin) / ch > 8000) ch = (yMax - yMin) / 8000f;
        var bmin = new RcVec3f(x0, yMin - 1, z0);
        var bmax = new RcVec3f(x0 + tileWorld, yMax + 1, z0 + tileWorld);
        int cells = s.TileCells;
        var hf = new RcHeightfield(cells, cells, bmin, bmax, s.CellSize, ch, 0);

        var tileTris = new int[tris.Count * 3];
        var tileAreas = new int[tris.Count];
        for (int i = 0; i < tris.Count; i++)
        {
            int t = tris[i];
            tileTris[i * 3] = indices[t * 3]; tileTris[i * 3 + 1] = indices[t * 3 + 1]; tileTris[i * 3 + 2] = indices[t * 3 + 2];
            tileAreas[i] = areas[t];
        }
        RcRasterizations.RasterizeTriangles(ctx, verts, tileTris, tileAreas, tris.Count, hf, walkableClimb);
        RcFilters.FilterLowHangingWalkableObstacles(ctx, walkableClimb, hf);
        RcFilters.FilterWalkableLowHeightSpans(ctx, walkableHeight, hf);
        long t1 = Stopwatch.GetTimestamp();
        var chf = RcCompacts.BuildCompactHeightfield(ctx, walkableHeight, walkableClimb, hf);

        foreach (int c in carvers) Mark(ctx, g.Carvers[c], NavArea.Null, chf);
        foreach (int c in painters) Mark(ctx, g.Painters[c], NavArea.Door, chf, s.CellSize * s.DoorInflateCells);

        long t2 = Stopwatch.GetTimestamp();
        if (s.Watershed)
        {
            RcRegions.BuildDistanceField(ctx, chf);
            RcRegions.BuildRegions(ctx, chf, s.MinRegionArea, s.MergeRegionArea);
        }
        else RcRegions.BuildRegionsMonotone(ctx, chf, s.MinRegionArea, s.MergeRegionArea);
        long t3 = Stopwatch.GetTimestamp();
        var cset = RcContours.BuildContours(ctx, chf, s.MaxSimplificationError, s.MaxEdgeLength, 1);
        long t4 = Stopwatch.GetTimestamp();
        var result = cset.conts.Count == 0 ? null : RcMeshs.BuildPolyMesh(ctx, cset, ZoneNavMesh.MaxVerticesPerPolygon);
        long t5 = Stopwatch.GetTimestamp();
        Interlocked.Add(ref ticksRaster, t1 - t0);
        Interlocked.Add(ref ticksCompact, t2 - t1);
        Interlocked.Add(ref ticksRegions, t3 - t2);
        Interlocked.Add(ref ticksContours, t4 - t3);
        Interlocked.Add(ref ticksMesh, t5 - t4);
        return result;
    }

    static void Mark(RcContext ctx, NavVolume v, byte area, RcCompactHeightfield chf, float inflate = 0)
    {
        var outline = inflate > 0 ? Inflate(v.Polygon, inflate) : v.Polygon;
        var poly = new float[outline.Length * 3];
        for (int i = 0; i < outline.Length; i++)
        {
            poly[i * 3] = outline[i].X;
            poly[i * 3 + 1] = 0;
            poly[i * 3 + 2] = outline[i].Y;
        }
        RcAreas.MarkConvexPolyArea(ctx, poly, v.YMin, v.YMax, new RcAreaModification(area), chf);
    }

    /// <summary>A convex counter-clockwise polygon with every edge moved outward by <paramref name="d"/> (corners mitred). A thin door leaf must paint a closed band of cells, or a path slips diagonally between them.</summary>
    static Vector2[] Inflate(Vector2[] p, float d)
    {
        int n = p.Length;
        var result = new Vector2[n];
        float area = 0;
        for (int i = 0; i < n; i++) area += p[i].X * p[(i + 1) % n].Y - p[(i + 1) % n].X * p[i].Y;
        float sign = area >= 0 ? 1 : -1;
        for (int i = 0; i < n; i++)
        {
            var a = p[(i + n - 1) % n]; var b = p[i]; var c = p[(i + 1) % n];
            var e0 = Vector2.Normalize(b - a); var e1 = Vector2.Normalize(c - b);
            var n0 = new Vector2(e0.Y, -e0.X) * sign; var n1 = new Vector2(e1.Y, -e1.X) * sign;
            var m = n0 + n1;
            float k = 1 + Vector2.Dot(n0, n1);
            result[i] = k < 1e-3f ? b + n0 * d : b + m * (d / k);
        }
        return result;
    }

    /// <summary>Gives every door polygon the building whose door painter covers it, so a door can be opened and closed at run time (<see cref="NavDoors"/>).</summary>
    static void AssignDoors(ZoneNavMesh mesh, ZoneGeometry g, NavBuildSettings s)
    {
        var owners = g.Painters.Where(p => p.Owner is not null).ToList();
        if (owners.Count == 0) return;
        var ids = owners.Select(p => p.Owner!).Distinct().ToList();
        var doorOf = new int[mesh.PolygonCount];
        Array.Fill(doorOf, -1);
        float tolerance = s.CellSize * (s.DoorInflateCells + 1);
        bool any = false;
        for (int p = 0; p < mesh.PolygonCount; p++)
        {
            if (mesh.Areas[p] != NavArea.Door) continue;
            var centre = Vector3.Zero;
            foreach (int i in mesh.Polygons[p]) centre += mesh.Vertices[i];
            centre /= mesh.Polygons[p].Length;
            float best = float.MaxValue;
            foreach (var painter in owners)
            {
                if (centre.Y < painter.YMin - 5 || centre.Y > painter.YMax + 5) continue;
                float d = DistanceOutside(painter.Polygon, new Vector2(centre.X, centre.Z));
                if (d > tolerance || d >= best) continue;
                best = d;
                doorOf[p] = ids.IndexOf(painter.Owner!);
                any = true;
            }
        }
        if (!any) return;
        mesh.DoorIds = [.. ids];
        mesh.DoorOf = doorOf;
    }

    /// <summary>How far a point is outside a convex polygon (0 inside), either winding.</summary>
    static float DistanceOutside(Vector2[] polygon, Vector2 point)
    {
        float area = 0;
        for (int i = 0; i < polygon.Length; i++) area += polygon[i].X * polygon[(i + 1) % polygon.Length].Y - polygon[(i + 1) % polygon.Length].X * polygon[i].Y;
        float sign = area >= 0 ? 1 : -1;
        float outside = 0;
        for (int i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length];
            var e = b - a;
            float len = e.Length();
            if (len < 1e-6f) continue;
            float d = -sign * (e.X * (point.Y - a.Y) - e.Y * (point.X - a.X)) / len;
            outside = Math.Max(outside, d);
        }
        return outside;
    }

    // ---- stitching ----

    sealed class BorderEdge
    {
        public int Polygon, Edge;
        public Vector3 A, B;
    }

    static ZoneNavMesh Stitch(ZoneGeometry g, NavBuildSettings s, RcPolyMesh?[] meshes, int tilesPerSide, float tileWorld)
    {
        var vertices = new List<Vector3>();
        var polygons = new List<int[]>();
        var neighbours = new List<int[]>();
        var areas = new List<byte>();
        var links = new List<int>?[meshes.Sum(m => m?.npolys ?? 0)];
        // Border edges per tile and side (0: -X, 1: +Z, 2: +X, 3: -Z, as Recast numbers them).
        var borders = new List<BorderEdge>[meshes.Length, 4];

        for (int tile = 0; tile < meshes.Length; tile++)
        {
            var m = meshes[tile];
            if (m is null) continue;
            int nvp = m.nvp;
            int vBase = vertices.Count, pBase = polygons.Count;
            for (int i = 0; i < m.nverts; i++)
                // A span's top is the surface rounded up to a cell: take the middle of the cell it fell in.
                vertices.Add(new Vector3(m.bmin.X + m.verts[i * 3] * m.cs, m.bmin.Y + (m.verts[i * 3 + 1] - 0.5f) * m.ch, m.bmin.Z + m.verts[i * 3 + 2] * m.cs));
            for (int p = 0; p < m.npolys; p++)
            {
                int count = 0;
                while (count < nvp && m.polys[p * nvp * 2 + count] != RcRecast.RC_MESH_NULL_IDX) count++;
                var vi = new int[count];
                var nb = new int[count];
                for (int k = 0; k < count; k++)
                {
                    vi[k] = vBase + m.polys[p * nvp * 2 + k];
                    int n = m.polys[p * nvp * 2 + nvp + k];
                    if (n == RcRecast.RC_MESH_NULL_IDX)
                    {
                        nb[k] = -1;
                        int ia = m.polys[p * nvp * 2 + k], ib = m.polys[p * nvp * 2 + (k + 1 == count ? 0 : k + 1)];
                        int xa = m.verts[ia * 3], za = m.verts[ia * 3 + 2], xb = m.verts[ib * 3], zb = m.verts[ib * 3 + 2];
                        int side = xa == 0 && xb == 0 ? 0 : za == s.TileCells && zb == s.TileCells ? 1 : xa == s.TileCells && xb == s.TileCells ? 2 : za == 0 && zb == 0 ? 3 : -1;
                        if (side >= 0)
                        {
                            borders[tile, side] ??= [];
                            borders[tile, side]!.Add(new BorderEdge { Polygon = pBase + p, Edge = k });
                        }
                    }
                    else if ((n & 0x8000) != 0)
                    {
                        nb[k] = -1;
                        int side = n & 0xf;
                        borders[tile, side] ??= [];
                        borders[tile, side].Add(new BorderEdge { Polygon = pBase + p, Edge = k });
                    }
                    else nb[k] = pBase + n;
                }
                polygons.Add(vi);
                neighbours.Add(nb);
                areas.Add((byte)m.areas[p]);
            }
        }

        // Recast winds polygons clockwise seen from above (+Y toward the viewer, Z down the page); our tests want the other way.
        bool flip = false;
        if (polygons.Count > 0)
        {
            double area2 = 0;
            var first = polygons.First(p => p.Length >= 3);
            for (int k = 0; k < first.Length; k++)
            {
                var a = vertices[first[k]];
                var b = vertices[first[(k + 1) % first.Length]];
                area2 += (b.X - a.X) * (b.Z + a.Z);
            }
            // Positive sum of (x2 - x1)(z2 + z1) means clockwise in an (X right, Z up) plane.
            flip = area2 > 0;
        }
        if (flip)
            for (int p = 0; p < polygons.Count; p++)
            {
                var vi = polygons[p];
                var nb = neighbours[p];
                int n = vi.Length;
                var rv = new int[n];
                var rn = new int[n];
                // Reverse the vertex order; edge k (k to k+1) becomes edge n-2-k... rebuilt from the old edges.
                for (int k = 0; k < n; k++) rv[k] = vi[n - 1 - k];
                for (int k = 0; k < n; k++) rn[k] = nb[(n - 2 - k + n) % n];
                polygons[p] = rv;
                neighbours[p] = rn;
            }
        // Border edge indices follow the same reversal.
        for (int tile = 0; tile < meshes.Length; tile++)
            for (int side = 0; side < 4; side++)
                if (borders[tile, side] is { } list && flip)
                    foreach (var be in list)
                        be.Edge = (polygons[be.Polygon].Length - 2 - be.Edge + polygons[be.Polygon].Length) % polygons[be.Polygon].Length;
        foreach (var list in borders)
            if (list is not null)
                foreach (var be in list)
                {
                    var p = polygons[be.Polygon];
                    be.A = vertices[p[be.Edge]];
                    be.B = vertices[p[(be.Edge + 1) % p.Length]];
                }

        // Link facing border edges of neighbouring tiles where they overlap along the shared line.
        float climb = s.MaxClimb + 0.5f;
        for (int tz = 0; tz < tilesPerSide; tz++)
            for (int tx = 0; tx < tilesPerSide; tx++)
            {
                int tile = tz * tilesPerSide + tx;
                if (tx + 1 < tilesPerSide) Link(borders[tile, 2], borders[tile + 1, 0], alongX: false);
                if (tz + 1 < tilesPerSide) Link(borders[tile, 1], borders[tile + tilesPerSide, 3], alongX: true);
            }

        void Link(List<BorderEdge>? left, List<BorderEdge>? right, bool alongX)
        {
            if (left is null || right is null) return;
            foreach (var a in left)
            {
                var (a0, a1, ay0, ay1) = Span(a, alongX);
                foreach (var b in right)
                {
                    var (b0, b1, by0, by1) = Span(b, alongX);
                    float lo = Math.Max(a0, b0), hi = Math.Min(a1, b1);
                    if (hi - lo < 0.01f) continue;
                    float ya = Lerp(a0, a1, ay0, ay1, (lo + hi) / 2), yb = Lerp(b0, b1, by0, by1, (lo + hi) / 2);
                    if (Math.Abs(ya - yb) > climb) continue;
                    if (areas[a.Polygon] == NavArea.Null || areas[b.Polygon] == NavArea.Null) continue;
                    // An edge can face several polygons: links, with the portal recomputed from the geometry at query time.
                    (links[a.Polygon] ??= []).Add(a.Edge); links[a.Polygon]!.Add(b.Polygon);
                    (links[b.Polygon] ??= []).Add(b.Edge); links[b.Polygon]!.Add(a.Polygon);
                }
            }
        }

        static (float, float, float, float) Span(BorderEdge e, bool alongX) =>
            alongX
                ? (Math.Min(e.A.X, e.B.X), Math.Max(e.A.X, e.B.X), e.A.X <= e.B.X ? e.A.Y : e.B.Y, e.A.X <= e.B.X ? e.B.Y : e.A.Y)
                : (Math.Min(e.A.Z, e.B.Z), Math.Max(e.A.Z, e.B.Z), e.A.Z <= e.B.Z ? e.A.Y : e.B.Y, e.A.Z <= e.B.Z ? e.B.Y : e.A.Y);

        static float Lerp(float a0, float a1, float y0, float y1, float at) => a1 - a0 < 1e-5f ? y0 : y0 + (y1 - y0) * (at - a0) / (a1 - a0);

        return new ZoneNavMesh
        {
            ZoneX = g.Zone.X,
            ZoneZ = g.Zone.Y,
            Vertices = [.. vertices],
            Polygons = [.. polygons],
            Neighbours = [.. neighbours],
            Links = links.Select(l => l?.ToArray()).ToArray(),
            Areas = [.. areas],
            Kept = Enumerable.Repeat(true, polygons.Count).ToArray(),
            BoundsMin = g.ZoneMin,
            BoundsMax = g.ZoneMax,
        };
    }
}
