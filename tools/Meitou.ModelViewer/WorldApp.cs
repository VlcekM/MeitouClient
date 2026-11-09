using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;
using Meitou.Data.World;
using Silk.NET.Maths;
using Silk.NET.Windowing;

using Meitou.Game.Rendering.Bench;
using Meitou.Game.Rendering.Display;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;
using Meitou.Game.Rendering.Upscalers;
using static Meitou.Game.Rendering.Frame.WorldFrame;

namespace Meitou.ModelViewer;

/// <summary>
/// The world viewer (<c>--world</c>, <c>--view</c>, <c>--bench-compare</c>): parses the options, loads the scene and runs the interactive viewer, or
/// headless the screenshot, the benchmarks and the A/B runs.
/// </summary>
static partial class WorldApp
{
    #region Run and window

    /// <summary>
    /// Streamline for DLSS, loaded before the Vulkan device when <c>--upscaler dlss</c> asks for it.
    /// </summary>
    static Streamline? s_streamline;

    /// <summary>
    /// NGX for DLSS, with <see cref="s_streamline"/>.
    /// </summary>
    static Ngx? s_ngx;

    /// <summary>
    /// Parses the options (exit code 2 for bad ones; <c>--bench-compare</c> compares two results and ends), loads the install and the scene and runs
    /// headless (a screenshot, a benchmark or an A/B run) or interactive; returns the exit code.
    /// </summary>
    public static int Run(string[] args)
    {
        WorldOptions? options;
        args = TerrainSandboxArgs(args);
        try
        {
            options = WorldOptions.Parse(args);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or IndexOutOfRangeException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        if (options is null)
        {
            Console.WriteLine(WorldOptions.Usage);
            return 2;
        }
        if (options.BenchCompare is { } compare)
        {
            try
            {
                BenchResult.Compare(compare.A, compare.B, Console.Out);
                return 0;
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException)
            {
                Console.Error.WriteLine($"--bench-compare: {e.Message}");
                return 2;
            }
        }
        var install = GameInstall.LocateOrAsk();
        if (install is null)
        {
            Console.Error.WriteLine($"Kenshi install not found: set {GameInstall.EnvironmentVariable} or create {GameInstall.LocalConfigFile}.");
            return 1;
        }
        using var scene = Load(install, options);
        if (scene is null) return 1;
        RenderJobs.RaiseRenderThread();
        var assets = new AssetLocator(install);
        return options.Screenshot is not null || options.FlyBenchmark > 0 || options.BenchFrames > 0 || options.Ab is not null
                ? Screenshot(install, scene, assets, options) : Interactive(install, scene, assets, options);
    }

    /// <summary>
    /// The renderer the window title and the statistics name: Vulkan, with the upscaler when one is on.
    /// </summary>
    internal static string RendererName(WorldOptions o) =>
        o.Post.Upscale.Kind == UpscalerKind.Off ? "Vulkan" : $"Vulkan + {o.Post.Upscale.Kind.ToString().ToUpperInvariant()}";

    /// <summary>
    /// The viewer's window: the options' size, maximized, on the chosen monitor.
    /// </summary>
    static WindowOptions WindowFor(WorldOptions o) =>
        CameraCode.OnMonitor(WindowOptions.Default with
        {
            Size = new Vector2D<int>(o.Width, o.Height),
            Title = $"Meitou world ({RendererName(o)})",
            WindowState = WindowState.Maximized,
        }, o.Monitor);

    /// <summary>
    /// The interactive viewer: the window and its display, then the session that runs it (<see cref="InteractiveSession"/>).
    /// </summary>
    static int Interactive(GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o)
    {
        VulkanDisplay.RayTracing = o.WantsRayTracing;
        using var display = new VulkanDisplay(WindowFor(o), vsync: o.VSync, dlss: o.Post.Upscale.Kind == UpscalerKind.Dlss);
        s_streamline = display.Streamline;
        s_ngx = display.Ngx;
        var window = display.Window!;
        if (!o.Windowed) BorderlessFullScreen.Toggle(window);
        return new InteractiveSession(install, scene, assets, o, display, window, s_streamline, s_ngx).Run();
    }

    /// <summary>
    /// Ends the frame (submits it).
    /// </summary>
    internal static void EndFrame(GpuContext context) => context.EndFrame();

    #endregion

    #region Screenshots

    /// <summary>
    /// Vulkan headless (no window at all).
    /// </summary>
    static unsafe int Screenshot(GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o)
    {
        VulkanDisplay.TriangleMeasurements = o.BenchTris;
        VulkanDisplay.RayTracing = o.WantsRayTracing;
        using var display = new VulkanDisplay(null, vsync: false, dlss: o.Post.Upscale.Kind == UpscalerKind.Dlss);
        s_streamline = display.Streamline;
        s_ngx = display.Ngx;
        using var watch = VramWatch.Start(display.Context.Device);
        try
        {
            return Screenshot(display, install, scene, assets, o);
        }
        finally
        {
            s_streamline = null;
            s_ngx = null;
        }
    }

    /// <summary>
    /// The headless run on <paramref name="display"/>: builds the renderer, then runs the frame benchmark or A/B session, or the fly benchmark, or
    /// flies, settles and draws the screenshot (with the keys overlay when asked); returns the exit code.
    /// </summary>
    static unsafe int Screenshot(VulkanDisplay display, GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o)
    {
        var context = display.Context;
        using var gpu = CreateGpu(context, install, scene, assets, o, interactive: false);
        if (gpu.Post is { } vendorPost)
            vendorPost.UpscalerFactory = VendorUpscalers.Factory(display.Context, s_streamline, s_ngx);
        var (camera, render) = Setup(scene, o);
        Console.WriteLine($"camera    {CameraCode.Encode(camera)} ({CameraCode.Describe(camera)}; --camera-code puts a viewer here)");
        SettleForPicture(gpu, camera, o);
        FinishLoading(context);

        // Offscreen: the post-processing chain (HDR scene, resolve, effects) ends in a plain RGBA8 texture that is read back.
        int w = o.Width, h = o.Height;
        using var target = context.CreateTexture(new TextureDesc(InternalFormat.Rgba8, w, h,
            Use: TextureUse.ColourTarget | TextureUse.TransferSrc | TextureUse.TransferDst | TextureUse.Sampled, Name: "offscreen picture"));
        gpu.Post!.Target = target;
        gpu.Post.InstantAdaptation = true;   // a still picture: the exposure settles at once
        Console.WriteLine($"post      {o.Post.Describe()}");
        if (o.BenchFrames > 0 || o.Ab is not null)
            return new BenchSession(display, gpu, scene, camera, render, o, w, h).Run();
        if (o.FlyBenchmark > 0)
        {
            int flown = new FlyBenchmarkSession(display, gpu, scene, camera, render, o, w, h).Run();
            // With --screenshot the picture is taken afterwards, back at the start (a check that unloaded data comes back right).
            if (o.Screenshot is null) return flown;
        }
        if (o.FlyToX is { } flyX && o.FlyToZ is { } flyZ) FlyTo(gpu, scene, camera, render, o, w, h, context, flyX, flyZ);

        FieldTour(gpu, scene, camera, render, o, w, h, context, target);
        // With --orbit-step and weather particles the frame clock runs at 1/60 s a frame (the draw's time unit is 600 s), so they move as in play.
        int orbitFrames = 0;
        float Clock() => o.OrbitStep != 0 && gpu.Particles is { Groups.Count: > 0 } ? (float)(orbitFrames / 60.0 / 600) : 0;
        void Step()
        {
            orbitFrames++;
            camera.Yaw += o.OrbitStep;
            if (o.SwayStep > 0 && gpu.Foliage is { } swaying)
                swaying.SwaySeconds = (swaying.SwaySeconds ?? 0) + o.SwayStep;
        }
        DetailedStats = true;   // per-cascade and per-step times in the statistics below
        // A temporal upscaler converges over its jitter sequence first (a still camera: the history only sharpens).
        for (int i = 0; i < gpu.Post!.WarmupFrames; i++)
        {
            Step();
            Draw(gpu, scene, camera, render, w, h, o.Hour, Clock(), o.FogDistance);
            EndFrame(context);
        }
        var drawWatch = Stopwatch.StartNew();
        {
            Step();
            Draw(gpu, scene, camera, render, w, h, o.Hour, Clock(), o.FogDistance);
            EndFrame(context);
        }
        context.Finish();
        Console.WriteLine($"drawn in {drawWatch.ElapsedMilliseconds} ms: "
            + $"{gpu.Terrain.DrawnChunks} chunks, {gpu.Terrain.DrawnTriangles:N0} terrain triangles" +
            (gpu.Objects is { } ob ? $", {ob.DrawnInstances} objects ({ob.DrawnTriangles:N0} triangles)" : ""));
        // Steady-state frame time (the first frame includes shader and texture warm-up).
        drawWatch.Restart();
        const int timedFrames = 10;
        gpu.Post?.Flush();
        gpu.Post?.TakeCosts(); // drop the warm-up frames
        for (int i = 0; i < timedFrames; i++)
        {
            Step();
            Draw(gpu, scene, camera, render, w, h, o.Hour, 0, o.FogDistance);
            EndFrame(context);
        }
        context.Finish();
        Console.WriteLine($"frame     {drawWatch.Elapsed.TotalMilliseconds / timedFrames:0.0} ms on average over {timedFrames} more frames");
        PrintFrameCosts(gpu, camera);
        if (o.ShowKeys) DrawKeysOverlay(context, assets, gpu, render, o, target, w, h);
        FramebufferCapture.SavePng(context, target, o.Screenshot!, w, h);
        Console.WriteLine($"saved     {Path.GetFullPath(o.Screenshot!)}");
        return 0;
    }

    /// <summary>
    /// Settles the streamers at the camera before the picture: the terrain, the objects (told the view first) and the foliage, each with
    /// its time.
    /// </summary>
    static void SettleForPicture(WorldGpu gpu, WorldCamera camera, WorldOptions o)
    {
        // Before the first settle, so the textures load knowing the picture (the display size, the bias without the render scale: the same
        // level).
        var up = o.Post.Upscale;
        gpu.Objects?.SetView(o.Width, o.Height, camera.FieldOfView, up.Temporal ? (up.Kind == UpscalerKind.Taa ? -0.5f : -1f) : 0f);
        if (gpu.Streamer is { } streamer)
        {
            var streamWatch = Stopwatch.StartNew();
            streamer.Settle(camera.Eye);
            Console.WriteLine($"streamed  {streamer.Describe()} ({streamWatch.ElapsedMilliseconds} ms)");
        }
        if (gpu.Objects is { } objectRenderer)
        {
            var objectWatch = Stopwatch.StartNew();
            objectRenderer.Settle(camera.Eye);
            Console.WriteLine($"objects   {objectRenderer.Describe()} ({objectWatch.ElapsedMilliseconds} ms)");
        }
        if (gpu.Foliage is { } foliageRenderer)
        {
            var foliageWatch = Stopwatch.StartNew();
            foliageRenderer.Settle(camera.Eye);
            Console.WriteLine($"foliage   {foliageRenderer.Describe()} ({foliageWatch.ElapsedMilliseconds} ms)");
            Console.WriteLine(foliageRenderer.LayoutCache is { } layoutCache
                    ? $"foliage   {layoutCache.Stats.Describe()} (key {layoutCache.KeyName})" : "foliage   layout cache off (--no-load-cache)");
            Console.WriteLine($"foliage   {foliageRenderer.SettleTimeline}");
        }
    }

    /// <summary>
    /// Streaming test: fly the camera to the point at 3x the interactive fast speed, 60 frames per second of wall time, and report the render
    /// thread's cost per frame (draw + uploads) while data streams in.
    /// </summary>
    static void FlyTo(WorldGpu gpu, WorldScene scene, WorldCamera camera, WorldRenderOptions render, WorldOptions o, int w, int h, GpuContext context,
        double flyX, double flyZ)
    {
        var start = camera.Target;
        var end = new Vector3((float)flyX, 0, (float)flyZ);
        float length = Vector2.Distance(new Vector2(start.X, start.Z), new Vector2(end.X, end.Z));
        float perFrame = 3f * 0.8f * Math.Max(camera.Distance, 50) / 60f;
        int frames = Math.Max((int)(length / perFrame), 1);
        var times = new List<double>();
        var frameWatch = new Stopwatch();
        for (int i = 1; i <= frames; i++)
        {
            float t = i / (float)frames;
            var p = Vector3.Lerp(start, end, t);
            camera.Target = new Vector3(p.X, gpu.Terrain.HeightAt(p.X, p.Z), p.Z);
            frameWatch.Restart();
            {
                Draw(gpu, scene, camera, render, w, h, o.Hour, 0, o.FogDistance);
                EndFrame(context);
            }
            context.Finish();
            times.Add(frameWatch.Elapsed.TotalMilliseconds);
            if (frameWatch.Elapsed.TotalMilliseconds > 15 && Environment.GetEnvironmentVariable("MEITOU_STREAM_LOG") == "1")
            {
                Console.WriteLine($"slow frame {i}: {frameWatch.Elapsed.TotalMilliseconds:0.0} ms, "
                    + $"streamer update {gpu.Streamer?.LastUpdateMs:0.0} ms [{gpu.Streamer?.LastSteps}], gen2 GCs {GC.CollectionCount(2)}, "
                    + $"gen0 {GC.CollectionCount(0)}");
            }
            int sleep = 16 - (int)frameWatch.ElapsedMilliseconds;
            if (sleep > 0) Thread.Sleep(sleep);
        }
        double first = times[0], worstAfterFirst = times.Count > 1 ? times.Skip(1).Max() : 0;
        times.Sort();
        Console.WriteLine($"flight    {frames} frames over {length:0} units: median {times[times.Count / 2]:0.0} ms, "
            + $"99th {times[(int)(times.Count * 0.99)]:0.0} ms, worst {times[^1]:0.0} ms (first frame {first:0.0} ms, "
            + $"worst after it {worstAfterFirst:0.0} ms), " +
            $"{times.Count(x => x > 10)} frames over 10 ms; streaming {(gpu.Streamer?.Idle == true ? "idle" : $"{gpu.Streamer?.Pending} pending")}");
        gpu.Objects?.Settle(camera.Eye);
        gpu.Foliage?.Settle(camera.Eye);
    }

    /// <summary>
    /// The last timed frames' costs and counts per renderer: objects, foliage, reflection, shadows, sky, post, exposure, particles, haze, fog,
    /// global illumination, occlusion and shafts.
    /// </summary>
    static void PrintFrameCosts(WorldGpu gpu, WorldCamera camera)
    {
        if (gpu.Objects is { } objectStats)
        {
            Console.WriteLine($"objects   draw cpu {objectStats.LastDrawCpuMs:0.00} ms, {objectStats.DrawCalls} draw calls, "
                + $"{objectStats.DrawnInstances} instances, {objectStats.DrawnTriangles:N0} triangles");
        }
        if (gpu.Foliage is { } foliageStats)
        {
            foliageStats.PollTimers(wait: true);
            Console.WriteLine($"foliage   update {foliageStats.LastUpdateMs:0.00} ms, draw cpu {foliageStats.LastDrawCpuMs:0.00} ms, "
                + $"gpu {foliageStats.GpuMs:0.00} ms, {foliageStats.DrawCalls} draw calls, {foliageStats.DrawnInstances} meshes, "
                + $"{foliageStats.DrawnBlades:N0} grass blades;{foliageStats.MainDetail}");
        }
        if (gpu.Reflection is { Valid: true } reflection)
        {
            reflection.Poll(wait: true);
            Console.WriteLine($"reflect   {reflection.Width}x{reflection.Height}: cpu {reflection.CpuMs:0.00} ms, "
                + $"gpu {reflection.GpuMs:0.00} ms, {reflection.DrawnChunks} chunks, {reflection.DrawnTriangles:N0} terrain triangles; "
                + $"{reflection.SceneStats}");
        }
        if (gpu.Shadow is { } shadowStats)
        {
            shadowStats.Poll(wait: true);
            Console.WriteLine(shadowStats.Cascades is { } cs
                ? $"shadows   last frame cpu {shadowStats.CpuMs:0.00} ms, gpu {shadowStats.GpuMs:0.00} ms; {shadowStats.DescribeStats()}; "
                + $"{shadowStats.CasterStats}; "
                + $"cascades to {string.Join(", ", cs.Select(c => c.FarDepth.ToString("0", CultureInfo.InvariantCulture)))} (sizes "
                + $"{string.Join(", ", cs.Select(c => c.Size.X.ToString("0", CultureInfo.InvariantCulture)))}, "
                + $"texels {string.Join(", ", cs.Select(c => c.Texel.ToString("0.00", CultureInfo.InvariantCulture)))})"
                : "shadows   off this frame (sun below the horizon)");
        }

        gpu.Sky.Poll(wait: true);
        Console.WriteLine($"sky       {gpu.Sky.DescribeCost()}");
        gpu.Post!.Flush();
        Console.WriteLine($"post cost gpu ms/frame: {gpu.Post.DescribeCosts()}");
        if (gpu.Post.AutoExposure is { } band && gpu.Post.ReadExposure() is var (adapted, mean) && float.IsFinite(adapted))
        {
            Console.WriteLine($"exposure  mean luminance {mean:0.000}, band {band.Min:0.###}..{band.Max:0.###}, "
                + $"adapted {adapted:0.000}: x{KenshiLighting.ExposureKey / adapted:0.000}");
        }
        if (gpu.Particles is { } particles)
        {
            Console.WriteLine($"particles {particles.ParticleCount} alive in {particles.Groups.Count} groups and "
                + $"{particles.PlacerGroups.Count} placer groups, {particles.ActiveUnits} units simulated, "
                + $"{particles.DrawnUnits} drawn ({particles.DrawnParticles} particles) in the last frame; "
                + $"simulation {particles.SimulationMilliseconds:0.00} ms, main thread waited {particles.WaitMilliseconds:0.00} ms; "
                + $"mean per frame: simulation {particles.MeanMilliseconds.Simulation:0.00} ms, "
                + $"wait {particles.MeanMilliseconds.Wait:0.00} ms; main thread per frame: "
                + $"update {particles.MeanMainThreadMilliseconds.Update:0.00} ms, draw {particles.MeanMainThreadMilliseconds.Draw:0.00} ms "
                + "(with the wait)");
        }
        Console.WriteLine($"haze      eye {camera.Eye.X:0}, {camera.Eye.Y:0}, {camera.Eye.Z:0}, {gpu.Sky.EyeClearance:0} above the ground "
            + $"within {KenshiCamera.MaxDistance:0}: altitude weight {gpu.Sky.AltitudeWeight:0.###}, strength {gpu.Sky.HazeStrength:0.##}");
        if (gpu.FogVolumes is { } fogVolumes)
        {
            Console.WriteLine($"fog vols  {fogVolumes.Active.Count} in view ({fogVolumes.EffectVolumesDrawn} of {fogVolumes.EffectVolumes} "
                + $"weather effect volumes), {fogVolumes.UsedData} of {FogVolumeShaders.MaxData} "
                + $"vec4s{(fogVolumes.Dropped > 0 ? $", {fogVolumes.Dropped} left out" : "")}, farthest first: "
                + $"{(fogVolumes.Active.Count == 0 ? "none" : string.Join("; ", fogVolumes.Active))}");
        }
        if (gpu.FogVolumes is { } fogCulled)
            Console.WriteLine($"fog cull  {(fogCulled.DescribeCull() ?? "off for this view")}");
        if (gpu.Gi is { } gi) Console.WriteLine($"gi         {gi.Describe()}");
        if (gpu.Probes is { } probeInfo) Console.WriteLine($"gi probes  {probeInfo.Describe()}");
        if (gpu.Post is { } occlusionPost && gpu.Foliage is { } occlusionFoliage)
        {
            string occlusion = occlusionPost.OcclusionCull
                ? $"depth pyramid {occlusionPost.Hiz?.Describe ?? "none"}: "
                    + $"{occlusionFoliage.OccludedInstances} foliage instances left out of the main view"
                : "off";
            Console.WriteLine($"occlusion  {occlusion}");
        }
        if (gpu.Post is { } shaftsPost) Console.WriteLine($"shafts     {shaftsPost.ShaftsDescribe}");
    }

    /// <summary>
    /// <c>--show-keys</c>: the key list and the settings panel drawn over the picture.
    /// </summary>
    static void DrawKeysOverlay(GpuContext context, AssetLocator assets, WorldGpu gpu, WorldRenderOptions render, WorldOptions o, GpuTexture target,
        int w, int h)
    {
        if (DebugOverlay.TryCreate(context, assets.Find) is not { } keysOverlay) return;
        keysOverlay.Target = target;
        keysOverlay.Visible = true;
        keysOverlay.Draw(w, h, "Keys   (F10 hides this)", DebugOverlay.KeyItems(WorldOptions.Usage));
        var settings = CreateSettingsPanel(keysOverlay, gpu, render, () => o.Hour, v => o.Hour = v, switches: WorldOptions.Switches(o));
        settings.Visible = true;
        settings.Draw(w, h);
        keysOverlay.Dispose();
    }

    #endregion
}
