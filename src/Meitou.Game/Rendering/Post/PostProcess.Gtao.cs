using Meitou.Game.Rendering.Frame;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;

using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// Ground-truth ambient occlusion (<c>gtao</c>; docs/render-post.md "GTAO"), a separate program from the SSAO: the same targets, mask and blur. It goes onto the scene
/// by its own pass, after the merged layers: that pass upsamples by depth and multiplies a colour, which the merged pass's grey factor cannot carry.
/// </summary>
public sealed unsafe partial class PostProcess
{
    sealed class GtaoGatherPass : FullscreenProgram
    {
        public readonly SamplerSlot Packed, Surface;
        public readonly UniformHandle Levels;
        public readonly UniformHandle CharacterOccluder, Tan, NearFar, Size, Reach, March, FadeStart, FadeEnd, Right, Up, Back, Eye, WaterY;

        public GtaoGatherPass(GpuContext gpu, bool surface) : base(gpu, GtaoShaders.Gather(surface), "post gtao")
        {
            (Packed, Surface) = (P.Sampler("uPacked"), P.Sampler("uSurface"));
            Levels = P.Uniform("uLevels");
            (CharacterOccluder, Tan, NearFar, Size) = (P.Uniform("uCharacterOccluder"), P.Uniform("uTan"), P.Uniform("uNearFar"), P.Uniform("uSize"));
            (Reach, March, FadeStart, FadeEnd) = (P.Uniform("uReach"), P.Uniform("uMarch"), P.Uniform("uFadeStart"), P.Uniform("uFadeEnd"));
            (Right, Up, Back, Eye, WaterY) = (P.Uniform("uRight"), P.Uniform("uUp"), P.Uniform("uBack"), P.Uniform("uGtaoEye"), P.Uniform("uWaterY"));
        }
    }

    sealed class GtaoPackPass : FullscreenProgram
    {
        public readonly SamplerSlot Depth, Scene;
        public readonly UniformHandle NearFar;
        public GtaoPackPass(GpuContext gpu) : base(gpu, GtaoShaders.Pack, "post gtao pack") =>
            (Depth, Scene, NearFar) = (P.Sampler("uDepth"), P.Sampler("uScene"), P.Uniform("uNearFar"));
    }

    sealed class GtaoDepthLevelPass : FullscreenProgram
    {
        public readonly SamplerSlot Packed;
        public readonly UniformHandle Reach, From;
        public GtaoDepthLevelPass(GpuContext gpu) : base(gpu, GtaoShaders.DepthLevel, "post gtao depth level") =>
            (Packed, Reach, From) = (P.Sampler("uPacked"), P.Uniform("uReach"), P.Uniform("uFrom"));
    }

    GtaoDepthLevelPass? _gtaoDepthLevel;
    /// <summary>The gather's far taps read coarser levels of its depth (docs/render-post.md "GTAO"; <c>MEITOU_GTAO_LEVELS=0</c>: every tap reads the pixels, for comparing).</summary>
    public static bool GtaoDepthLevels { get; set; } = Environment.GetEnvironmentVariable("MEITOU_GTAO_LEVELS") != "0";
    /// <summary>The coarsest level of the gather's depth, and what a tap's reach in pixels (as a power of two) gives up before its level rises (XeGTAO's 3.3).</summary>
    const int GtaoLevels = 4;
    const float GtaoLevelOffset = 3.3f;

    GtaoPackPass? _gtaoPack;
    /// <summary>The gather's depth at the render size (<see cref="GtaoShaders.Pack"/>).</summary>
    Target2D? _gtaoPacked;

    sealed class GtaoApplyPass : FullscreenProgram
    {
        public readonly SamplerSlot Ao, Depth, Surface, Packed, Traced;
        public readonly UniformHandle Size, AoSize, NearFar, Curve, Character, TracedOn;

        public GtaoApplyPass(GpuContext gpu, bool surface) : base(gpu, GtaoShaders.Apply(surface), "post gtao apply")
        {
            (Ao, Depth, Surface, Packed, Traced) = (P.Sampler("uAo"), P.Sampler("uDepth"), P.Sampler("uSurface"), P.Sampler("uPacked"), P.Sampler("uTraced"));
            (Size, AoSize, NearFar, Curve, Character, TracedOn) = (P.Uniform("uSize"), P.Uniform("uAoSize"), P.Uniform("uNearFar"), P.Uniform("uCurve"), P.Uniform("uCharacter"),
                P.Uniform("uTracedOn"));
        }
    }

    // Each made when first used: [0] without the surface target, [1] with it.
    readonly GtaoGatherPass?[] _gtaoGather = new GtaoGatherPass?[2];
    readonly GtaoApplyPass?[] _gtaoApply = new GtaoApplyPass?[2];

    /// <summary>GTAO stands in for the SSAO this frame: it is <c>ao</c>'s method, so it needs that switch on too.</summary>
    bool GtaoOn => Options is { Ssao: true, Gtao: true };

    /// <summary>The surface target holds this frame's normals and albedos (the GI resolve is on).</summary>
    bool SurfaceReady => GiResolve > 0 && _giAlbedo is not null;

