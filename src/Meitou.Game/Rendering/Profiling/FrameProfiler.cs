using System.Globalization;
using System.Numerics;

using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Overlays;

namespace Meitou.Game.Rendering.Profiling;

/// <summary>
/// Where the frame time goes, per <see cref="StageClock"/> stage: the render thread's time (stopwatch laps) and the GPU's (a timestamp at every lap;
/// a stage's time is the gap since the stamp before, summed when a stage runs more than once). Keeps the last <see cref="History"/> frames and draws
/// them with the <see cref="DebugOverlay"/> as one chart, a line per stage. GPU timings are read a few frames late, without waiting.
/// </summary>
public sealed class FrameProfiler : IDisposable
{
    #region Tables

    /// <summary>
    /// What the chart shows (<see cref="Showing"/>; F12 cycles it).
    /// </summary>
    public enum Mode
    {
        /// <summary>
        /// Nothing: the profiler is hidden.
        /// </summary>
        Off,

        /// <summary>
        /// The GPU's stage times.
        /// </summary>
        Gpu,

        /// <summary>
        /// The render thread's stage times.
        /// </summary>
        Cpu,
    }

    /// <summary>
    /// The frames kept and charted.
    /// </summary>
    public const int History = 300;

    /// <summary>
    /// The frames in flight timed at once (each a slot of stamps), the most stage stamps and pre-frame stamps a frame takes, and the <see
    /// cref="StageClock"/> stages.
    /// </summary>
    internal const int Slots = 4, MaxStamps = 128, MaxPre = 96, Stages = 28;

    /// <summary>
    /// The series: the stages by <see cref="StageClock"/> index, then the GPU time outside the stamped stages (uploads, overlay), then the total.
    /// </summary>
    internal const int Other = Stages, Total = Stages + 1, Series = Stages + 2;

    /// <summary>
    /// The panel's margin round it.
    /// </summary>
    const float Margin = 16;

    /// <summary>
    /// The panel's padding.
    /// </summary>
    internal const float Pad = 12;

    /// <summary>
    /// The legend's top rows: a stage of its own, or a group of stages (shown as their sum) that a click on its row opens. The GPU's submit span is
    /// the window copy/tail; driver-wait is CPU only.
    /// </summary>
    internal static readonly (string Name, int[] Stages)[] Groups =
    [
        ("sim", [StageClock.Sim]),
        ("update", [StageClock.Host, StageClock.Blood, StageClock.Birds, 0, 1, 2, StageClock.UpdChars, 3]),
        ("pre-frame", [StageClock.Uploads, StageClock.Cull, StageClock.Bake]),
        ("shadows", [12]), ("gi", [14]), ("reflection", [4]),
        ("scene", [5, 6, 7, 8, StageClock.Characters, 9, StageClock.Shafts, StageClock.Fog, 13]),
        ("post", [10]),
        ("present", [StageClock.Ui, StageClock.Submit, StageClock.DriverWait]),
        ("other", [Other]), ("total", [Total]),
    ];

    /// <summary>
    /// Every series of <see cref="Groups"/>, in the legend's order.
    /// </summary>
    internal static readonly int[] Order = Groups.SelectMany(g => g.Stages).ToArray();

    /// <summary>
    /// Each series' line colour, by series index.
    /// </summary>
    internal static readonly Vector4[] Colours =
    [
        new(0.55f, 0.75f, 1.00f, 1), new(0.35f, 0.55f, 0.95f, 1), new(0.60f, 0.45f, 0.95f, 1), new(0.95f, 0.85f, 0.35f, 1),
        new(0.25f, 0.80f, 0.85f, 1), new(0.95f, 0.55f, 0.85f, 1), new(0.65f, 0.65f, 0.65f, 1), new(0.95f, 0.55f, 0.25f, 1),
        new(0.40f, 0.85f, 0.40f, 1), new(0.30f, 0.60f, 1.00f, 1), new(0.95f, 0.35f, 0.35f, 1), new(0.55f, 0.55f, 0.75f, 1),
        new(0.45f, 0.35f, 0.25f, 1), new(1.00f, 0.75f, 0.20f, 1), new(0.85f, 0.95f, 0.45f, 1),
        new(0.70f, 0.55f, 0.40f, 1), new(0.80f, 0.30f, 0.55f, 1), new(0.55f, 0.90f, 0.70f, 1), new(0.85f, 0.70f, 0.95f, 1),
        new(0.95f, 0.75f, 0.60f, 1),
        new(0.95f, 0.45f, 0.65f, 1), new(0.70f, 0.50f, 0.95f, 1),
        new(0.30f, 0.80f, 0.70f, 1), new(0.90f, 0.65f, 0.35f, 1),
        new(0.75f, 0.75f, 0.55f, 1), new(0.85f, 0.15f, 0.15f, 1), new(0.20f, 0.95f, 0.60f, 1),
        new(0.95f, 0.90f, 0.55f, 1),
        new(0.50f, 0.50f, 0.50f, 1), new(1.00f, 1.00f, 1.00f, 1),
    ];

