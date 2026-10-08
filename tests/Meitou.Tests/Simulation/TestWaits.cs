using System.Diagnostics;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>Waiting for the simulation's helper threads (path service, population builder) by condition, not by a fixed number of sleeping ticks.</summary>
static class TestWaits
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Runs ticks until <paramref name="done"/> holds or the deadline passes; yields the thread after a tick that did not finish it, so a helper
    /// thread under load still gets its turn. Returns whether the condition held.
    /// </summary>
    public static bool TickUntil(SimWorld world, Func<bool> done, TimeSpan? deadline = null)
    {
        var watch = Stopwatch.StartNew();
        var limit = deadline ?? Deadline;
        while (!done())
        {
            if (watch.Elapsed > limit) return false;
            world.RunTick();
            if (!done()) Thread.Sleep(1);
        }
        return true;
    }
}
