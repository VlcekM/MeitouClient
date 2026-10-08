using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Combat;

public sealed partial class CombatSystem
{
    // ------------------------------------------------------------------ commit

    public void Apply(World world, in Effect e)
    {
        if (e.Kind != BlowEffect) return;
        var table = world.Characters;
        int a = e.Source, d = e.Target, blow = e.Arg, seq = (int)e.Amount, now = (int)world.Tick;
        ref var A = ref write[a];
        ref var D = ref write[d];
        if (A.AttackSeq != seq || A.AttackTech < 0) return;
        var ac = table.Cold(a);
        var dc = table.Cold(d);
        if (ac is null || dc is null || ac.Fighter is null || dc.Fighter is null || ac.Stats is null || dc.Stats is null || ac.Medical is null || dc.Medical is null || ac.Race is null || dc.Race is null) return;
        var tech = techniques[A.AttackTech];
        var prev = table.Previous;
        int direction = tech.DirectionOf(blow);

        // Reach: the blow arrives only if the target is still within the reach the attack started with.
        float gap = Vector2.Distance(new(prev[a].Position.X, prev[a].Position.Z), new(prev[d].Position.X, prev[d].Position.Z)) - CombatTuning.Footprint(ac) - CombatTuning.Footprint(dc);
        BlowOutcome outcome;
        float progress = 0;
        if (gap > A.AttackReach + CombatTuning.ReachSlack) outcome = BlowOutcome.Missed;
        else
        {
            CombatTechnique? reaction = D.ReactTech >= 0 && !D.Down && now >= D.StunUntil ? techniques[D.ReactTech] : null;
            if (reaction is not null) progress = Math.Clamp((now - D.ReactStart) / (float)D.ReactTicks, 0, 1);
            outcome = HitOutcomes.Decide(reaction, progress, direction);
        }

        switch (outcome)
        {
            case BlowOutcome.Missed:
                Record(now, a, d, blow, outcome, -1, default, false, false);
                return;
            case BlowOutcome.Blocked:
                A.Parried++;
                D.Blocks++;
                GainCombatXp(ac, dc, XpEvent.GlancingHit, XpEvent.Defended);
                Stop(ref A, tech, blow);
                Record(now, a, d, blow, outcome, -1, default, false, false);
                return;
            case BlowOutcome.Dodged:
                A.Evaded++;
                D.Dodges++;
                GainCombatXp(ac, dc, XpEvent.GlancingHit, null);
                var dodgeStats = dc.Stats;
                xp.PerEvent(dodgeStats, dc.Race, StatsEnumerated.Dodge, 0.25f * xp.SkillDifferenceFactor(dodgeStats[StatsEnumerated.Dodge], Eff(ac, StatsEnumerated.MeleeAttack)));
                Record(now, a, d, blow, outcome, -1, default, false, false);
                return;
        }

        // A hit.
        var fighter = ac.Fighter;
        var target = dc.Fighter;
        float toughness = Eff(dc, StatsEnumerated.Toughness);
        TargetKind kind = dc.Race.IsRobot ? TargetKind.Robot : target.IsAnimal ? TargetKind.Animal : TargetKind.Human;
        float skill = Eff(ac, fighter.WeaponSkill);
        float dex = Eff(ac, StatsEnumerated.Dexterity) * fighter.DexterityMult;
        float str = Eff(ac, StatsEnumerated.Strength);
        var packet = fighter.Unarmed
            ? DamageFormulas.UnarmedPacket(skill, Eff(ac, StatsEnumerated.Toughness), str, fighter.Weapon, kind, dc.Race.StringId, fighter.DamageOutput, combat)
            : DamageFormulas.Packet(skill, dex, str, fighter.Weapon!, kind, dc.Race.StringId, fighter.DamageOutput, combat);

        var ctx = MakeContext(world, d, dc);
        var events = hitEvents;   // Apply is serial, so one list serves every hit
        events.Clear();
        var cover = new CoverRoll(world.Seed, Rng.Key(table.IdOf(d)), a, seq, blow);
        var (part, damage) = HitResolver.Land(dc.Medical, ctx, packet, target.Armour, cover, toughness, tech.LowStrike, direction == 6, combat, events);

        A.Landed++;
        D.Taken++;
        D.HitTick = now;
        D.HitPart = part;
        D.HitHeavy = false;
        // From behind (docs/animation.md "Hit reactions"): the attacker is more than 90 degrees off the defender's heading (yaw 0 along +Z, towards +X).
        var toAttacker = new Vector2(prev[a].Position.X - prev[d].Position.X, prev[a].Position.Z - prev[d].Position.Z);
        D.HitBehind = toAttacker.X * MathF.Sin(prev[d].Yaw) + toAttacker.Y * MathF.Cos(prev[d].Yaw) < 0;
        bool knockout = false, death = false;
        foreach (var ev in events)
        {
            switch (ev.Kind)
            {
                case MedicalEventKind.KnockedOut: knockout = true; break;
                case MedicalEventKind.Died: death = true; break;
                case MedicalEventKind.PartSevered: xp.ToughnessFromLimbLoss(dc.Stats, dc.Race); break;
            }
        }
        if (dc.Medical.Incapacitated)
        {
            D.Down = true;
            D.AttackTech = -1;
            D.NextBlow = 0;
            D.ReactTech = -1;
        }
        else if (damage.Cut + damage.Blunt + damage.Pierce + damage.ExtraStun > DamageFormulas.StumbleThreshold(toughness, combat))
        {
            // A heavy hit ("Heavy_Hit" instead of "Light_Hit"): the target is helpless for a moment. It takes effect at the next Act, so the blows already thrown this tick still land.
            D.StunUntil = Math.Max(D.StunUntil, now + (int)MathF.Ceiling(CombatTuning.HeavyHitStun / world.TickSeconds));
            D.HitHeavy = true;
        }
        GainCombatXp(ac, dc, XpEvent.HitDealt, XpEvent.HitTaken);
        Record(now, a, d, blow, outcome, part, damage, knockout, death);
    }

