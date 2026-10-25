using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// Meitou bloom (the <c>bloom</c> switch; docs/render-post.md "Bloom"): the scene convolved with a point spread function and mixed into it by the
/// composite, <c>lerp(scene, psf ⊗ scene, strength)</c>, so the frame keeps its energy. No threshold and no history. The kernel is a pyramid:
/// <see cref="PostProcessShaders.BloomPrefilter"/> to half size, <see cref="PostProcessShaders.BloomDown"/> to 1/128, then
/// <see cref="PostProcessShaders.BloomUp"/> sums the levels back from the coarsest, <c>U_i = w_i L_i + tent(U_i+1)</c>, into the quarter-size level the
/// composite reads. After the upscaler, at the size the composite reads the scene at.
/// </summary>
public sealed unsafe partial class PostProcess
{
    /// <summary>The pyramid's shape and the levels' weights.</summary>
    internal static class BloomPlan
    {
        /// <summary>Levels below the scene: 1/2 down to 1/128 of its size.</summary>
        public const int MaxLevels = 7;
        /// <summary>The coarsest level stops once a side would drop below this: a 13-tap step over fewer texels is mostly clamped edge.</summary>
        public const int MinLevelSide = 4;

        /// <summary>
        /// The weights of levels 2 (quarter size) to 7, summing to one: fitted to the scattered part of the eye's photopic PSF (Spencer et al. 1995) at a 45°
        /// vertical field of view, as the composite reads level 2 (through the B-spline). The fit is KenshiNative's (<c>tools/checks/bloompsf.py --fit</c>).
        /// </summary>
        public static readonly float[] Weights = [0.41405f, 0.21190f, 0.10986f, 0.10064f, 0.04074f, 0.12281f];

        public static int LevelSide(int side, int level) => Math.Max(side >> level, 1);

        /// <summary>How many levels a scene of this size gets: at most <see cref="MaxLevels"/>, 0 when even level 2 would be under <see cref="MinLevelSide"/> (no bloom).</summary>
        public static int LevelCount(int width, int height)
        {
            int levels = 0;
            for (int level = 1; level <= MaxLevels && (width >> level) >= MinLevelSide && (height >> level) >= MinLevelSide; level++) levels = level;
            return levels >= 2 ? levels : 0;
        }

        /// <summary>The weight of <paramref name="level"/> when the pyramid stops at <paramref name="levels"/>: its own, and for the coarsest level also those of the
        /// levels that do not exist, so the sum stays one. 0 for level 1 and past the coarsest.</summary>
        public static float LevelWeight(int levels, int level)
        {
            if (level < 2 || level > levels) return 0;
            float w = Weights[level - 2];
            if (level == levels)
                for (int l = levels + 1; l <= MaxLevels; l++) w += Weights[l - 2];
            return w;
        }
    }

    sealed class BloomPrefilterPass : FullscreenProgram
    {
        public readonly SamplerSlot Scene;
        public readonly UniformHandle DestSize, Firefly;
        public BloomPrefilterPass(GpuContext gpu) : base(gpu, PostProcessShaders.BloomPrefilter, "post bloom prefilter") =>
            (Scene, DestSize, Firefly) = (P.Sampler("uScene"), P.Uniform("uDestSize"), P.Uniform("uFirefly"));
    }

    sealed class BloomDownPass : FullscreenProgram
    {
        public readonly SamplerSlot Source;
        public readonly UniformHandle Texel;
        public BloomDownPass(GpuContext gpu) : base(gpu, PostProcessShaders.BloomDown, "post bloom down") => (Source, Texel) = (P.Sampler("uSource"), P.Uniform("uTexel"));
    }

    sealed class BloomUpPass : FullscreenProgram
    {
        public readonly SamplerSlot Level, Coarse;
        public readonly UniformHandle Params;
        public BloomUpPass(GpuContext gpu) : base(gpu, PostProcessShaders.BloomUp, "post bloom up") =>
            (Level, Coarse, Params) = (P.Sampler("uLevel"), P.Sampler("uCoarse"), P.Uniform("uParams"));
    }

