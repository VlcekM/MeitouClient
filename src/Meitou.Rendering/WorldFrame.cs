using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Particles;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Rendering;

sealed class WorldOptions
{
    public double? X, Z;
    public ZoneCoordinate? Zone;
    public string? Town;
    public float Radius = 1.5f;
    public float? Yaw, Pitch, Distance;
    public string? Screenshot;
    public int Width = 1280, Height = 960;
    /// <summary><c>--size</c> was given: an interactive window keeps that size instead of starting maximised.</summary>
    public bool SizeGiven;
    /// <summary>The size of a bench run (<c>--bench-frames</c> or <c>--ab</c>) without <c>--size</c>: smaller, so the runs are quicker.</summary>
    public static readonly (int Width, int Height) BenchSize = (1280, 720);
    public bool NoTextures, NoObjects, Wireframe;
    public int Debug;
    /// <summary>The reach switch (Enhancements): Meitou by default (objects to 20000, landmarks 7.5 times that, terrain error 16 px) or the viewer's old values (12000, none, 10 px).
    /// <c>--object-distance</c> and <c>--terrain-error</c> as given (null: the mode's default, see the <c>...For</c> methods).</summary>
    public bool MeitouReach = true;
    /// <summary>The water switch (Enhancements): waves, breakers and foam (Meitou, default) or the game's flat water.</summary>
    public bool MeitouWater = true;
    /// <summary><c>--water-grid</c>: the Meitou water grid's segments round the eye (64 to 1024; fewer for integrated GPUs).</summary>
    public int WaterGrid = WaterRenderer.DefaultGridSegments;
    public int WaterOcean = OceanWaves.DefaultSize;
    public float? ObjectDistance;
    public float ObjectDistanceFor(bool meitou) => ObjectDistance ?? (meitou ? Enhancements.MeitouObjectDistance : Enhancements.FaithfulObjectDistance);
    /// <summary>The landmark distance (units; 0: no landmarks, the huge objects stay with the others): in Meitou a fixed multiple of the object distance (7.5, the defaults' ratio), none in Faithful.</summary>
    public float LandmarkDistanceFor(bool meitou) => meitou ? ObjectDistanceFor(true) * (Enhancements.MeitouLandmarkDistance / Enhancements.MeitouObjectDistance) : 0;
    public float TerrainErrorFor(bool meitou) => TerrainError ?? (meitou ? Enhancements.MeitouTerrainPixelError : WorldRenderOptions.DefaultTerrainPixelError);
    public float DistantZones = ObjectRanges.MaxDistantTownRangeZones;
    public bool NoFoliage;
    public float Hour = 13;
    /// <summary>The viewer's clock: game hours per real minute (0: the time stays where it is set).</summary>
    public float TimeSpeed;
    public float FogDistance = 250000, MaterialDistance = 30000;
    /// <summary>Terrain LOD (<see cref="TerrainLod"/>): the screen-space error in render pixels (null: the reach switch's mode default).</summary>
    public float? TerrainError;
    public bool NoWater, ShowKeys;
    /// <summary>No placed fog volumes (<c>fogfeatures.dat</c> not read); the weather's fog spheres still work. Set by the game's sandbox, no switch of its own.</summary>
    public bool NoFogFeatures;
    /// <summary>`--play-shots`: the viewer starts flying its saved camera keys (the F2 timeline) on a loop.</summary>
    public bool PlayShots;
    /// <summary>The game's <c>texture resolution gimping</c> (0..4; missing key: 1) and <c>water reflection</c> (0..4; missing: 2) settings (docs/formats/settings.md).</summary>
    // The viewer starts at full quality (the old look); the game's missing-key defaults are TextureQuality.Default (1) and
    // ReflectionPass.DefaultLevel (2): --texture-quality 1 --water-reflection 2.
    public int TextureQuality = 0, WaterReflection = 4;
    /// <summary>Samples per texel of the reflection target (<c>--reflection-samples</c>; 1 disables the MSAA/resolve path).</summary>
    public int ReflectionSamples = ReflectionPass.DefaultSamples;
    /// <summary>Sun shadows (docs/formats/shadows.md): the game's <c>shadow quality</c> index, <c>Shadow Range</c>, the debug view.</summary>
    public int ShadowQuality = 2, DebugShadows, ShadowFilter = 2;
    /// <summary>The ray-traced global illumination's debug view (0 off; docs/render-gi.md).</summary>
    public int GiDebug;
    /// <summary>The probe GI, on unless <c>--no-gi</c> (it needs ray queries: without them it is off, with a note); <see cref="GiProbes"/> is its Faithful / Meitou switch (<c>gi</c>).</summary>
    public bool Gi = true, GiProbes = true;
    public bool InteriorGi = true;
    /// <summary>Whether the device must be made with ray queries (<c>VulkanDisplay.RayTracing</c>).</summary>
    public bool WantsRayTracing => Gi || GiDebug > 0;
    public float? ShadowRange;   // --shadow-range as given (null: the default of the shadows switch's mode, see ShadowRangeFor)
    /// <summary><c>--shadow-range 0</c>: the shadow pass is made but starts off (the Tab slider's left end; it can be turned on there).</summary>
    public bool ShadowsOff;
    public bool MeitouShadows = true;   // the shadows switch (Enhancements): Meitou by default, false the game's CSM
    /// <summary><c>--no-vsync</c>: the interactive window starts uncapped (the Tab panel's VSync).</summary>
    public bool VSync = true;
    /// <summary>The Tab panel's "Object LOD distance x" (0.25 to 4; the object renderer's LOD bias is its inverse); only <c>--low-end</c> moves it.</summary>
    public float ObjectLod = 1;
    /// <summary><c>--grass-density x</c>: the Tab panel's "Grass density x" (0.1 to 2; null: the renderer's default).</summary>
    public float? GrassDensity;

    /// <summary>
    /// The shadow distance for the shadows switch's mode: <c>--shadow-range</c> when given, else the game's default (5000) in Faithful and
    /// <see cref="Enhancements.MeitouShadowRange"/> in Meitou. The game's slider ends at 9000 (<see cref="KenshiShadows.MaxRange"/>); with the
    /// Meitou shadows the option may go to <see cref="Enhancements.MeitouShadowRangeMax"/> (the cascades are fitted to the view, and far foliage casts
    /// through impostors; the Tab slider goes further, at the user's risk of running out of video memory).
    /// </summary>
    public float ShadowRangeFor(bool meitou) => meitou
        ? Math.Clamp(ShadowRange ?? Enhancements.MeitouShadowRange, KenshiShadows.MinRange, Enhancements.MeitouShadowRangeMax)
        : Math.Clamp(ShadowRange ?? KenshiShadows.DefaultRange, KenshiShadows.MinRange, KenshiShadows.MaxRange);
    /// <summary>The range switch (Enhancements): foliage meshes drawn to their size class's range (Meitou, default) or their layer's (the game); the large class's range (null: the default;
    /// <c>--range-large</c>), the medium and small ranges follow it at their default ratios.</summary>
    public bool MeitouRange = true;
    public float? LargeRange;
    /// <summary>The impostors switch (Enhancements): far foliage as baked billboards (Meitou, default) or meshes only (the game); the distances (null: the default; only <c>--low-end</c> sets them).</summary>
    public bool Impostors = true;
    /// <summary>The Meitou <c>lod</c> switch: generated mesh levels for TERRAIN-mode foliage meshes (<see cref="FoliageRenderer.Lod"/>).</summary>
    public bool FoliageLod = true;
    /// <summary>Characters' generated LOD levels (the <c>charlod</c> switch's Meitou side); false: the mesh files' own levels (<c>CharacterRenderer.GeneratedLod</c>).</summary>
    public bool CharacterLod = true;
    /// <summary>The <c>skin</c> switch's Meitou side: pre-integrated scattering on people's bare skin (<c>CharacterRenderer.SkinShading</c>).</summary>
    public bool SkinShading = true;
    /// <summary>The <c>interiorlamps</c> switch's Meitou side: interior lamps shine by day too (<c>WorldLamps.InteriorAllDay</c>).</summary>
    public bool InteriorLamps = true;
    /// <summary>Supersampled item icons (the <c>icons</c> switch's Meitou side, <c>CharacterRenderer.EnhancedIcons</c>); false: drawn as the game draws them.</summary>
    public bool MeitouIcons = true;
    /// <summary>The <c>blood</c> switch's Meitou side: every blood cell within Decal Range drawn, up to <c>TerrainDecalRenderer.MaxSlots</c>; false: the game's 8 nearest.</summary>
    public bool MeitouBlood = true;
    /// <summary>The <c>bloodsurfaces</c> switch's Meitou side: blood also on walls, buildings, floors and other static objects (<c>TerrainDecalRenderer.Surfaces</c>); false: the terrain only, as the game.</summary>
    public bool MeitouBloodSurfaces = true;
    /// <summary>The <c>tracers</c> switch's Meitou side: a semi-transparent streak behind each bolt in flight (the game's <c>GameHost.AddTracers</c>); false: the bolt alone, as the game.</summary>
    public bool MeitouTracers = true;
    public float? ImpostorDistance, LargeImpostorDistance;
    public double? ImpostorBudgetMb;
    public bool MeitouHaze = true; // the Meitou haze switch (default): the haze at HazeStrength; Faithful: the game's (1)
    public float HazeStrength = Enhancements.MeitouHazeStrength; // the Meitou haze's strength (a Tab slider), kept while the switch is Faithful
    public bool NightAir = true; // the Meitou night switch (default): the night air, a faint glow the haze fades into and the sky shows above the horizon (SkyRenderer.NightAir)
    public bool Planetshine = true; // the Meitou planetshine switch (default): the big planet lights the night (SkyRenderer.Planetshine)
    public float PlanetshineStrength = Enhancements.MeitouPlanetshineStrength; // x the physical value (a Tab slider)
    public bool MeitouStars = true;   // the Meitou stars switch (default): the procedural night sky (SkyRenderer.MeitouStars); false: the game's starfield texture
    public bool MeitouClouds = true;  // the Meitou clouds switch (default): the cloud layer lit by the sun through its depth (SkyRenderer.LitClouds); false: the game's flat colour
    public bool CloudShadows = true;  // the Meitou cloudshadows switch (default): the cloud layer shades the sun on the land and water (SkyRenderer.CloudShadows); false: none, as the game
    public bool CloudFog = true;      // the Meitou cloudfog switch (default): the cloud shadows fade near and inside placed fog volumes (SkyRenderer.CloudFog); false: full strength
    /// <summary><c>--weather</c>: a WEATHER record's name forces that weather at the camera; null or "auto" lets the scheduler (docs/formats/weather.md) run.</summary>
    public string? Weather;
    /// <summary><c>--day</c> (null: day 0 in the viewer, the game's clock start in the game).</summary>
    public int? Day;
    public bool AutoWeather => Weather is null || Weather.Equals("auto", StringComparison.OrdinalIgnoreCase);
    /// <summary>The weather particle effects (docs/formats/particle-universe.md): <c>--no-particles</c> turns the pass off, <c>--particle-prewarm</c> is the seconds simulated before the first picture (null: each system's longest particle life).</summary>
    public bool NoParticles;
    /// <summary><c>--no-audio</c>: no sound (docs/audio.md); <c>--audio-gain &lt;dB&gt;</c> raises or lowers the whole mix (0 = the game's levels).</summary>
    public bool NoAudio;
    public float AudioGainDb;
    public float? ParticlePrewarm;
    /// <summary><c>--particle-density x</c>: the Tab panel's "Particle density x" at start (0.1 to 1).</summary>
    public float ParticleDensity = 1;
    /// <summary><c>--anisotropy n</c>: the Tab panel's anisotropic filtering at start (1, 2, 4, 8 or 16; 16 leaves the textures as asked).</summary>
    public int Anisotropy = 16;
    public float? Clouds;
    public PostOptions Post = PostOptions.Create("meitou");
    public double? FlyToX, FlyToZ;
    /// <summary><c>--camera-code</c>: the camera's target, yaw, pitch and distance from a viewer's Ctrl+C code (the world is loaded round it).</summary>
    public WorldCameraCode.Pose? CameraPose;
    /// <summary>Frames of the offscreen benchmark flight (0: none), the circle's radius and the speed per frame.</summary>
    public int FlyBenchmark;
    public float FlyRadius = 12000, FlySpeed = 150;
    /// <summary>The benchmark without a wait for the GPU each frame and without the 60 fps pacing: up to two frames in flight (both backends), frame time = the interval between frames.</summary>
    public bool FlyPipelined;
    /// <summary><c>--fly-csv &lt;file&gt;</c>: the benchmark writes one row per frame there (frame times, video memory) and the allocator owners every 30 frames to <c>&lt;file&gt;.owners.csv</c>.</summary>
    public string? FlyCsv;
    /// <summary>
    /// The benchmark harness (docs/bench.md "Benchmark harness"): <c>--view</c> the named view, <c>--ab</c> the switch to alternate (<see cref="AbToggles"/>) with
    /// <c>--ab-period</c> frames per side, <c>--bench-frames</c> measured frames (default 600 with <c>--ab</c>), <c>--bench-out</c> the JSON, <c>--bench-motion</c>
    /// still / orbit / fly / turn, <c>--no-gpu-lock</c>, <c>--bench-compare</c> two JSON files.
    /// </summary>
    public string? View, Ab, BenchOut;
    public int AbPeriod = 1, BenchFrames;
    public string BenchMotion = "still";
    /// <summary><c>--bench-orbit</c> pixels of mouse drag a frame of the orbit motion.</summary>
    public float BenchOrbit = 3;
    public bool NoGpuLock;
    /// <summary>--bench-serial: the benchmark waits for every frame (CPU + GPU added up, the old behaviour) instead of keeping frames in flight.</summary>
    public bool BenchSerial;
    /// <summary>--bench-tris: after the timing, the triangles drawn per pass (pipeline statistics queries) and the main view's triangle size histogram (docs/bench.md "Triangles per pass").</summary>
    public bool BenchTris;
    public (string A, string B)? BenchCompare;
    /// <summary>Offscreen pictures: radians the camera orbits by every frame (tests the motion vectors under a temporal upscaler).</summary>
    public float OrbitStep;
    /// <summary>Interactive window: the monitor it opens on (1-based; <c>--monitor</c>).</summary>
    public int? Monitor;
    /// <summary>Interactive window: a bordered, maximized window instead of borderless full screen (<c>--windowed</c>).</summary>
    public bool Windowed;
    /// <summary>Offscreen pictures: seconds the grass sway advances every frame (tests the grass motion under a temporal upscaler; 0 holds it still).</summary>
    public float SwayStep;
    /// <summary>The character renderer's test harness (<c>--crowd N</c>, <see cref="Characters.CrowdHarness"/>): N generated characters round the start point.</summary>
    public int Crowd;

    /// <summary>The longest <c>--shadow-range</c> the command line takes (the game stops at 9000; larger ranges cost VRAM and above this the driver has been seen to reset).</summary>
    public const float CommandLineMaxShadowRange = 15000;

