using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>
/// The sun's path over the day as the game computes it (docs/formats/terrain.md, "Sky and sun"): from the CONSTANTS record's
/// <c>latitude</c>, <c>sunrise</c> and <c>sunset</c> (Verified, kenshi_x64.exe sky controller).
/// </summary>
public sealed record SkyClock(float LatitudeDegrees, float Sunrise, float Sunset)
{
    /// <summary>The game's fallback when <c>sunset</c> is not after <c>sunrise</c>.</summary>
    public static readonly SkyClock Fallback = new(0, 6, 20);

    public static SkyClock FromDatabase(GameDatabase db)
    {
        var c = db.OfType(FcsRecordType.CONSTANTS).FirstOrDefault(r => r.Name == "GLOBAL CONSTANTS") ?? db.OfType(FcsRecordType.CONSTANTS).FirstOrDefault();
        if (c is null) return Fallback;
        float rise = c.GetFloat("sunrise", 6), set = c.GetFloat("sunset", 20);
        if (set <= rise) (rise, set) = (Fallback.Sunrise, Fallback.Sunset);
        return new SkyClock(c.GetFloat("latitude", 0), rise, set);
    }

    /// <summary>
    /// Fraction of the sun's half-turn: 0 at sunrise, 1 at sunset (daytime spread over sunrise..sunset), then 1..2
    /// over the night.
    /// </summary>
    public float Phase(float hour)
    {
        hour = ((hour % 24) + 24) % 24;
        if (hour >= Sunrise && hour <= Sunset) return (hour - Sunrise) / (Sunset - Sunrise);
        if (hour < Sunrise) hour += 24;
        return (hour - Sunset) / (24 - (Sunset - Sunrise)) + 1;
    }

    /// <summary>
    /// Unit vector towards the sun: (cos a, cos(lat)·sin a, sin(lat)·sin a) with a = phase·π, so it rises at +X,
    /// sets at −X and is below the horizon (negative Y) at night. Which compass direction ±X is, is Unknown.
    /// </summary>
    public Vector3 SunDirection(float hour)
    {
        float a = Phase(hour) * MathF.PI, lat = LatitudeDegrees * MathF.PI / 180f;
        return new Vector3(MathF.Cos(a), MathF.Cos(lat) * MathF.Sin(a), MathF.Sin(lat) * MathF.Sin(a));
    }
}

/// <summary>
/// The SkyX atmosphere options the game creates its sky with and the shader parameters SkyX derives from them (docs/formats/sky.md,
/// "The scattering model"). Verified 2026-10-04 against <c>kenshi_x64.exe</c> (the sky creation passes the options struct to
/// <c>AtmosphereManager::_update</c>) and <c>SkyX_x64.dll</c> (which option feeds which uniform): inner radius 9.77501, outer 10.2963,
/// height position 0.01, Rayleigh 0.0022, Mie 0.000675, sun intensity 30, wavelengths (0.57, 0.48, 0.44), g −0.991, exposure 1.4,
/// 4 samples. <c>uScale = 1 / (outer − inner)</c>, <c>uScaleDepth = (outer − inner) / 2</c>, <c>uScaleOverScaleDepth = uScale / uScaleDepth</c>,
/// <c>uKr4PI = Kr · 4π</c>, <c>uKrESun = Kr · sun</c> (likewise Mie), <c>uInvWaveLength = 1 / λ⁴</c>, the camera at
/// <c>(0, inner + heightPosition · (outer − inner), 0)</c> whatever the real eye height.
/// </summary>
public static class SkyAtmosphere
{
    public const float InnerRadius = 9.77501f;
    public const float OuterRadius = 10.2963f;
    public const float HeightPosition = 0.01f;
    public const float RayleighMultiplier = 0.0022f;
    public const float MieMultiplier = 0.000675f;
    public const float SunIntensity = 30f;
    /// <summary>
    /// The wavelengths the game gives SkyX: its setup builds (0.57, 0.54, 0.44), then stores 0.48 over the green component, which
    /// <c>AtmosphereManager::_update</c> reads as the green wavelength.
    /// </summary>
    public static readonly Vector3 WaveLength = new(0.57f, 0.48f, 0.44f);
    /// <summary>Mie anisotropy as SkyX's shaders use it (the cosine is taken towards the eye, so the negative value gives a lobe round the sun).</summary>
    public const float PhaseG = -0.991f;
    /// <summary>SkyX's <c>uExposure</c>, the factor on the scattered light in HDR mode.</summary>
    public const float Exposure = 1.4f;
    public const int Samples = 4;

