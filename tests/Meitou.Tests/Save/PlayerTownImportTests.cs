using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Saving;
using Meitou.Simulation.Population;
using Meitou.Tests.Simulation;
using Xunit;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Save;

/// <summary>Player-town save import rules (docs/formats/save.md "Towns").</summary>
public class PlayerTownImportTests
{
    const string Player = "204-gamedata.base";
    const string Foreign = "20-t";

    static SimWorld World() => new(new WorldSettings { Seed = 1, PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 300));

    static BuildSite Site(string id, Vector3 position, int town) => new()
    {
        Id = id,
        Record = "building",
        Position = position,
        Town = town,
        Owner = 7,
        Materials = [],
        Target = 1,
        Rate = 1,
        Complete = true,
    };

    static (SaveGame Save, FcsRecord InstanceList) SaveWithInstances(params (string Id, Vector3 Position, SaveTown[] States)[] towns)
    {
        var builder = new SaveBuilder(7);
        var list = new FcsRecord { Type = (int)FcsRecordType.GamestateTownInstanceList, StringId = "towns" };
        foreach (var (id, position, states) in towns)
        {
            var instance = new FcsInstance { Id = id, Target = PlayerTown.Template, Position = position };
            instance.States.AddRange(states.Select(s => s.Record.StringId));
            list.Instances.Add(instance);
        }
        builder.Game.TownInstanceList = list;
        return (builder.Game, list);
    }

    static SaveTown Town(SaveBuilder builder, string name, string instance, string faction, Hand handle, bool publicTown = true, int? overrideRadius = null)
    {
        var town = builder.AddTown(name, instance, faction, isNest: false, zone: null, hand: handle);
        town.Record.Bools["public"] = publicTown;
        if (overrideRadius is { } radius) town.Record.Ints["override"] = radius;
        return town;
    }

    /// <summary>
    /// A resolved NPC-town handle stays valid for research; missing and normalized-away player-town handles stay unresolved across saves.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Building_town_resolution_distinguishes_NPC_houses_from_missing_towns(bool unresolved, bool removedOutpost)
    {
        var builder = new SaveBuilder(7);
        var handle = new Hand((int)FcsRecordType.Town, 4, 55, 2, 9);


        var record = new FcsRecord { StringId = "building", RecordType = FcsRecordType.Building };
        var db = new GameDatabase();
        var file = new FcsFile();
        file.Records.Add(record);
        db.Apply(file, "test.mod");
        using (var source = World())
        {
            source.Buildings.Add(Site("1-zone16.16-INGAME", Vector3.Zero, 0));
            BuildingSave.Write(builder.Game, source, Player);
        }
        if (!unresolved) Town(builder, "Town state NPC", "npc", Foreign, handle);
        if (removedOutpost)
        {
            var first = Town(builder, "Town state Removed", "removed", Player, handle);
            var second = Town(builder, "Town state Survivor", "survivor", Player, new Hand((int)FcsRecordType.Town, 5, 56, 2, 10));
            var (_, list) = SaveWithInstances(("removed", Vector3.Zero, [first]), ("survivor", new Vector3(100, 300, 0), [second]));
            builder.Game.TownInstanceList = list;
        }
        var state = Assert.Single(builder.Game.Zones.Values).BuildingStates.Single();
        handle.Write(state, "town");
        using var world = World();
        BuildingSave.Read(builder.Game, world, db, Player, 7);
        Assert.Equal(unresolved, Assert.Single(world.Buildings.All).UnresolvedTown);

        BuildingSave.Write(builder.Game, world, Player);
        using var reloaded = World();
        BuildingSave.Read(builder.Game, reloaded, db, Player, 7);
        Assert.Equal(unresolved, Assert.Single(reloaded.Buildings.All).UnresolvedTown);
    }

    [Fact]
    public void A_template_town_without_a_faction_is_imported_with_its_saved_properties()
    {
        // Catches the original faction-key requirement that dropped ordinary player outposts.
        var builder = new SaveBuilder(7);
        var state = Town(builder, "Town state Ridge Camp", "outpost", Player, new Hand((int)FcsRecordType.Town, 4, 55, 2, 9), false, 1800);
        state.Record.Strings.Remove("faction");
        var list = new FcsRecord { Type = (int)FcsRecordType.GamestateTownInstanceList, StringId = "towns" };
        list.Instances.Add(new FcsInstance { Id = "outpost", Target = PlayerTown.Template, Position = new Vector3(100, 300, 200), States = { state.Record.StringId } });
        builder.Game.TownInstanceList = list;

        using var world = World();
        BuildingSave.Read(builder.Game, world, new GameDatabase(), Player, 7);
        var town = Assert.Single(world.Buildings.Towns);
        Assert.Equal((1, "Ridge Camp", 7, false, 1800f, new Hand((int)FcsRecordType.Town, 4, 55, 2, 9)),
            (town.Number, town.Name, town.Owner, town.Public, town.Override, town.SaveHandle!.Value));
    }

