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
    static readonly string Source = $$"""
        #version 460
        layout(local_size_x = 8, local_size_y = 8) in;
        layout(set = 0, binding = 0) uniform sampler2D uDepth;
        layout(set = 0, binding = 1) uniform usampler2D uSurface;
        layout(set = 0, binding = 2) uniform sampler2D uIn;
        layout(set = 0, binding = 4, rgba16f) uniform writeonly image2D uOut;
        layout(set = 0, binding = 6, std430) readonly buffer CellLists { uint lists[]; };   // LampShadowPass's lists of cells (options.z)
        layout(set = 0, binding = 5, std140) uniform Params
        {
            mat4 inverse;      // NDC, jittered as the frame was -> position relative to the eye
            vec4 size;         // width, height, a pixel's width per unit of depth, the pass (0 along rows, then 1 along columns)
            vec4 options;      // x: the blue channel names what the channels hold; only taps with the pixel's own name are read; z: the pixels are a list's cells (w: its slot, area.x: the lists' base)
            vec4 area;         // the pixels filtered: first x, y, one past the last x, y
            uvec4 cells[16];   // with options.y: the cells worked on, a bit each
        } p;
        #define CELLS p.cells
        #define CELL_WIDTH ((int(p.size.x) + 63) / 64)

        // The cells of 64 pixels this lamp's pass works on (LampShadowPass.CellSize): the mover-free rest of its area costs a test.
        bool cellOn(ivec2 q)
        {
            ivec2 c = q / 64;
            int i = c.y * CELL_WIDTH + c.x;
            return ((CELLS[i / 128][(i / 32) % 4] >> uint(i % 32)) & 1u) != 0u;
        }

        vec3 pointAt(ivec2 px)
        {
            vec4 h = p.inverse * vec4((vec2(px) + 0.5) / p.size.xy * 2.0 - 1.0, texelFetch(uDepth, px, 0).r, 1.0);
            return h.xyz / h.w;
        }

        bool inside(ivec2 q) { return all(greaterThanEqual(q, ivec2(0))) && all(lessThan(q, ivec2(p.size.xy))); }

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
            if (!inside(q) || texelFetch(uIn, q, 0).a < 0.0) return false;
            if (p.options.x > 0.5 && abs(texelFetch(uIn, q, 0).b - ownName) > 0.5) return false;
            return abs(dot(pointAt(q) - at, n)) < tolerance;
        }

        // Whether a neighbour lies on a surface turned away from this one.
        bool onCrease(ivec2 px, vec3 n)
        {
            for (int k = 0; k < 4; k++)
            {
                ivec2 q = px + ivec2(k < 2 ? k * 2 - 1 : 0, k < 2 ? 0 : k * 2 - 5);
                if (inside(q) && dot(plane(q, pointAt(q)), n) < 0.6) return true;
            }
            return false;
        }

        // The penumbra's radius in pixels from the blockers round the pixel (0: none within the search).
        float searchRadius(ivec2 px, vec3 at, vec3 n, float footprint, float own)
        {
            float sum = own, count = own > 0.0 ? 1.0 : 0.0;
            float tolerance = max(0.25, footprint * {{F(Search)}} * 0.5);
            for (int k = 0; k < {{SearchTaps}}; k++)
            {
                float r = sqrt((float(k) + 0.5) / {{SearchTaps}}.0), a = float(k) * 2.39996323;
                ivec2 q = px + ivec2(round(vec2(cos(a), sin(a)) * r * {{F(Search)}}));
                if (!onSurface(q, at, n, tolerance)) continue;
                float spread = texelFetch(uIn, q, 0).a;
                if (spread > 0.0) { sum += spread; count += 1.0; }
            }
            return count == 0.0 ? 0.0 : clamp(sum / count / footprint, {{F(MinRadius)}}, {{F(MaxRadius)}});
        }

        void main()
        {
            ivec2 px;
            if (p.options.z > 0.5)
            {
                // A workgroup per 8 x 8 pixels of a listed cell: y the cell's place in the list, x the block in the cell.
                uint cell = lists[uint(p.area.x) + uint(p.options.w) * 2048u + gl_WorkGroupID.y];
                px = ivec2(cell & 0xFFFFu, cell >> 16) * 64 + ivec2(int(gl_WorkGroupID.x) & 7, int(gl_WorkGroupID.x) >> 3) * 8 + ivec2(gl_LocalInvocationID.xy);
                if (!inside(px)) return;
            }
            else
            {
                px = ivec2(gl_GlobalInvocationID.xy) + ivec2(p.area.xy);
                if (!inside(px) || px.x >= int(p.area.z) || px.y >= int(p.area.w)) return;
                if (p.options.y > 0.5 && !cellOn(px)) return;
            }
            vec4 c = texelFetch(uIn, px, 0);
            ownName = c.b;
            bool rows = p.size.w < 0.5;
            if (c.a < 0.0) { imageStore(uOut, px, c); return; }   // no receiver: kept, and left out of its neighbours
            if (!rows && c.a <= 0.0) { imageStore(uOut, px, vec4(c.rgb, 0.0)); return; }
            if (rows && c.a <= 0.0)
            {
                bool blocker = false;
                for (int k = 0; k < {{SearchTaps}} && !blocker; k++)
                {
                    float r = sqrt((float(k) + 0.5) / {{SearchTaps}}.0), a = float(k) * 2.39996323;
                    ivec2 q = px + ivec2(round(vec2(cos(a), sin(a)) * r * {{F(Search)}}));
                    blocker = inside(q) && texelFetch(uIn, q, 0).a > 0.0;
                }
                if (!blocker) { imageStore(uOut, px, vec4(c.rgb, 0.0)); return; }
            }
            vec3 at = pointAt(px);
            float footprint = length(at) * p.size.z;
            vec3 n = plane(px, at);
            float radius = rows ? searchRadius(px, at, n, footprint, c.a) : c.a;
            if (radius <= 0.0) { imageStore(uOut, px, vec4(c.rgb, 0.0)); return; }
            ivec2 axis = rows ? ivec2(1, 0) : ivec2(0, 1);
            int reach = int(ceil(radius));
            vec3 sum = c.rgb;
            float weight = 1.0;
            for (int k = 1; k <= {{(int)MaxRadius}}; k++)
            {
                if (k > reach) break;
                float w = clamp(radius + 0.5 - float(k), 0.0, 1.0);
                float tolerance = max(0.25, footprint * (float(k) * 0.5 + 1.0));
                for (int side = -1; side <= 1; side += 2)
                {
                    ivec2 q = px + axis * (k * side);
                    if (!onSurface(q, at, n, tolerance)) continue;
                    // A tap beyond a crease (a wall's foot: its pixels pass the plane's tolerance there) is another surface, with other lamps' light.
                    if (dot(plane(q, pointAt(q)), n) < 0.6) continue;
                    sum += texelFetch(uIn, q, 0).rgb * w;
                    weight += w;
                }
            }
            vec3 result = sum / weight;
            // On a crease (a wall's foot) the pixel's own ray stands: what lies round it is another surface, and even a sliver of it left a lit line along the foot.
            if (any(greaterThan(abs(result - c.rgb), vec3(0.02))) && onCrease(px, n)) result = c.rgb;
            imageStore(uOut, px, vec4(result, radius));
        }
        """;

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
            (4, DescriptorType.StorageImage),
            (5, DescriptorType.UniformBuffer),
            (6, DescriptorType.StorageBuffer),
        ]);
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
        Pass(cmd, t, t.Raw, t.Scratch, inverse, pixelSlope, 0, named, all);
        cmd.Barrier(BarrierBatch.Full);
        Pass(cmd, t, t.Scratch, t.Filtered, inverse, pixelSlope, 1, named, all);
        cmd.Barrier(BarrierBatch.Full);
    }

    /// <summary>
    /// Both passes over the cells of a list (<see cref="LampShadowPass"/>: <paramref name="lists"/>, the list's <paramref name="slot"/>, the lists'
    /// <paramref name="listBase"/>, the indirect dispatch's arguments at <paramref name="argsOffset"/>): the raw result must hold those cells; the
    /// output holds them, right where the cells round a pixel are listed too.
    /// </summary>
    public void RunList(CommandList cmd, in PenumbraTargets t, in Matrix4x4 inverse, float pixelSlope, GpuBuffer lists, ulong listsSize,
        ulong argsOffset, int slot, int listBase)
    {
        Pass(cmd, t, t.Raw, t.Scratch, inverse, pixelSlope, 0, false, default, lists, listsSize, argsOffset, slot, listBase);
        cmd.Barrier(BarrierBatch.Full);
        Pass(cmd, t, t.Scratch, t.Filtered, inverse, pixelSlope, 1, false, default, lists, listsSize, argsOffset, slot, listBase);
        cmd.Barrier(BarrierBatch.Full);
    }

    /// <summary>
    /// The kernel and the empty list buffer go.
    /// </summary>
    public void Dispose()
    {
        _ctx.Shaders.Dispose(_program);
        _noLists.Dispose();
    }

    /// <summary>
    /// One pass from <paramref name="input"/> into <paramref name="output"/>: over <paramref name="area"/>, or over a list's cells when
    /// <paramref name="slot"/> is set.
    /// </summary>
    void Pass(CommandList cmd, in PenumbraTargets t, GpuTexture input, GpuTexture output, in Matrix4x4 inverse, float pixelSlope, int pass, bool named,
        Vector4 area, GpuBuffer lists = default, ulong listsSize = 0, ulong argsOffset = 0, int slot = -1, int listBase = 0)
    {
        bool listed = slot >= 0;
        var parameters = new Params
        {
            Inverse = inverse,
            Size = new Vector4(t.Width, t.Height, pixelSlope, pass),
            Options = new Vector4(named ? 1 : 0, 0, listed ? 1 : 0, Math.Max(slot, 0)),
            Area = listed ? new Vector4(listBase, 0, 0, 0) : area,
        };
        var constants = _ctx.Frame.Constants.Write<Params>(new ReadOnlySpan<Params>(&parameters, 1), 256);

        BindPass(cmd, t, input, output, constants, listed, lists, listsSize);

        if (listed) cmd.DispatchIndirect(lists, argsOffset);
        else cmd.Dispatch((uint)(((int)(area.Z - area.X) + 7) / 8), (uint)(((int)(area.W - area.Y) + 7) / 8));
    }

    /// <summary>
    /// The pipeline bound and a pass's descriptors pushed: the depth, the surface, the input, the output, the constants and the cell lists.
    /// </summary>
    void BindPass(CommandList cmd, in PenumbraTargets t, GpuTexture input, GpuTexture output, Transient constants, bool listed, GpuBuffer lists,
        ulong listsSize)
    {
        var images = stackalloc DescriptorImageInfo[4];
        images[0] = Sampled(t.Depth);
        images[1] = Sampled(t.Surface);
        images[2] = Sampled(input);
        images[3] = new DescriptorImageInfo(default, _ctx.Device.ViewAt(output.View().Index), ImageLayout.General);
        var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(Params));
        var cells = listed
            ? new DescriptorBufferInfo(_ctx.Device.BufferAt(lists.Index), 0, listsSize)
            : new DescriptorBufferInfo(_ctx.Device.BufferAt(_noLists.Handle.Index), 0, _noLists.Size);

        var writes = stackalloc WriteDescriptorSet[6];
        for (int i = 0; i < 3; i++) writes[i] = Image((uint)i, DescriptorType.CombinedImageSampler, &images[i]);
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

        cmd.BindPipeline(_pipeline);
        cmd.PushDescriptors(_program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 6), GpuBindPoint.Compute);
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
