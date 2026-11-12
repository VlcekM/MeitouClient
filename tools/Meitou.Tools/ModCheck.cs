using System.Diagnostics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;

/// <summary>
/// <c>meitou-tools mod-check</c> (docs/modding.md "Determinism check"): loads a save headless with code mods (the enabled ones, or the folders given
/// with <c>--mod</c>) and runs it three times, twice on one thread and once on several, comparing the state hash after every tick. A difference
/// between the one-thread runs points at randomness or the clock, one between thread counts at a threading mistake. At the first differing tick it
/// names the hashed systems that differ (a mod's with its id) and, on a difference, runs the game without mods to tell whether the game itself
/// diverges there.
/// </summary>
static class ModCheck
{
    /// <summary>
    /// The command line: the save folder, how many ticks to run, the thread count of the parallel run and the mod folders (none: the enabled code
    /// mods).
    /// </summary>
    sealed record Options(string Save, int Ticks, int Threads, IReadOnlyList<string> ModFolders);

    /// <summary>
    /// Per tick: the world's hash and each hashed system's, by label.
    /// </summary>
    sealed record Trace(List<ulong> World, List<Dictionary<string, ulong>> Systems)
    {
        /// <summary>
        /// The run's character count at the end (said with the report).
        /// </summary>
        public int Characters { get; set; }
    }

    /// <summary>
    /// What every run shares: the game data, the population data read from it and the walkability.
    /// </summary>
    sealed record Setup(GameDatabase Db, PopulationData Data, IWalkability Walk);

    /// <summary>
    /// The world being run, for the mods' Enqueue and Tick.
    /// </summary>
    static World? s_current;

    /// <summary>
    /// <c>meitou-tools mod-check</c>: loads the setup and the mods, runs the three plays and compares them. Returns 0 when the runs agree, 1 when
    /// they differ or no mod loads, 2 for a bad command line or a save without a quicksave.
    /// </summary>
    public static int Run(GameInstall install, string[] args)
    {
        if (ParseOptions(args) is not { } options) return Usage();
        if (!File.Exists(Path.Combine(options.Save, SaveFolder.QuickSaveName)))
        {
            Console.Error.WriteLine($"no {SaveFolder.QuickSaveName} in {options.Save}");
            return 2;
        }
        var setup = LoadSetup(install);
        var mods = LoadMods(CodeMods(install, options), setup);
        if (mods is null) return 1;
        return Compare(options, setup, mods);
    }

    /// <summary>
    /// Prints the command line and returns 2.
    /// </summary>
    static int Usage()
    {
        Console.Error.WriteLine("meitou-tools mod-check <save folder> [--ticks n] [--threads n] [--mod <mod folder>]...");
        Console.Error.WriteLine("    defaults: 600 ticks, 4 threads, the enabled code mods");
        return 2;
    }

