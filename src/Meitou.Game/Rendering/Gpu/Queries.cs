using System.Diagnostics;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>A timestamp handed out by <see cref="QueryArena.Allocate"/>: the frame it belongs to and its index in that slot's pool.</summary>
public readonly record struct QuerySlot(long Frame, int Slot, uint Index)
{
    public bool IsValid => Frame > 0;
}

/// <summary>
/// GPU timestamps (docs/renderer-native.md 2.10): one pool per frame slot, reset from the host when the slot comes round (host query reset),
/// after the slot's previous results were collected. Indices are handed out with an interlocked counter, so any recording thread may take
/// them. Results are read a frame-ring later, never waited for: <see cref="TryRead"/> answers once the slot's frame has completed.
/// </summary>
public sealed unsafe class QueryArena : IGpuTimestamps, IDisposable
{
    public const uint Capacity = 4096;
    readonly VulkanDevice _device;
    readonly QueryPool[] _pools;
    readonly long[] _poolFrame;      // the frame whose timestamps a pool holds (or held, when collected)
    readonly int[] _poolCount;       // timestamps recorded into it
    readonly long[] _collectedFrame; // the frame whose results are in results[]
    readonly ulong[][] _results;     // collected, in nanoseconds; 0 = not written
    int _cursor;
    int _slot = -1;
    long _frame;

    public QueryArena(VulkanDevice device)
    {
        this._device = device;
        int n = device.Frames.Count;
        _pools = new QueryPool[n];
        _poolFrame = new long[n];
        _poolCount = new int[n];
        _collectedFrame = new long[n];
        _results = new ulong[n][];
        var info = new QueryPoolCreateInfo { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Timestamp, QueryCount = Capacity };
        for (int i = 0; i < n; i++)
        {
            VulkanException.Check(device.Vk.CreateQueryPool(device.Device, &info, null, out _pools[i]), "vkCreateQueryPool");
            device.Vk.ResetQueryPool(device.Device, _pools[i], 0, Capacity);
            _results[i] = new ulong[Capacity];
        }
    }

    /// <summary>Timestamps taken this frame.</summary>
    public int Count => Volatile.Read(ref _cursor);

    /// <summary>At the start of a frame in <paramref name="frameSlot"/> (its fence has passed): collects what the slot's pool held, resets it.</summary>
    internal void Begin(int frameSlot, long frameNumber)
    {
        Collect(frameSlot);
        _device.Vk.ResetQueryPool(_device.Device, _pools[frameSlot], 0, Capacity);
        _slot = frameSlot;
        _frame = frameNumber;
        _poolFrame[frameSlot] = frameNumber;
        _poolCount[frameSlot] = 0;
        _cursor = 0;
    }

    internal void End()
    {
        if (_slot >= 0) _poolCount[_slot] = Math.Min(_cursor, (int)Capacity);
    }

    void Collect(int s)
    {
        Array.Clear(_results[s]);
        int n = _poolCount[s];
        if (n == 0) return;
        var values = new ulong[n * 2];
        fixed (ulong* p = values)
        {
            var r = _device.Vk.GetQueryPoolResults(_device.Device, _pools[s], 0, (uint)n, (nuint)(values.Length * 8), p, 16,
                QueryResultFlags.Result64Bit | QueryResultFlags.ResultWithAvailabilityBit);
            if (r is not (Result.Success or Result.NotReady)) return;
        }
        double period = _device.Limits.TimestampPeriod;
        for (int i = 0; i < n; i++)
            if (values[i * 2 + 1] != 0) _results[s][i] = Math.Max(1, (ulong)(values[i * 2] * period));
        _collectedFrame[s] = _poolFrame[s];
    }

    /// <summary>A timestamp index in this frame's pool; invalid (Frame 0) when the pool is full or no frame is open.</summary>
    public QuerySlot Allocate()
    {
        if (_slot < 0) return default;
        int i = Interlocked.Increment(ref _cursor) - 1;
        if (i >= Capacity) return default;
        return new QuerySlot(_frame, _slot, (uint)i);
    }

    internal QueryPool Pool(in QuerySlot q) => _pools[q.Slot];

    /// <summary>The timestamp in nanoseconds once its frame has completed and been collected (a frame ring later), else false.</summary>
    public bool TryRead(in QuerySlot q, out ulong nanoseconds)
    {
        nanoseconds = 0;
        if (!q.IsValid || _collectedFrame[q.Slot] != q.Frame) return false;
        nanoseconds = _results[q.Slot][q.Index];
        return nanoseconds != 0;
    }

    public void Dispose()
    {
        foreach (var p in _pools) _device.Vk.DestroyQueryPool(_device.Device, p, null);
    }
}

/// <summary>Per-frame counters of the native API (docs/renderer-native.md 2.10).</summary>
public sealed class GpuStats
{
    public int Draws, IndirectDraws, Dispatches, PipelinesBound, DescriptorPushes, NativeSegments;
    public long ConstantBytes, UploadBytes;

    /// <summary>The counters <see cref="Running"/> gives, in its order.</summary>
    public static readonly string[] CounterNames = ["draws", "indirect", "dispatches", "pipelines", "pushes", "segments", "constantBytes", "uploadBytes"];
    readonly long[] _carried = new long[8];

    /// <summary>Starts the next frame's counts; what this frame counted is kept in the running totals.</summary>
    public void Reset()
    {
        Span<long> now = stackalloc long[8];
        Running(now);
        now.CopyTo(_carried);
        Draws = IndirectDraws = Dispatches = PipelinesBound = DescriptorPushes = NativeSegments = 0;
        ConstantBytes = UploadBytes = 0;
    }

    /// <summary>The counters summed over every frame so far, this one included (<see cref="CounterNames"/>): what a meter takes differences of.</summary>
    public void Running(Span<long> into)
    {
        into[0] = _carried[0] + Draws; into[1] = _carried[1] + IndirectDraws; into[2] = _carried[2] + Dispatches; into[3] = _carried[3] + PipelinesBound;
        into[4] = _carried[4] + DescriptorPushes; into[5] = _carried[5] + NativeSegments; into[6] = _carried[6] + ConstantBytes; into[7] = _carried[7] + UploadBytes;
    }

    public override string ToString() =>
        $"native: {Draws} draws ({IndirectDraws} indirect), {Dispatches} dispatches, {PipelinesBound} pipeline binds, {DescriptorPushes} pushes, " +
        $"{NativeSegments} segments, {ConstantBytes / 1024} KB constants, {UploadBytes / 1024} KB uploads";
}