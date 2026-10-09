using System.Numerics;
using System.Runtime.InteropServices;

using Meitou.Rendering.Gpu;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Rendering;

/// <summary>
/// The merged post passes (<c>post-merge</c>, docs/render-post.md "Merged passes"): the per-pixel full-screen passes that only read a depth or a colour and write a small
/// target are each a render pass with its own barrier; compute kernels do several of them in one dispatch. The velocity kernel makes the motion, the upscalers' depth and the
/// water's reactivity in one pass over the depth buffers (three full-screen passes before), the exposure kernel measures the luminance, reduces it and adapts in one dispatch
/// (a 256 x 256 pass, eight mip blits with their barriers and the adaptation pass before). Both are on the same arithmetic as the passes they replace.
/// </summary>
public sealed unsafe partial class PostProcess
{
    /// <summary>The merged velocity and exposure kernels (Meitou default); off runs the separate full-screen passes. <c>MEITOU_POST_MERGE=0</c> starts with them off.</summary>
    public bool MergePasses
    {
        get => MergeVelocity && MergeExposure;
        set => MergeVelocity = MergeExposure = value;
    }
    /// <summary>The velocity kernel alone (<c>MEITOU_POST_MERGE=velocity</c> or <c>=0</c> starts with it off).</summary>
    public bool MergeVelocity { get; set; } = Environment.GetEnvironmentVariable("MEITOU_POST_MERGE") is not ("0" or "exposure");
    /// <summary>The exposure kernel alone.</summary>
    public bool MergeExposure { get; set; } = Environment.GetEnvironmentVariable("MEITOU_POST_MERGE") is not ("0" or "velocity");

    ShaderProgram? velocityProgram, exposureProgram;
    ComputePipeline? velocityPipeline, exposurePipeline;
    DeviceBuffer? exposureState;
    bool exposureStateZeroed;
    Vk.Sampler pointSampler;

