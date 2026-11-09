using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Data.World;

/// <summary>
/// Reads the weather records of a game database into a <see cref="WeatherData"/> with the loader's rules (docs/formats/weather.md): EFFECT_FOG_VOLUME,
/// EFFECT, WEATHER, SEASON and BIOME_GROUP records, plus <c>days per year</c>.
/// </summary>
public static class WeatherDataReader
{
    /// <summary>
    /// Reads the weather records of the merged game database (<c>days per year</c> from GLOBAL CONSTANTS).
    /// </summary>
    public static WeatherData Read(GameDatabase db)
    {
        int daysPerYear = Math.Max(1, GameConstants.FromDatabase(db).DaysPerYear);

        var effects = new Dictionary<string, EffectDef>(StringComparer.Ordinal);
        var fogVolumes = new Dictionary<string, FogVolumeDef>(StringComparer.Ordinal);
        ReadEffects(db, effects, fogVolumes);
        var weathers = ReadWeathers(db, effects);
        var defaultWeather = weathers.GetValueOrDefault(WeatherData.DefaultWeatherId) ?? weathers.Values.FirstOrDefault(w => w.Name == "Default")
                ?? WeatherDef.Clear;
        var seasons = ReadSeasons(db, weathers, defaultWeather);
        var regions = ReadRegions(db, seasons, defaultWeather, daysPerYear);

        var none = NoneRegionOf(regions, defaultWeather, daysPerYear);
        if (!regions.Contains(none)) regions.Add(none);
        return new WeatherData(
            [.. weathers.Values],
            [.. seasons.Values],
            regions,
            [.. effects.Values],
            [.. fogVolumes.Values],
            daysPerYear,
            defaultWeather,
            none
        );
    }

    /// <summary>
    /// The EFFECT_FOG_VOLUME and EFFECT records, by string id.
    /// </summary>
    static void ReadEffects(GameDatabase db, Dictionary<string, EffectDef> effects, Dictionary<string, FogVolumeDef> fogVolumes)
    {
        foreach (var f in db.OfType(FcsRecordType.EffectFogVolume))
            fogVolumes[f.StringId] = ReadFogVolume(f);
        foreach (var e in db.OfType(FcsRecordType.Effect))
            effects[e.StringId] = ReadEffect(e);
    }

    /// <summary>
    /// The WEATHER records by string id, their effect lists resolved against <paramref name="effects"/>.
    /// </summary>
    static Dictionary<string, WeatherDef> ReadWeathers(GameDatabase db, Dictionary<string, EffectDef> effects)
    {
        var weathers = new Dictionary<string, WeatherDef>(StringComparer.Ordinal);
        foreach (var w in db.OfType(FcsRecordType.Weather))
            weathers[w.StringId] = ReadWeather(w, effects);
        return weathers;
    }

    /// <summary>
    /// The SEASON records by string id, with the loader's fallbacks (<see cref="SeasonDef.Build"/>).
    /// </summary>
    static Dictionary<string, SeasonDef> ReadSeasons(GameDatabase db, Dictionary<string, WeatherDef> weathers, WeatherDef defaultWeather)
    {
        var seasons = new Dictionary<string, SeasonDef>(StringComparer.Ordinal);
        foreach (var s in db.OfType(FcsRecordType.Season))
        {
            var entries = SeasonEntries(s, weathers);
            float limitMin = s.GetFloat("weather strength limit min");
            float limitMax = s.GetFloat("weather strength limit max", 1);
            seasons[s.StringId] = SeasonDef.Build(s.StringId, s.Name, entries, limitMin, limitMax, defaultWeather, s.GetInt("sunlight color"));
        }
        return seasons;
    }

    /// <summary>
    /// A SEASON's <c>weathers</c> entries that name a known weather: weight <c>val0</c>, duration <c>val1</c>..max(<c>val1</c>, <c>val2</c>).
    /// </summary>
    static List<SeasonWeather> SeasonEntries(GameRecord s, Dictionary<string, WeatherDef> weathers)
    {
        return s.GetReferences("weathers")
            .Where(r => weathers.ContainsKey(r.TargetStringId))
            .Select(r => new SeasonWeather(weathers[r.TargetStringId], r.Values.Value0, r.Values.Value1, Math.Max(r.Values.Value1, r.Values.Value2)))
            .ToList();
    }

