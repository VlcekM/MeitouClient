using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Host.Input;
using Meitou.Simulation.Session;

namespace Meitou.Game.Host.Cameras;

/// <summary>
/// Owns the strategy camera and the free camera, switches between them on <see cref="InputAction.ToggleFreeCamera"/>, updates the
/// active one each tick and keeps the previous and current tick's <see cref="CameraState"/> for drawing between ticks
/// (<see cref="At"/>). Switching modes is a cut (no blend across it). Engine choice: switching to the free camera starts it at the
/// strategy eye looking the same way; switching back puts the strategy pivot on the ground where the free camera's view centre
/// line meets it (within <see cref="LookRange"/>), the boom at that distance (clamped to the zoom limits), yaw as it is and the
/// pitch as it is (clamped to the pitch limits); with no ground in view (looking up or too far) the pivot is the ground under the
/// eye and the boom is the one the strategy camera had before.
/// </summary>
public sealed class CameraRig
{
    /// <summary>
    /// How far the view is followed to find the ground when leaving free flight (engine choice).
    /// </summary>
    public const float LookRange = 20000;

    readonly Interpolated<CameraState> _states;
    readonly Func<float, float, float> _ground;

    /// <summary>
    /// The height the strategy camera's pivot keeps above: the ground or the sea, whichever is higher, raised by <see cref="Floors"/> (the game's
    /// pivot ray also hits buildings' floors; the eye keeps above the ground and the sea only, docs/formats/camera.md "Height").
    /// </summary>
    public float GroundAt(float x, float z) => Floors?.Invoke(x, z) is { } floor ? MathF.Max(_ground(x, z), floor) : _ground(x, z);

    /// <summary>
    /// The height the strategy camera's eye keeps above: the ground or the sea, whichever is higher.
    /// </summary>
    public float EyeGroundAt(float x, float z) => _ground(x, z);

    /// <summary>
    /// The highest building floor at a point that the strategy camera's pivot rides on, or null (set by the host; the free camera keeps to the
    /// ground).
    /// </summary>
    public Func<float, float, float?>? Floors { get; set; }

    public CameraRig(Func<float, float, float> ground, CameraSettings? settings = null)
    {
        this._ground = ground;
        Settings = settings ?? new CameraSettings();
        Strategy = new StrategyCamera(ground, Settings, GroundAt);
        Free = new FreeCamera(ground, Settings);
        _states = new Interpolated<CameraState>(Strategy.State, CameraState.Lerp);
    }

    public CameraSettings Settings { get; }
    public StrategyCamera Strategy { get; }
    public FreeCamera Free { get; }
    public bool IsFree { get; private set; }

    /// <summary>
    /// The camera as of the previous tick.
    /// </summary>
    public CameraState Previous => _states.Previous;
    /// <summary>
    /// The camera as of the latest tick.
    /// </summary>
    public CameraState Current => _states.Current;

    /// <summary>
    /// The camera drawn between the previous (0) and the current (1) tick.
    /// </summary>
    public CameraState At(float alpha) => _states.At(alpha);

    /// <summary>
    /// Moves the strategy camera (leaving free flight if on) and cuts to it: no blend from the old position.
    /// </summary>
    public void Place(Vector2 pivotXz, float yaw, float pitch, float distance)
    {
        IsFree = false;
        Strategy.Place(pivotXz, yaw, pitch, distance);
        _states.Snap(Strategy.State);
    }

    /// <summary>
    /// Cuts to the free camera at <paramref name="from"/>'s eye looking the same way (a host's camera handed back, e.g. after the
    /// cinematic camera flew it: only the free camera can stand at any eye); with <paramref name="strategy"/> it then leaves free flight
    /// as <c>;</c> does (the pivot where the view meets the ground).
    /// </summary>
    public void TakeOver(CameraState from, bool strategy)
    {
        Free.Enter(from);
        IsFree = true;
        if (strategy) Toggle();
        _states.Snap(IsFree ? Free.State : Strategy.State);
    }

    /// <summary>
    /// Moves the strategy pivot without a cut: the view keeps its own yaw, pitch and boom, and the next tick blends from where it was (a camera that
    /// follows).
    /// </summary>
    public void MovePivot(Vector2 pivotXz) => Strategy.Place(pivotXz, Strategy.Yaw, Strategy.Pitch, Strategy.Distance);

    /// <summary>
    /// One simulation tick of <paramref name="dt"/> seconds.
    /// </summary>
    public void Update(float dt, ActionState actions)
    {
        bool cut = false;
        if (actions.Pressed(InputAction.ToggleFreeCamera))
        {
            Toggle();
            cut = true;
        }
        CameraState next;
        if (IsFree)
        {
            Free.Update(dt, actions);
            next = Free.State;
        }
        else
        {
            Strategy.Update(dt, actions);
            next = Strategy.State;
        }
        if (cut) _states.Snap(next); else _states.Push(next);
    }

    void Toggle()
    {
        if (!IsFree)
        {
            Free.Enter(Strategy.State);
            Free.ReportedDistance = Strategy.Distance;
            IsFree = true;
            return;
        }
        var hit = Free.LookPointOnGround(LookRange);
        var eye = Free.Position;
        Vector3 pivot = hit ?? new Vector3(eye.X, _ground(eye.X, eye.Z), eye.Z);
        float boom = hit is { } h ? Vector3.Distance(eye, h) : Strategy.Distance;
        Strategy.Place(new Vector2(pivot.X, pivot.Z), Free.Yaw, Free.Pitch, boom);
        IsFree = false;
    }
}