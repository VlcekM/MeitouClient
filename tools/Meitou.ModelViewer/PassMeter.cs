using System.Diagnostics;
using System.Globalization;
using System.Text;

using Meitou.Game.Rendering.Display;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Profiling;

namespace Meitou.ModelViewer;

/// <summary>
/// The frame cost breakdown (docs/engine.md "Frame cost breakdown"; <c>MEITOU_PASS_STATS=1</c>, off otherwise): at every stage boundary of the frame
/// (<see cref="Meitou.Game.Rendering.Profiling.StageClock"/>: the stage laps, the shadow cascades, the parts of the foliage and post passes) it takes
/// the render thread's time, the difference of the native counters since the previous boundary (<see cref="GpuStats.Running"/>, the context's fence
/// wait and submit, the presenter's acquire and present) and a GPU timestamp, and sums them per row over the frames after the first
/// <c>MEITOU_PASS_STATS_SKIP</c> (default 60). A stage is a row; its parts are rows under it (their time is part of the stage's; what is left over is
/// the stage's own row "rest"). Printed at the end by <see cref="Report"/>: a table of per-frame means and one <c>PASSCSV</c> line per row for
/// scripts. The meter only reads counters and timestamps, no picture changes. (Native since phase 8 stage 3: before, VkGl's counters and GL timer
/// queries.)
/// </summary>
sealed class PassMeter : IDisposable
{
    #region Meter

    /// <summary>
    /// The frame slots of the GPU stamp ring, and the stamps one frame may take.
    /// </summary>
    const int Slots = 4, MaxStamps = 192;

    /// <summary>
    /// How many native counters <see cref="GpuStats"/> keeps.
    /// </summary>
    static readonly int Native = GpuStats.CounterNames.Length;

    /// <summary>
    /// The counter columns: the native ones and the four Stopwatch tick columns after them.
    /// </summary>
    static readonly int N = Native + 4;

    /// <summary>
    /// The columns after the native counters: Stopwatch ticks.
    /// </summary>
    static readonly int Fence = Native, Submit = Native + 1, Acquire = Native + 2, Present = Native + 3;

    /// <summary>
    /// Milliseconds per Stopwatch tick.
    /// </summary>
    static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Label of the final CPU lap that closes a frame.
    /// </summary>
    const string EndLabel = "gpu-wait";

    /// <summary>
    /// One row of the table: a stage or a part of one (its parent), with its summed render-thread time, GPU time and counter deltas.
    /// </summary>
    sealed class Row(string key, string name, string? parent)
    {
        /// <summary>
        /// The row's key ("stage" or "stage/part") and its printed name.
        /// </summary>
        public readonly string Key = key, Name = name;

        /// <summary>
        /// The stage a part belongs to; null for a stage.
        /// </summary>
        public readonly string? Parent = parent;

        /// <summary>
        /// The summed render-thread and GPU milliseconds.
        /// </summary>
        public double Cpu, Gpu;

        /// <summary>
        /// The summed counter deltas, one per column.
        /// </summary>
        public readonly long[] C = new long[N];

        /// <summary>
        /// The frames whose GPU time went into <see cref="Gpu"/>.
        /// </summary>
        public long GpuSamples;
    }

    /// <summary>
    /// The display whose acquire and present ticks are counted.
    /// </summary>
    readonly VulkanDisplay _display;

    /// <summary>
    /// The context whose counters, fence waits, submits and timestamps are read.
    /// </summary>
    readonly GpuContext _ctx;

    /// <summary>
    /// The frames left out at the start (<c>MEITOU_PASS_STATS_SKIP</c>).
    /// </summary>
    readonly int _skip;

    /// <summary>
    /// The label of the PASSCSV and PASSFRAME lines.
    /// </summary>
    const string Tag = "run";

    /// <summary>
    /// Every row by key.
    /// </summary>
    readonly Dictionary<string, Row> _rows = [];

    /// <summary>
    /// The rows in the order they first appeared.
    /// </summary>
    readonly List<Row> _order = [];

