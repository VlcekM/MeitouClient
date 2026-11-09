using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Data.Gameplay.Combat;

/// <summary>
/// <c>WeaponCategory</c> of a WEAPON's <c>skill category</c> (docs/game/combat.md "Enums", from the editor's decompile): which skill the weapon uses.
/// </summary>
public enum WeaponCategory
{
    /// <summary>
    /// 0: the katana skill.
    /// </summary>
    Katanas = 0,
    /// <summary>
    /// 1: the sabre skill.
    /// </summary>
    Sabres = 1,
    /// <summary>
    /// 2: the blunt skill.
    /// </summary>
    Blunt = 2,
    /// <summary>
    /// 3: heavy weapons.
    /// </summary>
    Heavy = 3,
    /// <summary>
    /// 4: the hacker skill.
    /// </summary>
    Hackers = 4,
    /// <summary>
    /// 5: unarmed (martial arts), the fists.
    /// </summary>
    Unarmed = 5,
    /// <summary>
    /// 6: bows; not a skill weapon (<see cref="WeaponCategories"/>).
    /// </summary>
    Bow = 6,
    /// <summary>
    /// 7: turrets; not a skill weapon (<see cref="WeaponCategories"/>).
    /// </summary>
    Turret = 7,
    /// <summary>
    /// 8: the polearm skill.
    /// </summary>
    Polearms = 8,

    // 9 to 20: creature attacks (elephant, dog, bull, robot spider, spider, cage beast, duck, gorilla, gar, frog, goat, giraffe).
    /// <summary>
    /// 21: no category; as <c>skill category animation override</c> (its default) it means no override.
    /// </summary>
    AttackNull = 21,
}

/// <summary>
/// Which <see cref="WeaponKinds"/> bit a weapon category stands for. There are two mappings, which agree on the six skill weapons (0 to 4 and 8) and
/// on unarmed (5) and differ for the rest (Bow, Turret and the creature attacks, 6, 7 and 9 and up): the animations treat those as a one-handed
/// weapon, the techniques as unarmed. Whether they should be one mapping is <b>Unknown</b> (it would change which clip a character with such a weapon
/// in hand plays); both are kept as they were.
/// </summary>
public static class WeaponCategories
{
    /// <summary>
    /// The kind the animation records are matched with (<see cref="HandHold.OfCategory"/>): a category that is not a skill weapon or unarmed counts
    /// as one-handed.
    /// </summary>
    public static WeaponKinds AnimationKind(int category) => category switch
    {
        (int)WeaponCategory.Katanas => WeaponKinds.Katana,
        (int)WeaponCategory.Sabres => WeaponKinds.Sabre,
        (int)WeaponCategory.Blunt => WeaponKinds.Blunt,
        (int)WeaponCategory.Heavy => WeaponKinds.Heavy,
        (int)WeaponCategory.Hackers => WeaponKinds.Hacker,
        (int)WeaponCategory.Polearms => WeaponKinds.Polearm,
        (int)WeaponCategory.Unarmed => WeaponKinds.Unarmed,
        _ => WeaponKinds.OneHanded,
    };

    /// <summary>
    /// The kind the techniques' validity flags are matched with: a category that is not a skill weapon counts as unarmed.
    /// </summary>
    public static WeaponKinds TechniqueKind(int category) => category switch
    {
        (int)WeaponCategory.Katanas => WeaponKinds.Katana,
        (int)WeaponCategory.Sabres => WeaponKinds.Sabre,
        (int)WeaponCategory.Blunt => WeaponKinds.Blunt,
        (int)WeaponCategory.Heavy => WeaponKinds.Heavy,
        (int)WeaponCategory.Hackers => WeaponKinds.Hacker,
        (int)WeaponCategory.Polearms => WeaponKinds.Polearm,
        _ => WeaponKinds.Unarmed,
    };
}

