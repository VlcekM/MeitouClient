using System.Numerics;
using Meitou.Game.Host.Input;
using Meitou.Simulation.Session;

namespace Meitou.Game.Host.Cameras;

/// <summary>
/// The free camera (the game's <c>toggle_fps_camera</c>, docs/formats/camera.md: flies with a velocity, only kept a little above the ground, no
/// ceiling found; Observed). Everything about its feel is an engine choice: the velocity follows the wished direction (along the view for forward and
/// back, sideways, straight up and down) with an exponential response; the speed grows with the height above the ground like the viewer's free flight
/// (<see cref="CameraSettings.FreeSpeedPerHeight"/> per unit of height, between the minimum and maximum speed), times <see cref="SpeedScale"/> that the
/// wheel sets as the game's does (in steps of a quarter doubling, so scrolling back always lands on 100%); the eye is kept <see cref="CameraSettings.FreeClearance"/> above the ground under it. The mouse drag and the rotate and pitch keys turn the view; pitch is
/// limited to +-1.5 radians as in the viewer.
/// </summary>
public sealed class FreeCamera
{
    /// <summary>
    /// The pitch limit, +-1.5 radians as in the viewer.
    /// </summary>
    const float PitchLimit = 1.5f;
    /// <summary>
    /// Wheel notches per doubling of <see cref="SpeedScale"/>.
    /// </summary>
    const int StepsPerDoubling = 4;
    /// <summary>
    /// The most notches either way from 100%: 16 is 1/16 to 16 times.
    /// </summary>
    const int MaxSpeedStep = 16;
    /// <summary>
    /// How long <see cref="SpeedShown"/> stays true after the last change, seconds.
    /// </summary>
    const float SpeedShownSeconds = 3;

    /// <summary>
    /// The ground (or sea) height under a point, which the eye keeps above.
    /// </summary>
    readonly Func<float, float, float> _ground;
    /// <summary>
    /// The settings the camera reads (speeds, response, clearance).
    /// </summary>
    readonly CameraSettings _settings;
    /// <summary>
    /// Whole wheel notches from 100%, between -<see cref="MaxSpeedStep"/> and <see cref="MaxSpeedStep"/>.
    /// </summary>
    int _speedStep;
    /// <summary>
    /// The wheel's fraction of a notch not yet taken (a touchpad scrolls in fractions).
    /// </summary>
    float _wheelRest;
    /// <summary>
    /// Seconds left of <see cref="SpeedShown"/>.
    /// </summary>
    float _speedShownLeft;

    /// <summary>
    /// The eye, kept in double precision: far from the origin a float steps by 0.004 units at 50000, a pixel or two for something a few units from the
    /// eye, so a slow flight close to a thing moved in small jumps. <see cref="PositionResidual"/> is what the float leaves out.
    /// </summary>
    double _x, _y, _z;

    /// <summary>
    /// A free camera over <paramref name="ground"/>; <paramref name="settings"/> default to a new set.
    /// </summary>
    public FreeCamera(Func<float, float, float> ground, CameraSettings? settings = null)
    {
        this._ground = ground;
        this._settings = settings ?? new CameraSettings();
    }

    /// <summary>
    /// The eye, from the double-precision position.
    /// </summary>
    public Vector3 Position
    {
        get => new((float)_x, (float)_y, (float)_z);
        private set => (_x, _y, _z) = (value.X, value.Y, value.Z);
    }

    /// <summary>
    /// The double-precision eye less <see cref="Position"/>: under half a float step.
    /// </summary>
    public Vector3 PositionResidual
    {
        get
        {
            var p = Position;
            return new((float)(_x - p.X), (float)(_y - p.Y), (float)(_z - p.Z));
        }
    }

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
    /// Radians the view is banked about its direction (photo mode; the rest of the game keeps it 0). Positive banks right.
    /// </summary>
    public float Roll { get; set; }

    /// <summary>
    /// Photo mode's controls (null: the game's free flight): a fixed fly speed that does not follow the height, the factors of the fast and slow
    /// modifiers, the turn of the view by the right button besides Orbit, and the roll keys.
    /// </summary>
    public PhotoControls? Photo { get; set; }

