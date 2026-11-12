using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Meitou.Game.Host;
using Meitou.Game.Rendering.Profiling;
using Meitou.Simulation;
using Meitou.Simulation.Factions;

namespace Meitou.Game.DevTools;

/// <summary>
/// <c>--play-bench &lt;dir&gt;</c> (docs/bench.md, "Play benchmark"): the interactive game driven by a script instead of a player, every frame
/// measured. Once the world is up the script runs its segments (idle, camera orbit, zoom, pan, a long travel, the return, time x2 and x5, talking,
/// trading, a container, doors, a pick-up, path requests, the squad walking, a fight, looting, the interface panels cold and warm, a horizon view,
/// the overview) and records per frame the interval, the update and the world ticks (their steps and systems, <see cref="TickProfile"/>), the render
/// thread's stages and the GPU's (<see cref="FrameProfiler"/>), the garbage collections, allocation and memory; then it writes frames.csv and
/// summary.md and quits. The frame limit and vsync are off for it.
/// </summary>
sealed class PlayBench(GameHost host)
{
    /// <summary>
    /// One recorded frame: its segment, times, ticks, world counts, steps, stages, collections, memory and loop sections.
    /// </summary>
    sealed class BenchFrame
    {
        /// <summary>
        /// The script segment the frame fell in.
        /// </summary>
        public int Segment;

        /// <summary>
        /// The bench clock at the frame, the frame interval and the update in milliseconds, and the busy milliseconds the game loop measured to the
        /// presentation.
        /// </summary>
        public double T, FrameMs, UpdateMs, Busy;

        /// <summary>
        /// The world ticks the update ran, the characters in the snapshot and those the far tick skipped.
        /// </summary>
        public int Ticks, Characters, Far;

        /// <summary>
        /// The tick phases' milliseconds (<see cref="TickProfile.PhaseNames"/>).
        /// </summary>
        public double[] Steps = new double[TickProfile.PhaseNames.Length];

        /// <summary>
        /// The render thread's stages' milliseconds (<see cref="StageClock.Names"/>).
        /// </summary>
        public double[] Cpu = new double[StageClock.Names.Length];

        /// <summary>
        /// The GPU stages' milliseconds, once the profiler reads them back; null before.
        /// </summary>
        public double[]? Gpu;

        /// <summary>
        /// The GPU's total milliseconds for the frame.
        /// </summary>
        public double GpuTotal;

        /// <summary>
        /// The generation 0, 1 and 2 collections since the last frame.
        /// </summary>
        public int Gc0, Gc1, Gc2;

        /// <summary>
        /// The megabytes allocated and the collection pause milliseconds since the last frame, and the working set's megabytes.
        /// </summary>
        public double AllocMb, PauseMs, WorkingSetMb;

        /// <summary>
        /// This process's video memory (<c>VulkanDevice.VideoMemory</c>), MB: read every 30 frames (a driver call), carried between; NaN on Metal.
        /// </summary>
        public double VramMb = double.NaN;

        /// <summary>
        /// The loop sections' milliseconds (<see cref="BenchSections"/>).
        /// </summary>
        public double[] Sections = new double[BenchSections.Length];

        /// <summary>
        /// The camera's and the handlers' milliseconds in the update, and the GC listener's time at the frame's start.
        /// </summary>
        public double CameraMs, HandlersMs, StartMs;

        /// <summary>
        /// The frame's slowest system phase ("System.phase") and its time.
        /// </summary>
        public string TopSystem = "";

        /// <summary>
        /// Pipelines the frame created while drawing (nothing prepared them at load).
        /// </summary>
        public int LatePipelines;

        /// <summary>
        /// The slowest system phase's milliseconds.
        /// </summary>
        public double TopSystemMs;
    }

    /// <summary>
    /// The GC event listener (collections and allocation samples); null until the script starts.
    /// </summary>
    BenchGcListener? _benchGc;

    /// <summary>
    /// The listener's time when the script started.
    /// </summary>
    double _benchGcStart;

