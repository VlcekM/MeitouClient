using Meitou.Content;
using Meitou.Data.Ogre;
using Meitou.Data.Textures;
using Meitou.Data.World;

namespace Meitou.Tests.World;

/// <summary>
/// The cloud layer's numbers (docs/formats/clouds.md): what the weather feeds SkyX's pass, the coverage table, the drift.
/// </summary>
public class CloudLayerTests
{
    /// <summary>
    /// The coverage table of docs/formats/clouds.md, computed from the shipped textures.
    /// </summary>
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
