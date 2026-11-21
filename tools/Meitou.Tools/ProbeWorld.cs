using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Navigation;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;

/// <summary>
/// A save loaded headless for <c>meitou-tools probe</c>: the load order (<see cref="ProbeLoadOrder"/>), the population data, the world with the standard systems on one
/// thread, and optionally the real navmesh of the zones round the player's squad (else the terrain alone). Characters exist only once their zones
/// have activated, so the caller settles it for a few hundred ticks first.
/// </summary>
sealed class ProbeWorld : IDisposable
{
    /// <summary>
    /// The merged game data of the install's load order.
    /// </summary>
    public required GameDatabase Db { get; init; }

    /// <summary>
    /// The population data the world was made with.
    /// </summary>
    public required PopulationData Data { get; init; }

    /// <summary>
    /// The save as read (writers such as <see cref="BuildingSave.Write"/> change it in memory only).
    /// </summary>
    public required SaveGame Save { get; init; }

    /// <summary>
    /// The running world.
    /// </summary>
    public required World World { get; init; }

    /// <summary>
    /// What the load made of the save, which a capture carries on from.
    /// </summary>
    public required LoadedSave Loaded { get; init; }

    /// <summary>
    /// The level data of the load order.
    /// </summary>
    public required WorldLevelData Levels { get; init; }

    /// <summary>
    /// The navmesh, when it was asked for.
    /// </summary>
    public NavSystem? Nav { get; init; }

    /// <summary>
    /// The zone streaming of <c>--stream</c>; null when the zones were all built before the load.
    /// </summary>
    public ProbeZoneStreamer? Streamer { get; init; }

    /// <summary>
    /// The buildings' collision (sight rays, bolts, ragdolls), with the navmesh only.
    /// </summary>
    public RagdollGeometry? Buildings { get; init; }

    /// <summary>
    /// How far round the player's start the buildings' collision is gathered before the first tick (X and Z).
    /// </summary>
    const float CollisionReach = 800;

    /// <summary>
    /// How long the load waits for that gather (seconds).
    /// </summary>
    const double CollisionWaitSeconds = 120;

    /// <summary>
    /// The characters of the world.
    /// </summary>
    public CharacterTable Characters => World.Characters;

    /// <summary>
    /// Where the player's squad leader stands now; where the save put the player's first character when the load made no leader (its data
    /// files are missing).
    /// </summary>
    public Vector3 LeaderPosition => LeaderSlot(World) is { } slot ? Characters.Next[slot].Position : SavedStart(Save);

    /// <summary>
    /// Loads <paramref name="saveFolder"/> under <paramref name="order"/>; <paramref name="navmesh"/> builds the zones round the player's squad (slow) instead of walking the terrain.
    /// <paramref name="at"/> moves the player's characters there (X, Z) before the load, to look at another place with the same save; <paramref name="seed"/>
    /// is the world seed (the game's default is 0). <paramref name="stream"/> (with <paramref name="navmesh"/>) builds only the zones of the player's
    /// box first and streams the rest in while the world runs (<see cref="ProbeZoneStreamer"/>). Null, with the reason on stderr, when the save has no
    /// character of the player's faction.
    /// </summary>
    public static ProbeWorld? Load(GameInstall install, LoadOrder order, string saveFolder, bool navmesh, Vector2? at = null, ulong seed = 1,
        bool stream = false)
    {
        var db = GameDatabase.Load(order);
        var levels = WorldLevelData.Load(install, order, includeInteriors: true);
        var map = TerrainHeightmap.Open(install);
        var gate = new Lock();
        Func<float, float, float> height = (x, z) =>
        {
            lock (gate) return map.HeightAt(x, z);
        };
        var layouts = new BuildingLayouts(db, levels.Interiors);
        ResidentRuins.Apply(db, levels, layouts);   // the homes' ruins and layouts before anything reads the levels, as the game's boot
        var data = PopulationData(install, db, levels, layouts, height);
        var save = SaveGame.Load(SaveFolder.Open(saveFolder));
        ReportProblems(save);
        if (!HasPlayerCharacter(save))
        {
            WarnNoPlayerCharacter(save);
            return null;
        }
        if (at is { } spot) MovePlayer(save, spot, height);

        var zones = ZoneActivation.ZonesAround(SavedStart(save), stream ? 0 : 1);
        NavSystem? nav = navmesh ? LoadNavmesh(install, db, levels, layouts, data, zones, height) : null;
        IWalkability walk = nav is null ? new OpenGroundWalkability(height) : new NavAdapter(nav.Walkability);
        var streamer = stream && nav is not null ? new ProbeZoneStreamer(nav, zones) : null;
        var buildings = nav is null ? null : Collision(install, db, levels, height, nav, SavedStart(save));
        var (world, loaded) = MakeWorld(data, walk, save, seed, streamer, buildings);
        if (nav is not null) AddPlayerSites(db, world, nav);
        if (nav is not null) GiveGateCodes(world, nav);
        return new ProbeWorld
        {
            Db = db,
            Data = data,
            Save = save,
            World = world,
            Loaded = loaded,
            Levels = levels,
            Nav = nav,
            Streamer = streamer,
            Buildings = buildings,
        };
    }

