using System.Numerics;
using System.Runtime.InteropServices;
using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gi;

/// <summary>What the traced sun's rays read and write, all at the render size: the raw mask, and with a temporal upscaler last frame's filtered one and this frame's (<see cref="TracedSunShaders.Filter"/>).</summary>
readonly record struct TracedSunTargets(GpuTexture Depth, GpuTexture Surface, GpuTexture Term, GpuTexture Mask, int Width, int Height, GpuTexture? History = null, GpuTexture? Filtered = null);

/// <summary>The filter's reprojection: last frame's eye-relative position to its clip, this eye minus that one, and whether last frame's filtered mask is usable.</summary>
readonly record struct TracedSunHistory(Matrix4x4 Previous, Vector3 Shift, bool Valid);

/// <summary>
/// The traced sun's rays (docs/render-shadows.md "Traced sun"): <see cref="TracedSunShaders.Mask"/> over the scene's depth and surface target, one ray a pixel towards
/// the sun's disc, against the traced scene (<see cref="GiScene"/>). Render thread only.
/// </summary>
sealed unsafe class TracedSunPass : IDisposable
{
    /// <summary>The sun's angular radius the disc is sampled over (tangent): the real sun's, 0.267 degrees (KenshiNative's 0.015, about three suns, widened far casters' penumbrae as much).</summary>
    public const float DiscTangent = 0.00465f;
    /// <summary>The filter's blend of a new ray: about 8 frames of history where the neighbourhood keeps it.</summary>
    const float Response = 0.15f;
    /// <summary>The longest ray, within what the traced scene holds around the eye.</summary>
    const float RayLength = 8000;

    readonly GpuContext ctx;
    readonly GpuProgram program;
    readonly GpuComputePipeline pipeline;
    readonly GpuSampler sampler;
    readonly GpuProgram filterProgram;
    readonly GpuComputePipeline filterPipeline;

    public TracedSunPass(GpuContext ctx)
    {
        filterProgram = ctx.Shaders.Compute(TracedSunShaders.Filter, "traced sun filter",
        [
            (0, DescriptorType.CombinedImageSampler), (1, DescriptorType.CombinedImageSampler), (2, DescriptorType.CombinedImageSampler), (3, DescriptorType.CombinedImageSampler),
            (4, DescriptorType.StorageImage), (5, DescriptorType.UniformBuffer),
        ]);
        filterPipeline = ctx.Pipelines.Get(new ComputePipelineDesc(filterProgram, "traced sun filter"));
        this.ctx = ctx;
        program = ctx.Shaders.Compute(TracedSunShaders.Mask, "traced sun",
        [
            (0, DescriptorType.CombinedImageSampler), (1, DescriptorType.CombinedImageSampler), (2, DescriptorType.CombinedImageSampler), (3, DescriptorType.StorageImage),
            (4, DescriptorType.AccelerationStructureKhr), (5, DescriptorType.StorageBuffer), (6, DescriptorType.UniformBuffer),
        ], GpuSetLayout.Bindless);
        pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(program, "traced sun"));
        sampler = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Params
    {
        public Matrix4x4 Inverse;
        public Vector4 Eye, Sun, Size, Grass;
    }

