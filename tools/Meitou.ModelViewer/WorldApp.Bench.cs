using System.Diagnostics;
using System.Globalization;
using System.Numerics;

using Meitou.Game.Rendering.Bench;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Shadows;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;
using static Meitou.Game.Rendering.Frame.WorldFrame;

namespace Meitou.ModelViewer;

static partial class WorldApp
{
    /// <summary>
    /// The benchmark harness (<c>--bench-frames</c>, <c>--ab</c>; docs/bench.md "Benchmark harness"): measures a fixed view, alternating the two
    /// sides of one switch when asked, and writes per-stage statistics (mean, median, p95, p99, max for each side and the paired difference) as a
    /// table and JSON.
    /// </summary>
    static int Bench(Game.Rendering.Display.VulkanDisplay display, WorldGpu gpu, WorldScene scene, WorldCamera camera, WorldRenderOptions render,
            WorldOptions o, int w, int h)
    {
        var context = display.Context;
        var post = gpu.Post!;
        bool ab = o.Ab is not null;
        int frames = o.BenchFrames > 0 ? o.BenchFrames : 600;
        int period = ab ? o.AbPeriod : 1;
        int drop = ab && period > 1 ? Math.Min(Math.Min(period / 4, 16), period - 1) : 0;

        // The runtime switches: the Faithful / Meitou ones under their ids (A = Meitou), then what can flip between two frames (A = on).
        RegisterCullToggles(gpu, post, o, camera, render);
        RegisterPostToggles(post);
        RegisterShadowAndFoliageToggles(gpu);
        RegisterSkyAndSceneToggles(gpu, render);
        RegisterGiToggles(gpu);
        RegisterTerrainObjectAndWaterToggles(gpu, render);
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
        string[] stageNames = StageClock.Names;
        var profiler = new FrameProfiler(context, () => context.GpuFrameMs);
        profiler.OnGpuFrame = (tag, stages, total) =>
        {
            double sum = 0;
            for (int s = 0; s < stageNames.Length; s++)
            {
                if (StageClock.CpuOnly(s)) continue;
                Set("gpu:" + stageNames[s], tag, stages[s]);
                sum += stages[s];
            }
            Set("gpu:other", tag, Math.Max(total - sum, 0));
            Set("gpu:total", tag, Math.Max(total, sum));
        };
        post.OnCost = (tag, name, ms) => Set("post:" + name, tag, ms, add: true);

        // One frame, as the viewer draws it. By default frames stay in flight (the next one is recorded while the GPU still draws this one; the stage
        // times are read when the frame ring comes round, matched to their frame by the tag) and the returned wall time is the interval since the
        // previous frame was submitted, so 1000 / mean is the viewer's fps. --bench-serial waits for every frame instead: the interval is then CPU +
        // GPU added up. clock: the frame clock the weather particles and surfaces run on.
        bool serial = o.BenchSerial;
        long lastSubmit = Stopwatch.GetTimestamp();
        int clockFrame = 0;
        bool clockRuns = gpu.Particles is { Groups.Count: > 0 };
        float Clock() => clockRuns ? (float)(clockFrame / 60.0 / 600) : 0;
        double Frame(long tag, bool step = true)
        {
            if (step)
            {
                if (motion.Moves) motion.Step(++motionStep);
                clockFrame++;
                if (o.SwayStep > 0 && gpu.Foliage is { } swaying) swaying.SwaySeconds = (swaying.SwaySeconds ?? 0) + o.SwayStep;
            }
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

        // ---- warm up: shaders, the temporal upscaler's history, streaming until nothing is pending (the screenshot path's Settle and FinishLoading
        // came first) ----
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
                if (s != StageClock.DriverWait) cpuSum += StageClock.Ms[s];
            }
            Set("cpu:total", k, cpuSum);
            for (int p = 0; p < StageClock.PartNames.Length; p++) Set("cpu:upd-chars/" + StageClock.PartNames[p], k, StageClock.PartMs[p]);
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

        // The share of the screen's shading rate tiles at each rate (side A; serial frames after the timing, with the rate image read back).
        if (post.FogVrsSupported && (o.Ab == "fog-vrs" || post.FogVrs))
        {
            Side(0);
            post.FogVrsStats = true;
            post.ResetFogVrsStats();
            for (int i = 0; i < 24; i++) Frame(-1, true);
            for (int i = 0; i < 3; i++) { context.EnsureFrame(); context.Finish(); }
            post.FogVrsStats = false;
            if (post.FogVrsShare is { Frames: > 0 } share) result.Meta["fogVrsShare"] = string.Create(CultureInfo.InvariantCulture, $"1x1 {share.One * 100:0.0} %, 2x2 {share.Two * 100:0.0} %, 4x4 {share.Four * 100:0.0} % of the tiles ({share.Frames} frames)");
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
        if (gpu.FogVolumes?.DescribeCull() is { } fogText) meta["fogCull"] = fogText;   // what the weather fog hides in this view, and from how far
        if (gpu.Shadow is { Cascades: { } casc } shadowNow) meta["shadowRange"] = string.Create(CultureInfo.InvariantCulture, $"range {shadowNow.EffectiveRange:0} (setting {shadowNow.Settings.Range:0}), cascade view depths {string.Join(", ", casc.Select(c => $"{c.NearDepth:0}-{c.FarDepth:0}"))}");
        meta["profilerStamps"] = profiler.StampsLastFrame.ToString(CultureInfo.InvariantCulture);   // timestamps a frame, the profiler's cost
        meta["date"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        meta["measuredSeconds"] = measuredSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        meta["gpuLockWaitedSeconds"] = lockWaited.ToString("0.0", CultureInfo.InvariantCulture);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string key && key.StartsWith("MEITOU_", StringComparison.Ordinal)) meta["env." + key] = e.Value?.ToString() ?? "";
        if (o.Post.Describe() is { } postText) meta["post"] = postText;
        meta["sky"] = gpu.Sky.DescribeCost() + (gpu.Sky.FogCullDistance is { } skyFog ? string.Create(CultureInfo.InvariantCulture, $"; weather fog complete from {skyFog:0}") : "; weather fog never complete");

        // ---- --bench-tris: triangles per pass and the triangle size histogram (separate, serial counting frames after the timing) ----
        if (o.BenchTris)
            for (int s = 0; s < labels.Length; s++)
            {
                Side(s);
                MeasureTriangles(context, gpu, render, Frame, result, labels[s]);
            }

        // ---- the still pictures of A and B ----
        string outFile = o.BenchOut
                ?? Path.Combine(Path.GetTempPath(), $"meitou-bench-{o.View ?? "custom"}-{o.Ab ?? "single"}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        if (ab) result.Picture = StillPictures(display, gpu, scene, camera, render, o, w, h, toggle!, snapshot, motion.Moves, Clock(),
                Path.ChangeExtension(outFile, null), Frame);

        result.Print(Console.Out);
        result.Save(outFile);
        Console.WriteLine($"saved     {outFile}");
        string csvFile = Path.ChangeExtension(outFile, null) + "-frames.csv";
        SaveFrames(csvFile, series, sideOf, keep);
        Console.WriteLine($"saved     {csvFile} (every metric per measured frame)");
        return 0;
    }

    /// <summary>
    /// The runtime switches of <see cref="Bench"/>, first part: the Faithful / Meitou ones, the water, shadow and foliage culls and the fog volumes.
    /// </summary>
    static void RegisterCullToggles(WorldGpu gpu, PostProcess post, WorldOptions o, WorldCamera camera, WorldRenderOptions render)
    {
        // The runtime switches: the Faithful / Meitou ones under their ids (A = Meitou), then what can flip between two frames (A = on).
        AbToggles.Clear();
        AbToggles.RegisterEnhancements(LiveSwitches(o, () => gpu, () => camera, () => render));
        if (gpu.Water is { } water) AbToggles.Register("water-lean", () => water.Lean, v => water.Lean = v,
            "skip zero-contribution surf, ocean and distant detail work and refraction copies of an opaque view; B: always evaluate/copy");
        AbToggles.Register("shadow-zone-box", () => ShadowZoneBox.Enabled, v => ShadowZoneBox.Enabled = v,
            "each shadow cascade's foliage work list box-tested by zone against the cascade's planes (A) against every zone within 1.2 x the shadow range (B)");
        AbToggles.Register("char-casters", () => ShadowPass.CharacterCasterCull, v => ShadowPass.CharacterCasterCull = v,
            "character parts under a texel across cast nothing into the coarse shadow cascades (A) against every part in every cascade (B)");
        AbToggles.Register("occlusion", () => post.OcclusionCull, v => post.OcclusionCull = v,
                "foliage occlusion cull against the previous frame's depth pyramid");
        if (gpu.FogVolumes is { } fog)
        {
            AbToggles.Register("fog-cull", () => fog.CullEnabled, v => fog.CullEnabled = v, "culling of what the fog hides");
            AbToggles.Register("fog-volumes", () => fog.Enabled, v => fog.Enabled = v, "the placed fog volumes");
            AbToggles.Register("fog-direction", () => fog.DirectionBound, v => fog.DirectionBound = v,
                    "the fog cull's bound by ray direction (off: only the plain bound, which finds no distance at a 50000 far clip)");
        }
    }

    /// <summary>
    /// The post-processing chain's switches: fog shading rate, merged layers, half-size passes, render scale, merged compute passes.
    /// </summary>
    static void RegisterPostToggles(PostProcess post)
    {
        if (post.FogVrsSupported) AbToggles.Register("fog-vrs", () => post.FogVrs, v => post.FogVrs = v,
                "variable-rate shading where the fog hides the surface: the opaque scene passes shade 2x2 or 4x4 pixels with one fragment; B: every pixel shaded");
        AbToggles.Register("layer-merge", () => post.MergeLayers, v => post.MergeLayers = v,
                "the light shafts' apply, the fog volumes and the SSAO onto the scene in one pass (A) against three passes (B)");
        AbToggles.Register("sun-motion", () => post.SunMotion, v => post.SunMotion = v,
                "the sky round the sun moves with the sun in the motion vectors (A) against the camera's motion alone (B)");
        AbToggles.Register("fog-half", () => post.FogHalf, v => post.FogHalf = v,
                "with the merged layers, the fog volumes at half the render size, upsampled by depth (A) against full size (B)");
        AbToggles.Register("shafts-half", () => post.ShaftsHalf, v => post.ShaftsHalf = v,
                "with the merged layers, the light shafts' apply at half the render size too, in the fog volumes' texture, upsampled by depth (A) against full size (B)");
        AbToggles.Register("layers-lean", () => post.LeanLayers, v => post.LeanLayers = v,
                "with the layers at half size, the merged pass only upsamples and the few pixels no half-size texel fits are drawn by a second pass tested by a mask depth (A) against one pass doing both (B)");
        AbToggles.Register("layers-all", () => post.ShaftsHalf && post.LeanLayers, v => { post.ShaftsHalf = v; post.LeanLayers = v; },
                "shafts-half and layers-lean together (A) against both off (B): the merged layers as they were before the shafts went to half size");
        AbToggles.Register("ssao-mask-skip", () => post.MaskSkip, v => post.MaskSkip = v,
                "the SSAO blur's read of the character mask (the scene colour's alpha) left out when no character was drawn this frame (A) against always read (B)");
        AbToggles.Register("ssao-layers", () => post.ShaftsHalf && post.LeanLayers && post.MaskSkip,
                v => { post.ShaftsHalf = v; post.LeanLayers = v; post.MaskSkip = v; },
                        "all three of the SSAO and layers change (shafts-half, layers-lean, ssao-mask-skip) on (A) against all off (B): the merged layers and the blur as they were before");
        if (post.ShaftScanSupported) AbToggles.Register("shafts-scan", () => post.ShaftScan, v => post.ShaftScan = v,
                "the light shafts' integrate and air-layer scatter as one compute kernel walking each cell column's slices once, A, against the two fragment passes looping over every nearer slice, B");
        float startScale = post.Options.Upscale.EffectiveScale;
        AbToggles.Register("render-scale", () => post.Options.Upscale.EffectiveScale >= startScale, v => post.Options.Upscale.Scale = v ? startScale
                : MathF.Round(startScale * 0.67f * 100) / 100,
            "the render scale as set (A) against two thirds of it (B), switched at run time like the Tab slider (targets and upscaler made again)");
        if (post.MergeSupported) AbToggles.Register("post-merge",() => post.MergePasses, v => post.MergePasses = v,
                "merged post passes: one compute dispatch for the motion, upscaler depth and reactivity, one for the exposure measure and adaptation; B: the separate full-screen passes");
        if (post.MergeSupported)
        {
            AbToggles.Register("post-merge-velocity", () => post.MergeVelocity, v => post.MergeVelocity = v,
                    "the velocity kernel alone (see post-merge)");
            AbToggles.Register("post-merge-exposure", () => post.MergeExposure, v => post.MergeExposure = v,
                    "the exposure kernel alone (see post-merge)");
        }
    }

    /// <summary>
    /// The shadow pass's and the foliage's switches.
    /// </summary>
    static void RegisterShadowAndFoliageToggles(WorldGpu gpu)
    {
        if (gpu.Shadow is { } shadowPass) AbToggles.Register("shadow-pass", () => shadowPass.Enabled, v => shadowPass.Enabled = v,
                "the whole shadow pass");
        if (gpu.Shadow is { } spreadPass) AbToggles.Register("shadow-spread", () => spreadPass.FarBudget > 0, v => spreadPass.FarBudget = v
                ? ShadowSchedule.DefaultBudget : 0, "at most one far shadow cascade redrawn per frame (B: all that are due at once)");
        if (gpu.Shadow is { } fogCapPass) AbToggles.Register("shadow-fogcap", () => fogCapPass.FogCapEnabled, v => fogCapPass.FogCapEnabled = v,
                "the sun shadows' range capped at the placed fog block's hide distance when the block hides the whole view; B: the full range");
        AbToggles.Register("shadow-coarse", () => ShadowLod.Enabled, v => ShadowLod.Enabled = v,
                "coarser casters in the far shadow cascades (1 to 3): objects, terrain nodes and generated foliage levels by the cascade's texel (B: as the main view's rule, cascade by cascade as before)");
        if (gpu.Foliage is { } foliagePass) AbToggles.Register("foliage-draw", () => foliagePass.Enabled, v => foliagePass.Enabled = v,
                "trees, bushes, rocks and grass (--no-foliage)");
        if (gpu.Foliage is { } grassPass)
        {
            AbToggles.Register("grass", () => grassPass.DrawGrass, v => grassPass.DrawGrass = v, "the grass blades");
            AbToggles.Register("lod-far", () => grassPass.LodFar, v => grassPass.LodFar = v,
                    "generated rock levels: the pixel tolerance growing with the distance (needs lod on)");
            AbToggles.Register("foliage-meshes", () => grassPass.DrawMeshes, v => grassPass.DrawMeshes = v,
                    "foliage meshes and impostors, not the TERRAIN-mode rocks");
            AbToggles.Register("foliage-prepass", () => FoliagePrepass.Enabled, v => FoliagePrepass.Enabled = v,
                    "the main view's foliage meshes drawn as a depth prepass (the fade dither and the cut-out) and then the colour with no discard and the depth test EQUAL; B: the colour once, as before");
            AbToggles.Register("impostor-prepass", () => Game.Rendering.Impostors.ImpostorPrepass.Enabled,
                    v => Game.Rendering.Impostors.ImpostorPrepass.Enabled = v, "the main view's billboards drawn as a depth prepass (the fade, the frame pick, the cut-out) and then the lighting with no discard and the depth test EQUAL; B: the lighting for every pixel that passes, as before");
            AbToggles.Register("impostor-quad", () => Game.Rendering.Impostors.ImpostorQuad.Enabled,
                    v => Game.Rendering.Impostors.ImpostorQuad.Enabled = v, "billboards drawn as the rectangle that holds the mesh's bounding box seen from the eye; B: the square of the bounding sphere's silhouette, as before");
            AbToggles.Register("card-trim", () => grassPass.CardTrim, v => grassPass.CardTrim = v,
                    "alpha-tested foliage cards drawn cut to the opaque outline of their texture; B: the whole cards");
            AbToggles.Register("screen-lod", () => grassPass.ScreenLod, v => grassPass.ScreenLod = v,
                    "foliage by the size of its triangles on the screen: generated levels sooner and billboards sooner where the triangles are under 1.5 px2; B: levels by deviation, billboards at the impostor distance");
            AbToggles.Register("grass-velocity", () => grassPass.GrassVelocityCull, v => grassPass.GrassVelocityCull = v,
                    "the grass's own motion vectors only for blades that move 0.5 px a frame or more, the camera reprojection for the rest; B: every blade redrawn in the motion pass");
        }
    }

    /// <summary>
    /// The sky's fog skips, the lamps, anisotropy, weather particles, threaded recording and the objects.
    /// </summary>
    static void RegisterSkyAndSceneToggles(WorldGpu gpu, WorldRenderOptions render)
    {
        AbToggles.Register("cloud-fog-skip", () => gpu.Sky.Clouds.CloudFogSkip, v => gpu.Sky.Clouds.CloudFogSkip = v,
                "volumetric clouds not marched where the fog hides the sky: the whole pass under complete weather fog, and in the main view each texel the placed fog volumes hide; B: every upward ray marched");
        AbToggles.Register("sky-fog-skip", () => gpu.Sky.Clouds.SkyFogSkip, v => gpu.Sky.Clouds.SkyFogSkip = v,
                "the main view's sky pass and cloud march left out where the placed fog block hides the sky in every direction of the view; B: drawn");
        if (gpu.Lamps is { } lamps) AbToggles.Register("lights", () => lamps.Enabled, v => lamps.Enabled = v,
                "the lamps: the game's point and spot lights on the world");
        AbToggles.Register("anisotropy", () => gpu.Sky.Gpu.Samplers.MaxAnisotropy > 1, v => gpu.Sky.Gpu.Samplers.MaxAnisotropy = v ? 16 : 1,
                "anisotropic filtering as the textures ask (A) against none, 1x (B); the Tab slider's ends");
        AbToggles.Register("weather-particles", () => gpu.WeatherParticles, v => gpu.WeatherParticles = v,
                "the weather's particles, simulated and drawn (the Tab slider at 0)");
        {
            int threaded = Recording.Mode > 0 ? Recording.Mode : 2;
            AbToggles.Register("record-threads", () => Recording.Mode == threaded, v => Recording.Mode = v ? threaded : 0, "the native hosts' secondary command buffers recorded on the job threads (A: mode 2); B: mode 0, every segment inline on the render thread");
            }
        AbToggles.Register("objects-draw", () => render.Objects, v => render.Objects = v, "buildings and map features (--no-objects)");
    }

    /// <summary>
    /// The global illumination's switches (with <c>--gi</c>).
    /// </summary>
    static void RegisterGiToggles(WorldGpu gpu)
    {
        if (gpu.Gi is not null)
        {
            int giMode = gpu.GiDebug;
            AbToggles.Register("gi-scene", () => gpu.GiActive, v => gpu.GiActive = v,
                    "the global illumination's traced scene (acceleration structure builds) and its debug view (needs --gi-debug)");
            if (gpu.Probes is { } shadeProbes) AbToggles.Register("gi-shade", () => shadeProbes.Shade, v => shadeProbes.Shade = v,
                    "the world's shading reading the probes (side B: probes updated, not read)");
            if (gpu.Gi is { } traceScene) AbToggles.Register("gi-top-trace", () => traceScene.TopFastTrace, v => traceScene.TopFastTrace = v,
                    "the traced scene's top level built to trace fast (A) against built fast (B)");
            if (gpu.Gi is { } lazyScene) AbToggles.Register("gi-lazy-scene", () => lazyScene.LazyScene, v => lazyScene.LazyScene = v,
                    "the traced scene changing only when the eye crosses into another 1024-unit cell: object selection, foliage block, origin and finished terrain grids together; B: each at its own step");
            if (gpu.Gi is { } sharedScene) AbToggles.Register("gi-top-shared", () => sharedScene.SharedTop, v => sharedScene.SharedTop = v,
                    "one top-level structure for all frame slots, built once per change of the scene; B: one per slot");
            if (gpu.Gi is { } refitScene) AbToggles.Register("gi-terrain-refit", () => refitScene.RefitTerrain, v => refitScene.RefitTerrain = v,
                    "the traced terrain grids refitted in place when they follow the eye; B: rebuilt into a new structure");
            if (gpu.Gi is { } topScene) AbToggles.Register("gi-top-reuse", () => topScene.ReuseTop, v => topScene.ReuseTop = v,
                    "the traced scene's top level left as it was when its instances are those of its last build, the origin and the object set moving in coarse steps; B: built every frame");
            if (gpu.Probes is { } rayProbes)
            {
                int rayCount = rayProbes.Rays;
                AbToggles.Register("gi-rays", () => rayProbes.Rays == rayCount, v => rayProbes.Rays = v ? rayCount : rayCount == 64 ? 32 : 64,
                        "rays per probe as set (A, default 32) against the other of 32 and 64 (B), the hysteresis scaled so that the noise stays the same");
            }
            if (gpu.Probes is { } farProbes)
            {
                int farPeriod = farProbes.FarPeriod > 1 ? farProbes.FarPeriod : 2;
                AbToggles.Register("gi-far", () => farProbes.FarPeriod > 1, v => farProbes.FarPeriod = v ? farPeriod : 1,
                        "the far cascade's probes traced and blended on every other round only; B: every round");
            }
            if (gpu.Probes is { } sleepProbes) AbToggles.Register("gi-sleep", () => sleepProbes.SleepPeriod > 0, v => sleepProbes.SleepPeriod = v ? 8
                    : 0, "probes with no surface near sleep (side B: every probe traced)");
            if (gpu.Probes is { } fogProbes)
            {
                int hiddenPeriod = fogProbes.HiddenPeriod > 0 ? fogProbes.HiddenPeriod : 16;
                AbToggles.Register("gi-fog", () => fogProbes.HiddenPeriod > 0, v => fogProbes.HiddenPeriod = v ? hiddenPeriod : 0,
                        "probes no visible surface can read, because the fog hides everything near them, updated every 16th round (side B: at the normal rate)");
            }
            if (gpu.Probes is { } reflectProbes) AbToggles.Register("gi-reflection", () => !reflectProbes.Reflected,
                    v => reflectProbes.Reflected = !v, "the water reflection without the probes (side B: it reads them)");
            {
                int resolve = gpu.GiResolve > 0 ? gpu.GiResolve : 2;
                AbToggles.Register("gi-resolve", () => gpu.GiResolve > 0, v => gpu.GiResolve = v ? resolve : 0, "the probes read once per pixel after the scene (side B: in every lit fragment)");
                }
            AbToggles.Register("gi-debug", () => gpu.GiDebug > 0, v => gpu.GiDebug = v ? giMode : 0,
                    "the global illumination's debug view, the scene built on both sides (needs --gi-debug)");
        }
    }

    /// <summary>
    /// The terrain cost probes, the objects' draw order and programs, the terrain switches, normal maps, the water and its reflection.
    /// </summary>
    static void RegisterTerrainObjectAndWaterToggles(WorldGpu gpu, WorldRenderOptions render)
    {
        {
            // Cost probes for the terrain (side B: error doubled, about a quarter of the triangles; ground colour only, no textured material).
            float err = render.TerrainPixelError, materialDistance = render.MaterialDistance;
            AbToggles.Register("terrain-error", () => render.TerrainPixelError <= err * 1.01f, v => render.TerrainPixelError = v ? err : err * 2,
                    "terrain screen-space error doubled on side B (probe)");
            AbToggles.Register("terrain-material", () => render.MaterialDistance >= materialDistance, v => render.MaterialDistance = v
                    ? materialDistance : 1, "terrain textured material (side B: ground colour only; probe)");
        }
        if (gpu.Objects is { } sortObjects) AbToggles.Register("object-sort", () => sortObjects.SortNearestFirst,
                v => sortObjects.SortNearestFirst = v, "objects' colour batches drawn nearest first (Meitou reach)");
        AbToggles.Register("early-depth", () => EarlyDepth.Enabled, v => EarlyDepth.Enabled = v,
                "objects, rocks and foliage meshes use programs without a discard where fully visible and uncut (early depth test); B: the single program with the discard");
        AbToggles.Register("object-lod-gen", () => ObjectLodGen.Enabled, v => ObjectLodGen.Enabled = v,
                "objects and buildings: generated coarser mesh levels among the file's, picked by the size of their triangles on the screen; B: the file's levels only");
        AbToggles.Register("solid-first", () => WorldObjectRenderer.SolidFirst, v => WorldObjectRenderer.SolidFirst = v,
                "objects: the draws without a discard (early depth test) ahead of the dithered and cut-out ones");
        RegisterTerrainAb();
        AbToggles.Register("normal-maps", () => render.NormalMaps, v => render.NormalMaps = v, "normal maps on terrain and objects (probe)");
        AbToggles.Register("water-draw", () => render.Water, v => render.Water = v, "the water pass (--no-water)");
        AbToggles.Register("reflections", () => render.Reflections, v => render.Reflections = v, "the water reflection pass (--water-reflection 0)");
        if (gpu.Reflection is { } rp)
        {
            AbToggles.Register("refl-cull", () => rp.CullToWater, v => rp.CullToWater = rp.CropToWater = v,
                    "the cheaper reflection: only what the water shows (crop, footprint, size), objects to 2000 and foliage to 2200 units (B: 3000)");
            AbToggles.Register("refl-shadows", () => rp.NoShadows, v => rp.NoShadows = v,
                    "the reflection without sun shadows (A, Meitou; B: the shadows on the mirrored scene; Faithful shadows keep them regardless)");
            int msaaA = rp.Samples;
            AbToggles.Register("refl-msaa", () => rp.Samples == msaaA, v => rp.Samples = v ? msaaA : 1,
                    "the reflection's multisampling: the default samples (A, 4) against one sample (B)");
            AbToggles.Register("refl-foliage", () => rp.Level >= 4, v => rp.Level = v ? 4 : 3,
                    "foliage in the water reflection (--water-reflection 4 against 3)");
            AbToggles.Register("refl-objects", () => rp.Level >= 3, v => rp.Level = v ? 4 : 2,
                    "objects and foliage in the water reflection (--water-reflection 4 against 2)");
        }
    }

    /// <summary>
    /// The per-frame series as CSV (frame, side, kept, then each metric in print order; empty where a value never arrived), for patterns the
    /// summaries hide.
    /// </summary>
    static void SaveFrames(string path, Dictionary<string, double[]> series, int[] sideOf, bool[] keep)
    {
        var metrics = series.Keys.OrderBy(MetricOrder).ThenBy(k => k, StringComparer.Ordinal).ToArray();
        using var csv = new StreamWriter(path);
        csv.WriteLine("frame,side,kept," + string.Join(',', metrics));
        for (int k = 0; k < sideOf.Length; k++)
            csv.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{k},{(sideOf[k] == 0 ? 'A' : 'B')},{(keep[k] ? 1 : 0)},")
                + string.Join(',', metrics.Select(m => double.IsNaN(series[m][k]) ? "" : series[m][k].ToString("0.####", CultureInfo.InvariantCulture))));
    }

