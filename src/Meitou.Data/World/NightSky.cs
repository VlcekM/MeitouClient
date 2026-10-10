using System.Numerics;
using Meitou.Data.Textures;

namespace Meitou.Data.World;

/// <summary>
/// SkyX's starfield as Kenshi lays it on the dome (docs/formats/sky.md "Stars"): the dome's texture coordinates are an azimuthal-equidistant
/// map round the zenith, scaled by 0.1 in the skydome shader, and the shader shifts them along both axes by SkyX's time × 0.05. Kenshi runs
/// that time at the game's hours since the load. Verified: <c>SkyX_x64.dll</c> <c>MeshManager::updateGeometry</c> and <c>SkyX::update</c>,
/// <c>SkyX_Skydome.hlsl</c>, and the sky update (<c>kenshi_x64.exe</c> FUN_14066f190) setting the time multiplier.
/// </summary>
public static class Starfield
{
    /// <summary>The texture coordinate of the zenith (the dome's 4 × the shader's 0.1); the horizon is a circle of this radius round it.</summary>
    public const float Centre = 0.4f, Radius = 0.4f;
    /// <summary>Texture units the starfield moves along u and along v per game hour (<c>uTime · 0.1</c>, <c>uTime</c> = SkyX's time × 0.5).</summary>
    public const float ShiftPerHour = 0.05f;

    /// <summary>
    /// The starfield's texture coordinate (wrapped by the sampler) in the dome direction <paramref name="direction"/> (normalised, y up) after
    /// <paramref name="hours"/> game hours: <c>0.4 (1 + t · normalize(d.xz)) + 0.05 · hours</c> with <c>t</c> the zenith angle over 90°.
    /// </summary>
    public static Vector2 Uv(Vector3 direction, double hours)
    {
        float t = MathF.Acos(Math.Clamp(direction.Y, -1, 1)) / (MathF.PI / 2);
        var xz = new Vector2(direction.X, direction.Z);
        var around = xz.LengthSquared() > 1e-12f ? Vector2.Normalize(xz) : Vector2.Zero;
        return new Vector2(Centre) + Radius * t * around + new Vector2(Shift(hours));
    }

    /// <summary>The shift along both axes after <paramref name="hours"/>, wrapped to 0..1 (the texture repeats).</summary>
    public static float Shift(double hours)
    {
        double s = hours * ShiftPerHour;
        return (float)(s - Math.Floor(s));
    }
}

/// <summary>
/// One of the two planets the game hangs in its sky (docs/formats/sky.md "Planets"): <c>planet01.mesh</c> with the material
/// <paramref name="Material"/> (<c>materials/forward/moon.material</c>: the texture lit by the sun, plus the sky's Rayleigh colour), placed in
/// the fixed world direction <paramref name="Direction"/> from the camera, scaled by <paramref name="Scale"/>, turning about its own y axis by
/// <paramref name="SpinPerDay"/> radians per game day. Verified: the sky creation (<c>kenshi_x64.exe</c> FUN_14066e380), the planet's
/// constructor FUN_14066c920 and its per-frame update FUN_140670ed0.
/// </summary>
public sealed record SkyPlanet(string Material, string Texture, Vector3 Direction, float Scale, float SpinPerDay)
{
    /// <summary>The radius of every vertex of <c>planet01.mesh</c> (a UV sphere).</summary>
    public const float MeshRadius = 25.8721f;
    /// <summary>The update places the planet <c>½ (near + far) (1 − tan 0.01°)</c> from the camera and scales it by <c>Scale × that × tan 0.01°</c>.</summary>
    public static readonly float Tan001 = MathF.Tan(0.01f * MathF.PI / 180);
    /// <summary>The mesh's u at longitude 0: <c>u = 0.2685 − atan2(z, x) / 2π</c>, <c>v = acos(y) / π</c> (fitted to every vertex of the mesh, exact).</summary>
    public const float UOffset = 0.2685f;

    /// <summary>The unit vector towards the planet's centre.</summary>
    public Vector3 Towards => Vector3.Normalize(Direction);

