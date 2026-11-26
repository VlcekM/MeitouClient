using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Target2D = Meitou.Game.Rendering.Post.PostProcess.Target2D;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The merged post passes (<c>post-merge</c>, docs/render-post.md "Merged passes"): the per-pixel full-screen passes that only read a depth or a
/// colour and write a small target are each a render pass with its own barrier; compute kernels do several of them in one dispatch. The velocity
/// kernel makes the motion, the upscalers' depth and the water's reactivity in one pass over the depth buffers (three full-screen passes before), the
/// exposure kernel measures the luminance, reduces it and adapts in one dispatch (a 256 x 256 pass, eight mip blits with their barriers and the
/// adaptation pass before). Both are on the same arithmetic as the passes they replace. <see cref="PostProcess"/> keeps the switches and decides
/// when they run; this keeps the kernels' programs and the exposure's state.
/// </summary>
sealed unsafe class MergedKernels(PostProcess post)
{
    #region Velocity

    /// <summary>
    /// The velocity kernel's program (made with its first run).
    /// </summary>
    GpuProgram? _velocityProgram;

    /// <summary>
    /// The velocity kernel's pipeline (made with its first run).
    /// </summary>
    GpuComputePipeline? _velocityPipeline;

    /// <summary>
    /// The velocity kernel's parameters (a storage buffer in the frame's constants).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct VelocityParams
    {
        /// <summary>
        /// The reprojection to last frame's clip space.
        /// </summary>
        public Matrix4x4 ToPrev;
        /// <summary>
        /// The scene's near and far planes (zw unused).
        /// </summary>
        public Vector4 Planes;
        /// <summary>
        /// The upscalers' depth planes, then the jitter in NDC.
        /// </summary>
        public Vector4 Full;
        /// <summary>
        /// Tan x, tan y, the eye's height and the water's height.
        /// </summary>
        public Vector4 TanEye;
        /// <summary>
        /// The view's right axis (xyz) and the water's reactivity.
        /// </summary>
        public Vector4 Right;
        /// <summary>
        /// The view's up axis (w unused).
        /// </summary>
        public Vector4 Up;
        /// <summary>
        /// The view's back axis (xyz) and the outputs: bit 0 the upscalers' depth, bit 1 the reactivity.
        /// </summary>
        public Vector4 Back;
        /// <summary>
        /// The direction to the sun (xyz) and the cos of the angle within which the sky takes all of its motion (PostProcess.SunMotion.cs).
        /// </summary>
        public Vector4 Sun;
        /// <summary>
        /// The sun's motion in the motion target's units, the cos of the angle past which there is none, and 1 when on.
        /// </summary>
        public Vector4 SunMotion;
    }

