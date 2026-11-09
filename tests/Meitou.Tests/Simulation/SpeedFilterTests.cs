using Meitou.Simulation;
using Meitou.Simulation.Movement;

namespace Meitou.Tests.Simulation;

public class SpeedFilterTests
{
    static int TicksToFull(int window)
    {
        var a = new CharacterAnimation();
        for (int k = 0; k < 4; k++) a.SmoothSpeed(0, window);
        for (int k = 1; ; k++)
            if (a.SmoothSpeed(70, window) >= 70) return k;
    }

    [Fact]
    public void The_speed_filter_lags_as_many_seconds_at_30_ticks_as_kenshi_at_60_frames()
    {
        Assert.Equal(CharacterAnimation.SpeedWindow, CharacterAnimation.WindowFor(1f / 60));
        Assert.Equal(4, CharacterAnimation.WindowFor(1f / 30));

        // The verified rule (8 samples, the 3 lowest and highest dropped): a start registers fully on the 5th frame of 60 fps, 83 ms.
        Assert.Equal(5, TicksToFull(CharacterAnimation.WindowFor(1f / 60)));

        // At 30 ticks a second the 4-sample window takes 3 ticks, 100 ms, instead of the 8-sample window's 5 ticks, 167 ms.
        Assert.Equal(3, TicksToFull(CharacterAnimation.WindowFor(1f / 30)));

        // A single outlier is still filtered out.
        var a = new CharacterAnimation();
        int window = CharacterAnimation.WindowFor(1f / 30);
        for (int k = 0; k < 6; k++) a.SmoothSpeed(70, window);

        Assert.Equal(70, a.SmoothSpeed(0, window));
    }
}
