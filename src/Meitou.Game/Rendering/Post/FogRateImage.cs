using System.Numerics;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Sky;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Target2D = Meitou.Game.Rendering.Post.PostProcess.Target2D;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The fog shading rate (<c>fog-vrs</c>; <see cref="FogShadingRate"/>, docs/render-post.md "Fog shading rate"): after each frame's scene, two small
/// passes turn its depth into an R8_UINT image with one fragment size per tile of pixels, where the placed fog volumes, the haze and the weather's
/// fog hide the surface; the next frame's opaque scene passes (sky, terrain, objects, foliage, water) take it as their fragment shading rate
/// attachment. <see cref="PostProcess"/> owns the scene's targets and the camera; this keeps the rate images, their passes and the tile count.
/// </summary>
sealed unsafe class FogRateImage(PostProcess post)
{
    #region Passes

    /// <summary>
    /// The nearest distance of each block of 4 x 4 pixels (<see cref="PostProcessShaders.FogRateDistance"/>).
    /// </summary>
    sealed class FogRateDistancePass : PostProcess.FullscreenProgram
    {
        /// <summary>
        /// The scene's depth.
        /// </summary>
        public readonly SamplerSlot NearDepth;

        /// <summary>
        /// The depth planes, the view's tangents and axes, the eye, the render size and the water height.
        /// </summary>
        public readonly UniformHandle NearPlanes, Tan, Right, Up, Back, Eye, Size, WaterY;

        /// <summary>
        /// Compiles the program and resolves its handles.
        /// </summary>
        public FogRateDistancePass(GpuContext gpu) : base(gpu, PostProcessShaders.FogRateDistance, "post fog rate distance")
        {
            NearDepth = P.Sampler("uNearDepth");
            (NearPlanes, Tan, Right, Up, Back) = (P.Uniform("uNearPlanes"), P.Uniform("uTan"), P.Uniform("uRight"), P.Uniform("uUp"),
                P.Uniform("uBack"));
            (Eye, Size, WaterY) = (P.Uniform("uEye"), P.Uniform("uSize"), P.Uniform("uWaterY"));
        }
    }

    /// <summary>
    /// The fragment size code of each shading rate tile (<see cref="PostProcessShaders.FogRate"/>).
    /// </summary>
    sealed class FogRatePass : PostProcess.FullscreenProgram
    {
        /// <summary>
        /// The blocks' nearest distances.
        /// </summary>
        public readonly SamplerSlot Distance;

        /// <summary>
        /// The view's tangents and axes, the render size, the tile and rate limits and the opacity thresholds.
        /// </summary>
        public readonly UniformHandle Tan, Right, Up, Back, Size, Rate, Threshold;

        /// <summary>
        /// Compiles the program and resolves its handles.
        /// </summary>
        public FogRatePass(GpuContext gpu) : base(gpu, PostProcessShaders.FogRate, "post fog rate")
        {
            Distance = P.Sampler("uDistance");
            (Tan, Right, Up, Back) = (P.Uniform("uTan"), P.Uniform("uRight"), P.Uniform("uUp"), P.Uniform("uBack"));
            (Size, Rate, Threshold) = (P.Uniform("uSize"), P.Uniform("uRate"), P.Uniform("uThreshold"));
        }
    }

    #endregion

    #region Rate image

    /// <summary>
    /// The distance pass, made with the first build.
    /// </summary>
    FogRateDistancePass? _rateDistancePass;

    /// <summary>
    /// The rate pass, made with the first build.
    /// </summary>
    FogRatePass? _ratePass;

    /// <summary>
    /// The blocks' nearest distances and the rate image (null before the first build or after <see cref="Free"/>).
    /// </summary>
    Target2D? _rateDistance, _rateImage;

    /// <summary>
    /// The frame that built <see cref="_rateImage"/> (<see cref="long.MinValue"/>: none fit for the next frame).
    /// </summary>
    long _rateBuiltFrame = long.MinValue;

    /// <summary>
    /// The eye and the view's back axis the rate image was built with.
    /// </summary>
    Vector3 _rateEye, _rateBack;

    /// <summary>
    /// The neutral image (every tile 1 x 1) of the size last asked for.
    /// </summary>
    Target2D? _neutralRate;