    /// <summary>
    /// <c>--low-end</c>: the potato-PC settings, applied where it stands among the options (so any option after it overrides one of them).
    /// Short draw distances, no reflections, shadows, ground relief or light shafts, the game's FXAA turned off too, 0.83 render scale, quarter-size particles.
    /// </summary>
    void ApplyLowEnd()
    {
        VSync = false;
        (ObjectDistance, ObjectLod) = (LowEndObjectDistance, LowEndObjectLod);
        (LargeRange, ImpostorDistance, LargeImpostorDistance) = (LowEndLargeRange, LowEndImpostorDistance, LowEndLargeImpostorDistance);   // the medium and small ranges follow the large one
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

    /// <summary><c>--low-end</c>'s values, which the Low graphics preset sets too (<c>SettingsMenu.Presets</c>).</summary>
    public const float LowEndObjectDistance = 5000, LowEndObjectLod = 0.25f, LowEndLargeRange = 5050, LowEndImpostorDistance = 1150, LowEndLargeImpostorDistance = 1100,
        LowEndGrassDensity = 0.1f, LowEndRenderScale = 0.83f, LowEndParticleDensity = 0.1f;
    public const int LowEndTextureQuality = 3;

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
          --no-vsync               start with vsync off (uncapped; Tab slider)
          --grass-density <x>      grass blades x this (0.1-2, default 1; Tab slider)
          --camera-code <hex>      start at a camera code (Ctrl+C in the viewer copies one), the world loaded round it
          --faithful <all|ao,dither,haze,aa,shadows,range,impostors,reach,water,particles,lod,gi,tonemap,shafts,heathaze,night,planetshine,stars,nightgrade,clouds,cloudshadows,cloudfog,interiorgi,charlod,skin,interiorlamps,icons,blood,bloodsurfaces,parallax,displace,weld,tracers,fog>   the game's look instead of Meitou's enhancements (default: all Meitou; --meitou <...> turns them back on; these ids also name the --ab switches)
          --show-keys              start with the key list overlay open (toggle with F10)
          --play-shots             start flying the saved camera keys (the F2 timeline) on a loop
          --log-spikes             print a line for every frame whose GPU time is over 1.5 x the median: stage times (GPU, render thread) and what it uploaded or rebuilt
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
          --render-scale <0.25..1|native|ultra-quality|quality|balanced|performance|ultra>   render size per axis (Meitou default 0.83 with an upscaler, else 1)
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

    /// <summary>The Faithful / Meitou switches over the options (for <c>--meitou</c> / <c>--faithful</c>).</summary>
    internal static IReadOnlyList<Enhancement> Switches(WorldOptions o) => Enhancements.Create(o.Post, () => o.MeitouHaze, v => o.MeitouHaze = v,
        () => o.MeitouShadows, v => o.MeitouShadows = v, () => o.MeitouRange, v => o.MeitouRange = v, () => o.Impostors, v => o.Impostors = v, () => o.MeitouReach, v => o.MeitouReach = v,
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

    public static WorldOptions? Parse(string[] args)
    {
        var o = new WorldOptions();
        int viewAt = Array.LastIndexOf(args, "--view");
        if (viewAt >= 0 && viewAt + 1 < args.Length) o.View = args[viewAt + 1];
        args = NamedViews.Expand(args);   // --view <name> becomes its options, in place: the ones after it win
        bool sizeGiven = args.Contains("--size");
        o.SizeGiven = sizeGiven;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            float F() => float.Parse(Next(), CultureInfo.InvariantCulture);
            (double, double) Pair()
            {
                var p = Next().Split(',');
                if (p.Length != 2) throw new ArgumentException($"{a} needs <a>,<b>");
                return (double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture));
            }
            switch (a)
            {
                case "--world": break;
                case "--at": (o.X, o.Z) = Pair(); break;
                case "--zone": { var (zi, zj) = Pair(); o.Zone = new ZoneCoordinate((int)zi, (int)zj); break; }
                case "--town": o.Town = Next(); break;
                case "--radius": o.Radius = F(); break;
                case "--yaw": o.Yaw = F(); break;
                case "--pitch": o.Pitch = F(); break;
                case "--distance": o.Distance = F(); break;
                case "--screenshot": o.Screenshot = Next(); break;
                case "--size":
                    var parts = Next().Split('x');
                    o.Width = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    o.Height = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    break;
                case "--no-objects": o.NoObjects = true; break;
                case "--no-foliage": o.NoFoliage = true; break;
                case "--no-load-cache": Meitou.Data.World.LoadCaches.Disabled = true; break;
                case "--range-large": o.LargeRange = F(); break;
                case "--impostor-budget": o.ImpostorBudgetMb = F(); break;
                case "--object-distance": o.ObjectDistance = F(); break;
                case "--distant-range": o.DistantZones = F(); break;
                case "--debug": o.Debug = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--wireframe": o.Wireframe = true; break;
                case "--time": o.Hour = F(); break;
                case "--time-speed": o.TimeSpeed = F(); break;
                case "--no-water": o.NoWater = true; break;
                case "--water-grid": o.WaterGrid = Math.Clamp(int.Parse(Next(), CultureInfo.InvariantCulture), 64, 1024); break;
                case "--water-ocean": o.WaterOcean = int.Parse(Next(), CultureInfo.InvariantCulture) switch { <= 64 => 64, <= 128 => 128, _ => 256 }; break;
                case "--water-reflection": o.WaterReflection = Math.Clamp(int.Parse(Next(), CultureInfo.InvariantCulture), 0, 4); break;
                case "--reflection-samples":
                {
                    int samples = int.Parse(Next(), CultureInfo.InvariantCulture);
                    o.ReflectionSamples = samples <= 1 ? 1 : samples;
                    break;
                }
                case "--texture-quality": o.TextureQuality = Math.Clamp(int.Parse(Next(), CultureInfo.InvariantCulture), 0, Meitou.Data.Textures.TextureQuality.Maximum); break;
                case "--shadow-quality": o.ShadowQuality = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--shadow-filter": o.ShadowFilter = Math.Clamp(int.Parse(Next(), CultureInfo.InvariantCulture), 0, 2); break;
                case "--shadow-range":
                {
                    float range = F();
                    o.ShadowsOff = range <= 0;
                    if (!o.ShadowsOff) o.ShadowRange = Math.Clamp(range, KenshiShadows.MinRange, CommandLineMaxShadowRange);
                    break;
                }
                case "--debug-shadows": o.DebugShadows = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--gi-debug": o.GiDebug = Math.Clamp(int.Parse(Next(), CultureInfo.InvariantCulture), 0, 3); break;
                case "--no-gi": o.Gi = false; break;
                case "--meitou": Enhancements.Apply(Switches(o), Next(), meitou: true); break;
                case "--faithful": Enhancements.Apply(Switches(o), Next(), meitou: false); break;
                case "--weather": o.Weather = Next(); break;
                case "--day": o.Day = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--no-particles": o.NoParticles = true; break;
                case "--no-audio": o.NoAudio = true; break;
                case "--audio-gain": o.AudioGainDb = F(); break;
                case "--particle-prewarm": o.ParticlePrewarm = Math.Max(F(), 0); break;
                case "--particle-density": o.ParticleDensity = Math.Clamp(F(), 0.1f, 1); break;
                case "--anisotropy": o.Anisotropy = (int)F(); break;
                case "--clouds": o.Clouds = F(); break;
                case "--show-keys": o.ShowKeys = true; break;
                case "--play-shots": o.PlayShots = true; break;
                case "--no-vsync": o.VSync = false; break;
                case "--grass-density": o.GrassDensity = Math.Clamp(F(), 0.1f, 2); break;
                case "--low-end": o.ApplyLowEnd(); break;
                case var post when o.Post.TryParse(post, Next): break;
                case "--camera-code":
                {
                    string code = Next();
                    o.CameraPose = WorldCameraCode.Decode(code) ?? throw new ArgumentException($"--camera-code: '{code}' is not a camera code (the viewer's Ctrl+C)");
                    (o.X, o.Z) = (o.CameraPose.Value.Target.X, o.CameraPose.Value.Target.Z);   // loaded round it
                    break;
                }
                case "--fly-to": (o.FlyToX, o.FlyToZ) = Pair(); break;
                case "--fly-benchmark": o.FlyBenchmark = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--fly-radius": o.FlyRadius = F(); break;
                case "--fly-speed": o.FlySpeed = F(); break;
                case "--fly-pipelined": o.FlyPipelined = true; break;
                case "--fly-csv": o.FlyCsv = Next(); break;
                case "--ab": o.Ab = Next(); break;
                case "--ab-period": o.AbPeriod = Math.Max(int.Parse(Next(), CultureInfo.InvariantCulture), 1); break;
                case "--bench-frames": o.BenchFrames = Math.Max(int.Parse(Next(), CultureInfo.InvariantCulture), 1); break;
                case "--bench-out": o.BenchOut = Next(); break;
                case "--bench-motion": o.BenchMotion = Next() is var motion && motion is "still" or "orbit" or "fly" or "turn" ? motion : throw new ArgumentException("--bench-motion: still, orbit, fly or turn"); break;
                case "--bench-orbit": o.BenchOrbit = F(); break;
                case "--no-gpu-lock": o.NoGpuLock = true; break;
                case "--bench-serial": o.BenchSerial = true; break;
                case "--bench-tris": o.BenchTris = true; break;
                case "--bench-compare": o.BenchCompare = (Next(), Next()); break;
                case "--log-spikes": SpikeLog.Enabled = true; break;
                case "--orbit-step": o.OrbitStep = F() * MathF.PI / 180; break;
                case "--monitor": o.Monitor = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--windowed": o.Windowed = true; break;
                case "--sway-step": o.SwayStep = F(); break;
                case "--crowd": o.Crowd = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--fog": o.FogDistance = F(); break;
                case "--material-distance": o.MaterialDistance = F(); break;
                case "--terrain-error": o.TerrainError = F(); break;
                case "-h" or "--help": return null;
                default: throw new ArgumentException($"unknown option {a}");
            }
        }
        if (o.Radius <= 0) throw new ArgumentException("--radius must be positive");
        if (!sizeGiven && (o.BenchFrames > 0 || o.Ab is not null)) (o.Width, o.Height) = BenchSize;
        return o;
    }
}

/// <summary>
/// The scene's passes as a native host (wave 4, docs/renderer-native.md 4.5 and 6; phase 8 stage 3, 8.9): <see cref="Open"/> begins a rendering
/// of the slice's targets whose contents are secondaries, clearing them by load ops where asked, and hands the guests the targets and the scene's
/// state; their segments are queued (<see cref="GpuContext.Record"/>) or recorded at once into secondaries of their own (sky, water, the grass,
/// timestamps); <see cref="Close"/> records the queued jobs on the job threads and executes everything in order. <see cref="ClearDepth"/> clears
/// the depth in its place among the guests.
/// </summary>
sealed class SceneHost(IGpuContext ctx)
{
    IGpuCommandList? cmd;
    PassTargets? targets;

    public bool IsOpen => cmd is not null;

    /// <summary>Opens the host on <paramref name="targets"/> (nothing when it is open already); its jobs count towards <paramref name="stage"/>.
    /// <paramref name="clearColour"/> / <paramref name="clearDepth"/>: the attachments are cleared by the rendering's load ops (to the colour, to 1).
    /// Without secondaries (<see cref="IGpuContext.RecordsSecondaries"/>) the guests record straight into its rendering.</summary>
    public void Open(int stage, PassTargets targets, ClearColour? clearColour = null, bool clearDepth = false)
    {
        if (cmd is not null) { Stage(stage); return; }
        var list = ctx.BeginNative("scene");
        bool secondaries = ctx.RecordsSecondaries;
        var colour = clearColour is { } c ? targets.Colour with { Load = LoadOp.Clear, Clear = c } : targets.Colour;
        var depth = clearDepth ? targets.Depth with { Load = LoadOp.Clear, DepthClear = 1f } : targets.Depth;
        // The extra target (the GI resolve's albedo) is cleared to 0 with the colour: no surface there takes the probes' light in post.
        var extra = clearColour is not null && !targets.Extra.IsNull ? targets.Extra with { Load = LoadOp.Clear, Clear = default } : targets.Extra;
        list.BeginRendering(new RenderingDesc(colour, depth, targets.Width, targets.Height, 0, 0, targets.ShadingRate, targets.ShadingRateTexel, extra), secondaries);
        this.targets = targets;
        ctx.BeginHostPassParallel(list, targets, DrawState.Scene(targets.Formats, DrawState.Rgb), stage);   // alpha: the characters only (the SSAO character mask)
        cmd = list;
    }

    /// <summary>The <see cref="StageClock"/> stage the guests' jobs count towards from now on.</summary>
    public void Stage(int stage)
    {
        if (cmd is not null) ctx.SetPassStage(stage);
    }

    /// <summary>Clears the open rendering's depth to 1 over the whole target, in its place among the guests' segments.</summary>
    public void ClearDepth()
    {
        if (cmd is not { } list || targets is not { } t) throw new InvalidOperationException("ClearDepth without an open scene host");
        ctx.ClearDepth(list, 1f, new PixelRect(0, 0, (uint)t.Width, (uint)t.Height));
    }

    public void Close()
    {
        if (cmd is not { } list) return;
        // Vulkan's queued guests execute into the rendering's secondaries; they must finish before EndRendering.
        ctx.EndHostPassParallel(list);
        list.EndRendering();
        ctx.EndHostPass(list);
        ctx.EndNative(list);
        cmd = null;
        targets = null;
    }
}

/// <summary>The loaded world region: heights (the region fine, the whole world coarse), and where the camera starts.</summary>
sealed class WorldScene : IDisposable
{
    /// <summary>The world heightmap file; null for the flat floor of the game's animation sandbox (<see cref="FlatHeight"/>).</summary>
    public TerrainHeightmap? Heightmap;
    public float FlatHeight;
    public required HeightWindow Window;
    /// <summary>Whole-world heights every <see cref="WorldFrame.CoarseStep"/>-th sample, (CoarseSize)² raw values.</summary>
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
/// The world frame: loading the region, creating the renderers, and drawing a frame (shadows, reflection, sky, the depth
/// slices, water, post-processing). Shared by the game (<c>meitou</c>) and the viewer (<c>meitou-viewer --world</c>).
/// </summary>
static class WorldFrame
{
    /// <summary>Heightmap step of the whole-world height grid behind the loaded region (2049² samples, 144 units apart).</summary>
    public const int CoarseStep = 8;

    /// <summary>The weather scheduler's random seed (the default of <c>meitou-tools weather</c>, so the two agree).</summary>
    const int WeatherSeed = 1;

    /// <summary>The water reflection's range, a multiple of the haze distance (the game's own <c>reflection range</c> is 0.6; the viewer reflects further; the Tab slider moves it).</summary>
    const float ReflectionRange = 3;

