namespace Meitou.Engine.Time;

/// <summary>
/// Fixed simulation step decoupled from rendering: the host feeds real frame time to <see cref="Advance"/>, which says how many
/// simulation ticks to run and leaves <see cref="Alpha"/> (how far the next tick is) for drawing between the previous and the
/// current tick's state. The accumulator counts in ticks, not seconds, and the result is rounded with a small tolerance, so the
/// tick count depends only on the summed real time, not on how it was split into frames (60 x 1/60 s gives the same 30 ticks as
/// one 1 s frame). Nothing reads a wall clock. The default 30 Hz is an engine choice for the real-time control tick (input actions, camera, UI); the
/// world's own tick runs on a second instance fed game time (<see cref="SimulationClock"/>; the original has no fixed rate, docs/game/game-loop.md).
/// </summary>
public sealed class FixedStepClock
{
    /// <summary>Default simulation rate, ticks per second (engine choice).</summary>
    public const double DefaultTickRate = 30;
    /// <summary>Default most ticks one <see cref="Advance"/> returns (engine choice).</summary>
    public const int DefaultMaxCatchUp = 10;
    // Float noise from summing many frame times: a total that is a whole number of ticks within this counts as reached.
    const double Epsilon = 1e-9;

    double accumulated;

    public FixedStepClock(double tickRate = DefaultTickRate, int maxCatchUp = DefaultMaxCatchUp)
    {
        if (!(tickRate > 0) || double.IsInfinity(tickRate)) throw new ArgumentOutOfRangeException(nameof(tickRate));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCatchUp, 1);
        TickRate = tickRate;
        MaxCatchUp = maxCatchUp;
    }

    /// <summary>Ticks per second.</summary>
    public double TickRate { get; }
    /// <summary>Length of one tick in seconds.</summary>
    public double TickSeconds => 1 / TickRate;
    /// <summary>Most ticks one <see cref="Advance"/> returns; the rest of a long stall is dropped.</summary>
    public int MaxCatchUp { get; }
    /// <summary>Ticks returned so far.</summary>
    public long TotalTicks { get; private set; }
    /// <summary>Ticks dropped so far because a stall exceeded <see cref="MaxCatchUp"/>.</summary>
    public long DroppedTicks { get; private set; }
    /// <summary>Position between the previous tick (0) and the next one (1, exclusive), for interpolation.</summary>
    public float Alpha { get; private set; }

    /// <summary>Adds real seconds and returns how many ticks to run now.</summary>
    public int Advance(double realSeconds) => Advance(realSeconds, MaxCatchUp);

    /// <summary>Adds seconds and returns how many ticks to run now, at most <paramref name="maxTicks"/> (the rest are dropped).</summary>
    public int Advance(double realSeconds, int maxTicks)
    {
        if (!(realSeconds >= 0) || double.IsInfinity(realSeconds)) throw new ArgumentOutOfRangeException(nameof(realSeconds));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTicks, 1);
        accumulated += realSeconds * TickRate;
        double whole = Math.Floor(accumulated + Epsilon);
        accumulated = Math.Max(accumulated - whole, 0);
        long due = (long)whole;
        int run = (int)Math.Min(due, maxTicks);
        DroppedTicks += due - run;
        TotalTicks += run;
        // Rounding up to a tick boundary can leave a hair below zero or at one: keep alpha in [0, 1).
        Alpha = (float)Math.Clamp(accumulated, 0, 1 - 1e-7);
        return run;
    }
}
