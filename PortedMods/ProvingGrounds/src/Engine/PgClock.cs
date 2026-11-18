using ProvingGrounds.Core.Glue;

namespace ProvingGrounds.Engine;

/// <summary>Milliseconds of simulation time: the port's GetTickCount(). One tick = 1000/30 ms. A fresh world starts at
/// <see cref="SimClock.StartTicks"/> (one hour, 3,600,000 ms), not 0, because the original's clock is the system uptime: its throttles
/// against a never-set stamp of 0 fire on the first frame, and 0 can mean "never stamped". It does not advance while the game is paused
/// (a paused world runs no ticks), which matches the original's paused checks. Divergence row: docs/divergences.md "One per-frame UI hook".
/// The arithmetic (start offset, truncation, DWORD wrap at 2^32 ms) is Core/Glue/SimClock.cs, tested there.</summary>
sealed class PgClock
{
    long ticks;   // ticks of this world; written on the sim thread (SlowWorld), read by the frame thread for status: Interlocked keeps the read whole

    /// <summary>Ticks run in this world (from 0).</summary>
    public long Ticks => Interlocked.Read(ref ticks);
    public uint NowMs => SimClock.NowMsAfter(Interlocked.Read(ref ticks));
    public void Advance() => Interlocked.Increment(ref ticks);
    /// <summary>The original's per-frame dt, capped at 1 s (KOWatcher.cpp:36-43): here always one tick.</summary>
    public float DtSeconds => SimClock.TickSeconds;
}
