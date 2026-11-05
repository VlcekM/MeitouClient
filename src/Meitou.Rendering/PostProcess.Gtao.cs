using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;

using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;

namespace Meitou.Rendering;

/// <summary>
/// Ground-truth ambient occlusion (<c>gtao</c>; docs/render-post.md "GTAO"), a separate program from the SSAO: the same targets, mask and blur. It goes onto the scene
/// by its own pass, after the merged layers: that pass upsamples by depth and multiplies a colour, which the merged pass's grey factor cannot carry.
/// </summary>
public sealed unsafe partial class PostProcess
{
    sealed class GtaoGatherPass : FullscreenProgram
    {
        public readonly SamplerSlot Depth, Mask, Surface;
        public readonly UniformHandle CharacterOccluder, Tan, NearFar, Size, Reach, March, FadeStart, FadeEnd, Right, Up, Back, Eye, WaterY;

        public GtaoGatherPass(GpuContext gpu, bool surface) : base(gpu, GtaoShaders.Gather(surface), "post gtao")
        {
            (Depth, Mask, Surface) = (P.Sampler("uDepth"), P.Sampler("uMask"), P.Sampler("uSurface"));
            (CharacterOccluder, Tan, NearFar, Size) = (P.Uniform("uCharacterOccluder"), P.Uniform("uTan"), P.Uniform("uNearFar"), P.Uniform("uSize"));
            (Reach, March, FadeStart, FadeEnd) = (P.Uniform("uReach"), P.Uniform("uMarch"), P.Uniform("uFadeStart"), P.Uniform("uFadeEnd"));
            (Right, Up, Back, Eye, WaterY) = (P.Uniform("uRight"), P.Uniform("uUp"), P.Uniform("uBack"), P.Uniform("uGtaoEye"), P.Uniform("uWaterY"));
        }
    }

    sealed class GtaoApplyPass : FullscreenProgram
    {
        public readonly SamplerSlot Ao, Depth, Surface;
        public readonly UniformHandle Size, AoSize, NearFar, Curve;

        public GtaoApplyPass(GpuContext gpu, bool surface) : base(gpu, GtaoShaders.Apply(surface), "post gtao apply")
        {
            (Ao, Depth, Surface) = (P.Sampler("uAo"), P.Sampler("uDepth"), P.Sampler("uSurface"));
            (Size, AoSize, NearFar, Curve) = (P.Uniform("uSize"), P.Uniform("uAoSize"), P.Uniform("uNearFar"), P.Uniform("uCurve"));
        }
    }

    // Each made when first used: [0] without the surface target, [1] with it.
    readonly GtaoGatherPass?[] gtaoGather = new GtaoGatherPass?[2];
    readonly GtaoApplyPass?[] gtaoApply = new GtaoApplyPass?[2];

    /// <summary>GTAO stands in for the SSAO this frame: it is <c>ao</c>'s method, so it needs that switch on too.</summary>
    bool GtaoOn => Options is { Ssao: true, Gtao: true };

    /// <summary>The surface target holds this frame's normals and albedos (the GI resolve is on).</summary>
    bool SurfaceReady => GiResolve > 0 && giAlbedo is not null;

    /// <summary>The occlusion's targets at the size this frame asks for: half the render size, or all of it at GTAO's top quality.</summary>
    void EnsureAoSize()
    {
        if (aoA is null) return;
        bool full = GtaoOn && Options.GtaoFullSize;
        int w = full ? width : Math.Max((width + 1) / 2, 1), h = full ? height : Math.Max((height + 1) / 2, 1);
        if (aoA.Width == w && aoA.Height == h) return;
        aoA.Texture.Dispose();   // released after the frames in flight
        aoB?.Texture.Dispose();
        using var batch = Gpu.Uploads.Begin();
        aoA = Make(batch, w, h, InternalFormat.RG16f, TextureMinFilter.Linear, "post ssao");
        aoB = Make(batch, w, h, InternalFormat.RG16f, TextureMinFilter.Linear, "post ssao");
    }

    /// <summary><see cref="ApplySsao"/> with GTAO: the pending layers first (the old order: shafts, fog, occlusion), then the occlusion over them.</summary>
    void ApplyGtao()
    {
        if (MergeLayers) ApplyLayers(false);
        if (!haveScene || sceneColour is null || aoA is null) return;
        RunGtao();
        bool surface = SurfaceReady;
        var p = gtaoApply[surface ? 1 : 0] ??= new GtaoApplyPass(Gpu, surface);
        Bind(p.P, p.Ao, aoB);
        Bind(p.P, p.Depth, sceneDepth);
        if (surface) Bind(p.P, p.Surface, giAlbedo);
        p.P.Set(p.Size, (float)width, (float)height);
        p.P.Set(p.AoSize, (float)aoB!.Width, (float)aoB.Height);
        p.P.Set(p.NearFar, nearPlane, farPlane);
        // A half-size texel's gather read the depth texel past its centre (GtaoShaders.GatherMain's floor): a quarter of that texel.
        float shift = aoB.Width < width ? 0.25f : 0f;
        p.P.Set(p.Curve, PostOptions.GtaoPower, PostOptions.GtaoStrength, PostOptions.GtaoAlbedo, shift);
        Draw(p.P, sceneColour.Attachment, sceneColour.Format, width, height, width, height, AoMultiplyState);
        Stamp("ssao");
        CloseSegment();
        aoApplied = true;
    }

