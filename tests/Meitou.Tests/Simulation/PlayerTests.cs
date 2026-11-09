using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;
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
        var systems = SyntheticTown.Standard(data, walk, StandardParts.Population | StandardParts.Player | StandardParts.Movement,
                new PopulationSettings { CheckEveryTicks = 1, UnloadGraceSeconds = 5 });
        var population = systems.Population!;
        var world = new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = false }, walk, systems.Systems);
        var squad = population.StartPlayer(world, NewGameStart.From(db.Find("40-t")!));
        if (!residents) world.Commands.Enqueue(new FocusCommand(new Vector3(-90000, 0, -90000)) { Tick = 0 });   // no town in reach
        return new Game(world, population, squad);
    }

    internal static List<ulong> Scripted(int threads)
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

        Assert.Equal(6, hashes.Distinct().Count());   // the state moves between every checkpoint

        return hashes;
    }

    [Fact]
    public void When_the_selected_character_dies_the_first_living_one_of_the_player_is_selected()
    {
        using var g = Start();
        var first = g.Squad.Members[0];
        g.World.RunTicks(2);

        Assert.Equal([first], g.World.Player.Selection);

        g.World.Characters.Cold(first.Slot)!.DiedTick = g.World.Tick;
        g.World.RunTicks(2);
        var chosen = Assert.Single(g.World.Player.Selection);

        Assert.NotEqual(first, chosen);
        Assert.True(g.World.Characters.Cold(chosen.Slot) is { IsPlayer: true, DiedTick: < 0 });
    }
}
