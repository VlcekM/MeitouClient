using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Textures;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The simple colour model the world view's sky had before the atmosphere (<c>--simple-sky</c>, key B): zenith and horizon colours
/// from the sun's height, a warm band at sunrise and sunset, the sun disc. Also the record that carries the sky's colours to
/// the water shader. The game's sky (<see cref="SkyRenderer"/>, docs/formats/sky.md) fills it from its own numbers.
/// </summary>
public readonly record struct SkyColours(Vector3 Sun, Vector3 Zenith, Vector3 Horizon, Vector3 SunColour, float Twilight)
{
    static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }

    public static SkyColours For(Vector3 sun)
    {
        float e = sun.Y;
        float day = Smooth(-0.12f, 0.2f, e);
        float twilight = 1 - Smooth(0, 0.35f, MathF.Abs(e + 0.02f));
        var zenith = Vector3.Lerp(new(0.01f, 0.015f, 0.035f), new(0.20f, 0.40f, 0.72f), day);
        var horizon = Vector3.Lerp(new(0.03f, 0.035f, 0.06f), new(0.70f, 0.76f, 0.82f), day);
        horizon = Vector3.Lerp(horizon, new(0.90f, 0.55f, 0.32f), twilight * 0.55f * Smooth(-0.15f, 0.0f, e));
        var sunColour = Vector3.Lerp(new(1.0f, 0.55f, 0.3f), new(1.0f, 0.96f, 0.9f), Smooth(0, 0.4f, e)) * Smooth(-0.04f, 0.04f, e);
        return new SkyColours(sun, zenith, horizon, sunColour, twilight);
    }

    /// <summary>Light for the world shaders; <paramref name="fogDistance"/> is where the haze is complete.</summary>
    public WorldLighting Lighting(float fogDistance)
    {
        // Below the horizon the shaders still get a direction (lit faces stay lit) but no sun colour.
        var dir = Sun.Y > 0.02f ? Sun : Vector3.Normalize(new Vector3(Sun.X, 0.02f, Sun.Z));
        var ambientSky = (Horizon * 0.7f + Zenith * 0.3f) * 0.5f + new Vector3(0.01f);
        var ambientGround = Horizon * 0.28f + new Vector3(0.01f);
        return new WorldLighting(dir, SunColour, ambientSky, ambientGround, Horizon, fogDistance);
    }
}

/// <summary>
/// The world view's sky, light and haze (docs/formats/sky.md, docs/formats/lighting.md). Game mode (<see cref="Physical"/>): SkyX's
/// sky drawn per pixel with the game's options, the sun's colour taken from SkyX the way the game's sky controller takes it, the
/// deferred lighting pass's image-based light (<c>mp_irradiance.dds</c>, <c>mp_specularity.dds</c>) and per-biome ambient map, and the
/// exposure band of the game's HDR composite (<see cref="MinLuminance"/>, <see cref="MaxLuminance"/>). Simple mode is the old colour
/// model. A shader that wants any of it includes <see cref="AtmosphereShaders.Functions"/> and gets its uniforms from <see cref="Apply"/>.
/// </summary>
public sealed unsafe class SkyRenderer : IDisposable
{
    /// <summary>The renderer whose uniforms <see cref="Apply"/> sets (there is one per world view).</summary>
    public static SkyRenderer? Active { get; private set; }

    /// <summary>GLSL for shaders that look up the sky colour for a direction: <c>skyColour(dir, disc)</c> (what the water reflects).</summary>
    public static readonly string SkyFunctions = AtmosphereShaders.Functions + """

        uniform vec3 uSkySun;
        uniform vec3 uSkyZenith;
        uniform vec3 uSkyHorizon;
        uniform vec3 uSkySunColour;
        uniform float uSkyTwilight;
        vec3 skySimple(vec3 dir, bool disc)
        {
            float up = clamp(dir.y, 0.0, 1.0);
            float t = pow(up, 0.45);
            vec3 c = mix(uSkyHorizon, uSkyZenith, t);
            float toward = max(dot(dir, uSkySun), 0.0);
            c += vec3(1.0, 0.6, 0.3) * pow(toward, 6.0) * uSkyTwilight * 0.45 * (1.0 - t);
            c += uSkySunColour * pow(toward, 64.0) * 0.25;
            if (disc) c += uSkySunColour * smoothstep(0.99955, 0.9998, toward) * 3.0;
            return c;
        }
        vec3 skyColour(vec3 dir, bool disc) { return uAtmoParams.x > 0.5 ? atmoSky(dir) : skySimple(dir, disc); }
        """;

    const string Vertex = """
        #version 330 core
        out vec2 vNdc;
        void main()
        {
            vec2 p = vec2((gl_VertexID & 1) * 4.0 - 1.0, (gl_VertexID & 2) * 2.0 - 1.0);
            vNdc = p;
            gl_Position = vec4(p, 1.0, 1.0);
        }
        """;

    const string SimpleFragment = "#version 330 core\n" + """

        in vec2 vNdc;
        out vec4 fragColour;
        uniform mat4 uInverseViewProjection;
        uniform vec3 uSkySun;
        uniform vec3 uSkyZenith;
        uniform vec3 uSkyHorizon;
        uniform vec3 uSkySunColour;
        uniform float uSkyTwilight;
        void main()
        {
            vec4 a = uInverseViewProjection * vec4(vNdc, 0.0, 1.0);
            vec4 b = uInverseViewProjection * vec4(vNdc, 1.0, 1.0);
            vec3 dir = normalize(b.xyz / b.w - a.xyz / a.w);
            float up = clamp(dir.y, 0.0, 1.0);
            float t = pow(up, 0.45);
            vec3 c = mix(uSkyHorizon, uSkyZenith, t);
            float toward = max(dot(dir, uSkySun), 0.0);
            c += vec3(1.0, 0.6, 0.3) * pow(toward, 6.0) * uSkyTwilight * 0.45 * (1.0 - t);
            c += uSkySunColour * pow(toward, 64.0) * 0.25;
            c += uSkySunColour * smoothstep(0.99955, 0.9998, toward) * 3.0;
            fragColour = vec4(c, 1.0);
        }
        """;

