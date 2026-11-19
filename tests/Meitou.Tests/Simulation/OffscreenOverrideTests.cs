using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using static Meitou.Tests.Save.SaveSamples;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Towns that change version away from their zone's load (docs/simulation.md "Town overrides", "Towns change far away"; docs/modding.md "Town
/// overrides"), on the synthetic override town (<see cref="PopulationTests.OverrideDb"/>: with 91 and 92 holding it becomes B, then A from B's list).
/// </summary>
public class OffscreenOverrideTests
{
    static readonly Vector3 Away = new(-50000, 0, -50000);

    static SimWorld World(PopulationData data, bool offscreen = true, params ITickSystem[] extra)
    {
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var settings = new PopulationSettings { CheckEveryTicks = 1, UnloadGraceSeconds = 1, OffscreenOverrides = offscreen };
        var world = new SimWorld(new WorldSettings { Seed = 3, PublishSnapshots = false }, walk,
            SyntheticTown.Standard(data, walk, StandardParts.Population | StandardParts.Movement, settings).Systems);
        foreach (var system in extra) world.AddSystem(system);
        return world;
    }

    static PopulationSystem Population(SimWorld world) => world.Systems.OfType<PopulationSystem>().Single();

    static void Focus(SimWorld world, Vector3 at, int ticks)
    {
        world.Commands.Enqueue(new FocusCommand(at) { Tick = world.Tick });
        world.RunTicks(ticks);
    }

    /// <summary>
    /// The town loaded (past the unloaded platoons' 5 s activation dwell), then left until it unloads (the block's 4 s, then the 1 s grace).
    /// </summary>
    static void VisitAndLeave(SimWorld world, Vector3 at)
    {
        Focus(world, at, 170);
        Focus(world, Away, 180);
    }

    static string[] Templates(SimWorld world) => [.. world.Squads.All.Select(s => s.TemplateId).Order(StringComparer.Ordinal)];

    [Fact]
    public void An_unloaded_town_changes_at_the_next_look_in_meitou_mode_and_only_at_its_load_in_faithful_mode()
    {
        foreach (bool offscreen in new[] { true, false })
        {
            var states = new PopulationTests.FixedStates();
            var data = PopulationTests.WithStates(SyntheticTown.Data(PopulationTests.OverrideDb()), states);
            using var world = World(data, offscreen);
            var population = Population(world);
            world.RunTicks(2);
            states.Holding.UnionWith(["91-t", "92-t"]);
            world.RunTicks(1);

            Assert.Equal(offscreen ? "51-t" : null, data.Sites[0].Replacement?.Id);

            // A chain moves one step per look: B's list gives A, which has none.
            world.RunTicks(5);

            Assert.Equal(offscreen ? ("50-t", 2, true) : (null, 0, false),
                (data.Sites[0].Replacement?.Id, population.OffscreenSwitches, population.OverridePending(0)));
        }
    }

    [Fact]
    public void A_changed_town_makes_the_new_residents_once_and_its_old_garrison_does_not_come_back()
    {
        var states = new PopulationTests.FixedStates();
        var data = PopulationTests.WithStates(SyntheticTown.Data(PopulationTests.OverrideDb()), states);
        using var world = World(data);
        VisitAndLeave(world, SyntheticTown.Centre);

        Assert.Contains(world.Platoons.All, p => p.Origin == 0 && p.Pinned);

        // Away, the town becomes B then A; its stored squads of the own version are gone at once.
        states.Holding.UnionWith(["91-t", "92-t"]);
        world.RunTicks(3);

        Assert.Equal("50-t", data.Sites[0].Replacement?.Id);
        Assert.DoesNotContain(world.Platoons.All, p => p.Origin == 0 && p.Pinned);

        // The next load makes A's residents (one Patrol) and only those; the one after makes nothing more.
        Focus(world, SyntheticTown.Centre, 170);

        Assert.Equal(["10-t"], Templates(world));
        Assert.False(Population(world).OverridePending(0));

        Focus(world, Away, 180);
        Focus(world, SyntheticTown.Centre, 170);

        Assert.Equal(["10-t"], Templates(world));
    }