    /// <summary>
    /// The options from the command line (the save folder first), null when they are wrong.
    /// </summary>
    static Options? ParseOptions(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal)) return null;
        int ticks = 600, threads = 4;
        var folders = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            bool hasValue = i + 1 < args.Length;
            if (args[i] == "--ticks" && hasValue && int.TryParse(args[++i], out int t) && t > 0) ticks = t;
            else if (args[i] == "--threads" && hasValue && int.TryParse(args[++i], out int n) && n > 1) threads = n;
            else if (args[i] == "--mod" && hasValue) folders.Add(Path.GetFullPath(args[++i]));
            else return null;
        }
        return new Options(Path.GetFullPath(args[0]), ticks, threads, folders);
    }

    /// <summary>
    /// The merged data and the population data of the world, walking the terrain (no navmesh: its zones load on threads, no run repeats them).
    /// </summary>
    static Setup LoadSetup(GameInstall install)
    {
        var watch = Stopwatch.StartNew();
        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
        var levels = WorldLevelData.Load(install, includeInteriors: true);
        var map = TerrainHeightmap.Open(install);
        var gate = new Lock();
        Func<float, float, float> height = (x, z) => { lock (gate) return map.HeightAt(x, z); };
        var layouts = new BuildingLayouts(db, levels.Interiors);
        var buildings = TownBuildings.Build(db, levels, layouts, (x, z) => height((float)x, (float)z));
        var biomes = Meitou.Data.Gameplay.BiomeMap.Create(db, WeatherAreas.Load(install));
        var appearances = new GeneratedAppearances(db, install.Root);
        var data = PopulationData.Create(db, levels.Towns().ToList(), appearances, biomes: biomes, buildings: buildings,
            roads: levels.Roads(), townZones: levels.TownZones(), level: levels.Level);
        Console.WriteLine($"data      loaded in {watch.ElapsedMilliseconds} ms");
        return new Setup(db, data, new OpenGroundWalkability(height));
    }

    /// <summary>
    /// The <c>--mod</c> folders (their code only: their records are not in the merged data unless the mod is enabled), else every enabled code mod.
    /// </summary>
    static List<ModInfo> CodeMods(GameInstall install, Options options)
    {
        if (options.ModFolders.Count == 0) return ModHost.EnabledCodeMods(install);
        var mods = options.ModFolders.Select(f => new ModInfo(Path.GetFileName(f), Path.Combine(f, Path.GetFileName(f) + ".mod"), ModSource.Local))
            .ToList();
        foreach (var missing in mods.Where(m => !m.HasCode))
            Console.Error.WriteLine($"mods      {missing.Folder} has no {ModList.CodeManifestFile}");
        return [.. mods.Where(m => m.HasCode)];
    }

    /// <summary>
    /// Loads the code mods into a host bound to the world being run; null (said on the console) when there is none or none loads.
    /// </summary>
    static ModHost? LoadMods(List<ModInfo> enabled, Setup setup)
    {
        if (enabled.Count == 0)
        {
            Console.Error.WriteLine("no code mods to check (none enabled in mods.cfg, or no --mod folder with a manifest)");
            return null;
        }
        Console.WriteLine($"mods      running the code of {string.Join(", ", enabled.Select(m => m.Name))}");
        var mods = ModHost.Load(new ModHostOptions(enabled, setup.Db, setup.Data, () => s_current));
        foreach (var notice in mods.Notices) Console.Error.WriteLine($"mods      {notice}");
        return mods.Mods.Count > 0 ? mods : null;
    }

    /// <summary>
    /// Runs the save twice on one thread and once on <see cref="Options.Threads"/>, compares them and, on a difference, runs it without mods too.
    /// Returns 0 when the runs agree, else 1.
    /// </summary>
    static int Compare(Options options, Setup setup, ModHost mods)
    {
        var first = Play(options, setup, mods, threads: 1);
        var again = Play(options, setup, mods, threads: 1);
        var parallel = Play(options, setup, mods, options.Threads);
        ReportWorldSize(first.Characters);
        bool repeats = Report("one thread, twice", first, again);
        bool threads = Report($"1 against {options.Threads} threads", first, parallel);
        if (repeats && threads)
        {
            Console.WriteLine($"PASS      {options.Ticks} ticks agree run to run and across thread counts");
            return 0;
        }
        CheckGameAlone(options, setup, !repeats);
        return 1;
    }

    /// <summary>
    /// The parallel phases cut the slots into partitions of at least 32: under 64 characters they run as one partition.
    /// </summary>
    static void ReportWorldSize(int characters)
    {
        Console.WriteLine($"world     {characters} characters at the end of a run");
        if (characters < 64)
        {
            Console.WriteLine("warning   under 64 characters the parallel phases run as one partition: thread mistakes cannot show; "
                + "use a busier save");
        }
    }

    /// <summary>
    /// On a difference: the same comparison without mods. If the game alone differs too, the save shows a game fault, not only a mod's.
    /// </summary>
    static void CheckGameAlone(Options options, Setup setup, bool runToRun)
    {
        Console.WriteLine("baseline  the same runs without mods...");
        var first = Play(options, setup, null, threads: 1);
        var second = Play(options, setup, null, runToRun ? 1 : options.Threads);
        int tick = FirstDifference(first.World, second.World);
        string verdict = tick < 0 ? "the game alone agrees: the difference comes with the mods"
            : $"the game alone differs too, from tick {tick + 1}: report it as a Meitou bug with this save";
        Console.WriteLine($"baseline  {verdict}");
    }

    /// <summary>
    /// Loads the save into a new world (synchronous paths, no background population, seed 1, no snapshots), focuses it on the player squad and
    /// records the ticks.
    /// </summary>
    static Trace Play(Options options, Setup setup, ModHost? mods, int threads)
    {
        var save = SaveGame.Load(SaveFolder.Open(options.Save));
        var walk = setup.Walk;
        var systems = StandardSystems.Build(setup.Data, walk, new StandardSystemOptions
        {
            SynchronousPaths = true, Extensions = mods?.Simulation, Population = new PopulationSettings { Background = false },
        }).Systems;
        var settings = new WorldSettings { Seed = 1, Threads = threads, MinPartitionSize = 32, PublishSnapshots = false };
        using var world = new World(settings, walk, systems);
        s_current = world;
        SaveLoader.Load(world, save, setup.Data, new SaveLoadOptions { Appearances = setup.Data.Appearances });
        mods?.RestoreSaveData(world, save.ModData);
        Focus(world);
        try
        {
            return Record(world, options.Ticks, mods);
        }
        finally
        {
            s_current = null;
        }
    }

    /// <summary>
    /// Puts the camera focus on the player squad's leader, so the zones around it load as in the game.
    /// </summary>
    static void Focus(World world)
    {
        if (world.Squads.Find(world.Player.Squad) is not { Leader.IsNone: false } squad) return;
        var at = world.Characters.Previous[squad.Leader.Slot].Position;
        world.Commands.Enqueue(new FocusCommand(at) { Tick = world.Tick });
    }

    /// <summary>
    /// Runs <paramref name="ticks"/> ticks and records the world's hash and each hashed system's after every one.
    /// </summary>
    static Trace Record(World world, int ticks, ModHost? mods)
    {
        var trace = new Trace([], []);
        for (int i = 0; i < ticks; i++)
        {
            world.RunTick();
            trace.World.Add(world.StateHash());
            trace.Systems.Add(SystemHashes(world, mods));
        }
        trace.Characters = world.Characters.Count;
        return trace;
    }

    /// <summary>
    /// Each hashed system's state hash, by its index and label.
    /// </summary>
    static Dictionary<string, ulong> SystemHashes(World world, ModHost? mods)
    {
        var hashes = new Dictionary<string, ulong>(StringComparer.Ordinal);
        for (int k = 0; k < world.Systems.Count; k++)
        {
            if (world.Systems[k] is not IStateHashed hashed) continue;
            var h = new StateHasher();
            hashed.Hash(ref h);
            hashes[$"{k}:{Label(world.Systems[k], mods)}"] = h.Value;
        }
        return hashes;
    }

    /// <summary>
    /// A system's label: the owning mod's id and the system's name, else the system's type name.
    /// </summary>
    static string Label(ITickSystem system, ModHost? mods) =>
        mods?.OwnerOf(system) is { } owner ? $"mod {owner.ModId} system '{owner.Name}'" : system.GetType().Name;

    /// <summary>
    /// Prints the first differing tick and the hashed systems that differ there; true when the runs agree.
    /// </summary>
    static bool Report(string what, Trace a, Trace b)
    {
        int tick = FirstDifference(a.World, b.World);
        if (tick < 0)
        {
            Console.WriteLine($"ok        {what}: every tick agrees");
            return true;
        }
        Console.WriteLine($"DIFFERS   {what}: from tick {tick + 1}");
        var systems = a.Systems[tick].Where(p => b.Systems[tick].GetValueOrDefault(p.Key) != p.Value)
            .Select(p => p.Key[(p.Key.IndexOf(':') + 1)..]).ToList();
        if (systems.Count == 0)
            Console.WriteLine("          no hashed system differs: the world's own state does (characters, squads...); look at patches and writes");
        foreach (var system in systems) Console.WriteLine($"          {system}");
        return false;
    }

    /// <summary>
    /// The first index at which the two hash lists differ, -1 when they agree over the shorter.
    /// </summary>
    static int FirstDifference(List<ulong> a, List<ulong> b)
    {
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
            if (a[i] != b[i]) return i;
        return -1;
    }
}
