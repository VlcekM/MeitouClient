using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Frame;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// The characters' traced ambient occlusion (docs/render-post.md "Traced occlusion on characters"): on each pixel a character drew as a lamp receiver
/// (the surface target's bit 19), short rays over the hemisphere of its normal against the characters and what moves in the traced scene
/// (<see cref="GiScene"/>), so a hood shades the face under it and an arm the flank, from any view. The screen's occlusion cannot: it sees no
/// surface the picture does not show, and no probe ray sees a character. Render thread only.
/// </summary>
sealed unsafe class CharacterOcclusionPass : IDisposable
{
    /// <summary>The rays a pixel traces; each pixel of a 4 x 4 block turns them by a sixteenth of their spacing, so a block holds 16 times as many directions.</summary>
    public const int Rays = 8;
    /// <summary>World units: what is not worn counts for nothing as a blocker nearer than the first, in full from the second.</summary>
    const float NearStart = 0.4f, NearFull = 1.5f;

    static string F(float v) => v.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);

    readonly GpuContext _ctx;
    readonly GpuProgram _program;
    readonly GpuComputePipeline _pipeline;
    readonly GpuSampler _sampler;

    public CharacterOcclusionPass(GpuContext ctx)
    {
        this._ctx = ctx;
        _program = ctx.Shaders.Compute(Trace, "character occlusion",
        [
            (0, DescriptorType.CombinedImageSampler), (1, DescriptorType.CombinedImageSampler), (3, DescriptorType.StorageImage),
            (4, DescriptorType.AccelerationStructureKhr), (5, DescriptorType.StorageBuffer), (6, DescriptorType.UniformBuffer),
        ], GpuSetLayout.Bindless);
        _pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_program, "character occlusion"));
        _sampler = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Params
    {
        public Matrix4x4 Inverse;
        public Vector4 Eye, Size;
    }

    /// <summary>
    /// Writes each pixel's visibility (1 where no character is a receiver) to <paramref name="output"/>'s red. <paramref name="inverse"/>: NDC to the
    /// position relative to the eye; <paramref name="pixelSlope"/>: a pixel's width per unit of depth; <paramref name="radius"/>: how far a ray looks.
    /// </summary>
    public void Run(GiScene scene, GpuTexture depth, GpuTexture surface, GpuTexture output, int width, int height, in Matrix4x4 inverse, Vector3 eye, float pixelSlope,
        float radius)
    {
        if (scene.Top is null) return;
        var parameters = new Params
        {
            Inverse = inverse,
            Eye = new Vector4(eye - scene.Origin, TracedSunShaders.LampMovers),
            Size = new Vector4(width, height, radius, pixelSlope),
        };
        var constants = _ctx.Frame.Constants.Write<Params>(new ReadOnlySpan<Params>(&parameters, 1), 256);
        var cmd = (CommandList)_ctx.BeginNative("character occlusion");
        cmd.BeginLabel("character occlusion");
        cmd.Barrier(BarrierBatch.Full);

        DescriptorImageInfo Sampled(GpuTexture texture) => new(_ctx.Device.SamplerAt(_sampler.Index), _ctx.Device.ViewAt(texture.View().Index), ImageLayout.General);
        var depthInfo = Sampled(depth);
        var surfaceInfo = Sampled(surface);
        var storage = new DescriptorImageInfo(default, _ctx.Device.ViewAt(output.View().Index), ImageLayout.General);
        var top = scene.Top!.Handle;
        var asWrite = new WriteDescriptorSetAccelerationStructureKHR { SType = StructureType.WriteDescriptorSetAccelerationStructureKhr, AccelerationStructureCount = 1, PAccelerationStructures = &top };
        var records = new DescriptorBufferInfo(_ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
        var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(Params));
        var writes = stackalloc WriteDescriptorSet[6];
        writes[0] = Image(0, DescriptorType.CombinedImageSampler, &depthInfo);
        writes[1] = Image(1, DescriptorType.CombinedImageSampler, &surfaceInfo);
        writes[2] = Image(3, DescriptorType.StorageImage, &storage);
        writes[3] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstBinding = 4, DescriptorCount = 1, DescriptorType = DescriptorType.AccelerationStructureKhr };
        writes[4] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 5, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &records };
        writes[5] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 6, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = &uniform };
        cmd.PushDescriptors(_program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 6), GpuBindPoint.Compute);
        cmd.BindBindlessTable(_program, 1, GpuBindPoint.Compute);   // the cut-out maps at the candidates (hair)

        cmd.BindPipeline(_pipeline);
        cmd.Dispatch((uint)((width + 7) / 8), (uint)((height + 7) / 8));
        cmd.Barrier(BarrierBatch.Full);
        cmd.EndLabel();
        _ctx.EndNative(cmd);
    }

    static WriteDescriptorSet Image(uint binding, DescriptorType type, DescriptorImageInfo* info) =>
        new() { SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1, DescriptorType = type, PImageInfo = info };

    public void Dispose() => _ctx.Shaders.Dispose(_program);

    /// <summary>
    /// The rays: a cosine-weighted set round the shading normal, the same every frame (nothing for the upscaler to average), leaving the body as the traced
    /// sun's rays do (<see cref="TracedSunShaders"/>: close to the skin, past the creases between facets); a direction the drawn facet is turned from is not
    /// asked (the smooth shading goes round the facets). A blocker counts in full up to half the radius, then less, to nothing at the radius; what is worn from the skin on (a hood lies against the temples), the body's own parts from <see cref="NearStart"/>.
    /// </summary>
    static readonly string Trace = $$"""
        #version 460
        #extension GL_EXT_ray_query : require
        #extension GL_EXT_buffer_reference : require
        #extension GL_EXT_buffer_reference_uvec2 : require
        #extension GL_EXT_scalar_block_layout : require
        layout(local_size_x = 8, local_size_y = 8) in;
        {{BindlessTable.Declarations(1)}}

        layout(set = 0, binding = 0) uniform sampler2D uDepth;
        layout(set = 0, binding = 1) uniform usampler2D uSurface;
        layout(set = 0, binding = 3, rgba16f) uniform writeonly image2D uOut;
        layout(set = 0, binding = 4) uniform accelerationStructureEXT uScene;
        {{GiShaders.GeometryStruct}}
        layout(set = 0, binding = 5, std430) readonly buffer Geometries { Geometry geometries[]; };
        layout(set = 0, binding = 6, std140) uniform Params
        {
            mat4 inverse;      // NDC, jittered as the frame was -> position relative to the eye
            vec4 eye;          // xyz: the eye relative to the traced origin, w: the rays' mask
            vec4 size;         // width, height, the rays' reach, a pixel's width per unit of depth
        } p;
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer TexCoord { vec2 v; };
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer Triangle { uvec3 v; };
        uvec2 offsetBy(uvec2 a, uint bytes) { uint lo = a.x + bytes; return uvec2(lo, a.y + (lo < a.x ? 1u : 0u)); }
        """ + DepthConvention.Glsl + $$"""

        vec3 octDir(vec2 e)
        {
            vec3 d = vec3(e.x, 1.0 - abs(e.x) - abs(e.y), e.y);
            if (d.y < 0.0) d.xz = (1.0 - abs(d.zx)) * vec2(d.x >= 0.0 ? 1.0 : -1.0, d.z >= 0.0 ? 1.0 : -1.0);
            return normalize(d);
        }

        vec3 pointAt(ivec2 px)
        {
            ivec2 q = clamp(px, ivec2(0), ivec2(p.size.xy) - 1);
            vec4 h = p.inverse * vec4((vec2(q) + 0.5) / p.size.xy * 2.0 - 1.0, texelFetch(uDepth, q, 0).r, 1.0);
            return h.xyz / h.w;
        }

        // The drawn triangle's plane from the depth, along the neighbours of the smaller step, facing like n.
        vec3 planeNormal(ivec2 px, vec3 at, vec3 n)
        {
            vec3 l = at - pointAt(px - ivec2(1, 0)), r = pointAt(px + ivec2(1, 0)) - at;
            vec3 d = at - pointAt(px - ivec2(0, 1)), u = pointAt(px + ivec2(0, 1)) - at;
            vec3 g = cross(dot(r, r) < dot(l, l) ? r : l, dot(u, u) < dot(d, d) ? u : d);
            float len = length(g);
            if (!(len > 1e-8)) return n;
            g /= len;
            return dot(g, n) < 0.0 ? -g : g;
        }

        """ + TracedSunShaders.CasterCutout + $$"""

        void main()
        {
            ivec2 px = ivec2(gl_GlobalInvocationID.xy);
            if (px.x >= int(p.size.x) || px.y >= int(p.size.y)) return;
            uvec4 surface = texelFetch(uSurface, px, 0);
            if ((surface.w & 524288u) == 0u || depthIsSky(texelFetch(uDepth, px, 0).r)) { imageStore(uOut, px, vec4(1.0)); return; }

            vec3 at = pointAt(px);
            float pixel = length(at) * p.size.w;
            vec3 n = octDir(vec2(surface.xy) / 65535.0 * 2.0 - 1.0);
            vec3 side = planeNormal(px, at, n);
            vec3 t = normalize(abs(n.y) < 0.99 ? cross(n, vec3(0.0, 1.0, 0.0)) : cross(n, vec3(1.0, 0.0, 0.0))), b = cross(n, t);
            // Each pixel of a 4 x 4 block turns the set by a sixteenth of the golden angle's mean spacing: the block's mean holds 16 sets.
            float turn = float((px.x & 3) + (px.y & 3) * 4) * (6.2831853 / (16.0 * {{Rays}}.0));
            float reach = p.size.z, blocked = 0.0;
            for (int i = 0; i < {{Rays}}; i++)
            {
                float r = sqrt((float(i) + 0.5) / {{Rays}}.0), phi = float(i) * 2.3999632 + turn;
                vec3 dir = t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(1.0 - r * r);
                float facing = dot(side, dir);
                if (facing < 0.05) continue;
                float clearance = (0.01 + pixel * 0.5) / max(facing, 0.2);
                rayQueryEXT q;
                rayQueryInitializeEXT(q, uScene, gl_RayFlagsCullBackFacingTrianglesEXT, uint(p.eye.w), p.eye.xyz + at + side * clearance, max(clearance, {{TracedSunShaders.BodyContactText}}), dir, reach);
                while (rayQueryProceedEXT(q))
                {
                    int record = rayQueryGetIntersectionInstanceCustomIndexEXT(q, false) + rayQueryGetIntersectionGeometryIndexEXT(q, false);
                    if (casterKept(record, rayQueryGetIntersectionPrimitiveIndexEXT(q, false), rayQueryGetIntersectionBarycentricsEXT(q, false), 1.0)) rayQueryConfirmIntersectionEXT(q);
                }
                if (rayQueryGetIntersectionTypeEXT(q, true) != gl_RayQueryCommittedIntersectionNoneEXT)
                {
                    // What is worn counts from the skin on (a hood lies against the temples). The body's own parts nearer than a hand's breadth (a brow over an eye, its
                    // lashes, the eye, a nostril) are the screen's occlusion's to shade: counted here the eye sockets went black. A worn part's record has the tint 204
                    // in its red (CharacterRenderer.AddRayPart's 0.8).
                    float hit = rayQueryGetIntersectionTEXT(q, true);
                    int record = rayQueryGetIntersectionInstanceCustomIndexEXT(q, true) + rayQueryGetIntersectionGeometryIndexEXT(q, true);
                    float near = (geometries[record].charTint & 255u) == 204u ? 1.0 : smoothstep({{F(NearStart)}}, {{F(NearFull)}}, hit);
                    blocked += near * (1.0 - smoothstep(0.5 * reach, reach, hit));
                }
            }
            imageStore(uOut, px, vec4(1.0 - blocked / {{Rays}}.0, 0.0, 0.0, 1.0));
        }
        """;
}
