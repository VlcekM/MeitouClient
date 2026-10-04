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
    public float Hour = 13;
    public float ViewDistance = 450000, FogDistance = 250000, MaterialDistance = 30000;
    public bool NoWater;

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
          --object-distance <u>    draw placed objects up to this distance (default 12000)
          --layer-size <n>         terrain layer texture size (default 512)
          --debug <n>              1 blend-map slot weights, 2 layer weights (R cliff, G slope, B grass)
          --time <hour>            time of day for the sun (default 13; sunrise and sunset from the CONSTANTS record)
          --no-water               leave out the water
          --view-distance <u>      furthest terrain drawn (default 450000: the whole world)
          --fog <u>                distance where the haze is complete (default 250000)
          --material-distance <u>  beyond it the terrain shows the biomes' ground colour (default 30000, as the game)
          --wireframe --info
        Keys: left drag orbit, right drag look around, wheel zoom, W/A/S/D fly, Q/E down/up (Shift: faster),
          T textures, N normal maps, O objects, X wireframe, V debug view, [ / ] terrain LOD distance,
          G water, , / . time of day -/+ 1 hour, H print camera, P save screenshot, Esc quit.
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
                case "--object-distance": o.ObjectDistance = F(); break;
                case "--layer-size": o.LayerSize = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--debug": o.Debug = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--wireframe": o.Wireframe = true; break;
                case "--info": o.Info = true; break;
                case "--time": o.Hour = F(); break;
                case "--no-water": o.NoWater = true; break;
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
            Samples = visible ? 4 : 0,
            VSync = true,
            PreferredDepthBufferBits = 24,
        });

    static (WorldCamera, WorldRenderOptions) Setup(WorldScene scene, WorldOptions o)
    {
        float radius = o.Radius * WorldLayout.ZoneSize;
        var camera = new WorldCamera
        {
            Target = scene.Focus,
            Yaw = (o.Yaw ?? 30) * MathF.PI / 180,
            Pitch = (o.Pitch ?? 35) * MathF.PI / 180,
            Distance = o.Distance ?? radius,
            ViewDistance = o.ViewDistance,
            SplitDistance = Math.Max(20000, o.ObjectDistance * 1.1f),
        };
        var render = new WorldRenderOptions { Textures = !o.NoTextures, Objects = !o.NoObjects, Water = !o.NoWater, Wireframe = o.Wireframe ? 1 : 0, Debug = o.Debug, MaterialDistance = o.MaterialDistance };
        return (camera, render);
    }

    sealed class Gpu : IDisposable
    {
        public required TerrainRenderer Terrain;
        public required SkyRenderer Sky;
        public WaterRenderer? Water;
        public WorldObjectRenderer? Objects;
        public void Dispose()
        {
            Objects?.Dispose();
            Water?.Dispose();
            Sky.Dispose();
            Terrain.Dispose();
        }
    }

    static Gpu CreateGpu(GL gl, GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o, bool interactive)
    {
        var watch = Stopwatch.StartNew();
        var terrain = new TerrainRenderer(gl, scene.Coarse, scene.CoarseSize, scene.Window, new WorldRenderOptions().LodDistance);
        Console.WriteLine($"uploaded  terrain heights: {terrain.LevelCount} LOD levels, finest {terrain.FinestSpacing:0.#} units ({watch.ElapsedMilliseconds} ms)");
        if (!o.NoTextures && scene.Database is not null)
        {
            var textures = TerrainTextures.Build(gl, install, scene.Database, assets, scene.X0, scene.Z0, scene.X1, scene.Z1, o.LayerSize);
            foreach (var m in textures.Messages.Take(20)) Console.WriteLine($"warning   {m}");
            Console.WriteLine($"biomes    {textures.Biomes.Count}: {string.Join(", ", textures.Biomes.Select(b => b.Name.Trim()))} ({watch.ElapsedMilliseconds} ms)");
            terrain.SetTextures(textures);
        }
        var gpu = new Gpu { Terrain = terrain, Sky = new SkyRenderer(gl) };
        if (!o.NoWater && scene.Database is not null)
        {
            var messages = new List<string>();
            gpu.Water = WaterRenderer.Create(gl, install, scene.Database, assets, gpu.Sky, messages);
            foreach (var m in messages) Console.WriteLine($"warning   {m}");
            Console.WriteLine($"water     at height {WorldWater.Height} ({watch.ElapsedMilliseconds} ms)");
        }
        if (scene.Objects is not null)
        {
            gpu.Objects = new WorldObjectRenderer(gl, assets, scene.Objects) { ObjectDistance = o.ObjectDistance, LoadBudget = interactive ? 8 : 0 };
            Console.WriteLine($"objects   GPU ready ({watch.ElapsedMilliseconds} ms)");
        }
        return gpu;
    }

    /// <summary>Draws a frame: the sky, then the far depth slice (terrain, water), then the near one (terrain, objects, water).</summary>
    static void Draw(GL gl, Gpu gpu, WorldScene scene, WorldCamera camera, WorldRenderOptions render, int width, int height, float hour, float time, float fogDistance)
    {
        var eye = camera.Eye;
        float floor = scene.GroundAt(eye.X, eye.Z);
        if (render.Water) floor = Math.Max(floor, WorldWater.Height);
        camera.EyeClearance = Math.Max(eye.Y - floor, 1);
        var colours = SkyColours.For(scene.Clock.SunDirection(hour));
        // Thinner air higher up: the haze takes longer to close in the higher the eye.
        var light = colours.Lighting(fogDistance + 3 * Math.Max(eye.Y, 0));
        gl.Viewport(0, 0, (uint)width, (uint)height);
        gl.ClearColor(light.FogColour.X, light.FogColour.Y, light.FogColour.Z, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        float aspect = width / (float)Math.Max(height, 1);
        var view = camera.View;
        gpu.Sky.Draw(view * camera.Projection(aspect, 1, 1000), colours);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);
        gpu.Terrain.BeginFrame();
        bool first = true;
        foreach (var (near, far) in camera.Slices())
        {
            if (!first) gl.Clear(ClearBufferMask.DepthBufferBit);
            first = false;
            bool nearSlice = near <= camera.Near;
            var viewProjection = view * camera.Projection(aspect, near, far);
            var frustum = WorldCamera.FrustumPlanes(viewProjection);
            gpu.Terrain.Draw(viewProjection, eye, frustum, render, light);
            if (nearSlice && render.Objects) gpu.Objects?.Draw(viewProjection, eye, frustum, render, light.SunDirection, light.FogColour, light.FogDistance, gpu.Terrain);
            if (render.Water) gpu.Water?.Draw(viewProjection, eye, light, colours, gpu.Terrain, time);
        }
    }

    static unsafe int Screenshot(GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o)
    {
        using var window = CreateWindow(o, visible: false);
        window.Initialize();
        using var gl = window.CreateOpenGL();
        using var gpu = CreateGpu(gl, install, scene, assets, o, interactive: false);
        var (camera, render) = Setup(scene, o);

        // Offscreen: 4x multisampled framebuffer, resolved into a plain one and read back.
        int w = o.Width, h = o.Height;
        uint msFbo = gl.GenFramebuffer(), msColour = gl.GenRenderbuffer(), msDepth = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, msColour);
        gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, 4, InternalFormat.Rgba8, (uint)w, (uint)h);
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, msDepth);
        gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, 4, InternalFormat.DepthComponent24, (uint)w, (uint)h);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, msFbo);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, msColour);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, msDepth);
        if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
        {
            Console.Error.WriteLine("Offscreen framebuffer incomplete.");
            return 1;
        }
        var drawWatch = Stopwatch.StartNew();
        Draw(gl, gpu, scene, camera, render, w, h, o.Hour, 0, o.FogDistance);
        gl.Finish();
        Console.WriteLine($"drawn in {drawWatch.ElapsedMilliseconds} ms: {gpu.Terrain.DrawnChunks} chunks, {gpu.Terrain.DrawnTriangles:N0} terrain triangles" +
            (gpu.Objects is { } ob ? $", {ob.DrawnInstances} objects ({ob.DrawnTriangles:N0} triangles)" : ""));
        // Steady-state frame time (the first frame includes shader and texture warm-up).
        drawWatch.Restart();
        const int timedFrames = 10;
        for (int i = 0; i < timedFrames; i++) Draw(gl, gpu, scene, camera, render, w, h, o.Hour, 0, o.FogDistance);
        gl.Finish();
        Console.WriteLine($"frame     {drawWatch.Elapsed.TotalMilliseconds / timedFrames:0.0} ms on average over {timedFrames} more frames");

        uint fbo = gl.GenFramebuffer(), colour = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, colour);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, (uint)w, (uint)h);
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
        gl.FramebufferRenderbuffer(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, colour);
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, msFbo);
        gl.BlitFramebuffer(0, 0, w, h, 0, 0, w, h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
        ViewerApp.SavePng(gl, o.Screenshot!, w, h);
        Console.WriteLine($"saved     {Path.GetFullPath(o.Screenshot!)}");
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

        window.Load += () =>
        {
            gl = window.CreateOpenGL();
            gpu = CreateGpu(gl, install, scene, assets, o, interactive: true);
            (camera, render) = Setup(scene, o);
            gl.Enable(EnableCap.Multisample);
            var input = window.CreateInput();
            keyboard = input.Keyboards.FirstOrDefault();
            foreach (var kb in input.Keyboards) kb.KeyDown += (_, key, _) => OnKey(key);
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

        void OnKey(Key key)
        {
            switch (key)
            {
                case Key.Escape: window.Close(); break;
                case Key.T: render.Textures = !render.Textures; break;
                case Key.N: render.NormalMaps = !render.NormalMaps; break;
                case Key.O: render.Objects = !render.Objects; break;
                case Key.X: render.Wireframe = (render.Wireframe + 1) % 3; break;
                case Key.V: render.Debug = (render.Debug + 1) % 4; break;
                case Key.G: render.Water = !render.Water; break;
                case Key.Comma: hour = (hour + 23) % 24; Console.WriteLine($"time {hour:0}:00"); break;
                case Key.Period: hour = (hour + 1) % 24; Console.WriteLine($"time {hour:0}:00"); break;
                case Key.LeftBracket: render.LodDistance = Math.Max(render.LodDistance / 1.25f, 2f); Console.WriteLine($"LOD distance {render.LodDistance:0.##}"); break;
                case Key.RightBracket: render.LodDistance = Math.Min(render.LodDistance * 1.25f, 16f); Console.WriteLine($"LOD distance {render.LodDistance:0.##}"); break;
                case Key.H:
                    Console.WriteLine($"camera target {camera.Target.X:0}, {camera.Target.Y:0}, {camera.Target.Z:0} (zone {WorldLayout.ZoneOf(camera.Target.X, camera.Target.Z)}), " +
                        $"yaw {camera.Yaw * 180 / MathF.PI:0}, pitch {camera.Pitch * 180 / MathF.PI:0}, distance {camera.Distance:0}; " +
                        $"--at {camera.Target.X:0},{camera.Target.Z:0} --yaw {camera.Yaw * 180 / MathF.PI:0} --pitch {camera.Pitch * 180 / MathF.PI:0} --distance {camera.Distance:0}");
                    break;
                case Key.P: screenshotRequested = true; break;
            }
        }

        double titleTimer = 0;
        int frames = 0;
        window.Update += dt =>
        {
            if (keyboard is null || camera is null) return;
            bool Down(Key k) => keyboard.IsKeyPressed(k);
            float speed = (float)dt * Math.Max(camera.Distance, 50) * (Down(Key.ShiftLeft) || Down(Key.ShiftRight) ? 3f : 0.8f);
            float f = (Down(Key.W) ? 1 : 0) - (Down(Key.S) ? 1 : 0);
            float r = (Down(Key.D) ? 1 : 0) - (Down(Key.A) ? 1 : 0);
            float u = (Down(Key.E) ? 1 : 0) - (Down(Key.Q) ? 1 : 0);
            if (f != 0 || r != 0 || u != 0) camera.Fly(f * speed, r * speed, u * speed);
            titleTimer += dt;
            if (titleTimer > 0.25 && gpu is not null)
            {
                var t = camera.Target;
                window.Title = $"Meitou world | {frames / titleTimer:0} fps, {titleTimer * 1000 / Math.Max(frames, 1):0.00} ms | {t.X:0}, {t.Z:0} zone {WorldLayout.ZoneOf(t.X, t.Z)} | {gpu.Terrain.DrawnChunks} chunks, {gpu.Terrain.DrawnTriangles / 1000}k tris" +
                    (gpu.Objects is { } ob && render.Objects ? $" | {ob.DrawnInstances} objects" : "");
                titleTimer = 0;
                frames = 0;
            }
        };
        window.Render += _ =>
        {
            if (gpu is null || gl is null) return;
            var size = window.FramebufferSize;
            Draw(gl, gpu, scene, camera, render, size.X, size.Y, hour, (float)clock.Elapsed.TotalSeconds / 600f, o.FogDistance);
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
        };
        window.Closing += () => gpu?.Dispose();
        window.Run();
        return 0;
    }
}
