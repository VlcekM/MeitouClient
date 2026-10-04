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
/// The SkyX atmosphere settings the game creates its sky with (Verified, kenshi_x64.exe sky setup), and the numbers
/// SkyX's shaders derive from them (Verified against <c>data/materials/SkyX</c>: <c>uScale = 1 / (outer - inner)</c>,
/// <c>uScaleDepth = (outer - inner) / 2</c>, <c>uScaleOverScaleDepth = uScale / uScaleDepth</c>, the camera at
/// <c>inner + heightPosition · (outer - inner)</c>). The model itself is O'Neil's single scattering
/// (<see cref="AtmosphereModel"/>).
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
    /// The wavelengths the viewer's sky model uses: the vector the game's setup first builds. Not what the game sets (<see cref="GameWaveLength"/>);
    /// kept until the viewer's sky grading (<see cref="AtmosphereModel"/>) is redone for the game's green wavelength.
    /// </summary>
    public static readonly Vector3 WaveLength = new(0.57f, 0.54f, 0.44f);
    /// <summary>
    /// The wavelengths the game gives SkyX (Verified, kenshi_x64.exe sky setup: it builds (0.57, 0.54, 0.44), then stores 0.48 over its
    /// green component; SkyX_x64.dll's <c>AtmosphereManager::_update</c> reads that slot as the green wavelength).
    /// </summary>
    public static readonly Vector3 GameWaveLength = new(0.57f, 0.48f, 0.44f);
    public const float PhaseG = -0.991f;
    /// <summary>
    /// The exposure the game gives SkyX (Verified, the same sky setup). Earlier notes said 0.48,
    /// a misreading of the green wavelength's store. The viewer's sky has its own brightness calibration and does not use it.
    /// </summary>
    public const float GameExposure = 1.4f;
    public const int Samples = 4;

    /// <summary>Atmosphere thickness in SkyX units.</summary>
    public const float Thickness = OuterRadius - InnerRadius;
    /// <summary>SkyX's <c>uScaleDepth</c>: half the thickness.</summary>
    public const float ScaleDepth = Thickness / 2;
    /// <summary>Density scale height in SkyX units: <c>1 / uScaleOverScaleDepth = thickness² / 2</c>.</summary>
    public const float ScaleHeight = Thickness * Thickness / 2;
    /// <summary>Planet radius in scale heights.</summary>
    public const float PlanetRadius = InnerRadius / ScaleHeight;
    /// <summary>Atmosphere top radius in scale heights.</summary>
    public const float TopRadius = OuterRadius / ScaleHeight;
    /// <summary><c>1 / wavelength⁴</c> per channel (SkyX's <c>uInvWaveLength</c>).</summary>
    public static Vector3 InverseWaveLength4 => new(1 / MathF.Pow(WaveLength.X, 4), 1 / MathF.Pow(WaveLength.Y, 4), 1 / MathF.Pow(WaveLength.Z, 4));
    /// <summary>Rayleigh optical depth of a vertical ray from sea level to space, per channel (<c>Kr · 4π · invλ⁴ · scaleDepth</c>).</summary>
    public static Vector3 RayleighZenithDepth => InverseWaveLength4 * (RayleighMultiplier * 4 * MathF.PI * ScaleDepth);
    /// <summary>Mie optical depth of a vertical ray (<c>Km · 4π · scaleDepth</c>), before our turbidity factor.</summary>
    public const float MieZenithDepth = MieMultiplier * 4 * MathF.PI * ScaleDepth;
}

