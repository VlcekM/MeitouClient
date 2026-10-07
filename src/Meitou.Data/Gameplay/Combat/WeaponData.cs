using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Data.Gameplay.Combat;

/// <summary><c>WeaponCategory</c> of a WEAPON's <c>skill category</c> (docs/game/combat.md "Enums", from the editor's decompile): which skill the weapon uses.</summary>
public enum WeaponCategory
{
    Katanas = 0,
    Sabres = 1,
    Blunt = 2,
    Heavy = 3,
    Hackers = 4,
    Unarmed = 5,
    Bow = 6,
    Turret = 7,
    Polearms = 8,
    // 9 to 20: creature attacks (elephant, dog, bull, robot spider, spider, cage beast, duck, gorilla, gar, frog, goat, giraffe).
    AttackNull = 21,
}

/// <summary>
/// A WEAPON record (docs/game/combat.md "Weapons"; field names <b>Verified</b> against the 48 base records). Damage multipliers against robots, humans and animals,
/// and per-race damage (percent), are copied to the wielder when the weapon is equipped.
/// </summary>
public sealed record WeaponData
{
    public required string StringId { get; init; }
    public string Name { get; init; } = "";
    /// <summary><c>skill category</c> as the raw number (a creature attack has a number beyond <see cref="WeaponCategory.Polearms"/>).</summary>
    public int SkillCategory { get; init; }
    public float CutDamageMultiplier { get; init; }
    public float BluntDamageMultiplier { get; init; }
    public float MinCutDamageMult { get; init; } = 1;
    public float PierceDamageMultiplier { get; init; }
    /// <summary><c>armour penetration</c>: negative on thin blades (a katana -0.3), positive on clubs and cleavers.</summary>
    public float ArmourPenetration { get; init; }
    public float BleedMult { get; init; } = 1;
    public int AttackMod { get; init; }
    public int DefenceMod { get; init; }
    public int IndoorsMod { get; init; }
    public int Length { get; init; }
    public float WeightKg { get; init; }
    public float WeightMult { get; init; } = 1;
    public bool CanBlock { get; init; }
    public float HumanDamageMult { get; init; } = 1;
    public float AnimalDamageMult { get; init; } = 1;
    public float RobotDamageMult { get; init; } = 1;
    /// <summary><c>race damage</c>: RACE string id to percent (the engine multiplies by 0.01).</summary>
    public IReadOnlyDictionary<string, int> RaceDamage { get; init; } = new Dictionary<string, int>();
    public int Value { get; init; }

    /// <summary>Whether the weapon is a fist rather than a held weapon.</summary>
    public bool IsUnarmed => SkillCategory == (int)WeaponCategory.Unarmed;

    public static WeaponData From(GameRecord r) => new()
    {
        StringId = r.StringId,
        Name = r.Name,
        SkillCategory = RecordReading.Int(r, "skill category", (int)WeaponCategory.Unarmed),
        CutDamageMultiplier = RecordReading.Float(r, "cut damage multiplier", 1),
        BluntDamageMultiplier = RecordReading.Float(r, "blunt damage multiplier", 0),
        MinCutDamageMult = RecordReading.Float(r, "min cut damage mult", 1),
        PierceDamageMultiplier = RecordReading.Float(r, "pierce damage multiplier", 0),
        ArmourPenetration = RecordReading.Float(r, "armour penetration", 0),
        BleedMult = RecordReading.Float(r, "bleed mult", 1),
        AttackMod = RecordReading.Int(r, "attack mod", 0),
        DefenceMod = RecordReading.Int(r, "defence mod", 0),
        IndoorsMod = RecordReading.Int(r, "indoors mod", 0),
        Length = RecordReading.Int(r, "length", 0),
        WeightKg = RecordReading.Float(r, "weight kg", 1),
        WeightMult = RecordReading.Float(r, "weight mult", 1),
        CanBlock = RecordReading.Bool(r, "can block", false),
        HumanDamageMult = RecordReading.Float(r, "human damage mult", 1),
        AnimalDamageMult = RecordReading.Float(r, "animal damage mult", 1),
        RobotDamageMult = RecordReading.Float(r, "robot damage mult", 1),
        RaceDamage = r.GetReferences("race damage").ToDictionary(x => x.TargetStringId, x => x.Values.Value0),
        Value = RecordReading.Int(r, "value", 0),
    };
}

/// <summary>One entry of a manufacturer's <c>weapon models</c>: a MATERIAL_SPECS_WEAPON record, the weapon level it gives (value 0) and its relative pick chance (value 1, 0 counting as 100).</summary>
public readonly record struct WeaponModelEntry(string ModelStringId, int Level, int Chance);

/// <summary>A WEAPON_MANUFACTURER record: multipliers of the cut and blunt damage, the minimum cut damage, weight and price, and the models it makes.</summary>
public sealed record WeaponManufacturer
{
    public required string StringId { get; init; }
    public string Name { get; init; } = "";
    public float CutDamageMod { get; init; } = 1;
    public float BluntDamageMod { get; init; } = 1;
    public int MinCutDamage { get; init; } = 1;
    public float WeightMod { get; init; } = 1;
    public float PriceMod { get; init; } = 1;
    public IReadOnlyList<WeaponModelEntry> Models { get; init; } = [];

    /// <summary>A manufacturer that changes nothing (all multipliers 1): the stand-in for tests and for a weapon without one.</summary>
    public static WeaponManufacturer Neutral { get; } = new() { StringId = "neutral", Name = "Neutral" };

    public static WeaponManufacturer From(GameRecord r) => new()
    {
        StringId = r.StringId,
        Name = r.Name,
        CutDamageMod = RecordReading.Float(r, "cut damage mod", 1),
        BluntDamageMod = RecordReading.Float(r, "blunt damage mod", 1),
        MinCutDamage = RecordReading.Int(r, "min cut damage", 1),
        WeightMod = RecordReading.Float(r, "weight mod", 1),
        PriceMod = RecordReading.Float(r, "price mod", 1),
        Models = [.. r.GetReferences("weapon models").Select(x => new WeaponModelEntry(x.TargetStringId, x.Values.Value0, x.Values.Value1))],
    };
}

/// <summary>A MATERIAL_SPECS_WEAPON record (a weapon "model", e.g. Mk V or Edge Type 1): added to the weapon's attack and defence modifiers.</summary>
public sealed record WeaponMaterial
{
    public required string StringId { get; init; }
    public string Name { get; init; } = "";
    public int AttackMod { get; init; }
    public int DefenceMod { get; init; }
    public static WeaponMaterial None { get; } = new() { StringId = "none", Name = "None" };

    public static WeaponMaterial From(GameRecord r) => new()
    {
        StringId = r.StringId,
        Name = r.Name,
        AttackMod = RecordReading.Int(r, "attack mod", 0),
        DefenceMod = RecordReading.Int(r, "defence mod", 0),
    };
}
