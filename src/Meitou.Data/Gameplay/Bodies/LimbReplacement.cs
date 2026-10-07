namespace Meitou.Data.Gameplay.Bodies;

/// <summary>
/// A LIMB_REPLACEMENT record: a prosthetic limb (docs/game/character-stats.md "Limb replacements and robots", <b>Verified</b>, 28 base records).
/// Each multiplier is stored twice, <c>X mult</c> at the lowest quality and <c>X mult 1</c> at the highest, and the item's quality grade
/// interpolates; how an ITEM's <c>quality</c> (1 to 100) maps to the grade is <b>Unknown</b>, so every method takes the interpolation
/// parameter <c>t</c> in [0, 1] directly.
/// </summary>
public sealed record LimbReplacement
{
    public const int FirstSlot = 50;

    public required string StringId { get; init; }
    public string Name { get; init; } = "";
    /// <summary><c>slot</c>: 50 left arm, 51 right arm, 52 left leg, 53 right leg.</summary>
    public int Slot { get; init; }
    public float CraftTimeHours { get; init; }
    public float HpLow { get; init; }
    public float HpHigh { get; init; }
    public float UnarmedDamageBonusLow { get; init; }
    public float UnarmedDamageBonusHigh { get; init; }
    public float OverallMultLow { get; init; } = 1;
    public float OverallMultHigh { get; init; } = 1;
    public float SwimmingLow { get; init; } = 1;
    public float SwimmingHigh { get; init; } = 1;
    public float StrengthLow { get; init; } = 1;
    public float StrengthHigh { get; init; } = 1;
    public float DexterityLow { get; init; } = 1;
    public float DexterityHigh { get; init; } = 1;
    /// <summary><c>thievery mult</c>, applied to both thieving (10) and lockpicking (37).</summary>
    public float ThieveryLow { get; init; } = 1;
    public float ThieveryHigh { get; init; } = 1;
    /// <summary><c>ranged mult</c> (stat 35, crossbows).</summary>
    public float RangedLow { get; init; } = 1;
    public float RangedHigh { get; init; } = 1;
    public float AthleticsLow { get; init; } = 1;
    public float AthleticsHigh { get; init; } = 1;
    public float StealthLow { get; init; } = 1;
    public float StealthHigh { get; init; } = 1;

    /// <summary>The slot as the 0 left arm, 1 right arm, 2 left leg, 3 right leg index of <see cref="BodyPartTemplate.LimbSlot"/>.</summary>
    public int LimbSlot => Slot - FirstSlot;

    public bool IsLeg => LimbSlot is 2 or 3;

    /// <summary>The part's base HP with this limb fitted at quality <paramref name="t"/> (replaces the race's base HP for that slot).</summary>
    public float Hp(float t) => Lerp(HpLow, HpHigh, t);

    public float UnarmedDamageBonus(float t) => Lerp(UnarmedDamageBonusLow, UnarmedDamageBonusHigh, t);

    /// <summary>
    /// The multiplier this limb applies to a stat (1 when it has none, <c>FUN_1400cd780</c>): strength, dexterity, thieving and lockpicking
    /// (the same field), crossbows, athletics, stealth and swimming.
    /// </summary>
    public float Multiplier(StatsEnumerated stat, float t) => stat switch
    {
        StatsEnumerated.Strength => Lerp(StrengthLow, StrengthHigh, t),
        StatsEnumerated.Dexterity => Lerp(DexterityLow, DexterityHigh, t),
        StatsEnumerated.Thieving or StatsEnumerated.Lockpicking => Lerp(ThieveryLow, ThieveryHigh, t),
        StatsEnumerated.Crossbows => Lerp(RangedLow, RangedHigh, t),
        StatsEnumerated.Athletics => Lerp(AthleticsLow, AthleticsHigh, t),
        StatsEnumerated.Stealth => Lerp(StealthLow, StealthHigh, t),
        StatsEnumerated.Swimming => Lerp(SwimmingLow, SwimmingHigh, t),
        _ => 1,
    };

    static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0, 1);

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
            HpLow = hp.Low, HpHigh = hp.High,
            UnarmedDamageBonusLow = unarmed.Low, UnarmedDamageBonusHigh = unarmed.High,
            OverallMultLow = overall.Low, OverallMultHigh = overall.High,
            SwimmingLow = swim.Low, SwimmingHigh = swim.High,
            StrengthLow = str.Low, StrengthHigh = str.High,
            DexterityLow = dex.Low, DexterityHigh = dex.High,
            ThieveryLow = thief.Low, ThieveryHigh = thief.High,
            RangedLow = ranged.Low, RangedHigh = ranged.High,
            AthleticsLow = ath.Low, AthleticsHigh = ath.High,
            StealthLow = stealth.Low, StealthHigh = stealth.High,
        };
    }
}
