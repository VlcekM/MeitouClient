using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Combat;

/// <summary>
/// The Apply step (serial, in the commit's order): a blow's outcome (blocked, dodged or hit) and, for a hit, the damage, part, armour, medical hit,
/// stagger and XP of both sides.
/// </summary>
public sealed partial class CombatSystem
{
    /// <summary>
    /// The chance a landed blow knocks a shackled defender out (<c>Character::_startStumble</c>, Verified literal).
    /// </summary>
    public const float ShackledKnockout = 0.25f;

    #region Blow

    /// <summary>
    /// Whether a blocked blow rings as a parry and throws sparks (the take-hit <c>FUN_1404394f0</c>, outcome 5, <b>Verified</b>): not when the
    /// attacker fights with fists (weapon category 5) and <c>FUN_140436980</c> is false. That test is true when the attacker's race is a robot (race
    /// byte +0x7c), or when the <i>defender's</i> own limb of the number the blow strikes with (technique <c>limb N</c>: 0 left arm, 1 right arm, 2
    /// left leg, 3 right leg, 4 weapon: never) is a fitted prosthetic (limb state 2): the original reads its own medical object there, not the
    /// attacker's.
    /// </summary>
    static bool IsParry(CharacterCold attacker, CharacterCold defender, CombatTechnique tech, int blow)
    {
        if (!attacker.Fighter!.Unarmed || attacker.Race!.IsRobot) return true;

        int limb = tech.LimbOf(blow);
        if (limb is < 0 or > 3) return false;

        foreach (var p in defender.Medical!.Parts)
            if (p.Template.LimbSlot == limb && p.Limb == LimbState.Replaced) return true;

        return false;
    }

    /// <summary>
    /// Applies one combat effect: a shot, a finished reload, the buildings a zone reached, mass-combat XP, or a blow (<see cref="ApplyBlow"/>).
    /// </summary>
    public void Apply(World world, in Effect e)
    {
        if (e.Kind == FireEffect) ApplyFire(world, e);
        else if (e.Kind == ReloadEffect) ApplyReload(world, e);
        else if (e.Kind == ZoneBuildingsEffect) ApplyZoneBuildings(world, e);
        else if (e.Kind == MassCombatEffect) ApplyMassCombat(world, e);
        else if (e.Kind == BlowEffect) ApplyBlow(world, e);
    }

    /// <summary>
    /// The mass-combat XP of the character the effect names (<see cref="MassCombatEffect"/>).
    /// </summary>
    void ApplyMassCombat(World world, in Effect e)
    {
        if (world.Characters.Cold(e.Target) is { Stats: { } asked }) _xp.MassCombat(asked);
    }

