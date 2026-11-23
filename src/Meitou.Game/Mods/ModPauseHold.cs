using Meitou.Simulation.Session;

namespace Meitou.Modding;

/// <summary>
/// The pause holds every mod's <see cref="ModInterface.HoldPause"/> shares on the game clock: the speeds the first hold found, put back when the last
/// is released while the game is still paused. A new interface (a new session) drops them. Host thread only.
/// </summary>
sealed class ModPauseHolds
{
    /// <summary>
    /// The clock held, the speeds it had at the first hold, and the holds not yet released.
    /// </summary>
    SimulationClock? _clock;
    SimulationClock.SpeedState _saved;
    int _count;

    /// <summary>
    /// Bumped when the holds are dropped, so a hold from before releases nothing.
    /// </summary>
    internal int Generation { get; private set; }

    /// <summary>
    /// Drops every hold without touching the clock (its session is gone).
    /// </summary>
    internal void Drop()
    {
        _clock = null;
        _count = 0;
        Generation++;
    }

    /// <summary>
    /// A new hold on <paramref name="clock"/>: the first keeps the speeds; each pauses the game (the pause key's pause) when it runs.
    /// </summary>
    internal void Hold(SimulationClock clock)
    {
        if (_count == 0 || !ReferenceEquals(clock, _clock))
        {
            (_clock, _saved, _count) = (clock, clock.SaveSpeeds(), 0);
        }

        _count++;
        if (clock.Speed != 0) clock.Pause();
    }

    /// <summary>
    /// Releases a hold of <paramref name="generation"/>; the last puts the kept speeds back when the game is still paused (the player did not choose a
    /// speed meanwhile).
    /// </summary>
    internal void Release(int generation)
    {
        if (generation != Generation || _count == 0) return;
        if (--_count > 0) return;

        if (_clock is { Speed: 0 } clock) clock.RestoreSpeeds(_saved);
        _clock = null;
    }
}

/// <summary>
/// One mod's hold on the game's pause (<see cref="ModInterface.HoldPause"/>): released by <see cref="Release"/> or <see cref="Dispose"/>, once.
/// </summary>
public sealed class ModPauseHold : IDisposable
{
    readonly ModInterface _owner;
    readonly int _generation;

    internal ModPauseHold(ModInterface owner, int generation)
    {
        _owner = owner;
        _generation = generation;
    }

    /// <summary>
    /// Whether the hold is still held (not released, and its session still the interface's).
    /// </summary>
    public bool Held => !Released && _owner.Holds(_generation);

    /// <summary>
    /// Whether <see cref="Release"/> has run.
    /// </summary>
    bool Released { get; set; }

    /// <summary>
    /// Releases the hold (on the host's thread; a second call does nothing); false when it was not held or the call is refused.
    /// </summary>
    public bool Release()
    {
        if (Released || !_owner.Release(_generation)) return false;

        Released = true;
        return true;
    }

    /// <inheritdoc/>
    public void Dispose() => Release();
}
