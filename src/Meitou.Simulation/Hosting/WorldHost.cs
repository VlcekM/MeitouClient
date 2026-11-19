using System.Numerics;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Session;

namespace Meitou.Simulation.Hosting;

/// <summary>
/// A running world with no window: the world and its systems, the simulation clock and the game clock, the ticks a frame owes, the loading
/// pause and the ticks run ahead of the clock (docs/simulation.md "Time model", "Tick ahead"). It knows no camera and no input: whoever shows
/// the world (the game's <c>WorldSession</c>) sets <see cref="ViewPosition"/> and <see cref="ShownInteriors"/> before the ticks, feeds real time
/// to <see cref="Advance"/> and draws between <see cref="PreviousSnapshot"/> and <see cref="CurrentSnapshot"/>.
/// </summary>
public sealed class WorldHost : IDisposable
{
    #region World

    /// <summary>
    /// The world settings every world of the host is made with (<see cref="ReplaceWorld"/> too).
    /// </summary>
    readonly WorldSettings _settings;
    /// <summary>
    /// The walkability every world of the host uses.
    /// </summary>
    readonly IWalkability _walkability;

    /// <summary>
    /// Makes the host with its first world, its systems and walkability. The world's tick must be the simulation clock's: the game clock advances
    /// by the one and the world by the other.
    /// </summary>
    public WorldHost(WorldSettings settings, IWalkability walkability, IEnumerable<ITickSystem>? systems, GameClock clock)
    {
        if (Math.Abs(settings.TickSeconds - Simulation.TickSeconds) > 1e-6)
            throw new ArgumentException(
                $"the world ticks {settings.TickSeconds} s but the simulation clock {Simulation.TickSeconds} s", nameof(settings));

        _settings = settings;
        _walkability = walkability;
        Clock = clock;
        World = new World(settings, walkability, systems);
        World.Fixtures.Doors = walkability as IDoorGate;
    }

    /// <summary>
    /// The simulated world; it ticks with the simulation clock. Replaced as a whole by <see cref="ReplaceWorld"/> (a loaded save); read it again
    /// after a frame, do not keep it.
    /// </summary>
    public World World { get; private set; }

    /// <summary>
    /// Swaps in a new world made from <paramref name="systems"/> (same settings and walkability) at a tick boundary, for a loaded save. <paramref
    /// name="setup"/> fills the new world (before its first tick, as <c>SaveLoader.Load</c> needs); when it throws the old world stays and the new
    /// one and its systems are disposed. The old world and its systems are disposed after the swap. The simulation clock carries on; the game clock
    /// is the caller's to set. Call it between frames, never from inside a world tick.
    /// </summary>
    public World ReplaceWorld(IEnumerable<ITickSystem> systems, Action<World>? setup = null)
    {
        _ahead.EnsureNotRunning();
        var next = new World(_settings, _walkability, systems.ToList());
        CarryOver(World, next);
        try
        {
            setup?.Invoke(next);
        }
        catch
        {
            Discard(next);
            throw;
        }

        var old = World;
        World = next;

        // Ticks the old world ran ahead go with it, and so does its loading pause.
        _ahead.Forget();
        (Simulation.LoadingHold, ZonesLoading) = (false, false);
        Discard(old);

        // The navmesh door table follows the new world: the old one's locked doors are forgotten, the new one's told.
        if (_walkability is IDoorGate gate)
        {
            gate.ResetDoors();
            next.Fixtures.Doors = gate;
        }

        return next;
    }

    /// <summary>
    /// Gives <paramref name="next"/> what a world keeps across a swap: the floaters the host goes on drawing, the fixes switched on and the
    /// action log.
    /// </summary>
    static void CarryOver(World old, World next)
    {
        next.Floaters = old.Floaters;
        next.BenchFacingFix = old.BenchFacingFix;
        next.PrisonPoseFix = old.PrisonPoseFix;
        next.CagedAnimalFix = old.CagedAnimalFix;
        next.BagSearchFix = old.BagSearchFix;
        next.TakeIntoBackpack = old.TakeIntoBackpack;
        next.ActionLog = old.ActionLog;
    }

