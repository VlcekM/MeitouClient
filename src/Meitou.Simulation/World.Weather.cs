using Meitou.Data.World;
using Meitou.Simulation.Weather;

namespace Meitou.Simulation;

/// <summary>The weather of the BIOME_GROUP region at a place: its current weather's <c>affect type</c>, the region's strength s, the region's acidic ground and water, its wind speed (units/s) and its current weather's <c>wetness</c> (what rains on a farm).</summary>
public readonly record struct WeatherHere(WeatherAffecting Affect, float Strength, float AcidicGround, float AcidicWater, float WindSpeed = 0, float Wetness = 0);

/// <summary>
/// The weather the simulation reads (docs/formats/weather.md "Effects on characters"): the host's scheduler, which runs on the game clock and the world seed, and the
/// live local effects of its particle groups. An input from outside like the commands: not hashed, read only in serial phases.
/// </summary>
public interface IWeatherSource
{
    /// <summary>The region at (<paramref name="x"/>, <paramref name="z"/>); null where there is no weather.</summary>
    WeatherHere? At(float x, float z);
    /// <summary>The live effect units that act on characters (burning, gas, dust).</summary>
    IReadOnlyList<WeatherAffectZone> Zones { get; }
}

public sealed partial class World
{
    /// <summary>The weather (<see cref="WeatherSystem"/>); null (the default, every test): no weather acts on anyone.</summary>
    public IWeatherSource? Weather { get; set; }
}
