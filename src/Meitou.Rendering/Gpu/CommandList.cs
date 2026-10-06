using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Meitou.Rendering.Vulkan.Core;
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

/// <summary>What <see cref="CommandList.BeginRendering"/> renders into: up to one colour and one depth attachment over an area.</summary>
public readonly record struct RenderingDesc(RenderTarget Colour, RenderTarget Depth, int Width, int Height, int X = 0, int Y = 0);

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

    internal CommandList(VulkanDevice device, GpuStats stats)
    {
        this.device = device;
        vk = device.Vk;
        Stats = stats;
        if (device.HasPushDescriptor) vk.TryGetDeviceExtension(device.Instance, device.Device, out push);
        debug = device.DebugUtils;
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

    public void BeginRendering(in RenderingDesc d)
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
        var info = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(d.X, d.Y), new Extent2D((uint)d.Width, (uint)d.Height)),
            LayerCount = 1,
            ColorAttachmentCount = d.Colour.IsNull ? 0u : 1u, PColorAttachments = d.Colour.IsNull ? null : &colour,
            PDepthAttachment = d.Depth.IsNull ? null : &depth,
        };
        vk.CmdBeginRendering(Handle, &info);
        lastPipeline = default;
        lastIndex = default;
        vertexValid = 0;
        Log?.BeginRendering(in d);
    }

    public void EndRendering() => vk.CmdEndRendering(Handle);

    public void BindPipeline(GraphicsPipeline pipeline)
    {
        Log?.Pipeline(pipeline);
        if (pipeline.Handle.Handle == lastPipeline.Handle && lastPoint == PipelineBindPoint.Graphics) return;
        vk.CmdBindPipeline(Handle, PipelineBindPoint.Graphics, pipeline.Handle);
        (lastPipeline, lastPoint) = (pipeline.Handle, PipelineBindPoint.Graphics);
        Stats.PipelinesBound++;
    }

    public void SetViewport(in Viewport v)
    {
        fixed (Viewport* p = &v) vk.CmdSetViewport(Handle, 0, 1, p);
        Log?.Viewport(in v);
    }

    public void SetScissor(in Rect2D r)
    {
        fixed (Rect2D* p = &r) vk.CmdSetScissor(Handle, 0, 1, p);
        Log?.Scissor(in r);
    }

    public void SetRaster(CullModeFlags cull, FrontFace front)
    {
        vk.CmdSetCullMode(Handle, cull);
        vk.CmdSetFrontFace(Handle, front);
        Log?.Raster(cull, front);
    }

    /// <summary>The front face alone (a draw that turns the winding round, the cull mode left as set).</summary>
    public void SetFrontFace(FrontFace front)
    {
        vk.CmdSetFrontFace(Handle, front);
        Log?.Front(front);
    }

    public void SetDepth(bool test, bool write, CompareOp op)
    {
        vk.CmdSetDepthTestEnable(Handle, test);
        vk.CmdSetDepthWriteEnable(Handle, write);
        vk.CmdSetDepthCompareOp(Handle, op);
        Log?.Depth(test, write, op);
    }

    public void SetDepthBias(bool enable, float constant, float slope)
    {
        vk.CmdSetDepthBiasEnable(Handle, enable);
        vk.CmdSetDepthBias(Handle, constant, 0, slope);
        Log?.Bias(enable, constant, slope);
    }

    public void BindVertexBuffers(uint first, ReadOnlySpan<BufferBinding> bindings)
    {
        int n = bindings.Length;
        bool same = first + n <= 32;
        for (int i = 0; i < n && same; i++)
        {
            uint b = first + (uint)i;
            same = (vertexValid & (1u << (int)b)) != 0 && lastVertex[b].Handle == bindings[i].Buffer.Handle && lastVertexOffset[b] == bindings[i].Offset;
        }
        Log?.VertexBuffers(first, bindings);
        if (same) return;
        var buffers = stackalloc Buffer[n];
        var offsets = stackalloc ulong[n];
        for (int i = 0; i < n; i++)
        {
            buffers[i] = bindings[i].Buffer;
            offsets[i] = bindings[i].Offset;
            uint b = first + (uint)i;
            if (b < 32) { lastVertex[b] = buffers[i]; lastVertexOffset[b] = offsets[i]; vertexValid |= 1u << (int)b; }
        }
        vk.CmdBindVertexBuffers(Handle, first, (uint)n, buffers, offsets);
    }

    public void BindIndexBuffer(BufferBinding binding, IndexType type)
    {
        Log?.IndexBuffer(binding, type);
        if (binding.Buffer.Handle == lastIndex.Buffer.Handle && binding.Offset == lastIndex.Offset && type == lastIndexType && lastIndex.Buffer.Handle != 0) return;
        vk.CmdBindIndexBuffer(Handle, binding.Buffer, binding.Offset, type);
        (lastIndex, lastIndexType) = (binding, type);
    }

    public void BindSets(PipelineLayout layout, uint first, ReadOnlySpan<DescriptorSet> sets, ReadOnlySpan<uint> dynamicOffsets, PipelineBindPoint point = PipelineBindPoint.Graphics)
    {
        fixed (DescriptorSet* ps = sets)
        fixed (uint* po = dynamicOffsets)
            vk.CmdBindDescriptorSets(Handle, point, layout, first, (uint)sets.Length, ps, (uint)dynamicOffsets.Length, po);
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
        fixed (T* p = &value) vk.CmdPushConstants(Handle, layout, stages, offset, (uint)sizeof(T), p);
        Log?.PushConstants(offset, new ReadOnlySpan<byte>(Unsafe.AsPointer(ref Unsafe.AsRef(in value)), sizeof(T)));
    }

    public void Draw(uint vertices, uint instances = 1, uint firstVertex = 0, uint firstInstance = 0)
    {
        Log?.Draw(false, vertices, instances, firstVertex, 0, firstInstance);
        vk.CmdDraw(Handle, vertices, instances, firstVertex, firstInstance);
        Stats.Draws++;
    }

    public void DrawIndexed(uint indices, uint instances = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
    {
        Log?.Draw(true, indices, instances, firstIndex, vertexOffset, firstInstance);
        vk.CmdDrawIndexed(Handle, indices, instances, firstIndex, vertexOffset, firstInstance);
        Stats.Draws++;
    }

    public void DrawIndexedIndirect(Buffer args, ulong offset, uint count, uint stride = 20)
    {
        Log?.Indirect(args, offset, count, stride);
        vk.CmdDrawIndexedIndirect(Handle, args, offset, count, stride);
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
