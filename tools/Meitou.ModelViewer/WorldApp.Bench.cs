using System.Diagnostics;
using System.Globalization;
using System.Numerics;

using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using static Meitou.Rendering.WorldFrame;

namespace Meitou.ModelViewer;

static partial class WorldApp
{
    /// <summary>
    /// The benchmark harness (<c>--bench-frames</c>, <c>--ab</c>; docs/viewer.md "Benchmark harness"): measures a fixed view, alternating the two sides of one
    /// switch when asked, and writes per-stage statistics (mean, median, p95, p99, max for each side and the paired difference) as a table and JSON.
    /// </summary>
    static int Bench(Meitou.Rendering.Display.VulkanDisplay display, Gpu gpu, WorldScene scene, WorldCamera camera, WorldRenderOptions render, WorldOptions o, int w, int h)
    {
        var context = display.Context;
        var post = gpu.Post!;
        bool ab = o.Ab is not null;
        int frames = o.BenchFrames > 0 ? o.BenchFrames : 600;
        int period = ab ? o.AbPeriod : 1;
        int drop = ab && period > 1 ? Math.Min(o.AbDrop ?? Math.Min(period / 4, 16), period - 1) : 0;

        // The runtime switches: the Faithful / Meitou ones under their ids (A = Meitou), then what can flip between two frames (A = on).
        AbToggles.Clear();
        AbToggles.RegisterEnhancements(LiveSwitches(o, () => gpu, () => camera, () => render));
        AbToggles.Register("occlusion", () => post.OcclusionCull, v => post.OcclusionCull = v, "foliage occlusion cull against the previous frame's depth pyramid (--no-occlusion-cull)");
        if (gpu.FogVolumes is { } fog)
        {
            AbToggles.Register("fog-cull", () => fog.CullEnabled, v => fog.CullEnabled = v, "culling of what the fog hides (--no-fog-cull)");
            AbToggles.Register("fog-volumes", () => fog.Enabled, v => fog.Enabled = v, "the placed fog volumes (--no-fog-volumes)");
        }
        if (gpu.Shadow is { } shadowPass) AbToggles.Register("shadow-pass", () => shadowPass.Enabled, v => shadowPass.Enabled = v, "the whole shadow pass (--no-shadows)");
        if (gpu.Foliage is { } foliagePass) AbToggles.Register("foliage-draw", () => foliagePass.Enabled, v => foliagePass.Enabled = v, "trees, bushes, rocks and grass (--no-foliage)");
        AbToggles.Register("objects-draw", () => render.Objects, v => render.Objects = v, "buildings and map features (--no-objects)");
        AbToggles.Register("water-draw", () => render.Water, v => render.Water = v, "the water pass (--no-water)");
        AbToggles.Register("reflections", () => render.Reflections, v => render.Reflections = v, "the water reflection pass (--no-reflections)");
        AbToggles.Toggle? toggle = null;
        if (ab && !AbToggles.TryGet(o.Ab!, out toggle))
        {
            Console.Error.WriteLine($"--ab {o.Ab}: unknown switch ({AbToggles.Names})");
            return 2;
        }
        void Side(int side) => toggle?.Set(side == 0);

        var snapshot = (camera.Target, camera.Yaw, camera.Pitch, camera.Distance);
        var motion = CameraMotion.For(o.BenchMotion, o, camera, gpu);
        int motionStep = 0;

        // Per measured frame: the series of every metric (NaN where a value never arrived), the side, the block and whether it counts.
        var series = new Dictionary<string, double[]>();
        var sideOf = new int[frames];
        var blockOf = new int[frames];
        var keep = new bool[frames];
        void Set(string metric, long k, double v, bool add = false)
        {
            if (k < 0 || k >= frames) return;
            if (!series.TryGetValue(metric, out var s)) series[metric] = s = Enumerable.Repeat(double.NaN, frames).ToArray();
            s[k] = add && !double.IsNaN(s[k]) ? s[k] + v : v;
        }
        string[] stageNames = [.. StageClock.Names.Select((n, i) => i == 11 ? "present" : n)];
        var profiler = new FrameProfiler(context, () => context.GpuFrameMs);
        profiler.OnGpuFrame = (tag, stages, total) =>
        {
            double sum = 0;
            for (int s = 0; s < stageNames.Length; s++) { Set("gpu:" + stageNames[s], tag, stages[s]); sum += stages[s]; }
            Set("gpu:other", tag, Math.Max(total - sum, 0));
            Set("gpu:total", tag, Math.Max(total, sum));
        };
        post.OnCost = (tag, name, ms) => Set("post:" + name, tag, ms, add: true);

        // One frame, as the viewer draws it. By default frames stay in flight (the next one is recorded while the GPU still draws this one; the stage times are read when the frame ring comes
        // round, matched to their frame by the tag) and the returned wall time is the interval since the previous frame was submitted, so 1000 / mean is the viewer's fps.
        // --bench-serial waits for every frame instead: the interval is then CPU + GPU added up. clock: the frame clock the weather particles and surfaces run on.
        bool serial = o.BenchSerial;
        long lastSubmit = Stopwatch.GetTimestamp();
        int clockFrame = 0;
        bool clockRuns = gpu.Particles is { Groups.Count: > 0 };
        float Clock() => clockRuns ? (float)(clockFrame / 60.0 / 600) : 0;
        double Frame(long tag, bool step = true)
        {
            if (step) { if (motion.Moves) motion.Step(++motionStep); clockFrame++; }
            context.EnsureFrame();
            profiler.Tag = post.CostTag = tag;
            profiler.BeginFrame();
            Draw(gpu, scene, camera, render, w, h, o.Hour, Clock(), o.FogDistance);
            EndFrame(context);
            if (serial) context.Finish();
            profiler.EndFrame();
            long now = Stopwatch.GetTimestamp();
            double interval = (now - lastSubmit) * 1000.0 / Stopwatch.Frequency;
            lastSubmit = now;
            return interval;
        }

        // ---- warm up: shaders, the temporal upscaler's history, streaming until nothing is pending (the screenshot path's Settle and FinishLoading came first) ----
        var loading = Stopwatch.StartNew();
        for (int i = 0; i < Math.Max(post.WarmupFrames, 60); i++) Frame(-1, step: false);
        int quiet = 0;
        while (quiet < 30 && loading.Elapsed.TotalSeconds < 60)
        {
            Frame(-1, step: false);
            bool idle = (gpu.Streamer?.Idle ?? true) && (gpu.Objects?.Pending ?? 0) == 0 && (gpu.Foliage?.Pending ?? 0) == 0;
            quiet = idle ? quiet + 1 : 0;
        }
        Console.WriteLine($"bench     warm-up {loading.Elapsed.TotalSeconds:0.0} s, streaming {(quiet >= 30 ? "idle" : "still busy after 60 s")}");

        // ---- measure, holding the GPU lock ----
        using var gpuLock = o.NoGpuLock ? null : GpuLock.Acquire(Console.Out);
        if (gpuLock is not null)
            Console.WriteLine($"gpu-lock  waited {gpuLock.Waited.TotalSeconds:0.0} s{(gpuLock.StaleHolder is { } stale ? $" (took over a stale lock of PID {stale})" : "")}");
        double lockWaited = gpuLock?.Waited.TotalSeconds ?? 0;
        var wall = new double[frames];
        var cpuStages = StageClock.Names.Length;
        var measured = Stopwatch.StartNew();
        if (!ab) for (int i = 0; i < 16; i++) Frame(-1);   // the pipeline full and the first interval a frame's, not the set-up's
        lastSubmit = Stopwatch.GetTimestamp();
        if (ab)
        {
            // Both sides drawn once at least, in the pattern of the run, so the pipelines and caches of B exist before the numbers count.
            int cycleFrames = period == 1 ? 16 : period;
            for (int cycle = 0; cycle < 4; cycle++)
            {
                Side(cycle & 1);
                for (int i = 0; i < cycleFrames; i++) Frame(-1);
            }
        }
        for (int k = 0; k < frames; k++)
        {
            int block = k / period, side = ab ? block & 1 : 0;
            if (ab && k % period == 0) Side(side);
            sideOf[k] = side;
            blockOf[k] = block;
            keep[k] = k % period >= drop;
            wall[k] = Frame(k);
            Set("frame", k, wall[k]);
            double cpuSum = 0;
            for (int s = 0; s < cpuStages; s++)
            {
                Set("cpu:" + StageClock.Names[s], k, StageClock.Ms[s]);
                if (s != 11) cpuSum += StageClock.Ms[s];
            }
            Set("cpu:total", k, cpuSum);
        }
        double measuredSeconds = measured.Elapsed.TotalSeconds;
        // The last frames' timestamps arrive when their slots come round: two empty frames bring them in.
        for (int i = 0; i < 3; i++) { context.EnsureFrame(); context.Finish(); }
        profiler.Flush();
        post.Flush();
        gpuLock?.Dispose();

        // ---- the results ----
        var result = new BenchResult { Mode = ab ? "ab" : "single", Ab = o.Ab, Period = period, Drop = drop };
        string[] labels = ab ? ["A", "B"] : ["A"];
        for (int s = 0; s < labels.Length; s++)
        {
            var config = new BenchConfig { Label = labels[s] };
            var idx = Enumerable.Range(0, frames).Where(k => sideOf[k] == s && keep[k]).ToArray();
            config.Frames = idx.Length;
            foreach (var (metric, values) in series.OrderBy(m => MetricOrder(m.Key)).ThenBy(m => m.Key, StringComparer.Ordinal))
                if (BenchStats.Summarize(idx.Select(k => values[k])) is { } stats) config.Metrics[metric] = stats;
            result.Configs[labels[s]] = config;
        }
        if (ab)
            foreach (var (metric, values) in series)
                if (BenchStats.Pair(values, blockOf, keep) is { } delta) result.Deltas[metric] = delta;

        // Triangle and draw counts of the main view per side (the F11 numbers), taken after the timing so their read-backs cost nothing there.
        for (int s = 0; s < labels.Length; s++)
        {
            Side(s);
            result.Configs[labels[s]].Counts = MeasureCounts(gpu, Frame);
        }

        // ---- metadata ----
        var meta = result.Meta;
        meta["view"] = o.View ?? "(custom)";
        meta["args"] = string.Join(' ', Environment.GetCommandLineArgs().Skip(1));
        meta["commit"] = GitDescribe();
        meta["gpu"] = context.Device.DeviceName;
        meta["resolution"] = $"{w}x{h}";
        meta["upscaler"] = o.Post.Upscale.Describe();
        meta["renderScale"] = o.Post.Upscale.EffectiveScale.ToString("0.###", CultureInfo.InvariantCulture);
        meta["motion"] = o.BenchMotion;
        meta["frameMode"] = serial ? "serial (each frame waited for)" : "pipelined (frames in flight)";
        meta["frames"] = frames.ToString(CultureInfo.InvariantCulture);
        meta["date"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        meta["measuredSeconds"] = measuredSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        meta["gpuLockWaitedSeconds"] = lockWaited.ToString("0.0", CultureInfo.InvariantCulture);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string key && key.StartsWith("MEITOU_", StringComparison.Ordinal)) meta["env." + key] = e.Value?.ToString() ?? "";
        if (o.Post.Describe() is { } postText) meta["post"] = postText;

        // ---- the still pictures of A and B ----
        string outFile = o.BenchOut ?? Path.Combine(Path.GetTempPath(), $"meitou-bench-{o.View ?? "custom"}-{o.Ab ?? "single"}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        if (ab) result.Picture = StillPictures(display, gpu, scene, camera, render, o, w, h, toggle!, snapshot, motion.Moves, Clock(), Path.ChangeExtension(outFile, null), Frame);

        result.Print(Console.Out);
        result.Save(outFile);
        Console.WriteLine($"saved     {outFile}");
        return 0;
    }

    // The stages in the order a frame runs them (as the profiler lists them).
    static readonly int[] StageOrder = [0, 1, 2, 3, 12, 4, 5, 6, 7, 8, 9, 13, 10, 11];

    /// <summary>Print order of the metrics: the frame, the GPU total and stages in frame order, the post sections, the render thread's total and stages.</summary>
    static int MetricOrder(string key)
    {
        int Stage(string prefix) => Array.IndexOf(StageOrder, Array.IndexOf(StageClock.Names, key[prefix.Length..] == "present" ? "gpu-wait" : key[prefix.Length..]));
        if (key == "frame") return 0;
        if (key == "gpu:total") return 1;
        if (key.StartsWith("gpu:", StringComparison.Ordinal)) return 10 + (Stage("gpu:") is var g and >= 0 ? g : 50);
        if (key.StartsWith("post:", StringComparison.Ordinal)) return 100;
        if (key == "cpu:total") return 200;
        return 210 + (Stage("cpu:") is var c and >= 0 ? c : 50);
    }

    /// <summary>The main view's draw counts per frame, from 20 frames after a settle (the foliage's triangles from its GPU cull's arguments, which need <see cref="FoliageGpuCull.CountTriangles"/>).</summary>
    static Dictionary<string, double> MeasureCounts(Gpu gpu, Func<long, bool, double> frame)
    {
        const int settle = 10, counted = 20, late = 6;
        FoliageGpuCull.CountTriangles = true;
        for (int i = 0; i < settle; i++) frame(-1, true);
        gpu.Foliage?.ResetDrawTally();
        var objects0 = gpu.Objects is { } ot0 ? (long[,])ot0.Totals.Clone() : null;
        double terrainTris = 0, chunks = 0, blades = 0, meshes = 0, foliageCalls = 0, characterTris = 0, objectCalls = 0;
        for (int i = 0; i < counted; i++)
        {
            frame(-1, true);
            terrainTris += gpu.Terrain.DrawnTriangles;
            chunks += gpu.Terrain.DrawnChunks;
            if (gpu.Foliage is { Enabled: true } fol) { blades += fol.DrawnBlades; meshes += fol.DrawnInstances; foliageCalls += fol.DrawCalls; }
            characterTris += gpu.Characters?.DrawnTriangles ?? 0;
        }
        for (int i = 0; i < late; i++) frame(-1, true);   // the foliage tally is read a frame ring late
        var counts = new Dictionary<string, double>
        {
            ["terrain tris"] = terrainTris / counted,
            ["terrain chunks"] = chunks / counted,
        };
        if (gpu.Objects is { } ob && objects0 is not null)
        {
            int total = counted + late;
            counts["object instances"] = (ob.Totals[0, 0] - objects0[0, 0]) / (double)total;
            counts["object tris"] = (ob.Totals[0, 1] - objects0[0, 1]) / (double)total;
            objectCalls = (ob.Totals[0, 2] - objects0[0, 2]) / (double)total;
            counts["object draws"] = objectCalls;
        }
        if (gpu.Foliage is { Enabled: true } foliage)
        {
            counts["foliage tris"] = foliage.ColourTrianglesPerFrame ?? 0;
            counts["foliage draws"] = foliageCalls / counted;
            counts["foliage meshes"] = meshes / counted;
            counts["grass blades"] = blades / counted;
            if (foliage.DrawHistogram() is { } hist) foreach (var (k, v) in hist) counts[k] = v;
        }
        if (gpu.Characters is not null) counts["character tris"] = characterTris / counted;
        FoliageGpuCull.CountTriangles = false;
        return counts;
    }

    /// <summary>
    /// One still of each side from the start camera (no time advance: the frame clock stays where it was, the same number of frames is drawn for
    /// each so the upscaler's jitter phase and the caches agree), their PNGs, the differing pixels and a heat map.
    /// </summary>
    static PictureDiffResult StillPictures(Meitou.Rendering.Display.VulkanDisplay display, Gpu gpu, WorldScene scene, WorldCamera camera, WorldRenderOptions render, WorldOptions o, int w, int h,
        AbToggles.Toggle toggle, (Vector3 Target, float Yaw, float Pitch, float Distance) start, bool moved, float clock, string stem, Func<long, bool, double> frame)
    {
        var context = display.Context;
        var target = gpu.Post!.Target ?? throw new InvalidOperationException("the offscreen target is missing");
        byte[] Still(bool a)
        {
            toggle.Set(a);
            (camera.Target, camera.Yaw, camera.Pitch, camera.Distance) = start;
            if (moved)
            {
                // Back at the start: what was unloaded on the way comes back before the picture.
                gpu.Streamer?.Settle(gpu.Anchor ?? camera.Eye);
                gpu.Objects?.Settle(camera.Eye);
                gpu.Foliage?.Settle(camera.Eye);
            }
            for (int i = 0; i < Math.Max(gpu.Post!.WarmupFrames, 70); i++)
            {
                context.EnsureFrame();
                Draw(gpu, scene, camera, render, w, h, o.Hour, clock, o.FogDistance);
                EndFrame(context);
                context.Finish();
            }
            return Meitou.Rendering.FramebufferCapture.Read(context, target, w, h);
        }
        var pa = Still(true);
        var pb = Still(false);
        var (diff, heat) = PictureDiff.Compare(pa, pb, w, h);
        diff.A = stem + "-A.png";
        diff.B = stem + "-B.png";
        diff.Diff = stem + "-diff.png";
        PictureDiff.SavePng(diff.A, pa, w, h);
        PictureDiff.SavePng(diff.B, pb, w, h);
        PictureDiff.SavePng(diff.Diff, heat, w, h);
        toggle.Set(true);
        return diff;
    }

    static string GitDescribe()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "describe --always --dirty --abbrev=8") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory };
            using var p = Process.Start(psi);
            string text = p!.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return p.ExitCode == 0 && text.Length > 0 ? text : "unknown";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return "unknown"; }
    }
}
