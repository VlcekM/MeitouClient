using System.Diagnostics;
using System.Numerics;

using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using static Meitou.Rendering.WorldFrame;

namespace Meitou.ModelViewer;

static partial class WorldApp
{
    /// <summary>
    /// <c>--fly-benchmark &lt;frames&gt;</c>: flies the camera round a circle (radius <c>--fly-radius</c>, <c>--fly-speed</c> units per frame) around the
    /// loaded point at 60 frames per second of wall time, as the interactive viewer would, and prints frame-time percentiles, the worst frames with the
    /// time each streaming stage took on the render thread, and the resident memory.
    /// </summary>
    static int FlyBenchmark(Meitou.Rendering.Display.VulkanDisplay display, Gpu gpu, WorldScene scene, WorldCamera camera, WorldRenderOptions render, WorldOptions o, int w, int h)
    {
        var context = display.Context;
        var centre = camera.Target;
        float radius = Math.Max(o.FlyRadius, 1);
        // Start at angle 0 on the circle's east point so the first frame is where the settled view was not: the flight starts by moving.
        float angleStep = o.FlySpeed / radius;
        var times = new List<double>(o.FlyBenchmark);
        var cpu = new List<double>(o.FlyBenchmark);   // up to the end of the commands, before waiting for the GPU: the render thread's own work
        var worst = new List<(double Ms, int Frame, string Stages)>();
        var stageSums = new double[StageClock.Names.Length];
        var shadowSums = new double[3];
        var jobSums = new double[StageClock.Names.Length];
        var frameWatch = new Stopwatch();
        long gc2 = GC.CollectionCount(2), gcPrev = gc2, gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), allocated = GC.GetTotalAllocatedBytes(), renderAllocated = GC.GetAllocatedBytesForCurrentThread();
        TimeSpan pause0 = GC.GetTotalPauseDuration(), pausePrev = pause0;
        BackgroundWork.Measure = BackgroundWork.ReportJobs;
        var resident = new List<string>();
        // Pop-in: per frame, how near the nearest foliage zone without its whole layout (within the near reach) and without any layout (within
        // the far reach) are, and the nearest group in range whose mesh is not resident.
        var gaps = new List<(float Zone, float Unlaid, float Mesh, float Grass)>(o.FlyBenchmark);
        // Pipelined: no wait for the GPU after each frame (the context keeps its frames in flight), as the interactive viewer runs.
        bool pipelined = o.FlyPipelined;
        var interval = Stopwatch.StartNew();
        var meter = PassMeter.TryCreate(display);   // MEITOU_PASS_STATS=1: the frame cost breakdown (docs/engine.md)
        Console.WriteLine($"fly       {(pipelined ? "pipelined, " : "")}{o.FlyBenchmark} frames, circle radius {radius:0} units round {centre.X:0}, {centre.Z:0}, {o.FlySpeed:0} units per frame ({o.FlySpeed * 60:0} per second)");
        for (int i = 1; i <= o.FlyBenchmark; i++)
        {
            float a = i * angleStep;
            float x = centre.X + radius * (MathF.Cos(a) - 1), z = centre.Z + radius * MathF.Sin(a);
            camera.Target = new Vector3(x, gpu.Terrain.HeightAt(x, z), z);
            StageClock.Start();
            frameWatch.Restart();
            { Draw(gpu, scene, camera, render, w, h, o.Hour, 0, o.FogDistance); EndFrame(context); }
            double cpuMs = frameWatch.Elapsed.TotalMilliseconds;
            if (i % 500 == 0 || i <= 4) Console.WriteLine($"gpu       frame {i}: {context.Frame.Stats}");
            cpu.Add(cpuMs);
            if (gpu.Foliage is { } fol) gaps.Add((fol.NearestIncompleteZone, fol.NearestUnlaidZone, fol.NearestMissingMesh, fol.NearestMissingGrass));
            if (o.Screenshot is not null && FlyShots.Contains(i) && gpu.Post is { Target: { } shotTarget } shotPost)
            {
                // MEITOU_FLY_SHOT=<frame>[,<frame>...]: the picture as drawn at that frame of the flight, nothing waited for (what a flying user sees).
                string shot = Path.ChangeExtension(o.Screenshot, null) + $"-fly{i}.png";
                FramebufferCapture.SavePng(shotPost.Gpu, shotTarget, shot, w, h);
                Console.WriteLine($"fly shot  frame {i}: eye {camera.Eye.X:0}, {camera.Eye.Y:0}, {camera.Eye.Z:0}, {shot}");
            }
            if (!pipelined) context.Finish();
            StageClock.Lap(11);
            double ms = pipelined ? interval.Elapsed.TotalMilliseconds : frameWatch.Elapsed.TotalMilliseconds;
            interval.Restart();
            times.Add(ms);
            if (i % 150 == 0) resident.Add($"{(((gpu.Objects?.ResidentBytes ?? 0) + (gpu.Foliage?.ResidentBytes ?? 0)) / 1048576)}");
            for (int k = 0; k < stageSums.Length; k++) { stageSums[k] += StageClock.Ms[k]; jobSums[k] += StageClock.JobMs[k]; }
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
        // Wave 4 (docs/renderer-native.md 9.3): the recording jobs' own CPU time per stage, summed over the threads that ran them; the stage
        // times above are the render thread's wall time (including any wait for the jobs), so the two do not add up.
        Console.WriteLine($"jobs      record mode {Meitou.Rendering.Gpu.Recording.Mode} ({RenderJobs.Threads} threads); summed cpu mean ms: " +
            string.Join(", ", StageClock.Names.Select((n, k) => (n, k)).Where(x => jobSums[x.k] > 0).Select(x => $"{x.n} {jobSums[x.k] / o.FlyBenchmark:0.00}")));
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
        // Every allocator owner (names without their numbers, GpuAllocator.Breakdown), largest first: what fills the VRAM.
        Console.WriteLine($"vram      owners (MB, device-local MB, count): {string.Join(", ", context.Device.Allocator.Breakdown().OrderByDescending(b => b.Bytes).Select(b => $"{b.Name} {b.Bytes / 1048576.0:0.0} ({b.DeviceLocal / 1048576.0:0.0}) x{b.Count}"))}");
        Console.WriteLine($"vram      {VramWatch.Describe()}");
        if (gpu.Foliage is { } scratchFoliage) Console.WriteLine($"scratch   {scratchFoliage.ScratchDescription}");
        if (meter is not null) ReportPasses(meter, display, gpu, scene, camera, render, o, w, h);
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

    /// <summary>
    /// MEITOU_PASS_STATS=1: the pass meter's table and CSV lines, then one more frame with <see cref="WorldFrame.DetailedStats"/> on for what each
    /// pass drew (instances, calls, per cascade and per foliage step: the same lines the screenshot path prints).
    /// </summary>
    static void ReportPasses(PassMeter meter, Meitou.Rendering.Display.VulkanDisplay display, Gpu gpu, WorldScene scene, WorldCamera camera, WorldRenderOptions render, WorldOptions o, int w, int h)
    {
        meter.Report(Console.Out);
        meter.Dispose();
        if (Environment.GetEnvironmentVariable("MEITOU_VK_MICRO") == "1") PassMeter.VkCallMicro(display.Context, Console.Out);
        WorldFrame.DetailedStats = true;
        { Draw(gpu, scene, camera, render, w, h, o.Hour, 0, o.FogDistance); EndFrame(display.Context); }
        display.Context.Finish();
        Console.WriteLine($"detail    {gpu.Terrain.DrawnChunks} terrain chunks, {gpu.Terrain.DrawnTriangles:N0} terrain triangles" +
            (gpu.Objects is { } ob ? $"; objects {ob.DrawnInstances} instances, {ob.DrawCalls} draw calls, {ob.DrawnTriangles:N0} triangles" : ""));
        if (gpu.Foliage is { } fol) { fol.PollTimers(wait: true); Console.WriteLine($"detail    foliage draw cpu {fol.LastDrawCpuMs:0.00} ms, gpu {fol.GpuMs:0.00} ms, {fol.DrawCalls} draw calls, {fol.DrawnInstances} meshes, {fol.DrawnBlades:N0} grass blades;{fol.MainDetail}"); }
        if (gpu.Reflection is { Valid: true } refl) { refl.Poll(wait: true); Console.WriteLine($"detail    reflect cpu {refl.CpuMs:0.00} ms, gpu {refl.GpuMs:0.00} ms; {refl.SceneStats}"); }
        if (gpu.Shadow is { } shadow) { shadow.Poll(wait: true); Console.WriteLine($"detail    shadows cpu {shadow.CpuMs:0.00} ms, gpu {shadow.GpuMs:0.00} ms; {shadow.CasterStats}"); }
        WorldFrame.DetailedStats = false;
    }

    /// <summary>MEITOU_FLY_SHOT: frames of the fly benchmark saved as pictures (with <c>--screenshot</c>, next to it).</summary>
    static readonly HashSet<int> FlyShots = [.. (Environment.GetEnvironmentVariable("MEITOU_FLY_SHOT") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out int f) ? f : -1)];

    /// <summary>One line on the GPU memory the streamed meshes and textures hold.</summary>
    static string Resident(Gpu gpu) =>
        (gpu.Objects is { } objects ? $"objects {objects.ResidentDescription}" : "objects off") +
        (gpu.Foliage is { } foliage ? $"; foliage {foliage.ResidentDescription}" : "");
}
