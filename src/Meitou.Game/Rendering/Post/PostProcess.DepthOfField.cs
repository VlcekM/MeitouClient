using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// Meitou depth of field (the <c>dof</c> switch; docs/render-post.md "Depth of field"): a thin lens's circle of confusion from the camera's field of
/// view, f-number and focus, then a half-size scatter-as-gather with a near and a far field, in linear light (<see cref="CameraEffectShaders"/>).
/// After the upscaler, into its own target. No history. Port of KenshiNative's <c>docs/features/dof-rewrite.md</c> without its sparkle;
/// KenshiNative's lens is a slider's radius, this one's is the optics.
/// </summary>
public sealed unsafe partial class PostProcess
{
    #region Lens and pass programs

    /// <summary>
    /// The thin lens in the camera's own terms.
    /// </summary>
    internal static class ThinLens
    {
        /// <summary>
        /// The sensor's height: a full frame's, in millimetres.
        /// </summary>
        public const float SensorHeight = 24;

        /// <summary>
        /// Millimetres in a world unit (10 units are a metre: docs/render-post.md "Fog").
        /// </summary>
        public const float UnitLength = 100;

        /// <summary>
        /// The focal length that gives a vertical field of view on the sensor, in millimetres.
        /// </summary>
        public static float FocalLength(float fieldOfView) => SensorHeight / 2 / MathF.Tan(fieldOfView / 2);

        /// <summary>
        /// The radius, in half-size pixels of a picture <paramref name="height"/> high, of the disc a point at infinity blurs to with the lens
        /// focused at <paramref name="focus"/> world units: the aperture's diameter f / N scaled by f / (s - f), over the sensor. Nearer points take
        /// (z - s) / z of it, in the shader.
        /// </summary>
        public static float InfinityCoc(float fieldOfView, float aperture, float focus, int height)
        {
            float f = FocalLength(fieldOfView);
            float s = MathF.Max(focus * UnitLength, f + 1);
            float diameter = f * f / (MathF.Max(aperture, 0.01f) * (s - f));

            return diameter / SensorHeight * height * 0.25f;
        }
    }

    /// <summary>
    /// What the two passes that compute a CoC share: the depth, its planes and jitter, the lens.
    /// </summary>
    abstract class DofLensPass : FullscreenProgram
    {
        /// <summary>
        /// The picture, its depth and the autofocus texel.
        /// </summary>
        public readonly SamplerSlot Scene, Depth, Focus;

        /// <summary>
        /// The depth's planes, the jitter in picture units, the lens (CoC at infinity, focus, largest CoC) and the autofocus's lens.
        /// </summary>
        public readonly UniformHandle Planes, JitterUv, Lens, Auto;

        /// <summary>
        /// Compiles <paramref name="fragment"/> and looks up the lens's slots.
        /// </summary>
        protected DofLensPass(GpuContext gpu, string fragment, string name) : base(gpu, fragment, name)
        {
            (Scene, Depth, Focus) = (P.Sampler("uScene"), P.Sampler("uDepth"), P.Sampler("uFocus"));
            (Planes, JitterUv, Lens, Auto) = (P.Uniform("uPlanes"), P.Uniform("uJitterUv"), P.Uniform("uLens"), P.Uniform("uAuto"));
        }
    }

    /// <summary>
    /// The autofocus's one-texel pass (<see cref="CameraEffectShaders.DofFocus"/>).
    /// </summary>
    sealed class DofFocusPass : FullscreenProgram
    {
        /// <summary>
        /// The scene depth and the last frame's focus texel.
        /// </summary>
        public readonly SamplerSlot Depth, Previous;

        /// <summary>
        /// The depth's planes, the jitter in picture units, the spot (ease, fallback focus, size, aspect) and its centre.
        /// </summary>
        public readonly UniformHandle Planes, JitterUv, Spot, Centre;

