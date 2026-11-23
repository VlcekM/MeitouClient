namespace Meitou.Simulation;

/// <summary>
/// The registrations of one code mod hook that is asked from the parallel phases (docs/modding.md "Registration surface"): each hook with the system
/// that registered it and that system's index, and the first tick each faulted in, kept as a lock-free minimum so every thread of that tick still
/// asks it and none does from the next tick. Faults enter the state hash under <paramref name="tag"/>; <paramref name="what"/> names the hook in the
/// log line.
/// </summary>
sealed class ParallelHooks<T>(uint tag, string what) where T : class
{
    /// <summary>
    /// The hooks in registration order.
    /// </summary>
    readonly List<(T Hook, ITickSystem System, int SystemIndex)> _hooks = [];

    /// <summary>
    /// Each hook's first faulted tick; <see cref="long.MaxValue"/> while it has none.
    /// </summary>
    long[] _faults = [];

    /// <summary>
    /// Whether any hook is registered.
    /// </summary>
    internal bool Active => _hooks.Count > 0;

    /// <summary>
    /// How many hooks are registered.
    /// </summary>
    internal int Count => _hooks.Count;

    /// <summary>
    /// Hook <paramref name="i"/>.
    /// </summary>
    internal T this[int i] => _hooks[i].Hook;

    /// <summary>
    /// Registers <paramref name="hook"/>, made by <paramref name="system"/> at <paramref name="systemIndex"/>. Serial, before the first tick.
    /// </summary>
    internal void Add(T hook, ITickSystem system, int systemIndex)
    {
        _hooks.Add((hook, system, systemIndex));
        Array.Resize(ref _faults, _hooks.Count);
        _faults[^1] = long.MaxValue;
    }

    /// <summary>
    /// Takes back the hooks from <paramref name="count"/> on (a registration that threw).
    /// </summary>
    internal void TruncateTo(int count)
    {
        _hooks.RemoveRange(count, _hooks.Count - count);
        Array.Resize(ref _faults, count);
    }

    /// <summary>
    /// Whether hook <paramref name="i"/> is still asked at <paramref name="tick"/> (it did not fault before it). Any thread.
    /// </summary>
    internal bool Asked(int i, long tick) => Volatile.Read(ref _faults[i]) >= tick;

    /// <summary>
    /// Hook <paramref name="i"/> threw <paramref name="e"/> at <paramref name="tick"/> (<see cref="Fault"/>), and its mod is told when it was built
    /// for another version of the game.
    /// </summary>
    internal void Threw(int i, long tick, Exception e)
    {
        SimulationExtensions.TellOutdated(_hooks[i].System, e);
        Fault(i, tick, $"{e.GetType().Name}: {e.Message}");
    }

    /// <summary>
    /// Keeps the earliest faulted tick of hook <paramref name="i"/> (concurrent callers may fault it at once) and logs the first fault.
    /// </summary>
    internal void Fault(int i, long tick, string fault)
    {
        long seen = Volatile.Read(ref _faults[i]);
        while (tick < seen)
        {
            long was = Interlocked.CompareExchange(ref _faults[i], tick, seen);
            if (was == seen)
            {
                if (seen == long.MaxValue) HookLog.Fault(_hooks[i].System, what, $"failed at tick {tick}, dropped from the next: {fault}");
                return;
            }

            seen = was;
        }
    }

    /// <summary>
    /// Adds the faulted hooks (their system index and first faulted tick) to the world's state hash; nothing while none has faulted.
    /// </summary>
    internal void HashFaults(ref StateHasher hash)
    {
        for (int i = 0; i < _faults.Length; i++)
        {
            if (_faults[i] == long.MaxValue) continue;

            hash.Add(tag);
            hash.Add(_hooks[i].SystemIndex);
            hash.Add(_faults[i]);
        }
    }
}

/// <summary>
/// The registrations of one code mod hook that runs in the serial phases only: a hook that faults is disabled for that world at once. The disabled
/// hooks enter the state hash under <paramref name="tag"/>; <paramref name="what"/> names the hook in the log line.
/// </summary>
sealed class SerialHooks<T>(uint tag, string what) where T : class
{
    /// <summary>
    /// The hooks in registration order, each with whether it is still called.
    /// </summary>
    readonly List<(T Hook, ITickSystem System, int SystemIndex, bool Enabled)> _hooks = [];

    /// <summary>
    /// The hooks still called, and the ones disabled after a fault.
    /// </summary>
    int _active, _faulted;

    /// <summary>
    /// Whether any hook is still called.
    /// </summary>
    internal bool Active => _active > 0;

    /// <summary>
    /// How many hooks are registered.
    /// </summary>
    internal int Count => _hooks.Count;

    /// <summary>
    /// Hook <paramref name="i"/>.
    /// </summary>
    internal T this[int i] => _hooks[i].Hook;

    /// <summary>
    /// Whether hook <paramref name="i"/> is still called.
    /// </summary>
    internal bool Enabled(int i) => _hooks[i].Enabled;

    /// <summary>
    /// Registers <paramref name="system"/> when it is a <typeparamref name="T"/>.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is not T hook) return;

        _hooks.Add((hook, system, systemIndex, true));
        _active++;
    }

    /// <summary>
    /// Hook <paramref name="i"/> threw <paramref name="e"/>: disabled (<see cref="Disable"/>), and its mod told when it was built for another version.
    /// </summary>
    internal void Threw(int i, Exception e)
    {
        SimulationExtensions.TellOutdated(_hooks[i].System, e);
        Disable(i, $"{e.GetType().Name}: {e.Message}");
    }

    /// <summary>
    /// Disables hook <paramref name="i"/> for this world and logs why; nothing for one disabled already.
    /// </summary>
    internal void Disable(int i, string fault)
    {
        var entry = _hooks[i];
        if (!entry.Enabled) return;

        _hooks[i] = entry with { Enabled = false };
        _active--;
        _faulted++;
        HookLog.Fault(entry.System, what, $"disabled for this world: {fault}");
    }

    /// <summary>
    /// Adds the disabled hooks to the world's state hash; nothing while none has faulted.
    /// </summary>
    internal void HashFaults(ref StateHasher hash)
    {
        if (_faulted == 0) return;

        hash.Add(tag);
        hash.Add(_faulted);
        foreach (var entry in _hooks)
        {
            if (!entry.Enabled) hash.Add(entry.SystemIndex);
        }
    }
}

/// <summary>
/// The log lines of faulted code mod hooks.
/// </summary>
static class HookLog
{
    /// <summary>
    /// A faulted hook's log line: the mod and the name it registered the system under, as the mods' other log lines; else the system's type.
    /// </summary>
    internal static void Fault(ITickSystem system, string what, string text)
    {
        if (SimulationExtensions.FactoryOf(system) is { } factory)
            Console.Error.WriteLine($"mod       {factory.Owner}: {what} '{factory.Name}' {text}");
        else
            Console.Error.WriteLine($"{what} '{system.GetType().FullName}' {text}");
    }
}
