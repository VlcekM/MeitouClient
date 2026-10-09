using System.Globalization;
using System.Numerics;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// Where the frame time goes, per <see cref="StageClock"/> stage: the render thread's time (stopwatch laps) and the GPU's (a
/// timestamp at every lap; a stage's time is the gap since the stamp before, summed when a stage runs once per depth slice).
/// Keeps the last <see cref="History"/> frames and draws them with the <see cref="DebugOverlay"/> as one chart, a line per stage.
/// GPU timings are read a few frames late, without waiting.
/// </summary>
public sealed class FrameProfiler : IDisposable
{
    public enum Mode { Off, Gpu, Cpu }

    public const int History = 300;
    const int Slots = 4, MaxStamps = 128, Stages = 14;
    // Series: the stages by StageClock index, then the GPU time outside the stamped stages (uploads, overlay), then the total.
    const int Other = Stages, Total = Stages + 1, Series = Stages + 2;
    // The stages in the order a frame runs them; 11 is the overlay, submit and present after the scene.
    static readonly int[] Order = [0, 1, 2, 3, 12, 4, 5, 6, 7, 8, 9, 13, 10, 11, Other, Total];

    static readonly Vector4[] Colours =
    [
        new(0.55f, 0.75f, 1.00f, 1), new(0.35f, 0.55f, 0.95f, 1), new(0.60f, 0.45f, 0.95f, 1), new(0.95f, 0.85f, 0.35f, 1),
        new(0.25f, 0.80f, 0.85f, 1), new(0.95f, 0.55f, 0.85f, 1), new(0.65f, 0.65f, 0.65f, 1), new(0.95f, 0.55f, 0.25f, 1),
        new(0.40f, 0.85f, 0.40f, 1), new(0.30f, 0.60f, 1.00f, 1), new(0.95f, 0.35f, 0.35f, 1), new(0.55f, 0.55f, 0.75f, 1),
        new(0.45f, 0.35f, 0.25f, 1), new(1.00f, 0.75f, 0.20f, 1), new(0.50f, 0.50f, 0.50f, 1), new(1.00f, 1.00f, 1.00f, 1),
    ];

    readonly Func<double>? gpuFrameMs;
    readonly int[,] stampStage = new int[Slots, MaxStamps];
    readonly int[] stampCount = new int[Slots];
    readonly bool[] pending = new bool[Slots];
    int slot;

    readonly float[][] cpu = NewHistory(), gpu = NewHistory();
    int cpuHead, cpuCount, gpuHead, gpuCount;
    readonly double[] gpuFrame = new double[Series];

    // --log-spikes (SpikeLog): per slot the frame number, its notes and its render-thread stages, kept until the GPU times arrive.
    readonly long[] slotFrame = new long[Slots];
    readonly string?[] slotNotes = new string?[Slots], slotCpu = new string?[Slots];
    readonly double[] slotCpuTotal = new double[Slots];
    readonly (QuerySlot Begin, QuerySlot PreEnd)[] slotStamps = new (QuerySlot, QuerySlot)[Slots];
    readonly (QuerySlot Begin, QuerySlot End)[] slotWhole = new (QuerySlot, QuerySlot)[Slots];   // each profiled frame's own first and last stamps (its GPU total)
    long frameCounter;
    readonly long[] slotTag = new long[Slots];
    readonly List<double> medianScratch = new(History);

    public Mode Showing { get; set; }

    /// <summary>The benchmark harness's frame number: copied at <see cref="BeginFrame"/> and handed back with the frame's GPU times by <see cref="OnGpuFrame"/>, which arrive a few frames later.</summary>
    public long Tag { get; set; }

    /// <summary>Called when a frame's GPU times have been read: its <see cref="Tag"/>, the ms per <see cref="StageClock"/> stage (do not keep the array), and the whole frame's GPU ms.</summary>
    public Action<long, double[], double>? OnGpuFrame { get; set; }

    /// <summary>Reads every GPU timing that is ready now (after the last frame of a run has been waited for).</summary>
    public void Flush() { for (int s = 0; s < Slots; s++) Collect(s); }

    /// <summary>Frames whose GPU stage times have been read (at most <see cref="History"/>).</summary>
    public int GpuFrames => gpuCount;

    /// <summary>The newest frame's GPU time over its stamped stages, in ms (0 before the first frame is read).</summary>
    public double LastGpuMs => gpuCount == 0 ? 0 : gpu[Total][(gpuHead - 1 + History) % History];