    // The stages in the order a frame runs them (as the profiler lists them).
    static readonly int[] StageOrder = [0, 1, 2, StageClock.UpdChars, 3, StageClock.Uploads, StageClock.Cull, StageClock.Bake, 12, 14, 4, 5, 6, 7, 8,
            StageClock.Characters, 9, StageClock.Shafts, StageClock.Fog, 13, 10, StageClock.Ui, StageClock.Submit, StageClock.DriverWait];

    /// <summary>
    /// Print order of the metrics: the frame, the GPU total and stages in frame order, the post sections, the render thread's total and stages.
    /// </summary>
    static int MetricOrder(string key)
    {
        int Stage(string prefix) => Array.IndexOf(StageOrder,
                Array.IndexOf(StageClock.Names, key[prefix.Length..] == "present" ? "ui" : key[prefix.Length..]));
        if (key == "frame") return 0;
        if (key == "gpu:total") return 1;
        if (key.StartsWith("gpu:", StringComparison.Ordinal)) return 10 + (Stage("gpu:") is var g and >= 0 ? g : 50);
        if (key.StartsWith("post:", StringComparison.Ordinal)) return 100;
        if (key == "cpu:total") return 200;
        return 210 + (Stage("cpu:") is var c and >= 0 ? c : 50);
    }

