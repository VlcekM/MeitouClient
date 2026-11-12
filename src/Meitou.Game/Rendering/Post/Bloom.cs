using Meitou.Game.Rendering.Gpu;
using Target2D = Meitou.Game.Rendering.Post.PostProcess.Target2D;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// Meitou bloom (the <c>bloom</c> switch; docs/render-post.md "Bloom"): the scene convolved with a point spread function and mixed into it by the
/// composite, <c>lerp(scene, psf ⊗ scene, strength)</c>, so the frame keeps its energy. No threshold and no history. The kernel is a pyramid:
/// <see cref="PostProcessShaders.BloomPrefilter"/> to half size, <see cref="PostProcessShaders.BloomDown"/> to 1/128, then
/// <see cref="PostProcessShaders.BloomUp"/> sums the levels back from the coarsest, <c>U_i = w_i L_i + tent(U_i+1)</c>, into the quarter-size level
/// the composite reads. After the upscaler, at the size the composite reads the scene at.
/// </summary>
sealed class Bloom(PostProcess post)
{
    #region Passes

    /// <summary>
    /// The scene to half size (<see cref="PostProcessShaders.BloomPrefilter"/>).
    /// </summary>
    sealed class BloomPrefilterPass : PostProcess.FullscreenProgram
    {
        /// <summary>
        /// The scene.
        /// </summary>
        public readonly SamplerSlot Scene;

        /// <summary>
        /// The half-size level's size and the firefly clamp.
        /// </summary>
        public readonly UniformHandle DestSize, Firefly;

        /// <summary>
        /// Compiles the program and resolves its handles.
        /// </summary>
        public BloomPrefilterPass(GpuContext gpu) : base(gpu, PostProcessShaders.BloomPrefilter, "post bloom prefilter") =>
            (Scene, DestSize, Firefly) = (P.Sampler("uScene"), P.Uniform("uDestSize"), P.Uniform("uFirefly"));
    }

    /// <summary>
    /// One level down (<see cref="PostProcessShaders.BloomDown"/>).
    /// </summary>
    sealed class BloomDownPass : PostProcess.FullscreenProgram
    {
        /// <summary>
        /// The finer level.
        /// </summary>
        public readonly SamplerSlot Source;

        /// <summary>
        /// The finer level's texel.
        /// </summary>
        public readonly UniformHandle Texel;

        /// <summary>
        /// Compiles the program and resolves its handles.
        /// </summary>
        public BloomDownPass(GpuContext gpu) : base(gpu, PostProcessShaders.BloomDown, "post bloom down") => (Source, Texel) = (P.Sampler("uSource"),
            P.Uniform("uTexel"));
    }

    /// <summary>
    /// One level's sum on the way up (<see cref="PostProcessShaders.BloomUp"/>).
    /// </summary>
    sealed class BloomUpPass : PostProcess.FullscreenProgram
    {
        /// <summary>
        /// The level and the coarser image.
        /// </summary>
        public readonly SamplerSlot Level, Coarse;

        /// <summary>
        /// The level's weight, the coarser image's scale and its texel.
        /// </summary>
        public readonly UniformHandle Params;

        /// <summary>
        /// Compiles the program and resolves its handles.
        /// </summary>
        public BloomUpPass(GpuContext gpu) : base(gpu, PostProcessShaders.BloomUp, "post bloom up") =>
            (Level, Coarse, Params) = (P.Sampler("uLevel"), P.Sampler("uCoarse"), P.Uniform("uParams"));
    }

    #endregion

    #region Pyramid

    /// <summary>
    /// The prefilter pass, made the first time the bloom runs.
    /// </summary>
    BloomPrefilterPass? _bloomPrefilter;

    /// <summary>
    /// The down pass, made the first time the bloom runs.
    /// </summary>
    BloomDownPass? _bloomDownPass;

    /// <summary>
    /// The up pass, made the first time the bloom runs.
    /// </summary>
    BloomUpPass? _bloomUpPass;

    /// <summary>
    /// The levels and their sums: bloomDown[l - 1] is level l (L_l); bloomUp[l - 2] the sum of level l and the coarser ones (U_l), bloomUp[0] what
    /// the composite reads.
    /// </summary>
    readonly Target2D?[] _bloomDown = new Target2D?[BloomPlan.MaxLevels], _bloomUp = new Target2D?[BloomPlan.MaxLevels];

    /// <summary>
    /// The scene size the pyramid was made for (0: none).
    /// </summary>
    int _bloomWidth, _bloomHeight;

    /// <summary>
    /// Frees the pyramid's targets; the next run makes them again.
    /// </summary>
    public void Free()
    {
        foreach (var t in Targets()) t.Texture.Dispose();   // released after the frames in flight
        Array.Clear(_bloomDown);
        Array.Clear(_bloomUp);
        _bloomWidth = _bloomHeight = 0;
    }

