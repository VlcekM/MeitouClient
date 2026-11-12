using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Save;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using static Meitou.Tests.Bodies.BodyFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Save;

/// <summary>
/// The refit of the original game's two outposts (docs/game/buildings-production.md "Outposts"), read from its own save: the original (GOG 1.0.68,
/// save Beep Fan Club 1, after it dismantled Goonrock's Animal Feeder and saved) ended with Goonrock at (-17121.15, -9089.42) radius 2545.56 and
/// Drycoom at (82078.01, -77670.35) radius 2846.43.
/// </summary>
public class OriginalTownRefitTests
{
    const string Player = "204-gamedata.base";

    /// <summary>
    /// The first of the original's saves that holds both outposts, or null (the saves are the user's, not the repo's).
    /// </summary>
    static string? FindSave()
    {
        if (InstallData.Install is not { } install) return null;

        return new[] { "Beep Fan Club 1", "quicksave", "emergency_save_0" }.Select(n => Path.Combine(install.Root, "save", n)).FirstOrDefault(Directory.Exists);
    }

    /// <summary>
    /// A refit on open ground (no snap) puts each centre within 30 units of the original's (the formula's centre: its remaining gap is the original
    /// moving it off a wall footprint or a cliff, see the docs) and each radius within 30 of the original's; Wind Generator II 1099 outside Goonrock,
    /// whose saved town is NO_TOWN, is not counted (counting it gives about 2760).
    /// </summary>
    [Fact, Slow]
    public void Refitting_the_originals_two_outposts_gives_its_radii_and_centres_within_the_known_gap()
    {
        Assert.SkipWhen(InstallData.BaseGame is null || InstallData.Levels is null || FindSave() is null, "the original's save of the two outposts was not found");

        var db = InstallData.BaseGame!;
        var save = SaveGame.Load(FindSave()!);
        var construction = new ConstructionSystem(db, BaseGame, BodyOptions.Default, new ItemFactory(db, BaseGame), [], Player, new ConstructionSettings());
        // The towns of the level data: a building with no town handle takes the nearest town of any owner.
        var data = PopulationData.Create(db, InstallData.Levels!.Towns());
        using var world = new SimWorld(new WorldSettings { Seed = 1, PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 300),
            [new PopulationSystem(data), construction]) { Player = { Faction = 2 } };
        BuildingSave.Read(save, world, db, Player, 2, data);
        var goonrock = world.Buildings.Towns.Single(t => t.Name.Trim() == "Goonrock");
        var drycoom = world.Buildings.Towns.Single(t => t.Name.Trim() == "Drycoom");

        ConstructionSystem.ReassessTown(world, goonrock);

        Assert.True(Vector2.Distance(new Vector2(goonrock.Position.X, goonrock.Position.Z), new Vector2(-17121.15f, -9089.42f)) < 30);
        Assert.True(Vector2.Distance(new Vector2(drycoom.Position.X, drycoom.Position.Z), new Vector2(82078.01f, -77670.35f)) < 30);
        Assert.InRange(goonrock.Radius, 2545.56f - 30, 2545.56f + 30);
        Assert.InRange(drycoom.Radius, 2846.43f - 30, 2846.43f + 30);
    }
}
