using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Tests.World;

/// <summary>The weather data and scheduler (docs/formats/weather.md): calendar, choice, wind, fog, and the camera side. Quick tests use hand-made definitions.</summary>
public class WeatherTests
{
    // ---- hand-made definitions ----

    static WeatherDef Weather(string name, Action<WeatherDefBuilder>? configure = null)
    {
        var b = new WeatherDefBuilder(name);
        configure?.Invoke(b);
        return b.Build();
    }

    sealed class WeatherDefBuilder(string name)
    {
        public float WindMin, WindMax = 60, UpdateMinutes = 360, UpdateLimit = 20;
        public bool Fog;
        public Vector3 FogColour = Vector3.One;
        public float FogMin, FogMax, FogWindMin, FogWindMax;
        public float Rain, Wetness, Dust, DustSlope, HeatHaze, Clouds, Start, End;
        public Vector3 Sky = Vector3.One;

        public WeatherDef Build() => new()
        {
            StringId = name, Name = name, WindSpeedMin = WindMin, WindSpeedMax = WindMax, WindUpdateMinutes = UpdateMinutes, WindUpdateLimit = UpdateLimit,
            FogEnabled = Fog, FogColour = FogColour, FogDistanceMin = FogMin, FogDistanceMax = FogMax, FogWindMin = FogWindMin, FogWindMax = FogWindMax,
            RainIntensity = Rain, Wetness = Wetness, Dust = Dust, DustSlope = DustSlope, HeatHaze = HeatHaze, CloudsDensity = Clouds, StartTime = Start, EndTime = End, SkyColourMultiplier = Sky,
        };
    }

    static SeasonWeather Entry(WeatherDef w, int weight, int min, int max) => new(w, weight, min, max);

    static SeasonDef Season(string name, float limitMin, float limitMax, params SeasonWeather[] weathers) =>
        SeasonDef.Build(name, name, weathers, limitMin, limitMax, WeatherDef.Clear);

    static RegionDef Region(string name, int colour, params (SeasonDef Season, float Share)[] seasons)
    {
        var lengths = SeasonCalendar.Lengths(seasons.Select(s => s.Share).ToList(), 100);
        return new RegionDef
        {
            StringId = name, Name = name, Index = colour,
            Seasons = seasons.Select((s, i) => new RegionSeason(s.Season, i, s.Share, lengths[i])).ToList(),
        };
    }

    static WeatherRegion OneRegion(SeasonDef season, int seed = 1, float multMin = 1, float multMax = 1)
    {
        var def = Region("R", 1, (season, 1)) with { StrengthMultiplierMin = multMin, StrengthMultiplierMax = multMax };
        return new WeatherRegion(def, 0, seed, WeatherTime.At(0));
    }

    /// <summary>Runs the region minute by minute and returns the picks (the region's state right after each new weather).</summary>
    static List<(double Start, double End, string Name, float Strength, int Hour)> Picks(WeatherRegion region, int fromMinute, int minutes)
    {
        var picks = new List<(double, double, string, float, int)>();
        int serial = region.Serial;
        for (int m = fromMinute; m <= fromMinute + minutes; m++)
        {
            var t = WeatherTime.At(m / 1440, m % 1440 / 60.0);
            region.Update(t);
            if (region.Serial != serial)
            {
                serial = region.Serial;
                picks.Add((region.WeatherStartMinute, region.WeatherEndMinute, region.Weather.Name, region.Strength, t.WholeHour));
            }
        }
        return picks;
    }

    /// <summary>Two regions split at x = 0: cells 0..31 are "West" (colour 1), 32..63 "East" (colour 2).</summary>
    static WeatherWorld TwoRegionWorld(WeatherDef west, WeatherDef east, int seed = 1)
    {
        var colours = new int[WeatherAreas.Cells * WeatherAreas.Cells];
        for (int z = 0; z < WeatherAreas.Cells; z++)
            for (int x = 0; x < WeatherAreas.Cells; x++)
                colours[z * WeatherAreas.Cells + x] = x < WeatherAreas.Origin ? 1 : 2;
        var data = WeatherData.FromDefinitions([west, east],
            [],
            [Region("West", 1, (Season("w", 1, 1, Entry(west, 1, 100000, 100000)), 1)), Region("East", 2, (Season("e", 1, 1, Entry(east, 1, 100000, 100000)), 1))]);
        return new WeatherWorld(data, new WeatherAreas(colours), seed, WeatherTime.At(0));
    }

    static readonly FrameTimes Second = new(1, 1, 1);

    /// <summary>The numbers of a state (records of definitions compare their lists by reference).</summary>
    static object Key(WeatherState s) => (s.Weather.Name, s.RegionName, s.Strength, s.WindDirection, s.WindSpeed, s.SkyColourMultiplier, s.CloudDensity, s.CloudDrift, s.FogEnabled,
        s.FogColour, s.FogDistance, s.Rain, s.Wetness, s.DustAmount, s.HeatHaze);

    // ---- calendar ----