    /// <summary>
    /// Applies one blow (<see cref="BlowEffect"/>): the outcome against the defender's reaction (a miss, a block or a dodge ends there), else the
    /// hit (<see cref="LandBlow"/>).
    /// </summary>
    void ApplyBlow(World world, in Effect e)
    {
        var table = world.Characters;
        int a = e.Source, d = e.Target, blow = e.Arg, seq = (int)e.Amount, now = (int)world.Tick;
        ref var aSlot = ref _write[a];
        ref var dSlot = ref _write[d];

        // A blow thrown in Act lands even when Act then ended or restarted the swing on the same tick (the original resolves it inside the swing
        // update); only a hit, knock-out or kill applied earlier this tick takes the swing away from its attacker. It carries its technique, and
        // touches the attacker's swing only while that is still the one.
        if (aSlot.CancelTick == now) return;

        bool live = aSlot.AttackSeq == seq && aSlot.AttackTech >= 0;
        var ac = table.Cold(a);
        var dc = table.Cold(d);
        if (ac is null || dc is null || ac.Fighter is null || dc.Fighter is null || ac.Stats is null || dc.Stats is null || ac.Medical is null
            || dc.Medical is null || ac.Race is null || dc.Race is null)
            return;

        var tech = _techniques[(int)e.Position.X];
        var prev = table.Previous;
        int direction = HitOutcomes.Facing(tech.DirectionOf(blow),
            HitOutcomes.Behind(table.Previous[a].Position, table.Previous[d].Position, table.Previous[d].Yaw));

        // The defender was in the attack zone when the blow arrived (Strike); its own block or dodge decides the outcome.
        CombatTechnique? reaction = dSlot.ReactTech >= 0 && !dSlot.Down && now >= dSlot.StunUntil ? _techniques[dSlot.ReactTech] : null;
        float progress = reaction is not null ? Math.Clamp((now - dSlot.ReactStart) / (float)dSlot.ReactTicks, 0, 1) : 0;
        var outcome = HitOutcomes.Decide(reaction, progress, direction);

        if (live) aSlot.LastOutcome = outcome;
        else if (aSlot.AttackSeq == seq) Pause(world, ref aSlot, outcome, now);

        // A blow that lands or is blocked takes the defender off the attacker's list of fighters it lets close in (attackImpactCheck →
        // CombatClass::removeAttackerH on the attacker's own list, docs/game/combat.md "Attack slots and circling"): a defender fighting the attacker
        // asks again before its next swing.
        if (outcome is BlowOutcome.Hit or BlowOutcome.Blocked && dSlot.TargetSlot == a && dSlot.TargetGeneration == prev[a].Generation)
            dSlot.Engaged = false;

        switch (outcome)
        {
            case BlowOutcome.Missed:
                Record(now, a, d, blow, outcome, -1, default, false, false);
                return;
            case BlowOutcome.Blocked:
                ApplyBlocked(world, a, d, blow, ac, dc, tech, live, now);
                return;
            case BlowOutcome.Dodged:
                aSlot.Evaded++;
                dSlot.Dodges++;
                GainCombatXp(ac, dc, XpEvent.GlancingHit, null);
                var dodgeStats = dc.Stats;
                _xp.PerEvent(dodgeStats, dc.Race, StatsEnumerated.Dodge,
                    0.25f * _xp.SkillDifferenceFactor(dodgeStats[StatsEnumerated.Dodge], Eff(ac, StatsEnumerated.MeleeAttack)));
                Punch(world, a, ac, dc, tech, blow, 0);
                Record(now, a, d, blow, outcome, -1, default, false, false);
                return;
        }

        LandBlow(world, e, ref aSlot, ref dSlot, ac, dc, tech, direction);
    }

    /// <summary>
    /// A blocked blow: the parry and block counts and XP, the block's sound and sparks (a dodge that counts as a block is silent), the swing stopped
    /// while it is still the one, the puncher's own limb.
    /// </summary>
    void ApplyBlocked(World world, int a, int d, int blow, CharacterCold ac, CharacterCold dc, CombatTechnique tech, bool live, int now)
    {
        var prev = world.Characters.Previous;
        ref var aSlot = ref _write[a];
        ref var dSlot = ref _write[d];
        aSlot.Parried++;
        dSlot.Blocks++;
        GainCombatXp(ac, dc, XpEvent.GlancingHit, XpEvent.Defended);

        // A dodge technique that counts as a block (the base game's "Dodge back" rows are flagged both) is silent: it looks like a dodge, and
        // the block impact sounds like a hit.
        bool parry = IsParry(ac, dc, tech, blow);
        if (!_techniques[dSlot.ReactTech].IsDodge) Sound(world, SoundKind.Blocked, d, a, prev[d].Position, unarmed: !parry, robot: false);

        // A parry throws sparks at the parrying weapon (FUN_1404394f0, IsParry); a fist blow of a plain hand-to-hand impact does not.
        if (!_techniques[dSlot.ReactTech].IsDodge && parry) world.BlockSparks?.Enqueue(Id(world, d));
        if (live) Stop(ref aSlot, tech, blow);

        Punch(world, a, ac, dc, tech, blow, 5);
        Record(now, a, d, blow, BlowOutcome.Blocked, -1, default, false, false);
    }

