using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Combat;

/// <summary>
/// The execution of captives: one blow of <c>Task_KillPrisoner</c> (<see cref="Execute"/>) and its effects.
/// </summary>
public sealed partial class CombatSystem
{
    /// <summary>
    /// One blow of <c>Task_KillPrisoner</c> (goals 134, 135, docs/game/ai-tasks.md "Prisoner fate", <b>Observed</b>): the attacker's weapon packet
    /// (or its fists') lands on the target with no reach, block or dodge test (the target is caged), through its armour and toughness as a landed
    /// melee hit; the floater, sound, blood, KO, death and severed-limb effects follow as in <see cref="Apply"/>. The killing of a captive is no
    /// crime (engine choice: the original's call passes no crime). <paramref name="blow"/> numbers the blows for the armour cover roll. A code mod's
    /// hit filter may change the packet or cancel the blow, which then does nothing here (<see cref="ICombatHitFilter"/>; the caller's job strikes
    /// again). True when the target died of it. Serial phases only.
    /// </summary>
    public bool Execute(World world, int a, int d, int blow)
    {
        var table = world.Characters;
        var ac = table.Cold(a);
        var dc = table.Cold(d);
        if (ac is not { Fighter: { } fighter, Stats: not null, Medical: not null, Race: not null }) return false;
        if (dc is not { Medical: { Dead: false } medical, Race: { } race, Stats: not null }) return false;

        float toughness = dc.Stats.ScaledToughness;
        TargetKind kind = race.IsRobot ? TargetKind.Robot : dc.Fighter is { IsAnimal: true } ? TargetKind.Animal : TargetKind.Human;
        float skill = WeaponSkillLevel(ac, fighter);
        float str = Eff(ac, StatsEnumerated.Strength);
        var packet = fighter.Unarmed
            ? DamageFormulas.UnarmedPacket(UnarmedRating(world, a, ac, fighter), ac.Stats.ScaledToughness, str, fighter.FistsItem, kind,
                race.StringId, fighter.DamageOutput, _combat)
            : DamageFormulas.Packet(skill, Eff(ac, StatsEnumerated.Dexterity), str, fighter.Weapon!, kind, race.StringId,
                fighter.DamageOutput, _combat);
        if (!world.CombatHitFilters.Pass(world, CombatHitSource.Execution, table.IdOf(a), table.IdOf(d), 0, blow, true, ref packet)) return false;

        var ctx = MakeContext(world, d, dc);
        var events = _hitEvents;
        events.Clear();
        var armour = world.ArmourFilters.Pieces(world, CombatHitSource.Execution, table.IdOf(d), dc.Fighter);
        var cover = new CoverRoll(world.Seed, Rng.Key(table.IdOf(d)), a, blow, 0);
        bool wasIncapacitated = medical.Incapacitated;
        Bodies.HealthObservers.Before(world, d);
        var (part, damage, absorbed, _) = world.Player.OnePunch && ac.IsPlayer
            ? HitResolver.LandLethal(medical, ctx, DebugCheats.OnePunchDamage, false, false, events)
            : HitResolver.LandDetailed(medical, ctx, packet, armour, cover, toughness, false, false, _combat, events);
        Bodies.HealthObservers.After(world, d, Bodies.HealthChangeKind.Hit);
        FloatHit(world, d, dc, ac.IsPlayer, part, damage, absorbed);

        ref var dSlot = ref _write[d];
        dSlot.Taken++;
        dSlot.HitTick = (int)world.Tick;
        dSlot.HitPart = part;
        bool death = ApplyExecutionEvents(world, d, dc, events);
        if (medical.Incapacitated)
        {
            dSlot.Down = true;
            dSlot.AttackTech = -1;
            dSlot.CancelTick = (int)world.Tick;
            dSlot.NextBlow = 0;
            dSlot.ReactTech = -1;
        }

        ReportExecution(world, a, d, ac, dc, blow, part, damage, absorbed, death, wasIncapacitated);
        return death;
    }

    /// <summary>
    /// Applies the execution's medical events: a decapitation or a severed part changes the look and queues its dialogue. True when the target died.
    /// </summary>
    bool ApplyExecutionEvents(World world, int d, CharacterCold dc, List<MedicalEvent> events)
    {
        var table = world.Characters;
        bool death = false;
        foreach (var ev in events)
        {
            switch (ev.Kind)
            {
                case MedicalEventKind.Died:
                    death = true;
                    break;
                case MedicalEventKind.Decapitated:
                    // the look loses its head (nothing drops)
                    table.LimbsChanged.Enqueue((d, -1, false));
                    break;
                case MedicalEventKind.PartSevered:
                    LimbCut.Apply(world, d, dc, dc.Medical!, ev, _xp, limbDrops: true);
                    break;
            }
        }

        return death;
    }

    /// <summary>
    /// The execution's sound, blood, ragdoll throw, death record, the observers' hit feed and a death's report (<see cref="DeathObservers"/>).
    /// </summary>
    void ReportExecution(World world, int a, int d, CharacterCold ac, CharacterCold dc, int blow, int part, HitDamage damage, ArmourAbsorbed absorbed,
        bool death, bool wasIncapacitated)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var toAttacker = new Vector2(prev[a].Position.X - prev[d].Position.X, prev[a].Position.Z - prev[d].Position.Z);
        float total = damage.Cut + damage.Blunt + damage.Pierce;
        Sound(world, SoundKind.Hit, d, a, prev[d].Position, unarmed: ac.Fighter!.Unarmed, robot: ac.Race!.IsRobot);
        Splash(world, d, dc, -toAttacker, total, ac.Fighter!.Unarmed ? -1 : a);
        if (death) Knockdown(world, d, dc, -toAttacker, total, part);
        if (dc.Medical!.Incapacitated) Sound(world, SoundKind.Down, d, a, prev[d].Position, fatal: death);
        if (death && world.Uniques.RecordOf(table.IdOf(d)) is { } unique) world.Uniques.Died(unique, ac.IsPlayer, dc.Faction);
        if (part >= 0) ObserveExecution(world, a, d, blow, part, damage, absorbed, -toAttacker, wasIncapacitated);
        if (death) DeathObservers.Report(world, d, table.IdOf(a), DeathSite.Execution);
    }

    /// <summary>
    /// The execution blow as a <see cref="CombatHit"/> for the mods' observers and hit feeds: always the intended target, alive before it
    /// (<see cref="Execute"/>).
    /// </summary>
    static void ObserveExecution(World world, int a, int d, int blow, int part, HitDamage damage, ArmourAbsorbed absorbed, Vector2 travel,
        bool wasIncapacitated)
    {
        if (!world.WantsCombatHits) return;

        var table = world.Characters;
        var victim = table.Previous[d];
        world.ObserveCombatHit(new CombatHit(world.Tick, CombatHitSource.Execution, table.IdOf(a), table.IdOf(d), 0, blow, part,
            damage, absorbed, victim.Position, new(travel.X, 0, travel.Y), victim.Yaw, -1, false, true, true, wasIncapacitated));
    }
}
