using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Combat;

public sealed partial class CombatSystem
{
    /// <summary>
    /// One blow of <c>Task_KillPrisoner</c> (goals 134, 135, docs/game/ai-tasks.md "Prisoner fate", <b>Observed</b>): the attacker's weapon packet
    /// (or its fists') lands on the target with no reach, block or dodge test (the target is caged), through its armour and toughness as a landed
    /// melee hit; the floater, sound, blood, KO, death and severed-limb effects follow as in <see cref="Apply"/>. The killing of a captive is no
    /// crime (engine choice: the original's call passes no crime). <paramref name="blow"/> numbers the blows for the armour cover roll. True when the
    /// target died of it. Serial phases only.
    /// </summary>
    public bool Execute(World world, int a, int d, int blow)
    {
        var table = world.Characters;
        var ac = table.Cold(a);
        var dc = table.Cold(d);
        if (ac is not { Fighter: { } fighter, Stats: not null, Medical: not null, Race: not null }) return false;
        if (dc is not { Medical: { Dead: false } medical, Race: { } race, Stats: not null }) return false;

        float toughness = Eff(dc, StatsEnumerated.Toughness);
        TargetKind kind = race.IsRobot ? TargetKind.Robot : dc.Fighter is { IsAnimal: true } ? TargetKind.Animal : TargetKind.Human;
        float skill = Eff(ac, fighter.WeaponSkill);
        float str = Eff(ac, StatsEnumerated.Strength);
        var packet = fighter.Unarmed
            ? DamageFormulas.UnarmedPacket(UnarmedRating(world, a, ac, fighter), Eff(ac, StatsEnumerated.Toughness), str, fighter.FistsItem, kind,
                race.StringId, fighter.DamageOutput, _combat)
            : DamageFormulas.Packet(skill, Eff(ac, StatsEnumerated.Dexterity) * fighter.DexterityMult, str, fighter.Weapon!, kind, race.StringId,
                fighter.DamageOutput, _combat);

        var ctx = MakeContext(world, d, dc);
        var events = _hitEvents;
        events.Clear();
        ReadOnlySpan<ArmourPiece> armour = dc.Fighter is { } df ? df.Armour : default;
        var cover = new CoverRoll(world.Seed, Rng.Key(table.IdOf(d)), a, blow, 0);
        bool wasIncapacitated = medical.Incapacitated;
        var (part, damage, absorbed) = HitResolver.LandDetailed(medical, ctx, packet, armour, cover, toughness, false, false, _combat, events);
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
                    table.LimbsChanged.Enqueue((d, -1));
                    break;
                case MedicalEventKind.PartSevered:
                    var template = dc.Medical!.Parts[ev.Part].Template;
                    _xp.ToughnessFromLimbLoss(dc.Stats!, dc.Race!);
                    table.LimbsChanged.Enqueue((d, template.LimbSlot));
                    world.Defer(new Dialogue.DialogueEvent(table.IdOf(d), CharacterId.None,
                        template.LimbSlot is 2 or 3 ? 70 : 71) { Tick = world.Tick + 1 });
                    break;
            }
        }

        return death;
    }

    /// <summary>
    /// The execution's sound, blood, ragdoll throw, death record and the observers' hit feed.
    /// </summary>
    void ReportExecution(World world, int a, int d, CharacterCold ac, CharacterCold dc, int blow, int part, HitDamage damage, ArmourAbsorbed absorbed,
        bool death, bool wasIncapacitated)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var toAttacker = new Vector2(prev[a].Position.X - prev[d].Position.X, prev[a].Position.Z - prev[d].Position.Z);
        float total = damage.Cut + damage.Blunt + damage.Pierce;
        Sound(world, SoundKind.Hit, d, a, prev[d].Position, unarmed: ac.Fighter!.Unarmed, robot: ac.Race!.IsRobot);
        Splash(world, d, dc, -toAttacker, total);
        if (death) Knockdown(world, d, dc, -toAttacker, total, part);
        if (dc.Medical!.Incapacitated) Sound(world, SoundKind.Down, d, a, prev[d].Position, fatal: death);
        if (death && world.Uniques.RecordOf(table.IdOf(d)) is { } unique) world.Uniques.Died(unique, ac.IsPlayer, dc.Faction);
        if (part >= 0) ObserveExecution(world, a, d, blow, part, damage, absorbed, -toAttacker, wasIncapacitated);
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
