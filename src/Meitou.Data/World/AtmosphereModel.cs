using System.Numerics;

namespace Meitou.Data.World;

/// <summary>
/// Our knobs on top of the game's SkyX constants (<see cref="SkyAtmosphere"/>); everything here is the viewer's choice,
/// not game data (docs/formats/sky.md).
/// </summary>
public sealed record AtmosphereSettings
{
    /// <summary>
    /// World units in one density scale height of the air. The game's unit is Unknown (decimetres most likely, which would make
    /// 80000 an 8 km scale height, like the Earth's).
    /// </summary>
    public float ScaleHeightUnits { get; init; } = 40000;
    /// <summary>Aerosol (Mie) amount: a multiple of the game's Mie coefficient. SkyX's own is a very clear air; Kenshi's look is hazier.</summary>
    public float Turbidity { get; init; } = 15;
    /// <summary>Aerosol scale height relative to the air's (aerosols sit lower).</summary>
    public float MieScaleHeight { get; init; } = 0.3f;
    /// <summary>Anisotropy of the aerosol phase function (the game's −0.991 lobe is far too narrow for haze).</summary>
    public float MieG { get; init; } = 0.75f;
    /// <summary>Sun irradiance as a multiple of π: the sunlit side of a white diffuse surface has radiance <c>SunScale · transmittance · cos</c>.</summary>
    public float SunScale { get; init; } = 1.2f;
    /// <summary>Multiplier on the Rayleigh scattered radiance, standing in for the multiple scattering a single-scattering model lacks (aerosol light is left alone).</summary>
    public float SkyGain { get; init; } = 3.4f;

    /// <summary>
    /// Ozone absorption (peak coefficient per scale height; a tent-shaped layer centred 2 scale heights up, 1.4 wide each side).
    /// Not in SkyX. It takes out green and yellow from the long slant paths of a low sun, which turns the twilight from yellow-olive
    /// into orange and deep blue (Hillaire's sky model has the same term, with the Earth's ozone absorption spectrum).
    /// </summary>
    public Vector3 OzoneAbsorption { get; init; } = new(0.010f, 0.030f, 0.0013f);
    public const float OzoneCentre = 2.0f, OzoneHalfWidth = 1.4f;

    public Vector3 RayleighDepth => SkyAtmosphere.RayleighZenithDepth;
    public float MieDepth => SkyAtmosphere.MieZenithDepth * Turbidity;
}

/// <summary>
/// CPU version of the sky model: O'Neil's single scattering as SkyX's skydome shader does it (Rayleigh and Mie in-scatter along
/// the view ray, attenuated by the optical depth to the camera and to the sun), but with the exact planet and atmosphere
/// geometry and a separate aerosol scale height. Lengths are in density scale heights; <c>r</c> is the distance from the
/// planet's centre. The GPU version in <c>AtmosphereShaders</c> computes the same integral with lookup tables; this one gives the
/// sun's light, the ambient light and the tests. Radiances are linear.
/// </summary>
public sealed class AtmosphereModel(AtmosphereSettings settings)
{
    public const float PlanetRadius = SkyAtmosphere.PlanetRadius, TopRadius = SkyAtmosphere.TopRadius;
    public AtmosphereSettings Settings { get; } = settings;

    /// <summary>Gamma that turns radiance into the display-referred values the viewer's lighting works in.</summary>
    public const float DisplayGamma = 2.2f;

    /// <summary>
    /// Radiance to the display-referred values the viewer lights with: half per-channel gamma (what SkyX's HDR path does, which
    /// pales and greys the sky), half the same curve on the luminance only (keeps the hue, so sunsets stay orange).
    /// </summary>
    public static Vector3 Encode(Vector3 linear)
    {
        var l = Vector3.Max(linear, Vector3.Zero);
        var perChannel = new Vector3(MathF.Pow(l.X, 1 / DisplayGamma), MathF.Pow(l.Y, 1 / DisplayGamma), MathF.Pow(l.Z, 1 / DisplayGamma));
        float lum = 0.2126f * l.X + 0.7152f * l.Y + 0.0722f * l.Z;
        if (lum < 1e-9f) return perChannel;
        var c = Vector3.Lerp(perChannel, l * (MathF.Pow(lum, 1 / DisplayGamma) / lum), HuePreserved);
        // Only blue-dominated colours are graded, so orange sunsets keep their colour.
        float cool = Math.Clamp(((c.Z - c.X) / MathF.Max(c.Z, 1e-3f) - 0.05f) / 0.25f, 0, 1);
        c *= Vector3.Lerp(Vector3.One, WhiteBalance, cool);
        return Vector3.Lerp(c, new Vector3(Vector3.Dot(c, new Vector3(0.2126f, 0.7152f, 0.0722f))), Desaturation * cool);
    }
    public const float HuePreserved = 0.5f;