    /// <summary>
    /// The motion target (RG motion, B the upscalers' depth, A the reactivity), and where asked the upscalers' depth image and the reactivity image,
    /// in one pass over the depth buffers: <see cref="MergedShaders.Velocity"/> with its three modes in one dispatch. <paramref name="depthOut"/> and
    /// <paramref name="reactiveOut"/> may be null. With
    /// <paramref name="cleanColour"/> the scene colour's NaN and Inf become 0 in place, for the vendor upscalers (TAA cleans its own taps; FSR keeps
    /// a NaN in its history).
    /// </summary>
    public void Velocity(Target2D motionOut, Target2D? depthOut, Target2D? reactiveOut, bool cleanColour = false)
    {
        if (_velocityProgram is null)
        {
            _velocityProgram = post.Gpu.Shaders.Compute(MergedShaders.Velocity, "post velocity merged");
            _velocityPipeline = post.Gpu.Pipelines.Get(new ComputePipelineDesc(_velocityProgram!.Value, "post velocity merged"));
        }
        float tanY = MathF.Tan(post.FovNow * 0.5f);
        var (planes, jitter, sunMotion) = (post.Planes, post.JitterPixels, post.SunMotionNow);
        var (width, height) = (post.RenderWidth, post.RenderHeight);
        var r = post.ViewRotation;
        var p = new VelocityParams
        {
            ToPrev = post.PreviousReprojection,
            Planes = new Vector4(planes.X, planes.Y, 0, 0),
            Full = new Vector4(PostProcess.UpscaleNear, PostProcess.UpscaleFar, 2 * jitter.X / width, 2 * jitter.Y / height),
            TanEye = new Vector4(ViewWindow.Tan(tanY * post.AspectNow, tanY), post.EyeNow.Y, post.WaterHeight ?? float.MinValue),
            Right = new Vector4(r.M11, r.M21, r.M31, PostProcess.WaterReactive),
            Up = new Vector4(r.M12, r.M22, r.M32, 0),
            Back = new Vector4(ViewWindow.BackFor(r, post.FovNow, post.AspectNow),
                    (depthOut is not null ? 1 : 0) | (reactiveOut is not null ? 2 : 0) | (cleanColour ? 4 : 0)),
            Sun = sunMotion.Sun,
            SunMotion = sunMotion.Motion,
        };
        var data = post.Gpu.Frame.Constants.Write(new ReadOnlySpan<VelocityParams>(in p), 256);
        var cmd = post.Segment();
        var depth = post.Sampled(post.SceneDepthTarget!);
        var bindings = stackalloc GpuBinding[6];
        bindings[0] = GpuBinding.Sampled(0, new SampledTexture(post.Point(), depth.View));
        bindings[1] = GpuBinding.StorageImage(1, new SampledTexture(default, motionOut.Texture.View()));
        bindings[2] = GpuBinding.StorageImage(2, new SampledTexture(default, (depthOut ?? motionOut).Texture.View()));
        bindings[3] = GpuBinding.StorageImage(3, new SampledTexture(default, (reactiveOut ?? motionOut).Texture.View()));
        bindings[4] = GpuBinding.Storage(4, new GpuBufferBinding(data.Handle, data.Offset, (ulong)sizeof(VelocityParams)));
        bindings[5] = GpuBinding.StorageImage(5, new SampledTexture(default, (cleanColour ? post.SceneColourTarget! : motionOut).Texture.View()));
        cmd.Order();
        cmd.BindPipeline(_velocityPipeline!.Value);
        cmd.BindProgramResources(_velocityProgram!.Value, 0, new ReadOnlySpan<GpuBinding>(bindings, 6));
        var size = new Vector2(width, height);
        cmd.PushConstants(_velocityProgram!.Value, GpuStage.Compute, in size);
        cmd.Dispatch((uint)((width + 15) / 16), (uint)((height + 15) / 16));
        cmd.Order();
    }

    #endregion

    #region Exposure

    /// <summary>
    /// The exposure kernel's program (made with its first run).
    /// </summary>
    GpuProgram? _exposureProgram, _exposureWeightedProgram, _gainProgram;

    /// <summary>
    /// The exposure kernel's pipeline (made with its first run).
    /// </summary>
    GpuComputePipeline? _exposurePipeline, _exposureWeightedPipeline, _gainPipeline;

    /// <summary>
    /// The kernel's state across its workgroups (cleared once, then kept by the kernel).
    /// </summary>
    DeviceBuffer? _exposureState;

    /// <summary>
    /// Whether <see cref="_exposureState"/> was cleared.
    /// </summary>
    bool _exposureStateZeroed;

    /// <summary>
    /// The exposure kernel's push constants: the adaptation's blend and the band it is clamped to.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct ExposurePush
    {
        /// <summary>
        /// The adaptation's blend and the band's minimum and maximum.
        /// </summary>
        public float Blend, BandMin, BandMax;

        /// <summary>
        /// Above 0, an interior in view: the minimum follows the mean times this down, to no less than <see cref="IndoorFloor"/> of itself.
        /// </summary>
        public float Indoor, IndoorFloor;

        /// <summary>
        /// The most an interior's pixels are scaled by (<see cref="PostProcess.IndoorExposureMaxGain"/>).
        /// </summary>
        public float MaxGain;

        /// <summary>
        /// Adaptive exposure (<see cref="PostProcess.ShadeExposure"/>): under the band's minimum the target is the mean times this scale, no lower
        /// than the floor; scale 0 keeps the game's band.
        /// </summary>
        public float ShadeScale, ShadeFloor;
    }

