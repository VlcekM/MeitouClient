using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// One vertex input: the shader location, its format, the stride of its buffer (0 = a constant, GL's disabled attribute) and the
/// input rate. Each location has its own binding (binding number = location), as in VkGl.
/// </summary>
public readonly record struct VertexInput(uint Location, InternalFormat Format, uint Stride, bool PerInstance);

/// <summary>
/// The vertex inputs of a pipeline, by location. Compared by value; hashed once.
/// </summary>
public sealed class VertexLayout : IEquatable<VertexLayout>
{
    /// <summary>
    /// The hash of the inputs, made once at construction.
    /// </summary>
    readonly int _hash;

    /// <summary>
    /// A layout of the inputs, hashed once.
    /// </summary>
    public VertexLayout(params VertexInput[] inputs)
    {
        Inputs = inputs;
        var h = new HashCode();
        foreach (var i in inputs) h.Add(i);
        _hash = h.ToHashCode();
    }

    /// <summary>
    /// The vertex inputs by location.
    /// </summary>
    public VertexInput[] Inputs { get; }
    /// <summary>
    /// No vertex inputs.
    /// </summary>
    public static readonly VertexLayout Empty = new();

    /// <summary>
    /// Equal when the inputs are (compared after the hash).
    /// </summary>
    public bool Equals(VertexLayout? other) =>
        other is not null && (ReferenceEquals(this, other) || _hash == other._hash && Inputs.AsSpan().SequenceEqual(other.Inputs));
    public override bool Equals(object? obj) => obj is VertexLayout v && Equals(v);
    public override int GetHashCode() => _hash;
    public override string ToString() => string.Join(" ", Inputs.Select(i => $"{i.Location}:{i.Format}/{i.Stride}{(i.PerInstance ? "i" : "")}"));
}

/// <summary>
/// A pass's attachment formats: one colour attachment (Undefined = none), depth (Undefined = none), samples; <c>ShadingRate</c>: the rendering
/// has a fragment shading rate attachment (the pipeline is made for it); <paramref name="Extra"/>: a second colour attachment (location 1; the
/// GI resolve's surface target, docs/render-gi.md), written only by programs whose fragment shader declares that output.
/// </summary>
public readonly record struct AttachmentFormats(InternalFormat Colour, InternalFormat Depth, int Samples = 1, bool ShadingRate = false,
    InternalFormat Extra = InternalFormat.None);

/// <summary>
/// GL's blend: one factor pair for colour and alpha, add (or <paramref name="ColourOp"/> / <paramref name="AlphaOp"/>, e.g. min and max);
/// <paramref name="AlphaSrc"/> / <paramref name="AlphaDst"/>: alpha's own pair (GL's separate blend).
/// </summary>
public readonly record struct BlendState(bool Enable, BlendingFactor Src = BlendingFactor.One, BlendingFactor Dst = BlendingFactor.Zero,
    BlendOperation ColourOp = BlendOperation.Add, BlendOperation AlphaOp = BlendOperation.Add, BlendingFactor? AlphaSrc = null,
    BlendingFactor? AlphaDst = null)
{
    /// <summary>
    /// Blending off.
    /// </summary>
    public static readonly BlendState Off = new(false);
}

/// <summary>
/// Everything a graphics pipeline is made from (docs/renderer-native.md 2.5); built at load, hashed once. Dynamic state (viewport,
/// scissor, cull mode, front face, depth test/write/compare, depth bias enable and values) is set while recording.
/// </summary>
public readonly record struct GraphicsPipelineDesc(
    GpuProgram Program, VertexLayout Vertex, PrimitiveType Topology, AttachmentFormats Targets,
    BlendState Blend, ColorMask ColourMask, PolygonKind Polygon, bool AlphaToCoverage, bool DepthClamp, string Name = "")
{
    /// <summary>
    /// Equality without the name: the name is for labels and logs, so two descs that differ only by name are the same pipeline.
    /// </summary>
    public bool Equals(GraphicsPipelineDesc other) => Program == other.Program && Vertex.Equals(other.Vertex) && Topology == other.Topology &&
        Targets == other.Targets && Blend == other.Blend && ColourMask == other.ColourMask && Polygon == other.Polygon &&
        AlphaToCoverage == other.AlphaToCoverage && DepthClamp == other.DepthClamp;

    public override int GetHashCode() =>
        HashCode.Combine(Program, Vertex, Topology, Targets, Blend, ColourMask, Polygon, HashCode.Combine(AlphaToCoverage, DepthClamp));
}