    /// <summary>The occlusion into <see cref="aoB"/>, as <see cref="RunSsao"/> leaves it: the mask, the gather, the blur.</summary>
    void RunGtao()
    {
        bool characters = sceneColour is not null && (CharactersDrawn || !MaskSkip);
        if (characters)
        {
            Bind(ssaoMask.P, ssaoMask.Scene, sceneColour);
            Draw(ssaoMask.P, aoMask!);
        }
        GtaoGather(characters);
        if (SsaoStamps) Stamp("gtao gen");
        GtaoBlur(characters);
    }

    void GtaoGather(bool characters)
    {
        bool surface = SurfaceReady;
        var g = gtaoGather[surface ? 1 : 0] ??= new GtaoGatherPass(Gpu, surface);
        Bind(g.P, g.Depth, sceneDepth);
        Bind(g.P, g.Mask, characters ? aoMask : null);
        if (surface) Bind(g.P, g.Surface, giAlbedo);
        g.P.Set(g.CharacterOccluder, characters ? PostOptions.SsaoCharacterOccluder : 1f);
        SetGtaoView(g);
        SetGtaoMarch(g);
        g.P.ApplyGlobals();   // the atmosphere's and the fog volumes' uniforms, through the frame globals
        Draw(g.P, aoA!);
    }

    void SetGtaoView(GtaoGatherPass g)
    {
        float tanY = MathF.Tan(fovY * 0.5f);
        var r = viewRotation;
        g.P.Set(g.Tan, ViewWindow.Tan(tanY * aspect, tanY));
        g.P.Set(g.NearFar, nearPlane, farPlane);
        g.P.Set(g.Size, (float)width, (float)height);
        g.P.Set(g.Right, r.M11, r.M21, r.M31);
        g.P.Set(g.Up, r.M12, r.M22, r.M32);
        g.P.Set(g.Back, ViewWindow.BackFor(r, fovY, aspect));
        g.P.Set(g.Eye, eyeNow.X, eyeNow.Y, eyeNow.Z);
        g.P.Set(g.WaterY, WaterHeight ?? float.MinValue);
    }

    void SetGtaoMarch(GtaoGatherPass g)
    {
        var o = Options;
        g.P.Set(g.Reach, PostOptions.GtaoRadius, PostOptions.GtaoMaxPixels * height, PostOptions.GtaoMinPixels * height, PostOptions.GtaoFalloff);
        // Over the frames with a temporal resolve to average them; a still pattern without one (moving noise would shimmer).
        float offset = Temporal ? frameIndex % 64 * 5.588238f : 0f;
        g.P.Set(g.March, o.GtaoSlices, o.GtaoSteps, PostOptions.GtaoThickness, offset);
        g.P.Set(g.FadeStart, PostOptions.SsaoFadeStart);
        g.P.Set(g.FadeEnd, PostOptions.SsaoFadeEnd);
    }

    /// <summary>The SSAO's blur over the gather; the second pass takes the occlusion off the characters' pixels. Leaves the result in <see cref="aoB"/>.</summary>
    void GtaoBlur(bool characters)
    {
        var a = aoA!; var b = aoB!;
        var bl = blur;
        Bind(bl.P, bl.Ao, a);
        Bind(bl.P, bl.Mask, null);
        bl.P.Set(bl.Step, 1f / a.Width, 0f);
        bl.P.Set(bl.CharacterAo, 1f);
        Draw(bl.P, b);
        Bind(bl.P, bl.Ao, b);
        Bind(bl.P, bl.Mask, characters ? sceneColour : null);
        bl.P.Set(bl.Step, 0f, 1f / a.Height);
        bl.P.Set(bl.CharacterAo, characters ? PostOptions.SsaoCharacterStrength : 1f);
        Draw(bl.P, a);
        if (SsaoStamps) Stamp("gtao blur");
        (aoA, aoB) = (aoB, aoA);
    }

    void DisposeGtao()
    {
        foreach (var p in gtaoGather) p?.P.Dispose();
        foreach (var p in gtaoApply) p?.P.Dispose();
    }
}
