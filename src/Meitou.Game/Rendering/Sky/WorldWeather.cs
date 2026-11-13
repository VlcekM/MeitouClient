using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// Hooks the weather scheduler (<see cref="WeatherWorld"/>, docs/formats/weather.md) to the frame: the host sets the game day, speed and paused flag,
/// <see cref="Update"/>
/// feeds the scheduler the camera, the game time and this frame's three frame times, and <see cref="Apply"/> maps the resulting <see
/// cref="WeatherState"/> onto the sky (colour multiplier, cloud density and drift, fog). The state of the last frame is <see cref="State"/>, for the
/// renderers that read it later (wetness, dust, particles). Every timer runs on the frame clock the caller holds still for a picture: a held frame
/// has dt 0 and the first frame is a teleport, so a still shows the settled state.
/// </summary>
public sealed class WorldWeather
{
    /// <summary>
    /// The real time since the last update (at most 0.25 s a frame; 0 for a held frame).
    /// </summary>
    readonly Stopwatch _clock = new();

    /// <summary>
    /// The game time of the last update (null: none yet; a jump of over an hour or backwards is a teleport).
    /// </summary>
    WeatherTime? _last;

    /// <summary>
    /// The region serial last logged (-1: log the next one).
    /// </summary>
    int _lastSerial = -1;

    /// <summary>
    /// The forced weather last logged.
    /// </summary>
    string _lastForced = "";

    /// <summary>
    /// The camera of the last update.
    /// </summary>
    Vector3? _eye;

    /// <summary>
    /// The zone the camera of the last update is in (as <c>--zone</c> names it), null before the first.
    /// </summary>
    public ZoneCoordinate? CameraZone => _eye is { } e ? WorldLayout.ZoneOf(e.X, e.Z) : null;

    /// <summary>
    /// Finds the weather effects' records (null: no particles).
    /// </summary>
    readonly Data.GameDatabase? _database;

    /// <summary>
    /// Each weather's effect list, made once from its records.
    /// </summary>
    readonly Dictionary<WeatherDef, IReadOnlyList<Meitou.Data.Particles.WeatherEffectEntry>> _effectLists = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Hooks <paramref name="world"/>'s scheduler; <paramref name="database"/> supplies the effects' records.
    /// </summary>
    public WorldWeather(WeatherWorld world, Data.GameDatabase? database = null) => (World, this._database) = (world, database);

    /// <summary>
    /// The weather scheduler: regions, their weathers and the current state.
    /// </summary>
    public WeatherWorld World { get; }

    /// <summary>
    /// The state of the last <see cref="Update"/>.
    /// </summary>
    public WeatherState State => World.Current;

    /// <summary>
    /// The frame times of the last update (the sky's cloud drift steps by <see cref="FrameTimes.Game"/>).
    /// </summary>
    public FrameTimes Times { get; private set; }

    /// <summary>
    /// The game day (the viewer's <c>--day</c> and its day keys, the game's clock).
    /// </summary>
    public int Day { get; set; }

    /// <summary>
    /// The game speed (1 in the viewer) and whether the game is paused (never in the viewer).
    /// </summary>
    public float GameSpeed { get; set; } = 1;

    /// <summary>
    /// The game is paused: the game timers stand (never in the viewer).
    /// </summary>
    public bool Paused { get; set; }

    /// <summary>
    /// Overrides the cloud drift (<c>--cloud-wind</c>); null follows the weather's wind.
    /// </summary>
    public Vector2? CloudWindOverride { get; set; }

    /// <summary>
    /// Writes the weather change lines to the console.
    /// </summary>
    public bool Log { get; set; } = true;


