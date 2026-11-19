namespace Meitou.Simulation.Navigation;

/// <summary>
/// Rasterizes a tile's triangles into its heightfield's spans, the walkable and the cutting ones apart and merged column by column.
/// </summary>
internal static class NavTileRaster
{
    /// <summary>
    /// The tile's spans over <paramref name="grid"/>: the walkable and the cutting triangles are rasterized into two fields and merged column by
    /// column, a merged span walkable at its walkable top when that reaches within the climb of its top. Recast's own merge (rcAddSpan) first raises
    /// a new span to the top of an existing one it overlaps and then compares the tops, so a floor rasterized into a taller cutting span walks that
    /// span's top: the cells along a steep wall rising from a floor to a walkable roof slope became a walkable stair (docs/game/pathfinding.md,
    /// "Own builder", rasterization).
    /// </summary>
    internal static NavTileSpans Rasterize(in NavTileGrid grid, float[] verts, int[] indices, int[] areas, List<int> tris, int climb)
    {
        int columns = grid.Width * grid.Height;
        using var walk = new NavSpanField(columns);
        using var cut = new NavSpanField(columns);
        RasterizeWhere(grid, verts, indices, areas, tris, walk, climb, walkable: true);
        RasterizeWhere(grid, verts, indices, areas, tris, cut, climb, walkable: false);

        var merged = new NavTileSpans(grid, walk.Count + cut.Count);
        var tops = new List<(int Top, int Area)>();
        for (int c = 0; c < columns; c++)
        {
            MergeColumn(walk, cut, c, climb, tops, merged);
            merged.EndColumn();
        }

        return merged;
    }

    /// <summary>
    /// Rasterizes the triangles of <paramref name="tris"/> that are walkable (<paramref name="walkable"/> set) or cutting (area
    /// <see cref="NavArea.Null"/>) into <paramref name="field"/>, merging spans within <paramref name="climb"/>.
    /// </summary>
    static void RasterizeWhere(
        in NavTileGrid grid,
        float[] verts,
        int[] indices,
        int[] areas,
        List<int> tris,
        NavSpanField field,
        int climb,
        bool walkable)
    {
        foreach (int t in tris)
        {
            if ((areas[t] != NavArea.Null) != walkable) continue;

            NavTriangleRaster.Rasterize(field, grid, verts, indices[t * 3], indices[t * 3 + 1], indices[t * 3 + 2], areas[t], climb);
        }
    }

    /// <summary>
    /// Adds one column's spans of both fields (each sorted and apart) to <paramref name="merged"/>, merged where they touch, as Recast merges
    /// them; a merged span takes the highest walkable area whose top is within <paramref name="climb"/> of its own top, else none, and a walkable
    /// one stands at that walkable top, not on the cutting part over it: the original cuts a face under cutting geometry rather than lifting it,
    /// so a steep collar rising from a roof is no stair of cutting tops onto the dome it carries (docs/game/pathfinding.md, "Own builder",
    /// rasterization). <paramref name="tops"/> is scratch for the walkable spans of one merged span.
    /// </summary>
    static void MergeColumn(NavSpanField walkField, NavSpanField cutField, int column, int climb, List<(int Top, int Area)> tops, NavTileSpans merged)
    {
        int walk = walkField.First(column);
        int cut = cutField.First(column);
        while (walk != NavSpanField.None || cut != NavSpanField.None)
        {
            bool any = false;
            var span = default(NavSpan);
            int walkTop = int.MinValue;
            tops.Clear();
            while (true)
            {
                bool fromWalk = walk != NavSpanField.None && (cut == NavSpanField.None || walkField[walk].Min <= cutField[cut].Min);
                if (!fromWalk && cut == NavSpanField.None) break;

                var next = fromWalk ? walkField[walk] : cutField[cut];
                if (any && next.Min > span.Max) break;

                if (fromWalk)
                {
                    walk = walkField.Next(walk);
                    walkTop = Math.Max(walkTop, next.Max);
                    tops.Add((next.Max, next.Area));
                }
                else
                {
                    cut = cutField.Next(cut);
                }

                span.Min = any ? Math.Min(span.Min, next.Min) : next.Min;
                span.Max = any ? Math.Max(span.Max, next.Max) : next.Max;
                any = true;
            }

            span.Area = walkTop >= span.Max - climb ? TopArea(tops, walkTop, climb) : NavArea.Null;
            if (span.Area != NavArea.Null) span.Max = walkTop;

            merged.Add(span);
        }
    }

    /// <summary>
    /// The area a merged span's walkable top takes: the highest of the walkable spans whose own top is within <paramref name="climb"/> of it, as
    /// Recast's merge flags. A surface lower down in the same merged span gives none: through a deck's steep side to the water under it, the water's
    /// area went onto the deck's edge cells, which split from the deck as regions of their own and left a hole between two decks (Swamp Store
    /// <c>21.42:323-</c>, docs/game/pathfinding.md, "Own builder", rasterization).
    /// </summary>
    static int TopArea(List<(int Top, int Area)> tops, int walkTop, int climb)
    {
        int area = NavArea.Null;
        foreach (var (top, a) in tops)
            if (top >= walkTop - climb) area = Math.Max(area, a);

        return area;
    }
}
