using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Frame;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// The rooms seen through the floor view's cut (docs/render-post.md "Rooms' exposure"): a pixel lit as an interior (the surface target's bit 9)
/// that has, straight above it or, where that ray finds the sky, along one of <see cref="TiltRays"/> rays <see cref="TiltDegrees"/> from it, a
/// part the floor view hides (<see cref="GeometryRecord.Interior"/> bit 1; the traced scene still holds those). What
/// stands on an open roof, in a building seen from outside or in one whose every floor is shown has no hidden part over it and is no room;
/// nor is a wall of the viewed building turned to the street (bit 30), which a hidden cornice may hang over. With the eye itself inside a
/// viewed building (the character editor's bar) no floor is hidden between it and the room: every interior pixel but those walls is a room's.
/// Render thread only.
/// </summary>
sealed unsafe class RoomMaskPass : IDisposable
{
    /// <summary>
    /// World units a ray looks up: more than the tallest building.
    /// </summary>
    const float Reach = 800;

    /// <summary>
    /// World units the ray starts off the drawn surface, along its plane's normal: an inner wall's ray rises through the room, not along the wall.
    /// </summary>
    const float Lift = 0.3f;

    /// <summary>
    /// Where the ray straight up finds no hidden part, rays this far from the upright, round it, look again: under a stairwell's opening (the stairs,
    /// the floor below them) the upright ray left the room through the opening and they took the street's exposure, a dark band the opening's
    /// shape (**Observed**, save lamp, the Storm House's stairs). An open roof has no hidden part beside it either.
    /// </summary>
    const float TiltDegrees = 40;

    /// <summary>
    /// The tilted rays (<see cref="TiltDegrees"/>), evenly round the upright.
    /// </summary>
    const int TiltRays = 6;

    /// <summary>
    /// The GPU context the pass records into.
    /// </summary>
    readonly GpuContext _ctx;

    /// <summary>
    /// The trace (<see cref="Trace"/>).
    /// </summary>
    readonly GpuProgram _program;

    /// <summary>
    /// The pipeline of <see cref="_program"/>.
    /// </summary>
    readonly GpuComputePipeline _pipeline;

    /// <summary>
    /// The nearest, clamped sampler the depth and the surface target are read with.
    /// </summary>
    readonly GpuSampler _sampler;

    /// <summary>
    /// The trace's constants (its Params block).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Params
    {
        /// <summary>
        /// NDC, jittered as the frame was, to the position relative to the eye.
        /// </summary>
        public Matrix4x4 Inverse;

        /// <summary>
        /// The eye relative to the traced origin (w the rays' mask); the size, the rays' reach and a pixel's width per unit of depth; x 1: the
        /// eye is indoors.
        /// </summary>
        public Vector4 Eye, Size, Flags;
    }

