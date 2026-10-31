using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Combat;

public sealed partial class CombatSystem
{
    // ------------------------------------------------------------------ commit

    /// <summary>
    /// Whether a blocked blow rings as a parry and throws sparks (the take-hit <c>FUN_1404394f0</c>, outcome 5, <b>Verified</b>): not when the attacker fights with fists (weapon category 5)
    /// and <c>FUN_140436980</c> is false. That test is true when the attacker's race is a robot (race byte +0x7c), or when the <i>defender's</i> own limb of the number the blow strikes with
    /// (technique <c>limb N</c>: 0 left arm, 1 right arm, 2 left leg, 3 right leg, 4 weapon: never) is a fitted prosthetic (limb state 2): the original reads its own medical object there,
    /// not the attacker's.
    /// </summary>
    static bool IsParry(CharacterCold attacker, CharacterCold defender, CombatTechnique tech, int blow)
    {
        if (!attacker.Fighter!.Unarmed || attacker.Race!.IsRobot) return true;
        int limb = blow <= 1 ? tech.Limb1 : tech.Limb2;
        if (limb is < 0 or > 3) return false;
        foreach (var p in defender.Medical!.Parts)
            if (p.Template.LimbSlot == limb && p.Limb == LimbState.Replaced) return true;
        return false;
    }

