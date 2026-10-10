namespace Meitou.Data.World;

/// <summary>
/// Meitou's night grading (the <c>nightgrade</c> switch; docs/render-post.md "Night grading"). The game has no grading at all; this is the viewer's own: at night
/// the eye's rods take over and colour fades towards a blue-grey (the Purkinje shift), so the composite blends the scene towards its rod luminance times a blue
/// tint, by a weight that is 0 by day and rises as the scene's adapted luminance falls, applied less to pixels much brighter than the adaptation (lamps, the moon,
/// fires keep their colour). This is the C# mirror of the GLSL in <c>PostProcessShaders.Composite</c> (a test checks the constants match), so the maths can be tested.
/// </summary>
public static class NightGrade
{
    /// <summary>Linear RGB to rod (scotopic) luminance, normalised so a neutral grey keeps its value. Derived from Jensen et al. 2001 ("A physically-based night sky
    /// model"), V = Y (1.33 (1 + (Y + Z) / X) - 1.68) on XYZ: evaluated at the sRGB primaries (red 0.085, green 1.969, blue 0.519, white 2.573) it is nearly additive, so
    /// these are the primaries' shares of the white's. Against Rec. 709 luminance weights red is nearly invisible to rods (0.16 of its photopic weight) and blue shows 2.8 times brighter (against the game's Rec. 601: 0.11 and 1.8).</summary>
    public const float RodR = 0.033f, RodG = 0.765f, RodB = 0.202f;

    /// <summary>Photopic luminance weights of the game's composite (Rec. 601, the grade's and the exposure's).</summary>
    public const float LumR = 0.299f, LumG = 0.587f, LumB = 0.114f;

    /// <summary>The scotopic tint before normalisation (a desaturated blue); normalised so its photopic luminance is 1 (<see cref="Tint"/>), so the blend moves hue and
    /// saturation and (apart from <see cref="PurkinjeShare"/>) not brightness.</summary>
    public const float TintR0 = 0.6f, TintG0 = 0.75f, TintB0 = 1.0f;

    /// <summary>How far the grey the blend goes to follows the rod luminance rather than the photopic one (0: the same brightness as before, 1: the rods' brightness, which
    /// darkens reds and lifts blues). Half keeps the picture's brightness nearly as it was.</summary>
    public const float PurkinjeShare = 0.5f;

    /// <summary>Scene mean luminance (the game's HDR units, the auto exposure's unclamped mean) at and below which the weight is 1, and at and above which it is 0; the
    /// smoothstep between them runs over the logarithm. Measured: midday 0.45 to 0.68, dusk's end (22:30) 0.33, 23:00 0.16, deep night 0.04, 5:30 0.21, 6:00 0.32.</summary>
    public const float MeanDark = 0.06f, MeanLight = 0.30f;

    /// <summary>A pixel's luminance relative to the scene mean at and below which it is graded fully, and at and above which it keeps its colour (log smoothstep).</summary>
    public const float RatioFrom = 1.25f, RatioTo = 3.2f;

    /// <summary>The strength the Meitou switch sets (the Tab slider takes 0 to 1); the weight never exceeds it.</summary>
    public const float MeitouStrength = 0.7f;

    public static (float R, float G, float B) Tint
    {
        get
        {
            float y = LumR * TintR0 + LumG * TintG0 + LumB * TintB0;
            return (TintR0 / y, TintG0 / y, TintB0 / y);
        }
    }

    public static float RodLuminance(float r, float g, float b) => RodR * r + RodG * g + RodB * b;

    public static float Luminance(float r, float g, float b) => LumR * r + LumG * g + LumB * b;

    static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>The share of the scene that is scotopic from the adaptation: 1 in the dark, 0 in the light, smooth over the logarithm of the mean luminance.</summary>
    public static float AdaptationWeight(float meanLuminance)
    {
        float m = MathF.Max(meanLuminance, 1e-6f);
        return 1 - Smoothstep(MathF.Log(MeanDark), MathF.Log(MeanLight), MathF.Log(m));
    }

    /// <summary>1 for pixels near the adaptation level, falling to 0 for pixels far brighter (lamps, the moon): they are lit well above what the rods see.</summary>
    public static float PixelWeight(float pixelLuminance, float meanLuminance)
    {
        float ratio = MathF.Max(pixelLuminance, 1e-6f) / MathF.Max(meanLuminance, 1e-6f);
        return 1 - Smoothstep(MathF.Log(RatioFrom), MathF.Log(RatioTo), MathF.Log(ratio));
    }

    /// <summary>
    /// The graded colour. <paramref name="rgb"/> is the exposed linear colour; <paramref name="sceneLuminance"/> the same pixel's luminance in the scene's own units
    /// (before the exposure) to compare with <paramref name="meanLuminance"/>; <paramref name="strength"/> 0 returns the colour unchanged.
    /// </summary>
    public static (float R, float G, float B) Apply((float R, float G, float B) rgb, float sceneLuminance, float meanLuminance, float strength)
    {
        float w = strength * AdaptationWeight(meanLuminance) * PixelWeight(sceneLuminance, meanLuminance);
        if (w <= 0) return rgb;
        float grey = float.Lerp(Luminance(rgb.R, rgb.G, rgb.B), RodLuminance(rgb.R, rgb.G, rgb.B), PurkinjeShare);
        var t = Tint;
        return (float.Lerp(rgb.R, grey * t.R, w), float.Lerp(rgb.G, grey * t.G, w), float.Lerp(rgb.B, grey * t.B, w));
    }
}
