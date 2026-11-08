using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using System.Numerics;

using Meitou.Rendering.Gpu;
using Vk = Silk.NET.Vulkan;

using Meitou.Rendering.Gpu.Core;

namespace Meitou.Rendering;

/// <summary>
/// The fog shading rate (<c>fog-vrs</c>; <see cref="FogShadingRate"/>, docs/render-post.md "Fog shading rate"): after each frame's scene, two small passes turn its depth
/// into an R8_UINT image with one fragment size per tile of pixels, where the placed fog volumes, the haze and the weather's fog hide the surface; the next frame's opaque scene passes
/// (sky, terrain, objects, foliage, water) take it as their fragment shading rate attachment.
/// </summary>
public sealed unsafe partial class PostProcess
{
    /// <summary>The nearest distance of each block of 4 x 4 pixels (<see cref="PostProcessShaders.FogRateDistance"/>).</summary>
    sealed class FogRateDistancePass : FullscreenProgram
    {
        public readonly SamplerSlot NearDepth;
        public readonly UniformHandle NearPlanes, Tan, Right, Up, Back, Eye, Size, WaterY;
        public FogRateDistancePass(GpuContext gpu) : base(gpu, PostProcessShaders.FogRateDistance, "post fog rate distance")
        {
            NearDepth = P.Sampler("uNearDepth");
            (NearPlanes, Tan, Right, Up, Back) = (P.Uniform("uNearPlanes"), P.Uniform("uTan"), P.Uniform("uRight"), P.Uniform("uUp"), P.Uniform("uBack"));
            (Eye, Size, WaterY) = (P.Uniform("uEye"), P.Uniform("uSize"), P.Uniform("uWaterY"));
        }
    }

    /// <summary>The fragment size code of each shading rate tile (<see cref="PostProcessShaders.FogRate"/>).</summary>
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

    FogRateDistancePass? rateDistancePass;
    FogRatePass? ratePass;
    Target2D? rateDistance, rateImage;
    long rateBuiltFrame = long.MinValue;
    Vector3 rateEye, rateBack;
    readonly (ReadbackBuffer? Buffer, long Frame)[] rateReadbacks = new (ReadbackBuffer?, long)[4];
    int rateReadbackNext;
    readonly long[] rateTiles = new long[3];
    long rateTotal, rateFrames;

    /// <summary>The user's switch for the fog shading rate (default off). It does nothing where the device lacks VK_KHR_fragment_shading_rate (<see cref="FogVrsSupported"/>).</summary>
    public bool FogVrs { get; set; }

    /// <summary>The device can shade several pixels with one fragment from an attachment image.</summary>
    public bool FogVrsSupported => Gpu.Device.HasFragmentShadingRate;

    /// <summary>Counting the tiles of each rate (the bench's report): a readback of the rate image every frame.</summary>
    public bool FogVrsStats { get; set; }

    /// <summary>The share of the screen's tiles shaded 1 x 1, 2 x 2 and 4 x 4 over the frames counted (<see cref="FogVrsStats"/>), and how many frames were counted.</summary>
    public (double One, double Two, double Four, long Frames) FogVrsShare =>
        rateFrames == 0 ? default : (rateTiles[0] / (double)rateTotal, rateTiles[1] / (double)rateTotal, rateTiles[2] / (double)rateTotal, rateFrames);

    /// <summary>Starts the count of <see cref="FogVrsShare"/> afresh.</summary>
    public void ResetFogVrsStats() { Array.Clear(rateTiles); rateTotal = rateFrames = 0; }

