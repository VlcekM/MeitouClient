namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// Fork-join for the render thread's parallel loops (foliage culling): a few dedicated threads above normal priority that the render thread
/// wakes for a loop and helps. Not the thread pool: under a busy machine a pool thread holding one of the loop's items got preempted and the
/// render thread waited for it (a lower median frame, a worse tail). One caller at a time (the render thread); bodies must not call back in.
/// </summary>
public static class RenderJobs
{
    /// <summary>
    /// The job threads: a third of the processors, 1 to 4.
    /// </summary>
    static readonly int Workers = Math.Clamp(Environment.ProcessorCount / 3, 1, 4);

    /// <summary>
    /// Released once per job thread a loop wakes (<see cref="For"/>).
    /// </summary>
    static readonly SemaphoreSlim Wake = new(0);

    /// <summary>
    /// Set by the last job thread to finish its share of a loop.
    /// </summary>
    static readonly ManualResetEventSlim Done = new(false);

    /// <summary>
    /// The loop body; null between loops.
    /// </summary>
    static Action<int>? s_body;

    /// <summary>
    /// The loop's next item to take, its item count, and how many woken job threads have not finished yet.
    /// </summary>
    static int s_next, s_count, s_pending;

    /// <summary>
    /// This thread's index (<see cref="ThreadIndex"/>): set by each job thread, 0 elsewhere.
    /// </summary>
    [ThreadStatic] static int s_threadIndex;

    /// <summary>
    /// Whether this thread runs a recording job (<see cref="InJob"/>).
    /// </summary>
    [ThreadStatic] static bool s_inJob;

    /// <summary>
    /// Starts the <see cref="Workers"/> job threads (background, above normal priority).
    /// </summary>
    static RenderJobs()
    {
        for (int i = 0; i < Workers; i++)
        {
            int index = i + 1;
            new Thread(() => Worker(index))
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = $"meitou-render-job-{i}",
            }.Start();
        }
    }

    /// <summary>
    /// The threads a loop runs on: the job threads and the calling (render) thread.
    /// </summary>
    public static int Threads => Workers + 1;

    /// <summary>
    /// This thread's index among <see cref="Threads"/>: 0 for the render thread (and any other thread), 1 .. workers for the job threads.
    /// Per-thread resources (command pools for secondaries, docs/renderer-native.md 6.3) are indexed by it.
    /// </summary>
    public static int ThreadIndex => s_threadIndex;

    /// <summary>
    /// True while this thread runs a recording job (<see cref="Recording"/>): what a job may not touch checks it
    /// (<see cref="AssertNotInJob"/>), so a recording job that reaches for render-thread state fails at once instead of racing.
    /// </summary>
    public static bool InJob => s_inJob;

    /// <summary>
    /// Marks the calling thread as running a recording job (or not).
    /// </summary>
    internal static void SetInJob(bool value) => s_inJob = value;

    /// <summary>
    /// Throws on a recording job's thread: Prepare-only state (the frame's constants, the bindless table, the GL mirror, the frame globals).
    /// </summary>
    public static void AssertNotInJob()
    {
        if (s_inJob)
        {
            throw new InvalidOperationException("render-thread state used by a recording job " +
                                                "(docs/renderer-native.md 6.3: Record reads only what Prepare made)");
        }
    }

    /// <summary>
    /// A job thread's loop: waits to be woken, takes items until the loop's are gone, and signals <see cref="Done"/> when it is the last.
    /// </summary>
    static void Worker(int index)
    {
        s_threadIndex = index;
        while (true)
        {
            Wake.Wait();
            Run();
            if (Interlocked.Decrement(ref s_pending) == 0) Done.Set();
        }
    }

    /// <summary>
    /// Takes and runs the current loop's items until none are left; the render thread and the woken job threads all run it.
    /// </summary>
    static void Run()
    {
        var work = Volatile.Read(ref s_body);
        if (work is null) return;
        int i;
        while ((i = Interlocked.Increment(ref s_next) - 1) < s_count) work(i);
    }

    /// <summary>
    /// Raises the calling (render) thread above normal priority, level with the job threads: at normal priority a busy machine's other processes
    /// took whole time slices from it, frames of 8-16 ms in the middle of a cheap draw. The streaming threads stay below normal.
    /// </summary>
    public static void RaiseRenderThread() => Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

    /// <summary>
    /// Runs <paramref name="work"/> for 0 .. <paramref name="n"/> − 1 on the render thread and the job threads; returns when all are done.
    /// </summary>
    public static void For(int n, Action<int> work)
    {
        if (n <= 1)
        {
            if (n == 1) work(0);
            return;
        }
        int helpers = Math.Min(Workers, n - 1);
        s_count = n;
        s_next = 0;
        s_pending = helpers;
        Done.Reset();
        Volatile.Write(ref s_body, work);
        Wake.Release(helpers);
        Run();
        Done.Wait();
        Volatile.Write(ref s_body, null);
    }
}