    [Fact]
    public void Season_lengths_are_shares_of_the_year_rounded_with_the_last_taking_the_rest()
    {
        Assert.Equal([75, 25], SeasonCalendar.Lengths([900, 300], 100));              // Desert
        Assert.Equal([33, 17, 50], SeasonCalendar.Lengths([200, 100, 300], 100));     // Skinner's Roam
        Assert.Equal([50, 50], SeasonCalendar.Lengths([2, 2], 100));                  // Cannibal Plains
        Assert.Equal([91, 9], SeasonCalendar.Lengths([50, 5], 100));                  // Spider Plains
        Assert.Equal([100], SeasonCalendar.Lengths([1], 100));                        // most regions: one season of share 1
        Assert.Equal([50, 50], SeasonCalendar.Lengths([0, 0], 100));                  // a share below 1 counts as days per year / count
        Assert.Equal([94, 6], SeasonCalendar.Lengths([0.5f, 3], 100));               // 0.5 counts as 50 (100 / 2): 50 of 53 -> 94, the rest 6
        Assert.Equal([120, 360, 240], SeasonCalendar.Lengths([1, 3, 2], 720));
        Assert.Equal(100, SeasonCalendar.Lengths([300, 100, 100, 7], 100).Sum());
    }

    [Fact]
    public void The_calendar_walks_the_seasons_and_wraps_the_year()
    {
        var a = Weather("A");
        var b = Weather("B");
        var def = Region("Desert", 1, (Season("blasts", 1, 1, Entry(a, 1, 1000, 1000)), 900), (Season("summer", 1, 1, Entry(b, 1, 1000, 1000)), 300));
        var region = new WeatherRegion(def, 0, 7, WeatherTime.At(0));
        Assert.Equal((0, 75), (region.SeasonIndex, region.SeasonEndDay));
        region.Update(WeatherTime.At(74, 23.9f));
        Assert.Equal(0, region.SeasonIndex);
        region.Update(WeatherTime.At(75));
        Assert.Equal((1, 100), (region.SeasonIndex, region.SeasonEndDay));
        Assert.Equal("B", region.Weather.Name);                                        // a weather is chosen at once
        region.Update(WeatherTime.At(100));
        Assert.Equal((0, 175), (region.SeasonIndex, region.SeasonEndDay));
        Assert.Equal("A", region.Weather.Name);
    }

    // ---- choosing ----

    [Fact]
    public void The_pick_is_weighted_by_the_season_weights()
    {
        var w0 = Weather("W0"); var w1 = Weather("W1"); var w2 = Weather("W2");
        var region = OneRegion(Season("s", 0, 1, Entry(w0, 1, 1, 1), Entry(w1, 3, 1, 1), Entry(w2, 6, 1, 1)));
        var picks = Picks(region, 0, 30000);
        double n = picks.Count;
        Assert.True(n > 10000);   // float hours lose a minute now and then, so not every minute is a pick
        foreach (var (name, share) in new[] { ("W0", 0.1), ("W1", 0.3), ("W2", 0.6) })
            Assert.InRange(picks.Count(p => p.Name == name) / n, share - 0.02, share + 0.02);
    }

    [Fact]
    public void Zero_weight_weathers_are_never_picked_and_a_single_candidate_is_taken()
    {
        var a = Weather("A"); var b = Weather("B");
        var region = OneRegion(Season("s", 0, 1, Entry(a, 0, 1, 1), Entry(b, 5, 1, 1)));
        Assert.All(Picks(region, 0, 2000), p => Assert.Equal("B", p.Name));
    }

    [Fact]
    public void Reroll_starts_a_different_weather_of_the_season_and_fails_without_one()
    {
        var a = Weather("A"); var b = Weather("B"); var c = Weather("C");
        var region = OneRegion(Season("s", 0, 1, Entry(a, 1, 500, 500), Entry(b, 1, 500, 500), Entry(c, 0, 500, 500)));
        for (int i = 0; i < 50; i++)
        {
            string before = region.Weather.Name;
            var now = WeatherTime.At(0, i * 0.1);
            Assert.True(region.Reroll(now));
            Assert.NotEqual(before, region.Weather.Name);
            Assert.NotEqual("C", region.Weather.Name);                                  // weight 0 stays out
            Assert.Equal(now.Minutes, region.WeatherStartMinute);
        }
        Assert.False(OneRegion(Season("one", 0, 1, Entry(a, 1, 500, 500))).Reroll(WeatherTime.At(0, 1)));
    }

    [Fact]
    public void Time_limited_weathers_only_count_inside_their_hours_and_end_at_the_end_time()
    {
        var plain = Weather("plain");
        var hot = Weather("hot", b => { b.Start = 7; b.End = 22; });
        var season = Season("s", 0, 1, Entry(plain, 1, 30, 30), Entry(hot, 99, 30, 30));
        Assert.Equal(["plain", "hot", "Default"], season.Weathers.Select(w => w.Weather.Name));   // Default appended as the fallback
        Assert.Equal(0, season.Weathers[^1].Weight);
        var picks = Picks(OneRegion(season, seed: 3), 0, 1440 * 20);
        var hotPicks = picks.Where(p => p.Name == "hot").ToList();
        Assert.True(hotPicks.Count > 200);
        Assert.All(hotPicks, p => Assert.InRange(p.Hour, 7, 22));
        Assert.All(picks.Where(p => p.Hour is < 7 or > 22), p => Assert.Equal("plain", p.Name));
        // The end is cut at today's end time (22:00); picked inside the last hour (22:xx) it is already past and the rolled duration stands.
        Assert.All(hotPicks.Where(p => p.Hour < 22), p => Assert.True(p.End <= Math.Floor(p.Start / 1440) * 1440 + 22 * 60 + 1e-6));
        Assert.All(hotPicks.Where(p => p.Hour == 22), p => Assert.Equal(30, p.End - p.Start));
    }

