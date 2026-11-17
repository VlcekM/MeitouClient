using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// The penumbra filter's inputs, all at the render size: the scene depth and surface target, the rays' result, a scratch target and the output
/// (RGBA16F).
/// </summary>
readonly record struct PenumbraTargets(
    GpuTexture Depth, GpuTexture Surface, GpuTexture Raw, GpuTexture Scratch, GpuTexture Filtered, int Width, int Height);

/// <summary>
/// The penumbra filter of the traced shadows (docs/render-shadows.md "Traced sun", docs/render-lights.md "Traced lamp shadows"), the same every
/// frame (no history): the rays' hard result (rgb) softened by its light's size. The rays write, where they are blocked, the penumbra's radius at the
/// receiver in world units (alpha: the light's angular radius times the blocker's distance; -1 where the pixel is no receiver: it is kept and no
/// neighbour reads it). A horizontal pass finds the blockers' mean radius among taps on the pixel's own surface (its plane from the depth), takes it
/// to pixels (at least <see cref="MinRadius"/>, which anti-aliases a contact edge), and averages along the row with a box whose ends are fractional;
/// a vertical pass does the same along the column at that radius. The weights are continuous, so a wide penumbra is a smooth ramp, not a staircase
/// of tap counts. Render thread only.
/// </summary>
sealed unsafe class PenumbraFilter : IDisposable
{
    /// <summary>
    /// Taps of the blocker search (a sunflower within <see cref="Search"/> pixels).
    /// </summary>
    const int SearchTaps = 16;

    /// <summary>
    /// Pixels: the blocker search's reach.
    /// </summary>
    const float Search = 12;

    /// <summary>
    /// Pixels: the narrowest radius.
    /// </summary>
    const float MinRadius = 1;

    /// <summary>
    /// Pixels: the widest radius.
    /// </summary>
    const float MaxRadius = 16;

    /// <summary>
    /// How far past a filtered area the rows' pass reads (the search and the widest radius).
    /// </summary>
    public const int RowMargin = (int)(Search + MaxRadius) + 2;

    /// <summary>
    /// How far past a filtered area the columns' pass reads.
    /// </summary>
    public const int ColumnMargin = (int)MaxRadius;

    /// <summary>
    /// The filter's kernel, one pass along rows or columns (<c>size.w</c>) over an area or a list's cells.
    /// </summary>
    static readonly string Source = SourceFor(1, false);

    /// <summary>
    /// The kernel over the cells of a list of <see cref="LampShadowPass"/> (<see cref="LampShadowShaders.Classify"/>), each in its entry's slice of
    /// the targets (RG16F arrays: what the rays found, then the radius).
    /// </summary>
    static readonly string ListSource = SourceFor(1, true);

    /// <summary>
    /// The kernel over a target of half the size (<see cref="LampShadowShaders.TraceOneHalf"/>): a pixel stands for the pixel <c>area.zw</c> of
    /// its 2 x 2 of the full size, whose depth and plane it reads; the search and the radii are the same on the screen, half as many pixels.
    /// </summary>
    static readonly string HalfSource = SourceFor(2, true);

    /// <summary>
    /// The half-size kernel's narrowest radius, in its pixels: a contact edge keeps the rays' step, which the light kernel's mean of the flags
    /// round a pixel softens.
    /// </summary>
    const float HalfMinRadius = 0.5f;