    /// <summary>
    /// The user's switch for the fog shading rate (default off). It does nothing where the device lacks VK_KHR_fragment_shading_rate
    /// (<see cref="Supported"/>).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The device can shade several pixels with one fragment from an attachment image.
    /// </summary>
    public bool Supported => post.Gpu.Device.HasFragmentShadingRate;

    /// <summary>
    /// The blocks' nearest distances (the post chain's target list).
    /// </summary>
    public Target2D? Distance => _rateDistance;

    /// <summary>
    /// The rate image (the post chain's target list).
    /// </summary>
    public Target2D? Image => _rateImage;

    /// <summary>
    /// The neutral image (the post chain's target list).
    /// </summary>
    public Target2D? Neutral => _neutralRate;

    /// <summary>
    /// <paramref name="targets"/> with the fog shading rate image as their fragment shading rate attachment, when there is one fit for this frame:
    /// built by the previous frame, at this size, with the camera no more than a little moved (a cut or a fast turn shows none until the next build).
    /// Else, with the switch on, a neutral image (every tile 1 x 1): the frame shades as without one, and draws with the same pipelines as the
    /// others. Without any attachment it needed a second pipeline of everything it drew, made at the first cut in play: a stall of seconds with a
    /// cold driver cache (docs/bench.md "Play benchmark"). With the switch off, <paramref name="targets"/> as they are.
    /// </summary>
    public PassTargets WithShadingRate(PassTargets targets)
    {
        if (!Enabled || !Supported) return targets;
        int texel = post.Gpu.Device.ShadingRateTexel;
        int tw = FogShadingRate.Tiles(post.RenderWidth, texel), th = FogShadingRate.Tiles(post.RenderHeight, texel);
        bool fit = _rateImage is not null && _rateBuiltFrame == post.Gpu.Frame.Number - 1 && _rateImage.Width == tw && _rateImage.Height == th
            && Vector3.DistanceSquared(post.EyeNow, _rateEye) <= 500f * 500f && Vector3.Dot(BackOf(post.ViewRotation), _rateBack) >= 0.97f;
        return targets.WithShadingRate((fit ? _rateImage! : NeutralRate(tw, th)).Texture.View(), texel);
    }

    /// <summary>
    /// Builds the fog shading rate for the next frame from this frame's depth (call after the scene's passes, with <paramref name="eye"/> the
    /// camera's position): the nearest distance per 4 x 4 pixels, then per tile the opacity of the air to the nearest surface within it and a margin,
    /// and its rate. Does nothing with the switch off.
    /// </summary>
    public void Build(Vector3 eye)
    {
        if (!Enabled || !Supported || post.SceneColourTarget is null || !post.HaveScene)
        {
            _rateBuiltFrame = long.MinValue;
            return;
        }
        var device = post.Gpu.Device;
        int texel = device.ShadingRateTexel;
        EnsureImages(texel);
        _rateDistancePass ??= new FogRateDistancePass(post.Gpu);
        _ratePass ??= new FogRatePass(post.Gpu);
        if (Stats) CollectRateStats();

        float tanY = MathF.Tan(post.FovNow * 0.5f);
        var tan = ViewWindow.Tan(tanY * post.AspectNow, tanY);
        var r = post.ViewRotation;
        var size = new Vector2(post.RenderWidth, post.RenderHeight);
        DrawDistance(eye, tan, r, size);
        DrawRate(tan, r, size, texel, device.MaxShadingRate);
        if (Stats) QueueRateReadback();
        post.Stamp("fog rate");
        post.CloseSegment();
        (_rateBuiltFrame, _rateEye, _rateBack) = (post.Gpu.Frame.Number, post.EyeNow, BackOf(post.ViewRotation));
    }

    /// <summary>
    /// Forgets the targets the post chain has just released (its <c>Free</c> disposes them through the target list) and the built frame.
    /// </summary>
    public void Free()
    {
        _rateDistance = _rateImage = _neutralRate = null;
        _rateBuiltFrame = long.MinValue;
    }

    /// <summary>
    /// The view's back axis from its rotation.
    /// </summary>
    static Vector3 BackOf(Matrix4x4 rotation) => new(rotation.M13, rotation.M23, rotation.M33);

