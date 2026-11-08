using System.Numerics;
using Meitou.Game.Rendering.Overlays;

namespace Meitou.Game.Ui;

/// <summary>
/// The movement marker (docs/game/ui-input.md "Movement marker"): put down where a right click on the ground sends the selection and faded out over
/// one second of game time. The world renderer draws it (<see cref="MarkerRenderer"/>); this keeps its place and strength.
/// </summary>
sealed class MoveMarker(PlayerInterface player)
{
    long markerFrame;

    /// <summary>Shows the marker at <paramref name="at"/> at full strength (each move order given on the ground).</summary>
    internal void PlaceMarker(Vector3 at)
    {
        if (!player.MovementMarker || player.Markers is not { } marker) return;
        marker.At = at;
        marker.Strength = 1;
    }

    /// <summary>Lowers the marker's strength by this frame's game time (0.01 a frame at speed 0, as the original's scaled frame time).</summary>
    internal void FadeMarker()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float frame = markerFrame == 0 ? 0 : (float)System.Diagnostics.Stopwatch.GetElapsedTime(markerFrame, now).TotalSeconds;
        markerFrame = now;
        if (player.Markers is null) return;
        // The rotation marker: while the rotate button is held (FUN_1406b0f90); the renderer lays it over the pivot of the camera it draws with, which a followed
        // character moves every frame. Not in the free camera, which turns about its own eye, nor under the title or the editor, whose views are not the rig's.
        bool orbitHeld = player.session.Bindings.Get(Meitou.Game.Host.Input.InputAction.Orbit).Any(player.session.Input.IsHeld);
        player.Markers.Rotation = orbitHeld && player.RotationMarker && !player.session.Camera.IsFree && !player.TitleOpen && !player.characterEditor.EditorOpen;
        if (player.Markers is not { Strength: > 0 } marker) return;
        double speed = player.session.Simulation.Speed;
        marker.Strength = MathF.Max(0, marker.Strength - (speed > 0 ? frame * (float)speed : 0.01f));
    }
}