    [Fact]
    public void A_season_of_only_time_limited_weathers_falls_back_to_default_at_night()
    {
        var hot = Weather("hot", b => { b.Start = 7; b.End = 22; });
        var picks = Picks(OneRegion(Season("s", 0, 1, Entry(hot, 1, 30, 30)), seed: 5), 0, 1440 * 3);
        Assert.Contains(picks, p => p.Name == "Default" && p.Hour < 7);
        Assert.All(picks.Where(p => p.Name == "hot"), p => Assert.InRange(p.Hour, 7, 22));
        Assert.All(picks.Where(p => p.Hour is < 7 or > 22), p => Assert.Equal("Default", p.Name));
    }

    [Fact]
    public void A_season_without_weathers_gets_default_with_limits_zero_to_one()
    {
        var season = SeasonDef.Build("empty", "empty", [], 0.7f, 0.8f, WeatherDef.Clear);
        Assert.Equal("Default", Assert.Single(season.Weathers).Weather.Name);
        Assert.Equal((0f, 1f), (season.StrengthLimitMin, season.StrengthLimitMax));
    }

    [Fact]
    public void Window_test_matches_the_game_including_the_inverted_window()
    {
        var w = Weather("w", b => { b.Start = 7; b.End = 22; });
        Assert.True(w.IsInWindow(7)); Assert.True(w.IsInWindow(22)); Assert.False(w.IsInWindow(6)); Assert.False(w.IsInWindow(23));
        var inverted = Weather("i", b => { b.Start = 22; b.End = 7; });
        Assert.True(Enumerable.Range(0, 24).All(inverted.IsInWindow));   // hour <= start or hour >= end is always true
        Assert.True(Weather("none").IsInWindow(3));
    }

    [Fact]
    public void Durations_stay_within_the_season_range_and_reach_both_ends()
    {
        var region = OneRegion(Season("s", 0, 1, Entry(Weather("A"), 1, 30, 120)));
        var picks = new List<int>();
        int serial = region.Serial;
        for (int m = 0; m < 400000; m++)
        {
            region.Update(WeatherTime.At(m / 1440, m % 1440 / 60.0));
            if (region.Serial != serial) { serial = region.Serial; picks.Add((int)(region.WeatherEndMinute - region.WeatherStartMinute)); }
        }
        Assert.True(picks.Count > 4000);
        Assert.InRange(picks.Min(), 30, 31);
        Assert.InRange(picks.Max(), 119, 120);
        Assert.All(picks, d => Assert.InRange(d, 30, 120));
        Assert.Equal(30, picks.Min());
        Assert.Equal(120, picks.Max());
    }

    [Fact]
    public void Strength_is_season_limits_times_region_multipliers_clamped_to_one()
    {
        var season = Season("s", 0.5f, 0.8f, Entry(Weather("A"), 1, 1, 1));
        var picks = Picks(OneRegion(season), 0, 5000);
        Assert.All(picks, p => Assert.InRange(p.Strength, 0.5f, 0.8f));
        Assert.True(picks.Max(p => p.Strength) - picks.Min(p => p.Strength) > 0.25f);
        Assert.All(Picks(OneRegion(season, multMin: 0.5f, multMax: 1), 0, 5000), p => Assert.InRange(p.Strength, 0.25f, 0.8f));
        Assert.All(Picks(OneRegion(season, multMin: 2, multMax: 3), 0, 2000), p => Assert.Equal(1f, p.Strength));
    }

    // ---- determinism and saving ----

    [Fact]
    public void A_seed_gives_the_same_weather_and_another_seed_a_different_one()
    {
        WeatherWorld Run(int seed)
        {
            var world = TwoRegionWorld(Weather("A", b => b.WindMax = 100), Weather("B", b => b.WindMax = 100), seed);
            for (int m = 0; m < 3000; m += 7)
                world.Update(new Vector3(50, 0, 50), WeatherTime.FromHours(m / 60.0), new FrameTimes(0.1f, 0.1f, 0.1f), 0.5f);
            return world;
        }
        var a = Run(5); var b = Run(5); var c = Run(6);
        Assert.True(a.Snapshot().SequenceEqual(b.Snapshot()));
        Assert.False(a.Snapshot().SequenceEqual(c.Snapshot()));
        Assert.Equal(Key(a.Current), Key(b.Current));
    }

    [Fact]
    public void A_snapshot_restores_the_schedule()
    {
        var a = Weather("A"); var b = Weather("B");
        var season = Season("s", 0, 1, Entry(a, 1, 60, 300), Entry(b, 1, 60, 300));
        var first = OneRegion(season, seed: 2);
        Picks(first, 0, 2000);
        var snapshot = first.Snapshot();
        var second = OneRegion(season, seed: 99);
        second.Restore(snapshot, WeatherTime.At(1, 9.3f));
        Assert.Equal(snapshot, second.Snapshot());
        Assert.Equal(first.Weather, second.Weather);
        Assert.Equal(first.Strength, second.Strength);
        Assert.Equal(("s", first.Weather.StringId), (snapshot.SeasonId, snapshot.WeatherId));

        // A mod that reorders the season's weathers does not shift the restored one: the ids find it.
        var reordered = OneRegion(Season("s", 0, 1, Entry(b, 1, 60, 300), Entry(a, 1, 60, 300)), seed: 7);
        reordered.Restore(snapshot, WeatherTime.At(1, 9.3f));
        Assert.Equal(first.Weather.Name, reordered.Weather.Name);
    }

