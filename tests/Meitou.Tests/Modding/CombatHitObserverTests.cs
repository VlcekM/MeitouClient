using System.Numerics;
using System.Runtime.CompilerServices;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Modding;

public class CombatHitObserverTests
{
    sealed class Recorder(Action<SimWorld, CombatHit>? after = null) : ICombatHitObserver
    {
        public readonly List<CombatHit> Hits = [];
        public readonly List<int> Threads = [];

        public void HitResolved(SimWorld world, in CombatHit hit)
        {
            Hits.Add(hit);
            Threads.Add(Environment.CurrentManagedThreadId);
            after?.Invoke(world, hit);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_landed_melee_contact_reports_post_armour_damage_and_pre_damage_flags_after_medical_consequences(bool fatal)
    {
        var body = BodyOptions.Default with { ForcedHitPart = (Head.StringId, Head.Name), GlobalDamageMultiplier = 1.5f };
        var technique = Cut("cut left", direction: 2);
        var combat = new CombatSystem([technique], Constants, BaseGame, body, DuelFixture.Lengths,
            new CombatOptions { RecordLog = true, TickMedical = false });
        bool? deadAtCallback = null, downAtCallback = null;
        int logAtCallback = -1;
        var observer = new Recorder((world, hit) =>
        {
            var medical = world.Characters.Cold(hit.Victim.Slot)!.Medical!;
            deadAtCallback = medical.Dead;
            downAtCallback = medical.Incapacitated;
            logAtCallback = combat.Log.Count;
        });
        using var world = new SimWorld(new WorldSettings { Seed = 11, Threads = 4, MinPartitionSize = 1, PublishSnapshots = false },
            new OpenGroundWalkability((_, _) => 150), [combat, Senses(combat), observer]);
        var attackerCold = new Combatant { Attack = 100 }.MakeCold();
        var victimCold = new Combatant { Armour = [Helmet], ArmourQuality = .5f }.MakeCold();
        var attacker = Put(world, attackerCold, new(1000, 150, 1000));
        var victim = Put(world, victimCold, new(1014, 150, 1000));
        var head = victimCold.Medical!.Parts[HeadIx];
        head.Flesh = fatal ? -head.MaxHp + 1 : 1;
        float fleshBefore = head.Flesh;
        var fighter = attackerCold.Fighter!;
        var packet = DamageFormulas.Packet(50, 50 * fighter.DexterityMult, 50, fighter.Weapon!, TargetKind.Human,
            victimCold.Race!.StringId, fighter.DamageOutput, Constants);
        var armour = ArmourStack.Stack(victimCold.Fighter!.Armour, new[] { true }, packet.ArmourPenetration);
        float toughness = victimCold.Stats![StatsEnumerated.Toughness] * victimCold.Medical.StatMultiplier(StatsEnumerated.Toughness);
        var expected = HitResolver.Resolve(packet, armour, toughness, body.GlobalDamageMultiplier, Constants);
        var stopped = HitResolver.Absorbed(packet, armour);
        world.Commands.Enqueue(new AttackOrder([attacker], victim) { Tick = 0 });
        int caller = Environment.CurrentManagedThreadId;

        RunUntil(world, () => observer.Hits.Count > 0);

        var hit = Assert.Single(observer.Hits);
        var logged = Assert.Single(combat.Log, entry => entry.Outcome == BlowOutcome.Hit);
        Assert.Equal(CombatHitSource.Melee, hit.Source);
        Assert.Equal(attacker, hit.Attacker);
        Assert.Equal(victim, hit.Victim);
        Assert.Equal(logged.Tick, hit.Tick);
        Assert.Equal(logged.Blow, hit.Blow);
        Assert.Equal(HeadIx, hit.Part);
        Assert.Equal(expected, hit.Damage);
        Assert.Equal(stopped, hit.Absorbed);
        Assert.True(hit.Absorbed.Cut > 0);
        Assert.Equal(logged.Cut, hit.Damage.Cut);
        Assert.Equal(logged.Blunt, hit.Damage.Blunt);
        Assert.Equal(logged.Pierce, hit.Damage.Pierce);
        Assert.Equal(logged.Stun, hit.Damage.ExtraStun);
        Assert.Equal(fleshBefore - hit.Damage.Cut - hit.Damage.Pierce, head.Flesh, 4);
        Assert.True(hit.VictimWasAlive);
        Assert.False(hit.VictimWasIncapacitated);
        Assert.Equal(fatal, deadAtCallback);
        Assert.True(downAtCallback);
        Assert.Equal(1, logAtCallback);
        Assert.Equal(caller, Assert.Single(observer.Threads));
        Assert.True(hit.Intended);
        Assert.False(hit.LowStrike);
        Assert.Equal(2, hit.CutDirection);
        Assert.Equal(world.Characters.Previous[victim.Slot].Position, hit.VictimPosition);
        Assert.Equal(0, hit.Travel.Y);
        Assert.True(hit.Travel.X > 0);
        var kept = hit;
        world.RunTicks(3);
        Assert.Equal(kept, observer.Hits[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_real_projectile_reports_the_original_shooter_even_if_its_slot_was_reused(bool replaceShooter)
    {
        var body = BodyOptions.Default with { ForcedHitPart = (Head.StringId, Head.Name) };
        var combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, body, DuelFixture.Lengths,
            new CombatOptions { RecordLog = true, TickMedical = false });
        var observer = new Recorder();
        using var world = new SimWorld(new WorldSettings { Seed = 5, Threads = 4, MinPartitionSize = 1, PublishSnapshots = false },
            new OpenGroundWalkability((_, _) => 150), [combat, Senses(combat), observer]);
        var bow = RangedTests.Ranger with { PierceDamageMin0 = 100, PierceDamageMin1 = 100, PierceDamageMax0 = 100, PierceDamageMax1 = 100 };
        var shooterCold = RangedTests.Shooter(100, 100, 1, bow: bow);
        var shooter = Put(world, shooterCold, new(1000, 150, 1000));
        var victimCold = new Combatant { Toughness = 50, Armour = [Helmet], ArmourQuality = .5f }.MakeCold();
        victimCold.Faction = 1;
        var victim = Put(world, victimCold, new(1300, 150, 1000));
        var head = victimCold.Medical!.Parts[HeadIx];
        float fleshBefore = head.Flesh;
        world.Commands.Enqueue(new AttackOrder([shooter], victim) { Tick = 0 });
        RunUntil(world, () => combat.ProjectilesFired > 0);
        var launched = Assert.Single(combat.PublishProjectiles());
        int projectile = launched.Id;
        var bowStats = shooterCold.Fighter!.Crossbow!.Stats;
        Assert.Equal(bowStats.PierceMin, bowStats.PierceMax); // Fixed authored damage still takes the game's bow-damage scaling.
        float height = RangedFormulas.HeightFactor(launched.Position.Y, world.Characters.Previous[victim.Slot].Position.Y);
        var flight = RangedFormulas.Bolt(bowStats, height, 0);
        var packet = new DamagePacket(0, 0, flight.Damage, 0, RangedFormulas.BoltBleed, 0);
        var armour = ArmourStack.Stack(victimCold.Fighter!.Armour, new[] { true }, 0);
        float toughness = victimCold.Stats![StatsEnumerated.Toughness] * victimCold.Medical.StatMultiplier(StatsEnumerated.Toughness);
        var expected = HitResolver.Resolve(packet, armour, toughness, body.GlobalDamageMultiplier, Constants);
        var stopped = HitResolver.Absorbed(packet, armour);

        if (replaceShooter)
        {
            world.Characters.Remove(shooter);
            var replacement = Put(world, new Combatant().MakeCold(), new(5000, 150, 5000));
            Assert.Equal(shooter.Slot, replacement.Slot);
            Assert.NotEqual(shooter.Generation, replacement.Generation);
        }

        int caller = Environment.CurrentManagedThreadId;

        RunUntil(world, () => observer.Hits.Count > 0);

        var hit = Assert.Single(observer.Hits);
        var logged = Assert.Single(combat.Log, entry => entry.Ranged);
        Assert.Equal(CombatHitSource.Projectile, hit.Source);
        Assert.Equal(shooter, hit.Attacker);
        Assert.Equal(victim, hit.Victim);
        Assert.Equal(projectile, hit.Sequence);
        Assert.Equal(-1, hit.Blow);
        Assert.Equal(-1, hit.CutDirection);
        Assert.Equal(HeadIx, hit.Part);
        Assert.Equal(expected, hit.Damage);
        Assert.Equal(stopped, hit.Absorbed);
        Assert.True(hit.Absorbed.Pierce > 0);
        Assert.Equal(logged.Tick, hit.Tick);
        Assert.Equal(logged.Pierce, hit.Damage.Pierce);
        Assert.Equal(fleshBefore - hit.Damage.Pierce, head.Flesh, 4);
        Assert.True(hit.VictimWasAlive);
        Assert.False(hit.VictimWasIncapacitated);
        Assert.True(hit.Intended);
        Assert.False(hit.LowStrike);
        Assert.Equal(caller, Assert.Single(observer.Threads));
        Assert.InRange(hit.Travel.Length(), .999f, 1.001f);
        Assert.True(hit.Travel.X > .9f);
        if (replaceShooter) Assert.False(world.Characters.IsAlive(hit.Attacker));
    }

    [Fact]
    public void Successful_observation_changes_neither_combat_results_nor_hashes_and_delivery_order_agrees_across_threads()
    {
        static (ulong Hash, CombatLogEntry[] Log, CombatHit[] Hits) Run(int threads, bool observe)
        {
            using var world = DuelFixture.NewWorld(17, threads, out var combat);
            var observer = new Recorder();
            if (observe) world.AddSystem(observer);

            DuelFixture.Pair(world, new Combatant { Armour = [BodyPlate, Helmet] }, new Combatant());
            world.RunTicks(600);
            return (world.StateHash(), combat.Log.ToArray(), observer.Hits.ToArray());
        }

        var plain = Run(1, false);
        var one = Run(1, true);
        Assert.Equal(plain.Hash, one.Hash);
        Assert.Equal(plain.Log, one.Log);
        Assert.NotEmpty(one.Hits);
        Assert.Contains(one.Log, entry => entry.Outcome != BlowOutcome.Hit);
        Assert.Equal(one.Log.Count(entry => entry.Outcome == BlowOutcome.Hit), one.Hits.Length);
        Assert.Equal(one.Hits.Length, one.Hits
            .Select(hit => (hit.Tick, hit.Source, hit.Attacker, hit.Victim, hit.Sequence, hit.Blow))
            .Distinct()
            .Count());

        foreach (int threads in new[] { 4, 16 })
        {
            var parallel = Run(threads, true);
            Assert.Equal(one.Hash, parallel.Hash);
            Assert.Equal(one.Log, parallel.Log);
            Assert.Equal(one.Hits, parallel.Hits);
        }
    }

    static CharacterId Put(SimWorld world, CharacterCold cold, Vector3 at) =>
        world.Characters.Spawn(new CharacterHot { Position = at, Health = 100 }, cold, 0);

    static void RunUntil(SimWorld world, Func<bool> condition)
    {
        for (int ticks = 0; ticks < 900 && !condition(); ticks++) world.RunTick();

        Assert.True(condition(), "The fixture must reach a real combat delivery.");
    }
}
