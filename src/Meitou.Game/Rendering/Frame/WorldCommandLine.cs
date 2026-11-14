using System.Numerics;
using System.Globalization;
using Meitou.Data.World;
using Meitou.Game.Rendering.Bench;
using Meitou.Game.Rendering.Profiling;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// The world options' command line: its help text and the parse of <c>meitou-viewer --world</c>'s (and the game's) options into
/// <see cref="WorldOptions"/>.
/// </summary>
static class WorldCommandLine
{
    #region Usage

    /// <summary>
    /// The help text: the options and the viewer's keys.
    /// </summary>
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
          --headless                                     no window, swapchain or presentation: the interactive loop draws into an offscreen picture of --size (default 1920x1080), 30 fps unless --fps-limit; no sound; for the control channel (--control-dir) and its screenshot command; env MEITOU_HEADLESS=1
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
          --water-grid <n>         the Meitou water grid's segments round the eye (default 1024; 256 lower detail)
          --water-ocean <n>        the Meitou water's FFT size per cascade: 64, 128 or 256 (default 256; 128 for integrated GPUs, a quarter of the work)
          --water-reflection <0..4> the game's `water reflection`: what the water mirrors: 0 nothing (sky colour; R toggles), 1 sky and terrain, 2 + characters, 3 + buildings and features, 4 + trees, bushes and rocks (default 4; the game's own default is 2; Tab slider)
          --reflection-samples <n> samples per texel of the half-resolution reflection target (default 4; 1 disables MSAA; the device may cap it)
          --texture-quality <0..4> the game's `texture resolution gimping`: 0 Maximum, 1 High, 2 Medium, 3 Low, 4 Fugly; each step drops the top mip of compressed textures as they load (default 0, full size; the game's own default is 1)
          --shadow-quality <0|1|2> map side 1024/2048/4096 (default 2)   --shadow-range <u> (1000..9000, default 5000; with the Meitou shadows 1000..15000, default 10000)
          --shadow-range 0         shadows start off (the Tab slider turns them on)
          --shadow-filter <0|1|2>  Meitou shadow filter: 2 full (default), 1 the far cascades cheaper, 0 cheapest everywhere (low-end GPUs)
          --debug-shadows <n>      1 the four cascade maps, 2 the shadow term of the surfaces by cascade, 3 the term multiplied over the picture, 4 the traced sun's share and mask
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
          --faithful <all|ao,dither,haze,aa,shadows,range,impostors,reach,water,particles,lod,gi,tonemap,shafts,heathaze,night,planetshine,stars,nightgrade,clouds,cloudshadows,cloudfog,interiorgi,charlod,skin,interiorlamps,icons,blood,bloodsurfaces,footprints,parallax,displace,weld,tracers,fog,gihybrid,trails,contactfoam,gtao,lampshadows,ssr,lampwarmth>   the game's look instead of Meitou's enhancements (default: all Meitou; --meitou <...> turns them back on; these ids also name the --ab switches)
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
          --sequence <n>           with --screenshot: n more frames saved after it (name-01.png ...), each one step of the motion
          --pan-step <x>,<z>       with --screenshot: the camera's target moves this far every frame
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
          --gtao-quality <0..2>    the gtao switch's budget (default 1): 0 half size, 2 x 4; 1 half size, 3 x 6; 2 render size, 3 x 6
          --particle-divisor <0|1|2|4>   the weather's alpha and additive particles drawn at 1/n of the render size (0, the default: by sprite size; `--faithful particles` draws them full size)
          --gi-hybrid <parts>      the gihybrid switch on with these parts only (comma list: relocate, clouds, cache)
          --no-heat-haze           no heat haze (the game's HeatHaze setting, default on: strength from the weather's `heat haze`)
          (ambient occlusion, dithering, light shafts, heat haze strength, the tone map, night grading and the rest are the switches above: --meitou / --faithful <id>; their strengths are Tab sliders)
        Keys: left drag orbit, right drag look around, wheel zoom, W/A/S/D free fly along the view, Q/E down/up (Shift faster, Ctrl slower),
          T textures, N normal maps, O objects, F foliage, X wireframe, V debug view,
          G water, R water reflections, , / . time of day -/+ 1 hour (the day follows midnight), [ / ] game day -/+ 1 (the weather catches up), \ cycle the forced weather (auto, then each WEATHER record), H print camera, Ctrl+C copy camera code, Ctrl+V go to camera code, P save screenshot, Tab settings sliders, Alt+Enter borderless full screen, Esc quit.
          F1 Meitou on / off (every switch Faithful, then back as they were; the Tab panel sets them one by one), Ctrl+F1 flips each switch in turn, 1 s apart (again stops); - / = exposure; F9 fps counter, F10 key list, F11 frame statistics, F12 profiler (gpu, cpu, off).
          Num1-9 cut to a camera key, Ctrl+Num1-9 save the camera as key 1-9, NumEnter fly through the keys (Shift: record PNG frames), Num0 loop, Num+ / Num- slower / faster, Num* clean picture (letterbox), Num/ time of day follows the keys, Num. list keys (Ctrl: delete all), PageUp / PageDown zoom lens, Home lens back to 50 deg, F2 camera timeline editor (Space play, Del delete key).
        """;

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
        if (!o.SizeGiven && (o.BenchFrames > 0 || o.Ab is not null)) (o.Width, o.Height) = WorldOptions.BenchSize;
        return o;
    }

    /// <summary>
    /// The command line as <see cref="Parse"/> walks it: the option at hand and the values after it.
    /// </summary>
    sealed class OptionReader(string[] args)
    {
        /// <summary>
        /// The index of <see cref="Current"/> in the arguments; -1 before the first <see cref="MoveNext"/>.
        /// </summary>
        int _at = -1;

        /// <summary>
        /// The option at hand.
        /// </summary>
        public string Current => args[_at];

        /// <summary>
        /// Steps to the next argument; false past the last.
        /// </summary>
        public bool MoveNext() => ++_at < args.Length;

        /// <summary>
        /// Consumes and returns the value after <see cref="Current"/>; throws when there is none.
        /// </summary>
        public string Next() => _at + 1 < args.Length ? args[++_at] : throw new ArgumentException($"{Current} needs a value");

        /// <summary>
        /// The next value as an invariant-culture float.
        /// </summary>
        public float Float() => float.Parse(Next(), CultureInfo.InvariantCulture);

        /// <summary>
        /// The next value as an invariant-culture integer.
        /// </summary>
        public int Int() => int.Parse(Next(), CultureInfo.InvariantCulture);

        /// <summary>
        /// The next value as two invariant-culture numbers <c>a,b</c>; throws when it is not two.
        /// </summary>
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
                        o.ShadowRange = Math.Clamp(range, KenshiShadows.MinRange, WorldOptions.CommandLineMaxShadowRange);
                    break;
                }
            case "--debug-shadows": o.DebugShadows = r.Int(); break;
            case "--gi-debug": o.GiDebug = Math.Clamp(r.Int(), 0, 3); break;
            case "--no-gi": o.Gi = false; break;
            case "--meitou": Enhancements.Apply(WorldOptions.Switches(o), r.Next(), meitou: true); break;
            case "--faithful": Enhancements.Apply(WorldOptions.Switches(o), r.Next(), meitou: false); break;
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
            case "--sequence": o.Sequence = Math.Max(r.Int(), 0); break;
            case "--zoom-step": o.ZoomStep = r.Float(); break;
            case "--pan-frames": o.PanFrames = Math.Max(r.Int(), 0); break;
            case "--pan-step":
            {
                var (px, pz) = r.Pair();
                o.PanStep = new Vector2((float)px, (float)pz);
                break;
            }
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
