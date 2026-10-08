namespace Meitou.Data.World;

/// <summary>
/// Game time as the weather code sees it (docs/formats/weather.md "Time bases"): the sky controller's whole day count and the time of day in
/// hours. Weather durations and wind updates are in game minutes, season ends in days.
/// </summary>
public readonly record struct WeatherTime(int Day, double Hours)
{
    public const double MinutesPerDay = 1440;

    /// <summary>Whole game minutes since day 0 (as a double: it stays exact over thousands of days).</summary>
    public double Minutes => Day * MinutesPerDay + Hours * 60.0;

    /// <summary>The controller's current whole hour, the value the time-window test of the weather choice uses.</summary>
    public int WholeHour => (int)Math.Floor(Hours);

    /// <summary>Time from a continuous count of game hours since day 0.</summary>
    public static WeatherTime FromHours(double totalHours)
    {
        int day = (int)Math.Floor(totalHours / 24);
        return new WeatherTime(day, totalHours - day * 24.0);
    }

    /// <summary>The start of a day: day <paramref name="day"/>, hour <paramref name="hours"/>.</summary>
    public static WeatherTime At(int day, double hours = 0) => new(day, hours);
}

/// <summary>
/// The three frame times the game keeps (docs/formats/weather.md "Time bases", <b>Verified (decompiled)</b>): the real frame time; the frame time ×
/// game speed, 0 when paused (sky transition, cloud drift, fog fade); and the same × game speed but a factor 0.01 when paused (wetness, dust, heat
/// haze, so they still settle). <b>Observed</b>: "0.01 when paused" is taken as the factor replacing the game speed, not a fixed step.
/// </summary>
public readonly record struct FrameTimes(float Real, float Game, float Settling)
{
    /// <summary>The factor that replaces the game speed in <see cref="Settling"/> while paused.</summary>
    public const float PausedSettleFactor = 0.01f;

    public static FrameTimes FromClock(float realDt, float gameSpeed, bool paused) =>
        new(realDt, paused ? 0 : realDt * gameSpeed, realDt * (paused ? PausedSettleFactor : gameSpeed));
}
