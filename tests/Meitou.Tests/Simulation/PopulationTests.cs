using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class PopulationTests
{
    [Fact]
    public void Residents_appear_when_the_focus_is_near_and_leave_with_their_zones()
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

        // The focus moves far away: the town lapses with its zones (the camera block's 4 s), not after a grace of its own.
        world.Commands.Enqueue(new FocusCommand(new Vector3(-50000, 0, -50000)) { Tick = world.Tick });
        world.RunTicks(60);   // 2 s

        Assert.Equal(before, table.Count);

        world.RunTicks(90);   // past the 4 s the camera's block stays loaded

        Assert.Equal(SiteStatus.Unloaded, population.StatusOf(0));
        Assert.Equal(0, table.Count);
        Assert.Equal(0, world.Squads.Count);

        // Coming back loads them again (slots are reused with new generations) once the unloaded platoons' 5 s activation dwell has run.
        world.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = world.Tick });
        world.RunTicks(170);

        Assert.Equal(before, table.Count);
        Assert.Contains(table.Previous.ToArray(), c => c.Alive && c.Generation > 0);
    }

    /// <summary>
    /// FUN_1404ff410 / FUN_1404feed0: a squad that unloads keeps its dead in the record and destroys their bodies; they come back as corpses when it
    /// is made again.
    /// </summary>
    [Fact]
    public void An_unloaded_squad_keeps_its_dead_and_their_bodies_come_back_with_it()
    {
        using var world = SyntheticTown.World(5, 1, 3, new PopulationSettings { UnloadGraceSeconds = 10, CheckEveryTicks = 1 });
        world.RunTick();
        var table = world.Characters;
        var squad = world.Squads.All.First(s => s.Members.Count > 2);
        var fallen = squad.Members[1];
        table.Cold(fallen.Slot)!.DiedTick = world.Tick;
        int before = table.Count;
        world.Commands.Enqueue(new FocusCommand(new Vector3(-50000, 0, -50000)) { Tick = world.Tick });
        world.RunTicks(450);

        Assert.Equal(0, table.Count);   // the body went with the others

        world.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = world.Tick });
        world.RunTicks(170);

        Assert.Equal(before, table.Count);
        Assert.Single(Enumerable.Range(0, table.HighWater), i => table.Next[i].Alive && table.Cold(i) is { DiedTick: >= 0 });
    }

    /// <summary>
    /// The town unloads while its chaser is away: the chaser's squad is the only one kept in the world (pinned, one platoon), the others are stored.
    /// </summary>
    static (SimWorld World, PopulationSystem Population, CharacterId Chaser, int Total) UnloadedWithChaserAway()
    {
        var world = SyntheticTown.World(5, 1, 3, new PopulationSettings { UnloadGraceSeconds = 10, CheckEveryTicks = 1 });
        var population = (PopulationSystem)world.Systems[0];
        world.RunTick();
        var chaser = world.Squads.All.First().Members[0];
        int total = world.Characters.Count;
        var far = new Vector3(-50000, 0, -50000);
        world.Commands.Enqueue(new FocusCommand(far) { Tick = world.Tick });
        world.RunTick();
        world.Characters.Place(chaser.Slot, far, 0);
        world.RunTicks(350);
        return (world, population, chaser, total);
    }

    static int LivingOf(SimWorld world) =>
        world.Squads.All.Sum(s => s.Members.Count)
            + world.Platoons.All.Where(p => p.State != PlatoonState.Loaded && p.Stored is not null).Sum(p => p.Stored!.Members.Count);

    [Fact]
    public void A_town_that_unloads_keeps_only_the_squad_near_the_player_and_stores_the_rest()
    {
        var (world, population, chaser, total) = UnloadedWithChaserAway();
        using var _ = world;

        Assert.Equal(SiteStatus.Unloaded, population.StatusOf(0));

        var kept = Assert.Single(world.Squads.All);

        Assert.True(kept.Pinned);
        Assert.Equal(chaser, kept.Members[0]);

        var platoon = Assert.Single(world.Platoons.All, p => p.State == PlatoonState.Loaded);

        Assert.Equal((kept.Id, kept.PlatoonId), (platoon.SquadId, platoon.Id));
        Assert.True(world.Platoons.All.Count(p => p.State == PlatoonState.Unloaded && p.Stored is { Members.Count: > 0 }) >= 1);
        Assert.Equal(total, LivingOf(world));
    }

    [Fact]
    public void A_squad_walking_home_into_an_unloaded_town_unloads_cleanly_and_comes_back_with_the_town()
    {
        var (world, population, chaser, total) = UnloadedWithChaserAway();
        using var _ = world;
        world.Characters.Place(chaser.Slot, SyntheticTown.Centre, 0);   // home, in no active zone (the player is far)
        world.RunTicks(350);

        Assert.Equal(0, world.Characters.Count);
        Assert.Empty(world.Squads.All);
        Assert.DoesNotContain(world.Platoons.All, p => p.State == PlatoonState.Loaded);
        Assert.Equal(total, LivingOf(world));

        world.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = world.Tick });
        world.RunTicks(60);

        Assert.Equal(SiteStatus.Loaded, population.StatusOf(0));
        Assert.Equal(total, world.Characters.Count);
        Assert.Equal(total, LivingOf(world));
    }

    [Fact]
    public void A_housed_squads_prisoners_are_made_caged_in_its_home_held_without_a_sentence_and_trimmed_to_its_free_cages()
    {
        var function = SyntheticTown.Rec("53-t", Data.Fcs.FcsRecordType.BuildingFunctionality, "Cage");
        function.Ints["function"] = TownBuildings.CageFunction;
        var cage = SyntheticTown.Rec("54-t", Data.Fcs.FcsRecordType.Building, "Prisoner Cage");
        cage.References["functionality"] = [SyntheticTown.Ref("53-t")];
        var house = SyntheticTown.Rec("52-t", Data.Fcs.FcsRecordType.Building, "House");
        var captives = SyntheticTown.Rec("14-t", Data.Fcs.FcsRecordType.SquadTemplate, "Captives");
        captives.References["squad"] = [SyntheticTown.Ref("2-t", 3)];

        // One patrol, housed by its level-data template in a house with two cages; its template makes a captive squad of three.
        var db = SyntheticTown.Database(residentSquads: 1, extra: [function, cage, house, captives],
                changePatrol: p => p.References["prisoners"] = [SyntheticTown.Ref("14-t")]);
        var at = SyntheticTown.Centre + new Vector3(0, 0, 100);
        var zone = WorldLayout.ZoneOf(at.X, at.Z);
        string key = BuildingKey.Of(zone, "house");
        var home = new TownBuilding("house", "52-t", "house", at, 0, false, false, [])
        {
            Zone = zone, CanHouse = true, ResidentsTemplate = "10-t",
            Fixtures = [new TownFixture($"{key}/0", "54-t", true, at + new Vector3(10, 0, 0), 0),
                    new TownFixture($"{key}/1", "54-t", true, at + new Vector3(-10, 0, 0), 0)],
        };
        var data = PopulationData.Create(db, [new TownPlacement("inst", "30-t", SyntheticTown.Centre, null)], buildings: new Dictionary<string,
                List<TownBuilding>> { ["inst"] = [home] });
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        using var world = new Meitou.Simulation.World(new WorldSettings { Seed = 7, PublishSnapshots = false }, walk,
            SyntheticTown.Standard(data, walk, StandardParts.Population | StandardParts.Movement,
                    new PopulationSettings { CheckEveryTicks = 1 }).Systems);
        world.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = 0 });
        world.RunTicks(2);

        var patrol = world.Squads.All.Single(s => s.TemplateId == "10-t");

        Assert.Same(home, patrol.HomeBuilding);

        var held = world.Squads.All.Single(s => s.TemplateId == "14-t");

        Assert.Equal(2, held.Members.Count);   // three made, two free cages
        Assert.Equal(patrol.Faction, held.Faction);

        var table = world.Characters;
        var cages = held.Members.Select(m => table.Cold(m.Slot)!).Select(c => (c.IsImprisoned, c.Kidnapped, c.SentenceLength, c.InWhat)).ToList();

        Assert.All(cages, c => Assert.Equal((true, true, 0f), (c.IsImprisoned, c.Kidnapped, c.SentenceLength)));
        Assert.Equal([$"{key}/0", $"{key}/1"], cages.Select(c => c.InWhat).Order());
        Assert.All(cages, c => Assert.True(world.Fixtures.Find(c.InWhat!)!.Lock!.Locked));
    }

    /// <summary>
    /// World states answered from a set the test changes.
    /// </summary>
    internal sealed class FixedStates(params string[] holding) : IWorldStates
    {
        public HashSet<string> Holding { get; } = [.. holding];
        public bool IsTrue(string worldStateId) => Holding.Contains(worldStateId);
    }

    /// <summary>
    /// The synthetic town (30-t) with overrides, in this order: E (54-t, no conditions), A (50-t, state 91; residents a Patrol), C (52-t, 91 and 93),
    /// B (51-t, 91 and 92; faction Others 21-t, residents Faction folk; its own list B, A), D (53-t, 92 and 91). With 91 and 92 holding B wins, then
    /// A from B's list.
    /// </summary>
    internal static Data.GameDatabase OverrideDb(bool items = false, IReadOnlyList<Meitou.Data.Fcs.FcsRecord>? extra = null)
    {
        var r = SyntheticTown.Ref;
        Meitou.Data.Fcs.FcsRecord State(string id) => SyntheticTown.Rec(id, Data.Fcs.FcsRecordType.WorldEventState, id);
        Meitou.Data.Fcs.FcsRecord Town(string id, string name, string resident, Meitou.Data.Fcs.FcsReference[] states,
                params Meitou.Data.Fcs.FcsReference[] overrides)
        {
            var t = SyntheticTown.Rec(id, Data.Fcs.FcsRecordType.Town, name);
            t.Ints["type"] = 2;
            t.Floats["size radius"] = 300;
            t.Bools["residents override"] = true;
            t.References["residents"] = [r(resident, 1, 0)];
            t.References["world state"] = [.. states];
            if (overrides.Length > 0) t.References["override town"] = [.. overrides];
            return t;
        }
        var others = SyntheticTown.Rec("21-t", Data.Fcs.FcsRecordType.Faction, "Others");
        var a = Town("50-t", "A (override)", "10-t", [r("91-t", 1)]);
        var b = Town("51-t", "B (override)", "12-t", [r("91-t", 1), r("92-t", 1)], r("51-t"), r("50-t"));   // lists itself: never picked again
        b.References["faction"] = [r("21-t")];
        var c = Town("52-t", "C (override)", "10-t", [r("91-t", 1), r("93-t", 1)]);   // heavy, but 93 does not hold
        var d = Town("53-t", "D (override)", "10-t", [r("92-t", 1), r("91-t", 1)]);   // as heavy as B, later in the list
        var e = Town("54-t", "E (override)", "10-t", []);                              // no conditions: never
        return SyntheticTown.Database(items: items, extra: [others, State("91-t"), State("92-t"), State("93-t"), a, b, c, d, e, .. extra ?? []],
            changeTown: t => t.References["override town"] = [r("54-t"), r("50-t"), r("52-t"), r("51-t"), r("53-t")]);
    }

    /// <summary>
    /// <paramref name="made"/> answering world states from <paramref name="states"/>, its looks from <paramref name="looks"/>.
    /// </summary>
    internal static PopulationData WithStates(PopulationData made, IWorldStates states, IAppearanceSource? looks = null) => new()
    {
        Db = made.Db, Sites = made.Sites, Factions = made.Factions, PlayerFaction = made.PlayerFaction, Relations = made.Relations,
                SiteFactions = made.SiteFactions,
        States = states, Appearances = looks,
    };

    [Fact]
    public void A_town_takes_its_heaviest_qualifying_override_at_each_load_and_makes_its_residents()
    {
        // docs/game/factions-squads-towns.md "Town overrides". States 91 and 92 hold, 93 does not.
        var db = OverrideDb(items: true);
        var data = WithStates(SyntheticTown.Data(db), new FixedStates("91-t", "92-t"));
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        using var world = new Meitou.Simulation.World(new WorldSettings { Seed = 3, PublishSnapshots = false }, walk,
            SyntheticTown.Standard(data, walk, StandardParts.Population | StandardParts.Movement,
                    new PopulationSettings { CheckEveryTicks = 1, UnloadGraceSeconds = 1 }).Systems);
        void Visit()
        {
            world.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = world.Tick });
            world.RunTicks(170);   // past the unloaded platoons' 5 s activation dwell
        }
        void Leave()
        {
            world.Commands.Enqueue(new FocusCommand(new Vector3(-50000, 0, -50000)) { Tick = world.Tick });
            world.RunTicks(180);   // the block's 4 s, then the 1 s grace
        }
        var site = data.Sites[0];
        int Patrols() => world.Squads.All.Count(s => s.TemplateId == "10-t");

        // B (weight 2, first of the heaviest): its faction, the town's own name, its residents only.
        Visit();

        Assert.Equal("51-t", site.Replacement?.Id);
        Assert.Equal(("Testville", data.FactionIndex("21-t")), (site.Town.Name, data.SiteFactions[0]));
        Assert.Equal((1, 0), (world.Squads.All.Count(s => s.TemplateId == "12-t"), Patrols()));

        // Next load: B's own list gives A (B itself is skipped); A names no faction, so B's stays; A's residents are made though the town was made;
        // loose items go.
        Leave();
        world.GroundItems.Drop(data.Items.Create("70-t")!, SyntheticTown.Centre, 0);
        Visit();

        Assert.Equal(("50-t", data.FactionIndex("21-t")), (site.Replacement?.Id, data.SiteFactions[0]));
        Assert.Equal(1, Patrols());
        Assert.Equal(0, world.GroundItems.Count);

        // A has no overrides: nothing changes, nothing more is made.
        Leave();
        Visit();

        Assert.Equal(("50-t", 1), (site.Replacement?.Id, Patrols()));
    }

    /// <summary>
    /// Holds every look being made until <see cref="Open"/> is set, so a background build stays in flight.
    /// </summary>
    sealed class GatedLooks : IAppearanceSource
    {
        public ManualResetEventSlim Open { get; } = new(false);
        public Meitou.Data.Characters.CharacterAppearance? Create(string recordId, string? factionId, int seed)
        {
            Open.Wait(TimeSpan.FromSeconds(30));
            return null;
        }
    }

    [Fact]
    public void A_replaced_town_whose_residents_were_never_made_makes_them_at_the_next_load_without_a_new_choice()
    {
        // The original's updatedTownOverrides (docs/game/factions-squads-towns.md "Town overrides"): the build of B's residents is dropped as the
        // town unloads; next time B's residents are made and B's own list (which would give A) is not looked at.
        var looks = new GatedLooks();
        var data = WithStates(SyntheticTown.Data(OverrideDb()), new FixedStates("91-t", "92-t"), looks);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        using var world = new Meitou.Simulation.World(new WorldSettings { Seed = 3, PublishSnapshots = false }, walk,
            SyntheticTown.Standard(data, walk, StandardParts.Population | StandardParts.Movement,
                new PopulationSettings { CheckEveryTicks = 1, UnloadGraceSeconds = 1, Background = true }).Systems);
        using var population = world.Systems.OfType<PopulationSystem>().Single();
        world.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = 0 });
        world.RunTicks(2);

        Assert.Equal(("51-t", SiteStatus.Loading), (data.Sites[0].Replacement?.Id, population.StatusOf(0)));

        world.Commands.Enqueue(new FocusCommand(new Vector3(-50000, 0, -50000)) { Tick = world.Tick });
        world.RunTicks(180);   // the block's 4 s, then the 1 s grace

        Assert.Equal(SiteStatus.Unloaded, population.StatusOf(0));

        looks.Open.Set();
        world.RunTicks(2);

        Assert.Equal(0, world.Squads.Count);

        world.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = world.Tick });

        Assert.True(TestWaits.TickUntil(world, () => population.StatusOf(0) == SiteStatus.Loaded), "the town never loaded");
        Assert.Equal("51-t", data.Sites[0].Replacement?.Id);
        Assert.Equal(["12-t"], world.Squads.All.Select(s => s.TemplateId));
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
