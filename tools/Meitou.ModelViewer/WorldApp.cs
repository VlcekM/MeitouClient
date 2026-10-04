using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace Meitou.ModelViewer;

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
    public int LayerSize = 512;
    public int Debug;
    public float ObjectDistance = 12000;
    public float DistantZones = ObjectRanges.MaxDistantTownRangeZones;
    public bool NoDistant;
    public bool NoFoliage;
    public float Hour = 13;
    public float ViewDistance = 450000, FogDistance = 250000, MaterialDistance = 30000;
    public bool NoWater, NoStream, NoReflections, SimpleSky, ShowKeys;
    public string? Weather;
    public float? Clouds;
    public PostOptions Post = PostOptions.Create("kenshi");
    public double? CameraX, CameraZ, FlyToX, FlyToZ;

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
          --no-textures            height-tinted terrain without biome textures (faster start)
          --no-objects             skip buildings and map features
          --no-foliage             no trees, bushes, rocks or grass (F toggles)
          --object-distance <u>    draw placed objects at full detail up to this distance (default 12000)
          --distant-range <zones>  distant towns (and buildings' distant meshes) up to this many zones (default 10, the game's setting maximum; its default is 6)
          --no-distant             no distant towns: objects beyond --object-distance are simply not drawn
          --layer-size <n>         terrain layer texture size (default 512)
          --debug <n>              1 blend-map slot weights, 2 layer weights (R cliff, G slope, B grass)
          --time <hour>            time of day for the sun (default 13; sunrise and sunset from the CONSTANTS record)
          --no-water               leave out the water
          --no-reflections         the water reflects only the sky colour, not the mirrored scene (R toggles)
          --simple-sky             the old colour-model sky and squared-distance fog instead of the atmosphere (B toggles)
          --weather <name>         a WEATHER record's sky colour, fog and clouds (default "Default": clear, no fog, no clouds)   --clouds <0..1> cloud coverage
          --camera-at <x>,<z>      start the camera here instead of at the loaded point (as if flown there)
          --no-stream              keep the terrain detail around the start point instead of following the camera
          --show-keys              start with the key list overlay open (toggle with ?)
          --fly-to <x>,<z>         with --screenshot: fly there first (streaming test, reports frame times), then take the picture
          --view-distance <u>      furthest terrain drawn (default 450000: the whole world)
          --fog <u>                distance where the haze is complete (default 250000)
          --material-distance <u>  beyond it the terrain shows the biomes' ground colour (default 30000, as the game)
          --wireframe --info
          --post <kenshi|off>   post-processing preset (default kenshi), before the options below: HDR scene (4x MSAA), SSAO, bloom, tone map
          --ssao / --no-ssao, --bloom / --no-bloom, --vignette, --grade, --dither (or --no-...)   --msaa <1|2|4|8>
          --tonemap <clamp|shoulder|aces>  --exposure <x>  --bloom-intensity <x>  --bloom-threshold <x>  --ssao-radius <units>  --ssao-strength <x>
        Keys: left drag orbit, right drag look around, wheel zoom, W/A/S/D free fly along the view, Q/E down/up (Shift faster, Ctrl slower),
          T textures, N normal maps, O objects, F foliage, X wireframe, V debug view,
          G water, R water reflections, B simple sky, , / . time of day -/+ 1 hour, H print camera, P save screenshot, ? key list, Esc quit.
          F1 post off, F2 kenshi; F4 SSAO, F5 bloom, F6 tone map, F8 vignette, F9 grading, M MSAA, - / = exposure.
        """;

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
                case "--simple-sky": o.SimpleSky = true; break;
                case "--weather": o.Weather = Next(); break;
                case "--clouds": o.Clouds = F(); break;
                case "--no-stream": o.NoStream = true; break;
                case "--show-keys": o.ShowKeys = true; break;
                case var post when o.Post.TryParse(post, Next): break;
                case "--camera-at": (o.CameraX, o.CameraZ) = Pair(); break;
                case "--fly-to": (o.FlyToX, o.FlyToZ) = Pair(); break;
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
    /// <summary>Whole-world heights every <see cref="WorldApp.CoarseStep"/>-th sample, (CoarseSize)² raw values.</summary>
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

static class WorldApp
{
    /// <summary>Heightmap step of the whole-world height grid behind the loaded region (2049² samples, 144 units apart).</summary>
    public const int CoarseStep = 8;

    public static int Run(string[] args)
    {
        WorldOptions? options;
        try { options = WorldOptions.Parse(args); }
        catch (Exception e) when (e is ArgumentException or FormatException or IndexOutOfRangeException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        if (options is null)
        {
            Console.WriteLine(WorldOptions.Usage);
            return 2;
        }
        var install = GameInstall.Locate();
        if (install is null)
        {
            Console.Error.WriteLine($"Kenshi install not found: set {GameInstall.EnvironmentVariable} or create {GameInstall.LocalConfigFile}.");
            return 1;
        }
        using var scene = Load(install, options);
        if (scene is null) return 1;
        if (options.Info) return 0;
        var assets = new AssetLocator(install);
        return options.Screenshot is not null ? Screenshot(install, scene, assets, options) : Interactive(install, scene, assets, options);
    }

    static WorldScene? Load(GameInstall install, WorldOptions o)
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

    static IWindow CreateWindow(WorldOptions o, bool visible) =>
        Window.Create(WindowOptions.Default with
        {
            Size = new Vector2D<int>(o.Width, o.Height),
            Title = "Meitou world viewer",
            IsVisible = visible,
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
            Samples = 0, // the scene is multisampled in PostProcess's own framebuffer
            VSync = true,
            PreferredDepthBufferBits = 24,
        });

    static (WorldCamera, WorldRenderOptions) Setup(WorldScene scene, WorldOptions o)
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

    sealed class Gpu : IDisposable
    {
        public required TerrainRenderer Terrain;
        public required SkyRenderer Sky;
        public WaterRenderer? Water;
        public ReflectionPass? Reflection;
        public PostProcess? Post;
        public WorldObjectRenderer? Objects;
        public FoliageRenderer? Foliage;
        public TerrainStreamer? Streamer;
        /// <summary>With <c>--no-stream</c>: where the streamer is kept, instead of at the eye.</summary>
        public Vector3? Anchor;
        public void Dispose()
        {
            Streamer?.Dispose();
            Foliage?.Dispose();
            Objects?.Dispose();
            Water?.Dispose();
            Reflection?.Dispose();
            Post?.Dispose();
            Sky.Dispose();
            Terrain.Dispose();
        }
    }

    static Gpu CreateGpu(GL gl, GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o, bool interactive)
    {
        var watch = Stopwatch.StartNew();
        var terrain = new TerrainRenderer(gl, scene.Coarse, scene.CoarseSize, scene.Window, new WorldRenderOptions().LodDistance);
        Console.WriteLine($"uploaded  terrain heights: {terrain.LevelCount} LOD levels, finest {terrain.FinestSpacing:0.#} units ({watch.ElapsedMilliseconds} ms)");
        TerrainTextures? textures = null;
        if (!o.NoTextures && scene.Database is not null)
        {
            textures = TerrainTextures.Create(gl, install, scene.Database, assets, o.LayerSize);
            foreach (var m in textures.Messages.Take(20)) Console.WriteLine($"warning   {m}");
            Console.WriteLine($"biomes    {textures.TotalBiomes} in the world, {textures.TotalPairs} texture pairs, {textures.Capacity} slots of {o.LayerSize}² ({watch.ElapsedMilliseconds} ms)");
            terrain.SetTextures(textures);
        }
        var gpu = new Gpu { Terrain = terrain, Sky = new SkyRenderer(gl, assets) { Physical = !o.SimpleSky, CloudCoverage = o.Clouds }, Post = new PostProcess(gl, o.Post) };
        if (scene.Database is { } skyDb)
        {
            gpu.Sky.NightDarkness = SkyWeather.NightDarkness(skyDb);
            gpu.Sky.Weather = SkyWeather.Find(skyDb, o.Weather) ?? throw new ArgumentException($"no weather named '{o.Weather}'; known: {string.Join(", ", SkyWeather.Names(skyDb).Distinct().Take(12))} ...");
        }
        gpu.Streamer = new TerrainStreamer(install, terrain, textures, scene.Window.Step) { MaterialDistance = o.MaterialDistance };
        if (o.NoStream) gpu.Anchor = scene.Focus;
        if (!o.NoWater && scene.Database is not null)
        {
            var messages = new List<string>();
            gpu.Water = WaterRenderer.Create(gl, install, scene.Database, assets, gpu.Sky, messages);
            gpu.Reflection = new ReflectionPass(gl);
            foreach (var m in messages) Console.WriteLine($"warning   {m}");
            Console.WriteLine($"water     at height {WorldWater.Height} ({watch.ElapsedMilliseconds} ms)");
        }
        if (scene.Objects is not null)
        {
            gpu.Objects = new WorldObjectRenderer(gl, assets, scene.Objects) { ObjectDistance = o.ObjectDistance, DistantRange = o.DistantZones * WorldLayout.ZoneSize, NoDistant = o.NoDistant, LoadBudget = interactive ? 8 : 0 };
            Console.WriteLine($"objects   GPU ready ({watch.ElapsedMilliseconds} ms)");
        }
        if (!o.NoFoliage && scene.Database is not null)
        {
            gpu.Foliage = new FoliageRenderer(gl, install, scene.Database, scene.Objects?.Levels ?? WorldLevelData.Load(install), assets);
            Console.WriteLine($"foliage   catalog and shaders ready ({gpu.Foliage.LoadMs:0} ms)");
        }
        return gpu;
    }

    /// <summary>Draws a frame: the sky, then the far depth slice (terrain, water), then the near one (terrain, objects, water).</summary>
    static void Draw(GL gl, Gpu gpu, WorldScene scene, WorldCamera camera, WorldRenderOptions render, int width, int height, float hour, float time, float fogDistance)
    {
        // Everything is drawn into the post-processing chain's HDR framebuffer (before the reflection pass, which restores whatever is bound).
        gpu.Post?.Begin(width, height);
        var eye = camera.Eye;
        gpu.Streamer?.Update(gpu.Anchor ?? eye);
        gpu.Objects?.Update(gpu.Anchor ?? eye);
        gpu.Foliage?.Update(gpu.Anchor ?? eye);
        float floor = gpu.Terrain.HeightAt(eye.X, eye.Z);
        if (render.Water) floor = Math.Max(floor, WorldWater.Height);
        camera.EyeClearance = Math.Max(eye.Y - floor, 1);
        // The atmosphere (SkyRenderer): sky tables, sun and ambient light for this sun and eye height. Thinner air higher up: the
        // haze takes longer to close in the higher the eye.
        var (colours, light) = gpu.Sky.Prepare(scene.Clock.SunDirection(hour), eye.Y, fogDistance + 3 * Math.Max(eye.Y, 0));
        // Far enough that the haze is complete before the far plane and the water quad (1.5 × view distance wide) end,
        // so a high eye sees the sea fade into the sky instead of a cut-off edge.
        camera.ViewDistance = Math.Max(camera.MinViewDistance, light.FogDistance / 0.7f);
        // Water reflection: the mirrored scene into its own framebuffer (restores the bound one), before the main pass.
        bool reflecting = render.Water && render.Reflections && gpu.Water is not null && gpu.Reflection is not null;
        if (reflecting)
            gpu.Reflection!.Render(camera, width, height, gpu.Sky, colours, light, gpu.Terrain, render, gpu.Objects is null ? null : (vp, e, frustum) =>
            {
                var objects = gpu.Objects;
                float distance = objects.ObjectDistance;
                objects.ObjectDistance = Math.Min(distance, gpu.Reflection.ObjectDistance);
                objects.Draw(vp, e, frustum, render, light.SunDirection, light.FogColour, light.FogDistance, gpu.Terrain);
                objects.ObjectDistance = distance;
                gpu.Foliage?.Draw(vp, e, frustum, render, light.SunDirection, light.FogColour, light.FogDistance, gpu.Terrain, grass: false);
            });
        gl.Viewport(0, 0, (uint)width, (uint)height);
        gl.ClearColor(light.FogColour.X, light.FogColour.Y, light.FogColour.Z, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        float aspect = width / (float)Math.Max(height, 1);
        var view = camera.View;
        // Rotation only: with the eye's world position in the matrix, the directions rebuilt from it lose float
        // precision far from the origin and the sky blurs.
        var rotation = view with { M41 = 0, M42 = 0, M43 = 0 };
        gpu.Sky.Draw(rotation * camera.Projection(aspect, 1, 1000), colours);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);
        gpu.Terrain.BeginFrame();
        bool first = true;
        foreach (var (near, far) in camera.Slices())
        {
            if (!first) gl.Clear(ClearBufferMask.DepthBufferBit);
            first = false;
            bool nearSlice = near <= camera.Near;
            if (nearSlice) gpu.Post?.SetNearSlice(near, far, camera.FieldOfView, aspect);
            var viewProjection = view * camera.Projection(aspect, near, far);
            var frustum = WorldCamera.FrustumPlanes(viewProjection);
            gpu.Terrain.Draw(viewProjection, eye, frustum, render, light);
            if (render.Objects) gpu.Objects?.Draw(viewProjection, eye, frustum, render, light.SunDirection, light.FogColour, light.FogDistance, gpu.Terrain);
            if (nearSlice) gpu.Foliage?.Draw(viewProjection, eye, frustum, render, light.SunDirection, light.FogColour, light.FogDistance, gpu.Terrain);
            if (render.Water) gpu.Water?.Draw(viewProjection, eye, light, colours, gpu.Terrain, time, camera.ViewDistance * 1.5f, reflecting ? gpu.Reflection : null);
        }
        gpu.Post?.End(); // resolve, SSAO, bloom, tone map into gpu.Post.Target
    }

    static unsafe int Screenshot(GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o)
    {
        using var window = CreateWindow(o, visible: false);
        window.Initialize();
        using var gl = window.CreateOpenGL();
        using var gpu = CreateGpu(gl, install, scene, assets, o, interactive: false);
        var (camera, render) = Setup(scene, o);
        if (gpu.Streamer is { } streamer)
        {
            var streamWatch = Stopwatch.StartNew();
            streamer.Settle(gpu.Anchor ?? camera.Eye);
            Console.WriteLine($"streamed  {streamer.Describe()} ({streamWatch.ElapsedMilliseconds} ms)");
        }
        if (gpu.Objects is { } objectRenderer)
        {
            var objectWatch = Stopwatch.StartNew();
            objectRenderer.Settle(gpu.Anchor ?? camera.Eye);
            Console.WriteLine($"objects   {objectRenderer.Describe()} ({objectWatch.ElapsedMilliseconds} ms)");
        }
        if (gpu.Foliage is { } foliageRenderer)
        {
            var foliageWatch = Stopwatch.StartNew();
            foliageRenderer.Settle(gpu.Anchor ?? camera.Eye);
            Console.WriteLine($"foliage   {foliageRenderer.Describe()} ({foliageWatch.ElapsedMilliseconds} ms)");
        }

        // Offscreen: the post-processing chain (HDR scene, resolve, effects) ends in a plain RGBA8 framebuffer that is read back.
        int w = o.Width, h = o.Height;
        uint fbo = gl.GenFramebuffer(), colour = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, colour);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, (uint)w, (uint)h);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, colour);
        if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
        {
            Console.Error.WriteLine("Offscreen framebuffer incomplete.");
            return 1;
        }
        gpu.Post!.Target = fbo;
        Console.WriteLine($"post      {o.Post.Describe()}");
        if (o.FlyToX is { } flyX && o.FlyToZ is { } flyZ)
        {
            // Streaming test: fly the camera to the point at 3x the interactive fast speed, 60 frames per second of
            // wall time, and report the render thread's cost per frame (draw + uploads) while data streams in.
            var start = camera.Target;
            var end = new Vector3((float)flyX, 0, (float)flyZ);
            float length = Vector2.Distance(new Vector2(start.X, start.Z), new Vector2(end.X, end.Z));
            float perFrame = 3f * 0.8f * Math.Max(camera.Distance, 50) / 60f;
            int frames = Math.Max((int)(length / perFrame), 1);
            var times = new List<double>();
            var frameWatch = new Stopwatch();
            for (int i = 1; i <= frames; i++)
            {
                float t = i / (float)frames;
                var p = Vector3.Lerp(start, end, t);
                camera.Target = new Vector3(p.X, gpu.Terrain.HeightAt(p.X, p.Z), p.Z);
                frameWatch.Restart();
                Draw(gl, gpu, scene, camera, render, w, h, o.Hour, 0, o.FogDistance);
                gl.Finish();
                times.Add(frameWatch.Elapsed.TotalMilliseconds);
                if (frameWatch.Elapsed.TotalMilliseconds > 15 && Environment.GetEnvironmentVariable("MEITOU_STREAM_LOG") == "1")
                    Console.WriteLine($"slow frame {i}: {frameWatch.Elapsed.TotalMilliseconds:0.0} ms, streamer update {gpu.Streamer?.LastUpdateMs:0.0} ms [{gpu.Streamer?.LastSteps}], gen2 GCs {GC.CollectionCount(2)}, gen0 {GC.CollectionCount(0)}");
                int sleep = 16 - (int)frameWatch.ElapsedMilliseconds;
                if (sleep > 0) Thread.Sleep(sleep);
            }
            double first = times[0], worstAfterFirst = times.Count > 1 ? times.Skip(1).Max() : 0;
            times.Sort();
            Console.WriteLine($"flight    {frames} frames over {length:0} units: median {times[times.Count / 2]:0.0} ms, 99th {times[(int)(times.Count * 0.99)]:0.0} ms, worst {times[^1]:0.0} ms (first frame {first:0.0} ms, worst after it {worstAfterFirst:0.0} ms), " +
                $"{times.Count(x => x > 10)} frames over 10 ms; streaming {(gpu.Streamer?.Idle == true ? "idle" : $"{gpu.Streamer?.Pending} pending")}");
            gpu.Objects?.Settle(camera.Eye);
            gpu.Foliage?.Settle(camera.Eye);
        }

        var drawWatch = Stopwatch.StartNew();
        Draw(gl, gpu, scene, camera, render, w, h, o.Hour, 0, o.FogDistance);
        gl.Finish();
        Console.WriteLine($"drawn in {drawWatch.ElapsedMilliseconds} ms: {gpu.Terrain.DrawnChunks} chunks, {gpu.Terrain.DrawnTriangles:N0} terrain triangles" +
            (gpu.Objects is { } ob ? $", {ob.DrawnInstances} objects ({ob.DrawnTriangles:N0} triangles)" : ""));
        // Steady-state frame time (the first frame includes shader and texture warm-up).
        drawWatch.Restart();
        const int timedFrames = 10;
        gpu.Post?.Flush();
        gpu.Post?.TakeCosts(); // drop the warm-up frames
        for (int i = 0; i < timedFrames; i++) Draw(gl, gpu, scene, camera, render, w, h, o.Hour, 0, o.FogDistance);
        gl.Finish();
        Console.WriteLine($"frame     {drawWatch.Elapsed.TotalMilliseconds / timedFrames:0.0} ms on average over {timedFrames} more frames");
        if (gpu.Objects is { } objectStats) Console.WriteLine($"objects   draw cpu {objectStats.LastDrawCpuMs:0.00} ms, {objectStats.DrawCalls} draw calls, {objectStats.DrawnInstances} instances, {objectStats.DrawnTriangles:N0} triangles");
        if (gpu.Foliage is { } foliageStats) { foliageStats.PollTimers(wait: true); Console.WriteLine($"foliage   update {foliageStats.LastUpdateMs:0.00} ms, draw cpu {foliageStats.LastDrawCpuMs:0.00} ms, gpu {foliageStats.GpuMs:0.00} ms, {foliageStats.DrawCalls} draw calls, {foliageStats.DrawnInstances} meshes, {foliageStats.DrawnBlades:N0} grass blades"); }
        if (gpu.Reflection is { Valid: true } reflection)
        {
            reflection.Poll(wait: true);
            Console.WriteLine($"reflect   {reflection.Width}x{reflection.Height}: cpu {reflection.CpuMs:0.00} ms, gpu {reflection.GpuMs:0.00} ms, {reflection.DrawnChunks} chunks, {reflection.DrawnTriangles:N0} terrain triangles");
        }

        gpu.Sky.Poll(wait: true);
        Console.WriteLine($"sky       {gpu.Sky.DescribeCost()}");
        gpu.Post!.Flush();
        Console.WriteLine($"post cost gpu ms/frame: {gpu.Post.DescribeCosts()}");
        if (o.ShowKeys && DebugOverlay.TryCreate(gl) is { } keysOverlay)
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            keysOverlay.Visible = true;
            keysOverlay.Draw(w, h, "Keys   (? hides this)", DebugOverlay.KeyItems(WorldOptions.Usage));
            keysOverlay.Dispose();
        }
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
        ViewerApp.SavePng(gl, o.Screenshot!, w, h);
        Console.WriteLine($"saved     {Path.GetFullPath(o.Screenshot!)}");
        if (Environment.GetEnvironmentVariable("MEITOU_SKY_BENCH") == "1")
        {
            // Sky cost, back to back after the picture is saved (the passes draw over the framebuffer): MEITOU_SKY_BENCH=1.
            var rotation = camera.View with { M41 = 0, M42 = 0, M43 = 0 };
            var (pass, table) = gpu.Sky.Benchmark(rotation * camera.Projection(w / (float)Math.Max(h, 1), 1, 1000));
            Console.WriteLine($"skybench  sky pass {pass:0.000} ms, sky-view table rebuild {table:0.000} ms ({w}x{h})");
        }
        return 0;
    }

    static int Interactive(GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o)
    {
        using var window = CreateWindow(o, visible: true);
        GL? gl = null;
        Gpu? gpu = null;
        WorldCamera camera = null!;
        WorldRenderOptions render = null!;
        IKeyboard? keyboard = null;
        bool screenshotRequested = false;
        float hour = o.Hour;
        var clock = Stopwatch.StartNew();
        Vector2? lastMouse = null;
        MouseButton? dragging = null;
        DebugOverlay? overlay = null;
        bool keysToggleRequested = false;
        var keyItems = DebugOverlay.KeyItems(WorldOptions.Usage);

        window.Load += () =>
        {
            gl = window.CreateOpenGL();
            gpu = CreateGpu(gl, install, scene, assets, o, interactive: true);
            overlay = DebugOverlay.TryCreate(gl);
            if (overlay is null) Console.WriteLine("keys      no monospace system font found: the ? key list is unavailable");
            if (overlay is not null) overlay.Visible = o.ShowKeys;
            (camera, render) = Setup(scene, o);
            gl.Enable(EnableCap.Multisample);
            var input = window.CreateInput();
            keyboard = input.Keyboards.FirstOrDefault();
            foreach (var kb in input.Keyboards) kb.KeyDown += (_, key, _) => OnKey(key);
            // '?' by character, so it works on any keyboard layout.
            // "?" by character (any keyboard layout) or as Shift+/ (US position); both can arrive for one press, so
            // they only request the toggle and the next update applies it once.
            foreach (var kb in input.Keyboards) kb.KeyChar += (_, c) => { if (c == '?') keysToggleRequested = true; };
            foreach (var mouse in input.Mice)
            {
                mouse.MouseDown += (_, b) => { dragging = b; lastMouse = null; };
                mouse.MouseUp += (_, _) => dragging = null;
                mouse.MouseMove += (_, p) =>
                {
                    if (dragging is { } b && lastMouse is { } last)
                    {
                        var d = p - last;
                        if (b == MouseButton.Left) camera.Orbit(d.X, d.Y);
                        else camera.Look(d.X, d.Y);
                    }
                    lastMouse = p;
                };
                mouse.Scroll += (_, wheel) => camera.Zoom(wheel.Y);
            }
            Console.WriteLine(WorldOptions.Usage[WorldOptions.Usage.IndexOf("Keys:", StringComparison.Ordinal)..]);
        };

        void Preset(string name) { o.Post.CopyFrom(PostOptions.Create(name)); PostStatus(); }
        void PostStatus() => Console.WriteLine($"post      {o.Post.Describe()}");

        // The state a key controls, for the key list ("on", "off", a mode or a value); null for actions.
        string? KeyState(string key)
        {
            static string OnOff(bool on) => on ? "on" : "off";
            string Active(string preset) => o.Post.Preset == preset ? "active" : "";
            return key switch
            {
                "T" => OnOff(render.Textures),
                "N" => OnOff(render.NormalMaps),
                "O" => OnOff(render.Objects),
                "F" => gpu?.Foliage is { } foliage ? OnOff(foliage.Enabled) : "n/a",
                "X" => render.Wireframe switch { 0 => "solid", 1 => "solid + lines", _ => "lines" },
                "V" => render.Debug switch { 0 => "off", 1 => "blend weights", 2 => "layer weights", _ => "plain shading" },
                "G" => OnOff(render.Water),
                "R" => OnOff(render.Reflections),
                "B" => gpu is null ? null : gpu.Sky.Physical ? "atmosphere" : "simple",
                "," => $"{hour:00}:00",
                "F1" => Active("off"),
                "F2" => Active("kenshi"),
                "F4" => OnOff(o.Post.Ssao),
                "F5" => OnOff(o.Post.Bloom),
                "F6" => o.Post.ToneMap.ToString().ToLowerInvariant(),
                "F8" => OnOff(o.Post.Vignette),
                "F9" => OnOff(o.Post.Grade),
                "M" => o.Post.Msaa <= 1 ? "off" : $"{o.Post.Msaa}x",
                "-" => $"{o.Post.Exposure:0.00}",
                "?" => "on",
                _ => null,
            };
        }

        void OnKey(Key key)
        {
            switch (key)
            {
                case Key.Escape: window.Close(); break;
                case Key.F1: Preset("off"); break;
                case Key.F2: Preset("kenshi"); break;
                case Key.F4: o.Post.Ssao = !o.Post.Ssao; PostStatus(); break;
                case Key.F5: o.Post.Bloom = !o.Post.Bloom; PostStatus(); break;
                case Key.F6: o.Post.ToneMap = (ToneMapOperator)(((int)o.Post.ToneMap + 1) % 3); PostStatus(); break;
                case Key.F8: o.Post.Vignette = !o.Post.Vignette; PostStatus(); break;
                case Key.F9: o.Post.Grade = !o.Post.Grade; PostStatus(); break;
                case Key.M: o.Post.Msaa = o.Post.Msaa switch { 1 => 2, 2 => 4, 4 => 8, _ => 1 }; PostStatus(); break;
                case Key.Minus: o.Post.Exposure = MathF.Max(o.Post.Exposure / 1.1f, 0.05f); PostStatus(); break;
                case Key.Equal: o.Post.Exposure = MathF.Min(o.Post.Exposure * 1.1f, 20f); PostStatus(); break;
                case Key.T: render.Textures = !render.Textures; break;
                case Key.N: render.NormalMaps = !render.NormalMaps; break;
                case Key.O: render.Objects = !render.Objects; break;
                case Key.F when gpu?.Foliage is { } foliage: foliage.Enabled = !foliage.Enabled; Console.WriteLine(foliage.Enabled ? "foliage   on" : "foliage   off"); break;
                case Key.X: render.Wireframe = (render.Wireframe + 1) % 3; break;
                case Key.V: render.Debug = (render.Debug + 1) % 4; break;
                case Key.G: render.Water = !render.Water; break;
                case Key.R: render.Reflections = !render.Reflections; break;
                case Key.B when gpu is not null: gpu.Sky.Physical = !gpu.Sky.Physical; Console.WriteLine(gpu.Sky.Physical ? "sky      atmosphere" : "sky      simple colour model"); break;
                case Key.Comma: hour = (hour + 23) % 24; Console.WriteLine($"time {hour:0}:00"); break;
                case Key.Period: hour = (hour + 1) % 24; Console.WriteLine($"time {hour:0}:00"); break;
                case Key.H:
                    Console.WriteLine($"camera target {camera.Target.X:0}, {camera.Target.Y:0}, {camera.Target.Z:0} (zone {WorldLayout.ZoneOf(camera.Target.X, camera.Target.Z)}), " +
                        $"yaw {camera.Yaw * 180 / MathF.PI:0}, pitch {camera.Pitch * 180 / MathF.PI:0}, distance {camera.Distance:0}; " +
                        $"--at {camera.Target.X:0},{camera.Target.Z:0} --yaw {camera.Yaw * 180 / MathF.PI:0} --pitch {camera.Pitch * 180 / MathF.PI:0} --distance {camera.Distance:0}");
                    break;
                case Key.P: screenshotRequested = true; break;
                case Key.Slash when keyboard is not null && (keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight)): keysToggleRequested = true; break;
            }
        }

        double titleTimer = 0, cpuMs = 0, gpuMs = 0;
        int frames = 0, gpuSamples = 0, queryIndex = 0;
        uint[] queries = [];
        bool[] queryPending = [];
        var frameWatch = new Stopwatch();
        window.Update += dt =>
        {
            if (keysToggleRequested)
            {
                keysToggleRequested = false;
                if (overlay is not null) { overlay.Visible = !overlay.Visible; Console.WriteLine($"keys      list {(overlay.Visible ? "shown" : "hidden")}"); }
            }
        };
        window.Update += dt =>
        {
            if (keyboard is null || camera is null) return;
            bool Down(Key k) => keyboard.IsKeyPressed(k);
            // Free camera: W/S along the view direction, A/D sideways, Q/E world down/up; speed grows with the height
            // above the ground (Shift ×4, Ctrl ×0.25).
            float speed = (float)dt * Math.Max(camera.EyeClearance, 30) * 1.5f *
                (Down(Key.ShiftLeft) || Down(Key.ShiftRight) ? 4f : 1f) * (Down(Key.ControlLeft) || Down(Key.ControlRight) ? 0.25f : 1f);
            float f = (Down(Key.W) ? 1 : 0) - (Down(Key.S) ? 1 : 0);
            float r = (Down(Key.D) ? 1 : 0) - (Down(Key.A) ? 1 : 0);
            float u = (Down(Key.E) ? 1 : 0) - (Down(Key.Q) ? 1 : 0);
            if (f != 0 || r != 0 || u != 0) camera.FlyFree(f * speed, r * speed, u * speed);
            titleTimer += dt;
            if (titleTimer > 0.25 && gpu is not null)
            {
                var t = camera.Eye;
                // fps is capped by vsync; cpu is the time to record a frame, gpu the time the GPU spent on it (timer
                // queries), so they show the real cost under the cap.
                string gpuText = gpuSamples > 0 ? $"{gpuMs / gpuSamples:0.00}" : "-";
                window.Title = $"Meitou world | {frames / titleTimer:0} fps (vsync) | cpu {cpuMs / Math.Max(frames, 1):0.00} ms, gpu {gpuText} ms" +
                    (gpu.Reflection is { Valid: true } refl && render.Reflections ? $" (reflection cpu {refl.CpuMs:0.00}, gpu {refl.GpuMs:0.00})" : "") + " | " +
                    $"{t.X:0}, {t.Z:0} zone {WorldLayout.ZoneOf(t.X, t.Z)} | {gpu.Terrain.DrawnChunks} chunks, {gpu.Terrain.DrawnTriangles / 1000}k tris" +
                    (gpu.Objects is { } ob && render.Objects ? $" | {ob.DrawnInstances} objects, {ob.DrawCalls} calls, draw cpu {ob.LastDrawCpuMs:0.00} ms" + (ob.Pending > 0 ? $", loading {ob.Pending}" : "") : "") +
                    (gpu.Foliage is { Enabled: true } fo ? $" | foliage {fo.DrawnInstances} + {fo.DrawnBlades / 1000}k grass, {fo.DrawCalls} calls, cpu {fo.LastDrawCpuMs:0.00} gpu {fo.GpuMs:0.00} ms" + (fo.Pending > 0 ? $", loading {fo.Pending}" : "") : "") +
                    (gpu.Streamer is { Pending: > 0 } st ? $" | loading {st.Pending}" : "");
                if (gpu.Post is { } post) window.Title += $" | post gpu ms: {post.DescribeCosts()}";
                gpu.Sky.Poll();
                window.Title += gpu.Sky.Physical ? $" | sky cpu {gpu.Sky.PrepareMs:0.00} ms, gpu {gpu.Sky.GpuMs:0.00} ms" : " | simple sky";
                titleTimer = 0;
                frames = 0;
                cpuMs = gpuMs = 0;
                gpuSamples = 0;
            }
        };
        window.Render += _ =>
        {
            if (gpu is null || gl is null) return;
            var size = window.FramebufferSize;
            if (queries.Length == 0)
            {
                queries = new uint[4];
                queryPending = new bool[4];
                for (int i = 0; i < queries.Length; i++) queries[i] = gl.GenQuery();
            }
            // Collect finished GPU timings from earlier frames without waiting for them.
            for (int i = 0; i < queries.Length; i++)
            {
                if (!queryPending[i]) continue;
                gl.GetQueryObject(queries[i], QueryObjectParameterName.ResultAvailable, out int available);
                if (available == 0) continue;
                gl.GetQueryObject(queries[i], QueryObjectParameterName.Result, out ulong nanoseconds);
                gpuMs += nanoseconds / 1e6;
                gpuSamples++;
                queryPending[i] = false;
            }
            uint query = queries[queryIndex];
            bool timing = !queryPending[queryIndex];
            if (timing) gl.BeginQuery(QueryTarget.TimeElapsed, query);
            frameWatch.Restart();
            Draw(gl, gpu, scene, camera, render, size.X, size.Y, hour, (float)clock.Elapsed.TotalSeconds / 600f, o.FogDistance);
            cpuMs += frameWatch.Elapsed.TotalMilliseconds;
            if (timing)
            {
                gl.EndQuery(QueryTarget.TimeElapsed);
                queryPending[queryIndex] = true;
            }
            queryIndex = (queryIndex + 1) % queries.Length;
            frames++;
            if (screenshotRequested)
            {
                screenshotRequested = false;
                // Into the temp folder, never the working directory (which may be the repo).
                var file = Path.Combine(Path.GetTempPath(), $"meitou-world-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
                gl.ReadBuffer(ReadBufferMode.Back);
                ViewerApp.SavePng(gl, file, size.X, size.Y);
                Console.WriteLine($"saved {Path.GetFullPath(file)}");
            }
            // After the screenshot, so saved pictures never show it.
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            if (overlay is { Visible: true })
            {
                // Each item with its current state, aligned in a column.
                int width = keyItems.Max(i => i.Length) + 2;
                var lines = keyItems.Select(item => KeyState(item.Split(' ')[0]) is { } state ? item.PadRight(width) + state : item).ToList();
                overlay.Draw(size.X, size.Y, "Keys   (? hides this)", lines);
            }
        };
        window.Closing += () => { overlay?.Dispose(); gpu?.Dispose(); };
        window.Run();
        return 0;
    }
}
