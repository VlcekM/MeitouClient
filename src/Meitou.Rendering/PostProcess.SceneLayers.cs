using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using System.Numerics;

using Meitou.Rendering.Gpu;

using Vk = Silk.NET.Vulkan;

namespace Meitou.Rendering;

/// <summary>
/// The merged scene layers (docs/render-post.md "Merged scene layers"): with <see cref="MergeLayers"/> the light shafts' apply, the fog volumes and the
/// SSAO's factor go onto the scene colour in one full-screen pass (<see cref="PostProcessShaders.SceneLayers"/>) instead of three, each reading the
/// depth and the scene colour again; with <see cref="FogHalf"/> the fog volumes and with <see cref="ShaftsHalf"/> the shafts' apply are computed at half the
/// render size (one texture, <see cref="PostProcessShaders.LayersHalf"/>) and upsampled by depth there.
/// <see cref="RunLightShafts"/> and <see cref="RunFogVolumes"/> then leave their layer pending, <see cref="ApplySsao"/> draws them all.
/// </summary>
public sealed unsafe partial class PostProcess
{
    /// <summary>Meitou: one pass for the shafts, the fog volumes and the occlusion (default on; <c>--ab layer-merge</c>).</summary>
    public bool MergeLayers { get; set; } = true;
    /// <summary>Meitou: with <see cref="MergeLayers"/>, the fog volumes at half the render size (<c>--ab fog-half</c>).</summary>
    public bool FogHalf { get; set; } = true;
    /// <summary>Meitou: with <see cref="MergeLayers"/> and fog volumes in view, the light shafts' apply at half the render size too, in the fog volumes' texture
    /// (<c>--ab shafts-half</c>). Not for the shafts alone: a half-size pass and its upsample cost more than the apply they replace.</summary>
    public bool ShaftsHalf { get; set; } = true;
    /// <summary>Meitou: when the half-size texture holds every pending layer, the merged pass is a lean one (the upsample and the occlusion) and a
    /// second pass computes the few pixels no half-size texel fits at full size (<c>--ab layers-lean</c>).</summary>
    public bool LeanLayers { get; set; } = true;

    /// <summary>The ray and distance rebuild of the merged passes (<c>PostProcessShaders.SceneDistance</c>), the light shafts' camera and grid, and the
    /// fog volumes' shadow from the shafts' first atlas.</summary>
    class LayerPass : FullscreenProgram
    {
        public readonly SamplerSlot NearDepth, FarDepth, ShaftInjected, Light, Scatter;
        public readonly UniformHandle NearPlanes, FarPlanes, WaterY, HasFar, FullSize, Grid, Range, Eye, Right, Up, Back, Tan, ShaftFog, Atlas, Params, Air, Layers;
        public LayerPass(GpuContext gpu, string fragment, string name) : base(gpu, fragment, name)
        {
            (NearDepth, FarDepth, ShaftInjected) = (P.Sampler("uNearDepth"), P.Sampler("uFarDepth"), P.Sampler("uShaftInjected"));
            (NearPlanes, FarPlanes, WaterY, HasFar, FullSize) = (P.Uniform("uNearPlanes"), P.Uniform("uFarPlanes"), P.Uniform("uWaterY"), P.Uniform("uHasFar"), P.Uniform("uFullSize"));
            (Grid, Range, Eye, Tan, ShaftFog) = (P.Uniform("uShaftGrid"), P.Uniform("uShaftRange"), P.Uniform("uShaftEye"), P.Uniform("uShaftTan"), P.Uniform("uShaftFog"));
            (Right, Up, Back) = (P.Uniform("uShaftRight"), P.Uniform("uShaftUp"), P.Uniform("uShaftBack"));
            (Light, Scatter) = (P.Sampler("uShaftLight"), P.Sampler("uShaftScatter"));
            (Atlas, Params, Air, Layers) = (P.Uniform("uShaftAtlas"), P.Uniform("uShaftParams"), P.Uniform("uShaftAir"), P.Uniform("uLayers"));
        }
    }

    sealed class SceneLayersPass : LayerPass
    {
        public readonly SamplerSlot LayerHalfTexture, Ao;
        public readonly UniformHandle Half;
        public SceneLayersPass(GpuContext gpu, string fragment, string name) : base(gpu, fragment, name)
        {
            (LayerHalfTexture, Ao) = (P.Sampler("uLayerHalf"), P.Sampler("uAo"));
            Half = P.Uniform("uHalf");
        }
    }

