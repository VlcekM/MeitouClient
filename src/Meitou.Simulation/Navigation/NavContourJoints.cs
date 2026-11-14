using DotRecast.Recast;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Recast simplifies the boundary two regions share to a straight line between the points where a contour's neighbour changes, each contour by
/// its own changes. An obstacle that touches the boundary with a corner (a tree's cutter cell) changes the neighbour of the region it lies in
/// only: that contour keeps the corner, the other runs straight past it, and the triangle between them belongs to no polygon, a thin hole up
/// to a tile long that walkers path round (docs/game/pathfinding.md "Own builder", shared boundaries; #392, #415). This gives a contour every
/// point of its raw outline that another contour kept, before the polygons are made.
/// </summary>
internal static class NavContourJoints
{
    /// <summary>
    /// The bits of a contour vertex's flags holding the region across the edge that starts at it (Recast's RC_CONTOUR_REG_MASK); 0 is a wall.
    /// </summary>
    const int RegionMask = 0xffff;

    /// <summary>
    /// The flag of a vertex Recast removes after the polygons are made (RC_BORDER_VERTEX); an added point never carries it.
    /// </summary>
    const int BorderVertex = 0x10000;

    /// <summary>
    /// How many height cells apart two contour points at one cell corner still are one vertex (Recast welds a polygon mesh's vertices within 2).
    /// </summary>
    const int HeightWeld = 2;

    /// <summary>
    /// Adds to each contour of <paramref name="set"/>, along its edges toward other regions, the raw points that are vertices of the other contours.
    /// </summary>
    public static void Add(RcContourSet set)
    {
        var counts = VertexCounts(set);
        foreach (var contour in set.conts)
        {
            if (contour.nverts < 3) continue;

            var own = VertexCounts(contour);
            var raws = RawOutlines(set, contour);
            var added = new List<int>?[contour.nverts];
            bool any = false;
            for (int i = 0; i < contour.nverts; i++)
            {
                if ((contour.verts[i * 4 + 3] & RegionMask) == 0) continue;

                added[i] = Between(contour, i, raws, own, counts);
                any |= added[i] is not null;
            }

            if (any) Insert(contour, added);
        }
    }

    /// <summary>
    /// How many contour vertices of <paramref name="set"/> stand at each point (cells).
    /// </summary>
    static Dictionary<(int X, int Y, int Z), int> VertexCounts(RcContourSet set)
    {
        var counts = new Dictionary<(int X, int Y, int Z), int>();
        foreach (var contour in set.conts)
            Count(contour, counts);

        return counts;
    }

    /// <summary>
    /// How many vertices of <paramref name="contour"/> stand at each point (cells).
    /// </summary>
    static Dictionary<(int X, int Y, int Z), int> VertexCounts(RcContour contour)
    {
        var counts = new Dictionary<(int X, int Y, int Z), int>();
        Count(contour, counts);
        return counts;
    }

    /// <summary>
    /// Counts the vertices of <paramref name="contour"/> into <paramref name="counts"/>.
    /// </summary>
    static void Count(RcContour contour, Dictionary<(int X, int Y, int Z), int> counts)
    {
        for (int i = 0; i < contour.nverts; i++)
        {
            var point = Point(contour.verts, i);
            counts[point] = counts.GetValueOrDefault(point) + 1;
        }
    }

    /// <summary>
    /// The raw outlines the vertices of <paramref name="contour"/> were picked from: its own, and those of its region's holes, whose vertices
    /// Recast merged into it (a merged hole keeps its raw points and no vertices).
    /// </summary>
    static List<(int[] Points, int Count)> RawOutlines(RcContourSet set, RcContour contour)
    {
        var raws = new List<(int[] Points, int Count)> { (contour.rverts, contour.nrverts) };
        foreach (var other in set.conts)
        {
            if (other.nverts == 0 && other.reg == contour.reg && other.rverts is not null) raws.Add((other.rverts, other.nrverts));
        }

        return raws;
    }

    /// <summary>
    /// The raw points (x, y, z, flags each) between vertex <paramref name="i"/> of <paramref name="contour"/> and the next that other contours
    /// have as vertices, in order; null when there are none or the edge follows no raw outline (a bridge to a hole).
    /// </summary>
    static List<int>? Between(RcContour contour, int i, List<(int[] Points, int Count)> raws, Dictionary<(int X, int Y, int Z), int> own,
        Dictionary<(int X, int Y, int Z), int> counts)
    {
        var from = Point(contour.verts, i);
        var to = Point(contour.verts, (i + 1) % contour.nverts);
        int flags = contour.verts[i * 4 + 3] & ~BorderVertex;
        foreach (var (points, count) in raws)
        {
            for (int start = 0; start < count; start++)
            {
                if (Point(points, start) != from) continue;

                if (Run(points, count, start, to, own, counts, flags) is { } found) return found.Count == 0 ? null : found;
            }
        }

        return null;
    }

    /// <summary>
    /// Walks the raw outline from <paramref name="start"/> to <paramref name="to"/> and answers the points on the way that other contours have
    /// as vertices, with <paramref name="flags"/>; null when one of the contour's own vertices comes first (another pass of the outline through
    /// the start).
    /// </summary>
    static List<int>? Run(int[] points, int count, int start, (int X, int Y, int Z) to, Dictionary<(int X, int Y, int Z), int> own,
        Dictionary<(int X, int Y, int Z), int> counts, int flags)
    {
        var found = new List<int>();
        for (int step = 1; step < count; step++)
        {
            var point = Point(points, (start + step) % count);
            if (point == to) return found;

            if (own.ContainsKey(point)) return null;

            if (!SharedCorner(point, own, counts)) continue;

            found.Add(point.X);
            found.Add(point.Y);
            found.Add(point.Z);
            found.Add(flags);
        }

        return null;
    }

    /// <summary>
    /// Whether another contour has a vertex at <paramref name="point"/>'s cell corner within <see cref="HeightWeld"/> of its height, and the
    /// contour itself (<paramref name="own"/>) none.
    /// </summary>
    static bool SharedCorner((int X, int Y, int Z) point, Dictionary<(int X, int Y, int Z), int> own, Dictionary<(int X, int Y, int Z), int> counts)
    {
        bool shared = false;
        for (int dy = -HeightWeld; dy <= HeightWeld; dy++)
        {
            var at = (point.X, point.Y + dy, point.Z);
            if (own.ContainsKey(at)) return false;

            shared |= counts.ContainsKey(at);
        }

        return shared;
    }

    /// <summary>
    /// Rebuilds the vertices of <paramref name="contour"/> with the points of <paramref name="added"/> after the vertex each list belongs to.
    /// </summary>
    static void Insert(RcContour contour, List<int>?[] added)
    {
        var verts = new List<int>(contour.nverts * 4);
        for (int i = 0; i < contour.nverts; i++)
        {
            verts.AddRange(contour.verts.AsSpan(i * 4, 4));
            if (added[i] is { } points) verts.AddRange(points);
        }

        contour.verts = [.. verts];
        contour.nverts = verts.Count / 4;
    }

    /// <summary>
    /// Point <paramref name="i"/> of a contour's vertex or raw point array (cells).
    /// </summary>
    static (int X, int Y, int Z) Point(int[] points, int i) => (points[i * 4], points[i * 4 + 1], points[i * 4 + 2]);
}
