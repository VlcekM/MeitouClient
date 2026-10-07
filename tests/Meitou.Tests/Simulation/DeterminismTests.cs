using System.Numerics;
using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class DeterminismTests
{
    static readonly int[] Checkpoints = [1, 5, 30, 90, 200, 300];

    static List<ulong> Hashes(ulong seed, int threads, int characters = 300)
    {
        using var world = WanderSystem.Create(seed, threads, characters);
        var hashes = new List<ulong>();
        int ran = 0;
        foreach (int target in Checkpoints)
        {
            world.RunTicks(target - ran);
            ran = target;
            hashes.Add(world.StateHash());
        }
        return hashes;
    }

    [Fact]
    public void One_four_and_sixteen_threads_reach_the_same_state_at_every_checkpoint()
    {
        var one = Hashes(7, 1);
        Assert.Equal(one, Hashes(7, 4));
        Assert.Equal(one, Hashes(7, 16));
    }

    [Fact]
    public void Two_runs_with_the_same_seed_agree_and_another_seed_does_not()
    {
        var a = Hashes(7, 4);
        Assert.Equal(a, Hashes(7, 4));
        var other = Hashes(8, 4);
        Assert.NotEqual(a[^1], other[^1]);
        // The hash moves as the world does.
        Assert.Equal(a.Count, a.Distinct().Count());
    }

    [Fact]
    public void The_workload_really_exercises_deaths_births_and_slot_reuse()
    {
        using var world = WanderSystem.Create(7, 4, 300);
        int start = world.Characters.Count;
        world.RunTicks(300);
        var table = world.Characters;
        Assert.NotEqual(start, table.Count);
        Assert.True(table.HighWater > start, "births took new slots");
        bool reused = false;
        foreach (var c in table.Previous)
            if (c.Alive && c.Generation > 0) reused = true;
        Assert.True(reused, "a freed slot was used again with a higher generation");
        // Characters moved, stayed on dry ground and kept their height.
        foreach (var c in table.Previous)
        {
            if (!c.Alive) continue;
            Assert.True(WanderSystem.Ground(c.Position.X, c.Position.Z) > Meitou.Data.World.WorldWater.Height);
            Assert.Equal(WanderSystem.Ground(c.Position.X, c.Position.Z), c.Position.Y, 3);
        }
    }

    [Fact]
    public void Commands_apply_at_their_tick_in_queue_order()
    {
        using var world = new SimWorld(new WorldSettings(), new OpenGroundWalkability((_, _) => 200f), [new Recorder()]);
        var rec = (Recorder)world.Systems[0];
        var id = new CharacterId(0, 0);
        world.Commands.Enqueue(new MoveOrder([id], new Vector3(2, 0, 0)) { Tick = 2 });
        world.Commands.Enqueue(new MoveOrder([id], new Vector3(1, 0, 0)) { Tick = 0 });
        world.Commands.Enqueue(new MoveOrder([id], new Vector3(3, 0, 0)) { Tick = 2 });
        world.RunTicks(4);
        Assert.Equal([(0L, 1f), (2L, 2f), (2L, 3f)], rec.Seen);
    }

    sealed class Recorder : ITickSystem
    {
        public readonly List<(long Tick, float X)> Seen = [];
        public void Inputs(SimWorld world, IReadOnlyList<SimCommand> commands)
        {
            foreach (var c in commands.Cast<MoveOrder>()) Seen.Add((world.Tick, c.Target.X));
        }
    }

    [Fact]
    public void Snapshots_are_published_each_tick_and_keep_the_one_before()
    {
        using var world = new SimWorld(new WorldSettings(), new OpenGroundWalkability((_, _) => 200f));
        Assert.Same(WorldSnapshot.Empty, world.Snapshot);
        var appearance = (Meitou.Data.Characters.CharacterAppearance)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Meitou.Data.Characters.CharacterAppearance));
        var id = world.Characters.Spawn(new CharacterHot { Position = new(1, 2, 3), Yaw = 0.5f }, new CharacterCold { Appearance = appearance }, 0);
        world.Characters.Spawn(new CharacterHot(), new CharacterCold(), 0);   // no appearance: published, nothing to draw
        world.RunTick();
        Assert.Equal(1, world.Snapshot.Tick);
        Assert.Equal(2, world.Snapshot.Characters.Count);
        var c = world.Snapshot.Characters[0];
        Assert.Equal(id, c.Id);
        Assert.Equal(new Vector3(1, 2, 3), c.Position);
        Assert.Same(WorldSnapshot.Empty, world.PreviousSnapshot);
        world.RunTick();
        Assert.Equal(2, world.Snapshot.Tick);
        Assert.Equal(1, world.PreviousSnapshot.Tick);
    }
}
