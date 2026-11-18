namespace ProvingGrounds.Core.Glue;

/// <summary>The arithmetic of the port's <c>GetTickCount()</c> stand-in (src/Engine/PgClock.cs): milliseconds of simulation time from a
/// tick count, one tick = 1000/30 ms, wrapping at 2^32 like the original's <c>DWORD</c>. Not a port of an original file: glue support.</summary>
public static class SimClock
{
    /// <summary>Meitou's fixed tick: 30 ticks per simulated second.</summary>
    public const int TicksPerSecond = 30;

    /// <summary>One tick in seconds: the dt every frame-loop step sees.</summary>
    public const float TickSeconds = 1f / TicksPerSecond;

    /// <summary>
    /// The clock of a fresh world starts here, one hour of simulation time (3,600,000 ms), not at 0. The original's GetTickCount() is the
    /// system uptime, large by the time a world runs, so its throttles (<c>if (now - g_last >= interval)</c> with <c>g_last = 0</c>) fire
    /// on the first frame and a stamp of 0 can mean "never stamped". One hour is above every interval the C++ times with GetTickCount():
    /// the largest is ArenaIngress.cpp:98 kNpcFormationHardCapSec = 90 s (then PrisonerUtil.cpp:112 kReturnTimeoutSec 60 s,
    /// PrisonerUtil.cpp:113 kLockTimeoutMs 30000; spectator chatter at most 12 s, ArenaPerformance.cpp:43).
    /// </summary>
    public const long StartTicks = 108_000;

    /// <summary>The largest GetTickCount() interval in the C++ (ArenaIngress.cpp:98, 90 s); <see cref="StartTicks"/> must exceed it.</summary>
    public const uint LargestOriginalIntervalMs = 90_000;

    /// <summary>The clock value after <paramref name="worldTicks"/> ticks of this world: <see cref="MsAt"/> of <see cref="StartTicks"/> + worldTicks.</summary>
    public static uint NowMsAfter(long worldTicks) => MsAt(StartTicks + worldTicks);

    /// <summary>Milliseconds after <paramref name="ticks"/> ticks, truncated, as a wrapping 32-bit value (GetTickCount is a DWORD).</summary>
    public static uint MsAt(long ticks) => unchecked((uint)(ticks * 1000 / TicksPerSecond));

    /// <summary><c>now - then</c> in unsigned arithmetic, as the C++ throttles compute it (correct across the 2^32 wrap).</summary>
    public static uint ElapsedMs(uint now, uint then) => unchecked(now - then);
}
