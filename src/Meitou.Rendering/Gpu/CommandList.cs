using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu.Core;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Meitou.Rendering.Gpu;

/// <summary>An attachment of a <see cref="RenderingDesc"/>: a view in GENERAL layout, its load op and clear value (stores always).</summary>
public readonly record struct RenderTarget(ImageView View, AttachmentLoadOp Load = AttachmentLoadOp.Load, ClearValue Clear = default, Image Image = default)
{
    public bool IsNull => View.Handle == 0;
}

/// <summary>What <see cref="CommandList.BeginRendering"/> renders into: up to one colour and one depth attachment over an area, and optionally a fragment shading rate attachment
/// (<c>ShadingRate</c>, an R8_UINT view in GENERAL layout whose texels are <c>ShadingRateTexel</c> pixels on a side; the pipelines must have been made for it, <see cref="AttachmentFormats.ShadingRate"/>).</summary>
public readonly record struct RenderingDesc(RenderTarget Colour, RenderTarget Depth, int Width, int Height, int X = 0, int Y = 0, ImageView ShadingRate = default, int ShadingRateTexel = 0);

/// <summary>
/// A command buffer being recorded (docs/renderer-native.md 2.7). Thin: every method records what it says. It keeps only a redundancy
/// filter for the pipeline and the vertex and index buffers, cleared at <see cref="BeginRendering"/> and at every seam crossing
/// (<see cref="Invalidate"/>). Not thread-safe: one per recording thread.
/// </summary>
public sealed unsafe class CommandList
{
    readonly Vk vk;
    readonly VulkanDevice device;
    readonly KhrPushDescriptor? push;
    readonly ExtDebugUtils? debug;
    readonly Dictionary<string, nint> labels = [];
    Pipeline lastPipeline;
    PipelineBindPoint lastPoint;
    BufferBinding lastIndex;
    IndexType lastIndexType;
    readonly Buffer[] lastVertex = new Buffer[32];
    readonly ulong[] lastVertexOffset = new ulong[32];
    uint vertexValid;   // bit per binding whose last* entry is known

    // The per-draw and per-segment commands, called through the device's own entry points (vkGetDeviceProcAddr: the driver's, or the top
    // layer's when validation is on) instead of Silk.NET's per-call vtable lookup and cast (docs/renderer-native.md 9: ~0.02 us a draw).
    // The same commands with the same arguments; nothing else changes.
    readonly delegate* unmanaged<CommandBuffer, PipelineBindPoint, Pipeline, void> cmdBindPipeline;
    readonly delegate* unmanaged<CommandBuffer, uint, uint, Viewport*, void> cmdSetViewport;
    readonly delegate* unmanaged<CommandBuffer, uint, uint, Rect2D*, void> cmdSetScissor;
    readonly delegate* unmanaged<CommandBuffer, CullModeFlags, void> cmdSetCullMode;
    readonly delegate* unmanaged<CommandBuffer, FrontFace, void> cmdSetFrontFace;
    readonly delegate* unmanaged<CommandBuffer, Bool32, void> cmdSetDepthTestEnable, cmdSetDepthWriteEnable, cmdSetDepthBiasEnable;
    readonly delegate* unmanaged<CommandBuffer, CompareOp, void> cmdSetDepthCompareOp;
    readonly delegate* unmanaged<CommandBuffer, float, float, float, void> cmdSetDepthBias;
    readonly delegate* unmanaged<CommandBuffer, uint, uint, Buffer*, ulong*, void> cmdBindVertexBuffers;
    readonly delegate* unmanaged<CommandBuffer, Buffer, ulong, IndexType, void> cmdBindIndexBuffer;
    readonly delegate* unmanaged<CommandBuffer, PipelineBindPoint, PipelineLayout, uint, uint, DescriptorSet*, uint, uint*, void> cmdBindDescriptorSets;
    readonly delegate* unmanaged<CommandBuffer, PipelineLayout, ShaderStageFlags, uint, uint, void*, void> cmdPushConstants;
    readonly delegate* unmanaged<CommandBuffer, uint, uint, uint, uint, void> cmdDraw;
    readonly delegate* unmanaged<CommandBuffer, uint, uint, uint, int, uint, void> cmdDrawIndexed;
    readonly delegate* unmanaged<CommandBuffer, Buffer, ulong, uint, uint, void> cmdDrawIndexedIndirect;

