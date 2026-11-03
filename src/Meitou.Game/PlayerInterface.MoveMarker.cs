using System.Numerics;
using Meitou.Rendering;

namespace Meitou.Game;

/// <summary>
/// The movement marker (docs/game/ui-input.md "Movement marker"): put down where a right click on the ground sends the selection and faded out over
/// one second of game time. The world renderer draws it (<see cref="MarkerRenderer"/>); this keeps its place and strength.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>The renderer of the movement marker and the stealth arrows (null: none).</summary>
    public MarkerRenderer? Markers { get; set; }
    long markerFrame;

    /// <summary>The `Movement marker` option: off, no move order puts the marker down (the original tests it where it shows the marker, <c>FUN_1407f2660</c>, <c>FUN_140800250</c>).</summary>
    public bool MovementMarker { get; set; } = true;

    /// <summary>The `Rotation marker` option: off, the marker on the pivot never shows (tested in the camera update <c>FUN_1406b0f90</c>).</summary>
    public bool RotationMarker { get; set; } = true;

    /// <summary>Shows the marker at <paramref name="at"/> at full strength (each move order given on the ground).</summary>
    void PlaceMarker(Vector3 at)
    {
        if (!MovementMarker || Markers is not { } marker) return;
        marker.At = at;
        marker.Strength = 1;
    }

    /// <summary>Lowers the marker's strength by this frame's game time (0.01 a frame at speed 0, as the original's scaled frame time).</summary>
    void FadeMarker()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float frame = markerFrame == 0 ? 0 : (float)System.Diagnostics.Stopwatch.GetElapsedTime(markerFrame, now).TotalSeconds;
        markerFrame = now;
        if (Markers is null) return;
        // The rotation marker: while the rotate button is held, 1 unit over the camera's pivot, flat, scaled max(3, 0.06 x distance) (FUN_1406b0f90).
        // Not in the free camera, which turns about its own eye.
        var pose = session.Camera.Current;
        bool turning = !session.Camera.IsFree && session.Bindings.Get(Meitou.Engine.Input.InputAction.Orbit).Any(session.Input.IsHeld);
        Markers.RotationAt = turning && RotationMarker ? pose.Target + Vector3.UnitY : null;
        Markers.RotationScale = MathF.Max(3, pose.Distance * 0.06f);
        if (Markers is not { Strength: > 0 } marker) return;
        double speed = session.Simulation.Speed;
        marker.Strength = MathF.Max(0, marker.Strength - (speed > 0 ? frame * (float)speed : 0.01f));
    }
}
