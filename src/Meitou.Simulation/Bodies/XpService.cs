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
    /// is <see cref="WeaponWeightStrengthFactor"/>, or for unarmed (1 - the encumbrance factor). The gains are scaled by the Global damage multiplier option.
    /// <b>Observed</b> coefficients: hit 1 for attack, weapon skill and dexterity; glancing 0.5 and 0.4; defended defence 0.25 and weapon 0.1 (plus attack
    /// 0.25, from event 6's description); taken defence 2 and the strength tail x0.1. The dexterity coefficient is taken equal to the weapon-skill one
    /// (the original says "similarly", an engine choice). Toughness from a hit taken is <see cref="ToughnessFromDamage"/>.
    /// </summary>
    public void Combat(CharacterStats stats, RaceData race, XpEvent ev, bool unarmed, StatsEnumerated weaponStat, float opponentSkill, float strengthFactor,
        float damageKind = 0)
    {
        var attackStat = unarmed ? StatsEnumerated.MartialArts : StatsEnumerated.MeleeAttack;
        float g = options.GlobalDamageMultiplier;
        float attackCoef, weaponCoef, defenceCoef, strengthScale;
        switch (ev)
        {
            case XpEvent.HitDealt: attackCoef = 1; weaponCoef = 1; defenceCoef = 0; strengthScale = 1; break;
            case XpEvent.GlancingHit: attackCoef = 0.5f; weaponCoef = 0.4f; defenceCoef = 0; strengthScale = 1; break;
            case XpEvent.Defended: attackCoef = 0.25f; weaponCoef = 0.1f; defenceCoef = 0.25f; strengthScale = 0.4f; break;
            case XpEvent.HitTaken: attackCoef = 0; weaponCoef = 0; defenceCoef = 2; strengthScale = 0.1f; break;
            case XpEvent.Event6: attackCoef = 0.25f; weaponCoef = 0.1f; defenceCoef = 0; strengthScale = 0.4f; break;
            default: return;
        }
        if (attackCoef > 0)
            PerEvent(stats, race, attackStat, g * attackCoef * SkillDifferenceFactor(stats[attackStat], opponentSkill));
        if (weaponCoef > 0 && weaponStat != StatsEnumerated.None)
        {
            PerEvent(stats, race, weaponStat, g * weaponCoef * SkillDifferenceFactor(stats[weaponStat], opponentSkill));
            PerEvent(stats, race, StatsEnumerated.Dexterity, g * weaponCoef * SkillDifferenceFactor(stats.Dexterity, opponentSkill));
        }
        if (defenceCoef > 0)
            PerEvent(stats, race, StatsEnumerated.MeleeDefence, g * defenceCoef * SkillDifferenceFactor(stats[StatsEnumerated.MeleeDefence], opponentSkill));
        // Strength tail of every event: core(0.075 x 0.55 x 2 x xp rate strength x race[1]), scaled per event and by the weight factor.
        stats.Gain(StatsEnumerated.Strength,
            0.075f * 0.55f * 2 * constants.XpRateStrength * race.StatMultiplier(StatsEnumerated.Strength) * strengthScale * strengthFactor * g);
        if (ev == XpEvent.HitTaken) ToughnessFromDamage(stats, race, damageKind);
    }

    /// <summary>
    /// Toughness per damage received (<c>FUN_1408c68e0</c>): 0.075 x k x <c>xp rate toughness</c> x race[toughness], k = 0.05 normally, 0.25 for damage kind
    /// 1 and 0.5 for kind 5 (the kinds are not decoded).
    /// </summary>
    public float ToughnessFromDamage(CharacterStats stats, RaceData race, float damageKind = 0)
    {
        float k = damageKind == 1 ? 0.25f : damageKind == 5 ? 0.5f : 0.05f;
        return stats.Gain(StatsEnumerated.Toughness, 0.075f * k * constants.XpRateToughness * race.StatMultiplier(StatsEnumerated.Toughness));
    }

    /// <summary>Toughness from losing a limb (<c>FUN_14064edc0</c>): the generic entry with dt 1 and amount 300, a large one-off gain.</summary>
    public float ToughnessFromLimbLoss(CharacterStats stats, RaceData race) =>
        Continuous(stats, race, StatsEnumerated.Toughness, 1, 300);

    /// <summary>
    /// Strength from carrying (<c>FUN_1408c60c0</c>, called by the movement tick): amount = (1 - e)^2 capped at 0.5, doubled unless in state 0xb (pass
    /// <paramref name="doubled"/> = true for the usual case), times <c>xp rate strength from walking</c>, through the continuous entry.
    /// </summary>
    public float StrengthFromCarrying(CharacterStats stats, RaceData race, float encumbranceFactor, float dtSeconds, bool doubled = true)
    {
        float amount = MathF.Min((1 - encumbranceFactor) * (1 - encumbranceFactor), 0.5f) * (doubled ? 2 : 1) * constants.XpRateStrengthFromWalking;
        return Continuous(stats, race, StatsEnumerated.Strength, dtSeconds, amount);
    }

    /// <summary>
    /// Athletics from running (<c>FUN_1408c6660</c>): the encumbrance factor (x1.5 when the speed is over 80% of the character's maximum) times
    /// <c>xp rate athletics</c> through the continuous entry.
    /// </summary>
    public float AthleticsFromRunning(CharacterStats stats, RaceData race, float encumbranceFactor, float speedFractionOfMax, float dtSeconds)
    {
        float amount = encumbranceFactor * (speedFractionOfMax > 0.8f ? 1.5f : 1) * constants.XpRateAthletics;
        return Continuous(stats, race, StatsEnumerated.Athletics, dtSeconds, amount);
    }

    /// <summary>Swimming (stat 23) gains 5, or 10 when carrying, while in water (through the continuous entry).</summary>
    public float SwimmingInWater(CharacterStats stats, RaceData race, bool carrying, float dtSeconds) =>
        Continuous(stats, race, StatsEnumerated.Swimming, dtSeconds, carrying ? 10 : 5);

    /// <summary>Lockpicking (<c>FUN_1408c62e0</c>): 1.2 x race[37] on success, 0.24 x on failure, straight into the core curve.</summary>
    public float Lockpicking(CharacterStats stats, RaceData race, bool success) =>
        stats.Gain(StatsEnumerated.Lockpicking, (success ? 1.2f : 0.24f) * race.StatMultiplier(StatsEnumerated.Lockpicking));

    /// <summary>Engineering (<c>FUN_1408c6250</c>): <c>0.02 x dt x 0.75 x race[5] x 0.75</c>, i.e. the continuous entry with amount 0.75.</summary>
    public float Engineering(CharacterStats stats, RaceData race, float dtSeconds) =>
        Continuous(stats, race, StatsEnumerated.Engineering, dtSeconds, 0.75f);

    /// <summary>
    /// Medic (<c>FUN_1408c6130</c>): <c>dt / T x exp gain multiplier x race[stat]</c>, T seconds per point from <c>XP rate medic 1</c> (at skill 1) to
    /// <c>XP rate medic 99</c> (at skill 99). The interpolation argument is hidden in the decompile (<b>Unknown</b>); here t = (skill - 1) / 98. The
    /// stat trained is the patient's race's <c>heal stat</c>.
    /// </summary>
    public float Medic(CharacterStats stats, RaceData patientRace, RaceData medicRace, float dtSeconds)
    {
        var stat = patientRace.HealStat;
        float skill = stats[stat];
        float t = Math.Clamp((skill - 1) / 98, 0, 1);
        float secondsPerPoint = constants.XpRateMedic1 + (constants.XpRateMedic99 - constants.XpRateMedic1) * t;
        return stats.Gain(stat, dtSeconds / secondsPerPoint * constants.ExpGainMultiplier * medicRace.StatMultiplier(stat));
    }

    /// <summary>Mass combat (<c>FUN_1408c5e80</c>): rises by 0.75 x 0.05 per call (the exp gain multiplier x 0.05).</summary>
    public float MassCombat(CharacterStats stats) =>
        stats.Gain(StatsEnumerated.MassCombat, constants.ExpGainMultiplier * 0.05f);
}
