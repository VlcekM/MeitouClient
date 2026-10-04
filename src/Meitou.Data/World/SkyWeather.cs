using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>
/// The sky-related fields of a WEATHER record (docs/formats/sky.md, Observed from the base game's records): colour
/// multiplier of the sky, fog (colour and a start/end distance) and the cloud density. Rain, wetness, dust and wind
/// fields are not read here.
/// </summary>
public sealed record SkyWeather(string Name, Vector3 SkyColourMultiplier, bool FogEnabled, Vector3 FogColour, float FogMin, float FogMax, float CloudDensity)
{
    /// <summary>The base game's "Default" weather: clear, no fog, no clouds, white multipliers.</summary>
    public static readonly SkyWeather Default = new("Default", Vector3.One, false, Vector3.One, 0, 0, 0);

    /// <summary>Colours are stored as packed 0xRRGGBB integers (Observed: the dust storms' fog is E9CB9E, a sand colour).</summary>
    public static Vector3 Unpack(int rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);

    public static SkyWeather FromRecord(GameRecord r) => new(r.Name,
        Unpack(r.GetInt("sky color mult", 0xFFFFFF)), r.GetBool("fog enabled"), Unpack(r.GetInt("fog color", 0xFFFFFF)),
        r.GetFloat("fog distance min"), r.GetFloat("fog distance max"), r.GetFloat("clouds density"));

    /// <summary>The weather whose name matches (exactly, else by substring); null name picks "Default".</summary>
    public static SkyWeather? Find(GameDatabase db, string? name)
    {
        var all = db.OfType(FcsRecordType.WEATHER).ToList();
        name ??= "Default";
        var r = all.FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(w => w.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        return r is null ? null : FromRecord(r);
    }

    public static IEnumerable<string> Names(GameDatabase db) => db.OfType(FcsRecordType.WEATHER).Select(w => w.Name);

    /// <summary>CONSTANTS <c>night darkness</c> (0.35 in the merged data): the night's floor of the exposure band, as a fraction of <c>exposure min</c> (docs/formats/lighting.md).</summary>
    public static float NightDarkness(GameDatabase db) =>
        (db.OfType(FcsRecordType.CONSTANTS).FirstOrDefault(r => r.Name == "GLOBAL CONSTANTS") ?? db.OfType(FcsRecordType.CONSTANTS).FirstOrDefault())?.GetFloat("night darkness", 0.5f) ?? 0.5f;
}
