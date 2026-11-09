using System.Numerics;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Sky;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The fog shading rate (<c>fog-vrs</c>; <see cref="FogShadingRate"/>, docs/render-post.md "Fog shading rate"): after each frame's scene, two small
/// passes turn its depth into an R8_UINT image with one fragment size per tile of pixels, where the placed fog volumes, the haze and the weather's
/// fog hide the surface; the next frame's opaque scene passes (sky, terrain, objects, foliage, water) take it as their fragment shading rate
/// attachment.
/// </summary>
public sealed unsafe partial class PostProcess
{
    /// <summary>
    /// The nearest distance of each block of 4 x 4 pixels (<see cref="PostProcessShaders.FogRateDistance"/>).
    /// </summary>
    sealed class FogRateDistancePass : FullscreenProgram
    {
        public readonly SamplerSlot NearDepth;
        public readonly UniformHandle NearPlanes, Tan, Right, Up, Back, Eye, Size, WaterY;
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
    sealed class FogRatePass : FullscreenProgram
    {
        public readonly SamplerSlot Distance;
        public readonly UniformHandle Tan, Right, Up, Back, Size, Rate, Threshold;
        public FogRatePass(GpuContext gpu) : base(gpu, PostProcessShaders.FogRate, "post fog rate")
        {
            Distance = P.Sampler("uDistance");
            (Tan, Right, Up, Back) = (P.Uniform("uTan"), P.Uniform("uRight"), P.Uniform("uUp"), P.Uniform("uBack"));
            (Size, Rate, Threshold) = (P.Uniform("uSize"), P.Uniform("uRate"), P.Uniform("uThreshold"));
        }
    }

    FogRateDistancePass? _rateDistancePass;
    FogRatePass? _ratePass;
    Target2D? _rateDistance, _rateImage;
    long _rateBuiltFrame = long.MinValue;
    Vector3 _rateEye, _rateBack;
    readonly (ReadbackBuffer? Buffer, long Frame)[] _rateReadbacks = new (ReadbackBuffer?, long)[4];
    int _rateReadbackNext;
    readonly long[] _rateTiles = new long[3];
    long _rateTotal, _rateFrames;

    /// <summary>
    /// The user's switch for the fog shading rate (default off). It does nothing where the device lacks VK_KHR_fragment_shading_rate
    /// (<see cref="FogVrsSupported"/>).
    /// </summary>
    public bool FogVrs { get; set; }

    /// <summary>
    /// The device can shade several pixels with one fragment from an attachment image.
    /// </summary>
    public bool FogVrsSupported => Gpu.Device.HasFragmentShadingRate;

    /// <summary>
    /// Counting the tiles of each rate (the bench's report): a readback of the rate image every frame.
    /// </summary>
    public bool FogVrsStats { get; set; }

    /// <summary>
    /// The share of the screen's tiles shaded 1 x 1, 2 x 2 and 4 x 4 over the frames counted (<see cref="FogVrsStats"/>), and how many frames were
    /// counted.
    /// </summary>
    public (double One, double Two, double Four, long Frames) FogVrsShare =>
        _rateFrames == 0 ? default : (_rateTiles[0] / (double)_rateTotal, _rateTiles[1] / (double)_rateTotal, _rateTiles[2] / (double)_rateTotal,
                rateFrames: _rateFrames);

    /// <summary>
    /// Starts the count of <see cref="FogVrsShare"/> afresh.
    /// </summary>
    public void ResetFogVrsStats() { Array.Clear(_rateTiles); _rateTotal = _rateFrames = 0; }

    /// <summary>
    /// <paramref name="targets"/> with the fog shading rate image as their fragment shading rate attachment, when there is one fit for this frame:
    /// built by the previous frame, at this size, with the camera no more than a little moved (a cut or a fast turn shows none until the next build).
    /// Else, with the switch on, a neutral image (every tile 1 x 1): the frame shades as without one, and draws with the same pipelines as the
    /// others. Without any attachment it needed a second pipeline of everything it drew, made at the first cut in play: a stall of seconds with a
    /// cold driver cache (docs/bench.md "Play benchmark"). With the switch off, <paramref name="targets"/> as they are.
    /// </summary>
    public PassTargets WithShadingRate(PassTargets targets)
    {
        if (!FogVrs || !FogVrsSupported) return targets;
        int texel = Gpu.Device.ShadingRateTexel;
        int tw = FogShadingRate.Tiles(_width, texel), th = FogShadingRate.Tiles(_height, texel);
        bool fit = _rateImage is not null && _rateBuiltFrame == Gpu.Frame.Number - 1 && _rateImage.Width == tw && _rateImage.Height == th
            && Vector3.DistanceSquared(_eyeNow, _rateEye) <= 500f * 500f && Vector3.Dot(BackOf(_viewRotation), _rateBack) >= 0.97f;
        return targets.WithShadingRate((fit ? _rateImage! : NeutralRate(tw, th)).Texture.View(), texel);
    }

    Target2D? _neutralRate;

    /// <summary>
    /// An R8_UINT image of <paramref name="tiles"/> x <paramref name="rows"/> zeros: the rate code of 1 x 1 fragments in every tile.
    /// </summary>
    Target2D NeutralRate(int tiles, int rows)
    {
        if (_neutralRate is { } made && made.Width == tiles && made.Height == rows) return made;
        _neutralRate?.Texture.Dispose();   // released after the frames in flight
        using var batch = Gpu.Uploads.Begin();
        var texture = batch.Create(new TextureDesc(InternalFormat.R8Ui, tiles, rows,
                Use: TextureUse.Sampled | TextureUse.ShadingRate | TextureUse.TransferDst, Name: "post fog rate neutral"));
        batch.Write(texture, 0, 0, new PixelRect(0, 0, (uint)tiles, (uint)rows), new byte[tiles * rows]);
        return _neutralRate = new Target2D(texture, TextureMinFilter.Nearest, TextureMagFilter.Nearest);
    }

