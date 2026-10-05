using System.Numerics;
using Meitou.Engine.Input;
using Meitou.Engine.Time;

namespace Meitou.Engine.Cameras;

/// <summary>
/// The free camera (the game's <c>toggle_fps_camera</c>, docs/formats/camera.md: flies with a velocity, only kept a little above
/// the ground, no ceiling found; Observed). Everything about its feel is an engine choice: the velocity follows the wished
/// direction (along the view for forward and back, sideways, straight up and down) with an exponential response; the speed grows
/// with the height above the ground like the viewer's free flight (<see cref="CameraSettings.FreeSpeedPerHeight"/> per unit of
/// height, between the minimum and maximum speed); the eye is kept <see cref="CameraSettings.FreeClearance"/> above the ground
/// under it. The mouse drag and the rotate and pitch keys turn the view; pitch is limited to +-1.5 radians as in the viewer.
/// </summary>
public sealed class FreeCamera
{
    const float PitchLimit = 1.5f;

    readonly Func<float, float, float> ground;
    readonly CameraSettings settings;

    public FreeCamera(Func<float, float, float> ground, CameraSettings? settings = null)
    {
        this.ground = ground;
        this.settings = settings ?? new CameraSettings();
    }

    public Vector3 Position { get; private set; }
    public Vector3 Velocity { get; private set; }
    public float Yaw { get; private set; }
    /// <summary>Radians; positive looks down (the strategy camera's convention).</summary>
    public float Pitch { get; private set; }
    /// <summary>What <see cref="State"/> reports as <see cref="CameraState.Distance"/>: the boom of the strategy camera it came from.</summary>
    public float ReportedDistance { get; set; } = Meitou.Data.World.KenshiCamera.InitialDistance;

    public Vector3 Forward => -new Vector3(MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch), MathF.Cos(Pitch) * MathF.Cos(Yaw));

    /// <summary>The speed at the current height above the ground, units per second.</summary>
    public float Speed => Math.Clamp((Position.Y - ground(Position.X, Position.Z)) * settings.FreeSpeedPerHeight, settings.FreeMinSpeed, settings.FreeMaxSpeed);

    /// <summary>Starts flying from a camera state, at rest, looking the same way.</summary>
    public void Enter(CameraState from)
    {
        Position = from.Eye;
        Velocity = Vector3.Zero;
        Yaw = from.Yaw;
        Pitch = Math.Clamp(MathF.Asin(Math.Clamp(-from.Forward.Y, -1f, 1f)), -PitchLimit, PitchLimit);
        ReportedDistance = from.Distance;
        KeepAboveGround();
    }

    public CameraState State
    {
        get
        {
            var f = Forward;
            return new CameraState(Position, f, Vector3.UnitY, Position + f * ReportedDistance, Yaw, Pitch, ReportedDistance, true);
        }
    }

    public void Update(float dt, ActionState a)
    {
        float turn = (a.Held(InputAction.RotateLeft) ? 1 : 0) - (a.Held(InputAction.RotateRight) ? 1 : 0);
        float tilt = (a.Held(InputAction.PitchDown) ? 1 : 0) - (a.Held(InputAction.PitchUp) ? 1 : 0);
        Yaw = Interp.WrapAngle(Yaw + turn * settings.RotateSpeed * dt);
        Pitch += tilt * settings.RotateSpeed * dt;
        if (a.Held(InputAction.Orbit))
        {
            Yaw = Interp.WrapAngle(Yaw - a.MouseDelta.X * settings.MouseSensitivity);
            Pitch += a.MouseDelta.Y * settings.MouseSensitivity;
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
        var target = wish.LengthSquared() > 1e-6f ? Vector3.Normalize(wish) * Speed : Vector3.Zero;
        Velocity = Vector3.Lerp(Velocity, target, 1 - MathF.Exp(-settings.FreeResponse * dt));
        Position += Velocity * dt;
        KeepAboveGround();
    }

    void KeepAboveGround()
    {
        float min = ground(Position.X, Position.Z) + settings.FreeClearance;
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
            if (p.Y <= ground(p.X, p.Z))
            {
                float lo = prevT, hi = t;
                for (int i = 0; i < 24; i++)
                {
                    float mid = (lo + hi) / 2;
                    var q = Position + f * mid;
                    if (q.Y <= ground(q.X, q.Z)) hi = mid; else lo = mid;
                }
                var hit = Position + f * hi;
                return new Vector3(hit.X, ground(hit.X, hit.Z), hit.Z);
            }
        }
        return null;
    }
}
