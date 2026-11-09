using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Gpu;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;


namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// What a foliage cull dispatch needs of the previous frame's depth pyramid (<see cref="HizPyramid.ViewFor"/>): the buffer and the vec4s
/// <see cref="FoliageShaders"/>' <c>ViewData.hz</c> and <c>hzOff</c> hold (<see cref="Vectors"/>).
/// </summary>
public readonly struct OcclusionView(GpuBufferBinding buffer, Vector4[] vectors)
{
    /// <summary>
    /// The pyramid's storage buffer.
    /// </summary>
    public GpuBufferBinding Buffer { get; } = buffer;

    /// <summary>
    /// <see cref="HizPyramid.ViewVectors"/> vec4s: [0] the previous eye and the eye's step, [1..3] the previous view's right, up and back axes, [4]
    /// tan x, tan y, width and height of the depth, [5] pixels per unit of tangent, level count, level 0's width and height, [6..8] the level offsets
    /// (as uints).
    /// </summary>
    public Vector4[] Vectors { get; } = vectors;

    /// <summary>
    /// Whether there is no pyramid to test against (a default view).
    /// </summary>
    public bool IsEmpty => Buffer.IsNull;
}

/// <summary>
/// The previous frame's depth as a pyramid of the nearest and farthest view-space distance (z along the view axis) in each block of pixels, for the
/// foliage cull's occlusion test (docs/formats/foliage.md "Occlusion culling"). Built at the end of every frame from the scene's depth buffer
/// (<see cref="Build"/>: a compute kernel takes 4 x 4 pixels into each texel of level 0, then one per level halves it), read by the next frame's
/// cull from a storage buffer (<c>vec2</c> per texel: the least and the greatest distance, concatenated by level). A pixel that no geometry covered
/// (the sky, the cleared depth) is infinitely far. Render thread only.
/// </summary>
public sealed unsafe class HizPyramid : IDisposable
{
    #region Constants and state

    /// <summary>
    /// Texels of level 0 are this many depth pixels on a side.
    /// </summary>
    public const int BaseBlock = 4;

    /// <summary>
    /// Most levels (level 0 is 4 pixels a texel; 12 levels reach 8192 texels).
    /// </summary>
    public const int MaxLevels = 12;

    /// <summary>
    /// The vec4s <see cref="ViewFor"/> writes (and <c>ViewData</c> reads): 6 vec4 and 3 uvec4.
    /// </summary>
    public const int ViewVectors = 9;

    /// <summary>
    /// The levels the first kernel makes (the others come from the second).
    /// </summary>
    const int FusedLevels = 5;

    /// <summary>
    /// The context the pyramid is built and read on.
    /// </summary>
    readonly GpuContext _ctx;

    /// <summary>
    /// The two compute programs: the base kernel (the first <see cref="FusedLevels"/> levels from the depth) and the reduce kernel (the rest).
    /// </summary>
    readonly GpuProgram _baseProgram, _reduceProgram;

    /// <summary>
    /// The compute pipelines of <see cref="_baseProgram"/> and <see cref="_reduceProgram"/>.
    /// </summary>
    readonly GpuComputePipeline _basePipeline, _reducePipeline;

    /// <summary>
    /// The storage buffer holding every level, 8 bytes a texel; null until the first build.
    /// </summary>
    DeviceBuffer? _buffer;

    /// <summary>
    /// Where each level starts in <see cref="_buffer"/>, in texels.
    /// </summary>
    readonly uint[] _offsets = new uint[MaxLevels];

    /// <summary>
    /// The level count, the depth's size and level 0's size of the pyramid held.
    /// </summary>
    int _levelCount, _width, _height, _baseWidth, _baseHeight;

    /// <summary>
    /// The camera the depth was drawn from: its eye and view axes.
    /// </summary>
    Vector3 _eye, _right, _up, _back;

    /// <summary>
    /// The tangents of half the field of view across and up, of the camera the depth was drawn from.
    /// </summary>
    float _tanX, _tanY;

    /// <summary>
    /// The frame the pyramid was last built in; <see cref="long.MinValue"/>: none held.
    /// </summary>
    long _builtFrame = long.MinValue;

    /// <summary>
    /// The nearest-filtered, edge-clamped sampler the base kernel reads the depth with.
    /// </summary>
    readonly GpuSampler _sampler;

