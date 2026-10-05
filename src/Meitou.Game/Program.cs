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
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using EngineKey = Meitou.Engine.Input.Key;
using EngineButton = Meitou.Engine.Input.MouseButton;
using SilkKey = Silk.NET.Input.Key;
using SilkButton = Silk.NET.Input.MouseButton;

namespace Meitou.Game;

/// <summary>Game-only options; everything else is the world options shared with the viewer (<see cref="WorldOptions"/>).</summary>
sealed class GameOptions
{
    public int? FpsLimit, TickRate;
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
          --tick-rate <hz>           simulation ticks per second (default 30)
          --free-camera              start in the free camera (; toggles)
          --ticks <n>                with --screenshot: run n simulation ticks before the picture
          --quit-after <s>           close after s seconds and print the frame rate (smoke test)
          --yaw/--pitch/--distance   start view: heading, pitch above the horizon and boom (Kenshi: 30 degrees, boom 150; clamped to 10..2000)
          world options as meitou-viewer --world: --at, --zone, --town, --radius, --time, --screenshot, --size, --no-foliage, ...
        Keys: W/A/S/D move, Q/E or Left/Right rotate, Up/Down pitch, wheel or PageUp/PageDown zoom, right or middle drag orbit,
          ; free camera (R/F up/down), Space pause, . / , time faster/slower, Tab settings, F12 screenshot, Esc quit.
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
/// The game loop: a fixed simulation tick (<see cref="FixedStepClock"/>, 30 Hz by default) runs the input actions, the camera rig
/// and the game clock; each displayed frame draws the camera interpolated between the last two ticks at the display rate.
/// </summary>
sealed class GameHost(GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o, GameOptions g, UserConfig config)
{
    WorldSession session = null!;
    WorldFrame.Gpu gpu = null!;
    WorldCamera camera = null!;
    WorldRenderOptions render = null!;
    readonly Stopwatch realTime = Stopwatch.StartNew();

    public int Run()
    {
        return o.Screenshot is not null ? Screenshot() : Interactive();
    }

    void Boot(IGl gl, bool interactive)
    {
        gpu = WorldFrame.CreateGpu(gl, install, scene, assets, o, interactive);
        (camera, render) = WorldFrame.Setup(scene, o);
        session = new WorldSession(scene.Focus, (scene.X0, scene.Z0, scene.X1, scene.Z1), gpu.Terrain.HeightAt, o.Hour, g.TickRate ?? config.TickRate);
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
    }

    EngineKey FirstKey(InputAction action) => session.Bindings.Get(action).First(b => !b.IsMouse).Key;

    void ApplyCamera(CameraState s)
    {
        camera.Target = s.Target;
        camera.Yaw = s.Yaw;
        camera.Pitch = s.Pitch;
        camera.Distance = s.Distance;
    }

    void DrawWorld(IGl gl, int width, int height)
    {
        if (gpu.Foliage is { } foliage && o.Screenshot is null) foliage.SwaySeconds = realTime.Elapsed.TotalSeconds;
        WorldFrame.Draw(gl, gpu, scene, camera, render, width, height, (float)session.Clock.HourOfDay, (float)realTime.Elapsed.TotalSeconds / 600f, o.FogDistance);
    }