/// <summary>
/// A WEAPON record (docs/game/combat.md "Weapons"; field names <b>Verified</b> against the 48 base records). Damage multipliers against robots,
/// humans and animals, and per-race damage (percent), are copied to the wielder when the weapon is equipped.
/// </summary>
public sealed record WeaponData
{
    /// <summary>
    /// The WEAPON's string id.
    /// </summary>
    public required string StringId { get; init; }

    /// <summary>
    /// The WEAPON's name in the active language.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// <c>skill category</c> as the raw number (a creature attack has a number beyond <see cref="WeaponCategory.Polearms"/>).
    /// </summary>
    public int SkillCategory { get; init; }

    /// <summary>
    /// The category it animates as (<c>Sword::Sword</c> <c>+0x238</c>, <b>Verified</b>): <c>skill category animation override</c> unless that is 21
    /// (the default), else <see cref="SkillCategory"/>. 9 base weapons override (cleavers, jittes, heavy polearms, the Topper), so they take another
    /// class's clips and techniques while training their own skill.
    /// </summary>
    public int AnimationCategory { get; init; }

    /// <summary>
    /// <c>cut damage multiplier</c> (default 1).
    /// </summary>
    public float CutDamageMultiplier { get; init; }

    /// <summary>
    /// <c>blunt damage multiplier</c> (default 0).
    /// </summary>
    public float BluntDamageMultiplier { get; init; }

    /// <summary>
    /// <c>min cut damage mult</c> (default 1).
    /// </summary>
    public float MinCutDamageMult { get; init; } = 1;

    /// <summary>
    /// <c>pierce damage multiplier</c> (default 0).
    /// </summary>
    public float PierceDamageMultiplier { get; init; }

    /// <summary>
    /// <c>armour penetration</c>: negative on thin blades (a katana -0.3), positive on clubs and cleavers.
    /// </summary>
    public float ArmourPenetration { get; init; }

    /// <summary>
    /// <c>bleed mult</c> (default 1).
    /// </summary>
    public float BleedMult { get; init; } = 1;

    /// <summary>
    /// <c>attack mod</c> (default 0), with the model's added (<see cref="WeaponMaterial"/>).
    /// </summary>
    public int AttackMod { get; init; }

    /// <summary>
    /// <c>defence mod</c> (default 0), with the model's added (<see cref="WeaponMaterial"/>).
    /// </summary>
    public int DefenceMod { get; init; }

    /// <summary>
    /// <c>indoors mod</c> (default 0; docs/game/combat.md "Indoors").
    /// </summary>
    public int IndoorsMod { get; init; }

    /// <summary>
    /// <c>length</c> (default 0).
    /// </summary>
    public int Length { get; init; }

    /// <summary>
    /// <c>weight kg</c> (default 1).
    /// </summary>
    public float WeightKg { get; init; }

    /// <summary>
    /// <c>weight mult</c> (default 1).
    /// </summary>
    public float WeightMult { get; init; } = 1;

    /// <summary>
    /// <c>can block</c> (default false).
    /// </summary>
    public bool CanBlock { get; init; }

    /// <summary>
    /// <c>human damage mult</c> (default 1), copied to the wielder.
    /// </summary>
    public float HumanDamageMult { get; init; } = 1;

    /// <summary>
    /// <c>animal damage mult</c> (default 1), copied to the wielder.
    /// </summary>
    public float AnimalDamageMult { get; init; } = 1;

    /// <summary>
    /// <c>robot damage mult</c> (default 1), copied to the wielder.
    /// </summary>
    public float RobotDamageMult { get; init; } = 1;

    /// <summary>
    /// <c>race damage</c>: RACE string id to percent (the engine multiplies by 0.01).
    /// </summary>
    public IReadOnlyDictionary<string, int> RaceDamage { get; init; } = new Dictionary<string, int>();

    /// <summary>
    /// <c>value</c> (default 0).
    /// </summary>
    public int Value { get; init; }

    /// <summary>
    /// Whether the weapon is a fist rather than a held weapon.
    /// </summary>
    public bool IsUnarmed => SkillCategory == (int)WeaponCategory.Unarmed;

