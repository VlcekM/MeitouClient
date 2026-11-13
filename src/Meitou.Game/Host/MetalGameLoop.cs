#if MEITOU_METAL
// Metal exists on macOS only; the file is compiled only there (the csproj's MEITOU_METAL constant).
#pragma warning disable CA1416
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime;
using Meitou.Data.Save;
using Meitou.Data.Textures;
using Meitou.Data.World;
using Meitou.Game.DevTools;
using Meitou.Game.Editor;
using Meitou.Game.Host.Cameras;
using Meitou.Game.Host.Input;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Display;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Metal;
using Meitou.Game.Rendering.Metal.Gi;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Shadows;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;
using Meitou.Game.Rendering.Upscalers;
using Meitou.Game.Rendering.Water;
using Meitou.Game.Settings;
using Meitou.Game.Title;
using Meitou.Game.Tutorials;
using Meitou.Game.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Saving;
using Meitou.Simulation.Session;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using EngineKey = Meitou.Game.Host.Input.Key;
using SilkKey = Silk.NET.Input.Key;

namespace Meitou.Game.Host;

/// <summary>
/// The Metal path (macOS): the game boots without the Vulkan renderer and draws the world through the seam's recording model (native segments around
/// the scene's host pass, guests inside it, the frame committed with the present). The loop is interactive: the strategy camera and the HUD's
/// selection/orders read the window's input, while the frontend, audio and the remaining host services wait (docs/metal-backend.md).
/// </summary>
sealed class MetalGameLoop(GameHost host, Data.Gameplay.NewGameStart? start)
{
    #region Run and window

    /// <summary>
    /// The window's display: its drawable, vsync and full-screen presentation.
    /// </summary>
    MetalDisplay _display = null!;

    /// <summary>
    /// The Metal device, its queue and the frame being recorded.
    /// </summary>
    MetalContext _ctx = null!;

    /// <summary>
    /// The display's window.
    /// </summary>
    IWindow _window = null!;

    /// <summary>
    /// The frame cap without vsync (0: none), changed live by the settings' Frame limit.
    /// </summary>
    int _fpsLimit;

    /// <summary>
    /// The GPU allocation the memory guard keeps the caches under.
    /// </summary>
    ulong _memoryBudget;

    /// <summary>
    /// The memory guard is on (MEITOU_VRAM_GUARD is not 0).
    /// </summary>
    bool _guardEnabled;

    /// <summary>
    /// The shared renderers' memory guard (null when off).
    /// </summary>
    VramGuard? _memoryGuard;

    /// <summary>
    /// The play benchmark's hooks in this loop (inert without <c>--play-bench</c>).
    /// </summary>
    MetalPlayBench _bench = null!;

    /// <summary>
    /// MEITOU_METAL_NO_TAA: the diagnostic native view without jitter or temporal resolve.
    /// </summary>
    bool _noTaaRequested;

    /// <summary>
    /// <c>--upscaler metalfx</c> or MEITOU_METALFX asked for MetalFX temporal.
    /// </summary>
    bool _metalFxRequested;

    /// <summary>
    /// Foliage is drawn (MEITOU_METAL_FOLIAGE is not 0).
    /// </summary>
    bool _metalFoliage;

    /// <summary>
    /// Cloud shadows may be on (MEITOU_METAL_CLOUD_SHADOWS is not 0).
    /// </summary>
    bool _metalCloudShadows;

    /// <summary>
    /// Water is drawn (MEITOU_METAL_WATER is not 0).
    /// </summary>
    bool _metalWater;

    /// <summary>
    /// Shadows may be on (MEITOU_METAL_SHADOWS is not 0).
    /// </summary>
    bool _metalShadows;

    /// <summary>
    /// Far impostors may be on (MEITOU_METAL_NO_IMPOSTORS is not 1).
    /// </summary>
    bool _metalImpostors;

    /// <summary>
    /// The final composite through the game's tone map.
    /// </summary>
    PostComposite _composite = null!;

    /// <summary>
    /// The temporal resolve.
    /// </summary>
    PostTaa _taa = null!;

    /// <summary>
    /// MetalFX temporal, when the device has it.
    /// </summary>
    MetalFxUpscaler? _metalFx;

    /// <summary>
    /// MetalFX spatial, when the device has it.
    /// </summary>
    MetalFxSpatialUpscaler? _metalFxSpatial;

    /// <summary>
    /// The upscaler mode and render scale the Display page sets.
    /// </summary>
    MetalUpscaling _upscaling = null!;

    /// <summary>
    /// The post chain the shared frame draw runs.
    /// </summary>
    MetalPostChain _postChain = null!;

    /// <summary>
    /// MEITOU_METAL_SETTINGS: a still opens the settings dialog.
    /// </summary>
    bool _openSettingsForStill;

    /// <summary>
    /// The text and quad painter of the HUD and the frame-stats lines (null when it could not be made).
    /// </summary>
    DebugOverlay? _overlay;

    /// <summary>
    /// The frame-rate lines start shown (not in a still unless MEITOU_METAL_OVERLAY=1).
    /// </summary>
    bool _showFrameStats;

    /// <summary>
    /// The world's files read on a worker since <see cref="OpenDisplay"/>, when the host owns the scene (an interactive run; a still's is given).
    /// </summary>
    Task<WorldScene?>? _sceneRead;

    /// <summary>
    /// Opens the window and the device, boots the world, the simulation and the interface, then draws frames until the window closes or the game
    /// quits.
    /// </summary>
    internal int Run()
    {
        using var display = OpenDisplay();
        using var ctx = OpenContext();
        SetUpMemoryAndWindow();

        ReadSwitches();
        CreatePostChain();
        _bench = new MetalPlayBench(host, ctx, BenchDescription);
        ShowWindow();
        if (LoadWorld() is { } exit) return exit;

        // The audio device is up before the first frame; the title theme starts here when the menu is open.
        host.Sound.AttachAudio();

        // The real input path: the window's keys and pointer go to PlayerInterface first (its panels, builds and the world's
        // selection/order clicks), then to the session's bindings so the strategy camera pans, turns and zooms.
        using var input = OpenInput();
        ConnectClipboard();
        _frameStatsVisible = _showFrameStats;
        HookInput();

        PrepareStills();
        RunLoop();

        Shutdown();
        return 0;
    }

    /// <summary>
    /// The window and its display, with the run's vsync and frame cap; the world's files start on a worker meanwhile.
    /// </summary>
    MetalDisplay OpenDisplay()
    {
        // Same setting as VulkanDisplay: Metal used to always present with display sync, even under --no-vsync.
        bool vsync = host.GameOptions.VSync ?? host.Config.VSync;
        _fpsLimit = host.GameOptions.FpsLimit ?? host.Config.FpsLimit;
        _sceneRead = host.LoadScene is { } readScene ? Task.Run(readScene) : null;
        return _display = new MetalDisplay(WindowOptions.Default with
        {
            Size = new Vector2D<int>(host.WorldOptions.Width, host.WorldOptions.Height),
            Title = "Meitou (metal)",
            // Hidden until the first loading frame is in it: shown at once, macOS showed an empty window and took that into the full-screen Space.
            IsVisible = false,
            FramesPerSecond = 0,
            UpdatesPerSecond = 0,
        }, vsync);
    }

    /// <summary>
    /// The Metal device on the display.
    /// </summary>
    MetalContext OpenContext()
    {
        Console.WriteLine($"metal     vsync {(_display.VSync ? "on" : "off")}, frame cap {(_fpsLimit > 0 ? $"{_fpsLimit} fps" : "off")}");
        return _ctx = new MetalContext(_display);
    }

    /// <summary>
    /// The memory guard's budget, the window and the texture filtering.
    /// </summary>
    void SetUpMemoryAndWindow()
    {
        // The Metal path has no single GPU allocator with a global budget: every cache has its own high-water mark.
        // Travelling through the world can fill the object, foliage and character caches at the same time, so keep the
        // whole device allocation under a share of the Mac's memory (MetalMemoryBudget). The guard pauses/evicts caches
        // and shortens the ranges as the ceiling approaches, exactly as it does on Vulkan.
        ulong defaultMb = MetalMemoryBudget.DefaultMb((ulong)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, _ctx.VideoMemoryBudget);
        _memoryBudget = defaultMb << 20;
        ulong budgetMb = RequestedBudgetMb();
        if (budgetMb > 0) _memoryBudget = budgetMb << 20;
        _guardEnabled = Environment.GetEnvironmentVariable("MEITOU_VRAM_GUARD") != "0";
        if (_guardEnabled) Console.WriteLine($"metal     memory guard: {_memoryBudget / 1048576} MB of GPU allocation");
        if (budgetMb > defaultMb)
        {
            Console.WriteLine($"warning   metal GPU budget {budgetMb} MB is above this Mac's {defaultMb} MB default; full screen and a busy world can "
                + "exhaust its memory. Leave the default unless profiling.");
        }
        _window = _display.Window;

        _ctx.Samplers.MaxAnisotropy = host.WorldOptions.Anisotropy;
    }

    /// <summary>
    /// The memory guard's ceiling asked for, in MB: <c>--metal-gpu-budget</c>, else <c>MEITOU_METAL_GPU_BUDGET_MB</c> (the launcher's setting); 0 for
    /// the default (<see cref="MetalMemoryBudget.DefaultMb"/>).
    /// </summary>
    ulong RequestedBudgetMb()
    {
        if (host.GameOptions.MetalGpuBudgetMb is { } option) return option;

        string? variable = Environment.GetEnvironmentVariable("MEITOU_METAL_GPU_BUDGET_MB");
        return ulong.TryParse(variable, NumberStyles.None, CultureInfo.InvariantCulture, out ulong mb) ? mb : 0;
    }

    /// <summary>
    /// The play benchmark's run line: the device, the scene and window sizes, the upscaler, the pacing, the GPU budget and the memory guard.
    /// </summary>
    string BenchDescription()
    {
        var window = _window.FramebufferSize;
        string pacing = _display.VSync
            ? "vsync on"
            : _fpsLimit > 0
                ? $"vsync off, frame cap {_fpsLimit}"
                : "frame limit and vsync off";
        string guard = _memoryGuard?.Status ?? "guard off";
        string scene = $"scene {_ctx.SceneWidth}x{_ctx.SceneHeight} in {window.X}x{window.Y}";
        return string.Create(CultureInfo.InvariantCulture, $"Metal {_ctx.Device.Name}, {scene}, {_upscaling.UpscalerStatusText()}; {pacing}; "
            + $"GPU budget {_memoryBudget >> 20} MB ({guard} at the end)");
    }

    /// <summary>
    /// Switches native macOS full screen.
    /// </summary>
    void ToggleFullScreen() => _display.SetFullScreen(!_display.FullScreen);

    /// <summary>
    /// The upscaler requests and the diagnostic opt-outs of the environment.
    /// </summary>
    void ReadSwitches()
    {
        // Same scale as the Vulkan path's TAA/upscaler setting: the scene is rendered smaller and an upscaler reconstructs it.
        // The Metal default is MetalFX Balanced at 0.50; Native at 1.00 is the fallback when MetalFX is unavailable.
        // MEITOU_METAL_NO_TAA is a diagnostic view: native size, no jitter and no temporal resolve.
        _noTaaRequested = Environment.GetEnvironmentVariable("MEITOU_METAL_NO_TAA") is "1" or "true";
        // `--upscaler metalfx` is the command-line form; MEITOU_METALFX stays usable for launch scripts.
        _metalFxRequested = !_noTaaRequested && (host.WorldOptions.Post.Upscale.Kind == UpscalerKind.MetalFx ||
            Environment.GetEnvironmentVariable("MEITOU_METALFX") is "1" or "true");

        // Safe Metal defaults while the foliage mesh path and the cloud-shadow path are being fixed: both are opt-in.
        _metalFoliage = Environment.GetEnvironmentVariable("MEITOU_METAL_FOLIAGE") != "0";
        _metalCloudShadows = Environment.GetEnvironmentVariable("MEITOU_METAL_CLOUD_SHADOWS") != "0";
        _metalWater = Environment.GetEnvironmentVariable("MEITOU_METAL_WATER") != "0";
        _metalShadows = Environment.GetEnvironmentVariable("MEITOU_METAL_SHADOWS") != "0";
        _metalImpostors = Environment.GetEnvironmentVariable("MEITOU_METAL_NO_IMPOSTORS") != "1";
        if (!_metalFoliage) Console.WriteLine("metal     foliage disabled (MEITOU_METAL_FOLIAGE=0)");
        if (!_metalCloudShadows) Console.WriteLine("metal     cloud shadows disabled (MEITOU_METAL_CLOUD_SHADOWS=0)");
        if (!_metalWater) Console.WriteLine("metal     water disabled (MEITOU_METAL_WATER=0)");
        if (!_metalShadows) Console.WriteLine("metal     shadows disabled (MEITOU_METAL_SHADOWS=0)");
        if (!_metalImpostors) Console.WriteLine("metal     impostors disabled (MEITOU_METAL_NO_IMPOSTORS=1)");
    }

    /// <summary>
    /// The composite, the temporal resolve and the MetalFX upscalers, and the post chain the shared frame draw runs.
    /// </summary>
    void CreatePostChain()
    {
        // The world's renderers are built by the shared WorldFrame path after the display/upscaler setup. Metal's post chain
        // (PostComposite/PostTaa and the MetalFX adapters) sits behind IWorldPostChain, so WorldFrame.Draw owns the frame.
        // The final composite through the game's tone map: the scene draws into an HDR target, then this writes the LDR picture.
        _composite = new PostComposite(_ctx, flipY: true);
        _composite.LoadHeatHaze(host.Assets);
        _taa = new PostTaa(_ctx);
        // MetalFX temporal is the default at every preset scale; spatial is an explicit alternative.
        _metalFx = MetalFxUpscaler.TryCreate(_ctx);
        _metalFxSpatial = MetalFxSpatialUpscaler.TryCreate(_ctx);
        MetalUpscalerMode mode = ChooseUpscalerMode();

        _upscaling = new MetalUpscaling(host.Config, _ctx, _taa, _metalFx, _metalFxSpatial, () => _display.FullScreen, BackingScale);
        _upscaling.ApplyMode(mode);
        if (_upscaling.NoTaa) Console.WriteLine("metal     no TAA/upscaler: no jitter, no temporal resolve");
        // The seam's post chain for the shared WorldFrame.Draw: the scene effects and the composite are the same calls the local
        // loop used, with the jitter and temporal choice decided live as the settings change them.
        _postChain = new MetalPostChain(_ctx, _composite, _taa, host.WorldOptions.Post,
            noTaa: () => _upscaling.NoTaa,
            temporal: () => _upscaling.Mode is MetalUpscalerMode.Temporal or MetalUpscalerMode.Taa,
            jitter: (frame, sceneWidth, displayWidth) => _upscaling.NoTaa || _upscaling.Spatial
                ? Vector2.Zero
                : PostTaa.Offset(frame, sceneWidth, displayWidth));
    }

    /// <summary>
    /// The upscaler mode and render scale from the settings, the command line and the environment, as far as the device supports them.
    /// </summary>
    MetalUpscalerMode ChooseUpscalerMode()
    {
        MetalUpscalerMode mode = host.Config.MetalUseUpscaling ? host.Config.MetalUpscaler : MetalUpscalerMode.Disabled;
        float scale = Math.Clamp(host.Config.MetalRenderScale, 0.33f, 1f);
        if (_noTaaRequested)
        {
            mode = MetalUpscalerMode.Disabled;
            scale = 1f;
        }
        else if (Environment.GetEnvironmentVariable("MEITOU_METALFX_SPATIAL") is "1" or "true")
        {
            mode = MetalUpscalerMode.Spatial;
            scale = 0.4f;
        }
        else if (_metalFxRequested)
        {
            mode = MetalUpscalerMode.Temporal;
            // --upscaler metalfx without a scale means the balanced default; an explicit --render-scale wins.
            scale = host.WorldOptions.Post.Upscale.Scale is null ? 0.5f : host.WorldOptions.Post.Upscale.EffectiveScale;
        }
        else if (host.WorldOptions.Post.Upscale.Explicit)
            scale = host.WorldOptions.Post.Upscale.EffectiveScale;

        if (mode == MetalUpscalerMode.Temporal && _metalFx is null)
        {
            Console.WriteLine("metal     MetalFX temporal unavailable; using no upscaler");
            mode = MetalUpscalerMode.Disabled;
            scale = 1f;
        }
        if (mode == MetalUpscalerMode.Spatial && _metalFxSpatial is null)
        {
            Console.WriteLine("metal     MetalFX spatial unavailable; using " + (_metalFx is null ? "no upscaler" : "MetalFX temporal"));
            mode = _metalFx is null ? MetalUpscalerMode.Disabled : MetalUpscalerMode.Temporal;
            scale = mode == MetalUpscalerMode.Disabled ? 1f : scale;
        }
        if (mode == MetalUpscalerMode.Disabled) scale = 1f;
        // 0.33 is below what MetalFX temporal can run reliably on this device (it crashed before the first frame);
        // the lowest shipped MetalFX tier is 0.40.
        if (mode is MetalUpscalerMode.Temporal or MetalUpscalerMode.Spatial) scale = Math.Clamp(scale, 0.4f, 1f);
        else scale = Math.Clamp(scale, 0.33f, 1f);
        _ctx.RenderScale = scale;
        return mode;
    }

    /// <summary>
    /// The overlay painter and the loading screen, then the window brought forward with the first loading frame in it, in place of the splash, and
    /// taken into full screen.
    /// </summary>
    void ShowWindow()
    {
        _openSettingsForStill = Environment.GetEnvironmentVariable("MEITOU_METAL_SETTINGS") is "1" or "true";
        Console.WriteLine($"metal     upscaler {_upscaling.Mode.ToString().ToLowerInvariant()} at {_ctx.RenderScale:0.00}");
        // The text/quad painter, shared by the HUD and the optional frame-stats lines. It is created even for a screenshot:
        // the game's HUD is part of the picture. MEITOU_METAL_OVERLAY=1 adds the stats lines to a still.
        _overlay = DebugOverlay.TryCreate(_ctx);
        _showFrameStats = host.WorldOptions.Screenshot is null || Environment.GetEnvironmentVariable("MEITOU_METAL_OVERLAY") == "1";
        CreateLoadingScreen();

        // Shown before the first loading frame (a still draws none): the layer of a window not shown yet gives no drawable, nor does it for the
        // first frame right after showing; the next one does (Observed 2026-11-12, Apple M6, macOS 27.2). The window's black background fills
        // that moment. It is on screen before it goes full screen: AppKit ignores the request for a hidden window, and animates the Space from
        // what it shows.
        _window.IsVisible = true;
        _display.Activate();
        host.LoadingFrame();
        EnterFullScreen();
        host.Splash?.Dispose();
    }

