namespace Meitou.Data.Gameplay.Economy;

/// <summary>
/// The price rows of the <c>GLOBAL CONSTANTS</c> record as the price code uses them (docs/game/economy.md section 3, <b>Verified</b> row by row): the
/// constants loader (<c>FUN_14086b2b0</c>) multiplies <c>global price mult</c> into the seven category rows once, so each category property here
/// already holds that product; the sell fractions, the profit margin and the blueprint multiplier are stored as read. A field the record lacks takes
/// the <b>base game's value</b> (the editor's text is stale in 9 of the 14 rows, section 14), so <see cref="BaseGame"/> prices like the base game.
/// </summary>
public sealed record PriceConstants
{
    /// <summary>
    /// The CONSTANTS record without any field set: the base game's values.
    /// </summary>
    public static PriceConstants BaseGame { get; } = From(null);

    /// <summary>
    /// <c>global price mult</c> (1.0), already folded into the seven category rows.
    /// </summary>
    public float GlobalPriceMult { get; init; }

    /// <summary>
    /// <c>armor price mult</c> x global (0.6): armour with <c>part coverage</c>.
    /// </summary>
    public float ArmourPriceMult { get; init; }

    /// <summary>
    /// <c>clothing price mult</c> x global (1.0): armour without <c>part coverage</c>.
    /// </summary>
    public float ClothingPriceMult { get; init; }

    /// <summary>
    /// <c>sword price mult</c> x global (0.4): every WEAPON.
    /// </summary>
    public float SwordPriceMult { get; init; }

    /// <summary>
    /// <c>trade price mult</c> x global (0.6): plain items that are not <c>food crop</c>.
    /// </summary>
    public float TradePriceMult { get; init; }

    /// <summary>
    /// <c>food price mult</c> x global (0.5): plain items with <c>food crop</c>.
    /// </summary>
    public float FoodPriceMult { get; init; }

    /// <summary>
    /// The multiplier CROSSBOW records get: the game reads the <c>robotics price mult</c> slot for them (x global, 1.0). The crossbow and limb rows
    /// are swapped in <c>ItemValue</c> (<b>Verified</b> in the disassembly of both branches against the loader's index table).
    /// </summary>
    public float CrossbowRecordMult { get; init; }

    /// <summary>
    /// The multiplier LIMB_REPLACEMENT records get: the <c>crossbow price mult</c> slot (x global, 1.0), see <see cref="CrossbowRecordMult"/>.
    /// </summary>
    public float LimbRecordMult { get; init; }

    /// <summary>
    /// <c>trade profit margins</c> (0.3): the spread of a town's market factors (section 7.2).
    /// </summary>
    public float TradeProfitMargins { get; init; }

    /// <summary>
    /// <c>loot price mult GEAR</c> (0.25): the sell fraction of weapons, armour and blueprints.
    /// </summary>
    public float LootPriceMultGear { get; init; }

    /// <summary>
    /// <c>loot price mult</c> (0.5): the sell fraction of maps and of every item that is not an unstolen <c>trade item</c>.
    /// </summary>
    public float LootPriceMult { get; init; }

    /// <summary>
    /// <c>loot price mult player armour</c> (0.25): the sell fraction of armour the player made.
    /// </summary>
    public float LootPriceMultPlayerArmour { get; init; }

    /// <summary>
    /// <c>loot price mult player weapons</c> (0.25): the same for weapons.
    /// </summary>
    public float LootPriceMultPlayerWeapons { get; init; }

    /// <summary>
    /// <c>blueprint price mult</c> (1.5): the <c>money</c> factor of a generated gear blueprint research
    /// (<see cref="GameDatabase.AddGearBlueprints"/>).
    /// </summary>
    public float BlueprintPriceMult { get; init; }

    /// <summary>
    /// The 0.3 the loader writes into the slot after the blueprint row itself (no FCS field): an armour price factor of <c>Armour::vfunc_81</c> whose
    /// condition is <b>Unknown</b>.
    /// </summary>
    public const float HardCodedArmourFactor = 0.3f;

    /// <summary>
    /// The record named <c>GLOBAL CONSTANTS</c>, else the first CONSTANTS record, else <see cref="BaseGame"/>.
    /// </summary>
    public static PriceConstants FromDatabase(GameDatabase db)
    {
        var record = ConstantsRecord.Find(db);
        return record is null ? BaseGame : From(record);
    }

    /// <summary>
    /// Reads a CONSTANTS record (null reads as an empty one) and applies the loader's pre-multiplication.
    /// </summary>
    public static PriceConstants From(GameRecord? c)
    {
        float F(string key, float fallback) => RecordReading.Float(c, key, fallback);

        float global = F("global price mult", 1);
        return new PriceConstants
        {
            GlobalPriceMult = global,
            ArmourPriceMult = F("armor price mult", 0.6f) * global,
            ClothingPriceMult = F("clothing price mult", 1) * global,
            SwordPriceMult = F("sword price mult", 0.4f) * global,
            TradePriceMult = F("trade price mult", 0.6f) * global,
            FoodPriceMult = F("food price mult", 0.5f) * global,
            CrossbowRecordMult = F("robotics price mult", 1) * global,
            LimbRecordMult = F("crossbow price mult", 1) * global,
            TradeProfitMargins = F("trade profit margins", 0.3f),
            LootPriceMultGear = F("loot price mult GEAR", 0.25f),
            LootPriceMult = F("loot price mult", 0.5f),
            LootPriceMultPlayerArmour = F("loot price mult player armour", 0.25f),
            LootPriceMultPlayerWeapons = F("loot price mult player weapons", 0.25f),
            BlueprintPriceMult = F("blueprint price mult", 1.5f),
        };
    }
}
