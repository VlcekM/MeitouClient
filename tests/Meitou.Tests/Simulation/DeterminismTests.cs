using Meitou.Simulation;

namespace Meitou.Tests.Simulation;

public class DeterminismTests
{
    static readonly int[] Checkpoints = [1, 5, 30, 90, 200, 300];

    internal static List<ulong> Hashes(ulong seed, int threads, int characters = 300)
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

            Assert.True(WanderSystem.Ground(c.Position.X, c.Position.Z) > Data.World.WorldWater.Height);
            Assert.Equal(WanderSystem.Ground(c.Position.X, c.Position.Z), c.Position.Y, 3);
        }
    }
}