    /// <summary>
    /// Hooks the meter into <see cref="StageClock"/>'s stage starts and closes.
    /// </summary>
    PassMeter(VulkanDisplay display)
    {
        this._display = display;
        _ctx = display.Context;
        _skip = int.TryParse(Environment.GetEnvironmentVariable("MEITOU_PASS_STATS_SKIP"), out int s) ? s : 60;
        StageClock.OnStart = OnStart;
        StageClock.OnClose = OnClose;
    }

    /// <summary>
    /// A meter when <c>MEITOU_PASS_STATS=1</c>, else null.
    /// </summary>
    public static PassMeter? TryCreate(VulkanDisplay display) =>
        Environment.GetEnvironmentVariable("MEITOU_PASS_STATS") == "1" ? new PassMeter(display) : null;

    /// <summary>
    /// Unhooks the meter from <see cref="StageClock"/>.
    /// </summary>
    public void Dispose()
    {
        StageClock.OnStart = null;
        StageClock.OnClose = null;
    }

    #endregion

    #region Frame measuring

    // The frame being measured.
    /// <summary>
    /// Inside a frame (between the frame's start and its end label).
    /// </summary>
    bool _inFrame;

    /// <summary>
    /// The frame's index since the meter started; -1 before the first.
    /// </summary>
    int _frameIndex = -1;

    /// <summary>
    /// The Stopwatch time of the last boundary (any) and of the last stage boundary.
    /// </summary>
    long _tAny, _tMajor;

    /// <summary>
    /// The counters at the last boundary (any) and at the last stage boundary, now, and at the end of the last frame.
    /// </summary>
    readonly long[] _sAny = new long[N], _sMajor = new long[N], _now = new long[N], _endOfLast = new long[N];

    /// <summary>
    /// The Stopwatch time the last frame ended.
    /// </summary>
    long _lastEnd;

    /// <summary>
    /// The parts closed since the last stage boundary: label, render-thread time and counter deltas.
    /// </summary>
    readonly List<(string Label, double Cpu, long[] C)> _pendingSubs = [];

    /// <summary>
    /// The rows the frame closed, with their time and deltas; added to the rows when the frame ends.
    /// </summary>
    readonly List<(Row Row, double Cpu, long[] C)> _frameRows = [];

    /// <summary>
    /// The frames measured (after the skipped ones).
    /// </summary>
    int _measuredFrames;

    /// <summary>
    /// Each measured frame's render-thread time over the top-level stages, and each gap between frames.
    /// </summary>
    readonly List<double> _frameCpu = [], _betweenMs = [];

    /// <summary>
    /// The row of the time between frames (the acquire, the wait for a free frame, the window's events).
    /// </summary>
    readonly Row _between = new("(between frames)", "(between frames)", null);

    /// <summary>
    /// Reads the native counters, the fence wait and submit ticks and the presenter's acquire and present ticks into <paramref name="into"/>.
    /// </summary>
    void Snapshot(long[] into)
    {
        _ctx.Frame.Stats.Running(into);
        into[Fence] = _ctx.FenceWaitTicks;
        into[Submit] = _ctx.SubmitTicks;
        into[Acquire] = _display.AcquireTicks;
        into[Present] = _display.PresentTicks;
    }

    /// <summary>
    /// A frame's first stage starts: books the gap since the last frame, resets the boundaries, collects the finished GPU slots and opens the next
    /// slot with a stamp.
    /// </summary>
    void OnStart()
    {
        long t = Stopwatch.GetTimestamp();
        Snapshot(_now);
        if (_lastEnd != 0 && _frameIndex >= _skip)
        {
            // Between the end of the last frame and the start of this one: the acquire, the wait for a free frame, the window's events.
            var delta = new long[N];
            for (int i = 0; i < N; i++) delta[i] = _now[i] - _endOfLast[i];
            _between.Cpu += (t - _lastEnd) * TickMs;
            for (int i = 0; i < N; i++) _between.C[i] += delta[i];
            _betweenMs.Add((t - _lastEnd) * TickMs);
        }
        _frameIndex++;
        _inFrame = true;
        _tAny = _tMajor = t;
        Array.Copy(_now, _sAny, N);
        Array.Copy(_now, _sMajor, N);
        _pendingSubs.Clear();
        _frameRows.Clear();
        for (int s = 0; s < Slots; s++) Collect(s);
        _slot = (_slot + 1) % Slots;
        _pending[_slot] = false;
        _stampCount[_slot] = 0;
        Stamp("start", false);
    }