    /// <summary>
    /// The game loop's sections a frame's wall time splits into (<see cref="BenchLap"/>): the last runs from the end of the drawing to the next
    /// frame.
    /// </summary>
    static readonly string[] BenchSections = ["events", "update", "host", "camera", "audio", "view", "begin-frame", "render", "tail"];

    /// <summary>
    /// The index of the tail section (the end of the drawing to the next frame).
    /// </summary>
    internal const int BenchTail = 8;

    /// <summary>
    /// The Stopwatch time of the last section's end; 0 before the first.
    /// </summary>
    long _benchLapAt;

    /// <summary>
    /// This frame's sections' milliseconds so far.
    /// </summary>
    readonly double[] _benchLaps = new double[BenchSections.Length];

    /// <summary>
    /// Ends loop section <paramref name="section"/> (<see cref="BenchSections"/>) for the benchmark; the tail goes to the frame recorded last.
    /// </summary>
    internal void BenchLap(int section)
    {
        if (host.GameOptions.PlayBench is null) return;
        long now = Stopwatch.GetTimestamp();
        double ms = _benchLapAt == 0 ? 0 : (now - _benchLapAt) * 1000.0 / Stopwatch.Frequency;
        _benchLapAt = now;
        if (section == BenchTail)
        {
            if (_benchFrames.Count > 0) _benchFrames[^1].Sections[BenchTail] = ms;
            Array.Clear(_benchLaps);
            return;
        }
        _benchLaps[section] += ms;
    }

    /// <summary>
    /// The script's segments; null until the world is ready for it.
    /// </summary>
    BenchSegment[]? _benchScript;

    /// <summary>
    /// The frames recorded.
    /// </summary>
    readonly List<BenchFrame> _benchFrames = [];

    /// <summary>
    /// The summed milliseconds per segment and system.
    /// </summary>
    readonly Dictionary<(int Segment, string System), double> _benchSystems = [];

    /// <summary>
    /// The running segment; -1 before the first.
    /// </summary>
    int _benchSegment = -1;

    /// <summary>
    /// The bench clock, the running segment's start on it, and how long the world has been ready (the script starts after 1 s).
    /// </summary>
    double _benchClock, _benchSegmentStart, _benchReadyFor;

    /// <summary>
    /// The world tick and the allocated bytes at the frame's start and the last frame's.
    /// </summary>
    long _benchTickAt, _benchAllocAt;

    /// <summary>
    /// The late pipelines counted at the last frame.
    /// </summary>
    int _benchLateAt;

    /// <summary>
    /// The collection counts at the last frame.
    /// </summary>
    int[] _benchGcAt = new int[3];

    /// <summary>
    /// The collection pause total at the last frame.
    /// </summary>
    TimeSpan _benchPauseAt;

    /// <summary>
    /// The video memory last read, MB (every 30 frames).
    /// </summary>
    double _benchVramMb = double.NaN;

    /// <summary>
    /// The frame being recorded; null between frames.
    /// </summary>
    BenchFrame? _benchCurrent;

    /// <summary>
    /// The camera pivot when the script started, and the direction from it towards the world's centre.
    /// </summary>
    Vector2 _benchOrigin, _benchDirection;

    /// <summary>
    /// The squad leader the script follows.
    /// </summary>
    CharacterId _benchLeader;

