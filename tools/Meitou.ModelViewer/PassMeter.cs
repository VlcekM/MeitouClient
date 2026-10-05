using System.Diagnostics;
using System.Globalization;
using System.Text;

using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan;

namespace Meitou.ModelViewer;

/// <summary>
/// The frame cost breakdown (docs/engine.md "Frame cost breakdown"; <c>MEITOU_PASS_STATS=1</c>, off otherwise): at every stage boundary of the
/// frame (<see cref="Meitou.Rendering.StageClock"/>: the stage laps, the shadow cascades, the parts of the foliage and post passes) it takes the
/// render thread's time and the difference of <see cref="VkGlStats"/>'s counters since the previous boundary, and a GPU timestamp, and sums them
/// per row over the frames after the first <c>MEITOU_PASS_STATS_SKIP</c> (default 60). A stage is a row; its parts are rows under it (their
/// time is part of the stage's; what is left over is the stage's own row "rest"). Printed at the end by <see cref="Report"/>: a table
/// of per-frame means and one <c>PASSCSV</c> line per row for scripts. The meter only reads counters and timestamps, no picture changes.
/// </summary>
sealed class PassMeter : IDisposable
{
    const int Slots = 4, MaxStamps = 192, N = VkGlStats.CounterCount;
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

    readonly IGl gl;
    readonly VkGl vk;
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

    // GPU stamps: a ring of frame slots, read a few frames late without waiting.
    readonly uint[,] stamps = new uint[Slots, MaxStamps];
    readonly string?[,] stampLabel = new string?[Slots, MaxStamps];
    readonly bool[,] stampSub = new bool[Slots, MaxStamps];
    readonly int[] stampCount = new int[Slots];
    readonly bool[] pending = new bool[Slots];
    readonly bool[] pendingMeasured = new bool[Slots];
    int slot;
    double gpuFrameSum;
    long gpuFrames;

    public PassMeter(IGl gl, VkGl vk)
    {
        this.gl = gl;
        this.vk = vk;
        skip = int.TryParse(Environment.GetEnvironmentVariable("MEITOU_PASS_STATS_SKIP"), out int s) ? s : 60;
        tag = Environment.GetEnvironmentVariable("MEITOU_PASS_TAG") ?? "run";
        for (int i = 0; i < Slots; i++)
            for (int k = 0; k < MaxStamps; k++) stamps[i, k] = gl.GenQuery();
        StageClock.OnStart = OnStart;
        StageClock.OnClose = OnClose;
    }

    public static PassMeter? TryCreate(IGl gl) =>
        Environment.GetEnvironmentVariable("MEITOU_PASS_STATS") == "1" && gl is VkGl vk ? new PassMeter(gl, vk) : null;

    void Stamp(string label, bool sub)
    {
        int n = stampCount[slot];
        if (n >= MaxStamps) return;
        gl.QueryCounter(stamps[slot, n], QueryCounterTarget.Timestamp);
        stampLabel[slot, n] = label;
        stampSub[slot, n] = sub;
        stampCount[slot] = n + 1;
    }

