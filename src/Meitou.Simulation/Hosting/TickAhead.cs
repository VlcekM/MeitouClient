using Meitou.Simulation.Session;

namespace Meitou.Simulation.Hosting;

/// <summary>
/// The ticks a host runs ahead of the clock on a thread of their own (docs/simulation.md "Tick ahead"): how many the next batch holds, the
/// snapshot pair shown while they run and wait to fall due, and the batch's thread. The ticks are the next ones owed: the game clock moves as
/// each falls due.
/// </summary>
sealed class TickAhead : IDisposable
{
    /// <summary>
    /// Whether the shown snapshot pair and its tick are held while ticks run ahead.
    /// </summary>
    bool _held;
    /// <summary>
    /// The world tick of the held pair.
    /// </summary>
    long _heldTick;
    /// <summary>
    /// The ticks run ahead and not yet due.
    /// </summary>
    int _aheadTicks;
    /// <summary>
    /// The ticks a frame owes, smoothed (how many the next batch runs ahead), and the speed they were owed at.
    /// </summary>
    double _owedPerFrame = 1, _owedSpeed;
    /// <summary>
    /// The snapshot pair shown while ticks run ahead.
    /// </summary>
    WorldSnapshot? _heldCurrent, _heldPrevious;
    /// <summary>
    /// The thread ticks run ahead on, made on first use.
    /// </summary>
    TickRunner? _runner;
    /// <summary>
    /// Whether ticks run ahead now (between <see cref="Begin"/> and <see cref="End"/>); nothing may touch the world then.
    /// </summary>
    bool _running;
    /// <summary>
    /// The wall time (ms) of the last ticks run ahead, until <see cref="TakeMs"/> takes it.
    /// </summary>
    double _aheadMs;

    /// <summary>
    /// Whether ticks run ahead wait to fall due.
    /// </summary>
    public bool Pending => _aheadTicks > 0;

    /// <summary>
    /// The last ticks run ahead's ms, left for <see cref="TakeMs"/>.
    /// </summary>
    public double Ms => _aheadMs;

    /// <summary>
    /// The snapshot to show: the held one while ticks run ahead or wait to fall due, else <paramref name="world"/>'s.
    /// </summary>
    public WorldSnapshot Current(World world) => _held ? _heldCurrent! : world.Snapshot;

    /// <summary>
    /// The snapshot before <see cref="Current"/>.
    /// </summary>
    public WorldSnapshot Previous(World world) => _held ? _heldPrevious! : world.PreviousSnapshot;

    /// <summary>
    /// The world tick the shown snapshot is of (<see cref="World.Tick"/> less the ticks run ahead).
    /// </summary>
    public long ShownTick(World world) => _held ? _heldTick : world.Tick;

    /// <summary>
    /// The wall time (ms) of the last ticks run ahead, 0 once taken (for the profiler).
    /// </summary>
    public double TakeMs()
    {
        double ms = _aheadMs;
        _aheadMs = 0;
        return ms;
    }

    /// <summary>
    /// Throws while ticks run ahead: nothing may touch the world then.
    /// </summary>
    public void EnsureNotRunning()
    {
        if (_running) throw new InvalidOperationException("a simulation tick is running ahead: call EndTickAhead first");
    }

    /// <summary>
    /// Counts the <paramref name="owed"/> ticks of a frame into the size of the next batch; nothing while paused.
    /// </summary>
    public void NoteOwed(int owed, SimulationClock simulation)
    {
        if (simulation.IsPaused) return;

        // A speed change scales the estimate at once: a batch sized for 5x would hold the picture for frames at 1x.
        if (_owedSpeed > 0 && simulation.Speed != _owedSpeed) _owedPerFrame *= simulation.Speed / _owedSpeed;
        _owedSpeed = simulation.Speed;
        _owedPerFrame += (owed - _owedPerFrame) * 0.25;
    }

    /// <summary>
    /// Takes the ticks run ahead that fall due in a frame owing <paramref name="owed"/> ticks and returns how many: they are the first ones owed,
    /// and <paramref name="clock"/> moves by them. The shown pair moves on once the last of them is due. Paused, all are taken at once (the shown
    /// pair jumps), so what the interface changes shows.
    /// </summary>
    public int TakeDue(int owed, SimulationClock simulation, GameClock clock)
    {
        if (_aheadTicks == 0 || (owed == 0 && !simulation.IsPaused)) return 0;

        int take = simulation.IsPaused ? _aheadTicks : Math.Min(owed, _aheadTicks);
        for (int i = 0; i < take; i++) clock.Advance(simulation.TickSeconds);
        _aheadTicks -= take;
        if (_aheadTicks == 0) (_held, _heldPrevious, _heldCurrent) = (false, null, null);

        return take;
    }

    /// <summary>
    /// Starts the next ticks of <paramref name="world"/> on the runner's thread, ahead of <paramref name="clock"/>: as many as a frame has owed
    /// lately (at least one, at most the frame budget), so at 2x and 5x a frame's ticks overlap the draw too. The pair shown is held from here on.
    /// </summary>
    public void Begin(World world, SimulationClock simulation, GameClock clock)
    {
        // Rounded down (a tick short runs in the next frame; one too many holds the picture a frame), plus a hair: the average nears a steady count
        // from below. The limit at least 1: under half speed (photo mode's slow motion) it fell below the least.
        int count = (int)Math.Clamp(Math.Floor(_owedPerFrame + 0.1), 1, Math.Max(1, simulation.MaxTicksPerFrame * simulation.Speed));
        (_heldPrevious, _heldCurrent, _heldTick, _held) = (world.PreviousSnapshot, world.Snapshot, world.Tick, true);

        // Each tick's start, as a tick in the frame sets it (the clock summed tick by tick); the game clock moves when the ticks fall due.
        var starts = new (double Hours, bool Daytime)[count];
        double seconds = clock.GameSeconds;
        for (int i = 0; i < count; i++, seconds += simulation.TickSeconds) starts[i] = (clock.TotalHoursAt(seconds), clock.IsDaytimeAt(seconds));

        _runner ??= new TickRunner();
        _runner.Start(world, starts);
        _running = true;
        _aheadTicks = count;
    }

    /// <summary>
    /// Waits for the ticks <see cref="Begin"/> started (rethrowing what they threw) and returns one tick's wall time in ms; null when none run.
    /// They then wait to fall due.
    /// </summary>
    public double? End()
    {
        if (!_running) return null;

        _running = false;
        _aheadMs = _runner!.Wait();   // one batch a frame at most: the last one, whether or not the profiler took the one before
        return _aheadMs / _aheadTicks;
    }

    /// <summary>
    /// Takes the ticks run ahead as run now: <paramref name="clock"/> moves by them and the snapshots shown are the world's again.
    /// </summary>
    public void Settle(SimulationClock simulation, GameClock clock)
    {
        if (_aheadTicks == 0) return;

        for (; _aheadTicks > 0; _aheadTicks--) clock.Advance(simulation.TickSeconds);
        (_held, _heldPrevious, _heldCurrent) = (false, null, null);
    }

    /// <summary>
    /// Forgets the ticks run ahead and the held pair: they go with a world that is replaced.
    /// </summary>
    public void Forget() => (_aheadTicks, _held, _heldPrevious, _heldCurrent) = (0, false, null, null);

    /// <summary>
    /// Ends the runner's thread. A batch still running finishes first; what it threw no longer matters.
    /// </summary>
    public void Dispose()
    {
        if (_running)
        {
            _running = false;
            try
            {
                _runner!.Wait();
            }
            catch (Exception)
            {
            }
        }

        _runner?.Dispose();
    }
}
