using System.Numerics;

namespace Meitou.Data.World;

/// <summary>A region's weather fog: whether it is on (0..1 while fading), its colour and its distance.</summary>
public readonly record struct RegionFog(float Enabled, Vector3 Colour, float Distance);

/// <summary>
/// A region's wind change in progress: from (speed, heading) at <see cref="StartMinute"/> to the targets over <see cref="DurationMinutes"/> (docs/formats/weather.md
/// "Wind"). The heading is an angle in the ground plane, direction = (cos h, sin h) in (x, z); the interpolation takes the shorter way round
/// (<b>Observed</b>: the game's interpolation of an angle that wraps was not traced).
/// </summary>
public readonly record struct WindInterpolation(double StartMinute, double DurationMinutes, float Speed0, float Heading0, float Speed1, float Heading1)
{
    public (float Speed, Vector2 Direction) At(double minute)
    {
        float u = DurationMinutes <= 0 ? 1 : (float)Math.Clamp((minute - StartMinute) / DurationMinutes, 0, 1);
        float speed = Speed0 + (Speed1 - Speed0) * u;
        float heading = Heading0 + WeatherRegion.WrapPi(Heading1 - Heading0) * u;
        return (speed, new Vector2(MathF.Cos(heading), MathF.Sin(heading)));
    }
}

/// <summary>Everything a region needs to continue from a saved game (the random source is not part of it: a restored region draws from a fresh seeded sequence).</summary>
public sealed record WeatherRegionSnapshot(string RegionId, int SeasonIndex, int SeasonEndDay, int WeatherEntryIndex, double WeatherStartMinute, double WeatherEndMinute,
    float Strength, WindInterpolation Wind, double NextWindUpdateMinute, RegionFog FogFrom, double FogFadeMinutes);

/// <summary>
/// One weather region's schedule (docs/formats/weather.md "Seasons", "Choosing a weather", "Wind", "Fog"): the season calendar, the chain of weathers by
/// weight and duration, the strength, the wind and the fog fade. It runs on its own seeded random source, whatever the camera does.
/// </summary>
public sealed class WeatherRegion
{
    /// <summary>The share of a weather's duration over which its fog fades in from the previous weather's.</summary>
    public const double FogFadeShare = 0.025;
    /// <summary>The wind change after an update is interpolated over this share of the wind update time.</summary>
    public const double WindInterpolationShare = 0.05;

    Random random;
    readonly int seed;
    WindInterpolation wind;
    RegionFog fogFrom;
    double fogFadeMinutes;

    /// <param name="def">The region's record.</param>
    /// <param name="index">The region's position in the data (it varies the seed).</param>
    /// <param name="seed">The world's seed; the region's own sequence is derived from it and <paramref name="index"/>.</param>
    /// <param name="start">The game time the region is created at: its first season starts, and its first weather is chosen.</param>
    public WeatherRegion(RegionDef def, int index, int seed, WeatherTime start)
    {
        Def = def;
        Index = index;
        this.seed = unchecked(seed * 1000003 + index * 7919 + 17);
        random = new Random(this.seed);
        SeasonIndex = 0;
        SeasonEndDay = start.Day + Season.Days;
        StartWeather(start, first: true);
        SyncWind(start.Minutes);
    }

    public RegionDef Def { get; }
    public int Index { get; }

    /// <summary>The position of the current season in <see cref="RegionDef.Seasons"/>.</summary>
    public int SeasonIndex { get; private set; }
    public RegionSeason Season => Def.Seasons[SeasonIndex];
    /// <summary>The day count at which the next season starts.</summary>
    public int SeasonEndDay { get; private set; }

    /// <summary>The position of the current weather in the season's <see cref="SeasonDef.Weathers"/>.</summary>
    public int WeatherEntryIndex { get; private set; }
    public SeasonWeather Entry => Season.Season.Weathers[WeatherEntryIndex];
    public WeatherDef Weather => Entry.Weather;
    /// <summary>Game minutes (since day 0) at which the current weather started and at which it ends.</summary>
    public double WeatherStartMinute { get; private set; }
    public double WeatherEndMinute { get; private set; }
    /// <summary>The weather strength s (0..1): scales wind speed, rain, dust and heat haze.</summary>
    public float Strength { get; private set; }
    /// <summary>Counts the weathers started so far; a change means the camera's sky should start its transition.</summary>
    public int Serial { get; private set; }

