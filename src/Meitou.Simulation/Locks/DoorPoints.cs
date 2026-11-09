using System.Numerics;

namespace Meitou.Simulation.Locks;

/// <summary>
/// A door's two points (<c>FUN_14029cc90</c>, <b>Verified</b>): 15 either side of the leaf along its way through; the outer one is the side away from
/// the building's origin (which of the two the original calls outer is <b>Unknown</b>). Shared by state 126 (<c>FUN_140596e90</c>) and the town's
/// guard nodes (<c>FUN_14038b650</c>), which both extend it by 1.1 times the distance between the points.
/// </summary>
public static class DoorPoints
{
    /// <summary>
    /// The leaf's distance to each of its points.
    /// </summary>
    public const float Offset = 15;
    /// <summary>
    /// The factor of the points' distance by which the outer point is pushed out (<c>DAT_14169bb44</c>).
    /// </summary>
    public const float OuterFactor = 1.1f;

    /// <summary>
    /// The horizontal unit way through the door, pointing away from <paramref name="origin"/>, the building's position.
    /// </summary>
    public static Vector2 Away(DoorSpot spot, Vector3 origin)
    {
        var n = new Vector2(spot.Normal.X, spot.Normal.Z);
        n = n.LengthSquared() < 1e-6f ? Vector2.UnitY : Vector2.Normalize(n);
        return Vector2.Dot(new Vector2(spot.Centre.X - origin.X, spot.Centre.Z - origin.Z), n) < 0 ? -n : n;
    }

    /// <summary>
    /// The outer point pushed out by 1.1 times the distance between the points: 15 + 33 from the leaf.
    /// </summary>
    public static Vector2 Outer(DoorSpot spot, Vector3 origin) =>
        new Vector2(spot.Centre.X, spot.Centre.Z) + Away(spot, origin) * (Offset + 2 * Offset * OuterFactor);
}