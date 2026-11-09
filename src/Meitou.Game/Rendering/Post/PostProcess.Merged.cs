using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Vk = Silk.NET.Vulkan;


namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The merged post passes (<c>post-merge</c>, docs/render-post.md "Merged passes"): the per-pixel full-screen passes that only read a depth or a
/// colour and write a small target are each a render pass with its own barrier; compute kernels do several of them in one dispatch. The velocity
/// kernel makes the motion, the upscalers' depth and the water's reactivity in one pass over the depth buffers (three full-screen passes before), the
/// exposure kernel measures the luminance, reduces it and adapts in one dispatch (a 256 x 256 pass, eight mip blits with their barriers and the
/// adaptation pass before). Both are on the same arithmetic as the passes they replace.
/// </summary>
public sealed unsafe partial class PostProcess
{
    /// <summary>
    /// The merged velocity and exposure kernels (Meitou default); off runs the separate full-screen passes (<c>--ab post-merge</c>).
    /// </summary>
    public bool MergePasses
    {
        get => MergeVelocity && MergeExposure;
        set => MergeVelocity = MergeExposure = value;
    }
    /// <summary>
    /// The velocity kernel alone.
    /// </summary>
    public bool MergeVelocity { get; set; } = true;
    /// <summary>
    /// The exposure kernel alone.
    /// </summary>
    public bool MergeExposure { get; set; } = true;

    GpuProgram? _velocityProgram, _exposureProgram;
    GpuComputePipeline? _velocityPipeline, _exposurePipeline;
    DeviceBuffer? _exposureState;
    bool _exposureStateZeroed;
    GpuSampler _pointSampler;

