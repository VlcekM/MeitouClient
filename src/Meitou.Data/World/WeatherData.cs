using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Data.World;

/// <summary>
/// <c>WeatherAffecting</c> (fcs_enums.def): the direct effect of a weather or effect on characters.
/// </summary>
public enum WeatherAffecting
{
    /// <summary>
    /// WA_NONE (0): no effect.
    /// </summary>
    None = 0,

    /// <summary>
    /// WA_DUSTSTORM (1).
    /// </summary>
    DustStorm = 1,

    /// <summary>
    /// WA_ACID (2).
    /// </summary>
    Acid = 2,

    /// <summary>
    /// WA_BURNING (3).
    /// </summary>
    Burning = 3,

    /// <summary>
    /// WA_GAS (4).
    /// </summary>
    Gas = 4,

    /// <summary>
    /// WA_RAIN (5).
    /// </summary>
    Rain = 5
}

/// <summary>
/// A live local effect that acts on characters (docs/formats/weather.md "Effects on characters"): an effect unit whose EFFECT has an <c>affect
/// type</c>, at <paramref name="Position"/>, reaching characters within <paramref name="Radius"/> (<c>effect radius</c>) with
/// <paramref name="Strength"/>.
/// </summary>
public readonly record struct WeatherAffectZone(Vector3 Position, float Radius, WeatherAffecting Affect, float Strength);

/// <summary>
/// The biome ambience of a region (fcs_enums.def <c>Ambience</c>; each names an <c>SFX_Amb_&lt;name&gt;.bnk</c>).
/// </summary>
public enum Ambience
{
    /// <summary>
    /// 0: <c>SFX_Amb_Ashlands</c>.
    /// </summary>
    Ashlands,

    /// <summary>
    /// 1: <c>SFX_Amb_Boneyard</c>.
    /// </summary>
    Boneyard,

    /// <summary>
    /// 2: <c>SFX_Amb_Canyons</c>.
    /// </summary>
    Canyons,

    /// <summary>
    /// 3: <c>SFX_Amb_Coast</c>.
    /// </summary>
    Coast,

    /// <summary>
    /// 4: <c>SFX_Amb_Desert</c>, the BIOME_GROUP default.
    /// </summary>
    Desert,

    /// <summary>
    /// 5: <c>SFX_Amb_Forest</c>.
    /// </summary>
    Forest,

    /// <summary>
    /// 6: <c>SFX_Amb_Jungle</c>.
    /// </summary>
    Jungle,

    /// <summary>
    /// 7: <c>SFX_Amb_Plains</c>.
    /// </summary>
    Plains,

    /// <summary>
    /// 8: <c>SFX_Amb_Swamplands</c>.
    /// </summary>
    Swamplands,

    /// <summary>
    /// 9: <c>SFX_Amb_Tarsands</c>.
    /// </summary>
    Tarsands
}

/// <summary>
/// The music of a region (fcs_enums.def `BiomeMusic`; the spellings are the enum's, the banks are Dunes, Savannah, Swamplands, Tarsands).
/// </summary>
public enum BiomeMusic
{
    /// <summary>
    /// 0: the Dunes music.
    /// </summary>
    Dunes,

    /// <summary>
    /// 1: the Savannah music.
    /// </summary>
    Savanah,

    /// <summary>
    /// 2: the Swamplands music.
    /// </summary>
    Swamp,

    /// <summary>
    /// 3: the Tarsands music.
    /// </summary>
    Tarsands,

    /// <summary>
    /// 4: no music, the BIOME_GROUP default.
    /// </summary>
    None
}

/// <summary>
/// <c>EffectType</c> (fcs_enums.def, docs/formats/weather.md "Spawning").
/// </summary>
public enum EffectType
{
    /// <summary>
    /// NONE (0): not a weather effect (torches, blood, fire on objects).
    /// </summary>
    None = 0,

    /// <summary>
    /// CAMERA (1): a camera group, in front of the camera and moving with it.
    /// </summary>
    Camera = 1,

    /// <summary>
    /// POINT (2): a point group, spawned at random places in the area.
    /// </summary>
    Point = 2,

    /// <summary>
    /// WANDERING (3): a wandering group, moving at the <c>wandering speed</c>.
    /// </summary>
    Wandering = 3,

