using HarmonyLib;
using Meitou.Content;
using Meitou.Data.Save;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Tests.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Modding;

/// <summary>
/// The code-mod contract (docs/modding.md): shared assemblies, system anchors, save sidecars, determinism with a mod system.
/// </summary>
public class ModdingTests
{
    /// <summary>
    /// A mod system with state of its own: counts ticks and the characters it saw, hashed and saved.
    /// </summary>
    sealed class CountingSystem : ITickSystem, IStateHashed
    {
        public long Seen;

        public void SlowWorld(SimWorld world) => Seen += world.Characters.Count + 1;

        public void Hash(ref StateHasher hasher) => hasher.Add(Seen);
    }

    sealed class Probe(string name) : ITickSystem
    {
        public override string ToString() => name;
    }

    enum Pace { Slow, Fast }

    /// <summary>
    /// A mod's settings object: one property of each kind, and one that is not a setting.
    /// </summary>
    sealed class OreSettings
    {
        [Setting("Ore per deposit", Hint = "How much a rock holds.", Min = 10, Max = 2000, Step = 10)]
        public int Capacity { get; set; } = 100;

        [Setting("Refill days", Min = 0.5, Max = 60)]
        public double RefillDays { get; set; } = 10;

        [Setting("Natural rocks")]
        public bool Natural { get; set; } = true;

        [Setting("When the last one falls", Options = ["ask", "respawn"])]
        public string WhenFallen { get; set; } = "ask";

        [Setting("Pace")]
        public Pace Pace { get; set; }

        public int NotShown { get; set; } = 7;
    }

    sealed class NoRange
    {
        [Setting("Size")]
        public int Size { get; set; }
    }

    sealed class WrongType
    {
        [Setting("Names")]
        public List<string> Names { get; set; } = [];
    }

    sealed class NoChoice
    {
        [Setting("Mode", Options = ["a", "b"])]
        public string Mode { get; set; } = null!;
    }

    enum Side { Left = 1, Right = 2 }

    sealed class NoZeroSide
    {
        [Setting("Side")]
        public Side Side { get; set; }
    }

    struct StructSettings
    {
        public StructSettings() { }

        [Setting("On")]
        public bool On { get; set; } = true;
    }

    static ModContext Context(IReadOnlyDictionary<string, string>? saved = null) =>
        new(new LoadedMod(new ModInfo("Ores", "mods/Ores/Ores.mod", ModSource.Local),
                new CodeModManifest("ores", ModApi.Version, ["Ores.dll"], "Ores.Mod")),
            new ModRegistry(), null!, null!, () => null, saved);

