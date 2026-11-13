using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>
/// A building's roofs and ceilings as drawn: the near-level triangles of its parts' render meshes, in world space. Engine stand-in for a cover the
/// collision lacks: the upper storey of a Swamp House has collision for its floor only, so a tall ghost on that floor went up through the drawn roof
/// (feedback #603).
/// </summary>
public sealed class RoofCover
{
    /// <summary>
    /// One part's kept triangles (three corners each) with their X/Z box and height range.
    /// </summary>
    sealed record Part(Vector3[] Corners, Vector2 Min, Vector2 Max, float YMin, float YMax);

    /// <summary>
    /// The parts with a near-level triangle.
    /// </summary>
    readonly List<Part> _parts = [];

    /// <summary>
    /// The steepest triangle kept, in degrees from level (engine choice, as <see cref="FloorSurfaces"/> tells a slab's underside): walls are left
    /// to the collision.
    /// </summary>
    const float SteepestDegrees = 40;

    /// <summary>
    /// The drawn cover of a building placed at <paramref name="position"/> / <paramref name="rotation"/>: its own parts (not its doors), leaving out
    /// <c>passable</c> parts; none for a <c>path mode</c> IGNORE building or furniture (as <see cref="FloorSurfaces.For"/>).
    /// </summary>
    public static RoofCover For(GameDatabase db, GameRecord building, string placementId, Vector3 position, Quaternion rotation,
        InteriorCollisions meshes)
    {
        var result = new RoofCover();
        if (building.GetInt("path mode", 2) == 0 || building.GetBool("is exterior furniture") || building.GetBool("is interior furniture"))
            return result;

        var state = new BuildingState { WithInterior = true };
        foreach (var p in WorldObjectLayout.Building(db, building, placementId, position, rotation, state))
        {
            if (p.Owner != building || p.Source.GetBool("passable")) continue;
            if (Level(meshes.RenderTriangles(p.MeshPath), p.Transform) is { } part) result._parts.Add(part);
        }

        return result;
    }

    /// <summary>
    /// Whether a kept triangle cuts into <paramref name="prism"/> by more than <paramref name="tolerance"/> (<see cref="FootprintPrism.Cuts"/>).
    /// </summary>
    public bool Cuts(FootprintPrism prism, float tolerance)
    {
        foreach (var part in _parts)
        {
            if (!prism.Reaches(part.Min, part.Max, part.YMin, part.YMax)) continue;

            for (int i = 0; i + 2 < part.Corners.Length; i += 3)
                if (prism.Cuts(part.Corners[i], part.Corners[i + 1], part.Corners[i + 2], tolerance)) return true;
        }

        return false;
    }

    /// <summary>
    /// The near-level triangles of <paramref name="corners"/> placed with <paramref name="transform"/>; null when there are none.
    /// </summary>
    static Part? Level(Vector3[] corners, Matrix4x4 transform)
    {
        float steepest = MathF.Cos(SteepestDegrees * MathF.PI / 180);
        var kept = new List<Vector3>();
        for (int i = 0; i + 2 < corners.Length; i += 3)
        {
            var a = Vector3.Transform(corners[i], transform);
            var b = Vector3.Transform(corners[i + 1], transform);
            var c = Vector3.Transform(corners[i + 2], transform);
            var normal = Vector3.Cross(b - a, c - a);
            if (normal.LengthSquared() < 1e-10f || MathF.Abs(normal.Y) < normal.Length() * steepest) continue;

            kept.AddRange([a, b, c]);
        }

        if (kept.Count == 0) return null;

        var min = new Vector2(kept.Min(v => v.X), kept.Min(v => v.Z));
        var max = new Vector2(kept.Max(v => v.X), kept.Max(v => v.Z));
        return new Part([.. kept], min, max, kept.Min(v => v.Y), kept.Max(v => v.Y));
    }
}