    /// <summary>
    /// The main view's draw counts per frame, from 20 frames after a settle (the foliage's triangles from its GPU cull's arguments, which need
    /// <see cref="FoliageGpuCull.CountTriangles"/>).
    /// </summary>
    static Dictionary<string, double> MeasureCounts(WorldGpu gpu, Func<long, bool, double> frame)
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
            if (gpu.Foliage is { Enabled: true } fol)
            {
                blades += fol.DrawnBlades;
                meshes += fol.DrawnInstances;
                foliageCalls += fol.DrawCalls;
            }
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
    /// <c>--bench-tris</c> (docs/bench.md "Triangles per pass"): after the timing, (1) the pipeline statistics per pass over serial frames drawn with
    /// <see cref="Recording.Mode"/> 0 (a query cannot span the secondaries), and (2) the main view's triangles by screen area per category, with
    /// the colour programs swapped for counting variants and the reflection off (it draws the same programs). The camera keeps moving as in the
    /// counting frames. Neither runs in a timed frame.
    /// </summary>
    static void MeasureTriangles(GpuContext context, WorldGpu gpu, WorldRenderOptions render, Func<long, bool, double> frame, BenchResult result,
            string side)
    {
        const int frames = 8, settle = 4;
        var config = result.Configs[side];
        var post = gpu.Post!;
        result.Meta["renderPixels"] = ((double)post.RenderWidth * post.RenderHeight).ToString("0", CultureInfo.InvariantCulture);
        result.Meta["passFrames"] = frames.ToString(CultureInfo.InvariantCulture);

        if (PipelineStatsMeter.Supported(context))
        {
            int mode = Recording.Mode;
            Recording.Mode = 0;
            var previousStart = StageClock.OnStart;
            var previousClose = StageClock.OnClose;
            using var meter = new PipelineStatsMeter(context);
            StageClock.OnStart = () => { previousStart?.Invoke(); meter.BeginFrame(); };
            StageClock.OnClose = (label, sub) => { previousClose?.Invoke(label, sub); meter.OnClose(label, sub); };
            try
            {
                for (int i = 0; i < settle; i++) { frame(-1, true); context.Finish(); }
                var sum = new Dictionary<string, PassStat>();
                var order = new List<string>();
                var position = new Dictionary<string, (double Sum, int Seen)>();   // where in the frame a row came, to list them in frame order
                for (int i = 0; i < frames; i++)
                {
                    frame(-1, true);
                    context.Finish();
                    var read = meter.Read();
                    for (int k = 0; k < read.Count; k++)
                    {
                        var (key, stat) = read[k];
                        if (!sum.TryGetValue(key, out var total))
                        {
                            sum[key] = total = new PassStat();
                            order.Add(key);
                        }
                        var (psum, pseen) = position.GetValueOrDefault(key);
                        position[key] = (psum + k / (double)read.Count, pseen + 1);
                        total.Add(stat);
                    }
                }
                foreach (var key in order.OrderBy(k => position[k].Sum / position[k].Seen))
                {
                    var mean = new PassStat();
                    mean.Add(sum[key], 1.0 / frames);
                    config.Passes[key] = mean;
                }
                if (meter.Lost > 0) Console.WriteLine($"bench     pipeline statistics: {meter.Lost} queries lost (pool full or closed in another command buffer); the pass numbers are incomplete");
            }
            finally
            {
                StageClock.OnStart = previousStart;
                StageClock.OnClose = previousClose;
                Recording.Mode = mode;
            }
        }
        else Console.WriteLine("bench     the device has no pipeline statistics queries: only the counts per frame are given");

        if (TriangleCounter.Supported(context))
        {
            bool reflections = render.Reflections;
            render.Reflections = false;
            using var counter = new TriangleCounter(context);
            try
            {
                counter.Begin();
                for (int i = 0; i < settle; i++) { frame(-1, true); context.Finish(); }   // the counting pipelines are made at the first draws
                counter.Clear();
                for (int i = 0; i < frames; i++) frame(-1, true);
                foreach (var (name, h) in counter.Read()) config.Sizes[name] = h.Scaled(1.0 / frames);
                Console.WriteLine($"bench     triangle sizes: counting programs {string.Join(", ", counter.Counted)}");
                if (counter.Skipped.Count > 0) Console.WriteLine($"bench     triangle sizes: not counted (no single main in the text): {string.Join(", ", counter.Skipped)}");
                if (PipelineStatsMeter.Supported(context)) CrossCheck(context, counter, frame, result.Meta);
            }
            finally
            {
                counter.End();
                render.Reflections = reflections;
            }
        }
        else Console.WriteLine("bench     the device has no fragment shader barycentrics: no triangle size histogram");
    }

