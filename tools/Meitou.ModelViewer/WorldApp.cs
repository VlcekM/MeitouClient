using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

using Meitou.Rendering;
using Meitou.Rendering.Display;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan;
using Meitou.Rendering.Vulkan.Core;
using Meitou.Rendering.Vulkan.Upscalers;
using static Meitou.Rendering.WorldFrame;

namespace Meitou.ModelViewer;

static partial class WorldApp
{
    public static int Run(string[] args)
    {
        WorldOptions? options;
        try { options = WorldOptions.Parse(args); }
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
        var install = GameInstall.Locate();
        if (install is null)
        {
            Console.Error.WriteLine($"Kenshi install not found: set {GameInstall.EnvironmentVariable} or create {GameInstall.LocalConfigFile}.");
            return 1;
        }
        using var scene = Load(install, options);
        if (scene is null) return 1;
        if (options.Info) return 0;
        RenderJobs.RaiseRenderThread();
        var assets = new AssetLocator(install);
        return options.Screenshot is not null || options.FlyBenchmark > 0 ? Screenshot(install, scene, assets, options) : Interactive(install, scene, assets, options);
    }

    /// <summary>Streamline for DLSS, loaded before the Vulkan device when <c>--upscaler dlss</c> asks for it.</summary>
    static Streamline? streamline;

    static string RendererName(WorldOptions o) =>
        o.Post.Upscale.Kind == UpscalerKind.Off ? "Vulkan" : $"Vulkan + {o.Post.Upscale.Kind.ToString().ToUpperInvariant()}";

    static WindowOptions WindowFor(WorldOptions o) =>
        CameraCode.OnMonitor(WindowOptions.Default with
        {
            Size = new Vector2D<int>(o.Width, o.Height),
            Title = $"Meitou world ({RendererName(o)})",
            WindowState = WindowState.Maximized,
        }, o.Monitor);

    // Vulkan headless (no window at all).
    static unsafe int Screenshot(GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o)
    {
        using var display = new VulkanDisplay(null, vsync: false, streamline: o.Post.Upscale.Kind == UpscalerKind.Dlss);
        streamline = display.Streamline;
        try { return Screenshot(display.Gl, display.VkGl.Context, install, scene, assets, o); }
        finally { streamline = null; }
    }

    /// <summary>Ends the frame (submits it).</summary>
    static void EndFrame(IGl gl)
    {
        if (gl is VkGl vkGl) vkGl.EndFrame();
    }

