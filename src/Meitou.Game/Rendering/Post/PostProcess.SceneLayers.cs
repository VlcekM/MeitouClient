using System.Numerics;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The merged scene layers (docs/render-post.md "Merged scene layers"): with <see cref="MergeLayers"/> the light shafts' apply, the fog volumes and
/// the SSAO's factor go onto the scene colour in one full-screen pass (<see cref="PostProcessShaders.SceneLayers"/>) instead of three, each reading
/// the depth and the scene colour again; with <see cref="FogHalf"/> the fog volumes and with <see cref="ShaftsHalf"/> the shafts' apply are computed
/// at half the render size (one texture, <see cref="PostProcessShaders.LayersHalf"/>) and upsampled by depth there.
/// <see cref="RunLightShafts"/> and <see cref="RunFogVolumes"/> then leave their layer pending, <see cref="ApplySsao"/> draws them all.
/// </summary>
public sealed unsafe partial class PostProcess
{
    /// <summary>
    /// Meitou: one pass for the shafts, the fog volumes and the occlusion (default on; <c>--ab layer-merge</c>).
    /// </summary>
    public bool MergeLayers { get; set; } = true;
    /// <summary>
    /// Meitou: with <see cref="MergeLayers"/>, the fog volumes at half the render size (<c>--ab fog-half</c>).
    /// </summary>
    public bool FogHalf { get; set; } = true;
    /// <summary>
    /// Meitou: with <see cref="MergeLayers"/> and fog volumes in view, the light shafts' apply at half the render size too, in the fog volumes'
    /// texture (<c>--ab shafts-half</c>). Not for the shafts alone: a half-size pass and its upsample cost more than the apply they replace.
    /// </summary>
    public bool ShaftsHalf { get; set; } = true;
    /// <summary>
    /// Meitou: when the half-size texture holds every pending layer, the merged pass is a lean one (the upsample and the occlusion) and a
    /// second pass computes the few pixels no half-size texel fits at full size (<c>--ab layers-lean</c>).
    /// </summary>
    public bool LeanLayers { get; set; } = true;

    /// <summary>
    /// The ray and distance rebuild of the merged passes (<c>PostProcessShaders.SceneDistance</c>), the light shafts' camera and grid, and the
    /// fog volumes' shadow from the shafts' first atlas.
    /// </summary>
    class LayerPass : FullscreenProgram
    {
        /// <summary>
        /// The program's samplers <c>uNearDepth</c>, <c>uShaftInjected</c>, <c>uShaftLight</c>, <c>uShaftScatter</c>.
        /// </summary>
        public readonly SamplerSlot NearDepth, ShaftInjected, Light, Scatter;
        /// <summary>
        /// The program's uniforms <c>uNearPlanes</c>, <c>uWaterY</c>, <c>uFullSize</c>, the shafts' <c>uShaftGrid</c>, <c>uShaftRange</c>,
        /// <c>uShaftEye</c>, <c>uShaftRight</c>, <c>uShaftUp</c>, <c>uShaftBack</c>, <c>uShaftTan</c>, <c>uShaftFog</c>, <c>uShaftAtlas</c>,
        /// <c>uShaftParams</c>, <c>uShaftAir</c>, and <c>uLayers</c>.
        /// </summary>
        public readonly UniformHandle NearPlanes, WaterY, FullSize, Grid, Range, Eye, Right, Up, Back, Tan, ShaftFog, Atlas, Params, Air, Layers;
        /// <summary>
        /// Compiles the shared vertex shader with <paramref name="fragment"/> and resolves the shared handles.
        /// </summary>
        public LayerPass(GpuContext gpu, string fragment, string name) : base(gpu, fragment, name)
        {
            (NearDepth, ShaftInjected) = (P.Sampler("uNearDepth"), P.Sampler("uShaftInjected"));
            (NearPlanes, WaterY, FullSize) = (P.Uniform("uNearPlanes"), P.Uniform("uWaterY"), P.Uniform("uFullSize"));
            (Grid, Range, Eye, Tan, ShaftFog) = (P.Uniform("uShaftGrid"), P.Uniform("uShaftRange"), P.Uniform("uShaftEye"), P.Uniform("uShaftTan"),
                P.Uniform("uShaftFog"));
            (Right, Up, Back) = (P.Uniform("uShaftRight"), P.Uniform("uShaftUp"), P.Uniform("uShaftBack"));
            (Light, Scatter) = (P.Sampler("uShaftLight"), P.Sampler("uShaftScatter"));
            (Atlas, Params, Air, Layers) = (P.Uniform("uShaftAtlas"), P.Uniform("uShaftParams"), P.Uniform("uShaftAir"), P.Uniform("uLayers"));
        }
    }