    #endregion

    #region Pyramid

    /// <summary>
    /// Makes the two compute programs, their pipelines and the depth sampler.
    /// </summary>
    public HizPyramid(GpuContext ctx)
    {
        this._ctx = ctx;
        _baseProgram = ctx.Shaders.Compute(HizShaders.Base, "hiz base");
        _reduceProgram = ctx.Shaders.Compute(HizShaders.Reduce, "hiz reduce");
        _basePipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_baseProgram, "hiz base"));
        _reducePipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_reduceProgram, "hiz reduce"));
        _sampler = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge,
            TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, 1, false, 0));
    }

    /// <summary>
    /// Whether a pyramid is held (built since the depth's size last changed).
    /// </summary>
    public bool HasData => _buffer is not null && _builtFrame != long.MinValue;

    /// <summary>
    /// The level count and level 0's size, for the statistics.
    /// </summary>
    public string Describe => HasData ? $"{_levelCount} levels from {_baseWidth} x {_baseHeight}, {_buffer!.Size / 1024} KB" : "none";

    /// <summary>
    /// The pyramid's storage buffer; null until the first build.
    /// </summary>
    internal DeviceBuffer? Storage => _buffer;

    /// <summary>
    /// Frames built.
    /// </summary>
    public long Builds { get; private set; }

    /// <summary>
    /// The level sizes of a depth of <paramref name="w"/> x <paramref name="h"/> pixels: level 0 is a quarter of it on each side (rounded up), each
    /// next one half, to 1 x 1 or <see cref="MaxLevels"/>.
    /// </summary>
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
    /// Records the pyramid of the scene's depth <paramref name="depth"/>, drawn with the planes <paramref name="planes"/> (near, far), into
    /// <paramref name="cmd"/> (outside any rendering). <paramref name="viewRotation"/> is the camera's view without its translation (the world to
    /// view rotation, as <c>PostProcess</c> keeps it), <paramref name="fieldOfView"/> and <paramref name="aspect"/> the projection's.
    /// </summary>
    public void Build(IGpuCommandList cmd, GpuTexture depth, Vector2 planes, int w, int h, Vector3 eyePosition, Matrix4x4 viewRotation,
        float fieldOfView, float aspect)
    {
        var levels = Levels(w, h);
        Allocate(levels, w, h);
        KeepCamera(eyePosition, viewRotation, fieldOfView, aspect);

        cmd.BeginLabel("hiz");

        // The depth was just drawn (the scene's passes, in whichever layout they leave it: GENERAL), and the last frame's cull may still
        // read the buffer.
        cmd.Order();
        RecordBase(cmd, depth, planes, levels);
        if (levels.Count > FusedLevels)
        {
            cmd.Order();
            RecordReduce(cmd, levels);
        }

        // The cull reads it in the next frame's pre-frame commands.
        cmd.Order();
        cmd.EndLabel();
        _builtFrame = _ctx.Frame.Number;
        Builds++;
    }

    /// <summary>
    /// Sizes the storage for the <paramref name="levels"/> of a <paramref name="w"/> x <paramref name="h"/> depth and lays them out.
    /// </summary>
    void Allocate(List<(int W, int H)> levels, int w, int h)
    {
        ulong texels = 0;
        foreach (var (lw, lh) in levels) texels += (ulong)lw * (ulong)lh;
        if (_buffer is null || _buffer.Size < texels * 8 || _width != w || _height != h)
        {
            _buffer?.Dispose();   // deferred: earlier frames' cull may still read it
            _buffer = DeviceBuffer.Create(_ctx, texels * 8, BufferUse.Storage | BufferUse.TransferSrc, "hiz pyramid");
        }
        (_width, _height, _baseWidth, _baseHeight, _levelCount) = (w, h, levels[0].W, levels[0].H, levels.Count);
        uint offset = 0;
        for (int i = 0; i < levels.Count; i++)
        {
            _offsets[i] = offset;
            offset += (uint)(levels[i].W * levels[i].H);
        }
    }

    /// <summary>
    /// The camera the depth was drawn from, for <see cref="ViewFor"/>.
    /// </summary>
    void KeepCamera(Vector3 eyePosition, Matrix4x4 viewRotation, float fieldOfView, float aspect)
    {
        _eye = eyePosition;
        _right = new Vector3(viewRotation.M11, viewRotation.M21, viewRotation.M31);
        _up = new Vector3(viewRotation.M12, viewRotation.M22, viewRotation.M32);
        _back = new Vector3(viewRotation.M13, viewRotation.M23, viewRotation.M33);
        _tanY = MathF.Tan(fieldOfView * 0.5f);
        _tanX = _tanY * aspect;
    }

    /// <summary>
    /// Levels 0 to 4 in one dispatch (a 16 x 16 tile of level 0 per workgroup, the rest through shared memory).
    /// </summary>
    void RecordBase(IGpuCommandList cmd, GpuTexture depth, Vector2 planes, List<(int W, int H)> levels)
    {
        var bindings = stackalloc GpuBinding[2];
        bindings[0] = GpuBinding.Sampled(0, new SampledTexture(_sampler, depth.View()));
        bindings[1] = GpuBinding.Storage(1, new GpuBufferBinding(_buffer!.Handle, 0, _buffer.Size));
        cmd.BindPipeline(_basePipeline);
        cmd.BindProgramResources(_baseProgram, 0, new ReadOnlySpan<GpuBinding>(bindings, 2));
        var push = new BasePush
        {
            Planes = planes,
            Width = (uint)_width,
            Height = (uint)_height,
            BaseWidth = (uint)levels[0].W,
            BaseHeight = (uint)levels[0].H,
            Levels = (uint)levels.Count,
            Offset0 = _offsets[0],
            Offset1 = _offsets[1],
            Offset2 = _offsets[2],
            Offset3 = _offsets[3],
            Offset4 = _offsets[4],
        };
        cmd.PushConstants(_baseProgram, GpuStage.Compute, in push);
        cmd.Dispatch((uint)((levels[0].W + 15) / 16), (uint)((levels[0].H + 15) / 16));
    }

    /// <summary>
    /// The rest (at most about a thousand texels a level) in one workgroup, a level at a time.
    /// </summary>
    void RecordReduce(IGpuCommandList cmd, List<(int W, int H)> levels)
    {
        var bindings = stackalloc GpuBinding[1];
        bindings[0] = GpuBinding.Storage(0, new GpuBufferBinding(_buffer!.Handle, 0, _buffer.Size));
        cmd.BindPipeline(_reducePipeline);
        cmd.BindProgramResources(_reduceProgram, 0, new ReadOnlySpan<GpuBinding>(bindings, 1));
        var push = new ReducePush { BaseWidth = (uint)levels[0].W, BaseHeight = (uint)levels[0].H, Levels = (uint)levels.Count };
        for (int i = 0; i < MaxLevels; i++) push.Offsets[i] = i < levels.Count ? _offsets[i] : 0;
        cmd.PushConstants(_reduceProgram, GpuStage.Compute, in push);
        cmd.Dispatch(1);
    }

    /// <summary>
    /// Forgets the pyramid (the depth's size or the camera's continuity changed): no frame uses it until the next <see cref="Build"/>.
    /// </summary>
    public void Invalidate() => _builtFrame = long.MinValue;

    #endregion

    #region Occlusion view

    /// <summary>
    /// What the current frame's cull may use: the last frame's pyramid when it is the frame just before this one, and the eye <paramref
    /// name="current"/> has not
    /// moved <paramref name="maxStep"/> since (a cut or a teleport reads as a reset, as <c>PostProcess.RunUpscale</c> does at 5000); else empty.
    /// </summary>
    public OcclusionView ViewFor(Vector3 current, float maxStep = 5000f)
    {
        if (!HasData || _ctx.Frame.Number - _builtFrame > 1) return default;
        float step = Vector3.Distance(current, _eye);
        if (!(step <= maxStep)) return default;
        return new OcclusionView(new GpuBufferBinding(_buffer!.Handle, 0, _buffer.Size), Vectors(_eye, step, _right, _up, _back, _tanX, _tanY, _width,
            _height, _levelCount, _baseWidth, _baseHeight, _offsets));
    }

    /// <summary>
    /// The vec4s of <see cref="OcclusionView.Vectors"/>: the camera the depth was drawn from (<paramref name="eye"/>, view axes, tangents of half the
    /// field of view, size) and the eye's step since.
    /// </summary>
    internal static Vector4[] Vectors(Vector3 eye, float step, Vector3 right, Vector3 up, Vector3 back, float tanX, float tanY, int width, int height,
        int levelCount, int baseWidth, int baseHeight, uint[] offsets)
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

    /// <summary>
    /// <paramref name="v"/> with component <paramref name="i"/> (0 x, 1 y, 2 z, else w) set to <paramref name="value"/>.
    /// </summary>
    static Vector4 SetComponent(Vector4 v, int i, float value)
    {
        switch (i)
        {
            case 0:
                v.X = value;
                break;
            case 1:
                v.Y = value;
                break;
            case 2:
                v.Z = value;
                break;
            default:
                v.W = value;
                break;
        }

        return v;
    }

    /// <summary>
    /// The base kernel's push constants.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct BasePush
    {
        /// <summary>
        /// The depth's near and far planes.
        /// </summary>
        public Vector2 Planes;

        /// <summary>
        /// The depth's size, level 0's size, the level count, and where the first <see cref="FusedLevels"/> levels start in the buffer.
        /// </summary>
        public uint Width, Height, BaseWidth, BaseHeight, Levels, Offset0, Offset1, Offset2, Offset3, Offset4;
    }

    /// <summary>
    /// The reduce kernel's push constants.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct ReducePush
    {
        /// <summary>
        /// Level 0's size, the level count, and padding.
        /// </summary>
        public uint BaseWidth, BaseHeight, Levels, Pad;

        /// <summary>
        /// Where each of the <see cref="MaxLevels"/> levels starts in the buffer (0 past the level count).
        /// </summary>
        public OffsetArray Offsets;

        /// <summary>
        /// <see cref="MaxLevels"/> level offsets inline.
        /// </summary>
        [System.Runtime.CompilerServices.InlineArray(MaxLevels)]
        public struct OffsetArray
        {
            /// <summary>
            /// The first element: the runtime lays the others out after it.
            /// </summary>
            uint _first;
        }
    }

    #endregion

    #region Disposal

    /// <summary>
    /// Drops the two programs with their pipelines and the pyramid's buffer.
    /// </summary>
    public void Dispose()
    {
        foreach (var p in new[] { _baseProgram, _reduceProgram })
        {
            _ctx.Pipelines.Forget(p);
            _ctx.Shaders.Dispose(p);
        }
        _buffer?.Dispose();
    }

    #endregion
}