    /// <summary>
    /// Reads a WEAPON record; the defaults are those named on each property (<c>skill category</c> defaults to unarmed).
    /// </summary>
    public static WeaponData From(GameRecord r)
    {
        int animationOverride = RecordReading.Int(r, "skill category animation override", 21);
        return new()
        {
            StringId = r.StringId,
            Name = r.Name,
            SkillCategory = RecordReading.Int(r, "skill category", (int)WeaponCategory.Unarmed),
            AnimationCategory = animationOverride is not 21
                ? animationOverride
                : RecordReading.Int(r, "skill category", (int)WeaponCategory.Unarmed),
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
}

/// <summary>
/// One entry of a manufacturer's <c>weapon models</c>: a MATERIAL_SPECS_WEAPON record, the weapon level it gives (value 0) and its relative pick
/// chance (value 1, 0 counting as 100).
/// </summary>
public readonly record struct WeaponModelEntry(string ModelStringId, int Level, int Chance);

/// <summary>
/// A WEAPON_MANUFACTURER record: multipliers of the cut and blunt damage, the minimum cut damage, weight and price, and the models it makes.
/// </summary>
public sealed record WeaponManufacturer
{
    /// <summary>
    /// The WEAPON_MANUFACTURER's string id.
    /// </summary>
    public required string StringId { get; init; }

    /// <summary>
    /// The WEAPON_MANUFACTURER's name in the active language.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// <c>cut damage mod</c> (default 1).
    /// </summary>
    public float CutDamageMod { get; init; } = 1;

    /// <summary>
    /// <c>blunt damage mod</c> (default 1).
    /// </summary>
    public float BluntDamageMod { get; init; } = 1;

    /// <summary>
    /// <c>min cut damage</c> (default 1).
    /// </summary>
    public int MinCutDamage { get; init; } = 1;

    /// <summary>
    /// <c>weight mod</c> (default 1).
    /// </summary>
    public float WeightMod { get; init; } = 1;

    /// <summary>
    /// <c>price mod</c> (default 1).
    /// </summary>
    public float PriceMod { get; init; } = 1;

    /// <summary>
    /// <c>weapon models</c> in list order.
    /// </summary>
    public IReadOnlyList<WeaponModelEntry> Models { get; init; } = [];

    /// <summary>
    /// A manufacturer that changes nothing (all multipliers 1): the stand-in for tests and for a weapon without one.
    /// </summary>
    public static WeaponManufacturer Neutral { get; } = new() { StringId = "neutral", Name = "Neutral" };

    /// <summary>
    /// Reads a WEAPON_MANUFACTURER record; the defaults are those named on each property.
    /// </summary>
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

/// <summary>
/// A MATERIAL_SPECS_WEAPON record (a weapon "model", e.g. Mk V or Edge Type 1): added to the weapon's attack and defence modifiers.
/// </summary>
public sealed record WeaponMaterial
{
    /// <summary>
    /// The MATERIAL_SPECS_WEAPON's string id.
    /// </summary>
    public required string StringId { get; init; }

    /// <summary>
    /// The MATERIAL_SPECS_WEAPON's name in the active language.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// <c>attack mod</c> (default 0), added to the weapon's.
    /// </summary>
    public int AttackMod { get; init; }

    /// <summary>
    /// <c>defence mod</c> (default 0), added to the weapon's.
    /// </summary>
    public int DefenceMod { get; init; }

    /// <summary>
    /// A model that adds nothing: the stand-in for a weapon without one.
    /// </summary>
    public static WeaponMaterial None { get; } = new() { StringId = "none", Name = "None" };

    /// <summary>
    /// Reads a MATERIAL_SPECS_WEAPON record.
    /// </summary>
    public static WeaponMaterial From(GameRecord r) => new()
    {
        StringId = r.StringId,
        Name = r.Name,
        AttackMod = RecordReading.Int(r, "attack mod", 0),
        DefenceMod = RecordReading.Int(r, "defence mod", 0),
    };
}
