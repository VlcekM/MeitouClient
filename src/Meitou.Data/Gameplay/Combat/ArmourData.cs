using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Data.Gameplay.Combat;

/// <summary><c>ArmourClass</c> (docs/game/combat.md "Enums"): the weight class of a piece.</summary>
public enum ArmourClass { Cloth = 0, Light = 1, Medium = 2, Heavy = 3 }

/// <summary><c>ArmourType</c>: the material row of the armour tables (an ARMOUR's <c>material type</c>).</summary>
public enum ArmourType { Cloth = 0, Leather = 1, Chain = 2, MetalPlate = 3 }

/// <summary>One <c>part coverage</c> entry: the LOCATIONAL_DAMAGE record and the percentage of blows to that part the piece covers.</summary>
public readonly record struct PartCoverage(string PartStringId, int Percent);

/// <summary>
/// An ARMOUR record (also clothing: every worn item is one) as the hit code reads it (docs/game/combat.md "Armour"). Field names <b>Verified</b> against the 142 base records.
/// </summary>
public sealed record ArmourData
{
    public required string StringId { get; init; }
    public string Name { get; init; } = "";
    public ArmourClass Class { get; init; }
    public ArmourType Material { get; init; }
    /// <summary><c>level bonus</c>: added x0.01 to the quality fraction.</summary>
    public int LevelBonus { get; init; }
    /// <summary><c>slot</c>: 3 hat, 6 legs, 8 shirt, 9 boots (inferred from which records use each number).</summary>
    public int Slot { get; init; }
    public float CutDefBonus { get; init; }
    public float BluntDefBonus { get; init; }
    public float PierceDefMult { get; init; } = 1;
    public float CutIntoStun { get; init; }
    public float FistInjuryMult { get; init; } = 1;
    public int UnarmedBonus { get; init; }
    /// <summary><c>perception bonus</c>: summed over the worn pieces into CharStats <c>+0x30</c> and added to the trained perception (docs/game/character-stats.md "Gear factors").</summary>
    public int PerceptionBonus { get; init; }
    public int CombatAttackBonus { get; init; }
    public int CombatDefenceBonus { get; init; }
    public int CombatSkillBonus { get; init; }
    public float DodgeMult { get; init; } = 1;
    public float DexterityMult { get; init; } = 1;
    public float DamageOutputMult { get; init; } = 1;
    public float AthleticsMult { get; init; } = 1;
    public float CombatSpeedMult { get; init; } = 1;
    public float StealthMult { get; init; } = 1;
    /// <summary><c>ranged skill mult</c>: the crossbow and precision-shooting effect shown on the tooltip.</summary>
    public float RangedSkillMult { get; init; } = 1;
    /// <summary><c>asassination mult</c> (the field's spelling in the data).</summary>
    public float AssassinationMult { get; init; } = 1;
    public float WeightKg { get; init; }
    public IReadOnlyList<PartCoverage> Coverage { get; init; } = [];
    /// <summary><c>weather protection amount</c> (0..1): what the piece adds to its wearer's protection from each of <see cref="WeatherProtection"/> (docs/formats/weather.md "Protection").</summary>
    public float WeatherProtectionAmount { get; init; }
    /// <summary><c>weather protection0</c>, <c>1</c>...: the <see cref="World.WeatherAffecting"/> kinds the piece protects from, without None and repeats (the loader's set).</summary>
    public IReadOnlyList<World.WeatherAffecting> WeatherProtection { get; init; } = [];

    /// <summary>The Chest part of the base data (the coverage entry that counts one and a half times in the weight rule).</summary>
    public const string ChestPartId = "101-gamedata.quack";

