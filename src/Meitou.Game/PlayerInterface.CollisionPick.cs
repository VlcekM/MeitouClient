using System.Numerics;

namespace Meitou.Game;

/// <summary>
/// Picking buildings against their collision triangles (docs/game/ui-input.md "Building picking and selection"): the original's pick is a raycast against the collision shapes, not a prism
/// over their merged hull. The hull prism stays as the first, cheap test; a building that has collision files is then tested triangle by triangle, so ground, characters and neighbours
/// next to a building are not taken for it.
/// </summary>
sealed partial class PlayerInterface
{
    readonly Dictionary<string, IReadOnlyList<(Vector3[] Vertices, int[] Indices)>> collisionMeshes = [];

    IReadOnlyList<(Vector3[] Vertices, int[] Indices)> CollisionMeshOf(string record)
    {
        if (collisionMeshes.TryGetValue(record, out var known)) return known;
        var mesh = buildHost?.Placement.CollisionMesh(record) ?? [];
        collisionMeshes[record] = mesh;
        return mesh;
    }

    /// <summary>
    /// Where the ray meets a building: its hull prism first (null: no hit), then, when the record has collision triangles, the nearest of
    /// those (null: the ray passes through the prism without touching the building). <paramref name="y"/> is the building's base height.
    /// </summary>
    internal float? BuildingRayT((Vector3 Origin, Vector3 Direction) ray, Vector2[] hull, float yMin, float yMax, string? record, Vector3 position, float y, Quaternion rotation)
    {
        if (RayPrism(ray.Origin, ray.Direction, hull, y + yMin, y + yMax) is not { } prism) return null;
        if (record is null) return prism;
        var mesh = CollisionMeshOf(record);
        return mesh.Count == 0 ? prism : RayMesh(ray, mesh, new Vector3(position.X, y, position.Z), rotation);
    }

    /// <summary>
    /// Where the ray meets a building without collision (a farm) by its plot (<see cref="Building.PlacementServices.Plot"/>, an engine stand-in): just before the ground, when the ground
    /// the ray meets at <paramref name="ground"/> lies inside the placed hull; null otherwise.
    /// </summary>
    static float? PlotT((Vector3 Origin, Vector3 Direction) ray, Vector2[] hull, float? ground)
    {
        if (ground is not { } t) return null;
        var at = ray.Origin + ray.Direction * t;
        // Inside a convex polygon of either winding: on the same side of every edge.
        float sign = 0;
        for (int i = 0; i < hull.Length; i++)
        {
            var e = hull[(i + 1) % hull.Length] - hull[i];
            float side = e.X * (at.Z - hull[i].Y) - e.Y * (at.X - hull[i].X);
            if (side == 0) continue;
            if (sign == 0) sign = MathF.Sign(side);
            else if (MathF.Sign(side) != sign) return null;
        }
        return t - 0.01f;
    }

    /// <summary>The nearest hit of the ray on the triangles (double sided), the mesh turned by <paramref name="rotation"/> and put at <paramref name="at"/>; null for none.</summary>
    static float? RayMesh((Vector3 Origin, Vector3 Direction) ray, IReadOnlyList<(Vector3[] Vertices, int[] Indices)> mesh, Vector3 at, Quaternion rotation)
    {
        var q = rotation.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(rotation);
        var inverse = Quaternion.Conjugate(q);
        var origin = Vector3.Transform(ray.Origin - at, inverse);
        var direction = Vector3.Transform(ray.Direction, inverse);
        float? best = null;
        foreach (var (vertices, indices) in mesh)
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                var a = vertices[indices[t]];
                var e1 = vertices[indices[t + 1]] - a;
                var e2 = vertices[indices[t + 2]] - a;
                var p = Vector3.Cross(direction, e2);
                float determinant = Vector3.Dot(e1, p);
                if (MathF.Abs(determinant) < 1e-9f) continue;
                float inv = 1 / determinant;
                var s = origin - a;
                float u = Vector3.Dot(s, p) * inv;
                if (u < 0 || u > 1) continue;
                var r = Vector3.Cross(s, e1);
                float v = Vector3.Dot(direction, r) * inv;
                if (v < 0 || u + v > 1) continue;
                float distance = Vector3.Dot(e2, r) * inv;
                if (distance > 0 && (best is null || distance < best)) best = distance;
            }
        return best;
    }
}
