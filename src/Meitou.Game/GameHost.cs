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
/// The game loop: a real-time control tick (<see cref="FixedStepClock"/>, 30 Hz by default) runs the input actions, the camera rig
/// and pause/speed; a game-time simulation tick (<see cref="SimulationClock"/>) advances the game clock; each displayed frame draws the camera interpolated between the last two ticks at the display rate.
/// </summary>
sealed partial class GameHost(GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o, GameOptions g, UserConfig config, Meitou.Data.Gameplay.NewGameStart? start)
{
    // Primary-constructor parameters the feature classes read.
    internal readonly GameInstall install = install;
    internal WorldScene scene = scene;
    internal readonly AssetLocator assets = assets;
    internal readonly WorldOptions o = o;
    internal readonly GameOptions g = g;
    internal readonly UserConfig config = config;

    // The features, each a class of its own taking the host (made first in Run).
    internal GameLoop gameLoop = null!;
    internal GameSound sound = null!;
    internal TitleScreen titleScreen = null!;
    internal NewGameLaunch newGame = null!;
    internal GameSaves saves = null!;
    internal GameMods modding = null!;
    internal BattleScenario battle = null!;
    internal DuelScenario duel = null!;
    internal SandboxScenario sandbox = null!;

    internal BuildingSync buildingSync = null!;
    internal FarmCrops farmCrops = null!;
    internal TownChanges townChanges = null!;
    internal GroundItemSync groundItemSync = null!;
    internal InteriorView interiors = null!;
    internal WeatherFeed weatherFeed = null!;
    internal ItemIconFiles itemIconFiles = null!;

    internal BloodDecals bloodDecals = null!;
    internal Debris debris = null!;
    internal Decapitation decapitation = null!;
    internal CorpseFlies corpseFlies = null!;

    internal CharacterEditorHost characterEditor = null!;
    internal LevelEditorHost levelEditorHost = null!;
    internal InteriorEditorHost interiorEditorHost = null!;

    internal DebugMenu debugMenu = null!;
    internal DebugOverlays debugOverlays = null!;
    internal DevConsoleHost devConsoleHost = null!;
    internal AgentChannel agentChannel = null!;
    internal AgentPlay agentPlay = null!;
    internal AgentWorld agentWorld = null!;
    internal PlayBench playBench = null!;
    internal ScreenshotScenes screenshotScenes = null!;
    internal FeedbackCapture feedbackCapture = null!;

    /// <summary>The save the run starts in (<c>--load</c>), loaded at the end of <see cref="Boot"/> through the menu's load path; null for a new world.</summary>
    public Meitou.Data.Save.SaveGame? StartSave { get; init; }
    /// <summary>Reads the world in the interactive run, on a worker behind the loading screen (the host owns the scene then); null: the scene was given.</summary>
    public Func<WorldScene?>? LoadScene { get; init; }
    internal WorldScene? ownedScene;
    /// <summary>The native splash shown since the program started; closed when the game window first shows.</summary>
    public IDisposable? Splash { get; init; }
    internal WorldSession session = null!;   // disposed with the host (Run)
    internal Meitou.Simulation.Population.PopulationSystem? population;
    /// <summary>The fights started by faction hostility (printed to the log); null in the sandbox and without the population.</summary>
    // The game data the world was built from, kept to build the next world of a quickload (GameSaves).
    internal Meitou.Simulation.Population.PopulationData? popData;
    internal IReadOnlyList<Meitou.Data.World.TownPlacement> townPlacements = [];
    // The level data, for the zone files and town handles of a save (null in the sandbox and the duel).
    internal Meitou.Data.World.WorldLevelData? saveLevels;
    internal Meitou.Simulation.IWalkability? simWalkability;
    internal Meitou.Data.Characters.CharacterRigs? characterRigs;
    internal Meitou.Simulation.Navigation.RagdollGeometry? ragdollGeometry;
    internal Meitou.Simulation.Navigation.NavSystem? nav;
    /// <summary>Zones whose mesh went live since the last <see cref="Meitou.Simulation.RepathCommand"/> (filled on the nav thread, under its own lock).</summary>
    readonly HashSet<Meitou.Data.World.ZoneCoordinate> navReady = [];
    long lastRepath;
    /// <summary>Seconds between <see cref="Meitou.Simulation.RepathCommand"/>s at least: zones go live in runs along a long walk, and are sent together (engine choice).</summary>
    const double RepathCoalesceSeconds = 1;
    /// <summary>The far cadence (docs/simulation.md "Far cadence"): a character far from the camera and from every player character steps once in this many ticks.</summary>
    const int FarTickPeriod = 4;
    internal readonly Dictionary<Meitou.Data.World.ZoneCoordinate, long> navZones = [];
    const double NavCacheSeconds = 120;
    const int NavCacheZones = 60;
    internal PlayerInterface player = null!;
    internal Meitou.Simulation.Factions.Squad? playerSquad;
    internal WorldFrame.Gpu gpu = null!;
    /// <summary>This run uses the native Metal loop: the shared world object exists, but its Vulkan post/loading path does not drive it.</summary>
    internal bool metalRenderer = false;   // set by the Metal loop (GameHost.Metal.cs, macOS builds only)
    // Fallback services for a host whose renderers are not in WorldFrame.Gpu yet (the shared title, audio and save code asks through these).
    WorldWeather? fallbackWeather = null;
    WorldObjectRenderer? hostObjects = null;
    FoliageRenderer? hostFoliage = null;
    CharacterRenderer? hostCharacters = null;
    WorldLamps? hostLamps = null;
    Func<float, float, float>? hostGround = null;
    internal WorldWeather? ActiveWeather => gpu is null ? fallbackWeather : gpu.Weather ?? fallbackWeather;
    internal WorldObjectRenderer? ActiveObjects => gpu is null ? hostObjects : gpu.Objects ?? hostObjects;
    internal FoliageRenderer? ActiveFoliage => gpu is null ? hostFoliage : gpu.Foliage ?? hostFoliage;
    internal CharacterRenderer? ActiveCharacters => gpu is null ? hostCharacters : gpu.Characters ?? hostCharacters;
    internal WorldLamps? ActiveLamps => gpu is null ? hostLamps : gpu.Lamps ?? hostLamps;
    internal float ActiveGround(float x, float z) => hostGround?.Invoke(x, z) ?? (gpu is null ? 0 : gpu.Terrain.HeightAt(x, z));
    internal WorldCamera camera = null!;
    internal WorldRenderOptions render = null!;
    internal readonly Stopwatch realTime = Stopwatch.StartNew();
    internal Meitou.Game.Rendering.Upscalers.Streamline? streamline;
    internal Meitou.Game.Rendering.Upscalers.Ngx? ngx;
    internal bool screenshotRequested;
    /// <summary>Frames in a row that could not be drawn while a control screenshot waits; at 30 the screenshot is answered with an error.</summary>
    internal int shotNoFrame;
    // The interactive run's animation clock (foliage sway, clouds, water): the frame steps, which are exact while the cinema records; off, the real time.
    internal double animSeconds;
    internal bool animClock;
    // Draw-list state (GameHost.Characters.cs): the previous snapshot indexed by id, and the last camera focus sent to the world.
    internal WorldSnapshot? indexedFor;
    internal readonly Dictionary<CharacterId, CharacterSnapshot> previousById = [];
    internal Meitou.Data.Gameplay.AnimationLengths? animationLengths;
    internal Meitou.Data.Gameplay.AnimationLibrary? animationLibrary;
    Vector3 lastFocus;
    bool sentFocus;
    // Profile of the interactive run (printed by --quit-after).
    internal double advanceTotal, fillTotal;
    internal long fillCalls;
    // --quit-after keeps the exposed menu's frame pacing separate from covered loading work.
    internal double menuFrameSeconds, menuLongestFrame;
    internal long menuFrames, menuSlowFrames;

