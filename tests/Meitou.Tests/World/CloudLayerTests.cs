using System.Numerics;
using Meitou.Content;
using Meitou.Rendering;
using Meitou.Data;
using Meitou.Data.Textures;
using Meitou.Data.World;

namespace Meitou.Tests.World;

/// <summary>The cloud layer's numbers (docs/formats/clouds.md): what the weather feeds SkyX's pass, the coverage table, the drift.</summary>
public class CloudLayerTests
{
    [Fact]
    public void Density_offset_and_darkness_follow_the_sky_controller()
    {
        // 1.4 c - 0.8 and pow(clamp(c - 0.5, 0, 1), 0.3), c clamped to 0..1 (SkyBeam's 20 is 1).
        Assert.Equal(-0.8f, CloudLayer.DensityOffset(0), 5);
        Assert.Equal(-0.66f, CloudLayer.DensityOffset(0.1f), 5);
        Assert.Equal(-0.52f, CloudLayer.DensityOffset(0.2f), 5);
        Assert.Equal(-0.24f, CloudLayer.DensityOffset(0.4f), 5);
        Assert.Equal(-0.10f, CloudLayer.DensityOffset(0.5f), 5);
        Assert.Equal(0.04f, CloudLayer.DensityOffset(0.6f), 5);
        Assert.Equal(0.6f, CloudLayer.DensityOffset(1), 5);
        Assert.Equal(0.6f, CloudLayer.DensityOffset(20), 5);
        Assert.Equal(0, CloudLayer.Darkness(0));
        Assert.Equal(0, CloudLayer.Darkness(0.5f));
        Assert.Equal(0.5f, CloudLayer.Darkness(0.6f), 2);
        Assert.Equal(0.81f, CloudLayer.Darkness(1), 2);
        Assert.Equal(CloudLayer.Darkness(1), CloudLayer.Darkness(20));
    }

    [Fact]
    public void Horizon_alpha_is_the_haze_pull_and_the_band_follows_the_elevation()
    {
        foreach (float c in new[] { 0f, 0.1f, 0.2f, 0.4f, 0.5f, 0.6f, 1f })
            Assert.Equal(KenshiHaze.CloudPull(c), CloudLayer.HorizonAlpha(c));
        // The coverage table's "horizon band alpha" column.
        Assert.Equal(0, CloudLayer.HorizonAlpha(0.2f));
        Assert.Equal(0.26f, CloudLayer.HorizonAlpha(0.4f), 5);
        Assert.Equal(0.40f, CloudLayer.HorizonAlpha(0.5f), 5);
        Assert.Equal(0.54f, CloudLayer.HorizonAlpha(0.6f), 5);
        Assert.Equal(1, CloudLayer.HorizonAlpha(1));
        // h: 0 up to d.y 0.05 (2.9 degrees), 1 from 0.15 (8.6 degrees).
        Assert.Equal(0, CloudLayer.HorizonBand(0.05f));
        Assert.Equal(0.5f, CloudLayer.HorizonBand(0.1f), 5);
        Assert.Equal(1, CloudLayer.HorizonBand(0.15f));
        Assert.Equal(1, CloudLayer.HorizonBand(1));
    }

    [Fact]
    public void Zenith_light_is_the_plus_z_colour_over_a_floor_scaled_by_the_suns_green()
    {
        var noon = new SkyClock(54, 5, 23).SunDirection(13);
        var white = CloudLayer.ZenithLight(noon, Vector3.One);
        var expected = SkyXModel.Colour(Vector3.UnitZ, noon, skydome: false) * KenshiLighting.SunColour(noon).Y;
        Assert.True(white.X > 0 && Vector3.Distance(white, expected) < 1e-5f);
        // The sky multiplier tints it; below the sun's cut-off the sun colour is black and so is the light.
        Assert.True(Vector3.Distance(white * 0.5f, CloudLayer.ZenithLight(noon, new Vector3(0.5f))) < 1e-5f);
        var night = new SkyClock(54, 5, 23).SunDirection(1);
        Assert.Equal(Vector3.Zero, CloudLayer.ZenithLight(night, Vector3.One));
        // A near-black +Z colour is lifted to the floor before the sun's green scales it.
        Assert.True(Vector3.Distance(CloudLayer.ZenithFloor * KenshiLighting.SunColour(noon).Y, CloudLayer.ZenithLight(noon, Vector3.Zero)) < 1e-7f);
    }

