using System.Numerics;
using Meitou.Content;
using Meitou.Data.Textures;
using Meitou.Data.World;
using Meitou.Rendering;

namespace Meitou.Tests.World;

/// <summary>Meitou's planetshine (docs/formats/sky.md "Planetshine"): the phase angle and Lambert phase, the planet's irradiance, the twilight blend.</summary>
public class PlanetshineTests
{
    static readonly SkyPlanet Moon = SkyPlanet.All[0];

    [Fact]
    public void Phase_angle_is_zero_with_the_sun_behind_the_observer_and_pi_behind_the_planet()
    {
        var p = Moon.Towards;
        Assert.Equal(0, Planetshine.PhaseAngle(-p, p), 3);
        Assert.Equal(MathF.PI, Planetshine.PhaseAngle(p, p), 3);
        var side = Vector3.Normalize(Vector3.Cross(p, Vector3.UnitY));
        Assert.Equal(MathF.PI / 2, Planetshine.PhaseAngle(side, p), 3);
        Assert.Equal(MathF.PI / 2, Planetshine.PhaseAngle(-side, p), 3);
    }

    [Fact]
    public void Lambert_phase_runs_from_full_to_new()
    {
        Assert.Equal(1, Planetshine.LambertPhase(0), 5);
        Assert.Equal(0, Planetshine.LambertPhase(MathF.PI), 5);
        Assert.Equal(1 / MathF.PI, Planetshine.LambertPhase(MathF.PI / 2), 5);   // a quarter: sin 90° / π
        float last = 2;
        for (int degrees = 0; degrees <= 180; degrees += 5)
        {
            float phase = Planetshine.LambertPhase(degrees * MathF.PI / 180);
            Assert.True(phase < last, $"{degrees}°");
            last = phase;
        }
    }

    [Fact]
    public void The_big_planet_sends_the_land_under_a_percent_of_the_sun()
    {
        // 2π (1 − cos 9.09°) = 0.079 sr, so a full planet of albedo 0.3 sends 0.3 · 0.079 / π = 0.75 % of the sun's irradiance.
        float omega = Planetshine.SolidAngle(Moon.AngularRadius);
        Assert.InRange(omega, 0.078f, 0.080f);
        Assert.Equal(0.0075f, Planetshine.IrradianceRatio(0.3f, omega, 0), 4);
        // The small planet's disc is an eighth of it (why the viewer leaves it out).
        Assert.InRange(omega / Planetshine.SolidAngle(SkyPlanet.All[1].AngularRadius), 11.5f, 12.5f);
    }

    [Fact]
    public void The_planet_is_fullest_after_dusk_and_a_crescent_by_dawn()
    {
        // The sun sets at -X and rises at +X (SkyClock), the planet is at +X: opposite the setting sun, beside the rising one.
        var dusk = Vector3.Normalize(new Vector3(-1, -0.1f, 0));
        var dawn = Vector3.Normalize(new Vector3(1, -0.1f, 0));
        float atDusk = Planetshine.IrradianceRatio(1, 1, Planetshine.PhaseAngle(dusk, Moon.Towards));
        float atDawn = Planetshine.IrradianceRatio(1, 1, Planetshine.PhaseAngle(dawn, Moon.Towards));
        Assert.InRange(Planetshine.PhaseAngle(dusk, Moon.Towards) * 180 / MathF.PI, 40, 55);
        Assert.InRange(Planetshine.PhaseAngle(dawn, Moon.Towards) * 180 / MathF.PI, 125, 145);
        Assert.True(atDusk > 10 * atDawn, $"{atDusk} against {atDawn}");
    }

    [Fact]
    public void Light_scales_with_the_sun_light_the_albedo_and_the_strength()
    {
        var sun = Vector3.Normalize(new Vector3(-1, -0.3f, 0));
        var a = Planetshine.Light(sun, Moon, new Vector3(0.2f, 0.4f, 0.6f), 0.3f, 1);
        var b = Planetshine.Light(sun, Moon, new Vector3(0.2f, 0.4f, 0.6f), 0.6f, 5);
        Assert.Equal(a.X * 10, b.X, 5);
        Assert.Equal(a.Z * 10, b.Z, 5);
        Assert.Equal(2, a.Y / a.X, 4);
        Assert.Equal(3, a.Z / a.X, 4);
    }