    internal CommandList(VulkanDevice device, GpuStats stats)
    {
        this.device = device;
        vk = device.Vk;
        Stats = stats;
        if (device.HasPushDescriptor) vk.TryGetDeviceExtension(device.Instance, device.Device, out push);
        debug = device.DebugUtils;
        cmdBindPipeline = (delegate* unmanaged<CommandBuffer, PipelineBindPoint, Pipeline, void>)Proc("vkCmdBindPipeline");
        cmdSetViewport = (delegate* unmanaged<CommandBuffer, uint, uint, Viewport*, void>)Proc("vkCmdSetViewport");
        cmdSetScissor = (delegate* unmanaged<CommandBuffer, uint, uint, Rect2D*, void>)Proc("vkCmdSetScissor");
        cmdSetCullMode = (delegate* unmanaged<CommandBuffer, CullModeFlags, void>)Proc("vkCmdSetCullMode");
        cmdSetFrontFace = (delegate* unmanaged<CommandBuffer, FrontFace, void>)Proc("vkCmdSetFrontFace");
        cmdSetDepthTestEnable = (delegate* unmanaged<CommandBuffer, Bool32, void>)Proc("vkCmdSetDepthTestEnable");
        cmdSetDepthWriteEnable = (delegate* unmanaged<CommandBuffer, Bool32, void>)Proc("vkCmdSetDepthWriteEnable");
        cmdSetDepthBiasEnable = (delegate* unmanaged<CommandBuffer, Bool32, void>)Proc("vkCmdSetDepthBiasEnable");
        cmdSetDepthCompareOp = (delegate* unmanaged<CommandBuffer, CompareOp, void>)Proc("vkCmdSetDepthCompareOp");
        cmdSetDepthBias = (delegate* unmanaged<CommandBuffer, float, float, float, void>)Proc("vkCmdSetDepthBias");
        cmdBindVertexBuffers = (delegate* unmanaged<CommandBuffer, uint, uint, Buffer*, ulong*, void>)Proc("vkCmdBindVertexBuffers");
        cmdBindIndexBuffer = (delegate* unmanaged<CommandBuffer, Buffer, ulong, IndexType, void>)Proc("vkCmdBindIndexBuffer");
        cmdBindDescriptorSets = (delegate* unmanaged<CommandBuffer, PipelineBindPoint, PipelineLayout, uint, uint, DescriptorSet*, uint, uint*, void>)Proc("vkCmdBindDescriptorSets");
        cmdPushConstants = (delegate* unmanaged<CommandBuffer, PipelineLayout, ShaderStageFlags, uint, uint, void*, void>)Proc("vkCmdPushConstants");
        cmdDraw = (delegate* unmanaged<CommandBuffer, uint, uint, uint, uint, void>)Proc("vkCmdDraw");
        cmdDrawIndexed = (delegate* unmanaged<CommandBuffer, uint, uint, uint, int, uint, void>)Proc("vkCmdDrawIndexed");
        cmdDrawIndexedIndirect = (delegate* unmanaged<CommandBuffer, Buffer, ulong, uint, uint, void>)Proc("vkCmdDrawIndexedIndirect");
    }

    /// <summary>A device-level command entry point (all of them core in Vulkan 1.3, which the device requires).</summary>
    void* Proc(string name)
    {
        var p = (void*)vk.GetDeviceProcAddr(device.Device, name).Handle;
        return p != null ? p : throw new InvalidOperationException($"{name} not found on the device");
    }

    public CommandBuffer Handle { get; internal set; }
    public GpuStats Stats { get; }
    /// <summary>The draw log, when a tool turned it on (docs/renderer-native.md 7.6).</summary>
    public DrawLog? Log { get; internal set; }
    /// <summary>Counts seam crossings and new command buffers: state a caller cached (pushed descriptors, bound sets) is valid only within one epoch.</summary>
    public long Epoch { get; private set; }
    /// <summary>The legacy program whose set 0 / set 1 are bound in this epoch (<see cref="LegacyProgram.Flush"/>).</summary>
    internal object? BoundProgram;

    /// <summary>Forgets everything the redundancy filters and callers believed was bound (after VkGl recorded into the same buffer).</summary>
    public void Invalidate()
    {
        lastPipeline = default;
        lastIndex = default;
        vertexValid = 0;
        BoundProgram = null;
        Epoch++;
    }

