using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.Particles;

/// <summary>The EFFECT record's <c>type</c> (<c>EffectType</c> of <c>fcs_enums.def</c>): how the weather spawns and places the effect.</summary>
public enum EffectType { None, Camera, Point, Wandering, Global, CameraRain, CameraAcidRain, PointLighting, WanderingStorm, WanderingGas, GlobalPoint }

/// <summary>
/// An EFFECT_FOG_VOLUME record (docs/formats/weather.md "Fog volumes"): a sphere (all 8 base records are) or cylinder of coloured fog attached to
/// an effect, offset from the effect's node; <see cref="Distance"/> is the "density distance" and <see cref="Alpha"/> the volume's opacity.
/// </summary>
public sealed record EffectFogVolume(string Name, bool Cylinder, float Radius, float Distance, Vector3 Colour, float Alpha, bool Additive, Vector3 Offset, Vector3 Offset2, bool GroundMovement)
{
    public static EffectFogVolume From(GameRecord r) => new(r.Name, r.GetInt("type") == 1, r.GetFloat("radius", 1), r.GetFloat("distance", 1), EffectRecord.UnpackRgb(r.GetInt("colour", 0xFFFFFF)),
        r.GetFloat("alpha", 1), r.GetBool("additive colour"), new Vector3(r.GetFloat("position x"), r.GetFloat("position y"), r.GetFloat("position z")),
        new Vector3(r.GetFloat("position 2 x"), r.GetFloat("position 2 y"), r.GetFloat("position 2 z")), r.GetBool("ground movement"));
}

/// <summary>
/// An EFFECT record (docs/formats/weather.md "Effects", fields from <c>fcs.def</c>): the particle system and the weather fields the groups use.
/// The altitude / slope window (<see cref="MinAltitude"/> ...), the fade times, the fog volumes and the lights are init-only extras with the
/// <c>fcs.def</c> defaults.
/// </summary>
public sealed record EffectRecord(string Name, EffectType Type, string ParticleSystem, bool WindAffected, bool WindDirectionEmission, float WindSpeedMultiplier,
    float MinWindSpan, float MaxWindSpan, Vector3 ColourMultiplier, float SkyColourMultiplier, bool GroundColour, float MaximumViewDistance,
    float ParticleFadeOutDelay, float MinTimeToLive, float MaxTimeToLive, float WanderingSpeed)
{
    /// <summary>The ground height window and the terrain normal's y window an effect may be placed in (<c>min/max altitude</c>, <c>min/max slope</c>; Verified (decompiled), FUN_1408fc3c0).</summary>
    public float MinAltitude { get; init; } = 200;
    public float MaxAltitude { get; init; } = 100000;
    public float MinSlope { get; init; }
    public float MaxSlope { get; init; } = 1;
    public float FogFadeIn { get; init; }
    public float FogFadeOut { get; init; }
    public IReadOnlyList<EffectFogVolume> FogVolumes { get; init; } = [];
    /// <summary>The LIGHT records attached (names): none of the base game's lightning has one.</summary>
    public IReadOnlyList<string> Lights { get; init; } = [];

    public static EffectRecord From(GameRecord r, GameDatabase? db = null) => new(r.Name, (EffectType)Math.Clamp(r.GetInt("type"), 0, (int)EffectType.GlobalPoint), r.GetString("particle system"),
        r.GetBool("wind affected"), r.GetBool("wind direction emission"), r.GetFloat("wind speed mult", 1), r.GetFloat("min wind span rate"), r.GetFloat("max wind span rate"),
        UnpackRgb(r.GetInt("colour multiplier", 0xFFFFFF)), r.GetFloat("sky colour multiplier", 1), r.GetBool("ground colour"), r.GetFloat("maximum view distance"),
        r.GetFloat("particle fade out delay"), r.GetFloat("min time to live"), r.GetFloat("max time to live", 30), r.GetFloat("wandering speed", 10))
    {
        MinAltitude = r.GetFloat("min altitude", 200), MaxAltitude = r.GetFloat("max altitude", 100000),
        MinSlope = r.GetFloat("min slope"), MaxSlope = r.GetFloat("max slope", 1),
        FogFadeIn = r.GetFloat("fog fade in duration"), FogFadeOut = r.GetFloat("fog fade out duration"),
        FogVolumes = db is null ? [] : [.. r.GetReferences("fog volumes").Select(f => db.Find(f.TargetStringId)).Where(f => f is { Type: FcsRecordType.EFFECT_FOG_VOLUME }).Select(f => EffectFogVolume.From(f!))],
        Lights = db is null ? [] : [.. r.GetReferences("lights").Select(l => db.Find(l.TargetStringId)?.Name ?? "?")],
    };

    internal static Vector3 UnpackRgb(int rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);

    /// <summary>Whether the group is a camera group (CAMERA, CAMERA_RAIN, CAMERA_ACID_RAIN).</summary>
    public bool IsCameraGroup => Type is EffectType.Camera or EffectType.CameraRain or EffectType.CameraAcidRain;
    /// <summary>Whether the effect has no life: <c>max time to live</c> 0 or less (Verified (decompiled), FUN_1401034f0: the handler never ends).</summary>
    public bool Immortal => MaxTimeToLive <= 0;
}

