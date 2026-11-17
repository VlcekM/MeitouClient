using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Gameplay.Economy;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Trading;

/// <summary>
/// One stock container of a home being filled: its cells, its fixture and its record's storage rules (null: a record that says nothing); a squad
/// shop's goods are one with neither (<see cref="StockBox(Inventory)"/>).
/// </summary>
sealed record StockBox(Inventory Grid, Fixture? Fixture, MachineData? Data)
{
    /// <summary>
    /// A container fixture with its record's rules.
    /// </summary>
    public StockBox(Fixture fixture, MachineData? data) : this(fixture.Contents!, fixture, data)
    {
    }

    /// <summary>
    /// A squad shop's goods, which take anything that fits.
    /// </summary>
    public StockBox(Inventory goods) : this(goods, null, null)
    {
    }

    /// <summary>
    /// Its lock's level; 0 without a lock.
    /// </summary>
    public int LockLevel => Fixture?.Lock?.Level ?? 0;

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
    /// How full it is (<c>FUN_140746fe0</c>): the share of its cells that hold something; a one-item storage with a capacity (its <c>stackable
    /// bonus mult</c>) instead its first stack's quantity over that capacity.
    /// </summary>
    public float Fill
    {
        get
        {
            if (Limit.Count == 1 && Data is { StackBonusMult: > 0 } data)
                return Grid.Items.Count == 0 ? 0 : Grid.Items[0].Quantity / data.StackBonusMult;

            return Grid.Items.Where(i => i.Section == "main").Sum(i => i.Width * i.Height) / (float)(Grid.Width * Grid.Height);
        }
    }

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
    /// The factor on a money item's score (<c>DAT_141683250</c>).
    /// </summary>
    const float MoneyFactor = 100;

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
    /// Puts the fill's <paramref name="items"/>, each stack as the draw made it, into <paramref name="boxes"/> dearest first by unit value (a sort
    /// over reversed bounds, <c>FUN_140969ff0</c>; ties by record id here, the original's by the items' addresses): the first half of the items
    /// (trunc(count x 0.5), at least the first) each into the best-scoring container that takes it, the rest each into the first that takes it of a
    /// fresh shuffle. What no container takes is lost. Returns what was placed, each with the quantity that went in.
    /// </summary>
    public static List<ItemInstance> Spread(List<StockBox> boxes, IEnumerable<ItemInstance> items, Func<ItemInstance, int> value, ref RandomStream rng)
    {
        var ordered = items.OrderByDescending(value).ThenBy(i => i.Record, StringComparer.Ordinal).ToList();
        int scored = Math.Max((int)(ordered.Count * Half), 1);
        var open = new List<StockBox>(boxes);
        var placed = new List<ItemInstance>();

        for (int k = 0; k < ordered.Count; k++)
        {
            var item = ordered[k];
            int quantity = item.Quantity;
            var piece = item.WithQuantity(quantity);
            int left = k < scored ? PlaceScored(open, item, value(item), ref rng) : PlaceShuffled(boxes, item, ref rng);

            if (left < quantity) placed.Add(piece.WithQuantity(quantity - left));
        }

        return placed;
    }

    /// <summary>
    /// A scored item: the containers at least half full leave <paramref name="open"/> for good, the others are tried from the highest score down.
    /// Returns the quantity no container took.
    /// </summary>
    static int PlaceScored(List<StockBox> open, ItemInstance item, int value, ref RandomStream rng)
    {
        open.RemoveAll(b => b.Fill >= Half);

        var scores = new List<(StockBox Box, float Score)>();
        foreach (var box in open) scores.Add((box, Score(box, item, value) + rng.NextFloat() * ScoreNoise));

        foreach (var (box, _) in scores.OrderByDescending(s => s.Score))
            if (box.Take(item)) return 0;

        return item.Quantity;
    }

    /// <summary>
    /// An item put into the first container that takes it of <paramref name="boxes"/> shuffled again for it (the original's <c>random_shuffle</c> of
    /// the list): the unscored half of the units and each special item. Returns the quantity no container took.
    /// </summary>
    internal static int PlaceShuffled(List<StockBox> boxes, ItemInstance item, ref RandomStream rng)
    {
        for (int i = 1; i < boxes.Count; i++)
        {
            int j = rng.NextInt(i + 1);
            (boxes[i], boxes[j]) = (boxes[j], boxes[i]);
        }

        foreach (var box in boxes)
            if (box.Take(item)) return 0;

        return item.Quantity;
    }

    /// <summary>
    /// How well the container suits the item (<c>FUN_140952df0</c>): 0 for an item worth less than ten times the lock's level (money is not
    /// tested); else the level + 1, for an ITEM container as it is without a list, + 150 for an item on its list and 0 for one off it; for a
    /// container of another type x 1.5 for an item of that type and 0 for any other; x 100 for money (<c>item function</c> 14).
    /// </summary>
    static float Score(StockBox box, ItemInstance item, int value)
    {
        bool money = item.ItemFunction == Player.PlayerSystem.MoneyFunction;
        int level = box.LockLevel;
        if (level > 0 && !money && value < level * ValuePerLockLevel) return 0;

        float score = Fit(box, item, level + 1);
        return money ? score * MoneyFactor : score;
    }

    /// <summary>
    /// The score before the money factor: <paramref name="score"/> (the lock level + 1) as the container's type and list take the item.
    /// </summary>
    static float Fit(StockBox box, ItemInstance item, float score)
    {
        if (box.Type != FcsRecordType.Item) return item.Type == box.Type ? score * OwnTypeFactor : 0;
        if (box.Limit.Count == 0) return score;
        return box.Limit.Contains(item.Record) ? score + ListedBonus : 0;
    }
}