    /// <summary>
    /// The kernel for targets <paramref name="step"/> times smaller than the depth (1 or 2); <paramref name="layered"/>: over a list's cells,
    /// the targets arrays of slices.
    /// </summary>
    static string SourceFor(int step, bool layered) => $$"""
        #version 460
        layout(local_size_x = 8, local_size_y = 8) in;
        #define MAX_CELLS {{LampShadowShaders.MaxCells}}
        layout(set = 0, binding = 0) uniform sampler2D uDepth;
        layout(set = 0, binding = 1) uniform usampler2D uSurface;
        layout(set = 0, binding = 2) uniform {{(layered ? "sampler2DArray" : "sampler2D")}} uIn;
        layout(set = 0, binding = 3) uniform sampler2D uPlanes;   // every pixel's plane (PlanesSource), made once a frame
        layout(set = 0, binding = 4, {{(layered ? "rg16f) uniform writeonly image2DArray" : "rgba16f) uniform writeonly image2D")}} uOut;
        layout(set = 0, binding = 6, std430) readonly buffer CellLists { uint lists[]; };   // LampShadowPass's lists (options.z)
        layout(set = 0, binding = 5, std140) uniform Params
        {
            mat4 inverse;      // NDC, jittered as the frame was -> position relative to the eye
            vec4 size;         // width, height, a pixel's width per unit of depth, the pass (0 along rows, then 1 along columns)
            vec4 options;      // x: the blue channel names what the channels hold; only taps with the pixel's own name are read; z: the pixels are a list's cells (area.x: where the list starts)
            vec4 area;         // the pixels filtered: first x, y, one past the last x, y
            uvec4 cells[16];   // with options.y: the cells worked on, a bit each
        } p;
        #define CELLS p.cells
        #define CELL_WIDTH ((int(p.size.x) + 63) / 64)
        #define STEP {{step}}

        // The slice worked on (a list's entry names it), and the targets through it: a slice holds what the rays found and the radius.
        int slice = 0;
        {{(layered ? """
        vec4 inAt(ivec2 q) { vec2 v = texelFetch(uIn, ivec3(q, slice), 0).rg; return vec4(v.r, 0.0, 0.0, v.g); }
        void outAt(ivec2 q, vec4 c) { imageStore(uOut, ivec3(q, slice), vec4(c.r, c.a, 0.0, 0.0)); }
        """ : """
        vec4 inAt(ivec2 q) { return texelFetch(uIn, q, 0); }
        void outAt(ivec2 q, vec4 c) { imageStore(uOut, q, c); }
        """)}}

        // The cells of 64 pixels this lamp's pass works on (LampShadowPass.CellSize): the mover-free rest of its area costs a test.
        bool cellOn(ivec2 q)
        {
            ivec2 c = q / 64;
            int i = c.y * CELL_WIDTH + c.x;
            return ((CELLS[i / 128][(i / 32) % 4] >> uint(i % 32)) & 1u) != 0u;
        }

        // The pixel of the depth a pixel of the targets stands for (size.xy is the depth's).
        ivec2 fullOf(ivec2 q) { return STEP == 1 ? q : q * STEP + ivec2(p.area.zw); }

        vec3 pointAt(ivec2 q)
        {
            ivec2 px = fullOf(q);
            vec4 h = p.inverse * vec4((vec2(px) + 0.5) / p.size.xy * 2.0 - 1.0, texelFetch(uDepth, px, 0).r, 1.0);
            return h.xyz / h.w;
        }

        bool inside(ivec2 q) { return all(greaterThanEqual(q, ivec2(0))) && all(lessThan(fullOf(q), ivec2(p.size.xy))); }

        // The drawn surface's plane from the depth (the neighbours of the smaller step): the geometry, not the shading normal, which a relief, a trail or a normal map
        // tilts until a caster's side or a leg lies in "its" plane.
        vec3 plane(ivec2 px, vec3 at)
        {
            ivec2 last = ivec2(p.size.xy) - 1;
            vec3 l = at - pointAt(clamp(px - ivec2(1, 0), ivec2(0), last)), r = pointAt(clamp(px + ivec2(1, 0), ivec2(0), last)) - at;
            vec3 d = at - pointAt(clamp(px - ivec2(0, 1), ivec2(0), last)), u = pointAt(clamp(px + ivec2(0, 1), ivec2(0), last)) - at;
            vec3 g = cross(dot(r, r) < dot(l, l) ? r : l, dot(u, u) < dot(d, d) ? u : d);
            float len = length(g);
            return len > 1e-8 ? g / len : vec3(0.0, 1.0, 0.0);
        }

        float ownName;

        // A tap on the pixel's own surface: a receiver (alpha not -1) within the plane's tolerance, holding the same things as the pixel.
        bool onSurface(ivec2 q, vec3 at, vec3 n, float tolerance)
        {
            if (!inside(q) || inAt(q).a < 0.0) return false;
            if (p.options.x > 0.5 && abs(inAt(q).b - ownName) > 0.5) return false;
            return abs(dot(pointAt(q) - at, n)) < tolerance;
        }

        // Whether a neighbour lies on a surface turned away from this one.
        bool onCrease(ivec2 px, vec3 n)
        {
            for (int k = 0; k < 4; k++)
            {
                ivec2 q = px + ivec2(k < 2 ? k * 2 - 1 : 0, k < 2 ? 0 : k * 2 - 5);
                if (inside(q) && dot(texelFetch(uPlanes, fullOf(q), 0).xyz, n) < 0.6) return true;
            }
            return false;
        }

        // The penumbra's radius in pixels from the blockers round the pixel (0: none within the search).
        float searchRadius(ivec2 px, vec3 at, vec3 n, float footprint, float own)
        {
            float sum = own, count = own > 0.0 ? 1.0 : 0.0;
            float tolerance = max(0.25, footprint * {{F(Search / step)}} * 0.5);
            for (int k = 0; k < {{SearchTaps}}; k++)
            {
                float r = sqrt((float(k) + 0.5) / {{SearchTaps}}.0), a = float(k) * 2.39996323;
                ivec2 q = px + ivec2(round(vec2(cos(a), sin(a)) * r * {{F(Search / step)}}));
                if (!onSurface(q, at, n, tolerance)) continue;
                float spread = inAt(q).a;
                if (spread > 0.0) { sum += spread; count += 1.0; }
            }
            {{(step == 1 ? $"return count == 0.0 ? 0.0 : clamp(sum / count / footprint, {F(MinRadius)}, {F(MaxRadius)});" : $"""
            if (count == 0.0) return 0.0;
            // Half size: the rays' step and the light kernel's mean of the flags round a pixel already spread an edge (half a pixel squared of variance here): the box is
            // that much narrower, so a penumbra is as wide on the screen as the full-size filter's. At {F(HalfMinRadius)} the box is the pixel alone.
            float wide = min(sum / count / footprint, {F(MaxRadius / step)});
            return sqrt(max(wide * wide - 0.5, {F(HalfMinRadius * HalfMinRadius)}));
            """)}}
        }

        void main()
        {
            ivec2 px;
            if (p.options.z > 0.5)
            {
                // A workgroup per 8 x 8 pixels of a listed cell (its column and row, 8 bits each, then its slice; LampShadowShaders.Classify): y the cell's place in the list, x the block in the cell.
                uint cell = lists[uint(p.area.x) + gl_WorkGroupID.y];
                slice = int((cell >> 16) & 7u);
                px = ivec2(cell & 255u, (cell >> 8) & 255u) * {{64 / step}} + ivec2(int(gl_WorkGroupID.x) & {{8 / step - 1}}, int(gl_WorkGroupID.x) >> {{(step == 1 ? 3 : 2)}}) * 8 + ivec2(gl_LocalInvocationID.xy);
                if (!inside(px)) return;
            }
            else
            {
                px = ivec2(gl_GlobalInvocationID.xy) + ivec2(p.area.xy);
                if (!inside(px) || px.x >= int(p.area.z) || px.y >= int(p.area.w)) return;
                if (p.options.y > 0.5 && !cellOn(px)) return;
            }
            vec4 c = inAt(px);
            ownName = c.b;
            bool rows = p.size.w < 0.5;
            if (c.a < 0.0) { outAt(px, c); return; }   // no receiver: kept, and left out of its neighbours
            if (!rows && c.a <= 0.0) { outAt(px, vec4(c.rgb, 0.0)); return; }
            if (rows && c.a <= 0.0)
            {
                bool blocker = false;
                for (int k = 0; k < {{SearchTaps}} && !blocker; k++)
                {
                    float r = sqrt((float(k) + 0.5) / {{SearchTaps}}.0), a = float(k) * 2.39996323;
                    ivec2 q = px + ivec2(round(vec2(cos(a), sin(a)) * r * {{F(Search / step)}}));
                    blocker = inside(q) && inAt(q).a > 0.0;
                }
                if (!blocker) { outAt(px, vec4(c.rgb, 0.0)); return; }
            }
            vec3 at = pointAt(px);
            float footprint = length(at) * p.size.z;
            vec3 n = texelFetch(uPlanes, fullOf(px), 0).xyz;
            float radius = rows ? searchRadius(px, at, n, footprint, c.a) : c.a;
            if (radius <= 0.0) { outAt(px, vec4(c.rgb, 0.0)); return; }
            ivec2 axis = rows ? ivec2(1, 0) : ivec2(0, 1);
            int reach = int(ceil(radius));
            vec3 sum = c.rgb;
            float weight = 1.0;
            for (int k = 1; k <= {{(int)MaxRadius / step}}; k++)
            {
                if (k > reach) break;
                float w = clamp(radius + 0.5 - float(k), 0.0, 1.0);
                float tolerance = max(0.25, footprint * (float(k) * 0.5 + 1.0));
                for (int side = -1; side <= 1; side += 2)
                {
                    ivec2 q = px + axis * (k * side);
                    if (!onSurface(q, at, n, tolerance)) continue;
                    // A tap beyond a crease (a wall's foot: its pixels pass the plane's tolerance there) is another surface, with other lamps' light.
                    if (dot(texelFetch(uPlanes, fullOf(q), 0).xyz, n) < 0.6) continue;
                    sum += inAt(q).rgb * w;
                    weight += w;
                }
            }
            vec3 result = sum / weight;
            // On a crease (a wall's foot) the pixel's own ray stands: what lies round it is another surface, and even a sliver of it left a lit line along the foot.
            if (any(greaterThan(abs(result - c.rgb), vec3(0.02))) && onCrease(px, n)) result = c.rgb;
            outAt(px, vec4(result, radius));
        }
        """;

