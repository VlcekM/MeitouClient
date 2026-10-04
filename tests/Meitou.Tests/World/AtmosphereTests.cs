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