    /// <summary>
    /// Disposes a world and its systems that own resources.
    /// </summary>
    static void Discard(World world)
    {
        world.Dispose();
        foreach (var s in world.Systems) (s as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Disposes the world and its systems (some own threads), after any tick still running ahead.
    /// </summary>
    public void Dispose()
    {
        _ahead.Dispose();
        Discard(World);
    }

    #endregion

    #region Ticks

    /// <summary>
    /// The game-time simulation tick.
    /// </summary>
    public SimulationClock Simulation { get; } = new();
    /// <summary>
    /// The game's clock: the hour, the day and the date.
    /// </summary>
    public GameClock Clock { get; }
    /// <summary>
    /// The eye the next ticks take as the viewer's (<see cref="World.ViewPosition"/>); whoever shows the world sets it before they run.
    /// </summary>
    public Vector3 ViewPosition { get; set; }
    /// <summary>
    /// The buildings whose interior the viewer sees, by key, which the next ticks take (<see cref="World.ShownInteriors"/>). Null: no view (no
    /// gate).
    /// </summary>
    public IReadOnlySet<string>? ShownInteriors { get; set; }
    /// <summary>
    /// A world tick's wall time (ms), smoothed over the last ten or so ticks, whether run in the frame or ahead (the game's dynamic tick ahead reads
    /// it).
    /// </summary>
    public double TickMs { get; private set; }

    /// <summary>
    /// Feeds real time to the simulation clock and runs the ticks it owes; returns how many it owed. The ticks run ahead are the first of them
    /// (<see cref="TickAhead.TakeDue"/>).
    /// </summary>
    public int Advance(double realSeconds)
    {
        _ahead.EnsureNotRunning();
        StepLoadingPause();
        int owed = Simulation.Advance(realSeconds);
        _ahead.NoteOwed(owed, Simulation);
        int run = owed - Math.Min(owed, _ahead.TakeDue(owed, Simulation, Clock));

        // Only a frame's last two ticks publish: the viewer draws between those two snapshots, and the ones before would go unseen.
        for (int i = 0; i < run; i++) SimulationTick(publish: i >= run - 2);
        if (Simulation.IsPaused) World.ApplyPausedInterfaceCommands();

        return owed;
    }

    /// <summary>
    /// Runs <paramref name="ticks"/> simulation ticks now while the clock is paused (the agent control channel's <c>step</c>); only the last two
    /// publish a snapshot. Returns the world's tick afterwards.
    /// </summary>
    public long Step(int ticks)
    {
        _ahead.EnsureNotRunning();
        if (!Simulation.IsPaused) throw new InvalidOperationException("step needs the simulation paused");

        SettleAhead();
        for (int i = 0; i < ticks; i++) SimulationTick(publish: i >= ticks - 2);
        return World.Tick;
    }

    /// <summary>
    /// While the clock is paused: applies the interface commands queued so far now (<see cref="World.ApplyPausedInterfaceCommands"/>) and publishes
    /// the snapshot, so the control channel's reply already shows their effect. Returns false (nothing done) while the game runs; its next tick takes
    /// them.
    /// </summary>
    public bool ApplyPausedCommandsNow()
    {
        _ahead.EnsureNotRunning();
        if (!Simulation.IsPaused) return false;

        SettleAhead();
        World.ApplyPausedInterfaceCommands();
        return true;
    }

    /// <summary>
    /// One simulation tick: the world runs its phases (publishing its snapshot when <paramref name="publish"/>), the game clock moves by one tick of
    /// game time.
    /// </summary>
    void SimulationTick(bool publish)
    {
        World.GameHours = Clock.TotalHours;
        World.Daytime = Clock.IsDaytime;

        // the ragdoll physics steps in real time
        World.PhysicsSpeed = (float)(Simulation.Speed > 0 ? Simulation.Speed : Simulation.LastNonZeroSpeed);
        World.ViewPosition = ViewPosition;
        World.ShownInteriors = ShownInteriors;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        World.RunTick(publish);
        Measure(System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        Clock.Advance(Simulation.TickSeconds);
    }

    /// <summary>
    /// Folds a tick's wall time into <see cref="TickMs"/> (a tenth of the change each tick).
    /// </summary>
    void Measure(double ms) => TickMs = TickMs == 0 ? ms : TickMs + (ms - TickMs) * 0.1;

    #endregion

    #region Loading pause

    /// <summary>
    /// The original's loading pause is on (Advanced settings "Pause while loading", its Faithful side; <c>UserConfig.LoadingPause</c>): the game is
    /// held while the zones round the camera load (<see cref="SimulationClock.LoadingHold"/>, docs/game/game-loop.md "Loading pause").
    /// </summary>
    public bool LoadingPause { get; set; }
    /// <summary>
    /// With <see cref="LoadingPause"/> on, the camera's load is under way: the HUD shows the original's <c>Loading...</c> panel.
    /// </summary>
    public bool ZonesLoading { get; private set; }

    /// <summary>
    /// The frame's loading pause, before its ticks are counted: off, nothing is held; on, the world holds or lets go
    /// (<see cref="World.StepLoadingPause"/>). A world paused otherwise is not held, as the original tests only while its paused flag is clear,
    /// but its zones go on loading.
    /// </summary>
    void StepLoadingPause()
    {
        if (!LoadingPause)
        {
            (Simulation.LoadingHold, ZonesLoading) = (false, false);
            return;
        }

        bool held = Simulation.LoadingHold;
        (Simulation.LoadingHold, ZonesLoading) = World.StepLoadingPause(held, !held && Simulation.IsPaused);
    }

    #endregion

    #region Tick ahead

    /// <summary>
    /// The ticks run ahead of the clock, and the snapshot pair shown meanwhile.
    /// </summary>
    readonly TickAhead _ahead = new();

    /// <summary>
    /// The snapshot the last simulation tick published and the one before it, for interpolating by the simulation clock's alpha. While ticks run
    /// ahead (<see cref="BeginTickAhead"/>) or wait to fall due, the pair from before them: the picture moves on only when the clock reaches the last
    /// of them.
    /// </summary>
    public WorldSnapshot CurrentSnapshot => _ahead.Current(World);
    /// <summary>
    /// The snapshot before <see cref="CurrentSnapshot"/> (held while ticks run ahead).
    /// </summary>
    public WorldSnapshot PreviousSnapshot => _ahead.Previous(World);
    /// <summary>
    /// The world tick the shown snapshot is of (<see cref="World.Tick"/> less the ticks run ahead).
    /// </summary>
    public long ShownTick => _ahead.ShownTick(World);
    /// <summary>
    /// The last ticks run ahead's ms, left for <see cref="TakeAheadMs"/>.
    /// </summary>
    public double AheadMs => _ahead.Ms;

    /// <summary>
    /// The wall time (ms) of the last ticks run ahead, 0 once taken (for the profiler).
    /// </summary>
    public double TakeAheadMs() => _ahead.TakeMs();

    /// <summary>
    /// Starts the next simulation ticks on a thread of their own, ahead of the clock (docs/simulation.md "Tick ahead"), while the viewer draws the
    /// world from the shown snapshots; <see cref="EndTickAhead"/> waits for them. Nothing may touch <see cref="World"/> in between. Does nothing
    /// while paused or with ticks already ahead.
    /// </summary>
    public void BeginTickAhead()
    {
        _ahead.EnsureNotRunning();
        if (_ahead.Pending || Simulation.IsPaused) return;

        World.PhysicsSpeed = (float)Simulation.Speed;   // the ragdoll physics steps in real time
        World.ViewPosition = ViewPosition;
        World.ShownInteriors = ShownInteriors;
        _ahead.Begin(World, Simulation, Clock);
    }

    /// <summary>
    /// Waits for the ticks <see cref="BeginTickAhead"/> started (rethrowing what they threw); they then wait to fall due.
    /// </summary>
    public void EndTickAhead()
    {
        if (_ahead.End() is { } ms) Measure(ms);
    }

    /// <summary>
    /// Takes the ticks run ahead as run now: the game clock moves by them and the snapshots shown are the world's again (before a save, say).
    /// </summary>
    public void SettleAhead()
    {
        _ahead.EnsureNotRunning();
        _ahead.Settle(Simulation, Clock);
    }

    #endregion
}