    public float WindSpeed { get; private set; }
    /// <summary>The wind's unit direction in the ground plane (x, z).</summary>
    public Vector2 WindDirection { get; private set; } = Vector2.UnitX;
    public WindInterpolation Wind => wind;
    public double NextWindUpdateMinute { get; private set; }

    /// <summary>Advances the schedule to <paramref name="now"/>: a new season when the day count reaches the season's end, a new weather when the current one has run out, a wind update when it is due.</summary>
    public void Update(WeatherTime now)
    {
        double t = now.Minutes;
        if (now.Day >= SeasonEndDay)
        {
            var fog = FogAt(t);   // before the season (and with it the entry index) changes
            SeasonIndex = (SeasonIndex + 1) % Def.Seasons.Count;
            SeasonEndDay = now.Day + Season.Days;
            StartWeather(now, first: false, fog);
        }
        else if (t >= WeatherEndMinute)
            StartWeather(now, first: false, FogAt(t));
        else if (t >= NextWindUpdateMinute)
        {
            RetargetWind(t, Weather, Strength);
            NextWindUpdateMinute = t + Weather.WindUpdateMinutes;
        }
        SyncWind(t);
    }

    void SyncWind(double minute) => (WindSpeed, WindDirection) = wind.At(minute);

    /// <summary>The region's weather fog at a game time: the previous weather's fog fades into the current weather's over the first 2.5 % of its duration.</summary>
    public RegionFog FogAt(double minute)
    {
        var w = Weather;
        float targetEnabled = w.FogEnabled ? 1 : 0;
        float targetDistance = w.FogDistanceAt(WindSpeed);
        float k = fogFadeMinutes <= 0 ? 1 : (float)Math.Clamp((minute - WeatherStartMinute) / fogFadeMinutes, 0, 1);
        if (k >= 1) return new RegionFog(targetEnabled, w.FogColour, targetDistance);
        var from = fogFrom;
        float enabled = from.Enabled + (targetEnabled - from.Enabled) * k;
        // Observed: a side that is off lends its colour and distance to the other, so only the on/off weight fades (an "off" record's distance is not meant to be seen).
        if (from.Enabled <= 0) return new RegionFog(enabled, w.FogColour, targetDistance);
        if (targetEnabled <= 0) return new RegionFog(enabled, from.Colour, from.Distance);
        return new RegionFog(enabled, Vector3.Lerp(from.Colour, w.FogColour, k), from.Distance + (targetDistance - from.Distance) * k);
    }

    // ---- choosing ----

    void StartWeather(WeatherTime now, bool first, RegionFog previousFog = default)
    {
        double t = now.Minutes;
        var season = Season.Season;

        int entry = Choose(season, now.WholeHour);
        var chosen = season.Weathers[entry];
        int minutes = RollMinutes(chosen.MinMinutes, chosen.MaxMinutes);
        double end = t + minutes;
        if (chosen.Weather.HasTimeWindow)
        {
            // Cut at today's end time. Observed: a cut already in the past (the window's last hour, which the inclusive window test accepts) is not applied, so
            // the weather runs its rolled duration rather than being re-chosen every frame.
            double cut = now.Day * WeatherTime.MinutesPerDay + chosen.Weather.EndTime * 60.0;
            if (cut > t) end = Math.Min(end, cut);
        }
        float s = RollStrength(season);

        WeatherEntryIndex = entry;
        WeatherStartMinute = t;
        WeatherEndMinute = end;
        Strength = s;
        Serial++;

        if (first)
        {
            float speed = chosen.Weather.WindSpeedAt(s);
            var dir = new Vector2(random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1);
            dir = dir.LengthSquared() < 1e-8f ? Vector2.UnitX : Vector2.Normalize(dir);
            float heading = MathF.Atan2(dir.Y, dir.X);
            wind = new WindInterpolation(t, 0, speed, heading, speed, heading);
            fogFrom = new RegionFog(chosen.Weather.FogEnabled ? 1 : 0, chosen.Weather.FogColour, chosen.Weather.FogDistanceMax);
            fogFadeMinutes = 0;
        }
        else
        {
            RetargetWind(t, chosen.Weather, s);
            fogFrom = previousFog;
            fogFadeMinutes = FogFadeShare * (end - t);
        }
        NextWindUpdateMinute = t + chosen.Weather.WindUpdateMinutes;
    }

