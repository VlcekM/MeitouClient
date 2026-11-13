using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using SharpMetal.QuartzCore;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// A command list being recorded, on Metal (docs/renderer-native.md 2.7): the engine's words onto a Metal
/// command buffer's encoders. Sized to the sky pass: renderings, the draw state, a program's resources
/// (Metal's stages have their own binding tables, so a set's slots bind straight onto the encoder — no
/// argument buffer), labels, and the mip chain's blit. Everything else throws (docs/metal-backend.md).
/// </summary>
public sealed class MetalCommandList : IGpuCommandList
{
    /// <summary>
    /// The backend whose handles the list resolves.
    /// </summary>
    readonly MetalContext _ctx;
    /// <summary>
    /// The list's label (the frame's or the upload batch's).
    /// </summary>
    readonly string _name;
    /// <summary>
    /// The command buffer the encoders record into.
    /// </summary>
    MTLCommandBuffer _command;
    /// <summary>
    /// The open render encoder; default outside a rendering.
    /// </summary>
    MTLRenderCommandEncoder _render;
    /// <summary>
    /// The open compute encoder; default when none is open.
    /// </summary>
    MTLComputeCommandEncoder _compute;
    /// <summary>
    /// The open blit encoder; default when none is open.
    /// </summary>
    MTLBlitCommandEncoder _blit;
    /// <summary>
    /// The program of the last compute pipeline bound.
    /// </summary>
    GpuProgram _computeProgram;
    /// <summary>
    /// The last compute pipeline bound; default after the compute encoder ends.
    /// </summary>
    GpuComputePipeline _lastCompute;
    /// <summary>
    /// Whether this list opened <see cref="_blit"/> (and ends it); false for an upload batch's shared blit.
    /// </summary>
    bool _ownsBlit;
    /// <summary>
    /// Whether the open rendering has a depth attachment (no depth state is set without one).
    /// </summary>
    bool _depthAttachment;
    /// <summary>
    /// The last graphics pipeline bound (a rebind of the same description is skipped).
    /// </summary>
    GpuPipeline _lastPipeline;
    /// <summary>
    /// The topology of the bound pipeline, which the draws use.
    /// </summary>
    PrimitiveType _topology;
    /// <summary>
    /// The open rendering's description (its attachments, for <see cref="MetalContext.ClearAttachments"/>).
    /// </summary>
    RenderingDesc _renderDesc;
    /// <summary>
    /// The last viewport set.
    /// </summary>
    PixelViewport _viewport;
    /// <summary>
    /// The last scissor set.
    /// </summary>
    PixelRect _scissor;
    /// <summary>
    /// Whether a viewport and a scissor have been set in this rendering.
    /// </summary>
    bool _hasViewport, _hasScissor;
    /// <summary>
    /// Whether the depth state is known in this rendering, and its last test and write flags.
    /// </summary>
    bool _depthStateKnown, _depthTest, _depthWrite;
    /// <summary>
    /// The last depth compare op set.
    /// </summary>
    DepthFunction _depthOp;
    /// <summary>
    /// Counts <see cref="Invalidate"/> calls (<see cref="Epoch"/>).
    /// </summary>
    long _epoch;
    /// <summary>
    /// Counts the render and compute encoders this list has opened; the one open now is the last.
    /// </summary>
    long _encoderSerial;
    /// <summary>
    /// The encoder (<see cref="_encoderSerial"/>) and the bindless table version (<see cref="MetalBindlessTable.Version"/>) the encoder was last told
    /// the table's textures at; -1 before the first. A resource made resident stays so for the encoder's life, so it is told once.
    /// </summary>
    long _residentSerial = -1, _residentVersion = -1;

    /// <summary>
    /// The redundancy filters, on by default (validated in the 2026-10-24 Metal run); <c>MEITOU_METAL_NO_STATE_CACHE=1</c> reissues every state for
    /// an A/B. They are independent of the experimental GPU cull.
    /// </summary>
    static readonly bool CacheEncoderState = Environment.GetEnvironmentVariable("MEITOU_METAL_NO_STATE_CACHE") != "1";

    /// <summary>
    /// The vertex buffers bound per slot. Metal keeps these bound for the encoder's lifetime: a bind that would write the same buffer and offset
    /// again is skipped; Xcode's API insights otherwise report dozens of redundant binds in a busy scene encoder.
    /// </summary>
    readonly MTLBuffer[] _vertexBufferState = new MTLBuffer[64];
    /// <summary>
    /// The vertex buffer offsets bound per slot.
    /// </summary>
    readonly ulong[] _vertexOffsetState = new ulong[64];
    /// <summary>
    /// The fragment buffers bound per slot.
    /// </summary>
    readonly MTLBuffer[] _fragmentBufferState = new MTLBuffer[64];
    /// <summary>
    /// The fragment buffer offsets bound per slot.
    /// </summary>
    readonly ulong[] _fragmentOffsetState = new ulong[64];
    /// <summary>
    /// The vertex textures bound per slot.
    /// </summary>
    readonly MTLTexture[] _vertexTextureState = new MTLTexture[64];
    /// <summary>
    /// The vertex samplers bound per slot.
    /// </summary>
    readonly MTLSamplerState[] _vertexSamplerState = new MTLSamplerState[64];
    /// <summary>
    /// The LOD bias of each vertex sampler slot's binding.
    /// </summary>
    readonly float[] _vertexSamplerBias = new float[64];
    /// <summary>
    /// The fragment textures bound per slot.
    /// </summary>
    readonly MTLTexture[] _fragmentTextureState = new MTLTexture[64];
    /// <summary>
    /// The fragment samplers bound per slot.
    /// </summary>
    readonly MTLSamplerState[] _fragmentSamplerState = new MTLSamplerState[64];
    /// <summary>
    /// The LOD bias of each fragment sampler slot's binding.
    /// </summary>
    readonly float[] _fragmentSamplerBias = new float[64];

