using System.Numerics;
using Meitou.Engine;

namespace Meitou.Tests.Engine;

/// <summary>The tick run ahead during the world draw (docs/simulation.md "Tick ahead").</summary>
public class TickAheadTests
{
    [Fact]
    public void A_tick_run_ahead_shows_when_it_falls_due_and_a_pause_takes_it()
    {
        using var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0);
        double tick = session.Simulation.TickSeconds;
        Assert.Equal(1, session.AdvanceSimulation(tick));
        var (shown, before, seconds) = (session.CurrentSnapshot, session.PreviousSnapshot, session.Clock.GameSeconds);

        // Ahead: the world runs tick 2; the shown pair, the shown tick and the game clock stay.
        session.BeginTickAhead();
        session.EndTickAhead();
        Assert.Equal(2, session.World.Tick);
        Assert.Equal(1, session.ShownTick);
        Assert.NotSame(shown, session.World.Snapshot);
        Assert.Same(shown, session.CurrentSnapshot);
        Assert.Same(before, session.PreviousSnapshot);
        Assert.Equal(seconds, session.Clock.GameSeconds);

        // A frame that owes no tick leaves it waiting.
        Assert.Equal(0, session.AdvanceSimulation(tick * 0.25));
        Assert.Equal(1, session.ShownTick);
        Assert.Same(shown, session.CurrentSnapshot);

        // Due: it is the tick owed (no other runs), the game clock moves once and the world's snapshots show.
        Assert.Equal(1, session.AdvanceSimulation(tick * 0.8));
        Assert.Equal(2, session.World.Tick);
        Assert.Equal(2, session.ShownTick);
        Assert.Same(session.World.Snapshot, session.CurrentSnapshot);
        Assert.Equal(seconds + tick, session.Clock.GameSeconds, 9);

        // Paused with a tick ahead: it is taken at once and the clock moves by it; no other runs, and none starts while paused.
        session.BeginTickAhead();
        session.EndTickAhead();
        session.Simulation.TogglePause();
        Assert.Equal(0, session.AdvanceSimulation(tick));
        Assert.Equal(3, session.World.Tick);
        Assert.Equal(3, session.ShownTick);
        Assert.Same(session.World.Snapshot, session.CurrentSnapshot);
        Assert.Equal(seconds + 2 * tick, session.Clock.GameSeconds, 9);
        session.BeginTickAhead();
        session.EndTickAhead();
        Assert.Equal(3, session.World.Tick);
    }

    [Fact]
    public void At_5x_a_frames_owed_ticks_run_ahead_as_one_batch_and_leave_the_shown_pair_alone()
    {
        using var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0);
        session.World.Characters.Spawn(new(), new() { Name = "A" }, 0);
        double tick = session.Simulation.TickSeconds;
        session.Simulation.SetSpeed(5);
        // Frames owing 3 ticks each (a hair over, so the accumulator never falls short) settle the batch size at 3.
        for (int i = 0; i < 12; i++) Assert.Equal(3, session.AdvanceSimulation(3.0001 * tick / 5));
        var (shown, before, seconds, at) = (session.CurrentSnapshot, session.PreviousSnapshot, session.Clock.GameSeconds, session.World.Tick);
        var heldCharacters = shown.Characters.Concat(before.Characters).ToList();

        session.BeginTickAhead();
        session.EndTickAhead();
        Assert.Equal(at + 3, session.World.Tick);
        Assert.Equal(at, session.ShownTick);
        Assert.Equal(seconds, session.Clock.GameSeconds);
        Assert.Same(shown, session.CurrentSnapshot);
        Assert.Same(before, session.PreviousSnapshot);
        // The batch's two publishes went into the other two buffers, not the shown pair's.
        Assert.DoesNotContain(session.World.Snapshot.Characters.Concat(session.World.PreviousSnapshot.Characters), c => heldCharacters.Contains(c));

        // A frame owing 1: it is taken from the batch, the clock moves a tick, the picture holds; no tick runs and no new batch starts.
        Assert.Equal(1, session.AdvanceSimulation(tick / 5));
        Assert.Equal(at + 3, session.World.Tick);
        Assert.Equal(at, session.ShownTick);
        Assert.Equal(seconds + tick, session.Clock.GameSeconds, 9);
        Assert.Same(shown, session.CurrentSnapshot);
        session.BeginTickAhead();
        Assert.Equal(at + 3, session.World.Tick);

        // A frame owing 2: the rest are due, the world's snapshots show.
        Assert.Equal(2, session.AdvanceSimulation(2 * tick / 5));
        Assert.Equal(at + 3, session.World.Tick);
        Assert.Equal(at + 3, session.ShownTick);
        Assert.Equal(seconds + 3 * tick, session.Clock.GameSeconds, 9);
        Assert.Same(session.World.Snapshot, session.CurrentSnapshot);
    }
}