    [Fact]
    public void Weight_is_one_when_the_suns_light_ends_and_nothing_by_day()
    {
        Assert.Equal(1, Planetshine.Weight(-0.5f));
        Assert.Equal(1, Planetshine.Weight(Planetshine.FullBelow));
        Assert.Equal(0, Planetshine.Weight(Planetshine.NoneAbove));
        Assert.Equal(0, Planetshine.Weight(0.8f));
        // The game's sun light is out where the planet's weight is full.
        Assert.True(KenshiLighting.Daylight(Planetshine.FullBelow) < 0.02f);
        float last = 1;
        for (float y = Planetshine.FullBelow; y <= Planetshine.NoneAbove; y += 0.005f)
        {
            float w = Planetshine.Weight(y);
            Assert.InRange(w, 0, last + 1e-6f);
            last = w;
        }
    }

    [Fact]
    public void Combine_follows_the_brighter_light()
    {
        var sunDir = Vector3.UnitX;
        var planetDir = Vector3.UnitZ;
        var sun = new Vector3(0.3f, 0.3f, 0.3f);
        // No planet light: the sun's, untouched (Faithful by day).
        Assert.Equal((sunDir, sun), Planetshine.Combine(sunDir, sun, planetDir, Vector3.Zero));
        // No sun light: the planet's.
        var planet = new Vector3(0.01f, 0.02f, 0.03f);
        var (d, l) = Planetshine.Combine(sunDir, Vector3.Zero, planetDir, planet);
        Assert.Equal(planetDir.Z, d.Z, 5);
        Assert.Equal(planet, l);
        // Equal luminance: half way, the colours added.
        var (mid, both) = Planetshine.Combine(sunDir, new Vector3(1), planetDir, new Vector3(1));
        Assert.Equal(MathF.Sqrt(0.5f), mid.X, 4);
        Assert.Equal(MathF.Sqrt(0.5f), mid.Z, 4);
        Assert.Equal(new Vector3(2), both);
    }

    [Theory]
    [InlineData(-1)]   // dusk: the sun sets at -X
    [InlineData(1)]    // dawn: it rises at +X
    public void The_light_through_dusk_and_dawn_changes_smoothly_and_turns_in_the_dark(int side)
    {
        // The sun crosses the horizon at 0.05° steps from 20° up to 40° below, with the planet at the game's strength: neither the light's direction
        // nor its brightness may jump (no pop where the planet takes over from the sun, the shadows follow the direction), and the direction's
        // quickest turn comes while the light is dim, a fifth of the noon sun's or less (deep twilight, the sun a few degrees down), not in the sunset.
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
            worstJump = MathF.Max(worstJump, MathF.Abs(now.Brightness - last.Brightness) / MathF.Max(MathF.Max(now.Brightness, last.Brightness), 1e-6f));
            last = now;
        }
        Assert.True(worstAngle < 5f, $"direction step {worstAngle}°");
        Assert.True(worstJump < 0.1f, $"brightness step {worstJump:P1}");
        Assert.True(brightnessAtWorstAngle < 0.2f, $"the quickest turn is at {brightnessAtWorstAngle:P0} of the reference sun light");
    }

    [Fact]
    public void Mean_albedo_weights_the_rows_by_the_sphere_s_area()
    {
        // 4 rows of an equirectangular map: the polar rows (white) weigh sin(22.5°) each, the equatorial ones (black) sin(67.5°).
        var pixels = new byte[4 * 2 * 4];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 2; x++)
                for (int c = 0; c < 4; c++)
                    pixels[(y * 2 + x) * 4 + c] = y is 0 or 3 ? (byte)255 : (byte)0;
        var mean = Planetshine.MeanAlbedo(new RgbaImage(2, 4, pixels));
        float expected = 2 * MathF.Sin(MathF.PI / 8) / (2 * MathF.Sin(MathF.PI / 8) + 2 * MathF.Sin(3 * MathF.PI / 8));
        Assert.Equal(expected, mean.X, 4);
        Assert.Equal(mean.X, mean.Y);
        Assert.Equal(mean.X, mean.Z);
        // The plain average would have been a half.
        Assert.True(mean.X < 0.3f);
    }

    [Fact]
    [Slow]
    public void The_big_planet_is_a_grey_world_of_middling_albedo()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var dds = DdsReader.ReadFile(Path.Combine(install!.DataDirectory, "materials", Moon.Texture));
        var mean = Planetshine.MeanAlbedo(dds);
        Assert.InRange(mean.X, 0.1f, 0.6f);
        Assert.InRange(mean.Y, 0.1f, 0.6f);
        Assert.InRange(mean.Z, 0.1f, 0.6f);
    }
}
