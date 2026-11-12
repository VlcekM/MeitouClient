using System.Numerics;

namespace Meitou.Data.World;

/// <summary>
/// The part with ray picks and the floor below a point.
/// </summary>
public sealed partial class FloorSurfaces
{
    /// <summary>
    /// The nearest collision hit on floors up to the viewed floor. Walls drop the destination onto a floor beneath the hit;
    /// stairs and landings retain their collision height. Hidden upper floors do not intercept the ray.
    /// </summary>
    public (float Distance, Vector3 Position)? MoveHit(Vector3 origin, Vector3 direction, int floor, float maxDistance)
    {
        (float Distance, Vector3 Position)? best = null;

        foreach (var s in _surfaces)
        {
            if (s.Floor > floor || !RayBox(origin, direction, new(s.Min.X, s.YMin, s.Min.Y), new(s.Max.X, s.YMax, s.Max.Y), maxDistance))
                continue;

            for (int i = 0; i + 2 < s.Corners.Length; i += 3)
            {
                var a = s.Corners[i];
                var b = s.Corners[i + 1];
                var c = s.Corners[i + 2];
                if (RayTriangle(origin, direction, a, b, c) is not { } t || t > maxDistance || best is { } previous && t >= previous.Distance)
                    continue;

                var hit = origin + direction * t;

                if (Walkable(a, b, c)) best = (t, hit);
                else
                {
                    // Move just inside the wall so round-off at its boundary does not drop the pick outside the floor.
                    var inside = hit + new Vector3(direction.X, 0, direction.Z) * 0.1f;
                    if (FloorBelow(inside, floor) is { } destination) best = (t, destination);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// The upward unit normal of the floor surface a ray straight down from <paramref name="point"/> (raised by <paramref name="reach"/>) meets
    /// first, on floors up to the viewed one; null when it meets none. The indoor ghost's single slope ray (<c>FUN_1404daa20</c>, <b>Verified</b>:
    /// one ray at its origin when it has a parent building).
    /// </summary>
    public Vector3? NormalBelow(Vector3 point, int floor, float reach)
    {
        var origin = point + new Vector3(0, reach, 0);
        float nearest = float.MaxValue;
        Vector3? normal = null;

        foreach (var s in _surfaces)
        {
            if (s.Floor > floor || point.X < s.Min.X || point.X > s.Max.X || point.Z < s.Min.Y || point.Z > s.Max.Y)
                continue;

            foreach (int i in s.Grid.At(new Vector2(point.X, point.Z)))
            {
                var a = s.Corners[i];
                var b = s.Corners[i + 1];
                var c = s.Corners[i + 2];
                if (RayTriangle(origin, -Vector3.UnitY, a, b, c) is not { } t || t >= nearest) continue;

                var n = Vector3.Cross(b - a, c - a);
                if (n.LengthSquared() < 1e-12f) continue;

                nearest = t;
                normal = Vector3.Normalize(n.Y < 0 ? -n : n);
            }
        }

        return normal;
    }

    /// <summary>
    /// The highest point at most 0.5 over <paramref name="point"/> on a walkable triangle of a floor up to <paramref name="floor"/>: straight below
    /// it, else on a triangle edge within 4 units (a floor that starts inside a thick wall); null when there is none.
    /// </summary>
    Vector3? FloorBelow(Vector3 point, int floor)
    {
        Vector3? best = null;
        const float wallReach = 4;

        foreach (var s in _surfaces)
        {
            if (s.Floor > floor || point.X < s.Min.X - wallReach || point.X > s.Max.X + wallReach || point.Z < s.Min.Y - wallReach
                || point.Z > s.Max.Y + wallReach)
            {
                continue;
            }

            for (int i = 0; i + 2 < s.Corners.Length; i += 3)
            {
                var a = s.Corners[i];
                var b = s.Corners[i + 1];
                var c = s.Corners[i + 2];
                if (!Walkable(a, b, c)) continue;

                var q = new Vector2(point.X, point.Z);
                Vector3 target;

                if (HeightAt(a, b, c, q) is { } y) target = new(q.X, y, q.Y);
                else
                {
                    // The floor may start inside a thick wall; snap only across that small gap.
                    float nearest = wallReach * wallReach;
                    Vector3? edge = null;

                    for (int k = 0; k < 3; k++)
                    {
                        var p = s.Corners[i + k];
                        var end = s.Corners[i + (k + 1) % 3];
                        var delta = new Vector2(end.X - p.X, end.Z - p.Z);
                        float along = delta.LengthSquared() < 1e-9f
                            ? 0
                            : Math.Clamp(Vector2.Dot(q - new Vector2(p.X, p.Z), delta) / delta.LengthSquared(), 0, 1);
                        var candidate = Vector3.Lerp(p, end, along);
                        float distance = Vector2.DistanceSquared(q, new(candidate.X, candidate.Z));
                        if (distance > nearest) continue;

                        (nearest, edge) = (distance, candidate);
                    }

                    if (edge is not { } e) continue;

                    target = e;
                }

                if (target.Y <= point.Y + 0.5f && (best is null || target.Y > best.Value.Y)) best = target;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether the triangle slopes at most 40°, the navigation slope limit; collision meshes can have either winding.
    /// </summary>
    static bool Walkable(Vector3 a, Vector3 b, Vector3 c)
    {
        var normal = Vector3.Cross(b - a, c - a);
        return normal.LengthSquared() > 1e-12f && MathF.Abs(normal.Y) >= normal.Length() * MathF.Cos(40 * MathF.PI / 180);
    }

    /// <summary>
    /// The distance along <paramref name="direction"/> from <paramref name="origin"/> to triangle <paramref name="a"/>, <paramref name="b"/>,
    /// <paramref name="c"/> (either winding); null when the ray misses it or it lies behind.
    /// </summary>
    internal static float? RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
    {
        var edge = b - a;
        var cross = Vector3.Cross(direction, c - a);
        float determinant = Vector3.Dot(edge, cross);
        if (MathF.Abs(determinant) < 1e-7f) return null;

        var offset = origin - a;
        float u = Vector3.Dot(offset, cross) / determinant;
        var q = Vector3.Cross(offset, edge);
        float v = Vector3.Dot(direction, q) / determinant;
        if (u < -1e-5f || v < -1e-5f || u + v > 1.00001f) return null;

        float t = Vector3.Dot(c - a, q) / determinant;
        return t >= 0 ? t : null;
    }

    /// <summary>
    /// Whether the ray from <paramref name="origin"/> along <paramref name="direction"/> meets the box within <paramref name="distance"/>.
    /// </summary>
    internal static bool RayBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, float distance)
    {
        float near = 0, far = distance;

        for (int i = 0; i < 3; i++)
        {
            if (MathF.Abs(direction[i]) < 1e-9f)
            {
                if (origin[i] < min[i] || origin[i] > max[i]) return false;

                continue;
            }

            float a = (min[i] - origin[i]) / direction[i], b = (max[i] - origin[i]) / direction[i];
            near = MathF.Max(near, MathF.Min(a, b));
            far = MathF.Min(far, MathF.Max(a, b));
            if (near > far) return false;
        }

        return true;
    }
}