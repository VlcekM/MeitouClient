using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Meitou.Data.Particles;

/// <summary>
/// Runs the effect units' queued time of each frame (<see cref="EffectUnit.Advance"/>) on a thread of its own, while the caller does the rest of its frame.
/// It is not the thread pool: the pool is shared with every other <c>Parallel.For</c> of the viewer (shore bake, texture and impostor encodes...),
/// and a frame's particle task queued behind a burst of those started 10 to 70 ms late (Observed, 2026-10-10, fast flight: 22 frames over 10 ms of
/// waiting for a simulation that ran in under 1 ms; docs/formats/particle-universe.md "Threads"). The units of a job are claimed one at a time by the
/// worker and by whoever waits for the job (<see cref="Job.Wait"/>), so a worker that the CPU has not given a core yet costs nothing: the caller runs
/// the units itself, and the wait is never longer than the simulation. A frame's units cost 0.5 to 1.2 ms in all (Observed), too little for a
/// parallel loop to gain anything and enough to lose a frame when one of its helpers is not scheduled.
/// </summary>
public sealed class EffectSimulator : IDisposable
{
    readonly BlockingCollection<Job> queue = [];
    readonly Thread thread;

    public EffectSimulator()
    {
        thread = new Thread(() =>
        {
            foreach (var job in queue.GetConsumingEnumerable()) job.Work();
        })
        { IsBackground = true, Name = "meitou-particles", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    /// <summary>Queues <paramref name="units"/> (heaviest first is best).</summary>
    public Job Start(EffectUnit[] units)
    {
        var job = new Job(units);
        if (units.Length > 0) queue.Add(job);
        return job;
    }

    public void Dispose() => queue.CompleteAdding();

    /// <summary>One frame's simulation.</summary>
    public sealed class Job
    {
        readonly EffectUnit[] units;
        readonly ManualResetEventSlim done = new(false);
        readonly long queuedAt = Stopwatch.GetTimestamp();
        long startedAt;
        int started, next, finished;
        ExceptionDispatchInfo? failure;

        internal Job(EffectUnit[] units)
        {
            this.units = units;
            if (units.Length == 0) done.Set();
        }

        /// <summary>Stopwatch ticks from queuing to the first unit's start, and from there to the last unit's end (valid once <see cref="Wait"/> has returned).</summary>
        public long QueueTicks { get; private set; }
        public long RunTicks { get; private set; }
        public bool IsCompleted => done.IsSet;

        /// <summary>Runs units until none is left to claim (callable from any number of threads).</summary>
        internal void Work()
        {
            if (Interlocked.Exchange(ref started, 1) == 0)
            {
                startedAt = Stopwatch.GetTimestamp();
                QueueTicks = startedAt - queuedAt;
            }
            while (true)
            {
                int i = Interlocked.Increment(ref next) - 1;
                if (i >= units.Length) return;
                try { units[i].Advance(); }
                catch (Exception e) { Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(e), null); }
                if (Interlocked.Increment(ref finished) == units.Length)
                {
                    RunTicks = Stopwatch.GetTimestamp() - startedAt;
                    done.Set();
                    return;
                }
            }
        }

        /// <summary>Returns when every unit has run its time: this thread runs the units no other thread has claimed.</summary>
        public void Wait()
        {
            Work();
            done.Wait();
            failure?.Throw();
        }
    }
}