        /// <summary>
        /// Compiles the autofocus and looks up its slots.
        /// </summary>
        public DofFocusPass(GpuContext gpu) : base(gpu, CameraEffectShaders.DofFocus, "post dof focus")
        {
            (Depth, Previous) = (P.Sampler("uDepth"), P.Sampler("uPrevious"));
            (Planes, JitterUv, Spot, Centre) = (P.Uniform("uPlanes"), P.Uniform("uJitterUv"), P.Uniform("uSpot"), P.Uniform("uCentre"));
        }
    }

    /// <summary>
    /// Pass 1 (<see cref="CameraEffectShaders.DofPrepare"/>): the half-size light and CoC.
    /// </summary>
    sealed class DofPreparePass(GpuContext gpu) : DofLensPass(gpu, CameraEffectShaders.DofPrepare, "post dof prepare");

    /// <summary>
    /// Pass 6 (<see cref="CameraEffectShaders.DofComposite"/>): the two fields over the sharp picture.
    /// </summary>
    sealed class DofCompositePass : DofLensPass
    {
        /// <summary>
        /// The filtered far field and the near field.
        /// </summary>
        public readonly SamplerSlot FarField, NearField;

        /// <summary>
        /// Compiles the composite and looks up its fields' slots.
        /// </summary>
        public DofCompositePass(GpuContext gpu) : base(gpu, CameraEffectShaders.DofComposite, "post dof") =>
            (FarField, NearField) = (P.Sampler("uFarField"), P.Sampler("uNearField"));
    }

    /// <summary>
    /// Pass 2 (<see cref="CameraEffectShaders.DofTile"/>): the largest near CoC per tile.
    /// </summary>
    sealed class DofTilePass : FullscreenProgram
    {
        /// <summary>
        /// Pass 1's output.
        /// </summary>
        public readonly SamplerSlot Prepared;

        /// <summary>
        /// The tiles' count across and down.
        /// </summary>
        public readonly UniformHandle Tiles;

        /// <summary>
        /// Compiles the tile pass and looks up its slots.
        /// </summary>
        public DofTilePass(GpuContext gpu) : base(gpu, CameraEffectShaders.DofTile, "post dof tile") =>
            (Prepared, Tiles) = (P.Sampler("uPrepared"), P.Uniform("uTiles"));
    }

    /// <summary>
    /// Pass 3 (<see cref="CameraEffectShaders.DofDilate"/>): the largest near CoC that can reach each tile.
    /// </summary>
    sealed class DofDilatePass : FullscreenProgram
    {
        /// <summary>
        /// Pass 2's tiles.
        /// </summary>
        public readonly SamplerSlot Tile;

        /// <summary>
        /// The largest CoC and a tile's span in half-size pixels.
        /// </summary>
        public readonly UniformHandle Reach;

        /// <summary>
        /// Compiles the dilation and looks up its slots.
        /// </summary>
        public DofDilatePass(GpuContext gpu) : base(gpu, CameraEffectShaders.DofDilate, "post dof dilate") =>
            (Tile, Reach) = (P.Sampler("uTile"), P.Uniform("uReach"));
    }

    /// <summary>
    /// Pass 4 (<see cref="CameraEffectShaders.DofFar"/>): the far field's gather.
    /// </summary>
    sealed class DofFarPass : FullscreenProgram
    {
        /// <summary>
        /// Pass 1's output.
        /// </summary>
        public readonly SamplerSlot Prepared;

        /// <summary>
        /// Compiles the far gather and looks up its slot.
        /// </summary>
        public DofFarPass(GpuContext gpu) : base(gpu, CameraEffectShaders.DofFar, "post dof far") => Prepared = P.Sampler("uPrepared");
    }

    /// <summary>
    /// The far field's filter (<see cref="CameraEffectShaders.DofFarFilter"/>).
    /// </summary>
    sealed class DofFarFilterPass : FullscreenProgram
    {
        /// <summary>
        /// The gathered far field.
        /// </summary>
        public readonly SamplerSlot Far;

        /// <summary>
        /// Compiles the far filter and looks up its slot.
        /// </summary>
        public DofFarFilterPass(GpuContext gpu) : base(gpu, CameraEffectShaders.DofFarFilter, "post dof far filter") => Far = P.Sampler("uFar");
    }

