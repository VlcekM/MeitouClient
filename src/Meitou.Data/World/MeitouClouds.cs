using System.Numerics;

namespace Meitou.Data.World;

/// <summary>
/// The Meitou <c>clouds</c> switch (viewer design, not the game's; docs/formats/clouds.md "Meitou clouds"): the game's cloud layer keeps its textures,
/// shape, coverage and alpha, but is lit. The game gives every cloud pixel the same colour (the sun's colour plus the +Z sky colour, dimmed only by
/// density), so its clouds are flat, turn one uniform orange at sunset and black at night. Meitou lights each pixel by a key light (the sun, or the
/// planet at night) through the cloud's depth towards it, with a forward-scattering phase (bright, silvered edges towards the sun, grey undersides away
/// from it), plus the sky's own light as the ambient. The per-pixel part is in <c>SkyRenderer</c>'s shader; these are its numbers and the CPU parts.
/// </summary>
public static class MeitouClouds
{
    /// <summary>Density taps along the key light's direction across the layer, and their spacing in texture units (<c>Clouds.dds</c> repeats every 1; its blobs are about 0.05..0.1 across).</summary>
    public const int LightSteps = 4;
    public const float LightStep = 0.015f;

    /// <summary>
    /// The cloud's depth towards the light is the lesser of two paths: up through the layer (the pixel's own density over the light's height, the slant at most
    /// <see cref="MaxSlant"/>) and sideways across it (the taps' sum × <see cref="SidePath"/>). The light's extinction over that depth is <see cref="Extinction"/>.
    /// </summary>
    public const float MaxSlant = 6, SidePath = 0.6f, Extinction = 0.6f;

    /// <summary>
    /// Multiple scattering, folded into one term: a share <see cref="MultipleShare"/> of the light goes through with the extinction × <see cref="MultipleExtinction"/>,
    /// so thick cores stay grey rather than black (the octave approximation of Wrenninge et al., one octave).
    /// </summary>
    public const float MultipleShare = 0.5f, MultipleExtinction = 0.15f;

    /// <summary>Two Henyey-Greenstein lobes, forward (the silver lining towards the light) and a weak backward one, mixed by <see cref="ForwardShare"/>.</summary>
    public const float ForwardG = 0.6f, BackwardG = -0.25f, ForwardShare = 0.55f;

    /// <summary>The key light's gain on the lit colour, and the ambient's (the sky's light on the cloud).</summary>
    public const float KeyGain = 1.6f, AmbientGain = 1.0f;

    /// <summary>How much of the ambient is the pixel's own sky colour behind the cloud (the rest the sky's mean, <see cref="Ambient"/>): the gradient across the sky at sunset.</summary>
    public const float AmbientFromSky = 0.2f;

    /// <summary>
    /// How grey the ambient is (0 the sky's own colour, 1 its luminance): <see cref="AmbientGrey"/> in a clear sky, rising to 1 as the cloud density c does (under an overcast
    /// the light the clouds get comes from other clouds, not from blue sky). The blue sky colour on its own made the clouds cyan.
    /// </summary>
    public const float AmbientGrey = 0.45f;
    public static float AmbientGreyAt(float c) => AmbientGrey + (1 - AmbientGrey) * Math.Clamp(c, 0, 1);

    /// <summary>The ambient on a cloud's underside falls by up to this much with its density (thick bases are darker: less of the sky above reaches them).</summary>
    public const float BaseDarkening = 0.4f;

    /// <summary>The share of the weather's <c>Darkness</c> (CloudLayer.Darkness) the lit clouds take: their depth already darkens thick cloud, the full darkness on top made overcast skies near black.</summary>
    public const float DarknessShare = 0.5f;

    /// <summary>The planet's light on the clouds against the land's (the land's is boosted <c>×60</c>, Enhancements.MeitouPlanetshineStrength; clouds lit as bright would glow).</summary>
    public const float PlanetShare = 0.5f;

    /// <summary>
    /// The Henyey-Greenstein phase function normalised to 1 for isotropic scattering (the usual one × 4π): <c>(1 − g²) / (1 + g² − 2 g cos θ)^1.5</c>,
    /// with <paramref name="cosTheta"/> the cosine between the view ray and the direction towards the light (1: looking into the light).
    /// </summary>
    public static float HenyeyGreenstein(float cosTheta, float g) =>
        (1 - g * g) / MathF.Pow(MathF.Max(1 + g * g - 2 * g * cosTheta, 1e-6f), 1.5f);

