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

    public enum Mode { Off, Gpu, Cpu }

    public const int History = 300;
    const int Slots = 4, MaxStamps = 128, MaxPre = 96, Stages = 28;

    // Series: the stages by StageClock index, then the GPU time outside the stamped stages (uploads, overlay), then the total.
    const int Other = Stages, Total = Stages + 1, Series = Stages + 2;

    // The legend's open groups and upd-chars's parts; each row drawn last (its top and what a click on it opens: a group, PartsRow or nothing, -1),
    // the legend's box, a closed group's summed series on the side shown, and the series plotted.
    const int PartsRow = -2, SimRow = -3;

    // The panel's margin round it and its padding.
    const float Margin = 16, Pad = 12;

    // The legend's top rows: a stage of its own, or a group of stages (shown as their sum) that a click on its row opens. The GPU's submit span
    // is the window copy/tail; driver-wait is CPU only.
    static readonly (string Name, int[] Stages)[] Groups =
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

    static readonly int[] Order = Groups.SelectMany(g => g.Stages).ToArray();

    static readonly Vector4[] Colours =
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

    static readonly Vector4 FrameColour = new(1.00f, 0.55f, 0.10f, 1);
    static readonly Vector4 DimColour = new(0.7f, 0.7f, 0.68f, 1);

    static readonly Vector4[] PieColours =
    [
        new(0.35f, 0.60f, 1.00f, 1), new(0.95f, 0.55f, 0.25f, 1), new(0.40f, 0.85f, 0.40f, 1), new(0.95f, 0.35f, 0.35f, 1),
        new(0.65f, 0.45f, 0.95f, 1), new(0.95f, 0.85f, 0.35f, 1), new(0.25f, 0.80f, 0.85f, 1), new(0.95f, 0.55f, 0.85f, 1),
        new(0.55f, 0.55f, 0.55f, 1), new(0.35f, 0.35f, 0.40f, 1), new(0.20f, 0.20f, 0.22f, 1),
    ];

    #endregion

    #region Frame timing

    readonly Func<double>? _gpuFrameMs;

    // Native timestamps: the context (VkGl provides the seam), the slots per stamp, and the record callback (no allocation per stamp).
    readonly GpuContext _native;
    int _slot;
    double _outsideSim;
    long _lastBegin;
    double _frameMs;

    // The whole frame's wall time, from one BeginFrame to the next (the simulation, the host, the waits for a free frame and for the GPU included):
    // kept with the render thread's history, a frame late, and drawn on both charts.
    readonly float[] _frameTime = new float[History];

    // The simulation's parts (Outside with parts): the names, how many lead as phases (the last of them the time outside the world's ticks), how many
    // systems follow (listed by cost; the rest are parts of systems, listed in order), each one's ms and the world ticks per frame, listed under sim
    // in the legend, not plotted. simNow is the newest call's, kept with the frame at EndFrame.
    string[] _simNames = [];
    int _simPhases, _simSystems, _simPaths;
    float[][] _simParts = [];
    readonly float[] _simTicks = new float[History];
    double[] _simNow = [];
    int _simNowTicks;
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

    /// <param name="gpuFrameMs">The whole frame's GPU time, when the backend measures it (it includes the uploads before the first stage).</param>
    public FrameProfiler(GpuContext gpu, Func<double>? gpuFrameMs = null)
    {
        this._gpuFrameMs = gpuFrameMs;
        _native = gpu;
        GpuContext.ProfilePreFrame = true;
        _recordStamp = cmd => cmd.Timestamp(_native.Frame.Timestamps, _pendingStamp);
    }

    /// <summary>Before the frame's first stage (after the wait for a free frame, so that wait is not counted).</summary>
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

    /// <summary>After the frame is presented: the last stage (overlay, submit, present) ends and the frame's laps are kept.</summary>
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
        if (SpikeLog.Enabled) RecordSpikeCpu(sum);
        _cpuHead = (_cpuHead + 1) % History;
        _cpuCount = Math.Min(_cpuCount + 1, History);
    }

    /// <summary>Keeps the render thread's stage laps, the parts and the simulation rows of the frame just ended; returns its summed ms.</summary>
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

    /// <summary>--log-spikes: keeps the frame's number, its render-thread stages and its notes until its GPU times arrive.</summary>
    void RecordSpikeCpu(double sum)
    {
        _slotFrame[_slot] = ++_frameCounter;
        _slotStamps[_slot] = _native.LastFrameStamps;
        _slotCpuTotal[_slot] = sum;
        _slotCpuMedian[_slot] = CpuMedian();
        string stages = StageList(StageClock.Ms, 0.5);
        string ui = PartList("ui", StageClock.UiPartNames, StageClock.UiPartMs);
        string updChars = PartList("upd-chars", StageClock.PartNames, StageClock.PartMs);
        _slotCpu[_slot] = stages + ui + updChars;
        long uploaded = _native.Frame.Stats.UploadBytes;
        _slotNotes[_slot] = (uploaded > 0 ? $"uploads total {uploaded / 1048576.0:0.00} MB; " : "") + SpikeLog.TakeFrame();
    }

    #endregion

    #region GPU stamps

    readonly int[,] _stampStage = new int[Slots, MaxStamps];
    readonly int[] _stampCount = new int[Slots];
    readonly bool[] _pending = new bool[Slots];
    readonly bool[] _submitted = new bool[Slots];
    readonly QuerySlot[,] _nativeStamps = new QuerySlot[Slots, MaxStamps];
    QuerySlot _pendingStamp;
    readonly Action<IGpuCommandList> _recordStamp;

    // The pre-frame command buffer's own chain of stamps (uploads, culls, bakes; PreStamp): per slot the stamp and the stage that owns the time since
    // the one before.
    readonly QuerySlot[,] _preSlots = new QuerySlot[Slots, MaxPre];
    readonly int[,] _preStage = new int[Slots, MaxPre];
    readonly int[] _preCount = new int[Slots];
    readonly QuerySlot[] _slotPreEnd = new QuerySlot[Slots];

    // Series of the frame being read (Collect), and the stamp slots of each frame in flight.
    readonly double[] _gpuFrame = new double[Series];
    // Each profiled frame's own first and last stamps (its GPU total).
    readonly (QuerySlot Begin, QuerySlot End)[] _slotWhole = new (QuerySlot, QuerySlot)[Slots];

    /// <summary>
    /// A stamp in the pre-frame command buffer <paramref name="cmd"/>, called at the edges of the work that buffer holds: the time since the buffer's
    /// previous stamp (the first is the frame's start) goes to <paramref name="stage"/>. So a span is closed by <c>PreStamp(cmd,
    /// StageClock.Uploads)</c> just before it (whatever was recorded since the last stamp is uploads) and <c>PreStamp(cmd, stage)</c> after it.
    /// Nothing outside a profiled frame; the time after the last stamp, up to the buffer's end, is uploads too.
    /// </summary>
    internal static void PreStamp(IGpuCommandList cmd, int stage) => StageClock.Profiler?.RecordPre(cmd, stage);

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

    bool TryStamp(int s, int i, out ulong ns) => _native.Frame.Timestamps.TryRead(_nativeStamps[s, i], out ns);

    /// <summary>Reads the GPU stage times of slot <paramref name="s"/> when they are ready, into the GPU history.</summary>
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
        if (SpikeLog.Enabled && _slotFrame[s] > SpikeLog.SkipFrames) RecordRunGpu(total, sum);
        if (SpikeLog.Enabled && _slotNotes[s] is { } notes) ReportSpike(s, total, sum, notes);
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

    /// <summary>Publishes the read frame: its GPU stage series, the other and total series, and the frame's observers.</summary>
    void PublishGpuFrame(int s, double total, double sum)
    {
        for (int k = 0; k < Stages; k++) _gpu[k][_gpuHead] = (float)_gpuFrame[k];
        _gpu[Other][_gpuHead] = (float)(total - sum);
        _gpu[Total][_gpuHead] = (float)total;
        OnGpuFrame?.Invoke(_slotTag[s], _gpuFrame, total);
    }

    #endregion

    #region Chart

    readonly float[][] _cpu = NewHistory(), _gpu = NewHistory();

    // The render thread's ms per part of upd-chars (StageClock.PartNames), listed under it in the legend, not plotted.
    readonly float[][] _parts = StageClock.PartNames.Select(_ => new float[History]).ToArray();
    readonly bool[] _groupOpen = new bool[Groups.Length];
    bool _partsOpen, _simOpen;
    readonly List<(float Top, int Opens)> _rows = [];
    (float X0, float Y0, float X1, float Y1, float LineHeight) _legendBox;
    readonly float[][] _groupSeries = Groups.Select(_ => new float[History]).ToArray();
    readonly List<(float[] Series, Vector4 Colour, float Thickness, int Head, int Count)> _plotted = [];
    int _cpuHead, _cpuCount, _gpuHead, _gpuCount;

    /// <summary>What the chart shows; F12 cycles it.</summary>
    public Mode Showing { get; set; }

    /// <summary>Frames whose GPU stage times have been read (at most <see cref="History"/>).</summary>
    public int GpuFrames => _gpuCount;

    /// <summary>The newest frame's GPU time over its stamped stages, in ms (0 before the first frame is read).</summary>
    public double LastGpuMs => _gpuCount == 0 ? 0 : _gpu[Total][(_gpuHead - 1 + History) % History];

    /// <summary>Reads every GPU timing that is ready now (after the last frame of a run has been waited for).</summary>
    public void Flush() { for (int s = 0; s < Slots; s++) Collect(s); }

    /// <summary>
    /// A left click at <paramref name="at"/> (framebuffer pixels): on a legend row marked + or - it opens or closes that row. True when it was on the
    /// legend (the caller drops it).
    /// </summary>
    public bool Click(Vector2 at)
    {
        var (bx0, by0, bx1, by1, lh) = _legendBox;
        if (Showing == Mode.Off || at.X < bx0 || at.X > bx1 || at.Y < by0 || at.Y > by1) return false;

        foreach (var (top, opens) in _rows)
        {
            if (at.Y < top || at.Y >= top + lh) continue;

            if (opens >= 0) _groupOpen[opens] = !_groupOpen[opens];
            else if (opens == PartsRow) _partsOpen = !_partsOpen;
            else if (opens == SimRow) _simOpen = !_simOpen;
            break;
        }

        return true;
    }

    static float[][] NewHistory()
    {
        var h = new float[Series][];
        for (int i = 0; i < Series; i++) h[i] = new float[History];
        return h;
    }

    static bool OnCpu(int series) => series != Other && !StageClock.GpuOnly(series);

    static bool OnGpu(int series) => !StageClock.CpuOnly(series);

    static string Label(int series) => series switch { Other => "other", Total => "total", _ => StageClock.Names[series] };

    /// <summary>Mean of the last <paramref name="frames"/> entries of a series.</summary>
    static double Mean(float[] series, int head, int count, int frames)
    {
        frames = Math.Min(frames, count);
        if (frames == 0) return 0;

        double sum = 0;
        for (int i = 1; i <= frames; i++) sum += series[(head - i + History) % History];
        return sum / frames;
    }

    /// <summary>Largest of the last <paramref name="count"/> entries of a series (all it holds).</summary>
    static double Max(float[] series, int head, int count)
    {
        float max = 0;
        for (int i = 1; i <= count; i++) max = Math.Max(max, series[(head - i + History) % History]);
        return max;
    }

    /// <summary>Smallest 1, 2 or 5 × 10ⁿ at or above <paramref name="v"/>.</summary>
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
        if (_simOpen) SimRows(meanFrames);

        var panel = DrawPanel(overlay, width, height, showGpu);
        DrawHeader(overlay, showGpu, panel.X0, panel.Y0);
        DrawLegend(overlay, showGpu, panel.X1, panel.Y0, meanFrames);
        DrawPlot(overlay, width, height, panel.X0, panel.Y0, panel.Y1);
    }

    /// <summary>The chart's panel along the bottom: sized for its legend rows, drawn; returns its corners.</summary>
    (float X0, float Y0, float X1, float Y1) DrawPanel(DebugOverlay overlay, int width, int height, bool showGpu)
    {
        int rowCount = 0;
        for (int gi = 0; gi < Groups.Length; gi++)
        {
            var stages = Groups[gi].Stages;
            if (stages.Length > 1) rowCount++;
            if (stages.Length == 1 || _groupOpen[gi])
                foreach (int s in stages)
                {
                    rowCount += 1;
                    if (s == StageClock.UpdChars && _partsOpen) rowCount += _parts.Length;
                    if (s == StageClock.Sim && _simOpen) rowCount += _simRows.Count + 2;
                }
        }

        float lh = overlay.LineHeight;
        float panelH = (rowCount + 2.5f) * lh + 2 * Pad;
        float x0 = Margin, x1 = width - Margin, y1 = height - Margin, y0 = Math.Max(y1 - panelH, Margin);
        overlay.Rect(x0, y0, x1, y1, DebugOverlay.PanelColour);
        return (x0, y0, x1, y1);
    }

    /// <summary>The panel's first line: the mode, the VRAM and RAM figures, and the whole frame's time.</summary>
    void DrawHeader(DebugOverlay overlay, bool showGpu, float x0, float y0)
    {
        float cw = overlay.CharWidth;
        RefreshVram();

        // The process's RAM: its working set (all it holds in physical memory) and the managed heap within it.
        string header = string.Create(CultureInfo.InvariantCulture,
            $"{(showGpu ? "gpu" : "cpu")}   vram {_vram.Used / 1073741824.0:0.00} of {_vram.Budget / 1073741824.0:0.0} GB   " +
            $"ram {_ram.WorkingSet / 1073741824.0:0.00} GB (managed {_ram.Managed / 1073741824.0:0.00})   ");
        overlay.Text(header, x0 + Pad, y0 + Pad, DebugOverlay.TextColour);

        // The whole frame (FrameColour line): mean of the last frames, frames a second and the longest kept.
        double frameMean = Mean(_frameTime, _cpuHead, _cpuCount, 30);
        string frame = string.Create(CultureInfo.InvariantCulture,
            $"frame {frameMean:0.00} ms ({(frameMean > 0 ? 1000 / frameMean : 0):0} fps), max {Max(_frameTime, _cpuHead, _cpuCount):0.00}");
        overlay.Text(frame, x0 + Pad + header.Length * cw, y0 + Pad, FrameColour);
    }

    /// <summary>
    /// The legend at the right: swatch, row, mean of the last frames on the CPU and the GPU. A row marked + opens (a click), - closes; a closed group
    /// shows its stages' sum and is plotted as one line, an open one plots each stage.
    /// </summary>
    void DrawLegend(DebugOverlay overlay, bool showGpu, float x1, float y0, int meanFrames)
    {
        const float swatch = 10;
        float lh = overlay.LineHeight, cw = overlay.CharWidth;
        var hist = showGpu ? _gpu : _cpu;
        int head = showGpu ? _gpuHead : _cpuHead, count = showGpu ? _gpuCount : _cpuCount;
        float legendW = swatch + 6 + (16 + 10 + 10) * cw;
        float lx = x1 - Pad - legendW, ly = y0 + Pad + 1.5f * lh;
        var dim = DimColour;
        overlay.Text($"{"",-16}{"cpu",10}{"gpu",10}", lx + swatch + 6, ly, dim);
        _rows.Clear();
        _plotted.Clear();
        string Ms(bool shown, double ms) => shown ? ms.ToString("0.00", CultureInfo.InvariantCulture) : "-";
        float NextRow(int opens)
        {
            float top = ly + (_rows.Count + 1) * lh;
            _rows.Add((top, opens));
            return top;
        }

        bool Plots(int s) => showGpu ? OnGpu(s) : OnCpu(s);
        void StageRow(int s, string indent)
        {
            bool expands = s == StageClock.UpdChars || s == StageClock.Sim && _simNames.Length > 0;
            bool open = s == StageClock.Sim ? _simOpen : _partsOpen;
            float top = NextRow(!expands ? -1 : s == StageClock.Sim ? SimRow : PartsRow);
            overlay.Rect(lx, top + 4, lx + swatch, top + 4 + swatch, Colours[s]);
            if (Plots(s)) _plotted.Add((hist[s], Colours[s], s == Total ? 2f : 1.5f, head, count));
            string label = indent + (expands ? open ? "- " : "+ " : "  ") + Label(s);
            string cpu = Ms(OnCpu(s), Mean(_cpu[s], _cpuHead, _cpuCount, meanFrames));
            string gpu = Ms(_gpuCount > 0 && OnGpu(s), Mean(_gpu[s], _gpuHead, _gpuCount, meanFrames));
            overlay.Text($"{label,-16}{cpu,10}{gpu,10}", lx + swatch + 6, top, DebugOverlay.TextColour);
            if (!expands || !open) return;

            if (s == StageClock.Sim)
            {
                // The world ticks a frame ran and the mean tick, then the phases, the time outside the ticks and the costliest systems.
                double ticks = Mean(_simTicks, _cpuHead, _cpuCount, meanFrames), inTicks = 0;
                for (int p = 0; p < _simPhases - 1; p++) inTicks += _simRows[p].Ms;
                string ticksText = string.Create(CultureInfo.InvariantCulture,
                    $"{indent}    {ticks:0.0} ticks, {(ticks > 0 ? inTicks / ticks : 0):0.00} ms each");
                overlay.Text(ticksText, lx + swatch + 6, NextRow(-1), dim);
                overlay.Text($"{indent}    {_simPaths} paths waiting", lx + swatch + 6, NextRow(-1), dim);
                foreach (var (name, ms) in _simRows)
                {
                    string row = indent + "    " + name;
                    overlay.Text($"{(row.Length > 16 ? row[..16] : row),-16}{Ms(true, ms),10}{"",10}", lx + swatch + 6, NextRow(-1), dim);
                }

                return;
            }

            // Its parts, render thread only (the GPU work they record is in the passes that run it); not plotted.
            for (int p = 0; p < _parts.Length; p++)
            {
                string partName = indent + "    " + StageClock.PartNames[p];
                string partMs = Ms(true, Mean(_parts[p], _cpuHead, _cpuCount, meanFrames));
                overlay.Text($"{partName,-16}{partMs,10}{"",10}", lx + swatch + 6, NextRow(-1), dim);
            }
        }

        for (int gi = 0; gi < Groups.Length; gi++)
        {
            var (name, stages) = Groups[gi];
            if (stages.Length == 1)
            {
                StageRow(stages[0], "");
                continue;
            }

            double c = 0, g = 0;
            bool anyCpu = false, anyGpu = false;
            foreach (int s in stages)
            {
                if (OnCpu(s))
                {
                    c += Mean(_cpu[s], _cpuHead, _cpuCount, meanFrames);
                    anyCpu = true;
                }

                if (OnGpu(s))
                {
                    g += Mean(_gpu[s], _gpuHead, _gpuCount, meanFrames);
                    anyGpu = true;
                }
            }

            float top = NextRow(gi);
            bool open = _groupOpen[gi], shown = showGpu ? anyGpu : anyCpu;
            var sum = _groupSeries[gi];
            Array.Clear(sum);
            foreach (int s in stages)
            {
                if (Plots(s))
                {
                    for (int i = 0; i < History; i++) sum[i] += hist[s][i];
                }
            }

            if (!open)
            {
                overlay.Rect(lx, top + 4, lx + swatch, top + 4 + swatch, Colours[stages[0]]);
                if (shown) _plotted.Add((sum, Colours[stages[0]], 1.5f, head, count));
            }

            overlay.Text($"{(open ? "- " : "+ ") + name,-16}{Ms(anyCpu, c),10}{Ms(_gpuCount > 0 && anyGpu, g),10}",
                lx + swatch + 6, top, DebugOverlay.TextColour);
            if (open)
            {
                foreach (int s in stages) StageRow(s, "  ");
            }
        }

        _legendBox = (lx, ly, x1 - Pad, ly + (_rows.Count + 1) * lh, lh);
    }

    /// <summary>The plot: y from 0 to a round number above the largest value shown, with labelled grid lines; ends the overlay's batch.</summary>
    void DrawPlot(DebugOverlay overlay, int width, int height, float x0, float y0, float y1)
    {
        float cw = overlay.CharWidth, lh = overlay.LineHeight;
        float lx = _legendBox.X0;

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

    /// <summary>The grid lines and their labels: five steps from 0 to <paramref name="scale"/>.</summary>
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

    /// <summary>Every plotted series as lines, oldest at the left and the newest frame at the right edge.</summary>
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

    List<(string Name, ulong Bytes)> _vramSlices = [];
    (ulong Used, ulong Budget) _vram;
    int _vramAge;
    (long WorkingSet, long Managed) _ram;

    /// <summary>The VRAM pie at the top right is shown (Shift+F12 in the game), with the profiler chart or without it.</summary>
    public bool ShowVram { get; set; }

    /// <summary>
    /// Writes what is in video memory now to <paramref name="path"/> (Ctrl+Shift+F12 in the game): the driver's usage and budget, the room in our
    /// blocks, the owners, then every resource and block (<see cref="GpuAllocator.WriteReport"/>). What the driver counts beyond our blocks
    /// (swapchain, DLSS, its own) is only a total here: a RenderDoc or Nsight capture lists it.
    /// </summary>
    public void DumpVram(string path, Action<TextWriter>? more = null)
    {
        var alloc = _native.Device.Allocator;
        var (used, budget) = _native.Device.VideoMemory();
        var (empty, spare, scattered, partBlocks) = alloc.Slack();
        ulong blocks = alloc.TotalAllocatedBytes;
        using var w = new StreamWriter(path);
        w.WriteLine($"VRAM dump {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        w.WriteLine($"driver: used {used / 1048576.0:0.0} MB of budget {budget / 1048576.0:0.0} MB " +
            $"(memory budget extension: {_native.Device.HasMemoryBudget})");
        var heaps = _native.Device.Heaps();
        for (int i = 0; i < heaps.Count; i++)
            w.WriteLine($"  heap {i}: {heaps[i].Size / 1048576.0:0} MB, ours {heaps[i].Usage / 1048576.0:0.0} MB " +
                $"of budget {heaps[i].Budget / 1048576.0:0.0} MB ({heaps[i].Flags})");
        ulong local = (ulong)alloc.Breakdown().Sum(o => (double)o.DeviceLocal) + empty + spare + scattered;
        string ours = $"ours: {blocks / 1048576.0:0.0} MB in {alloc.BlockCount} blocks ({local / 1048576.0:0.0} MB device-local), " +
            $"{alloc.TotalUsedBytes / 1048576.0:0.0} MB used by {alloc.AllocationCount} allocations; ";
        w.WriteLine(ours + $"driver and others {(used > local ? used - local : 0) / 1048576.0:0.0} MB");
        w.WriteLine($"room in device-local blocks: empty {empty / 1048576.0:0.0} MB, spare {spare / 1048576.0:0.0} MB, " +
            $"scattered {scattered / 1048576.0:0.0} MB in {partBlocks} blocks");
        w.WriteLine();
        w.WriteLine("owners (MB, device-local MB, count):");
        foreach (var o in alloc.Breakdown()) w.WriteLine($"{o.Bytes / 1048576.0,10:0.0}  {o.DeviceLocal / 1048576.0,10:0.0}  {o.Count,6}  {o.Name}");
        w.WriteLine();
        more?.Invoke(w);
        alloc.WriteReport(w);
    }

    /// <summary>The VRAM pie at the top right, while <see cref="ShowVram"/>.</summary>
    public void DrawVram(DebugOverlay overlay, int width, int height)
    {
        if (!ShowVram || width <= 0 || height <= 0) return;

        RefreshVram();
        DrawVramPie(overlay, width);
        overlay.Flush(width, height);
    }

    // VRAM (a driver call) and RAM are read every 30 frames, for the chart's header and the pie; only while either is shown.
    void RefreshVram()
    {
        if (_vramAge-- <= 0)
        {
            _vram = _native.Device.VideoMemory();
            _vramSlices = VramSlices();
            _ram = (Environment.WorkingSet, GC.GetTotalMemory(false));
            _vramAge = 30;
        }
    }

    /// <summary>
    /// The VRAM by owner: the eight largest device-local owners of <see cref="GpuAllocator.Breakdown"/>, the rest of ours, the unused room in our
    /// blocks, and what the driver counts beyond our blocks (its own allocations, swapchain, other APIs' resources such as DLSS's).
    /// </summary>
    List<(string Name, ulong Bytes)> VramSlices()
    {
        var alloc = _native.Device.Allocator;
        var owners = alloc.Breakdown().Where(o => o.DeviceLocal > 0).OrderByDescending(o => o.DeviceLocal).ToList();
        var slices = owners.Take(8).Select(o => (o.Name, o.DeviceLocal)).ToList();
        ulong ours = (ulong)owners.Sum(o => (double)o.DeviceLocal), shown = (ulong)slices.Sum(s => (double)s.DeviceLocal);
        if (ours > shown) slices.Add(("other of ours", ours - shown));

        // Device-local blocks only: the host-visible ones (staging, constants) are system memory, not in the driver's VRAM figure.
        var (empty, spare, scattered, _) = alloc.Slack();
        ulong free = empty + spare + scattered, blocks = ours + free;
        if (free > 0) slices.Add(("free in our blocks", free));
        if (_vram.Used > blocks) slices.Add(("driver and others", _vram.Used - blocks));
        return slices;
    }

    void DrawVramPie(DebugOverlay overlay, int width)
    {
        if (_vramSlices.Count == 0) return;

        double total = _vramSlices.Sum(s => (double)s.Bytes);
        if (total <= 0) return;

        const float margin = 16, pad = 12, radius = 70;
        float lh = overlay.LineHeight, cw = overlay.CharWidth;
        float legendW = 2 * cw + 30 * cw;
        float panelW = pad * 3 + 2 * radius + legendW, panelH = Math.Max(2 * radius, (_vramSlices.Count + 1.5f) * lh) + 2 * pad;
        float x1 = width - margin, x0 = x1 - panelW, y0 = margin;
        overlay.Rect(x0, y0, x1, y0 + panelH, DebugOverlay.PanelColour);
        var centre = new Vector2(x0 + pad + radius, y0 + pad + Math.Max(radius, (panelH - 2 * pad) / 2));
        double angle = -Math.PI / 2;
        float lx = x0 + pad * 2 + 2 * radius, ly = y0 + pad;
        overlay.Text($"VRAM {_vram.Used / 1073741824.0:0.00} of {_vram.Budget / 1073741824.0:0.0} GB", lx, ly, DebugOverlay.TextColour);
        for (int i = 0; i < _vramSlices.Count; i++)
        {
            var (name, bytes) = _vramSlices[i];
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

    #endregion

    #region Spike log

    // --log-spikes (SpikeLog): per slot the frame number, its notes and its render-thread stages, kept until the GPU times arrive.
    readonly long[] _slotFrame = new long[Slots];
    readonly string?[] _slotNotes = new string?[Slots], _slotCpu = new string?[Slots];
    readonly double[] _slotCpuTotal = new double[Slots], _slotCpuMedian = new double[Slots];
    readonly (QuerySlot Begin, QuerySlot PreEnd)[] _slotStamps = new (QuerySlot, QuerySlot)[Slots];
    long _frameCounter;
    readonly long[] _slotTag = new long[Slots];
    readonly List<double> _medianScratch = new(History);
    readonly List<float>[] _runGpu = Enumerable.Range(0, Series).Select(_ => new List<float>()).ToArray();

    /// <summary>The render thread's median frame total over the history (0 before 30 frames), the frame just kept included.</summary>
    double CpuMedian()
    {
        int have = Math.Min(_cpuCount + 1, History);
        if (have < 30) return 0;

        _medianScratch.Clear();
        for (int i = 0; i < have; i++) _medianScratch.Add(_cpu[Total][i]);
        _medianScratch.Sort();
        return _medianScratch[have / 2];
    }

    /// <summary>Adds the frame's stage times to the run's samples (after the skipped frames) for the end-of-run summary.</summary>
    void RecordRunGpu(double total, double sum)
    {
        for (int k = 0; k < Stages; k++) _runGpu[k].Add((float)_gpuFrame[k]);
        _runGpu[Stages].Add((float)(total - sum));
        _runGpu[Stages + 1].Add((float)total);
        SpikeLog.OnSummary ??= PrintRunSummary;
    }

    /// <summary>Reports a spiked frame with its CPU and GPU stages and its notes.</summary>
    void ReportSpike(int s, double total, double sum, string notes)
    {
        _medianScratch.Clear();
        int have = Math.Min(_gpuCount + 1, History);
        for (int i = 0; i < have; i++) _medianScratch.Add(_gpu[Total][i]);
        _medianScratch.Sort();
        double median = have >= 30 ? _medianScratch[have / 2] : 0;
        string pre = PreFrameText(s);
        string gpuStages = StageList(_gpuFrame, 0.3) + (total - sum >= 0.3 ? $", other {total - sum:0.0}" : "");
        SpikeLog.Report(_slotFrame[s], total, median, pre + gpuStages, _slotCpu[s] ?? "", notes, _slotCpuTotal[s], _slotCpuMedian[s]);
        _slotNotes[s] = null;
    }

    /// <summary>
    /// The pre-frame buffer's time for slot <paramref name="s"/> as its log text ("pre-frame x, "), or empty when it has no stamps.
    /// </summary>
    string PreFrameText(int s)
    {
        var arena = _native.Frame.Timestamps;
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
        foreach (int s in Order)
        {
            var v = _runGpu[s];
            if (v.Count == 0) continue;

            var sorted = v.OrderBy(x => x).ToArray();
            double mean = keep.Length == 0 ? 0 : keep.Average(i => v[i]);
            string stats = string.Create(CultureInfo.InvariantCulture,
                $"{mean,6:0.00} {sorted[sorted.Length / 2],6:0.00} {sorted[(int)(sorted.Length * 0.95)],6:0.00} {sorted[^1],7:0.00}");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"gpu-run     {Label(s),-12} {stats}"));
        }
    }

    // A stage's parts of 1 ms or more, for the spike log: "; ui: hud 25.0, composite 3.1".
    static string PartList(string stage, string[] names, double[] ms)
    {
        var parts = new List<string>();
        for (int k = 0; k < names.Length; k++)
        {
            if (ms[k] >= 1) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{names[k]} {ms[k]:0.0}"));
        }

        return parts.Count == 0 ? "" : $"; {stage}: {string.Join(", ", parts)}";
    }

    static string StageList(double[] ms, double least)
    {
        var parts = new List<string>();
        for (int k = 0; k < Stages; k++)
        {
            if (ms[k] >= least) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Label(k)} {ms[k]:0.0}"));
        }

        return string.Join(", ", parts);
    }

    #endregion

    public void Dispose()
    {
        if (StageClock.Profiler == this) StageClock.Profiler = null;
    }
}
