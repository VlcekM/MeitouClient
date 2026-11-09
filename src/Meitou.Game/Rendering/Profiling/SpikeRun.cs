using System.Globalization;

using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Profiling;

/// <summary>
/// The profiler's part of <c>--log-spikes</c> (<see cref="SpikeLog"/>): per frame slot the frame's number, notes and render-thread stages, kept
/// until its GPU times arrive, then the report of a spiked frame; and the run's GPU stage samples for the end-of-run summary.
/// </summary>
sealed class SpikeRun(GpuContext native)
{
    /// <summary>
    /// The profiler's frame slots, stages, total series and history length.
    /// </summary>
    const int Slots = FrameProfiler.Slots, Stages = FrameProfiler.Stages, Total = FrameProfiler.Total, History = FrameProfiler.History;

    /// <summary>
    /// Per slot the frame number (<see cref="_frameCounter"/>).
    /// </summary>
    readonly long[] _slotFrame = new long[Slots];

    /// <summary>
    /// Per slot the frame's notes (null once reported) and its render-thread stages as log text.
    /// </summary>
    readonly string?[] _slotNotes = new string?[Slots], _slotCpu = new string?[Slots];

    /// <summary>
    /// Per slot the render thread's frame total and its median over the history.
    /// </summary>
    readonly double[] _slotCpuTotal = new double[Slots], _slotCpuMedian = new double[Slots];

    /// <summary>
    /// Per slot the frame's first stamp and the pre-frame buffer's end (its pre-frame time).
    /// </summary>
    readonly (QuerySlot Begin, QuerySlot PreEnd)[] _slotStamps = new (QuerySlot, QuerySlot)[Slots];

    /// <summary>
    /// The frames recorded so far.
    /// </summary>
    long _frameCounter;

    /// <summary>
    /// The medians' sort buffer.
    /// </summary>
    readonly List<double> _medianScratch = new(History);

    /// <summary>
    /// The run's GPU samples per series (after the skipped frames), for <see cref="PrintRunSummary"/>.
    /// </summary>
    readonly List<float>[] _runGpu = Enumerable.Range(0, FrameProfiler.Series).Select(_ => new List<float>()).ToArray();

    /// <summary>
    /// The frame number slot <paramref name="s"/> holds.
    /// </summary>
    public long FrameOf(int s) => _slotFrame[s];

    /// <summary>
    /// The notes slot <paramref name="s"/>'s frame holds, null once reported.
    /// </summary>
    public string? NotesOf(int s) => _slotNotes[s];

    /// <summary>
    /// Keeps the frame's number, its render-thread stages and its notes in <paramref name="slot"/> until its GPU times arrive;
    /// <paramref name="cpuTotal"/> is the render thread's total series (the frame just kept included), <paramref name="cpuCount"/> its frames before.
    /// </summary>
    public void RecordCpu(int slot, double sum, float[] cpuTotal, int cpuCount)
    {
        _slotFrame[slot] = ++_frameCounter;
        _slotStamps[slot] = native.LastFrameStamps;
        _slotCpuTotal[slot] = sum;
        _slotCpuMedian[slot] = CpuMedian(cpuTotal, cpuCount);
        string stages = StageList(StageClock.Ms, 0.5);
        string ui = PartList("ui", StageClock.UiPartNames, StageClock.UiPartMs);
        string updChars = PartList("upd-chars", StageClock.PartNames, StageClock.PartMs);
        _slotCpu[slot] = stages + ui + updChars;
        long uploaded = native.Frame.Stats.UploadBytes;
        _slotNotes[slot] = (uploaded > 0 ? $"uploads total {uploaded / 1048576.0:0.00} MB; " : "") + SpikeLog.TakeFrame();
    }

    /// <summary>
    /// Adds the frame's stage times to the run's samples (after the skipped frames) for the end-of-run summary.
    /// </summary>
    public void RecordRunGpu(double[] gpuFrame, double total, double sum)
    {
        for (int k = 0; k < Stages; k++) _runGpu[k].Add((float)gpuFrame[k]);
        _runGpu[Stages].Add((float)(total - sum));
        _runGpu[Stages + 1].Add((float)total);
        SpikeLog.OnSummary ??= PrintRunSummary;
    }