/// <summary>
/// Everything a compute pipeline is made from: its program (the name is for labels only).
/// </summary>
public readonly record struct ComputePipelineDesc(GpuProgram Program, string Name = "")
{
    /// <summary>
    /// Equality by program only (the name is for labels).
    /// </summary>
    public bool Equals(ComputePipelineDesc other) => Program == other.Program;
    public override int GetHashCode() => Program.GetHashCode();
}

/// <summary>
/// Pipeline creation shared by VkGl and the native API: the fixed conventions that decide pixels (Vulkan's own 0..1 clip depth,
/// no depth clip control; line polygon mode only with fillModeNonSolid, the dynamic state set, add blending with one factor pair).
/// </summary>
public static unsafe class PipelineFactory
{
    /// <summary>
    /// The dynamic state of every graphics pipeline (VkGl's set).
    /// </summary>
    public static readonly DynamicState[] Dynamic =
    [
        DynamicState.Viewport, DynamicState.Scissor, DynamicState.DepthBias, DynamicState.CullMode, DynamicState.FrontFace,
        DynamicState.DepthTestEnable, DynamicState.DepthWriteEnable, DynamicState.DepthCompareOp, DynamicState.DepthBiasEnable,
    ];

    /// <summary>
    /// Creates a graphics pipeline. The stack arrays and the pinned names live in this frame: the create-info structs point at them
    /// only while the Vulkan call runs.
    /// </summary>
    public static Pipeline CreateGraphics(VulkanDevice device, ShaderModule vertex, ShaderModule fragment, PipelineLayout layout,
        ReadOnlySpan<VertexInput> inputs, PrimitiveType topology, AttachmentFormats targets, BlendState blendState,
        ColorMask colourMask, PolygonKind polygon, bool alphaToCoverage, bool depthClamp, ColorMask extraMask = 0,
        ShaderModule tessControl = default, ShaderModule tessEvaluation = default)
    {
        var entry = "main"u8;
        int n = inputs.Length;
        var vbind = stackalloc VertexInputBindingDescription[Math.Max(n, 1)];
        var vattr = stackalloc VertexInputAttributeDescription[Math.Max(n, 1)];
        FillVertexInputs(inputs, vbind, vattr);
        var vertexInput = VertexInputState(n, vbind, vattr);
        var assembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = GlConventions.Topology(topology),
        };
        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount = 1,
        };
        var raster = RasterizationState(device, depthClamp, polygon);
        var multisample = MultisampleState(targets, alphaToCoverage);
        var depthStencil = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo };
        var attachments = stackalloc PipelineColorBlendAttachmentState[2];
        uint colourCount = FillBlendAttachments(device, blendState, colourMask, extraMask, targets, attachments);
        var blend = ColourBlendState(colourCount, attachments);
        fixed (DynamicState* dynamics = Dynamic)
        {
            var dynamic = DynamicStateInfo(dynamics);
            var colourFormats = stackalloc Format[2]
            {
                GlConventions.VkFormatOrUndefined(targets.Colour), GlConventions.VkFormatOrUndefined(targets.Extra),
            };
            var rendering = RenderingInfo(targets, colourCount, colourFormats);
            var rate = ShadingRateState();
            if (targets.ShadingRate) rendering.PNext = &rate;
            fixed (byte* name = entry)
            {
                var stages = stackalloc PipelineShaderStageCreateInfo[4];
                bool tessellated = FillStages(vertex, fragment, tessControl, tessEvaluation, name, stages);
                var origin = new PipelineTessellationDomainOriginStateCreateInfo
                {
                    SType = StructureType.PipelineTessellationDomainOriginStateCreateInfo,
                    DomainOrigin = TessellationDomainOrigin.LowerLeft,
                };
                var tessellation = new PipelineTessellationStateCreateInfo
                {
                    SType = StructureType.PipelineTessellationStateCreateInfo,
                    PNext = &origin,
                    PatchControlPoints = 3,
                };
                var info = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    PNext = &rendering,
                    Flags = targets.ShadingRate ? PipelineCreateFlags.CreateRenderingFragmentShadingRateAttachmentBitKhr : 0,
                    StageCount = tessellated ? 4u : 2u,
                    PStages = stages,
                    PTessellationState = tessellated ? &tessellation : null,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &assembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &raster,
                    PMultisampleState = &multisample,
                    PDepthStencilState = &depthStencil,
                    PColorBlendState = &blend,
                    PDynamicState = &dynamic,
                    Layout = layout,
                };
                var result = device.Vk.CreateGraphicsPipelines(device.Device, device.PipelineCache, 1, &info, null, out var pipeline);
                VulkanException.Check(result, "vkCreateGraphicsPipelines");
                return pipeline;
            }
        }
    }

    /// <summary>
    /// Fills the vertex binding and attribute descriptions of the inputs (binding number = location).
    /// </summary>
    static void FillVertexInputs(ReadOnlySpan<VertexInput> inputs, VertexInputBindingDescription* vbind, VertexInputAttributeDescription* vattr)
    {
        for (int i = 0; i < inputs.Length; i++)
        {
            var input = inputs[i];
            var rate = input.PerInstance ? VertexInputRate.Instance : VertexInputRate.Vertex;
            vbind[i] = new VertexInputBindingDescription(input.Location, input.Stride, rate);
            vattr[i] = new VertexInputAttributeDescription(input.Location, input.Location, GlConventions.VkFormat(input.Format), 0);
        }
    }

    /// <summary>
    /// The vertex input state of <paramref name="n"/> bindings with one attribute each.
    /// </summary>
    static PipelineVertexInputStateCreateInfo VertexInputState(int n, VertexInputBindingDescription* vbind, VertexInputAttributeDescription* vattr)
    {
        return new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = (uint)n,
            PVertexBindingDescriptions = vbind,
            VertexAttributeDescriptionCount = (uint)n,
            PVertexAttributeDescriptions = vattr,
        };
    }

    /// <summary>
    /// The rasterization state: line polygons only where the device fills them (else <see cref="GlConventions.VkPolygon"/>'s), depth clamp as asked.
    /// </summary>
    static PipelineRasterizationStateCreateInfo RasterizationState(VulkanDevice device, bool depthClamp, PolygonKind polygon)
    {
        var mode = polygon == PolygonKind.Line && device.FillModeNonSolid ? PolygonMode.Line : GlConventions.VkPolygon(polygon);
        return new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            DepthClampEnable = depthClamp,
            PolygonMode = mode,
            LineWidth = 1,
        };
    }

    /// <summary>
    /// The multisample state: the targets' sample count and alpha to coverage.
    /// </summary>
    static PipelineMultisampleStateCreateInfo MultisampleState(AttachmentFormats targets, bool alphaToCoverage)
    {
        return new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = (SampleCountFlags)targets.Samples,
            AlphaToCoverageEnable = alphaToCoverage,
        };
    }

    /// <summary>
    /// Fills the colour attachments' blend state (the extra attachment is never blended) and returns the colour attachment count.
    /// </summary>
    static uint FillBlendAttachments(VulkanDevice device, BlendState blendState, ColorMask colourMask, ColorMask extraMask,
        AttachmentFormats targets, PipelineColorBlendAttachmentState* attachments)
    {
        attachments[0] = new PipelineColorBlendAttachmentState
        {
            BlendEnable = blendState.Enable,
            SrcColorBlendFactor = GlConventions.BlendFactor(blendState.Src),
            DstColorBlendFactor = GlConventions.BlendFactor(blendState.Dst),
            ColorBlendOp = GlConventions.VkBlendOp(blendState.ColourOp),
            SrcAlphaBlendFactor = GlConventions.BlendFactor(blendState.AlphaSrc ?? blendState.Src),
            DstAlphaBlendFactor = GlConventions.BlendFactor(blendState.AlphaDst ?? blendState.Dst),
            AlphaBlendOp = GlConventions.VkBlendOp(blendState.AlphaOp),
            ColorWriteMask = GlConventions.VkColorMask(colourMask),
        };

        // The extra attachment (location 1) is never blended: written by the programs that declare it (extraMask), else left as it is.
        attachments[1] = new PipelineColorBlendAttachmentState { BlendEnable = false, ColorWriteMask = GlConventions.VkColorMask(extraMask) };
        uint colourCount = targets.Colour == InternalFormat.None ? 0u : targets.Extra == InternalFormat.None ? 1u : 2u;
        if (colourCount == 2 && blendState.Enable && !device.IndependentBlend)
            attachments[1] = attachments[0] with { ColorWriteMask = GlConventions.VkColorMask(extraMask) };

        return colourCount;
    }

    /// <summary>
    /// The colour blend state over the filled attachments.
    /// </summary>
    static PipelineColorBlendStateCreateInfo ColourBlendState(uint colourCount, PipelineColorBlendAttachmentState* attachments)
    {
        return new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = colourCount,
            PAttachments = attachments,
        };
    }

    /// <summary>
    /// The dynamic state info over <see cref="Dynamic"/> (the states the command list sets).
    /// </summary>
    static PipelineDynamicStateCreateInfo DynamicStateInfo(DynamicState* dynamics)
    {
        return new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = (uint)Dynamic.Length,
            PDynamicStates = dynamics,
        };
    }

    /// <summary>
    /// The dynamic rendering formats: the colour attachments, the depth format, and stencil for the depth-stencil formats.
    /// </summary>
    static PipelineRenderingCreateInfo RenderingInfo(AttachmentFormats targets, uint colourCount, Format* colourFormats)
    {
        bool stencil = targets.Depth is InternalFormat.Depth24Stencil8 or InternalFormat.Depth32FStencil8;
        return new PipelineRenderingCreateInfo
        {
            SType = StructureType.PipelineRenderingCreateInfo,
            ColorAttachmentCount = colourCount,
            PColorAttachmentFormats = colourFormats,
            DepthAttachmentFormat = GlConventions.VkFormatOrUndefined(targets.Depth),
            StencilAttachmentFormat = stencil ? GlConventions.VkFormat(targets.Depth) : Format.Undefined,
        };
    }

    /// <summary>
    /// The fragment shading rate state for a rendering with a shading rate attachment.
    /// </summary>
    static PipelineFragmentShadingRateStateCreateInfoKHR ShadingRateState()
    {
        // A rendering with a shading rate attachment (Formats.ShadingRate): the pipeline keeps its own rate (1x1) and takes the attachment's.
        var rate = new PipelineFragmentShadingRateStateCreateInfoKHR
        {
            SType = StructureType.PipelineFragmentShadingRateStateCreateInfoKhr,
            FragmentSize = new Extent2D(1, 1),
        };
        rate.CombinerOps[0] = FragmentShadingRateCombinerOpKHR.KeepKhr;
        rate.CombinerOps[1] = FragmentShadingRateCombinerOpKHR.ReplaceKhr;
        return rate;
    }

    /// <summary>
    /// Fills the shader stages: vertex and fragment, and the tessellation pair when the program has an evaluation stage. Returns whether
    /// the pipeline is tessellated.
    /// </summary>
    static bool FillStages(ShaderModule vertex, ShaderModule fragment, ShaderModule tessControl, ShaderModule tessEvaluation, byte* name,
        PipelineShaderStageCreateInfo* stages)
    {
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = vertex,
            PName = name,
        };
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = fragment,
            PName = name,
        };

        // Tessellated (ShaderLibrary.NativeTessellated): patch lists of three control points, the domain's origin at the lower left as in GL (so the
        // evaluation stage's winding means what it means there).
        bool tessellated = tessEvaluation.Handle != 0;
        if (tessellated)
        {
            stages[2] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.TessellationControlBit,
                Module = tessControl,
                PName = name,
            };
            stages[3] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.TessellationEvaluationBit,
                Module = tessEvaluation,
                PName = name,
            };
        }

        return tessellated;
    }

    /// <summary>
    /// A compute pipeline of the module's <c>main</c> entry point and the layout.
    /// </summary>
    public static Pipeline CreateCompute(VulkanDevice device, ShaderModule module, PipelineLayout layout)
    {
        fixed (byte* name = "main"u8)
        {
            var stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = module,
                PName = name,
            };
            var info = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stage,
                Layout = layout,
            };
            var result = device.Vk.CreateComputePipelines(device.Device, device.PipelineCache, 1, &info, null, out var pipeline);
            VulkanException.Check(result, "vkCreateComputePipelines");
            return pipeline;
        }
    }
}