    /// <summary>
    /// Pass 5 (<see cref="CameraEffectShaders.DofNear"/>): the near field's gather.
    /// </summary>
    sealed class DofNearPass : FullscreenProgram
    {
        /// <summary>
        /// Pass 1's output and pass 3's dilated tiles.
        /// </summary>
        public readonly SamplerSlot Prepared, Dilated;

        /// <summary>
        /// Compiles the near gather and looks up its slots.
        /// </summary>
        public DofNearPass(GpuContext gpu) : base(gpu, CameraEffectShaders.DofNear, "post dof near") =>
            (Prepared, Dilated) = (P.Sampler("uPrepared"), P.Sampler("uDilated"));
    }

    #endregion

    #region Autofocus

    /// <summary>
    /// The autofocus's spot (its half height as a share of the picture's) and the time its focus takes to cover most of a change.
    /// </summary>
    const float DofFocusSpot = 0.03f, DofFocusSeconds = 0.15f;

    /// <summary>
    /// The autofocus pass, made the first time it runs.
    /// </summary>
    DofFocusPass? _dofFocusPass;

    /// <summary>
    /// The autofocus's two texels: this frame's and the last's, by turns.
    /// </summary>
    readonly Target2D?[] _dofFocus = new Target2D?[2];

    /// <summary>
    /// Which of <see cref="_dofFocus"/> is this frame's.
    /// </summary>
    int _dofFocusTurn;

    /// <summary>
    /// Whether the last texel of <see cref="_dofFocus"/> holds a focus.
    /// </summary>
    bool _dofFocusHeld;

    /// <summary>
    /// The time since the last autofocus, which the ease is by.
    /// </summary>
    readonly System.Diagnostics.Stopwatch _dofFocusClock = new();

    /// <summary>
    /// This frame's autofocus texel: the nearest surface at the picture's centre, eased from the last frame's (snapped when the last is older than
    /// half a second: the effect was off, or the view was another).
    /// </summary>
    Target2D FocusNow(Target2D scene)
    {
        float dt = _dofFocusClock.IsRunning ? (float)_dofFocusClock.Elapsed.TotalSeconds : float.MaxValue;
        _dofFocusClock.Restart();
        bool snap = !_dofFocusHeld || dt > 0.5f;

        var previous = _dofFocus[_dofFocusTurn]!;
        _dofFocusTurn ^= 1;
        var now = _dofFocus[_dofFocusTurn]!;

        var pass = _dofFocusPass ??= new DofFocusPass(Gpu);
        Bind(pass.P, pass.Depth, _sceneDepth);
        Bind(pass.P, pass.Previous, previous);
        pass.P.Set(pass.Planes, _planes.X, _planes.Y);
        pass.P.Set(pass.JitterUv, JitterPixels.X / _width, JitterPixels.Y / _height);
        pass.P.Set(pass.Centre, Options.LensFocusSpot.X, Options.LensFocusSpot.Y);
        float ease = snap ? 1f : 1 - MathF.Exp(-dt / DofFocusSeconds);
        float aspect = scene.Width / (float)Math.Max(scene.Height, 1);
        pass.P.Set(pass.Spot, ease, Options.LensFocus, DofFocusSpot, aspect);
        Draw(pass.P, now);
        _dofFocusHeld = true;

        return now;
    }

    #endregion

    #region Depth of field

    /// <summary>
    /// Half-size pixels to a tile.
    /// </summary>
    const int DofTileSize = 8;

    /// <summary>
    /// Pass 1, made the first time the depth of field runs.
    /// </summary>
    DofPreparePass? _dofPrepare;

    /// <summary>
    /// Pass 2, made the first time the depth of field runs.
    /// </summary>
    DofTilePass? _dofTile;

    /// <summary>
    /// Pass 3, made the first time the depth of field runs.
    /// </summary>
    DofDilatePass? _dofDilate;

    /// <summary>
    /// Pass 4, made the first time the depth of field runs.
    /// </summary>
    DofFarPass? _dofFar;

