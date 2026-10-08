using System.Numerics;

namespace Meitou.Rendering;

/// <summary>
/// The Meitou water's clock-driven parameters besides the open waves (<see cref="OceanWaves"/>; <see cref="WaterRenderer"/>, docs/viewer.md
/// "Meitou water"): the shore's breakers and the normal maps' scrolling. Visual only: the game's water stays a flat plane at Y = 100.
/// Everything runs on the game clock, so it stands still while paused and speeds up with the game speed. The breakers' phase is integrated
/// frame by frame and their size follows the wind over about ten seconds, so a change of weather never makes them jump.
/// Units are the world's (1 unit = 1 dm); seconds are game seconds at speed 1 (1 game hour = 109.09 s, docs/game/game-loop.md).
/// </summary>
public sealed class WaveSet
{
    /// <summary>Real seconds per game hour at game speed 1 (docs/game/game-loop.md "Day length").</summary>
    public const double SecondsPerGameHour = 1200.0 / 11.0;

    /// <summary>The breakers: one every <see cref="BreakerPeriod"/> seconds, <see cref="BreakerLength"/> units apart.</summary>
    public const float BreakerPeriod = 8, BreakerLength = 110;

    double? lastHours;
    float wind;   // the smoothed wind factor
    double shorePhase;

    /// <summary>The wave count wraps here; the shader's per-wave noise and drift are periodic in it (a wave's number is the count at its place).</summary>
    public const double PhaseWrap = 4096;

    /// <summary>x the breakers' wave count (cycles since the start, below <see cref="PhaseWrap"/>; the shader takes its fraction as the phase), y the run-up height, z the breakers' wavelength, w their height (units).</summary>
    public Vector4 Shore;
    /// <summary>The game-clock time for the normal maps' scrolling, in the unit the Faithful water's time has (seconds / 600).</summary>
    public float NormalTime;

    /// <summary>The wind factor: 0 calm, 1 at 60 units per second (a stiff breeze), at most 1.5.</summary>
    public static float WindFactor(float windSpeed) => Math.Clamp(windSpeed / 60f, 0f, 1.5f);

    public WaveSet() { Step(0, Vector2.UnitX, 0); lastHours = null; }   // usable values before the first frame, which then takes its wind at once

    public void Step(double gameHours, Vector2 windDirection, float windSpeed)
    {
        _ = windDirection;
        double dt = lastHours is { } last ? Math.Clamp((gameHours - last) * SecondsPerGameHour, 0, 1) : 0;
        bool first = lastHours is null;
        lastHours = gameHours;
        double seconds = gameHours * SecondsPerGameHour;
        NormalTime = (float)(seconds / 600 % 1e4);
        float target = WindFactor(windSpeed);
        wind = first ? target : wind + (target - wind) * (float)(1 - Math.Exp(-dt / 10));
        // The first frame starts from the clock itself (so a picture at --water-seconds shows that moment), later ones integrate.
        shorePhase = (first ? seconds / BreakerPeriod : shorePhase + dt / BreakerPeriod) % PhaseWrap;
        float swell = 0.6f + 0.6f * MathF.Min(wind, 1.5f);
        Shore = new Vector4((float)shorePhase, 2.2f * swell, BreakerLength, 3.5f * swell);
    }
}