    /// <summary>
    /// The viewer's grading of the sky and haze, not game data: with the game's Rayleigh wavelengths (0.57, 0.54, 0.44) red
    /// scatters almost as much as green, so single scattering alone gives a violet sky. A slight shift towards green and a
    /// partial desaturation, both scaled by how blue-dominated the colour is (so sunsets keep their orange), give the pale,
    /// hazy blue Kenshi shows.
    /// </summary>
    public static readonly Vector3 WhiteBalance = new(0.92f, 1.04f, 0.97f);
    public const float Desaturation = 0.45f;

    /// <summary>Distance along a ray from radius r with cosine mu (to the local vertical) to the top of the atmosphere.</summary>
    public static float DistanceToTop(float r, float mu) => MathF.Max(-r * mu + MathF.Sqrt(MathF.Max(r * r * (mu * mu - 1) + TopRadius * TopRadius, 0)), 0);

    public static bool HitsPlanet(float r, float mu) => mu < 0 && r * r * (mu * mu - 1) + PlanetRadius * PlanetRadius >= 0;

    static float OzoneDensity(float altitude) => MathF.Max(1 - MathF.Abs(altitude - AtmosphereSettings.OzoneCentre) / AtmosphereSettings.OzoneHalfWidth, 0);

    /// <summary>Optical depth per channel along a ray of the given length.</summary>
    public Vector3 OpticalDepth(float r, float mu, float length, int steps = 32)
    {
        var rayleigh = Settings.RayleighDepth;
        float mie = Settings.MieDepth, mieH = Settings.MieScaleHeight;
        Vector3 sum = default;
        float ds = length / steps;
        for (int i = 0; i < steps; i++)
        {
            float s = (i + 0.5f) * ds;
            float alt = MathF.Sqrt(r * r + 2 * r * mu * s + s * s) - PlanetRadius;
            sum += (rayleigh * MathF.Exp(-alt) + new Vector3(mie * MathF.Exp(-alt / mieH)) + Settings.OzoneAbsorption * OzoneDensity(alt)) * ds;
        }
        return sum;
    }

    /// <summary>Transmittance from radius r along mu to the top of the atmosphere; zero when the planet is in the way.</summary>
    public Vector3 Transmittance(float r, float mu, int steps = 32)
    {
        if (HitsPlanet(r, mu)) return Vector3.Zero;
        var depth = OpticalDepth(r, mu, DistanceToTop(r, mu), steps);
        return new Vector3(MathF.Exp(-depth.X), MathF.Exp(-depth.Y), MathF.Exp(-depth.Z));
    }

    /// <summary>The colour of sunlight reaching an eye at the given altitude (scale heights), as <see cref="AtmosphereSettings.SunScale"/> times transmittance.</summary>
    public Vector3 SunLight(float altitude, Vector3 sun) => Transmittance(PlanetRadius + altitude, sun.Y) * Settings.SunScale;

    /// <summary>Cornette-Shanks phase function, averaging 1 over the sphere (SkyX's <c>miePhase</c> form); cos is towards the sun.</summary>
    public static float MiePhase(float cos, float g) => 1.5f * ((1 - g * g) / (2 + g * g)) * (1 + cos * cos) / MathF.Pow(1 + g * g - 2 * g * cos, 1.5f);

    /// <summary>SkyX's Rayleigh phase function (<c>0.75 (1 + 0.5 cos²)</c>).</summary>
    public static float RayleighPhase(float cos) => 0.75f * (1 + 0.5f * cos * cos);

