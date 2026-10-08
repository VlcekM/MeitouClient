using System.Numerics;
using Meitou.Content;

namespace Meitou.Data.World;

/// <summary>
/// The weather of the whole world and of the camera (docs/formats/weather.md): every region's schedule (<see cref="WeatherRegion"/>) runs all the time, and
/// <see cref="Update"/> turns the camera position, the game time and the frame times into a <see cref="WeatherState"/>: the region under the camera (with the
/// game's 500-unit hysteresis), the 30 s sky and cloud transition, the four-sample fog blend, rain, and the wetness, dust and heat-haze ramps. Deterministic for a seed and a
/// sequence of calls; no wall-clock time is read.
/// </summary>
public sealed class WeatherWorld
{
    /// <summary>The camera region only changes once the camera is this far outside the current cell's rectangle (squared: 250000).</summary>
    public const float RegionHysteresis = 500;
    /// <summary>A camera move of more than this squared distance in the ground plane in one frame is a teleport (about 89 units): values snap.</summary>
    public const float TeleportDistanceSquared = 8000;
    /// <summary>Seconds (game-speed) of the sky colour and cloud density transition.</summary>
    public const float SkyTransitionSeconds = 30;
    /// <summary>The fog is sampled at the camera ± this in x and z.</summary>
    public const float FogSampleOffset = 1300;

    readonly WeatherAreas areas;
    readonly Dictionary<string, int> regionIndex = new(StringComparer.Ordinal);

    // camera side
    bool hasCamera, snapRequested;
    Vector2 lastCamera;
    int cellX, cellZ;
    WeatherDef? skyWeather;
    Vector3 skyFrom = Vector3.One, skyTo = Vector3.One, sky = Vector3.One;
    float cloudFrom, cloudTo, cloud, skyElapsed;
    float wetness, dustX, dustSlope, heatHaze;

    WeatherDef? forced;
    float forcedStrength = 1;

    public WeatherWorld(WeatherData data, WeatherAreas areas, int seed, WeatherTime start)
    {
        Data = data;
        this.areas = areas;
        Seed = seed;
        var regions = new List<WeatherRegion>();
        for (int i = 0; i < data.Regions.Count; i++)
        {
            regions.Add(new WeatherRegion(data.Regions[i], i, seed, start));
            regionIndex[data.Regions[i].StringId] = i;
        }
        Regions = regions;
        Time = start;
    }

    /// <summary>The world made from a game database and its install (<c>areasmap.tga</c>).</summary>
    public static WeatherWorld Create(GameDatabase db, GameInstall install, int seed, WeatherTime start) =>
        new(WeatherData.Load(db), WeatherAreas.Load(install), seed, start);

    public WeatherData Data { get; }
    public WeatherAreas Areas => areas;
    public int Seed { get; }
    /// <summary>Every region's schedule, in <see cref="WeatherData.Regions"/> order.</summary>
    public IReadOnlyList<WeatherRegion> Regions { get; }
    /// <summary>The game time of the last <see cref="Update"/>.</summary>
    public WeatherTime Time { get; private set; }
    /// <summary>The state of the last <see cref="Update"/> (<see cref="WeatherState.Clear"/> before the first).</summary>
    public WeatherState Current { get; private set; } = WeatherState.Clear;

    /// <summary>The region the camera is in (null before the first update or while a weather is forced).</summary>
    public WeatherRegion? CameraRegion { get; private set; }

    /// <summary>The weather being forced, or null when the scheduler decides.</summary>
    public WeatherDef? ForcedWeather => forced;

    /// <summary>The wind direction a forced weather blows in (the schedule's own wind is not used then).</summary>
    public Vector2 ForcedWindDirection { get; set; } = Vector2.UnitX;

    /// <summary>The schedule of the region named <paramref name="name"/> (exact, else by substring); null when there is none.</summary>
    public WeatherRegion? Region(string name) => Data.FindRegion(name) is { } def ? RegionOf(def) : null;

    public WeatherRegion RegionOf(RegionDef def) => Regions[regionIndex[def.StringId]];

    /// <summary>The schedule of the region at a world position.</summary>
    public WeatherRegion RegionAt(float x, float z) => RegionOf(areas.RegionAt(Data, x, z));