    /// <summary>
    /// The colour of the whole frame's line.
    /// </summary>
    static readonly Vector4 FrameColour = new(1.00f, 0.55f, 0.10f, 1);

    /// <summary>
    /// The colour of the dim legend text.
    /// </summary>
    internal static readonly Vector4 DimColour = new(0.7f, 0.7f, 0.68f, 1);

    #endregion

    #region Frame timing

    /// <summary>
    /// The whole frame's GPU time, when the backend measures it; null otherwise.
    /// </summary>
    readonly Func<double>? _gpuFrameMs;

    /// <summary>
    /// The native timestamps' context (VkGl provides the seam).
    /// </summary>
    readonly GpuContext _native;

    /// <summary>
    /// The slot of the frame being made.
    /// </summary>
    int _slot;

    /// <summary>
    /// The simulation's ms of the last <see cref="Outside(double)"/> call, taken into the next frame at <see cref="BeginFrame"/>.
    /// </summary>
    double _outsideSim;

    /// <summary>
    /// When the last <see cref="BeginFrame"/> ran, in <see cref="System.Diagnostics.Stopwatch"/> ticks (0: never).
    /// </summary>
    long _lastBegin;

    /// <summary>
    /// The whole frame's wall time from the last <see cref="BeginFrame"/> to this one, in ms.
    /// </summary>
    double _frameMs;

    /// <summary>
    /// The whole frame's wall time, from one BeginFrame to the next (the simulation, the host, the waits for a free frame and for the GPU included):
    /// kept with the render thread's history, a frame late, and drawn on both charts.
    /// </summary>
    readonly float[] _frameTime = new float[History];

    /// <summary>
    /// The simulation's parts (Outside with parts): the names, how many lead as phases (the last of them the time outside the world's ticks), how
    /// many systems follow (listed by cost; the rest are parts of systems, listed in order), each one's ms and the world ticks per frame, listed
    /// under sim in the legend, not plotted. simNow is the newest call's, kept with the frame at EndFrame.
    /// </summary>
    string[] _simNames = [];

    /// <summary>
    /// How many of the simulation's parts lead as phases, how many systems follow, and the path requests not answered yet.
    /// </summary>
    int _simPhases, _simSystems, _simPaths;

    /// <summary>
    /// Each simulation part's ms per frame of the history.
    /// </summary>
    float[][] _simParts = [];

    /// <summary>
    /// The world ticks run per frame of the history.
    /// </summary>
    readonly float[] _simTicks = new float[History];

    /// <summary>
    /// The newest <see cref="Outside(double, int, string[], ReadOnlySpan{double}, int, int, int)"/> call's ms per part.
    /// </summary>
    double[] _simNow = [];

    /// <summary>
    /// The newest call's world ticks.
    /// </summary>
    int _simNowTicks;

    /// <summary>
    /// The simulation's rows as the legend lists them, refreshed each draw.
    /// </summary>
    readonly List<(string Name, double Ms)> _simRows = [];

    /// <summary>
    /// The benchmark harness's frame number: copied at <see cref="BeginFrame"/> and handed back with the frame's GPU times by <see
    /// cref="OnGpuFrame"/>, which arrive a few frames later.
    /// </summary>
    public long Tag { get; set; }

    /// <summary>
    /// The timestamps the newest frame took for the profiler (the stage laps and the pre-frame buffer's), to say what the profiler costs.
    /// </summary>
    public int StampsLastFrame { get; private set; }

    /// <summary>
    /// Called when a frame's GPU times have been read: its <see cref="Tag"/>, the ms per <see cref="StageClock"/> stage (do not keep the array), and
    /// the whole frame's GPU ms.
    /// </summary>
    public Action<long, double[], double>? OnGpuFrame { get; set; }

    /// <summary>
    /// A profiler of <paramref name="gpu"/>'s frames: timestamps around the stages (the pre-frame buffer's too), with its VRAM panel and spike run.
    /// </summary>
    /// <param name="gpuFrameMs">The whole frame's GPU time, when the backend measures it (it includes the uploads before the first stage).</param>
    public FrameProfiler(GpuContext gpu, Func<double>? gpuFrameMs = null)
    {
        this._gpuFrameMs = gpuFrameMs;
        _native = gpu;
        _vramPanel = new VramPanel(gpu);
        _spikes = new SpikeRun(gpu);
        GpuContext.ProfilePreFrame = true;
        _recordStamp = cmd => cmd.Timestamp(_native.Frame.Timestamps, _pendingStamp);
    }

