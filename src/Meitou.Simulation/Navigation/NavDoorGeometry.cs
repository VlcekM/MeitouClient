using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Where a building's doors are: the box of door i's collision (the shapes the navmesh paints as the door, docs/game/pathfinding.md) in the
/// building's frame, turned to the leaf's own line (<see cref="Fit"/>), scaled by the building's <c>scale</c>, not otherwise rotated or moved. Read
/// once per (BUILDING, door) from the data; safe from any thread.
/// </summary>
public sealed class NavDoorGeometry(GameInstall install, GameDatabase db)
{
    /// <summary>
    /// The collision files read so far.
    /// </summary>
    readonly CollisionCache _collision = new(install);
    /// <summary>
    /// The door boxes by (BUILDING id, door index), read once each; null for a door with no collision.
    /// </summary>
    readonly ConcurrentDictionary<(string Building, int Door), DoorSpot?> _spots = new();
    /// <summary>
    /// The door collision triangles by (BUILDING id, door index), read once each; empty for a door with none.
    /// </summary>
    readonly ConcurrentDictionary<(string Building, int Door), IReadOnlyList<(Vector3[] Vertices, int[] Indices)>> _shapes = new();

    /// <summary>
    /// The leaf's centre, the normal of its thinner horizontal side and half its width along the other (null: no collision found).
    /// </summary>
    public DoorSpot? Spot(string buildingRecordId, int doorIndex) => _spots.GetOrAdd((buildingRecordId, doorIndex), Find);

    /// <summary>
    /// Door i's collision triangles in the same frame as <see cref="Spot"/> (the leaf closed), for the pointer's ray (docs/game/ui-input.md "Building
    /// picking and selection"); empty when it has none.
    /// </summary>
    public IReadOnlyList<(Vector3[] Vertices, int[] Indices)> Shape(string buildingRecordId, int doorIndex) =>
        _shapes.GetOrAdd((buildingRecordId, doorIndex), FindShape);

    /// <summary>
    /// Door <c>key.Door</c>'s collision triangles of the parts of BUILDING <c>key.Building</c>, scaled by its <c>scale</c> (1 when not positive);
    /// empty for a missing record or a door without collision.
    /// </summary>
    IReadOnlyList<(Vector3[] Vertices, int[] Indices)> FindShape((string Building, int Door) key)
    {
        if (db.Find(key.Building) is not { Type: FcsRecordType.Building } building) return [];

        float scale = building.GetFloat("scale", 1);
        if (scale <= 0) scale = 1;

        var result = new List<(Vector3[] Vertices, int[] Indices)>();
        foreach (var mesh in WorldObjectLayout.Building(db, building, "", Vector3.Zero, Quaternion.Identity))
        {
            if (mesh.DoorIndex != key.Door || _collision.Get(mesh.Source.GetPath("xml collision")) is not { } prepared)
                continue;

            foreach (var shape in prepared.Shapes)
                result.Add(([.. shape.Vertices.Select(v => v * scale)], shape.Indices));
        }

        return result;
    }

    /// <summary>
    /// The box of door <c>key.Door</c>'s collision shapes in the building's scaled frame, with the leaf's full-open slide as the renderer moves it;
    /// null for a missing record or a door without collision.
    /// </summary>
    DoorSpot? Find((string Building, int Door) key)
    {
        if (db.Find(key.Building) is not { Type: FcsRecordType.Building } building) return null;

        float scale = building.GetFloat("scale", 1);
        var node = Matrix4x4.CreateScale(scale <= 0 ? 1 : scale);
        var points = new List<Vector3>();
        Vector3? slide = null;
        foreach (var mesh in WorldObjectLayout.Building(db, building, "", Vector3.Zero, Quaternion.Identity))
        {
            if (mesh.DoorIndex != key.Door || _collision.Get(mesh.Source.GetPath("xml collision")) is not { } prepared)
                continue;

            // As the renderer moves the leaf: its full-open offset along the part's unit axes, in world units.
            if (slide is null)
            {
                var local = DoorLeaf.Of(mesh.Owner).LocalOffset(1, prepared.Door);
                var m = mesh.Transform;
                slide = Unit(m.M11, m.M12, m.M13) * local.X + Unit(m.M21, m.M22, m.M23) * local.Y + Unit(m.M31, m.M32, m.M33) * local.Z;
            }

            foreach (var shape in prepared.Shapes)
                points.AddRange(shape.Vertices.Select(v => Vector3.Transform(v, node)));
        }

        if (points.Count == 0) return null;

        return Fit(points) with { Slide = slide ?? Vector3.Zero };

        static Vector3 Unit(float x, float y, float z) =>
            new Vector3(x, y, z) is var v && v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.Zero;
    }

    /// <summary>
    /// The leaf's box from its collision points: the normal is the thinnest horizontal direction of their footprint, so a leaf turned in its building
    /// (the Saloon's, an outpost gate's) keeps its own line and width; a leaf along the building's axes gets its plain box, normal +X or +Z. Every
    /// door leaf of the base game is one box, whose footprint is its rectangle; an outline of several shapes could give a slanted side instead.
    /// </summary>
    internal static DoorSpot Fit(IReadOnlyList<Vector3> points)
    {
        var hull = NavGeometry.ConvexHull2D([.. points.Select(p => new Vector2(p.X, p.Z))]);
        float yLo = points.Min(p => p.Y);
        float yHi = points.Max(p => p.Y);
        var normal = Thinnest(hull);
        var along = new Vector2(-normal.Y, normal.X);
        var (nLo, nHi) = Extent(hull, normal);
        var (aLo, aHi) = Extent(hull, along);
        var centre = normal * ((nLo + nHi) / 2) + along * ((aLo + aHi) / 2);

        return new DoorSpot(new Vector3(centre.X, (yLo + yHi) / 2, centre.Y), new Vector3(normal.X, 0, normal.Y), (aHi - aLo) / 2,
            (yHi - yLo) / 2);
    }

    /// <summary>
    /// The unit normal of the hull's side across which the hull is thinnest (the thinnest box of a convex outline has a side along one of its sides),
    /// signed so its larger component is positive; +X for a hull of fewer than two points. A tie keeps the first side.
    /// </summary>
    static Vector2 Thinnest(Vector2[] hull)
    {
        var best = Vector2.UnitX;
        if (hull.Length < 2) return best;

        float bestWidth = float.MaxValue;
        for (int i = 0; i < hull.Length; i++)
        {
            var side = hull[(i + 1) % hull.Length] - hull[i];
            if (side.LengthSquared() < 1e-12f) continue;

            var n = Vector2.Normalize(new Vector2(-side.Y, side.X));
            var (lo, hi) = Extent(hull, n);
            if (hi - lo < bestWidth) (best, bestWidth) = (n, hi - lo);
        }

        float larger = MathF.Abs(best.X) >= MathF.Abs(best.Y) ? best.X : best.Y;
        return larger < 0 ? -best : best;
    }

    /// <summary>
    /// The lowest and highest of the hull's points along <paramref name="axis"/>.
    /// </summary>
    static (float Lo, float Hi) Extent(Vector2[] hull, Vector2 axis)
    {
        float lo = float.MaxValue;
        float hi = float.MinValue;
        foreach (var p in hull)
        {
            float d = Vector2.Dot(p, axis);
            lo = MathF.Min(lo, d);
            hi = MathF.Max(hi, d);
        }

        return (lo, hi);
    }
}
