using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu;

/// <summary>
/// Copies data into device buffers and textures through the frame's upload command buffer, which the host submits ahead of the frame's own
/// (VkGl: <c>uploadCmd</c>, "before: uploadCmd"). An upload is therefore seen by the whole frame, and native and VkGl uploads keep their call
/// order. Staging comes from the frame's <see cref="GpuFrame.Staging"/>. Render thread only, while a frame is open.
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
        var staging = ctx.Frame.Staging.Write(data);
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
        var staging = ctx.Frame.Staging.Write(data);
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

    /// <summary>
    /// (Added in phase 8 stage 1, terrain.) Uploads and the commands that belong with them (a new texture's layout transition, its mip
    /// generation) for resources made outside the frame's passes, whether or not a frame is open: while one is, into the frame's upload
    /// command buffer (<see cref="GpuFrame.PreFrame"/>, as <see cref="Write(Texture, int, int, in Rect2D, ReadOnlySpan{byte})"/>); otherwise
    /// (at load, before the first frame: VkGl opened a frame for such uploads, the native API does not) into a one-shot command buffer with
    /// staging of its own, which <see cref="UploadBatch.Dispose"/> submits and waits for. Render thread only.
    /// </summary>
    public UploadBatch Begin() => new(ctx);

    /// <summary>Orders successive uploads (two writes to overlapping ranges must not race).</summary>
    internal static void TransferBarrier(Vk vk, CommandBuffer cmd)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllTransferBit, SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.AllTransferBit, DstAccessMask = AccessFlags2.TransferReadBit | AccessFlags2.TransferWriteBit,
        };
        var info = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
        vk.CmdPipelineBarrier2(cmd, &info);
    }

    void TransferBarrier(CommandBuffer cmd) => TransferBarrier(ctx.Device.Vk, cmd);
}

/// <summary>
/// A batch of uploads (<see cref="Uploader.Begin"/>): into the open frame's upload command buffer, or, without an open frame, into a
/// one-shot command buffer that <see cref="Dispose"/> submits and waits for. <see cref="Commands"/> takes what goes with the uploads (layout
/// transitions, <see cref="CommandList.GenerateMips"/>); they run after the uploads recorded before them.
/// </summary>
public sealed unsafe class UploadBatch : IDisposable
{
    readonly GpuContext ctx;
    readonly bool immediate;
    readonly List<Meitou.Rendering.Gpu.Core.GpuBuffer> staging = [];

    internal UploadBatch(GpuContext ctx)
    {
        this.ctx = ctx;
        immediate = !ctx.Frame.Open;
        Commands = immediate ? new CommandList(ctx.Device, new GpuStats()) { Handle = ctx.Device.BeginImmediate() } : ctx.Frame.PreFrame;
    }

    /// <summary>Where the batch records (the frame's <see cref="GpuFrame.PreFrame"/>, or the one-shot buffer).</summary>
    public CommandList Commands { get; }

    /// <summary>A new texture in GENERAL layout, its transition recorded into this batch.</summary>
    public Texture Create(TextureDesc desc) => Texture.Create(ctx, desc, Commands.Handle);

    /// <summary>Writes texels of one level and layer (tightly packed, in the texture's format; for block-compressed formats whole blocks).</summary>
    public void Write(Texture target, int level, int layer, in Rect2D region, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        if (!immediate) { ctx.Uploads.Write(target, level, layer, region, data); return; }
        var (buffer, offset) = Stage(data);
        var copy = new BufferImageCopy
        {
            BufferOffset = offset,
            ImageSubresource = new ImageSubresourceLayers(target.Desc.Aspect, (uint)level, (uint)layer, 1),
            ImageOffset = new Offset3D(region.Offset.X, region.Offset.Y, 0),
            ImageExtent = new Extent3D(region.Extent.Width, region.Extent.Height, 1),
        };
        ctx.Device.Vk.CmdCopyBufferToImage(Commands.Handle, buffer, target.Image, ImageLayout.General, 1, &copy);
        Uploader.TransferBarrier(ctx.Device.Vk, Commands.Handle);
    }

    /// <summary>Writes a device buffer's bytes from <paramref name="offset"/>.</summary>
    public void Write(DeviceBuffer target, ulong offset, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        if (!immediate) { ctx.Uploads.Write(target, offset, data); return; }
        if (offset + (ulong)data.Length > target.Size) throw new ArgumentOutOfRangeException(nameof(data), "write past the end of the buffer");
        var (buffer, from) = Stage(data);
        var copy = new BufferCopy(from, offset, (ulong)data.Length);
        ctx.Device.Vk.CmdCopyBuffer(Commands.Handle, buffer, target.Handle, 1, &copy);
        Uploader.TransferBarrier(ctx.Device.Vk, Commands.Handle);
    }

    (Silk.NET.Vulkan.Buffer Buffer, ulong Offset) Stage(ReadOnlySpan<byte> data)
    {
        var b = ctx.Device.Allocator.CreateBuffer((ulong)data.Length, BufferUsageFlags.TransferSrcBit, Meitou.Rendering.Gpu.Core.MemoryKind.Upload, "upload staging");
        staging.Add(b);
        data.CopyTo(new Span<byte>((void*)b.Mapped, data.Length));
        return (b.Buffer, 0);
    }

    /// <summary>Without an open frame: submits the batch and waits for it. With one: nothing (the frame submits its upload buffer).</summary>
    public void Dispose()
    {
        if (!immediate) return;
        ctx.Device.EndImmediate(Commands.Handle);
        foreach (var b in staging) ctx.Device.Allocator.Free(b);
        staging.Clear();
    }
}
