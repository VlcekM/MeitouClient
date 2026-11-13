using Meitou.Game.Rendering.Frame;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;

using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// Ground-truth ambient occlusion (<c>gtao</c>; docs/render-post.md "GTAO"), a separate program from the SSAO: the same targets, mask and blur. It
/// goes onto the scene by its own pass, after the merged layers: that pass upsamples by depth and multiplies a colour, which the merged pass's grey
/// factor cannot carry.
/// </summary>
public sealed unsafe partial class PostProcess
{
    #region GTAO pass programs

    /// <summary>
    /// The horizon search (<see cref="GtaoShaders.Gather"/>).
    /// </summary>
    sealed class GtaoGatherPass : FullscreenProgram
    {
        /// <summary>
        /// The packed depth and the GI resolve's surface target.
        /// </summary>
        public readonly SamplerSlot Packed, Surface;

        /// <summary>
        /// The coarsest level a tap may read and the reach a level rises by.
        /// </summary>
        public readonly UniformHandle Levels;

        /// <summary>
        /// The character's share as an occluder, the view, the reach, the march, the fade, the camera's axes and eye, the water's height.
        /// </summary>
        public readonly UniformHandle CharacterOccluder, Tan, NearFar, Size, Reach, March, FadeStart, FadeEnd, Right, Up, Back, Eye, WaterY;

        /// <summary>
        /// Compiles the gather, reading the surface target for its normals when <paramref name="surface"/>, and looks up its slots.
        /// </summary>
        public GtaoGatherPass(GpuContext gpu, bool surface) : base(gpu, GtaoShaders.Gather(surface), "post gtao")
        {
            (Packed, Surface) = (P.Sampler("uPacked"), P.Sampler("uSurface"));
            Levels = P.Uniform("uLevels");
            (CharacterOccluder, Tan, NearFar, Size) = (P.Uniform("uCharacterOccluder"), P.Uniform("uTan"), P.Uniform("uNearFar"), P.Uniform("uSize"));
            (Reach, March, FadeStart, FadeEnd) = (P.Uniform("uReach"), P.Uniform("uMarch"), P.Uniform("uFadeStart"), P.Uniform("uFadeEnd"));
            (Right, Up, Back, Eye, WaterY) = (P.Uniform("uRight"), P.Uniform("uUp"), P.Uniform("uBack"), P.Uniform("uGtaoEye"), P.Uniform("uWaterY"));
        }
    }

    /// <summary>
    /// The depth and characters' mask packed into one target (<see cref="GtaoShaders.Pack"/>).
    /// </summary>
    sealed class GtaoPackPass : FullscreenProgram
    {
        /// <summary>
        /// The scene depth and colour (its alpha marks characters).
        /// </summary>
        public readonly SamplerSlot Depth, Scene;

        /// <summary>
        /// The scene depth's planes.
        /// </summary>
        public readonly UniformHandle NearFar;

        /// <summary>
        /// Compiles the pack and looks up its slots.
        /// </summary>
        public GtaoPackPass(GpuContext gpu) : base(gpu, GtaoShaders.Pack, "post gtao pack") =>
            (Depth, Scene, NearFar) = (P.Sampler("uDepth"), P.Sampler("uScene"), P.Uniform("uNearFar"));
    }

    /// <summary>
    /// A coarser level of the packed depth (<see cref="GtaoShaders.DepthLevel"/>).
    /// </summary>
    sealed class GtaoDepthLevelPass : FullscreenProgram
    {
        /// <summary>
        /// The packed depth, read at the level before.
        /// </summary>
        public readonly SamplerSlot Packed;

        /// <summary>
        /// The gather's reach, and the level read with a pixel's width per unit of depth.
        /// </summary>
        public readonly UniformHandle Reach, From;

        /// <summary>
        /// Compiles the depth filter and looks up its slots.
        /// </summary>
        public GtaoDepthLevelPass(GpuContext gpu) : base(gpu, GtaoShaders.DepthLevel, "post gtao depth level") =>
            (Packed, Reach, From) = (P.Sampler("uPacked"), P.Uniform("uReach"), P.Uniform("uFrom"));
    }

