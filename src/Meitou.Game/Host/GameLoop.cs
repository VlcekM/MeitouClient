using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Game.DevTools;
using Meitou.Game.Host.Cameras;
using Meitou.Game.Host.Input;
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
/// The interactive run on the Vulkan display, windowed or headless (docs/engine.md "Host and loading"): the window and the device, the world loaded behind the
/// loading screen, the settings panel, the keyboard and mouse, then one <see cref="Frame"/> after another until the window closes or the game quits.
/// </summary>
sealed class GameLoop(GameHost host)
{
    // The display and its window (none headless: --headless draws into an offscreen picture), and the frame rate as this run started.
    VulkanDisplay display = null!;
    IWindow? window;
    bool headless, vsync;
    int fpsLimit, initialFpsLimit;
    bool savedVSync, savedFullScreen;

    /// <summary>Read at start-up only: a change in the settings waits for a restart.</summary>
    FrameGenerationMethod frameGenerationAtStart;

    // The debug overlay the interface draws through, the Faithful / Meitou switches and the settings panel (null without an overlay).
    DebugOverlay? overlay;
    IReadOnlyList<Enhancement> switches = [];
    MeitouToggle meitou = null!;
    SettingsMenu? panel;

    /// <summary>--low-end (WorldOptions.LowEnd) and --benchmark-baseline are presets for this run: the saved sliders and switches would undo them, so they are left as saved.</summary>
    bool lowEnd;

    /// <summary>The keys this run leaves as saved (--bloom).</summary>
    HashSet<string> runOnly = [];

    // The cinematic camera and the squad tour; while either holds the drawn camera the rig's state is not applied.
    Cinema cinema = null!;
    bool cinemaHolds;
    SquadTour tour = null!;
    bool tourFollows;
    bool cinemaWasPlaying, cinemaWasRecording;

    // The keyboards and mice (none headless), and the pointer's state between their events.
    Silk.NET.Input.IInputContext? silkInput;
    IReadOnlyList<Silk.NET.Input.IKeyboard> keyboards = [];
    IReadOnlyList<Silk.NET.Input.IMouse> mice = [];
    string? headlessClipboard;
    Vector2? lastMouse;
    Vector2? appliedHeadlessMouse;

    /// <summary>
    /// Rotating pins the pointer. Windows and macOS put it back after every move (HoldPointer); Linux locks it (GLFW's disabled cursor), because putting it back
    /// over-counts the movement there:
    /// - Wayland: the pointer is never put back (glfwSetCursorPos is unavailable), so each move counts the whole distance from the start.
    /// - X11 (native and XWayland): moves are counted more than once and the pointer jitters (Observed 2026-10-25).
    /// While locked, the game treats the pointer as at <see cref="orbitAnchor"/>.
    /// </summary>
    bool lockOrbit;
    Vector2? orbitAnchor;
    Silk.NET.Input.CursorMode windowCursor = Silk.NET.Input.CursorMode.Normal;
    Silk.NET.Input.CursorMode cursorBeforeOrbit = Silk.NET.Input.CursorMode.Normal;
    bool panelDrag, shiftDown, profilerClick;
    bool highlightHeld;
    bool windowFocused = true;

    /// <summary>
    /// Silk raises KeyDown once per press (no OS key repeat), so a held erase or caret key in a text field repeats here: after 0.5 s, then 30 times a second (the
    /// Windows defaults). Typed characters already repeat through KeyChar.
    /// </summary>
    (Silk.NET.Input.IKeyboard Keyboard, SilkKey Key, long Next)? heldEditKey;

    // The loop's clock and end.
    Stopwatch frame = null!;
    double last;
    int totalFrames;
    bool quit;

    // What a frame draws into: the interface's layer, frame generation (made on the first frame), the headless backbuffer.
    UiLayer? uiLayer;
    IFrameGenerator? frameGen;
    readonly FrameGenerationSetup.SystemPointer systemPointer = new();
    bool frameGenTried;
    GpuTexture? headlessTarget;

    // The fps counter (the mean over a tenth of a second) and F11's statistics (once a second); the triangle counts summed over the statistics' second.
    readonly List<string> stats = [];
    string fpsText = "";
    double fpsWindow;
    int fpsWindowFrames;
    double drawTotal, gpuSum, statsTimer;
    int gpuSamples, statsFrames;
    long statsTicks;
    double terrainTris, characterTris, grassBlades;
    long objectTris0;

    /// <summary>What the mouse is over in a frame, decided once before the world is drawn: the pointer (drawn last) and the hover outline (drawn with the world) agree.</summary>
    readonly record struct FramePointer(Silk.NET.Input.IMouse? Mouse, Vector2 At, bool OverWorld, bool FreeLook);

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
        host.agentChannel.settingsPanel = panel;
        host.agentChannel.InitControl();
        SetUpCameraFlights();
        ConnectInput();
        Console.WriteLine(GameOptions.Usage[GameOptions.Usage.IndexOf("Keys:", StringComparison.Ordinal)..]);
        Console.WriteLine($"display   vsync {(vsync ? "on" : "off")}, frame limit {(vsync || fpsLimit <= 0 ? "none" : fpsLimit + " fps")}, control tick {host.session.Ticks.TickRate:0} Hz, world tick {SimulationClock.TickRate:0} per game second; settings in {host.config.Path}");

        frame = Stopwatch.StartNew();
        ConnectGameEvents();
        while (window?.IsClosing != true && !quit)
        {
            if (!Frame()) break;
        }