    [Fact]
    public void A_loaded_town_waits_for_its_next_load_and_a_held_town_or_one_of_the_players_faction_never_changes_by_itself()
    {
        // Loaded: no change until it has unloaded.
        var states = new PopulationTests.FixedStates();
        var data = PopulationTests.WithStates(SyntheticTown.Data(PopulationTests.OverrideDb()), states);
        using (var world = World(data))
        {
            Focus(world, SyntheticTown.Centre, 170);
            states.Holding.UnionWith(["91-t", "92-t"]);
            world.RunTicks(5);

            Assert.Null(data.Sites[0].Replacement);

            Focus(world, Away, 185);

            Assert.NotNull(data.Sites[0].Replacement);
        }

        // Held: neither the look nor the mod's check moves it; the mod's own choice does.
        data.ClearReplacements();
        states.Holding.Clear();
        using (var world = World(data))
        {
            var population = Population(world);
            Assert.True(population.HoldTown(0, true));
            world.RunTicks(2);
            states.Holding.UnionWith(["91-t", "92-t"]);
            world.RunTicks(5);

            Assert.Equal(((string?)null, false), (data.Sites[0].Replacement?.Id, population.CheckOverrides(world, 0)));
            Assert.True(population.ApplyTownVersion(world, 0, "53-t"));
            Assert.Equal("53-t", data.Sites[0].Replacement?.Id);
        }

        // The player's faction's town: never.
        data.ClearReplacements();
        states.Holding.Clear();
        var made = SyntheticTown.Data(PopulationTests.OverrideDb());
        var players = new PopulationData
        {
            Db = made.Db, Sites = made.Sites, Factions = made.Factions, PlayerFaction = made.FactionIndex("20-t"), Relations = made.Relations,
            SiteFactions = made.SiteFactions, States = states,
        };
        using (var world = World(players))
        {
            world.RunTicks(2);
            states.Holding.UnionWith(["91-t", "92-t"]);
            world.RunTicks(5);

            Assert.Equal(((string?)null, false), (players.Sites[0].Replacement?.Id, Population(world).CheckOverrides(world, 0)));
        }
    }

    [Fact]
    public void A_two_way_pair_moves_once_per_change_of_its_states_answers()
    {
        // The town lists P; P lists Q and Q lists P, all on state 91.
        var r = SyntheticTown.Ref;
        FcsRecord Town(string id, string name, string next)
        {
            var t = SyntheticTown.Rec(id, FcsRecordType.Town, name);
            t.Ints["type"] = 2;
            t.Floats["size radius"] = 300;
            t.References["world state"] = [r("91-t", 1)];
            t.References["override town"] = [r(next)];
            return t;
        }
        var db = SyntheticTown.Database(extra: [SyntheticTown.Rec("91-t", FcsRecordType.WorldEventState, "91-t"), Town("55-t", "P", "56-t"),
                Town("56-t", "Q", "55-t")],
            changeTown: t => t.References["override town"] = [r("55-t")]);
        var states = new PopulationTests.FixedStates();
        var data = PopulationTests.WithStates(SyntheticTown.Data(db), states);
        using var world = World(data);
        var population = Population(world);
        world.RunTicks(2);
        states.Holding.Add("91-t");
        world.RunTicks(10);

        // Own → P → Q, and Q does not go back to P.
        Assert.Equal(("56-t", 2), (data.Sites[0].Replacement?.Id, population.OffscreenSwitches));

        // The answer changes twice (91 off, nothing qualifies; 91 on): Q → P once more.
        states.Holding.Remove("91-t");
        world.RunTicks(2);
        states.Holding.Add("91-t");
        world.RunTicks(10);

        Assert.Equal(("55-t", 3), (data.Sites[0].Replacement?.Id, population.OffscreenSwitches));
    }

    [Fact]
    public void A_town_changed_back_to_its_own_record_makes_its_own_residents_afresh()
    {
        // No state holds (states with no conditions would answer true live, and the fresh town would take an override at its load).
        var data = PopulationTests.WithStates(SyntheticTown.Data(PopulationTests.OverrideDb()), new PopulationTests.FixedStates());
        string[] own;
        using (var fresh = World(data))
        {
            Focus(fresh, SyntheticTown.Centre, 170);
            own = Templates(fresh);
        }

        using var world = World(data);
        var population = Population(world);
        VisitAndLeave(world, SyntheticTown.Centre);

        Assert.True(population.ApplyTownVersion(world, 0, "51-t"));
        Assert.True(population.ApplyTownVersion(world, 0, null));
        Assert.False(population.ApplyTownVersion(world, 0, "30-t"));   // already its own record
        Assert.Equal(((string?)null, true), (data.Sites[0].Replacement?.Id, population.OverridePending(0)));

        Focus(world, SyntheticTown.Centre, 170);

        Assert.Equal(own, Templates(world));
    }

