using System.Runtime.ExceptionServices;

namespace Meitou.Simulation;

/// <summary>
/// Dedicated worker threads for the tick's parallel phases (docs/simulation.md, "Threading"). <see cref="ForEach"/> hands out the
/// numbers 0..count-1, each exactly once, to whichever worker is free and returns when all are done, so a phase is a barrier. Which
/// worker takes which number is not fixed, so the body must write only what belongs to its number: then the thread count never
/// changes a result. One thread means no threads at all: the caller runs everything.
/// </summary>
public sealed class WorkerPool : IDisposable
{
    readonly Thread[] threads;
    readonly SemaphoreSlim[] wake;
    readonly CountdownEvent done = new(0);
    volatile bool stopping;

    // The job in flight (written by the caller before the workers are woken, which orders it for them).
    Action<int>? body;
    int count;
    int next;
    ExceptionDispatchInfo? failure;

    public WorkerPool(int threadCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threadCount, 1);
        ThreadCount = threadCount;
        if (threadCount == 1)
        {
            threads = [];
            wake = [];
            return;
        }
        threads = new Thread[threadCount];
        wake = new SemaphoreSlim[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            wake[i] = new SemaphoreSlim(0);
            int index = i;
            threads[i] = new Thread(() => Work(index)) { IsBackground = true, Name = $"Meitou.Sim {i}" };
            threads[i].Start();
        }
    }

    public int ThreadCount { get; }

    /// <summary>Runs <paramref name="action"/> for 0..<paramref name="count"/>-1 on the workers and waits. Not reentrant; one caller at a time.</summary>
    public void ForEach(int count, Action<int> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (count <= 0) return;
        if (ThreadCount == 1 || count == 1)
        {
            for (int i = 0; i < count; i++) action(i);
            return;
        }
        ObjectDisposedException.ThrowIf(stopping, this);
        body = action;
        this.count = count;
        next = 0;
        failure = null;
        int woken = Math.Min(ThreadCount, count);
        done.Reset(woken);
        for (int i = 0; i < woken; i++) wake[i].Release();
        done.Wait();
        body = null;
        failure?.Throw();
    }

    void Work(int index)
    {
        while (true)
        {
            wake[index].Wait();
            if (stopping) return;
            try
            {
                while (true)
                {
                    int i = Interlocked.Increment(ref next) - 1;
                    if (i >= count) break;
                    body!(i);
                }
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(e), null);
                Interlocked.Exchange(ref next, int.MaxValue / 2);   // the rest of the job is moot
            }
            finally
            {
                done.Signal();
            }
        }
    }

    public void Dispose()
    {
        if (stopping) return;
        stopping = true;
        foreach (var w in wake) w.Release();
        foreach (var t in threads) t.Join();
        foreach (var w in wake) w.Dispose();
        done.Dispose();
    }
}
