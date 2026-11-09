using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Meitou.Data.Particles;

/// <summary>
/// Runs the effect units' queued time of each frame (<see cref="EffectUnit.Advance"/>) on a thread of its own, while the caller does the rest of its
/// frame. It is not the thread pool: the pool is shared with every other <c>Parallel.For</c> of the viewer (shore bake, texture and impostor
/// encodes...), and a frame's particle task queued behind a burst of those started 10 to 70 ms late (Observed, 2026-10-10, fast flight: 22 frames
/// over 10 ms of waiting for a simulation that ran in under 1 ms; docs/formats/particle-universe.md "Threads"). The units of a job are claimed one at
/// a time by the worker and by whoever waits for the job (<see cref="Job.Wait"/>), so a worker that the CPU has not given a core yet costs nothing:
/// the caller runs the units itself, and the wait is never longer than the simulation. A frame's units cost 0.5 to 1.2 ms in all (Observed), too
/// little for a parallel loop to gain anything and enough to lose a frame when one of its helpers is not scheduled.
/// </summary>
public sealed class EffectSimulator : IDisposable
{
    /// <summary>
    /// The jobs waiting for the worker.
    /// </summary>
    readonly BlockingCollection<Job> _queue = [];
    /// <summary>
    /// The worker (background, above-normal priority) that runs the queued jobs.
    /// </summary>
    readonly Thread _thread;

    /// <summary>
    /// Starts the worker.
    /// </summary>
    public EffectSimulator()
    {
        _thread = new Thread(() =>
        {
            foreach (var job in _queue.GetConsumingEnumerable()) job.Work();
        })
        { IsBackground = true, Name = "meitou-particles", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>
    /// Queues <paramref name="units"/> (heaviest first is best).
    /// </summary>
    public Job Start(EffectUnit[] units)
    {
        var job = new Job(units);
        if (units.Length > 0) _queue.Add(job);
        return job;
    }

    /// <summary>
    /// Takes no more jobs; the worker ends once the queue is empty.
    /// </summary>
    public void Dispose() => _queue.CompleteAdding();

    /// <summary>
    /// One frame's simulation.
    /// </summary>
    public sealed class Job
    {
        /// <summary>
        /// The units the job runs.
        /// </summary>
        readonly EffectUnit[] _units;
        /// <summary>
        /// Set once every unit has run (at once for a job with none).
        /// </summary>
        readonly ManualResetEventSlim _done = new(false);
        /// <summary>
        /// Stopwatch timestamp of the job's making.
        /// </summary>
        readonly long _queuedAt = Stopwatch.GetTimestamp();
        /// <summary>
        /// Stopwatch timestamp of the first unit's start.
        /// </summary>
        long _startedAt;
        /// <summary>
        /// Interlocked counters: whether a thread has started the job, the next unit to claim, and the units finished.
        /// </summary>
        int _started, _next, _finished;
        /// <summary>
        /// The first exception a unit threw, rethrown by <see cref="Wait"/>.
        /// </summary>
        ExceptionDispatchInfo? _failure;

        /// <summary>
        /// A job of <paramref name="units"/>, done at once when there are none.
        /// </summary>
        internal Job(EffectUnit[] units)
        {
            this._units = units;
            if (units.Length == 0) _done.Set();
        }

        /// <summary>
        /// Stopwatch ticks from queuing to the first unit's start, and from there to the last unit's end (valid once <see cref="Wait"/> has
        /// returned).
        /// </summary>
        public long QueueTicks { get; private set; }
        /// <summary>
        /// Stopwatch ticks from the first unit's start to the last unit's end (valid once <see cref="Wait"/> has returned).
        /// </summary>
        public long RunTicks { get; private set; }
        /// <summary>
        /// Whether every unit has run.
        /// </summary>
        public bool IsCompleted => _done.IsSet;

        /// <summary>
        /// Runs units until none is left to claim (callable from any number of threads).
        /// </summary>
        internal void Work()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                _startedAt = Stopwatch.GetTimestamp();
                QueueTicks = _startedAt - _queuedAt;
            }
            while (true)
            {
                int i = Interlocked.Increment(ref _next) - 1;
                if (i >= _units.Length) return;
                try
                {
                    _units[i].Advance();
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(e), null);
                }
                if (Interlocked.Increment(ref _finished) == _units.Length)
                {
                    RunTicks = Stopwatch.GetTimestamp() - _startedAt;
                    _done.Set();
                    return;
                }
            }
        }

        /// <summary>
        /// Returns when every unit has run its time: this thread runs the units no other thread has claimed.
        /// </summary>
        public void Wait()
        {
            Work();
            _done.Wait();
            _failure?.Throw();
        }
    }
}