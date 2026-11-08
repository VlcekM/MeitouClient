using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Characters;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;
using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;

namespace Meitou.Rendering.Gi;

/// <summary>
/// The traced light of the character editor's bar set (docs/character-renderer.md "Meitou stage and portraits"), apart from the world's GI: a <see cref="GiScene"/> holding only the set's
/// meshes, a grid of probes round them (six irradiance directions each, every ray traced against the set and lit by the set's lamps with shadow rays, a few passes for the bounces, then still:
/// the set does not move) and, each frame, a ray from every picture pixel to each of the first <see cref="ShadowedLamps"/> lamps. The studio's shaders read both (StudioShaders). Render thread only.
/// </summary>
internal sealed unsafe class StudioGi : IDisposable
{
    /// <summary>The lamps the probe rays light with (the view block's room) and those with a traced visibility per pixel (two RGBA8 targets).</summary>
    public const int MaxLamps = 12, ShadowedLamps = 8;
    /// <summary>The rays of a probe, the passes (each adds a bounce), the grid at most and the finest spacing.</summary>
    const int Rays = 64, Passes = 5, MaxAcross = 48, MaxUp = 20;
    const float FinestSpacing = 6, MaxExtent = 420;

    [StructLayout(LayoutKind.Sequential)]
    struct ProbeParams
    {
        public Vector4 Origin, Dims, Night;
        public fixed float Lamps[MaxLamps * 8];
    }

    [StructLayout(LayoutKind.Sequential)]
    struct VisibilityParams
    {
        public Matrix4x4 Inverse;
        public Vector4 Size;
        public fixed float Lamps[MaxLamps * 8];
    }

    // A lamp stands inside its own fixture (the shade would cut its light into a cone): the rays stop this far short of it (FixtureClear).
    const string Lighting = """
        const float FixtureClear = 10.0;
        vec3 lampAt(int i, vec3 world, out vec3 l, out float d)
        {
            vec4 p = p_.lamps[2 * i];
            vec3 to = p.xyz - world;
            d = length(to);
            l = to / max(d, 1e-3);
            float x = clamp(d / max(p.w, 1e-3), 0.0, 1.0);
            float a = x < 0.649 ? 0.2 + 0.8 * (1.0 - x) * (1.0 - x) * (1.0 - x) : x < 0.8 ? 0.242 - 3.0 * (x - 0.6) * (x - 0.6) : 3.0 * (x - 1.0) * (x - 1.0);
            return p_.lamps[2 * i + 1].rgb * a;
        }
        bool blocked(vec3 from, vec3 dir, float reach)
        {
            rayQueryEXT q;
            rayQueryInitializeEXT(q, uScene, gl_RayFlagsTerminateOnFirstHitEXT, 0xFF, from, 0.0, dir, reach);
            TRACE(q)
            return rayQueryGetIntersectionTypeEXT(q, true) != gl_RayQueryCommittedIntersectionNoneEXT;
        }
        """;

    static string Header => $$"""
        #version 460
        #extension GL_EXT_ray_query : require
        #extension GL_EXT_buffer_reference : require
        #extension GL_EXT_buffer_reference_uvec2 : require
        #extension GL_EXT_scalar_block_layout : require
        {{BindlessTable.Declarations(1)}}
        layout(set = 0, binding = 0) uniform accelerationStructureEXT uScene;
        {{GiShaders.GeometryStruct}}
        layout(set = 0, binding = 1, std430) readonly buffer Geometries { Geometry geometries[]; };
        {{GiShaders.HitBuffers}}
        {{GiShaders.HitCutout}}
        #define TRACE(q) while (rayQueryProceedEXT(q)) { if (solid(rayQueryGetIntersectionInstanceCustomIndexEXT(q, false) + rayQueryGetIntersectionGeometryIndexEXT(q, false), rayQueryGetIntersectionPrimitiveIndexEXT(q, false), rayQueryGetIntersectionBarycentricsEXT(q, false))) rayQueryConfirmIntersectionEXT(q); }

        """;

