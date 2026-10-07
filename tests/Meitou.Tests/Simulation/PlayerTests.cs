using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class PlayerTests
{
    sealed record Game(SimWorld World, PopulationSystem Population, Squad Squad) : IDisposable
    {
        public void Dispose() => World.Dispose();
        public CharacterId Leader => Squad.Leader;
        public Vector3 Where(CharacterId id) => World.Characters.Previous[id.Slot].Position;
    }

    static Game Start(int threads = 1, bool residents = false, ulong seed = 4)
    {
        var db = SyntheticTown.Database();
        var data = SyntheticTown.Data(db);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var population = new PopulationSystem(data, new PopulationSettings { CheckEveryTicks = 1, UnloadGraceSeconds = 5 });
        var world = new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = false }, walk,
            [population, new PlayerSystem(), new MovementSystem(new PathService(walk, true))]);
        var squad = population.StartPlayer(world, NewGameStart.From(db.Find("40-t")!));
        if (!residents) world.Commands.Enqueue(new FocusCommand(new Vector3(-90000, 0, -90000)) { Tick = 0 });   // no town in reach
        return new Game(world, population, squad);
    }

    [Fact]
    public void A_new_game_starts_the_players_squad_at_the_start_town()
    {
        using var g = Start();
        var w = g.World;
        Assert.True(w.Player.Exists);
        Assert.Equal(500, w.Player.Money);
        Assert.Equal(g.Population.Data.Factions.ToList().FindIndex(f => f.Name == "Nameless"), w.Player.Faction);
        Assert.Equal(g.Squad.Id, w.Player.Squad);
        // The listed boss, then the template's three guards: one squad of four, the boss leading.
        Assert.Equal(4, g.Squad.Members.Count);
        Assert.Equal("3-t", w.Characters.Cold(g.Leader.Slot)!.RecordId);
        var centre = new Vector2(SyntheticTown.Centre.X, SyntheticTown.Centre.Z);
        foreach (var m in g.Squad.Members)
        {
            var cold = w.Characters.Cold(m.Slot)!;
            Assert.True(cold.IsPlayer);
            Assert.Equal(w.Player.Faction, cold.Faction);
            var p = w.Characters.Previous[m.Slot];
            Assert.Equal((byte)CharacterTask.Idle, p.Task);
            Assert.True(Vector2.Distance(new(p.Position.X, p.Position.Z), centre) < 300 / 3f + 30);
        }
        Assert.Empty(w.Player.Selection);
    }

    [Fact]
    public void Selection_takes_the_players_characters_only_and_adds_with_shift()
    {
        using var g = Start(residents: true);
        var w = g.World;
        w.RunTick();   // the town's residents load
        var stranger = w.Squads.All.First(s => s.Id != g.Squad.Id).Leader;
        var mine = g.Squad.Members;
        w.Commands.Enqueue(new SelectCommand([mine[0], stranger]) { Tick = w.Tick });
        w.RunTick();
        Assert.Equal([mine[0]], w.Player.Selection);
        w.Commands.Enqueue(new SelectCommand([mine[1], mine[0]], Additive: true) { Tick = w.Tick });
        w.RunTick();
        Assert.Equal([mine[0], mine[1]], w.Player.Selection);
        w.Commands.Enqueue(new SelectCommand([mine[2]]) { Tick = w.Tick });
        w.RunTick();
        Assert.Equal([mine[2]], w.Player.Selection);
        w.Commands.Enqueue(new SelectCommand([]) { Tick = w.Tick });
        w.RunTick();
        Assert.Empty(w.Player.Selection);
    }

    [Fact]
    public void An_order_with_no_characters_moves_the_selection_and_queued_orders_follow_in_turn()
    {
        using var g = Start();
        var w = g.World;
        var me = g.Leader;
        var other = g.Squad.Members[1];
        var start = g.Where(me);
        w.Commands.Enqueue(new SelectCommand([me]) { Tick = 0 });
        var a = new Vector3(start.X + 200, 0, start.Z);
        var b = new Vector3(start.X + 200, 0, start.Z + 200);
        w.Commands.Enqueue(new MoveOrder([], a) { Tick = 1 });
        w.Commands.Enqueue(new MoveOrder([], b) { Tick = 1, Queued = true });
        var otherStart = g.Where(other);
        w.RunTicks(30 * 4);
        Assert.InRange(g.Where(me).X - start.X, 20, 200);
        Assert.Single(w.Characters.Cold(me.Slot)!.OrderQueue);   // still on the first leg
        w.RunTicks(30 * 20);
        var end = g.Where(me);
        Assert.InRange(Vector2.Distance(new(end.X, end.Z), new(b.X, b.Z)), 0, MovementSystem.ArriveRadius + 1);
        Assert.Equal((byte)CharacterTask.Idle, w.Characters.Previous[me.Slot].Task);
        // The others were not selected, so they stayed (a few units of separation drift aside).
        Assert.True(Vector3.Distance(otherStart, g.Where(other)) < 30);
    }

    [Fact]
    public void A_plain_order_replaces_the_queue_and_stop_drops_everything()
    {
        using var g = Start();
        var w = g.World;
        var me = g.Leader;
        var start = g.Where(me);
        w.Commands.Enqueue(new MoveOrder([me], new(start.X + 1000, 0, start.Z)) { Tick = 0 });
        w.Commands.Enqueue(new MoveOrder([me], new(start.X + 1000, 0, start.Z + 500)) { Tick = 0, Queued = true });
        w.RunTicks(10);
        w.Commands.Enqueue(new MoveOrder([me], new(start.X - 100, 0, start.Z)) { Tick = w.Tick });
        w.RunTicks(2);
        Assert.Empty(w.Characters.Cold(me.Slot)!.OrderQueue);
        w.Commands.Enqueue(new SelectCommand([me]) { Tick = w.Tick });
        w.Commands.Enqueue(new StopCommand([]) { Tick = w.Tick });
        w.RunTicks(3);
        var stopped = g.Where(me);
        w.RunTicks(60);
        Assert.True(Vector3.Distance(stopped, g.Where(me)) < 8, "it stands (separation from its squad mates may nudge it)");
        Assert.Equal((byte)CharacterTask.Idle, w.Characters.Previous[me.Slot].Task);
    }

    [Fact]
    public void The_players_characters_keep_their_zones_active_whatever_the_camera_does()
    {
        using var g = Start(residents: true);
        var w = g.World;
        w.RunTick();
        Assert.Equal(SiteStatus.Loaded, g.Population.StatusOf(0));
        // The camera goes far away; the squad is still at the town (without a player the same camera would unload it: PopulationTests).
        w.Commands.Enqueue(new FocusCommand(new Vector3(-50000, 0, -50000)) { Tick = w.Tick });
        w.RunTicks(30 * 20);
        Assert.Equal(SiteStatus.Loaded, g.Population.StatusOf(0));
    }

    static List<ulong> Scripted(int threads)
    {
        using var g = Start(threads, residents: true, seed: 8);
        var w = g.World;
        var m = g.Squad.Members;
        var at = g.Where(m[0]);
        void Say(SimCommand c) => w.Commands.Enqueue(c);
        Say(new SelectCommand(m) { Tick = 5 });
        Say(new MoveOrder([], new(at.X + 300, 0, at.Z)) { Tick = 6 });
        Say(new MoveOrder([], new(at.X + 300, 0, at.Z + 300)) { Tick = 6, Queued = true });
        Say(new SelectCommand([m[1]]) { Tick = 100 });
        Say(new MoveOrder([], new(at.X - 200, 0, at.Z - 100)) { Tick = 101 });
        Say(new SelectCommand([m[0], m[2]]) { Tick = 150 });
        Say(new StopCommand([]) { Tick = 160 });
        Say(new SelectCommand([m[3]], Additive: true) { Tick = 200 });
        Say(new MoveOrder([], new(at.X, 0, at.Z + 250)) { Tick = 201 });
        var hashes = new List<ulong>();
        int ran = 0;
        foreach (int target in new[] { 5, 50, 120, 180, 300, 700 })
        {
            w.RunTicks(target - ran);
            ran = target;
            hashes.Add(w.StateHash());
        }
        return hashes;
    }

    [Fact]
    public void A_scripted_game_of_selects_and_orders_hashes_the_same_at_1_4_and_16_threads()
    {
        var one = Scripted(1);
        Assert.Equal(one, Scripted(4));
        Assert.Equal(one, Scripted(16));
        Assert.Equal(one, Scripted(1));
        Assert.Equal(6, one.Distinct().Count());
    }
}
