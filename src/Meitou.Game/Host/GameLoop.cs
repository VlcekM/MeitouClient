using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Game.DevTools;
using Meitou.Game.Host.Cameras;
using Meitou.Game.Host.Input;
using Meitou.Game.Host.Photo;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Display;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Terrain;
using Meitou.Game.Rendering.Upscalers;
using Meitou.Game.Settings;
using Meitou.Game.Tutorials;
using Meitou.Game.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Session;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using EngineButton = Meitou.Game.Host.Input.MouseButton;
using EngineKey = Meitou.Game.Host.Input.Key;
using SilkButton = Silk.NET.Input.MouseButton;
using SilkKey = Silk.NET.Input.Key;

namespace Meitou.Game.Host;

/// <summary>
/// The interactive run on the Vulkan display, windowed or headless (docs/engine.md "Host and loading"): the window and the device, the world loaded
/// behind the loading screen, the settings panel, the keyboard and mouse, then one <see cref="Frame"/> after another until the window closes or the
/// game quits.
/// </summary>
sealed class GameLoop(GameHost host)
{
    // The display and its window (none headless: --headless draws into an offscreen picture), and the frame rate as this run started.
    #region State

    VulkanDisplay _display = null!;
    IWindow? _window;
    bool _headless, _vsync;
    int _fpsLimit, _initialFpsLimit;
    bool _savedVSync, _savedFullScreen;

    /// <summary>
    /// Read at start-up only: a change in the settings waits for a restart.
    /// </summary>
    FrameGenerationMethod _frameGenerationAtStart;
    bool _hdrAtStart;

    // The debug overlay the interface draws through, the Faithful / Meitou switches and the settings panel (null without an overlay).
    DebugOverlay? _overlay;
    IReadOnlyList<Enhancement> _switches = [];
    MeitouToggle _meitou = null!;
    SettingsMenu? _panel;

    /// <summary>
    /// --low-end (WorldOptions.LowEnd) and --benchmark-baseline are presets for this run: the saved sliders and switches would undo them, so they are
    /// left as saved.
    /// </summary>
    bool _lowEnd;

    /// <summary>
    /// The keys this run leaves as saved (--bloom).
    /// </summary>
    HashSet<string> _runOnly = [];

    // The cinematic camera and the squad tour; while either holds the drawn camera the rig's state is not applied.
    Cinema _cinema = null!;

    /// <summary>The settings window, once made (the agent channel's <c>gfx</c>).</summary>
    internal SettingsMenu? Panel => _panel;

    bool _cinemaHolds;

    /// <summary>
    /// Photo mode (docs/viewer.md "Photo mode"): while it is on it owns the camera, the interface's visibility and the pause.
    /// </summary>
    PhotoMode _photo = null!;
    SquadTour _tour = null!;
    bool _tourFollows;
    bool _cinemaWasPlaying, _cinemaWasRecording;

    // The keyboards and mice (none headless), and the pointer's state between their events.
    Silk.NET.Input.IInputContext? _silkInput;
    IReadOnlyList<Silk.NET.Input.IKeyboard> _keyboards = [];
    IReadOnlyList<Silk.NET.Input.IMouse> _mice = [];
    string? _headlessClipboard;
    Vector2? _lastMouse;
    Vector2? _appliedHeadlessMouse;

    /// <summary>
    /// Rotating pins the pointer. Windows and macOS put it back after every move (HoldPointer); Linux locks it (GLFW's disabled cursor), because
    /// putting it back over-counts the movement there: - Wayland: the pointer is never put back (glfwSetCursorPos is unavailable), so each move
    /// counts the whole distance from the start. - X11 (native and XWayland): moves are counted more than once and the pointer jitters (Observed
    /// 2026-10-25). While locked, the game treats the pointer as at <see cref="_orbitAnchor"/>.
    /// </summary>
    bool _lockOrbit;
    Vector2? _orbitAnchor;
    Silk.NET.Input.CursorMode _windowCursor = Silk.NET.Input.CursorMode.Normal;
    Silk.NET.Input.CursorMode _cursorBeforeOrbit = Silk.NET.Input.CursorMode.Normal;
    bool _panelDrag, _shiftDown, _profilerClick;
    bool _highlightHeld;
    bool _windowFocused = true;

    /// <summary>
    /// Silk raises KeyDown once per press (no OS key repeat), so a held erase or caret key in a text field repeats here: after 0.5 s, then 30 times a
    /// second (the Windows defaults). Typed characters already repeat through KeyChar.
    /// </summary>
    (Silk.NET.Input.IKeyboard Keyboard, SilkKey Key, long Next)? _heldEditKey;

    // The loop's clock and end.
    Stopwatch _frame = null!;
    double _last;
    int _totalFrames;
    bool _quit;

    // What a frame draws into: the interface's layer, frame generation (made on the first frame), the headless backbuffer.
    UiLayer? _uiLayer;
    IFrameGenerator? _frameGen;
    readonly FrameGenerationSetup.SystemPointer _systemPointer = new();
    bool _frameGenTried;
    GpuTexture? _headlessTarget;

    // The fps counter (the mean over a tenth of a second) and F11's statistics (once a second); the triangle counts summed over the statistics'
    // second.
    readonly List<string> _stats = [];
    string _fpsText = "";
    double _fpsWindow;
    int _fpsWindowFrames;
    double _drawTotal, _gpuSum, _statsTimer;
    int _gpuSamples, _statsFrames;
    long _statsTicks;
    double _terrainTris, _characterTris, _grassBlades;
    long _objectTris0;

    #endregion

    #region Run and window

    /// <summary>
    /// What the mouse is over in a frame, decided once before the world is drawn: the pointer (drawn last) and the hover outline (drawn with the
    /// world) agree.
    /// </summary>
    readonly record struct FramePointer(Silk.NET.Input.IMouse? Mouse, Vector2 At, bool OverWorld, bool FreeLook);

    /// <summary>
    /// The world's files read on a worker since <see cref="OpenDisplay"/>, when the host owns the scene.
    /// </summary>
    Task<WorldScene?>? _sceneRead;

    internal int Run()
    {
        using var owned = OpenDisplay();
        ShowWindow();
        if (LoadWorld() is { } exit) return exit;
        CreateDebugTools();
        // --log-spikes: the run's allocations by type (the runtime's samples, one per ~100 KB), printed with the summary.
        using var spikeGc = SpikeLog.Enabled ? new BenchGcListener() : null;
        CreateSettingsPanel();
        ApplyUserConfig();
        ApplySavedGraphics();
        host.AgentChannel.SettingsPanel = _panel;
        host.AgentChannel.InitControl();
        SetUpCameraFlights();
        ConnectInput();
        Console.WriteLine(GameOptions.Usage[GameOptions.Usage.IndexOf("Keys:", StringComparison.Ordinal)..]);
        Console.WriteLine($"display   vsync {(_vsync ? "on" : "off")}, frame limit {(_vsync || _fpsLimit <= 0 ? "none" : _fpsLimit + " fps")}, control tick {host.Session.Ticks.TickRate:0} Hz, world tick {SimulationClock.TickRate:0} per game second; settings in {host.Config.Path}");

        _frame = Stopwatch.StartNew();
        ConnectGameEvents();
        while (_window?.IsClosing != true && !_quit)
        {
            if (!Frame()) break;
        }

        ShutDown(spikeGc);
        return host.AgentChannel.ControlExitCode;
    }

    // ---- start-up ----

    /// <summary>
    /// The display, its window hidden while the device is made; the audio and the world's files start on workers meanwhile.
    /// </summary>
    VulkanDisplay OpenDisplay()
    {
        _headless = host.WorldOptions.Headless;   // --headless: no window, no swapchain, no input devices; the loop draws into an offscreen picture
        _vsync = !_headless && (host.GameOptions.VSync ?? host.Config.VSync);
        _fpsLimit = host.GameOptions.FpsLimit ?? (_headless ? 30 : host.Config.FpsLimit);
        _initialFpsLimit = _fpsLimit;
        VulkanDisplay.RayTracing = host.WorldOptions.WantsRayTracing;
        // The game's methods compiled ahead, from now on (with code mods: once they are initialized), behind the loading.
        if (!host.GameOptions.NoPrecompile) host.Modding.StartWarmup();
        // The world's files are read on a worker from here on: while the window and the device are made, then behind the loading screen.
        _sceneRead = host.LoadScene is { } readScene ? Task.Run(readScene) : null;
        host.Sound.BeginAudio();
        _hdrAtStart = host.GameOptions.Hdr ?? host.Config.Hdr;   // read at start-up only
        // Read at start-up only; HDR output keeps the plain swapchain.
        _frameGenerationAtStart = _hdrAtStart ? FrameGenerationMethod.Off : host.Config.FrameGenerationMethod;

        // Load before the device so the settings panel can switch to DLSS without restarting.
        _display = new VulkanDisplay(_headless ? null : WindowOptions.Default with
        {
            Size = new Vector2D<int>(host.WorldOptions.Width, host.WorldOptions.Height),
            Title = "MeitouClient",
            // Hidden while the device is made (a second or two in which nothing can be drawn), shown maximised with the first loading frame.
            IsVisible = false,
            FramesPerSecond = 0,
            UpdatesPerSecond = 0,
        }, _vsync, dlss: true, frameGeneration: _headless ? FrameGenerationMethod.Off : _frameGenerationAtStart, hdr: _hdrAtStart);
        host.Streamline = _display.Streamline;
        host.Ngx = _display.Ngx;
        _window = _headless ? null : _display.Window!;
        return _display;
    }

    /// <summary>
    /// Sizes the window while hidden, starts the loading screen (or the Faithful splash) and shows the window with the first loading frame in it.
    /// </summary>
    void ShowWindow()
    {
        // X11 opens it where Silk.NET asks (50,50), the desktop's corner, which can be another monitor (Observed 2026-10-26, XWayland): on the
        // primary monitor instead, the one full screen uses; leaving full screen puts it back where it was when full screen began.
        if (_window?.Native?.X11 is not null) BorderlessFullScreen.MoveToPrimaryMonitor(_window);
        // Borderless full screen as the viewer starts (Alt+Enter and the Tab panel switch it); --windowed keeps a window for
        // this run: the size asked by --size, else maximised. Sized while still hidden: resizing after it shows (maximised,
        // then borderless) flashed white and black before the first frame.
        bool fullScreen = host.Config.FullScreen && !host.WorldOptions.Windowed && !_headless;
        (_savedVSync, _savedFullScreen) = (_vsync, fullScreen);
        if (_window is null) { }
        else if (fullScreen) BorderlessFullScreen.Toggle(_window);
        else if (!host.WorldOptions.SizeGiven && !host.WorldOptions.HiddenWindow)
            _window.WindowState = WindowState.Maximized;   // maximising would show a hidden window
        host.BeginLoadingScreen(_display);   // its textures and fonts are made before the window shows
        // The Faithful start shows the original's splash, as the game does before its title, instead of the loading screen (unless the Interface
        // setting turns it off).
        if (host.Loading is { Faithful: true } && host.Config.SplashScreens && host.OpensOnTitle(interactive: true) && !_headless)
            host.Loading.StartSplash();
        // The first loading frame is in the swapchain when the window shows, and on screen before the splash closes. Not on Wayland: a window gets
        // its shell role when it shows, and a buffer attached to the surface before that makes the compositor drop the client ("attached a buffer
        // before configure event"), so there the window shows first and takes the compositor's configure before anything is drawn. --hidden-window:
        // drawn and presented as usual, never shown (an unattended run).
        if (_window is null) { }
        else if (host.WorldOptions.HiddenWindow) host.LoadingFrame();
        else if (_window.Native?.Wayland is not null)
        {
            _window.IsVisible = true;
            _window.DoEvents();
        }
        else
        {
            host.LoadingFrame();
            _window.IsVisible = true;
        }
        host.LoadingFrame();
        host.Splash?.Dispose();
    }

    /// <summary>
    /// The scene and the boot under the loading screen; the exit code when the run ends here (the window closed, the scene failed), else null.
    /// </summary>
    int? LoadWorld()
    {
        try
        {
            if (_sceneRead is not null)
            {
                host.LoadingStep("scene");
                if (host.AwaitLoading(_sceneRead) is not { } loaded) return 1;
                host.Scene = host.OwnedScene = loaded;
            }
            host.Boot(_display, interactive: true);
            host.Sound.AttachAudio();
            host.BeginWarm();
            return null;
        }
        catch (GameHost.LoadingCancelledException)
        {
            Console.WriteLine("loading   cancelled: the window was closed");
            host.EndLoadingScreen();
            return 0;
        }
    }

    #endregion

    #region Settings

    /// <summary>
    /// The overlay the interface draws through, the portraits' texture, and the debug overlays (docs/engine.md): F10 the debug menu, F11 the
    /// statistics, F12 the profiler.
    /// </summary>
    void CreateDebugTools()
    {
        _overlay = DebugOverlay.TryCreate(_display.Context, host.Assets.Find);
        if (_overlay is not null)
        {
            host.Player.CreateUiPainter(_overlay, host.Assets.Find);
            host.ItemIconFiles.ConnectItemIcons();
        }
        if (host.Gpu.Characters is { } portraits)
        {
            var (atlas, sampled) = portraits.PortraitAtlas;
            host.Player.SetUiTexture(PortraitBar.PortraitTexture, atlas, sampled);
        }
        host.DebugMenu.DebugToolsOn = true;
        host.DebugMenu.Profiler = _overlay is null ? null : new FrameProfiler(_display.Context, () => _display.Context.GpuFrameMs);
    }