    public static WorldScene? Load(GameInstall install, WorldOptions o, GameDatabase? preloaded = null)
    {
        var watch = Stopwatch.StartNew();
        var map = TerrainHeightmap.Open(install);
        GameDatabase? db = null;
        if (o.Town is not null || !o.NoTextures || !o.NoObjects)
        {
            db = preloaded ?? GameDatabase.Load(LoadOrder.FromInstall(install));
            Console.WriteLine($"game data {db.Records.Count} records ({watch.ElapsedMilliseconds} ms)");
        }

        double x = 0, z = 0;
        float? y = null;
        if (o.X is { } ax && o.Z is { } az) (x, z) = (ax, az);
        else if (o.Zone is { } zone)
        {
            var (zx, zz) = WorldLayout.ZoneOrigin(zone);
            (x, z) = (zx + WorldLayout.ZoneSize / 2.0, zz + WorldLayout.ZoneSize / 2.0);
        }
        else if (o.Town is { } name)
        {
            var levels = WorldLevelData.Load(install);
            var towns = levels.Towns().Select(t => (Place: t, Record: db!.Find(t.TownId)))
                .Where(t => t.Record is not null && t.Record.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.Record!.Name.Length).ToList();
            if (towns.Count == 0)
            {
                Console.Error.WriteLine($"No placed town matches '{name}'.");
                map.Dispose();
                return null;
            }
            foreach (var t in towns.Take(8))
                Console.WriteLine($"town      '{t.Record!.Name}' ({t.Record.StringId}) at {t.Place.Position.X:0}, {t.Place.Position.Z:0}");
            var pick = towns[0].Place.Position;
            (x, z, y) = (pick.X, pick.Z, pick.Y);
        }

        // Window around the point, a whole number of 64-cell blocks per side.
        const int chunkCells = 64;
        double half = o.Radius * WorldLayout.ZoneSize;
        int cellsWanted = (int)Math.Ceiling(2 * half / WorldLayout.SampleSpacing);
        int step = 1;
        while (cellsWanted / step > 2048) step *= 2;   // the smallest power of two that keeps the window to 2048 cells a side
        int cells = (cellsWanted / step + chunkCells - 1) / chunkCells * chunkCells;
        var (sc, sr) = WorldLayout.ToSample(x - half, z - half);
        int c0 = (int)Math.Floor(sc / step) * step, r0 = (int)Math.Floor(sr / step) * step;
        var window = map.ReadWindow(c0, r0, cells + 1, cells + 1, step);
        var coarse = map.Downsample(CoarseStep, out int coarseSize);
        var (x0, z0) = window.WorldOf(0, 0);
        var (x1, z1) = window.WorldOf(cells, cells);
        var focus = new Vector3((float)x, y ?? map.HeightAt(x, z), (float)z);
        Console.WriteLine($"terrain   {cells}x{cells} cells, step {step} ({window.Spacing} units), world grid {coarseSize}² every {CoarseStep * WorldLayout.SampleSpacing} units; " +
            $"X {x0:0}..{x1:0}, Z {z0:0}..{z1:0} (zones {WorldLayout.ZoneOf(x0, z0)} .. {WorldLayout.ZoneOf(x1 - 1, z1 - 1)}) ({watch.ElapsedMilliseconds} ms)");
        Console.WriteLine($"focus     {focus.X:0}, {focus.Y:0}, {focus.Z:0} in zone {WorldLayout.ZoneOf(focus.X, focus.Z)}");
        var scene = new WorldScene { Heightmap = map, Window = window, Coarse = coarse, CoarseSize = coarseSize, Focus = focus, X0 = x0, Z0 = z0, X1 = x1, Z1 = z1, Database = db };
        if (db is not null) scene.Clock = SkyClock.FromDatabase(db);
        if (!o.NoObjects && db is not null)
            scene.Objects = WorldObjects.Load(install, db, map, x0, z0, x1, z1);
        Console.WriteLine($"loaded in {watch.ElapsedMilliseconds} ms");
        return scene;
    }
    /// <summary>
    /// A flat world for the game's animation sandbox: the whole terrain at one height (a 256² window of heights round the origin and a flat whole-world grid),
    /// no heightmap file, no objects. The caller turns textures, objects, foliage and water off in <paramref name="o"/>.
    /// </summary>
    public static WorldScene LoadFlat(WorldOptions o, GameDatabase db, ushort raw = 3344)
    {
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
        return new WorldScene { Window = window, Coarse = coarse, CoarseSize = coarseSize, FlatHeight = height, Focus = new Vector3(0, height, 0), X0 = x0, Z0 = z0, X1 = x1, Z1 = z1, Database = db, Clock = SkyClock.FromDatabase(db) };
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
            SplitDistance = Math.Max(20000, o.ObjectDistanceFor(o.MeitouReach) * 1.1f),
        };
        if (o.CameraPose is { } pose) WorldCameraCode.Apply(pose, camera);
        float terrainError = o.TerrainErrorFor(o.MeitouReach);
        var render = new WorldRenderOptions { Textures = !o.NoTextures, Objects = !o.NoObjects, Water = !o.NoWater, Wireframe = o.Wireframe ? 1 : 0, Debug = o.Debug, MaterialDistance = o.MaterialDistance,
            TerrainPixelError = terrainError };
        return (camera, render);
    }