        ShutDown(spikeGc);
        return host.agentChannel.controlExitCode;
    }

    // ---- start-up ----

    /// <summary>The display, its window hidden while the device is made; the audio and the world's files start on workers meanwhile.</summary>
    VulkanDisplay OpenDisplay()
    {
        headless = host.o.Headless;   // --headless: no window, no swapchain, no input devices; the loop draws into an offscreen picture
        vsync = !headless && (host.g.VSync ?? host.config.VSync);
        fpsLimit = host.g.FpsLimit ?? (headless ? 30 : host.config.FpsLimit);
        initialFpsLimit = fpsLimit;
        VulkanDisplay.RayTracing = host.o.WantsRayTracing;
        // The game's methods compiled ahead, from now on (with code mods: once they are initialized), behind the loading.
        if (!host.g.NoPrecompile) host.modding.StartWarmup();
        // The world's files are read on a worker from here on: while the window and the device are made, then behind the loading screen.
        sceneRead = host.LoadScene is { } readScene ? Task.Run(readScene) : null;
        host.sound.BeginAudio();
        frameGenerationAtStart = host.config.FrameGenerationMethod;
        display = new VulkanDisplay(headless ? null : WindowOptions.Default with
        {
            Size = new Vector2D<int>(host.o.Width, host.o.Height),
            Title = "MeitouClient",
            // Hidden while the device is made (a second or two in which nothing can be drawn), shown maximised with the first loading frame.
            IsVisible = false,
            FramesPerSecond = 0,
            UpdatesPerSecond = 0,
        }, vsync, dlss: true, frameGeneration: headless ? FrameGenerationMethod.Off : frameGenerationAtStart);   // Load before the device so the settings panel can switch to DLSS without restarting.
        host.streamline = display.Streamline;
        host.ngx = display.Ngx;
        window = headless ? null : display.Window!;
        return display;
    }

    /// <summary>The world's files read on a worker since <see cref="OpenDisplay"/>, when the host owns the scene.</summary>
    Task<WorldScene?>? sceneRead;

    /// <summary>Sizes the window while hidden, starts the loading screen (or the Faithful splash) and shows the window with the first loading frame in it.</summary>
    void ShowWindow()
    {
        // X11 opens it where Silk.NET asks (50,50), the desktop's corner, which can be another monitor (Observed 2026-10-26, XWayland): on the primary
        // monitor instead, the one full screen uses; leaving full screen puts it back where it was when full screen began.
        if (window?.Native?.X11 is not null) BorderlessFullScreen.MoveToPrimaryMonitor(window);
        // Borderless full screen as the viewer starts (Alt+Enter and the Tab panel switch it); --windowed keeps a window for
        // this run: the size asked by --size, else maximised. Sized while still hidden: resizing after it shows (maximised,
        // then borderless) flashed white and black before the first frame.
        bool fullScreen = host.config.FullScreen && !host.o.Windowed && !headless;
        (savedVSync, savedFullScreen) = (vsync, fullScreen);
        if (window is null) { }
        else if (fullScreen) BorderlessFullScreen.Toggle(window);
        else if (!host.o.SizeGiven && !host.o.HiddenWindow) window.WindowState = WindowState.Maximized;   // maximising would show a hidden window
        host.BeginLoadingScreen(display);   // its textures and fonts are made before the window shows
        // The Faithful start shows the original's splash, as the game does before its title, instead of the loading screen (unless the Interface setting turns it off).
        if (host.loading is { Faithful: true } && host.config.SplashScreens && host.OpensOnTitle(interactive: true) && !headless) host.loading.StartSplash();
        // The first loading frame is in the swapchain when the window shows, and on screen before the splash closes. Not on Wayland: a window gets its
        // shell role when it shows, and a buffer attached to the surface before that makes the compositor drop the client ("attached a buffer before
        // configure event"), so there the window shows first and takes the compositor's configure before anything is drawn.
        // --hidden-window: drawn and presented as usual, never shown (an unattended run).
        if (window is null) { }
        else if (host.o.HiddenWindow) host.LoadingFrame();
        else if (window.Native?.Wayland is not null)
        {
            window.IsVisible = true;
            window.DoEvents();
        }
        else
        {
            host.LoadingFrame();
            window.IsVisible = true;
        }
        host.LoadingFrame();
        host.Splash?.Dispose();
    }

    /// <summary>The scene and the boot under the loading screen; the exit code when the run ends here (the window closed, the scene failed), else null.</summary>
    int? LoadWorld()
    {
        try
        {
            if (sceneRead is not null)
            {
                host.LoadingStep("scene");
                if (host.AwaitLoading(sceneRead) is not { } loaded) return 1;
                host.scene = host.ownedScene = loaded;
            }
            host.Boot(display, interactive: true);
            host.sound.AttachAudio();
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

    /// <summary>The overlay the interface draws through, the portraits' texture, and the debug overlays (docs/engine.md): F10 the debug menu, F11 the statistics, F12 the profiler.</summary>
    void CreateDebugTools()
    {
        overlay = DebugOverlay.TryCreate(display.Context, host.assets.Find);
        if (overlay is not null) { host.player.CreateUiPainter(overlay, host.assets.Find); host.itemIconFiles.ConnectItemIcons(); }
        if (host.gpu.Characters is { } portraits) { var (atlas, sampled) = portraits.PortraitAtlas; host.player.SetUiTexture(PlayerInterface.PortraitTexture, atlas, sampled); }
        host.debugMenu.debugToolsOn = true;
        host.debugMenu.profiler = overlay is null ? null : new FrameProfiler(display.Context, () => display.Context.GpuFrameMs);
    }

    /// <summary>
    /// The Faithful / Meitou switches (Shift+F1 turns Meitou off and back on; plain F2..F4 and F8 are the game's speed and screenshot keys) and the settings panel, which
    /// keeps the shared rendering settings' callbacks and persistence keys with the game's own modal layout.
    /// </summary>
    void CreateSettingsPanel()
    {
        switches = WorldFrame.LiveSwitches(host.o, () => host.gpu, () => host.camera, () => host.render);
        meitou = new MeitouToggle(switches);
        lowEnd = Environment.GetCommandLineArgs().Contains("--low-end") || host.g.BenchmarkBaseline;
        runOnly = GameHost.RunOnlyKeys();
        if (overlay is null) return;
        var values = WorldFrame.CreateSettingsPanel(overlay, host.gpu, host.render,
            getHour: () => (float)host.session.Clock.HourOfDay, setHour: v => { host.session.Clock.SetHourOfDay(v); if (host.duel.IsDuel) host.duel.duelHour = v; },
            getVSync: () => display.VSync, setVSync: v => display.VSync = v, switches: switches,
            getFullScreen: FullScreenOn, setFullScreen: _ => ToggleFullScreen(),
            extraChoices: [new PanelChoice(() => $"UI mode: {host.player.UiMode}", () => ["Faithful", "Meitou"], () => (int)host.player.UiMode,
                index =>
                {
                    host.player.SetUiMode(index switch { 0 => Meitou.Data.Ui.UiMode.Faithful, 1 => Meitou.Data.Ui.UiMode.Meitou, _ => throw new ArgumentOutOfRangeException(nameof(index)) });
                    return $"UI mode: {host.player.UiMode}";
                })]);
        var frameLimit = new Slider("Frame limit", 0, 360, () => fpsLimit, v => fpsLimit = (int)MathF.Round(v / 5) * 5, "0", Text: v => v <= 0 ? "Unlimited" : $"{v:0} fps");
        panel = new SettingsMenu(host.player, values, host.session.Bindings, SaveSettings, frameLimit, host.config)
        {
            UpscalerStatus = () => host.gpu.Post?.ActiveUpscaler ?? "Off",
            // The loaded frame generation's state; a change it cannot follow shows as pending until the restart.
            FrameGenerationStatus = () => FrameGenerationSetup.FrameGenerationState(display, frameGenerationAtStart, host.config.FrameGenerationMethod),
            ModSettings = () => host.modding.mods?.Settings ?? [],
            AudioChanged = host.sound.AudioSettingsChanged,
            GameplayChanged = host.saves.ApplyGameplay,
            SkinRaceList = host.scene.Database is { } raceDb ? CharacterRenderer.PersonRaces(raceDb) : [],
            SkinRacesChanged = () => { if (host.gpu.Characters is { } chars) chars.SkinRaces = host.config.SkinRaces; },
            CameraRotateSpeedChanged = ApplyCameraRotateSpeed,
            CameraChanged = () => host.config.ApplyCamera(host.session.Camera.Settings),
            TutorialsChanged = on => { if (host.saves.tutorials is { } t) t.Enabled = on; },
            ResetTutorials = () => host.saves.tutorials?.Reset(),
            // The settings that reach into the running world's systems.
            AttackSlotsChanged = slots => EachSystem<Meitou.Simulation.Combat.CombatSystem>(combat => combat.UseAttackSlots(slots)),
            LateDefencesChanged = on => EachSystem<Meitou.Simulation.Combat.CombatSystem>(combat => combat.LateDefences = on),
            GunnerFacingChanged = on => EachSystem<Meitou.Simulation.Combat.CombatSystem>(combat => combat.GunnerFacesBarrel = on),
            PunchReachChanged = on => EachSystem<Meitou.Simulation.Combat.CombatSystem>(combat => combat.PunchReachFix = on),
            BenchFacingChanged = on => host.session.World.BenchFacingFix = on,
            TimeSpeedsChanged = () => host.session.Simulation.SetFastSpeeds(host.config.FastSpeed, host.config.FastestSpeed),
            DoorsCloseChanged = on => EachSystem<Meitou.Simulation.Locks.LockSystem>(locks => locks.DoorsCloseBehind = on),
            AnimalsPickUpChanged = on => EachSystem<Meitou.Simulation.Player.PlayerSystem>(players => players.AnimalsPickUp = on),
            OffscreenChanged = ApplyOffscreenSettings,
        };
    }

    void EachSystem<T>(Action<T> act)
    {
        foreach (var system in host.session.World.Systems.OfType<T>()) act(system);
    }

    /// <summary>The offscreen world's settings into the population and production systems.</summary>
    void ApplyOffscreenSettings()
    {
        foreach (var population in host.session.World.Systems.OfType<Meitou.Simulation.Population.PopulationSystem>())
        {
            (population.KeepRoamers, population.OffscreenFights, population.OffscreenHealing) = (host.config.KeepRoamers, host.config.OffscreenFights, host.config.OffscreenHealing);
            (population.CampaignAncestorsOnce, population.KeepWild, population.ResidentSchedule) = (host.config.CampaignAncestorsOnce, host.config.KeepWild, host.config.ResidentSchedule);
        }

        foreach (var production in host.session.World.Systems.OfType<Meitou.Simulation.Production.ProductionSystem>()) production.UnloadedProduction = host.config.UnloadedProduction;
    }

    void ApplyCameraRotateSpeed(float speed) => host.session.Camera.Settings.RotateSpeedX = host.session.Camera.Settings.RotateSpeedY =
        Math.Clamp(speed, UserConfig.MinCameraRotateSpeed, UserConfig.MaxCameraRotateSpeed);

    /// <summary>The camera, mouse, font, markers and tutorials as the user set them (the install's settings.cfg where meitou.user.json has none).</summary>
    void ApplyUserConfig()
    {
        ApplyCameraRotateSpeed(host.config.CameraRotateSpeed);
        host.config.EdgeScroll ??= Meitou.Game.Audio.AudioSystem.Setting(host.install, "Edge scrolling", 1) != 0;
        host.config.Tutorials ??= Meitou.Game.Audio.AudioSystem.Setting(host.install, "tutorials", 1) != 0;
        host.config.SeedCamera(host.install);
        host.config.ApplyCamera(host.session.Camera.Settings);
        KeyMap.SwapButtons = host.config.SwapMouseButtons == true;
        host.player.FontSize = host.config.FontSize ?? Meitou.Data.Ui.KenshiFontSize.DefaultSetting;
        host.config.MovementMarker ??= Meitou.Game.Audio.AudioSystem.Setting(host.install, "Movement marker", 1) != 0;
        host.config.RotationMarker ??= Meitou.Game.Audio.AudioSystem.Setting(host.install, "Rotation marker", 1) != 0;
        (host.player.MovementMarker, host.player.RotationMarker) = (host.config.MovementMarker == true, host.config.RotationMarker == true);
        host.saves.tutorials ??= new TutorialManager(Path.Combine(Directory.GetCurrentDirectory(), TutorialManager.FileName));
        host.saves.tutorials.Enabled = host.config.Tutorials == true;
        host.player.tutorialWindow.ConfigureTutorials(host.saves.tutorials);
    }

    /// <summary>The saved sliders, the look the command line chose, then the saved switches and preset (unless the command line chose them).</summary>
    void ApplySavedGraphics()
    {
        if (panel is not null && !lowEnd)
            foreach (var slider in panel.Sliders)
                if (!runOnly.Contains(slider.Label) && host.config.Graphics.TryGetValue(slider.Label, out float v) && !(host.o.Post.Upscale.Explicit && WorldFrame.UpscalerSliders.Contains(slider.Label))
                    && !(slider.Label == WorldFrame.TextureQualitySlider && Environment.GetCommandLineArgs().Contains("--texture-quality")))   // the saved one was applied before the boot (Program)
                    slider.Set(Math.Clamp(v, slider.Min, slider.Max));
        // The look as the command line chose it: Faithful or Meitou for "all", else read from the switches (not saved).
        if (CommandLineLook() is var (given, faithfulLook) && given) host.config.FaithfulLook = faithfulLook;
        // The switches as saved, then the saved preset (one the launcher chose), unless the command line chose them (--meitou / --faithful):
        // switches it moved would read as another preset.
        if (panel is not null && !lowEnd && !Environment.GetCommandLineArgs().Any(a => a is "--meitou" or "--faithful"))
        {
            foreach (var toggle in panel.Toggles)
                if (!SettingsMenu.HiddenSwitch(toggle.Label) && !runOnly.Contains(SwitchKey(toggle.Label)) && host.config.Graphics.TryGetValue(SwitchKey(toggle.Label), out float v) && (v >= 0.5f) != toggle.Get()) toggle.Set(v >= 0.5f);
            panel.ApplySavedPreset();
        }
        if (host.g.BenchmarkBaseline) panel?.ApplyBenchmarkBaseline();
    }

    /// <summary>
    /// The cinematic camera (docs/viewer.md "Cinematic camera", F6 its editor): while it flies, previews or cuts to a key it holds the drawn camera and the rig's state is
    /// not applied; the first camera input after that hands the camera back to the rig (CameraRig.TakeOver). The hour is the game clock's (time does not follow the keys);
    /// Home puts the lens back while the editor is open, in the free camera and on a camera the cinema holds. Numpad / flies the camera across the squad in portrait order
    /// (docs/viewer.md "Squad tour"), and a double click on a far squad member flies there; both hold the camera as the cinema does.
    /// </summary>
    void SetUpCameraFlights()
    {
        cinema = new Cinema(() => host.camera, () => (float)host.session.Clock.HourOfDay, null, FullScreenOn, ToggleFullScreen,
            Cinema.GameFile, lensKeysAlways: host.duel.IsDuel);   // the duel has no floors: PageUp/PageDown zoom the lens there
        cinema.Moved = () => cinemaHolds = true;
        // PageUp/PageDown zoom the lens and Home puts it back in these cameras; in the strategy camera PageUp/PageDown are the game's floor keys.
        cinema.LensKeys = () => host.session.Camera.IsFree || cinemaHolds;
        cinema.Load();
        tour = new SquadTour();
        // A double click on a squad member more than FlightDistance from the view flies there (Advanced settings "Character flights").
        host.player.FlyTo = id =>
        {
            if (!host.config.CharacterFlights || host.duel.IsDuel || host.session.Camera.IsFree || cinema.Playing || cinema.Recording || TourPosition(id) is not { } at) return false;
            if (new Vector2(at.X - host.camera.Target.X, at.Z - host.camera.Target.Z).Length() <= UserConfig.FlightDistance) return false;
            host.player.Follow = null;
            tour.HopSeconds = host.config.CharacterFlightSeconds;
            tour.Fly(host.camera, id, TourPosition);
            (cinemaHolds, tourFollows) = (true, true);
            return true;
        };
        // Pressing the member again while the flight is under way (a triple tap) cuts straight to the end of it.
        host.player.SkipFlight = id =>
        {
            if (!tour.Active || !tourFollows || tour.Current != id) return;
            tour.Finish(host.camera, TourPosition);
            EndTour();
        };
    }

    Vector3? TourPosition(CharacterId id) => host.session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is { } c ? host.DrawnPosition(c) : null;

    void EndTour()
    {
        tour.Stop();
        cinemaHolds = false;
        host.session.Camera.TakeOver(new CameraState(host.camera.Eye, host.camera.Forward, Vector3.UnitY, host.camera.Target, host.camera.Yaw, host.camera.Pitch, host.camera.Distance, true), strategy: true);
        if (tourFollows) host.player.Follow = tour.Current;   // a double-clicked member is followed, as a cut to it would be
    }

    /// <summary>The window's keyboards and mice, and the clipboard the faithful windows' edit boxes copy and paste through (headless: a clipboard of the game's own).</summary>
    void ConnectInput()
    {
        silkInput = window is null ? null : Silk.NET.Input.InputWindowExtensions.CreateInput(window);
        keyboards = silkInput is null ? [] : silkInput.Keyboards;
        mice = silkInput is null ? [] : silkInput.Mice;
        host.player.ClipboardRead = () => keyboards.Count > 0 ? ClipboardText.Read(keyboards[0]) : headless ? headlessClipboard : null;
        host.player.ClipboardWrite = text => { if (keyboards.Count > 0) keyboards[0].ClipboardText = text; else if (headless) headlessClipboard = text; };
        lockOrbit = OperatingSystem.IsLinux();
        foreach (var kb in keyboards) ConnectKeyboard(kb);
        foreach (var mouse in mice) ConnectMouse(mouse);
    }

    /// <summary>The interface's actions (saves, loads, a new game, the menus), the window's focus and the control tick's input actions.</summary>
    void ConnectGameEvents()
    {
        host.player.ScreenAction += OnScreenAction;
        if (window is not null) window.FocusChanged += focused =>
        {
            windowFocused = focused;
            if (!focused) { highlightHeld = false; host.session.Input.ReleaseAll(); }
        };
        host.session.Ticked += OnTicked;
    }

    void OnScreenAction(MyGuiEvent e)
    {
        if (e.Action == "save") host.saves.saveRequestedFolder = Meitou.Data.Save.SaveCatalog.Folder(Meitou.Simulation.Saving.QuickSave.DefaultRoot, e.Text);
        else if (e.Action == "load") host.saves.loadRequestedFolder = e.Text;
        else if (e.Action == "import") host.saves.importRequested = (e.Text, host.player.ImportFlags);
        else if (e.Action == "new-game") host.newGame.newGameRequested = e.Text;
        else if (e.Action.StartsWith("feedback-", StringComparison.Ordinal)) host.feedbackCapture.FeedbackAction(e);
        else if (e.Action == "main-menu") host.newGame.mainMenuRequested = true;
        else if (e.Widget == "OptionsButton" && panel is not null) panel.Visible = true;
        else if (e.Widget == "ExitGameButton") quit = true;
    }

    void OnTicked(ActionState actions)
    {
        highlightHeld = actions.Held(InputAction.Highlight);
        // The duel is for filming: Esc must not close it. The terrain sandbox has no game menu: Esc opens the settings (close the window to leave).
        if (actions.Pressed(InputAction.Quit) && host.g.TerrainSandbox is not null) { if (panel is not null) panel.Visible = true; }
        else if (actions.Pressed(InputAction.Quit) && !host.duel.IsDuel) quit = true;
        // Panning (or leaving for the free camera) lets go of the character the camera follows; turning and zooming keep it.
        if (actions.Held(InputAction.MoveForward) || actions.Held(InputAction.MoveBack) || actions.Held(InputAction.MoveLeft) || actions.Held(InputAction.MoveRight) ||
            actions.Pressed(InputAction.ToggleFreeCamera)) host.player.Follow = null;
        if (actions.Pressed(InputAction.ToggleSettings) && panel is not null) panel.Visible = !panel.Visible;
        if (actions.Pressed(InputAction.Screenshot)) host.screenshotRequested = true;
        if (actions.Pressed(InputAction.ToggleStats)) { host.debugMenu.statsVisible = !host.debugMenu.statsVisible; FoliageGpuCull.CountTriangles = host.debugMenu.statsVisible; }   // the foliage triangles are read from its GPU cull while shown
        if (actions.Pressed(InputAction.ToggleFps)) host.debugMenu.fpsVisible = !host.debugMenu.fpsVisible;
        if (actions.Pressed(InputAction.QuickSave)) host.saves.quickSaveRequested = true;
        if (actions.Pressed(InputAction.QuickLoad)) host.saves.quickLoadRequested = true;
        // PageUp / PageDown step the viewed floor in the strategy camera: in the free camera and on a held one the cinema takes them for the lens.
        if (actions.Pressed(InputAction.FloorUp)) host.interiors.StepFloor(1);
        if (actions.Pressed(InputAction.FloorDown)) host.interiors.StepFloor(-1);
        if (actions.Pressed(InputAction.CycleProfiler) && host.debugMenu.profiler is not null) host.debugMenu.profiler.Showing = (FrameProfiler.Mode)(((int)host.debugMenu.profiler.Showing + 1) % 3);
        // The camera the cinema left (a cut, a scrub, the end of a flight) goes back to the rig at the first camera input: the free camera at
        // that eye (only it can stand anywhere), or with ; the strategy camera where the view meets the ground. A flight in progress keeps it.
        if (tour.Active && GameHost.CameraInput(actions)) EndTour();
        else if (cinemaHolds && !cinema.Playing && GameHost.CameraInput(actions))
        {
            cinemaHolds = false;
            host.session.Camera.TakeOver(new CameraState(host.camera.Eye, host.camera.Forward, Vector3.UnitY, host.camera.Target, host.camera.Yaw, host.camera.Pitch, host.camera.Distance, true),
                strategy: actions.Pressed(InputAction.ToggleFreeCamera));
        }
    }

    // ---- the window and the pointer ----

    Vector2D<int> FramebufferSizeNow() => window is null ? new Vector2D<int>(host.o.Width, host.o.Height) : window.FramebufferSize;

    bool FullScreenOn() => window is not null && BorderlessFullScreen.IsOn(window);

    void ToggleFullScreen()
    {
        if (window is not null) BorderlessFullScreen.Toggle(window);
    }

    Vector2 Pixels(Vector2 p) => window is null ? p
        : p * new Vector2(window.FramebufferSize.X / (float)Math.Max(window.Size.X, 1), window.FramebufferSize.Y / (float)Math.Max(window.Size.Y, 1));

    /// <summary>The settings panel takes the mouse only while it is drawn (F7 and the cinema's clean picture hide it).</summary>
    bool PanelLive() => !host.agentChannel.interfaceHidden && !cinema.Clean;

    bool Held(SilkKey a, SilkKey b) => keyboards.Any(k => k.IsKeyPressed(a) || k.IsKeyPressed(b));

    static bool CtrlDown(Silk.NET.Input.IKeyboard keyboard) => keyboard.IsKeyPressed(SilkKey.ControlLeft) || keyboard.IsKeyPressed(SilkKey.ControlRight);

    /// <summary>The key a modal window takes: the keypad's Enter as Enter.</summary>
    static EngineKey? ModalKeyOf(SilkKey k) => k == SilkKey.KeypadEnter ? EngineKey.Enter : KeyMap.Map(k);

    // ---- keyboard ----

    bool TextFieldFocused() => panel?.Visible == true || host.debugMenu.debugMenuOpen || host.devConsoleHost.ConsoleOpen || host.player.UiEditing;

    void RepeatEditKey()
    {
        if (heldEditKey is not { } held || Stopwatch.GetTimestamp() < held.Next) return;
        if (!held.Keyboard.IsKeyPressed(held.Key) || !TextFieldFocused()) { heldEditKey = null; return; }
        heldEditKey = held with { Next = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 30 };
        OnKeyDown(held.Keyboard, held.Key);
    }

    void ConnectKeyboard(Silk.NET.Input.IKeyboard kb)
    {
        kb.KeyDown += (keyboard, k, _) =>
        {
            heldEditKey = k is SilkKey.Backspace or SilkKey.Delete or SilkKey.Left or SilkKey.Right or SilkKey.Up or SilkKey.Down && TextFieldFocused()
                ? (keyboard, k, Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2) : null;
            OnKeyDown(keyboard, k);
        };
        kb.KeyUp += (_, k, _) =>
        {
            if (heldEditKey?.Key == k) heldEditKey = null;
            if (k is SilkKey.ShiftLeft or SilkKey.ShiftRight) { shiftDown = false; host.player.SlowModifier = false; }
            if (KeyMap.Map(k) is { } key) { host.session.Input.SetKey(key, false); host.player.BuildKeyUp(key); }
        };
        kb.KeyChar += (_, value) => OnKeyChar(value);
    }

    void OnKeyChar(char value)
    {
        if (host.Covered) return;
        if (host.devConsoleHost.ConsoleOpen) host.devConsoleHost.devConsole.Type(value);
        else if (panel?.Visible == true) panel.TextInput(value);
        else if (host.sandbox.sandboxPicker is { Open: true }) host.sandbox.sandboxPicker.TextInput(value);
        else if (host.debugMenu.debugMenuOpen) host.debugMenu.DebugMenuText(value);
        else host.player.UiText(value);
    }

    /// <summary>A key goes to the first that takes it: the loading screen, a modal window, the interface's own toggles, the game's interface, then the game.</summary>
    void OnKeyDown(Silk.NET.Input.IKeyboard keyboard, SilkKey k)
    {
        if (k is SilkKey.ShiftLeft or SilkKey.ShiftRight) { shiftDown = true; host.player.SlowModifier = true; }
        if (k == SilkKey.Enter && (keyboard.IsKeyPressed(SilkKey.AltLeft) || keyboard.IsKeyPressed(SilkKey.AltRight))) { ToggleFullScreen(); return; }
        if (host.Covered)
        {
            if (k is SilkKey.Escape or SilkKey.Space) host.skyArrival?.Skip();
            host.loading?.SkipSplash();
            return;
        }
        if (ModalKey(keyboard, k) || InterfaceKey(keyboard, k)) return;
        if (KeyMap.Map(k) is { } uiKey && host.player.UiKey(uiKey, CtrlDown(keyboard), shiftDown)) return;
        GameKey(keyboard, k);
    }

    /// <summary>The dev console, the settings panel, the sandbox's animation list, the debug menu, the inspector and the interior editor each take every key while open.</summary>
    bool ModalKey(Silk.NET.Input.IKeyboard keyboard, SilkKey k)
    {
        // Insert or Ctrl+` opens the dev console, which then takes every key (the settings panel's, the animation list's and the debug menu's too) until it closes.
        if (DevConsoleHost.IsConsoleToggle(k, CtrlDown(keyboard))) { host.devConsoleHost.ToggleConsole(); return true; }
        if (host.devConsoleHost.ConsoleOpen) { host.devConsoleHost.ConsoleKey(keyboard, k); return true; }
        host.player.UiVisible = PanelLive() && !host.g.NoPopulation;
        if (panel?.Visible == true)
        {
            if (ModalKeyOf(k) is { } settingsKey)
            {
                if (settingsKey is EngineKey.F8 or EngineKey.PrintScreen) host.screenshotRequested = true;
                else panel.KeyDown(settingsKey, shiftDown, CtrlDown(keyboard));
            }
            return true;
        }
        if (host.sandbox.sandboxPicker is { Open: true })
        {
            if (ModalKeyOf(k) is { } pickerKey) host.sandbox.sandboxPicker.KeyDown(pickerKey);
            return true;
        }
        // F10 (rebindable) opens the debug menu, which then takes every key until it closes.
        if (KeyMap.Map(k) is { } mapped && host.session.Bindings.Get(InputAction.ToggleDebugMenu).Contains(Binding.Of(mapped))) { host.debugMenu.debugMenuOpen = !host.debugMenu.debugMenuOpen; host.debugMenu.debugParts = null; return true; }
        if (host.debugMenu.debugMenuOpen)
        {
            if (ModalKeyOf(k) is { } menuKey) host.debugMenu.DebugMenuKey(menuKey);
            return true;
        }
        // The pick & collision inspector's menu (opened from F10's Tools row, DebugMenu): while open it takes every key, like F10's (before the game interface, whose Esc would open the game menu instead).
        if (host.debugMenu.inspectorMenuOpen)
        {
            if (ModalKeyOf(k) is { } inspectorKey) host.debugMenu.InspectorMenuKey(inspectorKey);
            return true;
        }
        if (host.interiorEditorHost.interiorEditorOpen)
        {
            if (ModalKeyOf(k) is { } editorKey) host.interiorEditorHost.InteriorEditorKey(editorKey);
            return true;
        }
        return false;
    }

    /// <summary>F7 hides the interface, Ctrl+C / Ctrl+V the free camera's code, the settings key opens the panel, Esc with the interface hidden brings it back.</summary>
    bool InterfaceKey(Silk.NET.Input.IKeyboard keyboard, SilkKey k)
    {
        // toggle_bar (F7, rebindable) hides the game's interface; F6 is the cinema's, and so is the numpad except the keys bound to the game's hotkeys
        // (Space, Delete and the lens keys too while its editor is open).
        if (KeyMap.Map(k) is { } hideKey && !host.player.UiEditing && host.session.Bindings.Get(InputAction.ToggleInterface).Contains(Binding.Of(hideKey)))
        {
            host.agentChannel.interfaceHidden = !host.agentChannel.interfaceHidden;
            Console.WriteLine(host.agentChannel.interfaceHidden ? "interface hidden (F7 shows it)" : "interface shown");
            return true;
        }
        // Only in the free camera: elsewhere Ctrl+C and Ctrl+V keep their own meaning, and so do they in a focused edit box.
        if (k is SilkKey.C or SilkKey.V && host.session.Camera.IsFree && !host.player.UiEditing && CtrlDown(keyboard))
        {
            CameraCodeKey(keyboard, k);
            return true;
        }
        if (panel is not null && KeyMap.Map(k) is { } settingsToggle && host.session.Bindings.Get(InputAction.ToggleSettings).Contains(Binding.Of(settingsToggle)))
        {
            panel.Visible = true;
            return true;
        }
        // Esc is the game menu's, never Quit's while there is a game: with the interface hidden (F7) it comes back with the menu open,
        // under the cinema's clean picture it does nothing.
        if (k == SilkKey.Escape && !host.g.NoPopulation && !host.player.UiVisible)
        {
            if (host.agentChannel.interfaceHidden && !cinema.Clean) { host.agentChannel.interfaceHidden = false; host.player.UiVisible = true; host.player.UiKey(EngineKey.Escape); }
            return true;
        }
        return false;
    }

    /// <summary>Ctrl+C copies the free camera's position as a viewer camera code, Ctrl+V flies the free camera to the code (or a feedback report's camera location) in the clipboard.</summary>
    void CameraCodeKey(Silk.NET.Input.IKeyboard keyboard, SilkKey k)
    {
        if (k == SilkKey.C)
        {
            string code = WorldCameraCode.Encode(host.camera);
            keyboard.ClipboardText = code;
            Console.WriteLine($"camera    copied {code} ({WorldCameraCode.Describe(host.camera)})");
        }
        else if (WorldCameraCode.Decode(ClipboardText.Read(keyboard)) is { } pose)
        {
            // The code's orbit (target, yaw, pitch, distance) as the free camera's eye and look direction.
            Vector3 back = new(MathF.Cos(pose.Pitch) * MathF.Sin(pose.Yaw), MathF.Sin(pose.Pitch), MathF.Cos(pose.Pitch) * MathF.Cos(pose.Yaw));
            host.session.Camera.TakeOver(new CameraState(pose.Target + back * pose.Distance, -back, Vector3.UnitY, pose.Target, pose.Yaw, pose.Pitch, pose.Distance, true), strategy: false);
            host.player.Follow = null;
            Console.WriteLine("camera    moved to the clipboard's camera code");
        }
        else Console.WriteLine("camera    the clipboard holds no camera code (Ctrl+C in a viewer copies one) or feedback camera location");
    }

    /// <summary>The keys the interface left: the squad tour, the cinema, the fps counter, the debug combinations, then the game's input bindings.</summary>
    void GameKey(Silk.NET.Input.IKeyboard keyboard, SilkKey k)
    {
        bool ctrl = CtrlDown(keyboard);
        if (k == SilkKey.KeypadDivide && !host.duel.IsDuel)
        {
            ToggleSquadTour();
            return;
        }
        // A numpad key bound to a game hotkey (the orders, NUM0..NUM8 by default) is the game's, except with Ctrl (saving a shot) or the cinema's editor open (F6).
        bool hotkey = !host.duel.IsDuel && !ctrl && !cinema.EditorVisible && KeyMap.Map(k) is { } gameKey && (host.player.IsHotkey(gameKey) || host.player.OwnsBuildNumpad(gameKey));
        if (!hotkey && Enum.TryParse(k.ToString(), out CinemaKey cinemaKey) && cinema.OnKey(cinemaKey, ctrl, shiftDown)) return;
        // F9 is the game's quickload; the fps counter is on Ctrl+F9 (a modifier, so the key never reaches the input bindings).
        if (k == SilkKey.F9 && ctrl) { host.debugMenu.fpsVisible = !host.debugMenu.fpsVisible; return; }
        if (KeyMap.Map(k) is not { } key || DebugKey(key, ctrl)) return;
        if (host.player.BuildKey(key, shiftDown)) return;
        host.session.Input.SetKey(key, true);
        host.player.Key(key, shiftDown);
        host.sandbox.SandboxKey(key);
        if (host.duel.IsDuel && key == EngineKey.Backspace) host.duel.duelRestartRequested = true;
        if (host.battle.IsBattle && key == EngineKey.Backspace) host.battle.battleRestartRequested = true;
    }

    /// <summary>Numpad /: the squad tour starts, or the one under way ends.</summary>
    void ToggleSquadTour()
    {
        if (tour.Active) EndTour();
        else if (!cinema.Playing && !cinema.Recording)
        {
            host.player.Follow = null;
            tour.HopSeconds = host.config.CharacterFlightSeconds;
            tourFollows = false;
            tour.Start(host.camera, [.. host.player.SquadBarMembers().Select(c => c.Id)], TourPosition);
            if (tour.Active) cinemaHolds = true;
            else Console.WriteLine("squad tour: nobody else in the squad to fly to");
        }
    }

    /// <summary>The Shift combinations: Meitou on and off (F1), the skin switch alone (F2), the navmesh view (F11), the VRAM pie and dump (F12).</summary>
    bool DebugKey(EngineKey key, bool ctrl)
    {
        if (!shiftDown) return false;
        if (key == EngineKey.F1) { Console.WriteLine(meitou.Toggle()); host.config.FaithfulLook = !meitou.On; return true; }
        if (key == EngineKey.F2 && switches.FirstOrDefault(e => e.Id == "skin") is { } skin)   // the skin switch alone, to compare faces
        { skin.IsMeitou = !skin.IsMeitou; Console.WriteLine($"skin      {skin.State}"); return true; }
        if (key == EngineKey.F11) { host.debugOverlays.ToggleNavOverlay(); return true; }
        if (ctrl && key == EngineKey.F12) { DumpVram(host.debugMenu.profiler); return true; }
        if (key == EngineKey.F12) { if (host.debugMenu.profiler is not null) host.debugMenu.profiler.ShowVram = !host.debugMenu.profiler.ShowVram; return true; }   // the VRAM pie (F12 alone cycles the profiler)
        return false;
    }

    // ---- mouse ----

    void ConnectMouse(Silk.NET.Input.IMouse mouse)
    {
        mouse.MouseDown += (m, b) =>
        {
            if (host.Covered) { host.loading?.SkipSplash(); return; }
            PointerDown(KeyMap.Map(b), Pixels(orbitAnchor ?? m.Position));
        };
        mouse.MouseUp += (m, b) => PointerUp(KeyMap.Map(b), Pixels(orbitAnchor ?? m.Position));
        mouse.MouseMove += OnMouseMove;
        mouse.Scroll += (m, wheel) =>
        {
            if (host.Covered || PanelLive() && panel?.Scroll(wheel.Y) == true) return;
            if (PanelLive() && !host.config.SandboxPanelCollapsed && host.sandbox.sandboxPicker?.Scroll(wheel.Y) == true) return;
            var at = Pixels(orbitAnchor ?? m.Position);
            if (!cinema.Contains(at) && !host.player.UiScroll(at, wheel.Y) && !(PanelLive() && host.player.ContainsUi(at))) host.session.Input.AddWheel(wheel.Y);
        };
    }

    /// <summary>The press of a pointer button, shared by the window's events and the control channel's <c>click</c> (which injects them at a pixel).</summary>
    void PointerDown(EngineButton? physical, Vector2 at)
    {
        host.player.UiVisible = PanelLive() && !host.g.NoPopulation;
        bool select = physical is { } pressed && host.PlayerButton(pressed) == EngineButton.Left;   // the Select button, whichever it is bound to
        if (PanelLive() && physical is { } settingsButton && panel?.MouseDown(at, settingsButton) == true) { panelDrag = true; return; }
        if (PanelLive() && panel?.Contains(at) == true) return;
        if (select && PanelLive() && !host.config.SandboxPanelCollapsed && host.sandbox.sandboxPicker?.MouseDown(at) == true) return;
        if (select && PanelLive() && host.sandbox.SandboxToggleClick(at)) return;
        if (select && host.debugMenu.profiler?.Click(at) == true) { profilerClick = true; return; }   // F12's legend opens and closes rows
        if (select && host.debugOverlays.StatsToggleClick(at)) { profilerClick = true; return; }   // F11's character boxes and paths
        // The cinema's editor: its clicks never select or move characters.
        if (select && cinema.MouseDown(at)) return;
        if (cinema.Contains(at)) return;
        if (physical is { } button)
        {
            // Ctrl and Alt with the right button: pick up / put down and loot (docs/game/ui-input.md, Meitou's carry bindings).
            if (host.player.MouseDown(host.PlayerButton(button), at, shiftDown, Held(SilkKey.ControlLeft, SilkKey.ControlRight), Held(SilkKey.AltLeft, SilkKey.AltRight))) return;
            host.session.Input.SetMouseButton(button, true);
        }
    }

    /// <summary>The release of a pointer button (see <see cref="PointerDown"/>).</summary>
    void PointerUp(EngineButton? physical, Vector2 at)
    {
        bool cinemaDrag = cinema.MouseUp();
        if (!host.Covered && !cinemaDrag && !panelDrag && !profilerClick && panel?.Visible != true && physical is { } released) host.player.MouseUp(host.PlayerButton(released), at, shiftDown);
        profilerClick = false;
        panel?.MouseUp();
        panelDrag = false;
        if (physical is { } button) host.session.Input.SetMouseButton(button, false);
    }

    void OnMouseMove(Silk.NET.Input.IMouse m, Vector2 p)
    {
        if (orbitAnchor is not null)
        {
            // Locked: p is GLFW's virtual position, only its movement counts, and not under a loading screen or the interface (as below).
            if (lastMouse is { } prev && !host.Covered && !host.player.UiCapturesPointer && !host.player.ModalOpen) host.session.Input.AddMouseDelta(p.X - prev.X, p.Y - prev.Y);
            lastMouse = p;
            return;
        }
        host.session.Input.SetMousePosition(p.X, p.Y);
        if (host.Covered) { lastMouse = p; return; }
        if (PanelLive() && panel?.MouseMove(Pixels(p)) == true || panelDrag) { lastMouse = p; return; }
        if (cinema.MouseMove(Pixels(p))) { lastMouse = p; return; }
        bool hold = lastMouse is not null && host.OrbitHeld();
        host.player.MouseMove(Pixels(hold ? lastMouse!.Value : p));
        if (host.player.UiCapturesPointer || host.player.ModalOpen) { lastMouse = p; return; }
        if (lastMouse is { } last) host.session.Input.AddMouseDelta(p.X - last.X, p.Y - last.Y);
        if (hold && lockOrbit)
        {
            // Locked here, not with the cursor mode at the end of the frame: until then the pointer moves on, and GLFW counts that movement
            // twice (X11) or drops it (Wayland) when the lock starts. Movement is then measured from where GLFW's virtual position starts.
            orbitAnchor = lastMouse;
            host.session.Input.SetMousePosition(lastMouse!.Value.X, lastMouse.Value.Y);
            cursorBeforeOrbit = windowCursor;
            m.Cursor.CursorMode = windowCursor = Silk.NET.Input.CursorMode.Disabled;
            lastMouse = m.Position;
            return;
        }
        if (hold) { host.HoldPointer(m, lastMouse!.Value); return; }
        lastMouse = p;
    }

    // ---- a frame ----

    /// <summary>One frame of the loop: input, the world's ticks, the camera, the audio, then the picture drawn and presented. False ends the loop (the window closed, the control channel quit).</summary>
    bool Frame()
    {
        host.playBench.BenchLap(PlayBench.BenchTail);
        display.BeginLatencyFrame();   // DLSS frame generation's Reflex: before the input is read
        window?.DoEvents();
        if (window?.IsClosing == true) return false;
        if (host.devConsoleHost.controlQuit) { quit = true; return false; }
        RepeatEditKey();
        double now = frame.Elapsed.TotalSeconds, dt = now - last;
        last = now;
        double frameDt = BeginFrameTime(dt);
        AdvanceWorld(frameDt);
        MoveCamera(dt);
        host.agentChannel.PollControl();
        string? takeFile = cinema.TakeFrame();
        PlaceSceneCameras();
        host.playBench.BenchLap(3);
        host.sound.UpdateAudio(dt);
        host.playBench.BenchLap(4);
        AdvanceTitleAndArrival(frameDt);
        KeepEyeAboveGround();
        var size = FramebufferSizeNow();
        // Until the warm-up's frames run smoothly the water reflection draws everything and the sky its cloud volume, so their pipelines are made under the
        // cover (ReflectionPass.Warm, SkyRenderer.Warm).
        bool warmPasses = host.warming && host.smoothSeconds == 0;
        if (host.gpu.Reflection is { } reflection) reflection.Warm = warmPasses;
        host.gpu.Sky.Warm = warmPasses;
        host.UpdateView(size.X, size.Y);
        var pointer = PreparePointer(takeFile);
        host.playBench.BenchLap(5);
        if (display.BeginFrame(size.X, size.Y)) DrawFrame(size, pointer, takeFile);
        else if (host.screenshotRequested && host.agentChannel.shotPath is not null && ++host.shotNoFrame >= 30) host.agentChannel.ControlShotFailed("the frame cannot be drawn (window minimised or device lost)");
        UpdateStats(dt);
        totalFrames++;
        if (host.g.QuitAfter is { } quitAfter && now >= quitAfter) { PrintRunSummary(now); quit = true; }
        // The limit is on presented frames: with frame generation on every rendered frame brings a generated one, so the render rate is half of it.
        if (!display.VSync && fpsLimit > 0) GameHost.Limit(frame, now, (frameGen is { Enabled: true } ? 2.0 : 1.0) / fpsLimit);
        return true;
    }

    /// <summary>The frame's time for the counters and the scripted runs; returns the time the simulation advances by (a recording cinema's frame, else the real time).</summary>
    double BeginFrameTime(double dt)
    {
        // The last frame was drawn under the loading screen: a scene load's next stage may run, the warm-up counts it.
        if (host.Covered) host.CoveredFrame(dt);
        host.AdvanceSkyArrival(dt);
        if (host.g.QuitAfter is not null && host.titleScreen.TitleVisible && !host.Covered)
        {
            host.menuFrames++;
            host.menuFrameSeconds += dt;
            host.menuLongestFrame = Math.Max(host.menuLongestFrame, dt);
            if (dt > 0.1) host.menuSlowFrames++;
        }
        // The counter: the mean over the last tenth of a second, so a single late frame doesn't read as a drop; then a frame's work
        // (its time without the waits) and its whole time in brackets.
        fpsWindow += dt;
        fpsWindowFrames++;
        if (fpsWindow >= 0.1)
        {
            fpsText = FrameGenerationSetup.FpsCounterText(fpsWindowFrames, fpsWindow, frameGen);
            fpsWindow = 0;
            fpsWindowFrames = 0;
        }
        // While the cinema records, every frame is exactly one frame of its rate, for the simulation too (the take plays at game speed
        // however slowly the frames are drawn and saved); otherwise the real time.
        double frameDt = cinema.Recording ? cinema.FrameTime(dt) : dt;
        // The duel starts a lap when a flight starts (played or recorded), so every take begins with the fight's first frame, and when its lap is over.
        host.duel.DuelFrame(cinema.Playing && !cinemaWasPlaying || cinema.Recording && !cinemaWasRecording);
        host.battle.BattleFrame();
        (cinemaWasPlaying, cinemaWasRecording) = (cinema.Playing, cinema.Recording);
        if (!host.playBench.PlayBenchFrame(dt, host.debugMenu.profiler)) quit = true;
        host.playBench.BenchLap(0);
        return frameDt;
    }

    /// <summary>The world's ticks for the frame, then what follows them between ticks: news, saves, loads, the navmesh and the buildings.</summary>
    void AdvanceWorld(double frameDt)
    {
        long a0 = Stopwatch.GetTimestamp();
        if (panel?.Visible == true)
        {
            foreach (var key in Enum.GetValues<EngineKey>()) host.session.Input.SetKey(key, false);
            foreach (var button in Enum.GetValues<EngineButton>()) host.session.Input.SetMouseButton(button, false);
            host.session.Input.Consume();
        }
        // Edge scrolling (the game's default): the pointer at the edge of the window pans the camera, unless something else has the pointer.
        host.session.Camera.Settings.EdgeScroll = host.config.EdgeScroll != false;
        bool edgeOff = host.Covered || host.titleScreen.TitleVisible || panel?.Visible == true || host.agentChannel.interfaceHidden || cinemaHolds || cinema.EditorVisible;
        host.session.Camera.Strategy.EdgePointer = edgeOff ? null : window is null ? null : WindowPointer.InWindow(window);
        host.session.World.Profile.Enabled = host.debugMenu.profiler is { Showing: not FrameProfiler.Mode.Off } || host.g.PlayBench is not null || SpikeLog.Enabled;
        if (!host.Covered) host.session.Advance(frameDt);   // loading and arrival preserve the loaded world's exact time
        host.duel.DuelHoldClock();
        double advanceMs = Stopwatch.GetElapsedTime(a0).TotalMilliseconds;
        host.advanceTotal += advanceMs;
        host.playBench.PlayBenchUpdated(advanceMs);   // before SimProfile, which clears the world's tick profile
        if (host.debugMenu.profiler is not null) SimProfile(host.debugMenu.profiler, advanceMs);   // F12's sim row and its parts
        host.playBench.BenchLap(1);
        host.WorldNews();
        host.saves.HandleSaves(frameDt);   // a quicksave or quickload asked for by a key, the autosave: between ticks, none in flight
        host.newGame.HandleNewGame();
        host.newGame.HandleMainMenu();
        host.StepSceneLoad();
        host.animSeconds += frameDt;
        host.animClock = true;
        host.UpdateNav();
        host.debugOverlays.UpdateNavOverlay();
        host.debugOverlays.UpdateInspectorOverlay();
        host.buildingSync.UpdateBuildings();
        host.playBench.BenchLap(2);
    }

    /// <summary>The drawn camera: the rig's, unless the cinema or a flight holds it; and what the streaming and the simulation read from it.</summary>
    void MoveCamera(double dt)
    {
        host.agentWorld.ControlCameraMoveTick();   // a timed `camera ... --over` move, before the camera is drawn
        if (!cinemaHolds) host.ApplyCamera(host.session.CameraAt());
        cinema.Advance(cinema.FrameTime(dt));   // a flight through the keys moves the camera (and takes it from the rig)
        if (tour.Active && !tour.Advance(dt, host.camera, TourPosition)) EndTour();
        host.gpu.Ahead = tour.ArrivalEye(TourPosition);   // the streaming loads the landing view during the flight
        host.session.ViewEye = cinemaHolds ? host.camera.Eye : null;   // the simulation's far cadence follows the flying camera, not the rig it left
        host.gpu.CameraFlying = tour.Moving;   // the far shadow cascades wait for the landing (docs/render-shadows.md "Flights")
        host.gpu.Kept = !host.config.KeepTexturesAtCharacters ? []
            : host.config.KeepTexturesAtAllSquads ? [.. host.session.CurrentSnapshot.PlayerSquads.SelectMany(s => s.Members).Select(TourPosition).OfType<Vector3>()]
            : [.. host.player.SquadBarMembers().Select(c => TourPosition(c.Id)).OfType<Vector3>()];
        host.viewAspect = FramebufferSizeNow().X / (float)Math.Max(FramebufferSizeNow().Y, 1);   // headless: no window, the size asked for
    }

    /// <summary>The focus the world loads round, and the cameras of the scenes that place it themselves (the sandbox, the editor, the title).</summary>
    void PlaceSceneCameras()
    {
        // A flight puts the focus on where it lands from the first frame (the towns there populate during the flight), not on the arc.
        host.SendFocus(tour.Active && tour.Current is { } landing && TourPosition(landing) is { } landingAt ? landingAt : cinemaHolds ? host.camera.Target : null);
        if (!cinemaHolds) host.sandbox.SandboxFollowCamera();
        if (!cinemaHolds) host.sandbox.FollowCamera();
        if (!cinemaHolds) host.characterEditor.EditorCamera();
        host.characterEditor.OpenPendingEditor();
        host.characterEditor.ResumeAfterCreation();
        if (!cinemaHolds) host.titleScreen.ApplyTitleCamera();
    }

    void AdvanceTitleAndArrival(double frameDt)
    {
        if (host.titleScreen.TitleVisible && !host.warming) host.titleScreen.titleBattle?.Advance(frameDt);
        if (host.skyArrival is not null)
        {
            host.ApplyCamera(host.session.Camera.Current);
            host.titleScreen.ApplyTitleCamera();
            host.ApplySkyArrival();
        }
    }

    /// <summary>
    /// The strategy camera's eye clearance (StrategyCamera's height rule) for what bypasses the rig's pivot: a followed character's feet as the target, and the squad tour's
    /// and character flights' arcs over hills.
    /// </summary>
    void KeepEyeAboveGround()
    {
        if (host.skyArrival is not null || host.titleScreen.TitleVisible || !(tour.Active || !host.session.Camera.IsFree && !cinemaHolds)) return;
        var eye = host.camera.Eye;
        float shortfall = host.session.Camera.EyeGroundAt(eye.X, eye.Z) + Meitou.Data.World.KenshiCamera.EyeClearance(host.camera.Distance) - eye.Y;
        if (shortfall > 0) host.camera.Target += new Vector3(0, shortfall, 0);
    }

    /// <summary>The frame's pointer: the window's mouse or the control channel's, the modifiers, the highlight and the hover outlines.</summary>
    FramePointer PreparePointer(string? takeFile)
    {
        var pointerMouse = mice.FirstOrDefault();
        FeedHeadlessMouse();
        host.devConsoleHost.PumpPendingSay();
        StepControlPointer();
        // Rotation over on Linux: the pointer is let go where it was locked, in the cursor mode it had before, and before its position is read
        // (still locked, GLFW reports its virtual position, and the pointer was drawn there for a frame). The next move starts afresh.
        if (orbitAnchor is not null && !host.OrbitHeld())
        {
            orbitAnchor = null;
            lastMouse = null;
            if (pointerMouse is not null) pointerMouse.Cursor.CursorMode = windowCursor = cursorBeforeOrbit;
        }
        var pointerAt = headless ? host.devConsoleHost.headlessMouse ?? Vector2.Zero : pointerMouse is null ? Vector2.Zero : Pixels(orbitAnchor ?? pointerMouse.Position);
        host.devConsoleHost.consolePointer = pointerAt;
        host.player.CtrlHeld = Held(SilkKey.ControlLeft, SilkKey.ControlRight);
        host.player.AltHeld = Held(SilkKey.AltLeft, SilkKey.AltRight);
        host.screenshotRequested = host.player.GateScreenshot(host.screenshotRequested);
        bool highlighting = Highlight(takeFile);
        bool pointerWorld = (pointerMouse is not null || headless && host.devConsoleHost.headlessMouse is not null) && !host.Covered && !host.g.NoPopulation
            && !(PanelLive() && panel?.Visible == true) && !cinema.Contains(pointerAt);
        bool freeLook = pointerWorld && host.session.Camera.IsFree && !host.player.ModalOpen;
        Outline(takeFile, highlighting, pointerWorld);
        return new FramePointer(pointerMouse, pointerAt, pointerWorld, freeLook);
    }

    /// <summary>Headless: the pointer is the control channel's <c>mouse &lt;x&gt; &lt;y&gt;</c> (none until it is given).</summary>
    void FeedHeadlessMouse()
    {
        if (!headless || host.devConsoleHost.headlessMouse is not { } fedMouse || appliedHeadlessMouse == fedMouse) return;
        appliedHeadlessMouse = fedMouse;
        host.session.Input.SetMousePosition(fedMouse.X, fedMouse.Y);
        if (!host.Covered && !(PanelLive() && panel?.MouseMove(fedMouse) == true) && !cinema.MouseMove(fedMouse)) host.player.MouseMove(fedMouse);
    }

    /// <summary>The control channel's <c>click</c>: one step per frame (move, a frame for the ghost and hover to follow, press, release), through the window's press and release.</summary>
    void StepControlPointer()
    {
        if (host.devConsoleHost.pointerSteps.Count == 0) return;
        var (kind, right, at) = host.devConsoleHost.pointerSteps.Dequeue();
        switch (kind)
        {
            case 'm':
                if (headless) host.devConsoleHost.headlessMouse = at;
                host.session.Input.SetMousePosition(at.X, at.Y);
                if (!host.Covered && !(PanelLive() && panel?.MouseMove(at) == true) && !cinema.MouseMove(at)) host.player.MouseMove(at);
                break;
            case 'd': if (!host.Covered) PointerDown(KeyMap.Map(right ? SilkButton.Right : SilkButton.Left), at); break;
            case 'u': if (!host.Covered) PointerUp(KeyMap.Map(right ? SilkButton.Right : SilkButton.Left), at); break;
        }
    }

    /// <summary>The Highlight key (Alt) lights up the signs, outlines the characters near the focus and labels the ground items there.</summary>
    bool Highlight(string? takeFile)
    {
        bool highlighting = highlightHeld && windowFocused && !host.Covered && !host.session.Camera.IsFree &&
            panel?.Visible != true && !host.agentChannel.interfaceHidden && !cinema.Clean && !cinemaHolds && !cinema.EditorVisible;
        if (host.gpu.Objects is { } signObjects) signObjects.HighlightSigns = highlighting;
        host.player.HighlightFocus = highlighting && !host.screenshotRequested && takeFile is null ? host.camera.Target : null;
        return highlighting;
    }

    /// <summary>Hover and building outlines (Advanced, Meitou only): not in the free camera, with the interface hidden, or in saved pictures.</summary>
    void Outline(string? takeFile, bool highlighting, bool pointerWorld)
    {
        bool outlines = !host.session.Camera.IsFree && !host.agentChannel.interfaceHidden && !cinema.Clean && !host.screenshotRequested && takeFile is null;
        bool outlining = host.config.HoverOutline && outlines;
        if (outlining && highlighting)
            host.gpu.HoverKeys = [.. host.player.HighlightTargets(host.camera.Target, PlayerInterface.HighlightRadius).Select(t => (host.DrawKey(t.Id), t.Colour))];
        else
            host.gpu.HoverKeys = outlining && pointerWorld && host.player.HoverTarget is { } hover ? [(host.DrawKey(hover.Id), hover.Colour)] : [];
        host.gpu.OutlinedBuildings = outlines && host.config.BuildingOutline ? host.player.OutlinedBuildings(hover: pointerWorld) : [];
    }

    // ---- drawing ----

    /// <summary>The frame the display began: the world (or the loading sky), the interface, the loading screen and the pointer, presented, then saved where a picture was asked for.</summary>
    void DrawFrame(Vector2D<int> size, FramePointer pointer, string? takeFile)
    {
        host.shotNoFrame = 0;
        host.playBench.BenchLap(6);
        long t0 = Stopwatch.GetTimestamp();
        var backbuffer = PrepareTargets(size, takeFile, out var uiTarget);
        host.debugMenu.profiler?.BeginFrame();
        // A scene load draws only the loading screen; the warm-up draws whole frames under it (the first frames of a new place build what it needs).
        bool covered = host.Covered;
        DrawWorldOrSky(size, covered);
        StageClock.UiPart(StageClock.UiWorldEnd);
        // MEITOU_DIAG_SUBMIT=1 (issue #108): the world submitted and finished before the interface draws, and the interface before the present.
        if (GpuContext.DiagSubmit) display.Context.SubmitAndWait();
        // The sky arrival's fade to and from black, over the world and under everything else.
        if (host.skyArrival is { Fade: > 0 } fading && overlay is not null)
        {
            overlay.Rect(0, 0, size.X, size.Y, new Vector4(0, 0, 0, fading.Fade));
            overlay.Flush(size.X, size.Y);
        }
        drawTotal += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        bool shot = host.screenshotRequested, take = takeFile is not null;
        // The feedback screenshot: the frame without the menus, the settings, the debug overlays and the pointer (FeedbackCapture).
        bool feedbackShot = host.feedbackCapture.feedbackShotRequested && !take;
        host.player.feedbackPanel.ShotWithoutMenus = feedbackShot;
        DrawInterface(size, covered, shot, take, feedbackShot);
        // The loading screen over the whole frame while a scene loads or the world warms up.
        DrawLoadingCover(size, covered, uiTarget);
        StageClock.UiPart(StageClock.UiLoading);
        if (!shot && !take && !feedbackShot) DrawPointer(size, pointer);
        StageClock.UiPart(StageClock.UiPointer);
        var finalPicture = Present(backbuffer, uiTarget);
        host.playBench.PlayBenchRendered(Stopwatch.GetElapsedTime(t0).TotalMilliseconds, display.Context.Pipelines.LatePipelines);
        TallyFrame();
        SavePictures(size, takeFile, shot, feedbackShot, finalPicture ?? backbuffer, topDown: finalPicture is not null);
    }

    /// <summary>The backbuffer the chain draws into (headless: one of its own), frame generation for this frame, and the interface's cleared layer.</summary>
    GpuTexture PrepareTargets(Vector2D<int> size, string? takeFile, out GpuTexture uiTarget)
    {
        // The chain and the panel draw into the window's backbuffer (made again when the size changes).
        var backbuffer = display.Backbuffer ?? (headlessTarget ??= display.Context.CreateTexture(new TextureDesc(InternalFormat.Rgba8, size.X, size.Y,
            Use: TextureUse.ColourTarget | TextureUse.TransferSrc | TextureUse.Sampled, Name: "headless backbuffer")));
        host.gpu.Post!.Target = backbuffer;
        // Frame generation (the one loaded at start-up): made on the first frame; wanted except under the loading screen and for saved pictures.
        if (frameGen is Meitou.Game.Rendering.Upscalers.FrameGeneration && display.FrameGenerationLayer is null) { frameGen.Dispose(); frameGen = null; }   // the layer fell back to the plain swapchain
        if (!frameGenTried)
        {
            frameGenTried = true;
            frameGen = FrameGenerationSetup.CreateFrameGenerator(display);
        }
        bool frameGenWanted = !host.Covered && !host.screenshotRequested && !host.feedbackCapture.feedbackShotRequested && takeFile is null;
        frameGen?.BeginFrame(frameGenWanted, size.X, size.Y);
        host.gpu.Post!.FrameGen = frameGen;
        // The interface draws into a transparent layer of its own, laid over the backbuffer just before the present.
        uiLayer ??= new UiLayer(display.Context);
        uiTarget = uiLayer.Clear(size.X, size.Y);
        if (overlay is not null) overlay.Target = uiTarget;
        return backbuffer;
    }

    /// <summary>The world, or the loading sky at the stars of a sky arrival while its scene loads; nothing else while a scene loads.</summary>
    void DrawWorldOrSky(Vector2D<int> size, bool covered)
    {
        bool sceneLoading = host.sceneLoad is not null;
        // Under the cover the streams take bigger shares of a frame (smooth frames do not matter there), until the warm-up's content is ready: its run of
        // smooth frames is then measured at the play budgets.
        bool streamsCovered = covered && !(host.warming && host.readySeconds > 0);
        if (host.gpu.Characters is { } coveredCharacters) coveredCharacters.Covered = streamsCovered;
        if (host.gpu.Foliage is { } coveredFoliage) coveredFoliage.Covered = streamsCovered;
        if (host.gpu.Objects is { } coveredObjects) coveredObjects.Covered = streamsCovered;
        if (host.nav is not null) host.nav.Covered = covered;
        // The loading screen follows the interface's mode as it is now (the settings can switch it at any time); only the Faithful mode counts loads.
        bool faithfulUi = host.player.UiMode == Meitou.Data.Ui.UiMode.Faithful;
        host.loadBar.Update(faithfulUi ? host.PendingLoads() : 0);
        host.player.LoadBar = host.loadBar;
        if (host.loading is not null) (host.loading.LoadBar, host.loading.Faithful) = (host.loadBar, faithfulUi);
        if (sceneLoading && host.skyArrival is { AtStars: true })
            WorldFrame.DrawLoadingSky(host.gpu, host.scene, host.camera, size.X, size.Y, host.presentationHour ?? 0, (float)host.animSeconds, host.o.FogDistance);
        else if (!sceneLoading || host.skyArrival is not null)
        {
            // The duel's laps and recorded flights replay exactly, and the play benchmark is deterministic from its save: their ticks stay in step with the frame.
            host.tickAhead = !covered && host.skyArrival is null && !host.duel.IsDuel && !cinema.Recording && host.g.PlayBench is null && host.TickAheadWanted();
            host.DrawWorld(size.X, size.Y);
            host.tickAhead = false;
        }
    }

    /// <summary>The game's interface (markers, HUD, panels), the debug overlays, the settings panel, the cinema's editor and F11's boxes and paths, in that order.</summary>
    void DrawInterface(Vector2D<int> size, bool covered, bool shot, bool take, bool feedbackShot)
    {
        // Hidden by F7, the cinema's clean picture and in recorded frames.
        bool revealing = host.loading is { Exiting: true } && host.sceneLoad is null && !host.warming && host.skyArrival is null;
        // Bake/upload menu surfaces, fonts and pipelines behind the opaque cover, before timing the reveal.
        bool warmTitleUi = host.warming && host.player.TitleOpen && host.skyArrival is null;
        bool ui = !host.agentChannel.interfaceHidden && !cinema.Clean && !take && (!covered || revealing || warmTitleUi);
        host.player.UiVisible = ui && !host.g.NoPopulation;
        host.screenshotRequested = false;
        DrawHud(size, ui, covered, take);
        // The debug overlays are left out of saved pictures: the statistics at the top left, the key list below them, the profiler.
        if (ui && overlay is not null && !shot && !feedbackShot) DrawDebugPanels(overlay, size, covered);
        StageClock.UiPart(StageClock.UiDebug);
        if (ui && !feedbackShot) panel?.Draw(size.X, size.Y);
        StageClock.UiPart(StageClock.UiSettings);
        // Over everything: the letterbox and the cinema's editor (F6, also with F7); the game hides its status line.
        if (overlay is not null && !covered) cinema.Draw(overlay, size.X, size.Y, shot || take || feedbackShot || (host.agentChannel.interfaceHidden && !cinema.EditorVisible), showStatus: false);
        // The character boxes and paths (F11's tickboxes, also F10's Overlays rows), on top of everything; both stay once ticked, with the statistics hidden too.
        if (ui && overlay is not null && !covered && !shot && !feedbackShot)
        {
            if (host.debugOverlays.characterBoxes && host.gpu.Characters is { } boxed) host.debugOverlays.DrawCharacterBoxes(overlay, boxed, host.camera, size.X, size.Y);
            if (host.debugOverlays.walkPaths) host.debugOverlays.DrawWalkPaths(overlay, host.session.CurrentSnapshot, host.camera, size.X, size.Y);
        }
        StageClock.UiPart(StageClock.UiCinema);
    }

    /// <summary>The markers, the steering, the HUD (or the speech alone with the interface hidden), and the sandbox's and the battle's own panels.</summary>
    void DrawHud(Vector2D<int> size, bool ui, bool covered, bool take)
    {
        if (ui && overlay is not null && !host.g.NoPopulation) host.DrawMarkers(overlay, size.X, size.Y);
        StageClock.UiPart(StageClock.UiMarkers);
        if (!host.g.NoPopulation && !covered && panel?.Visible != true) host.player.Steer();
        StageClock.UiPart(StageClock.UiSteer);
        if (ui && overlay is not null && !host.g.NoPopulation) host.player.Draw(overlay, size.X, size.Y);
        else if (host.agentChannel.interfaceHidden && !cinema.Clean && !take && !covered && overlay is not null && !host.g.NoPopulation) host.player.DrawSpeechOnly(overlay, size.X, size.Y);
        if (ui && overlay is not null && host.sandbox.IsSandbox) host.sandbox.DrawSandbox(overlay, size.X, size.Y);
        if (ui && overlay is not null && host.battle.IsBattle) host.battle.DrawBattleCounter(overlay, size.X, size.Y);
        StageClock.UiPart(StageClock.UiHud);
    }

    /// <summary>Below the HUD, which holds the top left corner in the game: the fps counter, the terrain sandbox's tile, F11's statistics, then the menus and the profiler.</summary>
    void DrawDebugPanels(DebugOverlay overlay, Vector2D<int> size, bool covered)
    {
        float panelsBottom = host.g.NoPopulation ? 0 : host.player.HudBottom;
        if (host.debugMenu.fpsVisible && fpsText.Length > 0) panelsBottom = DrawLabel(overlay, size, fpsText, panelsBottom);
        // The terrain sandbox: the texture field's tile under the camera.
        if (GroundRelief.FieldCentre is not null && host.gpu.Terrain.FieldLabelAt(host.camera.Target.X, host.camera.Target.Z) is { } tileLabel) panelsBottom = DrawLabel(overlay, size, tileLabel, panelsBottom);
        if (host.debugOverlays.walkPaths && !covered) panelsBottom = DebugOverlays.DrawWalkPathsLabel(overlay, host.session.CurrentSnapshot, size.X, size.Y, panelsBottom);
        host.debugOverlays.characterBoxesToggle = host.debugOverlays.walkPathsToggle = null;
        float menuTop = panelsBottom;   // the debug menu stands at the right beside the statistics, else below the panels
        if (host.debugMenu.statsVisible && stats.Count > 0)
        {
            host.debugOverlays.TickStatsToggles(stats);
            panelsBottom = overlay.Panel(size.X, size.Y, "Meitou   (F11 hides this)", stats, panelsBottom, laidOut: host.debugOverlays.statsLaidOut);
            host.debugOverlays.FindStatsToggles(overlay.CharWidth);
        }
        if (host.debugMenu.debugMenuOpen) host.debugMenu.DrawDebugMenu(overlay, size.X, size.Y, host.debugMenu.statsVisible ? menuTop : panelsBottom, atRight: host.debugMenu.statsVisible);
        if (host.player.worldMap.MapPick is not null && host.player.worldMap.MapTabShown) host.debugMenu.DrawMapPickHint(overlay, size.X, size.Y);
        if (host.debugMenu.ExplodeClickOn) host.debugMenu.DrawExplodeClickHint(overlay, size.X, size.Y);
        if (host.debugMenu.inspectorMenuOpen) host.debugMenu.DrawInspectorMenu(overlay, size.X, size.Y, panelsBottom);
        if (host.interiorEditorHost.interiorEditorOpen) host.interiorEditorHost.DrawInteriorEditor(overlay, size.X, size.Y, panelsBottom);
        if (host.devConsoleHost.ConsoleOpen) host.devConsoleHost.DrawConsole(overlay, size.X, size.Y);
        host.debugMenu.profiler?.Draw(overlay, size.X, size.Y);
        host.debugMenu.profiler?.DrawVram(overlay, size.X, size.Y);
    }

    /// <summary>One line in a panel at the left below <paramref name="top"/>; returns the new bottom (the panels add their own 16 px margin).</summary>
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
        if (covered && host.loading is not null && host.loadingOverlay is not null)
        {
            host.loadingOverlay.Target = uiTarget;
            if (host.skyArrival is null) host.loading.Draw(size.X, size.Y);
            else if (host.loading.Exiting || host.skyArrival is { AtStars: true, Arriving: false } && host.sceneLoad?.Clock.Elapsed.TotalSeconds >= 2)
                host.loading.Draw(size.X, size.Y, background: false);
        }
        else host.loading?.ReleaseWhenGone();
    }

    /// <summary>
    /// The mouse pointer over everything, as the original's Pointer layer; never in saved pictures. What the right button would do picks it, the arrow over the settings
    /// panel, the cinema's editor and the loading screen. The free camera hides it (the original's <c>invisible</c>); Hardware mouse shows Windows' cursor instead; enhanced,
    /// the action's pointer beside it and the hand over what a click acts on. With frame generation the window system draws the game's pointer (SystemPointer): in a
    /// frame it would come a held-back frame late and be interpolated.
    /// </summary>
    void DrawPointer(Vector2D<int> size, FramePointer pointer)
    {
        if (overlay is null || pointer.Mouse is not { } pointerMouse) return;
        bool freeLook = pointer.FreeLook;
        bool byWindow = frameGen is { Wanted: true } && !host.config.HardwareMouse && !freeLook;
        var cursorMode = orbitAnchor is not null ? Silk.NET.Input.CursorMode.Disabled
            : (host.config.HardwareMouse || byWindow) && !freeLook ? Silk.NET.Input.CursorMode.Normal : Silk.NET.Input.CursorMode.Hidden;
        if (cursorMode != windowCursor) pointerMouse.Cursor.CursorMode = windowCursor = cursorMode;
        string pointerName = pointer.OverWorld ? host.player.PointerName : "arrow";
        bool enhanced = host.config.HardwareMouse && host.config.EnhancedHardwareMouse && !freeLook;
        bool hand = enhanced && (pointer.OverWorld ? host.player.PointsAtClickable(pointerName) : panel?.OverClickable(pointer.At) == true);
        if (byWindow)
        {
            systemPointer.Show(pointerMouse.Cursor, host.player, pointerName);
            return;
        }
        systemPointer.Standard(pointerMouse.Cursor, hand ? Silk.NET.Input.StandardCursor.Hand : Silk.NET.Input.StandardCursor.Default);
        if (!host.config.HardwareMouse && !freeLook) host.player.DrawPointer(pointerName, pointer.At, size.X, size.Y);
        else if (enhanced) host.player.DrawPointerBeside(pointerName, pointer.At, size.X, size.Y);
    }

    /// <summary>
    /// World and interface into the top-down picture the window shows (and a screenshot reads), presented. With frame generation on this frame the window shows the world
    /// alone (the backbuffer, flipped by the present) and the layer lays the interface over every frame, generated ones too: then null.
    /// </summary>
    GpuTexture? Present(GpuTexture backbuffer, GpuTexture uiTarget)
    {
        bool generating = frameGen is not null && frameGen.Finish(backbuffer, uiTarget);
        var finalPicture = generating && frameGen!.PresentsWorldAlone ? null : uiLayer!.Composite(backbuffer);
        StageClock.UiPart(StageClock.UiComposite);
        if (GpuContext.DiagSubmit) display.Context.SubmitAndWait();
        display.Present(finalPicture);
        host.debugMenu.profiler?.EndFrame();
        host.playBench.BenchLap(7);
        return finalPicture;
    }

    /// <summary>The graphics card's time and the drawn triangles, summed for F11's statistics.</summary>
    void TallyFrame()
    {
        if (display.Context.GpuFrameMs > 0) { gpuSum += display.Context.GpuFrameMs; gpuSamples++; }
        statsFrames++;
        terrainTris += host.gpu.Terrain.DrawnTriangles;
        characterTris += host.gpu.Characters?.DrawnTriangles ?? 0;
        if (host.gpu.Foliage is { Enabled: true } drawnFoliage) grassBlades += drawnFoliage.DrawnBlades;
    }

    /// <summary>The pictures asked of this frame: the cinema's take, the control channel's shot, a screenshot (tiled when screens.cfg multiplies it), the feedback shot.</summary>
    void SavePictures(Vector2D<int> size, string? takeFile, bool shot, bool feedbackShot, GpuTexture picture, bool topDown)
    {
        if (takeFile is not null) { FramebufferCapture.SavePng(display.Context, picture, takeFile, size.X, size.Y, topDown: topDown); cinema.FrameDone(); }
        else if (shot && host.agentChannel.shotPath is not null) host.agentChannel.SaveControlShot(display.Context, picture, size.X, size.Y, topDown: topDown);
        else if (shot && GameScreenshots.Multiplier(host.install.Root) is >= 2 and var multiplier) SaveSupersampled(display.Context, multiplier, size.X, size.Y);
        else if (shot) SaveScreenshot(display.Context, picture, size.X, size.Y, topDown: topDown);
        if (shot) host.player.EndScreenshot();
        if (feedbackShot)
        {
            host.player.feedbackPanel.ShotWithoutMenus = false;
            host.feedbackCapture.FeedbackCaptured(FramebufferCapture.ReadPicture(display.Context, picture, size.X, size.Y, topDown: topDown), size.X, size.Y);
        }
    }

    /// <summary>Once a second: F11's statistics, the frame and the simulation, then the renderer's sections.</summary>
    void UpdateStats(double dt)
    {
        statsTimer += dt;
        if (statsTimer < 1) return;
        stats.Clear();
        stats.Add("Performance");
        string limit = display.VSync ? "vsync" : fpsLimit > 0 ? $"limit {fpsLimit}" : "uncapped";
        string gpuMs = gpuSamples > 0 ? $"{gpuSum / gpuSamples:0.0}" : "-";
        stats.Add($"  frame rate    {statsFrames / statsTimer:0} fps{FrameGenerationSetup.FrameGenerationShown(frameGen)}, {limit}; graphics card {gpuMs} ms a frame");
        long ticks = host.session.Simulation.TotalTicks - statsTicks;
        statsTicks = host.session.Simulation.TotalTicks;
        stats.Add($"  simulation    {ticks / statsTimer:0} ticks/s, {host.session.CurrentSnapshot.Characters.Count} characters, {host.session.Simulation.DroppedTicks} ticks dropped since the start");
        long objectTris = host.gpu.Objects?.Totals[0, 1] ?? 0;
        int frames = Math.Max(statsFrames, 1);
        var triangles = new WorldStats.Triangles(terrainTris / frames, (objectTris - objectTris0) / (double)frames, host.gpu.Foliage?.ColourTrianglesPerFrame, characterTris / frames, grassBlades / frames);
        WorldStats.Add(stats, display.Context, host.gpu, host.camera.Eye, triangles, characterBoxes: true);
        host.gpu.Foliage?.ResetDrawTally();
        objectTris0 = objectTris;
        terrainTris = characterTris = grassBlades = 0;
        statsTimer = gpuSum = 0;
        statsFrames = gpuSamples = 0;
    }

    /// <summary>--quit-after: the run's profile, audio, menu and smoke lines.</summary>
    void PrintRunSummary(double now)
    {
        var snapshot = host.session.CurrentSnapshot;
        string characters = host.gpu.Characters is { } chars ? chars.Statistics() : "no characters";
        Console.WriteLine($"profile   {snapshot.Characters.Count} characters in the snapshot, {snapshot.Characters.Count(c => c.Appearance is not null)} drawable; per frame: simulation advance {host.advanceTotal / Math.Max(totalFrames, 1):0.000} ms ({host.session.Simulation.TotalTicks} ticks, {host.advanceTotal / Math.Max(host.session.Simulation.TotalTicks, 1):0.000} ms per tick), host draw-list fill {host.fillTotal / Math.Max(host.fillCalls, 1):0.000} ms, world draw cpu {drawTotal / Math.Max(totalFrames, 1):0.00} ms, {characters}");
        if (host.sound.audio is not null)
        {
            Console.WriteLine($"audio     {host.sound.audio.StatsLine()}");
            Console.WriteLine($"audio     world: {host.sound.WorldAudioStats()}");
            Console.WriteLine($"audio     characters: {host.sound.CharacterAudioStats()}, {host.sound.UiAudioStats()}");
        }
        if (host.menuFrames > 0) Console.WriteLine($"menu      {host.menuFrames} visible frames in {host.menuFrameSeconds:0.0} s: {host.menuFrames / host.menuFrameSeconds:0} fps, {host.menuSlowFrames} frames over 100 ms, longest {host.menuLongestFrame * 1000:0.0} ms");
        Console.WriteLine($"smoke     {totalFrames} frames in {now:0.0} s: {totalFrames / now:0} fps on average, {host.session.Ticks.TotalTicks} control ticks, {host.session.Simulation.TotalTicks} simulation ticks, game time {host.session.Clock.TimeText}");
    }

    // ---- pictures and profiles ----

    /// <summary>
    /// The look the command line chose: Given when <c>--meitou</c> or <c>--faithful</c> is on it; Faithful by the last <c>all</c> (<c>--faithful all</c> true,
    /// <c>--meitou all</c> false), null when only some switches are named (the menu then reads the look from the switches without saving it).
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
        var file = GameScreenshots.Next(host.install.Root);
        FramebufferCapture.SavePng(context, picture, file, width, height, topDown);
        Console.WriteLine($"saved     {file}");
    }

    /// <summary>
    /// The tiled shot of <c>screens.cfg</c>'s <c>multiplier</c> n (docs/game/ui-input.md "Screenshots"): the world alone, no interface, n x n tiles of window size, each drawn with the camera frustum
    /// offset to it (<see cref="ViewWindow"/>) and blitted into one picture n times the window in each direction, as the original does. Screen-space effects therefore keep the window's pixel scale.
    /// Each tile is drawn <see cref="TileGuard"/> pixels larger on every side and cropped, so what samples neighbours (ambient occlusion, bloom) has them at the tile's border; its temporal history is
    /// dropped and warmed up again. The exposure of the live view is pinned for the whole shot, as the original zeroes the adaptation rate.
    /// </summary>
    void SaveSupersampled(GpuContext context, int n, int width, int height) => SaveTiles(context, n, width, height, GameScreenshots.Next(host.install.Root));

    void SaveTiles(GpuContext context, int n, int width, int height, string file)
    {
        var post = host.gpu.Post!;
        int warm = post.WarmupFrames, guard = TileGuard(width, height), tw = width + 2 * guard, th = height + 2 * guard;
        var (adapted, _) = post.ReadExposure();
        if (!float.IsNaN(adapted)) post.HoldExposure(adapted, n * n * (warm + 1) + 2);
        var live = post.Target;
        using var tile = context.CreateTexture(new Meitou.Game.Rendering.Gpu.TextureDesc(Meitou.Game.Rendering.Gpu.InternalFormat.Rgba8, tw, th,
            Use: Meitou.Game.Rendering.Gpu.TextureUse.ColourTarget | Meitou.Game.Rendering.Gpu.TextureUse.TransferSrc | Meitou.Game.Rendering.Gpu.TextureUse.Sampled, Name: "screenshot tile"));
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
                    BlitTile(FramebufferCapture.ReadPicture(context, tile, tw, th), tw, guard, picture, n * width, column * width, row * height, width, height);
                }
        }
        finally { ViewWindow.Current = ViewWindow.Whole; post.Target = live; post.ResetHistory(); }
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (dir is not null) Directory.CreateDirectory(dir);
        Meitou.Data.Textures.PngWriter.Write(file, n * width, n * height, picture);
        Console.WriteLine($"saved     {file} ({n * width} x {n * height}, multiplier {n})");
    }

    /// <summary>The guard band of a tiled shot's tiles: an eighth of the height, at least 16 pixels.</summary>
    static int TileGuard(int width, int height) => Math.Max(16, Math.Min(width, height) / 8);

    /// <summary>Copies the middle <paramref name="width"/> x <paramref name="height"/> of a drawn tile (<paramref name="guard"/> pixels of guard round it) into the picture at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    static void BlitTile(byte[] tile, int tileWidth, int guard, byte[] picture, int pictureWidth, int x, int y, int width, int height)
    {
        for (int r = 0; r < height; r++)
            tile.AsSpan(((r + guard) * tileWidth + guard) * 4, width * 4).CopyTo(picture.AsSpan(((y + r) * pictureWidth + x) * 4));
    }

    /// <summary>--log-spikes, at exit: the most allocated types over the run and the collections by generation, their pauses summed.</summary>
    static void PrintAllocations(BenchGcListener gc)
    {
        var (allocated, collections) = gc.Take();
        long total = allocated.Values.Sum();
        Console.WriteLine($"spikes    allocated {total / 1048576.0:0} MB over the run (sampled); collections {string.Join(", ", collections.GroupBy(c => c.Generation).OrderBy(g => g.Key)
            .Select(g => $"gen{g.Key} x{g.Count()} {g.Sum(c => c.PauseMs):0} ms"))}; the most allocated types (MB):");
        foreach (var (type, bytes) in allocated.OrderByDescending(p => p.Value).Take(30))
            Console.WriteLine($"spikes      {type,-90} {bytes / 1048576.0,9:0}");
    }

    /// <summary>Ctrl+Shift+F12: what is in video memory now, to vram-&lt;time&gt;.txt beside log.txt (<see cref="FrameProfiler.DumpVram"/>).</summary>
    void DumpVram(FrameProfiler? profiler)
    {
        if (profiler is null) return;
        string path = Path.Combine(AppContext.BaseDirectory, $"vram-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        void Caches(TextWriter w)
        {
            if (host.gpu.Guard is { } guard) { w.WriteLine(guard.Status); w.WriteLine(); }
            host.gpu.Objects?.WriteViewReport(w);
            host.gpu.Objects?.Textures.WriteReport(w);
            host.gpu.Foliage?.Textures.WriteReport(w);
            host.gpu.Characters?.Textures.WriteReport(w);
        }
        try { profiler.DumpVram(path, Caches); Console.WriteLine($"vram      dumped to {path}"); }
        catch (IOException e) { Console.WriteLine($"vram      dump failed: {e.Message}"); }
    }

    // F12's sim parts: the names (the tick phases, the time outside the world's ticks, the world's systems), made again when the world is, and the frame's ms.
    World? simNamesFor;
    string[] simNames = [];
    double[] simMs = [];

    /// <summary>Hands the frame's <paramref name="advanceMs"/> to the profiler with the world's tick profile split into phases and systems, and clears the profile.</summary>
    internal void SimProfile(FrameProfiler profiler, double advanceMs)
    {
        var world = host.session.World;
        var profile = world.Profile;
        int phases = TickProfile.PhaseNames.Length;
        int systems = world.Systems.Count, parts = phases + 1 + systems;
        if (simNamesFor != world || simNames.Length != parts + profile.Parts.Count + 1)
        {
            simNamesFor = world;
            simNames = [.. TickProfile.PhaseNames, "outside ticks", .. world.Systems.Select(s => s.GetType().Name is var n && n.EndsWith("System", StringComparison.Ordinal) && n.Length > 6 ? n[..^6] : n),
                .. profile.Parts.Select(p => p.Name), "tick ahead"];
            simMs = new double[simNames.Length];
        }
        Array.Clear(simMs);
        double inTicks = 0, ahead = host.session.TakeAheadMs();
        for (int p = 0; p < phases; p++) inTicks += simMs[p] = profile.PhaseMs[p];
        // The phases also hold the tick run ahead during the last draw (docs/simulation.md "Tick ahead"), which is not in this frame's advance.
        simMs[phases] = Math.Max(0, advanceMs - Math.Max(0, inTicks - ahead));
        for (int k = 0; k < profile.SystemMs.Length && phases + 1 + k < parts; k++) simMs[phases + 1 + k] = profile.SystemMs[k];
        for (int k = 0; k < profile.Parts.Count && parts + k < simMs.Length; k++) simMs[parts + k] = profile.Parts[k].Ms;
        simMs[^1] = ahead;
        int paths = world.Systems.OfType<MovementSystem>().FirstOrDefault()?.Paths.Pending ?? 0;
        profiler.Outside(advanceMs, profile.Ticks, simNames, simMs, phases + 1, systems, paths);
        // --log-spikes: a slow frame's simulation by system and part (the phases only add up what these hold).
        if (SpikeLog.Enabled && advanceMs > GameHost.SpikeSimMs)
            for (int k = phases + 1; k < simMs.Length; k++)
                if (simMs[k] >= 2) SpikeLog.Note($"sim {simNames[k]}", ms: simMs[k]);
        if (SpikeLog.Enabled) SimWindow(profile, advanceMs, ahead, phases + 1, parts);
        profile.Clear();
    }

    // --log-spikes: the simulation's steady cost, summed over a window of real time (the spike notes only see frames over SpikeSimMs).
    const double SimWindowSeconds = 5;
    readonly Dictionary<string, double> simWindowMs = [];
    double simWindowStart = -1, simWindowMain, simWindowAhead;
    long simWindowTicks;
    int simWindowFrames;
    // The ragdolls sampled once a frame: in play, moving, the physics' awake bodies, frames with any moving.
    long simWindowRagdolls, simWindowMoving, simWindowAwake, simWindowCarried, simWindowWhole, simWindowLimbs;
    int simWindowStepping;

    /// <summary>
    /// --log-spikes: every <see cref="SimWindowSeconds"/> one line of the speed, ticks a second, the sim's ms a frame, then ms a tick by phase, by system, by system in the slow
    /// world phase and by part, biggest first. Keys carry their list's prefix: '#' phase, '~' slow world, '.' part.
    /// </summary>
    void SimWindow(TickProfile profile, double advanceMs, double ahead, int firstSystem, int firstPart)
    {
        double now = host.realTime.Elapsed.TotalSeconds;
        if (simWindowStart < 0) simWindowStart = now;
        (simWindowTicks, simWindowMain, simWindowAhead, simWindowFrames) = (simWindowTicks + profile.Ticks, simWindowMain + advanceMs, simWindowAhead + ahead, simWindowFrames + 1);
        void Add(string key, double ms) => simWindowMs[key] = simWindowMs.GetValueOrDefault(key) + ms;
        for (int p = 0; p < firstSystem - 1; p++) Add("#" + simNames[p], simMs[p]);
        for (int k = firstSystem; k < simMs.Length - 1; k++) Add((k >= firstPart ? "." : "") + simNames[k], simMs[k]);
        for (int k = 0; k < profile.SystemMs.Length && firstSystem + k < firstPart; k++) Add("~" + simNames[firstSystem + k], profile.Ms(TickProfile.SlowWorld, k));
        if (host.session.World.Systems.OfType<Meitou.Simulation.Ragdolls.RagdollSystem>().FirstOrDefault() is { } rag)
        {
            (simWindowRagdolls, simWindowMoving, simWindowAwake, simWindowStepping) = (simWindowRagdolls + rag.Count, simWindowMoving + rag.Moving, simWindowAwake + rag.AwakeBodies, simWindowStepping + (rag.Moving > 0 ? 1 : 0));
            (simWindowCarried, simWindowWhole, simWindowLimbs) = (simWindowCarried + rag.MovingCarried, simWindowWhole + rag.MovingWhole, simWindowLimbs + rag.MovingLimbs);
        }
        double seconds = now - simWindowStart;
        if (seconds < SimWindowSeconds) return;
        if (simWindowTicks > 0)
        {
            var inv = CultureInfo.InvariantCulture;
            string Top(char kind, int count) => string.Join(", ", simWindowMs.Where(p => (p.Key[0] is '#' or '~' or '.' ? p.Key[0] : ' ') == kind && p.Value / simWindowTicks >= 0.01)
                .OrderByDescending(p => p.Value).Take(count).Select(p => string.Create(inv, $"{p.Key.TrimStart('#', '~', '.')} {p.Value / simWindowTicks:0.00}")));
            double tickMs = simWindowMs.Where(p => p.Key[0] == '#').Sum(p => p.Value) / simWindowTicks;
            Console.WriteLine(string.Create(inv, $"simprof   {host.session.Simulation.Speed:0}x {simWindowFrames / seconds:0} fps, {simWindowTicks / seconds:0} ticks/s, {host.session.World.Snapshot.Characters.Count} characters; ") +
                string.Create(inv, $"ms a frame: in frame {simWindowMain / simWindowFrames:0.00}, ahead {simWindowAhead / simWindowFrames:0.00}; ms a tick {tickMs:0.00}: ") +
                $"phases: {Top('#', 10)}; systems: {Top(' ', 12)}; slow world: {Top('~', 12)}; parts: {Top('.', 12)}; " +
                string.Create(inv, $"ragdolls {(double)simWindowRagdolls / simWindowFrames:0.0}, moving {(double)simWindowMoving / simWindowFrames:0.0} (carried {(double)simWindowCarried / simWindowFrames:0.0}, whole {(double)simWindowWhole / simWindowFrames:0.0}, limbs {(double)simWindowLimbs / simWindowFrames:0.0}), awake bodies {(double)simWindowAwake / simWindowFrames:0}, stepping {100.0 * simWindowStepping / simWindowFrames:0}% of frames"));
        }
        simWindowMs.Clear();
        (simWindowStart, simWindowTicks, simWindowMain, simWindowAhead, simWindowFrames) = (now, 0, 0, 0, 0);
        (simWindowRagdolls, simWindowMoving, simWindowAwake, simWindowStepping, simWindowCarried, simWindowWhole, simWindowLimbs) = (0, 0, 0, 0, 0, 0, 0);
    }

    // ---- shut-down ----

    /// <summary>The bench's report, the save in flight, the settings, the run's logs, then the renderer and the interface let go.</summary>
    void ShutDown(BenchGcListener? spikeGc)
    {
        host.playBench.WritePlayBench(FramebufferSizeNow());
        // A save still being written finishes before the game closes (the original's quit waits while its save manager is busy, FUN_14082b6e0, Verified).
        try { host.saves.saveWrite?.Wait(); } catch (AggregateException) { }
        SaveSettings();
        // Collect the last frames' GPU timestamps and print the --log-spikes run summary: the game's own one-shot runs
        // otherwise dispose the profiler before the frame ring has delivered them.
        host.debugMenu.profiler?.Flush();
        if (SpikeLog.Enabled) SpikeLog.PrintSummary();
        if (spikeGc is not null) PrintAllocations(spikeGc);
        if (Meitou.Simulation.Navigation.NavQuery.Stats is { Searches: > 0 } paths)
            Console.WriteLine($"paths     {paths.Searches} searches, {paths.Expanded / paths.Searches} polygons each on average, {paths.Exhausted} ran to the limit of {Meitou.Simulation.Navigation.NavQuery.MaxIterations}");
        host.debugMenu.profiler?.Dispose();
        host.player.DisposeUi();
        host.EndLoadingScreen();
        overlay?.Dispose();
        if (host.gpu.Post is { } postChain) postChain.FrameGen = null;
        frameGen?.Dispose();   // before the display: it configures the layer's swapchain
        uiLayer?.Dispose();
        headlessTarget?.Dispose();
        host.titleScreen.DisposeTitleScene();
        host.gpu.Dispose();
        silkInput?.Dispose();
    }

    /// <summary>The settings into meitou.user.json; the command line's window and vsync overrides alone stay this run's.</summary>
    void SaveSettings()
    {
        if (display.VSync != savedVSync) host.config.VSync = display.VSync;
        if (fpsLimit != initialFpsLimit) host.config.FpsLimit = fpsLimit;
        if (FullScreenOn() != savedFullScreen) host.config.FullScreen = FullScreenOn();
        savedVSync = display.VSync;
        savedFullScreen = FullScreenOn();
        initialFpsLimit = fpsLimit;
        if (panel is not null && !lowEnd)
        {
            foreach (var slider in panel.Sliders)
                if (!runOnly.Contains(slider.Label)) host.config.Graphics[slider.Label] = slider.Get();
            foreach (var toggle in panel.Toggles)
                if (!runOnly.Contains(SwitchKey(toggle.Label))) host.config.Graphics[SwitchKey(toggle.Label)] = toggle.Get() ? 1 : 0;
            panel.SavePreset();
        }
        host.config.Bindings = host.session.Bindings.ToDictionary();
        if (!host.g.LowEnd || host.player.UiMode != Meitou.Data.Ui.UiMode.Faithful) host.config.UiMode = host.player.UiMode;   // --low-end's Faithful UI is for the run
        host.config.Save();
    }

    static string SwitchKey(string label) => SettingsMenu.SwitchKeyPrefix + label;
}
