using System.Collections.Concurrent;

namespace Meitou.Rendering;

/// <summary>
/// Where the world view's streaming work runs (file reads, decodes, layout): a few dedicated threads at below-normal priority, so that on a busy
/// machine the render thread is never the one preempted by a burst of decoding (the default thread pool runs at normal priority, one thread per
/// core, and a streaming burst then cost the render thread whole time slices: frames of 40 ms and more).
/// </summary>
static class BackgroundWork
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

    public static Task<T> Run<T>(Func<T> work) => Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.None, scheduler);
    public static Task Run(Action work) => Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.None, scheduler);
}

static class StreamingTuning
{
    /// <summary>Seconds a mesh or texture outside every draw range stays resident before it is unloaded (default 60; MEITOU_UNLOAD_IDLE overrides, for measurements; a huge value turns unloading off).</summary>
    public static double IdleSeconds { get; } = double.TryParse(Environment.GetEnvironmentVariable("MEITOU_UNLOAD_IDLE"), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 60;
}
