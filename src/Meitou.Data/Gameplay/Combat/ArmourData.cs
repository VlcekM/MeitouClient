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
    public int CombatAttackBonus { get; init; }
    public int CombatDefenceBonus { get; init; }
    public int CombatSkillBonus { get; init; }
    public float DodgeMult { get; init; } = 1;
    public float DexterityMult { get; init; } = 1;
    public float DamageOutputMult { get; init; } = 1;
    public float AthleticsMult { get; init; } = 1;
    public float CombatSpeedMult { get; init; } = 1;
    public float StealthMult { get; init; } = 1;
    public float WeightKg { get; init; }
    public IReadOnlyList<PartCoverage> Coverage { get; init; } = [];

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
        CombatAttackBonus = RecordReading.Int(r, "combat attk bonus", 0),
        CombatDefenceBonus = RecordReading.Int(r, "combat def bonus", 0),
        CombatSkillBonus = RecordReading.Int(r, "combat skill bonus", 0),
        DodgeMult = RecordReading.Float(r, "dodge mult", 1),
        DexterityMult = RecordReading.Float(r, "dexterity mult", 1),
        DamageOutputMult = RecordReading.Float(r, "damage output mult", 1),
        AthleticsMult = RecordReading.Float(r, "athletics mult", 1),
        CombatSpeedMult = RecordReading.Float(r, "combat speed mult", 1),
        StealthMult = RecordReading.Float(r, "stealth mult", 1),
        WeightKg = RecordReading.Float(r, "weight kg", 0),
        Coverage = [.. r.GetReferences("part coverage").Select(x => new PartCoverage(x.TargetStringId, x.Values.Value0))],
    };
}
