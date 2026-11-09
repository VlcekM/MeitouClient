using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay.Combat;

/// <summary>
/// The combat records of a game database as typed views, read once (docs/game/combat.md implementation outline, step 1).
/// </summary>
public sealed class CombatDatabase
{
    /// <summary>
    /// The combat values of the CONSTANTS record.
    /// </summary>
    public required CombatConstants Constants { get; init; }

    /// <summary>
    /// Every COMBAT_TECHNIQUE, in no defined order.
    /// </summary>
    public required IReadOnlyList<CombatTechnique> Techniques { get; init; }

    /// <summary>
    /// Every WEAPON, in no defined order.
    /// </summary>
    public required IReadOnlyList<WeaponData> Weapons { get; init; }

    /// <summary>
    /// Every WEAPON_MANUFACTURER, in no defined order.
    /// </summary>
    public required IReadOnlyList<WeaponManufacturer> Manufacturers { get; init; }

    /// <summary>
    /// Every MATERIAL_SPECS_WEAPON (weapon model), in no defined order.
    /// </summary>
    public required IReadOnlyList<WeaponMaterial> WeaponMaterials { get; init; }

    /// <summary>
    /// Every ARMOUR, in no defined order.
    /// </summary>
    public required IReadOnlyList<ArmourData> Armours { get; init; }

    /// <summary>
    /// Every CROSSBOW, in no defined order.
    /// </summary>
    public required IReadOnlyList<CrossbowData> Crossbows { get; init; }

    /// <summary>
    /// Every GUN_DATA, in no defined order.
    /// </summary>
    public required IReadOnlyList<GunData> Guns { get; init; }

    /// <summary>
    /// <see cref="Weapons"/> by string id.
    /// </summary>
    readonly Dictionary<string, WeaponData> _weaponsById = [];

    /// <summary>
    /// <see cref="Manufacturers"/> by string id.
    /// </summary>
    readonly Dictionary<string, WeaponManufacturer> _manufacturersById = [];

    /// <summary>
    /// <see cref="WeaponMaterials"/> by string id.
    /// </summary>
    readonly Dictionary<string, WeaponMaterial> _materialsById = [];

    /// <summary>
    /// <see cref="Armours"/> by string id.
    /// </summary>
    readonly Dictionary<string, ArmourData> _armoursById = [];

    /// <summary>
    /// <see cref="Crossbows"/> by string id.
    /// </summary>
    readonly Dictionary<string, CrossbowData> _crossbowsById = [];

    /// <summary>
    /// The WEAPON <paramref name="stringId"/>, or null when it is not one.
    /// </summary>
    public WeaponData? Weapon(string stringId) => _weaponsById.GetValueOrDefault(stringId);

    /// <summary>
    /// The WEAPON_MANUFACTURER <paramref name="stringId"/>, or null when it is not one.
    /// </summary>
    public WeaponManufacturer? Manufacturer(string stringId) => _manufacturersById.GetValueOrDefault(stringId);

    /// <summary>
    /// The MATERIAL_SPECS_WEAPON <paramref name="stringId"/>, or null when it is not one.
    /// </summary>
    public WeaponMaterial? WeaponMaterial(string stringId) => _materialsById.GetValueOrDefault(stringId);

    /// <summary>
    /// The ARMOUR <paramref name="stringId"/>, or null when it is not one.
    /// </summary>
    public ArmourData? Armour(string stringId) => _armoursById.GetValueOrDefault(stringId);

    /// <summary>
    /// The CROSSBOW <paramref name="stringId"/>, or null when it is not one.
    /// </summary>
    public CrossbowData? Crossbow(string stringId) => _crossbowsById.GetValueOrDefault(stringId);

    /// <summary>
    /// Reads every combat record of <paramref name="db"/> once and indexes the item kinds by string id.
    /// </summary>
    public static CombatDatabase From(GameDatabase db)
    {
        var weapons = db.OfType(FcsRecordType.Weapon).Select(WeaponData.From).ToList();
        var manufacturers = db.OfType(FcsRecordType.WeaponManufacturer).Select(WeaponManufacturer.From).ToList();
        var materials = db.OfType(FcsRecordType.MaterialSpecsWeapon).Select(Combat.WeaponMaterial.From).ToList();
        var armours = db.OfType(FcsRecordType.Armour).Select(ArmourData.From).ToList();

        var result = new CombatDatabase
        {
            Constants = CombatConstants.FromDatabase(db),
            Techniques = [.. db.OfType(FcsRecordType.CombatTechnique).Select(CombatTechnique.From)],
            Weapons = weapons,
            Manufacturers = manufacturers,
            WeaponMaterials = materials,
            Armours = armours,
            Crossbows = [.. db.OfType(FcsRecordType.Crossbow).Select(CrossbowData.From)],
            Guns = [.. db.OfType(FcsRecordType.GunData).Select(GunData.From)],
        };

        foreach (var w in weapons) result._weaponsById[w.StringId] = w;
        foreach (var m in manufacturers) result._manufacturersById[m.StringId] = m;
        foreach (var m in materials) result._materialsById[m.StringId] = m;
        foreach (var a in armours) result._armoursById[a.StringId] = a;
        foreach (var x in result.Crossbows) result._crossbowsById[x.StringId] = x;
        return result;
    }
}
