using System.Globalization;

namespace Meitou.Rendering;

/// <summary>
/// The pure rules of the fog shading rate (<c>fog-vrs</c>, docs/render-post.md "Fog shading rate"): where the fog (the placed volumes, the haze and the weather's fog) hides what is
/// left of a surface, the opaque scene passes shade one fragment for a 2 x 2 or 4 x 4 block of pixels (VK_KHR_fragment_shading_rate, an attachment image with one rate per tile of
/// 8 or 16 pixels). The GPU passes (<see cref="PostProcessShaders.FogRateDistance"/>, <see cref="PostProcessShaders.FogRate"/>) work the opacity out; this is the rest.
/// </summary>
public static class FogShadingRate
{
    /// <summary>How far round a tile the nearest surface is looked for, in blocks of 4 pixels: the camera moves between the depth the rates are made from and the frame they are used in.</summary>
    public const int MarginBlocks = 2;

    /// <summary>The opacity from which a tile is shaded 2 x 2, and from which 4 x 4 (<c>MEITOU_FOG_VRS=a,b</c> overrides).</summary>
    public static readonly (float TwoByTwo, float FourByFour) Thresholds = ParseThresholds(Environment.GetEnvironmentVariable("MEITOU_FOG_VRS"));

    public const float DefaultTwoByTwo = 0.8f, DefaultFourByFour = 0.97f;

    /// <summary>The thresholds of <paramref name="text"/> ("0.6,0.97"); the defaults when it is empty or not two numbers in 0..1 in order.</summary>
    public static (float TwoByTwo, float FourByFour) ParseThresholds(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            var parts = text.Split(',');
            if (parts.Length == 2 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float a) &&
                float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float b) && a is >= 0 and <= 1 && b >= a && b <= 1.0001f)
                return (a, b);
        }
        return (DefaultTwoByTwo, DefaultFourByFour);
    }

    /// <summary>What shows of a surface behind the haze and the volumes is <c>(1 - hazeHidden) * transmittance</c>; this is the rest, how much the air hides.</summary>
    public static float Opacity(float hazeHidden, float transmittance) => 1f - (1f - Math.Clamp(hazeHidden, 0f, 1f)) * Math.Clamp(transmittance, 0f, 1f);

    /// <summary>The fragment size (pixels on a side: 1, 2 or 4) for <paramref name="opacity"/>, at most <paramref name="largest"/> (what the device lists).</summary>
    public static int Size(float opacity, int largest, (float TwoByTwo, float FourByFour) thresholds)
    {
        int size = 1;
        if (opacity >= thresholds.TwoByTwo) size = 2;
        if (opacity >= thresholds.FourByFour && largest >= 4) size = 4;
        return Math.Min(size, Math.Max(largest, 1));
    }

    /// <summary>The attachment texel of a fragment of <paramref name="width"/> x <paramref name="height"/> pixels: <c>log2(width) &lt;&lt; 2 | log2(height)</c> (1x1 is 0, 2x2 is 5, 4x4 is 10).</summary>
    public static int Encode(int width, int height) => (System.Numerics.BitOperations.Log2((uint)width) << 2) | System.Numerics.BitOperations.Log2((uint)height);

    /// <summary>The square fragment size a texel code stands for (2 x 1 and the like read as the larger side).</summary>
    public static int SizeOf(int code) => 1 << Math.Max(code >> 2, code & 3);

    /// <summary>Texels of a rate image covering <paramref name="pixels"/> pixels with tiles of <paramref name="texel"/>.</summary>
    public static int Tiles(int pixels, int texel) => Math.Max((pixels + texel - 1) / texel, 1);
}
