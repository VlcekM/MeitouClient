using System.Numerics;
using Meitou.Game.Host.Cameras;
using Meitou.Game.Host.Input;
using Meitou.Simulation;
using Meitou.Simulation.Session;

namespace Meitou.Game.Host;

/// <summary>
/// A running world: what is loaded (the region around <see cref="Focus"/>), the two clocks, the game clock, the input and the
/// camera. The host feeds <see cref="Input"/> from its window and calls <see cref="Advance"/> once per displayed frame.
/// <list type="bullet">
/// <item>The <b>control tick</b> (<see cref="Ticks"/>, 30 Hz of real time, whatever the speed) consumes the input, handles pause and
/// speed and moves the camera, so the camera moves while the game is paused. The renderer draws <see cref="Camera"/> at <see cref="Alpha"/>.</item>
/// <item>The <b>simulation tick</b> (<see cref="Simulation"/>, 1/30 s of game time, 30/60/150 per real second at 1x/2x/5x, none while
/// paused) advances <see cref="Clock"/>. Within a frame all control ticks run first (they may change the speed), then the
/// simulation ticks the speed they left allows (docs/simulation.md "Time model").</item>
/// </list>
/// </summary>
public sealed class WorldSession : IDisposable
{
    public WorldSession(Vector3 focus, (double X0, double Z0, double X1, double Z1) region, Func<float, float, float> ground,
        double startHour = GameClock.DefaultStartHour, double tickRate = FixedStepClock.DefaultTickRate, InputBindings? bindings = null,
        GameClock? clock = null, WorldSettings? simulation = null, IEnumerable<ITickSystem>? systems = null,
        IWalkability? walkability = null)
    {
        // The game clock advances by the simulation clock's tick and the world by its own: they must be the same length.
        var settings = simulation ?? new WorldSettings();
        if (Math.Abs(settings.TickSeconds - Simulation.TickSeconds) > 1e-6)
            throw new ArgumentException(
                $"the world ticks {settings.TickSeconds} s but the simulation clock {Simulation.TickSeconds} s", nameof(simulation));
        Focus = focus;
        Region = region;
        Ticks = new FixedStepClock(tickRate);
        Clock = clock ?? new GameClock(startHour);
        Bindings = bindings ?? new InputBindings();
        // The game keeps its cameras above the water surface too (docs/formats/camera.md): the lowest ground is the sea.
        Camera = new CameraRig((x, z) => MathF.Max(ground(x, z), Data.World.WorldWater.Height));
        _worldSettings = settings;
        _worldWalkability = walkability ?? new OpenGroundWalkability(ground);
        World = new World(settings, _worldWalkability, systems);
        World.Fixtures.Doors = _worldWalkability as Simulation.Locks.IDoorGate;
    }

    readonly WorldSettings _worldSettings;
    readonly IWalkability _worldWalkability;

    /// <summary>
    /// The point the world was loaded around.
    /// </summary>
    public Vector3 Focus { get; }
    /// <summary>
    /// The loaded region in world X/Z (the detail streams with the camera beyond it).
    /// </summary>
    public (double X0, double Z0, double X1, double Z1) Region { get; }
    /// <summary>
    /// The real-time control tick (input actions, camera, UI).
    /// </summary>
    public FixedStepClock Ticks { get; }
    /// <summary>
    /// The game-time simulation tick.
    /// </summary>
    public SimulationClock Simulation { get; } = new();
    public GameClock Clock { get; }
    /// <summary>
    /// The simulated world; it ticks with the simulation clock. Replaced as a whole by <see cref="ReplaceWorld"/> (a loaded save); read it again
    /// after a frame, do not keep it.
    /// </summary>
    public World World { get; private set; }
    /// <summary>
    /// The snapshot the last simulation tick published and the one before it, for interpolating by <see cref="SimulationAlpha"/>. While ticks run
    /// ahead (<see cref="BeginTickAhead"/>) or wait to fall due, the pair from before them: the picture moves on only when the clock reaches the last
    /// of them.
    /// </summary>
    public WorldSnapshot CurrentSnapshot => _held ? _heldCurrent! : World.Snapshot;
    public WorldSnapshot PreviousSnapshot => _held ? _heldPrevious! : World.PreviousSnapshot;
    /// <summary>
    /// The world tick the shown snapshot is of (<see cref="World.Tick"/> less the ticks run ahead).
    /// </summary>
    public long ShownTick => _held ? _heldTick : World.Tick;