    /// <summary>
    /// The two <c>--bench-tris</c> measurements on the same frames: the shaded samples the counting variants saw per category against the fragment
    /// invocations the pipeline statistics found for the stage that draws it (mean per frame). For a category whose variant has the early depth test
    /// the samples are a subset of the invocations (the invocations also include samples a shader discards), so samples above invocations point at a
    /// draw that one of the two measurements misses. Printed and kept in the metadata as <c>trisCheck</c>.
    /// </summary>
    static void CrossCheck(GpuContext context, TriangleCounter counter, Func<long, bool, double> frame, Dictionary<string, string> meta)
    {
        const int frames = 4;
        int mode = Recording.Mode;
        Recording.Mode = 0;
        var previousStart = StageClock.OnStart;
        var previousClose = StageClock.OnClose;
        using var meter = new PipelineStatsMeter(context);
        StageClock.OnStart = () => { previousStart?.Invoke(); meter.BeginFrame(); };
        StageClock.OnClose = (label, sub) => { previousClose?.Invoke(label, sub); meter.OnClose(label, sub); };
        var invocations = new Dictionary<string, double>();
        try
        {
            counter.Clear();
            for (int i = 0; i < frames; i++)
            {
                frame(-1, true);
                context.Finish();
                foreach (var (key, stat) in meter.Read())
                {
                    string category = key.StartsWith("shadow", StringComparison.Ordinal) || key.StartsWith("reflection", StringComparison.Ordinal)
                            || key.StartsWith("post", StringComparison.Ordinal) ? "" :
                        key.EndsWith("fol rocks", StringComparison.Ordinal) ? "rocks" : key.EndsWith("fol meshes", StringComparison.Ordinal)
                                ? "foliage meshes" : key.EndsWith("fol grass", StringComparison.Ordinal) ? "grass" :
                        key.StartsWith("terrain", StringComparison.Ordinal) ? "terrain" : key.StartsWith("objects", StringComparison.Ordinal)
                                ? "objects" : "";
                    if (category.Length > 0) invocations[category] = invocations.GetValueOrDefault(category) + stat.FragmentInvocations / frames;
                }
            }
            var samples = counter.Read();
            var parts = TriangleBins.Categories.Where(c => invocations.ContainsKey(c) || samples.ContainsKey(c)).Select(c =>
                $"{c} {Big(samples.TryGetValue(c, out var h) ? h.TotalPixels / frames : 0)} / {Big(invocations.GetValueOrDefault(c))}");
            string text = string.Join(", ", parts);
            meta["trisCheck"] = text;
            Console.WriteLine($"bench     check, shaded samples / fragment invocations per frame (same frames): {text}");
        }
        finally
        {
            StageClock.OnStart = previousStart;
            StageClock.OnClose = previousClose;
            Recording.Mode = mode;
        }
    }