    /// <param name="gpuFrameMs">The whole frame's GPU time, when the backend measures it (it includes the uploads before the first stage).</param>
    public FrameProfiler(GpuContext gpu, Func<double>? gpuFrameMs = null)
    {
        this.gpuFrameMs = gpuFrameMs;
        native = gpu;
        recordStamp = cmd => cmd.Timestamp(native.Frame.Timestamps, pendingStamp);
    }

    static float[][] NewHistory()
    {
        var h = new float[Series][];
        for (int i = 0; i < Series; i++) h[i] = new float[History];
        return h;
    }

    static string Label(int series) => series switch { Other => "other", Total => "total", 11 => "present", _ => StageClock.Names[series] };

    /// <summary>Before the frame's first stage (after the wait for a free frame, so that wait is not counted).</summary>
    public void BeginFrame()
    {
        for (int s = 0; s < Slots; s++) Collect(s);
        slot = (slot + 1) % Slots;
        slotWhole[slot] = default;
        pending[slot] = false;   // still not ready after a full round: dropped
        stampCount[slot] = 0;
        slotTag[slot] = Tag;
        StageClock.Start();
        StageClock.Profiler = this;
        Stamp(-1);
    }

    /// <summary>After the frame is presented: the last stage (overlay, submit, present) ends and the frame's laps are kept.</summary>
    public void EndFrame()
    {
        // Its GPU stamp would land in the next frame's commands: the CPU lap only.
        StageClock.Profiler = null;
        StageClock.Lap(11);
        StageClock.Active = false;
        pending[slot] = stampCount[slot] > 1;
        slotWhole[slot] = native.CurrentFrameStamps;   // complete when the context has ended the frame already (else the total falls back to gpuFrameMs)
        double sum = 0;
        for (int s = 0; s < Stages; s++)
        {
            cpu[s][cpuHead] = (float)StageClock.Ms[s];
            sum += StageClock.Ms[s];
        }
        cpu[Other][cpuHead] = 0;
        cpu[Total][cpuHead] = (float)sum;
        if (SpikeLog.Enabled)
        {
            slotFrame[slot] = ++frameCounter;
            slotStamps[slot] = native.LastFrameStamps;
            slotCpuTotal[slot] = sum;
            slotCpu[slot] = StageList(StageClock.Ms, 0.5);
            long uploaded = native.Frame.Stats.UploadBytes;
            slotNotes[slot] = (uploaded > 0 ? $"uploads total {uploaded / 1048576.0:0.00} MB; " : "") + SpikeLog.TakeFrame();
        }
        cpuHead = (cpuHead + 1) % History;
        cpuCount = Math.Min(cpuCount + 1, History);
    }

    internal void Stamp(int stage)
    {
        int n = stampCount[slot];
        if (n >= MaxStamps) return;
        // The native timestamps (QueryArena), recorded through the seam without ending VkGl's pass: they keep working while the
        // stages move to native code (docs/renderer-native.md 7.1 step 9).
        var q = native.Frame.Timestamps.Allocate();
        if (!q.IsValid) return;
        pendingStamp = q;
        native.Interleave(recordStamp);
        nativeStamps[slot, n] = q;
        stampStage[slot, n] = stage;
        stampCount[slot] = n + 1;
    }

    // Native timestamps: the context (VkGl provides the seam), the slots per stamp, and the record callback (no allocation per stamp).
    readonly GpuContext native;
    (ulong Used, ulong Budget) vram;
    int vramAge;
    List<(string Name, ulong Bytes)> vramSlices = [];

    static readonly Vector4[] PieColours =
    [
        new(0.35f, 0.60f, 1.00f, 1), new(0.95f, 0.55f, 0.25f, 1), new(0.40f, 0.85f, 0.40f, 1), new(0.95f, 0.35f, 0.35f, 1),
        new(0.65f, 0.45f, 0.95f, 1), new(0.95f, 0.85f, 0.35f, 1), new(0.25f, 0.80f, 0.85f, 1), new(0.95f, 0.55f, 0.85f, 1),
        new(0.55f, 0.55f, 0.55f, 1), new(0.35f, 0.35f, 0.40f, 1), new(0.20f, 0.20f, 0.22f, 1),
    ];

    /// <summary>
    /// The VRAM by owner: the eight largest device-local owners of <see cref="GpuAllocator.Breakdown"/>, the rest of ours, the unused room in
    /// our blocks, and what the driver counts beyond our blocks (its own allocations, swapchain, other APIs' resources such as DLSS's).
    /// </summary>
    List<(string Name, ulong Bytes)> VramSlices()
    {
        var alloc = native.Device.Allocator;
        var owners = alloc.Breakdown().Where(o => o.DeviceLocal > 0).OrderByDescending(o => o.DeviceLocal).ToList();
        var slices = owners.Take(8).Select(o => (o.Name, o.DeviceLocal)).ToList();
        ulong ours = (ulong)owners.Sum(o => (double)o.DeviceLocal), shown = (ulong)slices.Sum(s => (double)s.DeviceLocal);
        if (ours > shown) slices.Add(("other of ours", ours - shown));
        ulong blocks = alloc.TotalAllocatedBytes, used = alloc.TotalUsedBytes;
        if (blocks > used) slices.Add(("free in our blocks", blocks - used));
        if (vram.Used > blocks) slices.Add(("driver and others", vram.Used - blocks));
        return slices;
    }

