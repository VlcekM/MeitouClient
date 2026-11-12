using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay.Economy;

/// <summary>
/// One thing a vendor list can give: an item record and its relative chance.
/// </summary>
public readonly record struct VendorEntry(string Id, FcsRecordType Type, int Weight);

/// <summary>
/// A VENDOR_LIST (docs/game/economy.md sections 9.2 and 13): the weighted items a shop, a container or a nest's loot is rolled from, and the
/// money roll.
/// <para>The names of its reference lists are not written down in the docs (<c>items</c>, <c>weapons</c>, <c>clothing</c>, ...), so every list is
/// read and its entries kept by the type of the record they point at: item kinds become <see cref="Entries"/>, WEAPON_MANUFACTURERs
/// <see cref="Manufacturers"/>. The game merges a list's entries into one weighted table (the item-group picker of factions-squads-towns.md section
/// 8, <b>Observed</b>). The weight is the entry's v0, "relative chance" in the editor; a v0 of 0 counts as 100 as in the generic weighted pick (the
/// vendor lists' own reading of 0 is <b>Unknown</b>, docs/game/economy.md Unknowns: 472 entries are (0, 100, 0)).</para>
/// </summary>
public sealed record VendorList
{
    /// <summary>
    /// The lists whose entries name gear and give its blueprint.
    /// </summary>
    static readonly string[] GearBlueprintLists = ["armour blueprints", "crossbow blueprints", "backpack blueprints"];

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
    /// The items, weapons, armour, crossbows, limbs, containers and maps of every list, in the record's order, and the blueprints: a
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
    /// Reads every reference list of <paramref name="r"/>: item kinds and blueprints into <see cref="Entries"/>, WEAPON_MANUFACTURERs into
    /// <see cref="Manufacturers"/>; a WEAPON no listed manufacturer makes is dropped.
    /// </summary>
    public static VendorList From(GameDatabase db, GameRecord r)
    {
        var entries = new List<VendorEntry>();
        var makers = new List<RecordLink>();
        foreach (string list in r.ReferenceLists)
            foreach (var e in r.GetReferences(list))
                AddEntry(db, list, e, entries, makers);

        // A weapon is stocked only when it is a `weapon types` entry of one of the list's manufacturers (<b>Verified</b>, `FUN_140956ba0`: "No valid
        // manufacturer found for weapon" otherwise); no manufacturer or manufacturers without weapon types skip the weapons altogether.
        var makable = makers
            .Where(m => db.Find(m.Id) is { Type: FcsRecordType.WeaponManufacturer })
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
    /// Sorts one reference of a list into the entries (items and blueprints) or the manufacturers; a reference to a record the database lacks
    /// is skipped.
    /// </summary>
    static void AddEntry(GameDatabase db, string list, GameReference e, List<VendorEntry> entries, List<RecordLink> makers)
    {
        bool gear = GearBlueprintLists.Contains(list);
        if (db.Find(gear ? e.TargetStringId + ".TECH.1" : e.TargetStringId) is not { } target) return;

        int weight = e.Values.Value0 == 0 ? 100 : e.Values.Value0;
        if (gear || list == "blueprints")
        {
            if (target.Type == FcsRecordType.Research)
                entries.Add(new VendorEntry(target.StringId, target.Type, weight));
        }
        else if (IsItemType(target.Type))
        {
            entries.Add(new VendorEntry(target.StringId, target.Type, weight));
        }
        else if (target.Type == FcsRecordType.WeaponManufacturer)
        {
            makers.Add(RecordLink.From(e));
        }
    }
}