    [Fact]
    public void Wind_offset_accumulates_and_shifts_the_texture_by_5e_minus_5_per_unit()
    {
        var offset = (X: 0.0, Z: 0.0);
        for (int i = 0; i < 600; i++) offset = CloudLayer.Advance(offset, new Vector2(20, -10), 1f / 60);   // 10 s at (20, -10) per second
        Assert.Equal(200, offset.X, 2);
        Assert.Equal(-100, offset.Z, 2);
        var shift = CloudLayer.TextureShift((200.0, -100.0));
        Assert.Equal(0.01f, shift.X, 5);                 // 200 x 0.00005
        Assert.Equal(1 - 0.005f, shift.Y, 5);            // -100 x 0.00005, wrapped
        Assert.Equal(offset, CloudLayer.Advance(offset, new Vector2(20, -10), 0));   // paused: dt 0
    }

    /// <summary>The coverage table of docs/formats/clouds.md, computed from the shipped textures.</summary>
    [Fact]
    [Slow]
    public void Coverage_table_matches_the_shipped_textures()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var assets = new AssetLocator(install!);
        var clouds = assets.Find("Clouds.dds");
        var tile = assets.Find("CloudsTile.dds");
        Assert.SkipWhen(clouds is null || tile is null, "cloud textures not found");
        var a = TextureLoader.LoadFile(clouds!, allMips: false).Levels[0];
        var b = TextureLoader.LoadFile(tile!, allMips: false).Levels[0];
        Assert.Equal(1024, a.Width);
        Assert.Equal(1024, b.Width);
        static byte[] Red(RgbaImage i) { var r = new byte[i.Width * i.Height]; for (int k = 0; k < r.Length; k++) r[k] = i.Pixels[k * 4]; return r; }
        var cloudRed = Red(a);
        var tileRed = Red(b);
        Assert.Equal(0.275, cloudRed.Average(v => v / 255.0), 2);
        Assert.Equal(0.53, tileRed.Average(v => v / 255.0), 2);
        // c: mean alpha, share above 0.05, share above 0.5.
        var table = new (float C, double Mean, double A005, double A05)[]
        {
            (0f, 0.000, 0.000, 0.000), (0.1f, 0.001, 0.005, 0.000), (0.2f, 0.010, 0.064, 0.001), (0.4f, 0.25, 0.87, 0.13),
            (0.5f, 0.54, 0.996, 0.49), (0.6f, 0.83, 1.0, 0.91), (1f, 1.0, 1.0, 1.0),
        };
        foreach (var (c, mean, a005, a05) in table)
        {
            var (m, s005, s05) = CloudLayer.Coverage(cloudRed, tileRed, 1024, c);
            Console.WriteLine($"c={c}: mean {m:0.0000} (doc {mean}), >0.05 {s005:0.0000} (doc {a005}), >0.5 {s05:0.0000} (doc {a05})");
            Assert.True(Math.Abs(m - mean) <= 0.006 + (mean > 0.1 ? 0.01 : 0), $"c={c}: mean {m:0.0000} vs {mean}");
            Assert.True(Math.Abs(s005 - a005) <= 0.01, $"c={c}: above 0.05 {s005:0.0000} vs {a005}");
            Assert.True(Math.Abs(s05 - a05) <= 0.01, $"c={c}: above 0.5 {s05:0.0000} vs {a05}");
        }
        // A clear sky has no clouds at all: not a single texel has any alpha.
        var (_, any, _) = CloudLayer.Coverage(cloudRed, tileRed, 1024, 0);
        Assert.Equal(0, any);
    }
}