    Vk.Sampler Point() => pointSampler.Handle != 0 ? pointSampler : pointSampler = Gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest,
        TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, 1, false, 0));

    /// <summary>Whether the kernels can run: they write storage images (the formats of the motion, depth, reactivity and adaptation targets).</summary>
    public bool MergeSupported => SupportsStorage(Vk.Format.R16G16B16A16Sfloat) && SupportsStorage(Vk.Format.R32Sfloat) && SupportsStorage(Vk.Format.R32G32Sfloat);

    [StructLayout(LayoutKind.Sequential)]
    struct VelocityParams
    {
        public Matrix4x4 NearToPrev, FarToPrev;
        public Vector4 Planes;     // near slice, far slice
        public Vector4 Full;       // the upscalers' planes, the jitter in NDC
        public Vector4 TanEye;     // tan x, tan y, eye height, water height
        public Vector4 Right;      // xyz, the water's reactivity
        public Vector4 Up;         // xyz, 1 when a far slice was drawn
        public Vector4 Back;       // xyz, the outputs: bit 0 the upscalers' depth, bit 1 the reactivity
    }

    /// <summary>
    /// The motion target (RG motion, B the upscalers' depth, A the reactivity), and where asked the upscalers' depth image and the reactivity image, in one pass over the depth
    /// buffers: <see cref="Velocity"/> with its three modes in one dispatch. <paramref name="depthOut"/> and <paramref name="reactiveOut"/> may be null.
    /// </summary>
    void VelocityMerged(Target2D motionOut, Target2D? depthOut, Target2D? reactiveOut)
    {
        if (velocityProgram is null)
        {
            velocityProgram = Gpu.Shaders.Compute(MergedShaders.Velocity, "post velocity merged");
            velocityPipeline = Gpu.Pipelines.Get(new ComputePipelineDesc(velocityProgram, "post velocity merged"));
        }
        float tanY = MathF.Tan(fovNow * 0.5f);
        var r = viewRotation;
        var p = new VelocityParams
        {
            NearToPrev = nearToPrevious, FarToPrev = farToPrevious,
            Planes = new Vector4(nearPlanes.X, nearPlanes.Y, farPlanes.X, farPlanes.Y),
            Full = new Vector4(UpscaleNear, UpscaleFar, 2 * JitterPixels.X / width, 2 * JitterPixels.Y / height),
            TanEye = new Vector4(tanY * aspectNow, tanY, eyeNow.Y, WaterHeight ?? float.MinValue),
            Right = new Vector4(r.M11, r.M21, r.M31, WaterReactive),
            Up = new Vector4(r.M12, r.M22, r.M32, farSliceDrawn ? 1 : 0),
            Back = new Vector4(r.M13, r.M23, r.M33, (depthOut is not null ? 1 : 0) | (reactiveOut is not null ? 2 : 0)),
        };
        var data = Gpu.Frame.Constants.Write(new ReadOnlySpan<VelocityParams>(in p), 256);
        var cmd = Segment();
        var near = Sampled(sceneDepth!);
        var far = Sampled(farDepth!);
        var infos = stackalloc Vk.DescriptorImageInfo[5];
        infos[0] = new Vk.DescriptorImageInfo(Point(), near.View, Vk.ImageLayout.General);
        infos[1] = new Vk.DescriptorImageInfo(Point(), far.View, Vk.ImageLayout.General);
        infos[2] = new Vk.DescriptorImageInfo(default, motionOut.Texture.View(), Vk.ImageLayout.General);
        infos[3] = new Vk.DescriptorImageInfo(default, (depthOut ?? motionOut).Texture.View(), Vk.ImageLayout.General);
        infos[4] = new Vk.DescriptorImageInfo(default, (reactiveOut ?? motionOut).Texture.View(), Vk.ImageLayout.General);
        var buffer = new Vk.DescriptorBufferInfo(data.Handle, data.Offset, (ulong)sizeof(VelocityParams));
        var writes = stackalloc Vk.WriteDescriptorSet[6];
        for (int i = 0; i < 5; i++)
            writes[i] = new Vk.WriteDescriptorSet
            {
                SType = Vk.StructureType.WriteDescriptorSet, DstBinding = (uint)i, DescriptorCount = 1, PImageInfo = &infos[i],
                DescriptorType = i < 2 ? Vk.DescriptorType.CombinedImageSampler : Vk.DescriptorType.StorageImage,
            };
        writes[5] = new Vk.WriteDescriptorSet { SType = Vk.StructureType.WriteDescriptorSet, DstBinding = 5, DescriptorCount = 1, DescriptorType = Vk.DescriptorType.StorageBuffer, PBufferInfo = &buffer };
        cmd.Barrier(BarrierBatch.Full);
        cmd.BindPipeline(velocityPipeline!);
        cmd.PushDescriptors(velocityProgram.Layout, 0, new ReadOnlySpan<Vk.WriteDescriptorSet>(writes, 6), Vk.PipelineBindPoint.Compute);
        var size = new Vector2(width, height);
        cmd.PushConstants(velocityProgram.Layout, Vk.ShaderStageFlags.ComputeBit, in size);
        cmd.Dispatch((uint)((width + 15) / 16), (uint)((height + 15) / 16));
        cmd.Barrier(BarrierBatch.Full);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ExposurePush
    {
        public float Blend, BandMin, BandMax;
    }

    /// <summary>
    /// The game's exposure measure and adaptation in one dispatch: <see cref="RunExposure"/>'s luminance pass, mip chain and adaptation pass. 256 workgroups of 16 x 16 make one
    /// cell of the 256 x 256 luminance each (the same 16 taps), reduce their tile by the mip chain's 2 x 2 means, and the last to finish reduces the 256 tiles and adapts.
    /// </summary>
    void ExposureMerged(float blend, (float Min, float Max) band)
    {
        if (exposureProgram is null)
        {
            exposureProgram = Gpu.Shaders.Compute(MergedShaders.Exposure, "post exposure merged");
            exposurePipeline = Gpu.Pipelines.Get(new ComputePipelineDesc(exposureProgram, "post exposure merged"));
        }
        exposureState ??= DeviceBuffer.Create(Gpu, 16 + 256 * 4, BufferUse.Storage | BufferUse.TransferDst, "post exposure state");
        var cmd = Segment();
        if (!exposureStateZeroed)
        {
            cmd.FillBuffer(exposureState.Handle, 0, exposureState.Size, 0);
            exposureStateZeroed = true;
        }
        var scene = Sampled(postColour);
        var infos = stackalloc Vk.DescriptorImageInfo[3];
        infos[0] = new Vk.DescriptorImageInfo(scene.Sampler, scene.View, Vk.ImageLayout.General);
        infos[1] = new Vk.DescriptorImageInfo(default, adaptA!.Texture.View(), Vk.ImageLayout.General);
        infos[2] = new Vk.DescriptorImageInfo(default, adaptB!.Texture.View(), Vk.ImageLayout.General);
        var buffer = new Vk.DescriptorBufferInfo(exposureState.Handle, 0, exposureState.Size);
        var writes = stackalloc Vk.WriteDescriptorSet[4];
        for (int i = 0; i < 3; i++)
            writes[i] = new Vk.WriteDescriptorSet
            {
                SType = Vk.StructureType.WriteDescriptorSet, DstBinding = (uint)i, DescriptorCount = 1, PImageInfo = &infos[i],
                DescriptorType = i == 0 ? Vk.DescriptorType.CombinedImageSampler : Vk.DescriptorType.StorageImage,
            };
        writes[3] = new Vk.WriteDescriptorSet { SType = Vk.StructureType.WriteDescriptorSet, DstBinding = 3, DescriptorCount = 1, DescriptorType = Vk.DescriptorType.StorageBuffer, PBufferInfo = &buffer };
        cmd.Barrier(BarrierBatch.Full);
        cmd.BindPipeline(exposurePipeline!);
        cmd.PushDescriptors(exposureProgram.Layout, 0, new ReadOnlySpan<Vk.WriteDescriptorSet>(writes, 4), Vk.PipelineBindPoint.Compute);
        var push = new ExposurePush { Blend = blend, BandMin = band.Min, BandMax = band.Max };
        cmd.PushConstants(exposureProgram.Layout, Vk.ShaderStageFlags.ComputeBit, in push);
        cmd.Dispatch(16, 16);
        cmd.Barrier(BarrierBatch.Full);
    }

    void DisposeMerged()
    {
        exposureState?.Dispose();
        velocityProgram?.Dispose();
        exposureProgram?.Dispose();
    }
}

