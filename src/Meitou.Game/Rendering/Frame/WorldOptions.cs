using Meitou.Data.World;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Terrain;
using Meitou.Game.Rendering.Water;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// The world view's options: what the command line (<see cref="WorldCommandLine"/>) and the presets set, read by the loading, the renderers
/// and the run.
/// </summary>
sealed class WorldOptions
{
    #region Place and view

    public double? X, Z;
    public ZoneCoordinate? Zone;
    public string? Town;
    public float Radius = 1.5f;
    public float? Yaw, Pitch, Distance;
    public string? Screenshot;
    public int Width = 1280, Height = 960;

    /// <summary>
    /// <c>--size</c> was given: an interactive window keeps that size instead of starting maximised.
    /// </summary>
    public bool SizeGiven;

    /// <summary>
    /// The size of a bench run (<c>--bench-frames</c> or <c>--ab</c>) without <c>--size</c>: smaller, so the runs are quicker.
    /// </summary>
    public static readonly (int Width, int Height) BenchSize = (1280, 720);

    /// <summary>
    /// The render size of <c>--benchmark-baseline</c>: 1080p in a window, whatever the monitor.
    /// </summary>
    public static readonly (int Width, int Height) BenchmarkBaselineSize = (1920, 1080);
    public bool NoTextures, NoObjects, Wireframe;
    public int Debug;

    /// <summary>
    /// The reach switch (Enhancements): Meitou by default (objects to 20000, landmarks 7.5 times that, terrain error 16 px) or the viewer's
    /// old values (12000, none, 10 px).
    /// <c>--object-distance</c> and <c>--terrain-error</c> as given (null: the mode's default, see the <c>...For</c> methods).
    /// </summary>
    public bool MeitouReach = true;

    /// <summary>
    /// The water switch (Enhancements): waves, breakers and foam (Meitou, default) or the game's flat water.
    /// </summary>
    public bool MeitouWater = true;

    /// <summary>
    /// <c>--water-grid</c>: the Meitou water grid's segments round the eye (64 to 1024; fewer for integrated GPUs).
    /// </summary>
    public int WaterGrid = WaterRenderer.DefaultGridSegments;
    public int WaterOcean = OceanWaves.DefaultSize;
    public float? ObjectDistance;
    public float ObjectDistanceFor(bool meitou) =>
        ObjectDistance ?? (meitou ? Enhancements.MeitouObjectDistance : Enhancements.FaithfulObjectDistance);

    /// <summary>
    /// The landmark distance (units; 0: no landmarks, the huge objects stay with the others): in Meitou a fixed multiple of the object distance (7.5,
    /// the defaults' ratio), none in Faithful.
    /// </summary>
    public float LandmarkDistanceFor(bool meitou) =>
        meitou ? ObjectDistanceFor(true) * (Enhancements.MeitouLandmarkDistance / Enhancements.MeitouObjectDistance) : 0;
    public float TerrainErrorFor(bool meitou) =>
        TerrainError ?? (meitou ? Enhancements.MeitouTerrainPixelError : WorldRenderOptions.DefaultTerrainPixelError);
    public float DistantZones = ObjectRanges.MaxDistantTownRangeZones;
    public bool NoFoliage;
    public float Hour = 13;

    /// <summary>
    /// The viewer's clock: game hours per real minute (0: the time stays where it is set).
    /// </summary>
    public float TimeSpeed;
    public float FogDistance = 250000, MaterialDistance = 30000;

    /// <summary>
    /// Terrain LOD (<see cref="TerrainLod"/>): the screen-space error in render pixels (null: the reach switch's mode default).
    /// </summary>
    public float? TerrainError;
    public bool NoWater, ShowKeys;

    #endregion

    #region Quality and rendering

    /// <summary>
    /// No placed fog volumes (<c>fogfeatures.dat</c> not read); the weather's fog spheres still work. Set by the game's sandbox, no switch of its
    /// own.
    /// </summary>
    public bool NoFogFeatures;

