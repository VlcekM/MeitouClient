using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// Meitou motion blur (the <c>motionblur</c> switch; docs/render-post.md "Motion blur"): a shutter's blur rebuilt from the motion target, tile
/// maximum, neighbour maximum, then
/// reconstruction (<see cref="CameraEffectShaders"/>). After the temporal resolve, into its own target (the resolve's output is the next
/// frame's history), before the exposure.
/// Without a temporal upscaler there is no motion target and it does not run.
/// </summary>
public sealed unsafe partial class PostProcess
{
    sealed class MotionBlurPreparePass : FullscreenProgram
    {
        public readonly SamplerSlot Motion, Depth;
        public readonly UniformHandle Planes, Pixels, MaxRadius;
        public MotionBlurPreparePass(GpuContext gpu) : base(gpu, CameraEffectShaders.MotionBlurPrepare, "post motion blur prepare")
        {
            (Motion, Depth) = (P.Sampler("uMotion"), P.Sampler("uDepth"));
            (Planes, Pixels, MaxRadius) = (P.Uniform("uPlanes"), P.Uniform("uPixels"), P.Uniform("uMaxRadius"));
        }
    }

    sealed class MotionBlurTilePass : FullscreenProgram
    {
        public readonly SamplerSlot Source;
        public readonly UniformHandle Tiles;
        public MotionBlurTilePass(GpuContext gpu) : base(gpu, CameraEffectShaders.MotionBlurTile, "post motion blur tile") =>
            (Source, Tiles) = (P.Sampler("uSource"), P.Uniform("uTiles"));
    }

    sealed class MotionBlurNeighbourPass : FullscreenProgram
    {
        public readonly SamplerSlot Tile;
        public MotionBlurNeighbourPass(GpuContext gpu) : base(gpu, CameraEffectShaders.MotionBlurNeighbour, "post motion blur neighbour") =>
            Tile = P.Sampler("uTile");
    }

    sealed class MotionBlurReconstructPass : FullscreenProgram
    {
        public readonly SamplerSlot Scene, Prepare, Neighbour;
        public MotionBlurReconstructPass(GpuContext gpu) : base(gpu, CameraEffectShaders.MotionBlurReconstruct, "post motion blur") =>
            (Scene, Prepare, Neighbour) = (P.Sampler("uScene"), P.Sampler("uPrepare"), P.Sampler("uNeighbour"));
    }

    /// <summary>The longest blur a frame may hold over its motion, whatever the shutter over a short frame asks.</summary>
    const float MaxShutterFrames = 8;

    MotionBlurPreparePass? motionBlurPrepare;   // made the first time the blur runs
    MotionBlurTilePass? motionBlurTile;
    MotionBlurNeighbourPass? motionBlurNeighbour;
    MotionBlurReconstructPass? motionBlurReconstruct;
    Target2D? blurVectors, blurTiles, blurNeighbours, blurOutput;
    /// <summary>This frame's motion target measures one real frame (no reset or first frame), and the seconds that frame took.</summary>
    bool motionFresh;
    float motionSeconds;

    /// <summary>Whether the camera effects may run on this frame: a whole view of a scene, in the cinematic cameras or everywhere by the
    /// player's choice.</summary>
    bool CameraEffectsLive => haveScene && ViewWindow.Current.IsWhole && !Options.CameraEffectsHeld
        && (Options.CinematicView || Options.CameraEffectsEverywhere);

    void FreeMotionBlur()
    {
        // Released after the frames in flight.
        foreach (var t in (ReadOnlySpan<Target2D?>)[blurVectors, blurTiles, blurNeighbours, blurOutput]) t?.Texture.Dispose();
        blurVectors = blurTiles = blurNeighbours = blurOutput = null;
    }

    /// <summary>The blur's targets for a picture of this size: the vectors at the render size, the tiles a blur radius wide.</summary>
    void EnsureMotionBlur(Target2D scene, int radius)
    {
        if (blurOutput is not null && blurOutput.Width == scene.Width && blurOutput.Height == scene.Height) return;
        FreeMotionBlur();
        int tilesX = Math.Max((scene.Width + radius - 1) / radius, 1), tilesY = Math.Max((scene.Height + radius - 1) / radius, 1);
        using var batch = Gpu.Uploads.Begin();
        blurVectors = Make(batch, width, height, InternalFormat.Rgba16f, TextureMinFilter.Nearest, "post motion blur vectors");
        blurTiles = Make(batch, tilesX, tilesY, InternalFormat.RG16f, TextureMinFilter.Nearest, "post motion blur tiles");
        blurNeighbours = Make(batch, tilesX, tilesY, InternalFormat.RG16f, TextureMinFilter.Nearest, "post motion blur neighbours");
        blurOutput = Make(batch, scene.Width, scene.Height, InternalFormat.Rgba16f, TextureMinFilter.Linear, "post motion blur");
    }

    /// <summary>How many frames of motion the shutter stays open for: its seconds over this frame's.</summary>
    float ShutterFrames()
    {
        float frame = Options.FrameSeconds > 0 ? Options.FrameSeconds : motionSeconds;
        return Math.Clamp(Options.ShutterSeconds / MathF.Max(frame, 1e-4f), 0, MaxShutterFrames);
    }

    /// <summary>The blurred picture, or <paramref name="scene"/> when the blur does not run (off, another camera, no motion this frame, a
    /// cut).</summary>
    Target2D RunMotionBlur(Target2D scene)
    {
        bool cut = Options.CameraCut;
        Options.CameraCut = false;
        if (!Options.MotionBlur || !CameraEffectsLive || motion is null || !motionFresh || cut) return scene;
        int radius = Math.Max((int)MathF.Round(scene.Height * PostOptions.MotionBlurMaxRadius), 4);
        EnsureMotionBlur(scene, radius);

        var prepare = motionBlurPrepare ??= new MotionBlurPreparePass(Gpu);
        Bind(prepare.P, prepare.Motion, motion);
        Bind(prepare.P, prepare.Depth, sceneDepth);
        prepare.P.Set(prepare.Planes, planes.X, planes.Y);
        float frames = ShutterFrames();
        prepare.P.Set(prepare.Pixels, scene.Width * frames, scene.Height * frames);
        prepare.P.Set(prepare.MaxRadius, (float)radius);
        Draw(prepare.P, blurVectors!);

        var tile = motionBlurTile ??= new MotionBlurTilePass(Gpu);
        Bind(tile.P, tile.Source, blurVectors);
        tile.P.Set(tile.Tiles, (float)blurTiles!.Width, (float)blurTiles.Height);
        Draw(tile.P, blurTiles);

        var neighbour = motionBlurNeighbour ??= new MotionBlurNeighbourPass(Gpu);
        Bind(neighbour.P, neighbour.Tile, blurTiles);
        Draw(neighbour.P, blurNeighbours!);

        var reconstruct = motionBlurReconstruct ??= new MotionBlurReconstructPass(Gpu);
        Bind(reconstruct.P, reconstruct.Scene, scene);
        Bind(reconstruct.P, reconstruct.Prepare, blurVectors);
        Bind(reconstruct.P, reconstruct.Neighbour, blurNeighbours);
        Draw(reconstruct.P, blurOutput!);
        Stamp("motion blur");
        return blurOutput!;
    }

    void DisposeMotionBlur()
    {
        motionBlurPrepare?.P.Dispose();
        motionBlurTile?.P.Dispose();
        motionBlurNeighbour?.P.Dispose();
        motionBlurReconstruct?.P.Dispose();
    }
}
