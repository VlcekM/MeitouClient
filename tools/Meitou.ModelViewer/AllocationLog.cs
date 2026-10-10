using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;

namespace Meitou.ModelViewer;

/// <summary>
/// <c>MEITOU_ALLOC_STATS=1</c> with <c>--fly-benchmark</c>: what allocates and what the collector does, from the runtime's own events (an in-process
/// <see cref="EventListener"/> on the GC keyword). Prints every garbage collection (generation, reason, kind, pause, the frame it landed in) and the
/// allocation ticks (one per ~100 KB allocated) summed by allocating thread (its name, or "pool/other") and type, so the big arrays show up by thread.
/// Sampled (the tick is statistical), not exact: the exact total is the <c>gc</c> line.
/// </summary>
sealed class AllocationLog : EventListener
{
    public static AllocationLog? Start() => Environment.GetEnvironmentVariable("MEITOU_ALLOC_STATS") == "1" ? new AllocationLog() : null;

    EventSource? runtime;   // set from the base constructor: no initialiser, or it would be reset
    readonly ConcurrentDictionary<(string Thread, string Type, bool Large), long> bytes = new();
    readonly ConcurrentDictionary<uint, string> threadNames = new();
    readonly ConcurrentQueue<(DateTime At, string Text)> collections = new();
    readonly List<DateTime> frameStarts = [];
    readonly uint renderThread = (uint)GetCurrentThreadId();
    DateTime suspendedAt, startedAt;
    int gen = -1, reason = -1, kind = -1;

    /// <summary>Marks the start of a frame (events arrive late, so a collection is placed in a frame by its own time stamp).</summary>
    public void FrameStart() => frameStarts.Add(DateTime.UtcNow);

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name != "Microsoft-Windows-DotNETRuntime") return;
        runtime = source;
        EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        switch (e.EventName)
        {
            case "GCAllocationTick_V4" or "GCAllocationTick_V3":
                {
                    long amount = Convert.ToInt64(Get(e, "AllocationAmount64") ?? Get(e, "AllocationAmount") ?? 0L);
                    bool large = Convert.ToInt32(Get(e, "AllocationKind") ?? 0) == 1;
                    string type = (Get(e, "TypeName") as string) ?? "?";
                    bytes.AddOrUpdate((ThreadName((uint)e.OSThreadId), type, large), amount, (_, v) => v + amount);
                    break;
                }
            case "GCSuspendEEBegin_V1":
                suspendedAt = e.TimeStamp;
                break;
            case "GCStart_V2" or "GCStart_V1":
                gen = Convert.ToInt32(Get(e, "Depth") ?? -1);
                reason = Convert.ToInt32(Get(e, "Reason") ?? -1);
                kind = Convert.ToInt32(Get(e, "Type") ?? -1);
                startedAt = e.TimeStamp;
                break;
            case "GCHeapStats_V2" or "GCHeapStats_V1":
                {
                    long G(string name) => Convert.ToInt64(Get(e, name) ?? 0L) / 1048576;
                    collections.Enqueue((e.TimeStamp, $"   heap after: gen0 {G("GenerationSize0")} MB (promoted {G("TotalPromotedSize0")}), gen1 {G("GenerationSize1")} ({G("TotalPromotedSize1")}), gen2 {G("GenerationSize2")} ({G("TotalPromotedSize2")}), LOH {G("GenerationSize3")} ({G("TotalPromotedSize3")}), pinned heap {G("GenerationSize4")} ({G("TotalPromotedSize4")}) MB; pinned objects {Get(e, "PinnedObjectCount")}, handles {Get(e, "GCHandleCount")}"));
                    break;
                }
            case "GCRestartEEEnd_V1":
                if (suspendedAt != default)
                {
                    double pauseMs = (e.TimeStamp - suspendedAt).TotalMilliseconds;
                    string[] kinds = ["blocking", "background", "foreground"];
                    string[] reasons = ["small", "induced", "low memory", "empty", "large", "oos small", "oos large", "induced not forced", "internal", "induced low memory", "induced compacting", "low memory host", "pm full gc", "low memory host blocking", "bgc tuning soh", "bgc tuning loh", "bgc stepping"];
                    collections.Enqueue((startedAt, $"gen{gen} {(kind >= 0 && kind < kinds.Length ? kinds[kind] : kind.ToString())}, reason {(reason >= 0 && reason < reasons.Length ? reasons[reason] : reason.ToString())}, suspended {pauseMs:0.0} ms"));
                    suspendedAt = default;
                }
                break;
        }
    }

    static object? Get(EventWrittenEventArgs e, string name)
    {
        int i = e.PayloadNames?.IndexOf(name) ?? -1;
        return i >= 0 ? e.Payload![i] : null;
    }

    string ThreadName(uint os) => threadNames.GetOrAdd(os, id =>
    {
        if (id == renderThread) return "render";
        var handle = OpenThread(0x0800, false, id);
        if (handle == 0) return "pool/other";
        try
        {
            if (GetThreadDescription(handle, out var p) == 0 && p != 0)
            {
                var s = Marshal.PtrToStringUni(p) ?? "";
                LocalFree(p);
                if (s.StartsWith("meitou-stream")) return "meitou-stream";
                if (s.Length > 0) return s;
            }
        }
        finally { CloseHandle(handle); }
        return "pool/other";
    });

    public void Print(int top = 30)
    {
        if (runtime is not null) DisableEvents(runtime);
        int n = 0;
        foreach (var (at, text) in collections.OrderBy(c => c.At))
        {
            int k = frameStarts.BinarySearch(at), frame = k >= 0 ? k + 1 : ~k;   // 1-based frame the collection started in (0: before the flight)
            Console.WriteLine(text.StartsWith("   heap") ? $"gc        {text}" : $"gc        #{++n} frame {frame}: {text}");
        }
        long total = bytes.Values.Sum();
        Console.WriteLine($"alloc     sampled {total / 1048576} MB in allocation ticks; by thread and type (MB, large-object-heap allocations marked LOH):");
        foreach (var g in bytes.GroupBy(p => p.Key.Thread).OrderByDescending(g => g.Sum(p => p.Value)))
            Console.WriteLine($"alloc       thread {g.Key}: {g.Sum(p => p.Value) / 1048576} MB, of which LOH {g.Where(p => p.Key.Large).Sum(p => p.Value) / 1048576} MB");
        foreach (var ((thread, type, large), size) in bytes.OrderByDescending(p => p.Value).Take(top))
            Console.WriteLine($"alloc       {size / 1048576,6} MB  {(large ? "LOH" : "   ")}  {thread,-14} {type}");
    }

    [DllImport("kernel32.dll")] static extern nint OpenThread(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] static extern int GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint p);
    [DllImport("kernel32.dll")] static extern int GetThreadDescription(nint thread, out nint description);
}