    /// <summary>
    /// The occlusion onto the scene (<see cref="GtaoShaders.Apply"/>).
    /// </summary>
    sealed class GtaoApplyPass : FullscreenProgram
    {
        /// <summary>
        /// The blurred occlusion, the scene depth and the GI resolve's surface target.
        /// </summary>
        public readonly SamplerSlot Ao, Depth, Surface;

        /// <summary>
        /// The render size, the occlusion's size, the depth's planes and the contrast curve.
        /// </summary>
        public readonly UniformHandle Size, AoSize, NearFar, Curve;

        /// <summary>
        /// Compiles the apply, reading the surface target for its albedo when <paramref name="surface"/>, and looks up its slots.
        /// </summary>
        public GtaoApplyPass(GpuContext gpu, bool surface) : base(gpu, GtaoShaders.Apply(surface), "post gtao apply")
        {
            (Ao, Depth, Surface) = (P.Sampler("uAo"), P.Sampler("uDepth"), P.Sampler("uSurface"));
            (Size, AoSize, NearFar, Curve) = (P.Uniform("uSize"), P.Uniform("uAoSize"), P.Uniform("uNearFar"), P.Uniform("uCurve"));
        }
    }

    #endregion

    #region GTAO

    /// <summary>
    /// The coarsest level of the gather's depth.
    /// </summary>
    const int GtaoLevels = 4;

    /// <summary>
    /// What a tap's reach in pixels (as a power of two) gives up before its level rises (XeGTAO's 3.3).
    /// </summary>
    const float GtaoLevelOffset = 3.3f;

    /// <summary>
    /// The depth filter, made when first used.
    /// </summary>
    GtaoDepthLevelPass? _gtaoDepthLevel;

    /// <summary>
    /// The pack, made when first used.
    /// </summary>
    GtaoPackPass? _gtaoPack;

    /// <summary>
    /// The gather's depth at the render size (<see cref="GtaoShaders.Pack"/>).
    /// </summary>
    Target2D? _gtaoPacked;

    /// <summary>
    /// The gathers, each made when first used: [0] without the surface target, [1] with it.
    /// </summary>
    readonly GtaoGatherPass?[] _gtaoGather = new GtaoGatherPass?[2];

    /// <summary>
    /// The applies, each made when first used: [0] without the surface target, [1] with it.
    /// </summary>
    readonly GtaoApplyPass?[] _gtaoApply = new GtaoApplyPass?[2];

    /// <summary>
    /// The gather's far taps read coarser levels of its depth (docs/render-post.md "GTAO"; <c>MEITOU_GTAO_LEVELS=0</c>: every tap reads the pixels,
    /// for comparing).
    /// </summary>
    public static bool GtaoDepthLevels { get; set; } = Environment.GetEnvironmentVariable("MEITOU_GTAO_LEVELS") != "0";

    /// <summary>
    /// GTAO stands in for the SSAO this frame: it is <c>ao</c>'s method, so it needs that switch on too.
    /// </summary>
    bool GtaoOn => Options is { Ssao: true, Gtao: true };

    /// <summary>
    /// The surface target holds this frame's normals and albedos (the GI resolve is on).
    /// </summary>
    bool SurfaceReady => GiResolve > 0 && _giAlbedo is not null;

    /// <summary>
    /// The occlusion's targets at the size this frame asks for: half the render size, or all of it at GTAO's top quality.
    /// </summary>
    void EnsureAoSize()
    {
        if (_occlusion.A is null) return;

        bool full = GtaoOn && Options.GtaoFullSize;
        int w = full ? _width : Math.Max((_width + 1) / 2, 1);
        int h = full ? _height : Math.Max((_height + 1) / 2, 1);
        if (_occlusion.A.Width == w && _occlusion.A.Height == h) return;

        _occlusion.A.Texture.Dispose();   // released after the frames in flight
        _occlusion.B?.Texture.Dispose();

        using var batch = Gpu.Uploads.Begin();
        _occlusion.A = Make(batch, w, h, InternalFormat.Rg16F, TextureMinFilter.Linear, "post ssao");
        _occlusion.B = Make(batch, w, h, InternalFormat.Rg16F, TextureMinFilter.Linear, "post ssao");
    }

