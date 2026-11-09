using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Data.World;

/// <summary>
/// <c>WeatherAffecting</c> (fcs_enums.def): the direct effect of a weather or effect on characters.
/// </summary>
public enum WeatherAffecting { None = 0, DustStorm = 1, Acid = 2, Burning = 3, Gas = 4, Rain = 5 }

/// <summary>
/// A live local effect that acts on characters (docs/formats/weather.md "Effects on characters"): an effect unit whose EFFECT has an <c>affect
/// type</c>, at <paramref name="Position"/>, reaching characters within <paramref name="Radius"/> (<c>effect radius</c>) with
/// <paramref name="Strength"/>.
/// </summary>
public readonly record struct WeatherAffectZone(Vector3 Position, float Radius, WeatherAffecting Affect, float Strength);

/// <summary>
/// The biome ambience of a region (fcs_enums.def `Ambience`; each names an `SFX_Amb_<name>.bnk`).
/// </summary>
public enum Ambience { Ashlands, Boneyard, Canyons, Coast, Desert, Forest, Jungle, Plains, Swamplands, Tarsands }

/// <summary>
/// The music of a region (fcs_enums.def `BiomeMusic`; the spellings are the enum's, the banks are Dunes, Savannah, Swamplands, Tarsands).
/// </summary>
public enum BiomeMusic { Dunes, Savanah, Swamp, Tarsands, None }

/// <summary>
/// <c>EffectType</c> (fcs_enums.def, docs/formats/weather.md "Spawning").
/// </summary>
public enum EffectType
{
    None = 0, Camera = 1, Point = 2, Wandering = 3, Global = 4, CameraRain = 5, CameraAcidRain = 6, PointLighting = 7,
    WanderingStorm = 8, WanderingGas = 9, GlobalPoint = 10,
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
    public required string StringId { get; init; }
    public required string Name { get; init; }
    public EffectType Type { get; init; }
    public string ParticleSystem { get; init; } = "";
    public WeatherAffecting AffectType { get; init; }
    public float EffectRadius { get; init; }
    public float EffectStrengthMin { get; init; }
    public float EffectStrengthMax { get; init; }
    public float MinAltitude { get; init; }
    public float MaxAltitude { get; init; }
    public float MinSlope { get; init; }
    public float MaxSlope { get; init; }
    public float MinTimeToLive { get; init; }
    public float MaxTimeToLive { get; init; }
    public float WanderingSpeed { get; init; }
    public bool WindAffected { get; init; }
    public bool WindDirectionEmission { get; init; }
    public float WindSpeedMult { get; init; }
    public float MinWindSpanRate { get; init; }
    public float MaxWindSpanRate { get; init; }
    public bool GroundColour { get; init; }
    public float MaximumViewDistance { get; init; }
    public IReadOnlyList<string> FogVolumeIds { get; init; } = [];
    public IReadOnlyList<string> SoundIds { get; init; } = [];

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

    public required string StringId { get; init; }
    public required string Name { get; init; }
    public float CloudsDensity { get; init; }
    public Vector3 SkyColourMultiplier { get; init; } = Vector3.One;
    public bool FogEnabled { get; init; }
    public Vector3 FogColour { get; init; } = Vector3.One;
    public float FogDistanceMin { get; init; }
    public float FogDistanceMax { get; init; }
    public float FogWindMin { get; init; }
    public float FogWindMax { get; init; }
    public float WindSpeedMin { get; init; }
    public float WindSpeedMax { get; init; }
    /// <summary>
    /// Game minutes between wind updates (<see cref="NeverMinutes"/> when the record says 0).
    /// </summary>
    public float WindUpdateMinutes { get; init; } = 360;
    /// <summary>
    /// Degrees, "0-360": the share of a full turn (and of the way to the weather's speed) one update may make.
    /// </summary>
    public float WindUpdateLimit { get; init; } = 20;
    public float WindIntensity { get; init; }
    public float RainIntensity { get; init; }
    public float Wetness { get; init; }
    public float Dust { get; init; }
    public float DustInside { get; init; }
    public float DustSlope { get; init; }
    public float HeatHaze { get; init; }
    public WeatherAffecting AffectType { get; init; }
    public float AffectStrength { get; init; }
    /// <summary>
    /// The game reads <c>effect strength min</c> for both bounds, so this is the strength every effect group gets.
    /// </summary>
    public float EffectStrength { get; init; } = 1;
    public float StartTime { get; init; }
    public float EndTime { get; init; }
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

    public required string StringId { get; init; }
    public required string Name { get; init; }
    /// <summary>
    /// The weathers in record order; never empty (a season without any gets "Default"); the "Default" fallback is last when a weather has a time
    /// window.
    /// </summary>
    public required IReadOnlyList<SeasonWeather> Weathers { get; init; }
    public float StrengthLimitMin { get; init; }
    public float StrengthLimitMax { get; init; } = 1;
    public int SunlightColour { get; init; }

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
    public required string StringId { get; init; }
    public required string Name { get; init; }
    /// <summary>
    /// The colour (0xRRGGBB) that paints the region in <c>areasmap.tga</c>.
    /// </summary>
    public int Index { get; init; }
    /// <summary>
    /// In calendar order (by <c>val0</c>, ties in record order); never empty: a region without seasons has one "Default" season of the whole year.
    /// </summary>
    public required IReadOnlyList<RegionSeason> Seasons { get; init; }
    public float StrengthMultiplierMin { get; init; } = 1;
    public float StrengthMultiplierMax { get; init; } = 1;

    /// <summary>
    /// True when the record lists no seasons of its own (always clear).
    /// </summary>
    public bool HasNoSeasons { get; init; }

    /// <summary>
    /// The region's biome ambience (BIOME_GROUP `ambience`, fcs.def default Desert): which `SFX_Amb_<name>` bank plays here.
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

    readonly Dictionary<int, RegionDef> _regionsByColour = [];

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

    public IReadOnlyList<WeatherDef> Weathers { get; }
    public IReadOnlyList<SeasonDef> Seasons { get; }
    /// <summary>
    /// The regions in record order (the scheduler's region index is the position here).
    /// </summary>
    public IReadOnlyList<RegionDef> Regions { get; }
    public IReadOnlyList<EffectDef> Effects { get; }
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
    /// <paramref name="none"/> is made.
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