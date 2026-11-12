using Meitou.Game.Rendering.Profiling;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// Copies data into device buffers and textures through the frame's upload command buffer, which the host submits ahead of the frame's own (VkGl:
/// <c>uploadCmd</c>, "before: uploadCmd"). An upload is therefore seen by the whole frame, and native and VkGl uploads keep their call order. Staging
/// comes from the frame's <see cref="GpuFrame.Staging"/>. Render thread only, while a frame is open.
/// </summary>
public sealed unsafe class Uploader : IGpuUploads
{
    /// <summary>
    /// The context whose frame, staging and device the uploads go through.
    /// </summary>
    readonly GpuContext _ctx;

    /// <summary>
    /// The uploads of the context.
    /// </summary>
    internal Uploader(GpuContext ctx) => this._ctx = ctx;

    /// <summary>
    /// The frame's upload command buffer; throws when no frame is open.
    /// </summary>
    CommandBuffer Cmd => _ctx.Frame.Open ? _ctx.Frame.UploadCommands : throw new InvalidOperationException("uploads need an open frame");

    /// <summary>
    /// Copies <paramref name="data"/> into the buffer at <paramref name="offset"/> through the frame's staging; throws when the frame already drew
    /// from the buffer or the write passes its end.
    /// </summary>
    public void Write(DeviceBuffer target, ulong offset, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        if (target.UsedFrame == _ctx.Frame.Number)
        {
            throw new InvalidOperationException(
                "the frame already drew from this buffer: per-frame data belongs in a Transient (docs/renderer-native.md 2.3)");
        }

        if (offset + (ulong)data.Length > target.Size) throw new ArgumentOutOfRangeException(nameof(data), "write past the end of the buffer");

        var staging = _ctx.Frame.Staging.Write(data);
        var copy = new BufferCopy(staging.Offset, offset, (ulong)data.Length);
        var cmd = Cmd;
        _ctx.Device.Vk.CmdCopyBuffer(cmd, _ctx.Device.BufferAt(staging.Handle.Index), _ctx.Device.BufferAt(target.Handle.Index), 1, &copy);
        TransferBarrier(cmd);
        _ctx.Frame.Stats.UploadBytes += data.Length;
        if (SpikeLog.Enabled) SpikeLog.Note("upload " + target.Name, data.Length);
    }

    /// <summary>
    /// Writes <paramref name="data"/>'s <paramref name="stride"/>-byte records each to its own offset of <paramref name="offsets"/> (in order): one
    /// staging copy and one copy command.
    /// </summary>
    public void WriteScattered(DeviceBuffer target, ReadOnlySpan<byte> data, int stride, ReadOnlySpan<ulong> offsets)
    {
        if (offsets.IsEmpty) return;

        if (target.UsedFrame == _ctx.Frame.Number)
        {
            throw new InvalidOperationException(
                "the frame already drew from this buffer: per-frame data belongs in a Transient (docs/renderer-native.md 2.3)");
        }

        if (data.Length != stride * offsets.Length) throw new ArgumentException("one record per offset", nameof(data));

        var staging = _ctx.Frame.Staging.Write(data);
        var copies = new BufferCopy[offsets.Length];
        for (int i = 0; i < offsets.Length; i++)
        {
            if (offsets[i] + (ulong)stride > target.Size) throw new ArgumentOutOfRangeException(nameof(offsets), "write past the end of the buffer");

            copies[i] = new BufferCopy(staging.Offset + (ulong)(i * stride), offsets[i], (ulong)stride);
        }

        var cmd = Cmd;
        fixed (BufferCopy* p = copies)
        {
            _ctx.Device.Vk.CmdCopyBuffer(cmd, _ctx.Device.BufferAt(staging.Handle.Index), _ctx.Device.BufferAt(target.Handle.Index),
                (uint)copies.Length, p);
        }

        TransferBarrier(cmd);
        _ctx.Frame.Stats.UploadBytes += data.Length;
        if (SpikeLog.Enabled) SpikeLog.Note("upload " + target.Name, data.Length);
    }

    /// <summary>
    /// Writes texels of one level and layer (tightly packed, in the texture's format; for block-compressed formats whole blocks).
    /// </summary>
    public void Write(GpuTexture target, int level, int layer, in PixelRect region, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        var staging = _ctx.Frame.Staging.Write(data);
        var copy = new BufferImageCopy
        {
            BufferOffset = staging.Offset,
            ImageSubresource = new ImageSubresourceLayers(GlConventions.VkAspect(target.Desc), (uint)level, (uint)layer, 1),
            ImageOffset = new Offset3D(region.X, region.Y, 0),
            ImageExtent = new Extent3D(region.Width, region.Height, 1),
        };
        var cmd = Cmd;
        _ctx.Device.Vk.CmdCopyBufferToImage(cmd, _ctx.Device.BufferAt(staging.Handle.Index), _ctx.Device.NativeImage(target), ImageLayout.General, 1,
            &copy);
        TransferBarrier(cmd);
        _ctx.Frame.Stats.UploadBytes += data.Length;
        if (SpikeLog.Enabled) SpikeLog.Note("upload " + target.Desc.Name + (level > 0 ? " mip" : ""), data.Length);
    }

    /// <summary>
    /// (Added in phase 8 stage 1, terrain.) Uploads and the commands that belong with them (a new texture's layout transition, its mip generation)
    /// for resources made outside the frame's passes, whether or not a frame is open: while one is, into the frame's upload command buffer
    /// (<see cref="GpuFrame.PreFrame"/>, as <see cref="Write(Texture, int, int, in PixelRect, ReadOnlySpan{byte})"/>); otherwise (at load, before the
    /// first frame: VkGl opened a frame for such uploads, the native API does not) into a one-shot command buffer with staging of its own, which
    /// <see cref="UploadBatch.Dispose"/> submits and waits for. Render thread only.
    /// </summary>
    public IGpuUploadBatch Begin() => new UploadBatch(_ctx);

