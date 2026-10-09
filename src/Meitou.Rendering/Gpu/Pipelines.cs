using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu;

/// <summary>One vertex input: the shader location, its format, the stride of its buffer (0 = a constant, GL's disabled attribute) and the
/// input rate. Each location has its own binding (binding number = location), as in VkGl.</summary>
public readonly record struct VertexInput(uint Location, Format Format, uint Stride, bool PerInstance);

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
public readonly record struct AttachmentFormats(Format Colour, Format Depth, int Samples = 1, bool ShadingRate = false, Format Extra = Format.Undefined);

/// <summary>GL's blend: one factor pair for colour and alpha, add.</summary>
public readonly record struct BlendState(bool Enable, BlendFactor Src = BlendFactor.One, BlendFactor Dst = BlendFactor.Zero)
{
    public static readonly BlendState Off = new(false);
}

/// <summary>Everything a graphics pipeline is made from (docs/renderer-native.md 2.5); built at load, hashed once. Dynamic state (viewport,
/// scissor, cull mode, front face, depth test/write/compare, depth bias enable and values) is set while recording.</summary>
public readonly record struct GraphicsPipelineDesc(
    ShaderProgram Program, VertexLayout Vertex, PrimitiveTopology Topology, AttachmentFormats Targets,
    BlendState Blend, ColorComponentFlags ColourMask, Silk.NET.Vulkan.PolygonMode Polygon, bool AlphaToCoverage, bool DepthClamp, string Name = "")
{
    // The name is for labels and logs: two descs that differ only by name are the same pipeline.
    public bool Equals(GraphicsPipelineDesc other) => ReferenceEquals(Program, other.Program) && Vertex.Equals(other.Vertex) && Topology == other.Topology &&
        Targets == other.Targets && Blend == other.Blend && ColourMask == other.ColourMask && Polygon == other.Polygon &&
        AlphaToCoverage == other.AlphaToCoverage && DepthClamp == other.DepthClamp;
    public override int GetHashCode() => HashCode.Combine(Program, Vertex, Topology, Targets, Blend, ColourMask, Polygon, HashCode.Combine(AlphaToCoverage, DepthClamp));
}

public sealed class GraphicsPipeline(Pipeline handle, GraphicsPipelineDesc desc)
{
    public Pipeline Handle { get; } = handle;
    public GraphicsPipelineDesc Desc { get; } = desc;
    public PipelineLayout Layout => Desc.Program.Layout;
}

public readonly record struct ComputePipelineDesc(ShaderProgram Program, string Name = "")
{
    public bool Equals(ComputePipelineDesc other) => ReferenceEquals(Program, other.Program);
    public override int GetHashCode() => Program.GetHashCode();
}

public sealed class ComputePipeline(Pipeline handle, ComputePipelineDesc desc)
{
    public Pipeline Handle { get; } = handle;
    public ComputePipelineDesc Desc { get; } = desc;
    public PipelineLayout Layout => Desc.Program.Layout;
}

