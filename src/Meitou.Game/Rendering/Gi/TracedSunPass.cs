using System.Numerics;
using System.Runtime.InteropServices;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gi;

/// <summary>What the traced sun's rays read and write, all at the render size: the rays' mask (visibility, the penumbra's spread), the filter's scratch and its result (<see cref="PenumbraFilter"/>).</summary>
readonly record struct TracedSunTargets(GpuTexture Depth, GpuTexture Surface, GpuTexture Term, GpuTexture Mask, GpuTexture Scratch, GpuTexture Filtered, int Width, int Height);

/// <summary>
/// The traced sun's rays (docs/render-shadows.md "Traced sun"): <see cref="TracedSunShaders.Mask"/> over the scene's depth and surface target, one ray a pixel towards
/// the sun's disc, against the traced scene (<see cref="GiScene"/>). Render thread only.
/// </summary>
sealed unsafe class TracedSunPass : IDisposable
{
    /// <summary>The sun's angular radius the disc is sampled over (tangent): the real sun's, 0.267 degrees (KenshiNative's 0.015, about three suns, widened far casters' penumbrae as much).</summary>
    public const float DiscTangent = 0.00465f;
    /// <summary>The longest ray, within what the traced scene holds around the eye.</summary>
    const float RayLength = 8000;

    readonly GpuContext _ctx;
    readonly GpuProgram _program;
    readonly GpuComputePipeline _pipeline;
    readonly GpuSampler _sampler;
    readonly PenumbraFilter _penumbra;

    public TracedSunPass(GpuContext ctx)
    {
        _penumbra = new PenumbraFilter(ctx);
        this._ctx = ctx;
        _program = ctx.Shaders.Compute(TracedSunShaders.Mask, "traced sun",
        [
            (0, DescriptorType.CombinedImageSampler), (1, DescriptorType.CombinedImageSampler), (2, DescriptorType.CombinedImageSampler), (3, DescriptorType.StorageImage),
            (4, DescriptorType.AccelerationStructureKhr), (5, DescriptorType.StorageBuffer), (6, DescriptorType.UniformBuffer),
        ], GpuSetLayout.Bindless);
        _pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_program, "traced sun"));
        _sampler = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Params
    {
        public Matrix4x4 Inverse;
        public Vector4 Eye, Sun, Size, Grass;
    }

    /// <summary>
    /// Traces into <see cref="TracedSunTargets.Mask"/>, then filters it into <see cref="TracedSunTargets.Filtered"/>. <paramref name="inverse"/>: NDC to the position relative to the eye;
    /// <paramref name="pixelSlope"/>: a pixel's width per unit of depth. Nothing depends on the frame: a still view gives the same picture.
    /// </summary>
    public void Run(GiScene scene, in TracedSunTargets t, in Matrix4x4 inverse, Vector3 eye, Vector3 toSun, float pixelSlope)
    {
        if (scene.Top is null) return;
        var parameters = new Params
        {
            Inverse = inverse,
            Eye = new Vector4(eye - scene.Origin, DiscTangent),
            Sun = new Vector4(Vector3.Normalize(toSun), RayLength),
            Size = new Vector4(t.Width, t.Height, 0, pixelSlope),
            Grass = scene.GrassRayParams,
        };
        var constants = _ctx.Frame.Constants.Write<Params>(new ReadOnlySpan<Params>(&parameters, 1), 256);
        var cmd = (CommandList)_ctx.BeginNative("traced sun");
        cmd.BeginLabel("traced sun");
        cmd.Barrier(BarrierBatch.Full);
        Bind(cmd, scene, t, constants);
        cmd.BindPipeline(_pipeline);
        cmd.Dispatch((uint)((t.Width + 7) / 8), (uint)((t.Height + 7) / 8));
        cmd.Barrier(BarrierBatch.Full);
        _penumbra.Run(cmd, new PenumbraTargets(t.Depth, t.Surface, t.Mask, t.Scratch, t.Filtered, t.Width, t.Height), inverse, pixelSlope);
        cmd.EndLabel();
        _ctx.EndNative(cmd);
    }

    void Bind(CommandList cmd, GiScene scene, in TracedSunTargets t, in Transient constants)
    {
        DescriptorImageInfo Sampled(GpuTexture texture) => new(_ctx.Device.SamplerAt(_sampler.Index), _ctx.Device.ViewAt(texture.View().Index), ImageLayout.General);
        var depth = Sampled(t.Depth);
        var surface = Sampled(t.Surface);
        var term = Sampled(t.Term);
        var storage = new DescriptorImageInfo(default, _ctx.Device.ViewAt(t.Mask.View().Index), ImageLayout.General);
        var top = scene.Top!.Handle;
        var asWrite = new WriteDescriptorSetAccelerationStructureKHR { SType = StructureType.WriteDescriptorSetAccelerationStructureKhr, AccelerationStructureCount = 1, PAccelerationStructures = &top };
        var records = new DescriptorBufferInfo(_ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
        var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(Params));
        var writes = stackalloc WriteDescriptorSet[7];
        writes[0] = Image(0, DescriptorType.CombinedImageSampler, &depth);
        writes[1] = Image(1, DescriptorType.CombinedImageSampler, &surface);
        writes[2] = Image(2, DescriptorType.CombinedImageSampler, &term);
        writes[3] = Image(3, DescriptorType.StorageImage, &storage);
        writes[4] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstBinding = 4, DescriptorCount = 1, DescriptorType = DescriptorType.AccelerationStructureKhr };
        writes[5] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 5, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &records };
        writes[6] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 6, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = &uniform };
        cmd.PushDescriptors(_program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 7), GpuBindPoint.Compute);
        cmd.BindBindlessTable(_program, 1, GpuBindPoint.Compute);   // the cut-out maps at the candidates
    }

    static WriteDescriptorSet Image(uint binding, DescriptorType type, DescriptorImageInfo* info) =>
        new() { SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1, DescriptorType = type, PImageInfo = info };

    public void Dispose()
    {
        _ctx.Shaders.Dispose(_program);
        _penumbra.Dispose();
    }
}