    // ---- rendering ----

    /// <summary>Begins dynamic rendering on <paramref name="d"/>. <paramref name="secondaries"/>: its contents are recorded in secondary command
    /// buffers (<see cref="ExecuteCommands"/>, docs/renderer-native.md 6.1); nothing else may then be recorded into this list until <see cref="EndRendering"/>.</summary>
    public void BeginRendering(in RenderingDesc d, bool secondaries = false)
    {
        var colour = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo, ImageView = d.Colour.View, ImageLayout = ImageLayout.General,
            LoadOp = d.Colour.Load, StoreOp = AttachmentStoreOp.Store, ClearValue = d.Colour.Clear,
        };
        var depth = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo, ImageView = d.Depth.View, ImageLayout = ImageLayout.General,
            LoadOp = d.Depth.Load, StoreOp = AttachmentStoreOp.Store, ClearValue = d.Depth.Clear,
        };
        var rate = new RenderingFragmentShadingRateAttachmentInfoKHR
        {
            SType = StructureType.RenderingFragmentShadingRateAttachmentInfoKhr, ImageView = d.ShadingRate, ImageLayout = ImageLayout.General,
            ShadingRateAttachmentTexelSize = new Extent2D((uint)d.ShadingRateTexel, (uint)d.ShadingRateTexel),
        };
        var info = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            PNext = d.ShadingRate.Handle != 0 ? &rate : null,
            Flags = secondaries ? RenderingFlags.ContentsSecondaryCommandBuffersBit : 0,
            RenderArea = new Rect2D(new Offset2D(d.X, d.Y), new Extent2D((uint)d.Width, (uint)d.Height)),
            LayerCount = 1,
            ColorAttachmentCount = d.Colour.IsNull ? 0u : 1u, PColorAttachments = d.Colour.IsNull ? null : &colour,
            PDepthAttachment = d.Depth.IsNull ? null : &depth,
        };
        var stats = PipelineStatsMeter.Current;   // the bench's counting frames only
        stats?.Rendering(this, before: true);
        vk.CmdBeginRendering(Handle, &info);
        stats?.Rendering(this, before: false);
        lastPipeline = default;
        lastIndex = default;
        vertexValid = 0;
        Log?.BeginRendering(in d);
    }

    public void EndRendering()
    {
        var stats = PipelineStatsMeter.Current;
        stats?.Rendering(this, before: true);
        vk.CmdEndRendering(Handle);
        stats?.Rendering(this, before: false);
    }

    /// <summary>Executes secondary command buffers in order (inside a rendering begun with secondaries). Afterwards nothing is assumed bound.</summary>
    public void ExecuteCommands(ReadOnlySpan<CommandBuffer> buffers)
    {
        if (buffers.IsEmpty) return;
        fixed (CommandBuffer* p = buffers) vk.CmdExecuteCommands(Handle, (uint)buffers.Length, p);
        Invalidate();
    }

    /// <summary>Begins this list's command buffer as a secondary that continues a rendering of <paramref name="formats"/> (the dynamic-rendering inheritance).</summary>
    internal void BeginSecondary(in AttachmentFormats formats)
    {
        var colour = formats.Colour;
        var rendering = new CommandBufferInheritanceRenderingInfo
        {
            SType = StructureType.CommandBufferInheritanceRenderingInfo,
            ColorAttachmentCount = colour == Format.Undefined ? 0u : 1u, PColorAttachmentFormats = &colour,
            DepthAttachmentFormat = formats.Depth, StencilAttachmentFormat = Format.Undefined,
            RasterizationSamples = (SampleCountFlags)Math.Max(formats.Samples, 1),
        };
        var inheritance = new CommandBufferInheritanceInfo { SType = StructureType.CommandBufferInheritanceInfo, PNext = &rendering };
        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.RenderPassContinueBit | CommandBufferUsageFlags.OneTimeSubmitBit,
            PInheritanceInfo = &inheritance,
        };
        VulkanException.Check(vk.BeginCommandBuffer(Handle, &begin), "vkBeginCommandBuffer");
        Invalidate();
    }

    internal void EndSecondary() => VulkanException.Check(vk.EndCommandBuffer(Handle), "vkEndCommandBuffer");

    /// <summary>Clears the depth attachment of the open rendering inside <paramref name="rect"/> (<c>vkCmdClearAttachments</c>; a shadow atlas tile, a depth slice).</summary>
    public void ClearDepth(float value, Rect2D rect) => Clear(false, default, true, value, rect);

    /// <summary>Clears the colour and / or depth attachment of the open rendering inside <paramref name="rect"/> (<c>vkCmdClearAttachments</c>,
    /// the colour first, as <c>VkGl.Clear</c> records it).</summary>
    public void Clear(bool colour, ClearColorValue colourValue, bool depth, float depthValue, Rect2D rect)
    {
        var attachments = stackalloc ClearAttachment[2];
        uint n = 0;
        if (colour) attachments[n++] = new ClearAttachment(ImageAspectFlags.ColorBit, 0, new ClearValue(colourValue));
        if (depth) attachments[n++] = new ClearAttachment(ImageAspectFlags.DepthBit, 0, new ClearValue(depthStencil: new ClearDepthStencilValue(depthValue, 0)));
        if (n == 0) return;
        var area = new ClearRect(rect, 0, 1);
        vk.CmdClearAttachments(Handle, n, attachments, 1, &area);
    }

    /// <summary>The bench's triangle size histogram (<c>--bench-tris</c>, <see cref="TriangleCounter"/>): while set, every pipeline bound is swapped for its counting variant. Null (a null check per bind) otherwise.</summary>
    internal static Func<GraphicsPipeline, GraphicsPipeline>? PipelineSubstitute;

    public void BindPipeline(GraphicsPipeline pipeline)
    {
        if (PipelineSubstitute is { } substitute) pipeline = substitute(pipeline);
        Log?.Pipeline(pipeline);
        if (pipeline.Handle.Handle == lastPipeline.Handle && lastPoint == PipelineBindPoint.Graphics) return;
        cmdBindPipeline(Handle, PipelineBindPoint.Graphics, pipeline.Handle);
        (lastPipeline, lastPoint) = (pipeline.Handle, PipelineBindPoint.Graphics);
        Stats.PipelinesBound++;
    }

    public void SetViewport(in Viewport v)
    {
        fixed (Viewport* p = &v) cmdSetViewport(Handle, 0, 1, p);
        Log?.Viewport(in v);
    }

    public void SetScissor(in Rect2D r)
    {
        fixed (Rect2D* p = &r) cmdSetScissor(Handle, 0, 1, p);
        Log?.Scissor(in r);
    }

    public void SetRaster(CullModeFlags cull, FrontFace front)
    {
        cmdSetCullMode(Handle, cull);
        cmdSetFrontFace(Handle, front);
        Log?.Raster(cull, front);
    }

    /// <summary>The front face alone (a draw that turns the winding round, the cull mode left as set).</summary>
    public void SetFrontFace(FrontFace front)
    {
        cmdSetFrontFace(Handle, front);
        Log?.Front(front);
    }

    public void SetDepth(bool test, bool write, CompareOp op)
    {
        cmdSetDepthTestEnable(Handle, test);
        cmdSetDepthWriteEnable(Handle, write);
        cmdSetDepthCompareOp(Handle, op);
        Log?.Depth(test, write, op);
    }

    public void SetDepthBias(bool enable, float constant, float slope)
    {
        cmdSetDepthBiasEnable(Handle, enable);
        cmdSetDepthBias(Handle, constant, 0, slope);
        Log?.Bias(enable, constant, slope);
    }

    public void BindVertexBuffers(uint first, ReadOnlySpan<BufferBinding> bindings)
    {
        Log?.VertexBuffers(first, bindings);
        int n = bindings.Length;
        var buffers = stackalloc Buffer[n];
        var offsets = stackalloc ulong[n];
        if (first + (uint)n <= 32)
        {
            // One pass: compare with what is bound and gather the arguments; record only when a binding differs or is not known.
            uint mask = (n == 32 ? uint.MaxValue : (1u << n) - 1) << (int)first;
            bool same = (vertexValid & mask) == mask;
            for (int i = 0; i < n; i++)
            {
                ref readonly var bb = ref bindings[i];
                buffers[i] = bb.Buffer;
                offsets[i] = bb.Offset;
                uint b = first + (uint)i;
                if (lastVertex[b].Handle != bb.Buffer.Handle || lastVertexOffset[b] != bb.Offset)
                {
                    same = false;
                    lastVertex[b] = bb.Buffer;
                    lastVertexOffset[b] = bb.Offset;
                }
            }
            if (same) return;
            vertexValid |= mask;
        }
        else
            for (int i = 0; i < n; i++)
            {
                buffers[i] = bindings[i].Buffer;
                offsets[i] = bindings[i].Offset;
                uint b = first + (uint)i;
                if (b < 32) { lastVertex[b] = buffers[i]; lastVertexOffset[b] = offsets[i]; vertexValid |= 1u << (int)b; }
            }
        cmdBindVertexBuffers(Handle, first, (uint)n, buffers, offsets);
    }

    public void BindIndexBuffer(BufferBinding binding, IndexType type)
    {
        Log?.IndexBuffer(binding, type);
        if (binding.Buffer.Handle == lastIndex.Buffer.Handle && binding.Offset == lastIndex.Offset && type == lastIndexType && lastIndex.Buffer.Handle != 0) return;
        cmdBindIndexBuffer(Handle, binding.Buffer, binding.Offset, type);
        (lastIndex, lastIndexType) = (binding, type);
    }

    public void BindSets(PipelineLayout layout, uint first, ReadOnlySpan<DescriptorSet> sets, ReadOnlySpan<uint> dynamicOffsets, PipelineBindPoint point = PipelineBindPoint.Graphics)
    {
        fixed (DescriptorSet* ps = sets)
        fixed (uint* po = dynamicOffsets)
            cmdBindDescriptorSets(Handle, point, layout, first, (uint)sets.Length, ps, (uint)dynamicOffsets.Length, po);
        Log?.Sets(first, sets, dynamicOffsets);
    }

    /// <summary>Pushes descriptors of set <paramref name="set"/> (legacy layout and compute; VK_KHR_push_descriptor).</summary>
    public void PushDescriptors(PipelineLayout layout, uint set, ReadOnlySpan<WriteDescriptorSet> writes, PipelineBindPoint point = PipelineBindPoint.Graphics)
    {
        if (push is null) throw new InvalidOperationException("VK_KHR_push_descriptor is not available");
        fixed (WriteDescriptorSet* pw = writes) push.CmdPushDescriptorSet(Handle, point, layout, set, (uint)writes.Length, pw);
        Stats.DescriptorPushes++;
        Log?.Push(set, writes);
    }

    public void PushConstants<T>(PipelineLayout layout, ShaderStageFlags stages, in T value, uint offset = 0) where T : unmanaged
    {
        fixed (T* p = &value) cmdPushConstants(Handle, layout, stages, offset, (uint)sizeof(T), p);
        Log?.PushConstants(offset, new ReadOnlySpan<byte>(Unsafe.AsPointer(ref Unsafe.AsRef(in value)), sizeof(T)));
    }

    public void Draw(uint vertices, uint instances = 1, uint firstVertex = 0, uint firstInstance = 0)
    {
        Log?.Draw(false, vertices, instances, firstVertex, 0, firstInstance);
        cmdDraw(Handle, vertices, instances, firstVertex, firstInstance);
        Stats.Draws++;
    }

    public void DrawIndexed(uint indices, uint instances = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
    {
        Log?.Draw(true, indices, instances, firstIndex, vertexOffset, firstInstance);
        cmdDrawIndexed(Handle, indices, instances, firstIndex, vertexOffset, firstInstance);
        Stats.Draws++;
    }

    public void DrawIndexedIndirect(Buffer args, ulong offset, uint count, uint stride = 20)
    {
        Log?.Indirect(args, offset, count, stride);
        cmdDrawIndexedIndirect(Handle, args, offset, count, stride);
        Stats.Draws++;
        Stats.IndirectDraws++;
    }

    public void DrawIndexedIndirectCount(Buffer args, ulong offset, Buffer count, ulong countOffset, uint maxCount, uint stride = 20)
    {
        Log?.Indirect(args, offset, maxCount, stride);
        vk.CmdDrawIndexedIndirectCount(Handle, args, offset, count, countOffset, maxCount, stride);
        Stats.Draws++;
        Stats.IndirectDraws++;
    }

    /// <summary>Non-indexed draws from <paramref name="args"/> (<c>VkDrawIndirectCommand</c>, 16 bytes each): the first
    /// <paramref name="count"/> (a 32-bit value in <paramref name="countBuffer"/> at <paramref name="countOffset"/>, at most <paramref name="maxCount"/>),
    /// one call. Needs <c>drawIndirectCount</c> (Vulkan 1.2) and <c>drawIndirectFirstInstance</c> for a non-zero firstInstance.</summary>
    public void DrawIndirectCount(Buffer args, ulong offset, Buffer countBuffer, ulong countOffset, uint maxCount, uint stride = 16)
    {
        Log?.Indirect(args, offset, maxCount, stride);
        vk.CmdDrawIndirectCount(Handle, args, offset, countBuffer, countOffset, maxCount, stride);
        Stats.Draws++;
        Stats.IndirectDraws++;
    }

    // ---- compute and transfer ----

    public void BindPipeline(ComputePipeline pipeline)
    {
        if (pipeline.Handle.Handle == lastPipeline.Handle && lastPoint == PipelineBindPoint.Compute) return;
        vk.CmdBindPipeline(Handle, PipelineBindPoint.Compute, pipeline.Handle);
        (lastPipeline, lastPoint) = (pipeline.Handle, PipelineBindPoint.Compute);
        Stats.PipelinesBound++;
    }

    public void Dispatch(uint x, uint y = 1, uint z = 1)
    {
        vk.CmdDispatch(Handle, x, y, z);
        Stats.Dispatches++;
    }

    public void DispatchIndirect(Buffer args, ulong offset)
    {
        vk.CmdDispatchIndirect(Handle, args, offset);
        Stats.Dispatches++;
    }

    public void FillBuffer(Buffer b, ulong offset, ulong size, uint value) => vk.CmdFillBuffer(Handle, b, offset, size, value);

    public void CopyBuffer(Buffer src, Buffer dst, in BufferCopy region)
    {
        fixed (BufferCopy* p = &region) vk.CmdCopyBuffer(Handle, src, dst, 1, p);
    }

    /// <summary>(Added for the Meitou water's ocean.) Copies tightly packed texels from <paramref name="src"/> at <paramref name="offset"/> over a
    /// whole level and layer of <paramref name="dst"/> (GENERAL layout). The caller orders it against the writes before and the reads after.</summary>
    public void CopyBufferToImage(Buffer src, ulong offset, Texture dst, int level, int layer)
    {
        var copy = new BufferImageCopy
        {
            BufferOffset = offset,
            ImageSubresource = new ImageSubresourceLayers(dst.Desc.Aspect, (uint)level, (uint)layer, 1),
            ImageExtent = new Extent3D((uint)Math.Max(dst.Desc.Width >> level, 1), (uint)Math.Max(dst.Desc.Height >> level, 1), 1),
        };
        vk.CmdCopyBufferToImage(Handle, src, dst.Image, ImageLayout.General, 1, &copy);
    }

    /// <summary>Resolves level 0 / layer 0 of a multisampled colour texture into a single-sampled one (the reflection's 4× target).</summary>
    public void Resolve(Texture src, Texture dst)
    {
        var region = new ImageResolve
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D((uint)src.Desc.Width, (uint)src.Desc.Height, 1),
        };
        vk.CmdResolveImage(Handle, src.Image, ImageLayout.General, dst.Image, ImageLayout.General, 1, &region);
    }

    /// <summary>As <see cref="Resolve(Texture, Texture)"/> for two images in GENERAL layout (a render target taken from <see cref="PassTargets"/>), <paramref name="width"/> × <paramref name="height"/> from the origin.</summary>
    public void Resolve(Image src, Image dst, int width, int height)
    {
        var region = new ImageResolve
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D((uint)width, (uint)height, 1),
        };
        vk.CmdResolveImage(Handle, src, ImageLayout.General, dst, ImageLayout.General, 1, &region);
    }

    /// <summary>Scales level 0 / layer 0 of <paramref name="src"/> over level 0 of <paramref name="dst"/>.</summary>
    public void Blit(Texture src, Texture dst, Filter filter)
    {
        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(src.Desc.Aspect, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(dst.Desc.Aspect, 0, 0, 1),
        };
        blit.SrcOffsets[1] = new Offset3D(src.Desc.Width, src.Desc.Height, 1);
        blit.DstOffsets[1] = new Offset3D(dst.Desc.Width, dst.Desc.Height, 1);
        vk.CmdBlitImage(Handle, src.Image, ImageLayout.General, dst.Image, ImageLayout.General, 1, &blit, filter);
    }

    /// <summary>
    /// (Added in phase 8 stage 1, terrain.) Fills levels 1 and up of a colour texture from level 0, all layers, exactly as VkGl's
    /// <c>GenerateMipmap</c>: a full barrier, then per level a transfer barrier and a linear blit from the level above (GENERAL layout), then a
    /// full barrier. Outside any rendering.
    /// </summary>
    public void GenerateMips(Texture t)
    {
        var d = t.Desc;
        if (d.Levels < 2) return;
        Barrier(BarrierBatch.Full);
        var transfer = new BarrierBatch
        {
            SrcStages = PipelineStageFlags2.TransferBit, SrcAccess = AccessFlags2.TransferWriteBit,
            DstStages = PipelineStageFlags2.TransferBit, DstAccess = AccessFlags2.TransferReadBit,
        };
        uint layers = (uint)(d.Kind == TextureKind.Cube ? 6 * Math.Max(d.Layers, 1) : d.Layers);
        for (int level = 1; level < d.Levels; level++)
        {
            Barrier(transfer);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)level - 1, 0, layers),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)level, 0, layers),
            };
            blit.SrcOffsets[1] = new Offset3D(Math.Max(d.Width >> (level - 1), 1), Math.Max(d.Height >> (level - 1), 1), 1);
            blit.DstOffsets[1] = new Offset3D(Math.Max(d.Width >> level, 1), Math.Max(d.Height >> level, 1), 1);
            vk.CmdBlitImage(Handle, t.Image, ImageLayout.General, t.Image, ImageLayout.General, 1, &blit, Filter.Linear);
        }
        Barrier(BarrierBatch.Full);
    }

    /// <summary>(Phase 8 stage 1.) Scales level <paramref name="srcLevel"/> of <paramref name="texture"/> over its level <paramref name="dstLevel"/>
    /// (all layers), as VkGl's <c>GenerateMipmap</c> does per level.</summary>
    public void BlitLevel(Texture texture, int srcLevel, int dstLevel, Filter filter)
    {
        var d = texture.Desc;
        uint layers = (uint)(d.Kind == TextureKind.Cube ? 6 * Math.Max(d.Layers, 1) : d.Layers);
        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(d.Aspect, (uint)srcLevel, 0, layers),
            DstSubresource = new ImageSubresourceLayers(d.Aspect, (uint)dstLevel, 0, layers),
        };
        blit.SrcOffsets[1] = new Offset3D(Math.Max(d.Width >> srcLevel, 1), Math.Max(d.Height >> srcLevel, 1), 1);
        blit.DstOffsets[1] = new Offset3D(Math.Max(d.Width >> dstLevel, 1), Math.Max(d.Height >> dstLevel, 1), 1);
        vk.CmdBlitImage(Handle, texture.Image, ImageLayout.General, texture.Image, ImageLayout.General, 1, &blit, filter);
    }

    // ---- sync, timing, labels ----

    public void Barrier(in BarrierBatch batch)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = batch.SrcStages, SrcAccessMask = batch.SrcAccess,
            DstStageMask = batch.DstStages, DstAccessMask = batch.DstAccess,
        };
        var info = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
        vk.CmdPipelineBarrier2(Handle, &info);
    }

    /// <summary>Writes a timestamp into the frame's query arena slot.</summary>
    public void Timestamp(QueryArena arena, in QuerySlot slot, PipelineStageFlags2 stage = PipelineStageFlags2.AllCommandsBit)
    {
        if (!slot.IsValid) return;
        vk.CmdWriteTimestamp2(Handle, stage, arena.Pool(in slot), slot.Index);
    }

    /// <summary>A debug label (VK_EXT_debug_utils; no-op without it).</summary>
    public void BeginLabel(string name)
    {
        Log?.Label(name);
        if (debug is null) return;
        if (!labels.TryGetValue(name, out var p)) labels[name] = p = Marshal.StringToCoTaskMemUTF8(name);
        var info = new DebugUtilsLabelEXT { SType = StructureType.DebugUtilsLabelExt, PLabelName = (byte*)p };
        debug.CmdBeginDebugUtilsLabel(Handle, &info);
    }

    public void EndLabel()
    {
        Log?.Label(null);
        debug?.CmdEndDebugUtilsLabel(Handle);
    }
}
