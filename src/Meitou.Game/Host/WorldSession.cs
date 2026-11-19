using System.Numerics;
using Meitou.Game.Host.Cameras;
using Meitou.Game.Host.Input;
using Meitou.Simulation;
using Meitou.Simulation.Hosting;
using Meitou.Simulation.Session;

namespace Meitou.Game.Host;

/// <summary>
/// The local player's seat at a running world: what is loaded (the region around <see cref="Focus"/>), the control tick, the input and the
/// camera, over the <see cref="Host"/> that runs the world itself (its clocks, its ticks, the ticks run ahead). The game host feeds
/// <see cref="Input"/> from its window and calls <see cref="Advance"/> once per displayed frame.
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
    #region Session

    /// <summary>
    /// Makes the session: the bindings, the camera rig over the ground or the sea, and the host with its clocks and its world (the systems and
    /// the walkability, open ground when none is given).
    /// </summary>
    public WorldSession(Vector3 focus, (double X0, double Z0, double X1, double Z1) region, Func<float, float, float> ground,
        double startHour = GameClock.DefaultStartHour, double tickRate = FixedStepClock.DefaultTickRate, InputBindings? bindings = null,
        GameClock? clock = null, WorldSettings? simulation = null, IEnumerable<ITickSystem>? systems = null,
        IWalkability? walkability = null)
    {
        Host = new WorldHost(simulation ?? new WorldSettings(), walkability ?? new OpenGroundWalkability(ground), systems,
            clock ?? new GameClock(startHour));
        Focus = focus;
        Region = region;
        Ticks = new FixedStepClock(tickRate);
        Bindings = bindings ?? new InputBindings();

        // The game keeps its cameras above the water surface too (docs/formats/camera.md): the lowest ground is the sea.
        Camera = new CameraRig((x, z) => MathF.Max(ground(x, z), Data.World.WorldWater.Height));
    }

    /// <summary>
    /// The world's host: the world, its clocks and its ticks, with no camera and no input (<see cref="GiveView"/> hands it the eye).
    /// </summary>
    public WorldHost Host { get; }
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
    /// The game-time simulation tick (the host's).
    /// </summary>
    public SimulationClock Simulation => Host.Simulation;
    /// <summary>
    /// The game's clock: the hour, the day and the date (the host's).
    /// </summary>
    public GameClock Clock => Host.Clock;
    /// <summary>
    /// The simulated world (the host's); it ticks with the simulation clock. Replaced as a whole by <see cref="ReplaceWorld"/> (a loaded save);
    /// read it again after a frame, do not keep it.
    /// </summary>
    public World World => Host.World;
    /// <summary>
    /// The snapshot the last simulation tick published and the one before it, for interpolating by <see cref="SimulationAlpha"/>. While ticks run
    /// ahead (<see cref="BeginTickAhead"/>) or wait to fall due, the pair from before them: the picture moves on only when the clock reaches the last
    /// of them.
    /// </summary>
    public WorldSnapshot CurrentSnapshot => Host.CurrentSnapshot;
    /// <summary>
    /// The snapshot before <see cref="CurrentSnapshot"/> (held while ticks run ahead).
    /// </summary>
    public WorldSnapshot PreviousSnapshot => Host.PreviousSnapshot;
    /// <summary>
    /// The world tick the shown snapshot is of (<see cref="World.Tick"/> less the ticks run ahead).
    /// </summary>
    public long ShownTick => Host.ShownTick;

    /// <summary>
    /// Where the frame is between the previous and the current simulation tick (the snapshots' alpha, not the camera's).
    /// </summary>
    public float SimulationAlpha => Simulation.Alpha;
    /// <summary>
    /// The input the host feeds from its window; each control tick consumes it.
    /// </summary>
    public InputState Input { get; } = new();
    /// <summary>
    /// The key and button bindings of the actions.
    /// </summary>
    public InputBindings Bindings { get; }
    /// <summary>
    /// The strategy and free cameras, moved by the control ticks.
    /// </summary>
    public CameraRig Camera { get; }
    /// <summary>
    /// The eye the simulation takes as the camera's (<see cref="World.ViewPosition"/>) instead of the rig's, while the host holds the view elsewhere
    /// (a character flight, a cinema take); null: the rig's.
    /// </summary>
    public Vector3? ViewEye { get; set; }
    /// <summary>
    /// The buildings whose interior the player sees, by key, which the simulation takes at each tick's start (<see cref="World.ShownInteriors"/>);
    /// the interior view sets it each frame it changes, never altering a set it handed over. Null: no view (no gate).
    /// </summary>
    public IReadOnlySet<string>? ShownInteriors { get; set; }
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
    /// Swaps in a new world made from <paramref name="systems"/> for a loaded save (<see cref="WorldHost.ReplaceWorld"/>). The control tick, the
    /// camera and the input carry on. Call it between frames or from a control tick's handler, never from inside a world tick.
    /// </summary>
    public World ReplaceWorld(IEnumerable<ITickSystem> systems, Action<World>? setup = null) => Host.ReplaceWorld(systems, setup);

    /// <summary>
    /// Disposes the host: its world and the systems that own threads.
    /// </summary>
    public void Dispose() => Host.Dispose();

    /// <summary>
    /// The camera to draw this frame.
    /// </summary>
    public CameraState CameraAt() => Camera.At(Alpha);

    #endregion

    #region Frame advance

    /// <summary>
    /// The drawn frame's length in seconds, smoothed over the last ten or so frames (the strategy camera's turn and tilt averaging counts in it).
    /// </summary>
    double _frameSeconds;

    /// <summary>
    /// A world tick's wall time (ms), smoothed over the last ten or so ticks, whether run in the frame or ahead (the host's dynamic tick ahead reads
    /// it).
    /// </summary>
    public double TickMs => Host.TickMs;

    /// <summary>
    /// Runs one frame's worth of real time: the control ticks it holds, then the simulation ticks; returns the control ticks.
    /// </summary>
    public int Advance(double realSeconds)
    {
        _frameSeconds = _frameSeconds == 0 ? realSeconds : _frameSeconds + (realSeconds - _frameSeconds) * 0.1;
        Camera.Strategy.DrawnFrameSeconds = (float)_frameSeconds;

        int n = Ticks.Advance(realSeconds);
        for (int i = 0; i < n; i++) Tick();
        AdvanceSimulation(realSeconds);
        return n;
    }

    /// <summary>
    /// Feeds real time to the simulation clock only and runs the ticks it owes; returns how many it owed.
    /// </summary>
    public int AdvanceSimulation(double realSeconds)
    {
        GiveView();
        return Host.Advance(realSeconds);
    }

    /// <summary>
    /// Runs <paramref name="ticks"/> simulation ticks now while the clock is paused (the agent control channel's <c>step</c>); only the last two
    /// publish a snapshot. Returns the world's tick afterwards.
    /// </summary>
    public long Step(int ticks)
    {
        GiveView();
        return Host.Step(ticks);
    }

    /// <summary>
    /// While the clock is paused: applies the interface commands queued so far now and publishes the snapshot
    /// (<see cref="WorldHost.ApplyPausedCommandsNow"/>). Returns false (nothing done) while the game runs; its next tick takes them.
    /// </summary>
    public bool ApplyPausedCommandsNow() => Host.ApplyPausedCommandsNow();

    /// <summary>
    /// Hands the host what the next ticks take from this viewer: the eye (<see cref="ViewEye"/>, else the rig's) and the shown interiors.
    /// </summary>
    void GiveView()
    {
        Host.ViewPosition = ViewEye ?? Camera.Current.Eye;
        Host.ShownInteriors = ShownInteriors;
    }

    #endregion

    #region Loading pause

    /// <summary>
    /// The original's loading pause is on (Advanced settings "Pause while loading", its Faithful side; <c>UserConfig.LoadingPause</c>): the game is
    /// held while the zones round the camera load (<see cref="WorldHost.LoadingPause"/>).
    /// </summary>
    public bool LoadingPause { get => Host.LoadingPause; set => Host.LoadingPause = value; }
    /// <summary>
    /// With <see cref="LoadingPause"/> on, the camera's load is under way: the HUD shows the original's <c>Loading...</c> panel.
    /// </summary>
    public bool ZonesLoading => Host.ZonesLoading;

    #endregion

    #region Tick ahead

    /// <summary>
    /// The last ticks run ahead's ms, left for <see cref="TakeAheadMs"/>.
    /// </summary>
    public double AheadMs => Host.AheadMs;

    /// <summary>
    /// The wall time (ms) of the last ticks run ahead, 0 once taken (for the profiler).
    /// </summary>
    public double TakeAheadMs() => Host.TakeAheadMs();

    /// <summary>
    /// Starts the next simulation ticks on a thread of their own, ahead of the clock, with this viewer's eye
    /// (<see cref="WorldHost.BeginTickAhead"/>), while the host draws the world from the shown snapshots; <see cref="EndTickAhead"/> waits for
    /// them. Nothing may touch <see cref="World"/> in between.
    /// </summary>
    public void BeginTickAhead()
    {
        GiveView();
        Host.BeginTickAhead();
    }

    /// <summary>
    /// Waits for the ticks <see cref="BeginTickAhead"/> started (rethrowing what they threw); they then wait to fall due.
    /// </summary>
    public void EndTickAhead() => Host.EndTickAhead();

    /// <summary>
    /// Takes the ticks run ahead as run now: the game clock moves by them and the snapshots shown are the world's again (before a save, say).
    /// </summary>
    public void SettleAhead() => Host.SettleAhead();

    #endregion

    #region Control tick

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
    /// <summary>
    /// The host's <see cref="Ticked"/> handlers' wall time (ms) since the host last cleared it.
    /// </summary>
    public double HandlersMs { get; set; }

    #endregion
}
