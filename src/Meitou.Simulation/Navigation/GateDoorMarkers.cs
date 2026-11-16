using System.Numerics;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// A gate's side points from its first door (docs/game/pathfinding.md "Gate codes"): the door's inner and outer markers (<c>FUN_14029cc90</c>) pushed
/// apart by half their distance (<c>FUN_1402eab70</c>).
/// </summary>
internal static class GateDoorMarkers
{
    /// <summary>
    /// How far either marker starts from the door shape's centre, across the door (<b>Verified</b>, <c>FUN_14029cc90</c>: 15).
    /// </summary>
    public const float MarkerOffset = 15;

    /// <summary>
    /// A marker less than this beyond the centre on its own side, along the door's tilted normal, is moved to <see cref="MarkerDepth"/>
    /// (<b>Verified</b>, <c>FUN_14029cc90</c>: 5).
    /// </summary>
    public const float MarkerMinDepth = 5;

    /// <summary>
    /// Where a marker too near the centre is moved to along the tilted normal (<b>Verified</b>, <c>FUN_14029cc90</c>: 10).
    /// </summary>
    public const float MarkerDepth = 10;

    /// <summary>
    /// The fraction of the marker-to-marker distance each side point lies beyond its marker (<b>Verified</b>, <c>FUN_1402eab70</c>'s 0.5).
    /// </summary>
    public const float Spread = 0.5f;

    /// <summary>
    /// A downward physics ray from a point: the height of the nearest hit at or below it, false for none.
    /// </summary>
    public delegate bool GroundRay(Vector3 from, out float y);

    /// <summary>
    /// The gate's side points: the inner marker dropped onto floor-0 building parts only (<paramref name="floor0"/>, mask <c>0x200</c>), the outer
    /// onto the terrain or floor-0 parts (<paramref name="ground"/>, mask <c>0x204</c>), each staying where it was on a miss.
    /// </summary>
    /// <param name="door">The door's BUILDING record (<c>door navmesh axis</c>).</param>
    /// <param name="frame">The door's first collision box (its shape 0's pose).</param>
    /// <param name="node">The door's placement (its parent building's).</param>
    public static (Vector3 First, Vector3 Second) Sides(GameRecord door, DoorFrame frame, Matrix4x4 node, GroundRay floor0, GroundRay ground)
    {
        var (inner, outer) = Markers(door, frame, node, floor0, ground);
        var apart = inner - outer;
        return (inner + apart * Spread, outer - apart * Spread);
    }

    /// <summary>
    /// The door's outer marker (<c>+0x3bc</c>), where its building reads its own gate code (<c>FUN_140555fd0</c>).
    /// </summary>
    public static Vector3 Outer(GameRecord door, DoorFrame frame, Matrix4x4 node, GroundRay floor0, GroundRay ground) =>
        Markers(door, frame, node, floor0, ground).Outer;

    /// <summary>
    /// The door's inner (<c>+0x3c8</c>) and outer (<c>+0x3bc</c>) markers, <c>FUN_14029cc90</c>.
    /// </summary>
    static (Vector3 Inner, Vector3 Outer) Markers(GameRecord door, DoorFrame frame, Matrix4x4 node, GroundRay floor0, GroundRay ground)
    {
        int axis = Math.Clamp(door.GetInt("door navmesh axis"), 0, 2);
        var along = Vector3.Normalize(Vector3.TransformNormal(frame.Axis(axis), node));
        var centre = Vector3.Transform(frame.Centre, node);

        // The markers start either side of the shape's centre, across the door's axis seen from above, and drop onto the ground.
        var across = Vector3.Cross(along, Vector3.UnitY);
        var inner = Drop(centre - across * MarkerOffset, floor0);
        var outer = Drop(centre + across * MarkerOffset, ground);

        // Then each is pushed out along the door's normal under its own up axis when it lies too near the centre on its side.
        var normal = Vector3.Cross(along, Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, node)));
        float innerDepth = Vector3.Dot(inner - centre, normal);
        if (innerDepth > -MarkerMinDepth) inner -= normal * (innerDepth + MarkerDepth);

        float outerDepth = Vector3.Dot(outer - centre, normal);
        if (outerDepth < MarkerMinDepth) outer += normal * (MarkerDepth - outerDepth);

        return (inner, outer);
    }

    /// <summary>
    /// <paramref name="at"/> moved down onto the ray's hit, or unchanged when it hits nothing.
    /// </summary>
    static Vector3 Drop(Vector3 at, GroundRay ray) => ray(at, out float y) ? at with { Y = y } : at;
}
