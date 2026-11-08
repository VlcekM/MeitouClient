using System.Collections.Concurrent;

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
        readonly BlockingCollection<Task> queue = [], urgent = [];

        public Scheduler(int threads)
        {
            // A free thread takes the oldest urgent job first (TakeFromAny tries the collections in order), then the oldest other one.
            var both = new[] { urgent, queue };
            for (int i = 0; i < threads; i++)
                new Thread(() =>
                {
                    while (true)
                    {
                        BlockingCollection<Task>.TakeFromAny(both, out var task);
                        TryExecuteTask(task!);
                    }
                })
                { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"meitou-stream-{i}" }.Start();
        }

        protected override void QueueTask(Task task) => (task.AsyncState is UrgentMark ? urgent : queue).Add(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override IEnumerable<Task> GetScheduledTasks() => [.. urgent.ToArray(), .. queue.ToArray()];
        public override int MaximumConcurrencyLevel => Math.Max(2, Environment.ProcessorCount - 3);
    }

    /// <summary>The state object that marks a job as urgent (<see cref="RunUrgent"/>).</summary>
    sealed class UrgentMark;
    static readonly UrgentMark Urgent = new();

    /// <summary>The streaming threads.</summary>
    public static readonly int Threads = Math.Max(2, Environment.ProcessorCount - 3);

    static readonly Scheduler scheduler = new(Threads);

    public static Task<T> Run<T>(Func<T> work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.None, scheduler);

    /// <summary>
    /// Like <see cref="Run{T}(Func{T})"/>, but ahead of every job that is not urgent: for work whose lateness shows near the camera
    /// (the foliage's whole zone layouts), which otherwise waited behind a burst of grass pages and texture decodes.
    /// </summary>
    public static Task<T> RunUrgent<T>(Func<T> work) =>
        Task.Factory.StartNew(_ => work(), Urgent, CancellationToken.None, TaskCreationOptions.None, scheduler);

    public static Task Run(Action work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.None, scheduler);
}
