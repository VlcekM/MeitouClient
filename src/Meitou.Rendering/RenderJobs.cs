namespace Meitou.Rendering;

/// <summary>
/// Fork-join for the render thread's parallel loops (foliage culling): a few dedicated threads above normal priority that the render thread
/// wakes for a loop and helps. Not the thread pool: under a busy machine a pool thread holding one of the loop's items got preempted and the
/// render thread waited for it (a lower median frame, a worse tail). One caller at a time (the render thread); bodies must not call back in.
/// </summary>
public static class RenderJobs
{
    static readonly int workers = Math.Clamp(Environment.ProcessorCount / 3, 1, 4);
    static readonly SemaphoreSlim wake = new(0);
    static readonly ManualResetEventSlim done = new(false);
    static Action<int>? body;
    static int next, count, pending;

    static RenderJobs()
    {
        for (int i = 0; i < workers; i++)
        {
            int index = i + 1;
            new Thread(() => Worker(index)) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = $"meitou-render-job-{i}" }.Start();
        }
    }

    /// <summary>The threads a loop runs on: the job threads and the calling (render) thread.</summary>
    public static int Threads => workers + 1;

    /// <summary>This thread's index among <see cref="Threads"/>: 0 for the render thread (and any other thread), 1 .. workers for the job threads.
    /// Per-thread resources (command pools for secondaries, docs/renderer-native.md 6.3) are indexed by it.</summary>
    public static int ThreadIndex => threadIndex;
    [ThreadStatic] static int threadIndex;

    /// <summary>True while this thread runs a recording job (<see cref="Recording"/>): what a job may not touch checks it
    /// (<see cref="AssertNotInJob"/>), so a recording job that reaches for render-thread state fails at once instead of racing.</summary>
    public static bool InJob => inJob;
    [ThreadStatic] static bool inJob;

    /// <summary>Marks the calling thread as running a recording job (or not).</summary>
    internal static void SetInJob(bool value) => inJob = value;

    /// <summary>Throws on a recording job's thread: Prepare-only state (the frame's constants, the bindless table, the GL mirror, the frame globals).</summary>
    public static void AssertNotInJob()
    {
        if (inJob) throw new InvalidOperationException("render-thread state used by a recording job (docs/renderer-native.md 6.3: Record reads only what Prepare made)");
    }

    static void Worker(int index)
    {
        threadIndex = index;
        while (true)
        {
            wake.Wait();
            Run();
            if (Interlocked.Decrement(ref pending) == 0) done.Set();
        }
    }

    static void Run()
    {
        var work = Volatile.Read(ref body);
        if (work is null) return;
        int i;
        while ((i = Interlocked.Increment(ref next) - 1) < count) work(i);
    }

    /// <summary>
    /// Raises the calling (render) thread above normal priority, level with the job threads: at normal priority a busy machine's other processes
    /// took whole time slices from it, frames of 8-16 ms in the middle of a cheap draw. The streaming threads stay below normal.
    /// </summary>
    public static void RaiseRenderThread() => Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

    /// <summary>Runs <paramref name="work"/> for 0 .. <paramref name="n"/> − 1 on the render thread and the job threads; returns when all are done.</summary>
    public static void For(int n, Action<int> work)
    {
        if (n <= 1)
        {
            if (n == 1) work(0);
            return;
        }
        int helpers = Math.Min(workers, n - 1);
        count = n;
        next = 0;
        pending = helpers;
        done.Reset();
        Volatile.Write(ref body, work);
        wake.Release(helpers);
        Run();
        done.Wait();
        Volatile.Write(ref body, null);
    }
}