    /// <summary>
    /// The shared window setting: native macOS full screen (a Space of its own); --windowed keeps the window for one run.
    /// </summary>
    void EnterFullScreen()
    {
        if (!host.Config.FullScreen || host.WorldOptions.Windowed) return;
        ToggleFullScreen();
        AwaitFullScreen();
    }

    /// <summary>
    /// Loading frames until the window is in its Space: AppKit runs the animation only while the window's events are handled, and a load step
    /// holding the thread meanwhile left the Space showing the window's last picture. Not for a still, which draws no loading frames.
    /// </summary>
    void AwaitFullScreen()
    {
        if (host.WorldOptions.Screenshot is not null || host.Loading is null) return;
        var watch = Stopwatch.StartNew();
        while (_display.FullScreenTransitioning && host.LoadingFrame()) Thread.Sleep(5);
        Console.WriteLine($"window    full-screen transition over after {watch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// The scene and the boot under the loading screen; the exit code when the run ends here (the window closed, the scene failed), else null.
    /// </summary>
    int? LoadWorld()
    {
        try
        {
            if (!ReadScene()) return 1;
            BootWorld();
            BootInterface();
            EndStartLoading();
            return null;
        }
        catch (GameHost.LoadingCancelledException)
        {
            Console.WriteLine("loading   cancelled: the window was closed");
            host.EndLoadingScreen();
            _ctx.Finish();
            return 0;
        }
    }

    /// <summary>
    /// The world's files from <see cref="_sceneRead"/> as the host's scene; false when they could not be read.
    /// </summary>
    bool ReadScene()
    {
        if (_sceneRead is null) return true;
        host.LoadingStep("scene");
        if (host.AwaitLoading(_sceneRead) is not { } loaded)
        {
            Console.Error.WriteLine("metal     the world could not be read");
            return false;
        }
        host.Scene = host.OwnedScene = loaded;
        return true;
    }

    /// <summary>
    /// The world's renderers, the simulation and its session, the start's squad and navmesh, and the characters.
    /// </summary>
    void BootWorld()
    {
        CreateWorldGpu();
        CreateGi();
        UnpackRenderers();
        ConfigureFoliage();
        KeepRenderers();

        BootWorldSession();
        StartPlayerSquad();
        LoadZonesAroundStart();
        host.ApplyCamera(host.Session.Camera.Current);
        host.SendFocus();
        host.LoadingStep("characters");
        CreateCharacters();

        // The squad tour and character flights (docs/viewer.md "Squad tour"). Metal has no cinema, so a held camera is
        // only the tour's; EndTour hands the view back to the rig as the Vulkan host does.
        _tour = new SquadTour();
    }

    /// <summary>
    /// The interface, the settings, the menus' actions and a --load save, then the view settled round the start's camera.
    /// </summary>
    void BootInterface()
    {
        // The game's HUD and panels. PlayerInterface is the interface's data/layout owner; ArrangeUi/BindUi and the UiPainter
        // work with the session the loop has. Only the panels' pointer/keyboard controls are wired here so far.
        host.LoadingStep("finishing");
        if (_overlay is not null) CreateInterface(_overlay);
        PlaceStartCamera();
        ApplyUserConfig();

        // The shared settings layout: Metal supplies the settings it can change live (window, upscaler, distances,
        // shadows, sky and the composite); the Interface/Controls/Advanced/Audio pages are the game's own.
        if (_overlay is not null && _hud is not null) CreateSettingsMenu(_hud);
        ConnectScreenActions();
        OpenStartSave();

        host.LoadingStep("view");
        SettleStart();
    }

    /// <summary>
    /// The start has loaded: no more loading frames, and the screen slides away over the world's first frames (Metal measures no warm-up).
    /// </summary>
    void EndStartLoading()
    {
        host.EndLoadingFrames();
        host.BeginWarm();
    }

    /// <summary>
    /// Saves the settings, lets the queue finish and releases the interface, the world's renderers and the post chain.
    /// </summary>
    void Shutdown()
    {
        _bench.Finish(_window.FramebufferSize);

        // Let the queue finish before the renderers release views, buffers and bindless entries; a quit during a
        // streaming upload otherwise freed resources the GPU was still reading and could fault on the way out.
        SaveSettings();
        host.Loading?.Dispose();
        host.Loading = null;
        _ctx.Finish();
        _hud?.DisposeUi();
        _overlay?.Dispose();
        // The shared world owns terrain (and its textures), sky, weather surfaces, particles, lamps, objects, foliage,
        // water, characters, interior masks, streamer and shadow. Metal's post chain and the picture target are local.
        host.Gpu?.Dispose();
        _postChain.Dispose();
        _composite.Dispose();
        _taa.Dispose();
        _metalFx?.Dispose();           // PostTaa owns it while selected; safe to dispose again after a preset switch
        _metalFxSpatial?.Dispose();
        double elapsed = _clock.Elapsed.TotalSeconds;
        Console.WriteLine($"metal     {_frames} frames in {elapsed:0.0} s: {_frames / Math.Max(elapsed, 1e-3):0} fps, " +
            $"camera {host.Camera.Target.X:0}, {host.Camera.Target.Z:0}, yaw {host.Camera.Yaw * 180 / MathF.PI:0}, " +
            $"pitch {host.Camera.Pitch * 180 / MathF.PI:0}, boom {host.Camera.Distance:0}");
    }

    #endregion

    #region World

    /// <summary>
    /// The world's renderers from the shared construction path.
    /// </summary>
    WorldGpu _worldGpu = null!;

    /// <summary>
    /// The sky.
    /// </summary>
    SkyRenderer _sky = null!;

    /// <summary>
    /// The weather surfaces.
    /// </summary>
    WorldWeather? _weather;

    /// <summary>
    /// The fog volumes.
    /// </summary>
    FogVolumes _fogVolumes = null!;

    /// <summary>
    /// The shadow pass.
    /// </summary>
    ShadowPass _shadow = null!;

    /// <summary>
    /// The terrain streamer.
    /// </summary>
    TerrainStreamer? _streamer;

    /// <summary>
    /// The buildings and props.
    /// </summary>
    WorldObjectRenderer? _objects;

    /// <summary>
    /// The foliage (null when MEITOU_METAL_FOLIAGE=0).
    /// </summary>
    FoliageRenderer? _foliage;

    /// <summary>
    /// The water (null when MEITOU_METAL_WATER=0).
    /// </summary>
    WaterRenderer? _water;

    /// <summary>
    /// The lamps.
    /// </summary>
    WorldLamps? _lamps;

    /// <summary>
    /// The simulation's CPU height grid, the one the shared renderer uses.
    /// </summary>
    Meitou.Data.World.GroundHeights _heights = null!;

    /// <summary>
    /// Where the camera starts: the focus, the new game's leader or the test mode's spot.
    /// </summary>
    Vector3 _startTarget;

    /// <summary>
    /// The characters (null without a database or with --no-population).
    /// </summary>
    CharacterRenderer? _characters;

    /// <summary>
    /// The world's renderers through the shared construction path, and the combat aftermath's.
    /// </summary>
    void CreateWorldGpu()
    {
        // The world's renderers through the shared construction path: terrain, textures, streamer, sky and weather,
        // particles, lamps, objects, foliage, faithful water, shadows and the memory guard, with a loading frame between the steps and while
        // the workers' files and bakes are awaited.
        // --crowd means simulated characters (SpawnCrowd) here too; the posed harness is only for a renderer test.
        host.Sandbox.Crowd = host.WorldOptions.Crowd;
        host.WorldOptions.Crowd = 0;
        bool terrainTexturesOff = Environment.GetEnvironmentVariable("MEITOU_METAL_NO_TERRAIN_TEXTURES") == "1";
        if (terrainTexturesOff) Console.WriteLine("metal     terrain textures disabled (MEITOU_METAL_NO_TERRAIN_TEXTURES=1)");
        bool metalParticles = Environment.GetEnvironmentVariable("MEITOU_METAL_NO_PARTICLES") != "1";
        // Upstream ground relief is on by default on Metal (validated with the 2026-10-24 run); the
        // experimental GPU cull, not the relief, caused the earlier progress timeouts.
        // MEITOU_METAL_GROUND_RELIEF=0 disables it for an A/B.
        bool metalGroundRelief = Environment.GetEnvironmentVariable("MEITOU_METAL_GROUND_RELIEF") is not ("0" or "false");
        GroundRelief.Enabled = metalGroundRelief;
        GroundRelief.ParallaxActive &= metalGroundRelief;   // keeps --faithful parallax
        Console.WriteLine(metalGroundRelief
            ? "metal     ground relief on (MEITOU_METAL_GROUND_RELIEF=0 disables it)"
            : "metal     ground relief off (MEITOU_METAL_GROUND_RELIEF=0)");
        _worldGpu = WorldFrame.CreateGpu(_ctx, host.Install, host.Scene, host.Assets, host.WorldOptions, interactive: true,
            step: host.LoadingStep, wait: host.AwaitLoading,
            terrainTextures: !terrainTexturesOff, particlesEnabled: metalParticles, objectLoadBudget: 0,
            vramSample: _guardEnabled ? () => ((ulong)_ctx.Device.CurrentAllocatedSize, _memoryBudget) : null);
        _worldGpu.PostChain = _postChain;
        host.Gpu = _worldGpu;
        // The combat aftermath's renderers and store exist before a loaded save's decals are read and before the first frame.
        host.BloodDecals.SetUpBlood(_ctx);
    }

    /// <summary>
    /// The experimental native probe GI, when asked for and the device ray traces.
    /// </summary>
    void CreateGi()
    {
        // Native Metal GI is experimental and opt-in: on this M1 Pro it could freeze the machine during a save load.
        // Keep the unfinished path out of normal launches; --no-gi still wins, and F10 can turn it off after it is on.
        bool metalGi = host.WorldOptions.Gi && Environment.GetEnvironmentVariable("MEITOU_METAL_GI") is "1" or "true";
        if (metalGi && _ctx.Device.SupportsRaytracing)
        {
            _worldGpu.ProbeGi = new MetalProbeGi(_ctx) { Enabled = host.WorldOptions.GiProbes, InteriorEnabled = host.WorldOptions.InteriorGi };
            Console.WriteLine($"gi        {_worldGpu.ProbeGi.Describe()} (experimental, MEITOU_METAL_GI=1)");
        }
        else if (metalGi)
            Console.WriteLine("gi        metal off: the device does not support ray tracing");
        else if (host.WorldOptions.Gi)
            Console.WriteLine("gi        metal off: experimental (set MEITOU_METAL_GI=1 to enable)");
    }

    /// <summary>
    /// The renderers the loop and the settings use, with the diagnostic opt-outs applied.
    /// </summary>
    void UnpackRenderers()
    {
        _memoryGuard = _worldGpu.Guard;
        _sky = _worldGpu.Sky;
        _weather = _worldGpu.Weather;
        _fogVolumes = _worldGpu.FogVolumes!;
        _shadow = _worldGpu.Shadow!;
        _streamer = _worldGpu.Streamer;
        _objects = _worldGpu.Objects;
        _foliage = _worldGpu.Foliage;
        _water = _worldGpu.Water;
        _lamps = _worldGpu.Lamps;
        // The diagnostic opt-outs keep their old meaning now that the shared path owns construction.
        if (!_metalFoliage)
        {
            _foliage?.Dispose();
            _worldGpu.Foliage = null;
            _foliage = null;
        }
        if (!_metalWater)
        {
            _water?.Dispose();
            _worldGpu.Water = null;
            _water = null;
        }
        if (!_metalShadows) _shadow.Enabled = false;
        if (!_metalCloudShadows) _sky.CloudShadows = false;
        if (!_metalImpostors) _foliage?.DisableImpostors();
    }

    /// <summary>
    /// The foliage's still sway, its grass and mesh switches and its cull.
    /// </summary>
    void ConfigureFoliage()
    {
        if (_foliage is not null)
        {
            _foliage.SwaySeconds = 0;   // a still picture: the grass holds still, so it repeats
            _foliage.DrawGrass = Environment.GetEnvironmentVariable("MEITOU_METAL_NO_GRASS") != "1";
            _foliage.DrawMeshes = Environment.GetEnvironmentVariable("MEITOU_METAL_NO_FOLIAGE_MESHES") != "1";
            // The CPU cull is the reference path (and the A/B diagnostic for the Metal GPU cull). The GPU cull is
            // opt-in on Metal after the 2026-10-23 progress timeouts; `MEITOU_METAL_CPU_CULL=1` still forces CPU.
            bool gpuCullWanted = Environment.GetEnvironmentVariable("MEITOU_METAL_GPU_CULL") is "1" or "true";
            _foliage.GpuCull = gpuCullWanted && Environment.GetEnvironmentVariable("MEITOU_METAL_CPU_CULL") != "1";
            Console.WriteLine(_foliage.GpuCull
                ? "warning   metal foliage GPU cull is experimental (MEITOU_METAL_GPU_CULL=1): it has produced GPU progress timeouts "
                    + "after a few minutes"
                : "metal     foliage CPU cull (GPU cull off by default; MEITOU_METAL_GPU_CULL=1 enables the experimental path)");
        }
    }

    /// <summary>
    /// The buildings' interior masks, and the renderers kept as the host's own.
    /// </summary>
    void KeepRenderers()
    {
        // The buildings' interior masks (docs/render-interior-cut.md): the depth range the terrain, foliage and map features are cut by
        // while a viewed building is open. The renderer is backend-neutral; Metal draws it into its own target before the scene.
        InteriorMaskRenderer? interiorMask = _objects is null ? null : new InteriorMaskRenderer(_ctx, _objects);
        _worldGpu.InteriorMask = interiorMask;
        host.KeepMetalRenderers(_weather, _lamps, _objects, _foliage);
    }

    /// <summary>
    /// The simulation on the renderer's height grid, the camera and the world session.
    /// </summary>
    void BootWorldSession()
    {
        // The simulation's side, using the same CPU height grid as the shared renderer.
        _heights = new Meitou.Data.World.GroundHeights(host.Scene.Window, host.Scene.Coarse, host.Scene.CoarseSize, WorldFrame.CoarseStep);
        host.KeepMetalGround(_heights.HeightAt);

        // On a worker (it makes no GPU resource), the loading screen drawn meanwhile.
        var heights = _heights;
        host.LoadingStep("people");
        var (walkability, systems, constants) = host.AwaitLoading(Task.Run(() => host.BootSimulation(heights, interactive: true)));

        host.LoadingStep("world");
        (host.Camera, host.Render) = WorldFrame.Setup(host.Scene, host.WorldOptions);
        // Terrain shader A/B: the no-normal program is a compile-time variant, so the option must be fixed before the first draw.
        if (Environment.GetEnvironmentVariable("MEITOU_TERRAIN_NO_NORMAL") is "1" or "true")
        {
            host.Render.NormalMaps = false;
            Console.WriteLine("metal     terrain normal maps off (MEITOU_TERRAIN_NO_NORMAL=1)");
        }
        host.Session = new WorldSession(host.Scene.Focus, (host.Scene.X0, host.Scene.Z0, host.Scene.X1, host.Scene.Z1), _heights.HeightAt,
            host.WorldOptions.Hour, host.Config.TickRate,
            clock: GameHost.GameClockFor(
                constants ??= host.Scene.Database is { } clockDb ? Meitou.Data.Gameplay.GameConstants.FromDatabase(clockDb) : null,
                host.WorldOptions.Hour, host.WorldOptions.Day ?? GameClock.DefaultStartDay),
            simulation: host.Duel.WorldSettings = new Meitou.Simulation.WorldSettings
            {
                Seed = host.GameOptions.Seed,
                MinPartitionSize = 128,
                Threads = Math.Max(1, host.Config.SimThreads ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 8)),
                FarTickPeriod = GameHost.FarTickPeriod,
            },
            systems: systems, walkability: walkability);
        host.Session.World.BenchFacingFix = host.Config.FixBenchFacing;
        host.Session.World.TakeIntoBackpack = host.Config.TakeIntoBackpack;
        host.Session.Simulation.SetFastSpeeds(host.Config.FastSpeed, host.Config.FastestSpeed);
        host.Render.Grid = host.Sandbox.IsSandbox;
        host.Session.Camera.Place(new Vector2((float)(host.WorldOptions.X ?? host.Scene.Focus.X), (float)(host.WorldOptions.Z ?? host.Scene.Focus.Z)),
            (float)(host.WorldOptions.Yaw ?? 30) * MathF.PI / 180,
            (float)(host.WorldOptions.Pitch ?? Meitou.Data.World.KenshiCamera.InitialPitchDegrees) * MathF.PI / 180,
            (float)(host.WorldOptions.Distance ?? Meitou.Data.World.KenshiCamera.InitialDistance));
        _startTarget = host.Session.Camera.Current.Target;
    }

    /// <summary>
    /// The saved key bindings, then the new game's squad.
    /// </summary>
    void StartPlayerSquad()
    {
        // The saved key bindings feed the same settings dialog and camera/order input as Vulkan.
        GameHost.UseSavedBindings(host.Session.Bindings, host.Install, host.Config);
        // The game's new-game shortcut: the player's squad in the simulation, which the world draw list then shows.
        if (start is not null && host.Population is not null)
        {
            try
            {
                host.PlayerSquad = host.Population.StartPlayer(host.Session.World, start);
                var lead = host.Session.World.Characters.Previous[host.PlayerSquad.Leader.Slot].Position;
                _startTarget = lead;
                Console.WriteLine($"metal     player squad: {host.PlayerSquad.Members.Count} characters, leader at {lead.X:0}, {lead.Z:0}");
                // --new-game is a shortcut past the menus: the rolled characters are kept (the menu's New game opens the editor);
                // a still keeps creation open for the showcase's editor pictures.
                if (host.WorldOptions.Screenshot is null)
                    host.Session.World.Commands.Enqueue(new Meitou.Simulation.Editing.FinishCreation { Tick = host.Session.World.Tick });
            }
            catch (InvalidOperationException e)
            {
                Console.WriteLine($"metal     player squad skipped: {e.Message}");
            }
        }
    }

    /// <summary>
    /// The navmesh zones round the start, loaded before the first frame.
    /// </summary>
    void LoadZonesAroundStart()
    {
        // The population's zones: the background towns activate the zones round the player, and a walker's path may cross one
        // that has to be built first. The interactive loop keeps this table current (UpdateNav), as the Vulkan host does.
        if (host.Nav is not null && host.Population is not null)
        {
            host.Population.ZoneGate = zone => host.Nav.Walkability.World.Contains(zone);
            var around = host.PlayerSquad is not null
                ? host.Session.World.Characters.Previous[host.PlayerSquad.Leader.Slot].Position
                : host.Session.Focus;
            var zones = Meitou.Simulation.Population.ZoneActivation.ZonesAround(around, 0);
            var watch = Stopwatch.StartNew();
            host.LoadingStep("navmesh");
            host.AwaitLoading(Task.WhenAll(zones.Select(z => (Task)host.Nav.LoadZone(z))));
            foreach (var z in zones) host.NavZones[z] = Stopwatch.GetTimestamp();
            Console.WriteLine($"metal     navmesh {zones.Count} zone(s) round the start ready in {watch.ElapsedMilliseconds} ms");
        }
    }

    /// <summary>
    /// The character renderer: the --crowd harness the shared path made, else one fed by the simulation's draw list.
    /// </summary>
    void CreateCharacters()
    {
        // Characters: WorldFrame.CreateGpu already made the `--crowd` harness; otherwise the live loop supplies the
        // simulation's snapshot list. The player interface is not booted here, so the source has no editor previews.
        _characters = _worldGpu.Characters;
        if (_characters is not null) Console.WriteLine($"metal     crowd ready ({_characters.Assets} appearances)");
        else if (host.Scene.Database is { } characterDb && !host.GameOptions.NoPopulation)
        {
            _characters = new CharacterRenderer(_ctx, host.Install, characterDb, host.Assets, host.CharacterRigs)
            {
                DrawDistance = GameplaySettings.NpcRange(host.Install),
                Source = host.FillWorldDrawList, SettleFirst = true, GeneratedLod = host.WorldOptions.CharacterLod,
                SkinShading = host.WorldOptions.SkinShading, SkinRaces = host.Config.SkinRaces, EnhancedIcons = host.WorldOptions.MeitouIcons,
                PoseClock = () => host.RealTime.Elapsed.TotalSeconds,
            };
            _worldGpu.Characters = _characters;
            Console.WriteLine("metal     characters ready (population draw list)");
        }
        host.KeepMetalCharacters(_characters);
        if (_characters is not null)
        {
            _characters.Guard = _memoryGuard;
            _characters.InteriorObjects = _objects;
        }
    }

    /// <summary>
    /// The content test modes, then the camera at the start and the new game's follow.
    /// </summary>
    void PlaceStartCamera()
    {
        // The content test modes, in the Vulkan boot's order: a sandbox on the flat floor, the duel on its measured
        // spot, then a simulated crowd round the start. The duel restarts a world, so it needs the HUD's player state.
        if (host.Sandbox.IsSandbox && host.Population is not null) _startTarget = host.Sandbox.SandboxSpawn();
        else if (host.Duel.IsDuel && host.Population is not null)
        {
            if (_hud is not null) _startTarget = host.Duel.DuelStart();
            else Console.Error.WriteLine("duel      skipped: the Metal host has no interface/player state");
        }
        if (!host.Sandbox.IsSandbox && host.Sandbox.Crowd > 0 && host.Population is not null) host.Sandbox.SpawnCrowd(_startTarget, host.Sandbox.Crowd);
        var startRig = host.Session.Camera;
        startRig.Place(new Vector2(_startTarget.X, _startTarget.Z), (host.WorldOptions.Yaw ?? 30) * MathF.PI / 180,
            (host.WorldOptions.Pitch ?? Meitou.Data.World.KenshiCamera.InitialPitchDegrees) * MathF.PI / 180,
            host.WorldOptions.Distance ?? Meitou.Data.World.KenshiCamera.InitialDistance);
        host.SendFocus();
        host.ApplyCamera(host.Session.Camera.Current);
        // --new-game is a shortcut past the creation editor; the same follow the editor's close gives a menu New Game.
        if (_hud is not null && start is not null && host.StartSave is null && host.WorldOptions.Screenshot is null
                && host.PlayerSquad is { Leader.IsNone: false } squad)
            _hud.Follow = squad.Leader;
    }

    /// <summary>
    /// The camera rotation slider changes the same StrategyCamera settings the Vulkan host changes.
    /// </summary>
    void ApplyCameraRotateSpeed(float speed) => host.Session.Camera.Settings.RotateSpeedX = host.Session.Camera.Settings.RotateSpeedY =
        Math.Clamp(speed, UserConfig.MinCameraRotateSpeed, UserConfig.MaxCameraRotateSpeed);

    /// <summary>
    /// The saved camera, input, marker and tutorial settings.
    /// </summary>
    void ApplyUserConfig()
    {
        ApplyCameraRotateSpeed(host.Config.CameraRotateSpeed);
        host.Config.EdgeScroll ??= Meitou.Game.Audio.AudioSystem.Setting(host.Install, "Edge scrolling", 1) != 0;
        host.Config.Tutorials ??= Meitou.Game.Audio.AudioSystem.Setting(host.Install, "tutorials", 1) != 0;
        host.Config.SeedCamera(host.Install);
        host.Config.ApplyCamera(host.Session.Camera.Settings);
        KeyMap.SwapButtons = host.Config.SwapMouseButtons == true;
        _hud?.FontSize = host.Config.FontSize ?? Meitou.Data.Ui.KenshiFontSize.DefaultSetting;
        _hud?.InterfaceScale = Math.Clamp(host.Config.InterfaceScale, UserConfig.MinInterfaceScale, UserConfig.MaxInterfaceScale);
        host.Config.MovementMarker ??= Meitou.Game.Audio.AudioSystem.Setting(host.Install, "Movement marker", 1) != 0;
        host.Config.RotationMarker ??= Meitou.Game.Audio.AudioSystem.Setting(host.Install, "Rotation marker", 1) != 0;
        if (_hud is not null) (_hud.MovementMarker, _hud.RotationMarker) = (host.Config.MovementMarker == true, host.Config.RotationMarker == true);
        host.Saves.Tutorials ??= new TutorialManager(Path.Combine(Directory.GetCurrentDirectory(), TutorialManager.FileName));
        host.Saves.Tutorials.Enabled = host.Config.Tutorials == true;
        _hud?.TutorialWindow.ConfigureTutorials(host.Saves.Tutorials);
    }

    /// <summary>
    /// The title camera, the terrain settled round it and a still's --fly-to walked through the windows on the way.
    /// </summary>
    void SettleStart()
    {
        // Put the title's camera and staging up before the streamers settle, so the menu's first frame is already the staged view.
        if (_hud is not null) host.TitleScreen.ApplyTitleCamera();
        // The streamer settles the fine heights and biome maps round the camera before the first frame draws (the still's camera does not move).
        _streamer?.Settle(host.Camera.Eye);
        if (_streamer is { } settled) Console.WriteLine($"metal     {settled.Describe()}");
        // --fly-to with a still: walk the camera through the intervening windows before the picture, so the map-window
        // recenter/upload path is exercised without needing an interactive flight.
        if (host.WorldOptions.Screenshot is not null && host.WorldOptions.FlyToX is { } flyX && host.WorldOptions.FlyToZ is { } flyZ)
        {
            var start = host.Session.Camera.Current.Target;
            const int steps = 6;
            for (int i = 1; i <= steps; i++)
            {
                double t = i / (double)steps;
                float x = (float)(start.X + (flyX - start.X) * t), z = (float)(start.Z + (flyZ - start.Z) * t);
                var view = host.Session.Camera.Current;
                host.Session.Camera.Place(new Vector2(x, z), view.Yaw, view.Pitch, view.Distance);
                host.ApplyCamera(host.Session.Camera.Current);
                _streamer?.Settle(host.Camera.Eye);
                Console.WriteLine($"metal     fly-to {i}/{steps}: {x:0}, {z:0}");
            }
            if (_streamer is { } flown) Console.WriteLine($"metal     fly-to settled: {flown.Describe()}");
        }
    }

    #endregion

    #region Squad tour

    /// <summary>
    /// The squad tour and the character flights.
    /// </summary>
    SquadTour _tour = null!;

    /// <summary>
    /// The tour holds the camera.
    /// </summary>
    bool _tourHolds;

    /// <summary>
    /// The camera follows the tour's character when it ends.
    /// </summary>
    bool _tourFollows;

    /// <summary>
    /// Where a character is drawn (null when it is not in the snapshot).
    /// </summary>
    Vector3? TourPosition(CharacterId id) =>
        host.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is { } c ? host.TrackedPosition(c) : null;

    /// <summary>
    /// Stops the tour and hands the view back to the rig.
    /// </summary>
    void EndTour()
    {
        _tour.Stop();
        _tourHolds = false;
        host.Session.Camera.TakeOver(new CameraState(host.Camera.Eye, host.Camera.Forward, Vector3.UnitY, host.Camera.Target, host.Camera.Yaw,
            host.Camera.Pitch, host.Camera.Distance, true), strategy: true);
        if (_tourFollows && _tour.Current is { } id) host.Player.Follow = id;
    }

    #endregion

    #region Interface

    /// <summary>
    /// The game's HUD and panels (null without an overlay).
    /// </summary>
    PlayerInterface? _hud;

    /// <summary>
    /// The hover outlines.
    /// </summary>
    HoverOutlineRenderer? _hoverOutline;

    /// <summary>
    /// The move-order marker.
    /// </summary>
    MarkerRenderer? _moveMarker;

    /// <summary>
    /// The navmesh overlay.
    /// </summary>
    NavmeshOverlayRenderer? _navOverlay;

    /// <summary>
    /// The menu's Exit asked to quit.
    /// </summary>
    bool _quit;

    /// <summary>
    /// The HUD and its panels, their flights, picks and markers.
    /// </summary>
    void CreateInterface(DebugOverlay overlay)
    {
        _hud = new PlayerInterface(host.Session, _heights.HeightAt, host.DrawnPosition)
        {
            SpeedUnit = host.Config.SpeedUnit,
            Floaters = host.Config.Floaters,
            FeedbackButtonInPlay = host.Config.FeedbackButton,
        };
        host.Player = _hud;
        _hud.LoadUi(host.GameOptions.UiMode ?? host.Config.UiMode, host.Assets.Find);
        _hud.MeitouMainMenu = host.GameOptions.MeitouMainMenu ?? host.Config.MeitouMainMenu;
        _hud.CreateUiPainter(overlay, host.Assets.Find);
        host.ConfigurePlayerInterface(_hud);
        if (host.Sandbox.IsSandbox)
            _hud.ReservedKeys.UnionWith([Meitou.Game.Host.Input.Key.Z, Meitou.Game.Host.Input.Key.X, Meitou.Game.Host.Input.Key.C,
                Meitou.Game.Host.Input.Key.Y, Meitou.Game.Host.Input.Key.P, Meitou.Game.Host.Input.Key.G]);
        ConnectFlights(_hud);
        ConnectInterface(_hud);
        CreateMarkers(_hud);
    }

    /// <summary>
    /// The character flights a double click starts and the third press ends.
    /// </summary>
    void ConnectFlights(PlayerInterface hud)
    {
        // A double click on a far squad member flies there (Advanced settings "Character flights"); the third press
        // ends the flight on that member. With the setting off PlayerInterface centres on them instead.
        hud.FlyTo = id =>
        {
            if (!host.Config.CharacterFlights || host.Duel.IsDuel || host.Session.Camera.IsFree || TourPosition(id) is not { } at) return false;
            if (new Vector2(at.X - host.Camera.Target.X, at.Z - host.Camera.Target.Z).Length() <= UserConfig.FlightDistance) return false;
            hud.Follow = null;
            _tour.HopSeconds = host.Config.CharacterFlightSeconds;
            _tour.Fly(host.Camera, id, TourPosition);
            (_tourHolds, _tourFollows) = (true, true);
            return true;
        };
        hud.SkipFlight = id =>
        {
            if (!_tour.Active || !_tourFollows || _tour.Current != id) return;
            _tour.Finish(host.Camera, TourPosition);
            EndTour();
        };
    }

    /// <summary>
    /// The portraits, the item icons, the frontend and the picks the interface asks the host for.
    /// </summary>
    void ConnectInterface(PlayerInterface hud)
    {
        if (_characters is not null)
        {
            // The HUD now owns the editor previews and portraits: use the same draw list as Vulkan instead of the
            // interface-less list, or the editor stage never shows a race, gender or slider change.
            _characters.Source = host.FillDrawList;
            var (atlas, sampled) = _characters.PortraitAtlas;
            hud.SetUiTexture(PortraitBar.PortraitTexture, atlas, sampled);
        }
        host.ItemIconFiles.ConnectItemIcons();
        hud.Frontend.ConfigureFrontend(host.Scene.Database,
            () => SaveCatalog.Scan(QuickSave.DefaultRoot, [host.Install.SaveDirectory, .. host.Install.UserSaveDirectories]),
            host.Assets.Find("credits.txt") is { } creditsFile ? File.ReadAllText(creditsFile) : "");
        // The picks the interface asks the host for: the drawn character meshes, the buildings' floors and their closed shells.
        hud.Picking.MeshHit = (id, origin, direction) => _characters?.RayHit(host.DrawKey(id), origin, direction);
        hud.Floor = (() => host.Interiors.ViewedFloorValue, host.Interiors.StepFloor);
        hud.Picking.BuildingMoveHit = (origin, direction, distance) => _objects?.MoveHit(origin, direction, host.Interiors.ViewedFloorValue, distance);
        hud.Picking.SurfaceMoveHit = host.Interiors.SurfaceMoveHit;
        hud.Picking.ClosedBuildingHit = (origin, direction, distance) => _objects?.ClosedBuildingHit(origin, direction, distance);
        hud.StealthView.HiddenInWorld = host.Interiors.HiddenIndoors.Contains;
        hud.StealthView.DrawnHead = id => _characters?.BonePosition(host.DrawKey(id), "Bip01 Head");
        hud.Picking.ShownInteriorHit = (origin, direction, distance) => _objects?.ShownInteriorHit(origin, direction, distance);
        hud.Furniture.AboveViewedFloor = host.Interiors.AboveViewedFloor;
        hud.Furniture.FloorHidden = host.Interiors.FloorHidden;
        hud.GroundItemPicks.GroundItemShape = host.GroundItemSync.GroundShape;
        hud.GroundItemPicks.GroundItemHidden = host.GroundItemSync.HiddenGround.Contains;
        hud.Furniture.DoorShape = (record, door) => host.Nav?.Walkability.DoorGeometry?.Shape(record, door) ?? [];
    }

    /// <summary>
    /// The hover outlines, the move marker and the navmesh overlay, drawn by the shared frame after the post chain.
    /// </summary>
    void CreateMarkers(PlayerInterface hud)
    {
        _hoverOutline = new HoverOutlineRenderer(_ctx);
        _moveMarker = MarkerRenderer.Create(_ctx, host.Assets.Find);
        hud.Markers = _moveMarker;
        _navOverlay = new NavmeshOverlayRenderer(_ctx);
        host.DebugOverlays.NavOverlayRenderer = _navOverlay;
        // The shared frame draw owns the overlays after the post chain now.
        _worldGpu.Markers = _moveMarker;
        _worldGpu.NavmeshOverlay = _navOverlay;
        _worldGpu.HoverOutline = _hoverOutline;
        Console.WriteLine($"metal     HUD ready ({hud.UiMode})");
    }

    /// <summary>
    /// The Kenshi loading screen, drawn while the start loads (not for a still) and between scenes.
    /// </summary>
    void CreateLoadingScreen()
    {
        // The loading screen is backend-neutral: the same Kenshi layout, picture and tips the Vulkan host draws.
        if (_overlay is null) return;
        try
        {
            host.Loading = LoadingScreen.Create(_overlay, host.Assets.Find, host.Install.DataDirectory,
                (host.GameOptions.UiMode ?? host.Config.UiMode) == Meitou.Data.Ui.UiMode.Faithful);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"loading   the loading screen did not load: {e.Message}");
            return;
        }
        if (host.WorldOptions.Screenshot is null) host.BeginLoadingFrames(new MetalLoadingCanvas(_ctx, _window, _overlay));
    }

