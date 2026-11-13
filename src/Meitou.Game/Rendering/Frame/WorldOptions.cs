using System.Numerics;
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

    /// <summary>
    /// <c>--at</c>: the world X/Z to load round (null: not given).
    /// </summary>
    public double? X, Z;

    /// <summary>
    /// <c>--zone</c>: the zone whose centre to load round (null: not given).
    /// </summary>
    public ZoneCoordinate? Zone;

    /// <summary>
    /// <c>--town</c>: part of the name of the town to load round (an FCS TOWN record; null: not given).
    /// </summary>
    public string? Town;

    /// <summary>
    /// <c>--radius</c>: half the loaded square's side, in zones of 4608 units.
    /// </summary>
    public float Radius = 1.5f;

    /// <summary>
    /// <c>--yaw</c>, <c>--pitch</c> (degrees) and <c>--distance</c>: the camera round the point (null: 30, 35 and the radius).
    /// </summary>
    public float? Yaw, Pitch, Distance;

    /// <summary>
    /// <c>--screenshot</c>: the PNG to render offscreen and exit with (null: interactive).
    /// </summary>
    public string? Screenshot;

    /// <summary>
    /// <c>--size</c>: the picture's (or window's) width and height in pixels.
    /// </summary>
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

    /// <summary>
    /// The renderers start without textures, without buildings and map features (<c>--no-objects</c>), and in wireframe (<c>--wireframe</c>).
    /// </summary>
    public bool NoTextures, NoObjects, Wireframe;

    /// <summary>
    /// <c>--debug</c>: the terrain's debug view (1 the blend-map slot weights, 2 the layer weights; 0 off).
    /// </summary>
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

    /// <summary>
    /// <c>--water-ocean</c>: the Meitou water's FFT size per cascade (64, 128 or 256).
    /// </summary>
    public int WaterOcean = OceanWaves.DefaultSize;

    /// <summary>
    /// <c>--object-distance</c> as given (null: the reach switch's mode default).
    /// </summary>
    public float? ObjectDistance;

    /// <summary>
    /// The objects' full-detail distance for the reach switch's mode: <see cref="ObjectDistance"/> when given, else the mode's default.
    /// </summary>
    public float ObjectDistanceFor(bool meitou) =>
        ObjectDistance ?? (meitou ? Enhancements.MeitouObjectDistance : Enhancements.FaithfulObjectDistance);

    /// <summary>
    /// The landmark distance (units; 0: no landmarks, the huge objects stay with the others): in Meitou a fixed multiple of the object distance (7.5,
    /// the defaults' ratio), none in Faithful.
    /// </summary>
    public float LandmarkDistanceFor(bool meitou) =>
        meitou ? ObjectDistanceFor(true) * (Enhancements.MeitouLandmarkDistance / Enhancements.MeitouObjectDistance) : 0;

    /// <summary>
    /// The terrain LOD's pixel error for the reach switch's mode: <see cref="TerrainError"/> when given, else the mode's default.
    /// </summary>
    public float TerrainErrorFor(bool meitou) =>
        TerrainError ?? (meitou ? Enhancements.MeitouTerrainPixelError : WorldRenderOptions.DefaultTerrainPixelError);

    /// <summary>
    /// <c>--distant-range</c>: distant towns (and buildings' distant meshes) up to this many zones (0: none).
    /// </summary>
    public float DistantZones = ObjectRanges.MaxDistantTownRangeZones;

    /// <summary>
    /// <c>--no-foliage</c>: no trees, bushes, rocks or grass.
    /// </summary>
    public bool NoFoliage;

    /// <summary>
    /// <c>--time</c>: the time of day in game hours, for the sun.
    /// </summary>
    public float Hour = 13;

    /// <summary>
    /// The viewer's clock: game hours per real minute (0: the time stays where it is set).
    /// </summary>
    public float TimeSpeed;

    /// <summary>
    /// <c>--fog</c>: where the haze is complete (the far plane follows it); <c>--material-distance</c>: beyond it the terrain shows the biomes'
    /// ground colour.
    /// </summary>
    public float FogDistance = 250000, MaterialDistance = 30000;

    /// <summary>
    /// Terrain LOD (<see cref="TerrainLod"/>): the screen-space error in render pixels (null: the reach switch's mode default).
    /// </summary>
    public float? TerrainError;

    /// <summary>
    /// <c>--no-water</c>: no water; <c>--show-keys</c>: the key list overlay starts open.
    /// </summary>
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
    /// (docs/formats/settings.md). The viewer starts at full quality (the old look); the game's missing-key defaults are
    /// <c>TextureQuality.Default</c> (1) and <see cref="ReflectionPass.DefaultLevel"/> (2): <c>--texture-quality 1 --water-reflection 2</c>.
    /// </summary>
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

    /// <summary>
    /// The <c>interiorgi</c> switch: bounced light indoors with the probe GI (false: the original ambient on room surfaces).
    /// </summary>
    public bool InteriorGi = true;

    /// <summary>
    /// Whether the device must be made with ray queries (<c>VulkanDisplay.RayTracing</c>).
    /// </summary>
    public bool WantsRayTracing => Gi || GiDebug > 0;

    /// <summary>
    /// <c>--shadow-range</c> as given (null: the default of the shadows switch's mode, see <see cref="ShadowRangeFor"/>).
    /// </summary>
    public float? ShadowRange;

    /// <summary>
    /// <c>--shadow-range 0</c>: the shadow pass is made but starts off (the Tab slider's left end; it can be turned on there).
    /// </summary>
    public bool ShadowsOff;

    /// <summary>
    /// The shadows switch (Enhancements): Meitou by default, false the game's CSM.
    /// </summary>
    public bool MeitouShadows = true;

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

    /// <summary>
    /// <c>--range-large</c>: the large size class's foliage range (null: the default).
    /// </summary>
    public float? LargeRange;

    /// <summary>
    /// The impostors switch (Enhancements): far foliage as baked billboards (Meitou, default) or meshes only (the game); the distances (null: the
    /// default; only <c>--low-end</c> sets them).
    /// </summary>
    public bool Impostors = true;

    /// <summary>
    /// The Meitou <c>lod</c> switch: generated mesh levels for TERRAIN-mode foliage meshes (<see cref="Foliage.FoliageRenderer.Lod"/>).
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
    /// The <c>chars2</c> switch's Meitou side, off by default: hair, cloth, skin and eye shading (<c>CharacterRenderer.Chars2</c>).
    /// </summary>
    public bool Chars2;

    /// <summary>
    /// Which parts of it are on (<c>CharacterRenderer.SurfaceParts</c>): all by default.
    /// </summary>
    public int Chars2Parts = Characters.CharacterRenderer.SurfaceAll;

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
    /// The <c>footprints</c> switch's Meitou side: wet blood a character walks through leaves temporary prints on the terrain
    /// (<c>BloodFootprints</c>); false: none, as the game.
    /// </summary>
    public bool MeitouFootprints = true;

    /// <summary>
    /// The <c>tracers</c> switch's Meitou side: a semi-transparent streak behind each bolt in flight (the game's <c>GameHost.AddTracers</c>); false:
    /// the bolt alone, as the game.
    /// </summary>
    public bool MeitouTracers = true;

    /// <summary>
    /// The foliage's impostor distances, of the small and of the large meshes (null: the renderer's defaults; <c>--low-end</c> sets them).
    /// </summary>
    public float? ImpostorDistance, LargeImpostorDistance;

    /// <summary>
    /// <c>--impostor-budget</c>: the most video memory the impostor atlases may use, in MB (null: a share of the card's budget).
    /// </summary>
    public double? ImpostorBudgetMb;

    /// <summary>
    /// The Meitou haze switch (default): the haze at <see cref="HazeStrength"/>; Faithful: the game's (1).
    /// </summary>
    public bool MeitouHaze = true;

    /// <summary>
    /// The Meitou haze's strength (a Tab slider), kept while the switch is Faithful.
    /// </summary>
    public float HazeStrength = Enhancements.MeitouHazeStrength;

    /// <summary>
    /// The Meitou night switch (default): the night air, a faint glow the haze fades into and the sky shows above the horizon
    /// (<c>SkyRenderer.NightAir</c>).
    /// </summary>
    public bool NightAir = true;

    /// <summary>
    /// The Meitou planetshine switch (default): the big planet lights the night (<c>SkyRenderer.Planetshine</c>).
    /// </summary>
    public bool Planetshine = true;

    /// <summary>
    /// The planetshine's strength, a multiple of the physical value (a Tab slider).
    /// </summary>
    public float PlanetshineStrength = Enhancements.MeitouPlanetshineStrength;

    /// <summary>
    /// The Meitou stars switch (default): the procedural night sky (<c>SkyRenderer.MeitouStars</c>); false: the game's starfield texture.
    /// </summary>
    public bool MeitouStars = true;

    /// <summary>
    /// The Meitou clouds switch (default): the cloud layer lit by the sun through its depth (<c>SkyRenderer.LitClouds</c>); false: the game's flat
    /// colour.
    /// </summary>
    public bool MeitouClouds = true;

    /// <summary>
    /// The Meitou cloudshadows switch (default): the cloud layer shades the sun on the land and water (<c>SkyRenderer.CloudShadows</c>); false: none,
    /// as the game.
    /// </summary>
    public bool CloudShadows = true;

    /// <summary>
    /// The Meitou cloudfog switch (default): the cloud shadows fade near and inside placed fog volumes (<c>SkyRenderer.CloudFog</c>); false: full
    /// strength.
    /// </summary>
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

    /// <summary>
    /// Whether the weather scheduler runs (<see cref="Weather"/> null or "auto") rather than a forced weather.
    /// </summary>
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

    /// <summary>
    /// <c>--audio-gain</c>: the whole mix raised or lowered, in dB (0: the game's levels).
    /// </summary>
    public float AudioGainDb;

    /// <summary>
    /// <c>--particle-prewarm</c>: seconds simulated before the first picture (null: each system's longest particle life).
    /// </summary>
    public float? ParticlePrewarm;

    /// <summary>
    /// <c>--particle-density x</c>: the Tab panel's "Particle density x" at start (0.1 to 1).
    /// </summary>
    public float ParticleDensity = 1;

    /// <summary>
    /// <c>--anisotropy n</c>: the Tab panel's anisotropic filtering at start (1, 2, 4, 8 or 16; 16 leaves the textures as asked).
    /// </summary>
    public int Anisotropy = 16;

    /// <summary>
    /// <c>--clouds</c>: the cloud density forced for testing, 0..1 (null: the weather's).
    /// </summary>
    public float? Clouds;

    /// <summary>
    /// The post-processing options: <c>--post</c>'s preset (Meitou by default) and the options after it.
    /// </summary>
    public PostOptions Post = PostOptions.Create("meitou");
    #endregion

    #region Fly and cameras

    /// <summary>
    /// <c>--fly-to</c>: with <c>--screenshot</c>, the world X/Z to fly to before taking the picture (null: none).
    /// </summary>
    public double? FlyToX, FlyToZ;

    /// <summary>
    /// <c>--camera-code</c>: the camera's target, yaw, pitch and distance from a viewer's Ctrl+C code (the world is loaded round it).
    /// </summary>
    public WorldCameraCode.Pose? CameraPose;

    /// <summary>
    /// Frames of the offscreen benchmark flight (0: none), the circle's radius and the speed per frame.
    /// </summary>
    public int FlyBenchmark;

    /// <summary>
    /// The benchmark flight's circle radius and speed per frame, in world units.
    /// </summary>
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
    /// cref="Bench.AbToggles"/>) with <c>--ab-period</c> frames per side, <c>--bench-frames</c> measured frames (default 600 with <c>--ab</c>),
    /// <c>--bench-out</c> the JSON, <c>--bench-motion</c> still / orbit / fly / turn, <c>--no-gpu-lock</c>, <c>--bench-compare</c> two JSON files.
    /// </summary>
    public string? View, Ab, BenchOut;

    /// <summary>
    /// <c>--ab-period</c>: frames per side of an A/B run (at least 1); <c>--bench-frames</c>: the frames measured (0: no bench run).
    /// </summary>
    public int AbPeriod = 1, BenchFrames;

    /// <summary>
    /// <c>--bench-motion</c>: the camera during a bench run, still, orbit, fly or turn.
    /// </summary>
    public string BenchMotion = "still";

    /// <summary>
    /// <c>--bench-orbit</c> pixels of mouse drag a frame of the orbit motion.
    /// </summary>
    public float BenchOrbit = 3;

    /// <summary>
    /// <c>--no-gpu-lock</c>: do not queue on the GPU lock file while measuring.
    /// </summary>
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

    /// <summary>
    /// <c>--bench-compare</c>: the two bench result files to print side by side.
    /// </summary>
    public (string A, string B)? BenchCompare;

    /// <summary>
    /// Offscreen pictures: radians the camera orbits by every frame (tests the motion vectors under a temporal upscaler).
    /// </summary>
    public float OrbitStep;

    /// <summary>
    /// <c>--sequence &lt;n&gt;</c>: with --screenshot, n more frames saved after the picture (<c>name-01.png</c>...), each a step of the motion
    /// (--orbit-step, --sway-step): a precision or motion check.
    /// </summary>
    public int Sequence;

    /// <summary>
    /// <c>--pan-step &lt;x&gt;,&lt;z&gt;</c>: with --screenshot, the camera's target moves this far (world units) every frame, its height kept
    /// (checks what follows the camera).
    /// </summary>
    public Vector2 PanStep;

    /// <summary>
    /// <c>--pan-frames &lt;n&gt;</c>: the motion (pan, orbit, zoom) stops after the sequence's frame n (the rest show the same pose settling).
    /// </summary>
    public int PanFrames;

    /// <summary>
    /// <c>--zoom-step &lt;f&gt;</c>: with --screenshot, the camera's distance grows by this fraction every frame (negative: closer).
    /// </summary>
    public float ZoomStep;

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
        Enhancements.Apply(Switches(this), "gi,interiorgi,gihybrid,parallax,displace", meitou: false);
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

    /// <summary>
    /// The texture quality <c>--low-end</c> sets (Low).
    /// </summary>
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
        () => o.MeitouTracers, v => o.MeitouTracers = v,
        () => o.Chars2, v => o.Chars2 = v,
        () => o.MeitouFootprints, v => o.MeitouFootprints = v);

    /// <summary>
    /// The world options from the command line (<see cref="WorldCommandLine.Parse"/>); null for <c>--help</c>.
    /// </summary>
    public static WorldOptions? Parse(string[] args) => WorldCommandLine.Parse(args);

    #endregion
}
