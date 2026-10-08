using System.Numerics;

namespace Meitou.Data.World;

/// <summary>
/// What the weather looks like at the camera this frame (docs/formats/weather.md "What the camera sees"), produced by <see cref="WeatherWorld.Update"/>: the
/// camera region's weather and strength, the wind, the sky colour multiplier and cloud density after their 30 s transition, the cloud drift, the fog
/// blended over up to four regions, rain, wetness, dust, heat haze and the active effect list. Renderers read it through their own setters.
/// </summary>
public sealed record WeatherState
{
    /// <summary>The camera region's current WEATHER record.</summary>
    public required WeatherDef Weather { get; init; }
    /// <summary>The name of the region the camera is in (empty when the weather is forced).</summary>
    public string RegionName { get; init; } = "";
    /// <summary>The weather strength s (0..1).</summary>
    public float Strength { get; init; } = 1;

    /// <summary>The wind in the ground plane: unit direction (x, z) and speed in units per second.</summary>
    public Vector2 WindDirection { get; init; } = Vector2.UnitX;
    public float WindSpeed { get; init; }

    /// <summary>The sky controller's colour multiplier after the 30 s transition (<c>sky color mult</c>).</summary>
    public Vector3 SkyColourMultiplier { get; init; } = Vector3.One;
    /// <summary>The cloud density c after the transition (<c>clouds density</c>; the sky controller clamps it to 0..1).</summary>
    public float CloudDensity { get; init; }
    /// <summary>The cloud drift velocity: the wind direction × speed (units per second).</summary>
    public Vector2 CloudDrift { get; init; }

    /// <summary>The blended weather fog's weight (0..1: 0 = off, between while regions or weathers disagree or fade).</summary>
    public float FogEnabled { get; init; }
    /// <summary>The fog colour and distance, averaged over the regions that have fog on (the weights of the regions that do not are left out; <b>Observed</b>).</summary>
    public Vector3 FogColour { get; init; } = Vector3.One;
    public float FogDistance { get; init; }

    /// <summary>Rain level 0..100: <c>rain intensity</c> × strength.</summary>
    public float Rain { get; init; }
    /// <summary>Surface wetness 0..1 (the shared <c>wetness</c>).</summary>
    public float Wetness { get; init; }
    /// <summary>The shared <c>dustAmount</c>: x = current dust, y = x × <c>dust inside</c>, z = the slope value.</summary>
    public Vector3 DustAmount { get; init; }
    /// <summary>The shared <c>heatHaze</c> (0..1).</summary>
    public float HeatHaze { get; init; }

    /// <summary>The camera region's effect groups: the weather's <c>effects</c> list (EFFECT, count, respawn).</summary>
    public IReadOnlyList<WeatherEffectEntry> Effects => Weather.Effects;
    /// <summary>The strength every effect group gets (<see cref="WeatherDef.EffectStrength"/>).</summary>
    public float EffectStrength => Weather.EffectStrength;

    /// <summary>A clear state: the "Default" weather, no wind, no fog.</summary>
    public static WeatherState Clear { get; } = new() { Weather = WeatherDef.Clear };
}