    /// <summary>
    /// Forces one weather at the camera (the viewer's <c>--weather</c>), at <paramref name="strength"/>, wind at its range for that strength. The sky,
    /// clouds and ramps snap to it on the next update unless <paramref name="snap"/> is false (then they fade and ramp like a weather change). <c>null</c> hands the camera back to the scheduler.
    /// </summary>
    public void ForceWeather(WeatherDef? weather, float strength = 1, bool snap = true)
    {
        forced = weather;
        forcedStrength = Math.Clamp(strength, 0, 1);
        snapRequested |= snap;
    }

    /// <summary>Forces the weather whose name matches; false when there is none.</summary>
    public bool ForceWeather(string? name, float strength = 1, bool snap = true)
    {
        var w = Data.FindWeather(name);
        if (w is null) return false;
        ForceWeather(w, strength, snap);
        return true;
    }

    /// <summary>The next update treats the camera as teleported (everything snaps), as a screenshot wants.</summary>
    public void Snap() => snapRequested = true;

    /// <summary>
    /// One frame. <paramref name="camera"/> is the camera position (X and Z are the ground plane), <paramref name="time"/> the game day and time of day,
    /// <paramref name="dt"/> the frame times (see <see cref="FrameTimes"/>; paused is expressed there), <paramref name="sunHeight"/> the Y of the sun direction.
    /// </summary>
    public WeatherState Update(Vector3 camera, WeatherTime time, FrameTimes dt, float sunHeight)
    {
        Time = time;
        foreach (var r in Regions) r.Update(time);
        double minute = time.Minutes;

        var here = new Vector2(camera.X, camera.Z);
        bool teleport = !hasCamera || snapRequested || Vector2.DistanceSquared(here, lastCamera) > TeleportDistanceSquared;
        snapRequested = false;
        (cellX, cellZ) = !hasCamera || WeatherAreas.DistanceSquaredToCell(cellX, cellZ, here.X, here.Y) >= RegionHysteresis * RegionHysteresis
            ? WeatherAreas.CellOf(here.X, here.Y) : (cellX, cellZ);
        hasCamera = true;
        lastCamera = here;

        WeatherRegion? region = null;
        WeatherDef weather;
        float s, speed;
        Vector2 direction;
        string regionName;
        float fogEnabled;
        Vector3 fogColour;
        float fogDistance;
        if (forced is { } f)
        {
            weather = f; s = forcedStrength; speed = f.WindSpeedAt(s); direction = ForcedWindDirection; regionName = "";
            fogEnabled = f.FogEnabled ? 1 : 0; fogColour = f.FogColour; fogDistance = f.FogDistanceAt(speed);
        }
        else
        {
            region = RegionOf(areas.RegionOf(Data, cellX, cellZ));
            weather = region.Weather; s = region.Strength; speed = region.WindSpeed; direction = region.WindDirection; regionName = region.Def.Name;
            (fogEnabled, fogColour, fogDistance) = BlendFog(here, minute, region);
        }
        CameraRegion = region;

        // Sky colour multiplier and cloud density: a linear transition of 30 game-speed seconds from the value at the change.
        if (teleport || skyWeather is null)
        {
            sky = skyFrom = skyTo = weather.SkyColourMultiplier;
            cloud = cloudFrom = cloudTo = weather.CloudsDensity;
            skyElapsed = SkyTransitionSeconds;
            skyWeather = weather;
        }
        else
        {
            if (!ReferenceEquals(weather, skyWeather))
            {
                skyWeather = weather;
                skyFrom = sky; cloudFrom = cloud;
                skyTo = weather.SkyColourMultiplier; cloudTo = weather.CloudsDensity;
                skyElapsed = 0;
            }
            skyElapsed = MathF.Min(skyElapsed + dt.Game, SkyTransitionSeconds);
            float u = skyElapsed / SkyTransitionSeconds;
            sky = Vector3.Lerp(skyFrom, skyTo, u);
            cloud = cloudFrom + (cloudTo - cloudFrom) * u;
        }

        // Wetness, dust and heat haze settle on the third time base.
        float wetTarget = weather.Wetness, dustTarget = weather.Dust * s;
        float heatTarget = HeatHaze.Target(weather.HeatHaze, s, sunHeight);
        if (teleport)
        {
            wetness = wetTarget; dustX = dustTarget; dustSlope = weather.DustSlope; heatHaze = heatTarget;
        }
        else
        {
            float d = dt.Settling;
            wetness = WeatherRamps.Wetness(wetness, wetTarget, d);
            dustX = WeatherRamps.Dust(dustX, dustTarget, d);
            dustSlope = WeatherRamps.DustSlope(dustSlope, weather.DustSlope, d);
            heatHaze = HeatHaze.Step(heatHaze, heatTarget, d);
        }

        return Current = new WeatherState
        {
            Weather = weather, RegionName = regionName, Strength = s,
            WindDirection = direction, WindSpeed = speed,
            SkyColourMultiplier = sky, CloudDensity = cloud, CloudDrift = direction * speed,
            FogEnabled = fogEnabled, FogColour = fogColour, FogDistance = fogDistance,
            Rain = weather.RainIntensity * s, Wetness = wetness,
            DustAmount = new Vector3(dustX, dustX * weather.DustInside, dustSlope), HeatHaze = heatHaze,
        };
    }

