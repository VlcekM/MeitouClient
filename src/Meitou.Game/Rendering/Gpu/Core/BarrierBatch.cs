using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gpu.Core;

/// <summary>
/// Barriers to record at once: memory dependencies only (the layouts stay GENERAL, docs/renderer-native.md 4.4). Vulkan's own masks: the engine's
/// words are the dependencies (<see cref="GpuDependency"/>), and the backend turns them into this.
/// </summary>
public struct BarrierBatch
{
    /// <summary>
    /// The stages and accesses before the barrier, OR-ed over every <see cref="Add"/>.
    /// </summary>
    public PipelineStageFlags2 SrcStages;

    /// <summary>
    /// The stages after the barrier, OR-ed over every <see cref="Add"/>.
    /// </summary>
    public PipelineStageFlags2 DstStages;

    /// <summary>
    /// The source accesses, OR-ed over every <see cref="Add"/>.
    /// </summary>
    public AccessFlags2 SrcAccess;

    /// <summary>
    /// The destination accesses, OR-ed over every <see cref="Add"/>.
    /// </summary>
    public AccessFlags2 DstAccess;

    /// <summary>
    /// Whether nothing was added.
    /// </summary>
    public readonly bool IsEmpty => SrcStages == 0 && DstStages == 0;

    /// <summary>
    /// ORs a dependency's stages and accesses into the batch.
    /// </summary>
    public void Add(PipelineStageFlags2 srcStages, AccessFlags2 srcAccess, PipelineStageFlags2 dstStages, AccessFlags2 dstAccess)
    {
        SrcStages |= srcStages;
        SrcAccess |= srcAccess;
        DstStages |= dstStages;
        DstAccess |= dstAccess;
    }

    /// <summary>
    /// Everything before against everything after (the seam's barrier).
    /// </summary>
    public static BarrierBatch Full => new()
    {
        SrcStages = PipelineStageFlags2.AllCommandsBit,
        SrcAccess = AccessFlags2.MemoryWriteBit,
        DstStages = PipelineStageFlags2.AllCommandsBit,
        DstAccess = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
    };
}