    /// <summary>
    /// Every pixel's plane, once a frame (<see cref="EnsurePlanes"/>): the drawn surface's normal from the depth, as the filter's <c>plane</c>. The
    /// filter asked for a tap's plane at every tap of every pixel, five depths and five unprojections each: most of its time, in the traced sun's
    /// pass and in each lamp's.
    /// </summary>
    const string PlanesSource = """
        #version 460
        layout(local_size_x = 8, local_size_y = 8) in;
        layout(set = 0, binding = 0) uniform sampler2D uDepth;
        layout(set = 0, binding = 4, rgba16f) uniform writeonly image2D uOut;
        layout(set = 0, binding = 5, std140) uniform Params
        {
            mat4 inverse;
            vec4 size;
            vec4 options;
            vec4 area;
            uvec4 cells[16];
        } p;

        vec3 pointAt(ivec2 px)
        {
            vec4 h = p.inverse * vec4((vec2(px) + 0.5) / p.size.xy * 2.0 - 1.0, texelFetch(uDepth, px, 0).r, 1.0);
            return h.xyz / h.w;
        }

        void main()
        {
            ivec2 px = ivec2(gl_GlobalInvocationID.xy);
            if (px.x >= int(p.size.x) || px.y >= int(p.size.y)) return;
            vec3 at = pointAt(px);
            ivec2 last = ivec2(p.size.xy) - 1;
            vec3 l = at - pointAt(clamp(px - ivec2(1, 0), ivec2(0), last)), r = pointAt(clamp(px + ivec2(1, 0), ivec2(0), last)) - at;
            vec3 d = at - pointAt(clamp(px - ivec2(0, 1), ivec2(0), last)), u = pointAt(clamp(px + ivec2(0, 1), ivec2(0), last)) - at;
            vec3 g = cross(dot(r, r) < dot(l, l) ? r : l, dot(u, u) < dot(d, d) ? u : d);
            float len = length(g);
            imageStore(uOut, px, vec4(len > 1e-8 ? g / len : vec3(0.0, 1.0, 0.0), 1.0));
        }
        """;

