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

/// <summary>Game-only options; everything else is the world options shared with the viewer (<see cref="WorldOptions"/>).</summary>
sealed class GameOptions
{
    public int? FpsLimit, TickRate, SimThreads;
    public ulong Seed;
    public bool? VSync;
    /// <summary>With <c>--screenshot</c>: control ticks (and their real time of simulation, at speed 1) run before the picture, with no input.</summary>
    public int Ticks;
    public bool FreeCamera, NoPopulation, NoNavmesh, NewGame, ListStarts, SelectPlayer;
    /// <summary>Multiplies the time of the body-part and blood rates (<c>MedicalContext.BodyTimeScale</c>, default 1: the documented rates).</summary>
    public float BodyTimeScale = 1;
    /// <summary>The start to play (null = the default); with <c>--select-player</c>/<c>--move-to</c> the picture shows a selected squad walking.</summary>
    public string? NewGameName;
    public (float X, float Z)? MoveTo;
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
          --no-population            no town residents or movement (an empty world)
          --no-navmesh               paths on open ground (no buildings), as in the tests; default: the navmesh of the active zones (built on first use, cached)
          --body-time-scale <x>      multiplies the time of body-part and blood rates (default 1, the documented rates in game hours; see docs/simulation.md "Bodies wired")
          --new-game [start]         a new game as the NEW_GAME_STARTOFF start (default Wanderer): the player squad at its town, camera on it
          --list-starts              print the available starts and exit
          --select-player, --move-to <x> <z>   with --new-game: select the squad / order it to walk (for screenshots)
          --ticks <n>                with --screenshot: run n control ticks (and the same real time of the simulation, at speed 1) before the picture
          --quit-after <s>           close after s seconds and print the frame rate (smoke test)
          --yaw/--pitch/--distance   start view: heading, pitch above the horizon and boom (Kenshi: 30 degrees, boom 150; clamped to 10..2000)
          world options as meitou-viewer --world: --at, --zone, --town, --radius, --time, --screenshot, --size, --no-foliage, ...
        Keys: W/A/S/D move, Q/E or Left/Right rotate, Up/Down pitch, wheel or PageUp/PageDown zoom, middle drag orbit, left click or drag selects, right click moves (shift queues), 1..9 / ` select, R stops,
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
                case "--no-population": g.NoPopulation = true; break;
                case "--no-navmesh": g.NoNavmesh = true; break;
                case "--body-time-scale": g.BodyTimeScale = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--new-game":
                    g.NewGame = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) g.NewGameName = args[++i];
                    break;
                case "--list-starts": g.ListStarts = true; break;
                case "--select-player": g.SelectPlayer = true; break;
                case "--move-to": g.MoveTo = (float.Parse(Next(), CultureInfo.InvariantCulture), float.Parse(Next(), CultureInfo.InvariantCulture)); break;
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
        Meitou.Data.GameDatabase? db = null;
        Meitou.Data.Gameplay.NewGameStart? start = null;
        if (game.ListStarts || game.NewGame)
        {
            db = Meitou.Data.GameDatabase.Load(Meitou.Data.LoadOrder.FromInstall(install));
            var starts = Meitou.Data.Gameplay.NewGameStart.LoadAll(db);
            if (game.ListStarts)
            {
                foreach (var s in starts.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                    Console.WriteLine($"{s.Name,-28} {s.Money,7} cats  {s.Squad.Count} squad links  {(s.ForceStartPos ? "fixed position" : s.Towns.Count + " town(s)")}");
                return 0;
            }
            start = Meitou.Data.Gameplay.NewGameStart.Find(starts, game.NewGameName ?? Meitou.Data.Gameplay.NewGameStart.DefaultName);
            if (start is null)
            {
                Console.Error.WriteLine($"No start '{game.NewGameName ?? Meitou.Data.Gameplay.NewGameStart.DefaultName}'; --list-starts shows them.");
                return 2;
            }
            if (game.NoPopulation)
            {
                Console.Error.WriteLine("--new-game needs the population (it is the player's squad); drop --no-population.");
                return 2;
            }
            // Load the world around the start: its first listed town, else the fixed position.
            Vector2? at = start.ForceStartPos ? start.StartPosition : null;
            if (at is null)
            {
                var towns = Meitou.Data.World.WorldLevelData.Load(install).Towns().ToList();
                foreach (var link in start.Towns)
                    if (towns.FirstOrDefault(t => t.TownId == link.Id) is { } placed) { at = new Vector2(placed.Position.X, placed.Position.Z); break; }
            }
            at ??= start.StartPosition;
            (world.X, world.Z) = (at.Value.X, at.Value.Y);
            world.Town = null;
            Console.WriteLine($"new game  {start.Name}: {start.Money} cats, {start.Squad.Count} squad link(s), at {at.Value.X:0}, {at.Value.Y:0}");
        }
        using var scene = WorldFrame.Load(install, world, db);
        if (scene is null) return 1;
        if (world.Info) return 0;
        return new GameHost(install, scene, new AssetLocator(install), world, game, config, start).Run();
    }
}

/// <summary>
/// The game loop: a real-time control tick (<see cref="FixedStepClock"/>, 30 Hz by default) runs the input actions, the camera rig
/// and pause/speed; a game-time simulation tick (<see cref="SimulationClock"/>) advances the game clock; each displayed frame draws the camera interpolated between the last two ticks at the display rate.
/// </summary>
sealed class GameHost(GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o, GameOptions g, UserConfig config, Meitou.Data.Gameplay.NewGameStart? start)
{
    WorldSession session = null!;   // disposed with the host (Run)
    Meitou.Simulation.PopulationSystem? population;
    Meitou.Navigation.NavSystem? nav;
    volatile bool navChanged;
    readonly Dictionary<Meitou.Data.World.ZoneCoordinate, long> navZones = [];
    PlayerInterface player = null!;
    Meitou.Simulation.Squad? playerSquad;
    WorldFrame.Gpu gpu = null!;
    WorldCamera camera = null!;
    WorldRenderOptions render = null!;
    readonly Stopwatch realTime = Stopwatch.StartNew();

    public int Run()
    {
        try { return o.Screenshot is not null ? Screenshot() : Interactive(); }
        finally { session?.Dispose(); nav?.Dispose(); }
    }

    void Boot(VulkanDisplay display, bool interactive)
    {
        var context = display.Context;
        gpu = WorldFrame.CreateGpu(context, install, scene, assets, o, interactive);
        if (gpu.Post is { } vendorPost) vendorPost.UpscalerFactory = Meitou.Rendering.Upscalers.VendorUpscalers.Factory(display.Context, streamline);
        (camera, render) = WorldFrame.Setup(scene, o);
        // The simulation samples the CPU heightmap (immutable, any thread), not the renderer's terrain.
        var heights = new Meitou.Data.World.GroundHeights(scene.Window, scene.Coarse, scene.CoarseSize, WorldFrame.CoarseStep);
        Meitou.Simulation.IWalkability walkability = new Meitou.Simulation.OpenGroundWalkability(heights.HeightAt);
        var systems = new List<Meitou.Simulation.ITickSystem>();
        if (!g.NoPopulation && scene.Database is { } gameDb)
        {
            var levels = scene.Objects?.Levels ?? Meitou.Data.World.WorldLevelData.Load(install);
            if (!g.NoNavmesh)
            {
                nav = new Meitou.Navigation.NavSystem(install, gameDb, levels, heights.HeightAt);
                nav.ZoneReady += _ => navChanged = true;
                walkability = new NavAdapter(nav.Walkability);
            }
            var data = Meitou.Simulation.PopulationData.Create(gameDb, levels.Towns(), new Meitou.Simulation.GeneratedAppearances(gameDb, install.Root));
            population = new Meitou.Simulation.PopulationSystem(data, new Meitou.Simulation.PopulationSettings { Background = interactive });
            if (nav is not null) population.ZoneGate = zone => nav.Walkability.World.Contains(zone);
            systems.Add(population);
            systems.Add(new Meitou.Simulation.PlayerSystem());
            systems.Add(new Meitou.Simulation.MovementSystem(new Meitou.Simulation.PathService(walkability, synchronous: !interactive)));
            systems.Add(new Meitou.Simulation.BodySystem(data.Bodies.Constants, data.BodyOptions, g.BodyTimeScale));
            systems.Add(new Meitou.Simulation.AnimationSystem(Meitou.Data.Gameplay.AnimationLibrary.FromDatabase(gameDb), Meitou.Data.Gameplay.AnimationLengths.Load(install.Root), Meitou.Data.Gameplay.GameConstants.FromDatabase(gameDb).AnimationBlendRate));
        }
        session = new WorldSession(scene.Focus, (scene.X0, scene.Z0, scene.X1, scene.Z1), gpu.Terrain.HeightAt, o.Hour, g.TickRate ?? config.TickRate,
            clock: GameClockFor(scene.Database, o.Hour),
            simulation: new Meitou.Simulation.WorldSettings { Seed = g.Seed, MinPartitionSize = 128, Threads = Math.Max(1, g.SimThreads ?? config.SimThreads ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 8)) },
            systems: systems, walkability: walkability);
        var target = camera.Target;
        foreach (var problem in session.Bindings.Apply(config.Bindings)) Console.Error.WriteLine($"config    binding skipped: {problem}");
        // Right is the command button now; older saved configs bound it to orbit.
        session.Bindings.Set(InputAction.Orbit, [.. session.Bindings.Get(InputAction.Orbit).Where(b => !(b.IsMouse && b.Button == EngineButton.Right))]);
        player = new PlayerInterface(session, heights.HeightAt, DrawnPosition);
        if (nav is not null && (start is not null || !interactive))
        {
            // The squad is placed on the mesh, and a still has its town at once: wait for the zones round the start (a cold build is about 2 s, then cached).
            var watch = Stopwatch.StartNew();
            var zones = Meitou.Simulation.ZoneActivation.ZonesAround(scene.Focus, 0);
            Task.WaitAll([.. zones.Select(z => (Task)nav.LoadZone(z))]);
            foreach (var z in zones) navZones[z] = Stopwatch.GetTimestamp();
            Console.WriteLine($"navmesh   {zones.Count} zone(s) round the start ready in {watch.ElapsedMilliseconds} ms");
        }
        if (start is not null && population is not null)
        {
            playerSquad = population.StartPlayer(session.World, start);
            var lead = session.World.Characters.Previous[playerSquad.Leader.Slot].Position;
            target = lead;
            Console.WriteLine($"player    {playerSquad.Members.Count} characters, leader at {lead.X:0}, {lead.Z:0}");
        }
        if (scene.Database is { } characterDb && !g.NoPopulation && gpu.Characters is null)
            gpu.Characters = new CharacterRenderer(context, install, characterDb, assets) { Source = FillDrawList, Guard = gpu.Guard };
        var rig = session.Camera;
        rig.Place(new Vector2(target.X, target.Z), (o.Yaw ?? 30) * MathF.PI / 180,
            (o.Pitch ?? Meitou.Data.World.KenshiCamera.InitialPitchDegrees) * MathF.PI / 180, o.Distance ?? Meitou.Data.World.KenshiCamera.InitialDistance);
        if (g.FreeCamera)
        {
            session.Input.SetKey(FirstKey(InputAction.ToggleFreeCamera), true);
            session.Tick();
            session.Input.SetKey(FirstKey(InputAction.ToggleFreeCamera), false);
        }
        SendFocus();
        ApplyCamera(session.Camera.Current);
        if (interactive) WorldFrame.FinishLoading(context);
    }

    /// <summary>The game clock with sunrise, sunset and days per year from the CONSTANTS record (defaults without data).</summary>
    static GameClock GameClockFor(Meitou.Data.GameDatabase? db, double startHour)
    {
        if (db is null) return new GameClock(startHour);
        var c = Meitou.Data.Gameplay.GameConstants.FromDatabase(db);
        return new GameClock(startHour, sunrise: c.Sunrise, sunset: c.Sunset, daysPerYear: c.DaysPerYear);
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

    /// <summary>Keeps the navmesh on the zones the population has active (built on the nav thread, cached), drops the ones idle for 30 s, and has characters ask for their paths again when a mesh arrived (their first path may have crossed a building).</summary>
    void UpdateNav(bool wait = false)
    {
        if (nav is null || population is null) return;
        long now = Stopwatch.GetTimestamp();
        var pending = new List<Task>();
        foreach (var z in population.ActiveZones)
        {
            if (!navZones.ContainsKey(z)) pending.Add(nav.LoadZone(z));
            navZones[z] = now;
        }
        foreach (var (z, seen) in navZones.ToList())
            if (Stopwatch.GetElapsedTime(seen, now).TotalSeconds > 30) { nav.UnloadZone(z); navZones.Remove(z); }
        if (wait) Task.WaitAll([.. pending]);
        if (navChanged || wait && pending.Count > 0)
        {
            navChanged = false;
            session.World.Commands.Enqueue(new Meitou.Simulation.RepathCommand { Tick = session.World.Tick });
        }
    }

    /// <summary>Gives the player interface the camera it picks with.</summary>
    void UpdateView(int width, int height)
    {
        player.ViewProjection = camera.View * camera.Projection(width / (float)Math.Max(height, 1), camera.Near, camera.ViewDistance);
        player.Eye = camera.Eye;
        player.Width = width;
        player.Height = height;
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
        if (playerSquad is not null && (g.SelectPlayer || g.MoveTo is not null))
        {
            session.World.Commands.Enqueue(new Meitou.Simulation.SelectCommand(playerSquad.Members.ToList()) { Tick = session.World.Tick });
            if (g.MoveTo is { } to) session.World.Commands.Enqueue(new Meitou.Simulation.MoveOrder([], new Vector3(to.X, 0, to.Z)) { Tick = session.World.Tick });
        }
        for (int i = 0; i < g.Ticks; i++)
        {
            session.Tick();
            session.AdvanceSimulation(session.Ticks.TickSeconds);   // one control tick of real time, at speed 1
            UpdateNav(wait: true);
        }
        ApplyCamera(session.Camera.Current);
        gpu.Streamer?.Settle(gpu.Anchor ?? camera.Eye);
        gpu.Objects?.Settle(gpu.Anchor ?? camera.Eye);
        gpu.Foliage?.Settle(gpu.Anchor ?? camera.Eye);
        gpu.Characters?.Settle(gpu.Anchor ?? camera.Eye);
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
        if (playerSquad is not null && DebugOverlay.TryCreate(context) is { } overlay)
        {
            using (overlay)
            {
                overlay.Target = target;
                UpdateView(w, h);
                player.Draw(overlay, w, h);
                if (session.CurrentSnapshot.Characters.FirstOrDefault(c => c.IsPlayer) is { } me && player.Project(DrawnPosition(me) + new Vector3(0, 1, 0)) is { } px)
                {
                    var picked = player.Pick(px);
                    var ray = player.Ray(px + new Vector2(0, 60));
                    var hit = ray is { } r ? player.GroundHit(r.Origin, r.Direction) : null;
                    Console.WriteLine($"pickcheck pixel {px.X:0}, {px.Y:0} picks {(picked is { } id && id == me.Id ? "the leader" : "NOTHING")}; ground 60 px lower: {(hit is { } gh ? $"{gh.X:0}, {gh.Z:0} (leader at {me.Position.X:0}, {me.Position.Z:0})" : "no hit")}");
                }
            }
        }
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
        bool panelDrag = false, shiftDown = false;
        Vector2 Pixels(Vector2 p) => p * new Vector2(window.FramebufferSize.X / (float)Math.Max(window.Size.X, 1), window.FramebufferSize.Y / (float)Math.Max(window.Size.Y, 1));
        foreach (var kb in silkInput.Keyboards)
        {
            kb.KeyDown += (_, k, _) =>
            {
                if (k is SilkKey.ShiftLeft or SilkKey.ShiftRight) shiftDown = true;
                if (Map(k) is { } key) { session.Input.SetKey(key, true); player.Key(key, shiftDown); }
            };
            kb.KeyUp += (_, k, _) =>
            {
                if (k is SilkKey.ShiftLeft or SilkKey.ShiftRight) shiftDown = false;
                if (Map(k) is { } key) session.Input.SetKey(key, false);
            };
        }
        foreach (var mouse in silkInput.Mice)
        {
            mouse.MouseDown += (m, b) =>
            {
                if (b == SilkButton.Left && panel?.MouseDown(Pixels(m.Position)) == true) { panelDrag = true; return; }
                if (panel?.Contains(Pixels(m.Position)) == true) return;
                if (Map(b) is { } button)
                {
                    if (player.MouseDown(button, Pixels(m.Position), shiftDown)) return;
                    session.Input.SetMouseButton(button, true);
                }
            };
            mouse.MouseUp += (m, b) =>
            {
                if (Map(b) is { } released) player.MouseUp(released, Pixels(m.Position), shiftDown);
                panel?.MouseUp();
                panelDrag = false;
                if (Map(b) is { } button) session.Input.SetMouseButton(button, false);
            };
            mouse.MouseMove += (_, p) =>
            {
                session.Input.SetMousePosition(p.X, p.Y);
                player.MouseMove(Pixels(p));
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
        double drawTotal = 0;
        while (!window.IsClosing && !quit)
        {
            window.DoEvents();
            if (window.IsClosing) break;
            double now = frame.Elapsed.TotalSeconds, dt = now - last;
            last = now;
            long a0 = Stopwatch.GetTimestamp();
            session.Advance(dt);
            advanceTotal += Stopwatch.GetElapsedTime(a0).TotalMilliseconds;
            UpdateNav();
            SendFocus();
            ApplyCamera(session.CameraAt());
            var size = window.FramebufferSize;
            UpdateView(size.X, size.Y);
            if (display.BeginFrame(size.X, size.Y))
            {
                long t0 = Stopwatch.GetTimestamp();
                // The chain and the panel draw into the window's backbuffer (made again when the size changes).
                var backbuffer = display.Backbuffer!;
                gpu.Post!.Target = backbuffer;
                if (overlay is not null) overlay.Target = backbuffer;
                DrawWorld(size.X, size.Y);
                cpuSum += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                drawTotal += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                bool shot = screenshotRequested;
                screenshotRequested = false;
                if (overlay is not null && !g.NoPopulation) DrawMarkers(overlay, size.X, size.Y);
                if (overlay is not null && !g.NoPopulation) player.Draw(overlay, size.X, size.Y);
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
            if (g.QuitAfter is { } quitAfter && now >= quitAfter) { Console.WriteLine($"profile   {session.CurrentSnapshot.Characters.Count} characters in the snapshot, {session.CurrentSnapshot.Characters.Count(c => c.Appearance is not null)} drawable; per frame: simulation advance {advanceTotal / Math.Max(totalFrames, 1):0.000} ms ({session.Simulation.TotalTicks} ticks, {advanceTotal / Math.Max(session.Simulation.TotalTicks, 1):0.000} ms per tick), host draw-list fill {fillTotal / Math.Max(fillCalls, 1):0.000} ms, world draw cpu {drawTotal / Math.Max(totalFrames, 1):0.00} ms, {(gpu.Characters is { } chars ? chars.Statistics() : "no characters")}");
                Console.WriteLine($"smoke     {totalFrames} frames in {now:0.0} s: {totalFrames / now:0} fps on average, {session.Ticks.TotalTicks} control ticks, {session.Simulation.TotalTicks} simulation ticks, game time {session.Clock.TimeText}"); quit = true; }
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


    // ---- characters: the draw list from the snapshots, markers for what has nothing to draw ----

    WorldSnapshot? indexedFor;
    readonly Dictionary<CharacterId, CharacterSnapshot> previousById = [];
    Vector3 lastFocus;
    bool sentFocus;

    /// <summary>Tells the world where the camera looks, when it has moved enough to matter (the zones follow it).</summary>
    void SendFocus()
    {
        var target = session.Camera.Current.Target;
        if (sentFocus && Vector3.DistanceSquared(target, lastFocus) < 50 * 50) return;
        session.World.Commands.Enqueue(new Meitou.Simulation.FocusCommand(target) { Tick = session.World.Tick });
        lastFocus = target;
        sentFocus = true;
    }

    void IndexPrevious()
    {
        var previous = session.PreviousSnapshot;
        if (ReferenceEquals(indexedFor, previous)) return;
        previousById.Clear();
        foreach (var c in previous.Characters) previousById[c.Id] = c;
        indexedFor = previous;
    }

    /// <summary>Where a character is drawn: between the last two snapshots, by the simulation's alpha.</summary>
    Vector3 DrawnPosition(CharacterSnapshot c)
    {
        IndexPrevious();
        return previousById.TryGetValue(c.Id, out var before) ? Vector3.Lerp(before.Position, c.Position, session.SimulationAlpha) : c.Position;
    }

    /// <summary>Fills the renderer's list from the last two snapshots, interpolated by the simulation's alpha.</summary>
    void FillDrawList(CharacterDrawList list)
    {
        long f0 = Stopwatch.GetTimestamp();
        try { FillDrawListCore(list); }
        finally { fillTotal += Stopwatch.GetElapsedTime(f0).TotalMilliseconds; fillCalls++; }
    }

    double advanceTotal, fillTotal;
    long fillCalls;

    void FillDrawListCore(CharacterDrawList list)
    {
        var current = session.CurrentSnapshot;
        IndexPrevious();
        float alpha = session.SimulationAlpha;
        foreach (var c in current.Characters)
        {
            if (c.Appearance is null) continue;
            var position = c.Position;
            float yaw = c.Yaw;
            CharacterSnapshot? before = null;
            if (previousById.TryGetValue(c.Id, out var found))
            {
                before = found;
                position = Vector3.Lerp(found.Position, c.Position, alpha);
                yaw = Meitou.Engine.Time.Interp.LerpAngle(found.Yaw, c.Yaw, alpha);
            }
            var poses = new CharacterPose[c.Animations.Count];
            for (int i = 0; i < poses.Length; i++)
            {
                var layer = c.Animations[i];
                float time = layer.Time, weight = layer.Weight;
                // Between ticks the clip time and weight move on from the last snapshot (a wrap of a looping clip just shows the new time).
                if (before is not null)
                    foreach (var old in before.Animations)
                        if (old.Name == layer.Name)
                        {
                            if (layer.Time >= old.Time) time = old.Time + (layer.Time - old.Time) * alpha;
                            weight = old.Weight + (layer.Weight - old.Weight) * alpha;
                            break;
                        }
                poses[i] = new CharacterPose(layer.Name, time, weight);
            }
            list.Add(new CharacterInstance(((long)c.Id.Slot << 32 | (uint)c.Id.Generation) + 1, c.Appearance, position, yaw, poses));
        }
    }

    /// <summary>Squares over the characters that have no drawing (animals, failed builds), coloured by faction.</summary>
    void DrawMarkers(DebugOverlay overlay, int width, int height)
    {
        var view = camera.View * camera.Projection(width / (float)Math.Max(height, 1), camera.Near, camera.ViewDistance);
        var current = session.CurrentSnapshot;
        foreach (var c in current.Characters)
        {
            if (c.Appearance is not null) continue;
            var clip = Vector4.Transform(new Vector4(c.Position + new Vector3(0, 10, 0), 1), view);
            if (clip.W <= 0.1f) continue;
            float x = (clip.X / clip.W * 0.5f + 0.5f) * width, y = (0.5f - clip.Y / clip.W * 0.5f) * height;
            if (x < -10 || y < -10 || x > width + 10 || y > height + 10) continue;
            float hue = c.Faction < 0 ? 0 : (c.Faction * 0.618034f) % 1f;
            var colour = new Vector4(0.5f + 0.5f * MathF.Sin(hue * MathF.Tau), 0.5f + 0.5f * MathF.Sin(hue * MathF.Tau + 2.1f), 0.5f + 0.5f * MathF.Sin(hue * MathF.Tau + 4.2f), 1);
            overlay.Rect(x - 3, y - 3, x + 3, y + 3, colour);
        }
        overlay.Flush(width, height);
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