    /// <summary>
    /// The weight of the piece at <paramref name="level"/> (<b>Observed</b>, docs/game/ui-screens.md "Item tooltip"): when the level is above 0 and the record lists part coverage, the sum of the coverage
    /// percentages (the Chest entry x1.5, truncated; 100 when the sum is 0) x 0.05 x the material's factor (cloth 0.1, leather 0.25, chain 0.9, metal plate 1), x1.25 for a hat (slot 3), x0.5 for
    /// slot 8, at least 1; null otherwise, and the record's <c>weight kg</c> stands.
    /// </summary>
    public float? WeightAt(int level)
    {
        if (level <= 0 || Coverage.Count == 0) return null;
        int total = 0;
        foreach (var c in Coverage) total += c.PartStringId == ChestPartId ? (int)(c.Percent * 1.5f) : c.Percent;
        if (total == 0) total = 100;
        float weight = total * 0.05f * Material switch { ArmourType.Cloth => 0.1f, ArmourType.Leather => 0.25f, ArmourType.Chain => 0.9f, _ => 1f };
        if (Slot == 3) weight *= 1.25f;
        else if (Slot == 8) weight *= 0.5f;
        return MathF.Max(weight, 1);
    }

    /// <summary>Whether the piece protects from <paramref name="kind"/>.</summary>
    public bool Protects(World.WeatherAffecting kind) => WeatherProtection.Contains(kind);

    /// <summary>
    /// The non-zero values of a numbered list field (<c>name0</c>, <c>name1</c>...) as the loaders read it (<c>FUN_140898e90</c>, <c>FUN_14042efa0</c>, <b>Verified</b>):
    /// up to the first missing key, a 0 skipped, each value once.
    /// </summary>
    public static IReadOnlyList<World.WeatherAffecting> NumberedKinds(GameRecord r, string prefix)
    {
        var list = new List<World.WeatherAffecting>();
        for (int i = 0; r.Ints.TryGetValue(prefix + i, out int n); i++)
            if (n != 0 && !list.Contains((World.WeatherAffecting)n)) list.Add((World.WeatherAffecting)n);
        return list;
    }

    /// <summary>The coverage percentage of a part (0 when the piece does not list it: a missing entry never covers).</summary>
    public int CoverageOf(string partStringId)
    {
        foreach (var c in Coverage) if (c.PartStringId == partStringId) return c.Percent;
        return 0;
    }

    public static ArmourData From(GameRecord r) => new()
    {
        StringId = r.StringId,
        Name = r.Name,
        Class = (ArmourClass)Math.Clamp(RecordReading.Int(r, "class", 0), 0, 3),
        Material = (ArmourType)Math.Clamp(RecordReading.Int(r, "material type", 0), 0, 3),
        LevelBonus = RecordReading.Int(r, "level bonus", 0),
        Slot = RecordReading.Int(r, "slot", 0),
        CutDefBonus = RecordReading.Float(r, "cut def bonus", 0),
        BluntDefBonus = RecordReading.Float(r, "blunt def bonus", 0),
        PierceDefMult = RecordReading.Float(r, "pierce def mult", 1),
        CutIntoStun = RecordReading.Float(r, "cut into stun", 0),
        FistInjuryMult = RecordReading.Float(r, "fist injury mult", 1),
        UnarmedBonus = RecordReading.Int(r, "unarmed bonus", 0),
        PerceptionBonus = RecordReading.Int(r, "perception bonus", 0),
        CombatAttackBonus = RecordReading.Int(r, "combat attk bonus", 0),
        CombatDefenceBonus = RecordReading.Int(r, "combat def bonus", 0),
        CombatSkillBonus = RecordReading.Int(r, "combat skill bonus", 0),
        DodgeMult = RecordReading.Float(r, "dodge mult", 1),
        DexterityMult = RecordReading.Float(r, "dexterity mult", 1),
        DamageOutputMult = RecordReading.Float(r, "damage output mult", 1),
        AthleticsMult = RecordReading.Float(r, "athletics mult", 1),
        CombatSpeedMult = RecordReading.Float(r, "combat speed mult", 1),
        StealthMult = RecordReading.Float(r, "stealth mult", 1),
        RangedSkillMult = RecordReading.Float(r, "ranged skill mult", 1),
        AssassinationMult = RecordReading.Float(r, "asassination mult", 1),
        WeightKg = RecordReading.Float(r, "weight kg", 0),
        Coverage = [.. r.GetReferences("part coverage").Select(x => new PartCoverage(x.TargetStringId, x.Values.Value0))],
        WeatherProtectionAmount = RecordReading.Float(r, "weather protection amount", 0),
        WeatherProtection = NumberedKinds(r, "weather protection"),
    };
}