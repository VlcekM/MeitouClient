using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>The combat XP events of <c>FUN_1408c6980</c> (docs/game/character-stats.md "Levelling"; the numbering is the original's).</summary>
public enum XpEvent
{
    /// <summary>0: a hit landed.</summary>
    HitDealt = 0,
    /// <summary>1: the same with smaller gains (apparently a blocked or glancing blow).</summary>
    GlancingHit = 1,
    /// <summary>2: the attack was defended.</summary>
    Defended = 2,
    /// <summary>4: a hit taken.</summary>
    HitTaken = 4,
    /// <summary>6: the attacker-side gains of event 2 without the defence gain (meaning <b>Unknown</b>).</summary>
    Event6 = 6,
}

/// <summary>
/// The XP sources: what raises which stat and by how much (docs/game/character-stats.md "Levelling (XP)"). Other systems call these with the
/// character's <see cref="CharacterStats"/> and race; each ends in <see cref="CharacterStats.Gain"/>. The service holds only constants and options, so one
/// instance serves every character on every thread.
/// <para>Time units: the continuous gains take <c>dtSeconds</c>, the game-speed-scaled frame time of game-loop.md (the original's third time
/// value); the medical code uses game hours. The per-event gains take no time.</para>
/// </summary>
public sealed class XpService(GameConstants constants, BodyOptions options)
{
    /// <summary>0.02, the per-second factor of the continuous gains (<b>Verified</b>: the float in the exe's <c>.data</c>).</summary>
    public const float ContinuousRate = 0.02f;

    /// <summary>Base of a per-event gain: <c>exp gain multiplier</c> (already x0.25, 0.75 in the base game) x 0.1 = 0.075.</summary>
    public float EventUnit => constants.ExpGainMultiplier * 0.1f;

    /// <summary>
    /// The skill-difference factor (<c>FUN_1408c5fa0</c>): 1 + (other - own) x 0.01 x (100 / K), K = <c>skill diff xp 2x bonus</c> (10) when the other is
    /// better, <c>skill diff xp 0x penalty</c> (25) when worse. So +10 gives 2 and -25 gives 0. The editor text names a 0.1 floor and a 6x cap that this
    /// function does not apply (callers may); a negative factor makes the amount negative, which the core curve ignores.
    /// </summary>
    public float SkillDifferenceFactor(float own, float other)
    {
        float diff = other - own;
        float k = diff >= 0 ? constants.SkillDiffXp2xBonus : constants.SkillDiffXp0xPenalty;
        return 1 + diff * 0.01f * (100 / k);
    }

    /// <summary>
    /// The generic continuous entry (<c>FUN_1408c6430</c>): the stat rises by the core gain of <c>0.02 x dt x exp gain multiplier x amount x race[stat]</c>.
    /// </summary>
    public float Continuous(CharacterStats stats, RaceData race, StatsEnumerated stat, float dtSeconds, float amount) =>
        stats.Gain(stat, ContinuousRate * dtSeconds * constants.ExpGainMultiplier * amount * race.StatMultiplier(stat));

    /// <summary>
    /// The continuous entry with a cap (<c>FUN_1408c64f0</c>, <b>Verified</b> by decompile; the training dummy's): nothing once the stat is at the cap, else the same gain with the core curve
    /// ((cap - stat) / cap)^2, so the stat only creeps towards the cap and never reaches it.
    /// </summary>
    public float Capped(CharacterStats stats, RaceData race, StatsEnumerated stat, float dtSeconds, float amount, float cap) =>
        stats[stat] < cap ? stats.Gain(stat, ContinuousRate * dtSeconds * constants.ExpGainMultiplier * amount * race.StatMultiplier(stat), cap) : stats[stat];

    /// <summary>
    /// A per-event gain: the core gain of <c>0.075 x factor x race[stat]</c> (<paramref name="factor"/> carries the event's coefficient and the
    /// skill-difference factor).
    /// </summary>
    public float PerEvent(CharacterStats stats, RaceData race, StatsEnumerated stat, float factor) =>
        stats.Gain(stat, EventUnit * factor * race.StatMultiplier(stat));

    /// <summary>
    /// The weapon-weight factor for strength XP (<c>FUN_1408838e0</c>): <c>min strength xp mult</c> + clamp((weapon weight - strength x injury multiplier) /
    /// <c>weight strength diff 1x</c>, 0, <c>weight strength diff max</c>). A heavy weapon trains strength faster.
    /// </summary>
    public float WeaponWeightStrengthFactor(float weaponWeight, float strength, float strengthInjuryMultiplier) =>
        constants.MinStrengthXpMult + Math.Clamp((weaponWeight - strength * strengthInjuryMultiplier) / constants.WeightStrengthDiff1x, 0, constants.WeightStrengthDiffMax);

    /// <summary>
    /// A combat event for one character (<c>FUN_1408c6980</c>). <paramref name="opponentSkill"/> is the opponent's defence for the attacker's events
    /// (<see cref="XpEvent.HitDealt"/>, <see cref="XpEvent.GlancingHit"/>) and the opponent's attack for <see cref="XpEvent.Defended"/> and
    /// <see cref="XpEvent.HitTaken"/>. <paramref name="weaponStat"/> is the current weapon's skill (none when unarmed); <paramref name="strengthFactor"/>
    /// is <see cref="WeaponWeightStrengthFactor"/>, or for unarmed (1 - the encumbrance factor). <paramref name="damageTaken"/> is the incoming packet's cut + blunt + pierce + stun
    /// (<see cref="XpEvent.HitTaken"/> only). The gains are scaled by the Global damage multiplier option.
    /// <b>Verified</b> coefficients (decompile, docs/game/character-stats.md "Levelling"): per event, which stats gain and by how much.
    /// </summary>
    public void Combat(CharacterStats stats, RaceData race, XpEvent ev, bool unarmed, StatsEnumerated weaponStat, float opponentSkill, float strengthFactor,
        float damageTaken = 0)
    {
        var attackStat = unarmed ? StatsEnumerated.MartialArts : StatsEnumerated.MeleeAttack;
        float g = options.GlobalDamageMultiplier;
        float attackCoef = 0, weaponCoef = 0, defenceCoef = 0, dexterityCoef = 0, strengthScale = 1;
        switch (ev)
        {
            case XpEvent.HitDealt: attackCoef = 1; weaponCoef = 1; dexterityCoef = 1; break;
            case XpEvent.GlancingHit: attackCoef = 0.5f; weaponCoef = 0.4f; dexterityCoef = 0.5f; break;
            case XpEvent.Defended: weaponCoef = 0.1f; defenceCoef = 0.25f; strengthScale = 0.4f; break;
            case XpEvent.HitTaken: defenceCoef = 2; strengthScale = 0.1f; break;
            case XpEvent.Event6: attackCoef = 0.25f; weaponCoef = 0.1f; break;
            default: return;
        }
        // Verified (FUN_1408c6980): the attack gain takes the race's melee attack entry (map[2]) even when unarmed; the weapon skill gain takes no race
        // factor (the race's STAT_WEAPONS entry, map[20], scales the skill inside the difference instead); dexterity gets the attack's difference.
        if (attackCoef > 0)
            stats.Gain(attackStat, EventUnit * g * attackCoef * SkillDifferenceFactor(stats[attackStat], opponentSkill) * race.StatMultiplier(StatsEnumerated.MeleeAttack));
        if (weaponCoef > 0 && weaponStat != StatsEnumerated.None)
            stats.Gain(weaponStat, EventUnit * g * weaponCoef * SkillDifferenceFactor(stats[weaponStat] * race.StatMultiplier(StatsEnumerated.Weapons), opponentSkill));
        if (dexterityCoef > 0)
            PerEvent(stats, race, StatsEnumerated.Dexterity, g * dexterityCoef * SkillDifferenceFactor(stats[attackStat], opponentSkill));
        if (defenceCoef > 0)
        {
            // Event 2 puts the race entry (map[19]) into the difference, event 4 into the gain.
            if (ev == XpEvent.Defended)
                stats.Gain(StatsEnumerated.MeleeDefence, EventUnit * g * defenceCoef
                    * SkillDifferenceFactor(stats[StatsEnumerated.MeleeDefence] * race.StatMultiplier(StatsEnumerated.MeleeDefence), opponentSkill));
            else
                PerEvent(stats, race, StatsEnumerated.MeleeDefence, g * defenceCoef * SkillDifferenceFactor(stats[StatsEnumerated.MeleeDefence], opponentSkill));
        }
        // Strength tail of every event: core(0.075 x 0.55 x 2 x xp rate strength x race[1]), scaled per event and by the weight factor.
        stats.Gain(StatsEnumerated.Strength,
            0.075f * 0.55f * 2 * constants.XpRateStrength * race.StatMultiplier(StatsEnumerated.Strength) * strengthScale * strengthFactor * g);
        if (ev == XpEvent.HitTaken)
        {
            // Verified (FUN_1408c6980 event 4): toughness x CharStats +0x34 (the stat scale) against the packet's four terms, x xp rate toughness.
            stats.Gain(StatsEnumerated.Toughness, EventUnit * g * constants.XpRateToughness
                * SkillDifferenceFactor(stats[StatsEnumerated.Toughness] * stats.Scale, damageTaken) * race.StatMultiplier(StatsEnumerated.Toughness));
        }
    }

    /// <summary>
    /// Toughness from a martial artist's own punch (<c>FUN_1408c68e0</c>, called only from <c>MedicalSystem::punchSomething</c>): 0.075 x k x <c>xp rate toughness</c> x
    /// race[toughness] x the Global damage multiplier, k = 0.25 for hit material 1 (metal), 0.5 for 5 (blocked), else 0.05.
    /// </summary>
    public float ToughnessFromDamage(CharacterStats stats, RaceData race, int material)
    {
        float k = material == 1 ? 0.25f : material == 5 ? 0.5f : 0.05f;
        return stats.Gain(StatsEnumerated.Toughness, 0.075f * k * constants.XpRateToughness * race.StatMultiplier(StatsEnumerated.Toughness) * options.GlobalDamageMultiplier);
    }

    /// <summary>Toughness from losing a limb (<c>FUN_14064edc0</c>): the generic entry with dt 1 and amount 300, a large one-off gain.</summary>
    public float ToughnessFromLimbLoss(CharacterStats stats, RaceData race) =>
        Continuous(stats, race, StatsEnumerated.Toughness, 1, 300);

    /// <summary>
    /// Strength from carrying (<c>FUN_1408c60c0</c>, called by the movement tick): amount = (1 - e)^2 capped at 0.5, <paramref name="doubled"/> while the character carries something
    /// (Character <c>+0x388</c>, the type of its <c>carryingObject</c> handle, is not 11 NULL_ITEM; KenshiLib 1ce4a16), times <c>xp rate strength from walking</c>, through the continuous entry.
    /// </summary>
    public float StrengthFromCarrying(CharacterStats stats, RaceData race, float encumbranceFactor, float dtSeconds, bool doubled) =>
        Continuous(stats, race, StatsEnumerated.Strength, dtSeconds, StrengthCarryRate(encumbranceFactor, doubled));

    /// <summary>The amount <see cref="StrengthFromCarrying"/> feeds the continuous entry, which the information panel shows as "Strength XP:" (<c>FUN_1408c60c0</c>; 0 when unencumbered).</summary>
    public float StrengthCarryRate(float encumbranceFactor, bool doubled) =>
        1 - encumbranceFactor <= 0 ? 0 : MathF.Min((1 - encumbranceFactor) * (1 - encumbranceFactor), 0.5f) * (doubled ? 2 : 1) * constants.XpRateStrengthFromWalking;

    /// <summary>What the information panel shows as "Athletics XP:" (<c>FUN_1408c5ec0</c>): the encumbrance factor, x1.5 above 80 % of the run value; <see cref="AthleticsFromRunning"/> scales it by <c>xp rate athletics</c>.</summary>
    public static float AthleticsRate(float encumbranceFactor, float speed, float runValue) =>
        MathF.Max(encumbranceFactor, 0) * (runValue * 0.8f < speed ? 1.5f : 1);

    /// <summary>
    /// Athletics from moving (<c>FUN_1408c6660</c>, <c>FUN_1408c5ec0</c>, <b>Verified</b>): <see cref="AthleticsRate"/> (x1.5 when the speed is over 80% of the run value
    /// <paramref name="runValue"/>, not the current maximum) times <c>xp rate athletics</c> through the continuous entry.
    /// </summary>
    public float AthleticsFromRunning(CharacterStats stats, RaceData race, float encumbranceFactor, float speed, float runValue, float dtSeconds)
    {
        float amount = AthleticsRate(encumbranceFactor, speed, runValue) * constants.XpRateAthletics;
        return Continuous(stats, race, StatsEnumerated.Athletics, dtSeconds, amount);
    }

    /// <summary>Swimming (stat 23) gains 5, or 10 when carrying, while in water (through the continuous entry).</summary>
    public float SwimmingInWater(CharacterStats stats, RaceData race, bool carrying, float dtSeconds) =>
        Continuous(stats, race, StatsEnumerated.Swimming, dtSeconds, carrying ? 10 : 5);

    /// <summary>
    /// Stealth while sneaking (<c>FUN_1408c65a0</c>, from the sneaker's sense update): the continuous entry with amount 0.2 when no hostile NPC is about, 0.9 when one has it
    /// within sight range unseen, 1.5 when a looker's spot meter on it is filling or full (Verified literals; the "about" and "seen" gates are <b>Observed</b> readings of
    /// the original's flags). The per-hearing-check gain of <c>FUN_1408c7330</c> is <see cref="HearingCheck"/>.
    /// </summary>
    public float Sneaking(CharacterStats stats, RaceData race, StealthExposure exposure, float dtSeconds) =>
        Continuous(stats, race, StatsEnumerated.Stealth, dtSeconds, SneakRate(exposure));

    /// <summary>The amount <see cref="Sneaking"/> feeds the continuous entry (<c>CharStats +0x170</c>, which the Stealth line's tooltip shows as "XP rate:").</summary>
    public static float SneakRate(StealthExposure exposure) => exposure switch { StealthExposure.Unobserved => 0.2f, StealthExposure.Near => 0.9f, _ => 1.5f };

    /// <summary>The factor after each hearing check (<c>FUN_1408c7330</c>: <c>DAT_1416ae650</c> = 0.7, <b>Verified</b>).</summary>
    public const float HearingFactorDecay = 0.7f;

    /// <summary>
    /// The amount of one hearing check's stealth XP before the exposure factor (<c>FUN_1408c7330</c>, <b>Verified</b>): 2 when the listener regards the heard character as an enemy, 2 more when
    /// <paramref name="home"/> (<see cref="AggroSystem"/> reads the rule), x20 (<c>DAT_141683188</c>) when it was heard.
    /// </summary>
    public static float HearingAmount(bool enemy, bool home, bool heard) => ((enemy ? 2 : 0) + (home ? 2 : 0)) * (heard ? 20 : 1);

    /// <summary>Stealth from the hearing checks: the generic entry with dt 1 (<c>DAT_14167c308</c>) and the summed amounts.</summary>
    public float HearingCheck(CharacterStats stats, RaceData race, float amount) => Continuous(stats, race, StatsEnumerated.Stealth, 1, amount);

    /// <summary>Lockpicking (<c>FUN_1408c62e0</c>): 1.2 x race[37] on success, 0.24 x on failure, straight into the core curve.</summary>
    public float Lockpicking(CharacterStats stats, RaceData race, bool success) =>
        stats.Gain(StatsEnumerated.Lockpicking, (success ? 1.2f : 0.24f) * race.StatMultiplier(StatsEnumerated.Lockpicking));

    /// <summary>Engineering (<c>FUN_1408c6250</c>): <c>0.02 x dt x 0.75 x race[5] x 0.75</c>, i.e. the continuous entry with amount 0.75.</summary>
    public float Engineering(CharacterStats stats, RaceData race, float dtSeconds) =>
        Continuous(stats, race, StatsEnumerated.Engineering, dtSeconds, 0.75f);

    /// <summary>
    /// Medic (<c>FUN_1408c6130</c>, <b>Verified</b> decompile): <c>dt / T x exp gain multiplier x race[stat]</c> added straight to the stat (no core curve, no cap), T seconds per point =
    /// lerp(<c>XP rate medic 1</c>, <c>XP rate medic 99</c>, skill / 100) and nothing when T is not above 0 (the order of the lerp's ends is read from the editor's field names). The
    /// race factor is the medic race's entry for the patient race's <c>heal stat</c>.
    /// </summary>
    public float Medic(CharacterStats stats, RaceData patientRace, RaceData medicRace, float dtSeconds, StatsEnumerated? healStat = null)
    {
        var stat = healStat ?? patientRace.HealStat;
        float skill = stats[stat];
        float t = skill * 0.01f;
        float secondsPerPoint = constants.XpRateMedic1 + (constants.XpRateMedic99 - constants.XpRateMedic1) * t;
        if (!(secondsPerPoint > 0)) return skill;
        return stats[stat] = skill + dtSeconds / secondsPerPoint * constants.ExpGainMultiplier * medicRace.StatMultiplier(patientRace.HealStat);
    }

    /// <summary>Mass combat (<c>FUN_1408c5e80</c>, <b>Verified</b> decompile): +0x9c rises by the exp gain multiplier x 0.05 per call, added straight (no core curve). Its one caller is the answer to
    /// a slot ask (<c>FUN_140666a30</c>, docs/game/combat.md "Attack slots and circling"): the asked character's, when it is stumbling or already fighting. The stat is save-only (nothing
    /// reads it), so nothing calls this.</summary>
    public float MassCombat(CharacterStats stats) =>
        stats[StatsEnumerated.MassCombat] += constants.ExpGainMultiplier * 0.05f;

    /// <summary>
    /// Toughness when a hit knocks the character out (<c>FUN_1408c6720</c>, from the part damage <c>FUN_14064f300</c>, <b>Verified</b> decompile): the core gain of
    /// 0.075 (exp gain multiplier x 0.1) x race[toughness].
    /// </summary>
    public float ToughnessFromKnockOut(CharacterStats stats, RaceData race) =>
        stats.Gain(StatsEnumerated.Toughness, EventUnit * race.StatMultiplier(StatsEnumerated.Toughness));

    /// <summary>
    /// The Toughness XP multiplier f (the HUD's, <c>FUN_1408c6780</c>): 1, or 2 with the left arm crippled, plus 2 for the right arm, 2 while the blood-loss trauma flag is set, and 2 for
    /// the "Critical wounds" term, which tests the worst part's fraction (at most 1) against 15 and so is always on.
    /// </summary>
    public static float ToughnessFactor(MedicalState medical)
    {
        var arms = medical.Aggregates();
        return (arms.LeftArmDisabled ? 2 : 1) + (arms.RightArmDisabled ? 2 : 0) + (medical.BloodKo ? 2 : 0) + 2;
    }

    /// <summary>
    /// Toughness for getting up on the player's order near enemies (<c>FUN_1408c6780</c>, <b>Observed</b>): the core gain of 0.075 x f x (<paramref name="enemies"/> + 10) x race[toughness],
    /// f = <see cref="ToughnessFactor"/>.
    /// </summary>
    public float ToughnessFromGettingUp(CharacterStats stats, RaceData race, MedicalState medical, int enemies) =>
        PerEvent(stats, race, StatsEnumerated.Toughness, ToughnessFactor(medical) * (enemies + 10));
}