    internal sealed class Gpu : IDisposable
    {
        public required TerrainRenderer Terrain;
        public required SkyRenderer Sky;
        public WaterRenderer? Water;
        /// <summary>The weather's wetness, dust and rain for every surface shader (null without a game database).</summary>
        public WeatherSurfaces? Surfaces;
        /// <summary>The placed fog volumes of <c>fogfeatures.dat</c> (null when the install has none).</summary>
        public FogVolumes? FogVolumes;
        /// <summary>This frame's weather fog spheres (<see cref="ParticleRenderer.CollectFogVolumes"/>), reused.</summary>
        public readonly List<FogVolumes.EffectFog> EffectFogs = [];
        /// <summary>The weather's particle effects (null while there are none: a clear weather, or <c>--no-particles</c>); <see cref="EnsureParticles"/> makes it.</summary>
        public ParticleRenderer? Particles;
        /// <summary>The movement marker the game interface shows after a ground move order (null in the viewer, or without <c>position.mesh</c>).</summary>
        public MarkerRenderer? Markers;
        /// <summary>The blood decals on the ground (null in the viewer, or with the game's <c>Blood</c> setting off) and the host's decals they draw (docs/game/blood-decals.md).</summary>
        public TerrainDecalRenderer? Decals;
        public TerrainDecalStore? DecalStore;
        /// <summary>luckyren Mode's flying bones and limbs (null until the host makes it) and the pieces it draws this frame (docs/game/luckyren.md).</summary>
        public DebrisRenderer? Debris;
        /// <summary>This frame's bolts stuck in characters (<see cref="CharacterRenderer.CollectBolts"/>), drawn with the debris.</summary>
        public readonly List<DebrisPiece> BoltPieces = [];
        public readonly List<DebrisPiece> DebrisPieces = [];
        /// <summary>The buildings' interior masks the terrain, foliage and map features are cut by (made with the first frame that has objects; docs/render-interior-cut.md).</summary>
        public InteriorMaskRenderer? InteriorMask;
        /// <summary>The navmesh debug view (null in the viewer; drawn while visible).</summary>
        public NavmeshOverlayRenderer? NavmeshOverlay;
        /// <summary>The hover outline the game draws round the character under the mouse, and with Alt those near (null in the viewer); <see cref="HoverKeys"/> picks the characters by their draw-list keys, each with its colour (empty: none).</summary>
        public HoverOutlineRenderer? HoverOutline;
        public IReadOnlyList<(long Key, Vector4 Colour)> HoverKeys = [];
        /// <summary>
        /// A presentation cap on how far the frame reaches (the Meitou editor's bar, high above the world): the far plane and the shadow cascades end there, so the world below
        /// leaves the view; null in play.
        /// </summary>
        public float? ViewCap;
        /// <summary>The Tab panel's weather particles at 0 and its particle density, given to <see cref="Particles"/> every frame (it is made only with the first weather).</summary>
        public bool WeatherParticles = true;
        public float ParticleDensity = 1;
        /// <summary>Makes the particle renderer on demand (null with <c>--no-particles</c>).</summary>
        public Func<ParticleRenderer>? MakeParticles;
        /// <summary>The weather regions' cells, and the colour of the camera's cell the particle area was made for.</summary>
        public WeatherAreas? WeatherAreas;
        public int ParticleAreaColour = -1;
        public ReflectionPass? Reflection;
        public ShadowPass? Shadow;
        public int DebugShadows;
        /// <summary>The ray-traced global illumination's scene and debug view (null without <c>--gi-debug</c> or ray queries; docs/render-gi.md).</summary>
        internal Gi.GiScene? Gi;
        internal Gi.GiDebugPass? GiDebugPass;
        internal Gi.GiProbes? Probes;
        /// <summary>A backend-owned probe GI (Metal's native ray tracing); Vulkan uses <see cref="Gi"/> and <see cref="Probes"/> instead.</summary>
        internal Gi.IProbeGi? ProbeGi;
        internal WorldLamps? Lamps;
        /// <summary>The placed buildings' effects (campfires, torches, smoke; docs/formats/lights.md "Effects on buildings"), read on a worker, handed to the particles once read.</summary>
        internal Task<List<PlacedEffect>>? BuildingEffects;
        public int GiDebug;
        /// <summary>Whether the traced scene is updated (and the debug view drawn) this frame: the bench's gi-scene switch.</summary>
        public bool GiActive = true;
        /// <summary>The GI resolve's divisor (0 the lit fragments read the probes, 1 full size, 2 half; the bench's gi-resolve switch; docs/render-gi.md "Resolve").</summary>
        public int GiResolve = 2;
        /// <summary>The Vulkan post chain (its reflection and GI also read it); null on a backend with a seam-only chain.</summary>
        public PostProcess? Post;
        /// <summary>The chain <see cref="WorldFrame.Draw"/> runs: <see cref="Post"/> on Vulkan (also when a host sets only that), the host's seam chain on Metal.</summary>
        public IWorldPostChain? PostChain { get => postChain ?? Post; set => postChain = value; }
        IWorldPostChain? postChain;
        public WorldObjectRenderer? Objects;
        public FoliageRenderer? Foliage;
        /// <summary>The characters (null without <c>--crowd</c> or a host that feeds them).</summary>
        public Characters.CharacterRenderer? Characters;
        public TerrainStreamer? Streamer;
        /// <summary>Where the camera is about to arrive (a character flight's end view's eye; null otherwise): the object and terrain texture streaming load there too.</summary>
        public Vector3? Ahead;
        /// <summary>Points whose surroundings stay loaded besides the camera's (the player's characters, "Keep textures at characters"): object meshes and textures, terrain biome textures.</summary>
        public IReadOnlyList<Vector3> Kept = [];
        /// <summary>Keeps the video memory under the budget by clamping the ranges and pausing the streaming (null with <c>MEITOU_VRAM_GUARD=0</c>).</summary>
        public VramGuard? Guard;
        /// <summary>The scene's native host (wave 4), made at the first frame.</summary>
        public SceneHost? Scene;
        /// <summary>The heat haze's target this frame (the weather's <c>heat haze</c> × strength 1 × the sun factor), for the statistics.</summary>
        public float HeatHazeTarget;
        /// <summary>Game hours since the load for the heat haze's animation, when the caller runs a game clock; null: real time at game speed 1.</summary>
        public double? GameHours;
        /// <summary>The weather scheduler hooked to the frame (null without a game database). The host sets <see cref="WorldWeather.Day"/>, <see cref="WorldWeather.GameSpeed"/> and <see cref="WorldWeather.Paused"/>.</summary>
        public WorldWeather? Weather;
        /// <summary>The weather at the camera this frame (what the other renderers read; <see cref="WeatherState.Clear"/> without a scheduler).</summary>
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
            Water?.Dispose();
            Surfaces?.Dispose();
            Reflection?.Dispose();
            Shadow?.Dispose();
            Post?.Dispose();   // a seam-only chain (MetalPostChain) is owned by its host
            Sky.Dispose();
            Terrain.Dispose();
        }
    }

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

    /// <summary>Which collector runs: the runtime's server or workstation GC (docs/engine.md).</summary>
    public static string GcName() => System.Runtime.GCSettings.IsServerGC ? "server" : "workstation";

    /// <summary>
    /// Feeds the particle renderer the weather state's effect list, strength and wind (the renderer is made the first time a weather has effects; a weather
    /// without any empties it again). The effect groups are rebuilt only when the weather's list changes.
    /// </summary>
    static void UpdateParticles(Gpu gpu, WorldCamera camera)
    {
        if (gpu.Weather is not { } weather || gpu.MakeParticles is null) return;
        var input = weather.EffectInput();
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

    /// <summary>The particle renderer (reads the scripts and materials, makes the pass's program) if there is none yet; for whoever feeds it a weather's effect list.</summary>
    public static ParticleRenderer EnsureParticles(Gpu gpu, IGpuContext context, GameInstall install)
    {
        if (gpu.Particles is { } existing) return existing;
        return gpu.Particles = ParticleRenderer.Create(context, install);
    }

    /// <summary>The renderer for <paramref name="scene"/>. <paramref name="step"/> is called before each of its steps (terrain, biomes, sky, water, objects, foliage, shadows) with the step's name: the game draws a loading frame there.</summary>
    /// <paramref name="wait"/> waits for the CPU work started on workers at the beginning (the terrain textures' and the water's files and bakes); the game draws loading frames while it waits.
    public static Gpu CreateGpu(IGpuContext context, GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o, bool interactive,
        Action<string>? step = null, Action<Task>? wait = null, bool terrainTextures = true, bool particlesEnabled = true, int objectLoadBudget = -1,
        Func<(ulong Used, ulong Budget)>? vramSample = null)
    {
        var watch = Stopwatch.StartNew();
        context.Samplers.MaxAnisotropy = o.Anisotropy;
        // The game's texture quality, before any texture loads: the world's texture caches read it as they decode. The terrain's layer arrays
        // have one size, so the Landscape group's drop (the game's textures are 2048²) lowers it instead of dropping mips per file.
        Meitou.Data.Textures.TextureQuality.Level = o.TextureQuality;
        int layerSize = Math.Max(2048 >> Meitou.Data.Textures.TextureQuality.LevelsToDrop("terrain.dds", "Landscape"), 16);
        if (layerSize != 2048) Console.WriteLine($"textures  quality {o.TextureQuality} ({Meitou.Data.Textures.TextureQuality.Labels[o.TextureQuality]}): terrain layers {layerSize}² instead of 2048²");
        // The terrain colour tiles BC3-encoded for the colour window, once (TerrainColourCache): a first start, or one after a tile changed, encodes them on workers.
        var colourEncode = !o.NoTextures && terrainTextures && TerrainColourCache.Missing(install)
            ? Task.Run(() => { var w = Stopwatch.StartNew(); int n = TerrainColourCache.EncodeMissing(install); return (n, w.ElapsedMilliseconds); }) : null;
        // The files the terrain textures and the water read and bake (seconds of CPU work) are read on workers while the steps before them run.
        var terrainSource = !o.NoTextures && terrainTextures && scene.Database is { } textureDb ? Task.Run(() => TerrainTextures.Prepare(install, textureDb)) : null;
        var waterSource = !o.NoWater && scene.Database is { } waterDb ? Task.Run(() => WaterRenderer.Prepare(install, waterDb, assets, scene.Coarse)) : null;
        T Ready<T>(Task<T> work) { if (wait is not null) wait(work); return work.GetAwaiter().GetResult(); }
        step?.Invoke("terrain");
        var terrain = new TerrainRenderer(context, scene.Coarse, scene.CoarseSize, scene.Window);
        Console.WriteLine($"uploaded  terrain heights: {terrain.LevelCount} LOD levels, finest {terrain.FinestSpacing:0.#} units ({watch.ElapsedMilliseconds} ms)");
        TerrainTextures? textures = null;
        if (colourEncode is not null)
        {
            if (!colourEncode.IsCompleted) step?.Invoke("colourmap");
            var (tiles, ms) = Ready(colourEncode);
            Console.WriteLine($"colour    {tiles} terrain colour tiles encoded to BC3 and cached in {ms} ms");
        }
        if (!o.NoTextures && terrainTextures && scene.Database is not null)
        {
            step?.Invoke("biomes");
            textures = TerrainTextures.Create(context, install, scene.Database, assets, layerSize, source: Ready(terrainSource!));
            foreach (var m in textures.Messages.Take(20)) Console.WriteLine($"warning   {m}");
            Console.WriteLine($"biomes    {textures.TotalBiomes} in the world, {textures.TotalPairs} texture pairs, {textures.Capacity} slots of {layerSize}² BC3+BC1, {textures.ArrayBytes / 1048576} MB ({watch.ElapsedMilliseconds} ms)");
            terrain.SetTextures(textures);
        }
        step?.Invoke("sky");
        // WorldFrame.Draw runs a post chain through IWorldPostChain. Vulkan's PostProcess is complete here; Metal supplies its
        // PostComposite/PostTaa slice as a host-constructed chain and leaves the Vulkan-only reflection and GI paths null.
        var post = context is GpuContext vulkanPost ? new PostProcess(vulkanPost, o.Post) : null;
        var gpu = new Gpu { Terrain = terrain, Sky = new SkyRenderer(context, assets) { CloudCoverage = o.Clouds }, Post = post, PostChain = post };
        post?.LoadHeatHaze(assets);
        // The placed fog volumes (docs/formats/fogfeatures.md): the swamp's fog and the like, part of the game's look.
        // Always made: the weather's fog spheres go through it too, with or without a fogfeatures.dat.
        gpu.FogVolumes = new FogVolumes(context, o.NoFogFeatures ? [] : FogFeatures.Load(install));
        if (post is not null) post.FogVrs = true;   // takes effect where the device supports fragment shading rates
        gpu.Sky.HazeStrength = o.HazeStrength;
        gpu.Sky.MeitouHaze = o.MeitouHaze;
        gpu.Sky.NightAir = o.NightAir;
        gpu.Sky.Planetshine = o.Planetshine;
        gpu.Sky.PlanetshineStrength = o.PlanetshineStrength;
        gpu.Sky.MeitouStars = o.MeitouStars;
        gpu.Sky.LitClouds = o.MeitouClouds;
        gpu.Sky.CloudShadows = o.CloudShadows;
        gpu.Sky.CloudFog = o.CloudFog;
        step?.Invoke("weather");
        if (scene.Database is { } skyDb)
        {
            gpu.Sky.LoadWorld(install, skyDb);   // the ambient map and the CONSTANTS exposure band (docs/formats/lighting.md)
            // The weather scheduler (docs/formats/weather.md): auto, or one forced record through the same path.
            var world = WeatherWorld.Create(skyDb, install, WeatherSeed, WeatherTime.At(0));   // created on day 0; WorldWeather replays the schedule up to --day
            gpu.Weather = new WorldWeather(world, skyDb) { Day = o.Day ?? 0 };
            if (!o.AutoWeather)
            {
                if (!world.ForceWeather(o.Weather))
                    throw new ArgumentException($"no weather named '{o.Weather}'; known: {string.Join(", ", SkyWeather.Names(skyDb).Distinct().Take(12))} ...");
                gpu.Sky.Weather = SkyWeather.Find(skyDb, o.Weather) ?? SkyWeather.Default;
            }
            Console.WriteLine($"weather   {(o.AutoWeather ? $"scheduler, seed {WeatherSeed}, from day {o.Day ?? 0}" : $"forced {o.Weather}")}");
            // Wetness, dust and rain for the surface shaders come from the weather state every frame (Draw).
            gpu.Surfaces = new WeatherSurfaces(context, assets);
        }
        // The weather's particle effects (rain, ash...) follow the weather state (UpdateParticles); the map's effect placers are always on
        // (docs/formats/weather.md "Effect placers on the map"), so the particle renderer exists from the start whatever the weather.
        step?.Invoke("particles");
        if (particlesEnabled && !o.NoParticles && scene.Database is { } effectsDb)
        {
            var particleWatch = Stopwatch.StartNew();
            var particles = EnsureParticles(gpu, context, install);
            particles.PrewarmSeconds = o.ParticlePrewarm;
            gpu.ParticleDensity = o.ParticleDensity;
            particles.World.GroundHeight = terrain.HeightAt;   // main thread only: the height grid swaps as the terrain streams
            particles.World.FreezeGroundHeight = () => terrain.Snapshot().HeightAt;   // for a weather warmed on a worker: the grid as it is when it starts
            gpu.WeatherAreas = WeatherAreas.Load(install);
            particles.World.Area = RegionArea.At(gpu.WeatherAreas, scene.Focus.X, scene.Focus.Z);
            var placers = MapEffectPlacers.Find(effectsDb, MapFeatureFile.Open(install));
            particles.SetPlacers(placers);
            gpu.MakeParticles = () => particles;
            Console.WriteLine($"particles {particles.Library.Systems.Count} systems read; {placers.Count} map placers in {particles.PlacerGroups.Count} groups ({particleWatch.ElapsedMilliseconds} ms)");
        }
        if (scene.Database is { } lampDb && scene.Heightmap is { } lampMap)
        {
            // The lamps (docs/render-lights.md), including room lights, and the buildings' effects, read on workers from one level load.
            var levels = new Lazy<(WorldLevelData Levels, BuildingLayouts Layouts)>(() =>
            {
                var l = WorldLevelData.Load(install, includeInteriors: true);
                return (l, new BuildingLayouts(lampDb, l.Interiors));
            });
            gpu.Lamps = new WorldLamps(context, () => WorldLights.ForWorld(lampDb, levels.Value.Levels, lampMap.HeightAt, levels.Value.Layouts))
                { InteriorAllDay = o.InteriorLamps };
            if (gpu.MakeParticles is not null)
                gpu.BuildingEffects = Task.Run(() => BuildingEffects.ForWorld(lampDb, levels.Value.Levels, lampMap.HeightAt, levels.Value.Layouts));
        }
        if (scene.Heightmap is not null) gpu.Streamer = new TerrainStreamer(install, terrain, textures, scene.Window.Step) { MaterialDistance = o.MaterialDistance };
        if (!o.NoWater && scene.Database is not null)
        {
            var messages = new List<string>();
            step?.Invoke("water");
            // The Meitou water's FFT, shore field and reflection are seam-neutral. Metal packs the two biome
            // parameter maps into one array and drops refraction (its scene copy is still Vulkan-only), which
            // leaves the Meitou fragment exactly at Metal's 16-sampler stage limit.
            // Vulkan always makes the Meitou water, so the Faithful / Meitou switch can turn it on while the game runs; another backend makes
            // it only when the run starts with it.
            bool meitouWater = o.MeitouWater, makeMeitou = meitouWater || context is GpuContext;
            gpu.Water = WaterRenderer.Create(context, install, scene.Database, assets, gpu.Sky, messages, scene.Coarse, o.WaterGrid, o.WaterOcean,
                Ready(waterSource!), meitouWaves: makeMeitou, shoreField: makeMeitou);
            gpu.Water.Meitou = meitouWater;
            step?.Invoke("reflections");
            gpu.Reflection = new ReflectionPass(context) { Level = o.WaterReflection, Range = ReflectionRange, Samples = o.ReflectionSamples };
            foreach (var m in messages) Console.WriteLine($"warning   {m}");
            Console.WriteLine($"water     at height {WorldWater.Height} ({watch.ElapsedMilliseconds} ms)");
        }
        if (scene.Objects is not null)
        {
            // Landmarks (huge objects, drawn to their own distance) are kept apart from the zones only when the viewer starts with them (Meitou reach, distance above 0).
            step?.Invoke("objects");
            int loadBudget = objectLoadBudget >= 0 ? objectLoadBudget : interactive ? 8 : 0;
            gpu.Objects = new WorldObjectRenderer(context, assets, scene.Objects, landmarks: o.LandmarkDistanceFor(o.MeitouReach) > 0)
            { ObjectDistance = o.ObjectDistanceFor(o.MeitouReach), LandmarkDistance = o.LandmarkDistanceFor(o.MeitouReach), DistantRange = o.DistantZones * WorldLayout.ZoneSize, NoDistant = o.DistantZones <= 0, LoadBudget = loadBudget, SortNearestFirst = o.MeitouReach, LodBias = 1 / o.ObjectLod };
            Console.WriteLine($"objects   GPU ready ({watch.ElapsedMilliseconds} ms)");
        }
        if (!o.NoFoliage && scene.Database is not null)
        {
            step?.Invoke("foliage");
            gpu.Foliage = new FoliageRenderer(context, install, scene.Database, scene.Objects?.Levels ?? WorldLevelData.Load(install), assets);
            if (!interactive) gpu.Foliage.SwaySeconds = 0;   // offscreen pictures and benchmarks: the grass holds still, so a picture repeats exactly
            var f = gpu.Foliage;
            f.Terrain = terrain;
            f.MeitouRange = o.MeitouRange;
            // --range-large moves the medium and small ranges with it, at their default ratios to the large one.
            if (o.LargeRange is { } largeRange)
            {
                float k = largeRange / f.LargeRange;
                (f.SmallRange, f.MediumRange, f.LargeRange) = (f.SmallRange * k, f.MediumRange * k, largeRange);
            }
            f.Lod = o.FoliageLod;
            (f.Impostors, f.ImpostorDistance, f.ImpostorBudgetMb) = (o.Impostors, o.ImpostorDistance ?? f.ImpostorDistance, o.ImpostorBudgetMb ?? f.ImpostorBudgetMb);
            if (o.LargeImpostorDistance is { } largeImpostor) f.LargeImpostorDistance = largeImpostor;
            if (o.GrassDensity is { } grassDensity) f.GrassDensitySetting = grassDensity;
            Console.WriteLine($"foliage   catalog and shaders ready ({gpu.Foliage.LoadMs:0} ms)");
        }
        // The game's CSM mode (docs/formats/shadows.md): four cascades in one atlas of the `shadow quality` side, out to `Shadow Range`.
        step?.Invoke("shadows");
        gpu.Shadow = new ShadowPass(context, assets) { Settings = new ShadowSettings(KenshiShadows.MapSize(o.ShadowQuality), o.ShadowRangeFor(o.MeitouShadows)), Meitou = o.MeitouShadows, FilterQuality = o.ShadowFilter, Enabled = !o.ShadowsOff };
        if (!gpu.Shadow.HasNoise) Console.WriteLine($"warning   shadows: {KenshiShadows.NoiseTexture} not found, the receiver's jitter is a hash");
        gpu.Shadow.SetTerrain(scene.Coarse, scene.CoarseSize);   // the Meitou shadows' terrain shadow beyond the range
        Console.WriteLine($"shadows   {gpu.Shadow.Settings.MapSize}² atlas, {gpu.Shadow.CascadeCount} cascades of {gpu.Shadow.Settings.TileSize}² ({(gpu.Shadow.Meitou ? "Meitou: a layer each" : "Faithful: tiles of one layer")}), range {gpu.Shadow.Settings.Range:0}");
        if (o.Crowd > 0 && scene.Database is not null) gpu.Characters = Characters.CrowdHarness.Create(context, install, scene, assets, o, interactive);
        if (gpu.Characters is { } crowd) (crowd.GeneratedLod, crowd.SkinShading) = (o.CharacterLod, o.SkinShading);
        gpu.DebugShadows = o.DebugShadows;
        step?.Invoke("gi");
        if ((o.GiDebug > 0 || o.Gi) && context is GpuContext giContext)
        {
            if (!giContext.Device.HasRayQuery) Console.WriteLine("gi        off: the device has no ray queries (VK_KHR_ray_query)");
            else
            {
                gpu.Gi = new Gi.GiScene(giContext);
                if (o.GiDebug > 0)
                {
                    gpu.GiDebugPass = new Gi.GiDebugPass(giContext);
                    gpu.GiDebug = o.GiDebug;
                    Console.WriteLine($"gi        debug view {o.GiDebug}");
                }
                if (o.Gi)
                {
                    gpu.Probes = new Gi.GiProbes(giContext) { Enabled = o.GiProbes, InteriorEnabled = o.InteriorGi };
                    Console.WriteLine($"gi        {gpu.Probes.Describe()}");
                }
            }
        }
        // The memory-pressure guard (VramGuard): MEITOU_VRAM_GUARD=0 leaves it off. Vulkan samples its allocator;
        // another backend supplies the sample (Metal's device allocation and conservative budget).
        if (Environment.GetEnvironmentVariable("MEITOU_VRAM_GUARD") != "0")
        {
            VramGuard? guard = context is GpuContext vulkanGuard
                ? new VramGuard(vulkanGuard.Device.VideoMemory) { Allocator = vulkanGuard.Device.Allocator }
                : vramSample is not null ? new VramGuard(vramSample) : null;
            if (guard is not null)
            {
                gpu.Guard = guard;
                if (gpu.Objects is not null) gpu.Objects.Guard = guard;
                if (gpu.Foliage is not null) gpu.Foliage.Guard = guard;
                if (gpu.Shadow is not null) gpu.Shadow.Guard = guard;
                if (gpu.Characters is not null) gpu.Characters.Guard = guard;
            }
        }
        return gpu;
    }

    /// <summary>The depth slices this chain's scene targets can hold: Vulkan's far and near depth, or one slice for a backend with a
    /// single scene depth target.</summary>
    static IEnumerable<(float Near, float Far)> SceneSlices(IWorldPostChain post, WorldCamera camera) =>
        post.DepthSlices ? camera.Slices() : [(camera.Near, camera.ViewDistance)];

    /// <summary>Draws a frame: the sky, then the far depth slice (terrain, water), then the near one (terrain, objects, water).</summary>
    // The Tab panel: draw distances and LOD.
    /// <summary>The upscaler sliders' labels (the game keeps command-line upscaler options over the saved ones).</summary>
    // The anti-aliasing slider's label changes with its numbering ("0 off" 2026-10-09, SMAA 2026-10-19), so a value saved under an old one is not misread.
    public static readonly string[] UpscalerSliders = ["Anti-aliasing: 0 off 1 FXAA 2 SMAA 3 TAA 4 FSR 5 DLSS", "Render scale (upscaler)", "Upscaler sharpness"];

    /// <summary>The anti-aliasing slider's value for the options: 0 none, 1 FXAA, 2 SMAA, 3.. the upscaler kinds (TAA, FSR, DLSS).</summary>
    public const string FilteringSlider = "Anisotropic filtering (texture filtering)";
    public const string TextureQualitySlider = "Texture quality 0-4 (game)";
    public static readonly string[] ParticleSliders = ["Weather particles: 0 off 1 quarter 2 half 3 auto 4 full", "Particle density x"];
    // The Tab panel sliders' labels: the meitou.user.json keys, and the launcher's settings schema names them (keep them).
    public const string ObjectDistanceSlider = "Object draw distance", GrassDensitySlider = "Grass density x", WaterReflectionSlider = "Water reflection 0-4 (game)",
        ShadowDistanceSlider = "Shadow distance (game 1k-9k, 0 off)", ShadowMapSlider = "Shadow map size (game quality 0-2)",
        GroundReliefQualitySlider = "Ground relief quality", ShaftQualitySlider = "Light shafts quality";
    public const string
        LandmarkDistanceSlider = "Landmark distance (Meitou)",
        DistantTownsSlider = "Distant towns (zones)",
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
    /// <summary>The Tab panel switches that are not Faithful / Meitou ones (saved as <c>Meitou: &lt;label&gt;</c> like those).</summary>
    public const string
        FifthCascadeSwitch = "Shadow cascade 5 (Meitou)",
        BloomSwitch = "Bloom",
        // The ground relief's Faithful / Meitou switches (Enhancements "parallax", "displace"), by name.
        GroundParallaxSwitch = "Ground parallax",
        GroundDisplaceSwitch = "Ground displacement (tessellation)",
        RockSmoothingSwitch = "Rock smoothing (tessellation)",
        OutlineRefineSwitch = "Terrain outline refinement (tessellation)";
    /// <summary>The weather particles slider's position for the renderer's state: 0 off, 1 quarter, 2 half, 3 by sprite size (auto), 4 full size.</summary>
    public static int WeatherParticlesSlider(bool weatherOn, PostOptions o) =>
        !weatherOn ? 0 : !o.LowResParticles || o.ParticleDivisor == 1 ? 4 : o.ParticleDivisor switch { 4 => 1, 2 => 2, _ => 3 };
    public static int AntiAliasingSlider(PostOptions o) => o.Upscale.Kind != UpscalerKind.Off ? (int)o.Upscale.Kind + 2 : o.Smaa ? 2 : o.Fxaa ? 1 : 0;

    /// <summary>The upscaler a value of the anti-aliasing slider picks (<see cref="UpscalerKind.Off"/> for none, FXAA and SMAA).</summary>
    public static UpscalerKind AntiAliasingKind(float v) => (int)MathF.Round(v) is var i && i >= 3 ? (UpscalerKind)Math.Min(i - 2, (int)UpscalerKind.Dlss) : UpscalerKind.Off;

    /// <summary>
    /// The Faithful / Meitou switches over the live renderer (the game's Shift+F keys, the viewer's F keys, the Tab checkboxes): they change the
    /// options and, once made, the renderer's parts; the distances follow the mode's defaults unless a Tab slider moved them.
    /// </summary>
    internal static IReadOnlyList<Enhancement> LiveSwitches(WorldOptions o, Func<Gpu?> gpu, Func<WorldCamera?> camera, Func<WorldRenderOptions?> render) =>
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
                    if (camera() is { } cam && MathF.Abs(cam.SplitDistance - Math.Max(20000, o.ObjectDistanceFor(was) * 1.1f)) < 1) cam.SplitDistance = Math.Max(20000, o.ObjectDistanceFor(v) * 1.1f);
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

    public static SettingsPanel CreateSettingsPanel(DebugOverlay ui, Gpu g, WorldRenderOptions r, Func<float>? getHour = null, Action<float>? setHour = null,
        Func<bool>? getVSync = null, Action<bool>? setVSync = null, IReadOnlyList<Enhancement>? switches = null,
        Func<float>? getTimeSpeed = null, Action<float>? setTimeSpeed = null, Func<bool>? getFullScreen = null, Action<bool>? setFullScreen = null,
        IReadOnlyList<PanelChoice>? extraChoices = null)
    {
        var sliders = new List<Slider>();
        // The viewer's time of day (the `--time` option and the , / . keys), to the minute. The game passes none: its clock runs on its own.
        if (getHour is not null && setHour is not null)
            sliders.Add(new Slider("Time of day", 0, 24 - 1 / 60f, getHour, v => setHour(MathF.Round(v * 60) / 60), Text: TimeText));
        // How fast the viewer's clock runs (`--time-speed`): game hours per real minute, 0 stopped.
        if (getTimeSpeed is not null && setTimeSpeed is not null)
            sliders.Add(new Slider("Time speed", 0, 240, getTimeSpeed, v => setTimeSpeed(v < 10 ? MathF.Round(v * 10) / 10 : MathF.Round(v)), Text: v => v <= 0 ? "stopped" : $"{v:0.#} game h / min"));
        // The window's vsync (off: MAILBOX, else IMMEDIATE; the frame rate is uncapped). The viewer passes it; the game keeps its own setting.
        if (getVSync is not null && setVSync is not null)
            sliders.Add(new Slider("VSync", 0, 1, () => getVSync() ? 1 : 0, v => setVSync(v >= 0.5f), Text: v => v >= 0.5f ? "on" : "off (uncapped)"));
        // Borderless full screen (the viewer passes it, also Alt+Enter); the game keeps its own window mode.
        if (getFullScreen is not null && setFullScreen is not null)
            sliders.Add(new Slider("Full screen", 0, 1, () => getFullScreen() ? 1 : 0, v => { if (v >= 0.5f != getFullScreen()) setFullScreen(v >= 0.5f); }, Text: v => v >= 0.5f ? "borderless" : "window"));
        if (g.Objects is { } objects)
        {
            sliders.Add(new Slider(ObjectDistanceSlider, 1000, 400000, () => objects.ObjectDistance, v => objects.ObjectDistance = v, "0", Logarithmic: true,
                Faithful: Enhancements.FaithfulObjectDistance, Meitou: Enhancements.MeitouObjectDistance));
            if (objects.HasLandmarks)
                sliders.Add(new Slider(LandmarkDistanceSlider, 1000, 400000, () => objects.LandmarkDistance, v => objects.LandmarkDistance = MathF.Round(v / 100) * 100, "0", Logarithmic: true,
                    Faithful: 0, Meitou: Enhancements.MeitouLandmarkDistance));
            sliders.Add(new Slider(DistantTownsSlider, 0, ObjectRanges.MaxDistantTownRangeZones * 10,
                () => objects.DistantRange / WorldLayout.ZoneSize, v => objects.DistantRange = MathF.Round(v) * WorldLayout.ZoneSize, "0"));
            sliders.Add(new Slider(ObjectLodSlider, 0.25f, 4, () => 1 / objects.LodBias, v => objects.LodBias = 1 / v, "0.00", Logarithmic: true));
        }
        if (g.Foliage is { } foliage)
        {
            sliders.Add(new Slider(FoliageDistanceSlider, 0.25f, 80, () => foliage.RangeSetting, v => foliage.RangeSetting = v, "0.00", Logarithmic: true));
            // The generated levels switch at a pixel tolerance; the distance where each level takes over scales with its inverse.
            sliders.Add(new Slider(FoliageLodSlider, 0.25f, 8, () => FoliageRenderer.DefaultLodTolerance / foliage.LodTolerance,
                v => foliage.LodTolerance = FoliageRenderer.DefaultLodTolerance / v, Logarithmic: true,
                Text: v => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{v:0.00} ({FoliageRenderer.DefaultLodTolerance / v:0.0} px)")));
            // The range switch's class ranges (Meitou; the slider above then only moves the FAR layers' large meshes).
            sliders.Add(new Slider(LargeFoliageRangeSlider, 1000, 120000, () => foliage.LargeRange, v => foliage.LargeRange = MathF.Round(v / 50) * 50, "0", Logarithmic: true));
            sliders.Add(new Slider(ImpostorDistanceSlider, 500, 40000, () => foliage.ImpostorDistance, v => foliage.ImpostorDistance = MathF.Round(v / 50) * 50, "0", Logarithmic: true));
            sliders.Add(new Slider(LargeImpostorDistanceSlider, 500, 40000, () => foliage.LargeImpostorDistance, v => foliage.LargeImpostorDistance = MathF.Round(v / 50) * 50, "0", Logarithmic: true));
            sliders.Add(new Slider(MediumFoliageRangeSlider, 400, 80000, () => foliage.MediumRange, v => foliage.MediumRange = MathF.Round(v / 50) * 50, "0", Logarithmic: true));
            sliders.Add(new Slider(SmallFoliageRangeSlider, 200, 40000, () => foliage.SmallRange, v => foliage.SmallRange = MathF.Round(v / 50) * 50, "0", Logarithmic: true));
            sliders.Add(new Slider(GrassDistanceSlider, 0.25f, 80, () => foliage.GrassRangeSetting, v => foliage.GrassRangeSetting = v, "0.00", Logarithmic: true));
            sliders.Add(new Slider(GrassDensitySlider, 0.1f, 2, () => foliage.GrassDensitySetting, v => foliage.GrassDensitySetting = v, "0.00"));
        }
        // The game's `water reflection` (0 off .. 4 everything) and `reflection range` (x the haze distance), docs/formats/settings.md.
        if (g.Reflection is { } reflection)
        {
            sliders.Add(new Slider(WaterReflectionSlider, 0, 4, () => reflection.Level, v => reflection.Level = (int)MathF.Round(v), "0",
                Text: v => (int)MathF.Round(v) switch { 0 => "0 off", 1 => "1 landscape", 2 => "2 characters", 3 => "3 buildings", _ => "4 everything" }));   // the label is the user config key: keep it
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
        sliders.Add(new Slider(TerrainErrorSlider, 4, 32, () => r.TerrainPixelError, v => r.TerrainPixelError = v, "0.0", Logarithmic: true,
            Faithful: WorldRenderOptions.DefaultTerrainPixelError, Meitou: Enhancements.MeitouTerrainPixelError));
        // Ours (docs/render-terrain.md "Ground relief"): the march's quality, the depth (parallax and displacement; 0 off) and where each ends.
        sliders.Add(new Slider(GroundReliefQualitySlider, 0, 2, () => GroundRelief.Quality,
            v => GroundRelief.Quality = Math.Clamp((int)MathF.Round(v), 0, 2), "0", Text: v => GroundRelief.QualityName((int)MathF.Round(v))));
        sliders.Add(new Slider(GroundReliefDepthSlider, 0, 80, () => GroundRelief.Depth * 10, v => GroundRelief.Depth = MathF.Round(v * 2) / 20, "0.0"));
        if (g.Sky.Gpu.HasTessellation)
            sliders.Add(new Slider(GroundDisplaceDistanceSlider, 10, 200, () => GroundRelief.DisplaceDistance / 10, v => GroundRelief.DisplaceDistance = MathF.Round(v) * 10, "0"));
        sliders.Add(new Slider(GroundParallaxDistanceSlider, 10, 150, () => GroundRelief.ParallaxDistance / 10, v => GroundRelief.ParallaxDistance = MathF.Round(v) * 10, "0"));
        // The game's `Shadow Range` slider goes 1000 to 9000; the viewer allows more (the cascades stretch over it), and its left end (0) turns the
        // shadows off (low-end GPUs).
        Toggle? fifthCascade = null;
        if (g.Shadow is { } shadow)
        {
            const float off = KenshiShadows.MinRange * 0.9f;
            sliders.Add(new Slider(ShadowDistanceSlider, off, 200000, () => shadow.Enabled ? shadow.Settings.Range : off, v =>
            {
                shadow.Enabled = v >= KenshiShadows.MinRange;
                if (shadow.Enabled) shadow.Settings = shadow.Settings with { Range = MathF.Round(v / 100) * 100 };
            }, "0", Logarithmic: true, Text: v => shadow.Enabled ? v.ToString("0", CultureInfo.InvariantCulture) : "0 (off)", Faithful: 5000, Meitou: Enhancements.MeitouShadowRange));
            // The game's `shadow quality` (--shadow-quality): the atlas is made again at the new side on the next frame.
            sliders.Add(new Slider(ShadowMapSlider, 0, 2, () => Array.IndexOf(KenshiShadows.QualitySizes, shadow.Settings.MapSize),
                v => shadow.Settings = shadow.Settings with { MapSize = KenshiShadows.MapSize((int)MathF.Round(v)) }, "0",
                Text: v => KenshiShadows.MapSize((int)MathF.Round(v)).ToString(CultureInfo.InvariantCulture) + "²"));
            if (shadow.Meitou) sliders.Add(new Slider(ShadowFilterSlider, 0, 2, () => shadow.FilterQuality, v => shadow.FilterQuality = (int)MathF.Round(v), "0"));
            if (shadow.Meitou) fifthCascade = new Toggle(FifthCascadeSwitch, () => shadow.MeitouCascades >= 5, v => shadow.MeitouCascades = v ? 5 : 4);
        }
        // A viewer option, not the game's: 1 is the game's haze (docs/formats/sky.md).
        sliders.Add(new Slider(HazeStrengthSlider, 0, 3, () => g.Sky.HazeStrength, v => g.Sky.HazeStrength = v, "0.00", Faithful: 1, Meitou: Enhancements.MeitouHazeStrength));
        // Ours (docs/formats/sky.md "Planetshine"): the planet's light at night, a multiple of the physical value.
        sliders.Add(new Slider(PlanetshineStrengthSlider, 0, 200, () => g.Sky.PlanetshineStrength, v => g.Sky.PlanetshineStrength = MathF.Round(v), "0"));
        // Ours: the sky's ambient light (shade, nights, interiors), a multiple of the game's.
        sliders.Add(new Slider(AmbientStrengthSlider, 0, 3, () => g.Sky.AmbientStrength, v => g.Sky.AmbientStrength = MathF.Round(v * 20) / 20, "0.00"));
        // Ours: a further multiple by night (the exposure sits at its night floor there, so it is not adapted away).
        sliders.Add(new Slider(NightAmbientStrengthSlider, 0, 5, () => g.Sky.NightAmbientStrength, v => g.Sky.NightAmbientStrength = MathF.Round(v * 20) / 20, "0.00"));
        if (g.Post is { } post)
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
            sliders.Add(new Slider(UpscalerSliders[1], 0.33f, 1, () => up.EffectiveScale, v => up.Scale = MathF.Round(v * 100) / 100, "0.00",
                Text: v => up.Kind == UpscalerKind.Off && v < 1 ? v.ToString("0.00", CultureInfo.InvariantCulture) + " (no upscaler: plain scaling)" : v.ToString("0.00", CultureInfo.InvariantCulture)));
            sliders.Add(new Slider(UpscalerSliders[2], 0, 1, () => up.Sharpness, v => up.Sharpness = v, "0.00"));
            // The weather's particles (docs/render-post.md "Particles"): off, forced to a quarter or half of the render size, by sprite size (the Meitou default), or all full size (as the game).
            sliders.Add(new Slider(ParticleSliders[0], 0, 4, () => WeatherParticlesSlider(g.WeatherParticles, post.Options), v =>
            {
                int i = (int)MathF.Round(v);
                g.WeatherParticles = i > 0;
                if (i > 0) (post.Options.LowResParticles, post.Options.ParticleDivisor) = i switch { 1 => (true, 4), 2 => (true, 2), 3 => (true, 0), _ => (false, 0) };
            }, "0"));
            sliders.Add(new Slider(ParticleSliders[1], 0.1f, 1, () => g.ParticleDensity, v => g.ParticleDensity = MathF.Round(v * 20) / 20, "0.00"));
            // Ours, not the game's (docs/render-post.md "Tone map and grading"): the tone curve, the saturation / contrast grade, and a scale on the heat haze.
            // The Meitou curve; the tone map switch's Faithful side is the game's clamp.
            sliders.Add(new Slider(ToneMapSlider, 1, 3, () => (int)post.Options.ToneMap, v => post.Options.ToneMap = (ToneMapOperator)(int)MathF.Round(v), "0",
                Text: v => (ToneMapOperator)(int)MathF.Round(v) switch { ToneMapOperator.Shoulder => "shoulder", ToneMapOperator.Aces => "ACES", ToneMapOperator.Hybrid => "hybrid (clamp + ACES)", _ => "clamp (as the game)" }));
            sliders.Add(new Slider(ToneMixSlider, 0, 1, () => post.Options.ToneMix, v => post.Options.ToneMix = MathF.Round(v * 20) / 20, "0.00"));
            sliders.Add(new Slider(GradingSlider, 0, 1, () => post.Options.Grade ? 1 : 0, v => post.Options.Grade = v >= 0.5f,
                Text: v => v >= 0.5f ? $"saturation {PostOptions.Saturation:0.00}, contrast {PostOptions.Contrast:0.00}" : "off (as the game)"));
            sliders.Add(new Slider(HeatHazeStrengthSlider, 0, 3, () => post.Options.HeatHazeStrength, v => post.Options.HeatHazeStrength = MathF.Round(v * 20) / 20, "0.00",
                Faithful: 1, Meitou: Enhancements.MeitouHeatHazeStrength));
            sliders.Add(new Slider(BloomStrengthSlider, 0, 0.25f, () => post.Options.BloomStrength, v => post.Options.BloomStrength = MathF.Round(v * 200) / 200, "0.000", Faithful: 0));
            sliders.Add(new Slider(NightGradeStrengthSlider, 0, 1, () => post.Options.NightGradeStrength, v => post.Options.NightGradeStrength = MathF.Round(v * 20) / 20, "0.00"));
            sliders.Add(new Slider(ShaftStrengthSlider, 0, 1, () => post.Options.ShaftStrength, v => post.Options.ShaftStrength = MathF.Round(v * 20) / 20, "0.00"));
            sliders.Add(new Slider(ShaftAirSlider, 0, 4, () => post.Options.ShaftAir, v => post.Options.ShaftAir = MathF.Round(v * 20) / 20, "0.00"));
            sliders.Add(new Slider(ShaftAirDawnSlider, 0, 6, () => post.Options.ShaftAirDawn, v => post.Options.ShaftAirDawn = MathF.Round(v * 10) / 10, "0.0"));
            sliders.Add(new Slider(ShaftQualitySlider, 0, 2, () => post.Options.ShaftQuality,
                v => post.Options.ShaftQuality = Math.Clamp((int)MathF.Round(v), 0, 2), "0",
                Text: v => (int)MathF.Round(v) switch { 0 => "Low", 1 => "Medium", _ => "High" }));
        }
        // Anisotropic filtering (docs/render-post.md "Texture filtering"): the most any texture is sampled with; the world's textures ask 8x, so 8x and 16x look the same there.
        sliders.Add(new Slider(FilteringSlider, 0, 4, () => MathF.Log2(g.Sky.Gpu.Samplers.MaxAnisotropy), v => g.Sky.Gpu.Samplers.MaxAnisotropy = 1 << (int)MathF.Round(v), "0",
            Text: v => (1 << (int)MathF.Round(v)) + "x" + (MathF.Round(v) >= 3 ? " (as the game)" : "")));
        // The Faithful / Meitou switches as checkboxes (ticked: Meitou), the F-key toggles' state.
        // The displacement switch only where the device has tessellation shaders.
        var toggles = switches?.Where(e => e.Id != "displace" || g.Sky.Gpu.HasTessellation).Select(e => new Toggle(e.Name, () => e.IsMeitou, v => e.IsMeitou = v, () => e.IsMeitou ? e.Meitou : e.Faithful, Remaster: true, OnOff: e.Faithful is "off" or "none")).ToList();
        if (fifthCascade is not null) (toggles ??= []).Add(fifthCascade);
        toggles ??= [];
        // Bloom: an optional effect, off in both looks (docs/render-post.md "Bloom"); a look preset may turn it on, its strength is a slider.
        if (g.Post is { } bloomPost)
            toggles.Add(new Toggle(BloomSwitch, () => bloomPost.Options.Bloom, v => bloomPost.Options.Bloom = v, Remaster: true, OnOff: true, Optional: true));
        if (g.Sky.Gpu.HasTessellation)
        {
            toggles.Add(new Toggle(RockSmoothingSwitch, () => RockSmoothing.TessellateActive, v => RockSmoothing.TessellateActive = v, Remaster: true, OnOff: true));
            toggles.Add(new Toggle(OutlineRefineSwitch, () => GroundRelief.RefinePixels > 0, v => GroundRelief.RefinePixels = v ? GroundRelief.DefaultRefinePixels : 0, Remaster: true, OnOff: true));
        }
        // The billboard (impostor atlas) disk cache: deleted files are baked again when next needed; the resident atlases stay until evicted.
        var actions = new List<PanelAction>();
        if (g.Foliage?.ImpostorDiskCache is { } cache)
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
        // The third column: textures. The game's `texture resolution gimping` (docs/formats/settings.md): the object, foliage and character textures
        // are loaded again at the new level as the slider moves; the terrain's layer arrays keep the size they were made with (--texture-quality at start).
        int terrainLevel = Meitou.Data.Textures.TextureQuality.Level;
        var side = new List<Slider>
        {
            new(TextureQualitySlider, 0, Meitou.Data.Textures.TextureQuality.Maximum, () => Meitou.Data.Textures.TextureQuality.Level,
                v => Meitou.Data.Textures.TextureQuality.Level = (int)MathF.Round(v), "0",
                Text: v => (int)MathF.Round(v) is var level && level != terrainLevel
                    ? $"{Meitou.Data.Textures.TextureQuality.Labels[level]} (terrain at start)" : Meitou.Data.Textures.TextureQuality.Labels[level]),
        };
        // The camera region's weathers of its season (the current one marked): a click starts one now.
        var choices = new List<PanelChoice>(extraChoices ?? []);
        if (g.Weather is { } weather) choices.Add(new PanelChoice(weather.ChoiceLabel, weather.Choices, weather.Current, weather.Choose));
        return new SettingsPanel(ui, "Settings   (Tab hides this)", sliders, toggles, "Meitou improvements (unticked: as the game)", actions, side, choices);
    }

    /// <summary>An hour as <c>HH:MM</c>.</summary>
    public static string TimeText(float hour)
    {
        int minutes = (int)MathF.Round(hour * 60) % (24 * 60);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    /// <summary>The unit of <c>Draw</c>'s <c>time</c> argument: seconds divided by 600 (the viewer's real seconds, the game's game seconds).</summary>
    const double SecondsPerTimeUnit = 600;

    /// <summary>Per-cascade and per-step CPU times in the stats strings (ShadowPass.CasterStats, FoliageRenderer's details); the viewer's screenshots and benchmarks turn it on.</summary>
    public static bool DetailedStats;
    static readonly string[] CascadeLabels = [.. Enumerable.Range(0, ShadowSettings.MaxCascades).Select(i => $"shadow c{i}")];

    /// <summary>Sky-only loading frames, with the existing sky and post chain and without touching world streaming or character content.</summary>
    public static void DrawLoadingSky(Gpu gpu, WorldScene scene, WorldCamera camera, int width, int height, float hour, float seconds, float fogDistance)
    {
        gpu.Terrain.Activate();
        var post = gpu.PostChain ?? throw new InvalidOperationException("the sky frame needs the post-processing chain");
        post.GiResolve = 0;
        post.Begin(width, height);
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
        post.BeginNearSlice(1, 1000);
        post.SetNearSlice(1, 1000, camera.FieldOfView, aspect);
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

    public static void Draw(Gpu gpu, WorldScene scene, WorldCamera camera, WorldRenderOptions render, int width, int height, float hour, float time, float fogDistance, float? weatherHour = null)
    {
        gpu.Terrain.Activate();
        var post = gpu.PostChain ?? throw new InvalidOperationException("the world frame needs a post-processing chain");
        if (gpu.Surfaces is { } surfaces) surfaces.GameTime = time;
        // Everything is drawn into the post-processing chain's HDR framebuffer (before the reflection pass, which restores whatever is bound).
        // The GI resolve while the probes light the world: the scene targets carry the albedo target, the lit fragments write it.
        bool giResolve = gpu.GiResolve > 0 && gpu.GiActive && gpu.Probes is { Enabled: true, Shade: true };
        if (gpu.Post is { } giPost) giPost.GiResolve = giResolve ? gpu.GiResolve : 0;
        if (gpu.Probes is { } giProbes) giProbes.Resolve = giResolve;
        post.Begin(width, height);
        // The scene is drawn at the render size (smaller than the display with an upscaler), its projection jittered by the upscaler.
        int rw = post.RenderWidth, rh = post.RenderHeight;
        // The terrain LOD measures its error in the pixels actually rendered (after the upscaler's render scale).
        render.TerrainPixelScale = TerrainLod.ProjectionScale(rh, camera.FieldOfView);
        var jitter = post.JitterPixels;
        var eye = camera.Eye;
        gpu.Guard?.Tick();
        var kept = ObjectStreamer.MergeKept(gpu.Kept, eye);
        if (gpu.Streamer is { } aheadTerrain) (aheadTerrain.Ahead, aheadTerrain.Kept) = (gpu.Ahead, kept);
        if (gpu.Objects is { } aheadObjects) (aheadObjects.Ahead, aheadObjects.Kept) = (gpu.Ahead, kept);
        if (gpu.Foliage is { } aheadFoliage) aheadFoliage.Ahead = gpu.Ahead;
        gpu.Streamer?.Update(eye);
        StageClock.Lap(0);
        if (gpu.Objects is { } lodObjects) lodObjects.LodPixelsPerRadian = render.TerrainPixelScale;   // the generated object levels are placed by the pixel size too
        gpu.Objects?.SetView(rw, rh, camera.FieldOfView);   // the object textures' mip streaming measures pixels at the render size
        gpu.Characters?.SetView(rw, rh, camera.FieldOfView);
        gpu.Objects?.Update(eye);
        StageClock.Lap(1);
        if (gpu.Foliage is { } lodFoliage) lodFoliage.LodPixelsPerRadian = render.TerrainPixelScale;   // the generated mesh levels measure their deviation in rendered pixels
        gpu.Foliage?.Update(eye);
        StageClock.Lap(2);
        gpu.Characters?.Update(eye);
        if (gpu.Characters is { } posed && gpu.Debris is { } boltMeshes)
        {
            posed.CollectBolts(gpu.BoltPieces);
            foreach (var bolt in gpu.BoltPieces) boltMeshes.Extent(bolt.Mesh);   // loads a mesh the first time
        }
        StageClock.Part(StageClock.PartBolts);
        gpu.Characters?.DrawPortraits();   // the squad portraits, from this update's poses
        StageClock.Part(StageClock.PartPortraits);
        gpu.Characters?.DrawHoverMasks(gpu.HoverOutline is null ? [] : gpu.HoverKeys, width, height, camera.View, camera.FieldOfView, eye);   // before the colour pass, whose statistics stand
        StageClock.Part(StageClock.PartHover);
        gpu.Characters?.DrawIcons();   // the item icons being made, from the same update
        gpu.Objects?.DrawBuildingIcons();   // the build window's pictures being made
        StageClock.Part(StageClock.PartIcons);
        StageClock.Lap(StageClock.UpdChars);
        float floor = gpu.Terrain.HeightAt(eye.X, eye.Z);
        if (render.Water) floor = Math.Max(floor, WorldWater.Height);
        camera.EyeClearance = Math.Max(eye.Y - floor, 1);
        // Above the game's camera heights the haze (and the water's glint) move to the viewer's own altitude-aware forms (SkyRenderer.AltitudeWeight).
        gpu.Sky.SetEye(eye, gpu.Terrain.HeightAt, render.Water ? WorldWater.Height : float.NegativeInfinity);
        // The atmosphere (SkyRenderer): sky tables, sun and ambient light for this sun and eye height. Thinner air higher up: the
        // haze takes longer to close in the higher the eye.
        var sun = scene.Clock.SunDirection(hour);
        // The weather at the camera (before the sky's tables are prepared: the colour multiplier feeds them). A held frame (a screenshot) has dt 0, the first frame snaps.
        bool held = post.InstantAdaptation;
        if (gpu.Weather is { } weather)
        {
            float scheduleHour = weatherHour ?? hour;
            weather.Update(eye, scheduleHour, scene.Clock.SunDirection(scheduleHour).Y, held);
            weather.Apply(gpu.Sky);
        }
        gpu.Surfaces?.Apply(gpu.WeatherState);
        // The night sky's clocks: the starfield shifts with the game hours since the load, the planets turn with the day and hour.
        gpu.Sky.StarHours = gpu.GameHours ?? gpu.HeatHazeHours;
        gpu.Sky.Day = gpu.Weather?.Day ?? 0;
        gpu.Sky.Hour = hour;
        gpu.Sky.Clock = scene.Clock;
        gpu.Sky.StarSeconds = (float)(gpu.HeatHazeHours / HeatHaze.HoursPerSecond % 3600);   // the twinkle's clock: still for a picture
        gpu.Sky.Eye = new Vector3((float)eye.X, (float)eye.Y, (float)eye.Z);
        var (colours, light) = gpu.Sky.Prepare(sun, eye.Y, fogDistance + 3 * Math.Max(eye.Y, 0));
        // The game's exposure (0.55 over the mean luminance, clamped to its band) goes with the game's sky and light.
        post.AutoExposure = (gpu.Sky.MinLuminance, gpu.Sky.MaxLuminance);
        post.WaterHeight = render.Water && gpu.Water is not null ? WorldWater.Height : null;
        UpdateHeatHaze(gpu, post, sun.Y);
        // The Meitou water's waves on the game clock (the heat haze's, held still for a still picture), led by the wind at the camera.
        gpu.Water?.Animate(gpu.GameHours ?? gpu.HeatHazeHours,gpu.WeatherState.WindDirection, gpu.WeatherState.WindSpeed);
        gpu.Water?.Track(eye, gpu.Terrain.Snapshot());
        // The clouds drift on the frame clock (game-speed seconds, 0 while paused), held still for a still picture.
        if (gpu.Weather is { } drift) gpu.Sky.StepClouds(drift.Times.Game); else gpu.Sky.StepClouds(held);
        gpu.Sky.PrepareCloudShadows();
        // Far enough that the haze is complete before the far plane and the water quad (1.5 × view distance wide) end,
        // so a high eye sees the sea fade into the sky instead of a cut-off edge.
        camera.ViewDistance = Math.Min(Math.Max(camera.MinViewDistance, light.FogDistance / 0.7f), gpu.ViewCap ?? float.MaxValue);
        StageClock.Lap(3);
        // The weather's camera particles step on the caller's time (1/600 s units; the game's clock, so still while paused; constant, so still, in pictures).
        // They count as their own stage (the profiler's "particles", with their draw below).
        UpdateParticles(gpu, camera);
        if (gpu.BuildingEffects is { IsCompleted: true } effectsLoad)
        {
            gpu.BuildingEffects = null;
            if (!effectsLoad.IsCompletedSuccessfully) Console.WriteLine($"building effects not read: {effectsLoad.Exception?.InnerException?.Message}");
            else if (gpu.Particles is { } buildingParticles)
            {
                foreach (var byBuilding in effectsLoad.Result.GroupBy(e => e.PlacementId)) buildingParticles.SetBuildingEffects(byBuilding.Key, byBuilding.ToList());
                var (buildings, groups) = buildingParticles.BuildingEffectCount;
                Console.WriteLine($"particles {effectsLoad.Result.Count} building effects on {buildings} buildings in {groups} groups");
            }
        }
        if (gpu.Particles is { } updated)
        {
            (updated.WeatherParticles, updated.World.Density) = (gpu.WeatherParticles, gpu.ParticleDensity);
            updated.Update(time * SecondsPerTimeUnit, camera);
        }
        // The placed fog volumes in view and the effects' fog spheres (after the particles moved), for every world shader (FogVolumes).
        if (gpu.FogVolumes is { } fogVolumes)
        {
            gpu.Particles?.CollectFogVolumes(gpu.EffectFogs);
            fogVolumes.Update(eye, camera.Forward, camera.FieldOfView, rw / (float)Math.Max(rh, 1), gpu.Sky.HazeDistance, sun.Y, true, gpu.EffectFogs, gpu.Sky.FogCullDistance);
            gpu.Sky.CloudShadowScale = fogVolumes.CloudShadowScale;
        }
        StageClock.Lap(13);
        if (gpu.Shadow is not null) gpu.Shadow.RangeCap = gpu.ViewCap is { } cap ? Math.Min(cap, gpu.FogVolumes?.AtmosphereDistance ?? cap) : gpu.FogVolumes?.AtmosphereDistance;   // the cascades end where the weather fog hides everything (or at the cap)
        gpu.Shadow?.SetFogHide(gpu.FogVolumes?.ShadowHideDistance, gpu.FogVolumes?.ShadowHidePending == true);   // or where the placed fog block the eye is in hides the whole view
        if (gpu.Shadow is not null) { gpu.Shadow.Temporal = post.Temporal; DrawShadows(gpu, camera, render, light, rw, rh, gpu.Sky.ShadowSunHeight); }

        StageClock.Lap(12);
        if (gpu.Lamps is { } lamps)
        {
            lamps.Moving.Clear();
            gpu.Characters?.CollectLamps(lamps.Moving);   // the lanterns characters carry, where this update posed them
        }
        gpu.Lamps?.Update(eye, gpu.Objects, WorldLights.DaylightFactor(hour, scene.Clock.Sunrise, scene.Clock.Sunset));
        // The traced scene of the global illumination: its acceleration structures for this eye (docs/render-gi.md).
        if (gpu.Probes is { } probesActive) probesActive.Active = gpu.GiActive;
        if (gpu.Gi is { } gi && gpu.GiActive)
        {
            var heights = gpu.Terrain.Snapshot();
            gi.Update(eye, heights, render.Objects ? gpu.Objects : null, gpu.Foliage is { Enabled: true } giFoliage ? giFoliage : null, gpu.Lamps);
            StageClock.Sub("gi scene");
            gpu.Probes?.Update(gi, eye, heights, gpu.Lamps, gpu.FogVolumes, gpu.Objects?.GiInteriorFocus(camera.Target));
            StageClock.Sub("gi blend");
        }
        if (gpu.ProbeGi is { } probeGi)
        {
            probeGi.Active = gpu.GiActive;
            if (gpu.GiActive)
            {
                probeGi.Update(eye, gpu.Terrain.Snapshot(), render.Objects ? gpu.Objects : null, gpu.Lamps, gpu.FogVolumes);
                StageClock.Sub("gi metal");
            }
        }
        StageClock.Lap(14);
        // The blood on the ground near the eye (docs/game/blood-decals.md), before any terrain draw reads it.
        if (gpu.Decals is { } decals && gpu.DecalStore is { } decalStore) decals.Record(decalStore, eye);
        gpu.Terrain.Decals = gpu.DecalStore is null ? null : gpu.Decals;
        StageClock.Lap(StageClock.Blood, "blood decals");
        // Water reflection: the mirrored scene into its own framebuffer (restores the bound one), before the main pass.
        bool reflecting = render.Water && render.Reflections && gpu.Water is not null && gpu.Reflection is { Level: > 0 };   // level 0: no pass, the water shows the sky colour
        if (gpu.Reflection is not null) { gpu.Reflection.FaithfulShadows = gpu.Shadow is { Meitou: false }; gpu.Reflection.MaxDistance = gpu.Sky.HazeDistance * gpu.Reflection.Range; gpu.Reflection.HideDistance = gpu.FogVolumes?.AtmosphereDistance; gpu.Reflection.WaterDistance = gpu.Sky.HazeCompleteDistance; gpu.Reflection.FogVolumes = gpu.Water is { Meitou: true } && gpu.FogVolumes is { UsedData: > 0 }; }
        // The mirrored scene keeps the flat ambient: the probes' light is not worth a second view's reads in a blurred reflection (docs/render-gi.md).
        if (gpu.Probes is { } reflectedProbes) reflectedProbes.InReflection = reflecting && !reflectedProbes.Reflected;
        if (reflecting)
            gpu.Reflection!.Render(camera, rw, rh, gpu.Sky, colours, light, gpu.Terrain, render,
                gpu.Objects is null && gpu.Characters is null && gpu.Foliage is null ? null : (vp, e, frustum) =>
            {
                var reflection = gpu.Reflection;
                // The level (docs/formats/settings.md): 2 characters, 3 buildings (and the map features, which are not told
                // apart), 4 everything else (foliage). Characters still reflect when the debug object switch is off.
                if (reflection.Level >= 3 && render.Objects && gpu.Objects is { } objects)
                {
                    float distance = objects.ObjectDistance, landmarks = objects.LandmarkDistance;
                    float bias = objects.LodBias;
                    objects.LodBias = bias * reflection.ObjectLodBias;
                    objects.ObjectDistance = Math.Min(distance, gpu.Reflection.ObjectReach);
                    objects.LandmarkDistance = Math.Min(landmarks, gpu.Reflection.ObjectReach);   // the mirrored scene has no landmarks beyond the reflection's own object range
                    objects.MirrorCull = reflection.CullToWater ? reflection.MayShow : null;
                    objects.AllFloors = true;   // the game shows every floor in the water reflection (docs/formats/zones.md "Interior view")
                    objects.Draw(vp, e, frustum, render, light.SunDirection, light.FogColour, light.FogDistance, gpu.Terrain);
                    objects.AllFloors = false;
                    objects.MirrorCull = null;
                    reflection.Lap(2);
                    StageClock.Sub("refl up to objects");
                    objects.ObjectDistance = distance;
                    objects.LandmarkDistance = landmarks;
                    objects.LodBias = bias;
                }
                if (reflection.Level >= 4 && render.Objects) gpu.Foliage?.Draw(vp, e, frustum, render, light.SunDirection, light.FogColour, light.FogDistance, gpu.Terrain, grass: false, maxRange: reflection.FoliageReach);
                reflection.Lap(3);
                StageClock.Sub("refl foliage");
                // Level 2 is the game's characters layer. They are drawn after the opaque objects and foliage as in the
                // main view, and without the main view's fog cull (the reflection's own range/HideDistance handles that).
                if (reflection.Level >= 2 && gpu.Characters is { } reflectedCharacters)
                {
                    reflectedCharacters.FogCull = null;
                    reflectedCharacters.InteriorObjects = null;
                    reflectedCharacters.Draw(vp, e, frustum, light.SunDirection, light.FogColour, light.FogDistance);
                    reflection.Lap(2);
                    StageClock.Sub("refl characters");
                }
                var reflectedObjects = reflection.Level >= 3 && render.Objects ? $"{gpu.Objects?.DrawnInstances ?? 0} objects ({gpu.Objects?.DrawnTriangles ?? 0:N0} triangles, {gpu.Objects?.DrawCalls ?? 0} calls)" : "no objects (level < 3)";
                var reflectedFoliage = reflection.Level >= 4 && render.Objects ? $"{gpu.Foliage?.DrawnInstances ?? 0} foliage meshes ({gpu.Foliage?.DrawCalls ?? 0} calls)" : "";
                var reflectedPeople = reflection.Level >= 2 ? $"{gpu.Characters?.DrawnCharacters ?? 0} characters" : "no characters (level < 2)";
                reflection.SceneStats = $"{reflectedPeople}, {reflectedObjects}{(reflectedFoliage.Length > 0 ? $", {reflectedFoliage}" : "")}";
            });
        if (gpu.Probes is not null) gpu.Probes.InReflection = false;
        // What the reflection's rect learnt for the water pass: the nearest water in view, or NaN when no pass ran to find out.
        if (gpu.Water is { } gated) gated.NearestWater = reflecting ? gpu.Reflection!.NearestWater : float.NaN;
        gpu.Water?.PrepareFrame();   // the FFT before the scene's render pass (Metal records compute in the same command buffer)
        StageClock.Lap(4);
        // The scene's passes are a native host that hands its guests the targets and state (phase 8 stage 3); wave 4 (docs/renderer-native.md 6):
        // its rendering takes secondaries, the guests' segments recorded on the job threads when it ends.
        // The scene starts cleared to the fog colour and depth 1 (the rendering's load ops).
        var host = gpu.Scene ??= new SceneHost(post.Context);
        float aspect = width / (float)Math.Max(height, 1);
        var view = camera.View;
        // Rotation only: with the eye's world position in the matrix, the directions rebuilt from it lose float
        // precision far from the origin and the sky blurs.
        var rotation = view with { M41 = 0, M42 = 0, M43 = 0 };
        var skyProjection = rotation * Jitter.Apply(camera.Projection(aspect, 1, 1000), jitter, rw, rh);
        // The fog volume pass runs over this view's sky exactly when volumes are in view (RunFogVolumes below), so the march may leave out what they hide.
        // With the placed fog block hiding the sky in every direction of the view, neither the sky nor its clouds can show: the clear colour stands in.
        bool skyHidden = gpu.Sky.SkyFogSkip && gpu.FogVolumes is { SkyHidden: true };
        if (skyHidden) gpu.Sky.SkipMainSky();
        else gpu.Sky.PrepareClouds(skyProjection, rw, rh, gpu.Sky.Eye, fogVolumes: gpu.FogVolumes is { UsedData: > 0 });
        // The interior masks with the near slice's camera, before the scene's passes (the terrain of that slice reads them).
        if (render.Objects && gpu.Objects is { } maskObjects && SceneSlices(post, camera).FirstOrDefault(s => s.Near <= camera.Near) is { Far: > 0 } maskSlice)
        {
            gpu.InteriorMask ??= new InteriorMaskRenderer(post.Context, maskObjects);
            gpu.InteriorMask.Record(view * Jitter.Apply(camera.Projection(aspect, maskSlice.Near, maskSlice.Far), jitter, rw, rh), eye, post.RenderWidth, post.RenderHeight);
            maskObjects.Masks = gpu.InteriorMask;
        }
        else if (gpu.InteriorMask is { } unused) unused.Record(default, eye, 0, 0);
        host.Open(5, post.WithShadingRate(post.SceneTargets), new ClearColour(light.FogColour.X, light.FogColour.Y, light.FogColour.Z, 0), clearDepth: true);   // alpha 0: no character (the SSAO mask)
        if (!skyHidden) gpu.Sky.Draw(skyProjection, colours);
        StageClock.Lap(5);
        post.SetCamera(eye, view, camera.FieldOfView, aspect);
        gpu.Terrain.BeginFrame();
        bool first = true, foliageDrawn = false;
        Matrix4x4 particleViewProjection = default, particleNearProjection = default, particleLowProjection = default, particleLowNearProjection = default;
        float particleNear = 0;
        foreach (var (near, far) in SceneSlices(post, camera))
        {
            bool nearSlice = near <= camera.Near;
            // The slices draw into different framebuffers (the far slice has a depth of its own, which the fog volumes pass reads): one host per slice.
            host.Close();
            if (!first || nearSlice) post.BeginNearSlice(near, far); else post.BeginFarSlice(near, far);
            // A later slice starts on a cleared depth: in its place among the segments while the host stays open, else by the load op.
            if (!first && host.IsOpen) host.ClearDepth();
            host.Open(6, post.WithShadingRate(post.SceneTargets), clearDepth: !first && !host.IsOpen);
            first = false;
            if (nearSlice) post.SetNearSlice(near, far, camera.FieldOfView, aspect);
            var viewProjection = view * Jitter.Apply(camera.Projection(aspect, near, far), jitter, rw, rh);
            var frustum = WorldCamera.FrustumPlanes(viewProjection);
            // The characters' view-projection for positions relative to the eye: the rotation only, exact (the matrix above, with the eye's world position in its translation,
            // has lost the digits far from the origin: CharacterRenderer.DrawView).
            var relativeProjection = rotation * Jitter.Apply(camera.Projection(aspect, near, far), jitter, rw, rh);
            var relativeUnjittered = rotation * camera.Projection(aspect, near, far);
            if (nearSlice && post.Temporal && gpu.Foliage is { } swaying)
            {
                post.ObjectMotion ??= swaying.DrawGrassMotion;
                swaying.SetMotionCamera(viewProjection, view * camera.Projection(aspect, near, far), eye, frustum);
            }
            if (nearSlice) gpu.Characters?.AttachMotion(post, gpu.Foliage, viewProjection, view * camera.Projection(aspect, near, far), eye, frustum, relativeProjection, relativeUnjittered);
            host.Stage(6);
            var fogCull = gpu.FogVolumes;
            gpu.Terrain.FogCull = fogCull;
            gpu.Terrain.InteriorCut = nearSlice ? gpu.InteriorMask : null;
            gpu.Terrain.Draw(viewProjection, eye, frustum, render, light, defer: TerrainLateColour.Enabled);   // the colour comes after the foliage (DrawDeferred) when on
            gpu.Terrain.InteriorCut = null;
            gpu.Terrain.FogCull = null;
            StageClock.Lap(6);
            // The blood on walls, buildings and objects (the bloodsurfaces switch) needs the depth the terrain alone wrote, to leave the terrain's pixels to its own blood.
            bool surfaceBlood = nearSlice && gpu.Decals is { Surfaces: true, SurfaceCount: > 0 } bloodDecals && gpu.DecalStore is not null && bloodDecals.SurfacesNear(eye);
            if (surfaceBlood)
            {
                host.Close();
                gpu.Decals!.CaptureTerrainDepth(post.SceneDepth);
                host.Open(7, post.WithShadingRate(post.SceneTargets));
            }
            host.Stage(7);
            if (gpu.Objects is { } fogObjects) { fogObjects.FogCull = fogCull; fogObjects.InteriorCut = nearSlice ? gpu.InteriorMask : null; }
            if (render.Objects) gpu.Objects?.Draw(viewProjection, eye, frustum, render, light.SunDirection, light.FogColour, light.FogDistance, gpu.Terrain);
            if (gpu.Objects is { } fogObjectsDone) { fogObjectsDone.FogCull = null; fogObjectsDone.InteriorCut = null; }
            StageClock.Lap(7);
            if (surfaceBlood)
            {
                host.Close();
                gpu.Decals!.CaptureStaticDepth(post.SceneDepth);
                host.Open(8, post.WithShadingRate(post.SceneTargets));
            }
            // Foliage in every depth slice (it reaches 32000+ units at the default x4), counted as one draw.
            host.Stage(8);
            if (gpu.Foliage is { } fogFoliage) { fogFoliage.FogCull = fogCull; fogFoliage.Occlusion = post.OcclusionFor(eye); fogFoliage.InteriorCut = nearSlice ? gpu.InteriorMask : null; }
            if (gpu.Foliage is { } prepassFoliage) prepassFoliage.PrepassView = true;
            if (nearSlice || FoliageFarSlice.Enabled) gpu.Foliage?.Draw(viewProjection, eye, frustum, render, light.SunDirection, light.FogColour, light.FogDistance, gpu.Terrain, continuation: foliageDrawn);
            if (gpu.Foliage is { } prepassDone) prepassDone.PrepassView = false;
            if (gpu.Foliage is { } fogFoliageDone) { fogFoliageDone.FogCull = null; fogFoliageDone.Occlusion = default; fogFoliageDone.InteriorCut = null; }
            foliageDrawn = true;
            StageClock.Lap(8);
            host.Stage(6);
            gpu.Terrain.DrawDeferred();   // the terrain colour where nothing nearer covers it (the depth is in from the draw above)
            if (nearSlice && gpu.Debris is { } debris)
            {
                debris.Draw(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(gpu.DebrisPieces), viewProjection, eye, frustum, light, look => gpu.Characters is { } chars && chars.SkinOn(look.Race, look.SkinDefault) ? chars.SkinTable : 0);
                if (gpu.BoltPieces.Count > 0) debris.Draw(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(gpu.BoltPieces), viewProjection, eye, frustum, light);
            }
            StageClock.Lap(6);
            // The blood on walls, buildings and objects (the bloodsurfaces switch): over the finished opaque scene, under the characters and the water.
            if (surfaceBlood)
            {
                host.Close();
                float tanY = MathF.Tan(camera.FieldOfView * 0.5f);
                gpu.Decals!.RecordSurfaces(post.SceneColour, post.SceneDepth, near, far, rotation, tanY * aspect, tanY, eye, new Vector2(2 * jitter.X / rw, 2 * jitter.Y / rh));
                host.Open(6, post.WithShadingRate(post.SceneTargets));
                StageClock.Lap(StageClock.Blood, "blood surfaces");
            }
            // Characters after the opaque geometry: they alone write the scene's alpha (SSAO's character mask), so nothing drawn later may cover them but water.
            if (gpu.Characters is { } fogCharacters) { fogCharacters.FogCull = fogCull; fogCharacters.InteriorObjects = gpu.Objects; }
            if (nearSlice) post.CharactersDrawn = false;   // none this frame unless the near slice draws some below
            if (nearSlice && gpu.Characters is { } characters)
            {
                host.Stage(StageClock.Characters);
                characters.Draw(viewProjection, eye, frustum, light.SunDirection, light.FogColour, light.FogDistance, relativeProjection);
                post.CharactersDrawn = characters.DrawnCharacters > 0;
                StageClock.Lap(StageClock.Characters);   // the colour draw; the motion draw (post chain, upscalers only) laps itself in
            }
            // Transparent water refracts the slice's scene and measures its depth through it. Fully opaque distant slices need no copy or pass restart.
            if (render.Water && gpu.Water is { } capturing && capturing.NeedsCapture(near, nearSlice))
            {
                host.Close();
                capturing.CaptureScene(post.SceneColour, nearSlice ? post.SceneDepth : null, near, far);   // the far slice's depth is a texture of its own
                host.Open(9, post.WithShadingRate(post.SceneTargets));
            }
            host.Stage(9);
            if (render.Water && gpu.Water is not null) gpu.Water.Overcast = Overcast(gpu);
            if (render.Water) gpu.Water?.Draw(viewProjection, eye, light, colours, time, camera.ViewDistance * 1.5f, reflecting ? gpu.Reflection : null);
            StageClock.Lap(9);
            if (nearSlice)   // the weather particles come last, after the fog volumes (below): the game draws them in queue 84, the volumes in 82
            {
                particleViewProjection = viewProjection;
                particleNearProjection = view * Jitter.Apply(camera.Projection(aspect, Math.Min(0.5f, near * 0.5f), near), jitter, rw, rh);
                // Unjittered, for low-resolution particles blended after the upscaler (its output is unjittered).
                particleLowProjection = view * camera.Projection(aspect, near, far);
                particleLowNearProjection = view * camera.Projection(aspect, Math.Min(0.5f, near * 0.5f), near);
                particleNear = near;
            }
        }
        // Records the last slice's jobs and executes them: the render thread's share (the fork-join) counts as "water", the last stage of the host.
        if (host.IsOpen) { host.Close(); StageClock.Lap(9); }
        post.RunGiResolve();
        StageClock.Lap(14, "gi resolve");   // the resolve is the gi stage's (its time was the particles stage's before, with the shafts and the fog volumes)
        // The Meitou light shafts (docs/render-shafts.md): the haze darkened where the sun is shadowed along the view, before the fog volumes blend over it.
        if (gpu.Shadow is { Enabled: true }) { post.RunLightShafts(ShaftDarkening(post.Options, light, sun.Y) * ShaftCloudFade(Overcast(gpu)), Math.Min(camera.ViewDistance, gpu.Sky.HazeCompleteDistance ?? gpu.Sky.HazeDistance), floor, ShaftAirByTime(post.Options, scene.Clock, hour)); StageClock.Lap(StageClock.Shafts); }
        if (gpu.DebugShadows >= 2 && gpu.Shadow is not null) gpu.Shadow.CaptureDepth(post.SceneDepth, rw, rh);
        // The placed fog volumes over the finished scene (opaque, water, sky; the haze is in the shaders), one pass reading the depth, as the game's queue 82 does.
        post.RunFogVolumes(gpu.FogVolumes is { UsedData: > 0 });
        post.BuildShadingRate(eye);   // the next frame's fog shading rate (fog-vrs), from this frame's depth
        StageClock.Lap(StageClock.Fog, "fog volumes");
        // The SSAO onto the scene before the particles: they are not occluded by the surfaces behind them (the post chain leaves it out of its composite).
        post.ApplySsao();
        // Then the particles, blended over it and tested against the near slice's depth (they are not fogged; docs/formats/fogfeatures.md).
        if (gpu.Particles is { } particleDraw && particleNear > 0)
        {
            particleDraw.Prepare(particleViewProjection, particleNearProjection, particleNear, view, eye, sun, post.ParticlePixelsPerUnit(camera.Projection(aspect, 1, 10).M22), (float)post.RenderWidth * post.RenderHeight, post.Options.ParticleDivisor,
                lowViewProjection: post.ParticlesAfterUpscale ? particleLowProjection : null, lowCloseViewProjection: post.ParticlesAfterUpscale ? particleLowNearProjection : null);
            if (particleDraw.FullCount > 0)
            {
                post.PrepareSoftParticleDepth();
                host.Open(13, post.SceneTargetsForParticles);
                particleDraw.Record(1, post.SoftParticleDepth(1));
                host.Close();
            }
            // Meitou: the alpha and additive sprites that are big on screen into low-resolution targets (half, then quarter size), each over the scene with a depth-aware upsample.
            if (post.LowResParticles)
                foreach (int divisor in (ReadOnlySpan<int>)[2, 4])
                {
                    if (particleDraw.CountFor(divisor) == 0) continue;
                    post.BeginParticlesLow(divisor);
                    host.Open(13, post.ParticleTargets(divisor));
                    particleDraw.Record(divisor, post.SoftParticleDepth(divisor));
                    host.Close();
                    post.MarkParticleDraws();
                    post.CompositeParticlesLow(divisor);
                }
            StageClock.Lap(13);
        }
        post.MarkParticles();
        post.Particles = gpu.Particles;
        post.SetCloudMotion(gpu.Sky.CloudMotion);
        post.End(); // SSAO, upscaler, exposure, tone map, FXAA into the chain's target
        var slices = SceneSlices(post, camera).ToList();
        // The movement marker and stealth arrows over the finished picture, in their materials' colours, sorted against the near slice's depth like the game's debug_marker.
        if (gpu.Markers is { Active: true } marker && particleNear > 0 && post.Target is { } picture)
        {
            var (markerNear, markerFar) = slices[^1];
            marker.Record(picture, width, height, post.SceneDepth, post.RenderWidth, post.RenderHeight, view * camera.Projection(aspect, markerNear, markerFar), eye, sun);
        }
        // The navmesh debug view over the finished picture (Meitou only).
        if (gpu.NavmeshOverlay is { Visible: true } navOverlay && post.Target is { } navPicture)
        {
            var (navNear, navFar) = slices[^1];
            navOverlay.Record(navPicture, width, height, post.SceneDepth, post.RenderWidth, post.RenderHeight, view * camera.Projection(aspect, navNear, navFar), eye);
        }
        // The hover outline over the finished picture too (Meitou only): the mask drawn after the portraits, sorted against the near slice.
        if (gpu.HoverOutline is { } outline && gpu.Characters?.HoverMasks is { Count: > 0 } hover && post.Target is { } hoverPicture)
        {
            var (hoverNear, hoverFar) = slices[^1];
            outline.Record(hoverPicture, width, height, hover, post.SceneDepth, post.RenderWidth, post.RenderHeight, hoverNear, hoverFar, view * camera.Projection(aspect, hoverNear, hoverFar));
        }
        if (gpu.DebugShadows > 0 && gpu.Shadow is not null)
        {
            var (nearestNear, nearestFar) = slices[^1];
            gpu.Shadow.DrawDebug(gpu.DebugShadows, post.Target!, width, height, view, camera.Projection(aspect, nearestNear, nearestFar), eye);
        }
        if (gpu.GiDebug > 0 && gpu.GiActive && gpu.Gi is { } traced && gpu.GiDebugPass is { } giDebug && post.Target is { } giTarget)
        {
            // The near slice's depth back to positions relative to the eye: the rotation-only view and that slice's projection, inverted.
            var (nearestNear, nearestFar) = slices[^1];
            Matrix4x4.Invert(rotation * camera.Projection(aspect, nearestNear, nearestFar), out var inverse);
            giDebug.Run(gpu.GiDebug, traced, post.SceneDepth, giTarget, post.RenderWidth, post.RenderHeight, inverse, eye, light);
        }
        StageClock.Lap(10);
    }

    /// <summary>The air layer's density by the time of day (docs/render-shafts.md "Air layer"): <c>ShaftAirDawn</c> at sunrise and at sunset, easing (smoothstep) to
    /// <c>ShaftAirDay</c> over <c>ShaftAirRamp</c> hours after sunrise and before sunset; at night the dawn value (the shafts need the sun anyway).</summary>
    internal static float ShaftAirByTime(PostOptions o, Meitou.Data.World.SkyClock clock, float hour)
    {
        hour = ((hour % 24) + 24) % 24;
        float fromEvent = hour < clock.Sunrise || hour > clock.Sunset ? 0 : Math.Min(hour - clock.Sunrise, clock.Sunset - hour);
        float t = Math.Clamp(fromEvent / PostOptions.ShaftAirRamp, 0, 1);
        return PostOptions.ShaftAirDay + (o.ShaftAirDawn - PostOptions.ShaftAirDay) * (1 - t * t * (3 - 2 * t));
    }

    /// <summary>How much of the haze in shadow the light shafts take off (docs/render-shafts.md): the strength times the sun's share of the light the
    /// air scatters (the sun's colour against the sky's ambient, by luminance), faded out as the sun sets, when the sky's light is all there is.</summary>
    internal static float ShaftDarkening(PostOptions o, WorldLighting light, float sunY)
    {
        static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
        float sun = Luminance(light.SunColour), sky = Luminance(light.AmbientSky);
        float share = sun / Math.Max(sun + sky, 1e-4f);
        float t = Math.Clamp((sunY + 0.02f) / 0.07f, 0, 1);
        return o.ShaftStrength * share * t * t * (3 - 2 * t);
    }

    /// <summary>How overcast the sky is, 0..1, for what fades under clouds (the light shafts, the Meitou water's glint): the sky's cloud density c, or the
    /// weather's <c>rainAmount</c> (saturate(rain / 50)) where that is more, since rain falls from a covered sky whatever c the weather lists
    /// (<c>swamp rain no wind</c> has c 0.5 and rain 40).</summary>
    internal static float Overcast(float cloudDensity, float rain) => MathF.Max(Math.Clamp(cloudDensity, 0, 1), WeatherSurfaces.RainAmount(rain));

    static float Overcast(Gpu gpu) => Overcast(gpu.Sky.CloudDensity, gpu.Surfaces?.Rain ?? 0);

    /// <summary>What is left of the light shafts under the cloud layer (docs/render-shafts.md "Cloud cover"): the game's sun shines through any weather, but
    /// crisp shafts under a full overcast in the rain read wrong, so the darkening loses <c>ShaftCloudShade</c> of itself as the cloud density c goes from
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
        // With the scheduler the value is the state's own (the weather's field × the strength × the sun factor, ramped on the settling time, snapped on a
        // teleport); without a scheduler (no database) it is the old forced path at strength 1, ramped here.
        var weather = gpu.Weather;
        float field = weather?.State.Weather.HeatHaze ?? gpu.Sky.Weather.HeatHaze;
        gpu.HeatHazeTarget = HeatHaze.Target(field, weather?.State.Strength ?? 1, sunY);
        if (weather is not null) post.HeatHazeAmount = weather.State.HeatHaze;
        else post.HeatHazeAmount = first || post.InstantAdaptation ? gpu.HeatHazeTarget : HeatHaze.Step(post.HeatHazeAmount, gpu.HeatHazeTarget, weather?.Times.Settling ?? dt);
        if (!post.InstantAdaptation) gpu.HeatHazeHours += dt * HeatHaze.HoursPerSecond;
        post.HeatHazeHours = gpu.GameHours ?? gpu.HeatHazeHours;
        post.HeatHazeFarClip = KenshiHaze.FarDistance(Meitou.Data.World.HeatHaze.ViewDistanceSetting);   // the heat haze's D follows the install's `view distance`, not the sky haze's constant
    }

    // DrawShadows's cascades for the characters (render thread).
    static readonly (int, Vector4[], float, float)[] CasterSpecs = new (int, Vector4[], float, float)[ShadowSettings.MaxCascades];

    /// <summary>
    /// The sun's shadow cascades (ShadowPass, docs/formats/shadows.md): fitted to this camera, their casters drawn by the renderers'
    /// depth-only paths (terrain, objects, foliage meshes; detail chosen from the camera's eye), before the reflection and the main pass.
    /// The characters of all the cascades drawn are gathered first, in one walk.
    /// </summary>
    /// <param name="sunHeight">The real sun's height: the map is drawn along the lighting direction while the sun still has a colour (as the game).</param>
    public static void DrawShadows(Gpu gpu, WorldCamera camera, WorldRenderOptions render, WorldLighting light, int width, int height, float? sunHeight = null,
        bool? temporal = null)
    {
        var shadow = gpu.Shadow!;
        // The caller's post chain owns the temporal decision (Metal's PostTaa/MetalFX is outside gpu.Post).
        shadow.Temporal = temporal ?? gpu.PostChain?.Temporal == true;
        var v = camera.View;
        var view = new ShadowView(camera.Eye, camera.Forward, Vector3.Normalize(new Vector3(v.M12, v.M22, v.M32)), camera.FieldOfView, width / (float)Math.Max(height, 1), camera.Near, camera.Distance);
        int objects = 0, foliage = 0;
        gpu.Terrain.DepthTriangles = 0;
        var cascades = DetailedStats ? new System.Text.StringBuilder() : null;
        // Characters take the far cascades' floor in both shadow modes: their levels are Meitou's own, and every character is in every cascade that holds the crowd.
        // In Meitou's coarse cascades a part under a texel across casts nothing (ShadowPass.MinCharacterCasterTexels).
        (float LodFloor, float MinRadius) CharacterCasting(ShadowCascade cascade)
        {
            float coarse = shadow.Meitou && ShadowLod.Applies(cascade.Index) ? (float)cascade.Texel : 0;
            return (ShadowLod.ObjectLodFloor(ShadowLod.Applies(cascade.Index) ? (float)cascade.Texel : 0, render.TerrainPixelScale),
                ShadowPass.CharacterCasterCull ? coarse * ShadowPass.MinCharacterCasterTexels * 0.5f : 0);
        }
        // The characters of every cascade drawn this frame, gathered in one walk before the first (CharacterRenderer.PrepareDepth).
        void PrepareCasters(ReadOnlySpan<ShadowPass.CasterView> views)
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
        shadow.Render(view, light.SunDirection, (cascade, worldToClip, planes, lodEye) =>
        {
            long t0 = Stopwatch.GetTimestamp();
            long tri = gpu.Terrain.DepthTriangles;
            float coarse = shadow.Meitou && ShadowLod.Applies(cascade.Index) ? (float)cascade.Texel : 0;   // a far cascade: coarser casters (ShadowLod)
            gpu.Terrain.DrawDepth(worldToClip, lodEye, planes, render, coarse);
            StageClock.Sub("terrain");
            long t1 = Stopwatch.GetTimestamp();
            int oi = 0, oc = 0, fi = 0, fc = 0;
            if (render.Objects && gpu.Objects is { } o) { o.DrawDepth(worldToClip, lodEye, planes, render, gpu.Terrain, coarse); objects += o.DrawnInstances; (oi, oc) = (o.DrawnInstances, o.DrawCalls); }
            StageClock.Sub("objects");
            var (characterFloor, characterMinRadius) = CharacterCasting(cascade);
            gpu.Characters?.DrawDepth(worldToClip, lodEye, planes, characterFloor, characterMinRadius, cascade.Index);
            StageClock.Sub("characters");
            long t2 = Stopwatch.GetTimestamp();
            if (gpu.Foliage is { } f) { f.DrawDepth(worldToClip, lodEye, planes, render, gpu.Terrain, maxRange: shadow.EffectiveRange * 1.2f, minSize: shadow.MinFoliageCaster(cascade), texel: (float)cascade.Texel, coarse: coarse > 0); foliage += f.DrawnInstances; (fi, fc) = (f.DrawnInstances, f.DrawCalls); }
            StageClock.Phase(CascadeLabels[cascade.Index]);
            long t3 = Stopwatch.GetTimestamp();
            double ms = 1000.0 / Stopwatch.Frequency;
            shadow.PhaseMs[0] += (t1 - t0) * ms;
            shadow.PhaseMs[1] += (t2 - t1) * ms;
            shadow.PhaseMs[2] += (t3 - t2) * ms;
            cascades?.Append($" [c{cascade.Index}: texel {cascade.Texel:0.0}, terrain {(gpu.Terrain.DepthTriangles - tri) / 1000}k tri {(t1 - t0) * ms:0.00} ms, objects {oi} in {oc} calls {(t2 - t1) * ms:0.00} ms, foliage {fi} in {fc} calls {(t3 - t2) * ms:0.00} ms ({gpu.Foliage?.DepthDetail})]");
        }, sunHeight, render.Objects && gpu.Objects is { HasLandmarks: true } lo
            ? new ShadowPass.LandmarkCasters(spheres => lo.LandmarkCasters(camera.Eye, spheres),
                (_, worldToClip, planes, lodEye) => lo.DrawLandmarksDepth(worldToClip, lodEye, planes, render, gpu.Terrain))
            : null, PrepareCasters);
        shadow.CasterStats = $"{gpu.Terrain.DepthTriangles:N0} terrain triangles, {objects} objects, {foliage} foliage meshes (over the cascades); cpu terrain {shadow.PhaseMs[0]:0.00}, objects {shadow.PhaseMs[1]:0.00}, foliage {shadow.PhaseMs[2]:0.00} ms;{cascades}"
            + (shadow.Meitou ? $"; meitou: {shadow.DescribeMeitou()}" : "");
    }
}