    // ---- wind ----

    [Fact]
    public void Wind_speed_stays_in_the_weather_range_and_each_update_turns_and_slows_by_at_most_its_limit()
    {
        var calm = Weather("calm", b => { b.WindMin = 10; b.WindMax = 50; b.UpdateMinutes = 20; b.UpdateLimit = 20; });
        var gale = Weather("gale", b => { b.WindMin = 10; b.WindMax = 50; b.UpdateMinutes = 20; b.UpdateLimit = 20; });
        var region = OneRegion(Season("s", 0, 1, Entry(calm, 1, 200, 700), Entry(gale, 1, 200, 700)), seed: 4);
        float k = 20f / 360, turn = MathF.PI * k;
        Assert.InRange(region.WindSpeed, 10, 50);
        Assert.Equal(1f, region.WindDirection.Length(), 4);
        var seen = new HashSet<double>();
        int updates = 0;
        for (int m = 0; m < 1440 * 5; m++)
        {
            region.Update(WeatherTime.At(m / 1440, m % 1440 / 60.0));
            Assert.InRange(region.WindSpeed, 10 - 1e-3, 50 + 1e-3);
            var w = region.Wind;
            if (w.StartMinute == 0 || !seen.Add(w.StartMinute)) continue;
            updates++;
            Assert.True(MathF.Abs(WeatherRegion.WrapPi(w.Heading1 - w.Heading0)) <= turn + 1e-4f, $"turn {w.Heading0} -> {w.Heading1}");
            Assert.True(MathF.Abs(w.Speed1 - w.Speed0) <= k * 40 + 1e-3f);
            Assert.Equal(1, w.DurationMinutes);                                         // round(0.05 * 20)
        }
        Assert.InRange(updates, 5 * 72 - 20, 5 * 72 + 20);                             // every 20 minutes, plus the weather changes' own
    }

    [Fact]
    public void Wind_interpolation_takes_five_percent_of_the_update_time_and_the_short_way_round()
    {
        var w = new WindInterpolation(100, 18, 10, 3.0f, 30, -3.0f);
        Assert.Equal(10f, w.At(100).Speed);
        Assert.Equal(20f, w.At(109).Speed, 4);
        Assert.Equal(30f, w.At(500).Speed);
        // 3.0 -> -3.0 is a turn of 0.283 rad through pi, not 6 rad through zero.
        var mid = w.At(109).Direction;
        Assert.True(mid.X < -0.99f);
        var region = OneRegion(Season("s", 0, 1, Entry(Weather("x", b => b.UpdateMinutes = 360), 1, 5000, 5000)));
        float duration = 0;
        for (int m = 0; m < 800 && duration == 0; m++)
        {
            region.Update(WeatherTime.At(0, m / 60.0));
            if (region.Wind.StartMinute > 0) duration = (float)region.Wind.DurationMinutes;
        }
        Assert.Equal(18f, duration);                                                    // round(0.05 * 360)
    }

    [Fact]
    public void A_wind_update_time_of_never_gives_a_wind_that_never_changes_inside_a_weather()
    {
        var steady = Weather("steady", b => b.UpdateMinutes = WeatherDef.NeverMinutes);
        var region = OneRegion(Season("s", 0, 1, Entry(steady, 1, 5000, 5000)));
        var (speed, direction) = (region.WindSpeed, region.WindDirection);
        for (int m = 0; m < 4000; m += 10) region.Update(WeatherTime.At(m / 1440, m % 1440 / 60.0));
        Assert.Equal((speed, direction), (region.WindSpeed, region.WindDirection));
    }

    // ---- fog ----

    [Fact]
    public void Fog_distance_is_the_max_when_the_fog_wind_values_match_else_interpolated_by_wind()
    {
        var steady = Weather("steady", b => { b.Fog = true; b.FogMin = 10000; b.FogMax = 25000; });   // wind values equal (0, 0), the loader's rule makes min = max
        Assert.Equal(10000f, steady.FogDistanceAt(0));                                               // (hand-made: the loader rule is applied in WeatherData.Load)
        var windy = Weather("windy", b => { b.Fog = true; b.FogMin = 1000; b.FogMax = 3000; b.FogWindMin = 0; b.FogWindMax = 100; });
        Assert.Equal(1000f, windy.FogDistanceAt(0));
        Assert.Equal(2000f, windy.FogDistanceAt(50));
        Assert.Equal(3000f, windy.FogDistanceAt(500));
        var misty = Weather("misty", b => { b.Fog = true; b.FogMin = 20000; b.FogMax = 20000; b.FogWindMin = 40; b.FogWindMax = 20; });
        Assert.All(new float[] { 0, 20, 30, 40, 100 }, wind => Assert.Equal(20000f, misty.FogDistanceAt(wind)));
    }

