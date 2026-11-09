using Meitou.Simulation.Construction;

namespace Meitou.Tests.Simulation;

/// <summary>
/// The height difference below which a worker has arrived at a building (<c>AI::isWithinChoppingRange</c> <c>FUN_14059c6d0</c>,
/// docs/game/buildings-production.md "Work distance").
/// </summary>
public class SiteReachTests
{
    [Fact]
    public void A_worker_arrives_within_130_of_a_door_45_of_an_outdoor_building_and_15_of_one_inside_a_building()
    {
        Assert.Equal(130, SiteReach.HeightLimit(door: true, inBuilding: true));
        Assert.Equal(130, SiteReach.HeightLimit(door: true, inBuilding: false));
        Assert.Equal(45, SiteReach.HeightLimit(door: false, inBuilding: false));
        Assert.Equal(15, SiteReach.HeightLimit(door: false, inBuilding: true));
    }
}
