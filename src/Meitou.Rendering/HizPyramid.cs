using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Meitou.Rendering;

/// <summary>What a foliage cull dispatch needs of the previous frame's depth pyramid (<see cref="HizPyramid.ViewFor"/>): the buffer and the
/// vec4s <see cref="FoliageShaders"/>' <c>ViewData.hz</c> and <c>hzOff</c> hold (<see cref="Vectors"/>).</summary>
public readonly struct OcclusionView(BufferBinding buffer, Vector4[] vectors)
{
    /// <summary>The pyramid's storage buffer.</summary>
    public BufferBinding Buffer { get; } = buffer;
    /// <summary><see cref="HizPyramid.ViewVectors"/> vec4s: [0] the previous eye and the eye's step, [1..3] the previous view's right, up and back axes,
    /// [4] tan x, tan y, width and height of the depth, [5] pixels per unit of tangent, level count, level 0's width and height, [6..8] the level offsets (as uints).</summary>
    public Vector4[] Vectors { get; } = vectors;
    public bool IsEmpty => Buffer.IsNull;
}

/// <summary>
/// The previous frame's depth as a pyramid of the nearest and farthest view-space distance (z along the view axis) in each block of pixels, for the
/// foliage cull's occlusion test (docs/formats/foliage.md "Occlusion culling"). Built at the end of every frame from the scene's depth buffers
/// (<see cref="Build"/>: a compute kernel takes 4 x 4 pixels into each texel of level 0, then one per level halves it), read by the next frame's
/// cull from a storage buffer (<c>vec2</c> per texel: the least and the greatest distance, concatenated by level). A pixel that no geometry covered
/// (the sky, the cleared depth) is infinitely far. Render thread only.
/// </summary>
public sealed unsafe class HizPyramid : IDisposable
{
    /// <summary>Texels of level 0 are this many depth pixels on a side.</summary>
    public const int BaseBlock = 4;
    /// <summary>Most levels (level 0 is 4 pixels a texel; 12 levels reach 8192 texels).</summary>
    public const int MaxLevels = 12;
    /// <summary>The vec4s <see cref="ViewFor"/> writes (and <c>ViewData</c> reads): 6 vec4 and 3 uvec4.</summary>
    public const int ViewVectors = 9;
    /// <summary>The levels the first kernel makes (the others come from the second).</summary>
    const int FusedLevels = 5;

    readonly GpuContext ctx;
    readonly ShaderProgram baseProgram, reduceProgram;
    readonly ComputePipeline basePipeline, reducePipeline;
    DeviceBuffer? buffer;
    readonly uint[] offsets = new uint[MaxLevels];
    int levelCount, width, height, baseWidth, baseHeight;
    // The camera the depth was drawn from.
    Vector3 eye, right, up, back;
    float tanX, tanY;
    long builtFrame = long.MinValue;
    Sampler sampler;

