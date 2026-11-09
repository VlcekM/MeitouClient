using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Terrain;
using Silk.NET.Vulkan;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// The global illumination's debug view (<c>--gi-debug &lt;mode&gt;</c>, docs/render-gi.md "Debug views"): <see cref="GiShaders.Debug"/> over the
/// scene
/// depth into a picture of the render size, scaled over the finished frame. Render thread only.
/// </summary>
sealed unsafe class GiDebugPass : IDisposable
{
    readonly GpuContext _ctx;
    readonly GpuProgram _program;
    readonly GpuComputePipeline _pipeline;
    readonly GpuSampler _sampler;
    GpuTexture? _output;
    uint _frame;

    /// <summary>
    /// Bounce rays per pixel in mode 2, their length, the exposure of the picture and the grey of every surface.
    /// </summary>
    public int Samples { get; set; } = 4;
    public float RayLength { get; set; } = 4000;
    public float Exposure { get; set; } = 0.35f;
    public float Albedo { get; set; } = 0.5f;

    public GiDebugPass(GpuContext ctx)
    {
        this._ctx = ctx;
        _program = ctx.Shaders.Compute(GiShaders.Debug, "gi debug",
        [
            (0, DescriptorType.CombinedImageSampler), (1, DescriptorType.StorageImage), (2, DescriptorType.AccelerationStructureKhr),
            (3, DescriptorType.StorageBuffer), (4, DescriptorType.UniformBuffer),
        ]);
        _pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_program, "gi debug"));
        _sampler = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge,
                TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Params
    {
        public Matrix4x4 Inverse;
        public Vector4 Eye, SunDirection, SunColour, SkyColour, GroundColour;
        public uint Width, Height, Mode, Frame;
        public Vector4 Extra;
    }

    /// <summary>
    /// Traces <paramref name="mode"/> over <paramref name="depth"/> (the scene's, <paramref name="width"/> × <paramref name="height"/>) and scales
    /// the
    /// result over <paramref name="target"/>. <paramref name="inverse"/>: NDC to the position relative to the eye; <paramref name="eye"/> in world
    /// space.
    /// </summary>
    public void Run(int mode, GiScene scene, GpuTexture depth, GpuTexture target, int width, int height, Matrix4x4 inverse, Vector3 eye,
            WorldLighting light)
    {
        if (scene.Top is null) return;
        if (_output is null || _output.Desc.Width != width || _output.Desc.Height != height)
        {
            _output?.Dispose();
            _output = _ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16F, width, height,
                    Use: TextureUse.Storage | TextureUse.Sampled | TextureUse.TransferSrc, Name: "gi debug"));
        }
        var parameters = new Params
        {
            Inverse = inverse,
            Eye = new Vector4(eye - scene.Origin, 0),
            SunDirection = new Vector4(light.SunDirection, RayLength),
            SunColour = new Vector4(light.SunColour, 0),
            SkyColour = new Vector4(light.AmbientSky, 0),
            GroundColour = new Vector4(light.AmbientGround, 0),
            Width = (uint)width,
            Height = (uint)height,
            Mode = (uint)mode,
            Frame = _frame++,
            Extra = new Vector4(Samples, Exposure, Albedo, 0),
        };
        var constants = _ctx.Frame.Constants.Write<Params>(new ReadOnlySpan<Params>(&parameters, 1), 256);

        var cmd = (CommandList)_ctx.BeginNative("gi debug");
        cmd.BeginLabel("gi debug");
        ((CommandList)cmd).Barrier(BarrierBatch.Full);
        var image = new DescriptorImageInfo(_ctx.Device.SamplerAt(_sampler.Index), _ctx.Device.ViewAt(depth.View().Index), ImageLayout.General);
        var storage = new DescriptorImageInfo(default, _ctx.Device.ViewAt(_output.View().Index), ImageLayout.General);
        var top = scene.Top.Handle;
        var asWrite = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr, AccelerationStructureCount = 1,
            PAccelerationStructures = &top,
        };
        var records = new DescriptorBufferInfo(_ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
        var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(Params));
        var writes = stackalloc WriteDescriptorSet[5];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstBinding = 0, DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &image,
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstBinding = 1, DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage, PImageInfo = &storage,
        };
        writes[2] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstBinding = 2, DescriptorCount = 1,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
        };
        writes[3] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstBinding = 3, DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &records,
        };
        writes[4] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstBinding = 4, DescriptorCount = 1,
            DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = &uniform,
        };
        cmd.BindPipeline(_pipeline);
        ((CommandList)cmd).PushDescriptors(_program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 5), GpuBindPoint.Compute);
        cmd.Dispatch((uint)((width + 7) / 8), (uint)((height + 7) / 8));
        ((CommandList)cmd).Barrier(BarrierBatch.Full);
        cmd.Blit(_output, target, TextureMinFilter.Linear);
        ((CommandList)cmd).Barrier(BarrierBatch.Full);
        cmd.EndLabel();
        _ctx.EndNative(cmd);
    }

    public void Dispose()
    {
        _output?.Dispose();
        _ctx.Shaders.Dispose(_program);
    }
}