/// <summary>
/// Pipelines by description (docs/renderer-native.md 2.5). <see cref="Prepare"/> creates them at load, in parallel (one VkPipelineCache,
/// thread-safe); <see cref="Get"/> while drawing returns a prepared one, and a miss creates it synchronously and counts it in
/// <see cref="LatePipelines"/>, which should stay 0 after the warm-up frames.
/// </summary>
public sealed unsafe class PipelineLibrary : IGpuPipelines, IDisposable
{
    #region State

    /// <summary>
    /// The device the pipelines are made on.
    /// </summary>
    readonly VulkanDevice _device;
    /// <summary>
    /// The shader library the programs' modules and layouts come from.
    /// </summary>
    readonly ShaderLibrary _shaders;
    /// <summary>
    /// The graphics pipelines by description.
    /// </summary>
    readonly Dictionary<GraphicsPipelineDesc, Pipeline> _graphics = [];
    /// <summary>
    /// The compute pipelines by description.
    /// </summary>
    readonly Dictionary<ComputePipelineDesc, Pipeline> _compute = [];
    /// <summary>
    /// The bench's variants with a replaced fragment module, by description (<see cref="GetWithFragment"/>).
    /// </summary>
    readonly Dictionary<GraphicsPipelineDesc, Pipeline> _variants = [];
    /// <summary>
    /// Guards the three caches (the recording threads and the prepare tasks use them).
    /// </summary>
    readonly object _gate = new();

