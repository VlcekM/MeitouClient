using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using static Meitou.Tests.Combat.CombatFixtures;
using static Meitou.Tests.Combat.DuelFixture;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Combat;

/// <summary>
/// Headless fights through the combat system: a duel resolves, deterministically at any thread count.
/// </summary>
public class DuelTests(ITestOutputHelper output)
{
    static string Summary(SimWorld world, CombatSystem combat, CharacterId a, CharacterId b)
    {
        var sa = combat.StateOf(a);
        var sb = combat.StateOf(b);
        return $"ticks {world.Tick}: A thrown {sa.Thrown} landed {sa.Landed} parried {sa.Parried} evaded {sa.Evaded} taken {sa.Taken}; "
            + $"B thrown {sb.Thrown} landed {sb.Landed} parried {sb.Parried} evaded {sb.Evaded} taken {sb.Taken}";
    }

    [Fact]
    public void A_duel_of_two_katana_fighters_ends_in_a_knockout_or_death()
    {
        using var world = NewWorld(11, 1, out var combat);
        var (a, b) = Pair(world, new Combatant { Name = "A" }, new Combatant { Name = "B" });
        int ticks = RunToEnd(world, a, b);
        output.WriteLine(Summary(world, combat, a, b));
        foreach (var e in combat.Log)
        {
            output.WriteLine($"  t{e.Tick} {e.Attacker}->{e.Defender} blow {e.Blow} {e.Outcome} part {e.Part} cut {e.Cut:0.0} blunt {e.Blunt:0.0} "
                + $"stun {e.Stun:0.0}{(e.Knockout ? " KO" : "")}{(e.Death ? " DEATH" : "")}");
        }

        Assert.True(Down(world, a) != Down(world, b), "exactly one of them is out");
        Assert.InRange(ticks, 60, 6000);
        int thrown = combat.StateOf(a).Thrown + combat.StateOf(b).Thrown;
        Assert.InRange(thrown, 4, 150);
        Assert.Contains(combat.Log, e => e.Outcome == BlowOutcome.Hit);
        Assert.Contains(combat.Log, e => e.Outcome == BlowOutcome.Blocked || e.Outcome == BlowOutcome.Dodged);
    }

    /// <summary>
    /// The most attackers holding a slot at the target at once (a swing with a blow to come), over <paramref name="ticks"/> or until it is down.
    /// </summary>
    static int MostAtOnce(SimWorld world, CombatSystem combat, CharacterId target, CharacterId[] attackers, int ticks)
    {
        int most = 0;
        for (int i = 0; i < ticks && !Down(world, target); i++)
        {
            world.RunTick();
            most = Math.Max(most, attackers.Count(a => combat.StateOf(a) is { AttackTech: >= 0, NextBlow: > 0 }));
        }
        return most;
    }

    [Fact]
    public void The_attack_slot_setting_caps_how_many_swing_at_one_target_and_applies_live()
    {
        foreach (int slots in new[] { 1, 2 })
        {
            using var world = NewWorld(5, 1, out var combat, new CombatOptions { AttackSlots = slots });
            var targetFighter = new Combatant { Name = "target", Toughness = 100, Defence = 90, Attack = 10, WeaponSkill = 10 };
            var target = Spawn(world, targetFighter, new Vector3(1000, 150, 1000));
            var attackers = Enumerable.Range(0, 5).Select(k => Spawn(world, new Combatant { Attack = 20, WeaponSkill = 20 },
                new Vector3(1000 + 12 * MathF.Cos(k * MathF.Tau / 5), 150, 1000 + 12 * MathF.Sin(k * MathF.Tau / 5)))).ToArray();
            world.Commands.Enqueue(new AttackOrder(attackers, target) { Tick = 0 });

            // It fights back: blocks and lost initiative rolls are where same-tick asks used to slip past the count.
            // Weakly: its cuts sweep the ring (the attack zone), and strong ones keep the attackers staggered.
            world.Commands.Enqueue(new AttackOrder([target], attackers[0]) { Tick = 0 });
            Assert.Equal(slots, MostAtOnce(world, combat, target, attackers, 300));
            if (slots != 1) continue;
            // The settings slider sets the running system's count.
            combat.AttackSlots = 3;
            Assert.Equal(3, MostAtOnce(world, combat, target, attackers, 900));
        }
    }

    // ------------------------------------------------------------------ determinism

    /// <summary>
    /// 40 duels and 20 two-on-one scuffles in a grid: enough characters for the world to cut the slots into several partitions.
    /// </summary>
    static SimWorld Crowd(ulong seed, int threads, out CombatSystem combat)
    {
        var world = NewWorld(seed, threads, out combat, minPartition: 4);
        int n = 0;
        for (int row = 0; row < 6; row++)
        {
            for (int col = 0; col < 10; col++)
            {
                float x = 500 + col * 300, z = 500 + row * 300;
                var skill = 35 + (n * 7) % 40;
                var armour = n % 3 == 0 ? new[] { BodyPlate } : [];
                var first = new Combatant { Attack = skill, Defence = skill + 5, WeaponSkill = skill, Armour = armour };
                var second = new Combatant { Attack = 80 - skill, Defence = 85 - skill, WeaponSkill = 80 - skill };
                var (a, b) = Pair(world, first, second, 14, x, z);
                if (n % 3 == 1)
                {
                    // A third fighter joins against the first.
                    var c = Spawn(world, new Combatant { Attack = 45, WeaponSkill = 45 }, new Vector3(x + 7, 150, z + 12));
                    world.Commands.Enqueue(new AttackOrder([c], a) { Tick = 0 });
                }
                n++;
            }
        }
        return world;
    }

    internal static List<ulong> Hashes(ulong seed, int threads, out int logCount)
    {
        using var world = Crowd(seed, threads, out var combat);
        var hashes = new List<ulong>();
        int ran = 0;
        foreach (int target in new[] { 1, 10, 40, 120, 300, 600 })
        {
            world.RunTicks(target - ran);
            ran = target;
            hashes.Add(world.StateHash());
        }
        logCount = combat.Log.Count;
        return hashes;
    }

    [Fact]
    public void One_four_and_sixteen_threads_reach_the_same_state_in_a_crowd_fight()
    {
        var one = Hashes(7, 1, out int logOne);
        var four = Hashes(7, 4, out int logFour);
        var sixteen = Hashes(7, 16, out int logSixteen);
        output.WriteLine($"{logOne} blows resolved in 600 ticks");
        Assert.Equal(one, four);
        Assert.Equal(one, sixteen);
        Assert.Equal(logOne, logFour);
        Assert.Equal(logOne, logSixteen);
        Assert.True(logOne > 150, "the crowd really fought");
        Assert.Equal(one.Count, one.Distinct().Count());
    }
}
