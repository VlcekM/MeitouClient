using System.Numerics;
using Meitou.Engine.Cameras;
using Meitou.Engine.Input;
using Meitou.Engine.Time;

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
public sealed class WorldSession
{
    public WorldSession(Vector3 focus, (double X0, double Z0, double X1, double Z1) region, Func<float, float, float> ground,
        double startHour = GameClock.DefaultStartHour, double tickRate = FixedStepClock.DefaultTickRate, InputBindings? bindings = null,
        GameClock? clock = null)
    {
        Focus = focus;
        Region = region;
        Ticks = new FixedStepClock(tickRate);
        Clock = clock ?? new GameClock(startHour);
        Bindings = bindings ?? new InputBindings();
        Camera = new CameraRig(ground);
    }

    /// <summary>The point the world was loaded around.</summary>
    public Vector3 Focus { get; }
    /// <summary>The loaded region in world X/Z (the detail streams with the camera beyond it).</summary>
    public (double X0, double Z0, double X1, double Z1) Region { get; }
    /// <summary>The real-time control tick (input actions, camera, UI).</summary>
    public FixedStepClock Ticks { get; }
    /// <summary>The game-time simulation tick.</summary>
    public SimulationClock Simulation { get; } = new();
    public GameClock Clock { get; }
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
        int n = Simulation.Advance(realSeconds);
        for (int i = 0; i < n; i++) SimulationTick();
        return n;
    }

    /// <summary>One simulation tick: the game clock moves by one tick of game time.</summary>
    void SimulationTick() => Clock.Advance(Simulation.TickSeconds);

    /// <summary>One control tick: the actions from the input (pause, speed), then the camera.</summary>
    public ActionState Tick()
    {
        var actions = Bindings.Resolve(Input.Consume());
        if (actions.Pressed(InputAction.Pause)) Simulation.TogglePause();
        if (actions.Pressed(InputAction.Speed1)) Simulation.SetSpeed(1);
        if (actions.Pressed(InputAction.Speed2)) Simulation.SetSpeed(2);
        if (actions.Pressed(InputAction.Speed3)) Simulation.SetSpeed(5);
        if (actions.Pressed(InputAction.TimeFaster)) Simulation.Faster();
        if (actions.Pressed(InputAction.TimeSlower)) Simulation.Slower();
        Camera.Update((float)Ticks.TickSeconds, actions);
        Ticked?.Invoke(actions);
        return actions;
    }

    /// <summary>The camera to draw this frame.</summary>
    public CameraState CameraAt() => Camera.At(Alpha);
}