    /// <summary>
    /// The menus' save, load, new game, feedback, options and exit, and the title when nothing chose the world.
    /// </summary>
    void ConnectScreenActions()
    {
        // The title opens unless a start, a save or a picture chose the world; --title-preview opens it for a still.
        bool showTitle = _hud is not null && (host.GameOptions.TitlePreview || host.WorldOptions.Screenshot is null && start is null
            && host.StartSave is null && !host.Sandbox.IsSandbox && !host.Duel.IsDuel && !host.GameOptions.NoPopulation);
        if (_hud is not null)
        {
            _hud.ScreenAction += e =>
            {
                if (e.Action == "save") host.Saves.SaveRequestedFolder = SaveCatalog.Folder(QuickSave.DefaultRoot, e.Text);
                else if (e.Action == "load") host.Saves.LoadRequestedFolder = e.Text;
                else if (e.Action == "new-game") host.NewGame.NewGameRequested = e.Text;
                else if (e.Action.StartsWith("feedback-", StringComparison.Ordinal)) host.FeedbackCapture.FeedbackAction(e);
                else if (e.Action == "main-menu") host.NewGame.MainMenuRequested = true;
                else if (e.Widget == "OptionsButton" && _settings is not null) _settings.Visible = true;
                else if (e.Widget == "ExitGameButton" || e.Action == "exit") _quit = true;
            };
            if (showTitle) _hud.Frontend.ShowTitle();
        }
    }

    /// <summary>
    /// A --load save swapped in before the first frame.
    /// </summary>
    void OpenStartSave()
    {
        // --load starts in the save: the same swap the menu's load uses, with the world already read around its camera.
        if (_hud is not null && host.StartSave is { } startSave)
        {
            host.Saves.ReadingFolder = host.GameOptions.Load ?? "";
            try
            {
                var navmesh = host.Saves.SwapInSave(Task.FromResult(startSave), interactive: true, out string summary);
                host.AwaitLoading(navmesh);
                host.SettleNewWorld();
                _hud.FollowSelected();
                host.Saves.FinishLoadNotice(summary);
                host.ApplyCamera(host.Session.Camera.Current);
            }
            catch (GameHost.SceneLoadFailed e)
            {
                Console.Error.WriteLine($"load      {e.Message}");
                _hud.Notice(e.Message, true);
            }
        }
    }