    /// <summary>
    /// Before the frame's first stage (after the wait for a free frame, so that wait is not counted).
    /// </summary>
    public void BeginFrame()
    {
        for (int s = 0; s < Slots; s++) Collect(s);

        _slot = (_slot + 1) % Slots;
        _slotWhole[_slot] = default;
        _pending[_slot] = false;   // still not ready after a full round: dropped
        _stampCount[_slot] = 0;
        _preCount[_slot] = 0;
        _slotTag[_slot] = Tag;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        _frameMs = _lastBegin == 0 ? 0 : (now - _lastBegin) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _lastBegin = now;
        StageClock.Start();
        StageClock.Ms[StageClock.Sim] = _outsideSim;
        _outsideSim = 0;
        StageClock.Profiler = this;
        Stamp(-1);
    }

    /// <summary>
    /// Render-thread work before <see cref="BeginFrame"/> that belongs to the next frame: the game's simulation ticks (<see cref="StageClock.Sim"/>),
    /// the last call's ms.
    /// </summary>
    public void Outside(double simMs) => _outsideSim = simMs;

    /// <summary>
    /// <see cref="Outside(double)"/> with the ticks' breakdown: <paramref name="names"/> (the same array while the parts stay the same) with their
    /// <paramref name="ms"/>, the first
    /// <paramref name="phases"/> of them phases shown in order (the last the time outside the ticks), the next <paramref name="systems"/> shown by
    /// cost, the rest (parts of systems) in order; <paramref name="ticks"/> the world ticks run; <paramref name="pathsWaiting"/> the path requests
    /// not answered yet.
    /// </summary>
    public void Outside(double simMs, int ticks, string[] names, ReadOnlySpan<double> ms, int phases, int systems, int pathsWaiting = 0)
    {
        _simSystems = systems;
        _simPaths = pathsWaiting;
        _outsideSim = simMs;
        if (!ReferenceEquals(names, _simNames))
        {
            _simNames = names;
            _simParts = names.Select(_ => new float[History]).ToArray();
            _simNow = new double[names.Length];
        }

        _simPhases = phases;
        ms[..Math.Min(ms.Length, _simNow.Length)].CopyTo(_simNow);
        _simNowTicks = ticks;
    }

    /// <summary>
    /// After the frame is presented: the last stage (overlay, submit, present) ends and the frame's laps are kept.
    /// </summary>
    public void EndFrame()
    {
        // Its GPU stamp would land in the next frame's commands: the CPU lap only.
        StageClock.Profiler = null;
        StageClock.EndFrame();
        _submitted[_slot] = StageClock.SubmissionStarted;
        _pending[_slot] = _stampCount[_slot] > 1;
        StampsLastFrame = _stampCount[_slot] + _preCount[_slot];

        // Complete when the context has ended the frame already (else the total falls back to gpuFrameMs).
        _slotWhole[_slot] = _native.CurrentFrameStamps;
        _slotPreEnd[_slot] = _native.LastFrameStamps.PreEnd;
        double sum = RecordCpuStages();
        _frameTime[_cpuHead] = (float)_frameMs;
        if (SpikeLog.Enabled) _spikes.RecordCpu(_slot, sum, _cpu[Total], _cpuCount);
        _cpuHead = (_cpuHead + 1) % History;
        _cpuCount = Math.Min(_cpuCount + 1, History);
    }

    /// <summary>
    /// Keeps the render thread's stage laps, the parts and the simulation rows of the frame just ended; returns its summed ms.
    /// </summary>
    double RecordCpuStages()
    {
        double sum = 0;
        for (int s = 0; s < Stages; s++)
        {
            _cpu[s][_cpuHead] = (float)StageClock.Ms[s];
            sum += StageClock.Ms[s];
        }

        for (int p = 0; p < _parts.Length; p++) _parts[p][_cpuHead] = (float)StageClock.PartMs[p];
        for (int p = 0; p < _simParts.Length; p++) _simParts[p][_cpuHead] = (float)_simNow[p];
        _simTicks[_cpuHead] = _simNowTicks;
        Array.Clear(_simNow);
        _simNowTicks = 0;
        _cpu[Other][_cpuHead] = 0;
        _cpu[Total][_cpuHead] = (float)sum;
        return sum;
    }

    #endregion

    #region GPU stamps

    /// <summary>
    /// Per slot and stamp, the stage the time up to the stamp goes to.
    /// </summary>
    readonly int[,] _stampStage = new int[Slots, MaxStamps];

    /// <summary>
    /// Per slot, the stage stamps taken.
    /// </summary>
    readonly int[] _stampCount = new int[Slots];

