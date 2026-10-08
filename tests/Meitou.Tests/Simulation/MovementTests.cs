using System.Numerics;
using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class MovementTests
{
    static SimWorld Flat(int threads = 1, Func<float, float, float>? ground = null, bool synchronous = true, params ITickSystem[] extra)
    {
        var walk = new OpenGroundWalkability(ground ?? SyntheticTown.Ground);
        var movement = new MovementSystem(new PathService(walk, synchronous));
        return new SimWorld(new WorldSettings { Threads = threads, PublishSnapshots = false }, walk, [movement, .. extra]);
    }

    static CharacterId Add(SimWorld w, Vector3 at, CharacterTask task = CharacterTask.Idle, float max = 80, float walk = 15, byte mode = SpeedMode.Free)
    {
        var hot = new CharacterHot { Position = at, Health = 100, Task = (byte)task, MaxSpeed = max, WalkSpeed = walk, Mode = mode };
        return w.Characters.Spawn(hot, new CharacterCold(), 0);
    }

    static Vector3 Where(SimWorld w, CharacterId id) => w.Characters.Previous[id.Slot].Position;

    [Fact]
    public void A_move_order_walks_the_character_to_the_target_and_it_idles_there()
    {
        using var w = Flat();
        var id = Add(w, new(0, 300, 0));
        w.Commands.Enqueue(new MoveOrder([id], new Vector3(400, 0, 0)) { Tick = 0 });
        w.RunTicks(30 * 3);
        var mid = Where(w, id);
        Assert.InRange(mid.X, 50, 399);
        w.RunTicks(30 * 12);
        var end = Where(w, id);
        Assert.InRange(Vector2.Distance(new(end.X, end.Z), new(400, 0)), 0, MovementSystem.ArriveRadius + 1);
        Assert.Equal((byte)CharacterTask.Idle, w.Characters.Previous[id.Slot].Task);
        Assert.Equal(0, w.Characters.Previous[id.Slot].Velocity.Length());
        Assert.Equal(300, end.Y);
    }

    [Fact]
    public void A_path_is_followed_at_the_stat_speed_at_once()
    {
        using var w = Flat();
        var id = Add(w, new(0, 300, 0), max: 40);
        w.Commands.Enqueue(new MoveOrder([id], new Vector3(3000, 0, 0)) { Tick = 0 });
        w.RunTicks(4);   // the first tick only asks for the path; no ramp outside combat
        Assert.Equal(40f, w.Characters.Previous[id.Slot].Velocity.Length(), 3);
        w.RunTicks(90);
        Assert.Equal(40f, w.Characters.Previous[id.Slot].Velocity.Length(), 3);
    }

    [Fact]
    public void Speed_modes_cap_walk_run_and_free()
    {
        Assert.Equal(15, SpeedMode.Cap(SpeedMode.Walk, 15));
        Assert.Equal(55, SpeedMode.Cap(SpeedMode.Run, 15));
        Assert.True(SpeedMode.Cap(SpeedMode.Free, 15) >= 999);
        var goals = new GiveGoals();
        using var w = Flat(extra: goals);
        goals.Ids.Add(Add(w, new(0, 300, 0), mode: SpeedMode.Walk));
        goals.Ids.Add(Add(w, new(0, 300, 100), mode: SpeedMode.Run));
        w.RunTicks(300);
        Assert.Equal(15f, w.Characters.Previous[goals.Ids[0].Slot].Velocity.Length(), 3);
        Assert.Equal(55f, w.Characters.Previous[goals.Ids[1].Slot].Velocity.Length(), 3);
    }

    /// <summary>Orders go through MoveOrder and set the free mode; this hands out a goal without touching the mode.</summary>
    sealed class GiveGoals : ITickSystem
    {
        public readonly List<CharacterId> Ids = [];
        public void Inputs(SimWorld world, IReadOnlyList<SimCommand> commands)
        {
            if (world.Tick != 0) return;
            foreach (var id in Ids)
            {
                ref var n = ref world.Characters.Next[id.Slot];
                n.Task = (byte)CharacterTask.GoTo;
                n.Goal = new(5000, n.Position.Z);
                n.Flags = (ushort)MoveFlags.NeedPath;
            }
        }
    }

    [Fact]
    public void Characters_do_not_walk_into_the_water_and_unreachable_goals_give_up()
    {
        // A lake between x = 200 and 300.
        float Lake(float x, float z) => x is > 200 and < 300 ? 50 : 300;
        using var w = Flat(ground: Lake);
        var id = Add(w, new(0, 300, 0));
        w.Commands.Enqueue(new MoveOrder([id], new Vector3(500, 0, 0)) { Tick = 0 });
        w.RunTicks(300);
        Assert.True(Where(w, id).X < 200);
        Assert.Equal((byte)CharacterTask.Idle, w.Characters.Previous[id.Slot].Task);
    }

    [Fact]
    public void Characters_standing_on_each_other_drift_apart()
    {
        using var w = Flat();
        var a = Add(w, new(0, 300, 0));
        var b = Add(w, new(1, 300, 0));
        w.RunTicks(120);
        float d = Vector3.Distance(Where(w, a), Where(w, b));
        Assert.InRange(d, MovementSystem.SeparationRadius - 0.5f, MovementSystem.SeparationRadius + 2);
    }

    [Fact]
    public void A_squad_wanders_within_its_home_and_followers_keep_with_the_leader()
    {
        using var w = SyntheticTown.World(3, 1, 2);
        w.RunTicks(30 * 60);
        var centre = new Vector2(SyntheticTown.Centre.X, SyntheticTown.Centre.Z);
        int moved = 0, followers = 0;
        foreach (var squad in w.Squads.All)
        {
            var lead = w.Characters.Previous[squad.Leader.Slot].Position;
            Assert.True(Vector2.Distance(new(lead.X, lead.Z), centre) <= squad.HomeRadius + 50);
            foreach (var m in squad.Members)
            {
                if (m == squad.Leader) continue;
                var p = w.Characters.Previous[m.Slot];
                if (p.Task == (byte)CharacterTask.Follow)
                {
                    followers++;
                    Assert.True(Vector3.Distance(p.Position, lead) < 120, "a follower stays near its leader");
                }
            }
            if (Vector2.Distance(new(lead.X, lead.Z), new(squad.Position.X, squad.Position.Z)) > 5) moved++;
        }
        Assert.True(followers > 0);
        Assert.True(moved > 0, "the leaders wandered off their start");
    }

    static List<ulong> Run(int threads, bool synchronous = true)
    {
        using var w = SyntheticTown.World(21, threads, 6, new PopulationSettings { CheckEveryTicks = 1 }, synchronous);
        var hashes = new List<ulong>();
        int ran = 0;
        foreach (int target in new[] { 1, 10, 100, 400, 900 })
        {
            if (target == 100) w.Commands.Enqueue(new MoveOrder([.. w.Squads.All.First().Members], new Vector3(1300, 0, 2100)) { Tick = w.Tick });
            w.RunTicks(target - ran);
            ran = target;
            hashes.Add(w.StateHash());
        }
        return hashes;
    }

    [Fact]
    public void The_real_workload_hashes_the_same_at_1_4_and_16_threads()
    {
        var one = Run(1);
        Assert.Equal(one, Run(4));
        Assert.Equal(one, Run(16));
        Assert.Equal(one, Run(4));
        Assert.Equal(5, one.Distinct().Count());
    }

    [Fact]
    public void Asynchronous_paths_reach_the_same_places()
    {
        using var w = Flat(synchronous: false);
        var id = Add(w, new(0, 300, 0));
        w.Commands.Enqueue(new MoveOrder([id], new Vector3(200, 0, 0)) { Tick = 0 });
        Assert.True(TestWaits.TickUntil(w, () => Where(w, id).X >= 190), "the character never arrived");
        Assert.InRange(Where(w, id).X, 190, 210);
    }

    [Fact]
    public void Spawning_or_removing_in_a_parallel_phase_fails_loudly()
    {
        using var w = Flat(extra: new Misbehaving());
        Add(w, new(0, 300, 0));
        Assert.Throws<InvalidOperationException>(() => w.RunTick());
        using var w2 = Flat(extra: new RemovesInInputs());
        var id = Add(w2, new(0, 300, 0));
        Assert.Throws<InvalidOperationException>(() => w2.RunTick());
        Assert.True(w2.Characters.IsAlive(id));
    }

    sealed class Misbehaving : ITickSystem
    {
        public void Think(SimWorld world, Partition partition) =>
            world.Characters.Spawn(new CharacterHot(), new CharacterCold(), world.Tick);
    }

    sealed class RemovesInInputs : ITickSystem
    {
        public void Inputs(SimWorld world, IReadOnlyList<SimCommand> commands) => world.Characters.Remove(new CharacterId(0, 0));
    }
}
