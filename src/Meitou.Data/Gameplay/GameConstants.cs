using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay;

/// <summary>
/// The <c>GLOBAL CONSTANTS</c> record as the game uses it at run time: the fields listed in docs/game/game-loop.md "Settings and
/// constants" and docs/game/character-stats.md "CONSTANTS used by this subsystem", with the rescalings the game's constants loader
/// (<c>FUN_14086b2b0</c>) applies when it copies them into its runtime block (<b>Verified</b> there, row by row). Property values are
/// the <i>stored</i> ones, the numbers the formulas use; the comment on each says the field and the scaling. A field the record does not
/// have takes the editor's default where character-stats.md lists one, else the base game's value (so <see cref="Default"/> is a
/// usable stand-in); the loader's own fixed defaults (docs/game/character-stats.md) are not modelled. Combat tuning that is not
/// a plain number here (the damage curves) stays in the record until the combat stage reads it.
/// </summary>
public sealed record GameConstants
{
    /// <summary>The CONSTANTS record without any field set: every property at its fallback.</summary>
    public static GameConstants Default { get; } = From(null);

    // Time and sun (game-loop.md, terrain.md). Sunset not after sunrise gives 6 and 20 (applied by the clock, not here).
    /// <summary><c>days per year</c>.</summary>
    public int DaysPerYear { get; init; }
    /// <summary><c>sunrise</c> (game hour).</summary>
    public float Sunrise { get; init; }
    /// <summary><c>sunset</c> (game hour).</summary>
    public float Sunset { get; init; }
    /// <summary><c>latitude</c> (degrees).</summary>
    public float Latitude { get; init; }
    /// <summary><c>night darkness</c>: exposure factor at night (formats/lighting.md).</summary>
    public float NightDarkness { get; init; }

    // Buildings, production, research (game-loop.md; the consumers are in buildings-production.md).
    /// <summary><c>build speed</c>: multiplier for overall construction rate.</summary>
    public float BuildSpeed { get; init; }
    /// <summary><c>production speed</c>.</summary>
    public float ProductionSpeed { get; init; }
    /// <summary><c>research rate</c>: multiplier for research TIMES, so below 1 is faster.</summary>
    public float ResearchRate { get; init; }
    /// <summary><c>research level increase rate</c>: research time growth per tech level.</summary>
    public float ResearchLevelIncreaseRate { get; init; }
    /// <summary><c>prison time</c>: multiplier for imprisonment time.</summary>
    public float PrisonTime { get; init; }
    /// <summary><c>min dismantle materials percentage</c>, stored x0.01.</summary>
    public float MinDismantleMaterialsFraction { get; init; }

    // Squad caps (factions-squads-towns.md section 9).
    public int MaxSquads { get; init; }
    public int MaxSquadSize { get; init; }
    public int MaxFactionSize { get; init; }

    // Hunger, blood, healing (character-stats.md).
    /// <summary><c>starvation time</c>: game hours per 100 hunger points.</summary>
    public float StarvationTime { get; init; }
    /// <summary><c>fed recovery rate mult</c>.</summary>
    public float FedRecoveryRateMult { get; init; }
    /// <summary><c>bed hunger rate</c>.</summary>
    public float BedHungerRate { get; init; }
    /// <summary><c>encumbrance hunger rate</c>.</summary>
    public float EncumbranceHungerRate { get; init; }
    /// <summary><c>food quality mult</c>: scales a food item's charges (stored as is).</summary>
    public float FoodQualityMult { get; init; }
    /// <summary><c>bleed rate</c>, stored x0.1.</summary>
    public float BleedRate { get; init; }
    /// <summary><c>immediate blood loss</c>.</summary>
    public float ImmediateBloodLoss { get; init; }
    /// <summary><c>bleeding clot rate</c>, stored x0.1.</summary>
    public float BleedingClotRate { get; init; }
    /// <summary><c>extra blood loss from bodyparts</c>.</summary>
    public float ExtraBloodLossFromBodyparts { get; init; }
    /// <summary><c>blood recovery rate</c>, stored x0.1.</summary>
    public float BloodRecoveryRate { get; init; }
    /// <summary><c>bodypart degeneration rate</c>.</summary>
    public float BodypartDegenerationRate { get; init; }
    /// <summary><c>degeneration mult 1</c> (at level 1; the value at level 99 is below).</summary>
    public float DegenerationMult1 { get; init; }
    /// <summary><c>degeneration mult 99</c>.</summary>
    public float DegenerationMult99 { get; init; }
    /// <summary><c>knockout mult 1</c>.</summary>
    public float KnockoutMult1 { get; init; }
    /// <summary><c>knockout mult 99</c>.</summary>
    public float KnockoutMult99 { get; init; }
    /// <summary><c>knockout time base</c>.</summary>
    public float KnockoutTimeBase { get; init; }
    /// <summary><c>min toughness ko point</c>.</summary>
    public float MinToughnessKoPoint { get; init; }
    /// <summary><c>max toughness ko point</c>.</summary>
    public float MaxToughnessKoPoint { get; init; }
    /// <summary><c>stun recovery rate</c>.</summary>
    public float StunRecoveryRate { get; init; }
    /// <summary><c>blunt permanent organ damage</c>.</summary>
    public float BluntPermanentOrganDamage { get; init; }
    /// <summary><c>heal rate mult</c>.</summary>
    public float HealRateMult { get; init; }
    /// <summary><c>resting heal rate mult</c>.</summary>
    public float RestingHealRateMult { get; init; }
    /// <summary><c>medic speed mult</c>.</summary>
    public float MedicSpeedMult { get; init; }
    /// <summary><c>medkit drain 1</c>, stored x0.1.</summary>
    public float MedkitDrain1 { get; init; }
    /// <summary><c>medkit drain 99</c>, stored x0.1.</summary>
    public float MedkitDrain99 { get; init; }
    /// <summary><c>robot medic speed mult</c>.</summary>
    public float RobotMedicSpeedMult { get; init; }
    /// <summary><c>robot wear rate</c>.</summary>
    public float RobotWearRate { get; init; }