    /// <summary>
    /// Per slot, whether its stamps wait to be read.
    /// </summary>
    readonly bool[] _pending = new bool[Slots];

    /// <summary>
    /// Per slot, whether the frame's submission had started (its tail then counts as submit, else as interface).
    /// </summary>
    readonly bool[] _submitted = new bool[Slots];

    /// <summary>
    /// Per slot and stamp, the native timestamp's query.
    /// </summary>
    readonly QuerySlot[,] _nativeStamps = new QuerySlot[Slots, MaxStamps];

    /// <summary>
    /// The query the record callback writes next.
    /// </summary>
    QuerySlot _pendingStamp;

    /// <summary>
    /// Records <see cref="_pendingStamp"/> into a command list (one delegate, so a stamp allocates nothing).
    /// </summary>
    readonly Action<IGpuCommandList> _recordStamp;

    /// <summary>
    /// The pre-frame command buffer's own chain of stamps (uploads, culls, bakes; <see cref="PreStamp"/>): per slot the stamp and the stage that owns
    /// the time since the one before.
    /// </summary>
    readonly QuerySlot[,] _preSlots = new QuerySlot[Slots, MaxPre];

    /// <summary>
    /// Per slot and pre-frame stamp, the stage that owns the time since the one before.
    /// </summary>
    readonly int[,] _preStage = new int[Slots, MaxPre];

    /// <summary>
    /// Per slot, the pre-frame stamps taken.
    /// </summary>
    readonly int[] _preCount = new int[Slots];

    /// <summary>
    /// Per slot, the pre-frame buffer's end stamp.
    /// </summary>
    readonly QuerySlot[] _slotPreEnd = new QuerySlot[Slots];

    /// <summary>
    /// The series of the frame being read (<see cref="Collect"/>).
    /// </summary>
    readonly double[] _gpuFrame = new double[Series];

    /// <summary>
    /// Each profiled frame's own first and last stamps (its GPU total).
    /// </summary>
    readonly (QuerySlot Begin, QuerySlot End)[] _slotWhole = new (QuerySlot, QuerySlot)[Slots];

    /// <summary>
    /// Per slot the frame's <see cref="Tag"/>, handed back with its GPU times (<see cref="OnGpuFrame"/>).
    /// </summary>
    readonly long[] _slotTag = new long[Slots];

    /// <summary>
    /// A stamp in the pre-frame command buffer <paramref name="cmd"/>, called at the edges of the work that buffer holds: the time since the buffer's
    /// previous stamp (the first is the frame's start) goes to <paramref name="stage"/>. So a span is closed by <c>PreStamp(cmd,
    /// StageClock.Uploads)</c> just before it (whatever was recorded since the last stamp is uploads) and <c>PreStamp(cmd, stage)</c> after it.
    /// Nothing outside a profiled frame; the time after the last stamp, up to the buffer's end, is uploads too.
    /// </summary>
    internal static void PreStamp(IGpuCommandList cmd, int stage) => StageClock.Profiler?.RecordPre(cmd, stage);

    /// <summary>
    /// Records a pre-frame stamp into <paramref name="cmd"/> for <paramref name="stage"/> (nothing past <see cref="MaxPre"/> or without a free
    /// query).
    /// </summary>
    void RecordPre(IGpuCommandList cmd, int stage)
    {
        int n = _preCount[_slot];
        if (n >= MaxPre) return;

        var q = _native.Frame.Timestamps.Allocate();
        if (!q.IsValid) return;

        cmd.Timestamp(_native.Frame.Timestamps, q);
        _preSlots[_slot, n] = q;
        _preStage[_slot, n] = stage;
        _preCount[_slot] = n + 1;
    }

    /// <summary>
    /// A stage stamp: the GPU time since the previous stamp goes to <paramref name="stage"/> (nothing past <see cref="MaxStamps"/> or without a free
    /// query).
    /// </summary>
    internal void Stamp(int stage)
    {
        int n = _stampCount[_slot];
        if (n >= MaxStamps) return;

        // The native timestamps (QueryArena), recorded through the seam without ending VkGl's pass: they keep working while the
        // stages move to native code (docs/renderer-native.md 7.1 step 9).
        var q = _native.Frame.Timestamps.Allocate();
        if (!q.IsValid) return;

        _pendingStamp = q;
        _native.Interleave(_recordStamp);
        _nativeStamps[_slot, n] = q;
        _stampStage[_slot, n] = stage;
        _stampCount[_slot] = n + 1;
    }

    /// <summary>
    /// Reads stamp <paramref name="i"/> of slot <paramref name="s"/> in nanoseconds; false while it is not ready.
    /// </summary>
    bool TryStamp(int s, int i, out ulong ns) => _native.Frame.Timestamps.TryRead(_nativeStamps[s, i], out ns);

