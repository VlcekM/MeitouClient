using System.Numerics;
using Meitou.Data.World;
using Meitou.Rendering;

namespace Meitou.Tests.World;

/// <summary>Meitou's night air (docs/formats/sky.md "Night air"): the glow the haze fades the far land into at night and the sky shows above the horizon, and the water's planet glint.</summary>
public class NightAirTests
{
    static readonly SkyPlanet Moon = SkyPlanet.All[0];
    static readonly Vector3 Albedo = new(0.284f, 0.293f, 0.259f);
    /// <summary>The game's clock (latitude 54, sunrise 5, sunset 23), as docs/formats/sky.md "Phase through the night".</summary>
    static readonly SkyClock Game = new(54, 5, 23);

    static Vector3 PlanetLight(Vector3 sun) =>
        Planetshine.Light(sun, Moon, Albedo, Planetshine.Luminance(KenshiLighting.SunLight(Vector3.UnitY)), Enhancements.MeitouPlanetshineStrength);

    [Fact]
    public void Without_the_planet_there_is_the_airglow_and_the_planet_adds_to_it()
    {
        Assert.Equal(NightAir.Airglow, NightAir.Colour(Vector3.Zero));
        var gibbous = NightAir.Colour(new Vector3(0.09f));
        Assert.True(gibbous.X > NightAir.Airglow.X && gibbous.Y > NightAir.Airglow.Y && gibbous.Z > NightAir.Airglow.Z);
        // The air is blue: the planet's light gains most in blue, least in red.
        Assert.True(gibbous.Z - NightAir.Airglow.Z > gibbous.X - NightAir.Airglow.X);
    }

    [Fact]
    public void A_thin_crescent_leaves_a_darker_air_than_a_gibbous_planet()
    {
        // The planet at +X, the sun setting at -X (docs/formats/sky.md "Phase through the night"): gibbous at 23:30, a thin crescent by 4:00.
        var clock = Game;
        float Luminance(float hour) => Planetshine.Luminance(NightAir.Colour(PlanetLight(clock.SunDirection(hour))));
        float dusk = Luminance(23.5f), midnight = Luminance(0), late = Luminance(4);   // 23:30, midnight, 4:00 (the crescent)
        Assert.True(dusk > midnight && midnight > late, $"{dusk} {midnight} {late}");
        float glow = Planetshine.Luminance(NightAir.Airglow);
        Assert.InRange(late / glow, 1f, 1.05f);       // by 4:00 the crescent adds almost nothing
        Assert.InRange(dusk / glow, 1.4f, 1.6f);      // at the brightest the planet adds about half the airglow again
    }

    [Fact]
    public void The_night_air_is_darker_than_the_land_the_planet_and_ambient_light_near_the_camera()
    {
        // An up-facing surface of albedo 0.3 under the game's night ambient (docs/formats/sky.md: 0.96 · 1.2 · 0.2 on a white surface), the planet's light not counted.
        float land = 0.3f * 0.23f;
        var brightest = NightAir.Colour(PlanetLight(Game.SunDirection(23.5f)));
        Assert.True(Planetshine.Luminance(brightest) < land, $"air {Planetshine.Luminance(brightest)} against land {land}");
        Assert.True(Planetshine.Luminance(NightAir.Airglow) > 0.05f * land);   // and it is there
    }

    [Fact]
    public void The_night_air_comes_in_through_deep_twilight_only()
    {
        Assert.Equal(0, NightAir.Weight(0.3f));
        Assert.Equal(0, NightAir.Weight(0));
        Assert.Equal(0, NightAir.Weight(Planetshine.NoneAbove));
        Assert.Equal(1, NightAir.Weight(Planetshine.FullBelow));
        Assert.Equal(1, NightAir.Weight(-1));
        float last = -1;
        for (float y = 0.05f; y >= -0.15f; y -= 0.001f)
        {
            float w = NightAir.Weight(y);
            Assert.True(w >= last, $"sunY {y}");
            last = w;
        }
    }