    static unsafe int Screenshot(IGl gl, GpuContext context, GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o)
    {
        using var gpu = CreateGpu(gl, context, install, scene, assets, o, interactive: false);
        if (gl is VkGl vkGl && gpu.Post is { } vendorPost) vendorPost.UpscalerFactory = VendorUpscalers.Factory(vkGl, streamline);
        var (camera, render) = Setup(scene, o);
        if (gpu.Streamer is { } streamer)
        {
            var streamWatch = Stopwatch.StartNew();
            streamer.Settle(gpu.Anchor ?? camera.Eye);
            Console.WriteLine($"streamed  {streamer.Describe()} ({streamWatch.ElapsedMilliseconds} ms)");
        }
        if (gpu.Objects is { } objectRenderer)
        {
            var objectWatch = Stopwatch.StartNew();
            objectRenderer.Settle(gpu.Anchor ?? camera.Eye);
            Console.WriteLine($"objects   {objectRenderer.Describe()} ({objectWatch.ElapsedMilliseconds} ms)");
        }
        if (gpu.Foliage is { } foliageRenderer)
        {
            var foliageWatch = Stopwatch.StartNew();
            foliageRenderer.Settle(gpu.Anchor ?? camera.Eye);
            Console.WriteLine($"foliage   {foliageRenderer.Describe()} ({foliageWatch.ElapsedMilliseconds} ms)");
        }
        FinishLoading(gl);

        // Offscreen: the post-processing chain (HDR scene, resolve, effects) ends in a plain RGBA8 texture that is read back.
        int w = o.Width, h = o.Height;
        using var target = Meitou.Rendering.Gpu.Texture.Create(context, new TextureDesc(Silk.NET.Vulkan.Format.R8G8B8A8Unorm, w, h,
            Use: TextureUse.ColourTarget | TextureUse.TransferSrc | TextureUse.Sampled, Name: "offscreen picture"));
        gpu.Post!.Target = target;
        gpu.Post.InstantAdaptation = true;   // a still picture: the exposure settles at once
        Console.WriteLine($"post      {o.Post.Describe()}");
        if (o.FlyBenchmark > 0)
        {
            int flown = FlyBenchmark(gl, gpu, scene, camera, render, o, w, h);
            if (o.Screenshot is null) return flown;   // with --screenshot the picture is taken afterwards, back at the start (a check that unloaded data comes back right)
        }
        if (o.FlyToX is { } flyX && o.FlyToZ is { } flyZ)
        {
            // Streaming test: fly the camera to the point at 3x the interactive fast speed, 60 frames per second of
            // wall time, and report the render thread's cost per frame (draw + uploads) while data streams in.
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
                { Draw(gl, gpu, scene, camera, render, w, h, o.Hour, 0, o.FogDistance); EndFrame(gl); }
                gl.Finish();
                times.Add(frameWatch.Elapsed.TotalMilliseconds);
                if (frameWatch.Elapsed.TotalMilliseconds > 15 && Environment.GetEnvironmentVariable("MEITOU_STREAM_LOG") == "1")
                    Console.WriteLine($"slow frame {i}: {frameWatch.Elapsed.TotalMilliseconds:0.0} ms, streamer update {gpu.Streamer?.LastUpdateMs:0.0} ms [{gpu.Streamer?.LastSteps}], gen2 GCs {GC.CollectionCount(2)}, gen0 {GC.CollectionCount(0)}");
                int sleep = 16 - (int)frameWatch.ElapsedMilliseconds;
                if (sleep > 0) Thread.Sleep(sleep);
            }
            double first = times[0], worstAfterFirst = times.Count > 1 ? times.Skip(1).Max() : 0;
            times.Sort();
            Console.WriteLine($"flight    {frames} frames over {length:0} units: median {times[times.Count / 2]:0.0} ms, 99th {times[(int)(times.Count * 0.99)]:0.0} ms, worst {times[^1]:0.0} ms (first frame {first:0.0} ms, worst after it {worstAfterFirst:0.0} ms), " +
                $"{times.Count(x => x > 10)} frames over 10 ms; streaming {(gpu.Streamer?.Idle == true ? "idle" : $"{gpu.Streamer?.Pending} pending")}");
            gpu.Objects?.Settle(camera.Eye);
            gpu.Foliage?.Settle(camera.Eye);
        }