    /// <summary>
    /// Reads the GPU stage times of slot <paramref name="s"/> when they are ready, into the GPU history.
    /// </summary>
    void Collect(int s)
    {
        if (!_pending[s]) return;

        int n = _stampCount[s];
        if (!TryStamp(s, n - 1, out _)) return;

        Array.Clear(_gpuFrame);
        var arena = _native.Frame.Timestamps;
        var whole = _slotWhole[s];
        double sum = CollectPreFrame(s, whole);

        // The stamped stages of the frame's own commands (the first stamp is the frame's start there: after the pre-frame buffer, so a gap between
        // the two stays in "other").
        TryStamp(s, 0, out ulong previous);
        for (int i = 1; i < n; i++)
        {
            TryStamp(s, i, out ulong now);
            double ms = now >= previous ? (now - previous) / 1e6 : 0;
            _gpuFrame[_stampStage[s, i]] += ms;
            sum += ms;
            previous = now;
        }

        // UI ends before the window copy. Without a submission boundary the tail still belongs to UI.
        if (arena.TryRead(whole.End, out ulong tailEnd) && tailEnd >= previous)
        {
            _gpuFrame[_submitted[s] ? StageClock.Submit : StageClock.Ui] += (tailEnd - previous) / 1e6;
            sum += (tailEnd - previous) / 1e6;
        }

        // The frame's own total (first to last stamp); the context's latest completed frame is only a stand-in and, with frames in flight, another
        // frame.
        double own = arena.TryRead(whole.Begin, out ulong wb) && arena.TryRead(whole.End, out ulong we) && we >= wb ? (we - wb) / 1e6 : 0;
        double total = own > 0 ? Math.Max(own, sum) : _gpuFrameMs?.Invoke() is > 0 and var t ? Math.Max(t, sum) : sum;
        PublishGpuFrame(s, total, sum);
        if (SpikeLog.Enabled && _spikes.FrameOf(s) > SpikeLog.SkipFrames) _spikes.RecordRunGpu(_gpuFrame, total, sum);
        if (SpikeLog.Enabled && _spikes.NotesOf(s) is { } notes) _spikes.ReportSpike(s, total, sum, notes, _gpuFrame, _gpu[Total], _gpuCount);
        _gpuHead = (_gpuHead + 1) % History;
        _gpuCount = Math.Min(_gpuCount + 1, History);
        _pending[s] = false;
    }

    /// <summary>
    /// The pre-frame command buffer, from the frame's first stamp: its spans (cull, bakes, the ocean) and what is left up to its end (uploads);
    /// returns its summed ms.
    /// </summary>
    double CollectPreFrame(int s, (QuerySlot Begin, QuerySlot End) whole)
    {
        double sum = 0;
        var arena = _native.Frame.Timestamps;
        if (arena.TryRead(whole.Begin, out ulong cursor))
        {
            for (int i = 0; i < _preCount[s]; i++)
            {
                if (!arena.TryRead(_preSlots[s, i], out ulong at)) continue;

                double span = at >= cursor ? (at - cursor) / 1e6 : 0;
                _gpuFrame[_preStage[s, i]] += span;
                sum += span;
                cursor = Math.Max(cursor, at);
            }

            if (arena.TryRead(_slotPreEnd[s], out ulong preEnd) && preEnd >= cursor)
            {
                _gpuFrame[StageClock.Uploads] += (preEnd - cursor) / 1e6;
                sum += (preEnd - cursor) / 1e6;
            }
        }

        return sum;
    }

    /// <summary>
    /// Publishes the read frame: its GPU stage series, the other and total series, and the frame's observers.
    /// </summary>
    void PublishGpuFrame(int s, double total, double sum)
    {
        for (int k = 0; k < Stages; k++) _gpu[k][_gpuHead] = (float)_gpuFrame[k];
        _gpu[Other][_gpuHead] = (float)(total - sum);
        _gpu[Total][_gpuHead] = (float)total;
        OnGpuFrame?.Invoke(_slotTag[s], _gpuFrame, total);
    }

    #endregion

    #region Chart

    /// <summary>
    /// The render thread's and the GPU's ms per series and frame (<see cref="History"/> frames each, ring buffers).
    /// </summary>
    readonly float[][] _cpu = NewHistory(), _gpu = NewHistory();

    /// <summary>
    /// The render thread's ms per part of upd-chars (<see cref="StageClock.PartNames"/>), listed under it in the legend, not plotted.
    /// </summary>
    readonly float[][] _parts = StageClock.PartNames.Select(_ => new float[History]).ToArray();

    /// <summary>
    /// The legend at the right, its open rows and where it was drawn (<see cref="ProfilerLegend"/>).
    /// </summary>
    readonly ProfilerLegend _legend = new();

