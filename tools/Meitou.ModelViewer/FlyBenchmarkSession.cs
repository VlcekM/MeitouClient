using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;

using Meitou.Game.Rendering.Display;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Terrain;
using static Meitou.Game.Rendering.Frame.WorldFrame;

namespace Meitou.ModelViewer;

/// <summary>
/// <c>--fly-benchmark &lt;frames&gt;</c>: flies the camera round a circle (radius <c>--fly-radius</c>, <c>--fly-speed</c> units per frame) around
/// the loaded point at 60 frames per second of wall time, as the interactive viewer would, and prints frame-time percentiles, the worst frames
/// with the time each streaming stage took on the render thread, and the resident memory.
/// </summary>
sealed class FlyBenchmarkSession
{
    #region Flight

    /// <summary>
    /// <c>MEITOU_FLY_TURN=&lt;radians&gt;</c>: the fly benchmark turns the camera by that much each frame about its eye instead of flying (the
    /// occlusion cull's turn test).
    /// </summary>
    static readonly float FlyTurn = float.TryParse(Environment.GetEnvironmentVariable("MEITOU_FLY_TURN"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out float turn) ? turn : 0;

    /// <summary>
    /// The display the frames are drawn on, offscreen.
    /// </summary>
    readonly VulkanDisplay _display;

    /// <summary>
    /// The renderers.
    /// </summary>
    readonly WorldGpu _gpu;

    /// <summary>
    /// The loaded world.
    /// </summary>
    readonly WorldScene _scene;

    /// <summary>
    /// The camera the flight moves.
    /// </summary>
    readonly WorldCamera _camera;

    /// <summary>
    /// The render switches.
    /// </summary>
    readonly WorldRenderOptions _render;

    /// <summary>
    /// The viewer's options.
    /// </summary>
    readonly WorldOptions _o;

    /// <summary>
    /// The picture's size in pixels.
    /// </summary>
    readonly int _w, _h;

    /// <summary>
    /// The display's GPU context.
    /// </summary>
    readonly GpuContext _context;

    /// <summary>
    /// The circle's centre: the loaded point, where the camera goes back to for a picture.
    /// </summary>
    readonly Vector3 _centre;

    /// <summary>
    /// The GPU device (its video memory and allocator).
    /// </summary>
    readonly VulkanDevice _device;

    /// <summary>
    /// The process's peak video memory use.
    /// </summary>
    ulong _vramPeak;

    /// <summary>
    /// The circle's radius.
    /// </summary>
    readonly float _radius;

    /// <summary>
    /// Every frame's time.
    /// </summary>
    readonly List<double> _times;

    /// <summary>
    /// Up to the end of the commands, before waiting for the GPU: the render thread's own work.
    /// </summary>
    readonly List<double> _cpu;

    /// <summary>
    /// Every frame's time with its number and the stages that took a millisecond or more.
    /// </summary>
    readonly List<(double Ms, int Frame, string Stages)> _worst;

    /// <summary>
    /// The render thread's time per stage, summed over the flight.
    /// </summary>
    readonly double[] _stageSums;

    /// <summary>
    /// The shadow casters' time per phase (terrain, objects, foliage), summed.
    /// </summary>
    readonly double[] _shadowSums;

    /// <summary>
    /// The recording jobs' own CPU time per stage, summed.
    /// </summary>
    readonly double[] _jobSums;

    /// <summary>
    /// Times a frame.
    /// </summary>
    readonly Stopwatch _frameWatch;

    /// <summary>
    /// The garbage collections at the start; <see cref="_gcPrev"/> the gen2 count at the last frame.
    /// </summary>
    readonly long _gc2, _gc0, _gc1;

    /// <summary>
    /// The gen2 collection count at the last frame.
    /// </summary>
    long _gcPrev;

    /// <summary>
    /// The bytes allocated at the start, by the process and by the render thread.
    /// </summary>
    readonly long _allocated, _renderAllocated;

    /// <summary>
    /// The total GC pause at the start.
    /// </summary>
    readonly TimeSpan _pause0;

    /// <summary>
    /// The total GC pause at the last frame.
    /// </summary>
    TimeSpan _pausePrev;

    /// <summary>
    /// The objects' and foliage's resident megabytes, every 150 frames.
    /// </summary>
    readonly List<string> _resident;

    /// <summary>
    /// Pop-in: per frame, how near the nearest foliage zone without its whole layout (within the near reach) and without any layout (within
    /// the far reach) are, and the nearest group in range whose mesh is not resident.
    /// </summary>
    readonly List<(float Zone, float Unlaid, float Mesh, float Grass)> _gaps;

    /// <summary>
    /// Pop-in: per frame, the objects' parts drawn before their detail arrived.
    /// </summary>
    readonly List<(int Untextured, int Refining, int Held, int Coarse, int Upgrading)> _objectPop;

    /// <summary>
    /// Pop-in: per frame, the impostors not yet as the view wants them.
    /// </summary>
    readonly List<(int NotAdmitted, int Missing, int Coarse, int Refining)> _impostorPop;

    /// <summary>
    /// Pipelined: no wait for the GPU after each frame (the context keeps its frames in flight), as the interactive viewer runs.
    /// </summary>
    readonly bool _pipelined;

    /// <summary>
    /// <c>--fly-csv</c>: the per-frame video memory lines, and the allocator owners' every 30 frames.
    /// </summary>
    readonly StringBuilder? _csv, _owners;

    /// <summary>
    /// The interval from one frame to the next.
    /// </summary>
    readonly Stopwatch _interval;

    /// <summary>
    /// MEITOU_PASS_STATS=1: the frame cost breakdown (docs/engine.md).
    /// </summary>
    readonly PassMeter? _meter;

    /// <summary>
    /// The camera's path round the circle.
    /// </summary>
    readonly CameraMotion _motion;

    /// <summary>
    /// --log-spikes: the stage times the spike lines print.
    /// </summary>
    readonly FrameProfiler? _spikeProfiler;

    /// <summary>
    /// Draw counts over the flight (docs/render-distance-benchmark.md): the objects' totals per kind at the start.
    /// </summary>
    readonly long[,]? _objectTotals0;

    /// <summary>
    /// The main view's grass blades, foliage instances and terrain triangles, and the GPU's pre-frame and frame milliseconds, summed per frame.
    /// </summary>
    double _bladeSum, _foliageSum, _terrainTriSum, _preFrameSum, _gpuFrameSum;

    /// <summary>
    /// The frames the GPU sums hold (after the first 30).
    /// </summary>
    int _gpuFrames;

    /// <summary>
    /// A flight round the point <paramref name="camera"/> looks at, at <paramref name="w"/> x <paramref name="h"/>.
    /// </summary>
    public FlyBenchmarkSession(VulkanDisplay display, WorldGpu gpu, WorldScene scene, WorldCamera camera, WorldRenderOptions render, WorldOptions o,
            int w, int h)
    {
        _display = display;
        _gpu = gpu;
        _scene = scene;
        _camera = camera;
        _render = render;
        _o = o;
        _w = w;
        _h = h;
        _context = display.Context;
        _centre = camera.Target;
        _device = _context.Device;
        _vramPeak = 0;
        _radius = Math.Max(o.FlyRadius, 1);
        _times = new List<double>(o.FlyBenchmark);
        _cpu = new List<double>(o.FlyBenchmark);   // up to the end of the commands, before waiting for the GPU: the render thread's own work
        _worst = new List<(double Ms, int Frame, string Stages)>();
        _stageSums = new double[StageClock.Names.Length];
        _shadowSums = new double[3];
        _jobSums = new double[StageClock.Names.Length];
        _frameWatch = new Stopwatch();
        _gc2 = GC.CollectionCount(2);
        _gcPrev = _gc2;
        _gc0 = GC.CollectionCount(0);
        _gc1 = GC.CollectionCount(1);
        _allocated = GC.GetTotalAllocatedBytes();
        _renderAllocated = GC.GetAllocatedBytesForCurrentThread();
        _pause0 = GC.GetTotalPauseDuration();
        _pausePrev = _pause0;
        _resident = new List<string>();
        _gaps = new List<(float Zone, float Unlaid, float Mesh, float Grass)>(o.FlyBenchmark);
        _objectPop = new List<(int Untextured, int Refining, int Held, int Coarse, int Upgrading)>(o.FlyBenchmark);
        _impostorPop = new List<(int NotAdmitted, int Missing, int Coarse, int Refining)>(o.FlyBenchmark);
        _pipelined = o.FlyPipelined;
        _csv = o.FlyCsv is null ? null : new StringBuilder("frame,ms,cpu_ms,vram_used_mb,vram_budget_mb,blocks_mb,blocks_used_mb\n");
        _owners = o.FlyCsv is null ? null : new StringBuilder("frame,owner,mb,device_local_mb,count\n");
        _interval = Stopwatch.StartNew();
        _meter = PassMeter.TryCreate(display);   // MEITOU_PASS_STATS=1: the frame cost breakdown (docs/engine.md)
        // MEITOU_BENCH_ORBIT=<pixels per frame>: the camera also turns round its target by this much a frame (0.005 rad per pixel), as a user
        // dragging the view.
        float benchOrbit = float.TryParse(Environment.GetEnvironmentVariable("MEITOU_BENCH_ORBIT"), NumberStyles.Float, CultureInfo.InvariantCulture,
                out float orbit) ? orbit : 0;
        _motion = CameraMotion.Fly(o, camera, gpu, FlyTurn, benchOrbit);
        _spikeProfiler = SpikeLog.Enabled ? new FrameProfiler(_context, () => _context.GpuFrameMs)
                : null;   // --log-spikes: the stage times the spike lines print
        _objectTotals0 = gpu.Objects is { } ot0 ? (long[,])ot0.Totals.Clone() : null;
        gpu.Foliage?.ResetDrawTally();
    }

    /// <summary>
    /// Flies the frames, prints the report, and with <c>--screenshot</c> settles back at the start for the picture.
    /// </summary>
    public int Run()
    {
        Console.WriteLine($"fly       {(_pipelined ? "pipelined, " : "")}{_o.FlyBenchmark} frames, "
            + $"circle radius {_radius:0} units round {_centre.X:0}, {_centre.Z:0}, {_o.FlySpeed:0} units per frame ({_o.FlySpeed * 60:0} "
            + "per second)");
        for (int i = 1; i <= _o.FlyBenchmark; i++) FlyFrame(i);
        Report();
        if (_o.Screenshot is not null)
        {
            // Back at the start: everything wanted there is loaded again (what was unloaded meanwhile comes back) before the picture.
            _camera.Target = _centre;
            _gpu.Streamer?.Settle(_camera.Eye);
            _gpu.Objects?.Settle(_camera.Eye);
            _gpu.Foliage?.Settle(_camera.Eye);
        }
        return 0;
    }

    /// <summary>
    /// Frame <paramref name="i"/> of the flight: drawn, its counts and times taken, then the rest of its 1/60 s slept when not pipelined.
    /// </summary>
    void FlyFrame(int i)
    {
        double cpuMs = DrawFrame(i);
        if (i % 500 == 0 || i <= 4) Console.WriteLine($"gpu       frame {i}: {_context.Frame.Stats}");
        _cpu.Add(cpuMs);
        CountFrame(i);
        if (!_pipelined) _context.Finish();
        if (_spikeProfiler is null) StageClock.EndFrame();
        _spikeProfiler?.EndFrame();
        double ms = _pipelined ? _interval.Elapsed.TotalMilliseconds : _frameWatch.Elapsed.TotalMilliseconds;
        _interval.Restart();
        _times.Add(ms);
        if (i % 150 == 0)
            _resident.Add($"{(((_gpu.Objects?.ResidentBytes ?? 0) + (_gpu.Foliage?.ResidentBytes ?? 0)) / 1048576)}");
        SampleMemory(i, ms, cpuMs);
        for (int k = 0; k < _stageSums.Length; k++)
        {
            _stageSums[k] += StageClock.Ms[k];
            _jobSums[k] += StageClock.JobMs[k];
        }
        if (_gpu.Shadow is { } shadowStats) for (int k = 0; k < 3; k++) _shadowSums[k] += shadowStats.PhaseMs[k];
        AddWorst(i, ms);
        int sleep = 16 - (int)ms;
        if (sleep > 0 && !_pipelined) Thread.Sleep(sleep);
    }

    /// <summary>
    /// Moves the camera and draws frame <paramref name="i"/>; the render thread's milliseconds up to the end of its commands.
    /// </summary>
    double DrawFrame(int i)
    {
        _motion.Step(i);
        if (_spikeProfiler is not null)
        {
            _context.EnsureFrame();
            _spikeProfiler.BeginFrame();
        }
        else StageClock.Start();
        _frameWatch.Restart();
        // With weather particles the frame clock runs (1/60 s a frame, in the draw's time units of 600 s) so they simulate; without, it stays 0
        // as before.
        float clock = _gpu.Particles is { Groups.Count: > 0 } ? (float)(i / 60.0 / 600) : 0;
        // --time-speed: the hour runs on as in the viewer (game hours per real minute), at 1/60 s a frame.
        float hour = _o.TimeSpeed > 0 ? (_o.Hour + _o.TimeSpeed * i / 3600f) % 24 : _o.Hour;
        // The water's, the stars' and the heat haze's clock follows it too (the frame loop holds that clock still for pictures), so the
        // ocean's FFT records every frame as it does in the viewer: the exit line's `water ocean: N passes` then counts the frames it ran.
        if (_o.TimeSpeed > 0) _gpu.HeatHazeHours = hour;
        Draw(_gpu, _scene, _camera, _render, _w, _h, hour, clock, _o.FogDistance);
        WorldApp.EndFrame(_context);
        double cpuMs = _frameWatch.Elapsed.TotalMilliseconds;
        return cpuMs;
    }

    /// <summary>
    /// The frame's draw counts, the GPU's times (after the first 30 frames) and the pop-in numbers.
    /// </summary>
    void CountFrame(int i)
    {
        if (_gpu.Foliage is { } counted)
        {
            _bladeSum += counted.DrawnBlades;
            _foliageSum += counted.DrawnInstances;
        }
        _terrainTriSum += _gpu.Terrain.DrawnTriangles;
        if (i > 30)
        {
            _preFrameSum += _context.PreFrameGpuMs;
            _gpuFrameSum += _context.GpuFrameMs;
            _gpuFrames++;
        }
        if (_gpu.Foliage is { } fol)
        {
            _gaps.Add((fol.NearestIncompleteZone, fol.NearestUnlaidZone, fol.NearestMissingMesh, fol.NearestMissingGrass));
            _impostorPop.Add(fol.ImpostorPopIn);
        }
        if (_gpu.Objects is { } objs)
        {
            var (untextured, refining, held, coarse) = objs.TakePopStats();
            _objectPop.Add((untextured, refining, held, coarse, objs.MeshesAwaitingDetail));
        }
    }

    /// <summary>
    /// The video memory: the peak every 30 frames, or with <c>--fly-csv</c> every frame's line and every 30th frame's owners.
    /// </summary>
    void SampleMemory(int i, double ms, double cpuMs)
    {
        if (_csv is not null)
        {
            // --fly-csv: this process's video memory (the driver's figure) and our allocator's blocks every frame; the owners (a walk over
            // every live resource under the allocator's lock) only every 30 frames, after the frame's time is taken.
            var (used, budget) = _device.VideoMemory();
            _vramPeak = Math.Max(_vramPeak, used);
            double blocks = _device.Allocator.TotalAllocatedBytes / 1048576.0, blocksUsed = _device.Allocator.TotalUsedBytes / 1048576.0;
            _csv.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{i},{ms:0.000},{cpuMs:0.000},{used / 1048576.0:0.0},{budget / 1048576.0:0.0},{blocks:0.0},{blocksUsed:0.0}"));
            if (i % 30 == 0)
            {
                foreach (var b in _device.Allocator.Breakdown())
                {
                    _owners!.AppendLine(string.Create(CultureInfo.InvariantCulture,
                            $"{i},\"{b.Name}\",{b.Bytes / 1048576.0:0.00},{b.DeviceLocal / 1048576.0:0.00},{b.Count}"));
                }
            }
        }
        else if (i % 30 == 0) _vramPeak = Math.Max(_vramPeak, _device.VideoMemory().Used);
    }

    /// <summary>
    /// The frame for the worst list: its time and what stood out (a slow reflection, a gen2 collection, a GC pause, the stages of 1 ms or more).
    /// </summary>
    void AddWorst(int i, double ms)
    {
        long gcNow = GC.CollectionCount(2);
        _worst.Add((ms, i, WorstNotes(gcNow)));
        _gcPrev = gcNow;
        _pausePrev = GC.GetTotalPauseDuration();
    }

    /// <summary>
    /// What stood out in a frame for <see cref="AddWorst"/>: a slow reflection, a gen2 collection, a GC pause, the stages of 1 ms or more.
    /// </summary>
    string WorstNotes(long gcNow)
    {
        string reflection = _gpu.Reflection is { Valid: true } rr && rr.CpuMs >= 5 ? $"reflection[{rr.DescribeLast()}], " : "";
        string gen2 = gcNow != _gcPrev ? "gen2 GC, " : "";
        string gcPause = GC.GetTotalPauseDuration() - _pausePrev is { TotalMilliseconds: >= 0.5 } pause
            ? $"GC pause {pause.TotalMilliseconds:0.0}, "
            : "";
        var stages = StageClock.Names
            .Select((n, k) => (n, v: StageClock.Ms[k]))
            .Where(s => s.v >= 1)
            .Select(s => $"{s.n} {s.v:0.0}");
        return reflection + gen2 + gcPause + string.Join(", ", stages);
    }

    #endregion

    #region Report

    /// <summary>
    /// The flight's report: the percentiles, the collections, the stages, the worst frames, the pop-in, the memory and the draw counts.
    /// </summary>
    void Report()
    {
        // MEITOU_BENCH_SKIP=<n>: leave the first n frames out of the percentiles and the worst list (default 1: shader compilation; the start of a
        // cold run also loads the first zones).
        int skipFrames = int.TryParse(Environment.GetEnvironmentVariable("MEITOU_BENCH_SKIP"), out int skipValue) && skipValue >= 1 ? skipValue : 1;
        var sorted = _times.Skip(skipFrames).OrderBy(t => t).ToList();
        var cpuSorted = _cpu.Skip(skipFrames).OrderBy(t => t).ToList();
        double P(double q) => sorted[Math.Min((int)(sorted.Count * q), sorted.Count - 1)];
        double C(double q) => cpuSorted[Math.Min((int)(cpuSorted.Count * q), cpuSorted.Count - 1)];
        Console.WriteLine($"flight    p50 {P(0.5):0.0} ms, p95 {P(0.95):0.0} ms, p99 {P(0.99):0.0} ms, max {sorted[^1]:0.0} ms; "
            + $"{sorted.Count(t => t > 20)} frames over 20 ms, {sorted.Count(t => t > 33)} over 33 ms; "
            + $"gen2 GCs {GC.CollectionCount(2) - _gc2}");
        PrintStages();
        Console.WriteLine($"cpu only  p50 {C(0.5):0.0} ms, p95 {C(0.95):0.0} ms, p99 {C(0.99):0.0} ms, "
            + $"max {cpuSorted[^1]:0.0} ms (commands recorded, GPU not waited for)");
        if (_gpu.Reflection is { } reflectionStats && _render.Reflections)
            Console.WriteLine($"reflect   {reflectionStats.DescribeStats()}");
        foreach (var f in _worst.Skip(skipFrames).OrderByDescending(f => f.Ms).Take(8))
            Console.WriteLine($"  worst   frame {f.Frame}: {f.Ms:0.0} ms ({f.Stages})");
        PrintPopIn();
        PrintMemory();
        PrintCounts();
        if (_meter is not null) ReportPasses(_meter, _display, _gpu, _scene, _camera, _render, _o, _w, _h);
    }

    /// <summary>
    /// The collections and allocations, the stages' and the shadow casters' mean times, the recording jobs' and the particles'.
    /// </summary>
    void PrintStages()
    {
        Console.WriteLine($"gc        gen0 {GC.CollectionCount(0) - _gc0}, gen1 {GC.CollectionCount(1) - _gc1}, "
            + $"pauses {(GC.GetTotalPauseDuration() - _pause0).TotalMilliseconds:0} ms; "
            + $"allocated {(GC.GetTotalAllocatedBytes() - _allocated) / 1048576} MB (render thread "
            + $"{(GC.GetAllocatedBytesForCurrentThread() - _renderAllocated) / 1048576} MB); "
            + $"heap {GC.GetGCMemoryInfo().HeapSizeBytes / 1048576} MB, native buffers {Data.NativeBuffer.LiveBytes / 1048576} MB held");
        Console.WriteLine("stages    mean ms: "
            + $"{string.Join(", ", StageClock.Names.Select((n, k) => $"{n} {_stageSums[k] / _o.FlyBenchmark:0.00}"))}");
        Console.WriteLine($"stages    shadow casters mean ms: terrain {_shadowSums[0] / _o.FlyBenchmark:0.00}, "
            + $"objects {_shadowSums[1] / _o.FlyBenchmark:0.00}, foliage {_shadowSums[2] / _o.FlyBenchmark:0.00}");
        if (_gpu.Shadow is { Meitou: true } meitouShadow)
        {
            Console.WriteLine($"shadows   meitou schedule (cascade redraws over the {meitouShadow.MeitouFrames} frames): "
                + $"{meitouShadow.DescribeMeitou()}");
        }
        // Wave 4 (docs/renderer-native.md 9.3): the recording jobs' own CPU time per stage, summed over the threads that ran them; the stage
        // times above are the render thread's wall time (including any wait for the jobs), so the two do not add up.
        var jobs = StageClock.Names
            .Select((n, k) => (n, k))
            .Where(x => _jobSums[x.k] > 0)
            .Select(x => $"{x.n} {_jobSums[x.k] / _o.FlyBenchmark:0.00}");
        Console.WriteLine($"jobs      record mode {Recording.Mode} ({RenderJobs.Threads} threads); summed cpu mean ms: " + string.Join(", ", jobs));
        if (_gpu.Particles is { } fx)
        {
            Console.WriteLine($"particles per frame over the flight: simulation {fx.MeanMilliseconds.Simulation:0.00} ms (background "
                + $"threads), main thread waited {fx.MeanMilliseconds.Wait:0.00} ms; update {fx.MeanMainThreadMilliseconds.Update:0.00} "
                + $"ms, draw {fx.MeanMainThreadMilliseconds.Draw:0.00} ms (waiting included, "
                + $"of which filling instance buffers {fx.CollectMilliseconds:0.00} ms); {fx.ActiveUnits} units simulated, "
                + $"{fx.DrawnUnits} drawn");
        }
    }

    /// <summary>
    /// Pop-in over the flight: the objects drawn before their detail, the impostors not as wanted, and the foliage zones and meshes missing.
    /// </summary>
    void PrintPopIn()
    {
        if (_objectPop.Count > 0)
        {
            Console.WriteLine("pop-in    objects: parts drawn untextured (diffuse map not resident) "
                + $"in {_objectPop.Count(p => p.Untextured > 0)} of {_objectPop.Count} frames (most {_objectPop.Max(p => p.Untextured)} "
                + "draws); " +
                $"textures waiting for a finer image in {_objectPop.Count(p => p.Refining > 0)} frames (most "
                    + $"{_objectPop.Max(p => p.Refining)}); instances within 3000 held back while their mesh is remade with finer levels "
                    + $"in {_objectPop.Count(p => p.Held > 0)} frames (most {_objectPop.Max(p => p.Held)}); "
                    + "instances drawn at a coarser level than their distance picks (mesh not yet remade) "
                    + $"in {_objectPop.Count(p => p.Coarse > 0)} frames (most {_objectPop.Max(p => p.Coarse)}); "
                    + $"mesh remakes for finer levels in flight in {_objectPop.Count(p => p.Upgrading > 0)} frames (most "
                    + $"{_objectPop.Max(p => p.Upgrading)})");
        }
        if (_impostorPop.Count > 0)
        {
            Console.WriteLine("pop-in    impostors: wanted atlases left out by the budget plan (meshes drawn) "
                + $"in {_impostorPop.Count(p => p.NotAdmitted > 0)} of {_impostorPop.Count} frames (most "
                + $"{_impostorPop.Max(p => p.NotAdmitted)}); " +
                "admitted but not resident yet (meshes drawn) "
                    + $"in {_impostorPop.Count(p => p.Missing > 0)} frames (most {_impostorPop.Max(p => p.Missing)}); " +
                $"resident but coarser than the nearest instance wants, refine on its way, in {_impostorPop.Count(p => p.Coarse > 0)} "
                    + $"frames (most {_impostorPop.Max(p => p.Coarse)}); refines in flight in {_impostorPop.Count(p => p.Refining > 0)} "
                    + $"frames (most {_impostorPop.Max(p => p.Refining)})");
        }
        if (_gpu.Foliage is { } foliageGaps && _gaps.Count > 0)
        {
            float near = foliageGaps.WholeReach, far = foliageGaps.FarReach;
            Console.WriteLine($"pop-in    foliage zones without their whole layout within the near reach ({near:0}) "
                + $"in {_gaps.Count(g => g.Zone < near)} of {_gaps.Count} frames " +
                $"(within 1500: {_gaps.Count(g => g.Zone < 1500)}), nearest {_gaps.Min(g => g.Zone):0}; "
                    + $"not laid out within the far reach ({far:0}) in {_gaps.Count(g => g.Unlaid < far)} frames, " +
                $"nearest {_gaps.Min(g => g.Unlaid):0}; meshes in range not resident in {_gaps.Count(g => g.Mesh < near)} frames, "
                    + $"nearest {_gaps.Min(g => g.Mesh):0}; " +
                $"grass pages missing in {_gaps.Count(g => g.Grass < float.PositiveInfinity)} frames (within 1500: "
                    + $"{_gaps.Count(g => g.Grass < 1500)}), nearest {_gaps.Min(g => g.Grass):0}");
            Console.WriteLine("pop-in    frames without the whole layout within the near reach, per 100 frames: "
                + $"{string.Join(" ", _gaps.Chunk(100).Select(c => c.Count(g => g.Zone < near)))}");
        }
    }

    /// <summary>
    /// The resident memory, the video memory and its owners, the <c>--fly-csv</c> files, the memory guard and the foliage's scratch buffers.
    /// </summary>
    void PrintMemory()
    {
        Console.WriteLine($"resident  every 150 frames (objects + foliage, MB): {string.Join(" ", _resident)}");
        Console.WriteLine($"resident  {Resident(_gpu)}; working set {Environment.WorkingSet / 1048576} MB, "
            + $"managed heap {GC.GetTotalMemory(false) / 1048576} MB");
        // The F11 VRAM lines: the process's use of the driver's budget (peak sampled every 30 frames), then every allocator owner
        // (GpuAllocator.Breakdown), largest first.
        var (vramUsed, vramBudget) = _device.VideoMemory();
        Console.WriteLine($"vram      {vramUsed / 1048576.0:0} MB of {vramBudget / 1048576.0:0} MB budget at the end, "
            + $"peak {Math.Max(_vramPeak, vramUsed) / 1048576.0:0} MB; our blocks {_device.Allocator.TotalAllocatedBytes / 1048576.0:0} "
            + $"MB, {_device.Allocator.TotalUsedBytes / 1048576.0:0} used");
        var owners = _device.Allocator.Breakdown()
            .OrderByDescending(b => b.Bytes)
            .Select(b => $"{b.Name} {b.Bytes / 1048576.0:0.0} ({b.DeviceLocal / 1048576.0:0.0}) x{b.Count}");
        Console.WriteLine($"vram      owners (MB, device-local MB, count): {string.Join(", ", owners)}");
        Console.WriteLine($"vram      {VramWatch.Describe()}");
        if (_o.FlyCsv is { } csvPath)
        {
            File.WriteAllText(csvPath, _csv!.ToString());
            File.WriteAllText(csvPath + ".owners.csv", _owners!.ToString());
            Console.WriteLine($"vram      per-frame CSV written to {csvPath} (owners every 30 frames in {csvPath}.owners.csv)");
        }
        if (_gpu.Guard is { } vramGuard)
        {
            Console.WriteLine($"guard     {vramGuard.Status}; entered pressure {vramGuard.Activations} times, "
                + $"lowest range scale x{vramGuard.LowestScale:0.00}");
        }
        if (_gpu.Foliage is { } scratchFoliage) Console.WriteLine($"scratch   {scratchFoliage.ScratchDescription}");
    }

    /// <summary>
    /// The spike summary, the GPU's mean times, and the draw counts per frame: grass, foliage, terrain, and the objects per kind.
    /// </summary>
    void PrintCounts()
    {
        int frames = Math.Max(_o.FlyBenchmark, 1);
        SpikeLog.PrintSummary();
        if (_gpuFrames > 0)
        {
            Console.WriteLine($"gpu       mean over frames 31-{_o.FlyBenchmark}: frame {_gpuFrameSum / _gpuFrames:0.00} ms, "
                + $"pre-frame {_preFrameSum / _gpuFrames:0.00} ms (uploads, compute culls, grass kernels, bakes; "
                + "MEITOU_PASS_STATS=1 only)");
        }
        Console.WriteLine($"counts    per frame (main view, last slice for terrain): grass blades {_bladeSum / frames:N0}, "
            + $"foliage instances {_foliageSum / frames:N0}, terrain triangles {_terrainTriSum / frames:N0}");
        if (_gpu.Objects is { } ot && _objectTotals0 is { } objectTotals0)
        {
            string Kind(int k,
                    string name) => $"{name} {(ot.Totals[k, 0] - objectTotals0[k, 0]) / (double)frames:0} instances, "
                        + $"{(ot.Totals[k, 1] - objectTotals0[k, 1]) / (double)frames / 1e3:0.0}k triangles, "
                        + $"{(ot.Totals[k, 2] - objectTotals0[k, 2]) / (double)frames:0} calls, "
                        + $"{(ot.Totals[k, 3] - objectTotals0[k, 3]) / (double)frames:0} part-limited in "
                        + $"{(ot.Totals[k, 4] - objectTotals0[k, 4]) / (double)frames:0.0} passes";
            Console.WriteLine($"counts    objects per frame: {Kind(0, "colour (slices + reflection)")}; {Kind(1, "shadow")}");
            Console.WriteLine($"sizes     objects: {ot.SizeSurvey()}");
        }
    }

    /// <summary>
    /// MEITOU_PASS_STATS=1: the pass meter's table and CSV lines, then one more frame with <see cref="WorldFrame.DetailedStats"/> on for what each
    /// pass drew (instances, calls, per cascade and per foliage step: the same lines the screenshot path prints).
    /// </summary>
    static void ReportPasses(PassMeter meter, Game.Rendering.Display.VulkanDisplay display, WorldGpu gpu, WorldScene scene, WorldCamera camera,
            WorldRenderOptions render, WorldOptions o, int w, int h)
    {
        meter.Report(Console.Out);
        meter.Dispose();
        DetailedStats = true;
        Draw(gpu, scene, camera, render, w, h, o.Hour, 0, o.FogDistance);
        WorldApp.EndFrame(display.Context);
        display.Context.Finish();
        Console.WriteLine($"detail    {gpu.Terrain.DrawnChunks} terrain chunks, {gpu.Terrain.DrawnTriangles:N0} terrain triangles" +
            (gpu.Objects is { } ob ? $"; objects {ob.DrawnInstances} instances, {ob.DrawCalls} draw calls, {ob.DrawnTriangles:N0} triangles" : ""));
        if (gpu.Foliage is { } fol)
        {
            fol.PollTimers(wait: true);
            Console.WriteLine($"detail    foliage draw cpu {fol.LastDrawCpuMs:0.00} ms, gpu {fol.GpuMs:0.00} ms, "
                + $"{fol.DrawCalls} draw calls, {fol.DrawnInstances} meshes, {fol.DrawnBlades:N0} grass blades;{fol.MainDetail}");
        }
        if (gpu.Reflection is { Valid: true } refl)
        {
            refl.Poll(wait: true);
            Console.WriteLine($"detail    reflect cpu {refl.CpuMs:0.00} ms, gpu {refl.GpuMs:0.00} ms; {refl.SceneStats}");
        }
        if (gpu.Shadow is { } shadow)
        {
            shadow.Poll(wait: true);
            Console.WriteLine($"detail    shadows cpu {shadow.CpuMs:0.00} ms, gpu {shadow.GpuMs:0.00} ms; {shadow.CasterStats}");
        }
        DetailedStats = false;
    }

    /// <summary>
    /// One line on the GPU memory the streamed meshes and textures hold.
    /// </summary>
    static string Resident(WorldGpu gpu) =>
        (gpu.Objects is { } objects ? $"objects {objects.ResidentDescription}" : "objects off") +
        (gpu.Foliage is { } foliage ? $"; foliage {foliage.ResidentDescription}" : "");

    #endregion
}
