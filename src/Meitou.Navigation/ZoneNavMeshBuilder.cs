using System.Diagnostics;
using System.Numerics;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Recast;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>
/// Builds a zone's navmesh from its gathered geometry with Recast (DotRecast): per tile rasterise the triangles with their areas, cut carvers,
/// paint doors, partition, contour and triangulate; then stitch the tiles, and keep only the regions near a seed
/// (docs/game/pathfinding.md, Generator "Seeds" and `regionPruningSettings`). Radius 0: clearance is a query-time check.
/// </summary>
internal static partial class ZoneNavMeshBuilder
{
    /// <summary>Cells of the neighbouring tiles rasterised round each tile (Recast's borderSize; engine choice: its usual walkable radius + 3, radius 0 here).</summary>
    const int TileBorderCells = 3;

    public static ZoneNavMesh Build(ZoneGeometry g, NavBuildSettings s, out NavBuildTimes times)
    {
        var total = Stopwatch.StartNew();
        times = new NavBuildTimes();
        float zoneSize = g.ZoneMax.X - g.ZoneMin.X;
        float pad = TileBorderCells * s.CellSize;
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
        var solids = new List<int>[tilesPerSide * tilesPerSide];
        var meshSolids = new List<int>[tilesPerSide * tilesPerSide];
        for (int i = 0; i < triBuckets.Length; i++) { triBuckets[i] = []; carvers[i] = []; painters[i] = []; solids[i] = []; meshSolids[i] = []; }
        void Tiles(float x0, float z0, float x1, float z1, List<int>[] buckets, int id)
        {
            // A tile also takes what lies in its border (BuildTile).
            x0 -= pad; z0 -= pad; x1 += pad; z1 += pad;
            int tx0 = Math.Clamp((int)MathF.Floor((x0 - g.ZoneMin.X) / tileWorld), 0, tilesPerSide - 1), tx1 = Math.Clamp((int)MathF.Floor((x1 - g.ZoneMin.X) / tileWorld), 0, tilesPerSide - 1);
            int tz0 = Math.Clamp((int)MathF.Floor((z0 - g.ZoneMin.Y) / tileWorld), 0, tilesPerSide - 1), tz1 = Math.Clamp((int)MathF.Floor((z1 - g.ZoneMin.Y) / tileWorld), 0, tilesPerSide - 1);
            if (x1 < g.ZoneMin.X || x0 > g.ZoneMax.X || z1 < g.ZoneMin.Y || z0 > g.ZoneMax.Y) return;
            for (int tz = tz0; tz <= tz1; tz++)
                for (int tx = tx0; tx <= tx1; tx++) buckets[tz * tilesPerSide + tx].Add(id);
        }
        for (int t = 0; t < indices.Length; t += 3)
        {
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            for (int k = 0; k < 3; k++)
            {
                float x = verts[indices[t + k] * 3], z = verts[indices[t + k] * 3 + 2];
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); z0 = Math.Min(z0, z); z1 = Math.Max(z1, z);
            }
            Tiles(x0, z0, x1, z1, triBuckets, t / 3);
        }
        for (int c = 0; c < g.Carvers.Count; c++)
        {
            var (a, b) = Bounds(g.Carvers[c]);
            Tiles(a.X, a.Y, b.X, b.Y, carvers, c);
        }
        for (int c = 0; c < g.Painters.Count; c++)
        {
            var (a, b) = Bounds(g.Painters[c]);
            Tiles(a.X, a.Y, b.X, b.Y, painters, c);
        }
        for (int c = 0; c < g.Solids.Count; c++) Tiles(g.Solids[c].Min.X, g.Solids[c].Min.Y, g.Solids[c].Max.X, g.Solids[c].Max.Y, solids, c);
        for (int c = 0; c < g.MeshSolids.Count; c++) Tiles(g.MeshSolids[c].Min.X, g.MeshSolids[c].Min.Y, g.MeshSolids[c].Max.X, g.MeshSolids[c].Max.Y, meshSolids, c);

