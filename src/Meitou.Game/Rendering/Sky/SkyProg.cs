using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// The handles of the sky colour uniforms of <see cref="SkyRenderer.SkyFunctions"/> in a native program, resolved once.
/// </summary>
public readonly record struct SkyColourHandles(UniformHandle Sun, UniformHandle Zenith, UniformHandle Horizon, UniformHandle SunColour,
    UniformHandle Twilight)
{
    public static SkyColourHandles Resolve(LegacyProgram p) =>
        new(p.Uniform("uSkySun"), p.Uniform("uSkyZenith"), p.Uniform("uSkyHorizon"), p.Uniform("uSkySunColour"), p.Uniform("uSkyTwilight"));

    /// <summary>
    /// What the GL <c>SetUniforms</c> set on a program; the atmosphere's uniforms come from <see cref="LegacyProgram.ApplyGlobals"/>.
    /// </summary>
    public void Set(LegacyProgram p, SkyColours c)
    {
        p.Set(Sun, c.Sun.X, c.Sun.Y, c.Sun.Z);
        p.Set(Zenith, c.Zenith.X, c.Zenith.Y, c.Zenith.Z);
        p.Set(Horizon, c.Horizon.X, c.Horizon.Y, c.Horizon.Z);
        p.Set(SunColour, c.SunColour.X, c.SunColour.Y, c.SunColour.Z);
        p.Set(Twilight, c.Twilight);
    }
}

/// <summary>
/// A native sky program with its handles resolved at load.
/// </summary>
sealed class SkyProg : IDisposable
{
    public readonly LegacyProgram P;
    public readonly NativeSegment Segment;
    public readonly UniformHandle InverseViewProjection, Extra, PlanetSpin, CloudLight, CloudSun, CloudWind, CloudMeitou, CloudKey, CloudKeyDir,
        CloudAmbient, Has, StarsMeitou, CelX, CelY, CelZ;
    public readonly UniformHandle[] Planet;
    public readonly SkyColourHandles Colours;
    public readonly SamplerSlot Stars, Planet0, Planet1, Clouds, CloudsNormal, CloudsTile, CloudsSmooth, CloudsTileSmooth, VolumeImage;
    public readonly Meitou.Game.Rendering.Gpu.Shaders.SamplerInfo? StarsInfo, CloudsInfo;

    public SkyProg(IGpuContext gpu, string vertex, string fragment, string name)
    {
        P = LegacyProgram.Create(gpu, vertex, fragment, name);
        Segment = new NativeSegment(gpu, P, PrimitiveType.Triangles, name);
        InverseViewProjection = P.Uniform("uInverseViewProjection");
        Colours = SkyColourHandles.Resolve(P);
        Extra = P.Uniform("uSkyExtra");
        Planet = [P.Uniform("uPlanetBody0"), P.Uniform("uPlanetBody1")];
        PlanetSpin = P.Uniform("uPlanetSpin");
        CloudLight = P.Uniform("uCloudLight");
        CloudSun = P.Uniform("uCloudSun");
        CloudWind = P.Uniform("uCloudWind");
        CloudMeitou = P.Uniform("uCloudMeitou");
        CloudKey = P.Uniform("uCloudKey");
        CloudKeyDir = P.Uniform("uCloudKeyDir");
        CloudAmbient = P.Uniform("uCloudAmbient");
        Has = P.Uniform("uHas");
        StarsMeitou = P.Uniform("uStarsMeitou");
        CelX = P.Uniform("uCelX");
        CelY = P.Uniform("uCelY");
        CelZ = P.Uniform("uCelZ");
        Stars = P.Sampler("uStars");
        Planet0 = P.Sampler("uPlanet0");
        Planet1 = P.Sampler("uPlanet1");
        Clouds = P.Sampler("uClouds");
        CloudsNormal = P.Sampler("uCloudsNormal");
        CloudsTile = P.Sampler("uCloudsTile");
        CloudsSmooth = P.Sampler("uCloudsSmooth");
        CloudsTileSmooth = P.Sampler("uCloudsTileSmooth");
        VolumeImage = P.Sampler("uVolumeImage");
        if (Stars.IsValid) StarsInfo = P.SamplerInfo(Stars);
        if (Clouds.IsValid) CloudsInfo = P.SamplerInfo(Clouds);
    }

    /// <summary>
    /// The two planets' directions (turned by <paramref name="turn"/>) and sizes, and their spin by the game's <paramref name="day"/> and
    /// <paramref name="hour"/>.
    /// </summary>
    public void SetPlanets(int day, float hour, Quaternion turn)
    {
        var p = P;
        // The planets: fixed directions and sizes, each turned by the game's day and hour.
        Span<float> spin = stackalloc float[4];
        for (int i = 0; i < 2; i++)
        {
            var planet = SkyPlanet.All[i];
            var towards = Vector3.Transform(planet.Towards, turn);
            p.Set(Planet[i], towards.X, towards.Y, towards.Z, planet.SinRadius);
            float a = planet.Spin(day, hour);
            (spin[2 * i], spin[2 * i + 1]) = (MathF.Cos(a), MathF.Sin(a));
        }
        p.Set(PlanetSpin, spin[0], spin[1], spin[2], spin[3]);
    }

    public void Dispose() => P.Dispose();
}