    /// <summary>
    /// The buildings' collision the game's boot gives with a navmesh, gathered round <paramref name="start"/> before the first tick: the game
    /// gathers a zone on a worker when first asked, and a probe's first ticks would otherwise see through walls for as long as that takes. The
    /// probe's geometry is synchronous (<see cref="RagdollGeometry.Synchronous"/>): with a worker, whether a zone's collision was in by a tick
    /// depended on the machine's load, and about one run in 30 of a probe on the same save ended on another hash.
    /// </summary>
    static RagdollGeometry Collision(GameInstall install, GameDatabase db, WorldLevelData levels, Func<float, float, float> height, NavSystem nav,
        Vector3 start)
    {
        var geometry = new RagdollGeometry(install, db, levels, height, nav.Runtime, synchronous: true);
        var reach = new Vector3(CollisionReach, 0, CollisionReach);
        var triangles = new List<Vector3>();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!geometry.Buildings(start - reach, start + reach, triangles))
        {
            // a zone whose gather failed never answers ready
            if (waited.Elapsed.TotalSeconds > CollisionWaitSeconds)
            {
                Console.Error.WriteLine("The buildings' collision round the player is not all gathered: sight may pass through some walls.");
                break;
            }
            triangles.Clear();
            Thread.Sleep(50);
        }

