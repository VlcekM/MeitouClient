using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class RoamingTests
{
    static (SimWorld World, PopulationSystem Population) Start(int threads = 1, ulong seed = 3)
    {
        var db = SyntheticTown.Database(roaming: true);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var population = new PopulationSystem(SyntheticTown.Data(db),
                new PopulationSettings { CheckEveryTicks = 15, UnloadGraceSeconds = 600, RoamUnloadGraceSeconds = 5 });
        var world = new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = false }, walk,
            [population, new MovementSystem(new PathService(walk, true))]);
        world.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = 0 });
        return (world, population);
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

        // The camera moves a zone west of the first town (its block still holds the town): the roamers walk out of the block, then only their
        // stand-ins go on.
        w.Commands.Enqueue(new FocusCommand(new Vector3(-2000, 0, 2000)) { Tick = w.Tick });
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
        Assert.All(travellers, p => Assert.Contains(Data.World.WorldLayout.ZoneOf(p.Position.X, p.Position.Y), population.ActiveZones));
    }

    /// <summary>
    /// A platoon with 2 or more members whose zone is left puts each imprisoned member in a squad and platoon of its own first
    /// (<c>FUN_14079c1c0</c>).
    /// </summary>
    [Fact]
    public void An_imprisoned_member_is_split_into_a_platoon_of_its_own_when_its_platoon_unloads()
    {
        var (w, _) = Start();
        using var world = w;
        w.RunTicks(700);
        var platoon = w.Platoons.All.First();
        var squad = w.Squads.Find(platoon.SquadId)!;
        var prisoner = squad.Members[^1];
        w.Characters.Cold(prisoner.Slot)!.InSomething = Meitou.Simulation.Locks.HeldState.Imprisoned;
        int platoons = w.Platoons.Count;
        w.Commands.Enqueue(new FocusCommand(new Vector3(-2000, 0, 2000)) { Tick = w.Tick });
        for (int i = 0; i < 40 && platoon.State != PlatoonState.Unloaded; i++) w.RunTicks(150);

        Assert.Equal(PlatoonState.Unloaded, platoon.State);
        Assert.Equal(platoons + 1, w.Platoons.Count);

        var split = w.Platoons.All.Single(p => p.Package == "CAPTURED_NPC");
        var own = w.Squads.Find(split.SquadId)!;

        Assert.Equal([prisoner], own.Members);
        Assert.Equal(own.Id, w.Characters.Cold(prisoner.Slot)!.SquadId);
        Assert.DoesNotContain(prisoner, squad.Members);
    }

    internal static List<ulong> Scripted(int threads)
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

        Assert.Equal(6, hashes.Distinct().Count());   // the state moves between every checkpoint

        return hashes;
    }
}
