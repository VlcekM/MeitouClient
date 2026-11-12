using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Combat;

/// <summary>
/// The Act step (parallel, per character): the medical tick, the defence against the blows coming at it, its swing and the blows it sends, and the
/// start of a new attack.
/// </summary>
public sealed partial class CombatSystem
{
    // ------------------------------------------------------------------ act

    #region Gear and ratings

    /// <summary>
    /// The guarding state: the fighter's own, or the Block order (its tooltip's +20 melee defence; docs/game/combat.md).
    /// </summary>
    static bool Guarding(CharacterCold c) => c.Fighter!.Guarding || Orders.Has(c, CharacterOrders.Block);

    /// <summary>
    /// The weapon's <c>indoors mod</c> (CharStats <c>+0x28</c>) while the fighter is in a building's indoor volume (<see cref="SightMap.Inside"/>;
    /// the original also asks a vfunc <c>0x1e0</c> and the building's <c>+0xc2</c>, <b>Unknown</b>), else 0 (<c>FUN_1408844c0</c>,
    /// <c>FUN_1408862d0</c>; docs/game/combat.md "Indoors"). With fists it is the fists item's (the <c>FISTS</c> record's 8), and 8 when there is no
    /// item at all (<c>FUN_140884370</c>).
    /// </summary>
    static int IndoorsMod(World world, int i, Fighter f) => world.Sight.Inside(i) ? (f.Weapon ?? f.Fists)?.Stats.IndoorsMod ?? 8 : 0;

    /// <summary>
    /// An attack bonus indoors (<c>FUN_1408844c0</c>): a positive bonus is dropped under a negative modifier, then the modifier is added.
    /// </summary>
    static float IndoorsAttackBonus(float bonus, int mod) => (bonus > 0 && mod < 0 ? 0 : bonus) + mod;

    /// <summary>
    /// A stat as the combat formulas read it: times the injury, hunger and light multiplier and the stats' read-time scale (an animal's age,
    /// docs/game/animals.md); crossbows and assassination also times the worn gear's factor (<see cref="Fighter.Factors"/>; the original's stat
    /// getter <c>FUN_140884ac0</c> asks for all of them). Dexterity's and the weapon skills' gear factors are applied by the formulas themselves
    /// (<see cref="Fighter.DexterityMult"/>, <see cref="Fighter.DamageOutput"/>).
    /// </summary>
    internal static float Eff(CharacterCold c, StatsEnumerated stat) =>
        (c.Stats![stat] + GearBonus(c, stat)) * c.Stats.Scale * c.Medical!.StatMultiplier(stat) * GearFactor(c, stat);

    /// <summary>
    /// The HUD's "Damage:" figure of a melee fighter (the Current Skill line's tooltip) and the stats window's "Martial arts damage"
    /// (<c>FUN_140892120</c>, <c>FUN_1408b1250</c>): the sum of the four terms of its blow (<c>FUN_1408868b0</c> with no target, so no kind or race
    /// multiplier; the gear's damage output applies), unpowered: the fists' for an unarmed fighter, else the weapon's.
    /// </summary>
    internal float HudDamage(World world, int slot, CharacterCold c)
    {
        if (c is not { Fighter: { } f, Stats: not null, Medical: not null }) return 0;
        float str = Eff(c, StatsEnumerated.Strength);
        return (f.Unarmed
            ? DamageFormulas.UnarmedPacket(UnarmedRating(world, slot, c, f), Eff(c, StatsEnumerated.Toughness), str, f.FistsItem,
                TargetKind.None, null, f.DamageOutput, _combat)
            : DamageFormulas.Packet(Eff(c, f.WeaponSkill), Eff(c, StatsEnumerated.Dexterity) * f.DexterityMult, str, f.Weapon!,
                TargetKind.None, null, f.DamageOutput, _combat)).Sum;
    }

    /// <summary>
    /// The HUD's crossbow accuracy (the Current Skill line in ranged mode): the crossbow's deviation at 0 skill, the effective crossbows skill and
    /// the resulting deviation in degrees (<see cref="RangedFormulas.Deviation"/>, as a shot takes it); null without a crossbow.
    /// <paramref name="stat"/>: Crossbows, or Turrets when <see cref="CharacterCold.LastWeaponCategory"/> is 7 (the crossbow's deviation with the
    /// Turrets level).
    /// </summary>
    internal static (float Weapon, float Skill, float Deviation)? HudAccuracy(CharacterCold c, StatsEnumerated stat = StatsEnumerated.Crossbows)
    {
        if (c is not { Fighter.Crossbow: { } x, Stats: not null, Medical: not null }) return null;
        float skill = Eff(c, stat);
        return (x.Stats.AccuracyDeviation, skill, RangedFormulas.Deviation(x.Stats.AccuracyDeviation, skill, Eff(c, StatsEnumerated.Perception)));
    }

    /// <summary>
    /// The worn gear's bonus added to the trained level before the multiplier (<c>FUN_140884ac0</c>): the armour's <c>perception bonus</c> for
    /// perception, else 0.
    /// </summary>
    internal static int GearBonus(CharacterCold c, StatsEnumerated stat) => stat == StatsEnumerated.Perception ? c.Fighter?.PerceptionBonus ?? 0 : 0;

    /// <summary>
    /// The fist rating (<see cref="DamageFormulas.UnarmedRating"/>, <c>FUN_140884e00</c> with its flag set, as every combat caller passes it): the
    /// raw martial arts stat (no injury multiplier), the gear's unarmed bonus with the indoors modifier inside (<see cref="IndoorsAttackBonus"/>),
    /// the dust penalty and the load. A fist blow's damage, an unarmed fighter's attack rating (<c>FUN_140884ee0</c>) and its defence rating
    /// (<c>FUN_140886290</c>) are this number.
    /// </summary>
    internal static float UnarmedRating(World world, int slot, CharacterCold c, Fighter fighter) =>
        DamageFormulas.UnarmedRating(c.Stats![StatsEnumerated.MartialArts] * c.Stats.Scale,
            IndoorsAttackBonus(fighter.UnarmedBonus, IndoorsMod(world, slot, fighter)),
            c.Medical!.DustAttackPenalty, fighter.EncumbranceFactor);

    /// <summary>
    /// The worn gear's factor of a stat that <see cref="Eff"/> applies: <c>ranged skill mult</c> for crossbows, <c>asassination mult</c> for
    /// assassination, else 1.
    /// </summary>
    internal static float GearFactor(CharacterCold c, StatsEnumerated stat) => stat switch
    {
        StatsEnumerated.Crossbows => c.Fighter?.Factors.Ranged ?? 1,
        StatsEnumerated.Assassination => c.Fighter?.Factors.Assassin ?? 1,
        _ => 1,
    };

    #endregion

    #region Tick timers and play rates

    /// <summary>
    /// The ticks a technique's clip takes at <c>anim speed mult</c> x <paramref name="rate"/> clip seconds per second; the clip's length is from the
    /// fighter's skeleton (an animal's own, <see cref="AnimationLengths.For"/>: <c>attack1</c> is 0.77 s on a bonedog and 5 s on a beak thing).
    /// </summary>
    int Ticks(CombatTechnique t, float rate, float dt, CharacterCold cold)
    {
        float clip = _lengths.For(cold.Fighter is { IsAnimal: true } ? cold.Race?.StringId : null).Of(t.AnimName);
        if (clip <= 0) clip = CombatTuning.DefaultClipSeconds;
        return Math.Max(2, (int)MathF.Ceiling(clip / (MathF.Max(t.AnimSpeedMult, 0.1f) * MathF.Max(rate, 0.05f)) / dt));
    }

    /// <summary>
    /// The seconds one swing of an animal's door bash takes (<c>Task_BashDoor</c>, <c>FUN_140887420</c>, <b>Verified</b>): the first attack
    /// technique, in data order, that fits the animal's weapon category (not <c>disabled</c>, no block or dodge) plays at speed 1; its clip's length
    /// from the animal's own skeleton over the technique's <c>anim speed mult</c>. 0 when there is none or the clip is unknown (the caller keeps the
    /// human cycle).
    /// </summary>
    public float AnimalBashSeconds(CharacterCold cold)
    {
        if (cold.Fighter is not { IsAnimal: true } f) return 0;
        foreach (var t in _asWritten)
        {
            if (t.Disabled || t.IsBlock || t.IsDodge || !TechniqueChooser.Fits(t, f.AnimalCategory)) continue;
            return _lengths.For(cold.Race?.StringId).Of(t.AnimName) / MathF.Max(t.AnimSpeedMult, 0.1f);
        }
        return 0;
    }

    /// <summary>
    /// The playback rate of a technique (docs/game/combat.md "Animation speed", <b>Verified</b>): attacks lerp(t, 0.8, 1.2) x G capped at 1.2, blocks
    /// lerp(t, 0.6, 4) x G capped at 4, with t = 0.01 (0.6 dexterity + 0.4 skill) (attack, or defence with the bonuses); dodges 1. G = (0.5 + 0.5
    /// min(1, e + 0.5) (1 - f)) x the gear's <c>combat speed mult</c>, e the encumbrance factor and f the weapon-weight-over-strength factor of the
    /// strength XP.
    /// </summary>
    float PlayRate(World world, int slot, CharacterCold cold, CombatTechnique tech, bool attack)
    {
        if (!attack && (tech.IsDodge || !tech.IsBlock)) return 1;
        return Rate(world, slot, cold, attack);
    }

    /// <summary>
    /// The attack play rate of a fighter (its <see cref="PlayRate"/> for any attack; CharStats +0x188 in the original), which the chase lead also
    /// reads.
    /// </summary>
    public float AttackRate(World world, int slot, CharacterCold cold) => Rate(world, slot, cold, true);

