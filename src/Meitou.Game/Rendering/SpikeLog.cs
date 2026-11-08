using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Meitou.Rendering;

/// <summary>
/// <c>--log-spikes</c>: for every frame whose GPU time is over <see cref="Factor"/> times the median of the last
/// frames, or whose render-thread time (the simulation's ticks included) is over that many of its own medians and <see cref="CpuMinMs"/>, one line with
/// the stage breakdown (GPU and render thread) and what the frame uploaded, rebuilt or baked (the places call
/// <see cref="Note"/>; the uploader notes every copy under the allocation's name). Off, a note costs one static read. The notes of a frame
/// are taken when the <see cref="FrameProfiler"/> ends it and printed when its GPU times arrive (a few frames later).
/// </summary>
public static class SpikeLog
{
    public static bool Enabled { get; set; }
    /// <summary>A frame is a spike when its GPU time exceeds this many medians (and <see cref="MinMs"/>).</summary>
    public const double Factor = 1.5;
    /// <summary>Frames at the start that print nothing (streaming settles: MEITOU_BENCH_SKIP).</summary>
    public static int SkipFrames { get; set; } = int.TryParse(Environment.GetEnvironmentVariable("MEITOU_BENCH_SKIP"), out int k) ? k : 0;
    public const double MinMs = 2;
    /// <summary>A frame slow only on the render thread is a spike from this many ms (shorter ones are no stutter).</summary>
    public const double CpuMinMs = 20;

    sealed class Tally { public int Count; public long Bytes; public double Ms; }

    sealed class Total { public long Frames, Count, Bytes, MaxFrameBytes; public double Ms; }
    static readonly Dictionary<string, Total> totals = [];

    /// <summary>One line per kind of note over the whole run (frames it appeared in, count, bytes, the most bytes in one frame), biggest first.</summary>
    public static void PrintSummary()
    {
        if (!Enabled) return;
        OnSummary?.Invoke();
        Console.WriteLine($"spikes    {Spikes} frames over {Factor:0.0} x the median; notes over the run (frames / count / MB total / MB most in one frame):");
        foreach (var (what, t) in totals.OrderByDescending(p => p.Value.Bytes).ThenByDescending(p => p.Value.Count).Take(40))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"spikes      {what,-60} {t.Frames,6} {t.Count,7} {t.Bytes / 1048576.0,9:0.00} {t.MaxFrameBytes / 1048576.0,7:0.00}"));
    }

    static readonly object gate = new();
    static Dictionary<string, Tally> current = [];
    static readonly string[] noNotes = [];
    static long gc2, gcCount, taken_frames;
    static TimeSpan gcPause;

    /// <summary>Set by the profiler: prints its per-stage run summary with <see cref="PrintSummary"/>.</summary>
    internal static Action? OnSummary;

    /// <summary>Every spike line printed so far (the benchmark's summary counts them).</summary>
    public static int Spikes { get; private set; }

    /// <summary>Records that <paramref name="what"/> happened this frame: its <paramref name="bytes"/> uploaded and/or <paramref name="ms"/> of CPU spent (any thread).</summary>
    public static void Note(string what, long bytes = 0, double ms = 0)
    {
        if (!Enabled) return;
        lock (gate)
        {
            if (!current.TryGetValue(what, out var t)) current[what] = t = new Tally();
            t.Count++; t.Bytes += bytes; t.Ms += ms;
        }
    }

    /// <summary>The notes of the frame just ended as one string, and starts the next frame's.</summary>
    internal static string TakeFrame()
    {
        Dictionary<string, Tally> taken;
        lock (gate) { taken = current; current = []; }
        var sb = new StringBuilder();
        long g2 = GC.CollectionCount(2), g0 = GC.CollectionCount(0);
        var pause = GC.GetTotalPauseDuration();
        if (g0 != gcCount || g2 != gc2) sb.Append(CultureInfo.InvariantCulture, $"GC gen0+{g0 - gcCount} gen2+{g2 - gc2} pause {(pause - gcPause).TotalMilliseconds:0.0} ms; ");
        (gcCount, gc2, gcPause) = (g0, g2, pause);
        if (++taken_frames > SkipFrames)
        foreach (var (what, t) in taken)
        {
            if (!totals.TryGetValue(what, out var all)) totals[what] = all = new Total();
            all.Frames++; all.Count += t.Count; all.Bytes += t.Bytes; all.Ms += t.Ms; all.MaxFrameBytes = Math.Max(all.MaxFrameBytes, t.Bytes);
        }
        foreach (var (what, t) in taken.OrderByDescending(p => p.Value.Bytes).ThenByDescending(p => p.Value.Ms))
        {
            sb.Append(what);
            if (t.Count > 1) sb.Append(CultureInfo.InvariantCulture, $" x{t.Count}");
            if (t.Bytes > 0) sb.Append(CultureInfo.InvariantCulture, $" {t.Bytes / 1048576.0:0.00} MB");
            if (t.Ms > 0) sb.Append(CultureInfo.InvariantCulture, $" {t.Ms:0.0} ms");
            sb.Append("; ");
        }
        return sb.ToString();
    }

    /// <summary>Prints the line when <paramref name="total"/> is a spike against <paramref name="median"/>, or <paramref name="cpuTotal"/> against <paramref name="cpuMedian"/>.</summary>
    internal static void Report(long frame, double total, double median, string gpuStages, string cpuStages, string notes, double cpuTotal, double cpuMedian)
    {
        bool gpu = median > 0 && total >= MinMs && total >= Factor * median;
        bool cpu = cpuMedian > 0 && cpuTotal >= CpuMinMs && cpuTotal >= Factor * cpuMedian;
        if (frame <= SkipFrames || !gpu && !cpu) return;
        Spikes++;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"spike    frame {frame}: gpu {total:0.0} ms (median {median:0.0}) [{gpuStages}] cpu {cpuTotal:0.0} ms (median {cpuMedian:0.0}) [{cpuStages}] | {(notes.Length == 0 ? "no notes" : notes)}"));
    }
}
