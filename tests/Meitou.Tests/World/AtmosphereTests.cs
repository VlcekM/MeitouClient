using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Textures;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class AtmosphereTests
{
    static readonly SkyClock Clock = new(54, 5, 23);

    [Fact]
    public void Skyx_constants_give_its_shader_parameters()
    {
        // data/materials/SkyX: uScale = 1 / (outer - inner), uScaleDepth = (outer - inner) / 2.
        Assert.Equal(0.5213f, SkyAtmosphere.Thickness, 3);
        Assert.Equal(SkyAtmosphere.Thickness / 2, SkyAtmosphere.ScaleDepth, 6);
        Assert.Equal(7.359f, 1 / SkyAtmosphere.ScaleHeight, 2);   // uScaleOverScaleDepth
        // The game's wavelengths (0.57, 0.48, 0.44): 1/λ⁴ = (9.47, 18.84, 26.68), so green scatters twice as much as red.
        var inv = SkyAtmosphere.InverseWaveLength4;
        Assert.Equal(9.473f, inv.X, 2);
        Assert.Equal(18.838f, inv.Y, 2);
        Assert.Equal(26.680f, inv.Z, 2);
        Assert.Equal(0.0683f, SkyAtmosphere.RayleighZenithDepth.X, 3);
        Assert.Equal(1.4f, SkyAtmosphere.Exposure);
        Assert.Equal(9.77501f + 0.01f * 0.5213f, SkyAtmosphere.CameraY, 4);
    }

    [Fact]
    public void The_day_sky_is_teal_blue_and_brightens_towards_the_horizon()
    {
        var sun = Clock.SunDirection(14);
        var away = Vector3.Normalize(new Vector3(-sun.X, 0, -sun.Z + 1e-4f));
        var zenith = SkyXModel.Colour(Vector3.UnitY, sun);
        var mid = SkyXModel.Colour(away + new Vector3(0, 0.364f, 0), sun);
        var horizon = SkyXModel.Colour(away + new Vector3(0, 0.02f, 0), sun);
        // Blue strongest, green close behind (no violet: red is well under green), as the game's screenshots show.
        foreach (var c in new[] { zenith, mid })
        {
            Assert.True(c.Z > c.Y && c.Y > c.X, $"{c}");
            Assert.True(c.Y > 1.5f * c.X, $"green well above red: {c}");
        }
        Assert.True(horizon.Y > 2 * zenith.Y, "the horizon is brighter (longer path)");
        Assert.True(zenith.Z / zenith.X > horizon.Z / horizon.X, "and paler");
        // Night: the scattered light vanishes; only the faint glow is left.
        var night = SkyXModel.Colour(Vector3.UnitY, Clock.SunDirection(1));
        Assert.True(night.Length() < 0.01f, $"{night}");
    }

    [Fact]
    public void Mie_lobe_glows_round_the_sun()
    {
        var sun = Clock.SunDirection(22.3f);
        var at = SkyXModel.Colour(sun + new Vector3(0, 0, 0.04f), sun);
        var side = SkyXModel.Colour(Vector3.Normalize(new Vector3(sun.Z, 0.15f, -sun.X)), sun);
        Assert.True(at.X > 3 * side.X, $"towards the sun {at}, to the side {side}");
        // SkyX's g is -0.991 with the cosine taken towards the eye: the lobe faces the sun, so the phase is large for cos = -1.
        Assert.True(SkyXModel.MiePhase(-0.999f) > 100 * SkyXModel.MiePhase(0.999f));
    }

    [Fact]
    public void Sun_colour_and_daylight_scale_follow_the_sky_controller()
    {
        // kenshi_x64.exe sky update: sunColour.rgb = getColorAt(sun + (0, 0, 0.04)) / (4 · 1.4), black under y = -0.2.
        var noon = Clock.SunDirection(14);
        var expected = SkyXModel.Colour(noon + new Vector3(0, 0, 0.04f), noon, skydome: false) / 5.6f;
        Assert.Equal(expected, KenshiLighting.SunColour(noon));
        var low = KenshiLighting.SunColour(Clock.SunDirection(22.3f));
        var high = KenshiLighting.SunColour(noon);
        Assert.True(low.Z / low.X < high.Z / high.X, "the low sun is redder");
        Assert.Equal(Vector3.Zero, KenshiLighting.SunColour(new Vector3(0.97f, -0.25f, 0)));
        // sunColour.w: 0.5 at the horizon, 1 at the zenith, gone 0.093 below.
        Assert.Equal(0.5f, KenshiLighting.Daylight(0), 5);
        Assert.Equal(1f, KenshiLighting.Daylight(1), 5);
        Assert.Equal(0.75f, KenshiLighting.Daylight(0.5f), 5);
        Assert.Equal(0, KenshiLighting.Daylight(-0.0926f), 2);
        Assert.Equal(0f, KenshiLighting.Daylight(-0.2f));
        // The lighting direction lies on the horizon at night; the environment light bottoms out at 0.2.
        var night = KenshiLighting.LightDirection(Clock.SunDirection(1));
        Assert.Equal(0, night.Y);
        Assert.Equal(1, night.Length(), 5);
        Assert.Equal(0.2f, KenshiLighting.EnvironmentFactor(night), 5);
        Assert.Equal(1f, KenshiLighting.EnvironmentFactor(Vector3.UnitY));
    }

    [Fact]
    public void Exposure_band_opens_at_night()
    {
        // MIN_LUMINANCE = exposure min · lerp(night darkness, 1, saturate(5 sunY)); scale = 0.55 / clamp(mean, min, max).
        Assert.Equal(0.8f, KenshiLighting.MinLuminance(0.5f, 0.8f, 0.35f), 5);
        Assert.Equal(0.28f, KenshiLighting.MinLuminance(-0.3f, 0.8f, 0.35f), 5);
        Assert.Equal(0.55f / 0.8f, KenshiLighting.ExposureScale(0.1f, 0.8f, 1.2f), 5);
        Assert.Equal(0.55f / 1.2f, KenshiLighting.ExposureScale(3f, 0.8f, 1.2f), 5);
        Assert.Equal(0.55f / 0.28f, KenshiLighting.ExposureScale(0.05f, 0.28f, 1.2f), 5);
    }

    [Fact]
    public void Kenshi_haze_ramps_between_the_games_fractions_of_its_far_distance()
    {
        // kenshi_x64.exe: D = view distance x 10, pFogParams = (D, 0.06 D, min(D, 0.6 D)); settings.cfg view distance 5000.
        float d = KenshiHaze.FarDistance(KenshiHaze.ViewDistanceSetting);
        Assert.Equal(50000, d);
        Assert.Equal(3000, d * KenshiHaze.StartFraction, 1);
        Assert.Equal(30000, d * KenshiHaze.EndFraction, 1);
    }

    [Fact]
    public void Kenshi_haze_colour_grows_with_distance_to_the_skys_at_the_dome_radius()
    {
        var sun = Clock.SunDirection(13);
        var dir = Vector3.Normalize(new Vector3(-sun.X, 0, -sun.Z));
        var near = KenshiHaze.Colour(dir * 5000, sun);
        var mid = KenshiHaze.Colour(dir * 30000, sun);
        var dome = KenshiHaze.Colour(dir * KenshiHaze.DomeRadius, sun);
        var beyond = KenshiHaze.Colour(dir * 300000, sun);
        Assert.True(near.Y < mid.Y && mid.Y < dome.Y, "more in-scattered light the longer the path");
        Assert.True((dome - beyond).Length() < 0.02f * dome.Length(), $"{dome} {beyond}");   // the path is capped at one dome radius (the direction still shifts a little)
        // At the dome radius the haze is the sky's Rayleigh light just above the horizon (same integral, same eye).
        var (ray, _, _) = KenshiHaze.Ray(dir * KenshiHaze.DomeRadius);
        var sky = SkyXModel.InScatter(ray, 1, sun) * SkyAtmosphere.InverseWaveLength4 * (SkyAtmosphere.KrESun * SkyXModel.RayleighPhase(Vector3.Dot(sun, ray)) * SkyAtmosphere.Exposure);
        Assert.Equal(sky.Y, dome.Y, 3);
        // Night: the integral vanishes, the haze is black.
        var night = KenshiHaze.Colour(dir * 30000, Clock.SunDirection(1));
        Assert.True(night.Length() < 1e-3f);
    }

    [Fact]
    public void Kenshi_haze_lifts_close_points_below_the_eye_and_clamps_steep_rays()
    {
        // A point straight below: the ray is replaced by the fixed (0, -0.3, 0.953).
        var (steep, _, _) = KenshiHaze.Ray(new Vector3(0, -20000, 1));
        Assert.Equal(new Vector3(0, -0.3f, 0.953f), steep);
        // A close point below the eye is lifted to the eye's level: the ray only dips by the fog eye's own height.
        var (low, len, _) = KenshiHaze.Ray(new Vector3(7000, -2000, 0));
        Assert.True(low.Y < 0 && low.Y > -0.06f);
        Assert.Equal(0.1f, len, 2);
        // No geometry clouds pull under a clear sky; full pull from cloud density ~0.93.
        Assert.Equal(0, KenshiHaze.CloudPull(0));
        Assert.Equal(1, KenshiHaze.CloudPull(1));
    }

    [Fact]
    public void Kenshi_haze_stays_bounded_from_far_above_the_games_camera_heights()
    {
        // Within the game's camera heights (at most 1840 above the pivot) the haze colour stays near the sky's own (blue at most ~1.3).
        // From far higher up, long steep rays drive the fit's optical depth strongly negative; without the viewer's floor the colour
        // reached thousands (the white arc of an overview shot). With it the colour stays finite and modest.
        float worstGame = 0, worstHigh = 0;
        foreach (float hour in new[] { 6f, 9, 13, 18, 22.6f })
        {
            var sun = Clock.SunDirection(hour);
            for (float d = 1000; d < 450000; d *= 1.3f)
            {
                worstGame = MathF.Max(worstGame, KenshiHaze.Colour(new Vector3(0, -KenshiCamera.MaxHeightAbovePivot, d), sun).Z);
                var high = KenshiHaze.Colour(new Vector3(0, -50000, d), sun);
                Assert.True(float.IsFinite(high.X) && float.IsFinite(high.Y) && float.IsFinite(high.Z));
                worstHigh = MathF.Max(worstHigh, high.Z);
            }
        }
        Assert.InRange(worstGame, 0.5f, 1.4f);
        Assert.InRange(worstHigh, 0, 40);
        Assert.Equal(1840, KenshiCamera.MaxHeightAbovePivot, 1);
    }

    [Fact]
    public void Ambient_map_built_from_the_biomes_matches_the_shipped_one()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = GameDatabase.Load(LoadOrder.FromInstall(install!));
        var shipped = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install!.DataDirectory, "newland/land/overlaymaps/ambientmap.png")));
        var biomes = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install.DataDirectory, TerrainMaps.BiomeMap)));
        var built = AmbientMap.Build(db, biomes);
        Assert.Equal(shipped.Width, built.Width);
        int differ = 0;
        for (int i = 0; i < built.Pixels.Length; i += 4)
            if (!built.Pixels.AsSpan(i, 4).SequenceEqual(shipped.Pixels.AsSpan(i, 4))) differ++;
        Assert.Equal(0, differ);
        // Sun brightness 1 -> alpha 127, 0.38 (Ashlands) -> 48.
        Assert.Contains(built.Pixels.Where((_, i) => i % 4 == 3), a => a == 127);
        Assert.Contains(built.Pixels.Where((_, i) => i % 4 == 3), a => a == 48);
    }

    [Fact]
    public void Base_game_default_weather_is_clear()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var weather = SkyWeather.Find(db, null)!;
        Assert.Equal("Default", weather.Name);
        Assert.False(weather.FogEnabled);
        Assert.Equal(0f, weather.CloudDensity);
        Assert.Equal(Vector3.One, weather.SkyColourMultiplier);
        // A dust storm: fog on, a sand colour (0xE9CB9E), and an orange sky multiplier.
        var storm = SkyWeather.Find(db, "Dust Storm Approach")!;
        Assert.True(storm.FogEnabled);
        Assert.Equal(7000f, storm.FogMin);
        Assert.Equal(25000f, storm.FogMax);
        Assert.Equal(0xE9 / 255f, storm.FogColour.X, 4);
        Assert.True(storm.SkyColourMultiplier.Z < storm.SkyColourMultiplier.X);
        Assert.Equal(0.35f, SkyWeather.NightDarkness(db), 3);
    }

    [Fact]
    public void Merged_constants_give_the_exposure_band()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var e = ExposureConstants.FromDatabase(GameDatabase.Load(LoadOrder.FromInstall(install!)));
        Assert.Equal(0.8f, e.Min, 3);
        Assert.Equal(1.2f, e.Max, 3);
        Assert.Equal(0.35f, e.NightDarkness, 3);
    }
}