    /// <summary>
    /// Made by the context (the frame's) or an upload batch (its own command buffer, its blit shared).
    /// </summary>
    internal MetalCommandList(MetalContext ctx, string name, MTLCommandBuffer command, MTLBlitCommandEncoder? sharedBlit = null)
    {
        this._ctx = ctx;
        this._name = name;
        this._command = command;

        if (sharedBlit.HasValue)
        {
            _blit = sharedBlit.Value;
            _ownsBlit = false;
        }
    }

    /// <inheritdoc/>
    public GpuStats Stats => _ctx.Frame.Stats;
    /// <inheritdoc/>
    public long Epoch => _epoch;
    /// <inheritdoc/>
    public object? BoundProgram { get; set; }

    /// <summary>
    /// Forgets the pipeline and program state; the encoder's buffer bindings stay valid across a segment label.
    /// </summary>
    public void Invalidate()
    {
        _lastPipeline = default;
        BoundProgram = null;
        _epoch++;
    }

    #region Rendering

    /// <summary>
    /// The Metal load action of an attachment's load op.
    /// </summary>
    static MTLLoadAction LoadAction(LoadOp load) => load switch
    {
        LoadOp.Clear => MTLLoadAction.Clear,
        LoadOp.DontCare => MTLLoadAction.DontCare,
        _ => MTLLoadAction.Load,
    };

    /// <inheritdoc/>
    public void BeginRendering(in RenderingDesc d, bool secondaries = false)
    {
        if (secondaries) throw new NotSupportedException("the Metal backend records no secondary command lists yet");

        EndCompute();
        EndBlit();
        _lastPipeline = default;   // a new encoder starts with no pipeline state (the filter would otherwise skip the set)
        var descriptor = new MTLRenderPassDescriptor(
            ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("MTLRenderPassDescriptor"), "renderPassDescriptor"));

        if (!d.Colour.IsNull)
        {
            // The attachments come back as struct copies, so a value-type property (the clear colour) has to be written back
            // into the array, or the pass clears to whatever the descriptor's memory held (docs/metal-backend.md).
            var attachments = descriptor.ColorAttachments;
            var attachment = attachments.Object(0);
            attachment.Texture = _ctx.ViewTexture(d.Colour.View);
            attachment.LoadAction = LoadAction(d.Colour.Load);
            attachment.StoreAction = _ctx.StoreActionFor(d.Colour.View, depth: false);
            attachment.ClearColor = new MTLClearColor
            {
                red = d.Colour.Clear.R, green = d.Colour.Clear.G, blue = d.Colour.Clear.B, alpha = d.Colour.Clear.A,
            };
            attachments.SetObject(attachment, 0);
        }

        if (!d.Extra.IsNull)
        {
            // The second colour target (the lit fragments' albedo for the GI resolve).
            var attachments = descriptor.ColorAttachments;
            var extra = attachments.Object(1);
            extra.Texture = _ctx.ViewTexture(d.Extra.View);
            extra.LoadAction = LoadAction(d.Extra.Load);
            extra.StoreAction = _ctx.StoreActionFor(d.Extra.View, depth: false);
            extra.ClearColor = new MTLClearColor { red = d.Extra.Clear.R, green = d.Extra.Clear.G, blue = d.Extra.Clear.B, alpha = d.Extra.Clear.A };
            attachments.SetObject(extra, 1);
        }

        if (!d.Depth.IsNull)
        {
            var depth = descriptor.DepthAttachment;
            depth.Texture = _ctx.ViewTexture(d.Depth.View);
            depth.LoadAction = LoadAction(d.Depth.Load);
            depth.StoreAction = _ctx.StoreActionFor(d.Depth.View, depth: true);
            depth.ClearDepth = d.Depth.DepthClear;
        }