    /// <summary>The two lobes mixed (<see cref="ForwardG"/>, <see cref="BackwardG"/>, <see cref="ForwardShare"/>); its mean over the sphere is 1.</summary>
    public static float Phase(float cosTheta) =>
        ForwardShare * HenyeyGreenstein(cosTheta, ForwardG) + (1 - ForwardShare) * HenyeyGreenstein(cosTheta, BackwardG);

    /// <summary>The light that gets through <paramref name="depth"/> of cloud: the single-scattered share and the multiple-scattered share.</summary>
    public static float Transmittance(float depth) =>
        (1 - MultipleShare) * MathF.Exp(-Extinction * depth) + MultipleShare * MathF.Exp(-Extinction * MultipleExtinction * depth);

    /// <summary>
    /// The key light: its direction and colour in the cloud pass's units (<c>sunColour.rgb</c>, which the game's layer uses: <see cref="KenshiLighting.SunColour"/>).
    /// By day the sun (its saturation cut by <see cref="DayKey"/>); with <paramref name="planetLight"/> (the planetshine's light, in <see cref="KenshiLighting.SunLight"/>'s unit, 0 without it) the planet joins
    /// at <see cref="PlanetShare"/>, converted to the sun colour's unit, and <see cref="Planetshine.Combine"/> makes the two one light.
    /// </summary>
    public static (Vector3 Direction, Vector3 Colour) Key(Vector3 sun, Vector3 planetDirection, Vector3 planetLight)
    {
        var sunColour = DayKey(KenshiLighting.SunColour(sun), sun.Y);
        float unit = KenshiLighting.SunColour(Vector3.UnitY).Y / KenshiLighting.SunLight(Vector3.UnitY).Y;
        return Planetshine.Combine(sun, sunColour, planetDirection, planetLight * (unit * PlanetShare));
    }

    /// <summary>
    /// The sky's mean light on the clouds (the HDR scattering colour, the sky pass's units before the exposure's √): the zenith and four directions 20 degrees up,
    /// times the weather's sky colour multiplier, plus the night air's glow (<paramref name="nightAir"/>: rgb at full night, w its weight; 0 by day or without it).
    /// </summary>
    public static Vector3 Ambient(Vector3 sun, Vector3 skyMultiplier, Vector4 nightAir)
    {
        var sum = SkyXModel.Colour(Vector3.UnitY, sun, skydome: false);
        float y = MathF.Sin(20 * MathF.PI / 180), r = MathF.Cos(20 * MathF.PI / 180);
        sum += SkyXModel.Colour(new Vector3(r, y, 0), sun, skydome: false) + SkyXModel.Colour(new Vector3(-r, y, 0), sun, skydome: false)
             + SkyXModel.Colour(new Vector3(0, y, r), sun, skydome: false) + SkyXModel.Colour(new Vector3(0, y, -r), sun, skydome: false);
        return sum / 5 * skyMultiplier + new Vector3(nightAir.X, nightAir.Y, nightAir.Z) * (nightAir.W * NightAir.HorizonFalloff(y));
    }

    // ---- cloud shadows and parallax (the `cloudshadows` switch; docs/formats/clouds.md "Meitou cloud shadows") ----

    /// <summary>
    /// The height of the world-anchored cloud plane, in world units (a unit is about a decimetre): above the terrain's highest point
    /// (<see cref="WorldLayout.MaxHeight"/>, 9800). With the layer's texture scale (<c>uv = 0.1 · xz / height</c>) the pattern repeats every
    /// 10 × height (12 km) and the wind's texture shift (<see cref="CloudLayer.WindScale"/> × speed per second) sweeps the shadows over the ground
    /// at 5e-4 × height = 6 times the weather's wind speed (a 30 units/s wind: 18 m/s, a plausible wind aloft).
    /// </summary>
    public const float PlaneHeight = 12000;

    /// <summary>How much of the sun a full cloud takes away (its alpha × this; the rest is light through and round it).</summary>
    public const float ShadowStrength = 0.7f;

    /// <summary>The mip level the shadow reads <c>Clouds.dds</c> at: a texel is 12 m at the plane, the sun's penumbra from 1.2 km about 10 m, so about one texel of blur.</summary>
    public const float ShadowLod = 0.5f;

    /// <summary>The sun's height (its direction's y) over which the shadows fade in: none at <see cref="ShadowFadeLow"/> (the hit point runs off far
    /// away), full from <see cref="ShadowFadeHigh"/>.</summary>
    public const float ShadowFadeLow = 0.03f, ShadowFadeHigh = 0.12f;