    /// <summary>
    /// A blow that hits (<see cref="ApplyBlow"/> checked both fighters, their stats, bodies and races): the packet through armour and the body, the
    /// medical events, the defender down or staggered, XP, sounds, blood, the knock-down, crimes and relations, the record and the hit observers.
    /// </summary>
    void LandBlow(World world, in Effect e, ref CombatSlot aSlot, ref CombatSlot dSlot, CharacterCold ac, CharacterCold dc, CombatTechnique tech,
        int direction)
    {
        var table = world.Characters;
        var prev = table.Previous;
        int a = e.Source, d = e.Target, blow = e.Arg, seq = (int)e.Amount, now = (int)world.Tick;
        var fighter = ac.Fighter!;
        var target = dc.Fighter!;
        float toughness = Eff(dc, StatsEnumerated.Toughness);
        var packet = BlowPacket(world, a, ac, dc, fighter, target, tech, blow);

        var ctx = MakeContext(world, d, dc);
        var events = _hitEvents;   // Apply is serial, so one list serves every hit
        events.Clear();
        var cover = new CoverRoll(world.Seed, Rng.Key(table.IdOf(d)), a, seq, blow);
        var medical = dc.Medical!;
        bool observe = world.WantsCombatHits;
        bool wasAlive = observe && !medical.Dead, wasIncapacitated = observe && medical.Incapacitated;
        bool intended = observe && d == aSlot.TargetSlot && prev[d].Generation == aSlot.TargetGeneration;
        var (part, damage, absorbed) =
            HitResolver.LandDetailed(medical, ctx, packet, target.Armour, cover, toughness, tech.LowStrike, direction == 6, _combat, events);
        FloatHit(world, d, dc, ac.IsPlayer, part, damage, absorbed);
        aSlot.Landed++;
        dSlot.Taken++;
        dSlot.HitTick = now;
        dSlot.HitPart = part;
        dSlot.HitFrom = a;
        dSlot.HitHeavy = false;

        // From behind (docs/animation.md "Hit reactions"): the attacker is more than 90 degrees off the defender's heading (yaw 0 along +Z,
        // towards +X).
        var toAttacker = new Vector2(prev[a].Position.X - prev[d].Position.X, prev[a].Position.Z - prev[d].Position.Z);
        dSlot.HitBehind = toAttacker.X * MathF.Sin(prev[d].Yaw) + toAttacker.Y * MathF.Cos(prev[d].Yaw) < 0;
        var (knockout, death) = HitEvents(world, d, dc, medical, dc.Race!, events);
        if (ShackledKnockOut(world, a, seq, blow, d, dc, medical, ctx)) knockout = true;

        BlowReaction(world, ref dSlot, d, dc, medical, damage, toughness, now, CombatRolls.BlowCounter(a, seq, blow, 0));
        GainCombatXp(ac, dc, XpEvent.HitDealt, XpEvent.HitTaken, packet.Cut + packet.Blunt + packet.Pierce + packet.Stun);
        Punch(world, a, ac, dc, tech, blow, 2);
        Sound(world, SoundKind.Hit, d, a, prev[d].Position, heavy: dSlot.HitHeavy, unarmed: fighter.Unarmed, robot: ac.Race!.IsRobot);
        Splash(world, d, dc, -new Vector2(toAttacker.X, toAttacker.Y), damage.Cut + damage.Blunt + damage.Pierce);
        if (knockout || death) Knockdown(world, d, dc, -toAttacker, damage.Cut + damage.Blunt + damage.Pierce, part);
        if (death && world.Luckyren) dc.Burst = -new Vector2(toAttacker.X, toAttacker.Y);
        if (medical.Incapacitated) Sound(world, SoundKind.Down, d, a, prev[d].Position, fatal: death);

        BlowConsequences(world, a, d, ac, dc, medical, death);
        Record(now, a, d, blow, BlowOutcome.Hit, part, damage, knockout, death);
        if (observe && part >= 0)
        {
            world.ObserveCombatHit(new CombatHit(world.Tick, CombatHitSource.Melee, table.IdOf(a), table.IdOf(d), seq, blow, part,
                damage, absorbed, prev[d].Position, new(-toAttacker.X, 0, -toAttacker.Y), prev[d].Yaw,
                direction, tech.LowStrike, intended, wasAlive, wasIncapacitated));
        }
    }

    /// <summary>
    /// A blow's damage packet: the fist packet of one fighting unarmed or the weapon's, against the defender's kind (robot, animal or human) and
    /// race, times the impact point's power (<see cref="Powered"/>).
    /// </summary>
    DamagePacket BlowPacket(World world, int a, CharacterCold ac, CharacterCold dc, Fighter fighter, Fighter target, CombatTechnique tech, int blow)
    {
        var race = dc.Race!;
        TargetKind kind = race.IsRobot ? TargetKind.Robot : target.IsAnimal ? TargetKind.Animal : TargetKind.Human;
        float skill = Eff(ac, fighter.WeaponSkill);
        float dex = Eff(ac, StatsEnumerated.Dexterity) * fighter.DexterityMult;
        float str = Eff(ac, StatsEnumerated.Strength);
        var packet = fighter.Unarmed
            ? DamageFormulas.UnarmedPacket(UnarmedRating(world, a, ac, fighter), Eff(ac, StatsEnumerated.Toughness), str, fighter.FistsItem, kind,
                race.StringId, fighter.DamageOutput, _combat)
            : DamageFormulas.Packet(skill, dex, str, fighter.Weapon!, kind, race.StringId, fighter.DamageOutput, _combat);

        return Powered(packet, tech, blow);
    }

