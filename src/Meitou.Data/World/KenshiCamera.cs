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
}
