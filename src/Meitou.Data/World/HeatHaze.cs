namespace Meitou.Data.World;

/// <summary>
/// The game's heat-haze amount and time base (docs/formats/weather.md "Heat haze", docs/formats/post-processing.md "Heat haze"):
/// the shared <c>heatHaze</c> moves towards the camera region's WEATHER <c>heat haze</c> × strength × saturate(6 · sunY) at 1/3 per
/// second, and the shader's animation runs on <c>gameTime</c> (game hours since the load) × 100.
/// </summary>
public static class HeatHaze
{
    /// <summary>Per second (of the game-speed-scaled frame time) that <c>heatHaze</c> moves towards its target.</summary>
    public const float Rate = 1f / 3f;

    /// <summary>Game hours per real second at game speed 1 (SkyX's time multiplier 0.0091666…; docs/game/game-loop.md).</summary>
    public const double HoursPerSecond = 11.0 / 1200.0;

    /// <summary>The value <c>heatHaze</c> moves towards: the weather's <c>heat haze</c> field × its strength s × saturate(6 · sunY).</summary>
    public static float Target(float weatherHeatHaze, float strength, float sunY) => weatherHeatHaze * strength * Math.Clamp(6 * sunY, 0, 1);

    /// <summary>One frame's step: by at most <see cref="Rate"/> × dt towards the target, landing on it when closer.</summary>
    public static float Step(float current, float target, float dt)
    {
        float step = dt * Rate;
        return MathF.Abs(target - current) <= step ? target : current + MathF.CopySign(step, target - current);
    }
}
