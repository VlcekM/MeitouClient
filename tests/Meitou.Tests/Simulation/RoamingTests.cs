using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class RoamingTests
{
    static (SimWorld World, PopulationSystem Population) Start(int threads = 1, ulong seed = 3)
    {
        var db = SyntheticTown.Database(roaming: true);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var population = new PopulationSystem(SyntheticTown.Data(db), new PopulationSettings { CheckEveryTicks = 15, UnloadGraceSeconds = 600, RoamUnloadGraceSeconds = 5 });
        var world = new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = false }, walk,
            [population, new MovementSystem(new PathService(walk, true))]);
        world.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = 0 });
        return (world, population);
    }

    static int SquadsOf(SimWorld w, string template) => w.Squads.All.Count(s => s.TemplateId == template && s.PlatoonId < 0);

    [Fact]
    public void Bar_squads_come_from_the_town_and_for_towns_of_type_Town_from_the_faction_too()
    {
        var db = SyntheticTown.Database(roaming: true);
        var town = TownData.From(db.Find("30-t")!);
        var faction = FactionData.From(db.Find("20-t")!);
        Assert.Equal(2, PopulationSystem.BarEntries(town, faction).Count);
        Assert.Equal(2, PopulationSystem.BarEntries(town with { Type = TownType.Village }, faction).Count);   // the faction here lists none
        var withFactionList = faction with { BarSquads = [new RecordLink("12-t", 1, 100, 0)] };
        Assert.Equal(3, PopulationSystem.BarEntries(town, withFactionList).Count);
        Assert.Equal(2, PopulationSystem.BarEntries(town with { Type = TownType.Village }, withFactionList).Count);
        Assert.Empty(PopulationSystem.BarEntries(town with { BarSquads = [new RecordLink("12-t", 0, 100, 0)] }, faction));   // v0 = 0 dropped
    }

    [Fact]
    public void A_loaded_town_adds_its_bar_squads_to_the_residents()
    {
        var (w, _) = Start();
        using var world = w;
        w.RunTicks(2);
        // Residents: 3 patrols, the gated one missing, the faction's folk (1); bars: 2 folk and 1 patrol, all certain.
        Assert.Equal(1 + 2, SquadsOf(w, "12-t"));
        Assert.Equal(3 + 1, SquadsOf(w, "10-t"));
    }

    [Fact]
    public void The_roaming_pool_fills_to_its_cap_one_squad_per_look_and_never_past_it()
    {
        var (w, _) = Start();
        using var world = w;
        w.RunTicks(5);
        Assert.Single(w.Platoons.All);   // the first look at the pools (the town loaded in the same tick)
        w.RunTicks(60);
        Assert.Equal(2, w.Platoons.Count);
        w.RunTicks(600);
        // Cap floor(0.7 x 10) = 7 members: two squads of 3, a third would make 9.
        Assert.Equal(2, w.Platoons.Count);
        Assert.All(w.Platoons.All, p => Assert.Equal(3, p.Size));
        Assert.All(w.Platoons.All, p => Assert.Equal(1, p.Target));   // the other town is the only place to go
    }

    [Fact]
    public void Roaming_squads_leave_the_active_zones_as_stand_ins_and_come_back_loaded_when_a_zone_near_them_is_active()
    {
        var (w, population) = Start();
        using var world = w;
        w.RunTicks(700);
        int characters = w.Characters.Count;
        var travellers = w.Platoons.All.ToList();
        Assert.Equal(2, travellers.Count);
        Assert.All(travellers, p => Assert.Equal(PlatoonState.Loaded, p.State));
        // The focus stays at the first town: the roamers walk out of it, then only their stand-ins go on.
        for (int i = 0; i < 40 && travellers.Any(p => p.State != PlatoonState.Unloaded); i++) w.RunTicks(150);
        Assert.All(travellers, p => Assert.Equal(PlatoonState.Unloaded, p.State));
        Assert.All(travellers, p => Assert.Equal(-1, p.SquadId));
        Assert.Equal(characters - 6, w.Characters.Count);   // their characters are gone, nobody else
        Assert.Equal(2, w.Platoons.Count);
        // They keep moving with no characters (25 units per second): the far town is 8500 units away.
        var before = travellers.Select(p => p.Position).ToList();
        w.RunTicks(300);
        Assert.True(Enumerable.Range(0, 2).All(i => Vector2.Distance(before[i], travellers[i].Position) > 100));
        // The camera goes to the far town: when they have got there, they take their characters back (the same plan: three members).
        w.Commands.Enqueue(new FocusCommand(SyntheticTown.Far) { Tick = w.Tick });
        for (int i = 0; i < 200 && travellers.Any(p => p.State != PlatoonState.Loaded); i++) w.RunTicks(150);
        Assert.All(travellers, p => Assert.Equal(PlatoonState.Loaded, p.State));
        Assert.All(travellers, p => Assert.Equal(3, w.Squads.Find(p.SquadId)!.Members.Count));
        Assert.All(travellers, p => Assert.Contains(Meitou.Data.World.WorldLayout.ZoneOf(p.Position.X, p.Position.Y), population.ActiveZones));
    }

    [Fact]
    public void After_a_visit_the_stand_in_waits_then_heads_home_and_goes_out_again()
    {
        var (w, _) = Start();
        using var world = w;
        w.RunTicks(700);
        var first = w.Platoons.All.First();
        bool reachedFar = false, wentHome = false, outAgain = false;
        for (int i = 0; i < 160 && !outAgain; i++)
        {
            w.RunTicks(300);
            if (first.Target == 1 && first.WaitLeft > 0) reachedFar = true;
            if (reachedFar && first.GoingHome && first.Target == first.Origin) wentHome = true;
            if (wentHome && !first.GoingHome && first.Target == 1) outAgain = true;
        }
        Assert.True(reachedFar, "it arrived at the other town and waits there");
        Assert.True(wentHome, "after the wait the target is the town it came from");
        Assert.True(outAgain, "at home it picks a town again");
    }

    static List<ulong> Scripted(int threads)
    {
        var (w, _) = Start(threads, 9);
        using var world = w;
        var hashes = new List<ulong>();
        int ran = 0;
        foreach (int target in new[] { 100, 800, 2500, 4500, 7000, 9000 })
        {
            if (target == 4500) w.Commands.Enqueue(new FocusCommand(SyntheticTown.Far) { Tick = w.Tick });
            w.RunTicks(target - ran);
            ran = target;
            hashes.Add(w.StateHash());
        }
        return hashes;
    }

    [Fact]
    public void Roaming_squads_hash_the_same_at_1_4_and_16_threads()
    {
        var one = Scripted(1);
        Assert.Equal(one, Scripted(4));
        Assert.Equal(one, Scripted(16));
        Assert.Equal(6, one.Distinct().Count());
    }
}
