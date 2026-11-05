using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;

using Meitou.Rendering.Gpu.Core;

namespace Meitou.Rendering.Gi;

/// <summary>
/// The hybrid near field (the <c>gihybrid</c> switch's <c>near</c> part, docs/render-gi.md "Hybrid GI"): at the GI resolve's low resolution one short ray per texel, shaded at a hit as
/// the probe trace shades its hits and read from the probes on a miss (<see cref="GiNearShaders.Trace"/>), accumulated with reprojection and filtered. The resolve then takes the result
/// in place of the probes' light (<see cref="GiResolveShaders.ResolveFor"/>). Runs after the scene, before the resolve. Render thread only.
/// </summary>
sealed unsafe class GiNearField(GpuContext ctx, GiScene scene, GiProbes probes) : IDisposable
{
    /// <summary>The rays' length, about a near probe cell: what the cells cannot resolve.</summary>
    public const float Reach = 150;
    /// <summary>The filter's guide: a hit nearer than this counts as near (KenshiNative's quarter of the reach).</summary>
    public const float GuideReach = Reach / 4;
    /// <summary>Beyond this eye distance no rays; the resolve fades back to the probes over its last quarter.</summary>
    public const float Range = 4000;
    /// <summary>The most light one sample may bring, per channel (KenshiNative's firefly clamp).</summary>
    public const float SampleClamp = 16;
    /// <summary>The samples a texel's mean keeps at most: the light's lag in frames.</summary>
    public const int MaxSamples = 16;

    /// <summary>The frame's camera and sizes (the resolve's reconstruction).</summary>
    public readonly record struct View(Vector3 Eye, Vector3 Right, Vector3 Up, Vector3 Back, Vector2 Tan, Vector2 Planes, int Width, int Height, int Divisor,
        int FullWidth, int FullHeight, bool Whole);

    [StructLayout(LayoutKind.Sequential)]
    struct TraceConstants
    {
        public GiProbes.Constants Probes;
        public Vector4 Reach, Frame, Planes, Eye, Right, Up, Back;
    }

