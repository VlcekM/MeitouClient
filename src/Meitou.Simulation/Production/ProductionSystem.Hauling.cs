using System.Numerics;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Production;

/// <summary>
/// Hauling (docs/simulation.md "Production"; the original's Task_EmptyMachine / Task_FillMachine in docs/game/buildings-production.md "Hauling"):
/// with Settings.AutoHaul an operator at its machine fills empty inputs with what it carries, takes a full output, carries the output to the nearest
/// storage that takes it and fetches an empty input's item from the nearest storage or producer output holding it (NearestSource). The trip is
/// CharacterCold.HaulSite / HaulFetch; Schedule walks there, and the operator walks back to its machine once the items have moved.
/// </summary>
public sealed partial class ProductionSystem
{
    /// <summary>
    /// The storage (or, fetching, the producer) a hauling operator walks to; one that is gone ends the trip.
    /// </summary>
    (BuildSite Site, Machine Machine)? Hauling(World world, CharacterCold cold)
    {
        if (cold.HaulSite is not { } id) return null;
        if (Site(world, id) is { Complete: true } site && world.Machines.Find(id) is { } m
            && (m.Storage is not null || cold.HaulFetch is not null && m.Output is not null))
            return (site, m);

        cold.HaulSite = null;
        cold.HaulFetch = null;
        return null;
    }

    /// <summary>
    /// What an operator at its machine does before working; true when it set off for a storage.
    /// </summary>
    bool StartHaul(World world, CharacterCold cold, BuildSite site, Machine m)
    {
        if (cold.Inventory is not { } inv) return false;
        bool changed = false;
        foreach (var input in m.Inputs)
        {
            int room = input.Recipe.Capacity - input.Count;
            if (input.Rate <= 0 || room <= 0) continue;
            foreach (var item in Carried(inv, input.Item).ToList())
            {
                int n = Math.Min(room, item.Quantity);
                input.Count += n;
                room -= n;
                item.Quantity -= n;
                if (item.Quantity == 0) inv.Remove(item);
                changed = true;
                if (room == 0) break;
            }
        }

        // A full output is taken only when some storage takes it; without one the machine stays full.
        bool took = false;
        if (m.Output is { Full: true } output && _items.Create(output.Recipe.Item, output.Count) is { } made
            && StorageFor(world, site, made, cold) is not null)
        {
            int moved = Placed(inv, made);
            if (moved > 0) { output.Count -= moved; changed = took = true; }
        }

        // A bench full on its first craft: its finished items a storage takes (#762).
        if (m.Craft is { } craft && CraftFull(craft) && TakeDone(craft, inv, i => StorageFor(world, site, i, cold) is not null))
            changed = took = true;

        if (changed) world.Machines.Touch();

        // The carried output goes to the storage once a full output was taken, or while the output stays full (no room to take it): not for a
        // stray unit, which sent a miner on a trip for every piece of ore it carried (#388).
        bool full = m.Output is { Full: true } || m.Craft is { } c && CraftFull(c);
        string? into = took || full
            ? CarriedMade(inv, m)
                .Select(i => StorageFor(world, site, i, cold))
                .FirstOrDefault(s => s is not null)
            : null;
        if (into is not null) return Go(cold, into, null);

        if (m.Craft is { Queue.Count: 0 }) return false;
        foreach (var input in m.Inputs)
        {
            if (input.Rate > 0 && input.Count == 0 && NearestSource(world, site, input.Item, cold) is { } from)
                return Go(cold, from, input.Item);
        }

        return false;
    }

    /// <summary>
    /// Starts a haul trip to <paramref name="storage"/>, fetching <paramref name="fetch"/> (null: delivering); always true.
    /// </summary>
    static bool Go(CharacterCold cold, string storage, string? fetch)
    {
        cold.HaulSite = storage;
        cold.HaulFetch = fetch;
        return true;
    }