    GpuSampler Point() => _pointSampler.Index != 0 ? _pointSampler : _pointSampler = Gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest,
            TextureMagFilter.Nearest,
        TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, 1, false, 0));

    /// <summary>
    /// Whether the kernels can run: they write storage images (the formats of the motion, depth, reactivity and adaptation targets).
    /// </summary>
    public bool MergeSupported => SupportsStorage(InternalFormat.Rgba16F) && SupportsStorage(InternalFormat.R32F) && SupportsStorage(InternalFormat.Rg32F);

    [StructLayout(LayoutKind.Sequential)]
    struct VelocityParams
    {
        public Matrix4x4 ToPrev;
        public Vector4 Planes;     // the scene's near and far (zw unused)
        public Vector4 Full;       // the upscalers' planes, the jitter in NDC
        public Vector4 TanEye;     // tan x, tan y, eye height, water height
        public Vector4 Right;      // xyz, the water's reactivity
        public Vector4 Up;         // xyz (w unused)
        public Vector4 Back;       // xyz, the outputs: bit 0 the upscalers' depth, bit 1 the reactivity
        // the direction to the sun, cos of the angle within which the sky takes all of its motion (PostProcess.SunMotion.cs)
        public Vector4 Sun;
        public Vector4 SunMotion;  // the sun's motion in the motion target's units, cos of the angle past which none, 1 when on
    }

    /// <summary>
    /// The motion target (RG motion, B the upscalers' depth, A the reactivity), and where asked the upscalers' depth image and the reactivity image,
    /// in one pass over the depth buffers: <see cref="Velocity"/> with its three modes in one dispatch. <paramref name="depthOut"/> and
    /// <paramref name="reactiveOut"/> may be null. With
    /// <paramref name="cleanColour"/> the scene colour's NaN and Inf become 0 in place, for the vendor upscalers (TAA cleans its own taps; FSR keeps
    /// a NaN in its history).
    /// </summary>
    void VelocityMerged(Target2D motionOut, Target2D? depthOut, Target2D? reactiveOut, bool cleanColour = false)
    {
        if (_velocityProgram is null)
        {
            _velocityProgram = Gpu.Shaders.Compute(MergedShaders.Velocity, "post velocity merged");
            _velocityPipeline = Gpu.Pipelines.Get(new ComputePipelineDesc(_velocityProgram!.Value, "post velocity merged"));
        }
        float tanY = MathF.Tan(_fovNow * 0.5f);
        var r = _viewRotation;
        var p = new VelocityParams
        {
            ToPrev = _toPrevious,
            Planes = new Vector4(_planes.X, _planes.Y, 0, 0),
            Full = new Vector4(UpscaleNear, UpscaleFar, 2 * JitterPixels.X / _width, 2 * JitterPixels.Y / _height),
            TanEye = new Vector4(ViewWindow.Tan(tanY * _aspectNow, tanY), _eyeNow.Y, WaterHeight ?? float.MinValue),
            Right = new Vector4(r.M11, r.M21, r.M31, WaterReactive),
            Up = new Vector4(r.M12, r.M22, r.M32, 0),
            Back = new Vector4(ViewWindow.BackFor(r, _fovNow, _aspectNow),
                    (depthOut is not null ? 1 : 0) | (reactiveOut is not null ? 2 : 0) | (cleanColour ? 4 : 0)),
            Sun = _sunMotion.Sun,
            SunMotion = _sunMotion.Motion,
        };
        var data = Gpu.Frame.Constants.Write(new ReadOnlySpan<VelocityParams>(in p), 256);
        var cmd = Segment();
        var depth = Sampled(_sceneDepth!);
        var bindings = stackalloc GpuBinding[6];
        bindings[0] = GpuBinding.Sampled(0, new SampledTexture(Point(), depth.View));
        bindings[1] = GpuBinding.StorageImage(1, new SampledTexture(default, motionOut.Texture.View()));
        bindings[2] = GpuBinding.StorageImage(2, new SampledTexture(default, (depthOut ?? motionOut).Texture.View()));
        bindings[3] = GpuBinding.StorageImage(3, new SampledTexture(default, (reactiveOut ?? motionOut).Texture.View()));
        bindings[4] = GpuBinding.Storage(4, new GpuBufferBinding(data.Handle, data.Offset, (ulong)sizeof(VelocityParams)));
        bindings[5] = GpuBinding.StorageImage(5, new SampledTexture(default, (cleanColour ? _sceneColour! : motionOut).Texture.View()));
        cmd.Order();
        cmd.BindPipeline(_velocityPipeline!.Value);
        cmd.BindProgramResources(_velocityProgram!.Value, 0, new ReadOnlySpan<GpuBinding>(bindings, 6));
        var size = new Vector2(_width, _height);
        cmd.PushConstants(_velocityProgram!.Value, GpuStage.Compute, in size);
        cmd.Dispatch((uint)((_width + 15) / 16), (uint)((_height + 15) / 16));
        cmd.Order();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ExposurePush
    {
        public float Blend, BandMin, BandMax;
    }

    /// <summary>
    /// The game's exposure measure and adaptation in one dispatch: <see cref="RunExposure"/>'s luminance pass, mip chain and adaptation pass. 256
    /// workgroups of 16 x 16 make one cell of the 256 x 256 luminance each (the same 16 taps), reduce their tile by the mip chain's 2 x 2 means, and
    /// the last to finish reduces the 256 tiles and adapts.
    /// </summary>
    void ExposureMerged(float blend, (float Min, float Max) band)
    {
        if (_exposureProgram is null)
        {
            _exposureProgram = Gpu.Shaders.Compute(MergedShaders.Exposure, "post exposure merged");
            _exposurePipeline = Gpu.Pipelines.Get(new ComputePipelineDesc(_exposureProgram!.Value, "post exposure merged"));
        }
        _exposureState ??= DeviceBuffer.Create(Gpu, 16 + 256 * 4, BufferUse.Storage | BufferUse.TransferDst, "post exposure state");
        var cmd = Segment();
        if (!_exposureStateZeroed)
        {
            cmd.FillBuffer(_exposureState.Handle, 0, _exposureState.Size, 0);
            _exposureStateZeroed = true;
        }
        var scene = Sampled(_postColour);
        var bindings = stackalloc GpuBinding[4];
        bindings[0] = GpuBinding.Sampled(0, new SampledTexture(scene.Sampler, scene.View));
        bindings[1] = GpuBinding.StorageImage(1, new SampledTexture(default, _adaptA!.Texture.View()));
        bindings[2] = GpuBinding.StorageImage(2, new SampledTexture(default, _adaptB!.Texture.View()));
        bindings[3] = GpuBinding.Storage(3, new GpuBufferBinding(_exposureState.Handle, 0, _exposureState.Size));
        cmd.Order();
        cmd.BindPipeline(_exposurePipeline!.Value);
        cmd.BindProgramResources(_exposureProgram!.Value, 0, new ReadOnlySpan<GpuBinding>(bindings, 4));
        var push = new ExposurePush { Blend = blend, BandMin = band.Min, BandMax = band.Max };
        cmd.PushConstants(_exposureProgram!.Value, GpuStage.Compute, in push);
        cmd.Dispatch(16, 16);
        cmd.Order();
    }

    void DisposeMerged()
    {
        _exposureState?.Dispose();
        if (_velocityProgram is { } vp) Gpu.Shaders.Dispose(vp);
        if (_exposureProgram is { } ep) Gpu.Shaders.Dispose(ep);
    }
}

