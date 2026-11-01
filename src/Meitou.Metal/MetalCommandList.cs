using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using SharpMetal.QuartzCore;

namespace Meitou.Metal;

/// <summary>
/// A command list being recorded, on Metal (docs/renderer-native.md 2.7): the engine's words onto a Metal
/// command buffer's encoders. Sized to the sky pass: renderings, the draw state, a program's resources
/// (Metal's stages have their own binding tables, so a set's slots bind straight onto the encoder — no
/// argument buffer), labels, and the mip chain's blit. Everything else throws (docs/metal-backend.md).
/// </summary>
public sealed class MetalCommandList : IGpuCommandList
{
    readonly MetalContext ctx;
    readonly string name;
    MTLCommandBuffer command;
    MTLRenderCommandEncoder render;
    MTLComputeCommandEncoder compute;
    MTLBlitCommandEncoder blit;
    GpuProgram computeProgram;
    GpuComputePipeline lastCompute;
    bool ownsBlit;
    bool depthAttachment;
    GpuPipeline lastPipeline;
    PrimitiveType topology;
    RenderingDesc renderDesc;
    PixelViewport viewport;
    PixelRect scissor;
    bool hasViewport, hasScissor;
    bool depthStateKnown, depthTest, depthWrite;
    DepthFunction depthOp;
    long epoch;
    // The redundancy filters are on by default (validated in the 2026-10-24 Metal run); MEITOU_METAL_NO_STATE_CACHE=1
    // reissues every state for an A/B. They are independent of the experimental GPU cull.
    static readonly bool CacheEncoderState = Environment.GetEnvironmentVariable("MEITOU_METAL_NO_STATE_CACHE") != "1";
    // Metal keeps these bound for the encoder's lifetime. Skip a bind that would write the same buffer and offset
    // again; Xcode's API insights otherwise report dozens of redundant binds in a busy scene encoder.
    readonly MTLBuffer[] vertexBufferState = new MTLBuffer[64];
    readonly ulong[] vertexOffsetState = new ulong[64];
    readonly MTLBuffer[] fragmentBufferState = new MTLBuffer[64];
    readonly ulong[] fragmentOffsetState = new ulong[64];
    readonly MTLTexture[] vertexTextureState = new MTLTexture[64];
    readonly MTLSamplerState[] vertexSamplerState = new MTLSamplerState[64];
    readonly float[] vertexSamplerBias = new float[64];
    readonly MTLTexture[] fragmentTextureState = new MTLTexture[64];
    readonly MTLSamplerState[] fragmentSamplerState = new MTLSamplerState[64];
    readonly float[] fragmentSamplerBias = new float[64];

    /// <summary>Made by the context (the frame's) or an upload batch (its own command buffer, its blit shared).</summary>
    internal MetalCommandList(MetalContext ctx, string name, MTLCommandBuffer command, MTLBlitCommandEncoder? sharedBlit = null)
    {
        this.ctx = ctx;
        this.name = name;
        this.command = command;
        if (sharedBlit.HasValue) { blit = sharedBlit.Value; ownsBlit = false; }
    }

    public GpuStats Stats => ctx.Frame.Stats;
    public long Epoch => epoch;
    public object? BoundProgram { get; set; }

    /// <summary>Forgets the pipeline and program state; the encoder's buffer bindings stay valid across a segment label.</summary>
    public void Invalidate()
    {
        lastPipeline = default;
        BoundProgram = null;
        epoch++;
    }

    // ---- rendering ----