    #endregion

    #region Construction

    /// <summary>
    /// A library making pipelines on the device from <paramref name="shaders"/>' programs.
    /// </summary>
    public PipelineLibrary(VulkanDevice device, ShaderLibrary shaders)
    {
        this._device = device;
        this._shaders = shaders;
    }

    #endregion

    #region Prepare and get

    /// <summary>
    /// Pipelines created by <see cref="Get"/> because nothing prepared them.
    /// </summary>
    public int LatePipelines { get; private set; }

    /// <summary>
    /// The graphics and compute pipelines made so far.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate) return _graphics.Count + _compute.Count;
        }
    }

    /// <summary>
    /// Makes the pipelines of the descriptions not made yet, in parallel on the thread pool (a race keeps the first made).
    /// </summary>
    public Task Prepare(IEnumerable<GraphicsPipelineDesc> descs)
    {
        var todo = descs.Distinct().Where(d => { lock (_gate) return !_graphics.ContainsKey(d); }).ToArray();
        return Task.Run(() => Parallel.ForEach(todo, d =>
        {
            var p = Create(d);
            lock (_gate)
                if (!_graphics.TryAdd(d, p)) _device.Vk.DestroyPipeline(_device.Device, p, null);
        }));
    }

    /// <summary>
    /// The pipeline of <paramref name="desc"/> with <paramref name="fragment"/> in place of its program's fragment module (the bench's counting
    /// variants, <c>--bench-tris</c>): kept apart from the normal pipelines, which nothing here touches. Safe from the recording threads.
    /// </summary>
    public GpuPipeline GetWithFragment(in GraphicsPipelineDesc desc, ShaderModule fragment)
    {
        lock (_gate)
            if (_variants.TryGetValue(desc, out var v)) return new GpuPipeline(desc, v.Handle);
        var made = Create(desc, fragment);
        lock (_gate)
        {
            if (_variants.TryGetValue(desc, out var raced))
            {
                _device.Vk.DestroyPipeline(_device.Device, made, null);
                return new GpuPipeline(desc, raced.Handle);
            }

            _variants[desc] = made;
        }

        return new GpuPipeline(desc, made.Handle);
    }

    /// <inheritdoc/>
    public GpuPipeline Get(in GraphicsPipelineDesc desc)
    {
        lock (_gate)
            if (_graphics.TryGetValue(desc, out var p)) return new GpuPipeline(desc, p.Handle);
        var created = Create(desc);
        lock (_gate)
        {
            // Another thread may have made the same one meanwhile: keep the first, so every holder binds the same pipeline.
            if (_graphics.TryGetValue(desc, out var raced))
            {
                _device.Vk.DestroyPipeline(_device.Device, created, null);
                return new GpuPipeline(desc, raced.Handle);
            }

            LatePipelines++;
            _graphics[desc] = created;
        }

        return new GpuPipeline(desc, created.Handle);
    }

    /// <inheritdoc/>
    public GpuComputePipeline Get(in ComputePipelineDesc desc)
    {
        lock (_gate)
        {
            if (_compute.TryGetValue(desc, out var p)) return new GpuComputePipeline(desc, p.Handle);
            var vp = _shaders.Backend(desc.Program);
            p = PipelineFactory.CreateCompute(_device, vp.ComputeModule, vp.Layout);
            _compute[desc] = p;
            return new GpuComputePipeline(desc, p.Handle);
        }
    }

    #endregion

    #region Handles

    /// <summary>
    /// The backend's identity of a handle (the command list's Vulkan-only internals resolve through this): the one it carries, else
    /// looked up by its description (the recording threads call this, so under the lock).
    /// </summary>
    internal Pipeline Handle(in GpuPipeline pipeline)
    {
        if (pipeline.Native != 0) return new Pipeline(pipeline.Native);
        lock (_gate) return _graphics.TryGetValue(pipeline.Desc, out var p) ? p : _variants.TryGetValue(pipeline.Desc, out var v) ? v : default;
    }

    /// <summary>
    /// The backend's identity of a compute handle.
    /// </summary>
    internal Pipeline Handle(in GpuComputePipeline pipeline)
    {
        if (pipeline.Native != 0) return new Pipeline(pipeline.Native);
        lock (_gate) return _compute.TryGetValue(pipeline.Desc, out var p) ? p : default;
    }

    #endregion

    #region Create

    /// <summary>
    /// Makes a graphics pipeline of a description, with <paramref name="fragment"/> in place of the program's fragment module when given; names it
    /// after the description.
    /// </summary>
    Pipeline Create(in GraphicsPipelineDesc d, ShaderModule fragment = default)
    {
        var p = _shaders.Backend(d.Program);
        if (p.VertexModule.Handle == 0 || p.FragmentModule.Handle == 0) throw new ArgumentException($"{p.Name}: not a graphics program");
        var fragmentModule = fragment.Handle != 0 ? fragment : p.FragmentModule;
        var extraMask = p.FragmentReflection?.Outputs.Any(o => o.Location == 1) == true ? DrawState.Rgba : ColorMask.None;
        var handle = PipelineFactory.CreateGraphics(_device, p.VertexModule, fragmentModule, p.Layout, d.Vertex.Inputs, d.Topology, d.Targets,
            d.Blend, d.ColourMask, d.Polygon, d.AlphaToCoverage, d.DepthClamp, extraMask, p.TessControlModule, p.TessEvaluationModule);
        if (d.Name.Length > 0) _device.SetName(ObjectType.Pipeline, handle.Handle, d.Name);
        return handle;
    }

    #endregion

    #region Lifetime

    /// <summary>
    /// Destroys the pipelines of <paramref name="program"/> after the frames in flight.
    /// </summary>
    public void Forget(GpuProgram program)
    {
        List<Pipeline> doomed = [];
        lock (_gate)
        {
            foreach (var k in _graphics.Keys.Where(k => k.Program == program).ToList())
            {
                doomed.Add(_graphics[k]);
                _graphics.Remove(k);
            }

            foreach (var k in _compute.Keys.Where(k => k.Program == program).ToList())
            {
                doomed.Add(_compute[k]);
                _compute.Remove(k);
            }

            foreach (var k in _variants.Keys.Where(k => k.Program == program).ToList())
            {
                doomed.Add(_variants[k]);
                _variants.Remove(k);
            }
        }

        var (vk, dev) = (_device.Vk, _device.Device);
        _device.Frames.DeferDelete(() => { foreach (var p in doomed) vk.DestroyPipeline(dev, p, null); });
    }

    /// <summary>
    /// Destroys every pipeline at once, the variants included.
    /// </summary>
    public void Dispose()
    {
        foreach (var p in _graphics.Values) _device.Vk.DestroyPipeline(_device.Device, p, null);
        foreach (var p in _compute.Values) _device.Vk.DestroyPipeline(_device.Device, p, null);
        foreach (var p in _variants.Values) _device.Vk.DestroyPipeline(_device.Device, p, null);
        _variants.Clear();
        _graphics.Clear();
        _compute.Clear();
    }

    #endregion
}