using System.Buffers;
using DotRecast.Core;
using DotRecast.Recast;
using static DotRecast.Recast.RcRecast;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Partitions one compact tile before contouring. Adapted from DotRecast's RcRegions to reuse scratch storage
/// (zlib licence, THIRD_PARTY_NOTICES.md).
/// </summary>
internal static class NavTileRegions
{
    /// <summary>
    /// Rebuilds the readonly compact spans with their final region IDs.
    /// </summary>
    [ThreadStatic]
    static RcCompactSpanBuilder? s_spanBuilder;

    /// <summary>
    /// Paints the tile border, partitions the interior, and stores the filtered regions on the compact spans.
    /// </summary>
    public static void Build(RcContext ctx, RcCompactHeightfield field, int minArea, int mergeArea, bool watershed)
    {
        int count = field.spanCount;
        int[] rented = ArrayPool<int>.Shared.Rent(watershed ? 3 * count : count);
        try
        {
            var regions = rented.AsSpan(0, count);
            regions.Clear();
            int nextId = watershed
                ? PartitionWatershed(field, regions, rented.AsSpan(count, count), rented.AsSpan(2 * count, count))
                : NavMonotoneRegions.Partition(field, regions, PaintBorder(field, regions));

            field.maxRegions = NavRegionMerge.Filter(field, regions, nextId, minArea, mergeArea, out int overlaps);
            if (watershed && overlaps > 0) ctx.Warn("rcBuildRegions: " + overlaps + " overlapping regions.");

            StoreRegions(field, regions);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Index of the connected span in the given direction, or -1 for an unconnected edge.
    /// </summary>
    public static int Neighbour(RcCompactHeightfield field, int x, int y, int index, int direction)
    {
        int layer = GetCon(field.spans[index], direction);
        if (layer == RC_NOT_CONNECTED) return -1;

        int nextX = x + GetDirOffsetX(direction);
        int nextY = y + GetDirOffsetY(direction);
        return field.cells[nextX + nextY * field.width].index + layer;
    }

    /// <summary>
    /// Builds the watershed distances, then reuses the unblurred distance buffer for expansion.
    /// </summary>
    static int PartitionWatershed(RcCompactHeightfield field, Span<int> regions, Span<int> distance, Span<int> work)
    {
        int maxDistance = NavRegionDistances.Build(field, work, distance);
        int nextId = PaintBorder(field, regions);
        return NavWatershed.Partition(field, regions, distance, work, maxDistance, nextId);
    }

    /// <summary>
    /// Paints the padded border west, east, south, then north and returns the first interior region ID.
    /// </summary>
    static int PaintBorder(RcCompactHeightfield field, Span<int> regions)
    {
        int border = field.borderSize;
        if (border <= 0) return 1;

        int width = field.width;
        int height = field.height;
        int borderWidth = Math.Min(width, border);
        int borderHeight = Math.Min(height, border);
        PaintRect(field, regions, 0, borderWidth, 0, height, 1 | RC_BORDER_REG);
        PaintRect(field, regions, width - borderWidth, width, 0, height, 2 | RC_BORDER_REG);
        PaintRect(field, regions, 0, width, 0, borderHeight, 3 | RC_BORDER_REG);
        PaintRect(field, regions, 0, width, height - borderHeight, height, 4 | RC_BORDER_REG);
        return 5;
    }

    /// <summary>
    /// Assigns the rectangle's walkable spans to a border region.
    /// </summary>
    static void PaintRect(RcCompactHeightfield field, Span<int> regions, int minX, int maxX, int minY, int maxY, int id)
    {
        for (int y = minY; y < maxY; y++)
        {
            for (int x = minX; x < maxX; x++)
            {
                var cell = field.cells[x + y * field.width];
                for (int i = cell.index; i < cell.index + cell.count; i++)
                    if (field.areas[i] != RC_NULL_AREA) regions[i] = id;
            }
        }
    }

    /// <summary>
    /// Replaces each compact span with a copy carrying its region ID.
    /// </summary>
    static void StoreRegions(RcCompactHeightfield field, ReadOnlySpan<int> regions)
    {
        var builder = s_spanBuilder ??= RcCompactSpanBuilder.NewBuilder();
        for (int i = 0; i < regions.Length; i++)
        {
            ref var span = ref field.spans[i];
            builder.y = span.y;
            builder.h = span.h;
            builder.con = span.con;
            builder.reg = regions[i];
            span = builder.Build();
        }
    }
}