    /// <summary>
    /// The frame's script step (before the world's update): false once the script is done. Waits for the world (no loading screen, a player
    /// character) first.
    /// </summary>
    internal bool PlayBenchFrame(double dt, FrameProfiler? profiler)
    {
        if (host.GameOptions.PlayBench is null) return true;
        if (_benchScript is null)
        {
            var players = host.Session.CurrentSnapshot.Characters.Where(c => c.IsPlayer).ToList();
            if (host.Covered || host.TitleScreen.TitleVisible || players.Count == 0)
            {
                _benchReadyFor = 0;
                return true;
            }

            if ((_benchReadyFor += dt) < 1) return true;
            _benchLeader = (host.PlayerSquad is { Leader.IsNone: false } squad ? squad.Leader : players[0].Id);
            _benchOrigin = host.Session.Camera.Strategy.PivotXz;
            _benchDirection = _benchOrigin.LengthSquared() > 1 ? Vector2.Normalize(-_benchOrigin) : Vector2.UnitX;
            _benchScript = new PlayBenchScript(host, _benchLeader, _benchOrigin, _benchDirection).Segments();
            // MEITOU_BENCH_ONLY=settle,zoom: only those segments (iterating on one hitch).
            if (Environment.GetEnvironmentVariable("MEITOU_BENCH_ONLY") is { Length: > 0 } only)
            {
                var keep = only.Split(',');
                _benchScript = [.. _benchScript.Where(s => keep.Contains(s.Name))];
                if (_benchScript.Length == 0)
                {
                    Console.WriteLine($"bench     no segment named {only}: nothing to run");
                    return false;
                }
            }
            _benchGc = new BenchGcListener();
            _benchGcStart = _benchGc.NowMs;
            if (profiler is not null) profiler.OnGpuFrame = (tag, stages, total) =>
            {
                if (tag >= 0 && tag < _benchFrames.Count)
                    (_benchFrames[(int)tag].Gpu, _benchFrames[(int)tag].GpuTotal) = ([.. stages], total);
            };
            Console.WriteLine($"bench     the script starts: {_benchScript.Length} segments, {_benchScript.Sum(s => s.Seconds):0} s, "
                + $"leader {_benchLeader}, from {_benchOrigin.X:0}, {_benchOrigin.Y:0}");
            StartBenchSegment(0);
        }
        _benchClock += dt;
        if (_benchClock - _benchSegmentStart >= _benchScript[_benchSegment].Seconds)
        {
            if (_benchSegment + 1 >= _benchScript.Length) return false;
            StartBenchSegment(_benchSegment + 1);
        }
        double t = _benchClock - _benchSegmentStart;
        // A modal screen (a message box) pauses the game until a player answers: the script says which and closes it.
        if (host.Player.ModalOpen && !_benchScript[_benchSegment].Name.StartsWith("panels", StringComparison.Ordinal))
        {
            Console.WriteLine($"bench     a modal screen paused the game: {string.Join(", ", host.Player.OpenScreens)}; closed");
            host.Player.ShowUiPanel(null);
        }
        _benchScript[_benchSegment].Frame?.Invoke(t, dt);
        _benchCurrent = new BenchFrame { Segment = _benchSegment, T = _benchClock, FrameMs = dt * 1000,
            Characters = host.Session.CurrentSnapshot.Characters.Count, Far = host.Session.World.FarSkipped, StartMs = _benchGc?.NowMs ?? 0 };
        if (profiler is not null) profiler.Tag = _benchFrames.Count;
        host.Session.World.Profile.Clear();
        (host.Session.CameraMs, host.Session.HandlersMs) = (0, 0);
        _benchTickAt = host.Session.World.Tick;
        return true;
    }

    /// <summary>
    /// Starts segment <paramref name="index"/>: its start on the bench clock, and its begin step.
    /// </summary>
    void StartBenchSegment(int index)
    {
        _benchSegment = index;
        _benchSegmentStart = _benchClock;
        Console.WriteLine($"bench     {_benchScript![index].Name} ({_benchScript[index].Seconds:0} s)");
        _benchScript[index].Begin?.Invoke();
    }

    /// <summary>
    /// After the world's update: its wall time and the ticks it ran.
    /// </summary>
    internal void PlayBenchUpdated(double ms)
    {
        if (_benchCurrent is not { } f) return;
        f.UpdateMs = ms;
        (f.CameraMs, f.HandlersMs) = (host.Session.CameraMs, host.Session.HandlersMs);
        f.Ticks = (int)(host.Session.World.Tick - _benchTickAt);
        var profile = host.Session.World.Profile;
        Array.Copy(profile.PhaseMs, f.Steps, f.Steps.Length);
        var systems = host.Session.World.Systems;
        for (int k = 0; k < profile.SystemMs.Length && k < systems.Count; k++)
        {
            string system = systems[k].GetType().Name;
            double value = profile.SystemMs[k];
            _benchSystems[(f.Segment, system)] = _benchSystems.GetValueOrDefault((f.Segment, system)) + value;
            if (value > f.TopSystemMs) (f.TopSystem, f.TopSystemMs) = (system, value);
        }
    }

