namespace Meitou.Data.World;

/// <summary>
/// The camera-side ramps of wetness and dust (docs/formats/weather.md "Rain and wetness", "Dust"), one step per frame on the settling time base
/// (<see cref="FrameTimes.Settling"/>). Heat haze has its own in <see cref="HeatHaze"/>.
/// </summary>
public static class WeatherRamps
{
    /// <summary>Wetness rises by 0.01 per second while below the target.</summary>
    public const float WetnessRise = 0.01f;
    /// <summary>Wetness dries by 0.005 per second, only when the target is 0; with a target above the current wetness it rises, below it (a lighter rain) it holds.</summary>
    public const float WetnessDry = 0.005f;
    /// <summary>Dust rises by 0.017 × target per second (about a minute to full).</summary>
    public const float DustRise = 0.017f;
    /// <summary>Dust falls by 0.001 per second (about 17 minutes from 1 to 0).</summary>
    public const float DustFall = 0.001f;
    /// <summary>The slope value moves towards the weather's <c>dust slope</c> by 0.1 per second.</summary>
    public const float DustSlopeRate = 0.1f;

    public static float Wetness(float current, float target, float dt)
    {
        if (current < target) return MathF.Min(target, current + WetnessRise * dt);
        if (target == 0 && current > 0) return MathF.Max(0, current - WetnessDry * dt);
        return current;
    }

    /// <param name="target">The weather's <c>dust</c> × its strength.</param>
    public static float Dust(float current, float target, float dt)
    {
        if (current < target) return MathF.Min(target, current + DustRise * target * dt);
        if (current > target) return MathF.Max(target, current - DustFall * dt);
        return current;
    }

    public static float DustSlope(float current, float target, float dt)
    {
        float step = DustSlopeRate * dt;
        return MathF.Abs(target - current) <= step ? target : current + MathF.CopySign(step, target - current);
    }
}
