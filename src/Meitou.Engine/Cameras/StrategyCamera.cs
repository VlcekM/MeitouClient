using System.Numerics;
using Meitou.Data.World;
using Meitou.Engine.Input;
using Meitou.Engine.Time;

namespace Meitou.Engine.Cameras;

/// <summary>
/// The game's strategy camera (docs/formats/camera.md, Verified): a pivot on the ground with the camera on a boom behind it. Boom
/// 150 and 30 degrees down at the start; the zoom step <c>boom -= zoomSpeed · input · min(boom / 600, 0.75)</c> clamps to
/// [10, 2000]; a pitch step is refused when the real view direction's y is already at or below -0.92 (down) or at or above 0.2
/// (up), checked before the step, so one step's overshoot is possible (here a step is cut into pieces of at most
/// <see cref="CameraSettings.MaxPitchSubStep"/>, each checked, so a big mouse jump overshoots no more than a key step);
/// the pivot's height comes from the ground and the eye is kept above the ground under it by <c>min(0.2 · boom, 20) + 10</c>.
/// Engine choices (not in the docs): the pivot moves on the ground plane relative to yaw at <see cref="CameraSettings.MoveSpeed"/>
/// times <c>clamp(boom / 600, 0.1, 1)</c> units per second; the clearance is met by tilting the view steeper (the pitch the player
/// asked for is kept and returns when the ground allows) and, if that is not enough, by raising the eye alone.
/// </summary>
public sealed class StrategyCamera
{
    /// <summary>Eye pitch (radians above the horizon) at the highest view the rule allows, view y = <see cref="KenshiCamera.MaxViewY"/>.</summary>
    public static readonly float MinPitch = -MathF.Asin(KenshiCamera.MaxViewY);
    /// <summary>Eye pitch at the steepest view the rule allows, view y = <see cref="KenshiCamera.MinViewY"/>.</summary>
    public static readonly float MaxPitch = MathF.Asin(-KenshiCamera.MinViewY);

    readonly Func<float, float, float> ground;
    readonly CameraSettings settings;

    public StrategyCamera(Func<float, float, float> ground, CameraSettings? settings = null)
    {
        this.ground = ground;
        this.settings = settings ?? new CameraSettings();
        Pitch = KenshiCamera.InitialPitchDegrees * MathF.PI / 180;
        Distance = KenshiCamera.InitialDistance;
        State = Compute();
    }

    /// <summary>Pivot position on the ground plane (its height is the ground's).</summary>
    public Vector2 PivotXZ { get; private set; }
    /// <summary>Radians; the view's heading (0 looks along -Z), as <see cref="CameraState.Yaw"/>.</summary>
    public float Yaw { get; private set; }
    /// <summary>The pitch asked for (eye above the horizon, radians); the state's pitch is this or steeper where the ground needs it.</summary>
    public float Pitch { get; private set; }
    /// <summary>The boom the player asked for, 10..2000.</summary>
    public float Distance { get; private set; }
    /// <summary>The camera as of the last <see cref="Update"/> or placement.</summary>
    public CameraState State { get; private set; }

    public Vector3 Pivot => new(PivotXZ.X, ground(PivotXZ.X, PivotXZ.Y), PivotXZ.Y);

    /// <summary>Sets the pivot and view (yaw wrapped, pitch kept within the pitch limits, boom within the zoom limits).</summary>
    public void Place(Vector2 pivotXZ, float yaw, float pitch, float distance)
    {
        PivotXZ = pivotXZ;
        Yaw = Interp.WrapAngle(yaw);
        Pitch = Math.Clamp(pitch, MinPitch, MaxPitch);
        Distance = Math.Clamp(distance, KenshiCamera.MinDistance, KenshiCamera.MaxDistance);
        State = Compute();
    }

    /// <summary>One zoom step of <paramref name="input"/> (positive zooms in; one wheel notch is 1).</summary>
    public void Zoom(float input)
    {
        Distance = KenshiCamera.ZoomedDistance(Distance, settings.ZoomSpeed, input);
        State = Compute();
    }

