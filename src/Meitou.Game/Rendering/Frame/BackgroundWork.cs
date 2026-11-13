using System.Collections.Concurrent;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// Where the world view's streaming work runs (file reads, decodes, layout): a few dedicated threads at below-normal priority, so that on a busy
/// machine the render thread is never the one preempted by a burst of decoding (the default thread pool runs at normal priority, one thread per
/// core, and a streaming burst then cost the render thread whole time slices: frames of 40 ms and more).
/// </summary>
public static class BackgroundWork
{
    /// <summary>
    /// The task scheduler behind <see cref="Run{T}(Func{T})"/>: a fixed set of background threads that take queued jobs, urgent ones first.
    /// </summary>
    sealed class Scheduler : TaskScheduler
    {
        /// <summary>
        /// The jobs waiting for a thread: the ordinary ones and the urgent ones (<see cref="RunUrgent"/>).
        /// </summary>
        readonly BlockingCollection<Task> _queue = [], _urgent = [];

        /// <summary>
        /// The jobs running on a thread now.
        /// </summary>
        int _running;

        /// <summary>
        /// Jobs waiting for a thread (urgent, other) and running on one now.
        /// </summary>
        public (int Urgent, int Queued, int Running) Load => (_urgent.Count, _queue.Count, Volatile.Read(ref _running));

        /// <summary>
        /// Starts <paramref name="threads"/> background threads that run the queued jobs until the process ends.
        /// </summary>
        public Scheduler(int threads)
        {
            // A free thread takes the oldest urgent job first (TakeFromAny tries the collections in order), then the oldest other one.
            var both = new[] { _urgent, _queue };
            for (int i = 0; i < threads; i++)
            {
                new Thread(() =>
                {
                    while (true)
                    {
                        BlockingCollection<Task>.TakeFromAny(both, out var task);
                        Interlocked.Increment(ref _running);
                        try
                        {
                            TryExecuteTask(task!);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _running);
                        }
                    }
                })
                { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"meitou-stream-{i}" }.Start();
            }
        }

        protected override void QueueTask(Task task) => (task.AsyncState is UrgentMark ? _urgent : _queue).Add(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override IEnumerable<Task> GetScheduledTasks() => [.. _urgent.ToArray(), .. _queue.ToArray()];

        /// <inheritdoc/>
        public override int MaximumConcurrencyLevel => Math.Max(2, Environment.ProcessorCount - 3);
    }

    /// <summary>
    /// The state object that marks a job as urgent (<see cref="RunUrgent"/>).
    /// </summary>
    sealed class UrgentMark;

    /// <summary>
    /// The mark <see cref="RunUrgent"/> passes as a job's state so the scheduler queues it ahead of the others.
    /// </summary>
    static readonly UrgentMark Urgent = new();

    /// <summary>
    /// The streaming threads.
    /// </summary>
    public static readonly int Threads = Math.Max(2, Environment.ProcessorCount - 3);

    /// <summary>
    /// The one scheduler every streaming job runs on, with <see cref="Threads"/> threads.
    /// </summary>
    static readonly Scheduler WorkScheduler = new(Threads);

    /// <summary>
    /// The queue, for the loading log: jobs waiting (urgent, other) and running.
    /// </summary>
    public static string Describe() =>
        WorkScheduler.Load is var (urgent, queued, running) ? $"{running}/{Threads} running, {urgent} urgent and {queued} queued" : "";

    /// <summary>
    /// Queues <paramref name="work"/> on the streaming threads behind the jobs already waiting; the task completes with its result.
    /// </summary>
    public static Task<T> Run<T>(Func<T> work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.None, WorkScheduler);

    /// <summary>
    /// Like <see cref="Run{T}(Func{T})"/>, but ahead of every job that is not urgent: for work whose lateness shows near the camera
    /// (the foliage's whole zone layouts), which otherwise waited behind a burst of grass pages and texture decodes.
    /// </summary>
    public static Task<T> RunUrgent<T>(Func<T> work) =>
        Task.Factory.StartNew(_ => work(), Urgent, CancellationToken.None, TaskCreationOptions.None, WorkScheduler);

    /// <summary>
    /// Queues <paramref name="work"/> on the streaming threads behind the jobs already waiting.
    /// </summary>
    public static Task Run(Action work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.None, WorkScheduler);
}