using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay.Combat;

/// <summary>The combat records of a game database as typed views, read once (docs/game/combat.md implementation outline, step 1).</summary>
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

    readonly Dictionary<string, WeaponData> weaponsById = [];
    readonly Dictionary<string, WeaponManufacturer> manufacturersById = [];
    readonly Dictionary<string, WeaponMaterial> materialsById = [];
    readonly Dictionary<string, ArmourData> armoursById = [];

    public WeaponData? Weapon(string stringId) => weaponsById.GetValueOrDefault(stringId);
    public WeaponManufacturer? Manufacturer(string stringId) => manufacturersById.GetValueOrDefault(stringId);
    public WeaponMaterial? WeaponMaterial(string stringId) => materialsById.GetValueOrDefault(stringId);
    public ArmourData? Armour(string stringId) => armoursById.GetValueOrDefault(stringId);

    public static CombatDatabase From(GameDatabase db)
    {
        var weapons = db.OfType(FcsRecordType.WEAPON).Select(WeaponData.From).ToList();
        var manufacturers = db.OfType(FcsRecordType.WEAPON_MANUFACTURER).Select(WeaponManufacturer.From).ToList();
        var materials = db.OfType(FcsRecordType.MATERIAL_SPECS_WEAPON).Select(Combat.WeaponMaterial.From).ToList();
        var armours = db.OfType(FcsRecordType.ARMOUR).Select(ArmourData.From).ToList();
        var result = new CombatDatabase
        {
            Constants = CombatConstants.FromDatabase(db),
            Techniques = [.. db.OfType(FcsRecordType.COMBAT_TECHNIQUE).Select(CombatTechnique.From)],
            Weapons = weapons,
            Manufacturers = manufacturers,
            WeaponMaterials = materials,
            Armours = armours,
            Crossbows = [.. db.OfType(FcsRecordType.CROSSBOW).Select(CrossbowData.From)],
            Guns = [.. db.OfType(FcsRecordType.GUN_DATA).Select(GunData.From)],
        };
        foreach (var w in weapons) result.weaponsById[w.StringId] = w;
        foreach (var m in manufacturers) result.manufacturersById[m.StringId] = m;
        foreach (var m in materials) result.materialsById[m.StringId] = m;
        foreach (var a in armours) result.armoursById[a.StringId] = a;
        return result;
    }
}
