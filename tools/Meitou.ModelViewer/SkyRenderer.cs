using System.Diagnostics;
using System.Numerics;
using Meitou.Data.Textures;
using Meitou.Data.World;
using Silk.NET.OpenGL;

namespace Meitou.ModelViewer;

/// <summary>
/// The simple colour model the world view's sky had before the atmosphere (<c>--simple-sky</c>, key B): zenith and horizon colours
/// from the sun's height, a warm band at sunrise and sunset, the sun disc. Also the record that carries the sky's colours to
/// the water shader. The physical sky (<see cref="SkyRenderer"/>, docs/formats/sky.md) fills it from its own numbers.
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
/// The world view's sky and atmosphere (docs/formats/sky.md). Physical mode: O'Neil's single scattering with SkyX's constants
/// (<see cref="AtmosphereModel"/>), kept as a transmittance table (built once) and a sky-view table (per frame, only when the sun or
/// the eye's height moved), drawn as a full-screen pass with the sun disc, stars, moon and optional clouds. The same tables give the
/// aerial perspective every world shader applies (<see cref="AtmosphereShaders.Functions"/>) and, through
/// <see cref="AtmosphereModel"/>, the sun and ambient light. Simple mode (<see cref="Physical"/> false) is the old colour model.
/// A shader that wants the atmosphere includes <see cref="AtmosphereShaders.Functions"/> and gets its uniforms from <see cref="Apply"/>.
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
        vec3 skyColour(vec3 dir, bool disc) { return uAtmoParams.x > 0.5 ? atmoSkyEnc(dir) : skySimple(dir, disc); }
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

    static readonly string SkyFragment = "#version 330 core\n" + AtmosphereShaders.Functions + """

        in vec2 vNdc;
        out vec4 fragColour;
        uniform mat4 uInverseViewProjection;
        uniform vec4 uSkyExtra;        // x: sun disc radiance, y: cloud coverage, z: stars' turn (radians), w: moon radius (radians)
        uniform vec3 uMoonDir, uMoonRight, uMoonUp;
        uniform sampler2D uStars, uMoon, uClouds;
        uniform vec3 uHas;             // stars, moon, clouds textures present
        void main()
        {
            vec4 a = uInverseViewProjection * vec4(vNdc, 0.0, 1.0);
            vec4 b = uInverseViewProjection * vec4(vNdc, 1.0, 1.0);
            vec3 dir = normalize(b.xyz / b.w - a.xyz / a.w);
            float night;
            vec3 col = atmoSkyEnc(dir, night);
            float aboveHorizon = smoothstep(-0.02, 0.06, dir.y);
            // Weather fog hides the sky near the horizon too (the terrain beyond its far distance is plain fog colour).
            if (uAtmoFog.z > 0.5) col = mix(col, uAtmoFogColour, 1.0 - smoothstep(0.0, 0.45, dir.y));

            // Stars: SkyX_Starfield.dds laid over the upper hemisphere (stereographic from the zenith), turning with the night.
            if (uHas.x > 0.5 && night > 0.0 && dir.y > 0.0)
            {
                float cs = cos(uSkyExtra.z), sn = sin(uSkyExtra.z);
                vec2 r = vec2(cs * dir.x - sn * dir.z, sn * dir.x + cs * dir.z);
                vec3 stars = texture(uStars, r / (1.0 + dir.y) * 0.5 + 0.5).rgb;
                col += night * aboveHorizon * min(stars, vec3(1.0)) * (0.35 + clamp(-uAtmoSun.y * 0.45, 0.0, 1.0)) * 2.2;
            }
            // Moon: SkyX_Moon.png on a disc opposite the sun (always full).
            float md = dot(dir, uMoonDir);
            if (uHas.y > 0.5 && md > 0.0)
            {
                vec2 p = vec2(dot(dir, uMoonRight), dot(dir, uMoonUp)) / md / uSkyExtra.w;
                vec4 m = texture(uMoon, vec2(p.x, -p.y) * 0.195 + 0.5);
                if (abs(p.x) < 2.6 && abs(p.y) < 2.6)
                    col = mix(col, m.rgb * vec3(1.0, 0.98, 0.92) * 1.15, m.a * night * aboveHorizon * smoothstep(0.0, 0.1, uMoonDir.y));
            }
            // Sun disc with limb darkening, dimmed by the air the light crosses.
            float ang = acos(clamp(dot(dir, uAtmoSun.xyz), -1.0, 1.0));
            const float SUN_RADIUS = 0.0062;
            if (ang < SUN_RADIUS * 1.2)
            {
                float mu = sqrt(max(1.0 - (ang / SUN_RADIUS) * (ang / SUN_RADIUS), 0.0));
                float limb = 1.0 - 0.6 * (1.0 - mu);
                float edge = 1.0 - smoothstep(SUN_RADIUS * 0.92, SUN_RADIUS, ang);
                // The disc clips towards white whatever the sunlight's colour: a tinted tone-mapped disc would read as a dull orange dot.
                vec3 tint = uAtmoSunLight / max(max(uAtmoSunLight.r, uAtmoSunLight.g), max(uAtmoSunLight.b, 1e-4));
                col += mix(tint, vec3(1.0), 0.65) * max(max(uAtmoSunLight.r, uAtmoSunLight.g), uAtmoSunLight.b) * uSkyExtra.x * limb * edge * aboveHorizon;
            }
            // Clouds: a flat layer of SkyX's cloud texture (SkyX_Clouds.hlsl projects the view ray on a plane the same way).
            if (uHas.z > 0.5 && uSkyExtra.y > 0.0 && dir.y > 0.01)
            {
                vec2 uv = dir.xz / dir.y * 0.3 + 0.37;
                float t = texture(uClouds, uv).r;
                float thr = mix(0.85, 0.15, clamp(uSkyExtra.y, 0.0, 1.0));
                float density = smoothstep(thr, thr + 0.3, t);
                float fade = smoothstep(0.01, 0.2, dir.y);
                float lit = 0.5 + 0.5 * clamp(dot(vec3(0.0, 1.0, 0.0), uAtmoSun.xyz) * 1.5 + 0.5, 0.0, 1.0);
                vec3 cloud = (uAtmoAmbient * 1.5 + uAtmoSunLight * 0.5) * mix(1.0, 0.55, density) * lit;
                col = mix(col, cloud, density * fade * 0.9);
            }
            fragColour = vec4(col, 1.0);
        }
        """;

    readonly GL gl;
    readonly uint simpleProgram, skyProgram, transmittanceProgram, skyViewProgram, vao;
    readonly uint transmittanceTexture, skyViewTexture, lutFbo;
    readonly Dictionary<(uint, string), int> uniforms = [];
    readonly int lutUnit;
    uint starsTexture, moonTexture, cloudsTexture;
    readonly GpuSpan lutTimer, skyTimer;

    /// <summary>Physical atmosphere (default) or the old simple colour model.</summary>
    public bool Physical { get; set; } = true;
    /// <summary>Aerial perspective: Kenshi's own haze (docs/formats/sky.md "Haze") or the physical integral (default).</summary>
    public bool KenshiHaze { get; set; } = false;
    /// <summary>Kenshi's far distance D (the game: "view distance" setting 5000 x 10); its haze ramps in between 0.8 D and 0.96 D.</summary>
    public float HazeDistance { get; set; } = 50000; // WorldApp sets it to the far clip each frame, or to this:
    /// <summary>A fixed D (`--haze-distance`) instead of the far clip.</summary>
    public float? FixedHazeDistance { get; set; }
    /// <summary>Where the haze starts and ends, as fractions of D. Unknown in the game (pFogParams y, z); these are the fractions it gives Ogre's own linear fog.</summary>
    public const float HazeStart = 0.8f, HazeEnd = 0.96f;
    /// <summary>The highest eye, in scale heights, the sky tables and the sun and sky light are computed for.</summary>
    public float MaxSkyAltitude { get; set; } = 0.9f;
    public AtmosphereSettings Settings { get; }
    public AtmosphereModel Model { get; }
    public SkyWeather Weather { get; set; } = SkyWeather.Default;
    /// <summary>Overrides the weather's cloud density when set.</summary>
    public float? CloudCoverage { get; set; }
    /// <summary>CONSTANTS <c>night darkness</c>; its exact meaning is Unknown, the viewer uses it to set the night's ambient floor.</summary>
    public float NightDarkness { get; set; } = 0.35f;
    /// <summary>Sun disc radiance (display-referred; the disc is a few times brighter than the sky so a bloom can pick it up).</summary>
    public float SunDiscRadiance { get; set; } = 8;
    /// <summary>CPU time of the last <see cref="Prepare"/> in ms, and whether it rebuilt the sky-view table.</summary>
    public double PrepareMs { get; private set; }
    public bool Rebuilt { get; private set; }
    public float EyeAltitudeScaleHeights => state.Altitude;

    struct State
    {
        public Vector3 Sun, SunLight, Ambient, UpperSky;
        public float Altitude, Horizon, FogDistance;
        public SkyColours Colours;
        public WorldLighting Light;
        public bool Valid;
    }
    State state;
    Vector3 builtSun;
    float builtAltitude = -1;
    bool tablesBuilt, physicalState;
    SkyWeather? builtWeather;

    public SkyRenderer(GL gl, AssetLocator? assets = null, AtmosphereSettings? settings = null)
    {
        this.gl = gl;
        Settings = settings ?? new AtmosphereSettings();
        Model = new AtmosphereModel(Settings);
        simpleProgram = WorldGl.Program(gl, Vertex, SimpleFragment);
        skyProgram = WorldGl.Program(gl, Vertex, SkyFragment);
        transmittanceProgram = WorldGl.Program(gl, Vertex, AtmosphereShaders.TransmittanceFragment);
        skyViewProgram = WorldGl.Program(gl, Vertex, AtmosphereShaders.SkyViewFragment);
        vao = gl.GenVertexArray();
        gl.GetInteger(GetPName.MaxTextureImageUnits, out int maxUnits);
        // The scene shaders already use units up to 16 (terrain, water, reflection): the table takes a high one.
        lutUnit = Math.Min(maxUnits - 1, 24);
        transmittanceTexture = Target(AtmosphereShaders.TransmittanceWidth, AtmosphereShaders.TransmittanceHeight);
        skyViewTexture = Target(AtmosphereShaders.SkyViewWidth, AtmosphereShaders.SkyViewHeight);
        lutFbo = gl.GenFramebuffer();
        lutTimer = new GpuSpan(gl);
        skyTimer = new GpuSpan(gl);
        if (assets is not null)
        {
            LoadTextures(assets);
            Console.WriteLine($"sky       textures: starfield {(starsTexture != 0 ? "yes" : "no")}, moon {(moonTexture != 0 ? "yes" : "no")}, clouds {(cloudsTexture != 0 ? "yes" : "no")}");
        }
        Active = this;
    }

    uint Target(int width, int height)
    {
        uint t = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, t);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.HalfFloat, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        return t;
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
        }
        catch (Exception e) when (e is DdsFormatException or IOException or InvalidOperationException)
        {
            Console.WriteLine($"warning   sky textures: {e.Message}");
        }
    }

    // ---- per frame -------------------------------------------------------------------------------------

    /// <summary>
    /// Brings the atmosphere up to date for this frame (sun direction, the eye's height in world units) and returns the colours and
    /// light the world shaders use. <paramref name="fogDistance"/> is where the haze is complete.
    /// </summary>
    public (SkyColours Colours, WorldLighting Light) Prepare(Vector3 sun, float eyeHeight, float fogDistance)
    {
        var watch = Stopwatch.StartNew();
        Rebuilt = false;
        if (!Physical)
        {
            var simple = SkyColours.For(sun);
            var light = simple.Lighting(fogDistance);
            state = new State { Sun = sun, Colours = simple, Light = light, FogDistance = fogDistance, Valid = true };
            physicalState = false;
            PrepareMs = watch.Elapsed.TotalMilliseconds;
            return (simple, light);
        }
        // The sky itself is that of an eye inside the lower air: higher up the real thing goes dark towards space, which the world
        // view (a map seen from far above) does not want. The aerial perspective still integrates the true heights.
        float altitude = Math.Clamp(eyeHeight / Settings.ScaleHeightUnits, 0.0004f, MaxSkyAltitude);
        float r = SkyAtmosphere.PlanetRadius + altitude;
        // The table and the light only change with the sun and the eye's height.
        bool changed = !physicalState || !ReferenceEquals(builtWeather, Weather) || (sun - builtSun).LengthSquared() > 1e-8f || MathF.Abs(altitude - builtAltitude) > 0.005f + 0.05f * builtAltitude || !tablesBuilt;
        if (changed)
        {
            builtSun = sun;
            builtWeather = Weather;
            physicalState = true;
            builtAltitude = altitude;
            var sunLight = Model.SunLight(altitude, sun);
            var (upper, horizon, zenith) = Model.SkyAverages(altitude, sun);
            var weather = Weather;
            upper *= weather.SkyColourMultiplier;
            horizon *= weather.SkyColourMultiplier;
            zenith *= weather.SkyColourMultiplier;
            var night = new Vector3(0.18f, 0.22f, 0.36f) * (1 - NightDarkness);   // moonlight, standing in for a lit moon
            var ambientSky = upper * 0.5f + new Vector3(0.01f) + night;
            var ambientGround = upper * 0.22f + sunLight * (MathF.Max(sun.Y, 0) * 0.12f) + new Vector3(0.01f) + night * 0.6f;
            float e = sun.Y;
            float twilight = 1 - Math.Clamp((MathF.Abs(e + 0.02f)) / 0.35f, 0, 1);
            var colours = new SkyColours(sun, zenith, horizon, sunLight, twilight);
            var dir = sun.Y > 0.02f ? sun : Vector3.Normalize(new Vector3(sun.X, 0.02f, sun.Z));
            // Weather fog is lit like the ground: dim at night.
            state = new State
            {
                Sun = sun, SunLight = sunLight, Ambient = ambientSky, UpperSky = upper, Altitude = altitude,
                Horizon = -MathF.Acos(SkyAtmosphere.PlanetRadius / r), FogDistance = fogDistance, Colours = colours,
                Light = new WorldLighting(dir, sunLight, ambientSky, ambientGround, horizon, fogDistance), Valid = true,
            };
            BuildSkyView();
            Rebuilt = true;
        }
        else
        {
            // Same lighting; only the fog distance (it follows the eye's height) and the exact sun may differ.
            state.Light = state.Light with { FogDistance = fogDistance };
            state.FogDistance = fogDistance;
        }
        PrepareMs = watch.Elapsed.TotalMilliseconds;
        return (state.Colours, state.Light);
    }

    void BuildSkyView()
    {
        lutTimer.Begin();
        gl.GetInteger(GLEnum.DrawFramebufferBinding, out int previousFbo);
        int* viewport = stackalloc int[4];
        gl.GetInteger(GLEnum.Viewport, viewport);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.Blend);
        gl.BindVertexArray(vao);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, lutFbo);
        if (!tablesBuilt)
        {
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, transmittanceTexture, 0);
            gl.Viewport(0, 0, AtmosphereShaders.TransmittanceWidth, AtmosphereShaders.TransmittanceHeight);
            gl.UseProgram(transmittanceProgram);
            SetTau(transmittanceProgram);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            tablesBuilt = true;
        }
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, skyViewTexture, 0);
        gl.Viewport(0, 0, AtmosphereShaders.SkyViewWidth, AtmosphereShaders.SkyViewHeight);
        gl.UseProgram(skyViewProgram);
        SetTau(skyViewProgram);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, transmittanceTexture);
        gl.Uniform1(U(skyViewProgram, "uAtmoTransmittance"), 0);
        gl.Uniform1(U(skyViewProgram, "uEyeRadius"), SkyAtmosphere.PlanetRadius + state.Altitude);
        gl.Uniform1(U(skyViewProgram, "uSunElevation"), MathF.Asin(Math.Clamp(state.Sun.Y, -1, 1)));
        gl.Uniform1(U(skyViewProgram, "uHorizon"), state.Horizon);
        gl.Uniform2(U(skyViewProgram, "uGain"), Settings.SunScale * Settings.SkyGain / 4, Settings.SunScale / 4);
        gl.Uniform1(U(skyViewProgram, "uMieG"), Settings.MieG);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        gl.BindVertexArray(0);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)previousFbo);
        gl.Viewport(viewport[0], viewport[1], (uint)viewport[2], (uint)viewport[3]);
        lutTimer.End();
    }

    void SetTau(uint program)
    {
        var t = Settings.RayleighDepth;
        gl.Uniform4(U(program, "uAtmoTau"), t.X, t.Y, t.Z, Settings.MieDepth);
        gl.Uniform4(U(program, "uAtmoParams"), 1f, 1f, Settings.ScaleHeightUnits, Settings.MieScaleHeight);
        var oz = Settings.OzoneAbsorption;
        gl.Uniform3(U(program, "uAtmoOzone"), oz.X, oz.Y, oz.Z);
    }

    /// <summary>Sets the atmosphere's uniforms (and binds its table) on a program that includes <see cref="AtmosphereShaders.Functions"/>.</summary>
    public void Apply(uint program)
    {
        if (!state.Valid) return;
        var s = state;
        var t = Settings.RayleighDepth;
        gl.UseProgram(program);
        gl.Uniform4(U(program, "uAtmoTau"), t.X, t.Y, t.Z, Settings.MieDepth);
        gl.Uniform4(U(program, "uAtmoParams"), Physical ? 1f : 0f, MathF.Max(s.FogDistance, 1), Settings.ScaleHeightUnits, Settings.MieScaleHeight);
        gl.Uniform4(U(program, "uAtmoSun"), s.Sun.X, s.Sun.Y, s.Sun.Z, s.Horizon);
        var h = new Vector2(s.Sun.X, s.Sun.Z);
        h = h.LengthSquared() > 1e-8f ? Vector2.Normalize(h) : new Vector2(1, 0);
        gl.Uniform3(U(program, "uAtmoSunH"), h.X, h.Y, 0);
        var tint = Weather.SkyColourMultiplier;
        gl.Uniform3(U(program, "uAtmoTint"), tint.X, tint.Y, tint.Z);
        var w = Weather;
        gl.Uniform4(U(program, "uAtmoFog"), w.FogMin, w.FogMax, w.FogEnabled ? 1f : 0f, 0);
        // Fog is lit like the ground: its colour scaled by how much light there is.
        float day = Math.Clamp((s.Sun.Y + 0.1f) / 0.3f, 0, 1);
        var fog = w.FogColour * (0.12f + 0.88f * day) * (Physical ? 0.85f : 1f);
        gl.Uniform3(U(program, "uAtmoFogColour"), fog.X, fog.Y, fog.Z);
        var hc = s.Colours.Horizon;
        gl.Uniform4(U(program, "uAtmoSimple"), hc.X, hc.Y, hc.Z, MathF.Max(s.FogDistance, 1));
        gl.Uniform4(U(program, "uAtmoHaze"), KenshiHaze ? 1f : 0f, HazeDistance * HazeStart, HazeDistance * HazeEnd, w.FogEnabled && w.FogMax > 1 ? 1f / w.FogMax : 0f);
        gl.Uniform3(U(program, "uAtmoSunLight"), s.SunLight.X, s.SunLight.Y, s.SunLight.Z);
        gl.Uniform3(U(program, "uAtmoAmbient"), s.Ambient.X, s.Ambient.Y, s.Ambient.Z);
        var ag = s.Light.AmbientGround;
        gl.Uniform3(U(program, "uAtmoAmbientGround"), ag.X, ag.Y, ag.Z);
        gl.ActiveTexture(TextureUnit.Texture0 + lutUnit);
        gl.BindTexture(TextureTarget.Texture2D, skyViewTexture);
        gl.Uniform1(U(program, "uAtmoSkyView"), lutUnit);
        gl.ActiveTexture(TextureUnit.Texture0);
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
        gl.Uniform4(U(skyProgram, "uSkyExtra"), SunDiscRadiance, coverage, turn, moonRadius);
        gl.Uniform3(U(skyProgram, "uMoonDir"), moon.X, moon.Y, moon.Z);
        gl.Uniform3(U(skyProgram, "uMoonRight"), right.X, right.Y, right.Z);
        gl.Uniform3(U(skyProgram, "uMoonUp"), up.X, up.Y, up.Z);
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

    /// <summary>Times <paramref name="count"/> sky passes and sky-view table rebuilds back to back (ms each); for the screenshot mode's cost report.</summary>
    public (double SkyPassMs, double TableMs) Benchmark(Matrix4x4 viewProjection, int count = 100)
    {
        var colours = state.Colours;
        gl.Finish();
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < count; i++) Draw(viewProjection, colours);
        gl.Finish();
        double pass = watch.Elapsed.TotalMilliseconds / count;
        double table = 0;
        if (Physical)
        {
            watch.Restart();
            for (int i = 0; i < count; i++) BuildSkyView();
            gl.Finish();
            table = watch.Elapsed.TotalMilliseconds / count;
        }
        return (pass, table);
    }

    /// <summary>Collects finished GPU timings (without waiting unless asked).</summary>
    public void Poll(bool wait = false) { lutTimer.Poll(wait); skyTimer.Poll(wait); }

    public string DescribeCost() =>
        Physical ? $"CPU {PrepareMs:0.00} ms (last prepare{(Rebuilt ? ", rebuilt table" : "")}); GPU sky pass {skyTimer.AverageMs:0.00} ms/frame, sky-view table {lutTimer.AverageMs:0.00} ms per rebuild"
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
        lutTimer.Dispose();
        skyTimer.Dispose();
        gl.DeleteVertexArray(vao);
        gl.DeleteFramebuffer(lutFbo);
        gl.DeleteTexture(transmittanceTexture);
        gl.DeleteTexture(skyViewTexture);
        foreach (var t in new[] { starsTexture, moonTexture, cloudsTexture }) if (t != 0) gl.DeleteTexture(t);
        foreach (var p in new[] { simpleProgram, skyProgram, transmittanceProgram, skyViewProgram }) gl.DeleteProgram(p);
    }

    /// <summary>GPU time of a span of commands, by timestamp queries (ring of a few, read without stalling).</summary>
    sealed class GpuSpan : IDisposable
    {
        readonly GL gl;
        readonly uint[] begin = new uint[4], end = new uint[4];
        readonly bool[] pending = new bool[4];
        int index;
        double total;
        int samples;
        public GpuSpan(GL gl)
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