    /// <summary>The sine of the planet's angular radius: the sphere's radius over its distance, <c>MeshRadius · Scale · tan 0.01°</c> whatever the clip planes.</summary>
    public float SinRadius => MeshRadius * Scale * Tan001;

    /// <summary>The angular radius in radians.</summary>
    public float AngularRadius => MathF.Asin(SinRadius);

    /// <summary>
    /// The turn about y after <paramref name="day"/> days and <paramref name="hour"/> hours: the update takes the game's whole minutes,
    /// <c>((day · 24 + hour) · 60 + minute) · SpinPerDay / 1440</c>. Wrapped to 0..2π.
    /// </summary>
    public float Spin(int day, float hour)
    {
        double minutes = (day * 24.0 + Math.Floor(hour)) * 60 + Math.Floor((hour - Math.Floor(hour)) * 60);
        double a = minutes * SpinPerDay / 1440;
        return (float)(a - Math.Floor(a / (2 * Math.PI)) * 2 * Math.PI);
    }

    /// <summary>The mesh's texture coordinate at the unit object-space normal <paramref name="n"/>.</summary>
    public static Vector2 Uv(Vector3 n) =>
        new(UOffset - MathF.Atan2(n.Z, n.X) / (2 * MathF.PI), MathF.Acos(Math.Clamp(n.Y, -1, 1)) / MathF.PI);

    /// <summary>"Moon" (<c>moon_HI.dds</c>, 4096 × 2048), the large one, and "Moon2" (<c>moon2_HI.dds</c>, 2048 × 1024), in the sky creation's order.</summary>
    public static readonly SkyPlanet[] All =
    [
        new("Moon", "moon_HI.dds", new Vector3(1, 0.3f, -1), 35, 1),
        new("Moon2", "moon2_HI.dds", new Vector3(1, 0.14f, -0.7f), 10, 4.7310f),
    ];
}

/// <summary>
/// Meitou's <c>planetshine</c> (docs/formats/sky.md "Planetshine"; the viewer's own feature, the game has nothing like it): the big planet as the
/// night's light. The planet is a Lambert sphere lit by the sun, so it sends the land the fraction
/// <c>E / E_sun = A · (Ω / π) · Φ(α)</c> of the sun's irradiance: <c>A</c> its mean albedo, <c>Ω = 2π (1 − cos r)</c> its solid angle, <c>Φ</c> the
/// Lambert phase function of the phase angle <c>α</c> (sun–planet–observer). The viewer multiplies the ratio by the sun's light in the game's
/// own units (<see cref="KenshiLighting.SunLight"/>) and a strength. Moon2 (an eighth of the solid angle) is left out.
/// </summary>
public static class Planetshine
{
    /// <summary>
    /// Below this sun height the planet is the only light (the game's sun light is zero from −0.093: <see cref="KenshiLighting.Daylight"/>); above
    /// <see cref="NoneAbove"/> it adds nothing; smoothstep between, so the planet comes in as the sun's light fades.
    /// </summary>
    public const float FullBelow = -0.09f, NoneAbove = 0.06f;

    /// <summary>The mean albedo (grey) assumed when the planet's texture is missing: Unknown, a plausible rocky world.</summary>
    public const float FallbackAlbedo = 0.3f;

    /// <summary>The first mip level at most this wide is averaged for the albedo (a 4096-wide map: level 5, 128 × 64).</summary>
    const int AlbedoWidth = 128;

    /// <summary>
    /// The phase angle in radians: at the planet, between the directions to the sun and to the observer. Both are far away, so the sun's direction
    /// <paramref name="sun"/> (from the observer) is also the direction from the planet, and the observer lies in <c>−towardsPlanet</c>:
    /// <c>cos α = −sun · towardsPlanet</c>. 0 with the sun behind the observer (a full planet), π with the sun behind the planet (new).
    /// </summary>
    public static float PhaseAngle(Vector3 sun, Vector3 towardsPlanet) =>
        MathF.Acos(Math.Clamp(-Vector3.Dot(Vector3.Normalize(sun), Vector3.Normalize(towardsPlanet)), -1, 1));

    /// <summary>The Lambert sphere's phase function, <c>(sin α + (π − α) cos α) / π</c>: 1 at α = 0, 0 at α = π.</summary>
    public static float LambertPhase(float alpha) => (MathF.Sin(alpha) + (MathF.PI - alpha) * MathF.Cos(alpha)) / MathF.PI;

