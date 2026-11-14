using System.Numerics;
using System.Text.Json;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Navigation;

namespace Meitou.Tests.Navigation;

/// <summary>
/// The debugging read of the navmesh round a point (the control channel's <c>navnear</c>): its faces and the closest point the town refit's snap takes.
/// </summary>
public class NavNearTests
{
    /// <summary>
    /// Two quads 100 apart in one zone: the near one is listed with its vertices and walkable flag, the far one is outside a small radius, and a point
    /// 30 units off the near quad snaps onto it.
    /// </summary>
    [Fact]
    public void Faces_within_the_radius_and_the_snap_of_a_point_beside_them()
    {
        var zone = new ZoneCoordinate(10, 10);
        var (ox, oz) = WorldLayout.ZoneOrigin(zone);
        float x0 = (float)ox + 1000, z0 = (float)oz + 1000;
        Vector3[] vertices = [new(x0, 0, z0), new(x0 + 100, 0, z0), new(x0 + 100, 0, z0 + 100), new(x0, 0, z0 + 100),
            new(x0 + 1000, 0, z0), new(x0 + 1100, 0, z0), new(x0 + 1100, 0, z0 + 100), new(x0 + 1000, 0, z0 + 100)];
        var mesh = new ZoneNavMesh
        {
            ZoneX = zone.X, ZoneZ = zone.Y, Vertices = vertices, Polygons = [[0, 1, 2, 3], [4, 5, 6, 7]], Neighbours = [[-1, -1, -1, -1], [-1, -1, -1, -1]],
            Areas = [NavArea.Ground, NavArea.Ground], Kept = [true, true],
            BoundsMin = new((float)ox, (float)oz), BoundsMax = new((float)ox + WorldLayout.ZoneSize, (float)oz + WorldLayout.ZoneSize),
        };
        var walk = new NavmeshWalkability((_, _) => 300);
        walk.SetWorld(NavWorld.Empty.With(mesh));

        using var json = JsonDocument.Parse(NavNear.Json(walk, x0 - 30, z0 + 50, 200));
        var root = json.RootElement;
        var faces = root.GetProperty("faces");
        var snap = root.GetProperty("snap");

        Assert.Equal(1, faces.GetArrayLength());
        Assert.True(faces[0].GetProperty("walkable").GetBoolean());
        Assert.Equal((int)NavArea.Ground, faces[0].GetProperty("area").GetInt32());
        Assert.Equal(4, faces[0].GetProperty("vertices").GetArrayLength());
        Assert.True(root.GetProperty("zones")[0].GetProperty("loaded").GetBoolean());
        Assert.False(snap.GetProperty("pointWalkable").GetBoolean());
        Assert.True(snap.GetProperty("found").GetBoolean());
        Assert.InRange(snap.GetProperty("x").GetDouble(), x0, x0 + 100);
        Assert.InRange(snap.GetProperty("distance").GetDouble(), 30, 40);
    }
}