    /// <summary>
    /// The series plotted: the legend's rows', then the whole frame's.
    /// </summary>
    readonly List<(float[] Series, Vector4 Colour, float Thickness, int Head, int Count)> _plotted = [];

    /// <summary>
    /// The render thread's and the GPU's ring heads (the next frame's index) and frame counts.
    /// </summary>
    int _cpuHead, _cpuCount, _gpuHead, _gpuCount;

    /// <summary>
    /// What the chart shows; F12 cycles it.
    /// </summary>
    public Mode Showing { get; set; }

    /// <summary>
    /// Frames whose GPU stage times have been read (at most <see cref="History"/>).
    /// </summary>
    public int GpuFrames => _gpuCount;

    /// <summary>
    /// The newest frame's GPU time over its stamped stages, in ms (0 before the first frame is read).
    /// </summary>
    public double LastGpuMs => _gpuCount == 0 ? 0 : _gpu[Total][(_gpuHead - 1 + History) % History];

    /// <summary>
    /// Reads every GPU timing that is ready now (after the last frame of a run has been waited for).
    /// </summary>
    public void Flush()
    {
        for (int s = 0; s < Slots; s++) Collect(s);
    }

    /// <summary>
    /// A left click at <paramref name="at"/> (framebuffer pixels): on a legend row marked + or - it opens or closes that row. True when it was on the
    /// legend (the caller drops it).
    /// </summary>
    public bool Click(Vector2 at) => Showing != Mode.Off && _legend.Click(at);

    /// <summary>
    /// A ring buffer of <see cref="History"/> frames for every series.
    /// </summary>
    static float[][] NewHistory()
    {
        var h = new float[Series][];
        for (int i = 0; i < Series; i++) h[i] = new float[History];
        return h;
    }

    /// <summary>
    /// Whether a series is charted for the render thread (not GPU-only, not other).
    /// </summary>
    internal static bool OnCpu(int series) => series != Other && !StageClock.GpuOnly(series);

    /// <summary>
    /// Whether a series is charted for the GPU (not CPU-only).
    /// </summary>
    internal static bool OnGpu(int series) => !StageClock.CpuOnly(series);

    /// <summary>
    /// A series' legend label: its stage's name, "other" or "total".
    /// </summary>
    internal static string Label(int series) => series switch { Other => "other", Total => "total", _ => StageClock.Names[series] };

    /// <summary>
    /// Mean of the last <paramref name="frames"/> entries of a series.
    /// </summary>
    internal static double Mean(float[] series, int head, int count, int frames)
    {
        frames = Math.Min(frames, count);
        if (frames == 0) return 0;

        double sum = 0;
        for (int i = 1; i <= frames; i++) sum += series[(head - i + History) % History];
        return sum / frames;
    }

    /// <summary>
    /// Largest of the last <paramref name="count"/> entries of a series (all it holds).
    /// </summary>
    static double Max(float[] series, int head, int count)
    {
        float max = 0;
        for (int i = 1; i <= count; i++) max = Math.Max(max, series[(head - i + History) % History]);
        return max;
    }

    /// <summary>
    /// Smallest 1, 2 or 5 × 10ⁿ at or above <paramref name="v"/>.
    /// </summary>
    static float NiceCeiling(float v)
    {
        if (v <= 0) return 1;

        float p = MathF.Pow(10, MathF.Floor(MathF.Log10(v)));
        foreach (float m in (ReadOnlySpan<float>)[1, 2, 5, 10])
        {
            if (m * p >= v) return m * p;
        }

        return 10 * p;
    }

    /// <summary>
    /// The chart along the bottom of the screen: one line per stage of the <see cref="Showing"/> side, the legend with both sides' means.
    /// </summary>
    public void Draw(DebugOverlay overlay, int width, int height)
    {
        if (Showing == Mode.Off || width <= 0 || height <= 0) return;

        bool showGpu = Showing == Mode.Gpu;
        const int meanFrames = 30;
        if (_legend.SimOpen) SimRows(meanFrames);

        var panel = DrawPanel(overlay, width, height, showGpu);
        DrawHeader(overlay, showGpu, panel.X0, panel.Y0);
        _legend.Draw(overlay, showGpu, panel.X1, panel.Y0, meanFrames, Timings(), _plotted);
        DrawPlot(overlay, width, height, panel.X0, panel.Y0, panel.Y1);
    }