/// <summary>The merged post kernels (<see cref="PostProcess.MergePasses"/>).</summary>
static class MergedShaders
{
    /// <summary><see cref="UpscaleShaders.Velocity"/>'s three modes in one dispatch (bit 0 of the outputs: the upscalers' depth image, bit 1: the reactivity image; the motion image always).</summary>
    public const string Velocity = """
        #version 450
        layout(local_size_x = 16, local_size_y = 16) in;
        layout(set = 0, binding = 0) uniform sampler2D uNearDepth;
        layout(set = 0, binding = 1) uniform sampler2D uFarDepth;
        layout(set = 0, binding = 2, rgba16f) writeonly uniform image2D uMotion;
        layout(set = 0, binding = 3, r32f) writeonly uniform image2D uUpscaleDepth;
        layout(set = 0, binding = 4, r32f) writeonly uniform image2D uReactive;
        layout(std430, set = 0, binding = 5) readonly buffer Params
        {
            mat4 nearToPrev; mat4 farToPrev;
            vec4 planes; vec4 full; vec4 tanEye; vec4 right; vec4 up; vec4 back;
        } p;
        layout(push_constant) uniform Push { vec2 size; } pc;

        float viewZ(float d, vec2 nf) { float zd = 2.0 * d - 1.0; return nf.x * nf.y / (nf.y - zd * (nf.y - nf.x)); }

        void main()
        {
            ivec2 px = ivec2(gl_GlobalInvocationID.xy);
            if (px.x >= int(pc.size.x) || px.y >= int(pc.size.y)) return;
            vec2 uv = (vec2(px) + 0.5) / pc.size;
            float d = texelFetch(uNearDepth, px, 0).r;
            mat4 toPrev = p.nearToPrev;
            vec2 planes = p.planes.xy;
            if (d >= 1.0 && p.up.w != 0.0)
            {
                d = texelFetch(uFarDepth, px, 0).r;
                toPrev = p.farToPrev;
                planes = p.planes.zw;
            }
            float z = viewZ(d, planes);
            float depth = clamp(p.full.y * (z - p.full.x) / (z * (p.full.y - p.full.x)), 0.0, 1.0);
            vec2 ndc = uv * 2.0 - 1.0;
            vec2 tanXY = p.tanEye.xy;
            vec3 offset = (p.right.xyz * ndc.x * tanXY.x + p.up.xyz * ndc.y * tanXY.y - p.back.xyz) * z;
            float reactive = p.tanEye.z > p.tanEye.w && p.tanEye.z + offset.y < p.tanEye.w ? p.right.w : 0.0;
            vec4 prev = toPrev * vec4(ndc, 2.0 * d - 1.0, 1.0);
            vec2 motion = ((ndc - p.full.zw) - prev.xy / prev.w) * 0.5;
            imageStore(uMotion, px, vec4(motion, depth, reactive));
            int outputs = int(p.back.w);
            if ((outputs & 1) != 0) imageStore(uUpscaleDepth, px, vec4(depth, 0.0, 0.0, 1.0));
            if ((outputs & 2) != 0) imageStore(uReactive, px, vec4(reactive, 0.0, 0.0, 1.0));
        }
        """;

