using Meitou.Game.Rendering.Shadows;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// The traced sun's visibility volume (docs/render-shadows.md "Traced sun"): one ray to the sun's centre from each froxel's centre of the view (<see cref="ShadowPass.VolumeWidth"/> x
/// <see cref="ShadowPass.VolumeHeight"/> x <see cref="ShadowPass.VolumeSlices"/>, the slices exponential in depth), against the traced scene, into the shadow pass's volume. What draws
/// no traced pixel reads it in place of the cascades: the light shafts, the lit dust, the water's foam, the skin's light entry, the reflection, impostors. The same every frame for
/// a still view. Render thread only.
/// </summary>
sealed unsafe class TracedSunVolume : IDisposable
{
    /// <summary>The longest ray, within what the traced scene holds round the eye.</summary>
    const float RayLength = 8000;

    static readonly string Source = $$"""
        #version 460
        #extension GL_EXT_ray_query : require
        #extension GL_EXT_buffer_reference : require
        #extension GL_EXT_buffer_reference_uvec2 : require
        #extension GL_EXT_scalar_block_layout : require
        layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
        {{BindlessTable.Declarations(1)}}
        layout(set = 0, binding = 0) uniform accelerationStructureEXT uScene;
        {{GiShaders.GeometryStruct}}
        layout(set = 0, binding = 1, std430) readonly buffer Geometries { Geometry geometries[]; };
        layout(set = 0, binding = 2, std140) uniform Params
        {
            vec4 eye;       // xyz: the eye relative to the traced origin
            vec4 sun;       // xyz: towards the sun, w: the ray's length
            vec4 right;     // xyz, w: the tangent of half the view across
            vec4 up;        // xyz, w: the tangent of half the view up
            vec4 forward;   // xyz, w: the slices' near
            vec4 size;      // froxels across, up, slices, the slices' far
        } p;
        layout(set = 0, binding = 3, rgba8) uniform writeonly image2DArray uOut;
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer TexCoord { vec2 v; };
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer Triangle { uvec3 v; };
        uvec2 offsetBy(uvec2 a, uint bytes) { uint lo = a.x + bytes; return uvec2(lo, a.y + (lo < a.x ? 1u : 0u)); }
        {{TracedSunShaders.CasterCutout}}

        bool blocked(vec3 o, uint mask, float tMin, bool opaque)
        {
            rayQueryEXT q;
            rayQueryInitializeEXT(q, uScene, gl_RayFlagsTerminateOnFirstHitEXT | (opaque ? gl_RayFlagsOpaqueEXT | gl_RayFlagsCullBackFacingTrianglesEXT : 0u), mask, o, tMin, p.sun.xyz, p.sun.w);
            while (rayQueryProceedEXT(q))
            {
                int record = rayQueryGetIntersectionInstanceCustomIndexEXT(q, false) + rayQueryGetIntersectionGeometryIndexEXT(q, false);
                if (casterKept(record, rayQueryGetIntersectionPrimitiveIndexEXT(q, false), rayQueryGetIntersectionBarycentricsEXT(q, false), 2.0)) rayQueryConfirmIntersectionEXT(q);
            }
            return rayQueryGetIntersectionTypeEXT(q, true) != gl_RayQueryCommittedIntersectionNoneEXT;
        }

        void main()
        {
            ivec3 f = ivec3(gl_GlobalInvocationID);
            if (f.x >= int(p.size.x) || f.y >= int(p.size.y) || f.z >= int(p.size.z)) return;
            vec2 ndc = (vec2(f.xy) + 0.5) / p.size.xy * 2.0 - 1.0;
            float depth = p.forward.w * pow(p.size.w / p.forward.w, (float(f.z) + 0.5) / p.size.z);
            vec3 at = (p.forward.xyz + p.right.xyz * ndc.x * p.right.w + p.up.xyz * ndc.y * p.up.w) * depth;
            vec3 o = p.eye.xyz + at;
            // A point in the air, not on a surface: every caster from the point itself, the coarse terrain grid past its facets' error.
            bool shade = blocked(o, {{(int)TracedSunShaders.VolumeSolids}}u, 0.0, false)
                || blocked(o, {{(int)TracedSunShaders.TerrainFine}}u, 0.0, true)
                || blocked(o, {{(int)TracedSunShaders.TerrainCoarse}}u, 320.0, true);
            imageStore(uOut, f, vec4(shade ? 0.0 : 1.0));
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    struct Params
    {
        public Vector4 Eye, Sun, Right, Up, Forward, Size;
    }

    readonly GpuContext _ctx;
    readonly GpuProgram _program;
    readonly GpuComputePipeline _pipeline;

    public TracedSunVolume(GpuContext ctx)
    {
        this._ctx = ctx;
        _program = ctx.Shaders.Compute(Source, "traced sun volume",
            [(0, DescriptorType.AccelerationStructureKhr), (1, DescriptorType.StorageBuffer), (2, DescriptorType.UniformBuffer), (3, DescriptorType.StorageImage)], GpuSetLayout.Bindless);
        _pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_program, "traced sun volume"));
    }

    /// <summary>Fills <paramref name="shadow"/>'s volume for this frame's camera (its <see cref="ShadowPass.VolumeBasis"/>) from <paramref name="scene"/>'s top level.</summary>
    public void Run(GiScene scene, ShadowPass shadow, Vector3 toSun)
    {
        if (scene.Top is null || shadow.TracedVolume is not { } volume) return;
        var (right, up, forward, tanX, tanY) = shadow.VolumeBasis;
        var (near, far) = shadow.VolumeRange;
        var parameters = new Params
        {
            Eye = new Vector4(shadow.VolumeEye - scene.Origin, 0),
            Sun = new Vector4(Vector3.Normalize(toSun), RayLength),
            Right = new Vector4(right, tanX),
            Up = new Vector4(up, tanY),
            Forward = new Vector4(forward, near),
            Size = new Vector4(ShadowPass.VolumeWidth, ShadowPass.VolumeHeight, ShadowPass.VolumeSlices, far),
        };
        var constants = _ctx.Frame.Constants.Write<Params>(new ReadOnlySpan<Params>(&parameters, 1), 256);
        var cmd = (CommandList)_ctx.BeginNative("traced sun volume");
        cmd.BeginLabel("traced sun volume");
        cmd.Barrier(BarrierBatch.Full);
        Bind(cmd, scene, volume, constants);
        cmd.BindPipeline(_pipeline);
        cmd.BindBindlessTable(_program, 1, GpuBindPoint.Compute);   // the cut-out maps at the candidates
        cmd.Dispatch((ShadowPass.VolumeWidth + 7) / 8, (ShadowPass.VolumeHeight + 7) / 8, ShadowPass.VolumeSlices);
        cmd.Barrier(BarrierBatch.Full);
        cmd.EndLabel();
        _ctx.EndNative(cmd);
    }

    void Bind(CommandList cmd, GiScene scene, GpuTexture volume, in Transient constants)
    {
        var top = scene.Top!.Handle;
        var asWrite = new WriteDescriptorSetAccelerationStructureKHR { SType = StructureType.WriteDescriptorSetAccelerationStructureKhr, AccelerationStructureCount = 1, PAccelerationStructures = &top };
        var records = new DescriptorBufferInfo(_ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
        var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(Params));
        var storage = new DescriptorImageInfo(default, _ctx.Device.ViewAt(volume.View().Index), ImageLayout.General);
        var writes = stackalloc WriteDescriptorSet[4];
        writes[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.AccelerationStructureKhr };
        writes[1] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 1, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &records };
        writes[2] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 2, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = &uniform };
        writes[3] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 3, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &storage };
        cmd.PushDescriptors(_program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 4), GpuBindPoint.Compute);
    }

    public void Dispose() => _ctx.Shaders.Dispose(_program);
}