    #endregion

    #region Settings

    /// <summary>
    /// The settings Metal can change live (null without the HUD).
    /// </summary>
    SettingsPanel? _settingsValues;

    /// <summary>
    /// The settings dialog (null without the HUD).
    /// </summary>
    SettingsMenu? _settings;

    /// <summary>
    /// The settings dialog over Metal's live settings, with the saved values.
    /// </summary>
    void CreateSettingsMenu(PlayerInterface hud)
    {
        _settingsValues = CreateMetalSettings();
        _settings = new SettingsMenu(hud, _settingsValues, host.Session.Bindings, SaveSettings,
            new Slider("Frame limit", 0, 360, () => _fpsLimit, v => _fpsLimit = (int)MathF.Round(v), "0",
                Text: v => v <= 0 ? "Unlimited" : $"{v:0} fps"), host.Config)
        {
            UpscalerStatus = _upscaling.UpscalerStatusText,
            DisplayRowShown = DisplayRowShown,
            RenderResolutionLabels = RenderResolutionLabels,
            GameplayChanged = host.Saves.ApplyGameplay,
            ModSettings = () => host.Modding.Mods?.Settings ?? [],
            AttackSlotsChanged = slots =>
            {
                foreach (var combat in host.Session.World.Systems.OfType<Meitou.Simulation.Combat.CombatSystem>()) combat.UseAttackSlots(slots);
            },
            LateDefencesChanged = on =>
            {
                foreach (var combat in host.Session.World.Systems.OfType<Meitou.Simulation.Combat.CombatSystem>()) combat.LateDefences = on;
            },
            TimeSpeedsChanged = () => host.Session.Simulation.SetFastSpeeds(host.Config.FastSpeed, host.Config.FastestSpeed),
            DoorsCloseChanged = on =>
            {
                foreach (var locks in host.Session.World.Systems.OfType<Meitou.Simulation.Locks.LockSystem>()) locks.DoorsCloseBehind = on;
            },
            AnimalsPickUpChanged = on =>
            {
                foreach (var players in host.Session.World.Systems.OfType<Meitou.Simulation.Player.PlayerSystem>()) players.AnimalsPickUp = on;
            },
            BenchFacingChanged = on => host.Session.World.BenchFacingFix = on,
            TakeIntoBackpackChanged = on => host.Session.World.TakeIntoBackpack = on,
            TutorialsChanged = on => { if (host.Saves.Tutorials is { } t) t.Enabled = on; },
            ResetTutorials = () => host.Saves.Tutorials?.Reset(),
            OffscreenChanged = () =>
            {
                foreach (var population in host.Session.World.Systems.OfType<Meitou.Simulation.Population.PopulationSystem>())
                {
                    (population.KeepRoamers, population.OffscreenFights, population.OffscreenHealing, population.CampaignAncestorsOnce,
                        population.KeepWild, population.ResidentSchedule) = (host.Config.KeepRoamers, host.Config.OffscreenFights,
                        host.Config.OffscreenHealing, host.Config.CampaignAncestorsOnce, host.Config.KeepWild, host.Config.ResidentSchedule);
                }
                foreach (var production in host.Session.World.Systems.OfType<Meitou.Simulation.Production.ProductionSystem>())
                    production.UnloadedProduction = host.Config.UnloadedProduction;
            },
            CameraRotateSpeedChanged = ApplyCameraRotateSpeed,
            CameraChanged = () => host.Config.ApplyCamera(host.Session.Camera.Settings),
            AudioChanged = host.Sound.AudioSettingsChanged,
        };
        ApplySavedSettings(_settingsValues, _settings);
    }

