using System.Collections.Concurrent;
using Meitou.Content;

namespace Meitou.Simulation;

/// <summary>
/// The world's tick systems made by a mod (<see cref="SimulationExtensions.FactoryOf"/>) and the ones stopped after a fault (docs/modding.md
/// "Tick systems"): a phase of a mod system that throws is caught and logged once with the mod and the system's name, the player gets a notice
/// (<see cref="TryTakeNotice"/>), the system's tick phases stop from the next serial step on (a parallel phase's other partitions still finish
/// that phase; its effects of that tick are not applied), and the fault enters the state hash. Its task handlers, its registrations (task handlers,
/// chooser goals and jobs) and its world ready fault it too (<see cref="Ai.ModTaskHandler"/>, <see cref="Fault"/>); its other hooks (observers,
/// filters, the goals and job contributors it registered) keep their own rules. A mod built for another version of the game gets the one notice of
/// <see cref="CodeModFaults.Tell"/> instead. The standard systems are called as they are, never caught.
/// </summary>
sealed class ModSystemFaults
{
    /// <summary>
    /// A system still running.
    /// </summary>
    const int Running = 0;

    /// <summary>
    /// A system that threw: its serial phases are skipped at once; a parallel phase under way finishes its other partitions.
    /// </summary>
    const int Faulted = 1;

    /// <summary>
    /// A system that threw before the last serial step: it runs no phase.
    /// </summary>
    const int Stopped = 2;

    /// <summary>
    /// Each system's factory by its index in the world; null for a standard system.
    /// </summary>
    readonly List<SystemFactory?> _factories = [];

    /// <summary>
    /// Each system's state by index (<see cref="Running"/>, <see cref="Faulted"/>, <see cref="Stopped"/>); set from the worker threads.
    /// </summary>
    int[] _states = [];

    /// <summary>
    /// The systems that faulted, for the state hash.
    /// </summary>
    int _faults;

    /// <summary>
    /// The notices for the player not yet taken.
    /// </summary>
    readonly ConcurrentQueue<string> _notices = new();

    /// <summary>
    /// Notes the world's next system, at index <see cref="_factories"/>.Count.
    /// </summary>
    public void Add(ITickSystem system)
    {
        _factories.Add(SimulationExtensions.FactoryOf(system));
        Array.Resize(ref _states, _factories.Count);
    }

    /// <summary>
    /// Whether system <paramref name="index"/> was made by a mod, so its phases are caught.
    /// </summary>
    public bool IsMod(int index) => _factories[index] is not null;

    /// <summary>
    /// At a serial step (a tick's start, before and after the parallel phases): the systems that faulted stop.
    /// </summary>
    public void Settle()
    {
        if (_faults == 0) return;

        for (int k = 0; k < _states.Length; k++)
        {
            if (_states[k] == Faulted) _states[k] = Stopped;
        }
    }

    /// <summary>
    /// Runs one serial phase of mod system <paramref name="index"/> (<paramref name="body"/> with <paramref name="argument"/>), unless it has
    /// faulted; a throw is a fault (<see cref="Fault"/>).
    /// </summary>
    public void Run<T>(int index, string phase, ITickSystem system, World world, T argument, Action<ITickSystem, World, T> body)
    {
        if (Volatile.Read(ref _states[index]) != Running) return;

        try
        {
            body(system, world, argument);
        }
        catch (Exception e)
        {
            Fault(index, phase, e);
        }
    }

    /// <summary>
    /// <paramref name="body"/>, a parallel phase of mod system <paramref name="index"/> over one partition, caught as <see cref="Run{T}"/> does.
    /// It skips only a system stopped before the phase, so whether a partition runs never depends on another's timing.
    /// </summary>
    public Action<int> Guard(int index, string phase, Action<int> body) => partition =>
    {
        if (Volatile.Read(ref _states[index]) == Stopped) return;

        try
        {
            body(partition);
        }
        catch (Exception e)
        {
            Fault(index, phase, e);
        }
    };

    /// <summary>
    /// Whether system <paramref name="index"/> was stopped after a fault. Changes only at a serial step (<see cref="Settle"/>), so a parallel phase
    /// reads one answer on every thread.
    /// </summary>
    public bool IsStopped(int index) => (uint)index < (uint)_states.Length && Volatile.Read(ref _states[index]) == Stopped;

    /// <summary>
    /// Queues a notice for the player about another mod fault (a mod's goal, <see cref="Ai.ModGoal"/>, or job contributor); the host shows it once.
    /// </summary>
    public void Notify(string notice) => _notices.Enqueue(notice);

    /// <summary>
    /// The next notice for the player about a mod system that stopped, if any; the host shows it once.
    /// </summary>
    public bool TryTakeNotice(out string notice) => _notices.TryDequeue(out notice!);

    /// <summary>
    /// Adds the faulted systems to the world's state hash: a stopped system's tick phases no longer change the world. Without a fault nothing is
    /// added.
    /// </summary>
    public void HashFaults(ref StateHasher hash)
    {
        if (Volatile.Read(ref _faults) == 0) return;

        hash.Add(0x4D4F4453);
        hash.Add(_faults);

        for (int k = 0; k < _states.Length; k++)
        {
            if (_states[k] != Running) hash.Add(k);
        }
    }

    /// <summary>
    /// Marks mod system <paramref name="index"/> faulted the first time one of its phases or hooks (a task handler, a registration, its world ready)
    /// throws, with a log line naming the mod, the system and the phase, and a notice; later throws are not logged again.
    /// </summary>
    public void Fault(int index, string phase, Exception e)
    {
        if (Interlocked.CompareExchange(ref _states[index], Faulted, Running) != Running) return;

        Interlocked.Increment(ref _faults);

        var factory = _factories[index]!;
        Console.Error.WriteLine($"mod       {factory.Owner}: system '{factory.Name}' stopped for this world, its {phase} threw: {e}");
        if (!CodeModFaults.Tell(factory.Owner, e))
            _notices.Enqueue($"Mod {factory.Owner}: its {factory.Name} stopped after an error ({e.GetType().Name}); the rest of the game goes on.");
    }
}
