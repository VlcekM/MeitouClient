using System.Diagnostics;
using System.Globalization;
using System.Text;

using Meitou.Rendering;
using Meitou.Rendering.Display;
using Meitou.Rendering.Gpu;

namespace Meitou.ModelViewer;

/// <summary>
/// The frame cost breakdown (docs/engine.md "Frame cost breakdown"; <c>MEITOU_PASS_STATS=1</c>, off otherwise): at every stage boundary of the
/// frame (<see cref="Meitou.Rendering.StageClock"/>: the stage laps, the shadow cascades, the parts of the foliage and post passes) it takes the
/// render thread's time, the difference of the native counters since the previous boundary (<see cref="GpuStats.Running"/>, the context's
/// fence wait and submit, the presenter's acquire and present) and a GPU timestamp, and sums them per row over the frames after the first
/// <c>MEITOU_PASS_STATS_SKIP</c> (default 60). A stage is a row; its parts are rows under it (their time is part of the stage's; what is left
/// over is the stage's own row "rest"). Printed at the end by <see cref="Report"/>: a table of per-frame means and one <c>PASSCSV</c> line per
/// row for scripts. The meter only reads counters and timestamps, no picture changes. (Native since phase 8 stage 3: before, VkGl's counters
/// and GL timer queries.)
/// </summary>
sealed class PassMeter : IDisposable
{
    const int Slots = 4, MaxStamps = 192;
    static readonly int Native = GpuStats.CounterNames.Length;
    static readonly int N = Native + 4;
    // The columns after the native counters: Stopwatch ticks.
    static readonly int Fence = Native, Submit = Native + 1, Acquire = Native + 2, Present = Native + 3;
    static readonly double TickMs = 1000.0 / Stopwatch.Frequency;
    /// <summary>Label of the lap that ends a frame (StageClock stage 11).</summary>
    const string EndLabel = "gpu-wait";

    sealed class Row(string key, string name, string? parent)
    {
        public readonly string Key = key, Name = name;
        public readonly string? Parent = parent;
        public double Cpu, Gpu;
        public readonly long[] C = new long[N];
        public long GpuSamples;
    }

    readonly VulkanDisplay display;
    readonly GpuContext ctx;
    readonly int skip;
    readonly string tag;
    readonly Dictionary<string, Row> rows = [];
    readonly List<Row> order = [];

    // The frame being measured.
    bool inFrame;
    int frameIndex = -1;
    long tAny, tMajor;
    readonly long[] sAny = new long[N], sMajor = new long[N], now = new long[N], endOfLast = new long[N];
    long lastEnd;
    readonly List<(string Label, double Cpu, long[] C)> pendingSubs = [];
    readonly List<(Row Row, double Cpu, long[] C)> frameRows = [];
    int measuredFrames;
    readonly List<double> frameCpu = [], betweenMs = [];
    readonly Row between = new("(between frames)", "(between frames)", null);

    // GPU stamps: a ring of frame slots in the frames' timestamp arenas, read a few frames late without waiting.
    readonly QuerySlot[,] stamps = new QuerySlot[Slots, MaxStamps];
    readonly string?[,] stampLabel = new string?[Slots, MaxStamps];
    readonly bool[,] stampSub = new bool[Slots, MaxStamps];
    readonly int[] stampCount = new int[Slots];
    readonly bool[] pending = new bool[Slots];
    readonly bool[] pendingMeasured = new bool[Slots];
    int slot;
    double gpuFrameSum;
    long gpuFrames;

    PassMeter(VulkanDisplay display)
    {
        this.display = display;
        ctx = display.Context;
        skip = int.TryParse(Environment.GetEnvironmentVariable("MEITOU_PASS_STATS_SKIP"), out int s) ? s : 60;
        tag = Environment.GetEnvironmentVariable("MEITOU_PASS_TAG") ?? "run";
        StageClock.OnStart = OnStart;
        StageClock.OnClose = OnClose;
    }

    public static PassMeter? TryCreate(VulkanDisplay display) =>
        Environment.GetEnvironmentVariable("MEITOU_PASS_STATS") == "1" ? new PassMeter(display) : null;

    void Snapshot(long[] into)
    {
        ctx.Frame.Stats.Running(into);
        into[Fence] = ctx.FenceWaitTicks;
        into[Submit] = ctx.SubmitTicks;
        into[Acquire] = display.AcquireTicks;
        into[Present] = display.PresentTicks;
    }

