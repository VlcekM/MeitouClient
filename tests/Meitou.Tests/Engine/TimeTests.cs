using Meitou.Game.Host.Cameras;
using Meitou.Game.Host.Input;
using Meitou.Simulation.Session;

namespace Meitou.Tests.Engine;

public class TimeTests
{
    static double[] RandomSplit(double total, int frames, int seed)
    {
        var rng = new Random(seed);
        var w = Enumerable.Range(0, frames).Select(_ => rng.NextDouble() + 0.01).ToArray();
        double sum = w.Sum();
        var parts = w.Select(x => x / sum * total).ToArray();
        parts[^1] = total - parts[..^1].Sum();
        return parts;
    }

    /// <summary>
    /// A scripted session: the input is a function of the tick number, applied before each tick.
    /// </summary>
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
                input.SetKey(Key.Home, tick % 50 > 40);
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

        // The speed-scaled simulation clock owes the same ticks however the time is split, at every speed.
        foreach (double speed in SimulationClock.DefaultSpeeds)
        {
            var one = new SimulationClock(maxTicksPerFrame: 1_000_000);
            one.SetSpeed(speed);
            // Frames over the cap are cut, so the whole is 80 frames at it.
            long expected = Enumerable.Repeat(SimulationClock.MaxFrameSeconds, 80).Sum(x => one.Advance(x));
            Assert.Equal((long)Math.Round(total * speed * 30), expected);
            var split = new SimulationClock(maxTicksPerFrame: 1_000_000);
            split.SetSpeed(speed);
            Assert.Equal(expected, RandomSplit(total, 400, 2).Sum(x => split.Advance(x)));
        }
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
}
