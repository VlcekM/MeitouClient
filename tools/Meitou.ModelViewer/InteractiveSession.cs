using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data.Ogre;
using Meitou.Data.World;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

using Meitou.Game.Audio;
using Meitou.Game.Rendering.Display;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Terrain;
using Meitou.Game.Rendering.Upscalers;
using static Meitou.Game.Rendering.Frame.WorldFrame;

namespace Meitou.ModelViewer;

/// <summary>
/// The interactive world viewer's window session: the state its key, mouse, update and render handlers share, from the set-up after the
/// window opens until it closes.
/// </summary>
sealed class InteractiveSession
{
    #region Session

    /// <summary>
    /// The game install the renderers load from.
    /// </summary>
    readonly GameInstall _install;

    /// <summary>
    /// The loaded world.
    /// </summary>
    readonly WorldScene _scene;

    /// <summary>
    /// Finds the game's files (the overlay's font).
    /// </summary>
    readonly AssetLocator _assets;

    /// <summary>
    /// The viewer's options; the keys and the Tab panel change some of them while it runs.
    /// </summary>
    readonly WorldOptions _o;

    /// <summary>
    /// The Vulkan display the window presents through.
    /// </summary>
    readonly VulkanDisplay _display;

    /// <summary>
    /// The viewer's window.
    /// </summary>
    readonly IWindow _window;

    /// <summary>
    /// Streamline for DLSS, as the display loaded it.
    /// </summary>
    readonly Streamline? _streamline;

    /// <summary>
    /// NGX for DLSS, with <see cref="_streamline"/>.
    /// </summary>
    readonly Ngx? _ngx;

    /// <summary>
    /// The renderers, made in <see cref="CreateRenderers"/>.
    /// </summary>
    WorldGpu? _gpu;

    /// <summary>
    /// The camera, from the options.
    /// </summary>
    WorldCamera _camera = null!;

    /// <summary>
    /// The render switches the keys and the panel flip.
    /// </summary>
    WorldRenderOptions _render = null!;

    /// <summary>
    /// The first keyboard (the held keys fly the camera; the clipboard carries camera codes).
    /// </summary>
    IKeyboard? _keyboard;

    /// <summary>
    /// The P key asked for a screenshot of the next frame.
    /// </summary>
    bool _screenshotRequested;

    /// <summary>
    /// The viewer's game hour.
    /// </summary>
    float _hour;

    /// <summary>
    /// The viewer's game day.
    /// </summary>
    int _day;

    /// <summary>
    /// The animation clock (clouds, particles, water): real seconds, or exact frame steps while the cinema records.
    /// </summary>
    double _animSeconds;

    /// <summary>
    /// The saved camera shots and their flights (F6).
    /// </summary>
    Cinema _cinema = null!;

    /// <summary>
    /// The key list and statistics overlay; null without a usable font.
    /// </summary>
    DebugOverlay? _overlay;

    /// <summary>
    /// The Tab settings panel.
    /// </summary>
    SettingsPanel? _panel;

    /// <summary>
    /// The F12 frame profiler.
    /// </summary>
    FrameProfiler? _profiler;

    /// <summary>
    /// MEITOU_PASS_STATS=1: the frame cost breakdown, printed when the window closes.
    /// </summary>
    PassMeter? _meter;

    /// <summary>
    /// The ambience and music; null with <c>--no-audio</c> or without a device.
    /// </summary>
    AudioSystem? _audio;

    /// <summary>
    /// Whether the F11 statistics and the F9 frame rate counter show.
    /// </summary>
    bool _statsVisible, _fpsVisible;

    /// <summary>
    /// The F11 statistics' lines, made again four times a second.
    /// </summary>
    readonly List<string> _stats = [];

    /// <summary>
    /// The key list's items, from the usage text.
    /// </summary>
    List<string> _keyItems = null!;

    /// <summary>
    /// The Faithful / Meitou switches (Enhancements).
    /// </summary>
    IReadOnlyList<Enhancement> _switches = null!;

    /// <summary>
    /// F1: every switch Faithful and back (the Tab panel sets them one by one).
    /// </summary>
    MeitouToggle _meitou = null!;

