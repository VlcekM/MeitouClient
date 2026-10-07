using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Engine;
using Meitou.Engine.Cameras;
using Meitou.Engine.Input;
using Meitou.Engine.Time;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Display;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using EngineKey = Meitou.Engine.Input.Key;
using EngineButton = Meitou.Engine.Input.MouseButton;
using SilkKey = Silk.NET.Input.Key;
using SilkButton = Silk.NET.Input.MouseButton;

namespace Meitou.Game;

/// <summary>Game-only options; everything else is the world options shared with the viewer (<see cref="WorldOptions"/>).</summary>
sealed class GameOptions
{
    public int? FpsLimit, TickRate, SimThreads;
    public ulong Seed;
    public bool? VSync;
    /// <summary>With <c>--screenshot</c>: simulation ticks run before the picture (with no input).</summary>
    public int Ticks;
    public bool FreeCamera;
    /// <summary>Interactive run that closes itself after this many seconds and prints the frame rate (an unattended smoke test).</summary>
    public double? QuitAfter;

    public const string Usage = """
        meitou [where] [options]     boots into the world with the Kenshi camera (default --town "The Hub")
          --fps-limit <n>            frame limit when vsync is off (default 240 from meitou.user.json; 0 = unlimited)
          --vsync / --no-vsync       vsync (default off)
          --tick-rate <hz>           control ticks per second: input actions and camera, in real time (default 30; the world ticks 30 per game second)
          --free-camera              start in the free camera (; toggles)
          --sim-threads <n>          worker threads of the simulation (default: half the cores, 1 to 8; the result never depends on it)
          --seed <n>                 the world seed (default 0)
          --ticks <n>                with --screenshot: run n simulation ticks before the picture
          --quit-after <s>           close after s seconds and print the frame rate (smoke test)
          --yaw/--pitch/--distance   start view: heading, pitch above the horizon and boom (Kenshi: 30 degrees, boom 150; clamped to 10..2000)
          world options as meitou-viewer --world: --at, --zone, --town, --radius, --time, --screenshot, --size, --no-foliage, ...
        Keys: W/A/S/D move, Q/E or Left/Right rotate, Up/Down pitch, wheel or PageUp/PageDown zoom, right or middle drag orbit,
          ; free camera (R/F up/down), Space pause, F2/F3/F4 speed 1x/2x/5x (. / , step), Tab settings, F12 screenshot, Esc quit.
        Settings (frame limit, vsync, tick rate, the Tab sliders, key bindings) are kept in meitou.user.json.
        """;

    /// <summary>Takes the game's own options out of <paramref name="args"/> and returns the rest.</summary>
    public static (GameOptions, string[]) Split(string[] args)
    {
        var g = new GameOptions();
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            switch (a)
            {
                case "--fps-limit": g.FpsLimit = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--tick-rate": g.TickRate = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--sim-threads": g.SimThreads = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--seed": g.Seed = ulong.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--vsync": g.VSync = true; break;
                case "--no-vsync": g.VSync = false; break;
                case "--ticks": g.Ticks = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--free-camera": g.FreeCamera = true; break;
                case "--quit-after": g.QuitAfter = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                default: rest.Add(a); break;
            }
        }
        return (g, rest.ToArray());
    }
}

static class Program
{
    static int Main(string[] args)
    {
        RenderJobs.RaiseRenderThread();
        GameOptions game;
        WorldOptions? world;
        try
        {
            (game, var rest) = GameOptions.Split(args);
            world = WorldOptions.Parse(rest);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or IndexOutOfRangeException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        if (world is null)
        {
            Console.WriteLine(GameOptions.Usage);
            return 2;
        }
        if (world.X is null && world.Zone is null && world.Town is null) world.Town = "The Hub";
        var install = GameInstall.Locate();
        if (install is null)
        {
            Console.Error.WriteLine($"Kenshi install not found: set {GameInstall.EnvironmentVariable} or create {GameInstall.LocalConfigFile}.");
            return 1;
        }
        var config = UserConfig.Load();
        using var scene = WorldFrame.Load(install, world);
        if (scene is null) return 1;
        if (world.Info) return 0;
        return new GameHost(install, scene, new AssetLocator(install), world, game, config).Run();
    }
}

/// <summary>
/// The game loop: a real-time control tick (<see cref="FixedStepClock"/>, 30 Hz by default) runs the input actions, the camera rig
/// and pause/speed; a game-time simulation tick (<see cref="SimulationClock"/>) advances the game clock; each displayed frame draws the camera interpolated between the last two ticks at the display rate.
/// </summary>
sealed class GameHost(GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o, GameOptions g, UserConfig config)
{
    WorldSession session = null!;   // disposed with the host (Run)
    WorldFrame.Gpu gpu = null!;
    WorldCamera camera = null!;
    WorldRenderOptions render = null!;
    readonly Stopwatch realTime = Stopwatch.StartNew();

