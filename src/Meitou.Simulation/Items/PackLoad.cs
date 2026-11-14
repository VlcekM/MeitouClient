using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Economy;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Items;

/// <summary>
/// What a new pack animal's worn bag holds (docs/game/animals.md "Pack animals"; the animal's inventory setup <c>FUN_14062a130</c> and the list
/// filler <c>FUN_14074a3d0</c>, <b>Verified</b>): the goods of its squad template's <c>vendors</c> lists, whether or not the squad is a trader, or
/// with none of those of the ANIMAL_CHARACTER's own <c>inventory</c> lists. This is all a Traders Guild <c>Caravan</c> has to sell (#657).
/// </summary>
public static class PackLoad
{
    /// <summary>
    /// The items a list gives when its <c>items count</c> is below 1.
    /// </summary>
    const int DefaultItemsCount = 40;

    /// <summary>
    /// Separates the load's rolls from the animal's other spawn rolls.
    /// </summary>
    const ulong Salt = 0x5061_636B_4C6F_6164UL;

    /// <summary>
    /// Loads the bag <paramref name="inventory"/> wears from each list in turn. <b>Engine choice</b>: a <paramref name="homeless"/> squad with a shop
    /// loads none, as its squad stock stands in for its bags (the original's squad fill empties them and fills them again, economy.md "Who trades").
    /// </summary>
    public static void Fill(PopulationData data, Inventory inventory, GameRecord animal, SquadTemplate template, bool homeless, int faction,
        ulong seed, ulong key)
    {
        if (inventory.Backpack?.Contents is not { } bag || homeless && template.HasShop) return;

        var rng = new RandomStream(seed, Rng.Mix(key ^ Salt), RngPurpose.Spawn);
        var lists = template.Vendors.Count > 0
            ? template.Vendors.Select(l => l.Id)
            : animal.GetReferences("inventory").Select(r => r.TargetStringId);

        foreach (string id in lists.ToList())
            if (data.Db.Find(id) is { Type: FcsRecordType.VendorList } list) FillFrom(data, bag, VendorList.From(data.Db, list), faction, ref rng);
    }

    /// <summary>
    /// Puts the list's <c>items count</c> picks into <paramref name="bag"/>, in at most twice as many tries: a pick that does not fit is lost.
    /// </summary>
    static void FillFrom(PopulationData data, Inventory bag, VendorList list, int faction, ref RandomStream rng)
    {
        if (list.Entries.Count == 0) return;

        int left = list.ItemsCount < 1 ? DefaultItemsCount : list.ItemsCount;

        for (int tries = left * 2; left > 0 && tries > 0; tries--)
            if (ItemSlots.Roll(data, list, faction, ref rng).Item is { } item && bag.AddTo("backpack_content", item)) left--;
    }
}
