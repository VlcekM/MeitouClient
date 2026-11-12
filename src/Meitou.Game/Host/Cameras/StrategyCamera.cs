using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Host.Input;
using Meitou.Simulation.Session;

namespace Meitou.Game.Host.Cameras;

/// <summary>
/// The game's strategy camera (docs/formats/camera.md, Verified against <c>kenshi_x64.exe</c>): a pivot with the camera on a boom behind
/// it. Boom 150 and 30 degrees down at the start; zoom, pan, turn, tilt, edge scrolling and the pivot's height follow the game's
/// per-frame step, run in steps of <see cref="CameraSettings.ReferenceFrameSeconds"/>:
/// <list type="bullet">
/// <item>zoom: <c>boom -= zoomSpeed · input · min(boom / 600, 0.75)</c> clamped to [10, 2000], input 1.2 per wheel notch;</item>
/// <item>pan: <c>camera speed · boom / 600</c> units per second along the heading, the diagonal skewed by the pitch as in the game;</item>
/// <item>turn and tilt: key and mouse input averaged over the last 20 frames, times the rotate speed and the frame time;
/// a pitch step is refused when the real view direction's y is already at or below -0.92 (down) or at or above 0.2 (up), checked before
/// the step (here a step is cut into pieces of at most <see cref="CameraSettings.MaxPitchSubStep"/>, each checked, so a big mouse jump
/// overshoots no more than a key step);</item>
/// <item>height: the pivot never goes below the ground at it (the pivot ground: buildings' floors too) or the lowest ground under the camera, is
/// raised until the eye clears the ground under it by <c>min(0.2 · boom, 20) + 10</c> (the view is not tilted for it) and closes on that height at
/// ten times the gap per second.</item>
/// </list>
/// </summary>
public sealed class StrategyCamera
{
    /// <summary>
    /// Eye pitch (radians above the horizon) at the highest view the rule allows, view y = <see cref="KenshiCamera.MaxViewY"/>.
    /// </summary>
    public static readonly float MinPitch = -MathF.Asin(KenshiCamera.MaxViewY);
    /// <summary>
    /// Eye pitch at the steepest view the rule allows, view y = <see cref="KenshiCamera.MinViewY"/>.
    /// </summary>
    public static readonly float MaxPitch = MathF.Asin(-KenshiCamera.MinViewY);

    /// <summary>
    /// Degrees to radians, for the rotate speeds (degrees per second).
    /// </summary>
    const float DegreesToRadians = MathF.PI / 180;

    /// <summary>
    /// The ground under the eye, and the ground under the pivot (with buildings' floors).
    /// </summary>
    readonly Func<float, float, float> _ground, _pivotGround;
    /// <summary>
    /// The settings the camera reads.
    /// </summary>
    readonly CameraSettings _settings;
    /// <summary>
    /// The turn input averaged over the last frames (<see cref="KenshiCamera.YawSmoothing"/>).
    /// </summary>
    readonly SmoothedInput _yawInput = new(KenshiCamera.YawSmoothing, KenshiCamera.SmoothingFrames);
    /// <summary>
    /// The tilt input averaged over the last frames (<see cref="KenshiCamera.PitchSmoothing"/>).
    /// </summary>
    readonly SmoothedInput _pitchInput = new(KenshiCamera.PitchSmoothing, KenshiCamera.SmoothingFrames);
    /// <summary>
    /// The pivot's height, which closes on its goal (<see cref="FollowHeight"/>).
    /// </summary>
    float _pivotY;
    /// <summary>
    /// The pivot height being closed on; a new goal sets <see cref="_heightSpeed"/> again.
    /// </summary>
    float _heightGoal;
    /// <summary>
    /// The pivot's climb or descent per second towards <see cref="_heightGoal"/>; 0 once there.
    /// </summary>
    float _heightSpeed;