    /// <summary>Atmosphere thickness in SkyX units.</summary>
    public const float Thickness = OuterRadius - InnerRadius;
    /// <summary>SkyX's <c>uScaleDepth</c>: half the thickness.</summary>
    public const float ScaleDepth = Thickness / 2;
    /// <summary>Density scale height in SkyX units: <c>1 / uScaleOverScaleDepth = thickness² / 2</c>.</summary>
    public const float ScaleHeight = Thickness * Thickness / 2;
    /// <summary>The height of SkyX's camera (its <c>uCameraPos.y</c>), above the planet's centre.</summary>
    public const float CameraY = InnerRadius + HeightPosition * Thickness;
    public const float KrESun = RayleighMultiplier * SunIntensity, KmESun = MieMultiplier * SunIntensity;
    public const float Kr4Pi = RayleighMultiplier * 4 * MathF.PI, Km4Pi = MieMultiplier * 4 * MathF.PI;
    /// <summary><c>1 / wavelength⁴</c> per channel (SkyX's <c>uInvWaveLength</c>).</summary>
    public static Vector3 InverseWaveLength4 => new(1 / MathF.Pow(WaveLength.X, 4), 1 / MathF.Pow(WaveLength.Y, 4), 1 / MathF.Pow(WaveLength.Z, 4));
    /// <summary>Extinction per unit of density-weighted path, per channel: <c>invλ⁴ · Kr · 4π + Km · 4π</c>.</summary>
    public static Vector3 Extinction => InverseWaveLength4 * Kr4Pi + new Vector3(Km4Pi);
    /// <summary>Rayleigh optical depth of a vertical ray from sea level to space, per channel (<c>Kr · 4π · invλ⁴ · scaleDepth</c>).</summary>
    public static Vector3 RayleighZenithDepth => InverseWaveLength4 * (Kr4Pi * ScaleDepth);
    /// <summary>Mie optical depth of a vertical ray (<c>Km · 4π · scaleDepth</c>).</summary>
    public const float MieZenithDepth = Km4Pi * ScaleDepth;
}

/// <summary>
/// SkyX's scattering as the game runs it (docs/formats/sky.md): O'Neil's single scattering with SkyX's polynomial fit of the optical
/// depth, sampled 4 times along the ray from SkyX's fixed camera. The skydome shader, the CPU <c>AtmosphereManager::getColorAt</c>
/// (in HDR mode, which the game selects) and the atmosphere haze all evaluate this integral; this class is the viewer's
/// implementation of it, written from the docs.
/// </summary>
public static class SkyXModel
{
    /// <summary>The lowest optical depth a sample may have (a viewer guard against the fit's runaway on steep downward rays, see <see cref="InScatter(Vector3, float, Vector3, out float)"/>).</summary>
    public const float OpticalFloor = -1;

    /// <summary>O'Neil's scale function: the optical depth towards the top of the air at a zenith cosine, in units of the scale depth.</summary>
    public static float Scale(float cos)
    {
        float x = 1 - cos;
        return SkyAtmosphere.ScaleDepth * MathF.Exp(-0.00287f + x * (0.459f + x * (3.83f + x * (-6.80f + x * 5.25f))));
    }

