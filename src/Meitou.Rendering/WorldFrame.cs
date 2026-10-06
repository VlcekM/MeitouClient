using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

sealed class WorldOptions
{
    public double? X, Z;
    public ZoneCoordinate? Zone;
    public string? Town;
    public float Radius = 1.5f;
    public int? Step;
    public float? Yaw, Pitch, Distance;
    public string? Screenshot;
    public int Width = 1280, Height = 960;
    public bool NoTextures, NoObjects, Wireframe, Info;
    public int LayerSize = 2048;
    public int Debug;
    public float ObjectDistance = 12000;
    public float DistantZones = ObjectRanges.MaxDistantTownRangeZones;
    public bool NoDistant;
    public bool NoFoliage;
    public float Hour = 13;
    public float ViewDistance = 450000, FogDistance = 250000, MaterialDistance = 30000;
    public bool NoWater, NoStream, NoReflections, SimpleSky, ShowKeys;
    /// <summary>The game's <c>texture resolution gimping</c> (0..4; missing key: 1) and <c>water reflection</c> (0..4; missing: 2) / <c>reflection range</c> (missing: 0.6) settings (docs/formats/settings.md).</summary>
    // The viewer starts at full quality (the old look); the game's missing-key defaults are TextureQuality.Default (1),
    // ReflectionPass.DefaultLevel (2) and DefaultRange (0.6): --texture-quality 1 --water-reflection 2 --reflection-range 0.6.
    public int TextureQuality = 0, WaterReflection = 4;
    public float ReflectionRange = 3;
    /// <summary>Sun shadows (docs/formats/shadows.md): off, the game's <c>shadow quality</c> index, <c>Shadow Range</c>, the debug view.</summary>
    public bool NoShadows;
    public int ShadowQuality = 1, DebugShadows;
    public float ShadowRange = KenshiShadows.DefaultRange;
    public bool MeitouShadows = true;   // the shadows switch (Enhancements): Meitou by default, false the game's CSM
    /// <summary>The range switch (Enhancements): foliage meshes drawn to their size class's range (Meitou, default) or their layer's (the game); the class ranges (null: the defaults).</summary>
    public bool MeitouRange = true;
    public float? SmallRange, MediumRange, LargeRange;
    public bool PhysicalHaze; // the game's own haze by default (docs/formats/sky.md "Haze")
    public float? HazeDistance;
    public float HazeStrength = Enhancements.MeitouHazeStrength; // the Meitou haze switch (default); 1 = the game's haze
    public string? Weather;
    public float? Clouds;
    public PostOptions Post = PostOptions.Create("meitou");
    public double? CameraX, CameraZ, FlyToX, FlyToZ;
    /// <summary>Frames of the offscreen benchmark flight (0: none), the circle's radius and the speed per frame.</summary>
    public int FlyBenchmark;
    public float FlyRadius = 12000, FlySpeed = 150;
    /// <summary>The benchmark without a wait for the GPU each frame and without the 60 fps pacing: up to two frames in flight (both backends), frame time = the interval between frames.</summary>
    public bool FlyPipelined;
    /// <summary>Offscreen pictures: radians the camera orbits by every frame (tests the motion vectors under a temporal upscaler).</summary>
    public float OrbitStep;
    /// <summary>Interactive window: the monitor it opens on (1-based; <c>--monitor</c>).</summary>
    public int? Monitor;
    /// <summary>Offscreen pictures: seconds the grass sway advances every frame (tests the grass motion under a temporal upscaler; 0 holds it still).</summary>
    public float SwayStep;
    /// <summary>Offscreen pictures: the grass sway's starting time in seconds (0 by default).</summary>
    public float SwayStart;