    /// <summary>
    /// A camera at the start view (<see cref="KenshiCamera.InitialDistance"/>, <see cref="KenshiCamera.InitialPitchDegrees"/>), its pivot
    /// settled on the ground.
    /// </summary>
    /// <param name="ground">The ground under the eye.</param>
    /// <param name="pivotGround">The ground under the pivot (the game's pivot ray also hits buildings' floors, its eye ray does not raise the eye for
    /// them); the eye's by default.</param>
    public StrategyCamera(Func<float, float, float> ground, CameraSettings? settings = null, Func<float, float, float>? pivotGround = null)
    {
        this._ground = ground;
        this._pivotGround = pivotGround ?? ground;
        this._settings = settings ?? new CameraSettings();
        Pitch = KenshiCamera.InitialPitchDegrees * MathF.PI / 180;
        Distance = KenshiCamera.InitialDistance;
        SettleHeight();
        State = Compute();
    }

    /// <summary>
    /// Pivot position on the ground plane.
    /// </summary>
    public Vector2 PivotXz { get; private set; }
    /// <summary>
    /// Radians; the view's heading (0 looks along -Z), as <see cref="CameraState.Yaw"/>.
    /// </summary>
    public float Yaw { get; private set; }
    /// <summary>
    /// The pitch (eye above the horizon, radians): the pivot's, which the ground never changes.
    /// </summary>
    public float Pitch { get; private set; }
    /// <summary>
    /// The boom, 10..2000.
    /// </summary>
    public float Distance { get; private set; }
    /// <summary>
    /// The camera as of the last <see cref="Update"/> or placement.
    /// </summary>
    public CameraState State { get; private set; }

    /// <summary>
    /// The pivot, at its height.
    /// </summary>
    public Vector3 Pivot => new(PivotXz.X, _pivotY, PivotXz.Y);

    /// <summary>
    /// Where the pointer is in the view as a fraction of its width and height (0..1), for edge scrolling; null when it must not scroll
    /// (the window is not in front, the pointer is outside it, something else uses the pointer). Set by the host before each frame.
    /// </summary>
    public Vector2? EdgePointer { get; set; }

    /// <summary>
    /// The drawn frame's length in seconds, which the turn and tilt averaging counts its frames in (<see cref="FramesPerStep"/>); 0 is a step's.
    /// Set by the host before each frame.
    /// </summary>
    public float DrawnFrameSeconds { get; set; }

    /// <summary>
    /// Sets the pivot and view (yaw wrapped, pitch kept within the pitch limits, boom within the zoom limits); the pivot's height is found at once.
    /// </summary>
    public void Place(Vector2 pivotXz, float yaw, float pitch, float distance)
    {
        PivotXz = pivotXz;
        Yaw = Interp.WrapAngle(yaw);
        Pitch = Math.Clamp(pitch, MinPitch, MaxPitch);
        Distance = Math.Clamp(distance, KenshiCamera.MinDistance, KenshiCamera.MaxDistance);
        SettleHeight();
        State = Compute();
    }

    /// <summary>
    /// One zoom step of <paramref name="notches"/> wheel notches (positive zooms in).
    /// </summary>
    public void Zoom(float notches)
    {
        ZoomBy(notches * KenshiCamera.WheelNotchInput, 1);
        State = Compute();
    }

    /// <summary>
    /// A pitch step of <paramref name="delta"/> radians, positive tilting the view steeper (down). Refused piece by piece once the
    /// real view direction is at the limit, as the game does.
    /// </summary>
    public void PitchStep(float delta)
    {
        if (delta == 0 || !float.IsFinite(delta)) return;
        int pieces = (int)Math.Ceiling(Math.Abs(delta) / _settings.MaxPitchSubStep);
        float piece = delta / pieces;
        for (int i = 0; i < pieces; i++)
        {
            float y = State.Forward.Y;
            if (piece > 0 ? y <= KenshiCamera.MinViewY : y >= KenshiCamera.MaxViewY) break;
            Pitch += piece;
            State = Compute();
        }
        if (_settings.HardPitchClamp)
        {
            Pitch = Math.Clamp(Pitch, MinPitch, MaxPitch);
            State = Compute();
        }
    }