    /// <summary>
    /// After the frame is presented: the render thread's stages (StageClock, read before the next frame starts it), the collections and memory.
    /// </summary>
    internal void PlayBenchRendered(double busyMs, int latePipelines)
    {
        if (_benchCurrent is not { } f) return;
        (f.LatePipelines, _benchLateAt) = (_benchFrames.Count > 0 ? latePipelines - _benchLateAt : 0, latePipelines);
        Array.Copy(StageClock.Ms, f.Cpu, f.Cpu.Length);
        Array.Copy(_benchLaps, f.Sections, BenchTail);
        f.Busy = busyMs;
        int[] gc = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        long alloc = GC.GetTotalAllocatedBytes(false);
        var pause = GC.GetTotalPauseDuration();
        if (_benchFrames.Count > 0)
        {
            (f.Gc0, f.Gc1, f.Gc2) = (gc[0] - _benchGcAt[0], gc[1] - _benchGcAt[1], gc[2] - _benchGcAt[2]);
            f.AllocMb = (alloc - _benchAllocAt) / 1048576.0;
            f.PauseMs = (pause - _benchPauseAt).TotalMilliseconds;
        }
        (_benchGcAt, _benchAllocAt, _benchPauseAt) = (gc, alloc, pause);
        f.WorkingSetMb = Environment.WorkingSet / 1048576.0;
        if (_benchFrames.Count % 30 == 0 && host.Gpu.Sky.Gpu is Rendering.Gpu.GpuContext vk)
            _benchVramMb = vk.Device.VideoMemory().Used / 1048576.0;
        f.VramMb = _benchVramMb;
        _benchFrames.Add(f);
        _benchCurrent = null;
    }

    /// <summary>
    /// Writes frames.csv and summary.md into the bench directory (after the loop).
    /// </summary>
    internal void WritePlayBench(Silk.NET.Maths.Vector2D<int> size)
    {
        if (host.GameOptions.PlayBench is not { } dir || _benchScript is null) return;
        Directory.CreateDirectory(dir);
        var ci = CultureInfo.InvariantCulture;
        var stages = StageClock.Names;
        var csv = new StringBuilder();
        csv.Append("frame,segment,t,frame_ms,update_ms,busy_ms,ticks,characters,far,gpu_total_ms,gc0,gc1,gc2,alloc_mb,gc_pause_ms,working_set_mb,"
            + "vram_mb");
        foreach (var s in BenchSections) csv.Append(",loop_").Append(s);
        csv.Append(",control_camera_ms,control_handlers_ms,top_system,top_system_ms,late_pipelines");
        foreach (var s in TickProfile.PhaseNames) csv.Append(",sim_").Append(s.Replace(' ', '-'));
        foreach (var s in stages) csv.Append(",cpu_").Append(s);
        foreach (var s in stages) csv.Append(",gpu_").Append(s);
        csv.AppendLine();
        for (int i = 0; i < _benchFrames.Count; i++)
        {
            var f = _benchFrames[i];
            csv.Append(ci, $"{i},{_benchScript[f.Segment].Name},{f.T:0.000},{f.FrameMs:0.00},{f.UpdateMs:0.00},{f.Busy:0.00},{f.Ticks},{f.Characters},"
                + $"{f.Far},{f.GpuTotal:0.00},{f.Gc0},{f.Gc1},{f.Gc2},{f.AllocMb:0.00},{f.PauseMs:0.00},{f.WorkingSetMb:0},"
                + $"{(double.IsNaN(f.VramMb) ? "" : f.VramMb.ToString("0", ci))}");
            foreach (var v in f.Sections) csv.Append(ci, $",{v:0.000}");
            csv.Append(ci, $",{f.CameraMs:0.000},{f.HandlersMs:0.000},{f.TopSystem},{f.TopSystemMs:0.000},{f.LatePipelines}");
            foreach (var v in f.Steps) csv.Append(ci, $",{v:0.000}");
            foreach (var v in f.Cpu) csv.Append(ci, $",{v:0.000}");
            for (int s = 0; s < stages.Length; s++)
                csv.Append(ci, $",{(f.Gpu is { } g2 && s < g2.Length ? g2[s] : double.NaN):0.000}");
            csv.AppendLine();
        }
        File.WriteAllText(Path.Combine(dir, "frames.csv"), csv.ToString());
        File.WriteAllText(Path.Combine(dir, "summary.md"), BenchSummary(size) + GcSummary());
        _benchGc?.Dispose();
        Console.WriteLine($"bench     {_benchFrames.Count} frames: {Path.GetFullPath(Path.Combine(dir, "summary.md"))}");
    }

