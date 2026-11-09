using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay.Economy;

/// <summary>
/// The base price of an item record at a quality: the game's <c>ItemValue</c> (<c>FUN_140896e20</c>), docs/game/economy.md section 4. All arithmetic
/// is 32-bit float with a truncation to int after every multiply in the weapon, armour, crossbow and limb branches, and <c>floor(x + 0.5)</c> in the
/// plain and ingredient branches (<b>Verified</b> in the disassembly); a model in doubles is off by one on products such as 500 x 1.4. With
/// <c>applyCategoryMult</c> false the result is the "raw" value (no CONSTANTS category row).
/// </summary>
public static class ItemValue
{
    /// <summary>
    /// The armour class row at quality 0 and 100 (<c>ArmourClass</c>; the first value column of the tables combat.md shares, <b>Verified</b>).
    /// </summary>
    static readonly float[] ClassLo = [100, 400, 800, 400], ClassHi = [5000, 30000, 35000, 40000];

    /// <summary>
    /// The material row (<c>ArmourType</c>) at quality 0 and 100.
    /// </summary>
    static readonly float[] MaterialLo = [0.2f, 0.5f, 1.0f, 1.5f], MaterialHi = [0.8f, 1.0f, 1.5f, 1.5f];

    /// <summary>
    /// How deep the <c>ingredients</c> recursion may go before an ingredient counts 0 (an engine guard against a cycle in a mod; the base data nests
    /// at most a few levels).
    /// </summary>
    const int MaxIngredientDepth = 16;

    /// <summary>
    /// The value of <paramref name="record"/> at <paramref name="quality"/> (0 to 100). <paramref name="manufacturer"/> is a WEAPON_MANUFACTURER
    /// (weapons only, its <c>price mod</c>); <paramref name="find"/> resolves the <c>ingredients</c> of a crafted item (null: such an item is
    /// worth 0).
    /// </summary>
    public static int Of(
        GameRecord record, float quality, bool applyCategoryMult, PriceConstants k, GameRecord? manufacturer = null,
            Func<string, GameRecord?>? find = null) =>
        Of(record, quality, applyCategoryMult, k, manufacturer, find, 0);

    /// <summary>
    /// The same against a database: ingredients are found in it.
    /// </summary>
    public static int Of(GameDatabase db, GameRecord record, float quality, bool applyCategoryMult, PriceConstants k,
        GameRecord? manufacturer = null) =>
        Of(record, quality, applyCategoryMult, k, manufacturer, db.Find, 0);

    /// <summary>
    /// The value of <paramref name="r"/> at <paramref name="quality"/>, <paramref name="find"/> resolving its ingredients; <paramref name="depth"/>
    /// counts the ingredient levels walked so far.
    /// </summary>
    static int Of(GameRecord r, float quality, bool apply, PriceConstants k, GameRecord? manufacturer, Func<string, GameRecord?>? find, int depth)
    {
        float s = quality * 0.01f;
        switch (r.Type)
        {
            case FcsRecordType.Weapon:
                {
                    int v = RecordReading.Int(r, "value", 0);
                    if (manufacturer is not null) v = (int)(v * RecordReading.Float(manufacturer, "price mod", 1));
                    int raw = (int)(Lerp(Logistic(s), 15, 160) * v);
                    return apply ? (int)(raw * k.SwordPriceMult) : raw;
                }

            case FcsRecordType.Armour:
                {
                    bool covers = r.GetReferences("part coverage").Count > 0;
                    int v;
                    if (quality > 0 && covers)
                    {
                        int c = Math.Clamp(RecordReading.Int(r, "class", 0), 0, 3);
                        int m = Math.Clamp(RecordReading.Int(r, "material type", 0), 0, 3);
                        v = (int)Lerp(s * s, ClassLo[c] * MaterialLo[m], ClassHi[c] * MaterialHi[m]);
                    }
                    else v = RecordReading.Int(r, "value", 0);

                    if (apply) v = (int)(v * (covers ? k.ArmourPriceMult : k.ClothingPriceMult));
                    v = (int)(v * RecordReading.Float(r, "relative price mult", 1));
                    return (int)(v * SlotFactor(RecordReading.Int(r, "slot", 0)));
                }

            case FcsRecordType.Crossbow or FcsRecordType.LimbReplacement:
                {
                    int raw = (int)Lerp(s * s, RecordReading.Int(r, "value", 0), RecordReading.Int(r, "value 1", 0));
                    if (!apply) return raw;

                    return (int)(raw * (r.Type == FcsRecordType.Crossbow ? k.CrossbowRecordMult : k.LimbRecordMult));
                }

            case FcsRecordType.Container:
                return RecordReading.Int(r, "value", 0);
        }

        var ingredients = r.GetReferences("ingredients");
        if (ingredients.Count > 0)
        {
            // The item's own value is ignored: each ingredient at the same quality and with the category rows, scaled by its share (v0 percent of one
            // unit) and the margin.
            if (find is null || depth >= MaxIngredientDepth) return 0;

            float profit = RecordReading.Float(r, "profitability", 0) + 1;
            int total = 0;
            foreach (var ing in ingredients)
            {
                if (find(ing.TargetStringId) is not { } part) continue;

                int each = Of(part, quality, true, k, null, find, depth + 1);
                total += (int)MathF.Floor(each * (ing.Values.Value0 / 100f) * profit + 0.5f);
            }

            return total;
        }

        int value = RecordReading.Int(r, "value", 0);
        if (!apply || RecordReading.Bool(r, "artifact", false)) return value;

        float mult = RecordReading.Bool(r, "food crop", false) ? k.FoodPriceMult : k.TradePriceMult;
        return (int)MathF.Floor(value * mult + 0.5f);
    }

    /// <summary>
    /// The weapon quality curve: 1 / (1 + e^(-(2s - 1) x 5 x 0.8)), 0.018 at quality 0, 0.5 at 50, 0.982 at 100 (constants read from the exe,
    /// <b>Verified</b>).
    /// </summary>
    public static float Logistic(float s) => 1f / (1f + MathF.Exp(-((s * 2f - 1f) * 5f * 0.8f)));

    /// <summary>
    /// The game's <c>lerp(t, a, b)</c> = a + (b - a) x t.
    /// </summary>
    static float Lerp(float t, float a, float b) => a + (b - a) * t;

    /// <summary>
    /// The ARMOUR <c>slot</c> price factor: hat x0.3, legs x0.5, shirt x1.5, boots x0.2, any other slot x1 (immediates <b>Verified</b> in
    /// the disassembly).
    /// </summary>
    public static float SlotFactor(int slot) => slot switch { 3 => 0.3f, 6 => 0.5f, 8 => 1.5f, 9 => 0.2f, _ => 1f };
}