    [Fact]
    public void Fog_fades_in_over_two_and_a_half_percent_of_the_new_weathers_duration()
    {
        var clear = Weather("clear");
        var foggy = Weather("foggy", b => { b.Fog = true; b.FogMin = 20000; b.FogMax = 20000; b.FogColour = new Vector3(1, 0.5f, 0); });
        var def = Region("R", 1, (Season("a", 1, 1, Entry(clear, 1, 1000, 1000)), 1), (Season("b", 1, 1, Entry(foggy, 1, 400, 400)), 1));
        var region = new WeatherRegion(def, 0, 1, WeatherTime.At(0));
        Assert.Equal(0f, region.FogAt(0).Enabled);
        region.Update(WeatherTime.At(50));                                                          // season 2 begins on day 50
        double start = region.WeatherStartMinute;
        Assert.Equal("foggy", region.Weather.Name);
        Assert.Equal(0f, region.FogAt(start).Enabled, 4);
        Assert.Equal(0.5f, region.FogAt(start + 5).Enabled, 3);                                     // 2.5 % of 400 = 10 minutes
        var done = region.FogAt(start + 10);
        Assert.Equal(1f, done.Enabled);
        Assert.Equal(20000f, done.Distance);
        Assert.Equal(1f, region.FogAt(start + 300).Enabled);
        // While fading in from "off", the colour and distance are already the new weather's.
        var half = region.FogAt(start + 5);
        Assert.Equal(20000f, half.Distance);
        Assert.Equal(new Vector3(1, 0.5f, 0), half.Colour);
    }

    [Fact]
    public void Fog_weights_blend_the_regions_of_the_four_corner_samples()
    {
        var red = Weather("red", b => { b.Fog = true; b.FogMin = 10000; b.FogMax = 10000; b.FogColour = new Vector3(1, 0, 0); });
        var world = TwoRegionWorld(red, Weather("off"));
        // Camera 100 units into East: the two samples west of x = 0 are in West at distance 100, the two east of it inside East.
        var weights = world.FogWeights(100, 2000);
        float westRaw = 2 * (1 - 100f * 100f / (1300f * 1300f)), eastRaw = 2;
        Assert.Equal(2, weights.Count);
        Assert.Equal(westRaw / (westRaw + eastRaw), weights.Single(w => w.Region.Def.Name == "West").Weight, 5);
        Assert.Equal(eastRaw / (westRaw + eastRaw), weights.Single(w => w.Region.Def.Name == "East").Weight, 5);
        Assert.Equal(1f, weights.Sum(w => w.Weight), 5);
        // Deep inside a region there is just that region.
        var inside = world.FogWeights(60000, 2000);
        Assert.Equal(1f, Assert.Single(inside).Weight);
        Assert.Equal("East", inside[0].Region.Def.Name);
        // 1500 units into East all four samples (at ±1300) are in East.
        Assert.Single(world.FogWeights(1500, 2000));

        // The blended fog is on in proportion to West's weight, with West's colour and distance.
        var state = world.Update(new Vector3(100, 0, 2000), WeatherTime.At(0), Second, 0.5f);
        Assert.Equal(westRaw / (westRaw + eastRaw), state.FogEnabled, 5);
        Assert.Equal(10000f, state.FogDistance, 2);
        Assert.Equal(new Vector3(1, 0, 0), state.FogColour);
        Assert.Equal("East", state.RegionName);
    }

    // ---- camera ----

    [Fact]
    public void The_camera_region_only_changes_after_the_camera_is_500_units_outside_its_cell()
    {
        var world = TwoRegionWorld(Weather("A"), Weather("B"));
        string Name(float x) => world.Update(new Vector3(x, 0, 2000), WeatherTime.At(0), Second, 0.5f).RegionName;
        Assert.Equal("East", Name(100));
        for (float x = 100; x > -499; x -= 50) Assert.Equal("East", Name(x));                        // steps stay under the teleport limit; none of them switches
        Assert.Equal("East", Name(-499));
        Assert.Equal("West", Name(-500));                                                           // squared distance 250000
        Assert.Equal("West", Name(-100));                                                           // back across the border: West's cell is now the current one
        Assert.Equal("West", Name(400));
        Assert.Equal("West", Name(499));
        Assert.Equal("East", Name(501));                                                            // 501 outside West's cell (its edge is x = 0)
    }

    [Fact]
    public void The_sky_fades_over_thirty_game_seconds_and_a_teleport_or_pause_changes_that()
    {
        var west = Weather("A", b => { b.Sky = new Vector3(1, 0, 0); b.Clouds = 0; });
        var east = Weather("B", b => { b.Sky = new Vector3(0, 0, 1); b.Clouds = 1; });
        var world = TwoRegionWorld(west, east);
        var settled = world.Update(new Vector3(100, 0, 2000), WeatherTime.At(0), Second, 0.5f);       // first update snaps
        Assert.Equal(new Vector3(0, 0, 1), settled.SkyColourMultiplier);
        Assert.Equal(1f, settled.CloudDensity);
        float x = 100;
        WeatherState s = settled;
        while (s.RegionName == "East")
            s = world.Update(new Vector3(x -= 80, 0, 2000), WeatherTime.At(0), new FrameTimes(1, 0, 0), 0.5f);   // walking, with the game paused
        Assert.Equal("West", s.RegionName);
        Assert.Equal(new Vector3(0, 0, 1), s.SkyColourMultiplier);                                  // the transition starts at 0 and a paused game does not advance it
        for (int i = 0; i < 15; i++) s = world.Update(new Vector3(x, 0, 2000), WeatherTime.At(0), Second, 0.5f);
        Assert.Equal(0.5f, s.SkyColourMultiplier.X, 4);
        Assert.Equal(0.5f, s.CloudDensity, 4);
        s = world.Update(new Vector3(x, 0, 2000), WeatherTime.At(0), new FrameTimes(1, 0, 0), 0.5f);   // paused: frozen
        Assert.Equal(0.5f, s.SkyColourMultiplier.X, 4);
        for (int i = 0; i < 15; i++) s = world.Update(new Vector3(x, 0, 2000), WeatherTime.At(0), Second, 0.5f);
        Assert.Equal(new Vector3(1, 0, 0), s.SkyColourMultiplier);
        Assert.Equal(0f, s.CloudDensity);

        // A teleport (more than about 89 units in a frame, here across the border) snaps instead of fading.
        s = world.Update(new Vector3(x + 5000, 0, 2000), WeatherTime.At(0), Second, 0.5f);
        Assert.Equal("East", s.RegionName);
        Assert.Equal(new Vector3(0, 0, 1), s.SkyColourMultiplier);
        Assert.Equal(1f, s.CloudDensity);
    }

