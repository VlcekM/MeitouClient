using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu.Core;

/// <summary>
/// Barriers to record at once: memory dependencies only (the layouts stay GENERAL,
/// docs/renderer-native.md 4.4). Vulkan's own masks: the engine's words are the dependencies
/// (<see cref="GpuDependency"/>), and the backend turns them into this.
/// </summary>
public struct BarrierBatch
{
    public PipelineStageFlags2 SrcStages, DstStages;
    public AccessFlags2 SrcAccess, DstAccess;
    public readonly bool IsEmpty => SrcStages == 0 && DstStages == 0;

    public void Add(PipelineStageFlags2 srcStages, AccessFlags2 srcAccess, PipelineStageFlags2 dstStages, AccessFlags2 dstAccess)
    {
        SrcStages |= srcStages; SrcAccess |= srcAccess;
        DstStages |= dstStages; DstAccess |= dstAccess;
    }

    /// <summary>Everything before against everything after (the seam's barrier).</summary>
    public static BarrierBatch Full => new()
    {
        SrcStages = PipelineStageFlags2.AllCommandsBit, SrcAccess = AccessFlags2.MemoryWriteBit,
        DstStages = PipelineStageFlags2.AllCommandsBit, DstAccess = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
    };
}
