using Meitou.Engine.Cameras;
using Meitou.Engine.Input;
using Meitou.Engine.Time;

namespace Meitou.Tests.Engine;

public class TimeTests
{
    [Fact]
    public void Ticks_and_alpha_follow_the_summed_time()
    {
        var clock = new FixedStepClock(30);
        Assert.Equal(0, clock.Advance(0.01));
        Assert.Equal(0.3f, clock.Alpha, 4);
        Assert.Equal(1, clock.Advance(0.04)); // 0.05 s = 1.5 ticks
        Assert.Equal(0.5f, clock.Alpha, 4);
        Assert.Equal(1, clock.TotalTicks);
    }

    [Fact]
    public void Alpha_stays_in_zero_to_one_exclusive()
    {
        var clock = new FixedStepClock(30);
        var rng = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            clock.Advance(rng.NextDouble() * 0.2);
            Assert.InRange(clock.Alpha, 0f, 0.9999999f);
        }
    }

    [Fact]
    public void A_long_stall_runs_at_most_the_catch_up_ticks_and_drops_the_rest()
    {
        var clock = new FixedStepClock(30, maxCatchUp: 5);
        Assert.Equal(5, clock.Advance(10)); // 300 ticks due
        Assert.Equal(295, clock.DroppedTicks);
        Assert.Equal(5, clock.TotalTicks);
        Assert.InRange(clock.Alpha, 0f, 1f);
        // Normal frames carry on from there.
        Assert.Equal(1, clock.Advance(1 / 30.0));
    }

    [Fact]
    public void The_tick_rate_is_configurable()
    {
        var clock = new FixedStepClock(60, maxCatchUp: 100);
        Assert.Equal(60, clock.Advance(1));
        Assert.Equal(1 / 60.0, clock.TickSeconds, 12);
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedStepClock(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(-1));
    }

    static double[] RandomSplit(double total, int frames, int seed)
    {
        var rng = new Random(seed);
        var w = Enumerable.Range(0, frames).Select(_ => rng.NextDouble() + 0.01).ToArray();
        double sum = w.Sum();
        var parts = w.Select(x => x / sum * total).ToArray();
        parts[^1] = total - parts[..^1].Sum();
        return parts;
    }

    /// <summary>A scripted session: the input is a function of the tick number, applied before each tick.</summary>
    static (long Ticks, CameraState Camera, double GameSeconds, double Hour) Simulate(double[] frames)
    {
        var step = new FixedStepClock(30, maxCatchUp: 100000);
        var sim = new SimulationClock();
        var game = new GameClock();
        var bindings = new InputBindings();
        var rig = new CameraRig((x, z) => 0.1f * x);
        var input = new InputState();
        long tick = 0;
        foreach (double frame in frames)
        {
            int n = step.Advance(frame);
            for (int i = 0; i < n; i++, tick++)
            {
                input.SetKey(Key.W, tick % 40 < 25);
                input.SetKey(Key.Q, tick % 17 < 6);
                input.SetKey(Key.PageUp, tick % 50 > 40);
                input.SetKey(Key.Down, tick % 23 < 3);
                input.SetKey(Key.Semicolon, tick % 45 == 30);
                input.SetKey(Key.Space, tick % 60 == 10);
                input.AddMouseDelta(tick % 7, -(tick % 5));
                var actions = bindings.Resolve(input.Consume());
                if (actions.Pressed(InputAction.Pause)) sim.TogglePause();
                if (!sim.IsPaused) game.Advance(step.TickSeconds);
                rig.Update((float)step.TickSeconds, actions);
            }
        }
        return (step.TotalTicks, rig.Current, game.GameSeconds, game.HourOfDay);
    }

    [Fact]
    public void Different_frame_splits_of_the_same_time_give_the_same_ticks_and_state()
    {
        const double total = 4;
        var whole = Simulate([total]);
        Assert.Equal(120, whole.Ticks);
        var sixty = Simulate(Enumerable.Repeat(1 / 60.0, 240).ToArray());
        var random1 = Simulate(RandomSplit(total, 97, 1));
        var random2 = Simulate(RandomSplit(total, 400, 2));
        foreach (var other in new[] { sixty, random1, random2 })
        {
            Assert.Equal(whole.Ticks, other.Ticks);
            Assert.Equal(whole.Camera, other.Camera);
            Assert.Equal(whole.GameSeconds, other.GameSeconds);
            Assert.Equal(whole.Hour, other.Hour);
        }
    }

    [Fact]
    public void Lerp_hits_endpoints_and_midpoints()
    {
        Assert.Equal(2f, Interp.Lerp(2, 10, 0));
        Assert.Equal(10f, Interp.Lerp(2, 10, 1));
        Assert.Equal(6f, Interp.Lerp(2, 10, 0.5f));
    }

    [Fact]
    public void Angles_interpolate_along_the_shortest_path_across_pi()
    {
        float a = MathF.PI - 0.1f, b = -MathF.PI + 0.1f; // 0.2 apart through +-pi
        Assert.Equal(a, Interp.LerpAngle(a, b, 0), 5);
        Assert.Equal(b, Interp.LerpAngle(a, b, 1), 5);
        float mid = Interp.LerpAngle(a, b, 0.5f);
        Assert.Equal(MathF.PI, MathF.Abs(mid), 4);
        Assert.Equal(0.2f, MathF.Abs(Interp.WrapAngle(Interp.LerpAngle(a, b, 0.25f) - a)) * 4, 4);
        Assert.Equal(0.25f, Interp.LerpAngle(0.1f, 0.4f, 0.5f), 5);
        Assert.Equal(-0.1f, Interp.LerpAngle(0.1f, -0.3f, 0.5f), 5);
    }

    [Fact]
    public void Interpolated_blends_previous_and_current_and_snaps()
    {
        var v = new Interpolated<float>(0, Interp.Lerp);
        v.Push(10);
        v.Push(20);
        Assert.Equal(10f, v.At(0));
        Assert.Equal(15f, v.At(0.5f));
        Assert.Equal(20f, v.At(1));
        v.Snap(100);
        Assert.Equal(100f, v.At(0.5f));
    }

    [Fact]
    public void Camera_state_lerp_wraps_yaw_and_cuts_between_modes()
    {
        var a = new CameraState(new(0, 0, 0), -System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitY, default, MathF.PI - 0.1f, 0.5f, 100, false);
        var b = a with { Eye = new(10, 0, 0), Yaw = -MathF.PI + 0.1f, Distance = 200 };
        var m = CameraState.Lerp(a, b, 0.5f);
        Assert.Equal(5f, m.Eye.X, 4);
        Assert.Equal(MathF.PI, MathF.Abs(m.Yaw), 4);
        Assert.Equal(150f, m.Distance, 3);
        var free = b with { IsFree = true };
        Assert.Equal(free, CameraState.Lerp(a, free, 0.5f));
    }
}