    /// <summary>
    /// The saved graphics values, the preset and the diagnostic opt-outs that win over them.
    /// </summary>
    void ApplySavedSettings(SettingsPanel settingsValues, SettingsMenu settings)
    {
        // The saved graphics values are the same keys the Vulkan panel uses. The Metal display choices have their own
        // config fields, and a command-line upscaler/scale stays explicit for this run.
        var metalDisplayKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "VSync", "Full screen", SettingsMenu.MetalUseUpscalingSlider, SettingsMenu.MetalPresetSlider, SettingsMenu.MetalRenderScaleSlider,
            WorldFrame.UpscalerSliders[1], SettingsMenu.MetalRetinaResolutionSlider, SettingsMenu.MetalRenderResolutionSlider,
            SettingsMenu.MetalUiResolutionSlider,
        };
        foreach (var slider in settingsValues.Sliders)
            if (!GameHost.RunOnlyKeys().Contains(slider.Label) && !metalDisplayKeys.Contains(slider.Label) &&
                host.Config.Graphics.TryGetValue(slider.Label, out float value))
                slider.Set(Math.Clamp(value, slider.Min, slider.Max));
        foreach (var toggle in settingsValues.Toggles)
            if (!LookPresets.HiddenSwitch(toggle.Label) && !GameHost.RunOnlyKeys().Contains(SettingsMenu.SwitchKeyPrefix + toggle.Label)
                    && host.Config.Graphics.TryGetValue(SettingsMenu.SwitchKeyPrefix + toggle.Label, out float value)
                    && (value >= 0.5f) != toggle.Get())
                toggle.Set(value >= 0.5f);
        settings.Tiers.ApplySavedPreset();
        // The impostor atlas path is re-enabled after the frame-slot fence. Keep the diagnostic off switch as a
        // fallback; the saved "Far impostors" toggle is otherwise allowed through.
        if (_foliage is not null && !_metalImpostors) _foliage.DisableImpostors();
        // The saved Cloud shadows and Shadow distance settings can re-enable the safe-mode suspects after the sky and the
        // shadow pass were made; keep both off until the black-terrain path is fixed, unless the diagnostic env asks.
        if (!_metalCloudShadows) _sky.CloudShadows = false;
        if (!_metalShadows) _shadow.Enabled = false;
        if (_openSettingsForStill) settings.Visible = true;
    }

    /// <summary>
    /// The settings Metal supplies: display, world distances, lighting, characters, ground relief, water and effects.
    /// </summary>
    SettingsPanel CreateMetalSettings()
    {
        var sliders = new List<Slider>();
        var toggles = new List<Toggle>();
        AddDisplaySettings(sliders);
        AddWorldSettings(sliders, toggles);
        AddLightingSettings(sliders, toggles);
        AddCharacterSettings(sliders, toggles);
        AddWaterSettings(sliders);
        AddEffectSettings(sliders, toggles);
        var actions = new List<PanelAction>();
        if (_foliage?.ImpostorDiskCache is { } cache)
            actions.Add(new PanelAction("Delete billboard cache", () =>
            {
                var (files, bytes) = cache.Measure();
                return $"Delete {files} billboard atlases ({bytes / 1048576.0:0} MB) in {cache.Root}?";
            }, () =>
            {
                var (files, bytes) = cache.Clear();
                Console.WriteLine($"impostors deleted {files} cached atlases ({bytes / 1048576.0:0} MB) from {cache.Root}");
                return $"Deleted {files} atlases ({bytes / 1048576.0:0} MB); they are baked again when next needed.";
            }));
        return new SettingsPanel(_overlay!, "Settings   (Tab hides this)", sliders, toggles, "Metal switches", actions, null, null);
    }

    /// <summary>
    /// VSync, full screen, the MetalFX preset, filtering and texture quality.
    /// </summary>
    void AddDisplaySettings(List<Slider> sliders)
    {
        // Display: VSync, full screen, the MetalFX scale preset, the free render scale under it and filtering. The frame limit is added by
        // SettingsMenu itself.
        sliders.Add(new Slider("VSync", 0, 1, () => _display.VSync ? 1 : 0, v => _display.VSync = v >= 0.5f,
            Text: v => v >= 0.5f ? "on" : "off (uncapped)"));
        sliders.Add(new Slider("Full screen", 0, 1, () => _display.FullScreen ? 1 : 0, v => _display.SetFullScreen(v >= 0.5f),
            Text: v => v >= 0.5f ? "on" : "window"));
        sliders.Add(new Slider(SettingsMenu.MetalUseUpscalingSlider, 0, 1, () => _upscaling.Enabled ? 1 : 0,
            v => _upscaling.SetEnabled(v >= 0.5f), "0"));
        sliders.Add(new Slider(SettingsMenu.MetalPresetSlider, 0, 8, () => _upscaling.CurrentPresetIndex(), v =>
        {
            int index = (int)MathF.Round(v);
            if (index is >= 0 and <= 7) _upscaling.SetPreset(index);
        }, "0"));
        sliders.Add(new Slider(SettingsMenu.MetalRenderScaleSlider, 0.33f, 1, () => _ctx.RenderScale, _upscaling.SetRenderScale, "0.00",
            Text: RenderScaleText, Step: 0.01f));
        sliders.Add(new Slider(SettingsMenu.MetalRetinaResolutionSlider, 0, 1, () => host.Config.MetalRetinaResolution ? 1 : 0,
            v => _upscaling.SetRetinaResolution(v >= 0.5f), "0"));
        sliders.Add(new Slider(SettingsMenu.MetalRenderResolutionSlider, 1, UserConfig.MetalRenderDivisorMax, () => host.Config.MetalRenderDivisor,
            v => _upscaling.SetDivisor((int)MathF.Round(v)), "0"));
        sliders.Add(new Slider(SettingsMenu.MetalUiResolutionSlider, 0, 1, () => host.Config.MetalUiAtScreenResolution ? 1 : 0,
            v => _upscaling.SetUiAtScreen(v >= 0.5f), "0"));
        sliders.Add(new Slider(WorldFrame.FilteringSlider, 0, 4, () => MathF.Log2(_ctx.Samplers.MaxAnisotropy),
            v => _ctx.Samplers.MaxAnisotropy = 1 << (int)MathF.Round(v), "0",
            Text: v => (1 << (int)MathF.Round(v)) + "x"));
        sliders.Add(new Slider(WorldFrame.TextureQualitySlider, 0, TextureQuality.Maximum, () => TextureQuality.Level,
            v => TextureQuality.Level = (int)MathF.Round(v), "0",
                Text: v => TextureQuality.Labels[Math.Clamp((int)MathF.Round(v), 0, TextureQuality.Maximum)]));
    }

    /// <summary>
    /// Whether a Display row of the Metal upscaling is shown: with the switch on the preset and the render scale, off the interface's size and, in a
    /// window the Retina resolution, in full screen the render resolution.
    /// </summary>
    bool DisplayRowShown(string key) => key switch
    {
        "metalUpscaler" or SettingsMenu.MetalRenderScaleSlider => _upscaling.Enabled,
        "metalUiAtScreenResolution" => !_upscaling.Enabled,
        "metalRetinaResolution" => !_upscaling.Enabled && !_display.FullScreen,
        "metalRenderDivisor" => !_upscaling.Enabled && _display.FullScreen,
        _ => true,
    };

    /// <summary>
    /// The Render resolution row's choices with their sizes on the screen as it is now.
    /// </summary>
    string[] RenderResolutionLabels()
    {
        var pixels = _display.Window.FramebufferSize;
        return MetalUpscaling.ResolutionLabels(pixels.X, pixels.Y);
    }

    /// <summary>
    /// The window's pixels per point: 2 on a Retina display, 1 elsewhere.
    /// </summary>
    float BackingScale() => _window.FramebufferSize.X / (float)Math.Max(_window.Size.X, 1);

    /// <summary>
    /// The render scale slider's value with the scene's size it gives, from the window's pixels as they are now (full screen, a resize).
    /// </summary>
    string RenderScaleText(float scale)
    {
        var pixels = _display.Window.FramebufferSize;
        var (width, height) = MetalContext.SceneSize(pixels.X, pixels.Y, scale);
        string ratio = scale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        return $"{ratio} ({width} x {height} px)";
    }

    /// <summary>
    /// The object, character, foliage and terrain distances.
    /// </summary>
    void AddWorldSettings(List<Slider> sliders, List<Toggle> toggles)
    {
        // World: the distances the Metal renderers actually read every frame.
        if (_objects is not null)
        {
            sliders.Add(new Slider("Object draw distance", 1000, 400000, () => _objects.ObjectDistance, v => _objects.ObjectDistance = v, "0",
                Logarithmic: true, Faithful: Enhancements.FaithfulObjectDistance, Meitou: Enhancements.MeitouObjectDistance));
            if (_objects.HasLandmarks)
                sliders.Add(new Slider("Landmark distance (Meitou)", 1000, 400000, () => _objects.LandmarkDistance,
                    v => _objects.LandmarkDistance = MathF.Round(v / 100) * 100, "0", Logarithmic: true));
            sliders.Add(new Slider("Distant towns (zones)", 0, ObjectRanges.MaxDistantTownRangeZones * 10,
                () => _objects.DistantRange / Meitou.Data.World.WorldLayout.ZoneSize,
                v => _objects.DistantRange = MathF.Round(v) * Meitou.Data.World.WorldLayout.ZoneSize, "0"));
            sliders.Add(new Slider("Object LOD distance x", 0.25f, 4, () => 1 / _objects.LodBias,
                v => _objects.LodBias = 1 / v, "0.00", Logarithmic: true));
        }
        if (_characters is not null) sliders.Add(WorldFrame.CharacterDistance(_characters));
        if (_foliage is not null)
        {
            sliders.Add(new Slider("Foliage draw distance x", 0.25f, 80, () => _foliage.RangeSetting,
                v => _foliage.RangeSetting = v, "0.00", Logarithmic: true));
            sliders.Add(new Slider("Grass draw distance x", 0.25f, 80, () => _foliage.GrassRangeSetting,
                v => _foliage.GrassRangeSetting = v, "0.00", Logarithmic: true));
            sliders.Add(new Slider("Grass density x", 0.1f, 2, () => _foliage.GrassDensitySetting,
                v => _foliage.GrassDensitySetting = v, "0.00"));
            sliders.Add(new Slider("Large foliage range (F6 Meitou)", 1000, 120000, () => _foliage.LargeRange,
                v => _foliage.LargeRange = MathF.Round(v / 50) * 50, "0", Logarithmic: true));
            sliders.Add(new Slider("Medium foliage range", 400, 80000, () => _foliage.MediumRange,
                v => _foliage.MediumRange = MathF.Round(v / 50) * 50, "0", Logarithmic: true));
            sliders.Add(new Slider("Small foliage range", 200, 40000, () => _foliage.SmallRange,
                v => _foliage.SmallRange = MathF.Round(v / 50) * 50, "0", Logarithmic: true));
            sliders.Add(new Slider("Foliage LOD distance x (lod Meitou)", 0.25f, 8,
                () => FoliageRenderer.DefaultLodTolerance / _foliage.LodTolerance,
                v => _foliage.LodTolerance = FoliageRenderer.DefaultLodTolerance / v, "0.00", Logarithmic: true));
            toggles.Add(new Toggle("Foliage ranges", () => _foliage.MeitouRange, v => _foliage.MeitouRange = v));
            toggles.Add(new Toggle("Far impostors", () => _foliage.Impostors, v => _foliage.Impostors = v));
            sliders.Add(new Slider("Impostor distance (F7 Meitou)", 500, 40000, () => _foliage.ImpostorDistance,
                v => _foliage.ImpostorDistance = MathF.Round(v / 50) * 50, "0", Logarithmic: true));
            sliders.Add(new Slider("Large impostor distance (F7 Meitou)", 500, 40000, () => _foliage.LargeImpostorDistance,
                v => _foliage.LargeImpostorDistance = MathF.Round(v / 50) * 50, "0", Logarithmic: true));
            sliders.Add(WorldFrame.ImpostorBudget(_foliage));
        }
        sliders.Add(new Slider("Terrain detail: error px (less = finer)", 1, 32, () => host.Render.TerrainPixelError,
            v => host.Render.TerrainPixelError = v, "0.0", Logarithmic: true,
            Faithful: WorldRenderOptions.DefaultTerrainPixelError, Meitou: Enhancements.MeitouTerrainPixelError));
        sliders.Add(WorldFrame.MaterialDistance(host.Render, _streamer, host.WorldOptions));
    }

    /// <summary>
    /// The shadow controls and the sky's switches.
    /// </summary>
    void AddLightingSettings(List<Slider> sliders, List<Toggle> toggles)
    {
        // Lighting: the same shadow controls the Vulkan settings expose, and the sky switches the Metal sky reads.
        const float shadowsOff = Meitou.Data.World.KenshiShadows.MinRange * 0.9f;
        sliders.Add(new Slider("Shadow distance (game 1k-9k, 0 off)", shadowsOff, 200000,
            () => _shadow.Enabled ? _shadow.Settings.Range : shadowsOff, v =>
            {
                _shadow.Enabled = v >= Meitou.Data.World.KenshiShadows.MinRange;
                if (_shadow.Enabled) _shadow.Settings = _shadow.Settings with { Range = MathF.Round(v / 100) * 100 };
            }, "0", Logarithmic: true, Text: v => _shadow.Enabled ? v.ToString("0", CultureInfo.InvariantCulture) : "0 (off)",
            Faithful: Meitou.Data.World.KenshiShadows.DefaultRange, Meitou: Enhancements.MeitouShadowRange));
        sliders.Add(new Slider("Shadow map size (game quality 0-2)", 0, 2,
            () => Array.IndexOf(Meitou.Data.World.KenshiShadows.QualitySizes, _shadow.Settings.MapSize),
            v => _shadow.Settings = _shadow.Settings with { MapSize = Meitou.Data.World.KenshiShadows.MapSize((int)MathF.Round(v)) }, "0",
            Text: v => Meitou.Data.World.KenshiShadows.MapSize((int)MathF.Round(v)).ToString(CultureInfo.InvariantCulture) + "²"));
        if (_shadow.Meitou)
        {
            sliders.Add(new Slider("Shadow filter 0-2 (Meitou; 0 low-end)", 0, 2, () => _shadow.FilterQuality,
                v => _shadow.FilterQuality = (int)MathF.Round(v), "0"));
            toggles.Add(new Toggle("Shadow cascade 5 (Meitou)", () => _shadow.MeitouCascades >= 5,
                v => _shadow.MeitouCascades = v ? 5 : 4));
        }
        sliders.Add(new Slider("Haze strength (1 = game)", 0, 3, () => _sky.HazeStrength,
            v => _sky.HazeStrength = v, "0.00", Faithful: 1, Meitou: Enhancements.MeitouHazeStrength));
        sliders.Add(WorldFrame.HazeDistance(host.WorldOptions));
        sliders.Add(new Slider("Planetshine strength (x physical)", 0, 200, () => _sky.PlanetshineStrength,
            v => _sky.PlanetshineStrength = MathF.Round(v), "0"));
        sliders.Add(new Slider(WorldFrame.CloudHeightSlider, Meitou.Data.World.VolumetricClouds.MinBottom,
            Meitou.Data.World.VolumetricClouds.MaxBottom, () => _sky.CloudHeight,
            v => _sky.CloudHeight = MathF.Round(v / 100) * 100, "0", Logarithmic: true));
        sliders.Add(new Slider("Ambient light strength (1 = game)", 0, 3, () => _sky.AmbientStrength,
            v => _sky.AmbientStrength = MathF.Round(v * 20) / 20, "0.00"));
        sliders.Add(new Slider("Night ambient light strength (1 = game)", 0, 5, () => _sky.NightAmbientStrength,
            v => _sky.NightAmbientStrength = MathF.Round(v * 20) / 20, "0.00"));
        if (_worldGpu.ProbeGi is { } probeGi)
        {
            toggles.Add(new Toggle("Indirect light", () => probeGi.Enabled, v => probeGi.Enabled = v));
            toggles.Add(new Toggle("Indoor GI", () => probeGi.InteriorEnabled, v => probeGi.InteriorEnabled = v));
        }
        toggles.Add(new Toggle("Cloud shadows", () => _sky.CloudShadows, v => _sky.CloudShadows = v));
        toggles.Add(new Toggle("Night air", () => _sky.NightAir, v => _sky.NightAir = v));
        toggles.Add(new Toggle("Planetshine", () => _sky.Planetshine, v => _sky.Planetshine = v));
        toggles.Add(new Toggle("Night sky", () => _sky.MeitouStars, v => _sky.MeitouStars = v));
        toggles.Add(new Toggle("Clouds", () => _sky.LitClouds, v => _sky.LitClouds = v));
    }

    /// <summary>
    /// The character and icon switches, and ground relief.
    /// </summary>
    void AddCharacterSettings(List<Slider> sliders, List<Toggle> toggles)
    {
        // The Meitou character, icon and audio switches from the Vulkan panel, which the shared renderers read.
        if (_characters is not null)
        {
            toggles.Add(new Toggle("Skin scattering", () => _characters.SkinShading, v => _characters.SkinShading = v));
            toggles.Add(new Toggle("Enhanced item icons", () => _characters.EnhancedIcons, v => _characters.EnhancedIcons = v));
        }
        // Ground relief (docs/render-terrain.md): the Tab panel's quality, depth, parallax distance and toggle.
        if (GroundRelief.Enabled)
        {
            sliders.Add(new Slider(WorldFrame.GroundReliefQualitySlider, 0, 2, () => GroundRelief.Quality,
                v => GroundRelief.Quality = Math.Clamp((int)MathF.Round(v), 0, 2), "0",
                Text: v => GroundRelief.QualityName((int)MathF.Round(v))));
            sliders.Add(new Slider("Ground relief depth (cm, typical ground)", 0, 40, () => GroundRelief.Depth * 10,
                v => GroundRelief.Depth = MathF.Round(v * 2) / 20, "0.0"));
            sliders.Add(new Slider(WorldFrame.GroundParallaxDepthSlider, 10, 200, () => GroundRelief.ParallaxScale * 100,
                v => GroundRelief.ParallaxScale = MathF.Round(v / 5) / 20, "0"));
            sliders.Add(new Slider("Ground parallax distance (m)", 10, 150, () => GroundRelief.ParallaxDistance / 10,
                v => GroundRelief.ParallaxDistance = MathF.Round(v) * 10, "0"));
            toggles.Add(new Toggle("Ground parallax", () => GroundRelief.ParallaxActive, v => GroundRelief.ParallaxActive = v, OnOff: true));
        }
    }

    /// <summary>
    /// The water reflection and the Meitou water clarity.
    /// </summary>
    void AddWaterSettings(List<Slider> sliders)
    {
        // Water reflection: the same game setting and reflection range the Vulkan panel exposes; the reflection pass is seam-neutral.
        if (_worldGpu.Reflection is { } reflection)
        {
            sliders.Add(new Slider(WorldFrame.WaterReflectionSlider, 0, 4, () => reflection.Level,
                v => reflection.Level = (int)MathF.Round(v), "0",
                Text: v => (int)MathF.Round(v) switch { 0 => "0 off", 1 => "1 landscape", 2 => "2 characters",
                    3 => "3 buildings", _ => "4 everything" }));
            sliders.Add(new Slider("Reflection range x (game 0.6)", 0.1f, 50, () => reflection.Range,
                v => reflection.Range = v, "0.00", Logarithmic: true));
            sliders.Add(WorldFrame.ReflectionSamples(reflection, host.WorldOptions));
        }
        // Water: the Meitou clarity controls only mean something on the Meitou surface; the faithful surface keeps them hidden.
        if (_water is { Meitou: true })
        {
            sliders.Add(new Slider("Water clarity x (Meitou)", 0.25f, 8, () => _water.Clarity,
                v => _water.Clarity = v, "0.00", Logarithmic: true));
            sliders.Add(new Slider("Far water clarity (Meitou)", 0, 1, () => _water.FarClarity,
                v => _water.FarClarity = v, "0.00", Text: v => v <= 0 ? "0 (true slant)" : v.ToString("0.00", CultureInfo.InvariantCulture)));
            sliders.Add(new Slider("Water see-through distance (Meitou, game 4000)", 1000, 100000,
                () => _water.ClearDistance, v => _water.ClearDistance = MathF.Round(v / 100) * 100, "0", Logarithmic: true));
            WorldFrame.AddWaterSizeSliders(sliders, host.WorldOptions);
        }
    }

    /// <summary>
    /// The tone map, grading, heat haze, particles, light shafts and dithering.
    /// </summary>
    void AddEffectSettings(List<Slider> sliders, List<Toggle> toggles)
    {
        // Effects: the composite already supports the game's exposure/tone-map/grading path, even before the rest of
        // the Vulkan post chain is ported.
        sliders.Add(new Slider("Tone map (0 = game)", 1, 3, () => (int)host.WorldOptions.Post.ToneMap,
            v => host.WorldOptions.Post.ToneMap = (ToneMapOperator)(int)MathF.Round(v), "0",
            Text: v => (ToneMapOperator)(int)MathF.Round(v) switch
            {
                ToneMapOperator.Shoulder => "shoulder",
                ToneMapOperator.Aces => "ACES",
                ToneMapOperator.Hybrid => "hybrid (clamp + ACES)",
                _ => "clamp (as the game)",
            }));
        sliders.Add(new Slider("Hybrid tone map: ACES share", 0, 1, () => host.WorldOptions.Post.ToneMix,
            v => host.WorldOptions.Post.ToneMix = MathF.Round(v * 20) / 20, "0.00"));
        sliders.Add(new Slider("Grading (0 = game)", 0, 1, () => host.WorldOptions.Post.Grade ? 1 : 0,
            v => host.WorldOptions.Post.Grade = v >= 0.5f,
            Text: v => v >= 0.5f ? $"saturation {PostOptions.Saturation:0.00}, contrast {PostOptions.Contrast:0.00}" : "off (as the game)"));
        sliders.Add(new Slider("Heat haze strength (1 = game)", 0, 3, () => host.WorldOptions.Post.HeatHazeStrength,
            v => host.WorldOptions.Post.HeatHazeStrength = MathF.Round(v * 20) / 20, "0.00", Faithful: 1,
                Meitou: Enhancements.MeitouHeatHazeStrength));
        sliders.Add(new Slider(WorldFrame.ParticleSliders[0], 0, 4,
            () => WorldFrame.WeatherParticlesSlider(_worldGpu.WeatherParticles, host.WorldOptions.Post), v =>
            {
                int i = (int)MathF.Round(v);
                _worldGpu.WeatherParticles = i > 0;
                if (i > 0)
                    (host.WorldOptions.Post.LowResParticles, host.WorldOptions.Post.ParticleDivisor) = i switch { 1 => (true, 4), 2 => (true, 2),
                        3 => (true, 0), _ => (false, 0) };
            }, "0"));
        sliders.Add(new Slider(WorldFrame.ParticleSliders[1], 0.1f, 1, () => _worldGpu.ParticleDensity,
            v => _worldGpu.ParticleDensity = MathF.Round(v * 20) / 20, "0.00"));
        sliders.Add(new Slider("Night grading strength (0 = game)", 0, 1, () => host.WorldOptions.Post.NightGradeStrength,
            v => host.WorldOptions.Post.NightGradeStrength = MathF.Round(v * 20) / 20, "0.00"));
        sliders.Add(new Slider("Light shafts strength", 0, 1, () => host.WorldOptions.Post.ShaftStrength,
            v => host.WorldOptions.Post.ShaftStrength = MathF.Round(v * 20) / 20, "0.00"));
        sliders.Add(new Slider("Light shafts air (0 = none)", 0, 4, () => host.WorldOptions.Post.ShaftAir,
            v => host.WorldOptions.Post.ShaftAir = MathF.Round(v * 20) / 20, "0.00"));
        sliders.Add(new Slider("Light shafts air at dawn and dusk (x)", 0, 6, () => host.WorldOptions.Post.ShaftAirDawn,
            v => host.WorldOptions.Post.ShaftAirDawn = MathF.Round(v * 10) / 10, "0.0"));
        sliders.Add(new Slider(WorldFrame.ShaftQualitySlider, 0, 2, () => host.WorldOptions.Post.ShaftQuality,
            v => host.WorldOptions.Post.ShaftQuality = Math.Clamp((int)MathF.Round(v), 0, 2), "0",
            Text: v => (int)MathF.Round(v) switch { 0 => "Low", 1 => "Medium", _ => "High" }));
        toggles.Add(new Toggle("Dithering", () => host.WorldOptions.Post.Dither, v => host.WorldOptions.Post.Dither = v));
    }

    /// <summary>
    /// Saves the display choices, the interface mode, the bindings and the graphics values.
    /// </summary>
    void SaveSettings()
    {
        host.Config.VSync = _display.VSync;
        host.Config.FpsLimit = _fpsLimit;
        host.Config.FullScreen = _display.FullScreen;
        if (_upscaling.Enabled) host.Config.MetalRenderScale = _ctx.RenderScale;   // without the upscaler it renders by the division
        if (_hud is not null && (!host.GameOptions.LowEnd || _hud.UiMode != Meitou.Data.Ui.UiMode.Faithful))
            host.Config.UiMode = _hud.UiMode;   // --low-end's Faithful UI is for the run
        host.Config.Bindings = host.Session.Bindings.ToDictionary();
        if (_settingsValues is not null)
        {
            foreach (var slider in _settingsValues.Sliders)
            {
                // These have their own config fields; the rest use the same graphics keys as the Vulkan settings.
                if (GameHost.RunOnlyKeys().Contains(slider.Label) || slider.Label == WorldFrame.UpscalerSliders[1] ||
                    slider.Label is SettingsMenu.MetalUseUpscalingSlider or SettingsMenu.MetalPresetSlider or SettingsMenu.MetalRenderScaleSlider
                        or SettingsMenu.MetalRetinaResolutionSlider or SettingsMenu.MetalRenderResolutionSlider
                        or SettingsMenu.MetalUiResolutionSlider) continue;
                host.Config.Graphics[slider.Label] = slider.Get();
            }
            foreach (var toggle in _settingsValues.Toggles)
                if (!GameHost.RunOnlyKeys().Contains(SettingsMenu.SwitchKeyPrefix + toggle.Label))
                    host.Config.Graphics[SettingsMenu.SwitchKeyPrefix + toggle.Label] = toggle.Get() ? 1 : 0;
            _settings?.Tiers.SavePreset();
        }
        host.Config.Save();
    }

    #endregion

    #region Input

    /// <summary>
    /// The window's keyboards and mice.
    /// </summary>
    Silk.NET.Input.IInputContext _input = null!;

    /// <summary>
    /// A Shift key is held.
    /// </summary>
    bool _shiftDown;

    /// <summary>
    /// F11: the frame's stats panel.
    /// </summary>
    bool _statsVisible;

    /// <summary>
    /// F12: the frame-rate lines.
    /// </summary>
    bool _frameStatsVisible;

    /// <summary>
    /// F8 or Print Screen asked for a screenshot.
    /// </summary>
    bool _screenshotNow;

    /// <summary>
    /// Ctrl+/ asked for a GPU capture of the next frame.
    /// </summary>
    bool _gpuCaptureRequested;

    /// <summary>
    /// The pointer's last position in window points.
    /// </summary>
    Vector2? _lastMouse;

    /// <summary>
    /// The window's input devices.
    /// </summary>
    Silk.NET.Input.IInputContext OpenInput()
    {
        return _input = Silk.NET.Input.InputWindowExtensions.CreateInput(_window);
    }

    /// <summary>
    /// The interface's clipboard through the first keyboard.
    /// </summary>
    void ConnectClipboard()
    {
        if (_hud is not null)
        {
            _hud.ClipboardRead = () => _input.Keyboards.Count > 0 ? ClipboardText.Read(_input.Keyboards[0]) : null;
            _hud.ClipboardWrite = text => { if (_input.Keyboards.Count > 0) _input.Keyboards[0].ClipboardText = text; };
        }
    }

    /// <summary>
    /// The keyboards, the mice, the window's focus and the session's ticks.
    /// </summary>
    void HookInput()
    {
        foreach (var keyboard in _input.Keyboards) HookKeyboard(keyboard);
        foreach (var mouse in _input.Mice) HookMouse(mouse);
        _window.FocusChanged += OnFocusChanged;
        // The game's quick save/load keys are control actions, as in the Vulkan host (F5/F9 by default).
        host.Session.Ticked += OnTicked;
    }

    /// <summary>
    /// A keyboard's keys and text.
    /// </summary>
    void HookKeyboard(Silk.NET.Input.IKeyboard keyboard)
    {
        keyboard.KeyDown += (keyboard, key, _) => OnKeyDown(keyboard, key);
        keyboard.KeyUp += (_, key, _) =>
        {
            if (key is SilkKey.ShiftLeft or SilkKey.ShiftRight)
            {
                _shiftDown = false;
                if (_hud is not null) _hud.SlowModifier = false;
            }
            if (KeyMap.Map(key) is { } mapped)
            {
                host.Session.Input.SetKey(mapped, false);
                _hud?.Build.BuildKeyUp(mapped);
            }
        };
        keyboard.KeyChar += (_, value) =>
        {
            if (host.Covered) return;
            if (_settings?.Visible == true) _settings.TextInput(value);
            else if (host.Sandbox.SandboxPicker is { Open: true }) host.Sandbox.SandboxPicker.TextInput(value);
            else if (host.DebugMenu.DebugMenuOpen) host.DebugMenu.DebugMenuText(value);
            else _hud?.UiText(value);
        };
    }

    /// <summary>
    /// A key goes to the dialogs and menus first, then the view keys, the camera codes, the tour, the interface and the session.
    /// </summary>
    void OnKeyDown(Silk.NET.Input.IKeyboard keyboard, SilkKey key)
    {
        if (key is SilkKey.ShiftLeft or SilkKey.ShiftRight)
        {
            _shiftDown = true;
            if (_hud is not null) _hud.SlowModifier = true;
        }
        if (key == SilkKey.Enter && Alt())
        {
            ToggleFullScreen();
            return;
        }
        if (host.Covered)
        {
            if (key is SilkKey.Escape or SilkKey.Space) host.SkyArrival?.Skip();
            return;
        }
        bool ctrl = Ctrl();
        // Ctrl+/ records one frame for Xcode's Metal debugger; it works over the settings dialog too.
        if (ctrl && (key is SilkKey.Slash or SilkKey.KeypadDivide))
        {
            _gpuCaptureRequested = true;
            return;
        }
        if (MenuKey(key, ctrl)) return;
        if (ViewKey(key)) return;
        if (CameraCodeKey(keyboard, key, ctrl)) return;
        if (TourKey(key)) return;
        if (KeyMap.Map(key) is not { } mapped) return;
        // The interface's keys come first (I/C, M/T/Y, Esc, B), as in the Vulkan host; what it does not take reaches
        // the session (WASD, Q/E, Space, F2..F4, PageUp/PageDown, the camera bindings).
        if (_hud is not null && (_hud.UiKey(mapped, ctrl, _shiftDown) || _hud.Build.BuildKey(mapped, _shiftDown))) return;
        host.Session.Input.SetKey(mapped, true);
        if (_hud is not null) _hud.GameHotkeys.Key(mapped, _shiftDown);
        host.Sandbox.SandboxKey(mapped);
        if (host.Duel.IsDuel && mapped == EngineKey.Backspace) host.Duel.DuelRestartRequested = true;
    }

    /// <summary>
    /// The settings dialog, the sandbox list and the debug menu take the key (true: taken).
    /// </summary>
    bool MenuKey(SilkKey key, bool ctrl)
    {
        // The full settings dialog (Metal's F10 used to be a preset overlay; the preset now lives in Display).
        if (_settings?.Visible == true)
        {
            if ((key == SilkKey.KeypadEnter ? EngineKey.Enter : KeyMap.Map(key)) is { } settingsKey)
            {
                if (settingsKey is EngineKey.F8 or EngineKey.PrintScreen) _screenshotNow = true;
                else _settings.KeyDown(settingsKey, _shiftDown, ctrl);
            }
            return true;
        }
        // The sandbox animation list, while open, takes every key too.
        if (host.Sandbox.SandboxPicker is { Open: true })
        {
            if ((key == SilkKey.KeypadEnter ? EngineKey.Enter : KeyMap.Map(key)) is { } pickerKey) host.Sandbox.SandboxPicker.KeyDown(pickerKey);
            return true;
        }
        if (_hud is not null && DebugToggle(key))
        {
            host.DebugMenu.DebugMenuOpen = !host.DebugMenu.DebugMenuOpen;
            host.DebugMenu.DebugParts = null;
            return true;
        }
        if (host.DebugMenu.DebugMenuOpen)
        {
            if ((key == SilkKey.KeypadEnter ? EngineKey.Enter : KeyMap.Map(key)) is { } menuKey) host.DebugMenu.DebugMenuKey(menuKey);
            return true;
        }
        if (key == SilkKey.F10 && _settings is not null)
        {
            _settings.Visible = !_settings.Visible;
            return true;
        }
        if (_settings is not null && KeyMap.Map(key) is { } settingsToggle &&
            host.Session.Bindings.Get(InputAction.ToggleSettings).Contains(Binding.Of(settingsToggle)))
        {
            _settings.Visible = !_settings.Visible;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The frame lists, the navmesh overlay, the screenshot and the HUD switch (true: taken).
    /// </summary>
    bool ViewKey(SilkKey key)
    {
        // F11 lists the frame; F12 is the small frame-rate line (the profiler chart waits for Metal timestamps).
        if (key == SilkKey.F11 && _shiftDown)
        {
            host.DebugOverlays.ToggleNavOverlay();
            return true;
        }
        if (key == SilkKey.F11)
        {
            _statsVisible = !_statsVisible;
            return true;
        }
        if (key == SilkKey.F12)
        {
            _frameStatsVisible = !_frameStatsVisible;
            return true;
        }
        if (key is SilkKey.F8 or SilkKey.PrintScreen)
        {
            _screenshotNow = true;
            return true;
        }
        if (key == SilkKey.F7 && _hud is not null)
        {
            _hud.UiVisible = !_hud.UiVisible;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Ctrl+C and Ctrl+V with the free camera: the viewer camera codes (true: taken).
    /// </summary>
    bool CameraCodeKey(Silk.NET.Input.IKeyboard keyboard, SilkKey key, bool ctrl)
    {
        // Ctrl+C copies the free camera's position as a viewer camera code; Ctrl+V flies to the clipboard's code or feedback camera location.
        if (key is SilkKey.C or SilkKey.V && host.Session.Camera.IsFree && _hud is not null && !_hud.UiEditing && ctrl)
        {
            if (key == SilkKey.C)
            {
                string code = WorldCameraCode.Encode(host.Camera);
                keyboard.ClipboardText = code;
                Console.WriteLine($"camera    copied {code} ({WorldCameraCode.Describe(host.Camera)})");
            }
            else if (WorldCameraCode.Decode(ClipboardText.Read(keyboard)) is { } pose)
            {
                Vector3 back = new(MathF.Cos(pose.Pitch) * MathF.Sin(pose.Yaw), MathF.Sin(pose.Pitch),
                    MathF.Cos(pose.Pitch) * MathF.Cos(pose.Yaw));
                host.Session.Camera.TakeOver(new CameraState(pose.Target + back * pose.Distance, -back, Vector3.UnitY, pose.Target,
                    pose.Yaw, pose.Pitch, pose.Distance, true), strategy: false);
                _hud.Follow = null;
                Console.WriteLine("camera    moved to the clipboard's camera code");
            }
            else Console.WriteLine("camera    the clipboard holds no camera code (Ctrl+C in a viewer copies one) or feedback camera location");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Numpad / starts or ends the squad tour (true: taken).
    /// </summary>
    bool TourKey(SilkKey key)
    {
        // Numpad / flies the camera across the squad in portrait order; it holds the camera like the tour on Vulkan.
        if (key == SilkKey.KeypadDivide && !host.Duel.IsDuel && _hud is not null)
        {
            if (_tour.Active) EndTour();
            else
            {
                _hud.Follow = null;
                _tour.HopSeconds = host.Config.CharacterFlightSeconds;
                _tourFollows = false;
                _tour.Start(host.Camera, [.. _hud.PortraitBar.SquadBarMembers().Select(c => c.Id)], TourPosition);
                if (_tour.Active) _tourHolds = true;
                else Console.WriteLine("squad tour: nobody else in the squad to fly to");
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// A mouse's pointer, buttons and wheel.
    /// </summary>
    void HookMouse(Silk.NET.Input.IMouse mouse)
    {
        mouse.MouseMove += OnMouseMove;
        mouse.MouseDown += (_, button) =>
        {
            if (host.Covered) return;
            if (KeyMap.Map(button) is not { } mapped) return;
            var at = Pixels(mouse.Position);
            if (_settings?.Visible == true)
            {
                _settings.MouseDown(at, mapped);
                return;
            }
            bool select = host.PlayerButton(mapped) == Meitou.Game.Host.Input.MouseButton.Left;
            if (_hud is { UiVisible: true } && select && !host.Config.SandboxPanelCollapsed && host.Sandbox.SandboxPicker?.MouseDown(at) == true)
                return;
            if (_hud is { UiVisible: true } && select && host.Sandbox.SandboxToggleClick(at)) return;
            // PlayerInterface sees every click: it takes the HUD's controls, selects, starts a drag box or gives a move order.
            if (_hud is not null && _hud.MouseDown(host.PlayerButton(mapped), at, _shiftDown, Ctrl(), host.HighlightHeld())) return;
            host.Session.Input.SetMouseButton(mapped, true);
        };
        mouse.MouseUp += (_, button) =>
        {
            if (host.Covered) return;
            if (_settings?.Visible == true)
            {
                _settings.MouseUp();
                return;
            }
            if (KeyMap.Map(button) is not { } mapped) return;
            _hud?.MouseUp(host.PlayerButton(mapped), Pixels(mouse.Position), _shiftDown);
            host.Session.Input.SetMouseButton(mapped, false);
        };
        mouse.Scroll += (_, wheel) =>
        {
            if (host.Covered) return;
            if (_settings?.Visible == true)
            {
                _settings.Scroll(wheel.Y);
                return;
            }
            var at = Pixels(mouse.Position);
            if (_hud is { UiVisible: true } && !host.Config.SandboxPanelCollapsed && host.Sandbox.SandboxPicker?.Scroll(wheel.Y) == true) return;
            // A panel's own scroll wins; over any HUD rectangle the wheel does not zoom the camera, as in the Vulkan host.
            if (_hud is not null && (_hud.UiScroll(at, wheel.Y) || _hud.ContainsUi(at))) return;
            host.Session.Input.AddWheel(wheel.Y);
        };
    }

    /// <summary>
    /// The pointer goes to the dialog or the interface; a held orbit turns the camera with the pointer held in place.
    /// </summary>
    void OnMouseMove(Silk.NET.Input.IMouse m, Vector2 p)
    {
        if (host.Covered)
        {
            _lastMouse = p;
            return;
        }
        if (_settings?.Visible == true)
        {
            _settings.MouseMove(Pixels(p));
            _lastMouse = p;
            return;
        }
        bool hold = _lastMouse is not null && host.MouseLook();
        _hud?.MouseMove(Pixels(hold ? _lastMouse!.Value : p));
        // The panels and their drags own the pointer: the camera must not also turn under them.
        if (_hud is { UiCapturesPointer: true })
        {
            _lastMouse = p;
            return;
        }
        if (_lastMouse is { } last) host.Session.Input.AddMouseDelta(p.X - last.X, p.Y - last.Y);
        if (hold)
        {
            host.HoldPointer(m, _lastMouse!.Value);
            return;
        }
        _lastMouse = p;
    }

    /// <summary>
    /// Losing the focus lets go of every key and button.
    /// </summary>
    void OnFocusChanged(bool focused)
    {
        if (focused) return;
        host.Session.Input.ReleaseAll();
        if (_hud is not null) _hud.SlowModifier = false;
        _shiftDown = false;
        _lastMouse = null;
    }

    /// <summary>
    /// The quick save and load and floor keys; panning or the free camera lets go of the followed character.
    /// </summary>
    void OnTicked(ActionState actions)
    {
        if (actions.Pressed(InputAction.QuickSave)) host.Saves.QuickSaveRequested = true;
        if (actions.Pressed(InputAction.QuickLoad)) host.Saves.QuickLoadRequested = true;
        if (actions.Pressed(InputAction.FloorUp)) host.Interiors.StepFloor(1);
        if (actions.Pressed(InputAction.FloorDown)) host.Interiors.StepFloor(-1);
        if (_hud is not null)
        {
            // Panning, or leaving for the free camera, lets go of the character the camera follows; turning and
            // zooming keep it, as in the Vulkan host.
            if (actions.Held(InputAction.MoveForward) || actions.Held(InputAction.MoveBack) ||
                actions.Held(InputAction.MoveLeft) || actions.Held(InputAction.MoveRight) ||
                actions.Pressed(InputAction.ToggleFreeCamera)) _hud.Follow = null;
            if (_tour.Active && GameHost.CameraInput(actions)) EndTour();
        }
    }

    /// <summary>
    /// A window point in the interface's pixels (the framebuffer's, or the render size's while the interface is drawn with a stretched picture).
    /// </summary>
    Vector2 Pixels(Vector2 p)
    {
        var pixels = _window.FramebufferSize;
        var (width, height) = _upscaling.UiSize(pixels.X, pixels.Y);
        return p * new Vector2(width / (float)Math.Max(_window.Size.X, 1), height / (float)Math.Max(_window.Size.Y, 1));
    }

    /// <summary>
    /// Either key is held on any keyboard.
    /// </summary>
    bool Held(SilkKey a, SilkKey b) => _input.Keyboards.Any(k => k.IsKeyPressed(a) || k.IsKeyPressed(b));

    /// <summary>
    /// A Ctrl key is held.
    /// </summary>
    bool Ctrl() => Held(SilkKey.ControlLeft, SilkKey.ControlRight);

    /// <summary>
    /// An Alt key is held.
    /// </summary>
    bool Alt() => Held(SilkKey.AltLeft, SilkKey.AltRight);

    /// <summary>
    /// Metal keeps F10 for its settings; the shared debug menu is on Ctrl+F10 (or another binding, except F10).
    /// </summary>
    bool DebugToggle(SilkKey key) => key == SilkKey.F10
        ? Ctrl()
        : KeyMap.Map(key) is { } mapped && mapped != EngineKey.F10 &&
          host.Session.Bindings.Get(InputAction.ToggleDebugMenu).Contains(Binding.Of(mapped));

    #endregion

    #region Stills

    /// <summary>
    /// A --screenshot run: the simulation freezes after the first frame and the loop saves its pictures.
    /// </summary>
    bool _still;

    /// <summary>
    /// The still's first frame has prepared it.
    /// </summary>
    bool _stillPrepared;

    /// <summary>
    /// The still's panels, one picture each (null: the plain world).
    /// </summary>
    string?[] _stillPanels = [];

    /// <summary>
    /// The panel being pictured.
    /// </summary>
    int _stillPanel;

    /// <summary>
    /// The frames the panel has warmed up.
    /// </summary>
    int _stillPanelFrames;

    /// <summary>
    /// The still's panels, or the one shared layout a run opens by name.
    /// </summary>
    void PrepareStills()
    {
        // The still's selection, --move-to walk and --ticks happen on the first frame: its Settle calls upload textures through
        // the open frame (Metal cannot open one before a drawable exists), and the loop then only lets the picture warm up.
        _still = host.WorldOptions.Screenshot is not null;
        _stillPanels = _hud is not null && _still ? host.ScreenshotScenes.StillPanels() : new string?[] { null };
        // A run can still open one shared layout by name for manual review; the still loop owns its panels.
        if (!_still && _hud is not null && host.GameOptions.UiPanel is { Length: > 0 } selectedPanel
            && selectedPanel is not ("trade" or "talk" or "edited"))
            _hud.ShowUiPanel(selectedPanel is "hud" or "hud-expanded" ? null : selectedPanel, selectedPanel == "hud-expanded");
    }

    /// <summary>
    /// Opens a still's panel (false: it could not open).
    /// </summary>
    bool BeginStillPanel(string? panelName)
    {
        if (panelName == "trade")
        {
            if (host.Player is null || !host.ScreenshotScenes.ShowcaseTrade()) return false;
            // The trade picture shows a stock item's tooltip: the pointer rests on the first item.
            if (host.Player.UiFrame.FirstOrDefault(c => c.Name.StartsWith("trade-item-", StringComparison.Ordinal)
                    && !c.Name.EndsWith("-background", StringComparison.Ordinal)
                    && !c.Name.EndsWith("-icon", StringComparison.Ordinal)) is { } item)
                host.Player.MouseMove(new Vector2(item.Bounds.X + item.Bounds.Width / 2, item.Bounds.Y + item.Bounds.Height / 2));
            return true;
        }
        if (panelName == "talk") return host.Player is not null && host.ScreenshotScenes.ShowcaseTalk();
        if (panelName == "edited" && host.Player is not null)
        {
            host.Player.CharacterEditor.ShowcaseEdit();
            host.Session.AdvanceSimulation(host.Session.Ticks.TickSeconds);
        }
        if (panelName?.StartsWith("editor", StringComparison.Ordinal) == true && _hud is not null)
        {
            _hud.ShowUiPanel(panelName);
            host.CharacterEditor.EditorCamera();
        }
        return true;
    }

    /// <summary>
    /// Shows a still's panel on its first frame.
    /// </summary>
    void ApplyStillPanel(string? panelName)
    {
        if (host.Player is null) return;
        if (host.GameOptions.TitlePreview) host.Player.Frontend.ShowTitle();
        else if (_hud is not null && panelName is not ("trade" or "talk"))
            _hud.ShowUiPanel(panelName is "hud-expanded" or "edited" ? null : panelName, panelName == "hud-expanded");
        if (_hud is not null) host.TitleScreen.ApplyTitleCamera();
    }

    /// <summary>
    /// Keeps a still's panel staged while it warms up.
    /// </summary>
    void KeepStillPanel(string? panelName)
    {
        if (host.Player is null) return;
        if (_hud is not null && panelName?.StartsWith("editor", StringComparison.Ordinal) == true) host.CharacterEditor.EditorCamera();
        if (_hud is not null) host.TitleScreen.ApplyTitleCamera();
    }

    /// <summary>
    /// A still's frame: the panel to picture opened and staged (false: none left).
    /// </summary>
    bool ShowStillPanel(Vector2D<int> size)
    {
        // A still freezes the simulation after PrepareStill: only the panel and the warm-up frames change, never the tick.
        host.TickAhead = false;
        if (_hud is not null)
        {
            _hud.Width = size.X;
            _hud.Height = size.Y;
        }
        bool panelReady = false;
        while (_stillPanel < _stillPanels.Length)
        {
            string? candidate = _stillPanels[_stillPanel];
            if (_stillPanelFrames == 0 && !BeginStillPanel(candidate))
            {
                _stillPanel++;
                continue;
            }
            panelReady = true;
            break;
        }
        if (!panelReady) return false;
        string? panelName = _stillPanels[_stillPanel];
        if (_stillPanelFrames == 0) ApplyStillPanel(panelName);
        KeepStillPanel(panelName);
        return true;
    }

    /// <summary>
    /// Saves the panel's picture once it has warmed up (false: the last one is saved).
    /// </summary>
    bool CaptureStill(GpuTexture picture)
    {
        // A panel's item icons are drawn after the frame that asks for them, and their textures stream in: let a UI still
        // wait for them (up to a bound), while an ordinary world still keeps its 40 frames. The simulation stays frozen.
        int pendingIcons = (_characters?.PendingIcons ?? 0) + (_hud?.ItemCells.PendingIconFiles ?? 0);
        bool pendingStillContent = pendingIcons > 0 || _objects is { StagePropPending: true } || _objects is { PendingBuildingIcons: > 0 };
        if (_still)
        {
            _stillPanelFrames++;
            if (_stillPanelFrames >= 40 && (!pendingStillContent || _stillPanelFrames >= 400))
            {
                string? panelName = _stillPanels[_stillPanel];
                if (_foliage is not null && _stillPanel == _stillPanels.Length - 1)
                {
                    Console.WriteLine($"metal     foliage: {_foliage.DrawnInstances} instances, {_foliage.DrawnBlades} blades, "
                        + $"{_foliage.DrawCalls} calls");
                }
                Capture(_ctx, picture, host.ScreenshotScenes.StillPicturePath(panelName));
                _stillPanel++;
                _stillPanelFrames = 0;
                if (_stillPanel >= _stillPanels.Length) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The backbuffer's picture as a PNG (the readback's rows are the picture's own: Metal draws right side up).
    /// </summary>
    static void Capture(MetalContext ctx, GpuTexture backbuffer, string path)
    {
        int w = backbuffer.Desc.Width, h = backbuffer.Desc.Height;
        var pixels = ctx.Readback(backbuffer);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        PngWriter.Write(path, w, h, pixels);
        Console.WriteLine($"shot      {path} ({w}x{h})");
    }

    #endregion

    #region Frame loop

    /// <summary>
    /// A save load or a new game swapped the world after the first frame: the objects and foliage settle at the new camera.
    /// </summary>
    bool _settleAfterWorldLoad;

    /// <summary>
    /// A frame has been drawn.
    /// </summary>
    bool _worldHasRendered;

    /// <summary>
    /// The loop's clock.
    /// </summary>
    Stopwatch _clock = null!;

    /// <summary>
    /// The previous frame's time on the clock.
    /// </summary>
    double _last;

    /// <summary>
    /// The seconds the frame-rate counter has gathered.
    /// </summary>
    double _fpsWindow;

    /// <summary>
    /// The seconds since the window title was written.
    /// </summary>
    double _titleTimer;

    /// <summary>
    /// When the last trace line was written.
    /// </summary>
    double _traceAt;

    /// <summary>
    /// When the last native trace lines were written.
    /// </summary>
    double _nativeAt;

    /// <summary>
    /// The last frame's wait for a drawable, in milliseconds.
    /// </summary>
    double _lastWait;

    /// <summary>
    /// The last frame's recording time, in milliseconds.
    /// </summary>
    double _lastRecord;

    /// <summary>
    /// The frames drawn.
    /// </summary>
    int _frames;

    /// <summary>
    /// The frames the frame-rate counter has gathered.
    /// </summary>
    int _fpsFrames;

    /// <summary>
    /// The frame-rate line.
    /// </summary>
    string _fpsText = "";

    /// <summary>
    /// MEITOU_METAL_TRACE=1: the loop's trace lines and watchdog.
    /// </summary>
    bool _trace;

    /// <summary>
    /// The stats panel's lines.
    /// </summary>
    readonly List<string> _statsLines = [];

    /// <summary>
    /// When the streamers went quiet (0: busy).
    /// </summary>
    double _streamingIdleSince;

    /// <summary>
    /// When the managed heap was last trimmed.
    /// </summary>
    double _lastManagedTrim = double.NegativeInfinity;

    /// <summary>
    /// The cursor mode the window was last given.
    /// </summary>
    Silk.NET.Input.CursorMode _windowCursor = Silk.NET.Input.CursorMode.Normal;

    /// <summary>
    /// The system cursor's shape.
    /// </summary>
    readonly FrameGenerationSetup.SystemPointer _systemPointer = new();

    /// <summary>
    /// A save load or a new game swapped the world: after the first frame the streamers settle at the new camera.
    /// </summary>
    internal void WorldSettled()
    {
        // A save load or a new game swaps the world after the first frame: force the object and foliage streamers
        // to settle at the new camera, as the first world does at frame 0. Otherwise they can stay empty until the
        // camera moves, even though the characters and the simulation are already there.
        if (_worldHasRendered) _settleAfterWorldLoad = true;
    }

    /// <summary>
    /// The loop's clock and trace, then frames until the window closes or the game quits.
    /// </summary>
    void RunLoop()
    {
        _clock = Stopwatch.StartNew();
        _trace = Environment.GetEnvironmentVariable("MEITOU_METAL_TRACE") == "1";
        if (_trace)
        {
            var watchdog = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(2000);
                    Console.WriteLine($"metal     trace watchdog: main loop at frame {Volatile.Read(ref _frames)}");
                }
            }) { IsBackground = true, Name = "metal trace" };
            watchdog.Start();
        }
        // Loading can take long enough for the terminal that started the game to take focus back: bring the game forward again
        // before the interactive loop (its title and FPS are watched there).
        _display.Activate();
        while (!_window.IsClosing && !_quit)
        {
            _bench.Lap(PlayBench.BenchTail);
            _window.DoEvents();
            if (_window.IsClosing) break;
            if (!Frame()) break;
        }
    }

    /// <summary>
    /// One frame: the world advanced (or the still staged), the shared frame drawn, the overlays, the present and the pictures (false: the run ends).
    /// </summary>
    bool Frame()
    {
        double now = _clock.Elapsed.TotalSeconds, frameStart = now, dt = now - _last;
        _last = now;
        TraceFrame(now, dt);
        // The interface's size: the screen's, or the render size when the Disabled upscaler draws it with the picture.
        _upscaling.Update();
        var screen = _window.FramebufferSize;
        var (uiWidth, uiHeight) = _upscaling.UiSize(screen.X, screen.Y);
        var size = new Vector2D<int>(uiWidth, uiHeight);
        var pointerMouse = _input.Mice.FirstOrDefault();
        var pointerAt = pointerMouse is null ? Vector2.Zero : Pixels(pointerMouse.Position);
        ShowLoadBar();
        CountFps(dt);
        if (!_still && !_bench.BeginFrame(dt)) return false;
        bool highlighting = false;
        if (!_still) highlighting = AdvanceWorld(dt);
        else if (!ShowStillPanel(size)) return false;
        // Ctrl+/ records the next complete frame for Xcode. Start before BeginFrame makes the command buffer and stop
        // after EndFrame commits the present, so the trace holds exactly one frame rather than the three in flight.
        MetalGpuCapture? gpuCapture = StartGpuCapture();
        _bench.Lap(5);

        // A live resize can leave the window without pixels for a moment: wait for the next frame instead of spinning
        // the main thread (the window would stop responding to its own resize).
        bool began = _ctx.BeginFrame();
        if (!began)
        {
            if (gpuCapture is not null)
            {
                // No frame yet: discard the empty capture and ask for the next frame instead.
                gpuCapture.Abort();
                _gpuCaptureRequested = true;
            }
            Thread.Sleep(1);
            return true;
        }
        _bench.FrameBegun();
        SettleBeforeDraw();
        long recordStart = Stopwatch.GetTimestamp();
        // The world draws into the picture: the backbuffer, or while it is stretched a target of the render size.
        var picture = _ctx.Picture;
        var worldSize = new Vector2D<int>(picture.Desc.Width, picture.Desc.Height);
        // The editor's scene: in the Meitou UI the bar, drawn by the world pass below from the editor's camera (CharacterEditorHost); else the
        // original's stage, the character and its platform drawn into the stage texture the interface shows over the world.
        var editorBarFrame = _hud is { CharacterEditor.EditorOpen: true } ? host.CharacterEditor.Bar.BeginEditorBar(worldSize.X, worldSize.Y) : null;
        PrepareDrawInputs(highlighting);
        // The frame's inputs, as WorldFrame.Draw prepares them: the weather at the camera, the atmosphere's tables for the sun and the
        // eye's height, the fog distance the light ends at, and the view distance that keeps the haze complete before the far plane.
        bool titleLook = _hud is not null && host.TitleScreen.TitleVisible;
        float hour = editorBarFrame is not null ? EditorBarScene.EditorBarHour
            : host.PresentationHour ?? (titleLook ? host.TitleScreen.TitleHour : (float)host.Session.Clock.HourOfDay);
        var aspect = size.X / (float)Math.Max(size.Y, 1);
        var eye = host.Camera.Eye;
        var view = host.Camera.View;
        DrawSharedFrame(worldSize, now, titleLook, hour);
        if (editorBarFrame is { } barFrame) host.CharacterEditor.Bar.EndEditorBar(barFrame, worldSize.X, worldSize.Y);
        var shown = ShowPicture(picture);
        // The feedback screenshot: the frame without the menus, the settings, the debug overlays and the pointer (FeedbackCapture).
        bool feedbackShot = host.FeedbackCapture.FeedbackShotRequested && _hud is not null && !_still;
        if (_hud is not null) _hud.FeedbackPanel.ShotWithoutMenus = feedbackShot;
        if (_overlay is not null) DrawOverlays(_overlay, size, dt, eye, view, aspect, shown, feedbackShot, pointerMouse, pointerAt);
        PresentFrame(gpuCapture, recordStart);
        _bench.FramePresented(_lastRecord);
        TrimManagedHeap(now);
        SavePictures(shown, feedbackShot);
        if (!CaptureStill(shown)) return false;
        UpdateTitle(dt);
        // The Vulkan loop's cap: only without vsync (vsync already paces the present); the same 30 fps cap in
        // both comparison runs isolates tearing from the much higher uncapped frame rate.
        if (!_display.VSync && _fpsLimit > 0)
        {
            GameHost.Limit(_clock, frameStart, 1.0 / _fpsLimit);
        }
        if (host.GameOptions.QuitAfter is { } quitAfter && now >= quitAfter) return false;
        return true;
    }

    /// <summary>
    /// MEITOU_METAL_TRACE: the frame's timings each second, the native state and the streaming every five.
    /// </summary>
    void TraceFrame(double now, double dt)
    {
        if (_trace && now - _traceAt >= 1)
        {
            _traceAt = now;
            Console.WriteLine($"metal     trace t={now:0.0}s frame {_frames}, window {_window.Size.X}x{_window.Size.Y} points, " +
                $"framebuffer {_window.FramebufferSize.X}x{_window.FramebufferSize.Y} pixels, cpu {dt * 1000:0.0} ms " +
                $"(drawable {_lastWait:0.0} ms, record {_lastRecord:0.0} ms), gpu {_ctx.LastGpuMilliseconds:0.0} ms");
        }
        if (_trace && now - _nativeAt >= 5)
        {
            _nativeAt = now;
            Console.WriteLine($"metal     native {_display.NativeWindowState()}");
            Console.WriteLine($"metal     resources {_ctx.ResourceSummary()}");
            Console.WriteLine($"metal     streaming terrain {_streamer?.Pending ?? 0} ({(_streamer is null || _streamer.Idle ? "idle" : "busy")}), " +
                $"objects {_objects?.Pending ?? 0}, foliage {_foliage?.Pending ?? 0}, cull groups {_foliage?.LastCullGroups ?? 0}, " +
                $"cull dispatches {_foliage?.GpuCullDispatches ?? 0}");
            if (_streamer is not null) Console.WriteLine($"metal     terrain stream: {_streamer.Describe()}");
            if (_memoryGuard is not null) Console.WriteLine($"metal     memory guard {_memoryGuard.Status}");
            if (_objects is not null) Console.WriteLine($"metal     objects resident: {_objects.ResidentDescription}");
            if (_foliage is not null) Console.WriteLine($"metal     foliage resident: {_foliage.ResidentDescription}");
            if (_characters is not null) Console.WriteLine($"metal     characters: {_characters.Describe()}");
        }
    }

    /// <summary>
    /// The background-loading bar, in the interface's mode.
    /// </summary>
    void ShowLoadBar()
    {
        // The original's background-loading bar follows the interface's mode, as on Vulkan; the Metal host keeps the same shared bar.
        bool faithfulUi = _hud?.UiMode == Meitou.Data.Ui.UiMode.Faithful;
        host.LoadBar.Update(faithfulUi ? host.PendingLoads() : 0);
        if (_hud is not null) _hud.LoadBar = host.LoadBar;
        if (host.Loading is not null) (host.Loading.LoadBar, host.Loading.Faithful) = (host.LoadBar, faithfulUi);
    }

    /// <summary>
    /// The frame-rate counter over the last quarter second.
    /// </summary>
    void CountFps(double dt)
    {
        // The counter is the mean over the last quarter second, so one late frame does not read as a drop; then a frame's work
        // (its time without the waits) and its whole time in brackets.
        _fpsWindow += dt;
        _fpsFrames++;
        if (_fpsWindow >= 0.25)
        {
            _fpsText = string.Create(CultureInfo.InvariantCulture,
                $"{_fpsFrames / _fpsWindow:0} fps  ({_fpsWindow * 1000 / _fpsFrames:0.0} ms)");
            _fpsWindow = 0;
            _fpsFrames = 0;
        }
    }

    /// <summary>
    /// The simulation, the camera and the world's state for this frame (the Highlight key's state).
    /// </summary>
    bool AdvanceWorld(double dt)
    {
        AdvanceSession(dt);
        FollowWithCamera(dt);
        _bench.Lap(3);
        // The pointer's Ctrl/Alt modifiers and the Highlight key (Alt): labels on the ground items near the focus, signs and the hover
        // outlines agree.
        bool ctrlHeld = Ctrl();
        bool altHeld = host.HighlightHeld();
        bool highlighting = altHeld && !host.Covered && !host.TitleScreen.TitleVisible && _hud is { UiVisible: true } &&
            _settings?.Visible != true && !host.Session.Camera.IsFree;
        if (_hud is not null)
        {
            _hud.CtrlHeld = ctrlHeld;
            _hud.AltHeld = altHeld;
            _hud.GroundItemPicks.HighlightFocus = highlighting ? host.Camera.Target : null;
        }
        if (_objects is not null) _objects.HighlightSigns = highlighting;
        UpdateWorldViews(dt);
        return highlighting;
    }

    /// <summary>
    /// Edge scrolling, the duel, the session's ticks, the saves and menus, the scene load and the sky arrival.
    /// </summary>
    void AdvanceSession(double dt)
    {
        // Edge scrolling only while the world owns the pointer (the interface is visible and not dragging a panel).
        host.Session.Camera.Settings.EdgeScroll = host.Config.EdgeScroll != false;
        host.Session.Camera.Strategy.EdgePointer = host.Covered || _hud is not null && host.TitleScreen.TitleVisible || _settings?.Visible == true ||
            _hud is { UiVisible: false } or { UiCapturesPointer: true } ? null : WindowPointer.InWindow(_window);

        // The duel restarts between ticks when Backspace asks or its measured lap ends; its sun then holds at the lap's hour.
        if (_hud is not null) host.Duel.DuelFrame(false);
        AdvanceTicks(dt);
        if (_hud is not null)
        {
            host.WorldNews();
            host.Saves.HandleSaves(dt);
            host.NewGame.HandleNewGame();
            host.NewGame.HandleMainMenu();
        }
        host.StepSceneLoad();
        host.AdvanceSkyArrival(dt);
        _bench.Lap(2);
    }

    /// <summary>
    /// The world's ticks for the frame (none while covered) and the duel's held clock, timed for the play benchmark (its tick profile on).
    /// </summary>
    void AdvanceTicks(double dt)
    {
        if (_bench.Active) host.Session.World.Profile.Enabled = true;
        long started = Stopwatch.GetTimestamp();

        if (!host.Covered) host.Session.Advance(dt);
        if (_hud is not null) host.Duel.DuelHoldClock();

        _bench.Updated(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>
    /// The tour or the rig moves the camera, the editors and the title stage it, and the focus follows.
    /// </summary>
    void FollowWithCamera(double dt)
    {
        // The squad tour and character flights hold the camera while they fly; otherwise the rig owns the view.
        if (!_tourHolds) host.ApplyCamera(host.Session.CameraAt());
        if (_tour.Active && !_tour.Advance(dt, host.Camera, TourPosition)) EndTour();
        host.Gpu.Ahead = _tour.ArrivalEye(TourPosition);
        if (_hud is not null)
            host.Gpu.Kept = host.Config.KeepTexturesAtCharacters
                ? [.. _hud.PortraitBar.SquadBarMembers().Select(c => TourPosition(c.Id)).OfType<Vector3>()] : [];
        if (!_tourHolds && _hud is not null)
        {
            host.Sandbox.SandboxFollowCamera();
            host.Sandbox.FollowCamera();
            host.CharacterEditor.EditorCamera();
        }
        // The merged character editor is shared with Vulkan: a new game opens it once the squad is in the snapshot, and the world resumes
        // when it closes.
        host.CharacterEditor.OpenPendingEditor();
        host.CharacterEditor.ResumeAfterCreation();
        if (_hud is not { CharacterEditor.EditorOpen: true })
        {
            host.CharacterEditor.ClearEditorStage();
            host.CharacterEditor.Bar.ClearEditorBar();
        }
        if (!_tourHolds && _hud is not null) host.TitleScreen.ApplyTitleCamera();
        // A flight puts the focus where it lands, so the towns there populate during the flight; otherwise the
        // followed character's pivot is already in the rig.
        host.ViewAspect = _window.Size.X / (float)Math.Max(_window.Size.Y, 1);
        host.SendFocus(_tour.Active && _tour.Current is { } tourLanding && TourPosition(tourLanding) is { } tourAt ? tourAt
            : _tourHolds ? host.Camera.Target : null);
        // The strategy camera's eye clearance for the followed character and the tour's arcs over hills.
        if (host.SkyArrival is null && _hud is not null && !host.TitleScreen.TitleVisible
            && (_tour.Active || !host.Session.Camera.IsFree && !_tourHolds))
        {
            var clearEye = host.Camera.Eye;
            float shortfall = host.Session.Camera.EyeGroundAt(clearEye.X, clearEye.Z) +
                Meitou.Data.World.KenshiCamera.EyeClearance(host.Camera.Distance) - clearEye.Y;
            // Over a hair only.
            if (shortfall > 1e-3f) host.Camera.Target += new Vector3(0, shortfall, 0);
        }
    }

    /// <summary>
    /// The title battle, the sky arrival, the audio, the navmesh, the buildings, the interior view and the tick ahead.
    /// </summary>
    void UpdateWorldViews(double dt)
    {
        if (!host.Covered && _hud is not null && host.TitleScreen.TitleVisible) host.TitleScreen.TitleBattle?.Advance(dt);
        if (host.SkyArrival is not null)
        {
            host.ApplyCamera(host.Session.Camera.Current);
            if (_hud is not null) host.TitleScreen.ApplyTitleCamera();
            host.ApplySkyArrival();
        }
        if (!host.Covered) host.Sound.UpdateAudio(dt);
        _bench.Lap(4);
        host.UpdateNav();
        host.DebugOverlays.UpdateNavOverlay();
        // Diagnostic isolation for the far-travel freeze: skip the runtime building/ground-item/lamps diff.
        if (Environment.GetEnvironmentVariable("MEITOU_METAL_NO_BUILDINGS") != "1") host.BuildingSync.UpdateBuildings();
        // The interior view (docs/formats/zones.md): the player's building opens, its upper floors and their characters are hidden.
        // It must run before the character draw list (which carries aboveFloor) and before the shadow casters.
        host.Interiors.UpdateInteriorView();
        // The next world tick may run on its own thread while the frame is drawn, as in the Vulkan loop (docs/simulation.md "Tick ahead").
        host.TickAhead = !host.Covered && host.SkyArrival is null && !host.Duel.IsDuel && !host.TitleScreen.TitleVisible
            && !host.Player.CharacterEditor.EditorOpen && host.GameOptions.PlayBench is null && host.TickAheadWanted();
    }

    /// <summary>
    /// Ctrl+/: starts a capture of this frame once the world has drawn one.
    /// </summary>
    MetalGpuCapture? StartGpuCapture()
    {
        MetalGpuCapture? gpuCapture = null;
        if (_gpuCaptureRequested)
        {
            _gpuCaptureRequested = false;
            // Never start during the loading frames: they create and upload most of the world's resources, and a
            // capture that spans them can write many gigabytes before the first picture exists.
            if (_frames < 1)
            {
                Console.WriteLine("metal     gpu capture: wait until the world has drawn a frame, then press Ctrl+/ again");
            }
            else
            {
                gpuCapture = MetalGpuCapture.TryStart(_ctx.Queue, out string? captureError);
                Console.WriteLine(gpuCapture is not null
                    ? $"metal     gpu capture: {gpuCapture.OutputPath}"
                    : $"metal     gpu capture unavailable: {captureError}");
            }
        }
        return gpuCapture;
    }

    /// <summary>
    /// A still's first frame prepares it; a swapped world settles its objects and foliage.
    /// </summary>
    void SettleBeforeDraw()
    {
        if (_still && !_stillPrepared)
        {
            host.ScreenshotScenes.PrepareStill(_ctx, finish: false);
            _stillPrepared = true;
        }
        if (_settleAfterWorldLoad)
        {
            _settleAfterWorldLoad = false;
            Console.WriteLine($"metal     settling objects and foliage at the loaded view");
            _objects?.Settle(host.Camera.Eye);
            _foliage?.Settle(host.Camera.Eye);
        }
    }

    /// <summary>
    /// The first frame's settle, the portrait requests and the hover outlines the shared draw needs.
    /// </summary>
    void PrepareDrawInputs(bool highlighting)
    {
        // The still's objects are all resident before the first picture, as the Vulkan screenshot path settles them (its Settle
        // opens its own frame; here the frame is the first one's).
        if (_frames == 0)
        {
            _objects?.Settle(host.Camera.Eye);
            _foliage?.Settle(host.Camera.Eye);
        }
        // What the shared draw needs before it updates the characters: the squad's portrait requests and the hover masks.
        if (_characters is not null)
        {
            _characters.PortraitRequests.Clear();
            if (_hud is not null)
                foreach (var (member, cell) in _hud.PortraitBar.SquadBarMembers().Select((member, cell) => (member, cell)))
                    _characters.PortraitRequests.Add(new PortraitRequest(host.PortraitKey(member.Id), cell, member.PortraitSerial));
        }
        var hoverKeys = new List<(long Key, Vector4 Colour)>();
        bool outlines = !host.Session.Camera.IsFree && _hud is { UiVisible: true } && !host.Covered && _settings?.Visible != true;
        bool outlining = host.Config.HoverOutline && outlines;
        if (outlining)
        {
            if (highlighting)
                hoverKeys.AddRange(_hud!.PointerPanel.HighlightTargets(host.Camera.Target, PointerView.HighlightRadius)
                    .Select(t => (host.DrawKey(t.Id), t.Colour)));
            else if (_hud!.PointerPanel.HoverTarget is { } hover)
                hoverKeys.Add((host.DrawKey(hover.Id), hover.Colour));
        }
        host.Gpu.HoverKeys = hoverKeys;
        host.Gpu.OutlinedBuildings = outlines && host.Config.BuildingOutline ? _hud!.BuildingSelection.OutlinedBuildings(hover: true) : [];
        host.Gpu.HoverOutline = _hoverOutline;
    }

    /// <summary>
    /// The shared frame draw, with the title's staged look put back after it.
    /// </summary>
    void DrawSharedFrame(Vector2D<int> size, double now, bool titleLook, float hour)
    {
        // The title stages the world as the poster: clear morning light, the sun right of the view, the moons full and the ruin
        // beside the logo. Save the gameplay values and put them back after the world pass.
        var savedClock = host.Scene.Clock;
        var savedWeather = host.Gpu.Weather;
        var savedSkyWeather = _sky.Weather;
        float? savedCloudCoverage = _sky.CloudCoverage;
        var savedCloudWind = _sky.CloudWind;
        var savedPlanetTurn = _sky.PlanetTurn;
        float savedPlanetFill = _sky.PlanetFill;
        var savedFill = _characters?.Fill ?? default;
        bool savedHidePlaced = _fogVolumes.HidePlaced;
        if (titleLook) StageTitleLook(hour);
        // One shared frame: the weather and sky preparation, streaming, shadows, the scene host pass, the post-chain scene
        // effects, the particles and the post chain, then the overlays over the finished picture. The Metal chain fills the
        // seam's Vulkan-only gaps (no reflection, GI, low-resolution particles or fog-rate target).
        PrepareSharedDraw();
        var savedParticles = host.Gpu.Particles;
        // the staged title sky and its own dust, not the world's weather
        if (titleLook)
        {
            host.Gpu.Weather = null;
            host.Gpu.Particles = null;
        }
        if (host.TickAhead) host.Session.BeginTickAhead();
        try
        {
            WorldFrame.Draw(host.Gpu, host.Scene, host.Camera, host.Render, size.X, size.Y, hour, (float)now / 600f, host.WorldOptions.FogDistance);
        }
        finally
        {
            if (host.TickAhead) host.Session.EndTickAhead();
            host.Gpu.Weather = savedWeather;
            host.Gpu.Particles = savedParticles;
            if (titleLook)
            {
                host.Scene.Clock = savedClock;
                _sky.Weather = savedSkyWeather;
                _sky.CloudCoverage = savedCloudCoverage;
                _sky.CloudWind = savedCloudWind;
                _sky.PlanetTurn = savedPlanetTurn;
                _sky.PlanetFill = savedPlanetFill;
                if (_characters is not null) _characters.Fill = savedFill;
                _fogVolumes.HidePlaced = savedHidePlaced;
            }
        }
    }

    /// <summary>
    /// The finished world picture on its way to the screen, and the target the interface is drawn into. A picture that is the backbuffer goes as
    /// it is. A stretched one (the Disabled upscaler below the screen's size in full screen) is copied to the backbuffer with the nearest pixel,
    /// and the interface is drawn over it at the screen's size; or, with the interface at the render size, the interface is drawn into the
    /// picture and the present stretches that.
    /// </summary>
    GpuTexture ShowPicture(GpuTexture picture)
    {
        if (!_ctx.PictureApart) return picture;

        if (_upscaling.UiAtRenderSize)
        {
            _ctx.PresentPicture = true;
            return picture;
        }

        var cmd = (MetalCommandList)_ctx.BeginNative("post stretch");
        _composite.Stretch(cmd, picture, _ctx.Backbuffer);
        _ctx.EndNative(cmd);
        return _ctx.Backbuffer;
    }

    /// <summary>
    /// The title's poster: clear morning light, the sun right of the view and the moons full.
    /// </summary>
    void StageTitleLook(float hour)
    {
        float fill = Math.Clamp(host.Scene.Clock.SunDirection(hour).Y
            / MathF.Max(host.Scene.Clock.SunDirection(host.TitleScreen.TitleHour).Y, 1e-3f), 0, 1);
        fill = fill * fill * (3 - 2 * fill);
        _sky.Weather = Meitou.Data.World.SkyWeather.Default;
        _sky.CloudCoverage = 0.38f;
        _sky.CloudWind = TitleComposition.CloudWind(host.Camera);
        _sky.PlanetTurn = TitleComposition.MoonTurn(host.Camera);
        _sky.PlanetFill = 0.8f * fill;
        host.Scene.Clock = host.Scene.Clock with { SunTurn = TitleComposition.SunTurn(host.Scene.Clock, hour, host.Camera) };
        if (_characters is not null) _characters.Fill = new Vector3(0.064f, 0.052f, 0.04f) * fill;
        _fogVolumes.HidePlaced = true;
    }

    /// <summary>
    /// The game hours, the blood, the mods' hits and the characters' look before the shared draw.
    /// </summary>
    void PrepareSharedDraw()
    {
        host.Gpu.GameHours = host.Session.Clock.HoursSinceStart;
        host.BloodDecals.UpdateBlood();
        host.Modding.UpdateModHits();
        if (_characters is not null)
        {
            _characters.Look = Meitou.Game.Rendering.Characters.PreviewLook.Of(host.WorldOptions.Post);
            if (_hud is not null)
            {
                _hud.PortraitBar.PortraitSize = Meitou.Game.Rendering.Characters.CharacterRenderer.PortraitSizeFor(_characters.Look);
                host.ItemIconFiles.SyncIconLook();
            }
        }
    }

    /// <summary>
    /// The overlays over the finished picture (<paramref name="target"/>, <paramref name="size"/> big), then the loading screen.
    /// </summary>
    void DrawOverlays(DebugOverlay overlay, Vector2D<int> size, double dt, Vector3 eye, Matrix4x4 view, float aspect, GpuTexture target,
        bool feedbackShot, Silk.NET.Input.IMouse? pointerMouse, Vector2 pointerAt)
    {
        overlay.Target = target;
        // One overlay pass: the HUD, the direct HUD lines and the stats/settings text would otherwise each load/store the whole backbuffer.
        DrawOverlayPass(overlay, size, dt, eye, view, aspect, feedbackShot, pointerMouse, pointerAt);
        // The scene changes use the same Kenshi loading screen as Vulkan; it covers the world until the sky arrival is done.
        if (host.Loading is not null && (host.ActiveSceneLoad is not null || host.Loading.Exiting))
        {
            overlay.Target = target;
            host.Loading.Draw(size.X, size.Y);
        }
        else host.Loading?.ReleaseWhenGone();
    }

    /// <summary>
    /// One overlay pass: the HUD, the stats and settings text and the pointer.
    /// </summary>
    void DrawOverlayPass(DebugOverlay overlay, Vector2D<int> size, double dt, Vector3 eye, Matrix4x4 view, float aspect, bool feedbackShot,
        Silk.NET.Input.IMouse? pointerMouse, Vector2 pointerAt)
    {
        using var overlayPass = overlay.BeginPass(size.X, size.Y);
        if (_hud is not null)
        {
            _hud.Width = size.X;
            _hud.Height = size.Y;
            _hud.Eye = eye;
            _hud.ViewProjection = view * host.Camera.Projection(aspect, host.Camera.Near, host.Camera.ViewDistance);
            // The move order under a held right button follows the cursor once a frame, as the Vulkan host's Steer does.
            _hud.Steer();
            if (_hud.UiVisible)
            {
                _hud.Draw(overlay, size.X, size.Y, overlayPass);
                if (host.Sandbox.IsSandbox && !host.GameOptions.UiShowcase) host.Sandbox.DrawSandbox(overlay, size.X, size.Y);
            }
        }
        bool wrote = DrawDebugText(overlay, overlayPass, size, dt, feedbackShot);
        // The mouse pointer over everything, as Vulkan draws it: the system cursor when Hardware mouse is on (enhanced, with the action's
        // pointer beside it and the hand over what a click acts on), else the game's own pointer. The free camera hides it, and saved
        // pictures never contain it.
        if (DrawPointer(overlayPass, size, feedbackShot, pointerMouse, pointerAt)) wrote = true;
        if (wrote) overlay.Flush(size.X, size.Y, null, overlayPass);
    }

    /// <summary>
    /// The stats panel, the frame-rate lines, the debug menu and the settings dialog (true: something was written).
    /// </summary>
    bool DrawDebugText(DebugOverlay overlay, DebugOverlay.OverlayPass overlayPass, Vector2D<int> size, double dt, bool feedbackShot)
    {
        bool wrote = false;
        // The framebuffer is 2x the window's points on a Retina display: the fixed-pixel debug text would be half size.
        float textScale = MathF.Max(1, size.X / (float)Math.Max(_window.Size.X, 1));
        if (_statsVisible && !feedbackShot)
        {
            _statsLines.Clear();
            _statsLines.Add($"{_fpsText}  {dt * 1000:0.00} ms  {ResolutionText()}");
            var session = host.Session;
            var camera = host.Camera;
            _statsLines.Add($"sim {session.Simulation.TotalTicks} ticks  {session.CurrentSnapshot.Characters.Count} characters  "
                + $"x{session.TimeScale:0}  {session.Clock.TimeText}");
            _statsLines.Add($"camera {camera.Target.X:0}, {camera.Target.Y:0}, {camera.Target.Z:0}  yaw {camera.Yaw * 180 / MathF.PI:0}  "
                + $"pitch {camera.Pitch * 180 / MathF.PI:0}  boom {camera.Distance:0}");
            if (_characters is not null) _statsLines.Add(_characters.Statistics());
            if (_foliage is not null)
                _statsLines.Add($"foliage {_foliage.DrawnInstances} instances, {_foliage.DrawnBlades} blades, {_foliage.DrawCalls} calls");
            overlay.Panel(size.X, size.Y, "Metal frame (F11 hides this)", _statsLines,
                (_hud is { UiVisible: true } ? _hud.HudBottom : 0) + 8, textScale);
            wrote = true;
        }
        if (_frameStatsVisible && !feedbackShot)
        {
            overlay.TextScaled("Meitou - Metal", 16 * textScale, 16 * textScale, DebugOverlay.TextColour, textScale);
            overlay.TextScaled($"{_frames + 1} frames  {_fpsText}  {dt * 1000:0.00} ms  {ResolutionText()}", 16 * textScale,
                (16 + overlay.LineHeight) * textScale, DebugOverlay.TextColour, textScale);
            wrote = true;
        }
        if (host.DebugMenu.DebugMenuOpen && _hud is not null && !feedbackShot)
        {
            host.DebugMenu.DrawDebugMenu(overlay, size.X, size.Y, (_hud.UiVisible ? _hud.HudBottom : 0) + 8, "Ctrl+F10");
            wrote = true;
        }
        if (_settings?.Visible == true && !feedbackShot)
        {
            _settings.Draw(size.X, size.Y, overlayPass);
            wrote = true;
        }
        return wrote;
    }

    /// <summary>
    /// The stats lines' sizes: what the scene renders at (an upscaler's input, or the stretched picture's) and the screen's.
    /// </summary>
    string ResolutionText()
    {
        var screen = _window.FramebufferSize;
        return $"render {_ctx.SceneWidth}x{_ctx.SceneHeight}  screen {screen.X}x{screen.Y}";
    }

    /// <summary>
    /// The mouse pointer over everything: the system cursor or the game's own (true: the game's was drawn).
    /// </summary>
    bool DrawPointer(DebugOverlay.OverlayPass overlayPass, Vector2D<int> size, bool feedbackShot, Silk.NET.Input.IMouse? pointerMouse,
        Vector2 pointerAt)
    {
        bool wrote = false;
        if (!_still && !_screenshotNow && !feedbackShot && pointerMouse is not null && _hud is not null)
        {
            bool pointerWorld = !host.Covered && !host.GameOptions.NoPopulation && _settings?.Visible != true && !host.TitleScreen.TitleVisible;
            bool freeLook = pointerWorld && host.Session.Camera.IsFree;
            var cursorMode = host.Config.HardwareMouse && !freeLook ? Silk.NET.Input.CursorMode.Normal : Silk.NET.Input.CursorMode.Hidden;
            if (cursorMode != _windowCursor) pointerMouse.Cursor.CursorMode = _windowCursor = cursorMode;
            string pointerName = pointerWorld ? _hud.PointerPanel.PointerName : "arrow";
            bool enhanced = host.Config.HardwareMouse && host.Config.EnhancedHardwareMouse && !freeLook;
            bool hand = enhanced
                && (pointerWorld ? _hud.PointerPanel.PointsAtClickable(pointerName) : _settings?.OverClickable(pointerAt) == true);
            _systemPointer.Standard(pointerMouse.Cursor, hand ? Silk.NET.Input.StandardCursor.Hand : Silk.NET.Input.StandardCursor.Default);
            if (!host.Config.HardwareMouse && !freeLook)
            {
                _hud.PointerPanel.DrawPointer(pointerName, pointerAt, size.X, size.Y, overlayPass);
                wrote = true;
            }
            else if (enhanced && pointerName != "arrow")
            {
                _hud.PointerPanel.DrawPointerBeside(pointerName, pointerAt, size.X, size.Y, overlayPass);
                wrote = true;
            }
        }
        return wrote;
    }

    /// <summary>
    /// Commits the frame with the present, stops a GPU capture and counts the frame.
    /// </summary>
    void PresentFrame(MetalGpuCapture? gpuCapture, long recordStart)
    {
        _ctx.EndFrame();
        if (gpuCapture is not null)
        {
            gpuCapture.Stop();
            Console.WriteLine($"metal     gpu capture written: {gpuCapture.OutputPath}");
        }
        // Mark the covered frame: the next one may run the next stage (which can hold the thread).
        if (host.ActiveSceneLoad is { } pendingLoad) pendingLoad.Shown = true;
        _lastWait = _display.LastDrawableWaitMilliseconds;
        _lastRecord = Stopwatch.GetElapsedTime(recordStart).TotalMilliseconds - _lastWait;
        _frames++;
        _worldHasRendered = true;
    }

    /// <summary>
    /// Compacts the managed heap once the streamers have been quiet (at most once a minute) or past 4 GB.
    /// </summary>
    void TrimManagedHeap(double now)
    {
        // Once the terrain, object and foliage streamers have been quiet for two seconds, the large decode/upload
        // buffers are garbage. Compact at that idle point (at most once a minute) so the LOH does not keep them
        // for the rest of the session. Active streaming is never interrupted.
        bool streamingIdle = (_streamer?.Idle ?? true) && (_objects?.Pending ?? 0) == 0 && (_foliage?.Pending ?? 0) == 0;
        if (!streamingIdle) _streamingIdleSince = 0;
        else if (_streamingIdleSince == 0) _streamingIdleSince = now;
        var managedBefore = GC.GetGCMemoryInfo();
        bool managedHighWater = managedBefore.HeapSizeBytes > 4L << 30;
        if ((streamingIdle && now - _streamingIdleSince >= 2 || managedHighWater) && now - _lastManagedTrim >= 60)
        {
            _lastManagedTrim = now;
            var before = managedBefore;
            // Compacting the LOH is a stop-the-world move of every large object: on a 4+ GB heap it stalled
            // this loop for 1.6 s. Do it where a loading screen or a menu covers the pause, or when the heap
            // has grown past 4 GB; otherwise a background collection is enough and does not move live arrays.
            bool safePoint = host.TitleScreen.TitleVisible || _settings?.Visible == true || host.ActiveSceneLoad is not null
                || host.Loading?.Exiting == true;
            bool compact = safePoint || managedHighWater;
            if (compact)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
            else GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false);
            var after = GC.GetGCMemoryInfo();
            long loh = after.GenerationInfo.Length > 3 ? after.GenerationInfo[3].SizeAfterBytes : 0;
            if (_trace)
                Console.WriteLine($"metal     idle trim ({(compact ? "compacting" : "background")}): " +
                    $"heap {before.HeapSizeBytes / 1048576} -> {after.HeapSizeBytes / 1048576} MB, LOH {loh / 1048576} MB");
        }
    }

    /// <summary>
    /// The feedback screenshot and an F8 screenshot.
    /// </summary>
    void SavePictures(GpuTexture backbuffer, bool feedbackShot)
    {
        if (feedbackShot)
        {
            _hud!.FeedbackPanel.ShotWithoutMenus = false;
            var picture = _ctx.Readback(backbuffer);
            for (int i = 3; i < picture.Length; i += 4) picture[i] = 255;
            host.FeedbackCapture.FeedbackCaptured(picture, backbuffer.Desc.Width, backbuffer.Desc.Height);
        }
        if (_screenshotNow)
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (pictures.Length == 0) pictures = Directory.GetCurrentDirectory();
            string file = Path.Combine(Directory.CreateDirectory(pictures).FullName, $"meitou-metal-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            Capture(_ctx, backbuffer, file);
            _screenshotNow = false;
        }
    }

    /// <summary>
    /// The window title: the frame rate, the camera and the game clock, twice a second.
    /// </summary>
    void UpdateTitle(double dt)
    {
        _titleTimer += dt;
        if (_titleTimer >= 0.5)
        {
            var s = host.Session.Camera.Current;
            _window.Title = $"Meitou (metal) | {_fpsText} | {(host.Session.Camera.IsFree ? "free camera" : $"boom {s.Distance:0}")} | " +
                $"{host.Session.Clock.TimeText} {host.Session.Clock.DayText} | {s.Target.X:0}, {s.Target.Z:0}";
            _titleTimer = 0;
        }
    }

    #endregion
}
#endif
