using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class SystemPhasesTests
{
    sealed class ActOnly : ITickSystem
    {
        public int Acts;
        public void Act(SimWorld world, Partition partition, EffectBuffer effects) => Interlocked.Increment(ref Acts);
    }

    sealed class ThinkAndMove : ITickSystem
    {
        public void Think(SimWorld world, Partition partition) { }
        public void Move(SimWorld world, Partition partition) { }
    }

    sealed class Nothing : ITickSystem { }

    [Fact]
    public void A_phase_is_skipped_exactly_when_the_system_leaves_the_default()
    {
        using var world = new SimWorld(new WorldSettings(), new OpenGroundWalkability((_, _) => 0));
        var actOnly = new SystemPhases(world, new ActOnly(), 0);
        Assert.Null(actOnly.Think);
        Assert.Null(actOnly.Move);
        Assert.NotNull(actOnly.Act);
        var tm = new SystemPhases(world, new ThinkAndMove(), 1);
        Assert.NotNull(tm.Think);
        Assert.NotNull(tm.Move);
        Assert.Null(tm.Act);
        var none = new SystemPhases(world, new Nothing(), 2);
        Assert.Null(none.Think);
        Assert.Null(none.Move);
        Assert.Null(none.Act);
        Assert.Equal(2, none.Index);
    }

    [Fact]
    public void The_standard_systems_skip_the_phases_they_do_not_use()
    {
        using var world = new SimWorld(new WorldSettings(), new OpenGroundWalkability((_, _) => 0));
        var movement = new SystemPhases(world, new MovementSystem(new PathService(world.Walkability, true)), 0);
        Assert.NotNull(movement.Think);
        Assert.NotNull(movement.Move);
        Assert.Null(movement.Act);
    }

    [Fact]
    public void A_system_in_a_many_partition_world_acts_once_per_partition_whatever_its_neighbours_skip()
    {
        var act = new ActOnly();
        using var world = new SimWorld(new WorldSettings { Threads = 4, MinPartitionSize = 1, PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0), [new Nothing(), act, new ThinkAndMove()]);
        for (int n = 0; n < 40; n++) world.Characters.Spawn(new CharacterHot { Health = 100 }, new CharacterCold(), 0);
        world.RunTicks(3);
        Assert.Equal(3 * 8, act.Acts);
    }
}