    public int Run()
    {
        CreateFeatures();
        try
        {
#if MEITOU_METAL
            if (g.Renderer == "metal") return Metal();
#endif
            if (g.Renderer != "vulkan")
            {
                Console.Error.WriteLine($"the {g.Renderer} renderer is macOS only.");
                return 2;
            }
            return o.Screenshot is not null ? screenshotScenes.Screenshot() : gameLoop.Run();
        }
        finally { sound.DisposeAudio(); titleScreen.DisposeTitleScene(); session?.Dispose(); nav?.Dispose(); ragdollGeometry?.Dispose(); ownedScene?.Dispose(); }
    }

    /// <summary>Makes the feature classes; first in <see cref="Run"/>, before anything reads them.</summary>
    void CreateFeatures()
    {
        gameLoop = new(this);
        sound = new(this);
        titleScreen = new(this);
        newGame = new(this);
        saves = new(this);
        modding = new(this);
        battle = new(this);
        duel = new(this);
        sandbox = new(this);

        buildingSync = new(this);
        farmCrops = new(this);
        townChanges = new(this);
        groundItemSync = new(this);
        interiors = new(this);
        weatherFeed = new(this);
        itemIconFiles = new(this);

        bloodDecals = new(this);
        debris = new(this);
        decapitation = new(this);
        corpseFlies = new(this);

        characterEditor = new(this);
        levelEditorHost = new(this);
        interiorEditorHost = new(this);

        debugMenu = new(this);
        debugOverlays = new(this);
        devConsoleHost = new(this);
        agentChannel = new(this);
        agentPlay = new(this);
        agentWorld = new(this);
        playBench = new(this);
        screenshotScenes = new(this);
        feedbackCapture = new(this);
    }

    /// <summary>The start opens on the title (no start, save, sandbox or duel was asked for).</summary>
    internal bool OpensOnTitle(bool interactive) => g.TitlePreview || interactive && start is null && StartSave is null && !sandbox.IsSandbox && !duel.IsDuel && !battle.IsBattle && !g.NoPopulation;

