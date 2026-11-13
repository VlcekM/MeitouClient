using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation;
using Meitou.Simulation.Combat;

namespace Meitou.Game.DevTools;

/// <summary>
/// One item record the debug menu's item picker offers: its string id, name, record type, category (by record type) and sub-category (by the
/// record's own data: a weapon's skill category, an armour's slot, an item's function, a limb's slot; empty for none).
/// </summary>
sealed record DebugItemEntry(string Id, string Name, FcsRecordType Type, string Group, string Sub)
{
    /// <summary>
    /// Whether the item takes a quality (<see cref="DebugGiveItem.QualityTypes"/>).
    /// </summary>
    public bool HasQuality => DebugGiveItem.QualityTypes.Contains(Type);
}

/// <summary>
/// One quality the picker offers for an item: its label and the <see cref="DebugPlaceItem.Quality"/> (0 to 100) that gives it; a weapon model
/// also names its maker and model (<see cref="DebugPlaceItem.Exact"/>), which the quality alone cannot say.
/// </summary>
sealed record DebugItemQuality(string Label, int Quality, DebugWeaponGrade? Exact = null);

/// <summary>
/// The item records of the game data for the debug menu's item picker (docs/game/ui-input.md "Debug menu"), grouped by record type and then by the
/// record's own category field (docs/formats/fcs-mod.md "Item categories"), and the qualities each kind of item can be placed at.
/// </summary>
sealed class DebugItemCatalog
{
    #region Categories

    /// <summary>
    /// The categories in the order shown, by the record types they hold.
    /// </summary>
    static readonly (string Group, FcsRecordType Type)[] Groups =
    [
        ("Weapons", FcsRecordType.Weapon), ("Crossbows", FcsRecordType.Crossbow), ("Armour", FcsRecordType.Armour),
        ("Items", FcsRecordType.Item), ("Containers", FcsRecordType.Container), ("Limb replacements", FcsRecordType.LimbReplacement),
        ("Blueprints", FcsRecordType.Blueprint), ("Maps", FcsRecordType.MapItem),
    ];

    /// <summary>
    /// The item function names (<c>ItemFunction</c> of the FCS editor), by value; one that is not listed is "Other".
    /// </summary>
    static readonly string[] FunctionNames =
    [
        "Other", "First aid", "Splint kits", "Food", "Containers", "Weapons", "Clothing", "Other", "Narcotics", "Tools", "Other", "Blueprints",
        "Robot repair", "Books", "Money", "Raw food", "Ammunition", "Severed limbs",
    ];

    /// <summary>
    /// The weapon skill category names (<c>WeaponCategory</c> of the FCS editor), by value 0 to 8; 9 to 20 are creature attacks.
    /// </summary>
    static readonly string[] WeaponCategoryNames =
        ["Katanas", "Sabres", "Blunt", "Heavy weapons", "Hackers", "Unarmed", "Bows", "Turrets", "Polearms"];

    /// <summary>
    /// The limb replacement slot names, from <c>LimbSlot</c> 50.
    /// </summary>
    static readonly string[] LimbNames = ["Left arm", "Right arm", "Left leg", "Right leg"];

    /// <summary>
    /// The sub-category of a record: empty for the types that have none.
    /// </summary>
    static string SubOf(GameRecord r) => r.Type switch
    {
        FcsRecordType.Weapon => WeaponCategoryName(r.GetInt("skill category")),
        FcsRecordType.Armour => ArmourSlotName(r.GetInt("slot", (int)AttachSlot.Body)),
        FcsRecordType.Item => FunctionName(r.GetInt("item function")),
        FcsRecordType.LimbReplacement => LimbName(r.GetInt("slot")),
        _ => "",
    };

    /// <summary>
    /// A weapon skill category's name.
    /// </summary>
    static string WeaponCategoryName(int category) => category switch
    {
        >= 0 and < 9 => WeaponCategoryNames[category],
        >= 9 and < 21 => "Creature attacks",
        _ => "Other",
    };

    /// <summary>
    /// An armour slot's name ("Other" for none).
    /// </summary>
    static string ArmourSlotName(int slot) => (AttachSlot)slot switch
    {
        AttachSlot.None => "Other",
        var named when Enum.IsDefined(named) => named.ToString(),
        _ => "Other",
    };

