using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gi;

/// <summary>The penumbra filter's inputs, all at the render size: the scene depth and surface target, the rays' result, a scratch target and the output (RGBA16F).</summary>
readonly record struct PenumbraTargets(GpuTexture Depth, GpuTexture Surface, GpuTexture Raw, GpuTexture Scratch, GpuTexture Filtered, int Width, int Height);

/// <summary>
/// The penumbra filter of the traced shadows (docs/render-shadows.md "Traced sun", docs/render-lights.md "Traced lamp shadows"), the same every frame (no history): the rays' hard
/// result (rgb) softened by its light's size. The rays write, where they are blocked, the penumbra's radius at the receiver in world units (alpha: the light's angular radius times
/// the blocker's distance; -1 where the pixel is no receiver: it is kept and no neighbour reads it). A horizontal pass finds the blockers' mean radius among taps on the pixel's own surface (its plane from the depth), takes it to pixels (at least <see cref="MinRadius"/>, which
/// anti-aliases a contact edge), and averages along the row with a box whose ends are fractional; a vertical pass does the same along the column at that radius. The weights are
/// continuous, so a wide penumbra is a smooth ramp, not a staircase of tap counts. Render thread only.
/// </summary>
sealed unsafe class PenumbraFilter : IDisposable
{
    /// <summary>Taps of the blocker search (a sunflower within <see cref="Search"/> pixels); pixels: the search's reach and the narrowest and widest radius.</summary>
    const int SearchTaps = 16;
    const float Search = 12, MinRadius = 1, MaxRadius = 16;

    static string F(float v) => v.ToString("0.0########", CultureInfo.InvariantCulture);

    static readonly string Source = $$"""
        #version 460
        layout(local_size_x = 8, local_size_y = 8) in;
        layout(set = 0, binding = 0) uniform sampler2D uDepth;
        layout(set = 0, binding = 1) uniform usampler2D uSurface;
        layout(set = 0, binding = 2) uniform sampler2D uIn;
        layout(set = 0, binding = 4, rgba16f) uniform writeonly image2D uOut;
        layout(set = 0, binding = 5, std140) uniform Params
        {
            mat4 inverse;      // NDC, jittered as the frame was -> position relative to the eye
            vec4 size;         // width, height, a pixel's width per unit of depth, the pass (0 along rows, then 1 along columns)
            vec4 options;      // x: the blue channel names what the channels hold; only taps with the pixel's own name are read
            vec4 area;         // the pixels filtered: first x, y, one past the last x, y
        } p;

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
            ivec2 px = ivec2(gl_GlobalInvocationID.xy) + ivec2(p.area.xy);
            if (!inside(px) || px.x >= int(p.area.z) || px.y >= int(p.area.w)) return;
            vec4 c = texelFetch(uIn, px, 0);
            ownName = c.b;
            bool rows = p.size.w < 0.5;
            if (c.a < 0.0) { imageStore(uOut, px, c); return; }   // no receiver: kept, and left out of its neighbours
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

    [StructLayout(LayoutKind.Sequential)]
    struct Params
    {
        public Matrix4x4 Inverse;
        public Vector4 Size, Options, Area;
    }

    readonly GpuContext ctx;
    readonly GpuProgram program;
    readonly GpuComputePipeline pipeline;
    readonly GpuSampler sampler;

    public PenumbraFilter(GpuContext ctx)
    {
        this.ctx = ctx;
        program = ctx.Shaders.Compute(Source, "penumbra filter",
        [
            (0, DescriptorType.CombinedImageSampler), (1, DescriptorType.CombinedImageSampler), (2, DescriptorType.CombinedImageSampler),
            (4, DescriptorType.StorageImage), (5, DescriptorType.UniformBuffer),
        ]);
        pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(program, "penumbra filter"));
        sampler = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
    }

    /// <summary>
    /// Both passes into <paramref name="cmd"/>, after the rays (a full barrier before each pass and after the last). <paramref name="inverse"/>: NDC, jittered as the frame was, to the
    /// position relative to the eye; <paramref name="pixelSlope"/>: a pixel's width per unit of depth.
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

    /// <summary>How far past a filtered area the rows' pass reads (the search and the widest radius), and the columns' pass.</summary>
    public const int RowMargin = (int)(Search + MaxRadius) + 2, ColumnMargin = (int)MaxRadius;

    /// <summary>
    /// Both passes over <paramref name="area"/> alone (first x, y, one past the last x, y): the raw result must hold <see cref="RowMargin"/> + <see cref="ColumnMargin"/> pixels round it; the output holds only the area.
    /// </summary>
    public void RunArea(CommandList cmd, in PenumbraTargets t, in Matrix4x4 inverse, float pixelSlope, Vector4 area)
    {
        var rows = new Vector4(MathF.Max(area.X - ColumnMargin, 0), MathF.Max(area.Y - ColumnMargin, 0), MathF.Min(area.Z + ColumnMargin, t.Width), MathF.Min(area.W + ColumnMargin, t.Height));
        Pass(cmd, t, t.Raw, t.Scratch, inverse, pixelSlope, 0, false, rows);
        cmd.Barrier(BarrierBatch.Full);
        Pass(cmd, t, t.Scratch, t.Filtered, inverse, pixelSlope, 1, false, area);
        cmd.Barrier(BarrierBatch.Full);
    }

    void Pass(CommandList cmd, in PenumbraTargets t, GpuTexture input, GpuTexture output, in Matrix4x4 inverse, float pixelSlope, int pass, bool named, Vector4 area)
    {
        var parameters = new Params { Inverse = inverse, Size = new Vector4(t.Width, t.Height, pixelSlope, pass), Options = new Vector4(named ? 1 : 0, 0, 0, 0), Area = area };
        var constants = ctx.Frame.Constants.Write<Params>(new ReadOnlySpan<Params>(&parameters, 1), 256);
        DescriptorImageInfo Sampled(GpuTexture texture) => new(ctx.Device.SamplerAt(sampler.Index), ctx.Device.ViewAt(texture.View().Index), ImageLayout.General);
        var images = stackalloc DescriptorImageInfo[4];
        images[0] = Sampled(t.Depth);
        images[1] = Sampled(t.Surface);
        images[2] = Sampled(input);
        images[3] = new DescriptorImageInfo(default, ctx.Device.ViewAt(output.View().Index), ImageLayout.General);
        var uniform = new DescriptorBufferInfo(ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(Params));
        var writes = stackalloc WriteDescriptorSet[5];
        for (int i = 0; i < 3; i++) writes[i] = Image((uint)i, DescriptorType.CombinedImageSampler, &images[i]);
        writes[3] = Image(4, DescriptorType.StorageImage, &images[3]);
        writes[4] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 5, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = &uniform };
        cmd.BindPipeline(pipeline);
        cmd.PushDescriptors(program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 5), GpuBindPoint.Compute);
        cmd.Dispatch((uint)(((int)(area.Z - area.X) + 7) / 8), (uint)(((int)(area.W - area.Y) + 7) / 8));
    }

    static WriteDescriptorSet Image(uint binding, DescriptorType type, DescriptorImageInfo* info) =>
        new() { SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1, DescriptorType = type, PImageInfo = info };

    public void Dispose() => ctx.Shaders.Dispose(program);
}