    /// <summary>
    /// A stage (or a part, <paramref name="sub"/>) closes: its render-thread time and counter deltas go to its row (a stage's parts with its "rest"),
    /// with a GPU stamp; the end label ends the frame.
    /// </summary>
    void OnClose(string label, bool sub)
    {
        if (!_inFrame) return;
        long t = Stopwatch.GetTimestamp();
        Snapshot(_now);
        bool end = !sub && label == EndLabel;
        if (sub)
        {
            var d = new long[N];
            for (int i = 0; i < N; i++) d[i] = _now[i] - _sAny[i];
            _pendingSubs.Add((label, (t - _tAny) * TickMs, d));
            Stamp(label, true);
        }
        else
        {
            var total = new long[N];
            for (int i = 0; i < N; i++) total[i] = _now[i] - _sMajor[i];
            double cpu = (t - _tMajor) * TickMs;
            var parent = GetRow(label, null);
            _frameRows.Add((parent, cpu, total));
            // The parts, and what the stage did besides them.
            if (_pendingSubs.Count > 0)
            {
                var rest = (long[])total.Clone();
                double restCpu = cpu;
                foreach (var (subLabel, subCpu, c) in _pendingSubs)
                {
                    _frameRows.Add((GetRow(label + "/" + subLabel, label, subLabel), subCpu, c));
                    for (int i = 0; i < N; i++) rest[i] -= c[i];
                    restCpu -= subCpu;
                }
                _frameRows.Add((GetRow(label + "/rest", label, "rest"), restCpu, rest));
            }
            _pendingSubs.Clear();
            if (!end && _ctx.Frame.Open) Stamp(label, false);
            _tMajor = t;
            Array.Copy(_now, _sMajor, N);
        }
        _tAny = t;
        Array.Copy(_now, _sAny, N);
        if (end) EndFrame(t);
    }

    /// <summary>
    /// The row of <paramref name="key"/>, made (and listed) on first use.
    /// </summary>
    Row GetRow(string key, string? parent, string? name = null)
    {
        if (!_rows.TryGetValue(key, out var r))
        {
            r = new Row(key, name ?? key, parent);
            _rows[key] = r;
            _order.Add(r);
        }
        return r;
    }

    /// <summary>
    /// Ends a frame: marks its stamp slot pending and, once past the skipped frames, adds the frame's rows to the sums.
    /// </summary>
    void EndFrame(long t)
    {
        _inFrame = false;
        _lastEnd = t;
        Array.Copy(_now, _endOfLast, N);
        _pending[_slot] = _stampCount[_slot] > 1;
        _pendingMeasured[_slot] = _frameIndex >= _skip;
        if (_frameIndex < _skip) return;
        _measuredFrames++;
        double cpu = 0;
        foreach (var (row, c, counters) in _frameRows)
        {
            row.Cpu += c;
            for (int i = 0; i < N; i++) row.C[i] += counters[i];
            if (row.Parent is null && row.Name is not (EndLabel or "driver-wait")) cpu += c;
        }
        _frameCpu.Add(cpu);
    }

    #endregion

    #region GPU stamps

    // GPU stamps: a ring of frame slots in the frames' timestamp arenas, read a few frames late without waiting.
    /// <summary>
    /// The stamps of each slot, in order.
    /// </summary>
    readonly QuerySlot[,] _stamps = new QuerySlot[Slots, MaxStamps];

    /// <summary>
    /// Each stamp's stage or part label.
    /// </summary>
    readonly string?[,] _stampLabel = new string?[Slots, MaxStamps];

    /// <summary>
    /// Whether each stamp closes a part (else a stage).
    /// </summary>
    readonly bool[,] _stampSub = new bool[Slots, MaxStamps];

    /// <summary>
    /// The stamps each slot holds.
    /// </summary>
    readonly int[] _stampCount = new int[Slots];

    /// <summary>
    /// Whether a slot waits to be read.
    /// </summary>
    readonly bool[] _pending = new bool[Slots];