    /// <summary>
    /// The Faithful / Meitou switches (Shift+F1 turns Meitou off and back on; plain F2..F4 and F8 are the game's speed and screenshot keys) and the
    /// settings panel, which keeps the shared rendering settings' callbacks and persistence keys with the game's own modal layout.
    /// </summary>
    void CreateSettingsPanel()
    {
        _switches = WorldFrame.LiveSwitches(host.WorldOptions, () => host.Gpu, () => host.Camera, () => host.Render);
        _meitou = new MeitouToggle(_switches);
        _lowEnd = Environment.GetCommandLineArgs().Contains("--low-end") || host.GameOptions.BenchmarkBaseline;
        _runOnly = GameHost.RunOnlyKeys();
        if (_overlay is null) return;
        var values = WorldFrame.CreateSettingsPanel(_overlay, host.Gpu, host.Render,
            getHour: () => (float)host.Session.Clock.HourOfDay,
            setHour: v => { host.Session.Clock.SetHourOfDay(v); if (host.Duel.IsDuel) host.Duel.HeldHour = v; },
            getVSync: () => _display.VSync, setVSync: v => _display.VSync = v, switches: _switches,
            getFullScreen: FullScreenOn, setFullScreen: _ => ToggleFullScreen(),
            extraChoices: [new PanelChoice(() => $"UI mode: {host.Player.UiMode}", () => ["Faithful", "Meitou"], () => (int)host.Player.UiMode,
                index =>
                {
                    host.Player.SetUiMode(index switch { 0 => Data.Ui.UiMode.Faithful, 1 => Data.Ui.UiMode.Meitou,
                        _ => throw new ArgumentOutOfRangeException(nameof(index)) });
                    return $"UI mode: {host.Player.UiMode}";
                })]);
        var frameLimit = new Slider("Frame limit", 0, 360, () => _fpsLimit, v => _fpsLimit = (int)MathF.Round(v / 5) * 5, "0", Text: v => v <= 0 ? "Unlimited" : $"{v:0} fps");
        _panel = new SettingsMenu(host.Player, values, host.Session.Bindings, SaveSettings, frameLimit, host.Config)
        {
            UpscalerStatus = () => host.Gpu.Post?.ActiveUpscaler ?? "Off",
            // The loaded frame generation's state; a change it cannot follow shows as pending until the restart.
            FrameGenerationStatus = () => FrameGenerationSetup.FrameGenerationState(_display, _frameGenerationAtStart,
                host.Config.FrameGenerationMethod),
            ModSettings = () => host.Modding.Mods?.Settings ?? [],
            AudioChanged = host.Sound.AudioSettingsChanged,
            GameplayChanged = host.Saves.ApplyGameplay,
            SkinRaceList = host.Scene.Database is { } raceDb ? CharacterRenderer.PersonRaces(raceDb) : [],
            SkinRacesChanged = () => { if (host.Gpu.Characters is { } chars) chars.SkinRaces = host.Config.SkinRaces; },
            CameraRotateSpeedChanged = ApplyCameraRotateSpeed,
            CameraChanged = () => host.Config.ApplyCamera(host.Session.Camera.Settings),
            TutorialsChanged = on => { if (host.Saves.Tutorials is { } t) t.Enabled = on; },
            ResetTutorials = () => host.Saves.Tutorials?.Reset(),
            // The settings that reach into the running world's systems.
            AttackSlotsChanged = slots => EachSystem<Simulation.Combat.CombatSystem>(combat => combat.UseAttackSlots(slots)),
            LateDefencesChanged = on => EachSystem<Simulation.Combat.CombatSystem>(combat => combat.LateDefences = on),
            GunnerFacingChanged = on => EachSystem<Simulation.Combat.CombatSystem>(combat => combat.GunnerFacesBarrel = on),
            PunchReachChanged = on => EachSystem<Simulation.Combat.CombatSystem>(combat => combat.PunchReachFix = on),
            BenchFacingChanged = on => host.Session.World.BenchFacingFix = on,
            TimeSpeedsChanged = () => host.Session.Simulation.SetFastSpeeds(host.Config.FastSpeed, host.Config.FastestSpeed),
            DoorsCloseChanged = on => EachSystem<Simulation.Locks.LockSystem>(locks => locks.DoorsCloseBehind = on),
            AnimalsPickUpChanged = on => EachSystem<Simulation.Player.PlayerSystem>(players => players.AnimalsPickUp = on),
            OffscreenChanged = ApplyOffscreenSettings,
        };
    }

    void EachSystem<T>(Action<T> act)
    {
        foreach (var system in host.Session.World.Systems.OfType<T>()) act(system);
    }

    /// <summary>
    /// The offscreen world's settings into the population and production systems.
    /// </summary>
    void ApplyOffscreenSettings()
    {
        foreach (var population in host.Session.World.Systems.OfType<Simulation.Population.PopulationSystem>())
        {
            (population.KeepRoamers, population.OffscreenFights, population.OffscreenHealing) = (host.Config.KeepRoamers, host.Config.OffscreenFights, host.Config.OffscreenHealing);
            (population.CampaignAncestorsOnce, population.KeepWild, population.ResidentSchedule) = (host.Config.CampaignAncestorsOnce, host.Config.KeepWild, host.Config.ResidentSchedule);
        }

        foreach (var production in host.Session.World.Systems.OfType<Simulation.Production.ProductionSystem>()) production.UnloadedProduction = host.Config.UnloadedProduction;
    }

    void ApplyCameraRotateSpeed(float speed) => host.Session.Camera.Settings.RotateSpeedX = host.Session.Camera.Settings.RotateSpeedY =
        Math.Clamp(speed, UserConfig.MinCameraRotateSpeed, UserConfig.MaxCameraRotateSpeed);

    /// <summary>
    /// The camera, mouse, font, markers and tutorials as the user set them (the install's settings.cfg where meitou.user.json has none).
    /// </summary>
    void ApplyUserConfig()
    {
        ApplyCameraRotateSpeed(host.Config.CameraRotateSpeed);
        host.Config.EdgeScroll ??= Audio.AudioSystem.Setting(host.Install, "Edge scrolling", 1) != 0;
        host.Config.Tutorials ??= Audio.AudioSystem.Setting(host.Install, "tutorials", 1) != 0;
        host.Config.SeedCamera(host.Install);
        host.Config.ApplyCamera(host.Session.Camera.Settings);
        KeyMap.SwapButtons = host.Config.SwapMouseButtons == true;
        host.Player.FontSize = host.Config.FontSize ?? Data.Ui.KenshiFontSize.DefaultSetting;
        host.Config.MovementMarker ??= Audio.AudioSystem.Setting(host.Install, "Movement marker", 1) != 0;
        host.Config.RotationMarker ??= Audio.AudioSystem.Setting(host.Install, "Rotation marker", 1) != 0;
        (host.Player.MovementMarker, host.Player.RotationMarker) = (host.Config.MovementMarker == true, host.Config.RotationMarker == true);
        host.Saves.Tutorials ??= new TutorialManager(Path.Combine(Directory.GetCurrentDirectory(), TutorialManager.FileName));
        host.Saves.Tutorials.Enabled = host.Config.Tutorials == true;
        host.Player.TutorialWindow.ConfigureTutorials(host.Saves.Tutorials);
    }

    /// <summary>
    /// The saved sliders, the look the command line chose, then the saved switches and preset (unless the command line chose them).
    /// </summary>
    void ApplySavedGraphics()
    {
        if (_panel is not null && !_lowEnd)
            foreach (var slider in _panel.Sliders)
                // the saved one was applied before the boot (Program)
                if (!_runOnly.Contains(slider.Label) && host.Config.Graphics.TryGetValue(slider.Label, out float v)
                        && !(host.WorldOptions.Post.Upscale.Explicit && WorldFrame.UpscalerSliders.Contains(slider.Label))
                        && !(slider.Label == WorldFrame.TextureQualitySlider && Environment.GetCommandLineArgs().Contains("--texture-quality")))
                    slider.Set(Math.Clamp(v, slider.Min, slider.Max));
        // The look as the command line chose it: Faithful or Meitou for "all", else read from the switches (not saved).
        if (CommandLineLook() is var (given, faithfulLook) && given) host.Config.FaithfulLook = faithfulLook;
        // The switches as saved, then the saved preset (one the launcher chose), unless the command line chose them (--meitou / --faithful):
        // switches it moved would read as another preset.
        if (_panel is not null && !_lowEnd && !Environment.GetCommandLineArgs().Any(a => a is "--meitou" or "--faithful"))
        {
            foreach (var toggle in _panel.Toggles)
                if (!LookPresets.HiddenSwitch(toggle.Label) && !_runOnly.Contains(SwitchKey(toggle.Label))
                        && host.Config.Graphics.TryGetValue(SwitchKey(toggle.Label), out float v) && (v >= 0.5f) != toggle.Get())
                    toggle.Set(v >= 0.5f);
            // The technique choices after the switches they stand for: the launcher writes the choice alone, and it wins (a file of switches only keeps them).
            foreach (var choice in _panel.Sliders.Where(s => WorldFrame.ChoiceSliders.Contains(s.Label)))
                if (host.Config.Graphics.TryGetValue(choice.Label, out float c) && MathF.Round(c) != MathF.Round(choice.Get())) choice.Set(Math.Clamp(c, choice.Min, choice.Max));
            _panel.Tiers.ApplySavedPreset();
        }
        if (host.GameOptions.BenchmarkBaseline) _panel?.Tiers.ApplyBenchmarkBaseline();
    }

    #endregion

    #region Camera tour

    /// <summary>
    /// The cinematic camera (docs/viewer.md "Cinematic camera", F6 its editor): while it flies, previews or cuts to a key it holds the drawn camera
    /// and the rig's state is not applied; the first camera input after that hands the camera back to the rig (CameraRig.TakeOver). The hour is the
    /// game clock's (time does not follow the keys); Home puts the lens back while the editor is open, in the free camera and on a camera the cinema
    /// holds. Numpad / flies the camera across the squad in portrait order (docs/viewer.md "Squad tour"), and a double click on a far squad member
    /// flies there; both hold the camera as the cinema does.
    /// </summary>
    void SetUpCameraFlights()
    {
        _cinema = new Cinema(() => host.Camera, () => (float)host.Session.Clock.HourOfDay, null, FullScreenOn, ToggleFullScreen,
            Cinema.GameFile, lensKeysAlways: host.Duel.IsDuel);   // the duel has no floors: PageUp/PageDown zoom the lens there
        _cinema.Moved = () => _cinemaHolds = true;
        // PageUp/PageDown zoom the lens and Home puts it back in these cameras; in the strategy camera PageUp/PageDown are the game's floor keys.
        _cinema.LensKeys = () => host.Session.Camera.IsFree || _cinemaHolds;
        _cinema.Load();
        _tour = new SquadTour();
        _photo = host.Photo = new PhotoMode(host)
        {
            Allowed = PhotoAllowed,
            Entering = () => _cinemaHolds = false,
        };
        // A double click on a squad member more than FlightDistance from the view flies there (Advanced settings "Character flights").
        host.Player.FlyTo = id =>
        {
            if (!host.Config.CharacterFlights || host.Duel.IsDuel || host.Session.Camera.IsFree || _cinema.Playing || _cinema.Recording
                    || TourPosition(id) is not { } at)
                return false;
            if (new Vector2(at.X - host.Camera.Target.X, at.Z - host.Camera.Target.Z).Length() <= UserConfig.FlightDistance) return false;
            host.Player.Follow = null;
            _tour.HopSeconds = host.Config.CharacterFlightSeconds;
            _tour.Fly(host.Camera, id, TourPosition);
            (_cinemaHolds, _tourFollows) = (true, true);
            return true;
        };
        // Pressing the member again while the flight is under way (a triple tap) cuts straight to the end of it.
        host.Player.SkipFlight = id =>
        {
            if (!_tour.Active || !_tourFollows || _tour.Current != id) return;
            _tour.Finish(host.Camera, TourPosition);
            EndTour();
        };
    }
    /// <summary>
    /// Photo mode starts only over a running game scene: not the title, a loading cover, a duel, a flight of the cinema or a squad tour.
    /// </summary>
    bool PhotoAllowed() => !host.Covered && !host.TitleScreen.TitleVisible && !host.Duel.IsDuel && host.SkyArrival is null
        && !host.GameOptions.NoPopulation && !_cinema.Playing && !_cinema.Recording && !_tour.Active;

    Vector3? TourPosition(CharacterId id) => host.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is { } c ? host.TrackedPosition(c) : null;

    void EndTour()
    {
        _tour.Stop();
        _cinemaHolds = false;
        host.Session.Camera.TakeOver(new CameraState(host.Camera.Eye, host.Camera.Forward, Vector3.UnitY, host.Camera.Target, host.Camera.Yaw,
            host.Camera.Pitch, host.Camera.Distance, true), strategy: true);
        if (_tourFollows) host.Player.Follow = _tour.Current;   // a double-clicked member is followed, as a cut to it would be
    }

    #endregion

    #region Input

    /// <summary>
    /// The window's keyboards and mice, and the clipboard the faithful windows' edit boxes copy and paste through (headless: a clipboard of the
    /// game's own).
    /// </summary>
    void ConnectInput()
    {
        _silkInput = _window is null ? null : Silk.NET.Input.InputWindowExtensions.CreateInput(_window);
        _keyboards = _silkInput is null ? [] : _silkInput.Keyboards;
        _mice = _silkInput is null ? [] : _silkInput.Mice;
        host.Player.ClipboardRead = () => _keyboards.Count > 0 ? ClipboardText.Read(_keyboards[0]) : _headless ? _headlessClipboard : null;
        host.Player.ClipboardWrite = text => { if (_keyboards.Count > 0) _keyboards[0].ClipboardText = text; else if (_headless) _headlessClipboard = text; };
        _lockOrbit = OperatingSystem.IsLinux();
        foreach (var kb in _keyboards) ConnectKeyboard(kb);
        foreach (var mouse in _mice) ConnectMouse(mouse);
    }

    /// <summary>
    /// The interface's actions (saves, loads, a new game, the menus), the window's focus and the control tick's input actions.
    /// </summary>
    void ConnectGameEvents()
    {
        host.Player.ScreenAction += OnScreenAction;
        if (_window is not null) _window.FocusChanged += focused =>
        {
            _windowFocused = focused;
            if (!focused)
            {
                _highlightHeld = false;
                host.Session.Input.ReleaseAll();
            }
        };
        host.Session.Ticked += OnTicked;
    }

