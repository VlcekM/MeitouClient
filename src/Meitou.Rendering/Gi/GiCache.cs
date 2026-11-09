using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;

using Meitou.Rendering.Gpu.Core;

namespace Meitou.Rendering.Gi;

/// <summary>
/// The hybrid GI's radiance cache (the <c>gihybrid</c> switch's <c>cache</c> part, docs/render-gi.md "Hybrid GI"): world-space cells made by the GI resolve's low texels,
/// traced from their own surface points as the probe trace shades its hits (<see cref="GiCacheShaders.Trace"/>), and gathered back per texel; the resolve takes the
/// result in place of the probes' light (<see cref="GiResolveShaders.ResolveFor"/>). Runs after the scene, before the resolve. Render thread only.
/// </summary>
sealed unsafe class GiCache(GpuContext ctx, GiScene scene, GiProbes probes) : IDisposable
{
    /// <summary>The table's slots (a power of two); a key takes one of 8 consecutive ones.</summary>
    public const int Capacity = 1 << 18;
    /// <summary>The direction subsets a cell keeps, one traced a frame: a still scene's cells hold <see cref="Ring"/> × <see cref="RaysPerCell"/> rays; a change of light is followed in as many frames.</summary>
    public const int Ring = 16;
    /// <summary>The rays a listed cell traces a frame (the trace's workgroup).</summary>
    public const int RaysPerCell = 32;
    /// <summary>The subsets a new cell traces in its first frame; from as many on the resolve takes it fully.</summary>
    public const int FirstSubsets = 4;
    /// <summary>A subset traced again whose light moved by more than this share of the cell's drops the cell's other subsets (a sudden change of light), which are traced again.</summary>
    public const float ChangeShare = 0.25f;
    /// <summary>The finest cell's size.</summary>
    public const float FinestCell = 4;
    /// <summary>Cells across the picture's height at most (the cell size grows with the eye distance to keep about this many), in powers of two from <see cref="FinestCell"/>.</summary>
    public const float CellsPerHeight = 96;
    /// <summary>Where in a level's distances the next coarser one starts to blend in (its fraction of the level).</summary>
    public const float LevelBlend = 0.6f;
    /// <summary>How far past the diagonal between two axes the second one still blends in (difference of the normal's two largest components).</summary>
    public const float AxisBlend = 0.2f;
    /// <summary>The share of their trilinear weight the found cells must keep through the plane and facing weights for the texel to take the cache fully (less: the probes mix in).</summary>
    public const float MatchFloor = 0.25f;
    /// <summary>The rays' length, about a near probe cell: the probes are the radiance past it.</summary>
    public const float Reach = 150;
    /// <summary>Beyond this eye distance no cells; the resolve fades back to the probes over its last quarter.</summary>
    public const float Range = 4000;
    /// <summary>The most light one ray may bring, per channel (KenshiNative's firefly clamp).</summary>
    public const float SampleClamp = 16;
    /// <summary>The frames a cell no texel touches is kept for while the table is at most half full.</summary>
    public const int EvictAge = 600;

    /// <summary>The frame's camera and sizes (the resolve's reconstruction). <paramref name="TanY"/>: the whole view's, whatever window is drawn (the cells' sizes).</summary>
    public readonly record struct View(Vector3 Eye, Vector3 Right, Vector3 Up, Vector3 Back, Vector2 Tan, Vector2 Planes, int Width, int Height, int Divisor,
        int FullWidth, int FullHeight, float TanY);

    [StructLayout(LayoutKind.Sequential)]
    struct ViewConstants
    {
        public Vector4 Eye, Right, Up, Back, Planes, Size, Cells;
        public uint Frame, EvictAge, Subset, Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TraceConstants
    {
        public GiProbes.Constants Probes;
        public Vector4 Reach;
        public ViewConstants View;
    }

