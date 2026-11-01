#if MEITOU_METAL
// Metal exists on macOS only; the file is compiled only there (the csproj's MEITOU_METAL constant).
#pragma warning disable CA1416
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Numerics;
using Meitou.Data.Particles;
using Meitou.Data.Save;
using Meitou.Data.Textures;
using Meitou.Data.World;
using Meitou.Engine;
using Meitou.Engine.Cameras;
using Meitou.Engine.Input;
using Meitou.Engine.Time;
using Meitou.Metal;
using Meitou.Metal.Gi;
using Meitou.Rendering;
using Meitou.Rendering.Characters;
using Meitou.Rendering.Display;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Editing;
using Meitou.Simulation.Saving;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using EngineKey = Meitou.Engine.Input.Key;
using SilkKey = Silk.NET.Input.Key;

namespace Meitou.Game;

sealed partial class GameHost
{
    bool settleAfterWorldLoad;
    bool worldHasRendered;
    /// <summary>
    /// The Metal path (macOS): the game boots without the Vulkan renderer and draws the world through the seam's recording model
    /// (native segments around the scene's host pass, guests inside it, the frame committed with the present). The loop is
    /// interactive: the strategy camera and the HUD's selection/orders read the window's input, while the frontend, audio and the
    /// remaining host services wait (docs/metal-backend.md).
    /// </summary>
    int Metal()
    {
        if (g.PlayBench is not null)
        {
            Console.Error.WriteLine("metal     --play-bench is Vulkan-only (its late-pipeline and display timings are Vulkan's).");
            return 2;
        }
        metalRenderer = true;
        // The banks and the output device load on a worker while the window, the world and the renderers are made.
        BeginAudio();
        var loaded = LoadScene is not null ? LoadScene() : scene;
        if (loaded is null)
        {
            Console.Error.WriteLine("metal     the world could not be read");
            return 1;
        }
        if (LoadScene is not null) scene = ownedScene = loaded;

        // Same setting as VulkanDisplay: Metal used to always present with display sync, even under --no-vsync.
        bool vsync = g.VSync ?? config.VSync;
        int fpsLimit = g.FpsLimit ?? config.FpsLimit;
        using var display = new MetalDisplay(WindowOptions.Default with
        {
            Size = new Vector2D<int>(o.Width, o.Height),
            Title = "Meitou (metal)",
            FramesPerSecond = 0,
            UpdatesPerSecond = 0,
        }, vsync);
        Console.WriteLine($"metal     vsync {(display.VSync ? "on" : "off")}, frame cap {(fpsLimit > 0 ? $"{fpsLimit} fps" : "off")}");
        using var ctx = new MetalContext(display);
        // The Metal path has no single GPU allocator with a global budget: every cache has its own high-water mark.
        // Travelling through the world can fill the object, foliage and character caches at the same time, so keep the
        // whole device allocation under a conservative share of what Metal recommends. The guard pauses/evicts caches
        // and shortens the ranges as the ceiling approaches, exactly as it does on Vulkan.
        ulong memoryBudget = ctx.VideoMemoryBudget > 0 ? Math.Min(ctx.VideoMemoryBudget, 5UL << 30) : 5UL << 30;
        if (ulong.TryParse(Environment.GetEnvironmentVariable("MEITOU_METAL_GPU_BUDGET_MB"), NumberStyles.None,
                CultureInfo.InvariantCulture, out ulong budgetMb) && budgetMb > 0)
            memoryBudget = budgetMb << 20;
        bool guardEnabled = Environment.GetEnvironmentVariable("MEITOU_VRAM_GUARD") != "0";
        VramGuard? memoryGuard = null;
        if (guardEnabled) Console.WriteLine($"metal     memory guard: {memoryBudget / 1048576} MB of GPU allocation");
        if (budgetMb > 5120)
            Console.WriteLine($"warning   metal GPU budget {budgetMb} MB is above the 5120 MB default; full screen and a busy world can exhaust the 16 GB M1 Pro. Use 5120 unless profiling.");
        var window = display.Window;
        // The shared window setting: borderless full screen as the Vulkan path starts; --windowed keeps the window for one run.
        bool fullScreen = config.FullScreen && !o.Windowed;
        void ToggleFullScreen()
        {
            BorderlessFullScreen.Toggle(window);
            display.SetFullScreenPresentation(BorderlessFullScreen.IsOn(window));
        }
        if (fullScreen) ToggleFullScreen();
        else display.SetFullScreenPresentation(false);
        ctx.Samplers.MaxAnisotropy = o.Anisotropy;
        // Same scale as the Vulkan path's TAA/upscaler setting: the scene is rendered smaller and an upscaler reconstructs it.
        // The Metal default is MetalFX Balanced at 0.50; Native at 1.00 is the fallback when MetalFX is unavailable.
        // MEITOU_METAL_NO_TAA is a diagnostic view: native size, no jitter and no temporal resolve.
        bool noTaaRequested = Environment.GetEnvironmentVariable("MEITOU_METAL_NO_TAA") is "1" or "true";
        // `--upscaler metalfx` is the command-line form; MEITOU_METALFX stays usable for launch scripts.
        bool metalFxRequested = !noTaaRequested && (o.Post.Upscale.Kind == UpscalerKind.MetalFx ||
            Environment.GetEnvironmentVariable("MEITOU_METALFX") is "1" or "true");
        bool noTaa = false;
        bool spatial = false;

        // Safe Metal defaults while the foliage mesh path and the cloud-shadow path are being fixed: both are opt-in.
        bool metalFoliage = Environment.GetEnvironmentVariable("MEITOU_METAL_FOLIAGE") != "0";
        bool metalCloudShadows = Environment.GetEnvironmentVariable("MEITOU_METAL_CLOUD_SHADOWS") != "0";
        bool metalWater = Environment.GetEnvironmentVariable("MEITOU_METAL_WATER") != "0";
        bool metalShadows = Environment.GetEnvironmentVariable("MEITOU_METAL_SHADOWS") != "0";
        bool metalImpostors = Environment.GetEnvironmentVariable("MEITOU_METAL_NO_IMPOSTORS") != "1";
        if (!metalFoliage) Console.WriteLine("metal     foliage disabled (MEITOU_METAL_FOLIAGE=0)");
        if (!metalCloudShadows) Console.WriteLine("metal     cloud shadows disabled (MEITOU_METAL_CLOUD_SHADOWS=0)");
        if (!metalWater) Console.WriteLine("metal     water disabled (MEITOU_METAL_WATER=0)");
        if (!metalShadows) Console.WriteLine("metal     shadows disabled (MEITOU_METAL_SHADOWS=0)");
        if (!metalImpostors) Console.WriteLine("metal     impostors disabled (MEITOU_METAL_NO_IMPOSTORS=1)");
        // The world's renderers are built by the shared WorldFrame path after the display/upscaler setup. Metal's post chain
        // (PostComposite/PostTaa and the MetalFX adapters) sits behind IWorldPostChain, so WorldFrame.Draw owns the frame.
        // The final composite through the game's tone map: the scene draws into an HDR target, then this writes the LDR picture.
        var composite = new PostComposite(ctx, flipY: true);
        composite.LoadHeatHaze(assets);
        var taa = new PostTaa(ctx);
        // MetalFX temporal is the default at every preset scale; spatial is an explicit alternative.
        var metalFx = MetalFxUpscaler.TryCreate(ctx);
        var metalFxSpatial = MetalFxSpatialUpscaler.TryCreate(ctx);
        MetalUpscalerMode mode = config.MetalUpscaler;
        float scale = Math.Clamp(config.MetalRenderScale, 0.33f, 1f);
        if (noTaaRequested)
        {
            mode = MetalUpscalerMode.Native;
            scale = 1f;
        }
        else if (Environment.GetEnvironmentVariable("MEITOU_METALFX_SPATIAL") is "1" or "true")
        {
            mode = MetalUpscalerMode.Spatial;
            scale = 0.4f;
        }
        else if (metalFxRequested)
        {
            mode = MetalUpscalerMode.Temporal;
            // --upscaler metalfx without a scale means the balanced default; an explicit --render-scale wins.
            scale = o.Post.Upscale.Scale is null ? 0.5f : o.Post.Upscale.EffectiveScale;
        }
        else if (o.Post.Upscale.Explicit)
            scale = o.Post.Upscale.EffectiveScale;

        if (mode == MetalUpscalerMode.Temporal && metalFx is null)
        {
            Console.WriteLine("metal     MetalFX temporal unavailable; using Native");
            mode = MetalUpscalerMode.Native;
            scale = 1f;
        }
        if (mode == MetalUpscalerMode.Spatial && metalFxSpatial is null)
        {
            Console.WriteLine("metal     MetalFX spatial unavailable; using " + (metalFx is null ? "Native" : "MetalFX temporal"));
            mode = metalFx is null ? MetalUpscalerMode.Native : MetalUpscalerMode.Temporal;
            scale = mode == MetalUpscalerMode.Native ? 1f : scale;
        }
        if (mode == MetalUpscalerMode.Native) scale = 1f;
        // 0.33 is below what MetalFX temporal can run reliably on this device (it crashed before the first frame);
        // the lowest shipped MetalFX tier is 0.40.
        if (mode is MetalUpscalerMode.Temporal or MetalUpscalerMode.Spatial) scale = Math.Clamp(scale, 0.4f, 1f);
        else scale = Math.Clamp(scale, 0.33f, 1f);
        ctx.RenderScale = scale;

        void ApplyMode(MetalUpscalerMode next)
        {
            mode = next;
            noTaa = mode == MetalUpscalerMode.Native;
            spatial = mode == MetalUpscalerMode.Spatial;
            taa.External = mode switch
            {
                MetalUpscalerMode.Temporal => metalFx,
                MetalUpscalerMode.Spatial => metalFxSpatial,
                _ => null,
            };
            config.MetalUpscaler = mode;
        }
        ApplyMode(mode);
        if (noTaa) Console.WriteLine("metal     no TAA/upscaler: native scene, no jitter");
        // The seam's post chain for the shared WorldFrame.Draw: the scene effects and the composite are the same calls the local
        // loop used, with the jitter and temporal choice decided live as the settings change them.
        var postChain = new MetalPostChain(ctx, composite, taa, o.Post,
            noTaa: () => noTaa,
            temporal: () => mode is MetalUpscalerMode.Temporal or MetalUpscalerMode.Taa,
            jitter: (frame, sceneWidth, displayWidth) => noTaa || spatial ? Vector2.Zero : PostTaa.Offset(frame, sceneWidth, displayWidth));

        int CurrentPresetIndex()
        {
            if (mode == MetalUpscalerMode.Native) return 0;
            if (mode == MetalUpscalerMode.Temporal && MathF.Abs(ctx.RenderScale - 0.77f) < 0.005f) return 1;
            if (mode == MetalUpscalerMode.Temporal && MathF.Abs(ctx.RenderScale - 0.67f) < 0.005f) return 2;
            if (mode == MetalUpscalerMode.Temporal && MathF.Abs(ctx.RenderScale - 0.59f) < 0.005f) return 3;
            if (mode == MetalUpscalerMode.Temporal && MathF.Abs(ctx.RenderScale - 0.50f) < 0.005f) return 4;
            if (mode == MetalUpscalerMode.Temporal && MathF.Abs(ctx.RenderScale - 0.40f) < 0.005f) return 5;
            if (mode == MetalUpscalerMode.Spatial && MathF.Abs(ctx.RenderScale - 0.50f) < 0.005f) return 6;
            if (mode == MetalUpscalerMode.Spatial && MathF.Abs(ctx.RenderScale - 0.40f) < 0.005f) return 7;
            if (mode == MetalUpscalerMode.Taa) return 8;
            return 9;   // a legacy/custom scale; the preset row is the only way to change a MetalFX scale
        }
        void SetPreset(int index)
        {
            MetalUpscalerMode next = index switch
            {
                0 => MetalUpscalerMode.Native,
                1 or 2 or 3 or 4 or 5 => MetalUpscalerMode.Temporal,
                6 or 7 => MetalUpscalerMode.Spatial,
                8 => MetalUpscalerMode.Taa,
                _ => mode,
            };
            float nextScale = index switch
            {
                0 => 1f,
                1 => 0.77f,
                2 => 0.67f,
                3 => 0.59f,
                4 => 0.50f,
                5 => 0.40f,
                6 => 0.50f,
                7 => 0.40f,
                8 => 0.50f,
                _ => ctx.RenderScale,
            };
            SetRenderScaleForPreset(next, nextScale);
        }
        void SetRenderScaleForPreset(MetalUpscalerMode next, float nextScale)
        {
            if (next == MetalUpscalerMode.Temporal && metalFx is null)
            {
                Console.WriteLine("metal     MetalFX temporal is unavailable on this device");
                next = MetalUpscalerMode.Native;
                nextScale = 1f;
            }
            if (next == MetalUpscalerMode.Spatial && metalFxSpatial is null)
            {
                Console.WriteLine("metal     MetalFX spatial is unavailable on this device");
                next = metalFx is null ? MetalUpscalerMode.Native : MetalUpscalerMode.Temporal;
                nextScale = next == MetalUpscalerMode.Native ? 1f : nextScale;
            }
            ApplyMode(next);
            ctx.RenderScale = Math.Clamp(nextScale, next is MetalUpscalerMode.Temporal or MetalUpscalerMode.Spatial ? 0.4f : 0.33f, 1f);
            config.MetalRenderScale = ctx.RenderScale;
        }
        string UpscalerStatusText() => mode switch
        {
            MetalUpscalerMode.Native => "Native",
            MetalUpscalerMode.Temporal => $"MetalFX temporal {ctx.RenderScale:0.00}",
            MetalUpscalerMode.Spatial => $"MetalFX spatial {ctx.RenderScale:0.00}",
            _ => $"TAA {ctx.RenderScale:0.00} (experimental)",
        };
        bool openSettingsForStill = Environment.GetEnvironmentVariable("MEITOU_METAL_SETTINGS") is "1" or "true";
        Console.WriteLine($"metal     upscaler {mode.ToString().ToLowerInvariant()} at {ctx.RenderScale:0.00}");
        // The text/quad painter, shared by the HUD and the optional frame-stats lines. It is created even for a screenshot:
        // the game's HUD is part of the picture. MEITOU_METAL_OVERLAY=1 adds the stats lines to a still.
        var overlay = DebugOverlay.TryCreate(ctx);
        bool showFrameStats = o.Screenshot is null || Environment.GetEnvironmentVariable("MEITOU_METAL_OVERLAY") == "1";
        display.Activate();
        if (BorderlessFullScreen.IsOn(window)) display.SetFullScreenPresentation(true);
        Splash?.Dispose();

        // The world's renderers through the shared construction path: terrain, textures, streamer, sky and weather,
        // particles, lamps, objects, foliage, faithful water, shadows and the memory guard. The step callback is
        // unused here because Metal has no separate Vulkan loading-frame loop.
        // --crowd means simulated characters (SpawnCrowd) here too; the posed harness is only for a renderer test.
        crowd = o.Crowd;
        o.Crowd = 0;
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
        var worldGpu = WorldFrame.CreateGpu(ctx, install, scene, assets, o, interactive: true,
            terrainTextures: !terrainTexturesOff, particlesEnabled: metalParticles, objectLoadBudget: 0,
            vramSample: guardEnabled ? () => ((ulong)ctx.Device.CurrentAllocatedSize, memoryBudget) : null);
        worldGpu.PostChain = postChain;
        gpu = worldGpu;
        // The combat aftermath's renderers and store exist before a loaded save's decals are read and before the first frame.
        SetUpBlood(ctx);
        // Native Metal GI is experimental and opt-in: on this M1 Pro it could freeze the machine during a save load.
        // Keep the unfinished path out of normal launches; --no-gi still wins, and F10 can turn it off after it is on.
        bool metalGi = o.Gi && Environment.GetEnvironmentVariable("MEITOU_METAL_GI") is "1" or "true";
        if (metalGi && ctx.Device.SupportsRaytracing)
        {
            worldGpu.ProbeGi = new MetalProbeGi(ctx) { Enabled = o.GiProbes, InteriorEnabled = o.InteriorGi };
            Console.WriteLine($"gi        {worldGpu.ProbeGi.Describe()} (experimental, MEITOU_METAL_GI=1)");
        }
        else if (metalGi)
            Console.WriteLine("gi        metal off: the device does not support ray tracing");
        else if (o.Gi)
            Console.WriteLine("gi        metal off: experimental (set MEITOU_METAL_GI=1 to enable)");
        memoryGuard = worldGpu.Guard;
        var sky = worldGpu.Sky;
        var weather = worldGpu.Weather;
        var fogVolumes = worldGpu.FogVolumes!;
        var shadow = worldGpu.Shadow!;
        var streamer = worldGpu.Streamer;
        var objects = worldGpu.Objects;
        var foliage = worldGpu.Foliage;
        var water = worldGpu.Water;
        var lamps = worldGpu.Lamps;
        // The diagnostic opt-outs keep their old meaning now that the shared path owns construction.
        if (!metalFoliage) { foliage?.Dispose(); worldGpu.Foliage = null; foliage = null; }
        if (!metalWater) { water?.Dispose(); worldGpu.Water = null; water = null; }
        if (!metalShadows) shadow.Enabled = false;
        if (!metalCloudShadows) sky.CloudShadows = false;
        if (!metalImpostors) foliage?.DisableImpostors();
        if (foliage is not null)
        {
            foliage.SwaySeconds = 0;   // a still picture: the grass holds still, so it repeats
            foliage.DrawGrass = Environment.GetEnvironmentVariable("MEITOU_METAL_NO_GRASS") != "1";
            foliage.DrawMeshes = Environment.GetEnvironmentVariable("MEITOU_METAL_NO_FOLIAGE_MESHES") != "1";
            // The CPU cull is the reference path (and the A/B diagnostic for the Metal GPU cull). The GPU cull is
            // opt-in on Metal after the 2026-10-23 progress timeouts; `MEITOU_METAL_CPU_CULL=1` still forces CPU.
            bool gpuCullWanted = Environment.GetEnvironmentVariable("MEITOU_METAL_GPU_CULL") is "1" or "true";
            foliage.GpuCull = gpuCullWanted && Environment.GetEnvironmentVariable("MEITOU_METAL_CPU_CULL") != "1";
            Console.WriteLine(foliage.GpuCull
                ? "warning   metal foliage GPU cull is experimental (MEITOU_METAL_GPU_CULL=1): it has produced GPU progress timeouts after a few minutes"
                : "metal     foliage CPU cull (GPU cull off by default; MEITOU_METAL_GPU_CULL=1 enables the experimental path)");
        }
        // The buildings' interior masks (docs/render-interior-cut.md): the depth range the terrain, foliage and map features are cut by
        // while a viewed building is open. The renderer is backend-neutral; Metal draws it into its own target before the scene.
        InteriorMaskRenderer? interiorMask = objects is null ? null : new InteriorMaskRenderer(ctx, objects);
        worldGpu.InteriorMask = interiorMask;
        fallbackWeather = weather;
        hostLamps = lamps;
        hostObjects = objects;
        hostFoliage = foliage;

        // The simulation's side, using the same CPU height grid as the shared renderer.
        var heights = new Meitou.Data.World.GroundHeights(scene.Window, scene.Coarse, scene.CoarseSize, WorldFrame.CoarseStep);
        hostGround = heights.HeightAt;
        var (walkability, systems, constants) = BootSimulation(heights, interactive: true);
        (camera, render) = WorldFrame.Setup(scene, o);
        // Terrain shader A/B: the no-normal program is a compile-time variant, so the option must be fixed before the first draw.
        if (Environment.GetEnvironmentVariable("MEITOU_TERRAIN_NO_NORMAL") is "1" or "true")
        {
            render.NormalMaps = false;
            Console.WriteLine("metal     terrain normal maps off (MEITOU_TERRAIN_NO_NORMAL=1)");
        }
        session = new WorldSession(scene.Focus, (scene.X0, scene.Z0, scene.X1, scene.Z1), heights.HeightAt, o.Hour, config.TickRate,
            clock: GameClockFor(constants ??= scene.Database is { } clockDb ? Meitou.Data.Gameplay.GameConstants.FromDatabase(clockDb) : null, o.Hour, o.Day ?? GameClock.DefaultStartDay),
            simulation: worldSettings = new Meitou.Simulation.WorldSettings { Seed = g.Seed, MinPartitionSize = 128, Threads = Math.Max(1, config.SimThreads ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 8)) },
            systems: systems, walkability: walkability);
        render.Grid = IsSandbox;
        session.Camera.Place(new Vector2((float)(o.X ?? scene.Focus.X), (float)(o.Z ?? scene.Focus.Z)), (float)(o.Yaw ?? 30) * MathF.PI / 180,
            (float)(o.Pitch ?? Meitou.Data.World.KenshiCamera.InitialPitchDegrees) * MathF.PI / 180,
            (float)(o.Distance ?? Meitou.Data.World.KenshiCamera.InitialDistance));
        var startTarget = session.Camera.Current.Target;
        // The saved key bindings feed the same settings dialog and camera/order input as Vulkan.
        foreach (var problem in session.Bindings.Apply(config.Bindings)) Console.Error.WriteLine($"config    binding skipped: {problem}");
        // The game's new-game shortcut: the player's squad in the simulation, which the world draw list then shows.
        if (start is not null && population is not null)
        {
            try
            {
                playerSquad = population.StartPlayer(session.World, start);
                var lead = session.World.Characters.Previous[playerSquad.Leader.Slot].Position;
                startTarget = lead;
                Console.WriteLine($"metal     player squad: {playerSquad.Members.Count} characters, leader at {lead.X:0}, {lead.Z:0}");
                // --new-game is a shortcut past the menus: the rolled characters are kept (the menu's New game opens the editor);
                // a still keeps creation open for the showcase's editor pictures.
                if (o.Screenshot is null) session.World.Commands.Enqueue(new Meitou.Simulation.Editing.FinishCreation { Tick = session.World.Tick });
            }
            catch (InvalidOperationException e) { Console.WriteLine($"metal     player squad skipped: {e.Message}"); }
        }
        // The population's zones: the background towns activate the zones round the player, and a walker's path may cross one
        // that has to be built first. The interactive loop keeps this table current (UpdateNav), as the Vulkan host does.
        if (nav is not null && population is not null)
        {
            population.ZoneGate = zone => nav.Walkability.World.Contains(zone);
            var around = playerSquad is not null ? session.World.Characters.Previous[playerSquad.Leader.Slot].Position : session.Focus;
            var zones = Meitou.Simulation.ZoneActivation.ZonesAround(around, 0);
            var watch = Stopwatch.StartNew();
            Task.WaitAll([.. zones.Select(z => (Task)nav.LoadZone(z))]);
            foreach (var z in zones) navZones[z] = Stopwatch.GetTimestamp();
            Console.WriteLine($"metal     navmesh {zones.Count} zone(s) round the start ready in {watch.ElapsedMilliseconds} ms");
        }
        ApplyCamera(session.Camera.Current);
        SendFocus();
        // Characters: WorldFrame.CreateGpu already made the `--crowd` harness; otherwise the live loop supplies the
        // simulation's snapshot list. The player interface is not booted here, so the source has no editor previews/portraits.
        CharacterRenderer? characters = worldGpu.Characters;
        if (characters is not null) Console.WriteLine($"metal     crowd ready ({characters.Assets} appearances)");
        else if (scene.Database is { } characterDb && !g.NoPopulation)
        {
            characters = new CharacterRenderer(ctx, install, characterDb, assets, characterRigs) { Source = FillWorldDrawList, SettleFirst = true, LoadBudget = 0, GeneratedLod = o.CharacterLod, SkinShading = o.SkinShading, SkinRaces = config.SkinRaces, EnhancedIcons = o.MeitouIcons, PoseClock = () => realTime.Elapsed.TotalSeconds };
            worldGpu.Characters = characters;
            Console.WriteLine("metal     characters ready (population draw list)");
        }
        hostCharacters = characters;
        if (characters is not null)
        {
            characters.Guard = memoryGuard;
            characters.InteriorObjects = objects;
        }
        // The squad tour and character flights (docs/viewer.md "Squad tour"). Metal has no cinema, so a held camera is
        // only the tour's; EndTour hands the view back to the rig as the Vulkan host does.
        var tour = new SquadTour();
        bool tourHolds = false, tourFollows = false;
        Vector3? TourPosition(CharacterId id) =>
            session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is { } c ? DrawnPosition(c) : null;
        void EndTour()
        {
            tour.Stop();
            tourHolds = false;
            session.Camera.TakeOver(new CameraState(camera.Eye, camera.Forward, Vector3.UnitY, camera.Target, camera.Yaw, camera.Pitch,
                camera.Distance, true), strategy: true);
            if (tourFollows && tour.Current is { } id) player.Follow = id;
        }
        // The game's HUD and panels. PlayerInterface is the interface's data/layout owner; ArrangeUi/BindUi and the UiPainter
        // work with the session the loop has. Only the panels' pointer/keyboard controls are wired here so far.
        PlayerInterface? hud = null;
        HoverOutlineRenderer? hoverOutline = null;
        MarkerRenderer? moveMarker = null;
        NavmeshOverlayRenderer? navOverlay = null;
        if (overlay is not null)
        {
            hud = new PlayerInterface(session, heights.HeightAt, DrawnPosition) { SpeedUnit = config.SpeedUnit, Floaters = config.Floaters, FeedbackButtonInPlay = config.FeedbackButton, PortraitMenu = config.PortraitMenu };
            player = hud;
            hud.LoadUi(g.UiMode ?? config.UiMode, assets.Find);
            hud.CreateUiPainter(overlay, assets.Find);
            ConfigurePlayerInterface(hud);
            if (IsSandbox)
                hud.ReservedKeys.UnionWith([Meitou.Engine.Input.Key.Z, Meitou.Engine.Input.Key.X, Meitou.Engine.Input.Key.C,
                    Meitou.Engine.Input.Key.Y, Meitou.Engine.Input.Key.P, Meitou.Engine.Input.Key.G]);
            // A double click on a far squad member flies there (Advanced settings "Character flights"); the third press
            // ends the flight on that member. With the setting off PlayerInterface centres on them instead.
            hud.FlyTo = id =>
            {
                if (!config.CharacterFlights || IsDuel || session.Camera.IsFree || TourPosition(id) is not { } at) return false;
                if (new Vector2(at.X - camera.Target.X, at.Z - camera.Target.Z).Length() <= UserConfig.FlightDistance) return false;
                hud.Follow = null;
                tour.HopSeconds = config.CharacterFlightSeconds;
                tour.Fly(camera, id, TourPosition);
                (tourHolds, tourFollows) = (true, true);
                return true;
            };
            hud.SkipFlight = id =>
            {
                if (!tour.Active || !tourFollows || tour.Current != id) return;
                tour.Finish(camera, TourPosition);
                EndTour();
            };
            if (characters is not null)
            {
                // The HUD now owns the editor previews and portraits: use the same draw list as Vulkan instead of the
                // interface-less list, or the editor stage never shows a race, gender or slider change.
                characters.Source = FillDrawList;
                var (atlas, sampled) = characters.PortraitAtlas;
                hud.SetUiTexture(PlayerInterface.PortraitTexture, atlas, sampled);
            }
            ConnectItemIcons();
            hud.ConfigureFrontend(scene.Database,
                () => SaveCatalog.Scan(QuickSave.DefaultRoot, install.SaveDirectory,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kenshi", "save")),
                assets.Find("credits.txt") is { } creditsFile ? File.ReadAllText(creditsFile) : "");
            // The picks the interface asks the host for: the drawn character meshes, the buildings' floors and their closed shells.
            hud.MeshHit = (id, origin, direction) => characters?.RayHit(DrawKey(id), origin, direction);
            hud.Floor = (() => viewedFloor, StepFloor);
            hud.BuildingMoveHit = (origin, direction, distance) => objects?.MoveHit(origin, direction, viewedFloor, distance);
            hud.SurfaceMoveHit = SurfaceMoveHit;
            hud.ClosedBuildingHit = (origin, direction, distance) => objects?.ClosedBuildingHit(origin, direction, distance);
            hud.HiddenInWorld = hiddenIndoors.Contains;
            hud.ShownInteriorHit = (origin, direction, distance) => objects?.ShownInteriorHit(origin, direction, distance);
            hud.AboveViewedFloor = AboveViewedFloor;
            hud.GroundItemShape = GroundShape;
            hud.GroundItemHidden = hiddenGround.Contains;
            hud.DoorShape = (record, door) => nav?.Walkability.DoorGeometry?.Shape(record, door) ?? [];
            hoverOutline = new HoverOutlineRenderer(ctx);
            moveMarker = MarkerRenderer.Create(ctx, assets.Find);
            hud.Markers = moveMarker;
            navOverlay = new NavmeshOverlayRenderer(ctx);
            navOverlayRenderer = navOverlay;
            // The shared frame draw owns the overlays after the post chain now.
            worldGpu.Markers = moveMarker;
            worldGpu.NavmeshOverlay = navOverlay;
            worldGpu.HoverOutline = hoverOutline;
            Console.WriteLine($"metal     HUD ready ({hud.UiMode})");
        }
        // The content test modes, in the Vulkan boot's order: a sandbox on the flat floor, the duel on its measured
        // spot, then a simulated crowd round the start. The duel restarts a world, so it needs the HUD's player state.
        if (IsSandbox && population is not null) startTarget = SandboxSpawn();
        else if (IsDuel && population is not null)
        {
            if (hud is not null) startTarget = DuelStart();
            else Console.Error.WriteLine("duel      skipped: the Metal host has no interface/player state");
        }
        if (!IsSandbox && crowd > 0 && population is not null) SpawnCrowd(startTarget, crowd);
        var startRig = session.Camera;
        startRig.Place(new Vector2(startTarget.X, startTarget.Z), (o.Yaw ?? 30) * MathF.PI / 180,
            (o.Pitch ?? Meitou.Data.World.KenshiCamera.InitialPitchDegrees) * MathF.PI / 180,
            o.Distance ?? Meitou.Data.World.KenshiCamera.InitialDistance);
        SendFocus();
        ApplyCamera(session.Camera.Current);
        // --new-game is a shortcut past the creation editor; the same follow the editor's close gives a menu New Game.
        if (hud is not null && start is not null && StartSave is null && o.Screenshot is null && playerSquad is { Leader.IsNone: false } squad)
            hud.Follow = squad.Leader;
        // The camera rotation slider changes the same StrategyCamera settings the Vulkan host changes.
        void ApplyCameraRotateSpeed(float speed) => session.Camera.Settings.RotateSpeedX = session.Camera.Settings.RotateSpeedY =
            Math.Clamp(speed, UserConfig.MinCameraRotateSpeed, UserConfig.MaxCameraRotateSpeed);
        ApplyCameraRotateSpeed(config.CameraRotateSpeed);
        config.EdgeScroll ??= Meitou.Audio.AudioSystem.Setting(install, "Edge scrolling", 1) != 0;
        config.Tutorials ??= Meitou.Audio.AudioSystem.Setting(install, "tutorials", 1) != 0;
        tutorials ??= new TutorialManager(Path.Combine(Directory.GetCurrentDirectory(), TutorialManager.FileName));
        tutorials.Enabled = config.Tutorials == true;
        hud?.ConfigureTutorials(tutorials);
        // The shared settings layout: Metal supplies the settings it can change live (window, upscaler, distances,
        // shadows, sky and the composite); the Interface/Controls/Advanced/Audio pages are the game's own.
        SettingsPanel? settingsValues = null;
        SettingsMenu? settings = null;
        if (overlay is not null && hud is not null)
        {
            settingsValues = CreateMetalSettings();
            settings = new SettingsMenu(hud, settingsValues, session.Bindings, SaveSettings,
                new Slider("Frame limit", 0, 360, () => fpsLimit, v => fpsLimit = (int)MathF.Round(v / 5) * 5, "0",
                    Text: v => v <= 0 ? "Unlimited" : $"{v:0} fps"), config)
            {
                UpscalerStatus = UpscalerStatusText,
                GameplayChanged = ApplyGameplay,
                AttackSlotsChanged = slots => { foreach (var combat in session.World.Systems.OfType<Meitou.Simulation.Combat.CombatSystem>()) combat.AttackSlots = slots; },
                LateDefencesChanged = on => { foreach (var combat in session.World.Systems.OfType<Meitou.Simulation.Combat.CombatSystem>()) combat.LateDefences = on; },
                DoorsCloseChanged = on => { foreach (var locks in session.World.Systems.OfType<Meitou.Simulation.Locks.LockSystem>()) locks.DoorsCloseBehind = on; },
                TutorialsChanged = on => { if (tutorials is { } t) t.Enabled = on; },
                ResetTutorials = () => tutorials?.Reset(),
                OffscreenChanged = () =>
                {
                    foreach (var population in session.World.Systems.OfType<Meitou.Simulation.PopulationSystem>())
                        (population.KeepRoamers, population.OffscreenFights, population.OffscreenHealing, population.CampaignAncestorsOnce, population.KeepWild, population.ResidentSchedule) =
                            (config.KeepRoamers, config.OffscreenFights, config.OffscreenHealing, config.CampaignAncestorsOnce, config.KeepWild, config.ResidentSchedule);
                    foreach (var production in session.World.Systems.OfType<Meitou.Simulation.Production.ProductionSystem>()) production.UnloadedProduction = config.UnloadedProduction;
                },
                CameraRotateSpeedChanged = ApplyCameraRotateSpeed,
                AudioChanged = AudioSettingsChanged,
            };
            // The saved graphics values are the same keys the Vulkan panel uses. The Metal display choices have their own
            // config fields, and a command-line upscaler/scale stays explicit for this run.
            var metalDisplayKeys = new HashSet<string>(StringComparer.Ordinal)
            {
                "VSync", "Full screen", SettingsMenu.MetalPresetSlider, WorldFrame.UpscalerSliders[1],
            };
            foreach (var slider in settingsValues.Sliders)
                if (!RunOnlyKeys().Contains(slider.Label) && !metalDisplayKeys.Contains(slider.Label) &&
                    config.Graphics.TryGetValue(slider.Label, out float value))
                    slider.Set(Math.Clamp(value, slider.Min, slider.Max));
            foreach (var toggle in settingsValues.Toggles)
                if (!SettingsMenu.HiddenSwitch(toggle.Label) && !RunOnlyKeys().Contains(SettingsMenu.SwitchKeyPrefix + toggle.Label) && config.Graphics.TryGetValue(SettingsMenu.SwitchKeyPrefix + toggle.Label, out float value) && (value >= 0.5f) != toggle.Get())
                    toggle.Set(value >= 0.5f);
            settings.ApplySavedPreset();
            // The impostor atlas path is re-enabled after the frame-slot fence. Keep the diagnostic off switch as a
            // fallback; the saved "Far impostors" toggle is otherwise allowed through.
            if (foliage is not null && !metalImpostors) foliage.DisableImpostors();
            // The saved Cloud shadows and Shadow distance settings can re-enable the safe-mode suspects after the sky and the
            // shadow pass were made; keep both off until the black-terrain path is fixed, unless the diagnostic env asks.
            if (!metalCloudShadows) sky.CloudShadows = false;
            if (!metalShadows) shadow.Enabled = false;
            if (openSettingsForStill) settings.Visible = true;
        }
        // The loading screen is backend-neutral: the same Kenshi layout, picture and tips the Vulkan host draws.
        if (overlay is not null)
        {
            try
            {
                loading = LoadingScreen.Create(overlay, assets.Find, install.DataDirectory,
                    (g.UiMode ?? config.UiMode) == Meitou.Data.Ui.UiMode.Faithful);
            }
            catch (Exception e) { Console.Error.WriteLine($"loading   the loading screen did not load: {e.Message}"); }
        }
        bool quit = false;
        // The title opens unless a start, a save or a picture chose the world; --title-preview opens it for a still.
        bool showTitle = hud is not null && (g.TitlePreview || o.Screenshot is null && start is null && StartSave is null && !IsSandbox && !IsDuel && !g.NoPopulation);
        if (hud is not null)
        {
            hud.ScreenAction += e =>
            {
                if (e.Action == "save") saveRequestedFolder = SaveCatalog.Folder(QuickSave.DefaultRoot, e.Text);
                else if (e.Action == "load") loadRequestedFolder = e.Text;
                else if (e.Action == "new-game") newGameRequested = e.Text;
                else if (e.Action.StartsWith("feedback-", StringComparison.Ordinal)) FeedbackAction(e);
                else if (e.Action == "main-menu") mainMenuRequested = true;
                else if (e.Widget == "OptionsButton" && settings is not null) settings.Visible = true;
                else if (e.Widget == "ExitGameButton" || e.Action == "exit") quit = true;
            };
            if (showTitle) hud.ShowTitle();
        }
        // --load starts in the save: the same swap the menu's load uses, with the world already read around its camera.
        if (hud is not null && StartSave is { } startSave)
        {
            readingFolder = g.Load ?? "";
            try
            {
                var navmesh = SwapInSave(Task.FromResult(startSave), interactive: true, out string summary);
                navmesh.Wait();
                SettleNewWorld();
                hud.FollowSelected();
                FinishLoadNotice(summary);
                ApplyCamera(session.Camera.Current);
            }
            catch (SceneLoadFailed e)
            {
                Console.Error.WriteLine($"load      {e.Message}");
                hud.Notice(e.Message, true);
            }
        }
        // The audio device is up before the first frame; the title theme starts here when the menu is open.
        AttachAudio();
        SettingsPanel CreateMetalSettings()
        {
            var sliders = new List<Slider>();
            var toggles = new List<Toggle>();
            // Display: VSync, full screen, the MetalFX scale preset and filtering. MetalFX presets own the render scale,
            // so there is no separate scale slider. The frame limit is added by SettingsMenu itself.
            sliders.Add(new Slider("VSync", 0, 1, () => display.VSync ? 1 : 0, v => display.VSync = v >= 0.5f,
                Text: v => v >= 0.5f ? "on" : "off (uncapped)"));
            sliders.Add(new Slider("Full screen", 0, 1, () => BorderlessFullScreen.IsOn(window) ? 1 : 0,
                v => { if (v >= 0.5f != BorderlessFullScreen.IsOn(window)) ToggleFullScreen(); },
                Text: v => v >= 0.5f ? "borderless" : "window"));
            sliders.Add(new Slider(SettingsMenu.MetalPresetSlider, 0, 9, () => CurrentPresetIndex(), v =>
            {
                int index = (int)MathF.Round(v);
                if (index is >= 0 and <= 8) SetPreset(index);
            }, "0"));
            sliders.Add(new Slider(WorldFrame.FilteringSlider, 0, 4, () => MathF.Log2(ctx.Samplers.MaxAnisotropy),
                v => ctx.Samplers.MaxAnisotropy = 1 << (int)MathF.Round(v), "0",
                Text: v => (1 << (int)MathF.Round(v)) + "x"));
            sliders.Add(new Slider(WorldFrame.TextureQualitySlider, 0, TextureQuality.Maximum, () => TextureQuality.Level,
                v => TextureQuality.Level = (int)MathF.Round(v), "0", Text: v => TextureQuality.Labels[Math.Clamp((int)MathF.Round(v), 0, TextureQuality.Maximum)]));

            // World: the distances the Metal renderers actually read every frame.
            if (objects is not null)
            {
                sliders.Add(new Slider("Object draw distance", 1000, 400000, () => objects.ObjectDistance, v => objects.ObjectDistance = v, "0", Logarithmic: true,
                    Faithful: Enhancements.FaithfulObjectDistance, Meitou: Enhancements.MeitouObjectDistance));
                if (objects.HasLandmarks)
                    sliders.Add(new Slider("Landmark distance (Meitou)", 1000, 400000, () => objects.LandmarkDistance,
                        v => objects.LandmarkDistance = MathF.Round(v / 100) * 100, "0", Logarithmic: true));
                sliders.Add(new Slider("Distant towns (zones)", 0, ObjectRanges.MaxDistantTownRangeZones * 10,
                    () => objects.DistantRange / Meitou.Data.World.WorldLayout.ZoneSize,
                    v => objects.DistantRange = MathF.Round(v) * Meitou.Data.World.WorldLayout.ZoneSize, "0"));
                sliders.Add(new Slider("Object LOD distance x", 0.25f, 4, () => 1 / objects.LodBias,
                    v => objects.LodBias = 1 / v, "0.00", Logarithmic: true));
            }
            if (foliage is not null)
            {
                sliders.Add(new Slider("Foliage draw distance x", 0.25f, 80, () => foliage.RangeSetting,
                    v => foliage.RangeSetting = v, "0.00", Logarithmic: true));
                sliders.Add(new Slider("Grass draw distance x", 0.25f, 80, () => foliage.GrassRangeSetting,
                    v => foliage.GrassRangeSetting = v, "0.00", Logarithmic: true));
                sliders.Add(new Slider("Grass density x", 0.1f, 2, () => foliage.GrassDensitySetting,
                    v => foliage.GrassDensitySetting = v, "0.00"));
                sliders.Add(new Slider("Large foliage range (F6 Meitou)", 1000, 120000, () => foliage.LargeRange,
                    v => foliage.LargeRange = MathF.Round(v / 50) * 50, "0", Logarithmic: true));
                sliders.Add(new Slider("Medium foliage range", 400, 80000, () => foliage.MediumRange,
                    v => foliage.MediumRange = MathF.Round(v / 50) * 50, "0", Logarithmic: true));
                sliders.Add(new Slider("Small foliage range", 200, 40000, () => foliage.SmallRange,
                    v => foliage.SmallRange = MathF.Round(v / 50) * 50, "0", Logarithmic: true));
                sliders.Add(new Slider("Foliage LOD distance x (lod Meitou)", 0.25f, 8,
                    () => FoliageRenderer.DefaultLodTolerance / foliage.LodTolerance,
                    v => foliage.LodTolerance = FoliageRenderer.DefaultLodTolerance / v, "0.00", Logarithmic: true));
                toggles.Add(new Toggle("Foliage ranges", () => foliage.MeitouRange, v => foliage.MeitouRange = v));
                toggles.Add(new Toggle("Far impostors", () => foliage.Impostors, v => foliage.Impostors = v));
                sliders.Add(new Slider("Impostor distance (F7 Meitou)", 500, 40000, () => foliage.ImpostorDistance,
                    v => foliage.ImpostorDistance = MathF.Round(v / 50) * 50, "0", Logarithmic: true));
                sliders.Add(new Slider("Large impostor distance (F7 Meitou)", 500, 40000, () => foliage.LargeImpostorDistance,
                    v => foliage.LargeImpostorDistance = MathF.Round(v / 50) * 50, "0", Logarithmic: true));
            }
            sliders.Add(new Slider("Terrain detail: error px (less = finer)", 1, 32, () => render.TerrainPixelError,
                v => render.TerrainPixelError = v, "0.0", Logarithmic: true,
                Faithful: WorldRenderOptions.DefaultTerrainPixelError, Meitou: Enhancements.MeitouTerrainPixelError));

            // Lighting: the same shadow controls the Vulkan settings expose, and the sky switches the Metal sky reads.
            const float shadowsOff = Meitou.Data.World.KenshiShadows.MinRange * 0.9f;
            sliders.Add(new Slider("Shadow distance (game 1k-9k, 0 off)", shadowsOff, 200000,
                () => shadow.Enabled ? shadow.Settings.Range : shadowsOff, v =>
                {
                    shadow.Enabled = v >= Meitou.Data.World.KenshiShadows.MinRange;
                    if (shadow.Enabled) shadow.Settings = shadow.Settings with { Range = MathF.Round(v / 100) * 100 };
                }, "0", Logarithmic: true, Text: v => shadow.Enabled ? v.ToString("0", CultureInfo.InvariantCulture) : "0 (off)",
                Faithful: Meitou.Data.World.KenshiShadows.DefaultRange, Meitou: Enhancements.MeitouShadowRange));
            sliders.Add(new Slider("Shadow map size (game quality 0-2)", 0, 2,
                () => Array.IndexOf(Meitou.Data.World.KenshiShadows.QualitySizes, shadow.Settings.MapSize),
                v => shadow.Settings = shadow.Settings with { MapSize = Meitou.Data.World.KenshiShadows.MapSize((int)MathF.Round(v)) }, "0",
                Text: v => Meitou.Data.World.KenshiShadows.MapSize((int)MathF.Round(v)).ToString(CultureInfo.InvariantCulture) + "²"));
            if (shadow.Meitou)
            {
                sliders.Add(new Slider("Shadow filter 0-2 (Meitou; 0 low-end)", 0, 2, () => shadow.FilterQuality,
                    v => shadow.FilterQuality = (int)MathF.Round(v), "0"));
                toggles.Add(new Toggle("Shadow cascade 5 (Meitou)", () => shadow.MeitouCascades >= 5,
                    v => shadow.MeitouCascades = v ? 5 : 4));
            }
            sliders.Add(new Slider("Haze strength (1 = game)", 0, 3, () => sky.HazeStrength,
                v => sky.HazeStrength = v, "0.00", Faithful: 1, Meitou: Enhancements.MeitouHazeStrength));
            sliders.Add(new Slider("Planetshine strength (x physical)", 0, 200, () => sky.PlanetshineStrength,
                v => sky.PlanetshineStrength = MathF.Round(v), "0"));
            sliders.Add(new Slider("Ambient light strength (1 = game)", 0, 3, () => sky.AmbientStrength,
                v => sky.AmbientStrength = MathF.Round(v * 20) / 20, "0.00"));
            sliders.Add(new Slider("Night ambient light strength (1 = game)", 0, 5, () => sky.NightAmbientStrength,
                v => sky.NightAmbientStrength = MathF.Round(v * 20) / 20, "0.00"));
            if (worldGpu.ProbeGi is { } probeGi)
            {
                toggles.Add(new Toggle("Indirect light", () => probeGi.Enabled, v => probeGi.Enabled = v));
                toggles.Add(new Toggle("Indoor GI", () => probeGi.InteriorEnabled, v => probeGi.InteriorEnabled = v));
            }
            toggles.Add(new Toggle("Cloud shadows", () => sky.CloudShadows, v => sky.CloudShadows = v));
            toggles.Add(new Toggle("Night air", () => sky.NightAir, v => sky.NightAir = v));
            toggles.Add(new Toggle("Planetshine", () => sky.Planetshine, v => sky.Planetshine = v));
            toggles.Add(new Toggle("Night sky", () => sky.MeitouStars, v => sky.MeitouStars = v));
            toggles.Add(new Toggle("Clouds", () => sky.LitClouds, v => sky.LitClouds = v));

            // The Meitou character, icon and audio switches from the Vulkan panel, which the shared renderers read.
            if (characters is not null)
            {
                toggles.Add(new Toggle("Skin scattering", () => characters.SkinShading, v => characters.SkinShading = v));
                toggles.Add(new Toggle("Enhanced item icons", () => characters.EnhancedIcons, v => characters.EnhancedIcons = v));
            }
            // Ground relief (docs/render-terrain.md): the Tab panel's quality, depth, parallax distance and toggle.
            if (GroundRelief.Enabled)
            {
                sliders.Add(new Slider(WorldFrame.GroundReliefQualitySlider, 0, 2, () => GroundRelief.Quality,
                    v => GroundRelief.Quality = Math.Clamp((int)MathF.Round(v), 0, 2), "0",
                    Text: v => GroundRelief.QualityName((int)MathF.Round(v))));
                sliders.Add(new Slider("Ground relief depth (cm, typical ground)", 0, 40, () => GroundRelief.Depth * 10,
                    v => GroundRelief.Depth = MathF.Round(v * 2) / 20, "0.0"));
                sliders.Add(new Slider("Ground parallax distance (m)", 10, 150, () => GroundRelief.ParallaxDistance / 10,
                    v => GroundRelief.ParallaxDistance = MathF.Round(v) * 10, "0"));
                toggles.Add(new Toggle("Ground parallax", () => GroundRelief.ParallaxActive, v => GroundRelief.ParallaxActive = v, OnOff: true));
            }

            // Water reflection: the same game setting and reflection range the Vulkan panel exposes; the reflection pass is seam-neutral.
            if (worldGpu.Reflection is { } reflection)
            {
                sliders.Add(new Slider(WorldFrame.WaterReflectionSlider, 0, 4, () => reflection.Level,
                    v => reflection.Level = (int)MathF.Round(v), "0",
                    Text: v => (int)MathF.Round(v) switch { 0 => "0 off", 1 => "1 landscape", 2 => "2 characters", 3 => "3 buildings", _ => "4 everything" }));
                sliders.Add(new Slider("Reflection range x (game 0.6)", 0.1f, 50, () => reflection.Range,
                    v => reflection.Range = v, "0.00", Logarithmic: true));
            }
            // Water: the Meitou clarity controls only mean something on the Meitou surface; the faithful surface keeps them hidden.
            if (water is { Meitou: true })
            {
                sliders.Add(new Slider("Water clarity x (Meitou)", 0.25f, 8, () => water.Clarity,
                    v => water.Clarity = v, "0.00", Logarithmic: true));
                sliders.Add(new Slider("Far water clarity (Meitou)", 0, 1, () => water.FarClarity,
                    v => water.FarClarity = v, "0.00", Text: v => v <= 0 ? "0 (true slant)" : v.ToString("0.00", CultureInfo.InvariantCulture)));
                sliders.Add(new Slider("Water see-through distance (Meitou, game 4000)", 1000, 100000,
                    () => water.ClearDistance, v => water.ClearDistance = MathF.Round(v / 100) * 100, "0", Logarithmic: true));
            }

            // Effects: the composite already supports the game's exposure/tone-map/grading path, even before the rest of
            // the Vulkan post chain is ported.
            sliders.Add(new Slider("Tone map (0 = game)", 1, 3, () => (int)o.Post.ToneMap,
                v => o.Post.ToneMap = (ToneMapOperator)(int)MathF.Round(v), "0",
                Text: v => (ToneMapOperator)(int)MathF.Round(v) switch
                {
                    ToneMapOperator.Shoulder => "shoulder",
                    ToneMapOperator.Aces => "ACES",
                    ToneMapOperator.Hybrid => "hybrid (clamp + ACES)",
                    _ => "clamp (as the game)",
                }));
            sliders.Add(new Slider("Hybrid tone map: ACES share", 0, 1, () => o.Post.ToneMix,
                v => o.Post.ToneMix = MathF.Round(v * 20) / 20, "0.00"));
            sliders.Add(new Slider("Grading (0 = game)", 0, 1, () => o.Post.Grade ? 1 : 0,
                v => o.Post.Grade = v >= 0.5f,
                Text: v => v >= 0.5f ? $"saturation {PostOptions.Saturation:0.00}, contrast {PostOptions.Contrast:0.00}" : "off (as the game)"));
            sliders.Add(new Slider("Heat haze strength (1 = game)", 0, 3, () => o.Post.HeatHazeStrength,
                v => o.Post.HeatHazeStrength = MathF.Round(v * 20) / 20, "0.00", Faithful: 1, Meitou: Enhancements.MeitouHeatHazeStrength));
            sliders.Add(new Slider(WorldFrame.ParticleSliders[0], 0, 4,
                () => WorldFrame.WeatherParticlesSlider(worldGpu.WeatherParticles, o.Post), v =>
                {
                    int i = (int)MathF.Round(v);
                    worldGpu.WeatherParticles = i > 0;
                    if (i > 0) (o.Post.LowResParticles, o.Post.ParticleDivisor) = i switch { 1 => (true, 4), 2 => (true, 2), 3 => (true, 0), _ => (false, 0) };
                }, "0"));
            sliders.Add(new Slider(WorldFrame.ParticleSliders[1], 0.1f, 1, () => worldGpu.ParticleDensity,
                v => worldGpu.ParticleDensity = MathF.Round(v * 20) / 20, "0.00"));
            sliders.Add(new Slider("Night grading strength (0 = game)", 0, 1, () => o.Post.NightGradeStrength,
                v => o.Post.NightGradeStrength = MathF.Round(v * 20) / 20, "0.00"));
            sliders.Add(new Slider("Light shafts strength", 0, 1, () => o.Post.ShaftStrength,
                v => o.Post.ShaftStrength = MathF.Round(v * 20) / 20, "0.00"));
            sliders.Add(new Slider("Light shafts air (0 = none)", 0, 4, () => o.Post.ShaftAir,
                v => o.Post.ShaftAir = MathF.Round(v * 20) / 20, "0.00"));
            sliders.Add(new Slider("Light shafts air at dawn and dusk (x)", 0, 6, () => o.Post.ShaftAirDawn,
                v => o.Post.ShaftAirDawn = MathF.Round(v * 10) / 10, "0.0"));
            sliders.Add(new Slider(WorldFrame.ShaftQualitySlider, 0, 2, () => o.Post.ShaftQuality,
                v => o.Post.ShaftQuality = Math.Clamp((int)MathF.Round(v), 0, 2), "0",
                Text: v => (int)MathF.Round(v) switch { 0 => "Low", 1 => "Medium", _ => "High" }));
            toggles.Add(new Toggle("Dithering", () => o.Post.Dither, v => o.Post.Dither = v));
            var actions = new List<PanelAction>();
            if (foliage?.ImpostorDiskCache is { } cache)
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
            return new SettingsPanel(overlay!, "Settings   (Tab hides this)", sliders, toggles, "Metal switches", actions, null, null);
        }
        // The real input path: the window's keys and pointer go to PlayerInterface first (its panels, builds and the world's
        // selection/order clicks), then to the session's bindings so the strategy camera pans, turns and zooms.
        using var input = Silk.NET.Input.InputWindowExtensions.CreateInput(window);
        if (hud is not null)
        {
            hud.ClipboardRead = () => input.Keyboards.Count > 0 ? input.Keyboards[0].ClipboardText : null;
            hud.ClipboardWrite = text => { if (input.Keyboards.Count > 0) input.Keyboards[0].ClipboardText = text; };
        }
        bool shiftDown = false, statsVisible = false, frameStatsVisible = showFrameStats, screenshotNow = false, gpuCaptureRequested = false;
        Vector2? lastMouse = null;
        var windowCursor = Silk.NET.Input.CursorMode.Normal;
        Vector2 Pixels(Vector2 p) => p * new Vector2(window.FramebufferSize.X / (float)Math.Max(window.Size.X, 1),
            window.FramebufferSize.Y / (float)Math.Max(window.Size.Y, 1));
        bool Held(SilkKey a, SilkKey b) => input.Keyboards.Any(k => k.IsKeyPressed(a) || k.IsKeyPressed(b));
        bool Ctrl() => Held(SilkKey.ControlLeft, SilkKey.ControlRight);
        bool Alt() => Held(SilkKey.AltLeft, SilkKey.AltRight);
        // Metal keeps F10 for its settings; the shared debug menu is on Ctrl+F10 (or another binding, except F10).
        bool DebugToggle(SilkKey key) => key == SilkKey.F10
            ? Ctrl()
            : KeyMap.Map(key) is { } mapped && mapped != EngineKey.F10 &&
              session.Bindings.Get(InputAction.ToggleDebugMenu).Contains(Binding.Of(mapped));
        foreach (var keyboard in input.Keyboards)
        {
            keyboard.KeyDown += (keyboard, key, _) =>
            {
                if (key is SilkKey.ShiftLeft or SilkKey.ShiftRight) { shiftDown = true; if (hud is not null) hud.SlowModifier = true; }
                if (key == SilkKey.Enter && Alt()) { ToggleFullScreen(); return; }
                if (Covered)
                {
                    if (key is SilkKey.Escape or SilkKey.Space) skyArrival?.Skip();
                    return;
                }
                bool ctrl = Ctrl();
                // Ctrl+/ records one frame for Xcode's Metal debugger; it works over the settings dialog too.
                if (ctrl && (key is SilkKey.Slash or SilkKey.KeypadDivide)) { gpuCaptureRequested = true; return; }
                // The full settings dialog (Metal's F10 used to be a preset overlay; the preset now lives in Display).
                if (settings?.Visible == true)
                {
                    if ((key == SilkKey.KeypadEnter ? EngineKey.Enter : KeyMap.Map(key)) is { } settingsKey)
                    {
                        if (settingsKey is EngineKey.F8 or EngineKey.PrintScreen) screenshotNow = true;
                        else settings.KeyDown(settingsKey, shiftDown, ctrl);
                    }
                    return;
                }
                // The sandbox animation list, while open, takes every key too.
                if (sandboxPicker is { Open: true })
                {
                    if ((key == SilkKey.KeypadEnter ? EngineKey.Enter : KeyMap.Map(key)) is { } pickerKey) sandboxPicker.KeyDown(pickerKey);
                    return;
                }
                if (hud is not null && DebugToggle(key)) { debugMenuOpen = !debugMenuOpen; debugParts = null; return; }
                if (debugMenuOpen)
                {
                    if ((key == SilkKey.KeypadEnter ? EngineKey.Enter : KeyMap.Map(key)) is { } menuKey) DebugMenuKey(menuKey);
                    return;
                }
                if (key == SilkKey.F10 && settings is not null) { settings.Visible = !settings.Visible; return; }
                if (settings is not null && KeyMap.Map(key) is { } settingsToggle &&
                    session.Bindings.Get(InputAction.ToggleSettings).Contains(Binding.Of(settingsToggle)))
                {
                    settings.Visible = !settings.Visible;
                    return;
                }
                // F11 lists the frame; F12 is the small frame-rate line (the profiler chart waits for Metal timestamps).
                if (key == SilkKey.F11 && shiftDown) { ToggleNavOverlay(); return; }
                if (key == SilkKey.F11) { statsVisible = !statsVisible; return; }
                if (key == SilkKey.F12) { frameStatsVisible = !frameStatsVisible; return; }
                if (key is SilkKey.F8 or SilkKey.PrintScreen) { screenshotNow = true; return; }
                if (key == SilkKey.F7 && hud is not null) { hud.UiVisible = !hud.UiVisible; return; }
                // Ctrl+C copies the free camera's position as a viewer camera code; Ctrl+V flies to the clipboard's code.
                if (key is SilkKey.C or SilkKey.V && session.Camera.IsFree && hud is not null && !hud.UiEditing && ctrl)
                {
                    if (key == SilkKey.C)
                    {
                        string code = WorldCameraCode.Encode(camera);
                        keyboard.ClipboardText = code;
                        Console.WriteLine($"camera    copied {code} ({WorldCameraCode.Describe(camera)})");
                    }
                    else if (WorldCameraCode.Decode(keyboard.ClipboardText) is { } pose)
                    {
                        Vector3 back = new(MathF.Cos(pose.Pitch) * MathF.Sin(pose.Yaw), MathF.Sin(pose.Pitch),
                            MathF.Cos(pose.Pitch) * MathF.Cos(pose.Yaw));
                        session.Camera.TakeOver(new CameraState(pose.Target + back * pose.Distance, -back, Vector3.UnitY, pose.Target,
                            pose.Yaw, pose.Pitch, pose.Distance, true), strategy: false);
                        hud.Follow = null;
                        Console.WriteLine("camera    moved to the clipboard's camera code");
                    }
                    else Console.WriteLine("camera    the clipboard holds no camera code (Ctrl+C in a viewer copies one)");
                    return;
                }
                // Numpad / flies the camera across the squad in portrait order; it holds the camera like the tour on Vulkan.
                if (key == SilkKey.KeypadDivide && !IsDuel && hud is not null)
                {
                    if (tour.Active) EndTour();
                    else
                    {
                        hud.Follow = null;
                        tour.HopSeconds = config.CharacterFlightSeconds;
                        tourFollows = false;
                        tour.Start(camera, [.. hud.SquadBarMembers().Select(c => c.Id)], TourPosition);
                        if (tour.Active) tourHolds = true;
                        else Console.WriteLine("squad tour: nobody else in the squad to fly to");
                    }
                    return;
                }
                if (KeyMap.Map(key) is not { } mapped) return;
                // The interface's keys come first (I/C, M/T/Y, Esc, B), as in the Vulkan host; what it does not take reaches
                // the session (WASD, Q/E, Space, F2..F4, PageUp/PageDown, the camera bindings).
                if (hud is not null && (hud.UiKey(mapped, ctrl, shiftDown) || hud.BuildKey(mapped, shiftDown))) return;
                session.Input.SetKey(mapped, true);
                if (hud is not null) hud.Key(mapped, shiftDown);
                SandboxKey(mapped);
                if (IsDuel && mapped == EngineKey.Backspace) duelRestartRequested = true;
            };
            keyboard.KeyUp += (_, key, _) =>
            {
                if (key is SilkKey.ShiftLeft or SilkKey.ShiftRight) { shiftDown = false; if (hud is not null) hud.SlowModifier = false; }
                if (KeyMap.Map(key) is { } mapped) { session.Input.SetKey(mapped, false); hud?.BuildKeyUp(mapped); }
            };
            keyboard.KeyChar += (_, value) =>
            {
                if (Covered) return;
                if (settings?.Visible == true) settings.TextInput(value);
                else if (sandboxPicker is { Open: true }) sandboxPicker.TextInput(value);
                else if (debugMenuOpen) DebugMenuText(value);
                else hud?.UiText(value);
            };
        }
        foreach (var mouse in input.Mice)
        {
            mouse.MouseMove += (m, p) =>
            {
                if (Covered) { lastMouse = p; return; }
                if (settings?.Visible == true) { settings.MouseMove(Pixels(p)); lastMouse = p; return; }
                bool hold = lastMouse is not null && OrbitHeld();
                hud?.MouseMove(Pixels(hold ? lastMouse!.Value : p));
                // The panels and their drags own the pointer: the camera must not also turn under them.
                if (hud is { UiCapturesPointer: true }) { lastMouse = p; return; }
                if (lastMouse is { } last) session.Input.AddMouseDelta(p.X - last.X, p.Y - last.Y);
                if (hold) { HoldPointer(m, lastMouse!.Value); return; }
                lastMouse = p;
            };
            mouse.MouseDown += (_, button) =>
            {
                if (Covered) return;
                if (KeyMap.Map(button) is not { } mapped) return;
                var at = Pixels(mouse.Position);
                if (settings?.Visible == true) { settings.MouseDown(at, mapped); return; }
                bool select = PlayerButton(mapped) == Meitou.Engine.Input.MouseButton.Left;
                if (hud is { UiVisible: true } && select && !config.SandboxPanelCollapsed && sandboxPicker?.MouseDown(at) == true) return;
                if (hud is { UiVisible: true } && select && SandboxToggleClick(at)) return;
                // PlayerInterface sees every click: it takes the HUD's controls, selects, starts a drag box or gives a move order.
                if (hud is not null && hud.MouseDown(PlayerButton(mapped), at, shiftDown, Ctrl(), Alt())) return;
                session.Input.SetMouseButton(mapped, true);
            };
            mouse.MouseUp += (_, button) =>
            {
                if (Covered) return;
                if (settings?.Visible == true) { settings.MouseUp(); return; }
                if (KeyMap.Map(button) is not { } mapped) return;
                hud?.MouseUp(PlayerButton(mapped), Pixels(mouse.Position), shiftDown);
                session.Input.SetMouseButton(mapped, false);
            };
            mouse.Scroll += (_, wheel) =>
            {
                if (Covered) return;
                if (settings?.Visible == true) { settings.Scroll(wheel.Y); return; }
                var at = Pixels(mouse.Position);
                if (hud is { UiVisible: true } && !config.SandboxPanelCollapsed && sandboxPicker?.Scroll(wheel.Y) == true) return;
                // A panel's own scroll wins; over any HUD rectangle the wheel does not zoom the camera, as in the Vulkan host.
                if (hud is not null && (hud.UiScroll(at, wheel.Y) || hud.ContainsUi(at))) return;
                session.Input.AddWheel(wheel.Y);
            };
        }
        window.FocusChanged += focused =>
        {
            if (focused) return;
            session.Input.ReleaseAll();
            if (hud is not null) hud.SlowModifier = false;
            shiftDown = false;
            lastMouse = null;
        };
        // The game's quick save/load keys are control actions, as in the Vulkan host (F5/F9 by default).
        session.Ticked += actions =>
        {
            if (actions.Pressed(InputAction.QuickSave)) quickSaveRequested = true;
            if (actions.Pressed(InputAction.QuickLoad)) quickLoadRequested = true;
            if (actions.Pressed(InputAction.FloorUp)) StepFloor(1);
            if (actions.Pressed(InputAction.FloorDown)) StepFloor(-1);
            if (hud is not null)
            {
                // Panning, or leaving for the free camera, lets go of the character the camera follows; turning and
                // zooming keep it, as in the Vulkan host.
                if (actions.Held(InputAction.MoveForward) || actions.Held(InputAction.MoveBack) ||
                    actions.Held(InputAction.MoveLeft) || actions.Held(InputAction.MoveRight) ||
                    actions.Pressed(InputAction.ToggleFreeCamera)) hud.Follow = null;
                if (tour.Active && CameraInput(actions)) EndTour();
            }
        };
        // Put the title's camera and staging up before the streamers settle, so the menu's first frame is already the staged view.
        if (hud is not null) ApplyTitleCamera();
        // The streamer settles the fine heights and biome maps round the camera before the first frame draws (the still's camera does not move).
        streamer?.Settle(camera.Eye);
        if (streamer is { } settled) Console.WriteLine($"metal     {settled.Describe()}");
        // --fly-to with a still: walk the camera through the intervening windows before the picture, so the map-window
        // recenter/upload path is exercised without needing an interactive flight.
        if (o.Screenshot is not null && o.FlyToX is { } flyX && o.FlyToZ is { } flyZ)
        {
            var start = session.Camera.Current.Target;
            const int Steps = 6;
            for (int i = 1; i <= Steps; i++)
            {
                double t = i / (double)Steps;
                float x = (float)(start.X + (flyX - start.X) * t), z = (float)(start.Z + (flyZ - start.Z) * t);
                var view = session.Camera.Current;
                session.Camera.Place(new Vector2(x, z), view.Yaw, view.Pitch, view.Distance);
                ApplyCamera(session.Camera.Current);
                streamer?.Settle(camera.Eye);
                Console.WriteLine($"metal     fly-to {i}/{Steps}: {x:0}, {z:0}");
            }
            if (streamer is { } flown) Console.WriteLine($"metal     fly-to settled: {flown.Describe()}");
        }
        // The still's selection, --move-to walk and --ticks happen on the first frame: its Settle calls upload textures through
        // the open frame (Metal cannot open one before a drawable exists), and the loop then only lets the picture warm up.
        bool still = o.Screenshot is not null, stillPrepared = false;
        string?[] stillPanels = hud is not null && still ? StillPanels() : new string?[] { null };
        int stillPanel = 0, stillPanelFrames = 0;
        // A run can still open one shared layout by name for manual review; the still loop owns its panels.
        if (!still && hud is not null && g.UiPanel is { Length: > 0 } selectedPanel && selectedPanel is not ("trade" or "talk" or "edited"))
            hud.ShowUiPanel(selectedPanel is "hud" or "hud-expanded" ? null : selectedPanel, selectedPanel == "hud-expanded");
        bool BeginStillPanel(string? panelName)
        {
            if (panelName == "trade")
            {
                if (player is null || !ShowcaseTrade()) return false;
                // The trade picture shows a stock item's tooltip: the pointer rests on the first item.
                if (player.UiFrame.FirstOrDefault(c => c.Name.StartsWith("trade-item-", StringComparison.Ordinal) &&
                        !c.Name.EndsWith("-background", StringComparison.Ordinal) && !c.Name.EndsWith("-icon", StringComparison.Ordinal)) is { } item)
                    player.MouseMove(new Vector2(item.Bounds.X + item.Bounds.Width / 2, item.Bounds.Y + item.Bounds.Height / 2));
                return true;
            }
            if (panelName == "talk") return player is not null && ShowcaseTalk();
            if (panelName == "edited" && player is not null)
            {
                player.ShowcaseEdit();
                session.AdvanceSimulation(session.Ticks.TickSeconds);
            }
            if (panelName?.StartsWith("editor", StringComparison.Ordinal) == true && hud is not null)
            {
                hud.ShowUiPanel(panelName);
                EditorCamera();
            }
            return true;
        }
        void ApplyStillPanel(string? panelName)
        {
            if (player is null) return;
            if (g.TitlePreview) player.ShowTitle();
            else if (hud is not null && panelName is not ("trade" or "talk"))
                hud.ShowUiPanel(panelName is "hud-expanded" or "edited" ? null : panelName, panelName == "hud-expanded");
            if (hud is not null) ApplyTitleCamera();
        }
        void KeepStillPanel(string? panelName)
        {
            if (player is null) return;
            if (hud is not null && panelName?.StartsWith("editor", StringComparison.Ordinal) == true) EditorCamera();
            if (hud is not null) ApplyTitleCamera();
        }

        var clock = Stopwatch.StartNew();
        double last = 0, fpsWindow = 0, titleTimer = 0, traceAt = 0, nativeAt = 0, lastWait = 0, lastRecord = 0;
        int frames = 0, fpsFrames = 0;
        string fpsText = "";
        bool trace = Environment.GetEnvironmentVariable("MEITOU_METAL_TRACE") == "1";
        if (trace)
        {
            var watchdog = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(2000);
                    Console.WriteLine($"metal     trace watchdog: main loop at frame {Volatile.Read(ref frames)}");
                }
            }) { IsBackground = true, Name = "metal trace" };
            watchdog.Start();
        }
        var statsLines = new List<string>();
        double streamingIdleSince = 0, lastManagedTrim = double.NegativeInfinity;
        // Loading can take long enough for the terminal that started the game to take focus back: bring the game forward again
        // before the interactive loop (its title and FPS are watched there).
        display.Activate();
        if (BorderlessFullScreen.IsOn(window)) display.SetFullScreenPresentation(true);
        while (!window.IsClosing && !quit)
        {
            window.DoEvents();
            if (window.IsClosing) break;
            double now = clock.Elapsed.TotalSeconds, frameStart = now, dt = now - last;
            last = now;
            if (trace && now - traceAt >= 1)
            {
                traceAt = now;
                Console.WriteLine($"metal     trace t={now:0.0}s frame {frames}, window {window.Size.X}x{window.Size.Y} points, " +
                    $"framebuffer {window.FramebufferSize.X}x{window.FramebufferSize.Y} pixels, cpu {dt * 1000:0.0} ms " +
                    $"(drawable {lastWait:0.0} ms, record {lastRecord:0.0} ms), gpu {ctx.LastGpuMilliseconds:0.0} ms");
            }
            if (trace && now - nativeAt >= 5)
            {
                nativeAt = now;
                Console.WriteLine($"metal     native {display.NativeWindowState()}");
                Console.WriteLine($"metal     resources {ctx.ResourceSummary()}");
                Console.WriteLine($"metal     streaming terrain {streamer?.Pending ?? 0} ({(streamer is null || streamer.Idle ? "idle" : "busy")}), " +
                    $"objects {objects?.Pending ?? 0}, foliage {foliage?.Pending ?? 0}, cull groups {foliage?.LastCullGroups ?? 0}, cull dispatches {foliage?.GpuCullDispatches ?? 0}");
                if (streamer is not null) Console.WriteLine($"metal     terrain stream: {streamer.Describe()}");
                if (memoryGuard is not null) Console.WriteLine($"metal     memory guard {memoryGuard.Status}");
                if (objects is not null) Console.WriteLine($"metal     objects resident: {objects.ResidentDescription}");
                if (foliage is not null) Console.WriteLine($"metal     foliage resident: {foliage.ResidentDescription}");
                if (characters is not null) Console.WriteLine($"metal     characters: {characters.Describe()}");
            }
            var size = window.FramebufferSize;
            var pointerMouse = input.Mice.FirstOrDefault();
            var pointerAt = pointerMouse is null ? Vector2.Zero : Pixels(pointerMouse.Position);
            // The original's background-loading bar follows the interface's mode, as on Vulkan; the Metal host keeps the same shared bar.
            bool faithfulUi = hud?.UiMode == Meitou.Data.Ui.UiMode.Faithful;
            loadBar.Update(faithfulUi ? PendingLoads() : 0);
            if (hud is not null) hud.LoadBar = loadBar;
            if (loading is not null) (loading.LoadBar, loading.Faithful) = (loadBar, faithfulUi);
            // The counter is the mean over the last quarter second, so one late frame does not read as a drop; then a frame's work
            // (its time without the waits) and its whole time in brackets.
            fpsWindow += dt;
            fpsFrames++;
            if (fpsWindow >= 0.25)
            {
                fpsText = string.Create(CultureInfo.InvariantCulture,
                    $"{fpsFrames / fpsWindow:0} fps  ({fpsWindow * 1000 / fpsFrames:0.0} ms)");
                fpsWindow = 0;
                fpsFrames = 0;
            }
            bool ctrlHeld = false, altHeld = false, highlighting = false;
            if (!still)
            {
                // Edge scrolling only while the world owns the pointer (the interface is visible and not dragging a panel).
                session.Camera.Settings.EdgeScroll = config.EdgeScroll != false;
                session.Camera.Strategy.EdgePointer = Covered || hud is not null && TitleVisible || settings?.Visible == true ||
                    hud is { UiVisible: false } or { UiCapturesPointer: true } ? null : WindowPointer.InWindow(window);
                // The duel restarts between ticks when Backspace asks or its measured lap ends; its sun then holds at the lap's hour.
                if (hud is not null)
                {
                    DuelFrame(false);
                    if (!Covered) session.Advance(dt);
                    DuelHoldClock();
                    WorldNews();
                }
                else if (!Covered) session.Advance(dt);
                if (hud is not null)
                {
                    HandleSaves(dt);
                    HandleNewGame();
                    HandleMainMenu();
                }
                StepSceneLoad();
                AdvanceSkyArrival(dt);
                // The squad tour and character flights hold the camera while they fly; otherwise the rig owns the view.
                if (!tourHolds) ApplyCamera(session.CameraAt());
                if (tour.Active && !tour.Advance(dt, camera, TourPosition)) EndTour();
                gpu.Ahead = tour.ArrivalEye(TourPosition);
                if (hud is not null)
                    gpu.Kept = config.KeepTexturesAtCharacters ? [.. hud.SquadBarMembers().Select(c => TourPosition(c.Id)).OfType<Vector3>()] : [];
                if (!tourHolds && hud is not null)
                {
                    SandboxFollowCamera();
                    FollowCamera();
                    EditorCamera();
                }
                // The merged character editor is shared with Vulkan: a new game opens it once the squad is in the snapshot, and the world resumes when it closes.
                OpenPendingEditor();
                ResumeAfterCreation();
                if (hud is not { EditorOpen: true }) { ClearEditorStage(); ClearEditorBar(); }
                if (!tourHolds && hud is not null) ApplyTitleCamera();
                // A flight puts the focus where it lands, so the towns there populate during the flight; otherwise the
                // followed character's pivot is already in the rig.
                SendFocus(tour.Active && tour.Current is { } tourLanding && TourPosition(tourLanding) is { } tourAt ? tourAt
                    : tourHolds ? camera.Target : null);
                // The strategy camera's eye clearance for the followed character and the tour's arcs over hills.
                if (skyArrival is null && hud is not null && !TitleVisible && (tour.Active || !session.Camera.IsFree && !tourHolds))
                {
                    var clearEye = camera.Eye;
                    float shortfall = session.Camera.EyeGroundAt(clearEye.X, clearEye.Z) +
                        Meitou.Data.World.KenshiCamera.EyeClearance(camera.Distance) - clearEye.Y;
                    if (shortfall > 0) camera.Target += new Vector3(0, shortfall, 0);
                }
                // The pointer's Ctrl/Alt modifiers and the Highlight key (Alt): labels on the ground items near the focus, signs and the hover outlines agree.
                ctrlHeld = Ctrl();
                altHeld = Alt();
                highlighting = altHeld && !Covered && !TitleVisible && hud is { UiVisible: true } &&
                    settings?.Visible != true && !session.Camera.IsFree;
                if (hud is not null)
                {
                    hud.CtrlHeld = ctrlHeld;
                    hud.AltHeld = altHeld;
                    hud.HighlightFocus = highlighting ? camera.Target : null;
                }
                if (objects is not null) objects.HighlightSigns = highlighting;
                if (!Covered && hud is not null && TitleVisible) titleBattle?.Advance(dt);
                if (skyArrival is not null)
                {
                    ApplyCamera(session.Camera.Current);
                    if (hud is not null) ApplyTitleCamera();
                    ApplySkyArrival();
                }
                if (!Covered) UpdateAudio(dt);
                UpdateNav();
                UpdateNavOverlay();
                // Diagnostic isolation for the far-travel freeze: skip the runtime building/ground-item/lamps diff.
                if (Environment.GetEnvironmentVariable("MEITOU_METAL_NO_BUILDINGS") != "1") UpdateBuildings();
                // The interior view (docs/formats/zones.md): the player's building opens, its upper floors and their characters are hidden.
                // It must run before the character draw list (which carries aboveFloor) and before the shadow casters.
                UpdateInteriorView();
                // The next world tick may run on its own thread while the frame is drawn, as in the Vulkan loop (docs/simulation.md "Tick ahead").
                tickAhead = !Covered && skyArrival is null && !IsDuel && !TitleVisible && !player.EditorOpen && g.PlayBench is null && TickAheadWanted();
            }
            else
            {
                // A still freezes the simulation after PrepareStill: only the panel and the warm-up frames change, never the tick.
                tickAhead = false;
                if (hud is not null) { hud.Width = size.X; hud.Height = size.Y; }
                bool panelReady = false;
                while (stillPanel < stillPanels.Length)
                {
                    string? candidate = stillPanels[stillPanel];
                    if (stillPanelFrames == 0 && !BeginStillPanel(candidate))
                    {
                        stillPanel++;
                        continue;
                    }
                    panelReady = true;
                    break;
                }
                if (!panelReady) break;
                string? panelName = stillPanels[stillPanel];
                if (stillPanelFrames == 0) ApplyStillPanel(panelName);
                KeepStillPanel(panelName);
            }
            // Ctrl+/ records the next complete frame for Xcode. Start before BeginFrame makes the command buffer and stop
            // after EndFrame commits the present, so the trace holds exactly one frame rather than the three in flight.
            MetalGpuCapture? gpuCapture = null;
            if (gpuCaptureRequested)
            {
                gpuCaptureRequested = false;
                // Never start during the loading frames: they create and upload most of the world's resources, and a
                // capture that spans them can write many gigabytes before the first picture exists.
                if (frames < 1)
                {
                    Console.WriteLine("metal     gpu capture: wait until the world has drawn a frame, then press Ctrl+/ again");
                }
                else
                {
                    gpuCapture = MetalGpuCapture.TryStart(ctx.Queue, out string? captureError);
                    Console.WriteLine(gpuCapture is not null
                        ? $"metal     gpu capture: {gpuCapture.OutputPath}"
                        : $"metal     gpu capture unavailable: {captureError}");
                }
            }
            // A live resize can leave the layer without a drawable for a moment: wait for the next one instead of spinning
            // the main thread (the window would stop responding to its own resize).
            bool began = ctx.BeginFrame();
            if (!began)
            {
                if (gpuCapture is not null)
                {
                    // No drawable yet: discard the empty capture and ask for the next frame instead.
                    gpuCapture.Abort();
                    gpuCaptureRequested = true;
                }
                Thread.Sleep(1);
                continue;
            }
            if (still && !stillPrepared)
            {
                PrepareStill(ctx, finish: false);
                stillPrepared = true;
            }
            if (settleAfterWorldLoad)
            {
                settleAfterWorldLoad = false;
                Console.WriteLine($"metal     settling objects and foliage at the loaded view");
                objects?.Settle(camera.Eye);
                foliage?.Settle(camera.Eye);
            }
            double drawableWait = display.LastDrawableWaitMilliseconds;
            long recordStart = Stopwatch.GetTimestamp();
            var backbuffer = ctx.Backbuffer;
            // The editor's scene: in the Meitou UI the bar, drawn by the world pass below from the editor's camera (GameHost.EditorBar.cs); else the original's stage, the
            // character and its platform drawn into the stage texture the interface shows over the world.
            var editorBarFrame = hud is { EditorOpen: true } ? BeginEditorBar(size.X, size.Y) : null;
            // The still's objects are all resident before the first picture, as the Vulkan screenshot path settles them (its Settle
            // opens its own frame; here the frame is the first one's).
            if (frames == 0) { objects?.Settle(camera.Eye); foliage?.Settle(camera.Eye); }
            // What the shared draw needs before it updates the characters: the squad's portrait requests and the hover masks.
            if (characters is not null)
            {
                characters.PortraitRequests.Clear();
                if (hud is not null)
                    foreach (var (member, cell) in hud.SquadBarMembers().Select((member, cell) => (member, cell)))
                        characters.PortraitRequests.Add(new PortraitRequest(DrawKey(member.Id), cell, member.PortraitSerial));
            }
            var hoverKeys = new List<(long Key, Vector4 Colour)>();
            bool outlining = config.HoverOutline && !session.Camera.IsFree && hud is { UiVisible: true } &&
                !Covered && settings?.Visible != true;
            if (outlining)
            {
                if (highlighting)
                    hoverKeys.AddRange(hud!.HighlightTargets(camera.Target, PlayerInterface.HighlightRadius)
                        .Select(t => (DrawKey(t.Id), t.Colour)));
                else if (hud!.HoverTarget is { } hover)
                    hoverKeys.Add((DrawKey(hover.Id), hover.Colour));
            }
            gpu.HoverKeys = hoverKeys;
            gpu.HoverOutline = hoverOutline;
            // The frame's inputs, as WorldFrame.Draw prepares them: the weather at the camera, the atmosphere's tables for the sun and the
            // eye's height, the fog distance the light ends at, and the view distance that keeps the haze complete before the far plane.
            bool titleLook = hud is not null && TitleVisible;
            float hour = editorBarFrame is not null ? EditorBarHour : presentationHour ?? (titleLook ? TitleHour : (float)session.Clock.HourOfDay);
            var aspect = size.X / (float)Math.Max(size.Y, 1);
            var eye = camera.Eye;
            var view = camera.View;
            // The title stages the world as the poster: clear morning light, the sun right of the view, the moons full and the ruin
            // beside the logo. Save the gameplay values and put them back after the world pass.
            var savedClock = scene.Clock;
            var savedWeather = gpu.Weather;
            var savedSkyWeather = sky.Weather;
            float? savedCloudCoverage = sky.CloudCoverage;
            var savedCloudWind = sky.CloudWind;
            var savedPlanetTurn = sky.PlanetTurn;
            float savedPlanetFill = sky.PlanetFill;
            var savedFill = characters?.Fill ?? default;
            bool savedHidePlaced = fogVolumes.HidePlaced;
            if (titleLook)
            {
                float fill = Math.Clamp(scene.Clock.SunDirection(hour).Y / MathF.Max(scene.Clock.SunDirection(TitleHour).Y, 1e-3f), 0, 1);
                fill = fill * fill * (3 - 2 * fill);
                sky.Weather = Meitou.Data.World.SkyWeather.Default;
                sky.CloudCoverage = 0.38f;
                sky.CloudWind = TitleComposition.CloudWind(camera);
                sky.PlanetTurn = TitleComposition.MoonTurn(camera);
                sky.PlanetFill = 0.8f * fill;
                scene.Clock = scene.Clock with { SunTurn = TitleComposition.SunTurn(scene.Clock, hour, camera) };
                if (characters is not null) characters.Fill = new Vector3(0.064f, 0.052f, 0.04f) * fill;
                fogVolumes.HidePlaced = true;
            }
            // One shared frame: the weather and sky preparation, streaming, shadows, the scene host pass, the post-chain scene
            // effects, the particles and the post chain, then the overlays over the finished picture. The Metal chain fills the
            // seam's Vulkan-only gaps (no reflection, GI, low-resolution particles or fog-rate target).
            gpu.GameHours = session.Clock.HoursSinceStart;
            UpdateBlood();
            if (characters is not null)
            {
                characters.Look = Meitou.Rendering.Characters.PreviewLook.Of(o.Post);
                if (hud is not null)
                {
                    hud.PortraitSize = Meitou.Rendering.Characters.CharacterRenderer.PortraitSizeFor(characters.Look);
                    SyncIconLook();
                }
            }
            var savedParticles = gpu.Particles;
            if (titleLook) { gpu.Weather = null; gpu.Particles = null; }   // the staged title sky and its own dust, not the world's weather
            if (tickAhead) session.BeginTickAhead();
            try
            {
                WorldFrame.Draw(gpu, scene, camera, render, size.X, size.Y, hour, (float)now / 600f, o.FogDistance);
            }
            finally
            {
                if (tickAhead) session.EndTickAhead();
                gpu.Weather = savedWeather;
                gpu.Particles = savedParticles;
                if (titleLook)
                {
                    scene.Clock = savedClock;
                    sky.Weather = savedSkyWeather;
                    sky.CloudCoverage = savedCloudCoverage;
                    sky.CloudWind = savedCloudWind;
                    sky.PlanetTurn = savedPlanetTurn;
                    sky.PlanetFill = savedPlanetFill;
                    if (characters is not null) characters.Fill = savedFill;
                    fogVolumes.HidePlaced = savedHidePlaced;
                }
            }
            if (editorBarFrame is { } barFrame) EndEditorBar(barFrame, size.X, size.Y);
            // The feedback screenshot: the frame without the menus, the settings, the debug overlays and the pointer (GameHost.Feedback.cs).
            bool feedbackShot = feedbackShotRequested && hud is not null && !still;
            if (hud is not null) hud.ShotWithoutMenus = feedbackShot;
            if (overlay is not null)
            {
                overlay.Target = backbuffer;
                // One overlay pass: the HUD, the direct HUD lines and the stats/settings text would otherwise each load/store the whole backbuffer.
                {
                using var overlayPass = overlay.BeginPass(size.X, size.Y);
                if (hud is not null)
                {
                    hud.Width = size.X;
                    hud.Height = size.Y;
                    hud.Eye = eye;
                    hud.ViewProjection = view * camera.Projection(aspect, camera.Near, camera.ViewDistance);
                    // The move order under a held right button follows the cursor once a frame, as the Vulkan host's Steer does.
                    hud.Steer();
                    if (hud.UiVisible)
                    {
                        hud.Draw(overlay, size.X, size.Y, overlayPass);
                        if (IsSandbox && !g.UiShowcase) DrawSandbox(overlay, size.X, size.Y);
                    }
                }
                bool wrote = false;
                // The framebuffer is 2x the window's points on a Retina display: the fixed-pixel debug text would be half size.
                float textScale = MathF.Max(1, size.X / (float)Math.Max(window.Size.X, 1));
                if (statsVisible && !feedbackShot)
                {
                    statsLines.Clear();
                    statsLines.Add($"{fpsText}  {dt * 1000:0.00} ms  {size.X}x{size.Y}");
                    statsLines.Add($"sim {session.Simulation.TotalTicks} ticks  {session.CurrentSnapshot.Characters.Count} characters  x{session.TimeScale:0}  {session.Clock.TimeText}");
                    statsLines.Add($"camera {camera.Target.X:0}, {camera.Target.Y:0}, {camera.Target.Z:0}  yaw {camera.Yaw * 180 / MathF.PI:0}  pitch {camera.Pitch * 180 / MathF.PI:0}  boom {camera.Distance:0}");
                    if (characters is not null) statsLines.Add(characters.Statistics());
                    if (foliage is not null) statsLines.Add($"foliage {foliage.DrawnInstances} instances, {foliage.DrawnBlades} blades, {foliage.DrawCalls} calls");
                    overlay.Panel(size.X, size.Y, "Metal frame (F11 hides this)", statsLines, (hud is { UiVisible: true } ? hud.HudBottom : 0) + 8, textScale);
                    wrote = true;
                }
                if (frameStatsVisible && !feedbackShot)
                {
                    overlay.TextScaled("Meitou - Metal", 16 * textScale, 16 * textScale, DebugOverlay.TextColour, textScale);
                    overlay.TextScaled($"{frames + 1} frames  {fpsText}  {dt * 1000:0.00} ms  {size.X}x{size.Y}", 16 * textScale,
                        (16 + overlay.LineHeight) * textScale, DebugOverlay.TextColour, textScale);
                    wrote = true;
                }
                if (debugMenuOpen && hud is not null && !feedbackShot)
                {
                    DrawDebugMenu(overlay, size.X, size.Y, (hud.UiVisible ? hud.HudBottom : 0) + 8, "Ctrl+F10");
                    wrote = true;
                }
                if (settings?.Visible == true && !feedbackShot)
                {
                    settings.Draw(size.X, size.Y, overlayPass);
                    wrote = true;
                }
                // The mouse pointer over everything, as Vulkan draws it: the system cursor when Hardware mouse is on, else the
                // game's own pointer. The free camera hides it, and saved pictures never contain it.
                if (!still && !screenshotNow && !feedbackShot && pointerMouse is not null && hud is not null)
                {
                    bool pointerWorld = !Covered && !g.NoPopulation && settings?.Visible != true && !TitleVisible;
                    bool freeLook = pointerWorld && session.Camera.IsFree;
                    var cursorMode = config.HardwareMouse && !freeLook ? Silk.NET.Input.CursorMode.Normal : Silk.NET.Input.CursorMode.Hidden;
                    if (cursorMode != windowCursor) pointerMouse.Cursor.CursorMode = windowCursor = cursorMode;
                    if (!config.HardwareMouse && !freeLook)
                    {
                        hud.DrawPointer(pointerWorld ? hud.PointerName : "arrow", pointerAt, size.X, size.Y, overlayPass);
                        wrote = true;
                    }
                }
                if (wrote) overlay.Flush(size.X, size.Y, null, overlayPass);
                }
                // The scene changes use the same Kenshi loading screen as Vulkan; it covers the world until the sky arrival is done.
                if (loading is not null && (sceneLoad is not null || loading.Exiting))
                {
                    overlay.Target = backbuffer;
                    loading.Draw(size.X, size.Y);
                }
            }
            ctx.EndFrame();
            if (gpuCapture is not null)
            {
                gpuCapture.Stop();
                Console.WriteLine($"metal     gpu capture written: {gpuCapture.OutputPath}");
            }
            // Mark the covered frame: the next one may run the next stage (which can hold the thread).
            if (sceneLoad is { } pendingLoad) pendingLoad.Shown = true;
            lastWait = drawableWait;
            lastRecord = Stopwatch.GetElapsedTime(recordStart).TotalMilliseconds;
            frames++;
            worldHasRendered = true;
            // Once the terrain, object and foliage streamers have been quiet for two seconds, the large decode/upload
            // buffers are garbage. Compact at that idle point (at most once a minute) so the LOH does not keep them
            // for the rest of the session. Active streaming is never interrupted.
            bool streamingIdle = (streamer?.Idle ?? true) && (objects?.Pending ?? 0) == 0 && (foliage?.Pending ?? 0) == 0;
            if (!streamingIdle) streamingIdleSince = 0;
            else if (streamingIdleSince == 0) streamingIdleSince = now;
            var managedBefore = GC.GetGCMemoryInfo();
            bool managedHighWater = managedBefore.HeapSizeBytes > 4L << 30;
            if ((streamingIdle && now - streamingIdleSince >= 2 || managedHighWater) && now - lastManagedTrim >= 60)
            {
                lastManagedTrim = now;
                var before = managedBefore;
                // Compacting the LOH is a stop-the-world move of every large object: on a 4+ GB heap it stalled
                // this loop for 1.6 s. Do it where a loading screen or a menu covers the pause, or when the heap
                // has grown past 4 GB; otherwise a background collection is enough and does not move live arrays.
                bool safePoint = TitleVisible || settings?.Visible == true || sceneLoad is not null || loading?.Exiting == true;
                bool compact = safePoint || managedHighWater;
                if (compact)
                {
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                }
                else GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false);
                var after = GC.GetGCMemoryInfo();
                long loh = after.GenerationInfo.Length > 3 ? after.GenerationInfo[3].SizeAfterBytes : 0;
                if (trace)
                    Console.WriteLine($"metal     idle trim ({(compact ? "compacting" : "background")}): " +
                        $"heap {before.HeapSizeBytes / 1048576} -> {after.HeapSizeBytes / 1048576} MB, LOH {loh / 1048576} MB");
            }
            if (feedbackShot)
            {
                hud!.ShotWithoutMenus = false;
                var picture = ctx.Readback(backbuffer);
                for (int i = 3; i < picture.Length; i += 4) picture[i] = 255;
                FeedbackCaptured(picture, backbuffer.Desc.Width, backbuffer.Desc.Height);
            }
            if (screenshotNow)
            {
                string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                if (pictures.Length == 0) pictures = Directory.GetCurrentDirectory();
                string file = Path.Combine(Directory.CreateDirectory(pictures).FullName, $"meitou-metal-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                Capture(ctx, backbuffer, file);
                screenshotNow = false;
            }
            // A panel's item icons are drawn after the frame that asks for them, and their textures stream in: let a UI still
            // wait for them (up to a bound), while an ordinary world still keeps its 40 frames. The simulation stays frozen.
            int pendingIcons = (characters?.PendingIcons ?? 0) + (hud?.PendingIconFiles ?? 0);
            bool pendingStillContent = pendingIcons > 0 || objects is { StagePropPending: true } || objects is { PendingBuildingIcons: > 0 };
            if (still)
            {
                stillPanelFrames++;
                if (stillPanelFrames >= 40 && (!pendingStillContent || stillPanelFrames >= 400))
                {
                    string? panelName = stillPanels[stillPanel];
                    if (foliage is not null && stillPanel == stillPanels.Length - 1)
                        Console.WriteLine($"metal     foliage: {foliage.DrawnInstances} instances, {foliage.DrawnBlades} blades, {foliage.DrawCalls} calls");
                    Capture(ctx, backbuffer, StillPicturePath(panelName));
                    stillPanel++;
                    stillPanelFrames = 0;
                    if (stillPanel >= stillPanels.Length) break;
                }
            }
            titleTimer += dt;
            if (titleTimer >= 0.5)
            {
                var s = session.Camera.Current;
                window.Title = $"Meitou (metal) | {fpsText} | {(session.Camera.IsFree ? "free camera" : $"boom {s.Distance:0}")} | " +
                    $"{session.Clock.TimeText} {session.Clock.DayText} | {s.Target.X:0}, {s.Target.Z:0}";
                titleTimer = 0;
            }
            // The Vulkan loop's cap: only without vsync (vsync already paces the present); the same 30 fps cap in
            // both comparison runs isolates tearing from the much higher uncapped frame rate.
            if (!display.VSync && fpsLimit > 0)
            {
                Limit(clock, frameStart, 1.0 / fpsLimit);
            }
            if (g.QuitAfter is { } quitAfter && now >= quitAfter) break;
        }
        void SaveSettings()
        {
            config.VSync = display.VSync;
            config.FpsLimit = fpsLimit;
            config.FullScreen = BorderlessFullScreen.IsOn(window);
            config.MetalUpscaler = mode;
            config.MetalRenderScale = ctx.RenderScale;
            if (hud is not null) config.UiMode = hud.UiMode;
            config.Bindings = session.Bindings.ToDictionary();
            if (settingsValues is not null)
            {
                foreach (var slider in settingsValues.Sliders)
                {
                    // These have their own config fields; the rest use the same graphics keys as the Vulkan settings.
                    if (RunOnlyKeys().Contains(slider.Label) || slider.Label is SettingsMenu.MetalPresetSlider ||
                        slider.Label == WorldFrame.UpscalerSliders[1]) continue;
                    config.Graphics[slider.Label] = slider.Get();
                }
                foreach (var toggle in settingsValues.Toggles)
                    if (!RunOnlyKeys().Contains(SettingsMenu.SwitchKeyPrefix + toggle.Label)) config.Graphics[SettingsMenu.SwitchKeyPrefix + toggle.Label] = toggle.Get() ? 1 : 0;
                settings?.SavePreset();
            }
            config.Save();
        }
        // Let the queue finish before the renderers release views, buffers and bindless entries; a quit during a
        // streaming upload otherwise freed resources the GPU was still reading and could fault on the way out.
        SaveSettings();
        display.SetFullScreenPresentation(false);
        loading?.Dispose();
        loading = null;
        ctx.Finish();
        hud?.DisposeUi();
        overlay?.Dispose();
        // The shared world owns terrain (and its textures), sky, weather surfaces, particles, lamps, objects, foliage,
        // water, characters, interior masks, streamer and shadow. Metal's post chain and the picture target are local.
        gpu?.Dispose();
        postChain.Dispose();
        composite.Dispose();
        taa.Dispose();
        metalFx?.Dispose();           // PostTaa owns it while selected; safe to dispose again after a preset switch
        metalFxSpatial?.Dispose();
        double elapsed = clock.Elapsed.TotalSeconds;
        Console.WriteLine($"metal     {frames} frames in {elapsed:0.0} s: {frames / Math.Max(elapsed, 1e-3):0} fps, " +
            $"camera {camera.Target.X:0}, {camera.Target.Z:0}, yaw {camera.Yaw * 180 / MathF.PI:0}, pitch {camera.Pitch * 180 / MathF.PI:0}, boom {camera.Distance:0}");
        return 0;
    }

    partial void OnWorldSettled()
    {
        // A save load or a new game swaps the world after the first frame: force the object and foliage streamers
        // to settle at the new camera, as the first world does at frame 0. Otherwise they can stay empty until the
        // camera moves, even though the characters and the simulation are already there.
        if (worldHasRendered) settleAfterWorldLoad = true;
    }

    /// <summary>The backbuffer's picture as a PNG (the readback's rows are the picture's own: Metal draws right side up).</summary>
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
}
#endif