    /// <summary>
    /// GLOBAL (4): a global group, on the ground at the camera centre.
    /// </summary>
    Global = 4,

    /// <summary>
    /// CAMERA_RAIN (5): a camera group.
    /// </summary>
    CameraRain = 5,

    /// <summary>
    /// CAMERA_ACID_RAIN (6): a camera group.
    /// </summary>
    CameraAcidRain = 6,

    /// <summary>
    /// POINT_LIGHTING (7): a point group that hits once.
    /// </summary>
    PointLighting = 7,

    /// <summary>
    /// WANDERING_STORM (8): a wandering group.
    /// </summary>
    WanderingStorm = 8,

    /// <summary>
    /// WANDERING_GAS (9): a wandering group.
    /// </summary>
    WanderingGas = 9,

    /// <summary>
    /// GLOBAL_POINT (10): a global point group, points round the camera area.
    /// </summary>
    GlobalPoint = 10,
}

/// <summary>
/// An EFFECT_FOG_VOLUME record (docs/formats/weather.md "Records").
/// </summary>
public sealed record FogVolumeDef(string StringId, string Name, int Type, float Radius, float Distance, int Colour, float Alpha, bool AdditiveColour,
    Vector3 Offset, Vector3 Offset2, bool GroundMovement);

/// <summary>
/// An EFFECT record: the fields the weather's effect groups use (docs/formats/weather.md "Records", "Effects").
/// </summary>
public sealed record EffectDef
{
    /// <summary>
    /// The record's string id.
    /// </summary>
    public required string StringId { get; init; }
    /// <summary>
    /// The record's name.
    /// </summary>
    public required string Name { get; init; }
    /// <summary>
    /// <c>type</c>: the group the effect spawns as (<see cref="EffectType.None"/> for an unknown value).
    /// </summary>
    public EffectType Type { get; init; }
    /// <summary>
    /// <c>particle system</c>: a ParticleUniverse system name from <c>data/particles/scripts/*.pu</c>.
    /// </summary>
    public string ParticleSystem { get; init; } = "";
    /// <summary>
    /// <c>affect type</c>: what the effect does to characters within <see cref="EffectRadius"/> (<see cref="WeatherAffecting.None"/> for an
    /// unknown value).
    /// </summary>
    public WeatherAffecting AffectType { get; init; }
    /// <summary>
    /// <c>effect radius</c>: the reach of the effect's damage area.
    /// </summary>
    public float EffectRadius { get; init; }
    /// <summary>
    /// <c>effect strength min</c> of the damage area.
    /// </summary>
    public float EffectStrengthMin { get; init; }
    /// <summary>
    /// <c>effect strength max</c> of the damage area.
    /// </summary>
    public float EffectStrengthMax { get; init; }
    /// <summary>
    /// <c>min altitude</c>: the lowest ground the effect is placed on.
    /// </summary>
    public float MinAltitude { get; init; }
    /// <summary>
    /// <c>max altitude</c>: the highest ground the effect is placed on.
    /// </summary>
    public float MaxAltitude { get; init; }
    /// <summary>
    /// <c>min slope</c> of the ground the effect is placed on.
    /// </summary>
    public float MinSlope { get; init; }
    /// <summary>
    /// <c>max slope</c> of the ground the effect is placed on.
    /// </summary>
    public float MaxSlope { get; init; }
    /// <summary>
    /// <c>min time to live</c> (seconds).
    /// </summary>
    public float MinTimeToLive { get; init; }
    /// <summary>
    /// <c>max time to live</c> (seconds).
    /// </summary>
    public float MaxTimeToLive { get; init; }
    /// <summary>
    /// <c>wandering speed</c>: how fast a wandering unit moves.
    /// </summary>
    public float WanderingSpeed { get; init; }
    /// <summary>
    /// <c>wind affected</c>: whether the wind pushes the particles.
    /// </summary>
    public bool WindAffected { get; init; }
    /// <summary>
    /// <c>wind direction emission</c>: whether particles are emitted along the wind.
    /// </summary>
    public bool WindDirectionEmission { get; init; }
    /// <summary>
    /// <c>wind speed mult</c>: the wind's push on the particles.
    /// </summary>
    public float WindSpeedMult { get; init; }
    /// <summary>
    /// <c>min wind span rate</c>: the wind speed for none of the particles.
    /// </summary>
    public float MinWindSpanRate { get; init; }
    /// <summary>
    /// <c>max wind span rate</c>: the wind speed for all of the particles.
    /// </summary>
    public float MaxWindSpanRate { get; init; }
    /// <summary>
    /// <c>ground colour</c>: whether the particle colour is multiplied by the biome's ground colour.
    /// </summary>
    public bool GroundColour { get; init; }
    /// <summary>
    /// <c>maximum view distance</c>.
    /// </summary>
    public float MaximumViewDistance { get; init; }
    /// <summary>
    /// The string ids of the <c>fog volumes</c> (EFFECT_FOG_VOLUME).
    /// </summary>
    public IReadOnlyList<string> FogVolumeIds { get; init; } = [];
    /// <summary>
    /// The string ids of the <c>sound</c> list (AMBIENT_SOUND).
    /// </summary>
    public IReadOnlyList<string> SoundIds { get; init; } = [];

