using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Textures;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The weather's frame-wide surface values (docs/formats/weather.md "Rain and wetness", "Dust"): the game's shared parameters <c>wetness</c>,
/// <c>dustAmount</c>, <c>rainAmount</c> and <c>gameTime</c>, published to every program as the frame globals <c>uWeatherWet</c> and
/// <c>uWeatherDust</c> (<see cref="AtmosphereShaders.Functions"/> declares them and <c>makeWet</c> / <c>dustCover</c>), with the dust noise texture.
/// The terrain publishes the ground colour map the dust takes its colour from (<c>uWeatherGround</c>). At 0 everywhere the shaders change nothing.
/// The viewer sets the values from <c>--weather</c> or the test options; the scheduler's <c>WeatherState</c> replaces that later.
/// </summary>
internal sealed class WeatherSurfaces : IDisposable
{
    /// <summary>The game's file for the dust noise (the <c>dust</c> unit the material builder adds, docs/formats/runtime-materials.md).</summary>
    public const string NoiseFile = "Turbulent.dds";

    SampledImage? noise;

    /// <summary>Shared <c>wetness</c> 0..1: how wet the ground and objects are.</summary>
    public float Wetness;
    /// <summary>Shared <c>dustAmount</c>: x the current dust (above 1 covers more), y the dust inside buildings, z the slope value.</summary>
    public Vector3 Dust;
    /// <summary>The weather's rain, 0..100 × strength: <c>rainAmount</c> is <c>saturate(rain / 50)</c>.</summary>
    public float Rain;
    /// <summary>The water shader's <c>gameTime</c> (game hours); the frame sets it.</summary>
    public float GameTime;

    /// <summary>The shader's <c>rainAmount</c>.</summary>
    public static float RainAmount(float rain) => Math.Clamp(rain / 50f, 0f, 1f);

    public WeatherSurfaces(GpuContext gpu, AssetLocator assets)
    {
        if (assets.Find(NoiseFile) is { } path)
            noise = SampledImage.Rgba8(gpu, TextureLoader.LoadFile(path, allMips: false).Levels[0], repeat: true, mipmaps: true, "weather dust noise");
        var g = gpu.Globals;
        g.PublishUniform("uWeatherWet", () => new Vector4(Wetness, RainAmount(Rain), GameTime, 0));
        g.PublishUniform("uWeatherDust", () => new Vector4(Dust, 1));   // w 1: triplanar map features get dust too (always on)
        g.Publish("uWeatherDustNoise", () => noise is { } n ? n.Sampled() : default);
    }

    /// <summary>Test overrides (<c>--wetness</c>, <c>--rain</c>, <c>--dust</c>; a NaN dust component keeps the weather's): they win over the weather state.</summary>
    public float? WetnessOverride, RainOverride;
    public Vector3? DustOverride;

    /// <summary>Takes the frame's weather at the camera (the scheduler's ramps, or a forced record snapped to its targets), then the overrides.</summary>
    public void Apply(Meitou.Data.World.WeatherState state)
    {
        Wetness = WetnessOverride ?? state.Wetness;
        Rain = RainOverride ?? state.Rain;
        var d = state.DustAmount;
        if (DustOverride is { } o) d = new Vector3(o.X, float.IsNaN(o.Y) ? d.Y : o.Y, float.IsNaN(o.Z) ? d.Z : o.Z);
        Dust = d;
    }

    public void Dispose() => noise?.Dispose();
}


/// <summary>The weather bits of a mesh draw (<c>uSurface</c> in <see cref="Shaders.MeshFragment"/>, the push constants' spare word).</summary>
static class MeshSurface
{
    /// <summary>The DUST define (building parts, triplanar map features): the weather's dust covers it.</summary>
    public const uint Dust = 1;
    /// <summary>The foliage shader: wetness with the fixed absorbance 0.9 (leaf and plant map features).</summary>
    public const uint Foliage = 2;
    /// <summary>An impostor bake: neither dust nor wetness is baked in.</summary>
    public const uint NoWeather = 4;
    /// <summary>A building interior: <c>dustAmount.y</c> instead of x, and no rain (nothing marks an interior in the viewer yet).</summary>
    public const uint Interior = 8;
    /// <summary>A triplanar map feature: dust as well (<c>uWeatherDust.w</c> is always 1; the game's caller table gives them no DUST).</summary>
    public const uint TriplanarDust = 16;
}
