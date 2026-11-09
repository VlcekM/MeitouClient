using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Movement;
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

    static CharacterId Add(SimWorld w, Vector3 at, CharacterTask task = CharacterTask.Idle, float max = 80, float walk = 15,
            byte mode = SpeedMode.Free)
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

    /// <summary>
    /// FUN_1405c9410: a character whose zone is not loaded is deactivated and takes no step; one in a loaded zone does.
    /// </summary>
    [Fact]
    public void A_character_in_an_unloaded_zone_is_deactivated_until_its_zone_loads()
    {
        using var w = Flat();
        var here = Add(w, new(0, 300, 0));
        var there = Add(w, new(9000, 300, 0));
        w.LoadedZones = new HashSet<Meitou.Data.World.ZoneCoordinate> { Data.World.WorldLayout.ZoneOf(0, 0) };
        w.Commands.Enqueue(new MoveOrder([here], new Vector3(0, 0, 400)) { Tick = 0 });
        w.Commands.Enqueue(new MoveOrder([there], new Vector3(9000, 0, 400)) { Tick = 0 });
        w.RunTicks(60);

        Assert.True(Where(w, here).Z > 50);
        Assert.Equal(0, Where(w, there).Z);

        w.LoadedZones = null;   // everything loaded again
        w.RunTicks(60);

        Assert.True(Where(w, there).Z > 50);
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
    public void A_reversal_slows_through_a_stop_at_the_agents_acceleration_and_faces_the_motion()
    {
        using var w = Flat();
        var id = Add(w, new(0, 300, 0));
        w.Commands.Enqueue(new MoveOrder([id], new Vector3(0, 0, 400)) { Tick = 0 });
        w.RunTicks(30);

        Assert.Equal(0, w.Characters.Previous[id.Slot].Yaw, 0.01f);

        // Straight back: the velocity changes by at most the agent's acceleration per tick, and the facing follows the motion.
        w.Commands.Enqueue(new MoveOrder([id], new Vector3(0, 0, -400)) { Tick = w.Tick });
        var v = w.Characters.Previous[id.Slot].Velocity;
        float maxStep = MovementSystem.PathAcceleration(new CharacterCold()) * w.TickSeconds + 0.5f;
        int ticks = 0;
        do
        {
            w.RunTick();
            var now = w.Characters.Previous[id.Slot];

            Assert.InRange(Vector3.Distance(now.Velocity, v), 0, maxStep);

            if (now.Velocity.Length() > 1)

                Assert.InRange(MathF.Abs(MathF.IEEERemainder(now.Yaw - MathF.Atan2(now.Velocity.X, now.Velocity.Z), MathF.Tau)), 0, 0.01f);

            v = now.Velocity;

            Assert.True(++ticks < 60, $"never ran off: velocity {v}");
        }
        while (!(v.Z < -79));

        // 80 units/s round to 80 the other way at 240 units/s per second: two thirds of a second.
        Assert.InRange(ticks, 18, 24);
    }

    [Fact]
    public void Sharp_queued_corners_are_reached_without_circling_a_waypoint()
    {
        using var w = Flat();
        var id = Add(w, new(0, 300, 0), max: 120);
        Vector3[] corners = [new(100, 0, 0), new(0, 0, 30), new(100, 0, 60), new(0, 0, 90)];
        w.Commands.Enqueue(new MoveOrder([id], corners[0]) { Tick = 0 });
        foreach (var c in corners.Skip(1)) w.Commands.Enqueue(new MoveOrder([id], c) { Tick = 0, Queued = true });
        w.RunTicks(30 * 8);
        var end = Where(w, id);

        Assert.InRange(Vector2.Distance(new(end.X, end.Z), new(0, 90)), 0, MovementSystem.ArriveRadius + 1);
        Assert.Equal((byte)CharacterTask.Idle, w.Characters.Previous[id.Slot].Task);
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