    /// <summary>
    /// The name and the type.
    /// </summary>
    public override string ToString() => $"{Name} ({Type})";
}

/// <summary>
/// One entry of a WEATHER's <c>effects</c> list: the effect with its count (0 = no limit) and respawn times in real seconds (0/0 = all at once).
/// </summary>
public sealed record WeatherEffectEntry(EffectDef Effect, int MaxCount, float RespawnMin, float RespawnMax);

/// <summary>
/// A WEATHER record after the loader's rules (docs/formats/weather.md "Records"): <c>wind update time</c> 0 means 1,000,000 game minutes, and when
/// <c>fog wind min</c> equals <c>fog wind max</c> the fog distance min becomes the larger of the two distances.
/// </summary>
public sealed record WeatherDef
{
    /// <summary>
    /// The wind update time that stands for "never" (the loader's replacement for 0).
    /// </summary>
    public const float NeverMinutes = 1_000_000f;

    /// <summary>
    /// The record's string id.
    /// </summary>
    public required string StringId { get; init; }
    /// <summary>
    /// The record's name.
    /// </summary>
    public required string Name { get; init; }
    /// <summary>
    /// <c>clouds density</c>: the cloud layer's density (docs/formats/clouds.md).
    /// </summary>
    public float CloudsDensity { get; init; }
    /// <summary>
    /// <c>sky color mult</c> (RRGGBB as 0..1): multiplies the two SkyX colours that light the clouds and <c>horizonClouds</c>.
    /// </summary>
    public Vector3 SkyColourMultiplier { get; init; } = Vector3.One;
    /// <summary>
    /// <c>fog enabled</c>: whether the weather has fog.
    /// </summary>
    public bool FogEnabled { get; init; }
    /// <summary>
    /// <c>fog color</c> (RRGGBB as 0..1).
    /// </summary>
    public Vector3 FogColour { get; init; } = Vector3.One;
    /// <summary>
    /// <c>fog distance min</c>, or the larger of the two distances when <see cref="FogWindMin"/> equals <see cref="FogWindMax"/>.
    /// </summary>
    public float FogDistanceMin { get; init; }
    /// <summary>
    /// <c>fog distance max</c>.
    /// </summary>
    public float FogDistanceMax { get; init; }
    /// <summary>
    /// <c>fog wind min</c>: the wind speed of the fog's <see cref="FogDistanceMin"/>.
    /// </summary>
    public float FogWindMin { get; init; }
    /// <summary>
    /// <c>fog wind max</c>: the wind speed of the fog's <see cref="FogDistanceMax"/>.
    /// </summary>
    public float FogWindMax { get; init; }
    /// <summary>
    /// <c>wind speed min</c> (units/s): the weather's wind at strength 0.
    /// </summary>
    public float WindSpeedMin { get; init; }
    /// <summary>
    /// <c>wind speed max</c> (units/s): the weather's wind at strength 1.
    /// </summary>
    public float WindSpeedMax { get; init; }
    /// <summary>
    /// Game minutes between wind updates (<see cref="NeverMinutes"/> when the record says 0).
    /// </summary>
    public float WindUpdateMinutes { get; init; } = 360;
    /// <summary>
    /// Degrees, "0-360": the share of a full turn (and of the way to the weather's speed) one update may make.
    /// </summary>
    public float WindUpdateLimit { get; init; } = 20;
    /// <summary>
    /// <c>wind intensity</c> (0..100): the wind sound level.
    /// </summary>
    public float WindIntensity { get; init; }
    /// <summary>
    /// <c>rain intensity</c> (0..100): the rain sound level and the water's rain ripples.
    /// </summary>
    public float RainIntensity { get; init; }
    /// <summary>
    /// <c>wetness</c> (0..1): the target wetness of surfaces.
    /// </summary>
    public float Wetness { get; init; }
    /// <summary>
    /// <c>dust</c>: dust on objects outside.
    /// </summary>
    public float Dust { get; init; }
    /// <summary>
    /// <c>dust inside</c>: dust on objects inside buildings.
    /// </summary>
    public float DustInside { get; init; }
    /// <summary>
    /// <c>dust slope</c>: the slope dust sticks to.
    /// </summary>
    public float DustSlope { get; init; }
    /// <summary>
    /// <c>heat haze</c> (0..1): the heat-haze post effect's strength.
    /// </summary>
    public float HeatHaze { get; init; }
    /// <summary>
    /// <c>affect type</c>: the weather's direct effect on characters (<see cref="WeatherAffecting.None"/> for an unknown value).
    /// </summary>
    public WeatherAffecting AffectType { get; init; }
    /// <summary>
    /// <c>affect strength</c>; the character update never reads it.
    /// </summary>
    public float AffectStrength { get; init; }
    /// <summary>
    /// The game reads <c>effect strength min</c> for both bounds, so this is the strength every effect group gets.
    /// </summary>
    public float EffectStrength { get; init; } = 1;
    /// <summary>
    /// <c>start time</c> (hours): the start of the time-of-day window.
    /// </summary>
    public float StartTime { get; init; }
    /// <summary>
    /// <c>end time</c> (hours): the end of the time-of-day window.
    /// </summary>
    public float EndTime { get; init; }
    /// <summary>
    /// The <c>effects</c> list, without entries whose EFFECT is unknown.
    /// </summary>
    public IReadOnlyList<WeatherEffectEntry> Effects { get; init; } = [];