    internal void Boot(VulkanDisplay display, bool interactive)
    {
        var context = display.Context;
        o.Day ??= (int)GameClock.DefaultStartDay;   // the weather schedule starts on the clock's first day (--day moves both)
        // --crowd in the game: simulated characters (SpawnCrowd), not the viewer's posed harness, which would take the renderer's place.
        sandbox.crowd = o.Crowd;
        o.Crowd = 0;
        // The simulation's data (the navmesh system, the towns' homes, the population's data and systems) is read on a worker while the renderer is made here,
        // a step at a time with a loading frame between the steps (docs/engine.md "Loading").
        var simulation = Task.Run(() =>
        {
            var grid = new Meitou.Data.World.GroundHeights(scene.Window, scene.Coarse, scene.CoarseSize, WorldFrame.CoarseStep);
            return (Grid: grid, Parts: BootSimulation(grid, interactive));
        });
        gpu = WorldFrame.CreateGpu(context, install, scene, assets, o, interactive, LoadingStep, AwaitLoading);
        if (!o.NoParticles) titleScreen.titleParticleFactory = () => ParticleRenderer.Create(context, assets, gpu.Particles?.Library);
        if (gpu.Post is { } vendorPost) vendorPost.UpscalerFactory = Meitou.Game.Rendering.Upscalers.VendorUpscalers.Factory(display.Context, streamline, ngx);
        (camera, render) = WorldFrame.Setup(scene, o);
        render.Grid = battle.IsFlat;
        LoadingStep("people");
        var (heights, (walkability, systems, constants)) = AwaitLoading(simulation);
        LoadingStep("world");
        var clock = GameClockFor(constants ??= scene.Database is { } clockDb ? Meitou.Data.Gameplay.GameConstants.FromDatabase(clockDb) : null, o.Hour, o.Day ?? GameClock.DefaultStartDay);
        session = new WorldSession(scene.Focus, (scene.X0, scene.Z0, scene.X1, scene.Z1), gpu.Terrain.HeightAt, o.Hour, config.TickRate,
            clock: clock,
            simulation: duel.worldSettings = new Meitou.Simulation.WorldSettings
            {
                Seed = g.Seed, MinPartitionSize = 32, Threads = Math.Max(1, config.SimThreads ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 8)),
                Sunrise = (float)clock.Sunrise, Sunset = (float)clock.Sunset, FarTickPeriod = FarTickPeriod,
            },
            systems: systems, walkability: walkability);
        session.World.BenchFacingFix = config.FixBenchFacing;
        session.Simulation.SetFastSpeeds(config.FastSpeed, config.FastestSpeed);
        if (gpu.Characters is { } nearChars) session.World.NearRadius = nearChars.NearReach;   // every drawn character steps every tick (docs/simulation.md "Far cadence")
        BootPlayer(display, interactive);
    }

    /// <summary>
    /// The simulation's side of <see cref="Boot"/>, on a worker (it reads the game data and the scene's level data and makes no GPU resource): the world's content
    /// (<see cref="WorldBoot"/>), the code mods and the systems. Returns the walkability, the systems and the game constants for the session.
    /// </summary>
    internal (Meitou.Simulation.IWalkability Walkability, IReadOnlyList<Meitou.Simulation.ITickSystem> Systems, Meitou.Data.Gameplay.GameConstants? Constants) BootSimulation(
        Meitou.Data.World.GroundHeights heights, bool interactive)
    {
        // The simulation samples the CPU heightmap (immutable, any thread), not the renderer's terrain.
        Meitou.Simulation.IWalkability walkability = new Meitou.Simulation.OpenGroundWalkability(heights.HeightAt);
        IReadOnlyList<Meitou.Simulation.ITickSystem> systems = [];
        Meitou.Data.Gameplay.GameConstants? constants = null;   // parsed once: the body factory's, else for the clock alone
        if (!g.NoPopulation && scene.Database is { } gameDb)
        {
            // The sandbox, the duel and the battle walk on open ground (the heightmap): the sandbox has no world, and the duel's spot may be a navmesh island the build prunes.
            // None has towns: their own characters are the only ones.
            var levels = sandbox.IsSandbox || duel.IsDuel || battle.IsBattle ? null : scene.Objects?.Levels ?? Meitou.Simulation.WorldBoot.LoadLevels(install, gameDb);
            var boot = Meitou.Simulation.WorldBoot.Load(BootOptions(gameDb, heights, levels));
            nav = boot.Nav;
            ragdollGeometry = boot.RagdollGeometry;
            characterRigs = boot.Rigs;
            animationLengths = boot.AnimationLengths;
            animationLibrary = boot.AnimationLibrary;
            popData = boot.Data;
            modding.StartMods(gameDb, boot.Data);
            townPlacements = boot.Towns;
            saveLevels = levels;
            walkability = simWalkability = boot.Walkability;
            var built = saves.BuildSystems(interactive);
            population = built.Population;
            if (nav is not null) population!.ZoneGate = zone => nav.Walkability.World.Contains(zone);
            saves.SnapshotPlayerTowns();
            systems = built.Systems;
            constants = boot.Data.Bodies.Constants;
        }
        modding.StartDeferredWarmup();
        return (walkability, systems, constants);
    }

    /// <summary>The world's content as this scene loads it: its levels and the renderer's layouts, the body settings, and the nav zones reported to the host.</summary>
    Meitou.Simulation.WorldBootOptions BootOptions(Meitou.Data.GameDatabase gameDb, Meitou.Data.World.GroundHeights heights, Meitou.Data.World.WorldLevelData? levels) =>
        new(install, gameDb, heights, assets)
        {
            Levels = levels,
            Layouts = scene.Objects?.Layouts,
            Bodies = Meitou.Simulation.Bodies.BodyOptions.Default with
            {
                Dismemberment = saves.Gameplay.Dismemberment, Decapitation = config.Decapitation, AdvancedRagdolls = config.AdvancedRagdolls,
            },
            ZoneReady = ready => { lock (navReady) navReady.Add(ready.Zone); },
        };

    /// <summary>
    /// The callbacks the interface asks the host for: the game data, the map, the build services, the tooltips and the character editor. Shared by the Vulkan
    /// and Metal loops so the two expose the same interaction layer.
    /// </summary>
    void ConfigurePlayerInterface(PlayerInterface ui)
    {
        modding.ConnectModUi(ui);
        ui.frontend.FixLoadButton = config.FixLoadButton;
        ui.frontend.SaveInUse = folder => session.World.Player.Exists && saves.saveInUse is { } used && string.Equals(Path.GetFullPath(used), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);
        ui.frontend.DeleteSave = folder =>
        {
            try { Directory.Delete(folder, recursive: true); return null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "Failed to delete save: " + e.Message; }
        };
        // The region (BIOME_GROUP) under a point, for the biome banner.
        ui.biomeBanner.RegionAt = (x, z) => ActiveWeather?.World.RegionAt(x, z).Def is { } def ? (def.StringId, def.Name) : null;
        ui.build.BuildHost = buildingSync.MakeBuildServices();
        ui.FactionName = faction => faction >= 0 && faction == session.World.Player.Faction && session.World.Player.FactionName is { } renamed ? renamed : popData is { } d && faction >= 0 && faction < d.Factions.Count ? d.Factions[faction].Name : null;
        // The rename's test (FUN_1402e7950, as RenameFactionCommand's): another faction of the data has the name.
        ui.factionWindow.FactionNameTaken = name => popData is { } fd && fd.Factions.Where((f, i) => i != session.World.Player.Faction).Any(f => f.Name == name);
        // The faction tab's list (docs/game/ui-screens.md "Faction tab"): every faction the player has met but the player's, the `not real` ones and the
        // generation template, with the player's relation towards it.
        // Its info panel's contracts and events, read from the live world as the list above is (a collection changed mid-read shows the panel empty for that frame).
        ui.factionWindow.FactionInfo = () =>
        {
            try { return (session.World.ActiveContracts(ui.FactionName), population?.UpcomingEvents(session.World, ui.FactionName) ?? []); }
            catch (InvalidOperationException) { return ([], []); }
        };
        ui.factionWindow.FactionRelations = () => popData is { } d && session.World.Player.Faction is >= 0 and var own && d.Relations.Contains(own) && session.CurrentSnapshot.KnownFactions is var known
            ? [.. d.Relations.Factions.Select((f, i) => (f, i)).Where(p => p.i != own && known.Contains(p.i) && !p.f.NotReal && p.f.Id != "GENERATION_TEMPLATE_FACTION").Select(p => (p.f.Name, d.Relations.Get(own, p.i)))]
            : [];
        // A BUILDING's construction target (its Condition rows): the sum of its materials' required amounts.
        ui.buildingSelection.BuildTarget = record => popData?.Db.Find(record) is { Type: Meitou.Data.Fcs.FcsRecordType.BUILDING } r
            ? Meitou.Data.Gameplay.Construction.BuildingBuildData.From(r).Materials.Sum(m => m.Required) : null;
        // The upgrade chain of a BUILDING (the panel's [UPGRADE] / [DOWNGRADE]) and its build data (their tooltips' materials).
        ui.buildingSelection.UpgradeOf = record => popData is { } d ? Meitou.Simulation.Construction.BuildingUpgrades.UpgradeOf(d.Db, record) : null;
        ui.buildingSelection.DowngradeOf = record => popData is { } d ? Meitou.Simulation.Construction.BuildingUpgrades.DowngradeOf(d.Db, record) : null;
        ui.buildingSelection.BuildingData = record => popData?.Db.Find(record) is { Type: Meitou.Data.Fcs.FcsRecordType.BUILDING } r ? Meitou.Data.Gameplay.Construction.BuildingBuildData.From(r) : null;
        ui.buildingSelection.PowerOutput = record => popData?.Db.Find(record)?.GetInt("power output");
        ui.squadWindows.SquadLimits = () => popData?.Bodies.Constants;
        ui.furniture.Rocks = () => popData?.ResourceNodes;
        ui.worldMap.MapTowns = () => popData?.Sites ?? [];
        ui.worldMap.MapFactionOf = id =>
        {
            if (popData is not { } d || id is null || d.FactionIndex(id) is not (>= 0 and var town)) return null;
            var faction = d.Factions[town];
            if (session.World.Player.Faction is not (>= 0 and var mine))
                return new MapFaction(faction.Name, 0, MapStanding.Neutral, faction.NotReal);
            float relation = d.Relations.Get(mine, town);
            var standing = town == mine ? MapStanding.Player : d.Relations.IsAlly(mine, town) ? MapStanding.Ally : relation < -30 ? MapStanding.Enemy : MapStanding.Neutral;
            return new MapFaction(faction.Name, relation, standing, faction.NotReal);
        };
        ui.worldMap.MapFactionId = faction => popData is { } d && faction >= 0 && faction < d.Factions.Count ? d.Factions[faction].Id : null;
        ui.ArmourOf = record => popData?.Combat.Armour(record);
        ui.RecordName = id => popData?.Db.Find(id)?.Name;
        ui.PlayerFactionName = () => ui.FactionName?.Invoke(session.World.Player.Faction) ?? "";
        Meitou.Data.Gameplay.Economy.PriceConstants? priceConstants = null;
        Meitou.Simulation.Trading.Market? shelf = null;
        ui.PricesOf = tip =>
        {
            if (popData is not { } d || d.Db.Find(tip.Record) is not { } r) return null;
            if (r.Type is Meitou.Data.Fcs.FcsRecordType.ARMOUR or Meitou.Data.Fcs.FcsRecordType.WEAPON or Meitou.Data.Fcs.FcsRecordType.CROSSBOW)
                return Meitou.Simulation.Trading.Market.GearPrices(d.Db, priceConstants ??= Meitou.Data.Gameplay.Economy.PriceConstants.FromDatabase(d.Db), tip.Record, tip.Level, tip.Company) is { } gear
                    ? (gear.Value, gear.Sell, null) : null;
            // No trader, so no town or culture factor (the seed only rolls those). StolenFrom is set only when the item is stolen for its holder; holder -1 keeps that.
            var m = shelf ??= new Meitou.Simulation.Trading.Market(d, 0);
            var item = new Meitou.Simulation.Items.ItemInstance
            {
                Record = tip.Record, Type = d.Db.Find(tip.Company) is { Type: Meitou.Data.Fcs.FcsRecordType.RESEARCH } ? Meitou.Data.Fcs.FcsRecordType.BLUEPRINT : r.Type, Quality = tip.Quality, Level = tip.Level, CompanyId = tip.Company, Charges = tip.Charges, MaxCharges = tip.MaxCharges,
                Quantity = tip.Quantity, StolenFrom = tip.StolenFrom,
            };
            var none = Meitou.Simulation.Trading.Trader.None;
            return (m.UnitPrice(item, false, none, -1), m.UnitPrice(item, true, none, -1), m.AveragePrice(item));
        };
        ui.groundItemPicks.ArtifactOf = id => popData?.Db.Find(id)?.GetBool("artifact") == true;
        ui.tooltipRuntime.ItemRecordOf = id => popData?.Db.Find(id);
        var specialFoodRaces = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        ui.tooltipRuntime.SpecialFoodRaces = id => popData is { } d ? specialFoodRaces.GetOrAdd(id, item => Meitou.Data.Gameplay.Bodies.SpecialFood.EatenBy(d.Db, item)) : "";
        ui.tooltipRuntime.CombatOf = () => popData?.Combat;
        ui.tooltipRuntime.RangedDamageMultiplier = () => popData?.BodyOptions.GlobalDamageMultiplier ?? 1;
        ui.worldMap.MapItemTowns = record =>
        {
            if (popData is not { } d || d.Db.Find(record) is not { Type: Meitou.Data.Fcs.FcsRecordType.MAP_ITEM } map) return null;
            return [.. Meitou.Simulation.Items.MapItems.Reveals(d, map, session.CurrentSnapshot.Towns, session.World.Seed).Select(i => d.Sites[i].Town.Name)];
        };
        characterEditor.ConfigureEditor(ui);
    }

    /// <summary>The rest of <see cref="Boot"/> once the session exists: bindings, the player's interface, the start (a new game's squad, the sandbox, the duel), the camera, a <c>--load</c> save.</summary>
    void BootPlayer(VulkanDisplay display, bool interactive)
    {
        var context = display.Context;
        var target = camera.Target;
        foreach (var problem in session.Bindings.Apply(config.Bindings)) Console.Error.WriteLine($"config    binding skipped: {problem}");
        // Right is the command button by default; older saved configs bound it to orbit (kept when Command has moved off it).
        if (session.Bindings.Get(InputAction.Command).Contains(Binding.Of(EngineButton.Right)))
            session.Bindings.Set(InputAction.Orbit, [.. session.Bindings.Get(InputAction.Orbit).Where(b => !(b.IsMouse && b.Button == EngineButton.Right))]);
        // Left Control orbits as the middle button does (mouse_rotate, FUN_1403636c0: only code 0x1d); older saved configs had the middle button alone.
        if (session.Bindings.Get(InputAction.Orbit).SequenceEqual([Binding.Of(EngineButton.Middle)])) session.Bindings.Set(InputAction.Orbit, EngineButton.Middle, EngineKey.LeftControl);
        // F12 is the profiler now (as in the viewer); older saved configs took it for the screenshot.
        if (session.Bindings.Get(InputAction.Screenshot).SequenceEqual([Binding.Of(EngineKey.F12)])) session.Bindings.Set(InputAction.Screenshot, EngineKey.F8, EngineKey.PrintScreen);
        // The profiler and debug menu were briefly on F10 and Insert; configs saved then go back to F12 and F10.
        if (session.Bindings.Get(InputAction.CycleProfiler).SequenceEqual([Binding.Of(EngineKey.F10)])
            && session.Bindings.Get(InputAction.ToggleDebugMenu).SequenceEqual([Binding.Of(EngineKey.Insert)]))
        { session.Bindings.Set(InputAction.CycleProfiler, EngineKey.F12); session.Bindings.Set(InputAction.ToggleDebugMenu, EngineKey.F10); }
        // F9 is the game's quickload now (F5 quicksave); older saved configs took F9 for the fps counter, which is on Ctrl+F9 and unbound here.
        if (session.Bindings.Get(InputAction.ToggleFps).SequenceEqual([Binding.Of(EngineKey.F9)])) session.Bindings.Set(InputAction.ToggleFps);
        // Tab is the game's change_squad, not the settings (they open from the Esc menu); older saved configs bound Tab to them.
        if (session.Bindings.Get(InputAction.ToggleSettings).SequenceEqual([Binding.Of(EngineKey.Tab)])) session.Bindings.Set(InputAction.ToggleSettings);
        LoadingStep("interface");
        // Picks against the drawn terrain (the streamed fine window): the boot window's heights fall back to a 144-unit grid away from the start.
        player = new PlayerInterface(session, gpu.Terrain.HeightAt, DrawnPosition);
        player.LoadUi(g.UiMode ?? config.UiMode, assets.Find);
        player.SpeedUnit = config.SpeedUnit;
        player.ShowNames = config.ShowNames;
        player.Floaters = config.Floaters;
        player.FeedbackButtonInPlay = config.FeedbackButton;
        player.frontend.FixLoadButton = config.FixLoadButton;
        player.buildingSelection.DeselectBuildings = config.DeselectBuildings;
        // The region (BIOME_GROUP) under a point, for the biome banner.
        player.biomeBanner.RegionAt = (x, z) => gpu.Weather?.World.RegionAt(x, z).Def is { } def ? (def.StringId, def.Name) : null;
        player.Markers = gpu.Markers ??= MarkerRenderer.Create(context, assets.Find);
        bloodDecals.SetUpBlood(context);
        gpu.HoverOutline ??= new HoverOutlineRenderer(context);
        gpu.NavmeshOverlay ??= new NavmeshOverlayRenderer(context);
        debugOverlays.navOverlayRenderer = gpu.NavmeshOverlay;
        gpu.InspectorOverlay ??= new NavmeshOverlayRenderer(context);
        player.picking.MeshHit = (id, origin, direction) => gpu.Characters?.RayHit(DrawKey(id), origin, direction);
        player.Floor = (() => interiors.viewedFloor, interiors.StepFloor);
        player.stealthView.HiddenInWorld = interiors.hiddenIndoors.Contains;
        player.stealthView.DrawnHead = id => gpu.Characters?.BonePosition(DrawKey(id), "Bip01 Head");
        player.furniture.AboveViewedFloor = interiors.AboveViewedFloor;
        player.groundItemPicks.GroundItemShape = groundItemSync.GroundShape;
        player.groundItemPicks.GroundItemHidden = groundItemSync.hiddenGround.Contains;
        player.picking.BuildingMoveHit = (origin, direction, distance) => gpu.Objects?.MoveHit(origin, direction, interiors.viewedFloor, distance);
        player.picking.SurfaceMoveHit = interiors.SurfaceMoveHit;
        player.picking.ClosedBuildingHit = (origin, direction, distance) => gpu.Objects?.ClosedBuildingHit(origin, direction, distance);
        player.picking.ShownInteriorHit = (origin, direction, distance) => gpu.Objects?.ShownInteriorHit(origin, direction, distance);
        player.furniture.DoorShape = (record, door) => nav?.Walkability.DoorGeometry?.Shape(record, door) ?? [];
        ConfigurePlayerInterface(player);
        player.levelEditorPanel.LevelHost = levelEditorHost.MakeLevelEditorServices();
        if (sandbox.IsSandbox) player.ReservedKeys.UnionWith([Meitou.Game.Host.Input.Key.Z, Meitou.Game.Host.Input.Key.X, Meitou.Game.Host.Input.Key.C, Meitou.Game.Host.Input.Key.Y, Meitou.Game.Host.Input.Key.P, Meitou.Game.Host.Input.Key.G]);
        player.frontend.ConfigureFrontend(scene.Database,
            () => Meitou.Data.Save.SaveCatalog.Scan(Meitou.Simulation.Saving.QuickSave.DefaultRoot, install.SaveDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kenshi", "save")),
            assets.Find("credits.txt") is { } creditsFile ? File.ReadAllText(creditsFile) : "");
        player.titleMenu.DataProblems = (scene.Database?.Issues.Count ?? 0) + (scene.Objects?.Levels.Problems().Count ?? 0);
        if (scene.Database is { } db)
        {
            player.titleMenu.Mods = ModReport.Build(install, db, modding.mods?.Refused);
            LogMods(player.titleMenu.Mods);
        }
        if (OpensOnTitle(interactive)) player.frontend.ShowTitle();
        if (nav is not null && (start is not null || !interactive))
        {
            // The squad is placed on the mesh, and a still has its town at once: wait for the zones round the start (a cold build is about 2 s, then cached).
            var watch = Stopwatch.StartNew();
            var zones = Meitou.Simulation.Population.ZoneActivation.ZonesAround(scene.Focus, 0);
            LoadingStep("navmesh");
            AwaitLoading(Task.WhenAll(zones.Select(z => (Task)nav.LoadZone(z))));
            foreach (var z in zones) navZones[z] = Stopwatch.GetTimestamp();
            Console.WriteLine($"navmesh   {zones.Count} zone(s) round the start ready in {watch.ElapsedMilliseconds} ms");
        }
        if (start is not null && population is not null)
        {
            playerSquad = population.StartPlayer(session.World, start);
            var lead = session.World.Characters.Previous[playerSquad.Leader.Slot].Position;
            target = lead;
            Console.WriteLine($"player    {playerSquad.Members.Count} characters, leader at {lead.X:0}, {lead.Z:0}");
            // --new-game is a shortcut past the menus: the start's characters stay as rolled (the menu's New game opens the character editor); a still keeps the creation
            // open for the showcase's editor pictures.
            if (interactive) session.World.Commands.Enqueue(new Meitou.Simulation.Editing.FinishCreation { Tick = session.World.Tick });
        }
        if (sandbox.IsSandbox && population is not null) target = sandbox.SandboxSpawn();
        if (duel.IsDuel && population is not null) target = duel.DuelStart();
        if (battle.IsBattle && population is not null) target = battle.BattleStart();
        if (!sandbox.IsSandbox && sandbox.crowd > 0 && population is not null) sandbox.SpawnCrowd(target, sandbox.crowd);
        LoadingStep("characters");
        if (scene.Database is { } characterDb && !g.NoPopulation && gpu.Characters is null)
            gpu.Characters = new CharacterRenderer(context, install, characterDb, assets, characterRigs) { DrawDistance = GameplaySettings.NpcRange(install), Source = FillDrawList, Guard = gpu.Guard, GeneratedLod = o.CharacterLod, SkinShading = o.SkinShading, SkinRaces = config.SkinRaces, EnhancedIcons = o.MeitouIcons,
                PoseClock = () => animClock ? animSeconds : realTime.Elapsed.TotalSeconds };   // the frame clock DrawWorld gives the foliage sway: a recorded flight poses the same every take
        var rig = session.Camera;
        rig.Place(new Vector2(target.X, target.Z), (o.Yaw ?? 30) * MathF.PI / 180,
            (o.Pitch ?? Meitou.Data.World.KenshiCamera.InitialPitchDegrees) * MathF.PI / 180, o.Distance ?? Meitou.Data.World.KenshiCamera.InitialDistance);
        SendFocus();
        ApplyCamera(session.Camera.Current);
        if (titleScreen.TitleVisible) titleScreen.ApplyTitleCamera();
        if (StartSave is { } save && g.Load is { } folder)
        {
            saves.readingFolder = folder;
            Task navmesh;
            string summary;
            try { navmesh = saves.SwapInSave(Task.FromResult(save), interactive, out summary); }
            catch (SceneLoadFailed e) { throw new InvalidDataException($"--load {folder}: {e.Message}"); }
            LoadingStep("navmesh");
            AwaitLoading(navmesh);
            SettleNewWorld();
            saves.FinishLoadNotice(summary);
        }
        if (interactive)
        {
            LoadingStep("finishing");
            WorldFrame.FinishLoading(context);
            ReportLoading("start");
        }
    }

    /// <summary>Logs the mod list the game data was loaded from: enabled (the names the list file gives), loaded (those whose .mod file was found), problems.</summary>
    void LogMods(ModReport report)
    {
        string file = Meitou.Content.ModList.ModsCfgOverride ?? "data/mods.cfg";
        string problems = report.Problems == 1 ? "problem" : "problems";
        Console.WriteLine($"mods      {report.Enabled} enabled, {report.Loaded} loaded, {report.Problems} {problems}, from {file}");
    }

    /// <summary>The game clock with sunrise, sunset and days per year from the CONSTANTS record (defaults without data).</summary>
    static GameClock GameClockFor(Meitou.Data.Gameplay.GameConstants? c, double startHour, long startDay)
    {
        if (c is null) return new GameClock(startHour, startDay);
        return new GameClock(startHour, startDay, sunrise: c.Sunrise, sunset: c.Sunset, daysPerYear: c.DaysPerYear);
    }

    internal void ApplyCamera(CameraState s)
    {
        camera.Target = s.Target;
        camera.Yaw = s.Yaw;
        camera.Pitch = s.Pitch;
        camera.Distance = s.Distance;
    }

    /// <summary>
    /// Keeps the navmesh on the zones the population has active (built on the nav thread, cached), drops the ones idle for 30 s, and has the characters whose way
    /// leads through a zone whose mesh arrived ask for their paths again (their first path may have crossed a building), at most every <see cref="RepathCoalesceSeconds"/>.
    /// </summary>
    Task navUnload = Task.CompletedTask;
    HashSet<Meitou.Data.World.ZoneCoordinate> navUnloading = [];

    /// <summary>Waits for the zones being dropped (a load right after must not be undone by it).</summary>
    internal void FinishNavUnload()
    {
        if (navUnloading.Count == 0) return;
        navUnload.Wait();
        navUnloading = [];
        relabelGates = true;
    }

    internal void UpdateNav(bool wait = false)
    {
        if (nav is null || population is null) return;
        UpdateGateRegions(nav);
        long now = Stopwatch.GetTimestamp();
        // Dropping zones republishes the whole mesh (tens of ms): it runs on the pool, one batch at a time, and a zone being dropped is loaded again once that is done.
        if (navUnloading.Count > 0 && navUnload.IsCompleted) { navUnloading = []; relabelGates = true; }
        if (wait) FinishNavUnload();
        var pending = new List<Task<Meitou.Simulation.Navigation.NavZoneReady>>();
        // The population's zones, and those the walkers' long routes lead into (NavmeshWalkability.RouteZones).
        foreach (var z in population.ActiveZones.Concat(nav.Walkability.RouteZones(TimeSpan.FromSeconds(30))))
        {
            if (navUnloading.Contains(z)) continue;
            if (!navZones.ContainsKey(z)) pending.Add(nav.LoadZone(z));
            navZones[z] = now;
        }
        // Meitou's asset cache (engine choice): a left zone's mesh stays NavCacheSeconds, at most NavCacheZones zones, the oldest dropped first, so a return within two minutes rebuilds nothing.
        var stale = navZones.Where(e => Stopwatch.GetElapsedTime(e.Value, now).TotalSeconds > NavCacheSeconds).Select(e => e.Key).ToList();
        if (navZones.Count - stale.Count > NavCacheZones)
            stale.AddRange(navZones.Where(e => !stale.Contains(e.Key)).OrderBy(e => e.Value).Take(navZones.Count - stale.Count - NavCacheZones).Select(e => e.Key));
        if (stale.Count > 0 && navUnloading.Count == 0)
        {
            foreach (var z in stale) navZones.Remove(z);
            navUnloading = [.. stale];
            var batch = stale;
            navUnload = Task.Run(() => nav.UnloadZones(batch));
        }
        if (wait) Task.WaitAll([.. pending]);
        HashSet<Meitou.Data.World.ZoneCoordinate> zones;
        lock (navReady)
        {
            // The event follows the task's completion, so zones waited for are added here.
            if (wait) foreach (var task in pending) navReady.Add(task.Result.Zone);
            if (navReady.Count == 0 || !wait && Stopwatch.GetElapsedTime(lastRepath, now).TotalSeconds < RepathCoalesceSeconds) return;
            zones = [.. navReady];
            navReady.Clear();
        }
        lastRepath = now;
        relabelGates = true;
        session.World.Commands.Enqueue(new Meitou.Simulation.RepathCommand { Tick = session.World.Tick, Zones = zones });
    }

    /// <summary>The navmesh changed since the gate codes were made (a zone went live or was dropped).</summary>
    bool relabelGates;
    Task<Meitou.Simulation.Navigation.NavGateRegions>? gateLabelling;
    Meitou.Simulation.Navigation.NavGateRegions? gateRegions;
    object? gateRegionsFor;

    /// <summary>
    /// Keeps the simulation's gate codes (docs/game/pathfinding.md "Gate codes") up to date: labels the loaded meshes on the pool after they changed, one labelling at a time, and
    /// hands each result to the world (again to a new world) as a <see cref="Meitou.Simulation.Locks.GateRegionsCommand"/>.
    /// </summary>
    void UpdateGateRegions(Meitou.Simulation.Navigation.NavSystem navSystem)
    {
        if (gateLabelling is { IsCompleted: true } done)
        {
            if (done.Status == TaskStatus.RanToCompletion) { gateRegions = done.Result; gateRegionsFor = null; }
            else Console.Error.WriteLine($"nav       gate codes failed: {done.Exception?.GetBaseException().Message}");
            gateLabelling = null;
        }
        if (relabelGates && gateLabelling is null)
        {
            relabelGates = false;
            gateLabelling = Task.Run(navSystem.LabelGates);
        }
        if (gateRegions is { } regions && !ReferenceEquals(gateRegionsFor, session.World))
        {
            gateRegionsFor = session.World;
            session.World.Commands.Enqueue(new Meitou.Simulation.Locks.GateRegionsCommand(regions) { Tick = session.World.Tick });
        }
    }

    /// <summary>Gives the player interface the camera it picks with.</summary>
    internal void UpdateView(int width, int height)
    {
        player.ViewProjection = camera.View * camera.Projection(width / (float)Math.Max(height, 1), camera.Near, camera.ViewDistance);
        player.Eye = camera.Eye;
        player.Width = width;
        player.Height = height;
    }

    internal void DrawWorld(int width, int height)
    {
        // The stage and the portraits finished as the world is (a change of the options redraws the portraits).
        if (gpu.Characters is { } previews)
        {
            previews.Look = Meitou.Game.Rendering.Characters.PreviewLook.Of(o.Post);
            player.portraitBar.PortraitSize = Meitou.Game.Rendering.Characters.CharacterRenderer.PortraitSizeFor(previews.Look);
            itemIconFiles.SyncIconLook();
        }
        if (!player.characterEditor.EditorOpen) characterEditor.ClearEditorBar();
        if (titleScreen.TitleVisible) { titleScreen.DrawTitleWorld(width, height); return; }
        if (player.characterEditor.EditorOpen) { characterEditor.DrawEditorScene(width, height); return; }
        if (gpu.Characters is { StageRequest: not null } staged) staged.StageRequest = null;
        characterEditor.ClearEditorStage();
        double seconds = animClock ? animSeconds : realTime.Elapsed.TotalSeconds;
        if (gpu.Foliage is { } foliage && o.Screenshot is null) foliage.SwaySeconds = seconds;
        // The heat haze's gameTime: game hours since the start (it stops while paused, as in the game).
        gpu.GameHours = session.Clock.HoursSinceStart;
        // The sim's sight counts the interior lamps the renderer shows (the interiorlamps switch).
        session.World.Sight.InteriorLampsAllDay = gpu.Lamps?.InteriorAllDay ?? o.InteriorLamps;
        // The weather's three time bases: the game day and time of day (the hour below), the game speed (the last non-zero one while paused) and the paused flag.
        if (gpu.Weather is { } weather)
        {
            var sim = session.Simulation;
            weather.Day = (int)session.Clock.Day;
            weather.Paused = sim.IsPaused || skyArrival is not null;
            weather.GameSpeed = (float)(sim.Speed > 0 ? sim.Speed : sim.LastNonZeroSpeed);
        }
        Meitou.Game.Rendering.Profiling.StageClock.Lap(Meitou.Game.Rendering.Profiling.StageClock.Host);   // the profiler: the host's frame so far, then the blood marks apart from the terrain's update
        bloodDecals.UpdateBlood();
        modding.UpdateModHits();
        Meitou.Game.Rendering.Profiling.StageClock.Lap(Meitou.Game.Rendering.Profiling.StageClock.Blood, "blood marks");
        birdAspect = width / (float)Math.Max(height, 1);
        UpdateBirds();
        Meitou.Game.Rendering.Profiling.StageClock.Lap(Meitou.Game.Rendering.Profiling.StageClock.Birds, "birds");
        weatherFeed.UpdateWeatherSource();
        interiors.UpdateInteriorView();
        // The frame's animation time (particles, water, wet surfaces) is game time: still while paused, faster at higher speeds. The clock moves in whole
        // 1/30 s ticks, so it runs on by the alpha the characters are drawn at; at 1x the weather particles otherwise moved at 30 Hz.
        double shownSeconds = session.Clock.GameSeconds + session.SimulationAlpha * session.Simulation.TickSeconds;
        float gameTime = (float)(shownSeconds / 600 % 1e4);
        // The next world tick runs on its own thread while the world is drawn from the shown snapshots (docs/simulation.md "Tick ahead"): from here to the
        // join nothing the renderer calls back may touch the world, only the session's snapshots.
        if (tickAhead) session.BeginTickAhead();
        try
        {
            WorldFrame.Draw(gpu, scene, camera, render, width, height, presentationHour ?? (float)session.Clock.HourOfDay, gameTime, o.FogDistance,
                weatherHour: (float)session.Clock.HourOfDay);
        }
        finally { session.EndTickAhead(); }
        if (tickAhead) NoteAheadTick();
    }

    Meitou.Game.Effects.BirdFlocks? birds;
    Meitou.Data.Gameplay.BiomeMap? birdAreas;
    double birdClock = double.NaN;

    /// <summary>The birds fly on the display's real time (the original's <c>BirdManager</c> thread is kicked with the real dt, docs/game/birds.md); the flocks are those the camera's BIOME_GROUP lists.</summary>
    void UpdateBirds()
    {
        if (scene.Database is not { } db) { gpu.BirdPieces.Clear(); return; }
        if (birds is null)
        {
            var kinds = Meitou.Data.Gameplay.BirdFlockData.LoadAll(db);
            try { birdAreas = Meitou.Data.Gameplay.BiomeMap.Create(Meitou.Data.Gameplay.BiomeGroupData.LoadAll(db), Meitou.Data.World.WeatherAreas.Load(install)); }
            catch (Exception e) when (e is IOException or FormatException) { birdAreas = null; }
            birds = new Meitou.Game.Effects.BirdFlocks(kinds, birdHeights.HeightAt, GroupIdOfCell);
            birdStep = new Meitou.Game.Effects.BirdThread(() => birds.Update(birdDt, birdEye, birdFocus, birdPlanes));

        }
        // The step runs beside the frame: its flocks are shown one frame late (the original's thread is as loose), and a step still running when the next frame starts is left to finish.
        bool async = o.Screenshot is null && !duel.IsDuel;
        if (birdStep!.Busy) return;
        if (birdStarted && async)
        {
            gpu.BirdPieces.Clear();
            birds.AddPieces(gpu.BirdPieces, mesh => gpu.Debris is { } birdMeshes && birdMeshes.Extent(mesh, null) is not null);   // a mesh is loaded when first asked for
        }
        UpdateBirdAttractors(db);
        double now = realTime.Elapsed.TotalSeconds;
        birdDt = double.IsNaN(birdClock) ? 0 : (float)(now - birdClock);
        birdClock = now;
        birdEye = camera.Eye;
        birdFocus = camera.Target;
        birdPlanes = BirdFrustum();
        birdHeights.Refresh(gpu.Terrain);
        birdStarted = true;
        if (async) birdStep.Start();
        else
        {
            birds.Update(birdDt, birdEye, birdFocus, birdPlanes);
            gpu.BirdPieces.Clear();
            birds.AddPieces(gpu.BirdPieces, mesh => gpu.Debris is { } birdMeshes && birdMeshes.Extent(mesh, null) is not null);
        }
    }

    readonly Meitou.Game.Rendering.Terrain.TerrainHeights birdHeights = new();
    Meitou.Game.Effects.BirdThread? birdStep;
    bool birdStarted;
    float birdDt;
    Vector3 birdEye, birdFocus;
    Vector4[]? birdPlanes;

    long birdBuildingsRevision = -1;
    object? birdAttractorsFor;
    Task<List<(string Key, Meitou.Data.GameRecord Record, Vector3 Position)>>? birdLevelSources;
    readonly List<Meitou.Game.Effects.BirdAttractor> birdBuildingAttractors = [], birdLevelAttractors = [];

    /// <summary>
    /// The <c>bird attractor</c> references of the objects the original registers them for (<c>FUN_140263700</c>, on a building's, map feature's or animal's creation):
    /// the player's standing buildings, the level's buildings and map features (read once off the main thread) and every animal, which carries its own as it moves.
    /// </summary>
    void UpdateBirdAttractors(Meitou.Data.GameDatabase db)
    {
        var snapshot = session.CurrentSnapshot;
        bool changed = false;
        if (!ReferenceEquals(birdAttractorsFor, session.World))
        {
            birdAttractorsFor = session.World;
            birdLevelAttractors.Clear();
            birdLevelSources = scene.Objects is { } objects ? Task.Run(objects.BirdAttractorSources) : null;
            birdBuildingsRevision = -1;
            changed = true;
        }
        if (birdLevelSources is { IsCompletedSuccessfully: true } read)
        {
            foreach (var (key, record, at) in read.Result) AddBirdAttractors(birdLevelAttractors, key, record, at);
            birdLevelSources = null;
            changed = true;
        }
        if (snapshot.BuildingsRevision != birdBuildingsRevision)
        {
            birdBuildingsRevision = snapshot.BuildingsRevision;
            birdBuildingAttractors.Clear();
            foreach (var building in snapshot.Buildings)
                if (building.Complete && db.Find(building.Record) is { } record) AddBirdAttractors(birdBuildingAttractors, building.Id, record, building.Position);
            changed = true;
        }
        var animals = new List<Meitou.Game.Effects.BirdAttractor>();
        foreach (var c in snapshot.Characters)
            if (c.Animal && db.Find(c.RecordId) is { } animal) AddBirdAttractors(animals, c.Id, animal, c.Position);
        if (changed || animals.Count > 0 || birdHadAnimals) birds!.SetAttractors([.. birdBuildingAttractors, .. birdLevelAttractors, .. animals]);
        birdHadAnimals = animals.Count > 0;
    }

    bool birdHadAnimals;

    void AddBirdAttractors(List<Meitou.Game.Effects.BirdAttractor> into, object owner, Meitou.Data.GameRecord record, Vector3 at)
    {
        foreach (var link in record.GetReferences("bird attractor"))
            into.Add(new Meitou.Game.Effects.BirdAttractor((owner, link.TargetStringId), link.TargetStringId, at, link.Values.Value0, link.Values.Value1));
    }

    string? GroupIdOfCell(int cx, int cz) => birdAreas is { } areas && areas.GroupOf(cx, cz) is >= 0 and var g ? areas.Groups[g].Id : null;

    /// <summary>The view's six planes, normals inwards and normalised (Ogre's cached frustum planes, as the birds' distance test takes them).</summary>
    Vector4[] BirdFrustum()
    {
        var planes = Meitou.Game.Rendering.Frame.WorldCamera.FrustumPlanes(camera.View * camera.Projection(birdAspect, camera.Near, camera.ViewDistance));
        for (int i = 0; i < planes.Length; i++) planes[i] /= new Vector3(planes[i].X, planes[i].Y, planes[i].Z).Length();
        return planes;
    }

    float birdAspect = 16f / 9;

    /// <summary>This frame's world draw overlaps the next world tick (set by the interactive loop; never in stills, the duel, recorded flights or the play benchmark).</summary>
    internal bool tickAhead;

    /// <summary>
    /// Meitou's dynamic tick ahead (engine choice): on once a world tick takes <see cref="AheadOnMs"/> (smoothed), off again under <see cref="AheadOffMs"/>, so a quiet
    /// world keeps the game's input delay and a big fight or crowd overlaps; a tick run ahead is slower (it shares the cores with the draw), hence the gap.
    /// </summary>
    const double AheadOnMs = 4, AheadOffMs = 2;
    bool worldBusy;

    internal bool TickAheadWanted() => config.TickAhead switch
    {
        TickAheadMode.Always => true,
        TickAheadMode.Dynamic => worldBusy = session.TickMs >= (worldBusy ? AheadOffMs : AheadOnMs),
        _ => false,
    };

}
