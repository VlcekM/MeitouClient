using System.Numerics;
using Meitou.Data.World;
using Meitou.Engine.Input;
using Meitou.Engine.Time;

namespace Meitou.Engine.Cameras;

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
    /// <summary>How far the view is followed to find the ground when leaving free flight (engine choice).</summary>
    public const float LookRange = 20000;

    readonly Interpolated<CameraState> states;
    readonly Func<float, float, float> ground;

    public CameraRig(Func<float, float, float> ground, CameraSettings? settings = null)
    {
        this.ground = ground;
        Settings = settings ?? new CameraSettings();
        Strategy = new StrategyCamera(ground, Settings);
        Free = new FreeCamera(ground, Settings);
        states = new Interpolated<CameraState>(Strategy.State, CameraState.Lerp);
    }

    public CameraSettings Settings { get; }
    public StrategyCamera Strategy { get; }
    public FreeCamera Free { get; }
    public bool IsFree { get; private set; }

    /// <summary>The camera as of the previous tick.</summary>
    public CameraState Previous => states.Previous;
    /// <summary>The camera as of the latest tick.</summary>
    public CameraState Current => states.Current;

    /// <summary>The camera drawn between the previous (0) and the current (1) tick.</summary>
    public CameraState At(float alpha) => states.At(alpha);

    /// <summary>Moves the strategy camera (leaving free flight if on) and cuts to it: no blend from the old position.</summary>
    public void Place(Vector2 pivotXZ, float yaw, float pitch, float distance)
    {
        IsFree = false;
        Strategy.Place(pivotXZ, yaw, pitch, distance);
        states.Snap(Strategy.State);
    }

    /// <summary>One simulation tick of <paramref name="dt"/> seconds.</summary>
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
        if (cut) states.Snap(next); else states.Push(next);
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
        Vector3 pivot = hit ?? new Vector3(eye.X, ground(eye.X, eye.Z), eye.Z);
        float boom = hit is { } h ? Vector3.Distance(eye, h) : Strategy.Distance;
        Strategy.Place(new Vector2(pivot.X, pivot.Z), Free.Yaw, Free.Pitch, boom);
        IsFree = false;
    }
}
