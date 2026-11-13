using System.Numerics;

namespace Meitou.Simulation.Construction;

/// <summary>
/// The two-step snap of a town's centre onto the navmesh (<b>Verified</b>, the closest-point wrapper <c>FUN_1403a2fa0</c> / <c>FUN_1403a2300</c>,
/// docs/game/buildings-production.md "Outposts"): the 3D closest point, then the nearest point kept <see cref="Clearance"/> units clear of the
/// mesh's boundary.
/// </summary>
static class RefitSnap
{
    /// <summary>
    /// The half size of the cube the 3D closest-point query searches (500 units, 50 in Havok space).
    /// </summary>
    public const float Reach = 500;
    /// <summary>
    /// The clearance step's distance from the mesh's boundary (the extent value 2 Havok = 20 units) and its vertical reach (the up component stays at
    /// the cap).
    /// </summary>
    public const float Clearance = 20, VerticalReach = 20;
    /// <summary>
    /// The half sizes of the clearance step's search boxes: the start extent doubled on each of three iterations; the first box that finds a point wins.
    /// </summary>
    static readonly float[] Boxes = [20, 40, 80];
    /// <summary>
    /// The spacing of the clearance search's candidate grid (engine choice; the original solves the 2D problem exactly).
    /// </summary>
    const float GridStep = 0.25f;

    /// <summary>
    /// The snapped centre for <paramref name="centre"/> on <paramref name="faces"/>: step 1, then the clearance step from its point (step 1's point
    /// when no box finds one); null when no unfiltered face lies within <see cref="Reach"/> (no snap).
    /// </summary>
    public static Vector3? Snap(IReadOnlyList<SurfaceFace> faces, Vector3 centre)
    {
        var first = Closest(faces, centre);
        if (first is null) return null;

        return Clear(faces, first.Value) ?? first;
    }

    #region Step 1

    /// <summary>
    /// The point of the unfiltered faces nearest to <paramref name="at"/> in 3D, among the faces whose box meets the cube of half size
    /// <see cref="Reach"/> round it.
    /// </summary>
    public static Vector3? Closest(IReadOnlyList<SurfaceFace> faces, Vector3 at)
    {
        Vector3? best = null;
        float bestDistance = float.MaxValue;
        foreach (var face in faces)
        {
            if (face.Filtered || !BoxMeets(face, at.X, at.Z, Reach, at.Y, Reach)) continue;

            for (int k = 1; k + 1 < face.Corners.Length; k++)
            {
                var point = ClosestOnTriangle(at, face.Corners[0], face.Corners[k], face.Corners[k + 1]);
                float d = Vector3.DistanceSquared(at, point);
                if (d >= bestDistance) continue;

                bestDistance = d;
                best = point;
            }
        }

        return best;
    }

