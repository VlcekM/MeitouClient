namespace Meitou.Engine.Time;

/// <summary>
/// Game time: seconds, the hour of day, a time scale and a pause. It moves only through <see cref="Tick"/>, by tick length times
/// the scale (nothing while paused), so it is as deterministic as the tick count. The game keeps a day count and a time of day
/// in game hours (docs/formats/weather.md "Time bases"); how many real seconds a game hour takes is not researched, so
/// <see cref="SecondsPerGameHour"/> is an engine choice. The game's pause also stops sky time but lets some effects settle at
/// 0.01 speed (same doc): that is the sky's business, not this clock's.
/// </summary>
public sealed class GameClock
{
    /// <summary>Game seconds per game hour at scale 1 (engine choice: a day lasts one real hour).</summary>
    public const double DefaultSecondsPerGameHour = 150;
    /// <summary>Hour of day a new game starts at.</summary>
    public const double DefaultStartHour = 13;

    double timeScale = 1;

    public GameClock(double startHour = DefaultStartHour, double secondsPerGameHour = DefaultSecondsPerGameHour)
    {
        if (!(secondsPerGameHour > 0)) throw new ArgumentOutOfRangeException(nameof(secondsPerGameHour));
        StartHour = startHour;
        SecondsPerGameHour = secondsPerGameHour;
    }

    public double StartHour { get; }
    public double SecondsPerGameHour { get; }
    /// <summary>Game seconds since the start (scaled; not advanced while paused).</summary>
    public double GameSeconds { get; private set; }
    public bool Paused { get; set; }

    /// <summary>Multiplier on tick length (1, 2, 3...); 0 or more.</summary>
    public double TimeScale
    {
        get => timeScale;
        set
        {
            if (!(value >= 0) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value));
            timeScale = value;
        }
    }

    /// <summary>Game hours since the start, plus the start hour.</summary>
    public double TotalHours => StartHour + GameSeconds / SecondsPerGameHour;

    /// <summary>Time of day in [0, 24).</summary>
    public double HourOfDay
    {
        get
        {
            double h = TotalHours % 24;
            return h < 0 ? h + 24 : h;
        }
    }

    /// <summary>Whole days passed counting from midnight before the start.</summary>
    public long Day => (long)Math.Floor(TotalHours / 24);

    /// <summary>Advances by one tick of <paramref name="tickSeconds"/> real seconds.</summary>
    public void Tick(double tickSeconds)
    {
        if (!Paused) GameSeconds += tickSeconds * timeScale;
    }

    /// <summary>Sets the time of day, keeping the day count.</summary>
    public void SetHourOfDay(double hour)
    {
        double target = Day * 24 + ((hour % 24) + 24) % 24;
        GameSeconds = (target - StartHour) * SecondsPerGameHour;
    }
}