/// <summary>
/// The game's distance haze, its full-screen atmosphere fog pass (docs/formats/sky.md "Haze"). The ramp and the scales are
/// Verified (kenshi_x64.exe sky controller and CONSTANTS loader, <c>data/materials/common/common.program</c>); the colour is
/// O'Neil's in-scattering from the eye to the point, which the viewer uses as a fraction of the sky's colour
/// (<see cref="SkyFraction"/>).
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
    /// <summary>The fog's eye height in SkyX units: SkyX's camera, <c>inner + heightPosition · thickness</c>.</summary>
    public const float CameraY = SkyAtmosphere.InnerRadius + SkyAtmosphere.HeightPosition * SkyAtmosphere.Thickness;
    /// <summary>Extinction per unit of density-weighted path, per channel: <c>invλ⁴ · Kr · 4π + Km · 4π</c> with the game's wavelengths.</summary>
    public static Vector3 Extinction
    {
        get
        {
            var w = SkyAtmosphere.GameWaveLength;
            var inv = new Vector3(1 / MathF.Pow(w.X, 4), 1 / MathF.Pow(w.Y, 4), 1 / MathF.Pow(w.Z, 4));
            return inv * (SkyAtmosphere.RayleighMultiplier * 4 * MathF.PI) + new Vector3(SkyAtmosphere.MieMultiplier * 4 * MathF.PI);
        }
    }

    /// <summary>
    /// The direction and length (SkyX units, at most 1) of the fog's ray to a point at <paramref name="offset"/> (world units,
    /// point − eye). Close points below the eye are lifted towards its level (fully at the eye, not at all one dome radius
    /// away), and a ray steeper than −0.3 is replaced by a fixed one.
    /// </summary>
    public static (Vector3 Ray, float Length) Ray(Vector3 offset)
    {
        var p = offset / DomeRadius;
        float y = p.Y + SkyAtmosphere.InnerRadius;
        float lift = Math.Clamp(p.X * p.X + p.Z * p.Z, 0, 1);
        y = MathF.Max(y, SkyAtmosphere.InnerRadius) + (y - MathF.Max(y, SkyAtmosphere.InnerRadius)) * lift;
        var ray = new Vector3(p.X, y - CameraY, p.Z);
        float len = ray.Length();
        if (len < 1e-9f) return (Vector3.UnitZ, 0);
        ray /= len;
        if (ray.Y < -0.3f) ray = new Vector3(0, -0.3f, 0.953f);
        return (ray, MathF.Min(len, 1));
    }

    /// <summary>O'Neil's scale function (SkyX's fit of the optical depth towards the top of the air at a zenith cosine).</summary>
    static float Scale(float cos)
    {
        float x = 1 - cos;
        return SkyAtmosphere.ScaleDepth * MathF.Exp(-0.00287f + x * (0.459f + x * (3.83f + x * (-6.80f + x * 5.25f))));
    }

    /// <summary>
    /// Rayleigh in-scattering along <paramref name="ray"/> from the fog's eye for <paramref name="length"/> SkyX units, 4 samples,
    /// before the per-channel colour, phase and exposure factors (they are the same for the haze and the sky, so they cancel
    /// in <see cref="SkyFraction"/>). <paramref name="sun"/>: unit direction towards the sun.
    /// </summary>
    public static Vector3 InScatter(Vector3 ray, float length, Vector3 sun)
    {
        float sosd = 1 / SkyAtmosphere.ScaleHeight, scale = 1 / SkyAtmosphere.Thickness;
        var start = new Vector3(0, CameraY, 0);
        float startOffset = MathF.Exp(sosd * (SkyAtmosphere.InnerRadius - CameraY)) * Scale(Vector3.Dot(ray, start) / CameraY);
        float step = length / SkyAtmosphere.Samples;
        var ext = Extinction;
        var sum = Vector3.Zero;
        for (int i = 0; i < SkyAtmosphere.Samples; i++)
        {
            var p = start + ray * (step * (i + 0.5f));
            float h = p.Length(), density = MathF.Exp(sosd * (SkyAtmosphere.InnerRadius - h));
            float optical = startOffset + density * (Scale(Vector3.Dot(sun, p) / h) - Scale(Vector3.Dot(ray, p) / h));
            var t = new Vector3(MathF.Exp(-optical * ext.X), MathF.Exp(-optical * ext.Y), MathF.Exp(-optical * ext.Z));
            sum += t * (density * step * scale);
        }
        return sum;
    }

    /// <summary>
    /// The haze's colour as a fraction of the sky's along the same ray (the sky is the same integral to the dome, length 1):
    /// 1 at and past <see cref="DomeRadius"/>, less nearer. Clamped to 0..1; 0 when both vanish (the sun far below the
    /// horizon: the game's fog colour is then black too). The guard is the viewer's.
    /// </summary>
    public static Vector3 SkyFraction(Vector3 offset, Vector3 sun)
    {
        var (ray, len) = Ray(offset);
        var part = InScatter(ray, len, sun);
        var full = InScatter(ray, 1, sun);
        static float F(float a, float b) => b > 1e-20f && float.IsFinite(a / b) ? Math.Clamp(a / b, 0, 1) : 0;
        return new Vector3(F(part.X, full.X), F(part.Y, full.Y), F(part.Z, full.Z));
    }

    /// <summary>
    /// <c>horizonClouds.a</c>: how far the haze is pulled towards the cloud colour, <c>saturate(densityOffset + 0.5)</c> with the
    /// cloud layer's density offset <c>1.4 c − 0.8</c> (c the weather's cloud density): 0 under a clear sky, 1 from c ≈ 0.93.
    /// </summary>
    public static float CloudPull(float cloudDensity) => Math.Clamp(1.4f * cloudDensity - 0.8f + 0.5f, 0, 1);
}
