using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Combat;

public sealed partial class CombatSystem
{
    /// <summary>
    /// One blow of <c>Task_KillPrisoner</c> (goals 134, 135, docs/game/ai-tasks.md "Prisoner fate", <b>Observed</b>): the attacker's weapon packet (or its fists') lands on the target
    /// with no reach, block or dodge test (the target is caged), through its armour and toughness as a landed melee hit; the floater, sound, blood, KO, death and severed-limb
    /// effects follow as in <see cref="Apply"/>. The killing of a captive is no crime (engine choice: the original's call passes no crime). <paramref name="blow"/> numbers the
    /// blows for the armour cover roll. True when the target died of it. Serial phases only.
    /// </summary>
    public bool Execute(World world, int a, int d, int blow)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var ac = table.Cold(a);
        var dc = table.Cold(d);
        if (ac is not { Fighter: { } fighter, Stats: not null, Medical: not null, Race: not null } || dc is not { Medical: { Dead: false } medical, Race: { } race, Stats: not null }) return false;
        float toughness = Eff(dc, StatsEnumerated.Toughness);
        TargetKind kind = race.IsRobot ? TargetKind.Robot : dc.Fighter is { IsAnimal: true } ? TargetKind.Animal : TargetKind.Human;
        float skill = Eff(ac, fighter.WeaponSkill);
        float str = Eff(ac, StatsEnumerated.Strength);
        var packet = fighter.Unarmed
            ? DamageFormulas.UnarmedPacket(UnarmedRating(world, a, ac, fighter), Eff(ac, StatsEnumerated.Toughness), str, fighter.FistsItem, kind, race.StringId, fighter.DamageOutput, combat)
            : DamageFormulas.Packet(skill, Eff(ac, StatsEnumerated.Dexterity) * fighter.DexterityMult, str, fighter.Weapon!, kind, race.StringId, fighter.DamageOutput, combat);
        var ctx = MakeContext(world, d, dc);
        var events = hitEvents;
        events.Clear();
        ReadOnlySpan<ArmourPiece> armour = dc.Fighter is { } df ? df.Armour : default;
        var cover = new CoverRoll(world.Seed, Rng.Key(table.IdOf(d)), a, blow, 0);
        var (part, damage, absorbed) = HitResolver.LandDetailed(medical, ctx, packet, armour, cover, toughness, false, false, combat, events);
        FloatHit(world, d, dc, ac.IsPlayer, part, damage, absorbed);
        ref var D = ref write[d];
        D.Taken++;
        D.HitTick = (int)world.Tick;
        D.HitPart = part;
        bool death = false;
        foreach (var ev in events)
        {
            switch (ev.Kind)
            {
                case MedicalEventKind.Died: death = true; break;
                case MedicalEventKind.Decapitated: table.LimbsChanged.Enqueue((d, -1)); break;   // the look loses its head (nothing drops)
                case MedicalEventKind.PartSevered:
                    xp.ToughnessFromLimbLoss(dc.Stats!, race);
                    table.LimbsChanged.Enqueue((d, medical.Parts[ev.Part].Template.LimbSlot));
                    world.Defer(new Dialogue.DialogueEvent(table.IdOf(d), CharacterId.None, medical.Parts[ev.Part].Template.LimbSlot is 2 or 3 ? 70 : 71) { Tick = world.Tick + 1 });
                    break;
            }
        }
        if (medical.Incapacitated)
        {
            D.Down = true;
            D.AttackTech = -1;
            D.NextBlow = 0;
            D.ReactTech = -1;
        }
        var toAttacker = new Vector2(prev[a].Position.X - prev[d].Position.X, prev[a].Position.Z - prev[d].Position.Z);
        Sound(world, SoundKind.Hit, d, a, prev[d].Position, unarmed: fighter.Unarmed, robot: ac.Race!.IsRobot);
        Splash(world, d, dc, -toAttacker, damage.Cut + damage.Blunt + damage.Pierce);
        if (death) Knockdown(world, d, dc, -toAttacker, damage.Cut + damage.Blunt + damage.Pierce);
        if (medical.Incapacitated) Sound(world, SoundKind.Down, d, a, prev[d].Position, fatal: death);
        if (death && world.Uniques.RecordOf(table.IdOf(d)) is { } unique) world.Uniques.Died(unique, ac.IsPlayer, dc.Faction);
        return death;
    }
}