    /// <summary>
    /// <paramref name="targets"/> with the fog shading rate image as their fragment shading rate attachment, when there is one fit for this frame: built by the previous frame, at this
    /// size, with the camera no more than a little moved (a cut or a fast turn shows none until the next build). Else, with the switch on, a neutral image (every tile 1 x 1):
    /// the frame shades as without one, and draws with the same pipelines as the others. Without any attachment it needed a second pipeline of everything it drew, made at the
    /// first cut in play: a stall of seconds with a cold driver cache (docs/bench.md "Play benchmark"). With the switch off, <paramref name="targets"/> as they are.
    /// </summary>
    public PassTargets WithShadingRate(PassTargets targets)
    {
        if (!FogVrs || !FogVrsSupported) return targets;
        int texel = Gpu.Device.ShadingRateTexel;
        int tw = FogShadingRate.Tiles(width, texel), th = FogShadingRate.Tiles(height, texel);
        bool fit = rateImage is not null && rateBuiltFrame == Gpu.Frame.Number - 1 && rateImage.Width == tw && rateImage.Height == th
            && Vector3.DistanceSquared(eyeNow, rateEye) <= 500f * 500f && Vector3.Dot(BackOf(viewRotation), rateBack) >= 0.97f;
        return targets.WithShadingRate((fit ? rateImage! : NeutralRate(tw, th)).Texture.View(), texel);
    }

    Target2D? neutralRate;

    /// <summary>An R8_UINT image of <paramref name="tiles"/> x <paramref name="rows"/> zeros: the rate code of 1 x 1 fragments in every tile.</summary>
    Target2D NeutralRate(int tiles, int rows)
    {
        if (neutralRate is { } made && made.Width == tiles && made.Height == rows) return made;
        neutralRate?.Texture.Dispose();   // released after the frames in flight
        using var batch = Gpu.Uploads.Begin();
        var texture = batch.Create(new TextureDesc(InternalFormat.R8ui, tiles, rows, Use: TextureUse.Sampled | TextureUse.ShadingRate | TextureUse.TransferDst, Name: "post fog rate neutral"));
        batch.Write(texture, 0, 0, new PixelRect(0, 0, (uint)tiles, (uint)rows), new byte[tiles * rows]);
        return neutralRate = new Target2D(texture, TextureMinFilter.Nearest, TextureMagFilter.Nearest);
    }

    static Vector3 BackOf(Matrix4x4 rotation) => new(rotation.M13, rotation.M23, rotation.M33);