    public const string Usage = """
        meitou-viewer --world [where] [options]
          where (default: the world's centre):
            --at <x>,<z>           world X/Z
            --zone <i>,<j>         centre of zone.i.j (i along +X, j along +Z)
            --town <name>          a town whose name contains <name> (FCS TOWN record)
          --radius <zones>         half-size of the loaded square in zones (default 1.5; 4608 units each)
          --step <n>               heightmap sample step (default: smallest power of two keeping <= 2048 cells per side)
          --yaw <deg> --pitch <deg> --distance <units>   camera around the point (defaults 30, 35, radius)
          --screenshot <out.png> --size <W>x<H>          render offscreen to a PNG and exit
          --monitor <n>                                  open the window on monitor n (1-based)
          --no-textures            height-tinted terrain without biome textures (faster start)
          --no-objects             skip buildings and map features
          --no-foliage             no trees, bushes, rocks or grass (F toggles)
          --range-large <u> --range-medium <u> --range-small <u>   foliage draw range by mesh size (the range switch, F6; defaults 5000, 2500, 800; Tab sliders)
          --object-distance <u>    draw placed objects at full detail up to this distance (default 12000)
          --distant-range <zones>  distant towns (and buildings' distant meshes) up to this many zones (default 10, the game's setting maximum; its default is 6)
          --no-distant             no distant towns: objects beyond --object-distance are simply not drawn
          --layer-size <n>         terrain layer texture size, a power of two up to 2048 (default 2048)
          --debug <n>              1 blend-map slot weights, 2 layer weights (R cliff, G slope, B grass)
          --time <hour>            time of day for the sun (default 13; sunrise and sunset from the CONSTANTS record)
          --no-water               leave out the water
          --no-reflections         the water reflects only the sky colour, not the mirrored scene (R toggles; the same as --water-reflection 0)
          --water-reflection <0..4> the game's `water reflection`: what the water mirrors: 0 nothing (sky colour), 1 sky and terrain, 2 the same (the characters' level; none yet), 3 + buildings and features, 4 + trees, bushes and rocks (default 2; Tab slider)
          --reflection-range <x>   the game's `reflection range`: the mirrored scene is drawn out to haze distance x this (default 0.6, with the default haze distance 30000)
          --texture-quality <0..4> the game's `texture resolution gimping`: 0 Maximum, 1 High, 2 Medium, 3 Low, 4 Fugly; each step drops the top mip of compressed textures as they load (default 1; 0 restores full size)
          --no-shadows             no sun shadow map   --shadow-quality <0|1|2> map side 1024/2048/4096 (default 1)   --shadow-range <u> (1000..9000, default 5000)
          --debug-shadows <n>      1 the four cascade maps, 2 the shadow term of the surfaces by cascade, 3 the term multiplied over the picture
          (the shadows switch, F5: Meitou by default, view-fitted cascades with soft contact-hardening penumbrae and the terrain's shadow out to the horizon; --faithful shadows the game's CSM)
          --simple-sky             the old colour-model sky and squared-distance fog instead of the atmosphere (B toggles)
          --haze <kenshi|physical>  aerial perspective: the game's own haze (default) or the physical integral (F7 toggles)
          --haze-distance <u>      the game's far distance D (view distance x 10) for its haze, which ramps in from 0.06 D to 0.6 D (default 50000: view distance 5000)
          --haze-strength <x>      the viewer's haze strength: scales how far the haze is blended in (default 0.87: far mountains stay visible; 1 is the game's; also a Tab slider)
          --weather <name>         a WEATHER record's sky colour, fog, clouds and heat haze (default "Default": clear, no fog, no clouds, no heat haze)   --clouds <0..1> cloud coverage
          --camera-at <x>,<z>      start the camera here instead of at the loaded point (as if flown there)
          --no-stream              keep the terrain detail around the start point instead of following the camera
          --faithful <all|ao,dither,haze,aa,shadows,range>   the game's look instead of Meitou's enhancements (default: all Meitou; --meitou <...> turns them back on)
          --show-keys              start with the key list overlay open (toggle with F10)
          --fly-to <x>,<z>         with --screenshot: fly there first (streaming test, reports frame times), then take the picture
          --fly-benchmark <frames> offscreen, no window: fly the camera round a circle at 60 frames per second of wall time, print frame-time
                                   percentiles, the worst frames with their stage times and resident memory   --fly-radius <u> (12000)   --fly-speed <u per frame> (150)
                                   --fly-pipelined: no GPU wait per frame and no pacing, two frames in flight; reports the interval between frames
          --orbit-step <degrees>   with --screenshot: the camera orbits this much every frame (checks the upscaler's motion vectors)
          --sway-step <seconds>    with --screenshot: the grass sway advances this much every frame (checks the grass motion)
          --sway-start <seconds>   with --screenshot: the grass sway's time at the start (default 0)
          --renderer vulkan        accepted and ignored (Vulkan is the only backend)
          --view-distance <u>      furthest terrain drawn (default 450000: the whole world)
          --fog <u>                distance where the haze is complete (default 250000)
          --material-distance <u>  beyond it the terrain shows the biomes' ground colour (default 30000, as the game)
          --wireframe --info
          --post <meitou|kenshi|off>   post-processing preset (default meitou), before the options below: HDR scene, SSAO
          --ssao / --no-ssao, --dither / --no-dither   --no-fxaa
          --no-heat-haze           no heat haze (the game's HeatHaze setting, default on: strength from the weather's `heat haze`)   --heat-haze <x> replaces that field (testing)
          --exposure <x>  --ssao-radius <units>  --ssao-strength <x>
        Keys: left drag orbit, right drag look around, wheel zoom, W/A/S/D free fly along the view, Q/E down/up (Shift faster, Ctrl slower),
          T textures, N normal maps, O objects, F foliage, X wireframe, V debug view,
          G water, R water reflections, B simple sky, , / . time of day -/+ 1 hour, H print camera, Ctrl+C copy camera code, Ctrl+V go to camera code, P save screenshot, Tab settings sliders, Esc quit.
          F1 ambient occlusion, F2 dithering, F3 haze, F4 anti-aliasing, F5 shadows, F6 foliage ranges;
          - / = exposure; F10 key list, F11 frame statistics, F12 profiler (gpu, cpu, off).
        """;

    /// <summary><c>--renderer</c> is kept so old command lines work: <c>vulkan</c> is accepted, anything else says OpenGL is gone; either way it changes nothing.</summary>
    public static void IgnoreRenderer(string value)
    {
        if (value != "vulkan") Console.Error.WriteLine($"renderer  --renderer {value}: OpenGL was removed, Vulkan is the only backend (the option is ignored)");
    }

    /// <summary>The Faithful / Meitou switches over the options (for <c>--meitou</c> / <c>--faithful</c>).</summary>
    static IReadOnlyList<Enhancement> Switches(WorldOptions o) => Enhancements.Create(o.Post, () => o.HazeStrength, v => o.HazeStrength = v,
        () => o.MeitouShadows, v => o.MeitouShadows = v, () => o.MeitouRange, v => o.MeitouRange = v);

