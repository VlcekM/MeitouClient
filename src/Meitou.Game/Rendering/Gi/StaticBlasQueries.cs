using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// Batches compacted-size queries for the static mesh builds of each frame slot.
/// </summary>
internal sealed class StaticBlasQueries(GpuContext ctx) : IDisposable
{
    /// <summary>
    /// The maximum number of static builds queried and compacted per frame.
    /// </summary>
    const int Capacity = 16;

    /// <summary>
    /// The queries, stored in consecutive ranges by frame slot.
    /// </summary>
    QueryPool _pool;

    /// <summary>
    /// The mesh builds corresponding to each slot's queries.
    /// </summary>
    readonly List<(object Key, GiScene.MeshEntry Entry, string Name)>[] _pending =
        Enumerable.Range(0, ctx.Device.Frames.Count).Select(_ => new List<(object, GiScene.MeshEntry, string)>()).ToArray();

    /// <summary>
    /// Static mesh builds queued during the current frame until <see cref="Flush"/> records their queries in one batch.
    /// </summary>
    readonly List<(object Key, GiScene.MeshEntry Entry, string Name)> _queued = [];

    /// <summary>
    /// The frame that recorded each slot's pending queries.
    /// </summary>
    readonly long[] _recordedFrame = new long[ctx.Device.Frames.Count];

    /// <summary>
    /// Whether this frame has space for another query.
    /// </summary>
    public bool HasRoom => _pending[ctx.Frame.Slot].Count == 0 && _queued.Count < Capacity;

    /// <summary>
    /// Queues a static mesh built in the current frame so its compacted size is queried in one batch by <see cref="Flush"/>.
    /// </summary>
    public void Enqueue(object key, GiScene.MeshEntry entry, string name)
    {
        if (!HasRoom) return;
        _queued.Add((key, entry, name));
    }

    /// <summary>
    /// Records one barrier, one query-pool reset and one batched compacted-size write for all static builds queued this frame.
    /// </summary>
    public unsafe void Flush(Func<CommandList> open)
    {
        int count = _queued.Count;
        if (count == 0) return;

        int slot = ctx.Frame.Slot;
        var pending = _pending[slot];
        if (pending.Count > 0)
        {
            _queued.Clear();
            return;
        }

        if (_pool.Handle == 0) CreatePool();

        var cmd = open();
        var device = ctx.Device;
        uint firstQuery = (uint)(slot * Capacity);
        var handles = stackalloc AccelerationStructureKHR[count];
        for (int i = 0; i < count; i++) handles[i] = _queued[i].Entry.Blas.Handle;

        device.Vk.CmdResetQueryPool(cmd.Handle, _pool, firstQuery, (uint)count);
        cmd.Barrier(AccelerationStructure.BuildToTrace);
        device.AccelerationStructures!.CmdWriteAccelerationStructuresProperties(cmd.Handle, (uint)count, handles,
            QueryType.AccelerationStructureCompactedSizeKhr, _pool, firstQuery);

        pending.AddRange(_queued);
        _queued.Clear();
        _recordedFrame[slot] = ctx.Frame.Number;
    }

    /// <summary>
    /// Reads the previous builds of the current frame slot, whose fence was waited before recording began.
    /// </summary>
    public IEnumerable<(object Key, GiScene.MeshEntry Entry, string Name, ulong Size)> Read()
    {
        int slot = ctx.Frame.Slot;
        var list = _pending[slot];
        if (list.Count == 0 || !ctx.FrameCompleted(_recordedFrame[slot])) yield break;

        var sizes = ReadSizes(slot, list.Count);
        for (int i = 0; i < sizes.Length; i++)
        {
            var (key, entry, name) = list[i];
            yield return (key, entry, name, sizes[i]);
        }

        list.Clear();
    }

    /// <summary>
    /// Defers destruction until all submitted compacted-size queries have completed.
    /// </summary>
    public unsafe void Dispose()
    {
        if (_pool.Handle == 0) return;

        var device = ctx.Device;
        var pool = _pool;
        device.Frames.DeferDelete(() => device.Vk.DestroyQueryPool(device.Device, pool, null));
        _pool = default;
    }

    /// <summary>
    /// Makes enough queries for all frame slots.
    /// </summary>
    unsafe void CreatePool()
    {
        var device = ctx.Device;
        var info = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.AccelerationStructureCompactedSizeKhr,
            QueryCount = (uint)(_pending.Length * Capacity),
        };

        VulkanException.Check(device.Vk.CreateQueryPool(device.Device, &info, null, out _pool), "vkCreateQueryPool(BLAS sizes)");
    }

    /// <summary>
    /// Retrieves the available 64-bit sizes without waiting on the GPU.
    /// </summary>
    unsafe ulong[] ReadSizes(int slot, int count)
    {
        var device = ctx.Device;
        var sizes = new ulong[count];

        fixed (ulong* output = sizes)
        {
            var result = device.Vk.GetQueryPoolResults(device.Device, _pool, (uint)(slot * Capacity), (uint)count,
                (nuint)(count * sizeof(ulong)), output, sizeof(ulong), QueryResultFlags.Result64Bit);
            if (result == Result.NotReady) return [];
            VulkanException.Check(result, "vkGetQueryPoolResults(BLAS sizes)");
        }

        return sizes;
    }
}
