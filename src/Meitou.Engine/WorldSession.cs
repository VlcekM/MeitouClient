using System.Numerics;
using Meitou.Engine.Cameras;
using Meitou.Engine.Input;
using Meitou.Engine.Time;

namespace Meitou.Engine;

/// <summary>
/// A running world: what is loaded (the region around <see cref="Focus"/>), the simulation tick, the game clock, the input and
/// the camera. The host feeds <see cref="Input"/> from its window and calls <see cref="Advance"/> once per displayed frame; the
/// renderer draws <see cref="Camera"/> at <see cref="Alpha"/> between the last two ticks (docs/engine.md).
/// </summary>
public sealed class WorldSession
{
    /// <summary>Time scales the speed actions step through: Kenshi's buttons are 1x, 2x, 3x; 5x as a common mod option (engine choice).</summary>
    public static readonly double[] TimeScales = [1, 2, 3, 5];

    int timeScale;

    public WorldSession(Vector3 focus, (double X0, double Z0, double X1, double Z1) region, Func<float, float, float> ground,
        double startHour = GameClock.DefaultStartHour, double tickRate = FixedStepClock.DefaultTickRate, InputBindings? bindings = null)
    {
        Focus = focus;
        Region = region;
        Ticks = new FixedStepClock(tickRate);
        Clock = new GameClock(startHour);
        Bindings = bindings ?? new InputBindings();
        Camera = new CameraRig(ground);
    }

    /// <summary>The point the world was loaded around.</summary>
    public Vector3 Focus { get; }
    /// <summary>The loaded region in world X/Z (the detail streams with the camera beyond it).</summary>
    public (double X0, double Z0, double X1, double Z1) Region { get; }
    public FixedStepClock Ticks { get; }
    public GameClock Clock { get; }
    public InputState Input { get; } = new();
    public InputBindings Bindings { get; }
    public CameraRig Camera { get; }
    /// <summary>Where the frame is between the previous and the current tick.</summary>
    public float Alpha => (float)Ticks.Alpha;
    public double TimeScale => TimeScales[timeScale];

    /// <summary>Called for each tick's actions after the session's own handling (pause, speed, camera); the host's actions (quit, screenshot...).</summary>
    public event Action<ActionState>? Ticked;

    /// <summary>Runs the ticks that <paramref name="realSeconds"/> of real time hold; returns how many ran.</summary>
    public int Advance(double realSeconds)
    {
        int n = Ticks.Advance(realSeconds);
        for (int i = 0; i < n; i++) Tick();
        return n;
    }

    /// <summary>One simulation tick: the actions from the input, then the camera and the clock.</summary>
    public ActionState Tick()
    {
        var actions = Bindings.Resolve(Input.Consume());
        if (actions.Pressed(InputAction.Pause)) Clock.Paused = !Clock.Paused;
        if (actions.Pressed(InputAction.TimeFaster)) timeScale = Math.Min(timeScale + 1, TimeScales.Length - 1);
        if (actions.Pressed(InputAction.TimeSlower)) timeScale = Math.Max(timeScale - 1, 0);
        Clock.TimeScale = TimeScale;
        float dt = (float)Ticks.TickSeconds;
        Camera.Update(dt, actions);
        Clock.Tick(dt);
        Ticked?.Invoke(actions);
        return actions;
    }

    /// <summary>The camera to draw this frame.</summary>
    public CameraState CameraAt() => Camera.At(Alpha);
}
