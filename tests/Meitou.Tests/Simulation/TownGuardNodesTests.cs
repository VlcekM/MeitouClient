using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Towns;

namespace Meitou.Tests.Simulation;

/// <summary>
/// The town node map of the bar squads: two guard nodes outside a door (docs/game/factions-squads-towns.md "Bar squads", <c>FUN_14038b650</c>).
/// </summary>
public class TownGuardNodesTests
{
    [Fact]
    public void A_door_makes_two_nodes_19_to_each_side_of_its_outer_point_and_a_close_one_is_skipped()
    {
        var walk = new OpenGroundWalkability((_, _) => 7);
        var door = new DoorSpot(new Vector3(0, 5, 0), Vector3.UnitZ, 20, 10);
        var nodes = new List<Vector3>();
        TownGuardNodes.Add(nodes, door, new Vector3(0, 0, -100), walk);   // the building lies behind -Z: out is +Z, 15 + 33 from the leaf
        Assert.Equal([new Vector3(-19, -3, 48), new Vector3(19, -3, 48)], nodes);
        TownGuardNodes.Add(nodes, door with { Centre = new Vector3(0.5f, 5, 0) }, new Vector3(0, 0, -100), walk);
        Assert.Equal(2, nodes.Count);
    }
}
