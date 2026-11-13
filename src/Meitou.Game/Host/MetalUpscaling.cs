#if MEITOU_METAL
// Metal exists on macOS only; the file is compiled only there (the csproj's MEITOU_METAL constant).
#pragma warning disable CA1416
using Meitou.Game.Rendering.Metal;
using Meitou.Game.Rendering.Post;

namespace Meitou.Game.Host;

/// <summary>
/// The Metal path's upscaling (docs/metal-backend.md): the Use Metal upscaling switch, with it on MetalFX temporal or spatial or the experimental
/// TAA at the render scale the Display page's preset row sets, with it off (<see cref="MetalUpscalerMode.Disabled"/>) a render at the window's
/// real pixels, or at its virtual Retina pixels (Full Retina resolution off), and in full screen at a whole division of the screen's, which the
/// present stretches with the nearest pixel (<see cref="Stretched"/>). <paramref name="backingScale"/> is the window's pixels per point (2 on Retina).
/// </summary>
sealed class MetalUpscaling(UserConfig config, MetalContext ctx, PostTaa taa, MetalFxUpscaler? metalFx, MetalFxSpatialUpscaler? metalFxSpatial,
    Func<bool> fullScreen, Func<float> backingScale)
{
    /// <summary>
    /// The upscaler mode now: <see cref="MetalUpscalerMode.Disabled"/> while the switch is off.
    /// </summary>
    internal MetalUpscalerMode Mode;

    /// <summary>
    /// Whether an upscaler is in use (the Use Metal upscaling switch).
    /// </summary>
    internal bool Enabled => Mode != MetalUpscalerMode.Disabled;

    /// <summary>
    /// No TAA or upscaler: the scene at the picture's size, no jitter and no temporal resolve.
    /// </summary>
    internal bool NoTaa;

    /// <summary>
    /// MetalFX spatial: no jitter.
    /// </summary>
    internal bool Spatial;

    /// <summary>
    /// Selects the mode: the TAA pass's external upscaler, no jitter for Disabled and Spatial; the switch and the upscaler are kept in the config
    /// (the upscaler stays while the switch is off, so turning it on returns to it).
    /// </summary>
    internal void ApplyMode(MetalUpscalerMode next)
    {
        Mode = next;
        NoTaa = Mode == MetalUpscalerMode.Disabled;
        Spatial = Mode == MetalUpscalerMode.Spatial;
        taa.External = Mode switch
        {
            MetalUpscalerMode.Temporal => metalFx,
            MetalUpscalerMode.Spatial => metalFxSpatial,
            _ => null,
        };
        config.MetalUseUpscaling = Enabled;
        if (Enabled) config.MetalUpscaler = Mode;
        Update();
    }

    /// <summary>
    /// The Use Metal upscaling switch: off, no upscaler; on, the saved upscaler at the saved render scale (MetalFX temporal when none was chosen).
    /// </summary>
    internal void SetEnabled(bool on)
    {
        if (on == Enabled) return;

        if (!on)
        {
            ApplyMode(MetalUpscalerMode.Disabled);
            return;
        }

        var saved = config.MetalUpscaler == MetalUpscalerMode.Disabled ? MetalUpscalerMode.Temporal : config.MetalUpscaler;
        SetRenderScaleForPreset(saved, config.MetalRenderScale);
    }

    #region Without the upscaler: the render resolution

    /// <summary>
    /// Whether the scene renders below the screen's size and is stretched to it: Disabled, at a fraction below 1 (in a window virtual Retina pixels
    /// on a Retina display, in full screen a division above 1).
    /// </summary>
    internal bool Stretched => Mode == MetalUpscalerMode.Disabled && RenderFraction < 1f;

    /// <summary>
    /// The share of the screen's pixels the scene renders at without the upscaler (<see cref="MetalRenderFraction.Of"/>).
    /// </summary>
    float RenderFraction => MetalRenderFraction.Of(fullScreen(), config.MetalRenderDivisor, config.MetalRetinaResolution, backingScale());

    /// <summary>
    /// Whether the interface is drawn at the render size with the picture (the screen's is the default), which the present then stretches.
    /// </summary>
    internal bool UiAtRenderSize => Stretched && !config.MetalUiAtScreenResolution;

    /// <summary>
    /// The render size of a <paramref name="width"/> x <paramref name="height"/> screen at the saved division (the screen's own when not stretched).
    /// </summary>
    internal (int Width, int Height) RenderSize(int width, int height) =>
        Stretched ? MetalContext.SceneSize(width, height, RenderFraction) : (width, height);

    /// <summary>
    /// The interface's size on a <paramref name="width"/> x <paramref name="height"/> screen: the render size when it is drawn with the picture,
    /// else the screen's.
    /// </summary>
    internal (int Width, int Height) UiSize(int width, int height) => UiAtRenderSize ? RenderSize(width, height) : (width, height);

    /// <summary>
    /// Brings the context to the state the mode and full screen ask for, before a frame: the render scale of Disabled (<see cref="RenderFraction"/>)
    /// and whether the picture is made apart and stretched.
    /// </summary>
    internal void Update()
    {
        bool stretched = Stretched;
        ctx.StretchPicture = stretched;
        if (Mode == MetalUpscalerMode.Disabled) ctx.RenderScale = stretched ? RenderFraction : 1f;
    }

    /// <summary>
    /// The Full Retina resolution switch, for a window: on, the render size is its real pixels; off, its virtual Retina pixels (one per point). It
    /// changes nothing where the window has one pixel per point.
    /// </summary>
    internal void SetRetinaResolution(bool on)
    {
        config.MetalRetinaResolution = on;
        Update();
    }

    /// <summary>
    /// The Render resolution row's choice: the screen's size divided by <paramref name="divisor"/> (1 to <see cref="UserConfig.MetalRenderDivisorMax"/>).
    /// </summary>
    internal void SetDivisor(int divisor)
    {
        config.MetalRenderDivisor = Math.Clamp(divisor, 1, UserConfig.MetalRenderDivisorMax);
        Update();
    }

    /// <summary>
    /// Whether the interface is drawn at the screen's size over a stretched picture.
    /// </summary>
    internal void SetUiAtScreen(bool on) => config.MetalUiAtScreenResolution = on;

    /// <summary>
    /// The render size's labels for the Render resolution row on a <paramref name="width"/> x <paramref name="height"/> screen: the divisions with
    /// their pixels.
    /// </summary>
    internal static string[] ResolutionLabels(int width, int height)
    {
        var labels = new string[UserConfig.MetalRenderDivisorMax];
        for (int divisor = 1; divisor <= labels.Length; divisor++)
        {
            var (w, h) = MetalContext.SceneSize(width, height, 1f / divisor);
            labels[divisor - 1] = divisor == 1 ? $"{w} x {h} (screen)" : $"{w} x {h} (screen / {divisor})";
        }
        return labels;
    }

    #endregion

    /// <summary>
    /// The Display page's preset row, shown while the upscaler is on: 0-4 MetalFX temporal at 0.77 to 0.40, 5-6 MetalFX spatial at 0.50 and 0.40,
    /// 7 TAA, 8 a legacy or custom scale.
    /// </summary>
    internal int CurrentPresetIndex()
    {
        if (Mode == MetalUpscalerMode.Temporal && MathF.Abs(ctx.RenderScale - 0.77f) < 0.005f) return 0;
        if (Mode == MetalUpscalerMode.Temporal && MathF.Abs(ctx.RenderScale - 0.67f) < 0.005f) return 1;
        if (Mode == MetalUpscalerMode.Temporal && MathF.Abs(ctx.RenderScale - 0.59f) < 0.005f) return 2;
        if (Mode == MetalUpscalerMode.Temporal && MathF.Abs(ctx.RenderScale - 0.50f) < 0.005f) return 3;
        if (Mode == MetalUpscalerMode.Temporal && MathF.Abs(ctx.RenderScale - 0.40f) < 0.005f) return 4;
        if (Mode == MetalUpscalerMode.Spatial && MathF.Abs(ctx.RenderScale - 0.50f) < 0.005f) return 5;
        if (Mode == MetalUpscalerMode.Spatial && MathF.Abs(ctx.RenderScale - 0.40f) < 0.005f) return 6;
        if (Mode == MetalUpscalerMode.Taa) return 7;
        return 8;   // a legacy/custom scale (also the switch off); the preset row is the only way to change a MetalFX scale
    }

    /// <summary>
    /// A preset row's choice: its mode and render scale.
    /// </summary>
    internal void SetPreset(int index)
    {
        MetalUpscalerMode next = index switch
        {
            0 or 1 or 2 or 3 or 4 => MetalUpscalerMode.Temporal,
            5 or 6 => MetalUpscalerMode.Spatial,
            7 => MetalUpscalerMode.Taa,
            _ => Mode,
        };
        float nextScale = index switch
        {
            0 => 0.77f,
            1 => 0.67f,
            2 => 0.59f,
            3 => 0.50f,
            4 => 0.40f,
            5 => 0.50f,
            6 => 0.40f,
            7 => 0.50f,
            _ => ctx.RenderScale,
        };
        SetRenderScaleForPreset(next, nextScale);
    }

    /// <summary>
    /// The free render-scale slider: the scale in steps of 0.01 under the current mode (the clamp keeps MetalFX at 0.40 at least); without the
    /// upscaler there is no scale, its row is not shown.
    /// </summary>
    internal void SetRenderScale(float scale)
    {
        if (Mode == MetalUpscalerMode.Disabled) return;

        SetRenderScaleForPreset(Mode, MathF.Round(scale * 100) / 100);
    }

    /// <summary>
    /// Sets the mode and render scale, falling back when the device lacks MetalFX temporal or spatial (to no upscaler, which switches it off); the
    /// scale is kept in the config (Disabled leaves the saved scale: it renders by the division).
    /// </summary>
    void SetRenderScaleForPreset(MetalUpscalerMode next, float nextScale)
    {
        if (next == MetalUpscalerMode.Temporal && metalFx is null)
        {
            Console.WriteLine("metal     MetalFX temporal is unavailable on this device");
            next = MetalUpscalerMode.Disabled;
        }
        if (next == MetalUpscalerMode.Spatial && metalFxSpatial is null)
        {
            Console.WriteLine("metal     MetalFX spatial is unavailable on this device");
            next = metalFx is null ? MetalUpscalerMode.Disabled : MetalUpscalerMode.Temporal;
        }
        ApplyMode(next);
        if (next == MetalUpscalerMode.Disabled) return;

        ctx.RenderScale = Math.Clamp(nextScale, next is MetalUpscalerMode.Temporal or MetalUpscalerMode.Spatial ? 0.4f : 0.33f, 1f);
        config.MetalRenderScale = ctx.RenderScale;
    }

    /// <summary>
    /// The settings menu's line naming the upscaler and its render scale.
    /// </summary>
    internal string UpscalerStatusText() => Mode switch
    {
        MetalUpscalerMode.Disabled => Stretched ? $"No upscaler, {RenderFraction:0.##} of the screen's size" : "No upscaler",
        MetalUpscalerMode.Temporal => $"MetalFX temporal {ctx.RenderScale:0.00}",
        MetalUpscalerMode.Spatial => $"MetalFX spatial {ctx.RenderScale:0.00}",
        _ => $"TAA {ctx.RenderScale:0.00} (experimental)",
    };
}
#endif
