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

/// <summary>
/// Player-town save import rules (docs/formats/save.md "Towns").
/// </summary>
public class PlayerTownImportTests
{
    /// <summary>
    /// The player faction id used in the synthetic saves.
    /// </summary>
    const string Player = "204-gamedata.base";
    /// <summary>
    /// A different faction whose towns must remain untouched.
    /// </summary>
    const string Foreign = "20-t";

    /// <summary>
    /// An empty headless world on flat walkable ground.
    /// </summary>
    static SimWorld World() => new(new WorldSettings { Seed = 1, PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 300));

    /// <summary>
    /// A complete synthetic player building assigned to the given outpost number.
    /// </summary>
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

    /// <summary>
    /// Builds a synthetic outpost instance list linked to the supplied states.
    /// </summary>
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

    /// <summary>
    /// Adds a town state with explicit ownership, handle, public flag and optional radius.
    /// </summary>
    static SaveTown Town(SaveBuilder builder, string name, string instance, string faction, Hand handle, bool publicTown = true, float? overrideRadius = null)
    {
        var town = builder.AddTown(name, instance, faction, isNest: false, zone: null, hand: handle);
        town.Record.Bools["public"] = publicTown;
        town.Record.Bools["discovered"] = true;
        if (overrideRadius is { } radius) town.Record.Floats["override"] = radius;
        return town;
    }

    /// <summary>
    /// NPC-town, missing and normalized-away player-town handles map to town zero across saves without a custom unresolved flag.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Non_outpost_building_handles_round_trip_as_town_zero(bool unresolved, bool removedOutpost)
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
        state.Bools["meitou unresolved town"] = true;
        using var world = World();
        BuildingSave.Read(builder.Game, world, db, Player, 7);
        Assert.Equal(0, Assert.Single(world.Buildings.All).Town);

