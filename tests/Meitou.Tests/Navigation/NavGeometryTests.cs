using System.Numerics;
using Meitou.Navigation;

namespace Meitou.Tests.Navigation;

/// <summary>The plane geometry the builder and queries share (<see cref="NavGeometry"/>).</summary>
public class NavGeometryTests
{
    /// <summary>
    /// A door painter's size polygon (about 3 by 1.5 units) at world coordinates near 5e4 and -1e5 keeps its winding: summed in float from the origin,
    /// the products round by hundreds of square units there and these came out 0, -512 and 512 (docs/game/pathfinding.md, "Door painters").
    /// </summary>
    [Theory]
    [InlineData(50013.37f, -100021.6f)]
    [InlineData(49871.9f, -99942.3f)]
    [InlineData(50240.25f, -100377.8f)]
    public void The_signed_area_of_a_small_polygon_far_from_the_origin_has_the_sign_of_its_winding(float x, float z)
    {
        Vector2[] ccw = [new(x, z), new(x + 3.2f, z + 0.4f), new(x + 2.9f, z + 1.9f), new(x - 0.3f, z + 1.5f)];
        Vector2[] cw = [.. ccw.Reverse()];

        // Twice the area of the quad, as summed at the origin: 9.84 square units (float coordinates there round by under 0.01).
        Assert.InRange(NavGeometry.SignedArea(ccw), 9.7f, 10f);
        Assert.InRange(NavGeometry.SignedArea(cw), -10f, -9.7f);
    }
}