    /// <summary>
    /// <see cref="PostProcessShaders.Luminance"/>, its mip chain and <see cref="PostProcessShaders.Adapt"/>: one thread per cell of the 256 x 256 luminance (the same 16 taps, in the
    /// same order), a 16 x 16 tile reduced by 2 x 2 means as the mip chain does, the last tile to finish (an atomic counter, reset by it) reducing the 256 tile means and adapting.
    /// </summary>
    public const string Exposure = """
        #version 450
        layout(local_size_x = 16, local_size_y = 16) in;
        layout(set = 0, binding = 0) uniform sampler2D uScene;
        layout(set = 0, binding = 1, rg32f) readonly uniform image2D uLast;
        layout(set = 0, binding = 2, rg32f) writeonly uniform image2D uAdapted;
        layout(std430, set = 0, binding = 3) coherent buffer State { uint counter; uint pad0; uint pad1; uint pad2; float tiles[256]; } st;
        layout(push_constant) uniform Push { float blend; float bandMin; float bandMax; } pc;
        shared float tile[256];
        shared uint finished;

        // The 2 x 2 means of the mip chain down to one value, in the tile.
        float reduce(uvec2 l)
        {
            for (uint s = 8u; s >= 1u; s >>= 1u)
            {
                float a = 0.0;
                bool live = l.x < s && l.y < s;
                if (live) a = 0.25 * (tile[(2u * l.y) * 16u + 2u * l.x] + tile[(2u * l.y) * 16u + 2u * l.x + 1u] + tile[(2u * l.y + 1u) * 16u + 2u * l.x] + tile[(2u * l.y + 1u) * 16u + 2u * l.x + 1u]);
                barrier();
                if (live) tile[l.y * 16u + l.x] = a;
                barrier();
            }
            return tile[0];
        }

        void main()
        {
            uvec2 l = gl_LocalInvocationID.xy;
            uint li = l.y * 16u + l.x;
            vec2 cell = vec2(1.0 / 256.0);
            vec2 centre = (vec2(gl_GlobalInvocationID.xy) + 0.5) * cell;
            float sum = 0.0;
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                {
                    vec2 uv = centre + (vec2(x, y) - 1.5) * 0.25 * cell;
                    sum += max(dot(textureLod(uScene, uv, 0.0).rgb, vec3(0.299, 0.587, 0.114)), 0.0001);
                }
            tile[li] = sum / 16.0;
            barrier();
            float mean = reduce(l);
            if (li == 0u)
            {
                st.tiles[gl_WorkGroupID.y * 16u + gl_WorkGroupID.x] = mean;
                memoryBarrierBuffer();
                finished = atomicAdd(st.counter, 1u) == 255u ? 1u : 0u;
            }
            barrier();
            if (finished == 0u) return;
            memoryBarrierBuffer();
            tile[li] = st.tiles[li];
            barrier();
            float total = reduce(l);
            if (li == 0u)
            {
                st.counter = 0u;
                float last = imageLoad(uLast, ivec2(0)).r;
                float adapted = mix(last, total, pc.blend);
                imageStore(uAdapted, ivec2(0), vec4(clamp(adapted, pc.bandMin, max(pc.bandMin, pc.bandMax)), total, 0.0, 1.0));
            }
        }
        """;
}
