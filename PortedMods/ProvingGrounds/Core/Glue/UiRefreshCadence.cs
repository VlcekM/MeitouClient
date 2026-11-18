namespace ProvingGrounds.Core.Glue;

/// <summary>The Arena UI's idle refresh clock (ArenaUI.cpp:213-230 globals and helpers, used at :3172-3185): elapsed starts at the
/// interval, the first dt after a reset is 0, dt is capped at 1 s, and <see cref="FrameCadencePolicy"/> decides. The port's
/// ProvingGroundsSystem uses it to publish its snapshot; dt comes from the simulation clock instead of GetTickCount().</summary>
public sealed class UiRefreshCadence
{
    /// <summary>C++: ArenaUI.cpp:213 <c>kIdleUiRefreshIntervalSec</c>.</summary>
    public const float IntervalSec = 0.20f;

    float elapsedSec = IntervalSec;   // C++: ArenaUI.cpp:215 g_uiRefreshElapsedSec
    bool started;                     // C++: ArenaUI.cpp:214 g_uiRefreshLastTick != 0

    /// <summary>The remainder FrameCadencePolicy keeps (for tests).</summary>
    public float ElapsedSec => elapsedSec;

    /// <summary>One frame of <paramref name="dtSec"/> seconds; true when the refresh is due.</summary>
    // C++: ArenaUI.cpp:217-224 (AdvanceUiRefreshClock) + :3174-3177
    public bool Advance(float dtSec)
    {
        float dt = started ? dtSec : 0.0f;
        started = true;
        if (dt > 1.0f) dt = 1.0f;
        return FrameCadencePolicy.Advance(ref elapsedSec, dt, IntervalSec);
    }

    // C++: ArenaUI.cpp:226-230 (ResetUiRefreshClock)
    public void Reset()
    {
        started = false;
        elapsedSec = IntervalSec;
    }
}