    // Experience.
    /// <summary><c>exp gain multiplier</c>, stored x0.25.</summary>
    public float ExpGainMultiplier { get; init; }
    /// <summary><c>XP rate medic 1</c>.</summary>
    public float XpRateMedic1 { get; init; }
    /// <summary><c>XP rate medic 99</c>.</summary>
    public float XpRateMedic99 { get; init; }
    /// <summary><c>skill diff xp 2x bonus</c>.</summary>
    public float SkillDiffXp2xBonus { get; init; }
    /// <summary><c>skill diff xp 0x penalty</c>.</summary>
    public float SkillDiffXp0xPenalty { get; init; }
    /// <summary><c>min strength xp mult</c>.</summary>
    public float MinStrengthXpMult { get; init; }
    /// <summary><c>weight strength diff 1x</c>.</summary>
    public float WeightStrengthDiff1x { get; init; }
    /// <summary><c>weight strength diff max</c>.</summary>
    public float WeightStrengthDiffMax { get; init; }
    /// <summary><c>xp rate strength</c>.</summary>
    public float XpRateStrength { get; init; }
    /// <summary><c>xp rate strength from walking</c>.</summary>
    public float XpRateStrengthFromWalking { get; init; }
    /// <summary><c>xp rate athletics</c>, stored x0.5.</summary>
    public float XpRateAthletics { get; init; }
    /// <summary><c>xp rate toughness</c>.</summary>
    public float XpRateToughness { get; init; }

    // Carrying.
    /// <summary><c>encumbrance base</c>.</summary>
    public float EncumbranceBase { get; init; }
    /// <summary><c>carry weight mult</c>.</summary>
    public float CarryWeightMult { get; init; }
    /// <summary><c>carry person weight</c>.</summary>
    public float CarryPersonWeight { get; init; }
    /// <summary><c>weapon inventory weight mult</c>.</summary>
    public float WeaponInventoryWeightMult { get; init; }

    // The combat rows of CONSTANTS (damage multiplier, block chances, attack slots...) are read by CombatConstants, the one the combat code uses.
    /// <summary><c>minimum lockpick chance</c>, stored x0.01.</summary>
    public float MinimumLockpickChance { get; init; }

    // Presentation.
    /// <summary><c>animation blend rate</c>: speed of blending between animations.</summary>
    public float AnimationBlendRate { get; init; }
    /// <summary><c>appearance random deviation percentage</c>, stored x0.5.</summary>
    public float AppearanceRandomDeviation { get; init; }

    /// <summary>The record named <c>GLOBAL CONSTANTS</c>, else the first CONSTANTS record, else <see cref="Default"/>.</summary>
    public static GameConstants FromDatabase(GameDatabase db)
    {
        var record = ConstantsRecord.Find(db);
        return record is null ? Default : From(record);
    }