    /// <summary>
    /// The sum, over the samples along <paramref name="ray"/> (unit, from SkyX's camera) for <paramref name="length"/> SkyX units, of the
    /// air's density times the sample's scaled length times the light's attenuation sun → sample → eye, per channel. Times
    /// <c>invλ⁴ · KrESun</c> it is the Rayleigh colour, times <c>KmESun</c> the Mie colour. <paramref name="thickness"/>: the same sum
    /// without the attenuation (the skydome's opacity term). <paramref name="sun"/>: unit direction towards the sun.
    /// </summary>
    public static Vector3 InScatter(Vector3 ray, float length, Vector3 sun, out float thickness)
    {
        const float camera = SkyAtmosphere.CameraY;
        const float sosd = 1 / SkyAtmosphere.ScaleHeight, scale = 1 / SkyAtmosphere.Thickness;
        float startOffset = MathF.Exp(sosd * (SkyAtmosphere.InnerRadius - camera)) * Scale(ray.Y);
        float step = length / SkyAtmosphere.Samples;
        var ext = SkyAtmosphere.Extinction;
        var sum = Vector3.Zero;
        thickness = 0;
        for (int i = 0; i < SkyAtmosphere.Samples; i++)
        {
            var p = new Vector3(0, camera, 0) + ray * (step * (i + 0.5f));
            float h = p.Length(), density = MathF.Exp(sosd * (SkyAtmosphere.InnerRadius - h));
            // The floor at −1 is a viewer guard (docs/formats/sky.md "Haze"): on long steep rays down, which only an eye far above the
            // game's camera heights produces, the fit's optical depth goes strongly negative and the light would grow without bound.
            // The sky's never goes below 0.35, the haze's below −0.26 for an eye up to 15000 above the ground: the game's own results are untouched.
            float optical = Math.Clamp(startOffset + density * (Scale(Vector3.Dot(sun, p) / h) - Scale(Vector3.Dot(ray, p) / h)), OpticalFloor, 1e4f);
            var t = new Vector3(MathF.Exp(-optical * ext.X), MathF.Exp(-optical * ext.Y), MathF.Exp(-optical * ext.Z));
            sum += t * (density * step * scale);
            thickness += density * step * scale;
        }
        return sum;
    }

    public static Vector3 InScatter(Vector3 ray, float length, Vector3 sun) => InScatter(ray, length, sun, out _);

    /// <summary>SkyX's Rayleigh phase function, <c>0.75 (1 + 0.5 cos²)</c>.</summary>
    public static float RayleighPhase(float cos) => 0.75f * (1 + 0.5f * cos * cos);

    /// <summary>SkyX's Mie phase function (Cornette-Shanks form) with the game's g; <paramref name="cos"/> as SkyX takes it, towards the eye.</summary>
    public static float MiePhase(float cos)
    {
        const float g = SkyAtmosphere.PhaseG, g2 = g * g;
        return 1.5f * ((1 - g2) / (2 + g2)) * (1 + cos * cos) / MathF.Pow(1 + g2 - 2 * g * cos, 1.5f);
    }

    /// <summary>
    /// The sky's HDR colour towards <paramref name="direction"/>: <c>exposure · (rayleighPhase · Rayleigh + miePhase · Mie)</c> to the dome
    /// point <c>direction + (0, inner, 0)</c>, plus SkyX's night glow <c>nightmult · ((0.05, 0.05, 0.1) (2 − 0.75 saturate(−sunY)) (1 − y)³)^2.2</c>
    /// where the scattered light's brightest channel is under 0.1. <paramref name="skydome"/>: the shader's form, whose night factor also
    /// fades with the opacity term; false gives <c>getColorAt</c>'s.
    /// </summary>
    public static Vector3 Colour(Vector3 direction, Vector3 sun, bool skydome = true)
    {
        var d = Vector3.Normalize(direction);
        var ray = d + new Vector3(0, SkyAtmosphere.InnerRadius - SkyAtmosphere.CameraY, 0);
        float far = ray.Length();
        ray /= far;
        var sum = InScatter(ray, far, sun, out float thickness);
        // SkyX takes the cosine with the vector from the dome point towards the eye.
        float cos = -Vector3.Dot(sun, ray);
        var c = (sum * SkyAtmosphere.InverseWaveLength4 * (SkyAtmosphere.KrESun * RayleighPhase(cos)) + sum * (SkyAtmosphere.KmESun * MiePhase(cos))) * SkyAtmosphere.Exposure;
        float night = Math.Clamp(1 - MathF.Max(c.X, MathF.Max(c.Y, c.Z)) * 10, 0, 1);
        if (skydome) night *= 1 - Math.Clamp(thickness * SkyAtmosphere.Kr4Pi, 0, 1);
        float glow = (2 - 0.75f * Math.Clamp(-sun.Y, 0, 1)) * MathF.Pow(1 - d.Y, 3);
        return c + night * new Vector3(MathF.Pow(0.05f * glow, 2.2f), MathF.Pow(0.05f * glow, 2.2f), MathF.Pow(0.1f * glow, 2.2f));
    }
}