    // The ticks run ahead (docs/simulation.md "Tick ahead"): the shown pair and its tick held meanwhile, the runner, and how many are run but not yet
    // due.
    bool _held;
    long _heldTick;
    int _aheadTicks;
    // The ticks a frame owes, smoothed (how many the next batch runs ahead), and the speed they were owed at.
    double _owedPerFrame = 1, _owedSpeed;
    WorldSnapshot? _heldCurrent, _heldPrevious;
    TickRunner? _runner;
    bool _running;

    /// <summary>
    /// The wall time (ms) of the last ticks run ahead, 0 once taken (for the profiler).
    /// </summary>
    public double TakeAheadMs() { double ms = _aheadMs; _aheadMs = 0; return ms; }
    /// <summary>
    /// The last ticks run ahead's ms, left for <see cref="TakeAheadMs"/>.
    /// </summary>
    public double AheadMs => _aheadMs;
    double _aheadMs;

    /// <summary>
    /// A world tick's wall time (ms), smoothed over the last ten or so ticks, whether run in the frame or ahead (the host's dynamic tick ahead reads
    /// it).
    /// </summary>
    public double TickMs { get; private set; }
    void Measure(double ms) => TickMs = TickMs == 0 ? ms : TickMs + (ms - TickMs) * 0.1;
    /// <summary>
    /// Where the frame is between the previous and the current simulation tick (the snapshots' alpha, not the camera's).
    /// </summary>
    public float SimulationAlpha => Simulation.Alpha;
    public InputState Input { get; } = new();
    public InputBindings Bindings { get; }
    public CameraRig Camera { get; }
    /// <summary>
    /// The eye the simulation takes as the camera's (<see cref="World.ViewPosition"/>) instead of the rig's, while the host holds the view elsewhere
    /// (a character flight, a cinema take); null: the rig's.
    /// </summary>
    public Vector3? ViewEye { get; set; }
    /// <summary>
    /// Where the frame is between the previous and the current control tick (camera interpolation).
    /// </summary>
    public float Alpha => (float)Ticks.Alpha;
    /// <summary>
    /// The speed: 0 while paused by the player, else 1, 2 or 5.
    /// </summary>
    public double TimeScale => Simulation.Speed;

    /// <summary>
    /// Called for each control tick's actions after the session's own handling (pause, speed, camera); the host's actions (quit, screenshot...).
    /// </summary>
    public event Action<ActionState>? Ticked;

    /// <summary>
    /// Runs one frame's worth of real time: the control ticks it holds, then the simulation ticks; returns the control ticks.
    /// </summary>
    public int Advance(double realSeconds)
    {
        int n = Ticks.Advance(realSeconds);
        for (int i = 0; i < n; i++) Tick();
        AdvanceSimulation(realSeconds);
        return n;
    }

    /// <summary>
    /// Feeds real time to the simulation clock only and runs the ticks it owes; returns how many ran.
    /// </summary>
    public int AdvanceSimulation(double realSeconds)
    {
        EnsureNotRunning();
        int n = Simulation.Advance(realSeconds);
        if (!Simulation.IsPaused)
        {
            // A speed change scales the estimate at once: a batch sized for 5x would hold the picture for frames at 1x.
            if (_owedSpeed > 0 && Simulation.Speed != _owedSpeed) _owedPerFrame *= Simulation.Speed / _owedSpeed;
            _owedSpeed = Simulation.Speed;
            _owedPerFrame += (n - _owedPerFrame) * 0.25;
        }
        int run = n;
        // Ticks run ahead are the first ones owed; the shown pair moves on once the last of them is due. Paused, they are taken at once (the shown
        // pair jumps), so what the interface changes shows.
        if (_aheadTicks > 0 && (n > 0 || Simulation.IsPaused))
        {
            int take = Simulation.IsPaused ? _aheadTicks : Math.Min(n, _aheadTicks);
            for (int i = 0; i < take; i++) Clock.Advance(Simulation.TickSeconds);
            _aheadTicks -= take;
            run -= Math.Min(run, take);
            if (_aheadTicks == 0) (_held, _heldPrevious, _heldCurrent) = (false, null, null);
        }
        // Only a frame's last two ticks publish: the host draws between those two snapshots, and the ones before would go unseen.
        for (int i = 0; i < run; i++) SimulationTick(publish: i >= run - 2);
        if (Simulation.IsPaused) World.ApplyPausedInterfaceCommands();
        return n;
    }

