using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// The orders panel's toggles: block, passive, hold, sneak and the move speed (docs/simulation.md "Orders").
/// </summary>
public class OrdersTests
{
    /// <summary>
    /// A player character at the origin and a bandit <paramref name="distance"/> away that attacks it at tick 0; <paramref name="orders"/> set on the
    /// player first.
    /// </summary>
    static (SimWorld World, CombatSystem Combat, CharacterId Player, CharacterId Bandit) Attacked(CharacterOrders orders, float distance = 30)
    {
        var combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, DuelFixture.Lengths,
                new CombatOptions { SelfApproach = false, TickMedical = false });
        var walk = new OpenGroundWalkability((_, _) => 150);

        // Seed 1: a fight where the unordered player is not knocked out before its first swing (seeds 3 and 9 are, since the original part order).
        var world = new SimWorld(new WorldSettings { Seed = 1, PublishSnapshots = false }, walk,
            [new AiSystem(new PopulationData { Db = new GameDatabase(), Sites = [], Factions = [], Relations = FactionRelations.Build([]) }),
                    new PlayerSystem(), new PursuitSystem(combat), new MovementSystem(new PathService(walk, true)), new BodySystem(BaseGame), combat,
                    new RetaliationSystem(combat), Senses(combat)]);

        // Each in a squad and a faction of its own: the chooser (its 32 Self preservation) runs only for a squad member, and the senses take only
        // another faction's attacker as a threat.
        CharacterId Make(string name, Vector3 at, bool player)
        {
            var cold = new Combatant { Name = name }.MakeCold();
            cold.IsPlayer = player;
            cold.Faction = player ? 1 : 0;
            var squad = new Squad { Id = world.Squads.NextId(), TemplateId = "t", Faction = cold.Faction };
            world.Squads.Add(squad);
            cold.SquadId = squad.Id;
            var id = world.Characters.Spawn(new CharacterHot { Position = at, Health = 100, MaxSpeed = 60, WalkSpeed = 15, Mode = SpeedMode.Free },
                    cold, 0);
            squad.Members.Add(id);
            squad.Leader = id;
            return id;
        }
        var player = Make("player", new Vector3(1000, 150, 1000), true);
        var bandit = Make("bandit", new Vector3(1000 + distance, 150, 1000), false);
        if (orders != CharacterOrders.None)
            world.Commands.Enqueue(new OrdersCommand([player], orders, true) { Tick = 0 });
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
            open.RunTicks(600);
            block.RunTicks(600);
            passive.RunTicks(600);

            Assert.True(openCombat.StateOf(p0).Thrown > 0, "without orders the attacked character swings back");
            Assert.True(blockCombat.StateOf(p1).TargetSlot >= 0 || block.Characters.Cold(p1.Slot)!.Medical!.Incapacitated,
                    "a blocker still faces its attacker");
            Assert.Equal(0, blockCombat.StateOf(p1).Thrown);

