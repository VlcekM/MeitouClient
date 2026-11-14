using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Construction;

namespace Meitou.Tests.Simulation;

/// <summary>
/// The refit's 3D snap on faces that know their own surface height (docs/game/buildings-production.md "Round 11").
/// </summary>
public class RefitSnapTests
{
    /// <summary>
    /// A square face whose corners are all at height 0 stands on a slope y = x / 2: the 3D closest point lies on the slope, not on the corners' plane.
    /// </summary>
    [Fact]
    public void A_face_with_its_own_surface_height_is_snapped_to_that_surface_not_to_its_corner_plane()
    {
        Vector3[] corners = [new(0, 0, 0), new(0, 0, 200), new(200, 0, 200), new(200, 0, 0)];
        var flat = new SurfaceFace(corners, new bool[4], false);
        var sloped = flat with { SurfaceHeight = (x, _) => x / 2 };
        var centre = new Vector3(100, 60, 100);

        var onPlane = RefitSnap.Closest([flat], centre)!.Value;
        var onSlope = RefitSnap.Closest([sloped], centre)!.Value;

        var normal = Vector3.Normalize(new Vector3(-0.5f, 1, 0));
        var expected = centre - Vector3.Dot(centre - new Vector3(100, 50, 100), normal) * normal;
        Assert.Equal(0, onPlane.Y, 0.01f);
        Assert.True(Vector3.Distance(onSlope, expected) < 0.05f, $"{onSlope} against {expected}");
        Assert.Equal(onSlope.X / 2, onSlope.Y, 0.05f);
    }
}
