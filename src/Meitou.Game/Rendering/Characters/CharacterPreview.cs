using System.Numerics;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering.Characters;

/// <summary>
/// How the editor's stage and the squad portraits are finished (docs/character-renderer.md "Meitou stage and portraits"). Faithful (<see cref="Enhanced"/> false): drawn
/// straight into their 8-bit pictures as the original's. Enhanced: drawn unclamped into a half-float picture <see cref="Supersample"/> times larger each way, then
/// tone mapped, graded, averaged down and dithered as the world's composite (<see cref="PostProcessShaders.Composite"/>) finishes the world.
/// </summary>
public readonly record struct PreviewLook(bool Enhanced, int Supersample, ToneMapOperator Tone, float ToneMix, bool Grade, bool Dither)
{
    /// <summary>The look of the world's options: enhanced when its tone map, grade or anti-aliasing is Meitou's, supersampled twice with Meitou's anti-aliasing.</summary>
    public static PreviewLook Of(PostOptions o)
    {
        bool aa = o.Upscale.Kind != UpscalerKind.Off;
        bool enhanced = aa || o.GradeUsed || o.ToneMapUsed != ToneMapOperator.Clamp;
        return enhanced ? new PreviewLook(true, aa ? 2 : 1, o.ToneMapUsed, Math.Clamp(o.ToneMix, 0, 1), o.GradeUsed, o.Dither) : default;
    }
}

/// <summary>The enhanced preview's resolve: a supersampled half-float picture into a region of an 8-bit one, per <see cref="PreviewLook"/>.</summary>
internal sealed class PreviewResolve : IDisposable
{
    const string Fragment = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uSource;
        uniform vec4 uRect;       // the destination region: x, y, width, height (pixels); the source holds it uScale times larger from its corner (0, 0)
        uniform int uScale, uTone, uGrade, uDither;
        uniform float uToneMix, uSaturation, uContrast;

