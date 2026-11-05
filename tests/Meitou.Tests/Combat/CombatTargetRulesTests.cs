using System.Numerics;
using System.Reflection;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Locks;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Combat;

public sealed class CombatTargetRulesTests
{
    sealed class Policy : ICombatTargetRules
    {
        public bool Claim = true;
        public CharacterId? OnlyTarget;
        public CombatTargetOverride Rule;
        public bool TryTarget(SimWorld world, CharacterId attacker, CharacterId target, bool picked, out CombatTargetOverride rule)
        { rule = Rule; return Claim && (OnlyTarget is null || target == OnlyTarget); }
    }
    sealed class Recorder : ICombatHitObserver
    {
        public readonly List<CombatHit> Hits = [];
        public void HitResolved(SimWorld world, in CombatHit hit) => Hits.Add(hit);
    }
    sealed class Scene : IDisposable
    {
        public Policy Policy { get; } = new();
        public Recorder Observer { get; } = new();
        public SimWorld World { get; }
        public CombatSystem Combat { get; }
        public CharacterCold Attacker { get; } = new Combatant().MakeCold("attacker");
        public CharacterCold Victim { get; } = new Combatant().MakeCold("target");
        public CharacterId AttackerId { get; }
        public CharacterId VictimId { get; }
        public Scene(int part = HeadIx)
        {
            var body = BodyOptions.Default with { ForcedHitPart = (Chest.StringId, Chest.Name), GlobalDamageMultiplier = .25f };
            var techniques = new[] { Cut("unused", chance: 0, direction: 1), Cut("ground cut", direction: 7) };
            var lengths = new AnimationLengths().Add("unused", .9f).Add("ground cut", .9f);
            var combat = Combat = new CombatSystem(techniques, Constants, BaseGame, body, lengths,
                new CombatOptions { TickMedical = false, RecordLog = true });
            World = new(new WorldSettings { Seed = 13, Threads = 2, MinPartitionSize = 1, PublishSnapshots = false },
                new OpenGroundWalkability((_, _) => 150), [Policy, combat, Senses(combat), Observer]);
            Attacker.IsPlayer = true; Attacker.Faction = 1; Victim.Faction = 2;
            AttackerId = World.Characters.Spawn(new() { Position = new(1000, 150, 1000), Health = 100 }, Attacker, 0);
            VictimId = World.Characters.Spawn(new() { Position = new(1004, 150, 1000), Health = 100 }, Victim, 0);
            Victim.Medical!.KnockOut(100);
            Policy.Rule = new(true, AllowIncapacitated: true, Part: part, AimPoint: new(1004, 150, 1000), GroundTarget: true);
        }
        public void Order() => World.Commands.Enqueue(new AttackOrder([AttackerId], VictimId) { Tick = World.Tick });
        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void An_admitted_incapacitated_target_uses_the_exact_part_and_the_emitted_technique()
    {
        using var scene = new Scene();
        var before = scene.Victim.Medical!.Parts.Select(part => part.Flesh).ToArray();
        scene.Order();
        for (int tick = 0; tick < 500 && scene.Observer.Hits.Count == 0; tick++) scene.World.RunTick();
        var hit = Assert.Single(scene.Observer.Hits);
        Assert.Equal(HeadIx, hit.Part); // Policy beats the forced Chest option, rather than replacing weighted randomness.
        Assert.Equal(7, hit.CutDirection); // Technique 1 must survive the queued effect; technique 0 has direction 1.
        Assert.True(hit.VictimWasAlive); Assert.True(hit.VictimWasIncapacitated);
        Assert.True(scene.Victim.Medical.Parts[HeadIx].Flesh < before[HeadIx]);
        for (int part = 1; part < before.Length; part++) Assert.Equal(before[part], scene.Victim.Medical.Parts[part].Flesh);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_queued_hit_uses_its_actual_victims_policy_after_the_attacker_retargets(bool invalidActualPart)
    {
        using var scene = new Scene();
        var replacement = DuelFixture.Spawn(scene.World, new Combatant(), new(1006, 150, 1000));
        scene.World.RunTick();
        // A contact has already been emitted for VictimId; a previous committed effect changes the live target.
        var write = (CombatSlot[])typeof(CombatSystem).GetField("write", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene.Combat)!;
        write[scene.AttackerId.Slot].AttackSeq = 1;
        write[scene.AttackerId.Slot].AttackTech = 1;
        write[scene.AttackerId.Slot].TargetSlot = replacement.Slot;
        write[scene.AttackerId.Slot].TargetGeneration = replacement.Generation;
        scene.Policy.OnlyTarget = invalidActualPart ? scene.VictimId : replacement;
        scene.Policy.Rule = scene.Policy.Rule with { Part = invalidActualPart ? int.MaxValue : LArm };
        var before = scene.Victim.Medical!.Parts.Select(part => part.Flesh).ToArray();
        scene.Combat.Apply(scene.World, new Effect
        {
            Kind = CombatSystem.BlowEffect, Source = scene.AttackerId.Slot, Target = scene.VictimId.Slot,
            Amount = 1, Arg = 1, Position = new(1, 0, 0),
        });
        if (invalidActualPart)
        {
            Assert.Empty(scene.Observer.Hits);
            Assert.Equal(before, scene.Victim.Medical.Parts.Select(part => part.Flesh).ToArray());
        }
        else
        {
            var hit = Assert.Single(scene.Observer.Hits);
            Assert.Equal(2, hit.Part); // Actual victim is unclaimed: its ordinary forced Chest wins over the new target's Left Arm.
            Assert.Equal(before[LArm], scene.Victim.Medical.Parts[LArm].Flesh);
            Assert.True(scene.Victim.Medical.Parts[2].Flesh < before[2]);
        }
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("unclaimed")]
    [InlineData("dead")]
    [InlineData("carried")]
    [InlineData("caged")]
    [InlineData("missing part")]
    [InlineData("invalid part")]
    public void A_declined_or_invalid_target_cannot_substitute_another_part(string reason)
    {
        using var scene = new Scene(LArm);
        switch (reason)
        {
            case "denied": scene.Policy.Rule = scene.Policy.Rule with { Allowed = false }; break;
            case "unclaimed": scene.Policy.Claim = false; break;
            case "dead": scene.Victim.Medical!.Butcher(); break;
            case "carried": scene.Victim.CarriedBy = scene.AttackerId; break;
            case "caged": scene.Victim.InSomething = HeldState.Imprisoned; break;
            case "missing part": scene.Victim.Medical!.Sever(LArm, Context()); break;
            case "invalid part": scene.Policy.Rule = scene.Policy.Rule with { Part = int.MaxValue }; break;
        }
        var before = scene.Victim.Medical!.Parts.Select(part => part.Flesh).ToArray();
        scene.Order(); scene.World.RunTicks(150);
        Assert.Empty(scene.Observer.Hits);
        Assert.Equal(before, scene.Victim.Medical.Parts.Select(part => part.Flesh).ToArray());
    }

    [Fact]
    public void Rules_are_queried_in_registration_order_and_the_first_claim_is_authoritative()
    {
        var declined = new Policy { Claim = false };
        var first = new Policy { Rule = new(false, Part: HeadIx) };
        var later = new Policy { Rule = new(true, Part: LArm) };
        using var world = new SimWorld(new WorldSettings(), new OpenGroundWalkability((_, _) => 0), [declined, first]);
        world.AddSystem(later);
        Assert.True(world.TryCombatTarget(default, default, true, out var denied));
        Assert.Equal(first.Rule, denied);
        first.Claim = false;
        Assert.True(world.TryCombatTarget(default, default, false, out var admitted));
        Assert.Equal(later.Rule, admitted);
        later.Claim = false;
        Assert.False(world.TryCombatTarget(default, default, false, out _));
    }

    [Fact]
    public void An_unclaimed_policy_changes_neither_native_combat_results_nor_hashes()
    {
        static (ulong Hash, CombatLogEntry[] Hits) Run(bool policy)
        {
            using var world = DuelFixture.NewWorld(31, 4, out var combat);
            if (policy) world.AddSystem(new Policy { Claim = false });
            DuelFixture.Pair(world, new Combatant(), new Combatant()); world.RunTicks(600);
            return (world.StateHash(), combat.Log.ToArray());
        }
        var plain = Run(false); var ruled = Run(true);
        Assert.Equal(plain.Hash, ruled.Hash); Assert.Equal(plain.Hits, ruled.Hits);
    }
}