    public void Apply(World world, in Effect e)
    {
        if (e.Kind == FireEffect) { ApplyFire(world, e); return; }
        if (e.Kind == ReloadEffect) { ApplyReload(world, e); return; }
        if (e.Kind == ZoneBuildingsEffect) { ApplyZoneBuildings(world, e); return; }
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
        int direction = HitOutcomes.Facing(tech.DirectionOf(blow), HitOutcomes.Behind(table.Previous[a].Position, table.Previous[d].Position, table.Previous[d].Yaw));

        // The defender was in the attack zone when the blow arrived (Strike); its own block or dodge decides the outcome.
        CombatTechnique? reaction = D.ReactTech >= 0 && !D.Down && now >= D.StunUntil ? techniques[D.ReactTech] : null;
        float progress = reaction is not null ? Math.Clamp((now - D.ReactStart) / (float)D.ReactTicks, 0, 1) : 0;
        var outcome = HitOutcomes.Decide(reaction, progress, direction);

        A.LastOutcome = outcome;
        // A blow that lands or is blocked takes the defender off the attacker's list of fighters it lets close in (attackImpactCheck → CombatClass::removeAttackerH on
        // the attacker's own list, docs/game/combat.md "Attack slots and circling"): a defender fighting the attacker asks again before its next swing.
        if (outcome is BlowOutcome.Hit or BlowOutcome.Blocked && D.TargetSlot == a && D.TargetGeneration == prev[a].Generation) D.Engaged = false;
        switch (outcome)
        {
            case BlowOutcome.Missed:
                Record(now, a, d, blow, outcome, -1, default, false, false);
                return;
            case BlowOutcome.Blocked:
                A.Parried++;
                D.Blocks++;
                GainCombatXp(ac, dc, XpEvent.GlancingHit, XpEvent.Defended);
                // A dodge technique that counts as a block (the base game's "Dodge back" rows are flagged both) is silent: it looks like a dodge, and the block impact sounds like a hit.
                bool parry = IsParry(ac, dc, tech, blow);
                if (!techniques[D.ReactTech].IsDodge) Sound(world, SoundKind.Blocked, d, a, prev[d].Position, unarmed: !parry, robot: false);
                // A parry throws sparks at the parrying weapon (FUN_1404394f0, IsParry); a fist blow of a plain hand-to-hand impact does not.
                if (!techniques[D.ReactTech].IsDodge && parry) world.BlockSparks?.Enqueue(Id(world, d));
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
            ? DamageFormulas.UnarmedPacket(UnarmedRating(world, a, ac, fighter), Eff(ac, StatsEnumerated.Toughness), str, fighter.FistsItem, kind, dc.Race.StringId, fighter.DamageOutput, combat)
            : DamageFormulas.Packet(skill, dex, str, fighter.Weapon!, kind, dc.Race.StringId, fighter.DamageOutput, combat);
        packet = Powered(packet, tech, blow);

        var ctx = MakeContext(world, d, dc);
        var events = hitEvents;   // Apply is serial, so one list serves every hit
        events.Clear();
        var cover = new CoverRoll(world.Seed, Rng.Key(table.IdOf(d)), a, seq, blow);
        var (part, damage, absorbed) = HitResolver.LandDetailed(dc.Medical, ctx, packet, target.Armour, cover, toughness, tech.LowStrike, direction == 6, combat, events);
        FloatHit(world, d, dc, ac.IsPlayer, part, damage, absorbed);
        A.Landed++;
        D.Taken++;
        D.HitTick = now;
        D.HitPart = part;
        D.HitFrom = a;
        D.HitHeavy = false;
        // From behind (docs/animation.md "Hit reactions"): the attacker is more than 90 degrees off the defender's heading (yaw 0 along +Z, towards +X).
        var toAttacker = new Vector2(prev[a].Position.X - prev[d].Position.X, prev[a].Position.Z - prev[d].Position.Z);
        D.HitBehind = toAttacker.X * MathF.Sin(prev[d].Yaw) + toAttacker.Y * MathF.Cos(prev[d].Yaw) < 0;
        bool knockout = false, death = false;
        foreach (var ev in events)
        {
            switch (ev.Kind)
            {
                case MedicalEventKind.KnockedOut: knockout = true; xp.ToughnessFromKnockOut(dc.Stats, dc.Race); break;
                case MedicalEventKind.Died: death = true; break;
                case MedicalEventKind.Decapitated: table.LimbsChanged.Enqueue((d, -1)); break;   // the look loses its head (nothing drops)
                case MedicalEventKind.PartSevered: xp.ToughnessFromLimbLoss(dc.Stats, dc.Race); table.LimbsChanged.Enqueue((d, dc.Medical.Parts[ev.Part].Template.LimbSlot));
                    world.Defer(new Dialogue.DialogueEvent(table.IdOf(d), CharacterId.None, dc.Medical.Parts[ev.Part].Template.LimbSlot is 2 or 3 ? 70 : 71) { Tick = world.Tick + 1 });   // LOST_LEG / LOST_ARM
                    break;
            }
        }
        // A shackled defender is knocked out instead of staggered a quarter of the time (Character::_startStumble: MedicalSystem::knockout with factor 0.002, the floor of 3 s).
        if (!dc.Medical.Incapacitated && Meitou.Simulation.Locks.LockSystem.WornShackles(dc) is not null
            && CombatRolls.Float(world.Seed, Rng.Key(table.IdOf(d)), CombatRoll.Shackled, CombatRolls.BlowCounter(a, seq, blow, 0)) < ShackledKnockout)
        {
            dc.Medical.KnockOut(dc.Medical.PutDownKoSeconds(ctx, 0.002f));
            knockout = true;
        }
        if (dc.Medical.Incapacitated)
        {
            D.Down = true;
            D.AttackTech = -1;
            D.NextBlow = 0;
            D.ReactTech = -1;
        }
        else Stagger(world, ref D, d, dc, damage.Cut + damage.Blunt + damage.Pierce + damage.ExtraStun > DamageFormulas.StumbleThreshold(toughness, combat), now, CombatRolls.BlowCounter(a, seq, blow, 0));
        GainCombatXp(ac, dc, XpEvent.HitDealt, XpEvent.HitTaken);
        Sound(world, SoundKind.Hit, d, a, prev[d].Position, heavy: D.HitHeavy, unarmed: fighter.Unarmed, robot: ac.Race.IsRobot);
        Splash(world, d, dc, -new Vector2(toAttacker.X, toAttacker.Y), damage.Cut + damage.Blunt + damage.Pierce);
        if (knockout || death) Knockdown(world, d, dc, -toAttacker, damage.Cut + damage.Blunt + damage.Pierce);
        if (death && world.Luckyren) dc.Burst = -new Vector2(toAttacker.X, toAttacker.Y);
        if (dc.Medical.Incapacitated) Sound(world, SoundKind.Down, d, a, prev[d].Position, fatal: death);
        // The police-hit rule first (the game's hit outcome runs before the damage), then the victim's faction's relation events (docs/game/factions-squads-towns.md 3.5).
        Crimes?.Hit(world, a, d);
        if (!dc.Medical.Incapacitated)
        {
            world.SystemOf<AggroSystem>()?.Notice(world, table.IdOf(d), table.IdOf(a));   // the victim senses its attacker (FUN_140999f00)
            HitReplan(dc, world.Tick);
        }
        Deeds?.Blow(world, a, d, dc.Medical.Incapacitated, death);
        // A unique killed: dead in the registry, with the player flag when the killer is the player's (the body pass would mark it dead without).
        if (death && world.Uniques.RecordOf(world.Characters.IdOf(d)) is { } unique) world.Uniques.Died(unique, ac.IsPlayer, dc.Faction);
        Record(now, a, d, blow, outcome, part, damage, knockout, death);
    }

    /// <summary>
    /// A hit's reaction (<b>Verified</b>, FUN_1404391f0 / FUN_140665490, docs/game/combat.md "Hit reactions"): a stumble clip is picked unless the hit is light and one is
    /// already playing; it is big when heavy and none was playing. A picked clip cancels the defender's own swing (that blow never lands) and its reaction, and holds it helpless
    /// until the clip ends (the knockback is the clip's root motion). With no clip to play the swing is still cancelled, with a short pause. Takes effect at the next Act.
    /// </summary>
    void Stagger(World world, ref CombatSlot D, int d, CharacterCold dc, bool heavy, int now, ulong counter)
    {
        // Outside a fight (docs/game/combat.md "Stagger outside a fight"): one getting up is left alone; any other drops its task, which is planned again from its start,
        // unless it mans a turret (shooting at a live target).
        if (D.TargetSlot < 0 && !dc.InCombat)
        {
            if (CharacterFacts.GettingUp(dc, now))
            {
                UnderAttackGettingUp(world, d, dc, counter);
                return;
            }
            if (dc.Gunning is null)
            {
                world.SystemOf<Carrying.CarrySystem>()?.Interrupted(world, d);   // a put-down is finished by the hit, a lift past its attach keeps the body
                Restart(world.Characters, d, dc);
                DecisionSystem.Request(dc);   // forceOrderInsert replans (the stumble is not modelled as an order)
            }
        }
        bool stumbling = D.StumbleClip >= 0 && now < D.StunUntil;
        D.HitHeavy = heavy && !stumbling;
        if (stumbling && !heavy) return;
        D.AttackTech = -1;
        D.NextBlow = 0;
        D.ReactTech = -1;
        string? part = D.HitPart >= 0 && D.HitPart < dc.Medical!.Parts.Count ? dc.Medical.Parts[D.HitPart].Template.StringId : null;
        float roll = CombatRolls.Float(world.Seed, Rng.Key(world.Characters.IdOf(d)), CombatRoll.Stumble, counter);
        if (part is not null && StumbleChooser?.Invoke(dc, part, D.HitHeavy, D.HitBehind ? 1 : 0, roll) is { } clip)
        {
            D.StumbleClip = clip.Clip;
            D.StumbleStart = now;
            D.BreakOutTick = now + Math.Max(1, (int)MathF.Round(CombatTuning.BreakOutSeconds / world.TickSeconds));
            D.StunUntil = now + Math.Max(1, (int)MathF.Ceiling(clip.Seconds / world.TickSeconds));
            D.PauseUntil = 0;   // the stumble state (8) ends the pause (FUN_14060cf60 clears +0x144)
            dc.StaggeredUntil = D.StunUntil;
            D.ReadyTick = Math.Max(D.ReadyTick, D.StunUntil);
        }
        else D.ReadyTick = Math.Max(D.ReadyTick, now + (int)MathF.Ceiling(CombatTuning.InterruptPause / world.TickSeconds));
    }

    /// <summary>
    /// A blow's packet times its impact point's <c>power N</c> (stored x0.01): cut, blunt, pierce and extra stun (<b>Verified</b>, <c>attackImpactCheck</c> FUN_14060a2f0 scales
    /// the first four <c>Damages</c> fields; base data: 60 / 40 on <c>Downward combo</c>, 180 on <c>ma mega kick</c>).
    /// </summary>
    internal static DamagePacket Powered(in DamagePacket p, CombatTechnique tech, int blow)
    {
        float k = tech.PowerOf(blow) * 0.01f;
        return p with { Cut = p.Cut * k, Blunt = p.Blunt * k, Pierce = p.Pierce * k, Stun = p.Stun * k };
    }

    /// <summary>The chance a landed blow knocks a shackled defender out (<c>Character::_startStumble</c>, Verified literal).</summary>
    public const float ShackledKnockout = 0.25f;

    /// <summary>
    /// A task cut off by a hit is planned again from its start (the original ends it and queues it behind Stumbling): its timer and any lift or knock-out swing start over,
    /// and a walk asks for a new path from where the hit left it. What each task keeps of its progress (a lock's pick time, a site's or a machine's work) stays with its object.
    /// </summary>
    static void Restart(CharacterTable table, int slot, CharacterCold cold)
    {
        ref var n = ref table.Next[slot];
        n.TaskTime = 0;
        cold.LiftTime = -1;
        if ((n.Flags & (ushort)MoveFlags.AnyPath) == 0) return;
        n.Flags = (ushort)((n.Flags & ~(ushort)MoveFlags.AnyPath) | (ushort)MoveFlags.NeedPath);
        n.PathCursor = 0;
        cold.PathRequest = 0;
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

    /// <summary>Queues the damage floater of a landed hit when the host shows them (<see cref="World.Floaters"/>).</summary>
    void FloatHit(World world, int d, CharacterCold dc, bool sourceIsPlayer, int part, in HitDamage damage, in ArmourAbsorbed absorbed)
    {
        if (world.Floaters is not { } queue || part < 0) return;
        queue.Enqueue(Floater.Hit(world.Characters.IdOf(d), world.Characters.Previous[d].Position, dc.IsPlayer, sourceIsPlayer, dc.Medical!.Parts[part].Template.Name, damage, absorbed));
    }

    void Record(int now, int a, int d, int blow, BlowOutcome outcome, int part, in HitDamage damage, bool knockout, bool death)
    {
        if (Settings.RecordLog) log.Add(new CombatLogEntry(now, a, d, blow, outcome, part, damage.Cut, damage.Blunt, damage.Pierce, damage.ExtraStun, knockout, death));
    }
}