    /// <summary>
    /// The game's exposure measure and adaptation in one dispatch: the post chain's luminance pass, mip chain and adaptation pass. 256
    /// workgroups of 16 x 16 make one cell of the 256 x 256 luminance each (the same 16 taps), reduce their tile by the mip chain's 2 x 2 means, and
    /// the last to finish reduces the 256 tiles and adapts. <paramref name="scene"/>: the picture measured; <paramref name="adaptA"/>: last
    /// frame's adaptation, <paramref name="adaptB"/>: this frame's.
    /// </summary>
    /// <param name="rooms">With <paramref name="indoor"/> above 0: the rooms in view (<see cref="Gi.RoomMaskPass"/>: 1 on a room's pixel). The
    /// rest alone sets the adapted luminance, by the game's rule; the rooms' own mean sets what their pixels are scaled by before the upscaler
    /// (<see cref="InteriorGain"/>, kept in the state buffer), so a room is read at its own exposure and the street at the street's.</param>
    /// <param name="shade">Adaptive exposure's scale and floor (<see cref="PostProcess.ShadeExposure"/>); scale 0: the game's band.</param>
    public void Exposure(float blend, (float Min, float Max) band, Target2D scene, Target2D adaptA, Target2D adaptB, float indoor = 0,
        float indoorFloor = PostProcess.IndoorExposureFloor, Target2D? rooms = null, (float Scale, float Floor) shade = default)
    {
        bool weighted = indoor > 0 && rooms is not null;
        ref var program = ref weighted ? ref _exposureWeightedProgram : ref _exposureProgram;
        ref var pipeline = ref weighted ? ref _exposureWeightedPipeline : ref _exposurePipeline;
        if (program is null)
        {
            string name = weighted ? "post exposure merged weighted" : "post exposure merged";
            program = post.Gpu.Shaders.Compute(MergedShaders.ExposureFor(weighted), name);
            pipeline = post.Gpu.Pipelines.Get(new ComputePipelineDesc(program!.Value, name));
        }
        _exposureState ??= DeviceBuffer.Create(post.Gpu, 16 + 256 * 16, BufferUse.Storage | BufferUse.TransferDst, "post exposure state");
        var cmd = post.Segment();
        if (!_exposureStateZeroed)
        {
            cmd.FillBuffer(_exposureState.Handle, 0, _exposureState.Size, 0);
            _exposureStateZeroed = true;
        }
        var sampled = post.Sampled(scene);
        var bindings = stackalloc GpuBinding[5];
        bindings[0] = GpuBinding.Sampled(0, new SampledTexture(sampled.Sampler, sampled.View));
        bindings[1] = GpuBinding.StorageImage(1, new SampledTexture(default, adaptA.Texture.View()));
        bindings[2] = GpuBinding.StorageImage(2, new SampledTexture(default, adaptB.Texture.View()));
        bindings[3] = GpuBinding.Storage(3, new GpuBufferBinding(_exposureState.Handle, 0, _exposureState.Size));
        cmd.Order();
        if (weighted)
        {
            var flags = post.Sampled(rooms!);
            bindings[4] = GpuBinding.Sampled(4, new SampledTexture(flags.Sampler, flags.View));
        }
        cmd.BindPipeline(pipeline!.Value);
        cmd.BindProgramResources(program!.Value, 0, new ReadOnlySpan<GpuBinding>(bindings, weighted ? 5 : 4));
        var push = new ExposurePush { Blend = blend, BandMin = band.Min, BandMax = band.Max, Indoor = indoor, IndoorFloor = indoorFloor,
            MaxGain = PostProcess.IndoorExposureMaxGain, ShadeScale = shade.Scale, ShadeFloor = shade.Floor };
        cmd.PushConstants(program!.Value, GpuStage.Compute, in push);
        cmd.Dispatch(16, 16);
        cmd.Order();
    }

