using System.Numerics;

namespace Meitou.Rendering;

/// <summary>
/// The Meitou water's motion parameters (<see cref="WaterRenderer"/>, docs/viewer.md "Water"): four Gerstner waves led by the weather's wind and the
/// shore's breakers. Visual only: the game's water stays a flat plane at Y = 100. Everything runs on the game clock, so it stands still while
/// paused and speeds up with the game speed. Phases are integrated frame by frame (a change of wind bends the waves instead of jumping them).
/// Units are the world's (1 unit = 1 dm); seconds are game seconds at speed 1 (1 game hour = 109.09 s, docs/game/game-loop.md).
/// </summary>
public sealed class WaveSet
{
    /// <summary>Real seconds per game hour at game speed 1 (docs/game/game-loop.md "Day length").</summary>
    public const double SecondsPerGameHour = 1200.0 / 11.0;
    /// <summary>Gravity in units per second squared, for the deep-water dispersion ω = √(g k).</summary>
    public const float Gravity = 98.1f;

    /// <summary>The waves' wavelengths at a wind factor of 0 (units); they grow by 60 % at a full wind.</summary>
    static readonly float[] BaseLength = [52, 31, 19, 11];
    /// <summary>Their angles off the wind (radians), so the crossing waves do not line up.</summary>
    static readonly float[] Angle = [0, 0.45f, -0.6f, 1.2f];

    /// <summary>The breakers: one every <see cref="BreakerPeriod"/> seconds, <see cref="BreakerLength"/> units apart.</summary>
    public const float BreakerPeriod = 7, BreakerLength = 80;

    double? lastHours;
    Vector2 wind = Vector2.UnitX;   // smoothed direction × factor
    double shorePhase;
    readonly double[] phase = new double[4];

    public Vector4 DirX, DirZ, K, Amplitude, Phase, Steepness;
    /// <summary>x the breakers' phase (cycles), y the run-up height, z the breakers' wavelength, w their height (units).</summary>
    public Vector4 Shore;
    /// <summary>The game-clock time for the normal maps' scrolling, in the unit the Faithful water's time has (seconds / 600).</summary>
    public float NormalTime;

    /// <summary>The wind factor: 0 calm, 1 at 60 units per second (a stiff breeze), at most 1.5.</summary>
    public static float WindFactor(float windSpeed) => Math.Clamp(windSpeed / 60f, 0f, 1.5f);

    public WaveSet() { Step(0, Vector2.UnitX, 0); lastHours = null; }   // usable values before the first frame, which then takes its wind at once

    public void Step(double gameHours, Vector2 windDirection, float windSpeed)
    {
        double dt = lastHours is { } last ? Math.Clamp((gameHours - last) * SecondsPerGameHour, 0, 1) : 0;
        bool first = lastHours is null;
        lastHours = gameHours;
        double seconds = gameHours * SecondsPerGameHour;
        NormalTime = (float)(seconds / 600 % 1e4);

        // The wind as the waves feel it: follows the weather's over about ten seconds.
        var dir = windDirection.LengthSquared() > 1e-6f ? Vector2.Normalize(windDirection) : Vector2.UnitX;
        var target = dir * WindFactor(windSpeed);
        wind = first ? target : Vector2.Lerp(wind, target, (float)(1 - Math.Exp(-dt / 10)));
        float w = wind.Length();
        float heading = MathF.Atan2(wind.Y, wind.X);

        Span<float> dx = stackalloc float[4], dz = stackalloc float[4], k = stackalloc float[4], a = stackalloc float[4], q = stackalloc float[4], ph = stackalloc float[4];
        for (int i = 0; i < 4; i++)
        {
            float length = BaseLength[i] * (1 + 0.6f * MathF.Min(w, 1));
            (dx[i], dz[i]) = (MathF.Cos(heading + Angle[i]), MathF.Sin(heading + Angle[i]));
            k[i] = 2 * MathF.PI / length;
            a[i] = length * (0.004f + 0.011f * w);
            // Steepness so the four together stay below folding (Σ q k a ≤ 0.8).
            q[i] = Math.Clamp(0.8f / (4 * k[i] * a[i]), 0, 1);
            phase[i] = (phase[i] + Math.Sqrt(Gravity * k[i]) * dt) % (2 * Math.PI);
            ph[i] = (float)phase[i];
        }
        (DirX, DirZ, K, Amplitude, Steepness, Phase) = (new(dx), new(dz), new(k), new(a), new(q), new(ph));

        shorePhase = (shorePhase + dt / BreakerPeriod) % 1.0;
        float swell = 0.6f + 0.6f * MathF.Min(w, 1.5f);
        Shore = new Vector4((float)shorePhase, 1.4f * swell, BreakerLength, 1.6f * swell);
    }
}
