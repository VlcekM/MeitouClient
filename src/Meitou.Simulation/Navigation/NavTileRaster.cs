using DotRecast.Core;
using DotRecast.Recast;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Rasterizes a tile's triangles into its heightfield, the walkable and the cutting ones apart and merged column by column.
/// </summary>
internal static class NavTileRaster
{
    /// <summary>
    /// The tile's heightfield: the walkable and the cutting triangles are rasterized into two fields and merged column by column, a merged span
    /// walkable at its walkable top when that reaches within the climb of its top. Recast's own merge (rcAddSpan) first raises a new span to the top
    /// of an existing one it overlaps and then compares the tops, so a floor rasterized into a taller cutting span walks that span's top: the cells
    /// along a steep wall rising from a floor to a walkable roof slope became a walkable stair (docs/game/pathfinding.md, "Own builder",
    /// rasterization).
    /// </summary>
    internal static RcHeightfield Rasterize(RcContext ctx, float[] verts, int[] indices, int[] areas, List<int> tris, Func<RcHeightfield> field,
        int climb)
    {
        var walk = field();
        var cut = field();
        RasterizeWhere(ctx, verts, indices, areas, tris, walk, climb, walkable: true);
        RasterizeWhere(ctx, verts, indices, areas, tris, cut, climb, walkable: false);
        for (int c = 0; c < walk.spans.Length; c++)
            walk.spans[c] = MergeColumn(walk.spans[c], cut.spans[c], climb);

        return walk;
    }

    /// <summary>
    /// Rasterizes the triangles of <paramref name="tris"/> that are walkable (<paramref name="walkable"/> set) or cutting (area
    /// <see cref="NavArea.Null"/>) into <paramref name="hf"/>, merging spans within <paramref name="climb"/>.
    /// </summary>
    static void RasterizeWhere(
        RcContext ctx,
        float[] verts,
        int[] indices,
        int[] areas,
        List<int> tris,
        RcHeightfield hf,
        int climb,
        bool walkable)
    {
        int count = tris.Count(t => (areas[t] != NavArea.Null) == walkable);
        var tileTris = new int[count * 3];
        var tileAreas = new int[count];
        int i = 0;
        foreach (int t in tris)
        {
            if ((areas[t] != NavArea.Null) != walkable) continue;

            tileTris[i * 3] = indices[t * 3];
            tileTris[i * 3 + 1] = indices[t * 3 + 1];
            tileTris[i * 3 + 2] = indices[t * 3 + 2];
            tileAreas[i++] = areas[t];
        }

        RcRasterizations.RasterizeTriangles(ctx, verts, tileTris, tileAreas, count, hf, climb);
    }

    /// <summary>
    /// One column's spans of both fields (each sorted and apart) merged where they touch, as Recast merges them; a merged span takes the highest
    /// walkable area whose top is within <paramref name="climb"/> of its own top, else none, and a walkable one stands at that walkable top, not on
    /// the cutting part over it: the original cuts a face under cutting geometry rather than lifting it, so a steep collar rising from a roof is no
    /// stair of cutting tops onto the dome it carries (docs/game/pathfinding.md, "Own builder", rasterization).
    /// </summary>
    static RcSpan? MergeColumn(RcSpan? walk, RcSpan? cut, int climb)
    {
        RcSpan? head = null, tail = null;
        while (walk is not null || cut is not null)
        {
            RcSpan? span = null;
            int walkTop = int.MinValue;
            int walkArea = NavArea.Null;
            while (true)
            {
                bool fromWalk = walk is not null && (cut is null || walk.smin <= cut.smin);
                var next = fromWalk ? walk : cut;
                if (next is null || span is not null && next.smin > span.smax) break;

                if (fromWalk)
                {
                    walk = walk!.next;
                    walkTop = Math.Max(walkTop, next.smax);
                    walkArea = Math.Max(walkArea, next.area);
                }
                else
                {
                    cut = cut!.next;
                }

                if (span is null)
                {
                    span = next;
                }
                else
                {
                    span.smin = Math.Min(span.smin, next.smin);
                    span.smax = Math.Max(span.smax, next.smax);
                }
            }

            span!.next = null;
            span.area = walkTop >= span.smax - climb ? walkArea : NavArea.Null;
            if (span.area != NavArea.Null) span.smax = walkTop;

            if (tail is null)
                head = span;
            else
                tail.next = span;

            tail = span;
        }

        return head;
    }
}
