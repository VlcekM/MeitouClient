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

    /// <summary>
    /// Twice the signed area of a polygon on (X, Z); positive when it winds counter-clockwise with X right and Z up. Summed from the first vertex in double:
    /// at world coordinates (~1e5) the float products round by hundreds of square units, more than a door painter's whole area, so its winding came out at
    /// random (docs/game/pathfinding.md, "Door painters").
    /// </summary>
    public static float SignedArea(Vector2[] p)
    {
        if (p.Length < 3) return 0;
        double area = 0, ox = p[0].X, oy = p[0].Y;
        for (int i = 1; i + 1 < p.Length; i++)
            area += (p[i].X - ox) * (double)(p[i + 1].Y - oy) - (p[i + 1].X - ox) * (double)(p[i].Y - oy);
        return (float)area;
    }

    /// <summary>Whether a point lies inside a convex polygon (either winding), allowing <paramref name="tolerance"/> outside it.</summary>
    public static bool InsideConvex(Vector2[] polygon, Vector2 point, float tolerance)
    {
        float sign = SignedArea(polygon) >= 0 ? 1 : -1;
        for (int i = 0; i < polygon.Length; i++)
            if (EdgeOutside(polygon, i, point, sign, out float d) && d > tolerance) return false;
        return true;
    }

    /// <summary>The cross-section of a triangle mesh at height <paramref name="y"/> on (X, Z): one segment per triangle the plane cuts.</summary>
    public static List<(Vector2 A, Vector2 B)> Slice(Vector3[] points, int[] indices, float y)
    {
        var segments = new List<(Vector2, Vector2)>();
        Span<Vector2> cut = stackalloc Vector2[3];
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int n = 0;
            for (int e = 0; e < 3; e++)
            {
                var a = points[indices[t + e]]; var b = points[indices[t + (e + 1) % 3]];
                if ((a.Y < y) == (b.Y < y)) continue;
                float f = (y - a.Y) / (b.Y - a.Y);
                cut[n++] = new Vector2(a.X + (b.X - a.X) * f, a.Z + (b.Z - a.Z) * f);
            }
            if (n == 2) segments.Add((cut[0], cut[1]));
        }
        return segments;
    }

    /// <summary>Whether a point lies inside closed outlines given as loose segments (even-odd rule).</summary>
    public static bool InsideOutline(List<(Vector2 A, Vector2 B)> segments, Vector2 point)
    {
        bool inside = false;
        foreach (var (a, b) in segments)
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < a.X + (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) inside = !inside;
        return inside;
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

    /// <summary>How far (units) the facing edge of an inner near-miss seam may lie off an edge's line (docs/game/pathfinding.md, "Inner seams").</summary>
    public const float NearMissGap = 1.5f;

    /// <summary>
    /// Whether <paramref name="b0"/> <paramref name="b1"/> runs opposite <paramref name="a0"/> <paramref name="a1"/>, overlaps it along that line and lies
    /// within <paramref name="maxGap"/> of it (on X/Z) at both ends of the overlap. <paramref name="lo"/>, <paramref name="hi"/>: the overlap as fractions of
    /// a0 a1; <paramref name="yLo"/>, <paramref name="yHi"/>: b's heights there.
    /// </summary>
    public static bool FacingOverlap(Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1, float maxGap, out float lo, out float hi, out float yLo, out float yHi)
    {
        lo = hi = yLo = yHi = 0;
        var d = a1 - a0;
        float len2 = d.X * d.X + d.Z * d.Z;
        if (len2 < 1e-9f) return false;
        float t0 = ((b0.X - a0.X) * d.X + (b0.Z - a0.Z) * d.Z) / len2, t1 = ((b1.X - a0.X) * d.X + (b1.Z - a0.Z) * d.Z) / len2;
        if (t1 >= t0) return false;
        lo = Math.Max(0, t1); hi = Math.Min(1, t0);
        if (hi - lo < 1e-4f) return false;
        var bLo = Vector3.Lerp(b0, b1, (lo - t0) / (t1 - t0));
        var bHi = Vector3.Lerp(b0, b1, (hi - t0) / (t1 - t0));
        float len = MathF.Sqrt(len2);
        if (MathF.Abs((bLo.X - a0.X) * d.Z - (bLo.Z - a0.Z) * d.X) / len > maxGap || MathF.Abs((bHi.X - a0.X) * d.Z - (bHi.Z - a0.Z) * d.X) / len > maxGap) return false;
        yLo = bLo.Y; yHi = bHi.Y;
        return true;
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