    [Fact]
    public void Rain_is_intensity_times_strength_and_the_drift_is_the_wind()
    {
        var rainy = Weather("rainy", b => { b.Rain = 80; b.WindMin = 20; b.WindMax = 20; });
        var world = TwoRegionWorld(rainy, rainy);
        var s = world.Update(new Vector3(100, 0, 2000), WeatherTime.At(0), Second, 0.5f);
        Assert.Equal(80f, s.Rain);                                                                 // the region's strength is 1 (limits 1..1)
        Assert.Equal(20f, s.WindSpeed, 3);
        Assert.Equal(s.WindDirection * 20, s.CloudDrift);
        Assert.Equal(1f, s.WindDirection.Length(), 4);
        Assert.True(world.ForceWeather("rainy", 0.25f));                                           // a forced weather at strength 0.25
        Assert.Equal(20f, world.Update(new Vector3(100, 0, 2000), WeatherTime.At(0), Second, 0.5f).Rain, 4);
        Assert.False(world.ForceWeather("no such weather"));
    }

    [Fact]
    public void Wetness_dust_and_heat_haze_ramp_on_the_settling_time_base()
    {
        var dry = Weather("dry");
        var storm = Weather("storm", b => { b.Wetness = 1; b.Dust = 1; b.DustSlope = 0.5f; b.HeatHaze = 1; b.Rain = 100; });
        var world = TwoRegionWorld(dry, dry);
        world.ForceWeather(dry);
        var s = world.Update(new Vector3(0, 0, 0), WeatherTime.At(0), Second, 1f);
        Assert.Equal((0f, 0f), (s.Wetness, s.HeatHaze));
        world.ForceWeather(storm, 1, snap: false);
        s = world.Update(new Vector3(0, 0, 0), WeatherTime.At(0), new FrameTimes(10, 10, 10), 1f);
        Assert.Equal(0.1f, s.Wetness, 5);                                                          // +0.01 per second
        Assert.Equal(0.17f, s.DustAmount.X, 5);                                                    // +0.017 x target per second
        Assert.Equal(0.5f, s.DustAmount.Z, 5);                                                     // slope moves 0.1 per second to 0.5 (reached after 5)
        Assert.Equal(1f, s.HeatHaze, 5);                                                           // 1/3 per second, so 3 s
        s = world.Update(new Vector3(0, 0, 0), WeatherTime.At(0), new FrameTimes(90, 90, 90), 1f);
        Assert.Equal(1f, s.Wetness, 5);
        Assert.Equal(1f, s.DustAmount.X, 5);
        // Paused: the real time still passes, the settling base runs at 0.01.
        world.ForceWeather(dry, 1, snap: false);
        s = world.Update(new Vector3(0, 0, 0), WeatherTime.At(0), FrameTimes.FromClock(10, 1, paused: true), 1f);
        Assert.Equal(1f - 0.005f * 10 * 0.01f, s.Wetness, 5);
    }

    [Fact]
    public void Wetness_and_dust_ramps_follow_the_game_rates()
    {
        Assert.Equal(0.3f, WeatherRamps.Wetness(0, 1, 30), 5);
        Assert.Equal(1f, WeatherRamps.Wetness(0.99f, 1, 30), 5);                                   // lands on the target
        Assert.Equal(0.5f, WeatherRamps.Wetness(0.6f, 0, 20), 5);                                  // dries 0.005 per second
        Assert.Equal(0f, WeatherRamps.Wetness(0.01f, 0, 20));
        Assert.Equal(0.8f, WeatherRamps.Wetness(0.8f, 0.4f, 100));                                 // a lighter rain than the current wetness: held
        Assert.Equal(0.34f, WeatherRamps.Dust(0, 1, 20), 5);                                       // 0.017 x target x 20 s
        Assert.Equal(0.986f, WeatherRamps.Dust(0, 1, 58), 5);                                      // about a minute to full
        Assert.Equal(1f, WeatherRamps.Dust(0, 1, 59));
        Assert.Equal(0.99f, WeatherRamps.Dust(1, 0.5f, 10), 5);                                    // 0.001 per second down
        Assert.Equal(0.5f, WeatherRamps.Dust(0.6f, 0.5f, 1000), 5);
        Assert.Equal(0.4f, WeatherRamps.DustSlope(0.5f, 0, 1), 5);
        Assert.Equal(0f, WeatherRamps.DustSlope(0.05f, 0, 1));
    }

