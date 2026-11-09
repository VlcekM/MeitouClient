using Meitou.Simulation.Session;

namespace Meitou.Tests.Engine;

public class SimulationClockTests
{
    [Fact]
    public void Ticks_beyond_the_budget_are_dropped_and_the_achieved_speed_drops()
    {
        var clock = new SimulationClock(maxTicksPerFrame: 6);

        // A 1 s stall counts as 0.05 s before the speed (FUN_14082ac70): 1.5 ticks owed at 1x, 1 runs and the half tick waits.
        Assert.Equal(1, clock.Advance(1));
        Assert.Equal(0, clock.DroppedTicks);

        // 5x owes 7.5 ticks of the capped frame: under the 30 a frame may run, so none is dropped.
        clock.SetSpeed(5);
        Assert.Equal(8, clock.Advance(1));   // 7.5 + the half tick left
        Assert.Equal(0, clock.DroppedTicks);

        // The safety limit still drops what a frame cannot run.
        clock.MaxTicksPerFrame = 1;
        Assert.Equal(5, clock.Advance(1));
        Assert.Equal(2, clock.DroppedTicks);

        // A normal frame is back at full speed.
        clock.SetSpeed(1);
        Assert.Equal(1, clock.Advance(1 / 30.0));
        Assert.Equal(1, clock.AchievedSpeed, 9);
        clock.TogglePause();
        clock.Advance(1);
        Assert.Equal(0, clock.AchievedSpeed);
    }
}