    /// <summary>
    /// `--play-shots`: the viewer starts flying its saved camera keys (the F2 timeline) on a loop.
    /// </summary>
    public bool PlayShots;

    /// <summary>
    /// The game's <c>texture resolution gimping</c> (0..4; missing key: 1) and <c>water reflection</c> (0..4; missing: 2) settings
    /// (docs/formats/settings.md).
    /// </summary>
    // The viewer starts at full quality (the old look); the game's missing-key defaults are TextureQuality.Default (1) and
    // ReflectionPass.DefaultLevel (2): --texture-quality 1 --water-reflection 2.
    public int TextureQuality = 0, WaterReflection = 4;

    /// <summary>
    /// Samples per texel of the reflection target (<c>--reflection-samples</c>; 1 disables the MSAA/resolve path).
    /// </summary>
    public int ReflectionSamples = ReflectionPass.DefaultSamples;

    /// <summary>
    /// Sun shadows (docs/formats/shadows.md): the game's <c>shadow quality</c> index, <c>Shadow Range</c>, the debug view.
    /// </summary>
    public int ShadowQuality = 2, DebugShadows, ShadowFilter = 2;

    /// <summary>
    /// The ray-traced global illumination's debug view (0 off; docs/render-gi.md).
    /// </summary>
    public int GiDebug;

    /// <summary>
    /// The probe GI, on unless <c>--no-gi</c> (it needs ray queries: without them it is off, with a note); <see cref="GiProbes"/> is its Faithful /
    /// Meitou switch (<c>gi</c>).
    /// </summary>
    public bool Gi = true, GiProbes = true;
    public bool InteriorGi = true;

    /// <summary>
    /// Whether the device must be made with ray queries (<c>VulkanDisplay.RayTracing</c>).
    /// </summary>
    public bool WantsRayTracing => Gi || GiDebug > 0;
    public float? ShadowRange;   // --shadow-range as given (null: the default of the shadows switch's mode, see ShadowRangeFor)

    /// <summary>
    /// <c>--shadow-range 0</c>: the shadow pass is made but starts off (the Tab slider's left end; it can be turned on there).
    /// </summary>
    public bool ShadowsOff;
    public bool MeitouShadows = true;   // the shadows switch (Enhancements): Meitou by default, false the game's CSM

    /// <summary>
    /// <c>--no-vsync</c>: the interactive window starts uncapped (the Tab panel's VSync).
    /// </summary>
    public bool VSync = true;

    /// <summary>
    /// The Tab panel's "Object LOD distance x" (0.25 to 4; the object renderer's LOD bias is its inverse); only <c>--low-end</c> moves it.
    /// </summary>
    public float ObjectLod = 1;

    /// <summary>
    /// <c>--grass-density x</c>: the Tab panel's "Grass density x" (0.1 to 2; null: the renderer's default).
    /// </summary>
    public float? GrassDensity;

    /// <summary>
    /// The shadow distance for the shadows switch's mode: <c>--shadow-range</c> when given, else the game's default (5000) in Faithful and
    /// <see cref="Enhancements.MeitouShadowRange"/> in Meitou. The game's slider ends at 9000 (<see cref="KenshiShadows.MaxRange"/>); with the
    /// Meitou shadows the option may go to <see cref="Enhancements.MeitouShadowRangeMax"/> (the cascades are fitted to the view, and far foliage
    /// casts through impostors; the Tab slider goes further, at the user's risk of running out of video memory).
    /// </summary>
    public float ShadowRangeFor(bool meitou) => meitou
        ? Math.Clamp(ShadowRange ?? Enhancements.MeitouShadowRange, KenshiShadows.MinRange, Enhancements.MeitouShadowRangeMax)
        : Math.Clamp(ShadowRange ?? KenshiShadows.DefaultRange, KenshiShadows.MinRange, KenshiShadows.MaxRange);