    /// <summary>
    /// The chart's panel along the bottom: sized for its legend rows, drawn; returns its corners.
    /// </summary>
    (float X0, float Y0, float X1, float Y1) DrawPanel(DebugOverlay overlay, int width, int height, bool showGpu)
    {
        int rowCount = _legend.RowCount(_parts.Length, _simRows.Count);
        float lh = overlay.LineHeight;
        float panelH = (rowCount + 2.5f) * lh + 2 * Pad;
        float x0 = Margin, x1 = width - Margin, y1 = height - Margin, y0 = Math.Max(y1 - panelH, Margin);
        overlay.Rect(x0, y0, x1, y1, DebugOverlay.PanelColour);
        return (x0, y0, x1, y1);
    }

    /// <summary>
    /// The panel's first line: the mode, the VRAM and RAM figures, and the whole frame's time.
    /// </summary>
    void DrawHeader(DebugOverlay overlay, bool showGpu, float x0, float y0)
    {
        float cw = overlay.CharWidth;
        _vramPanel.Refresh();
        var (vram, ram) = (_vramPanel.Vram, _vramPanel.Ram);

        // The process's RAM: its working set (all it holds in physical memory) and the managed heap within it.
        string header = string.Create(CultureInfo.InvariantCulture,
            $"{(showGpu ? "gpu" : "cpu")}   vram {vram.Used / 1073741824.0:0.00} of {vram.Budget / 1073741824.0:0.0} GB   " +
            $"ram {ram.WorkingSet / 1073741824.0:0.00} GB (managed {ram.Managed / 1073741824.0:0.00})   ");
        overlay.Text(header, x0 + Pad, y0 + Pad, DebugOverlay.TextColour);

        // The whole frame (FrameColour line): mean of the last frames, frames a second and the longest kept.
        double frameMean = Mean(_frameTime, _cpuHead, _cpuCount, 30);
        string frame = string.Create(CultureInfo.InvariantCulture,
            $"frame {frameMean:0.00} ms ({(frameMean > 0 ? 1000 / frameMean : 0):0} fps), max {Max(_frameTime, _cpuHead, _cpuCount):0.00}");
        overlay.Text(frame, x0 + Pad + header.Length * cw, y0 + Pad, FrameColour);
    }

    /// <summary>
    /// What the legend reads this frame: both histories, upd-chars's parts and the sim rows.
    /// </summary>
    LegendTimings Timings() => new()
    {
        Cpu = _cpu,
        CpuHead = _cpuHead,
        CpuCount = _cpuCount,
        Gpu = _gpu,
        GpuHead = _gpuHead,
        GpuCount = _gpuCount,
        Parts = _parts,
        SimTicks = _simTicks,
        SimRows = _simRows,
        SimPhases = _simPhases,
        SimPaths = _simPaths,
        SimExpands = _simNames.Length > 0,
    };

    /// <summary>
    /// The plot: y from 0 to a round number above the largest value shown, with labelled grid lines; ends the overlay's batch.
    /// </summary>
    void DrawPlot(DebugOverlay overlay, int width, int height, float x0, float y0, float y1)
    {
        float cw = overlay.CharWidth, lh = overlay.LineHeight;
        float lx = _legend.Left;

        // The whole frame, on both charts (the render thread's history: a GPU chart's newest frames are a few behind it).
        _plotted.Add((_frameTime, FrameColour, 2f, _cpuHead, _cpuCount));

        float px0 = x0 + Pad + 7 * cw, px1 = lx - 3 * cw, py0 = y0 + Pad + 1.5f * lh + lh * 0.5f, py1 = y1 - Pad - lh * 0.5f;
        if (px1 - px0 < 50 || py1 - py0 < 30)
        {
            overlay.Flush(width, height);
            return;
        }

        float max = 0;
        foreach (var (series, _, _, _, n) in _plotted)
        {
            for (int i = 0; i < n; i++) max = Math.Max(max, series[i]);
        }

        float scale = NiceCeiling(max * 1.05f);
        var grid = new Vector4(1, 1, 1, 0.12f);
        DrawGrid(overlay, scale, LabelFormat(scale / 4), x0, px0, px1, py0, py1, lh, grid);
        DrawSeries(overlay, scale, px0, px1, py0, py1);
        overlay.Flush(width, height);
    }

    /// <summary>
    /// The grid lines and their labels: five steps from 0 to <paramref name="scale"/>.
    /// </summary>
    void DrawGrid(DebugOverlay overlay, float scale, string labelFormat, float x0, float px0, float px1, float py0, float py1, float lh, Vector4 grid)
    {
        for (int k = 0; k <= 4; k++)
        {
            float v = scale * k / 4, y = py1 - (py1 - py0) * k / 4;
            overlay.Rect(px0, y - 0.5f, px1, y + 0.5f, grid);
            overlay.Text(v.ToString(labelFormat, CultureInfo.InvariantCulture).PadLeft(6), x0 + Pad, y - lh * 0.5f, DimColour);
        }
    }

