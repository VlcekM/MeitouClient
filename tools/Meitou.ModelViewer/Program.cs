using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;
using Meitou.Data.Textures;
using Meitou.ModelViewer;
using Meitou.Rendering;
using Meitou.Rendering.Display;
using Meitou.Rendering.Gpu;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
args = SmokeTest.Strip(args);
if (args.Contains("--world")) return WorldApp.Run(args);
if (args.Contains("--character")) return CharacterApp.Run(args);
if (args.Contains("--impostor-preview") || args.Contains("--impostor-bake-all")) return ImpostorApp.Run(args);
return ViewerApp.Run(args);

sealed class ViewerOptions
{
    public string? Mesh;
    public string? Texture, NormalTexture, Skeleton, Animation, Screenshot;
    public float Time;
    public int Width = 1280, Height = 960;
    public float? Yaw, Pitch, Zoom;
    public int Material;
    public bool Info, NoFcs, Wireframe, ShowSkeleton, NoGrid, VertexColours;

    public const string Usage = """
        meitou-viewer <mesh> [options]     (or: meitou-viewer --world --help, meitou-viewer --character <record> --help)
          <mesh>                 bare file name (looked up via resources.cfg, then data/) or a path
          --texture <file>       diffuse texture to use instead of the resolved one
          --normal <file>        normal map to use with --texture
          --skeleton <file>      skeleton to use instead of the mesh's link
          --anim <name|index>    animation to play (default: none, binding pose)
          --time <seconds>       start time in the animation
          --material <n>         pick the n-th resolved material candidate (default 0)
          --screenshot <out.png> render offscreen to a PNG and exit
          --size <W>x<H>         window / screenshot size (default 1280x960)
          --yaw <deg> --pitch <deg> --zoom <factor>   camera (defaults 35, 20, 1)
          --wireframe --skeleton-lines --no-grid --vertex-colours
          --no-fcs               don't load game records (faster; no FCS textures)
          --info                 print mesh, skeleton and texture resolution, then exit
        Keys: mouse left drag orbit, right/middle drag pan, wheel zoom, F frame, W wireframe (solid / both / lines),
          T textures, N normal maps, C vertex colours, B backface culling, K skeleton, G grid,
          M / Shift+M next / previous material, Right / Left next / previous animation, Home binding pose,
          Space pause, Up / Down speed, P save screenshot, Esc quit.
        """;

    public static ViewerOptions? Parse(string[] args)
    {
        var o = new ViewerOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            float F() => float.Parse(Next(), CultureInfo.InvariantCulture);
            switch (a)
            {
                case "--texture": o.Texture = Next(); break;
                case "--normal": o.NormalTexture = Next(); break;
                case "--skeleton": o.Skeleton = Next(); break;
                case "--anim": o.Animation = Next(); break;
                case "--time": o.Time = F(); break;
                case "--material": o.Material = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--screenshot": o.Screenshot = Next(); break;
                case "--size":
                    var parts = Next().Split('x');
                    o.Width = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    o.Height = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    break;
                case "--yaw": o.Yaw = F(); break;
                case "--pitch": o.Pitch = F(); break;
                case "--zoom": o.Zoom = F(); break;
                case "--wireframe": o.Wireframe = true; break;
                case "--skeleton-lines": o.ShowSkeleton = true; break;
                case "--no-grid": o.NoGrid = true; break;
                case "--vertex-colours": o.VertexColours = true; break;
                case "--renderer": WorldOptions.IgnoreRenderer(Next()); break;
                case "--no-fcs": o.NoFcs = true; break;
                case "--info": o.Info = true; break;
                case "-h" or "--help": return null;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"unknown option {a}");
                    o.Mesh = a;
                    break;
            }
        }
        return o.Mesh is null ? null : o;
    }
}

/// <summary>Everything loaded for one mesh, independent of the window.</summary>
sealed class Scene
{
    public required string MeshPath;
    public required OgreMesh Mesh;
    public required Model Model;
    public Animator? Animator;
    public string? SkeletonPath;
    /// <summary>Material candidates per model part.</summary>
    public required List<List<SurfaceMaterial>> Candidates;
    public int MaterialChoice;
    public int AnimationIndex = -1;
    public float Time;
    public float Speed = 1;
    public bool Paused;

