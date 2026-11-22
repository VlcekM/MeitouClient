using System.Collections.Concurrent;
using System.Numerics;

namespace Meitou.Data.World;

/// <summary>
/// The placed buildings' group-9 surfaces a ground ray meets (<see cref="BuildingShape.GroundTriangles"/>), read once per placement id and state,
/// with the X/Z box round each; safe from several threads.
/// </summary>
public sealed class GroundShapes(GameDatabase db, InteriorCollisions collisions)
{
    /// <summary>
    /// The shapes read, by placement id, place and whether the building is a ruin.
    /// </summary>
    readonly ConcurrentDictionary<(string Id, Vector3 Position, Quaternion Rotation, bool Destroyed), Shape> _shapes = new();

    /// <summary>
    /// A placed building's group-9 triangles and the X/Z box round them.
    /// </summary>
    sealed record Shape(Vector3[] Corners, Vector2 Min, Vector2 Max);

    /// <summary>
    /// Where a ground ray cast straight down over <paramref name="at"/> (X, Z) from height <paramref name="from"/> meets the placed building's group-9
    /// surfaces first; null when none lies under it. The building is read the first time it is asked for at that place in that state.
    /// </summary>
    public GroundHit? Hit(GameRecord building, string id, Vector3 position, Quaternion rotation, bool destroyed, Vector2 at,
        float from = float.PositiveInfinity)
    {
        var shape = _shapes.GetOrAdd((id, position, rotation, destroyed), _ => Read(building, id, position, rotation, destroyed));
        if (at.X < shape.Min.X || at.Y < shape.Min.Y || at.X > shape.Max.X || at.Y > shape.Max.Y) return null;

        return BuildingShape.TopOf(shape.Corners, at, from);
    }

    /// <summary>
    /// Reads a placed building's group-9 triangles and their X/Z box.
    /// </summary>
    Shape Read(GameRecord building, string id, Vector3 position, Quaternion rotation, bool destroyed)
    {
        var corners = BuildingShape.GroundTriangles(db, building, id, position, rotation, destroyed, collisions);
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var c in corners)
        {
            min = Vector2.Min(min, new Vector2(c.X, c.Z));
            max = Vector2.Max(max, new Vector2(c.X, c.Z));
        }

        return new Shape(corners, min, max);
    }
}