    /// <summary>
    /// The attack rating (<c>FUN_140884ee0</c>, <b>Verified</b>): unarmed, the fist rating; else the trained melee attack (no injury or hunger
    /// multiplier, times the stats' scale) plus the gear's attack bonus and the weapon's attack mod with the indoors modifier inside, less the dust
    /// penalty.
    /// </summary>
    internal static float AttackRating(World world, int slot, CharacterCold c, Fighter f) => f.Unarmed ? UnarmedRating(world, slot, c, f)
        : Trained(c, StatsEnumerated.MeleeAttack) + IndoorsAttackBonus(f.AttackBonus + (f.Weapon?.Stats.AttackMod ?? 0), IndoorsMod(world, slot, f))
            - c.Medical!.DustAttackPenalty;

    /// <summary>
    /// The defence rating (<c>FUN_140886290</c> → <c>FUN_140884f50</c>, <b>Verified</b>): unarmed, the fist rating; else the trained melee defence
    /// (no injury or hunger multiplier) plus the gear's defence bonus, the weapon's defence mod, the indoors modifier inside and, with
    /// <paramref name="guard"/> (the function's flag), 20 when guarding. The block contest's defence (<see cref="DefenceFormulas.Defence"/>) is the
    /// same sum.
    /// </summary>
    internal static float DefenceRating(World world, int slot, CharacterCold c, Fighter f, bool guard = true) =>
        f.Unarmed ? UnarmedRating(world, slot, c, f)
        : Trained(c, StatsEnumerated.MeleeDefence) + f.DefenceBonus + (f.Weapon?.Stats.DefenceMod ?? 0) + IndoorsMod(world, slot, f)
            + (guard && Guarding(c) ? 20 : 0);

    /// <summary>
    /// A trained level as the combat ratings read it (CharStats <c>+0x120</c>, <c>+0x124</c>): the stored level times the stats' read-time scale, no
    /// multiplier.
    /// </summary>
    static float Trained(CharacterCold c, StatsEnumerated stat) => c.Stats![stat] * c.Stats.Scale;

    /// <summary>
    /// The attack and block play rates (CharStats <c>+0x188</c> / <c>+0x18c</c>, <c>FUN_1408866b0</c> → <c>FUN_140883a80</c>,
    /// <b>Verified</b>): from dexterity and the <see cref="AttackRating"/> or <see cref="DefenceRating"/>; both x 0.8 while swimming
    /// (water state 3, <c>FUN_1405c7fd0</c>).
    /// </summary>
    float Rate(World world, int slot, CharacterCold cold, bool attack)
    {
        var fighter = cold.Fighter!;
        float weight = fighter.Unarmed ? 0 : fighter.Weapon!.Stats.Weight;
        float f = Math.Clamp(_xp.WeaponWeightStrengthFactor(weight, cold.Stats![StatsEnumerated.Strength],
            cold.Medical!.StatMultiplier(StatsEnumerated.Strength)), 0, 1);
        float g = (0.5f + 0.5f * MathF.Min(1, fighter.EncumbranceFactor + 0.5f) * (1 - f)) * fighter.CombatSpeed;
        float dex = Eff(cold, StatsEnumerated.Dexterity) * fighter.DexterityMult;
        float skill = attack ? AttackRating(world, slot, cold, fighter) : DefenceRating(world, slot, cold, fighter);
        float t = 0.01f * (0.6f * dex + 0.4f * skill);
        float swim = cold.WaterState == 3 ? 0.8f : 1;
        return swim * (attack ? MathF.Min(1.2f, WeaponStats.Lerp(t, 0.8f, 1.2f) * g) : MathF.Min(4f, WeaponStats.Lerp(t, 0.6f, 4f) * g));
    }

    #endregion

    #region Per-tick act

    /// <summary>
    /// Each character's fight as its body step left it (<see cref="BeforeAct"/>): what <see cref="Act"/> reads of another character's fight, which
    /// that one's own Act may end meanwhile (<see cref="EndFight(CharacterTable, int, ref CombatSlot)"/>). Derived each tick, not hashed.
    /// </summary>
    bool[] _inFight = [];

