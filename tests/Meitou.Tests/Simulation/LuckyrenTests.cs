using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;
using Meitou.Simulation.Population;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class LuckyrenTests
{
    /// <summary>
    /// A started player squad whose second member bursts at tick 2 carrying two items (one worn); the world after the burst's tick.
    /// </summary>
    static (SimWorld World, CharacterId Victim, Vector3 At, ConcurrentQueue<BloodMark> Marks) Burst(int threads)
    {
        var db = SyntheticTown.Database();
        var data = SyntheticTown.Data(db);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var systems = SyntheticTown.Standard(data, walk,
                StandardParts.Population | StandardParts.Player | StandardParts.Movement | StandardParts.Body,
            new PopulationSettings { CheckEveryTicks = 1, UnloadGraceSeconds = 5 });
        var world = new SimWorld(new WorldSettings { Seed = 4, Threads = threads, PublishSnapshots = false }, walk, systems.Systems);
        var squad = systems.Population!.StartPlayer(world, NewGameStart.From(db.Find("40-t")!));
        world.Commands.Enqueue(new FocusCommand(new Vector3(-90000, 0, -90000)) { Tick = 0 });
        world.RunTicks(2);
        var victim = squad.Members[1];
        var cold = world.Characters.Cold(victim.Slot)!;
        cold.Inventory = new Inventory();
        cold.Inventory.Items.Add(new ItemInstance { Record = "worn", Section = "body" });
        cold.Inventory.Items.Add(new ItemInstance { Record = "carried" });
        var marks = new ConcurrentQueue<BloodMark>();
        world.BloodMarks = marks;
        world.Luckyren = true;
        cold.Burst = new Vector2(1, 0);
        var at = world.Characters.Previous[victim.Slot].Position;
        world.RunTicks(1);
        return (world, victim, at, marks);
    }

    /// <summary>
    /// The state after the burst's tick, pinned in <see cref="GoldenHashTests"/>.
    /// </summary>
    internal static List<ulong> Scripted(int threads)
    {
        var (world, _, _, _) = Burst(threads);
        using (world) return [world.StateHash()];
    }

    [Fact]
    public void A_burst_removes_the_body_that_tick_drops_its_gear_round_it_and_queues_one_explosion()
    {
        var (world, victim, at, marks) = Burst(1);
        using var _ = world;

        Assert.False(world.Characters.IsAlive(victim));
        Assert.Equal(["carried", "worn"], world.GroundItems.All.Select(g => g.Item.Record).Order());
        Assert.All(world.GroundItems.All, g =>

        {
            Assert.Equal("main", g.Item.Section);
            Assert.InRange(Vector2.Distance(new(g.Position.X, g.Position.Z), new(at.X, at.Z)), 0, BodySystem.BurstSpread);
        });
        var mark = Assert.Single(marks);

        Assert.Equal(BloodMarkKind.Explosion, mark.Kind);
        Assert.Equal(victim, mark.Who);
        Assert.NotNull(mark.Race);
    }
}