    /// <summary>
    /// True when <c>start time</c> and <c>end time</c> differ: the weather only counts at those hours of the day.
    /// </summary>
    public bool HasTimeWindow => StartTime != EndTime;

    /// <summary>
    /// The window test of the weather choice for the controller's whole hour: <c>[start, end]</c> inclusive; for <c>start &gt; end</c> the game's
    /// test accepts <c>hour ≤ start or hour ≥ end</c>, which is always true.
    /// </summary>
    public bool IsInWindow(int hour) =>
        !HasTimeWindow || (StartTime <= EndTime ? hour >= StartTime && hour <= EndTime : hour <= StartTime || hour >= EndTime);

    /// <summary>
    /// The fog distance at a wind speed: <c>lerp(min, max, saturate((wind − fogWindMin) / (fogWindMax − fogWindMin)))</c>, or the minimum when the
    /// two wind values are equal (which the loader has made the larger distance).
    /// </summary>
    public float FogDistanceAt(float windSpeed)
    {
        if (FogWindMin == FogWindMax) return FogDistanceMin;
        float u = Math.Clamp((windSpeed - FogWindMin) / (FogWindMax - FogWindMin), 0, 1);
        return FogDistanceMin + (FogDistanceMax - FogDistanceMin) * u;
    }

    /// <summary>
    /// The wind speed the weather aims for at strength <paramref name="strength"/>: <c>lerp(wind speed min, max, s)</c>.
    /// </summary>
    public float WindSpeedAt(float strength) => WindSpeedMin + (WindSpeedMax - WindSpeedMin) * strength;

    /// <summary>
    /// The sky-related fields in the shape the renderer's <see cref="SkyWeather"/> takes.
    /// </summary>
    public SkyWeather ToSkyWeather() => new(
        Name,
        SkyColourMultiplier,
        FogEnabled,
        FogColour,
        FogDistanceMin,
        FogDistanceMax,
        CloudsDensity,
        HeatHaze
    );