    /// <summary>
    /// The far field's filter, made the first time the depth of field runs.
    /// </summary>
    DofFarFilterPass? _dofFarFilter;

    /// <summary>
    /// Pass 5, made the first time the depth of field runs.
    /// </summary>
    DofNearPass? _dofNear;

    /// <summary>
    /// Pass 6, made the first time the depth of field runs.
    /// </summary>
    DofCompositePass? _dofComposite;

    /// <summary>
    /// The passes' targets at the picture's size (<see cref="EnsureDepthOfField"/>).
    /// </summary>
    Target2D? _dofPrepared, _dofTiles, _dofDilated, _dofFarField, _dofFarSmooth, _dofNearField, _dofOutput;

    /// <summary>
    /// Whether the camera effects may run on this frame: a whole view of a scene, in the cinematic cameras or everywhere by the player's choice.
    /// </summary>
    bool CameraEffectsLive => _haveScene && ViewWindow.Current.IsWhole && !Options.CameraEffectsHeld
        && (Options.CinematicView || Options.CameraEffectsEverywhere);

    /// <summary>
    /// The picture through the lens, or <paramref name="scene"/> when the depth of field does not run (off, another camera).
    /// </summary>
    Target2D RunDepthOfField(Target2D scene)
    {
        if (!(Options.PhotoDepthOfField ?? Options.DepthOfField) || !CameraEffectsLive) return scene;

        EnsureDepthOfField(scene);
        float maxCoc = MathF.Min(MathF.Max(scene.Height * PostOptions.DofMaxCocShare, 2), PostOptions.DofMaxCocPixels);

        var focus = FocusNow(scene);
        var prepare = _dofPrepare ??= new DofPreparePass(Gpu);
        SetLens(prepare, scene, maxCoc, focus);
        Draw(prepare.P, _dofPrepared!);
        DofNearTiles(maxCoc);
        DofFields();

        var composite = _dofComposite ??= new DofCompositePass(Gpu);
        SetLens(composite, scene, maxCoc, focus);
        Bind(composite.P, composite.FarField, _dofFarSmooth);
        Bind(composite.P, composite.NearField, _dofNearField);
        Draw(composite.P, _dofOutput!);
        Stamp("dof");

        return _dofOutput!;
    }

    /// <summary>
    /// Releases the targets and the autofocus texels.
    /// </summary>
    void FreeDepthOfField()
    {
        // Released after the frames in flight.
        foreach (var t in (ReadOnlySpan<Target2D?>)[_dofPrepared, _dofTiles, _dofDilated, _dofFarField, _dofFarSmooth, _dofNearField, _dofOutput])
            t?.Texture.Dispose();
        _dofPrepared = _dofTiles = _dofDilated = _dofFarField = _dofFarSmooth = _dofNearField = _dofOutput = null;

        foreach (var t in _dofFocus) t?.Texture.Dispose();
        (_dofFocus[0], _dofFocus[1], _dofFocusHeld) = (null, null, false);
    }

    /// <summary>
    /// Releases the pass programs.
    /// </summary>
    void DisposeDepthOfField()
    {
        var programs = new FullscreenProgram?[] { _dofPrepare, _dofTile, _dofDilate, _dofFar, _dofFarFilter, _dofNear, _dofComposite, _dofFocusPass };
        foreach (var p in programs) p?.P.Dispose();
    }

