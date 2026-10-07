using System.Numerics;
using Meitou.Engine;
using Meitou.Engine.Input;
using Meitou.Engine.Time;

namespace Meitou.Tests.Engine;

public class SimulationClockTests
{
    static long TicksIn(double speed, double realSeconds, double frame = 1 / 60.0)
    {
        var clock = new SimulationClock(maxTicksPerFrame: 100000);
        if (speed == 0) clock.TogglePause(); else clock.SetSpeed(speed);
        long n = 0;
        for (double t = 0; t < realSeconds - 1e-9; t += frame) n += clock.Advance(frame);
        return n;
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(5, 150)]
    [InlineData(0, 0)]
    public void Ticks_per_real_second_follow_the_speed(double speed, long ticks)
    {
        Assert.Equal(ticks, TicksIn(speed, 1));
        Assert.Equal(ticks * 10, TicksIn(speed, 10));
    }

    [Fact]
    public void A_tick_is_a_thirtieth_of_a_game_second_at_every_speed()
    {
        var session = new WorldSession(Vector3.Zero, (-1000, -1000, 1000, 1000), (_, _) => 0, startHour: 0);
        session.Simulation.MaxTicksPerFrame = 1000;
        foreach (double speed in SimulationClock.Speeds)
        {
            session.Simulation.SetSpeed(speed);
            double before = session.Clock.GameSeconds;
            session.AdvanceSimulation(1);   // one real second
            Assert.Equal(speed, session.Clock.GameSeconds - before, 6);
        }
        Assert.Equal(8, session.Clock.GameSeconds, 6);
    }

    [Fact]
    public void Pause_remembers_the_last_speed_and_resumes_it()
    {
        var clock = new SimulationClock();
        Assert.Equal(1, clock.Speed);
        clock.SetSpeed(5);
        clock.TogglePause();
        Assert.Equal(0, clock.Speed);
        Assert.True(clock.IsPaused);
        Assert.Equal(5, clock.LastNonZeroSpeed);
        Assert.Equal(0, clock.Advance(1));
        clock.TogglePause();
        Assert.Equal(5, clock.Speed);
        Assert.False(clock.IsPaused);
        Assert.Equal(30, clock.Advance(0.2));   // 150 per second, within the budget of 6 x 5
    }

    [Fact]
    public void A_requested_pause_stops_the_world_without_touching_the_speed()
    {
        var clock = new SimulationClock();
        clock.SetSpeed(2);
        clock.RequestedPause = true;
        Assert.True(clock.IsPaused);
        Assert.Equal(2, clock.Speed);
        Assert.Equal(0, clock.Advance(1));
        // A player pause on top of the request: lifting the request leaves the world paused.
        clock.TogglePause();
        clock.RequestedPause = false;
        Assert.True(clock.IsPaused);
        clock.TogglePause();
        Assert.Equal(2, clock.Speed);
        Assert.Equal(2, clock.Advance(1.0 / 30));
    }

