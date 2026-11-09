using System.Numerics;
using Meitou.Game.Host.Input;
using Meitou.Simulation.Session;

namespace Meitou.Game.Host.Cameras;

/// <summary>
/// The free camera (the game's <c>toggle_fps_camera</c>, docs/formats/camera.md: flies with a velocity, only kept a little above the ground, no
/// ceiling found; Observed). Everything about its feel is an engine choice: the velocity follows the wished direction (along the view for forward and
/// back, sideways, straight up and down) with an exponential response; the speed grows with the height above the ground like the viewer's free flight
/// (<see cref="CameraSettings.FreeSpeedPerHeight"/> per unit of height, between the minimum and maximum speed), doubled while Shift is held; the eye
/// is kept <see cref="CameraSettings.FreeClearance"/> above the ground under it. The mouse drag and the rotate and pitch keys turn the view; pitch is
/// limited to +-1.5 radians as in the viewer.
/// </summary>
public sealed class FreeCamera
{
    /// <summary>
    /// The pitch limit, +-1.5 radians as in the viewer.
    /// </summary>
    const float PitchLimit = 1.5f;

    /// <summary>
    /// The ground (or sea) height under a point, which the eye keeps above.
    /// </summary>
    readonly Func<float, float, float> _ground;
    /// <summary>
    /// The settings the camera reads (speeds, response, clearance).
    /// </summary>
    readonly CameraSettings _settings;

    /// <summary>
    /// A free camera over <paramref name="ground"/>; <paramref name="settings"/> default to a new set.
    /// </summary>
    public FreeCamera(Func<float, float, float> ground, CameraSettings? settings = null)
    {
        this._ground = ground;
        this._settings = settings ?? new CameraSettings();
    }

    /// <summary>
    /// The eye.
    /// </summary>
    public Vector3 Position { get; private set; }
    /// <summary>
    /// Units per second; follows the wished direction with an exponential response.
    /// </summary>
    public Vector3 Velocity { get; private set; }
    /// <summary>
    /// Radians; the view's heading (0 looks along -Z), as <see cref="CameraState.Yaw"/>.
    /// </summary>
    public float Yaw { get; private set; }
    /// <summary>
    /// Radians; positive looks down (the strategy camera's convention).
    /// </summary>
    public float Pitch { get; private set; }
    /// <summary>
    /// What <see cref="State"/> reports as <see cref="CameraState.Distance"/>: the boom of the strategy camera it came from.
    /// </summary>
    public float ReportedDistance { get; set; } = Data.World.KenshiCamera.InitialDistance;

