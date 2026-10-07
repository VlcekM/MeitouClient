using System.Numerics;

namespace Meitou.Navigation;

/// <summary>
/// The small plane geometry the gatherer, builder, pruner and queries share. Each expression is evaluated exactly as it was where it lived before
/// (float evaluation order decides the built meshes bit for bit, and paths follow from them).
/// </summary>
internal static class NavGeometry
{
    /// <summary>How close (units) both ends of an edge have to be to a zone's border line for the edge to lie on it.</summary>
    public const float BorderTolerance = 0.05f;

    /// <summary>Twice the signed area of a polygon on (X, Z); positive when it winds counter-clockwise with X right and Z up.</summary>
    public static float SignedArea(Vector2[] p)
    {
        float area = 0;
        for (int i = 0; i < p.Length; i++) area += p[i].X * p[(i + 1) % p.Length].Y - p[(i + 1) % p.Length].X * p[i].Y;
        return area;
    }

    /// <summary>Whether a point lies inside a convex polygon (either winding), allowing <paramref name="tolerance"/> outside it.</summary>
    public static bool InsideConvex(Vector2[] polygon, Vector2 point, float tolerance)
    {
        float sign = SignedArea(polygon) >= 0 ? 1 : -1;
        for (int i = 0; i < polygon.Length; i++)
            if (EdgeOutside(polygon, i, point, sign, out float d) && d > tolerance) return false;
        return true;
    }

    /// <summary>How far a point is outside a convex polygon (0 inside), either winding.</summary>
    public static float DistanceOutsideConvex(Vector2[] polygon, Vector2 point)
    {
        float sign = SignedArea(polygon) >= 0 ? 1 : -1;
        float outside = 0;
        for (int i = 0; i < polygon.Length; i++)
            if (EdgeOutside(polygon, i, point, sign, out float d)) outside = Math.Max(outside, d);
        return outside;
    }

    /// <summary>The distance of the point beyond edge <paramref name="i"/> (negative inside); false for a degenerate edge.</summary>
    static bool EdgeOutside(Vector2[] polygon, int i, Vector2 point, float sign, out float d)
    {
        var a = polygon[i];
        var e = polygon[(i + 1) % polygon.Length] - a;
        float len = e.Length();
        if (len < 1e-6f) { d = 0; return false; }
        d = -sign * (e.X * (point.Y - a.Y) - e.Y * (point.X - a.X)) / len;
        return true;
    }

    /// <summary>The convex footprint of the points on the XZ plane (grown by <paramref name="grow"/> on each side), counter-clockwise from above.</summary>
    public static NavVolume Footprint(IReadOnlyList<Vector3> points, float yMin, float yMax, float grow)
    {
        var list = new List<Vector2>(points.Count * (grow > 0 ? 4 : 1));
        foreach (var p in points)
        {
            if (grow > 0)
            {
                list.Add(new(p.X - grow, p.Z - grow)); list.Add(new(p.X + grow, p.Z - grow));
                list.Add(new(p.X - grow, p.Z + grow)); list.Add(new(p.X + grow, p.Z + grow));
            }
            else list.Add(new(p.X, p.Z));
        }
        return new NavVolume(ConvexHull2D(list), yMin, yMax);
    }

    /// <summary>Andrew's monotone chain; counter-clockwise on (X, Z) with X right and Z up in the plane's own axes.</summary>
    public static Vector2[] ConvexHull2D(List<Vector2> pts)
    {
        pts.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        if (pts.Count < 3) return [.. pts];
        var hull = new List<Vector2>();
        foreach (var p in pts)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        int lower = hull.Count + 1;
        for (int i = pts.Count - 2; i >= 0; i--)
        {
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], pts[i]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(pts[i]);
        }
        hull.RemoveAt(hull.Count - 1);
        return [.. hull];
    }

    static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    /// <summary>Barycentric coordinates of (x, z) in the triangle seen from above; false for a triangle without area on that plane.</summary>
    public static bool Barycentric(float x, float z, Vector3 a, Vector3 b, Vector3 c, out float u, out float v, out float w)
    {
        float det = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        if (MathF.Abs(det) < 1e-9f) { u = v = w = 0; return false; }
        u = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / det;
        v = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / det;
        w = 1 - u - v;
        return true;
    }

    /// <summary>Distance on the XZ plane from a point to a segment.</summary>
    public static float SegmentDistanceXZ(float px, float pz, float ax, float az, float bx, float bz)
    {
        float dx = bx - ax, dz = bz - az;
        float len2 = dx * dx + dz * dz;
        float t = len2 < 1e-9f ? 0 : Math.Clamp(((px - ax) * dx + (pz - az) * dz) / len2, 0, 1);
        float cx = ax + t * dx - px, cz = az + t * dz - pz;
        return MathF.Sqrt(cx * cx + cz * cz);
    }

    /// <summary>Whether an edge with the coordinates <paramref name="a"/> and <paramref name="b"/> across a border line lies on the line <paramref name="line"/>.</summary>
    public static bool OnLine(float a, float b, float line) => Math.Abs(a - line) < BorderTolerance && Math.Abs(b - line) < BorderTolerance;

    /// <summary>Whether the edge <paramref name="edge"/> of a polygon has a link (<c>Links</c> holds pairs edge, polygon).</summary>
    public static bool HasLink(ReadOnlySpan<int> links, int edge)
    {
        for (int i = 0; i < links.Length; i += 2) if (links[i] == edge) return true;
        return false;
    }

    /// <summary>An edge's extent along the border line (sorted) and its heights at those two ends.</summary>
    public static (float From, float To, float YFrom, float YTo) Span(Vector3 a, Vector3 b, bool alongX)
    {
        float ta = alongX ? a.X : a.Z, tb = alongX ? b.X : b.Z;
        return ta <= tb ? (ta, tb, a.Y, b.Y) : (tb, ta, b.Y, a.Y);
    }

    /// <summary>The height of a span at <paramref name="at"/> along it.</summary>
    public static float Lerp(float a0, float a1, float y0, float y1, float at) => a1 - a0 < 1e-5f ? y0 : y0 + (y1 - y0) * (at - a0) / (a1 - a0);

    /// <summary>Whether two spans along one border line overlap and, at the middle of the overlap, their heights differ by no more than <paramref name="maxStep"/>.</summary>
    public static bool SpansMeet((float From, float To, float YFrom, float YTo) a, (float From, float To, float YFrom, float YTo) b, float maxStep)
    {
        float lo = Math.Max(a.From, b.From), hi = Math.Min(a.To, b.To);
        if (hi - lo < 0.01f) return false;
        float mid = (lo + hi) / 2;
        return !(Math.Abs(Lerp(a.From, a.To, a.YFrom, a.YTo, mid) - Lerp(b.From, b.To, b.YFrom, b.YTo, mid)) > maxStep);
    }
}
