using System.Diagnostics;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Profiling;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// A guest's native segment, prepared on the render thread and recorded later, on any thread (docs/renderer-native.md 6.2, wave 4).
/// <para>
/// Prepare (the renderer, render thread, in the frame's order) does everything that reads or changes shared state: the GL mirror
/// (<c>CurrentTargets</c>, <c>CurrentState</c>), the exports (<c>VertexArray</c>, <c>Bindless</c>, <c>Buffer</c>), pipelines, the frame's constants,
/// the bindless table, counters; it stores the results in the job. <see cref="Record"/> then only turns them into commands: it may read the job and
/// immutable objects (pipelines, buffers), nothing else. A job is handed to <see cref="GpuContext.Record"/> and released once recorded.
/// </para>
/// </summary>
public abstract class RecordJob
{
    /// <summary>
    /// Records the segment into <paramref name="cmd"/> (the host's rendering instance, or a secondary that continues it).
    /// </summary>
    public abstract void Record(IGpuCommandList cmd);

    /// <summary>
    /// About how many draws <see cref="Record"/> records (read on the render thread when queued): a pass with little work is recorded on the render
    /// thread rather than waking the job threads (<see cref="Recording.MinThreadedDraws"/>).
    /// </summary>
    public virtual int Size => 1;

    /// <summary>
    /// Called on the render thread after <see cref="Record"/> (the owner's pool takes the job back).
    /// </summary>
    public virtual void Release() { }
}

/// <summary>
/// A pool of one kind of job, render thread only: <see cref="Rent"/> in Prepare, the job goes back when released.
/// </summary>
public sealed class JobPool<T> where T : RecordJob, new()
{
    /// <summary>
    /// The jobs returned and not rented again.
    /// </summary>
    readonly Stack<T> _free = new();

    /// <summary>
    /// A returned job, else a new one.
    /// </summary>
    public T Rent() => _free.Count > 0 ? _free.Pop() : new T();

    /// <summary>
    /// Takes a job back for reuse.
    /// </summary>
    public void Return(T job) => _free.Push(job);
}

/// <summary>
/// The recording switch (the bench's record-threads switch, docs/viewer.md): how a native host's guests are recorded.
/// </summary>
public static class Recording
{
    /// <summary>
    /// 0: every segment inline into the frame's command buffer, as before wave 4 (the single-threaded command stream). 1: the hosts' rendering
    /// instances take secondary command buffers, recorded one after the other on the render thread (the multithreaded commands, serially: tells a
    /// secondary-buffer difference from a race). 2 (the default; any other value): the secondaries are recorded on the job threads.
    /// </summary>
    public static int Mode { get; set; } = 2;

    /// <summary>
    /// Hosts open their rendering with secondaries (modes 1 and 2).
    /// </summary>
    public static bool Secondaries => Mode > 0;

    /// <summary>
    /// In mode 2, a pass whose jobs record fewer draws than this (<see cref="RecordJob.Size"/> summed) is recorded on the render thread as in mode 1:
    /// waking the job threads costs more than a few draws (32; 0 always threads).
    /// </summary>
    public static int MinThreadedDraws { get; set; } = 32;
}

/// <summary>
/// A native host's rendering instance whose contents are secondary command buffers (docs/renderer-native.md 6.1 and 6.4, wave 4): the host opens it
/// on its primary with <see cref="Begin"/>, its guests add their prepared segments in the frame's order (<see cref="Add"/>, or
/// <see cref="BeginInline"/> for code that records on the render thread at once: an unported guest, a timestamp), and <see cref="End"/> records the
/// jobs (on the job threads in <see cref="Recording.Mode"/> 2) and executes every secondary in that order. The command stream inside each secondary
/// is what the single-threaded code recorded for that segment, so the picture is the same.
/// </summary>
public sealed class ParallelPass
{
    /// <summary>
    /// One secondary of the pass, in execution order: a job to record (or an inline list already recorded), its label, list, stage and CPU time.
    /// </summary>
    struct Entry
    {
        /// <summary>
        /// The prepared job; null for an inline segment, and after its release.
        /// </summary>
        public RecordJob? Job;
        /// <summary>
        /// The segment's debug label.
        /// </summary>
        public string Label;
        /// <summary>
        /// The secondary the segment is recorded into.
        /// </summary>
        public CommandList? List;
        /// <summary>
        /// The <see cref="StageClock"/> stage its CPU time counts towards.
        /// </summary>
        public int Stage;
        /// <summary>
        /// The job's recording time, in stopwatch ticks.
        /// </summary>
        public long Ticks;
    }