/// <summary>
/// Pipeline creation shared by VkGl and the native API: the fixed conventions that decide pixels (depth clip −1..1 through
/// VK_EXT_depth_clip_control, line polygon mode only with fillModeNonSolid, the dynamic state set, add blending with one factor pair).
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
        ReadOnlySpan<VertexInput> inputs, PrimitiveTopology topology, AttachmentFormats targets, BlendState blendState,
        ColorComponentFlags colourMask, bool polygonLine, bool alphaToCoverage, bool depthClamp, ColorComponentFlags extraMask = 0)
    {
        var entry = "main"u8;
        int n = inputs.Length;
        var vbind = stackalloc VertexInputBindingDescription[Math.Max(n, 1)];
        var vattr = stackalloc VertexInputAttributeDescription[Math.Max(n, 1)];
        for (int i = 0; i < n; i++)
        {
            var input = inputs[i];
            vbind[i] = new VertexInputBindingDescription(input.Location, input.Stride, input.PerInstance ? VertexInputRate.Instance : VertexInputRate.Vertex);
            vattr[i] = new VertexInputAttributeDescription(input.Location, input.Location, input.Format, 0);
        }
        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = (uint)n, PVertexBindingDescriptions = vbind,
            VertexAttributeDescriptionCount = (uint)n, PVertexAttributeDescriptions = vattr,
        };
        var assembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = topology };
        var clip = new PipelineViewportDepthClipControlCreateInfoEXT { SType = StructureType.PipelineViewportDepthClipControlCreateInfoExt, NegativeOneToOne = true };
        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1,
            PNext = device.HasDepthClipControl ? &clip : null,
        };
        var raster = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            DepthClampEnable = depthClamp,
            PolygonMode = polygonLine && device.FillModeNonSolid ? Silk.NET.Vulkan.PolygonMode.Line : Silk.NET.Vulkan.PolygonMode.Fill,
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
            SrcColorBlendFactor = blendState.Src, DstColorBlendFactor = blendState.Dst, ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = blendState.Src, DstAlphaBlendFactor = blendState.Dst, AlphaBlendOp = BlendOp.Add,
            ColorWriteMask = colourMask,
        };
        // The extra attachment (location 1) is never blended: written by the programs that declare it (extraMask), else left as it is.
        attachments[1] = new PipelineColorBlendAttachmentState { BlendEnable = false, ColorWriteMask = extraMask };
        uint colourCount = targets.Colour == Format.Undefined ? 0u : targets.Extra == Format.Undefined ? 1u : 2u;
        if (colourCount == 2 && blendState.Enable && !device.IndependentBlend) attachments[1] = attachments[0] with { ColorWriteMask = extraMask };
        var blend = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = colourCount, PAttachments = attachments,
        };
        fixed (DynamicState* dynamics = Dynamic)
        {
            var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = (uint)Dynamic.Length, PDynamicStates = dynamics };
            var colourFormats = stackalloc Format[2] { targets.Colour, targets.Extra };
            bool stencil = targets.Depth is Format.D24UnormS8Uint or Format.D32SfloatS8Uint;
            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = colourCount, PColorAttachmentFormats = colourFormats,
                DepthAttachmentFormat = targets.Depth, StencilAttachmentFormat = stencil ? targets.Depth : Format.Undefined,
            };
            // A rendering with a shading rate attachment (Formats.ShadingRate): the pipeline keeps its own rate (1x1) and takes the attachment's.
            var rate = new PipelineFragmentShadingRateStateCreateInfoKHR { SType = StructureType.PipelineFragmentShadingRateStateCreateInfoKhr, FragmentSize = new Extent2D(1, 1) };
            rate.CombinerOps[0] = FragmentShadingRateCombinerOpKHR.KeepKhr;
            rate.CombinerOps[1] = FragmentShadingRateCombinerOpKHR.ReplaceKhr;
            if (targets.ShadingRate) rendering.PNext = &rate;
            fixed (byte* name = entry)
            {
                var stages = stackalloc PipelineShaderStageCreateInfo[2];
                stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vertex, PName = name };
                stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragment, PName = name };
                var info = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    PNext = &rendering,
                    Flags = targets.ShadingRate ? PipelineCreateFlags.CreateRenderingFragmentShadingRateAttachmentBitKhr : 0,
                    StageCount = 2, PStages = stages,
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
public sealed unsafe class PipelineLibrary : IDisposable
{
    readonly VulkanDevice device;
    readonly Dictionary<GraphicsPipelineDesc, GraphicsPipeline> graphics = [];
    readonly Dictionary<ComputePipelineDesc, ComputePipeline> compute = [];
    readonly object gate = new();