    LayerPass? layersHalfPass;
    SceneLayersPass? sceneLayers, sceneLayersLean;
    Target2D? layerHalf, layerMask;

    // What is pending for the merged pass this frame: the shafts' apply (its grid and parameters), the fog volumes, which of them the half-size texture holds
    // (this frame), and the shafts' grid the fog's shadow reads.
    bool shaftsPending;
    (Vector4 Air, Vector4 Params) shaftArgs;
    bool fogPending, halfHoldsShafts, halfHoldsFog;
    (Vector4 Grid, Vector4 Range, Vector2 Tan, float Darkening) fogShadow;

    /// <summary>The uniforms both merged passes share; the grid and range those of this frame's shafts (unused while neither layer reads them).</summary>
    void SetLayerCommon(LayerPass p, (Vector4 Grid, Vector4 Range, Vector2 Tan, float Darkening) shaft)
    {
        Bind(p.P, p.NearDepth, sceneDepth);
        Bind(p.P, p.FarDepth, farSliceDrawn ? farDepth : null);
        p.P.Set(p.NearPlanes, nearPlanes.X, nearPlanes.Y);
        p.P.Set(p.FarPlanes, farPlanes.X, farPlanes.Y);
        p.P.Set(p.WaterY, WaterHeight ?? float.MinValue);
        p.P.Set(p.HasFar, farSliceDrawn ? 1 : 0);
        p.P.Set(p.FullSize, width, height);
        float tanY = MathF.Tan(fovNow * 0.5f);
        var r = viewRotation;
        p.P.Set(p.Tan, tanY * aspectNow, tanY);
        p.P.Set(p.Right, r.M11, r.M21, r.M31);
        p.P.Set(p.Up, r.M12, r.M22, r.M32);
        p.P.Set(p.Back, r.M13, r.M23, r.M33);
        p.P.Set(p.Eye, eyeNow);
        p.P.Set(p.Grid, shaft.Grid);
        p.P.Set(p.Range, shaft.Range);
        Bind(p.P, p.ShaftInjected, shaft.Darkening > 0 ? shaftInjected : null);
        p.P.Set(p.ShaftFog, shaft.Darkening, shaftInjected?.Width ?? 1, shaftInjected?.Height ?? 1, 0);
        // The shafts' layer: its atlases and parameters (read only where the pass applies the shafts).
        Bind(p.P, p.Light, shaftsPending ? shaftLight : null);
        Bind(p.P, p.Scatter, shaftsPending ? shaftScatter : null);
        p.P.Set(p.Atlas, shaftInjected?.Width ?? 1, shaftInjected?.Height ?? 1);
        p.P.Set(p.Params, shaftArgs.Params);
        p.P.Set(p.Air, shaftArgs.Air);
    }

    /// <summary>The fog volumes for the merged pass: at half size into the half-size texture now (with the shafts' apply when <see cref="ShaftsHalf"/>),
    /// or left to the merged pass (full size).</summary>
    void PrepareFogLayer()
    {
        fogShadow = shaftFrame;
        fogPending = true;
        PrepareHalfLayers(FogHalf, ShaftsHalf && shaftsPending, fogShadow);
    }

    /// <summary>The half-size pass for the layers asked for (which the merged pass then upsamples), when there is any.</summary>
    void PrepareHalfLayers(bool fog, bool shafts, (Vector4 Grid, Vector4 Range, Vector2 Tan, float Darkening) grid)
    {
        if (!fog && !shafts) return;
        int hw = Math.Max((width + 1) / 2, 1), hh = Math.Max((height + 1) / 2, 1);
        if (layerHalf is null || layerHalf.Width != hw || layerHalf.Height != hh)
        {
            layerHalf?.Texture.Dispose();   // released after the frames in flight
            using var batch = Gpu.Uploads.Begin();
            layerHalf = Make(batch, hw, hh, InternalFormat.Rgba16f, TextureMinFilter.Nearest, "post layers half");
        }
        var f = layersHalfPass ??= new LayerPass(Gpu, PostProcessShaders.LayersHalf, "post layers half");
        SetLayerCommon(f, grid);
        f.P.Set(f.Layers, shafts ? 1 : 0, fog ? 1 : 0, 0, 0);
        f.P.ApplyGlobals();   // the atmosphere's and the fog volumes' uniforms, through the frame globals
        Draw(f.P, layerHalf);
        (halfHoldsShafts, halfHoldsFog) = (shafts, fog);
    }