    /// <summary>
    /// The BIOME_GROUP records in record order.
    /// </summary>
    static List<RegionDef> ReadRegions(GameDatabase db, Dictionary<string, SeasonDef> seasons, WeatherDef defaultWeather, int daysPerYear)
    {
        var regions = new List<RegionDef>();
        foreach (var g in db.OfType(FcsRecordType.BiomeGroup))
            regions.Add(ReadRegion(g, seasons, defaultWeather, daysPerYear));
        return regions;
    }

    /// <summary>
    /// One BIOME_GROUP: its known seasons in calendar order with their lengths, or one "Default" season of the whole year.
    /// </summary>
    static RegionDef ReadRegion(GameRecord g, Dictionary<string, SeasonDef> seasons, WeatherDef defaultWeather, int daysPerYear)
    {
        var listed = g.GetReferences("seasons").Where(r => seasons.ContainsKey(r.TargetStringId))
            .Select(r => (Season: seasons[r.TargetStringId], Order: r.Values.Value0, Share: (float)r.Values.Value1))
            .OrderBy(x => x.Order).ToList();   // OrderBy is stable: ties keep record order (Unknown in the game)
        IReadOnlyList<RegionSeason> list;
        if (listed.Count == 0)
            list = [new RegionSeason(SeasonDef.Build("", "(no seasons)", [], 0, 1, defaultWeather), 0, 1, daysPerYear)];
        else
        {
            var lengths = SeasonCalendar.Lengths(listed.Select(x => x.Share).ToList(), daysPerYear);
            list = listed.Select((x, i) => new RegionSeason(x.Season, x.Order, x.Share, lengths[i])).ToList();
        }
        return new RegionDef
        {
            StringId = g.StringId,
            Name = g.Name,
            Index = g.GetInt("index"),
            Seasons = list,
            HasNoSeasons = listed.Count == 0,
            StrengthMultiplierMin = g.GetFloat("weather strength multiplier min", 1),
            StrengthMultiplierMax = g.GetFloat("weather strength multiplier max", 1),
            Ambience = Enum.IsDefined((Ambience)g.GetInt("ambience", (int)Ambience.Desert))
                ? (Ambience)g.GetInt("ambience", (int)Ambience.Desert)
                : Ambience.Desert,
            Music = Enum.IsDefined((BiomeMusic)g.GetInt("music", (int)BiomeMusic.None))
                ? (BiomeMusic)g.GetInt("music", (int)BiomeMusic.None)
                : BiomeMusic.None,
            AcidicGround = g.GetFloat("acidic ground"),
            AcidicWater = g.GetFloat("acidic water"),
        };
    }

    /// <summary>
    /// The "NONE" region: by its id, else a region named "NONE" painted 0, else a made one.
    /// </summary>
    static RegionDef NoneRegionOf(List<RegionDef> regions, WeatherDef defaultWeather, int daysPerYear)
    {
        return regions.FirstOrDefault(r => r.StringId == WeatherData.NoneRegionId)
            ?? regions.FirstOrDefault(r => r.Name == "NONE" && r.Index == 0)
            ?? WeatherData.MakeNone(defaultWeather, daysPerYear);
    }

    /// <summary>
    /// One EFFECT_FOG_VOLUME record.
    /// </summary>
    static FogVolumeDef ReadFogVolume(GameRecord f) => new(f.StringId, f.Name, f.GetInt("type"), f.GetFloat("radius"), f.GetFloat("distance"),
        f.GetInt("colour"), f.GetFloat("alpha"),
        f.GetBool("additive colour"), new Vector3(f.GetFloat("position x"), f.GetFloat("position y"), f.GetFloat("position z")),
        new Vector3(f.GetFloat("position 2 x"), f.GetFloat("position 2 y"), f.GetFloat("position 2 z")), f.GetBool("ground movement"));