    /// <summary>
    /// The collections (generation, reason, pause) and the allocation samples by type, from <see cref="BenchGcListener"/>.
    /// </summary>
    string GcSummary()
    {
        if (_benchGc is null) return "";
        var ci = CultureInfo.InvariantCulture;
        var (allocated, collections) = _benchGc.Take();
        var sb = new StringBuilder("\n## Garbage collector\n\n");
        double seconds = (_benchGc.NowMs - _benchGcStart) / 1000;
        foreach (var g2 in collections.GroupBy(c => c.Generation).OrderBy(g2 => g2.Key))
        {
            var p = g2.Select(c => c.PauseMs).Order().ToList();
            sb.Append(ci, $"- gen{g2.Key}: {p.Count} collections, pause median {p[p.Count / 2]:0.0} ms, "
                + $"p95 {p[Math.Min(p.Count - 1, (int)(0.95 * p.Count))]:0.0}, max {p[^1]:0.0}, total {p.Sum():0} ms; "
                + $"reasons {string.Join(", ", g2.GroupBy(c => c.Reason).Select(r => $"{r.Key}x{r.Count()}"))}\n");
        }
        // The longer pauses with the frame (and segment) they fell in: the last frame started before the pause.
        var starts = _benchFrames.Select(f => f.StartMs).ToArray();
        var longer = collections.Where(c => c.PauseMs >= 5).OrderByDescending(c => c.PauseMs).Take(30).ToList();
        if (longer.Count > 0)
        {
            sb.Append("\nPauses of 5 ms or more, longest first (frame: the one running when it began):\n\n"
                + "| frame | segment | gen | reason | pause ms |\n|---|---|---|---|---|\n");
            foreach (var c in longer)
            {
                int i = Array.BinarySearch(starts, c.AtMs);
                if (i < 0) i = ~i - 1;
                string segment = i >= 0 && i < _benchFrames.Count ? _benchScript![_benchFrames[i].Segment].Name : "-";
                sb.Append(ci, $"| {i} | {segment} | {c.Generation} | {c.Reason} | {c.PauseMs:0.0} |\n");
            }
        }
        long total = allocated.Values.Sum();
        sb.Append(ci, $"\nAllocation samples: about {total / 1048576.0:0} MB over {seconds:0} s ({total / 1048576.0 / Math.Max(seconds, 1):0} MB/s). "
            + $"The 40 largest types:\n\n| type | MB | share |\n|---|---|---|\n");
        foreach (var (type, bytes) in allocated.OrderByDescending(kv => kv.Value).Take(40))
            sb.Append(ci, $"| `{type}` | {bytes / 1048576.0:0} | {100.0 * bytes / Math.Max(total, 1):0.0}% |\n");
        sb.Append("\nGC reasons: 0 small allocation, 1 induced, 2 low memory, 3 empty, 4 large allocation, 5 out of space (small), "
            + "6 out of space (large), 7 induced not forced.\n");
        return sb.ToString();
    }

