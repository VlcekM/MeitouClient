using System.Diagnostics.Tracing;

namespace Meitou.Game.Host;

/// <summary>
/// The play benchmark's view of the garbage collector (PlayBench), from the runtime's own events in process: the allocation samples by type
/// (one about every 100 KB allocated, so each stands for about 100 KB), and every collection's generation, reason and pause (the runtime suspended to
/// restarted). Started and stopped around the script; read after.
/// </summary>
sealed class BenchGcListener : EventListener
{
    public readonly record struct Collection(double AtMs, int Generation, int Reason, double PauseMs);

    readonly object _gate = new();
    readonly Dictionary<string, long> _allocated = [];
    readonly List<Collection> _collections = [];
    double _suspendAt = -1;
    int _depth = -1, _reason = -1;

    /// <summary>
    /// Milliseconds since the listener started (the collections' <see cref="Collection.AtMs"/>).
    /// </summary>
    public double NowMs => (DateTime.UtcNow - _started).TotalMilliseconds;

    protected override void OnEventSourceCreated(EventSource source)
    {
        // The GC keyword (1) at verbose level brings the allocation ticks too.
        if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, (EventKeywords)1);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        lock (_gate)
        {
            switch (e.EventName)
            {
                case "GCAllocationTick_V4" or "GCAllocationTick_V3" or "GCAllocationTick_V2":
                    var names = e.PayloadNames;
                    string type = names is not null && names.IndexOf("TypeName") is var t and >= 0 ? e.Payload![t] as string ?? "?" : "?";
                    long amount = names is not null && names.IndexOf("AllocationAmount64") is var a and >= 0
                        ? Convert.ToInt64(e.Payload![a]) : 100_000;
                    _allocated[type] = _allocated.GetValueOrDefault(type) + amount;
                    break;
                case "GCSuspendEEBegin_V1":
                    _suspendAt = Ms(e);
                    break;
                case "GCStart_V2" or "GCStart_V1" or "GCStart":
                    _depth = Payload<int>(e, "Depth");
                    _reason = Payload<int>(e, "Reason");
                    break;
                case "GCRestartEEEnd_V1" when _suspendAt >= 0:
                    if (_depth >= 0) _collections.Add(new Collection(_suspendAt, _depth, _reason, Ms(e) - _suspendAt));
                    (_suspendAt, _depth, _reason) = (-1, -1, -1);
                    break;
            }
        }
    }

    // The event's own time (the runtime's events reach the listener in batches, later), in this clock.
    double Ms(EventWrittenEventArgs e) => (e.TimeStamp.ToUniversalTime() - _started).TotalMilliseconds;
    readonly DateTime _started = DateTime.UtcNow;

    static T Payload<T>(EventWrittenEventArgs e, string name) =>
        e.PayloadNames is { } n && n.IndexOf(name) is var i and >= 0 ? (T)Convert.ChangeType(e.Payload![i]!, typeof(T)) : default!;

    public (Dictionary<string, long> Allocated, List<Collection> Collections) Take()
    {
        lock (_gate) return (new(_allocated), [.. _collections]);
    }
}