using System.Numerics;

namespace Meitou.Game.Rendering.Water;

/// <summary>
/// The Meitou water's clock-driven parameters besides the open waves (<see cref="OceanWaves"/>; <see cref="WaterRenderer"/>, docs/render-water.md
/// "Meitou water"): the shore's breakers and the normal maps' scrolling. Visual only: the game's water stays a flat plane at Y = 100.
/// Everything runs on the game clock, so it stands still while paused and speeds up with the game speed. The shore waves follow the game clock
/// frame by frame and their size follows the wind over about ten seconds, so a change of weather never makes them jump.
/// Units are the world's (1 unit = 1 dm); seconds are game seconds at speed 1 (1 game hour = 109.09 s, docs/game/game-loop.md).
/// </summary>
public sealed class WaveSet
{
    /// <summary>
    /// Real seconds per game hour at game speed 1 (docs/game/game-loop.md "Day length").
    /// </summary>
    public const double SecondsPerGameHour = 1200.0 / 11.0;

    /// <summary>
    /// The shore waves: one every <see cref="BreakerPeriod"/> seconds on average (in sets of seven; the shader's waveCount, docs/render-water.md
    /// "Shore waves").
    /// </summary>
    public const float BreakerPeriod = 8;

    /// <summary>
    /// The game hours of the last <see cref="Step"/> (null: none yet, the next takes its values at once).
    /// </summary>
    double? _lastHours;

    /// <summary>
    /// The smoothed wind factor.
    /// </summary>
    float _wind;

    /// <summary>
    /// The shore waves' clock wraps here: 4096 sets of seven waves, so a wave's number (mod 4096, the shader's per-wave noise) and its set do not
    /// jump.
    /// </summary>
    public const double ClockWrap = 4096.0 * 7 * BreakerPeriod;

    /// <summary>
    /// x the shore waves' clock (game seconds, below <see cref="ClockWrap"/>), y the run-up height, z the mean wave period (s), w the waves' height
    /// (units).
    /// </summary>
    public Vector4 Shore;

    /// <summary>
    /// The game-clock time for the normal maps' scrolling, in the unit the Faithful water's time has (seconds / 600).
    /// </summary>
    public float NormalTime;

    /// <summary>
    /// The wind factor: 0 calm, 1 at 60 units per second (a stiff breeze), at most 1.5.
    /// </summary>
    public static float WindFactor(float windSpeed) => Math.Clamp(windSpeed / 60f, 0f, 1.5f);

    /// <summary>
    /// Usable values before the first frame, which then takes its wind at once.
    /// </summary>
    public WaveSet()
    {
        Step(0, Vector2.UnitX, 0);
        _lastHours = null;
    }

    /// <summary>
    /// Advances the breakers and the normals' time to <paramref name="gameHours"/> (a step is at most 1 game second; the wind direction is unused).
    /// </summary>
    public void Step(double gameHours, Vector2 windDirection, float windSpeed)
    {
        _ = windDirection;

        double dt = _lastHours is { } last ? Math.Clamp((gameHours - last) * SecondsPerGameHour, 0, 1) : 0;
        bool first = _lastHours is null;
        _lastHours = gameHours;

        double seconds = gameHours * SecondsPerGameHour;
        NormalTime = (float)(seconds / 600 % 1e4);

        float target = WindFactor(windSpeed);
        _wind = first ? target : _wind + (target - _wind) * (float)(1 - Math.Exp(-dt / 10));

        float swell = 0.6f + 0.6f * MathF.Min(_wind, 1.5f);
        Shore = new Vector4((float)(seconds % ClockWrap), 2.2f * swell, BreakerPeriod, 3.5f * swell);
    }
}
