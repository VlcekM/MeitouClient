using System.Diagnostics;
using System.Numerics;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Recast;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Builds one tile of a zone's navmesh with Recast (DotRecast): rasterise the tile's triangles with their areas, cut carvers and solids, paint
/// doors, partition, contour and triangulate, and sample each polygon's heights (docs/game/pathfinding.md, "Own builder").
/// </summary>
internal static class NavTileBuilder
{
    /// <summary>
    /// Stopwatch ticks the tiles of one build spent per phase; the tiles add to it from several threads.
    /// </summary>
    internal sealed class TileTicks
    {
        /// <summary>
        /// Ticks per phase.
        /// </summary>
        public long Raster, Compact, Regions, Contours, Mesh, Detail;
    }

    /// <summary>
    /// Cells of the neighbouring tiles rasterised round each tile (Recast's borderSize; engine choice: its usual walkable radius + 3, radius 0 here).
    /// </summary>
    internal const int TileBorderCells = 3;

    /// <summary>
    /// Height cells a tile may span (engine choice, far over any tile's span and under DotRecast's 2^20): past it the cells grow, and the climb and
    /// agent height counted in cells of <see cref="NavBuildSettings.CellHeight"/> no longer match them.
    /// </summary>
    const float MaxHeightCells = 60000;

    /// <summary>
    /// The tile's mesh (null when it has none) from the triangles <paramref name="tris"/> and the volumes that touch it, and its polygons' height
    /// samples in <paramref name="detail"/>.
    /// </summary>
    internal static RcPolyMesh? BuildTile(ZoneGeometry g, NavBuildSettings s, float[] verts, int[] indices, int[] areas, List<int> tris,
        List<int> carvers, List<int> painters, List<int> solids, List<int> meshSolids, TileTicks ticks, float x0, float z0, float tileWorld,
        out HeightGrid?[]? detail)
    {
        detail = null;
        var ctx = new RcContext();
        long t0 = Stopwatch.GetTimestamp();
        int walkableHeight = (int)MathF.Ceiling(s.AgentHeight / s.CellHeight);
        int walkableClimb = (int)MathF.Floor(s.MaxClimb / s.CellHeight);
        var hf = Heightfield(ctx, s, verts, indices, areas, tris, x0, z0, tileWorld, walkableHeight, walkableClimb);
        long t1 = Stopwatch.GetTimestamp();

        // Recast connects two neighbouring spans only when the free space they share (above the higher floor, below the lower ceiling) is the agent
        // height, so a ramp climbing out from under a floor slab, a leaning wall or a roof edge loses a line of cells where its rise per cell eats
        // the margin, although every cell has the headroom on its own (the filter in Heightfield). The original connects faces by step height alone
        // and asks the clearance per face, so the shared space is asked the agent height less the climb (docs/game/pathfinding.md, "Own builder",
        // stairs under floors).
        var chf = RcCompacts.BuildCompactHeightfield(ctx, Math.Max(1, walkableHeight - walkableClimb), walkableClimb, hf);

        CutVolumes(ctx, g, s, chf, carvers, painters, solids, meshSolids);
        var steps = NavSideSteps.Find(chf, g.Ground);

        long t2 = Stopwatch.GetTimestamp();
        var result = Partition(ctx, chf, s, ticks, steps);
        long t5 = Stopwatch.GetTimestamp();
        detail = result is null ? null : NavPolygonHeights.Sample(result, chf, TileBorderCells);
        long t6 = Stopwatch.GetTimestamp();

        Interlocked.Add(ref ticks.Raster, t1 - t0);
        Interlocked.Add(ref ticks.Compact, t2 - t1);
        Interlocked.Add(ref ticks.Detail, t6 - t5);
        return result;
    }

    /// <summary>
    /// The tile's heightfield over the tile and its border, its spans filtered for low obstacles and headroom.
    /// </summary>
    static RcHeightfield Heightfield(RcContext ctx, NavBuildSettings s, float[] verts, int[] indices, int[] areas, List<int> tris, float x0,
        float z0, float tileWorld, int walkableHeight, int walkableClimb)
    {
        // Tile bounds in Y from the triangles that touch it.
        float yMin = float.MaxValue;
        float yMax = float.MinValue;
        foreach (int t in tris)
        {
            for (int k = 0; k < 3; k++)
            {
                float y = verts[indices[t * 3 + k] * 3 + 1];
                yMin = Math.Min(yMin, y);
                yMax = Math.Max(yMax, y);
            }
        }

        float ch = s.CellHeight;
        if ((yMax - yMin) / ch > MaxHeightCells) ch = (yMax - yMin) / MaxHeightCells;

        // A border of cells from the neighbouring tiles: Recast then keeps the contour vertices on the tile's edge and marks those edges as portals,
        // so a narrow strip along the edge is not simplified away from it (docs/game/pathfinding.md, "Own builder", tile seams).
        int border = TileBorderCells;
        float pad = border * s.CellSize;
        var bmin = new RcVec3f(x0 - pad, yMin - 1, z0 - pad);
        var bmax = new RcVec3f(x0 + tileWorld + pad, yMax + 1, z0 + tileWorld + pad);
        int cells = s.TileCells + 2 * border;
        RcHeightfield Field() => new(cells, cells, bmin, bmax, s.CellSize, ch, border);
        var hf = NavTileRaster.Rasterize(ctx, verts, indices, areas, tris, Field, walkableClimb);

        // Recast walks the top of a cutting span within the climb over a walkable one; the original has no face there. Only a top under the climb is
        // lifted: one cell at it was the steep riser beside a roof cap, a stair of riser cells up to the cap (docs/game/pathfinding.md, "Own
        // builder", low obstacles).
        RcFilters.FilterLowHangingWalkableObstacles(ctx, walkableClimb - 1, hf);
        RcFilters.FilterWalkableLowHeightSpans(ctx, walkableHeight, hf);
        return hf;
    }