        return geometry;
    }

    /// <summary>
    /// The save's sites in the navmesh, as the game's host puts them there (<c>BuildingSync.AddToNavmesh</c>: <c>path mode</c> not 0, and complete,
    /// a repair or without a <c>build threshold</c>), once the loaded zones they reach are built again.
    /// </summary>
    static void AddPlayerSites(GameDatabase db, World world, NavSystem nav)
    {
        var build = BuildingBuildData.LoadAll(db)
            .GroupBy(b => b.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var placements = world.Buildings.All
            .Where(site => InNavmesh(site, build))
            .Select(site => SitePlacement(site, world.Player.Faction))
            .ToList();
        nav.AddRuntimeBuildings(placements).Wait();
    }

    /// <summary>
    /// Whether the game's host puts a site in the navmesh (<c>BuildingSync.AddToNavmesh</c>).
    /// </summary>
    static bool InNavmesh(Meitou.Simulation.Construction.BuildSite site, Dictionary<string, BuildingBuildData> build)
    {
        if (!build.TryGetValue(site.Record, out var data) || data.PathMode == 0) return false;
        return site.LevelPlacement is not null || site.Complete || data.BuildThreshold == 0;
    }

    /// <summary>
    /// A site's navmesh placement as the game's host makes it (<c>BuildingSync.Placement</c>).
    /// </summary>
    static BuildingPlacement SitePlacement(Meitou.Simulation.Construction.BuildSite site, int playerFaction)
    {
        bool mine = site.Owner == playerFaction;
        if (site.LevelPlacement is { } level)
            return level with { Key = site.Id, WorldY = site.Position.Y, DestroyedOverride = site.Destroyed, PlayerOwned = mine };

        var zone = WorldLayout.ZoneOf(site.Position.X, site.Position.Z);
        return new(zone, site.Id, site.Record, site.Position, site.Rotation, null, site.Position.Y)
        {
            Key = site.Id,
            Contained = site.Parent is not null,
            ContainerId = site.Parent,
            PlayerOwned = mine,
        };
    }

    /// <summary>
    /// Hands the world the gate codes of the built zones, as the game's host does once their meshes are live (docs/game/pathfinding.md "Gate codes").
    /// </summary>
    static void GiveGateCodes(World world, NavSystem nav) =>
        world.Commands.Enqueue(new Meitou.Simulation.Locks.GateRegionsCommand(nav.LabelGates()) { Tick = world.Tick });

    /// <summary>
    /// Puts the save's player characters at <paramref name="spot"/> on the terrain, a step apart.
    /// </summary>
    static void MovePlayer(SaveGame save, Vector2 spot, Func<float, float, float> height)
    {
        int k = 0;
        foreach (var c in save.Characters.Where(c => c.Platoon.FactionId == save.PlayerFaction!.Id))
        {
            float x = spot.X + 3 * (k % 4), z = spot.Y + 3 * (k / 4);
            c.Position = new Vector3(x, height(x, z), z);
            k++;
        }
    }

    /// <summary>
    /// The population data of the level (as <c>mod-check</c> reads it).
    /// </summary>
    static PopulationData PopulationData(GameInstall install, GameDatabase db, WorldLevelData levels, BuildingLayouts layouts,
        Func<float, float, float> height)
    {
        var buildings = TownBuildings.Build(db, levels, layouts, (x, z) => height((float)x, (float)z));
        var biomes = Meitou.Data.Gameplay.BiomeMap.Create(db, WeatherAreas.Load(install));
        return Meitou.Simulation.Population.PopulationData.Create(db, levels.Towns().ToList(), new GeneratedAppearances(db, install.Root),
            biomes: biomes, buildings: buildings, roads: levels.Roads(), townZones: levels.TownZones(), level: levels.Level);
    }

    /// <summary>
    /// The navmesh with <paramref name="zones"/> built, and the building sight and layouts it needs in the data.
    /// </summary>
    static NavSystem LoadNavmesh(GameInstall install, GameDatabase db, WorldLevelData levels, BuildingLayouts layouts, PopulationData data,
        IEnumerable<ZoneCoordinate> zones, Func<float, float, float> height)
    {
        var nav = new NavSystem(install, db, levels, height);
        nav.Walkability.Roads = data.Roads;
        foreach (var zone in zones) nav.LoadZone(zone).Wait();

        data.Layouts = layouts;
        data.Sight = new Meitou.Simulation.Perception.BuildingSight(install, db, levels, layouts);
        return nav;
    }

    /// <summary>
    /// The world on one thread with paths found at once, the save loaded into it and the camera focus on the player's squad leader;
    /// <paramref name="streamer"/> gates its population's zones before the load; <paramref name="buildings"/> is the buildings' collision (null: none).
    /// Returns the world and what the load made of the save.
    /// </summary>
    static (World World, LoadedSave Loaded) MakeWorld(PopulationData data, IWalkability walk, SaveGame save, ulong seed,
        ProbeZoneStreamer? streamer, Meitou.Simulation.Ragdolls.IRagdollEnvironment? buildings)
    {
        var options = new StandardSystemOptions
        {
            SynchronousPaths = true,
            Dialogue = new Meitou.Simulation.Dialogue.DialogueSettings { AmbientEvents = true },
            Population = new PopulationSettings { Background = false },
            RagdollEnvironment = buildings,
        };
        var world = new World(new WorldSettings { Seed = seed, Threads = 1, PublishSnapshots = false }, walk, StandardSystems.Build(data, walk, options).Systems);
        if (walk is Meitou.Simulation.Locks.IDoorGate doors) world.Fixtures.Doors = doors;
        streamer?.Attach(world);

        var loaded = SaveLoader.Load(world, save, data, new SaveLoadOptions { Appearances = data.Appearances });
        if (LeaderSlot(world) is null) WarnNoLeader(loaded);

        var focus = LeaderSlot(world) is { } slot ? world.Characters.Previous[slot].Position : SavedStart(save);
        world.Commands.Enqueue(new FocusCommand(focus) { Tick = world.Tick });
        return (world, loaded);
    }

    /// <summary>
    /// The slot of the player's squad leader, or null when the load made none (every member left out, e.g. made from a missing mod).
    /// </summary>
    static int? LeaderSlot(World world) => world.Squads.Find(world.Player.Squad) is { Leader.IsNone: false } squad ? squad.Leader.Slot : null;

    /// <summary>
    /// Where the save put the player's first character.
    /// </summary>
    static Vector3 SavedStart(SaveGame save) => save.Characters.First(c => c.Platoon.FactionId == save.PlayerFaction!.Id).Position;

    /// <summary>
    /// Whether the save has a character of the player's faction (<see cref="SavedStart"/> needs one).
    /// </summary>
    static bool HasPlayerCharacter(SaveGame save) =>
        save.PlayerFaction is { } player && save.Characters.Any(c => c.Platoon.FactionId == player.Id);

    /// <summary>
    /// Says the save has no player character. A zip unpacked without its UTF-8 names (Git Bash's unzip mangles a Cyrillic platoon file name)
    /// reads every platoon without its characters (#761's save).
    /// </summary>
    static void WarnNoPlayerCharacter(SaveGame save)
    {
        Console.Error.WriteLine($"probe     the save has no character of the player's faction ({save.Characters.Count()} characters in "
            + $"{save.Platoons.Count} platoons read)");
        Console.Error.WriteLine("  A zip unpacked without its UTF-8 names loses the platoons' characters: unpack it with .NET's ZipFile or Explorer.");
    }

    /// <summary>
    /// Says how many problems the save read with (<see cref="SaveGame.Problems"/>), with the first few.
    /// </summary>
    static void ReportProblems(SaveGame save)
    {
        if (save.Problems.Count == 0) return;

        Console.Error.WriteLine($"probe     the save read with {save.Problems.Count} problem(s), {save.RejectedFiles.Count} file(s) rejected");
        foreach (var problem in save.Problems.Take(5)) Console.Error.WriteLine("  " + problem);
    }

    /// <summary>
    /// Says the player's squad has no characters and why: the load's notes name the ones it left out.
    /// </summary>
    static void WarnNoLeader(LoadedSave loaded)
    {
        Console.Error.WriteLine("probe     the player's squad has no characters; centring on where the save put the first one");
        foreach (var note in loaded.Notes) Console.Error.WriteLine("  " + note);
    }

    /// <summary>
    /// Runs <paramref name="ticks"/> ticks, streaming zones before each with <c>--stream</c>.
    /// </summary>
    public void Tick(int ticks)
    {
        for (int k = 0; k < ticks; k++)
        {
            Streamer?.BeforeTick(World);
            World.RunTick();
        }
    }

    /// <summary>
    /// The living characters' slots whose name contains <paramref name="name"/>.
    /// </summary>
    public List<int> Named(string name) =>
        [.. Enumerable.Range(0, Characters.HighWater).Where(i => Characters.Next[i].Alive && Characters.Cold(i)?.Name.Contains(name) == true)];

    /// <summary>
    /// The living characters' slots within <paramref name="radius"/> (3D) of <paramref name="at"/>.
    /// </summary>
    public List<int> Near(Vector3 at, float radius) =>
        [.. Enumerable.Range(0, Characters.HighWater).Where(i => IsNear(i, at, radius))];

    /// <summary>
    /// Whether slot <paramref name="i"/> holds a living character within <paramref name="radius"/> of <paramref name="at"/>.
    /// </summary>
    bool IsNear(int i, Vector3 at, float radius) =>
        Characters.Next[i].Alive && Characters.Cold(i) is not null && Vector3.Distance(Characters.Next[i].Position, at) < radius;

    /// <summary>
    /// A faction's name, or its number when it has none (-1 for none).
    /// </summary>
    public string Faction(int faction) => faction >= 0 && faction < Data.Factions.Count ? Data.Factions[faction].Name : faction.ToString();

    /// <summary>
    /// One line naming the character in slot <paramref name="i"/>: slot, name, faction, squad, task, combat and position.
    /// </summary>
    public string Who(int i)
    {
        var c = Characters.Cold(i)!;
        var task = (CharacterTask)Characters.Next[i].Task;
        return $"{i} '{c.Name}' fac={Faction(c.Faction)} sq={c.SquadId} task={task} combat={c.InCombat} pos={Characters.Next[i].Position:F0}";
    }

    /// <summary>
    /// Prints and clears the world's player messages.
    /// </summary>
    public void PrintMessages()
    {
        foreach (var m in World.TakeMessages()) Console.WriteLine("  MSG " + m);
    }

    /// <summary>
    /// Disposes the world and the buildings' collision.
    /// </summary>
    public void Dispose()
    {
        World.Dispose();
        Buildings?.Dispose();
    }
}
