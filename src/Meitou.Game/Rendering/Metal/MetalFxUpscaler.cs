using System.Runtime.InteropServices;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using SharpMetal.Metal;
using SharpMetal.MetalFX;
using SharpMetal.ObjectiveCCore;

namespace Meitou.Metal;

/// <summary>
/// Apple's temporal upscaler, on the Metal path. It is deliberately small: <see cref="PostTaa"/> already runs the
/// velocity pass and owns the jitter, so this receives the same colour, depth and motion targets the TAA does and
/// encodes MetalFX into the frame's open command buffer. TAA remains the fallback when the framework, the scaler or
/// the encode is unavailable. Temporal is offered at every MetalFX preset scale down to 0.40; 0.33 crashed before
/// the first frame on this device.
/// </summary>
public sealed class MetalFxUpscaler : IUpscaler
{
    readonly MetalContext ctx;
    MTLFXTemporalScaler scaler;
    int inputWidth, inputHeight, outputWidth, outputHeight;
    bool failed;

    MetalFxUpscaler(MetalContext ctx) => this.ctx = ctx;

    public UpscalerKind Kind => UpscalerKind.MetalFx;
    public string Name => inputWidth == 0 ? "metalfx" : $"metalfx {inputWidth}x{inputHeight} -> {outputWidth}x{outputHeight}";

    /// <summary>Whether MetalFX can be loaded and created on this machine. The real scaler is made on the first dispatch,
    /// when the render and display sizes are known.</summary>
    public static MetalFxUpscaler? TryCreate(MetalContext ctx)
    {
        if (!MetalFxRuntime.Available) return null;
        return new MetalFxUpscaler(ctx);
    }

    public bool Dispatch(UpscaleInputs i)
    {
        if (failed) return false;
        if (i.RenderWidth <= 0 || i.RenderHeight <= 0 || i.DisplayWidth <= 0 || i.DisplayHeight <= 0) return false;
        if (!EnsureScaler(i.RenderWidth, i.RenderHeight, i.DisplayWidth, i.DisplayHeight)) return false;

        var s = scaler;
        s.ColorTexture = ctx.TextureAt(i.Colour);
        s.DepthTexture = ctx.TextureAt(i.Depth);
        s.MotionTexture = ctx.TextureAt(i.Motion);
        s.OutputTexture = ctx.TextureAt(i.Output);
        if (i.Reactive is { } reactive) s.ReactiveMaskTexture = ctx.TextureAt(reactive);
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
            s.EncodeToCommandBuffer(ctx.Frame.Command);
        }
        catch (Exception e)
        {
            Console.WriteLine($"metal     MetalFX encode failed: {e.Message}");
            failed = true;
            return false;
        }
        return true;
    }

    bool EnsureScaler(int width, int height, int displayWidth, int displayHeight)
    {
        // 0.40 (2.5x) is the lowest stable tier on this device; 0.33 (3x) crashed before the first frame.
        // The GameHost clamps saved/CLI MetalFX scales, so this is a last-resort guard for a hand-edited config.
        if (displayWidth * 10L > width * 26L || displayHeight * 10L > height * 26L)
        {
            Console.WriteLine($"metal     MetalFX refuses {width}x{height} -> {displayWidth}x{displayHeight} (below 0.40); using TAA");
            failed = true;
            return false;
        }
        if (scaler.NativePtr != IntPtr.Zero && inputWidth == width && inputHeight == height &&
            outputWidth == displayWidth && outputHeight == displayHeight) return true;
        // A resize replaces the scaler while the previous frame ring may still name it. Disposing it here can
        // fault the GPU and make every following submission fail; hold it until the ring has passed.
        if (scaler.NativePtr != IntPtr.Zero)
        {
            var previous = scaler;
            ctx.DeferDelete(() => previous.Dispose());
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
            scaler = d.NewTemporalScaler(ctx.Device);
        }
        catch (Exception e)
        {
            Console.WriteLine($"metal     MetalFX create failed: {e.Message}");
            failed = true;
            return false;
        }
        if (scaler.NativePtr == IntPtr.Zero)
        {
            Console.WriteLine("metal     MetalFX is unavailable on this device");
            failed = true;
            return false;
        }
        // The content region is the whole render target (no letterboxing); MetalFX otherwise leaves its content
        // scale at the descriptor's 0-sized default on the larger upscale ratios.
        scaler.InputContentWidth = (ulong)width;
        scaler.InputContentHeight = (ulong)height;
        inputWidth = width;
        inputHeight = height;
        outputWidth = displayWidth;
        outputHeight = displayHeight;
        Console.WriteLine($"metal     MetalFX temporal {width}x{height} -> {displayWidth}x{displayHeight}{(displayWidth > 2 * width || displayHeight > 2 * height ? " (below 0.50)" : "")}");
        return true;
    }

    public void Dispose()
    {
        if (scaler.NativePtr != IntPtr.Zero) scaler.Dispose();
        scaler = default;
    }
}

/// <summary>Loads the MetalFX framework once. SharpMetal's <c>ObjectiveC.LinkMetal()</c> links Metal, not MetalFX,
/// so without this the descriptor's Objective-C class does not exist.</summary>
static class MetalFxRuntime
{
    static nint framework;
    static bool looked;

    public static bool Available
    {
        get
        {
            if (looked) return framework != IntPtr.Zero;
            looked = true;
            if (!OperatingSystem.IsMacOSVersionAtLeast(13)) return false;
            try
            {
                framework = NativeLibrary.Load("/System/Library/Frameworks/MetalFX.framework/MetalFX");
                if (ObjectiveC.objc_getClass("MTLFXTemporalScalerDescriptor") == IntPtr.Zero)
                {
                    framework = IntPtr.Zero;
                    return false;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"metal     MetalFX framework unavailable: {e.Message}");
                framework = IntPtr.Zero;
            }
            return framework != IntPtr.Zero;
        }
    }
}