    /// <summary>
    /// The point of triangle (a, b, c) nearest to <paramref name="p"/>.
    /// </summary>
    static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;

        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));

        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));

        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)));

        float denominator = 1 / (va + vb + vc);
        return a + ab * (vb * denominator) + ac * (vc * denominator);
    }

    #endregion

    #region Step 2

    /// <summary>
    /// The clearance step from <paramref name="start"/>: for each search box in turn the faces in it (horizontally the box, vertically
    /// <see cref="VerticalReach"/>), then the point nearest the start in X/Z that lies on one of them and is at least <see cref="Clearance"/> from every
    /// open edge of them; null when no box has one. Engine choice: plain distance to the open edges, where the original grows the 2D convex hull of
    /// each boundary loop by 20 units with 8 segments per corner (docs/game/buildings-production.md).
    /// </summary>
    static Vector3? Clear(IReadOnlyList<SurfaceFace> faces, Vector3 start)
    {
        foreach (float box in Boxes)
        {
            var near = faces.Where(f => BoxMeets(f, start.X, start.Z, box, start.Y, VerticalReach)).ToList();
            var edges = OpenEdges(near, start, box + Clearance);
            var found = Nearest(near, edges, start, box);
            if (found is not null) return found;
        }

        return null;
    }

    /// <summary>
    /// The open edges (X/Z ends) of the faces that lie within <paramref name="reach"/> of the start's X/Z.
    /// </summary>
    static List<(Vector2 A, Vector2 B)> OpenEdges(List<SurfaceFace> near, Vector3 start, float reach)
    {
        var edges = new List<(Vector2, Vector2)>();
        var at = new Vector2(start.X, start.Z);
        foreach (var face in near)
        {
            for (int k = 0; k < face.Corners.Length; k++)
            {
                if (!face.OpenEdges[k]) continue;

                var a = new Vector2(face.Corners[k].X, face.Corners[k].Z);
                var b = new Vector2(face.Corners[(k + 1) % face.Corners.Length].X, face.Corners[(k + 1) % face.Corners.Length].Z);
                if (SegmentDistance(at, a, b) < reach) edges.Add((a, b));
            }
        }

        return edges;
    }

    /// <summary>
    /// The candidate nearest the start on the grid of <see cref="GridStep"/> over the box that stands on a face and clears every edge; its height is
    /// the face's, the one nearest the start's height where faces stack. Null when none.
    /// </summary>
    static Vector3? Nearest(List<SurfaceFace> near, List<(Vector2 A, Vector2 B)> edges, Vector3 start, float box)
    {
        int n = (int)(box / GridStep);
        float bestDistance = float.MaxValue;
        Vector3? best = null;
        for (int i = -n; i <= n; i++)
        {
            for (int j = -n; j <= n; j++)
            {
                float d = (i * i + j * j) * GridStep * GridStep;
                if (d >= bestDistance) continue;

                var p = new Vector2(start.X + i * GridStep, start.Z + j * GridStep);
                if (!ClearOf(edges, p) || !TryHeight(near, p, start.Y, out float y)) continue;

                bestDistance = d;
                best = new Vector3(p.X, y, p.Y);
            }
        }

        return best;
    }

    /// <summary>
    /// Whether <paramref name="p"/> is at least <see cref="Clearance"/> from every edge.
    /// </summary>
    static bool ClearOf(List<(Vector2 A, Vector2 B)> edges, Vector2 p)
    {
        foreach (var (a, b) in edges)
        {
            if (SegmentDistance(p, a, b) < Clearance) return false;
        }

        return true;
    }

    /// <summary>
    /// The height at X/Z of the face under it whose surface is nearest <paramref name="nearY"/> (within <see cref="VerticalReach"/>); false when no
    /// face lies under the point.
    /// </summary>
    static bool TryHeight(List<SurfaceFace> near, Vector2 p, float nearY, out float height)
    {
        height = 0;
        bool found = false;
        foreach (var face in near)
        {
            if (!HeightAt(face, p, out float y) || MathF.Abs(y - nearY) > VerticalReach) continue;
            if (found && MathF.Abs(y - nearY) >= MathF.Abs(height - nearY)) continue;

            height = y;
            found = true;
        }

        return found;
    }

    /// <summary>
    /// The height of the face's surface at X/Z, false when the point is outside it seen from above.
    /// </summary>
    static bool HeightAt(SurfaceFace face, Vector2 p, out float y)
    {
        var c = face.Corners;
        for (int k = 1; k + 1 < c.Length; k++)
        {
            if (!Barycentric(p, c[0], c[k], c[k + 1], out float u, out float v, out float w)) continue;
            if (u < -1e-5f || v < -1e-5f || w < -1e-5f) continue;

            y = u * c[0].Y + v * c[k].Y + w * c[k + 1].Y;
            return true;
        }

        y = 0;
        return false;
    }

    /// <summary>
    /// The barycentric coordinates of X/Z in triangle (a, b, c) seen from above; false for a degenerate triangle.
    /// </summary>
    static bool Barycentric(Vector2 p, Vector3 a, Vector3 b, Vector3 c, out float u, out float v, out float w)
    {
        float denominator = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        u = v = w = 0;
        if (MathF.Abs(denominator) < 1e-9f) return false;

        u = ((b.Z - c.Z) * (p.X - c.X) + (c.X - b.X) * (p.Y - c.Z)) / denominator;
        v = ((c.Z - a.Z) * (p.X - c.X) + (a.X - c.X) * (p.Y - c.Z)) / denominator;
        w = 1 - u - v;
        return true;
    }

    #endregion

    #region Shared

    /// <summary>
    /// Whether the face's box meets the box of half size <paramref name="half"/> round (<paramref name="x"/>, <paramref name="z"/>) and
    /// <paramref name="halfY"/> round <paramref name="y"/>.
    /// </summary>
    static bool BoxMeets(SurfaceFace face, float x, float z, float half, float y, float halfY)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        foreach (var c in face.Corners)
        {
            lo = Vector3.Min(lo, c);
            hi = Vector3.Max(hi, c);
        }

        return hi.X >= x - half && lo.X <= x + half && hi.Z >= z - half && lo.Z <= z + half && hi.Y >= y - halfY && lo.Y <= y + halfY;
    }

    /// <summary>
    /// The distance from <paramref name="p"/> to the segment (a, b).
    /// </summary>
    static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float length = ab.LengthSquared();
        float t = length < 1e-12f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / length, 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }

    #endregion
}
