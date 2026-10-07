using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Combat;

namespace Meitou.Tests.Combat;

/// <summary>Shared inputs of the combat tests: base-game constants, a katana as dumped from the install, a club, some armour, and the techniques the duel uses.</summary>
static class CombatFixtures
{
    public static CombatConstants Constants { get; } = CombatConstants.BaseGame;

    /// <summary>WEAPON "Katana" (476-gamedata.base) as dumped from the base game: cut 1, blunt 0, penetration -0.3, bleed 1.2, human 1.1, robot 0.6, attack +4, defence -4, length 21, 2 kg.</summary>
    public static WeaponData Katana { get; } = new()
    {
        StringId = "476-gamedata.base", Name = "Katana", SkillCategory = (int)WeaponCategory.Katanas,
        CutDamageMultiplier = 1, BluntDamageMultiplier = 0, MinCutDamageMult = 1, PierceDamageMultiplier = 0, ArmourPenetration = -0.3f, BleedMult = 1.2f,
        AttackMod = 4, DefenceMod = -4, Length = 21, WeightKg = 2, WeightMult = 1, CanBlock = true, HumanDamageMult = 1.1f, AnimalDamageMult = 1, RobotDamageMult = 0.6f,
    };

    /// <summary>A club-like blunt weapon: cut 0.5, blunt 1.5, penetration +0.2, 3 kg.</summary>
    public static WeaponData Club { get; } = new()
    {
        StringId = "club-t", Name = "Club", SkillCategory = (int)WeaponCategory.Blunt,
        CutDamageMultiplier = 0.5f, BluntDamageMultiplier = 1.5f, MinCutDamageMult = 1, ArmourPenetration = 0.2f, BleedMult = 0.5f, WeightKg = 3, WeightMult = 1, CanBlock = true,
    };

    /// <summary>A creature weapon with pierce damage (like the bull's or the spider's).</summary>
    public static WeaponData Horn { get; } = new()
    {
        StringId = "horn-t", Name = "Horn", SkillCategory = 12, CutDamageMultiplier = 1, PierceDamageMultiplier = 1, BleedMult = 1, WeightKg = 1,
    };

    public static WeaponInstance KatanaAt(float quality, WeaponManufacturer? m = null) => WeaponInstance.Create(quality, Katana, m, null, Constants);

    static ArmourData Plate(string id, ArmourClass cls, ArmourType material, int slot, float cutIntoStun, params (string Part, int Percent)[] coverage) => new()
    {
        StringId = id, Name = id, Class = cls, Material = material, Slot = slot, CutIntoStun = cutIntoStun,
        Coverage = [.. coverage.Select(c => new PartCoverage(c.Part, c.Percent))],
    };

    /// <summary>Heavy metal plate on the body, hat and legs (the body covers chest, stomach and arms at 90).</summary>
    public static ArmourData BodyPlate { get; } = Plate("plate-body", ArmourClass.Heavy, ArmourType.MetalPlate, 1, 0.4f,
        ("101-t", 100), ("100-t", 100), ("28-t", 100), ("29-t", 100));
    public static ArmourData Helmet { get; } = Plate("plate-hat", ArmourClass.Medium, ArmourType.MetalPlate, 3, 0.5f, ("32-t", 100));
    public static ArmourData Rags { get; } = Plate("rags", ArmourClass.Cloth, ArmourType.Cloth, 8, 0, ("101-t", 90), ("100-t", 90));

    /// <summary>A COMBAT_TECHNIQUE written out: a katana cut of one blow.</summary>
    public static CombatTechnique Cut(string name = "cut", float chance = 1, float strike = 0.6f, float animSpeed = 1.2f, int direction = 2, int blows = 1, float strike2 = 0.8f) => new()
    {
        StringId = name, Name = name, AnimName = name, Kinds = WeaponKinds.Katana | WeaponKinds.OneHanded, Blows = blows, AttackDirection1 = direction, AttackDirection2 = direction,
        Chance = chance, AttackDistance = 10, AttackDistanceMinVsStatic = 10, AnimSpeedMult = animSpeed, BlockedFrame1 = strike, BlockedFrame2 = strike2, StopFrame1 = Math.Min(1, strike + 0.02f), StopFrame2 = 1,
        AcceptableEndTime = 0.95f,
    };

    public static CombatTechnique Block(string name, int direction, float chance = 1, bool dodge = false, float animSpeed = 1.2f) => new()
    {
        StringId = name, Name = name, AnimName = name, Kinds = WeaponKinds.All, IsBlock = true, IsDodge = dodge, AttackDirection1 = direction, Chance = chance, AnimSpeedMult = animSpeed,
        BlockedFrame1 = 0.8f, BlockedFrame2 = 0.8f, StopFrame1 = 1, StopFrame2 = 1, AcceptableEndTime = 0.95f, HesitatePoint = 0.2f,
    };

    public static CombatTechnique Dodge(string name, float chance = 1) => new()
    {
        StringId = name, Name = name, AnimName = name, Kinds = WeaponKinds.Unarmed | WeaponKinds.Katana, IsDodge = true, Chance = chance, AnimSpeedMult = 1.1f,
        BlockedFrame1 = 0.8f, BlockedFrame2 = 0.8f, StopFrame1 = 1, StopFrame2 = 1, AcceptableEndTime = 0.95f,
    };

    /// <summary>The techniques of a katana duel: two cuts (one a two-blow combo), blocks of the four front directions and a dodge.</summary>
    public static CombatTechnique[] KatanaTechniques() =>
    [
        Cut("cut left", 1, 0.6f, 1.5f, 2), Cut("combo", 1, 0.5f, 1.2f, 1, 2, 0.79f),
        Block("block up", 1), Block("block left", 2), Block("block right", 3), Block("block thrust", 4), Dodge("dodge back"),
    ];
}
