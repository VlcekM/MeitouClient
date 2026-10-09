using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>The benchmark harness's pure parts (docs/bench.md "Benchmark harness"): views, statistics, picture diff, toggles, lock, result files.</summary>
public class BenchHarnessTests
{
    [Fact]
    public void ViewExpandsInPlaceAndLaterOptionsWin()
    {
        var args = NamedViews.Expand(["--view", "swamp", "--distance", "500"]);
        Assert.Contains("--town", args);
        Assert.Equal("Shark", args[Array.IndexOf(args, "--town") + 1]);
        var o = WorldOptions.Parse(["--view", "swamp", "--distance", "500"])!;
        Assert.Equal(500f, o.Distance);
        Assert.Equal("swamp", o.View);
        Assert.Equal(2f, o.Radius);
        var dust = WorldOptions.Parse(["--view", "dust"])!;
        Assert.Equal("Dust Storm Approach", dust.Weather);
        Assert.Equal("The Hub", WorldOptions.Parse(["--view", "hub"])!.Town);
    }

    [Fact]
    public void UnknownViewIsRejected() => Assert.Throws<ArgumentException>(() => WorldOptions.Parse(["--view", "nowhere"]));

    [Fact]
    public void BenchOptionsParse()
    {
        var o = WorldOptions.Parse(["--view", "swamp", "--ab", "shadows", "--ab-period", "64", "--bench-frames", "300", "--bench-motion", "turn", "--no-gpu-lock", "--bench-out", "x.json"])!;
        Assert.Equal(("shadows", 64, 300, "turn", true, "x.json"), (o.Ab, o.AbPeriod, o.BenchFrames, o.BenchMotion, o.NoGpuLock, o.BenchOut));
        Assert.Throws<ArgumentException>(() => WorldOptions.Parse(["--bench-motion", "spin"]));
        Assert.Equal(("a.json", "b.json"), WorldOptions.Parse(["--bench-compare", "a.json", "b.json"])!.BenchCompare);
    }

    [Fact]
    public void BenchSizeDefaultsSmaller()
    {
        var bench = WorldOptions.Parse(["--view", "swamp", "--bench-frames", "300"])!;
        Assert.Equal((1280, 720), (bench.Width, bench.Height));
        var ab = WorldOptions.Parse(["--view", "swamp", "--ab", "shadows"])!;
        Assert.Equal((1280, 720), (ab.Width, ab.Height));
        var sized = WorldOptions.Parse(["--view", "swamp", "--bench-frames", "300", "--size", "2560x1440"])!;
        Assert.Equal((2560, 1440), (sized.Width, sized.Height));
        var shot = WorldOptions.Parse(["--view", "swamp", "--screenshot", "x.png"])!;
        Assert.Equal((1280, 960), (shot.Width, shot.Height));
    }

    [Fact]
    public void SummaryHasMeanMedianAndTail()
    {
        var s = BenchStats.Summarize(Enumerable.Range(1, 100).Select(i => (double)i).Append(double.NaN))!;
        Assert.Equal(100, s.N);
        Assert.Equal(50.5, s.Mean, 6);
        Assert.Equal(50.5, s.Median, 6);
        Assert.Equal(96, s.P95);
        Assert.Equal(100, s.Max);
    }

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
    }

    [Fact]
    public void NoDifferenceIsNotSignificantAndDroppedFramesAreLeftOut()
    {
        var rng = new Random(2);
        int n = 200;
        var values = Enumerable.Range(0, n).Select(_ => 5 + rng.NextDouble()).ToArray();
        var blocks = Enumerable.Range(0, n).Select(i => i).ToArray();
        var keep = Enumerable.Repeat(true, n).ToArray();
        Assert.False(BenchStats.Pair(values, blocks, keep)!.Significant);
        Assert.Null(BenchStats.Pair(values, blocks, new bool[n]));   // nothing kept
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

    [Fact]
    public void TogglesFlipAndUnknownNamesAreMissing()
    {
        AbToggles.Clear();
        bool on = false;
        AbToggles.Register("thing", () => on, v => on = v);
        Assert.True(AbToggles.TryGet("THING", out var t));
        t.Set(true);
        Assert.True(on);
        Assert.False(AbToggles.TryGet("other", out _));
        AbToggles.Clear();
    }

    [Fact]
    public void EnhancementsRegisterUnderTheirIds()
    {
        var o = new WorldOptions();
        AbToggles.Clear();
        AbToggles.RegisterEnhancements(WorldOptions.Switches(o));
        foreach (var id in new[] { "ao", "shadows", "lod", "particles", "water", "impostors" }) Assert.True(AbToggles.TryGet(id, out _), id);
        AbToggles.TryGet("lod", out var lod);
        lod.Set(false);
        Assert.False(o.FoliageLod);
        lod.Set(true);
        Assert.True(o.FoliageLod);
        AbToggles.Clear();
    }

    [Fact]
    public async Task GpuLockQueuesAndTakesOverAStaleOne()
    {
        string path = Path.Combine(Path.GetTempPath(), "meitou-gpu-test-" + Guid.NewGuid().ToString("N") + ".lock");
        GpuLock.PathOverride = path;
        try
        {
        File.Delete(path);
        File.WriteAllText(path, "2147483000");   // a PID that is not running
        using (var first = GpuLock.Acquire(TextWriter.Null))
        {
            Assert.Equal(2147483000, first.StaleHolder);
            Assert.Equal(Environment.ProcessId.ToString(), ReadHolder(path));
            // A second taker waits until the first lets go.
            var second = Task.Run(() => GpuLock.Acquire(TextWriter.Null));
            await Task.Delay(600, TestContext.Current.CancellationToken);
            Assert.False(second.IsCompleted);
            first.Dispose();
            using var taken = await second;
            Assert.True(taken.Waited >= TimeSpan.FromMilliseconds(400));
        }
        Assert.False(File.Exists(path));
        }
        finally { GpuLock.PathOverride = null; }
    }

    static string ReadHolder(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(fs).ReadToEnd().Trim();
    }

    [Fact]
    public void ResultRoundTripsAndCompares()
    {
        BenchResult Make(double gpu)
        {
            var r = new BenchResult { Meta = { ["view"] = "swamp", ["commit"] = "abc" } };
            r.Configs["A"] = new BenchConfig { Label = "A", Frames = 100, Metrics = { ["gpu:total"] = new MetricStats(100, gpu, gpu, gpu + 1, gpu + 2, gpu + 3, 0.5), ["frame"] = new MetricStats(100, gpu + 2, gpu + 2, gpu + 3, gpu + 4, gpu + 5, 0.5) } };
            return r;
        }
        string dir = Path.Combine(Path.GetTempPath(), "meitou-bench-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Make(4).Save(Path.Combine(dir, "a.json"));
            Make(5).Save(Path.Combine(dir, "b.json"));
            var loaded = BenchResult.Load(Path.Combine(dir, "a.json"));
            Assert.Equal(4, loaded.Configs["A"].Metrics["gpu:total"].Mean);
            var text = new StringWriter();
            BenchResult.Compare(Path.Combine(dir, "a.json"), Path.Combine(dir, "b.json"), text);
            Assert.Contains("+1.00", text.ToString());
            var table = new StringWriter();
            loaded.Print(table);
            Assert.Contains("total", table.ToString());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