    /// <summary>
    /// The summary (summary.md) for the window size <paramref name="sz"/>.
    /// </summary>
    string BenchSummary(Silk.NET.Maths.Vector2D<int> sz)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        var stages = StageClock.Names;
        string device = host.Gpu.Sky.Gpu is Rendering.Gpu.GpuContext vk ? vk.Device.DeviceName : "Metal";
        sb.Append(ci, $"# Play benchmark\n\n{_benchFrames.Count} frames, {sz.X}x{sz.Y}, {device}, upscaler {host.WorldOptions.Post.Upscale.Kind} "
            + $"at {host.WorldOptions.Post.Upscale.EffectiveScale:0.00}; frame limit and vsync off.\n\n");
        if (host.Gpu.Shadow is { Meitou: true } shadowPass)
            sb.Append(ci, $"Shadows: {shadowPass.DescribeMeitou()}.\n\n");
        sb.Append("| segment | frames | fps | frame ms p50 | p95 | p99 | max | update ms | ticks/frame | sim ms/tick | gpu ms | render cpu ms | "
            + "alloc MB/s | gen2 |\n|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
        for (int s = 0; s < _benchScript!.Length; s++)
        {
            var fs = _benchFrames.Where(f => f.Segment == s).ToList();
            if (fs.Count < 2) continue;
            var ms = fs.Select(f => f.FrameMs).ToList();
            double seconds = ms.Sum() / 1000, ticks = fs.Sum(f => f.Ticks), sim = fs.Sum(f => f.Steps.Sum());
            var gpuMs = fs.Where(f => f.Gpu is not null).Select(f => f.GpuTotal).ToList();
            sb.Append(ci, $"| {_benchScript[s].Name} | {fs.Count} | {fs.Count / seconds:0} | {P(ms, 0.5):0.0} | {P(ms, 0.95):0.0} | "
                + $"{P(ms, 0.99):0.0} | {ms.Max():0.0} | {fs.Average(f => f.UpdateMs):0.00} | {ticks / fs.Count:0.0} "
                + $"| {(ticks > 0 ? sim / ticks : 0):0.00} | "
                + $"{(gpuMs.Count > 0 ? gpuMs.Average() : 0):0.00} | {fs.Average(f => f.Cpu.Sum()):0.00} | {fs.Sum(f => f.AllocMb) / seconds:0} | "
                + $"{fs.Sum(f => f.Gc2)} |\n");
        }
        // Per segment: the stages that cost most on each side, the world tick's steps, the systems.
        for (int s = 0; s < _benchScript.Length; s++)
        {
            var fs = _benchFrames.Where(f => f.Segment == s).ToList();
            if (fs.Count < 2) continue;
            sb.Append(ci, $"\n## {_benchScript[s].Name}\n\n");
            string Top(Func<BenchFrame, double[]?> pick, string[] names, int n) => string.Join(", ", Enumerable.Range(0, names.Length)
                .Select(i => (Name: names[i], Ms: fs.Where(f => pick(f) is { } a && i < a.Length && double.IsFinite(a[i])).Select(f => pick(f)![i])
                .DefaultIfEmpty(0)
                .Average()))
                .OrderByDescending(x => x.Ms).Take(n).Where(x => x.Ms >= 0.01).Select(x => string.Create(ci, $"{x.Name} {x.Ms:0.00}")));
            sb.Append("- game loop ms/frame: ").Append(Top(f => f.Sections, BenchSections, 9)).Append('\n');
            sb.Append("- render thread ms/frame: ").Append(Top(f => f.Cpu, stages, 8)).Append('\n');
            sb.Append("- GPU ms/frame: ").Append(Top(f => f.Gpu, stages, 8)).Append('\n');
            sb.Append("- world tick ms/frame: ").Append(Top(f => f.Steps, TickProfile.PhaseNames, 6)).Append('\n');
            double frames = fs.Count;
            var systems = _benchSystems.Where(kv => kv.Key.Segment == s).OrderByDescending(kv => kv.Value).Take(8)
                .Select(kv => string.Create(ci, $"{kv.Key.System} {kv.Value / frames:0.00}"));
            sb.Append("- systems ms/frame: ").Append(string.Join(", ", systems)).Append('\n');
        }
        BenchStutters(sb, stages);
        var last = _benchFrames.Count > 0 ? _benchFrames[^1] : null;
        sb.Append(ci, $"\nCollections over the run: gen0 {_benchFrames.Sum(f => f.Gc0)}, gen1 {_benchFrames.Sum(f => f.Gc1)}, "
            + $"gen2 {_benchFrames.Sum(f => f.Gc2)}, pauses {_benchFrames.Sum(f => f.PauseMs):0} ms; "
            + $"allocated {_benchFrames.Sum(f => f.AllocMb):0} MB; "
            + $"working set at the end {last?.WorkingSetMb:0} MB.\n");
        var vram = _benchFrames.Where(f => !double.IsNaN(f.VramMb)).ToList();
        if (vram.Count > 0)
            sb.Append(ci, $"Video memory: peak {vram.Max(f => f.VramMb):0} MB, at the end {vram[^1].VramMb:0} MB.\n");
        return sb.ToString();
    }