    [Fact]
    public void A_template_town_with_a_foreign_faction_is_not_imported()
    {
        // Catches the unsafe target-only import that would turn a foreign town into a player outpost.
        var builder = new SaveBuilder(7);
        var state = Town(builder, "Town state Foreign Camp", "foreign", Foreign, new Hand((int)FcsRecordType.Town, 5, 56, 2, 10));
        var (_, list) = SaveWithInstances(("foreign", new Vector3(100, 300, 200), [state]));
        builder.Game.TownInstanceList = list;

        using var world = World();
        BuildingSave.Read(builder.Game, world, new GameDatabase(), Player, 7);
        Assert.Empty(world.Buildings.Towns);
    }

    /// <summary>
    /// A foreign runtime owner wins over missing or player ownership in another state, in both import passes and when pruning saves.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Mixed_town_ownership_never_imports_or_rewrites_a_foreign_town(bool includePopulation, bool explicitPlayer)
    {
        var builder = new SaveBuilder(7);
        var first = Town(builder, "Town state Ambiguous", "mixed", Player, new Hand(13, 4, 55, 2, 9));
        if (!explicitPlayer) first.Record.Strings.Remove("faction");
        var foreign = Town(builder, "Town state Foreign", "mixed", Foreign, new Hand(13, 5, 56, 2, 10));
        var (_, list) = SaveWithInstances(("mixed", new Vector3(100, 300, 200), [first]));
        builder.Game.TownInstanceList = list;
        var db = SyntheticTown.Database();
        var population = includePopulation ? PopulationData.Create(db, []) : null;
        using var world = World();
        BuildingSave.Read(builder.Game, world, db, Player, 7, population);
        Assert.Empty(world.Buildings.Towns);

        BuildingSave.Write(builder.Game, world, Player);
        Assert.Equal(Foreign, foreign.FactionId);
        Assert.Single(list.Instances);
        Assert.Contains(first.Record, builder.Game.Quick.Records);
        Assert.Contains(foreign.Record, builder.Game.Quick.Records);
        Assert.Equal(explicitPlayer ? Player : null, first.Record.Strings.GetValueOrDefault("faction"));
    }

    [Fact]
    public void Multiple_town_states_for_one_instance_are_merged()
    {
        // Catches reading only States[0] and creating one PlayerTown per state instead of per instance.
        var builder = new SaveBuilder(7);
        var first = Town(builder, "Town state First Camp", "merged", Player, Hand.Null, false);
        var second = Town(builder, "Town state ", "merged", Player, new Hand((int)FcsRecordType.Town, 6, 57, 3, 11), true, 2800);
        var (_, list) = SaveWithInstances(("merged", new Vector3(100, 300, 200), [first, second]));
        builder.Game.TownInstanceList = list;

        using var world = World();
        BuildingSave.Read(builder.Game, world, new GameDatabase(), Player, 7);
        var town = Assert.Single(world.Buildings.Towns);
        Assert.Equal("First Camp", town.Name);
        Assert.True(town.Public);
        Assert.Equal(2800, town.Override);
        Assert.Equal(new Hand((int)FcsRecordType.Town, 6, 57, 3, 11), town.SaveHandle);
    }

    /// <summary>
    /// All states linked by id or instance keep the live public/radius values after writing and both town-load passes.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Multiple_town_states_round_trip_the_live_public_flag_and_radius(bool includePopulation)
    {
        var builder = new SaveBuilder(7);
        var first = Town(builder, "Town state First Camp", "merged", Player, Hand.Null, false);
        var second = Town(builder, "Town state ", "merged", Player, new Hand(13, 6, 57, 3, 11), true, 2800);
        var (_, list) = SaveWithInstances(("merged", new Vector3(100, 300, 200), [first, second]));
        builder.Game.TownInstanceList = list;
        var third = Town(builder, "Town state ", "merged", Player, Hand.Null, true, 2800);
        var db = SyntheticTown.Database();
        var population = includePopulation ? PopulationData.Create(db, []) : null;
        using var world = World();
        BuildingSave.Read(builder.Game, world, db, Player, 7, population);
        var town = Assert.Single(world.Buildings.Towns);
        town.Public = false;
        town.Override = 3400;

        BuildingSave.Write(builder.Game, world, Player);
        using var reloaded = World();
        BuildingSave.Read(builder.Game, reloaded, db, Player, 7, population);
        var restored = Assert.Single(reloaded.Buildings.Towns);
        Assert.False(restored.Public);
        Assert.Equal(3400, restored.Override);
        Assert.All(new[] { first, second, third }, state =>
        {
            Assert.False(state.Record.Bools["public"]);
            Assert.Equal(3400, state.Record.Ints["override"]);
        });
    }