    /// <summary>
    /// Orders successive uploads (two writes to overlapping ranges must not race).
    /// </summary>
    internal static void TransferBarrier(Vk vk, CommandBuffer cmd)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllTransferBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.AllTransferBit,
            DstAccessMask = AccessFlags2.TransferReadBit | AccessFlags2.TransferWriteBit,
        };
        var info = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
        vk.CmdPipelineBarrier2(cmd, &info);
    }

    /// <summary>
    /// <see cref="TransferBarrier(Vk, CommandBuffer)"/> on the context's device.
    /// </summary>
    void TransferBarrier(CommandBuffer cmd) => TransferBarrier(_ctx.Device.Vk, cmd);
}

/// <summary>
/// A batch of uploads (<see cref="Uploader.Begin"/>): into the open frame's upload command buffer, or, without an open frame, into a one-shot command
/// buffer that <see cref="Dispose"/> submits and waits for. <see cref="Commands"/> takes what goes with the uploads (layout transitions,
/// <see cref="CommandList.GenerateMips"/>); they run after the uploads recorded before them.
/// </summary>
public sealed unsafe class UploadBatch : IGpuUploadBatch
{
    /// <summary>
    /// The context the batch's uploads, textures and staging come from.
    /// </summary>
    readonly GpuContext _ctx;
    /// <summary>
    /// Whether the batch has its own one-shot command buffer (no frame was open when it began).
    /// </summary>
    readonly bool _immediate;
    /// <summary>
    /// The staging buffers of a one-shot batch, freed after its submission.
    /// </summary>
    readonly List<Core.BufferAllocation> _staging = [];

    /// <summary>
    /// A batch into the open frame's upload buffer, or, without an open frame, into a new one-shot command buffer.
    /// </summary>
    internal UploadBatch(GpuContext ctx)
    {
        this._ctx = ctx;
        _immediate = !ctx.Frame.Open;
        Commands = _immediate ? new CommandList(ctx, new GpuStats()) { Handle = ctx.Device.BeginImmediate() } : ctx.Frame.PreFrame;
    }

    /// <summary>
    /// Where the batch records (the frame's <see cref="GpuFrame.PreFrame"/>, or the one-shot buffer).
    /// </summary>
    public CommandList Commands { get; }

    /// <inheritdoc/>
    IGpuCommandList IGpuUploadBatch.Commands => Commands;

    /// <summary>
    /// A new texture in GENERAL layout, its transition recorded into this batch.
    /// </summary>
    public GpuTexture Create(TextureDesc desc) => _ctx.CreateTexture(desc, Commands.Handle);

    /// <summary>
    /// Writes texels of one level and layer (tightly packed, in the texture's format; for block-compressed formats whole blocks).
    /// </summary>
    public void Write(GpuTexture target, int level, int layer, in PixelRect region, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        if (!_immediate)
        {
            _ctx.Uploads.Write(target, level, layer, region, data);
            return;
        }

        var (buffer, offset) = Stage(data);
        var copy = new BufferImageCopy
        {
            BufferOffset = offset,
            ImageSubresource = new ImageSubresourceLayers(GlConventions.VkAspect(target.Desc), (uint)level, (uint)layer, 1),
            ImageOffset = new Offset3D(region.X, region.Y, 0),
            ImageExtent = new Extent3D(region.Width, region.Height, 1),
        };
        _ctx.Device.Vk.CmdCopyBufferToImage(Commands.Handle, buffer, _ctx.Device.NativeImage(target), ImageLayout.General, 1, &copy);
        Uploader.TransferBarrier(_ctx.Device.Vk, Commands.Handle);
    }

    /// <summary>
    /// Writes a device buffer's bytes from <paramref name="offset"/>.
    /// </summary>
    public void Write(DeviceBuffer target, ulong offset, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        if (!_immediate)
        {
            _ctx.Uploads.Write(target, offset, data);
            return;
        }

        if (offset + (ulong)data.Length > target.Size) throw new ArgumentOutOfRangeException(nameof(data), "write past the end of the buffer");

        var (buffer, from) = Stage(data);
        var copy = new BufferCopy(from, offset, (ulong)data.Length);
        _ctx.Device.Vk.CmdCopyBuffer(Commands.Handle, buffer, _ctx.Device.BufferAt(target.Handle.Index), 1, &copy);
        Uploader.TransferBarrier(_ctx.Device.Vk, Commands.Handle);
    }

    /// <summary>
    /// A new staging buffer with <paramref name="data"/> in it (kept until the batch is disposed), and the data's offset in it.
    /// </summary>
    (Silk.NET.Vulkan.Buffer Buffer, ulong Offset) Stage(ReadOnlySpan<byte> data)
    {
        var b = _ctx.Device.Allocator.CreateBuffer((ulong)data.Length, BufferUsageFlags.TransferSrcBit, Core.MemoryKind.Upload, "upload staging");
        _staging.Add(b);
        data.CopyTo(new Span<byte>((void*)b.Mapped, data.Length));
        return (b.Buffer, 0);
    }

    /// <summary>
    /// Without an open frame: submits the batch and waits for it. With one: nothing (the frame submits its upload buffer).
    /// </summary>
    public void Dispose()
    {
        if (!_immediate) return;

        _ctx.Device.EndImmediate(Commands.Handle);
        foreach (var b in _staging) _ctx.Device.Allocator.Free(b);

        _staging.Clear();
    }
}