    /// <summary>
    /// A clear weather with the "Default" record's wind, for databases without one.
    /// </summary>
    public static WeatherDef Clear { get; } = new() { StringId = "", Name = "Default", WindSpeedMax = 60 };

    /// <summary>
    /// The name.
    /// </summary>
    public override string ToString() => Name;
}

/// <summary>
/// A SEASON's <c>weathers</c> entry: the weather, its weight, and its minimum and maximum duration in game minutes.
/// </summary>
public sealed record SeasonWeather(WeatherDef Weather, int Weight, int MinMinutes, int MaxMinutes);

/// <summary>
/// A SEASON record with the loader's fallbacks applied (docs/formats/weather.md "SEASON").
/// </summary>
public sealed record SeasonDef
{
    /// <summary>
    /// Duration of the "Default" weather given to a season (or region) that has none; <b>Observed</b>, not read from the game: the most common
    /// base-game range is 1..120, 120..720 is the wind update time's order.
    /// </summary>
    public const int FallbackMinMinutes = 120, FallbackMaxMinutes = 720;

    /// <summary>
    /// The record's string id.
    /// </summary>
    public required string StringId { get; init; }
    /// <summary>
    /// The record's name.
    /// </summary>
    public required string Name { get; init; }
    /// <summary>
    /// The weathers in record order; never empty (a season without any gets "Default"); the "Default" fallback is last when a weather has a time
    /// window.
    /// </summary>
    public required IReadOnlyList<SeasonWeather> Weathers { get; init; }
    /// <summary>
    /// <c>weather strength limit min</c> (0..1): the low end of the strength a weather of the season is rolled at.
    /// </summary>
    public float StrengthLimitMin { get; init; }
    /// <summary>
    /// <c>weather strength limit max</c> (0..1): the high end of the strength a weather of the season is rolled at.
    /// </summary>
    public float StrengthLimitMax { get; init; } = 1;
    /// <summary>
    /// <c>sunlight color</c>: loaded by the game, its use not traced.
    /// </summary>
    public int SunlightColour { get; init; }

    /// <summary>
    /// Whether a weather of the season has a time window.
    /// </summary>
    public bool HasTimeWindows => Weathers.Any(w => w.Weather.HasTimeWindow);

    /// <summary>
    /// A season of the given weathers with the loader's rules: none → "Default" with limits 0..1; any time window → "Default" appended with weight 0.
    /// </summary>
    public static SeasonDef Build(
        string stringId,
        string name,
        IReadOnlyList<SeasonWeather> weathers,
        float limitMin,
        float limitMax,
        WeatherDef defaultWeather,
        int sunlight = 0
    )
    {
        if (weathers.Count == 0)
            return new SeasonDef
            {
                StringId = stringId,
                Name = name,
                Weathers = [new SeasonWeather(defaultWeather, 1, FallbackMinMinutes, FallbackMaxMinutes)],
                StrengthLimitMin = 0,
                StrengthLimitMax = 1,
                SunlightColour = sunlight,
            };
        var list = weathers.ToList();
        if (list.Any(w => w.Weather.HasTimeWindow))
            list.Add(new SeasonWeather(defaultWeather, 0, FallbackMinMinutes, FallbackMaxMinutes));
        return new SeasonDef
        {
            StringId = stringId,
            Name = name,
            Weathers = list,
            StrengthLimitMin = limitMin,
            StrengthLimitMax = limitMax,
            SunlightColour = sunlight
        };
    }

    /// <summary>
    /// The name.
    /// </summary>
    public override string ToString() => Name;
}

/// <summary>
/// A region's season with its order (<c>val0</c>), share of the year (<c>val1</c>) and length in days.
/// </summary>
public sealed record RegionSeason(SeasonDef Season, int Order, float Share, int Days);