    /// <summary>
    /// A mod folder's copies of game assemblies are never loaded: the mod gets the game's (one Harmony, one set of engine types).
    /// </summary>
    [Fact]
    public void A_mod_context_takes_the_game_assemblies_even_when_the_mod_folder_has_copies()
    {
        var folder = Directory.CreateTempSubdirectory("meitou-mod-").FullName;

        try
        {
            var harmony = typeof(Harmony).Assembly;
            var simulation = typeof(SimWorld).Assembly;
            File.Copy(harmony.Location, Path.Combine(folder, Path.GetFileName(harmony.Location)));
            File.Copy(simulation.Location, Path.Combine(folder, Path.GetFileName(simulation.Location)));
            var context = new ModLoadContext(folder, "test");

            Assert.Same(harmony, context.LoadFromAssemblyName(harmony.GetName()));
            Assert.Same(simulation, context.LoadFromAssemblyName(simulation.GetName()));
            Assert.False(ModLoadContext.IsShipped("SomeModsOwnLibrary"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Extension systems land at their anchors; the standard systems keep their order around them.
    /// </summary>
    [Fact]
    public void Extension_systems_are_spliced_at_their_anchors()
    {
        var data = SyntheticTown.Data(SyntheticTown.Database());
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var plain = StandardSystems.Build(data, walk, new StandardSystemOptions { SynchronousPaths = true }).Systems;
        var extensions = new SimulationExtensions();
        extensions.Systems.Add(new SystemFactory("m", "after", SystemAnchor.AfterPart(StandardParts.Combat), _ => new Probe("after combat")));
        extensions.Systems.Add(new SystemFactory("m", "first", SystemAnchor.Before(StandardParts.Population), _ => new Probe("first")));
        extensions.Systems.Add(new SystemFactory("m", "left out", SystemAnchor.End, _ => null));

        var options = new StandardSystemOptions { SynchronousPaths = true, Extensions = extensions };
        var spliced = StandardSystems.Build(data, walk, options).Systems.ToList();

        Assert.Equal(plain.Select(s => s.GetType()), spliced.Where(s => s is not Probe).Select(s => s.GetType()));
        Assert.Equal("first", spliced[0].ToString());
        Assert.Equal("after combat", spliced[spliced.FindIndex(s => s is CombatSystem) + 1].ToString());
        Assert.Equal(plain.Count + 2, spliced.Count);
    }

    /// <summary>
    /// Mod sidecars go into meitou-mods/ with the save, come back on load and copy with a clone, so later saves keep them.
    /// </summary>
    [Fact]
    public void Mod_data_round_trips_through_a_save_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "meitou-mod-save-" + Guid.NewGuid().ToString("N"));

        try
        {
            var game = new SaveBuilder().Game;
            game.ModData["some.mod"] = [1, 2, 3];
            game.ModData["other-mod"] = [];
            game.Write(folder);

            var loaded = SaveGame.Load(folder);

            Assert.True(File.Exists(Path.Combine(folder, SaveFolder.ModDataDirectory, "some.mod.dat")));
            Assert.Equal(game.ModData, loaded.ModData);
            Assert.Equal(game.ModData, loaded.Clone().ModData);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// A hashed mod system is part of the world's state hash, which agrees at 1 and 4 threads.
    /// </summary>
    [Fact]
    public void A_mod_system_keeps_the_world_deterministic_and_hashed()
    {
        var data = SyntheticTown.Data(SyntheticTown.Database());
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var extensions = new SimulationExtensions();
        extensions.Systems.Add(new SystemFactory("m", "count", SystemAnchor.AfterPart(StandardParts.Movement), _ => new CountingSystem()));

        ulong Run(int threads, SimulationExtensions? with)
        {
            var options = new StandardSystemOptions
            {
                Parts = StandardParts.Population | StandardParts.Movement, SynchronousPaths = true, Extensions = with,
            };
            using var world = new SimWorld(new WorldSettings { Seed = 5, Threads = threads, MinPartitionSize = 4, PublishSnapshots = false },
                walk, StandardSystems.Build(data, walk, options).Systems);
            world.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = 0 });
            world.RunTicks(60);
            return world.StateHash();
        }

        ulong one = Run(1, extensions);

        Assert.Equal(one, Run(4, extensions));
        Assert.NotEqual(Run(1, null), one);
    }

    /// <summary>
    /// A registered object's marked properties become settings in declaration order: kind, range, step, choices and default from the attribute and
    /// the value.
    /// </summary>
    [Fact]
    public void Registered_settings_describe_their_properties()
    {
        var group = Context().AddSettings(new OreSettings());

        Assert.Equal(["Capacity", "RefillDays", "Natural", "WhenFallen", "Pace"], group.Settings.Select(s => s.Key));
        var capacity = group.Settings[0];
        var described = (capacity.Kind, capacity.Min, capacity.Max, capacity.Step, capacity.Default);
        Assert.Equal((ModSettingKind.Slider, 10.0, 2000.0, 10.0, (object)100), described);
        Assert.Equal("How much a rock holds.", capacity.Hint);
        Assert.Equal(59.5 / 100, group.Settings[1].Step, 10);
        Assert.Equal(ModSettingKind.Switch, group.Settings[2].Kind);
        Assert.Equal(["ask", "respawn"], group.Settings[3].Options);
        Assert.Equal(["Slow", "Fast"], group.Settings[4].Options);
    }

    /// <summary>
    /// Reset puts back the default as registered, though it is off the step grid (10 on 0.5 + n x 0.595), and a saved number that is not finite is
    /// ignored. Catches a Reset that snaps to the step (10.02, the row left changed) and a "NaN" let past the range's clamp.
    /// </summary>
    [Fact]
    public void Reset_restores_an_off_step_default_and_a_saved_nan_is_ignored()
    {
        var ores = new OreSettings();
        var group = Context(new Dictionary<string, string> { ["RefillDays"] = "NaN" }).AddSettings(ores);
        var refill = group.Settings[1];
        Assert.Equal(10.0, ores.RefillDays);

        refill.Set(20.0);
        Assert.False(refill.IsDefault);
        refill.Reset();
        Assert.Equal(10.0, ores.RefillDays);
        Assert.True(refill.IsDefault);
    }

    /// <summary>
    /// A setting the page cannot show refuses the mod with a reason that names it, rather than failing later.
    /// </summary>
    [Fact]
    public void A_bad_setting_is_refused_with_a_reason()
    {
        Assert.Contains("'Size'", Assert.Throws<ArgumentException>(() => Context().AddSettings(new NoRange())).Message);
        Assert.Contains("'Names'", Assert.Throws<ArgumentException>(() => Context().AddSettings(new WrongType())).Message);
        Assert.Throws<ArgumentException>(() => Context().AddSettings(new object()));
        Assert.Contains("'Mode'", Assert.Throws<ArgumentException>(() => Context().AddSettings(new NoChoice())).Message);
        Assert.Contains("'Side'", Assert.Throws<ArgumentException>(() => Context().AddSettings(new NoZeroSide())).Message);
        Assert.Contains("struct", Assert.Throws<ArgumentException>(() => Context().AddSettings(new StructSettings())).Message);
        var context = Context();
        context.AddSettings(new OreSettings());
        Assert.Throws<InvalidOperationException>(() => context.AddSettings(new OreSettings()));
    }
}