    /// <summary>
    /// Scales the pixels of the rooms in view (<paramref name="rooms"/>, <see cref="Gi.RoomMaskPass"/>) by the gain <see cref="Exposure"/> last
    /// left in its state: the scene colour in place, before the upscaler. Nothing before the first <see cref="Exposure"/>.
    /// </summary>
    public void InteriorGain(Target2D rooms, Target2D colour)
    {
        if (_exposureState is null || !_exposureStateZeroed) return;
        if (_gainProgram is null)
        {
            _gainProgram = post.Gpu.Shaders.Compute(MergedShaders.InteriorGain, "post interior gain");
            _gainPipeline = post.Gpu.Pipelines.Get(new ComputePipelineDesc(_gainProgram!.Value, "post interior gain"));
        }
        var cmd = post.Segment();
        var flags = post.Sampled(rooms);
        var bindings = stackalloc GpuBinding[3];
        bindings[0] = GpuBinding.Sampled(0, new SampledTexture(flags.Sampler, flags.View));
        bindings[1] = GpuBinding.StorageImage(1, new SampledTexture(default, colour.Texture.View()));
        bindings[2] = GpuBinding.Storage(2, new GpuBufferBinding(_exposureState.Handle, 0, _exposureState.Size));
        cmd.Order();
        cmd.BindPipeline(_gainPipeline!.Value);
        cmd.BindProgramResources(_gainProgram!.Value, 0, new ReadOnlySpan<GpuBinding>(bindings, 3));
        var size = new Vector2(post.RenderWidth, post.RenderHeight);
        cmd.PushConstants(_gainProgram!.Value, GpuStage.Compute, in size);
        cmd.Dispatch((uint)((post.RenderWidth + 15) / 16), (uint)((post.RenderHeight + 15) / 16));
        cmd.Order();
    }

    #endregion

    #region Lifetime

    /// <summary>
    /// Disposes the exposure state and the programs.
    /// </summary>
    public void Dispose()
    {
        _exposureState?.Dispose();
        if (_velocityProgram is { } vp) post.Gpu.Shaders.Dispose(vp);
        if (_exposureProgram is { } ep) post.Gpu.Shaders.Dispose(ep);
        if (_exposureWeightedProgram is { } wp) post.Gpu.Shaders.Dispose(wp);
        if (_gainProgram is { } gp) post.Gpu.Shaders.Dispose(gp);
    }

    #endregion
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
    /// <param name="weighted">With the rooms' mask bound (<see cref="Gi.RoomMaskPass"/>): a tap on a room's pixel goes to the rooms' mean, any
    /// other to the rest's.</param>
    public static string ExposureFor(bool weighted) => Exposure.Replace("//WEIGHT-BINDING", weighted
        ? "layout(set = 0, binding = 4) uniform sampler2D uRooms;" : "", StringComparison.Ordinal).Replace("//WEIGHT", weighted
        ? "inside = pc.indoor > 0.0 && texelFetch(uRooms, ivec2(uv * vec2(textureSize(uRooms, 0))), 0).r > 0.5;" : "",
        StringComparison.Ordinal);

