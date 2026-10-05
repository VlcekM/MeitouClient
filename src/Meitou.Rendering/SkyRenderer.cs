using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Textures;
using Meitou.Data.World;
using Silk.NET.OpenGL;

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
    static readonly string SkyFragment = "#version 330 core\n" + AtmosphereShaders.Functions + """

        in vec2 vNdc;
        out vec4 fragColour;
        uniform mat4 uInverseViewProjection;
        uniform vec4 uSkyExtra;        // x: unused, y: cloud coverage, z: stars' turn (radians), w: moon radius (radians)
        uniform vec3 uMoonDir, uMoonRight, uMoonUp;
        uniform vec4 uCloudLight;      // rgb: the clouds' zenith light (a stand-in), a: the layer's darkness
        uniform sampler2D uStars, uMoon, uClouds;
        uniform vec3 uHas;             // stars, moon, clouds textures present
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
            // Moon: SkyX_Moon.png on a disc opposite the sun (always full; SkyX_Moon.hlsl saturates its colour and blends by alpha).
            float md = dot(dir, uMoonDir);
            if (uHas.y > 0.5 && md > 0.0)
            {
                vec2 p = vec2(dot(dir, uMoonRight), dot(dir, uMoonUp)) / md / uSkyExtra.w;
                vec4 m = texture(uMoon, vec2(p.x, -p.y) * 0.195 + 0.5);
                if (abs(p.x) < 2.6 && abs(p.y) < 2.6)
                    col = mix(col, clamp(m.rgb, 0.0, 1.0), m.a * night * aboveHorizon * smoothstep(0.0, 0.1, uMoonDir.y));
            }
            // Clouds: a flat layer of SkyX's cloud texture (SkyX_Clouds.hlsl projects the view ray on a plane the same way); its colour
            // follows the shader's form saturate(zenith + sun (1 − 0.1 density)) (1 − density · darkness) · √exposure with a stand-in zenith light.
            if (uHas.z > 0.5 && uSkyExtra.y > 0.0 && dir.y > 0.01)
            {
                vec2 uv = dir.xz / dir.y * 0.3 + 0.37;
                float t = texture(uClouds, uv).r;
                float thr = mix(0.85, 0.15, clamp(uSkyExtra.y, 0.0, 1.0));
                float density = smoothstep(thr, thr + 0.3, t);
                float fade = smoothstep(0.01, 0.2, dir.y);
                vec3 cloud = clamp(uCloudLight.rgb + uAtmoSunLight * (1.0 - 0.1 * density), 0.0, 1.0) * (1.0 - density * uCloudLight.a) * sqrt(SKYX_EXPOSURE);
                col = mix(col, cloud, density * fade * 0.9);
            }
            fragColour = vec4(col, 1.0);
        }
        """;

    readonly IGl gl;
    readonly uint simpleProgram, skyProgram, vao;
    readonly Dictionary<(uint, string), int> uniforms = [];
    uint starsTexture, moonTexture, cloudsTexture, irradianceCube, specularCube, ambientMap;
    readonly GpuSpan skyTimer;

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
    public float HazeStrength { get; set; } = 0.85f;
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
    public const float AltitudeBandStart = 6000, AltitudeBandEnd = 22000;
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
    /// <summary>Overrides the weather's cloud density when set.</summary>
    public float? CloudCoverage { get; set; }
    /// <summary>CONSTANTS <c>exposure min</c> / <c>exposure max</c> / <c>night darkness</c>: the composite's luminance band.</summary>
    public ExposureConstants Exposure { get; set; } = ExposureConstants.Default;
    /// <summary>This frame's <c>MIN_LUMINANCE</c> / <c>MAX_LUMINANCE</c> (game mode); the composite scales by 0.55 over the scene's mean luminance clamped to them.</summary>
    public float MinLuminance => state.MinLuminance;
    public float MaxLuminance => Exposure.Max;
    /// <summary>CPU time of the last <see cref="Prepare"/> in ms.</summary>
    public double PrepareMs { get; private set; }
    /// <summary>Which of the game's lighting textures were found (irradiance cube, specular cube, ambient map).</summary>
    public string LightingTextures => $"irradiance {(irradianceCube != 0 ? "yes" : "no")}, specularity {(specularCube != 0 ? "yes" : "no")}, ambient map {(ambientMap != 0 ? "yes" : "no")}";

    struct State
    {
        public Vector3 Sun, SunLight, LightDirection;
        public float Environment, MinLuminance, FogDistance;
        public SkyColours Colours;
        public WorldLighting Light;
        public bool Valid;
    }
    State state;
    Vector3 builtSun = new(float.NaN);
    SkyWeather? builtWeather;
    bool builtPhysical;

    public SkyRenderer(IGl gl, AssetLocator? assets = null)
    {
        this.gl = gl;
        simpleProgram = WorldGl.Program(gl, Vertex, SimpleFragment);
        skyProgram = WorldGl.Program(gl, Vertex, SkyFragment);
        vao = gl.GenVertexArray();
        skyTimer = new GpuSpan(gl);
        if (assets is not null)
        {
            LoadTextures(assets);
            Console.WriteLine($"sky       textures: starfield {(starsTexture != 0 ? "yes" : "no")}, moon {(moonTexture != 0 ? "yes" : "no")}, clouds {(cloudsTexture != 0 ? "yes" : "no")}");
        }
        Active = this;
    }

    void LoadTextures(AssetLocator assets)
    {
        try
        {
            if (assets.Find("SkyX_Starfield.dds") is { } stars)
            {
                var dds = DdsReader.ReadFile(stars);
                var img = DdsDecoder.Decode(dds, 0, Math.Min(2, dds.MipCount - 1));   // 1024²: one level of the 4096² file
                starsTexture = WorldGl.Texture2D(gl, img.Width, img.Height, img.Pixels, repeat: false);
            }
            if (assets.Find("SkyX_Moon.png") is { } moon)
            {
                var img = TextureLoader.LoadFile(moon, allMips: false).Levels[0];
                moonTexture = WorldGl.Texture2D(gl, img.Width, img.Height, img.Pixels, repeat: false);
            }
            if (assets.Find("Clouds.dds") is { } clouds)
            {
                var img = TextureLoader.LoadFile(clouds, allMips: false).Levels[0];
                cloudsTexture = WorldGl.Texture2D(gl, img.Width, img.Height, img.Pixels, repeat: true);
            }
            if (assets.Find("mp_irradiance.dds") is { } irradiance) irradianceCube = Cube(DdsReader.ReadFile(irradiance));
            if (assets.Find("mp_specularity.dds") is { } specularity) specularCube = Cube(DdsReader.ReadFile(specularity));
        }
        catch (Exception e) when (e is DdsFormatException or IOException or InvalidOperationException)
        {
            Console.WriteLine($"warning   sky textures: {e.Message}");
        }
    }

    /// <summary>A cube map with all its mips, faces in the DDS order (+X, −X, +Y, −Y, +Z, −Z) on GL's faces of the same names.</summary>
    uint Cube(DdsFile dds)
    {
        if (!dds.IsCubemap || dds.ImageCount < 6) throw new InvalidOperationException($"not a cube map: {dds}");
        uint t = gl.GenTexture();
        gl.BindTexture(TextureTarget.TextureCubeMap, t);
        for (int face = 0; face < 6; face++)
            for (int level = 0; level < dds.MipCount; level++)
            {
                var img = DdsDecoder.Decode(dds, face, level);
                fixed (byte* p = img.Pixels)
                    gl.TexImage2D(TextureTarget.TextureCubeMapPositiveX + face, level, InternalFormat.Rgba8, (uint)img.Width, (uint)img.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            }
        gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMaxLevel, dds.MipCount - 1);
        foreach (var w in new[] { TextureParameterName.TextureWrapS, TextureParameterName.TextureWrapT, TextureParameterName.TextureWrapR })
            gl.TexParameter(TextureTarget.TextureCubeMap, w, (int)TextureWrapMode.ClampToEdge);
        gl.Enable(EnableCap.TextureCubeMapSeamless);
        gl.BindTexture(TextureTarget.TextureCubeMap, 0);
        return t;
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
            ambientMap = WorldGl.Texture2D(gl, map.Width, map.Height, map.Pixels, repeat: false, mipmaps: false);
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
            state = new State { Sun = sun, Colours = simple, Light = light, FogDistance = fogDistance, Valid = true, MinLuminance = Exposure.Min };
            builtPhysical = false;
            PrepareMs = watch.Elapsed.TotalMilliseconds;
            return (simple, light);
        }
        // SkyX's sky does not depend on the eye's height (its camera is fixed); the light only changes with the sun and the weather.
        if (!builtPhysical || !ReferenceEquals(builtWeather, Weather) || (sun - builtSun).LengthSquared() > 1e-10f)
        {
            builtSun = sun;
            builtWeather = Weather;
            builtPhysical = true;
            var sunLight = KenshiLighting.SunLight(sun);
            var lightDir = KenshiLighting.LightDirection(sun);
            float env = KenshiLighting.EnvironmentFactor(lightDir);
            var tint = Weather.SkyColourMultiplier;
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

    /// <summary>Texture units of the atmosphere's samplers: the top three of the combined units, out of the way of every scene shader.</summary>
    static int irradianceUnit = -1, specularUnit, ambientUnit;

    /// <summary>
    /// Points the atmosphere's sampler uniforms of a freshly linked program at their own units (a cube sampler left on unit 0 would clash with
    /// the 2D samplers there). Called by every program builder that may include <see cref="AtmosphereShaders.Functions"/>.
    /// </summary>
    public static void AssignSamplerUnits(IGl gl, uint program)
    {
        if (irradianceUnit < 0)
        {
            gl.GetInteger(GetPName.MaxCombinedTextureImageUnits, out int combined);
            irradianceUnit = combined - 1; specularUnit = combined - 2; ambientUnit = combined - 3;
        }
        gl.UseProgram(program);
        int a = gl.GetUniformLocation(program, "uAtmoIrradiance"), b = gl.GetUniformLocation(program, "uAtmoSpecular"), c = gl.GetUniformLocation(program, "uAtmoAmbientMap");
        if (a >= 0) gl.Uniform1(a, irradianceUnit);
        if (b >= 0) gl.Uniform1(b, specularUnit);
        if (c >= 0) gl.Uniform1(c, ambientUnit);
        gl.UseProgram(0);
    }

    /// <summary>Sets the atmosphere's uniforms (and binds its textures) on a program that includes <see cref="AtmosphereShaders.Functions"/>.</summary>
    public void Apply(uint program)
    {
        if (!state.Valid) return;
        var s = state;
        gl.UseProgram(program);
        var tau = SkyAtmosphere.RayleighZenithDepth;
        gl.Uniform4(U(program, "uAtmoTau"), tau.X, tau.Y, tau.Z, SkyAtmosphere.MieZenithDepth);
        gl.Uniform4(U(program, "uAtmoParams"), Physical ? 1f : 0f, MathF.Max(s.FogDistance, 1), ScaleHeightUnits, 0);
        gl.Uniform4(U(program, "uAtmoSun"), s.Sun.X, s.Sun.Y, s.Sun.Z, 0);
        gl.Uniform4(U(program, "uAtmoLight"), s.LightDirection.X, s.LightDirection.Y, s.LightDirection.Z, s.Environment);
        gl.Uniform3(U(program, "uAtmoSunLight"), s.SunLight.X, s.SunLight.Y, s.SunLight.Z);
        var tint = Weather.SkyColourMultiplier;
        gl.Uniform3(U(program, "uAtmoTint"), tint.X, tint.Y, tint.Z);
        var w = Weather;
        gl.Uniform4(U(program, "uAtmoFog"), w.FogMin, w.FogMax, w.FogEnabled ? 1f : 0f, 0);
        // The weather fog's colour times sunColour.w, the daylight scale (the game's global fog term).
        var fog = w.FogColour * (Physical ? KenshiLighting.Daylight(s.Sun.Y) : 1f);
        gl.Uniform3(U(program, "uAtmoFogColour"), fog.X, fog.Y, fog.Z);
        var hc = s.Colours.Horizon;
        gl.Uniform4(U(program, "uAtmoSimple"), hc.X, hc.Y, hc.Z, MathF.Max(s.FogDistance, 1));
        float hazeStart = HazeDistance * Meitou.Data.World.KenshiHaze.StartFraction;
        float hazeEnd = MathF.Min(HazeDistance, HazeDistance * Meitou.Data.World.KenshiHaze.EndFraction);
        // The weather fog is complete at a distance between `fog distance min` and `max` by the wind; the viewer has no wind and takes max.
        gl.Uniform4(U(program, "uAtmoHaze"), KenshiHaze ? 1f : 0f, hazeStart, hazeEnd, w.FogEnabled && w.FogMax > 1 ? 1f / w.FogMax : 0f);
        var (cloud, pull, _) = HorizonClouds(s);
        gl.Uniform4(U(program, "uAtmoHazeCloud"), cloud.X, cloud.Y, cloud.Z, pull);
        gl.Uniform4(U(program, "uAtmoAltitude"), KenshiHaze ? AltitudeWeight : 1f, MathF.Max(HazeStrength, 0), AltitudeWeight, 0);
        gl.Uniform4(U(program, "uAtmoMaps"), irradianceCube != 0 ? 1f : 0f, specularCube != 0 ? 1f : 0f, ambientMap != 0 ? 1f : 0f, AmbientMap.HalfWorld);
        if (irradianceUnit >= 0)
        {
            gl.ActiveTexture(TextureUnit.Texture0 + irradianceUnit);
            gl.BindTexture(TextureTarget.TextureCubeMap, irradianceCube);
            gl.ActiveTexture(TextureUnit.Texture0 + specularUnit);
            gl.BindTexture(TextureTarget.TextureCubeMap, specularCube);
            gl.ActiveTexture(TextureUnit.Texture0 + ambientUnit);
            gl.BindTexture(TextureTarget.Texture2D, ambientMap);
        }
        gl.ActiveTexture(TextureUnit.Texture0);
    }

    /// <summary>
    /// <c>horizonClouds</c>: the pull is the game's (cloud cover); the colour is built the game's way,
    /// <c>saturate(sun (1 − 0.1 (offset + 0.2) · 3) + horizon · sun.g) (1 − darkness) √exposure</c>, from the viewer's sun colour and horizon sky
    /// (the game's own inputs, getColorAt's result for the cloud layer and a floor colour, are partly Unknown). Also the clouds' darkness.
    /// </summary>
    (Vector3 Colour, float Pull, float Darkness) HorizonClouds(State s)
    {
        float clouds = Math.Clamp(CloudCoverage ?? Weather.CloudDensity, 0, 1);
        float offset = 1.4f * clouds - 0.8f, darkness = MathF.Pow(Math.Clamp(clouds - 0.5f, 0, 1), 0.3f);
        var sunColour = KenshiLighting.SunColour(s.Sun);
        var colour = Vector3.Clamp(sunColour * (1 - 0.3f * (offset + 0.2f)) + s.Colours.Horizon * sunColour.Y, Vector3.Zero, Vector3.One) * ((1 - darkness) * MathF.Sqrt(SkyAtmosphere.Exposure));
        return (colour, Meitou.Data.World.KenshiHaze.CloudPull(clouds), darkness);
    }

    // ---- drawing ---------------------------------------------------------------------------------------

    public void Draw(Matrix4x4 viewProjection, SkyColours colours)
    {
        if (!Matrix4x4.Invert(viewProjection, out var inverse)) return;
        skyTimer.Begin();
        uint program = Physical && state.Valid ? skyProgram : simpleProgram;
        gl.UseProgram(program);
        WorldGl.Matrix(gl, U(program, "uInverseViewProjection"), inverse);
        SetUniforms(program, colours);
        if (program == skyProgram) SetSkyUniforms();
        gl.Disable(EnableCap.DepthTest);
        gl.DepthMask(false);
        gl.BindVertexArray(vao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        gl.BindVertexArray(0);
        gl.DepthMask(true);
        gl.Enable(EnableCap.DepthTest);
        gl.ActiveTexture(TextureUnit.Texture0);
        skyTimer.End();
    }

    void SetSkyUniforms()
    {
        var s = state;
        float coverage = Math.Clamp(CloudCoverage ?? Weather.CloudDensity, 0, 1.5f);
        // The stars turn with the sun's half-turn (a = phase · π); the moon stands opposite the sun.
        float turn = MathF.Atan2(s.Sun.Z, s.Sun.X);
        var moon = Vector3.Normalize(-s.Sun);
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, moon));
        var up = Vector3.Cross(moon, right);
        const float moonRadius = 0.016f;
        gl.Uniform4(U(skyProgram, "uSkyExtra"), 0, coverage, turn, moonRadius);
        gl.Uniform3(U(skyProgram, "uMoonDir"), moon.X, moon.Y, moon.Z);
        gl.Uniform3(U(skyProgram, "uMoonRight"), right.X, right.Y, right.Z);
        gl.Uniform3(U(skyProgram, "uMoonUp"), up.X, up.Y, up.Z);
        var (_, _, darkness) = HorizonClouds(s);
        var zenith = s.Colours.Zenith;
        gl.Uniform4(U(skyProgram, "uCloudLight"), zenith.X, zenith.Y, zenith.Z, MathF.Max(darkness, 0.35f));
        gl.Uniform3(U(skyProgram, "uHas"), starsTexture != 0 ? 1f : 0f, moonTexture != 0 ? 1f : 0f, cloudsTexture != 0 ? 1f : 0f);
        (string, uint)[] textures = [("uStars", starsTexture), ("uMoon", moonTexture), ("uClouds", cloudsTexture)];
        for (int i = 0; i < textures.Length; i++)
        {
            gl.ActiveTexture(TextureUnit.Texture0 + 1 + i);
            gl.BindTexture(TextureTarget.Texture2D, textures[i].Item2);
            gl.Uniform1(U(skyProgram, textures[i].Item1), 1 + i);
        }
        gl.ActiveTexture(TextureUnit.Texture0);
        Apply(skyProgram);
    }

    /// <summary>Sets the legacy sky uniforms (the simple model's, and the zenith colour the water shader reads) and the atmosphere's.</summary>
    public void SetUniforms(uint target, SkyColours sky)
    {
        gl.UseProgram(target);
        gl.Uniform3(U(target, "uSkySun"), sky.Sun.X, sky.Sun.Y, sky.Sun.Z);
        gl.Uniform3(U(target, "uSkyZenith"), sky.Zenith.X, sky.Zenith.Y, sky.Zenith.Z);
        gl.Uniform3(U(target, "uSkyHorizon"), sky.Horizon.X, sky.Horizon.Y, sky.Horizon.Z);
        gl.Uniform3(U(target, "uSkySunColour"), sky.SunColour.X, sky.SunColour.Y, sky.SunColour.Z);
        gl.Uniform1(U(target, "uSkyTwilight"), sky.Twilight);
        Apply(target);
    }

    /// <summary>Times <paramref name="count"/> sky passes back to back (ms each; no table any more); for the screenshot mode's cost report.</summary>
    public (double SkyPassMs, double TableMs) Benchmark(Matrix4x4 viewProjection, int count = 100)
    {
        var colours = state.Colours;
        gl.Finish();
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < count; i++) Draw(viewProjection, colours);
        gl.Finish();
        return (watch.Elapsed.TotalMilliseconds / count, 0);
    }

    /// <summary>Collects finished GPU timings (without waiting unless asked).</summary>
    public void Poll(bool wait = false) => skyTimer.Poll(wait);

    public string DescribeCost() =>
        Physical ? $"CPU {PrepareMs:0.00} ms (last prepare); GPU sky pass {skyTimer.AverageMs:0.00} ms/frame (SkyX per pixel)"
                 : $"simple sky: GPU {skyTimer.AverageMs:0.00} ms/frame";

    public double GpuMs => skyTimer.AverageMs;

    int U(uint p, string name)
    {
        if (!uniforms.TryGetValue((p, name), out int location)) uniforms[(p, name)] = location = gl.GetUniformLocation(p, name);
        return location;
    }

    public void Dispose()
    {
        if (Active == this) Active = null;
        skyTimer.Dispose();
        gl.DeleteVertexArray(vao);
        foreach (var t in new[] { starsTexture, moonTexture, cloudsTexture, irradianceCube, specularCube, ambientMap }) if (t != 0) gl.DeleteTexture(t);
        foreach (var p in new[] { simpleProgram, skyProgram }) gl.DeleteProgram(p);
    }

    /// <summary>GPU time of a span of commands, by timestamp queries (ring of a few, read without stalling).</summary>
    sealed class GpuSpan : IDisposable
    {
        readonly IGl gl;
        readonly uint[] begin = new uint[4], end = new uint[4];
        readonly bool[] pending = new bool[4];
        int index;
        double total;
        int samples;
        public GpuSpan(IGl gl)
        {
            this.gl = gl;
            for (int i = 0; i < 4; i++) { begin[i] = gl.GenQuery(); end[i] = gl.GenQuery(); }
        }
        public double AverageMs => samples == 0 ? 0 : total / samples;
        public void Begin() { if (!pending[index]) gl.QueryCounter(begin[index], QueryCounterTarget.Timestamp); }
        public void End()
        {
            if (pending[index]) { index = (index + 1) % 4; return; }
            gl.QueryCounter(end[index], QueryCounterTarget.Timestamp);
            pending[index] = true;
            index = (index + 1) % 4;
        }
        public void Poll(bool wait)
        {
            for (int i = 0; i < 4; i++)
            {
                if (!pending[i]) continue;
                gl.GetQueryObject(end[i], QueryObjectParameterName.ResultAvailable, out int available);
                if (available == 0 && !wait) continue;
                gl.GetQueryObject(begin[i], QueryObjectParameterName.Result, out ulong b);
                gl.GetQueryObject(end[i], QueryObjectParameterName.Result, out ulong e);
                pending[i] = false;
                if (e > b && e - b < 1_000_000_000UL) { total += (e - b) / 1e6; samples++; if (samples > 120) { total *= 0.5; samples /= 2; } }
            }
        }
        public void Dispose() { for (int i = 0; i < 4; i++) { gl.DeleteQuery(begin[i]); gl.DeleteQuery(end[i]); } }
    }
}