    /// <summary>
    /// The value at quantile <paramref name="q"/> of <paramref name="v"/> (0 when empty).
    /// </summary>
    static double P(List<double> v, double q)
    {
        if (v.Count == 0) return 0;
        var s = v.Order().ToList();
        return s[Math.Min(s.Count - 1, (int)(q * s.Count))];
    }

    /// <summary>
    /// The summary's stutter table: the frames over twice their segment's median and 20 ms, the worst 40, with what was largest in them.
    /// </summary>
    void BenchStutters(StringBuilder sb, string[] stages)
    {
        var ci = CultureInfo.InvariantCulture;
        // Stutters: frames over twice their segment's median and 20 ms, the worst first, with what was largest in them.
        sb.Append("\n## Stutters\n\nFrames over twice their segment's median frame time and over 20 ms, worst first: "
            + "what the frame held most (render-thread stage, world tick, collections).\n\n");
        var medians = Enumerable.Range(0, _benchScript!.Length)
            .Select(s => P(_benchFrames.Where(f => f.Segment == s).Select(f => f.FrameMs).ToList(), 0.5))
            .ToArray();
        var hitches = _benchFrames.Select((f, i) => (f, i)).Where(x => x.f.FrameMs > Math.Max(20, 2 * medians[x.f.Segment]))
            .OrderByDescending(x => x.f.FrameMs)
            .ToList();
        sb.Append(ci, $"{hitches.Count} frames; "
            + $"by segment: {string.Join(", ", hitches.GroupBy(h => _benchScript[h.f.Segment].Name).Select(g2 => $"{g2.Key} {g2.Count()}"))}.\n\n");
        sb.Append("| frame | segment | t s | frame ms | largest loop sections | ticks | largest render stages | largest tick steps | gc |\n"
            + "|---|---|---|---|---|---|---|---|---|\n");
        foreach (var (f, i) in hitches.Take(40))
        {
            string cpu = string.Join(", ", f.Cpu.Select((v, k) => (v, k)).OrderByDescending(x => x.v).Take(3)
                .Select(x => string.Create(ci, $"{stages[x.k]} {x.v:0.0}")));
            string sim = string.Join(", ", f.Steps.Select((v, k) => (v, k)).OrderByDescending(x => x.v).Take(2).Where(x => x.v > 0.1)
                .Select(x => string.Create(ci, $"{TickProfile.PhaseNames[x.k]} {x.v:0.0}")));
            if (f.TopSystemMs > 0.1) sim += string.Create(ci, $" ({f.TopSystem} {f.TopSystemMs:0.0})");
            string gc = f.Gc0 + f.Gc1 + f.Gc2 == 0 ? "" : string.Create(ci, $"gen0 {f.Gc0}, gen1 {f.Gc1}, gen2 {f.Gc2}, pause {f.PauseMs:0.0} ms");
            // The frame's interval holds the previous frame's tail and this one's sections.
            var tail = i > 0 ? _benchFrames[i - 1].Sections[BenchTail] : 0;
            string loop = string.Join(", ", f.Sections.Take(BenchTail).Select((v, k) => (v, k)).Append((tail, BenchTail))
                .OrderByDescending(x => x.Item1)
                .Take(3)
                .Select(x => string.Create(ci, $"{BenchSections[x.Item2]} {x.Item1:0.0}")));
            sb.Append(ci, $"| {i} | {_benchScript[f.Segment].Name} | {f.T:0.0} | {f.FrameMs:0.0} | {loop} | {f.Ticks} | {cpu} | {sim} | {gc} |\n");
        }
    }
}