    /// <summary>The occlusion's targets at the size this frame asks for: half the render size, or all of it at GTAO's top quality.</summary>
    void EnsureAoSize()
    {
        if (_occlusion.A is null) return;
        bool full = GtaoOn && Options.GtaoFullSize;
        int w = full ? _width : Math.Max((_width + 1) / 2, 1), h = full ? _height : Math.Max((_height + 1) / 2, 1);
        if (_occlusion.A.Width == w && _occlusion.A.Height == h) return;
        _occlusion.A.Texture.Dispose();   // released after the frames in flight
        _occlusion.B?.Texture.Dispose();
        using var batch = Gpu.Uploads.Begin();
        _occlusion.A = Make(batch, w, h, InternalFormat.Rg16F, TextureMinFilter.Linear, "post ssao");
        _occlusion.B = Make(batch, w, h, InternalFormat.Rg16F, TextureMinFilter.Linear, "post ssao");
    }

    /// <summary><see cref="ApplySsao"/> with GTAO: the pending layers first (the old order: shafts, fog, occlusion), then the occlusion over them.</summary>
    void ApplyGtao()
    {
        if (MergeLayers) ApplyLayers(false);
        if (!_haveScene || _sceneColour is null || _occlusion.A is null) return;
        RunGtao();
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
        Bind(p.P, p.Packed, _gtaoPacked);
        p.P.Set(p.Character, PostOptions.GtaoStrength, _gtaoPacked is null ? 0f : 1f);
        var traced = CharacterVisibilityNow;
        Bind(p.P, p.Traced, traced);
        p.P.Set(p.TracedOn, traced is null ? 0f : 1f, PostOptions.CharacterOcclusionStrength);
        Draw(p.P, _sceneColour.Attachment, _sceneColour.Format, _width, _height, _width, _height, AoMultiplyState);
        Stamp("ssao");
        CloseSegment();
        _aoApplied = true;
    }

    /// <summary>The occlusion into <see cref="aoB"/>, as <see cref="RunSsao"/> leaves it: the mask, the gather, the blur.</summary>
    void RunGtao()
    {
        bool characters = _sceneColour is not null && (CharactersDrawn || !MaskSkip);
        // The depth and the characters' mask in one target for the gather's taps.
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
        if (GtaoDepthLevels)
        {
            var level = _gtaoDepthLevel ??= new GtaoDepthLevelPass(Gpu);
            for (int m = 1; m <= GtaoLevels; m++)
            {
                int w = Math.Max(_width >> m, 1), h = Math.Max(_height >> m, 1);
                Bind(level.P, level.Packed, _gtaoPacked);
                level.P.Set(level.Reach, PostOptions.GtaoRadius, PostOptions.GtaoMaxPixels * _height, PostOptions.GtaoMinPixels * _height, PostOptions.GtaoFalloff);
                level.P.Set(level.From, m - 1, 2 * MathF.Tan(_fovY * 0.5f) / _height);
                Draw(level.P, new RenderTarget(_gtaoPacked.Texture.Attachment(m), LoadOp.Load, default), _gtaoPacked.Format, w, h, w, h);
            }
        }
        GtaoGather(characters);
        if (AmbientOcclusion.Stamps) Stamp("gtao gen");
        GtaoBlur(characters);
    }

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

    void SetGtaoMarch(GtaoGatherPass g)
    {
        var o = Options;
        g.P.Set(g.Reach, PostOptions.GtaoRadius, PostOptions.GtaoMaxPixels * _height, PostOptions.GtaoMinPixels * _height, PostOptions.GtaoFalloff);
        // Over the frames with a temporal resolve to average them; a still pattern without one (moving noise would shimmer).
        float offset = Temporal ? _frameIndex % 64 * 5.588238f : 0f;
        g.P.Set(g.March, o.GtaoSlices, o.GtaoSteps, PostOptions.GtaoThickness, offset);
        g.P.Set(g.FadeStart, PostOptions.SsaoFadeStart);
        g.P.Set(g.FadeEnd, PostOptions.SsaoFadeEnd);
    }

    /// <summary>The SSAO's blur over the gather; the second pass takes the occlusion off the characters' pixels. Leaves the result in <see cref="aoB"/>.</summary>
    void GtaoBlur(bool characters)
    {
        var a = _occlusion.A!; var b = _occlusion.B!;
        var bl = _occlusion.Blur;
        Bind(bl.P, bl.Ao, a);
        Bind(bl.P, bl.Mask, null);
        bl.P.Set(bl.Step, 1f / a.Width, 0f);
        bl.P.Set(bl.CharacterAo, 1f);
        Draw(bl.P, b);
        Bind(bl.P, bl.Ao, b);
        Bind(bl.P, bl.Mask, characters ? _sceneColour : null);
        bl.P.Set(bl.Step, 0f, 1f / a.Height);
        bl.P.Set(bl.CharacterAo, characters && CharacterVisibilityNow is null ? PostOptions.GtaoCharacterStrength : 1f);
        Draw(bl.P, a);
        if (AmbientOcclusion.Stamps) Stamp("gtao blur");
        (_occlusion.A, _occlusion.B) = (_occlusion.B, _occlusion.A);
    }

    void DisposeGtao()
    {
        foreach (var p in _gtaoGather) p?.P.Dispose();
        foreach (var p in _gtaoApply) p?.P.Dispose();
        _gtaoPack?.P.Dispose();
        _gtaoDepthLevel?.P.Dispose();
    }
}