    /// <summary>
    /// The context whose frame the secondaries come from.
    /// </summary>
    readonly GpuContext _ctx;
    /// <summary>
    /// Records job <c>k</c> on a job thread (made once, so the threaded loop allocates no closure).
    /// </summary>
    readonly Action<int> _recordOne;
    /// <summary>
    /// The pass's segments in execution order (grown by doubling).
    /// </summary>
    Entry[] _entries = new Entry[64];
    /// <summary>
    /// The segments added since <see cref="Begin"/>.
    /// </summary>
    int _count;
    /// <summary>
    /// The indices into <see cref="_entries"/> of the segments that are jobs.
    /// </summary>
    int[] _jobs = new int[64];
    /// <summary>
    /// The jobs added since <see cref="Begin"/>.
    /// </summary>
    int _jobCount;
    /// <summary>
    /// The jobs' summed <see cref="RecordJob.Size"/> (decides whether to thread).
    /// </summary>
    int _work;
    /// <summary>
    /// The secondaries' handles, in execution order, for the host's execute (grown as needed).
    /// </summary>
    CommandBuffer[] _handles = new CommandBuffer[64];
    /// <summary>
    /// The host's primary list the secondaries execute on; null outside the pass.
    /// </summary>
    CommandList? _primary;
    /// <summary>
    /// The host rendering's attachment formats (what the secondaries inherit).
    /// </summary>
    AttachmentFormats _formats;
    /// <summary>
    /// The inline segment open now; null when none is.
    /// </summary>
    CommandList? _inline;

    /// <summary>
    /// A pass for the context's frame.
    /// </summary>
    internal ParallelPass(GpuContext ctx)
    {
        this._ctx = ctx;
        _recordOne = k => RecordEntry(_jobs[k], threaded: true);
    }

    /// <summary>
    /// A host's rendering is open with secondaries (between <see cref="Begin"/> and <see cref="End"/>).
    /// </summary>
    public bool Open { get; private set; }

    /// <summary>
    /// The <see cref="StageClock"/> stage the jobs added now count towards (their summed CPU time, <see cref="StageClock.JobMs"/>).
    /// </summary>
    public int Stage { get; set; }

    /// <summary>
    /// What the secondaries of the last <see cref="End"/> recorded (VkGl adds it to its own counters).
    /// </summary>
    public GpuStats Totals { get; } = new();

    /// <summary>
    /// Opens the pass: <paramref name="host"/> has just begun a rendering with secondaries into <paramref name="targets"/>.
    /// </summary>
    public void Begin(CommandList host, in AttachmentFormats targets, int stage)
    {
        RenderJobs.AssertNotInJob();
        if (Open) throw new InvalidOperationException("a parallel pass is already open");

        (_primary, _formats, Stage, Open, _count, _jobCount, _work) = (host, targets, stage, true, 0, 0, 0);
        Totals.Reset();
    }

    /// <summary>
    /// A prepared segment, recorded at <see cref="End"/> into a secondary of its own, executed in this order.
    /// </summary>
    public void Add(string label, RecordJob job)
    {
        if (!Open || _inline is not null)
            throw new InvalidOperationException("ParallelPass.Add outside the pass or inside an inline segment");

        ref var e = ref Next();
        (e.Job, e.Label, e.List, e.Stage, e.Ticks) = (job, label, null, Stage, 0);
        if (_jobCount == _jobs.Length) Array.Resize(ref _jobs, _jobs.Length * 2);

        _jobs[_jobCount++] = _count - 1;
        _work += job.Size;
        _ctx.Frame.Stats.NativeSegments++;
    }

    /// <summary>
    /// A secondary recorded on the render thread now (an unported guest's segment, a timestamp), executed in this order; end it with
    /// <see cref="EndInline"/>.
    /// </summary>
    public CommandList BeginInline(string label)
    {
        RenderJobs.AssertNotInJob();
        if (!Open || _inline is not null)
            throw new InvalidOperationException("ParallelPass.BeginInline outside the pass or inside another inline segment");

        var list = _ctx.Frame.BeginSecondary(_formats);
        list.BeginLabel(label);
        _ctx.Frame.Stats.NativeSegments++;
        return _inline = list;
    }

