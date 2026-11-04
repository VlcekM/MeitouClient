using HarmonyLib;
using Meitou.Data.Save;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Tests.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Modding;

/// <summary>The code-mod contract (docs/modding.md): shared assemblies, system anchors, save sidecars, determinism with a mod system.</summary>
public class ModdingTests
{
    /// <summary>A mod system with state of its own: counts ticks and the characters it saw, hashed and saved.</summary>
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

    /// <summary>A mod folder's copies of game assemblies are never loaded: the mod gets the game's (one Harmony, one set of engine types).</summary>
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

    /// <summary>Extension systems land at their anchors; the standard systems keep their order around them.</summary>
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

    /// <summary>Mod sidecars go into meitou-mods/ with the save, come back on load and copy with a clone, so later saves keep them.</summary>
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

    /// <summary>A hashed mod system is part of the world's state hash, which agrees at 1 and 4 threads.</summary>
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
}
