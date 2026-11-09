using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation.Navigation;

namespace Meitou.Tests.Navigation;

/// <summary>
/// Gate codes (docs/game/pathfinding.md "Gate codes"): the regions a gate's door polygons close off.
/// </summary>
public class GateRegionTests
{
    [Fact]
    public void A_gate_walls_off_the_pocket_behind_it_from_the_open_world()
    {
        // A strip of five quads along X in one zone: two outside (the first on the zone's west edge, whose neighbour is not loaded),
        // the gate's door, two inside (ending in the middle of the zone).
        var zone = new ZoneCoordinate(10, 10);
        var (ox, oz) = WorldLayout.ZoneOrigin(zone);
        float x0 = (float)ox, z0 = (float)oz + 1000, z1 = z0 + 100;
        float[] xs = [0, 100, 200, 210, 310, 410];
        var vertices = new List<Vector3>();
        foreach (float x in xs)
        {
            vertices.Add(new(x0 + x, 0, z0));
            vertices.Add(new(x0 + x, 0, z1));
        }
        int n = xs.Length - 1;
        var polygons = new int[n][];
        var neighbours = new int[n][];
        var areas = new byte[n];
        for (int i = 0; i < n; i++)
        {
            polygons[i] = [2 * i, 2 * i + 2, 2 * i + 3, 2 * i + 1];
            neighbours[i] = [-1, i + 1 < n ? i + 1 : -1, -1, i - 1];
            areas[i] = i == 2 ? NavArea.Door : NavArea.Ground;
        }
        var mesh = new ZoneNavMesh
        {
            ZoneX = zone.X, ZoneZ = zone.Y, Vertices = [.. vertices], Polygons = polygons, Neighbours = neighbours, Areas = areas,
            Kept = Enumerable.Repeat(true, n).ToArray(),
            BoundsMin = new((float)ox, (float)oz),
            BoundsMax = new((float)ox + WorldLayout.ZoneSize, (float)oz + WorldLayout.ZoneSize),
            DoorIds = ["gate"], DoorOf = [-1, -1, 0, -1, -1],
        };
        var world = NavWorld.Empty.With(mesh);
        Vector3 At(float x) => new(x0 + x, 0, (z0 + z1) / 2);

        var regions = NavGateRegions.Label(world, id => id == "gate");
        Assert.Equal(1, regions.RegionCount);
        var gate = Assert.Single(regions.Gates);
        Assert.Equal("gate", gate.Building);
        Assert.Equal([0, 1], gate.Codes);
        Assert.Equal(1, regions.CodeAt(At(300)));
        Assert.Equal(0, regions.CodeAt(At(50)));
        Assert.Equal(-1, regions.CodeAt(At(2000)));

        // An ordinary door walls nothing in.
        var plain = NavGateRegions.Label(world, _ => false);
        Assert.Equal(0, plain.RegionCount);
        Assert.Equal(0, plain.CodeAt(At(300)));
    }
}