    public int Run()
    {
        try { return o.Screenshot is not null ? Screenshot() : Interactive(); }
        finally { session?.Dispose(); }
    }

    void Boot(VulkanDisplay display, bool interactive)
    {
        var context = display.Context;
        gpu = WorldFrame.CreateGpu(context, install, scene, assets, o, interactive);
        if (gpu.Post is { } vendorPost) vendorPost.UpscalerFactory = Meitou.Rendering.Upscalers.VendorUpscalers.Factory(display.Context, streamline);
        (camera, render) = WorldFrame.Setup(scene, o);
        session = new WorldSession(scene.Focus, (scene.X0, scene.Z0, scene.X1, scene.Z1), gpu.Terrain.HeightAt, o.Hour, g.TickRate ?? config.TickRate,
            simulation: new Meitou.Simulation.WorldSettings { Seed = g.Seed, Threads = Math.Max(1, g.SimThreads ?? config.SimThreads ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 8)) });
        foreach (var problem in session.Bindings.Apply(config.Bindings)) Console.Error.WriteLine($"config    binding skipped: {problem}");
        var rig = session.Camera;
        var target = camera.Target;
        rig.Place(new Vector2(target.X, target.Z), (o.Yaw ?? 30) * MathF.PI / 180,
            (o.Pitch ?? Meitou.Data.World.KenshiCamera.InitialPitchDegrees) * MathF.PI / 180, o.Distance ?? Meitou.Data.World.KenshiCamera.InitialDistance);
        if (g.FreeCamera)
        {
            session.Input.SetKey(FirstKey(InputAction.ToggleFreeCamera), true);
            session.Tick();
            session.Input.SetKey(FirstKey(InputAction.ToggleFreeCamera), false);
        }
        ApplyCamera(session.Camera.Current);
        if (interactive) WorldFrame.FinishLoading(context);
    }

    Meitou.Rendering.Upscalers.Streamline? streamline;

    /// <summary>DLSS asked for, on the command line or in the saved settings: Streamline must be loaded before the Vulkan device.</summary>
    bool WantsDlss() => o.Post.Upscale.Kind == UpscalerKind.Dlss || !o.Post.Upscale.Explicit && config.Graphics.TryGetValue(WorldFrame.UpscalerSliders[0], out float k) && MathF.Round(k) == (int)UpscalerKind.Dlss;

    EngineKey FirstKey(InputAction action) => session.Bindings.Get(action).First(b => !b.IsMouse).Key;

    void ApplyCamera(CameraState s)
    {
        camera.Target = s.Target;
        camera.Yaw = s.Yaw;
        camera.Pitch = s.Pitch;
        camera.Distance = s.Distance;
    }

    void DrawWorld(int width, int height)
    {
        if (gpu.Foliage is { } foliage && o.Screenshot is null) foliage.SwaySeconds = realTime.Elapsed.TotalSeconds;
        // The heat haze's gameTime: game hours since the start (it stops while paused, as in the game).
        gpu.GameHours = session.Clock.HoursSinceStart;
        WorldFrame.Draw(gpu, scene, camera, render, width, height, (float)session.Clock.HourOfDay, (float)realTime.Elapsed.TotalSeconds / 600f, o.FogDistance);
    }

