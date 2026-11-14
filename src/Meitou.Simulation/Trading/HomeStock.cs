using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Gameplay.Economy;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Trading;

/// <summary>
/// One stock container of a home being filled: its fixture and its record's storage rules (null: a record that says nothing).
/// </summary>
sealed record StockBox(Fixture Fixture, MachineData? Data)
{
    /// <summary>
    /// The container's cells.
    /// </summary>
    public Inventory Grid => Fixture.Contents!;

    /// <summary>
    /// Its lock's level; 0 without a lock.
    /// </summary>
    public int LockLevel => Fixture.Lock?.Level ?? 0;

    /// <summary>
    /// The record type it is for: a resource storage's <c>itemtype limit</c> (the <c>StorageBuilding</c> field <c>+0x440</c>), ITEM for any other
    /// building.
    /// </summary>
    public FcsRecordType Type => Data is { Function: MachineData.ResourceStorageFunction } ? Data.ItemTypeLimit : FcsRecordType.Item;

    /// <summary>
    /// The only items it takes (a resource storage's <c>limit inventory</c>); empty: any.
    /// </summary>
    public IReadOnlyList<string> Limit => Data?.StorageLimit ?? [];

    /// <summary>
    /// The share of its cells that hold something.
    /// </summary>
    public float Fill => Grid.Items.Where(i => i.Section == "main").Sum(i => i.Width * i.Height) / (float)(Grid.Width * Grid.Height);

    /// <summary>
    /// Puts the item in when the container's section takes it (a WEAPON-limited one only weapons, a limited one only its list;
    /// <see cref="MachineData.TakesByHand"/>) and it fits; a stack that fits in part leaves the rest in the item.
    /// </summary>
    public bool Take(ItemInstance item) => Data?.TakesByHand(item.Record, item.Type) != false && Grid.Add(item);
}

/// <summary>
/// How the building filler spreads a home's new stock over its containers (<c>FUN_14095a5b0</c>, <c>FUN_1409565e0</c>, <c>FUN_140952df0</c>,
/// <b>Verified</b>; docs/game/economy.md "Building shops"): the dearer half goes where each item scores best, a locked container taking only what
/// is worth its lock, and the rest goes to containers in a random order.
/// </summary>
static class HomeStock
{
    /// <summary>
    /// A container this full no longer takes a scored item (<c>DAT_14167e8d8</c>), and the share of the items that are scored.
    /// </summary>
    const float Half = 0.5f;

    /// <summary>
    /// An item is worth a lock when its value is at least this many times the lock's level.
    /// </summary>
    const int ValuePerLockLevel = 10;

    /// <summary>
    /// The score added for an item on the container's list (<c>DAT_1416cf06c</c>).
    /// </summary>
    const float ListedBonus = 150;

    /// <summary>
    /// The factor on the score of an item of the container's own record type (<c>DAT_14168ad18</c>).
    /// </summary>
    const float OwnTypeFactor = 1.5f;

    /// <summary>
    /// The random term added to each score, times a roll in [0, 1) (<c>DAT_14167c310</c>): it orders equal containers.
    /// </summary>
    const float ScoreNoise = 0.01f;

    /// <summary>
    /// The containers that can hold something of what the vendor lists make (<c>FUN_14095a5b0</c>): one for another record type than ITEM needs an
    /// entry of that type, one with a list needs an entry on it.
    /// </summary>
    public static List<StockBox> Usable(IEnumerable<StockBox> boxes, IReadOnlyCollection<VendorEntry> entries)
    {
        var types = entries.Select(e => e.Type).ToHashSet();
        var ids = entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        return [.. boxes.Where(b => b.Type != FcsRecordType.Item ? types.Contains(b.Type) : b.Limit.Count == 0 || b.Limit.Any(ids.Contains))];
    }

    /// <summary>
    /// Puts <paramref name="items"/> into <paramref name="boxes"/> unit by unit, dearest first (ties by record id): the first half (at least one)
    /// each into the best-scoring container that takes it, the rest each into the first that takes it of a fresh shuffle. What no container takes
    /// is lost.
    /// </summary>
    public static void Spread(List<StockBox> boxes, IEnumerable<ItemInstance> items, Func<ItemInstance, int> value, ref RandomStream rng)
    {
        var ordered = items.SelectMany(Units).OrderByDescending(value).ThenBy(i => i.Record, StringComparer.Ordinal).ToList();
        int scored = Math.Max((int)(ordered.Count * Half), 1);
        var open = new List<StockBox>(boxes);

        for (int k = 0; k < ordered.Count; k++)
        {
            if (k < scored) PlaceScored(open, ordered[k], value(ordered[k]), ref rng);
            else PlaceShuffled(boxes, ordered[k], ref rng);
        }
    }

    /// <summary>
    /// The stack as single units (the filler makes and places its items one by one).
    /// </summary>
    static IEnumerable<ItemInstance> Units(ItemInstance stack)
    {
        for (int k = 0; k < stack.Quantity; k++) yield return stack.WithQuantity(1);
    }

    /// <summary>
    /// A scored item: the containers at least half full leave <paramref name="open"/> for good, the others are tried from the highest score down.
    /// </summary>
    static void PlaceScored(List<StockBox> open, ItemInstance item, int value, ref RandomStream rng)
    {
        open.RemoveAll(b => b.Fill >= Half);

        var scores = new List<(StockBox Box, float Score)>();
        foreach (var box in open) scores.Add((box, Score(box, item, value) + rng.NextFloat() * ScoreNoise));

        foreach (var (box, _) in scores.OrderByDescending(s => s.Score))
            if (box.Take(item)) return;
    }

    /// <summary>
    /// An unscored item: every container, shuffled again for each item (the original's <c>random_shuffle</c> of the list), tried in that order.
    /// </summary>
    static void PlaceShuffled(List<StockBox> boxes, ItemInstance item, ref RandomStream rng)
    {
        for (int i = 1; i < boxes.Count; i++)
        {
            int j = rng.NextInt(i + 1);
            (boxes[i], boxes[j]) = (boxes[j], boxes[i]);
        }

        foreach (var box in boxes)
            if (box.Take(item)) return;
    }

    /// <summary>
    /// How well the container suits the item (<c>FUN_140952df0</c>): 0 for an item worth less than ten times the lock's level; else the level + 1,
    /// for an ITEM container as it is without a list, + 150 for an item on its list and 0 for one off it; for a container of another type x 1.5
    /// for an item of that type and 0 for any other. The original's money items (x 100, no lock test) are not stocked here.
    /// </summary>
    static float Score(StockBox box, ItemInstance item, int value)
    {
        int level = box.LockLevel;
        if (level > 0 && value < level * ValuePerLockLevel) return 0;

        float score = level + 1;
        if (box.Type != FcsRecordType.Item) return item.Type == box.Type ? score * OwnTypeFactor : 0;
        if (box.Limit.Count == 0) return score;
        return box.Limit.Contains(item.Record) ? score + ListedBonus : 0;
    }
}