    [Fact]
    public void The_glow_falls_off_with_the_height_and_is_all_of_it_at_and_below_the_horizon()
    {
        Assert.Equal(1, NightAir.HorizonFalloff(0));
        Assert.Equal(1, NightAir.HorizonFalloff(-0.4f));
        float last = 1;
        for (float y = 0.01f; y <= 1; y += 0.01f)
        {
            float f = NightAir.HorizonFalloff(y);
            Assert.True(f < last, $"y {y}");
            last = f;
        }
        float At(float degrees) => NightAir.HorizonFalloff(MathF.Sin(degrees * MathF.PI / 180));
        Assert.InRange(At(10), 0.35f, 0.5f);    // still a glow 10° up
        Assert.True(At(30) < 0.1f);              // nearly gone by 30°
        Assert.True(At(90) < 0.01f);             // the zenith's stars keep their black
    }

    [Fact]
    public void Radiance_is_the_one_function_the_haze_and_the_sky_both_add()
    {
        var planet = new Vector3(0.05f);
        var air = NightAir.Colour(planet);
        Assert.Equal(Vector3.Zero, NightAir.Radiance(planet, 0.1f, 0.3f));                                       // by day: nothing
        Assert.Equal(air, NightAir.Radiance(planet, -0.5f, 0));                                                  // night, the horizon: all of it
        Assert.Equal(air * NightAir.HorizonFalloff(0.2f), NightAir.Radiance(planet, -0.5f, 0.2f));
        Assert.Equal(NightAir.Radiance(planet, -0.5f, 0), NightAir.Radiance(planet, -0.5f, -0.3f));              // below the horizon the horizon's
    }

    [Theory]
    [InlineData(-1)]   // dusk: the sun sets at -X
    [InlineData(1)]    // dawn: it rises at +X
    public void The_far_haze_never_gets_brighter_than_it_was_at_the_horizon_through_twilight(int side)
    {
        // The far land fades into the sunlit haze colour plus the night air. The sun crosses the horizon in 0.02° steps from 10° up to 40° down: looking along the
        // sun's azimuth and away from it, the haze's target colour (KenshiHaze.Colour at 0.6 D plus the air at the horizon) must not exceed its brightness at the
        // moment the sun is on the horizon (no brighter-than-dusk moment as the air comes in), and must not jump.
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
                if (degrees >= 0) Assert.True(b <= atHorizon * 1.02f + 1e-6f, $"{degrees}° down, towards {towards}: {b} against {atHorizon} at the horizon");
                worstJump = MathF.Max(worstJump, MathF.Abs(b - last) / MathF.Max(MathF.Max(b, last), 1e-5f));
                last = b;
            }
            Assert.True(worstJump < 0.1f, $"towards {towards}: a step of {worstJump:P1}");
        }
    }

    [Fact]
    public void The_disc_radiance_is_the_albedo_times_the_lambert_phase_function()
    {
        var p = Moon.Towards;
        Assert.Equal(Albedo, Planetshine.DiscRadiance(-p, Moon, Albedo));          // the sun behind the observer: a full disc
        Assert.Equal(Vector3.Zero, Planetshine.DiscRadiance(p, Moon, Albedo));      // behind the planet: new
        var side = Vector3.Normalize(Vector3.Cross(p, Vector3.UnitY));
        Assert.Equal(Albedo.Y / MathF.PI, Planetshine.DiscRadiance(side, Moon, Albedo).Y, 5);   // a quarter: a third of a full disc, not dark
        Assert.Equal(Albedo.X * Planetshine.LambertPhase(MathF.PI / 4), Planetshine.DiscRadiance(Vector3.Normalize(side - p), Moon, Albedo).X, 4);
    }

    [Fact]
    public void A_lighting_without_a_glint_light_glints_with_the_sun()
    {
        var sun = Vector3.Normalize(new Vector3(0.2f, 0.8f, 0.1f));
        var colour = new Vector3(1, 0.9f, 0.8f);
        var plain = new WorldLighting(sun, colour, Vector3.One, Vector3.One, Vector3.One, 1000);
        Assert.Equal((sun, colour), plain.Glint);
        var glint = plain with { GlintDirection = Vector3.UnitX, GlintColour = new Vector3(0.1f) };
        Assert.Equal((Vector3.UnitX, new Vector3(0.1f)), glint.Glint);
        // A glint light with no colour (a new planet) still counts as given: no glint, not the sun's.
        Assert.Equal((Vector3.UnitX, Vector3.Zero), (plain with { GlintDirection = Vector3.UnitX }).Glint);
    }
}
