using System.Collections.Concurrent;
using System.Diagnostics;

namespace Meitou.Data.Particles;

/// <summary>
/// The effect groups of one weather being made (docs/formats/particle-universe.md "Weather changes"). A warm-up is two halves: the schedule, which
/// places the units (<see cref="EffectGroup.BeginWarm"/>), and the simulation of the time that queued up, up to 40 s of every unit
/// (<see cref="EffectGroup.SimulateSerial"/>). <see cref="Begin"/> makes the groups on the calling thread and leaves both to the caller
/// (<see cref="SimulateNow"/>: the first groups of a view); <see cref="BeginInBackground"/> makes the groups there, which is a few microseconds, and
/// does both halves on a thread of its own, with the ground height of a snapshot (<see cref="EffectWorld.Frozen"/>). Until <see cref="Ready"/> nobody
/// else may touch the groups' state (units, particles, world): the list <see cref="Groups"/> itself is made before the worker starts and never
/// changes, so the render thread may read it, <see cref="Source"/> and each group's immutable <see cref="EffectGroup.System"/> (the renderer preloads
/// the textures from them), and nothing else.
/// </summary>
public sealed class EffectSet
{
    /// <summary>
    /// The longest warm-up a group gets of its own accord (seconds).
    /// </summary>
    public const float MaxAutoPrewarm = 40;

    /// <summary>
    /// The groups made for <see cref="Source"/>; filled before the warm-up starts and never changed after.
    /// </summary>
    readonly List<EffectGroup> _groups = [];
    /// <summary>
    /// The entries no group was made for.
    /// </summary>
    readonly List<WeatherEffectEntry> _skipped = [];
    /// <summary>
    /// Stops the background warm-up between two steps.
    /// </summary>
    readonly CancellationTokenSource _cancel = new();
    /// <summary>
    /// The world the groups are rebound to once a background warm-up is done; null for a set made on the calling thread.
    /// </summary>
    EffectWorld? _live;
    /// <summary>
    /// The warm-up asked for; null is each system's longest particle life up to <see cref="MaxAutoPrewarm"/>.
    /// </summary>
    float? _prewarmSeconds;
    /// <summary>
    /// Where a background warm-up places the units.
    /// </summary>
    EffectCamera _camera;
    /// <summary>
    /// Set once the simulation finished (or was cancelled or failed); read across threads.
    /// </summary>
    volatile bool _ready;
    /// <summary>
    /// What the background warm-up threw, if anything.
    /// </summary>
    volatile Exception? _failure;

    /// <summary>
    /// A set with no groups yet for <paramref name="source"/>.
    /// </summary>
    EffectSet(WeatherEffectInput source) => Source = source;

    /// <summary>
    /// The weather this set was made for.
    /// </summary>
    public WeatherEffectInput Source { get; }
    /// <summary>
    /// The groups made: the list is made before the worker starts and never changes, so the render thread may read it before <see cref="Ready"/>.
    /// </summary>
    public IReadOnlyList<EffectGroup> Groups => _groups;
    /// <summary>
    /// The entries that have no group (a type that is not a weather effect, or an unknown particle system).
    /// </summary>
    public IReadOnlyList<WeatherEffectEntry> Skipped => _skipped;
    /// <summary>
    /// The simulation finished (or was cancelled): the set may be used (<see cref="Failure"/> null and not cancelled) or dropped.
    /// </summary>
    public bool Ready => _ready;
    /// <summary>
    /// Whether <see cref="Cancel"/> was called.
    /// </summary>
    public bool Cancelled => _cancel.IsCancellationRequested;
    /// <summary>
    /// What went wrong on the background thread, if anything; the set is then not to be used.
    /// </summary>
    public Exception? Failure => _failure;

    /// <summary>
    /// Makes a group for each of the weather's effects that has one, in order, the n-th taking <paramref name="seed"/> plus n; the others go to
    /// <see cref="Skipped"/>.
    /// </summary>
    EffectSet Make(ParticleLibrary library, int seed, EffectWorld world)
    {
        int i = 0;
        foreach (var entry in Source.Effects)
        {
            if (EffectGroups.Create(entry, library, seed + i++, world) is { } group) _groups.Add(group);
            else _skipped.Add(entry);
        }
        return this;
    }

    /// <summary>
    /// The warm-up of <paramref name="group"/>: the one asked for, else its system's longest particle life up to <see cref="MaxAutoPrewarm"/>.
    /// </summary>
    float WarmSeconds(EffectGroup group) => _prewarmSeconds ?? Math.Min(ParticleSimulation.LongestLife(group.System), MaxAutoPrewarm);