    /// <summary>
    /// The index of the season's next weather: a weighted pick among the weathers with weight above 0 that are not out of their time window (the
    /// controller's whole hour in [start, end]); a single candidate is taken as is, none gives the season's last weather.
    /// </summary>
    int Choose(SeasonDef season, int hour)
    {
        var candidates = new List<int>(season.Weathers.Count);
        long sum = 0;
        for (int i = 0; i < season.Weathers.Count; i++)
        {
            var w = season.Weathers[i];
            if (w.Weight > 0 && w.Weather.IsInWindow(hour)) { candidates.Add(i); sum += w.Weight; }
        }
        if (candidates.Count == 0) return season.Weathers.Count - 1;
        if (candidates.Count == 1) return candidates[0];
        double r = random.NextDouble() * sum;
        long running = 0;
        foreach (int i in candidates)
        {
            running += season.Weathers[i].Weight;
            if (running > r) return i;
        }
        return candidates[^1];
    }

    /// <summary>A whole number of minutes in [min, max], both inclusive: <c>min + trunc(r · (max − min + 1))</c>.</summary>
    int RollMinutes(int min, int max)
    {
        max = Math.Max(min, max);
        return Math.Clamp(min + (int)(random.NextDouble() * (max - min + 1)), min, max);
    }

    /// <summary><c>clamp(random[limit min, limit max] × random[multiplier min, multiplier max], 0, 1)</c>.</summary>
    float RollStrength(SeasonDef season)
    {
        float limit = season.StrengthLimitMin + (season.StrengthLimitMax - season.StrengthLimitMin) * random.NextSingle();
        float mult = Def.StrengthMultiplierMin + (Def.StrengthMultiplierMax - Def.StrengthMultiplierMin) * random.NextSingle();
        return Math.Clamp(limit * mult, 0, 1);
    }

    // ---- wind ----

    /// <summary>
    /// A wind update (also what a new weather does): the target speed moves <c>limit / 360</c> of the way from the current speed to the weather's, the
    /// target heading turns by a random angle in ±π · limit / 360 from the current heading, read back unsigned (0..π) as the game does; both are then
    /// interpolated over <c>round(0.05 × wind update time)</c> minutes.
    /// </summary>
    void RetargetWind(double t, WeatherDef w, float strength)
    {
        var (speed, direction) = wind.At(t);
        float heading = MathF.Acos(Math.Clamp(direction.X, -1f, 1f));
        float k = w.WindUpdateLimit / 360f;
        float targetSpeed = speed + (w.WindSpeedAt(strength) - speed) * k;
        float targetHeading = WrapPi(heading + (random.NextSingle() * 2 - 1) * MathF.PI * k);
        double duration = Math.Round(WindInterpolationShare * w.WindUpdateMinutes, MidpointRounding.AwayFromZero);
        wind = new WindInterpolation(t, duration, speed, heading, targetSpeed, targetHeading);
    }

    /// <summary>An angle wrapped into [−π, π].</summary>
    public static float WrapPi(float angle) => MathF.IEEERemainder(angle, MathF.Tau);

    // ---- saving ----

    public WeatherRegionSnapshot Snapshot() => new(Def.StringId, SeasonIndex, SeasonEndDay, WeatherEntryIndex, WeatherStartMinute, WeatherEndMinute, Strength, wind,
        NextWindUpdateMinute, fogFrom, fogFadeMinutes);

    /// <summary>Continues from a snapshot of this region; indexes outside the region's current seasons are clamped (a mod may have changed them).</summary>
    public void Restore(WeatherRegionSnapshot s, WeatherTime now)
    {
        SeasonIndex = Math.Clamp(s.SeasonIndex, 0, Def.Seasons.Count - 1);
        SeasonEndDay = s.SeasonEndDay;
        WeatherEntryIndex = Math.Clamp(s.WeatherEntryIndex, 0, Season.Season.Weathers.Count - 1);
        WeatherStartMinute = s.WeatherStartMinute;
        WeatherEndMinute = s.WeatherEndMinute;
        Strength = s.Strength;
        wind = s.Wind;
        NextWindUpdateMinute = s.NextWindUpdateMinute;
        fogFrom = s.FogFrom;
        fogFadeMinutes = s.FogFadeMinutes;
        random = new Random(unchecked(seed + SeasonEndDay * 31 + (int)WeatherStartMinute));
        Serial++;
        SyncWind(now.Minutes);
    }

    public override string ToString() => $"{Def.Name}: {Season.Season.Name} (until day {SeasonEndDay}), {Weather.Name} s={Strength:0.00}";
}