    /// <summary>
    /// Reports a spiked frame with its CPU and GPU stages and its notes; <paramref name="gpuTotal"/> is the GPU total series,
    /// <paramref name="gpuCount"/> its frames before this one.
    /// </summary>
    public void ReportSpike(int s, double total, double sum, string notes, double[] gpuFrame, float[] gpuTotal, int gpuCount)
    {
        _medianScratch.Clear();
        int have = Math.Min(gpuCount + 1, History);
        for (int i = 0; i < have; i++) _medianScratch.Add(gpuTotal[i]);
        _medianScratch.Sort();
        double median = have >= 30 ? _medianScratch[have / 2] : 0;
        string pre = PreFrameText(s);
        string gpuStages = StageList(gpuFrame, 0.3) + (total - sum >= 0.3 ? $", other {total - sum:0.0}" : "");
        SpikeLog.Report(_slotFrame[s], total, median, pre + gpuStages, _slotCpu[s] ?? "", notes, _slotCpuTotal[s], _slotCpuMedian[s]);
        _slotNotes[s] = null;
    }

    /// <summary>
    /// The render thread's median frame total over the history (0 before 30 frames), the frame just kept included.
    /// </summary>
    double CpuMedian(float[] cpuTotal, int cpuCount)
    {
        int have = Math.Min(cpuCount + 1, History);
        if (have < 30) return 0;

        _medianScratch.Clear();
        for (int i = 0; i < have; i++) _medianScratch.Add(cpuTotal[i]);
        _medianScratch.Sort();
        return _medianScratch[have / 2];
    }

    /// <summary>
    /// The pre-frame buffer's time for slot <paramref name="s"/> as its log text ("pre-frame x, "), or empty when it has no stamps.
    /// </summary>
    string PreFrameText(int s)
    {
        var arena = native.Frame.Timestamps;
        double pre = -1;
        if (arena.TryRead(_slotStamps[s].Begin, out ulong pb) && arena.TryRead(_slotStamps[s].PreEnd, out ulong pe) && pe >= pb)
            pre = (pe - pb) / 1e6;

        return pre >= 0 ? $"pre-frame {pre:0.0}, " : "";
    }

    /// <summary>
    /// --log-spikes: per stage over the run (after the skipped frames), the GPU time's mean without the frames over 2.5 x the median (the shared
    /// card's outliers), the median, p95 and max.
    /// </summary>
    void PrintRunSummary()
    {
        var total = _runGpu[Total];
        if (total.Count < 10) return;

        var sortedTotal = total.OrderBy(v => v).ToArray();
        float median = sortedTotal[sortedTotal.Length / 2];
        var keep = Enumerable.Range(0, total.Count).Where(i => total[i] <= 2.5f * median).ToArray();
        string dropped = string.Create(CultureInfo.InvariantCulture, $"{total.Count - keep.Length} over 2.5 x the median {median:0.00} ms");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"gpu-run   {total.Count} frames, {dropped} left out of the trimmed mean; per stage ms: trimmed mean / p50 / p95 / max"));
        foreach (int s in FrameProfiler.Order)
        {
            var v = _runGpu[s];
            if (v.Count == 0) continue;

            var sorted = v.OrderBy(x => x).ToArray();
            double mean = keep.Length == 0 ? 0 : keep.Average(i => v[i]);
            string stats = string.Create(CultureInfo.InvariantCulture,
                $"{mean,6:0.00} {sorted[sorted.Length / 2],6:0.00} {sorted[(int)(sorted.Length * 0.95)],6:0.00} {sorted[^1],7:0.00}");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"gpu-run     {FrameProfiler.Label(s),-12} {stats}"));
        }
    }

    /// <summary>
    /// A stage's parts of 1 ms or more, for the spike log: "; ui: hud 25.0, composite 3.1".
    /// </summary>
    static string PartList(string stage, string[] names, double[] ms)
    {
        var parts = new List<string>();
        for (int k = 0; k < names.Length; k++)
        {
            if (ms[k] >= 1) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{names[k]} {ms[k]:0.0}"));
        }

        return parts.Count == 0 ? "" : $"; {stage}: {string.Join(", ", parts)}";
    }

    /// <summary>
    /// The stages of <paramref name="least"/> ms or more, for the spike log: "shadows 3.2, scene 5.0".
    /// </summary>
    static string StageList(double[] ms, double least)
    {
        var parts = new List<string>();
        for (int k = 0; k < Stages; k++)
        {
            if (ms[k] >= least) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{FrameProfiler.Label(k)} {ms[k]:0.0}"));
        }

        return string.Join(", ", parts);
    }
}
