using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using static Meitou.Tests.Simulation.SyntheticTown;

namespace Meitou.Tests.Simulation;

/// <summary>Population rules pinned to the original's behaviour (docs/game/factions-squads-towns.md).</summary>
public class PopulationRulesTests
{
    /// <summary>FUN_1408f5ad0: 10 for an empty sector, a flat 5 below a load of 2, 1 at 2, 1 / n above.</summary>
    [Theory]
    [InlineData(0f, 10f)]
    [InlineData(1f, 5f)]
    [InlineData(4f, 0.25f)]
    public void Patrol_sector_weight_follows_the_original_constants(float load, float weight)
        => Assert.Equal(weight, PopulationSystem.PatrolWeight(load));

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

    /// <summary>The furniture of an interior layout goes through its building owner's swaps too: a skipped piece is left out, a replaced one is the new building in place.</summary>
    [Fact]
    public void A_layouts_furniture_is_swapped_by_the_buildings_owner()
    {
        var faction = SyntheticTown.Rec("f-t", FcsRecordType.FACTION, "F");
        faction.References["buildings replacements"] = [SyntheticTown.Ref("s1"), SyntheticTown.Ref("s2")];
        var carpet = SyntheticTown.Rec("s1", FcsRecordType.BUILDINGS_SWAP, "s1");
        carpet.References["to replace"] = [SyntheticTown.Ref("carpet")];
        var chair = SyntheticTown.Rec("s2", FcsRecordType.BUILDINGS_SWAP, "s2");
        chair.References["to replace"] = [SyntheticTown.Ref("chair")];
        chair.References["replace with"] = [SyntheticTown.Ref("metal", 100)];
        var records = new List<FcsRecord> { faction, carpet, chair };
        foreach (var id in new[] { "carpet", "chair", "metal", "bldg" }) records.Add(SyntheticTown.Rec(id, FcsRecordType.BUILDING, id));
        var layout = SyntheticTown.Rec("lay", FcsRecordType.INSTANCE_COLLECTION, "lay");
        layout.Strings["associated building"] = "bldg";
        layout.Instances.Add(new FcsInstance { Id = "a", Target = "carpet", Position = new Vector3(1, 0, 0) });
        layout.Instances.Add(new FcsInstance { Id = "b", Target = "chair", Position = new Vector3(2, 0, 0) });
        var file = new FcsFile();
        file.Records.AddRange(records);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        var interiorsFile = new FcsFile();
        interiorsFile.Records.Add(layout);
        var interiors = new GameDatabase();
        interiors.Apply(interiorsFile, "interiors.level");
        var layouts = new Meitou.Data.World.BuildingLayouts(db, interiors);
        var placement = new Meitou.Data.World.BuildingPlacement(new Meitou.Data.World.ZoneCoordinate(1, 1), "p", "bldg", new Vector3(1, 1, 1), Quaternion.Identity, null, null);
        var layoutRecord = interiors.Find("lay")!;
        Assert.Equal(["carpet", "chair"], layouts.Items(layoutRecord, placement).Select(i => i.Building.StringId));
        layouts.UseSwaps(BuildingSwaps.From(db), new Dictionary<string, string> { [placement.Key] = "f-t" });
        var items = layouts.Items(layoutRecord, placement).ToList();
        var only = Assert.Single(items);
        Assert.Equal("metal", only.Building.StringId);
        Assert.Equal(new Vector3(2, 0, 0), only.Position);
    }
}
