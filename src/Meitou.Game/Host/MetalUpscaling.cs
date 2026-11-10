#if MEITOU_METAL
// Metal exists on macOS only; the file is compiled only there (the csproj's MEITOU_METAL constant).
#pragma warning disable CA1416
using Meitou.Game.Rendering.Metal;
using Meitou.Game.Rendering.Post;

namespace Meitou.Game.Host;

/// <summary>
/// The Metal path's upscaler (docs/metal-backend.md): Native, MetalFX temporal or spatial, or the experimental TAA, with the render scale the
/// Display page's preset row sets.
/// </summary>
sealed class MetalUpscaling(UserConfig config, MetalContext ctx, PostTaa taa, MetalFxUpscaler? metalFx, MetalFxSpatialUpscaler? metalFxSpatial)
{
    /// <summary>
    /// The upscaler mode.
    /// </summary>
    internal MetalUpscalerMode Mode;

    /// <summary>
    /// No TAA or upscaler: the scene at native size, no jitter and no temporal resolve.
    /// </summary>
    internal bool NoTaa;

    /// <summary>
    /// MetalFX spatial: no jitter.
    /// </summary>
    internal bool Spatial;

    /// <summary>
    /// Selects the mode: the TAA pass's external upscaler, no jitter for Native and Spatial; the mode is kept in the config.
    /// </summary>
    internal void ApplyMode(MetalUpscalerMode next)
    {
        Mode = next;
        NoTaa = Mode == MetalUpscalerMode.Native;
        Spatial = Mode == MetalUpscalerMode.Spatial;
        taa.External = Mode switch
        {
            MetalUpscalerMode.Temporal => metalFx,
            MetalUpscalerMode.Spatial => metalFxSpatial,
            _ => null,
        };
        config.MetalUpscaler = Mode;
    }

    /// <summary>
    /// The Display page's preset row: 0 Native, 1-5 MetalFX temporal at 0.77 to 0.40, 6-7 MetalFX spatial at 0.50 and 0.40, 8 TAA, 9 a
    /// legacy or custom scale.
    /// </summary>
    internal int CurrentPresetIndex()
    {
        if (Mode == MetalUpscalerMode.Native) return 0;
        if (Mode == MetalUpscalerMode.Temporal && MathF.Abs(ctx.RenderScale - 0.77f) < 0.005f) return 1;
        if (Mode == MetalUpscalerMode.Temporal && MathF.Abs(ctx.RenderScale - 0.67f) < 0.005f) return 2;
        if (Mode == MetalUpscalerMode.Temporal && MathF.Abs(ctx.RenderScale - 0.59f) < 0.005f) return 3;
        if (Mode == MetalUpscalerMode.Temporal && MathF.Abs(ctx.RenderScale - 0.50f) < 0.005f) return 4;
        if (Mode == MetalUpscalerMode.Temporal && MathF.Abs(ctx.RenderScale - 0.40f) < 0.005f) return 5;
        if (Mode == MetalUpscalerMode.Spatial && MathF.Abs(ctx.RenderScale - 0.50f) < 0.005f) return 6;
        if (Mode == MetalUpscalerMode.Spatial && MathF.Abs(ctx.RenderScale - 0.40f) < 0.005f) return 7;
        if (Mode == MetalUpscalerMode.Taa) return 8;
        return 9;   // a legacy/custom scale; the preset row is the only way to change a MetalFX scale
    }

    /// <summary>
    /// A preset row's choice: its mode and render scale.
    /// </summary>
    internal void SetPreset(int index)
    {
        MetalUpscalerMode next = index switch
        {
            0 => MetalUpscalerMode.Native,
            1 or 2 or 3 or 4 or 5 => MetalUpscalerMode.Temporal,
            6 or 7 => MetalUpscalerMode.Spatial,
            8 => MetalUpscalerMode.Taa,
            _ => Mode,
        };
        float nextScale = index switch
        {
            0 => 1f,
            1 => 0.77f,
            2 => 0.67f,
            3 => 0.59f,
            4 => 0.50f,
            5 => 0.40f,
            6 => 0.50f,
            7 => 0.40f,
            8 => 0.50f,
            _ => ctx.RenderScale,
        };
        SetRenderScaleForPreset(next, nextScale);
    }

    /// <summary>
    /// The free render-scale slider: the scale in steps of 0.01 under the current mode (Native stays at full size; the clamp keeps MetalFX at
    /// 0.40 at least).
    /// </summary>
    internal void SetRenderScale(float scale) =>
        SetRenderScaleForPreset(Mode, Mode == MetalUpscalerMode.Native ? 1f : MathF.Round(scale * 100) / 100);

    /// <summary>
    /// Sets the mode and render scale, falling back when the device lacks MetalFX temporal or spatial; the scale is kept in the config.
    /// </summary>
    void SetRenderScaleForPreset(MetalUpscalerMode next, float nextScale)
    {
        if (next == MetalUpscalerMode.Temporal && metalFx is null)
        {
            Console.WriteLine("metal     MetalFX temporal is unavailable on this device");
            next = MetalUpscalerMode.Native;
            nextScale = 1f;
        }
        if (next == MetalUpscalerMode.Spatial && metalFxSpatial is null)
        {
            Console.WriteLine("metal     MetalFX spatial is unavailable on this device");
            next = metalFx is null ? MetalUpscalerMode.Native : MetalUpscalerMode.Temporal;
            nextScale = next == MetalUpscalerMode.Native ? 1f : nextScale;
        }
        ApplyMode(next);
        ctx.RenderScale = Math.Clamp(nextScale, next is MetalUpscalerMode.Temporal or MetalUpscalerMode.Spatial ? 0.4f : 0.33f, 1f);
        config.MetalRenderScale = ctx.RenderScale;
    }

    /// <summary>
    /// The settings menu's line naming the upscaler and its render scale.
    /// </summary>
    internal string UpscalerStatusText() => Mode switch
    {
        MetalUpscalerMode.Native => "Native",
        MetalUpscalerMode.Temporal => $"MetalFX temporal {ctx.RenderScale:0.00}",
        MetalUpscalerMode.Spatial => $"MetalFX spatial {ctx.RenderScale:0.00}",
        _ => $"TAA {ctx.RenderScale:0.00} (experimental)",
    };
}
#endif
