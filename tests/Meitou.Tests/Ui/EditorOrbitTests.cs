using Meitou.Game;
using Meitou.Game.Editor;

namespace Meitou.Tests.Ui;

/// <summary>
/// The character editor's camera (<see cref="EditorOrbit"/>, docs/characters.md "Character editor", Preview): drag direction, zoom and the limits.
/// </summary>
public class EditorOrbitTests
{
    [Fact]
    public void Drag_right_turns_and_down_lifts_within_the_elevation_limits_and_the_wheel_zooms_within_the_mode_limits()
    {
        var orbit = new EditorOrbit();
        orbit.Drag(10, 0);
        Assert.True(orbit.Yaw < EditorOrbit.StartYaw);
        orbit.Drag(0, 10_000);
        Assert.Equal(EditorOrbit.MaxElevation, orbit.Elevation);
        orbit.Drag(0, -10_000);
        Assert.Equal(EditorOrbit.MinElevation, orbit.Elevation);

        // The wheel zooms within the limits of the mode.
        foreach (var (head, near, far) in new[] { (false, EditorOrbit.BodyNear, EditorOrbit.BodyFar),
                (true, EditorOrbit.HeadNear, EditorOrbit.HeadFar) })
        {
            orbit.Reset(head);
            float start = orbit.Distance;
            orbit.Zoom(1);
            Assert.True(orbit.Distance < start);
            orbit.Zoom(1000);
            Assert.Equal(near, orbit.Distance);
            orbit.Zoom(-1000);
            Assert.Equal(far, orbit.Distance);
        }
    }
}
