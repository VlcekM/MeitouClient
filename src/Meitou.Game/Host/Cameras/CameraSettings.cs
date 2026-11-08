using Meitou.Data.World;

namespace Meitou.Engine.Cameras;

/// <summary>Tunables of the cameras. Speeds the game's settings define are named after them; the rest are engine choices.</summary>
public sealed class CameraSettings
{
    /// <summary><c>camera zoom</c> (settings.cfg, default 125): the speed the zoom step multiplies (docs/formats/camera.md).</summary>
    public float ZoomSpeed { get; set; } = KenshiCamera.DefaultZoomSpeed;
    /// <summary><c>camera speed</c> (settings.cfg, default 500): the pan speed in units per second at a boom of 600 (it scales with the boom).</summary>
    public float MoveSpeed { get; set; } = KenshiCamera.DefaultMoveSpeed;
    /// <summary><c>mouse speed pan</c> (default 6): degrees per second of turn per unit of yaw input.</summary>
    public float RotateSpeedX { get; set; } = KenshiCamera.DefaultRotateSpeed;
    /// <summary><c>mouse speed tilt</c> (default 6): degrees per second of tilt per unit of pitch input.</summary>
    public float RotateSpeedY { get; set; } = KenshiCamera.DefaultRotateSpeed;
    /// <summary><c>mouse speed horizontal</c> / <c>vertical</c> (default 1.5): scale of the mouse movement in pixels.</summary>
    public float MouseSpeedX { get; set; } = KenshiCamera.DefaultMouseSpeed;
    public float MouseSpeedY { get; set; } = KenshiCamera.DefaultMouseSpeed;
    /// <summary><c>invert X</c> (default off) and <c>invert Y</c> (default on): flip the turn and the tilt, keys and mouse alike.</summary>
    public bool InvertX { get; set; }
    public bool InvertY { get; set; } = true;
    /// <summary><c>Edge scrolling</c> (default on): the pointer at the edge of the screen pans the camera.</summary>
    public bool EdgeScroll { get; set; } = true;
    /// <summary>
    /// The game's camera step runs once per drawn frame with the frame's time, and some of its inputs are per frame (the mouse movement,
    /// the 20-frame averaging of the turn and tilt), so the feel depends on the frame rate. A control tick is split into steps of about
    /// this length so the camera behaves as the game at that frame rate whatever the tick rate (engine choice).
    /// </summary>
    public float ReferenceFrameSeconds { get; set; } = 1f / 60;
    /// <summary>Engine choice: largest pitch change applied between two checks of the pitch limit, radians.</summary>
    public float MaxPitchSubStep { get; set; } = 0.02f;
    /// <summary>Engine choice: when true the pitch is also clamped to the limits after a step (no overshoot); the game's rule is the refusal.</summary>
    public bool HardPitchClamp { get; set; }

    /// <summary>Engine choice (free camera): radians per second of the rotate and pitch keys.</summary>
    public float RotateSpeed { get; set; } = 1.5f;
    /// <summary>Engine choice (free camera): radians per pixel of a mouse drag (the viewer's orbit value).</summary>
    public float MouseSensitivity { get; set; } = 0.005f;
    /// <summary>Engine choice: free camera speed in units per second per unit of height above the ground.</summary>
    public float FreeSpeedPerHeight { get; set; } = 1.5f;
    public float FreeMinSpeed { get; set; } = 50;
    public float FreeMaxSpeed { get; set; } = 30000;
    /// <summary>Engine choice: how far above the ground the free camera is kept (it is only kept a little above it in the game).</summary>
    public float FreeClearance { get; set; } = 10;
    /// <summary>Engine choice: how fast the free camera's velocity follows the input (1/s).</summary>
    public float FreeResponse { get; set; } = 6;
}
