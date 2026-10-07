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

/// <summary>Headless fights through the combat system: a duel resolves, deterministically at any thread count.</summary>
public class DuelTests(ITestOutputHelper output)
{
    static string Summary(SimWorld world, CombatSystem combat, CharacterId a, CharacterId b)
    {
        var sa = combat.StateOf(a);
        var sb = combat.StateOf(b);
        return $"ticks {world.Tick}: A thrown {sa.Thrown} landed {sa.Landed} parried {sa.Parried} evaded {sa.Evaded} taken {sa.Taken}; B thrown {sb.Thrown} landed {sb.Landed} parried {sb.Parried} evaded {sb.Evaded} taken {sb.Taken}";
    }

    [Fact]
    public void A_duel_of_two_katana_fighters_ends_in_a_knockout_or_death()
    {
        using var world = NewWorld(11, 1, out var combat);
        var (a, b) = Pair(world, new Combatant { Name = "A" }, new Combatant { Name = "B" });
        int ticks = RunToEnd(world, a, b);
        output.WriteLine(Summary(world, combat, a, b));
        foreach (var e in combat.Log) output.WriteLine($"  t{e.Tick} {e.Attacker}->{e.Defender} blow {e.Blow} {e.Outcome} part {e.Part} cut {e.Cut:0.0} blunt {e.Blunt:0.0} stun {e.Stun:0.0}{(e.Knockout ? " KO" : "")}{(e.Death ? " DEATH" : "")}");
        Assert.True(Down(world, a) != Down(world, b), "exactly one of them is out");
        Assert.InRange(ticks, 60, 6000);
        int thrown = combat.StateOf(a).Thrown + combat.StateOf(b).Thrown;
        Assert.InRange(thrown, 4, 150);
        Assert.Contains(combat.Log, e => e.Outcome == BlowOutcome.Hit);
        Assert.Contains(combat.Log, e => e.Outcome == BlowOutcome.Blocked || e.Outcome == BlowOutcome.Dodged);
    }

    [Fact]
    public void The_fight_sets_the_stance_for_the_animation_and_clears_it_afterwards()
    {
        using var world = NewWorld(5, 1, out var combat);
        var (a, b) = Pair(world, new Combatant(), new Combatant());
        var cold = world.Characters.Cold(a.Slot)!;
        Assert.Equal(WeaponKinds.None, cold.DrawnWeapon);
        world.RunTick();
        Assert.True(cold.InCombat);
        Assert.Equal(WeaponKinds.Katana, cold.DrawnWeapon);
        Assert.Equal((byte)CharacterTask.Attack, world.Characters.Previous[a.Slot].Task);
        RunToEnd(world, a, b);
        world.RunTicks(3);
        var winner = Down(world, a) ? b : a;
        var loser = winner == a ? b : a;
        Assert.False(world.Characters.Cold(winner.Slot)!.InCombat, "the winner sheathes when its target is down");
        Assert.False(world.Characters.Cold(loser.Slot)!.InCombat);
        Assert.Equal(WeaponKinds.None, world.Characters.Cold(winner.Slot)!.DrawnWeapon);
    }

    [Fact]
    public void Fighters_that_start_apart_close_in_and_fight()
    {
        using var world = NewWorld(3, 1, out var combat);
        var (a, b) = Pair(world, new Combatant(), new Combatant(), distance: 90);
        world.RunTicks(60);
        float gap = Vector3.Distance(world.Characters.Previous[a.Slot].Position, world.Characters.Previous[b.Slot].Position);
        Assert.True(gap < 25, $"they closed in (distance {gap})");
        Assert.NotEqual(0f, world.Characters.Previous[a.Slot].Yaw);   // facing the target
        RunToEnd(world, a, b);
        Assert.True(combat.Log.Count > 0);
    }

    [Fact]
    public void A_much_better_fighter_wins_most_duels()
    {
        int wins = 0;
        const int duels = 24;
        for (ulong seed = 0; seed < duels; seed++)
        {
            using var world = NewWorld(seed, 1, out var combat, new CombatOptions());
            var strong = new Combatant { Attack = 80, Defence = 80, WeaponSkill = 80, Dexterity = 70, Strength = 70, Toughness = 70 };
            var weak = new Combatant { Attack = 30, Defence = 30, WeaponSkill = 30, Dexterity = 40, Strength = 40, Toughness = 30 };
            var (a, b) = Pair(world, strong, weak);
            RunToEnd(world, a, b);
            if (Down(world, b) && !Down(world, a)) wins++;
        }
        output.WriteLine($"strong fighter won {wins} of {duels}");
        Assert.True(wins >= duels * 0.8, $"won {wins} of {duels}");
    }

