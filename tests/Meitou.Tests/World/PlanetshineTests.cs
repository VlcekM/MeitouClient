using System.Numerics;
using Meitou.Data.Textures;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Tests.World;

/// <summary>
/// Meitou's planetshine (docs/formats/sky.md "Planetshine"): the phase angle and Lambert phase, the planet's irradiance, the twilight blend.
/// </summary>
public class PlanetshineTests
{
    static readonly SkyPlanet Moon = SkyPlanet.All[0];

    [Theory]
    [InlineData(-1)]   // dusk: the sun sets at -X
    [InlineData(1)]    // dawn: it rises at +X
    public void The_light_through_dusk_and_dawn_changes_smoothly_and_turns_in_the_dark(int side)
    {
        // The sun crosses the horizon at 0.05° steps from 20° up to 40° below, with the planet at the game's strength: neither the light's direction
        // nor its brightness may jump (no pop where the planet takes over from the sun, the shadows follow the direction), and the direction's
        // quickest turn comes while the light is dim, a fifth of the noon sun's or less (deep twilight, the sun a few degrees down), not in the
        // sunset.
        float strength = Enhancements.MeitouPlanetshineStrength;
        float reference = Planetshine.Luminance(KenshiLighting.SunLight(Vector3.UnitY));
        var albedo = new Vector3(0.28f, 0.29f, 0.26f);
        (Vector3 Direction, float Brightness) At(float degrees)
        {
            float a = degrees * MathF.PI / 180;   // below the horizon for positive degrees
            var sun = Vector3.Normalize(new Vector3(side * MathF.Cos(a), -MathF.Sin(a), -0.2f * MathF.Sin(a)));
            var sunLight = KenshiLighting.SunLight(sun);
            var planetLight = Planetshine.Light(sun, Moon, albedo, reference, strength) * Planetshine.Weight(sun.Y);
            var (direction, light) = Planetshine.Combine(KenshiLighting.LightDirection(sun), sunLight, Moon.Towards, planetLight);
            return (direction, Planetshine.Luminance(light));
        }
        var last = At(-20);
        float worstAngle = 0, worstJump = 0, brightnessAtWorstAngle = 0;
        for (float degrees = -19.95f; degrees <= 40; degrees += 0.05f)
        {
            var now = At(degrees);
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(last.Direction, now.Direction), -1, 1)) * 180 / MathF.PI;
            if (angle > worstAngle) (worstAngle, brightnessAtWorstAngle) = (angle, now.Brightness / reference);
            worstJump = MathF.Max(worstJump,
                    MathF.Abs(now.Brightness - last.Brightness) / MathF.Max(MathF.Max(now.Brightness, last.Brightness), 1e-6f));
            last = now;
        }
        Assert.True(worstAngle < 5f, $"direction step {worstAngle}°");
        Assert.True(worstJump < 0.1f, $"brightness step {worstJump:P1}");
        Assert.True(brightnessAtWorstAngle < 0.2f, $"the quickest turn is at {brightnessAtWorstAngle:P0} of the reference sun light");
    }
}
