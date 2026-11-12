using System.Diagnostics.Tracing;

namespace Meitou.Game.Host;

/// <summary>
/// The play benchmark's view of the garbage collector (PlayBench), from the runtime's own events in process: the allocation samples by type
/// (one about every 100 KB allocated, so each stands for about 100 KB), and every collection's generation, reason and pause (the runtime suspended to
/// restarted). Started and stopped around the script; read after.
/// </summary>
sealed class BenchGcListener : EventListener
{
    /// <summary>
    /// A collection: when the runtime was suspended for it (ms, <see cref="NowMs"/>'s clock), its generation and reason, and the pause until
    /// restarted (ms).
    /// </summary>
    public readonly record struct Collection(double AtMs, int Generation, int Reason, double PauseMs);

    /// <summary>
    /// Guards the samples and collections: the runtime's events arrive on its own thread.
    /// </summary>
    readonly object _gate = new();
    /// <summary>
    /// The bytes allocated by type name, summed from the allocation samples.
    /// </summary>
    readonly Dictionary<string, long> _allocated = [];
    /// <summary>
    /// The collections, in order.
    /// </summary>
    readonly List<Collection> _collections = [];
    /// <summary>
    /// When the runtime was last suspended (ms); -1 when no suspension is open.
    /// </summary>
    double _suspendAt = -1;
    /// <summary>
    /// The generation and reason of the collection in progress; -1 when none.
    /// </summary>
    int _depth = -1, _reason = -1;
    /// <summary>
    /// When the listener started, the clock's zero.
    /// </summary>
    readonly DateTime _started = DateTime.UtcNow;

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

    /// <summary>
    /// The event's own time (the runtime's events reach the listener in batches, later), in this clock.
    /// </summary>
    double Ms(EventWrittenEventArgs e) => (e.TimeStamp.ToUniversalTime() - _started).TotalMilliseconds;

    /// <summary>
    /// A payload field of an event by name, converted to <typeparamref name="T"/>; default when the event has no such field.
    /// </summary>
    static T Payload<T>(EventWrittenEventArgs e, string name) =>
        e.PayloadNames is { } n && n.IndexOf(name) is var i and >= 0 ? (T)Convert.ChangeType(e.Payload![i]!, typeof(T)) : default!;

    /// <summary>
    /// A copy of the allocations and collections so far.
    /// </summary>
    public (Dictionary<string, long> Allocated, List<Collection> Collections) Take()
    {
        lock (_gate) return (new(_allocated), [.. _collections]);
    }
}