    /// <summary>
    /// Turns the view about the vertical (positive turns it to the left).
    /// </summary>
    public void Rotate(float radians)
    {
        Yaw = Interp.WrapAngle(Yaw + radians);
        State = Compute();
    }

    /// <summary>
    /// Moves the pivot on the ground plane: <paramref name="forward"/> along the view's heading, <paramref name="right"/> sideways, world units.
    /// </summary>
    public void Move(float forward, float right)
    {
        var f = new Vector2(-MathF.Sin(Yaw), -MathF.Cos(Yaw));
        var r = new Vector2(MathF.Cos(Yaw), -MathF.Sin(Yaw));
        PivotXz += f * forward + r * right;
        State = Compute();
    }

    /// <summary>
    /// One tick: zoom (wheel and keys), pan (keys and edges), turn and tilt (keys and mouse drag), then the pivot's height, in game-sized frames.
    /// </summary>
    public void Update(float dt, ActionState a)
    {
        int frames = Math.Max(1, (int)MathF.Round(dt / _settings.ReferenceFrameSeconds));
        float h = dt / frames;
        for (int i = 0; i < frames; i++) Frame(h, a, 1f / frames);
        State = Compute();
    }

    /// <summary>
    /// One frame of the game's camera step; <paramref name="share"/> is this frame's part of the tick's mouse movement and wheel.
    /// </summary>
    void Frame(float h, ActionState a, float share)
    {
        // Zoom: the wheel (1.2 per notch) and the zoom keys (5 per second, 15 with Shift).
        float zoom = a.Wheel * share * KenshiCamera.WheelNotchInput;
        float zoomKeys = (a.Held(InputAction.ZoomIn) ? 1 : 0) - (a.Held(InputAction.ZoomOut) ? 1 : 0);
        if (zoomKeys != 0)
        {
            bool shift = a.Input.IsDown(Key.LeftShift) || a.Input.IsDown(Key.RightShift);
            zoom += zoomKeys * KenshiCamera.ZoomKeyRate * (shift ? KenshiCamera.ZoomKeyShiftFactor : 1) * h;
        }
        if (zoom != 0) ZoomBy(zoom, h);

        // Pan: the keys and the pointer at an edge add up; the direction is unit length whatever it is made of.
        float fwd = (a.Held(InputAction.MoveForward) ? 1 : 0) - (a.Held(InputAction.MoveBack) ? 1 : 0);
        float right = (a.Held(InputAction.MoveRight) ? 1 : 0) - (a.Held(InputAction.MoveLeft) ? 1 : 0);
        float speed = fwd != 0 || right != 0 ? KeyPanFactor(a) : 1;
        if (_settings.EdgeScroll && EdgePointer is { } p)
        {
            if (p.X < KenshiCamera.EdgeMargin) right -= 1;
            if (p.X > 1 - KenshiCamera.EdgeMargin) right += 1;
            if (p.Y < KenshiCamera.EdgeMargin) fwd += 1;
            if (p.Y > 1 - KenshiCamera.EdgeMargin) fwd -= 1;
        }
        if (fwd != 0 || right != 0) Pan(fwd, right, h * speed);

        TurnAndTilt(h, a, share);
        FollowHeight(h);
    }