    /// <summary>The temporal pass's block; the filter's is its first four members.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct FilterConstants
    {
        public Vector4 Size, Texel, Eye, Back, PreviousEye, PreviousRight, PreviousUp, PreviousBack, PreviousTan;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Iteration
    {
        public int Spread, Guided, Last;
    }

    readonly Dictionary<(bool Relocate, bool Clouds), (GpuProgram Program, GpuComputePipeline Pipeline)> traces = [];
    GpuProgram temporal, filter;
    GpuComputePipeline temporalPipeline, filterPipeline;
    bool made;
    readonly GpuSampler nearest = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge,
        TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));

    // Swapped each frame: index now, the other last frame's.
    readonly GpuTexture?[] surfaces = new GpuTexture?[2], histories = new GpuTexture?[2], lengths = new GpuTexture?[2];
    GpuTexture? raw, filteredA, filteredB;
    int now;
    uint frame;
    long lastFrame = -10;
    View last;

    (GpuProgram Program, GpuComputePipeline Pipeline) Trace(bool relocate, bool clouds)
    {
        if (traces.TryGetValue((relocate, clouds), out var built)) return built;
        (uint, DescriptorType)[] set0 =
        [
            (0, DescriptorType.AccelerationStructureKhr), (1, DescriptorType.StorageBuffer), (2, DescriptorType.StorageImage), (3, DescriptorType.UniformBuffer),
            (4, DescriptorType.CombinedImageSampler), (5, DescriptorType.CombinedImageSampler), (6, DescriptorType.CombinedImageSampler),
            (7, DescriptorType.CombinedImageSampler), (8, DescriptorType.CombinedImageSampler), (9, DescriptorType.CombinedImageSampler),
            (10, DescriptorType.CombinedImageSampler), (11, DescriptorType.CombinedImageSampler), (12, DescriptorType.CombinedImageSampler),
            (13, DescriptorType.CombinedImageSampler), (14, DescriptorType.CombinedImageSampler), (17, DescriptorType.StorageImage),
            .. clouds ? new (uint, DescriptorType)[] { (15, DescriptorType.CombinedImageSampler), (16, DescriptorType.CombinedImageSampler) } : [],
        ];
        var program = ctx.Shaders.Compute(GiNearShaders.Trace(relocate, clouds), "gi near trace", set0, GpuSetLayout.Bindless);
        return traces[(relocate, clouds)] = (program, ctx.Pipelines.Get(new ComputePipelineDesc(program, "gi near trace")));
    }

    void MakePasses()
    {
        if (made) return;
        made = true;
        temporal = ctx.Shaders.Compute(GiNearShaders.Temporal, "gi near temporal");
        temporalPipeline = ctx.Pipelines.Get(new ComputePipelineDesc(temporal, "gi near temporal"));
        filter = ctx.Shaders.Compute(GiNearShaders.Filter, "gi near filter");
        filterPipeline = ctx.Pipelines.Get(new ComputePipelineDesc(filter, "gi near filter"));
    }

    GpuTexture Image(InternalFormat format, int width, int height, string name) =>
        ctx.CreateTexture(new TextureDesc(format, width, height, Use: TextureUse.Storage | TextureUse.Sampled, Name: name));

    /// <summary>The images at the low target's size; new ones start without a history.</summary>
    void EnsureImages(int width, int height)
    {
        if (raw is { } made && made.Desc.Width == width && made.Desc.Height == height) return;
        DisposeImages();
        lastFrame = -10;
        raw = Image(InternalFormat.Rgba16f, width, height, "gi near raw");
        filteredA = Image(InternalFormat.Rgba16f, width, height, "gi near filtered");
        filteredB = Image(InternalFormat.Rgba16f, width, height, "gi near filtered");
        for (int i = 0; i < 2; i++)
        {
            surfaces[i] = Image(InternalFormat.Rgba32f, width, height, "gi near surface");
            histories[i] = Image(InternalFormat.Rgba16f, width, height, "gi near history");
            lengths[i] = Image(InternalFormat.R16f, width, height, "gi near samples");
        }
    }

    void DisposeImages()
    {
        foreach (var t in new[] { raw, filteredA, filteredB }.Concat(surfaces).Concat(histories).Concat(lengths)) t?.Dispose();
    }

    /// <summary>A history lasts while the frames follow one another and the camera does not cut (a tile of a tiled screenshot is another view).</summary>
    bool Continuous(in View v) => ctx.Frame.Number - lastFrame <= 2 && Vector3.Distance(last.Eye, v.Eye) < 2000 && v.Whole && last.Whole;

    /// <summary>
    /// Runs the three steps over <paramref name="depth"/> and <paramref name="surface"/> (the scene's, closed). Returns the filtered image (rgb the light, a how much to take),
    /// or null before the first probe update or the first traced scene.
    /// </summary>
    public GpuTexture? Run(GpuTexture depth, GpuTexture surface, in View v, bool relocate, bool clouds)
    {
        if (scene.Top is null) return null;
        var images = stackalloc DescriptorImageInfo[11];
        if (!probes.HitInputs(images, out var constants)) return null;
        var trace = Trace(relocate, clouds);
        MakePasses();
        EnsureImages(v.Width, v.Height);
        now = 1 - now;
        var cmd = (CommandList)ctx.BeginNative("gi near");
        cmd.BeginLabel("gi near");
        cmd.Barrier(BarrierBatch.Full);
        TraceRays(cmd, trace, images, constants, depth, surface, v, clouds);
        cmd.Order();
        var filterConstants = Filtering(v);
        RunTemporal(cmd, filterConstants);
        cmd.Order();
        RunFilter(cmd, filterConstants);
        cmd.EndLabel();
        ctx.EndNative(cmd);
        (last, lastFrame) = (v, ctx.Frame.Number);
        frame++;
        return filteredA;
    }

    void TraceRays(CommandList cmd, (GpuProgram Program, GpuComputePipeline Pipeline) trace, DescriptorImageInfo* images, in GiProbes.Constants probeConstants,
        GpuTexture depth, GpuTexture surface, in View v, bool clouds)
    {
        var constants = new TraceConstants
        {
            Probes = probeConstants,
            Reach = new Vector4(Reach, GuideReach, Range, SampleClamp),
            Frame = new Vector4(frame & 4095, v.Divisor, v.FullWidth, v.FullHeight),
            Planes = new Vector4(v.Planes, v.Tan.X, v.Tan.Y),
            Eye = new Vector4(v.Eye, 0), Right = new Vector4(v.Right, 0), Up = new Vector4(v.Up, 0), Back = new Vector4(v.Back, 0),
        };
        var block = ctx.Frame.Constants.Write<TraceConstants>(new ReadOnlySpan<TraceConstants>(&constants, 1), 256);
        var top = scene.Top!.Handle;
        var asWrite = new WriteDescriptorSetAccelerationStructureKHR { SType = StructureType.WriteDescriptorSetAccelerationStructureKhr, AccelerationStructureCount = 1, PAccelerationStructures = &top };
        var records = new DescriptorBufferInfo(ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
        var uniform = new DescriptorBufferInfo(ctx.Device.BufferAt(block.Handle.Index), block.Offset, (ulong)sizeof(TraceConstants));
        var rawImage = new DescriptorImageInfo(default, ctx.Device.ViewAt(raw!.View().Index), ImageLayout.General);
        var surfaceOut = new DescriptorImageInfo(default, ctx.Device.ViewAt(surfaces[now]!.View().Index), ImageLayout.General);
        var scenes = stackalloc DescriptorImageInfo[2];
        scenes[0] = new DescriptorImageInfo(ctx.Device.SamplerAt(nearest.Index), ctx.Device.ViewAt(surface.View().Index), ImageLayout.General);
        scenes[1] = new DescriptorImageInfo(ctx.Device.SamplerAt(nearest.Index), ctx.Device.ViewAt(depth.View().Index), ImageLayout.General);
        var writes = stackalloc WriteDescriptorSet[18];
        writes[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.AccelerationStructureKhr };
        writes[1] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 1, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &records };
        writes[2] = ImageWrite(2, DescriptorType.StorageImage, &rawImage);
        writes[3] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 3, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = &uniform };
        for (int i = 0; i < 9; i++) writes[4 + i] = ImageWrite((uint)(4 + i), DescriptorType.CombinedImageSampler, &images[i]);
        for (int i = 0; i < 2; i++) writes[13 + i] = ImageWrite((uint)(13 + i), DescriptorType.CombinedImageSampler, &scenes[i]);
        writes[15] = ImageWrite(17, DescriptorType.StorageImage, &surfaceOut);
        int count = 16;
        if (clouds)
            for (int i = 0; i < 2; i++) writes[count++] = ImageWrite((uint)(15 + i), DescriptorType.CombinedImageSampler, &images[9 + i]);
        cmd.BindPipeline(trace.Pipeline);
        cmd.PushDescriptors(trace.Program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, count), GpuBindPoint.Compute);
        cmd.BindBindlessTable(trace.Program, 1, GpuBindPoint.Compute);
        cmd.Dispatch((uint)((v.Width + 7) / 8), (uint)((v.Height + 7) / 8));
    }

    static WriteDescriptorSet ImageWrite(uint binding, DescriptorType type, DescriptorImageInfo* info) =>
        new() { SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1, DescriptorType = type, PImageInfo = info };

    FilterConstants Filtering(in View v) => new()
    {
        Size = new Vector4(v.Width, v.Height, MaxSamples, Continuous(v) ? 1 : 0),
        Texel = new Vector4(2 * v.Tan.Y / v.Height, Range, 0, 0),
        Eye = new Vector4(v.Eye, 0), Back = new Vector4(v.Back, 0),
        PreviousEye = new Vector4(last.Eye, 0), PreviousRight = new Vector4(last.Right, 0), PreviousUp = new Vector4(last.Up, 0), PreviousBack = new Vector4(last.Back, 0),
        PreviousTan = new Vector4(last.Tan, 0, 0),
    };

    static GpuBinding Storage(uint slot, GpuTexture texture) => GpuBinding.StorageImage(slot, new SampledTexture(default, texture.View()));

    void RunTemporal(CommandList cmd, in FilterConstants constants)
    {
        var data = ctx.Frame.Constants.Write(new ReadOnlySpan<FilterConstants>(in constants), 256);
        int previous = 1 - now;
        var bindings = stackalloc GpuBinding[8];
        bindings[0] = Storage(0, surfaces[now]!);
        bindings[1] = Storage(1, surfaces[previous]!);
        bindings[2] = Storage(2, raw!);
        bindings[3] = Storage(3, histories[previous]!);
        bindings[4] = Storage(4, lengths[previous]!);
        bindings[5] = Storage(5, histories[now]!);
        bindings[6] = Storage(6, lengths[now]!);
        bindings[7] = GpuBinding.Uniform(7, new GpuBufferBinding(data.Handle, data.Offset, (ulong)sizeof(FilterConstants)));
        cmd.BindPipeline(temporalPipeline);
        cmd.BindProgramResources(temporal, 0, new ReadOnlySpan<GpuBinding>(bindings, 8));
        cmd.Dispatch((uint)(((int)constants.Size.X + 7) / 8), (uint)(((int)constants.Size.Y + 7) / 8));
    }

    /// <summary>Spreads 1, 2 and 4; the wide one held by the near hits (<see cref="GiNearShaders.Filter"/>); the last output is <see cref="filteredA"/>.</summary>
    void RunFilter(CommandList cmd, in FilterConstants constants)
    {
        var data = ctx.Frame.Constants.Write(new ReadOnlySpan<FilterConstants>(in constants), 256);
        var input = histories[now]!;
        GpuTexture[] outputs = [filteredA!, filteredB!, filteredA!];
        var bindings = stackalloc GpuBinding[5];
        cmd.BindPipeline(filterPipeline);
        for (int i = 0; i < 3; i++)
        {
            bindings[0] = Storage(0, surfaces[now]!);
            bindings[1] = Storage(1, input);
            bindings[2] = Storage(2, lengths[now]!);
            bindings[3] = Storage(3, outputs[i]);
            bindings[4] = GpuBinding.Uniform(4, new GpuBufferBinding(data.Handle, data.Offset, (ulong)sizeof(FilterConstants)));
            cmd.BindProgramResources(filter, 0, new ReadOnlySpan<GpuBinding>(bindings, 5));
            var iteration = new Iteration { Spread = 1 << i, Guided = i == 2 ? 1 : 0, Last = i == 2 ? 1 : 0 };
            cmd.PushConstants(filter, GpuStage.Compute, in iteration);
            cmd.Dispatch((uint)(((int)constants.Size.X + 7) / 8), (uint)(((int)constants.Size.Y + 7) / 8));
            cmd.Order();
            input = outputs[i];
        }
    }

    public void Dispose()
    {
        DisposeImages();
        foreach (var t in traces.Values) ctx.Shaders.Dispose(t.Program);
        if (!made) return;
        ctx.Shaders.Dispose(temporal);
        ctx.Shaders.Dispose(filter);
    }
}
