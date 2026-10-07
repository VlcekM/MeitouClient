namespace Meitou.Rendering;

/// <summary>
/// Which top mip levels of a texture the picture can never sample, from how near the texture is used (docs/renderer-native.md 8.12).
/// A sampled level is the footprint of a pixel in texels, log2, taken from its short axis under anisotropic filtering (the sampler divides the long
/// axis by a whole number of taps, which can take up to one level below the short axis). The footprint of one
/// pixel on a surface at distance <c>d</c> is at least <c>d cos^2(a) / f</c> (<c>f</c> the focal length in pixels, <c>a</c> the angle from the
/// view axis to the screen's corner; the surface may be tilted, which stretches only the long axis) in world units, and one texel of an
/// <c>N</c>-texel texture spans <c>k / N</c> of them (<c>k</c>: <see cref="MeshTexelScale"/>, times the instance's scale, over the material's
/// tiling). So the level is at least <c>log2(d cos^2(a) N / (f k)) + bias</c>: with <c>need = d / k</c> that is
/// <c>log2(need * <see cref="PixelsPerDistance"/> * N) + bias</c>, and a texture whose use never gets below that can lose that many top levels
/// (less <see cref="Margin"/>, two levels: one for the tap rounding, one for the 2x2 derivative estimate, the triangles stretched more than the part's
/// <see cref="MeshTexelScale"/> and the time a finer image takes to arrive). The levels below
/// are sampled as before: the bottom levels of a mip chain do not depend on the top ones, so the picture is the same.
/// </summary>
public sealed class MipStreaming
{
    /// <summary>Levels held back against the bound: the anisotropic tap rounding (up to 1), derivative estimates that differ from the exact footprint, the
    /// few triangles more stretched than the part's texel scale, and an approaching object has to be
    /// refined before it needs to be.</summary>
    public static readonly float Margin = float.TryParse(Environment.GetEnvironmentVariable("MEITOU_MIP_MARGIN"), System.Globalization.CultureInfo.InvariantCulture, out float m) ? m : 2f;
    /// <summary>Never drop to a top level whose larger side is smaller than this (also keeps the level the normal-map swizzle test reads, the first within 256, in the chain).</summary>
    public const int MinSize = 512;

    /// <summary>Off until the owner tells the camera (<see cref="SetView"/>): no level is dropped.</summary>
    public bool Enabled { get; private set; }
    /// <summary>Lower bound of a pixel's footprint per unit of distance (world units), over the whole screen.</summary>
    public float PixelsPerDistance { get; private set; }
    /// <summary>The sampler's LOD bias in effect (an upscaler lowers every level).</summary>
    public float Bias { get; private set; }
    /// <summary>Extra levels dropped (the memory guard under pressure).</summary>
    public int Extra { get; set; }
    /// <summary><c>MEITOU_MIP_STREAM=0</c> turns it off, for comparisons.</summary>
    public static readonly bool Allowed = Environment.GetEnvironmentVariable("MEITOU_MIP_STREAM") != "0";

    /// <param name="height">Height in pixels of the picture the scene is drawn into (the render size, before an upscaler).</param>
    /// <param name="lodBias">The sampler bias the upscaler adds (<c>GpuContext.LodBias</c>); with the display size instead of the render size, without the part from the render scale.</param>
    public void SetView(int width, int height, float fieldOfView, float lodBias)
    {
        float tanV = MathF.Tan(fieldOfView * 0.5f), tanH = tanV * width / Math.Max(height, 1);
        float focal = height / (2 * tanV);
        float cos2 = 1 / (1 + tanV * tanV + tanH * tanH);   // cos^2 of the angle to the corner: tan^2 = tanV^2 + tanH^2
        PixelsPerDistance = cos2 / focal;
        // The sampler adds -0.25 on NVIDIA with anisotropic filtering (Samplers.cs); counted everywhere.
        Bias = Math.Min(lodBias, 0) - 0.25f;
        Enabled = Allowed && height > 0 && float.IsFinite(PixelsPerDistance);
    }

    /// <summary>How many top levels a texture of <paramref name="width"/> x <paramref name="height"/> texels (as in its file) can do without, at most.</summary>
    public int Drop(int width, int height, float need) => Drop(width, height, need, Enabled, PixelsPerDistance, Bias, Extra);

    public static int Drop(int width, int height, float need, bool enabled, float pixelsPerDistance, float bias, int extra)
    {
        if (!enabled || !float.IsFinite(need) || need <= 0) return 0;
        int n = Math.Min(width, height);
        if (n <= MinSize) return 0;
        double level = Math.Log2((double)pixelsPerDistance * need * n) + Math.Min(bias, 0) - Margin;
        int drop = (int)Math.Floor(level) + extra;
        int most = (int)Math.Log2(Math.Max(Math.Max(width, height), 1)) - (int)Math.Log2(MinSize);
        return Math.Clamp(drop, 0, Math.Max(most, 0));
    }
}
