using System.Diagnostics;

namespace Meitou.Rendering.Gpu;

/// <summary>
/// Where the time of an upload step goes (<c>MEITOU_STREAM_LOG=1</c> only; otherwise every call is a branch on a constant): per thread, the
/// ticks and counts of the parts of a step that are not its own work (beginning a frame, waiting for a free frame slot, a new memory block, a
/// block given back, a staging chunk, the copy into staging, image and buffer creation, a freed image) and the garbage collector's pauses.
/// <see cref="Take"/> returns what the calling thread has spent since the last call, for the slow-step log lines; the steps' loops call it
/// (and drop the result) before each step, so a step's line holds that step's parts only.
/// </summary>
public static class UploadProfile
{
    public enum Part { FrameBegin, FrameWait, NewBlock, FreeBlock, StagingChunk, StagingCopy, CreateImage, CreateBuffer, FreeImage, FreeBuffer, Count }

    public static readonly bool On = Environment.GetEnvironmentVariable("MEITOU_STREAM_LOG") == "1";

    [ThreadStatic] static long[]? ticks;
    [ThreadStatic] static int[]? counts;
    [ThreadStatic] static long bytes;
    [ThreadStatic] static TimeSpan gcMark;

    public static long Begin() => On ? Stopwatch.GetTimestamp() : 0;

    public static void End(Part part, long start, long size = 0)
    {
        if (!On) return;
        ticks ??= new long[(int)Part.Count];
        counts ??= new int[(int)Part.Count];
        ticks[(int)part] += Stopwatch.GetTimestamp() - start;
        counts[(int)part]++;
        if (part == Part.StagingCopy) bytes += size;
    }

    /// <summary>A memory block made or given back (<see cref="Part.NewBlock"/>, <see cref="Part.FreeBlock"/>); a line when it took 1 ms or more, with the thread kind.</summary>
    public static void EndBlock(bool created, long start, ulong size, uint type, bool optimal, bool dedicated)
    {
        if (!On) return;
        End(created ? Part.NewBlock : Part.FreeBlock, start);
        double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        if (ms >= 1)
            Console.WriteLine($"alloc     {(created ? "new" : "freed")} block {size >> 20} MB type {type}{(optimal ? " optimal" : "")}{(dedicated ? " dedicated" : "")}: {ms:0.0} ms on a {(Thread.CurrentThread.IsThreadPoolThread ? "worker" : "main")} thread");
    }

    /// <summary>The calling thread's parts since the last call ("NewBlock 12.1 ms x1, StagingCopy 0.4 ms x2 (1.0 MB), GC pause 14.2 ms"); empty when nothing was spent.</summary>
    public static string Take()
    {
        if (!On) return "";
        var parts = new List<string>();
        if (ticks is not null && counts is not null)
            for (int i = 0; i < (int)Part.Count; i++)
            {
                if (counts[i] == 0) continue;
                string extra = (Part)i == Part.StagingCopy ? $" ({bytes / 1048576.0:0.0} MB)" : "";
                parts.Add($"{(Part)i} {ticks[i] * 1000.0 / Stopwatch.Frequency:0.0} ms x{counts[i]}{extra}");
                ticks[i] = 0;
                counts[i] = 0;
            }
        bytes = 0;
        var pause = GC.GetTotalPauseDuration();
        if (pause - gcMark > TimeSpan.FromMilliseconds(0.5)) parts.Add($"GC pause {(pause - gcMark).TotalMilliseconds:0.0} ms");
        gcMark = pause;
        return string.Join(", ", parts);
    }
}