    void OnScreenAction(MyGuiEvent e)
    {
        if (e.Action == "save") host.Saves.SaveRequestedFolder = Data.Save.SaveCatalog.Folder(Simulation.Saving.QuickSave.DefaultRoot, e.Text);
        else if (e.Action == "load") host.Saves.LoadRequestedFolder = e.Text;
        else if (e.Action == "import") host.Saves.ImportRequested = (e.Text, host.Player.Frontend.ImportFlags);
        else if (e.Action == "new-game") host.NewGame.NewGameRequested = e.Text;
        else if (e.Action.StartsWith("feedback-", StringComparison.Ordinal)) host.FeedbackCapture.FeedbackAction(e);
        else if (e.Action == "main-menu") host.NewGame.MainMenuRequested = true;
        else if (e.Widget == "OptionsButton" && _panel is not null) _panel.Visible = true;
        else if (e.Widget == "ExitGameButton") _quit = true;
    }

    void OnTicked(ActionState actions)
    {
        _highlightHeld = actions.Held(InputAction.Highlight);
        // The duel is for filming: Esc must not close it. The terrain sandbox has no game menu: Esc opens the settings (close the window to leave).
        if (actions.Pressed(InputAction.Quit) && host.GameOptions.TerrainSandbox is not null)
        {
            if (_panel is not null) _panel.Visible = true;
        }
        else if (actions.Pressed(InputAction.Quit) && !host.Duel.IsDuel) _quit = true;
        // Panning (or leaving for the free camera) lets go of the character the camera follows; turning and zooming keep it.
        if (actions.Held(InputAction.MoveForward) || actions.Held(InputAction.MoveBack) || actions.Held(InputAction.MoveLeft)
            || actions.Held(InputAction.MoveRight) || actions.Pressed(InputAction.ToggleFreeCamera)) host.Player.Follow = null;
        if (actions.Pressed(InputAction.ToggleSettings) && _panel is not null) _panel.Visible = !_panel.Visible;
        if (actions.Pressed(InputAction.Screenshot)) host.ScreenshotRequested = true;
        // the foliage triangles are read from its GPU cull while shown
        if (actions.Pressed(InputAction.ToggleStats))
        {
            host.DebugMenu.StatsVisible = !host.DebugMenu.StatsVisible;
            FoliageGpuCull.CountTriangles = host.DebugMenu.StatsVisible;
        }
        if (actions.Pressed(InputAction.ToggleFps)) host.DebugMenu.FpsVisible = !host.DebugMenu.FpsVisible;
        if (actions.Pressed(InputAction.QuickSave)) host.Saves.QuickSaveRequested = true;
        if (actions.Pressed(InputAction.QuickLoad)) host.Saves.QuickLoadRequested = true;
        // PageUp / PageDown step the viewed floor in the strategy camera: in the free camera and on a held one the cinema takes them for the lens.
        if (actions.Pressed(InputAction.FloorUp)) host.Interiors.StepFloor(1);
        if (actions.Pressed(InputAction.FloorDown)) host.Interiors.StepFloor(-1);
        if (actions.Pressed(InputAction.CycleProfiler) && host.DebugMenu.Profiler is not null)
            host.DebugMenu.Profiler.Showing = (FrameProfiler.Mode)(((int)host.DebugMenu.Profiler.Showing + 1) % 3);
        // The camera the cinema left (a cut, a scrub, the end of a flight) goes back to the rig at the first camera input: the free camera at
        // that eye (only it can stand anywhere), or with ; the strategy camera where the view meets the ground. A flight in progress keeps it.
        if (_tour.Active && GameHost.CameraInput(actions)) EndTour();
        else if (_cinemaHolds && !_cinema.Playing && GameHost.CameraInput(actions))
        {
            _cinemaHolds = false;
            host.Session.Camera.TakeOver(new CameraState(host.Camera.Eye, host.Camera.Forward, Vector3.UnitY, host.Camera.Target, host.Camera.Yaw,
                host.Camera.Pitch, host.Camera.Distance, true), strategy: actions.Pressed(InputAction.ToggleFreeCamera));
        }
    }

    // ---- the window and the pointer ----

    Vector2D<int> FramebufferSizeNow() => _window is null
        ? new Vector2D<int>(host.WorldOptions.Width, host.WorldOptions.Height) : _window.FramebufferSize;

    bool FullScreenOn() => _window is not null && BorderlessFullScreen.IsOn(_window);

    void ToggleFullScreen()
    {
        if (_window is not null) BorderlessFullScreen.Toggle(_window);
    }

    Vector2 Pixels(Vector2 p) => _window is null ? p
        : p * new Vector2(_window.FramebufferSize.X / (float)Math.Max(_window.Size.X, 1),
            _window.FramebufferSize.Y / (float)Math.Max(_window.Size.Y, 1));

    /// <summary>
    /// The settings panel takes the mouse only while it is drawn (F7 and the cinema's clean picture hide it).
    /// </summary>
    bool PanelLive() => !host.AgentChannel.InterfaceHidden && !_cinema.Clean;

    bool Held(SilkKey a, SilkKey b) => _keyboards.Any(k => k.IsKeyPressed(a) || k.IsKeyPressed(b));

    static bool CtrlDown(Silk.NET.Input.IKeyboard keyboard) => keyboard.IsKeyPressed(SilkKey.ControlLeft)
        || keyboard.IsKeyPressed(SilkKey.ControlRight);

    /// <summary>
    /// The key a modal window takes: the keypad's Enter as Enter.
    /// </summary>
    static EngineKey? ModalKeyOf(SilkKey k) => k == SilkKey.KeypadEnter ? EngineKey.Enter : KeyMap.Map(k);

    // ---- keyboard ----

    bool TextFieldFocused() => _panel?.Visible == true || host.DebugMenu.DebugMenuOpen || host.DevConsoleHost.ConsoleOpen || host.Player.UiEditing;

    void RepeatEditKey()
    {
        if (_heldEditKey is not { } held || Stopwatch.GetTimestamp() < held.Next) return;
        if (!held.Keyboard.IsKeyPressed(held.Key) || !TextFieldFocused())
        {
            _heldEditKey = null;
            return;
        }
        _heldEditKey = held with { Next = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 30 };
        OnKeyDown(held.Keyboard, held.Key);
    }

    void ConnectKeyboard(Silk.NET.Input.IKeyboard kb)
    {
        kb.KeyDown += (keyboard, k, _) =>
        {
            _heldEditKey = k is SilkKey.Backspace or SilkKey.Delete or SilkKey.Left or SilkKey.Right or SilkKey.Up or SilkKey.Down
                && TextFieldFocused()
                ? (keyboard, k, Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2) : null;
            OnKeyDown(keyboard, k);
        };
        kb.KeyUp += (_, k, _) =>
        {
            if (_heldEditKey?.Key == k) _heldEditKey = null;
            if (k is SilkKey.ShiftLeft or SilkKey.ShiftRight)
            {
                _shiftDown = false;
                host.Player.SlowModifier = false;
            }
            if (KeyMap.Map(k) is { } key) { host.Session.Input.SetKey(key, false); host.Player.Build.BuildKeyUp(key); }
        };
        kb.KeyChar += (_, value) => OnKeyChar(value);
    }

    void OnKeyChar(char value)
    {
        if (host.Covered) return;
        if (host.DevConsoleHost.ConsoleOpen) host.DevConsoleHost.DevConsole.Type(value);
        else if (_panel?.Visible == true) _panel.TextInput(value);
        else if (host.Sandbox.SandboxPicker is { Open: true }) host.Sandbox.SandboxPicker.TextInput(value);
        else if (host.DebugMenu.DebugMenuOpen) host.DebugMenu.DebugMenuText(value);
        else host.Player.UiText(value);
    }

    /// <summary>
    /// A key goes to the first that takes it: the loading screen, a modal window, the interface's own toggles, the game's interface, then the game.
    /// </summary>
    void OnKeyDown(Silk.NET.Input.IKeyboard keyboard, SilkKey k)
    {
        if (k is SilkKey.ShiftLeft or SilkKey.ShiftRight)
        {
            _shiftDown = true;
            host.Player.SlowModifier = true;
        }
        if (k == SilkKey.Enter && (keyboard.IsKeyPressed(SilkKey.AltLeft) || keyboard.IsKeyPressed(SilkKey.AltRight)))
        {
            ToggleFullScreen();
            return;
        }
        if (host.Covered)
        {
            if (k is SilkKey.Escape or SilkKey.Space) host.SkyArrival?.Skip();
            host.Loading?.SkipSplash();
            return;
        }
        if (ModalKey(keyboard, k) || PhotoKey(k) || InterfaceKey(keyboard, k)) return;
        if (KeyMap.Map(k) is { } uiKey && host.Player.UiKey(uiKey, CtrlDown(keyboard), _shiftDown)) return;
        GameKey(keyboard, k);
    }

    /// <summary>
    /// The dev console, the settings panel, the sandbox's animation list, the debug menu, the inspector and the interior editor each take every key
    /// while open.
    /// </summary>
    bool ModalKey(Silk.NET.Input.IKeyboard keyboard, SilkKey k)
    {
        // Insert or Ctrl+` opens the dev console, which then takes every key (the settings panel's, the animation list's and the debug menu's too)
        // until it closes.
        if (DevConsoleHost.IsConsoleToggle(k, CtrlDown(keyboard)))
        {
            host.DevConsoleHost.ToggleConsole();
            return true;
        }
        if (host.DevConsoleHost.ConsoleOpen)
        {
            host.DevConsoleHost.ConsoleKey(keyboard, k);
            return true;
        }
        host.Player.UiVisible = PanelLive() && !host.GameOptions.NoPopulation;
        if (_panel?.Visible == true)
        {
            if (ModalKeyOf(k) is { } settingsKey)
            {
                if (settingsKey is EngineKey.F8 or EngineKey.PrintScreen) host.ScreenshotRequested = true;
                else _panel.KeyDown(settingsKey, _shiftDown, CtrlDown(keyboard));
            }
            return true;
        }
        if (host.Sandbox.SandboxPicker is { Open: true })
        {
            if (ModalKeyOf(k) is { } pickerKey) host.Sandbox.SandboxPicker.KeyDown(pickerKey);
            return true;
        }
        // F10 (rebindable) opens the debug menu, which then takes every key until it closes.
        if (KeyMap.Map(k) is { } mapped && host.Session.Bindings.Get(InputAction.ToggleDebugMenu).Contains(Binding.Of(mapped)))
        {
            host.DebugMenu.DebugMenuOpen = !host.DebugMenu.DebugMenuOpen;
            host.DebugMenu.DebugParts = null;
            return true;
        }
        if (host.DebugMenu.DebugMenuOpen)
        {
            if (ModalKeyOf(k) is { } menuKey) host.DebugMenu.DebugMenuKey(menuKey);
            return true;
        }
        // The pick & collision inspector's menu (opened from F10's Tools row, DebugMenu): while open it takes every key, like F10's (before the game
        // interface, whose Esc would open the game menu instead).
        if (host.DebugMenu.InspectorMenuOpen)
        {
            if (ModalKeyOf(k) is { } inspectorKey) host.DebugMenu.InspectorMenuKey(inspectorKey);
            return true;
        }
        if (host.InteriorEditorHost.InteriorEditorOpen)
        {
            if (ModalKeyOf(k) is { } editorKey) host.InteriorEditorHost.InteriorEditorKey(editorKey);
            return true;
        }
        return false;
    }

    /// <summary>
    /// A key while photo mode is on or starts it (see <see cref="PhotoMode.KeyDown"/>); not while a text field has the keyboard, not in the terrain
    /// sandbox (P is its patrol key) or with a window open over the game.
    /// </summary>
    bool PhotoKey(SilkKey k)
    {
        if (host.Player.UiEditing || host.GameOptions.NoPopulation) return false;
        if (ModalKeyOf(k) is not { } key) return _photo.Active;
        if (!_photo.Active && (host.Sandbox.IsSandbox || host.Player.ModalOpen)) return false;
        return _photo.KeyDown(key, k == SilkKey.Escape);
    }