    /// <summary>
    /// Builds the fog shading rate for the next frame from this frame's depth (call after the scene's passes, with <paramref name="eye"/> the camera's position): the nearest distance
    /// per 4 x 4 pixels, then per tile the opacity of the air to the nearest surface within it and a margin, and its rate. Does nothing with the switch off.
    /// </summary>
    public void BuildShadingRate(Vector3 eye)
    {
        if (!FogVrs || !FogVrsSupported || sceneColour is null || !haveScene)
        {
            rateBuiltFrame = long.MinValue;
            return;
        }
        var device = Gpu.Device;
        int texel = device.ShadingRateTexel;
        int tw = FogShadingRate.Tiles(width, texel), th = FogShadingRate.Tiles(height, texel);
        int dw = FogShadingRate.Tiles(width, 4), dh = FogShadingRate.Tiles(height, 4);
        if (rateImage is null || rateImage.Width != tw || rateImage.Height != th || rateDistance is null || rateDistance.Width != dw || rateDistance.Height != dh)
        {
            foreach (var t in new[] { rateDistance, rateImage }.OfType<Target2D>()) t.Texture.Dispose();   // released after the frames in flight
            using var batch = Gpu.Uploads.Begin();
            rateDistance = Make(batch, dw, dh, InternalFormat.RG32f, TextureMinFilter.Nearest, "post fog rate distance");
            var texture = batch.Create(new TextureDesc(InternalFormat.R8ui, tw, th, Use: TextureUse.Sampled | TextureUse.ColourTarget | TextureUse.ShadingRate | TextureUse.TransferSrc, Name: "post fog rate"));
            rateImage = new Target2D(texture, TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        }
        rateDistancePass ??= new FogRateDistancePass(Gpu);
        ratePass ??= new FogRatePass(Gpu);
        if (FogVrsStats) CollectRateStats();

        float tanY = MathF.Tan(fovNow * 0.5f);
        var tan = ViewWindow.Tan(tanY * aspectNow, tanY);
        var r = viewRotation;
        var size = new Vector2(width, height);

        var d = rateDistancePass;
        Bind(d.P, d.NearDepth, sceneDepth);
        d.P.Set(d.NearPlanes, planes.X, planes.Y);
        d.P.Set(d.Tan, tan);
        d.P.Set(d.Right, r.M11, r.M21, r.M31);
        d.P.Set(d.Up, r.M12, r.M22, r.M32);
        d.P.Set(d.Back, ViewWindow.BackFor(r, fovNow, aspectNow));
        d.P.Set(d.Eye, eye);
        d.P.Set(d.Size, size);
        d.P.Set(d.WaterY, WaterHeight ?? float.MinValue);
        Draw(d.P, rateDistance!);

        var p = ratePass;
        Bind(p.P, p.Distance, rateDistance);
        p.P.Set(p.Tan, tan);
        p.P.Set(p.Right, r.M11, r.M21, r.M31);
        p.P.Set(p.Up, r.M12, r.M22, r.M32);
        p.P.Set(p.Back, ViewWindow.BackFor(r, fovNow, aspectNow));
        p.P.Set(p.Size, size);
        p.P.Set(p.Rate, texel, FogShadingRate.MarginBlocks, device.MaxShadingRate, 0);
        var thresholds = FogShadingRate.Thresholds;
        p.P.Set(p.Threshold, thresholds.TwoByTwo, thresholds.FourByFour);
        p.P.ApplyGlobals();   // the atmosphere's and the fog volumes' uniforms, through the frame globals
        Draw(p.P, rateImage!);
        if (FogVrsStats) QueueRateReadback();
        Stamp("fog rate");
        CloseSegment();
        (rateBuiltFrame, rateEye, rateBack) = (Gpu.Frame.Number, eyeNow, BackOf(viewRotation));
    }

    void QueueRateReadback()
    {
        var cmd = Segment();
        var image = rateImage!;
        ref var slot = ref rateReadbacks[rateReadbackNext];
        slot.Buffer ??= ReadbackBuffer.Create(Gpu, (ulong)image.Width * (ulong)image.Height, "fog rate readback");
        var region = new Vk.BufferImageCopy
        {
            ImageSubresource = new Vk.ImageSubresourceLayers(Vk.ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Vk.Extent3D((uint)image.Width, (uint)image.Height, 1),
        };
        Gpu.Device.Vk.CmdCopyImageToBuffer(((CommandList)cmd).Handle, Gpu.Device.NativeImage(image.Texture), Vk.ImageLayout.General, Gpu.Device.BufferAt(slot.Buffer.Handle.Index), 1, &region);
        cmd.Order();
        slot.Frame = Gpu.Frame.Number;
        rateReadbackNext = (rateReadbackNext + 1) % rateReadbacks.Length;
    }

    /// <summary>Tallies the rate images whose frames have completed.</summary>
    void CollectRateStats()
    {
        if (rateImage is null) return;
        long tiles = (long)rateImage.Width * rateImage.Height;
        for (int i = 0; i < rateReadbacks.Length; i++)
        {
            ref var slot = ref rateReadbacks[i];
            if (slot.Buffer is null || slot.Frame == 0 || !ReadbackBuffer.Completed(Gpu, slot.Frame) || slot.Buffer.Size < (ulong)tiles) continue;
            foreach (byte code in slot.Buffer.Read(0, (ulong)tiles))
            {
                int s = FogShadingRate.SizeOf(code);
                rateTiles[s >= 4 ? 2 : s >= 2 ? 1 : 0]++;
            }
            rateTotal += tiles;
            rateFrames++;
            slot.Frame = 0;
        }
    }

    void DisposeShadingRate()
    {
        rateDistancePass?.P.Dispose();
        ratePass?.P.Dispose();
        foreach (var slot in rateReadbacks) slot.Buffer?.Dispose();
    }
}
