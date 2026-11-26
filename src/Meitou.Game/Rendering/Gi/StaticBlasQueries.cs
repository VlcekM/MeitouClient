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
    /// The maximum number of static builds queried per frame.
    /// </summary>
    const int Capacity = 64;

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
    /// The frame that recorded each slot's pending queries.
    /// </summary>
    readonly long[] _recordedFrame = new long[ctx.Device.Frames.Count];

    /// <summary>
    /// Whether this frame has space for another query.
    /// </summary>
    public bool HasRoom => _pending[ctx.Frame.Slot].Count < Capacity;

    /// <summary>
    /// Records a compacted-size query for a mesh built in the current frame slot.
    /// </summary>
    public unsafe void Record(CommandList cmd, object key, GiScene.MeshEntry entry, string name)
    {
        if (!HasRoom) return;
        if (_pool.Handle == 0) CreatePool();

        var list = _pending[ctx.Frame.Slot];
        uint index = (uint)(ctx.Frame.Slot * Capacity + list.Count);
        var device = ctx.Device;
        var handle = entry.Blas.Handle;
        const QueryType type = QueryType.AccelerationStructureCompactedSizeKhr;

        device.Vk.CmdResetQueryPool(cmd.Handle, _pool, index, 1);
        cmd.Barrier(AccelerationStructure.BuildToTrace);
        device.AccelerationStructures!.CmdWriteAccelerationStructuresProperties(cmd.Handle, 1, &handle, type, _pool, index);
        list.Add((key, entry, name));
        _recordedFrame[ctx.Frame.Slot] = ctx.Frame.Number;
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
