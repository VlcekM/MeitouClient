using System.Collections.Concurrent;
using System.Diagnostics;

namespace Meitou.Data.Particles;

/// <summary>
/// The effect groups of one weather being made (docs/formats/particle-universe.md "Weather changes"). A warm-up is two halves: the schedule, which places the
/// units (<see cref="EffectGroup.BeginWarm"/>), and the simulation of the time that queued up, up to 40 s of every unit (<see cref="EffectGroup.SimulateSerial"/>).
/// <see cref="Begin"/> makes the groups on the calling thread and leaves both to the caller (<see cref="SimulateNow"/>: the first groups of a view);
/// <see cref="BeginInBackground"/> makes the groups there, which is a few microseconds, and does both halves on a thread of its own, with the ground height of
/// a snapshot (<see cref="EffectWorld.Frozen"/>). Until <see cref="Ready"/> nobody else may touch the groups' state (units, particles, world): the list
/// <see cref="Groups"/> itself is made before the worker starts and never changes, so the render thread may read it, <see cref="Source"/> and each group's
/// immutable <see cref="EffectGroup.System"/> (the renderer preloads the textures from them), and nothing else.
/// </summary>
public sealed class EffectSet
{
    /// <summary>The longest warm-up a group gets of its own accord (seconds).</summary>
    public const float MaxAutoPrewarm = 40;

    readonly List<EffectGroup> groups = [];
    readonly List<WeatherEffectEntry> skipped = [];
    readonly CancellationTokenSource cancel = new();
    EffectWorld? live;
    float? prewarmSeconds;
    EffectCamera camera;
    volatile bool ready;
    volatile Exception? failure;

    EffectSet(WeatherEffectInput source) => Source = source;

    /// <summary>The weather this set was made for.</summary>
    public WeatherEffectInput Source { get; }
    public IReadOnlyList<EffectGroup> Groups => groups;
    /// <summary>The entries that have no group (a type that is not a weather effect, or an unknown particle system).</summary>
    public IReadOnlyList<WeatherEffectEntry> Skipped => skipped;
    /// <summary>The simulation finished (or was cancelled): the set may be used (<see cref="Failure"/> null and not cancelled) or dropped.</summary>
    public bool Ready => ready;
    public bool Cancelled => cancel.IsCancellationRequested;
    /// <summary>What went wrong on the background thread, if anything; the set is then not to be used.</summary>
    public Exception? Failure => failure;

    EffectSet Make(ParticleLibrary library, int seed, EffectWorld world)
    {
        int i = 0;
        foreach (var entry in Source.Effects)
        {
            if (EffectGroups.Create(entry, library, seed + i++, world) is { } group) groups.Add(group);
            else skipped.Add(entry);
        }
        return this;
    }

    float WarmSeconds(EffectGroup group) => prewarmSeconds ?? Math.Min(ParticleSimulation.LongestLife(group.System), MaxAutoPrewarm);

    /// <summary>
    /// Makes the groups of <paramref name="weather"/> and runs their schedules for the warm-up on the calling thread: <paramref name="prewarmSeconds"/>, or by default
    /// each system's longest particle life up to <see cref="MaxAutoPrewarm"/>. The first group takes <paramref name="seed"/>, the next one more.
    /// Simulate the queued time with <see cref="SimulateNow"/>.
    /// </summary>
    public static EffectSet Begin(WeatherEffectInput weather, ParticleLibrary library, int seed, EffectWorld world, float? prewarmSeconds, in EffectCamera camera)
    {
        var set = new EffectSet(weather) { prewarmSeconds = prewarmSeconds }.Make(library, seed, world);
        foreach (var group in set.groups) group.BeginWarm(set.WarmSeconds(group), camera, weather);
        if (set.groups.Count == 0) set.ready = true;   // nothing to simulate
        return set;
    }

    /// <summary>
    /// Makes the groups on the calling thread (the render thread: the world's ground height is frozen here) and queues them for <paramref name="warmer"/>'s thread, which
    /// runs their schedules and simulates the warm-up, the units one after the other: the frame keeps its cores and the pool its workers, and the
    /// particles are there some tenths of a second to seconds later (a weather has up to a few hundred units' worth of time to run).
    /// <see cref="Cancel"/> stops it between two steps. The groups belong to <paramref name="world"/> from <see cref="Ready"/> on.
    /// </summary>
    public static EffectSet BeginInBackground(WeatherEffectInput weather, ParticleLibrary library, int seed, EffectWorld world, float? prewarmSeconds, in EffectCamera camera, EffectWarmer warmer)
    {
        long t0 = Stopwatch.GetTimestamp();
        var frozen = world.Frozen();
        long t1 = Stopwatch.GetTimestamp();
        var set = new EffectSet(weather) { prewarmSeconds = prewarmSeconds, live = world, camera = camera }.Make(library, seed, frozen);
        long t2 = Stopwatch.GetTimestamp();
        if (set.groups.Count == 0) set.ready = true;
        else warmer.Queue(set);
        set.StartTicks = (t1 - t0, t2 - t1, Stopwatch.GetTimestamp() - t2);
        return set;
    }

    /// <summary>Stopwatch ticks the render thread spent starting a background set: freezing the world, making the groups, queuing (for the log).</summary>
    public (long Freeze, long Make, long Queue) StartTicks { get; private set; }

    internal void Warm()
    {
        try
        {
            foreach (var group in groups)
            {
                if (cancel.IsCancellationRequested) break;
                group.BeginWarm(WarmSeconds(group), camera, Source);
                if (!group.SimulateSerial(cancel.Token)) break;
            }
            foreach (var group in groups) group.Rebind(live!);
        }
        catch (Exception e) { failure = e; }
        ready = true;
    }

    /// <summary>Simulates the queued time on the calling thread, the units of a group in parallel (the start-up, where the first picture waits for it).</summary>
    public void SimulateNow()
    {
        foreach (var group in groups) group.Simulate();
        ready = true;
    }

    /// <summary>The set is not wanted any more: the background simulation stops at its next step.</summary>
    public void Cancel() => cancel.Cancel();
}

/// <summary>
/// The thread that warms <see cref="EffectSet"/>s made in the background: one thread for the life of the renderer, at below-normal priority, made at start-up.
/// A thread made for each weather change cost the render thread 3 to 73 ms in a fast flight (Observed, 2026-10-10, while other programs kept the cores
/// busy): <c>Thread.Start</c> does not return before the new thread has run its first instruction, and a below-normal thread waits for a core.
/// Queuing a set only signals a thread that waits. Sets are warmed one after the other; one that was cancelled meanwhile is passed over in an instant.
/// </summary>
public sealed class EffectWarmer : IDisposable
{
    readonly BlockingCollection<EffectSet> queue = [];

    public EffectWarmer()
    {
        new Thread(() =>
        {
            foreach (var set in queue.GetConsumingEnumerable()) set.Warm();
        })
        { IsBackground = true, Name = "meitou-particle-warmup", Priority = ThreadPriority.BelowNormal }.Start();
    }

    internal void Queue(EffectSet set) => queue.Add(set);

    public void Dispose() => queue.CompleteAdding();
}
