using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data.Ogre;
using Meitou.Game.Audio;
using Meitou.Game.DevTools;
using Meitou.Game.DevTools.Agents;
using Meitou.Game.Editor;
using Meitou.Game.Effects;
using Meitou.Game.Host;
using Meitou.Game.Host.Cameras;
using Meitou.Game.Host.Input;
using Meitou.Game.Host.WorldSync;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Display;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Particles;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;
using Meitou.Game.Scenarios;
using Meitou.Game.Settings;
using Meitou.Game.Title;
using Meitou.Game.Ui;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Session;
using EngineButton = Meitou.Game.Host.Input.MouseButton;
using EngineKey = Meitou.Game.Host.Input.Key;

namespace Meitou.Game;

/// <summary>
/// The game loop: a real-time control tick (<see cref="FixedStepClock"/>, 30 Hz by default) runs the input actions, the camera rig and pause/speed; a
/// game-time simulation tick (<see cref="SimulationClock"/>) advances the game clock; each displayed frame draws the camera interpolated between the last two
/// ticks at the display rate.
/// </summary>
sealed partial class GameHost(
    GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o, GameOptions g, UserConfig config, Data.Gameplay.NewGameStart? start)
{
    #region Inputs

    // Primary-constructor parameters the feature classes read.
    internal readonly GameInstall Install = install;
    internal WorldScene Scene = scene;
    internal readonly AssetLocator Assets = assets;
    internal readonly WorldOptions WorldOptions = o;
    internal readonly GameOptions GameOptions = g;
    internal readonly UserConfig Config = config;

    /// <summary>
    /// The save the run starts in (<c>--load</c>), loaded at the end of <see cref="Boot"/> through the menu's load path; null for a new world.
    /// </summary>
    public Data.Save.SaveGame? StartSave { get; init; }

    /// <summary>
    /// Reads the world in the interactive run, on a worker behind the loading screen (the host owns the scene then); null: the scene was given.
    /// </summary>
    public Func<WorldScene?>? LoadScene { get; init; }

    internal WorldScene? OwnedScene;

    /// <summary>
    /// The native splash shown since the program started; closed when the game window first shows.
    /// </summary>
    public IDisposable? Splash { get; init; }

    #endregion

    #region Features

    // The features, each a class of its own taking the host (made first in Run).
    internal GameLoop GameLoop = null!;
    internal GameSound Sound = null!;
    internal TitleScreen TitleScreen = null!;
    internal NewGameLaunch NewGame = null!;
    internal GameSaves Saves = null!;
    internal GameMods Modding = null!;
    internal BattleScenario Battle = null!;
    internal DuelScenario Duel = null!;
    internal SandboxScenario Sandbox = null!;

    internal BuildingSync BuildingSync = null!;
    internal FarmCrops FarmCrops = null!;
    internal TownChanges TownChanges = null!;
    internal GroundItemSync GroundItemSync = null!;
    internal InteriorView Interiors = null!;
    internal WeatherFeed WeatherFeed = null!;
    internal ItemIconFiles ItemIconFiles = null!;

    internal BloodDecals BloodDecals = null!;
    internal Birds Birds = null!;
    internal Debris Debris = null!;
    internal Decapitation Decapitation = null!;
    internal CorpseFlies CorpseFlies = null!;

    internal CharacterEditorHost CharacterEditor = null!;
    internal LevelEditorHost LevelEditorHost = null!;
    internal InteriorEditorHost InteriorEditorHost = null!;

    internal DebugMenu DebugMenu = null!;
    internal DebugOverlays DebugOverlays = null!;
    internal DevConsoleHost DevConsoleHost = null!;
    internal AgentChannel AgentChannel = null!;
    internal AgentPlay AgentPlay = null!;
    internal AgentWorld AgentWorld = null!;
    internal PlayBench PlayBench = null!;
    internal ScreenshotScenes ScreenshotScenes = null!;
    internal FeedbackCapture FeedbackCapture = null!;

    /// <summary>
    /// Makes the feature classes; first in <see cref="Run"/>, before anything reads them.
    /// </summary>
    void CreateFeatures()
    {
        GameLoop = new(this);
        Sound = new(this);
        TitleScreen = new(this);
        NewGame = new(this);
        Saves = new(this);
        Modding = new(this);
        Battle = new(this);
        Duel = new(this);
        Sandbox = new(this);

        BuildingSync = new(this);
        FarmCrops = new(this);
        TownChanges = new(this);
        GroundItemSync = new(this);
        Interiors = new(this);
        WeatherFeed = new(this);
        ItemIconFiles = new(this);

        BloodDecals = new(this);
        Birds = new(this);
        Debris = new(this);
        Decapitation = new(this);
        CorpseFlies = new(this);

        CharacterEditor = new(this);
        LevelEditorHost = new(this);
        InteriorEditorHost = new(this);

        DebugMenu = new(this);
        DebugOverlays = new(this);
        DevConsoleHost = new(this);
        AgentChannel = new(this);
        AgentPlay = new(this);
        AgentWorld = new(this);
        PlayBench = new(this);
        ScreenshotScenes = new(this);
        FeedbackCapture = new(this);
    }

    #endregion

    #region World state

    internal WorldSession Session = null!;   // disposed with the host (Run)
    internal Simulation.Population.PopulationSystem? Population;

    // The game data the world was built from, kept to build the next world of a quickload (GameSaves).
    internal Simulation.Population.PopulationData? PopData;
    internal IReadOnlyList<Meitou.Data.World.TownPlacement> TownPlacements = [];

    // The level data, for the zone files and town handles of a save (null in the sandbox and the duel).
    internal Meitou.Data.World.WorldLevelData? SaveLevels;

    internal IWalkability? SimWalkability;
    internal Data.Characters.CharacterRigs? CharacterRigs;
    internal Simulation.Navigation.RagdollGeometry? RagdollGeometry;
    internal Simulation.Navigation.NavSystem? Nav;
    internal Data.Gameplay.AnimationLengths? AnimationLengths;
    internal Data.Gameplay.AnimationLibrary? AnimationLibrary;

    internal PlayerInterface Player = null!;
    internal Simulation.Factions.Squad? PlayerSquad;

    internal WorldCamera Camera = null!;
    internal WorldRenderOptions Render = null!;
    internal readonly Stopwatch RealTime = Stopwatch.StartNew();

    internal bool ScreenshotRequested;

    /// <summary>
    /// Frames in a row that could not be drawn while a control screenshot waits; at 30 the screenshot is answered with an error.
    /// </summary>
    internal int ShotNoFrame;

    // The interactive run's animation clock (foliage sway, clouds, water): the frame steps, which are exact while the cinema records; off, the real time.
    internal double AnimSeconds;
    internal bool AnimClock;

    // Draw-list state (GameHost.Characters.cs): the previous snapshot indexed by id, and the last camera focus sent to the world.
    internal WorldSnapshot? IndexedFor;
    internal readonly Dictionary<CharacterId, CharacterSnapshot> PreviousById = [];
    Vector3 _lastFocus;
    bool _sentFocus;

    // Profile of the interactive run (printed by --quit-after).
    internal double AdvanceTotal, FillTotal;
    internal long FillCalls;

    // --quit-after keeps the exposed menu's frame pacing separate from covered loading work.
    internal double MenuFrameSeconds, MenuLongestFrame;
    internal long MenuFrames, MenuSlowFrames;

    #endregion

    #region Renderer services

    internal WorldFrame.Gpu Gpu = null!;
    internal Rendering.Upscalers.Streamline? Streamline;
    internal Rendering.Upscalers.Ngx? Ngx;

    /// <summary>
    /// This run uses the native Metal loop: the shared world object exists, but its Vulkan post/loading path does not drive it.
    /// </summary>
    internal bool MetalRenderer = false;   // set by the Metal loop (GameHost.Metal.cs, macOS builds only)

    // Fallback services for a host whose renderers are not in WorldFrame.Gpu yet (the shared title, audio and save code asks through these).
    WorldWeather? _fallbackWeather = null;
    WorldObjectRenderer? _hostObjects = null;
    FoliageRenderer? _hostFoliage = null;
    CharacterRenderer? _hostCharacters = null;
    WorldLamps? _hostLamps = null;
    Func<float, float, float>? _hostGround = null;

    internal WorldWeather? ActiveWeather => Gpu is null ? _fallbackWeather : Gpu.Weather ?? _fallbackWeather;
    internal WorldObjectRenderer? ActiveObjects => Gpu is null ? _hostObjects : Gpu.Objects ?? _hostObjects;
    internal FoliageRenderer? ActiveFoliage => Gpu is null ? _hostFoliage : Gpu.Foliage ?? _hostFoliage;
    internal CharacterRenderer? ActiveCharacters => Gpu is null ? _hostCharacters : Gpu.Characters ?? _hostCharacters;
    internal WorldLamps? ActiveLamps => Gpu is null ? _hostLamps : Gpu.Lamps ?? _hostLamps;
    internal float ActiveGround(float x, float z) => _hostGround?.Invoke(x, z) ?? (Gpu is null ? 0 : Gpu.Terrain.HeightAt(x, z));

    #endregion

    #region Run

    public int Run()
    {
        CreateFeatures();
        try
        {
#if MEITOU_METAL
            if (GameOptions.Renderer == "metal") return Metal();
#endif
            if (GameOptions.Renderer != "vulkan")
            {
                Console.Error.WriteLine($"the {GameOptions.Renderer} renderer is macOS only.");
                return 2;
            }

            return WorldOptions.Screenshot is not null ? ScreenshotScenes.Screenshot() : GameLoop.Run();
        }
        finally
        {
            Sound.DisposeAudio();
            TitleScreen.DisposeTitleScene();
            Session?.Dispose();
            Nav?.Dispose();
            RagdollGeometry?.Dispose();
            OwnedScene?.Dispose();
        }
    }

    /// <summary>
    /// The start opens on the title (no start, save, sandbox or duel was asked for).
    /// </summary>
    internal bool OpensOnTitle(bool interactive)
    {
        if (GameOptions.TitlePreview) return true;

        bool startAsked = start is not null || StartSave is not null || Sandbox.IsSandbox || Duel.IsDuel || Battle.IsBattle;
        return interactive && !startAsked && !GameOptions.NoPopulation;
    }

    #endregion

    #region Boot

    /// <summary>
    /// The far cadence (docs/simulation.md "Far cadence"): a character far from the camera and from every player character steps once in this many ticks.
    /// </summary>
    internal const int FarTickPeriod = 4;

    internal void Boot(VulkanDisplay display, bool interactive)
    {
        WorldOptions.Day ??= (int)GameClock.DefaultStartDay;   // the weather schedule starts on the clock's first day (--day moves both)

        // --crowd in the game: simulated characters (SpawnCrowd), not the viewer's posed harness, which would take the renderer's place.
        Sandbox.Crowd = WorldOptions.Crowd;
        WorldOptions.Crowd = 0;

        // The simulation's data (the navmesh system, the towns' homes, the population's data and systems) is read on a worker while the renderer is made
        // here, a step at a time with a loading frame between the steps (docs/engine.md "Loading").
        var simulation = Task.Run(() =>
        {
            var grid = new Meitou.Data.World.GroundHeights(Scene.Window, Scene.Coarse, Scene.CoarseSize, WorldFrame.CoarseStep);
            return (Grid: grid, Parts: BootSimulation(grid, interactive));
        });
        CreateRenderer(display, interactive);

        LoadingStep("people");
        var (_, (walkability, systems, constants)) = AwaitLoading(simulation);

        LoadingStep("world");
        CreateSession(walkability, systems, constants);
        BootPlayer(display, interactive);
    }

    /// <summary>
    /// Makes the world's renderers (a step at a time between loading frames), the camera and the render options.
    /// </summary>
    void CreateRenderer(VulkanDisplay display, bool interactive)
    {
        var context = display.Context;
        Gpu = WorldFrame.CreateGpu(context, Install, Scene, Assets, WorldOptions, interactive, LoadingStep, AwaitLoading);
        if (!WorldOptions.NoParticles)
            TitleScreen.TitleParticleFactory = () => ParticleRenderer.Create(context, Assets, Gpu.Particles?.Library);
        if (Gpu.Post is { } vendorPost)
            vendorPost.UpscalerFactory = Rendering.Upscalers.VendorUpscalers.Factory(context, Streamline, Ngx);

        (Camera, Render) = WorldFrame.Setup(Scene, WorldOptions);
        Render.Grid = Battle.IsFlat;
    }

    /// <summary>
    /// Makes the world session: its clock from the game constants (read from the data when the population did not), its simulation settings and systems.
    /// </summary>
    void CreateSession(IWalkability walkability, IReadOnlyList<ITickSystem> systems, Data.Gameplay.GameConstants? constants)
    {
        constants ??= Scene.Database is { } clockDb ? Data.Gameplay.GameConstants.FromDatabase(clockDb) : null;
        var clock = GameClockFor(constants, WorldOptions.Hour, WorldOptions.Day ?? GameClock.DefaultStartDay);

        var settings = new WorldSettings
        {
            Seed = GameOptions.Seed,
            MinPartitionSize = 32,
            Threads = Math.Max(1, Config.SimThreads ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 8)),
            Sunrise = (float)clock.Sunrise,
            Sunset = (float)clock.Sunset,
            FarTickPeriod = FarTickPeriod,
        };
        Duel.WorldSettings = settings;

        var bounds = (Scene.X0, Scene.Z0, Scene.X1, Scene.Z1);
        Session = new WorldSession(Scene.Focus, bounds, Gpu.Terrain.HeightAt, WorldOptions.Hour, Config.TickRate,
            clock: clock, simulation: settings, systems: systems, walkability: walkability);

        Session.World.BenchFacingFix = Config.FixBenchFacing;
        Session.Simulation.SetFastSpeeds(Config.FastSpeed, Config.FastestSpeed);
        if (Gpu.Characters is { } nearChars)
            Session.World.NearRadius = nearChars.NearReach;   // every drawn character steps every tick (docs/simulation.md "Far cadence")
    }

    /// <summary>
    /// The simulation's side of <see cref="Boot"/>, on a worker (it reads the game data and the scene's level data and makes no GPU resource): the world's
    /// content (<see cref="WorldBoot"/>), the code mods and the systems. Returns the walkability, the systems and the game constants for the session.
    /// </summary>
    internal (IWalkability Walkability, IReadOnlyList<ITickSystem> Systems, Data.Gameplay.GameConstants? Constants) BootSimulation(
        Meitou.Data.World.GroundHeights heights, bool interactive)
    {
        // The simulation samples the CPU heightmap (immutable, any thread), not the renderer's terrain.
        IWalkability walkability = new OpenGroundWalkability(heights.HeightAt);
        IReadOnlyList<ITickSystem> systems = [];
        Data.Gameplay.GameConstants? constants = null;   // parsed once: the body factory's, else for the clock alone

        if (!GameOptions.NoPopulation && Scene.Database is { } gameDb)
        {
            // The sandbox, the duel and the battle walk on open ground (the heightmap): the sandbox has no world, and the duel's spot may be a navmesh island
            // the build prunes. None has towns: their own characters are the only ones.
            bool ownCharactersOnly = Sandbox.IsSandbox || Duel.IsDuel || Battle.IsBattle;
            var levels = ownCharactersOnly ? null : Scene.Objects?.Levels ?? WorldBoot.LoadLevels(Install, gameDb);
            var boot = WorldBoot.Load(BootOptions(gameDb, heights, levels));
            KeepWorldContent(boot, levels);
            Modding.StartMods(gameDb, boot.Data);
            walkability = SimWalkability = boot.Walkability;

            var built = Saves.BuildSystems(interactive);
            Population = built.Population;
            if (Nav is not null) Population!.ZoneGate = zone => Nav.Walkability.World.Contains(zone);

            Saves.SnapshotPlayerTowns();
            systems = built.Systems;
            constants = boot.Data.Bodies.Constants;
        }

        Modding.StartDeferredWarmup();
        return (walkability, systems, constants);
    }

    /// <summary>
    /// Keeps what the world was loaded with for the renderers, the saves and the next world of a quickload.
    /// </summary>
    void KeepWorldContent(WorldBoot boot, Meitou.Data.World.WorldLevelData? levels)
    {
        Nav = boot.Nav;
        RagdollGeometry = boot.RagdollGeometry;
        CharacterRigs = boot.Rigs;
        AnimationLengths = boot.AnimationLengths;
        AnimationLibrary = boot.AnimationLibrary;
        PopData = boot.Data;
        TownPlacements = boot.Towns;
        SaveLevels = levels;
    }

    /// <summary>
    /// The world's content as this scene loads it: its levels and the renderer's layouts, the body settings, and the nav zones reported to the host.
    /// </summary>
    WorldBootOptions BootOptions(Data.GameDatabase gameDb, Meitou.Data.World.GroundHeights heights, Meitou.Data.World.WorldLevelData? levels) =>
        new(Install, gameDb, heights, Assets)
        {
            Levels = levels,
            Layouts = Scene.Objects?.Layouts,
            Bodies = Simulation.Bodies.BodyOptions.Default with
            {
                Dismemberment = Saves.Gameplay.Dismemberment,
                Decapitation = Config.Decapitation,
                AdvancedRagdolls = Config.AdvancedRagdolls,
            },
            ZoneReady = ready =>
            {
                lock (_navReady)
                    _navReady.Add(ready.Zone);
            },
        };

    /// <summary>
    /// The game clock with sunrise, sunset and days per year from the CONSTANTS record (defaults without data).
    /// </summary>
    internal static GameClock GameClockFor(Data.Gameplay.GameConstants? c, double startHour, long startDay)
    {
        if (c is null) return new GameClock(startHour, startDay);

        return new GameClock(startHour, startDay, sunrise: c.Sunrise, sunset: c.Sunset, daysPerYear: c.DaysPerYear);
    }

    #endregion

    #region Boot: the player

    /// <summary>
    /// The rest of <see cref="Boot"/> once the session exists: bindings, the player's interface, the start (a new game's squad, the sandbox, the duel), the
    /// camera, a <c>--load</c> save.
    /// </summary>
    void BootPlayer(VulkanDisplay display, bool interactive)
    {
        var context = display.Context;
        var target = Camera.Target;
        MigrateBindings();

        LoadingStep("interface");
        CreatePlayer(context);
        ConnectPicking();
        ConfigurePlayerInterface(Player);
        Player.LevelEditorPanel.LevelHost = LevelEditorHost.MakeLevelEditorServices();
        ConfigureFrontend(interactive);

        LoadStartZones(interactive);
        target = PlaceStart(target, interactive);

        LoadingStep("characters");
        CreateCharacterRenderer(context);
        PlaceCamera(target);
        LoadStartSave(interactive);

        if (interactive)
        {
            LoadingStep("finishing");
            WorldFrame.FinishLoading(context);
            ReportLoading("start");
        }
    }

    /// <summary>
    /// Applies the config's bindings, then moves those of older saved configs off the keys the game has since given to other actions.
    /// </summary>
    void MigrateBindings()
    {
        var bindings = Session.Bindings;
        foreach (var problem in bindings.Apply(Config.Bindings))
            Console.Error.WriteLine($"config    binding skipped: {problem}");

        // Right is the command button by default; older saved configs bound it to orbit (kept when Command has moved off it).
        if (bindings.Get(InputAction.Command).Contains(Binding.Of(EngineButton.Right)))
            bindings.Set(InputAction.Orbit, [.. bindings.Get(InputAction.Orbit).Where(b => !(b.IsMouse && b.Button == EngineButton.Right))]);

        // Left Control orbits as the middle button does (mouse_rotate, FUN_1403636c0: only code 0x1d); older saved configs had the middle button alone.
        if (BoundOnlyTo(InputAction.Orbit, Binding.Of(EngineButton.Middle)))
            bindings.Set(InputAction.Orbit, EngineButton.Middle, EngineKey.LeftControl);

        // F12 is the profiler now (as in the viewer); older saved configs took it for the screenshot.
        if (BoundOnlyTo(InputAction.Screenshot, Binding.Of(EngineKey.F12)))
            bindings.Set(InputAction.Screenshot, EngineKey.F8, EngineKey.PrintScreen);

        // The profiler and debug menu were briefly on F10 and Insert; configs saved then go back to F12 and F10.
        if (BoundOnlyTo(InputAction.CycleProfiler, Binding.Of(EngineKey.F10)) && BoundOnlyTo(InputAction.ToggleDebugMenu, Binding.Of(EngineKey.Insert)))
        {
            bindings.Set(InputAction.CycleProfiler, EngineKey.F12);
            bindings.Set(InputAction.ToggleDebugMenu, EngineKey.F10);
        }

        // F9 is the game's quickload now (F5 quicksave); older saved configs took F9 for the fps counter, which is on Ctrl+F9 and unbound here.
        if (BoundOnlyTo(InputAction.ToggleFps, Binding.Of(EngineKey.F9))) bindings.Set(InputAction.ToggleFps);

        // Tab is the game's change_squad, not the settings (they open from the Esc menu); older saved configs bound Tab to them.
        if (BoundOnlyTo(InputAction.ToggleSettings, Binding.Of(EngineKey.Tab)))
            bindings.Set(InputAction.ToggleSettings);
    }

    bool BoundOnlyTo(InputAction action, Binding binding) => Session.Bindings.Get(action).SequenceEqual([binding]);

    /// <summary>
    /// Makes the player's interface with the config's settings, and the overlays it draws with.
    /// </summary>
    void CreatePlayer(Rendering.Gpu.GpuContext context)
    {
        // Picks against the drawn terrain (the streamed fine window): the boot window's heights fall back to a 144-unit grid away from the start.
        Player = new PlayerInterface(Session, Gpu.Terrain.HeightAt, DrawnPosition);
        Player.LoadUi(GameOptions.UiMode ?? Config.UiMode, Assets.Find);
        Player.SpeedUnit = Config.SpeedUnit;
        Player.ShowNames = Config.ShowNames;
        Player.Floaters = Config.Floaters;
        Player.FeedbackButtonInPlay = Config.FeedbackButton;
        Player.Frontend.FixLoadButton = Config.FixLoadButton;
        Player.BuildingSelection.DeselectBuildings = Config.DeselectBuildings;

        // The region (BIOME_GROUP) under a point, for the biome banner.
        Player.BiomeBanner.RegionAt = (x, z) => Gpu.Weather?.World.RegionAt(x, z).Def is { } def ? (def.StringId, def.Name) : null;

        Player.Markers = Gpu.Markers ??= MarkerRenderer.Create(context, Assets.Find);
        BloodDecals.SetUpBlood(context);
        Gpu.HoverOutline ??= new HoverOutlineRenderer(context);
        Gpu.NavmeshOverlay ??= new NavmeshOverlayRenderer(context);
        DebugOverlays.NavOverlayRenderer = Gpu.NavmeshOverlay;
        Gpu.InspectorOverlay ??= new NavmeshOverlayRenderer(context);
    }

    /// <summary>
    /// What the interface's picks and visibility tests ask the drawn world: character meshes and heads, buildings, floors, doors, ground items.
    /// </summary>
    void ConnectPicking()
    {
        Player.Picking.MeshHit = (id, origin, direction) => Gpu.Characters?.RayHit(DrawKey(id), origin, direction);
        Player.Picking.BuildingMoveHit = (origin, direction, distance) => Gpu.Objects?.MoveHit(origin, direction, Interiors.ViewedFloorValue, distance);
        Player.Picking.SurfaceMoveHit = Interiors.SurfaceMoveHit;
        Player.Picking.ClosedBuildingHit = (origin, direction, distance) => Gpu.Objects?.ClosedBuildingHit(origin, direction, distance);
        Player.Picking.ShownInteriorHit = (origin, direction, distance) => Gpu.Objects?.ShownInteriorHit(origin, direction, distance);

        Player.Floor = (() => Interiors.ViewedFloorValue, Interiors.StepFloor);
        Player.StealthView.HiddenInWorld = Interiors.HiddenIndoors.Contains;
        Player.StealthView.DrawnHead = id => Gpu.Characters?.BonePosition(DrawKey(id), "Bip01 Head");
        Player.Furniture.AboveViewedFloor = Interiors.AboveViewedFloor;
        Player.Furniture.DoorShape = (record, door) => Nav?.Walkability.DoorGeometry?.Shape(record, door) ?? [];

        Player.GroundItemPicks.GroundItemShape = GroundItemSync.GroundShape;
        Player.GroundItemPicks.GroundItemHidden = GroundItemSync.HiddenGround.Contains;
    }

    /// <summary>
    /// The title and menus: the save list, the credits, the data problems and mod list, and the title itself when the run opens on it.
    /// </summary>
    void ConfigureFrontend(bool interactive)
    {
        if (Sandbox.IsSandbox) Player.ReservedKeys.UnionWith([Key.Z, Key.X, Key.C, Key.Y, Key.P, Key.G]);

        string localSaves = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kenshi", "save");
        string credits = Assets.Find("credits.txt") is { } creditsFile ? File.ReadAllText(creditsFile) : "";
        Player.Frontend.ConfigureFrontend(Scene.Database,
            () => Data.Save.SaveCatalog.Scan(Simulation.Saving.QuickSave.DefaultRoot, Install.SaveDirectory, localSaves), credits);

        Player.TitleMenu.DataProblems = (Scene.Database?.Issues.Count ?? 0) + (Scene.Objects?.Levels.Problems().Count ?? 0);
        if (Scene.Database is { } db)
        {
            Player.TitleMenu.Mods = ModReport.Build(Install, db, Modding.Mods?.Refused);
            LogMods(Player.TitleMenu.Mods);
        }

        if (OpensOnTitle(interactive)) Player.Frontend.ShowTitle();
    }

    /// <summary>
    /// Logs the mod list the game data was loaded from: enabled (the names the list file gives), loaded (those whose .mod file was found), problems.
    /// </summary>
    void LogMods(ModReport report)
    {
        string file = ModList.ModsCfgOverride ?? "data/mods.cfg";
        string problems = report.Problems == 1 ? "problem" : "problems";
        Console.WriteLine($"mods      {report.Enabled} enabled, {report.Loaded} loaded, {report.Problems} {problems}, from {file}");
    }

    /// <summary>
    /// Waits for the navmesh round the start when a new game's squad is placed on it or a still is taken: the squad stands on the mesh, and a still has its
    /// town at once (a cold build is about 2 s, then cached).
    /// </summary>
    void LoadStartZones(bool interactive)
    {
        if (Nav is null || start is null && interactive) return;

        var watch = Stopwatch.StartNew();
        var zones = Simulation.Population.ZoneActivation.ZonesAround(Scene.Focus, 0);
        LoadingStep("navmesh");
        AwaitLoading(Task.WhenAll(zones.Select(z => (Task)Nav.LoadZone(z))));

        foreach (var z in zones)
            NavZones[z] = Stopwatch.GetTimestamp();
        Console.WriteLine($"navmesh   {zones.Count} zone(s) round the start ready in {watch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Places the start's characters (a new game's squad, the sandbox, the duel, the battle, a <c>--crowd</c>) and returns where the camera looks first.
    /// </summary>
    Vector3 PlaceStart(Vector3 target, bool interactive)
    {
        if (Population is null) return target;

        if (start is not null)
        {
            PlayerSquad = Population.StartPlayer(Session.World, start);
            var lead = Session.World.Characters.Previous[PlayerSquad.Leader.Slot].Position;
            target = lead;
            Console.WriteLine($"player    {PlayerSquad.Members.Count} characters, leader at {lead.X:0}, {lead.Z:0}");

            // --new-game is a shortcut past the menus: the start's characters stay as rolled (the menu's New game opens the character editor); a still keeps
            // the creation open for the showcase's editor pictures.
            if (interactive)
                Session.World.Commands.Enqueue(new Simulation.Editing.FinishCreation { Tick = Session.World.Tick });
        }

        if (Sandbox.IsSandbox) target = Sandbox.SandboxSpawn();
        if (Duel.IsDuel) target = Duel.DuelStart();
        if (Battle.IsBattle) target = Battle.BattleStart();
        if (!Sandbox.IsSandbox && Sandbox.Crowd > 0) Sandbox.SpawnCrowd(target, Sandbox.Crowd);

        return target;
    }

    void CreateCharacterRenderer(Rendering.Gpu.GpuContext context)
    {
        if (Scene.Database is not { } characterDb || GameOptions.NoPopulation || Gpu.Characters is not null) return;

        Gpu.Characters = new CharacterRenderer(context, Install, characterDb, Assets, CharacterRigs)
        {
            DrawDistance = GameplaySettings.NpcRange(Install),
            Source = FillDrawList,
            Guard = Gpu.Guard,
            GeneratedLod = WorldOptions.CharacterLod,
            SkinShading = WorldOptions.SkinShading,
            SkinRaces = Config.SkinRaces,
            EnhancedIcons = WorldOptions.MeitouIcons,
            // The frame clock DrawWorld gives the foliage sway: a recorded flight poses the same every take.
            PoseClock = () => AnimClock ? AnimSeconds : RealTime.Elapsed.TotalSeconds
        };
    }

    void PlaceCamera(Vector3 target)
    {
        var yaw = (WorldOptions.Yaw ?? 30) * MathF.PI / 180;
        var pitch = (WorldOptions.Pitch ?? Data.World.KenshiCamera.InitialPitchDegrees) * MathF.PI / 180;
        var distance = WorldOptions.Distance ?? Data.World.KenshiCamera.InitialDistance;
        Session.Camera.Place(new Vector2(target.X, target.Z), yaw, pitch, distance);

        SendFocus();
        ApplyCamera(Session.Camera.Current);
        if (TitleScreen.TitleVisible) TitleScreen.ApplyTitleCamera();
    }

    /// <summary>
    /// Swaps in the <c>--load</c> save through the menu's load path and waits for its navmesh.
    /// </summary>
    void LoadStartSave(bool interactive)
    {
        if (StartSave is not { } save || GameOptions.Load is not { } folder) return;

        Saves.ReadingFolder = folder;
        Task navmesh;
        string summary;
        try
        {
            navmesh = Saves.SwapInSave(Task.FromResult(save), interactive, out summary);
        }
        catch (SceneLoadFailed e)
        {
            throw new InvalidDataException($"--load {folder}: {e.Message}");
        }

        LoadingStep("navmesh");
        AwaitLoading(navmesh);
        SettleNewWorld();
        Saves.FinishLoadNotice(summary);
    }

    #endregion

    #region Player interface services

    /// <summary>
    /// The callbacks the interface asks the host for: the game data, the map, the build services, the tooltips and the character editor. Shared by the Vulkan
    /// and Metal loops so the two expose the same interaction layer.
    /// </summary>
    internal void ConfigurePlayerInterface(PlayerInterface ui)
    {
        Modding.ConnectModUi(ui);
        ConnectSaves(ui);

        // The region (BIOME_GROUP) under a point, for the biome banner.
        ui.BiomeBanner.RegionAt = (x, z) => ActiveWeather?.World.RegionAt(x, z).Def is { } def ? (def.StringId, def.Name) : null;
        ui.Build.BuildHost = BuildingSync.MakeBuildServices();
        ui.SquadWindows.SquadLimits = () => PopData?.Bodies.Constants;
        ui.Furniture.Rocks = () => PopData?.ResourceNodes;

        ConnectFactions(ui);
        ConnectBuildings(ui);
        ConnectWorldMap(ui);
        ConnectItems(ui);
        CharacterEditor.ConfigureEditor(ui);
    }

    void ConnectSaves(PlayerInterface ui)
    {
        ui.Frontend.FixLoadButton = Config.FixLoadButton;
        ui.Frontend.SaveInUse = SaveInUse;
        ui.Frontend.DeleteSave = DeleteSave;
    }

    /// <summary>
    /// The save folder is the one the running world was loaded from or last saved to.
    /// </summary>
    bool SaveInUse(string folder)
    {
        if (!Session.World.Player.Exists || Saves.SaveInUse is not { } used) return false;

        return string.Equals(Path.GetFullPath(used), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Deletes a save folder; returns the message to show when that failed, else null.
    /// </summary>
    static string? DeleteSave(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "Failed to delete save: " + e.Message;
        }
    }

    void ConnectFactions(PlayerInterface ui)
    {
        ui.FactionName = FactionNameOf;
        ui.PlayerFactionName = () => ui.FactionName?.Invoke(Session.World.Player.Faction) ?? "";

        // The rename's test (FUN_1402e7950, as RenameFactionCommand's): another faction of the data has the name.
        ui.FactionWindow.FactionNameTaken = name =>
            PopData is { } fd && fd.Factions.Where((f, i) => i != Session.World.Player.Faction).Any(f => f.Name == name);

        // The faction tab's list (docs/game/ui-screens.md "Faction tab"): every faction the player has met but the player's, the `not real` ones and the
        // generation template, with the player's relation towards it.
        ui.FactionWindow.FactionRelations = () =>
        {
            if (PopData is not { } d || Session.World.Player.Faction is not (>= 0 and var own) || !d.Relations.Contains(own))
                return [];

            var known = Session.CurrentSnapshot.KnownFactions;
            return [.. d.Relations.Factions
                .Select((f, i) => (f, i))
                .Where(p => p.i != own && known.Contains(p.i) && !p.f.NotReal && p.f.Id != "GENERATION_TEMPLATE_FACTION")
                .Select(p => (p.f.Name, d.Relations.Get(own, p.i)))];
        };

        // Its info panel's contracts and events, read from the live world as the list above is (a collection changed mid-read shows the panel empty for that
        // frame).
        ui.FactionWindow.FactionInfo = () =>
        {
            try
            {
                return (Session.World.ActiveContracts(ui.FactionName), Population?.UpcomingEvents(Session.World, ui.FactionName) ?? []);
            }
            catch (InvalidOperationException)
            {
                return ([], []);
            }
        };
    }

    /// <summary>
    /// A faction's name: the player's own as they renamed it, else the data's.
    /// </summary>
    string? FactionNameOf(int faction)
    {
        if (faction >= 0 && faction == Session.World.Player.Faction && Session.World.Player.FactionName is { } renamed)
            return renamed;

        return PopData is { } d && faction >= 0 && faction < d.Factions.Count ? d.Factions[faction].Name : null;
    }

    void ConnectBuildings(PlayerInterface ui)
    {
        var selection = ui.BuildingSelection;

        // A BUILDING's construction target (its Condition rows): the sum of its materials' required amounts.
        selection.BuildTarget = record => BuildingDataOf(record)?.Materials.Sum(m => m.Required);

        // The upgrade chain of a BUILDING (the panel's [UPGRADE] / [DOWNGRADE]) and its build data (their tooltips' materials).
        selection.UpgradeOf = record => PopData is { } d ? Simulation.Construction.BuildingUpgrades.UpgradeOf(d.Db, record) : null;
        selection.DowngradeOf = record => PopData is { } d ? Simulation.Construction.BuildingUpgrades.DowngradeOf(d.Db, record) : null;
        selection.BuildingData = BuildingDataOf;
        selection.PowerOutput = record => PopData?.Db.Find(record)?.GetInt("power output");
    }

    Data.Gameplay.Construction.BuildingBuildData? BuildingDataOf(string record) =>
        PopData?.Db.Find(record) is { Type: Data.Fcs.FcsRecordType.Building } r ? Data.Gameplay.Construction.BuildingBuildData.From(r) : null;

    void ConnectWorldMap(PlayerInterface ui)
    {
        ui.WorldMap.MapTowns = () => PopData?.Sites ?? [];
        ui.WorldMap.MapFactionOf = MapFactionOf;
        ui.WorldMap.MapFactionId = faction => PopData is { } d && faction >= 0 && faction < d.Factions.Count ? d.Factions[faction].Id : null;
        ui.WorldMap.MapItemTowns = record =>
        {
            if (PopData is not { } d || d.Db.Find(record) is not { Type: Data.Fcs.FcsRecordType.MapItem } map)
                return null;

            var revealed = Simulation.Items.MapItems.Reveals(d, map, Session.CurrentSnapshot.Towns, Session.World.Seed);
            return [.. revealed.Select(i => d.Sites[i].Town.Name)];
        };
    }

    /// <summary>
    /// A town's faction on the map, with the player's standing towards it.
    /// </summary>
    MapFaction? MapFactionOf(string? id)
    {
        if (PopData is not { } d || id is null || d.FactionIndex(id) is not (>= 0 and var town)) return null;

        var faction = d.Factions[town];
        if (Session.World.Player.Faction is not (>= 0 and var mine))
            return new MapFaction(faction.Name, 0, MapStanding.Neutral, faction.NotReal);

        float relation = d.Relations.Get(mine, town);
        var standing = town == mine ? MapStanding.Player
            : d.Relations.IsAlly(mine, town) ? MapStanding.Ally
            : relation < -30 ? MapStanding.Enemy
            : MapStanding.Neutral;
        return new MapFaction(faction.Name, relation, standing, faction.NotReal);
    }

    void ConnectItems(PlayerInterface ui)
    {
        ui.ArmourOf = record => PopData?.Combat.Armour(record);
        ui.RecordName = id => PopData?.Db.Find(id)?.Name;
        ui.PricesOf = PriceLookup();
        ui.GroundItemPicks.ArtifactOf = id => PopData?.Db.Find(id)?.GetBool("artifact") == true;

        var specialFoodRaces = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        ui.TooltipRuntime.ItemRecordOf = id => PopData?.Db.Find(id);
        ui.TooltipRuntime.SpecialFoodRaces = id =>
            PopData is { } d ? specialFoodRaces.GetOrAdd(id, item => Data.Gameplay.Bodies.SpecialFood.EatenBy(d.Db, item)) : "";
        ui.TooltipRuntime.CombatOf = () => PopData?.Combat;
        ui.TooltipRuntime.RangedDamageMultiplier = () => PopData?.BodyOptions.GlobalDamageMultiplier ?? 1;
    }

    /// <summary>
    /// The tooltips' prices of an item: gear by its own price rules, anything else at a shelf with no trader. The price constants and the shelf are made on
    /// first use.
    /// </summary>
    Func<TooltipRuntime.ItemTip, (int Value, int Sell, int? Average)?> PriceLookup()
    {
        Data.Gameplay.Economy.PriceConstants? priceConstants = null;
        Simulation.Trading.Market? shelf = null;
        return tip =>
        {
            if (PopData is not { } d || d.Db.Find(tip.Record) is not { } r) return null;

            if (r.Type is Data.Fcs.FcsRecordType.Armour or Data.Fcs.FcsRecordType.Weapon or Data.Fcs.FcsRecordType.Crossbow)
            {
                priceConstants ??= Data.Gameplay.Economy.PriceConstants.FromDatabase(d.Db);
                return Simulation.Trading.Market.GearPrices(d.Db, priceConstants, tip.Record, tip.Level, tip.Company) is { } gear
                    ? (gear.Value, gear.Sell, null) : null;
            }

            // No trader, so no town or culture factor (the seed only rolls those).
            var market = shelf ??= new Simulation.Trading.Market(d, 0);
            var item = ShelfItem(d.Db, r.Type, tip);
            var none = Simulation.Trading.Trader.None;
            return (market.UnitPrice(item, false, none, -1), market.UnitPrice(item, true, none, -1), market.AveragePrice(item));
        };
    }

    /// <summary>
    /// The item a tooltip shows, as a shelf would hold it. StolenFrom is set only when the item is stolen for its holder; holder -1 keeps that.
    /// </summary>
    static Simulation.Items.ItemInstance ShelfItem(Data.GameDatabase db, Data.Fcs.FcsRecordType type, TooltipRuntime.ItemTip tip) => new()
    {
        Record = tip.Record,
        Type = db.Find(tip.Company) is { Type: Data.Fcs.FcsRecordType.Research } ? Data.Fcs.FcsRecordType.Blueprint : type,
        Quality = tip.Quality,
        Level = tip.Level,
        CompanyId = tip.Company,
        Charges = tip.Charges,
        MaxCharges = tip.MaxCharges,
        Quantity = tip.Quantity,
        StolenFrom = tip.StolenFrom,
    };

    #endregion

    #region Camera

    internal void ApplyCamera(CameraState s)
    {
        Camera.Target = s.Target;
        Camera.Yaw = s.Yaw;
        Camera.Pitch = s.Pitch;
        Camera.Distance = s.Distance;
    }

    /// <summary>
    /// Gives the player interface the camera it picks with.
    /// </summary>
    internal void UpdateView(int width, int height)
    {
        Player.ViewProjection = Camera.View * Camera.Projection(width / (float)Math.Max(height, 1), Camera.Near, Camera.ViewDistance);
        Player.Eye = Camera.Eye;
        Player.Width = width;
        Player.Height = height;
    }

    #endregion

    #region Nav streaming

    /// <summary>
    /// Seconds between <see cref="Meitou.Simulation.RepathCommand"/>s at least: zones go live in runs along a long walk, and are sent together (engine choice).
    /// </summary>
    const double RepathCoalesceSeconds = 1;

    // Meitou's asset cache (engine choice): a left zone's mesh stays NavCacheSeconds, at most NavCacheZones zones, the oldest dropped first, so a return within
    // two minutes rebuilds nothing.
    const double NavCacheSeconds = 120;
    const int NavCacheZones = 60;

    /// <summary>
    /// The loaded zones and when each was last wanted (a <see cref="Stopwatch"/> timestamp).
    /// </summary>
    internal readonly Dictionary<Meitou.Data.World.ZoneCoordinate, long> NavZones = [];

    /// <summary>
    /// Zones whose mesh went live since the last <see cref="Meitou.Simulation.RepathCommand"/> (filled on the nav thread, under its own lock).
    /// </summary>
    readonly HashSet<Meitou.Data.World.ZoneCoordinate> _navReady = [];
    long _lastRepath;

    // The batch of zones being dropped on the pool, and its task.
    Task _navUnload = Task.CompletedTask;
    HashSet<Meitou.Data.World.ZoneCoordinate> _navUnloading = [];

    /// <summary>
    /// Waits for the zones being dropped (a load right after must not be undone by it).
    /// </summary>
    internal void FinishNavUnload()
    {
        if (_navUnloading.Count == 0) return;

        _navUnload.Wait();
        _navUnloading = [];
        _relabelGates = true;
    }

    /// <summary>
    /// Keeps the navmesh on the zones the population has active (built on the nav thread, cached), drops the ones idle too long, and has the characters whose
    /// way leads through a zone whose mesh arrived ask for their paths again (their first path may have crossed a building), at most every
    /// <see cref="RepathCoalesceSeconds"/>.
    /// </summary>
    internal void UpdateNav(bool wait = false)
    {
        if (Nav is null || Population is null) return;

        UpdateGateRegions(Nav);
        long now = Stopwatch.GetTimestamp();

        // Dropping zones republishes the whole mesh (tens of ms): it runs on the pool, one batch at a time, and a zone being dropped is loaded again once that
        // is done.
        if (_navUnloading.Count > 0 && _navUnload.IsCompleted)
        {
            _navUnloading = [];
            _relabelGates = true;
        }
        if (wait) FinishNavUnload();

        var pending = LoadWantedZones(Nav, Population, now);
        DropStaleZones(Nav, now);
        if (wait) Task.WaitAll([.. pending]);

        SendRepath(pending, wait, now);
    }

    /// <summary>
    /// Starts loading the population's zones and those the walkers' long routes lead into (NavmeshWalkability.RouteZones), and marks them wanted now.
    /// </summary>
    List<Task<Simulation.Navigation.NavZoneReady>> LoadWantedZones(
        Simulation.Navigation.NavSystem nav, Simulation.Population.PopulationSystem population, long now)
    {
        var pending = new List<Task<Simulation.Navigation.NavZoneReady>>();
        foreach (var z in population.ActiveZones.Concat(nav.Walkability.RouteZones(TimeSpan.FromSeconds(30))))
        {
            if (_navUnloading.Contains(z)) continue;
            if (!NavZones.ContainsKey(z)) pending.Add(nav.LoadZone(z));

            NavZones[z] = now;
        }

        return pending;
    }

    /// <summary>
    /// Drops the zones unwanted for <see cref="NavCacheSeconds"/>, and the oldest past <see cref="NavCacheZones"/>, once no other batch is being dropped.
    /// </summary>
    void DropStaleZones(Simulation.Navigation.NavSystem nav, long now)
    {
        var stale = NavZones.Where(e => Stopwatch.GetElapsedTime(e.Value, now).TotalSeconds > NavCacheSeconds).Select(e => e.Key).ToList();
        int overCache = NavZones.Count - stale.Count - NavCacheZones;
        if (overCache > 0)
            stale.AddRange(NavZones.Where(e => !stale.Contains(e.Key)).OrderBy(e => e.Value).Take(overCache).Select(e => e.Key));

        if (stale.Count == 0 || _navUnloading.Count > 0) return;

        foreach (var z in stale)
            NavZones.Remove(z);
        _navUnloading = [.. stale];
        _navUnload = Task.Run(() => nav.UnloadZones(stale));
    }

    /// <summary>
    /// Sends the zones gone live as one <see cref="RepathCommand"/>, at most every <see cref="RepathCoalesceSeconds"/> unless waited for.
    /// </summary>
    void SendRepath(List<Task<Simulation.Navigation.NavZoneReady>> pending, bool wait, long now)
    {
        HashSet<Meitou.Data.World.ZoneCoordinate> zones;
        lock (_navReady)
        {
            // The event follows the task's completion, so zones waited for are added here.
            if (wait)
            {
                foreach (var task in pending)
                    _navReady.Add(task.Result.Zone);
            }

            bool coalescing = !wait && Stopwatch.GetElapsedTime(_lastRepath, now).TotalSeconds < RepathCoalesceSeconds;
            if (_navReady.Count == 0 || coalescing) return;

            zones = [.. _navReady];
            _navReady.Clear();
        }

        _lastRepath = now;
        _relabelGates = true;
        Session.World.Commands.Enqueue(new RepathCommand { Tick = Session.World.Tick, Zones = zones });
    }

    #endregion

    #region Gate codes

    /// <summary>
    /// The navmesh changed since the gate codes were made (a zone went live or was dropped).
    /// </summary>
    bool _relabelGates;
    Task<Simulation.Navigation.NavGateRegions>? _gateLabelling;
    Simulation.Navigation.NavGateRegions? _gateRegions;
    object? _gateRegionsFor;

    /// <summary>
    /// Keeps the simulation's gate codes (docs/game/pathfinding.md "Gate codes") up to date: labels the loaded meshes on the pool after they changed, one
    /// labelling at a time, and hands each result to the world (again to a new world) as a <see cref="Meitou.Simulation.Locks.GateRegionsCommand"/>.
    /// </summary>
    void UpdateGateRegions(Simulation.Navigation.NavSystem navSystem)
    {
        if (_gateLabelling is { IsCompleted: true } done)
        {
            if (done.Status == TaskStatus.RanToCompletion)
            {
                _gateRegions = done.Result;
                _gateRegionsFor = null;
            }
            else
            {
                Console.Error.WriteLine($"nav       gate codes failed: {done.Exception?.GetBaseException().Message}");
            }

            _gateLabelling = null;
        }

        if (_relabelGates && _gateLabelling is null)
        {
            _relabelGates = false;
            _gateLabelling = Task.Run(navSystem.LabelGates);
        }

        if (_gateRegions is { } regions && !ReferenceEquals(_gateRegionsFor, Session.World))
        {
            _gateRegionsFor = Session.World;
            Session.World.Commands.Enqueue(new Simulation.Locks.GateRegionsCommand(regions) { Tick = Session.World.Tick });
        }
    }

    #endregion

    #region Frame

    internal void DrawWorld(int width, int height)
    {
        SyncPreviewLook();
        if (!Player.CharacterEditor.EditorOpen) CharacterEditor.Bar.ClearEditorBar();

        if (TitleScreen.TitleVisible)
        {
            TitleScreen.DrawTitleWorld(width, height);
            return;
        }
        if (Player.CharacterEditor.EditorOpen)
        {
            CharacterEditor.Bar.DrawEditorScene(width, height);
            return;
        }

        if (Gpu.Characters is { StageRequest: not null } staged) staged.StageRequest = null;
        CharacterEditor.ClearEditorStage();

        FeedFrameClocks();
        UpdateFrameEffects(width / (float)Math.Max(height, 1));
        DrawFrame(width, height);
    }

    /// <summary>
    /// The stage and the portraits finished as the world is (a change of the options redraws the portraits).
    /// </summary>
    void SyncPreviewLook()
    {
        if (Gpu.Characters is not { } previews) return;

        previews.Look = PreviewLook.Of(WorldOptions.Post);
        Player.PortraitBar.PortraitSize = CharacterRenderer.PortraitSizeFor(previews.Look);
        ItemIconFiles.SyncIconLook();
    }

    /// <summary>
    /// Hands the renderers and the sim's sight this frame's times: the foliage sway, the heat haze, the interior lamps and the weather's time bases.
    /// </summary>
    void FeedFrameClocks()
    {
        double seconds = AnimClock ? AnimSeconds : RealTime.Elapsed.TotalSeconds;
        if (Gpu.Foliage is { } foliage && WorldOptions.Screenshot is null) foliage.SwaySeconds = seconds;

        // The heat haze's gameTime: game hours since the start (it stops while paused, as in the game).
        Gpu.GameHours = Session.Clock.HoursSinceStart;

        // The sim's sight counts the interior lamps the renderer shows (the interiorlamps switch).
        Session.World.Sight.InteriorLampsAllDay = Gpu.Lamps?.InteriorAllDay ?? WorldOptions.InteriorLamps;

        // The weather's three time bases: the game day and time of day (the hour DrawFrame passes), the game speed (the last non-zero one while paused) and the
        // paused flag.
        if (Gpu.Weather is { } weather)
        {
            var sim = Session.Simulation;
            weather.Day = (int)Session.Clock.Day;
            weather.Paused = sim.IsPaused || SkyArrival is not null;
            weather.GameSpeed = (float)(sim.Speed > 0 ? sim.Speed : sim.LastNonZeroSpeed);
        }
    }

    /// <summary>
    /// The per-frame effects before the draw, each lapped on the profiler: blood marks, the mods' hits, birds, then the weather source and the interior view.
    /// </summary>
    void UpdateFrameEffects(float aspect)
    {
        // The profiler: the host's frame so far, then the blood marks apart from the terrain's update.
        Rendering.Profiling.StageClock.Lap(Rendering.Profiling.StageClock.Host);
        BloodDecals.UpdateBlood();
        Modding.UpdateModHits();
        Rendering.Profiling.StageClock.Lap(Rendering.Profiling.StageClock.Blood, "blood marks");

        Birds.UpdateBirds(aspect);
        Rendering.Profiling.StageClock.Lap(Rendering.Profiling.StageClock.Birds, "birds");

        WeatherFeed.UpdateWeatherSource();
        Interiors.UpdateInteriorView();
    }

    void DrawFrame(int width, int height)
    {
        // The frame's animation time (particles, water, wet surfaces) is game time: still while paused, faster at higher speeds. The clock moves in whole
        // 1/30 s ticks, so it runs on by the alpha the characters are drawn at; at 1x the weather particles otherwise moved at 30 Hz.
        double shownSeconds = Session.Clock.GameSeconds + Session.SimulationAlpha * Session.Simulation.TickSeconds;
        float gameTime = (float)(shownSeconds / 600 % 1e4);
        float hour = (float)Session.Clock.HourOfDay;

        // The next world tick runs on its own thread while the world is drawn from the shown snapshots (docs/simulation.md "Tick ahead"): from here to the
        // join nothing the renderer calls back may touch the world, only the session's snapshots.
        if (TickAhead) Session.BeginTickAhead();
        try
        {
            WorldFrame.Draw(Gpu, Scene, Camera, Render, width, height, PresentationHour ?? hour, gameTime, WorldOptions.FogDistance, weatherHour: hour);
        }
        finally
        {
            Session.EndTickAhead();
        }

        if (TickAhead) NoteAheadTick();
    }

    #endregion

    #region Tick ahead

    /// <summary>
    /// This frame's world draw overlaps the next world tick (set by the interactive loop; never in stills, the duel, recorded flights or the play benchmark).
    /// </summary>
    internal bool TickAhead;

    /// <summary>
    /// Meitou's dynamic tick ahead (engine choice): on once a world tick takes <see cref="AheadOnMs"/> (smoothed), off again under <see cref="AheadOffMs"/>,
    /// so a quiet world keeps the game's input delay and a big fight or crowd overlaps; a tick run ahead is slower (it shares the cores with the draw), hence
    /// the gap.
    /// </summary>
    const double AheadOnMs = 4, AheadOffMs = 2;
    bool _worldBusy;

    internal bool TickAheadWanted() => Config.TickAhead switch
    {
        TickAheadMode.Always => true,
        TickAheadMode.Dynamic => _worldBusy = Session.TickMs >= (_worldBusy ? AheadOffMs : AheadOnMs),
        _ => false,
    };

    #endregion
}
