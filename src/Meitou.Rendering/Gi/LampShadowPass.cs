using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gi;

/// <summary>The view a lamp shadow trace reconstructs positions with (<see cref="PostProcess"/>'s, as the GI resolve's).</summary>
readonly record struct LampShadowView(Vector3 Right, Vector3 Up, Vector3 Back, Vector3 Eye, Vector2 NearPlanes, Vector2 Tan, Vector2 Size, float Footprint, float Phase);

/// <summary>
/// The trace of the lamps' shadows (docs/render-lights.md "Traced lamp shadows"): <see cref="LampShadowShaders.Trace"/> over the scene depth and the surface target
/// into a target of the render size, in the frame's traced scene. Render thread only.
/// </summary>
sealed unsafe class LampShadowPass : IDisposable
{
    const int Bindings = 10;

    readonly GpuContext ctx;
    readonly GpuProgram program;
    readonly GpuComputePipeline pipeline;
    readonly GpuSampler sampler;
    readonly PenumbraFilter penumbra;

    public LampShadowPass(GpuContext ctx, int lamps)
    {
        this.ctx = ctx;
        penumbra = new PenumbraFilter(ctx);
        program = ctx.Shaders.Compute(LampShadowShaders.Trace(lamps), "lamp shadow trace",
        [
            (0, DescriptorType.CombinedImageSampler), (1, DescriptorType.CombinedImageSampler), (2, DescriptorType.AccelerationStructureKhr),
            (3, DescriptorType.StorageBuffer), (4, DescriptorType.UniformBuffer), (5, DescriptorType.StorageImage),
            (6, DescriptorType.CombinedImageSampler), (7, DescriptorType.CombinedImageSampler), (8, DescriptorType.CombinedImageSampler), (9, DescriptorType.StorageImage),
        ], GpuSetLayout.Bindless);
        pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(program, "lamp shadow trace"));
        sampler = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Params
    {
        public Vector4 LightGrid, Right, Up, Back, Eye, Origin, PlanesTan, Size;
    }

    /// <summary>
    /// Traces into <paramref name="lost"/>, then filters it by the lamps' size into <paramref name="filtered"/> (<see cref="PenumbraFilter"/>, through <paramref name="scratch"/>;
    /// <paramref name="inverse"/>: NDC, jittered as the frame was, to the position relative to the eye). <paramref name="scene"/> has this frame's top level.
    /// </summary>
    public void Run(GiScene scene, WorldLamps lamps, GpuTexture depth, GpuTexture surface, GpuTexture lost, GpuTexture total, GpuTexture scratch, GpuTexture filtered, in LampShadowView view,
        in Matrix4x4 inverse)
    {
        var parameters = new Params
        {
            LightGrid = lamps.Grid,
            Right = new Vector4(view.Right, view.Footprint),
            Up = new Vector4(view.Up, view.Phase),
            Back = new Vector4(view.Back, 0),
            Eye = new Vector4(view.Eye, 0),
            Origin = new Vector4(scene.Origin, 0),
            PlanesTan = new Vector4(view.NearPlanes, view.Tan.X, view.Tan.Y),
            Size = new Vector4(view.Size, 0, 0),
        };
        var constants = ctx.Frame.Constants.Write<Params>(new ReadOnlySpan<Params>(&parameters, 1), 256);

        var cmd = (CommandList)ctx.BeginNative("lamp shadows");
        cmd.BeginLabel("lamp shadows");
        cmd.Barrier(BarrierBatch.Full);
        Push(cmd, scene, lamps, depth, surface, lost, total, constants);
        cmd.BindPipeline(pipeline);
        cmd.BindBindlessTable(program, 1, GpuBindPoint.Compute);
        cmd.Dispatch((uint)(((int)view.Size.X + 7) / 8), (uint)(((int)view.Size.Y + 7) / 8));
        cmd.Barrier(BarrierBatch.Full);
        penumbra.Run(cmd, new PenumbraTargets(depth, surface, lost, scratch, filtered, (int)view.Size.X, (int)view.Size.Y), inverse, view.Footprint);
        cmd.EndLabel();
        ctx.EndNative(cmd);
    }

    void Push(CommandList cmd, GiScene scene, WorldLamps lamps, GpuTexture depth, GpuTexture surface, GpuTexture lost, GpuTexture total, Transient constants)
    {
        var (cells, index, data) = lamps.Current;
        var images = stackalloc DescriptorImageInfo[5];
        images[0] = Image(depth);
        images[1] = Image(surface);
        images[2] = Image(cells);
        images[3] = Image(index);
        images[4] = Image(data);
        var storage = new DescriptorImageInfo(default, ctx.Device.ViewAt(lost.View().Index), ImageLayout.General);
        var storageTotal = new DescriptorImageInfo(default, ctx.Device.ViewAt(total.View().Index), ImageLayout.General);
        var top = scene.Top!.Handle;
        var asWrite = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr, AccelerationStructureCount = 1, PAccelerationStructures = &top,
        };
        var records = new DescriptorBufferInfo(ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
        var uniform = new DescriptorBufferInfo(ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(Params));

        var writes = stackalloc WriteDescriptorSet[Bindings];
        writes[0] = Sampled(0, &images[0]);
        writes[1] = Sampled(1, &images[1]);
        writes[2] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstBinding = 2, DescriptorCount = 1, DescriptorType = DescriptorType.AccelerationStructureKhr,
        };
        writes[3] = Buffer(3, DescriptorType.StorageBuffer, &records);
        writes[4] = Buffer(4, DescriptorType.UniformBuffer, &uniform);
        writes[5] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstBinding = 5, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &storage,
        };
        for (int i = 0; i < 3; i++) writes[6 + i] = Sampled((uint)(6 + i), &images[2 + i]);
        writes[9] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstBinding = 9, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &storageTotal,
        };
        cmd.PushDescriptors(program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, Bindings), GpuBindPoint.Compute);
    }

    DescriptorImageInfo Image(GpuTexture t) => new(ctx.Device.SamplerAt(sampler.Index), ctx.Device.ViewAt(t.View().Index), ImageLayout.General);

    static WriteDescriptorSet Sampled(uint binding, DescriptorImageInfo* image) => new()
    {
        SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = image,
    };

    static WriteDescriptorSet Buffer(uint binding, DescriptorType type, DescriptorBufferInfo* info) => new()
    {
        SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1, DescriptorType = type, PBufferInfo = info,
    };

    public void Dispose()
    {
        ctx.Shaders.Dispose(program);
        penumbra.Dispose();
    }
}