    /// <summary>
    /// Step 1 of a fighter's update, the body (bleeding, healing, waking, the arms, getting up, the fight's end on going down), for every character
    /// before any of them decides (<see cref="Act"/>): the decisions read these of their targets and candidates, so they must not depend on which
    /// partition ran first.
    /// </summary>
    public void BeforeAct(World world, Partition part)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        int now = (int)world.Tick;
        float dt = world.TickSeconds;
        List<MedicalEvent>? events = null;
        for (int i = part.Start; i < part.End; i++)
        {
            // the far cadence: a skipped fighter's tick timers run on, it acts on them at its step
            if (!prev[i].Alive || prev[i].Dormant || !world.Steps(i)) continue;
            var cold = table.Cold(i)!;
            Body(world, i, cold, ref _write[i], now, dt, ref events);
            _inFight[i] = cold.InCombat;
        }
    }

    /// <summary>
    /// A fighter's body step (<see cref="BeforeAct"/>): the medical tick, the arms, the weapon put away out of a fight, waking and getting up, and
    /// the fight's end on going down.
    /// </summary>
    void Body(World world, int i, CharacterCold cold, ref CombatSlot s, int now, float dt, ref List<MedicalEvent>? events)
    {
        if (cold.Fighter is null || cold.Stats is null || cold.Medical is null || cold.Race is null) return;
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;

        // 1. The body: bleeding, healing, waking.
        if (Settings.TickMedical)
        {
            events ??= [];
            events.Clear();
            cold.Medical.Tick(dt, MakeContext(world, i, cold), events);
        }

        FollowArms(cold, ref s);

        // A player's character out of a fight keeps its weapon out while it walks or follows on an order; idle or at work it sheathes (Idleness and
        // the jobs need it away).
        if (cold.IsPlayer && !cold.InCombat && cold.DrawnWeapon != WeaponKinds.None
            && next[i].Task is not ((byte)CharacterTask.GoTo or (byte)CharacterTask.Follow or (byte)CharacterTask.Attack))
            cold.DrawnWeapon = WeaponKinds.None;

        WakeUp(world, i, cold, ref s, now, dt);

        // The leg back up (posture 2 to 0, not a waking): the posture setter queues the get-up task 65 (FUN_1405c7e20), which stands the character
        // through the `standing up 3` clip with no bone blend (no ragdoll to leave: FUN_140334a20), holding it like a waker.
        bool crawling = cold.Medical.Crawling;
        if (s.Crawl && !crawling && !s.Down && !cold.Medical.Incapacitated && !cold.Medical.Prone && GetUpClipSeconds > 0 && cold.CarriedBy.IsNone)
        {
            cold.GettingUpUntil = now + (int)MathF.Ceiling(GetUpClipSeconds / dt);
            cold.StaggeredUntil = Math.Max(cold.StaggeredUntil, cold.GettingUpUntil);
            if (Orders.Has(cold, CharacterOrders.Hold)) cold.HoldPoint = prev[i].Position;
        }
        s.Crawl = crawling || cold.Medical.StayingLow;   // any posture above 0: the setter queues task 65 whenever it returns to 0 (FUN_1405c7e20)
        s.Down = cold.Medical.Incapacitated;
        if (s.Down)
        {
            s.AttackTech = -1;
            s.NextBlow = 0;
            s.ReactTech = -1;
            s.TargetSlot = -1;
            s.Engaged = false;
            s.Picked = false;
            if (cold.InCombat) EndFight(table, i, ref s);
            return;
        }
        if (CharacterFacts.GettingUp(cold, now))
        {
            s.AttackTech = -1;
            s.NextBlow = 0;
            s.ReactTech = -1;
        }
    }

    /// <summary>
    /// The weapon in hand follows the arms (docs/game/combat.md "Arms"): losing one puts the weapon away and the fight goes on with what the arms
    /// still allow.
    /// </summary>
    void FollowArms(CharacterCold cold, ref CombatSlot s)
    {
        if (cold.Fighter!.SetArms(cold.Medical!.ArmWorks(left: false), cold.Medical.ArmWorks(left: true)))
        {
            // The weapon put away ends the block it was held in (FUN_1405cc230 sheathes it on an arm's change); a dodge needs no weapon.
            if (s.ReactTech >= 0 && !cold.Fighter.CanBlock && !_techniques[s.ReactTech].IsDodge) s.ReactTech = -1;
            if (cold.InCombat && !s.Ranged) cold.DrawnWeapon = MeleeDrawn(cold.Fighter);
            // a weapon out on a walk follows the arms too
            else if (!cold.InCombat && cold.DrawnWeapon != WeaponKinds.None
                && cold.DrawnWeapon != HandHold.OfCategory((int)WeaponCategory.Bow).Weapon)
                cold.DrawnWeapon = MeleeDrawn(cold.Fighter);
        }
    }

    /// <summary>
    /// Waking: staying low or standing, the forced crawl's end, and the hold while it gets up.
    /// </summary>
    void WakeUp(World world, int i, CharacterCold cold, ref CombatSlot s, int now, float dt)
    {
        var table = world.Characters;
        var prev = table.Previous;

        // Waking while sneaking it stays low (posture 1, docs/game/ragdolls.md "Staying low"); otherwise it stands.
        if (s.Down && !cold.Medical!.Incapacitated)
        {
            cold.Medical.StayingLow = cold.Medical.WokenLow ?? Orders.Has(cold, CharacterOrders.Sneak);
            cold.Medical.WokenLow = null;
        }

        // A leg down replaces it with the forced crawl (posture 2), which ends standing.
        if (cold.Medical!.Crawling) cold.Medical.StayingLow = false;

        // Waking: it gets up (Task_GetUp, docs/game/ragdolls.md "Getting up"), less wanted as a target and left alone by a hit while its fight is
        // off. Standing up takes the clip's played length, after the bone blend when it wakes out of a whole-body ragdoll; one staying low or
        // crawling (posture above 0) is held only for that blend, else not at all (FUN_140334a20). One carried or in a bed plays no get-up clip
        // (AnimationSystem), so it is held for none either.
        if (s.Down && !cold.Medical.Incapacitated && GetUpClipSeconds > 0)
        {
            bool whole = world.SystemOf<Ragdolls.RagdollSystem>()?.WasWhole(table.IdOf(i)) == true;
            float hold = cold.Medical.StayingLow || cold.Medical.Crawling
                ? whole ? Ragdolls.RagdollSystem.GetUpBlend : 0
                : CharacterFacts.PlaysGetUp(cold) ? GetUpClipSeconds + (whole ? Ragdolls.RagdollSystem.GetUpBlend : 0) : 0;

            // It neither walks, works nor fights until then (Task_GetUp holds the character).
            if (hold > 0)
            {
                cold.GettingUpUntil = now + (int)MathF.Ceiling(hold / dt);
                cold.StaggeredUntil = Math.Max(cold.StaggeredUntil, cold.GettingUpUntil);
            }

            // The get-up's start puts the Hold point where it wakes (FUN_14033ca10).
            if (Orders.Has(cold, CharacterOrders.Hold)) cold.HoldPoint = prev[i].Position;
        }
    }

    /// <summary>
    /// Step 2 onward of each fighter's update in the partition (<see cref="ActOne"/>), after the body step (<see cref="BeforeAct"/>).
    /// </summary>
    public void Act(World world, Partition part, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        int now = (int)world.Tick;
        float dt = world.TickSeconds;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive || prev[i].Dormant || !world.Steps(i)) continue;
            var cold = table.Cold(i)!;
            if (cold.Fighter is null || cold.Stats is null || cold.Medical is null || cold.Race is null) continue;
            ActOne(world, i, cold, ref _write[i], now, dt, effects);
        }
    }

    /// <summary>
    /// One fighter's decisions this tick: a hit reaction it may break out of, the target, the crossbow or melee mode, the swing in progress and
    /// what it does next.
    /// </summary>
    void ActOne(World world, int i, CharacterCold cold, ref CombatSlot s, int now, float dt, EffectBuffer effects)
    {
        // Down or getting up (the body step, BeforeAct): it neither fights nor defends.
        if (s.Down || CharacterFacts.GettingUp(cold, now)) return;
        ulong key = Rng.Key(world.Characters.IdOf(i));

        if (Helpless(world, i, ref s, cold, key, now, dt)) return;

        bool fighting = ReviewTarget(world, i, ref s, cold, now);

        // 3b. A crossbowman fights with the crossbow or the melee weapon; with the crossbow the ranged controller runs instead of the melee
        // attack (a blow aimed at it is still met).
        ChooseMode(world, i, ref s, cold, fighting);
        if (s.Ranged)
        {
            EndReaction(world, i, ref s, now, dt);
            if (s.ReactTech < 0 && s.AttackTech < 0) React(world, i, ref s, cold, key, now, dt);
            else if (s.ReactTech >= 0) Reconsider(world, i, ref s, cold, key, now, dt);
            // its timers are seconds: a far character's step covers several ticks
            TickRanged(world, i, ref s, cold, effects, now, world.StepSeconds(i));
            return;
        }

        // 4. The reaction and the attack in progress.
        EndReaction(world, i, ref s, now, dt);
        float incoming = Incoming(world, i, now, out int yielder);
        fighting = TurnOnYielder(world, ref s, cold, now, yielder, fighting, key);
        bool aborted = SwingOn(world, i, ref s, cold, now, incoming, fighting, effects);
        SwingEnd(ref s, now, dt);

        Decide(world, i, ref s, cold, key, now, dt, effects, fighting, aborted, incoming);
    }

    /// <summary>
    /// Step 2: helpless in a hit reaction until its clip ends; every BreakOutSeconds of it the character may break out (the original's
    /// +0x124, FUN_1406085e0): a roll of its unarmed dodge chance against the strongest fighter swinging at it at 10 to 99 per cent of the
    /// swing; on success the stumble ends in a stumble-dodge row.
    /// True while it stays helpless (the fighter does nothing else this tick).
    /// </summary>
    bool Helpless(World world, int i, ref CombatSlot s, CharacterCold cold, ulong key, int now, float dt)
    {
        if (now < s.StunUntil)
        {
            s.AttackTech = -1;
            s.NextBlow = 0;
            int every = Math.Max(1, (int)MathF.Round(CombatTuning.BreakOutSeconds / dt));
            bool chance = s.StumbleClip >= 0 && now > s.StumbleStart && now >= s.BreakOutTick;
            s.ReactTech = -1;   // a hit reaction ends the defence
            if (chance && s.ReactTech < 0 && (s.TargetSlot >= 0 || cold.InCombat))
            {
                // Due, the timer stays out until a swing is in the window, then rolls on that tick; a failed roll starts it again
                // (FUN_1406085e0). A won roll ends the stumble and clears the no-defence timer, then starts a defence (FUN_1406085e0 -> state 3
                // -> FUN_140609290): a stumble-dodge row, or with none no technique and the no-defence time. That start fades the stumble clip
                // out (FUN_1405b36f0) once it has a swing to answer; without one it plays on.
                if (BreaksOut(world, i, cold, key, now, out bool rolled))
                {
                    s.StunUntil = now;
                    s.DefendReadyTick = Math.Min(s.DefendReadyTick, now);
                    if (MostAdvanced(world, i, now, s.ReactedSlot, landing: false).Attacker >= 0) s.StumbleClip = -1;
                    React(world, i, ref s, cold, key, now, dt, stumbling: true);
                }
                else if (rolled) s.BreakOutTick = now + every;
            }
            if (now < s.StunUntil) return true;
        }

        return false;
    }

    /// <summary>
    /// Step 3, the target: dropped when down or gone, chosen again on the pick timer or with none while in the fight. True when the fighter has a
    /// target after it.
    /// </summary>
    bool ReviewTarget(World world, int i, ref CombatSlot s, CharacterCold cold, int now)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;

        // 3. The target.
        // A target down or gone is dropped (state 4, re-decide): the fight ends at once when it was the task's own target (CombatClassAI update
        // FUN_14060d6a0, refocus read as clear), else the fighter chooses again at once and, with nobody to choose, fights on without a target
        // until nothing is left (NothingLeft). While deciding, it chooses again every PickSeconds, unless the target is the player's pick (a
        // focused order returns its target without scoring). A target caged or carried is dropped the same way, the player's pick too
        // (FUN_14060d6a0: down, dead, ragdolled or carried; imprisoned unless the fighter itself stumbles, which skips this step).
        bool fighting = s.TargetSlot >= 0;

        // The target's fight ended since it let this one close in: its engaged list was emptied (FUN_1406659b0 mode 2), the fighter keeps its
        // target and asks again below.
        if (s.Engaged && fighting && (uint)s.TargetSlot < (uint)prev.Length && !Listed(in s, in _read[s.TargetSlot])) s.Engaged = false;
        if (fighting && !(s.TargetSlot < prev.Length && prev[s.TargetSlot].Alive && prev[s.TargetSlot].Generation == s.TargetGeneration
            && !_read[s.TargetSlot].Down
            && table.Cold(s.TargetSlot) is { IsImprisoned: false, CarriedBy.IsNone: true, Medical.Incapacitated: false })
            && !FinishingSwing(in s, prev, now))
        {
            s.AttackTech = -1;
            s.NextBlow = 0;
            // Only a focused fight (the player's pick, rows 5 and 61) ends with its target: its start stores the target in CombatClassAI +0x2c8 and
            // clears refocus +0x2e8. Every other fight (rows 4, 13, 16, 227) starts with refocus set and no stored target, so a target down
            // leaves it deciding (state 4) and it picks the next one (FUN_14060d6a0, FUN_1406681e0, FUN_1403341f0, Verified); only the nothing-left
            // test ends it.
            bool task = s.Picked && s.TargetSlot == s.TaskSlot && s.TargetGeneration == s.TaskGeneration;
            int pick = task ? -1 : ChooseTarget(world, i, s, cold);
            s.PickTick = now + PickTicks(world, cold);
            if (pick >= 0)
            {
                Retarget(ref s, pick, prev[pick].Generation, now);
                next[i].TaskTime = 0;
            }   // its path is asked for at once, not after the wait the last target's failure set (PursuitSystem)
            else
            {
                fighting = false;
                s.TargetSlot = -1;
                s.Picked = false;
                if (task || NothingLeft(world, i, in s)) EndFight(table, i, ref s);
            }
        }

        // A player's pick is kept, unless no path leads to it (the focused target is dropped while its path is impossible).
        else if (fighting && (!s.Picked || PursuitSystem.PathImpossible(cold, new CharacterId(s.TargetSlot, s.TargetGeneration), now))
            && now >= s.PickTick && s.AttackTech < 0 && s.ReactTech < 0)
        {
            int pick = ChooseTarget(world, i, s, cold);
            s.PickTick = now + PickTicks(world, cold);
            if (pick >= 0 && pick != s.TargetSlot)
            {
                Retarget(ref s, pick, prev[pick].Generation, now);
                next[i].TaskTime = 0;
            }
            else if (pick < 0)
            {
                // Nobody qualifies, not even the current target (a holder's that left its area, a player's beyond 400): the chooser has no target
                // (state 9).
                fighting = false;
                s.TargetSlot = -1;
                if (NothingLeft(world, i, in s)) EndFight(table, i, ref s);
            }
        }

        // In the fight without a target: it chooses again every PickSeconds and leaves the fight once nothing is left.
        else if (!fighting && cold.InCombat && now >= s.PickTick && s.AttackTech < 0 && s.ReactTech < 0)
        {
            int pick = ChooseTarget(world, i, s, cold);
            s.PickTick = now + PickTicks(world, cold);
            if (pick >= 0)
            {
                Retarget(ref s, pick, prev[pick].Generation, now);
                next[i].TaskTime = 0;
                fighting = true;
            }
            else if (NothingLeft(world, i, in s)) EndFight(table, i, ref s);
        }

        return fighting;
    }

    /// <summary>
    /// Turns the fighter on the one that yielded the initiative to it and keeps the Block order's slot-freeing timer; true when it has a target
    /// after it.
    /// </summary>
    bool TurnOnYielder(World world, ref CombatSlot s, CharacterCold cold, int now, int yielder, bool fighting, ulong key)
    {
        var prev = world.Characters.Previous;

        // One that lost the initiative roll to it: it turns on that one (FUN_140666a30 sets the winner to attack the asker), unless it fights the
        // player's pick.
        if (yielder >= 0 && yielder != s.TargetSlot && !s.Ranged && !(fighting && s.Picked))
        {
            s.TargetSlot = yielder;
            s.TargetGeneration = prev[yielder].Generation;
            s.Engaged = false;
            s.Picked = false;
            fighting = true;
        }
        if (!fighting) s.Engaged = false;

        // The Block order, standing and let close in: every 1.5-4.5 s it frees the attack slots on itself, so others may start swinging at it
        // (CircleStateAI's timer).
        if (fighting && s.Engaged && Orders.Has(cold, CharacterOrders.Block) && !cold.Medical!.Prone)
        {
            if (s.BlockTimerTick == 0) s.BlockTimerTick = now + BlockTimerTicks(world, key, now);
            else if (now >= s.BlockTimerTick)
            {
                s.SlotsCleared = now;
                s.BlockTimerTick = now + BlockTimerTicks(world, key, now);
            }
        }
        else s.BlockTimerTick = 0;

        return fighting;
    }

    /// <summary>
    /// The swing in progress: given up to defend against a blow past its half while it is still in its first half (true: aborted), else its next
    /// blow strikes when due.
    /// </summary>
    bool SwingOn(World world, int i, ref CombatSlot s, CharacterCold cold, int now, float incoming, bool fighting, EffectBuffer effects)
    {
        bool aborted = false;
        if (s.AttackTech >= 0)
        {
            var tech = _techniques[s.AttackTech];

            // Given up to defend: still in the first half of the swing while a blow at it is past its half (docs/game/combat.md "Pacing").
            // It goes straight to the defence; one that may not defend yet (after a failed roll) keeps swinging.
            if (incoming >= CombatTuning.AbortIncomingProgress && SwingProgress(in s, now) < CombatTuning.AbortOwnProgress
                && now >= s.DefendReadyTick)
            {
                s.AttackTech = -1;
                s.NextBlow = 0;
                aborted = true;
            }
            else if (s.NextBlow > 0 && fighting && now >= s.AttackStart + (int)MathF.Ceiling(tech.StrikeProgress(s.NextBlow) * s.AttackTicks))
            {
                Strike(world, i, s, cold, tech, s.NextBlow, effects);
                s.Thrown++;
                int blow = s.NextBlow;
                s.NextBlow = s.NextBlow < tech.Blows ? s.NextBlow + 1 : 0;

                // Someone attacking it: the swing ends at this blow's stop frame and it decides again (FUN_14060a590).
                if (incoming >= 0) Stop(ref s, tech, blow);
            }
        }

        return aborted;
    }

    /// <summary>
    /// A swing past its end: the fighter waits out the pause its outcome sets.
    /// </summary>
    void SwingEnd(ref CombatSlot s, int now, float dt)
    {
        if (s.AttackTech >= 0 && now >= s.AttackEnd)
        {
            // A swing that ran to its end waits in state 6 (FUN_14060d6a0): no deciding, so no attack and no defence, until the pause is out.
            float finishProgress = Math.Max(_techniques[s.AttackTech].EndProgress, CombatTuning.SwingFinished);
            bool ran = s.AttackEnd >= s.AttackStart + Math.Max(1, (int)MathF.Ceiling(finishProgress * s.AttackTicks));
            s.AttackTech = -1;
            s.NextBlow = 0;
            float pause = s.LastOutcome switch
            {
                BlowOutcome.Blocked => CombatTuning.BlockedPause,
                BlowOutcome.Dodged => CombatTuning.DodgedPause,
                BlowOutcome.Missed => CombatTuning.MissedPause,
                _ => CombatTuning.HitPause,
            };
            s.ReadyTick = now + Math.Max(1, (int)MathF.Ceiling(pause / dt));
            if (ran) s.PauseUntil = s.ReadyTick;
        }
    }

    /// <summary>
    /// Step 5: deciding tries attacking first; only when that fails does the fighter defend, or a defence in progress follows the next blow.
    /// </summary>
    void Decide(World world, int i, ref CombatSlot s, CharacterCold cold, ulong key, int now, float dt, EffectBuffer effects, bool fighting,
            bool aborted, float incoming)
    {
        // 5. Deciding tries attacking first (asking the target, StartAttack); only when that fails does a fighter defend against the blows
        // coming at it, from anyone (docs/game/combat.md "Pacing"). Being attacked cuts the pause after an attack short; an opponent that lost
        // the initiative to it may be swung at at once.
        if (s.ReactTech < 0 && s.AttackTech < 0)
        {
            // Deciding, it first asks to be one of the fighters its target lets close in (FUN_140666e20); a fighter left out circles
            // (PursuitSystem) and asks again each tick.
            if (fighting && !s.Engaged)
            {
                s.Engaged = MayJoin(world, i, s.TargetSlot, now);
                if (s.Engaged) s.JoinedEpoch = _read[s.TargetSlot].ListEpoch;
            }
            bool yielded = fighting && _read[s.TargetSlot].YieldTo == i && now < _read[s.TargetSlot].YieldUntil;
            bool paused = now < s.PauseUntil;
            if (!aborted && fighting && !paused && (now >= s.ReadyTick || incoming >= 0 || yielded))
            {
                StartAttack(world, i, ref s, cold, key, now, dt, effects);

                // A failed ask: with a blow coming it defends (below), else an engaged fighter goes to state 5 (FUN_14060c8b0); an attack ends
                // it.
                s.CircleState = s.Engaged && s.AttackTech < 0 && incoming < 0;
            }
            if (!s.Engaged) s.CircleState = false;

            // Only a fighter defends: a target whose fight is off is simply hit (the ask answers "attack", docs/game/combat.md "Fight lifecycle
            // and orders").
            if (s.AttackTech < 0 && !paused && now >= s.DefendReadyTick && (fighting || cold.InCombat))
                React(world, i, ref s, cold, key, now, dt);
        }
        else if (s.ReactTech >= 0 && s.AttackTech < 0 && (fighting || cold.InCombat))
            // A defence in progress meets another blow: the defence update (FUN_140609c70, which does not read the +0x14c timer) follows the most
            // advanced incoming swing.
            Reconsider(world, i, ref s, cold, key, now, dt);
    }

    #endregion

    #region Attacks

    /// <summary>
    /// Starts an attack when the fighter may (in reach, engaged, drawn, no Block order, room round it): the technique the chooser picks for the gap
    /// to where its target will be.
    /// </summary>
    void StartAttack(World world, int i, ref CombatSlot s, CharacterCold cold, ulong key, int now, float dt, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        int t = s.TargetSlot;
        var fighter = cold.Fighter!;
        if (!fighter.CanAttack || Orders.Has(cold, CharacterOrders.Block)) return;   // the Block order: defends, starts no attack
        // A fighter draws before it swings (the plan's Equipping weapon step precedes Attacking, hasWeaponEquipped, docs/game/ai-tasks.md): never a
        // swing with the weapon still sheathed.
        if (!s.Ranged && cold.DrawnWeapon != MeleeDrawn(fighter)) cold.DrawnWeapon = MeleeDrawn(fighter);
        if (!s.Engaged) return;   // left out by its target: it circles (PursuitSystem)
        // No one but enemies within its body room (x1.2 in state 5): the ask fails (FUN_14060c670, FUN_1402b3180).
        if (NearestFriend(world, i, in s, cold) < BodyRoom(cold) * (s.CircleState ? CircleRoomFactor : 1)) return;
        // no swing through a floor: it walks round by the stairs (PursuitSystem)
        if (OtherFloor(world, i, prev[i].Position, t, prev[t].Position)) return;
        // nor through bars or a wall: it is on a path chase round them (PursuitSystem)
        if (PathChasing(cold, t)) return;

        var tc = table.Cold(t)!;
        var medical = cold.Medical!;
        float length = fighter.WeaponLength;

        // The gap to where the target will be (a target that is not fighting is led by LeadSeconds, FUN_1402b4570).
        var aim = prev[t].Position + (_inFight[t] ? Vector3.Zero : prev[t].Velocity * CombatTuning.AimLeadSeconds);
        float e = fighter.EncumbranceFactor;
        var query = new AttackQuery
        {
            Category = fighter.TechniqueCategory,
            Skill = cold.Stats![fighter.WeaponSkill],
            // the chooser asks FUN_140884e00 with the injury term on (FUN_140884580 subtracts the dust penalty)
            UnarmedSkill = WeaponStats.Lerp(e, 0.2f, 1)
                * (cold.Stats[StatsEnumerated.MartialArts] + fighter.UnarmedBonus - medical.DustAttackPenalty),
            Animal = fighter.IsAnimal,
            Prone = medical.Prone,
            Encumbrance = e,
            WeaponLength = length,
            Scale = medical.ScaleK,
            Gap = CombatTuning.Gap(prev[i].Position, aim, cold, tc),
            Previous = s.LastTech,
            TargetStaggered = now < _read[t].StunUntil,
            LeftArm = medical.ArmWorks(left: true),
            RightArm = medical.ArmWorks(left: false),
            LegDown = LegDown(medical),
        };
        float roll = CombatRolls.Float(world.Seed, key, CombatRoll.Technique, (ulong)(s.AttackSeq + 1));
        int pick = TechniqueChooser.ChooseAttack(_techniques, query, roll, out s.Reach);
        if (pick < 0) return;

        // In reach with a technique: ask the target (and hold an attack slot).
        if (!HasSlot(world, i, t, tc, now) || !HasInitiative(world, i, ref s, t, cold, tc, now, effects)) return;
        var tech = _techniques[pick];
        s.AttackSeq++;
        s.AttackTech = pick;
        s.LastTech = pick;
        s.AttackStart = now;
        s.AttackTicks = Ticks(tech, PlayRate(world, i, cold, tech, attack: true), dt, cold);
        s.AttackEnd = now + Math.Max(1, (int)MathF.Ceiling(Math.Max(tech.EndProgress, CombatTuning.SwingFinished) * s.AttackTicks));
        s.NextBlow = 1;
        s.LastOutcome = BlowOutcome.Missed;
        s.AttackFrom = prev[i].Position;
        RecordZone(world, i, in s, cold, tech, 1);   // the attack start builds the zone list (FUN_1402b4570 -> FUN_1406087a0)
        // The bonus with the weapon's indoors modifier inside, less the dust's "Weather penalty" (FUN_140643a30, docs/formats/weather.md "Effects on
        // characters"); unarmed, the fist rating.
        s.AttackSkill = AttackRating(world, i, cold, fighter);
    }

    /// <summary>
    /// Whether <paramref name="i"/> may swing at <paramref name="t"/> now, asking the target first (<b>Verified</b>, FUN_14060c670 / FUN_140666a30,
    /// docs/game/combat.md "Pacing"): yes when the target lost the initiative to it; not while the target is swinging at it (it defends instead); yes
    /// while the target is staggered or not fighting it; otherwise an initiative roll on both attack skills (the skill roll with <c>attack chance
    /// factor</c>, 50/50 at equal skill), drawn from the pair and tick so both sides agree. The loser yields: the winner may swing at it at once (the
    /// original sets the winner to attack).
    /// </summary>
    bool HasInitiative(World world, int i, ref CombatSlot s, int t, CharacterCold cold, CharacterCold tc, int now, EffectBuffer effects)
    {
        ref readonly var target = ref _read[t];
        var prev = world.Characters.Previous;
        bool atMe = target.TargetSlot == i && target.TargetGeneration == prev[i].Generation;
        if (target.YieldTo == i && now < target.YieldUntil) return true;
        // its swing at me is on the way to its blow (progress strictly between 0 and 1, FUN_140665420): I defend (FUN_140666a30)
        if (atMe && SwingProgress(in target, now) is > 0 and < 1) return false;
        if (MassCombatAnswer(world, t, in target, atMe, now)) effects.Emit(i, t, MassCombatEffect);
        if (Open(t, tc, now)) return true;
        if (s.YieldTo == t && now < s.YieldUntil) return false;

        // The target is free (deciding or in its pause): one draw for the pair and tick, the lower slot's view.
        int lo = Math.Min(i, t), hi = Math.Max(i, t);
        float skillI = Eff(cold, cold.Fighter!.Unarmed ? StatsEnumerated.MartialArts : StatsEnumerated.MeleeAttack);
        float skillT = Eff(tc, tc.Fighter!.Unarmed ? StatsEnumerated.MartialArts : StatsEnumerated.MeleeAttack);
        float lowSkill = lo == i ? skillI : skillT, highSkill = lo == i ? skillT : skillI;
        float a = 1 + _combat.AttackChanceFactor * MathF.Max(0, highSkill - lowSkill);
        float b = 1 + _combat.AttackChanceFactor * MathF.Max(0, lowSkill - highSkill);
        float k = b / a, lowWins = k >= 1 ? 1 - 1 / (2 * k) : k / 2;
        ulong pair = ((ulong)(uint)lo << 32) | (uint)hi;
        float roll = CombatRolls.Float(world.Seed, pair, CombatRoll.Initiative, (ulong)(uint)now);
        if ((roll < lowWins) == (i == lo)) return true;

        // A winner still blocking or dodging can turn on it only when that ends, so the yield runs from there (engine choice).
        int from = target.ReactTech >= 0 ? Math.Max(now, target.ReactStart + target.ReactTicks) : now;
        s.YieldTo = t;
        s.YieldUntil = from + Math.Max(1, (int)MathF.Ceiling(CombatTuning.YieldSeconds / world.TickSeconds));
        return false;
    }

    #endregion

    #region Mass combat and incoming blows

    /// <summary>
    /// The <see cref="Effect.Kind"/> of an ask whose answer adds mass combat experience to the asked character (<c>FUN_1408c5e80</c>, <c>Target</c>).
    /// </summary>
    public const int MassCombatEffect = 5;

    /// <summary>
    /// Whether the answer to an ask (<c>FUN_140666a30</c>, <b>Verified</b>) gives the asked character mass combat XP: it is stumbling, or it fights
    /// someone other than the asker with its dead-time flag clear (no block or dodge playing) and is defending (blows coming at it; engine reading of
    /// state 1) or attacking. A target without a target gives none.
    /// </summary>
    bool MassCombatAnswer(World world, int t, in CombatSlot target, bool atMe, int now)
    {
        if (now < target.StunUntil) return true;
        if (target.TargetSlot < 0 || atMe || target.Down || target.ReactTech >= 0) return false;
        return target.AttackTech >= 0 || Incoming(world, t, now, out _) > 0;
    }

    /// <summary>
    /// Whether <paramref name="t"/> would not swing back (staggered, down, not fighting, the Block order; the ask has no Passive case), so it cannot
    /// take the initiative. A fighter that is blocking or dodging is not open: once its defence has started (+0x144) the original's answer rolls the
    /// initiative (FUN_140666a30).
    /// </summary>
    bool Open(int t, CharacterCold tc, int now)
    {
        ref readonly var target = ref _read[t];
        return now < target.StunUntil || target.Down || target.TargetSlot < 0 || tc.Fighter is not { CanAttack: true }
            || Orders.Has(tc, CharacterOrders.Block);
    }

    /// <summary>
    /// The stumble break-out roll (<b>Verified</b>, <c>FUN_1406085e0</c> / <c>FUN_140885d80</c>): of the fighters swinging at it with the swing
    /// between 10 and 99 per cent, the one with the highest attack skill; a draw under the defender's unarmed dodge chance against that skill (0 to
    /// 95) breaks out. False with no such swing.
    /// </summary>
    bool BreaksOut(World world, int i, CharacterCold cold, ulong key, int now, out bool rolled)
    {
        rolled = false;
        var prev = world.Characters.Previous;
        var range = world.Grid.Range(prev[i].Position.X, prev[i].Position.Z, CombatTuning.ReactionScanRadius);
        int generation = prev[i].Generation;
        float best = float.NegativeInfinity;
        foreach (var (_, x, z, other) in _index.AttackersOf(i))
        {
            if (other == i || !range.Holds((x, z))) continue;
            ref readonly var a = ref _read[other];
            if (a.AttackTech < 0 || a.TargetGeneration != generation || a.AttackTicks <= 0) continue;
            float progress = SwingProgress(in a, now);
            if (progress >= CombatTuning.BreakOutFrom && progress < CombatTuning.BreakOutTo) best = MathF.Max(best, a.AttackSkill);
        }
        if (float.IsNegativeInfinity(best)) return false;
        rolled = true;
        var fighter = cold.Fighter!;
        float effective = DefenceFormulas.EffectiveDefence(Eff(cold, StatsEnumerated.MartialArts), fighter.EncumbranceFactor,
            Speed.LegFactor(cold.Medical!), fighter.DefenceBonus, Guarding(cold));
        float chance = DefenceFormulas.BlockChance(effective, best, _combat, 0, 95);
        return CombatRolls.Float(world.Seed, key, CombatRoll.Block, (ulong)(uint)now ^ 0x42524B4FUL) * 100 < chance;
    }

    /// <summary>
    /// How far through the most advanced attack aimed at <paramref name="i"/> is (0 to 1, from the last tick's state; -1 when nobody is swinging at
    /// it), and the lowest slot that lost the initiative roll to it and still yields (<paramref name="yielder"/>, -1: none).
    /// </summary>
    float Incoming(World world, int i, int now, out int yielder)
    {
        var prev = world.Characters.Previous;

        // Its neighbours within ReactionScanRadius (the grid's cells round it) that yield to it or swing at it: the index's lists, kept to those
        // cells.
        var range = world.Grid.Range(prev[i].Position.X, prev[i].Position.Z, CombatTuning.ReactionScanRadius);
        int generation = prev[i].Generation;
        yielder = -1;
        foreach (var (_, other) in _index.YieldingTo(i))
        {
            if (other == i || !range.Holds(_index.CellOf(other))) continue;
            ref readonly var a = ref _read[other];
            if (now < a.YieldUntil && !a.Down && (yielder < 0 || other < yielder)) yielder = other;
        }
        float most = -1;
        foreach (var (_, x, z, other) in _index.AttackersOf(i))
        {
            if (other == i || !range.Holds((x, z))) continue;
            ref readonly var a = ref _read[other];
            if (a.AttackTech < 0 || a.NextBlow == 0 || a.TargetGeneration != generation || a.AttackTicks <= 0) continue;
            float v = SwingProgress(in a, now);
            if (v > 0 && v < 1) most = MathF.Max(most, v);
        }
        return most;
    }

    #endregion

    #region Swing progress and aim

    /// <summary>
    /// Whether a swing at a target that went down, died or was caged or carried plays on before the target is dropped (<b>Verified</b>, the fight
    /// tick <c>FUN_14060d6a0</c>; docs/game/combat.md "Pacing"): the drop waits while the clip's own progress is under the technique's <c>acceptable
    /// end time</c>, so a kill is followed by the rest of the clip before the next choice. A swing ending now (a stop frame) or a target whose slot
    /// was reused drops at once.
    /// </summary>
    bool FinishingSwing(in CombatSlot s, ReadOnlySpan<CharacterHot> prev, int now)
    {
        if (s.AttackTech < 0 || s.AttackTicks <= 0 || now >= s.AttackEnd) return false;
        if ((uint)s.TargetSlot >= (uint)prev.Length || prev[s.TargetSlot].Generation != s.TargetGeneration) return false;

        float clip = (now - s.AttackStart) / (float)s.AttackTicks;
        return clip < _techniques[s.AttackTech].EndProgress;
    }

    /// <summary>
    /// A swing's progress as the original keeps it (CombatClass +0x140, <c>FUN_140665cf0</c>, <b>Verified</b>): the clip's progress divided by the
    /// next blow's <c>anim blocked frame</c> fraction, so it reaches 1 at the blow's impact (not at the clip's end); 1 once every blow has passed; 0
    /// when not swinging. Defenders read it from the swinger's last tick: 0.1 to 0.99 is the break-out window, a blow at 0.5 or more cuts a swing
    /// short that is itself under 0.5.
    /// </summary>
    float SwingProgress(in CombatSlot a, int now)
    {
        if (a.AttackTech < 0 || a.AttackTicks <= 0) return 0;
        if (a.NextBlow == 0) return 1;
        float clip = Math.Clamp((now - a.AttackStart) / (float)a.AttackTicks, 0, 1);
        float strike = _techniques[a.AttackTech].StrikeProgress(a.NextBlow);
        return strike > 0 ? clip / strike : 1;
    }

    /// <summary>
    /// The way a swinging fighter turns to its target (<b>Verified</b>, the swing update <c>FUN_14060a590</c>, the attack start <c>FUN_1402b4570</c>
    /// -> <c>FUN_140666400</c>, the tracking window <c>FUN_1406084e0</c>; docs/game/combat.md "Facing and movement in a swing"): a swing has no
    /// steering (the movement is animation driven), so its heading is only set by these calls, at once: at the attack start towards the target's
    /// place when it is over 5 away, and each tick while the first blow's progress is under X and the aim point (the target's place, led by 1.2 s
    /// while it is not fighting) is over 8 away. X = max(0.6 (20 - D) / 20, max(0.1, (4 - n) / 3)), D = the target's defence rating less the
    /// swinger's attack rating, n the fighters at the target (its engaged count and overflow); alone against one, X is 1 and the heading follows the
    /// target through the whole windup.
    /// </summary>
    public bool SwingAim(World world, int i, out Vector2 dir)
    {
        dir = default;
        var table = world.Characters;
        var prev = table.Previous;
        if ((uint)i >= (uint)prev.Length) return false;
        ref readonly var s = ref _read[i];
        int t = s.TargetSlot, now = (int)world.Tick;
        if (s.AttackTech < 0 || s.NextBlow != 1 || s.Ranged || (uint)t >= (uint)prev.Length || !prev[t].Alive
            || s.TargetGeneration != prev[t].Generation)
            return false;
        if (table.Cold(t) is not { Fighter: { } target, Stats: not null, Medical: not null } tc) return false;
        var to = prev[t].Position - prev[i].Position;
        bool start = now <= s.AttackStart + 1 && to.X * to.X + to.Z * to.Z > 25;   // the attack start's own turn, at the target's place
        if (!start)
        {
            var aim = prev[t].Position + (tc.InCombat ? Vector3.Zero : prev[t].Velocity * CombatTuning.AimLeadSeconds);
            to = aim - prev[i].Position;
            if (!(to.Length() > 8)) return false;
            float defence = DefenceRating(world, t, tc, target);
            float window = MathF.Max(0.6f * (20 - (defence - s.AttackSkill)) / 20, MathF.Max(0.1f, (4 - FightersAt(world, t)) / 3f));
            if (!(SwingProgress(in s, now) < window)) return false;
        }
        dir = new Vector2(to.X, to.Z);
        float len = dir.Length();
        if (len < 1e-4f) return false;
        dir /= len;
        return true;
    }

    /// <summary>
    /// The defence update's turn (<c>FUN_140666400</c>, called every tick from <c>FUN_140609c70</c>): a defending fighter faces the attacker its
    /// defence follows at once when that one is over 5 away, except while it plays a block of a rear direction (7, 8 or 9), which is met turned away.
    /// A steering defender faces it through its band (PursuitSystem.BandTarget).
    /// </summary>
    public bool DefenceAim(World world, int i, out Vector2 dir)
    {
        dir = default;
        var prev = world.Characters.Previous;
        if ((uint)i >= (uint)prev.Length) return false;
        ref readonly var s = ref _read[i];
        int a = s.ReactedSlot;
        if (s.ReactTech < 0 || (uint)a >= (uint)prev.Length || a == i || !prev[a].Alive) return false;
        if (!DefenceFaces(s.ReactTech)) return false;

        var to = prev[a].Position - prev[i].Position;
        if (!(to.LengthSquared() > 25)) return false;
        dir = new Vector2(to.X, to.Z);
        float len = dir.Length();
        if (len < 1e-4f) return false;
        dir /= len;
        return true;
    }

    /// <summary>
    /// Whether a defence with technique <paramref name="tech"/> turns to the swing it follows: not a block of a rear direction (7, 8 or 9,
    /// <c>FUN_140666400</c>).
    /// </summary>
    internal bool DefenceFaces(int tech) => !(_techniques[tech].IsBlock && _techniques[tech].AttackDirection1 is 7 or 8 or 9);

    /// <summary>
    /// Any leg part is down (flesh - stun + rig &lt; 0): the original's two leg parts at medical +0x4d8 / +0x4e0 in the kick factor
    /// (<c>FUN_140886030</c>).
    /// </summary>
    static bool LegDown(MedicalState medical)
    {
        foreach (var p in medical.Parts) if (p.IsLeg && p.IsDown) return true;
        return false;
    }

    #endregion

    #region Attack slots and engagement

    /// <summary>
    /// Whether <paramref name="i"/> gets one of its target's attack slots for this swing (docs/game/combat.md "Attack slots and circling",
    /// <c>FUN_140666310</c> / <c>FUN_1406661d0</c>): <see cref="AttackSlots"/> plus the target race's <c>extra attack slots</c> (1 to 5), only 1
    /// while the target swings itself or plays a hit reaction (<c>FUN_1406661d0</c>, <b>Verified</b>). A slot is held from the grant to the swing's
    /// last blow (<c>FUN_14060a590</c> gives it back), so the holders are the fighters at the target whose swing has a blow to come. The original
    /// grants in its update order; here the other fighters that may ask on the same tick and have a lower slot go first (engine choice). Their asks
    /// are foreseen from the last tick's state only, as Act gates them (engaged, deciding now, out of the pause or attacked, within their reach),
    /// erring towards counting one that then does not ask, so the count is never exceeded.
    /// </summary>
    bool HasSlot(World world, int i, int t, CharacterCold tc, int now)
    {
        int slots = _read[t].AttackTech >= 0 || now < _read[t].StunUntil
            ? 1 : Math.Clamp(AttackSlots + (tc.Race?.ExtraAttackSlots ?? 0), 1, MaxAttackSlots);
        var table = world.Characters;
        var prev = table.Previous;

        // The fighters at the target within EngageScanRadius of it (the index's list, kept to the grid's cells round it).
        var range = world.Grid.Range(prev[t].Position.X, prev[t].Position.Z, CombatTuning.EngageScanRadius);
        int generation = prev[t].Generation, holders = 0, ahead = 0;
        var at = prev[t].Position;
        bool open = Open(t, tc, now);
        foreach (var (_, x, z, other) in _index.AttackersOf(t))
        {
            if (other == i || !range.Holds((x, z))) continue;
            ref readonly var o = ref _read[other];
            if (o.TargetGeneration != generation || o.Down || o.Ranged) continue;

            // A swing holds its slot unless the target (a blocker) freed its slots after the swing started, or it has run past SlotHoldSeconds
            // (FUN_140668000).
            if (o.AttackTech >= 0 && o.NextBlow > 0 && o.AttackStart >= _read[t].SlotsCleared
                && now - o.AttackStart <= (int)MathF.Ceiling(SlotHoldSeconds / world.TickSeconds))
            {
                if (++holders + ahead >= slots) return false;
                continue;
            }
            if (other > i || now < o.StunUntil || now < o.PauseUntil && o.AttackTech < 0 && o.ReactTech < 0
                || !Listed(in o, in _read[t]) && !MayJoin(world, other, t, now))
                continue;

            // It decides this tick when its swing or reaction ends now (Act's steps 4 and 5): a block's end and a swing's end start a pause (cut
            // short by a blow at it).
            int ready = o.ReadyTick;
            if (o.AttackTech >= 0)
            {
                if (now < o.AttackEnd) continue;
                ready = int.MaxValue;
            }
            else if (o.ReactTech >= 0)
            {
                if (!ReactionEnds(world, other, in o, now, out bool wait)) continue;
                if (wait) ready = Math.Max(ready, now + (int)MathF.Ceiling(CombatTuning.AfterBlockPause / world.TickSeconds));
            }
            if (now < ready && !(_read[t].YieldTo == other && now < _read[t].YieldUntil) && Incoming(world, other, now, out _) < 0) continue;

            // A yielder asks in vain unless the target is open (HasInitiative).
            if (o.YieldTo == t && now < o.YieldUntil && !open) continue;

            // Its reach is known from its last ask (StartAttack); one that has never asked counts.
            var c = table.Cold(other);
            if (c is null || o.Reach > 0 && CombatTuning.Gap(prev[other].Position, at, c, tc) > o.Reach) continue;
            if (holders + ++ahead >= slots) return false;
        }
        return holders + ahead < slots;
    }

    /// <summary>
    /// The Block order's slot timer: uniform 1.5 to 4.5 s (<b>Verified</b> literals, FUN_1402b3180).
    /// </summary>
    static int BlockTimerTicks(World world, ulong key, int now) =>
        Math.Max(1, (int)MathF.Ceiling((1.5f + 3 * CombatRolls.Float(world.Seed, key, CombatRoll.BlockTimer, (ulong)(uint)now)) / world.TickSeconds));

    /// <summary>
    /// A slot holder is dropped once its slot timer passes this (AttackSlotManager::periodicUpdate, <c>DAT_141683188</c>, Verified): even a swing
    /// still on.
    /// </summary>
    public const float SlotHoldSeconds = 20;

    /// <summary>
    /// The most attack slots a target can have (the slot count is clamped to 1 to 5, <c>FUN_1406661d0</c>).
    /// </summary>
    public const int MaxAttackSlots = 5;

    /// <summary>
    /// The most fighters a target lets close in at once (<c>FUN_140666e20</c> adds an asker while it has at most four).
    /// </summary>
    public const int MaxEngaged = 5;

    /// <summary>
    /// Whether fighter <paramref name="o"/> is in the engaged list of <paramref name="target"/>, its target: it joined and the target's fight has not
    /// ended since (<see cref="CombatSlot.ListEpoch"/>).
    /// </summary>
    internal static bool Listed(in CombatSlot o, in CombatSlot target) => o.Engaged && o.JoinedEpoch == target.ListEpoch;

    /// <summary>
    /// Whether <paramref name="i"/> may join the fighters its target lets close in (<see cref="MaxEngaged"/>, <c>FUN_140666e20</c>): it joins while
    /// fewer are in. The ones that are not in yet ask whenever they decide; those with a lower slot ask first (engine choice for the original's
    /// update order), counted only when they may decide this tick (not staggered, blocking or dodging, or swinging past the point where it could
    /// still give the swing up), so a fighter is not left out for one that does not ask.
    /// </summary>
    bool MayJoin(World world, int i, int t, int now)
    {
        var prev = world.Characters.Previous;
        var range = world.Grid.Range(prev[t].Position.X, prev[t].Position.Z, CombatTuning.EngageScanRadius);
        int generation = prev[t].Generation, count = 0;
        foreach (var (_, x, z, other) in _index.AttackersOf(t))
        {
            if (other == i || !range.Holds((x, z))) continue;
            ref readonly var o = ref _read[other];
            if (o.TargetGeneration != generation || o.Down || o.Ranged) continue;
            if (!Listed(in o, in _read[t]) && (other > i || !MayDecide(in o, now))) continue;
            if (++count >= MaxEngaged) return false;
        }
        return count < MaxEngaged;
    }

    /// <summary>
    /// The room a fighter needs round it to ask: its race's <c>pathfind footprint radius</c> + 2.5 (CombatClass +0x280, <b>Observed</b>, constructor
    /// <c>FUN_14060a9e0</c>).
    /// </summary>
    internal static float BodyRoom(CharacterCold cold) => CombatTuning.Footprint(cold) + 2.5f;

    /// <summary>
    /// State 5's stricter room (<c>CircleStateAI</c> <c>FUN_1402b3180</c>).
    /// </summary>
    public const float CircleRoomFactor = 1.2f;

    /// <summary>
    /// How far above or below a body still counts (the flocking list's height test, <c>FUN_14085a070</c>).
    /// </summary>
    const float FlockHeight = 20;

    /// <summary>
    /// The state of <see cref="NearestFriend"/>'s grid query.
    /// </summary>
    struct FriendScan
    {
        /// <summary>
        /// The combat system, for the enemy test.
        /// </summary>
        public CombatSystem Self;
        /// <summary>
        /// The world the bodies are read from.
        /// </summary>
        public World World;
        /// <summary>
        /// The fighter's slot, its faction and its target's slot.
        /// </summary>
        public int Me, Faction, Target;
        /// <summary>
        /// The fighter's position.
        /// </summary>
        public Vector3 At;
        /// <summary>
        /// The centre distance to the nearest body that is not an enemy so far (99: none).
        /// </summary>
        public float Nearest;
    }

    /// <summary>
    /// The centre distance to the nearest body round fighter <paramref name="i"/> that is not an enemy
    /// (<c>FlockingTools::getDistanceToClosestCharacter</c> over the flocking list, <b>Observed</b>, docs/game/combat.md "Attack slots and
    /// circling"): standing characters within <see cref="FlockHeight"/> in height, an enemy being one of a hostile faction, its target, or one
    /// fighting it (engine reading of <c>isEnemy</c>); 99 when none is near. A carried body is not counted (engine choice). Any thread after
    /// Schedule.
    /// </summary>
    float NearestFriend(World world, int i, in CombatSlot s, CharacterCold cold)
    {
        var prev = world.Characters.Previous;
        var scan = new FriendScan
        {
            Self = this,
            World = world,
            Me = i,
            Faction = cold.Faction,
            Target = s.TargetSlot,
            At = prev[i].Position,
            Nearest = 99,
        };
        float radius = BodyRoom(cold) * CircleRoomFactor;
        world.Grid.Query(scan.At.X, scan.At.Z, radius, ref scan, static (int c, ref FriendScan q) =>
        {
            if (c == q.Me || c == q.Target) return;
            var table = q.World.Characters;
            ref readonly var p = ref table.Previous[c];
            if (!p.Alive || MathF.Abs(p.Position.Y - q.At.Y) >= FlockHeight) return;
            if (table.Cold(c) is not { } oc || CombatSteering.OutOfFlock(p, oc)) return;
            ref readonly var o = ref q.Self._read[c];
            if (o.TargetSlot == q.Me && o.TargetGeneration == table.Previous[q.Me].Generation) return;
            bool enemy = q.Self.Relations is { } relations ? AggroSystem.IsEnemy(relations, q.Faction, oc.Faction) : oc.Faction != q.Faction;
            if (enemy) return;
            float d = Vector3.Distance(p.Position, q.At);
            if (d < q.Nearest) q.Nearest = d;
        });
        return scan.Nearest;
    }

    /// <summary>
    /// Whether a fighter whose last tick's state is <paramref name="o"/> may reach Act's deciding step this tick (its swing or defence ends now, or a
    /// blow at it may cut a swing short).
    /// </summary>
    bool MayDecide(in CombatSlot o, int now) =>
        now >= o.StunUntil && (o.ReactTech < 0 || now >= o.ReactStart + o.ReactTicks)
        && (o.AttackTech < 0 || now >= o.AttackEnd || SwingProgress(in o, now) < CombatTuning.AbortOwnProgress);

    #endregion

    #region Attacker index

    /// <summary>
    /// Who is after whom as of the last tick (<see cref="IndexAttackers"/>).
    /// </summary>
    readonly AttackerIndex _index = new();
    /// <summary>
    /// <see cref="FightersAt"/>'s count per target, made by <see cref="IndexAttackers"/>.
    /// </summary>
    int[] _fightersAt = [];

    /// <summary>
    /// Lists who is after whom from the combat state as it stands (<see cref="AttackerIndex"/>), for <see cref="FightersAt"/> and the act's checks;
    /// serial phases only. Combat's schedule builds it for the act; a system asking <see cref="FightersAt"/> in its own schedule before combat's
    /// builds it first.
    /// </summary>
    public void IndexAttackers(World world)
    {
        _index.Build(_read, world.Characters, world.Grid);

        // FightersAt's counts, once per target: asked per attacker, a crowd round one target would walk its list once per member.
        int n = world.Characters.HighWater;
        if (_fightersAt.Length < n) _fightersAt = new int[Math.Max(n, _fightersAt.Length * 2)];
        for (int t = 0; t < n; t++) _fightersAt[t] = _index.AttackersOf(t).IsEmpty ? 0 : CountFightersAt(world, t);
    }

    /// <summary>
    /// The melee fighters (engaged or circling) at <paramref name="t"/>, as of the last tick: the original's engaged count plus the askers it turned
    /// away (<c>FUN_140666e20</c>). After <see cref="IndexAttackers"/>.
    /// </summary>
    public int FightersAt(World world, int t) => (uint)t < (uint)world.Characters.HighWater && t < _fightersAt.Length ? _fightersAt[t] : 0;

    /// <summary>
    /// The melee fighters at <paramref name="t"/>: its attackers within the engage scan's cells that still target it, are not down and do not shoot.
    /// </summary>
    int CountFightersAt(World world, int t)
    {
        var prev = world.Characters.Previous;
        var range = world.Grid.Range(prev[t].Position.X, prev[t].Position.Z, CombatTuning.EngageScanRadius);
        int generation = prev[t].Generation, count = 0;
        foreach (var (_, x, z, other) in _index.AttackersOf(t))
        {
            if (!range.Holds((x, z))) continue;
            ref readonly var o = ref _read[other];
            if (o.TargetGeneration == generation && !o.Down && !o.Ranged) count++;
        }
        return count;
    }

    /// <summary>
    /// Ends the reaction in progress when <see cref="ReactionEnds"/> says so; a wait that follows holds off the next attack (the +0x148 wait,
    /// AfterBlockPause).
    /// </summary>
    void EndReaction(World world, int i, ref CombatSlot s, int now, float dt)
    {
        if (!ReactionEnds(world, i, in s, now, out bool wait)) return;
        if (wait) s.ReadyTick = Math.Max(s.ReadyTick, now + (int)MathF.Ceiling(CombatTuning.AfterBlockPause / dt));
        s.ReactTech = -1;
    }

    #endregion

    #region Reactions

    /// <summary>
    /// A swing at the fighter whose blow has not landed yet: the attacker, its attack number, the blow, its progress (<see cref="SwingProgress"/>, 1
    /// or more on the tick it lands) and that tick.
    /// </summary>
    readonly record struct IncomingBlow(int Attacker, int Seq, int Blow, float Progress, int Lands);

    /// <summary>
    /// The swing at <paramref name="i"/> that is furthest on (the original's followed attacker +0x1a8, <c>FUN_140665e00</c>: the largest progress),
    /// from those swinging at it within ReactionScanRadius whose blow has not landed. An exact tie keeps <paramref name="keep"/>, else the lower slot
    /// (the original's <c>&lt;=</c> keeps one stable; Meitou's grid order is not, so the slot breaks it). <paramref name="landing"/>: a blow landing
    /// now counts (the hold); the defence start and update pick only among 0 &lt; v &lt; 1. Attacker -1: nobody.
    /// </summary>
    IncomingBlow MostAdvanced(World world, int i, int now, int keep, bool landing)
    {
        var prev = world.Characters.Previous;
        var range = world.Grid.Range(prev[i].Position.X, prev[i].Position.Z, CombatTuning.ReactionScanRadius);
        int generation = prev[i].Generation;
        var best = new IncomingBlow(-1, 0, 0, 0, 0);
        foreach (var (_, x, z, other) in _index.AttackersOf(i))
        {
            if (other == i || !range.Holds((x, z))) continue;
            ref readonly var a = ref _read[other];
            if (a.AttackTech < 0 || a.NextBlow == 0 || a.TargetGeneration != generation || a.AttackTicks <= 0) continue;

            // A blow is on its way until the attacker's state shows it struck (a far attacker strikes at its own step, after the landing tick;
            // strikes apply after every Act).
            int lands = a.AttackStart + (int)MathF.Ceiling(_techniques[a.AttackTech].StrikeProgress(a.NextBlow) * a.AttackTicks);
            float v = SwingProgress(in a, now);
            if (v <= 0 || !landing && v >= 1) continue;
            if (best.Attacker >= 0 && !Ahead(v, other, in best, keep)) continue;
            best = new IncomingBlow(other, a.AttackSeq, a.NextBlow, v, lands);
        }
        return best;
    }

    /// <summary>
    /// Whether a blow at <paramref name="v"/> of its swing by <paramref name="other"/> comes before <paramref name="best"/>: further along, or as far
    /// and from the kept attacker or a lower slot.
    /// </summary>
    static bool Ahead(float v, int other, in IncomingBlow best, int keep) =>
        v > best.Progress || v == best.Progress && best.Attacker != keep && (other == keep || other < best.Attacker);

    /// <summary>
    /// Whether the reaction in progress ends this tick (the defence update <c>FUN_140609c70</c>, docs/game/combat.md "Pacing"), and whether the
    /// +0x148 wait follows: any reaction once no swing at the fighter is on its way (then the wait), a dodge also once its clip is past 0.98 (state
    /// 4, deciding at once). A block is not ended by its clip running out.
    /// </summary>
    bool ReactionEnds(World world, int i, in CombatSlot s, int now, out bool wait)
    {
        wait = false;
        if (s.ReactTech < 0) return false;
        if (MostAdvanced(world, i, now, s.ReactedSlot, landing: true).Attacker < 0)
        {
            wait = true;
            return true;
        }
        return _techniques[s.ReactTech].IsDodge && now - s.ReactStart > HitOutcomes.DodgeTo * s.ReactTicks;
    }

    /// <summary>
    /// Starts a defence against the swing furthest on (the original's <c>FUN_140609290</c>): only once that swing is 0.1 of the way to its blow and
    /// the fighter is in that attacker's attack zone list (<c>FUN_140608460</c>); else it tries again in 0.1 s. A blow decided again rolls afresh.
    /// </summary>
    /// <param name="stumbling">A break-out from a stumble that won its roll: the reaction is one of the <c>is stumble dodge</c> rows, without a
    /// block or dodge roll.</param>
    void React(World world, int i, ref CombatSlot s, CharacterCold cold, ulong key, int now, float dt, bool stumbling = false)
    {
        var swing = MostAdvanced(world, i, now, s.ReactedSlot, landing: false);
        if (swing.Attacker < 0) return;
        if (!Startable(in swing, i))
        {
            s.DefendReadyTick = now + (int)MathF.Ceiling(CombatTuning.EarlyDefenceSeconds / dt);
            return;
        }
        if (!stumbling && cold.Fighter!.IsAnimal)
        {
            // An animal never defends: the start takes no technique and holds off the next try (FUN_140609290, the defender's asAnimal vfunc 0x248).
            s.ReactedSlot = swing.Attacker;
            s.DefendReadyTick = now + (int)MathF.Ceiling(CombatTuning.AnimalNoDefenceSeconds / dt);
            return;
        }
        StartDefence(world, i, ref s, cold, key, now, dt, in swing, stumbling, reconsider: false);
    }

    /// <summary>
    /// The defence update (<c>FUN_140609c70</c>): a defence follows the swing furthest on (a dodge too, so it turns to it); for a block, when that
    /// swing or its blow changes, or every 1 s, it starts a new defence if the blow's side (worked out now, so an attacker that walked round counts)
    /// differs from the block's. A block of the same side carries on; a dodge is never re-picked.
    /// </summary>
    void Reconsider(World world, int i, ref CombatSlot s, CharacterCold cold, ulong key, int now, float dt)
    {
        var swing = MostAdvanced(world, i, now, s.ReactedSlot, landing: false);
        if (swing.Attacker < 0) return;
        bool changed = swing.Attacker != s.ReactedSlot || swing.Seq != s.ReactedSeq || swing.Blow != s.ReactedBlow;
        s.ReactedSlot = swing.Attacker;
        s.ReactedSeq = swing.Seq;
        s.ReactedBlow = swing.Blow;
        if (_techniques[s.ReactTech].IsDodge || !changed && now < s.ReactCheckTick) return;

        s.ReactCheckTick = now + (int)MathF.Ceiling(CombatTuning.DefenceRecheckSeconds / dt);
        if (_techniques[s.ReactTech].AttackDirection1 == BlowSide(world, i, in swing)) return;
        if (!Startable(in swing, i))
        {
            // A new start that fails (too early, or not in the attacker's zone) ends the defence (state 4).
            s.ReactTech = -1;
            s.DefendReadyTick = now + (int)MathF.Ceiling(CombatTuning.EarlyDefenceSeconds / dt);
            return;
        }
        StartDefence(world, i, ref s, cold, key, now, dt, in swing, stumbling: false, reconsider: true);
    }

    /// <summary>
    /// The defence start's tests on the swing it picked (<c>FUN_140609290</c>): the fighter is in the attacker's zone list and the swing is 0.1 of
    /// the way to its blow.
    /// </summary>
    bool Startable(in IncomingBlow swing, int i) => _zones.Contains(swing.Attacker, i) && swing.Progress >= CombatTuning.DefenceStartProgress;

    /// <summary>
    /// The side the blow comes from as the fighter stands now (<c>FUN_140436520</c>): the swing's own direction, or its rear counterpart when the
    /// attacker is behind.
    /// </summary>
    int BlowSide(World world, int i, in IncomingBlow swing)
    {
        var prev = world.Characters.Previous;
        var tech = _techniques[_read[swing.Attacker].AttackTech];
        return HitOutcomes.Facing(tech.DirectionOf(swing.Blow), HitOutcomes.Behind(prev[swing.Attacker].Position, prev[i].Position, prev[i].Yaw));
    }

    #endregion

    #region Defence

    /// <summary>
    /// Starts the defence against <paramref name="swing"/>: the reaction roll, the technique for the blow's side (none tried again for a while after
    /// a failure), its clip at once.
    /// </summary>
    void StartDefence(World world, int i, ref CombatSlot s, CharacterCold cold, ulong key, int now, float dt, in IncomingBlow swing,
        bool stumbling, bool reconsider)
    {
        s.ReactedSlot = swing.Attacker;
        s.ReactedSeq = swing.Seq;
        s.ReactedBlow = swing.Blow;
        int pick = ChooseDefence(world, i, cold, key, now, in swing, stumbling);
        if (pick < 0)
        {
            // No defence (a failed roll or no row for the side, alike: FUN_140887970 returns none for both): none is tried again for a while (the
            // original's +0x14c).
            s.DefendReadyTick = now + (int)MathF.Ceiling(CombatTuning.NoDefenceSeconds / dt);
            if (reconsider) s.ReactTech = -1;   // the failed new start ends the defence (state 4, FUN_140609c70)
            return;
        }

        s.ReactCheckTick = now + (int)MathF.Ceiling(CombatTuning.DefenceRecheckSeconds / dt);
        s.DefendReadyTick = Math.Max(s.DefendReadyTick, now + (int)MathF.Ceiling(CombatTuning.DefenceStartedSeconds / dt));
        s.LastTech = -1;   // the original's current technique (+0x150) is now a defence, which the next attack choice does not count as its last
        if (pick == s.ReactTech) return;   // the same technique again: its clip plays on (FUN_1405b7600 finds the playing layer)

        var reaction = _techniques[pick];
        int ticks = Ticks(reaction, PlayRate(world, i, cold, reaction, attack: false), dt, cold);

        // The clip starts at once. With LateDefences (Meitou balance choice, docs/simulation.md "Combat") it is never too little along for the roll
        // it passed to count: a late one joins its clip partway.
        int start = now;
        if (LateDefences)
        {
            float least = reaction.IsBlock ? HitOutcomes.BlockProgress : HitOutcomes.DodgeFrom;
            start = Math.Min(start, swing.Lands - (int)MathF.Ceiling(least * ticks) - 1);
        }
        s.ReactTech = pick;
        s.ReactStart = start;
        s.ReactTicks = ticks;
    }

    /// <summary>
    /// The block or dodge roll against the swing's attacker, and the technique it picks for the blow's side (-1: none); each decision draws afresh
    /// (the tick salts the blow's counter).
    /// </summary>
    int ChooseDefence(World world, int i, CharacterCold cold, ulong key, int now, in IncomingBlow swing, bool stumbling)
    {
        var table = world.Characters;
        ref readonly var attack = ref _read[swing.Attacker];
        var fighter = cold.Fighter!;
        float meleeDefence = Trained(cold, StatsEnumerated.MeleeDefence);
        ulong counter = CombatRolls.BlowCounter(swing.Attacker, attack.AttackSeq, swing.Blow, 0) ^ (ulong)(uint)now * 0x9E3779B97F4A7C15UL;
        float chanceRoll = CombatRolls.Float(world.Seed, key, CombatRoll.Block, counter);
        float pickRoll = CombatRolls.Float(world.Seed, key, CombatRoll.Reaction, counter);
        float block, dodge = 0;
        if (fighter.CanBlock)
        {
            float d = DefenceFormulas.Defence(meleeDefence, fighter.Weapon!.Stats.DefenceMod, true,
                fighter.DefenceBonus + IndoorsMod(world, i, fighter), Guarding(cold));
            block = DefenceFormulas.BlockChance(d, attack.AttackSkill, _combat);
            if (table.Cold(swing.Attacker)?.Fighter is { Unarmed: true, IsAnimal: false })
                block *= DefenceFormulas.FistBlockFactor(cold.Stats![StatsEnumerated.MartialArts]);
        }
        else
        {
            block = 0;
            float effective = DefenceFormulas.EffectiveDefence(Eff(cold, StatsEnumerated.MartialArts), fighter.EncumbranceFactor,
                Speed.LegFactor(cold.Medical!), fighter.DefenceBonus, Guarding(cold));
            dodge = DefenceFormulas.BlockChance(effective, attack.AttackSkill, _combat, 0, 95);
        }
        int direction = BlowSide(world, i, in swing);
        return TechniqueChooser.ChooseReaction(_techniques, fighter.TechniqueCategory, cold.Medical!.Prone, fighter.CanBlock, direction,
            block, dodge, chanceRoll, pickRoll, stumbling);
    }

    #endregion

}