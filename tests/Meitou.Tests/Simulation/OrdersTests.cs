using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>The orders panel's toggles: block, passive, hold, sneak and the move speed (docs/simulation.md "Orders").</summary>
public class OrdersTests
{
    /// <summary>A player character at the origin and a bandit <paramref name="distance"/> away that attacks it at tick 0; <paramref name="orders"/> set on the player first.</summary>
    static (SimWorld World, CombatSystem Combat, CharacterId Player, CharacterId Bandit) Attacked(CharacterOrders orders, float distance = 30)
    {
        var combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, DuelFixture.Lengths, new CombatOptions { SelfApproach = false, TickMedical = false });
        var walk = new OpenGroundWalkability((_, _) => 150);
        // Seed 1: a fight where the unordered player is not knocked out before its first swing (seeds 3 and 9 are, since the original part order).
        var world = new SimWorld(new WorldSettings { Seed = 1, PublishSnapshots = false }, walk,
            [new PlayerSystem(), new PursuitSystem(combat), new MovementSystem(new PathService(walk, true)), new BodySystem(BaseGame), combat, new RetaliationSystem(combat)]);
        CharacterId Make(string name, Vector3 at, bool player)
        {
            var cold = new Combatant { Name = name }.MakeCold();
            cold.IsPlayer = player;
            return world.Characters.Spawn(new CharacterHot { Position = at, Health = 100, MaxSpeed = 60, WalkSpeed = 15, Mode = SpeedMode.Free }, cold, 0);
        }
        var player = Make("player", new Vector3(1000, 150, 1000), true);
        var bandit = Make("bandit", new Vector3(1000 + distance, 150, 1000), false);
        if (orders != CharacterOrders.None) world.Commands.Enqueue(new OrdersCommand([player], orders, true) { Tick = 0 });
        world.Commands.Enqueue(new AttackOrder([bandit], player) { Tick = 0 });
        return (world, combat, player, bandit);
    }

    [Fact]
    public void Block_fights_back_without_attacking_and_passive_still_defends_itself()
    {
        var (open, openCombat, p0, _) = Attacked(CharacterOrders.None);
        var (block, blockCombat, p1, _) = Attacked(CharacterOrders.Block);
        var (passive, passiveCombat, p2, _) = Attacked(CharacterOrders.Passive);
        using (open) using (block) using (passive)
        {
            open.RunTicks(600); block.RunTicks(600); passive.RunTicks(600);
            Assert.True(openCombat.StateOf(p0).Thrown > 0, "without orders the attacked character swings back");
            Assert.True(blockCombat.StateOf(p1).TargetSlot >= 0 || block.Characters.Cold(p1.Slot)!.Medical!.Incapacitated, "a blocker still faces its attacker");
            Assert.Equal(0, blockCombat.StateOf(p1).Thrown);
            // Passive keeps a character out of others' fights, not out of its own defence (Self preservation ignores it).
            Assert.True(passiveCombat.StateOf(p2).Thrown > 0, "a passive character attacked swings back");
        }
    }

    /// <summary>
    /// A stagger outside a fight (docs/game/combat.md "Fight lifecycle and orders"): a character walking on a move order and hit from behind stops while its stumble clip plays,
    /// does not fight back (its order outranks self-defence), and walks on to the same goal afterwards.
    /// </summary>
    [Fact]
    public void A_hit_walker_stands_through_its_stumble_and_walks_on()
    {
        var (world, combat, player, _) = Attacked(CharacterOrders.None, distance: 15);
        using var _w = world;
        combat.StumbleChooser = (_, _, _, _, _) => (0, 0.5f);
        world.Characters.Cold(player.Slot)!.RunSpeed = SpeedMode.Walk;   // the raider catches it
        var goal = new Vector3(1000, 150, 3000);
        world.Commands.Enqueue(new MoveOrder([player], goal) { Tick = 0 });
        bool stood = false, walkedOn = false, ordered = false;
        for (int k = 0; k < 900 && !(stood && walkedOn); k++)
        {
            world.RunTicks(1);
            var cold = world.Characters.Cold(player.Slot)!;
            ref readonly var n = ref world.Characters.Next[player.Slot];
            if (cold.Medical!.Incapacitated) break;
            ordered |= n.Task == (byte)CharacterTask.GoTo;
            if (!ordered) continue;
            Assert.Equal((byte)CharacterTask.GoTo, n.Task);
            float speed = new Vector2(n.Velocity.X, n.Velocity.Z).Length();
            if (cold.StaggeredUntil > world.Tick) { if (speed == 0) stood = true; }
            else if (stood && speed > 1) walkedOn = true;
        }
        Assert.True(stood, "it stands while its stumble clip plays");
        Assert.True(walkedOn, "it walks on afterwards");
        Assert.Equal(new Vector2(goal.X, goal.Z), world.Characters.Next[player.Slot].Goal);
        Assert.Equal(0, combat.StateOf(player).Thrown);
    }

    [Fact]
    public void Orders_go_to_the_originals_state_keys_and_back()
    {
        var cold = new CharacterCold { Orders = CharacterOrders.Block | CharacterOrders.Sneak | CharacterOrders.Hold, RunSpeed = SpeedMode.Run };
        var record = new FcsRecord { Type = (int)FcsRecordType.GAMESTATE_CHARACTER };
        Orders.Write(record, cold);
        Assert.True(record.Bools["defensive mode"]);
        Assert.True(record.Bools["stealth"]);
        Assert.True(record.Bools["passive mode"]);
        Assert.False(record.Bools["non combat mode"]);
        Assert.Equal(1, record.Ints["speed mode"]);
        // The jobs switch is not here: it is the AI record's (PermaJobSave).
        Assert.False(record.Bools.ContainsKey(Orders.JobsKey));
        var back = new CharacterCold { Orders = CharacterOrders.None };
        Orders.Read(record, back);
        Assert.Equal(cold.Orders, back.Orders);
        Assert.Equal(SpeedMode.Run, back.RunSpeed);
    }
}