    /// <summary>The cover roll of one blow: a function of the defender, the blow and the armour piece.</summary>
    readonly struct CoverRoll(ulong seed, ulong defenderKey, int attacker, int attackSeq, int blow) : ICoverRoll
    {
        public float Roll(int piece) => CombatRolls.Float(seed, defenderKey, CombatRoll.Cover, CombatRolls.BlowCounter(attacker, attackSeq, blow, piece));
    }

    static void Stop(ref CombatSlot attacker, CombatTechnique tech, int blow)
    {
        // A blocked blow stops the swing at its "stop frame": the rest of the combo is not thrown.
        float stop = tech.StopProgress(blow);
        if (stop < 1) attacker.AttackEnd = Math.Min(attacker.AttackEnd, attacker.AttackStart + (int)MathF.Ceiling(stop * attacker.AttackTicks));
        attacker.NextBlow = 0;
    }

    void GainCombatXp(CharacterCold attacker, CharacterCold defender, XpEvent attackerEvent, XpEvent? defenderEvent)
    {
        var af = attacker.Fighter!;
        var df = defender.Fighter!;
        float aStrength = StrengthFactor(attacker, af);
        xp.Combat(attacker.Stats!, attacker.Race!, attackerEvent, af.Unarmed, af.Unarmed ? StatsEnumerated.None : af.Weapon!.Skill, Eff(defender, StatsEnumerated.MeleeDefence), aStrength);
        if (defenderEvent is { } ev)
        {
            float dStrength = StrengthFactor(defender, df);
            var weaponStat = df.Unarmed || ev == XpEvent.HitTaken ? StatsEnumerated.None : df.Weapon!.Skill;
            xp.Combat(defender.Stats!, defender.Race!, ev, df.Unarmed, weaponStat, Eff(attacker, af.Unarmed ? StatsEnumerated.MartialArts : StatsEnumerated.MeleeAttack), dStrength);
        }
    }

    float StrengthFactor(CharacterCold c, Fighter f) => f.Unarmed
        ? 1 - f.EncumbranceFactor
        : xp.WeaponWeightStrengthFactor(f.Weapon!.Stats.Weight, c.Stats![StatsEnumerated.Strength], c.Medical!.StatMultiplier(StatsEnumerated.Strength));

    void Record(int now, int a, int d, int blow, BlowOutcome outcome, int part, in HitDamage damage, bool knockout, bool death)
    {
        if (Settings.RecordLog) log.Add(new CombatLogEntry(now, a, d, blow, outcome, part, damage.Cut, damage.Blunt, damage.Pierce, damage.ExtraStun, knockout, death));
    }
}
