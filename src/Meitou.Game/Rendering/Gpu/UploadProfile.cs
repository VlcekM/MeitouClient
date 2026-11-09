using System.Diagnostics;
using Meitou.Game.Rendering.Profiling;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// Where the time of an upload step goes (<c>--log-spikes</c> only; otherwise every call is a branch on a flag): per thread, the ticks and counts of
/// the parts of a step that are not its own work (beginning a frame, waiting for a free frame slot, a new memory block, a block given back, a staging
/// chunk, the copy into staging, image and buffer creation, a freed image) and the garbage collector's pauses. <see cref="Take"/> returns what the
/// calling thread has spent since the last call, for the slow-step log lines; the steps' loops call it (and drop the result) before each step, so a
/// step's line holds that step's parts only.
/// </summary>
public static class UploadProfile
{
    /// <summary>
    /// The parts of an upload step that are timed.
    /// </summary>
    public enum Part
    {
        /// <summary>
        /// Beginning a frame.
        /// </summary>
        FrameBegin,
        /// <summary>
        /// Waiting for a free frame slot.
        /// </summary>
        FrameWait,
        /// <summary>
        /// A new memory block.
        /// </summary>
        NewBlock,
        /// <summary>
        /// A memory block given back.
        /// </summary>
        FreeBlock,
        /// <summary>
        /// A new staging chunk.
        /// </summary>
        StagingChunk,
        /// <summary>
        /// The copy into staging (its bytes counted too).
        /// </summary>
        StagingCopy,
        /// <summary>
        /// Image creation.
        /// </summary>
        CreateImage,
        /// <summary>
        /// Buffer creation.
        /// </summary>
        CreateBuffer,
        /// <summary>
        /// Freeing an image.
        /// </summary>
        FreeImage,
        /// <summary>
        /// Freeing a buffer.
        /// </summary>
        FreeBuffer,
        /// <summary>
        /// The number of parts.
        /// </summary>
        Count
    }

    /// <summary>
    /// Whether the profile records (<c>--log-spikes</c>).
    /// </summary>
    public static bool On => SpikeLog.Enabled;

    /// <summary>
    /// The calling thread's ticks per part since the last <see cref="Take"/>.
    /// </summary>
    [ThreadStatic] static long[]? s_ticks;
    /// <summary>
    /// The calling thread's calls per part since the last <see cref="Take"/>.
    /// </summary>
    [ThreadStatic] static int[]? s_counts;
    /// <summary>
    /// The calling thread's bytes copied into staging since the last <see cref="Take"/>.
    /// </summary>
    [ThreadStatic] static long s_bytes;
    /// <summary>
    /// The total GC pause time at the calling thread's last <see cref="Take"/>.
    /// </summary>
    [ThreadStatic] static TimeSpan s_gcMark;

    /// <summary>
    /// A start timestamp for <see cref="End"/>; 0 when the profile is off.
    /// </summary>
    public static long Begin() => On ? Stopwatch.GetTimestamp() : 0;

    /// <summary>
    /// Adds the time since <paramref name="start"/> (and, for the staging copy, <paramref name="size"/> bytes) to a part of the calling thread.
    /// </summary>
    public static void End(Part part, long start, long size = 0)
    {
        if (!On) return;

        s_ticks ??= new long[(int)Part.Count];
        s_counts ??= new int[(int)Part.Count];
        s_ticks[(int)part] += Stopwatch.GetTimestamp() - start;
        s_counts[(int)part]++;
        if (part == Part.StagingCopy) s_bytes += size;
    }

    /// <summary>
    /// A memory block made or given back (<see cref="Part.NewBlock"/>, <see cref="Part.FreeBlock"/>); a line when it took 1 ms or more, with the
    /// thread kind.
    /// </summary>
    public static void EndBlock(bool created, long start, ulong size, uint type, bool optimal, bool dedicated)
    {
        if (!On) return;

        End(created ? Part.NewBlock : Part.FreeBlock, start);
        double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        if (ms >= 1)
        {
            string kind = created ? "new" : "freed";
            string thread = Thread.CurrentThread.Name is { } name ? name : Thread.CurrentThread.IsThreadPoolThread ? "worker" : "main";
            Console.WriteLine($"alloc     {kind} block {size >> 20} MB type {type}{(optimal ? " optimal" : "")}{(dedicated ? " dedicated" : "")}: " +
                $"{ms:0.0} ms on a {thread} thread");
        }
    }

    /// <summary>
    /// The calling thread's parts since the last call ("NewBlock 12.1 ms x1, StagingCopy 0.4 ms x2 (1.0 MB), GC pause 14.2 ms"); empty when nothing
    /// was spent.
    /// </summary>
    public static string Take()
    {
        if (!On) return "";

        var parts = new List<string>();
        TakeParts(parts);
        s_bytes = 0;
        AddGcPause(parts);
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Adds a "Part ms xN" entry per part used since the last call, and clears them.
    /// </summary>
    static void TakeParts(List<string> parts)
    {
        if (s_ticks is null || s_counts is null) return;

        for (int i = 0; i < (int)Part.Count; i++)
        {
            if (s_counts[i] == 0) continue;

            string extra = (Part)i == Part.StagingCopy ? $" ({s_bytes / 1048576.0:0.0} MB)" : "";
            parts.Add($"{(Part)i} {s_ticks[i] * 1000.0 / Stopwatch.Frequency:0.0} ms x{s_counts[i]}{extra}");
            s_ticks[i] = 0;
            s_counts[i] = 0;
        }
    }

    /// <summary>
    /// Adds the GC pause time since the last call when it is over 0.5 ms.
    /// </summary>
    static void AddGcPause(List<string> parts)
    {
        var pause = GC.GetTotalPauseDuration();
        if (pause - s_gcMark > TimeSpan.FromMilliseconds(0.5)) parts.Add($"GC pause {(pause - s_gcMark).TotalMilliseconds:0.0} ms");

        s_gcMark = pause;
    }
}