    /// <summary>The solid angle in steradians of a disc of angular radius <paramref name="radius"/> (radians): <c>2π (1 − cos r)</c>.</summary>
    public static float SolidAngle(float radius) => 2 * MathF.PI * (1 - MathF.Cos(radius));

    /// <summary>The planet's irradiance on a surface facing it, over the sun's: <c>albedo · Ω / π · Φ(α)</c>.</summary>
    public static float IrradianceRatio(float albedo, float solidAngle, float alpha) => albedo * solidAngle / MathF.PI * LambertPhase(alpha);

    /// <summary>
    /// The planet's light in the unit of <paramref name="sunLight"/> (the game's <c>uAtmoSunLight</c> scale), for the sun in direction <paramref name="sun"/>:
    /// <c>sunLight · albedo · Ω / π · Φ(α) · strength</c>, the albedo per colour channel.
    /// </summary>
    public static Vector3 Light(Vector3 sun, SkyPlanet planet, Vector3 albedo, float sunLight, float strength) =>
        sunLight * strength * albedo * IrradianceRatio(1, SolidAngle(planet.AngularRadius), PhaseAngle(sun, planet.Towards));

    /// <summary>
    /// How much of the planet's light the lighting takes at the sun's height <paramref name="sunY"/>: 1 up to <see cref="FullBelow"/>, 0 from
    /// <see cref="NoneAbove"/>, smoothstep between.
    /// </summary>
    public static float Weight(float sunY)
    {
        float t = Math.Clamp((sunY - FullBelow) / (NoneAbove - FullBelow), 0, 1);
        return 1 - t * t * (3 - 2 * t);
    }

    /// <summary>
    /// The one directional light that stands for the sun and the planet together: the colours add, the direction is the two directions
    /// weighted by the square root of their luminance (it follows whichever is brighter, and the square root, against the luminance itself, spreads
    /// the swing over twice as much of the sun's fade so the shadows turn rather than snap). With no planet light the sun's direction and colour
    /// come back unchanged. Both directions are unit vectors.
    /// </summary>
    public static (Vector3 Direction, Vector3 Light) Combine(Vector3 sunDirection, Vector3 sunLight, Vector3 planetDirection, Vector3 planetLight)
    {
        float s = Luminance(sunLight), p = Luminance(planetLight);
        if (p <= 0) return (sunDirection, sunLight);
        var d = sunDirection * MathF.Sqrt(s) + planetDirection * MathF.Sqrt(p);
        return (d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : planetDirection, sunLight + planetLight);
    }

    /// <summary>Rec. 709 luminance of a linear colour.</summary>
    public static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    /// <summary>
    /// The mean colour of an equirectangular planet map as the sky shader samples it (raw texel values 0..1, not linearised): each row weighted by
    /// <c>sin(v · π)</c>, the sphere's area in it, so the poles do not count more than the equator.
    /// </summary>
    public static Vector3 MeanAlbedo(RgbaImage map)
    {
        double r = 0, g = 0, b = 0, total = 0;
        for (int y = 0; y < map.Height; y++)
        {
            double w = Math.Sin(Math.PI * (y + 0.5) / map.Height);
            for (int x = 0; x < map.Width; x++)
            {
                int i = (y * map.Width + x) * 4;
                r += map.Pixels[i] * w; g += map.Pixels[i + 1] * w; b += map.Pixels[i + 2] * w;
                total += w;
            }
        }
        return total > 0 ? new Vector3((float)(r / total), (float)(g / total), (float)(b / total)) / 255f : new Vector3(FallbackAlbedo);
    }

    /// <summary>The mean albedo of a planet DDS, from the first level at most 128 wide (see <see cref="MeanAlbedo(RgbaImage)"/>).</summary>
    public static Vector3 MeanAlbedo(DdsFile dds)
    {
        int level = 0;
        while (level < dds.MipCount - 1 && (dds.Width >> level) > AlbedoWidth) level++;
        return MeanAlbedo(DdsDecoder.Decode(dds, 0, level));
    }
}