    /// <summary>Reads a CONSTANTS record (null reads as an empty one) and applies the loader's rescalings.</summary>
    public static GameConstants From(GameRecord? c)
    {
        // A few fields are stored as ints in the data (weight strength diff 1x...), the rest as floats: read either (RecordReading).
        float F(string key, float fallback, float scale = 1) => RecordReading.Float(c, key, fallback) * scale;
        int I(string key, int fallback) => RecordReading.Int(c, key, fallback);
        return new GameConstants
        {
            DaysPerYear = I("days per year", 100),
            Sunrise = F("sunrise", 5),
            Sunset = F("sunset", 23),
            Latitude = F("latitude", 54),
            NightDarkness = F("night darkness", 0.35f),

            BuildSpeed = F("build speed", 1),
            ProductionSpeed = F("production speed", 1),
            ResearchRate = F("research rate", 1),
            ResearchLevelIncreaseRate = F("research level increase rate", 1.3f),
            PrisonTime = F("prison time", 1),
            MinDismantleMaterialsFraction = F("min dismantle materials percentage", 60, 0.01f),

            MaxSquads = I("max squads", 10),
            MaxSquadSize = I("max squad size", 20),
            MaxFactionSize = I("max faction size", 30),

            StarvationTime = F("starvation time", 24),
            FedRecoveryRateMult = F("fed recovery rate mult", 4),
            BedHungerRate = F("bed hunger rate", 0.4f),
            EncumbranceHungerRate = F("encumbrance hunger rate", 0.7f),
            FoodQualityMult = F("food quality mult", 1),
            BleedRate = F("bleed rate", 0.01f, 0.1f),
            ImmediateBloodLoss = F("immediate blood loss", 0.2f),
            BleedingClotRate = F("bleeding clot rate", 0.0005f, 0.1f),
            ExtraBloodLossFromBodyparts = F("extra blood loss from bodyparts", 1),
            BloodRecoveryRate = F("blood recovery rate", 0.3f, 0.1f),
            BodypartDegenerationRate = F("bodypart degeneration rate", 1),
            DegenerationMult1 = F("degeneration mult 1", 3),
            DegenerationMult99 = F("degeneration mult 99", 0.5f),
            KnockoutMult1 = F("knockout mult 1", 3),
            KnockoutMult99 = F("knockout mult 99", 3),
            KnockoutTimeBase = F("knockout time base", 30),
            MinToughnessKoPoint = F("min toughness ko point", 10),
            MaxToughnessKoPoint = F("max toughness ko point", 99),
            StunRecoveryRate = F("stun recovery rate", 1),
            BluntPermanentOrganDamage = F("blunt permanent organ damage", 0),
            HealRateMult = F("heal rate mult", 1),
            RestingHealRateMult = F("resting heal rate mult", 1),
            MedicSpeedMult = F("medic speed mult", 1),
            MedkitDrain1 = F("medkit drain 1", 1, 0.1f),
            MedkitDrain99 = F("medkit drain 99", 0.05f, 0.1f),
            RobotMedicSpeedMult = F("robot medic speed mult", 1),
            RobotWearRate = F("robot wear rate", 1),

            ExpGainMultiplier = F("exp gain multiplier", 3, 0.25f),
            XpRateMedic1 = F("XP rate medic 1", 10),
            XpRateMedic99 = F("XP rate medic 99", 300),
            SkillDiffXp2xBonus = F("skill diff xp 2x bonus", 10),
            SkillDiffXp0xPenalty = F("skill diff xp 0x penalty", 20),
            MinStrengthXpMult = F("min strength xp mult", 0.05f),
            WeightStrengthDiff1x = F("weight strength diff 1x", 20),
            WeightStrengthDiffMax = F("weight strength diff max", 1.5f),
            XpRateStrength = F("xp rate strength", 1),
            XpRateStrengthFromWalking = F("xp rate strength from walking", 1),
            XpRateAthletics = F("xp rate athletics", 1, 0.5f),
            XpRateToughness = F("xp rate toughness", 1.33f),

            EncumbranceBase = F("encumbrance base", 10),
            CarryWeightMult = F("carry weight mult", 1.2f),
            CarryPersonWeight = F("carry person weight", 40),
            WeaponInventoryWeightMult = F("weapon inventory weight mult", 0.5f),

            MinimumLockpickChance = F("minimum lockpick chance", 5, 0.01f),

            AnimationBlendRate = F("animation blend rate", 4),
            AppearanceRandomDeviation = F("appearance random deviation percentage", 0.4f, 0.5f),
        };
    }
}
