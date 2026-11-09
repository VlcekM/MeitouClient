using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;
using Meitou.Data.Particles;
using Meitou.Data.World;

using Meitou.Game.Rendering.Bench;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Particles;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Shadows;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;
using Meitou.Game.Rendering.Water;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Frame;

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

    /// <summary>The reach switch (Enhancements): Meitou by default (objects to 20000, landmarks 7.5 times that, terrain error 16 px) or the viewer's
    /// old values (12000, none, 10 px).
    /// <c>--object-distance</c> and <c>--terrain-error</c> as given (null: the mode's default, see the <c>...For</c> methods).</summary>
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
    /// casts
    /// through impostors; the Tab slider goes further, at the user's risk of running out of video memory).
    /// </summary>
    public float ShadowRangeFor(bool meitou) => meitou
        ? Math.Clamp(ShadowRange ?? Enhancements.MeitouShadowRange, KenshiShadows.MinRange, Enhancements.MeitouShadowRangeMax)
        : Math.Clamp(ShadowRange ?? KenshiShadows.DefaultRange, KenshiShadows.MinRange, KenshiShadows.MaxRange);

    #endregion

    #region Enhancement switches

    /// <summary>The range switch (Enhancements): foliage meshes drawn to their size class's range (Meitou, default) or their layer's (the game); the
    /// large class's range (null: the default;
    /// <c>--range-large</c>), the medium and small ranges follow it at their default ratios.</summary>
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
    // the Meitou night switch (default): the night air, a faint glow the haze fades into and the sky shows above the horizon
    // (SkyRenderer.NightAir)
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
    void ApplyLowEnd()
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
    /// device
    /// is made without ray queries), no ground parallax or tessellation, no upscaler, SMAA at full size, texture quality 1. The game sets the rest of
    /// the
    /// Medium tier through the settings (SettingsMenu.ApplyBenchmarkBaseline).
    /// </summary>
    void ApplyBenchmarkBaseline()
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

    public const string Usage = """
        meitou-viewer --world [where] [options]
          where (default: the world's centre):
            --at <x>,<z>           world X/Z
            --zone <i>,<j>         centre of zone.i.j (i along +X, j along +Z)
            --town <name>          a town whose name contains <name> (FCS TOWN record)
          --radius <zones>         half-size of the loaded square in zones (default 1.5; 4608 units each)
          --yaw <deg> --pitch <deg> --distance <units>   camera around the point (defaults 30, 35, radius)
          --screenshot <out.png> --size <W>x<H>          render offscreen to a PNG and exit
          --monitor <n>                                  open the window on monitor n (1-based)
          --headless                                     no window, swapchain or presentation: the interactive loop draws into an offscreen picture of --size (default 1920x1080), 30 fps unless --fps-limit; for the control channel (--control-dir) and its screenshot command; env MEITOU_HEADLESS=1
          --windowed                                     a bordered window: the --size asked, else maximized (default: borderless full screen; Alt+Enter toggles)
          --no-objects             skip buildings and map features
          --no-foliage             no trees, bushes, rocks or grass (F toggles)
          --no-load-cache          do not read or write the load caches (foliage layout, character LOD and terrain colour caches)
          --range-large <u>        foliage draw range of the large size class (the range switch, F6; default 50000; the medium and small ranges follow at 12000 : 3500 of it; Tab sliders)
          --impostor-budget <MB>   fixes the most video memory the resident impostor atlases may use (default: 8% of the card's budget, 5% on an integrated GPU, 48-1024 MB; docs/impostors.md section 10; atlases that do not fit are held at coarser mips, then stay meshes)
          --object-distance <u>    draw placed objects at full detail up to this distance (the reach switch, F8: default 20000 in Meitou, 12000 in Faithful); Meitou draws huge placed objects (world radius 2000 and up: the giant wrecks, skeletons, towers) 7.5 times as far
          --distant-range <zones>  distant towns (and buildings' distant meshes) up to this many zones (default 10, the game's setting maximum; its default is 6; 0: no distant towns)
          --debug <n>              1 blend-map slot weights, 2 layer weights (R cliff, G slope, B grass)
          --time <hour>            time of day for the sun (default 13; sunrise and sunset from the CONSTANTS record)
          --time-speed <h/min>     the viewer's clock runs on, game hours per real minute (default 0: stopped; Tab slider)
          --no-water               leave out the water
          --water-grid <n>         the Meitou water grid's segments round the eye (default 256; 128 lower detail, 512 finer)
          --water-ocean <n>        the Meitou water's FFT size per cascade: 64, 128 or 256 (default 256; 128 for integrated GPUs, a quarter of the work)
          --water-reflection <0..4> the game's `water reflection`: what the water mirrors: 0 nothing (sky colour; R toggles), 1 sky and terrain, 2 + characters, 3 + buildings and features, 4 + trees, bushes and rocks (default 4; the game's own default is 2; Tab slider)
          --reflection-samples <n> samples per texel of the half-resolution reflection target (default 4; 1 disables MSAA; the device may cap it)
          --texture-quality <0..4> the game's `texture resolution gimping`: 0 Maximum, 1 High, 2 Medium, 3 Low, 4 Fugly; each step drops the top mip of compressed textures as they load (default 0, full size; the game's own default is 1)
          --shadow-quality <0|1|2> map side 1024/2048/4096 (default 2)   --shadow-range <u> (1000..9000, default 5000; with the Meitou shadows 1000..15000, default 10000)
          --shadow-range 0         shadows start off (the Tab slider turns them on)
          --shadow-filter <0|1|2>  Meitou shadow filter: 2 full (default), 1 the far cascades cheaper, 0 cheapest everywhere (low-end GPUs)
          --debug-shadows <n>      1 the four cascade maps, 2 the shadow term of the surfaces by cascade, 3 the term multiplied over the picture
          --no-gi                  no ray-traced probe GI (default on where the GPU has ray queries; the device is then made without them)
          --gi-debug <n>           ray-traced global illumination's debug views (needs ray queries; docs/render-gi.md): 1 a bounce ray's hit distance,
                                   2 one-bounce lighting on grey surfaces, 3 the traced scene seen from the eye (normals)
          (the shadows switch, F5: Meitou by default, view-fitted cascades with soft contact-hardening penumbrae and the terrain's shadow out to the horizon; --faithful shadows the game's CSM)
          --weather <name|auto>    auto (default): the weather scheduler (regions, seasons, wind) at the camera, from --day and --time; a WEATHER record's name forces that one (sky colour, fog, clouds, wind, heat haze, particles; "Default" is clear)
          --day <n>                the game day the weather schedule starts at (default 0)
          --clouds <0..1>          cloud density c (test override)
          --no-audio               no sound (the biome ambience and weather beds from the game's Wwise banks)   --audio-gain <dB> raise or lower the whole mix (0 = the game's levels)
          --no-particles           no weather particles  --particle-prewarm <s> seconds simulated before the first picture (default: the longest particle life; 0 starts empty)   --particle-density <x> the effects' emission rate x (0.1-1, the Tab slider)   --anisotropy <n> the most anisotropic filtering any texture gets (1-16, the Tab slider; 16 default)
          --low-end                potato-PC settings: short draw distances, no shadows, reflections, AO, ground relief, light shafts or anti-aliasing, 0.83 render scale, sparse grass and particles (options after it override)
          --benchmark-baseline     the benchmarks' settings: a 1920x1080 window, the Medium tier with no GI, ground parallax or tessellation, no upscaler, SMAA (the viewer: options after it override; the game: fixed)
          --no-vsync               start with vsync off (uncapped; Tab slider)
          --grass-density <x>      grass blades x this (0.1-2, default 1; Tab slider)
          --camera-code <hex>      start at a camera code (Ctrl+C in the viewer copies one), the world loaded round it
          --faithful <all|ao,dither,haze,aa,shadows,range,impostors,reach,water,particles,lod,gi,tonemap,shafts,heathaze,night,planetshine,stars,nightgrade,clouds,cloudshadows,cloudfog,interiorgi,charlod,skin,interiorlamps,icons,blood,bloodsurfaces,parallax,displace,weld,tracers,fog>   the game's look instead of Meitou's enhancements (default: all Meitou; --meitou <...> turns them back on; these ids also name the --ab switches)
          --show-keys              start with the key list overlay open (toggle with F10)
          --play-shots             start flying the saved camera keys (the F2 timeline) on a loop
          --log-spikes             print a line for every frame whose GPU time, or render-thread time over 20 ms, is over 1.5 x its median: stage times (GPU, render thread) and what it uploaded or rebuilt
          --fly-to <x>,<z>         with --screenshot: fly there first (streaming test, reports frame times), then take the picture
          --fly-benchmark <frames> offscreen, no window: fly the camera round a circle at 60 frames per second of wall time, print frame-time
                                   percentiles, the worst frames with their stage times and resident memory   --fly-radius <u> (12000)   --fly-speed <u per frame> (150)
                                   --fly-pipelined: no GPU wait per frame and no pacing, two frames in flight; reports the interval between frames
                                   --fly-csv <file>: per-frame times and video memory as CSV, the allocator owners every 30 frames in <file>.owners.csv
          --view <name>            a named camera/world/weather (swamp, swamp-rain, dust, hub): options after it override its own
          --bench-frames <n>       offscreen: measure n frames after warm-up and write per-stage statistics as JSON (--bench-out, default in %TEMP%)
          --ab <name>              with it: alternate side A (Meitou, on) and side B (Faithful, off) of a switch (ao, shadows, lod, particles, ... or occlusion, fog-cull), per frame
                                   or --ab-period <k> frames (the first quarter of each period, up to 16 frames, is dropped after each switch), then compare one still picture of each
          --bench-motion <m>       still (default), orbit (--bench-orbit px a frame), fly (--fly-radius, --fly-speed) or turn in place (0.5 degrees a frame)
          --no-gpu-lock            do not queue on %TEMP%\meitou-gpu.lock while measuring
          --bench-serial           wait for every frame in the benchmark (frame row = CPU + GPU added up; default: frames in flight, as the viewer)
          --bench-tris             with --bench-frames: after the timing count the triangles per pass (pipeline statistics queries) and histogram the main view's triangles by screen area, per category (debug device features; the timed frames are unchanged)
          --bench-compare <a.json> <b.json>   print two bench results side by side and exit
          --crowd <n>              place n generated characters of the start town round the start point (the character renderer's test; stills pose them at 0.35 s)
          --orbit-step <degrees>   with --screenshot: the camera orbits this much every frame (checks the upscaler's motion vectors)
          --terrain-sandbox [cliffs] [x,z]   the empty world round a flat spot whose ground is a field of tiles, one per terrain texture (cliffs: on a steep face)
          --field-tour <dir>       with --terrain-sandbox: a picture of every tile and an index.html (--tour-distance <u>, --tour-pitch <deg>)
          --relief <Name=value>    a GroundRelief or RockSmoothing setting for the run (repeatable), e.g. --relief TargetSlope=0.3
          --sway-step <seconds>    with --screenshot or a bench run: the grass sway advances this much every frame (checks the grass motion; 0.006 is about a frame at 165 fps)
          --fog <u>                distance where the haze is complete (default 250000; the far plane follows it)
          --material-distance <u>  beyond it the terrain shows the biomes' ground colour (default 30000, as the game)
          --terrain-error <px>     terrain LOD: the height error allowed near the eye, in pixels of the rendered picture (the reach switch, F8: default 16 in Meitou, 10 in Faithful; Tab slider)
          --wireframe
          --post <meitou|kenshi|off>   post-processing preset (default meitou), before the options below: HDR scene, SSAO
          --upscaler <off|taa|fsr|dlss|metalfx>   temporal upscaling (default taa in Meitou, off in Faithful; FSR and DLSS need the vendor library; MetalFX is the Metal backend's opt-in, else TAA)
          --render-scale <0.25..1|native|ultra-quality|quality|balanced|performance|ultra>   render size per axis (Meitou default 0.67, Quality, with an upscaler, else 1)
          --no-fxaa                the game's FXAA off when no upscaler runs (the game's own FXAA setting, default on)
          --smaa                   SMAA instead of FXAA when no upscaler runs
          --particle-divisor <0|1|2|4>   the weather's alpha and additive particles drawn at 1/n of the render size (0, the default: by sprite size; `--faithful particles` draws them full size)
          --no-heat-haze           no heat haze (the game's HeatHaze setting, default on: strength from the weather's `heat haze`)
          (ambient occlusion, dithering, light shafts, heat haze strength, the tone map, night grading and the rest are the switches above: --meitou / --faithful <id>; their strengths are Tab sliders)
        Keys: left drag orbit, right drag look around, wheel zoom, W/A/S/D free fly along the view, Q/E down/up (Shift faster, Ctrl slower),
          T textures, N normal maps, O objects, F foliage, X wireframe, V debug view,
          G water, R water reflections, , / . time of day -/+ 1 hour (the day follows midnight), [ / ] game day -/+ 1 (the weather catches up), \ cycle the forced weather (auto, then each WEATHER record), H print camera, Ctrl+C copy camera code, Ctrl+V go to camera code, P save screenshot, Tab settings sliders, Alt+Enter borderless full screen, Esc quit.
          F1 Meitou on / off (every switch Faithful, then back as they were; the Tab panel sets them one by one), Ctrl+F1 flips each switch in turn, 1 s apart (again stops); - / = exposure; F9 fps counter, F10 key list, F11 frame statistics, F12 profiler (gpu, cpu, off).
          Num1-9 cut to a camera key, Ctrl+Num1-9 save the camera as key 1-9, NumEnter fly through the keys (Shift: record PNG frames), Num0 loop, Num+ / Num- slower / faster, Num* clean picture (letterbox), Num/ time of day follows the keys, Num. list keys (Ctrl: delete all), PageUp / PageDown zoom lens, Home lens back to 50 deg, F2 camera timeline editor (Space play, Del delete key).
        """;

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

    #endregion

    #region Parsing

    /// <summary>
    /// The world options from the command line (a <c>--view</c> expanded in place first); null for <c>--help</c>.
    /// </summary>
    public static WorldOptions? Parse(string[] args)
    {
        var o = new WorldOptions();
        int viewAt = Array.LastIndexOf(args, "--view");
        if (viewAt >= 0 && viewAt + 1 < args.Length) o.View = args[viewAt + 1];
        args = NamedViews.Expand(args);   // --view <name> becomes its options, in place: the ones after it win
        o.SizeGiven = args.Contains("--size");

        var r = new OptionReader(args);
        while (r.MoveNext())
        {
            if (r.Current is "-h" or "--help") return null;
            bool known = ParsePlace(o, r) || ParseContent(o, r) || ParseQuality(o, r) || ParseRun(o, r) || o.Post.TryParse(r.Current, r.Next);
            if (!known) throw new ArgumentException($"unknown option {r.Current}");
        }

        if (o.Radius <= 0) throw new ArgumentException("--radius must be positive");
        if (Environment.GetEnvironmentVariable("MEITOU_HEADLESS") == "1") o.Headless = true;
        if (o.Headless && !o.SizeGiven) (o.Width, o.Height) = (1920, 1080);
        if (!o.SizeGiven && (o.BenchFrames > 0 || o.Ab is not null)) (o.Width, o.Height) = BenchSize;
        return o;
    }

    /// <summary>
    /// The command line as <see cref="Parse"/> walks it: the option at hand and the values after it.
    /// </summary>
    sealed class OptionReader(string[] args)
    {
        int _at = -1;

        public string Current => args[_at];

        public bool MoveNext() => ++_at < args.Length;

        public string Next() => _at + 1 < args.Length ? args[++_at] : throw new ArgumentException($"{Current} needs a value");

        public float Float() => float.Parse(Next(), CultureInfo.InvariantCulture);

        public int Int() => int.Parse(Next(), CultureInfo.InvariantCulture);

        public (double, double) Pair()
        {
            string option = Current;
            var p = Next().Split(',');
            if (p.Length != 2) throw new ArgumentException($"{option} needs <a>,<b>");
            return (double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Where and when: the region, the camera, the picture's size, the flight, the clock and the weather.
    /// </summary>
    static bool ParsePlace(WorldOptions o, OptionReader r)
    {
        switch (r.Current)
        {
            case "--world": break;
            case "--at": (o.X, o.Z) = r.Pair(); break;
            case "--zone":
                {
                    var (zi, zj) = r.Pair();
                    o.Zone = new ZoneCoordinate((int)zi, (int)zj);
                    break;
                }
            case "--town": o.Town = r.Next(); break;
            case "--radius": o.Radius = r.Float(); break;
            case "--yaw": o.Yaw = r.Float(); break;
            case "--pitch": o.Pitch = r.Float(); break;
            case "--distance": o.Distance = r.Float(); break;
            case "--camera-code":
                {
                    string code = r.Next();
                    o.CameraPose = WorldCameraCode.Decode(code)
                        ?? throw new ArgumentException($"--camera-code: '{code}' is not a camera code (the viewer's Ctrl+C)");
                    (o.X, o.Z) = (o.CameraPose.Value.Target.X, o.CameraPose.Value.Target.Z);   // loaded round it
                    break;
                }
            case "--screenshot": o.Screenshot = r.Next(); break;
            case "--size":
                {
                    var parts = r.Next().Split('x');
                    o.Width = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    o.Height = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    break;
                }
            case "--fly-to": (o.FlyToX, o.FlyToZ) = r.Pair(); break;
            case "--fly-benchmark": o.FlyBenchmark = r.Int(); break;
            case "--fly-radius": o.FlyRadius = r.Float(); break;
            case "--fly-speed": o.FlySpeed = r.Float(); break;
            case "--fly-pipelined": o.FlyPipelined = true; break;
            case "--fly-csv": o.FlyCsv = r.Next(); break;
            case "--time": o.Hour = r.Float(); break;
            case "--time-speed": o.TimeSpeed = r.Float(); break;
            case "--weather": o.Weather = r.Next(); break;
            case "--day": o.Day = r.Int(); break;
            default: return false;
        }
        return true;
    }

    /// <summary>
    /// What is loaded and how far it reaches: objects, foliage, water, particles, the crowd and the load caches.
    /// </summary>
    static bool ParseContent(WorldOptions o, OptionReader r)
    {
        switch (r.Current)
        {
            case "--no-objects": o.NoObjects = true; break;
            case "--no-foliage": o.NoFoliage = true; break;
            case "--no-load-cache": LoadCaches.Disabled = true; break;
            case "--range-large": o.LargeRange = r.Float(); break;
            case "--impostor-budget": o.ImpostorBudgetMb = r.Float(); break;
            case "--object-distance": o.ObjectDistance = r.Float(); break;
            case "--distant-range": o.DistantZones = r.Float(); break;
            case "--grass-density": o.GrassDensity = Math.Clamp(r.Float(), 0.1f, 2); break;
            case "--no-water": o.NoWater = true; break;
            case "--water-grid": o.WaterGrid = Math.Clamp(r.Int(), 64, 1024); break;
            case "--water-ocean": o.WaterOcean = r.Int() switch { <= 64 => 64, <= 128 => 128, _ => 256 }; break;
            case "--water-reflection": o.WaterReflection = Math.Clamp(r.Int(), 0, 4); break;
            case "--reflection-samples":
                {
                    int samples = r.Int();
                    o.ReflectionSamples = samples <= 1 ? 1 : samples;
                    break;
                }
            case "--no-particles": o.NoParticles = true; break;
            case "--particle-prewarm": o.ParticlePrewarm = Math.Max(r.Float(), 0); break;
            case "--particle-density": o.ParticleDensity = Math.Clamp(r.Float(), 0.1f, 1); break;
            case "--crowd": o.Crowd = r.Int(); break;
            default: return false;
        }
        return true;
    }

    /// <summary>
    /// How it looks: textures, shadows, GI, the Faithful / Meitou switches, filtering, clouds, fog, terrain detail and the debug views.
    /// </summary>
    static bool ParseQuality(WorldOptions o, OptionReader r)
    {
        switch (r.Current)
        {
            case "--texture-quality": o.TextureQuality = Math.Clamp(r.Int(), 0, Data.Textures.TextureQuality.Maximum); break;
            case "--shadow-quality": o.ShadowQuality = r.Int(); break;
            case "--shadow-filter": o.ShadowFilter = Math.Clamp(r.Int(), 0, 2); break;
            case "--shadow-range":
                {
                    float range = r.Float();
                    o.ShadowsOff = range <= 0;
                    if (!o.ShadowsOff)
                        o.ShadowRange = Math.Clamp(range, KenshiShadows.MinRange, CommandLineMaxShadowRange);
                    break;
                }
            case "--debug-shadows": o.DebugShadows = r.Int(); break;
            case "--gi-debug": o.GiDebug = Math.Clamp(r.Int(), 0, 3); break;
            case "--no-gi": o.Gi = false; break;
            case "--meitou": Enhancements.Apply(Switches(o), r.Next(), meitou: true); break;
            case "--faithful": Enhancements.Apply(Switches(o), r.Next(), meitou: false); break;
            case "--low-end": o.ApplyLowEnd(); break;
            case "--benchmark-baseline": o.ApplyBenchmarkBaseline(); break;
            case "--anisotropy": o.Anisotropy = (int)r.Float(); break;
            case "--clouds": o.Clouds = r.Float(); break;
            case "--fog": o.FogDistance = r.Float(); break;
            case "--material-distance": o.MaterialDistance = r.Float(); break;
            case "--terrain-error": o.TerrainError = r.Float(); break;
            case "--debug": o.Debug = r.Int(); break;
            case "--wireframe": o.Wireframe = true; break;
            default: return false;
        }
        return true;
    }

    /// <summary>
    /// How the run goes: audio, the window, keys and shots, the A/B and benchmark modes, and the frame logs.
    /// </summary>
    static bool ParseRun(WorldOptions o, OptionReader r)
    {
        switch (r.Current)
        {
            case "--no-audio": o.NoAudio = true; break;
            case "--audio-gain": o.AudioGainDb = r.Float(); break;
            case "--show-keys": o.ShowKeys = true; break;
            case "--play-shots": o.PlayShots = true; break;
            case "--no-vsync": o.VSync = false; break;
            case "--monitor": o.Monitor = r.Int(); break;
            case "--windowed": o.Windowed = true; break;
            case "--headless": o.Headless = true; break;
            case "--hidden-window": o.HiddenWindow = o.Windowed = true; break;
            case "--sway-step": o.SwayStep = r.Float(); break;
            case "--orbit-step": o.OrbitStep = r.Float() * MathF.PI / 180; break;
            case "--ab": o.Ab = r.Next(); break;
            case "--ab-period": o.AbPeriod = Math.Max(r.Int(), 1); break;
            case "--bench-frames": o.BenchFrames = Math.Max(r.Int(), 1); break;
            case "--bench-out": o.BenchOut = r.Next(); break;
            case "--bench-motion":
                o.BenchMotion = r.Next() is var motion && motion is "still" or "orbit" or "fly" or "turn"
                    ? motion : throw new ArgumentException("--bench-motion: still, orbit, fly or turn");
                break;
            case "--bench-orbit": o.BenchOrbit = r.Float(); break;
            case "--bench-serial": o.BenchSerial = true; break;
            case "--bench-tris": o.BenchTris = true; break;
            case "--bench-compare": o.BenchCompare = (r.Next(), r.Next()); break;
            case "--no-gpu-lock": o.NoGpuLock = true; break;
            case "--log-spikes": SpikeLog.Enabled = true; break;
            default: return false;
        }
        return true;
    }

    #endregion
}

/// <summary>
/// The scene's passes as a native host (wave 4, docs/renderer-native.md 4.5 and 6; phase 8 stage 3, 8.9): <see cref="Open"/> begins a rendering
/// of the scene's targets whose contents are secondaries, clearing them by load ops where asked, and hands the guests the targets and the scene's
/// state (the scene depth's convention, <see cref="DepthConvention"/>); their segments are queued (<see cref="GpuContext.Record"/>) or recorded at
/// once into secondaries of their own (sky, water, the grass, timestamps); <see cref="Close"/> records the queued jobs on the job threads and
/// executes
/// everything in order.
/// </summary>
sealed class SceneHost(IGpuContext ctx)
{
    IGpuCommandList? _cmd;

    public bool IsOpen => _cmd is not null;

    /// <summary>Opens the host on <paramref name="targets"/> (nothing when it is open already); its jobs count towards <paramref name="stage"/>.
    /// <paramref name="clearColour"/> / <paramref name="clearDepth"/>: the attachments are cleared by the rendering's load ops (to the colour, to
    /// <see cref="DepthConvention.Clear"/>).
    /// Without secondaries (<see cref="IGpuContext.RecordsSecondaries"/>) the guests record straight into its rendering.</summary>
    public void Open(int stage, PassTargets targets, ClearColour? clearColour = null, bool clearDepth = false)
    {
        if (_cmd is not null) { Stage(stage); return; }
        var list = ctx.BeginNative("scene");
        bool secondaries = ctx.RecordsSecondaries;
        var colour = clearColour is { } c ? targets.Colour with { Load = LoadOp.Clear, Clear = c } : targets.Colour;
        var depth = clearDepth ? targets.Depth with { Load = LoadOp.Clear, DepthClear = DepthConvention.Clear } : targets.Depth;
        // The extra target (the GI resolve's albedo) is cleared to 0 with the colour: no surface there takes the probes' light in post.
        var extra = clearColour is not null && !targets.Extra.IsNull ? targets.Extra with { Load = LoadOp.Clear, Clear = default } : targets.Extra;
        list.BeginRendering(new RenderingDesc(colour, depth, targets.Width, targets.Height, 0, 0, targets.ShadingRate, targets.ShadingRateTexel,
                extra), secondaries);
        ctx.BeginHostPassParallel(list, targets, DrawState.SceneDepth(targets.Formats, DrawState.Rgb),
                stage);   // alpha: the characters only (the SSAO character mask)
        _cmd = list;
    }

    /// <summary>
    /// The <see cref="StageClock"/> stage the guests' jobs count towards from now on.
    /// </summary>
    public void Stage(int stage)
    {
        if (_cmd is not null) ctx.SetPassStage(stage);
    }

    public void Close()
    {
        if (_cmd is not { } list) return;
        // Vulkan's queued guests execute into the rendering's secondaries; they must finish before EndRendering.
        ctx.EndHostPassParallel(list);
        list.EndRendering();
        ctx.EndHostPass(list);
        ctx.EndNative(list);
        _cmd = null;
    }
}

/// <summary>
/// The loaded world region: heights (the region fine, the whole world coarse), and where the camera starts.
/// </summary>
sealed class WorldScene : IDisposable
{
    /// <summary>
    /// The world heightmap file; null for the flat floor of the game's animation sandbox (<see cref="FlatHeight"/>).
    /// </summary>
    public TerrainHeightmap? Heightmap;
    public float FlatHeight;
    public required HeightWindow Window;

    /// <summary>
    /// Whole-world heights every <see cref="WorldFrame.CoarseStep"/>-th sample, (CoarseSize)² raw values.
    /// </summary>
    public required ushort[] Coarse;
    public required int CoarseSize;
    public SkyClock Clock = SkyClock.Fallback;
    public required Vector3 Focus;
    public required double X0, Z0, X1, Z1;
    public GameDatabase? Database;
    public WorldObjects? Objects;

    public float GroundAt(float x, float z) => Heightmap?.HeightAt(x, z) ?? FlatHeight;

    public void Dispose() => Heightmap?.Dispose();
}

/// <summary>
/// The world frame: loading the region, creating the renderers, and drawing a frame (shadows, reflection, sky, the scene,
/// water, post-processing). Shared by the game (<c>meitou</c>) and the viewer (<c>meitou-viewer --world</c>).
/// </summary>
static partial class WorldFrame
{
    #region Scene loading

    /// <summary>
    /// Heightmap step of the whole-world height grid behind the loaded region (2049² samples, 144 units apart).
    /// </summary>
    public const int CoarseStep = 8;

    /// <summary>
    /// How long <see cref="GpuState.Areas"/> holds its highest count before it may fall.
    /// </summary>
    public const int AreasHoldMs = 30000;

    /// <summary>
    /// The weather scheduler's random seed (the default of <c>meitou-tools weather</c>, so the two agree).
    /// </summary>
    const int WeatherSeed = 1;

    /// <summary>
    /// The water reflection's range, a multiple of the haze distance (the game's own <c>reflection range</c> is 0.6; the viewer reflects further; the
    /// Tab slider moves it).
    /// </summary>
    const float ReflectionRange = 3;

    /// <summary>
    /// The region round the options' place (<c>--at</c>, <c>--zone</c> or <c>--town</c>; the origin without one): its heights, the game data and its
    /// objects. Null when no town matches.
    /// </summary>
    public static WorldScene? Load(GameInstall install, WorldOptions o, GameDatabase? preloaded = null)
    {
        var watch = Stopwatch.StartNew();
        var map = TerrainHeightmap.Open(install);
        GameDatabase? db = null;
        if (o.Town is not null || !o.NoTextures || !o.NoObjects)
        {
            db = preloaded ?? GameDatabase.Load(LoadOrder.FromInstall(install));
            Console.WriteLine($"game data {db.Records.Count} records ({watch.ElapsedMilliseconds} ms)");
            LogIssues("game data", db.Issues);
        }

        if (StartPoint(install, o, db) is not { } start)
        {
            map.Dispose();
            return null;
        }
        var (x, z, y) = start;

        var (window, cells, step) = ReadWindow(map, o, x, z);
        var coarse = map.Downsample(CoarseStep, out int coarseSize);
        var (x0, z0) = window.WorldOf(0, 0);
        var (x1, z1) = window.WorldOf(cells, cells);
        var focus = new Vector3((float)x, y ?? map.HeightAt(x, z), (float)z);
        Console.WriteLine($"terrain   {cells}x{cells} cells, step {step} ({window.Spacing} units), "
            + $"world grid {coarseSize}² every {CoarseStep * WorldLayout.SampleSpacing} units; "
            + $"X {x0:0}..{x1:0}, Z {z0:0}..{z1:0} (zones {WorldLayout.ZoneOf(x0, z0)} .. {WorldLayout.ZoneOf(x1 - 1, z1 - 1)}) "
            + $"({watch.ElapsedMilliseconds} ms)");
        Console.WriteLine($"focus     {focus.X:0}, {focus.Y:0}, {focus.Z:0} in zone {WorldLayout.ZoneOf(focus.X, focus.Z)}");

        var scene = new WorldScene
        {
            Heightmap = map,
            Window = window,
            Coarse = coarse,
            CoarseSize = coarseSize,
            Focus = focus,
            X0 = x0,
            Z0 = z0,
            X1 = x1,
            Z1 = z1,
            Database = db,
        };
        if (db is not null) scene.Clock = SkyClock.FromDatabase(db);
        if (!o.NoObjects && db is not null) scene.Objects = WorldObjects.Load(install, db, map, x0, z0, x1, z1);
        Console.WriteLine($"loaded in {watch.ElapsedMilliseconds} ms");
        return scene;
    }

    /// <summary>
    /// Where the region is centred: <c>--at</c>, a <c>--zone</c>'s middle, or the placed town whose name contains <c>--town</c> (the shortest such
    /// name;
    /// its height too); the origin without any. Null when no town matches.
    /// </summary>
    static (double X, double Z, float? Y)? StartPoint(GameInstall install, WorldOptions o, GameDatabase? db)
    {
        if (o.X is { } ax && o.Z is { } az) return (ax, az, null);
        if (o.Zone is { } zone)
        {
            var (zx, zz) = WorldLayout.ZoneOrigin(zone);
            return (zx + WorldLayout.ZoneSize / 2.0, zz + WorldLayout.ZoneSize / 2.0, null);
        }
        if (o.Town is not { } name) return (0, 0, null);

        var levels = WorldLevelData.Load(install);
        var towns = levels.Towns().Select(t => (Place: t, Record: db!.Find(t.TownId)))
            .Where(t => t.Record is not null && t.Record.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Record!.Name.Length).ToList();
        if (towns.Count == 0)
        {
            Console.Error.WriteLine($"No placed town matches '{name}'.");
            return null;
        }

        foreach (var t in towns.Take(8))
            Console.WriteLine($"town      '{t.Record!.Name}' ({t.Record.StringId}) at {t.Place.Position.X:0}, {t.Place.Position.Z:0}");
        var pick = towns[0].Place.Position;
        return (pick.X, pick.Z, pick.Y);
    }

    /// <summary>
    /// The heights of the window round (<paramref name="x"/>, <paramref name="z"/>), <c>--radius</c> zones each way: a whole number of 64-cell blocks
    /// per
    /// side, every <c>step</c>-th sample, the smallest power of two that keeps the window to 2048 cells a side.
    /// </summary>
    static (HeightWindow Window, int Cells, int Step) ReadWindow(TerrainHeightmap map, WorldOptions o, double x, double z)
    {
        const int chunkCells = 64;
        double half = o.Radius * WorldLayout.ZoneSize;
        int cellsWanted = (int)Math.Ceiling(2 * half / WorldLayout.SampleSpacing);
        int step = 1;
        while (cellsWanted / step > 2048) step *= 2;
        int cells = (cellsWanted / step + chunkCells - 1) / chunkCells * chunkCells;

        var (sc, sr) = WorldLayout.ToSample(x - half, z - half);
        int c0 = (int)Math.Floor(sc / step) * step, r0 = (int)Math.Floor(sr / step) * step;
        return (map.ReadWindow(c0, r0, cells + 1, cells + 1, step), cells, step);
    }

    /// <summary>
    /// A flat world for the game's animation sandbox: the whole terrain at one height (a 256² window of heights round the origin and a flat
    /// whole-world grid),
    /// no heightmap file, no objects. The caller turns textures, objects, foliage and water off in <paramref name="o"/>.
    /// </summary>
    /// <summary>
    /// A load's data problems (a missing mod, a record defined twice or with another type, a level file left out...) to the log: a count by kind,
    /// then the first few.
    /// </summary>
    internal static void LogIssues(string what, List<GameDataIssue> issues)
    {
        if (issues.Count == 0) return;
        Console.WriteLine($"{what} {issues.Count} problems: {string.Join(", ", issues.GroupBy(i => i.Kind).Select(g => $"{g.Key} {g.Count()}"))}");
        const int shown = 20;
        foreach (var issue in issues.Take(shown))
            Console.WriteLine($"  {issue.Kind}: {issue}");
        if (issues.Count > shown) Console.WriteLine($"  ... and {issues.Count - shown} more");
    }

    public static WorldScene LoadFlat(WorldOptions o, GameDatabase db, ushort raw = 3344)
    {
        LogIssues("game data", db.Issues);
        const int cells = 256;   // 4608 units: one zone
        var (c0, r0) = ((int)Math.Round(WorldLayout.ToSample(0, 0).Column) - cells / 2, (int)Math.Round(WorldLayout.ToSample(0, 0).Row) - cells / 2);
        var flat = new ushort[(cells + 1) * (cells + 1)];
        Array.Fill(flat, raw);
        var window = new HeightWindow(c0, r0, 1, cells + 1, cells + 1, flat);
        int coarseSize = (WorldLayout.HeightmapSize - 1) / CoarseStep + 1;
        var coarse = new ushort[coarseSize * coarseSize];
        Array.Fill(coarse, raw);
        var (x0, z0) = window.WorldOf(0, 0);
        var (x1, z1) = window.WorldOf(cells, cells);
        float height = WorldLayout.RawToHeight(raw);
        return new WorldScene { Window = window, Coarse = coarse, CoarseSize = coarseSize, FlatHeight = height, Focus = new Vector3(0, height,
                0), X0 = x0, Z0 = z0, X1 = x1, Z1 = z1, Database = db, Clock = SkyClock.FromDatabase(db) };
    }

    public static (WorldCamera, WorldRenderOptions) Setup(WorldScene scene, WorldOptions o)
    {
        float radius = o.Radius * WorldLayout.ZoneSize;
        var camera = new WorldCamera
        {
            Target = scene.Focus,
            Yaw = (o.Yaw ?? 30) * MathF.PI / 180,
            Pitch = (o.Pitch ?? 35) * MathF.PI / 180,
            Distance = o.Distance ?? radius,
        };
        if (o.CameraPose is { } pose) WorldCameraCode.Apply(pose, camera);
        float terrainError = o.TerrainErrorFor(o.MeitouReach);
        var render = new WorldRenderOptions
        {
            Textures = !o.NoTextures,
            Objects = !o.NoObjects,
            Water = !o.NoWater,
            Wireframe = o.Wireframe ? 1 : 0,
            Debug = o.Debug,
            MaterialDistance = o.MaterialDistance,
            TerrainPixelError = terrainError
        };
        return (camera, render);
    }

    #endregion

    #region Gpu

    internal sealed class Gpu : IDisposable
    {
        public required TerrainRenderer Terrain;
        public required SkyRenderer Sky;
        public WaterRenderer? Water;

        /// <summary>
        /// The weather's wetness, dust and rain for every surface shader (null without a game database).
        /// </summary>
        public WeatherSurfaces? Surfaces;

        /// <summary>
        /// The placed fog volumes of <c>fogfeatures.dat</c> (null when the install has none).
        /// </summary>
        public FogVolumes? FogVolumes;

        /// <summary>
        /// This frame's weather fog spheres (<see cref="ParticleRenderer.CollectFogVolumes"/>), reused.
        /// </summary>
        public readonly List<FogVolumes.EffectFog> EffectFogs = [];

        /// <summary>
        /// The weather's particle effects (null while there are none: a clear weather, or <c>--no-particles</c>); <see cref="EnsureParticles"/> makes
        /// it.
        /// </summary>
        public ParticleRenderer? Particles;

        /// <summary>
        /// The movement marker the game interface shows after a ground move order (null in the viewer, or without <c>position.mesh</c>).
        /// </summary>
        public MarkerRenderer? Markers;

        /// <summary>
        /// The blood decals on the ground (null in the viewer, or with the game's <c>Blood</c> setting off) and the host's decals they draw
        /// (docs/game/blood-decals.md).
        /// </summary>
        public TerrainDecalRenderer? Decals;
        public TerrainDecalStore? DecalStore;

        /// <summary>
        /// luckyren Mode's flying bones and limbs (null until the host makes it) and the pieces it draws this frame (docs/game/luckyren.md).
        /// </summary>
        public DebrisRenderer? Debris;

        /// <summary>
        /// This frame's bolts stuck in characters (<see cref="CharacterRenderer.CollectBolts"/>), drawn with the debris.
        /// </summary>
        public readonly List<DebrisPiece> BoltPieces = [];
        public readonly List<DebrisPiece> DebrisPieces = [];

        /// <summary>
        /// The birds of this frame (<see cref="Meitou.Game"/>'s flocks), drawn as loose meshes after the debris.
        /// </summary>
        public readonly List<DebrisPiece> BirdPieces = [];

        /// <summary>
        /// The buildings' interior masks the terrain, foliage and map features are cut by (made with the first frame that has objects;
        /// docs/render-interior-cut.md).
        /// </summary>
        public InteriorMaskRenderer? InteriorMask;

        /// <summary>
        /// The navmesh debug view (null in the viewer; drawn while visible).
        /// </summary>
        public NavmeshOverlayRenderer? NavmeshOverlay;

        /// <summary>
        /// The pick & collision inspector's drawing (Meitou only): pick hulls and shapes, same kind of renderer as the navmesh view.
        /// </summary>
        public NavmeshOverlayRenderer? InspectorOverlay;

        /// <summary>
        /// The hover outline the game draws round the character under the mouse, and with Alt those near (null in the viewer); <see
        /// cref="HoverKeys"/> picks the characters by their draw-list keys, each with its colour (empty: none).
        /// </summary>
        public HoverOutlineRenderer? HoverOutline;
        public IReadOnlyList<(long Key, Vector4 Colour)> HoverKeys = [];

        /// <summary>
        /// The buildings <see cref="HoverOutline"/> outlines too (the selected and the hovered one), by placement key, above its floor height, in its
        /// colour (empty: none;
        /// nothing for one the interior view shows).
        /// </summary>
        public IReadOnlyList<(string Key, float Floor, Vector4 Colour)> OutlinedBuildings = [];

        /// <summary>
        /// A presentation cap on how far the frame reaches (the Meitou editor's bar, high above the world): the far plane and the shadow cascades end
        /// there, so the world below
        /// leaves the view; null in play.
        /// </summary>
        public float? ViewCap;

        /// <summary>
        /// The Tab panel's weather particles at 0 and its particle density, given to <see cref="Particles"/> every frame (it is made only with the
        /// first weather).
        /// </summary>
        public bool WeatherParticles = true;
        public float ParticleDensity = 1;

        /// <summary>
        /// Makes the particle renderer on demand (null with <c>--no-particles</c>).
        /// </summary>
        public Func<ParticleRenderer>? MakeParticles;

        /// <summary>
        /// The weather regions' cells, and the colour of the camera's cell the particle area was made for.
        /// </summary>
        public WeatherAreas? WeatherAreas;
        public int ParticleAreaColour = -1;
        public ReflectionPass? Reflection;
        public ShadowPass? Shadow;
        public int DebugShadows;

        /// <summary>
        /// The ray-traced global illumination's scene and debug view (null without <c>--gi-debug</c> or ray queries; docs/render-gi.md).
        /// </summary>
        internal Gi.GiScene? Gi;
        internal Gi.GiDebugPass? GiDebugPass;
        internal Gi.GiProbes? Probes;

        /// <summary>
        /// A backend-owned probe GI (Metal's native ray tracing); Vulkan uses <see cref="Gi"/> and <see cref="Probes"/> instead.
        /// </summary>
        internal Gi.IProbeGi? ProbeGi;
        internal WorldLamps? Lamps;

        /// <summary>
        /// The placed buildings' effects (campfires, torches, smoke; docs/formats/lights.md "Effects on buildings"), read on a worker, handed to the
        /// particles once read.
        /// </summary>
        internal Task<List<PlacedEffect>>? BuildingEffects;
        public int GiDebug;

        /// <summary>
        /// Whether the traced scene is updated (and the debug view drawn) this frame: the bench's gi-scene switch.
        /// </summary>
        public bool GiActive = true;

        /// <summary>
        /// The GI resolve's divisor (0 the lit fragments read the probes, 1 full size, 2 half; the bench's gi-resolve switch; docs/render-gi.md
        /// "Resolve").
        /// </summary>
        public int GiResolve = 2;

        /// <summary>
        /// The Vulkan post chain (its reflection and GI also read it); null on a backend with a seam-only chain.
        /// </summary>
        public PostProcess? Post;

        /// <summary>
        /// The chain <see cref="WorldFrame.Draw"/> runs: <see cref="Post"/> on Vulkan (also when a host sets only that), the host's seam chain on
        /// Metal.
        /// </summary>
        public IWorldPostChain? PostChain { get => _postChain ?? Post; set => _postChain = value; }
        IWorldPostChain? _postChain;
        public WorldObjectRenderer? Objects;
        public FoliageRenderer? Foliage;

        /// <summary>
        /// The characters (null without <c>--crowd</c> or a host that feeds them).
        /// </summary>
        public Characters.CharacterRenderer? Characters;
        public TerrainStreamer? Streamer;

        /// <summary>
        /// Where the camera is about to arrive (a character flight's end view's eye; null otherwise): the object and terrain texture streaming load
        /// there too.
        /// </summary>
        public Vector3? Ahead;

        /// <summary>
        /// The host flies the camera this frame (a character flight or squad tour hop under way, not its hold): the far shadow cascades are not drawn
        /// (docs/render-shadows.md "Flights").
        /// </summary>
        public bool CameraFlying;

        /// <summary>
        /// Points whose surroundings stay loaded besides the camera's (the player's characters, "Keep textures at characters"): object meshes and
        /// textures, terrain biome textures.
        /// </summary>
        public IReadOnlyList<Vector3> Kept = [];

        /// <summary>Places kept loaded: the eye's and each merged kept point's, held at its highest for <see cref="AreasHoldMs"/> (the merge flickers
        /// near
        /// its distance, and the grass store and terrain arrays reallocate when it grows). The texture, mesh and grass budgets scale with
        /// it.</summary>
        public int Areas = 1;
        public long AreasAt;

        /// <summary>
        /// Keeps the video memory under the budget by clamping the ranges and pausing the streaming (null with <c>MEITOU_VRAM_GUARD=0</c>).
        /// </summary>
        public VramGuard? Guard;

        /// <summary>
        /// The scene's native host (wave 4), made at the first frame.
        /// </summary>
        public SceneHost? Scene;

        /// <summary>
        /// The heat haze's target this frame (the weather's <c>heat haze</c> × strength 1 × the sun factor), for the statistics.
        /// </summary>
        public float HeatHazeTarget;

        /// <summary>
        /// Game hours since the load for the heat haze's animation, when the caller runs a game clock; null: real time at game speed 1.
        /// </summary>
        public double? GameHours;

        /// <summary>
        /// The weather scheduler hooked to the frame (null without a game database). The host sets <see cref="WorldWeather.Day"/>, <see
        /// cref="WorldWeather.GameSpeed"/> and <see cref="WorldWeather.Paused"/>.
        /// </summary>
        public WorldWeather? Weather;

        /// <summary>
        /// The weather at the camera this frame (what the other renderers read; <see cref="WeatherState.Clear"/> without a scheduler).
        /// </summary>
        public WeatherState WeatherState => Weather?.State ?? WeatherState.Clear;
        internal readonly Stopwatch HeatHazeClock = new();
        internal double HeatHazeHours;
        public void Dispose()
        {
            GiDebugPass?.Dispose();
            ProbeGi?.Dispose();
            Probes?.Dispose();
            Lamps?.Dispose();
            Gi?.Dispose();
            Streamer?.Dispose();
            Foliage?.Dispose();
            Characters?.Dispose();
            Objects?.Dispose();
            Particles?.Dispose();
            Markers?.Dispose();
            Decals?.Dispose();
            Debris?.Dispose();
            InteriorMask?.Dispose();
            NavmeshOverlay?.Dispose();
            InspectorOverlay?.Dispose();
            Water?.Dispose();
            Surfaces?.Dispose();
            Reflection?.Dispose();
            Shadow?.Dispose();
            Post?.Dispose();   // a seam-only chain (MetalPostChain) is owned by its host
            Sky.Dispose();
            Terrain.Dispose();
        }
    }

    #endregion

    #region Loading completion

    /// <summary>
    /// The end of loading: the GPU finishes the uploads queued so far (Vulkan records them all into the next frame: 1.7 GB at the
    /// benchmark start, a 300 ms stall two frames later), and one full, compacting collection runs now instead of a blocking gen2 collection a few
    /// frames into play (300+ ms measured), then gen2 collections only in the background while the world runs (DECISIONS 12).
    /// </summary>
    public static void FinishLoading(IGpuContext context)
    {
        context.Finish();
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        Console.WriteLine($"gc        {GcName()}, latency mode {System.Runtime.GCSettings.LatencyMode}");
    }

    /// <summary>
    /// Which collector runs: the runtime's server or workstation GC (docs/engine.md).
    /// </summary>
    public static string GcName() => System.Runtime.GCSettings.IsServerGC ? "server" : "workstation";

    #endregion

    #region Particles and GPU build

    /// <summary>
    /// Feeds the particle renderer the weather state's effect list, strength and wind (the renderer is made the first time a weather has effects; a
    /// weather
    /// without any empties it again). The effect groups are rebuilt only when the weather's list changes.
    /// </summary>
    static void UpdateParticles(Gpu gpu, WorldCamera camera)
    {
        if (gpu.Weather is not { } weather || gpu.MakeParticles is null) return;
        // In flight (KenshiCamera.FlightHeight, the platoons' gate) the weather's effects stop, as its fog does (SkyRenderer.WeatherFogScale).
        var input = gpu.Sky.WeatherFogScale == 0 ? WeatherEffectInput.None : weather.EffectInput();
        if (input.Effects.Count == 0 && gpu.Particles is null) return;
        var particles = gpu.Particles ??= gpu.MakeParticles();
        // The effects are placed in the camera's weather region: the area follows the cell the camera is in.
        if (gpu.WeatherAreas is { } areas)
        {
            var (cx, cz) = WeatherAreas.CellOf(camera.Eye.X, camera.Eye.Z);
            if (WeatherAreas.InMap(cx, cz) && areas.ColourOf(cx, cz) is var colour && colour != gpu.ParticleAreaColour)
            {
                gpu.ParticleAreaColour = colour;
                particles.World.Area = RegionArea.At(areas, camera.Eye.X, camera.Eye.Z);
            }
        }
        particles.SetWeather(input);
    }

    /// <summary>
    /// The particle renderer (reads the scripts and materials, makes the pass's program) if there is none yet; for whoever feeds it a weather's
    /// effect list.
    /// </summary>
    public static ParticleRenderer EnsureParticles(Gpu gpu, IGpuContext context, AssetLocator assets)
    {
        if (gpu.Particles is { } existing) return existing;
        return gpu.Particles = ParticleRenderer.Create(context, assets);
    }

    /// <summary>
    /// The renderer for <paramref name="scene"/>. <paramref name="step"/> is called before each of its steps (terrain, biomes, sky, water, objects,
    /// foliage, shadows) with the step's name: the game draws a loading frame there.
    /// </summary>
    /// <paramref name="wait"/> waits for the CPU work started on workers at the beginning (the terrain textures' and the water's files and bakes);
    /// the game draws loading frames while it waits.
    public static Gpu CreateGpu(IGpuContext context, GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o, bool interactive,
        Action<string>? step = null, Action<Task>? wait = null, bool terrainTextures = true, bool particlesEnabled = true, int objectLoadBudget = -1,
        Func<(ulong Used, ulong Budget)>? vramSample = null)
    {
        var b = new GpuBuild(context, install, scene, assets, o, step, wait);
        context.Samplers.MaxAnisotropy = o.Anisotropy;
        int layerSize = TerrainLayerSize(o);
        bool withTextures = !o.NoTextures && terrainTextures;

        // The files the terrain textures and the water read and bake (seconds of CPU work) are read on workers while the steps before them run.
        var colourEncode = withTextures && TerrainColourCache.Missing(install) ? Task.Run(() => EncodeColourTiles(install)) : null;
        var terrainSource = withTextures && scene.Database is { } textureDb ? Task.Run(() => TerrainTextures.Prepare(install, textureDb)) : null;
        var waterSource = !o.NoWater && scene.Database is { } waterDb ? Task.Run(() => WaterRenderer.Prepare(install, waterDb, assets,
                scene.Coarse)) : null;

        var (terrain, textures) = CreateTerrain(b, colourEncode, terrainSource, layerSize);
        var gpu = CreateSky(b, terrain);
        CreateWeather(b, gpu);
        if (particlesEnabled) CreateParticles(b, gpu, terrain);
        else b.Step("particles");
        CreateLamps(b, gpu);
        if (scene.Heightmap is not null)
            gpu.Streamer = new TerrainStreamer(install, terrain, textures, scene.Window.Step) { MaterialDistance = o.MaterialDistance };
        if (waterSource is not null) CreateWater(b, gpu, waterSource);
        if (scene.Objects is not null)
            CreateObjects(b, gpu, objectLoadBudget >= 0 ? objectLoadBudget : interactive ? 8 : 0);
        if (!o.NoFoliage && scene.Database is not null) CreateFoliage(b, gpu, terrain, interactive);
        CreateShadows(b, gpu);

        if (o.Crowd > 0 && scene.Database is not null)
            gpu.Characters = Characters.CrowdHarness.Create(context, install, scene, assets, o, interactive);
        if (gpu.Characters is { } crowd) (crowd.GeneratedLod, crowd.SkinShading) = (o.CharacterLod, o.SkinShading);
        gpu.DebugShadows = o.DebugShadows;

        b.Step("gi");
        if ((o.GiDebug > 0 || o.Gi) && context is GpuContext giContext) CreateGi(gpu, giContext, o);
        AttachGuard(gpu, context, vramSample);
        return gpu;
    }

    /// <summary>
    /// What the steps of one <see cref="CreateGpu"/> share: its inputs, the clock its log lines quote, and the wait for work started on workers.
    /// </summary>
    sealed class GpuBuild(IGpuContext context, GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions options, Action<string>? step,
        Action<Task>? wait)
    {
        public IGpuContext Context => context;
        public GameInstall Install => install;
        public WorldScene Scene => scene;
        public AssetLocator Assets => assets;
        public WorldOptions Options => options;
        public Stopwatch Watch { get; } = Stopwatch.StartNew();

        /// <summary>
        /// Names the next step (the game draws a loading frame).
        /// </summary>
        public void Step(string name) => step?.Invoke(name);

        /// <summary>
        /// The result of work started on a worker, the game drawing loading frames while it waits.
        /// </summary>
        public T Ready<T>(Task<T> work)
        {
            wait?.Invoke(work);
            return work.GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// The game's texture quality, set before any texture loads (the world's texture caches read it as they decode), and the terrain layers' size:
    /// their
    /// arrays have one size, so the Landscape group's drop (the game's textures are 2048²) lowers it instead of dropping mips per file.
    /// </summary>
    static int TerrainLayerSize(WorldOptions o)
    {
        Data.Textures.TextureQuality.Level = o.TextureQuality;
        int layerSize = Math.Max(2048 >> Data.Textures.TextureQuality.LevelsToDrop("terrain.dds", "Landscape"), 16);
        if (layerSize != 2048)
        {
            string label = Data.Textures.TextureQuality.Labels[o.TextureQuality];
            Console.WriteLine($"textures  quality {o.TextureQuality} ({label}): terrain layers {layerSize}² instead of 2048²");
        }
        return layerSize;
    }

    /// <summary>
    /// The terrain colour tiles BC3-encoded for the colour window, once (TerrainColourCache): a first start, or one after a tile changed, encodes
    /// them on workers.
    /// </summary>
    static (int Tiles, long Ms) EncodeColourTiles(GameInstall install)
    {
        var watch = Stopwatch.StartNew();
        int tiles = TerrainColourCache.EncodeMissing(install);
        return (tiles, watch.ElapsedMilliseconds);
    }

    /// <summary>
    /// The terrain's heights, then (with <paramref name="source"/>, the files read on a worker) its biome textures.
    /// </summary>
    static (TerrainRenderer Terrain, TerrainTextures? Textures) CreateTerrain(GpuBuild b, Task<(int Tiles, long Ms)>? colourEncode,
        Task<TerrainTextures.Source>? source, int layerSize)
    {
        var scene = b.Scene;
        b.Step("terrain");
        var terrain = new TerrainRenderer(b.Context, scene.Coarse, scene.CoarseSize, scene.Window);
        Console.WriteLine($"uploaded  terrain heights: {terrain.LevelCount} LOD levels, "
            + $"finest {terrain.FinestSpacing:0.#} units ({b.Watch.ElapsedMilliseconds} ms)");

        if (colourEncode is not null)
        {
            if (!colourEncode.IsCompleted) b.Step("colourmap");
            var (tiles, ms) = b.Ready(colourEncode);
            Console.WriteLine($"colour    {tiles} terrain colour tiles encoded to BC3 and cached in {ms} ms");
        }

        if (source is null || scene.Database is null) return (terrain, null);
        b.Step("biomes");
        var textures = TerrainTextures.Create(b.Context, b.Install, scene.Database, b.Assets, layerSize, source: b.Ready(source));
        foreach (var m in textures.Messages.Take(20)) Console.WriteLine($"warning   {m}");
        Console.WriteLine($"biomes    {textures.TotalBiomes} in the world, {textures.TotalPairs} texture pairs, "
            + $"{textures.Capacity} near slots of {layerSize}² and "
            + $"{textures.FarSlots} far of {textures.FarSize}², BC3+BC1, {textures.ArrayBytes / 1048576} MB ({b.Watch.ElapsedMilliseconds} ms)");
        terrain.SetTextures(textures);
        return (terrain, textures);
    }

    /// <summary>
    /// The frame's state with the terrain, the post chain (Vulkan's; Metal supplies its own), the sky and the placed fog volumes.
    /// </summary>
    static Gpu CreateSky(GpuBuild b, TerrainRenderer terrain)
    {
        var o = b.Options;
        b.Step("sky");
        // WorldFrame.Draw runs a post chain through IWorldPostChain. Vulkan's PostProcess is complete here; Metal supplies its
        // PostComposite/PostTaa slice as a host-constructed chain and leaves the Vulkan-only reflection and GI paths null.
        var post = b.Context is GpuContext vulkanPost ? new PostProcess(vulkanPost, o.Post) : null;
        var gpu = new Gpu { Terrain = terrain, Sky = new SkyRenderer(b.Context,
                b.Assets) { CloudCoverage = o.Clouds }, Post = post, PostChain = post };
        post?.LoadHeatHaze(b.Assets);

        // The placed fog volumes (docs/formats/fogfeatures.md): the swamp's fog and the like, part of the game's look.
        // Always made: the weather's fog spheres go through it too, with or without a fogfeatures.dat.
        gpu.FogVolumes = new FogVolumes(b.Context, o.NoFogFeatures ? [] : FogFeatures.Load(b.Install));
        if (post is not null) post.FogVrs = true;   // takes effect where the device supports fragment shading rates

        var sky = gpu.Sky;
        sky.HazeStrength = o.HazeStrength;
        sky.MeitouHaze = o.MeitouHaze;
        sky.NightAir = o.NightAir;
        sky.Planetshine = o.Planetshine;
        sky.PlanetshineStrength = o.PlanetshineStrength;
        sky.MeitouStars = o.MeitouStars;
        sky.LitClouds = o.MeitouClouds;
        sky.CloudShadows = o.CloudShadows;
        sky.CloudFog = o.CloudFog;
        return gpu;
    }

    /// <summary>
    /// The sky's world data and the weather scheduler (docs/formats/weather.md): auto, or one forced record through the same path.
    /// </summary>
    static void CreateWeather(GpuBuild b, Gpu gpu)
    {
        var o = b.Options;
        b.Step("weather");
        if (b.Scene.Database is not { } skyDb) return;

        gpu.Sky.LoadWorld(b.Install, skyDb);   // the ambient map and the CONSTANTS exposure band (docs/formats/lighting.md)
        var world = WeatherWorld.Create(skyDb, b.Install, WeatherSeed,
                WeatherTime.At(0));   // created on day 0; WorldWeather replays the schedule up to --day
        gpu.Weather = new WorldWeather(world, skyDb) { Day = o.Day ?? 0 };
        if (!o.AutoWeather)
        {
            if (!world.ForceWeather(o.Weather))
                throw new ArgumentException($"no weather named '{o.Weather}'; "
                    + $"known: {string.Join(", ", SkyWeather.Names(skyDb).Distinct().Take(12))} ...");
            gpu.Sky.Weather = SkyWeather.Find(skyDb, o.Weather) ?? SkyWeather.Default;
        }
        Console.WriteLine($"weather   {(o.AutoWeather ? $"scheduler, seed {WeatherSeed}, from day {o.Day ?? 0}" : $"forced {o.Weather}")}");

        // Wetness, dust and rain for the surface shaders come from the weather state every frame (Draw).
        gpu.Surfaces = new WeatherSurfaces(b.Context, b.Assets);
    }

    /// <summary>
    /// The particles: the weather's effects (rain, ash...) follow the weather state (UpdateParticles); the map's effect placers are always on
    /// (docs/formats/weather.md "Effect placers on the map"), so the particle renderer exists from the start whatever the weather.
    /// </summary>
    static void CreateParticles(GpuBuild b, Gpu gpu, TerrainRenderer terrain)
    {
        var o = b.Options;
        b.Step("particles");
        if (o.NoParticles || b.Scene.Database is not { } effectsDb) return;

        var watch = Stopwatch.StartNew();
        var particles = EnsureParticles(gpu, b.Context, b.Assets);
        particles.PrewarmSeconds = o.ParticlePrewarm;
        gpu.ParticleDensity = o.ParticleDensity;
        particles.World.GroundHeight = terrain.HeightAt;   // main thread only: the height grid swaps as the terrain streams
        // for a weather warmed on a worker: the grid as it is when it starts
        particles.World.FreezeGroundHeight = () => terrain.Snapshot().HeightAt;
        gpu.WeatherAreas = WeatherAreas.Load(b.Install);
        particles.World.Area = RegionArea.At(gpu.WeatherAreas, b.Scene.Focus.X, b.Scene.Focus.Z);

        var placers = MapEffectPlacers.Find(effectsDb, MapFeatureFile.Open(b.Install));
        particles.SetPlacers(placers);
        gpu.MakeParticles = () => particles;
        Console.WriteLine($"particles {particles.Library.Systems.Count} systems read; {placers.Count} map placers "
            + $"in {particles.PlacerGroups.Count} groups ({watch.ElapsedMilliseconds} ms)");
    }

    /// <summary>
    /// The lamps (docs/render-lights.md), including room lights, and the buildings' effects, read on workers from one level load.
    /// </summary>
    static void CreateLamps(GpuBuild b, Gpu gpu)
    {
        if (b.Scene.Database is not { } lampDb || b.Scene.Heightmap is not { } lampMap) return;
        var install = b.Install;
        var levels = new Lazy<(WorldLevelData Levels, BuildingLayouts Layouts)>(() =>
        {
            var l = WorldLevelData.Load(install, includeInteriors: true);
            var layouts = new BuildingLayouts(lampDb, l.Interiors);
            l.ApplyBuildingSwaps(lampDb, layouts);   // as the scene's objects: a swapped building or piece of furniture brings its own lamps
            ResidentRuins.Apply(lampDb, l, layouts);
            return (l, layouts);
        });

        gpu.Lamps = new WorldLamps(b.Context, () => WorldLights.ForWorld(lampDb, levels.Value.Levels, lampMap.HeightAt, levels.Value.Layouts))
        {
            InteriorAllDay = b.Options.InteriorLamps,
        };
        if (gpu.MakeParticles is not null)
            gpu.BuildingEffects = Task.Run(() => BuildingEffects.ForWorld(lampDb, levels.Value.Levels, lampMap.HeightAt, levels.Value.Layouts));
    }

    /// <summary>
    /// The water and its reflection pass, from the files read on a worker (<paramref name="source"/>).
    /// </summary>
    static void CreateWater(GpuBuild b, Gpu gpu, Task<WaterRenderer.Source> source)
    {
        var o = b.Options;
        var messages = new List<string>();
        b.Step("water");
        // The Meitou water's FFT, shore field and reflection are seam-neutral. Metal packs the two biome
        // parameter maps into one array and drops refraction (its scene copy is still Vulkan-only), which
        // leaves the Meitou fragment exactly at Metal's 16-sampler stage limit.
        // Vulkan always makes the Meitou water, so the Faithful / Meitou switch can turn it on while the game runs; another backend makes
        // it only when the run starts with it.
        bool meitouWater = o.MeitouWater, makeMeitou = meitouWater || b.Context is GpuContext;
        gpu.Water = WaterRenderer.Create(b.Context, b.Install, b.Scene.Database!, b.Assets, gpu.Sky, messages, b.Scene.Coarse, o.WaterGrid,
                o.WaterOcean,
            b.Ready(source), meitouWaves: makeMeitou, shoreField: makeMeitou);
        gpu.Water.Meitou = meitouWater;

        b.Step("reflections");
        gpu.Reflection = new ReflectionPass(b.Context) { Level = o.WaterReflection, Range = ReflectionRange, Samples = o.ReflectionSamples };
        foreach (var m in messages) Console.WriteLine($"warning   {m}");
        Console.WriteLine($"water     at height {WorldWater.Height} ({b.Watch.ElapsedMilliseconds} ms)");
    }

    /// <summary>
    /// The objects. Landmarks (huge objects, drawn to their own distance) are kept apart from the zones only when the viewer starts with them
    /// (Meitou reach, distance above 0).
    /// </summary>
    static void CreateObjects(GpuBuild b, Gpu gpu, int loadBudget)
    {
        var o = b.Options;
        b.Step("objects");
        float landmarks = o.LandmarkDistanceFor(o.MeitouReach);
        gpu.Objects = new WorldObjectRenderer(b.Context, b.Assets, b.Scene.Objects!, landmarks: landmarks > 0)
        {
            ObjectDistance = o.ObjectDistanceFor(o.MeitouReach),
            LandmarkDistance = landmarks,
            DistantRange = o.DistantZones * WorldLayout.ZoneSize,
            NoDistant = o.DistantZones <= 0,
            LoadBudget = loadBudget,
            SortNearestFirst = o.MeitouReach,
            LodBias = 1 / o.ObjectLod,
        };
        Console.WriteLine($"objects   GPU ready ({b.Watch.ElapsedMilliseconds} ms)");
    }

    /// <summary>
    /// The foliage, with its ranges, LOD, impostors and grass density from the options.
    /// </summary>
    static void CreateFoliage(GpuBuild b, Gpu gpu, TerrainRenderer terrain, bool interactive)
    {
        var o = b.Options;
        b.Step("foliage");
        var levels = b.Scene.Objects?.Levels ?? WorldLevelData.Load(b.Install);
        var f = gpu.Foliage = new FoliageRenderer(b.Context, b.Install, b.Scene.Database!, levels, b.Assets);
        if (!interactive) f.SwaySeconds = 0;   // offscreen pictures and benchmarks: the grass holds still, so a picture repeats exactly
        f.Terrain = terrain;
        f.MeitouRange = o.MeitouRange;

        // --range-large moves the medium and small ranges with it, at their default ratios to the large one.
        if (o.LargeRange is { } largeRange)
        {
            float k = largeRange / f.LargeRange;
            (f.SmallRange, f.MediumRange, f.LargeRange) = (f.SmallRange * k, f.MediumRange * k, largeRange);
        }

        f.Lod = o.FoliageLod;
        (f.Impostors, f.ImpostorDistance, f.ImpostorBudgetMb) = (o.Impostors, o.ImpostorDistance ?? f.ImpostorDistance,
                o.ImpostorBudgetMb ?? f.ImpostorBudgetMb);
        if (o.LargeImpostorDistance is { } largeImpostor) f.LargeImpostorDistance = largeImpostor;
        if (o.GrassDensity is { } grassDensity) f.GrassDensitySetting = grassDensity;
        Console.WriteLine($"foliage   catalog and shaders ready ({f.LoadMs:0} ms)");
    }

    /// <summary>
    /// The game's CSM mode (docs/formats/shadows.md): four cascades in one atlas of the <c>shadow quality</c> side, out to <c>Shadow Range</c>.
    /// </summary>
    static void CreateShadows(GpuBuild b, Gpu gpu)
    {
        var o = b.Options;
        b.Step("shadows");
        var settings = new ShadowSettings(KenshiShadows.MapSize(o.ShadowQuality), o.ShadowRangeFor(o.MeitouShadows));
        var shadow = gpu.Shadow = new ShadowPass(b.Context, b.Assets)
        {
            Settings = settings,
            Meitou = o.MeitouShadows,
            FilterQuality = o.ShadowFilter,
            Enabled = !o.ShadowsOff,
        };
        if (!shadow.HasNoise)
            Console.WriteLine($"warning   shadows: {KenshiShadows.NoiseTexture} not found, the receiver's jitter is a hash");
        shadow.SetTerrain(b.Scene.Coarse, b.Scene.CoarseSize);   // the Meitou shadows' terrain shadow beyond the range

        string layout = shadow.Meitou ? "Meitou: a layer each" : "Faithful: tiles of one layer";
        Console.WriteLine($"shadows   {settings.MapSize}² atlas, {shadow.CascadeCount} cascades of {settings.TileSize}² "
            + $"({layout}), range {settings.Range:0}");
    }

    /// <summary>
    /// The ray-traced global illumination: its scene, the debug view and the probes, on a device with ray queries.
    /// </summary>
    static void CreateGi(Gpu gpu, GpuContext context, WorldOptions o)
    {
        if (!context.Device.HasRayQuery)
        {
            Console.WriteLine("gi        off: the device has no ray queries (VK_KHR_ray_query)");
            return;
        }

        gpu.Gi = new Gi.GiScene(context);
        if (o.GiDebug > 0)
        {
            gpu.GiDebugPass = new Gi.GiDebugPass(context);
            gpu.GiDebug = o.GiDebug;
            Console.WriteLine($"gi        debug view {o.GiDebug}");
        }
        if (o.Gi)
        {
            gpu.Probes = new Gi.GiProbes(context) { Enabled = o.GiProbes, InteriorEnabled = o.InteriorGi };
            Console.WriteLine($"gi        {gpu.Probes.Describe()}");
        }
    }

    /// <summary>
    /// The memory-pressure guard (VramGuard), shared by the renderers that stream: MEITOU_VRAM_GUARD=0 leaves it off. Vulkan samples its allocator;
    /// another backend supplies the sample (Metal's device allocation and conservative budget).
    /// </summary>
    static void AttachGuard(Gpu gpu, IGpuContext context, Func<(ulong Used, ulong Budget)>? vramSample)
    {
        if (Environment.GetEnvironmentVariable("MEITOU_VRAM_GUARD") == "0") return;
        VramGuard? guard = context is GpuContext vulkanGuard
            ? new VramGuard(vulkanGuard.Device.VideoMemory) { Allocator = vulkanGuard.Device.Allocator }
            : vramSample is not null ? new VramGuard(vramSample) : null;
        if (guard is null) return;

        gpu.Guard = guard;
        if (gpu.Objects is not null) gpu.Objects.Guard = guard;
        if (gpu.Foliage is not null) gpu.Foliage.Guard = guard;
        if (gpu.Shadow is not null) gpu.Shadow.Guard = guard;
        if (gpu.Characters is not null) gpu.Characters.Guard = guard;
    }

    // The Tab panel: draw distances and LOD.

    #endregion

    #region Settings labels

    /// <summary>
    /// The upscaler sliders' labels (the game keeps command-line upscaler options over the saved ones).
    /// </summary>
    // The anti-aliasing slider's label changes with its numbering ("0 off" 2026-10-09, SMAA 2026-10-19), so a value saved under an old one is not
    // misread.
    public static readonly string[] UpscalerSliders = ["Anti-aliasing: 0 off 1 FXAA 2 SMAA 3 TAA 4 FSR 5 DLSS", "Render scale (upscaler)",
            "Upscaler sharpness"];

    /// <summary>
    /// DLSS's render preset (<see cref="UpscaleOptions.DlssPresets"/>); not an upscaler option of the command line, so the saved one always applies.
    /// </summary>
    public const string DlssPresetSlider = "DLSS preset";

    /// <summary>
    /// The anti-aliasing slider's value for the options: 0 none, 1 FXAA, 2 SMAA, 3.. the upscaler kinds (TAA, FSR, DLSS).
    /// </summary>
    public const string FilteringSlider = "Anisotropic filtering (texture filtering)";
    public const string TextureQualitySlider = "Texture quality 0-4 (game)";
    public static readonly string[] ParticleSliders = ["Weather particles: 0 off 1 quarter 2 half 3 auto 4 full", "Particle density x"];
    // The Tab panel sliders' labels: the meitou.user.json keys, and the launcher's settings schema names them (keep them).
    public const string ObjectDistanceSlider = "Object draw distance", GrassDensitySlider = "Grass density x",
        WaterReflectionSlider = "Water reflection 0-4 (game)", ShadowDistanceSlider = "Shadow distance (game 1k-9k, 0 off)",
        ShadowMapSlider = "Shadow map size (game quality 0-2)", GroundReliefQualitySlider = "Ground relief quality",
        ShaftQualitySlider = "Light shafts quality";
    public const string
        LandmarkDistanceSlider = "Landmark distance (Meitou)",
        DistantTownsSlider = "Distant towns (zones)",
        CharacterDistanceSlider = "Character draw distance",
        ObjectLodSlider = "Object LOD distance x",
        FoliageDistanceSlider = "Foliage draw distance x",
        FoliageLodSlider = "Foliage LOD distance x (lod Meitou)",
        LargeFoliageRangeSlider = "Large foliage range (F6 Meitou)",
        ImpostorDistanceSlider = "Impostor distance (F7 Meitou)",
        LargeImpostorDistanceSlider = "Large impostor distance (F7 Meitou)",
        MediumFoliageRangeSlider = "Medium foliage range",
        SmallFoliageRangeSlider = "Small foliage range",
        GrassDistanceSlider = "Grass draw distance x",
        ReflectionRangeSlider = "Reflection range x (game 0.6)",
        WaterClaritySlider = "Water clarity x (Meitou)",
        FarWaterClaritySlider = "Far water clarity (Meitou)",
        WaterClearDistanceSlider = "Water see-through distance (Meitou, game 4000)",
        TerrainErrorSlider = "Terrain detail: error px (less = finer)",
        GroundReliefDepthSlider = "Ground relief depth (cm, typical ground)",
        GroundDisplaceDistanceSlider = "Ground displacement distance (m)",
        GroundParallaxDistanceSlider = "Ground parallax distance (m)",
        ShadowFilterSlider = "Shadow filter 0-2 (Meitou; 0 low-end)",
        HazeStrengthSlider = "Haze strength (1 = game)",
        PlanetshineStrengthSlider = "Planetshine strength (x physical)",
        CloudHeightSlider = "Cloud height (Meitou)",
        AmbientStrengthSlider = "Ambient light strength (1 = game)",
        NightAmbientStrengthSlider = "Night ambient light strength (1 = 1.2x game)",
        ToneMapSlider = "Tone map (0 = game)",
        ToneMixSlider = "Hybrid tone map: ACES share",
        GradingSlider = "Grading (0 = game)",
        HeatHazeStrengthSlider = "Heat haze strength (1 = game)",
        BloomStrengthSlider = "Bloom strength (0 = game)",
        NightGradeStrengthSlider = "Night grading strength (0 = game)",
        ShaftStrengthSlider = "Light shafts strength",
        ShaftAirSlider = "Light shafts air (0 = none)",
        ShaftAirDawnSlider = "Light shafts air at dawn and dusk (x)";

    /// <summary>
    /// The Tab panel switches that are not Faithful / Meitou ones (saved as <c>Meitou: &lt;label&gt;</c> like those).
    /// </summary>
    public const string
        FifthCascadeSwitch = "Shadow cascade 5 (Meitou)",
        BloomSwitch = "Bloom",
        // The ground relief's Faithful / Meitou switches (Enhancements "parallax", "displace"), by name.
        GroundParallaxSwitch = "Ground parallax",
        GroundDisplaceSwitch = "Ground displacement (tessellation)",
        RockSmoothingSwitch = "Rock smoothing (tessellation)",
        OutlineRefineSwitch = "Terrain outline refinement (tessellation)";

    /// <summary>
    /// The weather particles slider's position for the renderer's state: 0 off, 1 quarter, 2 half, 3 by sprite size (auto), 4 full size.
    /// </summary>
    public static int WeatherParticlesSlider(bool weatherOn, PostOptions o) =>
        !weatherOn ? 0 : !o.LowResParticles || o.ParticleDivisor == 1 ? 4 : o.ParticleDivisor switch { 4 => 1, 2 => 2, _ => 3 };
    public static int AntiAliasingSlider(PostOptions o) => o.Upscale.Kind != UpscalerKind.Off ? (int)o.Upscale.Kind + 2 : o.Smaa ? 2 : o.Fxaa ? 1 : 0;

    /// <summary>
    /// The upscaler a value of the anti-aliasing slider picks (<see cref="UpscalerKind.Off"/> for none, FXAA and SMAA).
    /// </summary>
    public static UpscalerKind AntiAliasingKind(float v) =>
        (int)MathF.Round(v) is var i && i >= 3 ? (UpscalerKind)Math.Min(i - 2, (int)UpscalerKind.Dlss) : UpscalerKind.Off;

    #endregion

    #region Settings panel

    /// <summary>
    /// The Faithful / Meitou switches over the live renderer (the game's Shift+F keys, the viewer's F keys, the Tab checkboxes): they change the
    /// options and, once made, the renderer's parts; the distances follow the mode's defaults unless a Tab slider moved them.
    /// </summary>
    internal static IReadOnlyList<Enhancement> LiveSwitches(WorldOptions o, Func<Gpu?> gpu, Func<WorldCamera?> camera,
            Func<WorldRenderOptions?> render) =>
        Enhancements.Create(o.Post, () => gpu()?.Sky.MeitouHaze ?? o.MeitouHaze, v => { o.MeitouHaze = v; if (gpu() is { } g) g.Sky.MeitouHaze = v; },
            () => gpu()?.Shadow?.Meitou ?? o.MeitouShadows, v =>
            {
                // The shadow distance follows the mode's default unless the Tab slider moved it.
                bool was = o.MeitouShadows;
                o.MeitouShadows = v;
                if (gpu()?.Shadow is not { } s) return;
                if (MathF.Abs(s.Settings.Range - o.ShadowRangeFor(was)) < 1) s.Settings = s.Settings with { Range = o.ShadowRangeFor(v) };
                s.Meitou = v;
            },
            () => gpu()?.Foliage?.MeitouRange ?? o.MeitouRange, v => { o.MeitouRange = v; if (gpu()?.Foliage is { } f) f.MeitouRange = v; },
            () => gpu()?.Foliage?.Impostors ?? o.Impostors, v => { o.Impostors = v; if (gpu()?.Foliage is { } f) f.Impostors = v; },
            () => o.MeitouReach, v =>
            {
                // The draw distances follow the mode's defaults unless a Tab slider (or an option) moved them.
                bool was = o.MeitouReach;
                o.MeitouReach = v;
                if (gpu()?.Objects is { } ob)
                {
                    if (MathF.Abs(ob.ObjectDistance - o.ObjectDistanceFor(was)) < 1) ob.ObjectDistance = o.ObjectDistanceFor(v);
                    if (MathF.Abs(ob.LandmarkDistance - o.LandmarkDistanceFor(was)) < 1) ob.LandmarkDistance = o.LandmarkDistanceFor(v);
                }
                if (gpu()?.Objects is { } sortObjects) sortObjects.SortNearestFirst = v;
                if (render() is { } r && MathF.Abs(r.TerrainPixelError - o.TerrainErrorFor(was)) < 1e-3f) r.TerrainPixelError = o.TerrainErrorFor(v);
            },
            () => gpu()?.Water?.Meitou ?? o.MeitouWater, v => { o.MeitouWater = v; if (gpu()?.Water is { } w) w.Meitou = v; },
            () => gpu()?.Foliage?.Lod ?? o.FoliageLod, v => { o.FoliageLod = v; if (gpu()?.Foliage is { } f) f.Lod = v; },
            // Without probes (--no-gi, no ray queries) the switch reads off: the picture has the flat ambient whatever it is set to.
            () => gpu() is { } giGpu ? giGpu.Probes?.Enabled ?? giGpu.ProbeGi?.Enabled ?? false : o.GiProbes,
                v => { o.GiProbes = v; if (gpu()?.Probes is { } p) p.Enabled = v; if (gpu()?.ProbeGi is { } m) m.Enabled = v; },
            () => gpu()?.Sky.NightAir ?? o.NightAir, v => { o.NightAir = v; if (gpu() is { } g) g.Sky.NightAir = v; },
            () => gpu()?.Sky.Planetshine ?? o.Planetshine, v => { o.Planetshine = v; if (gpu() is { } g) g.Sky.Planetshine = v; },
            () => gpu()?.Sky.MeitouStars ?? o.MeitouStars, v => { o.MeitouStars = v; if (gpu() is { } stars) stars.Sky.MeitouStars = v; },
            () => gpu()?.Sky.LitClouds ?? o.MeitouClouds, v => { o.MeitouClouds = v; if (gpu() is { } g) g.Sky.LitClouds = v; },
            () => gpu()?.Sky.CloudShadows ?? o.CloudShadows, v => { o.CloudShadows = v; if (gpu() is { } g) g.Sky.CloudShadows = v; },
            () => gpu()?.Sky.CloudFog ?? o.CloudFog, v => { o.CloudFog = v; if (gpu() is { } g) g.Sky.CloudFog = v; },
            () => gpu()?.Probes?.InteriorEnabled ?? gpu()?.ProbeGi?.InteriorEnabled ?? o.InteriorGi,
                v => { o.InteriorGi = v; if (gpu()?.Probes is { } p) p.InteriorEnabled = v; if (gpu()?.ProbeGi is { } m) m.InteriorEnabled = v; },
            () => gpu()?.Characters?.GeneratedLod ?? o.CharacterLod, v => { o.CharacterLod = v; if (gpu()?.Characters is { } c) c.GeneratedLod = v; },
            () => gpu()?.Characters?.SkinShading ?? o.SkinShading, v => { o.SkinShading = v; if (gpu()?.Characters is { } c) c.SkinShading = v; },
            () => gpu()?.Lamps?.InteriorAllDay ?? o.InteriorLamps, v => { o.InteriorLamps = v; if (gpu()?.Lamps is { } l) l.InteriorAllDay = v; },
            () => gpu()?.Characters?.EnhancedIcons ?? o.MeitouIcons, v => { o.MeitouIcons = v; if (gpu()?.Characters is { } c) c.EnhancedIcons = v; },
            () => gpu()?.Decals?.Meitou ?? o.MeitouBlood, v => { o.MeitouBlood = v; if (gpu()?.Decals is { } d) d.Meitou = v; },
            () => gpu()?.Decals?.Surfaces ?? o.MeitouBloodSurfaces, v => { o.MeitouBloodSurfaces = v; if (gpu()?.Decals is { } d) d.Surfaces = v; },
            () => o.MeitouTracers, v => o.MeitouTracers = v);

    /// <summary>
    /// The Tab settings panel over <paramref name="g"/>'s renderers: the sliders (the viewer's clock, window and the render settings), the Faithful /
    /// Meitou switches as checkboxes, the cache actions, the texture column and the weather choices.
    /// </summary>
    public static SettingsPanel CreateSettingsPanel(DebugOverlay ui, Gpu g, WorldRenderOptions r, Func<float>? getHour = null,
            Action<float>? setHour = null,
        Func<bool>? getVSync = null, Action<bool>? setVSync = null, IReadOnlyList<Enhancement>? switches = null,
        Func<float>? getTimeSpeed = null, Action<float>? setTimeSpeed = null, Func<bool>? getFullScreen = null, Action<bool>? setFullScreen = null,
        IReadOnlyList<PanelChoice>? extraChoices = null)
    {
        var sliders = new List<Slider>();
        AddViewerSliders(sliders, getHour, setHour, getTimeSpeed, setTimeSpeed, getVSync, setVSync, getFullScreen, setFullScreen);
        if (g.Objects is { } objects) AddObjectSliders(sliders, objects);
        if (g.Characters is { } characters) sliders.Add(CharacterDistance(characters));
        if (g.Foliage is { } foliage) AddFoliageSliders(sliders, foliage);
        AddWaterSliders(sliders, g);
        AddTerrainSliders(sliders, g, r);
        var fifthCascade = g.Shadow is { } shadow ? AddShadowSliders(sliders, shadow) : null;
        AddSkySliders(sliders, g.Sky);
        if (g.Post is { } post) AddPostSliders(sliders, g, post);

        // Anisotropic filtering (docs/render-post.md "Texture filtering"): the most any texture is sampled with; the world's textures ask 8x,
        // so 8x and 16x look the same there.
        var samplers = g.Sky.Gpu.Samplers;
        sliders.Add(new Slider(FilteringSlider, 0, 4, () => MathF.Log2(samplers.MaxAnisotropy),
                v => samplers.MaxAnisotropy = 1 << (int)MathF.Round(v), "0",
            Text: v => (1 << (int)MathF.Round(v)) + "x" + (MathF.Round(v) >= 3 ? " (as the game)" : "")));

        var toggles = SettingsToggles(g, switches, fifthCascade);
        var actions = SettingsActions(g);
        var side = TextureSliders();

        // The camera region's weathers of its season (the current one marked): a click starts one now.
        var choices = new List<PanelChoice>(extraChoices ?? []);
        if (g.Weather is { } weather)
            choices.Add(new PanelChoice(weather.ChoiceLabel, weather.Choices, weather.Current, weather.Choose));
        return new SettingsPanel(ui, "Settings   (Tab hides this)", sliders, toggles, "Meitou improvements (unticked: as the game)", actions, side,
                choices);
    }

    /// <summary>
    /// The viewer's own rows; the game passes none of them (its clock, vsync and window mode are its own settings).
    /// </summary>
    static void AddViewerSliders(List<Slider> sliders, Func<float>? getHour, Action<float>? setHour, Func<float>? getTimeSpeed,
            Action<float>? setTimeSpeed,
        Func<bool>? getVSync, Action<bool>? setVSync, Func<bool>? getFullScreen, Action<bool>? setFullScreen)
    {
        // The time of day (the `--time` option and the , / . keys), to the minute.
        if (getHour is not null && setHour is not null)
            sliders.Add(new Slider("Time of day", 0, 24 - 1 / 60f, getHour, v => setHour(MathF.Round(v * 60) / 60), Text: TimeText));

        // How fast the clock runs (`--time-speed`): game hours per real minute, 0 stopped.
        if (getTimeSpeed is not null && setTimeSpeed is not null)
            sliders.Add(new Slider("Time speed", 0, 240, getTimeSpeed, v => setTimeSpeed(v < 10 ? MathF.Round(v * 10) / 10 : MathF.Round(v)),
                Text: v => v <= 0 ? "stopped" : $"{v:0.#} game h / min"));

        // The window's vsync (off: MAILBOX, else IMMEDIATE; the frame rate is uncapped).
        if (getVSync is not null && setVSync is not null)
            sliders.Add(new Slider("VSync", 0, 1, () => getVSync() ? 1 : 0, v => setVSync(v >= 0.5f),
                    Text: v => v >= 0.5f ? "on" : "off (uncapped)"));

        // Borderless full screen (also Alt+Enter).
        if (getFullScreen is not null && setFullScreen is not null)
            sliders.Add(new Slider("Full screen", 0, 1, () => getFullScreen() ? 1 : 0, v =>
            {
                if (v >= 0.5f != getFullScreen()) setFullScreen(v >= 0.5f);
            }, Text: v => v >= 0.5f ? "borderless" : "window"));
    }

    /// <summary>
    /// The objects' and landmarks' draw distances, the distant towns' range and the object LOD.
    /// </summary>
    static void AddObjectSliders(List<Slider> sliders, WorldObjectRenderer objects)
    {
        sliders.Add(new Slider(ObjectDistanceSlider, 1000, 400000, () => objects.ObjectDistance, v => objects.ObjectDistance = v, "0",
                Logarithmic: true,
            Faithful: Enhancements.FaithfulObjectDistance, Meitou: Enhancements.MeitouObjectDistance));
        if (objects.HasLandmarks)
            sliders.Add(new Slider(LandmarkDistanceSlider, 1000, 400000, () => objects.LandmarkDistance,
                    v => objects.LandmarkDistance = MathF.Round(v / 100) * 100,
                "0", Logarithmic: true, Faithful: 0, Meitou: Enhancements.MeitouLandmarkDistance));
        sliders.Add(new Slider(DistantTownsSlider, 0, ObjectRanges.MaxDistantTownRangeZones * 10,
            () => objects.DistantRange / WorldLayout.ZoneSize, v => objects.DistantRange = MathF.Round(v) * WorldLayout.ZoneSize, "0"));
        sliders.Add(new Slider(ObjectLodSlider, 0.25f, 4, () => 1 / objects.LodBias, v => objects.LodBias = 1 / v, "0.00", Logarithmic: true));
    }

    /// <summary>
    /// The game's NPC View Range: characters farther than this from the eye are not drawn, in steps of 50 (also the Metal panel's).
    /// </summary>
    internal static Slider CharacterDistance(Characters.CharacterRenderer characters) =>
        new(CharacterDistanceSlider, Characters.CharacterRenderer.MinDrawDistance, Characters.CharacterRenderer.MaxDrawDistance,
            () => characters.DrawDistance, v => characters.DrawDistance = MathF.Round(v / 50) * 50, "0", Step: 50);

    /// <summary>
    /// The foliage's distance, LOD, class ranges, impostor distances and the grass's distance and density.
    /// </summary>
    static void AddFoliageSliders(List<Slider> sliders, FoliageRenderer foliage)
    {
        static float Round50(float v) => MathF.Round(v / 50) * 50;

        sliders.Add(new Slider(FoliageDistanceSlider, 0.25f, 80, () => foliage.RangeSetting, v => foliage.RangeSetting = v, "0.00",
                Logarithmic: true));
        // The generated levels switch at a pixel tolerance; the distance where each level takes over scales with its inverse.
        sliders.Add(new Slider(FoliageLodSlider, 0.25f, 8, () => FoliageRenderer.DefaultLodTolerance / foliage.LodTolerance,
            v => foliage.LodTolerance = FoliageRenderer.DefaultLodTolerance / v, Logarithmic: true,
            Text: v => string.Create(CultureInfo.InvariantCulture, $"{v:0.00} ({FoliageRenderer.DefaultLodTolerance / v:0.0} px)")));

        // The range switch's class ranges (Meitou; the slider above then only moves the FAR layers' large meshes).
        sliders.Add(new Slider(LargeFoliageRangeSlider, 1000, 120000, () => foliage.LargeRange, v => foliage.LargeRange = Round50(v), "0",
                Logarithmic: true));
        sliders.Add(new Slider(ImpostorDistanceSlider, 500, 40000, () => foliage.ImpostorDistance, v => foliage.ImpostorDistance = Round50(v), "0",
                Logarithmic: true));
        sliders.Add(new Slider(LargeImpostorDistanceSlider, 500, 40000, () => foliage.LargeImpostorDistance,
                v => foliage.LargeImpostorDistance = Round50(v), "0",
            Logarithmic: true));
        sliders.Add(new Slider(MediumFoliageRangeSlider, 400, 80000, () => foliage.MediumRange, v => foliage.MediumRange = Round50(v), "0",
                Logarithmic: true));
        sliders.Add(new Slider(SmallFoliageRangeSlider, 200, 40000, () => foliage.SmallRange, v => foliage.SmallRange = Round50(v), "0",
                Logarithmic: true));

        sliders.Add(new Slider(GrassDistanceSlider, 0.25f, 80, () => foliage.GrassRangeSetting, v => foliage.GrassRangeSetting = v, "0.00",
                Logarithmic: true));
        sliders.Add(new Slider(GrassDensitySlider, 0.1f, 2, () => foliage.GrassDensitySetting, v => foliage.GrassDensitySetting = v, "0.00"));
    }

    /// <summary>
    /// The water's reflection and clarity.
    /// </summary>
    static void AddWaterSliders(List<Slider> sliders, Gpu g)
    {
        // The game's `water reflection` (0 off .. 4 everything) and `reflection range` (x the haze distance), docs/formats/settings.md.
        if (g.Reflection is { } reflection)
        {
            // The label is the user config key: keep it.
            sliders.Add(new Slider(WaterReflectionSlider, 0, 4, () => reflection.Level, v => reflection.Level = (int)MathF.Round(v), "0",
                Text: v => (int)MathF.Round(v) switch { 0 => "0 off", 1 => "1 landscape", 2 => "2 characters",
                    3 => "3 buildings", _ => "4 everything" }));
            sliders.Add(new Slider(ReflectionRangeSlider, 0.1f, 50, () => reflection.Range, v => reflection.Range = v, "0.00", Logarithmic: true));
        }

        // How far the Meitou water lets the floor show through (docs/render-water.md "Clarity"); the Faithful water keeps the game's alpha.
        if (g.Water is { } water)
        {
            sliders.Add(new Slider(WaterClaritySlider, 0.25f, 8, () => water.Clarity, v => water.Clarity = v, "0.00", Logarithmic: true));
            sliders.Add(new Slider(FarWaterClaritySlider, 0, 1, () => water.FarClarity, v => water.FarClarity = v, "0.00",
                Text: v => v <= 0 ? "0 (true slant)" : v.ToString("0.00", CultureInfo.InvariantCulture)));
            sliders.Add(new Slider(WaterClearDistanceSlider, 1000, 100000, () => water.ClearDistance,
                v => water.ClearDistance = MathF.Round(v / 100) * 100, "0", Logarithmic: true));
        }
    }

    /// <summary>
    /// The terrain's pixel error and the ground relief (ours, docs/render-terrain.md "Ground relief": the march's quality, the depth and where each
    /// ends).
    /// </summary>
    static void AddTerrainSliders(List<Slider> sliders, Gpu g, WorldRenderOptions r)
    {
        sliders.Add(new Slider(TerrainErrorSlider, 4, 32, () => r.TerrainPixelError, v => r.TerrainPixelError = v, "0.0", Logarithmic: true,
            Faithful: WorldRenderOptions.DefaultTerrainPixelError, Meitou: Enhancements.MeitouTerrainPixelError));

        // The depth drives parallax and displacement; 0 is off.
        sliders.Add(new Slider(GroundReliefQualitySlider, 0, 2, () => GroundRelief.Quality,
            v => GroundRelief.Quality = Math.Clamp((int)MathF.Round(v), 0, 2), "0", Text: v => GroundRelief.QualityName((int)MathF.Round(v))));
        sliders.Add(new Slider(GroundReliefDepthSlider, 0, 80, () => GroundRelief.Depth * 10, v => GroundRelief.Depth = MathF.Round(v * 2) / 20,
                "0.0"));
        if (g.Sky.Gpu.HasTessellation)
            sliders.Add(new Slider(GroundDisplaceDistanceSlider, 10, 200, () => GroundRelief.DisplaceDistance / 10,
                v => GroundRelief.DisplaceDistance = MathF.Round(v) * 10, "0"));
        sliders.Add(new Slider(GroundParallaxDistanceSlider, 10, 150, () => GroundRelief.ParallaxDistance / 10,
            v => GroundRelief.ParallaxDistance = MathF.Round(v) * 10, "0"));
    }

    /// <summary>
    /// The shadows' range, map size and filter, and the Meitou fifth cascade's switch (returned for the toggles). The game's <c>Shadow Range</c>
    /// slider goes
    /// 1000 to 9000; the viewer allows more (the cascades stretch over it), and its left end (0) turns the shadows off (low-end GPUs).
    /// </summary>
    static Toggle? AddShadowSliders(List<Slider> sliders, ShadowPass shadow)
    {
        const float off = KenshiShadows.MinRange * 0.9f;
        sliders.Add(new Slider(ShadowDistanceSlider, off, 200000, () => shadow.Enabled ? shadow.Settings.Range : off, v =>
        {
            shadow.Enabled = v >= KenshiShadows.MinRange;
            if (shadow.Enabled) shadow.Settings = shadow.Settings with { Range = MathF.Round(v / 100) * 100 };
        }, "0", Logarithmic: true, Text: v => shadow.Enabled ? v.ToString("0", CultureInfo.InvariantCulture) : "0 (off)",
            Faithful: 5000, Meitou: Enhancements.MeitouShadowRange));

        // The game's `shadow quality` (--shadow-quality): the atlas is made again at the new side on the next frame.
        sliders.Add(new Slider(ShadowMapSlider, 0, 2, () => Array.IndexOf(KenshiShadows.QualitySizes, shadow.Settings.MapSize),
            v => shadow.Settings = shadow.Settings with { MapSize = KenshiShadows.MapSize((int)MathF.Round(v)) }, "0",
            Text: v => KenshiShadows.MapSize((int)MathF.Round(v)).ToString(CultureInfo.InvariantCulture) + "²"));

        if (!shadow.Meitou) return null;
        sliders.Add(new Slider(ShadowFilterSlider, 0, 2, () => shadow.FilterQuality, v => shadow.FilterQuality = (int)MathF.Round(v), "0"));
        return new Toggle(FifthCascadeSwitch, () => shadow.MeitouCascades >= 5, v => shadow.MeitouCascades = v ? 5 : 4);
    }

    /// <summary>
    /// The sky's haze, planetshine, cloud height and ambient light.
    /// </summary>
    static void AddSkySliders(List<Slider> sliders, SkyRenderer sky)
    {
        // A viewer option, not the game's: 1 is the game's haze (docs/formats/sky.md).
        sliders.Add(new Slider(HazeStrengthSlider, 0, 3, () => sky.HazeStrength, v => sky.HazeStrength = v, "0.00", Faithful: 1,
                Meitou: Enhancements.MeitouHazeStrength));
        // Ours (docs/formats/sky.md "Planetshine"): the planet's light at night, a multiple of the physical value.
        sliders.Add(new Slider(PlanetshineStrengthSlider, 0, 200, () => sky.PlanetshineStrength, v => sky.PlanetshineStrength = MathF.Round(v), "0"));
        sliders.Add(new Slider(CloudHeightSlider, VolumetricClouds.MinBottom, VolumetricClouds.MaxBottom, () => sky.CloudHeight,
            v => sky.CloudHeight = MathF.Round(v / 100) * 100, "0", Logarithmic: true));
        // Ours: the sky's ambient light (shade, nights, interiors), a multiple of the game's.
        sliders.Add(new Slider(AmbientStrengthSlider, 0, 3, () => sky.AmbientStrength, v => sky.AmbientStrength = MathF.Round(v * 20) / 20, "0.00"));
        // Ours: a further multiple by night (the exposure sits at its night floor there, so it is not adapted away).
        sliders.Add(new Slider(NightAmbientStrengthSlider, 0, 5, () => sky.NightAmbientStrength,
                v => sky.NightAmbientStrength = MathF.Round(v * 20) / 20, "0.00"));
    }

    /// <summary>
    /// The post chain's rows: anti-aliasing and upscaling, the weather's particles, then the look (tone map, grade, haze, bloom, night grade,
    /// shafts).
    /// </summary>
    static void AddPostSliders(List<Slider> sliders, Gpu g, PostProcess post)
    {
        // Upscaling (docs/engine.md "Upscaling"): FSR and DLSS fall back to TAA where their library or backend is missing.
        var up = post.Options.Upscale;
        sliders.Add(new Slider(UpscalerSliders[0], 0, 5, () => AntiAliasingSlider(post.Options), v =>
        {
            up.Kind = AntiAliasingKind(v);
            int i = (int)MathF.Round(v);
            post.Options.Smaa = i == 2;
            post.Options.Fxaa = i >= 1 && i != 2;   // 0: no anti-aliasing at all (FXAA off, no upscaler); FXAA stays the fallback of the upscalers
            if (up.Kind != UpscalerKind.Off) up.Preferred = up.Kind;   // what the Meitou anti-aliasing switch turns back on
        }, "0"));
        sliders.Add(new Slider(UpscalerSliders[1], 0.33f, 1, () => up.EffectiveScale, v => up.Scale = MathF.Round(v * 100) / 100, "0.00", Text: v =>
        {
            string scale = v.ToString("0.00", CultureInfo.InvariantCulture);
            return up.Kind == UpscalerKind.Off && v < 1 ? scale + " (no upscaler: plain scaling)" : scale;
        }));
        sliders.Add(new Slider(UpscalerSliders[2], 0, 1, () => up.Sharpness, v => up.Sharpness = v, "0.00"));
        sliders.Add(DlssPresetSliderFor(up));

        // The weather's particles (docs/render-post.md "Particles"): off, forced to a quarter or half of the render size, by sprite size (the Meitou
        // default), or all full size (as the game).
        sliders.Add(new Slider(ParticleSliders[0], 0, 4, () => WeatherParticlesSlider(g.WeatherParticles, post.Options), v =>
        {
            int i = (int)MathF.Round(v);
            g.WeatherParticles = i > 0;
            if (i > 0)
                (post.Options.LowResParticles, post.Options.ParticleDivisor) = i switch { 1 => (true, 4), 2 => (true, 2), 3 => (true, 0), _ => (false,
                        0) };
        }, "0"));
        sliders.Add(new Slider(ParticleSliders[1], 0.1f, 1, () => g.ParticleDensity, v => g.ParticleDensity = MathF.Round(v * 20) / 20, "0.00"));

        AddLookSliders(sliders, post.Options);
    }

    /// <summary>
    /// The DLSS preset as a slider over <see cref="UpscaleOptions.DlssPresets"/>; a number not among them (a hand-edited setting) shows as the
    /// default.
    /// </summary>
    static Slider DlssPresetSliderFor(UpscaleOptions up)
    {
        var presets = UpscaleOptions.DlssPresets;
        int At(float v) => Math.Clamp((int)MathF.Round(v), 0, presets.Length - 1);

        return new Slider(DlssPresetSlider, 0, presets.Length - 1, () => Math.Max(Array.FindIndex(presets, p => p.Number == up.DlssPreset), 0),
            v => up.DlssPreset = presets[At(v)].Number, "0", Text: v => presets[At(v)].Name);
    }

    /// <summary>
    /// Ours, not the game's (docs/render-post.md "Tone map and grading"): the Meitou tone curve (the tone map switch's Faithful side is the game's
    /// clamp),
    /// the saturation / contrast grade, scales on the heat haze, bloom and night grade, and the light shafts.
    /// </summary>
    static void AddLookSliders(List<Slider> sliders, PostOptions options)
    {
        static float Round20(float v) => MathF.Round(v * 20) / 20;

        sliders.Add(new Slider(ToneMapSlider, 1, 3, () => (int)options.ToneMap, v => options.ToneMap = (ToneMapOperator)(int)MathF.Round(v), "0",
            Text: v => (ToneMapOperator)(int)MathF.Round(v) switch
            {
                ToneMapOperator.Shoulder => "shoulder",
                ToneMapOperator.Aces => "ACES",
                ToneMapOperator.Hybrid => "hybrid (clamp + ACES)",
                _ => "clamp (as the game)",
            }));
        sliders.Add(new Slider(ToneMixSlider, 0, 1, () => options.ToneMix, v => options.ToneMix = Round20(v), "0.00"));
        sliders.Add(new Slider(GradingSlider, 0, 1, () => options.Grade ? 1 : 0, v => options.Grade = v >= 0.5f,
            Text: v => v >= 0.5f ? $"saturation {PostOptions.Saturation:0.00}, contrast {PostOptions.Contrast:0.00}" : "off (as the game)"));

        sliders.Add(new Slider(HeatHazeStrengthSlider, 0, 3, () => options.HeatHazeStrength, v => options.HeatHazeStrength = Round20(v), "0.00",
            Faithful: 1, Meitou: Enhancements.MeitouHeatHazeStrength));
        sliders.Add(new Slider(BloomStrengthSlider, 0, 0.25f, () => options.BloomStrength, v => options.BloomStrength = MathF.Round(v * 200) / 200,
                "0.000", Faithful: 0));
        sliders.Add(new Slider(NightGradeStrengthSlider, 0, 1, () => options.NightGradeStrength, v => options.NightGradeStrength = Round20(v),
                "0.00"));

        sliders.Add(new Slider(ShaftStrengthSlider, 0, 1, () => options.ShaftStrength, v => options.ShaftStrength = Round20(v), "0.00"));
        sliders.Add(new Slider(ShaftAirSlider, 0, 4, () => options.ShaftAir, v => options.ShaftAir = Round20(v), "0.00"));
        sliders.Add(new Slider(ShaftAirDawnSlider, 0, 6, () => options.ShaftAirDawn, v => options.ShaftAirDawn = MathF.Round(v * 10) / 10, "0.0"));
        sliders.Add(new Slider(ShaftQualitySlider, 0, 2, () => options.ShaftQuality, v => options.ShaftQuality = Math.Clamp((int)MathF.Round(v), 0,
                2), "0",
            Text: v => (int)MathF.Round(v) switch { 0 => "Low", 1 => "Medium", _ => "High" }));
    }

    /// <summary>
    /// The Faithful / Meitou switches as checkboxes (ticked: Meitou), the F-key toggles' state (the displacement switch only where the device has
    /// tessellation shaders), then the optional effects: the fifth cascade, bloom (off in both looks, docs/render-post.md "Bloom"; a look preset may
    /// turn
    /// it on), and with tessellation the rock smoothing and outline refinement.
    /// </summary>
    static List<Toggle> SettingsToggles(Gpu g, IReadOnlyList<Enhancement>? switches, Toggle? fifthCascade)
    {
        bool tessellation = g.Sky.Gpu.HasTessellation;
        var toggles = switches?
            .Where(e => e.Id != "displace" || tessellation)
            .Select(e => new Toggle(e.Name, () => e.IsMeitou, v => e.IsMeitou = v, () => e.IsMeitou ? e.Meitou : e.Faithful, Remaster: true,
                OnOff: e.Faithful is "off" or "none"))
            .ToList() ?? [];
        if (fifthCascade is not null) toggles.Add(fifthCascade);

        if (g.Post is { } post)
            toggles.Add(new Toggle(BloomSwitch, () => post.Options.Bloom, v => post.Options.Bloom = v, Remaster: true, OnOff: true, Optional: true));
        if (tessellation)
        {
            toggles.Add(new Toggle(RockSmoothingSwitch, () => RockSmoothing.TessellateActive, v => RockSmoothing.TessellateActive = v, Remaster: true,
                    OnOff: true));
            toggles.Add(new Toggle(OutlineRefineSwitch, () => GroundRelief.RefinePixels > 0,
                    v => GroundRelief.RefinePixels = v ? GroundRelief.DefaultRefinePixels : 0,
                Remaster: true, OnOff: true));
        }
        return toggles;
    }

    /// <summary>
    /// The billboard (impostor atlas) disk cache's delete: deleted files are baked again when next needed; the resident atlases stay until evicted.
    /// </summary>
    static List<PanelAction> SettingsActions(Gpu g)
    {
        var actions = new List<PanelAction>();
        if (g.Foliage?.ImpostorDiskCache is not { } cache) return actions;
        actions.Add(new PanelAction("Delete billboard cache", () =>
        {
            var (files, bytes) = cache.Measure();
            return $"Delete {files} billboard atlases ({bytes / 1048576.0:0} MB) in {cache.Root}?";
        }, () =>
        {
            var (files, bytes) = cache.Clear();
            Console.WriteLine($"impostors deleted {files} cached atlases ({bytes / 1048576.0:0} MB) from {cache.Root}");
            return $"Deleted {files} atlases ({bytes / 1048576.0:0} MB); they are baked again when next needed.";
        }));
        return actions;
    }

    /// <summary>
    /// The third column: textures. The game's <c>texture resolution gimping</c> (docs/formats/settings.md): the object, foliage and character
    /// textures
    /// are loaded again at the new level as the slider moves; the terrain's layer arrays keep the size they were made with (--texture-quality at
    /// start).
    /// </summary>
    static List<Slider> TextureSliders()
    {
        int terrainLevel = Data.Textures.TextureQuality.Level;
        var labels = Data.Textures.TextureQuality.Labels;
        return
        [
            new(TextureQualitySlider, 0, Data.Textures.TextureQuality.Maximum, () => Data.Textures.TextureQuality.Level,
                v => Data.Textures.TextureQuality.Level = (int)MathF.Round(v), "0",
                Text: v => (int)MathF.Round(v) is var level && level != terrainLevel ? $"{labels[level]} (terrain at start)" : labels[level]),
        ];
    }

    #endregion

    #region Sky and shadows

    public static bool DetailedStats;
    static readonly string[] CascadeLabels = [.. Enumerable.Range(0, ShadowSettings.MaxCascades).Select(i => $"shadow c{i}")];
    static readonly (int, Vector4[], float, float)[] CasterSpecs = new (int, Vector4[], float, float)[ShadowSettings.MaxCascades];

    /// <summary>
    /// An hour as <c>HH:MM</c>.
    /// </summary>
    public static string TimeText(float hour)
    {
        int minutes = (int)MathF.Round(hour * 60) % (24 * 60);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    /// <summary>
    /// The unit of <c>Draw</c>'s <c>time</c> argument: seconds divided by 600 (the viewer's real seconds, the game's game seconds).
    /// </summary>
    const double SecondsPerTimeUnit = 600;

    /// <summary>
    /// Per-cascade and per-step CPU times in the stats strings (ShadowPass.CasterStats, FoliageRenderer's details); the viewer's screenshots and
    /// benchmarks turn it on.
    /// </summary>

    /// <summary>
    /// Sky-only loading frames, with the existing sky and post chain and without touching world streaming or character content.
    /// </summary>
    public static void DrawLoadingSky(Gpu gpu, WorldScene scene, WorldCamera camera, int width, int height, float hour, float seconds,
            float fogDistance)
    {
        gpu.Terrain.Activate();
        var post = gpu.PostChain ?? throw new InvalidOperationException("the sky frame needs the post-processing chain");
        post.GiResolve = 0;
        post.Begin(width, height);
        if (post is PostProcess { Unallocated: true }) return;
        float aspect = width / (float)Math.Max(height, 1);
        var eye = camera.Eye;
        var view = camera.View;
        var rotation = view with { M41 = 0, M42 = 0, M43 = 0 };
        var vp = rotation * Jitter.Apply(camera.Projection(aspect, 1, 1000), post.JitterPixels, post.RenderWidth, post.RenderHeight);
        gpu.Sky.SetEye(eye, gpu.Terrain.HeightAt, float.NegativeInfinity);
        gpu.Sky.Eye = eye;
        gpu.Sky.Clock = scene.Clock;
        gpu.Sky.Hour = hour;
        gpu.Sky.StarSeconds = seconds;
        var (colours, _) = gpu.Sky.Prepare(scene.Clock.SunDirection(hour), eye.Y, fogDistance);
        post.AutoExposure = (gpu.Sky.MinLuminance, gpu.Sky.MaxLuminance);
        post.SetCamera(eye, view, camera.FieldOfView, aspect);
        post.BeginScene(1, 1000, camera.FieldOfView, aspect);
        var host = gpu.Scene ??= new SceneHost(post.Context);
        host.Open(5, post.SceneTargets, new ClearColour(0, 0, 0, 0), clearDepth: true);
        gpu.Sky.Draw(vp, colours);
        host.Close();
        // Motion and particle callbacks belong to the world that is being replaced.
        var motion = gpu.PostChain?.ObjectMotion;
        var particles = post.Particles;
        gpu.PostChain?.ObjectMotion = null;
        post.Particles = null;
        post.SetCloudMotion(null);
        try { post.End(); }
        finally
        {
            gpu.PostChain?.ObjectMotion = motion;
            post.Particles = particles;
        }
    }

    /// <summary>
    /// --log-spikes: a step of the sky-prepare stage over 5 ms is noted (<c>slow sky &lt;step&gt;</c>), the time since <paramref name="since"/>,
    /// which moves on.
    /// </summary>
    static void SkyStep(string step, ref long since)
    {
        long now = Stopwatch.GetTimestamp();
        double ms = (now - since) * 1000.0 / Stopwatch.Frequency;
        since = now;
        if (ms > 5) SpikeLog.Note($"slow sky {step}", ms: ms);
    }

    /// <summary>The air layer's density by the time of day (docs/render-shafts.md "Air layer"): <c>ShaftAirDawn</c> at sunrise and at sunset, easing
    /// (smoothstep) to
    /// <c>ShaftAirDay</c> over <c>ShaftAirRamp</c> hours after sunrise and before sunset; at night the dawn value (the shafts need the sun
    /// anyway).</summary>
    internal static float ShaftAirByTime(PostOptions o, SkyClock clock, float hour)
    {
        hour = ((hour % 24) + 24) % 24;
        float fromEvent = hour < clock.Sunrise || hour > clock.Sunset ? 0 : Math.Min(hour - clock.Sunrise, clock.Sunset - hour);
        float t = Math.Clamp(fromEvent / PostOptions.ShaftAirRamp, 0, 1);
        return PostOptions.ShaftAirDay + (o.ShaftAirDawn - PostOptions.ShaftAirDay) * (1 - t * t * (3 - 2 * t));
    }

    /// <summary>How much of the haze in shadow the light shafts take off (docs/render-shafts.md): the strength times the sun's share of the light the
    /// air scatters (the sun's colour against the sky's ambient, by luminance), faded out as the sun sets, when the sky's light is all there
    /// is.</summary>
    internal static float ShaftDarkening(PostOptions o, WorldLighting light, float sunY)
    {
        static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
        float sun = Luminance(light.SunColour), sky = Luminance(light.AmbientSky);
        float share = sun / Math.Max(sun + sky, 1e-4f);
        float t = Math.Clamp((sunY + 0.02f) / 0.07f, 0, 1);
        return o.ShaftStrength * share * t * t * (3 - 2 * t);
    }

    /// <summary>How overcast the sky is, 0..1, for what fades under clouds (the light shafts, the Meitou water's glint): the sky's cloud density c,
    /// or the
    /// weather's <c>rainAmount</c> (saturate(rain / 50)) where that is more, since rain falls from a covered sky whatever c the weather lists
    /// (<c>swamp rain no wind</c> has c 0.5 and rain 40).</summary>
    internal static float Overcast(float cloudDensity, float rain) => MathF.Max(Math.Clamp(cloudDensity, 0, 1), WeatherSurfaces.RainAmount(rain));

    static float Overcast(Gpu gpu) => Overcast(gpu.Sky.CloudDensity, gpu.Surfaces?.Rain ?? 0);

    /// <summary>What is left of the light shafts under the cloud layer (docs/render-shafts.md "Cloud cover"): the game's sun shines through any
    /// weather, but
    /// crisp shafts under a full overcast in the rain read wrong, so the darkening loses <c>ShaftCloudShade</c> of itself as the cloud density c goes
    /// from
    /// <c>ShaftCloudFrom</c> to 1 (smoothstep).</summary>
    internal static float ShaftCloudFade(float cloudDensity)
    {
        float t = Math.Clamp((cloudDensity - PostOptions.ShaftCloudFrom) / Math.Max(1 - PostOptions.ShaftCloudFrom, 1e-3f), 0, 1);
        return 1 - Math.Clamp(PostOptions.ShaftCloudShade, 0, 1) * t * t * (3 - 2 * t);
    }

    /// <summary>
    /// The game's <c>heatHaze</c> (docs/formats/weather.md "Heat haze"): the target is the weather's <c>heat haze</c>
    /// × strength × saturate(6 · sunY), with strength 1 (a forced weather has no scheduler's roll); the value
    /// moves towards it at 1/3 per second of real time (game speed 1), and jumps there for still pictures. The animation's
    /// <c>gameTime</c> is the caller's game clock when it has one, else real time at game speed 1 (held still for pictures).
    /// </summary>
    static void UpdateHeatHaze(Gpu gpu, IWorldPostChain post, float sunY)
    {
        float dt = (float)Math.Min(gpu.HeatHazeClock.Elapsed.TotalSeconds, 0.25);
        bool first = !gpu.HeatHazeClock.IsRunning;
        gpu.HeatHazeClock.Restart();
        // With the scheduler the value is the state's own (the weather's field × the strength × the sun factor, ramped on the settling time,
        // snapped on a
        // teleport); without a scheduler (no database) it is the old forced path at strength 1, ramped here.
        var weather = gpu.Weather;
        float field = weather?.State.Weather.HeatHaze ?? gpu.Sky.Weather.HeatHaze;
        gpu.HeatHazeTarget = HeatHaze.Target(field, weather?.State.Strength ?? 1, sunY);
        if (weather is not null) post.HeatHazeAmount = weather.State.HeatHaze;
        else post.HeatHazeAmount = first || post.InstantAdaptation ? gpu.HeatHazeTarget : HeatHaze.Step(post.HeatHazeAmount, gpu.HeatHazeTarget,
                weather?.Times.Settling ?? dt);
        if (!post.InstantAdaptation) gpu.HeatHazeHours += dt * HeatHaze.HoursPerSecond;
        post.HeatHazeHours = gpu.GameHours ?? gpu.HeatHazeHours;
        // the heat haze's D follows the install's `view distance`, not the sky haze's constant
        post.HeatHazeFarClip = KenshiHaze.FarDistance(HeatHaze.ViewDistanceSetting);
    }

    // DrawShadows's cascades for the characters (render thread).

    /// <summary>
    /// The sun's shadow cascades (ShadowPass, docs/formats/shadows.md): fitted to this camera, their casters drawn by the renderers'
    /// depth-only paths (terrain, objects, foliage meshes; detail chosen from the camera's eye), before the reflection and the main pass.
    /// The characters of all the cascades drawn are gathered first, in one walk.
    /// </summary>
    /// <param name="sunHeight">The real sun's height: the map is drawn along the lighting direction while the sun still has a colour (as the
    /// game).</param>
    public static void DrawShadows(Gpu gpu, WorldCamera camera, WorldRenderOptions render, WorldLighting light, int width, int height,
            float? sunHeight = null,
        bool? temporal = null)
    {
        var shadow = gpu.Shadow!;
        // The caller's post chain owns the temporal decision (Metal's PostTaa/MetalFX is outside gpu.Post).
        shadow.Temporal = temporal ?? gpu.PostChain?.Temporal == true;
        var v = camera.View;
        var up = Vector3.Normalize(new Vector3(v.M12, v.M22, v.M32));
        var view = new ShadowView(camera.Eye, camera.Forward, up, camera.FieldOfView, width / (float)Math.Max(height, 1), camera.Near,
                camera.Distance);
        gpu.Terrain.DepthTriangles = 0;

        var casters = new ShadowCasters(gpu, shadow, render);
        var landmarks = render.Objects && gpu.Objects is { HasLandmarks: true } lo
            ? new ShadowPass.LandmarkCasters(spheres => lo.LandmarkCasters(camera.Eye, spheres),
                (_, worldToClip, planes, lodEye) => lo.DrawLandmarksDepth(worldToClip, lodEye, planes, render, gpu.Terrain))
            : (ShadowPass.LandmarkCasters?)null;
        shadow.CameraFlying = gpu.CameraFlying;
        shadow.Render(view, light.SunDirection, casters.Draw, sunHeight, landmarks, casters.Prepare);
        shadow.CasterStats = casters.Stats();
    }

    #endregion

    #region Shadow casters

    /// <summary>
    /// One <see cref="DrawShadows"/>'s casters: each cascade's terrain, objects, characters and foliage by the renderers' depth-only paths, timed per
    /// kind.
    /// </summary>
    sealed class ShadowCasters(Gpu gpu, ShadowPass shadow, WorldRenderOptions render)
    {
        int _objects, _foliage;
        readonly System.Text.StringBuilder? _cascades = DetailedStats ? new() : null;

        /// <summary>
        /// The characters of every cascade drawn this frame, gathered in one walk before the first (CharacterRenderer.PrepareDepth).
        /// </summary>
        public void Prepare(ReadOnlySpan<ShadowPass.CasterView> views)
        {
            if (gpu.Characters is not { } characters || views.IsEmpty) return;
            var specs = CasterSpecs.AsSpan(0, views.Length);
            for (int k = 0; k < views.Length; k++)
            {
                var (floor, minRadius) = CharacterCasting(views[k].Cascade);
                specs[k] = (views[k].Cascade.Index, views[k].Planes, floor, minRadius);
            }
            characters.PrepareDepth(views[0].LodEye, specs);
            specs.Clear();
            StageClock.Sub("characters");
        }

        /// <summary>
        /// One cascade's casters; a far cascade (ShadowLod) takes coarser ones.
        /// </summary>
        public void Draw(ShadowCascade cascade, Matrix4x4 worldToClip, Vector4[] planes, Vector3 lodEye)
        {
            var terrain = gpu.Terrain;
            long t0 = Stopwatch.GetTimestamp();
            long triangles = terrain.DepthTriangles;
            float coarse = shadow.Meitou && ShadowLod.Applies(cascade.Index) ? (float)cascade.Texel : 0;
            terrain.DrawDepth(worldToClip, lodEye, planes, render, coarse);
            StageClock.Sub("terrain");

            long t1 = Stopwatch.GetTimestamp();
            int objectInstances = 0, objectCalls = 0, foliageInstances = 0, foliageCalls = 0;
            if (render.Objects && gpu.Objects is { } o)
            {
                o.DrawDepth(worldToClip, lodEye, planes, render, terrain, coarse);
                _objects += o.DrawnInstances;
                (objectInstances, objectCalls) = (o.DrawnInstances, o.DrawCalls);
            }
            StageClock.Sub("objects");
            var (characterFloor, characterMinRadius) = CharacterCasting(cascade);
            gpu.Characters?.DrawDepth(worldToClip, lodEye, planes, characterFloor, characterMinRadius, cascade.Index);
            StageClock.Sub("characters");

            long t2 = Stopwatch.GetTimestamp();
            if (gpu.Foliage is { } f)
            {
                f.DrawDepth(worldToClip, lodEye, planes, render, terrain, maxRange: shadow.EffectiveRange * 1.2f,
                        minSize: shadow.MinFoliageCaster(cascade),
                    texel: (float)cascade.Texel, coarse: coarse > 0);
                _foliage += f.DrawnInstances;
                (foliageInstances, foliageCalls) = (f.DrawnInstances, f.DrawCalls);
            }
            StageClock.Phase(CascadeLabels[cascade.Index]);

            long t3 = Stopwatch.GetTimestamp();
            double ms = 1000.0 / Stopwatch.Frequency;
            shadow.PhaseMs[0] += (t1 - t0) * ms;
            shadow.PhaseMs[1] += (t2 - t1) * ms;
            shadow.PhaseMs[2] += (t3 - t2) * ms;
            _cascades?.Append($" [c{cascade.Index}: texel {cascade.Texel:0.0}, "
                + $"terrain {(terrain.DepthTriangles - triangles) / 1000}k tri {(t1 - t0) * ms:0.00} ms, "
                + $"objects {objectInstances} in {objectCalls} calls {(t2 - t1) * ms:0.00} ms, "
                + $"foliage {foliageInstances} in {foliageCalls} calls {(t3 - t2) * ms:0.00} ms ({gpu.Foliage?.DepthDetail})]");
        }

        /// <summary>
        /// The casters drawn over the cascades and their CPU time per kind, for the statistics.
        /// </summary>
        public string Stats()
        {
            var phase = shadow.PhaseMs;
            return $"{gpu.Terrain.DepthTriangles:N0} terrain triangles, {_objects} objects, {_foliage} foliage meshes (over the cascades); "
                + $"cpu terrain {phase[0]:0.00}, objects {phase[1]:0.00}, foliage {phase[2]:0.00} ms;{_cascades}"
                + (shadow.Meitou ? $"; meitou: {shadow.DescribeMeitou()}" : "");
        }

        /// <summary>
        /// Characters take the far cascades' floor in both shadow modes: their levels are Meitou's own, and every character is in every cascade that
        /// holds
        /// the crowd. In Meitou's coarse cascades a part under a texel across casts nothing (ShadowPass.MinCharacterCasterTexels).
        /// </summary>
        (float LodFloor, float MinRadius) CharacterCasting(ShadowCascade cascade)
        {
            bool far = ShadowLod.Applies(cascade.Index);
            float coarse = shadow.Meitou && far ? (float)cascade.Texel : 0;
            float floor = ShadowLod.ObjectLodFloor(far ? (float)cascade.Texel : 0, render.TerrainPixelScale);
            return (floor, ShadowPass.CharacterCasterCull ? coarse * ShadowPass.MinCharacterCasterTexels * 0.5f : 0);
        }
    }

    #endregion
}