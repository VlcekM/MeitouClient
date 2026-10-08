using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation;

namespace Meitou.Tests.Simulation;

public class PopulationTests
{
    [Theory]
    [InlineData(3, 0, 1f, false, 3)]       // v1 = 0: exactly v0
    [InlineData(3, 100, 1f, false, 3)]     // 100 is a sentinel, not a maximum
    [InlineData(5, 2, 1f, false, 5)]       // v1 below v0 gives v0
    [InlineData(4, 0, 0.5f, false, 2)]     // trunc(4 x 0.5)
    [InlineData(1, 0, 0.25f, false, 1)]    // at least 1 when n > 0
    [InlineData(0, 0, 3f, false, 0)]       // nothing stays nothing
    [InlineData(4, 0, 0.5f, true, 4)]      // dont multiply
    [InlineData(2, 0, 2.5f, false, 5)]
    public void Counts_follow_the_range_sentinel_and_multiplier_rules(int v0, int v1, float multiplier, bool dontMultiply, int expected)
    {
        Assert.Equal(expected, SquadFactory.Count(v0, v1, multiplier, dontMultiply, 12345));
    }

    [Fact]
    public void A_range_count_stays_in_the_range_and_covers_it()
    {
        var seen = new HashSet<int>();
        for (ulong i = 0; i < 500; i++)
        {
            int n = SquadFactory.Count(2, 5, 1, false, Rng.Hash(1, i, RngPurpose.Test));
            Assert.InRange(n, 2, 5);
            seen.Add(n);
        }
        Assert.Equal([2, 3, 4, 5], seen.Order());
    }

    [Fact]
    public void Members_stand_in_rows_of_eight_three_units_apart_with_odd_ones_pushed_back()
    {
        Assert.Equal(new Vector2(0, 0), SquadFactory.Offset(0));
        Assert.Equal(new Vector2(3, 1), SquadFactory.Offset(1));
        Assert.Equal(new Vector2(21, 1), SquadFactory.Offset(7));
        Assert.Equal(new Vector2(0, 3), SquadFactory.Offset(8));
        Assert.Equal(new Vector2(3, 4), SquadFactory.Offset(9));
        Assert.Equal(new Vector2(5, 6), SquadFactory.Offset(9, 5));   // animals2: spacing 5
    }

    [Fact]
    public void A_plan_creates_the_leader_then_squad_squad2_and_animals_in_order_with_roles()
    {
        var db = SyntheticTown.Database();
        var template = SquadTemplate.From(db.Find("10-t")!);
        var plan = SquadFactory.Plan(db, template, 1, 7, 1);
        Assert.Empty(plan.Problems);
        var m = plan.Members;
        Assert.Equal(SquadRole.Leader, m[0].Role);
        Assert.Equal("3-t", m[0].RecordId);
        Assert.Equal(new Vector2(0, 0), m[0].Offset);
        int squad = m.Count(x => x.Role == SquadRole.Squad1 && !x.IsAnimal);
        Assert.InRange(squad, 2, 4);
        Assert.Single(m, x => x.Role == SquadRole.Squad2);   // 1 with v1 = 100: exactly 1
        Assert.Single(m, x => x.IsAnimal);
        // Creation order: leader, squad, squad2, animals; each member's index is its place, offsets follow it.
        var roles = m.Select(x => x.IsAnimal ? 'a' : x.Role == SquadRole.Squad2 ? '2' : x.Role == SquadRole.Leader ? 'L' : '1').ToArray();
        Assert.Equal("L" + new string('1', squad) + "2a", new string(roles));
        for (int i = 0; i < m.Count; i++)
        {
            Assert.Equal(i, m[i].Index);
            Assert.Equal(SquadFactory.Offset(i), m[i].Offset);
        }
        Assert.InRange(m[^1].Age, 0.39f, 1.4f);   // 50% grown: -0.1 + 0.5 + up to 0.5 x 0.5
    }

    [Fact]
    public void The_multiplier_scales_counts_and_a_plan_is_a_pure_function_of_the_key()
    {
        var db = SyntheticTown.Database();
        var template = SquadTemplate.From(db.Find("12-t")!);
        Assert.Equal(3, SquadFactory.Plan(db, template, 1, 1, 1).Members.Count);
        Assert.Equal(6, SquadFactory.Plan(db, template, 2, 1, 1).Members.Count);
        Assert.Single(SquadFactory.Plan(db, template, 0.2f, 1, 1).Members);
        var a = SquadFactory.Plan(db, SquadTemplate.From(db.Find("10-t")!), 1, 9, 4);
        var b = SquadFactory.Plan(db, SquadTemplate.From(db.Find("10-t")!), 1, 9, 4);
        Assert.Equal(a.Members, b.Members);
    }

    [Fact]
    public void A_squad_gated_by_a_world_state_that_does_not_hold_is_not_made()
    {
        var db = SyntheticTown.Database();
        var template = SquadTemplate.From(db.Find("11-t")!);
        var closed = SquadFactory.Plan(db, template, 1, 1, 1);
        Assert.Empty(closed.Members);
        Assert.NotEmpty(closed.Problems);
        var open = SquadFactory.Plan(db, template, 1, 1, 1, new AllTrue());
        Assert.Equal(5, open.Members.Count);
    }

    sealed class AllTrue : IWorldStates
    {
        public bool IsTrue(string worldStateId) => true;
    }