    /// <summary>
    /// Ends the inline segment: its label and recording end, and it takes its place in the order.
    /// </summary>
    public void EndInline(CommandList list)
    {
        if (!ReferenceEquals(list, _inline)) throw new InvalidOperationException("EndInline without a matching BeginInline");

        _inline = null;
        list.EndLabel();
        list.EndSecondary();
        ref var e = ref Next();
        (e.Job, e.Label, e.List, e.Stage, e.Ticks) = (null, "", list, Stage, 0);
    }

    /// <summary>
    /// Records the jobs (<see cref="Recording.Mode"/> 2: on the job threads; 1, or while a draw log is written: here, in order), executes every
    /// secondary in order on the host's primary, adds their counters to the frame's and their CPU time to <see cref="StageClock.JobMs"/>, and
    /// releases the jobs. The host then ends its rendering.
    /// </summary>
    public void End()
    {
        if (!Open) throw new InvalidOperationException("ParallelPass.End without Begin");
        if (_inline is not null) throw new InvalidOperationException("ParallelPass.End with an inline segment open");

        bool threaded = Recording.Mode >= 2 && _jobCount > 1 && _work >= Recording.MinThreadedDraws;
        RecordAll(threaded);
        CollectHandles();
        _primary!.ExecuteCommands(_handles.AsSpan(0, _count));
        Open = false;
        _primary = null;
    }

    /// <summary>
    /// Records every job: on the job threads when <paramref name="threaded"/>, else here in order.
    /// </summary>
    void RecordAll(bool threaded)
    {
        if (threaded)
        {
            RenderJobs.For(_jobCount, _recordOne);
            return;
        }

        for (int k = 0; k < _jobCount; k++) RecordEntry(_jobs[k], threaded: false);
    }

    /// <summary>
    /// Collects the secondaries' handles in order, adds their counters to <see cref="Totals"/> and the frame's, counts the jobs' CPU time and
    /// releases the jobs.
    /// </summary>
    void CollectHandles()
    {
        if (_handles.Length < _count) _handles = new CommandBuffer[Math.Max(_count, _handles.Length * 2)];

        var frameStats = _ctx.Frame.Stats;
        for (int i = 0; i < _count; i++)
        {
            ref var e = ref _entries[i];
            var list = e.List!;
            _handles[i] = list.Handle;
            Add(Totals, list.Stats);
            if (e.Job is { } job)
            {
                StageClock.AddJob(e.Stage, e.Ticks);
                job.Release();
                e.Job = null;
            }

            e.List = null;
        }

        Add(frameStats, Totals);
    }

    /// <summary>
    /// Records one job into a new secondary (with <see cref="RenderJobs.InJob"/> set) and times it.
    /// </summary>
    void RecordEntry(int i, bool threaded)
    {
        ref var e = ref _entries[i];
        long t0 = Stopwatch.GetTimestamp();
        RenderJobs.SetInJob(true);
        try
        {
            var list = _ctx.Frame.BeginSecondary(_formats);
            list.BeginLabel(e.Label);
            e.Job!.Record(list);
            list.EndLabel();
            list.EndSecondary();
            e.List = list;
        }
        finally
        {
            RenderJobs.SetInJob(false);
        }

        e.Ticks = Stopwatch.GetTimestamp() - t0;
    }

    /// <summary>
    /// The next segment slot (the array doubles when full).
    /// </summary>
    ref Entry Next()
    {
        if (_count == _entries.Length) Array.Resize(ref _entries, _entries.Length * 2);
        return ref _entries[_count++];
    }

    /// <summary>
    /// Adds one list's counters to another's.
    /// </summary>
    static void Add(GpuStats to, GpuStats from)
    {
        to.Draws += from.Draws;
        to.IndirectDraws += from.IndirectDraws;
        to.Dispatches += from.Dispatches;
        to.PipelinesBound += from.PipelinesBound;
        to.DescriptorPushes += from.DescriptorPushes;
    }
}
