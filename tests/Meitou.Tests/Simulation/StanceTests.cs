using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Movement;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class StanceTests
{
    /// <summary>
    /// A fighter running at a target from afar keeps the plain walk and run clips; once near it, it is in the combat stance.
    /// </summary>
    [Fact]
    public void A_fighter_closing_in_from_afar_takes_the_combat_stance_only_near_its_target()
    {
        var combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, DuelFixture.Lengths,
                new CombatOptions { SelfApproach = false, TickMedical = false });
        var walk = new OpenGroundWalkability((_, _) => 150);
        using var world = new SimWorld(new WorldSettings { Seed = 3, Threads = 1, PublishSnapshots = false }, walk,
            [new PursuitSystem(combat), new MovementSystem(new PathService(walk, true)), new BodySystem(BaseGame), combat, Senses(combat)]);
        CharacterId Make(Vector3 at) => world.Characters.Spawn(new CharacterHot { Position = at, Health = 100, MaxSpeed = 60, WalkSpeed = 15,
                Mode = SpeedMode.Free }, new Combatant().MakeCold(), 0);
        var a = Make(new Vector3(1000, 150, 1000));
        var b = Make(new Vector3(1200, 150, 1000));
        world.Commands.Enqueue(new AttackOrder([a], b) { Tick = 0 });
        var animation = new AnimationSystem(new AnimationLibrary([]), DuelFixture.Lengths, combat: combat);
        var table = world.Characters;
        bool Approaching() => animation.Approaching(table, a.Slot, table.Cold(a.Slot)!);
        float Distance() => Vector3.Distance(table.Previous[a.Slot].Position, table.Previous[b.Slot].Position);

        world.RunTicks(30);

        Assert.True(table.Cold(a.Slot)!.InCombat);
        Assert.True(Distance() > CombatTuning.RunGap);
        Assert.True(Approaching(), "on its way from afar");

        for (int ticks = 0; Distance() > CombatTuning.FootworkGap / 2 && ticks < 900; ticks++) world.RunTick();

        Assert.True(Distance() <= CombatTuning.FootworkGap / 2, "arrived");
        Assert.True(table.Cold(a.Slot)!.InCombat);
        Assert.False(Approaching(), "near its target");
    }
}