    /// <summary>The pending layers and, with <paramref name="occlusion"/>, the SSAO (run here) in one pass over the scene colour. Nothing when no layer is pending.</summary>
    void ApplyLayers(bool occlusion)
    {
        if (occlusion) RunSsao();
        if (!shaftsPending && !fogPending && !occlusion) return;
        // The fog's shadow and the shafts' layer read the same grid when both ran; the fog's copy was taken before RunFogVolumes reset the frame's.
        var grid = fogPending ? fogShadow : shaftFrame;
        bool half = halfHoldsShafts || halfHoldsFog;
        // Lean: the half-size texture holds every pending layer, so the pass only upsamples it; the pixels it cannot are drawn after.
        bool lean = LeanLayers && half && halfHoldsShafts == shaftsPending && halfHoldsFog == fogPending;
        if (lean)
        {
            int w = width, h = height;
            if (layerMask is null || layerMask.Width != w || layerMask.Height != h)
            {
                layerMask?.Texture.Dispose();   // released after the frames in flight
                using var batch = Gpu.Uploads.Begin();
                layerMask = Make(batch, w, h, InternalFormat.DepthComponent24, TextureMinFilter.Nearest, "post layers mask");
            }
            var l = sceneLayersLean ??= new SceneLayersPass(Gpu, PostProcessShaders.SceneLayersLean, "post scene layers lean");
            SetLayerCommon(l, grid);
            Bind(l.P, l.LayerHalfTexture, layerHalf);
            Bind(l.P, l.Ao, occlusion ? aoB : null);
            l.P.Set(l.Layers, shaftsPending ? 1 : 0, fogPending ? 1 : 0, occlusion ? 1 : 0, 0);
            l.P.ApplyGlobals();
            DrawPass(l.P, sceneColour!.Attachment, sceneColour.Format,
                layerMask.Attachment with { Load = LoadOp.Clear, DepthClear = 0f }, layerMask.Format,
                width, height, width, height, LeanState);
            if (SsaoStamps) Stamp("layers lean");
        }
        var p = sceneLayers ??= new SceneLayersPass(Gpu, PostProcessShaders.SceneLayers, "post scene layers");
        SetLayerCommon(p, grid);
        Bind(p.P, p.LayerHalfTexture, half && !lean ? layerHalf : null);
        Bind(p.P, p.Ao, occlusion ? aoB : null);
        p.P.Set(p.Layers, shaftsPending ? 1 : 0, fogPending ? 1 : 0, occlusion ? 1 : 0, 0);
        p.P.Set(p.Half, half && !lean && halfHoldsShafts ? 1 : 0, half && !lean && halfHoldsFog ? 1 : 0, 0, 0);
        p.P.ApplyGlobals();
        if (lean)
            DrawPass(p.P, sceneColour!.Attachment, sceneColour.Format, layerMask!.Attachment, layerMask.Format, width, height, width, height, FallbackState);
        else
            Draw(p.P, sceneColour!.Attachment, sceneColour.Format, width, height, width, height, FogState);
        Stamp("layers");
        CloseSegment();
        shaftsPending = false;
        fogPending = false;
        halfHoldsShafts = halfHoldsFog = false;
        if (occlusion) aoApplied = true;
    }

    /// <summary>The lean pass: the fog's blend, and the mask depth written (always passing) for the full pass to test.</summary>
    static readonly DrawState LeanState = FogState with { DepthTest = true, DepthWrite = true, Compare = DepthFunction.Always };
    /// <summary>The full pass over the pixels the lean one marked (mask depth 1): the full-screen triangle (depth 0.5) is less than it, and not less than the 0 of the rest.</summary>
    static readonly DrawState FallbackState = FogState with { DepthTest = true, DepthWrite = false, Compare = DepthFunction.Less };

    void DisposeSceneLayers()
    {
        layersHalfPass?.P.Dispose();
        sceneLayers?.P.Dispose();
        sceneLayersLean?.P.Dispose();
        layerHalf?.Texture.Dispose();
        layerMask?.Texture.Dispose();
    }
}
