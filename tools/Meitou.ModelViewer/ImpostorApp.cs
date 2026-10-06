using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Textures;
using Meitou.Data.World;
using Meitou.Rendering;
using Meitou.Rendering.Display;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Impostors;
using Silk.NET.Vulkan;
using Texture = Meitou.Rendering.Gpu.Texture;

namespace Meitou.ModelViewer;

/// <summary>
/// <c>--impostor-preview &lt;mesh&gt;</c> and <c>--impostor-bake-all</c> (docs/impostors.md, "Preview"): bake (or load from the cache) a foliage mesh's
/// impostor atlas and draw the mesh and its impostor side by side, offscreen, into PNGs; or bake every eligible foliage mesh of the loaded game
/// and print the times and sizes.
/// </summary>
static class ImpostorApp
{
    public const string Usage = """
        meitou-viewer --impostor-preview <FOLIAGE_MESH name, string id or mesh file> [options]
        meitou-viewer --impostor-bake-all [--rebake]
          --out <dir>        where the pictures go (default C:\Temp\meitou-impostors)
          --size <W>x<H>     the field pictures' size (default 1600x900)
          --rebake           ignore the cache (the new atlas is written to it)
          --parallax         one depth step per frame when sampling
          --blend            blend the three frames (default: one frame per pixel, ordered dither)
          --debug <n>        unlit impostor: 1 albedo, 2 normal, 3 coverage
          --depth-write      the impostor writes its blended depth
          --class <medium|large>   force the size class
        Pictures: <name>-sheet-<sun>.png (rows: elevation -8..85 degrees; columns: mesh | impostor at four azimuths, seen from 1500 units),
        <name>-field-{mesh,impostor}.png and -near-{mesh,impostor}.png (random instances 1500-4000 and 1500-2000 units away, field of view 50),
        <name>-atlas-{albedo,normal,depth}.png (level 0 as stored).
        """;

    sealed class Options
    {
        public string? Mesh;
        public bool BakeAll, Rebake, Parallax, DepthWrite, Blend;
        public int Debug;
        public string Out = @"C:\Temp\meitou-impostors";
        public int Width = 1600, Height = 900;
        public string? Class;
    }

