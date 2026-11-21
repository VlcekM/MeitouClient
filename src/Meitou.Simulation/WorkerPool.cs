using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Meitou.Simulation;

/// <summary>
/// Dedicated worker threads for the tick's parallel phases (docs/simulation.md, "Threading"). <see cref="ForEach"/> hands out the
/// numbers 0..count-1, each exactly once, to whichever thread is free (the caller and the helpers) and returns when all are done, so a phase is a
/// barrier. Which thread takes which number is not fixed, so the body must write only what belongs to its number: then the thread count never
/// changes a result. One thread means no helpers at all: the caller runs everything.
/// </summary>
public sealed class WorkerPool : IDisposable
{
    /// <summary>
    /// One <see cref="ForEach"/> call: its body, its numbers and how far they got. Each call has its own, so a helper still on an old one never
    /// touches the next.
    /// </summary>
    sealed class Job(Action<int> body, int count)
    {
        /// <summary>
        /// The body run for each number.
        /// </summary>
        public readonly Action<int> Body = body;
        /// <summary>
        /// How many numbers the job has.
        /// </summary>
        public readonly int Count = count;
        /// <summary>
        /// The next number to hand out.
        /// </summary>
        public int Next;
        /// <summary>
        /// How many numbers have finished (run, skipped after a failure, or failed).
        /// </summary>
        public int Done;
        /// <summary>
        /// 1 once the caller is about to park on <see cref="_callerWake"/>: the thread finishing the last number then sets it.
        /// </summary>
        public int CallerParked;
        /// <summary>
        /// The first failure; the numbers after it are skipped.
        /// </summary>
        public ExceptionDispatchInfo? Failure;

        /// <summary>
        /// Whether a helper that last ran <paramref name="last"/> has numbers to take here.
        /// </summary>
        public bool Offers(Job last) => this != last && Volatile.Read(ref Next) < Count;
    }

    /// <summary>
    /// The job published between calls: it has no numbers.
    /// </summary>
    static readonly Job IdleJob = new(_ => { }, 0);

    /// <summary>
    /// How long an idle helper spins for the next job before it parks: tick phases follow each other within microseconds, and waking a parked
    /// thread costs far more.
    /// </summary>
    static readonly long HelperSpinTicks = Stopwatch.Frequency / 20_000;

    /// <summary>
    /// How long the caller spins for the helpers' last numbers before it parks.
    /// </summary>
    static readonly long CallerSpinTicks = Stopwatch.Frequency / 10_000;

    /// <summary>
    /// The pause instructions between two looks while spinning. A spin only ever pauses: the runtime's own spin waits end in
    /// <c>Thread.Sleep(1)</c>, a whole timer tick on Windows.
    /// </summary>
    const int SpinIterations = 8;

    /// <summary>
    /// The helper threads (one fewer than <see cref="ThreadCount"/>: the caller is the other).
    /// </summary>
    readonly Thread[] _threads;
    /// <summary>
    /// The lock the idle helpers park on (<see cref="Monitor.Wait(object)"/> needs a plain object).
    /// </summary>
    readonly object _park = new();
    /// <summary>
    /// Wakes the caller parked for a job's last numbers. No spinning of its own: the caller spun already (see <see cref="SpinIterations"/>).
    /// </summary>
    readonly ManualResetEventSlim _callerWake = new(false, 0);
    /// <summary>
    /// The job in flight, else <see cref="IdleJob"/>.
    /// </summary>
    Job _job = IdleJob;
    /// <summary>
    /// How many helpers are parked (or about to be) on <see cref="_park"/>.
    /// </summary>
    int _parked;
    /// <summary>
    /// Whether the pool is stopping.
    /// </summary>
    volatile bool _stopping;

    /// <summary>
    /// A pool of <paramref name="threadCount"/> threads: the caller and <paramref name="threadCount"/> - 1 helpers above normal priority, like the
    /// render jobs, so another process or a navmesh build does not preempt one in the middle of a phase (1: no helpers).
    /// </summary>
    public WorkerPool(int threadCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threadCount, 1);
        ThreadCount = threadCount;
        _threads = new Thread[threadCount - 1];