    /// <summary>
    /// At the storage: puts the carried output in, or takes what the machine's empty inputs hold room for (from a producer, out of its output); then
    /// the trip ends.
    /// </summary>
    void HaulAt(World world, CharacterCold cold, Machine machine, Machine store)
    {
        var inv = cold.Inventory;
        if (inv is not null && cold.HaulFetch is { } want)
        {
            int room = InputRoom(machine, want);

            // A producer (a well): out of its output.
            if (!store.Data.IsStorage)
            {
                if (store.Output is { } from && from.Recipe.Item == want && Math.Min(room, from.Count) is > 0 and var take
                    && _items.Create(want, take) is { } made)
                    from.Count -= Placed(inv, made);
            }
            else if (store.Storage is { } storage) TakeStored(storage, inv, want, room);
        }
        else if (inv is not null && store.Storage is { } into)
        {
            foreach (var item in CarriedMade(inv, machine).ToList())
            {
                if (!store.Data.Accepts(item.Record, item.Type)) continue;
                inv.Remove(item);
                if (into.Add(item)) continue;

                // What the storage has no room for stays with the character.
                if (!inv.Add(item)) world.GroundItems.Drop(item, store.Position, 0);
                break;
            }
        }

        cold.HaulSite = null;
        cold.HaulFetch = null;
        world.Machines.Touch();
    }

    /// <summary>
    /// Puts an item into an inventory; the units placed (<see cref="Inventory.Add"/> leaves the quantity as it was when all fit).
    /// </summary>
    internal static int Placed(Inventory into, ItemInstance item)
    {
        int before = item.Quantity;
        return into.Add(item) ? before : before - item.Quantity;
    }

    /// <summary>
    /// A fetch's take at a storage, the player's haul and an NPC worker's (<see cref="Kidnap.TownDutySystem"/>): up to <paramref name="room"/> units
    /// of <paramref name="want"/> out of the storage's main grid into <paramref name="into"/>, what finds no room going back; the units moved.
    /// </summary>
    internal static int TakeStored(Inventory storage, Inventory into, string want, int room)
    {
        int moved = 0;
        foreach (var item in storage.Items.Where(i => i.Record == want && i.Section == "main").ToList())
        {
            if (room <= 0) break;

            int n = Math.Min(room, item.Quantity);
            var moving = n == item.Quantity ? item : item.WithQuantity(n);
            if (ReferenceEquals(moving, item)) storage.Items.Remove(item);
            else item.Quantity -= n;

            // What the character has no room for goes back.
            int placed = Placed(into, moving);
            if (placed < n) storage.Add(moving);

            room -= placed;
            moved += placed;
            if (placed == 0) break;
        }

        return moved;
    }

    /// <summary>
    /// The units of <paramref name="record"/> a machine's inputs have room for (the haul fetch's take, <see cref="HaulAt"/>).
    /// </summary>
    internal static int InputRoom(Machine machine, string record)
    {
        int room = 0;
        foreach (var input in machine.Inputs)
            if (input.Takes(record)) room += Math.Max(0, input.Recipe.Capacity - input.Count);

        return room;
    }

    /// <summary>
    /// The loose stacks of an ITEM in a character's main grid and backpack.
    /// </summary>
    static IEnumerable<ItemInstance> Carried(Inventory inv, string record) =>
        inv.All().Where(i => i.Record == record && i.Section is "main" or "backpack_content" && !i.LockedOn && i.Contents is null);

    /// <summary>
    /// The loose stacks in a character's main grid and backpack of what machine <paramref name="m"/> makes (<see cref="Makes"/>).
    /// </summary>
    static IEnumerable<ItemInstance> CarriedMade(Inventory inv, Machine m) =>
        inv.All().Where(i => i.Section is "main" or "backpack_content" && !i.LockedOn && i.Contents is null && Makes(m, i.Record));

    /// <summary>
    /// Whether machine <paramref name="m"/> makes <paramref name="record"/>: its output's item, or one of its bench's crafts.
    /// </summary>
    static bool Makes(Machine m, string record) =>
        m.Output?.Recipe.Item == record || m.Data.Craft?.Options.Any(o => o.Item == record) == true;

