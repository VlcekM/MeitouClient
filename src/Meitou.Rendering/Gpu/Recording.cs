using System.Diagnostics;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu;

/// <summary>
/// A guest's native segment, prepared on the render thread and recorded later, on any thread (docs/renderer-native.md 6.2, wave 4).
/// <para>
/// Prepare (the renderer, render thread, in the frame's order) does everything that reads or changes shared state: the GL mirror
/// (<c>CurrentTargets</c>, <c>CurrentState</c>), the exports (<c>VertexArray</c>, <c>Bindless</c>, <c>Buffer</c>), pipelines, the frame's constants,
/// the bindless table, counters; it stores the results in the job. <see cref="Record"/> then only turns them into commands: it may read the job
/// and immutable objects (pipelines, buffers), nothing else. A job is handed to <see cref="GpuContext.Record"/> and released once recorded.
/// </para>
/// </summary>
public abstract class RecordJob
{
    /// <summary>Records the segment into <paramref name="cmd"/> (the host's rendering instance, or a secondary that continues it).</summary>
    public abstract void Record(CommandList cmd);

    /// <summary>About how many draws <see cref="Record"/> records (read on the render thread when queued): a pass with little work is recorded
    /// on the render thread rather than waking the job threads (<see cref="Recording.MinThreadedDraws"/>).</summary>
    public virtual int Size => 1;

    /// <summary>Called on the render thread after <see cref="Record"/> (the owner's pool takes the job back).</summary>
    public virtual void Release() { }
}

/// <summary>A pool of one kind of job, render thread only: <see cref="Rent"/> in Prepare, the job goes back when released.</summary>
public sealed class JobPool<T> where T : RecordJob, new()
{
    readonly Stack<T> free = new();

    public T Rent() => free.Count > 0 ? free.Pop() : new T();

    public void Return(T job) => free.Push(job);
}

/// <summary>
/// The recording switch (<c>MEITOU_RECORD_THREADS</c>, docs/viewer.md): how a native host's guests are recorded.
/// </summary>
public static class Recording
{
    /// <summary>
    /// 0: every segment inline into the frame's command buffer, as before wave 4 (the single-threaded command stream). 1: the hosts' rendering
    /// instances take secondary command buffers, recorded one after the other on the render thread (the multithreaded commands, serially: tells
    /// a secondary-buffer difference from a race). 2 (the default; any other value): the secondaries are recorded on the job threads.
    /// </summary>
    public static int Mode { get; set; } = Environment.GetEnvironmentVariable("MEITOU_RECORD_THREADS") switch
    {
        "0" => 0,
        "1" => 1,
        _ => 2,
    };

    /// <summary>Hosts open their rendering with secondaries (modes 1 and 2).</summary>
    public static bool Secondaries => Mode > 0;

    /// <summary>
    /// In mode 2, a pass whose jobs record fewer draws than this (<see cref="RecordJob.Size"/> summed) is recorded on the render thread as in mode 1:
    /// waking the job threads costs more than a few draws (<c>MEITOU_RECORD_MIN_DRAWS</c>, default 32; 0 always threads).
    /// </summary>
    public static int MinThreadedDraws { get; set; } = int.TryParse(Environment.GetEnvironmentVariable("MEITOU_RECORD_MIN_DRAWS"), out int n) ? n : 32;
}

/// <summary>
/// A native host's rendering instance whose contents are secondary command buffers (docs/renderer-native.md 6.1 and 6.4, wave 4): the host
/// opens it on its primary with <see cref="Begin"/>, its guests add their prepared segments in the frame's order (<see cref="Add"/>, or
/// <see cref="BeginInline"/> for code that records on the render thread at once: an unported guest, a timestamp), and <see cref="End"/> records the
/// jobs (on the job threads in <see cref="Recording.Mode"/> 2) and executes every secondary in that order. The command stream inside each secondary
/// is what the single-threaded code recorded for that segment, so the picture is the same.
/// </summary>
public sealed class ParallelPass
{
    struct Entry
    {
        public RecordJob? Job;
        public string Label;
        public CommandList? List;
        public int Stage;
        public long Ticks;
    }

    readonly GpuContext ctx;
    Entry[] entries = new Entry[64];
    int count;
    int[] jobs = new int[64];
    int jobCount, work;
    CommandBuffer[] handles = new CommandBuffer[64];
    CommandList? primary;
    AttachmentFormats formats;
    CommandList? inline;
    readonly Action<int> recordOne;

