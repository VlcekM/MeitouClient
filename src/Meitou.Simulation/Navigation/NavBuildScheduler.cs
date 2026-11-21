using System.Collections.Concurrent;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Where the navmesh and foliage builds' parallel loops run: dedicated threads below normal priority. On the default thread pool (normal
/// priority) a background zone build preempted the simulation's workers in the middle of a tick phase, and every phase then waited for the one
/// preempted. Loops take it through <see cref="Options"/>; nested loops are safe, because a waiting loop runs queued items itself.
/// </summary>
sealed class NavBuildScheduler : TaskScheduler
{
    /// <summary>
    /// The one scheduler every build loop shares.
    /// </summary>
    static readonly NavBuildScheduler Shared = new(Math.Max(1, Environment.ProcessorCount - 1));

    /// <summary>
    /// The queued items waiting for a thread.
    /// </summary>
    readonly BlockingCollection<Task> _queue = [];

    /// <summary>
    /// The number of build threads.
    /// </summary>
    readonly int _threads;

    /// <summary>
    /// Starts <paramref name="threads"/> background threads below normal priority that run the queued items until the process ends.
    /// </summary>
    NavBuildScheduler(int threads)
    {
        _threads = threads;
        for (int i = 0; i < threads; i++)
            new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"meitou-navbuild-{i}" }.Start();
    }

    /// <summary>
    /// Loop options on the build threads with at most <paramref name="workers"/> items at once.
    /// </summary>
    public static ParallelOptions Options(int workers) => new() { MaxDegreeOfParallelism = Math.Max(1, workers), TaskScheduler = Shared };

    /// <summary>
    /// The most items that run at once.
    /// </summary>
    public override int MaximumConcurrencyLevel => _threads;

    /// <summary>
    /// A build thread's loop: takes and runs queued items.
    /// </summary>
    void Work()
    {
        foreach (var task in _queue.GetConsumingEnumerable()) TryExecuteTask(task);
    }

    /// <summary>
    /// Queues <paramref name="task"/> for a build thread.
    /// </summary>
    protected override void QueueTask(Task task) => _queue.Add(task);

    /// <summary>
    /// Runs <paramref name="task"/> on the calling thread: a loop's caller runs its first share itself, and a loop waiting for a queued share runs
    /// it rather than block (with every build thread inside an outer loop, an inner loop would otherwise wait forever). A task taken off the
    /// queue later is then skipped, as it already ran.
    /// </summary>
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => TryExecuteTask(task);

    /// <summary>
    /// The queued items, for the debugger.
    /// </summary>
    protected override IEnumerable<Task> GetScheduledTasks() => _queue.ToArray();
}
