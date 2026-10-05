using System.Numerics;
using Meitou.Engine;
using Meitou.Engine.Input;

namespace Meitou.Tests.Engine;

public class WorldSessionTests
{
    static float Ground(float x, float z) => 100 + 0.01f * x;

    static WorldSession Make() => new(Vector3.Zero, (-1000, -1000, 1000, 1000), Ground);

    [Fact]
    public void Same_time_in_different_frame_splits_gives_the_same_session_state()
    {
        var a = Make();
        var b = Make();
        a.Input.SetKey(Key.W, true);
        b.Input.SetKey(Key.W, true);
        a.Advance(0.3);   // 9 ticks: under the catch-up cap
        var random = new Random(7);
        double left = 0.3;
        while (left > 1e-12)
        {
            double step = Math.Min(left, random.NextDouble() * 0.05);
            b.Advance(step);
            left -= step;
        }
        Assert.Equal(a.Ticks.TotalTicks, b.Ticks.TotalTicks);
        Assert.Equal(a.Camera.Current.Target, b.Camera.Current.Target);
        Assert.Equal(a.Clock.GameSeconds, b.Clock.GameSeconds);
    }

    [Fact]
    public void Pause_and_speed_actions_change_the_clock()
    {
        var s = Make();
        s.Input.SetKey(Key.Period, true);
        s.Tick();
        s.Input.SetKey(Key.Period, false);
        Assert.Equal(2, s.TimeScale);
        s.Input.SetKey(Key.Space, true);
        s.Tick();
        double before = s.Clock.GameSeconds;
        s.Tick();
        Assert.True(s.Clock.Paused);
        Assert.Equal(before, s.Clock.GameSeconds);
    }
}
