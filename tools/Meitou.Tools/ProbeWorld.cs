using System.Numerics;
using Meitou.Content;
using Meitou.Data;
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
    /// The navmesh, when it was asked for.
    /// </summary>
    public NavSystem? Nav { get; init; }

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
    /// </summary>
    public static ProbeWorld Load(GameInstall install, LoadOrder order, string saveFolder, bool navmesh)
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
        var data = PopulationData(install, db, levels, layouts, height);
        var save = SaveGame.Load(SaveFolder.Open(saveFolder));

        NavSystem? nav = navmesh ? LoadNavmesh(install, db, levels, layouts, data, save, height) : null;
        IWalkability walk = nav is null ? new OpenGroundWalkability(height) : new NavAdapter(nav.Walkability);
        var world = MakeWorld(data, walk, save);
        return new ProbeWorld { Db = db, Data = data, Save = save, World = world, Nav = nav };
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
    /// The navmesh with the 3 x 3 zones round the player's first character built, and the building sight and layouts it needs in the data.
    /// </summary>
    static NavSystem LoadNavmesh(GameInstall install, GameDatabase db, WorldLevelData levels, BuildingLayouts layouts, PopulationData data,
        SaveGame save, Func<float, float, float> height)
    {
        ResidentRuins.Apply(db, levels, layouts);
        var nav = new NavSystem(install, db, levels, height);
        nav.Walkability.Roads = data.Roads;
        foreach (var zone in ZoneActivation.ZonesAround(SavedStart(save), 1)) nav.LoadZone(zone).Wait();

        data.Layouts = layouts;
        data.Sight = new Meitou.Simulation.Perception.BuildingSight(install, db, levels, layouts);
        return nav;
    }

    /// <summary>
    /// The world on one thread with paths found at once, the save loaded into it and the camera focus on the player's squad leader.
    /// </summary>
    static World MakeWorld(PopulationData data, IWalkability walk, SaveGame save)
    {
        var options = new StandardSystemOptions
        {
            SynchronousPaths = true,
            Dialogue = new Meitou.Simulation.Dialogue.DialogueSettings { AmbientEvents = true },
            Population = new PopulationSettings { Background = false },
        };
        var world = new World(new WorldSettings { Seed = 1, Threads = 1, PublishSnapshots = false }, walk, StandardSystems.Build(data, walk, options).Systems);
        if (walk is Meitou.Simulation.Locks.IDoorGate doors) world.Fixtures.Doors = doors;

        var loaded = SaveLoader.Load(world, save, data, new SaveLoadOptions { Appearances = data.Appearances });
        if (LeaderSlot(world) is null) WarnNoLeader(loaded);

        var focus = LeaderSlot(world) is { } slot ? world.Characters.Previous[slot].Position : SavedStart(save);
        world.Commands.Enqueue(new FocusCommand(focus) { Tick = world.Tick });
        return world;
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
    /// Says the player's squad has no characters and why: the load's notes name the ones it left out.
    /// </summary>
    static void WarnNoLeader(LoadedSave loaded)
    {
        Console.Error.WriteLine("probe     the player's squad has no characters; centring on where the save put the first one");
        foreach (var note in loaded.Notes) Console.Error.WriteLine("  " + note);
    }

    /// <summary>
    /// Runs <paramref name="ticks"/> ticks.
    /// </summary>
    public void Tick(int ticks)
    {
        for (int k = 0; k < ticks; k++) World.RunTick();
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
    /// Disposes the world.
    /// </summary>
    public void Dispose() => World.Dispose();
}