/// <summary>
/// A BIOME_GROUP record: a weather region (docs/formats/weather.md "BIOME_GROUP", "Seasons").
/// </summary>
public sealed record RegionDef
{
    /// <summary>
    /// The record's string id.
    /// </summary>
    public required string StringId { get; init; }
    /// <summary>
    /// The record's name.
    /// </summary>
    public required string Name { get; init; }
    /// <summary>
    /// The colour (0xRRGGBB) that paints the region in <c>areasmap.tga</c>.
    /// </summary>
    public int Index { get; init; }
    /// <summary>
    /// In calendar order (by <c>val0</c>, ties in record order); never empty: a region without seasons has one "Default" season of the whole year.
    /// </summary>
    public required IReadOnlyList<RegionSeason> Seasons { get; init; }
    /// <summary>
    /// <c>weather strength multiplier min</c>: the low end of the region's factor on a rolled weather strength.
    /// </summary>
    public float StrengthMultiplierMin { get; init; } = 1;
    /// <summary>
    /// <c>weather strength multiplier max</c>: the high end of the region's factor on a rolled weather strength.
    /// </summary>
    public float StrengthMultiplierMax { get; init; } = 1;

    /// <summary>
    /// True when the record lists no seasons of its own (always clear).
    /// </summary>
    public bool HasNoSeasons { get; init; }

    /// <summary>
    /// The region's biome ambience (BIOME_GROUP <c>ambience</c>, fcs.def default Desert): which <c>SFX_Amb_&lt;name&gt;</c> bank plays here.
    /// </summary>
    public Ambience Ambience { get; init; } = Ambience.Desert;
    /// <summary>
    /// The region's music (BIOME_GROUP `music`, default None).
    /// </summary>
    public BiomeMusic Music { get; init; } = BiomeMusic.None;
    /// <summary>
    /// BIOME_GROUP <c>acidic ground</c> (0..1): the ground burns a bare-footed character's feet (docs/formats/weather.md "Effects on characters").
    /// </summary>
    public float AcidicGround { get; init; }
    /// <summary>
    /// BIOME_GROUP <c>acidic water</c> (0..1): the water burns a character swimming in it.
    /// </summary>
    public float AcidicWater { get; init; }

    /// <summary>
    /// The name.
    /// </summary>
    public override string ToString() => Name;
}

/// <summary>
/// The season calendar of a region: how the year is cut.
/// </summary>
public static class SeasonCalendar
{
    /// <summary>
    /// Lengths in days from the seasons' shares in calendar order: each share (a share below 1 counts as <c>days per year / count</c>) over the sum
    /// of the shares × <paramref name="daysPerYear"/>, rounded; the last takes what is left (at least 1 day).
    /// </summary>
    public static int[] Lengths(IReadOnlyList<float> shares, int daysPerYear)
    {
        int n = shares.Count;
        var lengths = new int[n];
        if (n == 0) return lengths;
        var effective = shares.Select(s => s < 1 ? (double)daysPerYear / n : s).ToArray();
        double sum = effective.Sum();
        int used = 0;
        for (int i = 0; i < n - 1; i++)
        {
            lengths[i] = (int)Math.Round(effective[i] / sum * daysPerYear, MidpointRounding.AwayFromZero);
            used += lengths[i];
        }
        lengths[n - 1] = Math.Max(1, daysPerYear - used);
        return lengths;
    }
}

/// <summary>
/// Every weather record of a game database, read with the loader's rules (docs/formats/weather.md): <see cref="Weathers"/>, <see cref="Seasons"/>,
/// <see cref="Regions"/> (BIOME_GROUP), <see cref="Effects"/> and fog volumes, plus <c>days per year</c>.
/// </summary>
public sealed class WeatherData
{
    #region Model

    /// <summary>
    /// The StringId of the base game's "Default" WEATHER (<c>5460-weather.mod</c>).
    /// </summary>
    public const string DefaultWeatherId = "5460-weather.mod";
    /// <summary>
    /// The StringId of the BIOME_GROUP "NONE" that the areas map's unmatched colours (and positions off the map) belong to
    /// (<c>18003-gamedata.base</c>).
    /// </summary>
    public const string NoneRegionId = "18003-gamedata.base";

    /// <summary>
    /// The regions by their areas-map colour.
    /// </summary>
    readonly Dictionary<int, RegionDef> _regionsByColour = [];