    public HizPyramid(GpuContext ctx)
    {
        this.ctx = ctx;
        baseProgram = ctx.Shaders.Compute(HizShaders.Base, "hiz base");
        reduceProgram = ctx.Shaders.Compute(HizShaders.Reduce, "hiz reduce");
        basePipeline = ctx.Pipelines.Get(new ComputePipelineDesc(baseProgram, "hiz base"));
        reducePipeline = ctx.Pipelines.Get(new ComputePipelineDesc(reduceProgram, "hiz reduce"));
        sampler = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
            TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, 1, false, 0));
    }

    /// <summary>Whether a pyramid is held (built since the depth's size last changed).</summary>
    public bool HasData => buffer is not null && builtFrame != long.MinValue;
    /// <summary>The level count and level 0's size, for the statistics.</summary>
    public string Describe => HasData ? $"{levelCount} levels from {baseWidth} x {baseHeight}, {buffer!.Size / 1024} KB" : "none";
    internal DeviceBuffer? Storage => buffer;
    /// <summary>Frames built.</summary>
    public long Builds { get; private set; }

    /// <summary>The level sizes of a depth of <paramref name="w"/> x <paramref name="h"/> pixels: level 0 is a quarter of it on each side (rounded up), each next one half, to 1 x 1 or <see cref="MaxLevels"/>.</summary>
    public static List<(int W, int H)> Levels(int w, int h)
    {
        var levels = new List<(int, int)>();
        int lw = (w + BaseBlock - 1) / BaseBlock, lh = (h + BaseBlock - 1) / BaseBlock;
        while (true)
        {
            levels.Add((lw, lh));
            if ((lw == 1 && lh == 1) || levels.Count == MaxLevels) break;
            lw = (lw + 1) / 2;
            lh = (lh + 1) / 2;
        }
        return levels;
    }

    /// <summary>
    /// Records the pyramid of the scene's depth into <paramref name="cmd"/> (outside any rendering): the near slice's depth <paramref name="near"/>, and
    /// where that is cleared the far slice's <paramref name="far"/> (null: none was drawn), each with the planes it was drawn with. <paramref name="viewRotation"/>
    /// is the camera's view without its translation (the world to view rotation, as <c>PostProcess</c> keeps it), <paramref name="fieldOfView"/> and <paramref name="aspect"/> the
    /// projection's.
    /// </summary>
    public void Build(CommandList cmd, Texture near, Texture? far, Vector2 nearPlanes, Vector2 farPlanes, int w, int h, Vector3 eyePosition, Matrix4x4 viewRotation, float fieldOfView, float aspect)
    {
        var levels = Levels(w, h);
        ulong texels = 0;
        foreach (var (lw, lh) in levels) texels += (ulong)lw * (ulong)lh;
        if (buffer is null || buffer.Size < texels * 8 || width != w || height != h)
        {
            buffer?.Dispose();   // deferred: earlier frames' cull may still read it
            buffer = DeviceBuffer.Create(ctx, texels * 8, BufferUse.Storage | BufferUse.TransferSrc, "hiz pyramid");
        }
        (width, height, baseWidth, baseHeight, levelCount) = (w, h, levels[0].W, levels[0].H, levels.Count);
        uint offset = 0;
        for (int i = 0; i < levels.Count; i++) { offsets[i] = offset; offset += (uint)(levels[i].W * levels[i].H); }
        eye = eyePosition;
        (right, up, back) = (new Vector3(viewRotation.M11, viewRotation.M21, viewRotation.M31), new Vector3(viewRotation.M12, viewRotation.M22, viewRotation.M32),
            new Vector3(viewRotation.M13, viewRotation.M23, viewRotation.M33));
        tanY = MathF.Tan(fieldOfView * 0.5f);
        tanX = tanY * aspect;

        var depthView = near.View();
        var farView = (far ?? near).View();
        cmd.BeginLabel("hiz");
        // The depth was just drawn (the scene's passes, in whichever layout they leave it: GENERAL), and the last frame's cull may still read the buffer.
        cmd.Barrier(BarrierBatch.Full);
        {
            // Levels 0 to 4 in one dispatch (a 16 x 16 tile of level 0 per workgroup, the rest through shared memory).
            var infos = stackalloc DescriptorImageInfo[2];
            infos[0] = new DescriptorImageInfo(sampler, depthView, ImageLayout.General);
            infos[1] = new DescriptorImageInfo(sampler, farView, ImageLayout.General);
            var bufferInfo = new DescriptorBufferInfo(buffer.Handle, 0, buffer.Size);
            var writes = stackalloc WriteDescriptorSet[3];
            writes[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &infos[0] };
            writes[1] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 1, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &infos[1] };
            writes[2] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 2, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &bufferInfo };
            cmd.BindPipeline(basePipeline);
            cmd.PushDescriptors(baseProgram.Layout, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 3), PipelineBindPoint.Compute);
            var push = new BasePush
            {
                Near = nearPlanes, Far = farPlanes, Width = (uint)w, Height = (uint)h, BaseWidth = (uint)levels[0].W, BaseHeight = (uint)levels[0].H, HasFar = far is null ? 0u : 1u,
                Levels = (uint)levels.Count, Offset0 = offsets[0], Offset1 = offsets[1], Offset2 = offsets[2], Offset3 = offsets[3], Offset4 = offsets[4],
            };
            cmd.PushConstants(baseProgram.Layout, ShaderStageFlags.ComputeBit, in push);
            cmd.Dispatch((uint)((levels[0].W + 15) / 16), (uint)((levels[0].H + 15) / 16));
        }
        if (levels.Count > FusedLevels)
        {
            // The rest (at most about a thousand texels a level) in one workgroup, a level at a time.
            cmd.Barrier(BarrierBatch.Full);
            var bufferInfo = new DescriptorBufferInfo(buffer.Handle, 0, buffer.Size);
            var write = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &bufferInfo };
            cmd.BindPipeline(reducePipeline);
            cmd.PushDescriptors(reduceProgram.Layout, 0, new ReadOnlySpan<WriteDescriptorSet>(&write, 1), PipelineBindPoint.Compute);
            var push = new ReducePush { BaseWidth = (uint)levels[0].W, BaseHeight = (uint)levels[0].H, Levels = (uint)levels.Count };
            for (int i = 0; i < MaxLevels; i++) push.Offsets[i] = i < levels.Count ? offsets[i] : 0;
            cmd.PushConstants(reduceProgram.Layout, ShaderStageFlags.ComputeBit, in push);
            cmd.Dispatch(1);
        }
        // The cull reads it in the next frame's pre-frame commands.
        cmd.Barrier(BarrierBatch.Full);
        cmd.EndLabel();
        builtFrame = ctx.Frame.Number;
        Builds++;
    }

    /// <summary>Forgets the pyramid (the depth's size or the camera's continuity changed): no frame uses it until the next <see cref="Build"/>.</summary>
    public void Invalidate() => builtFrame = long.MinValue;

    /// <summary>
    /// What the current frame's cull may use: the last frame's pyramid when it is the frame just before this one, and the eye <paramref name="current"/> has not
    /// moved <paramref name="maxStep"/> since (a cut or a teleport reads as a reset, as <c>PostProcess.RunUpscale</c> does at 5000); else empty.
    /// </summary>
    public OcclusionView ViewFor(Vector3 current, float maxStep = 5000f)
    {
        if (!HasData || ctx.Frame.Number - builtFrame > 1) return default;
        float step = Vector3.Distance(current, eye);
        if (!(step <= maxStep)) return default;
        return new OcclusionView(new BufferBinding(buffer!.Handle, 0, buffer.Size), Vectors(eye, step, right, up, back, tanX, tanY, width, height, levelCount, baseWidth, baseHeight, offsets));
    }

    /// <summary>The vec4s of <see cref="OcclusionView.Vectors"/>: the camera the depth was drawn from (<paramref name="eye"/>, view axes, tangents of half the field of view, size) and the eye's step since.</summary>
    internal static Vector4[] Vectors(Vector3 eye, float step, Vector3 right, Vector3 up, Vector3 back, float tanX, float tanY, int width, int height, int levelCount, int baseWidth, int baseHeight, uint[] offsets)
    {
        var v = new Vector4[ViewVectors];
        v[0] = new Vector4(eye, step);
        v[1] = new Vector4(right, 0);
        v[2] = new Vector4(up, 0);
        v[3] = new Vector4(back, 0);
        v[4] = new Vector4(tanX, tanY, width, height);
        v[5] = new Vector4(height * 0.5f / tanY, levelCount, baseWidth, baseHeight);
        for (int i = 0; i < MaxLevels; i++)
            v[6 + i / 4] = SetComponent(v[6 + i / 4], i % 4, BitConverter.UInt32BitsToSingle(i < levelCount ? offsets[i] : 0));
        return v;
    }

    static Vector4 SetComponent(Vector4 v, int i, float value)
    {
        switch (i) { case 0: v.X = value; break; case 1: v.Y = value; break; case 2: v.Z = value; break; default: v.W = value; break; }
        return v;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BasePush
    {
        public Vector2 Near, Far;
        public uint Width, Height, BaseWidth, BaseHeight, HasFar, Levels, Offset0, Offset1, Offset2, Offset3, Offset4;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ReducePush
    {
        public uint BaseWidth, BaseHeight, Levels, Pad;
        public OffsetArray Offsets;

        [System.Runtime.CompilerServices.InlineArray(MaxLevels)]
        public struct OffsetArray { uint first; }
    }

    public void Dispose()
    {
        foreach (var p in new[] { baseProgram, reduceProgram })
        {
            ctx.Pipelines.Forget(p);
            p.Dispose();
        }
        buffer?.Dispose();
    }
}

/// <summary>The kernels of <see cref="HizPyramid"/>.</summary>
static class HizShaders
{
    /// <summary>Distances beyond this stand for "no geometry" (the sky): nothing is hidden by them.</summary>
    public const float Infinite = 1e9f;

    /// <summary>
    /// Levels 0 to 4: a workgroup of 16 x 16 threads takes 64 x 64 pixels. Each thread makes a texel of level 0 (the least and greatest view distance over its 4 x 4
    /// pixels; the distance is rebuilt from the depth as the velocity pass does, <c>UpscaleShaders.Velocity</c>: the near slice's depth, else the far slice's, GL's
    /// 0.5..1 range of a D3D-style projection; a cleared pixel, 1.0, is infinitely far), and the later levels come through shared memory (a texel out of range is
    /// the neutral (+inf, 0), which gives the clamped result of a ceiling-sized level). Level k at (x, y) is written when it exists (k below the level count).
    /// </summary>
    public const string Base = """
        #version 450
        layout(local_size_x = 16, local_size_y = 16) in;
        layout(set = 0, binding = 0) uniform sampler2D uNear;
        layout(set = 0, binding = 1) uniform sampler2D uFar;
        layout(std430, set = 0, binding = 2) writeonly buffer Hiz { vec2 hiz[]; };
        layout(push_constant) uniform Push
        {
            vec2 nearPlanes; vec2 farPlanes;
            uint width; uint height; uint baseWidth; uint baseHeight; uint hasFar; uint levels;
            uint off0; uint off1; uint off2; uint off3; uint off4;
        } pc;
        shared vec2 tile[256];
        float viewZ(float d, vec2 nf) { float zd = 2.0 * d - 1.0; return nf.x * nf.y / (nf.y - zd * (nf.y - nf.x)); }
        uint offsetOf(uint k) { return k == 0u ? pc.off0 : k == 1u ? pc.off1 : k == 2u ? pc.off2 : k == 3u ? pc.off3 : pc.off4; }
        void main()
        {
            uvec2 t = gl_GlobalInvocationID.xy, l = gl_LocalInvocationID.xy;
            uint li = l.y * 16u + l.x;
            vec2 r = vec2(3.0e38, 0.0);
            if (t.x < pc.baseWidth && t.y < pc.baseHeight)
            {
                for (uint dy = 0u; dy < 4u; dy++)
                    for (uint dx = 0u; dx < 4u; dx++)
                    {
                        uvec2 p = t * 4u + uvec2(dx, dy);
                        if (p.x >= pc.width || p.y >= pc.height) continue;
                        float d = texelFetch(uNear, ivec2(p), 0).r;
                        vec2 planes = pc.nearPlanes;
                        if (d >= 1.0 && pc.hasFar != 0u) { d = texelFetch(uFar, ivec2(p), 0).r; planes = pc.farPlanes; }
                        float z = d >= 1.0 ? 1.0e9 : viewZ(d, planes);
                        r = vec2(min(r.x, z), max(r.y, z));
                    }
                hiz[pc.off0 + t.y * pc.baseWidth + t.x] = r;
            }
            tile[li] = r;
            barrier();
            for (uint k = 1u; k <= 4u; k++)
            {
                uint stride = 1u << k, hs = stride >> 1u;
                bool live = (l.x & (stride - 1u)) == 0u && (l.y & (stride - 1u)) == 0u;
                vec2 v = vec2(3.0e38, 0.0);
                if (live)
                {
                    vec2 a = tile[li], b = tile[li + hs], c = tile[li + hs * 16u], d = tile[li + hs * 16u + hs];
                    v = vec2(min(min(a.x, b.x), min(c.x, d.x)), max(max(a.y, b.y), max(c.y, d.y)));
                }
                barrier();
                if (live)
                {
                    tile[li] = v;
                    uvec2 at = t >> k;
                    uint w = (pc.baseWidth + stride - 1u) >> k, h = (pc.baseHeight + stride - 1u) >> k;
                    if (k < pc.levels && at.x < w && at.y < h) hiz[offsetOf(k) + at.y * w + at.x] = v;
                }
                barrier();
            }
        }
        """;

    /// <summary>The levels from 5 up, one workgroup, a level at a time (each texel the least and greatest of the 2 x 2 below it, clamped at the edge).</summary>
    public const string Reduce = """
        #version 450
        layout(local_size_x = 256) in;
        layout(std430, set = 0, binding = 0) buffer Hiz { vec2 hiz[]; };
        layout(push_constant) uniform Push { uint baseWidth; uint baseHeight; uint levels; uint pad; uvec4 offsets[3]; } pc;
        uint offsetOf(uint l) { return pc.offsets[l >> 2u][l & 3u]; }
        void main()
        {
            for (uint l = 5u; l < pc.levels; l++)
            {
                uint stride = 1u << l, sstride = stride >> 1u;
                uint dw = (pc.baseWidth + stride - 1u) >> l, dh = (pc.baseHeight + stride - 1u) >> l;
                uint sw = (pc.baseWidth + sstride - 1u) >> (l - 1u), sh = (pc.baseHeight + sstride - 1u) >> (l - 1u);
                for (uint i = gl_LocalInvocationIndex; i < dw * dh; i += 256u)
                {
                    uvec2 t = uvec2(i % dw, i / dw);
                    vec2 r = vec2(3.0e38, 0.0);
                    for (uint dy = 0u; dy < 2u; dy++)
                        for (uint dx = 0u; dx < 2u; dx++)
                        {
                            uvec2 s = min(t * 2u + uvec2(dx, dy), uvec2(sw - 1u, sh - 1u));
                            vec2 v = hiz[offsetOf(l - 1u) + s.y * sw + s.x];
                            r = vec2(min(r.x, v.x), max(r.y, v.y));
                        }
                    hiz[offsetOf(l) + t.y * dw + t.x] = r;
                }
                memoryBarrierBuffer();
                barrier();
            }
        }
        """;
}