    public static int Run(string[] args)
    {
        var o = new Options();
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
                switch (a)
                {
                    case "--impostor-preview": o.Mesh = Next(); break;
                    case "--impostor-bake-all": o.BakeAll = true; break;
                    case "--out": o.Out = Next(); break;
                    case "--size":
                        var parts = Next().Split('x');
                        o.Width = int.Parse(parts[0], CultureInfo.InvariantCulture);
                        o.Height = int.Parse(parts[1], CultureInfo.InvariantCulture);
                        break;
                    case "--rebake": o.Rebake = true; break;
                    case "--parallax": o.Parallax = true; break;
                    case "--blend": o.Blend = true; break;
                    case "--debug": o.Debug = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--depth-write": o.DepthWrite = true; break;
                    case "--class": o.Class = Next(); break;
                    case "--renderer": WorldOptions.IgnoreRenderer(Next()); break;
                    case "-h" or "--help": Console.WriteLine(Usage); return 2;
                    default: throw new ArgumentException($"unknown option {a}");
                }
            }
        }
        catch (Exception e) when (e is ArgumentException or FormatException or IndexOutOfRangeException)
        {
            Console.Error.WriteLine(e.Message);
            Console.WriteLine(Usage);
            return 2;
        }
        var install = GameInstall.Locate();
        if (install is null)
        {
            Console.Error.WriteLine($"Kenshi install not found: set {GameInstall.EnvironmentVariable} or create {GameInstall.LocalConfigFile}.");
            return 1;
        }
        var watch = Stopwatch.StartNew();
        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
        var catalog = FoliageCatalog.Load(db);
        var assets = new AssetLocator(install);
        Console.WriteLine($"loaded    {catalog.Meshes.Count} foliage meshes in {watch.ElapsedMilliseconds} ms");
        using var display = new VulkanDisplay(null, vsync: false);
        var frames = new Frames(display);
        var cache = new ImpostorCache();
        Console.WriteLine($"cache     {cache.Root}");
        try { return o.BakeAll ? BakeAll(frames, catalog, assets, cache, o) : Preview(frames, catalog, assets, cache, o); }
        finally { frames.Flush(); }
    }

    /// <summary>
    /// The frames the native work records into (offscreen, no window): the bake and the pictures are recorded into the open frame's
    /// <see cref="GpuFrame.PreFrame"/>; <see cref="Flush"/> submits it and waits, <see cref="Next"/> also opens the next one.
    /// </summary>
    sealed class Frames(VulkanDisplay display)
    {
        public GpuContext Gpu { get; } = display.VkGl.Context;
        bool open;

        public void Open()
        {
            if (open) return;
            display.VkGl.BeginFrame(1, 1);
            open = true;
        }

        public void Flush()
        {
            if (!open) return;
            display.EndFrame();
            Gpu.Device.Frames.WaitAll();
            open = false;
        }

        public void Next()
        {
            Flush();
            Open();
        }
    }

    static (ImpostorAtlas Atlas, bool Hit) Obtain(Frames frames, ImpostorBaker baker, ImpostorCache cache, ImpostorSource source, ImpostorMeshes meshes, ImpostorClass size, Options o)
    {
        if (!o.Rebake && cache.TryLoad(source) is { } cached && cached.FramePixels == size.FramePixels && cached.Grid == size.Grid) return (cached, true);
        frames.Open();
        var atlas = baker.Bake(source, meshes, size, frames.Next);
        cache.Save(source, atlas);
        return (atlas, false);
    }

    static int BakeAll(Frames frames, FoliageCatalog catalog, AssetLocator assets, ImpostorCache cache, Options o)
    {
        var seen = new HashSet<string>();
        var sources = new List<(FoliageMesh Mesh, ImpostorSource Source)>();
        int ineligible = 0, missing = 0;
        foreach (var mesh in catalog.Meshes.Values.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            if (ImpostorSource.Ineligible(mesh) is not null) { ineligible++; continue; }
            var source = ImpostorSource.From(mesh, assets);
            if (source is null) { missing++; continue; }
            if (seen.Add(source.Key)) sources.Add((mesh, source));
        }
        Console.WriteLine($"sources   {sources.Count} distinct (mesh, material, scale); {ineligible} records TERRAIN/EMISSIVE, {missing} without a mesh file");
        // Decode on the worker threads, bake in order on this one.
        var decoded = sources.Select(s => Task.Run(() => ImpostorMeshes.Load(s.Source))).ToList();
        frames.Open();
        using var baker = new ImpostorBaker(frames.Gpu, assets);
        var total = Stopwatch.StartNew();
        int baked = 0, hits = 0, small = 0;
        long gpuBytes = 0, fileBytes = 0;
        double bakeMs = 0;
        var byClass = new Dictionary<string, (int Count, long Bytes)>();
        for (int i = 0; i < sources.Count; i++)
        {
            var (mesh, source) = sources[i];
            var meshes = decoded[i].Result;
            if (meshes is null) { missing++; continue; }
            float worldRadius = meshes.Radius * mesh.MaxScale;
            if (ImpostorClass.For(worldRadius) is not { } size) { small++; continue; }
            var one = Stopwatch.StartNew();
            var (atlas, hit) = Obtain(frames, baker, cache, source, meshes, size, o);
            double ms = one.Elapsed.TotalMilliseconds;
            if (hit) hits++;
            else { baked++; bakeMs += ms; }
            long file = File.Exists(cache.PathFor(source)) ? new FileInfo(cache.PathFor(source)).Length : 0;
            gpuBytes += atlas.Bytes;
            fileBytes += file;
            byClass.TryGetValue(size.Name, out var sum);
            byClass[size.Name] = (sum.Count + 1, sum.Bytes + atlas.Bytes);
            var t = baker.LastTimes;
            string steps = hit ? "" : string.Create(CultureInfo.InvariantCulture, $" (upload {t.Upload:0}, render {t.Render:0}, filter {t.Filter:0}, encode {t.Encode:0})");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{(hit ? "cached" : "baked "),-7} {size.Name,-6} r {worldRadius,6:0} {atlas.AtlasPixels,5}² {atlas.Bytes / 1048576.0,6:0.0} MB GPU {file / 1048576.0,6:0.0} MB file {ms,7:0} ms{steps}  {source.Name}"));
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"total     {baked} baked in {bakeMs / 1000:0.0} s, {hits} from the cache, {small} too small; {gpuBytes / 1048576.0:0} MB on the GPU if all resident, {fileBytes / 1048576.0:0} MB on disk; wall {total.Elapsed.TotalSeconds:0.0} s"));
        foreach (var (name, (count, bytes)) in byClass) Console.WriteLine($"class     {name}: {count} atlases, {bytes / 1048576.0:0} MB");
        foreach (var m in baker.Messages.Distinct().Take(20)) Console.WriteLine($"warning   {m}");
        return 0;
    }

    static FoliageMesh? Find(FoliageCatalog catalog, string name)
    {
        var all = catalog.Meshes.Values.ToList();
        return all.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(m => m.StringId.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(m => Path.GetFileName(m.MeshPath.Replace('\\', '/')).Equals(name.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase) ? name : name + ".mesh", StringComparison.OrdinalIgnoreCase));
    }

    static int Preview(Frames frames, FoliageCatalog catalog, AssetLocator assets, ImpostorCache cache, Options o)
    {
        var mesh = Find(catalog, o.Mesh!);
        if (mesh is null) { Console.Error.WriteLine($"no foliage mesh '{o.Mesh}'"); return 1; }
        if (ImpostorSource.Ineligible(mesh) is { } why) Console.WriteLine($"note      {mesh.Name}: {why}; previewed anyway");
        var source = ImpostorSource.From(mesh, assets);
        var meshes = source is null ? null : ImpostorMeshes.Load(source);
        if (source is null || meshes is null) { Console.Error.WriteLine($"mesh not found or unreadable: {mesh.MeshPath}"); return 1; }
        float worldRadius = meshes.Radius * mesh.MaxScale;
        var size = o.Class switch
        {
            "medium" => ImpostorClass.Medium,
            "large" => ImpostorClass.Large,
            _ => ImpostorClass.For(worldRadius) ?? ImpostorClass.Medium,
        };
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"mesh      {mesh.Name} ({Path.GetFileName(source.MeshPath)}{(source.LeavesPath is null ? "" : " + " + Path.GetFileName(source.LeavesPath))}): radius {meshes.Radius:0.0}, scale {mesh.MinScale:0.##}..{mesh.MaxScale:0.##}, class {size.Name} ({size.Grid}x{size.Grid} frames of {size.FramePixels})"));
        frames.Open();
        using var baker = new ImpostorBaker(frames.Gpu, assets);
        var watch = Stopwatch.StartNew();
        var (atlas, hit) = Obtain(frames, baker, cache, source, meshes, size, o);
        var t = baker.LastTimes;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"atlas     {(hit ? "from the cache" : $"baked in {watch.ElapsedMilliseconds} ms (upload {t.Upload:0}, render {t.Render:0}, filter {t.Filter:0}, encode {t.Encode:0})")}: " +
            $"{atlas.AtlasPixels}² x 3 maps, {atlas.Levels} levels, {atlas.Bytes / 1048576.0:0.0} MB"));
        foreach (var h in new[] { 900, 1080, 2160 })
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"distance  {h}p: transition at {ImpostorLayout.TransitionDistance(meshes.Radius * mesh.MinScale, atlas.FramePixels, h, 50 * MathF.PI / 180, 0):0} (smallest instance) .. {ImpostorLayout.TransitionDistance(worldRadius, atlas.FramePixels, h, 50 * MathF.PI / 180, 0):0} (largest)"));
        foreach (var m in baker.Messages) Console.WriteLine($"warning   {m}");

        Directory.CreateDirectory(o.Out);
        string stem = Path.Combine(o.Out, Safe(mesh.Name));
        foreach (var map in new[] { ImpostorMap.Albedo, ImpostorMap.Normal, ImpostorMap.Depth })
        {
            var texture = atlas[map]!;
            int px = atlas.AtlasPixels;
            var rgba = ImpostorEncoder.Decode(texture.Encoding, texture.Levels[0], px);
            if (map == ImpostorMap.Albedo)   // coverage shown over a grey background
                for (int i = 0; i < rgba.Length; i += 4)
                {
                    float a = rgba[i + 3] / 255f;
                    for (int c = 0; c < 3; c++) rgba[i + c] = (byte)(rgba[i + c] * a + 96 * (1 - a));
                    rgba[i + 3] = 255;
                }
            PngWriter.Write($"{stem}-atlas-{map.ToString().ToLowerInvariant()}.png", px, px, Flip(rgba, px, px));
        }

        frames.Open();
        using var preview = new ImpostorPreview(frames.Gpu, assets) { Parallax = o.Parallax, DepthWrite = o.DepthWrite, Blend = o.Blend, Debug = o.Debug };
        preview.SetMesh(source, meshes);
        preview.SetAtlas(atlas);
        var suns = new (string Name, Vector3 Direction)[]
        {
            ("high", Vector3.Normalize(new Vector3(0.45f, 0.8f, 0.4f))),
            ("low", Vector3.Normalize(new Vector3(-0.95f, 0.22f, 0.15f))),
            ("back", Vector3.Normalize(new Vector3(-0.25f, 0.45f, -0.85f))),
        };
        foreach (var (name, sun) in suns) Sheet(frames, preview, meshes, $"{stem}-sheet-{name}.png", sun, (row, col) => Direction(Elevations[row], Azimuths[col]));
        // Cameras on baked frame directions: one frame is sampled alone (the bake and the projection, without the blend).
        int g = atlas.Grid;
        Sheet(frames, preview, meshes, $"{stem}-frames.png", suns[0].Direction, (row, col) => ImpostorLayout.FrameDirection(col * (g - 1) / 3, Math.Min(row, g - 1), g));
        Sheet(frames, preview, meshes, $"{stem}-frames2.png", suns[0].Direction, (row, col) => ImpostorLayout.FrameDirection(col, row, g));
        Field(frames, preview, meshes, mesh, $"{stem}-field", o, suns[0].Direction, 1500, 4000, 160);
        Field(frames, preview, meshes, mesh, $"{stem}-near", o, suns[0].Direction, 1500, 2000, 40);
        Console.WriteLine($"saved     {stem}-*.png");
        return 0;
    }

    static string Safe(string name) => new(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    static byte[] Flip(byte[] rgba, int w, int h)
    {
        var flipped = new byte[rgba.Length];
        for (int y = 0; y < h; y++) rgba.AsSpan((h - 1 - y) * w * 4, w * 4).CopyTo(flipped.AsSpan(y * w * 4));
        return flipped;
    }

    static readonly Vector3 Sky = new(0.55f, 0.65f, 0.78f);

    /// <summary>
    /// A 4× multisampled target of the given size cleared to the sky colour, drawn by <paramref name="draw"/> (into the open rendering of the frame's
    /// <see cref="GpuFrame.PreFrame"/>), resolved, read back and saved as a PNG (rows flipped: the images hold GL's bottom row first).
    /// </summary>
    static void Offscreen(Frames frames, int w, int h, string path, Action<CommandList, AttachmentFormats> draw)
    {
        var gpu = frames.Gpu;
        frames.Open();
        var cmd = gpu.Frame.PreFrame;
        var pre = cmd.Handle;
        using var colour = Texture.Create(gpu, new TextureDesc(Format.R8G8B8A8Unorm, w, h, Samples: 4, Use: TextureUse.ColourTarget | TextureUse.TransferSrc, Name: "impostor preview target"), pre);
        using var depth = Texture.Create(gpu, new TextureDesc(Format.D32Sfloat, w, h, Samples: 4, Use: TextureUse.DepthTarget, Name: "impostor preview target"), pre);
        using var resolved = Texture.Create(gpu, new TextureDesc(Format.R8G8B8A8Unorm, w, h, Use: TextureUse.TransferDst | TextureUse.TransferSrc, Name: "impostor preview target"), pre);
        var formats = new AttachmentFormats(colour.Desc.Format, depth.Desc.Format, 4);
        cmd.Barrier(BarrierBatch.Full);
        cmd.BeginRendering(new RenderingDesc(
            new RenderTarget(colour.Attachment(), AttachmentLoadOp.Clear, new ClearValue(new ClearColorValue(Sky.X, Sky.Y, Sky.Z, 1f)), colour.Image),
            new RenderTarget(depth.Attachment(), AttachmentLoadOp.Clear, new ClearValue(depthStencil: new ClearDepthStencilValue(1f, 0)), depth.Image), w, h));
        cmd.SetScissor(new Rect2D(default, new Extent2D((uint)w, (uint)h)));
        draw(cmd, formats);
        cmd.EndRendering();
        cmd.Barrier(BarrierBatch.Full);
        cmd.Resolve(colour, resolved);
        cmd.Barrier(BarrierBatch.Full);
        cmd.Invalidate();
        frames.Flush();
        var pixels = gpu.ReadBack(resolved, 4);
        var flipped = Flip(pixels, w, h);
        for (int i = 3; i < flipped.Length; i += 4) flipped[i] = 255;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        PngWriter.Write(path, w, h, flipped);
    }

    static Matrix4x4 Instance(float scale, float yaw, Vector3 position, float fade = 2)
    {
        var m = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(position);
        m.M14 = fade;
        return m;
    }

    /// <summary>One row per elevation, mesh and impostor side by side per azimuth, each seen from 1500 units with the view just fitting the sphere.</summary>
    static Vector3 Direction(float elevation, float azimuth)
    {
        float e = elevation * MathF.PI / 180, a = azimuth * MathF.PI / 180;
        return new Vector3(MathF.Cos(e) * MathF.Sin(a), MathF.Sin(e), MathF.Cos(e) * MathF.Cos(a));
    }

    static readonly float[] Elevations = [-8, 0, 12, 25, 40, 60, 85];
    static readonly float[] Azimuths = [20, 75, 160, 250];

    /// <summary>Rows of view directions, pairs of mesh and impostor per direction.</summary>
    static void Sheet(Frames frames, ImpostorPreview preview, ImpostorMeshes meshes, string path, Vector3 sun, Func<int, int, Vector3> direction, int rows = 7, int columns = 4)
    {
        const int cell = 224;
        int w = cell * columns * 2, h = cell * rows;
        float distance = 1500, r = meshes.Radius;
        var centre = meshes.Centre;
        float fov = 2 * MathF.Atan(1.08f * r / distance);
        Matrix4x4[] one = [Instance(1, 0, Vector3.Zero)];
        Offscreen(frames, w, h, path, (cmd, formats) =>
        {
            for (int row = 0; row < rows; row++)
                for (int col = 0; col < columns; col++)
                {
                    var dir = direction(row, col);
                    var eye = centre + dir * distance;
                    var worldUp = dir.Y > 0.98f ? Vector3.Normalize(new Vector3(-dir.X, 0, -dir.Z) + new Vector3(0, 0, -1e-3f)) : Vector3.UnitY;
                    var view = Matrix4x4.CreateLookAt(eye, centre, worldUp);
                    var projection = Matrix4x4.CreatePerspectiveFieldOfView(fov, 1, distance - 2 * r, distance + 2 * r);
                    var forward = Vector3.Normalize(centre - eye);
                    var right = Vector3.Normalize(Vector3.Cross(forward, worldUp));
                    var cameraUp = Vector3.Cross(right, forward);
                    int y = (rows - 1 - row) * cell;
                    preview.DrawMeshes(cmd, formats, new Viewport(col * 2 * cell, y, cell, cell, 0, 1), view * projection, eye, sun, one, coverage: false);
                    preview.DrawImpostors(cmd, formats, new Viewport((col * 2 + 1) * cell, y, cell, cell, 0, 1), view * projection, eye, cameraUp, sun, one, coverage: false);
                }
        });
    }

    /// <summary>Random instances (yaw, scale in the record's range) between <paramref name="near"/> and <paramref name="far"/> units, drawn once as meshes and once as impostors.</summary>
    static void Field(Frames frames, ImpostorPreview preview, ImpostorMeshes meshes, FoliageMesh mesh, string stem, Options o, Vector3 sun, float near, float far, int count)
    {
        var random = new Random(1234);
        float fov = 50 * MathF.PI / 180, aspect = o.Width / (float)o.Height;
        float halfWidth = MathF.Atan(MathF.Tan(fov / 2) * aspect) * 0.9f;
        var instances = new List<(float Distance, Matrix4x4 M)>();
        for (int i = 0; i < count; i++)
        {
            float d = near + (far - near) * MathF.Sqrt(random.NextSingle());
            float angle = (random.NextSingle() * 2 - 1) * halfWidth;
            float scale = mesh.MinScale + (mesh.MaxScale - mesh.MinScale) * random.NextSingle();
            var p = new Vector3(MathF.Sin(angle) * d, 0, -MathF.Cos(angle) * d);
            instances.Add((d, Instance(scale, random.NextSingle() * MathF.Tau, p)));
        }
        var ordered = instances.OrderBy(i => i.Distance).Select(i => i.M).ToArray();
        var eye = new Vector3(0, 400, 0);
        var target = new Vector3(0, 400 - MathF.Tan(4 * MathF.PI / 180) * 2000, -2000);
        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, 50, 20000);
        var forward = Vector3.Normalize(target - eye);
        var cameraUp = Vector3.Cross(Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY)), forward);
        var all = new Viewport(0, 0, o.Width, o.Height, 0, 1);
        Offscreen(frames, o.Width, o.Height, stem + "-mesh.png", (cmd, formats) => preview.DrawMeshes(cmd, formats, all, view * projection, eye, sun, ordered, coverage: false));
        Offscreen(frames, o.Width, o.Height, stem + "-impostor.png", (cmd, formats) => preview.DrawImpostors(cmd, formats, all, view * projection, eye, cameraUp, sun, ordered, coverage: false));
    }
}
