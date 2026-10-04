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
/// The SkyX atmosphere settings the game creates its sky with (Verified, kenshi_x64.exe sky setup). Kept as facts;
/// the viewer's sky is a simpler model of its own.
/// </summary>
public static class SkyAtmosphere
{
    public const float InnerRadius = 9.77501f;
    public const float OuterRadius = 10.2963f;
    public const float HeightPosition = 0.01f;
    public const float RayleighMultiplier = 0.0022f;
    public const float MieMultiplier = 0.000675f;
    public const float SunIntensity = 30f;
    public static readonly Vector3 WaveLength = new(0.57f, 0.54f, 0.44f);
    public const float PhaseG = -0.991f;
    public const float Exposure = 0.48f;
    public const int Samples = 4;
}