            // Passive keeps a character out of others' fights, not out of its own defence (Self preservation ignores it).
            Assert.True(passiveCombat.StateOf(p2).Thrown > 0, "a passive character attacked swings back");
        }
    }

    /// <summary>
    /// A stagger outside a fight (docs/game/combat.md "Stagger outside a fight"): a character walking on a move order and hit from behind gets the
    /// stumble order 147, which its next AI update ends whatever the clip's length (here 0.5 s): one tick, then the move is planned again from its
    /// start and it walks on to the same goal, without fighting back (its order outranks self-defence).
    /// </summary>
    [Fact]
    public void A_hit_walker_stumbles_until_its_next_update_and_walks_on()
    {
        var (world, combat, player, _) = Attacked(CharacterOrders.None, distance: 15);
        using var w = world;
        combat.StumbleChooser = (_, _, _, _, _) => (0, 0.5f);
        world.Characters.Cold(player.Slot)!.RunSpeed = SpeedMode.Walk;   // the raider catches it
        var goal = new Vector3(1000, 150, 3000);
        world.Commands.Enqueue(new MoveOrder([player], goal) { Tick = 0 });
        int stumbles = 0, walkedOn = 0;
        bool ordered = false;
        TaskType? lastRow = null;
        for (int k = 0; k < 900 && walkedOn < 3; k++)
        {
            world.RunTicks(1);
            var cold = world.Characters.Cold(player.Slot)!;
            ref readonly var n = ref world.Characters.Next[player.Slot];
            if (cold.Medical!.Incapacitated) break;
            ordered |= n.Task == (byte)CharacterTask.GoTo;
            var row = cold.Running?.Row;
            if (!ordered) continue;

            // The stagger ends the walk and its order 147 stands in for it; the tick after, its update has ended it, the hold cut short of the
            // clip, and the move is planned again from its start, its walk the tick after (docs/game/combat.md "Stagger outside a fight").
            var expected = lastRow == TaskType.StumbleTaskForced ? CharacterTask.Idle : CharacterTask.GoTo;
            Assert.Equal((byte)expected, n.Task);
            if (row == TaskType.StumbleTaskForced)
            {
                stumbles++;
                Assert.NotEqual(TaskType.StumbleTaskForced, lastRow);
            }
            else if (lastRow == TaskType.StumbleTaskForced)
            {
                Assert.Equal(TaskType.MoveCusOrdered, row);
                Assert.True(cold.StaggeredUntil <= world.Tick, "the update ends the stumble's hold with it");
                walkedOn++;
            }

            lastRow = row;
        }

        Assert.True(walkedOn > 0, "it is staggered and walks on afterwards");
        Assert.Equal(stumbles, walkedOn + (lastRow == TaskType.StumbleTaskForced ? 1 : 0));
        Assert.Equal(new Vector2(goal.X, goal.Z), world.Characters.Next[player.Slot].Goal);
        Assert.Equal(0, combat.StateOf(player).Thrown);
    }

    /// <summary>
    /// Whose AI update ends a stumble (<c>FUN_140511130</c> stops the running task before the chooser, <see cref="BodyOrders.UpdateEnds"/>,
    /// docs/game/combat.md "Stagger outside a fight"): a walker in a squad, whose chooser runs, is held by its stumble (order 147's
    /// <see cref="CharacterCold.StaggeredUntil"/>) for one tick; one with no squad, whose chooser does not run (<c>FUN_1405112b0</c>), for the clip's
    /// whole second. Catches the update's end applied to a character with no update, or lost for one that has it.
    /// </summary>
    [Fact]
    public void A_stumble_ends_at_the_next_ai_update_only_for_a_character_whose_chooser_runs()
    {
        Assert.Equal(1, StumbleTicks(squadless: false).Held);

        var (held, clip) = StumbleTicks(squadless: true);

        Assert.InRange(held, clip - 1, clip + 1);
    }

    /// <summary>
    /// The ticks the first stumble of a walker hit from behind holds it (0 when it is not staggered within 900 ticks) and the ticks of its 1 s clip;
    /// the walker is taken out of its squad when <paramref name="squadless"/>.
    /// </summary>
    static (int Held, int Clip) StumbleTicks(bool squadless)
    {
        var (world, combat, player, _) = Attacked(CharacterOrders.None, distance: 15);
        using var w = world;
        combat.StumbleChooser = (_, _, _, _, _) => (0, 1f);
        var cold = world.Characters.Cold(player.Slot)!;
        cold.RunSpeed = SpeedMode.Walk;
        if (squadless)
        {
            world.Squads.Remove(cold.SquadId);
            cold.SquadId = -1;
        }

        world.Commands.Enqueue(new MoveOrder([player], new Vector3(1000, 150, 3000)) { Tick = 0 });
        int ticks = 0;
        bool staggered = false;
        for (int k = 0; k < 900; k++)
        {
            world.RunTicks(1);
            if (cold.Medical!.Incapacitated) break;

            staggered |= cold.Running?.Row == TaskType.StumbleTaskForced;
            if (!staggered) continue;

            if (cold.StaggeredUntil > world.Tick) ticks++;
            else break;
        }

        return (ticks, (int)MathF.Round(1 / world.TickSeconds));
    }

    /// <summary>
    /// A follower copies its leader's sneaking each update (<c>FUN_14033b350</c>), and dropping it leaves the posture alone (the setter
    /// <c>FUN_1405ca9a0</c> resets the mover, not the posture; docs/game/combat.md standing orders): a player character staying low that follows a
    /// sneaking mate, when the mate stops sneaking and walks off, stops sneaking itself but stays low, keeps its follow order at the front of its
    /// queue and walks after the mate. Catches the copy standing the follower up, whose get-up order (65, with the clear flag) wipes the follow.
    /// </summary>
    [Fact]
    public void A_sneaking_follower_stays_low_and_keeps_its_follow_order_when_its_leader_stops_sneaking()
    {
        using var rig = new TaskStartTests.Rig(carrying: false);
        var (follower, leader) = (rig.Fighter, rig.Mate);
        rig.Say(new StopCommand([follower]) { Tick = 0 });
        rig.World.RunTicks(3);
        rig.Cold(follower).Medical!.StayingLow = true;
        rig.Cold(leader).Orders |= CharacterOrders.Sneak;
        rig.Say(new FollowOrder([follower], leader) { Tick = rig.World.Tick });
        rig.World.RunTicks(15);

        Assert.True(Orders.Has(rig.Cold(follower), CharacterOrders.Sneak), "it sneaks with its leader");
        Assert.Equal(CharacterTask.Follow, rig.Task(follower));

        rig.Say(new OrdersCommand([leader], CharacterOrders.Sneak, false) { Tick = rig.World.Tick });
        rig.Say(new MoveOrder([leader], new Vector3(1040, 300, 2400)) { Tick = rig.World.Tick });
        float startZ = rig.World.Characters.Next[follower.Slot].Position.Z;
        rig.World.RunTicks(10 * 30);
        var cold = rig.Cold(follower);

        float walked = rig.World.Characters.Next[follower.Slot].Position.Z - startZ;

        Assert.False(Orders.Has(cold, CharacterOrders.Sneak), "it stops sneaking with its leader");
        Assert.True(cold.Medical!.StayingLow, "it stays low");
        Assert.Equal(TaskType.StayCloseToTarget, cold.OrderQueue.Front?.Row);
        Assert.Equal(CharacterTask.Follow, rig.Task(follower));
        Assert.True(walked > 40, $"it went only {walked:0} after its leader");
    }

    [Fact]
    public void Orders_go_to_the_originals_state_keys_and_back()
    {
        var cold = new CharacterCold { Orders = CharacterOrders.Block | CharacterOrders.Sneak | CharacterOrders.Hold, RunSpeed = SpeedMode.Run };
        var record = new FcsRecord { Type = (int)FcsRecordType.GamestateCharacter };
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
