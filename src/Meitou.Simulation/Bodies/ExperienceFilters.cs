using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// The world's systems that filter experience gains (<see cref="IExperienceFilter"/>). Gains happen in the parallel phases too, so a fault is kept
/// as the tick it happened: the filter is still asked for the rest of that tick and not from the next, and the cut falls on a tick boundary
/// whatever the threads did. A character's stats are bound to the world and its id while it is in the world's table (bound as it enters, cleared as
/// it leaves, <see cref="CharacterWatch"/>), so a gain knows whose it is.
/// </summary>
sealed class ExperienceFilters
{
    /// <summary>
    /// The filtering systems, each with its system index.
    /// </summary>
    List<(IExperienceFilter Filter, int SystemIndex)>? _filters;
    /// <summary>
    /// Each filter's fault tick (<see cref="long.MaxValue"/>: none), in <see cref="_filters"/>' order.
    /// </summary>
    long[] _faults = [];

    /// <summary>
    /// Whether any filter is registered; without one no stats are bound and no gain is filtered.
    /// </summary>
    internal bool Active => _filters is not null;

    /// <summary>
    /// Registers <paramref name="system"/> when it is a filter.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is not IExperienceFilter filter) return;

        (_filters ??= []).Add((filter, systemIndex));
        Array.Resize(ref _faults, _filters.Count);
        _faults[^1] = long.MaxValue;
    }

    /// <summary>
    /// Adds the faulted filters and their fault ticks to the world's state hash; nothing while none has faulted.
    /// </summary>
    internal void HashFaults(ref StateHasher hash)
    {
        for (int i = 0; i < _faults.Length; i++)
        {
            if (_faults[i] == long.MaxValue) continue;

            hash.Add(0x58504649);
            hash.Add(_filters![i].SystemIndex);
            hash.Add(_faults[i]);
        }
    }

    /// <summary>
    /// Binds the stats of every character already in the world's table (<see cref="CharacterStats.BindExperience"/>), when the first filter is
    /// registered; the ones entering later are bound as they enter. Serial, before the first tick.
    /// </summary>
    internal static void BindAll(World world)
    {
        var table = world.Characters;
        for (int slot = 0; slot < table.HighWater; slot++)
        {
            if (table.Cold(slot)?.Stats is { } stats)
                stats.BindExperience(world, new CharacterId(slot, table.Next[slot].Generation));
        }
    }

    /// <summary>
    /// Clears the binding of every character's stats in the world's table (the world is discarded), so no stats object keeps it alive.
    /// </summary>
    internal static void UnbindAll(World world)
    {
        var table = world.Characters;
        for (int slot = 0; slot < table.HighWater; slot++) table.Cold(slot)?.Stats?.UnbindExperience();
    }

    /// <summary>
    /// <paramref name="amount"/> after every filter not faulted before this tick, in system order, until one answers 0. Any thread.
    /// </summary>
    internal float Pass(World world, CharacterId character, StatsEnumerated skill, float amount, ExperienceSource source)
    {
        var filters = _filters!;
        long tick = world.Tick;
        for (int i = 0; i < filters.Count && amount > 0; i++)
        {
            if (Volatile.Read(ref _faults[i]) < tick) continue;

            amount = Ask(world, i, new ExperienceGain(tick, character, skill, amount, source));
        }

        return amount;
    }

    /// <summary>
    /// One filter's amount; the amount as it came when the filter throws or answers one that is negative or not finite, which faults it.
    /// </summary>
    float Ask(World world, int i, in ExperienceGain gain)
    {
        string fault;
        try
        {
            float amount = _filters![i].Filter.Gain(world, gain);
            if (float.IsFinite(amount) && amount >= 0) return amount;

            fault = $"amount {amount} is negative or not a finite number";
        }
        catch (Exception e)
        {
            fault = $"{e.GetType().Name}: {e.Message}";
            SimulationExtensions.TellOutdated(_filters![i].Filter, e);
        }

        Fault(i, gain.Tick, fault);
        return gain.Amount;
    }

    /// <summary>
    /// Records filter <paramref name="i"/>'s fault at <paramref name="tick"/> (the earliest wins; threads of one tick all write the same tick) and
    /// logs the first.
    /// </summary>
    void Fault(int i, long tick, string fault)
    {
        long seen = Volatile.Read(ref _faults[i]);
        while (tick < seen)
        {
            long was = Interlocked.CompareExchange(ref _faults[i], tick, seen);
            if (was == seen)
            {
                if (seen == long.MaxValue) LogFault(_filters![i].Filter, tick, fault);
                return;
            }

            seen = was;
        }
    }

    /// <summary>
    /// A faulted filter's log line: the mod and the name it registered the system under, as the mods' other log lines; else its type.
    /// </summary>
    static void LogFault(IExperienceFilter filter, long tick, string fault)
    {
        string what = $"failed at tick {tick}, dropped from the next: {fault}";
        if (SimulationExtensions.FactoryOf(filter) is { } factory)
            Console.Error.WriteLine($"mod       {factory.Owner}: experience filter '{factory.Name}' {what}");
        else
            Console.Error.WriteLine($"experience filter '{filter.GetType().FullName}' {what}");
    }
}