    /// <summary>The cube of irradiance a surface reads from the grid: <c>FETCH(ivec2)</c> gives a texel of the atlas; a probe in a solid (alpha 0) or behind the surface does not count.</summary>
    internal const string Reading = """
        vec3 GI_NAME(vec3 world, vec3 n, out float found)
        {
            vec4 grid = GI_GRID;
            ivec3 dims = GI_DIMS;
            vec3 g = (world - grid.xyz) / grid.w - 0.5;
            ivec3 i0 = ivec3(floor(g));
            vec3 f = g - vec3(i0), sq = n * n, sum = vec3(0.0);
            ivec3 face = ivec3(n.x >= 0.0 ? 0 : 1, n.y >= 0.0 ? 2 : 3, n.z >= 0.0 ? 4 : 5);
            float total = 0.0;
            for (int k = 0; k < 8; k++)
            {
                ivec3 o = ivec3(k & 1, (k >> 1) & 1, k >> 2), c = i0 + o;
                if (any(lessThan(c, ivec3(0))) || any(greaterThanEqual(c, dims))) continue;
                vec3 t = mix(1.0 - f, f, vec3(o));
                vec3 toProbe = (vec3(c) + 0.5) * grid.w + grid.xyz - world;
                float w = t.x * t.y * t.z * smoothstep(-0.25 * grid.w, 0.25 * grid.w, dot(toProbe, n) + 0.25 * grid.w);
                if (w <= 0.0) continue;
                int row = c.y * dims.z + c.z, column = c.x * 6;
                vec4 x = FETCH(ivec2(column + face.x, row)), y = FETCH(ivec2(column + face.y, row)), z = FETCH(ivec2(column + face.z, row));
                w *= x.a;
                sum += (x.rgb * sq.x + y.rgb * sq.y + z.rgb * sq.z) * w;
                total += w;
            }
            found = total;
            return total > 1e-4 ? sum / total : vec3(0.0);
        }
        """;