    /// <summary>
    /// The merged pass over the scene colour: the pending layers, the half-size texture's upsample and the occlusion.
    /// </summary>
    sealed class SceneLayersPass : LayerPass
    {
        /// <summary>
        /// The program's samplers <c>uLayerHalf</c>, <c>uAo</c>.
        /// </summary>
        public readonly SamplerSlot LayerHalfTexture, Ao;
        /// <summary>
        /// The program's uniform <c>uHalf</c>.
        /// </summary>
        public readonly UniformHandle Half;
        /// <summary>
        /// Compiles the program and resolves its handles.
        /// </summary>
        public SceneLayersPass(GpuContext gpu, string fragment, string name) : base(gpu, fragment, name)
        {
            (LayerHalfTexture, Ao) = (P.Sampler("uLayerHalf"), P.Sampler("uAo"));
            Half = P.Uniform("uHalf");
        }
    }

    /// <summary>
    /// The half-size layers pass (<see cref="PostProcessShaders.LayersHalf"/>), made with its first use.
    /// </summary>
    LayerPass? _layersHalfPass;
    /// <summary>
    /// The merged pass, full and lean, each made with its first use.
    /// </summary>
    SceneLayersPass? _sceneLayers, _sceneLayersLean;
    /// <summary>
    /// The half-size layers texture, and the lean pass's mask (a depth target at the render size).
    /// </summary>
    Target2D? _layerHalf, _layerMask;

    // What is pending for the merged pass this frame besides the shafts' apply (LightShafts.Pending): the fog volumes, which of them the
    // half-size texture holds (this frame), and the shafts' grid the fog's shadow reads.
    /// <summary>
    /// The fog volumes are pending; the half-size texture holds the shafts, the fog.
    /// </summary>
    bool _fogPending, _halfHoldsShafts, _halfHoldsFog;
    /// <summary>
    /// This frame's shafts' grid for the fog's shadow, taken before <see cref="RunFogVolumes"/> resets the frame's.
    /// </summary>
    (Vector4 Grid, Vector4 Range, Vector2 Tan, float Darkening) _fogShadow;

    /// <summary>
    /// The uniforms both merged passes share; the grid and range those of this frame's shafts (unused while neither layer reads them).
    /// </summary>
    void SetLayerCommon(LayerPass p, (Vector4 Grid, Vector4 Range, Vector2 Tan, float Darkening) shaft)
    {
        Bind(p.P, p.NearDepth, _sceneDepth);
        p.P.Set(p.NearPlanes, _planes.X, _planes.Y);
        p.P.Set(p.WaterY, WaterHeight ?? float.MinValue);
        p.P.Set(p.FullSize, _width, _height);
        float tanY = MathF.Tan(_fovNow * 0.5f);
        var r = _viewRotation;
        p.P.Set(p.Tan, ViewWindow.Tan(tanY * _aspectNow, tanY));
        p.P.Set(p.Right, r.M11, r.M21, r.M31);
        p.P.Set(p.Up, r.M12, r.M22, r.M32);
        p.P.Set(p.Back, ViewWindow.BackFor(r, _fovNow, _aspectNow));
        p.P.Set(p.Eye, _eyeNow);
        p.P.Set(p.Grid, shaft.Grid);
        p.P.Set(p.Range, shaft.Range);
        Bind(p.P, p.ShaftInjected, shaft.Darkening > 0 ? _shafts.Injected : null);
        p.P.Set(p.ShaftFog, shaft.Darkening, _shafts.Injected?.Width ?? 1, _shafts.Injected?.Height ?? 1, 0);

        // The shafts' layer: its atlases and parameters (read only where the pass applies the shafts).
        Bind(p.P, p.Light, _shafts.Pending ? _shafts.Light : null);
        Bind(p.P, p.Scatter, _shafts.Pending ? _shafts.Scatter : null);
        p.P.Set(p.Atlas, _shafts.Injected?.Width ?? 1, _shafts.Injected?.Height ?? 1);
        p.P.Set(p.Params, _shafts.Args.Params);
        p.P.Set(p.Air, _shafts.Args.Air);
    }

    /// <summary>
    /// The fog volumes for the merged pass: at half size into the half-size texture now (with the shafts' apply when <see cref="ShaftsHalf"/>),
    /// or left to the merged pass (full size).
    /// </summary>
    void PrepareFogLayer()
    {
        _fogShadow = _shafts.Frame;
        _fogPending = true;
        PrepareHalfLayers(FogHalf, ShaftsHalf && _shafts.Pending, _fogShadow);
    }

