using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Engine;
using Meitou.Engine.Cameras;
using Meitou.Engine.Input;
using Meitou.Engine.Time;
using Meitou.Rendering;
using Meitou.Rendering.Characters;
using Meitou.Simulation;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Display;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using EngineKey = Meitou.Engine.Input.Key;
using EngineButton = Meitou.Engine.Input.MouseButton;
using SilkKey = Silk.NET.Input.Key;
using SilkButton = Silk.NET.Input.MouseButton;

namespace Meitou.Game;

sealed partial class GameHost
{
    int Interactive()
    {
        bool vsync = g.VSync ?? config.VSync;
        int fpsLimit = g.FpsLimit ?? config.FpsLimit;
        int initialFpsLimit = fpsLimit;
        VulkanDisplay.RayTracing = o.WantsRayTracing;
        if (!g.NoPrecompile) JitWarmup.Start();   // the game's methods compiled ahead, from now on, behind the loading
        // The world's files are read on a worker from here on: while the window and the device are made, then behind the loading screen.
        var sceneRead = LoadScene is { } readScene ? Task.Run(readScene) : null;
        BeginAudio();
        bool frameGenerationAtStart = config.FrameGeneration;   // read at start-up only
        using var display = new VulkanDisplay(WindowOptions.Default with
        {
            Size = new Vector2D<int>(o.Width, o.Height),
            Title = "MeitouClient",
            // Hidden while the device is made (a second or two in which nothing can be drawn), shown maximised with the first loading frame.
            IsVisible = false,
            FramesPerSecond = 0,
            UpdatesPerSecond = 0,
        }, vsync, dlss: true, frameGeneration: frameGenerationAtStart);   // Load before the device so the settings panel can switch to DLSS without restarting.
        streamline = display.Streamline;
        ngx = display.Ngx;
        var window = display.Window!;
        // X11 opens it where Silk.NET asks (50,50), the desktop's corner, which can be another monitor (Observed 2026-10-26, XWayland): on the primary
        // monitor instead, the one full screen uses; leaving full screen puts it back where it was when full screen began.
        if (window.Native?.X11 is not null) BorderlessFullScreen.MoveToPrimaryMonitor(window);
        // Borderless full screen as the viewer starts (Alt+Enter and the Tab panel switch it); --windowed keeps a window for
        // this run: the size asked by --size, else maximised. Sized while still hidden: resizing after it shows (maximised,
        // then borderless) flashed white and black before the first frame.
        bool fullScreen = config.FullScreen && !o.Windowed;
        bool savedVSync = vsync, savedFullScreen = fullScreen;
        if (fullScreen) BorderlessFullScreen.Toggle(window);
        else if (!o.SizeGiven) window.WindowState = WindowState.Maximized;
        BeginLoadingScreen(display);   // its textures and fonts are made before the window shows
        // The Faithful start shows the original's splash, as the game does before its title, instead of the loading screen (unless the Interface setting turns it off).
        if (loading is { Faithful: true } && config.SplashScreens && OpensOnTitle(interactive: true)) loading.StartSplash();
        // The first loading frame is in the swapchain when the window shows, and on screen before the splash closes. Not on Wayland: a window gets its
        // shell role when it shows, and a buffer attached to the surface before that makes the compositor drop the client ("attached a buffer before
        // configure event"), so there the window shows first and takes the compositor's configure before anything is drawn.
        if (window.Native?.Wayland is not null)
        {
            window.IsVisible = true;
            window.DoEvents();
        }
        else
        {
            LoadingFrame();
            window.IsVisible = true;
        }
        LoadingFrame();
        Splash?.Dispose();
        try
        {
            if (sceneRead is not null)
            {
                LoadingStep("scene");
                if (AwaitLoading(sceneRead) is not { } loaded) return 1;
                scene = ownedScene = loaded;
            }
            Boot(display, interactive: true);
            AttachAudio();
            BeginWarm();
        }
        catch (LoadingCancelledException)
        {
            Console.WriteLine("loading   cancelled: the window was closed");
            EndLoadingScreen();
            return 0;
        }
        var overlay = DebugOverlay.TryCreate(display.Context, assets.Find);
        if (overlay is not null) { player.CreateUiPainter(overlay, assets.Find); ConnectItemIcons(); }
        if (gpu.Characters is { } portraits) { var (atlas, sampled) = portraits.PortraitAtlas; player.SetUiTexture(PlayerInterface.PortraitTexture, atlas, sampled); }
        // The debug overlays (docs/engine.md): F10 the debug menu (GameHost.DebugMenu.cs), F11 the frame statistics, F12 the profiler chart.
        var profiler = overlay is null ? null : new FrameProfiler(display.Context, () => display.Context.GpuFrameMs);
        // The Faithful / Meitou switches; Shift+F1 turns Meitou off and back on (plain F2..F4 and F8 are the game's speed and screenshot keys), the Tab panel sets them one by one.
        var switches = WorldFrame.LiveSwitches(o, () => gpu, () => camera, () => render);
        var meitou = new MeitouToggle(switches);
        // Keep the shared rendering settings' callbacks and persistence keys; the game supplies its own modal layout.
        // --low-end (WorldOptions.LowEnd) is a preset for this run: the saved sliders and switches would undo it, so they are left as saved and not applied.
        bool lowEnd = Environment.GetCommandLineArgs().Contains("--low-end");
        var runOnly = RunOnlyKeys();   // the keys this run leaves as saved (--bloom)
        var settingsValues = overlay is null ? null : WorldFrame.CreateSettingsPanel(overlay, gpu, render,
            getHour: () => (float)session.Clock.HourOfDay, setHour: v => { session.Clock.SetHourOfDay(v); if (IsDuel) duelHour = v; },
            getVSync: () => display.VSync, setVSync: v => display.VSync = v, switches: switches,
            getFullScreen: () => BorderlessFullScreen.IsOn(window), setFullScreen: _ => BorderlessFullScreen.Toggle(window),
            extraChoices: [new PanelChoice(() => $"UI mode: {player.UiMode}", () => ["Faithful", "Meitou"], () => (int)player.UiMode,
                index =>
                {
                    player.SetUiMode(index switch { 0 => Meitou.Data.Ui.UiMode.Faithful, 1 => Meitou.Data.Ui.UiMode.Meitou, _ => throw new ArgumentOutOfRangeException(nameof(index)) });
                    return $"UI mode: {player.UiMode}";
                })]);
        SettingsMenu? panel = null;
        if (settingsValues is not null)
            panel = new SettingsMenu(player, settingsValues, session.Bindings, SaveSettings,
                new Slider("Frame limit", 0, 360, () => fpsLimit, v => fpsLimit = (int)MathF.Round(v / 5) * 5, "0",
                    Text: v => v <= 0 ? "Unlimited" : $"{v:0} fps"), config)
            {
                UpscalerStatus = () => gpu.Post?.ActiveUpscaler ?? "Off",
                // The state is the start-up setting's; a change shows as pending until the restart.
                FrameGenerationStatus = () => (display.FrameGeneration ? "active" : frameGenerationAtStart ? "unavailable (see the log), plain swapchain in use" : "off")
                    + (config.FrameGeneration != frameGenerationAtStart ? $", {(config.FrameGeneration ? "on" : "off")} after a restart" : ""),
                AttackSlotsChanged = slots => { foreach (var combat in session.World.Systems.OfType<Meitou.Simulation.Combat.CombatSystem>()) combat.AttackSlots = slots; },
                LateDefencesChanged = on => { foreach (var combat in session.World.Systems.OfType<Meitou.Simulation.Combat.CombatSystem>()) combat.LateDefences = on; },
                GunnerFacingChanged = on => { foreach (var combat in session.World.Systems.OfType<Meitou.Simulation.Combat.CombatSystem>()) combat.GunnerFacesBarrel = on; },
                PunchReachChanged = on => { foreach (var combat in session.World.Systems.OfType<Meitou.Simulation.Combat.CombatSystem>()) combat.PunchReachFix = on; },
                DoorsCloseChanged = on => { foreach (var locks in session.World.Systems.OfType<Meitou.Simulation.Locks.LockSystem>()) locks.DoorsCloseBehind = on; },
                AudioChanged = AudioSettingsChanged,
                GameplayChanged = ApplyGameplay,
                SkinRaceList = scene.Database is { } raceDb ? CharacterRenderer.PersonRaces(raceDb) : [],
                SkinRacesChanged = () => { if (gpu.Characters is { } chars) chars.SkinRaces = config.SkinRaces; },
                CameraRotateSpeedChanged = ApplyCameraRotateSpeed,
                TutorialsChanged = on => { if (tutorials is { } t) t.Enabled = on; },
                ResetTutorials = () => tutorials?.Reset(),
                OffscreenChanged = () =>
                {
                    foreach (var population in session.World.Systems.OfType<Meitou.Simulation.PopulationSystem>())
                        (population.KeepRoamers, population.OffscreenFights, population.OffscreenHealing, population.CampaignAncestorsOnce, population.KeepWild, population.ResidentSchedule) = (config.KeepRoamers, config.OffscreenFights, config.OffscreenHealing, config.CampaignAncestorsOnce, config.KeepWild, config.ResidentSchedule);
                    foreach (var production in session.World.Systems.OfType<Meitou.Simulation.Production.ProductionSystem>()) production.UnloadedProduction = config.UnloadedProduction;
                },
            };
        void ApplyCameraRotateSpeed(float speed) => session.Camera.Settings.RotateSpeedX = session.Camera.Settings.RotateSpeedY =
            Math.Clamp(speed, UserConfig.MinCameraRotateSpeed, UserConfig.MaxCameraRotateSpeed);
        ApplyCameraRotateSpeed(config.CameraRotateSpeed);
        config.EdgeScroll ??= Meitou.Audio.AudioSystem.Setting(install, "Edge scrolling", 1) != 0;
        config.Tutorials ??= Meitou.Audio.AudioSystem.Setting(install, "tutorials", 1) != 0;
        tutorials ??= new TutorialManager(Path.Combine(Directory.GetCurrentDirectory(), TutorialManager.FileName));
        tutorials.Enabled = config.Tutorials == true;
        player.ConfigureTutorials(tutorials);
        if (panel is not null && !lowEnd)
            foreach (var slider in panel.Sliders)
                if (!runOnly.Contains(slider.Label) && config.Graphics.TryGetValue(slider.Label, out float v) && !(o.Post.Upscale.Explicit && WorldFrame.UpscalerSliders.Contains(slider.Label))
                    && !(slider.Label == WorldFrame.TextureQualitySlider && Environment.GetCommandLineArgs().Contains("--texture-quality")))   // the saved one was applied before the boot (Program)
                    slider.Set(Math.Clamp(v, slider.Min, slider.Max));
        // The look as the command line chose it: Faithful or Meitou for "all", else read from the switches (not saved).
        if (CommandLineLook() is var (given, faithfulLook) && given) config.FaithfulLook = faithfulLook;
        // The switches as saved, then the saved preset (one the launcher chose), unless the command line chose them (--meitou / --faithful):
        // switches it moved would read as another preset.
        if (panel is not null && !lowEnd && !Environment.GetCommandLineArgs().Any(a => a is "--meitou" or "--faithful"))
        {
            foreach (var toggle in panel.Toggles)
                if (!SettingsMenu.HiddenSwitch(toggle.Label) && !runOnly.Contains(SwitchKey(toggle.Label)) && config.Graphics.TryGetValue(SwitchKey(toggle.Label), out float v) && (v >= 0.5f) != toggle.Get()) toggle.Set(v >= 0.5f);
            panel.ApplySavedPreset();
        }
        var stats = new List<string>();
        bool statsVisible = false, fpsVisible = false;
        string fpsText = "";
        double fpsWindow = 0;
        int fpsWindowFrames = 0;
        // The cinematic camera (docs/viewer.md "Cinematic camera", F6 its editor): while it flies, previews or cuts to a key it holds the drawn
        // camera and the rig's state is not applied; the first camera input after that hands the camera back to the rig (CameraRig.TakeOver).
        // The hour is the game clock's (time does not follow the keys); Home puts the lens back while the editor is open, in the free camera and on a camera the cinema holds.
        var cinema = new Cinema(() => camera, () => (float)session.Clock.HourOfDay, null, () => BorderlessFullScreen.IsOn(window), () => BorderlessFullScreen.Toggle(window),
            Cinema.GameFile, lensKeysAlways: IsDuel);   // the duel has no floors: PageUp/PageDown zoom the lens there
        bool cinemaHolds = false, interfaceHidden = false;
        cinema.Moved = () => cinemaHolds = true;
        // PageUp/PageDown zoom the lens and Home puts it back in these cameras; in the strategy camera PageUp/PageDown are the game's floor keys.
        cinema.LensKeys = () => session.Camera.IsFree || cinemaHolds;
        cinema.Load();
        // Numpad / flies the camera across the squad in portrait order (docs/viewer.md "Squad tour"); it holds the camera like the cinema does.
        var tour = new SquadTour();
        Vector3? TourPosition(CharacterId id) => session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is { } c ? DrawnPosition(c) : null;
        bool tourFollows = false;
        void EndTour()
        {
            tour.Stop();
            cinemaHolds = false;
            session.Camera.TakeOver(new CameraState(camera.Eye, camera.Forward, Vector3.UnitY, camera.Target, camera.Yaw, camera.Pitch, camera.Distance, true), strategy: true);
            if (tourFollows) player.Follow = tour.Current;   // a double-clicked member is followed, as a cut to it would be
        }
        // A double click on a squad member more than FlightDistance from the view flies there (Advanced settings "Character flights").
        player.FlyTo = id =>
        {
            if (!config.CharacterFlights || IsDuel || session.Camera.IsFree || cinema.Playing || cinema.Recording || TourPosition(id) is not { } at) return false;
            if (new Vector2(at.X - camera.Target.X, at.Z - camera.Target.Z).Length() <= UserConfig.FlightDistance) return false;
            player.Follow = null;
            tour.HopSeconds = config.CharacterFlightSeconds;
            tour.Fly(camera, id, TourPosition);
            (cinemaHolds, tourFollows) = (true, true);
            return true;
        };
        // Pressing the member again while the flight is under way (a triple tap) cuts straight to the end of it.
        player.SkipFlight = id =>
        {
            if (!tour.Active || !tourFollows || tour.Current != id) return;
            tour.Finish(camera, TourPosition);
            EndTour();
        };

        var silkInput = Silk.NET.Input.InputWindowExtensions.CreateInput(window);
        // The faithful windows' edit boxes copy and paste through the first keyboard's system clipboard.
        player.ClipboardRead = () => silkInput.Keyboards.Count > 0 ? silkInput.Keyboards[0].ClipboardText : null;
        player.ClipboardWrite = text => { if (silkInput.Keyboards.Count > 0) silkInput.Keyboards[0].ClipboardText = text; };
        Vector2? lastMouse = null;
        // Rotating pins the pointer. Windows and macOS put it back after every move (HoldPointer); Linux locks it (GLFW's disabled cursor),
        // because putting it back over-counts the movement there:
        // - Wayland: the pointer is never put back (glfwSetCursorPos is unavailable), so each move counts the whole distance from the start.
        // - X11 (native and XWayland): moves are counted more than once and the pointer jitters (Observed 2026-10-25).
        // While locked, the game treats the pointer as at orbitAnchor.
        bool lockOrbit = OperatingSystem.IsLinux();
        Vector2? orbitAnchor = null;
        var windowCursor = Silk.NET.Input.CursorMode.Normal;
        var cursorBeforeOrbit = windowCursor;
        bool panelDrag = false, shiftDown = false, profilerClick = false;
        Vector2 Pixels(Vector2 p) => p * new Vector2(window.FramebufferSize.X / (float)Math.Max(window.Size.X, 1), window.FramebufferSize.Y / (float)Math.Max(window.Size.Y, 1));
        // The settings panel takes the mouse only while it is drawn (F7 and the cinema's clean picture hide it).
        bool PanelLive() => !interfaceHidden && !cinema.Clean;
        // Silk raises KeyDown once per press (no OS key repeat), so a held erase or caret key in a text field repeats here: after 0.5 s, then
        // 30 times a second (the Windows defaults). Typed characters already repeat through KeyChar.
        (Silk.NET.Input.IKeyboard Keyboard, SilkKey Key, long Next)? heldEditKey = null;
        bool TextFieldFocused() => panel?.Visible == true || debugMenuOpen || player.UiEditing;
        void RepeatEditKey()
        {
            if (heldEditKey is not { } held || Stopwatch.GetTimestamp() < held.Next) return;
            if (!held.Keyboard.IsKeyPressed(held.Key) || !TextFieldFocused()) { heldEditKey = null; return; }
            heldEditKey = held with { Next = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 30 };
            OnKeyDown(held.Keyboard, held.Key);
        }
        foreach (var kb in silkInput.Keyboards)
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
                if (k is SilkKey.ShiftLeft or SilkKey.ShiftRight) { shiftDown = false; player.SlowModifier = false; }
                if (KeyMap.Map(k) is { } key) { session.Input.SetKey(key, false); player.BuildKeyUp(key); }
            };
            kb.KeyChar += (_, value) =>
            {
                if (Covered) return;
                if (panel?.Visible == true) panel.TextInput(value);
                else if (sandboxPicker is { Open: true }) sandboxPicker.TextInput(value);
                else if (debugMenuOpen) DebugMenuText(value);
                else player.UiText(value);
            };
        }
        void OnKeyDown(Silk.NET.Input.IKeyboard keyboard, SilkKey k)
        {
            if (k is SilkKey.ShiftLeft or SilkKey.ShiftRight) { shiftDown = true; player.SlowModifier = true; }
            if (k == SilkKey.Enter && (keyboard.IsKeyPressed(SilkKey.AltLeft) || keyboard.IsKeyPressed(SilkKey.AltRight))) { BorderlessFullScreen.Toggle(window); return; }
            if (Covered)
            {
                if (k is SilkKey.Escape or SilkKey.Space) skyArrival?.Skip();
                loading?.SkipSplash();
                return;
            }
            player.UiVisible = PanelLive() && !g.NoPopulation;
            if (panel?.Visible == true)
            {
                if ((k == SilkKey.KeypadEnter ? EngineKey.Enter : KeyMap.Map(k)) is { } settingsKey)
                {
                    if (settingsKey is EngineKey.F8 or EngineKey.PrintScreen) screenshotRequested = true;
                    else panel.KeyDown(settingsKey, shiftDown, keyboard.IsKeyPressed(SilkKey.ControlLeft) || keyboard.IsKeyPressed(SilkKey.ControlRight));
                }
                return;
            }
            // The sandbox animation list, while open, takes every key too.
            if (sandboxPicker is { Open: true })
            {
                if ((k == SilkKey.KeypadEnter ? EngineKey.Enter : KeyMap.Map(k)) is { } pickerKey) sandboxPicker.KeyDown(pickerKey);
                return;
            }
            // F10 (rebindable) opens the debug menu, which then takes every key until it closes.
            if (KeyMap.Map(k) is { } mapped && session.Bindings.Get(InputAction.ToggleDebugMenu).Contains(Binding.Of(mapped))) { debugMenuOpen = !debugMenuOpen; debugParts = null; return; }
            if (debugMenuOpen)
            {
                if ((k == SilkKey.KeypadEnter ? EngineKey.Enter : KeyMap.Map(k)) is { } menuKey) DebugMenuKey(menuKey);
                return;
            }
            // toggle_bar (F7, rebindable) hides the game's interface; F6 is the cinema's, and so is the numpad except the keys bound to the game's hotkeys
            // (Space, Delete and the lens keys too while its editor is open).
            if (KeyMap.Map(k) is { } hideKey && !player.UiEditing && session.Bindings.Get(InputAction.ToggleInterface).Contains(Binding.Of(hideKey)))
            { interfaceHidden = !interfaceHidden; Console.WriteLine(interfaceHidden ? "interface hidden (F7 shows it)" : "interface shown"); return; }
            // Ctrl+C copies the free camera's position as a viewer camera code, Ctrl+V flies the free camera to the code in the clipboard.
            // Only in the free camera: elsewhere Ctrl+C and Ctrl+V keep their own meaning, and so do they in a focused edit box.
            if (k is SilkKey.C or SilkKey.V && session.Camera.IsFree && !player.UiEditing && (keyboard.IsKeyPressed(SilkKey.ControlLeft) || keyboard.IsKeyPressed(SilkKey.ControlRight)))
            {
                if (k == SilkKey.C)
                {
                    string code = WorldCameraCode.Encode(camera);
                    keyboard.ClipboardText = code;
                    Console.WriteLine($"camera    copied {code} ({WorldCameraCode.Describe(camera)})");
                }
                else if (WorldCameraCode.Decode(keyboard.ClipboardText) is { } pose)
                {
                    // The code's orbit (target, yaw, pitch, distance) as the free camera's eye and look direction.
                    Vector3 back = new(MathF.Cos(pose.Pitch) * MathF.Sin(pose.Yaw), MathF.Sin(pose.Pitch), MathF.Cos(pose.Pitch) * MathF.Cos(pose.Yaw));
                    session.Camera.TakeOver(new CameraState(pose.Target + back * pose.Distance, -back, Vector3.UnitY, pose.Target, pose.Yaw, pose.Pitch, pose.Distance, true), strategy: false);
                    player.Follow = null;
                    Console.WriteLine("camera    moved to the clipboard's camera code");
                }
                else Console.WriteLine("camera    the clipboard holds no camera code (Ctrl+C in a viewer copies one)");
                return;
            }
            if (panel is not null && KeyMap.Map(k) is { } settingsToggle && session.Bindings.Get(InputAction.ToggleSettings).Contains(Binding.Of(settingsToggle)))
            { panel.Visible = true; return; }
            // Esc is the game menu's, never Quit's while there is a game: with the interface hidden (F7) it comes back with the menu open,
            // under the cinema's clean picture it does nothing.
            if (k == SilkKey.Escape && !g.NoPopulation && !player.UiVisible)
            {
                if (interfaceHidden && !cinema.Clean) { interfaceHidden = false; player.UiVisible = true; player.UiKey(EngineKey.Escape); }
                return;
            }
            if (KeyMap.Map(k) is { } uiKey && player.UiKey(uiKey, keyboard.IsKeyPressed(SilkKey.ControlLeft) || keyboard.IsKeyPressed(SilkKey.ControlRight), shiftDown)) return;
            bool ctrl = keyboard.IsKeyPressed(SilkKey.ControlLeft) || keyboard.IsKeyPressed(SilkKey.ControlRight);
            if (k == SilkKey.KeypadDivide && !IsDuel)
            {
                if (tour.Active) EndTour();
                else if (!cinema.Playing && !cinema.Recording)
                {
                    player.Follow = null;
                    tour.HopSeconds = config.CharacterFlightSeconds;
                    tourFollows = false;
                    tour.Start(camera, [.. player.SquadBarMembers().Select(c => c.Id)], TourPosition);
                    if (tour.Active) cinemaHolds = true;
                    else Console.WriteLine("squad tour: nobody else in the squad to fly to");
                }
                return;
            }
            // A numpad key bound to a game hotkey (the orders, NUM0..NUM8 by default) is the game's, except with Ctrl (saving a shot) or the cinema's editor open (F6).
            bool hotkey = !IsDuel && !ctrl && !cinema.EditorVisible && KeyMap.Map(k) is { } gameKey && player.IsHotkey(gameKey);
            if (!hotkey && Enum.TryParse(k.ToString(), out CinemaKey cinemaKey) && cinema.OnKey(cinemaKey, ctrl, shiftDown)) return;
            // F9 is the game's quickload; the fps counter is on Ctrl+F9 (a modifier, so the key never reaches the input bindings).
            if (k == SilkKey.F9 && ctrl) { fpsVisible = !fpsVisible; return; }
            if (KeyMap.Map(k) is { } key)
            {
                if (shiftDown && key == EngineKey.F1) { Console.WriteLine(meitou.Toggle()); config.FaithfulLook = !meitou.On; return; }
                if (shiftDown && key == EngineKey.F2 && switches.FirstOrDefault(e => e.Id == "skin") is { } skin)   // the skin switch alone, to compare faces
                { skin.IsMeitou = !skin.IsMeitou; Console.WriteLine($"skin      {skin.State}"); return; }
                if (shiftDown && key == EngineKey.F11) { ToggleNavOverlay(); return; }
                if (shiftDown && key == EngineKey.F12) { if (profiler is not null) profiler.ShowVram = !profiler.ShowVram; return; }   // the VRAM pie (F12 alone cycles the profiler)
                if (player.BuildKey(key, shiftDown)) return;
                session.Input.SetKey(key, true);
                player.Key(key, shiftDown);
                SandboxKey(key);
                if (IsDuel && key == EngineKey.Backspace) duelRestartRequested = true;
                if (IsBattle && key == EngineKey.Backspace) battleRestartRequested = true;
            }
        }
        foreach (var mouse in silkInput.Mice)
        {
            mouse.MouseDown += (m, b) =>
            {
                if (Covered) { loading?.SkipSplash(); return; }
                player.UiVisible = PanelLive() && !g.NoPopulation;
                var at = Pixels(orbitAnchor ?? m.Position);
                var physical = KeyMap.Map(b);
                bool select = physical is { } pressed && PlayerButton(pressed) == EngineButton.Left;   // the Select button, whichever it is bound to
                if (PanelLive() && physical is { } settingsButton && panel?.MouseDown(at, settingsButton) == true) { panelDrag = true; return; }
                if (PanelLive() && panel?.Contains(at) == true) return;
                if (select && PanelLive() && !config.SandboxPanelCollapsed && sandboxPicker?.MouseDown(at) == true) return;
                if (select && PanelLive() && SandboxToggleClick(at)) return;
                if (select && profiler?.Click(at) == true) { profilerClick = true; return; }   // F12's legend opens and closes rows
                // The cinema's editor: its clicks never select or move characters.
                if (select && cinema.MouseDown(at)) return;
                if (cinema.Contains(at)) return;
                if (physical is { } button)
                {
                    // Ctrl and Alt with the right button: pick up / put down and loot (docs/game/ui-input.md, Meitou's carry bindings).
                    bool Held(SilkKey a, SilkKey b) => silkInput.Keyboards.Any(k => k.IsKeyPressed(a) || k.IsKeyPressed(b));
                    if (player.MouseDown(PlayerButton(button), at, shiftDown, Held(SilkKey.ControlLeft, SilkKey.ControlRight), Held(SilkKey.AltLeft, SilkKey.AltRight))) return;
                    session.Input.SetMouseButton(button, true);
                }
            };
            mouse.MouseUp += (m, b) =>
            {
                bool cinemaDrag = cinema.MouseUp();
                if (!Covered && !cinemaDrag && !panelDrag && !profilerClick && panel?.Visible != true && KeyMap.Map(b) is { } released) player.MouseUp(PlayerButton(released), Pixels(orbitAnchor ?? m.Position), shiftDown);
                profilerClick = false;
                panel?.MouseUp();
                panelDrag = false;
                if (KeyMap.Map(b) is { } button) session.Input.SetMouseButton(button, false);
            };
            mouse.MouseMove += (m, p) =>
            {
                if (orbitAnchor is not null)
                {
                    // Locked: p is GLFW's virtual position, only its movement counts, and not under a loading screen or the interface (as below).
                    if (lastMouse is { } prev && !Covered && !player.UiCapturesPointer && !player.ModalOpen) session.Input.AddMouseDelta(p.X - prev.X, p.Y - prev.Y);
                    lastMouse = p;
                    return;
                }
                session.Input.SetMousePosition(p.X, p.Y);
                if (Covered) { lastMouse = p; return; }
                if (PanelLive() && panel?.MouseMove(Pixels(p)) == true || panelDrag) { lastMouse = p; return; }
                if (cinema.MouseMove(Pixels(p))) { lastMouse = p; return; }
                bool hold = lastMouse is not null && OrbitHeld();
                player.MouseMove(Pixels(hold ? lastMouse!.Value : p));
                if (player.UiCapturesPointer || player.ModalOpen) { lastMouse = p; return; }
                if (lastMouse is { } last) session.Input.AddMouseDelta(p.X - last.X, p.Y - last.Y);
                if (hold && lockOrbit)
                {
                    // Locked here, not with the cursor mode at the end of the frame: until then the pointer moves on, and GLFW counts that movement
                    // twice (X11) or drops it (Wayland) when the lock starts. Movement is then measured from where GLFW's virtual position starts.
                    orbitAnchor = lastMouse;
                    session.Input.SetMousePosition(lastMouse!.Value.X, lastMouse.Value.Y);
                    cursorBeforeOrbit = windowCursor;
                    m.Cursor.CursorMode = windowCursor = Silk.NET.Input.CursorMode.Disabled;
                    lastMouse = m.Position;
                    return;
                }
                if (hold) { HoldPointer(m, lastMouse!.Value); return; }
                lastMouse = p;
            };
            mouse.Scroll += (m, wheel) =>
            {
                if (Covered || PanelLive() && panel?.Scroll(wheel.Y) == true) return;
                if (PanelLive() && !config.SandboxPanelCollapsed && sandboxPicker?.Scroll(wheel.Y) == true) return;
                var at = Pixels(orbitAnchor ?? m.Position);
                if (!cinema.Contains(at) && !player.UiScroll(at, wheel.Y) && !(PanelLive() && player.ContainsUi(at))) session.Input.AddWheel(wheel.Y);
            };
        }
        Console.WriteLine(GameOptions.Usage[GameOptions.Usage.IndexOf("Keys:", StringComparison.Ordinal)..]);
        Console.WriteLine($"display   vsync {(vsync ? "on" : "off")}, frame limit {(vsync || fpsLimit <= 0 ? "none" : fpsLimit + " fps")}, control tick {session.Ticks.TickRate:0} Hz, world tick {SimulationClock.TickRate:0} per game second; settings in {config.Path}");

        var frame = Stopwatch.StartNew();
        double last = 0;
        int totalFrames = 0;
        bool quit = false;
        player.ScreenAction += e =>
        {
            if (e.Action == "save") saveRequestedFolder = Meitou.Data.Save.SaveCatalog.Folder(Meitou.Simulation.Saving.QuickSave.DefaultRoot, e.Text);
            else if (e.Action == "load") loadRequestedFolder = e.Text;
            else if (e.Action == "import") importRequested = (e.Text, player.ImportFlags);
            else if (e.Action == "new-game") newGameRequested = e.Text;
            else if (e.Action.StartsWith("feedback-", StringComparison.Ordinal)) FeedbackAction(e);
            else if (e.Action == "main-menu") mainMenuRequested = true;
            else if (e.Widget == "OptionsButton" && panel is not null) panel.Visible = true;
            else if (e.Widget == "ExitGameButton") quit = true;
        };
        bool cinemaWasPlaying = false, cinemaWasRecording = false;
        bool highlightHeld = false;
        bool windowFocused = true;
        window.FocusChanged += focused =>
        {
            windowFocused = focused;
            if (!focused) { highlightHeld = false; session.Input.ReleaseAll(); }
        };
        session.Ticked += actions =>
        {
            highlightHeld = actions.Held(InputAction.Highlight);
            // The duel is for filming: Esc must not close it. The terrain sandbox has no game menu: Esc opens the settings (close the window to leave).
            if (actions.Pressed(InputAction.Quit) && g.TerrainSandbox is not null) { if (panel is not null) panel.Visible = true; }
            else if (actions.Pressed(InputAction.Quit) && !IsDuel) quit = true;
            // Panning (or leaving for the free camera) lets go of the character the camera follows; turning and zooming keep it.
            if (actions.Held(InputAction.MoveForward) || actions.Held(InputAction.MoveBack) || actions.Held(InputAction.MoveLeft) || actions.Held(InputAction.MoveRight) ||
                actions.Pressed(InputAction.ToggleFreeCamera)) player.Follow = null;
            if (actions.Pressed(InputAction.ToggleSettings) && panel is not null) panel.Visible = !panel.Visible;
            if (actions.Pressed(InputAction.Screenshot)) screenshotRequested = true;
            if (actions.Pressed(InputAction.ToggleStats)) { statsVisible = !statsVisible; FoliageGpuCull.CountTriangles = statsVisible; }   // the foliage triangles are read from its GPU cull while shown
            if (actions.Pressed(InputAction.ToggleFps)) fpsVisible = !fpsVisible;
            if (actions.Pressed(InputAction.QuickSave)) quickSaveRequested = true;
            if (actions.Pressed(InputAction.QuickLoad)) quickLoadRequested = true;
            // PageUp / PageDown step the viewed floor in the strategy camera: in the free camera and on a held one the cinema takes them for the lens.
            if (actions.Pressed(InputAction.FloorUp)) StepFloor(1);
            if (actions.Pressed(InputAction.FloorDown)) StepFloor(-1);
            if (actions.Pressed(InputAction.CycleProfiler) && profiler is not null) profiler.Showing = (FrameProfiler.Mode)(((int)profiler.Showing + 1) % 3);
            // The camera the cinema left (a cut, a scrub, the end of a flight) goes back to the rig at the first camera input: the free camera at
            // that eye (only it can stand anywhere), or with ; the strategy camera where the view meets the ground. A flight in progress keeps it.
            if (tour.Active && CameraInput(actions)) EndTour();
            else if (cinemaHolds && !cinema.Playing && CameraInput(actions))
            {
                cinemaHolds = false;
                session.Camera.TakeOver(new CameraState(camera.Eye, camera.Forward, Vector3.UnitY, camera.Target, camera.Yaw, camera.Pitch, camera.Distance, true),
                    strategy: actions.Pressed(InputAction.ToggleFreeCamera));
            }
        };
        Meitou.Rendering.UiLayer? uiLayer = null;
        Meitou.Rendering.Upscalers.FrameGeneration? frameGen = null;
        bool frameGenTried = false;
        double drawTotal = 0, gpuSum = 0, statsTimer = 0;
        int gpuSamples = 0, statsFrames = 0;
        long statsTicks = 0;
        // The triangle counts summed over the statistics' second (the objects' from their running colour total).
        double terrainTris = 0, characterTris = 0, grassBlades = 0;
        long objectTris0 = 0;
        while (!window.IsClosing && !quit)
        {
            BenchLap(BenchTail);
            window.DoEvents();
            if (window.IsClosing) break;
            RepeatEditKey();
            double now = frame.Elapsed.TotalSeconds, dt = now - last;
            last = now;
            // The last frame was drawn under the loading screen: a scene load's next stage may run, the warm-up counts it.
            if (Covered) CoveredFrame(dt);
            AdvanceSkyArrival(dt);
            if (g.QuitAfter is not null && TitleVisible && !Covered)
            {
                menuFrames++;
                menuFrameSeconds += dt;
                menuLongestFrame = Math.Max(menuLongestFrame, dt);
                if (dt > 0.1) menuSlowFrames++;
            }
            // The counter: the mean over the last tenth of a second, so a single late frame doesn't read as a drop; then a frame's work
            // (its time without the waits) and its whole time in brackets.
            fpsWindow += dt;
            fpsWindowFrames++;
            if (fpsWindow >= 0.1)
            {
                fpsText = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{fpsWindowFrames / fpsWindow:0} fps  ({fpsWindow * 1000 / fpsWindowFrames:0.0} ms)");
                fpsWindow = 0;
                fpsWindowFrames = 0;
            }
            // While the cinema records, every frame is exactly one frame of its rate, for the simulation too (the take plays at game speed
            // however slowly the frames are drawn and saved); otherwise the real time.
            double frameDt = cinema.Recording ? cinema.FrameTime(dt) : dt;
            // The duel starts a lap when a flight starts (played or recorded), so every take begins with the fight's first frame, and when its lap is over.
            DuelFrame(cinema.Playing && !cinemaWasPlaying || cinema.Recording && !cinemaWasRecording);
            BattleFrame();
            (cinemaWasPlaying, cinemaWasRecording) = (cinema.Playing, cinema.Recording);
            if (!PlayBenchFrame(dt, profiler)) quit = true;
            BenchLap(0);
            long a0 = Stopwatch.GetTimestamp();
            if (panel?.Visible == true)
            {
                foreach (var key in Enum.GetValues<EngineKey>()) session.Input.SetKey(key, false);
                foreach (var button in Enum.GetValues<EngineButton>()) session.Input.SetMouseButton(button, false);
                session.Input.Consume();
            }
            // Edge scrolling (the game's default): the pointer at the edge of the window pans the camera, unless something else has the pointer.
            session.Camera.Settings.EdgeScroll = config.EdgeScroll != false;
            session.Camera.Strategy.EdgePointer = Covered || TitleVisible || panel?.Visible == true || interfaceHidden || cinemaHolds || cinema.EditorVisible ? null : WindowPointer.InWindow(window);
            session.World.Profile.Enabled = profiler is { Showing: not FrameProfiler.Mode.Off } || g.PlayBench is not null;
            if (!Covered) session.Advance(frameDt);   // loading and arrival preserve the loaded world's exact time
            DuelHoldClock();
            double advanceMs = Stopwatch.GetElapsedTime(a0).TotalMilliseconds;
            advanceTotal += advanceMs;
            PlayBenchUpdated(advanceMs);   // before SimProfile, which clears the world's tick profile
            if (profiler is not null) SimProfile(profiler, advanceMs);   // F12's sim row and its parts
            BenchLap(1);
            WorldNews();
            HandleSaves(frameDt);   // a quicksave or quickload asked for by a key, the autosave: between ticks, none in flight
            HandleNewGame();
            HandleMainMenu();
            StepSceneLoad();
            animSeconds += frameDt;
            animClock = true;
            UpdateNav();
            UpdateNavOverlay();
            UpdateBuildings();
            BenchLap(2);
            if (!cinemaHolds) ApplyCamera(session.CameraAt());
            cinema.Advance(cinema.FrameTime(dt));   // a flight through the keys moves the camera (and takes it from the rig)
            if (tour.Active && !tour.Advance(dt, camera, TourPosition)) EndTour();
            gpu.Ahead = tour.ArrivalEye(TourPosition);   // the streaming loads the landing view during the flight
            gpu.Kept = !config.KeepTexturesAtCharacters ? []
                : config.KeepTexturesAtAllSquads ? [.. session.CurrentSnapshot.PlayerSquads.SelectMany(s => s.Members).Select(TourPosition).OfType<Vector3>()]
                : [.. player.SquadBarMembers().Select(c => TourPosition(c.Id)).OfType<Vector3>()];
            string? takeFile = cinema.TakeFrame();
            // A flight puts the focus on where it lands from the first frame (the towns there populate during the flight), not on the arc.
            SendFocus(tour.Active && tour.Current is { } landing && TourPosition(landing) is { } landingAt ? landingAt : cinemaHolds ? camera.Target : null);
            if (!cinemaHolds) SandboxFollowCamera();
            if (!cinemaHolds) FollowCamera();
            if (!cinemaHolds) EditorCamera();
            OpenPendingEditor();
            ResumeAfterCreation();
            if (!cinemaHolds) ApplyTitleCamera();
            BenchLap(3);
            UpdateAudio(dt);
            BenchLap(4);
            if (TitleVisible && !warming) titleBattle?.Advance(frameDt);
            if (skyArrival is not null)
            {
                ApplyCamera(session.Camera.Current);
                ApplyTitleCamera();
                ApplySkyArrival();
            }
            // The strategy camera's eye clearance (StrategyCamera's height rule) for what bypasses the rig's pivot: a followed character's
            // feet as the target, and the squad tour's and character flights' arcs over hills.
            if (skyArrival is null && !TitleVisible && (tour.Active || !session.Camera.IsFree && !cinemaHolds))
            {
                var eye = camera.Eye;
                float shortfall = session.Camera.EyeGroundAt(eye.X, eye.Z) + Meitou.Data.World.KenshiCamera.EyeClearance(camera.Distance) - eye.Y;
                if (shortfall > 0) camera.Target += new Vector3(0, shortfall, 0);
            }
            var size = window.FramebufferSize;
            // Until the warm-up's frames run smoothly the water reflection draws everything and the sky its cloud volume, so their pipelines are made under the
            // cover (ReflectionPass.Warm, SkyRenderer.Warm).
            bool warmPasses = warming && smoothSeconds == 0;
            if (gpu.Reflection is { } reflection) reflection.Warm = warmPasses;
            gpu.Sky.Warm = warmPasses;
            UpdateView(size.X, size.Y);
            // What the mouse is over, decided once before the world is drawn: the pointer (drawn last) and the hover outline (drawn with the world) agree.
            var pointerMouse = silkInput.Mice.FirstOrDefault();
            // Rotation over on Linux: the pointer is let go where it was locked, in the cursor mode it had before, and before its position is read
            // (still locked, GLFW reports its virtual position, and the pointer was drawn there for a frame). The next move starts afresh.
            if (orbitAnchor is not null && !OrbitHeld())
            {
                orbitAnchor = null;
                lastMouse = null;
                if (pointerMouse is not null) pointerMouse.Cursor.CursorMode = windowCursor = cursorBeforeOrbit;
            }
            var pointerAt = pointerMouse is null ? Vector2.Zero : Pixels(orbitAnchor ?? pointerMouse.Position);
            bool Held(SilkKey a, SilkKey b) => silkInput.Keyboards.Any(k => k.IsKeyPressed(a) || k.IsKeyPressed(b));
            player.CtrlHeld = Held(SilkKey.ControlLeft, SilkKey.ControlRight);
            player.AltHeld = Held(SilkKey.AltLeft, SilkKey.AltRight);
            // The Highlight key (Alt) lights up the signs, outlines the characters near the focus and labels the ground items there.
            bool highlighting = highlightHeld && windowFocused && !Covered && !session.Camera.IsFree &&
                panel?.Visible != true && !interfaceHidden && !cinema.Clean && !cinemaHolds && !cinema.EditorVisible;
            if (gpu.Objects is { } signObjects) signObjects.HighlightSigns = highlighting;
            player.HighlightFocus = highlighting && !screenshotRequested && takeFile is null ? camera.Target : null;
            bool pointerWorld = pointerMouse is not null && !Covered && !g.NoPopulation && !(PanelLive() && panel?.Visible == true) && !cinema.Contains(pointerAt);
            bool freeLook = pointerWorld && session.Camera.IsFree && !player.ModalOpen;
            // Hover outline (Advanced, Meitou only): not in the free camera, with the interface hidden, or in saved pictures.
            bool outlining = config.HoverOutline && !session.Camera.IsFree && !interfaceHidden && !cinema.Clean && !screenshotRequested && takeFile is null;
            if (outlining && highlighting)
                gpu.HoverKeys = [.. player.HighlightTargets(camera.Target, PlayerInterface.HighlightRadius).Select(t => (DrawKey(t.Id), t.Colour))];
            else
                gpu.HoverKeys = outlining && pointerWorld && player.HoverTarget is { } hover ? [(DrawKey(hover.Id), hover.Colour)] : [];
            BenchLap(5);
            bool began = display.BeginFrame(size.X, size.Y);
            if (began)
            {
                BenchLap(6);
                long t0 = Stopwatch.GetTimestamp();
                // The chain and the panel draw into the window's backbuffer (made again when the size changes).
                var backbuffer = display.Backbuffer!;
                gpu.Post!.Target = backbuffer;
                // Frame generation (the setting, when the FFX layer is in use): made on the first frame; wanted except under the loading screen and for saved pictures.
                if (frameGen is not null && display.FrameGenerationLayer is null) { frameGen.Dispose(); frameGen = null; }   // the layer fell back to the plain swapchain
                if (!frameGenTried && display.FrameGenerationLayer is { } fgLayer)
                {
                    frameGenTried = true;
                    frameGen = Meitou.Rendering.Upscalers.FrameGeneration.TryCreate(display.Context, fgLayer, out var fgWhy);
                    if (frameGen is null) Console.WriteLine($"frame generation  unavailable: {fgWhy}");
                }
                frameGen?.BeginFrame(!Covered && !screenshotRequested && !feedbackShotRequested && takeFile is null, size.X, size.Y);
                gpu.Post!.FrameGen = frameGen;
                // The interface draws into a transparent layer of its own, laid over the backbuffer just before the present.
                uiLayer ??= new Meitou.Rendering.UiLayer(display.Context);
                var uiTarget = uiLayer.Clear(size.X, size.Y);
                if (overlay is not null) overlay.Target = uiTarget;
                profiler?.BeginFrame();
                // A scene load draws only the loading screen; the warm-up draws whole frames under it (the first frames of a new place build what it needs).
                bool covered = Covered, sceneLoading = sceneLoad is not null;
                // Under the cover the streams take bigger shares of a frame (smooth frames do not matter there), until the warm-up's content is ready: its run of
                // smooth frames is then measured at the play budgets.
                bool streamsCovered = covered && !(warming && readySeconds > 0);
                if (gpu.Characters is { } coveredCharacters) coveredCharacters.Covered = streamsCovered;
                if (gpu.Foliage is { } coveredFoliage) coveredFoliage.Covered = streamsCovered;
                if (gpu.Objects is { } coveredObjects) coveredObjects.Covered = streamsCovered;
                // The loading screen follows the interface's mode as it is now (the settings can switch it at any time); only the Faithful mode counts loads.
                bool faithfulUi = player.UiMode == Meitou.Data.Ui.UiMode.Faithful;
                loadBar.Update(faithfulUi ? PendingLoads() : 0);
                player.LoadBar = loadBar;
                if (loading is not null) (loading.LoadBar, loading.Faithful) = (loadBar, faithfulUi);
                if (sceneLoading && skyArrival is { AtStars: true })
                    WorldFrame.DrawLoadingSky(gpu, scene, camera, size.X, size.Y, presentationHour ?? 0, (float)animSeconds, o.FogDistance);
                else if (!sceneLoading || skyArrival is not null)
                {
                    // The duel's laps and recorded flights replay exactly, and the play benchmark is deterministic from its save: their ticks stay in step with the frame.
                    tickAhead = !covered && skyArrival is null && !IsDuel && !cinema.Recording && g.PlayBench is null && TickAheadWanted();
                    DrawWorld(size.X, size.Y);
                    tickAhead = false;
                }
                // The sky arrival's fade to and from black, over the world and under everything else.
                if (skyArrival is { Fade: > 0 } fading && overlay is not null)
                {
                    overlay.Rect(0, 0, size.X, size.Y, new Vector4(0, 0, 0, fading.Fade));
                    overlay.Flush(size.X, size.Y);
                }
                drawTotal += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                bool shot = screenshotRequested, take = takeFile is not null;
                // The feedback screenshot: the frame without the menus, the settings, the debug overlays and the pointer (GameHost.Feedback.cs).
                bool feedbackShot = feedbackShotRequested && !take;
                player.ShotWithoutMenus = feedbackShot;
                // The game's interface (markers, HUD, panels, debug overlays): hidden by F7, the cinema's clean picture and in recorded frames.
                bool revealing = loading is { Exiting: true } && sceneLoad is null && !warming && skyArrival is null;
                // Bake/upload menu surfaces, fonts and pipelines behind the opaque cover, before timing the reveal.
                bool warmTitleUi = warming && player.TitleOpen && skyArrival is null;
                bool ui = !interfaceHidden && !cinema.Clean && !take && (!covered || revealing || warmTitleUi);
                player.UiVisible = ui && !g.NoPopulation;
                screenshotRequested = false;
                if (ui && overlay is not null && !g.NoPopulation) DrawMarkers(overlay, size.X, size.Y);
                if (!g.NoPopulation && !covered && panel?.Visible != true) player.Steer();
                if (ui && overlay is not null && !g.NoPopulation)
                {
                    player.Draw(overlay, size.X, size.Y);
                }
                if (ui && overlay is not null && IsSandbox) DrawSandbox(overlay, size.X, size.Y);
                if (ui && overlay is not null && IsBattle) DrawBattleCounter(overlay, size.X, size.Y);
                // The debug overlays are left out of saved pictures: the statistics at the top left, the key list below them, the profiler.
                if (ui && overlay is not null && !shot && !feedbackShot)
                {
                    // Below the HUD, which holds the top left corner in the game (the viewer's overlays start at the top).
                    float panelsBottom = g.NoPopulation ? 0 : player.HudBottom;
                    if (fpsVisible && fpsText.Length > 0)
                    {
                        float w = fpsText.Length * overlay.CharWidth + 12, h = overlay.LineHeight + 8, top = panelsBottom + 8;
                        overlay.Rect(8, top, 8 + w, top + h, DebugOverlay.PanelColour);
                        overlay.Text(fpsText, 14, top + 4, DebugOverlay.TextColour);
                        overlay.Flush(size.X, size.Y);
                        panelsBottom = top + h - 8;   // the panels add their own 16 px margin
                    }
                    // The terrain sandbox: the texture field's tile under the camera.
                    if (Meitou.Rendering.GroundRelief.FieldCentre is not null && gpu.Terrain.FieldLabelAt(camera.Target.X, camera.Target.Z) is { } tileLabel)
                    {
                        float w = tileLabel.Length * overlay.CharWidth + 12, h = overlay.LineHeight + 8, top = panelsBottom + 8;
                        overlay.Rect(8, top, 8 + w, top + h, DebugOverlay.PanelColour);
                        overlay.Text(tileLabel, 14, top + 4, DebugOverlay.TextColour);
                        overlay.Flush(size.X, size.Y);
                        panelsBottom = top + h - 8;
                    }
                    if (statsVisible && stats.Count > 0) panelsBottom = overlay.Panel(size.X, size.Y, "Meitou   (F11 hides this)", stats, panelsBottom);
                    if (debugMenuOpen) DrawDebugMenu(overlay, size.X, size.Y, panelsBottom);
                    profiler?.Draw(overlay, size.X, size.Y);
                    profiler?.DrawVram(overlay, size.X, size.Y);
                }
                if (ui && !feedbackShot) panel?.Draw(size.X, size.Y);
                // Over everything: the letterbox and the cinema's editor (F6, also with F7); the game hides its status line.
                if (overlay is not null && !covered) cinema.Draw(overlay, size.X, size.Y, shot || take || feedbackShot || (interfaceHidden && !cinema.EditorVisible), showStatus: false);
                // The loading screen over the whole frame while a scene loads or the world warms up.
                if (covered && loading is not null && loadingOverlay is not null)
                {
                    loadingOverlay.Target = uiTarget;
                    if (skyArrival is null) loading.Draw(size.X, size.Y);
                    else if (loading.Exiting || skyArrival is { AtStars: true, Arriving: false } && sceneLoad?.Clock.Elapsed.TotalSeconds >= 2)
                        loading.Draw(size.X, size.Y, background: false);
                }
                // The mouse pointer over everything, as the original's Pointer layer; never in saved pictures. What the right button would do picks it,
                // the arrow over the settings panel, the cinema's editor and the loading screen. The free camera hides it (the original's `invisible`);
                // Hardware mouse shows Windows' cursor instead.
                if (!shot && !take && !feedbackShot && overlay is not null && pointerMouse is not null)
                {
                    var cursorMode = orbitAnchor is not null ? Silk.NET.Input.CursorMode.Disabled
                        : config.HardwareMouse && !freeLook ? Silk.NET.Input.CursorMode.Normal : Silk.NET.Input.CursorMode.Hidden;
                    if (cursorMode != windowCursor) pointerMouse.Cursor.CursorMode = windowCursor = cursorMode;
                    if (!config.HardwareMouse && !freeLook) player.DrawPointer(pointerWorld ? player.PointerName : "arrow", pointerAt, size.X, size.Y);
                }
                // World and interface into the top-down picture the window shows (and a screenshot reads). With frame generation on this frame the window shows the
                // world alone (the backbuffer, flipped by the present) and the layer lays the interface over every frame, generated ones too.
                bool generating = frameGen is not null && frameGen.Finish(uiTarget);
                var finalPicture = generating ? null : uiLayer.Composite(backbuffer);
                display.Present(finalPicture);
                profiler?.EndFrame();
                BenchLap(7);
                PlayBenchRendered(Stopwatch.GetElapsedTime(t0).TotalMilliseconds, display.Context.Pipelines.LatePipelines);
                if (display.Context.GpuFrameMs > 0) { gpuSum += display.Context.GpuFrameMs; gpuSamples++; }
                statsFrames++;
                terrainTris += gpu.Terrain.DrawnTriangles;
                characterTris += gpu.Characters?.DrawnTriangles ?? 0;
                if (gpu.Foliage is { Enabled: true } drawnFoliage) grassBlades += drawnFoliage.DrawnBlades;
                if (takeFile is not null) { FramebufferCapture.SavePng(display.Context, finalPicture ?? backbuffer, takeFile, size.X, size.Y, topDown: finalPicture is not null); cinema.FrameDone(); }
                else if (shot) SaveScreenshot(display.Context, finalPicture ?? backbuffer, size.X, size.Y, topDown: finalPicture is not null);
                if (feedbackShot)
                {
                    player.ShotWithoutMenus = false;
                    FeedbackCaptured(FramebufferCapture.ReadPicture(display.Context, finalPicture ?? backbuffer, size.X, size.Y, topDown: finalPicture is not null), size.X, size.Y);
                }
            }
            statsTimer += dt;
            if (statsTimer >= 1)
            {
                // Once a second: the frame and the simulation, then the renderer's sections.
                stats.Clear();
                stats.Add("Performance");
                stats.Add($"  frame rate    {statsFrames / statsTimer:0} fps{(frameGen is { Enabled: true } ? " (x2 shown with frame generation)" : "")}, {(display.VSync ? "vsync" : fpsLimit > 0 ? $"limit {fpsLimit}" : "uncapped")}; graphics card {(gpuSamples > 0 ? $"{gpuSum / gpuSamples:0.0}" : "-")} ms a frame");
                long ticks = session.Simulation.TotalTicks - statsTicks;
                statsTicks = session.Simulation.TotalTicks;
                stats.Add($"  simulation    {ticks / statsTimer:0} ticks/s, {session.CurrentSnapshot.Characters.Count} characters, {session.Simulation.DroppedTicks} ticks dropped since the start");
                long objectTris = gpu.Objects?.Totals[0, 1] ?? 0;
                int frames = Math.Max(statsFrames, 1);
                WorldStats.Add(stats, display.Context, gpu, camera.Eye,
                    new WorldStats.Triangles(terrainTris / frames, (objectTris - objectTris0) / (double)frames, gpu.Foliage?.ColourTrianglesPerFrame, characterTris / frames, grassBlades / frames));
                gpu.Foliage?.ResetDrawTally();
                objectTris0 = objectTris;
                terrainTris = characterTris = grassBlades = 0;
                statsTimer = gpuSum = 0;
                statsFrames = gpuSamples = 0;
            }
            totalFrames++;
            if (g.QuitAfter is { } quitAfter && now >= quitAfter) { Console.WriteLine($"profile   {session.CurrentSnapshot.Characters.Count} characters in the snapshot, {session.CurrentSnapshot.Characters.Count(c => c.Appearance is not null)} drawable; per frame: simulation advance {advanceTotal / Math.Max(totalFrames, 1):0.000} ms ({session.Simulation.TotalTicks} ticks, {advanceTotal / Math.Max(session.Simulation.TotalTicks, 1):0.000} ms per tick), host draw-list fill {fillTotal / Math.Max(fillCalls, 1):0.000} ms, world draw cpu {drawTotal / Math.Max(totalFrames, 1):0.00} ms, {(gpu.Characters is { } chars ? chars.Statistics() : "no characters")}");
                if (audio is not null) Console.WriteLine($"audio     {audio.StatsLine()}");
                if (audio is not null) Console.WriteLine($"audio     world: {WorldAudioStats()}");
                if (audio is not null) Console.WriteLine($"audio     characters: {CharacterAudioStats()}, {UiAudioStats()}");
                if (menuFrames > 0) Console.WriteLine($"menu      {menuFrames} visible frames in {menuFrameSeconds:0.0} s: {menuFrames / menuFrameSeconds:0} fps, {menuSlowFrames} frames over 100 ms, longest {menuLongestFrame * 1000:0.0} ms");
                Console.WriteLine($"smoke     {totalFrames} frames in {now:0.0} s: {totalFrames / now:0} fps on average, {session.Ticks.TotalTicks} control ticks, {session.Simulation.TotalTicks} simulation ticks, game time {session.Clock.TimeText}"); quit = true; }
            if (!display.VSync && fpsLimit > 0)
            {
                // The limit is on presented frames: with frame generation on every rendered frame brings a generated one, so the render rate is half of it.
                Limit(frame, now, (frameGen is { Enabled: true } ? 2.0 : 1.0) / fpsLimit);
            }
        }
        WritePlayBench(window.FramebufferSize);
        // A save still being written finishes before the game closes (the original's quit waits while its save manager is busy, FUN_14082b6e0, Verified).
        try { saveWrite?.Wait(); } catch (AggregateException) { }
        SaveSettings();
        // Collect the last frames' GPU timestamps and print the --log-spikes run summary: the game's own one-shot runs
        // otherwise dispose the profiler before the frame ring has delivered them.
        profiler?.Flush();
        if (SpikeLog.Enabled) SpikeLog.PrintSummary();
        profiler?.Dispose();
        player.DisposeUi();
        EndLoadingScreen();
        overlay?.Dispose();
        if (gpu.Post is { } postChain) postChain.FrameGen = null;
        frameGen?.Dispose();   // before the display: it configures the layer's swapchain
        uiLayer?.Dispose();
        DisposeTitleScene();
        gpu.Dispose();
        silkInput.Dispose();
        return 0;

        void SaveSettings()
        {
            // Command-line window/vsync overrides alone remain local to this run.
            if (display.VSync != savedVSync) config.VSync = display.VSync;
            if (fpsLimit != initialFpsLimit) config.FpsLimit = fpsLimit;
            if (BorderlessFullScreen.IsOn(window) != savedFullScreen) config.FullScreen = BorderlessFullScreen.IsOn(window);
            savedVSync = display.VSync;
            savedFullScreen = BorderlessFullScreen.IsOn(window);
            initialFpsLimit = fpsLimit;
            if (panel is not null && !lowEnd)
            {
                foreach (var slider in panel.Sliders)
                    if (!runOnly.Contains(slider.Label)) config.Graphics[slider.Label] = slider.Get();
                foreach (var toggle in panel.Toggles)
                    if (!runOnly.Contains(SwitchKey(toggle.Label))) config.Graphics[SwitchKey(toggle.Label)] = toggle.Get() ? 1 : 0;
                panel.SavePreset();
            }
            config.Bindings = session.Bindings.ToDictionary();
            config.UiMode = player.UiMode;
            config.Save();
        }

        static string SwitchKey(string label) => SettingsMenu.SwitchKeyPrefix + label;
    }

    /// <summary>Panel rows that are the session's state, not graphics settings: kept out of <c>graphics</c> in meitou.user.json (vsync and full screen have their own keys).</summary>
    static readonly HashSet<string> SessionSliders = ["Time of day", "VSync", "Full screen"];

    /// <summary>
    /// The <c>graphics</c> keys this run neither reads from nor writes to meitou.user.json: <see cref="SessionSliders"/>, and the bloom's switch and strength
    /// when <c>--bloom</c> sets them for one run.
    /// </summary>
    static HashSet<string> RunOnlyKeys()
    {
        var keys = new HashSet<string>(SessionSliders);
        if (Environment.GetCommandLineArgs().Contains("--bloom")) { keys.Add(WorldFrame.BloomStrengthSlider); keys.Add(SettingsMenu.SwitchKeyPrefix + WorldFrame.BloomSwitch); }
        return keys;
    }

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

    /// <summary>The player moved, turned, zoomed or switched the camera this tick (what hands a camera the cinema left back to the rig).</summary>
    static bool CameraInput(ActionState a) =>
        a.Held(InputAction.MoveForward) || a.Held(InputAction.MoveBack) || a.Held(InputAction.MoveLeft) || a.Held(InputAction.MoveRight) ||
        a.Held(InputAction.MoveUp) || a.Held(InputAction.MoveDown) || a.Held(InputAction.RotateLeft) || a.Held(InputAction.RotateRight) ||
        a.Held(InputAction.PitchUp) || a.Held(InputAction.PitchDown) || a.Held(InputAction.ZoomIn) || a.Held(InputAction.ZoomOut) ||
        a.Held(InputAction.Orbit) && a.MouseDelta != Vector2.Zero || a.Wheel != 0 || a.Pressed(InputAction.ToggleFreeCamera);

    /// <summary>A key or button bound to Orbit (middle button or Ctrl by default) is down.</summary>
    bool OrbitHeld() => session.Bindings.Get(InputAction.Orbit).Any(session.Input.IsHeld);

    /// <summary>
    /// The button the interface sees for a physical one: the Select button is its left, the Command button its right (each falls back to its own default while unbound, so
    /// the interface stays reachable). Any other of the three, such as the Orbit button, is its middle (which the map pans with and which stops no click reaching the world
    /// under the interface); the extra buttons stay as they are.
    /// </summary>
    EngineButton PlayerButton(EngineButton physical)
    {
        var select = session.Bindings.Get(InputAction.Select);
        var command = session.Bindings.Get(InputAction.Command);
        if (select.Count == 0 ? physical == EngineButton.Left : select.Contains(Binding.Of(physical))) return EngineButton.Left;
        if (command.Count == 0 ? physical == EngineButton.Right : command.Contains(Binding.Of(physical))) return EngineButton.Right;
        return physical is EngineButton.Left or EngineButton.Right ? EngineButton.Middle : physical;
    }

    /// <summary>While the camera orbits the pointer stays where it was, as in the original: each movement turns the camera and the pointer is put back.</summary>
    void HoldPointer(Silk.NET.Input.IMouse mouse, Vector2 anchor)
    {
        mouse.Position = anchor;
        session.Input.SetMousePosition(anchor.X, anchor.Y);
    }

    static void SaveScreenshot(GpuContext context, GpuTexture picture, int width, int height, bool topDown = false)
    {
        var file = Path.Combine(Directory.CreateDirectory(@"C:\Temp").FullName, $"meitou-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        FramebufferCapture.SavePng(context, picture, file, width, height, topDown);
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

    // F12's sim parts: the names (the tick phases, the time outside the world's ticks, the world's systems), made again when the world is, and the frame's ms.
    World? simNamesFor;
    string[] simNames = [];
    double[] simMs = [];

    /// <summary>Hands the frame's <paramref name="advanceMs"/> to the profiler with the world's tick profile split into phases and systems, and clears the profile.</summary>
    void SimProfile(FrameProfiler profiler, double advanceMs)
    {
        var world = session.World;
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
        double inTicks = 0, ahead = session.TakeAheadMs();
        for (int p = 0; p < phases; p++) inTicks += simMs[p] = profile.PhaseMs[p];
        // The phases also hold the tick run ahead during the last draw (docs/simulation.md "Tick ahead"), which is not in this frame's advance.
        simMs[phases] = Math.Max(0, advanceMs - Math.Max(0, inTicks - ahead));
        for (int k = 0; k < profile.SystemMs.Length && phases + 1 + k < parts; k++) simMs[phases + 1 + k] = profile.SystemMs[k];
        for (int k = 0; k < profile.Parts.Count && parts + k < simMs.Length; k++) simMs[parts + k] = profile.Parts[k].Ms;
        simMs[^1] = ahead;
        int paths = world.Systems.OfType<MovementSystem>().FirstOrDefault()?.Paths.Pending ?? 0;
        profiler.Outside(advanceMs, profile.Ticks, simNames, simMs, phases + 1, systems, paths);
        profile.Clear();
    }

    /// <summary>Between ticks: the world's messages for the player go to the HUD and the log, the fights hostility started to the log.</summary>
    void WorldNews()
    {
        var world = session.World;
        foreach (string message in world.TakeMessages())
        {
            Console.WriteLine($"relations {message}");
            player.Notice(message);
        }
        foreach (var line in world.TakeSaid()) player.LogMessage(line.Owner, line.Text, line.Player ? MessageTone.Player : MessageTone.Normal);
        // Stealth knock-outs: the original's floating "*Thunk*" (a hit) or "Failed!" over the attacker, here as a notice.
        if (world.Systems.OfType<Meitou.Simulation.Carrying.CarrySystem>().FirstOrDefault() is { Knockouts.Count: > 0 } carry)
            foreach (var k in carry.TakeKnockouts())
                if (world.Characters.IsAlive(k.Attacker) && world.Characters.Cold(k.Attacker.Slot) is { IsPlayer: true } attacker)
                    player.Notice($"{attacker.Name}: {Loc.Tr(k.Success ? "*Thunk*" : "Failed!")}{(k.NoXp ? Loc.Tr(" (No XP)") : "")} ({Math.Clamp(k.Chance, 0, 100):0}%)", !k.Success);
        if (population is not null)
            foreach (var line in population.TakeTownLog()) Console.WriteLine($"town      {line}");
        if (population is not null)
            foreach (var w in population.TakeWildLog())
                Console.WriteLine($"wild      tick {w.Tick}: {w.Squad} ({w.Members}) from the {(w.Kind == Meitou.Simulation.WildKind.Nest ? "nest" : "sector of")} {w.Place}");
        if (aggro is null || aggro.Log.Count == 0) return;
        var table = world.Characters;
        string Who(Meitou.Simulation.CharacterId id, int faction) =>
            $"{(table.IsAlive(id) ? table.Cold(id.Slot)!.Name : "?")} ({(popData is { } d && faction >= 0 && faction < d.Factions.Count ? d.Factions[faction].Name : "no faction")})";
        foreach (var e in aggro.TakeLog())
            Console.WriteLine($"aggro     tick {e.Tick}: {Who(e.Attacker, e.AttackerFaction)} attacks {Who(e.Target, e.TargetFaction)} at {e.Distance:0}");
    }
}