    /// <summary>
    /// One frame: the scheduler at the camera, the game day and <paramref name="hour"/> of day. <paramref name="held"/> holds every timer still (a
    /// screenshot); real time is measured here otherwise (at most 0.25 s a frame).
    /// </summary>
    public WeatherState Update(Vector3 eye, double hour, float sunHeight, bool held)
    {
        this._eye = eye;
        float real = held || !_clock.IsRunning ? 0 : (float)Math.Min(_clock.Elapsed.TotalSeconds, 0.25);
        _clock.Restart();
        var times = FrameTimes.FromClock(real, GameSpeed, Paused);
        Times = times;
        var now = new WeatherTime(Day, hour);
        // The schedule runs from the world's creation (day 0, 00:00; the seasons count from there, as `meitou-tools weather --days 0 ..` prints them)
        // in game minutes, so a start at --day 50 or a jump forward (the step keys, the time slider) passes through every change before it; a jump of
        // more than an hour then snaps everything.
        double from = _last?.Minutes ?? 0;
        for (double m = Math.Floor(from) + 1; m <= now.Minutes; m++)
        {
            var t = WeatherTime.FromHours(m / 60);
            foreach (var region in World.Regions) region.Update(t);
        }
        // The clock moved back (the viewer's time slider or its camera keys set the hour, not the day): nothing to replay, the sky and fog snap as
        // for a long jump.
        if (now.Minutes - from > 60 || (_last is not null && now.Minutes < from))
        {
            World.Snap();
            times = default;
        }
        _last = now;
        var state = World.Update(eye, now, times, sunHeight);
        LogChange(state);
        return state;
    }

    /// <summary>
    /// Continues the regions of a save (docs/formats/save.md "BIOMES") at the game <paramref name="day"/> and <paramref name="hour"/>: the schedules
    /// first run up to that time (so a region the save lacks has its own weather there), then the saved ones take over and the sky snaps to them. The
    /// next frame does not replay.
    /// </summary>
    public void Restore(IReadOnlyList<WeatherRegionSnapshot> regions, int day, double hour)
    {
        Day = day;
        var now = new WeatherTime(day, hour);
        double from = _last?.Minutes ?? 0;

        // A region the save holds is set from its snapshot below, which replaces everything the replay would have made of it (docs/engine.md
        // "Loading"): only the others replay, minute by minute from day 0 (a second at day 330 for the 71 regions).
        var saved = regions
            .Select(r => r.RegionId)
            .ToHashSet(StringComparer.Ordinal);
        var replayed = World.Regions
            .Where(r => !saved.Contains(r.Def.StringId))
            .ToList();
        for (double m = Math.Floor(from) + 1; replayed.Count > 0 && m <= now.Minutes; m++)
        {
            var t = WeatherTime.FromHours(m / 60);
            foreach (var region in replayed) region.Update(t);
        }
        World.Restore(regions, now);
        _last = now;
    }

    /// <summary>
    /// The weather region the camera is in (the scheduler's, else by the last eye).
    /// </summary>
    WeatherRegion? Here => World.CameraRegion ?? (_eye is { } e ? World.RegionAt(e.X, e.Z) : null);

    /// <summary>
    /// The Tab panel's weather list: the weathers of the camera region's season, in its order (empty before the first frame).
    /// </summary>
    public IReadOnlyList<string> Choices() => Here is { } r ? [.. r.Season.Season.Weathers.Select(w => w.Weather.Name)] : [];

    /// <summary>
    /// The list's current entry (-1: a forced weather or no region).
    /// </summary>
    public int Current() => World.ForcedWeather is null && Here is { } r ? r.WeatherEntryIndex : -1;

    /// <summary>
    /// The list's button: the region and its weather.
    /// </summary>
    public string ChoiceLabel() => World.ForcedWeather is { } f ? $"Weather: {f.Name} (forced)"
        : Here is { } r ? $"Weather: {r.Weather.Name}"
        : "Weather";