    /// <summary>
    /// F7 hides the interface, Ctrl+C / Ctrl+V the free camera's code, the settings key opens the panel, Esc with the interface hidden brings it
    /// back.
    /// </summary>
    bool InterfaceKey(Silk.NET.Input.IKeyboard keyboard, SilkKey k)
    {
        // toggle_bar (F7, rebindable) hides the game's interface; F6 is the cinema's, and so is the numpad except the keys bound to the game's
        // hotkeys (Space, Delete and the lens keys too while its editor is open).
        if (KeyMap.Map(k) is { } hideKey && !host.Player.UiEditing
                && host.Session.Bindings.Get(InputAction.ToggleInterface).Contains(Binding.Of(hideKey)))
        {
            host.AgentChannel.InterfaceHidden = !host.AgentChannel.InterfaceHidden;
            Console.WriteLine(host.AgentChannel.InterfaceHidden ? "interface hidden (F7 shows it)" : "interface shown");
            return true;
        }
        // Only in the free camera: elsewhere Ctrl+C and Ctrl+V keep their own meaning, and so do they in a focused edit box.
        if (k is SilkKey.C or SilkKey.V && host.Session.Camera.IsFree && !host.Player.UiEditing && CtrlDown(keyboard))
        {
            CameraCodeKey(keyboard, k);
            return true;
        }
        if (_panel is not null && KeyMap.Map(k) is { } settingsToggle
                && host.Session.Bindings.Get(InputAction.ToggleSettings).Contains(Binding.Of(settingsToggle)))
        {
            _panel.Visible = true;
            return true;
        }
        // Esc is the game menu's, never Quit's while there is a game: with the interface hidden (F7) it comes back with the menu open,
        // under the cinema's clean picture it does nothing.
        if (k == SilkKey.Escape && !host.GameOptions.NoPopulation && !host.Player.UiVisible)
        {
            if (host.AgentChannel.InterfaceHidden && !_cinema.Clean)
            {
                host.AgentChannel.InterfaceHidden = false;
                host.Player.UiVisible = true;
                host.Player.UiKey(EngineKey.Escape);
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Ctrl+C copies the free camera's position as a viewer camera code, Ctrl+V flies the free camera to the code (or a feedback report's camera
    /// location) in the clipboard.
    /// </summary>
    void CameraCodeKey(Silk.NET.Input.IKeyboard keyboard, SilkKey k)
    {
        if (k == SilkKey.C)
        {
            string code = WorldCameraCode.Encode(host.Camera);
            keyboard.ClipboardText = code;
            Console.WriteLine($"camera    copied {code} ({WorldCameraCode.Describe(host.Camera)})");
        }
        else if (WorldCameraCode.Decode(ClipboardText.Read(keyboard)) is { } pose)
        {
            // The code's orbit (target, yaw, pitch, distance) as the free camera's eye and look direction.
            Vector3 back = new(MathF.Cos(pose.Pitch) * MathF.Sin(pose.Yaw), MathF.Sin(pose.Pitch), MathF.Cos(pose.Pitch) * MathF.Cos(pose.Yaw));
            host.Session.Camera.TakeOver(new CameraState(pose.Target + back * pose.Distance, -back, Vector3.UnitY, pose.Target, pose.Yaw, pose.Pitch,
                pose.Distance, true), strategy: false);
            host.Player.Follow = null;
            Console.WriteLine("camera    moved to the clipboard's camera code");
        }
        else Console.WriteLine("camera    the clipboard holds no camera code (Ctrl+C in a viewer copies one) or feedback camera location");
    }

    /// <summary>
    /// The keys the interface left: the squad tour, the cinema, the fps counter, the debug combinations, then the game's input bindings.
    /// </summary>
    void GameKey(Silk.NET.Input.IKeyboard keyboard, SilkKey k)
    {
        bool ctrl = CtrlDown(keyboard);
        if (k == SilkKey.KeypadDivide && !host.Duel.IsDuel)
        {
            ToggleSquadTour();
            return;
        }
        // A numpad key bound to a game hotkey (the orders, NUM0..NUM8 by default) is the game's, except with Ctrl (saving a shot) or the cinema's
        // editor open (F6).
        bool hotkey = !host.Duel.IsDuel && !ctrl && !_cinema.EditorVisible && KeyMap.Map(k) is { } gameKey
            && (host.Player.GameHotkeys.IsHotkey(gameKey) || host.Player.Build.OwnsBuildNumpad(gameKey));
        if (!hotkey && Enum.TryParse(k.ToString(), out CinemaKey cinemaKey) && _cinema.OnKey(cinemaKey, ctrl, _shiftDown)) return;
        // F9 is the game's quickload; the fps counter is on Ctrl+F9 (a modifier, so the key never reaches the input bindings).
        if (k == SilkKey.F9 && ctrl)
        {
            host.DebugMenu.FpsVisible = !host.DebugMenu.FpsVisible;
            return;
        }
        if (KeyMap.Map(k) is not { } key || DebugKey(key, ctrl)) return;
        if (host.Player.Build.BuildKey(key, _shiftDown)) return;
        host.Session.Input.SetKey(key, true);
        host.Player.GameHotkeys.Key(key, _shiftDown);
        host.Sandbox.SandboxKey(key);
        if (host.Duel.IsDuel && key == EngineKey.Backspace) host.Duel.DuelRestartRequested = true;
        if (host.Battle.IsBattle && key == EngineKey.Backspace) host.Battle.BattleRestartRequested = true;
    }

    /// <summary>
    /// Numpad /: the squad tour starts, or the one under way ends.
    /// </summary>
    void ToggleSquadTour()
    {
        if (_tour.Active) EndTour();
        else if (!_cinema.Playing && !_cinema.Recording)
        {
            host.Player.Follow = null;
            _tour.HopSeconds = host.Config.CharacterFlightSeconds;
            _tourFollows = false;
            _tour.Start(host.Camera, [.. host.Player.PortraitBar.SquadBarMembers().Select(c => c.Id)], TourPosition);
            if (_tour.Active) _cinemaHolds = true;
            else Console.WriteLine("squad tour: nobody else in the squad to fly to");
        }
    }

    /// <summary>
    /// The Shift combinations: Meitou on and off (F1), the skin switch alone (F2), the navmesh view (F11), the VRAM pie and dump (F12).
    /// </summary>
    bool DebugKey(EngineKey key, bool ctrl)
    {
        if (!_shiftDown) return false;
        if (key == EngineKey.F1)
        {
            Console.WriteLine(_meitou.Toggle());
            host.Config.FaithfulLook = !_meitou.On;
            return true;
        }
        if (key == EngineKey.F2 && _switches.FirstOrDefault(e => e.Id == "skin") is { } skin)   // the skin switch alone, to compare faces
        { skin.IsMeitou = !skin.IsMeitou; Console.WriteLine($"skin      {skin.State}"); return true; }
        if (key == EngineKey.F11)
        {
            host.DebugOverlays.ToggleNavOverlay();
            return true;
        }
        if (ctrl && key == EngineKey.F12)
        {
            DumpVram(host.DebugMenu.Profiler);
            return true;
        }
        // the VRAM pie (F12 alone cycles the profiler)
        if (key == EngineKey.F12)
        {
            if (host.DebugMenu.Profiler is not null) host.DebugMenu.Profiler.ShowVram = !host.DebugMenu.Profiler.ShowVram;
            return true;
        }
        return false;
    }

    // ---- mouse ----

    void ConnectMouse(Silk.NET.Input.IMouse mouse)
    {
        mouse.MouseDown += (m, b) =>
        {
            if (host.Covered)
            {
                host.Loading?.SkipSplash();
                return;
            }
            PointerDown(KeyMap.Map(b), Pixels(_orbitAnchor ?? m.Position));
        };
        mouse.MouseUp += (m, b) => PointerUp(KeyMap.Map(b), Pixels(_orbitAnchor ?? m.Position));
        mouse.MouseMove += OnMouseMove;
        mouse.Scroll += (m, wheel) =>
        {
            if (host.Covered || PanelLive() && _panel?.Scroll(wheel.Y) == true) return;
            if (_photo.Active)
            {
                if (!_photo.Panel.Contains(Pixels(_orbitAnchor ?? m.Position))) host.Session.Input.AddWheel(wheel.Y);
                return;
            }
            if (PanelLive() && !host.Config.SandboxPanelCollapsed && host.Sandbox.SandboxPicker?.Scroll(wheel.Y) == true) return;
            var at = Pixels(_orbitAnchor ?? m.Position);
            if (!_cinema.Contains(at) && !host.Player.UiScroll(at, wheel.Y) && !(PanelLive() && host.Player.ContainsUi(at)))
                host.Session.Input.AddWheel(wheel.Y);
        };
    }

    /// <summary>
    /// The press of a pointer button, shared by the window's events and the control channel's <c>click</c> (which injects them at a pixel).
    /// </summary>
    void PointerDown(EngineButton? physical, Vector2 at)
    {
        if (_photo.Active)
        {
            PhotoPointerDown(physical, at);
            return;
        }
        host.Player.UiVisible = PanelLive() && !host.GameOptions.NoPopulation;
        bool select = physical is { } pressed && host.PlayerButton(pressed) == EngineButton.Left;   // the Select button, whichever it is bound to
        if (PanelLive() && physical is { } settingsButton && _panel?.MouseDown(at, settingsButton) == true) { _panelDrag = true; return; }
        if (PanelLive() && _panel?.Contains(at) == true) return;
        if (select && PanelLive() && !host.Config.SandboxPanelCollapsed && host.Sandbox.SandboxPicker?.MouseDown(at) == true) return;
        if (select && PanelLive() && host.Sandbox.SandboxToggleClick(at)) return;
        if (select && host.DebugMenu.Profiler?.Click(at) == true) { _profilerClick = true; return; }   // F12's legend opens and closes rows
        if (select && host.DebugOverlays.StatsToggleClick(at)) { _profilerClick = true; return; }   // F11's character boxes and paths
        // The cinema's editor: its clicks never select or move characters.
        if (select && _cinema.MouseDown(at)) return;
        if (_cinema.Contains(at)) return;
        if (physical is { } button)
        {
            // Ctrl and Alt with the right button: pick up / put down and loot (docs/game/ui-input.md, Meitou's carry bindings).
            if (host.Player.MouseDown(host.PlayerButton(button), at, _shiftDown, Held(SilkKey.ControlLeft, SilkKey.ControlRight),
                    Held(SilkKey.AltLeft, SilkKey.AltRight)))
                return;
            host.Session.Input.SetMouseButton(button, true);
        }
    }

    /// <summary>
    /// Photo mode's press: the panel takes the left button; no click reaches the world (no selecting, no orders), the camera sees the buttons.
    /// </summary>
    void PhotoPointerDown(EngineButton? physical, Vector2 at)
    {
        if (physical == EngineButton.Left && _photo.Panel.MouseDown(at)) return;
        if (_photo.Panel.Contains(at)) return;
        if (physical is { } button) host.Session.Input.SetMouseButton(button, true);
    }

    /// <summary>
    /// The release of a pointer button (see <see cref="PointerDown"/>).
    /// </summary>
    void PointerUp(EngineButton? physical, Vector2 at)
    {
        if (_photo.Active)
        {
            _photo.Panel.MouseUp();
            if (physical is { } photoButton) host.Session.Input.SetMouseButton(photoButton, false);
            return;
        }
        bool cinemaDrag = _cinema.MouseUp();
        if (!host.Covered && !cinemaDrag && !_panelDrag && !_profilerClick && _panel?.Visible != true && physical is { } released)
            host.Player.MouseUp(host.PlayerButton(released), at, _shiftDown);
        _profilerClick = false;
        _panel?.MouseUp();
        _panelDrag = false;
        if (physical is { } button) host.Session.Input.SetMouseButton(button, false);
    }

    void OnMouseMove(Silk.NET.Input.IMouse m, Vector2 p)
    {
        if (_orbitAnchor is not null)
        {
            // Locked: p is GLFW's virtual position, only its movement counts, and not under a loading screen or the interface (as below).
            if (_lastMouse is { } prev && !host.Covered && !host.Player.UiCapturesPointer && !host.Player.ModalOpen)
                host.Session.Input.AddMouseDelta(p.X - prev.X, p.Y - prev.Y);
            _lastMouse = p;
            return;
        }
        host.Session.Input.SetMousePosition(p.X, p.Y);
        if (host.Covered)
        {
            _lastMouse = p;
            return;
        }
        if (PanelLive() && _panel?.MouseMove(Pixels(p)) == true || _panelDrag)
        {
            _lastMouse = p;
            return;
        }
        if (_cinema.MouseMove(Pixels(p)) || _photo.Active && !host.OrbitHeld() && _photo.Panel.MouseMove(Pixels(p)))
        {
            _lastMouse = p;
            return;
        }
        bool hold = _lastMouse is not null && host.OrbitHeld();
        host.Player.MouseMove(Pixels(hold ? _lastMouse!.Value : p));
        if (host.Player.UiCapturesPointer || host.Player.ModalOpen)
        {
            _lastMouse = p;
            return;
        }
        if (_lastMouse is { } last) host.Session.Input.AddMouseDelta(p.X - last.X, p.Y - last.Y);
        if (hold && _lockOrbit)
        {
            // Locked here, not with the cursor mode at the end of the frame: until then the pointer moves on, and GLFW counts that movement
            // twice (X11) or drops it (Wayland) when the lock starts. Movement is then measured from where GLFW's virtual position starts.
            _orbitAnchor = _lastMouse;
            host.Session.Input.SetMousePosition(_lastMouse!.Value.X, _lastMouse.Value.Y);
            _cursorBeforeOrbit = _windowCursor;
            m.Cursor.CursorMode = _windowCursor = Silk.NET.Input.CursorMode.Disabled;
            _lastMouse = m.Position;
            return;
        }
        if (hold)
        {
            host.HoldPointer(m, _lastMouse!.Value);
            return;
        }
        _lastMouse = p;
    }

    // ---- a frame ----

    #endregion

    #region Frame loop

    /// <summary>
    /// One frame of the loop: input, the world's ticks, the camera, the audio, then the picture drawn and presented. False ends the loop (the window
    /// closed, the control channel quit).
    /// </summary>
    bool Frame()
    {
        host.PlayBench.BenchLap(PlayBench.BenchTail);
        _display.BeginLatencyFrame();   // DLSS frame generation's Reflex: before the input is read
        _window?.DoEvents();
        if (_window?.IsClosing == true) return false;
        if (host.DevConsoleHost.ControlQuit)
        {
            _quit = true;
            return false;
        }
        RepeatEditKey();
        double now = _frame.Elapsed.TotalSeconds, dt = now - _last;
        _last = now;
        double frameDt = BeginFrameTime(dt);
        AdvanceWorld(frameDt);
        MoveCamera(dt);
        host.AgentChannel.PollControl();
        string? takeFile = _cinema.TakeFrame();
        PlaceSceneCameras();
        host.PlayBench.BenchLap(3);
        host.Sound.UpdateAudio(dt);
        host.PlayBench.BenchLap(4);
        AdvanceTitleAndArrival(frameDt);
        KeepEyeAboveGround();
        var size = FramebufferSizeNow();
        // Until the warm-up's frames run smoothly the water reflection draws everything and the sky its cloud volume, so their pipelines are made
        // under the cover (ReflectionPass.Warm, SkyRenderer.Warm).
        bool warmPasses = host.Warming && host.SmoothSeconds == 0;
        if (host.Gpu.Reflection is { } reflection) reflection.Warm = warmPasses;
        host.Gpu.Sky.Warm = warmPasses;
        host.UpdateView(size.X, size.Y);
        var pointer = PreparePointer(takeFile);
        host.PlayBench.BenchLap(5);
        if (_display.BeginFrame(size.X, size.Y)) DrawFrame(size, pointer, takeFile);
        else if (host.ScreenshotRequested && host.AgentChannel.ShotPath is not null && ++host.ShotNoFrame >= 30)
            host.AgentChannel.ControlShotFailed("the frame cannot be drawn (window minimised or device lost)");
        UpdateStats(dt);
        _totalFrames++;
        if (host.GameOptions.QuitAfter is { } quitAfter && now >= quitAfter) { PrintRunSummary(now); _quit = true; }
        // The limit is on presented frames: with frame generation on every rendered frame brings a generated one, so the render rate is half of it.
        if (!_display.VSync && _fpsLimit > 0) GameHost.Limit(_frame, now, (_frameGen is { Enabled: true } ? 2.0 : 1.0) / _fpsLimit);
        return true;
    }

    /// <summary>
    /// The frame's time for the counters and the scripted runs; returns the time the simulation advances by (a recording cinema's frame, else the
    /// real time).
    /// </summary>
    double BeginFrameTime(double dt)
    {
        // The last frame was drawn under the loading screen: a scene load's next stage may run, the warm-up counts it.
        if (host.Covered) host.CoveredFrame(dt);
        host.AdvanceSkyArrival(dt);
        if (host.GameOptions.QuitAfter is not null && host.TitleScreen.TitleVisible && !host.Covered)
        {
            host.MenuFrames++;
            host.MenuFrameSeconds += dt;
            host.MenuLongestFrame = Math.Max(host.MenuLongestFrame, dt);
            if (dt > 0.1) host.MenuSlowFrames++;
        }
        // The counter: the mean over the last tenth of a second, so a single late frame doesn't read as a drop; then a frame's work
        // (its time without the waits) and its whole time in brackets.
        _fpsWindow += dt;
        _fpsWindowFrames++;
        if (_fpsWindow >= 0.1)
        {
            _fpsText = FrameGenerationSetup.FpsCounterText(_fpsWindowFrames, _fpsWindow, _frameGen);
            _fpsWindow = 0;
            _fpsWindowFrames = 0;
        }
        // While the cinema records, every frame is exactly one frame of its rate, for the simulation too (the take plays at game speed
        // however slowly the frames are drawn and saved); otherwise the real time.
        double frameDt = _cinema.Recording ? _cinema.FrameTime(dt) : dt;
        // The duel starts a lap when a flight starts (played or recorded), so every take begins with the fight's first frame, and when its lap is
        // over.
        host.Duel.DuelFrame(_cinema.Playing && !_cinemaWasPlaying || _cinema.Recording && !_cinemaWasRecording);
        host.Battle.BattleFrame();
        (_cinemaWasPlaying, _cinemaWasRecording) = (_cinema.Playing, _cinema.Recording);
        if (!host.PlayBench.PlayBenchFrame(dt, host.DebugMenu.Profiler)) _quit = true;
        host.PlayBench.BenchLap(0);
        return frameDt;
    }

    /// <summary>
    /// The world's ticks for the frame, then what follows them between ticks: news, saves, loads, the navmesh and the buildings.
    /// </summary>
    void AdvanceWorld(double frameDt)
    {
        long a0 = Stopwatch.GetTimestamp();
        if (_panel?.Visible == true)
        {
            foreach (var key in Enum.GetValues<EngineKey>()) host.Session.Input.SetKey(key, false);
            foreach (var button in Enum.GetValues<EngineButton>()) host.Session.Input.SetMouseButton(button, false);
            host.Session.Input.Consume();
        }
        // Edge scrolling (the game's default): the pointer at the edge of the window pans the camera, unless something else has the pointer.
        host.Session.Camera.Settings.EdgeScroll = host.Config.EdgeScroll != false;
        bool edgeOff = host.Covered || host.TitleScreen.TitleVisible || _panel?.Visible == true || host.AgentChannel.InterfaceHidden
            || _cinemaHolds || _cinema.EditorVisible;
        host.Session.Camera.Strategy.EdgePointer = edgeOff ? null : _window is null ? null : WindowPointer.InWindow(_window);
        host.Session.World.Profile.Enabled = host.DebugMenu.Profiler is { Showing: not FrameProfiler.Mode.Off }
            || host.GameOptions.PlayBench is not null || SpikeLog.Enabled;
        if (!host.Covered) host.Session.Advance(frameDt);   // loading and arrival preserve the loaded world's exact time
        host.Duel.DuelHoldClock();
        double advanceMs = Stopwatch.GetElapsedTime(a0).TotalMilliseconds;
        host.AdvanceTotal += advanceMs;
        host.PlayBench.PlayBenchUpdated(advanceMs);   // before SimProfile, which clears the world's tick profile
        if (host.DebugMenu.Profiler is not null) SimProfile(host.DebugMenu.Profiler, advanceMs);   // F12's sim row and its parts
        host.PlayBench.BenchLap(1);
        host.WorldNews();
        host.Saves.HandleSaves(frameDt);   // a quicksave or quickload asked for by a key, the autosave: between ticks, none in flight
        host.NewGame.HandleNewGame();
        host.NewGame.HandleMainMenu();
        host.StepSceneLoad();
        host.AnimSeconds += frameDt;
        host.AnimClock = true;
        host.UpdateNav();
        host.DebugOverlays.UpdateNavOverlay();
        host.DebugOverlays.UpdateInspectorOverlay();
        host.BuildingSync.UpdateBuildings();
        host.PlayBench.BenchLap(2);
    }

    /// <summary>
    /// The drawn camera: the rig's, unless the cinema or a flight holds it; and what the streaming and the simulation read from it.
    /// </summary>
    void MoveCamera(double dt)
    {
        host.AgentWorld.ControlCameraMoveTick();   // a timed `camera ... --over` move, before the camera is drawn
        if (_photo.Active && (host.Covered || host.TitleScreen.TitleVisible)) _photo.Abandon();   // its world is gone
        if (!_cinemaHolds) host.ApplyCamera(host.Session.CameraAt());
        _cinema.Advance(_cinema.FrameTime(dt));   // a flight through the keys moves the camera (and takes it from the rig)
        // The camera effects (docs/render-post.md "Camera effects order"): for these cameras, never over the title.
        host.WorldOptions.Post.CinematicView = !host.TitleScreen.TitleVisible && (host.Session.Camera.IsFree || _cinemaHolds || _cinema.Playing || _cinema.Recording || _cinema.EditorVisible);
        _photo.Apply();
        if (_tour.Active && !_tour.Advance(dt, host.Camera, TourPosition)) EndTour();
        host.Gpu.Ahead = _tour.ArrivalEye(TourPosition);   // the streaming loads the landing view during the flight
        host.Session.ViewEye = _cinemaHolds ? host.Camera.Eye : null;   // the simulation's far cadence follows the flying camera, not the rig it left
        host.Gpu.CameraFlying = _tour.Moving;   // the far shadow cascades wait for the landing (docs/render-shadows.md "Flights")
        host.Gpu.Kept = !host.Config.KeepTexturesAtCharacters ? []
            : host.Config.KeepTexturesAtAllSquads
                ? [.. host.Session.CurrentSnapshot.PlayerSquads.SelectMany(s => s.Members).Select(TourPosition).OfType<Vector3>()]
            : [.. host.Player.PortraitBar.SquadBarMembers().Select(c => TourPosition(c.Id)).OfType<Vector3>()];
        host.ViewAspect = FramebufferSizeNow().X / (float)Math.Max(FramebufferSizeNow().Y, 1);   // headless: no window, the size asked for
    }

    /// <summary>
    /// The focus the world loads round, and the cameras of the scenes that place it themselves (the sandbox, the editor, the title).
    /// </summary>
    void PlaceSceneCameras()
    {
        // A flight puts the focus on where it lands from the first frame (the towns there populate during the flight), not on the arc.
        host.SendFocus(_tour.Active && _tour.Current is { } landing && TourPosition(landing) is { } landingAt ? landingAt
            : _cinemaHolds ? host.Camera.Target : null);
        if (!_cinemaHolds) host.Sandbox.SandboxFollowCamera();
        if (!_cinemaHolds) host.Sandbox.FollowCamera();
        if (!_cinemaHolds) host.CharacterEditor.EditorCamera();
        host.CharacterEditor.OpenPendingEditor();
        host.CharacterEditor.ResumeAfterCreation();
        if (!_cinemaHolds) host.TitleScreen.ApplyTitleCamera();
    }

    void AdvanceTitleAndArrival(double frameDt)
    {
        if (host.TitleScreen.TitleVisible && !host.Warming) host.TitleScreen.TitleBattle?.Advance(frameDt);
        if (host.SkyArrival is not null)
        {
            host.ApplyCamera(host.Session.Camera.Current);
            host.TitleScreen.ApplyTitleCamera();
            host.ApplySkyArrival();
        }
    }

    /// <summary>
    /// The strategy camera's eye clearance (StrategyCamera's height rule) for what bypasses the rig's pivot: a followed character's feet as the
    /// target, and the squad tour's and character flights' arcs over hills.
    /// </summary>
    void KeepEyeAboveGround()
    {
        if (host.SkyArrival is not null || host.TitleScreen.TitleVisible || !(_tour.Active || !host.Session.Camera.IsFree && !_cinemaHolds)) return;
        var eye = host.Camera.Eye;
        float shortfall = host.Session.Camera.EyeGroundAt(eye.X, eye.Z) + Data.World.KenshiCamera.EyeClearance(host.Camera.Distance) - eye.Y;
        // Over a hair only: at the clearance's edge the ground read under the eye wavers by a few ten-thousandths frame to frame, and lifting by
        // that moved the eye by a float step now and then, a jump of a pixel or two for a character close to it.
        if (shortfall > 1e-3f) host.Camera.Target += new Vector3(0, shortfall, 0);
    }

    #endregion

    #region Pointer and highlight

    /// <summary>
    /// The frame's pointer: the window's mouse or the control channel's, the modifiers, the highlight and the hover outlines.
    /// </summary>
    FramePointer PreparePointer(string? takeFile)
    {
        var pointerMouse = _mice.FirstOrDefault();
        FeedHeadlessMouse();
        host.DevConsoleHost.PumpPendingSay();
        StepControlPointer();
        // Rotation over on Linux: the pointer is let go where it was locked, in the cursor mode it had before, and before its position is read
        // (still locked, GLFW reports its virtual position, and the pointer was drawn there for a frame). The next move starts afresh.
        if (_orbitAnchor is not null && !host.OrbitHeld())
        {
            _orbitAnchor = null;
            _lastMouse = null;
            if (pointerMouse is not null) pointerMouse.Cursor.CursorMode = _windowCursor = _cursorBeforeOrbit;
        }
        var pointerAt = _headless ? host.DevConsoleHost.HeadlessMouse ?? Vector2.Zero : pointerMouse is null ? Vector2.Zero
            : Pixels(_orbitAnchor ?? pointerMouse.Position);
        host.DevConsoleHost.ConsolePointer = pointerAt;
        host.Player.CtrlHeld = Held(SilkKey.ControlLeft, SilkKey.ControlRight);
        host.Player.AltHeld = Held(SilkKey.AltLeft, SilkKey.AltRight);
        host.ScreenshotRequested = host.Player.GateScreenshot(host.ScreenshotRequested);
        bool highlighting = Highlight(takeFile);
        bool pointerWorld = (pointerMouse is not null || _headless && host.DevConsoleHost.HeadlessMouse is not null) && !host.Covered
            && !host.GameOptions.NoPopulation
            && !(PanelLive() && _panel?.Visible == true) && !_cinema.Contains(pointerAt);
        // Photo mode flies the free camera too, but its panel is worked with the pointer: hidden only while the view is turned or the panel is.
        bool freeLook = pointerWorld && host.Session.Camera.IsFree && !host.Player.ModalOpen && (!_photo.Active || !_photo.PanelShown || host.OrbitHeld());
        Outline(takeFile, highlighting, pointerWorld);
        return new FramePointer(pointerMouse, pointerAt, pointerWorld, freeLook);
    }

    /// <summary>
    /// Headless: the pointer is the control channel's <c>mouse &lt;x&gt; &lt;y&gt;</c> (none until it is given).
    /// </summary>
    void FeedHeadlessMouse()
    {
        if (!_headless || host.DevConsoleHost.HeadlessMouse is not { } fedMouse || _appliedHeadlessMouse == fedMouse) return;
        _appliedHeadlessMouse = fedMouse;
        host.Session.Input.SetMousePosition(fedMouse.X, fedMouse.Y);
        if (!host.Covered && !(PanelLive() && _panel?.MouseMove(fedMouse) == true) && !_cinema.MouseMove(fedMouse)) host.Player.MouseMove(fedMouse);
    }

    /// <summary>
    /// The control channel's <c>click</c>: one step per frame (move, a frame for the ghost and hover to follow, press, release), through the window's
    /// press and release.
    /// </summary>
    void StepControlPointer()
    {
        if (host.DevConsoleHost.PointerSteps.Count == 0) return;
        var (kind, right, at) = host.DevConsoleHost.PointerSteps.Dequeue();
        switch (kind)
        {
            case 'm':
                if (_headless) host.DevConsoleHost.HeadlessMouse = at;
                host.Session.Input.SetMousePosition(at.X, at.Y);
                if (!host.Covered && !(PanelLive() && _panel?.MouseMove(at) == true) && !_cinema.MouseMove(at)) host.Player.MouseMove(at);
                break;
            case 'd':
                if (!host.Covered) PointerDown(KeyMap.Map(right ? SilkButton.Right : SilkButton.Left), at);
                break;
            case 'u':
                if (!host.Covered) PointerUp(KeyMap.Map(right ? SilkButton.Right : SilkButton.Left), at);
                break;
        }
    }

    /// <summary>
    /// The Highlight key (Alt) lights up the signs, outlines the characters near the focus and labels the ground items there.
    /// </summary>
    bool Highlight(string? takeFile)
    {
        bool highlighting = _highlightHeld && _windowFocused && !host.Covered && !host.Session.Camera.IsFree &&
            _panel?.Visible != true && !host.AgentChannel.InterfaceHidden && !_cinema.Clean && !_cinemaHolds && !_cinema.EditorVisible;
        if (host.Gpu.Objects is { } signObjects) signObjects.HighlightSigns = highlighting;
        host.Player.GroundItemPicks.HighlightFocus = highlighting && !host.ScreenshotRequested && takeFile is null ? host.Camera.Target : null;
        return highlighting;
    }

    /// <summary>
    /// Hover and building outlines (Advanced, Meitou only): not in the free camera, with the interface hidden, or in saved pictures.
    /// </summary>
    void Outline(string? takeFile, bool highlighting, bool pointerWorld)
    {
        bool outlines = !host.Session.Camera.IsFree && !host.AgentChannel.InterfaceHidden && !_cinema.Clean
            && !host.ScreenshotRequested && takeFile is null;
        bool outlining = host.Config.HoverOutline && outlines;
        if (outlining && highlighting)
            host.Gpu.HoverKeys = [.. host.Player.PointerPanel.HighlightTargets(host.Camera.Target, PointerView.HighlightRadius)
                .Select(t => (host.DrawKey(t.Id), t.Colour))];
        else
            host.Gpu.HoverKeys = outlining && pointerWorld && host.Player.PointerPanel.HoverTarget is { } hover
                ? [(host.DrawKey(hover.Id), hover.Colour)] : [];
        host.Gpu.OutlinedBuildings = outlines && host.Config.BuildingOutline
            ? host.Player.BuildingSelection.OutlinedBuildings(hover: pointerWorld) : [];
    }

    // ---- drawing ----

    #endregion

    #region Drawing

    /// <summary>
    /// The frame the display began: the world (or the loading sky), the interface, the loading screen and the pointer, presented, then saved where a
    /// picture was asked for.
    /// </summary>
    void DrawFrame(Vector2D<int> size, FramePointer pointer, string? takeFile)
    {
        host.ShotNoFrame = 0;
        host.PlayBench.BenchLap(6);
        long t0 = Stopwatch.GetTimestamp();
        var backbuffer = PrepareTargets(size, takeFile, out var uiTarget);
        host.DebugMenu.Profiler?.BeginFrame();
        // A scene load draws only the loading screen; the warm-up draws whole frames under it (the first frames of a new place build what it needs).
        bool covered = host.Covered;
        DrawWorldOrSky(size, covered);
        StageClock.UiPart(StageClock.UiWorldEnd);
        // MEITOU_DIAG_SUBMIT=1 (issue #108): the world submitted and finished before the interface draws, and the interface before the present.
        if (GpuContext.DiagSubmit) _display.Context.SubmitAndWait();
        // The sky arrival's fade to and from black, over the world and under everything else.
        if (host.SkyArrival is { Fade: > 0 } fading && _overlay is not null)
        {
            _overlay.Rect(0, 0, size.X, size.Y, new Vector4(0, 0, 0, fading.Fade));
            _overlay.Flush(size.X, size.Y);
        }
        _drawTotal += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        bool shot = host.ScreenshotRequested, take = takeFile is not null;
        // The feedback screenshot: the frame without the menus, the settings, the debug overlays and the pointer (FeedbackCapture).
        bool feedbackShot = host.FeedbackCapture.FeedbackShotRequested && !take;
        host.Player.FeedbackPanel.ShotWithoutMenus = feedbackShot;
        DrawInterface(size, covered, shot, take, feedbackShot);
        // The loading screen over the whole frame while a scene loads or the world warms up.
        DrawLoadingCover(size, covered, uiTarget);
        StageClock.UiPart(StageClock.UiLoading);
        if (!shot && !take && !feedbackShot) DrawPointer(size, pointer);
        StageClock.UiPart(StageClock.UiPointer);
        var finalPicture = Present(backbuffer, uiTarget, shot || take || feedbackShot);
        host.PlayBench.PlayBenchRendered(Stopwatch.GetElapsedTime(t0).TotalMilliseconds, _display.Context.Pipelines.LatePipelines);
        TallyFrame();
        SavePictures(size, takeFile, shot, feedbackShot, finalPicture ?? backbuffer, topDown: finalPicture is not null);
    }

    /// <summary>
    /// The backbuffer the chain draws into (headless: one of its own), frame generation for this frame, and the interface's cleared layer.
    /// </summary>
    GpuTexture PrepareTargets(Vector2D<int> size, string? takeFile, out GpuTexture uiTarget)
    {
        // The chain and the panel draw into the window's backbuffer (made again when the size changes).
        var backbuffer = _display.Backbuffer
            ?? (_headlessTarget ??= _display.Context.CreateTexture(new TextureDesc(InternalFormat.Rgba8, size.X, size.Y,
            Use: TextureUse.ColourTarget | TextureUse.TransferSrc | TextureUse.Sampled, Name: "headless backbuffer")));
        host.Gpu.Post!.Target = backbuffer;
        host.Gpu.Post.Options.HdrPeak = _display.HdrActive ? PostOptions.HdrPeakFor(host.Config.HdrPeakNits, host.Config.HdrPaperNitsNow) : 0;
        // Frame generation (the one loaded at start-up): made on the first frame; wanted except under the loading screen and for saved pictures.
        if (_frameGen is FrameGeneration && _display.FrameGenerationLayer is null)
        { _frameGen.Dispose(); _frameGen = null; }   // the layer fell back to the plain swapchain
        if (!_frameGenTried)
        {
            _frameGenTried = true;
            _frameGen = FrameGenerationSetup.CreateFrameGenerator(_display);
        }
        bool frameGenWanted = !host.Covered && !host.ScreenshotRequested && !host.FeedbackCapture.FeedbackShotRequested && takeFile is null;
        _frameGen?.BeginFrame(frameGenWanted, size.X, size.Y);
        host.Gpu.Post!.FrameGen = _frameGen;
        // The interface draws into a transparent layer of its own, laid over the backbuffer just before the present.
        _uiLayer ??= new UiLayer(_display.Context);
        uiTarget = _uiLayer.Clear(size.X, size.Y);
        if (_overlay is not null) _overlay.Target = uiTarget;
        return backbuffer;
    }

    /// <summary>
    /// The world, or the loading sky at the stars of a sky arrival while its scene loads; nothing else while a scene loads.
    /// </summary>
    void DrawWorldOrSky(Vector2D<int> size, bool covered)
    {
        bool sceneLoading = host.ActiveSceneLoad is not null;
        // Under the cover the streams take bigger shares of a frame (smooth frames do not matter there), until the warm-up's content is ready: its
        // run of smooth frames is then measured at the play budgets.
        bool streamsCovered = covered && !(host.Warming && host.ReadySeconds > 0);
        if (host.Gpu.Characters is { } coveredCharacters) coveredCharacters.Covered = streamsCovered;
        if (host.Gpu.Foliage is { } coveredFoliage) coveredFoliage.Covered = streamsCovered;
        if (host.Gpu.Objects is { } coveredObjects) coveredObjects.Covered = streamsCovered;
        if (host.Nav is not null) host.Nav.Covered = covered;
        // The loading screen follows the interface's mode as it is now (the settings can switch it at any time); only the Faithful mode counts loads.
        bool faithfulUi = host.Player.UiMode == Data.Ui.UiMode.Faithful;
        host.LoadBar.Update(faithfulUi ? host.PendingLoads() : 0);
        host.Player.LoadBar = host.LoadBar;
        if (host.Loading is not null) (host.Loading.LoadBar, host.Loading.Faithful) = (host.LoadBar, faithfulUi);
        if (sceneLoading && host.SkyArrival is { AtStars: true })
            WorldFrame.DrawLoadingSky(host.Gpu, host.Scene, host.Camera, size.X, size.Y, host.PresentationHour ?? 0, (float)host.AnimSeconds,
                host.WorldOptions.FogDistance);
        else if (!sceneLoading || host.SkyArrival is not null)
        {
            // The duel's laps and recorded flights replay exactly, and the play benchmark is deterministic from its save: their ticks stay in step
            // with the frame.
            host.TickAhead = !covered && host.SkyArrival is null && !host.Duel.IsDuel && !_cinema.Recording
                && host.GameOptions.PlayBench is null && host.TickAheadWanted();
            host.DrawWorld(size.X, size.Y);
            host.TickAhead = false;
        }
    }

    /// <summary>
    /// The game's interface (markers, HUD, panels), the debug overlays, the settings panel, the cinema's editor and F11's boxes and paths, in that
    /// order.
    /// </summary>
    void DrawInterface(Vector2D<int> size, bool covered, bool shot, bool take, bool feedbackShot)
    {
        // Hidden by F7, the cinema's clean picture and in recorded frames.
        bool revealing = host.Loading is { Exiting: true } && host.ActiveSceneLoad is null && !host.Warming && host.SkyArrival is null;
        // Bake/upload menu surfaces, fonts and pipelines behind the opaque cover, before timing the reveal.
        bool warmTitleUi = host.Warming && host.Player.TitleOpen && host.SkyArrival is null;
        bool ui = !host.AgentChannel.InterfaceHidden && !_cinema.Clean && !take && (!covered || revealing || warmTitleUi);
        host.Player.UiVisible = ui && !host.GameOptions.NoPopulation;
        host.ScreenshotRequested = false;
        DrawHud(size, ui, covered, take);
        // The debug overlays are left out of saved pictures: the statistics at the top left, the key list below them, the profiler.
        if (ui && _overlay is not null && !shot && !feedbackShot) DrawDebugPanels(_overlay, size, covered);
        StageClock.UiPart(StageClock.UiDebug);
        if (ui && !feedbackShot) _panel?.Draw(size.X, size.Y);
        StageClock.UiPart(StageClock.UiSettings);
        // Over everything: the letterbox and the cinema's editor (F6, also with F7); the game hides its status line.
        if (_overlay is not null && !covered)
            _cinema.Draw(_overlay, size.X, size.Y,
                shot || take || feedbackShot || (host.AgentChannel.InterfaceHidden && !_cinema.EditorVisible), showStatus: false);
        if (_overlay is not null && !covered && _photo.Active)
        {
            _photo.Panel.Draw(_overlay, size.X, size.Y, shot || take || feedbackShot);
            _overlay.Flush(size.X, size.Y);
        }
        // The character boxes and paths (F11's tickboxes, also F10's Overlays rows), on top of everything; both stay once ticked, with the statistics
        // hidden too.
        if (ui && _overlay is not null && !covered && !shot && !feedbackShot)
        {
            if (host.DebugOverlays.CharacterBoxes && host.Gpu.Characters is { } boxed)
                host.DebugOverlays.DrawCharacterBoxes(_overlay, boxed, host.Camera, size.X, size.Y);
            if (host.DebugOverlays.WalkPaths) host.DebugOverlays.DrawWalkPaths(_overlay, host.Session.CurrentSnapshot, host.Camera, size.X, size.Y);
        }
        StageClock.UiPart(StageClock.UiCinema);
    }

    /// <summary>
    /// The markers, the steering, the HUD (or the speech alone with the interface hidden), and the sandbox's and the battle's own panels.
    /// </summary>
    void DrawHud(Vector2D<int> size, bool ui, bool covered, bool take)
    {
        if (ui && _overlay is not null && !host.GameOptions.NoPopulation) host.DrawMarkers(_overlay, size.X, size.Y);
        StageClock.UiPart(StageClock.UiMarkers);
        if (!host.GameOptions.NoPopulation && !covered && _panel?.Visible != true) host.Player.Steer();
        StageClock.UiPart(StageClock.UiSteer);
        if (ui && _overlay is not null && !host.GameOptions.NoPopulation) host.Player.Draw(_overlay, size.X, size.Y);
        else if (host.AgentChannel.InterfaceHidden && !_cinema.Clean && !take && !covered && _overlay is not null && !host.GameOptions.NoPopulation)
            host.Player.DrawSpeechOnly(_overlay, size.X, size.Y);
        if (ui && _overlay is not null && host.Sandbox.IsSandbox) host.Sandbox.DrawSandbox(_overlay, size.X, size.Y);
        if (ui && _overlay is not null && host.Battle.IsBattle) host.Battle.DrawBattleCounter(_overlay, size.X, size.Y);
        StageClock.UiPart(StageClock.UiHud);
    }

    /// <summary>
    /// Below the HUD, which holds the top left corner in the game: the fps counter, the terrain sandbox's tile, F11's statistics, then the menus and
    /// the profiler.
    /// </summary>
    void DrawDebugPanels(DebugOverlay overlay, Vector2D<int> size, bool covered)
    {
        float panelsBottom = host.GameOptions.NoPopulation ? 0 : host.Player.HudBottom;
        if (host.DebugMenu.FpsVisible && _fpsText.Length > 0) panelsBottom = DrawLabel(overlay, size, _fpsText, panelsBottom);
        // The terrain sandbox: the texture field's tile under the camera.
        if (GroundRelief.FieldCentre is not null && host.Gpu.Terrain.FieldLabelAt(host.Camera.Target.X, host.Camera.Target.Z) is { } tileLabel)
            panelsBottom = DrawLabel(overlay, size, tileLabel, panelsBottom);
        if (host.DebugOverlays.WalkPaths && !covered)
            panelsBottom = DebugOverlays.DrawWalkPathsLabel(overlay, host.Session.CurrentSnapshot, size.X, size.Y, panelsBottom);
        host.DebugOverlays.CharacterBoxesToggle = host.DebugOverlays.WalkPathsToggle = null;
        float menuTop = panelsBottom;   // the debug menu stands at the right beside the statistics, else below the panels
        if (host.DebugMenu.StatsVisible && _stats.Count > 0)
        {
            host.DebugOverlays.TickStatsToggles(_stats);
            panelsBottom = overlay.Panel(size.X, size.Y, "Meitou   (F11 hides this)", _stats, panelsBottom, laidOut: host.DebugOverlays.StatsLaidOut);
            host.DebugOverlays.FindStatsToggles(overlay.CharWidth);
        }
        if (host.DebugMenu.DebugMenuOpen)
            host.DebugMenu.DrawDebugMenu(overlay, size.X, size.Y, host.DebugMenu.StatsVisible ? menuTop : panelsBottom,
                atRight: host.DebugMenu.StatsVisible);
        if (host.Player.WorldMap.MapPick is not null && host.Player.WorldMap.MapTabShown) host.DebugMenu.DrawMapPickHint(overlay, size.X, size.Y);
        if (host.DebugMenu.ExplodeClickOn) host.DebugMenu.DrawExplodeClickHint(overlay, size.X, size.Y);
        if (host.DebugMenu.InspectorMenuOpen) host.DebugMenu.DrawInspectorMenu(overlay, size.X, size.Y, panelsBottom);
        if (host.InteriorEditorHost.InteriorEditorOpen) host.InteriorEditorHost.DrawInteriorEditor(overlay, size.X, size.Y, panelsBottom);
        if (host.DevConsoleHost.ConsoleOpen) host.DevConsoleHost.DrawConsole(overlay, size.X, size.Y);
        host.DebugMenu.Profiler?.Draw(overlay, size.X, size.Y);
        host.DebugMenu.Profiler?.DrawVram(overlay, size.X, size.Y);
    }

    /// <summary>
    /// One line in a panel at the left below <paramref name="top"/>; returns the new bottom (the panels add their own 16 px margin).
    /// </summary>
    static float DrawLabel(DebugOverlay overlay, Vector2D<int> size, string text, float top)
    {
        float w = text.Length * overlay.CharWidth + 12, h = overlay.LineHeight + 8, y = top + 8;
        overlay.Rect(8, y, 8 + w, y + h, DebugOverlay.PanelColour);
        overlay.Text(text, 14, y + 4, DebugOverlay.TextColour);
        overlay.Flush(size.X, size.Y);
        return y + h - 8;
    }

    void DrawLoadingCover(Vector2D<int> size, bool covered, GpuTexture uiTarget)
    {
        if (covered && host.Loading is not null && host.LoadingOverlay is not null)
        {
            host.LoadingOverlay.Target = uiTarget;
            if (host.SkyArrival is null) host.Loading.Draw(size.X, size.Y);
            else if (host.Loading.Exiting
                    || host.SkyArrival is { AtStars: true, Arriving: false } && host.ActiveSceneLoad?.Clock.Elapsed.TotalSeconds >= 2)
                host.Loading.Draw(size.X, size.Y, background: false);
        }
        else host.Loading?.ReleaseWhenGone();
    }

    /// <summary>
    /// The mouse pointer over everything, as the original's Pointer layer; never in saved pictures. What the right button would do picks it, the
    /// arrow over the settings panel, the cinema's editor and the loading screen. The free camera hides it (the original's <c>invisible</c>);
    /// Hardware mouse shows Windows' cursor instead; enhanced, the action's pointer beside it and the hand over what a click acts on. With frame
    /// generation the window system draws the game's pointer (SystemPointer): in a frame it would come a held-back frame late and be interpolated.
    /// </summary>
    void DrawPointer(Vector2D<int> size, FramePointer pointer)
    {
        if (_overlay is null || pointer.Mouse is not { } pointerMouse) return;
        bool freeLook = pointer.FreeLook;
        bool byWindow = _frameGen is { Wanted: true } && !host.Config.HardwareMouse && !freeLook;
        var cursorMode = _orbitAnchor is not null ? Silk.NET.Input.CursorMode.Disabled
            : (host.Config.HardwareMouse || byWindow) && !freeLook ? Silk.NET.Input.CursorMode.Normal : Silk.NET.Input.CursorMode.Hidden;
        if (cursorMode != _windowCursor) pointerMouse.Cursor.CursorMode = _windowCursor = cursorMode;
        string pointerName = pointer.OverWorld && !_photo.Active ? host.Player.PointerPanel.PointerName : "arrow";
        bool enhanced = host.Config.HardwareMouse && host.Config.EnhancedHardwareMouse && !freeLook;
        bool hand = enhanced
            && (pointer.OverWorld ? host.Player.PointerPanel.PointsAtClickable(pointerName) : _panel?.OverClickable(pointer.At) == true);
        if (byWindow)
        {
            _systemPointer.Show(pointerMouse.Cursor, host.Player, pointerName);
            return;
        }
        _systemPointer.Standard(pointerMouse.Cursor, hand ? Silk.NET.Input.StandardCursor.Hand : Silk.NET.Input.StandardCursor.Default);
        if (!host.Config.HardwareMouse && !freeLook) host.Player.PointerPanel.DrawPointer(pointerName, pointer.At, size.X, size.Y);
        else if (enhanced) host.Player.PointerPanel.DrawPointerBeside(pointerName, pointer.At, size.X, size.Y);
    }

    /// <summary>
    /// World and interface into the top-down picture the window shows (and a screenshot reads), presented. With frame generation on this frame the
    /// window shows the world alone (the backbuffer, flipped by the present) and the layer lays the interface over every frame, generated ones too:
    /// then null.
    /// </summary>
    GpuTexture? Present(GpuTexture backbuffer, GpuTexture uiTarget, bool capturing)
    {
        bool generating = _frameGen is not null && _frameGen.Finish(backbuffer, uiTarget);
        bool worldAlone = generating && _frameGen!.PresentsWorldAlone, hdrShown = _display.HdrActive && !worldAlone;

        // Pictures saved from an HDR frame are the SDR composite, the shown one the PQ composite.
        var finalPicture = worldAlone ? null : !hdrShown || capturing ? _uiLayer!.Composite(backbuffer) : null;
        var shownPicture = hdrShown ? _uiLayer!.CompositeHdr(backbuffer, host.Config.HdrPaperNitsNow) : finalPicture;
        StageClock.UiPart(StageClock.UiComposite);
        if (GpuContext.DiagSubmit) _display.Context.SubmitAndWait();
        _display.Present(shownPicture);
        host.DebugMenu.Profiler?.EndFrame();
        host.PlayBench.BenchLap(7);
        return finalPicture;
    }

    #endregion

    #region Statistics and profiling

    World? _simNamesFor;

    string[] _simNames = [];

    double[] _simMs = [];

    const double SimWindowSeconds = 5;

    readonly Dictionary<string, double> _simWindowMs = [];

    double _simWindowStart = -1, _simWindowMain, _simWindowAhead;

    long _simWindowTicks;

    int _simWindowFrames;

    long _simWindowRagdolls, _simWindowMoving, _simWindowAwake, _simWindowCarried, _simWindowWhole, _simWindowLimbs;

    int _simWindowStepping;

    /// <summary>
    /// The graphics card's time and the drawn triangles, summed for F11's statistics.
    /// </summary>
    void TallyFrame()
    {
        if (_display.Context.GpuFrameMs > 0)
        {
            _gpuSum += _display.Context.GpuFrameMs;
            _gpuSamples++;
        }
        _statsFrames++;
        _terrainTris += host.Gpu.Terrain.DrawnTriangles;
        _characterTris += host.Gpu.Characters?.DrawnTriangles ?? 0;
        if (host.Gpu.Foliage is { Enabled: true } drawnFoliage) _grassBlades += drawnFoliage.DrawnBlades;
    }

    /// <summary>
    /// The pictures asked of this frame: the cinema's take, the control channel's shot, a screenshot (tiled when screens.cfg multiplies it), the
    /// feedback shot.
    /// </summary>
    void SavePictures(Vector2D<int> size, string? takeFile, bool shot, bool feedbackShot, GpuTexture picture, bool topDown)
    {
        if (takeFile is not null)
        {
            FramebufferCapture.SavePng(_display.Context, picture, takeFile, size.X, size.Y, topDown: topDown);
            _cinema.FrameDone();
        }
        else if (shot && host.AgentChannel.ShotPath is { } shotPath)
        {
            host.AgentChannel.SaveControlShot(_display.Context, picture, size.X, size.Y, topDown: topDown);
            host.Photo?.PictureSaved(shotPath);
        }
        else if (shot && GameScreenshots.Multiplier(host.Install.Root) is >= 2 and var multiplier)
            SaveSupersampled(_display.Context, multiplier, size.X, size.Y);
        else if (shot) SaveScreenshot(_display.Context, picture, size.X, size.Y, topDown: topDown);
        if (shot) host.Player.EndScreenshot();
        if (feedbackShot)
        {
            host.Player.FeedbackPanel.ShotWithoutMenus = false;
            var captured = FramebufferCapture.ReadPicture(_display.Context, picture, size.X, size.Y, topDown: topDown);
            host.FeedbackCapture.FeedbackCaptured(captured, size.X, size.Y);
        }
    }

    /// <summary>
    /// Once a second: F11's statistics, the frame and the simulation, then the renderer's sections.
    /// </summary>
    void UpdateStats(double dt)
    {
        _statsTimer += dt;
        if (_statsTimer < 1) return;
        _stats.Clear();
        _stats.Add("Performance");
        string limit = _display.VSync ? "vsync" : _fpsLimit > 0 ? $"limit {_fpsLimit}" : "uncapped";
        string gpuMs = _gpuSamples > 0 ? $"{_gpuSum / _gpuSamples:0.0}" : "-";
        _stats.Add($"  frame rate    {_statsFrames / _statsTimer:0} fps{FrameGenerationSetup.FrameGenerationShown(_frameGen)}, {limit}; graphics card {gpuMs} ms a frame");
        long ticks = host.Session.Simulation.TotalTicks - _statsTicks;
        _statsTicks = host.Session.Simulation.TotalTicks;
        _stats.Add($"  simulation    {ticks / _statsTimer:0} ticks/s, {host.Session.CurrentSnapshot.Characters.Count} characters, {host.Session.Simulation.DroppedTicks} ticks dropped since the start");
        long objectTris = host.Gpu.Objects?.Totals[0, 1] ?? 0;
        int frames = Math.Max(_statsFrames, 1);
        var triangles = new WorldStats.Triangles(_terrainTris / frames, (objectTris - _objectTris0) / (double)frames,
            host.Gpu.Foliage?.ColourTrianglesPerFrame, _characterTris / frames, _grassBlades / frames);
        WorldStats.Add(_stats, _display.Context, host.Gpu, host.Camera.Eye, triangles, characterBoxes: true);
        host.Gpu.Foliage?.ResetDrawTally();
        _objectTris0 = objectTris;
        _terrainTris = _characterTris = _grassBlades = 0;
        _statsTimer = _gpuSum = 0;
        _statsFrames = _gpuSamples = 0;
    }

    /// <summary>
    /// --quit-after: the run's profile, audio, menu and smoke lines.
    /// </summary>
    void PrintRunSummary(double now)
    {
        var snapshot = host.Session.CurrentSnapshot;
        string characters = host.Gpu.Characters is { } chars ? chars.Statistics() : "no characters";
        Console.WriteLine($"profile   {snapshot.Characters.Count} characters in the snapshot, {snapshot.Characters.Count(c => c.Appearance is not null)} drawable; per frame: simulation advance {host.AdvanceTotal / Math.Max(_totalFrames, 1):0.000} ms ({host.Session.Simulation.TotalTicks} ticks, {host.AdvanceTotal / Math.Max(host.Session.Simulation.TotalTicks, 1):0.000} ms per tick), host draw-list fill {host.FillTotal / Math.Max(host.FillCalls, 1):0.000} ms, world draw cpu {_drawTotal / Math.Max(_totalFrames, 1):0.00} ms, {characters}");
        if (host.Sound.Audio is not null)
        {
            Console.WriteLine($"audio     {host.Sound.Audio.StatsLine()}");
            Console.WriteLine($"audio     world: {host.Sound.WorldAudioStats()}");
            Console.WriteLine($"audio     characters: {host.Sound.CharacterAudioStats()}, {host.Sound.UiAudioStats()}");
        }
        if (host.MenuFrames > 0) Console.WriteLine($"menu      {host.MenuFrames} visible frames in {host.MenuFrameSeconds:0.0} s: {host.MenuFrames / host.MenuFrameSeconds:0} fps, {host.MenuSlowFrames} frames over 100 ms, longest {host.MenuLongestFrame * 1000:0.0} ms");
        Console.WriteLine($"smoke     {_totalFrames} frames in {now:0.0} s: {_totalFrames / now:0} fps on average, {host.Session.Ticks.TotalTicks} control ticks, {host.Session.Simulation.TotalTicks} simulation ticks, game time {host.Session.Clock.TimeText}");
    }

    // ---- pictures and profiles ----

    /// <summary>
    /// The look the command line chose: Given when <c>--meitou</c> or <c>--faithful</c> is on it; Faithful by the last <c>all</c> (<c>--faithful
    /// all</c> true, <c>--meitou all</c> false), null when only some switches are named (the menu then reads the look from the switches without
    /// saving it).
    /// </summary>
    static (bool Given, bool? Faithful) CommandLineLook()
    {
        var args = Environment.GetCommandLineArgs();
        bool given = false;
        bool? faithful = null;
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] is "--meitou" or "--faithful")
            {
                given = true;
                if (args[i + 1].Split(',', StringSplitOptions.TrimEntries).Contains("all")) faithful = args[i] == "--faithful";
            }
        return (given, faithful);
    }

    void SaveScreenshot(GpuContext context, GpuTexture picture, int width, int height, bool topDown = false)
    {
        var file = GameScreenshots.Next(host.Install.Root);
        FramebufferCapture.SavePng(context, picture, file, width, height, topDown);
        Console.WriteLine($"saved     {file}");
    }

    /// <summary>
    /// The tiled shot of <c>screens.cfg</c>'s <c>multiplier</c> n (docs/game/ui-input.md "Screenshots"): the world alone, no interface, n x n tiles
    /// of window size, each drawn with the camera frustum offset to it (<see cref="ViewWindow"/>) and blitted into one picture n times the window in
    /// each direction, as the original does. Screen-space effects therefore keep the window's pixel scale. Each tile is drawn <see cref="TileGuard"/>
    /// pixels larger on every side and cropped, so what samples neighbours (ambient occlusion, bloom) has them at the tile's border; its temporal
    /// history is dropped and warmed up again. The exposure of the live view is pinned for the whole shot, as the original zeroes the adaptation
    /// rate.
    /// </summary>
    void SaveSupersampled(GpuContext context, int n, int width, int height) => SaveTiles(context, n, width, height,
        GameScreenshots.Next(host.Install.Root));

    void SaveTiles(GpuContext context, int n, int width, int height, string file)
    {
        var post = host.Gpu.Post!;
        int warm = post.WarmupFrames, guard = TileGuard(width, height), tw = width + 2 * guard, th = height + 2 * guard;
        var (adapted, _) = post.ReadExposure();
        if (!float.IsNaN(adapted)) post.HoldExposure(adapted, n * n * (warm + 1) + 2);
        var live = post.Target;
        using var tile = context.CreateTexture(new TextureDesc(InternalFormat.Rgba8, tw, th,
            Use: TextureUse.ColourTarget | TextureUse.TransferSrc | TextureUse.Sampled, Name: "screenshot tile"));
        var picture = new byte[n * width * n * height * 4];
        post.Target = tile;
        try
        {
            for (int row = 0; row < n; row++)
                for (int column = 0; column < n; column++)
                {
                    ViewWindow.Current = ViewWindow.Tile(n, column, row, width, height, guard);
                    post.ResetHistory();
                    for (int i = 0; i <= warm; i++)
                    {
                        host.DrawWorld(tw, th);
                        context.EndFrame();   // headless frames: the window's own frame was presented already
                    }
                    context.Finish();
                    BlitTile(FramebufferCapture.ReadPicture(context, tile, tw, th), tw, guard, picture, n * width,
                        column * width, row * height, width, height);
                }
        }
        finally { ViewWindow.Current = ViewWindow.Whole; post.Target = live; post.ResetHistory(); }
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (dir is not null) Directory.CreateDirectory(dir);
        Data.Textures.PngWriter.Write(file, n * width, n * height, picture);
        Console.WriteLine($"saved     {file} ({n * width} x {n * height}, multiplier {n})");
    }

    /// <summary>
    /// The guard band of a tiled shot's tiles: an eighth of the height, at least 16 pixels.
    /// </summary>
    static int TileGuard(int width, int height) => Math.Max(16, Math.Min(width, height) / 8);

    /// <summary>
    /// Copies the middle <paramref name="width"/> x <paramref name="height"/> of a drawn tile (<paramref name="guard"/> pixels of guard round it)
    /// into the picture at (<paramref name="x"/>, <paramref name="y"/>).
    /// </summary>
    static void BlitTile(byte[] tile, int tileWidth, int guard, byte[] picture, int pictureWidth, int x, int y, int width, int height)
    {
        for (int r = 0; r < height; r++)
            tile.AsSpan(((r + guard) * tileWidth + guard) * 4, width * 4).CopyTo(picture.AsSpan(((y + r) * pictureWidth + x) * 4));
    }

    /// <summary>
    /// --log-spikes, at exit: the most allocated types over the run and the collections by generation, their pauses summed.
    /// </summary>
    static void PrintAllocations(BenchGcListener gc)
    {
        var (allocated, collections) = gc.Take();
        long total = allocated.Values.Sum();
        Console.WriteLine($"spikes    allocated {total / 1048576.0:0} MB over the run (sampled); collections {string.Join(", ", collections.GroupBy(c => c.Generation).OrderBy(g => g.Key)
            .Select(g => $"gen{g.Key} x{g.Count()} {g.Sum(c => c.PauseMs):0} ms"))}; the most allocated types (MB):");
        foreach (var (type, bytes) in allocated.OrderByDescending(p => p.Value).Take(30))
            Console.WriteLine($"spikes      {type,-90} {bytes / 1048576.0,9:0}");
    }