    void DrawVramPie(DebugOverlay overlay, int width)
    {
        if (vramSlices.Count == 0) return;
        double total = vramSlices.Sum(s => (double)s.Bytes);
        if (total <= 0) return;
        const float margin = 16, pad = 12, radius = 70;
        float lh = overlay.LineHeight, cw = overlay.CharWidth;
        float legendW = 2 * cw + 30 * cw;
        float panelW = pad * 3 + 2 * radius + legendW, panelH = Math.Max(2 * radius, (vramSlices.Count + 1.5f) * lh) + 2 * pad;
        float x1 = width - margin, x0 = x1 - panelW, y0 = margin;
        overlay.Rect(x0, y0, x1, y0 + panelH, DebugOverlay.PanelColour);
        var centre = new Vector2(x0 + pad + radius, y0 + pad + Math.Max(radius, (panelH - 2 * pad) / 2));
        double angle = -Math.PI / 2;
        float lx = x0 + pad * 2 + 2 * radius, ly = y0 + pad;
        overlay.Text($"VRAM {vram.Used / 1073741824.0:0.00} of {vram.Budget / 1073741824.0:0.0} GB", lx, ly, DebugOverlay.TextColour);
        for (int i = 0; i < vramSlices.Count; i++)
        {
            var (name, bytes) = vramSlices[i];
            var colour = PieColours[Math.Min(i, PieColours.Length - 1)];
            double sweep = bytes / total * Math.Tau;
            int steps = Math.Max(1, (int)Math.Ceiling(sweep / (Math.Tau / 96)));
            for (int k = 0; k < steps; k++)
            {
                double a0 = angle + sweep * k / steps, a1 = angle + sweep * (k + 1) / steps;
                overlay.Triangle(centre, centre + radius * new Vector2((float)Math.Cos(a0), (float)Math.Sin(a0)),
                    centre + radius * new Vector2((float)Math.Cos(a1), (float)Math.Sin(a1)), colour);
            }
            angle += sweep;
            float top = ly + (i + 1.5f) * lh;
            overlay.Rect(lx, top + 4, lx + 10, top + 14, colour);
            string label = name.Length > 20 ? name[..20] : name;
            overlay.Text($"{label,-20}{bytes / 1048576.0,7:0} MB", lx + 2 * cw, top, DebugOverlay.TextColour);
        }
    }
    readonly QuerySlot[,] nativeStamps = new QuerySlot[Slots, MaxStamps];
    QuerySlot pendingStamp;
    readonly Action<CommandList> recordStamp;

    bool TryStamp(int s, int i, out ulong ns) => native.Frame.Timestamps.TryRead(nativeStamps[s, i], out ns);