    /// <summary>
    /// Whether a slot's frame counts (past the skipped ones).
    /// </summary>
    readonly bool[] _pendingMeasured = new bool[Slots];

    /// <summary>
    /// The slot the frame records into.
    /// </summary>
    int _slot;

    /// <summary>
    /// The summed GPU time of the read frames, first stamp to last, milliseconds.
    /// </summary>
    double _gpuFrameSum;

    /// <summary>
    /// The frames <see cref="_gpuFrameSum"/> holds.
    /// </summary>
    long _gpuFrames;

    /// <summary>
    /// Writes a GPU timestamp for <paramref name="label"/> into the frame's slot (nothing when the slot is full or the arena has no room).
    /// </summary>
    void Stamp(string label, bool sub)
    {
        int n = _stampCount[_slot];
        if (n >= MaxStamps) return;
        var arena = _ctx.Frame.Timestamps;
        // Taken inside the callback: Interleave opens the frame when none is (the benchmark starts its clock before), and an index taken
        // before that would belong to the previous frame's pool.
        QuerySlot q = default;
        _ctx.Interleave(cmd =>
        {
            q = arena.Allocate();
            if (q.IsValid) cmd.Timestamp(arena, q);
        });
        if (!q.IsValid) return;
        _stamps[_slot, n] = q;
        _stampLabel[_slot, n] = label;
        _stampSub[_slot, n] = sub;
        _stampCount[_slot] = n + 1;
    }

    /// <summary>
    /// Reads slot <paramref name="s"/> once its last stamp is available: each stage's and part's GPU time goes to its row (a stage with parts also
    /// gets its "rest").
    /// </summary>
    void Collect(int s)
    {
        if (!_pending[s]) return;
        int n = _stampCount[s];
        var arena = _ctx.Frame.Timestamps;
        // The last stamp must be available (its frame's slot has come round); one that never will (its frame long gone) is dropped.
        if (!arena.TryRead(_stamps[s, n - 1], out _))
        {
            if (_ctx.Frame.Number - _stamps[s, n - 1].Frame > 8) _pending[s] = false;
            return;
        }
        _pending[s] = false;
        if (!_pendingMeasured[s]) return;
        if (!arena.TryRead(_stamps[s, 0], out ulong previous)) return;
        ulong first = previous;
        double majorSum = 0;
        var subGaps = new List<(string Label, double Ms)>();
        for (int i = 1; i < n; i++)
        {
            if (!arena.TryRead(_stamps[s, i], out ulong t)) t = previous;
            double ms = t >= previous ? (t - previous) / 1e6 : 0;
            previous = t;
            majorSum += ms;
            if (_stampSub[s, i])
            {
                subGaps.Add((_stampLabel[s, i]!, ms));
                continue;
            }
            string label = _stampLabel[s, i]!;
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
            if (subGaps.Count > 0)
            {
                var r = GetRow(label + "/rest", label, "rest");
                r.Gpu += rest;
                r.GpuSamples++;
            }
            subGaps.Clear();
            majorSum = 0;
        }
        _gpuFrameSum += (previous - first) / 1e6;
        _gpuFrames++;
    }

    #endregion

    #region Report