        var ticks = new TileTicks();
        var meshes = new RcPolyMesh?[tilesPerSide * tilesPerSide];
        var details = new HeightGrid?[]?[meshes.Length];
        var workers = s.Threads > 0 ? s.Threads : Math.Max(1, Environment.ProcessorCount - 1);
        var watch = Stopwatch.StartNew();
        Parallel.For(0, meshes.Length, new ParallelOptions { MaxDegreeOfParallelism = workers }, tile =>
        {
            if (triBuckets[tile].Count == 0) return;
            int tx = tile % tilesPerSide, tz = tile / tilesPerSide;
            meshes[tile] = BuildTile(g, s, verts, indices, areas, triBuckets[tile], carvers[tile], painters[tile], solids[tile], meshSolids[tile], ticks,
                g.ZoneMin.X + tx * tileWorld, g.ZoneMin.Y + tz * tileWorld, tileWorld, out details[tile]);
        });
        times.Tiles = watch.Elapsed.TotalMilliseconds;
        times.TileCount = meshes.Count(m => m is not null);
        double ms = 1000.0 / Stopwatch.Frequency;
        times.CpuRaster = ticks.Raster * ms; times.CpuCompact = ticks.Compact * ms; times.CpuRegions = ticks.Regions * ms; times.CpuContours = ticks.Contours * ms; times.CpuMesh = ticks.Mesh * ms; times.CpuDetail = ticks.Detail * ms;

        watch.Restart();
        var mesh = Stitch(g, s, meshes, tilesPerSide, tileWorld, details);
        AddDetail(mesh, meshes, details, g.Ground);
        AssignDoors(mesh, g, s);
        times.Stitch = watch.Elapsed.TotalMilliseconds;