    void Collect(int s)
    {
        if (!pending[s]) return;
        int n = stampCount[s];
        if (!TryStamp(s, n - 1, out _)) return;
        Array.Clear(gpuFrame);
        TryStamp(s, 0, out ulong previous);
        double sum = 0;
        for (int i = 1; i < n; i++)
        {
            TryStamp(s, i, out ulong now);
            double ms = now >= previous ? (now - previous) / 1e6 : 0;
            gpuFrame[stampStage[s, i]] += ms;
            sum += ms;
            previous = now;
        }
        // The frame's own total (first to last stamp); the context's latest completed frame is only a stand-in and, with frames in flight, another frame.
        var whole = slotWhole[s];
        double own = native.Frame.Timestamps.TryRead(whole.Begin, out ulong wb) && native.Frame.Timestamps.TryRead(whole.End, out ulong we) && we >= wb ? (we - wb) / 1e6 : 0;
        double total = own > 0 ? Math.Max(own, sum) : gpuFrameMs?.Invoke() is > 0 and var t ? Math.Max(t, sum) : sum;
        for (int k = 0; k < Stages; k++) gpu[k][gpuHead] = (float)gpuFrame[k];
        gpu[Other][gpuHead] = (float)(total - sum);
        gpu[Total][gpuHead] = (float)total;
        OnGpuFrame?.Invoke(slotTag[s], gpuFrame, total);
        if (SpikeLog.Enabled && slotFrame[s] > SpikeLog.SkipFrames)
        {
            for (int k = 0; k < Stages; k++) runGpu[k].Add((float)gpuFrame[k]);
            runGpu[Stages].Add((float)(total - sum));
            runGpu[Stages + 1].Add((float)total);
            SpikeLog.OnSummary ??= PrintRunSummary;
        }
        if (SpikeLog.Enabled && slotNotes[s] is { } notes)
        {
            medianScratch.Clear();
            int have = Math.Min(gpuCount + 1, History);
            for (int i = 0; i < have; i++) medianScratch.Add(gpu[Total][i]);
            medianScratch.Sort();
            double median = have >= 30 ? medianScratch[have / 2] : 0;
            double pre = native.Frame.Timestamps.TryRead(slotStamps[s].Begin, out ulong pb) && native.Frame.Timestamps.TryRead(slotStamps[s].PreEnd, out ulong pe) && pe >= pb ? (pe - pb) / 1e6 : -1;
            SpikeLog.Report(slotFrame[s], total, median, (pre >= 0 ? $"pre-frame {pre:0.0}, " : "") + StageList(gpuFrame, 0.3) + (total - sum >= 0.3 ? $", other {total - sum:0.0}" : ""), slotCpu[s] ?? "", notes, slotCpuTotal[s]);
            slotNotes[s] = null;
        }
        gpuHead = (gpuHead + 1) % History;
        gpuCount = Math.Min(gpuCount + 1, History);
        pending[s] = false;
    }

    readonly List<float>[] runGpu = Enumerable.Range(0, Series).Select(_ => new List<float>()).ToArray();

