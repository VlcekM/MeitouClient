using System.Numerics;
using Meitou.Game.Rendering.Overlays;

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
    internal void PlaceMarker(Vector3 at)
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
        // The rotation marker: while the rotate button is held (FUN_1406b0f90); the renderer lays it over the pivot of the camera it draws with, which a followed
        // character moves every frame. Not in the free camera, which turns about its own eye, nor under the title or the editor, whose views are not the rig's.
        bool orbitHeld = session.Bindings.Get(Meitou.Game.Host.Input.InputAction.Orbit).Any(session.Input.IsHeld);
        Markers.Rotation = orbitHeld && RotationMarker && !session.Camera.IsFree && !TitleOpen && !characterEditor.EditorOpen;
        if (Markers is not { Strength: > 0 } marker) return;
        double speed = session.Simulation.Speed;
        marker.Strength = MathF.Max(0, marker.Strength - (speed > 0 ? frame * (float)speed : 0.01f));
    }
}