    #endregion

    #region Enhancement switches

    /// <summary>
    /// The range switch (Enhancements): foliage meshes drawn to their size class's range (Meitou, default) or their layer's (the game); the
    /// large class's range (null: the default;
    /// <c>--range-large</c>), the medium and small ranges follow it at their default ratios.
    /// </summary>
    public bool MeitouRange = true;
    public float? LargeRange;

    /// <summary>
    /// The impostors switch (Enhancements): far foliage as baked billboards (Meitou, default) or meshes only (the game); the distances (null: the
    /// default; only <c>--low-end</c> sets them).
    /// </summary>
    public bool Impostors = true;

    /// <summary>
    /// The Meitou <c>lod</c> switch: generated mesh levels for TERRAIN-mode foliage meshes (<see cref="FoliageRenderer.Lod"/>).
    /// </summary>
    public bool FoliageLod = true;

    /// <summary>
    /// Characters' generated LOD levels (the <c>charlod</c> switch's Meitou side); false: the mesh files' own levels
    /// (<c>CharacterRenderer.GeneratedLod</c>).
    /// </summary>
    public bool CharacterLod = true;

    /// <summary>
    /// The <c>skin</c> switch's Meitou side: pre-integrated scattering on people's bare skin (<c>CharacterRenderer.SkinShading</c>).
    /// </summary>
    public bool SkinShading = true;

    /// <summary>
    /// The <c>interiorlamps</c> switch's Meitou side: interior lamps shine by day too (<c>WorldLamps.InteriorAllDay</c>).
    /// </summary>
    public bool InteriorLamps = true;

    /// <summary>
    /// Supersampled item icons (the <c>icons</c> switch's Meitou side, <c>CharacterRenderer.EnhancedIcons</c>); false: drawn as the game draws them.
    /// </summary>
    public bool MeitouIcons = true;

    /// <summary>
    /// The <c>blood</c> switch's Meitou side: every blood cell within Decal Range drawn, up to <c>TerrainDecalRenderer.MaxSlots</c>; false: the
    /// game's 8 nearest.
    /// </summary>
    public bool MeitouBlood = true;

    /// <summary>
    /// The <c>bloodsurfaces</c> switch's Meitou side: blood also on walls, buildings, floors and other static objects
    /// (<c>TerrainDecalRenderer.Surfaces</c>); false: the terrain only, as the game.
    /// </summary>
    public bool MeitouBloodSurfaces = true;

    /// <summary>
    /// The <c>tracers</c> switch's Meitou side: a semi-transparent streak behind each bolt in flight (the game's <c>GameHost.AddTracers</c>); false:
    /// the bolt alone, as the game.
    /// </summary>
    public bool MeitouTracers = true;
    public float? ImpostorDistance, LargeImpostorDistance;
    public double? ImpostorBudgetMb;
    public bool MeitouHaze = true; // the Meitou haze switch (default): the haze at HazeStrength; Faithful: the game's (1)
    public float HazeStrength = Enhancements.MeitouHazeStrength; // the Meitou haze's strength (a Tab slider), kept while the switch is Faithful
    // the Meitou night switch (default): the night air, a faint glow the haze fades into and the sky shows above the horizon (SkyRenderer.NightAir)
    public bool NightAir = true;
    public bool Planetshine = true; // the Meitou planetshine switch (default): the big planet lights the night (SkyRenderer.Planetshine)
    public float PlanetshineStrength = Enhancements.MeitouPlanetshineStrength; // x the physical value (a Tab slider)
    // the Meitou stars switch (default): the procedural night sky (SkyRenderer.MeitouStars); false: the game's starfield texture
    public bool MeitouStars = true;
    // the Meitou clouds switch (default): the cloud layer lit by the sun through its depth (SkyRenderer.LitClouds);
    // false: the game's flat colour
    public bool MeitouClouds = true;
    // the Meitou cloudshadows switch (default): the cloud layer shades the sun on the land and water (SkyRenderer.CloudShadows);
    // false: none, as the game
    public bool CloudShadows = true;
    // the Meitou cloudfog switch (default): the cloud shadows fade near and inside placed fog volumes (SkyRenderer.CloudFog);
    // false: full strength
    public bool CloudFog = true;

