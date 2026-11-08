using Meitou.Data.Particles;
using Meitou.Data.World;
using Meitou.Game.Rendering.Sky;
using Meitou.Simulation;

namespace Meitou.Game.Host.WorldSync;

/// <summary>
/// The weather the simulation reads (<see cref="World.Weather"/>, docs/formats/weather.md "Effects on characters"): the renderer's scheduler gives each place its region's
/// weather (<see cref="WeatherWorld.RegionAt"/>, the schedule only: a weather forced at the camera stays a view setting), and the live units of its particle groups
/// whose EFFECT has an <c>affect type</c> are the local effects. Without a weather (the viewer's <c>--no-weather</c>) nothing acts.
/// </summary>
sealed class WeatherFeed(GameHost host)
{
    internal sealed class HostWeather(Func<WorldWeather?> weather) : IWeatherSource
    {
        public readonly List<WeatherAffectZone> ZoneList = [];
        public IReadOnlyList<WeatherAffectZone> Zones => ZoneList;
        /// <summary>A weather every point gets instead of the schedule's (the <c>--scene acid</c> harness).</summary>
        public WeatherHere? Forced;

        public WeatherHere? At(float x, float z)
        {
            if (Forced is { } forced) return forced;
            if (weather()?.World is not { } world) return null;
            var region = world.RegionAt(x, z);
            return new WeatherHere(region.Weather.AffectType, region.Strength, region.Def.AcidicGround, region.Def.AcidicWater, region.WindSpeed, region.Weather.Wetness);
        }
    }

    internal HostWeather? hostWeather;

    /// <summary>
    /// Once a frame, before the simulation's ticks: hands the weather to the world and collects the affecting units of the particle groups (units whose life is over do not act;
    /// an effect without an <c>effect radius</c> is left out, an engine choice: the original then takes 0.375 x the width of its particle bounds, which Meitou does not keep).
    /// </summary>
    internal void UpdateWeatherSource()
    {
        hostWeather ??= new HostWeather(() => host.gpu.Weather);
        host.session.World.Weather = hostWeather;
        var zones = hostWeather.ZoneList;
        zones.Clear();
        if (host.gpu.Particles is not { } particles) return;
        Collect(particles.Groups);
        Collect(particles.PlacerGroups);

        void Collect(IReadOnlyList<EffectGroup> groups)
        {
            foreach (var g in groups)
            {
                var e = g.Effect;
                if (e.AffectType == WeatherAffecting.None || !(e.EffectRadius > 0)) continue;
                foreach (var u in g.Units)
                    if (!u.Stopped) zones.Add(new WeatherAffectZone(u.Position, e.EffectRadius, e.AffectType, e.EffectStrength));
            }
        }
    }
}