    static string Big(double v) => v >= 1e6 ? (v / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + "M" : v >= 1e3
            ? (v / 1e3).ToString("0.0", CultureInfo.InvariantCulture) + "k" : v.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>
    /// One still of each side from the start camera (no time advance: the frame clock stays where it was, the same number of frames is drawn for
    /// each so the upscaler's jitter phase and the caches agree), their PNGs, the differing pixels and a heat map.
    /// </summary>
    static PictureDiffResult StillPictures(Game.Rendering.Display.VulkanDisplay display, WorldGpu gpu, WorldScene scene, WorldCamera camera,
            WorldRenderOptions render, WorldOptions o, int w, int h,
        AbToggles.Toggle toggle, (Vector3 Target, float Yaw, float Pitch,
                float Distance) start, bool moved, float clock, string stem, Func<long, bool, double> frame)
    {
        var context = display.Context;
        var target = gpu.Post!.Target ?? throw new InvalidOperationException("the offscreen target is missing");
        byte[] Still(bool a)
        {
            toggle.Set(a);
            (camera.Target, camera.Yaw, camera.Pitch, camera.Distance) = start;
            if (moved)
            {
                // Back at the start: what was unloaded on the way comes back before the picture. The settles upload, so they need an open frame
                // (the timing loop ends with the frames finished).
                context.EnsureFrame();
                gpu.Streamer?.Settle(camera.Eye);
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
            return FramebufferCapture.Read(context, target, w, h);
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
            var psi = new ProcessStartInfo("git", "describe --always --dirty --abbrev=8") { RedirectStandardOutput = true,
                    RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory };
            using var p = Process.Start(psi);
            string text = p!.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return p.ExitCode == 0 && text.Length > 0 ? text : "unknown";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return "unknown"; }
    }
}
