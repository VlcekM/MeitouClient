using System.Numerics;
using Meitou.Data.Gameplay.Ai;
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
    /// The storage or machine with inputs a hauling operator takes its output to (or, fetching, the storage or producer); one that is gone ends the
    /// trip.
    /// </summary>
    (BuildSite Site, Machine Machine)? Hauling(World world, CharacterCold cold)
    {
        if (cold.HaulSite is not { } id) return null;
        if (Site(world, id) is { Complete: true } site && world.Machines.Find(id) is { } m
            && (m.Storage is not null || (cold.HaulFetch is not null ? m.Output is not null : m.Inputs.Length > 0)))
            return (site, m);

        cold.HaulSite = null;
        cold.HaulFetch = null;
        return null;
    }

    /// <summary>
    /// What an operator at its machine does before working; true when it set off for a storage. <paramref name="autoHaul"/> (row 152, whose plan is 93
    /// at a storage, docs/game/ai-tasks.md "Finished tests") takes an output that holds any, not only a full one, takes any carried output to the
    /// storage and fetches no input (its 124 companion's work).
    /// </summary>
    bool StartHaul(World world, CharacterCold cold, BuildSite site, Machine m, bool autoHaul = false)
    {
        if (cold.Inventory is not { } inv) return false;
        bool changed = PutCarried(inv, m);

        // A full output is taken only when some storage takes it; without one the machine stays full.
        bool took = false;
        if (m.Output is { Count: > 0 } output && (output.Full || autoHaul) && _items.Create(output.Recipe.Item, output.Count) is { } made
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
        // stray unit, which sent a miner on a trip for every piece of ore it carried (#388). The 152 order takes any carried output there.
        bool full = m.Output is { Full: true } || m.Craft is { } c && CraftFull(c);
        string? into = took || full || autoHaul
            ? CarriedMade(inv, m)
                .Select(i => StorageFor(world, site, i, cold))
                .FirstOrDefault(s => s is not null)
            : null;
        if (into is not null) return Go(cold, into, null);

        if (autoHaul || m.Craft is { Queue.Count: 0 }) return false;

        // The fill finder's walk with an empty input (Machine.FillOrder): the empty inputs in list order, the first with a source decides.
        foreach (var input in m.Inputs)
        {
            if (input.Rate > 0 && input.Count == 0 && NearestSource(world, site, input.Item, cold) is { } from)
                return Go(cold, from, input.Item);
        }

        return false;
    }

    /// <summary>
    /// Puts the carried items the machine's inputs take into them, up to each input's room; with <paramref name="madeBy"/> only what that machine
    /// makes (a haul trip's delivery). True when something moved.
    /// </summary>
    static bool PutCarried(Inventory inv, Machine m, Machine? madeBy = null)
    {
        bool changed = false;
        foreach (var input in m.Inputs)
        {
            int room = input.FillRoom;
            if (room <= 0 || madeBy is not null && !Makes(madeBy, input.Item)) continue;
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

        return changed;
    }

    /// <summary>
    /// Row 152's desire x finder (<c>FUN_14060fd80</c>, <b>Verified</b>): AI slot 21's desire (<see cref="Ai.RowDesires"/>; 1 in a world without the AI
    /// system), when above 0, times <see cref="AutoHaulFinder"/>. The order step pops a 152 order and the job pass skips a 152 job at 0 or less
    /// (docs/game/ai-tasks.md "Finished tests", "Perma-jobs").
    /// </summary>
    internal float AutoHaulScore(World world, int slot, CharacterCold cold, string site)
    {
        float desire = world.SystemOf<Ai.AiSystem>() is { } ai ? ai.Rows.ForSite(world, slot, TaskType.OperateAutomaticMachinery, site) : 1;

        return desire > 0 ? desire * AutoHaulFinder(world, cold, site) : desire;
    }

    /// <summary>
    /// Row 152's finder (the ditch finder <c>FUN_1405a59a0</c> through the stub <c>FUN_1405a70a0</c>, <b>Verified</b>), which weighs the 152 order's
    /// desire: 1 when a storage or machine of the character's takes the machine's output item (<see cref="StorageFor"/>) and the output holds some or
    /// the character carries some; else 0, so always 0 at a machine with no output (a fuel generator) (docs/game/ai-tasks.md "Finished tests").
    /// </summary>
    internal float AutoHaulFinder(World world, CharacterCold cold, string siteId)
    {
        if (Site(world, siteId) is not { Complete: true } site || world.Machines.Find(siteId) is not { Output: { } output } m) return 0;
        if (output.Count == 0 && !CarriesOutputOf(cold, m)) return 0;
        if (_items.Create(output.Recipe.Item, 1) is not { } one) return 0;

        return StorageFor(world, site, one, cold) is not null ? 1 : 0;
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
    /// At the storage, where the character stands at <paramref name="at"/>: puts the carried output in (a machine target: into its inputs,
    /// <see cref="PutCarried"/>), or fetches (an automatic machine's hauler as an NPC worker's fetch does, <see cref="FetchTownWants"/>; an operator
    /// what it went for, <see cref="FetchAt"/>); then the trip ends.
    /// </summary>
    void HaulAt(World world, CharacterCold cold, Vector3 at, Machine machine, Machine store)
    {
        var inv = cold.Inventory;
        if (inv is not null && cold.HaulFetch is { } want)
        {
            if (machine.Data.IsAutomatic) FetchTownWants(world, cold, at, store, want, inv);
            else FetchAt(machine, store, want, inv);
        }
        else if (inv is not null && store.Storage is null) PutCarried(inv, store, machine);
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
    /// An operator's Auto haul fetch: the record it went for (<paramref name="want"/>), out of a storage's main grid or a producer's output (a
    /// well), up to the room the machine's inputs of it have. Engine choice: the original's operate job fetches no inputs at all (docs/game/
    /// buildings-production.md "Hauling"), so Auto haul has no original take to follow, and the original's take (<see cref="FetchTownWants"/>)
    /// hands over another record than the one looked for whenever the storage lists one the town wants first (#1230).
    /// </summary>
    void FetchAt(Machine machine, Machine store, string want, Inventory inv)
    {
        int room = InputRoom(machine, want);
        if (room <= 0) return;

        if (store.Data.IsStorage)
        {
            if (store.Storage is { } storage) TakeStored(storage, inv, want, room);
            return;
        }

        TakeOutput(store, want, room, inv);
    }

    /// <summary>
    /// An automatic machine's hauler's fetch (the 152 job, which the original plans through <c>Task_EmptyMachine</c> with flag 0,
    /// <see cref="FillDemand"/>): out of a producer's output (a well) what the town wants of it (<see cref="FillDemand.Count"/>), else the
    /// storage's first record the town wants, up to that count; not bounded by the machine's room.
    /// </summary>
    void FetchTownWants(World world, CharacterCold cold, Vector3 at, Machine store, string want, Inventory inv)
    {
        if (store.Data.IsStorage)
        {
            if (store.Storage is { } storage) FillDemand.TakeStored(world, _towns, cold, at, storage, store.Site, store.Data.Id, inv);
            return;
        }

        if (store.Output?.Recipe.Item != want || _items.Create(want, 1) is not { } unit) return;

        TakeOutput(store, want, FillDemand.Count(world, _towns, cold, at, unit, store.Site, store.Data.Id), inv);
    }

    /// <summary>
    /// The units of <paramref name="record"/> the operator's fill would put into the machine's inputs (<see cref="InputSlot.FillRoom"/>).
    /// </summary>
    static int InputRoom(Machine m, string record)
    {
        int room = 0;
        foreach (var input in m.Inputs)
        {
            if (input.Item == record) room += input.FillRoom;
        }

        return room;
    }

    /// <summary>
    /// Takes up to <paramref name="limit"/> units of <paramref name="want"/> out of a producer's output into <paramref name="inv"/>.
    /// </summary>
    void TakeOutput(Machine store, string want, int limit, Inventory inv)
    {
        if (store.Output is not { } from || from.Recipe.Item != want) return;

        int take = Math.Min(limit, from.Count);
        if (take > 0 && _items.Create(want, take) is { } made) from.Count -= Placed(inv, made);
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
    /// Where <paramref name="site"/>'s output <paramref name="item"/> goes (the per-item storage finder <c>FUN_14059acd0</c>, <b>Verified</b>): the
    /// nearest building <paramref name="by"/> uses within <see cref="InFinderRange"/> whose slot <c>0x4a0</c> takes it (<see cref="TakesOutput"/>),
    /// a storage or a machine, so a miner's stone goes into a stone processor nearer than any storage (#1406).
    /// </summary>
    string? StorageFor(World world, BuildSite site, ItemInstance item, CharacterCold by)
    {
        string? own = world.Machines.Find(site.Id)?.Data.Id;
        return NearestStorage(world, site, by, item.Record, s => TakesOutput(s, own, item));
    }

    /// <summary>
    /// A candidate of the per-item storage finder that takes <paramref name="item"/> (slot <c>0x4a0</c>, <b>Verified</b>): a storage that accepts it
    /// and has room (<c>StorageBuilding</c>, <c>FUN_1402988c0</c>), or a machine of the finder's mask (<see cref="FillSources.InMask"/>) not of the
    /// served machine's record <paramref name="own"/> (the finder skips that record, vfunc <c>0x18</c>) whose input of it has room
    /// (<see cref="InputTakes"/>).
    /// </summary>
    static bool TakesOutput(Machine m, string? own, ItemInstance item)
    {
        if (m.Data.IsStorage) return m.Storage is { } grid && m.Data.Accepts(item.Record, item.Type) && Fits(grid, item);

        return m.Data.Id != own && FillSources.InMask(m.Data.Function) && InputTakes(m, item.Record);
    }

    /// <summary>
    /// A machine's slot <c>0x4a0</c> (<c>ProductionBuilding::canHaveSomeOfThese</c>, <c>FUN_1402989b0</c>, <b>Verified</b>): its first input of
    /// <paramref name="record"/> holds less than its capacity; a crafting bench's (<c>FUN_140298a80</c>) only while a craft is queued. Meitou's fill
    /// puts nothing into an input that draws nothing (<see cref="InputSlot.FillRoom"/>), so such an input takes nothing here either.
    /// </summary>
    static bool InputTakes(Machine m, string record)
    {
        if (m.Craft is { Queue.Count: 0 }) return false;

        int at = Array.FindIndex(m.Inputs, i => i.Takes(record));
        return at >= 0 && m.Inputs[at].FillRoom > 0;
    }

    /// <summary>
    /// Whether some of an item fits the storage's grid: a stack it may join with room, or a free cell (<see cref="Inventory.HasRoomIn"/>, the
    /// stacking rule the put itself follows, <see cref="Inventory.SameStack"/>), so a storage chosen for an item always takes some of it.
    /// </summary>
    static bool Fits(Inventory storage, ItemInstance item) => storage.HasRoomIn("main", item);

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

        return NearestSite(world, from.Position, float.MaxValue, IsStore, Stored) ?? NearestSite(world, from.Position, float.MaxValue, IsMaker, Made);
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
    /// The nearest complete building <paramref name="by"/> uses (<see cref="StoresFor"/>) within <see cref="InFinderRange"/> of a machine, other
    /// than that machine, that passes <paramref name="ok"/> and the storage filters for putting <paramref name="record"/> in
    /// (<see cref="IStorageFilter"/>; ties: the lower site id). The original's per-item storage finder (<c>FUN_14059acd0</c>) measures from the
    /// character, who stands at the machine (#965: a storage 3000 or more away was missed).
    /// </summary>
    string? NearestStorage(World world, BuildSite from, CharacterCold by, string record, Func<Machine, bool> ok)
    {
        string? best = null;
        float bestGap = float.MaxValue;
        foreach (var s in world.Machines.All)
        {
            if (s.Site == from.Id || Site(world, s.Site) is not { Complete: true } site || !StoresFor(world, by, site)) continue;

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