    /// <summary>
    /// The planes' program and pipeline.
    /// </summary>
    readonly GpuProgram _planesProgram;

    /// <summary>
    /// See <see cref="_planesProgram"/>.
    /// </summary>
    readonly GpuComputePipeline _planesPipeline;

    /// <summary>
    /// The planes (<see cref="PlanesSource"/>), the frame they were made in and the depth they were made from.
    /// </summary>
    GpuTexture? _planes;

    /// <summary>
    /// See <see cref="_planes"/>.
    /// </summary>
    long _planesFrame = -1;

    /// <summary>
    /// See <see cref="_planes"/>.
    /// </summary>
    GpuTexture? _planesDepth;

    /// <summary>
    /// The device the kernel runs on.
    /// </summary>
    readonly GpuContext _ctx;

    /// <summary>
    /// The kernel's program.
    /// </summary>
    readonly GpuProgram _program;

    /// <summary>
    /// The kernel's pipeline.
    /// </summary>
    readonly GpuComputePipeline _pipeline;

    /// <summary>
    /// The kernels over a list's cells, at the depth's size (<see cref="ListSource"/>) and at half of it (<see cref="HalfSource"/>).
    /// </summary>
    readonly GpuProgram _listProgram, _halfProgram;

    /// <summary>
    /// The pipelines of <see cref="_listProgram"/> and <see cref="_halfProgram"/>.
    /// </summary>
    readonly GpuComputePipeline _listPipeline, _halfPipeline;