    static Vector3 BackOf(Matrix4x4 rotation) => new(rotation.M13, rotation.M23, rotation.M33);

    /// <summary>
    /// Builds the fog shading rate for the next frame from this frame's depth (call after the scene's passes, with <paramref name="eye"/> the
    /// camera's position): the nearest distance per 4 x 4 pixels, then per tile the opacity of the air to the nearest surface within it and a margin,
    /// and its rate. Does nothing with the switch off.
    /// </summary>
    public void BuildShadingRate(Vector3 eye)
    {
        if (!FogVrs || !FogVrsSupported || _sceneColour is null || !_haveScene)
        {
            _rateBuiltFrame = long.MinValue;
            return;
        }
        var device = Gpu.Device;
        int texel = device.ShadingRateTexel;
        int tw = FogShadingRate.Tiles(_width, texel), th = FogShadingRate.Tiles(_height, texel);
        int dw = FogShadingRate.Tiles(_width, 4), dh = FogShadingRate.Tiles(_height, 4);
        if (_rateImage is null || _rateImage.Width != tw || _rateImage.Height != th || _rateDistance is null || _rateDistance.Width != dw || _rateDistance.Height != dh)
        {
            foreach (var t in new[] { _rateDistance, _rateImage }.OfType<Target2D>()) t.Texture.Dispose();   // released after the frames in flight
            using var batch = Gpu.Uploads.Begin();
            _rateDistance = Make(batch, dw, dh, InternalFormat.Rg32F, TextureMinFilter.Nearest, "post fog rate distance");
            var texture = batch.Create(new TextureDesc(InternalFormat.R8Ui, tw, th,
                    Use: TextureUse.Sampled | TextureUse.ColourTarget | TextureUse.ShadingRate | TextureUse.TransferSrc, Name: "post fog rate"));
            _rateImage = new Target2D(texture, TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        }
        _rateDistancePass ??= new FogRateDistancePass(Gpu);
        _ratePass ??= new FogRatePass(Gpu);
        if (FogVrsStats) CollectRateStats();

        float tanY = MathF.Tan(_fovNow * 0.5f);
        var tan = ViewWindow.Tan(tanY * _aspectNow, tanY);
        var r = _viewRotation;
        var size = new Vector2(_width, _height);

        var d = _rateDistancePass;
        Bind(d.P, d.NearDepth, _sceneDepth);
        d.P.Set(d.NearPlanes, _planes.X, _planes.Y);
        d.P.Set(d.Tan, tan);
        d.P.Set(d.Right, r.M11, r.M21, r.M31);
        d.P.Set(d.Up, r.M12, r.M22, r.M32);
        d.P.Set(d.Back, ViewWindow.BackFor(r, _fovNow, _aspectNow));
        d.P.Set(d.Eye, eye);
        d.P.Set(d.Size, size);
        d.P.Set(d.WaterY, WaterHeight ?? float.MinValue);
        Draw(d.P, _rateDistance!);

        var p = _ratePass;
        Bind(p.P, p.Distance, _rateDistance);
        p.P.Set(p.Tan, tan);
        p.P.Set(p.Right, r.M11, r.M21, r.M31);
        p.P.Set(p.Up, r.M12, r.M22, r.M32);
        p.P.Set(p.Back, ViewWindow.BackFor(r, _fovNow, _aspectNow));
        p.P.Set(p.Size, size);
        p.P.Set(p.Rate, texel, FogShadingRate.MarginBlocks, device.MaxShadingRate, 0);
        var thresholds = FogShadingRate.Thresholds;
        p.P.Set(p.Threshold, thresholds.TwoByTwo, thresholds.FourByFour);
        p.P.ApplyGlobals();   // the atmosphere's and the fog volumes' uniforms, through the frame globals
        Draw(p.P, _rateImage!);
        if (FogVrsStats) QueueRateReadback();
        Stamp("fog rate");
        CloseSegment();
        (_rateBuiltFrame, _rateEye, _rateBack) = (Gpu.Frame.Number, _eyeNow, BackOf(_viewRotation));
    }

    void QueueRateReadback()
    {
        var cmd = Segment();
        var image = _rateImage!;
        ref var slot = ref _rateReadbacks[_rateReadbackNext];
        slot.Buffer ??= ReadbackBuffer.Create(Gpu, (ulong)image.Width * (ulong)image.Height, "fog rate readback");
        var region = new Vk.BufferImageCopy
        {
            ImageSubresource = new Vk.ImageSubresourceLayers(Vk.ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Vk.Extent3D((uint)image.Width, (uint)image.Height, 1),
        };
        Gpu.Device.Vk.CmdCopyImageToBuffer(((CommandList)cmd).Handle, Gpu.Device.NativeImage(image.Texture), Vk.ImageLayout.General,
                Gpu.Device.BufferAt(slot.Buffer.Handle.Index), 1, &region);
        cmd.Order();
        slot.Frame = Gpu.Frame.Number;
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
            if (slot.Buffer is null || slot.Frame == 0 || !ReadbackBuffer.Completed(Gpu, slot.Frame) || slot.Buffer.Size < (ulong)tiles) continue;
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

    void DisposeShadingRate()
    {
        _rateDistancePass?.P.Dispose();
        _ratePass?.P.Dispose();
        foreach (var slot in _rateReadbacks) slot.Buffer?.Dispose();
    }
}