    /// <summary>
    /// The medical events of a landed blow or bolt: knock-out and limb-loss toughness XP, the look's lost head or limb and the victim's lost-limb
    /// line. Returns whether it was knocked out and whether it died.
    /// </summary>
    (bool Knockout, bool Death) HitEvents(World world, int v, CharacterCold dc, MedicalState medical, RaceData race, List<MedicalEvent> events)
    {
        var table = world.Characters;
        bool knockout = false, death = false;
        foreach (var ev in events)
        {
            switch (ev.Kind)
            {
                case MedicalEventKind.KnockedOut:
                    knockout = true;
                    if (dc.Stats is { } ko) _xp.ToughnessFromKnockOut(ko, race);
                    break;
                case MedicalEventKind.Died:
                    death = true;
                    break;
                case MedicalEventKind.Decapitated:
                    // the look loses its head (nothing drops)
                    table.LimbsChanged.Enqueue((v, -1, false));
                    break;
                case MedicalEventKind.PartSevered:
                    if (dc.Stats is { } st) _xp.ToughnessFromLimbLoss(st, race);
                    table.LimbsChanged.Enqueue((v, medical.Parts[ev.Part].Template.LimbSlot, true));

                    // LOST_LEG / LOST_ARM
                    world.Defer(new Dialogue.DialogueEvent(table.IdOf(v), CharacterId.None,
                        medical.Parts[ev.Part].Template.LimbSlot is 2 or 3 ? 70 : 71)
                    {
                        Tick = world.Tick + 1,
                    });
                    break;
            }
        }

        return (knockout, death);
    }

    /// <summary>
    /// A shackled defender is knocked out instead of staggered a quarter of the time (<c>Character::_startStumble</c>: <c>MedicalSystem::knockout</c>
    /// with factor 0.002, the floor of 3 s). True when it was.
    /// </summary>
    static bool ShackledKnockOut(World world, int a, int seq, int blow, int d, CharacterCold dc, MedicalState medical, in MedicalContext ctx)
    {
        var table = world.Characters;
        bool knockedOut = !medical.Incapacitated && Meitou.Simulation.Locks.LockSystem.WornShackles(dc) is not null
            && CombatRolls.Float(world.Seed, Rng.Key(table.IdOf(d)), CombatRoll.Shackled,
                CombatRolls.BlowCounter(a, seq, blow, 0)) < ShackledKnockout;
        if (!knockedOut) return false;

        medical.KnockOut(medical.PutDownKoSeconds(ctx, 0.002f));
        return true;
    }

    /// <summary>
    /// The defender's reaction to a hit: down (its swing and reaction cut off), else a stagger, heavy past the stumble threshold.
    /// </summary>
    void BlowReaction(World world, ref CombatSlot dSlot, int d, CharacterCold dc, MedicalState medical, in HitDamage damage, float toughness, int now,
        ulong counter)
    {
        if (medical.Incapacitated)
        {
            dSlot.Down = true;
            dSlot.AttackTech = -1;
            dSlot.CancelTick = now;
            dSlot.NextBlow = 0;
            dSlot.ReactTech = -1;
        }
        else
        {
            bool heavy = damage.Cut + damage.Blunt + damage.Pierce + damage.ExtraStun > DamageFormulas.StumbleThreshold(toughness, _combat);
            Stagger(world, ref dSlot, d, dc, heavy, now, counter);
        }
    }