    BloomPrefilterPass? bloomPrefilter;   // made the first time the bloom runs
    BloomDownPass? bloomDownPass;
    BloomUpPass? bloomUpPass;
    // bloomDown[l - 1] is level l (L_l); bloomUp[l - 2] the sum of level l and the coarser ones (U_l), bloomUp[0] what the composite reads.
    readonly Target2D?[] bloomDown = new Target2D?[BloomPlan.MaxLevels], bloomUp = new Target2D?[BloomPlan.MaxLevels];
    int bloomWidth, bloomHeight;

    IEnumerable<Target2D> BloomTargets() => bloomDown.Concat(bloomUp).OfType<Target2D>();

    void FreeBloom()
    {
        foreach (var t in BloomTargets()) t.Texture.Dispose();   // released after the frames in flight
        Array.Clear(bloomDown);
        Array.Clear(bloomUp);
        bloomWidth = bloomHeight = 0;
    }

    /// <summary>The pyramid for a scene of this size (again when it changed): R11G11B10 floats, as linear and unsigned as the light.</summary>
    void EnsureBloom(int w, int h, int levels)
    {
        if (bloomWidth == w && bloomHeight == h) return;
        FreeBloom();
        (bloomWidth, bloomHeight) = (w, h);
        using var batch = Gpu.Uploads.Begin();
        for (int level = 1; level <= levels; level++)
            bloomDown[level - 1] = Make(batch, BloomPlan.LevelSide(w, level), BloomPlan.LevelSide(h, level), InternalFormat.R11fG11fB10f, TextureMinFilter.Linear, $"post bloom 1/{1 << level}");
        // The coarsest level has no sum of its own (the first up pass reads it directly), except when it is level 2, the output.
        for (int level = 2; level <= Math.Max(levels - 1, 2); level++)
            bloomUp[level - 2] = Make(batch, BloomPlan.LevelSide(w, level), BloomPlan.LevelSide(h, level), InternalFormat.R11fG11fB10f, TextureMinFilter.Linear, $"post bloom sum 1/{1 << level}");
    }

    /// <summary>The bloom of <paramref name="scene"/> at a quarter of its size, or null when it is off (strength 0) or the scene too small.</summary>
    Target2D? RunBloom(Target2D scene)
    {
        if (Options.BloomStrength <= 0) return null;
        int levels = BloomPlan.LevelCount(scene.Width, scene.Height);
        if (levels == 0) return null;
        EnsureBloom(scene.Width, scene.Height, levels);

        var pre = bloomPrefilter ??= new BloomPrefilterPass(Gpu);
        var half = bloomDown[0]!;
        Bind(pre.P, pre.Scene, scene);
        pre.P.Set(pre.DestSize, (float)half.Width, (float)half.Height);
        pre.P.Set(pre.Firefly, PostOptions.BloomFirefly);
        Draw(pre.P, half);

        var down = bloomDownPass ??= new BloomDownPass(Gpu);
        for (int level = 2; level <= levels; level++)
        {
            var source = bloomDown[level - 2]!;
            Bind(down.P, down.Source, source);
            down.P.Set(down.Texel, 1f / source.Width, 1f / source.Height);
            Draw(down.P, bloomDown[level - 1]!);
        }

        var up = bloomUpPass ??= new BloomUpPass(Gpu);
        for (int level = levels == 2 ? 2 : levels - 1; level >= 2; level--)
        {
            // The coarser image: the coarsest level itself (scaled by its weight here), or the sum below; nothing when level 2 is the coarsest.
            var coarse = levels == 2 ? bloomDown[level - 1]! : level + 1 == levels ? bloomDown[levels - 1]! : bloomUp[level - 1]!;
            float coarseScale = levels == 2 ? 0 : level + 1 == levels ? BloomPlan.LevelWeight(levels, levels) : 1;
            Bind(up.P, up.Level, bloomDown[level - 1]);
            Bind(up.P, up.Coarse, coarse);
            up.P.Set(up.Params, BloomPlan.LevelWeight(levels, level), coarseScale, 1f / coarse.Width, 1f / coarse.Height);
            Draw(up.P, bloomUp[level - 2]!);
        }
        Stamp("bloom");
        return bloomUp[0];
    }

    void DisposeBloom()
    {
        bloomPrefilter?.P.Dispose();
        bloomDownPass?.P.Dispose();
        bloomUpPass?.P.Dispose();
    }
}