    /// <summary>
    /// Traces into <see cref="TracedSunTargets.Mask"/>. <paramref name="inverse"/>: NDC to the position relative to the eye; <paramref name="pixelSlope"/>: a pixel's
    /// width per unit of depth; <paramref name="frame"/>: the temporal upscaler's frame (the disc is then sampled anew each frame), null: the disc's centre, a hard shadow.
    /// </summary>
    public void Run(GiScene scene, in TracedSunTargets t, in Matrix4x4 inverse, Vector3 eye, Vector3 toSun, float pixelSlope, uint? frame, in TracedSunHistory history = default)
    {
        if (scene.Top is null) return;
        var parameters = new Params
        {
            Inverse = inverse,
            Eye = new Vector4(eye - scene.Origin, frame is null ? 0 : DiscTangent),
            Sun = new Vector4(Vector3.Normalize(toSun), RayLength),
            Size = new Vector4(t.Width, t.Height, (frame ?? 0) % 1024, pixelSlope),
            Grass = scene.GrassRayParams,
        };
        var constants = ctx.Frame.Constants.Write<Params>(new ReadOnlySpan<Params>(&parameters, 1), 256);
        var cmd = (CommandList)ctx.BeginNative("traced sun");
        cmd.BeginLabel("traced sun");
        cmd.Barrier(BarrierBatch.Full);
        Bind(cmd, scene, t, constants);
        cmd.BindPipeline(pipeline);
        cmd.Dispatch((uint)((t.Width + 7) / 8), (uint)((t.Height + 7) / 8));
        cmd.Barrier(BarrierBatch.Full);
        if (t.History is { } previous && t.Filtered is { } filtered)
        {
            RunFilter(cmd, t, previous, filtered, inverse, history);
            cmd.Barrier(BarrierBatch.Full);
        }
        cmd.EndLabel();
        ctx.EndNative(cmd);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FilterParams
    {
        public Matrix4x4 Inverse, Previous;
        public Vector4 Shift, Size;
    }

    void RunFilter(CommandList cmd, in TracedSunTargets t, GpuTexture previous, GpuTexture filtered, in Matrix4x4 inverse, in TracedSunHistory history)
    {
        var parameters = new FilterParams
        {
            Inverse = inverse, Previous = history.Previous, Shift = new Vector4(history.Shift, history.Valid ? 1 : 0), Size = new Vector4(t.Width, t.Height, Response, 0),
        };
        var constants = ctx.Frame.Constants.Write<FilterParams>(new ReadOnlySpan<FilterParams>(&parameters, 1), 256);
        DescriptorImageInfo Sampled(GpuTexture texture) => new(ctx.Device.SamplerAt(sampler.Index), ctx.Device.ViewAt(texture.View().Index), ImageLayout.General);
        var images = stackalloc DescriptorImageInfo[5];
        images[0] = Sampled(t.Depth);
        images[1] = Sampled(t.Surface);
        images[2] = Sampled(t.Mask);
        images[3] = Sampled(previous);
        images[4] = new DescriptorImageInfo(default, ctx.Device.ViewAt(filtered.View().Index), ImageLayout.General);
        var uniform = new DescriptorBufferInfo(ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(FilterParams));
        var writes = stackalloc WriteDescriptorSet[6];
        for (int i = 0; i < 4; i++) writes[i] = Image((uint)i, DescriptorType.CombinedImageSampler, &images[i]);
        writes[4] = Image(4, DescriptorType.StorageImage, &images[4]);
        writes[5] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 5, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = &uniform };
        cmd.BindPipeline(filterPipeline);
        cmd.PushDescriptors(filterProgram, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 6), GpuBindPoint.Compute);
        cmd.Dispatch((uint)((t.Width + 7) / 8), (uint)((t.Height + 7) / 8));
    }

    void Bind(CommandList cmd, GiScene scene, in TracedSunTargets t, in Transient constants)
    {
        DescriptorImageInfo Sampled(GpuTexture texture) => new(ctx.Device.SamplerAt(sampler.Index), ctx.Device.ViewAt(texture.View().Index), ImageLayout.General);
        var depth = Sampled(t.Depth);
        var surface = Sampled(t.Surface);
        var term = Sampled(t.Term);
        var storage = new DescriptorImageInfo(default, ctx.Device.ViewAt(t.Mask.View().Index), ImageLayout.General);
        var top = scene.Top!.Handle;
        var asWrite = new WriteDescriptorSetAccelerationStructureKHR { SType = StructureType.WriteDescriptorSetAccelerationStructureKhr, AccelerationStructureCount = 1, PAccelerationStructures = &top };
        var records = new DescriptorBufferInfo(ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
        var uniform = new DescriptorBufferInfo(ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(Params));
        var writes = stackalloc WriteDescriptorSet[7];
        writes[0] = Image(0, DescriptorType.CombinedImageSampler, &depth);
        writes[1] = Image(1, DescriptorType.CombinedImageSampler, &surface);
        writes[2] = Image(2, DescriptorType.CombinedImageSampler, &term);
        writes[3] = Image(3, DescriptorType.StorageImage, &storage);
        writes[4] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstBinding = 4, DescriptorCount = 1, DescriptorType = DescriptorType.AccelerationStructureKhr };
        writes[5] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 5, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &records };
        writes[6] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 6, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = &uniform };
        cmd.PushDescriptors(program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 7), GpuBindPoint.Compute);
        cmd.BindBindlessTable(program, 1, GpuBindPoint.Compute);   // the cut-out maps at the candidates
    }

    static WriteDescriptorSet Image(uint binding, DescriptorType type, DescriptorImageInfo* info) =>
        new() { SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1, DescriptorType = type, PImageInfo = info };

    public void Dispose()
    {
        ctx.Shaders.Dispose(program);
        ctx.Shaders.Dispose(filterProgram);
    }
}