    void Stamp(string label, bool sub)
    {
        int n = stampCount[slot];
        if (n >= MaxStamps) return;
        var arena = ctx.Frame.Timestamps;
        // Taken inside the callback: Interleave opens the frame when none is (the benchmark starts its clock before), and an index taken
        // before that would belong to the previous frame's pool.
        QuerySlot q = default;
        ctx.Interleave(cmd =>
        {
            q = arena.Allocate();
            if (q.IsValid) cmd.Timestamp(arena, q);
        });
        if (!q.IsValid) return;
        stamps[slot, n] = q;
        stampLabel[slot, n] = label;
        stampSub[slot, n] = sub;
        stampCount[slot] = n + 1;
    }

    void OnStart()
    {
        long t = Stopwatch.GetTimestamp();
        Snapshot(now);
        if (lastEnd != 0 && frameIndex >= skip)
        {
            // Between the end of the last frame and the start of this one: the acquire, the wait for a free frame, the window's events.
            var delta = new long[N];
            for (int i = 0; i < N; i++) delta[i] = now[i] - endOfLast[i];
            between.Cpu += (t - lastEnd) * TickMs;
            for (int i = 0; i < N; i++) between.C[i] += delta[i];
            betweenMs.Add((t - lastEnd) * TickMs);
        }
        frameIndex++;
        inFrame = true;
        tAny = tMajor = t;
        Array.Copy(now, sAny, N);
        Array.Copy(now, sMajor, N);
        pendingSubs.Clear();
        frameRows.Clear();
        for (int s = 0; s < Slots; s++) Collect(s);
        slot = (slot + 1) % Slots;
        pending[slot] = false;
        stampCount[slot] = 0;
        Stamp("start", false);
    }

    void OnClose(string label, bool sub)
    {
        if (!inFrame) return;
        long t = Stopwatch.GetTimestamp();
        Snapshot(now);
        bool end = !sub && label == EndLabel;
        if (sub)
        {
            var d = new long[N];
            for (int i = 0; i < N; i++) d[i] = now[i] - sAny[i];
            pendingSubs.Add((label, (t - tAny) * TickMs, d));
            Stamp(label, true);
        }
        else
        {
            var total = new long[N];
            for (int i = 0; i < N; i++) total[i] = now[i] - sMajor[i];
            double cpu = (t - tMajor) * TickMs;
            var parent = GetRow(label, null);
            frameRows.Add((parent, cpu, total));
            // The parts, and what the stage did besides them.
            if (pendingSubs.Count > 0)
            {
                var rest = (long[])total.Clone();
                double restCpu = cpu;
                foreach (var (subLabel, subCpu, c) in pendingSubs)
                {
                    frameRows.Add((GetRow(label + "/" + subLabel, label, subLabel), subCpu, c));
                    for (int i = 0; i < N; i++) rest[i] -= c[i];
                    restCpu -= subCpu;
                }
                frameRows.Add((GetRow(label + "/rest", label, "rest"), restCpu, rest));
            }
            pendingSubs.Clear();
            if (!end) Stamp(label, false);
            tMajor = t;
            Array.Copy(now, sMajor, N);
        }
        tAny = t;
        Array.Copy(now, sAny, N);
        if (end) EndFrame(t);
    }

    Row GetRow(string key, string? parent, string? name = null)
    {
        if (!rows.TryGetValue(key, out var r))
        {
            r = new Row(key, name ?? key, parent);
            rows[key] = r;
            order.Add(r);
        }
        return r;
    }

    void EndFrame(long t)
    {
        inFrame = false;
        lastEnd = t;
        Array.Copy(now, endOfLast, N);
        pending[slot] = stampCount[slot] > 1;
        pendingMeasured[slot] = frameIndex >= skip;
        if (frameIndex < skip) return;
        measuredFrames++;
        double cpu = 0;
        foreach (var (row, c, counters) in frameRows)
        {
            row.Cpu += c;
            for (int i = 0; i < N; i++) row.C[i] += counters[i];
            if (row.Parent is null && row.Name != EndLabel) cpu += c;
            if (row.Parent is null && row.Name == EndLabel) cpu += counters[Submit] * TickMs;   // the submit
        }
        frameCpu.Add(cpu);
    }

