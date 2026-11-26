using System.Diagnostics;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// Scratch memory reused by serial acceleration structure builds on the graphics queue.
/// </summary>
internal sealed class AccelerationStructureScratch(VulkanDevice device) : IDisposable
{
    /// <summary>
    /// The current buffer, or null until the first build.
    /// </summary>
    BufferAllocation? _buffer;

    /// <summary>
    /// When the current buffer was last used to record a build.
    /// </summary>
    long _lastUse;

    /// <summary>
    /// Orders reuse after earlier builds and returns an address with the device's required alignment.
    /// </summary>
    public ulong Acquire(CommandList cmd, ulong size)
    {
        if (size == 0) return 0;

        ulong alignment = device.ScratchAlignment;
        ulong required = size + alignment - 1;
        if (_buffer is null || _buffer.Size < required)
        {
            var replacement = device.Allocator.CreateBuffer(required,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit, MemoryKind.DeviceLocal, "gi build scratch");
            Dispose();
            _buffer = replacement;
        }
        else
        {
            cmd.Barrier(new BarrierBatch
            {
                SrcStages = PipelineStageFlags2.AccelerationStructureBuildBitKhr,
                SrcAccess = AccessFlags2.AccelerationStructureWriteBitKhr,
                DstStages = PipelineStageFlags2.AccelerationStructureBuildBitKhr,
                DstAccess = AccessFlags2.AccelerationStructureReadBitKhr | AccessFlags2.AccelerationStructureWriteBitKhr,
            });
        }

        _lastUse = Stopwatch.GetTimestamp();
        ulong address = device.Address(_buffer.Handle);
        return (address + alignment - 1) / alignment * alignment;
    }

    /// <summary>
    /// Releases the buffer after five seconds without builds.
    /// </summary>
    public void TrimIdle()
    {
        if (_buffer is not null && Stopwatch.GetElapsedTime(_lastUse).TotalSeconds >= 5) Dispose();
    }

    /// <summary>
    /// Defers freeing scratch until its recorded builds have finished.
    /// </summary>
    public void Dispose()
    {
        if (_buffer is null) return;

        device.DeferFree(_buffer);
        _buffer = null;
    }
}