        for (int i = 0; i < _threads.Length; i++)
        {
            _threads[i] = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = $"Meitou.Sim {i}" };
            _threads[i].Start();
        }
    }

    /// <summary>
    /// The number of threads (the caller included).
    /// </summary>
    public int ThreadCount { get; }

    /// <summary>
    /// Runs <paramref name="action"/> for 0..<paramref name="count"/>-1 on the caller and the helpers and waits. Not reentrant; one caller at a
    /// time. No body runs after it returns; the first failure is rethrown once every number is done.
    /// </summary>
    public void ForEach(int count, Action<int> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (count <= 0) return;

        if (_threads.Length == 0 || count == 1)
        {
            for (int i = 0; i < count; i++) action(i);

            return;
        }

        ObjectDisposedException.ThrowIf(_stopping, this);
        var job = new Job(action, count);
        Publish(job);

        // The caller takes numbers too: when it finishes them all before a parked helper wakes, there is nothing to wait for.
        Run(job);
        AwaitDone(job);

        Volatile.Write(ref _job, IdleJob);
        job.Failure?.Throw();
    }

    /// <summary>
    /// Makes <paramref name="job"/> the one in flight and wakes the parked helpers. The exchange is a full fence before <see cref="_parked"/> is
    /// read, and a helper counts itself parked before it looks at the job once more: so either the helper sees the job or the caller sees it parked.
    /// </summary>
    void Publish(Job job)
    {
        Interlocked.Exchange(ref _job, job);
        if (Volatile.Read(ref _parked) == 0) return;

        lock (_park) Monitor.PulseAll(_park);
    }

    /// <summary>
    /// Waits for the numbers the helpers still run: spins briefly, then parks until the last one sets <see cref="_callerWake"/>.
    /// </summary>
    void AwaitDone(Job job)
    {
        if (SpinForDone(job)) return;

        // Reset before the flag, and again on each round: a set left over from an earlier job then only costs one more round. The flag's
        // exchange and the finisher's increment are both full fences, so either the finisher sees the flag or the caller sees the count.
        while (Volatile.Read(ref job.Done) < job.Count)
        {
            _callerWake.Reset();
            Interlocked.Exchange(ref job.CallerParked, 1);
            if (Volatile.Read(ref job.Done) >= job.Count) break;

            _callerWake.Wait();
        }
    }

    /// <summary>
    /// Takes and runs <paramref name="job"/>'s numbers until none are left; the caller and the helpers all run it. A failure is kept and the numbers
    /// after it are skipped (the rest of the job is moot), but each still counts as done.
    /// </summary>
    void Run(Job job)
    {
        while (true)
        {
            int i = Interlocked.Increment(ref job.Next) - 1;
            if (i >= job.Count) return;

            try
            {
                if (Volatile.Read(ref job.Failure) is null) job.Body(i);
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref job.Failure, ExceptionDispatchInfo.Capture(e), null);
            }
            finally
            {
                Finish(job);
            }
        }
    }

    /// <summary>
    /// Counts one of <paramref name="job"/>'s numbers done, and wakes the parked caller when it was the last.
    /// </summary>
    void Finish(Job job)
    {
        if (Interlocked.Increment(ref job.Done) < job.Count) return;
        if (Volatile.Read(ref job.CallerParked) != 0) _callerWake.Set();
    }

    /// <summary>
    /// A helper's loop: runs each new job it finds until the pool stops.
    /// </summary>
    void Work()
    {
        var last = IdleJob;
        while (NextJob(last) is { } job)
        {
            last = job;
            Run(job);
        }
    }

    /// <summary>
    /// The next job with numbers to take after <paramref name="last"/>, spinning briefly and then parked; null once the pool stops.
    /// </summary>
    Job? NextJob(Job last)
    {
        if (SpinForJob(last) is { } spun) return spun;

        lock (_park)
        {
            while (true)
            {
                if (_stopping) return null;

                // Counted parked before the last look at the job (see Publish).
                Interlocked.Increment(ref _parked);
                var job = Volatile.Read(ref _job);
                if (job.Offers(last))
                {
                    Interlocked.Decrement(ref _parked);
                    return job;
                }

                Monitor.Wait(_park);
                Interlocked.Decrement(ref _parked);
            }
        }
    }

    /// <summary>
    /// Spins up to <see cref="CallerSpinTicks"/> for <paramref name="job"/>'s numbers to be done; whether they are.
    /// </summary>
    static bool SpinForDone(Job job)
    {
        long end = Stopwatch.GetTimestamp() + CallerSpinTicks;
        while (Volatile.Read(ref job.Done) < job.Count)
        {
            if (Stopwatch.GetTimestamp() >= end) return false;

            Thread.SpinWait(SpinIterations);
        }

        return true;
    }

    /// <summary>
    /// Spins up to <see cref="HelperSpinTicks"/> for a job after <paramref name="last"/>; the job, or null when the time ran out or the pool
    /// stops (the helper then goes to park, or finds the stop there).
    /// </summary>
    Job? SpinForJob(Job last)
    {
        long end = Stopwatch.GetTimestamp() + HelperSpinTicks;
        while (!_stopping)
        {
            var job = Volatile.Read(ref _job);
            if (job.Offers(last)) return job;
            if (Stopwatch.GetTimestamp() >= end) return null;

            Thread.SpinWait(SpinIterations);
        }

        return null;
    }

    /// <summary>
    /// Stops the helpers and frees the caller's signal.
    /// </summary>
    public void Dispose()
    {
        if (_stopping) return;

        _stopping = true;
        lock (_park) Monitor.PulseAll(_park);
        foreach (var t in _threads) t.Join();

        _callerWake.Dispose();
    }
}