    /// <summary>
    /// Runs <paramref name="ticks"/> simulation ticks now while the clock is paused (the agent control channel's <c>step</c>); only the last two
    /// publish a snapshot. Returns the world's tick afterwards.
    /// </summary>
    public long Step(int ticks)
    {
        EnsureNotRunning();
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
        EnsureNotRunning();
        if (!Simulation.IsPaused) return false;
        SettleAhead();
        World.ApplyPausedInterfaceCommands();
        return true;
    }

    /// <summary>
    /// Starts the next simulation ticks on a thread of their own, ahead of the clock (docs/simulation.md "Tick ahead"), while the host draws the
    /// world from the shown snapshots; <see cref="EndTickAhead"/> waits for them. As many run as a frame has owed lately (at least one, at most the
    /// frame budget), so at 2x and 5x a frame's ticks overlap the draw too. Nothing may touch <see cref="World"/> in between. Does nothing while
    /// paused or with ticks already ahead.
    /// </summary>
    public void BeginTickAhead()
    {
        EnsureNotRunning();
        if (_aheadTicks > 0 || Simulation.IsPaused) return;
        // Rounded down (a tick short runs in the next frame; one too many holds the picture a frame), plus a hair: the average nears a steady count
        // from below.
        int count = (int)Math.Clamp(Math.Floor(_owedPerFrame + 0.1), 1, Simulation.MaxTicksPerFrame * Simulation.Speed);
        (_heldPrevious, _heldCurrent, _heldTick, _held) = (World.PreviousSnapshot, World.Snapshot, World.Tick, true);
        // Each tick's start, as SimulationTick sets it (the clock summed tick by tick); the game clock moves when the ticks fall due.
        var starts = new (double Hours, bool Daytime)[count];
        double seconds = Clock.GameSeconds;
        for (int i = 0; i < count; i++, seconds += Simulation.TickSeconds) starts[i] = (Clock.TotalHoursAt(seconds), Clock.IsDaytimeAt(seconds));
        World.PhysicsSpeed = (float)Simulation.Speed;   // the ragdoll physics steps in real time
        World.ViewPosition = ViewEye ?? Camera.Current.Eye;
        _runner ??= new TickRunner();
        _runner.Start(World, starts);
        _running = true;
        _aheadTicks = count;
    }

    /// <summary>
    /// Waits for the ticks <see cref="BeginTickAhead"/> started (rethrowing what they threw); they then wait to fall due.
    /// </summary>
    public void EndTickAhead()
    {
        if (!_running) return;
        _running = false;
        _aheadMs = _runner!.Wait();   // one batch a frame at most: the last one, whether or not the profiler took the one before
        Measure(_aheadMs / _aheadTicks);
    }

    /// <summary>
    /// Takes the ticks run ahead as run now: the game clock moves by them and the snapshots shown are the world's again (before a save, say).
    /// </summary>
    public void SettleAhead()
    {
        EnsureNotRunning();
        if (_aheadTicks == 0) return;
        for (; _aheadTicks > 0; _aheadTicks--) Clock.Advance(Simulation.TickSeconds);
        (_held, _heldPrevious, _heldCurrent) = (false, null, null);
    }

    void EnsureNotRunning()
    {
        if (_running) throw new InvalidOperationException("a simulation tick is running ahead: call EndTickAhead first");
    }

    /// <summary>
    /// The thread ticks run ahead on (one per session, idle between batches).
    /// </summary>
    sealed class TickRunner : IDisposable
    {
        readonly Thread _thread;
        readonly SemaphoreSlim _start = new(0), _done = new(0);
        World? _world;
        (double Hours, bool Daytime)[] _starts = [];
        double _ms;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? _failure;
        volatile bool _stopping;

