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
    const int Slots = 4, MaxStamps = 128, MaxPre = 96, Stages = 27;
    // Series: the stages by StageClock index, then the GPU time outside the stamped stages (uploads, overlay), then the total.
    const int Other = Stages, Total = Stages + 1, Series = Stages + 2;
    // The legend's top rows: a stage of its own, or a group of stages (shown as their sum) that a click on its row opens. The GPU's submit span
    // is the window copy/tail; driver-wait is CPU only.
    static readonly (string Name, int[] Stages)[] Groups =
    [
        ("sim", [StageClock.Sim]),
        ("update", [StageClock.Host, StageClock.Blood, 0, 1, 2, StageClock.UpdChars, 3]),
        ("pre-frame", [StageClock.Uploads, StageClock.Cull, StageClock.Bake]),
        ("shadows", [12]), ("gi", [14]), ("reflection", [4]),
        ("scene", [5, 6, 7, 8, StageClock.Characters, 9, StageClock.Shafts, StageClock.Fog, 13]),
        ("post", [10]),
        ("present", [StageClock.Ui, StageClock.Submit, StageClock.DriverWait]),
        ("other", [Other]), ("total", [Total]),
    ];
    static readonly int[] Order = Groups.SelectMany(g => g.Stages).ToArray();
    static bool OnCpu(int series) => series != Other && !StageClock.GpuOnly(series);
    static bool OnGpu(int series) => !StageClock.CpuOnly(series);

    static readonly Vector4[] Colours =
    [
        new(0.55f, 0.75f, 1.00f, 1), new(0.35f, 0.55f, 0.95f, 1), new(0.60f, 0.45f, 0.95f, 1), new(0.95f, 0.85f, 0.35f, 1),
        new(0.25f, 0.80f, 0.85f, 1), new(0.95f, 0.55f, 0.85f, 1), new(0.65f, 0.65f, 0.65f, 1), new(0.95f, 0.55f, 0.25f, 1),
        new(0.40f, 0.85f, 0.40f, 1), new(0.30f, 0.60f, 1.00f, 1), new(0.95f, 0.35f, 0.35f, 1), new(0.55f, 0.55f, 0.75f, 1),
        new(0.45f, 0.35f, 0.25f, 1), new(1.00f, 0.75f, 0.20f, 1), new(0.85f, 0.95f, 0.45f, 1),
        new(0.70f, 0.55f, 0.40f, 1), new(0.80f, 0.30f, 0.55f, 1), new(0.55f, 0.90f, 0.70f, 1), new(0.85f, 0.70f, 0.95f, 1), new(0.95f, 0.75f, 0.60f, 1),
        new(0.95f, 0.45f, 0.65f, 1), new(0.70f, 0.50f, 0.95f, 1),
        new(0.30f, 0.80f, 0.70f, 1), new(0.90f, 0.65f, 0.35f, 1),
        new(0.75f, 0.75f, 0.55f, 1), new(0.85f, 0.15f, 0.15f, 1), new(0.20f, 0.95f, 0.60f, 1),
        new(0.50f, 0.50f, 0.50f, 1), new(1.00f, 1.00f, 1.00f, 1),
    ];

    readonly Func<double>? gpuFrameMs;
    readonly int[,] stampStage = new int[Slots, MaxStamps];
    readonly int[] stampCount = new int[Slots];
    readonly bool[] pending = new bool[Slots];
    readonly bool[] submitted = new bool[Slots];
    int slot;

    // The pre-frame command buffer's own chain of stamps (uploads, culls, bakes; PreStamp): per slot the stamp and the stage that owns the time since the one before.
    readonly QuerySlot[,] preSlots = new QuerySlot[Slots, MaxPre];
    readonly int[,] preStage = new int[Slots, MaxPre];
    readonly int[] preCount = new int[Slots];
    readonly QuerySlot[] slotPreEnd = new QuerySlot[Slots];

    readonly float[][] cpu = NewHistory(), gpu = NewHistory();
    // The render thread's ms per part of upd-chars (StageClock.PartNames), listed under it in the legend, not plotted.
    readonly float[][] parts = StageClock.PartNames.Select(_ => new float[History]).ToArray();

    // The legend's open groups and upd-chars's parts; each row drawn last (its top and what a click on it opens: a group, PartsRow or nothing, -1),
    // the legend's box, a closed group's summed series on the side shown, and the series plotted.
    const int PartsRow = -2, SimRow = -3;
    readonly bool[] groupOpen = new bool[Groups.Length];
    bool partsOpen, simOpen;
    readonly List<(float Top, int Opens)> rows = [];
    (float X0, float Y0, float X1, float Y1, float LineHeight) legendBox;
    readonly float[][] groupSeries = Groups.Select(_ => new float[History]).ToArray();
    readonly List<(float[] Series, Vector4 Colour, float Thickness, int Head, int Count)> plotted = [];
    static readonly Vector4 FrameColour = new(1.00f, 0.55f, 0.10f, 1);
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

    /// <summary>What the chart shows; F12 cycles it.</summary>
    public Mode Showing { get; set; }

    /// <summary>The benchmark harness's frame number: copied at <see cref="BeginFrame"/> and handed back with the frame's GPU times by <see cref="OnGpuFrame"/>, which arrive a few frames later.</summary>
    public long Tag { get; set; }

    /// <summary>The timestamps the newest frame took for the profiler (the stage laps and the pre-frame buffer's), to say what the profiler costs.</summary>
    public int StampsLastFrame { get; private set; }

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
        GpuContext.ProfilePreFrame = true;
        recordStamp = cmd => cmd.Timestamp(native.Frame.Timestamps, pendingStamp);
    }

    static float[][] NewHistory()
    {
        var h = new float[Series][];
        for (int i = 0; i < Series; i++) h[i] = new float[History];
        return h;
    }

    static string Label(int series) => series switch { Other => "other", Total => "total", _ => StageClock.Names[series] };

    /// <summary>Before the frame's first stage (after the wait for a free frame, so that wait is not counted).</summary>
    public void BeginFrame()
    {
        for (int s = 0; s < Slots; s++) Collect(s);
        slot = (slot + 1) % Slots;
        slotWhole[slot] = default;
        pending[slot] = false;   // still not ready after a full round: dropped
        stampCount[slot] = 0;
        preCount[slot] = 0;
        slotTag[slot] = Tag;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        frameMs = lastBegin == 0 ? 0 : (now - lastBegin) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        lastBegin = now;
        StageClock.Start();
        StageClock.Ms[StageClock.Sim] = outsideSim;
        outsideSim = 0;
        StageClock.Profiler = this;
        Stamp(-1);
    }

    double outsideSim;
    long lastBegin;
    double frameMs;
    // The whole frame's wall time, from one BeginFrame to the next (the simulation, the host, the waits for a free frame and for the GPU included): kept with the render
    // thread's history, a frame late, and drawn on both charts.
    readonly float[] frameTime = new float[History];

    /// <summary>Render-thread work before <see cref="BeginFrame"/> that belongs to the next frame: the game's simulation ticks (<see cref="StageClock.Sim"/>), the last call's ms.</summary>
    public void Outside(double simMs) => outsideSim = simMs;

    // The simulation's parts (Outside with parts): the names, how many lead as phases (the last of them the time outside the world's ticks), how many systems follow (listed by
    // cost; the rest are parts of systems, listed in order), each one's ms and the world ticks per frame, listed under sim in the legend, not plotted. simNow is the newest
    // call's, kept with the frame at EndFrame.
    string[] simNames = [];
    int simPhases, simSystems, simPaths;
    float[][] simParts = [];
    readonly float[] simTicks = new float[History];
    double[] simNow = [];
    int simNowTicks;
    readonly List<(string Name, double Ms)> simRows = [];

    /// <summary>
    /// <see cref="Outside(double)"/> with the ticks' breakdown: <paramref name="names"/> (the same array while the parts stay the same) with their <paramref name="ms"/>, the first
    /// <paramref name="phases"/> of them phases shown in order (the last the time outside the ticks), the next <paramref name="systems"/> shown by cost, the rest (parts of systems)
    /// in order; <paramref name="ticks"/> the world ticks run; <paramref name="pathsWaiting"/> the path requests not answered yet.
    /// </summary>
    public void Outside(double simMs, int ticks, string[] names, ReadOnlySpan<double> ms, int phases, int systems, int pathsWaiting = 0)
    {
        simSystems = systems;
        simPaths = pathsWaiting;
        outsideSim = simMs;
        if (!ReferenceEquals(names, simNames))
        {
            simNames = names;
            simParts = names.Select(_ => new float[History]).ToArray();
            simNow = new double[names.Length];
        }
        simPhases = phases;
        ms[..Math.Min(ms.Length, simNow.Length)].CopyTo(simNow);
        simNowTicks = ticks;
    }

    /// <summary>After the frame is presented: the last stage (overlay, submit, present) ends and the frame's laps are kept.</summary>
    public void EndFrame()
    {
        // Its GPU stamp would land in the next frame's commands: the CPU lap only.
        StageClock.Profiler = null;
        StageClock.EndFrame();
        submitted[slot] = StageClock.SubmissionStarted;
        pending[slot] = stampCount[slot] > 1;
        StampsLastFrame = stampCount[slot] + preCount[slot];
        slotWhole[slot] = native.CurrentFrameStamps;   // complete when the context has ended the frame already (else the total falls back to gpuFrameMs)
        slotPreEnd[slot] = native.LastFrameStamps.PreEnd;
        double sum = 0;
        for (int s = 0; s < Stages; s++)
        {
            cpu[s][cpuHead] = (float)StageClock.Ms[s];
            sum += StageClock.Ms[s];
        }
        for (int p = 0; p < parts.Length; p++) parts[p][cpuHead] = (float)StageClock.PartMs[p];
        for (int p = 0; p < simParts.Length; p++) simParts[p][cpuHead] = (float)simNow[p];
        simTicks[cpuHead] = simNowTicks;
        Array.Clear(simNow);
        simNowTicks = 0;
        cpu[Other][cpuHead] = 0;
        cpu[Total][cpuHead] = (float)sum;
        frameTime[cpuHead] = (float)frameMs;
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

    /// <summary>
    /// A stamp in the pre-frame command buffer <paramref name="cmd"/>, called at the edges of the work that buffer holds: the time since the buffer's previous stamp (the first
    /// is the frame's start) goes to <paramref name="stage"/>. So a span is closed by <c>PreStamp(cmd, StageClock.Uploads)</c> just before it (whatever was recorded since the last stamp is uploads)
    /// and <c>PreStamp(cmd, stage)</c> after it. Nothing outside a profiled frame; the time after the last stamp, up to the buffer's end, is uploads too.
    /// </summary>
    internal static void PreStamp(IGpuCommandList cmd, int stage) => StageClock.Profiler?.RecordPre(cmd, stage);

    void RecordPre(IGpuCommandList cmd, int stage)
    {
        int n = preCount[slot];
        if (n >= MaxPre) return;
        var q = native.Frame.Timestamps.Allocate();
        if (!q.IsValid) return;
        cmd.Timestamp(native.Frame.Timestamps, q);
        preSlots[slot, n] = q;
        preStage[slot, n] = stage;
        preCount[slot] = n + 1;
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
        // Device-local blocks only: the host-visible ones (staging, constants) are system memory, not in the driver's VRAM figure.
        var (empty, spare, scattered, _) = alloc.Slack();
        ulong free = empty + spare + scattered, blocks = ours + free;
        if (free > 0) slices.Add(("free in our blocks", free));
        if (vram.Used > blocks) slices.Add(("driver and others", vram.Used - blocks));
        return slices;
    }

    /// <summary>
    /// Writes what is in video memory now to <paramref name="path"/> (Ctrl+Shift+F12 in the game): the driver's usage and budget, the room in our
    /// blocks, the owners, then every resource and block (<see cref="GpuAllocator.WriteReport"/>). What the driver counts beyond our blocks
    /// (swapchain, DLSS, its own) is only a total here: a RenderDoc or Nsight capture lists it.
    /// </summary>
    public void DumpVram(string path, Action<TextWriter>? more = null)
    {
        var alloc = native.Device.Allocator;
        var (used, budget) = native.Device.VideoMemory();
        var (empty, spare, scattered, partBlocks) = alloc.Slack();
        ulong blocks = alloc.TotalAllocatedBytes;
        using var w = new StreamWriter(path);
        w.WriteLine($"VRAM dump {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        w.WriteLine($"driver: used {used / 1048576.0:0.0} MB of budget {budget / 1048576.0:0.0} MB (memory budget extension: {native.Device.HasMemoryBudget})");
        var heaps = native.Device.Heaps();
        for (int i = 0; i < heaps.Count; i++)
            w.WriteLine($"  heap {i}: {heaps[i].Size / 1048576.0:0} MB, ours {heaps[i].Usage / 1048576.0:0.0} MB of budget {heaps[i].Budget / 1048576.0:0.0} MB ({heaps[i].Flags})");
        ulong local = (ulong)alloc.Breakdown().Sum(o => (double)o.DeviceLocal) + empty + spare + scattered;
        w.WriteLine($"ours: {blocks / 1048576.0:0.0} MB in {alloc.BlockCount} blocks ({local / 1048576.0:0.0} MB device-local), {alloc.TotalUsedBytes / 1048576.0:0.0} MB used by {alloc.AllocationCount} allocations; "
            + $"driver and others {(used > local ? used - local : 0) / 1048576.0:0.0} MB");
        w.WriteLine($"room in device-local blocks: empty {empty / 1048576.0:0.0} MB, spare {spare / 1048576.0:0.0} MB, scattered {scattered / 1048576.0:0.0} MB in {partBlocks} blocks");
        w.WriteLine();
        w.WriteLine("owners (MB, device-local MB, count):");
        foreach (var o in alloc.Breakdown()) w.WriteLine($"{o.Bytes / 1048576.0,10:0.0}  {o.DeviceLocal / 1048576.0,10:0.0}  {o.Count,6}  {o.Name}");
        w.WriteLine();
        more?.Invoke(w);
        alloc.WriteReport(w);
    }

    /// <summary>The VRAM pie at the top right is shown (Shift+F12 in the game), with the profiler chart or without it.</summary>
    public bool ShowVram { get; set; }

    // VRAM (a driver call) and RAM are read every 30 frames, for the chart's header and the pie; only while either is shown.
    void RefreshVram()
    {
        if (vramAge-- <= 0)
        {
            vram = native.Device.VideoMemory();
            vramSlices = VramSlices();
            ram = (Environment.WorkingSet, GC.GetTotalMemory(false));
            vramAge = 30;
        }
    }

    (long WorkingSet, long Managed) ram;

    /// <summary>The VRAM pie at the top right, while <see cref="ShowVram"/>.</summary>
    public void DrawVram(DebugOverlay overlay, int width, int height)
    {
        if (!ShowVram || width <= 0 || height <= 0) return;
        RefreshVram();
        DrawVramPie(overlay, width);
        overlay.Flush(width, height);
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
    readonly Action<IGpuCommandList> recordStamp;

    bool TryStamp(int s, int i, out ulong ns) => native.Frame.Timestamps.TryRead(nativeStamps[s, i], out ns);

    void Collect(int s)
    {
        if (!pending[s]) return;
        int n = stampCount[s];
        if (!TryStamp(s, n - 1, out _)) return;
        Array.Clear(gpuFrame);
        var arena = native.Frame.Timestamps;
        var whole = slotWhole[s];
        double sum = 0;
        // The pre-frame command buffer, from the frame's first stamp: its spans (cull, bakes, the ocean) and what is left up to its end (uploads).
        if (arena.TryRead(whole.Begin, out ulong cursor))
        {
            for (int i = 0; i < preCount[s]; i++)
            {
                if (!arena.TryRead(preSlots[s, i], out ulong at)) continue;
                double span = at >= cursor ? (at - cursor) / 1e6 : 0;
                gpuFrame[preStage[s, i]] += span;
                sum += span;
                cursor = Math.Max(cursor, at);
            }
            if (arena.TryRead(slotPreEnd[s], out ulong preEnd) && preEnd >= cursor)
            {
                gpuFrame[StageClock.Uploads] += (preEnd - cursor) / 1e6;
                sum += (preEnd - cursor) / 1e6;
            }
        }
        // The stamped stages of the frame's own commands (the first stamp is the frame's start there: after the pre-frame buffer, so a gap between the two stays in "other").
        TryStamp(s, 0, out ulong previous);
        for (int i = 1; i < n; i++)
        {
            TryStamp(s, i, out ulong now);
            double ms = now >= previous ? (now - previous) / 1e6 : 0;
            gpuFrame[stampStage[s, i]] += ms;
            sum += ms;
            previous = now;
        }
        // UI ends before the window copy. Without a submission boundary the tail still belongs to UI.
        if (arena.TryRead(whole.End, out ulong tailEnd) && tailEnd >= previous)
        {
            gpuFrame[submitted[s] ? StageClock.Submit : StageClock.Ui] += (tailEnd - previous) / 1e6;
            sum += (tailEnd - previous) / 1e6;
        }
        // The frame's own total (first to last stamp); the context's latest completed frame is only a stand-in and, with frames in flight, another frame.
        double own = arena.TryRead(whole.Begin, out ulong wb) && arena.TryRead(whole.End, out ulong we) && we >= wb ? (we - wb) / 1e6 : 0;
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

        if (simOpen) SimRows(meanFrames);
        int rowCount = 0;
        for (int gi = 0; gi < Groups.Length; gi++)
        {
            var stages = Groups[gi].Stages;
            if (stages.Length > 1) rowCount++;
            if (stages.Length == 1 || groupOpen[gi])
                foreach (int s in stages) rowCount += 1 + (s == StageClock.UpdChars && partsOpen ? parts.Length : 0) + (s == StageClock.Sim && simOpen ? simRows.Count + 2 : 0);
        }
        float panelH = (rowCount + 2.5f) * lh + 2 * pad;
        float x0 = margin, x1 = width - margin, y1 = height - margin, y0 = Math.Max(y1 - panelH, margin);
        overlay.Rect(x0, y0, x1, y1, DebugOverlay.PanelColour);
        RefreshVram();
        // The process's RAM: its working set (all it holds in physical memory) and the managed heap within it.
        string header = string.Create(CultureInfo.InvariantCulture, $"{(showGpu ? "gpu" : "cpu")}   vram {vram.Used / 1073741824.0:0.00} of {vram.Budget / 1073741824.0:0.0} GB   " +
            $"ram {ram.WorkingSet / 1073741824.0:0.00} GB (managed {ram.Managed / 1073741824.0:0.00})   ");
        overlay.Text(header, x0 + pad, y0 + pad, DebugOverlay.TextColour);
        // The whole frame (FrameColour line): mean of the last frames, frames a second and the longest kept.
        double frameMean = Mean(frameTime, cpuHead, cpuCount, meanFrames);
        overlay.Text(string.Create(CultureInfo.InvariantCulture, $"frame {frameMean:0.00} ms ({(frameMean > 0 ? 1000 / frameMean : 0):0} fps), max {Max(frameTime, cpuHead, cpuCount):0.00}"),
            x0 + pad + header.Length * cw, y0 + pad, FrameColour);

        // Legend at the right: swatch, row, mean of the last frames on the CPU and the GPU. A row marked + opens (a click), - closes; a closed group shows its
        // stages' sum and is plotted as one line, an open one plots each stage.
        float legendW = swatch + 6 + (16 + 10 + 10) * cw;
        float lx = x1 - pad - legendW, ly = y0 + pad + 1.5f * lh;
        var dim = new Vector4(0.7f, 0.7f, 0.68f, 1);
        overlay.Text($"{"",-16}{"cpu",10}{"gpu",10}", lx + swatch + 6, ly, dim);
        rows.Clear();
        plotted.Clear();
        string Ms(bool shown, double ms) => shown ? ms.ToString("0.00", CultureInfo.InvariantCulture) : "-";
        float NextRow(int opens)
        {
            float top = ly + (rows.Count + 1) * lh;
            rows.Add((top, opens));
            return top;
        }
        bool Plots(int s) => showGpu ? OnGpu(s) : OnCpu(s);
        void StageRow(int s, string indent)
        {
            bool expands = s == StageClock.UpdChars || s == StageClock.Sim && simNames.Length > 0;
            bool open = s == StageClock.Sim ? simOpen : partsOpen;
            float top = NextRow(!expands ? -1 : s == StageClock.Sim ? SimRow : PartsRow);
            overlay.Rect(lx, top + 4, lx + swatch, top + 4 + swatch, Colours[s]);
            if (Plots(s)) plotted.Add((hist[s], Colours[s], s == Total ? 2f : 1.5f, head, count));
            string label = indent + (expands ? open ? "- " : "+ " : "  ") + Label(s);
            overlay.Text($"{label,-16}{Ms(OnCpu(s), Mean(cpu[s], cpuHead, cpuCount, meanFrames)),10}{Ms(gpuCount > 0 && OnGpu(s), Mean(gpu[s], gpuHead, gpuCount, meanFrames)),10}",
                lx + swatch + 6, top, DebugOverlay.TextColour);
            if (!expands || !open) return;
            if (s == StageClock.Sim)
            {
                // The world ticks a frame ran and the mean tick, then the phases, the time outside the ticks and the costliest systems.
                double ticks = Mean(simTicks, cpuHead, cpuCount, meanFrames), inTicks = 0;
                for (int p = 0; p < simPhases - 1; p++) inTicks += simRows[p].Ms;
                overlay.Text(string.Create(CultureInfo.InvariantCulture, $"{indent}    {ticks:0.0} ticks, {(ticks > 0 ? inTicks / ticks : 0):0.00} ms each"), lx + swatch + 6, NextRow(-1), dim);
                overlay.Text($"{indent}    {simPaths} paths waiting", lx + swatch + 6, NextRow(-1), dim);
                foreach (var (name, ms) in simRows)
                {
                    string row = indent + "    " + name;
                    overlay.Text($"{(row.Length > 16 ? row[..16] : row),-16}{Ms(true, ms),10}{"",10}", lx + swatch + 6, NextRow(-1), dim);
                }
                return;
            }
            // Its parts, render thread only (the GPU work they record is in the passes that run it); not plotted.
            for (int p = 0; p < parts.Length; p++)
                overlay.Text($"{indent + "    " + StageClock.PartNames[p],-16}{Ms(true, Mean(parts[p], cpuHead, cpuCount, meanFrames)),10}{"",10}", lx + swatch + 6, NextRow(-1), dim);
        }
        for (int gi = 0; gi < Groups.Length; gi++)
        {
            var (name, stages) = Groups[gi];
            if (stages.Length == 1) { StageRow(stages[0], ""); continue; }
            double c = 0, g = 0;
            bool anyCpu = false, anyGpu = false;
            foreach (int s in stages)
            {
                if (OnCpu(s)) { c += Mean(cpu[s], cpuHead, cpuCount, meanFrames); anyCpu = true; }
                if (OnGpu(s)) { g += Mean(gpu[s], gpuHead, gpuCount, meanFrames); anyGpu = true; }
            }
            float top = NextRow(gi);
            bool open = groupOpen[gi], shown = showGpu ? anyGpu : anyCpu;
            var sum = groupSeries[gi];
            Array.Clear(sum);
            foreach (int s in stages)
                if (Plots(s)) for (int i = 0; i < History; i++) sum[i] += hist[s][i];
            if (!open)
            {
                overlay.Rect(lx, top + 4, lx + swatch, top + 4 + swatch, Colours[stages[0]]);
                if (shown) plotted.Add((sum, Colours[stages[0]], 1.5f, head, count));
            }
            overlay.Text($"{(open ? "- " : "+ ") + name,-16}{Ms(anyCpu, c),10}{Ms(gpuCount > 0 && anyGpu, g),10}",
                lx + swatch + 6, top, DebugOverlay.TextColour);
            if (open) foreach (int s in stages) StageRow(s, "  ");
        }
        legendBox = (lx, ly, x1 - pad, ly + (rows.Count + 1) * lh, lh);
        // The whole frame, on both charts (the render thread's history: a GPU chart's newest frames are a few behind it).
        plotted.Add((frameTime, FrameColour, 2f, cpuHead, cpuCount));

        // The plot: y from 0 to a round number above the largest value shown, with labelled grid lines.
        float px0 = x0 + pad + 7 * cw, px1 = lx - 3 * cw, py0 = y0 + pad + 1.5f * lh + lh * 0.5f, py1 = y1 - pad - lh * 0.5f;
        if (px1 - px0 < 50 || py1 - py0 < 30) { overlay.Flush(width, height); return; }
        float max = 0;
        foreach (var (series, _, _, _, n) in plotted)
            for (int i = 0; i < n; i++) max = Math.Max(max, series[i]);
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
        {
            float dx = (px1 - px0) / (History - 1);
            // Oldest at the left; the newest frame at the right edge.
            foreach (var (series, colour, thickness, h, n) in plotted)
            {
                if (n < 2) continue;
                // A run of points within a quarter pixel of the height it started at is one segment: most stages lie flat near 0, and the
                // 300 segments a series would otherwise queue made the plot cost as much as the HUD.
                float runX = 0, runY = 0, lastX = 0, lastY = 0;
                for (int i = n - 1; i >= 0; i--)
                {
                    float v = series[(h - 1 - i + History) % History];
                    float x = px1 - i * dx, y = py1 - (py1 - py0) * Math.Clamp(v / scale, 0, 1);
                    if (i == n - 1) (runX, runY) = (x, y);
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
        overlay.Flush(width, height);
    }

    /// <summary>A left click at <paramref name="at"/> (framebuffer pixels): on a legend row marked + or - it opens or closes that row. True when it was on the legend (the caller drops it).</summary>
    public bool Click(Vector2 at)
    {
        var (bx0, by0, bx1, by1, lh) = legendBox;
        if (Showing == Mode.Off || at.X < bx0 || at.X > bx1 || at.Y < by0 || at.Y > by1) return false;
        foreach (var (top, opens) in rows)
        {
            if (at.Y < top || at.Y >= top + lh) continue;
            if (opens >= 0) groupOpen[opens] = !groupOpen[opens];
            else if (opens == PartsRow) partsOpen = !partsOpen;
            else if (opens == SimRow) simOpen = !simOpen;
            break;
        }
        return true;
    }

    /// <summary>
    /// The sim rows to list: the phases in order, then the systems costing the most over the last <paramref name="frames"/> frames and the rest as one row, then the systems'
    /// parts in order.
    /// </summary>
    void SimRows(int frames)
    {
        const int Shown = 10;
        simRows.Clear();
        int systemsEnd = Math.Min(simParts.Length, simPhases + simSystems);
        for (int p = 0; p < simPhases && p < simParts.Length; p++) simRows.Add((simNames[p], Mean(simParts[p], cpuHead, cpuCount, frames)));
        var systems = Enumerable.Range(simPhases, Math.Max(0, systemsEnd - simPhases))
            .Select(p => (Name: "  " + simNames[p], Ms: Mean(simParts[p], cpuHead, cpuCount, frames))).OrderByDescending(r => r.Ms).ToList();
        simRows.AddRange(systems.Take(Shown));
        if (systems.Count > Shown) simRows.Add(("  other systems", systems.Skip(Shown).Sum(r => r.Ms)));
        for (int p = systemsEnd; p < simParts.Length; p++) simRows.Add(("  " + simNames[p], Mean(simParts[p], cpuHead, cpuCount, frames)));
    }

    public void Dispose()
    {
        if (StageClock.Profiler == this) StageClock.Profiler = null;
    }
}
