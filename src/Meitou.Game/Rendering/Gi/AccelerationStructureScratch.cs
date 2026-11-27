using System.Diagnostics;
using System.Numerics;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// Bump-allocated shared scratch memory for acceleration structure builds and refits on the graphics queue.
/// </summary>
internal sealed class AccelerationStructureScratch(VulkanDevice device) : IDisposable
{
    /// <summary>
    /// Initial scratch slab size in bytes.
    /// </summary>
    const ulong MinSlabBytes = 8UL << 20;

    /// <summary>
    /// Maximum slab size grown for multi-build frames before wrapping with a barrier.
    /// </summary>
    const ulong MaxSlabBytes = 16UL << 20;

    /// <summary>
    /// Idle duration in seconds before releasing an unused scratch slab.
    /// </summary>
    const double IdleSeconds = 60;

    /// <summary>
    /// The current buffer, or null until the first build.
    /// </summary>
    BufferAllocation? _buffer;

    /// <summary>
    /// Next free byte offset inside <see cref="_buffer"/> for this frame.
    /// </summary>
    ulong _cursor;

    /// <summary>
    /// Whether the next allocation must emit a build barrier before reusing <see cref="_buffer"/>.
    /// </summary>
    bool _needsBarrier;

    /// <summary>
    /// Whether <see cref="_buffer"/> has already been used by an earlier build.
    /// </summary>
    bool _usedPreviously;

    /// <summary>
    /// When the current buffer was last used to record a build.
    /// </summary>
    long _lastUse;

    /// <summary>
    /// Resets the bump cursor for a new frame so the first build orders after earlier frames and later builds run in parallel.
    /// </summary>
    public void BeginFrame()
    {
        _cursor = 0;
        _needsBarrier = _usedPreviously;
    }

    /// <summary>
    /// Returns an aligned scratch address for a build or refit, growing the slab or emitting a barrier only when needed.
    /// </summary>
    public ulong Acquire(CommandList cmd, ulong size)
    {
        if (size == 0) return 0;

        ulong alignment = Math.Max(device.ScratchAlignment, 1UL);
        ulong required = size + alignment - 1;
        if (_buffer is null)
        {
            Allocate(Math.Max(MinSlabBytes, BitOperations.RoundUpToPowerOf2(required)));
        }
        else if (!Fits(_buffer, _cursor, size, alignment))
        {
            if (_buffer.Size < MaxSlabBytes || required > _buffer.Size)
            {
                ulong target = Math.Max(required, Math.Min(MaxSlabBytes, _buffer.Size * 2));
                Allocate(BitOperations.RoundUpToPowerOf2(Math.Max(MinSlabBytes, target)));
            }
            else
            {
                _cursor = 0;
                _needsBarrier = true;
            }
        }

        if (_needsBarrier)
        {
            EmitBarrier(cmd);
            _needsBarrier = false;
        }

        ulong baseAddress = device.Address(_buffer!.Handle);
        ulong aligned = AlignUp(baseAddress + _cursor, alignment);
        _cursor = aligned - baseAddress + size;
        _usedPreviously = true;
        _lastUse = Stopwatch.GetTimestamp();
        return aligned;
    }

    /// <summary>
    /// Releases the buffer after a minute without builds.
    /// </summary>
    public void TrimIdle()
    {
        if (_buffer is not null && Stopwatch.GetElapsedTime(_lastUse).TotalSeconds >= IdleSeconds) Dispose();
    }

    /// <summary>
    /// Defers freeing scratch until its recorded builds have finished.
    /// </summary>
    public void Dispose()
    {
        if (_buffer is null) return;

        device.DeferFree(_buffer);
        _buffer = null;
        _cursor = 0;
        _needsBarrier = false;
        _usedPreviously = false;
    }

    /// <summary>
    /// Replaces the current scratch slab with a fresh device-local buffer of <paramref name="capacity"/> bytes.
    /// </summary>
    void Allocate(ulong capacity)
    {
        var replacement = device.Allocator.CreateBuffer(capacity,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit, MemoryKind.DeviceLocal, "gi build scratch");
        Dispose();
        _buffer = replacement;
    }

    /// <summary>
    /// Whether an aligned allocation of <paramref name="size"/> bytes fits at <paramref name="cursor"/> inside <paramref name="buffer"/>.
    /// </summary>
    bool Fits(BufferAllocation buffer, ulong cursor, ulong size, ulong alignment)
    {
        ulong baseAddress = device.Address(buffer.Handle);
        ulong aligned = AlignUp(baseAddress + cursor, alignment);
        return aligned - baseAddress + size <= buffer.Size;
    }

    /// <summary>
    /// Rounds <paramref name="address"/> up to a multiple of <paramref name="alignment"/>.
    /// </summary>
    static ulong AlignUp(ulong address, ulong alignment) => (address + alignment - 1) / alignment * alignment;

    /// <summary>
    /// Orders subsequent acceleration structure builds after earlier builds that wrote to the same scratch memory.
    /// </summary>
    static void EmitBarrier(CommandList cmd) => cmd.Barrier(new BarrierBatch
    {
        SrcStages = PipelineStageFlags2.AccelerationStructureBuildBitKhr,
        SrcAccess = AccessFlags2.AccelerationStructureWriteBitKhr,
        DstStages = PipelineStageFlags2.AccelerationStructureBuildBitKhr,
        DstAccess = AccessFlags2.AccelerationStructureReadBitKhr | AccessFlags2.AccelerationStructureWriteBitKhr,
    });
}