    public static WorldOptions? Parse(string[] args)
    {
        var o = new WorldOptions();
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
                case "--step": o.Step = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--yaw": o.Yaw = F(); break;
                case "--pitch": o.Pitch = F(); break;
                case "--distance": o.Distance = F(); break;
                case "--screenshot": o.Screenshot = Next(); break;
                case "--size":
                    var parts = Next().Split('x');
                    o.Width = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    o.Height = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    break;
                case "--no-textures": o.NoTextures = true; break;
                case "--no-objects": o.NoObjects = true; break;
                case "--no-foliage": o.NoFoliage = true; break;
                case "--range-large": o.LargeRange = F(); break;
                case "--range-medium": o.MediumRange = F(); break;
                case "--range-small": o.SmallRange = F(); break;
                case "--object-distance": o.ObjectDistance = F(); break;
                case "--distant-range": o.DistantZones = F(); break;
                case "--no-distant": o.NoDistant = true; break;
                case "--layer-size": o.LayerSize = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--debug": o.Debug = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--wireframe": o.Wireframe = true; break;
                case "--info": o.Info = true; break;
                case "--time": o.Hour = F(); break;
                case "--no-water": o.NoWater = true; break;
                case "--no-reflections": o.NoReflections = true; break;
                case "--water-reflection": o.WaterReflection = Math.Clamp(int.Parse(Next(), CultureInfo.InvariantCulture), 0, 4); break;
                case "--reflection-range": o.ReflectionRange = F(); break;
                case "--texture-quality": o.TextureQuality = Math.Clamp(int.Parse(Next(), CultureInfo.InvariantCulture), 0, Meitou.Data.Textures.TextureQuality.Maximum); break;
                case "--no-shadows": o.NoShadows = true; break;
                case "--shadow-quality": o.ShadowQuality = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--shadow-range": o.ShadowRange = Math.Clamp(F(), KenshiShadows.MinRange, KenshiShadows.MaxRange); break;
                case "--debug-shadows": o.DebugShadows = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--simple-sky": o.SimpleSky = true; break;
                case "--haze": o.PhysicalHaze = Next() switch { "kenshi" => false, "physical" => true, var h => throw new ArgumentException($"--haze: kenshi or physical, not {h}") }; break;
                case "--haze-distance": o.HazeDistance = F(); break;
                case "--haze-strength": o.HazeStrength = F(); break;
                case "--meitou": Enhancements.Apply(Switches(o), Next(), meitou: true); break;
                case "--faithful": Enhancements.Apply(Switches(o), Next(), meitou: false); break;
                case "--weather": o.Weather = Next(); break;
                case "--clouds": o.Clouds = F(); break;
                case "--no-stream": o.NoStream = true; break;
                case "--show-keys": o.ShowKeys = true; break;
                case var post when o.Post.TryParse(post, Next): break;
                case "--camera-at": (o.CameraX, o.CameraZ) = Pair(); break;
                case "--fly-to": (o.FlyToX, o.FlyToZ) = Pair(); break;
                case "--fly-benchmark": o.FlyBenchmark = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--renderer": IgnoreRenderer(Next()); break;
                case "--fly-radius": o.FlyRadius = F(); break;
                case "--fly-speed": o.FlySpeed = F(); break;
                case "--fly-pipelined": o.FlyPipelined = true; break;
                case "--orbit-step": o.OrbitStep = F() * MathF.PI / 180; break;
                case "--monitor": o.Monitor = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--sway-step": o.SwayStep = F(); break;
                case "--sway-start": o.SwayStart = F(); break;
                case "--view-distance": o.ViewDistance = F(); break;
                case "--fog": o.FogDistance = F(); break;
                case "--material-distance": o.MaterialDistance = F(); break;
                case "-h" or "--help": return null;
                default: throw new ArgumentException($"unknown option {a}");
            }
        }
        if (o.Radius <= 0) throw new ArgumentException("--radius must be positive");
        return o;
    }
}

/// <summary>The loaded world region: heights (the region fine, the whole world coarse), and where the camera starts.</summary>
sealed class WorldScene : IDisposable
{
    public required TerrainHeightmap Heightmap;
    public required HeightWindow Window;
    /// <summary>Whole-world heights every <see cref="WorldFrame.CoarseStep"/>-th sample, (CoarseSize)² raw values.</summary>
    public required ushort[] Coarse;
    public required int CoarseSize;
    public SkyClock Clock = SkyClock.Fallback;
    public required Vector3 Focus;
    public required double X0, Z0, X1, Z1;
    public GameDatabase? Database;
    public WorldObjects? Objects;

    public float GroundAt(float x, float z) => Heightmap.HeightAt(x, z);

    public void Dispose() => Heightmap.Dispose();
}

/// <summary>
/// The world frame: loading the region, creating the renderers, and drawing a frame (shadows, reflection, sky, the depth
/// slices, water, post-processing). Shared by the game (<c>meitou</c>) and the viewer (<c>meitou-viewer --world</c>).
/// </summary>
static class WorldFrame
{
    /// <summary>Heightmap step of the whole-world height grid behind the loaded region (2049² samples, 144 units apart).</summary>
    public const int CoarseStep = 8;