    static string ProbeShader => Header + $$"""
        layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;
        layout(set = 0, binding = 2, std140) uniform Params { vec4 origin; vec4 dims; vec4 night; vec4 lamps[{{MaxLamps * 2}}]; } p_;
        layout(set = 0, binding = 3) uniform sampler2D uPrevious;
        layout(set = 0, binding = 4, rgba16f) writeonly uniform image2D uOut;
        {{Lighting}}
        {{Reading.Replace("GI_NAME", "previous").Replace("GI_GRID", "p_.origin").Replace("GI_DIMS", "ivec3(p_.dims.xyz)").Replace("FETCH(", "texelFetch(uPrevious, ").Replace(", ivec2(", ", ivec2(").Replace("texelFetch(uPrevious, ivec2(column + face.x, row))", "texelFetch(uPrevious, ivec2(column + face.x, row), 0)").Replace("texelFetch(uPrevious, ivec2(column + face.y, row))", "texelFetch(uPrevious, ivec2(column + face.y, row), 0)").Replace("texelFetch(uPrevious, ivec2(column + face.z, row))", "texelFetch(uPrevious, ivec2(column + face.z, row), 0)")}}

        vec3 fibonacci(uint i, uint n)
        {
            float y = 1.0 - (float(i) + 0.5) * 2.0 / float(n), r = sqrt(max(1.0 - y * y, 0.0)), phi = float(i) * 2.39996323;
            return vec3(cos(phi) * r, y, sin(phi) * r);
        }

        // The light coming along one ray; back: it met the inside of something.
        vec3 rayLight(vec3 from, vec3 dir, out bool back)
        {
            back = false;
            rayQueryEXT rq;
            rayQueryInitializeEXT(rq, uScene, gl_RayFlagsNoneEXT, 0xFF, from, 0.0, dir, 2000.0);
            TRACE(rq)
            if (rayQueryGetIntersectionTypeEXT(rq, true) != gl_RayQueryCommittedIntersectionTriangleEXT) return p_.night.rgb;
            if (!rayQueryGetIntersectionFrontFaceEXT(rq, true) && rayQueryGetIntersectionTEXT(rq, true) < p_.origin.w * 0.75) { back = true; return vec3(0.0); }
            int record = rayQueryGetIntersectionInstanceCustomIndexEXT(rq, true) + rayQueryGetIntersectionGeometryIndexEXT(rq, true);
            Geometry g = geometries[record];
            uvec3 tri = Triangle(offsetBy(g.indices, uint(rayQueryGetIntersectionPrimitiveIndexEXT(rq, true)) * 12u)).v;
            vec3 a = Position(offsetBy(g.vertices, tri.x * g.stride)).v, b = Position(offsetBy(g.vertices, tri.y * g.stride)).v, c = Position(offsetBy(g.vertices, tri.z * g.stride)).v;
            vec3 n = normalize(cross(b - a, c - a) * mat3(rayQueryGetIntersectionWorldToObjectEXT(rq, true)));
            if (dot(n, dir) > 0.0) n = -n;
            vec3 hit = from + dir * rayQueryGetIntersectionTEXT(rq, true);
            vec3 colour = g.diffuse != 0u ? textureLod(textures2D[nonuniformEXT(g.diffuse)], hitUv(g, tri, rayQueryGetIntersectionBarycentricsEXT(rq, true)), 4.0).rgb : vec3(0.5);
            vec3 light = vec3(0.0);
            for (int i = 0; i < int(p_.dims.w); i++)
            {
                vec3 l; float d;
                vec3 radiance = lampAt(i, hit, l, d);
                float nl = max(dot(n, l), 0.0);
                if (nl > 0.0 && !blocked(hit + n * 0.3, l, max(d - FixtureClear, 0.1))) light += radiance * nl;
            }
            float found;
            vec3 bounce = previous(hit, n, found);
            return colour * (light + bounce + p_.night.rgb * 0.5);
        }

        void main()
        {
            ivec3 c = ivec3(gl_GlobalInvocationID);
            ivec3 dims = ivec3(p_.dims.xyz);
            if (any(greaterThanEqual(c, dims))) return;
            vec3 from = (vec3(c) + 0.5) * p_.origin.w + p_.origin.xyz;
            const vec3 axes[6] = vec3[6](vec3(1, 0, 0), vec3(-1, 0, 0), vec3(0, 1, 0), vec3(0, -1, 0), vec3(0, 0, 1), vec3(0, 0, -1));
            vec3 sum[6] = vec3[6](vec3(0.0), vec3(0.0), vec3(0.0), vec3(0.0), vec3(0.0), vec3(0.0));
            float weight[6] = float[6](0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
            int backs = 0;
            for (uint r = 0u; r < {{Rays}}u; r++)
            {
                vec3 dir = fibonacci(r, {{Rays}}u);
                bool back;
                vec3 light = rayLight(from, dir, back);
                if (back) { backs++; continue; }
                for (int k = 0; k < 6; k++)
                {
                    float w = max(dot(dir, axes[k]), 0.0);
                    sum[k] += light * w;
                    weight[k] += w;
                }
            }
            float valid = backs * 4 < {{Rays}} ? 1.0 : 0.0;
            int row = c.y * dims.z + c.z;
            for (int k = 0; k < 6; k++)
                imageStore(uOut, ivec2(c.x * 6 + k, row), vec4(weight[k] > 0.0 ? sum[k] / weight[k] : vec3(0.0), valid));
        }
        """;

