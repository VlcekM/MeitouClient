namespace Meitou.Data.World;

/// <summary>
/// The limits of the game's strategy camera (docs/formats/camera.md): a pivot on the ground with the camera on a boom behind it.
/// Verified 2026-10-05 against <c>kenshi_x64.exe</c> (the camera rig's set-up, zoom and pitch steps): the boom is clamped to
/// <see cref="MinDistance"/>..<see cref="MaxDistance"/>, the view direction's height component to
/// <see cref="MinViewY"/>..<see cref="MaxViewY"/>; nothing in the settings or the game data changes either.
/// </summary>
public static class KenshiCamera
{
    /// <summary>Shortest boom (pivot to camera), world units.</summary>
    public const float MinDistance = 10;
    /// <summary>Longest boom: the zoom step clamps to it (the same constant scales the <c>Camera_Zoom</c> sound parameter).</summary>
    public const float MaxDistance = 2000;
    /// <summary>The boom when the camera is created or reset.</summary>
    public const float InitialDistance = 150;
    /// <summary>The pitch when the camera is created or reset, degrees below the horizon.</summary>
    public const float InitialPitchDegrees = 30;
    /// <summary>Steepest view: a pitch step is refused once the view direction's y is at or below this (about 67 degrees down).</summary>
    public const float MinViewY = -0.92f;
    /// <summary>Highest view: a pitch step upwards is refused once the view direction's y is at or above this (about 11.5 degrees up).</summary>
    public const float MaxViewY = 0.2f;

    /// <summary>The zoom step scales by <c>min(boom / ZoomLengthDivisor, ZoomScaleCap)</c>.</summary>
    public const float ZoomLengthDivisor = 600;
    /// <summary>Largest factor of the zoom step's length scaling.</summary>
    public const float ZoomScaleCap = 0.75f;
    /// <summary><c>settings.cfg</c> <c>camera zoom</c> in the install (the zoom speed the step multiplies).</summary>
    public const float DefaultZoomSpeed = 125;
    /// <summary><c>settings.cfg</c> <c>camera speed</c> in the install (units not traced).</summary>
    public const float DefaultMoveSpeed = 500;
    /// <summary>Zoom input of one wheel notch: the mouse reports 120 per notch and the camera divides by 100.</summary>
    public const float WheelNotchInput = 1.2f;
    /// <summary>Zoom input per second of a held <c>camera_zoom_in</c> / <c>camera_zoom_out</c> key (three times that with Shift).</summary>
    public const float ZoomKeyRate = 5;
    public const float ZoomKeyShiftFactor = 3;
    /// <summary>The boom closes on the zoom step's target by <c>min(ZoomFollowRate · frame time, 1)</c> of the remaining distance per frame.</summary>
    public const float ZoomFollowRate = 70;

    /// <summary><c>camera speed</c> scaled by <c>boom / ZoomLengthDivisor</c> (no cap) is the pan speed in units per second.</summary>
    public static float PanSpeed(float moveSpeed, float boom) => moveSpeed * boom / ZoomLengthDivisor;

    /// <summary>The rotation and tilt keys' input per frame (the mouse's is its movement in pixels times <see cref="DefaultMouseSpeed"/>).</summary>
    public const float YawKeyInput = 9;
    public const float TiltKeyInput = 2;
    /// <summary>Settings <c>mouse speed pan</c> / <c>mouse speed tilt</c> (Camera Rotate Speed X / Y): degrees per second per unit of input.</summary>
    public const float DefaultRotateSpeed = 6;
    /// <summary>Settings <c>mouse speed horizontal</c> / <c>vertical</c>: scale of the raw mouse movement.</summary>
    public const float DefaultMouseSpeed = 1.5f;
    /// <summary>The yaw and pitch inputs are averaged over the last <see cref="SmoothingFrames"/> frames with weights 1, a, a², ... (newest first).</summary>
    public const int SmoothingFrames = 20;
    public const float YawSmoothing = 0.5f;
    public const float PitchSmoothing = 0.7f;

    /// <summary>The pivot's height closes on its ground target at <c>HeightRate</c> times the gap per second (a gap of up to <see cref="HeightSnap"/> is closed at once).</summary>
    public const float HeightRate = 10;
    public const float HeightSnap = 0.1f;

    /// <summary>Edge scrolling pans while the pointer is within this fraction of the screen's width or height of an edge.</summary>
    public const float EdgeMargin = 0.01f;

