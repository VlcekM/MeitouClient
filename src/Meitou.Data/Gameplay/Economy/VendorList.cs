using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay.Economy;

/// <summary>
/// One thing a vendor list can give: an item record and its relative chance.
/// </summary>
public readonly record struct VendorEntry(string Id, FcsRecordType Type, int Weight);

/// <summary>
/// A VENDOR_LIST (docs/game/economy.md sections 9.2 and 13): the weighted items a shop, a container or a nest's loot is rolled from, and the
/// money roll.
/// <para>Each named reference list gives the records of one type (<see cref="From"/>), <c>weapon manufacturers</c> the
/// <see cref="Manufacturers"/>; the list builder <c>FUN_140956ba0</c> puts them all into one weighted table. The weight is the entry's v0,
/// "relative chance" in the editor; a v0 of 0 counts as 100 (<b>Verified</b> by disassembly of the builder, docs/game/economy.md "Building shops":
/// 472 entries are (0, 100, 0)).</para>
/// </summary>
public sealed record VendorList
{
    /// <summary>
    /// The entry lists in the list builder's order, each with the record type it takes; the gear blueprint lists name gear and give its
    /// blueprint (<c>FUN_140956ba0</c>, <b>Verified</b>, decompile).
    /// </summary>
    static readonly (string List, FcsRecordType Type, bool Gear)[] Branches =
    [
        ("weapons", FcsRecordType.Weapon, false), ("items", FcsRecordType.Item, false), ("armours", FcsRecordType.Armour, false),
        ("clothing", FcsRecordType.Armour, false), ("containers", FcsRecordType.Container, false), ("blueprints", FcsRecordType.Research, false),
        ("crossbows", FcsRecordType.Crossbow, false), ("robotics", FcsRecordType.LimbReplacement, false),
        ("armour blueprints", FcsRecordType.Research, true), ("crossbow blueprints", FcsRecordType.Research, true),
        ("backpack blueprints", FcsRecordType.Research, true), ("maps", FcsRecordType.MapItem, false),
    ];

    /// <summary>
    /// The record's string id (a VENDOR_LIST, or another record read as one).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The record's name in the active language.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// <c>items count</c>: the number of items a caller that asks for "the list's own count" takes (40 when below 0).
    /// </summary>
    public int ItemsCount { get; init; }

    /// <summary>
    /// <c>money item prob</c>: compared with a roll in [0, 1), so the data's percent-looking values of 1 and more always give money (section 13,
    /// <b>Verified</b>).
    /// </summary>
    public float MoneyItemProb { get; init; }

    /// <summary>
    /// <c>money item min</c> (default 0).
    /// </summary>
    public int MoneyItemMin { get; init; }

    /// <summary>
    /// <c>money item max</c> (default 0).
    /// </summary>
    public int MoneyItemMax { get; init; }

    /// <summary>
    /// The items, weapons, armour, crossbows, limbs, containers and maps of the entry lists, in the builder's order, and the blueprints: a
    /// <c>blueprints</c> entry is a RESEARCH (made into a blueprint item), an <c>armour blueprints</c> / <c>crossbow blueprints</c> / <c>backpack
    /// blueprints</c> entry the gear's <c>&lt;id&gt;.TECH.1</c> research (<b>Verified</b>, <c>FUN_140956ba0</c> -> <c>Research::getBlueprintsFor</c>;
    /// never the gear itself).
    /// </summary>
    public IReadOnlyList<VendorEntry> Entries { get; init; } = [];

    /// <summary>
    /// The WEAPON_MANUFACTURERs a weapon from the list may be made by (v0 the weight, 0 counting as 100).
    /// </summary>
    public IReadOnlyList<RecordLink> Manufacturers { get; init; } = [];

    /// <summary>
    /// The record types a vendor list hands out as items.
    /// </summary>
    public static bool IsItemType(FcsRecordType t) =>
        t is FcsRecordType.Item or FcsRecordType.Weapon or FcsRecordType.Armour or FcsRecordType.Crossbow
        or FcsRecordType.LimbReplacement or FcsRecordType.Container or FcsRecordType.MapItem;

    /// <summary>
    /// Builds the list's table as the list builder does (<c>FUN_140956ba0</c>, <b>Verified</b>, decompile): <c>weapon manufacturers</c> into
    /// <see cref="Manufacturers"/>, then each entry list in <see cref="Branches"/> order taking only records of its type; a WEAPON no listed
    /// manufacturer makes is dropped.
    /// </summary>
    public static VendorList From(GameDatabase db, GameRecord r)
    {
        var makers = r.GetReferences("weapon manufacturers")
            .Where(e => db.Find(e.TargetStringId) is { Type: FcsRecordType.WeaponManufacturer })
            .Select(RecordLink.From)
            .ToList();
        var entries = new List<VendorEntry>();
        foreach (var (list, type, gear) in Branches)
            foreach (var e in r.GetReferences(list))
                AddEntry(db, e, type, gear, entries);

        // A weapon is stocked only when it is a `weapon types` entry of one of the list's manufacturers (<b>Verified</b>, `FUN_140956ba0`: "No valid
        // manufacturer found for weapon" otherwise); no manufacturer or manufacturers without weapon types skip the weapons altogether.
        var makable = makers
            .SelectMany(m => db.Find(m.Id)!.GetReferences("weapon types"))
            .Select(t => t.TargetStringId)
            .ToHashSet();
        entries.RemoveAll(e => e.Type == FcsRecordType.Weapon && !makable.Contains(e.Id));

        int count = RecordReading.Int(r, "items count", 40);
        return new VendorList
        {
            Id = r.StringId,
            Name = r.Name,
            ItemsCount = count < 0 ? 40 : count,
            MoneyItemProb = RecordReading.Float(r, "money item prob", 0),
            MoneyItemMin = RecordReading.Int(r, "money item min", 0),
            MoneyItemMax = RecordReading.Int(r, "money item max", 0),
            Entries = entries,
            Manufacturers = makers,
        };
    }

    /// <summary>
    /// Adds one reference of an entry list when its record is of the list's <paramref name="type"/> (a gear blueprint list names the gear: the
    /// entry is its <c>&lt;id&gt;.TECH.1</c> research); a reference to a record the database lacks is skipped.
    /// </summary>
    static void AddEntry(GameDatabase db, GameReference e, FcsRecordType type, bool gear, List<VendorEntry> entries)
    {
        if (db.Find(gear ? e.TargetStringId + ".TECH.1" : e.TargetStringId) is not { } target || target.Type != type) return;

        entries.Add(new VendorEntry(target.StringId, target.Type, e.Values.Value0 == 0 ? 100 : e.Values.Value0));
    }
}