    internal ParallelPass(GpuContext ctx)
    {
        this.ctx = ctx;
        recordOne = k => RecordEntry(jobs[k], threaded: true);
    }

    /// <summary>A host's rendering is open with secondaries (between <see cref="Begin"/> and <see cref="End"/>).</summary>
    public bool Open { get; private set; }

    /// <summary>The <see cref="StageClock"/> stage the jobs added now count towards (their summed CPU time, <see cref="StageClock.JobMs"/>).</summary>
    public int Stage { get; set; }

    /// <summary>What the secondaries of the last <see cref="End"/> recorded (VkGl adds it to its own counters).</summary>
    public GpuStats Totals { get; } = new();

    /// <summary>Opens the pass: <paramref name="host"/> has just begun a rendering with secondaries into <paramref name="targets"/>.</summary>
    public void Begin(CommandList host, in AttachmentFormats targets, int stage)
    {
        RenderJobs.AssertNotInJob();
        if (Open) throw new InvalidOperationException("a parallel pass is already open");
        (primary, formats, Stage, Open, count, jobCount, work) = (host, targets, stage, true, 0, 0, 0);
        Totals.Reset();
    }

    /// <summary>A prepared segment, recorded at <see cref="End"/> into a secondary of its own, executed in this order.</summary>
    public void Add(string label, RecordJob job)
    {
        if (!Open || inline is not null) throw new InvalidOperationException("ParallelPass.Add outside the pass or inside an inline segment");
        ref var e = ref Next();
        (e.Job, e.Label, e.List, e.Stage, e.Ticks) = (job, label, null, Stage, 0);
        if (jobCount == jobs.Length) Array.Resize(ref jobs, jobs.Length * 2);
        jobs[jobCount++] = count - 1;
        work += job.Size;
        ctx.Frame.Stats.NativeSegments++;
    }

    /// <summary>A secondary recorded on the render thread now (an unported guest's segment, a timestamp), executed in this order; end it with <see cref="EndInline"/>.</summary>
    public CommandList BeginInline(string label)
    {
        RenderJobs.AssertNotInJob();
        if (!Open || inline is not null) throw new InvalidOperationException("ParallelPass.BeginInline outside the pass or inside another inline segment");
        var list = ctx.Frame.BeginSecondary(formats);
        list.Log = ctx.Log;
        list.BeginLabel(label);
        ctx.Frame.Stats.NativeSegments++;
        return inline = list;
    }

    public void EndInline(CommandList list)
    {
        if (!ReferenceEquals(list, inline)) throw new InvalidOperationException("EndInline without a matching BeginInline");
        inline = null;
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
        if (inline is not null) throw new InvalidOperationException("ParallelPass.End with an inline segment open");
        bool threaded = Recording.Mode >= 2 && ctx.Log is null && jobCount > 1 && work >= Recording.MinThreadedDraws;
        if (threaded) RenderJobs.For(jobCount, recordOne);
        else for (int k = 0; k < jobCount; k++) RecordEntry(jobs[k], threaded: false);
        if (handles.Length < count) handles = new CommandBuffer[Math.Max(count, handles.Length * 2)];
        var frameStats = ctx.Frame.Stats;
        for (int i = 0; i < count; i++)
        {
            ref var e = ref entries[i];
            var list = e.List!;
            handles[i] = list.Handle;
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
        primary!.ExecuteCommands(handles.AsSpan(0, count));
        Open = false;
        primary = null;
    }

    void RecordEntry(int i, bool threaded)
    {
        ref var e = ref entries[i];
        long t0 = Stopwatch.GetTimestamp();
        RenderJobs.SetInJob(true);
        try
        {
            var list = ctx.Frame.BeginSecondary(formats);
            list.Log = threaded ? null : ctx.Log;
            list.BeginLabel(e.Label);
            e.Job!.Record(list);
            list.EndLabel();
            list.EndSecondary();
            e.List = list;
        }
        finally { RenderJobs.SetInJob(false); }
        e.Ticks = Stopwatch.GetTimestamp() - t0;
    }

    ref Entry Next()
    {
        if (count == entries.Length) Array.Resize(ref entries, entries.Length * 2);
        return ref entries[count++];
    }

    static void Add(GpuStats to, GpuStats from)
    {
        to.Draws += from.Draws;
        to.IndirectDraws += from.IndirectDraws;
        to.Dispatches += from.Dispatches;
        to.PipelinesBound += from.PipelinesBound;
        to.DescriptorPushes += from.DescriptorPushes;
    }
}
