using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation.Navigation;

namespace Meitou.Tests.Navigation;

/// <summary>
/// A player-founded town keeps <c>avoid towns</c> foliage away from the navmesh like a level town (docs/formats/foliage.md "Towns"). Goonrock's
/// outpost stands among three TumbleBoulder02 boulders in the GOG data; the test picks its boulders from whatever the install places instead.
/// </summary>
public class PlayerTownFoliageTests
{
    /// <summary>
    /// The zone the foliage is read from and its neighbours.
    /// </summary>
    static readonly ZoneCoordinate[] Zones = [.. Enumerable.Range(0, 9).Select(i => new ZoneCoordinate(27 + i % 3, 28 + i / 3))];

    /// <summary>
    /// Whether the placed obstacles hold one standing within 1 unit of <paramref name="at"/>.
    /// </summary>
    static bool Has(IEnumerable<FoliageObstacles.FoliageObstacle> list, Vector3 at) =>
        list.Any(o => Vector2.Distance(new Vector2(o.Position.X, o.Position.Z), new Vector2(at.X, at.Z)) < 1);

    /// <summary>
    /// A town of range 150 on one solid <c>avoid towns</c> object drops it, keeps another one more than 600 away, and placing again without the
    /// town brings the first back.
    /// </summary>
    [Fact, Slow]
    public void A_player_town_removes_an_avoid_towns_object_inside_its_range_and_keeps_one_outside_it()
    {
        Assert.SkipWhen(InstallData.Install is null || InstallData.FullLoadOrder is null || InstallData.Levels is null, "Kenshi install not found");
        using var world = new FoliageWorld(InstallData.Install!, InstallData.FullLoadOrder!, InstallData.Levels!);
        var solid = Zones.SelectMany(z => world.Zone(z).Instances)
            .Where(i => i.Mesh.AvoidTowns && i.Mesh.BuildingType is null && i.Mesh.Record.GetPath("collision").EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Position).ToList();
        Assert.SkipWhen(solid.Count == 0, "this install places no solid avoid-towns object in the zones");
        var inside = solid[0];
        var outside = solid.First(p => Vector2.Distance(new Vector2(p.X, p.Z), new Vector2(inside.X, inside.Z)) > 600);

        var runtime = new NavRuntimeBuildings();
        using var obstacles = new FoliageObstacles(InstallData.Install!, InstallData.FullLoadOrder!, InstallData.Levels!, runtime);
        Assert.True(Has(obstacles.Of(Zones).SelectMany(z => z), inside) && Has(obstacles.Of(Zones).SelectMany(z => z), outside));

        runtime.SetFoliageTowns(new PlayerFoliageTowns([new FoliageTown(new Vector2(inside.X, inside.Z), 150, Owned: true)], 2000));
        var with = obstacles.Of(Zones).SelectMany(z => z).ToList();
        Assert.False(Has(with, inside), "the object on the town is rejected");
        Assert.True(Has(with, outside), "the object outside the town's range stays");

        runtime.SetFoliageTowns(PlayerFoliageTowns.None);
        Assert.True(Has(obstacles.Of(Zones).SelectMany(z => z), inside), "without the town the object is placed again");
    }
}
