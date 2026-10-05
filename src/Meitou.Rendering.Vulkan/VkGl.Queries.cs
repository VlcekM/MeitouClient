using Silk.NET.OpenGL;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Vulkan;

public sealed unsafe partial class VkGl
{
    /// <summary>
    /// A GL query: a timestamp (<c>glQueryCounter</c>) or an elapsed time (<c>glBeginQuery(TIME_ELAPSED)</c>, two timestamps).
    /// Each query has its own two slots in a shared pool, reset from the host (Vulkan 1.2 host query reset) when it is issued; the
    /// renderers re-issue a query only after reading its previous result, as GL code must.
    /// </summary>
    internal sealed class GlQueryObj(int slot)
    {
        public readonly int Slot = slot;   // slots 2*Slot and 2*Slot+1 in the pool
        public bool Elapsed, Issued;
        public long IssuedFrame = -1;
        public ulong? Result;
        public void Dispose(VkGl gl) { }
    }

    QueryPool queryPool;
    const int MaxQueries = 4096;
    int nextQuerySlot;
    readonly Stack<int> freeQuerySlots = new();

    void EnsureQueryPool()
    {
        if (queryPool.Handle != 0) return;
        var info = new QueryPoolCreateInfo { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Timestamp, QueryCount = MaxQueries * 2 };
        Check(vk.CreateQueryPool(dev, &info, null, out queryPool));
        vk.ResetQueryPool(dev, queryPool, 0, MaxQueries * 2);
    }

    public uint GenQuery()
    {
        EnsureQueryPool();
        int slot = freeQuerySlots.Count > 0 ? freeQuerySlots.Pop() : nextQuerySlot++;
        if (slot >= MaxQueries) throw new InvalidOperationException("too many GL queries");
        uint id = NewId();
        queries[id] = new GlQueryObj(slot);
        return id;
    }

    public void DeleteQuery(uint id)
    {
        if (queries.Remove(id, out var q)) freeQuerySlots.Push(q.Slot);
    }

    public void QueryCounter(uint id, QueryCounterTarget target)
    {
        var q = queries[id];
        Issue(q, elapsed: false);
        vk.CmdWriteTimestamp2(Cmd, PipelineStageFlags2.AllCommandsBit, queryPool, (uint)(q.Slot * 2 + 1));
    }

    GlQueryObj? activeElapsed;

    public void BeginQuery(QueryTarget target, uint id)
    {
        var q = queries[id];
        Issue(q, elapsed: true);
        vk.CmdWriteTimestamp2(Cmd, PipelineStageFlags2.AllCommandsBit, queryPool, (uint)(q.Slot * 2));
        activeElapsed = q;
    }

    public void EndQuery(QueryTarget target)
    {
        if (activeElapsed is not { } q) return;
        vk.CmdWriteTimestamp2(Cmd, PipelineStageFlags2.AllCommandsBit, queryPool, (uint)(q.Slot * 2 + 1));
        activeElapsed = null;
    }

    void Issue(GlQueryObj q, bool elapsed)
    {
        vk.ResetQueryPool(dev, queryPool, (uint)(q.Slot * 2), 2);
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
        if (!Available(q))
        {
            if (q.IssuedFrame == device.Frames.FrameNumber && frameOpen) { Flush(); Stats.Flushes++; }
            else device.Frames.WaitAll();
        }
        @params = Read(q, wait: true) ?? 0;
    }

    bool Available(GlQueryObj q)
    {
        if (!q.Issued) return true;
        if (q.Result is not null) return true;
        if (q.IssuedFrame >= device.Frames.FrameNumber && frameOpen) return false;   // not even submitted yet
        return Read(q, wait: false) is not null;
    }

    ulong? Read(GlQueryObj q, bool wait)
    {
        if (q.Result is { } r) return r;
        var values = stackalloc ulong[4];
        var flags = QueryResultFlags.Result64Bit | QueryResultFlags.ResultWithAvailabilityBit | (wait ? QueryResultFlags.ResultWaitBit : 0);
        uint first = (uint)(q.Slot * 2 + (q.Elapsed ? 0 : 1)), count = q.Elapsed ? 2u : 1u;
        var result = vk.GetQueryPoolResults(dev, queryPool, first, count, (nuint)(count * 16), values, 16, flags);
        if (result != Result.Success && result != Result.NotReady) return null;
        if (q.Elapsed ? values[1] == 0 || values[3] == 0 : values[1] == 0) return null;
        double period = device.Limits.TimestampPeriod;
        q.Result = q.Elapsed ? (ulong)((values[2] - values[0]) * period) : (ulong)(values[0] * period);
        return q.Result;
    }

    void PollQueries() { }

    void DestroyQueries()
    {
        if (queryPool.Handle != 0) vk.DestroyQueryPool(dev, queryPool, null);
    }
}
