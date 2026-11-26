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
    /// <summary>
    /// The handles in <paramref name="p"/> (unresolved names give invalid handles).
    /// </summary>
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
    /// <summary>
    /// The program.
    /// </summary>
    public readonly LegacyProgram P;

    /// <summary>
    /// The native segment the program is recorded in.
    /// </summary>
    public readonly NativeSegment Segment;

    /// <summary>
    /// The physical sky's uniforms: the inverse view-projection, <c>uSkyExtra</c>, the planets' spin, the cloud layer's light, sun and wind, the
    /// Meitou clouds' mode, key light, key direction and ambient, which textures it has, the Meitou stars, the celestial axes and the high clouds.
    /// </summary>
    public readonly UniformHandle InverseViewProjection, Extra, PlanetSpin, CloudLight, CloudSun, CloudWind, CloudMeitou, CloudKey, CloudKeyDir,
        CloudAmbient, Has, StarsMeitou, CelX, CelY, CelZ, HighClouds, HighCloudSeconds;

    /// <summary>
    /// The two planets' bodies (direction, sine of the angular radius).
    /// </summary>
    public readonly UniformHandle[] Planet;

    /// <summary>
    /// The simple model's colour uniforms.
    /// </summary>
    public readonly SkyColourHandles Colours;

    /// <summary>
    /// The sky's textures: stars, the two planets, the cloud layer (colour, normal, tile and their smooth variants), the volumetric clouds' image and
    /// the high clouds' cirrus.
    /// </summary>
    public readonly SamplerSlot Stars, Planet0, Planet1, Clouds, CloudsNormal, CloudsTile, CloudsSmooth, CloudsTileSmooth, VolumeImage, HighCirrus;

    /// <summary>
    /// The stars' and clouds' sampler reflection (null when the program has none).
    /// </summary>
    public readonly Meitou.Game.Rendering.Gpu.Shaders.SamplerInfo? StarsInfo, CloudsInfo;

    /// <summary>
    /// Makes the program and its segment and resolves every handle.
    /// </summary>
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
        HighClouds = P.Uniform("uHighClouds");
        HighCloudSeconds = P.Uniform("uHighCloudSeconds");
        Stars = P.Sampler("uStars");
        Planet0 = P.Sampler("uPlanet0");
        Planet1 = P.Sampler("uPlanet1");
        Clouds = P.Sampler("uClouds");
        CloudsNormal = P.Sampler("uCloudsNormal");
        CloudsTile = P.Sampler("uCloudsTile");
        CloudsSmooth = P.Sampler("uCloudsSmooth");
        CloudsTileSmooth = P.Sampler("uCloudsTileSmooth");
        VolumeImage = P.Sampler("uVolumeImage");
        HighCirrus = P.Sampler("uHighCirrus");
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

    /// <summary>
    /// The Meitou stars' row: computed (<paramref name="meitou"/>, by night for a sun height <paramref name="sunY"/>) or the game's texture, the
    /// <paramref name="layers"/> drawn, the twinkle's <paramref name="seconds"/> and the star <paramref name="gain"/>.
    /// </summary>
    public void SetStars(bool meitou, float sunY, int layers, float seconds, float gain)
    {
        // The Meitou stars: nothing to draw once the sun is high (SkyX's night factor is 0 by day; the game's texture is not drawn then either), so
        // the pass skips them.
        bool night = meitou && sunY < 0.3f;
        P.Set(StarsMeitou, meitou ? (night ? 1 : -1) : 0, layers, seconds, gain);
    }

    /// <summary>
    /// The celestial axes of the Meitou stars for the sun's path <paramref name="clock"/> at the game's <paramref name="day"/> and
    /// <paramref name="hour"/>.
    /// </summary>
    public void SetCelestialAxes(SkyClock clock, int day, float hour)
    {
        // Staging the title's sun must not turn the stars, which stay continuous into the loading sky.
        var frame = CelestialSphere.Frame(clock with { SunTurn = 0 }, day, hour);
        P.Set(CelX, frame.X.X, frame.X.Y, frame.X.Z, 0);
        P.Set(CelY, frame.Y.X, frame.Y.Y, frame.Y.Z, 0);
        P.Set(CelZ, frame.Z.X, frame.Z.Y, frame.Z.Z, 0);
    }

    /// <summary>
    /// The extra row: the planets' <paramref name="planetFill"/>, the cloud <paramref name="coverage"/> and the starfield's shift after
    /// <paramref name="starHours"/> game hours.
    /// </summary>
    public void SetExtra(float planetFill, float coverage, double starHours) =>
        P.Set(Extra, planetFill, coverage, Starfield.Shift(starHours), 0);

    /// <summary>
    /// The cloud layer's uniforms (<see cref="CloudPassValues"/>).
    /// </summary>
    public void SetClouds(in CloudPassValues v)
    {
        var p = P;
        p.Set(CloudLight, v.Light);
        p.Set(CloudSun, v.Sun);
        p.Set(CloudWind, v.Wind);
        p.Set(CloudMeitou, v.Meitou);
        p.Set(CloudKey, v.Key);
        p.Set(CloudKeyDir, v.KeyDir);
        p.Set(CloudAmbient, v.Ambient);
    }

    /// <summary>
    /// The high clouds' densities and amounts (uHighClouds) and clock.
    /// </summary>
    public void SetHighClouds(Vector4 look, float seconds)
    {
        P.Set(HighClouds, look);
        P.Set(HighCloudSeconds, seconds);
    }

    /// <summary>
    /// Frees the program.
    /// </summary>
    public void Dispose() => P.Dispose();
}