    /// <summary>
    /// Turn and tilt: the held keys and, while the rotate button is down, the mouse movement, averaged over the last frames (engine choice: not
    /// averaged with <see cref="CameraSettings.Smoothing"/> off).
    /// </summary>
    void TurnAndTilt(float h, ActionState a, float share)
    {
        float yaw = (a.Held(InputAction.RotateRight) ? KenshiCamera.YawKeyInput : 0)
            - (a.Held(InputAction.RotateLeft) ? KenshiCamera.YawKeyInput : 0);
        float tilt = (a.Held(InputAction.PitchUp) ? KenshiCamera.TiltKeyInput : 0) - (a.Held(InputAction.PitchDown) ? KenshiCamera.TiltKeyInput : 0);
        if (a.Held(InputAction.Orbit))
        {
            yaw -= a.MouseDelta.X * share * _settings.MouseSpeedX;
            tilt -= a.MouseDelta.Y * share * _settings.MouseSpeedY;
        }

        float frames = FramesPerStep(h);
        float turn = _yawInput.Push(yaw, frames);
        float lean = _pitchInput.Push(tilt, frames);
        if (!_settings.Smoothing)
        {
            turn = yaw;
            lean = tilt;
        }
        if (turn == 0 && lean == 0) return;

        Yaw = Interp.WrapAngle(Yaw + _settings.RotateSpeedX * turn * h * (_settings.InvertX ? -1 : 1) * DegreesToRadians);
        State = Compute();
        PitchStep(_settings.RotateSpeedY * lean * h * (_settings.InvertY ? -1 : 1) * DegreesToRadians);
    }

    /// <summary>
    /// How many of the game's frames a step of <paramref name="h"/> seconds spans, for the averaging. The game averages drawn frames, so its lag
    /// shrinks as the frame rate rises; the step itself stays about <see cref="CameraSettings.ReferenceFrameSeconds"/>, which the mouse's turn per
    /// pixel goes with. Engine choice: a drawn frame longer than the step counts as the step, so a low frame rate lags no more than 60 fps.
    /// </summary>
    float FramesPerStep(float h) => DrawnFrameSeconds > 0 && DrawnFrameSeconds < h ? h / DrawnFrameSeconds : 1;

    /// <summary>
    /// A zoom step of <paramref name="input"/>; the boom closes on its new length at <see cref="KenshiCamera.ZoomFollowRate"/> per second.
    /// </summary>
    void ZoomBy(float input, float h)
    {
        float target = KenshiCamera.ZoomedDistance(Distance, _settings.ZoomSpeed, input);
        Distance += (target - Distance) * MathF.Min(KenshiCamera.ZoomFollowRate * h, 1);
    }

    /// <summary>
    /// The pan speed factor of the pan keys: <see cref="CameraSettings.ShiftPanFactor"/> while Shift is held, else 1.
    /// </summary>
    float KeyPanFactor(ActionState a) => a.Input.IsDown(Key.LeftShift) || a.Input.IsDown(Key.RightShift) ? _settings.ShiftPanFactor : 1;

    /// <summary>
    /// Pans along the heading. The game turns the key direction by the pivot's orientation, drops its height and normalises: sideways is
    /// unchanged by the pitch, forward shrinks by its cosine, so a diagonal leans to the side the steeper the view is.
    /// </summary>
    void Pan(float forward, float right, float h)
    {
        var f = new Vector2(-MathF.Sin(Yaw), -MathF.Cos(Yaw));
        var r = new Vector2(MathF.Cos(Yaw), -MathF.Sin(Yaw));
        var direction = f * (forward * MathF.Cos(Pitch)) + r * right;
        if (direction.LengthSquared() < 1e-12f) return;
        PivotXz += Vector2.Normalize(direction) * (KenshiCamera.PanSpeed(_settings.MoveSpeed, Distance) * h);
    }

    /// <summary>
    /// The height the pivot is heading for, given its height now (the game's per-frame ground step; the view direction is not touched).
    /// </summary>
    float HeightGoal(float y)
    {
        var eye = CameraState.EyeFor(new Vector3(PivotXz.X, y, PivotXz.Y), Yaw, Pitch, Distance);
        float groundAtPivot = _pivotGround(PivotXz.X, PivotXz.Y);
        float groundAtEye = _ground(eye.X, eye.Z);
        float drop = MathF.Min(0, groundAtEye - y);   // a pivot above the ground under the eye comes down to it ...
        float required = groundAtEye + KenshiCamera.EyeClearance(Distance);
        float goal = y + drop;
        if (eye.Y + drop <= required) goal = y + (required - eye.Y);   // ... unless the eye would then be too low: up by the shortfall
        return MathF.Max(goal, groundAtPivot);
    }

