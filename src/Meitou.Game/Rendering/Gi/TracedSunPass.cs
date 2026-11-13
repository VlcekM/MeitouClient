using System.Numerics;
using System.Runtime.InteropServices;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// What the traced sun's rays read and write, all at the render size: the rays' mask (visibility, the penumbra's spread), the filter's scratch and
/// its result (<see cref="PenumbraFilter"/>).
/// </summary>
readonly record struct TracedSunTargets(
    GpuTexture Depth, GpuTexture Surface, GpuTexture Term, GpuTexture Mask, GpuTexture Scratch, GpuTexture Filtered, int Width, int Height);

/// <summary>
/// The traced sun's rays (docs/render-shadows.md "Traced sun"): <see cref="TracedSunShaders.Mask"/> over the scene's depth and surface target, one
/// ray a pixel towards the sun's disc, against the traced scene (<see cref="GiScene"/>). Render thread only.
/// </summary>
sealed unsafe class TracedSunPass : IDisposable
{
    /// <summary>
    /// The sun's angular radius the disc is sampled over (tangent): the real sun's, 0.267 degrees (KenshiNative's 0.015, about three suns, widened
    /// far casters' penumbrae as much).
    /// </summary>
    public const float DiscTangent = 0.00465f;

    /// <summary>
    /// The longest ray, within what the traced scene holds around the eye.
    /// </summary>
    const float RayLength = 8000;

    /// <summary>
    /// The device the kernel runs on.
    /// </summary>
    readonly GpuContext _ctx;

    /// <summary>
    /// The rays' program.
    /// </summary>
    readonly GpuProgram _program;

    /// <summary>
    /// The rays' pipeline.
    /// </summary>
    readonly GpuComputePipeline _pipeline;

    /// <summary>
    /// Nearest, clamped: the inputs are read texel by texel.
    /// </summary>
    readonly GpuSampler _sampler;

    /// <summary>
    /// The filter that softens the rays' mask.
    /// </summary>
    readonly PenumbraFilter _penumbra;

    /// <summary>
    /// The rays' kernel and the penumbra filter.
    /// </summary>
    public TracedSunPass(GpuContext ctx)
    {
        _penumbra = new PenumbraFilter(ctx);
        this._ctx = ctx;
        _program = ctx.Shaders.Compute(TracedSunShaders.Mask, "traced sun",
        [
            (0, DescriptorType.CombinedImageSampler),
            (1, DescriptorType.CombinedImageSampler),
            (2, DescriptorType.CombinedImageSampler),
            (3, DescriptorType.StorageImage),
            (4, DescriptorType.AccelerationStructureKhr),
            (5, DescriptorType.StorageBuffer),
            (6, DescriptorType.UniformBuffer),
        ], GpuSetLayout.Bindless);
        _pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_program, "traced sun"));
        _sampler = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
    }

    /// <summary>
    /// The kernel's constants, as <see cref="TracedSunShaders.Mask"/>'s <c>Params</c> reads them.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Params
    {
        /// <summary>
        /// NDC, jittered as the frame was, to the position relative to the eye.
        /// </summary>
        public Matrix4x4 Inverse;

        /// <summary>
        /// The eye relative to the traced origin, and the tangent of the sun's radius.
        /// </summary>
        public Vector4 Eye;

        /// <summary>
        /// Towards the sun, and the ray's length.
        /// </summary>
        public Vector4 Sun;

        /// <summary>
        /// Width, height, the frame, a pixel's width per unit of depth.
        /// </summary>
        public Vector4 Size;

        /// <summary>
        /// The grass's ray constants (<see cref="TracedGrass.RayParams"/>).
        /// </summary>
        public Vector4 Grass;
    }

    /// <summary>
    /// Traces into <see cref="TracedSunTargets.Mask"/>, then filters it into <see cref="TracedSunTargets.Filtered"/>. <paramref name="inverse"/>:
    /// NDC to the position relative to the eye; <paramref name="pixelSlope"/>: a pixel's width per unit of depth. Nothing depends on the frame: a
    /// still view gives the same picture.
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
        var filter = new PenumbraTargets(t.Depth, t.Surface, t.Mask, t.Scratch, t.Filtered, t.Width, t.Height);
        _penumbra.Run(cmd, filter, inverse, pixelSlope);
        cmd.EndLabel();
        _ctx.EndNative(cmd);
    }

    /// <summary>
    /// The kernel and the filter go.
    /// </summary>
    public void Dispose()
    {
        _ctx.Shaders.Dispose(_program);
        _penumbra.Dispose();
    }

    /// <summary>
    /// The rays' descriptors pushed: the depth, the surface, the term, the mask, the top level, the geometry records and the constants; then the
    /// bindless table.
    /// </summary>
    void Bind(CommandList cmd, GiScene scene, in TracedSunTargets t, in Transient constants)
    {
        var depth = Sampled(t.Depth);
        var surface = Sampled(t.Surface);
        var term = Sampled(t.Term);
        var storage = new DescriptorImageInfo(default, _ctx.Device.ViewAt(t.Mask.View().Index), ImageLayout.General);
        var top = scene.Top!.Handle;
        var asWrite = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1,
            PAccelerationStructures = &top,
        };
        var records = new DescriptorBufferInfo(_ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
        var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(Params));

        var writes = stackalloc WriteDescriptorSet[7];
        writes[0] = Image(0, DescriptorType.CombinedImageSampler, &depth);
        writes[1] = Image(1, DescriptorType.CombinedImageSampler, &surface);
        writes[2] = Image(2, DescriptorType.CombinedImageSampler, &term);
        writes[3] = Image(3, DescriptorType.StorageImage, &storage);
        writes[4] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstBinding = 4, DescriptorCount = 1,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
        };
        writes[5] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstBinding = 5, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &records,
        };
        writes[6] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstBinding = 6, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBuffer,
            PBufferInfo = &uniform,
        };

        cmd.PushDescriptors(_program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 7), GpuBindPoint.Compute);
        cmd.BindBindlessTable(_program, 1, GpuBindPoint.Compute);   // the cut-out maps at the candidates
    }

    /// <summary>
    /// A texture read through the nearest sampler.
    /// </summary>
    DescriptorImageInfo Sampled(GpuTexture texture) =>
        new(_ctx.Device.SamplerAt(_sampler.Index), _ctx.Device.ViewAt(texture.View().Index), ImageLayout.General);

    /// <summary>
    /// An image's descriptor write at <paramref name="binding"/>.
    /// </summary>
    static WriteDescriptorSet Image(uint binding, DescriptorType type, DescriptorImageInfo* info) => new()
    {
        SType = StructureType.WriteDescriptorSet,
        DstBinding = binding,
        DescriptorCount = 1,
        DescriptorType = type,
        PImageInfo = info,
    };
}
