using System.Diagnostics;
using Meitou.Game.Rendering.Gpu;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// The pipelines by description, on Metal (docs/resource-layer.md): the description is the identity, and the
/// backend's object is an <c>MTLRenderPipelineState</c> made on first use. What is dynamic state on Vulkan
/// (viewport, scissor, cull, winding, depth test/write/compare, depth bias, fill mode) is the encoder's on
/// Metal; what is fixed state is the pipeline's: blend, colour mask, alpha to coverage, depth clamp, samples.
/// Compute pipelines are <c>MTLComputePipelineState</c>s, cached by the same description.
/// </summary>
public sealed class MetalPipelines : IGpuPipelines
{
    /// <summary>
    /// The device the pipeline states are made on.
    /// </summary>
    readonly MTLDevice _device;
    /// <summary>
    /// The programs the pipelines' functions come from.
    /// </summary>
    readonly MetalShaders _shaders;
    /// <summary>
    /// The made render pipeline states, by description.
    /// </summary>
    readonly Dictionary<GraphicsPipelineDesc, MTLRenderPipelineState> _graphics = [];
    /// <summary>
    /// The made compute pipeline states, by description.
    /// </summary>
    readonly Dictionary<ComputePipelineDesc, MTLComputePipelineState> _computes = [];

    /// <summary>
    /// The render pipeline states made so far: Metal makes every one on its first use, nothing ahead, as the Vulkan library's
    /// <c>LatePipelines</c> counts (the play benchmark's <c>late_pipelines</c> is a frame's increase).
    /// </summary>
    public int LatePipelines { get; private set; }

    /// <summary>
    /// <c>MEITOU_METAL_TRACE</c>: every pipeline state built and every library compiled (<see cref="MetalStage"/>) is logged with the time Metal took,
    /// so a hitch in a frame names its shader.
    /// </summary>
    internal static readonly bool Trace = Environment.GetEnvironmentVariable("MEITOU_METAL_TRACE") is "1" or "true";

    /// <summary>
    /// An empty cache on <paramref name="device"/> over <paramref name="shaders"/>.
    /// </summary>
    public MetalPipelines(MTLDevice device, MetalShaders shaders)
    {
        this._device = device;
        this._shaders = shaders;
    }

    /// <summary>
    /// The backend's object of a pipeline handle.
    /// </summary>
    internal MTLRenderPipelineState Backend(in GpuPipeline pipeline) => _graphics[pipeline.Desc];

    /// <summary>
    /// The backend's object of a compute pipeline handle.
    /// </summary>
    internal MTLComputePipelineState Backend(in GpuComputePipeline pipeline) => _computes[pipeline.Desc];

    /// <summary>
    /// The render pipeline for <paramref name="desc"/>, its state made on first use (blend, colour mask, attachments, vertex inputs); throws when
    /// Metal rejects it.
    /// </summary>
    public GpuPipeline Get(in GraphicsPipelineDesc desc)
    {
        if (_graphics.TryGetValue(desc, out var made)) return new GpuPipeline(desc);
        var program = _shaders.Backend(desc.Program);
        var d = new MTLRenderPipelineDescriptor(new ObjectiveCClass(ObjectiveC.objc_getClass("MTLRenderPipelineDescriptor")).AllocInit());
        d.VertexFunction = program.VertexFunction;
        d.FragmentFunction = program.FragmentFunction;
        d.RasterSampleCount = (ulong)Math.Max(1, desc.Targets.Samples);
        d.IsAlphaToCoverageEnabled = desc.AlphaToCoverage;

        // A depth-only pass (the shadow casters) has no colour attachment at slot 0.
        if (desc.Targets.Colour != InternalFormat.None)
        {
            var attachment = d.ColorAttachments.Object(0);
            attachment.PixelFormat = MetalConventions.Pixel(desc.Targets.Colour);
            attachment.IsBlendingEnabled = desc.Blend.Enable;
            attachment.SourceRGBBlendFactor = MetalConventions.BlendFactor(desc.Blend.Src);
            attachment.SourceAlphaBlendFactor = MetalConventions.BlendFactor(desc.Blend.AlphaSrc ?? desc.Blend.Src);
            attachment.DestinationRGBBlendFactor = MetalConventions.BlendFactor(desc.Blend.Dst);
            attachment.DestinationAlphaBlendFactor = MetalConventions.BlendFactor(desc.Blend.AlphaDst ?? desc.Blend.Dst);
            attachment.RgbBlendOperation = MetalConventions.BlendOp(desc.Blend.ColourOp);
            attachment.AlphaBlendOperation = MetalConventions.BlendOp(desc.Blend.AlphaOp);
            attachment.WriteMask = WithChannel(desc.ColourMask.HasFlag(ColorMask.R), attachment.WriteMask, MTLColorWriteMask.Red);
            attachment.WriteMask = WithChannel(desc.ColourMask.HasFlag(ColorMask.G), attachment.WriteMask, MTLColorWriteMask.Green);
            attachment.WriteMask = WithChannel(desc.ColourMask.HasFlag(ColorMask.B), attachment.WriteMask, MTLColorWriteMask.Blue);
            attachment.WriteMask = WithChannel(desc.ColourMask.HasFlag(ColorMask.A), attachment.WriteMask, MTLColorWriteMask.Alpha);
            d.ColorAttachments.SetObject(attachment, 0);
        }
        if (desc.Targets.Extra != InternalFormat.None)
        {
            var extra = d.ColorAttachments.Object(1);
            extra.PixelFormat = MetalConventions.Pixel(desc.Targets.Extra);
            extra.WriteMask = MTLColorWriteMask.All;
            d.ColorAttachments.SetObject(extra, 1);
        }

        if (desc.Targets.Depth != InternalFormat.None) d.DepthAttachmentPixelFormat = MetalConventions.Pixel(desc.Targets.Depth);
        if (desc.Vertex.Inputs.Length > 0) d.VertexDescriptor = VertexDescriptor(desc.Vertex);
        NSError error = default;
        long started = Stopwatch.GetTimestamp();
        var state = _device.NewRenderPipelineState(d, ref error);
        if (error.NativePtr != IntPtr.Zero)
            throw new InvalidOperationException($"{desc.Program.Name}: the pipeline '{desc.Name}' failed: {error.LocalizedDescription}");
        _graphics[desc] = state;
        LatePipelines++;
        if (Trace)
        {
            Console.WriteLine($"metal     pipeline #{LatePipelines} {desc.Program.Name} '{desc.Name}' built in " +
                $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0} ms");
        }
        return new GpuPipeline(desc);
    }

