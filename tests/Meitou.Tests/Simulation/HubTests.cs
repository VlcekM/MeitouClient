using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation;
using Xunit.v3;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>The Hub of the installed game: residents by the template rules, where they stand, and what a tick costs.</summary>
[Slow]
public class HubTests
{
    static (GameDatabase Db, PopulationData Data, TownSite Hub)? cached;
    static readonly Lock gate = new();

    static (GameDatabase Db, PopulationData Data, TownSite Hub) Load()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        lock (gate)
        {
            if (cached is { } c) return c;
            var db = InstallData.BaseGame!;
            var levels = InstallData.Levels!;
            var data = PopulationData.Create(db, levels.Towns());
            var hub = data.Sites.Single(s => s.Town.Name == "The Hub");
            return (cached = (db, data, hub)).Value;
        }
    }

    static SimWorld Make(PopulationData data, TownSite hub, int threads, ulong seed = 1, float multiplier = 1)
    {
        var walk = new OpenGroundWalkability((_, _) => 300);
        var world = new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = false }, walk,
            [new PopulationSystem(data, new PopulationSettings { CheckEveryTicks = 1, SquadSizeMultiplier = multiplier }), new MovementSystem(new PathService(walk, true))]);
        world.Commands.Enqueue(new FocusCommand(hub.Position) { Tick = 0 });
        return world;
    }

    [Fact]
    public void The_hubs_residents_follow_the_template_rules_and_stand_in_the_town()
    {
        var (db, data, hub) = Load();
        Assert.Equal(TownType.Town, hub.Town.Type);
        using var world = Make(data, hub, 1);
        world.RunTick();
        var faction = data.Factions.First(f => f.Id == hub.Town.Faction);
        var entries = PopulationSystem.ResidentEntries(hub.Town, faction);
        Assert.NotEmpty(entries);

        var centre = new Vector2(hub.Position.X, hub.Position.Z);
        float reach = hub.Town.SizeRadius * hub.Town.TownRadiusMult * 0.6f + 8 * 3 + 3 * 8;   // the placement radius plus the layout rows
        int expectedSquads = 0;
        foreach (var e in entries)
        {
            var t = SquadTemplate.From(db.Find(e.Id)!);
            int made = world.Squads.All.Count(s => s.TemplateId == t.Id);
            Assert.True(made <= e.V0, $"{t.Name}: {made} squads for a count of {e.V0}");
            expectedSquads += e.V0;
            foreach (var s in world.Squads.All.Where(s => s.TemplateId == t.Id))
            {
                var (lo, hi) = Bounds(t);
                Assert.InRange(s.Members.Count, lo, hi);
                Assert.Equal(hub.Index, s.Town);
                Assert.True(Vector2.Distance(new(s.Position.X, s.Position.Z), centre) <= hub.Town.SizeRadius * hub.Town.TownRadiusMult * 0.6f + 0.1f);
            }
        }
        expectedSquads += PopulationSystem.BarEntries(hub.Town, faction).Sum(e => e.V0);   // bar squads, each present by its chance
        Assert.True(world.Squads.Count > 0 && world.Squads.Count <= expectedSquads);
        foreach (var c in world.Characters.Previous)
        {
            if (!c.Alive) continue;
            Assert.True(Vector2.Distance(new(c.Position.X, c.Position.Z), centre) <= reach, "a resident stands within the town");
        }
        Assert.Equal(world.Squads.All.Sum(s => s.Members.Count), world.Characters.Count);
    }

    /// <summary>The fewest and most members a template can make at multiplier 1 (leader, random picks, squad, squad2, animals).</summary>
    static (int Min, int Max) Bounds(SquadTemplate t)
    {
        int lo = t.Leader is null ? 0 : 1, hi = lo;
        int picks = t.ChooseFrom.Count > 0 ? Math.Max(t.NumRandomChars, t.NumRandomCharsMax) : 0;
        hi += picks;
        foreach (var e in t.Squad.Concat(t.Squad2).Concat(t.Animals).Concat(t.Animals2))
        {
            if (e.V1 is 0 or 100) { lo += e.V0; hi += e.V0; }
            else { lo += e.V0; hi += Math.Max(e.V0, e.V1); }
        }
        return (0, hi);   // gated or missing records can only lower the count
    }

    [Fact]
    public void The_hubs_residents_do_not_depend_on_the_thread_count()
    {
        var (_, data, hub) = Load();
        var hashes = new List<ulong>();
        foreach (int threads in new[] { 1, 4, 16 })
        {
            using var world = Make(data, hub, threads);
            world.RunTicks(300);
            hashes.Add(world.StateHash());
        }
        Assert.Single(hashes.Distinct());
    }

    [Fact]
    [Bench]
    public void Tick_cost_of_the_hub_is_recorded()
    {
        var (_, data, hub) = Load();
        foreach (var (threads, multiplier) in new[] { (1, 1f), (4, 1f), (1, 10f), (4, 10f), (8, 10f) })
        {
            using var world = Make(data, hub, threads, 1, multiplier);
            world.RunTicks(60);   // load and settle
            var watch = Stopwatch.StartNew();
            const int ticks = 600;
            world.RunTicks(ticks);
            double ms = watch.Elapsed.TotalMilliseconds / ticks;
            // 30 ticks per real second at 1x, 150 at 5x.
            TestContext.Current.TestOutputHelper!.WriteLine($"BENCH hub x{multiplier}: {world.Characters.Count} characters, {world.Squads.Count} squads, {threads} thread(s): {ms:0.000} ms/tick, " +
                $"1x {ms * 30:0.0} ms per real second, 5x {ms * 150:0.0} ms per real second");
            Assert.True(ms < 50, $"{ms} ms per tick");   // a loose bound: the machine is shared with the other tests
        }
    }
}
