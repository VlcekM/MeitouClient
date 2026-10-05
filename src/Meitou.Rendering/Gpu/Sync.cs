using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu;

/// <summary>How a pass uses a resource (docs/renderer-native.md 2.9).</summary>
public enum Access { ColourTarget, DepthTarget, DepthRead, Sampled, StorageRead, StorageWrite, TransferSrc, TransferDst, IndirectRead, VertexRead, UniformRead, Present }

/// <summary>A resource a pass reads or writes: a <see cref="Texture"/>, <see cref="DeviceBuffer"/> or any other object identifying it.</summary>
public readonly record struct PassUse(object Resource, Access Access, ImageSubresourceRange? Range = null);

/// <summary>Barriers to record at once: memory dependencies only (layouts stay GENERAL, docs/renderer-native.md 4.4).</summary>
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

/// <summary>
/// The last access of every resource a frame's native passes touched, and the barrier a new access needs (stage and access masks from a fixed
/// table). Read after read needs none; anything involving a write gets an execution and memory dependency. All barriers a pass needs are
/// merged into one <c>vkCmdPipelineBarrier2</c>. A seam crossing orders everything (<see cref="AssumeFullBarrier"/>). Render thread only.
/// </summary>
public sealed class ResourceStates
{
    readonly Dictionary<object, Access> last = new(ReferenceEqualityComparer.Instance);

    /// <summary>The stages and access of an <see cref="Access"/>, and whether it writes.</summary>
    public static (PipelineStageFlags2 Stages, AccessFlags2 Access, bool Write) Table(Access a) => a switch
    {
        Access.ColourTarget => (PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit, true),
        Access.DepthTarget => (PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
            AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit, true),
        Access.DepthRead => (PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit, AccessFlags2.DepthStencilAttachmentReadBit, false),
        Access.Sampled => (PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit, false),
        Access.StorageRead => (PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit, false),
        Access.StorageWrite => (PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit, true),
        Access.TransferSrc => (PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferReadBit, false),
        Access.TransferDst => (PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit, true),
        Access.IndirectRead => (PipelineStageFlags2.DrawIndirectBit, AccessFlags2.IndirectCommandReadBit, false),
        Access.VertexRead => (PipelineStageFlags2.VertexAttributeInputBit | PipelineStageFlags2.IndexInputBit, AccessFlags2.VertexAttributeReadBit | AccessFlags2.IndexReadBit, false),
        Access.UniformRead => (PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit, AccessFlags2.UniformReadBit, false),
        Access.Present => (PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryReadBit, false),
        _ => throw new ArgumentOutOfRangeException(nameof(a)),
    };

    /// <summary>The barrier needed for <paramref name="uses"/> after what was recorded; the new accesses become the last ones.</summary>
    public BarrierBatch Plan(ReadOnlySpan<PassUse> uses)
    {
        var batch = new BarrierBatch();
        foreach (var u in uses)
        {
            var (dstStages, dstAccess, write) = Table(u.Access);
            if (last.TryGetValue(u.Resource, out var previous))
            {
                var (srcStages, srcAccess, wrote) = Table(previous);
                // Write then anything: make the write available and visible. Read then write: an execution dependency suffices.
                if (wrote) batch.Add(srcStages, srcAccess & WriteMask, dstStages, dstAccess);
                else if (write) batch.Add(srcStages, 0, dstStages, 0);
            }
            last[u.Resource] = u.Access;
        }
        return batch;
    }

    const AccessFlags2 WriteMask = AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.DepthStencilAttachmentWriteBit | AccessFlags2.ShaderStorageWriteBit |
        AccessFlags2.TransferWriteBit | AccessFlags2.MemoryWriteBit;

    /// <summary>Plans and records the barrier for <paramref name="uses"/> (one call, nothing when no hazard).</summary>
    public void Use(CommandList cmd, ReadOnlySpan<PassUse> uses)
    {
        var batch = Plan(uses);
        if (!batch.IsEmpty) cmd.Barrier(in batch);
    }

    /// <summary>After a full barrier (a seam crossing, the start of a frame): nothing is pending.</summary>
    public void AssumeFullBarrier() => last.Clear();

    public int Tracked => last.Count;
}

/// <summary>
/// A labelled native pass: declares its uses (barriers from <see cref="ResourceStates"/>), opens a debug label and closes it on dispose.
/// <c>using var pass = new GpuPass(frame, cmd, "shadows c2", uses);</c>
/// </summary>
public readonly ref struct GpuPass
{
    readonly CommandList cmd;

    public GpuPass(GpuFrame frame, CommandList cmd, string label, ReadOnlySpan<PassUse> uses)
    {
        this.cmd = cmd;
        frame.States.Use(cmd, uses);
        cmd.BeginLabel(label);
    }

    public void Dispose() => cmd.EndLabel();
}
