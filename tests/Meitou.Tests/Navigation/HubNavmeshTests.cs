using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Navigation;
using Meitou.Simulation;

namespace Meitou.Tests.Navigation;

/// <summary>The navmesh pipeline on The Hub (zones 20.32 and 21.32) against the Kenshi install.</summary>
[Slow]
public class HubNavmeshTests
{
    static readonly object Gate = new();
    static readonly Dictionary<ZoneCoordinate, (ZoneGeometry Geometry, ZoneNavMesh Full)> built = [];

    // With the zones' interior files, as the game and `meitou-tools navmesh --fingerprint` load them (the pins come from the latter).
    static readonly Lazy<WorldLevelData?> levels = new(() => InstallData.Install is { } i ? WorldLevelData.LoadBaseGame(i, includeInteriors: true) : null);

    static (GameInstall Install, GameDatabase Db, WorldLevelData Levels)? Load() =>
        InstallData.Install is { } install ? (install, InstallData.FullLoadOrder!, levels.Value!) : null;

    static (ZoneGeometry Geometry, ZoneNavMesh Full) Zone(ZoneCoordinate zone)
    {
        var (install, db, levels) = Load()!.Value;
        lock (Gate)
        {
            if (built.TryGetValue(zone, out var b)) return b;
            using var gatherer = new ZoneGeometryGatherer(install, db, levels, new CollisionCache(install));
            var g = gatherer.Gather(zone);
            var mesh = NavMeshPipeline.BuildZone(gatherer, g, new NavBuildSettings(), out _);
            return built[zone] = (g, mesh);
        }
    }

    static readonly ZoneCoordinate West = new(20, 32), East = new(21, 32);

    static string Pin(PathResult path) =>
        path.Found ? string.Join("\n", path.Points.Select(p => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p.X:R},{p.Y:R},{p.Z:R}"))) : "none";

    [Fact]
    public void The_hub_paths_keep_their_exact_points()
    {
        Assert.SkipWhen(Load() is null, "Kenshi install not found");
        var west = Zone(West).Full;
        var east = Zone(East).Full;
        var doors = new NavDoors();
        var house = new NavQuery(NavWorld.Empty.With(west.WithoutPruned()), doors);
        var street = new Vector3(-51290, 1566, 2625);
        var floor = new Vector3(-51158, 1579, 2664);
        Assert.Equal(HubPathPins.StreetToHouseFloor.ReplaceLineEndings("\n"), Pin(house.FindPath(street, floor)));
        Assert.Equal(HubPathPins.HouseFloorToStreet.ReplaceLineEndings("\n"), Pin(house.FindPath(floor, street)));
        // With the Storm House door (placement 4933) closed the room is shut (its walls are solid).
        doors.Close("20.32:4933-Newwworld-INGAME");
        Assert.False(house.FindPath(street, floor).Found);
        doors.Open("20.32:4933-Newwworld-INGAME");
        Assert.True(house.FindPath(street, floor).Found);
        var query = new NavQuery(NavWorld.Empty.With(west.WithoutPruned()).With(east.WithoutPruned()));
        Vector3 At(float x, float z) => new(x, 0, z);
        var outsideWest = At(-53500, 2000);
        var centre = At(-51000, 2900);
        var outsideEast = At(-49500, 3500);
        Assert.Equal(HubPathPins.WestToCentre.ReplaceLineEndings("\n"), Pin(query.FindPath(outsideWest, centre)));
        Assert.Equal(HubPathPins.CentreToEast.ReplaceLineEndings("\n"), Pin(query.FindPath(centre, outsideEast)));
        Assert.Equal(HubPathPins.WestToEast.ReplaceLineEndings("\n"), Pin(query.FindPath(outsideWest, outsideEast)));
        Assert.Equal(HubPathPins.WestToCentreDoorsClosed.ReplaceLineEndings("\n"), Pin(query.FindPath(outsideWest, centre, new NavAgent { DoorsClosed = true })));
    }
}
