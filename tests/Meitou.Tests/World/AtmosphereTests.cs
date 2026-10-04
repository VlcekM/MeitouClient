using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class AtmosphereTests
{
    static readonly AtmosphereModel Model = new(new AtmosphereSettings());
    static readonly SkyClock Clock = new(54, 5, 23);
    const float EyeHeight = 100f / 40000f;   // 100 world units at the default scale height

    [Fact]
    public void Skyx_constants_give_its_shader_parameters()
    {
        // data/materials/SkyX: uScale = 1 / (outer - inner), uScaleDepth = (outer - inner) / 2.
        Assert.Equal(0.5213f, SkyAtmosphere.Thickness, 3);
        Assert.Equal(SkyAtmosphere.Thickness / 2, SkyAtmosphere.ScaleDepth, 6);
        Assert.Equal(7.359f, 1 / SkyAtmosphere.ScaleHeight, 2);   // uScaleOverScaleDepth
        // A vertical ray's Rayleigh depth: Kr · 4π · invλ⁴ · scaleDepth, bluest in the shortest wavelength.
        var tau = SkyAtmosphere.RayleighZenithDepth;
        Assert.Equal(0.0683f, tau.X, 3);
        Assert.True(tau.Z > tau.Y && tau.Y > tau.X);
    }

    [Fact]
    public void Sunlight_reddens_towards_the_horizon_and_is_blocked_below_it()
    {
        var noon = Model.SunLight(EyeHeight, Clock.SunDirection(14));
        var low = Model.SunLight(EyeHeight, Clock.SunDirection(22.5f));
        Assert.True(noon.Z / noon.X > low.Z / low.X, "the low sun is redder");
        Assert.True(noon.X > low.X && low.X > 0);
        Assert.Equal(Vector3.Zero, Model.SunLight(EyeHeight, Clock.SunDirection(1)));   // night
    }

    [Fact]
    public void Daytime_zenith_is_bluer_than_the_horizon_and_brighter_by_day()
    {
        var sun = Clock.SunDirection(14);
        var zenith = Model.SkyRadiance(EyeHeight, Vector3.UnitY, sun);
        var away = Vector3.Normalize(new Vector3(-sun.X, 0, -sun.Z));
        var horizon = Model.SkyRadiance(EyeHeight, Vector3.Normalize(away + new Vector3(0, 0.03f, 0)), sun);
        Assert.True(zenith.Z / zenith.X > horizon.Z / horizon.X, "blue/red ratio is higher at the zenith");
        Assert.True(horizon.Y > zenith.Y, "the horizon is brighter (longer path)");
        var night = Model.SkyRadiance(EyeHeight, Vector3.UnitY, Clock.SunDirection(1));
        Assert.True(night.Length() < zenith.Length() * 0.05f);
    }

    [Fact]
    public void The_sky_is_brighter_towards_the_sun()
    {
        var sun = Clock.SunDirection(22.5f);
        var towards = Vector3.Normalize(new Vector3(sun.X, 0.1f, sun.Z));
        var away = Vector3.Normalize(new Vector3(-sun.X, 0.1f, -sun.Z));
        Assert.True(Model.SkyRadiance(EyeHeight, towards, sun).Length() > 2 * Model.SkyRadiance(EyeHeight, away, sun).Length());
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
        var near = KenshiHaze.SkyFraction(dir * 5000, sun);
        var mid = KenshiHaze.SkyFraction(dir * 30000, sun);
        var dome = KenshiHaze.SkyFraction(dir * KenshiHaze.DomeRadius, sun);
        var beyond = KenshiHaze.SkyFraction(dir * 300000, sun);
        Assert.True(near.Y < mid.Y && mid.Y < dome.Y, "more in-scattered light the longer the path");
        Assert.Equal(1, dome.Y, 3);
        Assert.Equal(dome, beyond);   // the path is capped at one dome radius
        Assert.True(mid.Y > 0.3f && mid.Y < 0.9f);
        // Less extinction on the short path: relatively bluer than the full sky.
        Assert.True(mid.Z > mid.X);
        // Night: both integrals vanish; the fraction stays finite (0, a black haze like the game's).
        var night = KenshiHaze.SkyFraction(dir * 30000, Clock.SunDirection(1));
        Assert.True(float.IsFinite(night.X) && night.X >= 0 && night.X <= 1);
    }

    [Fact]
    public void Kenshi_haze_lifts_close_points_below_the_eye_and_clamps_steep_rays()
    {
        // A point straight below: the ray is replaced by the fixed (0, -0.3, 0.953).
        var (steep, _) = KenshiHaze.Ray(new Vector3(0, -20000, 1));
        Assert.Equal(new Vector3(0, -0.3f, 0.953f), steep);
        // A close point below the eye is lifted to the eye's level: the ray only dips by the fog eye's own height.
        var (low, len) = KenshiHaze.Ray(new Vector3(7000, -2000, 0));
        Assert.True(low.Y < 0 && low.Y > -0.06f);
        Assert.Equal(0.1f, len, 2);
        // No geometry clouds pull under a clear sky; full pull from cloud density ~0.93.
        Assert.Equal(0, KenshiHaze.CloudPull(0));
        Assert.Equal(1, KenshiHaze.CloudPull(1));
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
}