    [Fact]
    public void Frame_times_follow_the_three_bases()
    {
        var running = FrameTimes.FromClock(0.02f, 3, paused: false);
        Assert.Equal((0.02f, 0.06f, 0.06f), (running.Real, running.Game, running.Settling));
        var paused = FrameTimes.FromClock(0.02f, 3, paused: true);
        Assert.Equal((0.02f, 0f), (paused.Real, paused.Game));
        Assert.Equal(0.0002f, paused.Settling, 6);
    }

    [Fact]
    public void Time_converts_between_hours_and_day_plus_time_of_day()
    {
        var t = WeatherTime.FromHours(24 * 3 + 5.5);
        Assert.Equal((3, 5.5f), (t.Day, t.Hours));
        Assert.Equal(3 * 1440 + 330.0, t.Minutes);
        Assert.Equal(5, t.WholeHour);
    }

    [Fact]
    public void Areas_cells_use_the_zone_grid()
    {
        Assert.Equal((32, 32), WeatherAreas.CellOf(0, 0));
        Assert.Equal((31, 31), WeatherAreas.CellOf(-0.5f, -4608));
        Assert.Equal((63, 0), WeatherAreas.CellOf(147455, -147456));
        Assert.Equal((64, 64), WeatherAreas.CellOf(147456, 147456));
        Assert.Equal(0f, WeatherAreas.DistanceSquaredToCell(32, 32, 100, 100));
        Assert.Equal(250000f, WeatherAreas.DistanceSquaredToCell(32, 32, -500, 100));
        Assert.Equal(2 * 300f * 300f, WeatherAreas.DistanceSquaredToCell(32, 32, -300, 4908), 1);
    }

    // ---- the game's records ----

    [Fact]
    [Slow]
    public void Areas_map_places_known_towns_in_their_regions()
    {
        var install = InstallData.Install;
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = InstallData.BaseGame!;
        var data = WeatherData.Load(db);
        var areas = WeatherAreas.Load(install!);
        var towns = InstallData.Levels!.Towns().Select(t => (Name: db.Find(t.TownId)?.Name ?? "", t.Position)).ToList();
        (string Town, string Region)[] expected =
        [
            ("The Hub", "Border Zone"), ("Squin", "Border Zone"), ("Rebirth", "Rebirth"), ("Heng", "Heng"), ("Bast", "Bast"), ("Flats Lagoon", "Flats Lagoon"),
            ("Mongrel", "Fog Islands"), ("Ashland Dome I", "Ashlands"), ("Ashland Dome II", "Ashlands"), ("Ashland Dome III", "Ashlands"), ("Tower of Abuse", "Venge"),
            ("Heft", "The Great Desert"), ("Stoat", "The Great Desert"), ("Shark", "The Swamp"), ("Swamp Village", "The Swamp"), ("Admag", "Stenn Desert"),
        ];
        foreach (var (town, region) in expected)
        {
            var placed = towns.Where(t => t.Name == town).ToList();
            Assert.NotEmpty(placed);
            foreach (var t in placed)
                Assert.Equal(region, areas.RegionAt(data, t.Position.X, t.Position.Z).Name);
        }
        // The mirrored map would put Rebirth in the swamp and the Ashland domes elsewhere: the placement is not a coincidence of symmetric regions.
        var rebirth = towns.First(t => t.Name == "Rebirth").Position;
        Assert.NotEqual("Rebirth", areas.RegionAt(data, rebirth.X, -rebirth.Z).Name);
    }