/// <summary>
/// The kernels of <see cref="HizPyramid"/>.
/// </summary>
static class HizShaders
{
    /// <summary>
    /// Distances beyond this stand for "no geometry" (the sky): nothing is hidden by them.
    /// </summary>
    public const float Infinite = 1e9f;

    /// <summary>
    /// Levels 0 to 4: a workgroup of 16 x 16 threads takes 64 x 64 pixels. Each thread makes a texel of level 0 (the least and greatest view distance
    /// over its 4 x 4 pixels; the distance is rebuilt from the scene depth as the velocity pass does, <c>UpscaleShaders.Velocity</c>, through
    /// <see cref="DepthConvention.Glsl"/>; a cleared pixel, the sky, is infinitely far), and the later levels come through shared memory (a texel
    /// out of range is the neutral (+inf, 0), which gives the clamped result of a ceiling-sized level). Level k at (x, y) is written when it exists
    /// (k below the level count).
    /// </summary>
    public const string Base = """
        #version 450
        layout(local_size_x = 16, local_size_y = 16) in;
        layout(set = 0, binding = 0) uniform sampler2D uDepth;
        layout(std430, set = 0, binding = 1) writeonly buffer Hiz { vec2 hiz[]; };
        layout(push_constant) uniform Push
        {
            vec2 planes;   // near, far of the scene depth
            uint width; uint height; uint baseWidth; uint baseHeight; uint levels;
            uint off0; uint off1; uint off2; uint off3; uint off4;
        } pc;
        shared vec2 tile[256];
        """ + DepthConvention.Glsl + """
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
                        float d = texelFetch(uDepth, ivec2(p), 0).r;
                        float z = depthIsSky(d) ? 1.0e9 : depthViewZ(d, pc.planes);
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

    /// <summary>
    /// The levels from 5 up, one workgroup, a level at a time (each texel the least and greatest of the 2 x 2 below it, clamped at the edge).
    /// </summary>
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