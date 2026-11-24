using Meitou.Data.World;

namespace Meitou.Simulation.Ai;

/// <summary>
/// Operator slots a code mod holds for its own workers (docs/modding.md "Workplace slots"): counted with the AI's, so the town workers' claim
/// leaves them alone. The AI's own taken count (<see cref="AiSystem"/>'s workplace slots) is shared, so a hold counts at once.
/// </summary>
internal sealed class ModWorkHolds(TownPlaceIndex places, Dictionary<TownBuilding, int> taken, Func<TownBuilding, bool> hasFreeSlot)
{
    /// <summary>
    /// The mods' held slots by the worker's slot: the worker and the workplace's key (hashed while any).
    /// </summary>
    readonly SortedDictionary<int, (CharacterId Worker, string Building)> _held = [];

    #region Holds

    /// <summary>
    /// Holds an operator slot of workplace <paramref name="building"/> for <paramref name="worker"/> (<see cref="AiSystem.HoldWorkSlot"/>): false,
    /// holding nothing new, for a worker not alive, a building that is no workplace the AI looked at, or one with no free slot.
    /// </summary>
    public bool Hold(World world, string building, CharacterId worker)
    {
        ArgumentNullException.ThrowIfNull(building);
        ThrowIfParallel(world);
        if (!Living(world, worker) || places.FindWorkplace(building) is not { } b) return false;
        if (_held.TryGetValue(worker.Slot, out var held) && held.Worker == worker && held.Building == building)
            return true;
        if (!hasFreeSlot(b)) return false;

        Release(world, worker);
        _held[worker.Slot] = (worker, building);
        taken[b] = taken.GetValueOrDefault(b) + 1;
        return true;
    }

    /// <summary>
    /// Lets go of the slot <see cref="Hold"/> holds for <paramref name="worker"/>; false when it holds none.
    /// </summary>
    public bool Release(World world, CharacterId worker)
    {
        ThrowIfParallel(world);
        if (!_held.TryGetValue(worker.Slot, out var held) || held.Worker != worker) return false;

        _held.Remove(worker.Slot);
        if (places.FindWorkplace(held.Building) is { } b) taken[b] = taken.GetValueOrDefault(b) - 1;
        return true;
    }

    /// <summary>
    /// Whether the mods hold no slot.
    /// </summary>
    public bool None => _held.Count == 0;

    /// <summary>
    /// Throws in a parallel phase: the holds are serial.
    /// </summary>
    static void ThrowIfParallel(World world)
    {
        if (world.Characters.Phase == TablePhase.Parallel)
            throw new InvalidOperationException("Work slots are held in the serial phases only.");
    }

    #endregion

    #region Cadence pass

    /// <summary>
    /// The cadence pass's reservation of the held slots (<see cref="AiSystem"/>'s work reservation): a hold whose worker left the world or died goes;
    /// the others count on their workplace while it is one.
    /// </summary>
    public void Reserve(World world)
    {
        if (None) return;

        List<int>? gone = null;
        foreach (var (slot, held) in _held)
        {
            if (!Living(world, held.Worker)) (gone ??= []).Add(slot);
            else if (places.FindWorkplace(held.Building) is { } b) taken[b] = taken.GetValueOrDefault(b) + 1;
        }

        if (gone is not null) foreach (int slot in gone) _held.Remove(slot);
    }

    /// <summary>
    /// Hashes the held slots while any.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        if (None) return;

        hasher.Add(0x4D6F6457);
        foreach (var (_, held) in _held)
        {
            hasher.Add(held.Worker);
            hasher.Add(Rng.StableHash(held.Building));
        }
    }

    /// <summary>
    /// Whether <paramref name="worker"/> is in the world and has not died.
    /// </summary>
    static bool Living(World world, CharacterId worker) =>
        world.Characters.IsAlive(worker) && world.Characters.Cold(worker.Slot) is { DiedTick: < 0 };

    #endregion
}