/// <summary>
/// The merged post kernels (<see cref="PostProcess.MergePasses"/>).
/// </summary>
static class MergedShaders
{
    /// <summary>
    /// <see cref="UpscaleShaders.Velocity"/>'s three modes in one dispatch (bit 0 of the outputs: the upscalers' depth image, bit 1: the reactivity
    /// image, bit 2: the scene colour's NaN and Inf to 0 in place; the motion image always, its NaN and Inf as 0).
    /// </summary>
    public const string Velocity = """
        #version 450
        layout(local_size_x = 16, local_size_y = 16) in;
        layout(set = 0, binding = 0) uniform sampler2D uNearDepth;
        layout(set = 0, binding = 1, rgba16f) writeonly uniform image2D uMotion;
        layout(set = 0, binding = 2, r32f) writeonly uniform image2D uUpscaleDepth;
        layout(set = 0, binding = 3, r32f) writeonly uniform image2D uReactive;
        layout(std430, set = 0, binding = 4) readonly buffer Params
        {
            mat4 toPrev;
            vec4 planes; vec4 full; vec4 tanEye; vec4 right; vec4 up; vec4 back; vec4 sun; vec4 sunMotion;
        } p;
        layout(set = 0, binding = 5, rgba16f) uniform image2D uColour;
        layout(push_constant) uniform Push { vec2 size; } pc;
        """ + DepthConvention.Glsl + """

        float finite(float v) { return isnan(v) || isinf(v) ? 0.0 : v; }

        void main()
        {
            ivec2 px = ivec2(gl_GlobalInvocationID.xy);
            if (px.x >= int(pc.size.x) || px.y >= int(pc.size.y)) return;
            vec2 uv = (vec2(px) + 0.5) / pc.size;
            float d = texelFetch(uNearDepth, px, 0).r;
            float z = depthViewZ(d, p.planes.xy);
            float depth = clamp(p.full.y * (z - p.full.x) / (z * (p.full.y - p.full.x)), 0.0, 1.0);
            vec2 ndc = uv * 2.0 - 1.0;
            vec2 tanXY = p.tanEye.xy;
            vec3 offset = (p.right.xyz * ndc.x * tanXY.x + p.up.xyz * ndc.y * tanXY.y - p.back.xyz) * z;
            float reactive = p.tanEye.z > p.tanEye.w && p.tanEye.z + offset.y < p.tanEye.w ? p.right.w : 0.0;
            vec4 prev = p.toPrev * vec4(ndc, d, 1.0);
            vec2 motion = ((ndc - p.full.zw) - prev.xy / prev.w) * 0.5;
            // The sky round the sun moves with the sun (PostProcess.SunMotion.cs).
            if (p.sunMotion.w != 0.0 && depthIsSky(d))
                motion += p.sunMotion.xy * smoothstep(p.sunMotion.z, p.sun.w, dot(normalize(offset), p.sun.xyz));
            imageStore(uMotion, px, vec4(finite(motion.x), finite(motion.y), depth, reactive));
            int outputs = int(p.back.w);
            if ((outputs & 4) != 0)
            {
                vec4 c = imageLoad(uColour, px);
                if (any(isnan(c)) || any(isinf(c))) imageStore(uColour, px, vec4(finite(c.r), finite(c.g), finite(c.b), finite(c.a)));
            }
            if ((outputs & 1) != 0) imageStore(uUpscaleDepth, px, vec4(depth, 0.0, 0.0, 1.0));
            if ((outputs & 2) != 0) imageStore(uReactive, px, vec4(reactive, 0.0, 0.0, 1.0));
        }
        """;

    /// <summary>
    /// <see cref="PostProcessShaders.Luminance"/>, its mip chain and <see cref="PostProcessShaders.Adapt"/>: one thread per cell of the 256 x 256
    /// luminance (the same 16 taps, in the same order), a 16 x 16 tile reduced by 2 x 2 means as the mip chain does, the last tile to finish (an
    /// atomic counter, reset by it) reducing the 256 tile means and adapting.
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
                float smoothMean = mix(imageLoad(uLast, ivec2(0)).g, total, pc.blend);   // the mean smoothed without the band (the night grading reads it)
                imageStore(uAdapted, ivec2(0), vec4(clamp(adapted, pc.bandMin, max(pc.bandMin, pc.bandMax)), smoothMean, 0.0, 1.0));
            }
        }
        """;
}