    /// <summary>
    /// One EFFECT record.
    /// </summary>
    static EffectDef ReadEffect(GameRecord e) => new()
    {
        StringId = e.StringId,
        Name = e.Name,
        Type = Enum.IsDefined((EffectType)e.GetInt("type")) ? (EffectType)e.GetInt("type") : EffectType.None,
        ParticleSystem = e.GetString("particle system"),
        AffectType = Enum.IsDefined((WeatherAffecting)e.GetInt("affect type")) ? (WeatherAffecting)e.GetInt("affect type") : WeatherAffecting.None,
        EffectRadius = e.GetFloat("effect radius"),
        EffectStrengthMin = e.GetFloat("effect strength min"),
        EffectStrengthMax = e.GetFloat("effect strength max"),
        MinAltitude = e.GetFloat("min altitude"),
        MaxAltitude = e.GetFloat("max altitude"),
        MinSlope = e.GetFloat("min slope"),
        MaxSlope = e.GetFloat("max slope"),
        MinTimeToLive = e.GetFloat("min time to live"),
        MaxTimeToLive = e.GetFloat("max time to live"),
        WanderingSpeed = e.GetFloat("wandering speed"),
        WindAffected = e.GetBool("wind affected"),
        WindDirectionEmission = e.GetBool("wind direction emission"),
        WindSpeedMult = e.GetFloat("wind speed mult"),
        MinWindSpanRate = e.GetFloat("min wind span rate"),
        MaxWindSpanRate = e.GetFloat("max wind span rate"),
        GroundColour = e.GetBool("ground colour"),
        MaximumViewDistance = e.GetFloat("maximum view distance"),
        FogVolumeIds = e.GetReferences("fog volumes").Select(r => r.TargetStringId).ToList(),
        SoundIds = e.GetReferences("sound").Select(r => r.TargetStringId).ToList(),
    };

    /// <summary>
    /// One WEATHER record with the loader's rules (the fog distance and wind update time fallbacks).
    /// </summary>
    static WeatherDef ReadWeather(GameRecord w, IReadOnlyDictionary<string, EffectDef> effects)
    {
        float fogMin = w.GetFloat("fog distance min"), fogMax = w.GetFloat("fog distance max");
        float fogWindMin = w.GetFloat("fog wind min"), fogWindMax = w.GetFloat("fog wind max");
        if (fogWindMin == fogWindMax) fogMin = Math.Max(fogMin, fogMax);   // the loader's rule
        int updateTime = w.GetInt("wind update time");
        return new WeatherDef
        {
            StringId = w.StringId,
            Name = w.Name,
            CloudsDensity = w.GetFloat("clouds density"),
            SkyColourMultiplier = SkyWeather.Unpack(w.GetInt("sky color mult", 0)),
            FogEnabled = w.GetBool("fog enabled"),
            FogColour = SkyWeather.Unpack(w.GetInt("fog color", 0)),
            FogDistanceMin = fogMin,
            FogDistanceMax = fogMax,
            FogWindMin = fogWindMin,
            FogWindMax = fogWindMax,
            WindSpeedMin = w.GetFloat("wind speed min"),
            WindSpeedMax = w.GetFloat("wind speed max"),
            WindUpdateMinutes = updateTime == 0 ? WeatherDef.NeverMinutes : updateTime,
            WindUpdateLimit = w.GetInt("wind update limit"),
            WindIntensity = w.GetFloat("wind intensity"),
            RainIntensity = w.GetFloat("rain intensity"),
            Wetness = w.GetFloat("wetness"),
            Dust = w.GetFloat("dust"),
            DustInside = w.GetFloat("dust inside"),
            DustSlope = w.GetFloat("dust slope"),
            HeatHaze = w.GetFloat("heat haze"),
            AffectType = Enum.IsDefined((WeatherAffecting)w.GetInt("affect type")) ? (WeatherAffecting)w.GetInt("affect type")
                    : WeatherAffecting.None,
            AffectStrength = w.GetFloat("affect strength"),
            EffectStrength = w.GetFloat("effect strength min", 1),
            StartTime = w.GetFloat("start time"),
            EndTime = w.GetFloat("end time"),
            Effects = w.GetReferences("effects").Where(r => effects.ContainsKey(r.TargetStringId))
                .Select(r => new WeatherEffectEntry(effects[r.TargetStringId], r.Values.Value0, r.Values.Value1, r.Values.Value2)).ToList(),
        };
    }
}
