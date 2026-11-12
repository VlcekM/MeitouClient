using System.Numerics;

namespace Meitou.Data.World;

/// <summary>
/// An upright prism: a convex footprint in world X/Z (counter-clockwise) between two heights, what a placement ghost fills. Tells whether a triangle
/// cuts into it by the separating axis test (engine stand-in for the original's footprint node boxes against physics shapes,
/// docs/game/buildings-production.md "Placement").
/// </summary>
public sealed class FootprintPrism
{
    /// <summary>
    /// The footprint's corners in X/Z.
    /// </summary>
    readonly Vector2[] _corners;
    /// <summary>
    /// The prism's bottom and top heights.
    /// </summary>
    readonly float _bottom, _top;
    /// <summary>
    /// The footprint's X/Z box.
    /// </summary>
    readonly Vector2 _min, _max;
    /// <summary>
    /// The candidate separating axes that depend only on the prism: up and each footprint edge's horizontal normal (unit length).
    /// </summary>
    readonly Vector3[] _ownAxes;
    /// <summary>
    /// The footprint edges' directions (horizontal), crossed with each triangle edge for the remaining axes.
    /// </summary>
    readonly Vector3[] _edges;

    /// <summary>
    /// The prism of <paramref name="footprint"/> (convex, counter-clockwise) from <paramref name="bottom"/> to <paramref name="top"/>.
    /// </summary>
    public FootprintPrism(Vector2[] footprint, float bottom, float top)
    {
        (_corners, _bottom, _top) = (footprint, bottom, top);
        (_min, _max) = (footprint[0], footprint[0]);
        foreach (var p in footprint) (_min, _max) = (Vector2.Min(_min, p), Vector2.Max(_max, p));

        var axes = new List<Vector3> { Vector3.UnitY };
        var edges = new List<Vector3>();
        for (int i = 0; i < footprint.Length; i++)
        {
            var e = footprint[(i + 1) % footprint.Length] - footprint[i];
            if (e.LengthSquared() < 1e-8f) continue;

            edges.Add(new Vector3(e.X, 0, e.Y));
            axes.Add(Vector3.Normalize(new Vector3(-e.Y, 0, e.X)));
        }

        (_ownAxes, _edges) = ([.. axes], [.. edges]);
    }

    /// <summary>
    /// Whether the X/Z box <paramref name="min"/>..<paramref name="max"/> and heights <paramref name="yMin"/>..<paramref name="yMax"/> of some
    /// triangles reach the prism's box: the cheap test before theirs.
    /// </summary>
    public bool Reaches(Vector2 min, Vector2 max, float yMin, float yMax) =>
        min.X <= _max.X && max.X >= _min.X && min.Y <= _max.Y && max.Y >= _min.Y && yMin <= _top && yMax >= _bottom;

    /// <summary>
    /// Whether triangle (<paramref name="a"/>, <paramref name="b"/>, <paramref name="c"/>) reaches more than <paramref name="tolerance"/> into the
    /// prism along every axis: a triangle that only touches its sides, or grazes them by less, does not cut it.
    /// </summary>
    public bool Cuts(Vector3 a, Vector3 b, Vector3 c, float tolerance)
    {
        var lo = Vector3.Min(a, Vector3.Min(b, c));
        var hi = Vector3.Max(a, Vector3.Max(b, c));
        if (!Reaches(new Vector2(lo.X, lo.Z), new Vector2(hi.X, hi.Z), lo.Y, hi.Y)) return false;

        foreach (var axis in _ownAxes)
            if (Apart(axis, a, b, c, tolerance)) return false;

        var normal = Vector3.Cross(b - a, c - a);
        if (normal.LengthSquared() > 1e-12f && Apart(Vector3.Normalize(normal), a, b, c, tolerance)) return false;

        return !CrossAxesApart(a, b, c, tolerance);
    }

    /// <summary>
    /// Whether an axis made of a triangle edge crossed with a footprint edge or with up separates the triangle from the prism.
    /// </summary>
    bool CrossAxesApart(Vector3 a, Vector3 b, Vector3 c, float tolerance)
    {
        ReadOnlySpan<Vector3> sides = [b - a, c - b, a - c];
        foreach (var side in sides)
        {
            if (Separates(Vector3.Cross(side, Vector3.UnitY), a, b, c, tolerance)) return true;

            foreach (var edge in _edges)
                if (Separates(Vector3.Cross(side, edge), a, b, c, tolerance)) return true;
        }

        return false;
    }

    /// <summary>
    /// <see cref="Apart"/> on <paramref name="axis"/> made unit length; false for a degenerate axis (parallel edges give none).
    /// </summary>
    bool Separates(Vector3 axis, Vector3 a, Vector3 b, Vector3 c, float tolerance)
    {
        float length = axis.Length();
        return length > 1e-6f && Apart(axis / length, a, b, c, tolerance);
    }

    /// <summary>
    /// Whether the triangle reaches no more than <paramref name="tolerance"/> into the prism's span on the unit <paramref name="axis"/>, from
    /// either end: a flat triangle's span along its normal is a point, so it is how deep that point lies, not the spans' overlap.
    /// </summary>
    bool Apart(Vector3 axis, Vector3 a, Vector3 b, Vector3 c, float tolerance)
    {
        float da = Vector3.Dot(axis, a), db = Vector3.Dot(axis, b), dc = Vector3.Dot(axis, c);
        float triangleMin = MathF.Min(da, MathF.Min(db, dc)), triangleMax = MathF.Max(da, MathF.Max(db, dc));

        float prismMin = float.MaxValue, prismMax = float.MinValue;
        foreach (var p in _corners)
        {
            float flat = axis.X * p.X + axis.Z * p.Y;
            float low = flat + axis.Y * (axis.Y >= 0 ? _bottom : _top), high = flat + axis.Y * (axis.Y >= 0 ? _top : _bottom);
            (prismMin, prismMax) = (MathF.Min(prismMin, low), MathF.Max(prismMax, high));
        }

        return MathF.Min(triangleMax - prismMin, prismMax - triangleMin) <= tolerance;
    }
}
