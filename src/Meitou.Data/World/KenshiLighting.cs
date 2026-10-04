using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Textures;

namespace Meitou.Data.World;

/// <summary>
/// How the game lights the deferred scene (docs/formats/lighting.md): the sun's colour taken from SkyX, its daylight scale, the light
/// direction and ambient factor, and the exposure band of the HDR composite. Verified 2026-10-04 against <c>kenshi_x64.exe</c> (the sky
/// controller's update and creation), <c>SkyX_x64.dll</c> and <c>data/materials/deferred/deferred.hlsl</c>.
/// </summary>
public static class KenshiLighting
{
    /// <summary>The sky controller asks SkyX for its colour at the sun's direction plus this much world +Z.</summary>
    public const float SunColourOffsetZ = 0.04f;
    /// <summary>Below this sun height the sun's colour is black.</summary>
    public const float SunColourCutoff = -0.2f;
    /// <summary>The SkyX colour is divided by this times SkyX's exposure.</summary>
    public const float SunColourDivisor = 4;
    /// <summary>The specular colour of a dielectric; diffuse light is scaled by <c>1 − </c> it.</summary>
    public const float DielectricSpecular = 0.04f;
    /// <summary><c>EXPOSURE_KEY</c> of the HDR composite: the image is scaled by this over the adapted luminance.</summary>
    public const float ExposureKey = 0.55f;
    /// <summary>The irradiance cube is read at this mip level (<c>mp_irradiance.dds</c>, 16² with 5 levels).</summary>
    public const float IrradianceLevel = 3;

    /// <summary><c>sunColour.rgb</c>: SkyX's HDR colour towards <c>normalise(sun + (0, 0, 0.04))</c> over <c>4 · exposure</c>; black when the sun is under −0.2.</summary>
    public static Vector3 SunColour(Vector3 sun) =>
        sun.Y < SunColourCutoff ? Vector3.Zero
        : SkyXModel.Colour(sun + new Vector3(0, 0, SunColourOffsetZ), sun, skydome: false) / (SunColourDivisor * SkyAtmosphere.Exposure);

    /// <summary>
    /// <c>sunColour.w</c>: with the controller's value <paramref name="scale"/> (1, set when the sky is created), half of it at the horizon,
    /// rising to all of it with the sun's height, and falling at 10.8 times the rate below the horizon to 0 (at <c>sunY ≈ −0.093</c>).
    /// </summary>
    public static float Daylight(float sunY, float scale = 1)
    {
        float half = scale * 0.5f;
        float w = sunY > 0 ? half + (scale - half) * sunY : half - half * sunY * -10.8f;
        return MathF.Max(w, 0);
    }

    /// <summary>
    /// The sun's light before the biome's sun brightness: <c>sunColour.rgb · sunColour.w · ambientParams.w</c> (the last is 1: no code sets it).
    /// The shader multiplies it by the ambient map's alpha times 2.
    /// </summary>
    public static Vector3 SunLight(Vector3 sun) => SunColour(sun) * Daylight(sun.Y);

    /// <summary>The lighting pass's <c>sunDirection</c>: SkyX's sun direction with its height clamped to 0 and renormalised.</summary>
    public static Vector3 LightDirection(Vector3 sun)
    {
        if (sun.Y >= 0) return sun;
        var flat = new Vector3(sun.X, 0, sun.Z);
        return flat.LengthSquared() > 1e-12f ? Vector3.Normalize(flat) : Vector3.UnitX;
    }

    /// <summary>The environment light's scale by the sun: <c>clamp(5 L.y + 0.2, 0.1, 1)</c>, with L the clamped light direction (so at least 0.2).</summary>
    public static float EnvironmentFactor(Vector3 lightDirection) => Math.Clamp(lightDirection.Y * 5 + 0.2f, 0.1f, 1);

