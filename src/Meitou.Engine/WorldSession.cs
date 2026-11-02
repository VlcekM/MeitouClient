using System.Numerics;
using Meitou.Engine.Cameras;
using Meitou.Engine.Input;
using Meitou.Engine.Time;
using Meitou.Simulation;

namespace Meitou.Engine;

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
            throw new ArgumentException($"the world ticks {settings.TickSeconds} s but the simulation clock {Simulation.TickSeconds} s", nameof(simulation));
        Focus = focus;
        Region = region;
        Ticks = new FixedStepClock(tickRate);
        Clock = clock ?? new GameClock(startHour);
        Bindings = bindings ?? new InputBindings();
        // The game keeps its cameras above the water surface too (docs/formats/camera.md): the lowest ground is the sea.
        Camera = new CameraRig((x, z) => MathF.Max(ground(x, z), Meitou.Data.World.WorldWater.Height));
        worldSettings = settings;
        worldWalkability = walkability ?? new OpenGroundWalkability(ground);
        World = new World(settings, worldWalkability, systems);
        World.Fixtures.Doors = worldWalkability as Meitou.Simulation.Locks.IDoorGate;
    }

    readonly WorldSettings worldSettings;
    readonly IWalkability worldWalkability;

    /// <summary>The point the world was loaded around.</summary>
    public Vector3 Focus { get; }
    /// <summary>The loaded region in world X/Z (the detail streams with the camera beyond it).</summary>
    public (double X0, double Z0, double X1, double Z1) Region { get; }
    /// <summary>The real-time control tick (input actions, camera, UI).</summary>
    public FixedStepClock Ticks { get; }
    /// <summary>The game-time simulation tick.</summary>
    public SimulationClock Simulation { get; } = new();
    public GameClock Clock { get; }
    /// <summary>The simulated world; it ticks with the simulation clock. Replaced as a whole by <see cref="ReplaceWorld"/> (a loaded save); read it again after a frame, do not keep it.</summary>
    public World World { get; private set; }
    /// <summary>
    /// The snapshot the last simulation tick published and the one before it, for interpolating by <see cref="SimulationAlpha"/>. While a tick runs ahead
    /// (<see cref="BeginTickAhead"/>) or waits to fall due, the pair from before it: the picture moves on only when the clock reaches that tick.
    /// </summary>
    public WorldSnapshot CurrentSnapshot => held ? heldCurrent! : World.Snapshot;
    public WorldSnapshot PreviousSnapshot => held ? heldPrevious! : World.PreviousSnapshot;
    /// <summary>The world tick the shown snapshot is of (<see cref="World.Tick"/> less a tick run ahead).</summary>
    public long ShownTick => World.Tick - (aheadDone ? 1 : 0);

    // The tick run ahead (docs/simulation.md "Tick ahead"): the shown pair held meanwhile, the runner, and whether the tick is done but not yet due.
    bool held, aheadDone;
    WorldSnapshot? heldCurrent, heldPrevious;
    TickRunner? runner;
    bool running;

    /// <summary>The wall time (ms) of the last tick run ahead, 0 once taken (for the profiler).</summary>
    public double TakeAheadMs() { double ms = aheadMs; aheadMs = 0; return ms; }
    double aheadMs;

    /// <summary>A world tick's wall time (ms), smoothed over the last ten or so ticks, whether run in the frame or ahead (the host's dynamic tick ahead reads it).</summary>
    public double TickMs { get; private set; }
    void Measure(double ms) => TickMs = TickMs == 0 ? ms : TickMs + (ms - TickMs) * 0.1;
    /// <summary>Where the frame is between the previous and the current simulation tick (the snapshots' alpha, not the camera's).</summary>
    public float SimulationAlpha => Simulation.Alpha;
    public InputState Input { get; } = new();
    public InputBindings Bindings { get; }
    public CameraRig Camera { get; }
    /// <summary>Where the frame is between the previous and the current control tick (camera interpolation).</summary>
    public float Alpha => (float)Ticks.Alpha;
    /// <summary>The speed: 0 while paused by the player, else 1, 2 or 5.</summary>
    public double TimeScale => Simulation.Speed;

    /// <summary>Called for each control tick's actions after the session's own handling (pause, speed, camera); the host's actions (quit, screenshot...).</summary>
    public event Action<ActionState>? Ticked;

    /// <summary>Runs one frame's worth of real time: the control ticks it holds, then the simulation ticks; returns the control ticks.</summary>
    public int Advance(double realSeconds)
    {
        int n = Ticks.Advance(realSeconds);
        for (int i = 0; i < n; i++) Tick();
        AdvanceSimulation(realSeconds);
        return n;
    }

    /// <summary>Feeds real time to the simulation clock only and runs the ticks it owes; returns how many ran.</summary>
    public int AdvanceSimulation(double realSeconds)
    {
        EnsureNotRunning();
        int n = Simulation.Advance(realSeconds);
        int run = n;
        // A tick run ahead is the first one owed. Paused, it is taken at once (the shown pair jumps a tick), so what the interface changes shows.
        if (aheadDone && (n > 0 || Simulation.IsPaused))
        {
            SettleAhead();
            if (n > 0) run--;
        }
        // Only a frame's last two ticks publish: the host draws between those two snapshots, and the ones before would go unseen.
        for (int i = 0; i < run; i++) SimulationTick(publish: i >= run - 2);
        if (Simulation.IsPaused) World.ApplyPausedInterfaceCommands();
        return n;
    }

    /// <summary>
    /// Starts the next simulation tick on a thread of its own, ahead of the clock (docs/simulation.md "Tick ahead"), while the host draws the world from the shown
    /// snapshots; <see cref="EndTickAhead"/> waits for it. Nothing may touch <see cref="World"/> in between. Does nothing while paused or with a tick already ahead.
    /// </summary>
    public void BeginTickAhead()
    {
        EnsureNotRunning();
        if (aheadDone || Simulation.IsPaused) return;
        (heldPrevious, heldCurrent, held) = (World.PreviousSnapshot, World.Snapshot, true);
        World.GameHours = Clock.TotalHours;   // the tick's start, as SimulationTick sets it; the game clock moves when the tick falls due
        World.Daytime = Clock.IsDaytime;
        World.PhysicsSpeed = (float)(Simulation.Speed > 0 ? Simulation.Speed : Simulation.LastNonZeroSpeed);   // the ragdoll physics steps in real time
        runner ??= new TickRunner();
        runner.Start(World);
        running = true;
    }

    /// <summary>Waits for the tick <see cref="BeginTickAhead"/> started (rethrowing what it threw); it then waits to fall due.</summary>
    public void EndTickAhead()
    {
        if (!running) return;
        running = false;
        aheadMs = runner!.Wait();   // one a frame at most: the last one, whether or not the profiler took the one before
        Measure(aheadMs);
        aheadDone = true;
    }

    /// <summary>Takes a tick run ahead as run now: the game clock moves by it and the snapshots shown are the world's again (before a save, say).</summary>
    public void SettleAhead()
    {
        EnsureNotRunning();
        if (!aheadDone) return;
        aheadDone = held = false;
        (heldPrevious, heldCurrent) = (null, null);
        Clock.Advance(Simulation.TickSeconds);
    }

    void EnsureNotRunning()
    {
        if (running) throw new InvalidOperationException("a simulation tick is running ahead: call EndTickAhead first");
    }

    /// <summary>The thread a tick runs ahead on (one per session, idle between ticks).</summary>
    sealed class TickRunner : IDisposable
    {
        readonly Thread thread;
        readonly SemaphoreSlim start = new(0), done = new(0);
        World? world;
        double ms;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure;
        volatile bool stopping;

        public TickRunner()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "Meitou.Tick" };
            thread.Start();
        }

        public void Start(World w)
        {
            (world, failure) = (w, null);
            start.Release();
        }

        public double Wait()
        {
            done.Wait();
            world = null;
            failure?.Throw();
            return ms;
        }

        void Run()
        {
            while (true)
            {
                start.Wait();
                if (stopping) return;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try { world!.RunTick(publish: true); }
                catch (Exception e) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
                ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                done.Release();
            }
        }

        public void Dispose()
        {
            stopping = true;
            start.Release();
        }
    }

    /// <summary>One simulation tick: the world runs its phases (publishing its snapshot when <paramref name="publish"/>), the game clock moves by one tick of game time.</summary>
    void SimulationTick(bool publish)
    {
        World.GameHours = Clock.TotalHours;
        World.Daytime = Clock.IsDaytime;
        World.PhysicsSpeed = (float)(Simulation.Speed > 0 ? Simulation.Speed : Simulation.LastNonZeroSpeed);   // the ragdoll physics steps in real time
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        World.RunTick(publish);
        Measure(System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        Clock.Advance(Simulation.TickSeconds);
    }

    /// <summary>
    /// Swaps in a new world made from <paramref name="systems"/> (same settings and walkability) at a tick boundary, for a loaded save. <paramref name="setup"/>
    /// fills the new world (before its first tick, as <c>SaveLoader.Load</c> needs); when it throws the old world stays and the new one and its systems are disposed.
    /// The old world and its systems are disposed after the swap. The control tick, the camera, the input and the simulation clock carry on; the game clock is the
    /// caller's to set. Call it between frames or from a control tick's handler, never from inside a world tick.
    /// </summary>
    public World ReplaceWorld(IEnumerable<ITickSystem> systems, Action<World>? setup = null)
    {
        EnsureNotRunning();
        var list = systems.ToList();
        var next = new World(worldSettings, worldWalkability, list);
        next.Floaters = World.Floaters;   // the host keeps drawing the floaters across the swap
        next.BenchFacingFix = World.BenchFacingFix;
        try { setup?.Invoke(next); }
        catch
        {
            Discard(next);
            throw;
        }
        var old = World;
        World = next;
        // A tick the old world ran ahead goes with it.
        (aheadDone, held, heldPrevious, heldCurrent) = (false, false, null, null);
        Discard(old);
        // The navmesh door table follows the new world: the old one's locked doors are forgotten, the new one's told.
        if (worldWalkability is Meitou.Simulation.Locks.IDoorGate gate) { gate.ResetDoors(); next.Fixtures.Doors = gate; }
        return next;
    }

    static void Discard(World world)
    {
        world.Dispose();
        foreach (var s in world.Systems) (s as IDisposable)?.Dispose();
    }

    /// <summary>Called when the session is disposed, for the systems that own threads.</summary>
    public void Dispose()
    {
        if (running) { running = false; try { runner!.Wait(); } catch (Exception) { } }   // a tick still ahead finishes first; what it threw no longer matters
        runner?.Dispose();
        Discard(World);
    }

    /// <summary>One control tick: the actions from the input (pause, speed), then the camera.</summary>
    public ActionState Tick()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var actions = Bindings.Resolve(Input.Consume());
        if (actions.Pressed(InputAction.Pause)) Simulation.PauseKey();
        if (actions.Pressed(InputAction.Speed1)) Simulation.SetSpeed(1);
        if (actions.Pressed(InputAction.Speed2)) Simulation.SetSpeed(2);
        if (actions.Pressed(InputAction.Speed3)) Simulation.SetSpeed(5);
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

    /// <summary>The control ticks' wall time since the host last cleared them: the input and camera, and the host's <see cref="Ticked"/> handlers (for the play benchmark).</summary>
    public double CameraMs { get; set; }
    public double HandlersMs { get; set; }

    /// <summary>The camera to draw this frame.</summary>
    public CameraState CameraAt() => Camera.At(Alpha);
}
