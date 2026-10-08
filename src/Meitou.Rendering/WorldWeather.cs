using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Rendering;

/// <summary>
/// Hooks the weather scheduler (<see cref="WeatherWorld"/>, docs/formats/weather.md) to the frame: the host sets the game day, speed and paused flag, <see cref="Update"/>
/// feeds the scheduler the camera, the game time and this frame's three frame times, and <see cref="Apply"/> maps the resulting <see cref="WeatherState"/> onto the sky
/// (colour multiplier, cloud density and drift, fog). The state of the last frame is <see cref="State"/>, for the renderers that read it later (wetness, dust, particles).
/// Every timer runs on the frame clock the caller holds still for a picture: a held frame has dt 0 and the first frame is a teleport, so a still shows the settled state.
/// </summary>
public sealed class WorldWeather
{
    readonly Stopwatch clock = new();
    WeatherTime? last;
    int lastSerial = -1;
    string lastForced = "";
    Vector3? eye;   // the camera of the last update

    /// <summary>The zone the camera of the last update is in (as <c>--zone</c> names it), null before the first.</summary>
    public ZoneCoordinate? CameraZone => eye is { } e ? WorldLayout.ZoneOf(e.X, e.Z) : null;

    readonly Meitou.Data.GameDatabase? database;
    readonly Dictionary<WeatherDef, IReadOnlyList<Meitou.Data.Particles.WeatherEffectEntry>> effectLists = new(ReferenceEqualityComparer.Instance);

    public WorldWeather(WeatherWorld world, Meitou.Data.GameDatabase? database = null) => (World, this.database) = (world, database);

    public WeatherWorld World { get; }
    /// <summary>The state of the last <see cref="Update"/>.</summary>
    public WeatherState State => World.Current;
    /// <summary>The frame times of the last update (the sky's cloud drift steps by <see cref="FrameTimes.Game"/>).</summary>
    public FrameTimes Times { get; private set; }

    /// <summary>The game day (the viewer's <c>--day</c> and its day keys, the game's clock).</summary>
    public int Day { get; set; }
    /// <summary>The game speed (1 in the viewer) and whether the game is paused (never in the viewer).</summary>
    public float GameSpeed { get; set; } = 1;
    public bool Paused { get; set; }
    /// <summary>Overrides the cloud drift (<c>--cloud-wind</c>); null follows the weather's wind.</summary>
    public Vector2? CloudWindOverride { get; set; }
    /// <summary>Writes the weather change lines to the console.</summary>
    public bool Log { get; set; } = true;


    /// <summary>
    /// One frame: the scheduler at the camera, the game day and <paramref name="hour"/> of day. <paramref name="held"/> holds every timer still (a screenshot);
    /// real time is measured here otherwise (at most 0.25 s a frame).
    /// </summary>
    public WeatherState Update(Vector3 eye, double hour, float sunHeight, bool held)
    {
        this.eye = eye;
        float real = held || !clock.IsRunning ? 0 : (float)Math.Min(clock.Elapsed.TotalSeconds, 0.25);
        clock.Restart();
        var times = FrameTimes.FromClock(real, GameSpeed, Paused);
        Times = times;
        var now = new WeatherTime(Day, hour);
        // The schedule runs from the world's creation (day 0, 00:00; the seasons count from there, as `meitou-tools weather --days 0 ..` prints them) in game minutes, so a
        // start at --day 50 or a jump forward (the step keys, the time slider) passes through every change before it; a jump of more than an hour then snaps everything.
        double from = last?.Minutes ?? 0;
        for (double m = Math.Floor(from) + 1; m <= now.Minutes; m++)
        {
            var t = WeatherTime.FromHours(m / 60);
            foreach (var region in World.Regions) region.Update(t);
        }
        if (now.Minutes - from > 60) { World.Snap(); times = default; }
        last = now;
        var state = World.Update(eye, now, times, sunHeight);
        LogChange(state);
        return state;
    }

    /// <summary>
    /// The Tab panel's "Reroll weather": the camera region starts a different weather of its season now (a forced weather is released first), and the sky,
    /// fog and ramps snap to it. Returns the line the panel shows.
    /// </summary>
    public string Reroll()
    {
        World.ForceWeather((WeatherDef?)null, snap: true);
        if ((World.CameraRegion ?? (eye is { } e ? World.RegionAt(e.X, e.Z) : null)) is not { } region) return "No weather region at the camera yet.";
        string before = region.Weather.Name;
        if (!region.Reroll(World.Time)) return $"{region.Def.Name} ({region.Season.Season.Name}) has no other weather than {before}.";
        World.Snap();
        Console.WriteLine($"weather   rerolled {region.Def.Name}: {before} -> {region.Weather.Name}");
        return $"{region.Def.Name}: {before} -> {region.Weather.Name}, strength {region.Strength:0.00}, for {FormatMinutes(region.WeatherEndMinute - World.Time.Minutes)}.";
    }

