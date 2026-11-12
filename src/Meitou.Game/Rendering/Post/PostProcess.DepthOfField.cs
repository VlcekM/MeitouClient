using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// Meitou depth of field (the <c>dof</c> switch; docs/render-post.md "Depth of field"): a thin lens's circle of confusion from the camera's
/// field of view, f-number and focus, then a
/// half-size scatter-as-gather with a near and a far field, in linear light (<see cref="CameraEffectShaders"/>). After the upscaler,
/// into its own target. No history.
/// Port of KenshiNative's <c>docs/features/dof-rewrite.md</c> without its sparkle; KenshiNative's lens is a slider's radius, this one's is the
/// optics.
/// </summary>
public sealed unsafe partial class PostProcess
{
    /// <summary>Whether the camera effects may run on this frame: a whole view of a scene, in the cinematic cameras or everywhere by the
    /// player's choice.</summary>
    bool CameraEffectsLive => _haveScene && ViewWindow.Current.IsWhole && !Options.CameraEffectsHeld
        && (Options.CinematicView || Options.CameraEffectsEverywhere);

    /// <summary>The thin lens in the camera's own terms.</summary>
    internal static class ThinLens
    {
        /// <summary>The sensor's height: a full frame's, in millimetres.</summary>
        public const float SensorHeight = 24;
        /// <summary>Millimetres in a world unit (10 units are a metre: docs/render-post.md "Fog").</summary>
        public const float UnitLength = 100;

        /// <summary>The focal length that gives a vertical field of view on the sensor, in millimetres.</summary>
        public static float FocalLength(float fieldOfView) => SensorHeight / 2 / MathF.Tan(fieldOfView / 2);

        /// <summary>
        /// The radius, in half-size pixels of a picture <paramref name="height"/> high, of the disc a point at infinity blurs to with the lens
        /// focused at <paramref name="focus"/> world
        /// units: the aperture's diameter f / N scaled by f / (s - f), over the sensor. Nearer points take (z - s) / z of it, in the shader.
        /// </summary>
        public static float InfinityCoc(float fieldOfView, float aperture, float focus, int height)
        {
            float f = FocalLength(fieldOfView), s = MathF.Max(focus * UnitLength, f + 1);
            float diameter = f * f / (MathF.Max(aperture, 0.01f) * (s - f));
            return diameter / SensorHeight * height * 0.25f;
        }
    }

    /// <summary>What the two passes that compute a CoC share: the depth, its planes and jitter, the lens.</summary>
    abstract class DofLensPass : FullscreenProgram
    {
        public readonly SamplerSlot Scene, Depth;
        public readonly UniformHandle Planes, JitterUv, Lens;
        protected DofLensPass(GpuContext gpu, string fragment, string name) : base(gpu, fragment, name)
        {
            (Scene, Depth) = (P.Sampler("uScene"), P.Sampler("uDepth"));
            (Planes, JitterUv, Lens) = (P.Uniform("uPlanes"), P.Uniform("uJitterUv"), P.Uniform("uLens"));
        }
    }

    sealed class DofPreparePass(GpuContext gpu) : DofLensPass(gpu, CameraEffectShaders.DofPrepare, "post dof prepare");

    sealed class DofCompositePass : DofLensPass
    {
        public readonly SamplerSlot FarField, NearField;
        public DofCompositePass(GpuContext gpu) : base(gpu, CameraEffectShaders.DofComposite, "post dof") =>
            (FarField, NearField) = (P.Sampler("uFarField"), P.Sampler("uNearField"));
    }

    sealed class DofTilePass : FullscreenProgram
    {
        public readonly SamplerSlot Prepared;
        public readonly UniformHandle Tiles;
        public DofTilePass(GpuContext gpu) : base(gpu, CameraEffectShaders.DofTile, "post dof tile") =>
            (Prepared, Tiles) = (P.Sampler("uPrepared"), P.Uniform("uTiles"));
    }

    sealed class DofDilatePass : FullscreenProgram
    {
        public readonly SamplerSlot Tile;
        public readonly UniformHandle Reach;
        public DofDilatePass(GpuContext gpu) : base(gpu, CameraEffectShaders.DofDilate, "post dof dilate") =>
            (Tile, Reach) = (P.Sampler("uTile"), P.Uniform("uReach"));
    }

    sealed class DofFarPass : FullscreenProgram
    {
        public readonly SamplerSlot Prepared;
        public DofFarPass(GpuContext gpu) : base(gpu, CameraEffectShaders.DofFar, "post dof far") => Prepared = P.Sampler("uPrepared");
    }

    sealed class DofNearPass : FullscreenProgram
    {
        public readonly SamplerSlot Prepared, Dilated;
        public DofNearPass(GpuContext gpu) : base(gpu, CameraEffectShaders.DofNear, "post dof near") =>
            (Prepared, Dilated) = (P.Sampler("uPrepared"), P.Sampler("uDilated"));
    }

    /// <summary>Half-size pixels to a tile.</summary>
    const int DofTileSize = 8;

    DofPreparePass? _dofPrepare;   // made the first time the depth of field runs
    DofTilePass? _dofTile;
    DofDilatePass? _dofDilate;
    DofFarPass? _dofFar;
    DofNearPass? _dofNear;
    DofCompositePass? _dofComposite;
    Target2D? _dofPrepared, _dofTiles, _dofDilated, _dofFarField, _dofNearField, _dofOutput;