    /// <summary>
    /// Makes the groups of <paramref name="weather"/> and runs their schedules for the warm-up on the calling thread:
    /// <paramref name="prewarmSeconds"/>, or by default each system's longest particle life up to <see cref="MaxAutoPrewarm"/>. The first group takes
    /// <paramref name="seed"/>, the next one more. Simulate the queued time with <see cref="SimulateNow"/>.
    /// </summary>
    public static EffectSet Begin(WeatherEffectInput weather, ParticleLibrary library, int seed, EffectWorld world,
        float? prewarmSeconds, in EffectCamera camera)
    {
        var set = new EffectSet(weather) { _prewarmSeconds = prewarmSeconds }.Make(library, seed, world);
        foreach (var group in set._groups) group.BeginWarm(set.WarmSeconds(group), camera, weather);
        if (set._groups.Count == 0) set._ready = true;   // nothing to simulate
        return set;
    }

    /// <summary>
    /// Makes the groups on the calling thread (the render thread: the world's ground height is frozen here) and queues them for
    /// <paramref name="warmer"/>'s thread, which runs their schedules and simulates the warm-up, the units one after the other: the frame keeps its
    /// cores and the pool its workers, and the particles are there some tenths of a second to seconds later (a weather has up to a few hundred units'
    /// worth of time to run). <see cref="Cancel"/> stops it between two steps. The groups belong to <paramref name="world"/> from <see cref="Ready"/>
    /// on.
    /// </summary>
    public static EffectSet BeginInBackground(WeatherEffectInput weather, ParticleLibrary library, int seed, EffectWorld world,
        float? prewarmSeconds, in EffectCamera camera, EffectWarmer warmer)
    {
        long t0 = Stopwatch.GetTimestamp();
        var frozen = world.Frozen();
        long t1 = Stopwatch.GetTimestamp();
        var set = new EffectSet(weather) { _prewarmSeconds = prewarmSeconds, _live = world, _camera = camera }.Make(library, seed, frozen);
        long t2 = Stopwatch.GetTimestamp();
        if (set._groups.Count == 0) set._ready = true;
        else warmer.Queue(set);
        set.StartTicks = (t1 - t0, t2 - t1, Stopwatch.GetTimestamp() - t2);
        return set;
    }

    /// <summary>
    /// Stopwatch ticks the render thread spent starting a background set: freezing the world, making the groups, queuing (for the log).
    /// </summary>
    public (long Freeze, long Make, long Queue) StartTicks { get; private set; }

    /// <summary>
    /// The background half on the warmer's thread: each group's schedule and simulation in turn, stopping when cancelled, then the groups rebound to
    /// the live world; marks the set ready even when it threw.
    /// </summary>
    internal void Warm()
    {
        try
        {
            foreach (var group in _groups)
            {
                if (_cancel.IsCancellationRequested) break;
                group.BeginWarm(WarmSeconds(group), _camera, Source);
                if (!group.SimulateSerial(_cancel.Token)) break;
            }
            foreach (var group in _groups) group.Rebind(_live!);
        }
        catch (Exception e)
        {
            _failure = e;
        }
        _ready = true;
    }

    /// <summary>
    /// Simulates the queued time on the calling thread, the units of a group in parallel (the start-up, where the first picture waits for it).
    /// </summary>
    public void SimulateNow()
    {
        foreach (var group in _groups) group.Simulate();
        _ready = true;
    }

    /// <summary>
    /// The set is not wanted any more: the background simulation stops at its next step.
    /// </summary>
    public void Cancel() => _cancel.Cancel();
}

/// <summary>
/// The thread that warms <see cref="EffectSet"/>s made in the background: one thread for the life of the renderer, at below-normal priority, made at
/// start-up. A thread made for each weather change cost the render thread 3 to 73 ms in a fast flight (Observed, 2026-10-10, while other programs
/// kept the cores busy): <c>Thread.Start</c> does not return before the new thread has run its first instruction, and a below-normal thread waits for
/// a core. Queuing a set only signals a thread that waits. Sets are warmed one after the other; one that was cancelled meanwhile is passed over in an
/// instant.
/// </summary>
public sealed class EffectWarmer : IDisposable
{
    /// <summary>
    /// The sets waiting to be warmed.
    /// </summary>
    readonly BlockingCollection<EffectSet> _queue = [];

    /// <summary>
    /// Starts the warming thread (background, below-normal priority).
    /// </summary>
    public EffectWarmer()
    {
        new Thread(() =>
        {
            foreach (var set in _queue.GetConsumingEnumerable()) set.Warm();
        })
        { IsBackground = true, Name = "meitou-particle-warmup", Priority = ThreadPriority.BelowNormal }.Start();
    }

    /// <summary>
    /// Hands <paramref name="set"/> to the warming thread.
    /// </summary>
    internal void Queue(EffectSet set) => _queue.Add(set);

    /// <summary>
    /// Takes no more sets; the thread ends once the queue is empty.
    /// </summary>
    public void Dispose() => _queue.CompleteAdding();
}