        _render = _command.RenderCommandEncoder(descriptor);
        _encoderSerial++;
        Array.Clear(_vertexBufferState);
        Array.Clear(_vertexOffsetState);
        Array.Clear(_fragmentBufferState);
        Array.Clear(_fragmentOffsetState);
        Array.Clear(_vertexTextureState);
        Array.Clear(_vertexSamplerState);
        Array.Clear(_vertexSamplerBias);
        Array.Clear(_fragmentTextureState);
        Array.Clear(_fragmentSamplerState);
        Array.Clear(_fragmentSamplerBias);
        _pushSent.Clear();
        _depthAttachment = !d.Depth.IsNull;
        _renderDesc = d;
        _hasViewport = _hasScissor = false;
        _depthStateKnown = false;
        if (_name.Length > 0) _render.PushDebugGroup((NSString)_name);
    }

    /// <inheritdoc/>
    public void EndRendering()
    {
        if (_render.NativePtr == IntPtr.Zero)
            throw new InvalidOperationException("EndRendering without BeginRendering");

        _render.PopDebugGroup();
        _render.EndEncoding();
        _render = default;
    }

    /// <summary>
    /// Clears a depth area (a shadow tile, the depth between slices): a fullscreen triangle under the rect's scissor.
    /// </summary>
    public void ClearDepth(float value, PixelRect rect)
    {
        if (_render.NativePtr == IntPtr.Zero) throw new InvalidOperationException("ClearDepth outside a rendering");

        Clear(false, default, true, value, rect);
    }

    /// <summary>
    /// Clears the colour and / or the depth attachment over an area; the pass's pipeline state is forgotten, so the next draw binds again.
    /// </summary>
    public void Clear(bool colour, ClearColour colourValue, bool depth, float depthValue, PixelRect rect)
    {
        if (_render.NativePtr == IntPtr.Zero) throw new InvalidOperationException("Clear outside a rendering");

        _ctx.ClearAttachments(_render, _renderDesc, colour, colourValue, depth, depthValue, rect);
        _lastPipeline = default;
        _depthStateKnown = false;   // ClearAttachments writes a depth state directly
        if (_hasViewport)
        {
            _render.SetViewport(new MTLViewport
            {
                originX = _viewport.X, originY = _viewport.Y, width = _viewport.Width, height = _viewport.Height, znear = _viewport.MinDepth,
                zfar = _viewport.MaxDepth,
            });
        }

        if (_hasScissor)
        {
            _render.SetScissorRect(new MTLScissorRect
            {
                x = (ulong)_scissor.X, y = (ulong)_scissor.Y, width = _scissor.Width, height = _scissor.Height,
            });
        }
    }

    #endregion

    #region State

    /// <inheritdoc/>
    public void BindPipeline(GpuPipeline pipeline)
    {
        if (pipeline.Desc == _lastPipeline.Desc) return;
        if (_render.NativePtr == IntPtr.Zero) throw new InvalidOperationException("BindPipeline outside a rendering");

        _render.SetRenderPipelineState(_ctx.Pipelines.Backend(pipeline));
        _ctx.Pipelines.BindDepthClipMode(_render, pipeline.Desc.DepthClamp);
        _topology = pipeline.Desc.Topology;
        _lastPipeline = pipeline;
        Stats.PipelinesBound++;
    }

    /// <summary>
    /// Binds a compute pipeline (the encoder's <c>setComputePipelineState</c>).
    /// </summary>
    public void BindPipeline(GpuComputePipeline pipeline)
    {
        if (pipeline.Desc == _lastCompute.Desc) return;

        var encoder = ComputeEncoder();
        encoder.SetComputePipelineState(_ctx.Pipelines.Backend(pipeline));
        _computeProgram = pipeline.Desc.Program;
        _lastCompute = pipeline;
        Stats.PipelinesBound++;
    }

    /// <inheritdoc/>
    public void SetViewport(in PixelViewport v)
    {
        bool same = CacheEncoderState && _hasViewport && _viewport == v;
        _viewport = v;
        _hasViewport = true;
        if (!same)
        {
            _render.SetViewport(new MTLViewport
            {
                originX = v.X, originY = v.Y, width = v.Width, height = v.Height, znear = v.MinDepth, zfar = v.MaxDepth,
            });
        }
    }

    /// <inheritdoc/>
    public void SetScissor(in PixelRect r)
    {
        bool same = CacheEncoderState && _hasScissor && _scissor == r;
        _scissor = r;
        _hasScissor = true;
        if (!same)
            _render.SetScissorRect(new MTLScissorRect { x = (ulong)r.X, y = (ulong)r.Y, width = r.Width, height = r.Height });
    }

    /// <inheritdoc/>
    public void SetRaster(CullMode cull, FrontFaceDirection front)
    {
        _render.SetCullMode(MetalConventions.Cull(cull));
        _render.SetFrontFacingWinding(MetalConventions.Winding(front));
    }

    /// <summary>
    /// The front face alone (a draw that turns the winding round, the cull mode left as set).
    /// </summary>
    public void SetFrontFace(FrontFaceDirection front) => _render.SetFrontFacingWinding(MetalConventions.Winding(front));

    /// <inheritdoc/>
    public void SetDepth(bool test, bool write, DepthFunction op)
    {
        // With no depth attachment the state would be read nowhere; Metal's validation objects to setting it.
        if (!_depthAttachment) return;
        if (CacheEncoderState && _depthStateKnown && _depthTest == test && _depthWrite == write && _depthOp == op)
            return;

        _render.SetDepthStencilState(_ctx.DepthState(test, write, op));
        (_depthStateKnown, _depthTest, _depthWrite, _depthOp) = (true, test, write, op);
    }

    /// <inheritdoc/>
    public void SetDepthBias(bool enable, float constant, float slope) => _render.SetDepthBias(enable ? constant : 0, enable ? slope : 0, 0);

    #endregion

    #region Resources

    /// <summary>
    /// The index buffer bound for the next indexed draw.
    /// </summary>
    MTLBuffer _indexBuffer;
    /// <summary>
    /// The offset of the indices in <see cref="_indexBuffer"/>, in bytes.
    /// </summary>
    ulong _indexOffset;
    /// <summary>
    /// The index type of <see cref="_indexBuffer"/>.
    /// </summary>
    MTLIndexType _indexType;
    /// <summary>
    /// The push constant block of each program and stage, as written so far.
    /// </summary>
    readonly Dictionary<(int Program, GpuStage Stage), byte[]> _pushBlocks = [];
    /// <summary>
    /// The bytes last sent for each program and stage (an unchanged block is not sent again).
    /// </summary>
    readonly Dictionary<(int Program, GpuStage Stage), byte[]> _pushSent = [];

    /// <summary>
    /// Binds the vertex buffers of the locations from <paramref name="first"/>: the pipeline's slots, Vulkan's own numbers (the MSL's indices below
    /// <see cref="MetalShaders.VertexBlockBase"/>, where the blocks go).
    /// </summary>
    public unsafe void BindVertexBuffers(uint first, ReadOnlySpan<GpuBufferBinding> bindings)
    {
        if (_render.NativePtr == IntPtr.Zero)
            throw new InvalidOperationException("BindVertexBuffers outside a rendering");
        if (first + (uint)bindings.Length > MetalShaders.VertexBlockBase)
        {
            throw new NotSupportedException($"the Metal backend keeps the vertex buffers in the slots below {MetalShaders.VertexBlockBase} " +
                $"(docs/metal-backend.md), {first}..{first + bindings.Length - 1} asked");
        }

        for (int i = 0; i < bindings.Length; i++)
        {
            var (buffer, offset) = _ctx.Buffer(bindings[i]);
            BindVertexBuffer(first + (uint)i, buffer, offset);
        }
    }

    /// <summary>
    /// Binds a vertex buffer slot, or only its offset when the buffer is already bound there; skipped when both are the same.
    /// </summary>
    void BindVertexBuffer(uint index, MTLBuffer buffer, ulong offset)
    {
        if (index >= (uint)_vertexBufferState.Length)
            throw new NotSupportedException($"vertex buffer index {index} is outside the Metal buffer table");

        if (CacheEncoderState && _vertexBufferState[index].NativePtr == buffer.NativePtr)
        {
            if (_vertexOffsetState[index] == offset) return;

            // Same buffer, new slice: setVertexBufferOffset is the cheap path Xcode asks for.
            _render.SetVertexBufferOffset(offset, index);
            _vertexOffsetState[index] = offset;
            return;
        }

        _render.SetVertexBuffer(buffer, offset, index);
        _vertexBufferState[index] = buffer;
        _vertexOffsetState[index] = offset;
    }

    /// <summary>
    /// Binds a fragment buffer slot, or only its offset when the buffer is already bound there; skipped when both are the same.
    /// </summary>
    void BindFragmentBuffer(uint index, MTLBuffer buffer, ulong offset)
    {
        if (index >= (uint)_fragmentBufferState.Length)
            throw new NotSupportedException($"fragment buffer index {index} is outside the Metal buffer table");

        if (CacheEncoderState && _fragmentBufferState[index].NativePtr == buffer.NativePtr)
        {
            if (_fragmentOffsetState[index] == offset) return;

            _render.SetFragmentBufferOffset(offset, index);
            _fragmentOffsetState[index] = offset;
            return;
        }

        _render.SetFragmentBuffer(buffer, offset, index);
        _fragmentBufferState[index] = buffer;
        _fragmentOffsetState[index] = offset;
    }

    /// <summary>
    /// Binds the index buffer, held until the next indexed draw.
    /// </summary>
    public void BindIndexBuffer(GpuBufferBinding binding, IndexKind type)
    {
        (_indexBuffer, _indexOffset) = _ctx.Buffer(binding);
        _indexType = MetalConventions.IndexType(type);
    }

    /// <inheritdoc/>
    public void BindProgramResources(GpuProgram program, uint set, ReadOnlySpan<GpuBinding> bindings)
    {
        if (set != 0)
            throw new NotSupportedException($"the Metal backend binds set {set} only through CreateSet and BindSets");

        if (program.Model == ShaderModel.Compute)
        {
            _ = ComputeEncoder();
            BindSetArguments(_ctx.Shaders.Backend(program), set, bindings, computePass: true);
            Stats.DescriptorPushes++;
            return;
        }

        if (_render.NativePtr == IntPtr.Zero)
            throw new InvalidOperationException("BindProgramResources outside a rendering");

        if (_ctx.Shaders.Backend(program).ArgumentBuffers)
        {
            BindSetArguments(_ctx.Shaders.Backend(program), set, bindings);
            return;
        }

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

    /// <summary>
    /// Binds one slot of a program's resources onto its stage's table at the slot's flattened index.
    /// </summary>
    void BindSlot(bool isVertex, uint set, in GpuSlot slot, in GpuBinding binding)
    {
        // Metal's one binding table per stage: the slot's flattened index (MetalShaders.Flatten, the MSL's own numbers).
        uint index = MetalShaders.Flatten(slot.Kind, slot.Stages, set, slot.Binding);

        switch (binding.Kind)
        {
            case GpuBindingKind.Sampled:
                var (texture, sampler) = _ctx.Sampled(binding.Texture);
                float bias = MetalSamplers.LodBias(binding.Texture.Sampler);

                if (isVertex)
                {
                    if (!CacheEncoderState || _vertexTextureState[index].NativePtr != texture.NativePtr)
                    {
                        _render.SetVertexTexture(texture, index);
                        _vertexTextureState[index] = texture;
                    }

                    if (!CacheEncoderState || _vertexSamplerState[index].NativePtr != sampler.NativePtr || _vertexSamplerBias[index] != bias)
                    {
                        _render.SetVertexSamplerState(sampler, bias, 1000 + bias, index);
                        _vertexSamplerState[index] = sampler;
                        _vertexSamplerBias[index] = bias;
                    }
                }
                else
                {
                    if (!CacheEncoderState || _fragmentTextureState[index].NativePtr != texture.NativePtr)
                    {
                        _render.SetFragmentTexture(texture, index);
                        _fragmentTextureState[index] = texture;
                    }

                    if (!CacheEncoderState || _fragmentSamplerState[index].NativePtr != sampler.NativePtr || _fragmentSamplerBias[index] != bias)
                    {
                        _render.SetFragmentSamplerState(sampler, bias, 1000 + bias, index);
                        _fragmentSamplerState[index] = sampler;
                        _fragmentSamplerBias[index] = bias;
                    }
                }

                break;
            case GpuBindingKind.UniformBuffer or GpuBindingKind.StorageBuffer:
                var (buffer, offset) = _ctx.Buffer(binding.Buffer);
                if (isVertex) BindVertexBuffer(index, buffer, offset);
                else BindFragmentBuffer(index, buffer, offset);
                break;
            default:
                throw new NotSupportedException($"the Metal backend binds no storage images yet ({binding.Kind} at set {set}, binding {binding.Slot})");
        }
    }

    /// <inheritdoc/>
    public void BindSets(GpuProgram program, uint first, ReadOnlySpan<GpuSet> sets, ReadOnlySpan<uint> dynamicOffsets,
        GpuBindPoint point = GpuBindPoint.Graphics)
    {
        bool computePass = point == GpuBindPoint.Compute;
        if (computePass) _ = ComputeEncoder();
        else if (_render.NativePtr == IntPtr.Zero) throw new InvalidOperationException("BindSets outside a rendering");

        if (_ctx.Shaders.Backend(program).ArgumentBuffers)
        {
            int at = 0;

            for (int i = 0; i < sets.Length; i++)
            {
                var set = _ctx.SetAt(sets[i]);
                var bindings = new GpuBinding[set.Bindings.Length];

                for (int b = 0; b < bindings.Length; b++)
                {
                    var binding = set.Bindings[b];

                    if (set.Layout.Slots[b].OffsetAtBind)
                    {
                        binding = new GpuBinding(binding.Slot, binding.Kind, Buffer: new GpuBufferBinding(binding.Buffer.Buffer,
                            dynamicOffsets.IsEmpty ? 0 : dynamicOffsets[at++], binding.Buffer.Size));
                    }

                    bindings[b] = binding;
                }

                BindSetArguments(_ctx.Shaders.Backend(program), first + (uint)i, bindings, computePass);
            }

            Stats.DescriptorPushes++;
            return;
        }

        int used = 0;

        for (int i = 0; i < sets.Length; i++)
        {
            var set = _ctx.SetAt(sets[i]);
            uint index = first + (uint)i;
            int next = 0;

            foreach (var slot in set.Layout.Slots)
            {
                var binding = set.Bindings[next++].Buffer;

                // A slot whose offset comes at bind time takes the bind's dynamic offset (the default blocks' slices).
                if (slot.OffsetAtBind)
                    binding = new GpuBufferBinding(binding.Buffer, dynamicOffsets.IsEmpty ? 0 : dynamicOffsets[used++], binding.Size);

                var (buffer, offset) = _ctx.Buffer(binding);

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
        if (computePass)
        {
            BindSetStage(backend.ComputeSets, GpuStage.Compute, set, bindings, computePass: true);
            return;
        }

        BindSetStage(backend.VertexSets, GpuStage.Vertex, set, bindings);
        BindSetStage(backend.FragmentSets, GpuStage.Fragment, set, bindings);
    }

    /// <summary>
    /// One stage's set: the bindings' addresses at the positions their <c>[[id]]</c>s name in the stage's struct (Metal lays an argument buffer's
    /// members out by declaration, so the position is the entry, not the id).
    /// </summary>
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
                _ => throw new NotSupportedException(
                    $"the Metal backend puts no {binding.Kind} in an argument buffer set (set {set}, binding {binding.Slot})"),
            };
            int at = Array.IndexOf(members, MetalShaders.Flatten(slotKind, stage, set, binding.Slot));
            if (at < 0) continue;   // this stage's struct has no such member (its shader never reads it)

            if (binding.Kind is GpuBindingKind.UniformBuffer or GpuBindingKind.StorageBuffer)
            {
                if (binding.Buffer.Buffer.IsNull) continue;

                var (buffer, offset) = _ctx.Buffer(binding.Buffer);
                UseResource(buffer, BufferUsage(binding.Kind, computePass), computePass);
                entries[at] = buffer.GpuAddress + offset;
            }
            else
            {
                if (binding.Texture.IsNull) continue;

                var image = _ctx.ViewTexture(binding.Texture.View);
                var usage = binding.Kind == GpuBindingKind.StorageImage ? MTLResourceUsage.Read | MTLResourceUsage.Write : MTLResourceUsage.Read;
                UseResource(image, usage, computePass);
                entries[at] = image.GpuResourceID._impl;
            }

            any = true;
        }

        if (!any) return;

        // The MSL struct is padded to argument-buffer alignment (16); a one-member set needs 16 bytes, not 8.
        var size = Math.Max(16ul, (ulong)members.Length * 8 + 15 & ~15ul);

        // A slice of the frame's constants ring, not a fresh 16-byte MTLBuffer per bind (Xcode's late-buffer insight).
        var constants = _ctx.Frame.AllocateConstants(size, 16);
        fixed (ulong* from = entries) Buffer.MemoryCopy(from, (void*)constants.Pointer, (long)size, (long)size);

        var argumentBuffer = _ctx.BufferAt(constants.Handle);
        if (computePass) _compute.SetBuffer(argumentBuffer, constants.Offset, set);
        else if (stage == GpuStage.Vertex)
            BindVertexBuffer(MetalShaders.VertexBlockBase + set, argumentBuffer, constants.Offset);
        else BindFragmentBuffer(set, argumentBuffer, constants.Offset);
    }

    /// <summary>
    /// How the encoder is told a buffer of an argument buffer is used. A compute pass's storage buffers may be written (the engine's compute shaders
    /// declare which are <c>readonly</c>, but the argument buffer hides that from Metal): declared read-only, a buffer one dispatch writes and the next
    /// reads (the foliage cull's counts, offsets, fades and rows) gets no ordering between the encoders, and the reader runs on stale data.
    /// </summary>
    static MTLResourceUsage BufferUsage(GpuBindingKind kind, bool computePass) =>
        computePass && kind == GpuBindingKind.StorageBuffer ? MTLResourceUsage.Read | MTLResourceUsage.Write : MTLResourceUsage.Read;

    /// <summary>
    /// Tells the current encoder a resource the argument buffer holds; the compute encoder needs it for its writes too.
    /// </summary>
    void UseResource(MTLBuffer buffer, MTLResourceUsage usage, bool computePass)
    {
        if (computePass) _compute.UseResource(buffer, usage);
        else _render.UseResource(buffer, usage);
    }

    /// <summary>
    /// Declares a texture the argument buffer names as used by the open compute or render encoder.
    /// </summary>
    void UseResource(MTLTexture texture, MTLResourceUsage usage, bool computePass)
    {
        if (computePass) _compute.UseResource(texture, usage);
        else _render.UseResource(texture, usage);
    }

    /// <summary>
    /// Binds the bindless table: the slot's argument buffer at <see cref="MslTranslator.TableBinding"/> in both stages (the MSL's table set is moved
    /// there, so the set's number is only the layout's word for it).
    /// </summary>
    public void BindBindlessTable(GpuProgram program, uint set, GpuBindPoint point = GpuBindPoint.Graphics)
    {
        bool computePass = point == GpuBindPoint.Compute;
        if (computePass) _ = ComputeEncoder();
        else if (_render.NativePtr == IntPtr.Zero)
            throw new InvalidOperationException("BindBindlessTable outside a rendering");
        if (set != Frame.NativeShaders.BindlessSet)
            throw new InvalidOperationException($"the Metal backend's table is set {Frame.NativeShaders.BindlessSet} (asked for {set})");

        var backend = _ctx.Shaders.Backend(program);
        int slot = _ctx.Frame.Slot;

        if (computePass)
        {
            if (_ctx.BindlessTable.BufferFor(backend.ComputeTable, slot) is { } table)
                _compute.SetBuffer(table, 0, MslTranslator.TableBinding);
        }
        else
        {
            if (_ctx.BindlessTable.BufferFor(backend.VertexTable, slot) is { } vertex)
                BindVertexBuffer(MslTranslator.TableBinding, vertex, 0);
            if (_ctx.BindlessTable.BufferFor(backend.FragmentTable, slot) is { } fragment)
                BindFragmentBuffer(MslTranslator.TableBinding, fragment, 0);
        }

        var bindless = _ctx.BindlessTable;
        if (_residentSerial != _encoderSerial || _residentVersion != bindless.Version)
        {
            bindless.UseResources(this);
            (_residentSerial, _residentVersion) = (_encoderSerial, bindless.Version);
        }

        Stats.DescriptorPushes++;
    }

    /// <summary>
    /// The <c>useResources:count:usage:</c> selector.
    /// </summary>
    static readonly Selector UseResourcesSelector = new("useResources:count:usage:");

    /// <summary>
    /// <c>useResources:count:usage:</c> on an encoder: <paramref name="count"/> resources' native pointers, one usage for all.
    /// </summary>
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    static extern unsafe void UseResourcesMessage(IntPtr encoder, IntPtr selector, nint* resources, ulong count, ulong usage);

    /// <summary>
    /// Tells the open encoder, in one call, the textures the argument buffer's entries hold (the first <paramref name="count"/> native pointers of
    /// <paramref name="textures"/>): the shaders read them through the table, not through a binding the encoder could see.
    /// </summary>
    internal unsafe void UseResources(nint[] textures, int count)
    {
        var encoder = _render.NativePtr != IntPtr.Zero ? _render.NativePtr : _compute.NativePtr;
        if (count == 0 || encoder == IntPtr.Zero) return;

        fixed (nint* resources = textures)
        {
            UseResourcesMessage(encoder, UseResourcesSelector.SelPtr, resources, (ulong)count, (ulong)MTLResourceUsage.Read);
        }
    }

    /// <summary>
    /// Pushes a block of constants: the bytes both stages' encoders take inline at the stage's top index
    /// (<see cref="MslTranslator.PushConstantBinding"/>, the push blocks' own <c>[[buffer(n)]]</c>).
    /// </summary>
    public unsafe void PushConstants<T>(GpuProgram program, GpuStage stages, in T value, uint offset = 0) where T : unmanaged
    {
        // The MSL block is the SPIR-V one, rounded up to its 16-byte alignment; pushing only sizeof(T) fails Metal's length check
        // (the terrain's 24-byte block is 32 there). Vulkan's push constants keep the bytes a partial push does not name, so the
        // list keeps the stage's last block and sets the named range in it (every draw pushes its whole block, as the renderers do).
        int raw = sizeof(T);
        uint end = offset + (uint)raw;

        if (end > program.PushConstantBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(offset),
                $"{program.Name}: the push ends at {end}, the block is {program.PushConstantBytes} bytes");
        }

        int padded = (int)((Math.Max(program.PushConstantBytes, end) + 15u) & ~15u);
        byte* source = (byte*)Unsafe.AsPointer(ref Unsafe.AsRef(in value));
        if (stages.HasFlag(GpuStage.Compute))
            PushStage(GpuStage.Compute, program, source, raw, (int)offset, padded, compute: true);
        if (stages.HasFlag(GpuStage.Vertex))
            PushStage(GpuStage.Vertex, program, source, raw, (int)offset, padded, compute: false);
        if (stages.HasFlag(GpuStage.Fragment))
            PushStage(GpuStage.Fragment, program, source, raw, (int)offset, padded, compute: false);
    }

    /// <summary>
    /// Writes a push into the stage's block and sends the whole block with setBytes at <see cref="MslTranslator.PushConstantBinding"/> when it
    /// changed.
    /// </summary>
    unsafe void PushStage(GpuStage stage, GpuProgram program, byte* source, int raw, int offset, int padded, bool compute)
    {
        var key = (program.Index, stage);
        if (!_pushBlocks.TryGetValue(key, out var block) || block.Length < padded)
            _pushBlocks[key] = block = new byte[Math.Max(padded, 16)];
        if (!_pushSent.TryGetValue(key, out var sent) || sent.Length < block.Length)
            _pushSent[key] = sent = new byte[block.Length];

        fixed (byte* destination = block)
        {
            Buffer.MemoryCopy(source, destination + offset, block.Length - offset, raw);
            if (block.AsSpan(0, block.Length).SequenceEqual(sent.AsSpan(0, block.Length))) return;

            block.AsSpan(0, block.Length).CopyTo(sent);
        }

        fixed (byte* destination = sent)
        {
            if (compute)
                ComputeEncoder().SetBytes((nint)destination, (ulong)block.Length, MslTranslator.PushConstantBinding);
            else if (stage == GpuStage.Vertex)
                _render.SetVertexBytes((nint)destination, (ulong)block.Length, MslTranslator.PushConstantBinding);
            else _render.SetFragmentBytes((nint)destination, (ulong)block.Length, MslTranslator.PushConstantBinding);
        }
    }

    /// <summary>
    /// Writes a timestamp into a query slot (the frame's timings). Metal records none yet (<see cref="MetalTimestamps"/>).
    /// </summary>
    public void Timestamp(IGpuTimestamps arena, in QuerySlot slot, GpuStage stage = GpuStage.All) { }

    #endregion

    #region Draws

    /// <inheritdoc/>
    public void Draw(uint vertices, uint instances = 1, uint firstVertex = 0, uint firstInstance = 0)
    {
        // The grass draws a blade range by firstInstance; baseInstance is Metal's own (per-instance attributes fetch from it).
        if (firstInstance != 0)
            _render.DrawPrimitives(MetalConventions.Primitive(_topology), firstVertex, vertices, instances, firstInstance);
        else
            _render.DrawPrimitives(MetalConventions.Primitive(_topology), firstVertex, vertices, instances);

        Stats.Draws++;
    }

    /// <summary>
    /// Draws indexed vertices out of the bound index buffer.
    /// </summary>
    public unsafe void DrawIndexed(uint indices, uint instances = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
    {
        if (_indexBuffer.NativePtr == IntPtr.Zero)
            throw new InvalidOperationException("DrawIndexed with no index buffer bound");

        _render.DrawIndexedPrimitives(MetalConventions.Primitive(_topology), indices, _indexType, _indexBuffer,
            _indexOffset + (ulong)firstIndex * MetalConventions.IndexBytes(_indexType), instances, vertexOffset, firstInstance);
        Stats.Draws++;
    }

    /// <summary>
    /// Draws indexed vertices, the arguments in a buffer: one Metal indirect draw per command (Metal has no multi-draw).
    /// </summary>
    public void DrawIndexedIndirect(GpuBuffer args, ulong offset, uint count, uint stride = 20)
    {
        if (count == 0) return;
        if (_indexBuffer.NativePtr == IntPtr.Zero)
            throw new InvalidOperationException("DrawIndexedIndirect with no index buffer bound");

        var buffer = _ctx.BufferAt(args);
        for (uint i = 0; i < count; i++)
            _render.DrawIndexedPrimitives(MetalConventions.Primitive(_topology), _indexType, _indexBuffer, _indexOffset, buffer, offset + i * stride);

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
        if (_indexBuffer.NativePtr == IntPtr.Zero)
            throw new InvalidOperationException("DrawIndexedIndirectCount with no index buffer bound");

        var buffer = _ctx.BufferAt(args);
        for (uint i = 0; i < maxCount; i++)
            _render.DrawIndexedPrimitives(MetalConventions.Primitive(_topology), _indexType, _indexBuffer, _indexOffset, buffer, offset + i * stride);

        Stats.Draws += (int)maxCount;
    }

    /// <summary>
    /// Draws indirect, the count in another buffer: as <see cref="DrawIndexedIndirectCount"/>, the unused slots zero.
    /// </summary>
    public void DrawIndirectCount(GpuBuffer args, ulong offset, GpuBuffer countBuffer, ulong countOffset, uint maxCount, uint stride = 16)
    {
        if (maxCount == 0) return;

        var buffer = _ctx.BufferAt(args);
        for (uint i = 0; i < maxCount; i++)
            _render.DrawPrimitives(MetalConventions.Primitive(_topology), buffer, offset + i * stride);

        Stats.Draws += (int)maxCount;
    }

    /// <summary>
    /// Dispatches a compute pass: the arguments are threadgroups, as Vulkan's <c>vkCmdDispatch</c>'s; the shader's
    /// <c>local_size_x/y/z</c> (parsed from the GLSL) is the threadgroup.
    /// </summary>
    public void Dispatch(uint x, uint y = 1, uint z = 1)
    {
        var encoder = ComputeEncoder();
        if (_computeProgram.IsNull) throw new InvalidOperationException("Dispatch without a bound compute pipeline");

        var threads = _ctx.Shaders.Backend(_computeProgram).Threads;
        encoder.DispatchThreadgroups(new MTLSize { width = x, height = y, depth = z },
            new MTLSize { width = threads.X, height = threads.Y, depth = threads.Z });
        Stats.Draws++;
    }

    /// <summary>
    /// A new texture: Metal tracks no image layouts, so the context makes it and nothing is recorded.
    /// </summary>
    public GpuTexture CreateTexture(TextureDesc desc) => _ctx.CreateTexture(desc);

    #endregion

    #region Transfers

    /// <summary>
    /// Fills a range of a buffer with a value (the blit encoder's <c>fillBuffer</c>).
    /// </summary>
    public void FillBuffer(GpuBuffer b, ulong offset, ulong size, uint value)
    {
        // Metal fills with one byte; every caller so far clears (0).
        if (value != 0)
            throw new NotSupportedException($"the Metal backend fills buffers with zero only ({value:x8} asked)");

        var blit = OpenBlit();
        blit.FillBuffer(_ctx.BufferAt(b), new NSRange { location = offset, length = size }, 0);
    }

    /// <summary>
    /// Copies a range of a buffer into another (the blit encoder's <c>copyFromBuffer</c>).
    /// </summary>
    public void CopyBuffer(GpuBuffer src, GpuBuffer dst, in GpuBufferCopy region)
    {
        var blit = OpenBlit();
        blit.CopyFromBuffer(_ctx.BufferAt(src), region.SourceOffset, _ctx.BufferAt(dst), region.DestinationOffset, region.Size);
    }

    /// <summary>
    /// Copies a tightly packed level and layer of a buffer into a texture (the ocean's FFT results' upload).
    /// </summary>
    public void CopyBufferToImage(GpuBuffer src, ulong offset, GpuTexture dst, int level, int layer)
    {
        var desc = dst.Desc;
        int block = MetalConventions.BlockWidth(desc.Format);
        int texel = MetalConventions.TexelBytes(desc.Format);
        uint width = (uint)Math.Max(desc.Width >> level, 1), height = (uint)Math.Max(desc.Height >> level, 1);
        ulong bytesPerRow = (ulong)(((width + (uint)block - 1) / (uint)block) * (uint)texel);
        ulong blockRows = (height + (uint)block - 1) / (uint)block;
        var blit = OpenBlit();
        blit.CopyFromBuffer(_ctx.BufferAt(src), offset, bytesPerRow, bytesPerRow * blockRows,
            new MTLSize { width = width, height = height, depth = 1 },
            _ctx.TextureAt(dst), (ulong)layer, (ulong)level, new MTLOrigin());
    }

    /// <summary>
    /// Resolves a multisample texture into a single-sample one: a render pass whose only work is its resolve attachment.
    /// </summary>
    public void Resolve(GpuTexture src, GpuTexture dst)
    {
        if (_render.NativePtr != IntPtr.Zero) throw new InvalidOperationException("Resolve inside a rendering");

        EndCompute();
        EndBlit();
        var descriptor = new MTLRenderPassDescriptor(
            ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("MTLRenderPassDescriptor"), "renderPassDescriptor"));
        var attachments = descriptor.ColorAttachments;
        var attachment = attachments.Object(0);
        attachment.Texture = _ctx.TextureAt(src);
        attachment.ResolveTexture = _ctx.TextureAt(dst);
        attachment.ResolveLevel = 0;
        attachment.ResolveSlice = 0;
        attachment.LoadAction = MTLLoadAction.Load;
        attachment.StoreAction = MTLStoreAction.MultisampleResolve;
        attachments.SetObject(attachment, 0);
        var encoder = _command.RenderCommandEncoder(descriptor);
        encoder.EndEncoding();
    }

    /// <summary>
    /// Copies a texture into another: depth by a native copy, colour by a sampling pass that can scale (the impostor bake).
    /// </summary>
    public void Blit(GpuTexture src, GpuTexture dst, TextureMinFilter filter)
    {
        EndCompute();
        EndBlit();
        if (_render.NativePtr != IntPtr.Zero) EndRendering();

        _ctx.Blit(_command, src, dst, filter);
    }

    /// <summary>
    /// Copies a tightly packed region of a texture's level and layer into a buffer (the impostor bakes' readback).
    /// </summary>
    public void CopyTextureToBuffer(GpuTexture src, int level, int layer, in PixelRect region, GpuBuffer dst, ulong offset)
    {
        var blit = OpenBlit();
        ulong bytesPerRow = (ulong)region.Width * (ulong)MetalConventions.TexelBytes(src.Desc.Format);
        blit.CopyFromTexture(_ctx.TextureAt(src), (ulong)layer, (ulong)level,
            new MTLOrigin { x = (ulong)region.X, y = (ulong)region.Y, z = 0 },
            new MTLSize { width = (ulong)region.Width, height = (ulong)region.Height, depth = 1 },
            _ctx.BufferAt(dst), offset, bytesPerRow, bytesPerRow * region.Height);
    }

    /// <summary>
    /// Blits one level of a texture into another (Metal's blit when the sizes match, a sampling pass when they do not).
    /// </summary>
    public void BlitLevel(GpuTexture texture, int srcLevel, int dstLevel, TextureMinFilter filter)
    {
        if (_render.NativePtr != IntPtr.Zero) throw new InvalidOperationException("BlitLevel inside a rendering");

        EndCompute();
        EndBlit();
        _ctx.BlitLevel(_command, texture, srcLevel, dstLevel, filter);
    }

    /// <summary>
    /// Generates a texture's mip chain (as GL's GenerateMipmap made it: a blit's mip generation, filter linear).
    /// </summary>
    public void GenerateMips(GpuTexture t)
    {
        var b = OpenBlit();
        b.GenerateMipmaps(_ctx.TextureAt(t));
    }

    #endregion

    #region Sync

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

    /// <summary>
    /// Orders everything recorded before against everything after it: ending the compute and transfer encoders (Metal orders encoders in one command
    /// buffer, and Apple silicon tracks the hazards within one) is the seam.
    /// </summary>
    public void Order()
    {
        EndCompute();
        EndBlit();
    }

    #endregion

    #region Labels

    /// <inheritdoc/>
    public void BeginLabel(string name)
    {
        if (_render.NativePtr != IntPtr.Zero) _render.PushDebugGroup((NSString)name);
        else if (_blit.NativePtr != IntPtr.Zero) _blit.PushDebugGroup((NSString)name);
        else _command.PushDebugGroup((NSString)name);
    }

    /// <inheritdoc/>
    public void EndLabel()
    {
        if (_render.NativePtr != IntPtr.Zero) _render.PopDebugGroup();
        else if (_blit.NativePtr != IntPtr.Zero) _blit.PopDebugGroup();
        else _command.PopDebugGroup();
    }

    #endregion

    #region Blit encoder

    // The blit encoder (the mip chain's and the uploads').

    /// <summary>
    /// The blit encoder of this list's command buffer, opened when the first transfer needs it.
    /// </summary>
    internal MTLBlitCommandEncoder OpenBlit()
    {
        if (_blit.NativePtr != IntPtr.Zero) return _blit;
        if (_render.NativePtr != IntPtr.Zero) throw new InvalidOperationException("a transfer inside a rendering");

        EndCompute();
        _blit = _command.BlitCommandEncoder();
        _ownsBlit = true;
        return _blit;
    }

    /// <summary>
    /// Ends the blit encoder when this list owns it, and forgets it.
    /// </summary>
    internal void EndBlit()
    {
        if (_blit.NativePtr == IntPtr.Zero) return;
        if (_ownsBlit) _blit.EndEncoding();

        _blit = default;
        _ownsBlit = false;
    }

    /// <summary>
    /// The frame's list is reused every frame of its slot: the next frame's command buffer comes in here.
    /// </summary>
    internal void Rebind(MTLCommandBuffer next) => _command = next;

    /// <summary>
    /// Closes what is open (the upload batch's own encoder is the caller's).
    /// </summary>
    internal void Finish()
    {
        EndCompute();
        EndBlit();
        if (_render.NativePtr != IntPtr.Zero) EndRendering();
    }

    /// <summary>
    /// Opens the compute encoder, ending the transfers first (a command buffer records its encoders in order).
    /// </summary>
    internal MTLComputeCommandEncoder ComputeEncoder()
    {
        if (_compute.NativePtr != IntPtr.Zero) return _compute;

        EndBlit();
        if (_render.NativePtr != IntPtr.Zero) throw new InvalidOperationException("a compute pass inside a rendering");

        _compute = _command.ComputeCommandEncoder();
        _encoderSerial++;
        _pushSent.Clear();
        _lastCompute = default;
        return _compute;
    }

    /// <summary>
    /// Ends the compute encoder (the next encoder sees its writes).
    /// </summary>
    internal void EndCompute()
    {
        if (_compute.NativePtr == IntPtr.Zero) return;

        _compute.EndEncoding();
        _compute = default;
        _lastCompute = default;
    }

    #endregion
}