    [Fact]
    [Slow]
    public void Base_game_records_follow_the_loader_rules()
    {
        Assert.SkipWhen(InstallData.Install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = InstallData.BaseGame!;
        var data = WeatherData.Load(db);
        Assert.Equal(100, data.DaysPerYear);
        Assert.Equal(53, data.Weathers.Count);
        Assert.Equal(74, data.Regions.Count);
        Assert.Equal("Default", data.Default.Name);
        Assert.Equal(WeatherData.NoneRegionId, data.None.StringId);

        // The fog distance is complete at fog distance max for every base weather (misty rain has different wind values but equal distances).
        var expected = new Dictionary<string, float>
        {
            ["Dust Storm Approach"] = 25000, ["Dust Storm Approach SHORT"] = 190000, ["Heavy_Rain sonorous"] = 30000, ["Heavy_Rain sonorous acid"] = 30000,
            ["Kenshi_Ash-Flakes"] = 35000, ["Kenshi_Ash-Flakes_BLACK_and_Rising Steam"] = 35000, ["misty rain"] = 20000, ["Sand stream"] = 20000, ["shek desert storm"] = 15000,
        };
        Assert.Equal(expected.Keys.Order(), data.Weathers.Where(w => w.FogEnabled).Select(w => w.Name).Order());
        foreach (var w in data.Weathers.Where(w => w.FogEnabled))
            foreach (float wind in new float[] { 0, 10, 20, 30, 40, 55, 100, 700 })
                Assert.Equal(expected[w.Name], w.FogDistanceAt(wind));
        // The loader's rule against the raw record, and "update time 0 means never".
        foreach (var raw in db.OfType(FcsRecordType.WEATHER))
        {
            var w = data.Weathers.First(x => x.StringId == raw.StringId);
            if (raw.GetFloat("fog wind min") == raw.GetFloat("fog wind max"))
                Assert.Equal(Math.Max(raw.GetFloat("fog distance min"), raw.GetFloat("fog distance max")), w.FogDistanceMin);
            Assert.Equal(raw.GetInt("wind update time") == 0 ? WeatherDef.NeverMinutes : raw.GetInt("wind update time"), w.WindUpdateMinutes);
            Assert.True(w.WindUpdateMinutes > 0);
        }
    }

    [Fact]
    [Slow]
    public void Base_game_seasons_and_regions_follow_the_calendar_and_fallback_rules()
    {
        Assert.SkipWhen(InstallData.Install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = InstallData.BaseGame!;
        var data = WeatherData.Load(db);
        string Lengths(string region) => string.Join("/", data.FindRegion(region)!.Seasons.Select(s => s.Days));
        Assert.Equal("75/25", Lengths("Desert"));
        Assert.Equal(["Desert Blasts", "Desert Summer"], data.FindRegion("Desert")!.Seasons.Select(s => s.Season.Name));
        Assert.Equal("33/17/50", Lengths("Skinner's Roam"));
        Assert.Equal(["Clear Times", "Clear AND Storm Mix SHORTSEASON", "Dust Storm Approach"], data.FindRegion("Skinner's Roam")!.Seasons.Select(s => s.Season.Name));
        Assert.Equal("50/50", Lengths("Cannibal Plains"));
        Assert.Equal("91/9", Lengths("Spider Plains"));
        Assert.Equal(["south coast", "purple desert"], data.FindRegion("Spider Plains")!.Seasons.Select(s => s.Season.Name));
        Assert.Equal("100", Lengths("Venge"));

        // Regions without seasons are always clear.
        foreach (var name in new[] { "Central", "Arm of Okran", "Berserker Country", "Empire", "Obedience", "Rebirth", "Watcher's Rim" })
        {
            var r = data.FindRegion(name)!;
            Assert.True(r.HasNoSeasons, name);
            Assert.Equal("Default", Assert.Single(Assert.Single(r.Seasons).Season.Weathers).Weather.Name);
        }
        Assert.True(data.None.HasNoSeasons);
        Assert.Equal(66, data.Regions.Count(r => !r.HasNoSeasons));

        // A season with no weather gets Default with limits 0..1; a season with a time-limited weather gets Default appended with weight 0.
        var empty = db.OfType(FcsRecordType.SEASON).Where(s => s.GetReferences("weathers").Count == 0).ToList();
        Assert.NotEmpty(empty);
        foreach (var raw in empty)
        {
            var s = data.Seasons.First(x => x.StringId == raw.StringId);
            Assert.Equal("Default", Assert.Single(s.Weathers).Weather.Name);
            Assert.Equal((0f, 1f), (s.StrengthLimitMin, s.StrengthLimitMax));
        }
        var windowed = data.Seasons.Where(s => s.Weathers.Any(w => w.Weather.HasTimeWindow)).ToList();
        Assert.Equal(["Desert Calm hot 0.6", "venge beams"], windowed.SelectMany(s => s.Weathers).Select(w => w.Weather).Where(w => w.HasTimeWindow).Select(w => w.Name).Distinct().Order());
        Assert.All(windowed, s => Assert.Equal(("Default", 0), (s.Weathers[^1].Weather.Name, s.Weathers[^1].Weight)));
        var venge = data.FindRegion("Venge")!.Seasons[0].Season;
        Assert.Equal(5f, venge.Weathers.Single(w => w.Weather.Name.StartsWith("venge beams")).Weather.StartTime);
        Assert.Equal(23f, venge.Weathers.Single(w => w.Weather.Name.StartsWith("venge beams")).Weather.EndTime);
        Assert.Equal(0.5f, venge.StrengthLimitMin);
    }

    [Fact]
    [Slow]
    public void The_whole_base_world_runs_for_a_year_with_sane_values_and_is_deterministic()
    {
        var install = InstallData.Install;
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = InstallData.BaseGame!;
        WeatherWorld Run(int seed, List<WeatherState>? log)
        {
            var world = WeatherWorld.Create(db, install!, seed, WeatherTime.At(0));
            var path = new[] { new Vector3(-50979, 0, 2932), new Vector3(94355, 0, 94196), new Vector3(23826, 0, 17893), new Vector3(8698, 0, -13295) };
            for (int minute = 0; minute < 365 * 1440 / 2; minute += 5)
            {
                var camera = path[minute / (1440 * 20) % path.Length] + new Vector3(minute % 400, 0, 0);
                var s = world.Update(camera, WeatherTime.At(minute / 1440, minute % 1440 / 60.0), new FrameTimes(0.05f, 0.05f, 0.05f), 0.3f);
                Assert.InRange(s.Strength, 0f, 1f);
                Assert.InRange(s.Rain, 0f, 100f);
                Assert.InRange(s.Wetness, 0f, 1f);
                Assert.InRange(s.FogEnabled, 0f, 1f);
                Assert.InRange(s.HeatHaze, 0f, 1f);
                Assert.True(float.IsFinite(s.FogDistance) && float.IsFinite(s.WindSpeed) && float.IsFinite(s.DustAmount.X));
                log?.Add(s);
            }
            return world;
        }
        var a = new List<WeatherState>();
        var b = new List<WeatherState>();
        var first = Run(11, a); var second = Run(11, b);
        Assert.Equal(a.Select(Key), b.Select(Key));
        Assert.True(first.Snapshot().SequenceEqual(second.Snapshot()));
        Assert.True(a.Select(s => s.Weather.Name).Distinct().Count() > 5);
    }
}
