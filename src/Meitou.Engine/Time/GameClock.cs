namespace Meitou.Engine.Time;

/// <summary>
/// The game clock (docs/game/game-loop.md "The clock", Verified): game seconds since the start, from which come the day count, the
/// time of day, the total game hours, the day/night flag and the daylight factor. It moves only through <see cref="Advance"/>, which
/// the simulation calls once per simulation tick with that tick's game seconds (1/30 s; docs/simulation.md "Time model"), so it is
/// as deterministic as the tick count. Speed and pause are not its business (<see cref="SimulationClock"/> decides how many ticks
/// run). One game hour is 1200/11 = 109.09 game seconds: the game advances its clock by <c>dt x speed x 11/1200</c> hours.
/// </summary>
public sealed class GameClock
{
    /// <summary>Game seconds per game hour (Verified: the game's 0.0091666... hours per second at speed 1).</summary>
    public const double SecondsPerGameHour = 1200.0 / 11;
    /// <summary>Hour of day a new game starts at (Unknown in the original: the one sample save, a new game, was saved at 13:39; engine choice).</summary>
    public const double DefaultStartHour = 13;
    /// <summary>Day count of a new game (Observed: the sample save, a new game, reads day 1).</summary>
    public const long DefaultStartDay = 1;
    /// <summary>The CONSTANTS record's values in the shipped data (Verified: <c>sunrise</c> 5, <c>sunset</c> 23, <c>days per year</c> 100).</summary>
    public const double DefaultSunrise = 5, DefaultSunset = 23;
    public const int DefaultDaysPerYear = 100;
    /// <summary>Length of the daylight ramps after sunrise and before sunset, in game hours (Observed, <c>FUN_14066cb50</c>).</summary>
    public const double RampHours = 2;

    public GameClock(double startHour = DefaultStartHour, long startDay = DefaultStartDay,
        double sunrise = DefaultSunrise, double sunset = DefaultSunset, int daysPerYear = DefaultDaysPerYear)
    {
        if (daysPerYear < 1) throw new ArgumentOutOfRangeException(nameof(daysPerYear));
        // Sunset not after sunrise gives 6 and 20 (game-loop.md, formats/terrain.md).
        if (sunset <= sunrise) (sunrise, sunset) = (6, 20);
        StartHour = ((startHour % 24) + 24) % 24;
        StartDay = startDay;
        Sunrise = sunrise;
        Sunset = sunset;
        DaysPerYear = daysPerYear;
    }

    public double StartHour { get; }
    public long StartDay { get; }
    public double Sunrise { get; }
    public double Sunset { get; }
    public int DaysPerYear { get; }

    /// <summary>Game seconds since the start.</summary>
    public double GameSeconds { get; private set; }

    /// <summary>Advances by <paramref name="gameSeconds"/> of game time (one simulation tick's worth).</summary>
    public void Advance(double gameSeconds)
    {
        if (!(gameSeconds >= 0) || double.IsInfinity(gameSeconds)) throw new ArgumentOutOfRangeException(nameof(gameSeconds));
        GameSeconds += gameSeconds;
    }

    /// <summary>Game hours since the start of this session.</summary>
    public double HoursSinceStart => GameSeconds / SecondsPerGameHour;

    /// <summary>Total game hours: <c>day x 24 + time of day</c> (the game's double at +0xa0 of the sky controller).</summary>
    public double TotalHours => StartDay * 24.0 + StartHour + HoursSinceStart;

    /// <summary>Time of day in [0, 24).</summary>
    public double HourOfDay
    {
        get
        {
            double h = TotalHours % 24;
            if (h < 0) h += 24;
            // A double a hair under 24 would print as 24:00.
            return h >= 24 ? 0 : h;
        }
    }

    /// <summary>The day count (what the save stores as <c>time day</c>).</summary>
    public long Day => (long)Math.Floor(TotalHours / 24);

    /// <summary>The hour shown on the clock, 0 to 23.</summary>
    public int Hour => Math.Min((int)HourOfDay, 23);

    /// <summary>The minute shown on the clock, 0 to 59: <c>floor(frac(time of day) x 60)</c>; seconds are not kept.</summary>
    public int Minute => Math.Min((int)((HourOfDay - Math.Floor(HourOfDay)) * 60), 59);

    /// <summary>Daytime when <c>sunrise &lt; hour &lt; sunset</c>, both strict (Observed, <c>FUN_14066cb20</c>).</summary>
    public bool IsDaytime => IsDay(HourOfDay);

    /// <summary>The daylight factor, 0 to 1 (Observed, <c>FUN_14066cb50</c>): 0 outside daytime, a linear rise over <see cref="RampHours"/> after sunrise and fall before sunset, 1 between.</summary>
    public double DaylightFactor => DaylightAt(HourOfDay);

    /// <summary>The clock text <c>HH:MM</c>.</summary>
    public string TimeText => $"{Hour:00}:{Minute:00}";

    /// <summary>The day label, the game's format <c>Day: {n}</c>.</summary>
    public string DayText => $"Day: {Day}";

    public bool IsDay(double hour) => hour > Sunrise && hour < Sunset;

    public double DaylightAt(double hour)
    {
        if (!IsDay(hour)) return 0;
        double rise = (hour - Sunrise) / RampHours, set = (Sunset - hour) / RampHours;
        return Math.Clamp(Math.Min(rise, set), 0, 1);
    }

    /// <summary>Sets the time of day, keeping the day count.</summary>
    public void SetHourOfDay(double hour)
    {
        double target = Day * 24.0 + ((hour % 24) + 24) % 24;
        GameSeconds = (target - StartDay * 24.0 - StartHour) * SecondsPerGameHour;
    }
}