    // SkyX's skydome per pixel (HDR): the scattered light, its night glow and starfield; no sun disc (the game's sun is the Mie lobe).
    static string F(float v) => v.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture) + (v == MathF.Floor(v) ? ".0" : "");

    static readonly string SkyFragment = "#version 330 core\n" + AtmosphereShaders.Functions + $$"""

        in vec2 vNdc;
        out vec4 fragColour;
        uniform mat4 uInverseViewProjection;
        uniform vec4 uSkyExtra;        // x: unused, y: cloud density c (0 skips the cloud pass), z: stars' turn (radians), w: moon radius (radians)
        uniform vec3 uMoonDir, uMoonRight, uMoonUp;
        uniform vec4 uCloudLight;      // rgb: zenithLight, a: Darkness
        uniform vec4 uCloudSun;        // rgb: sunColour.rgb, a: DensityOffset
        uniform vec4 uCloudWind;       // xy: the wind offset x 0.00005 (wrapped to 0..1)
        uniform sampler2D uStars, uMoon, uClouds, uCloudsNormal, uCloudsTile;
        uniform vec3 uHas;             // stars, moon, cloud textures (all three) present
        void main()
        {
            vec4 a = uInverseViewProjection * vec4(vNdc, 0.0, 1.0);
            vec4 b = uInverseViewProjection * vec4(vNdc, 1.0, 1.0);
            vec3 dir = normalize(b.xyz / b.w - a.xyz / a.w);
            float night;
            vec3 col = atmoSky(dir, night);
            float aboveHorizon = smoothstep(-0.02, 0.06, dir.y);
            // Weather fog hides the sky near the horizon too (the terrain beyond its far distance is plain fog colour).
            if (uAtmoFog.z > 0.5) col = mix(col, uAtmoFogColour, 1.0 - smoothstep(0.0, 0.45, dir.y));

            // Stars: SkyX_Starfield.dds (SkyX's HDR form: nightmult · texture · (0.35 + saturate(−sunY · 0.45)) · 2), laid over the upper
            // hemisphere stereographically and turning with the night (the dome's own UV layout is not reproduced).
            if (uHas.x > 0.5 && night > 0.0 && dir.y > 0.0)
            {
                float cs = cos(uSkyExtra.z), sn = sin(uSkyExtra.z);
                vec2 r = vec2(cs * dir.x - sn * dir.z, sn * dir.x + cs * dir.z);
                vec3 stars = texture(uStars, r / (1.0 + dir.y) * 0.5 + 0.5).rgb;
                col += night * aboveHorizon * stars * (0.35 + clamp(-uAtmoSun.y * 0.45, 0.0, 1.0)) * 2.0;
            }
            // Clouds: SkyX's cloud layer on the dome (SkyX_Clouds.hlsl, docs/formats/clouds.md), after the sky and the stars, before the moon;
            // alpha-blended over them in HDR. The dome direction below the horizon is evaluated at a hair above it (alpha is then the horizon value).
            if (uHas.z > 0.5 && uSkyExtra.y > 0.0)
            {
                vec3 d = normalize(vec3(dir.x, max(dir.y, 0.0005), dir.z));
                float o = uCloudSun.w;
                const float MULT = {{F(CloudLayer.DensityMultiplier)}}, SCALE = {{F(CloudLayer.Scale)}}, HEIGHT = {{F(CloudLayer.Height)}};
                vec2 wind = uCloudWind.xy;
                // The plane hit: the cloud point is d · height / d.y, the texture coordinate its xz · scale.
                vec2 uv = d.xz * (HEIGHT / d.y) * SCALE;
                float density = texture(uClouds, uv + wind).r;
                vec3 normal = -(2.0 * texture(uCloudsNormal, uv + wind).rgb - 1.0);
                normal = vec3(normal.x, normal.z, normal.y);   // the shader swaps y and z
                density = clamp((density + o) * MULT, 0.0, 1.0);
                // The fake volume: the direction bent along the normal map, the plane raised where the cloud is thin.
                vec3 nd = normalize(d + {{F(CloudLayer.VolumetricDisplacement)}} * d.y * vec3(normal.x, 0.0, normal.z));
                float vh = (HEIGHT + HEIGHT * (1.0 - density) * {{F(CloudLayer.HeightVolume)}} * d.y) / nd.y;
                uv = nd.xz * vh * SCALE;
                density = (texture(uClouds, uv + wind + vec2({{F(CloudLayer.SecondLookupShift.X)}}, {{F(CloudLayer.SecondLookupShift.Y)}})).r + o) * MULT;
                float tile = texture(uCloudsTile, uv - wind).r;
                density += tile * {{F(CloudLayer.TileWeight)}};
                vec3 pixel = uCloudLight.rgb + uCloudSun.rgb * (1.0 - density * 0.1);
                // The horizon band: from 0.05 to 0.15 of d.y the layer gives way to the uniform alpha o + 0.5 (horizonClouds.a).
                float band = clamp(10.0 * clamp(d.y - {{F(CloudLayer.DistanceAttenuation)}}, 0.0, 1.0), 0.0, 1.0);
                density += band;
                pixel *= 1.0 - clamp(density, 0.0, 1.0) * uCloudLight.a;
                float alpha = density * clamp(1.0 - tile + o, 0.0, 1.0);
                alpha = mix(o + 0.5, alpha, band);
                // The viewer's weather fog hides the sky near the horizon (above); the clouds fade out with it, as they did before.
                float fogKeep = uAtmoFog.z > 0.5 ? smoothstep(0.0, 0.45, dir.y) : 1.0;
                col = mix(col, clamp(pixel, 0.0, 1.0) * sqrt(SKYX_EXPOSURE), clamp(alpha, 0.0, 1.0) * fogKeep);
            }
            // Moon: SkyX_Moon.png on a disc opposite the sun (always full; SkyX_Moon.hlsl saturates its colour and blends by alpha).
            float md = dot(dir, uMoonDir);
            if (uHas.y > 0.5 && md > 0.0)
            {
                vec2 p = vec2(dot(dir, uMoonRight), dot(dir, uMoonUp)) / md / uSkyExtra.w;
                vec4 m = texture(uMoon, vec2(p.x, -p.y) * 0.195 + 0.5);
                if (abs(p.x) < 2.6 && abs(p.y) < 2.6)
                    col = mix(col, clamp(m.rgb, 0.0, 1.0), m.a * night * aboveHorizon * smoothstep(0.0, 0.1, uMoonDir.y));
            }
            fragColour = vec4(col, 1.0);
        }
        """;

    /// <summary>The native GPU API (phase 8 stage 2: the sky makes no GL call; docs/renderer-native.md 8.6).</summary>
    public GpuContext Gpu { get; }
    // Native programs (docs/renderer-native.md 7.1, wave 3 agent D): the same SPIR-V as the GL programs they replace.
    readonly SkyProg simple, sky;
    // Native textures with the GL sampler state their GL versions had (phase 8 stage 2); null when the file was not found.
    SampledImage? starsTexture, moonTexture, cloudsTexture, cloudsNormalTexture, cloudsTileTexture, irradianceCube, specularCube, ambientMap;
    readonly PassTimer skyTimer;
    readonly List<double> gpuSamples = [];
    double gpuTotal;
    int gpuCount;

    /// <summary>The game's sky and light (default) or the old simple colour model.</summary>
    public bool Physical { get; set; } = true;
    /// <summary>Haze: Kenshi's own (default, docs/formats/sky.md "Haze") or the physical integral over SkyX's air.</summary>
    public bool KenshiHaze { get; set; } = true;
    /// <summary>
    /// Kenshi's far distance D (<c>view distance × 10</c>, 50000 with the install's setting); its haze ramps in between 0.06 D and
    /// 0.6 D. Independent of the viewer's far clip: past it the game's own formula goes on.
    /// </summary>
    public float HazeDistance { get; set; } = Meitou.Data.World.KenshiHaze.FarDistance(Meitou.Data.World.KenshiHaze.ViewDistanceSetting);
    /// <summary>
    /// The viewer's haze strength (not the game's; 1 = the game's haze): scales how far the atmosphere haze is blended in (the game's ramp,
    /// or the physical haze's amount), not the weather's fog.
    /// </summary>
    public float HazeStrength { get; set; } = Enhancements.MeitouHazeStrength;
    /// <summary>
    /// The eye's height above the highest ground or water within the game's longest camera boom (<see cref="KenshiCamera.MaxDistance"/>) around it,
    /// set each frame by <see cref="SetEye"/>. The game's camera never gets more than <see cref="KenshiCamera.MaxHeightAbovePivot"/> above its
    /// pivot, which sits on the ground within that distance, so in-game this stays under about 1840 (plus a roof the pivot may stand on).
    /// </summary>
    public float EyeClearance { get; private set; }
    /// <summary>
    /// The viewer's height band (its own choice; no game behaviour exists up there) over which the game's haze, which measures from SkyX's
    /// fixed eye near the ground, gives way to the physical haze: none below <see cref="AltitudeBandStart"/>, all of it above <see cref="AltitudeBandEnd"/>.
    /// </summary>
    public const float AltitudeBandStart = 5100, AltitudeBandEnd = 18700;
    /// <summary>0 within the game's camera heights, rising to 1 across the altitude band (smoothstep of <see cref="EyeClearance"/>).</summary>
    public float AltitudeWeight
    {
        get
        {
            float t = Math.Clamp((EyeClearance - AltitudeBandStart) / (AltitudeBandEnd - AltitudeBandStart), 0, 1);
            return t * t * (3 - 2 * t);
        }
    }

    /// <summary>
    /// Measures the eye's <see cref="EyeClearance"/>: its height above the highest of the ground (<paramref name="groundAt"/>) and
    /// <paramref name="floor"/> (the water level, or −∞) at the eye and on two rings round it out to the game's longest boom.
    /// </summary>
    public void SetEye(Vector3 eye, Func<float, float, float> groundAt, float floor)
    {
        float top = MathF.Max(groundAt(eye.X, eye.Z), floor);
        foreach (float r in (ReadOnlySpan<float>)[KenshiCamera.MaxDistance * 0.5f, KenshiCamera.MaxDistance])
            for (int i = 0; i < 8; i++)
            {
                float a = i * MathF.PI / 4;
                top = MathF.Max(top, groundAt(eye.X + r * MathF.Cos(a), eye.Z + r * MathF.Sin(a)));
            }
        EyeClearance = eye.Y - top;
    }

    /// <summary>The physical haze only: world units in one density scale height of SkyX's air (the game's world unit is Unknown; a viewer choice).</summary>
    public float ScaleHeightUnits { get; set; } = 40000;
    public SkyWeather Weather { get; set; } = SkyWeather.Default;
    /// <summary>Test override of the cloud density c (<c>--clouds</c>); wins over <see cref="CloudDensityInput"/> and the weather's.</summary>
    public float? CloudCoverage { get; set; }
    /// <summary>
    /// The cloud density c the weather system gives (after the sky's 30 s transition), when it drives the sky; null takes the forced
    /// <see cref="Weather"/> record's. Clamped to 0..1 as the game's sky controller does.
    /// </summary>
    public float? CloudDensityInput { get; set; }
    /// <summary>The sky colour multiplier the weather system gives (after the transition); null takes the forced <see cref="Weather"/> record's.</summary>
    public Vector3? SkyColourMultiplierInput { get; set; }
    /// <summary>The clouds' drift velocity (the weather's wind direction.xz × speed, world units per second); <see cref="StepClouds"/> accumulates it.</summary>
    public Vector2 CloudWind { get; set; }
    /// <summary>The cloud density in use, 0..1.</summary>
    public float CloudDensity => Math.Clamp(CloudCoverage ?? CloudDensityInput ?? Weather.CloudDensity, 0, 1);
    /// <summary>The sky colour multiplier in use.</summary>
    public Vector3 SkyColourMultiplier => SkyColourMultiplierInput ?? Weather.SkyColourMultiplier;

    (double X, double Z) cloudOffset;
    readonly Stopwatch cloudClock = new();
    /// <summary>The accumulated wind offset of the cloud layer (world units, the sky controller's; the shader shifts the textures by it × 0.00005).</summary>
    public (double X, double Z) CloudOffset => cloudOffset;

    /// <summary>Advances the clouds' drift by <paramref name="dt"/> seconds of the caller's frame clock (0 holds them still).</summary>
    public void StepClouds(float dt) => cloudOffset = CloudLayer.Advance(cloudOffset, CloudWind, dt);

    /// <summary>
    /// Advances the drift by the real time since the last call (at most 0.25 s, game speed 1), or holds it still for a still picture
    /// (<paramref name="held"/>: <c>--screenshot</c>), the viewer's frame clock as the heat haze's.
    /// </summary>
    public void StepClouds(bool held)
    {
        float dt = held || !cloudClock.IsRunning ? 0 : (float)Math.Min(cloudClock.Elapsed.TotalSeconds, 0.25);
        cloudClock.Restart();
        StepClouds(dt);
    }
    /// <summary>CONSTANTS <c>exposure min</c> / <c>exposure max</c> / <c>night darkness</c>: the composite's luminance band.</summary>
    public ExposureConstants Exposure { get; set; } = ExposureConstants.Default;
    /// <summary>This frame's <c>MIN_LUMINANCE</c> / <c>MAX_LUMINANCE</c> (game mode); the composite scales by 0.55 over the scene's mean luminance clamped to them.</summary>
    public float MinLuminance => state.MinLuminance;
    public float MaxLuminance => Exposure.Max;
    /// <summary>CPU time of the last <see cref="Prepare"/> in ms.</summary>
    public double PrepareMs { get; private set; }
    /// <summary>Which of the game's lighting textures were found (irradiance cube, specular cube, ambient map).</summary>
    public string LightingTextures => $"irradiance {(irradianceCube is not null ? "yes" : "no")}, specularity {(specularCube is not null ? "yes" : "no")}, ambient map {(ambientMap is not null ? "yes" : "no")}";

    struct State
    {
        public Vector3 Sun, SunLight, LightDirection, CloudSun, CloudZenith;   // the last two: sunColour.rgb and zenithLight of the cloud pass
        public float Environment, MinLuminance, FogDistance;
        public SkyColours Colours;
        public WorldLighting Light;
        public bool Valid;
    }
    State state;
    Vector3 builtSun = new(float.NaN);
    SkyWeather? builtWeather;
    Vector3 builtTint;
    bool builtPhysical;

    public SkyRenderer(GpuContext gpu, AssetLocator? assets = null)
    {
        Gpu = gpu;
        simple = new SkyProg(gpu, Vertex, SimpleFragment, "sky simple");
        sky = new SkyProg(gpu, Vertex, SkyFragment, "sky");
        skyTimer = new PassTimer(gpu);
        if (assets is not null)
        {
            LoadTextures(assets);
            Console.WriteLine($"sky       textures: starfield {(starsTexture is not null ? "yes" : "no")}, moon {(moonTexture is not null ? "yes" : "no")}, clouds {(cloudsTexture is not null ? "yes" : "no")}");
        }
        Active = this;
        PublishGlobals();
        ShadowsOffGlobals.Publish(gpu);
    }

    void LoadTextures(AssetLocator assets)
    {
        try
        {
            if (assets.Find("SkyX_Starfield.dds") is { } stars)
            {
                var dds = DdsReader.ReadFile(stars);
                var img = DdsDecoder.Decode(dds, 0, Math.Min(2, dds.MipCount - 1));   // 1024²: one level of the 4096² file
                starsTexture = SampledImage.Rgba8(Gpu, img, repeat: false, mipmaps: true, "sky stars");
            }
            if (assets.Find("SkyX_Moon.png") is { } moon)
            {
                var img = TextureLoader.LoadFile(moon, allMips: false).Levels[0];
                moonTexture = SampledImage.Rgba8(Gpu, img, repeat: false, mipmaps: true, "sky moon");
            }
            if (assets.Find("Clouds.dds") is { } clouds)
            {
                var img = TextureLoader.LoadFile(clouds, allMips: false).Levels[0];
                cloudsTexture = SampledImage.Rgba8(Gpu, img, repeat: true, mipmaps: true, "sky clouds");
            }
            if (assets.Find("CloudsNormal.dds") is { } cloudsNormal)
            {
                var img = TextureLoader.LoadFile(cloudsNormal, allMips: false).Levels[0];
                cloudsNormalTexture = SampledImage.Rgba8(Gpu, img, repeat: true, mipmaps: true, "sky clouds normal");
            }
            if (assets.Find("CloudsTile.dds") is { } cloudsTile)
            {
                var img = TextureLoader.LoadFile(cloudsTile, allMips: false).Levels[0];
                cloudsTileTexture = SampledImage.Rgba8(Gpu, img, repeat: true, mipmaps: true, "sky clouds tile");
            }
            if (assets.Find("mp_irradiance.dds") is { } irradiance) irradianceCube = SampledImage.Cube(Gpu, DdsReader.ReadFile(irradiance), "sky irradiance");
            if (assets.Find("mp_specularity.dds") is { } specularity) specularCube = SampledImage.Cube(Gpu, DdsReader.ReadFile(specularity), "sky specularity");
        }
        catch (Exception e) when (e is DdsFormatException or IOException or InvalidOperationException)
        {
            Console.WriteLine($"warning   sky textures: {e.Message}");
        }
    }

    /// <summary>
    /// The ambient map, built the game's way from <c>biomemap.png</c> and the BIOMES records (<see cref="AmbientMap"/>), and the CONSTANTS
    /// exposure values.
    /// </summary>
    public void LoadWorld(GameInstall install, GameDatabase db)
    {
        Exposure = ExposureConstants.FromDatabase(db);
        try
        {
            var biomes = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install.DataDirectory, TerrainMaps.BiomeMap)));
            var map = AmbientMap.Build(db, biomes);
            ambientMap?.Dispose();
            ambientMap = SampledImage.Rgba8(Gpu, map, repeat: false, mipmaps: false, "sky ambient map");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or InvalidDataException)
        {
            Console.WriteLine($"warning   ambient map: {e.Message}");
        }
        Console.WriteLine($"lighting  {LightingTextures}; exposure band {Exposure.Min:0.##}..{Exposure.Max:0.##}, night darkness {Exposure.NightDarkness:0.##}");
    }

    // ---- per frame -------------------------------------------------------------------------------------

    /// <summary>
    /// Brings the sky up to date for this frame's sun and returns the colours and light the world shaders use.
    /// <paramref name="fogDistance"/> is where the physical haze is complete.
    /// </summary>
    public (SkyColours Colours, WorldLighting Light) Prepare(Vector3 sun, float eyeHeight, float fogDistance)
    {
        var watch = Stopwatch.StartNew();
        if (!Physical)
        {
            var simple = SkyColours.For(sun);
            var light = simple.Lighting(fogDistance);
            state = new State { Sun = sun, Colours = simple, Light = light, FogDistance = fogDistance, Valid = true, MinLuminance = Exposure.Min,
                CloudSun = KenshiLighting.SunColour(sun), CloudZenith = CloudLayer.ZenithLight(sun, SkyColourMultiplier) };
            builtPhysical = false;
            PrepareMs = watch.Elapsed.TotalMilliseconds;
            return (simple, light);
        }
        // SkyX's sky does not depend on the eye's height (its camera is fixed); the light only changes with the sun and the weather.
        if (!builtPhysical || !ReferenceEquals(builtWeather, Weather) || builtTint != SkyColourMultiplier || (sun - builtSun).LengthSquared() > 1e-10f)
        {
            builtSun = sun;
            builtWeather = Weather;
            builtTint = SkyColourMultiplier;
            builtPhysical = true;
            var sunLight = KenshiLighting.SunLight(sun);
            var lightDir = KenshiLighting.LightDirection(sun);
            float env = KenshiLighting.EnvironmentFactor(lightDir);
            var tint = SkyColourMultiplier;
            var zenith = SkyXModel.Colour(Vector3.UnitY, sun) * tint;
            var flat = new Vector2(sun.X, sun.Z);
            flat = flat.LengthSquared() > 1e-8f ? Vector2.Normalize(flat) : Vector2.UnitX;
            var horizon = (SkyXModel.Colour(new Vector3(flat.X, 0.05f, flat.Y), sun) + SkyXModel.Colour(new Vector3(-flat.X, 0.05f, -flat.Y), sun)) * 0.5f * tint;
            float twilight = 1 - Math.Clamp(MathF.Abs(sun.Y + 0.02f) / 0.35f, 0, 1);
            // For the code that still takes a sun colour and an ambient (the water): a white surface facing the sun reflects π · 0.96 · light;
            // the ambient is the irradiance cube's up and down faces (docs/formats/lighting.md) times the environment factor.
            var sunRadiance = sunLight * (MathF.PI * (1 - KenshiLighting.DielectricSpecular));
            var ambientSky = new Vector3(1.09f, 1.20f, 1.51f) * (env * 0.96f);
            var ambientGround = new Vector3(1.11f, 1.05f, 0.96f) * (env * 0.96f);
            state = new State
            {
                Sun = sun, SunLight = sunLight, LightDirection = lightDir, Environment = env, FogDistance = fogDistance,
                CloudSun = KenshiLighting.SunColour(sun), CloudZenith = CloudLayer.ZenithLight(sun, tint),
                MinLuminance = KenshiLighting.MinLuminance(sun.Y, Exposure.Min, Exposure.NightDarkness),
                Colours = new SkyColours(sun, zenith, horizon, sunRadiance, twilight),
                Light = new WorldLighting(lightDir, sunRadiance, ambientSky, ambientGround, horizon, fogDistance), Valid = true,
            };
        }
        else
        {
            state.Light = state.Light with { FogDistance = fogDistance };
            state.FogDistance = fogDistance;
        }
        PrepareMs = watch.Elapsed.TotalMilliseconds;
        return (state.Colours, state.Light);
    }

    /// <summary>The values of <see cref="AtmosphereShaders.Functions"/>' loose uniforms (what <see cref="Apply"/> sets), by GLSL name minus <c>uAtmo</c>.</summary>
    public readonly record struct AtmosphereUniforms(Vector4 Tau, Vector4 Params, Vector4 Sun, Vector4 Light, Vector3 SunLight, Vector3 Tint, Vector4 Fog,
        Vector3 FogColour, Vector4 Simple, Vector4 Haze, Vector4 HazeCloud, Vector4 Altitude, Vector4 Maps);

    /// <summary>The atmosphere uniforms for the current state (valid only while <c>state.Valid</c>).</summary>
    AtmosphereUniforms Uniforms()
    {
        var s = state;
        var tau = SkyAtmosphere.RayleighZenithDepth;
        var tint = SkyColourMultiplier;
        var w = Weather;
        // The weather fog's colour times sunColour.w, the daylight scale (the game's global fog term).
        var fog = w.FogColour * (Physical ? KenshiLighting.Daylight(s.Sun.Y) : 1f);
        var hc = s.Colours.Horizon;
        float hazeStart = HazeDistance * Meitou.Data.World.KenshiHaze.StartFraction;
        float hazeEnd = MathF.Min(HazeDistance, HazeDistance * Meitou.Data.World.KenshiHaze.EndFraction);
        var (cloud, pull) = HorizonClouds(s);
        return new AtmosphereUniforms(
            new Vector4(tau.X, tau.Y, tau.Z, SkyAtmosphere.MieZenithDepth),
            new Vector4(Physical ? 1f : 0f, MathF.Max(s.FogDistance, 1), ScaleHeightUnits, 0),
            new Vector4(s.Sun.X, s.Sun.Y, s.Sun.Z, 0),
            new Vector4(s.LightDirection.X, s.LightDirection.Y, s.LightDirection.Z, s.Environment),
            new Vector3(s.SunLight.X, s.SunLight.Y, s.SunLight.Z),
            new Vector3(tint.X, tint.Y, tint.Z),
            new Vector4(w.FogMin, w.FogMax, w.FogEnabled ? 1f : 0f, 0),
            new Vector3(fog.X, fog.Y, fog.Z),
            new Vector4(hc.X, hc.Y, hc.Z, MathF.Max(s.FogDistance, 1)),
            // The weather fog is complete at a distance between `fog distance min` and `max` by the wind; the viewer has no wind and takes max.
            new Vector4(KenshiHaze ? 1f : 0f, hazeStart, hazeEnd, w.FogEnabled && w.FogMax > 1 ? 1f / w.FogMax : 0f),
            new Vector4(cloud.X, cloud.Y, cloud.Z, pull),
            new Vector4(KenshiHaze ? AltitudeWeight : 1f, MathF.Max(HazeStrength, 0), AltitudeWeight, 0),
            new Vector4(irradianceCube is not null ? 1f : 0f, specularCube is not null ? 1f : 0f, ambientMap is not null ? 1f : 0f, AmbientMap.HalfWorld));
    }

    /// <summary>
    /// Publishes the atmosphere to the frame globals (docs/renderer-native.md 4.3): the uniform values (what the GL <c>Apply</c> set on a
    /// program), and the three textures, natively (phase 8 stage 2; before, the GL texture units <c>BindUnits</c> bound them to). Both appear
    /// once the sky has a state (<see cref="Prepare(Vector3, float, float)"/>), as the GL code set them from then on; before that the readers get
    /// the stand-in. A missing file gives the stand-in too (GL's unbound unit).
    /// </summary>
    void PublishGlobals()
    {
        var g = Gpu.Globals;
        bool Valid() => state.Valid;
        g.Publish("uAtmoIrradiance", () => state.Valid && irradianceCube is { } t ? t.Sampled() : default);
        g.Publish("uAtmoSpecular", () => state.Valid && specularCube is { } t ? t.Sampled() : default);
        g.Publish("uAtmoAmbientMap", () => state.Valid && ambientMap is { } t ? t.Sampled() : default);
        g.PublishUniform("uAtmoTau", () => Published().Tau, Valid);
        g.PublishUniform("uAtmoParams", () => Published().Params, Valid);
        g.PublishUniform("uAtmoSun", () => Published().Sun, Valid);
        g.PublishUniform("uAtmoLight", () => Published().Light, Valid);
        g.PublishUniform("uAtmoSunLight", () => Published().SunLight, Valid);
        g.PublishUniform("uAtmoTint", () => Published().Tint, Valid);
        g.PublishUniform("uAtmoFog", () => Published().Fog, Valid);
        g.PublishUniform("uAtmoFogColour", () => Published().FogColour, Valid);
        g.PublishUniform("uAtmoSimple", () => Published().Simple, Valid);
        g.PublishUniform("uAtmoHaze", () => Published().Haze, Valid);
        g.PublishUniform("uAtmoHazeCloud", () => Published().HazeCloud, Valid);
        g.PublishUniform("uAtmoAltitude", () => Published().Altitude, Valid);
        g.PublishUniform("uAtmoMaps", () => Published().Maps, Valid);
    }

    // The values the getters above share, computed once per ApplyGlobals call (FrameGlobals.ApplyCount) instead of once per name.
    AtmosphereUniforms published;
    int publishedAt = -1;

    AtmosphereUniforms Published()
    {
        int at = Gpu.Globals.ApplyCount;
        if (at != publishedAt) (published, publishedAt) = (Uniforms(), at);
        return published;
    }

    /// <summary>
    /// <c>horizonClouds</c> (docs/formats/sky.md): the pull is <c>saturate(DensityOffset + 0.5)</c>, the colour is the cloud pass's own light,
    /// <c>saturate(sun (1 − 0.1 (offset + 0.2) · 3) + zenithLight) (1 − darkness) √exposure</c>, from the same c as the layer.
    /// </summary>
    (Vector3 Colour, float Pull) HorizonClouds(State s)
    {
        float c = CloudDensity;
        return (CloudLayer.HorizonColour(s.CloudSun, s.CloudZenith, c), CloudLayer.HorizonAlpha(c));
    }

    // ---- drawing ---------------------------------------------------------------------------------------

    /// <summary>
    /// The sky pass: a full-screen triangle into the pass VkGl has open (the scene's, or the reflection's multisampled one). Prepare sets the
    /// program's uniforms and textures; Record is one native segment (docs/renderer-native.md 7.5) with the pass's state, depth test and write off.
    /// No GL state is changed (phase 8 stage 2): the GL code turned the depth test and write off around the draw and back on after it, and
    /// every caller turns the depth test on after the sky itself (docs/renderer-native.md 8.6).
    /// </summary>
    public void Draw(Matrix4x4 viewProjection, SkyColours colours)
    {
        if (!Matrix4x4.Invert(viewProjection, out var inverse)) return;
        Poll();   // a timestamp pair is readable only for a few frames after its own (the arena's ring), so collect them as they come
        skyTimer.Begin();
        var program = Physical && state.Valid ? sky : simple;
        Prepare(program, inverse, colours);
        Record(program);
        skyTimer.End();
    }

    void Prepare(SkyProg program, in Matrix4x4 inverse, SkyColours colours)
    {
        var p = program.P;
        p.Set(program.InverseViewProjection, in inverse);
        program.Colours.Set(p, colours);
        if (program == sky) SetSkyUniforms(program);
        p.ApplyGlobals();   // the atmosphere's uniforms and textures, through the frame globals
    }

    void Record(SkyProg program)
    {
        var cmd = Gpu.BeginGuest(program == sky ? "sky" : "sky simple");
        var targets = Gpu.CurrentTargets();
        // What the GL code's Disable(DepthTest) and DepthMask(false) made of the pass's state.
        var drawState = Gpu.CurrentState() with { DepthTest = false, DepthWrite = false };
        cmd.SetViewport(targets.Viewport);
        cmd.SetScissor(targets.Scissor);
        cmd.SetRaster(drawState.Cull, drawState.Front);
        cmd.SetDepth(drawState.DepthTest, drawState.DepthWrite, drawState.Compare);
        cmd.SetDepthBias(drawState.BiasEnable, drawState.BiasConstant, drawState.BiasSlope);
        cmd.BindPipeline(program.Segment.Get(drawState, targets.Formats, null));
        program.P.Flush(cmd);
        cmd.Draw(3);
        Gpu.EndGuest(cmd);
    }

    void SetSkyUniforms(SkyProg program)
    {
        var s = state;
        var p = program.P;
        float coverage = CloudDensity;
        // The stars turn with the sun's half-turn (a = phase · π); the moon stands opposite the sun.
        float turn = MathF.Atan2(s.Sun.Z, s.Sun.X);
        var moon = Vector3.Normalize(-s.Sun);
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, moon));
        var up = Vector3.Cross(moon, right);
        const float moonRadius = 0.016f;
        p.Set(program.Extra, 0, coverage, turn, moonRadius);
        p.Set(program.MoonDir, moon.X, moon.Y, moon.Z);
        p.Set(program.MoonRight, right.X, right.Y, right.Z);
        p.Set(program.MoonUp, up.X, up.Y, up.Z);
        // The cloud pass: zenithLight and Darkness, sunColour.rgb and DensityOffset, the wind offset's texture shift.
        var zenith = s.CloudZenith;
        p.Set(program.CloudLight, zenith.X, zenith.Y, zenith.Z, CloudLayer.Darkness(coverage));
        p.Set(program.CloudSun, s.CloudSun.X, s.CloudSun.Y, s.CloudSun.Z, CloudLayer.DensityOffset(coverage));
        var shift = CloudLayer.TextureShift(cloudOffset);
        p.Set(program.CloudWind, shift.X, shift.Y, 0, 0);
        bool clouds = cloudsTexture is not null && cloudsNormalTexture is not null && cloudsTileTexture is not null;
        p.Set(program.Has, starsTexture is not null ? 1f : 0f, moonTexture is not null ? 1f : 0f, clouds ? 1f : 0f);
        // A missing texture reads GL's stand-in, which an unbound sampler does too.
        if (starsTexture is not null) p.Bind(program.Stars, starsTexture.Sampled());
        if (moonTexture is not null) p.Bind(program.Moon, moonTexture.Sampled());
        if (cloudsTexture is not null) p.Bind(program.Clouds, cloudsTexture.Sampled());
        if (cloudsNormalTexture is not null) p.Bind(program.CloudsNormal, cloudsNormalTexture.Sampled());
        if (cloudsTileTexture is not null) p.Bind(program.CloudsTile, cloudsTileTexture.Sampled());
    }

    /// <summary>
    /// Records <paramref name="count"/> sky passes back to back and returns the CPU time each took to record (ms; no table any more), for the
    /// screenshot mode's cost report (<c>MEITOU_SKY_BENCH</c>). The GL version waited for the GPU (<c>Finish</c>); the native API has no such
    /// wait inside a frame, so the GPU's share shows in <see cref="GpuMs"/> instead.
    /// </summary>
    public (double SkyPassMs, double TableMs) Benchmark(Matrix4x4 viewProjection, int count = 100)
    {
        var colours = state.Colours;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < count; i++) Draw(viewProjection, colours);
        return (watch.Elapsed.TotalMilliseconds / count, 0);
    }

    /// <summary>Collects finished GPU timings: native timestamps, read once their frame's slot comes round. Nothing waits, also with
    /// <paramref name="wait"/> (kept for callers; the GL queries could block).</summary>
    public void Poll(bool wait = false)
    {
        _ = wait;
        gpuSamples.Clear();
        skyTimer.Poll(gpuSamples);
        foreach (double ms in gpuSamples)
        {
            if (ms <= 0 || ms >= 1000) continue;
            gpuTotal += ms;
            gpuCount++;
            if (gpuCount > 120) { gpuTotal *= 0.5; gpuCount /= 2; }
        }
    }

    public string DescribeCost() =>
        Physical ? $"CPU {PrepareMs:0.00} ms (last prepare); GPU sky pass {GpuMs:0.00} ms/frame (SkyX per pixel)"
                 : $"simple sky: GPU {GpuMs:0.00} ms/frame";

    /// <summary>Mean GPU time of a sky pass over the last hundred or so (a running mean, halved now and then).</summary>
    public double GpuMs => gpuCount == 0 ? 0 : gpuTotal / gpuCount;

    public void Dispose()
    {
        if (Active == this) Active = null;
        foreach (var t in new[] { starsTexture, moonTexture, cloudsTexture, cloudsNormalTexture, cloudsTileTexture, irradianceCube, specularCube, ambientMap }) t?.Dispose();
        simple.Dispose();
        sky.Dispose();
    }
}

