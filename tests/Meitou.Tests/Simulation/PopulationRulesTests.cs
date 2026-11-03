using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation;
using static Meitou.Tests.Simulation.SyntheticTown;

namespace Meitou.Tests.Simulation;

/// <summary>Population rules pinned to the original's behaviour (docs/game/factions-squads-towns.md).</summary>
public class PopulationRulesTests
{
    /// <summary>FUN_1408f5ad0: 10 for an empty sector, a flat 5 below a load of 2, 1 at 2, 1 / n above.</summary>
    [Theory]
    [InlineData(0f, 10f)]
    [InlineData(0.5f, 5f)]
    [InlineData(1f, 5f)]
    [InlineData(1.5f, 5f)]
    [InlineData(2f, 1f)]
    [InlineData(4f, 0.25f)]
    public void Patrol_sector_weight_follows_the_original_constants(float load, float weight)
        => Assert.Equal(weight, PopulationSystem.PatrolWeight(load));

    /// <summary>FUN_1405cf470: state 2 returns to 1 for the player's faction or in a town friendly to the character (its own faction's), never in the open.</summary>
    [Fact]
    public void A_unique_is_freed_again_only_in_a_friendly_town_or_for_the_player()
    {
        using var world = SyntheticTown.World(5, 1, 3, new PopulationSettings { CheckEveryTicks = 1 });
        var population = (PopulationSystem)world.Systems[0];
        world.RunTick();
        var table = world.Characters;
        int slot = Enumerable.Range(0, table.Previous.Length).First(i => table.Previous[i].Alive);
        var cold = table.Cold(slot)!;
        Assert.True(population.FreeAgain(world, cold, Centre, -1));
        var open = new Vector3(-90000, 0, -90000);
        Assert.False(population.FreeAgain(world, cold, open, -1));
        Assert.True(population.FreeAgain(world, cold, open, cold.Faction));
    }

    /// <summary>FUN_140998fa0 calls the day's reset (FUN_140998e40): standing in another town than the last purchase's clears the list and records the day.</summary>
    [Fact]
    public void A_shopper_in_another_town_starts_its_list_over_and_the_day_is_recorded()
    {
        var list = new Meitou.Simulation.Ai.ShoppingList([3], [1]);
        list.Begin(1, 4, 9);
        list.Use(0);
        Assert.False(list.Pending(4, 9, 1));
        Assert.True(list.Pending(4, 9, 2));
        list.Use(0);
        // At 03:00 of the next day another town clears it and records that day, so the 07:00 reset does not clear it a second time.
        Assert.True(list.Pending(5, 3, 2));
        list.Use(0);
        Assert.False(list.Pending(5, 9, 1));
    }

    /// <summary>FUN_1406724e0: a memory of more than 40 entries erases those older than 48 whole hours whose character is not the player's.</summary>
    [Fact]
    public void A_big_squad_memory_forgets_old_entries_once_it_holds_more_than_forty()
    {
        var ground = new OpenGroundWalkability((_, _) => 0);
        using var world = new Meitou.Simulation.World(new WorldSettings { Seed = 1, Threads = 1, PublishSnapshots = false }, ground, []);
        world.Player.Faction = 1;
        CharacterId Make(int faction)
        {
            var cold = new Meitou.Tests.Combat.Combatant { Name = "c" }.MakeCold();
            cold.Faction = faction;
            return world.Characters.Spawn(new CharacterHot { Position = Vector3.Zero, Health = 100 }, cold, 0);
        }
        var memory = new SquadMemory();
        var others = Enumerable.Range(0, 41).Select(_ => Make(0)).ToList();
        world.GameHours = 0;
        foreach (var c in others.Take(40)) memory.Remember(world, 7, c, 0);
        world.GameHours = 10;
        memory.Remember(world, 7, others[40], 0);
        world.GameHours = 10 + 48.9;
        memory.Age(world);
        // 41 entries: the 40 old ones go, the fresher one stays.
        Assert.Null(memory.Find(world, 7, others[0]));
        Assert.NotNull(memory.Find(world, 7, others[40]));
    }