    /// <summary>
    /// The targets for a picture of this size: half size RGBA16F for linear light above 1, the near CoC's tiles, the output as the picture.
    /// </summary>
    void EnsureDepthOfField(Target2D scene)
    {
        if (_dofOutput is not null && _dofOutput.Width == scene.Width && _dofOutput.Height == scene.Height) return;

        FreeDepthOfField();
        int hw = Math.Max((scene.Width + 1) / 2, 1);
        int hh = Math.Max((scene.Height + 1) / 2, 1);
        int tw = Math.Max((hw + DofTileSize - 1) / DofTileSize, 1);
        int th = Math.Max((hh + DofTileSize - 1) / DofTileSize, 1);

        using var batch = Gpu.Uploads.Begin();
        _dofPrepared = Make(batch, hw, hh, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post dof prepared");
        _dofTiles = Make(batch, tw, th, InternalFormat.Rg16F, TextureMinFilter.Nearest, "post dof tiles");
        _dofDilated = Make(batch, tw, th, InternalFormat.Rg16F, TextureMinFilter.Nearest, "post dof dilated");
        _dofFarField = Make(batch, hw, hh, InternalFormat.Rgba16F, TextureMinFilter.Linear, "post dof far");
        _dofFarSmooth = Make(batch, hw, hh, InternalFormat.Rgba16F, TextureMinFilter.Linear, "post dof far smooth");
        _dofNearField = Make(batch, hw, hh, InternalFormat.Rgba16F, TextureMinFilter.Linear, "post dof near");
        _dofOutput = Make(batch, scene.Width, scene.Height, InternalFormat.Rgba16F, TextureMinFilter.Linear, "post dof");
        for (int i = 0; i < 2; i++) _dofFocus[i] = Make(batch, 1, 1, InternalFormat.R32F, TextureMinFilter.Nearest, "post dof focus");
    }

    /// <summary>
    /// Binds a CoC pass's inputs: the autofocus texel, the autofocus's lens, the picture and its depth, and the lens.
    /// </summary>
    void SetLens(DofLensPass p, Target2D scene, float maxCoc, Target2D focus)
    {
        Bind(p.P, p.Focus, focus);

        // The autofocus's lens in the shader: ThinLens.InfinityCoc with the focus left to the texel.
        float focal = ThinLens.FocalLength(_fovNow);
        float autoCoc = focal * focal / MathF.Max(Options.LensAperture, 0.01f) / ThinLens.SensorHeight * scene.Height * 0.25f;
        p.P.Set(p.Auto, autoCoc, focal, Options.LensAutoFocus ? 1f : 0f, MathF.Max(Options.LensSharpZone, 0));

        Bind(p.P, p.Scene, scene);
        Bind(p.P, p.Depth, _sceneDepth);
        p.P.Set(p.Planes, _planes.X, _planes.Y);
        p.P.Set(p.JitterUv, JitterPixels.X / _width, JitterPixels.Y / _height);
        float infinity = ThinLens.InfinityCoc(_fovNow, Options.LensAperture, Options.LensFocus, scene.Height);
        p.P.Set(p.Lens, infinity, Options.LensFocus, maxCoc);
    }

    /// <summary>
    /// The largest near CoC per tile, then the largest that can reach each tile.
    /// </summary>
    void DofNearTiles(float maxCoc)
    {
        var tile = _dofTile ??= new DofTilePass(Gpu);
        Bind(tile.P, tile.Prepared, _dofPrepared);
        tile.P.Set(tile.Tiles, (float)_dofTiles!.Width, (float)_dofTiles.Height);
        Draw(tile.P, _dofTiles);

        var dilate = _dofDilate ??= new DofDilatePass(Gpu);
        Bind(dilate.P, dilate.Tile, _dofTiles);
        float tileSpan = MathF.Min(_dofPrepared!.Width / (float)_dofTiles.Width, _dofPrepared.Height / (float)_dofTiles.Height);
        dilate.P.Set(dilate.Reach, maxCoc, tileSpan);
        Draw(dilate.P, _dofDilated!);
    }

    /// <summary>
    /// The far field's gather and filter, then the near field's gather.
    /// </summary>
    void DofFields()
    {
        var far = _dofFar ??= new DofFarPass(Gpu);
        Bind(far.P, far.Prepared, _dofPrepared);
        Draw(far.P, _dofFarField!);

        var smooth = _dofFarFilter ??= new DofFarFilterPass(Gpu);
        Bind(smooth.P, smooth.Far, _dofFarField);
        Draw(smooth.P, _dofFarSmooth!);

        var near = _dofNear ??= new DofNearPass(Gpu);
        Bind(near.P, near.Prepared, _dofPrepared);
        Bind(near.P, near.Dilated, _dofDilated);
        Draw(near.P, _dofNearField!);
    }

    #endregion
}
