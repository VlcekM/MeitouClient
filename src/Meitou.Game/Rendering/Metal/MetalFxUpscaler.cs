using System.Runtime.InteropServices;
using SharpMetal.Metal;
using SharpMetal.MetalFX;
using SharpMetal.ObjectiveCCore;
using Meitou.Game.Rendering.Post;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// Apple's temporal upscaler, on the Metal path. It is deliberately small: <see cref="PostTaa"/> already runs the
/// velocity pass and owns the jitter, so this receives the same colour, depth and motion targets the TAA does and
/// encodes MetalFX into the frame's open command buffer. TAA remains the fallback when the framework, the scaler or
/// the encode is unavailable. Temporal is offered at every MetalFX preset scale down to 0.40; 0.33 crashed before
/// the first frame on this device.
/// </summary>
public sealed class MetalFxUpscaler : IUpscaler
{
    /// <summary>
    /// The context whose device, textures and frame command buffer the scaler uses.
    /// </summary>
    readonly MetalContext _ctx;
    /// <summary>
    /// The temporal scaler; null until the first dispatch, replaced when a size changes.
    /// </summary>
    MTLFXTemporalScaler _scaler;
    /// <summary>
    /// The render and display sizes the scaler was made for (0 before it is made).
    /// </summary>
    int _inputWidth, _inputHeight, _outputWidth, _outputHeight;
    /// <summary>
    /// Set when the scaler could not be made or encoded: every later dispatch returns false (TAA takes over).
    /// </summary>
    bool _failed;

    /// <summary>
    /// An upscaler over the context; the scaler itself is made on the first dispatch.
    /// </summary>
    MetalFxUpscaler(MetalContext ctx) => this._ctx = ctx;

    /// <summary>
    /// MetalFX.
    /// </summary>
    public UpscalerKind Kind => UpscalerKind.MetalFx;
    /// <summary>
    /// "metalfx", with the render and display sizes once the scaler is made.
    /// </summary>
    public string Name => _inputWidth == 0 ? "metalfx" : $"metalfx {_inputWidth}x{_inputHeight} -> {_outputWidth}x{_outputHeight}";

    /// <summary>
    /// Whether MetalFX can be loaded and created on this machine. The real scaler is made on the first dispatch,
    /// when the render and display sizes are known.
    /// </summary>
    public static MetalFxUpscaler? TryCreate(MetalContext ctx)
    {
        if (!MetalFxRuntime.Available) return null;
        return new MetalFxUpscaler(ctx);
    }