    /// <summary>--log-spikes: per stage over the run (after the skipped frames), the GPU time's mean without the frames over 2.5 x the median (the shared card's outliers), the median, p95 and max.</summary>
    void PrintRunSummary()
    {
        var total = runGpu[Total];
        if (total.Count < 10) return;
        var sortedTotal = total.OrderBy(v => v).ToArray();
        float median = sortedTotal[sortedTotal.Length / 2];
        var keep = Enumerable.Range(0, total.Count).Where(i => total[i] <= 2.5f * median).ToArray();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"gpu-run   {total.Count} frames, {total.Count - keep.Length} over 2.5 x the median {median:0.00} ms left out of the trimmed mean; per stage ms: trimmed mean / p50 / p95 / max"));
        foreach (int s in Order)
        {
            var v = runGpu[s];
            if (v.Count == 0) continue;
            var sorted = v.OrderBy(x => x).ToArray();
            double mean = keep.Length == 0 ? 0 : keep.Average(i => v[i]);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"gpu-run     {Label(s),-12} {mean,6:0.00} {sorted[sorted.Length / 2],6:0.00} {sorted[(int)(sorted.Length * 0.95)],6:0.00} {sorted[^1],7:0.00}"));
        }
    }

    static string StageList(double[] ms, double least)
    {
        var parts = new List<string>();
        for (int k = 0; k < Stages; k++) if (ms[k] >= least) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Label(k)} {ms[k]:0.0}"));
        return string.Join(", ", parts);
    }

    /// <summary>Mean of the last <paramref name="frames"/> entries of a series.</summary>
    static double Mean(float[] series, int head, int count, int frames)
    {
        frames = Math.Min(frames, count);
        if (frames == 0) return 0;
        double sum = 0;
        for (int i = 1; i <= frames; i++) sum += series[(head - i + History) % History];
        return sum / frames;
    }

    /// <summary>Smallest 1, 2 or 5 × 10ⁿ at or above <paramref name="v"/>.</summary>
    static float NiceCeiling(float v)
    {
        if (v <= 0) return 1;
        float p = MathF.Pow(10, MathF.Floor(MathF.Log10(v)));
        foreach (float m in (ReadOnlySpan<float>)[1, 2, 5, 10]) if (m * p >= v) return m * p;
        return 10 * p;
    }

    /// <summary>The chart along the bottom of the screen: one line per stage of the <see cref="Showing"/> side, the legend with both sides' means.</summary>
    public void Draw(DebugOverlay overlay, int width, int height)
    {
        if (Showing == Mode.Off || width <= 0 || height <= 0) return;
        bool showGpu = Showing == Mode.Gpu;
        var hist = showGpu ? gpu : cpu;
        int head = showGpu ? gpuHead : cpuHead, count = showGpu ? gpuCount : cpuCount;
        const float margin = 16, pad = 12, swatch = 10;
        const int meanFrames = 30;
        float lh = overlay.LineHeight, cw = overlay.CharWidth;

        float panelH = (Order.Length + 2.5f) * lh + 2 * pad;
        float x0 = margin, x1 = width - margin, y1 = height - margin, y0 = Math.Max(y1 - panelH, margin);
        overlay.Rect(x0, y0, x1, y1, DebugOverlay.PanelColour);
        // VRAM is read every 30 frames (a driver call), shown in the header and as the pie at the top right.
        if (vramAge-- <= 0) { vram = native.Device.VideoMemory(); vramSlices = VramSlices(); vramAge = 30; }
        DrawVramPie(overlay, width);
        overlay.Text($"Profiler: {(showGpu ? "GPU" : "render thread")} ms per stage, last {History} frames   (F12: {(showGpu ? "cpu" : "off")})   " +
            $"vram {vram.Used / 1073741824.0:0.00} of {vram.Budget / 1073741824.0:0.0} GB", x0 + pad, y0 + pad, DebugOverlay.TextColour);

        // Legend at the right: swatch, stage, mean of the last frames on the CPU and the GPU.
        float legendW = swatch + 6 + (12 + 10 + 10) * cw;
        float lx = x1 - pad - legendW, ly = y0 + pad + 1.5f * lh;
        var dim = new Vector4(0.7f, 0.7f, 0.68f, 1);
        overlay.Text($"{"",-12}{"cpu",10}{"gpu",10}", lx + swatch + 6, ly, dim);
        for (int i = 0; i < Order.Length; i++)
        {
            int s = Order[i];
            float top = ly + (i + 1) * lh;
            overlay.Rect(lx, top + 4, lx + swatch, top + 4 + swatch, Colours[s]);
            string c = s == Other ? "-" : Mean(cpu[s], cpuHead, cpuCount, meanFrames).ToString("0.00", CultureInfo.InvariantCulture);
            string g = gpuCount == 0 ? "-" : Mean(gpu[s], gpuHead, gpuCount, meanFrames).ToString("0.00", CultureInfo.InvariantCulture);
            overlay.Text($"{Label(s),-12}{c,10}{g,10}", lx + swatch + 6, top, DebugOverlay.TextColour);
        }

        // The plot: y from 0 to a round number above the largest value shown, with labelled grid lines.
        float px0 = x0 + pad + 7 * cw, px1 = lx - 3 * cw, py0 = y0 + pad + 1.5f * lh + lh * 0.5f, py1 = y1 - pad - lh * 0.5f;
        if (px1 - px0 < 50 || py1 - py0 < 30) { overlay.Flush(width, height); return; }
        float max = 0;
        foreach (int s in Order)
        {
            if (!showGpu && s == Other) continue;
            for (int i = 0; i < count; i++) max = Math.Max(max, hist[s][i]);
        }
        float scale = NiceCeiling(max * 1.05f);
        var grid = new Vector4(1, 1, 1, 0.12f);
        // As many decimals as the grid step needs (10 / 4 = 2.5 needs one), so no label is rounded.
        float step = scale / 4;
        string labelFormat = MathF.Abs(step - MathF.Round(step)) < 1e-4f ? "0" : MathF.Abs(step * 10 - MathF.Round(step * 10)) < 1e-3f ? "0.0" : MathF.Abs(step * 100 - MathF.Round(step * 100)) < 1e-2f ? "0.00" : "0.000";
        for (int k = 0; k <= 4; k++)
        {
            float v = scale * k / 4, y = py1 - (py1 - py0) * k / 4;
            overlay.Rect(px0, y - 0.5f, px1, y + 0.5f, grid);
            overlay.Text(v.ToString(labelFormat, CultureInfo.InvariantCulture).PadLeft(6), x0 + pad, y - lh * 0.5f, dim);
        }
        if (count >= 2)
        {
            float dx = (px1 - px0) / (History - 1);
            // Oldest at the left; the newest frame at the right edge.
            foreach (int s in Order)
            {
                if (!showGpu && s == Other) continue;
                var series = hist[s];
                float thickness = s == Total ? 2f : 1.5f;
                float? lastX = null, lastY = null;
                for (int i = count - 1; i >= 0; i--)
                {
                    float v = series[(head - 1 - i + History) % History];
                    float x = px1 - i * dx, y = py1 - (py1 - py0) * Math.Clamp(v / scale, 0, 1);
                    if (lastX is { } fromX && lastY is { } fromY) overlay.Line(fromX, fromY, x, y, thickness, Colours[s]);
                    lastX = x; lastY = y;
                }
            }
        }
        overlay.Flush(width, height);
    }

    public void Dispose()
    {
        if (StageClock.Profiler == this) StageClock.Profiler = null;
    }
}