    public static WorldScene? Load(GameInstall install, WorldOptions o)
    {
        var watch = Stopwatch.StartNew();
        var map = TerrainHeightmap.Open(install);
        GameDatabase? db = null;
        if (o.Town is not null || !o.NoTextures || !o.NoObjects)
        {
            db = GameDatabase.Load(LoadOrder.FromInstall(install));
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
        int step = o.Step ?? 1;
        if (o.Step is null) while (cellsWanted / step > 2048) step *= 2;
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
    public static (WorldCamera, WorldRenderOptions) Setup(WorldScene scene, WorldOptions o)
    {
        float radius = o.Radius * WorldLayout.ZoneSize;
        var camera = new WorldCamera
        {
            Target = o.CameraX is { } cx && o.CameraZ is { } cz ? new Vector3((float)cx, scene.GroundAt((float)cx, (float)cz), (float)cz) : scene.Focus,
            Yaw = (o.Yaw ?? 30) * MathF.PI / 180,
            Pitch = (o.Pitch ?? 35) * MathF.PI / 180,
            Distance = o.Distance ?? radius,
            ViewDistance = o.ViewDistance,
            MinViewDistance = o.ViewDistance,
            SplitDistance = Math.Max(20000, o.ObjectDistance * 1.1f),
        };
        var render = new WorldRenderOptions { Textures = !o.NoTextures, Objects = !o.NoObjects, Water = !o.NoWater, Reflections = !o.NoReflections, Wireframe = o.Wireframe ? 1 : 0, Debug = o.Debug, MaterialDistance = o.MaterialDistance };
        return (camera, render);
    }

    internal sealed class Gpu : IDisposable
    {
        public required TerrainRenderer Terrain;
        public required SkyRenderer Sky;
        public WaterRenderer? Water;
        public ReflectionPass? Reflection;
        public ShadowPass? Shadow;
        public int DebugShadows;
        public PostProcess? Post;
        public WorldObjectRenderer? Objects;
        public FoliageRenderer? Foliage;
        public TerrainStreamer? Streamer;
        /// <summary>With <c>--no-stream</c>: where the streamer is kept, instead of at the eye.</summary>
        public Vector3? Anchor;
        /// <summary>The heat haze's target this frame (the weather's <c>heat haze</c> × strength 1 × the sun factor), for the statistics.</summary>
        public float HeatHazeTarget;
        /// <summary>Game hours since the load for the heat haze's animation, when the caller runs a game clock; null: real time at game speed 1.</summary>
        public double? GameHours;
        internal readonly Stopwatch HeatHazeClock = new();
        internal double HeatHazeHours;
        public void Dispose()
        {
            Streamer?.Dispose();
            Foliage?.Dispose();
            Objects?.Dispose();
            Water?.Dispose();
            Reflection?.Dispose();
            Shadow?.Dispose();
            Post?.Dispose();
            Sky.Dispose();
            Terrain.Dispose();
        }
    }

    /// <summary>
    /// The end of loading: the GPU finishes the uploads queued so far (Vulkan records them all into the next frame: 1.7 GB at the
    /// benchmark start, a 300 ms stall two frames later), and one full, compacting collection runs now instead of a blocking gen2 collection a few
    /// frames into play (300+ ms measured), then gen2 collections only in the background while the world runs (DECISIONS 12).
    /// </summary>
    public static void FinishLoading(IGl gl)
    {
        gl.Finish();
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
    }

    public static Gpu CreateGpu(IGl gl, GpuContext context, GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o, bool interactive)
    {
        var watch = Stopwatch.StartNew();
        // The game's texture quality, before any texture loads: the world's texture caches read it as they decode. The terrain's layer arrays
        // have one size, so the Landscape group's drop (the game's textures are 2048²) lowers it instead of dropping mips per file.
        Meitou.Data.Textures.TextureQuality.Level = o.TextureQuality;
        int layerSize = Math.Max(Math.Min(o.LayerSize, 2048 >> Meitou.Data.Textures.TextureQuality.LevelsToDrop("terrain.dds", "Landscape")), 16);
        if (layerSize != o.LayerSize) Console.WriteLine($"textures  quality {o.TextureQuality} ({Meitou.Data.Textures.TextureQuality.Labels[o.TextureQuality]}): terrain layers {layerSize}² instead of {o.LayerSize}²");
        var terrain = new TerrainRenderer(gl, context, scene.Coarse, scene.CoarseSize, scene.Window, new WorldRenderOptions().LodDistance);
        Console.WriteLine($"uploaded  terrain heights: {terrain.LevelCount} LOD levels, finest {terrain.FinestSpacing:0.#} units ({watch.ElapsedMilliseconds} ms)");
        TerrainTextures? textures = null;
        if (!o.NoTextures && scene.Database is not null)
        {
            textures = TerrainTextures.Create(gl, context, install, scene.Database, assets, layerSize);
            foreach (var m in textures.Messages.Take(20)) Console.WriteLine($"warning   {m}");
            Console.WriteLine($"biomes    {textures.TotalBiomes} in the world, {textures.TotalPairs} texture pairs, {textures.Capacity} slots of {layerSize}² BC3+BC1, {textures.ArrayBytes / 1048576} MB ({watch.ElapsedMilliseconds} ms)");
            terrain.SetTextures(textures);
        }
        var gpu = new Gpu { Terrain = terrain, Sky = new SkyRenderer(gl, context, assets) { Physical = !o.SimpleSky, CloudCoverage = o.Clouds, KenshiHaze = !o.PhysicalHaze }, Post = new PostProcess(gl, context, o.Post) };
        gpu.Post.LoadHeatHaze(assets);
        if (o.HazeDistance is { } hazeDistance) gpu.Sky.HazeDistance = hazeDistance;
        gpu.Sky.HazeStrength = o.HazeStrength;
        if (scene.Database is { } skyDb)
        {
            gpu.Sky.LoadWorld(install, skyDb);   // the ambient map and the CONSTANTS exposure band (docs/formats/lighting.md)
            gpu.Sky.Weather = SkyWeather.Find(skyDb, o.Weather) ?? throw new ArgumentException($"no weather named '{o.Weather}'; known: {string.Join(", ", SkyWeather.Names(skyDb).Distinct().Take(12))} ...");
        }
        gpu.Streamer = new TerrainStreamer(install, terrain, textures, scene.Window.Step) { MaterialDistance = o.MaterialDistance };
        if (o.NoStream) gpu.Anchor = scene.Focus;
        if (!o.NoWater && scene.Database is not null)
        {
            var messages = new List<string>();
            gpu.Water = WaterRenderer.Create(gl, context, install, scene.Database, assets, gpu.Sky, messages);
            gpu.Reflection = new ReflectionPass(gl, context) { Level = o.WaterReflection, Range = o.ReflectionRange };
            foreach (var m in messages) Console.WriteLine($"warning   {m}");
            Console.WriteLine($"water     at height {WorldWater.Height} ({watch.ElapsedMilliseconds} ms)");
        }
        if (scene.Objects is not null)
        {
            gpu.Objects = new WorldObjectRenderer(gl, context, assets, scene.Objects) { ObjectDistance = o.ObjectDistance, DistantRange = o.DistantZones * WorldLayout.ZoneSize, NoDistant = o.NoDistant, LoadBudget = interactive ? 8 : 0 };
            Console.WriteLine($"objects   GPU ready ({watch.ElapsedMilliseconds} ms)");
        }
        if (!o.NoFoliage && scene.Database is not null)
        {
            gpu.Foliage = new FoliageRenderer(gl, context, install, scene.Database, scene.Objects?.Levels ?? WorldLevelData.Load(install), assets);
            if (!interactive) gpu.Foliage.SwaySeconds = o.SwayStart;   // offscreen pictures and benchmarks: the grass holds still, so a picture repeats exactly
            var f = gpu.Foliage;
            (f.MeitouRange, f.SmallRange, f.MediumRange, f.LargeRange) = (o.MeitouRange, o.SmallRange ?? f.SmallRange, o.MediumRange ?? f.MediumRange, o.LargeRange ?? f.LargeRange);
            Console.WriteLine($"foliage   catalog and shaders ready ({gpu.Foliage.LoadMs:0} ms)");
        }
        if (!o.NoShadows)
        {
            // The game's CSM mode (docs/formats/shadows.md): four cascades in one atlas of the `shadow quality` side, out to `Shadow Range`.
            gpu.Shadow = new ShadowPass(gl, context, assets) { Settings = new ShadowSettings(KenshiShadows.MapSize(o.ShadowQuality), o.ShadowRange), Meitou = o.MeitouShadows };
            if (!gpu.Shadow.HasNoise) Console.WriteLine($"warning   shadows: {KenshiShadows.NoiseTexture} not found, the receiver's jitter is a hash");
            gpu.Shadow.SetTerrain(scene.Coarse, scene.CoarseSize);   // the Meitou shadows' terrain shadow beyond the range
            Console.WriteLine($"shadows   {gpu.Shadow.Settings.MapSize}² atlas, {gpu.Shadow.Settings.Cascades} cascades of {gpu.Shadow.Settings.TileSize}², range {o.ShadowRange:0}");
        }
        gpu.DebugShadows = o.DebugShadows;
        return gpu;
    }

    /// <summary>Draws a frame: the sky, then the far depth slice (terrain, water), then the near one (terrain, objects, water).</summary>
    // The Tab panel: draw distances and LOD.
    /// <summary>The upscaler sliders' labels (the game keeps command-line upscaler options over the saved ones).</summary>
    public static readonly string[] UpscalerSliders = ["Anti-aliasing: 0 FXAA 1 TAA 2 FSR 3 DLSS", "Render scale (upscaler)", "Upscaler sharpness"];

    public static SettingsPanel CreateSettingsPanel(DebugOverlay ui, Gpu g, WorldRenderOptions r, Func<float>? getHour = null, Action<float>? setHour = null)
    {
        var sliders = new List<Slider>();
        // The viewer's time of day (the `--time` option and the , / . keys), to the minute. The game passes none: its clock runs on its own.
        if (getHour is not null && setHour is not null)
            sliders.Add(new Slider("Time of day", 0, 24 - 1 / 60f, getHour, v => setHour(MathF.Round(v * 60) / 60), Text: TimeText));
        if (g.Objects is { } objects)
        {
            sliders.Add(new Slider("Object draw distance", 1000, 40000, () => objects.ObjectDistance, v => objects.ObjectDistance = v, "0", Logarithmic: true));
            sliders.Add(new Slider("Distant towns (zones)", 0, ObjectRanges.MaxDistantTownRangeZones,
                () => objects.DistantRange / WorldLayout.ZoneSize, v => objects.DistantRange = MathF.Round(v) * WorldLayout.ZoneSize, "0"));
            sliders.Add(new Slider("Object LOD distance x", 0.25f, 4, () => 1 / objects.LodBias, v => objects.LodBias = 1 / v, "0.00", Logarithmic: true));
        }
        if (g.Foliage is { } foliage)
        {
            sliders.Add(new Slider("Foliage draw distance x", 0.25f, 8, () => foliage.RangeSetting, v => foliage.RangeSetting = v, "0.00", Logarithmic: true));
            // The range switch's class ranges (Meitou; the slider above then only moves the FAR layers' large meshes).
            sliders.Add(new Slider("Large foliage range (F6 Meitou)", 1000, 12000, () => foliage.LargeRange, v => foliage.LargeRange = MathF.Round(v / 50) * 50, "0", Logarithmic: true));
            sliders.Add(new Slider("Medium foliage range", 400, 8000, () => foliage.MediumRange, v => foliage.MediumRange = MathF.Round(v / 50) * 50, "0", Logarithmic: true));
            sliders.Add(new Slider("Small foliage range", 200, 4000, () => foliage.SmallRange, v => foliage.SmallRange = MathF.Round(v / 50) * 50, "0", Logarithmic: true));
            sliders.Add(new Slider("Grass draw distance x", 0.25f, 8, () => foliage.GrassRangeSetting, v => foliage.GrassRangeSetting = v, "0.00", Logarithmic: true));
            sliders.Add(new Slider("Grass density x", 0.1f, 2, () => foliage.GrassDensitySetting, v => foliage.GrassDensitySetting = v, "0.00"));
        }
        // The game's `water reflection` (0 off .. 4 everything) and `reflection range` (x the haze distance), docs/formats/settings.md.
        if (g.Reflection is { } reflection)
        {
            sliders.Add(new Slider("Water reflection 0-4 (game)", 0, 4, () => reflection.Level, v => reflection.Level = (int)MathF.Round(v), "0"));
            sliders.Add(new Slider("Reflection range x (game 0.6)", 0.1f, 5, () => reflection.Range, v => reflection.Range = v, "0.00", Logarithmic: true));
        }
        sliders.Add(new Slider("Terrain LOD distance", 2, 16, () => r.LodDistance, v => r.LodDistance = v, "0.0"));
        // The game's `Shadow Range` slider goes 1000 to 9000; the viewer allows more (the cascades stretch over it).
        if (g.Shadow is { } shadow)
            sliders.Add(new Slider("Shadow distance (game 1k-9k)", KenshiShadows.MinRange, 20000, () => shadow.Settings.Range,
                v => shadow.Settings = shadow.Settings with { Range = MathF.Round(v / 100) * 100 }, "0", Logarithmic: true));
        // A viewer option, not the game's: 1 is the game's haze (docs/formats/sky.md).
        sliders.Add(new Slider("Haze strength (1 = game)", 0, 3, () => g.Sky.HazeStrength, v => g.Sky.HazeStrength = v, "0.00"));
        if (g.Post is { } post)
        {
            // Upscaling (docs/engine.md "Upscaling"): FSR and DLSS fall back to TAA where their library or backend is missing.
            var up = post.Options.Upscale;
            sliders.Add(new Slider(UpscalerSliders[0], 0, 3, () => (int)up.Kind, v =>
            {
                up.Kind = (UpscalerKind)(int)MathF.Round(v);
                if (up.Kind != UpscalerKind.Off) up.Preferred = up.Kind;   // what the Meitou anti-aliasing switch turns back on
            }, "0"));
            sliders.Add(new Slider(UpscalerSliders[1], 0.33f, 1, () => up.EffectiveScale, v => up.Scale = MathF.Round(v * 100) / 100, "0.00"));
            sliders.Add(new Slider(UpscalerSliders[2], 0, 1, () => up.Sharpness, v => up.Sharpness = v, "0.00"));
        }
        return new SettingsPanel(ui, "Settings   (Tab hides this)", sliders);
    }

    /// <summary>An hour as <c>HH:MM</c>.</summary>
    public static string TimeText(float hour)
    {
        int minutes = (int)MathF.Round(hour * 60) % (24 * 60);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    /// <summary>The swaying grass's own motion for the upscalers (MEITOU_GRASS_MOTION=0 turns it off, for comparisons).</summary>
    static readonly bool GrassMotion = Environment.GetEnvironmentVariable("MEITOU_GRASS_MOTION") != "0";
    /// <summary>Per-cascade and per-step CPU times in the stats strings (ShadowPass.CasterStats, FoliageRenderer's details); the viewer's screenshots and benchmarks turn it on.</summary>
    public static bool DetailedStats;
    static readonly string[] CascadeLabels = ["shadow c0", "shadow c1", "shadow c2", "shadow c3"];

    public static void Draw(IGl gl, Gpu gpu, WorldScene scene, WorldCamera camera, WorldRenderOptions render, int width, int height, float hour, float time, float fogDistance)
    {
        // Everything is drawn into the post-processing chain's HDR framebuffer (before the reflection pass, which restores whatever is bound).
        gpu.Post?.Begin(width, height);
        // The scene is drawn at the render size (smaller than the display with an upscaler), its projection jittered by the upscaler.
        int rw = gpu.Post?.RenderWidth ?? width, rh = gpu.Post?.RenderHeight ?? height;
        var jitter = gpu.Post?.JitterPixels ?? Vector2.Zero;
        var eye = camera.Eye;
        gpu.Streamer?.Update(gpu.Anchor ?? eye);
        StageClock.Lap(0);
        gpu.Objects?.Update(gpu.Anchor ?? eye);
        StageClock.Lap(1);
        gpu.Foliage?.Update(gpu.Anchor ?? eye);
        StageClock.Lap(2);
        float floor = gpu.Terrain.HeightAt(eye.X, eye.Z);
        if (render.Water) floor = Math.Max(floor, WorldWater.Height);
        camera.EyeClearance = Math.Max(eye.Y - floor, 1);
        // Above the game's camera heights the haze (and the water's glint) move to the viewer's own altitude-aware forms (SkyRenderer.AltitudeWeight).
        gpu.Sky.SetEye(eye, gpu.Terrain.HeightAt, render.Water ? WorldWater.Height : float.NegativeInfinity);
        // The atmosphere (SkyRenderer): sky tables, sun and ambient light for this sun and eye height. Thinner air higher up: the
        // haze takes longer to close in the higher the eye.
        var sun = scene.Clock.SunDirection(hour);
        var (colours, light) = gpu.Sky.Prepare(sun, eye.Y, fogDistance + 3 * Math.Max(eye.Y, 0));
        // The game's exposure (0.55 over the mean luminance, clamped to its band) goes with the game's sky and light; the simple sky keeps a plain scale.
        if (gpu.Post is { } post) post.AutoExposure = gpu.Sky.Physical ? (gpu.Sky.MinLuminance, gpu.Sky.MaxLuminance) : null;
        if (gpu.Post is { } upscaling) upscaling.WaterHeight = render.Water && gpu.Water is not null ? WorldWater.Height : null;
        if (gpu.Post is { } hazy) UpdateHeatHaze(gpu, hazy, sun.Y);
        // Far enough that the haze is complete before the far plane and the water quad (1.5 × view distance wide) end,
        // so a high eye sees the sea fade into the sky instead of a cut-off edge.
        camera.ViewDistance = Math.Max(camera.MinViewDistance, light.FogDistance / 0.7f);
        StageClock.Lap(3);
        if (gpu.Shadow is not null) { gpu.Shadow.Temporal = gpu.Post?.Temporal == true; DrawShadows(gpu, camera, render, light, rw, rh, sun.Y); }
        StageClock.Lap(12);
        // Water reflection: the mirrored scene into its own framebuffer (restores the bound one), before the main pass.
        bool reflecting = render.Water && render.Reflections && gpu.Water is not null && gpu.Reflection is { Level: > 0 };   // level 0: no pass, the water shows the sky colour
        if (gpu.Reflection is not null) { gpu.Reflection.RestoreFramebuffer = gpu.Post?.SceneFramebuffer; gpu.Reflection.MaxDistance = gpu.Sky.HazeDistance * gpu.Reflection.Range; }
        if (reflecting)
            gpu.Reflection!.Render(camera, rw, rh, gpu.Sky, colours, light, gpu.Terrain, render, gpu.Objects is null ? null : (vp, e, frustum) =>
            {
                var reflection = gpu.Reflection;
                var objects = gpu.Objects;
                // The level (docs/formats/settings.md): 3 adds buildings (and the map features, which are not told apart), 4 everything else (foliage).
                if (reflection.Level >= 3)
                {
                    float distance = objects.ObjectDistance;
                    float bias = objects.LodBias;
                    objects.LodBias = bias * reflection.ObjectLodBias;
                    objects.ObjectDistance = Math.Min(distance, gpu.Reflection.ObjectDistance);
                    objects.Draw(vp, e, frustum, render, light.SunDirection, light.FogColour, light.FogDistance, gpu.Terrain);
                    reflection.Lap(2);
                    StageClock.Sub("refl up to objects");
                    objects.ObjectDistance = distance;
                    objects.LodBias = bias;
                }
                if (reflection.Level >= 4) gpu.Foliage?.Draw(vp, e, frustum, render, light.SunDirection, light.FogColour, light.FogDistance, gpu.Terrain, grass: false, maxRange: reflection.FoliageDistance);
                reflection.Lap(3);
                StageClock.Sub("refl foliage");
                reflection.SceneStats = reflection.Level < 3 ? "no objects (level < 3)" : $"{objects.DrawnInstances} objects ({objects.DrawnTriangles:N0} triangles, {objects.DrawCalls} calls), {(reflection.Level >= 4 ? gpu.Foliage?.DrawnInstances ?? 0 : 0)} foliage meshes ({(reflection.Level >= 4 ? gpu.Foliage?.DrawCalls ?? 0 : 0)} calls)";
            });
        StageClock.Lap(4);
        gl.Viewport(0, 0, (uint)rw, (uint)rh);
        gl.ClearColor(light.FogColour.X, light.FogColour.Y, light.FogColour.Z, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        float aspect = width / (float)Math.Max(height, 1);
        var view = camera.View;
        // Rotation only: with the eye's world position in the matrix, the directions rebuilt from it lose float
        // precision far from the origin and the sky blurs.
        var rotation = view with { M41 = 0, M42 = 0, M43 = 0 };
        gpu.Sky.Draw(rotation * Jitter.Apply(camera.Projection(aspect, 1, 1000), jitter, rw, rh), colours);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);
        StageClock.Lap(5);
        gpu.Post?.SetCamera(eye, view, camera.FieldOfView, aspect);
        gpu.Terrain.BeginFrame();
        bool first = true, foliageDrawn = false;
        foreach (var (near, far) in camera.Slices())
        {
            bool nearSlice = near <= camera.Near;
            if (!first || nearSlice) gpu.Post?.BeginNearSlice(near, far); else gpu.Post?.BeginFarSlice(near, far);
            if (!first) gl.Clear(ClearBufferMask.DepthBufferBit);
            first = false;
            if (nearSlice) gpu.Post?.SetNearSlice(near, far, camera.FieldOfView, aspect);
            var viewProjection = view * Jitter.Apply(camera.Projection(aspect, near, far), jitter, rw, rh);
            var frustum = WorldCamera.FrustumPlanes(viewProjection);
            if (nearSlice && GrassMotion && gpu.Post is { Temporal: true } && gpu.Foliage is { } swaying)
            {
                gpu.Post.ObjectMotion ??= swaying.DrawGrassMotion;
                swaying.SetMotionCamera(viewProjection, view * camera.Projection(aspect, near, far), eye, frustum);
            }
            gpu.Terrain.Draw(viewProjection, eye, frustum, render, light);
            StageClock.Lap(6);
            if (render.Objects) gpu.Objects?.Draw(viewProjection, eye, frustum, render, light.SunDirection, light.FogColour, light.FogDistance, gpu.Terrain);
            StageClock.Lap(7);
            // Foliage in every depth slice (it reaches 32000+ units at the default x4), counted as one draw.
            gpu.Foliage?.Draw(viewProjection, eye, frustum, render, light.SunDirection, light.FogColour, light.FogDistance, gpu.Terrain, continuation: foliageDrawn);
            foliageDrawn = true;
            StageClock.Lap(8);
            if (render.Water) gpu.Water?.Draw(viewProjection, eye, light, colours, gpu.Terrain, time, camera.ViewDistance * 1.5f, reflecting ? gpu.Reflection : null);
            StageClock.Lap(9);
        }
        if (gpu.DebugShadows >= 2 && gpu.Shadow is not null && gpu.Post is not null) gpu.Shadow.CaptureDepth(gpu.Post.SceneFramebuffer, rw, rh);
        gpu.Post?.End(); // SSAO, upscaler, exposure, tone map, FXAA into gpu.Post.Target
        if (gpu.DebugShadows > 0 && gpu.Shadow is not null)
        {
            var (nearestNear, nearestFar) = camera.Slices().Last();
            gpu.Shadow.DrawDebug(gpu.DebugShadows, gpu.Post?.Target ?? 0, width, height, view, camera.Projection(aspect, nearestNear, nearestFar), eye);
        }
        StageClock.Lap(10);
    }

    /// <summary>
    /// The game's <c>heatHaze</c> (docs/formats/weather.md "Heat haze"): the target is the weather's <c>heat haze</c> (or
    /// <c>--heat-haze</c>) × strength × saturate(6 · sunY), with strength 1 (a forced weather has no scheduler's roll); the value
    /// moves towards it at 1/3 per second of real time (game speed 1), and jumps there for still pictures. The animation's
    /// <c>gameTime</c> is the caller's game clock when it has one, else real time at game speed 1 (held still for pictures).
    /// </summary>
    static void UpdateHeatHaze(Gpu gpu, PostProcess post, float sunY)
    {
        float dt = (float)Math.Min(gpu.HeatHazeClock.Elapsed.TotalSeconds, 0.25);
        bool first = !gpu.HeatHazeClock.IsRunning;
        gpu.HeatHazeClock.Restart();
        float field = post.Options.HeatHazeOverride ?? gpu.Sky.Weather.HeatHaze;
        gpu.HeatHazeTarget = HeatHaze.Target(field, 1, sunY);
        post.HeatHazeAmount = first || post.InstantAdaptation ? gpu.HeatHazeTarget : HeatHaze.Step(post.HeatHazeAmount, gpu.HeatHazeTarget, dt);
        if (!post.InstantAdaptation) gpu.HeatHazeHours += dt * HeatHaze.HoursPerSecond;
        post.HeatHazeHours = gpu.GameHours ?? gpu.HeatHazeHours;
        post.HeatHazeFarClip = gpu.Sky.HazeDistance;
    }

    /// <summary>
    /// The sun's shadow cascades (ShadowPass, docs/formats/shadows.md): fitted to this camera, their casters drawn by the renderers'
    /// depth-only paths (terrain, objects, foliage meshes; detail chosen from the camera's eye), before the reflection and the main pass.
    /// </summary>
    /// <param name="sunHeight">The real sun's height: the map is drawn along the lighting direction while the sun still has a colour (as the game).</param>
    public static void DrawShadows(Gpu gpu, WorldCamera camera, WorldRenderOptions render, WorldLighting light, int width, int height, float? sunHeight = null)
    {
        var shadow = gpu.Shadow!;
        var v = camera.View;
        var view = new ShadowView(camera.Eye, camera.Forward, Vector3.Normalize(new Vector3(v.M12, v.M22, v.M32)), camera.FieldOfView, width / (float)Math.Max(height, 1), camera.Near);
        int objects = 0, foliage = 0;
        gpu.Terrain.DepthTriangles = 0;
        var cascades = DetailedStats ? new System.Text.StringBuilder() : null;
        shadow.Render(view, light.SunDirection, gpu.Post?.SceneFramebuffer ?? 0, width, height, (cascade, worldToClip, planes, lodEye) =>
        {
            long t0 = Stopwatch.GetTimestamp();
            long tri = gpu.Terrain.DepthTriangles;
            gpu.Terrain.DrawDepth(worldToClip, lodEye, planes, render);
            StageClock.Sub("terrain");
            long t1 = Stopwatch.GetTimestamp();
            int oi = 0, oc = 0, fi = 0, fc = 0;
            if (render.Objects && gpu.Objects is { } o) { o.DrawDepth(worldToClip, lodEye, planes, render, gpu.Terrain); objects += o.DrawnInstances; (oi, oc) = (o.DrawnInstances, o.DrawCalls); }
            StageClock.Sub("objects");
            long t2 = Stopwatch.GetTimestamp();
            if (gpu.Foliage is { } f) { f.DrawDepth(worldToClip, lodEye, planes, render, gpu.Terrain, maxRange: shadow.Settings.Range * 1.2f); foliage += f.DrawnInstances; (fi, fc) = (f.DrawnInstances, f.DrawCalls); }
            StageClock.Phase(CascadeLabels[cascade.Index & 3]);
            long t3 = Stopwatch.GetTimestamp();
            double ms = 1000.0 / Stopwatch.Frequency;
            shadow.PhaseMs[0] += (t1 - t0) * ms;
            shadow.PhaseMs[1] += (t2 - t1) * ms;
            shadow.PhaseMs[2] += (t3 - t2) * ms;
            cascades?.Append($" [c{cascade.Index}: terrain {(gpu.Terrain.DepthTriangles - tri) / 1000}k tri {(t1 - t0) * ms:0.00} ms, objects {oi} in {oc} calls {(t2 - t1) * ms:0.00} ms, foliage {fi} in {fc} calls {(t3 - t2) * ms:0.00} ms ({gpu.Foliage?.DepthDetail})]");
        }, sunHeight);
        shadow.CasterStats = $"{gpu.Terrain.DepthTriangles:N0} terrain triangles, {objects} objects, {foliage} foliage meshes (over the cascades); cpu terrain {shadow.PhaseMs[0]:0.00}, objects {shadow.PhaseMs[1]:0.00}, foliage {shadow.PhaseMs[2]:0.00} ms;{cascades}"
            + (shadow.Meitou ? $"; meitou: {shadow.DescribeMeitou()}" : "");
    }
}
