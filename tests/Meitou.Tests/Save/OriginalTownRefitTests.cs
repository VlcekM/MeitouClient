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
    /// The first of the original's saves that loads and holds the town states of both outposts, or null (the saves are the user's, not the
    /// repo's: a quicksave without Goonrock and Drycoom is skipped, not failed). Found once.
    /// </summary>
    static string? FindSave() => Found.Value;

    static readonly Lazy<string?> Found = new(Search);

    /// <summary>
    /// The search behind <see cref="FindSave"/>.
    /// </summary>
    static string? Search()
    {
        if (InstallData.Install is not { } install) return null;

        foreach (string name in new[] { "Beep Fan Club 1", "quicksave", "emergency_save_0" })
        {
            string path = Path.Combine(install.Root, "save", name);
            if (Directory.Exists(path) && HoldsBothOutposts(path)) return path;
        }

        return null;
    }

    /// <summary>
    /// Whether the save at <paramref name="path"/> loads and names Goonrock and Drycoom in its records.
    /// </summary>
    static bool HoldsBothOutposts(string path)
    {
        try
        {
            var records = SaveGame.Load(path).Quick.Records;
            return records.Any(r => r.Name.Contains("Goonrock", StringComparison.Ordinal))
                && records.Any(r => r.Name.Contains("Drycoom", StringComparison.Ordinal));
        }
        catch (Exception)
        {
            return false;
        }
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
    /// <summary>
    /// The original (GOG 1.0.68, parity round 6: the same save loaded, the Drycoom Longhouse dismantled, saved) refitted Drycoom to
    /// (82004.72, -77760.71) radius 2907.50. Without the Longhouse the centre is the plain weighted mean of the remaining houses and the gate: the
    /// formula matches the original within one unit when that point is not inside a wall footprint (it is 0.8 units off).
    /// </summary>
    [Fact, Slow]
    public void Refitting_drycoom_without_its_longhouse_matches_the_originals_centre_within_one_unit()
    {
        Assert.SkipWhen(InstallData.BaseGame is null || InstallData.Levels is null || FindSave() is null, "the original's save of the two outposts was not found");

        var db = InstallData.BaseGame!;
        var save = SaveGame.Load(FindSave()!);
        var construction = new ConstructionSystem(db, BaseGame, BodyOptions.Default, new ItemFactory(db, BaseGame), [], Player, new ConstructionSettings());
        var data = PopulationData.Create(db, InstallData.Levels!.Towns());
        using var world = new SimWorld(new WorldSettings { Seed = 1, PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 300),
            [new PopulationSystem(data), construction]) { Player = { Faction = 2 } };
        BuildingSave.Read(save, world, db, Player, 2, data);
        var drycoom = world.Buildings.Towns.Single(t => t.Name.Trim() == "Drycoom");
        world.Buildings.Remove(world.Buildings.All.Single(s => s.Id == "96-zone49.15-INGAME"));

        ConstructionSystem.ReassessTown(world, drycoom);

        Assert.True(Vector2.Distance(new Vector2(drycoom.Position.X, drycoom.Position.Z), new Vector2(82004.72f, -77760.71f)) < 1.5f);
        Assert.InRange(drycoom.Radius, 2907.50f - 1, 2907.50f + 1);
    }

    /// <summary>
    /// A refit on the original's own saved navmesh tiles (the tiles it regenerated with the player's buildings in them, <c>zone/tileX.Y.hkt</c> of the
    /// save) with the two-step 3D snap (docs/game/buildings-production.md "Round 8"): the 4-building Drycoom 1.39 off the original's centre (the
    /// clearance step moves it 22 units inward), the 3-building Drycoom 2.01 off (0.78 on the formula alone: the 3D step slides it 2.2 down the slope
    /// from the members' mean height), Goonrock 9.90 off (formula alone 20.3).
    /// </summary>
    [Fact, Slow]
    public void Refitting_on_the_originalFoundd_tiles_snaps_the_centres_to_within_the_known_residuals()
    {
        Assert.SkipWhen(InstallData.BaseGame is null || InstallData.Levels is null || FindSave() is null, "the original's save of the two outposts was not found");
        string save = FindSave()!;
        Assert.SkipWhen(!File.Exists(Path.Combine(save, "zone", "tile49.15.hkt")) || !File.Exists(Path.Combine(save, "zone", "tile28.30.hkt")),
            "the save has no saved navmesh tiles of the two outposts");

        var db = InstallData.BaseGame!;
        var construction = new ConstructionSystem(db, BaseGame, BodyOptions.Default, new ItemFactory(db, BaseGame), [], Player, new ConstructionSettings());
        var data = PopulationData.Create(db, InstallData.Levels!.Towns());
        var walk = new SavedTileWalkability((_, _) => 300, save, (49, 15), (28, 30));
        using var world = new SimWorld(new WorldSettings { Seed = 1, PublishSnapshots = false }, walk, [new PopulationSystem(data), construction]) { Player = { Faction = 2 } };
        BuildingSave.Read(SaveGame.Load(save), world, db, Player, 2, data);
        var goonrock = world.Buildings.Towns.Single(t => t.Name.Trim() == "Goonrock");
        var drycoom = world.Buildings.Towns.Single(t => t.Name.Trim() == "Drycoom");

        ConstructionSystem.ReassessTown(world, goonrock);
        float goonrockOff = Off(goonrock, -17121.15f, -9089.42f), drycoomOff = Off(drycoom, 82078.01f, -77670.35f);
        world.Buildings.Remove(world.Buildings.All.Single(s => s.Id == "96-zone49.15-INGAME"));
        ConstructionSystem.ReassessTown(world, drycoom);
        float withoutLonghouseOff = Off(drycoom, 82004.72f, -77760.71f);

        Assert.True(drycoomOff < 1.5f, $"Drycoom, 4 buildings: {drycoomOff}");
        Assert.True(withoutLonghouseOff < 2.1f, $"Drycoom, 3 buildings: {withoutLonghouseOff}");
        Assert.True(goonrockOff < 10, $"Goonrock: {goonrockOff}");
    }

    static float Off(PlayerTown town, float x, float z) => Vector2.Distance(new Vector2(town.Position.X, town.Position.Z), new Vector2(x, z));
}
