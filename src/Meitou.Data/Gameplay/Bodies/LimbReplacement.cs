namespace Meitou.Data.Gameplay.Bodies;

/// <summary>
/// A LIMB_REPLACEMENT record: a prosthetic limb (docs/game/character-stats.md "Limb replacements and robots", <b>Verified</b>, 28 base records). Each
/// multiplier is stored twice, <c>X mult</c> at the lowest quality and <c>X mult 1</c> at the highest, and the item's quality grade interpolates by
/// the item's <c>level</c> (t = level x 0.01, not the save's <c>quality</c> float): HP, unarmed bonus and swimming linearly, the skill multipliers by
/// <see cref="GradeCurve"/>. Every method takes t; <see cref="SlotOf"/> names a slot's section.
/// </summary>
public sealed record LimbReplacement
{
    /// <summary>
    /// The <c>slot</c> number of the left arm, the first of the four limb slots (fcs_enums.def <c>LimbSlot</c> LEFT_ARM=50).
    /// </summary>
    public const int FirstSlot = 50;

    /// <summary>
    /// The LIMB_REPLACEMENT's string id.
    /// </summary>
    public required string StringId { get; init; }

    /// <summary>
    /// The LIMB_REPLACEMENT's name in the active language.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// <c>slot</c>: 50 left arm, 51 right arm, 52 left leg, 53 right leg.
    /// </summary>
    public int Slot { get; init; }

    /// <summary>
    /// <c>craft time hrs</c> (game hours, default 0).
    /// </summary>
    public float CraftTimeHours { get; init; }

    /// <summary>
    /// <c>HP</c>: the part's base HP at the lowest quality (default 0).
    /// </summary>
    public float HpLow { get; init; }

    /// <summary>
    /// <c>HP 1</c>: the part's base HP at the highest quality (default <see cref="HpLow"/>).
    /// </summary>
    public float HpHigh { get; init; }

    /// <summary>
    /// <c>unarmed damage bonus</c> at the lowest quality (default 0).
    /// </summary>
    public float UnarmedDamageBonusLow { get; init; }

    /// <summary>
    /// <c>unarmed damage bonus 1</c> at the highest quality (default the low value).
    /// </summary>
    public float UnarmedDamageBonusHigh { get; init; }

    /// <summary>
    /// <c>overall mult</c> at the lowest quality (default 1).
    /// </summary>
    public float OverallMultLow { get; init; } = 1;

    /// <summary>
    /// <c>overall mult 1</c> at the highest quality (default the low value).
    /// </summary>
    public float OverallMultHigh { get; init; } = 1;

    /// <summary>
    /// <c>swimming mult</c> at the lowest quality (default 1).
    /// </summary>
    public float SwimmingLow { get; init; } = 1;

    /// <summary>
    /// <c>swimming mult 1</c> at the highest quality (default the low value).
    /// </summary>
    public float SwimmingHigh { get; init; } = 1;

    /// <summary>
    /// <c>strength mult</c> at the lowest quality (default 1).
    /// </summary>
    public float StrengthLow { get; init; } = 1;

    /// <summary>
    /// <c>strength mult 1</c> at the highest quality (default the low value).
    /// </summary>
    public float StrengthHigh { get; init; } = 1;

    /// <summary>
    /// <c>dexterity mult</c> at the lowest quality (default 1).
    /// </summary>
    public float DexterityLow { get; init; } = 1;

    /// <summary>
    /// <c>dexterity mult 1</c> at the highest quality (default the low value).
    /// </summary>
    public float DexterityHigh { get; init; } = 1;

    /// <summary>
    /// <c>thievery mult</c>, applied to both thieving (10) and lockpicking (37).
    /// </summary>
    public float ThieveryLow { get; init; } = 1;

    /// <summary>
    /// <c>thievery mult 1</c> at the highest quality (default the low value).
    /// </summary>
    public float ThieveryHigh { get; init; } = 1;

    /// <summary>
    /// <c>ranged mult</c> (stat 35, crossbows).
    /// </summary>
    public float RangedLow { get; init; } = 1;

    /// <summary>
    /// <c>ranged mult 1</c> at the highest quality (default the low value).
    /// </summary>
    public float RangedHigh { get; init; } = 1;

    /// <summary>
    /// <c>athletics mult</c> at the lowest quality (default 1).
    /// </summary>
    public float AthleticsLow { get; init; } = 1;

    /// <summary>
    /// <c>athletics mult 1</c> at the highest quality (default the low value).
    /// </summary>
    public float AthleticsHigh { get; init; } = 1;

    /// <summary>
    /// <c>stealth mult</c> at the lowest quality (default 1).
    /// </summary>
    public float StealthLow { get; init; } = 1;

    /// <summary>
    /// <c>stealth mult 1</c> at the highest quality (default the low value).
    /// </summary>
    public float StealthHigh { get; init; } = 1;

    /// <summary>
    /// The slot as the 0 left arm, 1 right arm, 2 left leg, 3 right leg index of <see cref="BodyPartTemplate.LimbSlot"/>.
    /// </summary>
    public int LimbSlot => Slot - FirstSlot;

    /// <summary>
    /// True for the two leg slots (limb slots 2 and 3).
    /// </summary>
    public bool IsLeg => LimbSlot is 2 or 3;

