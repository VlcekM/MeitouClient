using System.Diagnostics;
using System.Numerics;
using Silk.NET.OpenGL;

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
    static int FlyBenchmark(IGl gl, Gpu gpu, WorldScene scene, WorldCamera camera, WorldRenderOptions render, WorldOptions o, int w, int h)
    {
        var centre = camera.Target;
        float radius = Math.Max(o.FlyRadius, 1);
        // Start at angle 0 on the circle's east point so the first frame is where the settled view was not: the flight starts by moving.
        float angleStep = o.FlySpeed / radius;
        var times = new List<double>(o.FlyBenchmark);
        var cpu = new List<double>(o.FlyBenchmark);   // up to the end of the commands, before waiting for the GPU: the render thread's own work
        var worst = new List<(double Ms, int Frame, string Stages)>();
        var frameWatch = new Stopwatch();
        long gc2 = GC.CollectionCount(2), gcPrev = gc2;
        var resident = new List<string>();
        Console.WriteLine($"fly       {o.FlyBenchmark} frames, circle radius {radius:0} units round {centre.X:0}, {centre.Z:0}, {o.FlySpeed:0} units per frame ({o.FlySpeed * 60:0} per second)");
        for (int i = 1; i <= o.FlyBenchmark; i++)
        {
            float a = i * angleStep;
            float x = centre.X + radius * (MathF.Cos(a) - 1), z = centre.Z + radius * MathF.Sin(a);
            camera.Target = new Vector3(x, gpu.Terrain.HeightAt(x, z), z);
            StageClock.Start();
            frameWatch.Restart();
            { Draw(gl, gpu, scene, camera, render, w, h, o.Hour, 0, o.FogDistance); EndFrame(gl); }
            double cpuMs = frameWatch.Elapsed.TotalMilliseconds;
            if (gl is Meitou.Rendering.Vulkan.VkGl vkStats && i % 500 == 0) Console.WriteLine($"vkgl      frame {i}: {vkStats.Stats}");
            cpu.Add(cpuMs);
            gl.Finish();
            StageClock.Lap(11);
            double ms = frameWatch.Elapsed.TotalMilliseconds;
            times.Add(ms);
            if (i % 150 == 0) resident.Add($"{(((gpu.Objects?.ResidentBytes ?? 0) + (gpu.Foliage?.ResidentBytes ?? 0)) / 1048576)}");
            long gcNow = GC.CollectionCount(2);
            worst.Add((ms, i, ((gpu.Reflection is { Valid: true } rr && rr.CpuMs >= 5 ? $"reflection[{rr.DescribeLast()}], " : "") + (gcNow != gcPrev ? "gen2 GC, " : "")) + string.Join(", ", StageClock.Names.Select((n, k) => (n, v: StageClock.Ms[k])).Where(s => s.v >= 1).Select(s => $"{s.n} {s.v:0.0}"))));
            gcPrev = gcNow;
            int sleep = 16 - (int)ms;
            if (sleep > 0) Thread.Sleep(sleep);
        }
        var sorted = times.Skip(1).OrderBy(t => t).ToList();   // the first frame is shader compilation
        var cpuSorted = cpu.Skip(1).OrderBy(t => t).ToList();
        double P(double q) => sorted[Math.Min((int)(sorted.Count * q), sorted.Count - 1)];
        double C(double q) => cpuSorted[Math.Min((int)(cpuSorted.Count * q), cpuSorted.Count - 1)];
        Console.WriteLine($"flight    p50 {P(0.5):0.0} ms, p95 {P(0.95):0.0} ms, p99 {P(0.99):0.0} ms, max {sorted[^1]:0.0} ms; {sorted.Count(t => t > 20)} frames over 20 ms, {sorted.Count(t => t > 33)} over 33 ms; gen2 GCs {GC.CollectionCount(2) - gc2}");
        Console.WriteLine($"cpu only  p50 {C(0.5):0.0} ms, p95 {C(0.95):0.0} ms, p99 {C(0.99):0.0} ms, max {cpuSorted[^1]:0.0} ms (commands recorded, GPU not waited for)");
        if (gpu.Reflection is { } reflectionStats && render.Reflections) Console.WriteLine($"reflect   {reflectionStats.DescribeStats()}");
        foreach (var f in worst.Skip(1).OrderByDescending(f => f.Ms).Take(8))
            Console.WriteLine($"  worst   frame {f.Frame}: {f.Ms:0.0} ms ({f.Stages})");
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

    /// <summary>One line on the GPU memory the streamed meshes and textures hold.</summary>
    static string Resident(Gpu gpu) =>
        (gpu.Objects is { } objects ? $"objects {objects.ResidentDescription}" : "objects off") +
        (gpu.Foliage is { } foliage ? $"; foliage {foliage.ResidentDescription}" : "");
}