        public TickRunner()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Meitou.Tick" };
            _thread.Start();
        }

        public void Start(World w, (double Hours, bool Daytime)[] tickStarts)
        {
            (_world, _starts, _failure) = (w, tickStarts, null);
            _start.Release();
        }

        public double Wait()
        {
            _done.Wait();
            _world = null;
            _failure?.Throw();
            return _ms;
        }

        void Run()
        {
            while (true)
            {
                _start.Wait();
                if (_stopping) return;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    // Only the last two publish, as in a frame (the shown pair keeps its buffers: World's snapshot ring).
                    for (int i = 0; i < _starts.Length; i++)
                    {
                        (_world!.GameHours, _world.Daytime) = _starts[i];
                        _world.RunTick(publish: i >= _starts.Length - 2);
                    }
                }
                catch (Exception e)
                {
                    _failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e);
                }
                _ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                _done.Release();
            }
        }

        public void Dispose()
        {
            _stopping = true;
            _start.Release();
        }
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
        World.ViewPosition = ViewEye ?? Camera.Current.Eye;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        World.RunTick(publish);
        Measure(System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        Clock.Advance(Simulation.TickSeconds);
    }

    /// <summary>
    /// Swaps in a new world made from <paramref name="systems"/> (same settings and walkability) at a tick boundary, for a loaded save. <paramref
    /// name="setup"/> fills the new world (before its first tick, as <c>SaveLoader.Load</c> needs); when it throws the old world stays and the new
    /// one and its systems are disposed. The old world and its systems are disposed after the swap. The control tick, the camera, the input and the
    /// simulation clock carry on; the game clock is the caller's to set. Call it between frames or from a control tick's handler, never from inside a
    /// world tick.
    /// </summary>
    public World ReplaceWorld(IEnumerable<ITickSystem> systems, Action<World>? setup = null)
    {
        EnsureNotRunning();
        var list = systems.ToList();
        var next = new World(_worldSettings, _worldWalkability, list);
        next.Floaters = World.Floaters;   // the host keeps drawing the floaters across the swap
        next.BenchFacingFix = World.BenchFacingFix;
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
        // Ticks the old world ran ahead go with it.
        (_aheadTicks, _held, _heldPrevious, _heldCurrent) = (0, false, null, null);
        Discard(old);
        // The navmesh door table follows the new world: the old one's locked doors are forgotten, the new one's told.
        if (_worldWalkability is Simulation.Locks.IDoorGate gate)
        {
            gate.ResetDoors();
            next.Fixtures.Doors = gate;
        }
        return next;
    }

    static void Discard(World world)
    {
        world.Dispose();
        foreach (var s in world.Systems) (s as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Called when the session is disposed, for the systems that own threads.
    /// </summary>
    public void Dispose()
    {
        // A tick still ahead finishes first; what it threw no longer matters.
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
        Discard(World);
    }

    /// <summary>
    /// One control tick: the actions from the input (pause, speed), then the camera.
    /// </summary>
    public ActionState Tick()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var actions = Bindings.Resolve(Input.Consume());
        if (actions.Pressed(InputAction.Pause)) Simulation.PauseKey();
        if (actions.Pressed(InputAction.Speed1)) Simulation.SetSpeed(1);
        if (actions.Pressed(InputAction.Speed2)) Simulation.SetSpeed(Simulation.Speeds[1]);
        if (actions.Pressed(InputAction.Speed3)) Simulation.SetSpeed(Simulation.Speeds[2]);
        if (actions.Pressed(InputAction.TimeFaster)) Simulation.Faster();
        if (actions.Pressed(InputAction.TimeSlower)) Simulation.Slower();
        Camera.Update((float)Ticks.TickSeconds, actions);
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        Ticked?.Invoke(actions);
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        CameraMs += (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        HandlersMs += (t2 - t1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return actions;
    }

    /// <summary>
    /// The control ticks' wall time since the host last cleared them: the input and camera, and the host's <see cref="Ticked"/> handlers (for the
    /// play benchmark).
    /// </summary>
    public double CameraMs { get; set; }
    public double HandlersMs { get; set; }

    /// <summary>
    /// The camera to draw this frame.
    /// </summary>
    public CameraState CameraAt() => Camera.At(Alpha);
}