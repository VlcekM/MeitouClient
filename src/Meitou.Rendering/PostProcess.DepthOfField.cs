using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// Meitou depth of field (the <c>dof</c> switch; docs/render-post.md "Depth of field"): a thin lens's circle of confusion from the camera's
/// field of view, f-number and focus, then a
/// half-size scatter-as-gather with a near and a far field, in linear light (<see cref="CameraEffectShaders"/>). After the upscaler and the
/// motion blur, into its own target. No history.
/// Port of KenshiNative's <c>docs/features/dof-rewrite.md</c> without its sparkle; KenshiNative's lens is a slider's radius, this one's is the
/// optics.
/// </summary>
public sealed unsafe partial class PostProcess
{
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

    DofPreparePass? dofPrepare;   // made the first time the depth of field runs
    DofTilePass? dofTile;
    DofDilatePass? dofDilate;
    DofFarPass? dofFar;
    DofNearPass? dofNear;
    DofCompositePass? dofComposite;
    Target2D? dofPrepared, dofTiles, dofDilated, dofFarField, dofNearField, dofOutput;

    void FreeDepthOfField()
    {
        // Released after the frames in flight.
        foreach (var t in (ReadOnlySpan<Target2D?>)[dofPrepared, dofTiles, dofDilated, dofFarField, dofNearField, dofOutput]) t?.Texture.Dispose();
        dofPrepared = dofTiles = dofDilated = dofFarField = dofNearField = dofOutput = null;
    }

    /// <summary>The targets for a picture of this size: half size RGBA16F for linear light above 1, the near CoC's tiles, the output as the
    /// picture.</summary>
    void EnsureDepthOfField(Target2D scene)
    {
        if (dofOutput is not null && dofOutput.Width == scene.Width && dofOutput.Height == scene.Height) return;
        FreeDepthOfField();
        int hw = Math.Max((scene.Width + 1) / 2, 1), hh = Math.Max((scene.Height + 1) / 2, 1);
        int tw = Math.Max((hw + DofTileSize - 1) / DofTileSize, 1), th = Math.Max((hh + DofTileSize - 1) / DofTileSize, 1);
        using var batch = Gpu.Uploads.Begin();
        dofPrepared = Make(batch, hw, hh, InternalFormat.Rgba16f, TextureMinFilter.Nearest, "post dof prepared");
        dofTiles = Make(batch, tw, th, InternalFormat.RG16f, TextureMinFilter.Nearest, "post dof tiles");
        dofDilated = Make(batch, tw, th, InternalFormat.RG16f, TextureMinFilter.Nearest, "post dof dilated");
        dofFarField = Make(batch, hw, hh, InternalFormat.Rgba16f, TextureMinFilter.Linear, "post dof far");
        dofNearField = Make(batch, hw, hh, InternalFormat.Rgba16f, TextureMinFilter.Linear, "post dof near");
        dofOutput = Make(batch, scene.Width, scene.Height, InternalFormat.Rgba16f, TextureMinFilter.Linear, "post dof");
    }

    void SetLens(DofLensPass p, Target2D scene, float maxCoc)
    {
        Bind(p.P, p.Scene, scene);
        Bind(p.P, p.Depth, sceneDepth);
        p.P.Set(p.Planes, planes.X, planes.Y);
        p.P.Set(p.JitterUv, JitterPixels.X / width, JitterPixels.Y / height);
        float infinity = ThinLens.InfinityCoc(fovNow, Options.LensAperture, Options.LensFocus, scene.Height);
        p.P.Set(p.Lens, infinity, Options.LensFocus, maxCoc);
    }

    /// <summary>The largest near CoC per tile, then the largest that can reach each tile.</summary>
    void DofNearTiles(float maxCoc)
    {
        var tile = dofTile ??= new DofTilePass(Gpu);
        Bind(tile.P, tile.Prepared, dofPrepared);
        tile.P.Set(tile.Tiles, (float)dofTiles!.Width, (float)dofTiles.Height);
        Draw(tile.P, dofTiles);

        var dilate = dofDilate ??= new DofDilatePass(Gpu);
        Bind(dilate.P, dilate.Tile, dofTiles);
        dilate.P.Set(dilate.Reach, maxCoc, MathF.Min(dofPrepared!.Width / (float)dofTiles.Width, dofPrepared.Height / (float)dofTiles.Height));
        Draw(dilate.P, dofDilated!);
    }

    void DofFields()
    {
        var far = dofFar ??= new DofFarPass(Gpu);
        Bind(far.P, far.Prepared, dofPrepared);
        Draw(far.P, dofFarField!);

        var near = dofNear ??= new DofNearPass(Gpu);
        Bind(near.P, near.Prepared, dofPrepared);
        Bind(near.P, near.Dilated, dofDilated);
        Draw(near.P, dofNearField!);
    }

    /// <summary>The picture through the lens, or <paramref name="scene"/> when the depth of field does not run (off, another camera).</summary>
    Target2D RunDepthOfField(Target2D scene)
    {
        if (!Options.DepthOfField || !CameraEffectsLive) return scene;
        EnsureDepthOfField(scene);
        float maxCoc = MathF.Min(MathF.Max(scene.Height * PostOptions.DofMaxCocShare, 2), PostOptions.DofMaxCocPixels);

        var prepare = dofPrepare ??= new DofPreparePass(Gpu);
        SetLens(prepare, scene, maxCoc);
        Draw(prepare.P, dofPrepared!);
        DofNearTiles(maxCoc);
        DofFields();

        var composite = dofComposite ??= new DofCompositePass(Gpu);
        SetLens(composite, scene, maxCoc);
        Bind(composite.P, composite.FarField, dofFarField);
        Bind(composite.P, composite.NearField, dofNearField);
        Draw(composite.P, dofOutput!);
        Stamp("dof");
        return dofOutput!;
    }

    void DisposeDepthOfField()
    {
        foreach (var p in new FullscreenProgram?[] { dofPrepare, dofTile, dofDilate, dofFar, dofNear, dofComposite }) p?.P.Dispose();
    }
}