    /// <summary>
    /// An item function's name.
    /// </summary>
    static string FunctionName(int function) => function >= 0 && function < FunctionNames.Length ? FunctionNames[function] : "Other";

    /// <summary>
    /// A limb replacement slot's name.
    /// </summary>
    static string LimbName(int slot) => slot is >= 50 and < 54 ? LimbNames[slot - 50] : "Other";

    #endregion

    #region Entries

    /// <summary>
    /// The game data the entries were made from; the entries are made again for another database.
    /// </summary>
    GameDatabase? _database;

    /// <summary>
    /// Every item record with a name, by name then id.
    /// </summary>
    List<DebugItemEntry> _entries = [];

    /// <summary>
    /// The entries by string id.
    /// </summary>
    Dictionary<string, DebugItemEntry> _byId = [];

    /// <summary>
    /// The item entries of <paramref name="db"/>, sorted by name; made once per database.
    /// </summary>
    public IReadOnlyList<DebugItemEntry> Entries(GameDatabase db)
    {
        if (ReferenceEquals(_database, db)) return _entries;

        _database = db;
        _entries = [.. db.Records.Values
            .Where(r => DebugGiveItem.ItemTypes.Contains(r.Type) && r.Name.Length > 0)
            .Select(r => new DebugItemEntry(r.StringId, r.Name, r.Type, GroupOf(r.Type), SubOf(r)))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id, StringComparer.Ordinal)];
        _byId = _entries.ToDictionary(e => e.Id);
        return _entries;
    }

    /// <summary>
    /// The entry of a string id; null when there is none.
    /// </summary>
    public DebugItemEntry? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// The category names in the order shown.
    /// </summary>
    public static IEnumerable<string> GroupNames => Groups.Select(g => g.Group);

    /// <summary>
    /// The category name of a record type.
    /// </summary>
    static string GroupOf(FcsRecordType type) => Groups.First(g => g.Type == type).Group;

    #endregion

    #region Qualities

    /// <summary>
    /// The qualities of a crafted piece, by grade: the grade names of <see cref="ArmourTooltip.GradeNames"/>, at its grade's quality.
    /// </summary>
    static IEnumerable<DebugItemQuality> CraftGrades() =>
        ArmourTooltip.GradeNames.Select((name, i) => new DebugItemQuality(
            $"{name}  (quality {ArmourTooltip.GradeQualities[i]})", ArmourTooltip.GradeQualities[i]));

    /// <summary>
    /// The qualities an item can be placed at, lowest first: every weapon model of every maker for a weapon, the six grades for armour, crossbows
    /// and limbs; none for an item without a quality, or a weapon with no models in the data.
    /// </summary>
    public IReadOnlyList<DebugItemQuality> Qualities(DebugItemEntry entry, GameDatabase db)
    {
        if (!entry.HasQuality) return [];

        return entry.Type == FcsRecordType.Weapon ? [.. WeaponModels(db)] : [.. CraftGrades()];
    }

    /// <summary>
    /// The weapon models any WEAPON_MANUFACTURER lists (<c>weapon models</c>, value 0 the level), each once, with the first maker that lists it (the
    /// player's own first), by level then name. A model of level 0 (the creatures' own) is left out. Includes the models only other makers have, such
    /// as Rebirth's Meitou grade (level 100), which the player's maker lacks.
    /// </summary>
    static IEnumerable<DebugItemQuality> WeaponModels(GameDatabase db)
    {
        var models = new Dictionary<string, (string Maker, string Name, int Level)>();
        var makers = db.Records.Values.Where(r => r.Type == FcsRecordType.WeaponManufacturer).OrderBy(r => r.StringId != CraftBench.PlayerWeapons);
        foreach (var maker in makers)
        {
            foreach (var reference in maker.GetReferences("weapon models"))
            {
                if (reference.Values.Value0 < 1 || db.Find(reference.TargetStringId) is not { } model) continue;

                models.TryAdd(model.StringId, (maker.StringId, model.Name, reference.Values.Value0));
            }
        }

        return models
            .OrderBy(m => m.Value.Level)
            .ThenBy(m => m.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => new DebugItemQuality($"{m.Value.Name}  (level {m.Value.Level})", m.Value.Level,
                new DebugWeaponGrade(m.Value.Maker, m.Key, m.Value.Level)));
    }

    #endregion
}