    /// <summary>
    /// The half-size pass for the layers asked for (which the merged pass then upsamples), when there is any.
    /// </summary>
    void PrepareHalfLayers(bool fog, bool shafts, (Vector4 Grid, Vector4 Range, Vector2 Tan, float Darkening) grid)
    {
        if (!fog && !shafts) return;
        int hw = Math.Max((_width + 1) / 2, 1), hh = Math.Max((_height + 1) / 2, 1);
        if (_layerHalf is null || _layerHalf.Width != hw || _layerHalf.Height != hh)
        {
            _layerHalf?.Texture.Dispose();   // released after the frames in flight
            using var batch = Gpu.Uploads.Begin();
            _layerHalf = Make(batch, hw, hh, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post layers half");
        }
        var f = _layersHalfPass ??= new LayerPass(Gpu, PostProcessShaders.LayersHalf, "post layers half");
        SetLayerCommon(f, grid);
        f.P.Set(f.Layers, shafts ? 1 : 0, fog ? 1 : 0, 0, 0);
        f.P.ApplyGlobals();   // the atmosphere's and the fog volumes' uniforms, through the frame globals
        Draw(f.P, _layerHalf);
        (_halfHoldsShafts, _halfHoldsFog) = (shafts, fog);
    }

    /// <summary>
    /// The pending layers and, with <paramref name="occlusion"/>, the SSAO (run here) in one pass over the scene colour. Nothing when no layer is
    /// pending.
    /// </summary>
    void ApplyLayers(bool occlusion)
    {
        if (occlusion) RunSsao();
        if (!_shafts.Pending && !_fogPending && !occlusion) return;

        // The fog's shadow and the shafts' layer read the same grid when both ran; the fog's copy was taken before RunFogVolumes reset the frame's.
        var grid = _fogPending ? _fogShadow : _shafts.Frame;
        bool half = _halfHoldsShafts || _halfHoldsFog;

        // Lean: the half-size texture holds every pending layer, so the pass only upsamples it; the pixels it cannot are drawn after.
        bool lean = LeanLayers && half && _halfHoldsShafts == _shafts.Pending && _halfHoldsFog == _fogPending;
        if (lean)
        {
            int w = _width, h = _height;
            if (_layerMask is null || _layerMask.Width != w || _layerMask.Height != h)
            {
                _layerMask?.Texture.Dispose();   // released after the frames in flight
                using var batch = Gpu.Uploads.Begin();
                _layerMask = Make(batch, w, h, InternalFormat.DepthComponent24, TextureMinFilter.Nearest, "post layers mask");
            }
            var l = _sceneLayersLean ??= new SceneLayersPass(Gpu, PostProcessShaders.SceneLayersLean, "post scene layers lean");
            SetLayerCommon(l, grid);
            Bind(l.P, l.LayerHalfTexture, _layerHalf);
            Bind(l.P, l.Ao, occlusion ? _occlusion.B : null);
            l.P.Set(l.Layers, _shafts.Pending ? 1 : 0, _fogPending ? 1 : 0, occlusion ? 1 : 0, 0);
            l.P.ApplyGlobals();

            // The mask's own depth protocol (clear 0, the shader writes 0 or 1), not the scene's DepthConvention.
            DrawPass(l.P, _sceneColour!.Attachment, _sceneColour.Format,
                _layerMask.Attachment with { Load = LoadOp.Clear, DepthClear = 0f }, _layerMask.Format,
                _width, _height, _width, _height, LeanState);
            if (AmbientOcclusion.Stamps) Stamp("layers lean");
        }
        var p = _sceneLayers ??= new SceneLayersPass(Gpu, PostProcessShaders.SceneLayers, "post scene layers");
        SetLayerCommon(p, grid);
        Bind(p.P, p.LayerHalfTexture, half && !lean ? _layerHalf : null);
        Bind(p.P, p.Ao, occlusion ? _occlusion.B : null);
        p.P.Set(p.Layers, _shafts.Pending ? 1 : 0, _fogPending ? 1 : 0, occlusion ? 1 : 0, 0);
        p.P.Set(p.Half, half && !lean && _halfHoldsShafts ? 1 : 0, half && !lean && _halfHoldsFog ? 1 : 0, 0, 0);
        p.P.ApplyGlobals();
        if (lean)
        {
            DrawPass(p.P, _sceneColour!.Attachment, _sceneColour.Format, _layerMask!.Attachment, _layerMask.Format, _width, _height, _width, _height,
                    FallbackState);
        }
        else
            Draw(p.P, _sceneColour!.Attachment, _sceneColour.Format, _width, _height, _width, _height, FogState);
        Stamp("layers");
        CloseSegment();
        _shafts.Pending = false;
        _fogPending = false;
        _halfHoldsShafts = _halfHoldsFog = false;
        if (occlusion) _aoApplied = true;
    }

    /// <summary>
    /// The lean pass: the fog's blend, and the mask depth written (always passing) for the full pass to test.
    /// </summary>
    static readonly DrawState LeanState = FogState with { DepthTest = true, DepthWrite = true, Compare = DepthFunction.Always };
    /// <summary>
    /// The full pass over the pixels the lean one marked (mask depth 1): the full-screen triangle (depth 0.5) is less than it, and not less than the
    /// 0 of the rest.
    /// </summary>
    static readonly DrawState FallbackState = FogState with { DepthTest = true, DepthWrite = false, Compare = DepthFunction.Less };

    /// <summary>
    /// Frees the layer passes' programs and targets.
    /// </summary>
    void DisposeSceneLayers()
    {
        _layersHalfPass?.P.Dispose();
        _sceneLayers?.P.Dispose();
        _sceneLayersLean?.P.Dispose();
        _layerHalf?.Texture.Dispose();
        _layerMask?.Texture.Dispose();
    }
}