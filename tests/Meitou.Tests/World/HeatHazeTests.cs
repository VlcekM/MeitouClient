using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Textures;
using Meitou.Data.World;

namespace Meitou.Tests.World;

/// <summary>The heat haze's amount and inputs (docs/formats/weather.md and post-processing.md "Heat haze").</summary>
public class HeatHazeTests
{
    [Fact]
    public void Target_is_field_times_strength_times_the_sun_factor()
    {
        Assert.Equal(0.6f, HeatHaze.Target(0.6f, 1, 0.9f), 5);          // full from sunY 1/6 (about 10 degrees)
        Assert.Equal(0.3f, HeatHaze.Target(0.6f, 1, 1f / 12), 5);       // half way up the ramp
        Assert.Equal(0.42f, HeatHaze.Target(0.6f, 0.7f, 0.5f), 5);      // the weather strength scales it
        Assert.Equal(0f, HeatHaze.Target(1, 1, -0.2f));                 // none at night
    }

    [Fact]
    public void Step_moves_a_third_per_second()
    {
        Assert.Equal(1f / 3, HeatHaze.Step(0, 1, 1), 5);
        Assert.Equal(1f, HeatHaze.Step(0.9f, 1, 1), 5);                 // lands on the target when closer than a step
        Assert.Equal(0.5f - 0.1f / 3, HeatHaze.Step(0.5f, 0, 0.1f), 5);
        // The animation's time base: 0.01 game hours per cycle, 1.09 real seconds at game speed 1.
        Assert.Equal(1.0909, 0.01 / HeatHaze.HoursPerSecond, 3);
    }

    [Fact]
    [Slow]
    public void Base_game_weathers_and_textures()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        Assert.Equal(0f, SkyWeather.Find(db, null)!.HeatHaze);
        Assert.Equal(0.6f, SkyWeather.Find(db, "Desert Calm hot 0.6")!.HeatHaze, 4);
        Assert.Equal(1f, SkyWeather.Find(db, "venge beams")!.HeatHaze, 4);
        Assert.Equal(19, db.OfType(FcsRecordType.WEATHER).Count(w => w.GetFloat("heat haze") > 0));
        // The shader's two maps: 2048² BC1 with a full mip chain.
        foreach (var name in new[] { "FlowHAZE.dds", "Perturber.dds" })
        {
            var dds = DdsReader.ReadFile(Path.Combine(install!.DataDirectory, "materials", name));
            Assert.Equal((2048, 2048, 12, DdsFormat.Bc1), (dds.Width, dds.Height, dds.MipCount, dds.Format));
        }
    }
}
