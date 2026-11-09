using System.Numerics;
using Meitou.Data.World;
using Meitou.Game;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Title;

namespace Meitou.Tests.Rendering;

public class TitleCompositionTests
{
    [Fact]
    public void TheSpotIsACameraCodeAndTheSunIsTurnedIntoTheRightOfItsView()
    {
        var camera = new WorldCamera();
        TitleComposition.Place(camera);
        var clock = new SkyClock(54, 5, 23);
        float hour = clock.Sunrise + 0.75f;
        var sun = (clock with { SunTurn = TitleComposition.SunTurn(clock, hour, camera) }).SunDirection(hour);

        // Same height as the game's sun at that hour; only its bearing moves, to the right of the heading.
        Assert.Equal(clock.SunDirection(hour).Y, sun.Y, 5);

        var ahead = Vector3.Normalize(camera.Forward with { Y = 0 });
        var flat = Vector3.Normalize(sun with { Y = 0 });

        Assert.InRange(MathF.Acos(Vector3.Dot(ahead, flat)) * 180 / MathF.PI, 20, 32);
        Assert.True(Vector3.Dot(flat, Vector3.Cross(ahead, Vector3.UnitY)) > 0, "the sun is right of the view");
    }
}