    /// <summary>
    /// What a hit means beyond the fighters: the police-hit rule first (the game's hit outcome runs before the damage), then the victim's alarm and
    /// replan and its faction's relation events (docs/game/factions-squads-towns.md 3.5), and a unique's death.
    /// </summary>
    void BlowConsequences(World world, int a, int d, CharacterCold ac, CharacterCold dc, MedicalState medical, bool death)
    {
        var table = world.Characters;
        Crimes?.Hit(world, a, d);
        if (!medical.Incapacitated)
        {
            // the victim senses its attacker, with the alarm flag (FUN_140999f00)
            world.SystemOf<AggroSystem>()?.Alarm(world, table.IdOf(d), table.IdOf(a));
            HitReplan(dc, world.Tick);
        }

        Deeds?.Blow(world, a, d, medical.Incapacitated, death);

        // A unique killed: dead in the registry, with the player flag when the killer is the player's (the body pass would mark it dead without).
        if (death && world.Uniques.RecordOf(world.Characters.IdOf(d)) is { } unique) world.Uniques.Died(unique, ac.IsPlayer, dc.Faction);
    }

    /// <summary>
    /// The martial artist's own limb takes its blow (<c>MedicalSystem::punchSomething</c> <c>FUN_140666780</c>, <b>Verified</b>, docs/game/combat.md
    /// "Punch self-damage"), at the end of every take-hit: <paramref name="material"/> is the hit material, 5 blocked, 0 dodged, 2 hit, and 1 (metal)
    /// against a robot race. Only a person fighting with fists whose race is not a robot, on the limb of the blow's impact point (<c>limb N</c>; 4,
    /// the weapon, takes none) unless a prosthetic is fitted there. Packet: cut 2 and blunt 2 on metal, cut 5 and blunt 1 blocked, else blunt 1;
    /// times (1 - 0.5 x (fist rating + toughness x scale) / 100), unclamped, x the Global damage multiplier x the gear's <c>fist injury mult</c>. The
    /// punch then pays the puncher's toughness XP (<see cref="XpService.ToughnessFromDamage"/>).
    /// </summary>
    void Punch(World world, int a, CharacterCold ac, CharacterCold dc, CombatTechnique tech, int blow, int material)
    {
        var f = ac.Fighter!;
        if (!f.Unarmed || f.IsAnimal || ac.Race!.IsRobot) return;

        int limb = tech.LimbOf(blow);
        if (limb is < 0 or > 3) return;

        if (dc.Race!.IsRobot) material = 1;
        var (cut, blunt) = material switch { 1 => (2f, 2f), 5 => (5f, 1f), 6 => (1f, 1f), _ => (0f, 1f) };
        var stats = ac.Stats!;
        float k = (1 - 0.5f * (UnarmedRating(world, a, ac, f) + stats.ScaledToughness) * 0.01f) * _options.GlobalDamageMultiplier * f.FistInjury;
        if (ac.Medical!.PunchDamage(limb, cut * k, blunt * k, _constants)) _xp.ToughnessFromDamage(stats, ac.Race, material);
    }

    /// <summary>
    /// A blow's packet times its impact point's <c>power N</c> (stored x0.01): cut, blunt, pierce and extra stun (<b>Verified</b>,
    /// <c>attackImpactCheck</c> FUN_14060a2f0 scales the first four <c>Damages</c> fields; base data: 60 / 40 on <c>Downward combo</c>, 180 on <c>ma
    /// mega kick</c>).
    /// </summary>
    internal static DamagePacket Powered(in DamagePacket p, CombatTechnique tech, int blow)
    {
        float k = tech.PowerOf(blow) * 0.01f;
        return p with { Cut = p.Cut * k, Blunt = p.Blunt * k, Pierce = p.Pierce * k, Stun = p.Stun * k };
    }

    /// <summary>
    /// The cover roll of one blow: a function of the defender, the blow and the armour piece.
    /// </summary>
    readonly struct CoverRoll(ulong seed, ulong defenderKey, int attacker, int attackSeq, int blow) : ICoverRoll
    {
        /// <summary>
        /// The roll for armour <paramref name="piece"/> covering the blow.
        /// </summary>
        public float Roll(int piece) =>
            CombatRolls.Float(seed, defenderKey, CombatRoll.Cover, CombatRolls.BlowCounter(attacker, attackSeq, blow, piece));
    }