    /// <summary>
    /// An R8_UINT image of <paramref name="tiles"/> x <paramref name="rows"/> zeros: the rate code of 1 x 1 fragments in every tile.
    /// </summary>
    Target2D NeutralRate(int tiles, int rows)
    {
        if (_neutralRate is { } made && made.Width == tiles && made.Height == rows) return made;
        _neutralRate?.Texture.Dispose();   // released after the frames in flight
        using var batch = post.Gpu.Uploads.Begin();
        var texture = batch.Create(new TextureDesc(InternalFormat.R8Ui, tiles, rows,
            Use: TextureUse.Sampled | TextureUse.ShadingRate | TextureUse.TransferDst, Name: "post fog rate neutral"));
        batch.Write(texture, 0, 0, new PixelRect(0, 0, (uint)tiles, (uint)rows), new byte[tiles * rows]);
        return _neutralRate = new Target2D(texture, TextureMinFilter.Nearest, TextureMagFilter.Nearest);
    }

    /// <summary>
    /// The distance target and the rate image at this render size and tile (made again when either changed).
    /// </summary>
    void EnsureImages(int texel)
    {
        int width = post.RenderWidth, height = post.RenderHeight;
        int tw = FogShadingRate.Tiles(width, texel), th = FogShadingRate.Tiles(height, texel);
        int dw = FogShadingRate.Tiles(width, 4), dh = FogShadingRate.Tiles(height, 4);
        if (_rateImage is null || _rateImage.Width != tw || _rateImage.Height != th
            || _rateDistance is null || _rateDistance.Width != dw || _rateDistance.Height != dh)
        {
            foreach (var t in new[] { _rateDistance, _rateImage }.OfType<Target2D>()) t.Texture.Dispose();   // released after the frames in flight
            using var batch = post.Gpu.Uploads.Begin();
            _rateDistance = post.Make(batch, dw, dh, InternalFormat.Rg32F, TextureMinFilter.Nearest, "post fog rate distance");
            var texture = batch.Create(new TextureDesc(InternalFormat.R8Ui, tw, th,
                Use: TextureUse.Sampled | TextureUse.ColourTarget | TextureUse.ShadingRate | TextureUse.TransferSrc, Name: "post fog rate"));
            _rateImage = new Target2D(texture, TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        }
    }

    /// <summary>
    /// The nearest distance per 4 x 4 pixels into the distance target.
    /// </summary>
    void DrawDistance(Vector3 eye, Vector2 tan, Matrix4x4 r, Vector2 size)
    {
        var d = _rateDistancePass!;
        var planes = post.Planes;
        post.Bind(d.P, d.NearDepth, post.SceneDepthTarget);
        d.P.Set(d.NearPlanes, planes.X, planes.Y);
        d.P.Set(d.Tan, tan);
        d.P.Set(d.Right, r.M11, r.M21, r.M31);
        d.P.Set(d.Up, r.M12, r.M22, r.M32);
        d.P.Set(d.Back, ViewWindow.BackFor(r, post.FovNow, post.AspectNow));
        d.P.Set(d.Eye, eye);
        d.P.Set(d.Size, size);
        d.P.Set(d.WaterY, post.WaterHeight ?? float.MinValue);
        post.Draw(d.P, _rateDistance!);
    }

    /// <summary>
    /// Per tile the opacity of the air to its nearest surface, and its rate, into the rate image.
    /// </summary>
    void DrawRate(Vector2 tan, Matrix4x4 r, Vector2 size, int texel, int maxRate)
    {
        var p = _ratePass!;
        post.Bind(p.P, p.Distance, _rateDistance);
        p.P.Set(p.Tan, tan);
        p.P.Set(p.Right, r.M11, r.M21, r.M31);
        p.P.Set(p.Up, r.M12, r.M22, r.M32);
        p.P.Set(p.Back, ViewWindow.BackFor(r, post.FovNow, post.AspectNow));
        p.P.Set(p.Size, size);
        p.P.Set(p.Rate, texel, FogShadingRate.MarginBlocks, maxRate, 0);
        var thresholds = FogShadingRate.Thresholds;
        p.P.Set(p.Threshold, thresholds.TwoByTwo, thresholds.FourByFour);
        p.P.ApplyGlobals();   // the atmosphere's and the fog volumes' uniforms, through the frame globals
        post.Draw(p.P, _rateImage!);
    }

    #endregion

    #region Statistics

    /// <summary>
    /// The readbacks of the rate image in flight and the frame each was recorded in (0: read).
    /// </summary>
    readonly (ReadbackBuffer? Buffer, long Frame)[] _rateReadbacks = new (ReadbackBuffer?, long)[4];

    /// <summary>
    /// The readback slot the next copy goes into.
    /// </summary>
    int _rateReadbackNext;

    /// <summary>
    /// The tiles counted at 1 x 1, 2 x 2 and 4 x 4.
    /// </summary>
    readonly long[] _rateTiles = new long[3];

    /// <summary>
    /// The tiles counted in all, and the frames.
    /// </summary>
    long _rateTotal, _rateFrames;

    /// <summary>
    /// Counting the tiles of each rate (the bench's report): a readback of the rate image every frame.
    /// </summary>
    public bool Stats { get; set; }

    /// <summary>
    /// The share of the screen's tiles shaded 1 x 1, 2 x 2 and 4 x 4 over the frames counted (<see cref="Stats"/>), and how many frames were
    /// counted.
    /// </summary>
    public (double One, double Two, double Four, long Frames) Share =>
        _rateFrames == 0 ? default : (_rateTiles[0] / (double)_rateTotal, _rateTiles[1] / (double)_rateTotal, _rateTiles[2] / (double)_rateTotal,
                rateFrames: _rateFrames);

    /// <summary>
    /// Starts the count of <see cref="Share"/> afresh.
    /// </summary>
    public void ResetStats()
    {
        Array.Clear(_rateTiles);
        _rateTotal = _rateFrames = 0;
    }

    /// <summary>
    /// Copies the rate image into the next readback slot (in the post segment).
    /// </summary>
    void QueueRateReadback()
    {
        var cmd = post.Segment();
        var image = _rateImage!;
        var gpu = post.Gpu;
        ref var slot = ref _rateReadbacks[_rateReadbackNext];
        slot.Buffer ??= ReadbackBuffer.Create(gpu, (ulong)image.Width * (ulong)image.Height, "fog rate readback");
        var region = new Vk.BufferImageCopy
        {
            ImageSubresource = new Vk.ImageSubresourceLayers(Vk.ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Vk.Extent3D((uint)image.Width, (uint)image.Height, 1),
        };
        gpu.Device.Vk.CmdCopyImageToBuffer(((CommandList)cmd).Handle, gpu.Device.NativeImage(image.Texture), Vk.ImageLayout.General,
            gpu.Device.BufferAt(slot.Buffer.Handle.Index), 1, &region);
        cmd.Order();
        slot.Frame = gpu.Frame.Number;
        _rateReadbackNext = (_rateReadbackNext + 1) % _rateReadbacks.Length;
    }

    /// <summary>
    /// Tallies the rate images whose frames have completed.
    /// </summary>
    void CollectRateStats()
    {
        if (_rateImage is null) return;
        long tiles = (long)_rateImage.Width * _rateImage.Height;
        for (int i = 0; i < _rateReadbacks.Length; i++)
        {
            ref var slot = ref _rateReadbacks[i];
            if (slot.Buffer is null || slot.Frame == 0 || !ReadbackBuffer.Completed(post.Gpu, slot.Frame) || slot.Buffer.Size < (ulong)tiles) continue;
            foreach (byte code in slot.Buffer.Read(0, (ulong)tiles))
            {
                int s = FogShadingRate.SizeOf(code);
                _rateTiles[s >= 4 ? 2 : s >= 2 ? 1 : 0]++;
            }
            _rateTotal += tiles;
            _rateFrames++;
            slot.Frame = 0;
        }
    }

    #endregion

    #region Lifetime

    /// <summary>
    /// Disposes the passes and the readbacks (the images go with the post chain's target list).
    /// </summary>
    public void Dispose()
    {
        _rateDistancePass?.P.Dispose();
        _ratePass?.P.Dispose();
        foreach (var slot in _rateReadbacks) slot.Buffer?.Dispose();
    }

    #endregion
}