    /// <summary>
    /// Cuts the carvers, the solids, the mesh solids and the ground under the terrain out of the tile's spans, then paints its doors.
    /// </summary>
    static void CutVolumes(RcContext ctx, ZoneGeometry g, NavBuildSettings s, RcCompactHeightfield chf, List<int> carvers, List<int> painters,
        List<int> solids, List<int> meshSolids)
    {
        foreach (int c in carvers)
            Mark(ctx, g.Carvers[c], NavArea.Null, chf);

        foreach (int c in solids)
            NavSolidCarving.RemoveInside(g.Solids[c], chf);

        foreach (int c in meshSolids)
            NavSolidCarving.RemoveInside(g.MeshSolids[c], chf, g.Solids, solids);

        if (g.Ground is { } ground) NavSolidCarving.RemoveUnderground(ground, s.AgentHeight, chf);

        foreach (int c in painters)
            Mark(ctx, g.Painters[c], NavArea.Door, chf, s.CellSize * s.DoorInflateCells);
    }

    /// <summary>
    /// The tile's polygons over watershed regions: chunky polygons, where monotone partitioning cuts open ground into long strips (a fifth more
    /// polygons for a search to expand; docs/game/pathfinding.md "Search cost"). DotRecast refuses some watershed contours that Recast only warns
    /// about (a region winding round a hole with two outlines, an outline simplified into a bad one): such a tile is partitioned monotone.
    /// </summary>
    static RcPolyMesh? Partition(RcContext ctx, RcCompactHeightfield chf, NavBuildSettings s, TileTicks ticks, List<NavSideSteps.StepLink> steps)
    {
        if (s.Watershed && TryPartition(ctx, chf, s, ticks, steps, watershed: true, out var mesh)) return mesh;

        TryPartition(ctx, chf, s, ticks, steps, watershed: false, out mesh);
        return mesh;
    }

    /// <summary>
    /// Builds the regions (watershed or monotone), the contours and the polygons; false when the watershed contours were refused.
    /// </summary>
    static bool TryPartition(RcContext ctx, RcCompactHeightfield chf, NavBuildSettings s, TileTicks ticks, List<NavSideSteps.StepLink> steps,
        bool watershed, out RcPolyMesh? mesh)
    {
        mesh = null;
        long t0 = Stopwatch.GetTimestamp();
        NavSideSteps.Cut(chf, steps);
        if (watershed)
        {
            RcRegions.BuildDistanceField(ctx, chf);
            RcRegions.BuildRegions(ctx, chf, s.MinRegionArea, s.MergeRegionArea);
        }
        else
        {
            RcRegions.BuildRegionsMonotone(ctx, chf, s.MinRegionArea, s.MergeRegionArea);
        }

        NavSideSteps.Relink(chf, steps);
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

        // A contour takes the corners its neighbours kept on the boundary they share, or the polygons leave a thin hole between them.
        NavContourJoints.Add(contours);

        long t2 = Stopwatch.GetTimestamp();
        mesh = contours.conts.Count == 0 ? null : RcMeshs.BuildPolyMesh(ctx, contours, ZoneNavMesh.MaxVerticesPerPolygon);
        Interlocked.Add(ref ticks.Contours, t2 - t1);
        Interlocked.Add(ref ticks.Mesh, Stopwatch.GetTimestamp() - t2);
        return true;
    }

    /// <summary>
    /// Gives the spans inside a volume (its outline grown by <paramref name="inflate"/>) the area <paramref name="area"/>.
    /// </summary>
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

    /// <summary>
    /// A convex counter-clockwise polygon with every edge moved outward by <paramref name="d"/> (corners mitred). A thin door leaf must paint a
    /// closed band of cells, or a path slips diagonally between them.
    /// </summary>
    static Vector2[] Inflate(Vector2[] p, float d)
    {
        int n = p.Length;
        var result = new Vector2[n];
        float sign = NavGeometry.SignedArea(p) >= 0 ? 1 : -1;
        for (int i = 0; i < n; i++)
        {
            var a = p[(i + n - 1) % n];
            var b = p[i];
            var c = p[(i + 1) % n];
            var e0 = Vector2.Normalize(b - a);
            var e1 = Vector2.Normalize(c - b);
            var n0 = new Vector2(e0.Y, -e0.X) * sign;
            var n1 = new Vector2(e1.Y, -e1.X) * sign;
            var m = n0 + n1;
            float k = 1 + Vector2.Dot(n0, n1);
            result[i] = k < 1e-3f ? b + n0 * d : b + m * (d / k);
        }

        return result;
    }
}