    /// <summary>
    /// Hears every town change and moves each homeless platoon to <paramref name="host"/>.
    /// </summary>
    sealed class Rehomer(int host) : ITownSwitchObserver
    {
        public List<TownSwitch> Heard { get; } = [];

        public void TownSwitched(SimWorld world, in TownSwitch change)
        {
            Heard.Add(change);
            var population = world.Systems.OfType<PopulationSystem>().Single();
            foreach (int id in change.Homeless) Assert.True(population.RehomePlatoon(world, id, host));
        }
    }

    [Fact]
    public void A_mod_hears_a_town_change_and_moves_the_homeless_squads_to_another_town_as_guests()
    {
        var far = SyntheticTown.Rec("31-t", FcsRecordType.Town, "Faraway");
        far.Ints["type"] = 2;
        far.Floats["size radius"] = 300;
        far.Floats["town radius mult"] = 1;
        far.References["faction"] = [SyntheticTown.Ref("20-t")];
        var states = new PopulationTests.FixedStates();
        var data = PopulationTests.WithStates(SyntheticTown.Data(PopulationTests.OverrideDb(extra: [far])), states);
        var rehomer = new Rehomer(1);
        using var world = World(data, extra: rehomer);
        VisitAndLeave(world, SyntheticTown.Centre);
        int[] garrison =
        [
            .. world.Platoons.All
                .Where(p => p.Origin == 0 && p.Pinned)
                .Select(p => p.Id)
                .Order(),
        ];

        // Own → B: the garrison is homeless, moved to Faraway as guests; B → A finds nobody left.
        states.Holding.UnionWith(["91-t", "92-t"]);
        world.RunTicks(3);

        Assert.Equal(2, rehomer.Heard.Count);
        Assert.Equal(((string?)null, "51-t", true), (rehomer.Heard[0].From, rehomer.Heard[0].To, rehomer.Heard[0].Offscreen));
        Assert.Equal(garrison, rehomer.Heard[0].Homeless.ToArray());
        Assert.Empty(rehomer.Heard[1].Homeless);
        Assert.All(garrison, id => Assert.Equal((1, true), (world.Platoons.Find(id)!.Origin, world.Platoons.Find(id)!.Guest)));

        // At Faraway they come back round its centre.
        Focus(world, SyntheticTown.Far, 170);
        var centre = new Vector2(SyntheticTown.Far.X, SyntheticTown.Far.Z);
        foreach (int id in garrison)
        {
            var squad = world.Squads.Find(world.Platoons.Find(id)!.SquadId)!;
            Assert.True(Vector2.Distance(new Vector2(squad.Position.X, squad.Position.Z), centre) < 600, $"squad {squad.Id} far from Faraway");
        }
    }

    [Fact]
    public void A_save_between_the_change_and_the_load_keeps_the_rebuild_and_not_the_old_garrison()
    {
        var states = new PopulationTests.FixedStates();
        var data = PopulationTests.WithStates(SyntheticTown.Data(PopulationTests.OverrideDb()), states);
        using var a = World(data);
        VisitAndLeave(a, SyntheticTown.Centre);
        states.Holding.UnionWith(["91-t", "92-t"]);
        a.RunTicks(3);

        Assert.Equal("50-t", data.Sites[0].Replacement?.Id);

        TownPlacement[] placements = [new TownPlacement("inst", "30-t", SyntheticTown.Centre, null)];
        var options = new SaveCaptureOptions { Placements = placements };
        var save = SaveCapture.Capture(a, data, new SaveClock(3, 7, 45), null, options, SaveCapture.PrepareBase(data, null, options));

        Assert.True(save.Towns.Single(t => t.Instance == "inst").Record.Bools[PopulationSystem.OverridePendingKey]);

        string folder = Path.Combine(Temp("offscreen-override"), QuickSave.Name);
        try
        {
            save.Write(folder);
            data.ClearReplacements();
            using var b = World(data);
            SaveLoader.Load(b, SaveGame.Load(folder), data);
            b.RunTicks(1);

            Assert.Equal(("50-t", true), (data.Sites[0].Replacement?.Id, Population(b).OverridePending(0)));
            Assert.DoesNotContain(b.Platoons.All, p => p.Origin == 0 && p.Pinned);

            Focus(b, SyntheticTown.Centre, 170);

            Assert.Equal(["10-t"], Templates(b));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }
}
