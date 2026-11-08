namespace Meitou.Rendering;

/// <summary>Mean, median and tail of one metric over the frames of one side (<c>N</c> samples; NaN values are skipped).</summary>
public sealed record MetricStats(int N, double Mean, double Median, double P95, double P99, double Max, double Sd);

/// <summary>A minus B with its confidence: the standard error of the paired difference (blocks of one side against the next block of the other).</summary>
public sealed record DeltaStats(int Pairs, double Delta, double Se, double Ci95, double MedianDelta, bool Significant);

/// <summary>The harness's statistics: pure functions over samples, tested without a GPU.</summary>
public static class BenchStats
{
    /// <summary>A difference under this many ms is not called significant however narrow its interval (timestamp resolution, rounding).</summary>
    public const double MinMeaningful = 0.005;

    public static MetricStats? Summarize(IEnumerable<double> values)
    {
        var v = values.Where(x => !double.IsNaN(x)).Order().ToArray();
        if (v.Length == 0) return null;
        double mean = v.Average();
        double sd = v.Length > 1 ? Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Length - 1)) : 0;
        return new MetricStats(v.Length, mean, Median(v), Percentile(v, 0.95), Percentile(v, 0.99), v[^1], sd);
    }

    /// <summary>Nearest-rank percentile of sorted values.</summary>
    public static double Percentile(double[] sorted, double q) => sorted[Math.Min((int)(sorted.Length * q), sorted.Length - 1)];

    public static double Median(double[] sorted) => sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;

    /// <summary>
    /// A against B. <paramref name="blocks"/> numbers the frames' blocks (even blocks are side A, odd ones side B); each A block's mean is paired with the
    /// next block's (B) mean, so slow drifts of the shared GPU cancel in the pair. The 95% interval is 1.96 standard errors of the pair differences; the
    /// difference is called significant when it excludes zero. Null with fewer than 3 pairs.
    /// </summary>
    public static DeltaStats? Pair(double[] values, int[] blocks, bool[] keep)
    {
        var sums = new Dictionary<int, (double Sum, int Count)>();
        for (int i = 0; i < values.Length; i++)
        {
            if (!keep[i] || double.IsNaN(values[i])) continue;
            sums.TryGetValue(blocks[i], out var s);
            sums[blocks[i]] = (s.Sum + values[i], s.Count + 1);
        }
        var diffs = new List<double>();
        foreach (var (block, a) in sums)
        {
            if (block % 2 != 0 || !sums.TryGetValue(block + 1, out var b)) continue;
            diffs.Add(a.Sum / a.Count - b.Sum / b.Count);
        }
        if (diffs.Count < 3) return null;
        double mean = diffs.Average();
        double se = Math.Sqrt(diffs.Sum(d => (d - mean) * (d - mean)) / (diffs.Count - 1) / diffs.Count);
        var sorted = diffs.Order().ToArray();
        double ci = 1.96 * se;
        return new DeltaStats(diffs.Count, mean, se, ci, Median(sorted), Math.Abs(mean) > ci && Math.Abs(mean) >= MinMeaningful);
    }

    /// <summary>Two independent runs' difference B - A by their means and spreads (frames taken as independent: optimistic for slow drifts).</summary>
    public static (double Delta, double Se) Unpaired(MetricStats a, MetricStats b) =>
        (b.Mean - a.Mean, Math.Sqrt(a.Sd * a.Sd / Math.Max(a.N, 1) + b.Sd * b.Sd / Math.Max(b.N, 1)));
}