        BuildingSave.Write(builder.Game, world, Player);
        using var reloaded = World();
        BuildingSave.Read(builder.Game, reloaded, db, Player, 7);
        Assert.Equal(0, Assert.Single(reloaded.Buildings.All).Town);
        Assert.False(state.Bools.ContainsKey("meitou unresolved town"));
    }

    /// <summary>
    /// The template id alone does not supply player ownership when both state and template lack a faction.
    /// </summary>
    [Fact]
    public void A_template_town_without_any_resolved_faction_is_not_imported()
    {
        var builder = new SaveBuilder(7);
        var state = Town(builder, "Town state Ridge Camp", "outpost", Player, new Hand((int)FcsRecordType.Town, 4, 55, 2, 9), false, 1800);
        state.Record.Strings.Remove("faction");
        var list = new FcsRecord { Type = (int)FcsRecordType.GamestateTownInstanceList, StringId = "towns" };
        list.Instances.Add(new FcsInstance { Id = "outpost", Target = PlayerTown.Template, Position = new Vector3(100, 300, 200), States = { state.Record.StringId } });
        builder.Game.TownInstanceList = list;

        using var world = World();
        BuildingSave.Read(builder.Game, world, new GameDatabase(), Player, 7);
        Assert.Empty(world.Buildings.Towns);
    }

    /// <summary>
    /// A template alone cannot turn an explicitly foreign town into a player outpost.
    /// </summary>
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
    /// A final discovered foreign runtime state replaces earlier missing or player ownership, including when pruning saves.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_final_discovered_foreign_owner_is_not_imported_or_rewritten(bool includePopulation, bool explicitPlayer)
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

    /// <summary>
    /// Each state's unresolved owner falls back to the actual TOWN faction reference, including during obsolete-outpost pruning.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("missing-faction")]
    public void Missing_or_invalid_faction_uses_the_template_reference(string faction)
    {
        var template = SyntheticTown.Rec(PlayerTown.Template, FcsRecordType.Town, "Template");
        template.References["faction"] = [SyntheticTown.Ref(Player)];
        var db = SyntheticTown.Database(extra: [template]);
        var builder = new SaveBuilder(7);
        var state = Town(builder, "Town state Fallback", "camp", faction, new Hand(13, 4, 55, 2, 9));
        var (_, list) = SaveWithInstances(("camp", new Vector3(100, 300, 200), [state]));
        builder.Game.TownInstanceList = list;
        using var world = World();

        BuildingSave.Read(builder.Game, world, db, Player, 7);

        var town = Assert.Single(world.Buildings.Towns);
        Assert.Equal(7, town.Owner);
        world.Buildings.RemoveTown(town);
        BuildingSave.Write(builder.Game, world, Player, db: db);
        Assert.Empty(list.Instances);
        Assert.DoesNotContain(state.Record, builder.Game.Quick.Records);
    }

    /// <summary>
    /// Runtime ownership follows traversal order, skipping nests and undiscovered states once ownership is already player-side.
    /// </summary>
    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, true, true)]
    public void Runtime_owner_order_and_skip_rules(bool lastPlayer, bool discovered, bool nest, bool expectedPlayer)
    {
        var builder = new SaveBuilder(7);
        var first = Town(builder, "Town state First", "camp", lastPlayer ? Foreign : Player, new Hand(13, 4, 55, 2, 9), false, 1800);
        var last = Town(builder, "Town state Last", "camp", lastPlayer ? Player : Foreign, new Hand(13, 4, 55, 2, 9), true, 2800);
        first.Record.Strings["name"] = "First";
        last.Record.Strings["name"] = "Last";
        last.Record.Bools["discovered"] = discovered;
        last.Record.Bools["is nest"] = nest;
        var (_, list) = SaveWithInstances(("camp", new Vector3(100, 300, 200), [first]));
        builder.Game.TownInstanceList = list;
        var db = SyntheticTown.Database();
        using var world = World();

        BuildingSave.Read(builder.Game, world, db, Player, 7);

        if (!expectedPlayer)
        {
            Assert.Empty(world.Buildings.Towns);
            BuildingSave.Write(builder.Game, world, Player, db: db);
            Assert.Contains(last.Record, builder.Game.Quick.Records);
            Assert.Single(list.Instances);
            return;
        }

        var town = Assert.Single(world.Buildings.Towns);
        Assert.Equal(lastPlayer ? "Last" : "First", town.Name);
        Assert.Equal(lastPlayer ? 2800 : 1800, town.Override);
        Assert.Equal(lastPlayer, town.Public);
        world.Buildings.RemoveTown(town);
        BuildingSave.Write(builder.Game, world, Player, db: db);
        Assert.Empty(list.Instances);
        Assert.DoesNotContain(first.Record, builder.Game.Quick.Records);
        if (nest) Assert.Contains(last.Record, builder.Game.Quick.Records);
        else Assert.DoesNotContain(last.Record, builder.Game.Quick.Records);
    }

    /// <summary>
    /// The sample's sparse attached state and separate full runtime state restore both outposts and fractional radii.
    /// </summary>
    [Fact]
    public void Sparse_linked_and_full_runtime_states_restore_the_sample_outposts()
    {
        var builder = new SaveBuilder(7);
        var entries = new List<(string Id, Vector3 Position, SaveTown[] States)>();
        foreach (var (name, radius, number) in new[] { ("Drycoom", 2652.530029296875f, 1), ("Goonrock", 2186.80908203125f, 2) })
        {
            var hand = new Hand(13, number, 55 + number, 2, 9);
            var sparse = Town(builder, "0", name, "", hand);
            sparse.Record.Strings.Remove("faction");
            sparse.Record.Bools.Remove("public");
            sparse.Record.Ints.Remove("plevel");
            var full = Town(builder, "Town state " + name, name, Player, hand, true, radius);
            full.Record.Strings["name"] = name;
            entries.Add((name, new Vector3(number * 10000, 300, 0), [sparse]));
        }
        var (_, list) = SaveWithInstances(entries.ToArray());
        builder.Game.TownInstanceList = list;
        using var world = World();

        BuildingSave.Read(builder.Game, world, SyntheticTown.Database(), Player, 7);

        Assert.Equal(["Drycoom", "Goonrock"], world.Buildings.Towns.Select(town => town.Name));
        Assert.Equal([2652.530029296875f, 2186.80908203125f], world.Buildings.Towns.Select(town => town.Override));
        Assert.All(world.Buildings.Towns, town => Assert.Equal(7, town.Owner));
    }

    /// <summary>
    /// Linked states combine into one outpost with the first valid name and handle and the last public/radius values.
    /// </summary>
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
            Assert.Equal(3400, state.Record.Floats["override"]);
        });
    }

    /// <summary>
    /// A level placement can resolve a player-town state even when the save has no outpost instance list.
    /// </summary>
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

    /// <summary>
    /// A player outpost imported from a level town is written back through its runtime state only, not as a dynamic placement with the same id.
    /// </summary>
    [Fact]
    public void A_level_town_outpost_is_written_without_a_dynamic_instance()
    {
        // Catches a level placement and a dynamic instance with one id both instantiating after reload.
        var db = SyntheticTown.Database();
        var data = PopulationData.Create(db, [new TownPlacement("level-outpost", "30-t", SyntheticTown.Centre, null)]);
        var builder = new SaveBuilder(7);
        var state = Town(builder, "Town state Level Camp", "level-outpost", Player, new Hand((int)FcsRecordType.Town, 8, 58, 4, 12), false, 1900);
        using var world = World();
        BuildingSave.Read(builder.Game, world, db, Player, 7, data);
        world.Buildings.Towns.Single().Name = "Renamed";

        BuildingSave.Write(builder.Game, world, Player);

        Assert.True(builder.Game.TownInstanceList is null || builder.Game.TownInstanceList.Instances.All(i => i.Id != "level-outpost"));
        Assert.Equal("Renamed", state.Record.Strings["name"]);
        using var reloaded = World();
        BuildingSave.Read(builder.Game, reloaded, db, Player, 7, data);
        var town = Assert.Single(reloaded.Buildings.Towns);
        Assert.Equal(("Renamed", 1900f), (town.Name, town.Override));
    }

    /// <summary>
    /// A player-faction town state that was never imported as an outpost survives a save.
    /// </summary>
    [Fact]
    public void A_never_imported_player_faction_town_state_is_not_deleted_on_save()
    {
        // Catches pruning of states whose level site is unknown (a mod town) or whose instance is empty.
        var builder = new SaveBuilder(7);
        var unknown = Town(builder, "Town state Mod town", "mod-town", Player, new Hand((int)FcsRecordType.Town, 8, 58, 4, 12));
        var empty = Town(builder, "Town state Nowhere", "", Player, new Hand((int)FcsRecordType.Town, 9, 59, 4, 12));
        using var world = World();
        BuildingSave.Read(builder.Game, world, SyntheticTown.Database(), Player, 7, PopulationData.Create(SyntheticTown.Database(), []));
        Assert.Empty(world.Buildings.Towns);

        BuildingSave.Write(builder.Game, world, Player);

        Assert.Contains(unknown.Record, builder.Game.Quick.Records);
        Assert.Contains(empty.Record, builder.Game.Quick.Records);
        Assert.Contains(unknown, builder.Game.Towns);
    }

    /// <summary>
    /// A removed outpost's number is never given to a later outpost.
    /// </summary>
    [Fact]
    public void A_removed_outposts_number_is_not_reused()
    {
        // Catches CampaignTown.OfPlayer(number) in AI state pointing at a different, later outpost.
        using var world = World();
        world.Buildings.AddTown(Vector3.Zero, 7);
        var last = world.Buildings.AddTown(new Vector3(9000, 300, 0), 7);
        ConstructionSystem.ReassessTown(world, last);
        Assert.Single(world.Buildings.Towns);

        Assert.Equal(3, world.Buildings.AddTown(new Vector3(20000, 300, 0), 7).Number);
    }

    /// <summary>
    /// A removed outpost's handle container is not reused by a later outpost, also after a save and reload.
    /// </summary>
    [Fact]
    public void A_removed_outposts_handle_container_is_not_reused()
    {
        var builder = new SaveBuilder(7);
        using var world = World();
        var first = world.Buildings.AddTown(Vector3.Zero, 7);
        int firstContainer = BuildingSave.Write(builder.Game, world, Player)[first.Number].C;
        ConstructionSystem.ReassessTown(world, first);
        BuildingSave.Write(builder.Game, world, Player);

        using var reloaded = World();
        BuildingSave.Read(builder.Game, reloaded, new GameDatabase(), Player, 7);
        var second = reloaded.Buildings.AddTown(new Vector3(9000, 300, 0), 7);
        int secondContainer = BuildingSave.Write(builder.Game, reloaded, Player)[second.Number].C;

        Assert.NotEqual(firstContainer, secondContainer);
    }

    /// <summary>
    /// A save written by an earlier Meitou build keeps its integer radius and the exact label name, trailing spaces included.
    /// </summary>
    [Fact]
    public void An_old_meitou_save_restores_the_integer_radius_and_untrimmed_name()
    {
        // Catches an old outpost falling back to the default radius and losing trailing spaces of its name.
        var builder = new SaveBuilder(7);
        var state = Town(builder, "Town state Camp  ", "camp", Player, new Hand((int)FcsRecordType.Town, 4, 55, 2, 9));
        state.Record.Ints["override"] = 1800;
        var (_, list) = SaveWithInstances(("camp", new Vector3(100, 300, 200), [state]));
        builder.Game.TownInstanceList = list;

        using var world = World();
        BuildingSave.Read(builder.Game, world, new GameDatabase(), Player, 7);

        var town = Assert.Single(world.Buildings.Towns);
        Assert.Equal(("Camp  ", 1800f), (town.Name, town.Override));
    }

    /// <summary>
    /// A float radius wins over an old integer one.
    /// </summary>
    [Fact]
    public void A_float_radius_wins_over_an_integer_radius()
    {
        var builder = new SaveBuilder(7);
        var state = Town(builder, "Town state Camp", "camp", Player, new Hand((int)FcsRecordType.Town, 4, 55, 2, 9), true, 2652.5f);
        state.Record.Ints["override"] = 1800;
        var (_, list) = SaveWithInstances(("camp", new Vector3(100, 300, 200), [state]));
        builder.Game.TownInstanceList = list;

        using var world = World();
        BuildingSave.Read(builder.Game, world, new GameDatabase(), Player, 7);

        Assert.Equal(2652.5f, Assert.Single(world.Buildings.Towns).Override);
    }

    /// <summary>
    /// Load normalization excludes the exact radius boundary and preserves sparse town numbers.
    /// </summary>
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

    /// <summary>
    /// Placement reassessment preserves the larger outpost identity and properties.
    /// </summary>
    [Fact]
    public void Placement_overlap_merges_buildings_into_the_larger_radius_town()
    {
        using var world = World();
        var first = world.Buildings.AddTown(new Vector3(0, 300, 0), 7);
        var second = world.Buildings.AddTown(new Vector3(1000, 300, 0), 7);
        world.Player.Faction = 7;
        first.Override = 1000;
        second.Override = 3000;
        second.Name = "Larger";
        second.Public = true;
        second.SaveHandle = new Hand(13, 4, 55, 2, 9);
        world.Buildings.Add(Site("first", new Vector3(0, 300, 0), first.Number));
        world.Buildings.Add(Site("second", new Vector3(1000, 300, 0), second.Number));

        ConstructionSystem.ReassessTown(world, first);

        Assert.Same(second, Assert.Single(world.Buildings.Towns));
        Assert.All(world.Buildings.All, site => Assert.Equal(second.Number, site.Town));
        Assert.Equal("Larger", second.Name);
        Assert.True(second.Public);
        Assert.Equal(new Hand(13, 4, 55, 2, 9), second.SaveHandle);
    }

    /// <summary>
    /// The first eligible overlap wins over a nearer candidate, then the survivor merges again.
    /// </summary>
    [Fact]
    public void Placement_overlap_uses_list_order_and_reassesses_the_survivor()
    {
        using var world = World();
        world.Player.Faction = 7;
        var current = world.Buildings.AddTown(new Vector3(0, 300, 0), 7);
        var first = world.Buildings.AddTown(new Vector3(2000, 300, 0), 7);
        var nearer = world.Buildings.AddTown(new Vector3(100, 300, 0), 7);
        first.Override = 3000;
        nearer.Override = 2000;
        world.Buildings.Add(Site("current", current.Position, current.Number));
        world.Buildings.Add(Site("first", first.Position, first.Number));
        world.Buildings.Add(Site("nearer", nearer.Position, nearer.Number));

        ConstructionSystem.ReassessTown(world, current);

        Assert.Same(nearer, Assert.Single(world.Buildings.Towns));
        Assert.All(world.Buildings.All, site => Assert.Equal(nearer.Number, site.Town));
    }

    /// <summary>
    /// Load normalization leaves building town references unchanged when their town is removed.
    /// </summary>
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

    /// <summary>
    /// Saving replaces the old radius with the live reassessed value.
    /// </summary>
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

        Assert.Equal(3400, state.Record.Floats["override"]);
    }

    /// <summary>
    /// Loading keeps a town's saved centre and radius even when its buildings lie elsewhere; the original refits only on a placement or dismantle.
    /// </summary>
    [Fact]
    public void Loading_keeps_the_saved_centre_and_radius()
    {
        var builder = new SaveBuilder(7);
        var handle = new Hand((int)FcsRecordType.Town, 4, 55, 2, 9);
        var state = Town(builder, "Town state Camp", "camp", Player, handle, false, 1800);
        var (_, list) = SaveWithInstances(("camp", new Vector3(100, 300, 200), [state]));
        builder.Game.TownInstanceList = list;
        var db = new GameDatabase();
        var file = new FcsFile();
        file.Records.Add(new FcsRecord { StringId = "building", RecordType = FcsRecordType.Building });
        db.Apply(file, "test.mod");
        using (var source = World())
        {
            source.Buildings.Add(Site("1-zone16.16-INGAME", new Vector3(5000, 300, 5000), 0));
            BuildingSave.Write(builder.Game, source, Player);
        }

        handle.Write(Assert.Single(builder.Game.Zones.Values).BuildingStates.Single(), "town");
        using var world = World();

        BuildingSave.Read(builder.Game, world, db, Player, 7);

        var town = Assert.Single(world.Buildings.Towns);
        Assert.Equal(1, Assert.Single(world.Buildings.All).Town);
        Assert.Equal(new Vector3(100, 300, 200), town.Position);
        Assert.Equal(1800, town.Override);
    }

    /// <summary>
    /// Original-save fractional radii survive loading and writing without an integer shadow field.
    /// </summary>
    [Theory]
    [InlineData(2652.530029296875f)]
    [InlineData(2186.80908203125f)]
    public void Original_float_radius_round_trips(float radius)
    {
        var builder = new SaveBuilder(7);
        var state = Town(builder, "Town state Camp", "camp", Player, new Hand(13, 4, 55, 2, 9), false, radius);
        var (_, list) = SaveWithInstances(("camp", new Vector3(100, 300, 200), [state]));
        builder.Game.TownInstanceList = list;
        using var world = World();
        BuildingSave.Read(builder.Game, world, new GameDatabase(), Player, 7);
        var town = Assert.Single(world.Buildings.Towns);
        Assert.Equal(radius, town.Override);
        town.Override = radius + 0.25f;
        state.Record.Ints["override"] = 999;

        BuildingSave.Write(builder.Game, world, Player);

        Assert.False(state.Record.Ints.ContainsKey("override"));
        Assert.Equal(radius + 0.25f, state.Record.Floats["override"]);
        using var reloaded = World();
        BuildingSave.Read(builder.Game, reloaded, new GameDatabase(), Player, 7);
        Assert.Equal(radius + 0.25f, Assert.Single(reloaded.Buildings.Towns).Override);
    }

    /// <summary>
    /// The full runtime name takes precedence over its record label and survives a rename and reload.
    /// </summary>
    [Fact]
    public void Runtime_town_name_survives_a_rename_round_trip()
    {
        var builder = new SaveBuilder(7);
        var state = Town(builder, "Town state Stale label", "camp", Player, new Hand(13, 4, 55, 2, 9));
        state.Record.Strings["name"] = "Runtime name";
        var (_, list) = SaveWithInstances(("camp", new Vector3(100, 300, 200), [state]));
        builder.Game.TownInstanceList = list;
        using var world = World();
        BuildingSave.Read(builder.Game, world, new GameDatabase(), Player, 7);
        var town = Assert.Single(world.Buildings.Towns);
        Assert.Equal("Runtime name", town.Name);
        town.Name = "Renamed camp";

        BuildingSave.Write(builder.Game, world, Player);

        Assert.Equal("Renamed camp", state.Record.Strings["name"]);
        state.Record.Name = "Town state Another stale label";
        using var reloaded = World();
        BuildingSave.Read(builder.Game, reloaded, new GameDatabase(), Player, 7);
        Assert.Equal("Renamed camp", Assert.Single(reloaded.Buildings.Towns).Name);
    }

    /// <summary>
    /// Removing the last outdoor site preserves a town with indoor sites, including furniture whose parent no longer exists.
    /// </summary>
    [Theory]
    [InlineData("last-outdoor")]
    [InlineData("unloaded-house")]
    public void Dismantle_reassessment_preserves_a_town_with_indoor_or_orphan_sites(string parent)
    {
        using var world = World();
        var town = world.Buildings.AddTown(new Vector3(100, 300, 200), 7);
        town.Name = "Still occupied";
        var outdoor = Site("last-outdoor", town.Position, town.Number);
        var indoor = Site("indoor", town.Position, town.Number);
        indoor.Parent = parent;
        world.Buildings.Add(outdoor);
        world.Buildings.Add(indoor);
        world.Buildings.Remove(outdoor);

        ConstructionSystem.ReassessTown(world, town);

        Assert.Same(town, Assert.Single(world.Buildings.Towns));
        Assert.Equal("Still occupied", town.Name);
        world.Buildings.Remove(indoor);
        ConstructionSystem.ReassessTown(world, town);
        Assert.Empty(world.Buildings.Towns);
    }

    /// <summary>
    /// Placement/dismantle reassessment drops an outpost whose last building was removed.
    /// </summary>
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