    /// <summary>
    /// Whether a bench's output takes no more of its first craft (<see cref="OutputFull"/>).
    /// </summary>
    static bool CraftFull(CraftState state) => state.Queue.Count > 0 && OutputFull(state, state.Queue[0].Option);

    /// <summary>
    /// Moves a bench's finished items that <paramref name="takes"/> into the inventory, the instances themselves so a crafted piece keeps its
    /// quality and model; true when any moved. What finds no room stays in the bench.
    /// </summary>
    static bool TakeDone(CraftState state, Inventory inv, Func<ItemInstance, bool> takes)
    {
        bool moved = false;
        for (int k = 0; k < state.Done.Count; k++)
        {
            var item = state.Done[k];
            if (!takes(item)) continue;

            int before = item.Quantity;
            state.Done.RemoveAt(k);
            if (inv.Add(item))
            {
                moved = true;
                k--;
                continue;
            }

            // Inventory.Add leaves the units it could not place on the item.
            moved |= item.Quantity < before;
            state.Done.Insert(k, item);
            break;
        }

        return moved;
    }

    /// <summary>
    /// Whether <paramref name="from"/>'s output holds something <paramref name="store"/> takes and has room for: its output slot's item, or a
    /// bench's finished item.
    /// </summary>
    bool Offers(Machine from, Machine store)
    {
        if (from.Output is { Count: > 0 } output && _items.Create(output.Recipe.Item, 1) is { } one && store.Data.Accepts(one.Record, one.Type)
            && Fits(store.Storage!, one))
            return true;

        return from.Craft is { } craft && craft.Done.Any(i => store.Data.Accepts(i.Record, i.Type) && Fits(store.Storage!, i));
    }

    /// <summary>
    /// The nearest storage <paramref name="by"/> uses within <see cref="InFinderRange"/> of <paramref name="site"/> that takes <paramref name="item"/>
    /// and has room for it.
    /// </summary>
    string? StorageFor(World world, BuildSite site, ItemInstance item, CharacterCold by) =>
        NearestStorage(world, site, by, item.Record, s => s.Data.Accepts(item.Record, item.Type) && Fits(s.Storage!, item));

    /// <summary>
    /// Whether some of an item fits: a stack of it with room, or a free cell.
    /// </summary>
    static bool Fits(Inventory storage, ItemInstance item)
    {
        int limit = storage.LimitOf(item);
        foreach (var other in storage.Items)
        {
            if (other.Record == item.Record && other.Quality == item.Quality && other.Mark == item.Mark && other.Section == "main"
                && other.Quantity < limit)
                return true;
        }

        return storage.FindCell("main", item.Width, item.Height, out _, out _);
    }

    /// <summary>
    /// Where an input's item is fetched from (the fill finder <c>FUN_14059d9b0</c> -> <c>FUN_14059d000</c>, <b>Verified</b>): the player's nearest
    /// complete storage holding it in its main grid, else its nearest machine whose output holds it (a well's water; the split is the building's
    /// vfunc <c>0x3b0</c>, null for a <c>StorageBuilding</c>), each within <see cref="ConstructionSystem.SourceRangeSquared"/> (3D, inclusive) of
    /// the machine (ties: the lower site id); no building of the machine's own record (vfunc <c>0x18</c>, the record, against the excluded target's).
    /// The finder measures from the character, who stands at the machine when it fetches (#963: a well 3000 or more away was missed). A producer
    /// <paramref name="operating"/> operates counts once its output is full, so it fills the well before it leaves with the water. A storage is
    /// taken only when the storage filters let <paramref name="fetcher"/> take the item from it (<see cref="IStorageFilter"/>).
    /// </summary>
    string? NearestSource(World world, BuildSite from, string record, CharacterCold fetcher, CharacterCold? operating = null)
    {
        string? own = world.Machines.Find(from.Id)?.Data.Id;
        bool Fetchable(BuildSite s, Machine m) => s.Id != from.Id && m.Data.Id != own && InSourceRange(s.Position, from.Position);
        bool Stored(BuildSite s, Machine m) => m.Data.IsStorage && Fetchable(s, m) && m.Storage is { } st && StorageHolds(st, record)
            && world.StorageAllows(fetcher, s.Id, StorageUse.Take, record);
        bool Made(BuildSite s, Machine m) => !m.Data.IsStorage && Fetchable(s, m) && OutputHolds(m, s, record, operating);

        return NearestSite(world, from.Position, float.MaxValue, Stored) ?? NearestSite(world, from.Position, float.MaxValue, Made);
    }