    /// <summary><c>MIN_LUMINANCE</c>, set every frame: <c>exposure min · lerp(night darkness, 1, saturate(5 · sunY))</c> (the sun's real height).</summary>
    public static float MinLuminance(float sunY, float exposureMin, float nightDarkness) =>
        exposureMin * (nightDarkness + (1 - nightDarkness) * Math.Clamp(sunY * 5, 0, 1));

    /// <summary>The composite's scale for a mean scene luminance: <c>max(0.55 / clamp(mean, min, max), 0.001)</c>.</summary>
    public static float ExposureScale(float meanLuminance, float minLuminance, float maxLuminance) =>
        MathF.Max(ExposureKey / Math.Clamp(meanLuminance, minLuminance, MathF.Max(minLuminance, maxLuminance)), 0.001f);
}

/// <summary>The CONSTANTS values the sky controller reads for the exposure: <c>exposure min</c> / <c>exposure max</c> and <c>night darkness</c>.</summary>
public sealed record ExposureConstants(float Min, float Max, float NightDarkness)
{
    /// <summary>The merged load order's values (0.8, 1.2, 0.35).</summary>
    public static readonly ExposureConstants Default = new(0.8f, 1.2f, 0.35f);

    public static ExposureConstants FromDatabase(GameDatabase db)
    {
        var c = db.OfType(FcsRecordType.CONSTANTS).FirstOrDefault(r => r.Name == "GLOBAL CONSTANTS") ?? db.OfType(FcsRecordType.CONSTANTS).FirstOrDefault();
        if (c is null) return Default;
        return new ExposureConstants(c.GetFloat("exposure min", Default.Min), c.GetFloat("exposure max", Default.Max), c.GetFloat("night darkness", Default.NightDarkness));
    }
}

/// <summary>
/// The ambient map the lighting pass multiplies by (docs/formats/lighting.md "The ambient map"): built by the game from the biome map
/// and the BIOMES records, rgb the biome's <c>ambient light</c>, alpha its <c>sun brightness</c> (0..2) × 0.5 × 255; white with alpha
/// 0x80 where a colour has no record. Covers the square ±<see cref="HalfWorld"/> like the biome map (row = +Z).
/// </summary>
public static class AmbientMap
{
    public const float HalfWorld = 147456;

    /// <summary>The texel for a biome record (null: no record for the colour).</summary>
    public static (byte R, byte G, byte B, byte A) Texel(GameRecord? biome)
    {
        if (biome is null) return (255, 255, 255, 0x80);
        int rgb = biome.GetInt("ambient light", 0xFFFFFF);
        float sun = Math.Clamp(biome.GetFloat("sun brightness", 1), 0, 2);
        return ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, (byte)(int)(sun * 0.5f * 255f));
    }

    /// <summary>The map at the biome map's size: each biome-map colour through its record (the first by string id when two share an index).</summary>
    public static RgbaImage Build(GameDatabase db, RgbaImage biomeMap)
    {
        var byIndex = new Dictionary<uint, GameRecord>();
        foreach (var r in db.OfType(FcsRecordType.BIOMES).OrderBy(r => r.StringId, StringComparer.Ordinal))
            byIndex.TryAdd((uint)r.GetInt("index") & 0xFFFFFF, r);
        var cache = new Dictionary<uint, (byte, byte, byte, byte)>();
        var pixels = new byte[biomeMap.Width * biomeMap.Height * 4];
        for (int i = 0; i < biomeMap.Width * biomeMap.Height; i++)
        {
            uint key = (uint)(biomeMap.Pixels[i * 4] << 16 | biomeMap.Pixels[i * 4 + 1] << 8 | biomeMap.Pixels[i * 4 + 2]);
            if (!cache.TryGetValue(key, out var t)) cache[key] = t = Texel(byIndex.GetValueOrDefault(key));
            (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]) = t;
        }
        return new RgbaImage(biomeMap.Width, biomeMap.Height, pixels);
    }
}