    /// <summary>
    /// The bloom of <paramref name="scene"/> at a quarter of its size, or null when it is off (strength 0) or the scene too small.
    /// </summary>
    public Target2D? Run(Target2D scene)
    {
        if (post.Options.BloomAmount <= 0) return null;
        int levels = BloomPlan.LevelCount(scene.Width, scene.Height);
        if (levels == 0) return null;
        Ensure(scene.Width, scene.Height, levels);

        var pre = _bloomPrefilter ??= new BloomPrefilterPass(post.Gpu);
        var half = _bloomDown[0]!;
        post.Bind(pre.P, pre.Scene, scene);
        pre.P.Set(pre.DestSize, (float)half.Width, (float)half.Height);
        pre.P.Set(pre.Firefly, PostOptions.BloomFirefly);
        post.Draw(pre.P, half);

        var down = _bloomDownPass ??= new BloomDownPass(post.Gpu);
        for (int level = 2; level <= levels; level++)
        {
            var source = _bloomDown[level - 2]!;
            post.Bind(down.P, down.Source, source);
            down.P.Set(down.Texel, 1f / source.Width, 1f / source.Height);
            post.Draw(down.P, _bloomDown[level - 1]!);
        }

        var up = _bloomUpPass ??= new BloomUpPass(post.Gpu);
        for (int level = levels == 2 ? 2 : levels - 1; level >= 2; level--)
        {
            // The coarser image: the coarsest level itself (scaled by its weight here), or the sum below; nothing when level 2 is the coarsest.
            var coarse = levels == 2 ? _bloomDown[level - 1]! : level + 1 == levels ? _bloomDown[levels - 1]! : _bloomUp[level - 1]!;
            float coarseScale = levels == 2 ? 0 : level + 1 == levels ? BloomPlan.LevelWeight(levels, levels) : 1;
            post.Bind(up.P, up.Level, _bloomDown[level - 1]);
            post.Bind(up.P, up.Coarse, coarse);
            up.P.Set(up.Params, BloomPlan.LevelWeight(levels, level), coarseScale, 1f / coarse.Width, 1f / coarse.Height);
            post.Draw(up.P, _bloomUp[level - 2]!);
        }
        post.Stamp("bloom");
        return _bloomUp[0];
    }

    /// <summary>
    /// The pyramid's targets that exist.
    /// </summary>
    IEnumerable<Target2D> Targets() => _bloomDown.Concat(_bloomUp).OfType<Target2D>();

    /// <summary>
    /// The pyramid for a scene of this size (again when it changed): R11G11B10 floats, as linear and unsigned as the light.
    /// </summary>
    void Ensure(int w, int h, int levels)
    {
        if (_bloomWidth == w && _bloomHeight == h) return;
        Free();
        (_bloomWidth, _bloomHeight) = (w, h);
        using var batch = post.Gpu.Uploads.Begin();
        for (int level = 1; level <= levels; level++)
        {
            _bloomDown[level - 1] = post.Make(batch, BloomPlan.LevelSide(w, level), BloomPlan.LevelSide(h, level), InternalFormat.R11FG11FB10F,
                TextureMinFilter.Linear, $"post bloom 1/{1 << level}");
        }

        // The coarsest level has no sum of its own (the first up pass reads it directly), except when it is level 2, the output.
        for (int level = 2; level <= Math.Max(levels - 1, 2); level++)
        {
            _bloomUp[level - 2] = post.Make(batch, BloomPlan.LevelSide(w, level), BloomPlan.LevelSide(h, level), InternalFormat.R11FG11FB10F,
                TextureMinFilter.Linear, $"post bloom sum 1/{1 << level}");
        }
    }

    #endregion

    #region Lifetime

    /// <summary>
    /// Disposes the passes (the targets go with <see cref="Free"/>).
    /// </summary>
    public void Dispose()
    {
        _bloomPrefilter?.P.Dispose();
        _bloomDownPass?.P.Dispose();
        _bloomUpPass?.P.Dispose();
    }

    #endregion
}

/// <summary>
/// The bloom pyramid's shape and the levels' weights.
/// </summary>
static class BloomPlan
{
    /// <summary>
    /// Levels below the scene: 1/2 down to 1/128 of its size.
    /// </summary>
    public const int MaxLevels = 7;

    /// <summary>
    /// The coarsest level stops once a side would drop below this: a 13-tap step over fewer texels is mostly clamped edge.
    /// </summary>
    public const int MinLevelSide = 4;

    /// <summary>
    /// The weights of levels 2 (quarter size) to 7, summing to one: fitted to the scattered part of the eye's photopic PSF (Spencer et al. 1995)
    /// at a 45° vertical field of view, as the composite reads level 2 (through the B-spline). The fit is KenshiNative's
    /// (<c>tools/checks/bloompsf.py --fit</c>).
    /// </summary>
    public static readonly float[] Weights = [0.41405f, 0.21190f, 0.10986f, 0.10064f, 0.04074f, 0.12281f];

    /// <summary>
    /// The side of <paramref name="level"/> for a scene side of <paramref name="side"/> (at least 1).
    /// </summary>
    public static int LevelSide(int side, int level) => Math.Max(side >> level, 1);

    /// <summary>
    /// How many levels a scene of this size gets: at most <see cref="MaxLevels"/>, 0 when even level 2 would be under <see cref="MinLevelSide"/>
    /// (no bloom).
    /// </summary>
    public static int LevelCount(int width, int height)
    {
        int levels = 0;
        for (int level = 1; level <= MaxLevels && (width >> level) >= MinLevelSide && (height >> level) >= MinLevelSide; level++) levels = level;
        return levels >= 2 ? levels : 0;
    }

    /// <summary>
    /// The weight of <paramref name="level"/> when the pyramid stops at <paramref name="levels"/>: its own, and for the coarsest level also those
    /// of the levels that do not exist, so the sum stays one. 0 for level 1 and past the coarsest.
    /// </summary>
    public static float LevelWeight(int levels, int level)
    {
        if (level < 2 || level > levels) return 0;
        float w = Weights[level - 2];
        if (level == levels)
            for (int l = levels + 1; l <= MaxLevels; l++) w += Weights[l - 2];
        return w;
    }
}
