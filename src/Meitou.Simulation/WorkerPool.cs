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
    readonly Thread[] _threads;
    readonly SemaphoreSlim[] _wake;
    readonly CountdownEvent _done = new(0);
    volatile bool _stopping;

    // The job in flight (written by the caller before the workers are woken, which orders it for them).
    Action<int>? _body;
    int _count;
    int _next;
    ExceptionDispatchInfo? _failure;

    public WorkerPool(int threadCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threadCount, 1);
        ThreadCount = threadCount;
        if (threadCount == 1)
        {
            _threads = [];
            _wake = [];
            return;
        }
        _threads = new Thread[threadCount];
        _wake = new SemaphoreSlim[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            _wake[i] = new SemaphoreSlim(0);
            int index = i;
            _threads[i] = new Thread(() => Work(index)) { IsBackground = true, Name = $"Meitou.Sim {i}" };
            _threads[i].Start();
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
        ObjectDisposedException.ThrowIf(_stopping, this);
        _body = action;
        this._count = count;
        _next = 0;
        _failure = null;
        int woken = Math.Min(ThreadCount, count);
        _done.Reset(woken);
        for (int i = 0; i < woken; i++) _wake[i].Release();
        _done.Wait();
        _body = null;
        _failure?.Throw();
    }

    void Work(int index)
    {
        while (true)
        {
            _wake[index].Wait();
            if (_stopping) return;
            try
            {
                while (true)
                {
                    int i = Interlocked.Increment(ref _next) - 1;
                    if (i >= _count) break;
                    _body!(i);
                }
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(e), null);
                Interlocked.Exchange(ref _next, int.MaxValue / 2);   // the rest of the job is moot
            }
            finally
            {
                _done.Signal();
            }
        }
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        foreach (var w in _wake) w.Release();
        foreach (var t in _threads) t.Join();
        foreach (var w in _wake) w.Dispose();
        _done.Dispose();
    }
}