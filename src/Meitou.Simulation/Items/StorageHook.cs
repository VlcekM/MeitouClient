namespace Meitou.Simulation.Items;

/// <summary>
/// One system asked about a storage the game picks for a character (<see cref="IStorageFilter"/>, <see cref="IStoragePriority"/>) and the tick it
/// first threw in. One that throws goes on being asked for the rest of that tick, so the tick's answers do not depend on which worker threw first,
/// and is not asked from the next tick on.
/// </summary>
sealed class StorageHook<T>(T hook, int systemIndex, string kind) where T : ITickSystem
{
    /// <summary>
    /// The tick the hook first threw in; -1 while it never has.
    /// </summary>
    long _faultTick = -1;

    /// <summary>
    /// The system asked.
    /// </summary>
    public T Hook { get; } = hook;

    /// <summary>
    /// The hook's system index (what the hash records).
    /// </summary>
    public int SystemIndex { get; } = systemIndex;

    /// <summary>
    /// Whether the hook has thrown.
    /// </summary>
    public bool Faulted => FaultTick >= 0;

    /// <summary>
    /// The tick the hook first threw in; -1 while it never has.
    /// </summary>
    public long FaultTick => Interlocked.Read(ref _faultTick);

    /// <summary>
    /// Whether the hook is asked at <paramref name="tick"/>: never threw, or threw in this tick.
    /// </summary>
    public bool AskedAt(long tick)
    {
        long fault = Interlocked.Read(ref _faultTick);
        return fault < 0 || fault == tick;
    }

    /// <summary>
    /// Records the first throw at <paramref name="tick"/> with its log line; later throws in the same tick are not logged again.
    /// </summary>
    public void Fault(long tick, Exception e)
    {
        if (Interlocked.CompareExchange(ref _faultTick, tick, -1) != -1) return;

        string fault = $"{e.GetType().Name}: {e.Message}";
        SimulationExtensions.TellOutdated(Hook, e);
        if (SimulationExtensions.FactoryOf(Hook) is { } factory)
            Console.Error.WriteLine($"mod       {factory.Owner}: {kind} '{factory.Name}' disabled for this world: {fault}");
        else
            Console.Error.WriteLine($"{kind} '{Hook.GetType().FullName}' disabled for this world: {fault}");
    }
}
