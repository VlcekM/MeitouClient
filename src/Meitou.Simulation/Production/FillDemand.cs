using System.Numerics;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Production;

/// <summary>
/// What a fetch takes at its source: <c>Task_EmptyMachine</c> with flag 0 (row 92's factory <c>FUN_14032ebd0</c>, its update
/// <c>FUN_14033cd10</c>, <b>Verified</b>; docs/game/buildings-production.md "NPC town machines"), for the NPC worker's fetch and the player's haul
/// alike. The source lists its records (a storage its distinct records in item order, a producer its output); the first that its slot 5
/// (<c>FUN_140356db0</c>, <see cref="Count"/>) counts above 0 is taken, up to that count, while the character has room. The machine the fetch is
/// for is not asked. Pure reads but for the take; serial phases only.
/// </summary>
internal static class FillDemand
{
    /// <summary>
    /// What <c>FUN_140356db0</c> returns at once for a candidate that takes the item other than by its first input: a storage, or a machine whose
    /// input of the item is not its first.
    /// </summary>
    const int ElsewhereCount = 5;

    /// <summary>
    /// The take at a storage (<paramref name="source"/>, its id and its building record): the first record of its main grid, in item order, that
    /// <see cref="Count"/> counts above 0, up to that count (<see cref="ProductionSystem.TakeStored(Inventory, Inventory, string, int)"/>); the
    /// units moved, 0 when no record is wanted.
    /// </summary>
    public static int TakeStored(World world, PopulationData? data, CharacterCold cold, Vector3 at, Inventory source, string sourceId,
        string sourceRecord, Inventory into)
    {
        foreach (var stack in Listed(source))
        {
            int wanted = Count(world, data, cold, at, stack, sourceId, sourceRecord);
            if (wanted > 0) return ProductionSystem.TakeStored(source, into, stack.Record, wanted);
        }

        return 0;
    }

    /// <summary>
    /// Slot 5 of <c>Task_EmptyMachine</c> (<c>FUN_140356db0</c>, <b>Verified</b>): how many units of <paramref name="unit"/>'s record the
    /// character's town wants. The candidates are the buildings of the fill finder's mask in the character's town
    /// (<see cref="FixtureRegistry.TownCandidates"/>; every one with no town data), complete, other than the source and of another record; no
    /// distance bound. A storage that takes a unit, or a machine whose input of it has room but is not its first, makes the count 5 at once;
    /// otherwise each machine whose first input is the item adds that input's room. One more when the item is food (<c>item function</c> 3) and
    /// the character carries none (Character slot <c>0x2f0</c>, <c>FUN_1405972d0</c>: functions 3 and 15).
    /// </summary>
    public static int Count(World world, PopulationData? data, CharacterCold cold, Vector3 at, ItemInstance unit, string sourceId,
        string sourceRecord)
    {
        var inTown = data is not null ? world.Fixtures.TownCandidates(world, data, cold, at) : null;
        if (StorageTakes(world, cold, unit, sourceId, sourceRecord, inTown)) return ElsewhereCount;

        int count = ForSelf(cold, unit);
        foreach (var m in world.Machines.All)
        {
            if (m.Data.IsStorage || !Candidate(world, m, sourceId, sourceRecord, inTown)) continue;
            if (InputWants(m, unit.Record) is not { } room) return ElsewhereCount;

            count += room;
        }

        return count;
    }

    /// <summary>
    /// The first stack of each record of a storage's main grid, in item order (<c>StorageBuilding</c> slot <c>0x548</c>, <c>FUN_140299990</c>).
    /// </summary>
    static IEnumerable<ItemInstance> Listed(Inventory source) =>
        source.Items
            .Where(i => i.Section == "main" && i.Quantity > 0)
            .DistinctBy(i => i.Record);

    /// <summary>
    /// A candidate storage takes a unit (<c>StorageBuilding</c> slot <c>0x4a0</c>, <c>FUN_1402988c0</c>; its entry 0 is empty, so the count's
    /// loop returns 5 on its inventory's room, <c>FUN_14074afa0</c>): a container fixture or a player storage machine the character may use.
    /// </summary>
    static bool StorageTakes(World world, CharacterCold cold, ItemInstance unit, string sourceId, string sourceRecord,
        Func<Vector3, bool>? inTown)
    {
        if (world.SystemOf<Kidnap.TownDutySystem>() is { } duties)
        {
            foreach (var f in world.Fixtures.All)
            {
                if (f.Id == sourceId || f.RecordId == sourceRecord || !FillSources.InMask(f.Function)) continue;
                if (inTown is not null && !inTown(f.Position) || !duties.Takes(f, unit) || !world.Fixtures.CanUse(world, cold, f)) continue;

                return true;
            }
        }

        foreach (var m in world.Machines.All)
        {
            if (!m.Data.IsStorage || m.Storage is null || !Candidate(world, m, sourceId, sourceRecord, inTown)) continue;
            if (!ProductionSystem.Stores(m, unit) || world.Buildings.Find(m.Site) is not { } site) continue;
            if (world.Fixtures.CanUse(world, cold, site.Owner, (Fixture?)null)) return true;
        }

        return false;
    }

    /// <summary>
    /// A machine of the mask in the town, complete (slot <c>0x228</c>, the complete byte <c>+0x160</c>; a town machine has no site and stands
    /// complete), other than the source and of another record.
    /// </summary>
    static bool Candidate(World world, Machine m, string sourceId, string sourceRecord, Func<Vector3, bool>? inTown)
    {
        if (m.Site == sourceId || m.Data.Id == sourceRecord || !FillSources.InMask(m.Data.Function)) return false;
        if (world.Buildings.Find(m.Site) is { Complete: false }) return false;

        return inTown is null || inTown(m.Position);
    }

    /// <summary>
    /// A machine's part of the count (<c>ProductionBuilding</c> slots <c>0x4a0</c> / <c>0x588</c> / <c>0x590</c>, <c>FUN_1402989b0</c>): 0 when
    /// its first input of <paramref name="record"/> is missing or full; that input's room when it is the machine's first input; null (the count is
    /// 5) when it is a later one, whose room the inventory test of the inputs before it finds.
    /// </summary>
    static int? InputWants(Machine m, string record)
    {
        int at = Array.FindIndex(m.Inputs, i => i.Takes(record));
        if (at < 0) return 0;

        int room = m.Inputs[at].Recipe.Capacity - m.Inputs[at].Count;
        if (room <= 0) return 0;

        return at == 0 ? room : null;
    }

    /// <summary>
    /// One unit for the character itself: the item is food and it carries none it would bite (<see cref="FeedSystem.CarriesFood"/>, the
    /// functions 3 and 15 of slot <c>0x2f0</c>).
    /// </summary>
    static int ForSelf(CharacterCold cold, ItemInstance unit)
    {
        if (unit.ItemFunction != FeedSystem.FoodFunction || cold is not { Race: { } race, Medical: { } med }) return 0;

        return FeedSystem.CarriesFood(cold, race, !med.HasStatsObject) ? 0 : 1;
    }
}