    /// <summary>
    /// The text <see cref="ExposureFor"/> fills in. The state: the tiles done; the rooms' adapted luminance, their smoothed mean and the gain
    /// their pixels are scaled by (<see cref="InteriorGain"/>); then each tile's sums (x, y: the luminance and the share of what is no room's;
    /// z, w: of the rooms', their luminance taken back through the gain it was drawn with).
    /// </summary>
    const string Exposure = """
        #version 450
        layout(local_size_x = 16, local_size_y = 16) in;
        layout(set = 0, binding = 0) uniform sampler2D uScene;
        layout(set = 0, binding = 1, rg32f) readonly uniform image2D uLast;
        layout(set = 0, binding = 2, rg32f) writeonly uniform image2D uAdapted;
        layout(std430, set = 0, binding = 3) coherent buffer State { uint counter; float room; float roomMean; float gain; vec4 tiles[256]; } st;
        //WEIGHT-BINDING
        layout(push_constant) uniform Push { float blend; float bandMin; float bandMax; float indoor; float indoorFloor; float maxGain;
            float shadeScale; float shadeFloor; } pc;
        shared vec4 tile[256];
        shared uint finished;

        // The 2 x 2 means of the mip chain down to one value, in the tile.
        vec4 reduce(uvec2 l)
        {
            for (uint s = 8u; s >= 1u; s >>= 1u)
            {
                vec4 a = vec4(0.0);
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
            float drawn = max(st.gain, 1.0);   // what the rooms' pixels were scaled by in the picture measured
            vec4 sum = vec4(0.0);
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                {
                    vec2 uv = centre + (vec2(x, y) - 1.5) * 0.25 * cell;
                    float lum = max(dot(textureLod(uScene, uv, 0.0).rgb, vec3(0.299, 0.587, 0.114)), 0.0001);
                    bool inside = false;
                    //WEIGHT
                    sum += inside ? vec4(0.0, 0.0, lum / drawn, 1.0) : vec4(lum, 1.0, 0.0, 0.0);
                }
            tile[li] = sum / 16.0;
            barrier();
            vec4 mean = reduce(l);
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
            vec4 pooled = reduce(l);
            if (li == 0u)
            {
                st.counter = 0u;
                vec2 last = imageLoad(uLast, ivec2(0)).rg;
                // What is no room's sets the adapted luminance, by the game's rule (next to none of it in view: held, or the band's minimum while there
                // is no adaptation to hold, as when the targets were made again); the whole picture, as its fragments were lit, the smoothed mean (the
                // night grading reads it).
                bool seen = pooled.y > 0.02;
                float held = last.r > 0.0 ? last.r : pc.bandMin;
                float outside = seen ? pooled.x / pooled.y : held;
                float adapted = clamp(mix(last.r, outside, pc.blend), pc.bandMin, pc.bandMax);

                // Adaptive exposure (PostProcess.ShadeExposure): the target is the band's own above its minimum, the mean scaled under it; smoothed
                // towards, not clamped after.
                if (pc.shadeScale > 0.0)
                {
                    float target = outside >= pc.bandMin ? min(outside, pc.bandMax) : clamp(outside * pc.shadeScale, pc.shadeFloor, pc.bandMin);
                    adapted = seen ? mix(last.r, target, pc.blend) : held;
                }
                float smoothMean = mix(last.g, (pooled.x + pooled.z) / max(pooled.y + pooled.w, 1e-6), pc.blend);
                // The rooms in view: their own adapted luminance goes down with their mean, under the game's minimum (PostProcess.IndoorExposure), and
                // their pixels are scaled by the adapted luminance over it.
                float gain = 1.0, roomMean = 0.0, room = adapted;
                bool rooms = pc.indoor > 0.0 && pooled.w > 0.002;
                if (rooms)
                {
                    roomMean = mix(st.roomMean > 0.0 ? st.roomMean : pooled.z / pooled.w, pooled.z / pooled.w, pc.blend);
                    room = min(clamp(roomMean * pc.indoor, pc.bandMin * pc.indoorFloor, pc.bandMin), adapted);
                    gain = clamp(adapted / room, 1.0, pc.maxGain);
                }
                // With no room in view the gain stays for the next one (nothing is scaled meanwhile): a floor switched and back finds its room as it
                // left it. A room coming into view takes its own gain at once, then follows it as the eye adapts: eased in from 1 it stayed dark for
                // a second after every door and floor switch.
                st.gain = !rooms ? drawn : st.roomMean > 0.0 ? mix(drawn, gain, pc.blend) : gain;
                st.room = room;
                st.roomMean = roomMean;
                imageStore(uAdapted, ivec2(0), vec4(adapted, smoothMean, 0.0, 1.0));
            }
        }
        """;

    /// <summary>
    /// The rooms' pixels (the mask's red) times the gain in <see cref="Exposure"/>'s state, in place.
    /// </summary>
    public const string InteriorGain = """
        #version 450
        layout(local_size_x = 16, local_size_y = 16) in;
        layout(set = 0, binding = 0) uniform sampler2D uRooms;
        layout(set = 0, binding = 1, rgba16f) uniform image2D uColour;
        layout(std430, set = 0, binding = 2) readonly buffer State { uint counter; float room; float roomMean; float gain; vec4 tiles[256]; } st;
        layout(push_constant) uniform Push { vec2 size; } pc;

        void main()
        {
            ivec2 px = ivec2(gl_GlobalInvocationID.xy);
            if (px.x >= int(pc.size.x) || px.y >= int(pc.size.y) || st.gain <= 1.001) return;
            if (texelFetch(uRooms, px, 0).r <= 0.5) return;
            vec4 c = imageLoad(uColour, px);
            imageStore(uColour, px, vec4(c.rgb * st.gain, c.a));
        }
        """;
}