    /// <summary>The question the panel asks before <see cref="Reroll"/>.</summary>
    public string RerollQuestion() => (World.CameraRegion ?? (eye is { } e ? World.RegionAt(e.X, e.Z) : null)) is { } region
        ? World.ForcedWeather is { } f ? $"Release the forced {f.Name} and reroll the weather of {region.Def.Name}?" : $"Reroll the weather of {region.Def.Name} (now {region.Weather.Name})?"
        : "Reroll the weather?";

    /// <summary>Maps the state onto the sky renderer's weather inputs.</summary>
    public void Apply(SkyRenderer sky)
    {
        var s = State;
        sky.SkyColourMultiplierInput = s.SkyColourMultiplier;
        sky.CloudDensityInput = s.CloudDensity;
        sky.CloudWind = CloudWindOverride ?? s.CloudDrift;
        sky.FogInput = (s.FogEnabled, s.FogColour, s.FogDistance);
    }

    /// <summary>
    /// The particle effect groups' input from the state: the camera region's effect list (EFFECT, count, respawn; the same list object while the weather
    /// stays, so the groups are kept), the weather strength s and the wind velocity. Empty without a database.
    /// </summary>
    public Meitou.Data.Particles.WeatherEffectInput EffectInput()
    {
        var s = State;
        if (database is null) return Meitou.Data.Particles.WeatherEffectInput.None;
        if (!effectLists.TryGetValue(s.Weather, out var list))
        {
            var entries = new List<Meitou.Data.Particles.WeatherEffectEntry>();
            foreach (var e in s.Effects)
                if (database.Find(e.Effect.StringId) is { } record)
                    entries.Add(new(Meitou.Data.Particles.EffectRecord.From(record, database), e.MaxCount, (int)e.RespawnMin, (int)e.RespawnMax));
            effectLists[s.Weather] = list = entries;
        }
        // TODO(effects): the game gives each group the weather's effect strength (WeatherDef.EffectStrength); the groups take the strength s, as the forced adapter did.
        return new Meitou.Data.Particles.WeatherEffectInput { Effects = list, Strength = s.Strength, Wind = s.WindDirection * s.WindSpeed };
    }

    void LogChange(WeatherState s)
    {
        if (!Log) return;
        if (World.ForcedWeather is { } forced)
        {
            if (lastForced != forced.Name) Console.WriteLine($"weather   forced {forced.Name}, strength {s.Strength:0.00}");
            lastForced = forced.Name;
            lastSerial = -1;
            return;
        }
        lastForced = "";
        if (World.CameraRegion is not { } region || region.Serial == lastSerial && lastRegion == region.Index) return;
        lastSerial = region.Serial;
        lastRegion = region.Index;
        Console.WriteLine($"weather   {Describe()}");
    }

    int lastRegion = -1;

    /// <summary>The line for the log and the statistics: region, season, weather, strength, wind and the time left.</summary>
    public string Describe()
    {
        var s = State;
        var inv = CultureInfo.InvariantCulture;
        string where;
        if (World.CameraRegion is { } region)
        {
            double left = Math.Max(region.WeatherEndMinute - World.Time.Minutes, 0);
            where = $"{region.Def.Name} ({region.Season.Season.Name}), {s.Weather.Name}, ends in {FormatMinutes(left)}";
        }
        else where = $"forced {s.Weather.Name}";
        return string.Create(inv, $"{where}; strength {s.Strength:0.00}, wind {s.WindSpeed:0} u/s heading {MathF.Atan2(s.WindDirection.Y, s.WindDirection.X) * 180 / MathF.PI:0} deg, day {Day}");
    }

    /// <summary>Game minutes as <c>1 d 3 h</c>, <c>3 h 20 min</c> or <c>45 min</c>.</summary>
    public static string FormatMinutes(double minutes)
    {
        if (minutes >= 1_000_000 - 1) return "never";
        int m = (int)Math.Round(minutes);
        return m >= 1440 ? $"{m / 1440} d {m % 1440 / 60} h" : m >= 60 ? $"{m / 60} h {m % 60} min" : $"{m} min";
    }
}