    /// <summary>
    /// A storage's inventory holding <paramref name="record"/> in its main grid.
    /// </summary>
    internal static bool StorageHolds(Inventory storage, string record) =>
        storage.Items.Any(i => i.Record == record && i.Quantity > 0 && i.Section == "main");

    /// <summary>
    /// A producer whose output holds <paramref name="record"/>: one <paramref name="by"/> operates only once its output is full.
    /// </summary>
    static bool OutputHolds(Machine m, BuildSite s, string record, CharacterCold? by) =>
        m.Output is { Count: > 0 } o && o.Recipe.Item == record && (o.Full || by?.OperateSite != s.Id);

    /// <summary>
    /// The nearest complete storage <paramref name="by"/> uses (<see cref="StoresFor"/>) within <see cref="InFinderRange"/> of a machine that passes
    /// <paramref name="ok"/> and the storage filters for putting <paramref name="record"/> in (<see cref="IStorageFilter"/>; ties: the lower
    /// site id). The original's per-item storage finder (<c>FUN_14059acd0</c>) measures from the character, who stands at the machine (#965: a
    /// storage 3000 or more away was missed).
    /// </summary>
    string? NearestStorage(World world, BuildSite from, CharacterCold by, string record, Func<Machine, bool> ok)
    {
        string? best = null;
        float bestGap = float.MaxValue;
        foreach (var s in world.Machines.All)
        {
            if (!s.Data.IsStorage || s.Storage is null || s.Site == from.Id || Site(world, s.Site) is not { Complete: true } site
                || !StoresFor(world, by, site))
                continue;

            float gap = Gap(site.Position, from.Position);
            if (gap > bestGap || (gap == bestGap && string.CompareOrdinal(s.Site, best) > 0)) continue;
            if (!InFinderRange(site.Position, from.Position) || !ok(s)) continue;
            if (!world.StorageAllows(by, s.Site, StorageUse.Put, record)) continue;

            best = s.Site;
            bestGap = gap;
        }

        return best;
    }

    /// <summary>
    /// The storage finder's owner test (<c>FUN_14059acd0</c>, <b>Verified</b>): a storage of the character's own faction, or, for one of the player's
    /// side (<c>FUN_140856bc0</c>: the player's faction, or a squad whose AI contract the player's faction runs), of the player's faction.
    /// </summary>
    static bool StoresFor(World world, CharacterCold by, BuildSite storage) =>
        storage.Owner == by.Faction || storage.Owner == world.Player.Faction && world.Player.Faction >= 0 && Ai.DecisionSystem.Decides(by);

    /// <summary>
    /// Whether two points are within the squared 3D distance the original's storage and fill finders take in the default ROAM_FAR range mode
    /// (<see cref="ConstructionSystem.SourceRangeSquared"/>, about 6517; docs/game/buildings-production.md "Material delivery").
    /// </summary>
    static bool InFinderRange(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) < ConstructionSystem.SourceRangeSquared;

    /// <summary>
    /// Whether two points are within the source finder's range (<c>FUN_14059d000</c> keeps a building at the bound: <c>&lt;=</c>).
    /// </summary>
    static bool InSourceRange(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) <= ConstructionSystem.SourceRangeSquared;
}