    [Fact]
    public void Resident_lists_add_the_factions_unless_the_town_overrides()
    {
        var db = SyntheticTown.Database(overrideFlag: false);
        var town = TownData.From(db.Find("30-t")!);
        var faction = FactionData.From(db.Find("20-t")!);
        var ids = PopulationSystem.ResidentEntries(town, faction).Select(e => e.Id).ToArray();
        Assert.Equal(["10-t", "11-t", "12-t"], ids);   // the v0 = 0 entry (13-t) is dropped
        var overriding = TownData.From(SyntheticTown.Database(overrideFlag: true).Find("30-t")!);
        Assert.Equal(["10-t", "11-t"], PopulationSystem.ResidentEntries(overriding, faction).Select(e => e.Id));
    }

    [Fact]
    public void Zones_around_a_focus_are_the_340_box_plus_the_ring()
    {
        // In the middle of zone (32, 32): one zone.
        var middle = new Vector3(2304, 0, 2304);
        Assert.Equal([new ZoneCoordinate(32, 32)], ZoneActivation.ZonesAround(middle, 0));
        Assert.Equal(9, ZoneActivation.ZonesAround(middle, 1).Count);
        // 300 units from the edge of a zone: the box reaches over, the zone next door is activated.
        var edge = new Vector3(4608 - 300, 0, 2304);
        Assert.Equal(2, ZoneActivation.ZonesAround(edge, 0).Count);
        Assert.Equal(12, ZoneActivation.ZonesAround(edge, 1).Count);
        // 400 units from the edge: out of reach.
        Assert.Single(ZoneActivation.ZonesAround(new Vector3(4608 - 400, 0, 2304), 0));
    }

    [Fact]
    public void Residents_appear_when_the_focus_is_near_and_leave_after_the_grace_period()
    {
        using var world = SyntheticTown.World(5, 1, 3, new PopulationSettings { UnloadGraceSeconds = 10, CheckEveryTicks = 1 });
        var population = (PopulationSystem)world.Systems[0];
        world.RunTick();
        Assert.Equal(SiteStatus.Loaded, population.StatusOf(0));
        // 3 squads of 10 (patrol), 1 faction squad of 3; the gated one is not made.
        Assert.Equal(4, world.Squads.Count);
        var table = world.Characters;
        Assert.Equal(world.Squads.All.Sum(s => s.Members.Count), table.Count);
        var centre = new Vector2(SyntheticTown.Centre.X, SyntheticTown.Centre.Z);
        foreach (var s in world.Squads.All)
        {
            Assert.InRange(Vector2.Distance(new Vector2(s.Position.X, s.Position.Z), centre), 0, 300 * 0.6f + 0.01f);
            Assert.False(s.Leader.IsNone);
        }
        var patrols = world.Squads.All.Where(s => s.TemplateId == "10-t").ToList();
        Assert.Equal(3, patrols.Count);
        Assert.All(patrols, s => Assert.InRange(s.Members.Count, 1 + 2 + 1 + 1, 1 + 4 + 1 + 1));
        int before = table.Count;

        // The focus moves far away: nothing is removed during the grace period, then everything is.
        world.Commands.Enqueue(new FocusCommand(new Vector3(-50000, 0, -50000)) { Tick = world.Tick });
        world.RunTicks(150);   // 5 s
        Assert.Equal(before, table.Count);
        world.RunTicks(200);   // past 10 s
        Assert.Equal(SiteStatus.Unloaded, population.StatusOf(0));
        Assert.Equal(0, table.Count);
        Assert.Equal(0, world.Squads.Count);

        // Coming back loads them again (slots are reused with new generations).
        world.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = world.Tick });
        world.RunTicks(2);
        Assert.Equal(before, table.Count);
        Assert.Contains(table.Previous.ToArray(), c => c.Alive && c.Generation > 0);
    }

    [Fact]
    public void Coming_back_before_the_grace_ends_keeps_the_residents()
    {
        using var world = SyntheticTown.World(5, 1, 2, new PopulationSettings { UnloadGraceSeconds = 10, CheckEveryTicks = 1 });
        world.RunTick();
        var ids = world.Characters.Previous.ToArray().Select((c, i) => (c, i)).Where(x => x.c.Alive).Select(x => x.i).ToArray();
        world.Commands.Enqueue(new FocusCommand(new Vector3(-50000, 0, -50000)) { Tick = world.Tick });
        world.RunTicks(150);
        world.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = world.Tick });
        world.RunTicks(400);
        Assert.Equal(ids, world.Characters.Previous.ToArray().Select((c, i) => (c, i)).Where(x => x.c.Alive).Select(x => x.i).ToArray());
        Assert.All(world.Characters.Previous.ToArray().Where(c => c.Alive), c => Assert.Equal(0, c.Generation));
    }

    [Fact]
    public void Background_building_gives_the_same_town_as_building_in_the_tick()
    {
        using var sync = SyntheticTown.World(11, 1, 3);
        sync.RunTick();
        var settings = new PopulationSettings { Background = true };
        using var async = SyntheticTown.World(11, 1, 3, settings);
        Assert.True(TestWaits.TickUntil(async, () => async.Characters.Count > 0), "the background build never finished");
        Assert.Equal(sync.Characters.Count, async.Characters.Count);
        var a = sync.Squads.All.Select(s => (s.TemplateId, s.Position.X, s.Position.Z, s.Members.Count)).ToList();
        var b = async.Squads.All.Select(s => (s.TemplateId, s.Position.X, s.Position.Z, s.Members.Count)).ToList();
        Assert.Equal(a, b);
    }
}