    /// <summary>
    /// Queues the damage floater of a landed hit when the host shows them (<see cref="World.Floaters"/>).
    /// </summary>
    void FloatHit(World world, int d, CharacterCold dc, bool sourceIsPlayer, int part, in HitDamage damage, in ArmourAbsorbed absorbed)
    {
        if (world.Floaters is not { } queue || part < 0) return;

        var id = world.Characters.IdOf(d);
        bool tagged = Memory is { } memory && world.Player.Squad >= 0;
        queue.Enqueue(Floater.Hit(id, world.Characters.Previous[d].Position, dc.IsPlayer, sourceIsPlayer,
            dc.Medical!.Parts[part].Template.Name, damage, absorbed)
            with
        {
            TargetTempEnemy = tagged && Memory!.HasShort(world, world.Player.Squad, id, Dialogue.MemoryTag.TempEnemy),
            TargetTempAlly = tagged && Memory!.HasShort(world, world.Player.Squad, id, Dialogue.MemoryTag.TempAlly),
        });
    }

    /// <summary>
    /// Adds the blow to the combat log while <see cref="CombatOptions.RecordLog"/> is on.
    /// </summary>
    void Record(int now, int a, int d, int blow, BlowOutcome outcome, int part, in HitDamage damage, bool knockout, bool death)
    {
        if (Settings.RecordLog)
            _log.Add(new CombatLogEntry(now, a, d, blow, outcome, part, damage.Cut, damage.Blunt, damage.Pierce, damage.ExtraStun, knockout, death));
    }

    #endregion

    #region Reactions

    /// <summary>
    /// A hit's reaction (<b>Verified</b>, FUN_1404391f0 / FUN_140665490, docs/game/combat.md "Hit reactions"): a stumble clip is picked unless the
    /// hit is light and one is already playing; it is big when heavy and none was playing. A picked clip cancels the defender's own swing (that blow
    /// never lands) and its reaction, and holds it helpless until the clip ends (the knockback is the clip's root motion). With no clip to play the
    /// swing is still cancelled, with a short pause. Takes effect at the next Act. A <paramref name="bolt"/> leaves one getting up alone with no
    /// knock-down roll (its shot handler <c>FUN_140999d50</c> has none).
    /// </summary>
    void Stagger(World world, ref CombatSlot dSlot, int d, CharacterCold dc, bool heavy, int now, ulong counter, bool bolt = false)
    {
        // Outside a fight (docs/game/combat.md "Stagger outside a fight"): one getting up is left alone; any other drops its task, which is planned
        // again from its start, unless it mans a turret (shooting at a live target).
        bool outsideFight = dSlot.TargetSlot < 0 && !dc.InCombat;
        if (outsideFight)
        {
            if (CharacterFacts.GettingUp(dc, now))
            {
                if (!bolt) UnderAttackGettingUp(world, d, dc, counter);
                return;
            }

            if (dc.Gunning is null)
            {
                // a put-down is finished by the hit, a lift past its attach keeps the body
                world.SystemOf<Carrying.CarrySystem>()?.Interrupted(world, d);
                Restart(world.Characters, d, dc);

                // forceOrderInsert replans (the stumble is not modelled as an order)
                DecisionSystem.Request(dc);
            }
        }

        bool stumbling = dSlot.StumbleClip >= 0 && now < dSlot.StunUntil;
        dSlot.HitHeavy = heavy && !stumbling;
        if (stumbling && !heavy) return;

        dSlot.AttackTech = -1;
        dSlot.CancelTick = now;
        dSlot.NextBlow = 0;
        dSlot.ReactTech = -1;
        string? part = dSlot.HitPart >= 0 && dSlot.HitPart < dc.Medical!.Parts.Count ? dc.Medical.Parts[dSlot.HitPart].Template.StringId : null;
        float roll = CombatRolls.Float(world.Seed, Rng.Key(world.Characters.IdOf(d)), CombatRoll.Stumble, counter);
        if (part is not null && StumbleChooser?.Invoke(dc, part, dSlot.HitHeavy, dSlot.HitBehind ? 1 : 0, roll) is { } clip)
        {
            dSlot.StumbleClip = clip.Clip;
            dSlot.StumbleStart = now;
            dSlot.BreakOutTick = now + Math.Max(1, (int)MathF.Round(CombatTuning.BreakOutSeconds / world.TickSeconds));
            dSlot.StunUntil = now + Math.Max(1, (int)MathF.Ceiling(clip.Seconds / world.TickSeconds));

            // the stumble state (8) ends the pause (FUN_14060cf60 clears +0x144)
            dSlot.PauseUntil = 0;
            dc.StaggeredUntil = dSlot.StunUntil;

            // reported as Stumbling (147) meanwhile
            if (outsideFight && dc.Gunning is null) dc.StumbleTaskUntil = dSlot.StunUntil;
            dSlot.ReadyTick = Math.Max(dSlot.ReadyTick, dSlot.StunUntil);
        }
        else
        {
            dSlot.ReadyTick = Math.Max(dSlot.ReadyTick, now + (int)MathF.Ceiling(CombatTuning.InterruptPause / world.TickSeconds));
        }
    }