    /// <summary>
    /// Takes over the window that <paramref name="display"/> opened.
    /// </summary>
    public InteractiveSession(GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o, VulkanDisplay display, IWindow window,
            Streamline? streamline, Ngx? ngx)
    {
        _install = install;
        _scene = scene;
        _assets = assets;
        _o = o;
        _display = display;
        _window = window;
        _streamline = streamline;
        _ngx = ngx;
    }

    /// <summary>
    /// Sets the viewer up, runs the window until it closes and returns the exit code.
    /// </summary>
    public int Run()
    {
        _hour = _o.Hour;
        _day = _o.Day ?? 0;
        _cinema = new Cinema(() => _camera, () => _hour, v => _hour = v, () => BorderlessFullScreen.IsOn(_window),
                () => BorderlessFullScreen.Toggle(_window), Cinema.ViewerFile);
        _cinema.Load();
        if (_o.PlayShots) _cinema.PlayLoop();
        _keyItems = DebugOverlay.KeyItems(WorldOptions.Usage);

        // The Faithful / Meitou switches (Enhancements); F1 turns them all Faithful and back (the Tab panel sets them one by one).
        _switches = LiveSwitches(_o, () => _gpu, () => _camera, () => _render);
        _meitou = new MeitouToggle(_switches);

        CreateRenderers();
        WireInput();
        Console.WriteLine(WorldOptions.Usage[WorldOptions.Usage.IndexOf("Keys:", StringComparison.Ordinal)..]);

        _frameClock.Start();
        _window.Update += dt => SmokeTest.Check(_window);
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Closing += OnClosing;
        _window.Run();
        return 0;
    }