    unsafe int Screenshot()
    {
        using var window = Window.Create(WindowOptions.Default with
        {
            Size = new Vector2D<int>(o.Width, o.Height),
            IsVisible = false,
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
            Samples = 0,
            PreferredDepthBufferBits = 24,
        });
        window.Initialize();
        using var rawGl = window.CreateOpenGL();
        IGl gl = new GlPassthrough(rawGl);
        Boot(gl, interactive: false);
        for (int i = 0; i < g.Ticks; i++) session.Tick();
        ApplyCamera(session.Camera.Current);
        gpu.Streamer?.Settle(gpu.Anchor ?? camera.Eye);
        gpu.Objects?.Settle(gpu.Anchor ?? camera.Eye);
        gpu.Foliage?.Settle(gpu.Anchor ?? camera.Eye);
        int w = o.Width, h = o.Height;
        uint fbo = gl.GenFramebuffer(), colour = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, colour);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, (uint)w, (uint)h);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, colour);
        gpu.Post!.Target = fbo;
        gpu.Post.InstantAdaptation = true;
        DrawWorld(gl, w, h);
        DrawWorld(gl, w, h);
        gl.Finish();
        var s = session.Camera.Current;
        Console.WriteLine($"camera    {(session.Camera.IsFree ? "free" : "strategy")}: pivot {s.Target.X:0}, {s.Target.Y:0}, {s.Target.Z:0}, eye {s.Eye.X:0}, {s.Eye.Y:0}, {s.Eye.Z:0}, " +
            $"yaw {s.Yaw * 180 / MathF.PI:0.#}, pitch {s.Pitch * 180 / MathF.PI:0.#}, boom {s.Distance:0.#}; {session.Ticks.TotalTicks} ticks, game time {session.Clock.HourOfDay:0.00} h");
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
        GlCapture.SavePng(gl, o.Screenshot!, w, h);
        Console.WriteLine($"saved     {Path.GetFullPath(o.Screenshot!)}");
        gpu.Dispose();
        return 0;
    }

    int Interactive()
    {
        bool vsync = g.VSync ?? config.VSync;
        int fpsLimit = g.FpsLimit ?? config.FpsLimit;
        using var window = Window.Create(WindowOptions.Default with
        {
            Size = new Vector2D<int>(o.Width, o.Height),
            Title = "Meitou",
            WindowState = WindowState.Maximized,
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
            Samples = 0,
            VSync = vsync,
            PreferredDepthBufferBits = 24,
            FramesPerSecond = 0,
            UpdatesPerSecond = 0,
        });
        window.Initialize();
        var rawGl = window.CreateOpenGL();
        IGl gl = new GlPassthrough(rawGl);
        Boot(gl, interactive: true);
        var overlay = DebugOverlay.TryCreate(gl);
        var panel = overlay is null ? null : WorldFrame.CreateSettingsPanel(overlay, gpu, render);
        if (panel is not null)
            foreach (var slider in panel.Sliders)
                if (config.Graphics.TryGetValue(slider.Label, out float v)) slider.Set(Math.Clamp(v, slider.Min, slider.Max));
        gl.Enable(EnableCap.Multisample);

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
        Console.WriteLine($"display   vsync {(vsync ? "on" : "off")}, frame limit {(vsync || fpsLimit <= 0 ? "none" : fpsLimit + " fps")}, simulation {session.Ticks.TickRate:0} Hz; settings in {config.Path}");

        var frame = Stopwatch.StartNew();
        double last = 0, titleTimer = 0, cpuSum = 0;
        int frames = 0, totalFrames = 0;
        bool quit = false;
        while (!window.IsClosing && !quit)
        {
            window.DoEvents();
            if (window.IsClosing) break;
            double now = frame.Elapsed.TotalSeconds, dt = now - last;
            last = now;
            int n = session.Ticks.Advance(dt);
            for (int i = 0; i < n; i++)
            {
                var actions = session.Tick();
                if (actions.Pressed(InputAction.Quit)) quit = true;
                if (actions.Pressed(InputAction.ToggleSettings) && panel is not null) panel.Visible = !panel.Visible;
                if (actions.Pressed(InputAction.Screenshot)) screenshotRequested = true;
            }
            ApplyCamera(session.CameraAt());
            var size = window.FramebufferSize;
            if (size.X > 0 && size.Y > 0)
            {
                long t0 = Stopwatch.GetTimestamp();
                DrawWorld(gl, size.X, size.Y);
                cpuSum += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (screenshotRequested)
                {
                    screenshotRequested = false;
                    var file = Path.Combine(Path.GetTempPath(), $"meitou-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                    gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
                    gl.ReadBuffer(ReadBufferMode.Back);
                    GlCapture.SavePng(gl, file, size.X, size.Y);
                    Console.WriteLine($"saved     {file}");
                }
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                panel?.Draw(size.X, size.Y);
                window.SwapBuffers();
                frames++;
            }
            titleTimer += dt;
            if (titleTimer >= 0.5)
            {
                var s = session.Camera.Current;
                int minutes = (int)(session.Clock.HourOfDay * 60);
                window.Title = $"Meitou | {frames / titleTimer:0} fps, cpu {cpuSum / Math.Max(frames, 1):0.00} ms | {(session.Camera.IsFree ? "free camera" : $"boom {s.Distance:0}")} | " +
                    $"{minutes / 60:00}:{minutes % 60:00} x{session.TimeScale:0}{(session.Clock.Paused ? " paused" : "")} | {s.Target.X:0}, {s.Target.Z:0}";
                titleTimer = cpuSum = 0;
                frames = 0;
            }
            totalFrames++;
            if (g.QuitAfter is { } quitAfter && now >= quitAfter) { Console.WriteLine($"smoke     {totalFrames} frames in {now:0.0} s: {totalFrames / now:0} fps on average, {session.Ticks.TotalTicks} ticks"); quit = true; }
            if (!vsync && fpsLimit > 0) Limit(frame, now, 1.0 / fpsLimit);
        }
        if (panel is not null)
            foreach (var slider in panel.Sliders) config.Graphics[slider.Label] = slider.Get();
        config.Bindings = session.Bindings.ToDictionary();
        config.Save();
        overlay?.Dispose();
        gpu.Dispose();
        silkInput.Dispose();
        rawGl.Dispose();
        return 0;
    }

    bool screenshotRequested;

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