    /// <summary>
    /// The Tab panel's weather list: the camera region starts the season's weather <paramref name="entry"/> now (a forced weather is released first),
    /// and the sky, fog and ramps snap to it. Returns the line the panel shows.
    /// </summary>
    public string Choose(int entry)
    {
        World.ForceWeather((WeatherDef?)null, snap: true);
        if (Here is not { } region) return "No weather region at the camera yet.";
        string before = region.Weather.Name;
        region.Pick(World.Time, entry);
        World.Snap();
        Console.WriteLine($"weather   chose {region.Def.Name}: {before} -> {region.Weather.Name}");
        string minutes = FormatMinutes(region.WeatherEndMinute - World.Time.Minutes);
        return $"{region.Def.Name}: {before} -> {region.Weather.Name}, strength {region.Strength:0.00}, for {minutes}.";
    }

    /// <summary>
    /// Maps the state onto the sky renderer's weather inputs.
    /// </summary>
    public void Apply(SkyRenderer sky)
    {
        var s = State;
        sky.SkyColourMultiplierInput = s.SkyColourMultiplier;
        sky.CloudDensityInput = s.CloudDensity;
        sky.CloudWind = CloudWindOverride ?? s.CloudDrift;
        sky.FogInput = (s.FogEnabled, s.FogColour, s.FogDistance);
    }

    /// <summary>
    /// The particle effect groups' input from the state: the camera region's effect list (EFFECT, count, respawn; the same list object while the
    /// weather stays, so the groups are kept), the weather strength s and the wind velocity. Empty without a database.
    /// </summary>
    public Meitou.Data.Particles.WeatherEffectInput EffectInput()
    {
        var s = State;
        if (_database is null) return Data.Particles.WeatherEffectInput.None;
        if (!_effectLists.TryGetValue(s.Weather, out var list))
        {
            var entries = new List<Meitou.Data.Particles.WeatherEffectEntry>();
            foreach (var e in s.Effects)
                if (_database.Find(e.Effect.StringId) is { } record)
                    entries.Add(new(Data.Particles.EffectRecord.From(record, _database), e.MaxCount, (int)e.RespawnMin, (int)e.RespawnMax));
            _effectLists[s.Weather] = list = entries;
        }
        // TODO(effects): the game gives each group the weather's effect strength (WeatherDef.EffectStrength); the groups take the strength s, as the
        // forced adapter did.
        return new Meitou.Data.Particles.WeatherEffectInput
        {
            Effects = list, Strength = s.Strength, Wind = s.WindDirection * s.WindSpeed, Region = s.RegionName,
        };
    }

    /// <summary>
    /// Logs a change of weather (with <see cref="Log"/>): the forced one by name, else the camera region's when its weather changes.
    /// </summary>
    void LogChange(WeatherState s)
    {
        if (!Log) return;
        if (World.ForcedWeather is { } forced)
        {
            if (_lastForced != forced.Name) Console.WriteLine($"weather   forced {forced.Name}, strength {s.Strength:0.00}");
            _lastForced = forced.Name;
            _lastSerial = -1;
            return;
        }
        _lastForced = "";
        if (World.CameraRegion is not { } region || region.Serial == _lastSerial && _lastRegion == region.Index) return;
        _lastSerial = region.Serial;
        _lastRegion = region.Index;
        Console.WriteLine($"weather   {Describe()}");
    }

    /// <summary>
    /// The region index last logged (-1: none).
    /// </summary>
    int _lastRegion = -1;

    /// <summary>
    /// The line for the log and the statistics: region, season, weather, strength, wind and the time left.
    /// </summary>
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
        float heading = MathF.Atan2(s.WindDirection.Y, s.WindDirection.X) * 180 / MathF.PI;
        return string.Create(inv,
            $"{where}; strength {s.Strength:0.00}, wind {s.WindSpeed:0} u/s heading {heading:0} deg, day {Day}");
    }

    /// <summary>
    /// Game minutes as <c>1 d 3 h</c>, <c>3 h 20 min</c> or <c>45 min</c>.
    /// </summary>
    public static string FormatMinutes(double minutes)
    {
        if (minutes >= 1_000_000 - 1) return "never";
        int m = (int)Math.Round(minutes);
        return m >= 1440 ? $"{m / 1440} d {m % 1440 / 60} h" : m >= 60 ? $"{m / 60} h {m % 60} min" : $"{m} min";
    }
}