using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Ogre;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace Meitou.ModelViewer;

/// <summary>Command line of <c>meitou-viewer --character</c>.</summary>
public sealed class CharacterViewOptions
{
    public string? Character;
    public bool? Female;
    public List<string> Equip = [];
    public bool Naked, Drawn, NoMorphs, NoSkinTone, NoPostures, BindPose, Info, ShowSkeleton, NoGrid, Wireframe;
    public int? Seed;
    /// <summary>--faction: the FACTION a generated character belongs to (its "hairstyles" limit hair and beards).</summary>
    public string? Faction;
    public List<(string Name, float Weight)> Animations = [];
    public float Time;
    public string? Screenshot;
    public int Width = 1280, Height = 960;
    public float? Yaw, Pitch, Zoom;
    /// <summary>--shape name=value overrides (CharacterShape.Override), --no-shape, --lod n.</summary>
    public List<string> Shape = [];
    public bool NoShape;
    public int? Lod;

    public const string Usage = """
        meitou-viewer --character <CHARACTER or RACE record name or string id> [options]
          --female / --male      force the gender (default: the body file, else CHARACTER "female chance" >= 50)
          --equip <record>       also wear / carry an item (ARMOUR, ATTACHMENT, WEAPON...; repeatable; replaces the same armour slot)
          --naked                leave out the CHARACTER's clothing and weapons
          --drawn                first weapon drawn: bare blade in the right hand, sheath left at the hip
          --seed <n>             roll the character as the game spawns one (gender, random face/body/colours without a
                                 body file, clothing, quality, weapons, manufacturer); same seed, same character
          --faction <record>     FACTION of a --seed character (default its own "faction"; e.g. "Dust Bandits")
          --anim <name>[:weight] play an animation (ANIMATION record name or Ogre name, e.g. "walk lower" --anim "walk upper":0.8);
                                 repeatable, blended; default: the body file's idle stance
          --bind-pose            no default animation
          --time <s>             start time of the animations
          --no-morphs            don't bake the body file's face poses into the body mesh
          --no-skin-tone         ignore the body file's skin tone
          --no-postures          don't hold the posture, neck and shoulder pose libraries at the body file's sliders
          --shape <name=value>   override a body-shape slider (Kenshi units, 100 = neutral; values <= 3 are fractions:
                                 height=0.8 = 80) or muscle=, starve=, legratio=, missing=larm,rarm,lleg,rleg, hidestump=0-3;
                                 repeatable or ';'-separated
          --no-shape             no body-shape sliders (bone sizes all 1)
          --lod <n>              force mesh LOD level n (default: Kenshi's distance_sphere rule per mesh; L key cycles)
          --screenshot <png> --size WxH --yaw/--pitch/--zoom --skeleton-lines --no-grid --wireframe --info
        Keys: as the mesh viewer (orbit, pan, zoom, F, W, T, N, B, K, G, P, Space, Up/Down speed), plus
          Tab / 1-9 select an animation layer, Right / Left change its animation, + / - its weight, Delete remove it,
          Insert add a layer (copy of the selected one), Home binding pose, L cycle the LOD level (auto, 0, 1...).
        """;