    /// <summary>
    /// Ctrl+Shift+F12: what is in video memory now, to vram-&lt;time&gt;.txt beside log.txt (<see cref="FrameProfiler.DumpVram"/>).
    /// </summary>
    void DumpVram(FrameProfiler? profiler)
    {
        if (profiler is null) return;
        string path = Path.Combine(AppContext.BaseDirectory, $"vram-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        void Caches(TextWriter w)
        {
            if (host.Gpu.Guard is { } guard) { w.WriteLine(guard.Status); w.WriteLine(); }
            host.Gpu.Objects?.WriteViewReport(w);
            host.Gpu.Objects?.Textures.WriteReport(w);
            host.Gpu.Foliage?.Textures.WriteReport(w);
            host.Gpu.Characters?.Textures.WriteReport(w);
        }
        try { profiler.DumpVram(path, Caches); Console.WriteLine($"vram      dumped to {path}"); }
        catch (IOException e) { Console.WriteLine($"vram      dump failed: {e.Message}"); }
    }

    // F12's sim parts: the names (the tick phases, the time outside the world's ticks, the world's systems), made again when the world is, and the
    // frame's ms.

    /// <summary>
    /// Hands the frame's <paramref name="advanceMs"/> to the profiler with the world's tick profile split into phases and systems, and clears the
    /// profile.
    /// </summary>
    internal void SimProfile(FrameProfiler profiler, double advanceMs)
    {
        var world = host.Session.World;
        var profile = world.Profile;
        int phases = TickProfile.PhaseNames.Length;
        int systems = world.Systems.Count, parts = phases + 1 + systems;
        if (_simNamesFor != world || _simNames.Length != parts + profile.Parts.Count + 1)
        {
            _simNamesFor = world;
            _simNames =
            [
                .. TickProfile.PhaseNames,
                "outside ticks",
                .. world.Systems.Select(s => s.GetType().Name is var n && n.EndsWith("System", StringComparison.Ordinal) && n.Length > 6 ? n[..^6] : n),
                .. profile.Parts.Select(p => p.Name),
                "tick ahead",
            ];
            _simMs = new double[_simNames.Length];
        }
        Array.Clear(_simMs);
        double inTicks = 0, ahead = host.Session.TakeAheadMs();
        for (int p = 0; p < phases; p++) inTicks += _simMs[p] = profile.PhaseMs[p];
        // The phases also hold the tick run ahead during the last draw (docs/simulation.md "Tick ahead"), which is not in this frame's advance.
        _simMs[phases] = Math.Max(0, advanceMs - Math.Max(0, inTicks - ahead));
        for (int k = 0; k < profile.SystemMs.Length && phases + 1 + k < parts; k++) _simMs[phases + 1 + k] = profile.SystemMs[k];
        for (int k = 0; k < profile.Parts.Count && parts + k < _simMs.Length; k++) _simMs[parts + k] = profile.Parts[k].Ms;
        _simMs[^1] = ahead;
        int paths = world.Systems.OfType<MovementSystem>().FirstOrDefault()?.Paths.Pending ?? 0;
        profiler.Outside(advanceMs, profile.Ticks, _simNames, _simMs, phases + 1, systems, paths);
        // --log-spikes: a slow frame's simulation by system and part (the phases only add up what these hold).
        if (SpikeLog.Enabled && advanceMs > GameHost.SpikeSimMs)
            for (int k = phases + 1; k < _simMs.Length; k++)
                if (_simMs[k] >= 2) SpikeLog.Note($"sim {_simNames[k]}", ms: _simMs[k]);
        if (SpikeLog.Enabled) SimWindow(profile, advanceMs, ahead, phases + 1, parts);
        profile.Clear();
    }

    // --log-spikes: the simulation's steady cost, summed over a window of real time (the spike notes only see frames over SpikeSimMs).
    // The ragdolls sampled once a frame: in play, moving, the physics' awake bodies, frames with any moving.

    /// <summary>
    /// --log-spikes: every <see cref="SimWindowSeconds"/> one line of the speed, ticks a second, the sim's ms a frame, then ms a tick by phase, by
    /// system, by system in the slow world phase and by part, biggest first. Keys carry their list's prefix: '#' phase, '~' slow world, '.' part.
    /// </summary>
    void SimWindow(TickProfile profile, double advanceMs, double ahead, int firstSystem, int firstPart)
    {
        double now = host.RealTime.Elapsed.TotalSeconds;
        if (_simWindowStart < 0) _simWindowStart = now;
        (_simWindowTicks, _simWindowMain, _simWindowAhead, _simWindowFrames) = (_simWindowTicks + profile.Ticks, _simWindowMain + advanceMs, _simWindowAhead + ahead, _simWindowFrames + 1);
        void Add(string key, double ms) => _simWindowMs[key] = _simWindowMs.GetValueOrDefault(key) + ms;
        for (int p = 0; p < firstSystem - 1; p++) Add("#" + _simNames[p], _simMs[p]);
        for (int k = firstSystem; k < _simMs.Length - 1; k++) Add((k >= firstPart ? "." : "") + _simNames[k], _simMs[k]);
        for (int k = 0; k < profile.SystemMs.Length && firstSystem + k < firstPart; k++)
            Add("~" + _simNames[firstSystem + k], profile.Ms(TickProfile.SlowWorld, k));
        if (host.Session.World.Systems.OfType<Simulation.Ragdolls.RagdollSystem>().FirstOrDefault() is { } rag)
        {
            (_simWindowRagdolls, _simWindowMoving, _simWindowAwake, _simWindowStepping) = (_simWindowRagdolls + rag.Count, _simWindowMoving + rag.Moving, _simWindowAwake + rag.AwakeBodies, _simWindowStepping + (rag.Moving > 0 ? 1 : 0));
            (_simWindowCarried, _simWindowWhole, _simWindowLimbs) = (_simWindowCarried + rag.MovingCarried, _simWindowWhole + rag.MovingWhole, _simWindowLimbs + rag.MovingLimbs);
        }
        double seconds = now - _simWindowStart;
        if (seconds < SimWindowSeconds) return;
        if (_simWindowTicks > 0)
        {
            var inv = CultureInfo.InvariantCulture;
            string Top(char kind, int count) => string.Join(", ",
                _simWindowMs.Where(p => (p.Key[0] is '#' or '~' or '.' ? p.Key[0] : ' ') == kind && p.Value / _simWindowTicks >= 0.01)
                .OrderByDescending(p => p.Value).Take(count)
                .Select(p => string.Create(inv, $"{p.Key.TrimStart('#', '~', '.')} {p.Value / _simWindowTicks:0.00}")));
            double tickMs = _simWindowMs.Where(p => p.Key[0] == '#').Sum(p => p.Value) / _simWindowTicks;
            Console.WriteLine(string.Create(inv, $"simprof   {host.Session.Simulation.Speed:0}x {_simWindowFrames / seconds:0} fps, {_simWindowTicks / seconds:0} ticks/s, {host.Session.World.Snapshot.Characters.Count} characters; ") +
                string.Create(inv, $"ms a frame: in frame {_simWindowMain / _simWindowFrames:0.00}, ahead {_simWindowAhead / _simWindowFrames:0.00}; ms a tick {tickMs:0.00}: ") +
                $"phases: {Top('#', 10)}; systems: {Top(' ', 12)}; slow world: {Top('~', 12)}; parts: {Top('.', 12)}; " +
                string.Create(inv, $"ragdolls {(double)_simWindowRagdolls / _simWindowFrames:0.0}, moving {(double)_simWindowMoving / _simWindowFrames:0.0} (carried {(double)_simWindowCarried / _simWindowFrames:0.0}, whole {(double)_simWindowWhole / _simWindowFrames:0.0}, limbs {(double)_simWindowLimbs / _simWindowFrames:0.0}), awake bodies {(double)_simWindowAwake / _simWindowFrames:0}, stepping {100.0 * _simWindowStepping / _simWindowFrames:0}% of frames"));
        }
        _simWindowMs.Clear();
        (_simWindowStart, _simWindowTicks, _simWindowMain, _simWindowAhead, _simWindowFrames) = (now, 0, 0, 0, 0);
        (_simWindowRagdolls, _simWindowMoving, _simWindowAwake, _simWindowStepping, _simWindowCarried, _simWindowWhole, _simWindowLimbs) = (0, 0, 0, 0, 0, 0, 0);
    }

    // ---- shut-down ----

    #endregion

    #region Shutdown

    /// <summary>
    /// The bench's report, the save in flight, the settings, the run's logs, then the renderer and the interface let go.
    /// </summary>
    void ShutDown(BenchGcListener? spikeGc)
    {
        host.PlayBench.WritePlayBench(FramebufferSizeNow());
        // A save still being written finishes before the game closes (the original's quit waits while its save manager is busy, FUN_14082b6e0,
        // Verified).
        try
        {
            host.Saves.SaveWrite?.Wait();
        }
        catch (AggregateException)
        {
        }
        SaveSettings();
        // Collect the last frames' GPU timestamps and print the --log-spikes run summary: the game's own one-shot runs
        // otherwise dispose the profiler before the frame ring has delivered them.
        host.DebugMenu.Profiler?.Flush();
        if (SpikeLog.Enabled) SpikeLog.PrintSummary();
        if (spikeGc is not null) PrintAllocations(spikeGc);
        if (Simulation.Navigation.NavQuery.Stats is { Searches: > 0 } paths)
            Console.WriteLine($"paths     {paths.Searches} searches, {paths.Expanded / paths.Searches} polygons each on average, {paths.Exhausted} ran to the limit of {Simulation.Navigation.NavQuery.MaxIterations}");
        host.DebugMenu.Profiler?.Dispose();
        host.Player.DisposeUi();
        host.EndLoadingScreen();
        _overlay?.Dispose();
        if (host.Gpu.Post is { } postChain) postChain.FrameGen = null;
        _frameGen?.Dispose();   // before the display: it configures the layer's swapchain
        _uiLayer?.Dispose();
        _headlessTarget?.Dispose();
        host.TitleScreen.DisposeTitleScene();
        host.Gpu.Dispose();
        _silkInput?.Dispose();
    }

    /// <summary>
    /// The settings into meitou.user.json; the command line's window and vsync overrides alone stay this run's.
    /// </summary>
    void SaveSettings()
    {
        if (_display.VSync != _savedVSync) host.Config.VSync = _display.VSync;
        if (_fpsLimit != _initialFpsLimit) host.Config.FpsLimit = _fpsLimit;
        if (FullScreenOn() != _savedFullScreen) host.Config.FullScreen = FullScreenOn();
        _savedVSync = _display.VSync;
        _savedFullScreen = FullScreenOn();
        _initialFpsLimit = _fpsLimit;
        if (_panel is not null && !_lowEnd)
        {
            foreach (var slider in _panel.Sliders)
                if (!_runOnly.Contains(slider.Label) && !host.GfxRunOnly.Contains(slider.Label)) host.Config.Graphics[slider.Label] = slider.Get();
            foreach (var toggle in _panel.Toggles)
                if (!_runOnly.Contains(SwitchKey(toggle.Label)) && !host.GfxRunOnly.Contains(toggle.Label)) host.Config.Graphics[SwitchKey(toggle.Label)] = toggle.Get() ? 1 : 0;
            _panel.Tiers.SavePreset();
        }
        host.Config.Bindings = host.Session.Bindings.ToDictionary();
        if (!host.GameOptions.LowEnd || host.Player.UiMode != Data.Ui.UiMode.Faithful)
            host.Config.UiMode = host.Player.UiMode;   // --low-end's Faithful UI is for the run
        host.Config.Save();
    }

    static string SwitchKey(string label) => SettingsMenu.SwitchKeyPrefix + label;
    #endregion

}