    [Fact]
    public void Faster_and_slower_step_through_one_two_five_and_resume_from_a_pause()
    {
        var clock = new SimulationClock();
        clock.Faster();
        Assert.Equal(2, clock.Speed);
        clock.Faster();
        clock.Faster();
        Assert.Equal(5, clock.Speed);
        clock.Slower();
        Assert.Equal(2, clock.Speed);
        clock.Slower();
        clock.Slower();
        Assert.Equal(1, clock.Speed);
        clock.TogglePause();
        clock.Faster();
        Assert.Equal(2, clock.Speed);
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.SetSpeed(3));
    }

    [Fact]
    public void Ticks_beyond_the_budget_are_dropped_and_the_achieved_speed_drops()
    {
        var clock = new SimulationClock(maxTicksPerFrame: 6);
        // A 1 s stall at 1x owes 30 ticks; 6 run.
        Assert.Equal(6, clock.Advance(1));
        Assert.Equal(24, clock.DroppedTicks);
        Assert.Equal(0.2, clock.AchievedSpeed, 9);
        // The budget scales with the speed: 5x runs up to 30 per frame.
        clock.SetSpeed(5);
        Assert.Equal(30, clock.Advance(1));   // owes 150
        Assert.Equal(1.0, clock.AchievedSpeed, 9);   // 5 x 30 / 150
        // A normal frame is back at full speed.
        clock.SetSpeed(1);
        Assert.Equal(1, clock.Advance(1 / 30.0));
        Assert.Equal(1, clock.AchievedSpeed, 9);
        clock.TogglePause();
        clock.Advance(1);
        Assert.Equal(0, clock.AchievedSpeed);
    }

    [Fact]
    public void Tick_count_depends_only_on_the_summed_time_and_speed()
    {
        const double total = 7;
        var rng = new Random(11);
        foreach (double speed in SimulationClock.Speeds)
        {
            var whole = new SimulationClock(maxTicksPerFrame: 1_000_000);
            whole.SetSpeed(speed);
            long expected = whole.Advance(total);
            Assert.Equal((long)Math.Round(total * speed * 30), expected);
            var split = new SimulationClock(maxTicksPerFrame: 1_000_000);
            split.SetSpeed(speed);
            long sum = 0;
            double left = total;
            while (left > 1e-12)
            {
                double step = Math.Min(left, rng.NextDouble() * 0.05);
                sum += split.Advance(step);
                left -= step;
            }
            Assert.Equal(expected, sum);
        }
    }

    [Fact]
    public void Alpha_holds_while_paused()
    {
        var clock = new SimulationClock();
        clock.Advance(0.05);   // 1.5 ticks
        Assert.Equal(0.5f, clock.Alpha, 4);
        clock.TogglePause();
        clock.Advance(10);
        Assert.Equal(0.5f, clock.Alpha, 4);
    }

    static WorldSession Session() => new(Vector3.Zero, (-1000, -1000, 1000, 1000), (x, z) => 100 + 0.01f * x);

    [Fact]
    public void Speed_and_pause_actions_drive_the_clock_with_the_games_keys()
    {
        var s = Session();
        void Press(Key k) { s.Input.SetKey(k, true); s.Tick(); s.Input.SetKey(k, false); s.Tick(); }
        Press(Key.F3);
        Assert.Equal(2, s.TimeScale);
        Press(Key.F4);
        Assert.Equal(5, s.TimeScale);
        Press(Key.F2);
        Assert.Equal(1, s.TimeScale);
        Press(Key.Period);
        Assert.Equal(2, s.TimeScale);
        Press(Key.Comma);
        Assert.Equal(1, s.TimeScale);
        Press(Key.F4);
        Press(Key.Space);
        Assert.Equal(0, s.TimeScale);
        Assert.True(s.Simulation.IsPaused);
        Press(Key.Space);
        Assert.Equal(5, s.TimeScale);
    }

    [Fact]
    public void The_game_clock_stops_while_paused_and_the_camera_does_not()
    {
        var s = Session();
        s.Input.SetKey(Key.W, true);
        s.Simulation.TogglePause();
        double clockBefore = s.Clock.GameSeconds;
        var before = s.Camera.Current.Target;
        s.Advance(0.3);   // 9 control ticks, no simulation ticks
        Assert.Equal(0, s.Simulation.TotalTicks);
        Assert.Equal(clockBefore, s.Clock.GameSeconds);
        Assert.NotEqual(before, s.Camera.Current.Target);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void The_camera_moves_the_same_distance_at_every_speed_and_while_paused(double speed)
    {
        var s = Session();
        if (speed == 0) s.Simulation.TogglePause(); else s.Simulation.SetSpeed(speed);
        s.Input.SetKey(Key.W, true);
        var start = s.Camera.Current.Target;
        for (int i = 0; i < 10; i++) s.Advance(0.1);   // one real second
        float moved = Vector3.Distance(start, s.Camera.Current.Target);
        var reference = Session();
        reference.Input.SetKey(Key.W, true);
        var refStart = reference.Camera.Current.Target;
        for (int i = 0; i < 10; i++) reference.Advance(0.1);
        Assert.True(moved > 1f);
        Assert.Equal(Vector3.Distance(refStart, reference.Camera.Current.Target), moved, 3);
        Assert.Equal(30, s.Ticks.TotalTicks);
    }

    [Fact]
    public void Control_ticks_run_before_the_simulation_ticks_of_the_same_frame()
    {
        var s = Session();
        s.Input.SetKey(Key.F4, true);   // 5x, applied by this frame's control tick
        s.Advance(1.0 / 30);
        Assert.Equal(5, s.Simulation.Speed);
        Assert.Equal(5, s.Simulation.TotalTicks);
    }
}
