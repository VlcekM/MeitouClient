using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using static Meitou.Tests.Combat.DuelFixture;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Combat;

/// <summary>Rules of the fight loop beyond the formulas: reach, staggering, orders, the animation stance, and the downed.</summary>
public class FightRulesTests
{
    /// <summary>Teleports one character once the other has started an attack (a stand-in for a target that runs away).</summary>
    sealed class Mover(int slot, int attacker, Func<CharacterId, CombatSlot> state, Vector3 to) : ITickSystem
    {
        bool done;
        public void Move(SimWorld world, Partition part)
        {
            if (done || slot < part.Start || slot >= part.End) return;
            if (state(world.Characters.IdOf(attacker)).AttackTech < 0) return;
            world.Characters.Next[slot].Position = to;
            done = true;
        }
    }

    [Fact]
    public void A_blow_at_a_target_that_has_left_reach_misses()
    {
        var combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, Lengths, new CombatOptions { RecordLog = true, SelfApproach = false });
        using var world = new SimWorld(new WorldSettings { Seed = 4, PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 150), [new Mover(1, 0, combat.StateOf, new(1500, 150, 1000)), combat]);
        var a = Spawn(world, new Combatant(), new Vector3(1000, 150, 1000));
        var b = Spawn(world, new Combatant(), new Vector3(1014, 150, 1000));
        world.Commands.Enqueue(new AttackOrder([a], b) { Tick = 0 });   // only A attacks
        world.RunTicks(90);
        Assert.NotEmpty(combat.Log);
        Assert.All(combat.Log, e => Assert.Equal(BlowOutcome.Missed, e.Outcome));
        Assert.Equal(0, combat.StateOf(b).Taken);
    }

    [Fact]
    public void A_heavy_hit_staggers_the_target_and_a_light_one_does_not()
    {
        foreach (var (skill, toughness, expectStagger) in new[] { (100f, 1f, true), (1f, 100f, false) })
        {
            using var world = NewWorld(2, 1, out var combat, new CombatOptions { RecordLog = true, SelfApproach = false });
            // A fist fighter with no defence: blows land sooner or later; the stagger depends on the damage against the target's toughness.
            var a = Spawn(world, new Combatant { Strength = skill, WeaponSkill = skill, Dexterity = skill, Attack = skill, Quality = 1 }, new Vector3(1000, 150, 1000));
            var b = Spawn(world, new Combatant { Toughness = toughness, Defence = 0, Weapon = null }, new Vector3(1014, 150, 1000));
            world.Commands.Enqueue(new AttackOrder([a], b) { Tick = 0 });
            for (int i = 0; i < 400 && !combat.Log.Any(e => e.Outcome == BlowOutcome.Hit); i++) world.RunTick();
            Assert.Contains(combat.Log, e => e.Outcome == BlowOutcome.Hit);
            Assert.Equal(expectStagger, combat.StateOf(b).StunUntil > world.Tick);
        }
    }

    [Fact]
    public void A_knocked_out_fighter_cannot_react_and_the_fight_ends()
    {
        using var world = NewWorld(6, 1, out var combat);
        var (a, b) = Pair(world, new Combatant { Attack = 100, WeaponSkill = 100, Dexterity = 100, Strength = 100, Quality = 1 }, new Combatant { Defence = 0, Toughness = 1, Attack = 1, WeaponSkill = 1 });
        RunToEnd(world, a, b);
        Assert.True(Down(world, b));
        Assert.False(Down(world, a));
        int blows = combat.Log.Count;
        world.RunTicks(120);
        Assert.Equal(blows, combat.Log.Count);                       // no more blows at the downed
        Assert.False(combat.StateOf(a).Fighting);
        Assert.False(combat.StateOf(b).Fighting);
        // An order against the downed does nothing.
        world.Commands.Enqueue(new AttackOrder([a], b) { Tick = world.Tick });
        world.RunTicks(60);
        Assert.False(world.Characters.Cold(a.Slot)!.InCombat);
    }

    [Fact]
    public void Stop_ends_the_fight_and_sheathes()
    {
        using var world = NewWorld(9, 1, out var combat);
        var (a, b) = Pair(world, new Combatant(), new Combatant());
        world.RunTicks(30);
        Assert.True(world.Characters.Cold(a.Slot)!.InCombat);
        world.Commands.Enqueue(new StopCommand([a, b]) { Tick = world.Tick });
        world.RunTicks(80);
        foreach (var id in new[] { a, b })
        {
            Assert.False(world.Characters.Cold(id.Slot)!.InCombat);
            Assert.Equal(WeaponKinds.None, world.Characters.Cold(id.Slot)!.DrawnWeapon);
            Assert.NotEqual((byte)CharacterTask.Attack, world.Characters.Previous[id.Slot].Task);
        }
        int blows = combat.Log.Count;
        world.RunTicks(120);
        Assert.Equal(blows, combat.Log.Count);
    }

    [Fact]
    public void A_character_without_a_body_is_not_ordered_to_fight()
    {
        using var world = NewWorld(1, 1, out var combat);
        var a = Spawn(world, new Combatant(), new Vector3(1000, 150, 1000));
        var ghost = world.Characters.Spawn(new CharacterHot { Position = new(1010, 150, 1000) }, new CharacterCold(), 0);
        world.Commands.Enqueue(new AttackOrder([a], ghost) { Tick = 0 });
        world.Commands.Enqueue(new AttackOrder([ghost], a) { Tick = 0 });
        world.RunTicks(60);
        Assert.False(world.Characters.Cold(a.Slot)!.InCombat);
        Assert.Empty(combat.Log);
    }

    [Fact]
    public void The_combat_flag_reaches_the_animation_stance()
    {
        AnimationDefinition Idle(string name, int combatMode, int right) => new()
        {
            Id = name, Name = name, Clip = name, Area = AnimationArea.All, Idle = true, IdleChance = 100, IdleTimeMin = 10, IdleTimeMax = 40, Chance = 100,
            WeaponLeft = 0, WeaponRight = right, CombatMode = combatMode, StealthMode = 0, Kinds = WeaponKinds.All,
            LeftLegMin = 0, LeftLegMax = 100, RightLegMin = 0, RightLegMax = 100,
        };
        var library = new AnimationLibrary([Idle("stand 1", 0, 0), Idle("stand 1 sword", 0, 1), Idle("stand combat sword", 1, 1)]);
        var animation = new AnimationSystem(library, new AnimationLengths());
        var combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, Lengths, new CombatOptions { RecordLog = true });
        using var world = new SimWorld(new WorldSettings { Seed = 3, PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 150), [combat, animation]);
        var (a, b) = Pair(world, new Combatant(), new Combatant());
        string Playing() => string.Join(",", animation.Publish(world.Characters.Cold(a.Slot)!.Animation!).Where(l => l.Weight > 0).Select(l => l.Name));
        world.RunTicks(30);
        Assert.Contains("stand combat sword", Playing());
        // Once the fight is stopped the plain idle comes back.
        world.Commands.Enqueue(new StopCommand([a, b]) { Tick = world.Tick });
        world.RunTicks(120);
        Assert.Contains("stand 1", Playing());
        Assert.DoesNotContain("combat", Playing());
    }

    [Fact]
    public void The_playing_technique_is_exposed_for_the_animation_layer()
    {
        using var world = NewWorld(11, 1, out var combat);
        var (a, b) = Pair(world, new Combatant(), new Combatant());
        bool sawAttack = false, sawReaction = false;
        for (int i = 0; i < 120; i++)
        {
            world.RunTick();
            foreach (var id in new[] { a, b })
                if (combat.Playing(id, world.Tick) is { } p)
                {
                    Assert.InRange(p.Progress, 0f, 1f);
                    if (p.IsAttack) { sawAttack = true; Assert.False(p.Technique.IsBlock); }
                    else { sawReaction = true; Assert.True(p.Technique.IsBlock || p.Technique.IsDodge); }
                }
        }
        Assert.True(sawAttack && sawReaction);
    }
}
