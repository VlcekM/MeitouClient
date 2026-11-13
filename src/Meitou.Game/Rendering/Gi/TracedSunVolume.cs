using Meitou.Game.Rendering.Shadows;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// The traced sun's depth map for what draws no traced pixel (docs/render-shadows.md "Traced sun"): rays from a plane towards the sun, down along the
/// sun's direction, one per texel of <see cref="ShadowPass.SunMapLayers"/> nested squares locked to the world, into the shadow pass's map: the
/// distance to the first caster. The light shafts, the lit dust, the water's foam, the skin's light entry, the reflection and impostors compare with
/// it, so a beam is as continuous as the samples along it and stays put as the camera moves. Render thread only.
/// </summary>
sealed unsafe class TracedSunVolume : IDisposable
{
    /// <summary>
    /// The compute shader: per texel of each traced layer, the nearest of the solids' and the fine and coarse terrain's first hits along the ray.
    /// </summary>
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
            vec4 sun;       // xyz: towards the sun, w: the rays' length
            vec4 right;     // xyz, w: how far towards the sun the rays start
            vec4 up;
            vec4 layers[3]; // x, y: the square's centre along right and up from the eye, z: its half side
            vec4 size;      // x: texels across
        } p;
        layout(set = 0, binding = 3, r32f) uniform writeonly image2DArray uOut;
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer TexCoord { vec2 v; };
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer Triangle { uvec3 v; };
        uvec2 offsetBy(uvec2 a, uint bytes) { uint lo = a.x + bytes; return uvec2(lo, a.y + (lo < a.x ? 1u : 0u)); }
        {{TracedSunShaders.CasterCutout}}

        float firstHit(vec3 o, uint mask, bool opaque)
        {
            rayQueryEXT q;
            rayQueryInitializeEXT(q, uScene, opaque ? gl_RayFlagsOpaqueEXT | gl_RayFlagsCullBackFacingTrianglesEXT : 0u, mask, o, 0.0, -p.sun.xyz, p.sun.w);
            while (rayQueryProceedEXT(q))
            {
                int record = rayQueryGetIntersectionInstanceCustomIndexEXT(q, false) + rayQueryGetIntersectionGeometryIndexEXT(q, false);
                if (casterKept(record, rayQueryGetIntersectionPrimitiveIndexEXT(q, false), rayQueryGetIntersectionBarycentricsEXT(q, false), 2.0)) rayQueryConfirmIntersectionEXT(q);
            }
            return rayQueryGetIntersectionTypeEXT(q, true) != gl_RayQueryCommittedIntersectionNoneEXT ? rayQueryGetIntersectionTEXT(q, true) : 1e9;
        }

        void main()
        {
            ivec3 f = ivec3(gl_GlobalInvocationID);
            int texels = int(p.size.x);
            if (f.x >= texels || f.y >= texels || f.z >= {{ShadowPass.SunMapLayers}} || ((uint(p.size.y) >> uint(f.z)) & 1u) == 0u) return;
            vec2 at = (vec2(f.xy) + 0.5) / float(texels) * 2.0 - 1.0;
            vec4 layer = p.layers[f.z];
            vec3 o = p.eye.xyz + p.right.xyz * (layer.x + at.x * layer.z) + p.up.xyz * (layer.y + at.y * layer.z) + p.sun.xyz * p.right.w;
            float solid = firstHit(o, {{(int)TracedSunShaders.VolumeSolids}}u, false);
            float fine = firstHit(o, {{(int)TracedSunShaders.TerrainFine}}u, true);
            float coarse = firstHit(o, {{(int)TracedSunShaders.TerrainCoarse}}u, true);
            // The coarse terrain counts past its facets' error from the fine grid: a ridge beyond the fine grid, not its own surface over the fine one.
            float first = min(solid, fine);
            if (coarse < fine - 320.0) first = min(first, coarse);
            imageStore(uOut, f, vec4(first));
        }
        """;

    /// <summary>
    /// The shader's <c>Params</c> block (std140).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Params
    {
        /// <summary>
        /// The eye relative to the traced origin; towards the sun with the rays' length; the plane's axes (right's w: how far towards the sun the
        /// rays start); the three squares; the texels across with the layers traced this frame.
        /// </summary>
        public Vector4 Eye, Sun, Right, Up, Layer0, Layer1, Layer2, Size;
    }

    /// <summary>
    /// The device the program, the pipeline and the frame's constants come from.
    /// </summary>
    readonly GpuContext _ctx;

    /// <summary>
    /// The compiled <see cref="Source"/>.
    /// </summary>
    readonly GpuProgram _program;

    /// <summary>
    /// The compute pipeline of <see cref="_program"/>.
    /// </summary>
    readonly GpuComputePipeline _pipeline;

    /// <summary>
    /// Compiles the shader with its four push-descriptor bindings and the bindless table, and makes its pipeline.
    /// </summary>
    public TracedSunVolume(GpuContext ctx)
    {
        this._ctx = ctx;
        _program = ctx.Shaders.Compute(Source, "traced sun volume",
            [
                (0, DescriptorType.AccelerationStructureKhr),
                (1, DescriptorType.StorageBuffer),
                (2, DescriptorType.UniformBuffer),
                (3, DescriptorType.StorageImage),
            ],
            GpuSetLayout.Bindless);
        _pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_program, "traced sun volume"));
    }

    /// <summary>
    /// Fills <paramref name="shadow"/>'s map for this frame's squares from <paramref name="scene"/>'s top level.
    /// </summary>
    public void Run(GiScene scene, ShadowPass shadow, Vector3 toSun)
    {
        if (scene.Top is null || shadow.TracedVolume is not { } volume) return;

        var m = shadow.SunMap;
        var parameters = new Params
        {
            Eye = new Vector4(shadow.VolumeEye - scene.Origin, 0),
            Sun = new Vector4(m.ToSun, ShadowPass.SunMapRayLength),
            Right = new Vector4(m.Right, ShadowPass.SunMapHeight),
            Up = new Vector4(m.Up, 0),
            Layer0 = m.Layers[0],
            Layer1 = m.Layers[1],
            Layer2 = m.Layers[2],
            Size = new Vector4(ShadowPass.SunMapTexels, shadow.SunMapMask, 0, 0),   // y: the layers traced this frame, a bit each
        };
        var constants = _ctx.Frame.Constants.Write<Params>(new ReadOnlySpan<Params>(&parameters, 1), 256);

        var cmd = (CommandList)_ctx.BeginNative("traced sun volume");
        cmd.BeginLabel("traced sun volume");
        cmd.Barrier(BarrierBatch.Full);
        Bind(cmd, scene, volume, constants);
        cmd.BindPipeline(_pipeline);
        cmd.BindBindlessTable(_program, 1, GpuBindPoint.Compute);   // the cut-out maps at the candidates
        cmd.Dispatch((ShadowPass.SunMapTexels + 7) / 8, (ShadowPass.SunMapTexels + 7) / 8, ShadowPass.SunMapLayers);
        cmd.Barrier(BarrierBatch.Full);
        cmd.EndLabel();
        _ctx.EndNative(cmd);
    }

    /// <summary>
    /// Pushes the four descriptors: the top level, the geometry records, this frame's <see cref="Params"/> and the shadow pass's map.
    /// </summary>
    void Bind(CommandList cmd, GiScene scene, GpuTexture volume, in Transient constants)
    {
        var top = scene.Top!.Handle;
        var asWrite = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1,
            PAccelerationStructures = &top,
        };
        var records = new DescriptorBufferInfo(_ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
        var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(Params));
        var storage = new DescriptorImageInfo(default, _ctx.Device.ViewAt(volume.View().Index), ImageLayout.General);

        var writes = stackalloc WriteDescriptorSet[4];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            PNext = &asWrite,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstBinding = 1,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &records,
        };
        writes[2] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstBinding = 2,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.UniformBuffer,
            PBufferInfo = &uniform,
        };
        writes[3] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstBinding = 3,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &storage,
        };
        cmd.PushDescriptors(_program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 4), GpuBindPoint.Compute);
    }

    /// <summary>
    /// Releases the program.
    /// </summary>
    public void Dispose() => _ctx.Shaders.Dispose(_program);
}