    /// <summary>
    /// Puts the pivot at its goal height at once (a placement, not a frame).
    /// </summary>
    void SettleHeight()
    {
        _pivotY = _pivotGround(PivotXz.X, PivotXz.Y);
        for (int i = 0; i < 3; i++) _pivotY = HeightGoal(_pivotY);
        _heightGoal = _pivotY;
        _heightSpeed = 0;
    }

    /// <summary>
    /// The pivot's height closes on its goal: a gap of up to 0.1 at once, else at ten times the gap per second, the speed being set when the
    /// goal changes (so a goal that keeps moving is followed with a lag of a tenth of a second, a steady one is reached in a straight line).
    /// </summary>
    void FollowHeight(float h)
    {
        float goal = HeightGoal(_pivotY);
        float y;
        if (MathF.Abs(goal - _pivotY) > KenshiCamera.HeightSnap)
        {
            if (goal != _heightGoal)
            {
                _heightGoal = goal;
                _heightSpeed = (goal - _pivotY) * KenshiCamera.HeightRate;
            }
            y = _pivotY + _heightSpeed * h;
            if (_heightSpeed > 0 ? y >= _heightGoal : y <= _heightGoal)
            {
                y = _heightGoal;
                _heightSpeed = 0;
            }
        }
        else
        {
            y = goal;
            _heightGoal = goal;
            _heightSpeed = 0;
        }
        // Engine choice: the lag never shows the eye under the ground (the game's smoothing alone lets a fast climb dip into it for a few frames).
        var eye = CameraState.EyeFor(new Vector3(PivotXz.X, y, PivotXz.Y), Yaw, Pitch, Distance);
        float shortfall = _ground(eye.X, eye.Z) + KenshiCamera.EyeClearance(Distance) - eye.Y;
        _pivotY = shortfall > 0 ? y + shortfall : y;
    }

    /// <summary>
    /// The camera state from the pivot, yaw, pitch and boom.
    /// </summary>
    CameraState Compute()
    {
        var pivot = Pivot;
        var eye = CameraState.EyeFor(pivot, Yaw, Pitch, Distance);
        var forward = (pivot - eye) / Distance;
        return new CameraState(eye, forward, Vector3.UnitY, pivot, Yaw, Pitch, Distance, false);
    }

    /// <summary>
    /// The average of the last frames' input with weights 1, a, a², ... from the newest back (the game's turn and tilt smoothing). A sample may span
    /// several frames: the weights then fall by a per frame spanned and the window is as many samples as hold its frames.
    /// </summary>
    sealed class SmoothedInput(float decay, int frames)
    {
        /// <summary>
        /// The last frames' input, a ring.
        /// </summary>
        readonly float[] _samples = new float[frames];
        /// <summary>
        /// The ring's newest sample.
        /// </summary>
        int _newest;

        /// <summary>
        /// Adds the input of a sample spanning <paramref name="span"/> frames (1 or more) and returns the weighted average.
        /// </summary>
        public float Push(float value, float span)
        {
            _newest = (_newest + 1) % _samples.Length;
            _samples[_newest] = value;

            float step = span == 1 ? decay : MathF.Pow(decay, span);
            int window = Math.Max(1, (int)MathF.Ceiling(_samples.Length / span));
            float sum = 0, weights = 0, weight = 1;
            for (int i = 0; i < window; i++)
            {
                sum += weight * _samples[(_newest - i + _samples.Length) % _samples.Length];
                weights += weight;
                weight *= step;
            }

            return sum / weights;
        }
    }
}