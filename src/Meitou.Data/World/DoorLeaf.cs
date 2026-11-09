using System.Numerics;
using Meitou.Data.Physics;

namespace Meitou.Data.World;

/// <summary>
/// How a door leaf moves (docs/game/locks.md "Doors"): doors slide, they do not swing. <see cref="Speed"/> is open amount per second; the leaf moves
/// <see cref="Distance"/> units along its collision box's local <see cref="Axis"/> (0 X, 1 Y, 2 Z, PhysX's Z-up frame), first stepping
/// <see cref="ComesOut"/> units out of the wall when that is not 0.
/// </summary>
/// <param name="Axis">BUILDING <c>door axis</c>.</param>
/// <param name="Distance">BUILDING <c>door move dist</c> (signed).</param>
/// <param name="Speed">BUILDING <c>door move speed</c>.</param>
/// <param name="ComesOut">BUILDING <c>door comes out</c> (signed).</param>
public readonly record struct DoorLeaf(int Axis, float Distance, float Speed, float ComesOut)
{
    /// <summary>
    /// The navmesh counts a door as open from this open amount on (docs/game/locks.md "Doors", <b>Verified</b>).
    /// </summary>
    public const float NavOpen = 0.6f;

    /// <summary>
    /// The fields of a door's BUILDING record (the fcs.def defaults when absent: axis 0, 30 units, 1 per second, no step out).
    /// </summary>
    public static DoorLeaf Of(GameRecord door) =>
        new(Math.Clamp(door.GetInt("door axis"), 0, 2), door.GetFloat("door move dist", 30), door.GetFloat("door move speed", 1),
            door.GetFloat("door comes out"));

    /// <summary>
    /// The leaf's displacement at open amount <paramref name="doa"/> in its part's local frame (<b>Verified</b> formula, kenshi_x64
    /// <c>FUN_140298d10</c>): without a step out, <c>slide x Distance x doa</c>; with one, the leaf first moves out along <c>out</c> until
    /// <c>t = |ComesOut| / (0.25 |Distance| + |ComesOut|)</c>, then slides: <c>slide x Distance x (doa - t) / (1 - t) + out x ComesOut</c>.
    /// <c>slide</c> is the box's axis <see cref="Axis"/>; <c>out</c> its thinnest other axis, pointing away from the part's origin (<b>Observed</b>:
    /// the original dot-tests against the shape position; the reference point is not decoded).
    /// </summary>
    public Vector3 LocalOffset(float doa, DoorFrame frame)
    {
        var slide = frame.Axis(Axis);
        if (ComesOut == 0) return slide * (Distance * doa);
        float t = MathF.Abs(ComesOut) / (0.25f * MathF.Abs(Distance) + MathF.Abs(ComesOut));
        int o = -1;
        for (int k = 0; k < 3; k++)
            if (k != Axis && (o < 0 || Component(frame.HalfExtents, k) < Component(frame.HalfExtents, o))) o = k;
        var outward = frame.Axis(o) * (Vector3.Dot(frame.Centre, frame.Axis(o)) < 0 ? -1 : 1);
        return doa < t ? outward * (ComesOut * doa / t) : slide * (Distance * (doa - t) / (1 - t)) + outward * ComesOut;
    }

    /// <summary>
    /// Component <paramref name="axis"/> (0 X, 1 Y, 2 Z) of <paramref name="v"/>.
    /// </summary>
    static float Component(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };
}

/// <summary>
/// A door leaf's collision box as its motion needs it, in the part's (Y-up) frame: the box's three local axes, its half extents along them and its
/// centre. The collision files are PhysX's Z-up frame (<see cref="CollisionTriangulator.ToWorldAxes"/>), so a box with no rotation has axis 1 along
/// -Z and axis 2 up (docs/game/locks.md "Doors", <b>Observed</b>).
/// </summary>
public readonly record struct DoorFrame(Vector3 X, Vector3 Y, Vector3 Z, Vector3 HalfExtents, Vector3 Centre)
{
    /// <summary>
    /// An unrotated box with no size at the origin (a door part without a collision box).
    /// </summary>
    public static DoorFrame Default { get; } = Of(Pose.Identity, Vector3.Zero);

    /// <summary>
    /// The frame of a box shape with pose <paramref name="pose"/> (its actor's and its own, composed) and half extents
    /// <paramref name="halfExtents"/>.
    /// </summary>
    public static DoorFrame Of(Pose pose, Vector3 halfExtents) => new(
        CollisionTriangulator.ToWorldAxes(pose.Rotate(Vector3.UnitX)), CollisionTriangulator.ToWorldAxes(pose.Rotate(Vector3.UnitY)),
        CollisionTriangulator.ToWorldAxes(pose.Rotate(Vector3.UnitZ)), halfExtents, CollisionTriangulator.ToWorldAxes(pose.Translation));

    /// <summary>
    /// The first box of a door part's collision file, or <see cref="Default"/> when it has none.
    /// </summary>
    public static DoorFrame Of(CollisionFile? file) =>
        file?.Shapes.FirstOrDefault(s => s.Kind == CollisionShapeKind.Box) is { } box ? Of(box.Pose, box.HalfExtents) : Default;

    /// <summary>
    /// The box's local axis <paramref name="axis"/> (0 X, 1 Y, 2 Z).
    /// </summary>
    public Vector3 Axis(int axis) => axis switch { 0 => X, 1 => Y, _ => Z };
}