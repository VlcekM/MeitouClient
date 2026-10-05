using System.Diagnostics;
using System.Numerics;

using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan;
using static Meitou.Rendering.WorldFrame;

namespace Meitou.ModelViewer;

static partial class WorldApp
{
    /// <summary>
    /// <c>--fly-benchmark &lt;frames&gt;</c>: flies the camera round a circle (radius <c>--fly-radius</c>, <c>--fly-speed</c> units per frame) around the
    /// loaded point at 60 frames per second of wall time, as the interactive viewer would, and prints frame-time percentiles, the worst frames with the
    /// time each streaming stage took on the render thread, and the resident memory.
    /// </summary>
    static int FlyBenchmark(IGl gl, Gpu gpu, WorldScene scene, WorldCamera camera, WorldRenderOptions render, WorldOptions o, int w, int h)
    {
        var centre = camera.Target;
        float radius = Math.Max(o.FlyRadius, 1);
        // Start at angle 0 on the circle's east point so the first frame is where the settled view was not: the flight starts by moving.
        float angleStep = o.FlySpeed / radius;
        var times = new List<double>(o.FlyBenchmark);
        var cpu = new List<double>(o.FlyBenchmark);   // up to the end of the commands, before waiting for the GPU: the render thread's own work
        var worst = new List<(double Ms, int Frame, string Stages)>();
        var stageSums = new double[StageClock.Names.Length];
        var shadowSums = new double[3];
        var frameWatch = new Stopwatch();
        long gc2 = GC.CollectionCount(2), gcPrev = gc2, gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), allocated = GC.GetTotalAllocatedBytes(), renderAllocated = GC.GetAllocatedBytesForCurrentThread();
        TimeSpan pause0 = GC.GetTotalPauseDuration(), pausePrev = pause0;
        BackgroundWork.Measure = BackgroundWork.ReportJobs;
        var resident = new List<string>();
        // Pop-in: per frame, how near the nearest foliage zone without its whole layout (within the near reach) and without any layout (within
        // the far reach) are, and the nearest group in range whose mesh is not resident.
        var gaps = new List<(float Zone, float Unlaid, float Mesh, float Grass)>(o.FlyBenchmark);
        // Pipelined: GL gets the same two frames in flight as the Vulkan frame ring, through a timestamp query per frame waited on two frames later.
        bool pipelined = o.FlyPipelined;
        uint[] fences = pipelined && gl is not VkGl ? [gl.GenQuery(), gl.GenQuery(), gl.GenQuery()] : [];
        var interval = Stopwatch.StartNew();
        Console.WriteLine($"fly       {(pipelined ? "pipelined, " : "")}{o.FlyBenchmark} frames, circle radius {radius:0} units round {centre.X:0}, {centre.Z:0}, {o.FlySpeed:0} units per frame ({o.FlySpeed * 60:0} per second)");
        for (int i = 1; i <= o.FlyBenchmark; i++)
        {
            float a = i * angleStep;
            float x = centre.X + radius * (MathF.Cos(a) - 1), z = centre.Z + radius * MathF.Sin(a);
            camera.Target = new Vector3(x, gpu.Terrain.HeightAt(x, z), z);
            StageClock.Start();
            frameWatch.Restart();
            { Draw(gl, gpu, scene, camera, render, w, h, o.Hour, 0, o.FogDistance); EndFrame(gl); }
            double cpuMs = frameWatch.Elapsed.TotalMilliseconds;
            if (gl is Meitou.Rendering.Vulkan.VkGl vkStats && (i % 500 == 0 || i <= 4)) Console.WriteLine($"vkgl      frame {i}: {vkStats.Stats}");
            cpu.Add(cpuMs);
            if (gpu.Foliage is { } fol) gaps.Add((fol.NearestIncompleteZone, fol.NearestUnlaidZone, fol.NearestMissingMesh, fol.NearestMissingGrass));
            if (o.Screenshot is not null && FlyShots.Contains(i) && gpu.Post is { Target: var shotFbo and not 0 })
            {
                // MEITOU_FLY_SHOT=<frame>[,<frame>...]: the picture as drawn at that frame of the flight, nothing waited for (what a flying user sees).
                gl.Finish();
                gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, shotFbo);
                string shot = Path.ChangeExtension(o.Screenshot, null) + $"-fly{i}.png";
                FramebufferCapture.SavePng(gl, shot, w, h);
                Console.WriteLine($"fly shot  frame {i}: eye {camera.Eye.X:0}, {camera.Eye.Y:0}, {camera.Eye.Z:0}, {shot}");
            }
            if (!pipelined) gl.Finish();
            else if (fences.Length > 0)
            {
                gl.QueryCounter(fences[i % 3], QueryCounterTarget.Timestamp);
                if (i >= 3) gl.GetQueryObject(fences[(i - 2) % 3], QueryObjectParameterName.Result, out ulong _);
            }
            StageClock.Lap(11);
            double ms = pipelined ? interval.Elapsed.TotalMilliseconds : frameWatch.Elapsed.TotalMilliseconds;
            interval.Restart();
            times.Add(ms);
            if (i % 150 == 0) resident.Add($"{(((gpu.Objects?.ResidentBytes ?? 0) + (gpu.Foliage?.ResidentBytes ?? 0)) / 1048576)}");
            for (int k = 0; k < stageSums.Length; k++) stageSums[k] += StageClock.Ms[k];
            if (gpu.Shadow is { } shadowStats) for (int k = 0; k < 3; k++) shadowSums[k] += shadowStats.PhaseMs[k];
            long gcNow = GC.CollectionCount(2);
            worst.Add((ms, i, ((gpu.Reflection is { Valid: true } rr && rr.CpuMs >= 5 ? $"reflection[{rr.DescribeLast()}], " : "") + (gcNow != gcPrev ? "gen2 GC, " : "") + (GC.GetTotalPauseDuration() - pausePrev is { TotalMilliseconds: >= 0.5 } pause ? $"GC pause {pause.TotalMilliseconds:0.0}, " : "")) + string.Join(", ", StageClock.Names.Select((n, k) => (n, v: StageClock.Ms[k])).Where(s => s.v >= 1).Select(s => $"{s.n} {s.v:0.0}"))));
            gcPrev = gcNow;
            pausePrev = GC.GetTotalPauseDuration();
            int sleep = 16 - (int)ms;
            if (sleep > 0 && !pipelined) Thread.Sleep(sleep);
        }
        var sorted = times.Skip(1).OrderBy(t => t).ToList();   // the first frame is shader compilation
        var cpuSorted = cpu.Skip(1).OrderBy(t => t).ToList();
        double P(double q) => sorted[Math.Min((int)(sorted.Count * q), sorted.Count - 1)];
        double C(double q) => cpuSorted[Math.Min((int)(cpuSorted.Count * q), cpuSorted.Count - 1)];
        Console.WriteLine($"flight    p50 {P(0.5):0.0} ms, p95 {P(0.95):0.0} ms, p99 {P(0.99):0.0} ms, max {sorted[^1]:0.0} ms; {sorted.Count(t => t > 20)} frames over 20 ms, {sorted.Count(t => t > 33)} over 33 ms; gen2 GCs {GC.CollectionCount(2) - gc2}");
        Console.WriteLine($"gc        gen0 {GC.CollectionCount(0) - gc0}, gen1 {GC.CollectionCount(1) - gc1}, pauses {(GC.GetTotalPauseDuration() - pause0).TotalMilliseconds:0} ms; allocated {(GC.GetTotalAllocatedBytes() - allocated) / 1048576} MB (render thread {(GC.GetAllocatedBytesForCurrentThread() - renderAllocated) / 1048576} MB)");
        Console.WriteLine($"stages    mean ms: {string.Join(", ", StageClock.Names.Select((n, k) => $"{n} {stageSums[k] / o.FlyBenchmark:0.00}"))}");
        Console.WriteLine($"stages    shadow casters mean ms: terrain {shadowSums[0] / o.FlyBenchmark:0.00}, objects {shadowSums[1] / o.FlyBenchmark:0.00}, foliage {shadowSums[2] / o.FlyBenchmark:0.00}");
        if (BackgroundWork.ReportJobs) { BackgroundWork.Measure = false; BackgroundWork.Report(); }
        Console.WriteLine($"cpu only  p50 {C(0.5):0.0} ms, p95 {C(0.95):0.0} ms, p99 {C(0.99):0.0} ms, max {cpuSorted[^1]:0.0} ms (commands recorded, GPU not waited for)");
        if (gpu.Reflection is { } reflectionStats && render.Reflections) Console.WriteLine($"reflect   {reflectionStats.DescribeStats()}");
        foreach (var f in worst.Skip(1).OrderByDescending(f => f.Ms).Take(8))
            Console.WriteLine($"  worst   frame {f.Frame}: {f.Ms:0.0} ms ({f.Stages})");
        if (gpu.Foliage is { } foliageGaps && gaps.Count > 0)
        {
            float near = foliageGaps.NearReach, far = foliageGaps.FarReach;
            Console.WriteLine($"pop-in    foliage zones without their whole layout within the near reach ({near:0}) in {gaps.Count(g => g.Zone < near)} of {gaps.Count} frames " +
                $"(within 1500: {gaps.Count(g => g.Zone < 1500)}), nearest {gaps.Min(g => g.Zone):0}; not laid out within the far reach ({far:0}) in {gaps.Count(g => g.Unlaid < far)} frames, " +
                $"nearest {gaps.Min(g => g.Unlaid):0}; meshes in range not resident in {gaps.Count(g => g.Mesh < near)} frames, nearest {gaps.Min(g => g.Mesh):0}; " +
                $"grass pages missing in {gaps.Count(g => g.Grass < float.PositiveInfinity)} frames (within 1500: {gaps.Count(g => g.Grass < 1500)}), nearest {gaps.Min(g => g.Grass):0}");
            Console.WriteLine($"pop-in    frames without the whole layout within the near reach, per 100 frames: {string.Join(" ", gaps.Chunk(100).Select(c => c.Count(g => g.Zone < near)))}");
        }
        Console.WriteLine($"resident  every 150 frames (objects + foliage, MB): {string.Join(" ", resident)}");
        Console.WriteLine($"resident  {Resident(gpu)}; working set {Environment.WorkingSet / 1048576} MB, managed heap {GC.GetTotalMemory(false) / 1048576} MB");
        if (o.Screenshot is not null)
        {
            // Back at the start: everything wanted there is loaded again (what was unloaded meanwhile comes back) before the picture.
            camera.Target = centre;
            gpu.Streamer?.Settle(gpu.Anchor ?? camera.Eye);
            gpu.Objects?.Settle(camera.Eye);
            gpu.Foliage?.Settle(camera.Eye);
        }
        return 0;
    }

    /// <summary>MEITOU_FLY_SHOT: frames of the fly benchmark saved as pictures (with <c>--screenshot</c>, next to it).</summary>
    static readonly HashSet<int> FlyShots = [.. (Environment.GetEnvironmentVariable("MEITOU_FLY_SHOT") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out int f) ? f : -1)];

    /// <summary>One line on the GPU memory the streamed meshes and textures hold.</summary>
    static string Resident(Gpu gpu) =>
        (gpu.Objects is { } objects ? $"objects {objects.ResidentDescription}" : "objects off") +
        (gpu.Foliage is { } foliage ? $"; foliage {foliage.ResidentDescription}" : "");
}
