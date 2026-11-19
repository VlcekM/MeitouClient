using DotRecast.Recast;
using static DotRecast.Recast.RcRecast;
using static Meitou.Simulation.Navigation.NavTileRegions;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Watershed distances to the nearest area edge. Adapted from DotRecast's RcRegions to use pooled buffers
/// (zlib licence, THIRD_PARTY_NOTICES.md).
/// </summary>
internal static class NavRegionDistances
{
    /// <summary>
    /// Fills the blurred distances and returns the maximum distance before blurring.
    /// </summary>
    public static int Build(RcCompactHeightfield field, Span<int> distance, Span<int> blurred)
    {
        distance.Fill(0xffff);
        MarkBoundaries(field, distance);
        Propagate(field, distance, reverse: false);
        Propagate(field, distance, reverse: true);

        int maxDistance = 0;
        foreach (int value in distance)
            maxDistance = Math.Max(maxDistance, value);

        Blur(field, distance, blurred);
        return maxDistance;
    }

    /// <summary>
    /// Starts an area edge wherever a span lacks a same-area cardinal neighbour.
    /// </summary>
    static void MarkBoundaries(RcCompactHeightfield field, Span<int> distance)
    {
        for (int y = 0; y < field.height; y++)
        {
            for (int x = 0; x < field.width; x++)
            {
                var cell = field.cells[x + y * field.width];
                for (int i = cell.index; i < cell.index + cell.count; i++)
                {
                    for (int dir = 0; dir < 4; dir++)
                    {
                        int next = Neighbour(field, x, y, i, dir);
                        if (next >= 0 && field.areas[next] == field.areas[i]) continue;

                        distance[i] = 0;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Runs one chamfer pass. Both passes visit the spans within a column in ascending order.
    /// </summary>
    static void Propagate(RcCompactHeightfield field, Span<int> distance, bool reverse)
    {
        int xStart = reverse ? field.width - 1 : 0;
        int yStart = reverse ? field.height - 1 : 0;
        int xEnd = reverse ? -1 : field.width;
        int yEnd = reverse ? -1 : field.height;
        int step = reverse ? -1 : 1;
        int horizontal = reverse ? 2 : 0;
        int vertical = reverse ? 1 : 3;

        for (int y = yStart; y != yEnd; y += step)
        {
            for (int x = xStart; x != xEnd; x += step)
            {
                var cell = field.cells[x + y * field.width];
                for (int i = cell.index; i < cell.index + cell.count; i++)
                {
                    Relax(field, distance, x, y, i, horizontal, vertical);
                    Relax(field, distance, x, y, i, vertical, horizontal ^ 2);
                }
            }
        }
    }

    /// <summary>
    /// Relaxes the distance from one cardinal neighbour and the diagonal reached through that neighbour.
    /// </summary>
    static void Relax(RcCompactHeightfield field, Span<int> distance, int x, int y, int index, int dir, int diagonalDir)
    {
        int next = Neighbour(field, x, y, index, dir);
        if (next < 0) return;

        distance[index] = Math.Min(distance[index], distance[next] + 2);
        int diagonal = Neighbour(field, x + GetDirOffsetX(dir), y + GetDirOffsetY(dir), next, diagonalDir);
        if (diagonal >= 0) distance[index] = Math.Min(distance[index], distance[diagonal] + 3);
    }

    /// <summary>
    /// Applies Recast's nine-sample box blur to distances greater than two.
    /// </summary>
    static void Blur(RcCompactHeightfield field, ReadOnlySpan<int> distance, Span<int> blurred)
    {
        for (int y = 0; y < field.height; y++)
        {
            for (int x = 0; x < field.width; x++)
            {
                var cell = field.cells[x + y * field.width];
                for (int i = cell.index; i < cell.index + cell.count; i++)
                    blurred[i] = distance[i] <= 2 ? distance[i] : BlurSpan(field, distance, x, y, i);
            }
        }
    }

    /// <summary>
    /// Averages the span and its eight neighbours, substituting the centre for missing samples.
    /// </summary>
    static int BlurSpan(RcCompactHeightfield field, ReadOnlySpan<int> distance, int x, int y, int index)
    {
        int centre = distance[index];
        int sum = centre;
        for (int dir = 0; dir < 4; dir++)
        {
            int next = Neighbour(field, x, y, index, dir);
            if (next < 0)
            {
                sum += centre * 2;
                continue;
            }

            int diagonal = Neighbour(field, x + GetDirOffsetX(dir), y + GetDirOffsetY(dir), next, (dir + 1) & 3);
            sum += distance[next] + (diagonal >= 0 ? distance[diagonal] : centre);
        }

        return (sum + 5) / 9;
    }
}
