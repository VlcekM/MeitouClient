using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation.Navigation;

namespace Meitou.Tests.Navigation;

/// <summary>
/// Gate codes (docs/game/pathfinding.md "Gate codes"): the gate-code pass over a gate's stored side points.
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
        Vector3 At(float x) => new(x0 + x, 0, (z0 + z1) / 2);

        var gated = Strip(zone, x0, z0, z1);
        gated.GateSides = [new GateSide("gate", At(260), At(150))];

        var regions = NavGateRegions.Label(NavWorld.Empty.With(gated));
        var gate = Assert.Single(regions.Gates);
        Assert.Equal("gate", gate.Building);
        Assert.Equal([1, 0], gate.Codes);
        Assert.Equal(1, regions.CodeAt(At(300)));
        Assert.Equal(0, regions.CodeAt(At(50)));
        Assert.Equal(-1, regions.CodeAt(At(205)));
        Assert.Equal(-1, regions.CodeAt(At(2000)));

        // Without side points the door is an ordinary one: it walls nothing in.
        var plain = NavGateRegions.Label(NavWorld.Empty.With(Strip(zone, x0, z0, z1)));
        Assert.Empty(plain.Gates);
        Assert.Equal(0, plain.CodeAt(At(300)));
    }

    /// <summary>
    /// The five-quad strip, its middle quad a door's.
    /// </summary>
    static ZoneNavMesh Strip(ZoneCoordinate zone, float x0, float z0, float z1)
    {
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

        var (ox, oz) = WorldLayout.ZoneOrigin(zone);
        return new ZoneNavMesh
        {
            ZoneX = zone.X, ZoneZ = zone.Y, Vertices = [.. vertices], Polygons = polygons, Neighbours = neighbours, Areas = areas,
            Kept = Enumerable.Repeat(true, n).ToArray(),
            BoundsMin = new((float)ox, (float)oz),
            BoundsMax = new((float)ox + WorldLayout.ZoneSize, (float)oz + WorldLayout.ZoneSize),
            DoorIds = ["gate"], DoorOf = [-1, -1, 0, -1, -1],
        };
    }
}

/// <summary>
/// The gate-code pass over the install's navmesh (docs/game/pathfinding.md "Gate codes"): Squin's two gates wall the town in, the first side inside.
/// </summary>
[Slow]
public class SquinGateCodeTests
{
    [Fact]
    public void Squins_gates_have_the_town_inside_and_the_open_world_outside()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");

        var install = InstallData.Install!;
        using var gatherer = new ZoneGeometryGatherer(install, InstallData.FullLoadOrder!, InstallData.LevelsWithInteriors!, new CollisionCache(install));
        var settings = new NavBuildSettings();
        var full = new Dictionary<ZoneCoordinate, ZoneNavMesh>();
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                var c = new ZoneCoordinate(18 + dx, 35 + dz);
                full[c] = NavMeshPipeline.BuildZone(gatherer, gatherer.Gather(c), settings, out _);
            }
        }

        var world = NavWorld.Empty;
        foreach (var (c, keep) in ZoneKeptClosure.Resolve(full, settings))
            world = world.With(full[c].WithKept(keep).WithoutPruned());

        var regions = NavGateRegions.Label(world);
        Assert.Equal(["18.35:3-rebirth-INGAME", "18.35:5-rebirth-INGAME"], regions.Gates.Select(g => g.Building).Order());
        Assert.All(regions.Gates, g => Assert.Equal([1, 0], g.Codes));
        Assert.Empty(regions.Log);
    }
}
