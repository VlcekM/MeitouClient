using Meitou.Data.World;

namespace Meitou.Engine.Cameras;

/// <summary>Tunables of the cameras. Speeds the game's settings define are named after them; the rest are engine choices.</summary>
public sealed class CameraSettings
{
    /// <summary><c>camera zoom</c> (settings.cfg, default 125): the speed the zoom step multiplies (docs/formats/camera.md).</summary>
    public float ZoomSpeed { get; set; } = KenshiCamera.DefaultZoomSpeed;
    /// <summary>
    /// <c>camera speed</c> (settings.cfg, default 500). Its exact units are Unknown; the engine reads it as ground units per second
    /// at a boom of 600 or more, and less for a shorter boom (see <see cref="StrategyCamera"/>).
    /// </summary>
    public float MoveSpeed { get; set; } = KenshiCamera.DefaultMoveSpeed;
    /// <summary>Engine choice: wheel notches per second a held zoom key counts as.</summary>
    public float ZoomKeyNotchesPerSecond { get; set; } = 6;
    /// <summary>Engine choice: radians per second of the rotate and pitch keys.</summary>
    public float RotateSpeed { get; set; } = 1.5f;
    /// <summary>Engine choice: radians per pixel of a mouse drag (the viewer's orbit value).</summary>
    public float MouseSensitivity { get; set; } = 0.005f;
    /// <summary>Engine choice: largest pitch change applied between two checks of the pitch limit, radians.</summary>
    public float MaxPitchSubStep { get; set; } = 0.02f;
    /// <summary>Engine choice: when true the pitch is also clamped to the limits after a step (no overshoot); the game's rule is the refusal.</summary>
    public bool HardPitchClamp { get; set; }

    /// <summary>Engine choice: free camera speed in units per second per unit of height above the ground.</summary>
    public float FreeSpeedPerHeight { get; set; } = 1.5f;
    public float FreeMinSpeed { get; set; } = 50;
    public float FreeMaxSpeed { get; set; } = 30000;
    /// <summary>Engine choice: how far above the ground the free camera is kept (it is only kept a little above it in the game).</summary>
    public float FreeClearance { get; set; } = 10;
    /// <summary>Engine choice: how fast the free camera's velocity follows the input (1/s).</summary>
    public float FreeResponse { get; set; } = 6;
}