    /// <summary>How strong the shadows are at the sun's height <paramref name="sunY"/>: <see cref="ShadowStrength"/> × a smoothstep from <see cref="ShadowFadeLow"/> to <see cref="ShadowFadeHigh"/>.</summary>
    public static float ShadowAt(float sunY)
    {
        float t = Math.Clamp((sunY - ShadowFadeLow) / (ShadowFadeHigh - ShadowFadeLow), 0, 1);
        return ShadowStrength * t * t * (3 - 2 * t);
    }

    /// <summary>
    /// The sky's view of the world-anchored plane from the eye: the game's lookup is <c>uv = 0.1 · d.xz / d.y</c> (the plane at "height 1", always
    /// the same distance above the eye); anchored, it is <c>0.1 · (eye.xz + d.xz / d.y · (height − eye.y)) / height</c>. Returns the factor on the
    /// game's uv (<c>(height − eye.y) / height</c>, at least a quarter, so an eye above the plane still sees the layer above it) and the offset
    /// <c>0.1 · eye.xz / height</c> wrapped to 0..1 in doubles (the textures repeat with period 1).
    /// </summary>
    public static (float Scale, Vector2 Offset) Parallax(double eyeX, double eyeY, double eyeZ)
    {
        static float Wrap(double v) => (float)(v - Math.Floor(v));
        float scale = (float)Math.Max((PlaneHeight - eyeY) / PlaneHeight, 0.25);
        return (scale, new Vector2(Wrap(0.1 * eyeX / PlaneHeight), Wrap(0.1 * eyeZ / PlaneHeight)));
    }

    // ---- the smoothed textures (docs/formats/clouds.md "Smoothed textures") ----

    /// <summary>
    /// The Gaussian sigma, in texels, of the blur the Meitou clouds' copies of <c>Clouds.dds</c> and <c>CloudsTile.dds</c> get. Both are BC1 (DXT1): every 4 × 4
    /// block holds four values between two 5-bit red endpoints, so a smooth density comes out as small blocks with steps between them. The game's flat white
    /// hid them; lit, their steps became contour bands and grain. A sigma of 1.2 removes the blocks and keeps the clouds' shapes (their features are 50 to 100 texels).
    /// </summary>
    public const float SmoothSigma = 1.2f;

    /// <summary>
    /// The red channel of a tiling RGBA8 image (the game's density; the other channels of <c>Clouds.dds</c> are other data) blurred by a separable Gaussian of
    /// <paramref name="sigma"/> texels, wrapping at the edges as the texture repeats, written to all three colour channels (alpha 255). Rounded to 8 bits.
    /// </summary>
    public static byte[] SmoothRed(ReadOnlySpan<byte> rgba, int width, int height, float sigma = SmoothSigma)
    {
        int radius = Math.Max(1, (int)MathF.Ceiling(3 * sigma));
        var kernel = new float[2 * radius + 1];
        float sum = 0;
        for (int i = -radius; i <= radius; i++) sum += kernel[i + radius] = MathF.Exp(-i * i / (2 * sigma * sigma));
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= sum;
        var across = new float[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float v = 0;
                for (int i = -radius; i <= radius; i++) v += kernel[i + radius] * rgba[(y * width + ((x + i) % width + width) % width) * 4];
                across[y * width + x] = v;
            }
        var result = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float v = 0;
                for (int i = -radius; i <= radius; i++) v += kernel[i + radius] * across[((y + i) % height + height) % height * width + x];
                byte b = (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
                int o = (y * width + x) * 4;
                (result[o], result[o + 1], result[o + 2], result[o + 3]) = (b, b, b, 255);
            }
        return result;
    }

    /// <summary>
    /// How much of its colour the daytime key light keeps (the rest goes to its luminance): SkyX's sun colour is a warm cream at noon, and against the greyed
    /// blue ambient that made a warm rim on every thin cloud edge and blue-grey cores (an oily look). Full colour at a low sun (sunset stays orange), this share
    /// from a sun height of <see cref="KeyDesaturateFrom"/> up.
    /// </summary>
    public const float DayKeySaturation = 0.4f, KeyDesaturateFrom = 0.35f, KeyDesaturateTo = 0.1f;

    /// <summary>The key light's colour <paramref name="colour"/> with its saturation cut by day (<see cref="DayKeySaturation"/>), at the sun's height <paramref name="sunY"/>.</summary>
    public static Vector3 DayKey(Vector3 colour, float sunY)
    {
        float t = Math.Clamp((sunY - KeyDesaturateTo) / (KeyDesaturateFrom - KeyDesaturateTo), 0, 1);
        float keep = 1 - (1 - DayKeySaturation) * t * t * (3 - 2 * t);
        return Vector3.Lerp(new Vector3(Planetshine.Luminance(colour)), colour, keep);
    }
}