        float ign(vec2 p) { return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715)))); }
        vec3 shoulder(vec3 c)
        {
            const float K = 0.8;
            float m = max(c.r, max(c.g, c.b));
            if (m <= K) return c;
            return c * ((K + (1.0 - K) * (1.0 - exp(-(m - K) / (1.0 - K)))) / m);
        }
        vec3 aces(vec3 x) { return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0); }
        // The composite's tone curves, on the stage light's values (its exposure is in them).
        vec3 tone(vec3 c)
        {
            if (uTone == 1) c = shoulder(c);
            else if (uTone == 2) c = aces(c);
            else if (uTone == 3) c = mix(clamp(c, 0.0, 1.0), aces(c * 0.7), uToneMix);
            return clamp(c, 0.0, 1.0);
        }

        void main()
        {
            ivec2 local = ivec2(floor(vUv * uRect.zw)), p = ivec2(uRect.xy) + local;
            // Each sample tone mapped before the average (a bright highlight does not swamp its pixel), weighted by its coverage: the edges come out anti-aliased
            // against the transparent background the interface draws over.
            vec3 sum = vec3(0.0);
            float covered = 0.0;
            for (int y = 0; y < uScale; y++)
                for (int x = 0; x < uScale; x++)
                {
                    vec4 s = texelFetch(uSource, local * uScale + ivec2(x, y), 0);
                    sum += tone(max(s.rgb, 0.0)) * s.a;
                    covered += s.a;
                }
            float n = float(uScale * uScale);
            if (covered <= 0.0) { fragColour = vec4(0.0); return; }
            vec3 c = sum / covered;
            if (uGrade != 0)
            {
                c = mix(vec3(dot(c, vec3(0.299, 0.587, 0.114))), c, uSaturation);
                c = clamp((c - 0.5) * uContrast + 0.5, 0.0, 1.0);
            }
            if (uDither != 0) c += (ign(vec2(p)) + ign(vec2(p) + 17.0) - 1.0) / 255.0;
            fragColour = vec4(c, covered / n);
        }
        """;

    readonly IGpuContext gpu;
    readonly LegacyProgram program;
    readonly SamplerSlot source;
    readonly UniformHandle rect, scale, tone, toneMix, grade, saturation, contrast, dither;
    readonly Dictionary<InternalFormat, GpuPipeline> pipelines = [];

    public PreviewResolve(IGpuContext gpu)
    {
        this.gpu = gpu;
        program = LegacyProgram.Create(gpu, PostProcessShaders.Vertex, Fragment, "character preview resolve");
        source = program.Sampler("uSource");
        (rect, scale, tone, toneMix) = (program.Uniform("uRect"), program.Uniform("uScale"), program.Uniform("uTone"), program.Uniform("uToneMix"));
        (grade, saturation, contrast, dither) = (program.Uniform("uGrade"), program.Uniform("uSaturation"), program.Uniform("uContrast"), program.Uniform("uDither"));
    }

    /// <summary>A half-float colour target and its depth for <paramref name="width"/> × <paramref name="height"/> destination pixels at <paramref name="look"/>'s supersampling.</summary>
    public static (GpuTexture Colour, GpuTexture Depth, SampledTexture Sampled) Targets(IGpuContext gpu, int width, int height, PreviewLook look, string name)
    {
        int s = Math.Max(look.Supersample, 1);
        var colour = gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba16f, width * s, height * s, Use: TextureUse.Sampled | TextureUse.ColourTarget, Name: name));
        var depth = gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, width * s, height * s, Use: TextureUse.DepthTarget, Name: name + " depth"));
        var sampled = new SampledTexture(gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest,
            TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0)), colour.View());
        return (colour, depth, sampled);
    }

    /// <summary>
    /// Resolves <paramref name="from"/> into the <paramref name="regions"/> of <paramref name="into"/> (each its destination pixels; the source holds each from its corner,
    /// <see cref="PreviewLook.Supersample"/> times larger), outside any pass; the rest of <paramref name="into"/> keeps what it holds.
    /// </summary>
    public void Run(SampledTexture from, GpuTexture into, PreviewLook look, ReadOnlySpan<PixelRect> regions)
    {
        if (regions.Length == 0) return;
        var targets = PassTargets.Of(into, null);
        var state = DrawState.For(targets.Formats, false);
        if (!pipelines.TryGetValue(into.Desc.Format, out var pipeline))
            pipelines[into.Desc.Format] = pipeline = gpu.Pipelines.Get(state.Pipeline(program.Program, program.VertexLayout([]), PrimitiveType.Triangles, targets.Formats, program.Name));
        var cmd = gpu.BeginNative("character preview resolve");
        cmd.Order();   // the source was just drawn
        cmd.BeginRendering(new RenderingDesc(targets.Colour, default, targets.Width, targets.Height));
        cmd.SetRaster(state.Cull, state.Front);
        cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
        cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
        cmd.BindPipeline(pipeline);
        program.Bind(source, from);
        program.Set(scale, Math.Max(look.Supersample, 1));
        program.Set(tone, (int)look.Tone);
        program.Set(toneMix, look.ToneMix);
        program.Set(grade, look.Grade ? 1 : 0);
        program.Set(saturation, PostOptions.Saturation);
        program.Set(contrast, PostOptions.Contrast);
        program.Set(dither, look.Dither ? 1 : 0);
        foreach (var r in regions)
        {
            cmd.SetViewport(new PixelViewport(r.X, r.Y, r.Width, r.Height, 0, 1));
            cmd.SetScissor(r);
            program.Set(rect, new Vector4(r.X, r.Y, r.Width, r.Height));
            program.Flush(cmd);
            cmd.Draw(3);
        }
        cmd.EndRendering();
        cmd.Order();
        gpu.EndNative(cmd);
    }

    public void Dispose() => program.Dispose();
}