    /// <summary>
    /// Encodes MetalFX from the frame's colour, depth and motion (and reactive mask) into the output; false when it cannot (the caller falls back to
    /// TAA).
    /// </summary>
    public bool Dispatch(UpscaleInputs i)
    {
        if (_failed) return false;
        if (i.RenderWidth <= 0 || i.RenderHeight <= 0 || i.DisplayWidth <= 0 || i.DisplayHeight <= 0) return false;
        if (!EnsureScaler(i.RenderWidth, i.RenderHeight, i.DisplayWidth, i.DisplayHeight)) return false;

        var s = _scaler;
        s.ColorTexture = _ctx.TextureAt(i.Colour);
        s.DepthTexture = _ctx.TextureAt(i.Depth);
        s.MotionTexture = _ctx.TextureAt(i.Motion);
        s.OutputTexture = _ctx.TextureAt(i.Output);
        if (i.Reactive is { } reactive) s.ReactiveMaskTexture = _ctx.TextureAt(reactive);

        // Our motion is UV from current to previous; MetalFX takes render pixels towards the previous position.
        // MetalFX's motion space is y-down (the standard Metal framebuffer), while the velocity shader's NDC is y-up,
        // so X flips and Y does not. (Apple's example: a +1 NDC step uses motion scale (-width, +height).)
        // Jitter is in render pixels along +row (Metal's row is top-down).
        s.MotionVectorScaleX = -i.RenderWidth;
        s.MotionVectorScaleY = i.RenderHeight;
        s.JitterOffsetX = i.JitterPixels.X;
        s.JitterOffsetY = -i.JitterPixels.Y;
        s.PreExposure = 1;
        s.IsDepthReversed = false;
        s.Reset = i.Reset;
        try
        {
            // PostTaa is already inside its native segment and has closed the velocity encoder. MetalFX is a command
            // buffer stage, like a compute dispatch: encoding it directly keeps the pass labels and the single list.
            s.EncodeToCommandBuffer(_ctx.Frame.Command);
        }
        catch (Exception e)
        {
            Console.WriteLine($"metal     MetalFX encode failed: {e.Message}");
            _failed = true;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Makes the scaler for the sizes when it has none or another size (the old one deleted after the frame ring); false when the scale is below 0.40
    /// or MetalFX fails.
    /// </summary>
    bool EnsureScaler(int width, int height, int displayWidth, int displayHeight)
    {
        // 0.40 (2.5x) is the lowest stable tier on this device; 0.33 (3x) crashed before the first frame.
        // The GameHost clamps saved/CLI MetalFX scales, so this is a last-resort guard for a hand-edited config.
        if (displayWidth * 10L > width * 26L || displayHeight * 10L > height * 26L)
        {
            Console.WriteLine($"metal     MetalFX refuses {width}x{height} -> {displayWidth}x{displayHeight} (below 0.40); using TAA");
            _failed = true;
            return false;
        }
        if (_scaler.NativePtr != IntPtr.Zero && _inputWidth == width && _inputHeight == height &&
            _outputWidth == displayWidth && _outputHeight == displayHeight) return true;

        // A resize replaces the scaler while the previous frame ring may still name it. Disposing it here can
        // fault the GPU and make every following submission fail; hold it until the ring has passed.
        if (_scaler.NativePtr != IntPtr.Zero)
        {
            var previous = _scaler;
            _ctx.DeferDelete(() => previous.Dispose());
        }
        var d = new MTLFXTemporalScalerDescriptor
        {
            InputWidth = (ulong)width,
            InputHeight = (ulong)height,
            OutputWidth = (ulong)displayWidth,
            OutputHeight = (ulong)displayHeight,
            ColorTextureFormat = MTLPixelFormat.RGBA16Float,
            DepthTextureFormat = MTLPixelFormat.Depth32Float,
            MotionTextureFormat = MTLPixelFormat.RGBA16Float,
            IsReactiveMaskTextureEnabled = true,
            ReactiveMaskTextureFormat = MTLPixelFormat.R32Float,
            OutputTextureFormat = MTLPixelFormat.RGBA16Float,
        };
        try
        {
            _scaler = d.NewTemporalScaler(_ctx.Device);
        }
        catch (Exception e)
        {
            Console.WriteLine($"metal     MetalFX create failed: {e.Message}");
            _failed = true;
            return false;
        }
        if (_scaler.NativePtr == IntPtr.Zero)
        {
            Console.WriteLine("metal     MetalFX is unavailable on this device");
            _failed = true;
            return false;
        }

        // The content region is the whole render target (no letterboxing); MetalFX otherwise leaves its content
        // scale at the descriptor's 0-sized default on the larger upscale ratios.
        _scaler.InputContentWidth = (ulong)width;
        _scaler.InputContentHeight = (ulong)height;
        _inputWidth = width;
        _inputHeight = height;
        _outputWidth = displayWidth;
        _outputHeight = displayHeight;
        Console.WriteLine($"metal     MetalFX temporal {width}x{height} -> {displayWidth}x{displayHeight}" +
            $"{(displayWidth > 2 * width || displayHeight > 2 * height ? " (below 0.50)" : "")}");
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

/// <summary>
/// Loads the MetalFX framework once. SharpMetal's <c>ObjectiveC.LinkMetal()</c> links Metal, not MetalFX,
/// so without this the descriptor's Objective-C class does not exist.
/// </summary>
static class MetalFxRuntime
{
    /// <summary>
    /// The loaded MetalFX framework handle; 0 when it is not loaded.
    /// </summary>
    static nint s_framework;
    /// <summary>
    /// Whether the load was tried already.
    /// </summary>
    static bool s_looked;

    /// <summary>
    /// Whether the MetalFX framework loads (macOS 13 or later) and has its temporal scaler descriptor class; tried once.
    /// </summary>
    public static bool Available
    {
        get
        {
            if (s_looked) return s_framework != IntPtr.Zero;
            s_looked = true;
            if (!OperatingSystem.IsMacOSVersionAtLeast(13)) return false;
            try
            {
                s_framework = NativeLibrary.Load("/System/Library/Frameworks/MetalFX.framework/MetalFX");
                if (ObjectiveC.objc_getClass("MTLFXTemporalScalerDescriptor") == IntPtr.Zero)
                {
                    s_framework = IntPtr.Zero;
                    return false;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"metal     MetalFX framework unavailable: {e.Message}");
                s_framework = IntPtr.Zero;
            }
            return s_framework != IntPtr.Zero;
        }
    }
}
