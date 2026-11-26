using Meitou.Simulation;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// The selected-building panel learns a cage's or container's own lock (its Lock state and Lock complexity rows, #1502).
/// </summary>
public class FixtureLockDetailTests
{
    /// <summary>
    /// A town cage selected by its fixture id gives its lock's level and state, and follows a lock change.
    /// </summary>
    [Fact]
    public void A_selected_cage_reports_its_lock_level_and_state()
    {
        var data = ArrestTests.Town();
        using var world = new SimWorld(new WorldSettings { Seed = 1, PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 150),
            [new PlayerSystem()]);
        world.Fixtures.RegisterSite(world, data, 0);
        var cage = world.Fixtures.Find("0.0:cageB")!;
        world.Fixtures.SetLocked(cage, false);

        var open = world.Fixtures.DetailOf(cage.Id, 0)!.Lock;
        world.Fixtures.SetLocked(cage, true);
        var shut = world.Fixtures.DetailOf(cage.Id, 0)!.Lock;

        Assert.Equal(new LockDetail(false, cage.Lock!.Level), open);
        Assert.Equal(new LockDetail(cage.Lock.Level >= 1, cage.Lock.Level), shut);
    }
}