    static string VisibilityShader => Header + $$"""
        layout(local_size_x = 8, local_size_y = 8) in;
        layout(set = 0, binding = 2, std140) uniform Params { mat4 inverse; vec4 size; vec4 lamps[{{MaxLamps * 2}}]; } p_;
        layout(set = 0, binding = 3) uniform sampler2D uDepth;
        layout(set = 0, binding = 4, rgba8) writeonly uniform image2D uFirst;
        layout(set = 0, binding = 5, rgba8) writeonly uniform image2D uSecond;
        {{Lighting}}

        vec3 positionAt(ivec2 p)
        {
            float z = texelFetch(uDepth, p, 0).r;
            vec4 w = p_.inverse * vec4((vec2(p) + 0.5) / p_.size.xy * 2.0 - 1.0, z, 1.0);
            return w.xyz / w.w;
        }

        void main()
        {
            int down = int(p_.size.z);
            ivec2 px = ivec2(gl_GlobalInvocationID.xy), at = px * down + down / 2, last = ivec2(p_.size.xy) - 1;
            if (any(greaterThan(at, last))) return;
            vec4 seen[2] = vec4[2](vec4(1.0), vec4(1.0));
            if (texelFetch(uDepth, at, 0).r < 1.0)
            {
                vec3 p = positionAt(at);
                vec3 dx = positionAt(min(at + ivec2(1, 0), last)) - p, dx2 = p - positionAt(max(at - ivec2(1, 0), ivec2(0)));
                vec3 dy = positionAt(min(at + ivec2(0, 1), last)) - p, dy2 = p - positionAt(max(at - ivec2(0, 1), ivec2(0)));
                vec3 n = cross(dot(dx, dx) < dot(dx2, dx2) ? dx : dx2, dot(dy, dy) < dot(dy2, dy2) ? dy : dy2);
                n = normalize(n);
                int lamps = min(int(p_.size.w), {{ShadowedLamps}});
                for (int i = 0; i < lamps; i++)
                {
                    vec3 l; float d;
                    vec3 radiance = lampAt(i, p, l, d);
                    vec3 side = dot(n, l) >= 0.0 ? n : -n;
                    if (dot(radiance, radiance) > 0.0 && blocked(p + side * 0.25 + l * 0.1, l, max(d - FixtureClear, 0.1))) seen[i / 4][i % 4] = 0.0;
                }
            }
            imageStore(uFirst, px, seen[0]);
            imageStore(uSecond, px, seen[1]);
        }
        """;

    readonly GpuContext ctx;
    readonly GiScene scene;
    readonly GpuProgram probeProgram, visibilityProgram;
    readonly GpuComputePipeline probePipeline, visibilityPipeline;
    readonly GpuSampler nearest, linear;
    GpuTexture?[] atlas = new GpuTexture?[2];
    uint[] atlasIndex = new uint[2];
    GpuTexture? visibilityA, visibilityB;
    uint visibilityIndexA, visibilityIndexB;
    int current, pass, built, width, height, depthDown = 1;
    Vector3 gridOrigin;
    float spacing;
    (int X, int Y, int Z) dims;
    readonly List<StageLamp> lamps = [];

    /// <summary>The grid has at least its first pass and the atlas can be read.</summary>
    public bool Ready => pass > 0;