    /// <summary>
    /// <paramref name="mask"/> with <paramref name="channel"/> written when <paramref name="on"/>, masked off otherwise.
    /// </summary>
    static MTLColorWriteMask WithChannel(bool on, MTLColorWriteMask mask, MTLColorWriteMask channel) => on ? mask | channel : mask & ~channel;

    /// <summary>
    /// The compute pipeline for <paramref name="desc"/>, its state made on first use; throws when the program is not a compute program or Metal
    /// rejects it.
    /// </summary>
    public GpuComputePipeline Get(in ComputePipelineDesc desc)
    {
        if (_computes.ContainsKey(desc)) return new GpuComputePipeline(desc);
        var program = _shaders.Backend(desc.Program);
        if (!program.IsCompute) throw new InvalidOperationException($"{desc.Program.Name}: not a compute program");
        NSError error = default;
        var state = _device.NewComputePipelineState(program.ComputeFunction, ref error);
        if (error.NativePtr != IntPtr.Zero)
            throw new InvalidOperationException($"{desc.Program.Name}: the compute pipeline '{desc.Name}' failed: {error.LocalizedDescription}");
        _computes[desc] = state;
        return new GpuComputePipeline(desc);
    }

    /// <summary>
    /// Drops a program's cached pipelines (the program is going away).
    /// </summary>
    public void Forget(GpuProgram program)
    {
        foreach (var (desc, _) in _graphics)
            if (desc.Program.Index == program.Index) _graphics.Remove(desc);
        foreach (var (desc, _) in _computes)
            if (desc.Program.Index == program.Index) _computes.Remove(desc);
    }

    /// <summary>
    /// The pipeline's depth clip mode is the encoder's on Metal (dynamic per draw, like the depth test): set when the pipeline binds.
    /// </summary>
    internal void BindDepthClipMode(MTLRenderCommandEncoder encoder, bool depthClamp) =>
        encoder.SetDepthClipMode(depthClamp ? MTLDepthClipMode.Clamp : MTLDepthClipMode.Clip);

    /// <summary>
    /// The vertex inputs of a pipeline: each location its own binding (binding = location, as VkGl), stride and step per binding.
    /// </summary>
    static MTLVertexDescriptor VertexDescriptor(VertexLayout vertex)
    {
        var descriptor = new MTLVertexDescriptor();
        foreach (var input in vertex.Inputs)
        {
            var attribute = descriptor.Attributes.Object(input.Location);
            attribute.Format = MetalConventions.VertexFormat(input.Format);
            attribute.Offset = 0;
            attribute.BufferIndex = input.Location;
            descriptor.Attributes.SetObject(attribute, input.Location);
            var layout = descriptor.Layouts.Object(input.Location);
            layout.Stride = input.Stride;
            layout.StepFunction = input.PerInstance ? MTLVertexStepFunction.PerInstance : MTLVertexStepFunction.PerVertex;
            descriptor.Layouts.SetObject(layout, input.Location);
        }
        return descriptor;
    }
}