/// <summary>One entry of a WEATHER's <c>effects</c> list: the effect, how many may be active at once (0 = no limit) and the respawn times in real seconds (0 / 0: all at once).</summary>
public sealed record WeatherEffectEntry(EffectRecord Effect, int Count, int RespawnMin, int RespawnMax);

/// <summary>
/// What the effect groups need of the weather, per frame (docs/formats/weather.md "Effects"): the camera region's active effects, the
/// weather's strength and the wind. The scheduler fills it from its state; <see cref="WeatherEffectAdapter"/> makes one from a forced WEATHER record.
/// </summary>
public sealed class WeatherEffectInput
{
    public IReadOnlyList<WeatherEffectEntry> Effects { get; init; } = [];
    /// <summary>The weather's strength <c>s</c> (0..1).</summary>
    public float Strength { get; init; } = 1;
    /// <summary>The wind as a velocity in x and z, units per second.</summary>
    public Vector2 Wind { get; init; }
    /// <summary>Bumped by whoever fills the input when <see cref="Effects"/> changed (a new weather), so the groups are rebuilt.</summary>
    public int Version { get; init; }

    public static readonly WeatherEffectInput None = new();
}

public static class WeatherEffectAdapter
{
    /// <summary>The WEATHER record named <paramref name="name"/> (exact, else a substring, as <see cref="World.SkyWeather.Find"/>), or null.</summary>
    public static GameRecord? FindWeather(GameDatabase db, string? name)
    {
        var all = db.OfType(FcsRecordType.WEATHER).ToList();
        name ??= "Default";
        return all.FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(w => w.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The effect list of a WEATHER record, at strength 1 with the wind at its fastest and blowing along +x (Observed: a forced weather has no scheduler to roll them).</summary>
    public static WeatherEffectInput FromWeather(GameDatabase db, GameRecord weather)
    {
        var entries = new List<WeatherEffectEntry>();
        foreach (var reference in weather.GetReferences("effects"))
            if (db.Find(reference.TargetStringId) is { Type: FcsRecordType.EFFECT } effect)
                entries.Add(new WeatherEffectEntry(EffectRecord.From(effect, db), reference.Values.Value0, reference.Values.Value1, reference.Values.Value2));
        return new WeatherEffectInput { Effects = entries, Strength = 1, Wind = new Vector2(weather.GetFloat("wind speed max"), 0) };
    }

    public static WeatherEffectInput FromName(GameDatabase db, string? name) =>
        FindWeather(db, name) is { } weather ? FromWeather(db, weather) : WeatherEffectInput.None;
}