    unsafe int Screenshot()
    {
        using var display = new VulkanDisplay(null, vsync: false, streamline: WantsDlss());
        streamline = display.Streamline;
        Boot(display, interactive: false);
        for (int i = 0; i < g.Ticks; i++)
        {
            session.Tick();
            session.AdvanceSimulation(session.Ticks.TickSeconds);   // one control tick of real time, at speed 1
        }
        ApplyCamera(session.Camera.Current);
        gpu.Streamer?.Settle(gpu.Anchor ?? camera.Eye);
        gpu.Objects?.Settle(gpu.Anchor ?? camera.Eye);
        gpu.Foliage?.Settle(gpu.Anchor ?? camera.Eye);
        WorldFrame.FinishLoading(display.Context);
        var context = display.Context;
        int w = o.Width, h = o.Height;
        using var target = Meitou.Rendering.Gpu.Texture.Create(context, new TextureDesc(Silk.NET.Vulkan.Format.R8G8B8A8Unorm, w, h,
            Use: TextureUse.ColourTarget | TextureUse.TransferSrc | TextureUse.Sampled, Name: "offscreen picture"));
        gpu.Post!.Target = target;
        gpu.Post.InstantAdaptation = true;
        for (int i = 0; i < gpu.Post.WarmupFrames; i++) { DrawWorld(w, h); display.EndFrame(); }   // a temporal upscaler converges first
        DrawWorld(w, h);
        display.EndFrame();
        DrawWorld(w, h);
        display.EndFrame();
        context.Finish();
        var s = session.Camera.Current;
        Console.WriteLine($"camera    {(session.Camera.IsFree ? "free" : "strategy")}: pivot {s.Target.X:0}, {s.Target.Y:0}, {s.Target.Z:0}, eye {s.Eye.X:0}, {s.Eye.Y:0}, {s.Eye.Z:0}, " +
            $"yaw {s.Yaw * 180 / MathF.PI:0.#}, pitch {s.Pitch * 180 / MathF.PI:0.#}, boom {s.Distance:0.#}; {session.Ticks.TotalTicks} control ticks, {session.Simulation.TotalTicks} simulation ticks, game time {session.Clock.TimeText} ({session.Clock.DayText})");
        FramebufferCapture.SavePng(context, target, o.Screenshot!, w, h);
        Console.WriteLine($"saved     {Path.GetFullPath(o.Screenshot!)}");
        gpu.Dispose();
        return 0;
    }