    /// <summary>FUN_14057f820:86-103: a resident member stands at a general node, else a shopkeeper node, else (null) at the platoon point.</summary>
    [Fact]
    public void A_resident_member_takes_a_general_node_before_a_shopkeeper_node()
    {
        HomeSpot Node(int type, float x) => new(HomeSpotKind.Other, new Vector3(x, 0, 0), 0) { NodeType = type };
        TownBuilding Building(params HomeSpot[] spots) => new("i", "b", "n", Vector3.Zero, 0, false, false, spots);
        var both = Building(Node(0, 5), Node(1, 9));
        for (ulong salt = 0; salt < 8; salt++) Assert.Equal(9, PopulationSystem.MemberSpot(both, 1, salt)!.Value.X);
        Assert.Equal(5, PopulationSystem.MemberSpot(Building(Node(0, 5)), 1, 0)!.Value.X);
        Assert.Null(PopulationSystem.MemberSpot(Building(Node(2, 5)), 1, 0));
    }

    /// <summary>FUN_14057f1f0 / FUN_14057f4c0 / FUN_140553210: the first designation set wins unless a platoon is made at the building (it overwrites); the public day and the template are the last written.</summary>
    [Fact]
    public void A_buildings_record_keeps_the_first_designation_the_last_template_and_the_last_public_day()
    {
        using var world = new Meitou.Simulation.World(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0));
        SquadTemplate T(string id, int designation, bool open) => new() { Id = id, Name = id, BuildingDesignation = designation, PublicDay = open };
        var fixtures = world.Fixtures;
        fixtures.NoteSetup("b", T("bar", 3, false));
        fixtures.NoteSetup("b", T("shop", 1, true));
        Assert.Equal(3, fixtures.RecordOf("b")!.Designation);   // first set wins
        Assert.True(fixtures.PublicDayOf("b"));                 // last setup wins
        fixtures.NoteAssigned("b", T("a", 1, true));
        fixtures.NoteAssigned("b", T("c", 0, true));
        Assert.Equal("c", fixtures.RecordOf("b")!.Template);    // last assignment wins
        Assert.Equal(3, fixtures.RecordOf("b")!.Designation);
        fixtures.NoteMade("b", T("c", 0, true));
        Assert.Equal(0, fixtures.RecordOf("b")!.Designation);   // a platoon made at the building overwrites, a zero too
    }

    sealed class Duty : ITickSystem, IGoalSource
    {
        public bool On;
        public CharacterId Worker, Body;
        public void Publish(Meitou.Simulation.World world, SquadGoals goals) { if (On) goals.Add(1, Worker, GoalTarget.Of(Body)); }
    }

    /// <summary>FUN_14026a320 / FUN_140269ff0: an entry of the squad's goal list stays 3 s after its last refresh, so a claim outlives the duty that made it.</summary>
    [Fact]
    public void A_goal_claim_outlives_its_duty_by_three_seconds()
    {
        var duty = new Duty { On = true, Worker = new CharacterId(5, 1), Body = new CharacterId(6, 1) };
        using var world = new Meitou.Simulation.World(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0), [duty]);
        world.RunTicks(3);
        var asker = new CharacterId(7, 1);
        Assert.Equal(1, SquadGoals.Of(world).Working(1, asker, GoalTarget.Of(duty.Body)));
        duty.On = false;
        world.RunTicks(80);   // 2.7 s
        Assert.Equal(1, SquadGoals.Of(world).Working(1, asker, GoalTarget.Of(duty.Body)));
        world.RunTicks(20);   // past 3 s
        Assert.Equal(0, SquadGoals.Of(world).Working(1, asker, GoalTarget.Of(duty.Body)));
    }

    /// <summary>Issue #134 (docs/game/character-stats.md): 1 + the window's share of the zone's class, windows 6 (unflagged) and max(4, N1 / 4) (flagged).</summary>
    [Theory]
    [InlineData(true, 4, 2f)]
    [InlineData(true, 8, 1.5f)]
    [InlineData(true, 16, 1.25f)]
    [InlineData(false, 3, 2f)]
    [InlineData(false, 12, 1.5f)]
    public void The_medical_clock_adds_the_rotating_windows_share(bool flagged, int count, float factor)
        => Assert.Equal(factor, MedicalClock.Factor(flagged, count));

    [Fact]
    public void A_character_is_on_screen_in_front_of_the_eye_within_the_npc_range_only()
    {
        var view = new CameraViewCommand(new Vector3(0, 500, 0), new Vector3(0, -0.5f, 1), 50 * MathF.PI / 180, 16f / 9, 3000) { Tick = 0 };
        Assert.True(MedicalClock.OnScreen(view, new Vector3(0, 0, 1500)));
        Assert.False(MedicalClock.OnScreen(view, new Vector3(0, 0, -1500)));      // behind
        Assert.False(MedicalClock.OnScreen(view, new Vector3(0, 0, 4000)));       // beyond the range
        Assert.False(MedicalClock.OnScreen(view, new Vector3(5000, 0, 1500)));    // far to the side
    }

    /// <summary>FUN_1408cbea0 / FUN_140a00690: the buildings a finder looks at are those within 1.1 x the radius of the character's town; none of them beyond.</summary>
    [Fact]
    public void The_material_finder_looks_at_buildings_within_the_characters_town_only()
    {
        var data = SyntheticTown.Data(SyntheticTown.Database());
        using var world = new Meitou.Simulation.World(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0));
        var cold = new CharacterCold { Faction = data.SiteFactions[0] };
        var inTown = world.Fixtures.TownCandidates(world, data, cold, SyntheticTown.Centre);
        Assert.True(inTown(SyntheticTown.Centre + new Vector3(50, 0, 0)));
        Assert.False(inTown(SyntheticTown.Centre + new Vector3(3000, 0, 0)));
    }

    /// <summary>FUN_1407f4770: the first set holding the building answers; no replacement skips it, one gives it, several pick by cumulative weight; none leaves the building.</summary>
    [Fact]
    public void A_factions_first_swap_holding_the_building_skips_replaces_or_picks_it_by_weight()
    {
        FcsRecord Swap(string id, string replace, params (string With, int Weight)[] with)
        {
            var r = SyntheticTown.Rec(id, FcsRecordType.BUILDINGS_SWAP, id);
            r.References["to replace"] = [SyntheticTown.Ref(replace)];
            r.References["replace with"] = [.. with.Select(w => SyntheticTown.Ref(w.With, w.Weight))];
            return r;
        }
        var faction = SyntheticTown.Rec("f-t", FcsRecordType.FACTION, "F");
        faction.References["buildings replacements"] = [SyntheticTown.Ref("s1"), SyntheticTown.Ref("s2"), SyntheticTown.Ref("s3"), SyntheticTown.Ref("s4")];
        var file = new FcsFile();
        file.Records.AddRange([faction, Swap("s1", "carpet"), Swap("s2", "chair", ("metal chair", 1)), Swap("s3", "table", ("a", 1), ("b", 3)), Swap("s4", "carpet", ("never", 1))]);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        var swaps = BuildingSwaps.From(db);
        Assert.Equal("", swaps.Replacement("f-t", "carpet", 0.5f));                 // the first set holding it has no replacement
        Assert.Equal("metal chair", swaps.Replacement("f-t", "chair", 0.9f));       // one entry
        Assert.Equal("a", swaps.Replacement("f-t", "table", 0.2f));                 // cumulative 1 of 4 is above 0.8
        Assert.Equal("b", swaps.Replacement("f-t", "table", 0.3f));
        Assert.Null(swaps.Replacement("f-t", "bench", 0.1f));                       // no set holds it
        Assert.Null(swaps.Replacement("other", "chair", 0.1f));                     // not this faction's
    }
}