    void Collect(int s)
    {
        if (!pending[s]) return;
        int n = stampCount[s];
        var arena = ctx.Frame.Timestamps;
        // The last stamp must be available (its frame's slot has come round); one that never will (its frame long gone) is dropped.
        if (!arena.TryRead(stamps[s, n - 1], out _))
        {
            if (ctx.Frame.Number - stamps[s, n - 1].Frame > 8) pending[s] = false;
            return;
        }
        pending[s] = false;
        if (!pendingMeasured[s]) return;
        if (!arena.TryRead(stamps[s, 0], out ulong previous)) return;
        ulong first = previous;
        double majorSum = 0;
        var subGaps = new List<(string Label, double Ms)>();
        for (int i = 1; i < n; i++)
        {
            if (!arena.TryRead(stamps[s, i], out ulong t)) t = previous;
            double ms = t >= previous ? (t - previous) / 1e6 : 0;
            previous = t;
            majorSum += ms;
            if (stampSub[s, i]) { subGaps.Add((stampLabel[s, i]!, ms)); continue; }
            string label = stampLabel[s, i]!;
            var parent = GetRow(label, null);
            parent.Gpu += majorSum;
            parent.GpuSamples++;
            double rest = majorSum;
            foreach (var (subLabel, subMs) in subGaps)
            {
                var r = GetRow(label + "/" + subLabel, label, subLabel);
                r.Gpu += subMs;
                r.GpuSamples++;
                rest -= subMs;
            }
            if (subGaps.Count > 0) { var r = GetRow(label + "/rest", label, "rest"); r.Gpu += rest; r.GpuSamples++; }
            subGaps.Clear();
            majorSum = 0;
        }
        gpuFrameSum += (previous - first) / 1e6;
        gpuFrames++;
    }

