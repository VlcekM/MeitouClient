using System.Numerics;

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