    public static CharacterViewOptions? Parse(string[] args)
    {
        var o = new CharacterViewOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            float F() => float.Parse(Next(), CultureInfo.InvariantCulture);
            switch (a)
            {
                case "--character": o.Character = Next(); break;
                case "--female": o.Female = true; break;
                case "--male": o.Female = false; break;
                case "--equip": o.Equip.Add(Next()); break;
                case "--naked": o.Naked = true; break;
                case "--drawn": o.Drawn = true; break;
                case "--seed": o.Seed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--faction": o.Faction = Next(); break;
                case "--anim":
                {
                    var spec = Next();
                    int colon = spec.LastIndexOf(':');
                    if (colon > 0 && float.TryParse(spec[(colon + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var w))
                        o.Animations.Add((spec[..colon], w));
                    else o.Animations.Add((spec, 1));
                    break;
                }
                case "--bind-pose": o.BindPose = true; break;
                case "--time": o.Time = F(); break;
                case "--no-morphs": o.NoMorphs = true; break;
                case "--no-skin-tone": o.NoSkinTone = true; break;
                case "--no-postures": o.NoPostures = true; break;
                case "--shape": o.Shape.Add(Next()); break;
                case "--no-shape": o.NoShape = true; break;
                case "--lod": o.Lod = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--screenshot": o.Screenshot = Next(); break;
                case "--size":
                    var parts = Next().Split('x');
                    o.Width = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    o.Height = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    break;
                case "--yaw": o.Yaw = F(); break;
                case "--pitch": o.Pitch = F(); break;
                case "--zoom": o.Zoom = F(); break;
                case "--skeleton-lines": o.ShowSkeleton = true; break;
                case "--no-grid": o.NoGrid = true; break;
                case "--wireframe": o.Wireframe = true; break;
                case "--info": o.Info = true; break;
                case "-h" or "--help": return null;
                default: throw new ArgumentException($"unknown option {a}");
            }
        }
        return o.Character is null ? null : o;
    }
}

/// <summary><c>meitou-viewer --character</c>: assembles a character from FCS records and shows it (docs/viewer.md, "Characters").</summary>
static class CharacterApp
{
    public static int Run(string[] args)
    {
        CharacterViewOptions? o;
        try { o = CharacterViewOptions.Parse(args); }
        catch (Exception e) when (e is ArgumentException or FormatException or IndexOutOfRangeException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        if (o is null)
        {
            Console.WriteLine(CharacterViewOptions.Usage);
            return 2;
        }
        var install = GameInstall.Locate();
        if (install is null)
        {
            Console.Error.WriteLine($"Kenshi install not found: set {GameInstall.EnvironmentVariable} or create {GameInstall.LocalConfigFile}.");
            return 1;
        }
        var watch = Stopwatch.StartNew();
        var assets = new AssetLocator(install);
        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
        var resolver = new MaterialResolver(db, OgreMaterialLibrary.LoadConfigured(install, out _), assets);
        var scene = CharacterScene.Load(install, db, assets, resolver, o);
        if (scene is null) return 1;
        Console.WriteLine($"loaded in {watch.ElapsedMilliseconds} ms");
        if (o.Info) return 0;
        return o.Screenshot is not null ? Screenshot(scene, assets, o) : Interactive(scene, assets, o);
    }

    static IWindow CreateWindow(CharacterViewOptions o, bool visible) =>
        Window.Create(WindowOptions.Default with
        {
            Size = new Vector2D<int>(o.Width, o.Height),
            Title = "Meitou character viewer",
            IsVisible = visible,
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
            Samples = visible ? 4 : 0,
            VSync = true,
        });

    static (Vector3 Min, Vector3 Max) Bounds(CharacterScene scene)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var part in scene.Parts)
        {
            var (a, b) = part.Model.PosedBounds(scene.Animator.SkinMatrices);
            if (part.Mode == Meitou.Data.Characters.AttachMode.Bone)
            {
                var t = part.Transform(scene.Animator);
                (a, b) = (Vector3.Transform(a, t), Vector3.Transform(b, t));
                (a, b) = (Vector3.Min(a, b), Vector3.Max(a, b));
            }
            min = Vector3.Min(min, a);
            max = Vector3.Max(max, b);
        }
        return (min, max);
    }

    static (Camera, RenderOptions) Setup(CharacterScene scene, CharacterViewOptions o)
    {
        var camera = new Camera();
        var (min, max) = Bounds(scene);
        camera.Frame((min + max) / 2, Math.Max((max - min).Length() / 2, 1e-3f), o.Yaw ?? 35, o.Pitch ?? 15);
        if (o.Zoom is { } z) camera.Distance /= z;
        return (camera, new RenderOptions { Skeleton = o.ShowSkeleton, Grid = !o.NoGrid, Wireframe = o.Wireframe ? 1 : 0 });
    }

