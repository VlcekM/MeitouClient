namespace Meitou.Simulation.Ai;

/// <summary>
/// Operator slots a code mod holds for its own workers (docs/modding.md "Workplace slots"): counted with the AI's, so the town workers' claim
/// leaves them alone.
/// </summary>
public sealed partial class AiSystem
{
    /// <summary>
    /// The mods' held slots by the worker's slot: the worker and the workplace's key (hashed while any).
    /// </summary>
    readonly SortedDictionary<int, (CharacterId Worker, string Building)> _modWork = [];

    /// <summary>
    /// Holds an operator slot of workplace <paramref name="building"/> (<see cref="TownBuilding.Key"/>, as <see cref="WorkSlots"/>) for the mod's
    /// worker <paramref name="worker"/>: counted in <see cref="WorkplaceSlots.Held"/> at once and in every package cadence pass after, so the AI's
    /// workers do not take it (the original's hired operator holds the machine's slot). One hold a worker: holding another building moves it; the
    /// same one again changes nothing. False, holding nothing new, for a worker not alive, a building that is no workplace the AI looked at, or
    /// one with no free slot. Released by <see cref="ReleaseWorkSlot"/> or when the worker leaves the world or dies. Not saved: a mod holds again
    /// after a load (<c>IWorldReady</c>). Serial phases only (throws in a parallel one).
    /// </summary>
    public bool HoldWorkSlot(World world, string building, CharacterId worker)
    {
        ArgumentNullException.ThrowIfNull(building);
        if (world.Characters.Phase == TablePhase.Parallel) throw new InvalidOperationException("Work slots are held in the serial phases only.");
        if (!Living(world, worker) || _townPlaces.FindWorkplace(building) is not { } b) return false;
        if (_modWork.TryGetValue(worker.Slot, out var held) && held.Worker == worker && held.Building == building) return true;
        if (!HasFreeSlot(b)) return false;

        ReleaseWorkSlot(world, worker);
        _modWork[worker.Slot] = (worker, building);
        _workTaken[b] = _workTaken.GetValueOrDefault(b) + 1;
        return true;
    }

    /// <summary>
    /// Lets go of the slot <see cref="HoldWorkSlot"/> holds for <paramref name="worker"/>; false when it holds none. Serial phases only.
    /// </summary>
    public bool ReleaseWorkSlot(World world, CharacterId worker)
    {
        if (world.Characters.Phase == TablePhase.Parallel) throw new InvalidOperationException("Work slots are held in the serial phases only.");
        if (!_modWork.TryGetValue(worker.Slot, out var held) || held.Worker != worker) return false;

        _modWork.Remove(worker.Slot);
        if (_townPlaces.FindWorkplace(held.Building) is { } b) _workTaken[b] = _workTaken.GetValueOrDefault(b) - 1;
        return true;
    }

    /// <summary>
    /// The cadence pass's reservation of the mods' held slots (<see cref="ReserveWork"/>): a hold whose worker left the world or died goes; the others
    /// count on their workplace while it is one.
    /// </summary>
    void ReserveModWork(World world)
    {
        if (_modWork.Count == 0) return;

        List<int>? gone = null;
        foreach (var (slot, held) in _modWork)
        {
            if (!Living(world, held.Worker)) (gone ??= []).Add(slot);
            else if (_townPlaces.FindWorkplace(held.Building) is { } b) _workTaken[b] = _workTaken.GetValueOrDefault(b) + 1;
        }

        if (gone is not null) foreach (int slot in gone) _modWork.Remove(slot);
    }

    /// <summary>
    /// Whether <paramref name="worker"/> is in the world and has not died.
    /// </summary>
    static bool Living(World world, CharacterId worker) =>
        world.Characters.IsAlive(worker) && world.Characters.Cold(worker.Slot) is { DiedTick: < 0 };

    /// <summary>
    /// Hashes the mods' held slots while any.
    /// </summary>
    void HashModWork(ref StateHasher hasher)
    {
        if (_modWork.Count == 0) return;

        hasher.Add(0x4D6F6457);
        foreach (var (_, held) in _modWork)
        {
            hasher.Add(held.Worker);
            hasher.Add(Rng.StableHash(held.Building));
        }
    }
}