    #endregion

    #region Weather and effects

    /// <summary>
    /// <c>--weather</c>: a WEATHER record's name forces that weather at the camera; null or "auto" lets the scheduler (docs/formats/weather.md) run.
    /// </summary>
    public string? Weather;

    /// <summary>
    /// <c>--day</c> (null: day 0 in the viewer, the game's clock start in the game).
    /// </summary>
    public int? Day;
    public bool AutoWeather => Weather is null || Weather.Equals("auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The weather particle effects (docs/formats/particle-universe.md): <c>--no-particles</c> turns the pass off, <c>--particle-prewarm</c> is the
    /// seconds simulated before the first picture (null: each system's longest particle life).
    /// </summary>
    public bool NoParticles;

    /// <summary>
    /// <c>--no-audio</c>: no sound (docs/audio.md); <c>--audio-gain &lt;dB&gt;</c> raises or lowers the whole mix (0 = the game's levels).
    /// </summary>
    public bool NoAudio;
    public float AudioGainDb;
    public float? ParticlePrewarm;

    /// <summary>
    /// <c>--particle-density x</c>: the Tab panel's "Particle density x" at start (0.1 to 1).
    /// </summary>
    public float ParticleDensity = 1;

    /// <summary>
    /// <c>--anisotropy n</c>: the Tab panel's anisotropic filtering at start (1, 2, 4, 8 or 16; 16 leaves the textures as asked).
    /// </summary>
    public int Anisotropy = 16;
    public float? Clouds;
    public PostOptions Post = PostOptions.Create("meitou");
    #endregion

    #region Fly and cameras

    public double? FlyToX, FlyToZ;

    /// <summary>
    /// <c>--camera-code</c>: the camera's target, yaw, pitch and distance from a viewer's Ctrl+C code (the world is loaded round it).
    /// </summary>
    public WorldCameraCode.Pose? CameraPose;

    /// <summary>
    /// Frames of the offscreen benchmark flight (0: none), the circle's radius and the speed per frame.
    /// </summary>
    public int FlyBenchmark;
    public float FlyRadius = 12000, FlySpeed = 150;

    /// <summary>
    /// The benchmark without a wait for the GPU each frame and without the 60 fps pacing: up to two frames in flight (both backends), frame time =
    /// the interval between frames.
    /// </summary>
    public bool FlyPipelined;

    /// <summary>
    /// <c>--fly-csv &lt;file&gt;</c>: the benchmark writes one row per frame there (frame times, video memory) and the allocator owners every 30
    /// frames to <c>&lt;file&gt;.owners.csv</c>.
    /// </summary>
    public string? FlyCsv;

    #endregion

    #region Benchmark and run

    /// <summary>
    /// The benchmark harness (docs/bench.md "Benchmark harness"): <c>--view</c> the named view, <c>--ab</c> the switch to alternate (<see
    /// cref="AbToggles"/>) with
    /// <c>--ab-period</c> frames per side, <c>--bench-frames</c> measured frames (default 600 with <c>--ab</c>), <c>--bench-out</c> the JSON,
    /// <c>--bench-motion</c>
    /// still / orbit / fly / turn, <c>--no-gpu-lock</c>, <c>--bench-compare</c> two JSON files.
    /// </summary>
    public string? View, Ab, BenchOut;
    public int AbPeriod = 1, BenchFrames;
    public string BenchMotion = "still";

    /// <summary>
    /// <c>--bench-orbit</c> pixels of mouse drag a frame of the orbit motion.
    /// </summary>
    public float BenchOrbit = 3;
    public bool NoGpuLock;

    /// <summary>
    /// --bench-serial: the benchmark waits for every frame (CPU + GPU added up, the old behaviour) instead of keeping frames in flight.
    /// </summary>
    public bool BenchSerial;

    /// <summary>
    /// --bench-tris: after the timing, the triangles drawn per pass (pipeline statistics queries) and the main view's triangle size histogram
    /// (docs/bench.md "Triangles per pass").
    /// </summary>
    public bool BenchTris;
    public (string A, string B)? BenchCompare;

    /// <summary>
    /// Offscreen pictures: radians the camera orbits by every frame (tests the motion vectors under a temporal upscaler).
    /// </summary>
    public float OrbitStep;

    /// <summary>
    /// Interactive window: the monitor it opens on (1-based; <c>--monitor</c>).
    /// </summary>
    public int? Monitor;

    /// <summary>
    /// Interactive window: a bordered, maximized window instead of borderless full screen (<c>--windowed</c>).
    /// </summary>
    public bool Windowed;

    /// <summary>
    /// Interactive window: never shown (<c>--hidden-window</c>, for unattended runs such as <c>--play-bench</c>); implies <see cref="Windowed"/>.
    /// </summary>
    public bool HiddenWindow;

    /// <summary>
    /// Interactive loop without a window, swapchain or presentation (<c>--headless</c> or <c>MEITOU_HEADLESS=1</c>; the control channel and
    /// screenshots work); the size is <c>--size</c>, else 1920x1080.
    /// </summary>
    public bool Headless;

    /// <summary>
    /// Offscreen pictures: seconds the grass sway advances every frame (tests the grass motion under a temporal upscaler; 0 holds it still).
    /// </summary>
    public float SwayStep;

    /// <summary>
    /// The character renderer's test harness (<c>--crowd N</c>, <see cref="Characters.CrowdHarness"/>): N generated characters round the start point.
    /// </summary>
    public int Crowd;

    /// <summary>
    /// The longest <c>--shadow-range</c> the command line takes (the game stops at 9000; larger ranges cost VRAM and above this the driver has been
    /// seen to reset).
    /// </summary>
    public const float CommandLineMaxShadowRange = 15000;

    #endregion

    #region Presets

    /// <summary>
    /// <c>--low-end</c>: the potato-PC settings, applied where it stands among the options (so any option after it overrides one of them).
    /// Short draw distances, no reflections, shadows, ground relief or light shafts, the game's FXAA turned off too, 0.83 render scale, quarter-size
    /// particles.
    /// </summary>
    internal void ApplyLowEnd()
    {
        VSync = false;
        (ObjectDistance, ObjectLod) = (LowEndObjectDistance, LowEndObjectLod);
        (LargeRange, ImpostorDistance, LargeImpostorDistance) = (LowEndLargeRange, LowEndImpostorDistance,
            LowEndLargeImpostorDistance);   // the medium and small ranges follow the large one
        GrassDensity = LowEndGrassDensity;
        WaterReflection = 0;
        (ShadowsOff, ShadowFilter) = (true, 0);   // --shadow-range 0
        Enhancements.Apply(Switches(this), "ao,aa,shadows,water,parallax,displace", meitou: false);
        Enhancements.Apply(Switches(this), "particles", meitou: true);
        GroundRelief.Quality = 0;
        GroundRelief.RefinePixels = 0;
        RockSmoothing.TessellateActive = false;
        Post.LightShafts = false;
        Post.ShaftQuality = 0;
        Post.Fxaa = false;
        (Post.Upscale.Scale, Post.Upscale.Explicit) = (LowEndRenderScale, true);
        Post.ParticleDivisor = 4;
        ParticleDensity = LowEndParticleDensity;
        (Anisotropy, TextureQuality, MaterialDistance) = (1, LowEndTextureQuality, 15000);
    }

    /// <summary>
    /// <c>--benchmark-baseline</c>: the parts of the benchmark baseline that must be set before the device and the post chain exist, where it stands
    /// among the options (the game sets them again after the command line): a 1920x1080 window (not full screen or maximised), no ray-traced GI (the
    /// device is made without ray queries), no ground parallax or tessellation, no upscaler, SMAA at full size, texture quality 1. The game sets the
    /// rest of the Medium tier through the settings (SettingsMenu.ApplyBenchmarkBaseline).
    /// </summary>
    internal void ApplyBenchmarkBaseline()
    {
        (Width, Height) = BenchmarkBaselineSize;
        SizeGiven = Windowed = true;
        Gi = false;
        Enhancements.Apply(Switches(this), "gi,interiorgi,parallax,displace", meitou: false);
        RockSmoothing.TessellateActive = false;
        GroundRelief.RefinePixels = 0;
        (Post.Upscale.Kind, Post.Upscale.Scale, Post.Upscale.Explicit) = (UpscalerKind.Off, 1, true);
        Post.Smaa = true;
        TextureQuality = 1;
    }

    /// <summary>
    /// <c>--low-end</c>'s values, which the Low graphics preset sets too (<c>SettingsMenu.Presets</c>).
    /// </summary>
    public const float LowEndObjectDistance = 5000, LowEndObjectLod = 0.25f, LowEndLargeRange = 5050,
        LowEndImpostorDistance = 1150, LowEndLargeImpostorDistance = 1100,
        LowEndGrassDensity = 0.1f, LowEndRenderScale = 0.83f, LowEndParticleDensity = 0.1f;
    public const int LowEndTextureQuality = 3;

    #endregion

    #region Usage and switches

    /// <summary>
    /// The command line's help text (<see cref="WorldCommandLine.Usage"/>).
    /// </summary>
    public const string Usage = WorldCommandLine.Usage;

    /// <summary>
    /// The Faithful / Meitou switches over the options (for <c>--meitou</c> / <c>--faithful</c>).
    /// </summary>
    internal static IReadOnlyList<Enhancement> Switches(WorldOptions o) => Enhancements.Create(o.Post, () => o.MeitouHaze, v => o.MeitouHaze = v,
        () => o.MeitouShadows, v => o.MeitouShadows = v, () => o.MeitouRange, v => o.MeitouRange = v,
        () => o.Impostors, v => o.Impostors = v, () => o.MeitouReach, v => o.MeitouReach = v,
        () => o.MeitouWater, v => o.MeitouWater = v, () => o.FoliageLod, v => o.FoliageLod = v, () => o.GiProbes, v => o.GiProbes = v,
        () => o.NightAir, v => o.NightAir = v, () => o.Planetshine, v => o.Planetshine = v,
        () => o.MeitouStars, v => o.MeitouStars = v,
        () => o.MeitouClouds, v => o.MeitouClouds = v,
        () => o.CloudShadows, v => o.CloudShadows = v,
        () => o.CloudFog, v => o.CloudFog = v,
        () => o.InteriorGi, v => o.InteriorGi = v,
        () => o.CharacterLod, v => o.CharacterLod = v,
        () => o.SkinShading, v => o.SkinShading = v,
        () => o.InteriorLamps, v => o.InteriorLamps = v,
        () => o.MeitouIcons, v => o.MeitouIcons = v,
        () => o.MeitouBlood, v => o.MeitouBlood = v,
        () => o.MeitouBloodSurfaces, v => o.MeitouBloodSurfaces = v,
        () => o.MeitouTracers, v => o.MeitouTracers = v);

    /// <summary>
    /// The world options from the command line (<see cref="WorldCommandLine.Parse"/>); null for <c>--help</c>.
    /// </summary>
    public static WorldOptions? Parse(string[] args) => WorldCommandLine.Parse(args);

    #endregion
}
