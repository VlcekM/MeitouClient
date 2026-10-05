using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Meitou.Rendering;

/// <summary>
/// Where the world view's streaming work runs (file reads, decodes, layout): a few dedicated threads at below-normal priority, so that on a busy
/// machine the render thread is never the one preempted by a burst of decoding (the default thread pool runs at normal priority, one thread per
/// core, and a streaming burst then cost the render thread whole time slices: frames of 40 ms and more).
/// </summary>
public static class BackgroundWork
{
    sealed class Scheduler : TaskScheduler
    {
        readonly BlockingCollection<Task> queue = [];

        public Scheduler(int threads)
        {
            for (int i = 0; i < threads; i++)
                new Thread(() =>
                {
                    foreach (var task in queue.GetConsumingEnumerable()) TryExecuteTask(task);
                })
                { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"meitou-stream-{i}" }.Start();
        }

        protected override void QueueTask(Task task) => queue.Add(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override IEnumerable<Task> GetScheduledTasks() => queue.ToArray();
        public override int MaximumConcurrencyLevel => Math.Max(2, Environment.ProcessorCount - 3);
    }

    static readonly Scheduler scheduler = new(Math.Max(2, Environment.ProcessorCount - 3));

    public static Task<T> Run<T>(Func<T> work, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Task.Factory.StartNew(Measure ? Measured(work, file, line) : work, CancellationToken.None, TaskCreationOptions.None, scheduler);
    public static Task Run(Action work, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Task.Factory.StartNew(Measure ? () => { Measured(() => { work(); return 0; }, file, line)(); } : work, CancellationToken.None, TaskCreationOptions.None, scheduler);

    // ---- per call site statistics (MEITOU_JOB_STATS=1 with --fly-benchmark): what the streaming work allocates and costs ----

    /// <summary>Whether <see cref="Report"/> is wanted (MEITOU_JOB_STATS=1).</summary>
    public static bool ReportJobs { get; } = Environment.GetEnvironmentVariable("MEITOU_JOB_STATS") == "1";
    /// <summary>Jobs started while this is set are measured.</summary>
    public static volatile bool Measure;
    static readonly ConcurrentDictionary<string, (long Bytes, double Ms, int Count)> sites = new();

    static Func<T> Measured<T>(Func<T> work, string file, int line) => () =>
    {
        long before = GC.GetAllocatedBytesForCurrentThread(), start = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return work(); }
        finally
        {
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            sites.AddOrUpdate($"{Path.GetFileName(file)}:{line}", (bytes, ms, 1), (_, v) => (v.Bytes + bytes, v.Ms + ms, v.Count + 1));
        }
    };

    /// <summary>Prints the measured jobs by call site, most allocating first.</summary>
    public static void Report()
    {
        foreach (var (site, (bytes, ms, count)) in sites.OrderByDescending(s => s.Value.Bytes))
            Console.WriteLine($"jobs      {bytes / 1048576,6} MB allocated, {ms,7:0} ms, {count,5} jobs  {site}");
    }
}

static class StreamingTuning
{
    /// <summary>Seconds a mesh or texture outside every draw range stays resident before it is unloaded (default 60; MEITOU_UNLOAD_IDLE overrides, for measurements; a huge value turns unloading off).</summary>
    public static double IdleSeconds { get; } = double.TryParse(Environment.GetEnvironmentVariable("MEITOU_UNLOAD_IDLE"), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 60;
}