    /// <summary>
    /// <see cref="ApplySsao"/> with GTAO: the pending layers first (the old order: shafts, fog, occlusion), then the occlusion over them.
    /// </summary>
    void ApplyGtao()
    {
        if (MergeLayers) ApplyLayers(false);
        if (!_haveScene || _sceneColour is null || _occlusion.A is null) return;

        RunGtao();
        DrawGtaoApply();
        Stamp("ssao");
        CloseSegment();
        _aoApplied = true;
    }

    /// <summary>
    /// The occlusion into <see cref="AmbientOcclusion.B"/>, as <see cref="RunSsao"/> leaves it: the mask, the gather, the blur.
    /// </summary>
    void RunGtao()
    {
        bool characters = _sceneColour is not null && (CharactersDrawn || !MaskSkip);

        PackGtaoDepth();
        if (GtaoDepthLevels) BuildGtaoDepthLevels();

        GtaoGather(characters);
        if (AmbientOcclusion.Stamps) Stamp("gtao gen");
        GtaoBlur(characters);
    }

    /// <summary>
    /// Releases the pass programs.
    /// </summary>
    void DisposeGtao()
    {
        foreach (var p in _gtaoGather) p?.P.Dispose();
        foreach (var p in _gtaoApply) p?.P.Dispose();
        _gtaoPack?.P.Dispose();
        _gtaoDepthLevel?.P.Dispose();
    }

    /// <summary>
    /// The blurred occlusion multiplied onto the scene colour, upsampled by depth.
    /// </summary>
    void DrawGtaoApply()
    {
        bool surface = SurfaceReady;
        var p = _gtaoApply[surface ? 1 : 0] ??= new GtaoApplyPass(Gpu, surface);
        Bind(p.P, p.Ao, _occlusion.B);
        Bind(p.P, p.Depth, _sceneDepth);
        if (surface) Bind(p.P, p.Surface, _giAlbedo);

        p.P.Set(p.Size, (float)_width, (float)_height);
        p.P.Set(p.AoSize, (float)_occlusion.B!.Width, (float)_occlusion.B.Height);
        p.P.Set(p.NearFar, _nearPlane, _farPlane);

        // A half-size texel's gather read the depth texel past its centre (GtaoShaders.GatherMain's floor): a quarter of that texel.
        float shift = _occlusion.B.Width < _width ? 0.25f : 0f;
        p.P.Set(p.Curve, PostOptions.GtaoPower, PostOptions.GtaoStrength, PostOptions.GtaoAlbedo, shift);
        Draw(p.P, _sceneColour!.Attachment, _sceneColour.Format, _width, _height, _width, _height, AoMultiplyState);
    }

    /// <summary>
    /// The depth and the characters' mask in one target for the gather's taps.
    /// </summary>
    void PackGtaoDepth()
    {
        if (_gtaoPacked is null || _gtaoPacked.Width != _width || _gtaoPacked.Height != _height)
        {
            _gtaoPacked?.Texture.Dispose();   // released after the frames in flight
            using var batch = Gpu.Uploads.Begin();
            _gtaoPacked = Make(batch, _width, _height, InternalFormat.R32F, TextureMinFilter.Nearest, "post gtao depth", GtaoLevels + 1);
        }

        var pack = _gtaoPack ??= new GtaoPackPass(Gpu);
        Bind(pack.P, pack.Depth, _sceneDepth);
        Bind(pack.P, pack.Scene, _sceneColour);
        pack.P.Set(pack.NearFar, _nearPlane, _farPlane);
        Draw(pack.P, _gtaoPacked);
    }

    /// <summary>
    /// The packed depth's coarser levels, each from the one before.
    /// </summary>
    void BuildGtaoDepthLevels()
    {
        var level = _gtaoDepthLevel ??= new GtaoDepthLevelPass(Gpu);
        for (int m = 1; m <= GtaoLevels; m++)
        {
            int w = Math.Max(_width >> m, 1);
            int h = Math.Max(_height >> m, 1);
            Bind(level.P, level.Packed, _gtaoPacked);
            SetGtaoReach(level.P, level.Reach);
            level.P.Set(level.From, m - 1, 2 * MathF.Tan(_fovY * 0.5f) / _height);
            Draw(level.P, new RenderTarget(_gtaoPacked!.Texture.Attachment(m), LoadOp.Load, default), _gtaoPacked.Format, w, h, w, h);
        }
    }