    static int Screenshot(CharacterScene scene, AssetLocator assets, CharacterViewOptions o)
    {
        using var window = CreateWindow(o, visible: false);
        window.Initialize();
        using var gl = window.CreateOpenGL();
        using var basis = new Renderer(gl, assets);
        using var renderer = new CharacterRenderer(gl, basis, scene);
        var (camera, render) = Setup(scene, o);
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
        renderer.Draw(camera, w, h, render, scene);
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

    static int Interactive(CharacterScene scene, AssetLocator assets, CharacterViewOptions o)
    {
        using var window = CreateWindow(o, visible: true);
        GL? gl = null;
        Renderer? basis = null;
        CharacterRenderer? renderer = null;
        Camera camera = null!;
        RenderOptions render = null!;
        bool screenshotRequested = false;
        Vector2? lastMouse = null;
        MouseButton? dragging = null;
        string name = scene.Appearance.Character?.Name ?? scene.Appearance.Race.Name;

        void Title()
        {
            string layers = scene.Layers.Count == 0 ? "bind pose" : string.Join(" + ", scene.Layers.Select((l, i) =>
                $"{(i == scene.Selected ? "[" : "")}{l.Label} {l.Weight:0.##}{(i == scene.Selected ? "]" : "")}"));
            window.Title = $"{name} | {layers} | x{scene.Speed:0.##}{(scene.Paused ? " paused" : "")}" +
                $" | LOD {(scene.ForcedLod is { } lod ? lod.ToString(CultureInfo.InvariantCulture) : "auto")} [{string.Join(",", scene.Parts.Select(p => p.LodLevel))}]";
        }

        void ChangeAnimation(int step)
        {
            var anims = scene.Animator.Animations;
            if (anims.Count == 0) return;
            if (scene.Layers.Count == 0)
            {
                scene.Layers.Add(scene.Layer(anims[step > 0 ? 0 : anims.Count - 1].Name, 1)!);
                scene.Selected = 0;
            }
            else
            {
                var cur = scene.Layers[scene.Selected];
                int index = (anims.ToList().IndexOf(cur.Animation) + step + anims.Count) % anims.Count;
                var next = scene.Layer(anims[index].Name, cur.Weight)!;
                scene.Layers[scene.Selected] = next;
            }
            var l = scene.Layers[scene.Selected];
            Console.WriteLine($"layer {scene.Selected}: {l.Label} ({l.Animation.Length:0.##} s), weight {l.Weight:0.##}" +
                (l.Excluded.Count > 0 ? $", {l.Excluded.Count} tracks deleted" : "") + (l.Override.Count > 0 ? $", {l.Override.Count} override bones" : ""));
        }

        void OnKey(IKeyboard keyboard, Key key)
        {
            int count = scene.Layers.Count;
            switch (key)
            {
                case Key.Escape: window.Close(); break;
                case Key.F:
                    var (min, max) = Bounds(scene);
                    camera.Frame((min + max) / 2, Math.Max((max - min).Length() / 2, 1e-3f), camera.Yaw * 180 / MathF.PI, camera.Pitch * 180 / MathF.PI);
                    break;
                case Key.W: render.Wireframe = (render.Wireframe + 1) % 3; break;
                case Key.T: render.Textures = !render.Textures; break;
                case Key.N: render.NormalMaps = !render.NormalMaps; break;
                case Key.B: render.BackfaceCulling = !render.BackfaceCulling; break;
                case Key.K: render.Skeleton = !render.Skeleton; break;
                case Key.G: render.Grid = !render.Grid; break;
                case Key.P: screenshotRequested = true; break;
                case Key.Space: scene.Paused = !scene.Paused; break;
                case Key.Up: scene.Speed *= 1.5f; break;
                case Key.Down: scene.Speed /= 1.5f; break;
                case Key.Home: scene.Layers.Clear(); scene.Selected = 0; break;
                case Key.L: // LOD: auto -> 0 -> 1 ... -> auto
                    int maxLod = scene.Parts.Max(p => Math.Max(p.Lods.Count, 1)) - 1;
                    scene.ForcedLod = scene.ForcedLod is null ? 0 : scene.ForcedLod < maxLod ? scene.ForcedLod + 1 : null;
                    break;
                case Key.Tab when count > 0: scene.Selected = (scene.Selected + 1) % count; break;
                case >= Key.Number1 and <= Key.Number9 when key - Key.Number1 < count: scene.Selected = key - Key.Number1; break;
                case Key.Right: ChangeAnimation(1); break;
                case Key.Left: ChangeAnimation(-1); break;
                case Key.Equal or Key.KeypadAdd when count > 0: scene.Layers[scene.Selected].Weight = MathF.Min(scene.Layers[scene.Selected].Weight + 0.1f, 2); break;
                case Key.Minus or Key.KeypadSubtract when count > 0: scene.Layers[scene.Selected].Weight = MathF.Max(scene.Layers[scene.Selected].Weight - 0.1f, 0); break;
                case Key.Delete when count > 0:
                    scene.Layers.RemoveAt(scene.Selected);
                    scene.Selected = Math.Max(0, Math.Min(scene.Selected, scene.Layers.Count - 1));
                    break;
                case Key.Insert when count > 0:
                    var copy = scene.Layer(scene.Layers[scene.Selected].Animation.Name, 0.5f)!;
                    scene.Layers.Add(copy);
                    scene.Selected = scene.Layers.Count - 1;
                    break;
            }
            Title();
        }

        window.Load += () =>
        {
            gl = window.CreateOpenGL();
            basis = new Renderer(gl, assets);
            renderer = new CharacterRenderer(gl, basis, scene);
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
            Console.WriteLine(CharacterViewOptions.Usage[CharacterViewOptions.Usage.IndexOf("Keys:", StringComparison.Ordinal)..]);
            Title();
        };
        window.Update += dt => scene.Advance((float)dt);
        window.Render += _ =>
        {
            if (renderer is null || gl is null) return;
            scene.Pose();
            var size = window.FramebufferSize;
            renderer.Draw(camera, size.X, size.Y, render, scene);
            if (scene.LodChanged) { scene.LodChanged = false; Title(); }
            if (screenshotRequested)
            {
                screenshotRequested = false;
                var file = Path.Combine(Path.GetTempPath(), $"meitou-viewer-{name}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
                gl.ReadBuffer(ReadBufferMode.Back);
                ViewerApp.SavePng(gl, file, size.X, size.Y);
                Console.WriteLine($"saved {file}");
            }
        };
        window.Closing += () => { renderer?.Dispose(); basis?.Dispose(); };
        window.Run();
        return 0;
    }
}