    static string Fmt(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>The table (per-frame means over the measured frames) and the PASSCSV lines.</summary>
    public void Report(TextWriter output)
    {
        for (int s = 0; s < Slots; s++) Collect(s);
        if (measuredFrames == 0) { output.WriteLine($"passes    no frames measured (the first {skip} are skipped)"); return; }
        double f = measuredFrames;
        // Per frame, like the CPU column: a stage that runs once per depth slice (terrain, objects, foliage, water) or a cascade drawn every
        // second frame is summed over the frame, not averaged per run (until 2026-10-07 it was divided by its runs, which under-counted the
        // slices' stages: docs/render-distance-benchmark.md).
        double Gpu(Row r) => r.GpuSamples > 0 && gpuFrames > 0 ? r.Gpu / gpuFrames : double.NaN;
        var sb = new StringBuilder();
        sb.AppendLine($"passes    {tag}: means per frame over {measuredFrames} frames (first {skip} skipped), {gpuFrames} with GPU times; Stopwatch CPU ms of the render thread; GPU ms between timestamps");
        sb.AppendLine($"passes    {"row",-28}{"cpu",7}{"gpu",7}{"draws",7}{"indir",6}{"disp",6}{"pipes",6}{"push",6}{"segs",6}{"constKB",8}{"uplKB",7}{"fence",7}{"submit",7}{"acq",6}{"pres",6}");
        void Line(Row r, string prefix)
        {
            var c = r.C;
            double g = Gpu(r);
            sb.AppendLine($"passes    {prefix + r.Name,-28}{Fmt(r.Cpu / f),7}{(double.IsNaN(g) ? "-" : Fmt(g)),7}{c[0] / f,7:0}{c[1] / f,6:0}{c[2] / f,6:0}{c[3] / f,6:0}{c[4] / f,6:0}{c[5] / f,6:0}" +
                $"{c[6] / 1024.0 / f,8:0.0}{c[7] / 1024.0 / f,7:0.0}{Fmt(c[Fence] * TickMs / f),7}{Fmt(c[Submit] * TickMs / f),7}{Fmt(c[Acquire] * TickMs / f),6}{Fmt(c[Present] * TickMs / f),6}");
        }
        foreach (var r in order.Where(r => r.Parent is null))
        {
            Line(r, "");
            foreach (var sub in order.Where(x => x.Parent == r.Key)) Line(sub, "  ");
        }
        if (between.Cpu > 0) Line(between, "");
        var total = new Row("TOTAL", "TOTAL", null);
        foreach (var r in order.Where(r => r.Parent is null)) { total.Cpu += r.Cpu; for (int i = 0; i < N; i++) total.C[i] += r.C[i]; if (!double.IsNaN(Gpu(r))) total.Gpu += Gpu(r) * f; }
        total.GpuSamples = (long)f;
        Line(total, "");
        var sorted = frameCpu.OrderBy(x => x).ToList();
        double gpuFrame = gpuFrames > 0 ? gpuFrameSum / gpuFrames : double.NaN;
        sb.AppendLine($"passes    frame render-thread cpu (rows but {EndLabel}, plus the submit): mean {Fmt(frameCpu.Average())}, p50 {Fmt(sorted[sorted.Count / 2])}, p95 {Fmt(sorted[Math.Min((int)(sorted.Count * 0.95), sorted.Count - 1)])} ms; gpu frame (first to last timestamp) {Fmt(gpuFrame)} ms");
        output.Write(sb.ToString());
        // For scripts: PASSCSV|tag|row|parent|cpu|gpu|counters... (per-frame means; the tick columns in ms).
        output.WriteLine($"PASSCSVHEAD|{string.Join("|", GpuStats.CounterNames)}|fenceMs|submitMs|acquireMs|presentMs");
        void Csv(Row r)
        {
            var parts = new string[N];
            for (int i = 0; i < N; i++)
                parts[i] = (i >= Fence ? r.C[i] * TickMs / f : r.C[i] / f).ToString("0.###", CultureInfo.InvariantCulture);
            double g = Gpu(r);
            output.WriteLine($"PASSCSV|{tag}|{r.Key}|{r.Parent}|{r.Cpu / f:0.####}|{(double.IsNaN(g) ? "" : g.ToString("0.####", CultureInfo.InvariantCulture))}|{string.Join("|", parts)}");
        }
        foreach (var r in order) Csv(r);
        if (between.Cpu > 0) Csv(between);
        output.WriteLine($"PASSFRAME|{tag}|{measuredFrames}|{frameCpu.Average():0.####}|{sorted[sorted.Count / 2]:0.####}|{sorted[Math.Min((int)(sorted.Count * 0.95), sorted.Count - 1)]:0.####}|{gpuFrame:0.####}");
    }

    /// <summary>
    /// <c>MEITOU_VK_MICRO=1</c>: what one <c>vkCmd*</c> call costs the render thread, from a loop of 100 000 recorded into a native segment of the
    /// open frame (outside a rendering: state-setting commands only): through Silk.NET's wrapper and straight through the driver's function
    /// pointer, against an empty loop. The difference is Silk.NET's dispatch; the raw call is the driver's recording cost.
    /// </summary>
    public static unsafe void VkCallMicro(GpuContext ctx, TextWriter output)
    {
        var vk = ctx.Device.Vk;
        var dev = ctx.Device.Device;
        const int n = 100_000;
        var list = ctx.BeginNative("vkCmd micro");
        var cb = list.Handle;
        var rect = new Silk.NET.Vulkan.Rect2D(new Silk.NET.Vulkan.Offset2D(0, 0), new Silk.NET.Vulkan.Extent2D(800, 600));
        var scissor = (delegate* unmanaged[Cdecl]<Silk.NET.Vulkan.CommandBuffer, uint, uint, Silk.NET.Vulkan.Rect2D*, void>)vk.GetDeviceProcAddr(dev, "vkCmdSetScissor").Handle;
        var cull = (delegate* unmanaged[Cdecl]<Silk.NET.Vulkan.CommandBuffer, Silk.NET.Vulkan.CullModeFlags, void>)vk.GetDeviceProcAddr(dev, "vkCmdSetCullMode").Handle;
        double Ns(long ticks) => ticks * TickMs * 1e6 / n;
        var r = rect;
        long t0, empty = 0, silkScissor = 0, rawScissor = 0, silkCull = 0, rawCull = 0;
        for (int pass = 0; pass < 2; pass++)   // the second pass is the one kept (warm)
        {
            t0 = Stopwatch.GetTimestamp(); for (int i = 0; i < n; i++) GC.KeepAlive(null); empty = Stopwatch.GetTimestamp() - t0;
            t0 = Stopwatch.GetTimestamp(); for (int i = 0; i < n; i++) vk.CmdSetScissor(cb, 0, 1, &r); silkScissor = Stopwatch.GetTimestamp() - t0;
            t0 = Stopwatch.GetTimestamp(); for (int i = 0; i < n; i++) scissor(cb, 0, 1, &r); rawScissor = Stopwatch.GetTimestamp() - t0;
            t0 = Stopwatch.GetTimestamp(); for (int i = 0; i < n; i++) vk.CmdSetCullMode(cb, Silk.NET.Vulkan.CullModeFlags.BackBit); silkCull = Stopwatch.GetTimestamp() - t0;
            t0 = Stopwatch.GetTimestamp(); for (int i = 0; i < n; i++) cull(cb, Silk.NET.Vulkan.CullModeFlags.BackBit); rawCull = Stopwatch.GetTimestamp() - t0;
        }
        list.Invalidate();   // the segment's own state tracking knows nothing of the raw calls
        ctx.EndNative(list);
        output.WriteLine($"micro     vkCmd call, ns each over {n} calls (empty loop {Ns(empty):0.0}): vkCmdSetScissor Silk.NET {Ns(silkScissor):0.0}, raw pointer {Ns(rawScissor):0.0}; vkCmdSetCullMode Silk.NET {Ns(silkCull):0.0}, raw {Ns(rawCull):0.0}");
        output.WriteLine($"MICROCSV|{Ns(empty):0.0}|{Ns(silkScissor):0.0}|{Ns(rawScissor):0.0}|{Ns(silkCull):0.0}|{Ns(rawCull):0.0}");
    }

    public void Dispose() { StageClock.OnStart = null; StageClock.OnClose = null; }
}
