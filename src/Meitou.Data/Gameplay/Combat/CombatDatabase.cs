using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay.Combat;

/// <summary>
/// The combat records of a game database as typed views, read once (docs/game/combat.md implementation outline, step 1).
/// </summary>
public sealed class CombatDatabase
{
    public required CombatConstants Constants { get; init; }
    public required IReadOnlyList<CombatTechnique> Techniques { get; init; }
    public required IReadOnlyList<WeaponData> Weapons { get; init; }
    public required IReadOnlyList<WeaponManufacturer> Manufacturers { get; init; }
    public required IReadOnlyList<WeaponMaterial> WeaponMaterials { get; init; }
    public required IReadOnlyList<ArmourData> Armours { get; init; }
    public required IReadOnlyList<CrossbowData> Crossbows { get; init; }
    public required IReadOnlyList<GunData> Guns { get; init; }

    readonly Dictionary<string, WeaponData> _weaponsById = [];
    readonly Dictionary<string, WeaponManufacturer> _manufacturersById = [];
    readonly Dictionary<string, WeaponMaterial> _materialsById = [];
    readonly Dictionary<string, ArmourData> _armoursById = [];
    readonly Dictionary<string, CrossbowData> _crossbowsById = [];

    public WeaponData? Weapon(string stringId) => _weaponsById.GetValueOrDefault(stringId);

    public WeaponManufacturer? Manufacturer(string stringId) => _manufacturersById.GetValueOrDefault(stringId);

    public WeaponMaterial? WeaponMaterial(string stringId) => _materialsById.GetValueOrDefault(stringId);

    public ArmourData? Armour(string stringId) => _armoursById.GetValueOrDefault(stringId);

    public CrossbowData? Crossbow(string stringId) => _crossbowsById.GetValueOrDefault(stringId);

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