    /// <summary>
    /// <paramref name="v"/> as a GLSL float literal (invariant culture, up to four decimals).
    /// </summary>
    static string F(float v) => v.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Compiles the trace and makes its pipeline and sampler.
    /// </summary>
    public RoomMaskPass(GpuContext ctx)
    {
        _ctx = ctx;
        _program = ctx.Shaders.Compute(Trace, "room mask",
        [
            (0, DescriptorType.CombinedImageSampler), (1, DescriptorType.CombinedImageSampler), (3, DescriptorType.StorageImage),
            (4, DescriptorType.AccelerationStructureKhr), (5, DescriptorType.StorageBuffer), (6, DescriptorType.UniformBuffer),
        ]);
        _pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_program, "room mask"));
        _sampler = ctx.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Nearest, TextureMagFilter.Nearest));
    }

    /// <summary>
    /// Writes 1 to <paramref name="output"/>'s red where the pixel is a room's, 0 elsewhere. <paramref name="inverse"/>: NDC to the position
    /// relative to the eye; <paramref name="pixelSlope"/>: a pixel's width per unit of depth; <paramref name="eyeIndoors"/>: the eye is inside a
    /// viewed building, so every interior pixel is a room's.
    /// </summary>
    public void Run(GiScene scene, GpuTexture depth, GpuTexture surface, GpuTexture output, int width, int height, in Matrix4x4 inverse, Vector3 eye,
        float pixelSlope, bool eyeIndoors)
    {
        if (scene.Top is null) return;

        var parameters = new Params
        {
            Inverse = inverse,
            Eye = new Vector4(eye - scene.Origin, TracedSunShaders.Objects | TracedSunShaders.Moving),
            Size = new Vector4(width, height, Reach, pixelSlope),
            Flags = new Vector4(eyeIndoors ? 1 : 0, 0, 0, 0),
        };
        var constants = _ctx.Frame.Constants.Write<Params>(new ReadOnlySpan<Params>(&parameters, 1), 256);
        var cmd = (CommandList)_ctx.BeginNative("room mask");
        cmd.BeginLabel("room mask");
        cmd.Barrier(BarrierBatch.Full);

        var depthInfo = SampledImage(depth);
        var surfaceInfo = SampledImage(surface);
        var storage = new DescriptorImageInfo(default, _ctx.Device.ViewAt(output.View().Index), ImageLayout.General);
        var top = scene.Top!.Handle;
        var asWrite = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr, AccelerationStructureCount = 1, PAccelerationStructures = &top,
        };
        var records = new DescriptorBufferInfo(_ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
        var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(Params));

        var writes = stackalloc WriteDescriptorSet[6];
        writes[0] = Image(0, DescriptorType.CombinedImageSampler, &depthInfo);
        writes[1] = Image(1, DescriptorType.CombinedImageSampler, &surfaceInfo);
        writes[2] = Image(3, DescriptorType.StorageImage, &storage);
        writes[3] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstBinding = 4, DescriptorCount = 1,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
        };
        writes[4] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstBinding = 5, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &records,
        };
        writes[5] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstBinding = 6, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBuffer,
            PBufferInfo = &uniform,
        };
        cmd.PushDescriptors(_program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 6), GpuBindPoint.Compute);

        cmd.BindPipeline(_pipeline);
        cmd.Dispatch((uint)((width + 7) / 8), (uint)((height + 7) / 8));
        cmd.Barrier(BarrierBatch.Full);
        cmd.EndLabel();
        _ctx.EndNative(cmd);
    }

    /// <summary>
    /// Disposes the trace.
    /// </summary>
    public void Dispose() => _ctx.Shaders.Dispose(_program);

    /// <summary>
    /// <paramref name="texture"/> read through <see cref="_sampler"/>.
    /// </summary>
    DescriptorImageInfo SampledImage(GpuTexture texture) =>
        new(_ctx.Device.SamplerAt(_sampler.Index), _ctx.Device.ViewAt(texture.View().Index), ImageLayout.General);

    /// <summary>
    /// A descriptor write of image <paramref name="info"/> as <paramref name="type"/> at <paramref name="binding"/>.
    /// </summary>
    static WriteDescriptorSet Image(uint binding, DescriptorType type, DescriptorImageInfo* info) =>
        new() { SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1, DescriptorType = type, PImageInfo = info };

    /// <summary>
    /// One ray straight up from each interior pixel, off its drawn plane: through the shown parts, however many, until one the
    /// floor view hides, which makes the pixel a room's.
    /// </summary>
    static readonly string Trace = $$"""
        #version 460
        #extension GL_EXT_ray_query : require
        layout(local_size_x = 8, local_size_y = 8) in;

        layout(set = 0, binding = 0) uniform sampler2D uDepth;
        layout(set = 0, binding = 1) uniform usampler2D uSurface;
        layout(set = 0, binding = 3, r32f) uniform writeonly image2D uOut;
        layout(set = 0, binding = 4) uniform accelerationStructureEXT uScene;
        {{GiShaders.GeometryStruct}}
        layout(set = 0, binding = 5, std430) readonly buffer Geometries { Geometry geometries[]; };
        layout(set = 0, binding = 6, std140) uniform Params
        {
            mat4 inverse;      // NDC, jittered as the frame was -> position relative to the eye
            vec4 eye;          // xyz: the eye relative to the traced origin, w: the rays' mask
            vec4 size;         // width, height, the rays' reach, a pixel's width per unit of depth
            vec4 flags;        // x 1: the eye is inside a viewed building
        } p;
        """ + DepthConvention.Glsl + $$"""

        vec3 pointAt(ivec2 px)
        {
            ivec2 q = clamp(px, ivec2(0), ivec2(p.size.xy) - 1);
            vec4 h = p.inverse * vec4((vec2(q) + 0.5) / p.size.xy * 2.0 - 1.0, texelFetch(uDepth, q, 0).r, 1.0);
            return h.xyz / h.w;
        }

        // The drawn triangle's plane from the depth, along the neighbours of the smaller step, facing the eye.
        vec3 planeNormal(ivec2 px, vec3 at)
        {
            vec3 l = at - pointAt(px - ivec2(1, 0)), r = pointAt(px + ivec2(1, 0)) - at;
            vec3 d = at - pointAt(px - ivec2(0, 1)), u = pointAt(px + ivec2(0, 1)) - at;
            vec3 g = cross(dot(r, r) < dot(l, l) ? r : l, dot(u, u) < dot(d, d) ? u : d);
            float len = length(g);
            if (!(len > 1e-8)) return -normalize(at);
            g /= len;
            return dot(g, at) > 0.0 ? -g : g;
        }

""" + "\n" + TracedSunShaders.Enclosed + "\n" + $$"""
        // One ray that only a hidden part stops: every shown part it meets is passed, however many (a partition of planks and battens stood
        // a dozen deep over its own face, and a ray that gave up after four left rectangles of it out of the room, black at night).
        bool underHidden(vec3 from, vec3 dir)
        {
            rayQueryEXT q;
            rayQueryInitializeEXT(q, uScene, gl_RayFlagsNoOpaqueEXT | gl_RayFlagsTerminateOnFirstHitEXT, uint(p.eye.w), from, 0.02, dir, p.size.z);
            while (rayQueryProceedEXT(q))
            {
                int record = rayQueryGetIntersectionInstanceCustomIndexEXT(q, false) + rayQueryGetIntersectionGeometryIndexEXT(q, false);
                if ((geometries[record].interior & 2u) != 0u) rayQueryConfirmIntersectionEXT(q);
            }
            return rayQueryGetIntersectionTypeEXT(q, true) != gl_RayQueryCommittedIntersectionNoneEXT;
        }

        void main()
        {
            ivec2 px = ivec2(gl_GlobalInvocationID.xy);
            if (px.x >= int(p.size.x) || px.y >= int(p.size.y)) return;
            // Bit 30 on what is not a character (bit 19; its skin's share is there): a wall of the viewed building turned to the street.
            uint w = texelFetch(uSurface, px, 0).w;
            bool street = (w & 1073741824u) != 0u && (w & 524288u) == 0u;
            if (((w & 512u) == 0u && !street) || depthIsSky(texelFetch(uDepth, px, 0).r)) { imageStore(uOut, px, vec4(0.0)); return; }

            if (p.flags.x > 0.5 && !street) { imageStore(uOut, px, vec4(1.0, 0.0, 0.0, 1.0)); return; }

            vec3 at = pointAt(px);
            // The shell's faces turned to the street are lit as the outside (BuildingLodShaders), inside the walls or not: none takes the room's gain.
            if (street) { imageStore(uOut, px, vec4(0.0)); return; }
            vec3 from = p.eye.xyz + at + planeNormal(px, at) * ({{F(Lift)}} + length(at) * p.size.w);
            bool room = underHidden(from, vec3(0.0, 1.0, 0.0));
            const vec2 tilt = vec2({{F(MathF.Sin(TiltDegrees * MathF.PI / 180))}}, {{F(MathF.Cos(TiltDegrees * MathF.PI / 180))}});   // sine, cosine
            for (int i = 0; i < {{TiltRays}} && !room; i++)
            {
                float a = float(i) * {{F(2 * MathF.PI / TiltRays)}};
                room = underHidden(from, vec3(cos(a) * tilt.x, tilt.y, sin(a) * tilt.x));
            }
            imageStore(uOut, px, vec4(room ? 1.0 : 0.0, 0.0, 0.0, 1.0));
        }
        """;
}
