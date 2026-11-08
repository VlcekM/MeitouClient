using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>One vertex input: the shader location, its format, the stride of its buffer (0 = a constant, GL's disabled attribute) and the
/// input rate. Each location has its own binding (binding number = location), as in VkGl.</summary>
public readonly record struct VertexInput(uint Location, InternalFormat Format, uint Stride, bool PerInstance);

/// <summary>The vertex inputs of a pipeline, by location. Compared by value; hashed once.</summary>
public sealed class VertexLayout : IEquatable<VertexLayout>
{
    readonly int hash;

    public VertexLayout(params VertexInput[] inputs)
    {
        Inputs = inputs;
        var h = new HashCode();
        foreach (var i in inputs) h.Add(i);
        hash = h.ToHashCode();
    }

    public VertexInput[] Inputs { get; }
    public static readonly VertexLayout Empty = new();

    public bool Equals(VertexLayout? other) => other is not null && (ReferenceEquals(this, other) || hash == other.hash && Inputs.AsSpan().SequenceEqual(other.Inputs));
    public override bool Equals(object? obj) => obj is VertexLayout v && Equals(v);
    public override int GetHashCode() => hash;
    public override string ToString() => string.Join(" ", Inputs.Select(i => $"{i.Location}:{i.Format}/{i.Stride}{(i.PerInstance ? "i" : "")}"));
}

/// <summary>A pass's attachment formats: one colour attachment (Undefined = none), depth (Undefined = none), samples; <c>ShadingRate</c>: the rendering has a fragment
/// shading rate attachment (the pipeline is made for it); <paramref name="Extra"/>: a second colour attachment (location 1; the GI resolve's surface target,
/// docs/render-gi.md), written only by programs whose fragment shader declares that output.</summary>
public readonly record struct AttachmentFormats(InternalFormat Colour, InternalFormat Depth, int Samples = 1, bool ShadingRate = false, InternalFormat Extra = InternalFormat.None);

/// <summary>GL's blend: one factor pair for colour and alpha, add (or <paramref name="ColourOp"/> / <paramref name="AlphaOp"/>, e.g. min and max); <paramref name="AlphaSrc"/> /
/// <paramref name="AlphaDst"/>: alpha's own pair (GL's separate blend).</summary>
public readonly record struct BlendState(bool Enable, BlendingFactor Src = BlendingFactor.One, BlendingFactor Dst = BlendingFactor.Zero, BlendOperation ColourOp = BlendOperation.Add, BlendOperation AlphaOp = BlendOperation.Add,
    BlendingFactor? AlphaSrc = null, BlendingFactor? AlphaDst = null)
{
    public static readonly BlendState Off = new(false);
}

/// <summary>Everything a graphics pipeline is made from (docs/renderer-native.md 2.5); built at load, hashed once. Dynamic state (viewport,
/// scissor, cull mode, front face, depth test/write/compare, depth bias enable and values) is set while recording.</summary>
public readonly record struct GraphicsPipelineDesc(
    GpuProgram Program, VertexLayout Vertex, PrimitiveType Topology, AttachmentFormats Targets,
    BlendState Blend, ColorMask ColourMask, PolygonKind Polygon, bool AlphaToCoverage, bool DepthClamp, string Name = "")
{
    // The name is for labels and logs: two descs that differ only by name are the same pipeline.
    public bool Equals(GraphicsPipelineDesc other) => Program == other.Program && Vertex.Equals(other.Vertex) && Topology == other.Topology &&
        Targets == other.Targets && Blend == other.Blend && ColourMask == other.ColourMask && Polygon == other.Polygon &&
        AlphaToCoverage == other.AlphaToCoverage && DepthClamp == other.DepthClamp;
    public override int GetHashCode() => HashCode.Combine(Program, Vertex, Topology, Targets, Blend, ColourMask, Polygon, HashCode.Combine(AlphaToCoverage, DepthClamp));
}

public readonly record struct ComputePipelineDesc(GpuProgram Program, string Name = "")
{
    public bool Equals(ComputePipelineDesc other) => Program == other.Program;
    public override int GetHashCode() => Program.GetHashCode();
}

/// <summary>
/// Pipeline creation shared by VkGl and the native API: the fixed conventions that decide pixels (Vulkan's own 0..1 clip depth,
/// no depth clip control; line polygon mode only with fillModeNonSolid, the dynamic state set, add blending with one factor pair).
/// </summary>
public static unsafe class PipelineFactory
{
    /// <summary>The dynamic state of every graphics pipeline (VkGl's set).</summary>
    public static readonly DynamicState[] Dynamic =
    [
        DynamicState.Viewport, DynamicState.Scissor, DynamicState.DepthBias, DynamicState.CullMode, DynamicState.FrontFace,
        DynamicState.DepthTestEnable, DynamicState.DepthWriteEnable, DynamicState.DepthCompareOp, DynamicState.DepthBiasEnable,
    ];

    public static Pipeline CreateGraphics(VulkanDevice device, ShaderModule vertex, ShaderModule fragment, PipelineLayout layout,
        ReadOnlySpan<VertexInput> inputs, PrimitiveType topology, AttachmentFormats targets, BlendState blendState,
        ColorMask colourMask, PolygonKind polygon, bool alphaToCoverage, bool depthClamp, ColorMask extraMask = 0, ShaderModule tessControl = default, ShaderModule tessEvaluation = default)
    {
        var entry = "main"u8;
        int n = inputs.Length;
        var vbind = stackalloc VertexInputBindingDescription[Math.Max(n, 1)];
        var vattr = stackalloc VertexInputAttributeDescription[Math.Max(n, 1)];
        for (int i = 0; i < n; i++)
        {
            var input = inputs[i];
            vbind[i] = new VertexInputBindingDescription(input.Location, input.Stride, input.PerInstance ? VertexInputRate.Instance : VertexInputRate.Vertex);
            vattr[i] = new VertexInputAttributeDescription(input.Location, input.Location, GlConventions.VkFormat(input.Format), 0);
        }
        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = (uint)n, PVertexBindingDescriptions = vbind,
            VertexAttributeDescriptionCount = (uint)n, PVertexAttributeDescriptions = vattr,
        };
        var assembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = GlConventions.Topology(topology) };
        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1,
        };
        var raster = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            DepthClampEnable = depthClamp,
            PolygonMode = polygon == PolygonKind.Line && device.FillModeNonSolid ? Silk.NET.Vulkan.PolygonMode.Line : GlConventions.VkPolygon(polygon),
            LineWidth = 1,
        };
        var multisample = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = (SampleCountFlags)targets.Samples,
            AlphaToCoverageEnable = alphaToCoverage,
        };
        var depthStencil = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo };
        var attachments = stackalloc PipelineColorBlendAttachmentState[2];
        attachments[0] = new PipelineColorBlendAttachmentState
        {
            BlendEnable = blendState.Enable,
            SrcColorBlendFactor = GlConventions.BlendFactor(blendState.Src), DstColorBlendFactor = GlConventions.BlendFactor(blendState.Dst), ColorBlendOp = GlConventions.VkBlendOp(blendState.ColourOp),
            SrcAlphaBlendFactor = GlConventions.BlendFactor(blendState.AlphaSrc ?? blendState.Src), DstAlphaBlendFactor = GlConventions.BlendFactor(blendState.AlphaDst ?? blendState.Dst), AlphaBlendOp = GlConventions.VkBlendOp(blendState.AlphaOp),
            ColorWriteMask = GlConventions.VkColorMask(colourMask),
        };
        // The extra attachment (location 1) is never blended: written by the programs that declare it (extraMask), else left as it is.
        attachments[1] = new PipelineColorBlendAttachmentState { BlendEnable = false, ColorWriteMask = GlConventions.VkColorMask(extraMask) };
        uint colourCount = targets.Colour == InternalFormat.None ? 0u : targets.Extra == InternalFormat.None ? 1u : 2u;
        if (colourCount == 2 && blendState.Enable && !device.IndependentBlend) attachments[1] = attachments[0] with { ColorWriteMask = GlConventions.VkColorMask(extraMask) };
        var blend = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = colourCount, PAttachments = attachments,
        };
        fixed (DynamicState* dynamics = Dynamic)
        {
            var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = (uint)Dynamic.Length, PDynamicStates = dynamics };
            var colourFormats = stackalloc Format[2] { GlConventions.VkFormatOrUndefined(targets.Colour), GlConventions.VkFormatOrUndefined(targets.Extra) };
            bool stencil = targets.Depth is InternalFormat.Depth24Stencil8 or InternalFormat.Depth32fStencil8;
            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = colourCount, PColorAttachmentFormats = colourFormats,
                DepthAttachmentFormat = GlConventions.VkFormatOrUndefined(targets.Depth), StencilAttachmentFormat = stencil ? GlConventions.VkFormat(targets.Depth) : Format.Undefined,
            };
            // A rendering with a shading rate attachment (Formats.ShadingRate): the pipeline keeps its own rate (1x1) and takes the attachment's.
            var rate = new PipelineFragmentShadingRateStateCreateInfoKHR { SType = StructureType.PipelineFragmentShadingRateStateCreateInfoKhr, FragmentSize = new Extent2D(1, 1) };
            rate.CombinerOps[0] = FragmentShadingRateCombinerOpKHR.KeepKhr;
            rate.CombinerOps[1] = FragmentShadingRateCombinerOpKHR.ReplaceKhr;
            if (targets.ShadingRate) rendering.PNext = &rate;
            fixed (byte* name = entry)
            {
                var stages = stackalloc PipelineShaderStageCreateInfo[4];
                stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vertex, PName = name };
                stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragment, PName = name };
                // Tessellated (ShaderLibrary.NativeTessellated): patch lists of three control points, the domain's origin at the lower left as in GL (so the
                // evaluation stage's winding means what it means there).
                bool tessellated = tessEvaluation.Handle != 0;
                if (tessellated)
                {
                    stages[2] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.TessellationControlBit, Module = tessControl, PName = name };
                    stages[3] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.TessellationEvaluationBit, Module = tessEvaluation, PName = name };
                }
                var origin = new PipelineTessellationDomainOriginStateCreateInfo { SType = StructureType.PipelineTessellationDomainOriginStateCreateInfo, DomainOrigin = TessellationDomainOrigin.LowerLeft };
                var tessellation = new PipelineTessellationStateCreateInfo { SType = StructureType.PipelineTessellationStateCreateInfo, PNext = &origin, PatchControlPoints = 3 };
                var info = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    PNext = &rendering,
                    Flags = targets.ShadingRate ? PipelineCreateFlags.CreateRenderingFragmentShadingRateAttachmentBitKhr : 0,
                    StageCount = tessellated ? 4u : 2u, PStages = stages, PTessellationState = tessellated ? &tessellation : null,
                    PVertexInputState = &vertexInput, PInputAssemblyState = &assembly, PViewportState = &viewportState,
                    PRasterizationState = &raster, PMultisampleState = &multisample, PDepthStencilState = &depthStencil,
                    PColorBlendState = &blend, PDynamicState = &dynamic, Layout = layout,
                };
                VulkanException.Check(device.Vk.CreateGraphicsPipelines(device.Device, device.PipelineCache, 1, &info, null, out var pipeline), "vkCreateGraphicsPipelines");
                return pipeline;
            }
        }
    }

    public static Pipeline CreateCompute(VulkanDevice device, ShaderModule module, PipelineLayout layout)
    {
        fixed (byte* name = "main"u8)
        {
            var info = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = module, PName = name },
                Layout = layout,
            };
            VulkanException.Check(device.Vk.CreateComputePipelines(device.Device, device.PipelineCache, 1, &info, null, out var pipeline), "vkCreateComputePipelines");
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
    readonly VulkanDevice device;
    readonly ShaderLibrary shaders;
    readonly Dictionary<GraphicsPipelineDesc, Pipeline> graphics = [];
    readonly Dictionary<ComputePipelineDesc, Pipeline> compute = [];
    readonly object gate = new();

    public PipelineLibrary(VulkanDevice device, ShaderLibrary shaders)
    {
        this.device = device;
        this.shaders = shaders;
    }

    /// <summary>Pipelines created by <see cref="Get"/> because nothing prepared them.</summary>
    public int LatePipelines { get; private set; }
    public int Count { get { lock (gate) return graphics.Count + compute.Count; } }

    public Task Prepare(IEnumerable<GraphicsPipelineDesc> descs)
    {
        var todo = descs.Distinct().Where(d => { lock (gate) return !graphics.ContainsKey(d); }).ToArray();
        return Task.Run(() => Parallel.ForEach(todo, d =>
        {
            var p = Create(d);
            lock (gate)
                if (!graphics.TryAdd(d, p)) device.Vk.DestroyPipeline(device.Device, p, null);
        }));
    }

    readonly Dictionary<GraphicsPipelineDesc, Pipeline> variants = [];

    /// <summary>
    /// The pipeline of <paramref name="desc"/> with <paramref name="fragment"/> in place of its program's fragment module (the bench's counting
    /// variants, <c>--bench-tris</c>): kept apart from the normal pipelines, which nothing here touches. Safe from the recording threads.
    /// </summary>
    public GpuPipeline GetWithFragment(in GraphicsPipelineDesc desc, ShaderModule fragment)
    {
        lock (gate)
            if (variants.TryGetValue(desc, out var v)) return new GpuPipeline(desc, v.Handle);
        var made = Create(desc, fragment);
        lock (gate)
        {
            if (variants.TryGetValue(desc, out var raced)) { device.Vk.DestroyPipeline(device.Device, made, null); return new GpuPipeline(desc, raced.Handle); }
            variants[desc] = made;
        }
        return new GpuPipeline(desc, made.Handle);
    }

    public GpuPipeline Get(in GraphicsPipelineDesc desc)
    {
        lock (gate)
            if (graphics.TryGetValue(desc, out var p)) return new GpuPipeline(desc, p.Handle);
        var created = Create(desc);
        lock (gate)
        {
            // Another thread may have made the same one meanwhile: keep the first, so every holder binds the same pipeline.
            if (graphics.TryGetValue(desc, out var raced)) { device.Vk.DestroyPipeline(device.Device, created, null); return new GpuPipeline(desc, raced.Handle); }
            LatePipelines++;
            graphics[desc] = created;
        }
        return new GpuPipeline(desc, created.Handle);
    }

    public GpuComputePipeline Get(in ComputePipelineDesc desc)
    {
        lock (gate)
        {
            if (compute.TryGetValue(desc, out var p)) return new GpuComputePipeline(desc, p.Handle);
            { var vp = shaders.Backend(desc.Program); p = PipelineFactory.CreateCompute(device, vp.ComputeModule, vp.Layout); }
            compute[desc] = p;
            return new GpuComputePipeline(desc, p.Handle);
        }
    }

    /// <summary>The backend's identity of a handle (the command list's Vulkan-only internals resolve through this): the one it carries, else
    /// looked up by its description (the recording threads call this, so under the lock).</summary>
    internal Pipeline Handle(in GpuPipeline pipeline)
    {
        if (pipeline.Native != 0) return new Pipeline(pipeline.Native);
        lock (gate) return graphics.TryGetValue(pipeline.Desc, out var p) ? p : variants.TryGetValue(pipeline.Desc, out var v) ? v : default;
    }

    /// <summary>The backend's identity of a compute handle.</summary>
    internal Pipeline Handle(in GpuComputePipeline pipeline)
    {
        if (pipeline.Native != 0) return new Pipeline(pipeline.Native);
        lock (gate) return compute.TryGetValue(pipeline.Desc, out var p) ? p : default;
    }

    Pipeline Create(in GraphicsPipelineDesc d, ShaderModule fragment = default)
    {
        var p = shaders.Backend(d.Program);
        if (p.VertexModule.Handle == 0 || p.FragmentModule.Handle == 0) throw new ArgumentException($"{p.Name}: not a graphics program");
        var handle = PipelineFactory.CreateGraphics(device, p.VertexModule, fragment.Handle != 0 ? fragment : p.FragmentModule, p.Layout, d.Vertex.Inputs, d.Topology, d.Targets, d.Blend,
            d.ColourMask, d.Polygon, d.AlphaToCoverage, d.DepthClamp,
            p.FragmentReflection?.Outputs.Any(o => o.Location == 1) == true ? DrawState.Rgba : ColorMask.None, p.TessControlModule, p.TessEvaluationModule);
        if (d.Name.Length > 0) device.SetName(ObjectType.Pipeline, handle.Handle, d.Name);
        return handle;
    }

    /// <summary>Destroys the pipelines of <paramref name="program"/> after the frames in flight.</summary>
    public void Forget(GpuProgram program)
    {
        List<Pipeline> doomed = [];
        lock (gate)
        {
            foreach (var k in graphics.Keys.Where(k => k.Program == program).ToList()) { doomed.Add(graphics[k]); graphics.Remove(k); }
            foreach (var k in compute.Keys.Where(k => k.Program == program).ToList()) { doomed.Add(compute[k]); compute.Remove(k); }
            foreach (var k in variants.Keys.Where(k => k.Program == program).ToList()) { doomed.Add(variants[k]); variants.Remove(k); }
        }
        var (vk, dev) = (device.Vk, device.Device);
        device.Frames.DeferDelete(() => { foreach (var p in doomed) vk.DestroyPipeline(dev, p, null); });
    }

    public void Dispose()
    {
        foreach (var p in graphics.Values) device.Vk.DestroyPipeline(device.Device, p, null);
        foreach (var p in compute.Values) device.Vk.DestroyPipeline(device.Device, p, null);
        foreach (var p in variants.Values) device.Vk.DestroyPipeline(device.Device, p, null);
        variants.Clear();
        graphics.Clear();
        compute.Clear();
    }
}

