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
    const int Slots = 4, MaxStamps = 128, Stages = 13;
    // Series: the stages by StageClock index, then the GPU time outside the stamped stages (uploads, overlay), then the total.
    const int Other = Stages, Total = Stages + 1, Series = Stages + 2;
    // The stages in the order a frame runs them; 11 is the overlay, submit and present after the scene.
    static readonly int[] Order = [0, 1, 2, 3, 12, 4, 5, 6, 7, 8, 9, 10, 11, Other, Total];

    static readonly Vector4[] Colours =
    [
        new(0.55f, 0.75f, 1.00f, 1), new(0.35f, 0.55f, 0.95f, 1), new(0.60f, 0.45f, 0.95f, 1), new(0.95f, 0.85f, 0.35f, 1),
        new(0.25f, 0.80f, 0.85f, 1), new(0.95f, 0.55f, 0.85f, 1), new(0.65f, 0.65f, 0.65f, 1), new(0.95f, 0.55f, 0.25f, 1),
        new(0.40f, 0.85f, 0.40f, 1), new(0.30f, 0.60f, 1.00f, 1), new(0.95f, 0.35f, 0.35f, 1), new(0.55f, 0.55f, 0.75f, 1),
        new(0.45f, 0.35f, 0.25f, 1), new(0.50f, 0.50f, 0.50f, 1), new(1.00f, 1.00f, 1.00f, 1),
    ];

    readonly IGl gl;
    readonly Func<double>? gpuFrameMs;
    readonly uint[,] stamps = new uint[Slots, MaxStamps];
    readonly int[,] stampStage = new int[Slots, MaxStamps];
    readonly int[] stampCount = new int[Slots];
    readonly bool[] pending = new bool[Slots];
    int slot;

    readonly float[][] cpu = NewHistory(), gpu = NewHistory();
    int cpuHead, cpuCount, gpuHead, gpuCount;
    readonly double[] gpuFrame = new double[Series];

    public Mode Showing { get; set; }

    /// <summary>Frames whose GPU stage times have been read (at most <see cref="History"/>).</summary>
    public int GpuFrames => gpuCount;

    /// <summary>The newest frame's GPU time over its stamped stages, in ms (0 before the first frame is read).</summary>
    public double LastGpuMs => gpuCount == 0 ? 0 : gpu[Total][(gpuHead - 1 + History) % History];

    /// <param name="gpuFrameMs">The whole frame's GPU time, when the backend measures it (it includes the uploads before the first stage).</param>
    public FrameProfiler(IGl gl, GpuContext gpu, Func<double>? gpuFrameMs = null)
    {
        this.gl = gl;
        this.gpuFrameMs = gpuFrameMs;
        native = gpu.Interop is not null ? gpu : null;
        recordStamp = cmd => cmd.Timestamp(native!.Frame.Timestamps, pendingStamp);
        if (native is null)
            for (int s = 0; s < Slots; s++)
                for (int i = 0; i < MaxStamps; i++) stamps[s, i] = gl.GenQuery();
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
        pending[slot] = false;   // still not ready after a full round: dropped
        stampCount[slot] = 0;
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
        double sum = 0;
        for (int s = 0; s < Stages; s++)
        {
            cpu[s][cpuHead] = (float)StageClock.Ms[s];
            sum += StageClock.Ms[s];
        }
        cpu[Other][cpuHead] = 0;
        cpu[Total][cpuHead] = (float)sum;
        cpuHead = (cpuHead + 1) % History;
        cpuCount = Math.Min(cpuCount + 1, History);
    }

    internal void Stamp(int stage)
    {
        int n = stampCount[slot];
        if (n >= MaxStamps) return;
        if (native is { } ctx)
        {
            // The native timestamps (QueryArena), recorded through the seam without ending VkGl's pass: they keep working while the
            // stages move to native code (docs/renderer-native.md 7.1 step 9).
            var arena = ctx.Frame.Timestamps;
            var q = arena.Allocate();
            if (!q.IsValid) return;
            pendingStamp = q;
            ctx.Interop!.Interleave(recordStamp);
            nativeStamps[slot, n] = q;
        }
        else gl.QueryCounter(stamps[slot, n], QueryCounterTarget.Timestamp);
        stampStage[slot, n] = stage;
        stampCount[slot] = n + 1;
    }

    // Native timestamps: the context while VkGl provides the seam, the slots per stamp, and the record callback (no allocation per stamp).
    readonly GpuContext? native;
    readonly QuerySlot[,] nativeStamps = new QuerySlot[Slots, MaxStamps];
    QuerySlot pendingStamp;
    readonly Action<CommandList> recordStamp;

    bool TryStamp(int s, int i, out ulong ns)
    {
        if (native is { } ctx) return ctx.Frame.Timestamps.TryRead(nativeStamps[s, i], out ns);
        gl.GetQueryObject(stamps[s, i], QueryObjectParameterName.ResultAvailable, out int ready);
        ns = 0;
        if (ready == 0) return false;
        gl.GetQueryObject(stamps[s, i], QueryObjectParameterName.Result, out ns);
        return true;
    }

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
        double total = gpuFrameMs?.Invoke() is > 0 and var t ? Math.Max(t, sum) : sum;
        for (int k = 0; k < Stages; k++) gpu[k][gpuHead] = (float)gpuFrame[k];
        gpu[Other][gpuHead] = (float)(total - sum);
        gpu[Total][gpuHead] = (float)total;
        gpuHead = (gpuHead + 1) % History;
        gpuCount = Math.Min(gpuCount + 1, History);
        pending[s] = false;
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
        overlay.Text($"Profiler: {(showGpu ? "GPU" : "render thread")} ms per stage, last {History} frames   (F12: {(showGpu ? "cpu" : "off")})", x0 + pad, y0 + pad, DebugOverlay.TextColour);

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
        if (native is null) foreach (uint q in stamps) gl.DeleteQuery(q);
    }
}
