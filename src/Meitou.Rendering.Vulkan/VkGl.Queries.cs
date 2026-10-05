using Silk.NET.OpenGL;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Vulkan;

public sealed unsafe partial class VkGl
{
    /// <summary>
    /// A GL query: a timestamp (<c>glQueryCounter</c>) or an elapsed time (<c>glBeginQuery(TIME_ELAPSED)</c>, two timestamps).
    /// Every issue takes a fresh pair of entries from its frame slot's pool (a query may be issued several times a frame).
    /// When a slot comes round again its fence has passed: the results still unread are collected, then the pool is reset from
    /// the host (Vulkan 1.2 host query reset) and handed out again. A pool per slot, as the validation layer treats a pool that
    /// any frame in flight uses as busy as a whole.
    /// </summary>
    internal sealed class GlQueryObj
    {
        public QueryPool Pool;  // the issuing frame slot's pool
        public int Slot = -1, Base = -1;   // that slot, and the first of the two entries of the last issue
        public bool Elapsed, Issued;
        public long IssuedFrame = -1;
        public ulong? Result;
        public void Dispose(VkGl gl) { }
    }

    QueryPool[]? queryPools;
    const int PairsPerSlot = 4096;
    int queryCursor;
    List<GlQueryObj>[]? issuedQueries;

    void EnsureQueryPool()
    {
        if (queryPools is not null) return;
        int n = device.Frames.Count;
        var info = new QueryPoolCreateInfo { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Timestamp, QueryCount = PairsPerSlot * 2 };
        queryPools = new QueryPool[n];
        for (int i = 0; i < n; i++)
        {
            Check(vk.CreateQueryPool(dev, &info, null, out queryPools[i]));
            vk.ResetQueryPool(dev, queryPools[i], 0, PairsPerSlot * 2);
        }
        issuedQueries = new List<GlQueryObj>[n];
        for (int i = 0; i < n; i++) issuedQueries[i] = [];
    }

    /// <summary>At the start of a frame (its slot's fence has passed): results of the slot's queries kept, its range reset.</summary>
    void RecycleQueries(int slot)
    {
        queryCursor = 0;
        if (queryPools is null) return;
        var list = issuedQueries![slot];
        foreach (var q in list)
            if (q.Result is null && q.Slot == slot) Read(q, wait: false);
        list.Clear();
        vk.ResetQueryPool(dev, queryPools[slot], 0, PairsPerSlot * 2);
    }

    public uint GenQuery()
    {
        EnsureQueryPool();
        uint id = NewId();
        queries[id] = new GlQueryObj();
        return id;
    }

    public void DeleteQuery(uint id) => queries.Remove(id);

    public void QueryCounter(uint id, QueryCounterTarget target)
    {
        var q = queries[id];
        Issue(q, elapsed: false);
        vk.CmdWriteTimestamp2(cmd, PipelineStageFlags2.AllCommandsBit, q.Pool, (uint)(q.Base + 1));
    }

    GlQueryObj? activeElapsed;

    public void BeginQuery(QueryTarget target, uint id)
    {
        var q = queries[id];
        Issue(q, elapsed: true);
        vk.CmdWriteTimestamp2(cmd, PipelineStageFlags2.AllCommandsBit, q.Pool, (uint)q.Base);
        activeElapsed = q;
    }

    public void EndQuery(QueryTarget target)
    {
        if (activeElapsed is not { } q) return;
        vk.CmdWriteTimestamp2(Cmd, PipelineStageFlags2.AllCommandsBit, q.Pool, (uint)(q.Base + 1));
        activeElapsed = null;
    }

    void Issue(GlQueryObj q, bool elapsed)
    {
        _ = Cmd;   // opens the frame, so Frames.Slot is the slot the timestamps go into
        if (queryCursor >= PairsPerSlot) throw new InvalidOperationException($"more than {PairsPerSlot} GL queries in one frame");
        int slot = device.Frames.Slot;
        q.Pool = queryPools![slot];
        q.Slot = slot;
        q.Base = queryCursor++ * 2;
        issuedQueries![slot].Add(q);
        q.Elapsed = elapsed;
        q.Issued = true;
        q.IssuedFrame = device.Frames.FrameNumber;
        q.Result = null;
    }

    public void GetQueryObject(uint id, QueryObjectParameterName pname, out int @params)
    {
        if (pname == QueryObjectParameterName.ResultAvailable) { @params = Available(queries[id]) ? 1 : 0; return; }
        GetQueryObject(id, pname, out ulong v);
        @params = (int)v;
    }

    public void GetQueryObject(uint id, QueryObjectParameterName pname, out ulong @params)
    {
        var q = queries[id];
        if (pname == QueryObjectParameterName.ResultAvailable) { @params = Available(q) ? 1u : 0u; return; }
        if (q.Result is null && q.IssuedFrame == device.Frames.FrameNumber && frameOpen) Flush();   // not submitted yet
        @params = q.Result ?? Read(q, wait: true) ?? 0;
    }

    bool Available(GlQueryObj q)
    {
        if (!q.Issued || q.Result is not null) return true;
        if (q.IssuedFrame == device.Frames.FrameNumber && frameOpen) return false;   // not submitted yet
        return Read(q, wait: false) is not null;
    }

    ulong? Read(GlQueryObj q, bool wait)
    {
        if (q.Result is { } r) return r;
        if (q.Base < 0) return null;
        var values = stackalloc ulong[4];
        var flags = QueryResultFlags.Result64Bit | QueryResultFlags.ResultWithAvailabilityBit | (wait ? QueryResultFlags.ResultWaitBit : 0);
        uint first = (uint)(q.Base + (q.Elapsed ? 0 : 1)), count = q.Elapsed ? 2u : 1u;
        var result = vk.GetQueryPoolResults(dev, q.Pool, first, count, (nuint)(count * 16), values, 16, flags);
        if (result != Result.Success && result != Result.NotReady) return null;
        if (q.Elapsed ? values[1] == 0 || values[3] == 0 : values[1] == 0) return null;
        double period = device.Limits.TimestampPeriod;
        q.Result = q.Elapsed ? (ulong)((values[2] - values[0]) * period) : (ulong)(values[0] * period);
        return q.Result;
    }

    void PollQueries() { }

    void DestroyQueries()
    {
        if (queryPools is not null) foreach (var pool in queryPools) vk.DestroyQueryPool(dev, pool, null);
    }
}