    /// <summary>
    /// The number format of the grid labels: as many decimals as the grid step needs (10 / 4 = 2.5 needs one), so no label is rounded.
    /// </summary>
    static string LabelFormat(float step)
    {
        if (MathF.Abs(step - MathF.Round(step)) < 1e-4f) return "0";
        if (MathF.Abs(step * 10 - MathF.Round(step * 10)) < 1e-3f) return "0.0";
        if (MathF.Abs(step * 100 - MathF.Round(step * 100)) < 1e-2f) return "0.00";
        return "0.000";
    }

    /// <summary>
    /// Every plotted series as lines, oldest at the left and the newest frame at the right edge.
    /// </summary>
    void DrawSeries(DebugOverlay overlay, float scale, float px0, float px1, float py0, float py1)
    {
        float dx = (px1 - px0) / (History - 1);

        // Oldest at the left; the newest frame at the right edge.
        foreach (var (series, colour, thickness, h, n) in _plotted)
        {
            if (n < 2) continue;

            // A run of points within a quarter pixel of the height it started at is one segment: most stages lie flat near 0, and the
            // 300 segments a series would otherwise queue made the plot cost as much as the HUD.
            float runX = 0, runY = 0, lastX = 0, lastY = 0;
            for (int i = n - 1; i >= 0; i--)
            {
                float v = series[(h - 1 - i + History) % History];
                float x = px1 - i * dx, y = py1 - (py1 - py0) * Math.Clamp(v / scale, 0, 1);
                if (i == n - 1)
                {
                    (runX, runY) = (x, y);
                }
                else if (MathF.Abs(y - runY) >= 0.25f)
                {
                    if (lastX > runX) overlay.Line(runX, runY, lastX, lastY, thickness, colour);
                    overlay.Line(lastX, lastY, x, y, thickness, colour);
                    (runX, runY) = (x, y);
                }

                (lastX, lastY) = (x, y);
            }

            if (lastX > runX) overlay.Line(runX, runY, lastX, lastY, thickness, colour);
        }
    }

    /// <summary>
    /// The sim rows to list: the phases in order, then the systems costing the most over the last <paramref name="frames"/> frames and the rest as
    /// one row, then the systems' parts in order.
    /// </summary>
    void SimRows(int frames)
    {
        const int shown = 10;
        _simRows.Clear();
        int systemsEnd = Math.Min(_simParts.Length, _simPhases + _simSystems);
        for (int p = 0; p < _simPhases && p < _simParts.Length; p++)
            _simRows.Add((_simNames[p], Mean(_simParts[p], _cpuHead, _cpuCount, frames)));

        var systems = Enumerable.Range(_simPhases, Math.Max(0, systemsEnd - _simPhases))
            .Select(p => (Name: "  " + _simNames[p], Ms: Mean(_simParts[p], _cpuHead, _cpuCount, frames)))
            .OrderByDescending(r => r.Ms)
            .ToList();
        _simRows.AddRange(systems.Take(shown));
        if (systems.Count > shown) _simRows.Add(("  other systems", systems.Skip(shown).Sum(r => r.Ms)));
        for (int p = systemsEnd; p < _simParts.Length; p++)
            _simRows.Add(("  " + _simNames[p], Mean(_simParts[p], _cpuHead, _cpuCount, frames)));
    }

    #endregion

    #region VRAM

    /// <summary>
    /// The VRAM pie, the header's figures and the VRAM dump (<see cref="VramPanel"/>).
    /// </summary>
    readonly VramPanel _vramPanel;

    /// <summary>
    /// The VRAM pie at the top right is shown (Shift+F12 in the game), with the profiler chart or without it.
    /// </summary>
    public bool ShowVram { get => _vramPanel.Show; set => _vramPanel.Show = value; }

    /// <summary>
    /// Writes what is in video memory now to <paramref name="path"/> (Ctrl+Shift+F12 in the game; <see cref="VramPanel.Dump"/>).
    /// </summary>
    public void DumpVram(string path, Action<TextWriter>? more = null) => _vramPanel.Dump(path, more);

    /// <summary>
    /// The VRAM pie at the top right, while <see cref="ShowVram"/>.
    /// </summary>
    public void DrawVram(DebugOverlay overlay, int width, int height) => _vramPanel.Draw(overlay, width, height);

    #endregion

    #region Spike log

    /// <summary>
    /// --log-spikes: the frames' CPU side until their GPU times arrive, the spike reports and the run's summary (<see cref="SpikeRun"/>).
    /// </summary>
    readonly SpikeRun _spikes;

    #endregion

    /// <summary>
    /// Stops being the <see cref="StageClock"/>'s profiler.
    /// </summary>
    public void Dispose()
    {
        if (StageClock.Profiler == this) StageClock.Profiler = null;
    }
}