    /// <summary>The eye is kept above the ground under it by <c>min(ClearanceBoomFraction · boom, ClearanceCap) + ClearanceBase</c>.</summary>
    public const float ClearanceBoomFraction = 0.2f;
    public const float ClearanceCap = 20;
    public const float ClearanceBase = 10;

    /// <summary>How far the eye is kept above the ground under it for a boom of <paramref name="boom"/>.</summary>
    public static float EyeClearance(float boom) => MathF.Min(ClearanceBoomFraction * boom, ClearanceCap) + ClearanceBase;

    /// <summary>The boom after one zoom step of <paramref name="input"/> (positive zooms in), clamped to the limits.</summary>
    public static float ZoomedDistance(float boom, float zoomSpeed, float input) =>
        Math.Clamp(boom - zoomSpeed * input * MathF.Min(boom / ZoomLengthDivisor, ZoomScaleCap), MinDistance, MaxDistance);

    /// <summary>The highest the camera gets above its pivot: the longest boom at the steepest view (1840).</summary>
    public const float MaxHeightAbovePivot = MaxDistance * -MinViewY;

    /// <summary>
    /// Engine choice: a free camera's eye this far above the ground under it is in flight. The world's focus (squads, navmesh) stays where it left the
    /// ground (docs/formats/camera.md "In the game") and the weather's fog and effects are off. The strategy camera never gets this high (<see cref="MaxHeightAbovePivot"/>).
    /// </summary>
    public const float FlightHeight = 3000;

    // ------------------------------------------------------------------ the saved camera (docs/formats/camera.md "Saved camera")

    /// <summary>A saved pivot whose x or z is outside ±this is replaced on load (<c>FUN_1407ade60</c>).</summary>
    public const float SavedPivotLimit = 147456;
    /// <summary>Where the loader puts a pivot that is out of bounds (x, z; the height comes from the ground).</summary>
    public static readonly System.Numerics.Vector2 SavedPivotFallback = new(500, 51000);

    /// <summary>Whether a saved pivot is inside the bounds the loader accepts.</summary>
    public static bool SavedPivotInBounds(System.Numerics.Vector3 pivot) =>
        pivot.X >= -SavedPivotLimit && pivot.X <= SavedPivotLimit && pivot.Z >= -SavedPivotLimit && pivot.Z <= SavedPivotLimit;

    /// <summary>
    /// The CAMERA <c>rot</c> and <c>zoom</c> for a view: <c>rot</c> is the pivot's orientation, yaw about Y then pitch about the pivot's X, stored w, x, y, z; <c>zoom</c> is
    /// the camera's position on the pivot's local Z, minus the boom. <paramref name="yaw"/> 0 looks along -Z, <paramref name="pitch"/> is radians below the horizon.
    /// </summary>
    public static (System.Numerics.Vector4 Rot, float Zoom) ToSaved(float yaw, float pitch, float distance)
    {
        // Ry(yaw + pi) * Rx(pitch): the pivot's +Z is the view direction, the camera sits at -distance on it.
        float a = (yaw + MathF.PI) * 0.5f, b = pitch * 0.5f;
        float aw = MathF.Cos(a), ay = MathF.Sin(a), bw = MathF.Cos(b), bx = MathF.Sin(b);
        var q = new System.Numerics.Vector4(aw * bw, aw * bx, ay * bw, -ay * bx);
        return (q.X < 0 ? -q : q, -distance);
    }

    /// <summary>The view a CAMERA <c>rot</c> (w, x, y, z) and <c>zoom</c> describe: yaw, pitch and boom as <see cref="ToSaved"/> takes them.</summary>
    public static (float Yaw, float Pitch, float Distance) FromSaved(System.Numerics.Vector4 rot, float zoom)
    {
        float w = rot.X, x = rot.Y, y = rot.Z, z = rot.W;
        // The pivot's +Z in world space; a camera on the +Z side (zoom > 0) looks the other way.
        var forward = new System.Numerics.Vector3(2 * (x * z + w * y), 2 * (y * z - w * x), 1 - 2 * (x * x + y * y));
        if (zoom > 0) forward = -forward;
        float yaw = MathF.Atan2(-forward.X, -forward.Z);
        float pitch = MathF.Asin(Math.Clamp(-forward.Y, -1, 1));
        return (yaw, pitch, MathF.Abs(zoom));
    }
}