    /// <summary>
    /// The data from records read already, with the "Default" weather and the "NONE" region.
    /// </summary>
    internal WeatherData(IReadOnlyList<WeatherDef> weathers, IReadOnlyList<SeasonDef> seasons, IReadOnlyList<RegionDef> regions,
        IReadOnlyList<EffectDef> effects, IReadOnlyList<FogVolumeDef> fogVolumes, int daysPerYear, WeatherDef defaultWeather, RegionDef none)
    {
        Weathers = weathers;
        Seasons = seasons;
        Regions = regions;
        Effects = effects;
        FogVolumes = fogVolumes;
        DaysPerYear = daysPerYear;
        Default = defaultWeather;
        None = none;
        foreach (var r in regions)
            _regionsByColour[r.Index] = r;
    }

    /// <summary>
    /// The WEATHER records.
    /// </summary>
    public IReadOnlyList<WeatherDef> Weathers { get; }
    /// <summary>
    /// The SEASON records.
    /// </summary>
    public IReadOnlyList<SeasonDef> Seasons { get; }
    /// <summary>
    /// The regions in record order (the scheduler's region index is the position here).
    /// </summary>
    public IReadOnlyList<RegionDef> Regions { get; }
    /// <summary>
    /// The EFFECT records.
    /// </summary>
    public IReadOnlyList<EffectDef> Effects { get; }
    /// <summary>
    /// The EFFECT_FOG_VOLUME records.
    /// </summary>
    public IReadOnlyList<FogVolumeDef> FogVolumes { get; }
    /// <summary>
    /// CONSTANTS <c>days per year</c>.
    /// </summary>
    public int DaysPerYear { get; }
    /// <summary>
    /// The "Default" weather: clear, the fallback of seasons without weathers or with time windows.
    /// </summary>
    public WeatherDef Default { get; }
    /// <summary>
    /// The region of positions the areas map does not give to any BIOME_GROUP.
    /// </summary>
    public RegionDef None { get; }

    /// <summary>
    /// The region painted with <paramref name="colour"/> (0xRRGGBB) in the areas map, or <see cref="None"/>.
    /// </summary>
    public RegionDef RegionOfColour(int colour) => _regionsByColour.GetValueOrDefault(colour, None);

    #endregion

    #region Lookups

    /// <summary>
    /// The weather whose name matches (exactly, else by substring); null name picks "Default".
    /// </summary>
    public WeatherDef? FindWeather(string? name)
    {
        if (name is null) return Default;
        return Weathers.FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? Weathers.FirstOrDefault(w => w.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The region whose name matches (exactly, else by substring).
    /// </summary>
    public RegionDef? FindRegion(string name) =>
        Regions.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? Regions.FirstOrDefault(r => r.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    #endregion

    #region Construction

    /// <summary>
    /// Builds the data from hand-made definitions (tests and tools); <paramref name="regions"/> must include a region named "NONE" or
    /// one is made (<see cref="MakeNone"/>).
    /// </summary>
    public static WeatherData FromDefinitions(
        IReadOnlyList<WeatherDef> weathers,
        IReadOnlyList<SeasonDef> seasons,
        IReadOnlyList<RegionDef> regions,
        int daysPerYear = 100
    )
    {
        var def = weathers.FirstOrDefault(w => w.Name == "Default") ?? WeatherDef.Clear;
        var none = regions.FirstOrDefault(r => r.Name == "NONE") ?? MakeNone(def, daysPerYear);
        var all = regions.Contains(none) ? regions : [.. regions, none];
        return new WeatherData(weathers, seasons, all, [], [], daysPerYear, def, none);
    }

    /// <summary>
    /// The "NONE" region of data that has none: no seasons of its own, one "Default" season of the whole year.
    /// </summary>
    internal static RegionDef MakeNone(WeatherDef def, int daysPerYear) => new()
    {
        StringId = NoneRegionId,
        Name = "NONE",
        HasNoSeasons = true,
        Index = 0,
        Seasons = [new RegionSeason(SeasonDef.Build("", "(no seasons)", [], 0, 1, def), 0, 1, daysPerYear)],
    };

    /// <summary>
    /// Reads the weather records of the merged game database (<see cref="WeatherDataReader.Read"/>).
    /// </summary>
    public static WeatherData Load(GameDatabase db) => WeatherDataReader.Read(db);

    #endregion
}