    int Interactive()
    {
        bool vsync = g.VSync ?? config.VSync;
        int fpsLimit = g.FpsLimit ?? config.FpsLimit;
        using var display = new VulkanDisplay(WindowOptions.Default with
        {
            Size = new Vector2D<int>(o.Width, o.Height),
            Title = "Meitou",
            WindowState = WindowState.Maximized,
            FramesPerSecond = 0,
            UpdatesPerSecond = 0,
        }, vsync, streamline: WantsDlss());
        streamline = display.Streamline;
        var window = display.Window!;
        Boot(display, interactive: true);
        var overlay = DebugOverlay.TryCreate(display.Context);
        var panel = overlay is null ? null : WorldFrame.CreateSettingsPanel(overlay, gpu, render);
        if (panel is not null)
            foreach (var slider in panel.Sliders)
                if (config.Graphics.TryGetValue(slider.Label, out float v) && !(o.Post.Upscale.Explicit && WorldFrame.UpscalerSliders.Contains(slider.Label)))
                    slider.Set(Math.Clamp(v, slider.Min, slider.Max));

        var silkInput = Silk.NET.Input.InputWindowExtensions.CreateInput(window);
        Vector2? lastMouse = null;
        bool panelDrag = false;
        Vector2 Pixels(Vector2 p) => p * new Vector2(window.FramebufferSize.X / (float)Math.Max(window.Size.X, 1), window.FramebufferSize.Y / (float)Math.Max(window.Size.Y, 1));
        foreach (var kb in silkInput.Keyboards)
        {
            kb.KeyDown += (_, k, _) => { if (Map(k) is { } key) session.Input.SetKey(key, true); };
            kb.KeyUp += (_, k, _) => { if (Map(k) is { } key) session.Input.SetKey(key, false); };
        }
        foreach (var mouse in silkInput.Mice)
        {
            mouse.MouseDown += (m, b) =>
            {
                if (b == SilkButton.Left && panel?.MouseDown(Pixels(m.Position)) == true) { panelDrag = true; return; }
                if (panel?.Contains(Pixels(m.Position)) == true) return;
                if (Map(b) is { } button) session.Input.SetMouseButton(button, true);
            };
            mouse.MouseUp += (_, b) =>
            {
                panel?.MouseUp();
                panelDrag = false;
                if (Map(b) is { } button) session.Input.SetMouseButton(button, false);
            };
            mouse.MouseMove += (_, p) =>
            {
                session.Input.SetMousePosition(p.X, p.Y);
                if (panel?.MouseMove(Pixels(p)) == true || panelDrag) { lastMouse = p; return; }
                if (lastMouse is { } last) session.Input.AddMouseDelta(p.X - last.X, p.Y - last.Y);
                lastMouse = p;
            };
            mouse.Scroll += (m, wheel) => { if (panel?.Contains(Pixels(m.Position)) != true) session.Input.AddWheel(wheel.Y); };
        }
        Console.WriteLine(GameOptions.Usage[GameOptions.Usage.IndexOf("Keys:", StringComparison.Ordinal)..]);
        Console.WriteLine($"display   vsync {(vsync ? "on" : "off")}, frame limit {(vsync || fpsLimit <= 0 ? "none" : fpsLimit + " fps")}, control tick {session.Ticks.TickRate:0} Hz, world tick {SimulationClock.TickRate:0} per game second; settings in {config.Path}");

        var frame = Stopwatch.StartNew();
        double last = 0, titleTimer = 0, cpuSum = 0;
        int frames = 0, totalFrames = 0;
        bool quit = false;
        session.Ticked += actions =>
        {
            if (actions.Pressed(InputAction.Quit)) quit = true;
            if (actions.Pressed(InputAction.ToggleSettings) && panel is not null) panel.Visible = !panel.Visible;
            if (actions.Pressed(InputAction.Screenshot)) screenshotRequested = true;
        };
        long windowRan = 0, windowDropped = 0;
        while (!window.IsClosing && !quit)
        {
            window.DoEvents();
            if (window.IsClosing) break;
            double now = frame.Elapsed.TotalSeconds, dt = now - last;
            last = now;
            session.Advance(dt);
            ApplyCamera(session.CameraAt());
            var size = window.FramebufferSize;
            if (display.BeginFrame(size.X, size.Y))
            {
                long t0 = Stopwatch.GetTimestamp();
                // The chain and the panel draw into the window's backbuffer (made again when the size changes).
                var backbuffer = display.Backbuffer!;
                gpu.Post!.Target = backbuffer;
                if (overlay is not null) overlay.Target = backbuffer;
                DrawWorld(size.X, size.Y);
                cpuSum += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                bool shot = screenshotRequested;
                screenshotRequested = false;
                panel?.Draw(size.X, size.Y);
                display.Present();
                if (shot) SaveScreenshot(display.Context, backbuffer, size.X, size.Y);
                frames++;
            }
            titleTimer += dt;
            if (titleTimer >= 0.5)
            {
                var s = session.Camera.Current;
                // The achieved speed over this window: the asked speed scaled by the share of the owed ticks that ran.
                long ran = session.Simulation.TotalTicks - windowRan, dropped = session.Simulation.DroppedTicks - windowDropped;
                windowRan = session.Simulation.TotalTicks;
                windowDropped = session.Simulation.DroppedTicks;
                double achieved = ran + dropped == 0 ? session.TimeScale : session.TimeScale * ran / (ran + dropped);
                string speedText = session.Simulation.IsPaused ? "paused" + (session.Simulation.RequestedPause ? "" : $" (x{session.Simulation.LastNonZeroSpeed:0})") : $"x{session.TimeScale:0}" + (achieved < session.TimeScale - 0.05 ? $" (running x{achieved:0.0})" : "");
                window.Title = $"Meitou | {frames / titleTimer:0} fps, cpu {cpuSum / Math.Max(frames, 1):0.00} ms | {(session.Camera.IsFree ? "free camera" : $"boom {s.Distance:0}")} | " +
                    $"{session.Clock.TimeText} {session.Clock.DayText} {speedText} | {s.Target.X:0}, {s.Target.Z:0}{(gpu.Post is { Temporal: true } p ? $" | {p.ActiveUpscaler}" : "")}";
                titleTimer = cpuSum = 0;
                frames = 0;
            }
            totalFrames++;
            if (g.QuitAfter is { } quitAfter && now >= quitAfter) { Console.WriteLine($"smoke     {totalFrames} frames in {now:0.0} s: {totalFrames / now:0} fps on average, {session.Ticks.TotalTicks} control ticks, {session.Simulation.TotalTicks} simulation ticks, game time {session.Clock.TimeText}"); quit = true; }
            if (!vsync && fpsLimit > 0) Limit(frame, now, 1.0 / fpsLimit);
        }
        if (panel is not null)
            foreach (var slider in panel.Sliders) config.Graphics[slider.Label] = slider.Get();
        config.Bindings = session.Bindings.ToDictionary();
        config.Save();
        overlay?.Dispose();
        gpu.Dispose();
        silkInput.Dispose();
        return 0;
    }