        watch.Restart();
        SeedPruner.Prune(mesh, g, s);
        // A ruin's floor outside its destroyed boundary lies under walls without collision: dropped, so no border closure or hull join keeps it again.
        if (g.DestroyedOutline is { } outline)
        {
            for (int p = 0; p < mesh.PolygonCount; p++)
                if (mesh.Kept[p]) { var c = mesh.Centre(p); mesh.Kept[p] = NavGeometry.InsideOutline(outline, new Vector2(c.X, c.Z)); }
            mesh = mesh.WithoutPruned();
        }
        times.Prune = watch.Elapsed.TotalMilliseconds;
        times.Total = total.Elapsed.TotalMilliseconds;
        times.Polygons = mesh.PolygonCount;
        times.KeptPolygons = mesh.KeptCount;
        times.Vertices = mesh.Vertices.Length;
        return mesh;
    }

    /// <summary>Stopwatch ticks the tiles of one build spent per phase; the tiles add to it from several threads.</summary>
    sealed class TileTicks
    {
        public long Raster, Compact, Regions, Contours, Mesh, Detail;
    }

    static (Vector2 Min, Vector2 Max) Bounds(NavVolume v)
    {
        var lo = new Vector2(float.MaxValue);
        var hi = new Vector2(float.MinValue);
        foreach (var p in v.Polygon) { lo = Vector2.Min(lo, p); hi = Vector2.Max(hi, p); }
        return (lo, hi);
    }

    /// <summary>
    /// Height cells a tile may span (engine choice, far over any tile's span and under DotRecast's 2^20): past it the cells grow, and the climb and agent
    /// height counted in cells of <see cref="NavBuildSettings.CellHeight"/> no longer match them.
    /// </summary>
    const float MaxHeightCells = 60000;

    static RcPolyMesh? BuildTile(ZoneGeometry g, NavBuildSettings s, float[] verts, int[] indices, int[] areas, List<int> tris, List<int> carvers, List<int> painters, List<int> solids, List<int> meshSolids, TileTicks ticks,
        float x0, float z0, float tileWorld, out HeightGrid?[]? detail)
    {
        detail = null;
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
        if ((yMax - yMin) / ch > MaxHeightCells) ch = (yMax - yMin) / MaxHeightCells;
        // A border of cells from the neighbouring tiles: Recast then keeps the contour vertices on the tile's edge and marks those edges as portals, so a
        // narrow strip along the edge is not simplified away from it (docs/game/pathfinding.md, "Own builder", tile seams).
        int border = TileBorderCells;
        float pad = border * s.CellSize;
        var bmin = new RcVec3f(x0 - pad, yMin - 1, z0 - pad);
        var bmax = new RcVec3f(x0 + tileWorld + pad, yMax + 1, z0 + tileWorld + pad);
        int cells = s.TileCells + 2 * border;
        RcHeightfield Field() => new(cells, cells, bmin, bmax, s.CellSize, ch, border);
        var hf = Rasterize(ctx, verts, indices, areas, tris, Field, walkableClimb);
        // Recast walks the top of a cutting span within the climb over a walkable one; the original has no face there. Only a top under the climb is lifted:
        // one cell at it was the steep riser beside a roof cap, a stair of riser cells up to the cap (docs/game/pathfinding.md, "Own builder", low obstacles).
        RcFilters.FilterLowHangingWalkableObstacles(ctx, walkableClimb - 1, hf);
        RcFilters.FilterWalkableLowHeightSpans(ctx, walkableHeight, hf);
        long t1 = Stopwatch.GetTimestamp();
        // Recast connects two neighbouring spans only when the free space they share (above the higher floor, below the lower ceiling) is the agent height, so a
        // ramp climbing out from under a floor slab, a leaning wall or a roof edge loses a line of cells where its rise per cell eats the margin, although every
        // cell has the headroom on its own (the filter above). The original connects faces by step height alone and asks the clearance per face, so the
        // shared space is asked the agent height less the climb (docs/game/pathfinding.md, "Own builder", stairs under floors).
        var chf = RcCompacts.BuildCompactHeightfield(ctx, Math.Max(1, walkableHeight - walkableClimb), walkableClimb, hf);

        foreach (int c in carvers) Mark(ctx, g.Carvers[c], NavArea.Null, chf);
        foreach (int c in solids) RemoveInside(g.Solids[c], chf);
        foreach (int c in meshSolids) RemoveInside(g.MeshSolids[c], chf, g.Solids, solids);
        if (g.Ground is { } ground) RemoveUnderground(ground, s.AgentHeight, chf);
        foreach (int c in painters) Mark(ctx, g.Painters[c], NavArea.Door, chf, s.CellSize * s.DoorInflateCells);
        var steps = SideSteps(chf, g.Ground);

        long t2 = Stopwatch.GetTimestamp();
        var result = Partition(ctx, chf, s, ticks, steps);
        long t5 = Stopwatch.GetTimestamp();
        detail = result is null ? null : SampleHeights(result, chf, border);
        long t6 = Stopwatch.GetTimestamp();
        Interlocked.Add(ref ticks.Raster, t1 - t0);
        Interlocked.Add(ref ticks.Compact, t2 - t1);
        Interlocked.Add(ref ticks.Detail, t6 - t5);
        return result;
    }

    /// <summary>
    /// The tile's polygons over watershed regions: chunky polygons, where monotone partitioning cuts open ground into long strips (a fifth more polygons for a
    /// search to expand; docs/game/pathfinding.md "Search cost"). DotRecast refuses some watershed contours that Recast only warns about (a region winding
    /// round a hole with two outlines, an outline simplified into a bad one): such a tile is partitioned monotone.
    /// </summary>
    static RcPolyMesh? Partition(RcContext ctx, RcCompactHeightfield chf, NavBuildSettings s, TileTicks ticks, List<StepLink> steps)
    {
        if (s.Watershed && TryPartition(ctx, chf, s, ticks, steps, watershed: true, out var mesh)) return mesh;
        TryPartition(ctx, chf, s, ticks, steps, watershed: false, out mesh);
        return mesh;
    }

    static bool TryPartition(RcContext ctx, RcCompactHeightfield chf, NavBuildSettings s, TileTicks ticks, List<StepLink> steps, bool watershed, out RcPolyMesh? mesh)
    {
        mesh = null;
        long t0 = Stopwatch.GetTimestamp();
        CutSteps(chf, steps);
        if (watershed)
        {
            RcRegions.BuildDistanceField(ctx, chf);
            RcRegions.BuildRegions(ctx, chf, s.MinRegionArea, s.MergeRegionArea);
        }
        else RcRegions.BuildRegionsMonotone(ctx, chf, s.MinRegionArea, s.MergeRegionArea);
        RelinkWithinRegions(chf, steps);
        long t1 = Stopwatch.GetTimestamp();
        Interlocked.Add(ref ticks.Regions, t1 - t0);
        RcContourSet contours;
        try
        {
            contours = RcContours.BuildContours(ctx, chf, s.MaxSimplificationError, s.MaxEdgeLength, 1);
        }
        catch (Exception) when (watershed)
        {
            return false;
        }
        long t2 = Stopwatch.GetTimestamp();
        mesh = contours.conts.Count == 0 ? null : RcMeshs.BuildPolyMesh(ctx, contours, ZoneNavMesh.MaxVerticesPerPolygon);
        Interlocked.Add(ref ticks.Contours, t2 - t1);
        Interlocked.Add(ref ticks.Mesh, Stopwatch.GetTimestamp() - t2);
        return true;
    }

    /// <summary>Cells between two height samples of a polygon (engine choice: 4 units, under a stair tread).</summary>
    const int HeightSampleCells = 2;

    /// <summary>How far (units) a polygon's samples have to leave its plane for them to be kept (engine choice: most polygons are flat ground).</summary>
    const float HeightKeep = 2;

    /// <summary>How far (units) from a polygon's plane a span may be and still be its surface when the polygon has no region (engine choice: under half a storey).</summary>
    const float HeightReach = 25;

    /// <summary>
    /// The heights of each polygon's own surface (docs/game/pathfinding.md, "Heights"): every <see cref="HeightSampleCells"/> cells over its box, the
    /// walkable span of the region it was made from, else the walkable span nearest its plane within <see cref="HeightReach"/>, NaN where there is none;
    /// at the stitched vertices' level (half a cell under the span's top). DotRecast leaves the region 0 on polygons merged across regions: their regions
    /// are those of the spans at their corners (<see cref="CornerRegions"/>), each sample taking the one of theirs nearest the plane, which over a large
    /// polygon on a slope lies far off it. A polygon is planar only between its corners, so over stairs, ramps and humps it lay up to ~40 off the surface.
    /// Recast's own detail mesh gives the same heights at a hundred times the cost.
    /// </summary>
    static HeightGrid?[] SampleHeights(RcPolyMesh m, RcCompactHeightfield chf, int border)
    {
        var grids = new HeightGrid?[m.npolys];
        int nvp = m.nvp;
        float step = HeightSampleCells * chf.cs;
        var corner = new Vector3[nvp];
        var regions = new HashSet<int>();
        for (int p = 0; p < m.npolys; p++)
        {
            int reg = m.regs[p];
            regions.Clear();
            if (reg == 0) CornerRegions(m, chf, border, p, regions);
            else regions.Add(reg);
            int x0 = int.MaxValue, x1 = int.MinValue, z0 = int.MaxValue, z1 = int.MinValue;
            int count = 0;
            for (int k = 0; k < nvp && m.polys[p * nvp * 2 + k] != RcRecast.RC_MESH_NULL_IDX; k++, count++)
            {
                int v = m.polys[p * nvp * 2 + k] * 3;
                x0 = Math.Min(x0, m.verts[v]); x1 = Math.Max(x1, m.verts[v]);
                z0 = Math.Min(z0, m.verts[v + 2]); z1 = Math.Max(z1, m.verts[v + 2]);
                corner[k] = new Vector3(m.verts[v], m.verts[v + 1] - 0.5f, m.verts[v + 2]);
            }
            // The plane's height (in cells) at a cell: the fan triangle the point is in, else the one it is nearest to, extended.
            float Plane(float cx, float cz)
            {
                float best = float.MinValue, y = corner[0].Y;
                for (int k = 1; k + 1 < count; k++)
                {
                    if (!NavGeometry.Barycentric(cx, cz, corner[0], corner[k], corner[k + 1], out float u, out float v, out float w)) continue;
                    float inside = MathF.Min(u, MathF.Min(v, w));
                    if (inside <= best) continue;
                    best = inside;
                    y = u * corner[0].Y + v * corner[k].Y + w * corner[k + 1].Y;
                }
                return y;
            }
            // Cell centres from the box's first cell, one column past its last cell so the far edge lies between samples.
            int columns = (x1 - 1 - x0) / HeightSampleCells + 2, rows = (z1 - 1 - z0) / HeightSampleCells + 2;
            var values = new float[columns * rows];
            var own = new bool[columns * rows];
            SampleCells(chf, border, x0, z0, columns, regions, Plane, values, own);
            FillFromNeighbours(chf, border, x0, z0, columns, values);
            grids[p] = new HeightGrid(chf.bmin.X + (x0 + border + 0.5f) * chf.cs, chf.bmin.Z + (z0 + border + 0.5f) * chf.cs, step, columns, rows, values) { Own = own };
        }
        return grids;
    }

    /// <summary>
    /// Each sample of a polygon's box (<see cref="SampleHeights"/>): the walkable span of its regions nearest its <paramref name="planeAt"/> (in cells), else the one
    /// nearest the plane within <see cref="HeightReach"/>; NaN where there is none.
    /// </summary>
    static void SampleCells(RcCompactHeightfield chf, int border, int x0, int z0, int columns, HashSet<int> regions, Func<float, float, float> planeAt,
        float[] values, bool[] own)
    {
        for (int j = 0, rows = values.Length / columns; j < rows; j++)
            for (int i = 0; i < columns; i++)
            {
                int cx = x0 + i * HeightSampleCells + border, cz = z0 + j * HeightSampleCells + border;
                float y = float.NaN;
                if (cx < chf.width && cz < chf.height)
                {
                    var cell = chf.cells[cx + cz * chf.width];
                    float plane = planeAt(cx - border + 0.5f, cz - border + 0.5f), nearest = HeightReach / chf.ch, nearestOwn = float.MaxValue;
                    for (int s = cell.index, end = cell.index + cell.count; s < end; s++)
                    {
                        if (chf.areas[s] == NavArea.Null) continue;
                        float top = chf.spans[s].y - 0.5f;
                        float off = MathF.Abs(top - plane);
                        if (regions.Contains(chf.spans[s].reg))
                        {
                            if (off >= nearestOwn) continue;
                            (nearestOwn, y) = (off, top);
                            own[j * columns + i] = true;
                        }
                        else if (!own[j * columns + i] && off < nearest) (nearest, y) = (off, top);
                    }
                    if (!float.IsNaN(y)) y = chf.bmin.Y + y * chf.ch;
                }
                values[j * columns + i] = y;
            }
    }

    /// <summary>
    /// Gives the samples <see cref="SampleCells"/> left without a span the walkable span within a climb a cell of a sampled neighbour's height, spreading from
    /// sample to sample until none is added (engine choice). A polygon is one connected surface, so where the ground leaves its plane by more than
    /// <see cref="HeightReach"/> in a region none of its corners touches, its height carries on from its neighbours (Observed zone 20.32 at -50695,805,
    /// docs/game/pathfinding.md "Polygon heights"). Without it the plane, ~40 over the ground, was taken there and walkers could not step onto it.
    /// </summary>
    static void FillFromNeighbours(RcCompactHeightfield chf, int border, int x0, int z0, int columns, float[] values)
    {
        int rows = values.Length / columns;
        float reach = chf.walkableClimb * HeightSampleCells * chf.ch;
        var queue = new Queue<int>();
        for (int k = 0; k < values.Length; k++)
            if (!float.IsNaN(values[k])) queue.Enqueue(k);

        while (queue.TryDequeue(out int k))
        {
            int i = k % columns, j = k / columns;
            Spread(i - 1, j, values[k]);
            Spread(i + 1, j, values[k]);
            Spread(i, j - 1, values[k]);
            Spread(i, j + 1, values[k]);
        }

        void Spread(int i, int j, float from)
        {
            if (i < 0 || j < 0 || i >= columns || j >= rows || !float.IsNaN(values[j * columns + i])) return;
            int cx = x0 + i * HeightSampleCells + border, cz = z0 + j * HeightSampleCells + border;
            if (cx >= chf.width || cz >= chf.height) return;
            var cell = chf.cells[cx + cz * chf.width];
            float y = float.NaN, nearest = reach;
            for (int s = cell.index, end = cell.index + cell.count; s < end; s++)
            {
                if (chf.areas[s] == NavArea.Null) continue;
                float top = chf.bmin.Y + (chf.spans[s].y - 0.5f) * chf.ch;
                if (MathF.Abs(top - from) > nearest) continue;
                (nearest, y) = (MathF.Abs(top - from), top);
            }
            if (float.IsNaN(y)) return;
            values[j * columns + i] = y;
            queue.Enqueue(j * columns + i);
        }
    }

    /// <summary>The regions (not the tile border's) of the walkable spans within the climb of a polygon's corners, in the four cells round each.</summary>
    static void CornerRegions(RcPolyMesh m, RcCompactHeightfield chf, int border, int p, HashSet<int> regions)
    {
        int nvp = m.nvp;
        for (int k = 0; k < nvp && m.polys[p * nvp * 2 + k] != RcRecast.RC_MESH_NULL_IDX; k++)
        {
            int v = m.polys[p * nvp * 2 + k] * 3;
            int vx = m.verts[v] + border, vy = m.verts[v + 1], vz = m.verts[v + 2] + border;
            for (int cz = vz - 1; cz <= vz; cz++)
                for (int cx = vx - 1; cx <= vx; cx++)
                {
                    if (cx < 0 || cz < 0 || cx >= chf.width || cz >= chf.height) continue;
                    var cell = chf.cells[cx + cz * chf.width];
                    for (int s = cell.index, end = cell.index + cell.count; s < end; s++)
                    {
                        int r = chf.spans[s].reg;
                        bool own = r != 0 && (r & RcRecast.RC_BORDER_REG) == 0 && chf.areas[s] != NavArea.Null;
                        if (own && Math.Abs(chf.spans[s].y - vy) <= chf.walkableClimb) regions.Add(r);
                    }
                }
        }
    }

    /// <summary>
    /// Gives the polygons whose surface leaves their plane by more than <see cref="HeightKeep"/> their height samples (<see cref="ZoneNavMesh.Heights"/>). The zone's
    /// polygons are the tiles' in tile order (<see cref="Stitch"/>).
    /// </summary>
    static void AddDetail(ZoneNavMesh mesh, RcPolyMesh?[] meshes, HeightGrid?[]?[] details, HeightWindow? ground)
    {
        var heights = new HeightGrid?[mesh.PolygonCount];
        bool any = false;
        int polygon = 0;
        for (int tile = 0; tile < meshes.Length; tile++)
        {
            if (meshes[tile] is not { } m) continue;
            for (int p = 0; p < m.npolys; p++, polygon++)
            {
                if (details[tile]?[p] is not { } grid) continue;
                grid.Own = null;   // only the stitch reads it
                float off = 0;
                bool onTerrain = ground is not null;
                for (int j = 0; j < grid.Rows; j++)
                    for (int i = 0; i < grid.Columns; i++)
                    {
                        float y = grid.Values[j * grid.Columns + i];
                        float x = grid.X0 + i * grid.Step, z = grid.Z0 + j * grid.Step;
                        if (float.IsNaN(y) || !mesh.Contains(polygon, x, z)) continue;
                        off = MathF.Max(off, MathF.Abs(y - mesh.HeightAt(polygon, x, z)));
                        if (onTerrain && !(ground!.TryHeightAt(x, z, out float t) && MathF.Abs(MathF.Max(t, WorldWater.Height) - y) <= TerrainMatch)) onTerrain = false;
                    }
                if (off <= HeightKeep) continue;
                heights[polygon] = onTerrain ? HeightGrid.OnTerrain : grid;
                any = true;
            }
        }
        if (any) mesh.Heights = heights;
    }

    /// <summary>
    /// How near (units) the heightmap every sample of a polygon has to be for the polygon to stand on the terrain (<see cref="HeightGrid.OnTerrain"/>; engine
    /// choice: the spans come from the terrain's 36-unit triangles, the heightmap's 18-unit samples differ from them by a few units on curved ground).
    /// </summary>
    const float TerrainMatch = 3;

    /// <summary>
    /// How far below an upward face of a solid a span's floor has to be to count as inside (engine choice: one height cell). Recast rounds a span's top up to the
    /// highest point of its surface over the cell, so a span on the solid's own top is on or above that face and stays.
    /// </summary>
    internal const float SolidTopInset = 1;

    /// <summary>Removes the walkable spans whose floor, at the middle of their cell, lies inside the solid (docs/game/pathfinding.md, "Solids").</summary>
    static void RemoveInside(NavSolid solid, RcCompactHeightfield chf)
    {
        float cs = chf.cs, ch = chf.ch;
        int x0 = Math.Max(0, (int)MathF.Floor((solid.Min.X - chf.bmin.X) / cs)), x1 = Math.Min(chf.width - 1, (int)MathF.Floor((solid.Max.X - chf.bmin.X) / cs));
        int z0 = Math.Max(0, (int)MathF.Floor((solid.Min.Y - chf.bmin.Z) / cs)), z1 = Math.Min(chf.height - 1, (int)MathF.Floor((solid.Max.Y - chf.bmin.Z) / cs));
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                var cell = chf.cells[x + z * chf.width];
                for (int i = cell.index, end = cell.index + cell.count; i < end; i++)
                {
                    if (chf.areas[i] == NavArea.Null) continue;
                    var p = new Vector3(chf.bmin.X + (x + 0.5f) * cs, chf.bmin.Y + chf.spans[i].y * ch, chf.bmin.Z + (z + 0.5f) * cs);
                    bool inside = true;
                    foreach (var plane in solid.Planes)
                        if (plane.X * p.X + plane.Y * p.Y + plane.Z * p.Z + plane.W >= (plane.Y > 0 ? -SolidTopInset : 0)) { inside = false; break; }
                    if (inside) chf.areas[i] = NavArea.Null;
                }
            }
    }

    /// <summary>
    /// How far (cells) from where a map feature's surface runs inside a building's solid that surface is removed (engine choice: two cells, so the building's
    /// walkway and the feature's surface are not neighbours; docs/game/pathfinding.md, "Map features").
    /// </summary>
    const int FeatureSeamCells = 2;

    /// <summary>How far (units) a span's floor may be over a feature triangle's height at the cell's middle and still stand on it (engine choice).</summary>
    const float FeatureSurfaceReach = 2;

    /// <summary>The height over a span's floor its enclosure rays run at (engine choice: just over the ground, under a gravel pile's skirt).</summary>
    const float EncloseRayHeight = 1;

    /// <summary>How many of the <see cref="EncloseDirections"/> must hit the mesh for a span to be walled in (engine choice).</summary>
    const int EncloseHits = 4;

    static readonly Vector3[] EncloseDirections = [.. Enumerable.Range(0, 8).Select(k => new Vector3(MathF.Cos(k * MathF.PI / 4), 0, MathF.Sin(k * MathF.PI / 4)))];

    /// <summary>Whether a point is walled in by a triangle mesh: at least <see cref="EncloseHits"/> of eight horizontal rays from it hit one of its triangles.</summary>
    static bool Enclosed(Vector3[] t, Vector3 at)
    {
        int hits = 0;
        for (int d = 0; d < EncloseDirections.Length; d++)
        {
            if (RayHitsMesh(t, at, EncloseDirections[d])) hits++;
            if (hits >= EncloseHits) return true;
            if (hits + EncloseDirections.Length - 1 - d < EncloseHits) return false;
        }
        return false;
    }

    static bool RayHitsMesh(Vector3[] t, Vector3 from, Vector3 dir)
    {
        for (int i = 0; i < t.Length; i += 3)
            if (RayHits(from, dir, t[i], t[i + 1], t[i + 2])) return true;
        return false;
    }

    /// <summary>Whether the ray from <paramref name="o"/> along <paramref name="d"/> crosses the triangle (a, b, c), Moller-Trumbore.</summary>
    static bool RayHits(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 e1 = b - a, e2 = c - a, p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-6f) return false;
        float inv = 1 / det;
        Vector3 s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return false;
        Vector3 q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        return v >= 0 && u + v <= 1 && Vector3.Dot(e2, q) * inv > 0;
    }

    /// <summary>
    /// Removes the walkable spans inside a closed or bottom-open triangle mesh (<see cref="NavMeshSolid"/>): the nearest of its triangles above the span's floor, at
    /// the middle of the cell and more than <see cref="SolidTopInset"/> over it, faces up, and the mesh walls the span in (<see cref="Enclosed"/>; docs/game/pathfinding.md, "Map features").
    /// </summary>
    static void RemoveInside(NavMeshSolid solid, RcCompactHeightfield chf, List<NavSolid> allSolids, List<int> tileSolids)
    {
        float cs = chf.cs, ch = chf.ch;
        int x0 = Math.Max(0, (int)MathF.Floor((solid.Min.X - chf.bmin.X) / cs)), x1 = Math.Min(chf.width - 1, (int)MathF.Floor((solid.Max.X - chf.bmin.X) / cs));
        int z0 = Math.Max(0, (int)MathF.Floor((solid.Min.Y - chf.bmin.Z) / cs)), z1 = Math.Min(chf.height - 1, (int)MathF.Floor((solid.Max.Y - chf.bmin.Z) / cs));
        if (x0 > x1 || z0 > z1) return;
        int w = x1 - x0 + 1;
        // The mesh's crossings of each cell's middle line: height, and whether that triangle faces up.
        var hits = new List<(float Y, bool Up)>?[w * (z1 - z0 + 1)];
        var t = solid.Triangles;
        for (int i = 0; i < t.Length; i += 3)
        {
            Vector3 a = t[i], b = t[i + 1], c = t[i + 2];
            float area = (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
            // Counter-clockwise seen from above (+Y) is a face whose normal points up.
            bool up = area < 0;
            int cx0 = Math.Max(x0, (int)MathF.Ceiling((MathF.Min(a.X, MathF.Min(b.X, c.X)) - chf.bmin.X) / cs - 0.5f));
            int cx1 = Math.Min(x1, (int)MathF.Floor((MathF.Max(a.X, MathF.Max(b.X, c.X)) - chf.bmin.X) / cs - 0.5f));
            int cz0 = Math.Max(z0, (int)MathF.Ceiling((MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) - chf.bmin.Z) / cs - 0.5f));
            int cz1 = Math.Min(z1, (int)MathF.Floor((MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) - chf.bmin.Z) / cs - 0.5f));
            for (int z = cz0; z <= cz1; z++)
                for (int x = cx0; x <= cx1; x++)
                {
                    float px = chf.bmin.X + (x + 0.5f) * cs, pz = chf.bmin.Z + (z + 0.5f) * cs;
                    if (!NavGeometry.Barycentric(px, pz, a, b, c, out float u, out float v, out float wt) || u < 0 || v < 0 || wt < 0) continue;
                    ref var list = ref hits[(z - z0) * w + x - x0];
                    (list ??= []).Add((u * a.Y + v * b.Y + wt * c.Y, up));
                }
        }
        // Where the feature's upward surface runs inside a building's solid (Mourn's tube through the defensive wall), it does not join the building's walkway:
        // its own surface within FeatureSeamCells of there is removed.
        var buried = new bool[hits.Length];
        bool anyBuried = false;
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                if (hits[(z - z0) * w + x - x0] is not { } list) continue;
                float px = chf.bmin.X + (x + 0.5f) * cs, pz = chf.bmin.Z + (z + 0.5f) * cs;
                foreach (int c in tileSolids)
                {
                    var b = allSolids[c];
                    if (b.Feature || px < b.Min.X || px > b.Max.X || pz < b.Min.Y || pz > b.Max.Y) continue;
                    foreach (var (y, faceUp) in list)
                    {
                        if (!faceUp) continue;
                        bool inside = true;
                        foreach (var plane in b.Planes)
                            if (plane.X * px + plane.Y * (y - SolidTopInset) + plane.Z * pz + plane.W >= 0) { inside = false; break; }
                        if (inside) { buried[(z - z0) * w + x - x0] = anyBuried = true; break; }
                    }
                    if (buried[(z - z0) * w + x - x0]) break;
                }
            }
        bool NearBuried(int x, int z)
        {
            if (!anyBuried) return false;
            for (int dz = -FeatureSeamCells; dz <= FeatureSeamCells; dz++)
                for (int dx = -FeatureSeamCells; dx <= FeatureSeamCells; dx++)
                {
                    int bx = x + dx - x0, bz = z + dz - z0;
                    if (bx >= 0 && bx < w && bz >= 0 && bz <= z1 - z0 && buried[bz * w + bx]) return true;
                }
            return false;
        }
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                if (hits[(z - z0) * w + x - x0] is not { } list) continue;
                bool seam = NearBuried(x, z);
                var cell = chf.cells[x + z * chf.width];
                for (int i = cell.index, end = cell.index + cell.count; i < end; i++)
                {
                    if (chf.areas[i] == NavArea.Null) continue;
                    float floor = chf.bmin.Y + chf.spans[i].y * ch, nearest = float.MaxValue;
                    bool up = false, onSurface = false;
                    foreach (var (y, faceUp) in list)
                    {
                        if (y > floor + SolidTopInset && y < nearest) { nearest = y; up = faceUp; }
                        if (faceUp && floor >= y - SolidTopInset && floor - y <= FeatureSurfaceReach) onSurface = true;
                    }
                    // Under a single-sided top (a trestle bridge's deck) the ground stays walkable unless the mesh walls it in.
                    if (up && !Enclosed(solid.Triangles, new Vector3(chf.bmin.X + (x + 0.5f) * cs, floor + EncloseRayHeight, chf.bmin.Z + (z + 0.5f) * cs))) up = false;
                    if (up || seam && onSurface) chf.areas[i] = NavArea.Null;
                }
            }
    }

    /// <summary>
    /// Removes the walkable spans more than <paramref name="depth"/> (the agent's height) under the terrain at the middle of their cell: the top of a building part
    /// buried in the ground, which has headroom under the terrain's surface (Observed: zone 24.27 by Bad Teeth's gate, a face at 120 under ground at 159;
    /// docs/game/pathfinding.md, "Solids"). Only columns with more than one span can hold one; the exterior mesh only.
    /// </summary>
    static void RemoveUnderground(Meitou.Data.World.HeightWindow terrain, float depth, RcCompactHeightfield chf)
    {
        for (int z = 0; z < chf.height; z++)
            for (int x = 0; x < chf.width; x++)
            {
                var cell = chf.cells[x + z * chf.width];
                if (cell.count < 2) continue;
                float ground = float.NaN;
                for (int i = cell.index, end = cell.index + cell.count; i < end; i++)
                {
                    if (chf.areas[i] == NavArea.Null) continue;
                    if (float.IsNaN(ground) && !terrain.TryHeightAt(chf.bmin.X + (x + 0.5f) * chf.cs, chf.bmin.Z + (z + 0.5f) * chf.cs, out ground)) break;
                    if (chf.bmin.Y + chf.spans[i].y * chf.ch < ground - depth) chf.areas[i] = NavArea.Null;
                }
            }
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
}