    /// <summary>
    /// The view direction (unit), from <see cref="Yaw"/> and <see cref="Pitch"/>.
    /// </summary>
    public Vector3 Forward => -new Vector3(MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch), MathF.Cos(Pitch) * MathF.Cos(Yaw));

    /// <summary>
    /// The wheel's multiplier of <see cref="Speed"/>: 2^(notches / 4), exactly 1 at no notches.
    /// </summary>
    public float SpeedScale => MathF.Pow(2, _speedStep / (float)StepsPerDoubling);
    /// <summary>
    /// Whether the wheel changed <see cref="SpeedScale"/> within the last 3 seconds (the host shows it then).
    /// </summary>
    public bool SpeedShown => _speedShownLeft > 0;

    /// <summary>
    /// The speed at the current height above the ground before <see cref="SpeedScale"/>, units per second.
    /// </summary>
    public float Speed => Photo?.FlySpeed ?? Math.Clamp((Position.Y - _ground(Position.X, Position.Z)) * _settings.FreeSpeedPerHeight,
        _settings.FreeMinSpeed, _settings.FreeMaxSpeed);

    /// <summary>
    /// Starts flying from a camera state, at rest, looking the same way.
    /// </summary>
    public void Enter(CameraState from)
    {
        (_x, _y, _z) = (
            from.Eye.X + (double)from.EyeResidual.X, from.Eye.Y + (double)from.EyeResidual.Y, from.Eye.Z + (double)from.EyeResidual.Z);
        Velocity = Vector3.Zero;
        Yaw = from.Yaw;
        Pitch = Math.Clamp(MathF.Asin(Math.Clamp(-from.Forward.Y, -1f, 1f)), -PitchLimit, PitchLimit);
        Roll = 0;
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
            return new CameraState(Position, f, Vector3.UnitY, Position + f * ReportedDistance, Yaw, Pitch, ReportedDistance, true)
            {
                EyeResidual = PositionResidual,
            };
        }
    }

    /// <summary>
    /// One step of <paramref name="dt"/> seconds: the keys and the mouse drag turn the view, the wheel sets the speed, the move keys set the wished
    /// velocity, the eye moves and is kept above the ground.
    /// </summary>
    public void Update(float dt, ActionState a)
    {
        // Photo mode has its own keys (the game's turn keys are its down and up, its Orbit key its slow modifier): the mouse alone turns the view
        // there.
        bool photoMode = Photo is not null;
        if (!photoMode) AdjustSpeed(dt, a.Wheel);
        TurnView(dt, a, photoMode);
        if (Photo is { } photo) UpdatePhoto(photo, dt, a);
        Fly(dt, a, photoMode);
    }

    /// <summary>
    /// Turns the view by the rotate and pitch keys (not in photo mode) and the mouse drag, scaled by photo mode's <see cref="PhotoControls.LookScale"/>;
    /// clamps the pitch.
    /// </summary>
    void TurnView(float dt, ActionState a, bool photoMode)
    {
        float look = Photo?.LookScale ?? 1;
        float turn = photoMode ? 0 : (a.Held(InputAction.RotateLeft) ? 1 : 0) - (a.Held(InputAction.RotateRight) ? 1 : 0);
        float tilt = photoMode ? 0 : (a.Held(InputAction.PitchDown) ? 1 : 0) - (a.Held(InputAction.PitchUp) ? 1 : 0);
        Yaw = Interp.WrapAngle(Yaw + turn * _settings.RotateSpeed * look * dt);
        Pitch += tilt * _settings.RotateSpeed * look * dt;
        if (photoMode ? a.Input.IsDown(MouseButton.Right) || a.Input.IsDown(MouseButton.Middle) : a.Held(InputAction.Orbit))
        {
            Yaw = Interp.WrapAngle(Yaw - a.MouseDelta.X * _settings.MouseSensitivity * look);
            Pitch += a.MouseDelta.Y * _settings.MouseSensitivity * look;
        }

        Pitch = Math.Clamp(Pitch, -PitchLimit, PitchLimit);
    }

    /// <summary>
    /// The move keys set the wished velocity (along the view, sideways, straight up and down), the velocity follows it, the eye moves and is kept
    /// above the ground.
    /// </summary>
    void Fly(float dt, ActionState a, bool photoMode)
    {
        float fwd = (a.Held(InputAction.MoveForward) ? 1 : 0) - (a.Held(InputAction.MoveBack) ? 1 : 0);
        float right = (a.Held(InputAction.MoveRight) ? 1 : 0) - (a.Held(InputAction.MoveLeft) ? 1 : 0);
        float up = (a.Held(InputAction.MoveUp) ? 1 : 0) - (a.Held(InputAction.MoveDown) ? 1 : 0);
        if (photoMode)
        {
            // Whatever the game's keys are bound to: W A S D and the arrows move, Q goes down and E up.
            fwd = Down(a, Key.W, Key.Up) - Down(a, Key.S, Key.Down);
            right = Down(a, Key.D, Key.Right) - Down(a, Key.A, Key.Left);
            up = Down(a, Key.E, Key.E) - Down(a, Key.Q, Key.Q);
        }

        var f = Forward;
        var flat = new Vector3(f.X, 0, f.Z);
        flat = flat.LengthSquared() < 1e-6f ? new Vector3(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw)) : Vector3.Normalize(flat);
        var side = Vector3.Cross(flat, Vector3.UnitY);
        var wish = f * fwd + side * right + Vector3.UnitY * up;
        float factor = Photo is { } p
            ? (a.Input.IsDown(Key.LeftShift) || a.Input.IsDown(Key.RightShift) ? p.FastFactor : 1)
                * (a.Input.IsDown(Key.LeftControl) || a.Input.IsDown(Key.RightControl) ? p.SlowFactor : 1)
            : SpeedScale;
        var target = wish.LengthSquared() > 1e-6f ? Vector3.Normalize(wish) * Speed * factor : Vector3.Zero;

        Velocity = Vector3.Lerp(Velocity, target, 1 - MathF.Exp(-_settings.FreeResponse * dt));
        (_x, _y, _z) = (_x + (double)Velocity.X * dt, _y + (double)Velocity.Y * dt, _z + (double)Velocity.Z * dt);
        KeepAboveGround();
    }

    /// <summary>
    /// 1 when either key is down, else 0.
    /// </summary>
    static float Down(ActionState a, Key first, Key second) => a.Input.IsDown(first) || a.Input.IsDown(second) ? 1 : 0;

    /// <summary>
    /// Photo mode's extra: the roll keys (Z banks left, X right).
    /// </summary>
    void UpdatePhoto(PhotoControls photo, float dt, ActionState a)
    {
        float bank = (a.Input.IsDown(Key.X) ? 1 : 0) - (a.Input.IsDown(Key.Z) ? 1 : 0);
        Roll = Math.Clamp(Roll + bank * photo.RollSpeed * dt, -PhotoControls.MaxRoll, PhotoControls.MaxRoll);
    }

    /// <summary>
    /// Stands the camera at <paramref name="eye"/> looking along <paramref name="yaw"/> and <paramref name="pitch"/> (the strategy camera's
    /// conventions), at rest and level.
    /// </summary>
    public void Place(Vector3 eye, float yaw, float pitch)
    {
        Position = eye;
        Velocity = Vector3.Zero;
        Yaw = Interp.WrapAngle(yaw);
        Pitch = Math.Clamp(pitch, -PitchLimit, PitchLimit);
        Roll = 0;
        KeepAboveGround();
    }

    /// <summary>
    /// Takes the whole wheel notches into <see cref="SpeedScale"/> (away from the user is faster) and counts down <see cref="SpeedShown"/>.
    /// </summary>
    void AdjustSpeed(float dt, float wheel)
    {
        _speedShownLeft = MathF.Max(0, _speedShownLeft - dt);
        _wheelRest += wheel;
        int notches = (int)MathF.Truncate(_wheelRest);
        if (notches == 0) return;

        _wheelRest -= notches;
        _speedStep = Math.Clamp(_speedStep + notches, -MaxSpeedStep, MaxSpeedStep);
        _speedShownLeft = SpeedShownSeconds;
    }

    /// <summary>
    /// Lifts the eye to <see cref="CameraSettings.FreeClearance"/> above the ground under it, ending a fall.
    /// </summary>
    void KeepAboveGround()
    {
        float min = _ground(Position.X, Position.Z) + _settings.FreeClearance;
        if (_y >= min) return;
        _y = min;
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

/// <summary>
/// Photo mode's flight (<see cref="FreeCamera.Photo"/>): the speed is its own, set by the wheel, and does not follow the height above the ground.
/// </summary>
public sealed class PhotoControls
{
    /// <summary>
    /// The slowest and fastest fly speed (the wheel's range), and the most the view may bank (radians).
    /// </summary>
    public const float MinSpeed = 5, MaxSpeed = 30000, MaxRoll = MathF.PI / 2;

    /// <summary>
    /// Units per second (10 units are a metre) at the normal pace.
    /// </summary>
    public float FlySpeed = 300;

    /// <summary>
    /// What Shift and Ctrl multiply the speed by.
    /// </summary>
    public float FastFactor = 4, SlowFactor = 0.2f;

    /// <summary>
    /// Radians per second of the roll keys.
    /// </summary>
    public float RollSpeed = 0.7f;

    /// <summary>
    /// The turn of the view by a mouse movement or key, relative to a 50 degree lens: a narrow lens turns less.
    /// </summary>
    public float LookScale = 1;
}