/// <summary>
/// The game's distance haze, its full-screen atmosphere fog pass (docs/formats/sky.md "Haze"). The ramp and the scales are
/// Verified (kenshi_x64.exe sky controller and CONSTANTS loader, <c>data/materials/common/common.program</c>); the colour is SkyX's
/// Rayleigh in-scattering from the eye to the point (<see cref="Colour"/>).
/// </summary>
public static class KenshiHaze
{
    /// <summary>The <c>view distance</c> setting in the install's <c>settings.cfg</c> (Observed); its options slider is given 1500 and 12000, presumably the range (Observed).</summary>
    public const float ViewDistanceSetting = 5000;
    /// <summary>The game's far distance D: <c>view distance × 10</c>, also its far clip.</summary>
    public static float FarDistance(float viewDistance) => viewDistance * 10;
    /// <summary>The haze starts at <c>0.06 D</c> and is complete at <c>min(D, 0.6 D)</c> (pFogParams y and z).</summary>
    public const float StartFraction = 0.06f, EndFraction = 0.6f;
    /// <summary>World units per SkyX unit for the fog's ray (<c>uSkydomeRadius</c> of <c>SkyXFogParams</c>, never set at run time).</summary>
    public const float DomeRadius = 70000;
    /// <summary>The fog's eye height in SkyX units: SkyX's camera.</summary>
    public const float CameraY = SkyAtmosphere.CameraY;

    /// <summary>
    /// The fog's ray to a point at <paramref name="offset"/> (world units, point − eye): its direction (a ray steeper than −0.3 is replaced by
    /// a fixed one), its length in SkyX units (at most 1), and the unclamped direction (the phase function's). Close points below the
    /// eye are lifted towards its level (fully at the eye, not at all one dome radius away).
    /// </summary>
    public static (Vector3 Ray, float Length, Vector3 Direction) Ray(Vector3 offset)
    {
        var p = offset / DomeRadius;
        float y = p.Y + SkyAtmosphere.InnerRadius;
        float lift = Math.Clamp(p.X * p.X + p.Z * p.Z, 0, 1);
        y = MathF.Max(y, SkyAtmosphere.InnerRadius) + (y - MathF.Max(y, SkyAtmosphere.InnerRadius)) * lift;
        var ray = new Vector3(p.X, y - CameraY, p.Z);
        float len = ray.Length();
        if (len < 1e-9f) return (Vector3.UnitZ, 0, Vector3.UnitZ);
        ray /= len;
        var direction = ray;
        if (ray.Y < -0.3f) ray = new Vector3(0, -0.3f, 0.953f);
        return (ray, MathF.Min(len, 1), direction);
    }

    /// <summary>
    /// The haze's colour (HDR, before the clouds' pull): <c>exposure · rayleighPhase · invλ⁴ · KrESun ·</c> the in-scattering to the point.
    /// Mie light attenuates but adds no colour. <paramref name="sun"/>: unit direction towards the sun (its real height).
    /// </summary>
    public static Vector3 Colour(Vector3 offset, Vector3 sun)
    {
        var (ray, len, direction) = Ray(offset);
        float phase = SkyXModel.RayleighPhase(Vector3.Dot(sun, direction));
        return SkyXModel.InScatter(ray, len, sun) * SkyAtmosphere.InverseWaveLength4 * (SkyAtmosphere.KrESun * phase * SkyAtmosphere.Exposure);
    }

    /// <summary>
    /// <c>horizonClouds.a</c>: how far the haze is pulled towards the cloud colour, <c>saturate(densityOffset + 0.5)</c> with the
    /// cloud layer's density offset <c>1.4 c − 0.8</c> (c the weather's cloud density): 0 under a clear sky, 1 from c ≈ 0.93.
    /// </summary>
    public static float CloudPull(float cloudDensity) => Math.Clamp(1.4f * cloudDensity - 0.8f + 0.5f, 0, 1);
}