    public StudioGi(GpuContext ctx)
    {
        this.ctx = ctx;
        scene = new GiScene(ctx);
        probeProgram = ctx.Shaders.Compute(ProbeShader, "studio gi probes",
            [(0, DescriptorType.AccelerationStructureKhr), (1, DescriptorType.StorageBuffer), (2, DescriptorType.UniformBuffer), (3, DescriptorType.CombinedImageSampler), (4, DescriptorType.StorageImage)], GpuSetLayout.Bindless);
        visibilityProgram = ctx.Shaders.Compute(VisibilityShader, "studio gi visibility",
            [(0, DescriptorType.AccelerationStructureKhr), (1, DescriptorType.StorageBuffer), (2, DescriptorType.UniformBuffer), (3, DescriptorType.CombinedImageSampler), (4, DescriptorType.StorageImage), (5, DescriptorType.StorageImage)], GpuSetLayout.Bindless);
        probePipeline = ctx.Pipelines.Get(new ComputePipelineDesc(probeProgram, "studio gi probes"));
        visibilityPipeline = ctx.Pipelines.Get(new ComputePipelineDesc(visibilityProgram, "studio gi visibility"));
        nearest = Sampler(TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        linear = Sampler(TextureMinFilter.Linear, TextureMagFilter.Linear);
    }

    GpuSampler Sampler(TextureMinFilter min, TextureMagFilter mag) => ctx.Samplers.Get(SamplerDesc.FromGl(min, mag, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));

    /// <summary>The set's structures for this frame and, until the light has settled, one more pass of the probes.</summary>
    public void Update(WorldObjectRenderer objects, IReadOnlyList<ObjectStreamer.Instance> stage, IReadOnlyList<StageLamp> setLamps, Vector3 centre, float lampGain)
    {
        scene.UpdateStage(centre, objects, stage);
        if (scene.Top is null || scene.InstanceCount == 0) return;
        int signature = HashCode.Combine(scene.InstanceCount, stage.Count, setLamps.Count, centre);
        if (signature != built)
        {
            built = signature;
            lamps.Clear();
            for (int i = 0; i < Math.Min(setLamps.Count, MaxLamps); i++) lamps.Add(setLamps[i] with { Colour = setLamps[i].Colour * lampGain });
            PlaceGrid(stage, centre);
            pass = 0;
        }
        if (pass >= Passes) return;
        RunProbes();
        pass++;
    }

    /// <summary>The grid over the set's building round the character, as fine as its size allows.</summary>
    void PlaceGrid(IReadOnlyList<ObjectStreamer.Instance> stage, Vector3 centre)
    {
        Vector3 min = centre, max = centre;
        foreach (var inst in stage)
        {
            if (inst.Placed.Kind != Meitou.Data.World.PlacedKind.BuildingPart || Vector3.Distance(inst.Centre, centre) > MaxExtent) continue;
            min = Vector3.Min(min, inst.Centre - new Vector3(inst.Radius));
            max = Vector3.Max(max, inst.Centre + new Vector3(inst.Radius));
        }
        min = Vector3.Max(min, centre - new Vector3(MaxExtent / 2));
        max = Vector3.Min(max, centre + new Vector3(MaxExtent / 2));
        var extent = max - min;
        spacing = MathF.Max(FinestSpacing, MathF.Max(MathF.Max(extent.X, extent.Z) / MaxAcross, extent.Y / MaxUp));
        dims = (Math.Max((int)MathF.Ceiling(extent.X / spacing), 2), Math.Max((int)MathF.Ceiling(extent.Y / spacing), 2), Math.Max((int)MathF.Ceiling(extent.Z / spacing), 2));
        gridOrigin = min;
        int w = dims.X * 6, h = dims.Y * dims.Z;
        if (atlas[0] is { } made && made.Desc.Width == w && made.Desc.Height == h) { current = 0; return; }
        var cmd = ctx.Device.BeginImmediate();
        var clear = new ClearColorValue(0f, 0f, 0f, 0f);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        for (int i = 0; i < 2; i++)
        {
            if (atlas[i] is { } old) { ctx.Bindless.Free(BindlessKind.Texture2D, atlasIndex[i]); old.Dispose(); }
            var made2 = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16f, w, h, Use: TextureUse.Storage | TextureUse.Sampled | TextureUse.TransferDst, Name: "studio gi probes"));
            atlas[i] = made2;
            atlasIndex[i] = ctx.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(nearest, made2.View()));
            ctx.Device.Vk.CmdClearColorImage(cmd, ctx.Device.NativeImage(made2), ImageLayout.General, &clear, 1, &range);
        }
        ctx.Device.EndImmediate(cmd);
        current = 0;
    }

    void WriteLamps(float* target, Vector3 relativeTo)
    {
        for (int i = 0; i < lamps.Count; i++)
        {
            var at = lamps[i].Position - relativeTo;
            (target[i * 8], target[i * 8 + 1], target[i * 8 + 2], target[i * 8 + 3]) = (at.X, at.Y, at.Z, lamps[i].Radius);
            (target[i * 8 + 4], target[i * 8 + 5], target[i * 8 + 6]) = (lamps[i].Colour.X, lamps[i].Colour.Y, lamps[i].Colour.Z);
        }
    }

    void RunProbes()
    {
        var parameters = new ProbeParams
        {
            Origin = new Vector4(gridOrigin - scene.Origin, spacing), Dims = new Vector4(dims.X, dims.Y, dims.Z, lamps.Count), Night = new Vector4(0.02f, 0.03f, 0.05f, 0),
        };
        WriteLamps(parameters.Lamps, scene.Origin);
        var block = ctx.Frame.Constants.Write<ProbeParams>(new ReadOnlySpan<ProbeParams>(&parameters, 1), 256);
        var cmd = (CommandList)ctx.BeginNative("studio gi");
        cmd.Barrier(BarrierBatch.Full);
        var top = scene.Top!.Handle;
        var asWrite = new WriteDescriptorSetAccelerationStructureKHR { SType = StructureType.WriteDescriptorSetAccelerationStructureKhr, AccelerationStructureCount = 1, PAccelerationStructures = &top };
        var records = new DescriptorBufferInfo(ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
        var uniform = new DescriptorBufferInfo(ctx.Device.BufferAt(block.Handle.Index), block.Offset, (ulong)sizeof(ProbeParams));
        var previous = new DescriptorImageInfo(ctx.Device.SamplerAt(nearest.Index), ctx.Device.ViewAt(atlas[current]!.View().Index), ImageLayout.General);
        var output = new DescriptorImageInfo(default, ctx.Device.ViewAt(atlas[1 - current]!.View().Index), ImageLayout.General);
        var writes = stackalloc WriteDescriptorSet[5];
        writes[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.AccelerationStructureKhr };
        writes[1] = BufferWrite(1, DescriptorType.StorageBuffer, &records);
        writes[2] = BufferWrite(2, DescriptorType.UniformBuffer, &uniform);
        writes[3] = ImageWrite(3, DescriptorType.CombinedImageSampler, &previous);
        writes[4] = ImageWrite(4, DescriptorType.StorageImage, &output);
        cmd.BindPipeline(probePipeline);
        cmd.PushDescriptors(probeProgram, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 5), GpuBindPoint.Compute);
        cmd.BindBindlessTable(probeProgram, 1, GpuBindPoint.Compute);
        cmd.Dispatch((uint)((dims.X + 3) / 4), (uint)((dims.Y + 3) / 4), (uint)((dims.Z + 3) / 4));
        cmd.Barrier(BarrierBatch.Full);
        ctx.EndNative(cmd);
        current = 1 - current;
    }

    /// <summary>A ray from every picture pixel (of <paramref name="depth"/>, one in <paramref name="down"/> each way) to the first lamps, into the two visibility targets.</summary>
    public void TraceVisibility(GpuTexture depth, Matrix4x4 viewProjection, int depthWidth, int depthHeight, int down)
    {
        if (scene.Top is null || lamps.Count == 0) return;
        depthDown = Math.Max(down, 1);
        int w = depthWidth / depthDown, h = depthHeight / depthDown;
        if (visibilityA is null || width != w || height != h)
        {
            if (visibilityA is not null)
            {
                ctx.Bindless.Free(BindlessKind.Texture2D, visibilityIndexA);
                ctx.Bindless.Free(BindlessKind.Texture2D, visibilityIndexB);
                visibilityA.Dispose();
                visibilityB!.Dispose();
            }
            visibilityA = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, w, h, Use: TextureUse.Storage | TextureUse.Sampled, Name: "studio lamp visibility"));
            visibilityB = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, w, h, Use: TextureUse.Storage | TextureUse.Sampled, Name: "studio lamp visibility 2"));
            visibilityIndexA = ctx.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(linear, visibilityA.View()));
            visibilityIndexB = ctx.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(linear, visibilityB.View()));
            (width, height) = (w, h);
        }
        Matrix4x4.Invert(Matrix4x4.CreateTranslation(scene.Origin) * viewProjection, out var inverse);
        var parameters = new VisibilityParams { Inverse = inverse, Size = new Vector4(depthWidth, depthHeight, depthDown, lamps.Count) };
        WriteLamps(parameters.Lamps, scene.Origin);
        var block = ctx.Frame.Constants.Write<VisibilityParams>(new ReadOnlySpan<VisibilityParams>(&parameters, 1), 256);
        var cmd = (CommandList)ctx.BeginNative("studio lamp visibility");
        cmd.Barrier(BarrierBatch.Full);
        var top = scene.Top.Handle;
        var asWrite = new WriteDescriptorSetAccelerationStructureKHR { SType = StructureType.WriteDescriptorSetAccelerationStructureKhr, AccelerationStructureCount = 1, PAccelerationStructures = &top };
        var records = new DescriptorBufferInfo(ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
        var uniform = new DescriptorBufferInfo(ctx.Device.BufferAt(block.Handle.Index), block.Offset, (ulong)sizeof(VisibilityParams));
        var depthInfo = new DescriptorImageInfo(ctx.Device.SamplerAt(nearest.Index), ctx.Device.ViewAt(depth.View().Index), ImageLayout.General);
        var first = new DescriptorImageInfo(default, ctx.Device.ViewAt(visibilityA.View().Index), ImageLayout.General);
        var second = new DescriptorImageInfo(default, ctx.Device.ViewAt(visibilityB!.View().Index), ImageLayout.General);
        var writes = stackalloc WriteDescriptorSet[6];
        writes[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.AccelerationStructureKhr };
        writes[1] = BufferWrite(1, DescriptorType.StorageBuffer, &records);
        writes[2] = BufferWrite(2, DescriptorType.UniformBuffer, &uniform);
        writes[3] = ImageWrite(3, DescriptorType.CombinedImageSampler, &depthInfo);
        writes[4] = ImageWrite(4, DescriptorType.StorageImage, &first);
        writes[5] = ImageWrite(5, DescriptorType.StorageImage, &second);
        cmd.BindPipeline(visibilityPipeline);
        cmd.PushDescriptors(visibilityProgram, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 6), GpuBindPoint.Compute);
        cmd.BindBindlessTable(visibilityProgram, 1, GpuBindPoint.Compute);
        cmd.Dispatch((uint)((w + 7) / 8), (uint)((h + 7) / 8));
        cmd.Barrier(BarrierBatch.Full);
        ctx.EndNative(cmd);
    }

    /// <summary>The view block's fields the studio's shaders read: the grid (cutPlanes 28: origin, spacing; 29: atlas, the dimensions) and the visibility (30: its two targets, how much smaller than the picture).</summary>
    internal void Fill(ref ViewConstants v)
    {
        if (!Ready) return;
        v.CutPlanes[28] = new Vector4(gridOrigin, spacing);
        v.CutPlanes[29] = new Vector4(atlasIndex[current], dims.X, dims.Y, dims.Z);
        v.CutPlanes[30] = visibilityA is null ? default : new Vector4(visibilityIndexA, visibilityIndexB, depthDown, 0);
    }

    static WriteDescriptorSet BufferWrite(uint binding, DescriptorType type, DescriptorBufferInfo* info) =>
        new() { SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1, DescriptorType = type, PBufferInfo = info };

    static WriteDescriptorSet ImageWrite(uint binding, DescriptorType type, DescriptorImageInfo* info) =>
        new() { SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1, DescriptorType = type, PImageInfo = info };

    public void Dispose()
    {
        scene.Dispose();
        foreach (var t in atlas) t?.Dispose();
        visibilityA?.Dispose();
        visibilityB?.Dispose();
        ctx.Shaders.Dispose(probeProgram);
        ctx.Shaders.Dispose(visibilityProgram);
    }
}
