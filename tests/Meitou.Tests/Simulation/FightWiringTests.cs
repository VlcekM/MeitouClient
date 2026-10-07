using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>Combat wired into the game's system order: pursuit through the path service, the body system's medical tick, retaliation.</summary>
public class FightWiringTests
{
    static (SimWorld World, CombatSystem Combat, CharacterId A, CharacterId B, CharacterId C) Fight(ulong seed, int threads, float distance = 120)
    {
        var combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, DuelFixture.Lengths, new CombatOptions { SelfApproach = false, TickMedical = false, RecordLog = true });
        var walk = new OpenGroundWalkability((_, _) => 150);
        var world = new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = false, MinPartitionSize = 1 }, walk,
            [new PursuitSystem(combat), new MovementSystem(new PathService(walk, true)), new BodySystem(BaseGame), combat, new RetaliationSystem(combat)]);
        var squad = new Squad { Id = world.Squads.NextId(), TemplateId = "t" };
        world.Squads.Add(squad);
        CharacterId Make(string name, Vector3 at, bool player)
        {
            var c = new Combatant { Name = name };
            var stats = c.MakeStats();
            var cold = new CharacterCold { Name = name, Stats = stats, Race = Human, Medical = MedicalState.Create(Human, stats.Strength), Fighter = c.MakeFighter(), IsPlayer = player, SquadId = player ? -1 : squad.Id };
            return world.Characters.Spawn(new CharacterHot { Position = at, Health = 100, MaxSpeed = 60, WalkSpeed = 15, Mode = SpeedMode.Free }, cold, 0);
        }
        var a = Make("player", new Vector3(1000, 150, 1000), true);
        var b = Make("bandit", new Vector3(1000 + distance, 150, 1000), false);
        var c = Make("bandit mate", new Vector3(1000 + distance + 40, 150, 1030), false);
        squad.Members.AddRange([b, c]);
        world.Commands.Enqueue(new AttackOrder([a], b) { Tick = 0 });
        return (world, combat, a, b, c);
    }

    [Fact]
    public void An_attacker_walks_to_its_target_fights_and_the_attacked_and_their_mate_retaliate()
    {
        var (world, combat, a, b, c) = Fight(5, 1);
        using var _ = world;
        var table = world.Characters;
        float start = Vector3.Distance(table.Previous[a.Slot].Position, table.Previous[b.Slot].Position);
        world.RunTicks(120);
        Assert.True(Vector3.Distance(table.Previous[a.Slot].Position, table.Previous[b.Slot].Position) < start - 20, "walked towards the target");
        int ticks = 120;
        while (ticks < 9000 && !table.Cold(a.Slot)!.Medical!.Incapacitated && !table.Cold(b.Slot)!.Medical!.Incapacitated) { world.RunTick(); ticks++; }
        Assert.True(table.Cold(a.Slot)!.Medical!.Incapacitated || table.Cold(b.Slot)!.Medical!.Incapacitated, "the fight resolved");
        // The attacked bandit and its squad mate fought back at the player.
        Assert.Contains(combat.Log, e => e.Attacker == b.Slot && e.Defender == a.Slot);
        Assert.True(combat.StateOf(c).Thrown > 0 || combat.StateOf(c).TargetSlot == a.Slot || table.Cold(c.Slot)!.InCombat, "the mate joined");
        // A downed character does not move any more.
        int down = table.Cold(a.Slot)!.Medical!.Incapacitated ? a.Slot : b.Slot;
        var at = table.Previous[down].Position;
        world.RunTicks(120);
        Assert.Equal(at, table.Previous[down].Position);
    }

    [Fact]
    public void The_medical_state_is_ticked_once_per_tick_by_the_body_system()
    {
        var (world, _, a, _, _) = Fight(5, 1, distance: 100000);   // nobody can reach anybody
        using var _w = world;
        var hunger = world.Characters.Cold(a.Slot)!.Medical!.Hunger;
        world.RunTicks(1000);
        float once = hunger - world.Characters.Cold(a.Slot)!.Medical!.Hunger;
        Assert.InRange(once, 1e-5f, 0.1f);
        // A second world, the same but with the combat system also ticking: twice the drain.
        var combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, DuelFixture.Lengths, new CombatOptions { SelfApproach = false, TickMedical = true });
        var walk = new OpenGroundWalkability((_, _) => 150);
        using var twice = new SimWorld(new WorldSettings { Seed = 5, PublishSnapshots = false }, walk, [new MovementSystem(new PathService(walk, true)), new BodySystem(BaseGame), combat]);
        var stats = new Combatant().MakeStats();
        var cold = new CharacterCold { Stats = stats, Race = Human, Medical = MedicalState.Create(Human, stats.Strength), Fighter = new Combatant().MakeFighter() };
        var id = twice.Characters.Spawn(new CharacterHot { Position = new Vector3(1000, 150, 1000), Health = 100 }, cold, 0);
        twice.RunTicks(1000);
        Assert.True(hunger - cold.Medical!.Hunger > once * 1.5f, "two systems ticking the same body drain it twice as fast (why the game sets TickMedical = false)");
        _ = id;
    }

    internal static List<ulong> Hashes(int threads)
    {
        var (world, _, _, _, _) = Fight(11, threads, distance: 60);
        using var _w = world;
        var hashes = new List<ulong>();
        int ran = 0;
        foreach (int target in new[] { 1, 60, 200, 600, 1500 })
        {
            world.RunTicks(target - ran);
            ran = target;
            hashes.Add(world.StateHash());
        }
        return hashes;
    }

    [Fact]
    public void A_wired_fight_does_not_depend_on_the_thread_count()
    {
        var one = Hashes(1);
        Assert.Equal(one, Hashes(4));
        Assert.Equal(one, Hashes(16));
        Assert.Equal(one.Count, one.Distinct().Count());
    }
}
