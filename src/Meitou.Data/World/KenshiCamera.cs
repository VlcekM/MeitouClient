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

    /// <summary>The highest the camera gets above its pivot: the longest boom at the steepest view (1840).</summary>
    public const float MaxHeightAbovePivot = MaxDistance * -MinViewY;
}
