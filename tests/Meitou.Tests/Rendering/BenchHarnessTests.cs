using Meitou.Game.Rendering.Bench;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Post;


namespace Meitou.Tests.Rendering;

/// <summary>
/// The benchmark harness's pure parts (docs/bench.md "Benchmark harness"): views, statistics, picture diff, toggles, lock, result files.
/// </summary>
public class BenchHarnessTests
{
    [Fact]
    public void PairedDifferenceFindsAStepThroughDrift()
    {
        // A costs 1 ms more than B, on top of a drift that is far larger than the step; blocks of 4 frames.
        var rng = new Random(1);
        int n = 400;
        var values = new double[n];
        var blocks = new int[n];
        for (int i = 0; i < n; i++)
        {
            blocks[i] = i / 4;
            values[i] = 10 + i * 0.05 + (blocks[i] % 2 == 0 ? 1 : 0) + rng.NextDouble() * 0.2;
        }
        var d = BenchStats.Pair(values, blocks, Enumerable.Repeat(true, n).ToArray())!;

        Assert.Equal(50, d.Pairs);

        // The drift adds 0.05 * 4 = 0.2 ms per block pair on top of the 1 ms step, constant in every pair: tight interval.
        Assert.InRange(d.Delta, 0.7, 0.9);
        Assert.True(d.Significant);
        Assert.True(d.Ci95 < 0.1);

        // Without a step the difference is not significant, and with every frame dropped there is nothing to pair.
        var noise = Enumerable.Range(0, n).Select(_ => 5 + rng.NextDouble()).ToArray();
        var single = Enumerable.Range(0, n).ToArray();

        Assert.False(BenchStats.Pair(noise, single, Enumerable.Repeat(true, n).ToArray())!.Significant);
        Assert.Null(BenchStats.Pair(noise, single, new bool[n]));
    }

    [Fact]
    public void LowEndAppliesWhereItStandsAndLaterOptionsWin()
    {
        var o = WorldOptions.Parse(["--world", "--low-end", "--object-distance", "8000"])!;

        Assert.Equal(8000f, o.ObjectDistance);
        Assert.False(o.VSync);
        Assert.True(o.ShadowsOff);
        Assert.Equal(15000f, o.MaterialDistance);
        Assert.Equal((0.25f, 0.1f, 0, 1, 3), (o.ObjectLod, o.GrassDensity, o.WaterReflection, o.Anisotropy, o.TextureQuality));
        Assert.Equal((false, false, false), (o.Post.Ssao, o.Post.Fxaa, o.MeitouWater));
        Assert.Equal((UpscalerKind.Off, 0.83f), (o.Post.Upscale.Kind, o.Post.Upscale.Scale));
        Assert.Equal((true, 4), (o.Post.LowResParticles, o.Post.ParticleDivisor));

        var plain = WorldOptions.Parse(["--world"])!;

        Assert.Equal((true, false, 1f), (plain.VSync, plain.ShadowsOff, plain.ObjectLod));
    }

    [Fact]
    public void PictureDiffCountsThresholds()
    {
        int w = 4, h = 2;
        var a = new byte[w * h * 4];
        var b = new byte[w * h * 4];
        b[0] = 1;          // pixel 0: 1/255
        b[4 + 1] = 5;      // pixel 1: 5
        b[8 + 2] = 40;     // pixel 2: 40
        var (r, heat) = PictureDiff.Compare(a, b, w, h);

        Assert.Equal((1L, 2L, 3L), (r.Over12, r.Over4, r.Over1));
        Assert.Equal(40, r.MaxDiff);
        Assert.Equal(46 / (3.0 * w * h), r.MeanDiff, 9);
        Assert.Equal((0, 0, 0), (heat[12], heat[13], heat[14]));   // equal pixels are black
        Assert.NotEqual((0, 0, 0), (heat[8], heat[9], heat[10]));
    }
}
