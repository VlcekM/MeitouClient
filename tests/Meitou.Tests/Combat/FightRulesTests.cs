using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using static Meitou.Tests.Combat.DuelFixture;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Combat;

/// <summary>
/// Rules of the fight loop beyond the formulas: reach, staggering, orders, the animation stance, and the downed.
/// </summary>
public class FightRulesTests
{
    [Fact]
    public void A_knocked_out_fighter_cannot_react_and_the_fight_ends()
    {
        using var world = NewWorld(6, 1, out var combat);
        var strong = new Combatant { Attack = 100, WeaponSkill = 100, Dexterity = 100, Strength = 100, Quality = 1 };
        var weak = new Combatant { Defence = 0, Toughness = 1, Attack = 1, WeaponSkill = 1 };
        var (a, b) = Pair(world, strong, weak);
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

    /// <summary>
    /// A, ordered onto B (its task target), with C attacking it from the other side; each of another faction, standing where it is put.
    /// </summary>
    static (CharacterId A, CharacterId B, CharacterId C) Three(SimWorld world, float far)
    {
        CharacterId Put(int faction, float x)
        {
            var cold = new Combatant().MakeCold();
            cold.Faction = faction;
            return world.Characters.Spawn(new CharacterHot { Position = new Vector3(1000 + x, 150, 1000), Health = 100 }, cold, 0);
        }
        CharacterId a = Put(0, 0);
        CharacterId b = Put(1, far);
        CharacterId c = Put(2, -14);
        world.Commands.Enqueue(new AttackOrder([a], b) { Tick = world.Tick });
        world.Commands.Enqueue(new AttackOrder([c], a) { Tick = world.Tick });
        return (a, b, c);
    }

    /// <summary>
    /// The fight's own target down (docs/game/combat.md "Leaving a fight", CombatClassAI update FUN_14060d6a0): the fight ends once a swing in
    /// progress reaches its end marker ("Pacing"), though another still attacks the fighter.
    /// </summary>
    [Fact]
    public void A_fighter_whose_task_target_drops_ends_its_fight_at_once()
    {
        using var world = NewWorld(6, 1, out var combat, new CombatOptions { TickMedical = false });
        var (a, b, c) = Three(world, 14);
        world.RunTicks(10);
        Assert.Equal(b.Slot, combat.StateOf(a).TargetSlot);
        world.Characters.Cold(b.Slot)!.Medical!.KnockOut(600);
        for (int k = 0; k < 120 && combat.StateOf(a).AttackTech >= 0; k++) world.RunTicks(1);
        world.RunTicks(1);
        Assert.False(world.Characters.Cold(a.Slot)!.InCombat, "the task target is down: the fight ends");
        Assert.Equal(a.Slot, combat.StateOf(c).TargetSlot);
    }
}
