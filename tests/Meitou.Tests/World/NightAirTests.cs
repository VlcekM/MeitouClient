using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Tests.World;

/// <summary>
/// Meitou's night air (docs/formats/sky.md "Night air"): the glow the haze fades the far land into at night and the sky shows above the horizon, and
/// the water's planet glint.
/// </summary>
public class NightAirTests
{
    static readonly SkyPlanet Moon = SkyPlanet.All[0];
    static readonly Vector3 Albedo = new(0.284f, 0.293f, 0.259f);
    static Vector3 PlanetLight(Vector3 sun) =>
        Planetshine.Light(sun, Moon, Albedo, Planetshine.Luminance(KenshiLighting.SunLight(Vector3.UnitY)), Enhancements.MeitouPlanetshineStrength);

    [Theory]
    [InlineData(-1)]   // dusk: the sun sets at -X
    [InlineData(1)]    // dawn: it rises at +X
    public void The_far_haze_never_gets_brighter_than_it_was_at_the_horizon_through_twilight(int side)
    {
        // The far land fades into the sunlit haze colour plus the night air. The sun crosses the horizon in 0.02° steps from 10° up to 40° down:
        // looking along the sun's azimuth and away from it, the haze's target colour (KenshiHaze.Colour at 0.6 D plus the air at the horizon) must
        // not exceed its brightness at the moment the sun is on the horizon (no brighter-than-dusk moment as the air comes in), and must not jump.
        const float far = 30000;
        foreach (float towards in new[] { 1f, -1f })
        {
            float Brightness(float degrees, out Vector3 sun)
            {
                float a = degrees * MathF.PI / 180;
                sun = Vector3.Normalize(new Vector3(side * MathF.Cos(a), -MathF.Sin(a), -0.2f * MathF.Sin(a)));
                var offset = new Vector3(side * towards * far, 0, 0);
                var haze = KenshiHaze.Colour(offset, sun);
                var air = NightAir.Radiance(PlanetLight(sun), sun.Y, 0);
                return Planetshine.Luminance(haze + air);
            }
            float atHorizon = Brightness(0, out _), last = Brightness(-10, out _), worstJump = 0;
            for (float degrees = -9.98f; degrees <= 40; degrees += 0.02f)
            {
                float b = Brightness(degrees, out _);
                if (degrees >= 0) Assert.True(b <= atHorizon * 1.02f + 1e-6f,
                        $"{degrees}° down, towards {towards}: {b} against {atHorizon} at the horizon");
                worstJump = MathF.Max(worstJump, MathF.Abs(b - last) / MathF.Max(MathF.Max(b, last), 1e-5f));
                last = b;
            }
            Assert.True(worstJump < 0.1f, $"towards {towards}: a step of {worstJump:P1}");
        }
    }
}