    /// <summary>Linear sky radiance seen from the altitude (scale heights) along <paramref name="view"/>, with the sun at <paramref name="sun"/>.</summary>
    public Vector3 SkyRadiance(float altitude, Vector3 view, Vector3 sun, int steps = 32)
    {
        float r0 = PlanetRadius + altitude;
        float mu = view.Y;
        float tMax = DistanceToTop(r0, mu);
        if (HitsPlanet(r0, mu))
        {
            float b = r0 * mu, disc = b * b - (r0 * r0 - PlanetRadius * PlanetRadius);
            tMax = -b - MathF.Sqrt(MathF.Max(disc, 0));
        }
        float cos = Vector3.Dot(view, sun);
        float pr = RayleighPhase(cos), pm = MiePhase(cos, Settings.MieG);
        var rayleigh = Settings.RayleighDepth;
        float mie = Settings.MieDepth, mieH = Settings.MieScaleHeight;
        Vector3 depthToEye = default, sum = default;
        var p0 = new Vector3(0, r0, 0);
        float previous = 0;
        for (int i = 0; i < steps; i++)
        {
            float f = (i + 0.5f) / steps, f1 = (i + 1f) / steps;
            float t = tMax * f * f, end = tMax * f1 * f1, ds = end - previous;
            previous = end;
            var p = p0 + view * t;
            float r = p.Length(), alt = r - PlanetRadius;
            var sigma = rayleigh * MathF.Exp(-alt) + new Vector3(mie * MathF.Exp(-alt / mieH)) + Settings.OzoneAbsorption * OzoneDensity(alt);
            var scatter = rayleigh * (MathF.Exp(-alt) * pr * Settings.SkyGain) + new Vector3(mie * MathF.Exp(-alt / mieH) * pm);
            var step = sigma * ds;
            var transToEye = new Vector3(MathF.Exp(-(depthToEye.X + step.X * 0.5f)), MathF.Exp(-(depthToEye.Y + step.Y * 0.5f)), MathF.Exp(-(depthToEye.Z + step.Z * 0.5f)));
            depthToEye += step;
            var sunT = Transmittance(r, Vector3.Dot(p, sun) / r, 16);
            sum += scatter * transToEye * sunT * ds;
        }
        return sum * (Settings.SunScale / 4);
    }

    /// <summary>
    /// Ambient light from the sky: the display-referred sky colour averaged over the upper hemisphere (cosine weighted), and the
    /// colour at the horizon around the sun.
    /// </summary>
    public (Vector3 Upper, Vector3 Horizon, Vector3 Zenith) SkyAverages(float altitude, Vector3 sun)
    {
        float[] elevations = [12, 35, 60, 82];
        Vector3 sum = default;
        float weights = 0;
        // Azimuths measured from the sun's own, so the sun side and the far side weigh equally.
        var sunH = new Vector2(sun.X, sun.Z);
        float sunAz = sunH.LengthSquared() > 1e-6f ? MathF.Atan2(sunH.Y, sunH.X) : 0;
        foreach (float elevationDegrees in elevations)
        {
            float e = elevationDegrees * MathF.PI / 180, w = MathF.Sin(e);
            const int azimuths = 4;
            for (int a = 0; a < azimuths; a++)
            {
                float az = sunAz + (a + 0.5f) * 2 * MathF.PI / azimuths;
                var d = new Vector3(MathF.Cos(e) * MathF.Cos(az), MathF.Sin(e), MathF.Cos(e) * MathF.Sin(az));
                sum += Encode(SkyRadiance(altitude, d, sun, 16)) * w;
                weights += w;
            }
        }
        var zenith = Encode(SkyRadiance(altitude, Vector3.UnitY, sun, 16));
        // Horizon: average over four azimuths a little above the horizon.
        Vector3 horizon = default;
        for (int a = 0; a < 4; a++)
        {
            float az = sunAz + a * MathF.PI / 2, e = 3 * MathF.PI / 180;
            horizon += Encode(SkyRadiance(altitude, new Vector3(MathF.Cos(e) * MathF.Cos(az), MathF.Sin(e), MathF.Cos(e) * MathF.Sin(az)), sun, 24)) / 4;
        }
        return (sum / weights, horizon, zenith);
    }
}
