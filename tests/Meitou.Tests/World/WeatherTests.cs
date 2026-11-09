using System.Numerics;
using Meitou.Content;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Tests.World;

/// <summary>
/// The weather data and scheduler (docs/formats/weather.md): calendar, choice, wind, fog, and the camera side. Quick tests use hand-made definitions.
/// </summary>
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
        public bool Fog;
        public Vector3 FogColour = Vector3.One;
        public float FogMin, FogMax;

        public WeatherDef Build() => new()
        {
            StringId = name, Name = name, WindSpeedMin = 0, WindSpeedMax = 60, WindUpdateMinutes = 360, WindUpdateLimit = 20,
            FogEnabled = Fog, FogColour = FogColour, FogDistanceMin = FogMin, FogDistanceMax = FogMax, SkyColourMultiplier = Vector3.One,
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

    /// <summary>
    /// Two regions split at x = 0: cells 0..31 are "West" (colour 1), 32..63 "East" (colour 2).
    /// </summary>
    static WeatherWorld TwoRegionWorld(WeatherDef west, WeatherDef east, int seed = 1)
    {
        var colours = new int[WeatherAreas.Cells * WeatherAreas.Cells];
        for (int z = 0; z < WeatherAreas.Cells; z++)
            for (int x = 0; x < WeatherAreas.Cells; x++)
                colours[z * WeatherAreas.Cells + x] = x < WeatherAreas.Origin ? 1 : 2;
        var data = WeatherData.FromDefinitions([west, east],
            [],
            [Region("West", 1, (Season("w", 1, 1, Entry(west, 1, 100000, 100000)), 1)),
                Region("East", 2, (Season("e", 1, 1, Entry(east, 1, 100000, 100000)), 1))]);
        return new WeatherWorld(data, new WeatherAreas(colours), seed, WeatherTime.At(0));
    }

    static readonly FrameTimes Second = new(1, 1, 1);

    /// <summary>
    /// The numbers of a state (records of definitions compare their lists by reference).
    /// </summary>
    static object Key(WeatherState s) => (s.Weather.Name, s.RegionName, s.Strength, s.WindDirection, s.WindSpeed, s.SkyColourMultiplier,
            s.CloudDensity, s.CloudDrift, s.FogEnabled,
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

    // ---- fog ----

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
            ("The Hub", "Border Zone"), ("Squin", "Border Zone"), ("Rebirth", "Rebirth"), ("Heng", "Heng"), ("Bast", "Bast"), ("Flats Lagoon",
                    "Flats Lagoon"),
            ("Mongrel", "Fog Islands"), ("Ashland Dome I", "Ashlands"), ("Ashland Dome II", "Ashlands"), ("Ashland Dome III",
                    "Ashlands"), ("Tower of Abuse", "Venge"),
            ("Heft", "The Great Desert"), ("Stoat", "The Great Desert"), ("Shark", "The Swamp"), ("Swamp Village", "The Swamp"), ("Admag",
                    "Stenn Desert"),
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
            ["Dust Storm Approach"] = 25000, ["Dust Storm Approach SHORT"] = 190000, ["Heavy_Rain sonorous"] = 30000,
            ["Heavy_Rain sonorous acid"] = 30000, ["Kenshi_Ash-Flakes"] = 35000, ["Kenshi_Ash-Flakes_BLACK_and_Rising Steam"] = 35000,
            ["misty rain"] = 20000, ["Sand stream"] = 20000, ["shek desert storm"] = 15000,
        };
        Assert.Equal(expected.Keys.Order(), data.Weathers.Where(w => w.FogEnabled).Select(w => w.Name).Order());
        foreach (var w in data.Weathers.Where(w => w.FogEnabled))
            foreach (float wind in new float[] { 0, 10, 20, 30, 40, 55, 100, 700 })
                Assert.Equal(expected[w.Name], w.FogDistanceAt(wind));
        // The loader's rule against the raw record, and "update time 0 means never".
        foreach (var raw in db.OfType(FcsRecordType.Weather))
        {
            var w = data.Weathers.First(x => x.StringId == raw.StringId);
            if (raw.GetFloat("fog wind min") == raw.GetFloat("fog wind max"))
                Assert.Equal(Math.Max(raw.GetFloat("fog distance min"), raw.GetFloat("fog distance max")), w.FogDistanceMin);
            Assert.Equal(raw.GetInt("wind update time") == 0 ? WeatherDef.NeverMinutes : raw.GetInt("wind update time"), w.WindUpdateMinutes);
            Assert.True(w.WindUpdateMinutes > 0);
        }

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
        var empty = db.OfType(FcsRecordType.Season).Where(s => s.GetReferences("weathers").Count == 0).ToList();
        Assert.NotEmpty(empty);
        foreach (var raw in empty)
        {
            var s = data.Seasons.First(x => x.StringId == raw.StringId);
            Assert.Equal("Default", Assert.Single(s.Weathers).Weather.Name);
            Assert.Equal((0f, 1f), (s.StrengthLimitMin, s.StrengthLimitMax));
        }
        var windowed = data.Seasons.Where(s => s.Weathers.Any(w => w.Weather.HasTimeWindow)).ToList();
        Assert.Equal(["Desert Calm hot 0.6", "venge beams"],
                windowed.SelectMany(s => s.Weathers).Select(w => w.Weather).Where(w => w.HasTimeWindow).Select(w => w.Name).Distinct().Order());
        Assert.All(windowed, s => Assert.Equal(("Default", 0), (s.Weathers[^1].Weather.Name, s.Weathers[^1].Weight)));

        // Season lengths and names per region, and the venge beams window.
        string Lengths(string region) => string.Join("/", data.FindRegion(region)!.Seasons.Select(s => s.Days));
        Assert.Equal("75/25", Lengths("Desert"));
        Assert.Equal(["Desert Blasts", "Desert Summer"], data.FindRegion("Desert")!.Seasons.Select(s => s.Season.Name));
        Assert.Equal("33/17/50", Lengths("Skinner's Roam"));
        Assert.Equal(["Clear Times", "Clear AND Storm Mix SHORTSEASON", "Dust Storm Approach"],
                data.FindRegion("Skinner's Roam")!.Seasons.Select(s => s.Season.Name));
        Assert.Equal("50/50", Lengths("Cannibal Plains"));
        Assert.Equal("91/9", Lengths("Spider Plains"));
        Assert.Equal(["south coast", "purple desert"], data.FindRegion("Spider Plains")!.Seasons.Select(s => s.Season.Name));
        Assert.Equal("100", Lengths("Venge"));
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
            var path = new[] { new Vector3(-50979, 0, 2932), new Vector3(94355, 0, 94196), new Vector3(23826, 0, 17893),
                    new Vector3(8698, 0, -13295) };
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
        var first = Run(11, a);
        var second = Run(11, b);
        Assert.Equal(a.Select(Key), b.Select(Key));
        Assert.True(first.Snapshot().SequenceEqual(second.Snapshot()));
        Assert.True(a.Select(s => s.Weather.Name).Distinct().Count() > 5);
    }
}