    public OgreAnimation? CurrentAnimation => Animator is { } a && AnimationIndex >= 0 && AnimationIndex < a.Animations.Count ? a.Animations[AnimationIndex] : null;

    public int CandidateCount => Candidates.Count == 0 ? 0 : Candidates.Max(c => c.Count);

    public SurfaceMaterial? MaterialFor(int part) =>
        Candidates[part].Count == 0 ? null : Candidates[part][Math.Min(MaterialChoice, Candidates[part].Count - 1)];

    public void Advance(float dt)
    {
        if (CurrentAnimation is not { } anim) return;
        if (!Paused) Time += dt * Speed;
        if (anim.Length > 0) Time = ((Time % anim.Length) + anim.Length) % anim.Length;
    }
}

static class ViewerApp
{
    public static int Run(string[] args)
    {
        ViewerOptions? options;
        try { options = ViewerOptions.Parse(args); }
        catch (Exception e) when (e is ArgumentException or FormatException or IndexOutOfRangeException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        if (options is null)
        {
            Console.WriteLine(ViewerOptions.Usage);
            return 2;
        }
        var install = GameInstall.Locate();
        if (install is null)
        {
            Console.Error.WriteLine($"Kenshi install not found: set {GameInstall.EnvironmentVariable} or create {GameInstall.LocalConfigFile}.");
            return 1;
        }

        var scene = Load(install, options, out var assets);
        if (scene is null) return 1;
        if (options.Info) return 0;
        return options.Screenshot is not null ? Screenshot(scene, assets, options) : Interactive(scene, assets, options);
    }

    static Scene? Load(GameInstall install, ViewerOptions o, out AssetLocator assets)
    {
        var watch = Stopwatch.StartNew();
        assets = new AssetLocator(install);
        string meshName = o.Mesh!.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase) ? o.Mesh : o.Mesh + ".mesh";
        var meshPath = assets.Find(meshName, out var how);
        if (meshPath is null)
        {
            Console.Error.WriteLine($"Mesh not found: {o.Mesh}");
            return null;
        }
        var mesh = OgreMeshReader.ReadFile(meshPath);
        Console.WriteLine($"mesh      {assets.Relative(meshPath)} ({how}), {mesh.Version}, {mesh.SubMeshes.Count} submeshes");

        GameDatabase? db = null;
        if (!o.NoFcs)
            db = GameDatabase.Load(LoadOrder.FromInstall(install));
        var library = OgreMaterialLibrary.LoadConfigured(install, out _);
        var resolver = new MaterialResolver(db, library, assets);
        var users = resolver.RecordsUsing(meshPath);
        foreach (var (record, field) in users)
            Console.WriteLine($"used by   {record.Type} '{record.Name}' ({record.StringId}) .{field}");

        // Skeleton: --skeleton; else for worn items (ARMOUR, CONTAINER, ATTACHMENT, LIMB_REPLACEMENT) the body skeleton,
        // which Kenshi shares with them by bone index; else the mesh's link, else "<mesh>.skeleton" beside it.
        string? bodySkeleton = users.Where(u => u.Record.Type is FcsRecordType.ARMOUR or FcsRecordType.CONTAINER
                or FcsRecordType.ATTACHMENT or FcsRecordType.LIMB_REPLACEMENT)
            .Select(u => u.Field.Contains("female", StringComparison.Ordinal) ? "female_skeleton.skeleton" : "male_skeleton.skeleton").FirstOrDefault();
        Animator? animator = null;
        string? skeletonPath = null;
        var skeletonCandidates = new List<string?> { o.Skeleton, mesh.SkeletonName is null ? null : bodySkeleton, mesh.SkeletonName, Path.ChangeExtension(Path.GetFileName(meshPath), ".skeleton") };
        if (mesh.SkeletonName is not null || o.Skeleton is not null)
            foreach (var name in skeletonCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (name is null) continue;
                var path = assets.Find(name.EndsWith(".skeleton", StringComparison.OrdinalIgnoreCase) ? name : name + ".skeleton");
                if (path is null)
                {
                    Console.WriteLine($"skeleton  {name}: not found");
                    continue;
                }
                try
                {
                    animator = new Animator(OgreSkeletonReader.ReadFile(path));
                    skeletonPath = path;
                    Console.WriteLine($"skeleton  {assets.Relative(path)}: {animator.BoneCount} bones, {animator.Animations.Count} animations");
                    break;
                }
                catch (Exception e) when (e is OgreFormatException or EndOfStreamException or IOException)
                {
                    Console.WriteLine($"skeleton  {name}: {e.Message}");
                }
            }
        if (mesh.SkeletonName is not null && animator is null)
            Console.WriteLine("skeleton  none usable: drawing the binding pose (armour binds to the character skeleton; try --skeleton male_skeleton or female_skeleton)");
        else if (animator is { Animations.Count: 0 } && o.Skeleton is null)
            Console.WriteLine("skeleton  has no animations (try --skeleton male_skeleton or female_skeleton)");

        var model = Model.Build(mesh, animator?.BoneCount);
        foreach (var w in model.Warnings) Console.WriteLine($"warning   {w}");

        var candidates = new List<List<SurfaceMaterial>>();
        foreach (var part in model.Parts)
        {
            var list = resolver.Candidates(meshPath, mesh.SubMeshes[part.SubMeshIndex]);
            if (o.Texture is not null)
                list.Insert(0, new SurfaceMaterial { Description = "--texture", Diffuse = o.Texture, Normal = o.NormalTexture });
            candidates.Add(list);
            Console.WriteLine($"submesh {part.SubMeshIndex,2} material '{part.MaterialName}', {part.Vertices.Length} vertices, {part.Indices.Length / 3} triangles"
                + (part.Skinned ? ", skinned" : "") + (part.HasTangents ? ", tangents" : "") + (part.HasColours ? ", colours" : ""));
            if (list.Count == 0) Console.WriteLine("           no textures found (untextured)");
            for (int i = 0; i < list.Count && i < 12; i++)
                Console.WriteLine($"           [{i}] {list[i].Description}: {list[i].Diffuse ?? "-"}" + (list[i].Normal is { } n ? $" + {n}" : ""));
            if (list.Count > 12) Console.WriteLine($"           ... {list.Count - 12} more");
        }

        var scene = new Scene
        {
            MeshPath = meshPath, Mesh = mesh, Model = model, Animator = animator, SkeletonPath = skeletonPath,
            Candidates = candidates, MaterialChoice = o.Material, Time = o.Time,
        };
        if (animator is not null)
        {
            if (animator.Animations.Count > 0)
                Console.WriteLine("animations " + string.Join(", ", animator.Animations.Select((a, i) => $"{i}:{a.Name}({a.Length:0.##}s)")));
            if (o.Animation is not null)
            {
                int index = int.TryParse(o.Animation, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
                    : animator.Animations.ToList().FindIndex(a => string.Equals(a.Name, o.Animation, StringComparison.OrdinalIgnoreCase));
                if (index < 0 || index >= animator.Animations.Count) Console.WriteLine($"animation '{o.Animation}' not found");
                else scene.AnimationIndex = index;
            }
            animator.Pose(scene.CurrentAnimation, scene.Time);
        }
        else if (o.Animation is not null) Console.WriteLine("no skeleton: --anim ignored");
        Console.WriteLine($"bounds    {model.Min} .. {model.Max} (loaded in {watch.ElapsedMilliseconds} ms)");
        return scene;
    }

    static WindowOptions WindowFor(ViewerOptions o) =>
        WindowOptions.Default with { Size = new Vector2D<int>(o.Width, o.Height), Title = "Meitou model viewer" };

    static (Camera, RenderOptions) Setup(Scene scene, ViewerOptions o)
    {
        var camera = new Camera();
        var (min, max) = scene.Animator is { } a && scene.CurrentAnimation is not null ? scene.Model.PosedBounds(a.SkinMatrices) : (scene.Model.Min, scene.Model.Max);
        camera.Frame((min + max) / 2, Math.Max((max - min).Length() / 2, 1e-3f), o.Yaw ?? 35, o.Pitch ?? 20);
        if (o.Zoom is { } z) camera.Distance /= z;
        var render = new RenderOptions { Wireframe = o.Wireframe ? 1 : 0, Skeleton = o.ShowSkeleton, Grid = !o.NoGrid, ForceVertexColours = o.VertexColours };
        return (camera, render);
    }

    static void ApplyMaterials(Scene scene, Renderer renderer)
    {
        for (int i = 0; i < scene.Model.Parts.Count; i++) renderer.SetMaterial(i, scene.MaterialFor(i));
        foreach (var m in renderer.Messages) Console.WriteLine($"warning   {m}");
        renderer.Messages.Clear();
    }

    static unsafe int Screenshot(Scene scene, AssetLocator assets, ViewerOptions o)
    {
        using var display = new VulkanDisplay(null, vsync: false);
        var gl = display.Gl;
        using var renderer = new Renderer(gl, assets);
        renderer.Upload(scene.Model);
        ApplyMaterials(scene, renderer);
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
        renderer.Draw(camera, w, h, render, scene.Animator?.SkinMatrices, scene.Animator);

        uint fbo = gl.GenFramebuffer(), colour = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, colour);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, (uint)w, (uint)h);
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
        gl.FramebufferRenderbuffer(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, colour);
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, msFbo);
        gl.BlitFramebuffer(0, 0, w, h, 0, 0, w, h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
        SavePng(gl, o.Screenshot!, w, h);
        Console.WriteLine($"saved     {Path.GetFullPath(o.Screenshot!)}");
        return 0;
    }

    internal static void SavePng(IGl gl, string path, int w, int h) => FramebufferCapture.SavePng(gl, path, w, h);

    static int Interactive(Scene scene, AssetLocator assets, ViewerOptions o)
    {
        using var display = new VulkanDisplay(WindowFor(o), vsync: true);
        var window = display.Window!;
        var gl = display.Gl;
        Renderer? renderer = null;
        Camera camera = null!;
        RenderOptions render = null!;
        bool screenshotRequested = false;
        Vector2? lastMouse = null;
        MouseButton? dragging = null;

        void Title()
        {
            var anim = scene.CurrentAnimation;
            string animText = anim is null ? (scene.Animator is null ? "" : " | bind pose") :
                $" | anim {scene.AnimationIndex + 1}/{scene.Animator!.Animations.Count} {anim.Name} {scene.Time:0.00}/{anim.Length:0.00}s x{scene.Speed:0.##}{(scene.Paused ? " paused" : "")}";
            string mat = scene.CandidateCount > 1 ? $" | material {Math.Min(scene.MaterialChoice, scene.CandidateCount - 1) + 1}/{scene.CandidateCount}" : "";
            window.Title = $"{Path.GetFileName(scene.MeshPath)}{animText}{mat}";
        }

        {
            renderer = new Renderer(gl, assets);
            renderer.Upload(scene.Model);
            ApplyMaterials(scene, renderer);
            (camera, render) = Setup(scene, o);
            gl.Enable(EnableCap.Multisample);
            var input = window.CreateInput();
            foreach (var kb in input.Keyboards) kb.KeyDown += (k, key, _) => OnKey(k, key);
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
                        else camera.Pan(d.X, d.Y, window.FramebufferSize.Y);
                    }
                    lastMouse = p;
                };
                mouse.Scroll += (_, wheel) => camera.Zoom(wheel.Y);
            }
            Console.WriteLine(ViewerOptions.Usage[ViewerOptions.Usage.IndexOf("Keys:", StringComparison.Ordinal)..]);
            Title();
        }

        void OnKey(IKeyboard keyboard, Key key)
        {
            bool shift = keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight);
            var animator = scene.Animator;
            switch (key)
            {
                case Key.Escape: window.Close(); break;
                case Key.F: camera.Frame(scene.Model.Center, scene.Model.Radius, camera.Yaw * 180 / MathF.PI, camera.Pitch * 180 / MathF.PI); break;
                case Key.W: render.Wireframe = (render.Wireframe + 1) % 3; break;
                case Key.T: render.Textures = !render.Textures; break;
                case Key.N: render.NormalMaps = !render.NormalMaps; break;
                case Key.C: render.VertexColours = !render.VertexColours; break;
                case Key.B: render.BackfaceCulling = !render.BackfaceCulling; break;
                case Key.K: render.Skeleton = !render.Skeleton; break;
                case Key.G: render.Grid = !render.Grid; break;
                case Key.P: screenshotRequested = true; break;
                case Key.M when scene.CandidateCount > 1:
                    scene.MaterialChoice = (Math.Min(scene.MaterialChoice, scene.CandidateCount - 1) + (shift ? -1 : 1) + scene.CandidateCount) % scene.CandidateCount;
                    ApplyMaterials(scene, renderer!);
                    for (int i = 0; i < scene.Model.Parts.Count; i++)
                        Console.WriteLine($"submesh {scene.Model.Parts[i].SubMeshIndex}: {scene.MaterialFor(i)?.Description ?? "untextured"}");
                    break;
                case Key.Right or Key.Left when animator is { Animations.Count: > 0 }:
                    int count = animator.Animations.Count;
                    scene.AnimationIndex = scene.AnimationIndex < 0 ? (key == Key.Right ? 0 : count - 1)
                        : (scene.AnimationIndex + (key == Key.Right ? 1 : -1) + count) % count;
                    scene.Time = 0;
                    Console.WriteLine($"animation {scene.AnimationIndex}: {scene.CurrentAnimation!.Name} ({scene.CurrentAnimation.Length:0.##} s)");
                    break;
                case Key.Home: scene.AnimationIndex = -1; break;
                case Key.Space: scene.Paused = !scene.Paused; break;
                case Key.Up: scene.Speed *= 1.5f; break;
                case Key.Down: scene.Speed /= 1.5f; break;
            }
            Title();
        }

        double titleTimer = 0;
        window.Update += dt =>
        {
            SmokeTest.Check(window);
            scene.Advance((float)dt);
            titleTimer += dt;
            if (titleTimer > 0.1 && scene.CurrentAnimation is not null) { Title(); titleTimer = 0; }
        };
        window.Render += _ =>
        {
            if (renderer is null) return;
            scene.Animator?.Pose(scene.CurrentAnimation, scene.Time);
            var size = window.FramebufferSize;
            if (!display.BeginFrame(size.X, size.Y)) return;
            renderer.Draw(camera, size.X, size.Y, render, scene.Animator?.SkinMatrices, scene.Animator);
            display.Present();
            SmokeTest.Frame();
            if (screenshotRequested)
            {
                screenshotRequested = false;
                // Into C:\Temp (the user's screenshot folder), never the working directory (which may be the repo).
                var file = Path.Combine(Directory.CreateDirectory(@"C:\Temp").FullName, $"meitou-viewer-{Path.GetFileNameWithoutExtension(scene.MeshPath)}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
                gl.ReadBuffer(ReadBufferMode.Back);
                SavePng(gl, file, size.X, size.Y);
                Console.WriteLine($"saved {Path.GetFullPath(file)}");
            }
        };
        window.Closing += () => renderer?.Dispose();
        window.Run();
        return 0;
    }
}

/// <summary><c>--quit-after &lt;s&gt;</c> (any interactive mode): the window closes itself after that many seconds and the frames drawn are printed (an unattended smoke test).</summary>
static class SmokeTest
{
    static double? seconds;
    static long frames;
    static readonly Stopwatch clock = new();

    /// <summary>Takes <c>--quit-after</c> out of <paramref name="args"/>.</summary>
    public static string[] Strip(string[] args)
    {
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--quit-after" && i + 1 < args.Length) seconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
            else rest.Add(args[i]);
        }
        return rest.ToArray();
    }

    /// <summary>Counts a drawn frame.</summary>
    public static void Frame()
    {
        if (frames++ == 0) clock.Start();   // the time starts at the first frame, after loading
    }

    /// <summary>Closes <paramref name="window"/> once the time is up (call from the update).</summary>
    public static void Check(IWindow window)
    {
        if (seconds is not { } s || frames == 0 || clock.Elapsed.TotalSeconds < s) return;
        double t = clock.Elapsed.TotalSeconds;
        Console.WriteLine($"smoke     {frames} frames in {t:0.0} s: {frames / t:0} fps on average");
        seconds = null;
        window.Close();
    }
}