    void OnStart()
    {
        long t = Stopwatch.GetTimestamp();
        vk.Stats.Snapshot(now);
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
        vk.Stats.Snapshot(now);
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
            if (row.Parent is null && row.Name == EndLabel) cpu += counters[27] * TickMs;   // the submit
        }
        frameCpu.Add(cpu);
    }

    void Collect(int s)
    {
        if (!pending[s]) return;
        int n = stampCount[s];
        // The last stamp must be available.
        gl.GetQueryObject(stamps[s, n - 1], QueryObjectParameterName.ResultAvailable, out int ready);
        if (ready == 0) return;
        pending[s] = false;
        if (!pendingMeasured[s]) return;
        gl.GetQueryObject(stamps[s, 0], QueryObjectParameterName.Result, out ulong previous);
        ulong first = previous;
        double majorSum = 0;
        var subGaps = new List<(string Label, double Ms)>();
        for (int i = 1; i < n; i++)
        {
            gl.GetQueryObject(stamps[s, i], QueryObjectParameterName.Result, out ulong t);
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
        double Gpu(Row r) => r.GpuSamples > 0 ? r.Gpu / r.GpuSamples : double.NaN;
        var sb = new StringBuilder();
        sb.AppendLine($"passes    {tag}: means per frame over {measuredFrames} frames (first {skip} skipped), {gpuFrames} with GPU times; Stopwatch CPU ms of the render thread; GPU ms between timestamps");
        sb.AppendLine($"passes    {"row",-28}{"cpu",7}{"gpu",7}{"prep",6}{"draws",7}{"pbind",6}{"newpl",6}{"uniKB",7}{"ucopy",6}{"push",6}{"dwrit",6}{"tex",6}{"skip",6}{"vbuf",6}{"dyn",6}{"set1",6}{"pass",6}{"bar",5}{"glst",6}{"gluni",6}{"gltex",6}{"glbnd",6}{"glatt",6}{"fence",7}{"submit",7}{"acq",6}{"pres",6}");
        void Line(Row r, string prefix)
        {
            var c = r.C;
            double g = Gpu(r);
            sb.AppendLine($"passes    {prefix + r.Name,-28}{Fmt(r.Cpu / f),7}{(double.IsNaN(g) ? "-" : Fmt(g)),7}{Fmt(c[24] * TickMs / f),6}{c[0] / f,7:0}{c[9] / f,6:0}{c[2] / f,6:0.0}{c[8] / 1024.0 / f,7:0.0}{c[12] / f,6:0}{c[7] / f,6:0}{c[13] / f,6:0}{c[14] / f,6:0}{c[15] / f,6:0}{c[16] / f,6:0}{c[10] / f,6:0}{c[11] / f,6:0}{c[1] / f,6:0.0}{c[18] / f,5:0}{c[19] / f,6:0}{c[20] / f,6:0}{c[21] / f,6:0}{c[22] / f,6:0}{c[23] / f,6:0}{Fmt(c[26] * TickMs / f),7}{Fmt(c[27] * TickMs / f),7}{Fmt(c[28] * TickMs / f),6}{Fmt(c[29] * TickMs / f),6}");
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
        // For scripts: PASSCSV|tag|row|parent|cpu|gpu|counters... (per-frame means; ticks columns in ms).
        output.WriteLine($"PASSCSVHEAD|{string.Join("|", VkGlStats.CounterNames)}");
        void Csv(Row r)
        {
            var parts = new string[N];
            for (int i = 0; i < N; i++)
                parts[i] = (i >= 24 ? r.C[i] * TickMs / f : r.C[i] / f).ToString("0.###", CultureInfo.InvariantCulture);
            double g = Gpu(r);
            output.WriteLine($"PASSCSV|{tag}|{r.Key}|{r.Parent}|{r.Cpu / f:0.####}|{(double.IsNaN(g) ? "" : g.ToString("0.####", CultureInfo.InvariantCulture))}|{string.Join("|", parts)}");
        }
        foreach (var r in order) Csv(r);
        if (between.Cpu > 0) Csv(between);
        output.WriteLine($"PASSFRAME|{tag}|{measuredFrames}|{frameCpu.Average():0.####}|{sorted[sorted.Count / 2]:0.####}|{sorted[Math.Min((int)(sorted.Count * 0.95), sorted.Count - 1)]:0.####}|{gpuFrame:0.####}");
    }

    /// <summary>The per-draw split of <see cref="VkGl.Phases"/> (<c>MEITOU_VKGL_PHASES=1</c>): ticks of each part of the draw preparation over the measured frames, per draw.</summary>
    public void ReportPhases(TextWriter output, long drawsMeasured)
    {
        if (!VkGl.Phases || drawsMeasured == 0) return;
        double total = 0;
        long[] ticks = (long[])vk.Stats.PhaseTicks.Clone();
        ticks[1] -= vk.Stats.PipelineCreateTicks;   // pipeline creation (warm-up) is not the lookup
        for (int i = 0; i < ticks.Length; i++) total += ticks[i];
        output.WriteLine($"phases    {tag}: per draw (µs, over {drawsMeasured} draws including the warm-up frames); 'in vkCmd' is the part inside the vkCmd* calls (Silk.NET dispatch + driver); the stopwatch reads add ~0.05 µs each");
        double native = 0;
        for (int i = 0; i < VkGlStats.PhaseNames.Length; i++)
        {
            double us = ticks[i] * TickMs * 1000.0 / drawsMeasured, nat = vk.Stats.NativeTicks[i] * TickMs * 1000.0 / drawsMeasured;
            native += nat;
            output.WriteLine($"phases    {VkGlStats.PhaseNames[i],-26}{us,7:0.000} µs   in vkCmd {nat,7:0.000} µs");
            output.WriteLine($"PHASECSV|{tag}|{VkGlStats.PhaseNames[i]}|{us:0.0000}|{nat:0.0000}");
        }
        output.WriteLine($"phases    {"total",-26}{total * TickMs * 1000.0 / drawsMeasured,7:0.000} µs   in vkCmd {native,7:0.000} µs");
        output.WriteLine($"PHASECSV|{tag}|total|{total * TickMs * 1000.0 / drawsMeasured:0.0000}|{native:0.0000}");
    }

    /// <summary>
    /// <c>MEITOU_VK_MICRO=1</c>: what one <c>vkCmd*</c> call costs the render thread, from a loop of 100 000 recorded into the open frame (outside a render
    /// pass: state-setting commands only): through Silk.NET's wrapper and straight through the driver's function pointer, against an empty loop.
    /// The difference is Silk.NET's dispatch; the raw call is the driver's recording cost.
    /// </summary>
    public static unsafe void VkCallMicro(VkGl gl, TextWriter output)
    {
        var vk = gl.Device.Vk;
        var dev = gl.Device.Device;
        const int n = 100_000;
        var cb = gl.BeginExternal();
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
        gl.EndExternal();
        output.WriteLine($"micro     vkCmd call, ns each over {n} calls (empty loop {Ns(empty):0.0}): vkCmdSetScissor Silk.NET {Ns(silkScissor):0.0}, raw pointer {Ns(rawScissor):0.0}; vkCmdSetCullMode Silk.NET {Ns(silkCull):0.0}, raw {Ns(rawCull):0.0}");
        output.WriteLine($"MICROCSV|{Ns(empty):0.0}|{Ns(silkScissor):0.0}|{Ns(rawScissor):0.0}|{Ns(silkCull):0.0}|{Ns(rawCull):0.0}");
    }

    public void Dispose() { StageClock.OnStart = null; StageClock.OnClose = null; }
}
