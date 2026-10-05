using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu;

/// <summary>
/// Copies data into device buffers and textures through the frame's upload command buffer, which the host submits ahead of the frame's own
/// (VkGl: <c>uploadCmd</c>, "before: uploadCmd"). An upload is therefore seen by the whole frame, and native and VkGl uploads keep their call
/// order. Staging comes from the frame's <see cref="GpuFrame.Constants"/>. Render thread only, while a frame is open.
/// </summary>
public sealed unsafe class Uploader
{
    readonly GpuContext ctx;

    internal Uploader(GpuContext ctx) => this.ctx = ctx;

    CommandBuffer Cmd => ctx.Frame.Open ? ctx.Frame.UploadCommands : throw new InvalidOperationException("uploads need an open frame");

    public void Write(DeviceBuffer target, ulong offset, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        if (target.UsedFrame == ctx.Frame.Number)
            throw new InvalidOperationException("the frame already drew from this buffer: per-frame data belongs in a Transient (docs/renderer-native.md 2.3)");
        if (offset + (ulong)data.Length > target.Size) throw new ArgumentOutOfRangeException(nameof(data), "write past the end of the buffer");
        var staging = ctx.Frame.Constants.Write(data);
        var copy = new BufferCopy(staging.Offset, offset, (ulong)data.Length);
        var cmd = Cmd;
        ctx.Device.Vk.CmdCopyBuffer(cmd, staging.Handle, target.Handle, 1, &copy);
        TransferBarrier(cmd);
        ctx.Frame.Stats.UploadBytes += data.Length;
    }

    /// <summary>Writes texels of one level and layer (tightly packed, in the texture's format; for block-compressed formats whole blocks).</summary>
    public void Write(Texture target, int level, int layer, in Rect2D region, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        var staging = ctx.Frame.Constants.Write(data);
        var copy = new BufferImageCopy
        {
            BufferOffset = staging.Offset,
            ImageSubresource = new ImageSubresourceLayers(target.Desc.Aspect, (uint)level, (uint)layer, 1),
            ImageOffset = new Offset3D(region.Offset.X, region.Offset.Y, 0),
            ImageExtent = new Extent3D(region.Extent.Width, region.Extent.Height, 1),
        };
        var cmd = Cmd;
        ctx.Device.Vk.CmdCopyBufferToImage(cmd, staging.Handle, target.Image, ImageLayout.General, 1, &copy);
        TransferBarrier(cmd);
        ctx.Frame.Stats.UploadBytes += data.Length;
    }

    /// <summary>Orders successive uploads (two writes to overlapping ranges must not race).</summary>
    void TransferBarrier(CommandBuffer cmd)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllTransferBit, SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.AllTransferBit, DstAccessMask = AccessFlags2.TransferReadBit | AccessFlags2.TransferWriteBit,
        };
        var info = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
        ctx.Device.Vk.CmdPipelineBarrier2(cmd, &info);
    }
}
