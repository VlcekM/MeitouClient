using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Meitou.Game.Rendering.Profiling;

/// <summary>
/// <c>--log-spikes</c>: for every frame whose GPU time is over <see cref="Factor"/> times the median of the last
/// frames, or whose render-thread time (the simulation's ticks included) is over that many of its own medians and <see cref="CpuMinMs"/>, one line
/// with the stage breakdown (GPU and render thread) and what the frame uploaded, rebuilt or baked (the places call
/// <see cref="Note"/>; the uploader notes every copy under the allocation's name). Off, a note costs one static read. The notes of a frame
/// are taken when the <see cref="FrameProfiler"/> ends it and printed when its GPU times arrive (a few frames later).
/// </summary>
public static class SpikeLog
{
    /// <summary>A frame is a spike when its GPU time exceeds this many medians (and <see cref="MinMs"/>).</summary>
    public const double Factor = 1.5;

    public const double MinMs = 2;

    /// <summary>A frame slow only on the render thread is a spike from this many ms (shorter ones are no stutter).</summary>
    public const double CpuMinMs = 20;

    static readonly object Gate = new();
    static readonly string[] NoNotes = [];
    static readonly Dictionary<string, Total> Totals = [];
    static Dictionary<string, Tally> s_current = [];
    static long s_gc2, s_gcCount, s_takenFrames;
    static TimeSpan s_gcPause;

    /// <summary>Set by the profiler: prints its per-stage run summary with <see cref="PrintSummary"/>.</summary>
    internal static Action? OnSummary;

    public static bool Enabled { get; set; }

    /// <summary>Frames at the start that print nothing (streaming settles: MEITOU_BENCH_SKIP).</summary>
    public static int SkipFrames { get; set; } = int.TryParse(Environment.GetEnvironmentVariable("MEITOU_BENCH_SKIP"), out int k) ? k : 0;

    /// <summary>Every spike line printed so far (the benchmark's summary counts them).</summary>
    public static int Spikes { get; private set; }

    /// <summary>
    /// One line per kind of note over the whole run (frames it appeared in, count, bytes, the most bytes in one frame), biggest first.
    /// </summary>
    public static void PrintSummary()
    {
        if (!Enabled) return;

        OnSummary?.Invoke();
        string title = $"spikes    {Spikes} frames over {Factor:0.0} x the median; " +
            "notes over the run (frames / count / MB total / MB most in one frame):";
        Console.WriteLine(title);
        foreach (var (what, t) in Totals.OrderByDescending(p => p.Value.Bytes).ThenByDescending(p => p.Value.Count).Take(40))
        {
            string row = string.Create(CultureInfo.InvariantCulture,
                $"spikes      {what,-60} {t.Frames,6} {t.Count,7} {t.Bytes / 1048576.0,9:0.00} {t.MaxFrameBytes / 1048576.0,7:0.00}");
            Console.WriteLine(row);
        }
    }

    /// <summary>
    /// Records that <paramref name="what"/> happened this frame: its <paramref name="bytes"/> uploaded and/or <paramref name="ms"/> of CPU spent (any
    /// thread).
    /// </summary>
    public static void Note(string what, long bytes = 0, double ms = 0)
    {
        if (!Enabled) return;

        lock (Gate)
        {
            if (!s_current.TryGetValue(what, out var t)) s_current[what] = t = new Tally();

            t.Count++;
            t.Bytes += bytes;
            t.Ms += ms;
        }
    }

    /// <summary>The notes of the frame just ended as one string, and starts the next frame's.</summary>
    internal static string TakeFrame()
    {
        Dictionary<string, Tally> taken;
        lock (Gate)
        {
            taken = s_current;
            s_current = [];
        }

        var sb = new StringBuilder();
        AppendGarbageCollections(sb);
        if (++s_takenFrames > SkipFrames)
        {
            foreach (var (what, t) in taken) AddToTotals(what, t);
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

    /// <summary>
    /// Prints the line when <paramref name="total"/> is a spike against <paramref name="median"/>, or <paramref name="cpuTotal"/> against <paramref
    /// name="cpuMedian"/>.
    /// </summary>
    internal static void Report(long frame, double total, double median, string gpuStages, string cpuStages, string notes, double cpuTotal,
        double cpuMedian)
    {
        bool gpu = median > 0 && total >= MinMs && total >= Factor * median;
        bool cpu = cpuMedian > 0 && cpuTotal >= CpuMinMs && cpuTotal >= Factor * cpuMedian;
        if (frame <= SkipFrames || !gpu && !cpu) return;

        Spikes++;
        string notesText = notes.Length == 0 ? "no notes" : notes;
        string gpuText = string.Create(CultureInfo.InvariantCulture, $"gpu {total:0.0} ms (median {median:0.0}) [{gpuStages}]");
        string cpuText = string.Create(CultureInfo.InvariantCulture, $"cpu {cpuTotal:0.0} ms (median {cpuMedian:0.0}) [{cpuStages}]");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"spike    frame {frame}: {gpuText} {cpuText} | {notesText}"));
    }

    /// <summary>Appends the collections since the last frame (gen0 and gen2 counts, the pause) when there were any.</summary>
    static void AppendGarbageCollections(StringBuilder sb)
    {
        long g2 = GC.CollectionCount(2), g0 = GC.CollectionCount(0);
        var pause = GC.GetTotalPauseDuration();
        if (g0 != s_gcCount || g2 != s_gc2)
            sb.Append(CultureInfo.InvariantCulture,
                    $"GC gen0+{g0 - s_gcCount} gen2+{g2 - s_gc2} pause {(pause - s_gcPause).TotalMilliseconds:0.0} ms; ");

        (s_gcCount, s_gc2, s_gcPause) = (g0, g2, pause);
    }

    /// <summary>Adds one frame's notes of a kind to the run's totals.</summary>
    static void AddToTotals(string what, Tally t)
    {
        if (!Totals.TryGetValue(what, out var all)) Totals[what] = all = new Total();

        all.Frames++;
        all.Count += t.Count;
        all.Bytes += t.Bytes;
        all.Ms += t.Ms;
        all.MaxFrameBytes = Math.Max(all.MaxFrameBytes, t.Bytes);
    }

    sealed class Tally
    {
        public int Count;
        public long Bytes;
        public double Ms;
    }

    sealed class Total
    {
        public long Frames, Count, Bytes, MaxFrameBytes;
        public double Ms;
    }
}