    /// <summary>
    /// Nearest, clamped: the inputs are read texel by texel.
    /// </summary>
    readonly GpuSampler _sampler;

    /// <summary>
    /// What binding 6 holds when the pass works on an area, not a list.
    /// </summary>
    readonly DeviceBuffer _noLists;

    /// <summary>
    /// The kernel's constants, as <see cref="Source"/>'s <c>Params</c> reads them.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Params
    {
        /// <summary>
        /// NDC, jittered as the frame was, to the position relative to the eye.
        /// </summary>
        public Matrix4x4 Inverse;

        /// <summary>
        /// Width, height, a pixel's width per unit of depth, the pass (0 along rows, then 1 along columns).
        /// </summary>
        public Vector4 Size;

        /// <summary>
        /// x: the blue channel names what the channels hold, z: the pixels are a list's cells, w: the list's slot.
        /// </summary>
        public Vector4 Options;

        /// <summary>
        /// The pixels filtered: first x, y, one past the last x, y (a list's pass: x the lists' base).
        /// </summary>
        public Vector4 Area;

        /// <summary>
        /// The cells worked on, a bit each.
        /// </summary>
        public fixed uint Cells[64];
    }

    /// <summary>
    /// The kernel and the empty list buffer.
    /// </summary>
    public PenumbraFilter(GpuContext ctx)
    {
        this._ctx = ctx;
        _program = ctx.Shaders.Compute(Source, "penumbra filter",
        [
            (0, DescriptorType.CombinedImageSampler),
            (1, DescriptorType.CombinedImageSampler),
            (2, DescriptorType.CombinedImageSampler),
            (3, DescriptorType.CombinedImageSampler),
            (4, DescriptorType.StorageImage),
            (5, DescriptorType.UniformBuffer),
            (6, DescriptorType.StorageBuffer),
        ]);
        _listProgram = ctx.Shaders.Compute(ListSource, "penumbra filter list",
        [
            (0, DescriptorType.CombinedImageSampler),
            (1, DescriptorType.CombinedImageSampler),
            (2, DescriptorType.CombinedImageSampler),
            (3, DescriptorType.CombinedImageSampler),
            (4, DescriptorType.StorageImage),
            (5, DescriptorType.UniformBuffer),
            (6, DescriptorType.StorageBuffer),
        ]);
        _listPipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_listProgram, "penumbra filter list"));
        _halfProgram = ctx.Shaders.Compute(HalfSource, "penumbra filter half",
        [
            (0, DescriptorType.CombinedImageSampler),
            (1, DescriptorType.CombinedImageSampler),
            (2, DescriptorType.CombinedImageSampler),
            (3, DescriptorType.CombinedImageSampler),
            (4, DescriptorType.StorageImage),
            (5, DescriptorType.UniformBuffer),
            (6, DescriptorType.StorageBuffer),
        ]);
        _halfPipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_halfProgram, "penumbra filter half"));
        _planesProgram = ctx.Shaders.Compute(PlanesSource, "penumbra planes",
        [
            (0, DescriptorType.CombinedImageSampler),
            (4, DescriptorType.StorageImage),
            (5, DescriptorType.UniformBuffer),
        ]);
        _planesPipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_planesProgram, "penumbra planes"));
        _noLists = DeviceBuffer.Create(ctx, 16, BufferUse.Storage, "penumbra filter no lists");
        _pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_program, "penumbra filter"));
        _sampler = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
    }

    /// <summary>
    /// Both passes into <paramref name="cmd"/>, after the rays (a full barrier before each pass and after the last). <paramref name="inverse"/>: NDC,
    /// jittered as the frame was, to the position relative to the eye; <paramref name="pixelSlope"/>: a pixel's width per unit of depth.
    /// </summary>
    /// <param name="named">The blue channel names what the others hold (the lamps' pair): only taps with the pixel's name are read.</param>
    public void Run(CommandList cmd, in PenumbraTargets t, in Matrix4x4 inverse, float pixelSlope, bool named = false)
    {
        var all = new Vector4(0, 0, t.Width, t.Height);
        EnsurePlanes(cmd, t, inverse, pixelSlope);
        Pass(cmd, t, t.Raw, t.Scratch, inverse, pixelSlope, 0, named, all);
        cmd.Barrier(BarrierBatch.Full);
        Pass(cmd, t, t.Scratch, t.Filtered, inverse, pixelSlope, 1, named, all);
        cmd.Barrier(BarrierBatch.Full);
    }

    /// <summary>
    /// Both passes over a list of <see cref="LampShadowPass"/> (<paramref name="lists"/>, the list starting at <paramref name="listBase"/>, the
    /// indirect dispatch's arguments at <paramref name="argsOffset"/>), each entry's cell in its slice: the raw result must hold those cells; the
    /// output holds them, right where the cells round a pixel are listed too. The raw result, the scratch and the output are arrays of slices.
    /// </summary>
    /// <param name="half">The slices are half the size of <paramref name="t"/>'s width and height (the depth's), a pixel standing for the pixel
    /// <paramref name="sub"/> of its 2 x 2; <paramref name="argsOffset"/> is then the half-size dispatch's.</param>
    public void RunList(CommandList cmd, in PenumbraTargets t, in Matrix4x4 inverse, float pixelSlope, GpuBuffer lists, ulong listsSize,
        ulong argsOffset, int listBase, bool half = false, Vector2 sub = default)
    {
        EnsurePlanes(cmd, t, inverse, pixelSlope);
        Pass(cmd, t, t.Raw, t.Scratch, inverse, pixelSlope, 0, false, default, lists, listsSize, argsOffset, true, listBase, half, sub);
        cmd.Barrier(BarrierBatch.Full);
        Pass(cmd, t, t.Scratch, t.Filtered, inverse, pixelSlope, 1, false, default, lists, listsSize, argsOffset, true, listBase, half, sub);
        cmd.Barrier(BarrierBatch.Full);
    }

    /// <summary>
    /// The kernel and the empty list buffer go.
    /// </summary>
    public void Dispose()
    {
        _ctx.Shaders.Dispose(_program);
        _ctx.Shaders.Dispose(_listProgram);
        _ctx.Shaders.Dispose(_halfProgram);
        _ctx.Shaders.Dispose(_planesProgram);
        _planes?.Dispose();
        _noLists.Dispose();
    }

    /// <summary>
    /// The planes of this frame's depth (<see cref="PlanesSource"/>), made on the frame's first pass over <paramref name="t"/>'s depth.
    /// </summary>
    void EnsurePlanes(CommandList cmd, in PenumbraTargets t, in Matrix4x4 inverse, float pixelSlope)
    {
        if (_planes is null || _planes.Desc.Width != t.Width || _planes.Desc.Height != t.Height)
        {
            _planes?.Dispose();   // released after the frames in flight
            _planes = _ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16F, t.Width, t.Height, Use: TextureUse.Storage | TextureUse.Sampled,
                Name: "penumbra planes"));
            _planesFrame = -1;
        }
        if (_planesFrame == _ctx.Frame.Number && _planesDepth == t.Depth) return;

        (_planesFrame, _planesDepth) = (_ctx.Frame.Number, t.Depth);
        var parameters = new Params { Inverse = inverse, Size = new Vector4(t.Width, t.Height, pixelSlope, 0) };
        var constants = _ctx.Frame.Constants.Write<Params>(new ReadOnlySpan<Params>(&parameters, 1), 256);
        var images = stackalloc DescriptorImageInfo[2];
        images[0] = Sampled(t.Depth);
        images[1] = new DescriptorImageInfo(default, _ctx.Device.ViewAt(_planes.View().Index), ImageLayout.General);
        var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(Params));
        var writes = stackalloc WriteDescriptorSet[3];
        writes[0] = Image(0, DescriptorType.CombinedImageSampler, &images[0]);
        writes[1] = Image(4, DescriptorType.StorageImage, &images[1]);
        writes[2] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstBinding = 5, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBuffer,
            PBufferInfo = &uniform,
        };
        cmd.BindPipeline(_planesPipeline);
        cmd.PushDescriptors(_planesProgram, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 3), GpuBindPoint.Compute);
        cmd.Dispatch((uint)((t.Width + 7) / 8), (uint)((t.Height + 7) / 8));
        cmd.Barrier(BarrierBatch.Full);
    }

    /// <summary>
    /// One pass from <paramref name="input"/> into <paramref name="output"/>: over <paramref name="area"/>, or over a list's cells when
    /// <paramref name="listed"/>.
    /// </summary>
    void Pass(CommandList cmd, in PenumbraTargets t, GpuTexture input, GpuTexture output, in Matrix4x4 inverse, float pixelSlope, int pass, bool named,
        Vector4 area, GpuBuffer lists = default, ulong listsSize = 0, ulong argsOffset = 0, bool listed = false, int listBase = 0, bool half = false,
        Vector2 sub = default)
    {
        var parameters = new Params
        {
            Inverse = inverse,
            Size = new Vector4(t.Width, t.Height, half ? pixelSlope * 2 : pixelSlope, pass),
            Options = new Vector4(named ? 1 : 0, 0, listed ? 1 : 0, 0),
            Area = listed ? new Vector4(listBase, 0, sub.X, sub.Y) : area,
        };
        var constants = _ctx.Frame.Constants.Write<Params>(new ReadOnlySpan<Params>(&parameters, 1), 256);

        BindPass(cmd, t, input, output, constants, listed, lists, listsSize, half);

        if (listed) cmd.DispatchIndirect(lists, argsOffset);
        else cmd.Dispatch((uint)(((int)(area.Z - area.X) + 7) / 8), (uint)(((int)(area.W - area.Y) + 7) / 8));
    }

    /// <summary>
    /// The pipeline bound and a pass's descriptors pushed: the depth, the surface, the input, the output, the constants and the cell lists.
    /// </summary>
    void BindPass(CommandList cmd, in PenumbraTargets t, GpuTexture input, GpuTexture output, Transient constants, bool listed, GpuBuffer lists,
        ulong listsSize, bool half = false)
    {
        var images = stackalloc DescriptorImageInfo[5];
        images[0] = Sampled(t.Depth);
        images[1] = Sampled(t.Surface);
        images[2] = Sampled(input);
        images[3] = new DescriptorImageInfo(default, _ctx.Device.ViewAt(output.View().Index), ImageLayout.General);
        images[4] = Sampled(_planes!);
        var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(Params));
        var cells = listed
            ? new DescriptorBufferInfo(_ctx.Device.BufferAt(lists.Index), 0, listsSize)
            : new DescriptorBufferInfo(_ctx.Device.BufferAt(_noLists.Handle.Index), 0, _noLists.Size);

        var writes = stackalloc WriteDescriptorSet[7];
        for (int i = 0; i < 3; i++) writes[i] = Image((uint)i, DescriptorType.CombinedImageSampler, &images[i]);
        writes[6] = Image(3, DescriptorType.CombinedImageSampler, &images[4]);
        writes[3] = Image(4, DescriptorType.StorageImage, &images[3]);
        writes[4] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstBinding = 5, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBuffer,
            PBufferInfo = &uniform,
        };
        writes[5] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstBinding = 6, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &cells,
        };

        cmd.BindPipeline(half ? _halfPipeline : listed ? _listPipeline : _pipeline);
        var program = half ? _halfProgram : listed ? _listProgram : _program;
        cmd.PushDescriptors(program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 7), GpuBindPoint.Compute);
    }

    /// <summary>
    /// A texture read through the nearest sampler.
    /// </summary>
    DescriptorImageInfo Sampled(GpuTexture texture) =>
        new(_ctx.Device.SamplerAt(_sampler.Index), _ctx.Device.ViewAt(texture.View().Index), ImageLayout.General);

    /// <summary>
    /// An image's descriptor write at <paramref name="binding"/>.
    /// </summary>
    static WriteDescriptorSet Image(uint binding, DescriptorType type, DescriptorImageInfo* info) => new()
    {
        SType = StructureType.WriteDescriptorSet,
        DstBinding = binding,
        DescriptorCount = 1,
        DescriptorType = type,
        PImageInfo = info,
    };

    /// <summary>
    /// A float as a GLSL literal: invariant culture, at least one decimal, at most nine.
    /// </summary>
    static string F(float v) => v.ToString("0.0########", CultureInfo.InvariantCulture);
}
