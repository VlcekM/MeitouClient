namespace Meitou.Rendering.Characters;

/// <summary>Render-thread-owned requests; bounded background CPU work. A result is consumed as one asset,
/// so a large multi-part asset cannot deadlock waiting for an evicted first part. No worker touches GPU or cache dictionaries.</summary>
internal sealed class RuntimeCharacterPreparationCache<TKey, TValue>(Func<TValue, long> sizeOf,
    int workers = 2, int pendingLimit = 64, long byteLimit = 192L << 20,
    Func<Func<TValue>, Task<TValue>>? schedule = null) : IDisposable where TKey : notnull
{
    sealed class Entry(TKey key, Func<TValue> work, long estimate)
    {
        internal readonly TKey Key = key;
        internal Func<TValue>? Work = work;
        internal readonly long Estimate = Math.Max(1, estimate);
        internal Task<TValue>? Task;
        internal long Reservation;
        internal bool Abandoned;
    }
    readonly Dictionary<TKey, Entry> entries = [];
    readonly Queue<Entry> pending = [];
    readonly List<Entry> running = [];
    readonly Func<Func<TValue>, Task<TValue>> start = schedule ?? BackgroundWork.Run;
    long reserved;
    bool disposed;
    public int Count => entries.Count;
    public int Running => running.Count;
    public long ReservedBytes => reserved;

    public bool Ready(TKey key, Func<TValue> work, long estimate)
    {
        if (disposed) return false;
        Pump();
        if (!entries.TryGetValue(key, out var entry))
        {
            int outstanding = 0;
            foreach (var existing in entries.Values) if (existing.Task is null || !existing.Task.IsCompleted) outstanding++;
            if (outstanding >= Math.Max(1, pendingLimit)) return false;
            entries.Add(key, entry = new(key, work, estimate)); pending.Enqueue(entry);
        }
        Pump();
        return entry.Task?.IsCompleted == true;
    }

    public bool TryGet(TKey key, out TValue value, out Exception? error)
    {
        Pump(); value = default!; error = null;
        if (!entries.TryGetValue(key, out var entry) || entry.Task is not { IsCompleted: true } task) return false;
        if (task.IsCompletedSuccessfully) value = task.GetAwaiter().GetResult();
        else error = task.Exception?.GetBaseException() ?? new TaskCanceledException(task);
        return true;
    }

    public void Remove(TKey key)
    {
        if (!entries.Remove(key, out var entry)) return;
        entry.Abandoned = true; entry.Work = null;
        if (entry.Task is { IsCompleted: true } && !running.Contains(entry))
        { reserved -= entry.Reservation; entry.Reservation = 0; entry.Task = null; }
    }

    public void Retain(Func<TKey, bool> wanted)
    {
        foreach (var key in entries.Keys.Where(key => !wanted(key)).ToArray()) Remove(key);
        Pump();
    }

    public void Pump()
    {
        if (disposed) return;
        for (int i = running.Count - 1; i >= 0; i--)
        {
            var entry = running[i];
            if (entry.Task is not { IsCompleted: true } task) continue;
            running.RemoveAt(i);
            long bytes = !entry.Abandoned && task.IsCompletedSuccessfully ? Math.Max(0, sizeOf(task.GetAwaiter().GetResult())) : 0;
            reserved += bytes - entry.Reservation; entry.Reservation = bytes;
            if (entry.Abandoned) entry.Task = null;
        }
        while (running.Count < Math.Max(1, workers) && pending.TryPeek(out var next))
        {
            if (next.Abandoned) { pending.Dequeue(); continue; }
            // One oversized asset may complete alone; requiring every single asset to fit would starve it forever.
            if (reserved > 0 && next.Estimate > Math.Max(1, byteLimit) - reserved) break;
            pending.Dequeue(); var work = next.Work!; next.Work = null;
            next.Reservation = next.Estimate; reserved += next.Reservation;
            try { next.Task = start(work); }
            catch (Exception error) { next.Task = Task.FromException<TValue>(error); }
            running.Add(next);
        }
    }

    public void Dispose()
    {
        disposed = true;
        foreach (var entry in entries.Values) { entry.Abandoned = true; entry.Work = null; }
        entries.Clear(); pending.Clear(); running.Clear(); reserved = 0;
        // Already executing jobs own only immutable CPU inputs and finish without publishing into this cache.
    }
}