    /// <summary>
    /// The inventory section of a fitted prosthetic in limb slot 0 to 3 (the save's <c>section</c>; strings of <c>FUN_1400d04d0</c>).
    /// </summary>
    public static string SlotOf(int limbSlot) => limbSlot switch
    {
        0 => "left_arm_replace",
        1 => "right_arm_replace",
        2 => "left_leg_replace",
        _ => "right_leg_replace",
    };

    /// <summary>
    /// The part's base HP with this limb fitted at quality <paramref name="t"/> (replaces the race's base HP for that slot): <c>HP</c> to <c>HP
    /// 1</c>, rounded to the nearest 5, half up (<b>Verified</b>, the limb item's setup <c>FUN_1400ce2d0</c> passes it to <c>FUN_1409b2310</c> with
    /// 5: floor((hp + 2.5) / 5) x 5; a Masterwork lifter arm's 193.5 is 195, #360).
    /// </summary>
    public float Hp(float t) => (float)(Math.Floor((Lerp(HpLow, HpHigh, t) + 2.5) / 5) * 5);

    /// <summary>
    /// The unarmed damage bonus at quality <paramref name="t"/>, truncated to an int as the original stores it.
    /// </summary>
    public int UnarmedDamageBonus(float t) => (int)Lerp(UnarmedDamageBonusLow, UnarmedDamageBonusHigh, t);

    /// <summary>
    /// The multiplier this limb applies to a stat (1 when it has none, <c>FUN_1400cd780</c>): swimming lerps by <paramref name="t"/> like HP;
    /// strength, dexterity, thieving and lockpicking (the same field), crossbows, athletics and stealth follow <see cref="GradeCurve"/> of the level
    /// round(t x 100).
    /// </summary>
    public float Multiplier(StatsEnumerated stat, float t)
    {
        int level = (int)MathF.Round(t * 100);
        return stat switch
        {
            StatsEnumerated.Strength => GradeCurve(level, StrengthLow, StrengthHigh),
            StatsEnumerated.Dexterity => GradeCurve(level, DexterityLow, DexterityHigh),
            StatsEnumerated.Thieving or StatsEnumerated.Lockpicking => GradeCurve(level, ThieveryLow, ThieveryHigh),
            StatsEnumerated.Crossbows => GradeCurve(level, RangedLow, RangedHigh),
            StatsEnumerated.Athletics => GradeCurve(level, AthleticsLow, AthleticsHigh),
            StatsEnumerated.Stealth => GradeCurve(level, StealthLow, StealthHigh),
            StatsEnumerated.Swimming => Lerp(SwimmingLow, SwimmingHigh, t),
            _ => 1,
        };
    }

    /// <summary>
    /// A skill multiplier at item level <paramref name="level"/> (<c>FUN_1400cd500</c>, docs/game/character-stats.md "Limb replacements and robots",
    /// <b>Verified</b>): a field that turns a penalty into a bonus (low &lt; 1 &lt;= high) is 1 at level 40, rising from low at level 5 and on to
    /// high at 95; any other lerps by level x 0.01.
    /// </summary>
    public static float GradeCurve(int level, float low, float high)
    {
        if (low < 1 && high >= 1)
            return level >= 40 ? Lerp(1, high, (level - 40) / 55f) : Lerp(low, 1, (level - 5) / 35f);
        return low + (high - low) * (level * 0.01f);
    }

    /// <summary>
    /// Lerps from <paramref name="a"/> to <paramref name="b"/> by <paramref name="t"/> clamped to 0..1.
    /// </summary>
    static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0, 1);

    /// <summary>
    /// Reads a LIMB_REPLACEMENT record: each multiplier pair from <c>X</c> and <c>X 1</c>, the second defaulting to the first.
    /// </summary>
    public static LimbReplacement From(GameRecord r)
    {
        // "X mult" is the low-quality value, "X mult 1" the high-quality one; a record that has only the first uses it for both.
        (float Low, float High) Pair(string key, float fallback)
        {
            float lo = RecordReading.Float(r, key, fallback);
            return (lo, RecordReading.Float(r, key + " 1", lo));
        }
        var hp = Pair("HP", 0);
        var unarmed = Pair("unarmed damage bonus", 0);
        var overall = Pair("overall mult", 1);
        var swim = Pair("swimming mult", 1);
        var str = Pair("strength mult", 1);
        var dex = Pair("dexterity mult", 1);
        var thief = Pair("thievery mult", 1);
        var ranged = Pair("ranged mult", 1);
        var ath = Pair("athletics mult", 1);
        var stealth = Pair("stealth mult", 1);
        return new LimbReplacement
        {
            StringId = r.StringId,
            Name = r.Name,
            Slot = RecordReading.Int(r, "slot", 0),
            CraftTimeHours = RecordReading.Float(r, "craft time hrs", 0),
            HpLow = hp.Low,
            HpHigh = hp.High,
            UnarmedDamageBonusLow = unarmed.Low,
            UnarmedDamageBonusHigh = unarmed.High,
            OverallMultLow = overall.Low,
            OverallMultHigh = overall.High,
            SwimmingLow = swim.Low,
            SwimmingHigh = swim.High,
            StrengthLow = str.Low,
            StrengthHigh = str.High,
            DexterityLow = dex.Low,
            DexterityHigh = dex.High,
            ThieveryLow = thief.Low,
            ThieveryHigh = thief.High,
            RangedLow = ranged.Low,
            RangedHigh = ranged.High,
            AthleticsLow = ath.Low,
            AthleticsHigh = ath.High,
            StealthLow = stealth.Low,
            StealthHigh = stealth.High,
        };
    }
}