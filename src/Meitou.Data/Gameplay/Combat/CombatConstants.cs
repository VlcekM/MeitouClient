using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay.Combat;

/// <summary>
/// The combat rows of the <c>GLOBAL CONSTANTS</c> record as the hit code uses them (docs/game/combat.md "Global tuning"), with the loader's rescalings applied once
/// (<c>FUN_14086b2b0</c>, <b>Verified</b>): the <c>damage multiplier</c> D (0.65) is multiplied into the blunt and cut damage at skill 1 and 99, the pierce
/// multiplier and the stumble maximum; the block increase and reduction are stored x0.1. A property is the <i>stored</i> number the formulas use.
/// <para>Unlike <see cref="GameConstants"/> (whose fallbacks are the editor's defaults), a field the record lacks takes the <b>base game's value</b>, so a missing
/// record still tunes like the base game; <see cref="BaseGame"/> is that, and the install test checks it against the real record.</para>
/// The remaining rows the combat code uses (knockout, bleeding, XP...) are in <see cref="GameConstants"/>.
/// </summary>
public sealed record CombatConstants
{
    /// <summary>The CONSTANTS record without any field set: the base game's values.</summary>
    public static CombatConstants BaseGame { get; } = From(null);

    /// <summary><c>damage multiplier</c> (0.65), already folded into the damages below.</summary>
    public float DamageMultiplier { get; init; }
    /// <summary>Stored <c>blunt damage 1</c> = D x 20: the blunt base at skill 1 (13).</summary>
    public float BluntDamage1 { get; init; }
    /// <summary>Stored <c>blunt damage 99</c> = D x 80 (52).</summary>
    public float BluntDamage99 { get; init; }
    /// <summary>Stored <c>cut damage 1</c> = D x 20 (13).</summary>
    public float CutDamage1 { get; init; }
    /// <summary>Stored <c>cut damage 99</c> = D x 80 (52).</summary>
    public float CutDamage99 { get; init; }
    /// <summary>Stored <c>pierce damage multiplier</c> = D x 1.2 (0.78).</summary>
    public float PierceDamageMultiplier { get; init; }
    /// <summary><c>bow damage 1</c> (1.1), stored unscaled. Its use is <b>Unknown</b>.</summary>
    public float BowDamage1 { get; init; }
    /// <summary><c>bow damage 99</c> (1.3), stored unscaled.</summary>
    public float BowDamage99 { get; init; }
    /// <summary>Stored <c>stumble damage max</c> = D x 80 (52).</summary>
    public float StumbleDamageMax { get; init; }
    /// <summary><c>unarmed damage mult</c> (0.8).</summary>
    public float UnarmedDamageMult { get; init; }
    /// <summary><c>attack chance factor</c> (0.05): the skew of the skill-against-skill roll.</summary>
    public float AttackChanceFactor { get; init; }
    /// <summary><c>base block chance</c> (70).</summary>
    public float BaseBlockChance { get; init; }
    /// <summary>Stored <c>block chance increase per 10 levels</c> x0.1 (1.2 per level of advantage).</summary>
    public float BlockChanceIncreasePerLevel { get; init; }
    /// <summary>Stored <c>block chance reduction per 10 levels</c> x0.1 (1.5 per level of disadvantage).</summary>
    public float BlockChanceReductionPerLevel { get; init; }
    /// <summary><c>max num attack slots</c> (1): how many enemies may melee one target (plus the race's extra slots).</summary>
    public int MaxNumAttackSlots { get; init; }
    /// <summary><c>damage resistance min</c> (-0.65): the toughness resistance at toughness 0.</summary>
    public float DamageResistanceMin { get; init; }
    /// <summary><c>damage resistance max</c> (0.65): at toughness 100.</summary>
    public float DamageResistanceMax { get; init; }
    /// <summary><c>weapon inventory weight mult</c> (0.5).</summary>
    public float WeaponInventoryWeightMult { get; init; }

    public static CombatConstants FromDatabase(GameDatabase db)
    {
        return From(ConstantsRecord.Find(db));
    }

    /// <summary>Reads a CONSTANTS record (null reads as an empty one) and applies the loader's rescalings.</summary>
    public static CombatConstants From(GameRecord? c)
    {
        float F(string key, float fallback) => RecordReading.Float(c, key, fallback);
        float d = F("damage multiplier", 0.65f);
        return new CombatConstants
        {
            DamageMultiplier = d,
            BluntDamage1 = d * F("blunt damage 1", 20),
            BluntDamage99 = d * F("blunt damage 99", 80),
            CutDamage1 = d * F("cut damage 1", 20),
            CutDamage99 = d * F("cut damage 99", 80),
            PierceDamageMultiplier = d * F("pierce damage multiplier", 1.2f),
            BowDamage1 = F("bow damage 1", 1.1f),
            BowDamage99 = F("bow damage 99", 1.3f),
            StumbleDamageMax = d * F("stumble damage max", 80),
            UnarmedDamageMult = F("unarmed damage mult", 0.8f),
            AttackChanceFactor = F("attack chance factor", 0.05f),
            BaseBlockChance = F("base block chance", 70),
            BlockChanceIncreasePerLevel = F("block chance increase per 10levels", 12) * 0.1f,
            BlockChanceReductionPerLevel = F("block chance reduction per 10levels", 15) * 0.1f,
            MaxNumAttackSlots = (int)F("max num attack slots", 1),
            DamageResistanceMin = F("damage resistance min", -0.65f),
            DamageResistanceMax = F("damage resistance max", 0.65f),
            WeaponInventoryWeightMult = F("weapon inventory weight mult", 0.5f),
        };
    }
}
