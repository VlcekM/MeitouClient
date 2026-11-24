namespace Meitou.Simulation.Items;

/// <summary>
/// The world's systems asked how much a character prefers a storage the game picks for it (<see cref="IStoragePriority"/>). Asked from worker
/// threads too: one that throws is answered as <see cref="StoragePriority.Normal"/> for the rest of the tick it threw in and is not asked from the
/// next tick on.
/// </summary>
sealed class StoragePriorities
{
    /// <summary>
    /// The priority systems in system order, each with its system index.
    /// </summary>
    List<StorageHook<IStoragePriority>>? _hooks;

    /// <summary>
    /// Whether any priority system is registered; a search asks nothing without one.
    /// </summary>
    internal bool Active => _hooks is not null;

    /// <summary>
    /// Registers <paramref name="system"/> when it is a priority system.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is IStoragePriority hook) (_hooks ??= []).Add(new StorageHook<IStoragePriority>(hook, systemIndex, "storage priority"));
    }

    /// <summary>
    /// The storage's priority: <see cref="StoragePriority.Forbidden"/> when any system still asked answers it, else the first answer other than
    /// <see cref="StoragePriority.Normal"/> in system order; Normal without one. An answer outside the levels counts as Normal. Thread-safe.
    /// </summary>
    internal StoragePriority Of(World world, CharacterId who, string storage, StorageUse use, string? record)
    {
        if (_hooks is not { } hooks) return StoragePriority.Normal;

        var first = StoragePriority.Normal;
        foreach (var entry in hooks)
        {
            var answer = Ask(world, entry, who, storage, use, record);
            if (answer == StoragePriority.Forbidden) return answer;
            if (first == StoragePriority.Normal) first = answer;
        }

        return first;
    }

    /// <summary>
    /// Adds the priority systems that threw to the world's state hash. Successful ones add nothing.
    /// </summary>
    internal void HashFaults(ref StateHasher hash)
    {
        if (_hooks is not { } hooks || !hooks.Any(h => h.Faulted)) return;

        hash.Add(0x53505249);
        foreach (var entry in hooks)
        {
            if (!entry.Faulted) continue;

            hash.Add(entry.SystemIndex);
            hash.Add(entry.FaultTick);
        }
    }

    /// <summary>
    /// One system's answer: Normal when it is no longer asked, throws, or answers outside the levels.
    /// </summary>
    static StoragePriority Ask(World world, StorageHook<IStoragePriority> entry, CharacterId who, string storage, StorageUse use, string? record)
    {
        if (!entry.AskedAt(world.Tick)) return StoragePriority.Normal;

        try
        {
            var answer = entry.Hook.Priority(world, who, storage, use, record);
            return Enum.IsDefined(answer) ? answer : StoragePriority.Normal;
        }
        catch (Exception e)
        {
            entry.Fault(world.Tick, e);
            return StoragePriority.Normal;
        }
    }
}