    /// <summary>
    /// The renderers, the overlay and its panel, the profiler, the pass meter and the audio.
    /// </summary>
    void CreateRenderers()
    {
        _gpu = CreateGpu(_display.Context, _install, _scene, _assets, _o, interactive: true);
        if (_gpu.Post is { } vendorPost)
            vendorPost.UpscalerFactory = VendorUpscalers.Factory(_display.Context, _streamline, _ngx);
        _overlay = DebugOverlay.TryCreate(_display.Context, _assets.Find);
        if (_overlay is null)
            Console.WriteLine("keys      no monospace system font or game font found:the F10 key list and F11 statistics are unavailable");
        if (_overlay is not null) _overlay.Visible = _o.ShowKeys;
        (_camera, _render) = Setup(_scene, _o);
        if (_overlay is not null)
        {
            _panel = CreateSettingsPanel(_overlay, _gpu, _render, () => _hour, v => _hour = v, () => _display.VSync, v => _display.VSync = v,
                _switches, () => _o.TimeSpeed, v => _o.TimeSpeed = v, () => BorderlessFullScreen.IsOn(_window),
                _ => BorderlessFullScreen.Toggle(_window));
        }
        _profiler = new FrameProfiler(_display.Context, () => _display.Context.GpuFrameMs);
        _meter = PassMeter.TryCreate(_display);   // MEITOU_PASS_STATS=1: the frame cost breakdown, printed when the window closes
        if (!_o.NoAudio)
        {
            _audio = AudioSystem.TryCreate(_install, AudioSystem.AmbientVolumeSetting(_install), out var audioError);
            if (_audio is not null) _audio.Player.Gain = MathF.Pow(10, _o.AudioGainDb / 20);
            Console.WriteLine(_audio is not null
                ? $"audio     {_audio.DeviceName}, "
                + $"ambience and music from the Wwise banks{(_o.AudioGainDb != 0 ? $", gain {_o.AudioGainDb:+0.#;-0.#} dB" : "")} "
                + "(--no-audio turns it off)" : $"audio     off: {audioError}");
        }
    }

    /// <summary>
    /// The window closes: the audio, the spike summary and the pass meter's report, then the profiler, the overlay and the renderers.
    /// </summary>
    void OnClosing()
    {
        _audio?.Dispose();
        SpikeLog.PrintSummary();
        if (_meter is not null)
        {
            _meter.Report(Console.Out);
            _meter.Dispose();
        }
        _profiler?.Dispose();
        _overlay?.Dispose();
        _gpu?.Dispose();
    }

    /// <summary>
    /// Feeds the ambience and the music the frame's state the way the game's camera update does (docs/audio.md): the region under the
    /// camera's target (the game's pivot), the hour against sunrise and sunset, the eye's height above the ground, and the weather's rain and
    /// wind.
    /// </summary>
    static void UpdateAudio(AudioSystem audio, WorldGpu gpu, WorldScene scene, WorldCamera camera, float hour, double realSeconds)
    {
        var target = camera.Target;
        var eye = camera.Eye;
        var weather = gpu.Weather;
        audio.Update(weather?.World.RegionAt(target.X, target.Z), weather?.State, hour, scene.Clock.Sunrise, scene.Clock.Sunset,
            eye.Y - gpu.Terrain.HeightAt(eye.X, eye.Z), realSeconds);
    }

    #endregion

    #region Input

    /// <summary>
    /// The pointer's last position while a button drags the camera.
    /// </summary>
    Vector2? _lastMouse;

    /// <summary>
    /// The button dragging the camera (left orbits, others look), null when none.
    /// </summary>
    MouseButton? _dragging;

    /// <summary>
    /// The keyboards' key presses and the mice's buttons, moves and wheels.
    /// </summary>
    void WireInput()
    {
        var input = _window.CreateInput();
        _keyboard = input.Keyboards.FirstOrDefault();
        foreach (var kb in input.Keyboards) kb.KeyDown += (_, key, _) => OnKey(key);
        foreach (var mouse in input.Mice)
        {
            mouse.MouseDown += OnMouseDown;
            mouse.MouseUp += (_, _) =>
            {
                _panel?.MouseUp();
                _cinema.MouseUp();
                _dragging = null;
            };
            mouse.MouseMove += OnMouseMove;
            mouse.Scroll += (_, wheel) => _camera.Zoom(wheel.Y);
        }
    }

    /// <summary>
    /// Window to framebuffer pixels (they differ with display scaling); the panel is laid out in the latter.
    /// </summary>
    Vector2 Pixels(Vector2 p) => p * new Vector2(_window.FramebufferSize.X / (float)Math.Max(_window.Size.X, 1),
            _window.FramebufferSize.Y / (float)Math.Max(_window.Size.Y, 1));

    /// <summary>
    /// A press goes to the panel, the profiler or the cinema under it, else starts dragging the camera.
    /// </summary>
    void OnMouseDown(IMouse m, MouseButton b)
    {
        if (b == MouseButton.Left && _panel?.MouseDown(Pixels(m.Position)) == true) return;
        if (b == MouseButton.Left && _profiler?.Click(Pixels(m.Position)) == true) return;
        if (b == MouseButton.Left && _cinema.MouseDown(Pixels(m.Position))) return;
        if (_cinema.Contains(Pixels(m.Position))) return;
        if (_panel?.Contains(Pixels(m.Position)) == true) return;
        _dragging = b;
        _lastMouse = null;
    }

    /// <summary>
    /// A move goes to the panel or the cinema, else orbits (left button) or turns (others) the dragged camera.
    /// </summary>
    void OnMouseMove(IMouse mouse, Vector2 p)
    {
        if (_panel?.MouseMove(Pixels(p)) == true) return;
        if (_cinema.MouseMove(Pixels(p))) return;
        if (_dragging is { } b && _lastMouse is { } last)
        {
            var d = p - last;
            if (b == MouseButton.Left) _camera.Orbit(d.X, d.Y);
            else _camera.Look(d.X, d.Y);
        }
        _lastMouse = p;
    }

    /// <summary>
    /// Prints the post-processing settings (after the exposure keys).
    /// </summary>
    void PostStatus() => Console.WriteLine($"post      {_o.Post.Describe()}");

    /// <summary>
    /// The state a key controls, for the key list ("on", "off", a mode or a value); null for actions.
    /// </summary>
    string? KeyState(string key)
    {
        static string OnOff(bool on) => on ? "on" : "off";
        return key switch
        {
            "T" => OnOff(_render.Textures),
            "N" => OnOff(_render.NormalMaps),
            "O" => OnOff(_render.Objects),
            "F" => _gpu?.Foliage is { } foliage ? OnOff(foliage.Enabled) : "n/a",
            "X" => _render.Wireframe switch { 0 => "solid", 1 => "solid + lines", _ => "lines" },
            "V" => _render.Debug switch { 0 => "off", 1 => "blend weights", 2 => "layer weights", _ => "plain shading" },
            "G" => OnOff(_render.Water),
            "R" => OnOff(_render.Reflections),
            "," => TimeText(_hour),
            "[" => $"day {_day}",
            "\\" => _gpu?.Weather is { } w ? (w.World.ForcedWeather is { } forced ? forced.Name : "auto") : null,
            "F1" => _meitou.On ? "Meitou" : "Faithful",
            "-" => $"{_o.Post.Exposure:0.00}",
            "F10" => "on",
            "F11" => OnOff(_statsVisible),
            "F12" => _profiler?.Showing.ToString().ToLowerInvariant(),
            "Tab" => _panel is { Visible: true } ? "on" : "off",
            _ => null,
        };
    }

    /// <summary>
    /// A key press: Ctrl+C / Ctrl+V the camera code, Alt+Enter full screen, the cinema's keys, then the viewer's.
    /// </summary>
    void OnKey(Key key)
    {
        if (_keyboard is not null && (_keyboard.IsKeyPressed(Key.ControlLeft) || _keyboard.IsKeyPressed(Key.ControlRight)) && key is Key.C or Key.V)
        {
            CopyOrPasteCamera(_keyboard, key);
            return;
        }
        bool ctrl = _keyboard is not null && (_keyboard.IsKeyPressed(Key.ControlLeft) || _keyboard.IsKeyPressed(Key.ControlRight));
        bool shift = _keyboard is not null && (_keyboard.IsKeyPressed(Key.ShiftLeft) || _keyboard.IsKeyPressed(Key.ShiftRight));
        if (key == Key.Enter && _keyboard is not null && (_keyboard.IsKeyPressed(Key.AltLeft) || _keyboard.IsKeyPressed(Key.AltRight)))
        {
            BorderlessFullScreen.Toggle(_window);
            return;
        }
        if (Enum.TryParse(key.ToString(), out CinemaKey cinemaKey) && _cinema.OnKey(cinemaKey, ctrl, shift)) return;
        switch (key)
        {
            case Key.Escape: _window.Close(); break;
            case Key.F1 when ctrl: Console.WriteLine(_meitou.Sweep()); break;
            case Key.F1: Console.WriteLine(_meitou.Toggle()); break;
            case Key.Minus:
                _o.Post.Exposure = MathF.Max(_o.Post.Exposure / 1.1f, 0.05f);
                PostStatus();
                break;
            case Key.Equal:
                _o.Post.Exposure = MathF.Min(_o.Post.Exposure * 1.1f, 20f);
                PostStatus();
                break;
            case Key.T: _render.Textures = !_render.Textures; break;
            case Key.N: _render.NormalMaps = !_render.NormalMaps; break;
            case Key.K:
                GroundRelief.ParallaxActive = !GroundRelief.ParallaxActive;
                Console.WriteLine(GroundRelief.ParallaxActive ? "ground parallax on" : "ground parallax off");
                break;
            case Key.O: _render.Objects = !_render.Objects; break;
            case Key.F when _gpu?.Foliage is { } foliage:
                foliage.Enabled = !foliage.Enabled;
                Console.WriteLine(foliage.Enabled ? "foliage   on" : "foliage   off");
                break;
            case Key.X: _render.Wireframe = (_render.Wireframe + 1) % 3; break;
            case Key.V: _render.Debug = (_render.Debug + 1) % 4; break;
            case Key.G: _render.Water = !_render.Water; break;
            case Key.R: _render.Reflections = !_render.Reflections; break;
            case Key.Comma:
                if (_hour < 1) _day = Math.Max(_day - 1, 0);
                _hour = (_hour + 23) % 24;
                Console.WriteLine($"time {TimeText(_hour)}, day {_day}");
                break;
            case Key.Period:
                if (_hour + 1 >= 24) _day++;
                _hour = (_hour + 1) % 24;
                Console.WriteLine($"time {TimeText(_hour)}, day {_day}");
                break;
            // The weather schedule only moves forward (it catches up through a jump hour by hour); going back changes the clock, not the weather
            // already rolled.
            case Key.LeftBracket:
                _day = Math.Max(_day - 1, 0);
                Console.WriteLine($"time {TimeText(_hour)}, day {_day}");
                break;
            case Key.RightBracket:
                _day++;
                Console.WriteLine($"time {TimeText(_hour)}, day {_day}");
                break;
            case Key.BackSlash when _gpu?.Weather is { } weather: CycleWeather(weather.World); break;
            case Key.H: PrintCamera(); break;
            case Key.P: _screenshotRequested = true; break;
            case Key.Tab when _panel is not null: _panel.Visible = !_panel.Visible; break;
            case Key.F9: _fpsVisible = !_fpsVisible; break;
            case Key.F10 when _overlay is not null: _overlay.Visible = !_overlay.Visible; break;
            case Key.F11 when _overlay is not null:
                _statsVisible = !_statsVisible;
                FoliageGpuCull.CountTriangles = _statsVisible;
                break;
            case Key.F12 when shift && _profiler is not null && _overlay is not null: _profiler.ShowVram = !_profiler.ShowVram; break;
            case Key.F12 when _profiler is not null && _overlay is not null:
                _profiler.Showing = (FrameProfiler.Mode)(((int)_profiler.Showing + 1) % 3);
                break;
        }
    }

    /// <summary>
    /// Ctrl+C copies the camera's code to the clipboard; Ctrl+V moves the camera to the code the clipboard holds.
    /// </summary>
    void CopyOrPasteCamera(IKeyboard keyboard, Key key)
    {
        if (key == Key.C)
        {
            string code = CameraCode.Encode(_camera);
            keyboard.ClipboardText = code;
            Console.WriteLine($"camera    copied {code} ({CameraCode.Describe(_camera)})");
        }
        else if (CameraCode.TryApply(keyboard.ClipboardText, _camera))
        {
            Console.WriteLine($"camera    moved to {CameraCode.Describe(_camera)}");
        }
        else
        {
            Console.WriteLine("camera    the clipboard holds no camera code (Ctrl+C in a viewer copies one)");
        }
    }

    /// <summary>
    /// The H key: the camera's target, zone and angles, and the options that put a viewer there.
    /// </summary>
    void PrintCamera()
    {
        Console.WriteLine($"camera target {_camera.Target.X:0}, {_camera.Target.Y:0}, "
            + $"{_camera.Target.Z:0} (zone {WorldLayout.ZoneOf(_camera.Target.X, _camera.Target.Z)}), "
            + $"yaw {_camera.Yaw * 180 / MathF.PI:0}, pitch {_camera.Pitch * 180 / MathF.PI:0}, distance {_camera.Distance:0}; "
            + $"--at {_camera.Target.X:0},{_camera.Target.Z:0} --yaw {_camera.Yaw * 180 / MathF.PI:0} "
            + $"--pitch {_camera.Pitch * 180 / MathF.PI:0} --distance {_camera.Distance:0}");
    }

    /// <summary>
    /// The <c>\</c> key: the scheduler, then each WEATHER record forced in turn (back to the scheduler after the last).
    /// </summary>
    static void CycleWeather(WeatherWorld world)
    {
        var all = world.Data.Weathers;
        int next = world.ForcedWeather is { } current ? all.ToList().FindIndex(w => ReferenceEquals(w, current)) + 1 : 0;
        if (next >= all.Count)
        {
            world.ForceWeather((WeatherDef?)null);
            Console.WriteLine("weather   auto (the scheduler)");
        }
        else world.ForceWeather(all[next]);
    }

    #endregion

    #region Statistics

    /// <summary>
    /// The time since the statistics were last made, and the render thread's and the GPU's milliseconds and the longest frame over it.
    /// </summary>
    double _titleTimer, _cpuMs, _gpuMs, _frameMaxMs;

    /// <summary>
    /// The objects' running colour triangle total when the statistics were last made.
    /// </summary>
    long _lastObjectTris;

    /// <summary>
    /// The frames drawn and the GPU timings read since the statistics were last made.
    /// </summary>
    int _frames, _gpuSamples;

    /// <summary>
    /// Each update: the held keys fly the camera, and four times a second the statistics and the window title are made again.
    /// </summary>
    void OnUpdate(double dt)
    {
        if (_keyboard is null || _camera is null) return;
        FlyCamera(_keyboard, _camera, dt);
        _titleTimer += dt;
        if (_titleTimer > 0.25 && _gpu is not null) UpdateStats(_gpu);
    }

    /// <summary>
    /// Free camera: W/S along the view direction, A/D sideways, Q/E world down/up.
    /// </summary>
    static void FlyCamera(IKeyboard keyboard, WorldCamera camera, double dt)
    {
        bool Down(Key k) => keyboard.IsKeyPressed(k);
        // Free camera: W/S along the view direction, A/D sideways, Q/E world down/up; speed grows with the height
        // above the ground (Shift ×4, Ctrl ×0.25).
        float speed = (float)dt * Math.Max(camera.EyeClearance, 30) * 1.5f *
            (Down(Key.ShiftLeft) || Down(Key.ShiftRight) ? 4f : 1f) * (Down(Key.ControlLeft) || Down(Key.ControlRight) ? 0.25f : 1f);
        float f = (Down(Key.W) ? 1 : 0) - (Down(Key.S) ? 1 : 0);
        float r = (Down(Key.D) ? 1 : 0) - (Down(Key.A) ? 1 : 0);
        float u = (Down(Key.E) ? 1 : 0) - (Down(Key.Q) ? 1 : 0);
        if (f != 0 || r != 0 || u != 0) camera.FlyFree(f * speed, r * speed, u * speed);
    }

    /// <summary>
    /// The F11 statistics and the window title's frame rate, over the frames since the last time; the counts start again.
    /// </summary>
    void UpdateStats(WorldGpu gpu)
    {
        var t = _camera.Eye;
        // fps is capped by vsync when it is on (Tab panel); cpu is the time to record a frame, gpu the time the GPU spent on it (timer
        // queries), so they show the real cost under the cap.
        string gpuText = _gpuSamples > 0 ? $"{_gpuMs / _gpuSamples:0.00}" : "-";
        _stats.Clear();
        // frame: the wall time from one frame to the next (mean and the longest since the last update), what the fps is made of.
        _stats.Add("Performance");
        _stats.Add($"  frame rate    {_frames / _titleTimer:0} fps, {(_display.VSync ? "vsync" : "uncapped")}; "
            + $"frame {_titleTimer * 1000 / Math.Max(_frames, 1):0.0} ms (longest {_frameMaxMs:0.0}), " +
            $"cpu {_cpuMs / Math.Max(_frames, 1):0.0} ms, graphics card {gpuText} ms");
        _frameMaxMs = 0;
        // The window title carries the frame rate too.
        _window.Title = $"Meitou world ({WorldApp.RendererName(_o)}) | {_frames / _titleTimer:0} fps";
        // Triangles of the main view (not the shadows); the objects' from their running colour total.
        long objectTotal = gpu.Objects?.Totals[0, 1] ?? 0;
        int counted = Math.Max(_frames, 1);
        var tris = new WorldStats.Triangles(gpu.Terrain.DrawnTriangles, (objectTotal - _lastObjectTris) / (double)counted,
            gpu.Foliage is { Enabled: true } ft ? ft.ColourTrianglesPerFrame : 0, gpu.Characters?.DrawnTriangles
                    ?? 0, gpu.Foliage is { Enabled: true } fg ? fg.DrawnBlades : 0);
        gpu.Foliage?.ResetDrawTally();
        _lastObjectTris = objectTotal;
        WorldStats.Add(_stats, _display.Context, gpu, t, in tris);
        _titleTimer = 0;
        _frames = 0;
        _cpuMs = _gpuMs = 0;
        _gpuSamples = 0;
    }

    #endregion

    #region Frame

    /// <summary>
    /// The wall time from one frame to the next; the render thread's time recording a frame.
    /// </summary>
    readonly Stopwatch _frameClock = new(), _frameWatch = new();

    /// <summary>
    /// The GPU time of a frame: a native timestamp before and after it (QueryArena), read a frame ring later without waiting.
    /// </summary>
    readonly (QuerySlot Begin, QuerySlot End)[] _timers = new (QuerySlot Begin, QuerySlot End)[4];

    /// <summary>
    /// Which of <see cref="_timers"/> wait for their timestamps.
    /// </summary>
    readonly bool[] _timerPending = new bool[4];

    /// <summary>
    /// The <see cref="_timers"/> slot this frame uses.
    /// </summary>
    int _queryIndex;

    /// <summary>
    /// The F9 counter's text.
    /// </summary>
    string _fpsText = "";

    /// <summary>
    /// The F9 counter's window so far: its milliseconds.
    /// </summary>
    double _fpsWindowMs;

    /// <summary>
    /// The F9 counter's window so far: its frames.
    /// </summary>
    int _fpsWindowFrames;

    /// <summary>
    /// A frame: the GPU timings, the clocks, the world, then the panels, the present and a saved picture.
    /// </summary>
    void OnRender(double renderDt)
    {
        if (_gpu is null) return;
        var size = _window.FramebufferSize;
        bool began = _display.BeginFrame(size.X, size.Y);
        if (!began) return; // minimized, or not yet shown at its maximized size
        var context = _display.Context;
        // The chain and the overlays draw into the window's backbuffer (made again when the size changes).
        var backbuffer = _display.Backbuffer!;
        if (_gpu.Post is { } windowPost) windowPost.Target = backbuffer;
        if (_overlay is not null) _overlay.Target = backbuffer;
        var arena = context.Frame.Timestamps;
        CollectGpuTimes(context, arena);
        bool timing = BeginGpuTime(context, arena);
        _frameWatch.Restart();
        _profiler?.BeginFrame();
        if (_meitou.TickSweep() is { } sweepLine) Console.WriteLine(sweepLine);   // Ctrl+F1: the switches flip one a second
        double frameDt = AdvanceClock(renderDt);
        string? takeFile = _cinema.TakeFrame();
        // Game speed 1, never paused: the viewer's time is the day and the hour set here.
        if (_gpu.Weather is { } clockWeather) clockWeather.Day = _day;
        Draw(_gpu, _scene, _camera, _render, size.X, size.Y, _hour, (float)_animSeconds / 600f, _o.FogDistance);
        if (_audio is not null) UpdateAudio(_audio, _gpu, _scene, _camera, _hour, frameDt);
        _cpuMs += _frameWatch.Elapsed.TotalMilliseconds;
        if (timing) EndGpuTime(context, arena);
        _queryIndex = (_queryIndex + 1) % _timers.Length;
        _frames++;
        _frameMaxMs = Math.Max(_frameMaxMs, _frameClock.Elapsed.TotalMilliseconds);
        CountFps();
        _frameClock.Restart();
        PresentFrame(size, backbuffer, takeFile);
    }

    /// <summary>
    /// Collect finished GPU timings from earlier frames without waiting for them (one that never comes, its frame long gone, is dropped).
    /// </summary>
    void CollectGpuTimes(GpuContext context, QueryArena arena)
    {
        for (int i = 0; i < _timers.Length; i++)
        {
            if (!_timerPending[i]) continue;
            if (arena.TryRead(_timers[i].Begin, out ulong begin) && arena.TryRead(_timers[i].End, out ulong end))
            {
                _gpuMs += (end - begin) / 1e6;
                _gpuSamples++;
                _timerPending[i] = false;
            }
            else if (context.Frame.Number - _timers[i].End.Frame > 8) _timerPending[i] = false;
        }
    }

    /// <summary>
    /// The timestamp before the frame, when this frame's slot is free and the arena has room; whether the frame is timed.
    /// </summary>
    bool BeginGpuTime(GpuContext context, QueryArena arena)
    {
        if (_timerPending[_queryIndex]) return false;
        var begin = arena.Allocate();
        if (!begin.IsValid) return false;
        context.Interleave(cmd => cmd.Timestamp(arena, begin));
        _timers[_queryIndex].Begin = begin;
        return true;
    }

    /// <summary>
    /// The timestamp after a timed frame; its slot then waits for both.
    /// </summary>
    void EndGpuTime(GpuContext context, QueryArena arena)
    {
        var end = arena.Allocate();
        if (end.IsValid)
        {
            context.Interleave(cmd => cmd.Timestamp(arena, end));
            _timers[_queryIndex].End = end;
            _timerPending[_queryIndex] = true;
        }
    }

    /// <summary>
    /// The frame's time step: the animation clock, the hour with <c>--time-speed</c>, and the cinema's flight.
    /// </summary>
    double AdvanceClock(double renderDt)
    {
        double frameDt = _cinema.FrameTime(renderDt);
        _animSeconds += frameDt;
        if (_o.TimeSpeed > 0)
        {
            // --time-speed: the clock runs on, game hours per real minute, into the next day past midnight.
            _hour += _o.TimeSpeed * (float)frameDt / 60f;
            while (_hour >= 24)
            {
                _hour -= 24;
                _day++;
            }
        }
        _cinema.Advance(frameDt);   // a flight through the saved shots moves the camera (and the hour)
        return frameDt;
    }

    /// <summary>
    /// The counter: the mean over the last tenth of a second, so a single late frame doesn't read as a drop; then a frame's work (its time
    /// without the waits) and its whole time in brackets.
    /// </summary>
    void CountFps()
    {
        _fpsWindowMs += _frameClock.Elapsed.TotalMilliseconds;
        _fpsWindowFrames++;
        if (_fpsWindowMs >= 100)
        {
            _fpsText = $"{_fpsWindowFrames * 1000 / _fpsWindowMs:0} fps  ({_fpsWindowMs / _fpsWindowFrames:0.0} ms)";
            _fpsWindowMs = 0;
            _fpsWindowFrames = 0;
        }
    }

    /// <summary>
    /// The panels over the frame, the present, then the picture saved when one was asked for or the cinema records.
    /// </summary>
    void PresentFrame(Vector2D<int> size, GpuTexture backbuffer, string? takeFile)
    {
        // The picture is read after the present (framebuffer 0 stays intact until the next frame); the frame that is saved is drawn without the
        // overlay and the panel, so saved pictures never show them. A recorded frame is drawn like a screenshot; the cinema's clean picture hides
        // the panels too (its letterbox stays in saved frames, the F6 editor stays open).
        bool shot = _screenshotRequested || takeFile is not null;
        _screenshotRequested = false;
        bool panels = !shot && !_cinema.Clean;
        DrawPanels(size, panels);
        // Over everything: the letterbox, and the timeline editor (F6) or the cinema status line.
        if (_overlay is not null) _cinema.Draw(_overlay, size.X, size.Y, shot);
        StageClock.Phase("overlays");
        _display.Present();
        _profiler?.EndFrame();
        SmokeTest.Frame();
        SaveShot(size, backbuffer, takeFile, shot);
    }

    /// <summary>
    /// The F9 counter and the F11 statistics at the top left, the key list below them, the profiler, then the settings panel.
    /// </summary>
    void DrawPanels(Vector2D<int> size, bool panels)
    {
        // The statistics at the top left, the key list below them.
        float panelsBottom = 0;
        if (panels && _fpsVisible && _overlay is not null && _fpsText.Length > 0)
        {
            float w = _fpsText.Length * _overlay.CharWidth + 12, h = _overlay.LineHeight + 8;
            _overlay.Rect(8, 8, 8 + w, 8 + h, DebugOverlay.PanelColour);
            _overlay.Text(_fpsText, 14, 12, DebugOverlay.TextColour);
            _overlay.Flush(size.X, size.Y);
            panelsBottom = h;   // the panels add their own 16 px margin
        }
        if (panels && _statsVisible && _overlay is not null && _stats.Count > 0)
            panelsBottom = _overlay.Panel(size.X, size.Y, $"Meitou world ({WorldApp.RendererName(_o)})   (F11 hides this)", _stats, panelsBottom);
        if (panels && _overlay is { Visible: true })
        {
            // Each item with its current state, aligned in a column.
            int width = _keyItems.Max(i => i.Length) + 2;
            var lines = _keyItems.Select(item => KeyState(item.Split(' ')[0]) is { } state ? item.PadRight(width) + state : item).ToList();
            _overlay.Draw(size.X, size.Y, "Keys   (F10 hides this)", lines, panelsBottom);
        }
        if (panels && _overlay is not null)
        {
            _profiler?.Draw(_overlay, size.X, size.Y);
            _profiler?.DrawVram(_overlay, size.X, size.Y);
        }
        // Last, over the statistics and the profiler: the settings sit at the top left.
        if (panels) _panel?.Draw(size.X, size.Y);
    }

    /// <summary>
    /// The cinema's recorded frame into its file, or a screenshot (P) into C:\Temp.
    /// </summary>
    void SaveShot(Vector2D<int> size, GpuTexture backbuffer, string? takeFile, bool shot)
    {
        if (takeFile is not null)
        {
            FramebufferCapture.SavePng(_display.Context, backbuffer, takeFile, size.X, size.Y);
            _cinema.FrameDone();
        }
        else if (shot)
        {
            // Into C:\Temp (the user's screenshot folder), never the working directory (which may be the repo).
            var file = Path.Combine(Directory.CreateDirectory(@"C:\Temp").FullName, $"meitou-world-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            FramebufferCapture.SavePng(_display.Context, backbuffer, file, size.X, size.Y);
            Console.WriteLine($"saved {Path.GetFullPath(file)}");
        }
    }

    #endregion
}
