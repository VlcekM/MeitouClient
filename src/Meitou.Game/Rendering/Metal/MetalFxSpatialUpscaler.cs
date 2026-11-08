using Meitou.Rendering;
using SharpMetal.Metal;
using SharpMetal.MetalFX;

namespace Meitou.Metal;

/// <summary>
/// MetalFX's spatial upscaler: one color frame, no depth or motion history. It is a different algorithm from
/// <see cref="MetalFxUpscaler"/> (temporal), used for the 0.40 performance preset and the explicit 0.50 comparison;
/// it is available where the temporal scaler's 2.5x output darkens on this device.
/// </summary>
public sealed class MetalFxSpatialUpscaler : IUpscaler
{
    readonly MetalContext ctx;
    MTLFXSpatialScaler scaler;
    int inputWidth, inputHeight, outputWidth, outputHeight;
    bool failed;

    MetalFxSpatialUpscaler(MetalContext ctx) => this.ctx = ctx;

    public UpscalerKind Kind => UpscalerKind.MetalFx;
    public string Name => inputWidth == 0 ? "metalfx spatial" : $"metalfx spatial {inputWidth}x{inputHeight} -> {outputWidth}x{outputHeight}";

    public static MetalFxSpatialUpscaler? TryCreate(MetalContext ctx) =>
        MetalFxRuntime.Available ? new MetalFxSpatialUpscaler(ctx) : null;

    public bool Dispatch(UpscaleInputs i)
    {
        if (failed) return false;
        if (i.RenderWidth <= 0 || i.RenderHeight <= 0 || i.DisplayWidth <= 0 || i.DisplayHeight <= 0) return false;
        if (!EnsureScaler(i.RenderWidth, i.RenderHeight, i.DisplayWidth, i.DisplayHeight)) return false;
        var s = scaler;
        s.ColorTexture = ctx.TextureAt(i.Colour);
        s.OutputTexture = ctx.TextureAt(i.Output);
        try
        {
            // PostTaa is already inside its native segment; the spatial scaler has no history or reset state.
            s.EncodeToCommandBuffer(ctx.Frame.Command);
        }
        catch (Exception e)
        {
            Console.WriteLine($"metal     MetalFX spatial encode failed: {e.Message}");
            failed = true;
            return false;
        }
        return true;
    }

    bool EnsureScaler(int width, int height, int displayWidth, int displayHeight)
    {
        if (scaler.NativePtr != IntPtr.Zero && inputWidth == width && inputHeight == height &&
            outputWidth == displayWidth && outputHeight == displayHeight) return true;
        // A resize replaces the scaler while the previous frame ring may still name it. Disposing it here can
        // fault the GPU and make every following submission fail; hold it until the ring has passed.
        if (scaler.NativePtr != IntPtr.Zero)
        {
            var previous = scaler;
            ctx.DeferDelete(() => previous.Dispose());
        }
        var d = new MTLFXSpatialScalerDescriptor
        {
            InputWidth = (ulong)width,
            InputHeight = (ulong)height,
            OutputWidth = (ulong)displayWidth,
            OutputHeight = (ulong)displayHeight,
            ColorTextureFormat = MTLPixelFormat.RGBA16Float,
            OutputTextureFormat = MTLPixelFormat.RGBA16Float,
            ColorProcessingMode = MTLFXSpatialScalerColorProcessingMode.HDR,
        };
        try
        {
            scaler = d.NewSpatialScaler(ctx.Device);
        }
        catch (Exception e)
        {
            Console.WriteLine($"metal     MetalFX spatial create failed: {e.Message}");
            failed = true;
            return false;
        }
        if (scaler.NativePtr == IntPtr.Zero)
        {
            Console.WriteLine("metal     MetalFX spatial is unavailable on this device");
            failed = true;
            return false;
        }
        scaler.InputContentWidth = (ulong)width;
        scaler.InputContentHeight = (ulong)height;
        inputWidth = width;
        inputHeight = height;
        outputWidth = displayWidth;
        outputHeight = displayHeight;
        Console.WriteLine($"metal     MetalFX spatial {width}x{height} -> {displayWidth}x{displayHeight}");
        return true;
    }

    public void Dispose()
    {
        if (scaler.NativePtr != IntPtr.Zero) scaler.Dispose();
        scaler = default;
    }
}
