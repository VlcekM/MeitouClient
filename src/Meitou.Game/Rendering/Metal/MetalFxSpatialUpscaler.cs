using SharpMetal.Metal;
using SharpMetal.MetalFX;
using Meitou.Game.Rendering.Post;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// MetalFX's spatial upscaler: one color frame, no depth or motion history. It is a different algorithm from
/// <see cref="MetalFxUpscaler"/> (temporal), used for the 0.40 performance preset and the explicit 0.50 comparison;
/// it is available where the temporal scaler's 2.5x output darkens on this device.
/// </summary>
public sealed class MetalFxSpatialUpscaler : IUpscaler
{
    /// <summary>
    /// The context whose device, textures and frame command buffer the scaler uses.
    /// </summary>
    readonly MetalContext _ctx;
    /// <summary>
    /// The spatial scaler; null until the first dispatch, replaced when a size changes.
    /// </summary>
    MTLFXSpatialScaler _scaler;
    /// <summary>
    /// The render and display sizes the scaler was made for (0 before it is made).
    /// </summary>
    int _inputWidth, _inputHeight, _outputWidth, _outputHeight;
    /// <summary>
    /// Set when the scaler could not be made or encoded: every later dispatch returns false.
    /// </summary>
    bool _failed;

    /// <summary>
    /// An upscaler over the context; the scaler itself is made on the first dispatch.
    /// </summary>
    MetalFxSpatialUpscaler(MetalContext ctx) => this._ctx = ctx;

    /// <summary>
    /// MetalFX.
    /// </summary>
    public UpscalerKind Kind => UpscalerKind.MetalFx;
    /// <summary>
    /// "metalfx spatial", with the render and display sizes once the scaler is made.
    /// </summary>
    public string Name => _inputWidth == 0 ? "metalfx spatial" : $"metalfx spatial {_inputWidth}x{_inputHeight} -> {_outputWidth}x{_outputHeight}";

    /// <summary>
    /// The spatial upscaler when the MetalFX framework loads, else null; the scaler is made on the first dispatch.
    /// </summary>
    public static MetalFxSpatialUpscaler? TryCreate(MetalContext ctx) =>
        MetalFxRuntime.Available ? new MetalFxSpatialUpscaler(ctx) : null;

    /// <summary>
    /// Encodes the spatial scaler from the frame's colour into the output; false when it cannot.
    /// </summary>
    public bool Dispatch(UpscaleInputs i)
    {
        if (_failed) return false;
        if (i.RenderWidth <= 0 || i.RenderHeight <= 0 || i.DisplayWidth <= 0 || i.DisplayHeight <= 0) return false;
        if (!EnsureScaler(i.RenderWidth, i.RenderHeight, i.DisplayWidth, i.DisplayHeight)) return false;
        var s = _scaler;
        s.ColorTexture = _ctx.TextureAt(i.Colour);
        s.OutputTexture = _ctx.TextureAt(i.Output);
        try
        {
            // PostTaa is already inside its native segment; the spatial scaler has no history or reset state.
            s.EncodeToCommandBuffer(_ctx.Frame.Command);
        }
        catch (Exception e)
        {
            Console.WriteLine($"metal     MetalFX spatial encode failed: {e.Message}");
            _failed = true;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Makes the scaler (HDR colour processing) for the sizes when it has none or another size (the old one deleted after the frame ring); false when
    /// MetalFX fails.
    /// </summary>
    bool EnsureScaler(int width, int height, int displayWidth, int displayHeight)
    {
        if (_scaler.NativePtr != IntPtr.Zero && _inputWidth == width && _inputHeight == height &&
            _outputWidth == displayWidth && _outputHeight == displayHeight) return true;

        // A resize replaces the scaler while the previous frame ring may still name it. Disposing it here can
        // fault the GPU and make every following submission fail; hold it until the ring has passed.
        if (_scaler.NativePtr != IntPtr.Zero)
        {
            var previous = _scaler;
            _ctx.DeferDelete(() => previous.Dispose());
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
            _scaler = d.NewSpatialScaler(_ctx.Device);
        }
        catch (Exception e)
        {
            Console.WriteLine($"metal     MetalFX spatial create failed: {e.Message}");
            _failed = true;
            return false;
        }
        if (_scaler.NativePtr == IntPtr.Zero)
        {
            Console.WriteLine("metal     MetalFX spatial is unavailable on this device");
            _failed = true;
            return false;
        }
        _scaler.InputContentWidth = (ulong)width;
        _scaler.InputContentHeight = (ulong)height;
        _inputWidth = width;
        _inputHeight = height;
        _outputWidth = displayWidth;
        _outputHeight = displayHeight;
        Console.WriteLine($"metal     MetalFX spatial {width}x{height} -> {displayWidth}x{displayHeight}");
        return true;
    }

    /// <summary>
    /// Releases the scaler.
    /// </summary>
    public void Dispose()
    {
        if (_scaler.NativePtr != IntPtr.Zero) _scaler.Dispose();
        _scaler = default;
    }
}