    public PipelineLibrary(VulkanDevice device) => this.device = device;

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
                if (!graphics.TryAdd(d, p)) device.Vk.DestroyPipeline(device.Device, p.Handle, null);
        }));
    }

    readonly Dictionary<GraphicsPipelineDesc, GraphicsPipeline> variants = [];

    /// <summary>
    /// The pipeline of <paramref name="desc"/> with <paramref name="fragment"/> in place of its program's fragment module (the bench's counting
    /// variants, <c>--bench-tris</c>): kept apart from the normal pipelines, which nothing here touches. Safe from the recording threads.
    /// </summary>
    public GraphicsPipeline GetWithFragment(in GraphicsPipelineDesc desc, ShaderModule fragment)
    {
        lock (gate)
            if (variants.TryGetValue(desc, out var v)) return v;
        var made = Create(desc, fragment);
        lock (gate)
        {
            if (variants.TryGetValue(desc, out var raced)) { device.Vk.DestroyPipeline(device.Device, made.Handle, null); return raced; }
            variants[desc] = made;
        }
        return made;
    }

    public GraphicsPipeline Get(in GraphicsPipelineDesc desc)
    {
        lock (gate)
            if (graphics.TryGetValue(desc, out var p)) return p;
        var created = Create(desc);
        lock (gate)
        {
            LatePipelines++;
            graphics[desc] = created;
        }
        return created;
    }

    public ComputePipeline Get(in ComputePipelineDesc desc)
    {
        lock (gate)
        {
            if (compute.TryGetValue(desc, out var p)) return p;
            p = new ComputePipeline(PipelineFactory.CreateCompute(device, desc.Program.ComputeModule, desc.Program.Layout), desc);
            compute[desc] = p;
            return p;
        }
    }

    GraphicsPipeline Create(in GraphicsPipelineDesc d, ShaderModule fragment = default)
    {
        var p = d.Program;
        if (p.VertexModule.Handle == 0 || p.FragmentModule.Handle == 0) throw new ArgumentException($"{p.Name}: not a graphics program");
        var handle = PipelineFactory.CreateGraphics(device, p.VertexModule, fragment.Handle != 0 ? fragment : p.FragmentModule, p.Layout, d.Vertex.Inputs, d.Topology, d.Targets, d.Blend,
            d.ColourMask, d.Polygon == Silk.NET.Vulkan.PolygonMode.Line, d.AlphaToCoverage, d.DepthClamp,
            p.FragmentReflection?.Outputs.Any(o => o.Location == 1) == true ? DrawState.Rgba : 0);
        if (d.Name.Length > 0) device.SetName(ObjectType.Pipeline, handle.Handle, d.Name);
        return new GraphicsPipeline(handle, d);
    }

    /// <summary>Destroys the pipelines of <paramref name="program"/> after the frames in flight.</summary>
    public void Forget(ShaderProgram program)
    {
        List<Pipeline> doomed = [];
        lock (gate)
        {
            foreach (var k in graphics.Keys.Where(k => ReferenceEquals(k.Program, program)).ToList()) { doomed.Add(graphics[k].Handle); graphics.Remove(k); }
            foreach (var k in compute.Keys.Where(k => ReferenceEquals(k.Program, program)).ToList()) { doomed.Add(compute[k].Handle); compute.Remove(k); }
            foreach (var k in variants.Keys.Where(k => ReferenceEquals(k.Program, program)).ToList()) { doomed.Add(variants[k].Handle); variants.Remove(k); }
        }
        var (vk, dev) = (device.Vk, device.Device);
        device.Frames.DeferDelete(() => { foreach (var p in doomed) vk.DestroyPipeline(dev, p, null); });
    }

    public void Dispose()
    {
        foreach (var p in graphics.Values) device.Vk.DestroyPipeline(device.Device, p.Handle, null);
        foreach (var p in compute.Values) device.Vk.DestroyPipeline(device.Device, p.Handle, null);
        foreach (var p in variants.Values) device.Vk.DestroyPipeline(device.Device, p.Handle, null);
        variants.Clear();
        graphics.Clear();
        compute.Clear();
    }
}