    [Fact]
    public void Plate_armour_makes_a_fighter_much_harder_to_hurt()
    {
        float bare = 0, armoured = 0;
        int bareHits = 0, armouredHits = 0;
        const int duels = 12;
        for (ulong seed = 0; seed < duels; seed++)
        {
            foreach (var plated in new[] { false, true })
            {
                using var world = NewWorld(100 + seed, 1, out var combat);
                var defender = new Combatant { Armour = plated ? [BodyPlate, Helmet] : [], ArmourQuality = 0.8f };
                var (a, b) = Pair(world, new Combatant(), defender);
                RunToEnd(world, a, b, 4000);
                var hits = combat.Log.Where(e => e.Defender == b.Slot && e.Outcome == BlowOutcome.Hit).ToList();
                if (plated) { armoured += hits.Sum(e => e.Cut); armouredHits += hits.Count; }
                else { bare += hits.Sum(e => e.Cut); bareHits += hits.Count; }
            }
        }
        output.WriteLine($"cut damage per hit taken: bare {bare / bareHits:0.0} ({bareHits} hits), plate {armoured / armouredHits:0.0} ({armouredHits} hits)");
        // The plate covers chest, stomach, arms and head: most hits land on it. A katana's -0.3 penetration still cannot take it under the bare number.
        Assert.True(armoured / armouredHits < bare / bareHits * 0.8f);
        Assert.True(armouredHits > 0 && bareHits > 0);
    }

    [Fact]
    public void Combat_xp_raises_the_skills_the_fight_used()
    {
        using var world = NewWorld(21, 1, out var combat);
        var (a, b) = Pair(world, new Combatant(), new Combatant());
        var stats = new[] { world.Characters.Cold(a.Slot)!.Stats!, world.Characters.Cold(b.Slot)!.Stats! };
        float[][] before = [stats[0].ToArray(), stats[1].ToArray()];
        RunToEnd(world, a, b);
        foreach (int who in new[] { a.Slot, b.Slot })
        {
            int other = who == a.Slot ? b.Slot : a.Slot;
            var s = stats[who];
            bool dealt = combat.Log.Any(e => e.Attacker == who && e.Outcome == BlowOutcome.Hit);
            bool took = combat.Log.Any(e => e.Defender == who && e.Outcome == BlowOutcome.Hit);
            bool blocked = combat.Log.Any(e => e.Defender == who && e.Outcome == BlowOutcome.Blocked);
            output.WriteLine($"{who}: dealt {dealt} took {took} blocked {blocked}: attack {before[who][(int)StatsEnumerated.MeleeAttack]}->{s[StatsEnumerated.MeleeAttack]:0.000} katanas ->{s[StatsEnumerated.Katanas]:0.000} defence ->{s[StatsEnumerated.MeleeDefence]:0.000} toughness ->{s[StatsEnumerated.Toughness]:0.000}");
            if (dealt)
            {
                Assert.True(s[StatsEnumerated.MeleeAttack] > before[who][(int)StatsEnumerated.MeleeAttack]);
                Assert.True(s[StatsEnumerated.Katanas] > before[who][(int)StatsEnumerated.Katanas]);
                Assert.True(s[StatsEnumerated.Dexterity] > before[who][(int)StatsEnumerated.Dexterity]);
            }
            if (took) Assert.True(s[StatsEnumerated.Toughness] > before[who][(int)StatsEnumerated.Toughness]);
            if (took || blocked) Assert.True(s[StatsEnumerated.MeleeDefence] > before[who][(int)StatsEnumerated.MeleeDefence]);
        }
    }

    [Fact]
    public void Fists_dodge_and_hit_without_a_weapon()
    {
        var techniques = new List<CombatTechnique>(KatanaTechniques()) { Cut("punch") with { Kinds = WeaponKinds.Unarmed } };
        using var world = NewWorld(8, 1, out var combat, null, techniques);
        var (a, b) = Pair(world, new Combatant { Weapon = null }, new Combatant { Weapon = null });
        int ticks = RunToEnd(world, a, b, 12000);
        output.WriteLine(Summary(world, combat, a, b));
        Assert.True(combat.Log.Count > 0);
        Assert.Contains(combat.Log, e => e.Outcome == BlowOutcome.Hit);
        Assert.True(ticks > 0);
    }

    // ------------------------------------------------------------------ determinism

    /// <summary>40 duels and 20 two-on-one scuffles in a grid: enough characters for the world to cut the slots into several partitions.</summary>
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

    static List<ulong> Hashes(ulong seed, int threads, out int logCount)
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

    [Fact]
    public void Another_seed_gives_another_fight_and_the_same_seed_the_same()
    {
        var a = Hashes(7, 4, out _);
        var b = Hashes(7, 4, out _);
        var c = Hashes(8, 4, out _);
        Assert.Equal(a, b);
        Assert.NotEqual(a[^1], c[^1]);
    }

    [Fact]
    public void The_log_of_a_duel_is_identical_at_every_thread_count()
    {
        List<CombatLogEntry> Run(int threads)
        {
            using var world = NewWorld(33, threads, out var combat);
            var (a, b) = Pair(world, new Combatant(), new Combatant());
            RunToEnd(world, a, b);
            return [.. combat.Log];
        }
        var one = Run(1);
        Assert.NotEmpty(one);
        Assert.Equal(one, Run(4));
        Assert.Equal(one, Run(16));
    }
}