/// <summary>The handles of the sky colour uniforms of <see cref="SkyRenderer.SkyFunctions"/> in a native program, resolved once.</summary>
public readonly record struct SkyColourHandles(UniformHandle Sun, UniformHandle Zenith, UniformHandle Horizon, UniformHandle SunColour, UniformHandle Twilight)
{
    public static SkyColourHandles Resolve(LegacyProgram p) =>
        new(p.Uniform("uSkySun"), p.Uniform("uSkyZenith"), p.Uniform("uSkyHorizon"), p.Uniform("uSkySunColour"), p.Uniform("uSkyTwilight"));

    /// <summary>What the GL <c>SetUniforms</c> set on a program; the atmosphere's uniforms come from <see cref="LegacyProgram.ApplyGlobals"/>.</summary>
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
/// The pipelines of one native program's draws (docs/renderer-native.md 7.5): remembers the last two segment states (the scene's pass and the
/// reflection's multisampled one alternate) and the vertex export they were made for, so a draw compares a few fields instead of building a
/// pipeline description.
/// </summary>
internal sealed class NativeSegment(GpuContext gpu, LegacyProgram program, Silk.NET.Vulkan.PrimitiveTopology topology, string label)
{
    readonly record struct Key(AttachmentFormats Formats, BlendState Blend, Silk.NET.Vulkan.ColorComponentFlags Mask, Silk.NET.Vulkan.PolygonMode Polygon,
        bool AlphaToCoverage, bool DepthClamp);

    Key key0, key1;
    GraphicsPipeline? pipeline0, pipeline1;
    VertexArrayBindings? source0, source1;

    /// <summary>The pipeline for <paramref name="state"/> into <paramref name="formats"/>, with the vertex inputs of <paramref name="vertices"/> (null: none).</summary>
    public GraphicsPipeline Get(DrawState state, AttachmentFormats formats, VertexArrayBindings? vertices)
    {
        var key = new Key(formats, state.Blend, state.ColourMask, state.Polygon, state.AlphaToCoverage, state.DepthClamp);
        if (pipeline0 is not null && key == key0 && ReferenceEquals(vertices, source0)) return pipeline0;
        if (pipeline1 is not null && key == key1 && ReferenceEquals(vertices, source1)) return pipeline1;
        var made = gpu.Pipelines.Get(state.Pipeline(program.Program, program.VertexLayout(vertices is null ? default : vertices.Attributes), topology, formats, label));
        (key1, pipeline1, source1) = (key0, pipeline0, source0);
        (key0, pipeline0, source0) = (key, made, vertices);
        return made;
    }
}

/// <summary>A native sky program with its handles resolved at load.</summary>
sealed class SkyProg : IDisposable
{
    public readonly LegacyProgram P;
    public readonly NativeSegment Segment;
    public readonly UniformHandle InverseViewProjection, Extra, MoonDir, MoonRight, MoonUp, CloudLight, CloudSun, CloudWind, Has;
    public readonly SkyColourHandles Colours;
    public readonly SamplerSlot Stars, Moon, Clouds, CloudsNormal, CloudsTile;
    public readonly Meitou.Rendering.Gpu.Shaders.SamplerInfo? StarsInfo, MoonInfo, CloudsInfo;

    public SkyProg(GpuContext gpu, string vertex, string fragment, string name)
    {
        P = LegacyProgram.Create(gpu, vertex, fragment, name);
        Segment = new NativeSegment(gpu, P, Silk.NET.Vulkan.PrimitiveTopology.TriangleList, name);
        InverseViewProjection = P.Uniform("uInverseViewProjection");
        Colours = SkyColourHandles.Resolve(P);
        Extra = P.Uniform("uSkyExtra");
        MoonDir = P.Uniform("uMoonDir");
        MoonRight = P.Uniform("uMoonRight");
        MoonUp = P.Uniform("uMoonUp");
        CloudLight = P.Uniform("uCloudLight");
        CloudSun = P.Uniform("uCloudSun");
        CloudWind = P.Uniform("uCloudWind");
        Has = P.Uniform("uHas");
        Stars = P.Sampler("uStars");
        Moon = P.Sampler("uMoon");
        Clouds = P.Sampler("uClouds");
        CloudsNormal = P.Sampler("uCloudsNormal");
        CloudsTile = P.Sampler("uCloudsTile");
        if (Stars.IsValid) StarsInfo = P.SamplerInfo(Stars);
        if (Moon.IsValid) MoonInfo = P.SamplerInfo(Moon);
        if (Clouds.IsValid) CloudsInfo = P.SamplerInfo(Clouds);
    }

    public void Dispose() => P.Dispose();
}

/// <summary>
/// (Phase 8 stage 2.) A native texture with the sampler state of the GL texture it replaces: <see cref="SamplerDesc.FromGl"/> with the GL
/// parameters and, on a mipmapped filter, the upscaler's LOD bias (<see cref="GpuContext.LodBias"/>), as VkGl's <c>SamplerFor</c> made it; the
/// view covers the levels VkGl's covered. For <see cref="LegacyProgram.Bind"/> and the frame globals (no bindless entry of its own).
/// Render thread only.
/// </summary>
internal sealed class SampledImage : IDisposable
{
    readonly GpuContext ctx;
    readonly TextureMinFilter min;
    readonly TextureMagFilter mag;
    readonly TextureWrapMode wrap, wrapR;
    float cachedBias = float.NaN;
    SampledTexture cached;

    SampledImage(GpuContext ctx, Texture texture, TextureMinFilter min, TextureMagFilter mag, TextureWrapMode wrap, TextureWrapMode wrapR)
    {
        this.ctx = ctx;
        Texture = texture;
        (this.min, this.mag, this.wrap, this.wrapR) = (min, mag, wrap, wrapR);
    }

    public Texture Texture { get; }

    /// <summary>The sampler and view a draw samples it with now (what VkGl's <c>Sampled</c> gave for the GL texture).</summary>
    public SampledTexture Sampled()
    {
        float bias = ctx.LodBias;
        if (!(bias == cachedBias))
        {
            var sampler = ctx.Samplers.Get(SamplerDesc.FromGl(min, mag, wrap, wrap, wrapR, false, DepthFunction.Lequal, false, 1, false, bias));
            (cached, cachedBias) = (new SampledTexture(sampler, Texture.View(), Texture.Image), bias);
        }
        return cached;
    }

    static Silk.NET.Vulkan.Rect2D Rect(int width, int height) => new(new(0, 0), new((uint)width, (uint)height));

    /// <summary>
    /// What <c>WorldGl.Texture2D</c> made: RGBA8 from top-first rows, with <paramref name="mipmaps"/> the whole chain made on the GPU as GL's
    /// GenerateMipmap made it in VkGl (<see cref="CommandList.GenerateMips"/>) and trilinear filtering, else one level, linear; repeating or
    /// clamped to the edge (S and T; R stays GL's default, repeat).
    /// </summary>
    public static SampledImage Rgba8(GpuContext ctx, RgbaImage image, bool repeat, bool mipmaps, string name) =>
        Rgba8(ctx, image.Width, image.Height, image.Pixels, repeat, mipmaps, name);

    /// <inheritdoc cref="Rgba8(GpuContext, RgbaImage, bool, bool, string)"/>
    public static SampledImage Rgba8(GpuContext ctx, int width, int height, byte[] rgba, bool repeat, bool mipmaps, string name)
    {
        int levels = mipmaps ? 1 + (int)Math.Floor(Math.Log2(Math.Max(Math.Max(width, height), 1))) : 1;
        using var batch = ctx.Uploads.Begin();
        var t = batch.Create(new TextureDesc(Silk.NET.Vulkan.Format.R8G8B8A8Unorm, width, height, levels,
            Use: TextureUse.Sampled | TextureUse.TransferDst | TextureUse.TransferSrc, Name: name));
        batch.Write(t, 0, 0, Rect(width, height), rgba.AsSpan(0, width * height * 4));
        if (mipmaps) batch.Commands.GenerateMips(t);
        return new SampledImage(ctx, t, mipmaps ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear, TextureMagFilter.Linear,
            repeat ? TextureWrapMode.Repeat : TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat);
    }

    /// <summary>RGBA32F, one level, linear, clamped to the edge: the GL texture <c>WaterRenderer</c> made for its biome parameter maps.</summary>
    public static SampledImage Rgba32F(GpuContext ctx, Vector4[] data, int width, int height, string name)
    {
        using var batch = ctx.Uploads.Begin();
        var t = batch.Create(new TextureDesc(Silk.NET.Vulkan.Format.R32G32B32A32Sfloat, width, height, Name: name));
        batch.Write(t, 0, 0, Rect(width, height), System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan(0, width * height)));
        return new SampledImage(ctx, t, TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat);
    }

    /// <summary>
    /// A cube map from a DDS file with all its mips (RGBA8), faces in the DDS order (+X, −X, +Y, −Y, +Z, −Z) as layers 0..5 (Vulkan's and GL's
    /// face order); trilinear, clamped to the edge on S, T and R (Vulkan's cube sampling is always seamless, as GL's was with
    /// <c>TEXTURE_CUBE_MAP_SEAMLESS</c>). The GL version's <c>MAX_LEVEL</c> was the file's last level, so its view had the file's levels too.
    /// </summary>
    public static SampledImage Cube(GpuContext ctx, DdsFile dds, string name)
    {
        if (!dds.IsCubemap || dds.ImageCount < 6) throw new InvalidOperationException($"not a cube map: {dds}");
        var first = DdsDecoder.Decode(dds, 0, 0);
        using var batch = ctx.Uploads.Begin();
        var t = batch.Create(new TextureDesc(Silk.NET.Vulkan.Format.R8G8B8A8Unorm, first.Width, first.Height, dds.MipCount, Kind: TextureKind.Cube, Name: name));
        for (int face = 0; face < 6; face++)
            for (int level = 0; level < dds.MipCount; level++)
            {
                var img = face == 0 && level == 0 ? first : DdsDecoder.Decode(dds, face, level);
                batch.Write(t, level, face, Rect(img.Width, img.Height), img.Pixels);
            }
        return new SampledImage(ctx, t, TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge);
    }

    public void Dispose() => Texture.Dispose();
}

/// <summary>
/// (Phase 8 stage 2.) The shadow blocks a world program reads when there is no <see cref="ShadowPass"/> (<c>--no-shadows</c>), as frame
/// globals: zeros (shadows off), what <c>ShadowShaders.Bind</c> put on the GL binding points through the unused GL program the terrain
/// linked for it. Published only where nothing is yet; a <see cref="ShadowPass"/> publishes its own over them whenever it is made. The shadow
/// textures stay unpublished: readers get the stand-in, as an empty GL unit gave. Called by the sky, which every world view has.
/// </summary>
static class ShadowsOffGlobals
{
    public static void Publish(GpuContext gpu)
    {
        var g = gpu.Globals;
        foreach (var (name, bytes) in new[] { (ShadowShaders.ReceiverBlock, ShadowPass.ReceiverBytes), (ShadowShaders.CasterBlock, 16), (MeitouShadowShaders.Block, MeitouShadowShaders.BlockBytes) })
            if (g.Block(name) is null) g.Publish(name, new FrameBlock(gpu, bytes).Binding);
    }
}