    public void BeginRendering(in RenderingDesc d, bool secondaries = false)
    {
        if (secondaries) throw new NotSupportedException("the Metal backend records no secondary command lists yet");
        EndCompute();
        EndBlit();
        lastPipeline = default;   // a new encoder starts with no pipeline state (the filter would otherwise skip the set)
        var descriptor = new MTLRenderPassDescriptor(
            ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("MTLRenderPassDescriptor"), "renderPassDescriptor"));
        if (!d.Colour.IsNull)
        {
            // The attachments come back as struct copies, so a value-type property (the clear colour) has to be written back
            // into the array, or the pass clears to whatever the descriptor's memory held (docs/metal-backend.md).
            var attachments = descriptor.ColorAttachments;
            var attachment = attachments.Object(0);
            attachment.Texture = ctx.ViewTexture(d.Colour.View);
            attachment.LoadAction = d.Colour.Load switch { LoadOp.Clear => MTLLoadAction.Clear, LoadOp.DontCare => MTLLoadAction.DontCare, _ => MTLLoadAction.Load };
            attachment.StoreAction = MTLStoreAction.Store;
            attachment.ClearColor = new MTLClearColor { red = d.Colour.Clear.R, green = d.Colour.Clear.G, blue = d.Colour.Clear.B, alpha = d.Colour.Clear.A };
            attachments.SetObject(attachment, 0);
        }
        if (!d.Extra.IsNull)
        {
            // The second colour target (the lit fragments' albedo for the GI resolve).
            var attachments = descriptor.ColorAttachments;
            var extra = attachments.Object(1);
            extra.Texture = ctx.ViewTexture(d.Extra.View);
            extra.LoadAction = d.Extra.Load switch { LoadOp.Clear => MTLLoadAction.Clear, LoadOp.DontCare => MTLLoadAction.DontCare, _ => MTLLoadAction.Load };
            extra.StoreAction = MTLStoreAction.Store;
            extra.ClearColor = new MTLClearColor { red = d.Extra.Clear.R, green = d.Extra.Clear.G, blue = d.Extra.Clear.B, alpha = d.Extra.Clear.A };
            attachments.SetObject(extra, 1);
        }
        if (!d.Depth.IsNull)
        {
            var depth = descriptor.DepthAttachment;
            depth.Texture = ctx.ViewTexture(d.Depth.View);
            depth.LoadAction = d.Depth.Load switch { LoadOp.Clear => MTLLoadAction.Clear, LoadOp.DontCare => MTLLoadAction.DontCare, _ => MTLLoadAction.Load };
            depth.StoreAction = MTLStoreAction.Store;
            depth.ClearDepth = d.Depth.DepthClear;
        }
        render = command.RenderCommandEncoder(descriptor);
        Array.Clear(vertexBufferState);
        Array.Clear(vertexOffsetState);
        Array.Clear(fragmentBufferState);
        Array.Clear(fragmentOffsetState);
        Array.Clear(vertexTextureState);
        Array.Clear(vertexSamplerState);
        Array.Clear(vertexSamplerBias);
        Array.Clear(fragmentTextureState);
        Array.Clear(fragmentSamplerState);
        Array.Clear(fragmentSamplerBias);
        pushSent.Clear();
        depthAttachment = !d.Depth.IsNull;
        renderDesc = d;
        hasViewport = hasScissor = false;
        depthStateKnown = false;
        if (name.Length > 0) render.PushDebugGroup((NSString)name);
    }

    public void EndRendering()
    {
        if (render.NativePtr == IntPtr.Zero) throw new InvalidOperationException("EndRendering without BeginRendering");
        render.PopDebugGroup();
        render.EndEncoding();
        render = default;
    }

    /// <summary>Clears a depth area (a shadow tile, the depth between slices): a fullscreen triangle under the rect's scissor.</summary>
    public void ClearDepth(float value, PixelRect rect)
    {
        if (render.NativePtr == IntPtr.Zero) throw new InvalidOperationException("ClearDepth outside a rendering");
        Clear(false, default, true, value, rect);
    }

    /// <summary>Clears the colour and / or the depth attachment over an area; the pass's pipeline state is forgotten, so the next draw binds again.</summary>
    public void Clear(bool colour, ClearColour colourValue, bool depth, float depthValue, PixelRect rect)
    {
        if (render.NativePtr == IntPtr.Zero) throw new InvalidOperationException("Clear outside a rendering");
        ctx.ClearAttachments(render, renderDesc, colour, colourValue, depth, depthValue, rect);
        lastPipeline = default;
        depthStateKnown = false;   // ClearAttachments writes a depth state directly
        if (hasViewport) render.SetViewport(new MTLViewport { originX = viewport.X, originY = viewport.Y, width = viewport.Width, height = viewport.Height, znear = viewport.MinDepth, zfar = viewport.MaxDepth });
        if (hasScissor) render.SetScissorRect(new MTLScissorRect { x = (ulong)scissor.X, y = (ulong)scissor.Y, width = scissor.Width, height = scissor.Height });
    }

    // ---- state ----

    public void BindPipeline(GpuPipeline pipeline)
    {
        if (pipeline.Desc == lastPipeline.Desc) return;
        if (render.NativePtr == IntPtr.Zero) throw new InvalidOperationException("BindPipeline outside a rendering");
        render.SetRenderPipelineState(ctx.Pipelines.Backend(pipeline));
        ctx.Pipelines.BindDepthClipMode(render, pipeline.Desc.DepthClamp);
        topology = pipeline.Desc.Topology;
        lastPipeline = pipeline;
        Stats.PipelinesBound++;
    }

    /// <summary>Binds a compute pipeline (the encoder's <c>setComputePipelineState</c>).</summary>
    public void BindPipeline(GpuComputePipeline pipeline)
    {
        if (pipeline.Desc == lastCompute.Desc) return;
        var encoder = ComputeEncoder();
        encoder.SetComputePipelineState(ctx.Pipelines.Backend(pipeline));
        computeProgram = pipeline.Desc.Program;
        lastCompute = pipeline;
        Stats.PipelinesBound++;
    }

    public void SetViewport(in PixelViewport v)
    {
        bool same = CacheEncoderState && hasViewport && viewport == v;
        viewport = v;
        hasViewport = true;
        if (!same) render.SetViewport(new MTLViewport { originX = v.X, originY = v.Y, width = v.Width, height = v.Height, znear = v.MinDepth, zfar = v.MaxDepth });
    }

    public void SetScissor(in PixelRect r)
    {
        bool same = CacheEncoderState && hasScissor && scissor == r;
        scissor = r;
        hasScissor = true;
        if (!same) render.SetScissorRect(new MTLScissorRect { x = (ulong)r.X, y = (ulong)r.Y, width = r.Width, height = r.Height });
    }

    public void SetRaster(CullMode cull, FrontFaceDirection front)
    {
        render.SetCullMode(MetalConventions.Cull(cull));
        render.SetFrontFacingWinding(MetalConventions.Winding(front));
    }

    /// <summary>The front face alone (a draw that turns the winding round, the cull mode left as set).</summary>
    public void SetFrontFace(FrontFaceDirection front) => render.SetFrontFacingWinding(MetalConventions.Winding(front));

    public void SetDepth(bool test, bool write, DepthFunction op)
    {
        // With no depth attachment the state would be read nowhere; Metal's validation objects to setting it.
        if (!depthAttachment) return;
        if (CacheEncoderState && depthStateKnown && depthTest == test && depthWrite == write && depthOp == op) return;
        render.SetDepthStencilState(ctx.DepthState(test, write, op));
        (depthStateKnown, depthTest, depthWrite, depthOp) = (true, test, write, op);
    }

    public void SetDepthBias(bool enable, float constant, float slope) => render.SetDepthBias(enable ? constant : 0, enable ? slope : 0, 0);

    // ---- resources ----

    /// <summary>Binds the vertex buffers of the locations from <paramref name="first"/>: the pipeline's slots, Vulkan's own numbers
    /// (the MSL's indices below <see cref="MetalShaders.VertexBlockBase"/>, where the blocks go).</summary>
    public unsafe void BindVertexBuffers(uint first, ReadOnlySpan<GpuBufferBinding> bindings)
    {
        if (render.NativePtr == IntPtr.Zero) throw new InvalidOperationException("BindVertexBuffers outside a rendering");
        if (first + (uint)bindings.Length > MetalShaders.VertexBlockBase)
            throw new NotSupportedException($"the Metal backend keeps the vertex buffers in the slots below {MetalShaders.VertexBlockBase} (docs/metal-backend.md), {first}..{first + bindings.Length - 1} asked");
        for (int i = 0; i < bindings.Length; i++)
        {
            var (buffer, offset) = ctx.Buffer(bindings[i]);
            BindVertexBuffer(first + (uint)i, buffer, offset);
        }
    }

    void BindVertexBuffer(uint index, MTLBuffer buffer, ulong offset)
    {
        if (index >= (uint)vertexBufferState.Length)
            throw new NotSupportedException($"vertex buffer index {index} is outside the Metal buffer table");
        if (CacheEncoderState && vertexBufferState[index].NativePtr == buffer.NativePtr)
        {
            if (vertexOffsetState[index] == offset) return;
            // Same buffer, new slice: setVertexBufferOffset is the cheap path Xcode asks for.
            render.SetVertexBufferOffset(offset, index);
            vertexOffsetState[index] = offset;
            return;
        }
        render.SetVertexBuffer(buffer, offset, index);
        vertexBufferState[index] = buffer;
        vertexOffsetState[index] = offset;
    }

    void BindFragmentBuffer(uint index, MTLBuffer buffer, ulong offset)
    {
        if (index >= (uint)fragmentBufferState.Length)
            throw new NotSupportedException($"fragment buffer index {index} is outside the Metal buffer table");
        if (CacheEncoderState && fragmentBufferState[index].NativePtr == buffer.NativePtr)
        {
            if (fragmentOffsetState[index] == offset) return;
            render.SetFragmentBufferOffset(offset, index);
            fragmentOffsetState[index] = offset;
            return;
        }
        render.SetFragmentBuffer(buffer, offset, index);
        fragmentBufferState[index] = buffer;
        fragmentOffsetState[index] = offset;
    }

    /// <summary>Binds the index buffer, held until the next indexed draw.</summary>
    public void BindIndexBuffer(GpuBufferBinding binding, IndexKind type)
    {
        (indexBuffer, indexOffset) = ctx.Buffer(binding);
        indexType = MetalConventions.IndexType(type);
    }
    MTLBuffer indexBuffer;
    ulong indexOffset;
    MTLIndexType indexType;

    public void BindProgramResources(GpuProgram program, uint set, ReadOnlySpan<GpuBinding> bindings)
    {
        if (set != 0) throw new NotSupportedException($"the Metal backend binds set {set} only through CreateSet and BindSets");
        if (program.Model == ShaderModel.Compute)
        {
            _ = ComputeEncoder();
            BindSetArguments(ctx.Shaders.Backend(program), set, bindings, computePass: true);
            Stats.DescriptorPushes++;
            return;
        }
        if (render.NativePtr == IntPtr.Zero) throw new InvalidOperationException("BindProgramResources outside a rendering");
        if (ctx.Shaders.Backend(program).ArgumentBuffers) { BindSetArguments(ctx.Shaders.Backend(program), set, bindings); return; }
        var slots = program.NeutralSets[set].Slots;
        foreach (var binding in bindings)
        {
            foreach (var slot in slots)
            {
                if (slot.Binding != binding.Slot) continue;
                if (slot.Stages.HasFlag(GpuStage.Vertex)) BindSlot(isVertex: true, set, slot, binding);
                if (slot.Stages.HasFlag(GpuStage.Fragment)) BindSlot(isVertex: false, set, slot, binding);
            }
        }
        Stats.DescriptorPushes++;
    }

    void BindSlot(bool isVertex, uint set, in GpuSlot slot, in GpuBinding binding)
    {
        // Metal's one binding table per stage: the slot's flattened index (MetalShaders.Flatten, the MSL's own numbers).
        uint index = MetalShaders.Flatten(slot.Kind, slot.Stages, set, slot.Binding);
        switch (binding.Kind)
        {
            case GpuBindingKind.Sampled:
                var (texture, sampler) = ctx.Sampled(binding.Texture);
                float bias = MetalSamplers.LodBias(binding.Texture.Sampler);
                if (isVertex)
                {
                    if (!CacheEncoderState || vertexTextureState[index].NativePtr != texture.NativePtr)
                    {
                        render.SetVertexTexture(texture, index);
                        vertexTextureState[index] = texture;
                    }
                    if (!CacheEncoderState || vertexSamplerState[index].NativePtr != sampler.NativePtr || vertexSamplerBias[index] != bias)
                    {
                        render.SetVertexSamplerState(sampler, bias, 1000 + bias, index);
                        vertexSamplerState[index] = sampler;
                        vertexSamplerBias[index] = bias;
                    }
                }
                else
                {
                    if (!CacheEncoderState || fragmentTextureState[index].NativePtr != texture.NativePtr)
                    {
                        render.SetFragmentTexture(texture, index);
                        fragmentTextureState[index] = texture;
                    }
                    if (!CacheEncoderState || fragmentSamplerState[index].NativePtr != sampler.NativePtr || fragmentSamplerBias[index] != bias)
                    {
                        render.SetFragmentSamplerState(sampler, bias, 1000 + bias, index);
                        fragmentSamplerState[index] = sampler;
                        fragmentSamplerBias[index] = bias;
                    }
                }
                break;
            case GpuBindingKind.UniformBuffer or GpuBindingKind.StorageBuffer:
                var (buffer, offset) = ctx.Buffer(binding.Buffer);
                if (isVertex) BindVertexBuffer(index, buffer, offset);
                else BindFragmentBuffer(index, buffer, offset);
                break;
            default:
                throw new NotSupportedException($"the Metal backend binds no storage images yet ({binding.Kind} at set {set}, binding {binding.Slot})");
        }
    }

    public void BindSets(GpuProgram program, uint first, ReadOnlySpan<GpuSet> sets, ReadOnlySpan<uint> dynamicOffsets, GpuBindPoint point = GpuBindPoint.Graphics)
    {
        bool computePass = point == GpuBindPoint.Compute;
        if (computePass) _ = ComputeEncoder();
        else if (render.NativePtr == IntPtr.Zero) throw new InvalidOperationException("BindSets outside a rendering");
        if (ctx.Shaders.Backend(program).ArgumentBuffers)
        {
            int at = 0;
            for (int i = 0; i < sets.Length; i++)
            {
                var set = ctx.SetAt(sets[i]);
                var bindings = new GpuBinding[set.Bindings.Length];
                for (int b = 0; b < bindings.Length; b++)
                {
                    var binding = set.Bindings[b];
                    if (set.Layout.Slots[b].OffsetAtBind)
                        binding = new GpuBinding(binding.Slot, binding.Kind, Buffer: new GpuBufferBinding(binding.Buffer.Buffer, dynamicOffsets.IsEmpty ? 0 : dynamicOffsets[at++], binding.Buffer.Size));
                    bindings[b] = binding;
                }
                BindSetArguments(ctx.Shaders.Backend(program), first + (uint)i, bindings, computePass);
            }
            Stats.DescriptorPushes++;
            return;
        }
        int used = 0;
        for (int i = 0; i < sets.Length; i++)
        {
            var set = ctx.SetAt(sets[i]);
            uint index = first + (uint)i;
            int next = 0;
            foreach (var slot in set.Layout.Slots)
            {
                var binding = set.Bindings[next++].Buffer;
                // A slot whose offset comes at bind time takes the bind's dynamic offset (the default blocks' slices).
                if (slot.OffsetAtBind) binding = new GpuBufferBinding(binding.Buffer, dynamicOffsets.IsEmpty ? 0 : dynamicOffsets[used++], binding.Size);
                var (buffer, offset) = ctx.Buffer(binding);
                // Metal's one binding table per stage: the slot's flattened index (MetalShaders.Flatten, the MSL's own numbers).
                if (slot.Stages.HasFlag(GpuStage.Vertex))
                    BindVertexBuffer(MetalShaders.Flatten(slot.Kind, GpuStage.Vertex, index, slot.Binding), buffer, offset);
                if (slot.Stages.HasFlag(GpuStage.Fragment))
                    BindFragmentBuffer(MetalShaders.Flatten(slot.Kind, GpuStage.Fragment, index, slot.Binding), buffer, offset);
            }
        }
        Stats.DescriptorPushes++;
    }

    /// <summary>
    /// A descriptor set as a Metal argument buffer (every set of a program translated with them): its members are 8-byte GPU
    /// addresses at the flattened ids (<see cref="MetalShaders.Flatten"/>), the same buffer serving both stages at their own set
    /// indices (set + 15 in the vertex stage, whose buffers take 0 to 14; set in the fragment stay as they are). The addresses
    /// live in the frame's constants ring (a slice of their own per bind: a later bind of the same set must not change what an
    /// earlier draw reads).
    /// </summary>
    unsafe void BindSetArguments(MetalProgram backend, uint set, ReadOnlySpan<GpuBinding> bindings, bool computePass = false)
    {
        if (computePass) { BindSetStage(backend.ComputeSets, GpuStage.Compute, set, bindings, computePass: true); return; }
        BindSetStage(backend.VertexSets, GpuStage.Vertex, set, bindings);
        BindSetStage(backend.FragmentSets, GpuStage.Fragment, set, bindings);
    }

    /// <summary>One stage's set: the bindings' addresses at the positions their <c>[[id]]</c>s name in the stage's struct
    /// (Metal lays an argument buffer's members out by declaration, so the position is the entry, not the id).</summary>
    unsafe void BindSetStage(MetalSetLayout layout, GpuStage stage, uint set, ReadOnlySpan<GpuBinding> bindings, bool computePass = false)
    {
        uint[] members = layout.Members(set);
        if (members.Length == 0) return;   // the stage's shader does not read this set
        Span<ulong> entries = stackalloc ulong[members.Length];
        entries.Clear();
        bool any = false;
        foreach (var binding in bindings)
        {
            GpuSlotKind slotKind = binding.Kind switch
            {
                GpuBindingKind.UniformBuffer => GpuSlotKind.UniformBuffer,
                GpuBindingKind.StorageBuffer => GpuSlotKind.StorageBuffer,
                GpuBindingKind.Sampled => GpuSlotKind.CombinedSampler,
                GpuBindingKind.StorageImage => GpuSlotKind.CombinedSampler,   // the id is the slot; the kind only picks the table
                _ => throw new NotSupportedException($"the Metal backend puts no {binding.Kind} in an argument buffer set (set {set}, binding {binding.Slot})"),
            };
            int at = Array.IndexOf(members, MetalShaders.Flatten(slotKind, stage, set, binding.Slot));
            if (at < 0) continue;   // this stage's struct has no such member (its shader never reads it)
            if (binding.Kind is GpuBindingKind.UniformBuffer or GpuBindingKind.StorageBuffer)
            {
                if (binding.Buffer.Buffer.IsNull) continue;
                var (buffer, offset) = ctx.Buffer(binding.Buffer);
                UseResource(buffer, MTLResourceUsage.Read, computePass);
                entries[at] = buffer.GpuAddress + offset;
            }
            else
            {
                if (binding.Texture.IsNull) continue;
                var image = ctx.ViewTexture(binding.Texture.View);
                UseResource(image, binding.Kind == GpuBindingKind.StorageImage ? MTLResourceUsage.Read | MTLResourceUsage.Write : MTLResourceUsage.Read, computePass);
                entries[at] = image.GpuResourceID._impl;
            }
            any = true;
        }
        if (!any) return;
        // The MSL struct is padded to argument-buffer alignment (16); a one-member set needs 16 bytes, not 8.
        var size = Math.Max(16ul, (ulong)members.Length * 8 + 15 & ~15ul);
        // A slice of the frame's constants ring, not a fresh 16-byte MTLBuffer per bind (Xcode's late-buffer insight).
        var constants = ctx.Frame.AllocateConstants(size, 16);
        fixed (ulong* from = entries) Buffer.MemoryCopy(from, (void*)constants.Pointer, (long)size, (long)size);
        var argumentBuffer = ctx.BufferAt(constants.Handle);
        if (computePass) compute.SetBuffer(argumentBuffer, constants.Offset, set);
        else if (stage == GpuStage.Vertex) BindVertexBuffer(MetalShaders.VertexBlockBase + set, argumentBuffer, constants.Offset);
        else BindFragmentBuffer(set, argumentBuffer, constants.Offset);
    }

    /// <summary>Tells the current encoder a resource the argument buffer holds; the compute encoder needs it for its writes too.</summary>
    void UseResource(MTLBuffer buffer, MTLResourceUsage usage, bool computePass)
    {
        if (computePass) compute.UseResource(buffer, usage);
        else render.UseResource(buffer, usage);
    }

    void UseResource(MTLTexture texture, MTLResourceUsage usage, bool computePass)
    {
        if (computePass) compute.UseResource(texture, usage);
        else render.UseResource(texture, usage);
    }

    /// <summary>Binds the bindless table: the slot's argument buffer at <see cref="MslTranslator.TableBinding"/> in both stages
    /// (the MSL's table set is moved there, so the set's number is only the layout's word for it).</summary>
    public void BindBindlessTable(GpuProgram program, uint set, GpuBindPoint point = GpuBindPoint.Graphics)
    {
        bool computePass = point == GpuBindPoint.Compute;
        if (computePass) _ = ComputeEncoder();
        else if (render.NativePtr == IntPtr.Zero) throw new InvalidOperationException("BindBindlessTable outside a rendering");
        if (set != Rendering.NativeShaders.BindlessSet) throw new InvalidOperationException($"the Metal backend's table is set {Rendering.NativeShaders.BindlessSet} (asked for {set})");
        var backend = ctx.Shaders.Backend(program);
        int slot = ctx.Frame.Slot;
        if (computePass)
        {
            if (ctx.BindlessTable.BufferFor(backend.ComputeTable, slot) is { } table) compute.SetBuffer(table, 0, MslTranslator.TableBinding);
        }
        else
        {
            if (ctx.BindlessTable.BufferFor(backend.VertexTable, slot) is { } vertex)
                BindVertexBuffer(MslTranslator.TableBinding, vertex, 0);
            if (ctx.BindlessTable.BufferFor(backend.FragmentTable, slot) is { } fragment)
                BindFragmentBuffer(MslTranslator.TableBinding, fragment, 0);
        }
        ctx.BindlessTable.UseResources(this);
        Stats.DescriptorPushes++;
    }

    /// <summary>Tells the encoder a resource the shaders read through the argument buffer (not through a direct binding).</summary>
    internal void UseResource(MTLTexture texture)
    {
        if (render.NativePtr != IntPtr.Zero) render.UseResource(texture, MTLResourceUsage.Read);
        else if (compute.NativePtr != IntPtr.Zero) compute.UseResource(texture, MTLResourceUsage.Read);
    }

    /// <summary>Pushes a block of constants: the bytes both stages' encoders take inline at the stage's top index
    /// (<see cref="MslTranslator.PushConstantBinding"/>, the push blocks' own <c>[[buffer(n)]]</c>).</summary>
    public unsafe void PushConstants<T>(GpuProgram program, GpuStage stages, in T value, uint offset = 0) where T : unmanaged
    {
        // The MSL block is the SPIR-V one, rounded up to its 16-byte alignment; pushing only sizeof(T) fails Metal's length check
        // (the terrain's 24-byte block is 32 there). Vulkan's push constants keep the bytes a partial push does not name, so the
        // list keeps the stage's last block and sets the named range in it (every draw pushes its whole block, as the renderers do).
        int raw = sizeof(T);
        uint end = offset + (uint)raw;
        if (end > program.PushConstantBytes) throw new ArgumentOutOfRangeException(nameof(offset), $"{program.Name}: the push ends at {end}, the block is {program.PushConstantBytes} bytes");
        int padded = (int)((Math.Max(program.PushConstantBytes, end) + 15u) & ~15u);
        byte* source = (byte*)Unsafe.AsPointer(ref Unsafe.AsRef(in value));
        if (stages.HasFlag(GpuStage.Compute)) PushStage(GpuStage.Compute, program, source, raw, (int)offset, padded, compute: true);
        if (stages.HasFlag(GpuStage.Vertex)) PushStage(GpuStage.Vertex, program, source, raw, (int)offset, padded, compute: false);
        if (stages.HasFlag(GpuStage.Fragment)) PushStage(GpuStage.Fragment, program, source, raw, (int)offset, padded, compute: false);
    }

    readonly Dictionary<(int Program, GpuStage Stage), byte[]> pushBlocks = [];
    readonly Dictionary<(int Program, GpuStage Stage), byte[]> pushSent = [];

    unsafe void PushStage(GpuStage stage, GpuProgram program, byte* source, int raw, int offset, int padded, bool compute)
    {
        var key = (program.Index, stage);
        if (!pushBlocks.TryGetValue(key, out var block) || block.Length < padded)
            pushBlocks[key] = block = new byte[Math.Max(padded, 16)];
        if (!pushSent.TryGetValue(key, out var sent) || sent.Length < block.Length)
            pushSent[key] = sent = new byte[block.Length];
        fixed (byte* destination = block)
        {
            Buffer.MemoryCopy(source, destination + offset, block.Length - offset, raw);
            if (block.AsSpan(0, block.Length).SequenceEqual(sent.AsSpan(0, block.Length))) return;
            block.AsSpan(0, block.Length).CopyTo(sent);
        }
        fixed (byte* destination = sent)
        {
            if (compute) ComputeEncoder().SetBytes((nint)destination, (ulong)block.Length, MslTranslator.PushConstantBinding);
            else if (stage == GpuStage.Vertex) render.SetVertexBytes((nint)destination, (ulong)block.Length, MslTranslator.PushConstantBinding);
            else render.SetFragmentBytes((nint)destination, (ulong)block.Length, MslTranslator.PushConstantBinding);
        }
    }

    /// <summary>Writes a timestamp into a query slot (the frame's timings). Metal records none yet (<see cref="MetalTimestamps"/>).</summary>
    public void Timestamp(IGpuTimestamps arena, in QuerySlot slot, GpuStage stage = GpuStage.All) { }

    // ---- draws ----

    public void Draw(uint vertices, uint instances = 1, uint firstVertex = 0, uint firstInstance = 0)
    {
        // The grass draws a blade range by firstInstance; baseInstance is Metal's own (per-instance attributes fetch from it).
        if (firstInstance != 0)
            render.DrawPrimitives(MetalConventions.Primitive(topology), firstVertex, vertices, instances, firstInstance);
        else
            render.DrawPrimitives(MetalConventions.Primitive(topology), firstVertex, vertices, instances);
        Stats.Draws++;
    }

    /// <summary>Draws indexed vertices out of the bound index buffer.</summary>
    public unsafe void DrawIndexed(uint indices, uint instances = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
    {
        if (indexBuffer.NativePtr == IntPtr.Zero) throw new InvalidOperationException("DrawIndexed with no index buffer bound");
        render.DrawIndexedPrimitives(MetalConventions.Primitive(topology), indices, indexType, indexBuffer,
            indexOffset + (ulong)firstIndex * MetalConventions.IndexBytes(indexType), instances, vertexOffset, firstInstance);
        Stats.Draws++;
    }

    /// <summary>Draws indexed vertices, the arguments in a buffer: one Metal indirect draw per command (Metal has no multi-draw).</summary>
    public void DrawIndexedIndirect(GpuBuffer args, ulong offset, uint count, uint stride = 20)
    {
        if (count == 0) return;
        if (indexBuffer.NativePtr == IntPtr.Zero) throw new InvalidOperationException("DrawIndexedIndirect with no index buffer bound");
        var buffer = ctx.BufferAt(args);
        for (uint i = 0; i < count; i++)
            render.DrawIndexedPrimitives(MetalConventions.Primitive(topology), indexType, indexBuffer, indexOffset, buffer, offset + i * stride);
        Stats.Draws += (int)count;
    }

    /// <summary>
    /// Draws indexed vertices, the count in another buffer. Metal has no draw-count command: every slot is issued and the
    /// arguments past the count must be zero (the caller's cull pads its list; a slot with indexCount 0 draws nothing).
    /// </summary>
    public void DrawIndexedIndirectCount(GpuBuffer args, ulong offset, GpuBuffer count, ulong countOffset, uint maxCount, uint stride = 20)
    {
        // The count buffer is the GPU's; reading it here would need a wait, so the arguments carry the decision.
        if (maxCount == 0) return;
        if (indexBuffer.NativePtr == IntPtr.Zero) throw new InvalidOperationException("DrawIndexedIndirectCount with no index buffer bound");
        var buffer = ctx.BufferAt(args);
        for (uint i = 0; i < maxCount; i++)
            render.DrawIndexedPrimitives(MetalConventions.Primitive(topology), indexType, indexBuffer, indexOffset, buffer, offset + i * stride);
        Stats.Draws += (int)maxCount;
    }

    /// <summary>Draws indirect, the count in another buffer: as <see cref="DrawIndexedIndirectCount"/>, the unused slots zero.</summary>
    public void DrawIndirectCount(GpuBuffer args, ulong offset, GpuBuffer countBuffer, ulong countOffset, uint maxCount, uint stride = 16)
    {
        if (maxCount == 0) return;
        var buffer = ctx.BufferAt(args);
        for (uint i = 0; i < maxCount; i++)
            render.DrawPrimitives(MetalConventions.Primitive(topology), buffer, offset + i * stride);
        Stats.Draws += (int)maxCount;
    }

    /// <summary>Dispatches a compute pass: the arguments are threadgroups, as Vulkan's <c>vkCmdDispatch</c>'s; the shader's
    /// <c>local_size_x/y/z</c> (parsed from the GLSL) is the threadgroup.</summary>
    public void Dispatch(uint x, uint y = 1, uint z = 1)
    {
        var encoder = ComputeEncoder();
        if (computeProgram.IsNull) throw new InvalidOperationException("Dispatch without a bound compute pipeline");
        var threads = ctx.Shaders.Backend(computeProgram).Threads;
        encoder.DispatchThreadgroups(new MTLSize { width = x, height = y, depth = z },
            new MTLSize { width = threads.X, height = threads.Y, depth = threads.Z });
        Stats.Draws++;
    }

    /// <summary>A new texture: Metal tracks no image layouts, so the context makes it and nothing is recorded.</summary>
    public GpuTexture CreateTexture(TextureDesc desc) => ctx.CreateTexture(desc);

    // ---- transfers ----

    /// <summary>Fills a range of a buffer with a value (the blit encoder's <c>fillBuffer</c>).</summary>
    public void FillBuffer(GpuBuffer b, ulong offset, ulong size, uint value)
    {
        // Metal fills with one byte; every caller so far clears (0).
        if (value != 0) throw new NotSupportedException($"the Metal backend fills buffers with zero only ({value:x8} asked)");
        var blit = OpenBlit();
        blit.FillBuffer(ctx.BufferAt(b), new NSRange { location = offset, length = size }, 0);
    }

    /// <summary>Copies a range of a buffer into another (the blit encoder's <c>copyFromBuffer</c>).</summary>
    public void CopyBuffer(GpuBuffer src, GpuBuffer dst, in GpuBufferCopy region)
    {
        var blit = OpenBlit();
        blit.CopyFromBuffer(ctx.BufferAt(src), region.SourceOffset, ctx.BufferAt(dst), region.DestinationOffset, region.Size);
    }

    /// <summary>Copies a tightly packed level and layer of a buffer into a texture (the ocean's FFT results' upload).</summary>
    public void CopyBufferToImage(GpuBuffer src, ulong offset, GpuTexture dst, int level, int layer)
    {
        var desc = dst.Desc;
        int block = MetalConventions.BlockWidth(desc.Format);
        int texel = MetalConventions.TexelBytes(desc.Format);
        uint width = (uint)Math.Max(desc.Width >> level, 1), height = (uint)Math.Max(desc.Height >> level, 1);
        ulong bytesPerRow = (ulong)(((width + (uint)block - 1) / (uint)block) * (uint)texel);
        ulong blockRows = (height + (uint)block - 1) / (uint)block;
        var blit = OpenBlit();
        blit.CopyFromBuffer(ctx.BufferAt(src), offset, bytesPerRow, bytesPerRow * blockRows,
            new MTLSize { width = width, height = height, depth = 1 },
            ctx.TextureAt(dst), (ulong)layer, (ulong)level, new MTLOrigin());
    }

    /// <summary>Copies the first layers of every level of a texture into another of the same format and size (the blit encoder's texture copy, a slice at a time).</summary>
    public void CopyLayers(GpuTexture src, GpuTexture dst, int layers)
    {
        var d = src.Desc;
        int levels = Math.Min(d.Levels, dst.Desc.Levels);
        if (layers <= 0 || levels <= 0) return;
        var blit = OpenBlit();
        for (uint layer = 0; layer < (uint)layers; layer++)
            for (int level = 0; level < levels; level++)
                blit.CopyFromTexture(ctx.TextureAt(src), layer, (ulong)level, new MTLOrigin(),
                    new MTLSize { width = (ulong)Math.Max(d.Width >> level, 1), height = (ulong)Math.Max(d.Height >> level, 1), depth = 1 },
                    ctx.TextureAt(dst), layer, (ulong)level, new MTLOrigin());
        EndBlit();
    }

    /// <summary>Resolves a multisample texture into a single-sample one: a render pass whose only work is its resolve attachment.</summary>
    public void Resolve(GpuTexture src, GpuTexture dst)
    {
        if (render.NativePtr != IntPtr.Zero) throw new InvalidOperationException("Resolve inside a rendering");
        EndCompute();
        EndBlit();
        var descriptor = new MTLRenderPassDescriptor(
            ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("MTLRenderPassDescriptor"), "renderPassDescriptor"));
        var attachments = descriptor.ColorAttachments;
        var attachment = attachments.Object(0);
        attachment.Texture = ctx.TextureAt(src);
        attachment.ResolveTexture = ctx.TextureAt(dst);
        attachment.ResolveLevel = 0;
        attachment.ResolveSlice = 0;
        attachment.LoadAction = MTLLoadAction.Load;
        attachment.StoreAction = MTLStoreAction.MultisampleResolve;
        attachments.SetObject(attachment, 0);
        var encoder = command.RenderCommandEncoder(descriptor);
        encoder.EndEncoding();
    }

    /// <summary>Copies a texture into another: depth by a native copy, colour by a sampling pass that can scale (the impostor bake).</summary>
    public void Blit(GpuTexture src, GpuTexture dst, TextureMinFilter filter)
    {
        EndCompute();
        EndBlit();
        if (render.NativePtr != IntPtr.Zero) EndRendering();
        ctx.Blit(command, src, dst, filter);
    }

    /// <summary>Copies a tightly packed region of a texture's level and layer into a buffer (the impostor bakes' readback).</summary>
    public void CopyTextureToBuffer(GpuTexture src, int level, int layer, in PixelRect region, GpuBuffer dst, ulong offset)
    {
        var blit = OpenBlit();
        ulong bytesPerRow = (ulong)region.Width * (ulong)MetalConventions.TexelBytes(src.Desc.Format);
        blit.CopyFromTexture(ctx.TextureAt(src), (ulong)layer, (ulong)level,
            new MTLOrigin { x = (ulong)region.X, y = (ulong)region.Y, z = 0 },
            new MTLSize { width = (ulong)region.Width, height = (ulong)region.Height, depth = 1 },
            ctx.BufferAt(dst), offset, bytesPerRow, bytesPerRow * region.Height);
    }

    /// <summary>Blits one level of a texture into another (Metal's blit when the sizes match, a sampling pass when they do not).</summary>
    public void BlitLevel(GpuTexture texture, int srcLevel, int dstLevel, TextureMinFilter filter)
    {
        if (render.NativePtr != IntPtr.Zero) throw new InvalidOperationException("BlitLevel inside a rendering");
        EndCompute();
        EndBlit();
        ctx.BlitLevel(command, texture, srcLevel, dstLevel, filter);
    }

    /// <summary>Generates a texture's mip chain (as GL's GenerateMipmap made it: a blit's mip generation, filter linear).</summary>
    public void GenerateMips(GpuTexture t)
    {
        var b = OpenBlit();
        b.GenerateMipmaps(ctx.TextureAt(t));
    }

    // ---- sync ----

    /// <summary>
    /// Records the dependencies the engine's words describe. Two dispatches in one compute encoder may overlap, and a
    /// compute encoder's writes are not always visible to a following blit; closing the encoder at every dependency
    /// makes the next encoder start after the previous one's work. (Apple silicon tracks the resources themselves.)
    /// </summary>
    public void Barrier(ReadOnlySpan<GpuDependency> dependencies)
    {
        EndCompute();
        EndBlit();
    }

    /// <summary>Orders everything recorded before against everything after it: ending the compute and transfer encoders (Metal
    /// orders encoders in one command buffer, and Apple silicon tracks the hazards within one) is the seam.</summary>
    public void Order()
    {
        EndCompute();
        EndBlit();
    }

    // ---- labels ----

    public void BeginLabel(string name)
    {
        if (render.NativePtr != IntPtr.Zero) render.PushDebugGroup((NSString)name);
        else if (blit.NativePtr != IntPtr.Zero) blit.PushDebugGroup((NSString)name);
        else command.PushDebugGroup((NSString)name);
    }

    public void EndLabel()
    {
        if (render.NativePtr != IntPtr.Zero) render.PopDebugGroup();
        else if (blit.NativePtr != IntPtr.Zero) blit.PopDebugGroup();
        else command.PopDebugGroup();
    }

    // ---- the blit encoder (the mip chain's and the uploads') ----

    /// <summary>The blit encoder of this list's command buffer, opened when the first transfer needs it.</summary>
    internal MTLBlitCommandEncoder OpenBlit()
    {
        if (blit.NativePtr != IntPtr.Zero) return blit;
        if (render.NativePtr != IntPtr.Zero) throw new InvalidOperationException("a transfer inside a rendering");
        EndCompute();
        blit = command.BlitCommandEncoder();
        ownsBlit = true;
        return blit;
    }

    internal void EndBlit()
    {
        if (blit.NativePtr == IntPtr.Zero) return;
        if (ownsBlit) blit.EndEncoding();
        blit = default;
        ownsBlit = false;
    }

    /// <summary>The frame's list is reused every frame of its slot: the next frame's command buffer comes in here.</summary>
    internal void Rebind(MTLCommandBuffer next) => command = next;

    /// <summary>Closes what is open (the upload batch's own encoder is the caller's).</summary>
    internal void Finish()
    {
        EndCompute();
        EndBlit();
        if (render.NativePtr != IntPtr.Zero) EndRendering();
    }

    /// <summary>Opens the compute encoder, ending the transfers first (a command buffer records its encoders in order).</summary>
    internal MTLComputeCommandEncoder ComputeEncoder()
    {
        if (compute.NativePtr != IntPtr.Zero) return compute;
        EndBlit();
        if (render.NativePtr != IntPtr.Zero) throw new InvalidOperationException("a compute pass inside a rendering");
        compute = command.ComputeCommandEncoder();
        pushSent.Clear();
        lastCompute = default;
        return compute;
    }

    /// <summary>Ends the compute encoder (the next encoder sees its writes).</summary>
    internal void EndCompute()
    {
        if (compute.NativePtr == IntPtr.Zero) return;
        compute.EndEncoding();
        compute = default;
        lastCompute = default;
    }
}