        void Step()
        {
            camera.Yaw += o.OrbitStep;
            if (o.SwayStep > 0 && gpu.Foliage is { } swaying) swaying.SwaySeconds = (swaying.SwaySeconds ?? 0) + o.SwayStep;
        }
        WorldFrame.DetailedStats = true;   // per-cascade and per-step times in the statistics below
        // A temporal upscaler converges over its jitter sequence first (a still camera: the history only sharpens).
        for (int i = 0; i < gpu.Post!.WarmupFrames; i++) { Step(); Draw(gl, gpu, scene, camera, render, w, h, o.Hour, 0, o.FogDistance); EndFrame(gl); }
        var drawWatch = Stopwatch.StartNew();
        { Step(); Draw(gl, gpu, scene, camera, render, w, h, o.Hour, 0, o.FogDistance); EndFrame(gl); }
        gl.Finish();
        Console.WriteLine($"drawn in {drawWatch.ElapsedMilliseconds} ms: {gpu.Terrain.DrawnChunks} chunks, {gpu.Terrain.DrawnTriangles:N0} terrain triangles" +
            (gpu.Objects is { } ob ? $", {ob.DrawnInstances} objects ({ob.DrawnTriangles:N0} triangles)" : ""));
        // Steady-state frame time (the first frame includes shader and texture warm-up).
        drawWatch.Restart();
        const int timedFrames = 10;
        gpu.Post?.Flush();
        gpu.Post?.TakeCosts(); // drop the warm-up frames
        for (int i = 0; i < timedFrames; i++) { Step(); Draw(gl, gpu, scene, camera, render, w, h, o.Hour, 0, o.FogDistance); EndFrame(gl); }
        gl.Finish();
        Console.WriteLine($"frame     {drawWatch.Elapsed.TotalMilliseconds / timedFrames:0.0} ms on average over {timedFrames} more frames");
        if (gpu.Objects is { } objectStats) Console.WriteLine($"objects   draw cpu {objectStats.LastDrawCpuMs:0.00} ms, {objectStats.DrawCalls} draw calls, {objectStats.DrawnInstances} instances, {objectStats.DrawnTriangles:N0} triangles");
        if (gpu.Foliage is { } foliageStats) { foliageStats.PollTimers(wait: true); Console.WriteLine($"foliage   update {foliageStats.LastUpdateMs:0.00} ms, draw cpu {foliageStats.LastDrawCpuMs:0.00} ms, gpu {foliageStats.GpuMs:0.00} ms, {foliageStats.DrawCalls} draw calls, {foliageStats.DrawnInstances} meshes, {foliageStats.DrawnBlades:N0} grass blades;{foliageStats.MainDetail}"); }
        if (gpu.Reflection is { Valid: true } reflection)
        {
            reflection.Poll(wait: true);
            Console.WriteLine($"reflect   {reflection.Width}x{reflection.Height}: cpu {reflection.CpuMs:0.00} ms, gpu {reflection.GpuMs:0.00} ms, {reflection.DrawnChunks} chunks, {reflection.DrawnTriangles:N0} terrain triangles; {reflection.SceneStats}");
        }
        if (gpu.Shadow is { } shadowStats)
        {
            shadowStats.Poll(wait: true);
            Console.WriteLine(shadowStats.Cascades is { } cs
                ? $"shadows   last frame cpu {shadowStats.CpuMs:0.00} ms, gpu {shadowStats.GpuMs:0.00} ms; {shadowStats.DescribeStats()}; {shadowStats.CasterStats}; cascades to {string.Join(", ", cs.Select(c => c.FarDepth.ToString("0", CultureInfo.InvariantCulture)))} (sizes {string.Join(", ", cs.Select(c => c.Size.X.ToString("0", CultureInfo.InvariantCulture)))}, texels {string.Join(", ", cs.Select(c => c.Texel.ToString("0.00", CultureInfo.InvariantCulture)))})"
                : "shadows   off this frame (sun below the horizon)");
        }

