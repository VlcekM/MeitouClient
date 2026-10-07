using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;

namespace Meitou.Simulation.Combat;

/// <summary>
/// The numbers of one weapon item, computed once from its quality, the WEAPON record, the WEAPON_MANUFACTURER and the MATERIAL_SPECS_WEAPON model
/// (<c>FUN_140889cd0</c>, docs/game/combat.md "Stats of a weapon item", <b>Observed</b>).
/// </summary>
/// <param name="Quality">The item's quality fraction q in [0, 1] (the weapon level x 0.01: a manufacturer's model entry gives the level).</param>
/// <param name="CutMultiplier">lerp(q, 0.3, 2) x the record's cut damage multiplier x the manufacturer's cut damage mod.</param>
/// <param name="BluntMultiplier">lerp(q, 0, 2) x the record's blunt damage multiplier x the manufacturer's blunt damage mod.</param>
/// <param name="MinCut">The record's min cut damage mult x the manufacturer's min cut damage.</param>
/// <param name="Weight">The larger of weight kg x the manufacturer's weight mod and 40 x blunt multiplier x weight mult x the weapon inventory weight multiplier.</param>
/// <param name="AttackMod">The record's attack mod plus the model's.</param>
/// <param name="DefenceMod">The record's defence mod plus the model's.</param>
public readonly record struct WeaponStats(float Quality, float CutMultiplier, float BluntMultiplier, float MinCut, float Weight, int AttackMod, int DefenceMod, int IndoorsMod)
{
    public static WeaponStats Compute(float quality, WeaponData weapon, WeaponManufacturer manufacturer, WeaponMaterial material, CombatConstants constants)
    {
        float q = Math.Clamp(quality, 0, 1);
        float cut = Lerp(q, 0.3f, 2f) * weapon.CutDamageMultiplier * manufacturer.CutDamageMod;
        float blunt = Lerp(q, 0f, 2f) * weapon.BluntDamageMultiplier * manufacturer.BluntDamageMod;
        float minCut = weapon.MinCutDamageMult * manufacturer.MinCutDamage;
        float weight = MathF.Max(weapon.WeightKg * manufacturer.WeightMod, 40 * blunt * weapon.WeightMult * constants.WeaponInventoryWeightMult);
        return new WeaponStats(q, cut, blunt, minCut, weight, weapon.AttackMod + material.AttackMod, weapon.DefenceMod + material.DefenceMod, weapon.IndoorsMod);
    }

    /// <summary>The game's <c>lerp(t, a, b)</c> (<c>140015b63</c>): the parameter first.</summary>
    public static float Lerp(float t, float a, float b) => a + (b - a) * t;
}

/// <summary>A weapon in a character's hand: the record, its computed numbers, and the skill and animation kind it uses.</summary>
public sealed class WeaponInstance(WeaponData data, WeaponStats stats)
{
    public WeaponData Data { get; } = data;
    public WeaponStats Stats { get; } = stats;

    /// <summary>The skill the weapon's category uses (<c>FUN_140897f30</c>, <b>Observed</b>): katanas, sabres, hackers, heavy weapons, blunt, polearms; martial arts for fists and creature attacks.</summary>
    public StatsEnumerated Skill { get; } = SkillOf(data.SkillCategory);

    /// <summary>The weapon kind the animations and techniques are matched with.</summary>
    public WeaponKinds Kind { get; } = KindOf(data.SkillCategory);

    public bool CanBlock => Data.CanBlock;

    public static WeaponInstance Create(float quality, WeaponData data, WeaponManufacturer? manufacturer, WeaponMaterial? material, CombatConstants constants) =>
        new(data, WeaponStats.Compute(quality, data, manufacturer ?? WeaponManufacturer.Neutral, material ?? WeaponMaterial.None, constants));

    public static StatsEnumerated SkillOf(int category) => category switch
    {
        (int)WeaponCategory.Katanas => StatsEnumerated.Katanas,
        (int)WeaponCategory.Sabres => StatsEnumerated.Sabres,
        (int)WeaponCategory.Blunt => StatsEnumerated.Blunt,
        (int)WeaponCategory.Heavy => StatsEnumerated.HeavyWeapons,
        (int)WeaponCategory.Hackers => StatsEnumerated.Hackers,
        (int)WeaponCategory.Polearms => StatsEnumerated.Polearms,
        (int)WeaponCategory.Bow => StatsEnumerated.Crossbows,
        (int)WeaponCategory.Turret => StatsEnumerated.Turrets,
        _ => StatsEnumerated.MartialArts,
    };

    /// <summary>The weapon type bit of a category, what a technique's validity flags are matched with (a technique's "1 handed" flag is not tested: how it splits one- and two-handed weapons is <b>Unknown</b>).</summary>
    public static WeaponKinds KindOf(int category) => category switch
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