    bool screenshotRequested;

    static void SaveScreenshot(GpuContext context, Texture backbuffer, int width, int height)
    {
        var file = Path.Combine(Directory.CreateDirectory(@"C:\Temp").FullName, $"meitou-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        FramebufferCapture.SavePng(context, backbuffer, file, width, height);
        Console.WriteLine($"saved     {file}");
    }

    /// <summary>Waits until <paramref name="period"/> has passed since <paramref name="start"/>: sleeps most of it, spins the last 1.5 ms.</summary>
    static void Limit(Stopwatch clock, double start, double period)
    {
        double end = start + period;
        double remaining = end - clock.Elapsed.TotalSeconds;
        if (remaining > 0.002) Thread.Sleep(TimeSpan.FromSeconds(remaining - 0.0015));
        while (clock.Elapsed.TotalSeconds < end) Thread.SpinWait(64);
    }

    static EngineButton? Map(SilkButton b) => b switch
    {
        SilkButton.Left => EngineButton.Left,
        SilkButton.Right => EngineButton.Right,
        SilkButton.Middle => EngineButton.Middle,
        SilkButton.Button4 => EngineButton.Button4,
        SilkButton.Button5 => EngineButton.Button5,
        _ => null,
    };

    static EngineKey? Map(SilkKey k) => k switch
    {
        >= SilkKey.A and <= SilkKey.Z => EngineKey.A + (k - SilkKey.A),
        >= SilkKey.Number0 and <= SilkKey.Number9 => EngineKey.D0 + (k - SilkKey.Number0),
        >= SilkKey.F1 and <= SilkKey.F12 => EngineKey.F1 + (k - SilkKey.F1),
        SilkKey.Left => EngineKey.Left,
        SilkKey.Right => EngineKey.Right,
        SilkKey.Up => EngineKey.Up,
        SilkKey.Down => EngineKey.Down,
        SilkKey.Space => EngineKey.Space,
        SilkKey.Enter => EngineKey.Enter,
        SilkKey.Escape => EngineKey.Escape,
        SilkKey.Tab => EngineKey.Tab,
        SilkKey.Backspace => EngineKey.Backspace,
        SilkKey.PageUp => EngineKey.PageUp,
        SilkKey.PageDown => EngineKey.PageDown,
        SilkKey.Home => EngineKey.Home,
        SilkKey.End => EngineKey.End,
        SilkKey.Insert => EngineKey.Insert,
        SilkKey.Delete => EngineKey.Delete,
        SilkKey.ShiftLeft => EngineKey.LeftShift,
        SilkKey.ShiftRight => EngineKey.RightShift,
        SilkKey.ControlLeft => EngineKey.LeftControl,
        SilkKey.ControlRight => EngineKey.RightControl,
        SilkKey.AltLeft => EngineKey.LeftAlt,
        SilkKey.AltRight => EngineKey.RightAlt,
        SilkKey.Semicolon => EngineKey.Semicolon,
        SilkKey.Comma => EngineKey.Comma,
        SilkKey.Period => EngineKey.Period,
        SilkKey.Minus => EngineKey.Minus,
        SilkKey.Equal => EngineKey.Equal,
        SilkKey.Slash => EngineKey.Slash,
        SilkKey.BackSlash => EngineKey.Backslash,
        SilkKey.GraveAccent => EngineKey.Grave,
        SilkKey.LeftBracket => EngineKey.LeftBracket,
        SilkKey.RightBracket => EngineKey.RightBracket,
        SilkKey.Apostrophe => EngineKey.Apostrophe,
        _ => null,
    };
}