    /// <summary>
    /// The weights of the regions around a camera position: four points at the camera ± <see cref="FogSampleOffset"/> in x and z (<b>Observed</b>: the
    /// four corners; the axis points would be the other reading), each its cell's region with weight <c>1 − min(1, d² / 1300²)</c> (d = distance from the
    /// camera to that cell's rectangle); weights of one region add, then they are normalised. Returns (region, weight) pairs, at most four.
    /// </summary>
    public IReadOnlyList<(WeatherRegion Region, float Weight)> FogWeights(float x, float z)
    {
        var list = new List<(WeatherRegion, float)>(4);
        float total = 0;
        for (int i = 0; i < 4; i++)
        {
            float px = x + ((i & 1) == 0 ? -FogSampleOffset : FogSampleOffset), pz = z + ((i & 2) == 0 ? -FogSampleOffset : FogSampleOffset);
            var (cx, cz) = WeatherAreas.CellOf(px, pz);
            float d2 = WeatherAreas.DistanceSquaredToCell(cx, cz, x, z);
            float w = 1 - MathF.Min(1, d2 / (FogSampleOffset * FogSampleOffset));
            if (w <= 0) continue;
            var region = RegionOf(areas.RegionOf(Data, cx, cz));
            int at = list.FindIndex(e => ReferenceEquals(e.Item1, region));
            if (at >= 0) list[at] = (region, list[at].Item2 + w);
            else list.Add((region, w));
            total += w;
        }
        if (list.Count == 0 || total <= 0) return [(RegionOf(areas.RegionAt(Data, x, z)), 1f)];
        for (int i = 0; i < list.Count; i++) list[i] = (list[i].Item1, list[i].Item2 / total);
        return list;
    }

    (float Enabled, Vector3 Colour, float Distance) BlendFog(Vector2 camera, double minute, WeatherRegion fallback)
    {
        float enabled = 0, onWeight = 0, distance = 0;
        Vector3 colour = Vector3.Zero;
        foreach (var (region, w) in FogWeights(camera.X, camera.Y))
        {
            var fog = region.FogAt(minute);
            enabled += w * fog.Enabled;
            // Observed: colour and distance are averaged over the fog that is on, so a region without fog does not drag them to zero.
            float we = w * fog.Enabled;
            onWeight += we;
            colour += fog.Colour * we;
            distance += fog.Distance * we;
        }
        if (onWeight <= 0)
        {
            var own = fallback.FogAt(minute);
            return (0, own.Colour, own.Distance);
        }
        return (enabled, colour / onWeight, distance / onWeight);
    }

    // ---- saving ----

    /// <summary>The state of every region, for a save (no save format is defined here).</summary>
    public IReadOnlyList<WeatherRegionSnapshot> Snapshot() => Regions.Select(r => r.Snapshot()).ToList();

    /// <summary>Continues every region from a snapshot taken by <see cref="Snapshot"/>; regions are matched by StringId and any not in the snapshot keep their new schedule.</summary>
    public void Restore(IEnumerable<WeatherRegionSnapshot> snapshots, WeatherTime now)
    {
        foreach (var s in snapshots)
            if (regionIndex.TryGetValue(s.RegionId, out int i)) Regions[i].Restore(s, now);
        Snap();
    }
}