    /// <summary>
    /// A pitch step of <paramref name="delta"/> radians, positive tilting the view steeper (down). Refused piece by piece once the
    /// real view direction is at the limit, as the game does.
    /// </summary>
    public void PitchStep(float delta)
    {
        if (delta == 0 || !float.IsFinite(delta)) return;
        int pieces = (int)Math.Ceiling(Math.Abs(delta) / settings.MaxPitchSubStep);
        float piece = delta / pieces;
        for (int i = 0; i < pieces; i++)
        {
            float y = State.Forward.Y;
            if (piece > 0 ? y <= KenshiCamera.MinViewY : y >= KenshiCamera.MaxViewY) break;
            Pitch += piece;
            State = Compute();
        }
        if (settings.HardPitchClamp)
        {
            Pitch = Math.Clamp(Pitch, MinPitch, MaxPitch);
            State = Compute();
        }
    }

    /// <summary>Turns the view about the vertical (positive turns it to the left).</summary>
    public void Rotate(float radians)
    {
        Yaw = Interp.WrapAngle(Yaw + radians);
        State = Compute();
    }

    /// <summary>Moves the pivot on the ground plane: <paramref name="forward"/> along the view's heading, <paramref name="right"/> sideways, world units.</summary>
    public void Move(float forward, float right)
    {
        var f = new Vector2(-MathF.Sin(Yaw), -MathF.Cos(Yaw));
        var r = new Vector2(MathF.Cos(Yaw), -MathF.Sin(Yaw));
        PivotXZ += f * forward + r * right;
        State = Compute();
    }

    /// <summary>One tick: rotation, pitch, mouse drag, zoom (wheel and keys) and movement from the actions.</summary>
    public void Update(float dt, ActionState a)
    {
        float turn = (a.Held(InputAction.RotateLeft) ? 1 : 0) - (a.Held(InputAction.RotateRight) ? 1 : 0);
        if (turn != 0) Rotate(turn * settings.RotateSpeed * dt);

        float tilt = (a.Held(InputAction.PitchDown) ? 1 : 0) - (a.Held(InputAction.PitchUp) ? 1 : 0);
        if (tilt != 0) PitchStep(tilt * settings.RotateSpeed * dt);

        if (a.Held(InputAction.Orbit) && a.MouseDelta != Vector2.Zero)
        {
            Rotate(-a.MouseDelta.X * settings.MouseSensitivity);
            PitchStep(a.MouseDelta.Y * settings.MouseSensitivity);
        }

        float zoom = a.Wheel + ((a.Held(InputAction.ZoomIn) ? 1 : 0) - (a.Held(InputAction.ZoomOut) ? 1 : 0)) * settings.ZoomKeyNotchesPerSecond * dt;
        if (zoom != 0) Zoom(zoom);

        float fwd = (a.Held(InputAction.MoveForward) ? 1 : 0) - (a.Held(InputAction.MoveBack) ? 1 : 0);
        float right = (a.Held(InputAction.MoveRight) ? 1 : 0) - (a.Held(InputAction.MoveLeft) ? 1 : 0);
        if (fwd != 0 || right != 0)
        {
            var dir = Vector2.Normalize(new Vector2(right, fwd));
            float step = settings.MoveSpeed * Math.Clamp(Distance / KenshiCamera.ZoomLengthDivisor, 0.1f, 1f) * dt;
            Move(dir.Y * step, dir.X * step);
        }
        State = Compute();
    }

    CameraState Compute()
    {
        var pivot = Pivot;
        float pitch = Pitch;
        float clearance = KenshiCamera.EyeClearance(Distance);
        var eye = CameraState.EyeFor(pivot, Yaw, pitch, Distance);
        // Steeper view pulls the eye in over the pivot and up: a few rounds settle the ground under the moved eye.
        for (int i = 0; i < 4; i++)
        {
            float need = ground(eye.X, eye.Z) + clearance - pivot.Y;
            if (eye.Y - pivot.Y >= need) break;
            if (need >= Distance) break;
            pitch = MathF.Max(pitch, MathF.Asin(need / Distance));
            eye = CameraState.EyeFor(pivot, Yaw, pitch, Distance);
        }
        float minEye = ground(eye.X, eye.Z) + clearance;
        if (eye.Y < minEye) eye.Y = minEye;

        var toPivot = pivot - eye;
        float length = toPivot.Length();
        var forward = length > 1e-4f ? toPivot / length : new Vector3(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));
        float reportedPitch = length > 1e-4f ? MathF.Asin(Math.Clamp(-forward.Y, -1f, 1f)) : pitch;
        return new CameraState(eye, forward, Vector3.UnitY, pivot, Yaw, reportedPitch, Math.Max(length, 1e-4f), false);
    }
}