    /// <summary>
    /// The horizon search into the occlusion's first target.
    /// </summary>
    void GtaoGather(bool characters)
    {
        bool surface = SurfaceReady;
        var g = _gtaoGather[surface ? 1 : 0] ??= new GtaoGatherPass(Gpu, surface);
        Bind(g.P, g.Packed, _gtaoPacked);
        g.P.Set(g.Levels, GtaoDepthLevels ? GtaoLevels : 0, GtaoLevelOffset);
        if (surface) Bind(g.P, g.Surface, _giAlbedo);

        g.P.Set(g.CharacterOccluder, characters ? PostOptions.SsaoCharacterOccluder : 1f);
        SetGtaoView(g);
        SetGtaoMarch(g);
        g.P.ApplyGlobals();   // the atmosphere's and the fog volumes' uniforms, through the frame globals
        Draw(g.P, _occlusion.A!);
    }

    /// <summary>
    /// The gather's view: the frustum's tangents, the depth's planes, the render size, the camera's axes and eye, the water's height.
    /// </summary>
    void SetGtaoView(GtaoGatherPass g)
    {
        float tanY = MathF.Tan(_fovY * 0.5f);
        var r = _viewRotation;
        g.P.Set(g.Tan, ViewWindow.Tan(tanY * _aspect, tanY));
        g.P.Set(g.NearFar, _nearPlane, _farPlane);
        g.P.Set(g.Size, (float)_width, (float)_height);
        g.P.Set(g.Right, r.M11, r.M21, r.M31);
        g.P.Set(g.Up, r.M12, r.M22, r.M32);
        g.P.Set(g.Back, ViewWindow.BackFor(r, _fovY, _aspect));
        g.P.Set(g.Eye, _eyeNow.X, _eyeNow.Y, _eyeNow.Z);
        g.P.Set(g.WaterY, WaterHeight ?? float.MinValue);
    }

    /// <summary>
    /// The gather's reach, march and fade.
    /// </summary>
    void SetGtaoMarch(GtaoGatherPass g)
    {
        var o = Options;
        SetGtaoReach(g.P, g.Reach);

        // Over the frames with a temporal resolve to average them; a still pattern without one (moving noise would shimmer).
        float offset = Temporal ? _frameIndex % 64 * 5.588238f : 0f;
        g.P.Set(g.March, o.GtaoSlices, o.GtaoSteps, PostOptions.GtaoThickness, offset);
        g.P.Set(g.FadeStart, PostOptions.SsaoFadeStart);
        g.P.Set(g.FadeEnd, PostOptions.SsaoFadeEnd);
    }

    /// <summary>
    /// The gather's reach as the gather and the depth filter read it: the radius in world units, the most and the least pixels, the full-weight share.
    /// </summary>
    void SetGtaoReach(LegacyProgram program, UniformHandle reach) =>
        program.Set(reach, PostOptions.GtaoRadius, PostOptions.GtaoMaxPixels * _height, PostOptions.GtaoMinPixels * _height, PostOptions.GtaoFalloff);

    /// <summary>
    /// The SSAO's blur over the gather; the second pass takes the occlusion off the characters' pixels. Leaves the result in <see cref="AmbientOcclusion.B"/>.
    /// </summary>
    void GtaoBlur(bool characters)
    {
        var a = _occlusion.A!;
        var b = _occlusion.B!;
        var bl = _occlusion.Blur;

        Bind(bl.P, bl.Ao, a);
        Bind(bl.P, bl.Mask, null);
        bl.P.Set(bl.Step, 1f / a.Width, 0f);
        bl.P.Set(bl.CharacterAo, 1f);
        Draw(bl.P, b);

        Bind(bl.P, bl.Ao, b);
        Bind(bl.P, bl.Mask, characters ? _sceneColour : null);
        bl.P.Set(bl.Step, 0f, 1f / a.Height);
        bl.P.Set(bl.CharacterAo, characters ? PostOptions.SsaoCharacterStrength : 1f);
        Draw(bl.P, a);
        if (AmbientOcclusion.Stamps) Stamp("gtao blur");

        (_occlusion.A, _occlusion.B) = (_occlusion.B, _occlusion.A);
    }

    #endregion
}