    /// <summary>
    /// A task cut off by a hit is planned again from its start (the original ends it and queues it behind Stumbling): its timer and any lift or
    /// knock-out swing start over, and a walk asks for a new path from where the hit left it. What each task keeps of its progress (a lock's pick
    /// time, a site's or a machine's work) stays with its object.
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

    /// <summary>
    /// The pause after a swing whose last blow was resolved after Act ended the swing (on the same tick): Act chose it from the blow before, so it is
    /// chosen again from this outcome.
    /// </summary>
    void Pause(World world, ref CombatSlot attacker, BlowOutcome outcome, int now)
    {
        // the swing ended with no pause of its own
        if (attacker.ReadyTick <= now) return;

        float pause = outcome switch
        {
            BlowOutcome.Blocked => CombatTuning.BlockedPause,
            BlowOutcome.Dodged => CombatTuning.DodgedPause,
            BlowOutcome.Missed => CombatTuning.MissedPause,
            _ => CombatTuning.HitPause,
        };
        bool held = attacker.PauseUntil == attacker.ReadyTick;
        attacker.ReadyTick = now + Math.Max(1, (int)MathF.Ceiling(pause / world.TickSeconds));
        if (held) attacker.PauseUntil = attacker.ReadyTick;

        attacker.LastOutcome = outcome;
    }

    /// <summary>
    /// Stops the swing at the blow's stop frame (a blocked blow: the rest of the combo is not thrown).
    /// </summary>
    static void Stop(ref CombatSlot attacker, CombatTechnique tech, int blow)
    {
        // A blocked blow stops the swing at its "stop frame": the rest of the combo is not thrown.
        float stop = tech.StopProgress(blow);
        if (stop < 1) attacker.AttackEnd = Math.Min(attacker.AttackEnd, attacker.AttackStart + (int)MathF.Ceiling(stop * attacker.AttackTicks));

        attacker.NextBlow = 0;
    }

    #endregion

    #region Experience

    /// <summary>
    /// The XP of a blow for both sides: the attacker's weapon skill (or unarmed) and strength, the defender's defence and toughness by the damage
    /// it took.
    /// </summary>
    void GainCombatXp(CharacterCold attacker, CharacterCold defender, XpEvent attackerEvent, XpEvent? defenderEvent, float damageTaken = 0)
    {
        var af = attacker.Fighter!;
        var df = defender.Fighter!;
        float aStrength = StrengthFactor(attacker, af);
        var attackerWeapon = af.Unarmed ? StatsEnumerated.None : af.Weapon!.Skill;
        _xp.Combat(attacker.Stats!, attacker.Race!, attackerEvent, af.Unarmed, attackerWeapon,
            Eff(defender, StatsEnumerated.MeleeDefence), aStrength);
        if (defenderEvent is { } ev)
        {
            float dStrength = StrengthFactor(defender, df);
            var weaponStat = df.Unarmed || ev == XpEvent.HitTaken ? StatsEnumerated.None : df.Weapon!.Skill;
            var attackerStat = af.Unarmed ? StatsEnumerated.MartialArts : StatsEnumerated.MeleeAttack;
            _xp.Combat(defender.Stats!, defender.Race!, ev, df.Unarmed, weaponStat, Eff(attacker, attackerStat), dStrength, damageTaken);
        }
    }

    /// <summary>
    /// The strength XP factor of a fighter: 1 - encumbrance unarmed, else its weapon's weight against its strength.
    /// </summary>
    float StrengthFactor(CharacterCold c, Fighter f) => f.Unarmed
        ? 1 - f.EncumbranceFactor
        : _xp.WeaponWeightStrengthFactor(f.Weapon!.Stats.Weight, c.Stats![StatsEnumerated.Strength],
            c.Medical!.StatMultiplier(StatsEnumerated.Strength));

    #endregion
}
