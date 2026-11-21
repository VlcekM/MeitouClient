using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Production;

/// <summary>
/// What a source of row 94's finder is (<see cref="FillSources.Find"/>).
/// </summary>
public enum FillSourceKind : byte
{
    /// <summary>
    /// A loose item on the ground.
    /// </summary>
    Loose,
    /// <summary>
    /// A storage holding the item: a town's container fixture or a player storage machine.
    /// </summary>
    Storage,
    /// <summary>
    /// A machine whose output holds the item.
    /// </summary>
    Producer,
}

/// <summary>
/// A source an input's item is fetched from: its kind, where it is, the record, the storage's id (a machine's site id, else a container's fixture
/// id), the loose item's <see cref="GroundItems"/> id (-1: none) and the producer's town building.
/// </summary>
public readonly record struct FillSource(FillSourceKind Kind, Vector3 Position, string Record, string? Store = null, int Item = -1,
    TownBuilding? Producer = null);

/// <summary>
/// The finder of row 94 Try filling machine (<c>FUN_14059d9b0</c> -> <c>FUN_14059d000</c>, <b>Verified</b>; docs/game/buildings-production.md
/// "NPC town machines"), for whoever plans it. Pure reads of state the serial phases write, safe in the parallel think.
/// </summary>
internal static class FillSources
{
    /// <summary>
    /// For each input of <paramref name="target"/>'s <see cref="Machine.FillOrder"/>, the first source of its item (the first input with one
    /// decides): a loose item (<see cref="LooseSource.Nearest"/>), else the nearest storage holding it, else the nearest producer; null when no
    /// input has one. The buildings are those of the character's town (<see cref="Locks.FixtureRegistry.RegisteredTownCandidates"/>) of the query
    /// mask's functions (<see cref="InMask"/>), within <see cref="ConstructionSystem.SourceRangeSquared"/> of <paramref name="at"/> (3D, inclusive).
    /// Engine choices: the exact record (the original lets any record of the same type stand in for a non-ITEM one); the producers are the work
    /// town's workplaces.
    /// </summary>
    public static FillSource? Find(World world, PopulationData? data, Machine target, CharacterCold cold, Vector3 at,
        IEnumerable<TownBuilding> workplaces)
    {
        var inTown = data is not null ? world.Fixtures.RegisteredTownCandidates(world, data, cold, at) : null;
        foreach (var input in target.FillOrder)
        {
            string record = input.Item;
            if (LooseSource.Nearest(world, data, cold, at, record) is { } loose)
                return new FillSource(FillSourceKind.Loose, loose.Position, record, Item: loose.Id);

            if (Storage(world, cold, at, record, inTown) is { } stored) return stored;
            if (Producer(world, target, at, record, workplaces) is { } made) return made;
        }

        return null;
    }

    /// <summary>
    /// The building functions of the finder's query mask <c>0x4002436</c>, one bit per function (<b>Verified</b> by the other finders' masks: the
    /// bed search's <c>0x40</c> is function 6, the cage finders' <c>0x100</c> function 8): 1 mines, wells and farms, 2 resource storage, 4 refinery,
    /// 5 generator, 10 crafting, 13 general storage, 26 rain collector.
    /// </summary>
    internal static bool InMask(int function) => function is MachineData.MineFunction or MachineData.ResourceStorageFunction or MachineData.RefineryFunction
        or MachineData.GeneratorFunction or CraftBench.CraftingFunction or MachineData.GeneralStorageFunction or MachineData.RainCollectorFunction;

    /// <summary>
    /// The storages of the mask (functions 2 and 13): the buildings whose vfunc <c>0x3b0</c> is null (<c>StorageBuilding</c>), searched before the
    /// producers.
    /// </summary>
    static bool IsStorage(int function) => function is MachineData.ResourceStorageFunction or MachineData.GeneralStorageFunction;

    /// <summary>
    /// The nearest storage holding <paramref name="record"/> that the character may use (<c>canIUseThisBuilding</c>,
    /// <see cref="Locks.FixtureRegistry.CanUse(World, CharacterCold, int, Locks.Fixture?)"/>: a storage, holding an inventory, only of its own
    /// faction) and the storage filters let it take the item from (<see cref="Items.IStorageFilter"/>): a container fixture, then a complete player
    /// storage machine; null with none.
    /// </summary>
    static FillSource? Storage(World world, CharacterCold cold, Vector3 at, string record, Func<Vector3, bool>? inTown)
    {
        FillSource? best = null;
        float bestD = float.MaxValue;
        foreach (var f in world.Fixtures.All)
        {
            if (!IsStorage(f.Function) || f.Contents is not { } box || !ProductionSystem.StorageHolds(box, record)) continue;

            float d = Vector3.DistanceSquared(f.Position, at);
            if (!Closer(d, bestD) || inTown is not null && !inTown(f.Position) || !world.Fixtures.CanUse(world, cold, f)) continue;
            if (!world.StorageAllows(cold, f.Id, Items.StorageUse.Take, record)) continue;

            (best, bestD) = (new FillSource(FillSourceKind.Storage, f.Position, record, f.Id), d);
        }

        foreach (var m in world.Machines.All)
        {
            if (!m.Data.IsStorage || m.Storage is not { } st || !ProductionSystem.StorageHolds(st, record)) continue;
            if (world.Buildings.Find(m.Site) is not { Complete: true } site) continue;

            float d = Vector3.DistanceSquared(site.Position, at);
            if (!Closer(d, bestD) || inTown is not null && !inTown(site.Position)) continue;
            if (!world.Fixtures.CanUse(world, cold, site.Owner, (Locks.Fixture?)null)) continue;
            if (!world.StorageAllows(cold, m.Site, Items.StorageUse.Take, record)) continue;

            (best, bestD) = (new FillSource(FillSourceKind.Storage, site.Position, record, m.Site), d);
        }

        return best;
    }

    /// <summary>
    /// The nearest workplace machine of another record than <paramref name="target"/>'s (<c>FUN_14059d000</c> skips the excluded target's record)
    /// whose output holds <paramref name="record"/> and has some; null with none.
    /// </summary>
    static FillSource? Producer(World world, Machine target, Vector3 at, string record, IEnumerable<TownBuilding> workplaces)
    {
        FillSource? best = null;
        float bestD = float.MaxValue;
        foreach (var b in workplaces)
        {
            if (ProductionSystem.TownWorkOf(world, b) is not { Output: { Count: > 0 } o } other || other.Data.Id == target.Data.Id) continue;
            if (o.Recipe.Item != record || !InMask(other.Data.Function)) continue;

            float d = Vector3.DistanceSquared(b.Position, at);
            if (Closer(d, bestD)) (best, bestD) = (new FillSource(FillSourceKind.Producer, b.Position, record, Producer: b), d);
        }

        return best;
    }

    /// <summary>
    /// Whether a squared distance is within the finder's range (the bound kept) and nearer than the best so far (a tie keeps the first).
    /// </summary>
    static bool Closer(float d, float bestD) => d <= ConstructionSystem.SourceRangeSquared && d < bestD;
}