        gpu.Sky.Poll(wait: true);
        Console.WriteLine($"sky       {gpu.Sky.DescribeCost()}");
        gpu.Post!.Flush();
        Console.WriteLine($"post cost gpu ms/frame: {gpu.Post.DescribeCosts()}");
        if (gpu.Post.AutoExposure is { } band && gpu.Post.ReadExposure() is var (adapted, mean) && float.IsFinite(adapted))
            Console.WriteLine($"exposure  mean luminance {mean:0.000}, band {band.Min:0.###}..{band.Max:0.###}, adapted {adapted:0.000}: x{KenshiLighting.ExposureKey / adapted:0.000}");
        Console.WriteLine($"haze      {(gpu.Sky.KenshiHaze ? "kenshi" : "physical")}, eye {camera.Eye.X:0}, {camera.Eye.Y:0}, {camera.Eye.Z:0}, {gpu.Sky.EyeClearance:0} above the ground within {KenshiCamera.MaxDistance:0}: altitude weight {gpu.Sky.AltitudeWeight:0.###}, strength {gpu.Sky.HazeStrength:0.##}");
        if (o.ShowKeys && DebugOverlay.TryCreate(context) is { } keysOverlay)
        {
            keysOverlay.Target = target;
            keysOverlay.Visible = true;
            keysOverlay.Draw(w, h, "Keys   (F10 hides this)", DebugOverlay.KeyItems(WorldOptions.Usage));
            var settings = CreateSettingsPanel(keysOverlay, gpu, render, () => o.Hour, v => o.Hour = v);
            settings.Visible = true;
            settings.Draw(w, h);
            keysOverlay.Dispose();
        }
        FramebufferCapture.SavePng(context, target, o.Screenshot!, w, h);
        Console.WriteLine($"saved     {Path.GetFullPath(o.Screenshot!)}");
        if (Environment.GetEnvironmentVariable("MEITOU_SKY_BENCH") == "1")
        {
            // Sky cost, back to back after the picture is saved (the passes draw over the framebuffer): MEITOU_SKY_BENCH=1.
            var rotation = camera.View with { M41 = 0, M42 = 0, M43 = 0 };
            var (pass, table) = gpu.Sky.Benchmark(rotation * camera.Projection(w / (float)Math.Max(h, 1), 1, 1000));
            Console.WriteLine($"skybench  sky pass {pass:0.000} ms, sky-view table rebuild {table:0.000} ms ({w}x{h})");
        }
        return 0;
    }

    static int Interactive(GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o)
    {
        using var display = new VulkanDisplay(WindowFor(o), vsync: true, streamline: o.Post.Upscale.Kind == UpscalerKind.Dlss);
        streamline = display.Streamline;
        var window = display.Window!;
        var gl = display.Gl;
        Gpu? gpu = null;
        WorldCamera camera = null!;
        WorldRenderOptions render = null!;
        IKeyboard? keyboard = null;
        bool screenshotRequested = false;
        float hour = o.Hour;
        var clock = Stopwatch.StartNew();
        Vector2? lastMouse = null;
        MouseButton? dragging = null;
        DebugOverlay? overlay = null;
        SettingsPanel? panel = null;
        FrameProfiler? profiler = null;
        PassMeter? meter = null;
        bool statsVisible = false;
        var stats = new List<string>();
        var keyItems = DebugOverlay.KeyItems(WorldOptions.Usage);
        // F1 upwards: the Faithful / Meitou switches (Enhancements), in order.
        var switches = Enhancements.Create(o.Post, () => gpu?.Sky.HazeStrength ?? o.HazeStrength, v => { if (gpu is not null) gpu.Sky.HazeStrength = v; },
            () => gpu?.Shadow?.Meitou ?? o.MeitouShadows, v => { o.MeitouShadows = v; if (gpu?.Shadow is { } s) s.Meitou = v; },
            () => gpu?.Foliage?.MeitouRange ?? o.MeitouRange, v => { o.MeitouRange = v; if (gpu?.Foliage is { } f) f.MeitouRange = v; });

        {
            gpu = CreateGpu(gl, display.VkGl.Context, install, scene, assets, o, interactive: true);
            if (gl is VkGl vkGl && gpu.Post is { } vendorPost) vendorPost.UpscalerFactory = VendorUpscalers.Factory(vkGl, streamline);
            overlay = DebugOverlay.TryCreate(display.VkGl.Context);
            if (overlay is null) Console.WriteLine("keys      no monospace system font found: the F10 key list and F11 statistics are unavailable");
            if (overlay is not null) overlay.Visible = o.ShowKeys;
            (camera, render) = Setup(scene, o);
            if (overlay is not null) panel = CreateSettingsPanel(overlay, gpu, render, () => hour, v => hour = v);
            profiler = new FrameProfiler(display.VkGl.Context, gl is VkGl statsGl ? () => statsGl.Stats.GpuFrameMs : null);
            meter = PassMeter.TryCreate(gl);   // MEITOU_PASS_STATS=1: the frame cost breakdown, printed when the window closes
            var input = window.CreateInput();
            keyboard = input.Keyboards.FirstOrDefault();
            foreach (var kb in input.Keyboards) kb.KeyDown += (_, key, _) => OnKey(key);
            foreach (var mouse in input.Mice)
            {
                // Window to framebuffer pixels (they differ with display scaling); the panel is laid out in the latter.
                Vector2 Pixels(Vector2 p) => p * new Vector2(window.FramebufferSize.X / (float)Math.Max(window.Size.X, 1), window.FramebufferSize.Y / (float)Math.Max(window.Size.Y, 1));
                mouse.MouseDown += (m, b) =>
                {
                    if (b == MouseButton.Left && panel?.MouseDown(Pixels(m.Position)) == true) return;
                    if (panel?.Contains(Pixels(m.Position)) == true) return;
                    dragging = b; lastMouse = null;
                };
                mouse.MouseUp += (_, _) => { panel?.MouseUp(); dragging = null; };
                mouse.MouseMove += (_, p) =>
                {
                    if (panel?.MouseMove(Pixels(p)) == true) return;
                    if (dragging is { } b && lastMouse is { } last)
                    {
                        var d = p - last;
                        if (b == MouseButton.Left) camera.Orbit(d.X, d.Y);
                        else camera.Look(d.X, d.Y);
                    }
                    lastMouse = p;
                };
                mouse.Scroll += (_, wheel) => camera.Zoom(wheel.Y);
            }
            Console.WriteLine(WorldOptions.Usage[WorldOptions.Usage.IndexOf("Keys:", StringComparison.Ordinal)..]);
        }

        void Toggle(int index)
        {
            if (index >= switches.Count) return;
            var e = switches[index];
            e.IsMeitou = !e.IsMeitou;
            Console.WriteLine($"{e.Name,-18}{e.State}: {e.Note}");
        }
        void PostStatus() => Console.WriteLine($"post      {o.Post.Describe()}");

        // The state a key controls, for the key list ("on", "off", a mode or a value); null for actions.
        string? KeyState(string key)
        {
            static string OnOff(bool on) => on ? "on" : "off";
            return key switch
            {
                "T" => OnOff(render.Textures),
                "N" => OnOff(render.NormalMaps),
                "O" => OnOff(render.Objects),
                "F" => gpu?.Foliage is { } foliage ? OnOff(foliage.Enabled) : "n/a",
                "X" => render.Wireframe switch { 0 => "solid", 1 => "solid + lines", _ => "lines" },
                "V" => render.Debug switch { 0 => "off", 1 => "blend weights", 2 => "layer weights", _ => "plain shading" },
                "G" => OnOff(render.Water),
                "R" => OnOff(render.Reflections),
                "B" => gpu is null ? null : gpu.Sky.Physical ? "atmosphere" : "simple",
                "," => TimeText(hour),
                ['F', >= '1' and <= '9'] when key[1] - '1' < switches.Count => switches[key[1] - '1'].State,
                "-" => $"{o.Post.Exposure:0.00}",
                "F10" => "on",
                "F11" => OnOff(statsVisible),
                "F12" => profiler?.Showing.ToString().ToLowerInvariant(),
                "Tab" => panel is { Visible: true } ? "on" : "off",
                _ => null,
            };
        }

        void OnKey(Key key)
        {
            if (keyboard is not null && (keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight)) && key is Key.C or Key.V)
            {
                if (key == Key.C)
                {
                    string code = CameraCode.Encode(camera);
                    keyboard.ClipboardText = code;
                    Console.WriteLine($"camera    copied {code} ({CameraCode.Describe(camera)})");
                }
                else if (CameraCode.TryApply(keyboard.ClipboardText, camera)) Console.WriteLine($"camera    moved to {CameraCode.Describe(camera)}");
                else Console.WriteLine("camera    the clipboard holds no camera code (Ctrl+C in a viewer copies one)");
                return;
            }
            switch (key)
            {
                case Key.Escape: window.Close(); break;
                case >= Key.F1 and <= Key.F9: Toggle(key - Key.F1); break;
                case Key.Minus: o.Post.Exposure = MathF.Max(o.Post.Exposure / 1.1f, 0.05f); PostStatus(); break;
                case Key.Equal: o.Post.Exposure = MathF.Min(o.Post.Exposure * 1.1f, 20f); PostStatus(); break;
                case Key.T: render.Textures = !render.Textures; break;
                case Key.N: render.NormalMaps = !render.NormalMaps; break;
                case Key.O: render.Objects = !render.Objects; break;
                case Key.F when gpu?.Foliage is { } foliage: foliage.Enabled = !foliage.Enabled; Console.WriteLine(foliage.Enabled ? "foliage   on" : "foliage   off"); break;
                case Key.X: render.Wireframe = (render.Wireframe + 1) % 3; break;
                case Key.V: render.Debug = (render.Debug + 1) % 4; break;
                case Key.G: render.Water = !render.Water; break;
                case Key.R: render.Reflections = !render.Reflections; break;
                case Key.B when gpu is not null: gpu.Sky.Physical = !gpu.Sky.Physical; Console.WriteLine(gpu.Sky.Physical ? "sky      atmosphere" : "sky      simple colour model"); break;
                case Key.Comma: hour = (hour + 23) % 24; Console.WriteLine($"time {TimeText(hour)}"); break;
                case Key.Period: hour = (hour + 1) % 24; Console.WriteLine($"time {TimeText(hour)}"); break;
                case Key.H:
                    Console.WriteLine($"camera target {camera.Target.X:0}, {camera.Target.Y:0}, {camera.Target.Z:0} (zone {WorldLayout.ZoneOf(camera.Target.X, camera.Target.Z)}), " +
                        $"yaw {camera.Yaw * 180 / MathF.PI:0}, pitch {camera.Pitch * 180 / MathF.PI:0}, distance {camera.Distance:0}; " +
                        $"--at {camera.Target.X:0},{camera.Target.Z:0} --yaw {camera.Yaw * 180 / MathF.PI:0} --pitch {camera.Pitch * 180 / MathF.PI:0} --distance {camera.Distance:0}");
                    break;
                case Key.P: screenshotRequested = true; break;
                case Key.Tab when panel is not null: panel.Visible = !panel.Visible; break;
                case Key.F10 when overlay is not null: overlay.Visible = !overlay.Visible; break;
                case Key.F11 when overlay is not null: statsVisible = !statsVisible; break;
                case Key.F12 when profiler is not null && overlay is not null: profiler.Showing = (FrameProfiler.Mode)(((int)profiler.Showing + 1) % 3); break;
            }
        }

        double titleTimer = 0, cpuMs = 0, gpuMs = 0;
        int frames = 0, gpuSamples = 0, queryIndex = 0;
        // The GPU time of a frame: a native timestamp before and after it (QueryArena), read a frame ring later without waiting.
        var timers = new (QuerySlot Begin, QuerySlot End)[4];
        var timerPending = new bool[4];
        var frameWatch = new Stopwatch();
        window.Update += dt =>
        {
            SmokeTest.Check(window);
        };
        window.Update += dt =>
        {
            if (keyboard is null || camera is null) return;
            bool Down(Key k) => keyboard.IsKeyPressed(k);
            // Free camera: W/S along the view direction, A/D sideways, Q/E world down/up; speed grows with the height
            // above the ground (Shift ×4, Ctrl ×0.25).
            float speed = (float)dt * Math.Max(camera.EyeClearance, 30) * 1.5f *
                (Down(Key.ShiftLeft) || Down(Key.ShiftRight) ? 4f : 1f) * (Down(Key.ControlLeft) || Down(Key.ControlRight) ? 0.25f : 1f);
            float f = (Down(Key.W) ? 1 : 0) - (Down(Key.S) ? 1 : 0);
            float r = (Down(Key.D) ? 1 : 0) - (Down(Key.A) ? 1 : 0);
            float u = (Down(Key.E) ? 1 : 0) - (Down(Key.Q) ? 1 : 0);
            if (f != 0 || r != 0 || u != 0) camera.FlyFree(f * speed, r * speed, u * speed);
            titleTimer += dt;
            if (titleTimer > 0.25 && gpu is not null)
            {
                var t = camera.Eye;
                // fps is capped by vsync; cpu is the time to record a frame, gpu the time the GPU spent on it (timer
                // queries), so they show the real cost under the cap.
                string gpuText = gpuSamples > 0 ? $"{gpuMs / gpuSamples:0.00}" : "-";
                stats.Clear();
                stats.Add($"{frames / titleTimer:0} fps (vsync), cpu {cpuMs / Math.Max(frames, 1):0.00} ms, gpu {gpuText} ms");
                var (vramUsed, vramBudget) = display.VkGl.Context.Device.VideoMemory();
                var alloc = display.VkGl.Context.Device.Allocator;
                stats.Add($"vram        {vramUsed / 1073741824.0:0.00} of {vramBudget / 1073741824.0:0.0} GB; our blocks {alloc.TotalAllocatedBytes / 1073741824.0:0.00} GB, {alloc.TotalUsedBytes / 1073741824.0:0.00} used");
                // The largest owners (GpuAllocator.Breakdown: names without their numbers), to see what fills the VRAM.
                foreach (var (name, count, bytes, _) in alloc.Breakdown().Take(8))
                    stats.Add($"  {name,-26} {bytes / 1048576.0,7:0} MB  x{count}");
                if (gpu.Reflection is { Valid: true } refl && render.Reflections) stats.Add($"reflection  cpu {refl.CpuMs:0.00} ms, gpu {refl.GpuMs:0.00} ms");
                gpu.Sky.Poll();
                stats.Add(gpu.Sky.Physical ? $"sky         cpu {gpu.Sky.PrepareMs:0.00} ms, gpu {gpu.Sky.GpuMs:0.00} ms" : "sky         simple");
                if (gpu.Post is { } post) stats.Add($"post gpu    {post.DescribeCosts()}");
                if (gpu.Post is { } hazy && (hazy.HeatHazeAmount > 0 || gpu.HeatHazeTarget > 0))
                    stats.Add($"heat haze   {hazy.HeatHazeAmount:0.00} (target {gpu.HeatHazeTarget:0.00}, weather {gpu.Sky.Weather.Name})" + (hazy.HeatHazeRuns ? "" : hazy.HasHeatHaze ? ", off" : ", no textures"));
                stats.Add($"camera      {t.X:0}, {t.Y:0}, {t.Z:0}, zone {WorldLayout.ZoneOf(t.X, t.Z)}");
                stats.Add($"terrain     {gpu.Terrain.DrawnChunks} chunks, {gpu.Terrain.DrawnTriangles / 1000}k tris" + (gpu.Streamer is { Pending: > 0 } st ? $", loading {st.Pending}" : ""));
                if (gpu.Objects is { } ob && render.Objects)
                    stats.Add($"objects     {ob.DrawnInstances}, {ob.DrawCalls} calls, draw cpu {ob.LastDrawCpuMs:0.00} ms" + (ob.Pending > 0 ? $", loading {ob.Pending}" : ""));
                if (gpu.Foliage is { Enabled: true } fo)
                    stats.Add($"foliage     {fo.DrawnInstances} + {fo.DrawnBlades / 1000}k grass, {fo.DrawCalls} calls, cpu {fo.LastDrawCpuMs:0.00} ms, gpu {fo.GpuMs:0.00} ms" + (fo.Pending > 0 ? $", loading {fo.Pending}" : ""));
                stats.Add($"resident    {((gpu.Objects?.ResidentBytes ?? 0) + (gpu.Foliage?.ResidentBytes ?? 0)) / 1048576} MB");
                if (gpu.Foliage is { } fr) stats.Add($"  foliage   {fr.ResidentDescription}; {fr.Describe()}");
                if (gpu.Objects is { } orr) stats.Add($"  objects   {orr.ResidentDescription}");
                titleTimer = 0;
                frames = 0;
                cpuMs = gpuMs = 0;
                gpuSamples = 0;
            }
        };
        window.Render += _ =>
        {
            if (gpu is null) return;
            var size = window.FramebufferSize;
            if (!display.BeginFrame(size.X, size.Y)) return; // minimized, or not yet shown at its maximized size
            var context = display.VkGl.Context;
            // The chain and the overlays draw into the window's backbuffer (made again when the size changes).
            var backbuffer = display.Backbuffer!;
            if (gpu.Post is { } windowPost) windowPost.Target = backbuffer;
            if (overlay is not null) overlay.Target = backbuffer;
            var arena = context.Frame.Timestamps;
            // Collect finished GPU timings from earlier frames without waiting for them (one that never comes, its frame long gone, is dropped).
            for (int i = 0; i < timers.Length; i++)
            {
                if (!timerPending[i]) continue;
                if (arena.TryRead(timers[i].Begin, out ulong begin) && arena.TryRead(timers[i].End, out ulong end))
                {
                    gpuMs += (end - begin) / 1e6;
                    gpuSamples++;
                    timerPending[i] = false;
                }
                else if (context.Frame.Number - timers[i].End.Frame > 8) timerPending[i] = false;
            }
            bool timing = !timerPending[queryIndex];
            if (timing)
            {
                var begin = arena.Allocate();
                if (begin.IsValid) { context.Interleave(cmd => cmd.Timestamp(arena, begin)); timers[queryIndex].Begin = begin; }
                else timing = false;
            }
            frameWatch.Restart();
            profiler?.BeginFrame();
            Draw(gl, gpu, scene, camera, render, size.X, size.Y, hour, (float)clock.Elapsed.TotalSeconds / 600f, o.FogDistance);
            cpuMs += frameWatch.Elapsed.TotalMilliseconds;
            if (timing)
            {
                var end = arena.Allocate();
                if (end.IsValid)
                {
                    context.Interleave(cmd => cmd.Timestamp(arena, end));
                    timers[queryIndex].End = end;
                    timerPending[queryIndex] = true;
                }
            }
            queryIndex = (queryIndex + 1) % timers.Length;
            frames++;
            // The picture is read after the present (framebuffer 0 stays intact until the next frame); the frame that is saved is drawn
            // without the overlay and the panel, so saved pictures never show them.
            bool shot = screenshotRequested;
            screenshotRequested = false;
            // The statistics at the top left, the key list below them.
            float panelsBottom = !shot && statsVisible && overlay is not null && stats.Count > 0
                ? overlay.Panel(size.X, size.Y, $"Meitou world ({RendererName(o)})   (F11 hides this)", stats) : 0;
            if (!shot && overlay is { Visible: true })
            {
                // Each item with its current state, aligned in a column.
                int width = keyItems.Max(i => i.Length) + 2;
                var lines = keyItems.Select(item => KeyState(item.Split(' ')[0]) is { } state ? item.PadRight(width) + state : item).ToList();
                overlay.Draw(size.X, size.Y, "Keys   (F10 hides this)", lines, panelsBottom);
            }
            if (!shot && overlay is not null) profiler?.Draw(overlay, size.X, size.Y);
            // Last, over the statistics and the profiler: the settings sit at the top left.
            if (!shot) panel?.Draw(size.X, size.Y);
            StageClock.Phase("overlays");
            display.Present();
            profiler?.EndFrame();
            SmokeTest.Frame();
            if (shot)
            {
                // Into C:\Temp (the user's screenshot folder), never the working directory (which may be the repo).
                var file = Path.Combine(Directory.CreateDirectory(@"C:\Temp").FullName, $"meitou-world-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                FramebufferCapture.SavePng(display.VkGl.Context, backbuffer, file, size.X, size.Y);
                Console.WriteLine($"saved {Path.GetFullPath(file)}");
            }
        };
        window.Closing += () => { if (meter is not null && gl is VkGl vkStats) { meter.Report(Console.Out); meter.ReportPhases(Console.Out, vkStats.Stats.Draws); meter.Dispose(); } profiler?.Dispose(); overlay?.Dispose(); gpu?.Dispose(); };
        window.Run();
        return 0;
    }
}