    /// <summary>
    /// The view direction (unit), from <see cref="Yaw"/> and <see cref="Pitch"/>.
    /// </summary>
    public Vector3 Forward => -new Vector3(MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch), MathF.Cos(Pitch) * MathF.Cos(Yaw));

    /// <summary>
    /// The speed at the current height above the ground, units per second.
    /// </summary>
    public float Speed => Math.Clamp((Position.Y - _ground(Position.X, Position.Z)) * _settings.FreeSpeedPerHeight,
        _settings.FreeMinSpeed, _settings.FreeMaxSpeed);

    /// <summary>
    /// Starts flying from a camera state, at rest, looking the same way.
    /// </summary>
    public void Enter(CameraState from)
    {
        Position = from.Eye;
        Velocity = Vector3.Zero;
        Yaw = from.Yaw;
        Pitch = Math.Clamp(MathF.Asin(Math.Clamp(-from.Forward.Y, -1f, 1f)), -PitchLimit, PitchLimit);
        ReportedDistance = from.Distance;
        KeepAboveGround();
    }

    /// <summary>
    /// The camera now: the eye looking along <see cref="Forward"/>, its target <see cref="ReportedDistance"/> ahead.
    /// </summary>
    public CameraState State
    {
        get
        {
            var f = Forward;
            return new CameraState(Position, f, Vector3.UnitY, Position + f * ReportedDistance, Yaw, Pitch, ReportedDistance, true);
        }
    }

    /// <summary>
    /// One step of <paramref name="dt"/> seconds: the keys and the mouse drag turn the view, the move keys set the wished velocity (doubled with
    /// Shift), the eye moves and is kept above the ground.
    /// </summary>
    public void Update(float dt, ActionState a)
    {
        float turn = (a.Held(InputAction.RotateLeft) ? 1 : 0) - (a.Held(InputAction.RotateRight) ? 1 : 0);
        float tilt = (a.Held(InputAction.PitchDown) ? 1 : 0) - (a.Held(InputAction.PitchUp) ? 1 : 0);
        Yaw = Interp.WrapAngle(Yaw + turn * _settings.RotateSpeed * dt);
        Pitch += tilt * _settings.RotateSpeed * dt;
        if (a.Held(InputAction.Orbit))
        {
            Yaw = Interp.WrapAngle(Yaw - a.MouseDelta.X * _settings.MouseSensitivity);
            Pitch += a.MouseDelta.Y * _settings.MouseSensitivity;
        }
        Pitch = Math.Clamp(Pitch, -PitchLimit, PitchLimit);

        float fwd = (a.Held(InputAction.MoveForward) ? 1 : 0) - (a.Held(InputAction.MoveBack) ? 1 : 0);
        float right = (a.Held(InputAction.MoveRight) ? 1 : 0) - (a.Held(InputAction.MoveLeft) ? 1 : 0);
        float up = (a.Held(InputAction.MoveUp) ? 1 : 0) - (a.Held(InputAction.MoveDown) ? 1 : 0);
        var f = Forward;
        var flat = new Vector3(f.X, 0, f.Z);
        flat = flat.LengthSquared() < 1e-6f ? new Vector3(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw)) : Vector3.Normalize(flat);
        var side = Vector3.Cross(flat, Vector3.UnitY);
        var wish = f * fwd + side * right + Vector3.UnitY * up;
        bool shift = a.Input.IsDown(Key.LeftShift) || a.Input.IsDown(Key.RightShift);
        var target = wish.LengthSquared() > 1e-6f ? Vector3.Normalize(wish) * Speed * (shift ? 2 : 1) : Vector3.Zero;
        Velocity = Vector3.Lerp(Velocity, target, 1 - MathF.Exp(-_settings.FreeResponse * dt));
        Position += Velocity * dt;
        KeepAboveGround();
    }

    /// <summary>
    /// Lifts the eye to <see cref="CameraSettings.FreeClearance"/> above the ground under it, ending a fall.
    /// </summary>
    void KeepAboveGround()
    {
        float min = _ground(Position.X, Position.Z) + _settings.FreeClearance;
        if (Position.Y >= min) return;
        Position = new Vector3(Position.X, min, Position.Z);
        if (Velocity.Y < 0) Velocity = new Vector3(Velocity.X, 0, Velocity.Z);
    }

    /// <summary>
    /// Where the view's centre line meets the ground within <paramref name="maxRange"/>, or null if it looks up or never gets there.
    /// Marched in steps growing with the distance, then bisected.
    /// </summary>
    public Vector3? LookPointOnGround(float maxRange)
    {
        var f = Forward;
        float prevT = 0;
        float t = 0;
        while (t < maxRange)
        {
            prevT = t;
            t = MathF.Min(t + MathF.Max(5, t * 0.02f), maxRange);
            var p = Position + f * t;
            if (p.Y <= _ground(p.X, p.Z))
            {
                float lo = prevT, hi = t;
                for (int i = 0; i < 24; i++)
                {
                    float mid = (lo + hi) / 2;
                    var q = Position + f * mid;
                    if (q.Y <= _ground(q.X, q.Z)) hi = mid; else lo = mid;
                }
                var hit = Position + f * hi;
                return new Vector3(hit.X, _ground(hit.X, hit.Z), hit.Z);
            }
        }
        return null;
    }
}