    [Fact]
    public void A_player_level_town_is_imported_without_a_town_instance_list()
    {
        // Catches losing a player outpost when the save has only a level-town state and PopulationData supplies its site.
        var db = SyntheticTown.Database();
        var data = PopulationData.Create(db, [new TownPlacement("level-outpost", "30-t", SyntheticTown.Centre, null)]);
        var builder = new SaveBuilder(7);
        var state = Town(builder, "Town state Level Camp", "level-outpost", Player, new Hand((int)FcsRecordType.Town, 8, 58, 4, 12), false, 1900);

        using var world = World();
        BuildingSave.Read(builder.Game, world, db, Player, 7, data);
        var town = Assert.Single(world.Buildings.Towns);
        Assert.Equal(("Level Camp", SyntheticTown.Centre, 7, false, 1900f), (town.Name, town.Position, town.Owner, town.Public, town.Override));
    }

    [Fact]
    public void Town_overlap_normalisation_is_strict_and_preserves_numbers()
    {
        // Catches non-strict edge removal and renumbering after overlap removal, including the next AddTown number.
        var builder = new SaveBuilder(7);
        var outer = Town(builder, "Town state Outer", "outer", Player, new Hand((int)FcsRecordType.Town, 10, 60, 1, 13), true, 2500);
        var inner = Town(builder, "Town state Inner", "inner", Player, new Hand((int)FcsRecordType.Town, 11, 61, 1, 14), true, 1100);
        var edge = Town(builder, "Town state Edge", "edge", Player, new Hand((int)FcsRecordType.Town, 12, 62, 1, 15), true, 2500);
        var (_, list) = SaveWithInstances(
            ("outer", new Vector3(0, 300, 0), [outer]),
            ("inner", new Vector3(1200, 300, 0), [inner]),
            ("edge", new Vector3(2500, 300, 0), [edge]));
        builder.Game.TownInstanceList = list;

        using var world = World();
        BuildingSave.Read(builder.Game, world, new GameDatabase(), Player, 7);
        Assert.Equal([1, 3], world.Buildings.Towns.Select(t => t.Number));
        Assert.Equal("Outer", world.Buildings.FindTown(1)!.Name);
        Assert.Equal("Edge", world.Buildings.FindTown(3)!.Name);
        var added = world.Buildings.AddTown(new Vector3(5000, 300, 0), 7);
        Assert.Equal(4, added.Number);
    }

    [Fact]
    public void Placement_overlap_merges_buildings_into_the_smaller_radius_town()
    {
        using var world = World();
        var first = world.Buildings.AddTown(new Vector3(0, 300, 0), 7);
        var second = world.Buildings.AddTown(new Vector3(1000, 300, 0), 7);
        first.Override = 1000;
        second.Override = 3000;
        world.Buildings.Add(Site("first", new Vector3(0, 300, 0), first.Number));
        world.Buildings.Add(Site("second", new Vector3(1000, 300, 0), second.Number));

        ConstructionSystem.ReassessTown(world, first);

        Assert.Same(first, Assert.Single(world.Buildings.Towns));
        Assert.All(world.Buildings.All, site => Assert.Equal(first.Number, site.Town));
        Assert.True(first.Override > 1000);
    }

    [Fact]
    public void Load_overlap_normalisation_does_not_rewrite_building_town_handles()
    {
        using var world = World();
        var removed = world.Buildings.AddTown(Vector3.Zero, 7);
        var survivor = world.Buildings.AddTown(new Vector3(100, 300, 0), 7);
        var site = Site("removed-town-building", Vector3.Zero, removed.Number);
        world.Buildings.Add(site);

        BuildingSave.NormalizeTownOverlap(world);

        Assert.Equal(removed.Number, site.Town);
        Assert.Same(survivor, Assert.Single(world.Buildings.Towns));
    }

    [Fact]
    public void Save_writes_the_current_outpost_radius()
    {
        var builder = new SaveBuilder(7);
        var state = Town(builder, "Town state Camp", "camp", Player, new Hand((int)FcsRecordType.Town, 4, 55, 2, 9), false, 1800);
        var (_, list) = SaveWithInstances(("camp", new Vector3(100, 300, 200), [state]));
        builder.Game.TownInstanceList = list;

        using var world = World();
        BuildingSave.Read(builder.Game, world, new GameDatabase(), Player, 7);
        var town = Assert.Single(world.Buildings.Towns);
        town.Override = 3400;

        BuildingSave.Write(builder.Game, world, Player);

        Assert.Equal(3400, state.Record.Ints["override"]);
    }

    [Fact]
    public void Reassessing_after_the_last_building_is_removed_removes_the_town()
    {
        using var world = World();
        var town = world.Buildings.AddTown(Vector3.Zero, 7);
        var site = Site("last-building", Vector3.Zero, town.Number);
        world.Buildings.Add(site);
        world.Buildings.Remove(site);

        ConstructionSystem.ReassessTown(world, town);

        Assert.Empty(world.Buildings.Towns);
    }
}