    readonly Dictionary<(bool Relocate, bool Clouds), (GpuProgram Program, GpuComputePipeline Pipeline)> traces = [];
    GpuProgram evict, touch, arguments, gather;
    GpuComputePipeline evictPipeline, touchPipeline, argumentsPipeline, gatherPipeline;
    bool made;
    readonly GpuSampler nearest = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge,
        TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));

    DeviceBuffer? keys, stamps, sums, cells, ring, control, active;
    GpuTexture? output;
    uint frame;

    (GpuProgram Program, GpuComputePipeline Pipeline) Trace(bool relocate, bool clouds)
    {
        if (traces.TryGetValue((relocate, clouds), out var built)) return built;
        (uint, DescriptorType)[] set0 =
        [
            (0, DescriptorType.AccelerationStructureKhr), (1, DescriptorType.StorageBuffer), (2, DescriptorType.StorageBuffer), (3, DescriptorType.UniformBuffer),
            (4, DescriptorType.CombinedImageSampler), (5, DescriptorType.CombinedImageSampler), (6, DescriptorType.CombinedImageSampler),
            (7, DescriptorType.CombinedImageSampler), (8, DescriptorType.CombinedImageSampler), (9, DescriptorType.CombinedImageSampler),
            (10, DescriptorType.CombinedImageSampler), (11, DescriptorType.CombinedImageSampler), (12, DescriptorType.CombinedImageSampler),
            (13, DescriptorType.StorageBuffer), (14, DescriptorType.StorageBuffer), (17, DescriptorType.StorageBuffer), (18, DescriptorType.CombinedImageSampler),
            .. clouds ? new (uint, DescriptorType)[] { (15, DescriptorType.CombinedImageSampler), (16, DescriptorType.CombinedImageSampler) } : [],
        ];
        var program = ctx.Shaders.Compute(GiCacheShaders.Trace(relocate, clouds), "gi cache trace", set0, GpuSetLayout.Bindless);
        return traces[(relocate, clouds)] = (program, ctx.Pipelines.Get(new ComputePipelineDesc(program, "gi cache trace")));
    }

    void MakePasses()
    {
        if (made) return;
        made = true;
        (evict, evictPipeline) = Pass(GiCacheShaders.Evict, "gi cache evict");
        (touch, touchPipeline) = Pass(GiCacheShaders.Touch, "gi cache touch");
        (arguments, argumentsPipeline) = Pass(GiCacheShaders.Arguments, "gi cache arguments");
        (gather, gatherPipeline) = Pass(GiCacheShaders.Gather, "gi cache gather");
        keys = Buffer(Capacity * 4, "gi cache keys");
        stamps = Buffer(Capacity * 4, "gi cache stamps");
        sums = Buffer(Capacity * 32, "gi cache sums");
        cells = Buffer(Capacity * 64, "gi cache cells");
        ring = Buffer((ulong)Capacity * Ring * 4, "gi cache ring");
        active = Buffer(Capacity * 4, "gi cache list");
        control = DeviceBuffer.Create(ctx, 32, BufferUse.Storage | BufferUse.Indirect | BufferUse.TransferDst, "gi cache control");
        // Empty: no keys, no sums, never touched (the frames count from 1).
        var cmd = ctx.Device.BeginImmediate();
        foreach (var b in new[] { keys, stamps, sums, control }) ctx.Device.Vk.CmdFillBuffer(cmd, ctx.Device.BufferAt(b.Handle.Index), 0, b.Size, 0);
        ctx.Device.EndImmediate(cmd);
    }

    (GpuProgram, GpuComputePipeline) Pass(string source, string name)
    {
        var program = ctx.Shaders.Compute(source, name);
        return (program, ctx.Pipelines.Get(new ComputePipelineDesc(program, name)));
    }

    DeviceBuffer Buffer(ulong size, string name) => DeviceBuffer.Create(ctx, size, BufferUse.Storage | BufferUse.TransferDst, name);

    void EnsureOutput(int width, int height)
    {
        if (output is { } made && made.Desc.Width == width && made.Desc.Height == height) return;
        output?.Dispose();
        output = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16f, width, height, Use: TextureUse.Storage | TextureUse.Sampled, Name: "gi cache"));
    }

    /// <summary>
    /// Runs the passes over <paramref name="depth"/> and <paramref name="surface"/> (the scene's, closed). Returns the gathered image (rgb the light, a how much to take),
    /// or null before the first probe update or the first traced scene.
    /// </summary>
    public GpuTexture? Run(GpuTexture depth, GpuTexture surface, in View v, bool relocate, bool clouds)
    {
        if (scene.Top is null) return null;
        var images = stackalloc DescriptorImageInfo[11];
        if (!probes.HitInputs(images, out var probeConstants)) return null;
        var trace = Trace(relocate, clouds);
        MakePasses();
        EnsureOutput(v.Width, v.Height);
        frame++;
        var view = Constants(v);
        var data = ctx.Frame.Constants.Write(new ReadOnlySpan<ViewConstants>(in view), 256);
        var viewBinding = GpuBinding.Uniform(8, new GpuBufferBinding(data.Handle, data.Offset, (ulong)sizeof(ViewConstants)));
        var cmd = (CommandList)ctx.BeginNative("gi cache");
        cmd.BeginLabel("gi cache");
        cmd.Barrier(BarrierBatch.Full);
        RunEvict(cmd, viewBinding);
        cmd.Order();
        RunTouch(cmd, viewBinding, depth, surface, v);
        cmd.Order();
        RunArguments(cmd);
        cmd.Order();
        TraceCells(cmd, trace, images, probeConstants, view, clouds);
        cmd.Order();
        RunGather(cmd, viewBinding, depth, surface, v);
        cmd.EndLabel();
        ctx.EndNative(cmd);
        return output;
    }

    ViewConstants Constants(in View v) => new()
    {
        Eye = new Vector4(v.Eye, v.Divisor), Right = new Vector4(v.Right, 0), Up = new Vector4(v.Up, 0), Back = new Vector4(v.Back, 0),
        Planes = new Vector4(v.Planes, v.Tan.X, v.Tan.Y),
        Size = new Vector4(v.Width, v.Height, v.FullWidth, v.FullHeight),
        Cells = new Vector4(FinestCell, 2 * v.TanY / CellsPerHeight, Range, LevelBlend),
        Frame = frame, EvictAge = EvictAge, Subset = frame % Ring,
    };

    static GpuBinding Storage(uint slot, DeviceBuffer buffer) => GpuBinding.Storage(slot, new GpuBufferBinding(buffer.Handle, 0, buffer.Size));

    void RunEvict(CommandList cmd, in GpuBinding view)
    {
        var bindings = stackalloc GpuBinding[4];
        bindings[0] = Storage(0, keys!);
        bindings[1] = Storage(1, stamps!);
        bindings[2] = Storage(2, control!);
        bindings[3] = view;
        cmd.BindPipeline(evictPipeline);
        cmd.BindProgramResources(evict, 0, new ReadOnlySpan<GpuBinding>(bindings, 4));
        cmd.Dispatch(Capacity / 64);
    }

    void RunTouch(CommandList cmd, in GpuBinding view, GpuTexture depth, GpuTexture surface, in View v)
    {
        var bindings = stackalloc GpuBinding[9];
        bindings[0] = GpuBinding.Sampled(0, new SampledTexture(nearest, surface.View()));
        bindings[1] = GpuBinding.Sampled(1, new SampledTexture(nearest, depth.View()));
        bindings[2] = Storage(2, keys!);
        bindings[3] = Storage(3, stamps!);
        bindings[4] = Storage(4, sums!);
        bindings[5] = Storage(5, cells!);
        bindings[6] = Storage(6, control!);
        bindings[7] = Storage(7, active!);
        bindings[8] = view;
        cmd.BindPipeline(touchPipeline);
        cmd.BindProgramResources(touch, 0, new ReadOnlySpan<GpuBinding>(bindings, 9));
        cmd.Dispatch((uint)((v.Width + 7) / 8), (uint)((v.Height + 7) / 8));
    }

    void RunArguments(CommandList cmd)
    {
        var binding = Storage(0, control!);
        cmd.BindPipeline(argumentsPipeline);
        cmd.BindProgramResources(arguments, 0, new ReadOnlySpan<GpuBinding>(in binding));
        cmd.Dispatch(1);
    }

    void TraceCells(CommandList cmd, (GpuProgram Program, GpuComputePipeline Pipeline) trace, DescriptorImageInfo* images, in GiProbes.Constants probeConstants,
        in ViewConstants view, bool clouds)
    {
        var constants = new TraceConstants { Probes = probeConstants, Reach = new Vector4(Reach, SampleClamp, 0, 0), View = view };
        var block = ctx.Frame.Constants.Write<TraceConstants>(new ReadOnlySpan<TraceConstants>(&constants, 1), 256);
        var top = scene.Top!.Handle;
        var asWrite = new WriteDescriptorSetAccelerationStructureKHR { SType = StructureType.WriteDescriptorSetAccelerationStructureKhr, AccelerationStructureCount = 1, PAccelerationStructures = &top };
        var records = new DescriptorBufferInfo(ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
        var uniform = new DescriptorBufferInfo(ctx.Device.BufferAt(block.Handle.Index), block.Offset, (ulong)sizeof(TraceConstants));
        var buffers = stackalloc DescriptorBufferInfo[4];
        buffers[0] = Whole(cells!);
        buffers[1] = Whole(sums!);
        buffers[2] = Whole(active!);
        buffers[3] = Whole(ring!);
        var writes = stackalloc WriteDescriptorSet[19];
        var tiles = probes.TileImage;
        writes[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.AccelerationStructureKhr };
        writes[1] = BufferWrite(1, DescriptorType.StorageBuffer, &records);
        writes[2] = BufferWrite(2, DescriptorType.StorageBuffer, &buffers[0]);
        writes[3] = BufferWrite(3, DescriptorType.UniformBuffer, &uniform);
        for (int i = 0; i < 9; i++) writes[4 + i] = ImageWrite((uint)(4 + i), &images[i]);
        writes[13] = BufferWrite(13, DescriptorType.StorageBuffer, &buffers[1]);
        writes[14] = BufferWrite(14, DescriptorType.StorageBuffer, &buffers[2]);
        writes[15] = BufferWrite(17, DescriptorType.StorageBuffer, &buffers[3]);
        writes[16] = ImageWrite(18, &tiles);
        int count = 17;
        if (clouds)
            for (int i = 0; i < 2; i++) writes[count++] = ImageWrite((uint)(15 + i), &images[9 + i]);
        cmd.BindPipeline(trace.Pipeline);
        cmd.PushDescriptors(trace.Program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, count), GpuBindPoint.Compute);
        cmd.BindBindlessTable(trace.Program, 1, GpuBindPoint.Compute);
        cmd.DispatchIndirect(control!.Handle, 4);
    }

    DescriptorBufferInfo Whole(DeviceBuffer buffer) => new(ctx.Device.BufferAt(buffer.Handle.Index), 0, buffer.Size);

    static WriteDescriptorSet BufferWrite(uint binding, DescriptorType type, DescriptorBufferInfo* info) =>
        new() { SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1, DescriptorType = type, PBufferInfo = info };

    static WriteDescriptorSet ImageWrite(uint binding, DescriptorImageInfo* info) =>
        new() { SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = info };

    void RunGather(CommandList cmd, in GpuBinding view, GpuTexture depth, GpuTexture surface, in View v)
    {
        var bindings = stackalloc GpuBinding[6];
        bindings[0] = GpuBinding.Sampled(0, new SampledTexture(nearest, surface.View()));
        bindings[1] = GpuBinding.Sampled(1, new SampledTexture(nearest, depth.View()));
        bindings[2] = Storage(2, keys!);
        bindings[3] = Storage(5, cells!);
        bindings[4] = GpuBinding.StorageImage(6, new SampledTexture(default, output!.View()));
        bindings[5] = view;
        cmd.BindPipeline(gatherPipeline);
        cmd.BindProgramResources(gather, 0, new ReadOnlySpan<GpuBinding>(bindings, 6));
        cmd.Dispatch((uint)((v.Width + 7) / 8), (uint)((v.Height + 7) / 8));
    }

    public void Dispose()
    {
        output?.Dispose();
        foreach (var b in new[] { keys, stamps, sums, cells, ring, control, active }) b?.Dispose();
        foreach (var t in traces.Values) ctx.Shaders.Dispose(t.Program);
        if (!made) return;
        foreach (var p in new[] { evict, touch, arguments, gather }) ctx.Shaders.Dispose(p);
    }
}