    /// <summary>
    /// <paramref name="v"/> with two decimals.
    /// </summary>
    static string Fmt(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// The table (per-frame means over the measured frames) and the PASSCSV lines.
    /// </summary>
    public void Report(TextWriter output)
    {
        for (int s = 0; s < Slots; s++) Collect(s);
        if (_measuredFrames == 0)
        {
            output.WriteLine($"passes    no frames measured (the first {_skip} are skipped)");
            return;
        }
        double f = _measuredFrames;
        // Per frame, like the CPU column: a stage that runs more than once (a capture splitting the scene pass) or a cascade drawn every
        // second frame is summed over the frame, not averaged per run (until 2026-10-07 it was divided by its runs, which under-counted the
        // slices' stages: docs/render-distance-benchmark.md).
        double Gpu(Row r) => r.GpuSamples > 0 && _gpuFrames > 0 ? r.Gpu / _gpuFrames : double.NaN;
        var sb = new StringBuilder();
        sb.AppendLine($"passes    {Tag}: means per frame over {_measuredFrames} frames (first {_skip} skipped), "
            + $"{_gpuFrames} with GPU times; Stopwatch CPU ms of the render thread; GPU ms between timestamps");
        sb.Append($"passes    {"row",-28}{"cpu",7}{"gpu",7}{"draws",7}{"indir",6}{"disp",6}{"pipes",6}{"push",6}{"segs",6}");
        sb.AppendLine($"{"constKB",8}{"uplKB",7}{"fence",7}{"submit",7}{"acq",6}{"pres",6}");
        void Line(Row r, string prefix)
        {
            var c = r.C;
            double g = Gpu(r);
            sb.AppendLine($"passes    {prefix + r.Name,-28}{Fmt(r.Cpu / f),7}{(double.IsNaN(g) ? "-" : Fmt(g)),7}"
                + $"{c[0] / f,7:0}{c[1] / f,6:0}{c[2] / f,6:0}{c[3] / f,6:0}{c[4] / f,6:0}{c[5] / f,6:0}"
                + $"{c[6] / 1024.0 / f,8:0.0}{c[7] / 1024.0 / f,7:0.0}{Fmt(c[Fence] * TickMs / f),7}{Fmt(c[Submit] * TickMs / f),7}"
                + $"{Fmt(c[Acquire] * TickMs / f),6}{Fmt(c[Present] * TickMs / f),6}");
        }
        foreach (var r in _order.Where(r => r.Parent is null))
        {
            Line(r, "");
            foreach (var sub in _order.Where(x => x.Parent == r.Key)) Line(sub, "  ");
        }
        if (_between.Cpu > 0) Line(_between, "");
        var total = new Row("TOTAL", "TOTAL", null);
        foreach (var r in _order.Where(r => r.Parent is null))
        {
            total.Cpu += r.Cpu;
            for (int i = 0; i < N; i++) total.C[i] += r.C[i];
            if (!double.IsNaN(Gpu(r))) total.Gpu += Gpu(r) * f;
        }
        total.GpuSamples = (long)f;
        Line(total, "");
        var sorted = _frameCpu.OrderBy(x => x).ToList();
        double gpuFrame = _gpuFrames > 0 ? _gpuFrameSum / _gpuFrames : double.NaN;
        sb.AppendLine($"passes    frame render-thread cpu (rows except {EndLabel} and driver-wait): mean {Fmt(_frameCpu.Average())}, "
            + $"p50 {Fmt(sorted[sorted.Count / 2])}, p95 {Fmt(sorted[Math.Min((int)(sorted.Count * 0.95), sorted.Count - 1)])} ms; "
            + $"gpu frame (first to last timestamp) {Fmt(gpuFrame)} ms");
        output.Write(sb.ToString());
        // For scripts: PASSCSV|tag|row|parent|cpu|gpu|counters... (per-frame means; the tick columns in ms).
        output.WriteLine($"PASSCSVHEAD|{string.Join("|", GpuStats.CounterNames)}|fenceMs|submitMs|acquireMs|presentMs");
        void Csv(Row r)
        {
            var parts = new string[N];
            for (int i = 0; i < N; i++)
                parts[i] = (i >= Fence ? r.C[i] * TickMs / f : r.C[i] / f).ToString("0.###", CultureInfo.InvariantCulture);
            double g = Gpu(r);
            output.WriteLine($"PASSCSV|{Tag}|{r.Key}|{r.Parent}|{r.Cpu / f:0.####}|"
                + $"{(double.IsNaN(g) ? "" : g.ToString("0.####", CultureInfo.InvariantCulture))}|{string.Join("|", parts)}");
        }
        foreach (var r in _order) Csv(r);
        if (_between.Cpu > 0) Csv(_between);
        output.WriteLine($"PASSFRAME|{Tag}|{_measuredFrames}|{_frameCpu.Average():0.####}|{sorted[sorted.Count / 2]:0.####}|"
            + $"{sorted[Math.Min((int)(sorted.Count * 0.95), sorted.Count - 1)]:0.####}|{gpuFrame:0.####}");
    }

    #endregion
}
