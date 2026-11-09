using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Textures;
using Meitou.Data.World;

namespace Meitou.Tests.World;

/// <summary>
/// The heat haze's amount and inputs (docs/formats/weather.md and post-processing.md "Heat haze").
/// </summary>
public class HeatHazeTests
{
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
        Assert.Equal(19, db.OfType(FcsRecordType.Weather).Count(w => w.GetFloat("heat haze") > 0));
        // The shader's two maps: 2048² BC1 with a full mip chain.
        foreach (var name in new[] { "FlowHAZE.dds", "Perturber.dds" })
        {
            var dds = DdsReader.ReadFile(Path.Combine(install!.DataDirectory, "materials", name));
            Assert.Equal((2048, 2048, 12, DdsFormat.Bc1), (dds.Width, dds.Height, dds.MipCount, dds.Format));
        }
    }
}