    void FreeDepthOfField()
    {
        // Released after the frames in flight.
        foreach (var t in (ReadOnlySpan<Target2D?>)[_dofPrepared, _dofTiles, _dofDilated, _dofFarField, _dofNearField, _dofOutput]) t?.Texture.Dispose();
        _dofPrepared = _dofTiles = _dofDilated = _dofFarField = _dofNearField = _dofOutput = null;
    }

    /// <summary>The targets for a picture of this size: half size RGBA16F for linear light above 1, the near CoC's tiles, the output as the
    /// picture.</summary>
    void EnsureDepthOfField(Target2D scene)
    {
        if (_dofOutput is not null && _dofOutput.Width == scene.Width && _dofOutput.Height == scene.Height) return;
        FreeDepthOfField();
        int hw = Math.Max((scene.Width + 1) / 2, 1), hh = Math.Max((scene.Height + 1) / 2, 1);
        int tw = Math.Max((hw + DofTileSize - 1) / DofTileSize, 1), th = Math.Max((hh + DofTileSize - 1) / DofTileSize, 1);
        using var batch = Gpu.Uploads.Begin();
        _dofPrepared = Make(batch, hw, hh, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post dof prepared");
        _dofTiles = Make(batch, tw, th, InternalFormat.Rg16F, TextureMinFilter.Nearest, "post dof tiles");
        _dofDilated = Make(batch, tw, th, InternalFormat.Rg16F, TextureMinFilter.Nearest, "post dof dilated");
        _dofFarField = Make(batch, hw, hh, InternalFormat.Rgba16F, TextureMinFilter.Linear, "post dof far");
        _dofNearField = Make(batch, hw, hh, InternalFormat.Rgba16F, TextureMinFilter.Linear, "post dof near");
        _dofOutput = Make(batch, scene.Width, scene.Height, InternalFormat.Rgba16F, TextureMinFilter.Linear, "post dof");
    }

    void SetLens(DofLensPass p, Target2D scene, float maxCoc)
    {
        Bind(p.P, p.Scene, scene);
        Bind(p.P, p.Depth, _sceneDepth);
        p.P.Set(p.Planes, _planes.X, _planes.Y);
        p.P.Set(p.JitterUv, JitterPixels.X / _width, JitterPixels.Y / _height);
        float infinity = ThinLens.InfinityCoc(_fovNow, Options.LensAperture, Options.LensFocus, scene.Height);
        p.P.Set(p.Lens, infinity, Options.LensFocus, maxCoc);
    }

    /// <summary>The largest near CoC per tile, then the largest that can reach each tile.</summary>
    void DofNearTiles(float maxCoc)
    {
        var tile = _dofTile ??= new DofTilePass(Gpu);
        Bind(tile.P, tile.Prepared, _dofPrepared);
        tile.P.Set(tile.Tiles, (float)_dofTiles!.Width, (float)_dofTiles.Height);
        Draw(tile.P, _dofTiles);

        var dilate = _dofDilate ??= new DofDilatePass(Gpu);
        Bind(dilate.P, dilate.Tile, _dofTiles);
        dilate.P.Set(dilate.Reach, maxCoc, MathF.Min(_dofPrepared!.Width / (float)_dofTiles.Width, _dofPrepared.Height / (float)_dofTiles.Height));
        Draw(dilate.P, _dofDilated!);
    }

    void DofFields()
    {
        var far = _dofFar ??= new DofFarPass(Gpu);
        Bind(far.P, far.Prepared, _dofPrepared);
        Draw(far.P, _dofFarField!);

        var near = _dofNear ??= new DofNearPass(Gpu);
        Bind(near.P, near.Prepared, _dofPrepared);
        Bind(near.P, near.Dilated, _dofDilated);
        Draw(near.P, _dofNearField!);
    }

    /// <summary>The picture through the lens, or <paramref name="scene"/> when the depth of field does not run (off, another camera).</summary>
    Target2D RunDepthOfField(Target2D scene)
    {
        if (!Options.DepthOfField || !CameraEffectsLive) return scene;
        EnsureDepthOfField(scene);
        float maxCoc = MathF.Min(MathF.Max(scene.Height * PostOptions.DofMaxCocShare, 2), PostOptions.DofMaxCocPixels);

        var prepare = _dofPrepare ??= new DofPreparePass(Gpu);
        SetLens(prepare, scene, maxCoc);
        Draw(prepare.P, _dofPrepared!);
        DofNearTiles(maxCoc);
        DofFields();

        var composite = _dofComposite ??= new DofCompositePass(Gpu);
        SetLens(composite, scene, maxCoc);
        Bind(composite.P, composite.FarField, _dofFarField);
        Bind(composite.P, composite.NearField, _dofNearField);
        Draw(composite.P, _dofOutput!);
        Stamp("dof");
        return _dofOutput!;
    }

    void DisposeDepthOfField()
    {
        foreach (var p in new FullscreenProgram?[] { _dofPrepare, _dofTile, _dofDilate, _dofFar, _dofNear, _dofComposite }) p?.P.Dispose();
    }
}
