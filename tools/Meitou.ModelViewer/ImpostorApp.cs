using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.Textures;
using Meitou.Data.World;
using Meitou.Game.Rendering.Display;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Impostors;
using Silk.NET.Vulkan;
using GpuTexture = Meitou.Game.Rendering.Gpu.GpuTexture;


namespace Meitou.ModelViewer;

/// <summary>
/// <c>--impostor-preview &lt;mesh&gt;</c> and <c>--impostor-bake-all</c> (docs/impostors.md, "Preview"): bake (or load from the cache) a foliage
/// mesh's impostor atlas and draw the mesh and its impostor side by side, offscreen, into PNGs; or bake every eligible foliage mesh of the loaded
/// game and print the times and sizes.
/// </summary>
static class ImpostorApp
{
    #region Options and run

    /// <summary>
    /// The command lines and options this mode takes, printed for a bad command line.
    /// </summary>
    public const string Usage = """
        meitou-viewer --impostor-preview <FOLIAGE_MESH name, string id or mesh file> [options]
        meitou-viewer --impostor-bake-all [--rebake]
          --out <dir>        where the pictures go (default C:\Temp\meitou-impostors)
          --size <W>x<H>     the field pictures' size (default 1600x900)
          --rebake           ignore the cache (the new atlas is written to it)
          --debug <n>        unlit impostor: 1 albedo, 2 normal, 3 coverage
          --class <medium|large>   force the size class
        Pictures: <name>-sheet-<sun>.png (rows: elevation -8..85 degrees; columns: mesh | impostor at four azimuths, seen from 1500 units),
        <name>-field-{mesh,impostor}.png and -near-{mesh,impostor}.png (random instances 1500-4000 and 1500-2000 units away, field of view 50),
        <name>-atlas-{albedo,normal}.png (level 0 as stored).
        """;

    /// <summary>
    /// The parsed command line.
    /// </summary>
    sealed class Options
    {
        /// <summary>
        /// The FOLIAGE_MESH to preview (name, string id or mesh file); null with <c>--impostor-bake-all</c>.
        /// </summary>
        public string? Mesh;

        /// <summary>
        /// <see cref="BakeAll"/>: bake every eligible mesh. <see cref="Rebake"/>: ignore the cache.
        /// </summary>
        public bool BakeAll, Rebake;

        /// <summary>
        /// The unlit debug view of the impostor: 0 off, 1 albedo, 2 normal, 3 coverage.
        /// </summary>
        public int Debug;

        /// <summary>
        /// Where the pictures go.
        /// </summary>
        public string Out = @"C:\Temp\meitou-impostors";

        /// <summary>
        /// The field pictures' size in pixels.
        /// </summary>
        public int Width = 1600, Height = 900;

        /// <summary>
        /// The size class forced with <c>--class</c> (medium or large); null for the mesh's own.
        /// </summary>
        public string? Class;
    }

    /// <summary>
    /// Parses the command line, starts Vulkan offscreen, loads the foliage catalog and bakes all or previews one; returns the exit code.
    /// </summary>
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
                    case "--debug": o.Debug = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--class": o.Class = Next(); break;
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
        try
        {
            return o.BakeAll ? BakeAll(frames, catalog, assets, cache, o) : Preview(frames, catalog, assets, cache, o);
        }
        finally
        {
            frames.Flush();
        }
    }

    /// <summary>
    /// The frames the native work records into (offscreen, no window): the bake and the pictures are recorded into the open frame's
    /// <see cref="GpuFrame.PreFrame"/>; <see cref="Flush"/> submits it and waits, <see cref="Next"/> also opens the next one.
    /// </summary>
    sealed class Frames(VulkanDisplay display)
    {
        /// <summary>
        /// The offscreen Vulkan context the frames run on.
        /// </summary>
        public GpuContext Gpu { get; } = display.Context;

        /// <summary>
        /// A frame is open.
        /// </summary>
        bool _open;

        /// <summary>
        /// Opens a frame unless one is.
        /// </summary>
        public void Open()
        {
            if (_open) return;
            Gpu.BeginFrame();
            _open = true;
        }

        /// <summary>
        /// Submits the open frame and waits for every frame to finish; nothing when none is open.
        /// </summary>
        public void Flush()
        {
            if (!_open) return;
            Gpu.EndFrame();
            Gpu.Device.Frames.WaitAll();
            _open = false;
        }

        /// <summary>
        /// Submits the open frame and opens the next.
        /// </summary>
        public void Next()
        {
            Flush();
            Open();
        }
    }

    #endregion

    #region Baking

    /// <summary>
    /// The atlas of <paramref name="source"/> at the size class: from the cache when it matches (unless <c>--rebake</c>), else baked and stored; Hit
    /// says which.
    /// </summary>
    static (ImpostorAtlas Atlas, bool Hit) Obtain(Frames frames, ImpostorBaker baker, ImpostorCache cache, ImpostorSource source,
            ImpostorMeshes meshes, ImpostorClass size, Options o)
    {
        if (!o.Rebake && cache.TryLoad(source) is { } cached && cached.FramePixels == size.FramePixels && cached.Grid == size.Grid)
            return (cached, true);

        frames.Open();
        var atlas = baker.Bake(source, meshes, size, frames.Next);
        cache.Save(source, atlas);
        return (atlas, false);
    }

    /// <summary>
    /// One source per distinct (mesh, material, scale); the records that are not impostor material are counted.
    /// </summary>
    static (List<(FoliageMesh Mesh, ImpostorSource Source)> Sources, int Ineligible, int Missing) CollectSources(
        FoliageCatalog catalog, AssetLocator assets)
    {
        var seen = new HashSet<string>();
        var sources = new List<(FoliageMesh Mesh, ImpostorSource Source)>();
        int ineligible = 0, missing = 0;
        foreach (var mesh in catalog.Meshes.Values.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            if (ImpostorSource.Ineligible(mesh) is not null)
            {
                ineligible++;
                continue;
            }

            var source = ImpostorSource.From(mesh, assets);
            if (source is null)
            {
                missing++;
                continue;
            }

            if (seen.Add(source.Key)) sources.Add((mesh, source));
        }

        return (sources, ineligible, missing);
    }

    /// <summary>
    /// Bakes (or loads) every distinct eligible source of the catalog and prints the times and sizes; returns the exit code.
    /// </summary>
    static int BakeAll(Frames frames, FoliageCatalog catalog, AssetLocator assets, ImpostorCache cache, Options o)
    {
        var (sources, ineligible, missing) = CollectSources(catalog, assets);
        Console.WriteLine($"sources   {sources.Count} distinct (mesh, material, scale); "
            + $"{ineligible} records TERRAIN/EMISSIVE, {missing} without a mesh file");
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
            if (meshes is null)
            {
                missing++;
                continue;
            }

            float worldRadius = meshes.Radius * mesh.MaxScale;
            if (ImpostorClass.For(worldRadius, meshes.Triangles) is not { } size)
            {
                small++;
                continue;
            }
            var one = Stopwatch.StartNew();
            var (atlas, hit) = Obtain(frames, baker, cache, source, meshes, size, o);
            double ms = one.Elapsed.TotalMilliseconds;
            if (hit) hits++;
            else
            {
                baked++;
                bakeMs += ms;
            }
            long file = File.Exists(cache.PathFor(source)) ? new FileInfo(cache.PathFor(source)).Length : 0;
            gpuBytes += atlas.Bytes;
            fileBytes += file;
            byClass.TryGetValue(size.Name, out var sum);
            byClass[size.Name] = (sum.Count + 1, sum.Bytes + atlas.Bytes);
            var t = baker.LastTimes;
            string steps = hit
                ? ""
                : string.Create(CultureInfo.InvariantCulture, $" (upload {t.Upload:0}, "
                + $"render {t.Render:0}, filter {t.Filter:0}, encode {t.Encode:0})");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{(hit ? "cached" : "baked "),-7} {size.Name,-6} r {worldRadius,6:0} {atlas.AtlasPixels,5}² "
                    + $"{atlas.Bytes / 1048576.0,6:0.0} MB GPU {file / 1048576.0,6:0.0} MB file {ms,7:0} ms{steps}  {source.Name}"));
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"total     {baked} baked in {bakeMs / 1000:0.0} s, {hits} from the cache, {small} too small; "
                + $"{gpuBytes / 1048576.0:0} MB on the GPU if all resident, {fileBytes / 1048576.0:0} MB on disk; "
                + $"wall {total.Elapsed.TotalSeconds:0.0} s"));
        foreach (var (name, (count, bytes)) in byClass) Console.WriteLine($"class     {name}: {count} atlases, {bytes / 1048576.0:0} MB");
        foreach (var m in baker.Messages.Distinct().Take(20)) Console.WriteLine($"warning   {m}");
        return 0;
    }

    /// <summary>
    /// The catalog mesh <paramref name="name"/> names: a record's name or string id, or its mesh file; null when none.
    /// </summary>
    static FoliageMesh? Find(FoliageCatalog catalog, string name)
    {
        var all = catalog.Meshes.Values.ToList();
        string file = name.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase) ? name : name + ".mesh";
        return all.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(m => m.StringId.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(m => Path.GetFileName(m.MeshPath.Replace('\\', '/')).Equals(file, StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Preview

    /// <summary>
    /// The pictures' clear colour (RGB, 0 to 1).
    /// </summary>
    static readonly Vector3 Sky = new(0.55f, 0.65f, 0.78f);

    /// <summary>
    /// The sheet's rows: one per elevation (degrees), mesh and impostor side by side per azimuth, each seen from 1500 units with the view just
    /// fitting the sphere.
    /// </summary>
    static readonly float[] Elevations = [-8, 0, 12, 25, 40, 60, 85];

    /// <summary>
    /// The sheet's columns: the azimuths (degrees) each elevation is seen from.
    /// </summary>
    static readonly float[] Azimuths = [20, 75, 160, 250];

    /// <summary>
    /// The preview of <c>--impostor-preview</c>: bakes or loads the mesh's atlas and writes the atlas, sheet and field pictures; returns the exit
    /// code.
    /// </summary>
    static int Preview(Frames frames, FoliageCatalog catalog, AssetLocator assets, ImpostorCache cache, Options o)
    {
        var mesh = Find(catalog, o.Mesh!);
        if (mesh is null)
        {
            Console.Error.WriteLine($"no foliage mesh '{o.Mesh}'");
            return 1;
        }

        if (ImpostorSource.Ineligible(mesh) is { } why)
            Console.WriteLine($"note      {mesh.Name}: {why}; previewed anyway");
        var source = ImpostorSource.From(mesh, assets);
        var meshes = source is null ? null : ImpostorMeshes.Load(source);
        if (source is null || meshes is null)
        {
            Console.Error.WriteLine($"mesh not found or unreadable: {mesh.MeshPath}");
            return 1;
        }

        float worldRadius = meshes.Radius * mesh.MaxScale;
        var size = o.Class switch
        {
            "medium" => ImpostorClass.Medium,
            "large" => ImpostorClass.Large,
            "small" => new ImpostorClass("small", ImpostorClass.SmallFrame, ImpostorClass.SmallGrid,
                    ImpostorClass.ReferenceDistance * worldRadius / ImpostorClass.MinimumRadius),
            _ => ImpostorClass.For(worldRadius, meshes.Triangles) ?? ImpostorClass.Medium,
        };
        string files = Path.GetFileName(source.MeshPath) + (source.LeavesPath is null ? "" : " + " + Path.GetFileName(source.LeavesPath));
        string shape = string.Create(CultureInfo.InvariantCulture, $"{size.Name} ({size.Grid}x{size.Grid} frames of {size.FramePixels})");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"mesh      {mesh.Name} ({files}): radius {meshes.Radius:0.0}, scale {mesh.MinScale:0.##}..{mesh.MaxScale:0.##}, class {shape}"));
        frames.Open();
        using var baker = new ImpostorBaker(frames.Gpu, assets);
        var watch = Stopwatch.StartNew();
        var (atlas, hit) = Obtain(frames, baker, cache, source, meshes, size, o);
        var t = baker.LastTimes;
        string how = hit
            ? "from the cache"
            : string.Create(CultureInfo.InvariantCulture,
                $"baked in {watch.ElapsedMilliseconds} ms (upload {t.Upload:0}, render {t.Render:0}, filter {t.Filter:0}, encode {t.Encode:0})");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"atlas     {how}: " +
            $"{atlas.AtlasPixels}² x 2 maps, {atlas.Levels} levels, {atlas.Bytes / 1048576.0:0.0} MB"));
        foreach (var h in new[] { 900, 1080, 2160 })
            PrintDistances(meshes, mesh, atlas, worldRadius, h);
        foreach (var m in baker.Messages) Console.WriteLine($"warning   {m}");

        Directory.CreateDirectory(o.Out);
        string stem = Path.Combine(o.Out, Safe(mesh.Name));
        WriteAtlasPngs(atlas, stem);
        frames.Open();
        using var preview = new ImpostorPreview(frames.Gpu, assets) { Debug = o.Debug };
        preview.SetMesh(source, meshes);
        preview.SetAtlas(atlas);
        WriteSheets(frames, preview, meshes, atlas, stem);
        // The small class is seen from nearer.
        float scale = size.Transition(worldRadius, ImpostorClass.ReferenceDistance) / ImpostorClass.ReferenceDistance;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"scale     transition {size.Transition(worldRadius, ImpostorClass.ReferenceDistance):0} units, pictures scaled by {scale:0.###}"));
        var sun = Vector3.Normalize(new Vector3(0.45f, 0.8f, 0.4f));
        Field(frames, preview, meshes, mesh, $"{stem}-field", o, sun, 1500 * scale, 4000 * scale, 160, scale);
        Field(frames, preview, meshes, mesh, $"{stem}-near", o, sun, 1500 * scale, 2000 * scale, 40, scale);

        // Around the distance the bake's texture detail is matched to (ImpostorClass.ReferenceDistance): where mesh and impostor should look alike.
        Field(frames, preview, meshes, mesh, $"{stem}-t4k", o, sun, 3500 * scale, 4500 * scale, 120, scale);
        Console.WriteLine($"saved     {stem}-*.png");
        return 0;
    }

    /// <summary>
    /// Prints the distance at which the impostor takes over at <paramref name="h"/> pixels of height, for the smallest and the largest instance.
    /// </summary>
    static void PrintDistances(ImpostorMeshes meshes, FoliageMesh mesh, ImpostorAtlas atlas, float worldRadius, int h)
    {
        var smallest = ImpostorLayout.TransitionDistance(meshes.Radius * mesh.MinScale, atlas.FramePixels, h, 50 * MathF.PI / 180, 0);
        var largest = ImpostorLayout.TransitionDistance(worldRadius, atlas.FramePixels, h, 50 * MathF.PI / 180, 0);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"distance  {h}p: transition at {smallest:0} (smallest instance) .. {largest:0} (largest)"));
    }

    /// <summary>
    /// Writes the atlas's albedo and normal maps (level 0) as PNGs.
    /// </summary>
    static void WriteAtlasPngs(ImpostorAtlas atlas, string stem)
    {
        foreach (var map in new[] { ImpostorMap.Albedo, ImpostorMap.Normal })
        {
            var texture = atlas[map]!;
            int px = atlas.AtlasPixels;
            var rgba = ImpostorEncoder.Decode(texture.Encoding, texture.Levels[0].ToArray(), px);
            if (map == ImpostorMap.Albedo)   // coverage shown over a grey background
            {
                for (int i = 0; i < rgba.Length; i += 4)
                {
                    float a = rgba[i + 3] / 255f;
                    for (int c = 0; c < 3; c++) rgba[i + c] = (byte)(rgba[i + c] * a + 96 * (1 - a));
                    rgba[i + 3] = 255;
                }
            }

            PngWriter.Write($"{stem}-atlas-{map.ToString().ToLowerInvariant()}.png", px, px, Flip(rgba, px, px));
        }
    }

    /// <summary>
    /// Writes the sheets: one per sun (high, low) and the two on baked frame directions.
    /// </summary>
    static void WriteSheets(Frames frames, ImpostorPreview preview, ImpostorMeshes meshes, ImpostorAtlas atlas, string stem)
    {
        var suns = new (string Name, Vector3 Direction)[]
        {
            ("high", Vector3.Normalize(new Vector3(0.45f, 0.8f, 0.4f))),
            ("low", Vector3.Normalize(new Vector3(-0.95f, 0.22f, 0.15f))),
            ("back", Vector3.Normalize(new Vector3(-0.25f, 0.45f, -0.85f))),
        };
        foreach (var (name, sun) in suns)
            Sheet(frames, preview, meshes, $"{stem}-sheet-{name}.png", sun, (row, col) => Direction(Elevations[row], Azimuths[col]));

        // Cameras on baked frame directions: one frame is sampled alone (the bake and the projection, without the blend).
        int g = atlas.Grid;
        Sheet(frames, preview, meshes, $"{stem}-frames.png", suns[0].Direction,
            (row, col) => ImpostorLayout.FrameDirection(col * (g - 1) / 3, Math.Min(row, g - 1), g));
        Sheet(frames, preview, meshes, $"{stem}-frames2.png", suns[0].Direction, (row, col) => ImpostorLayout.FrameDirection(col, row, g));
    }

    /// <summary>
    /// <paramref name="name"/> with every character but ASCII letters, digits, '-' and '_' made '_' (a file name).
    /// </summary>
    static string Safe(string name) => new(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    /// <summary>
    /// The image's rows in reverse order (top row first again).
    /// </summary>
    static byte[] Flip(byte[] rgba, int w, int h)
    {
        var flipped = new byte[rgba.Length];
        for (int y = 0; y < h; y++) rgba.AsSpan((h - 1 - y) * w * 4, w * 4).CopyTo(flipped.AsSpan(y * w * 4));
        return flipped;
    }

    /// <summary>
    /// A 4× multisampled target of the given size cleared to the sky colour, drawn by <paramref name="draw"/> (into the open rendering of the frame's
    /// <see cref="GpuFrame.PreFrame"/>), resolved, read back and saved as a PNG (rows flipped: the images hold GL's bottom row first).
    /// </summary>
    static void Offscreen(Frames frames, int w, int h, string path, Action<IGpuCommandList, AttachmentFormats> draw)
    {
        var gpu = frames.Gpu;
        frames.Open();
        var cmd = gpu.Frame.PreFrame;
        var pre = cmd.Handle;
        using var colour = gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, w, h, Samples: 4,
                Use: TextureUse.ColourTarget | TextureUse.TransferSrc, Name: "impostor preview target"), pre);
        using var depth = gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, w, h, Samples: 4, Use: TextureUse.DepthTarget,
                Name: "impostor preview target"), pre);
        using var resolved = gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, w, h, Use: TextureUse.TransferDst | TextureUse.TransferSrc,
                Name: "impostor preview target"), pre);
        var formats = new AttachmentFormats(colour.Desc.Format, depth.Desc.Format, 4);
        cmd.Order();
        cmd.BeginRendering(new RenderingDesc(
            new RenderTarget(colour.Attachment(), LoadOp.Clear, new ClearColour(Sky.X, Sky.Y, Sky.Z, 1f)),
            new RenderTarget(depth.Attachment(), LoadOp.Clear, default, DepthClear: 1f), w, h));
        cmd.SetScissor(new PixelRect(0, 0, (uint)w, (uint)h));
        draw(cmd, formats);
        cmd.EndRendering();
        cmd.Order();
        cmd.Resolve(colour, resolved);
        cmd.Order();
        cmd.Invalidate();
        frames.Flush();
        var pixels = gpu.ReadBack(resolved, 4);
        var flipped = Flip(pixels, w, h);
        for (int i = 3; i < flipped.Length; i += 4) flipped[i] = 255;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        PngWriter.Write(path, w, h, flipped);
    }

    /// <summary>
    /// An instance transform: scale, then yaw about +Y, then the position; its M14 carries <paramref name="fade"/>.
    /// </summary>
    static Matrix4x4 Instance(float scale, float yaw, Vector3 position, float fade = 2)
    {
        var m = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(position);
        m.M14 = fade;
        return m;
    }

    /// <summary>
    /// The unit direction at <paramref name="elevation"/> above the horizon and <paramref name="azimuth"/> round from +Z towards +X (degrees).
    /// </summary>
    static Vector3 Direction(float elevation, float azimuth)
    {
        float e = elevation * MathF.PI / 180, a = azimuth * MathF.PI / 180;
        return new Vector3(MathF.Cos(e) * MathF.Sin(a), MathF.Sin(e), MathF.Cos(e) * MathF.Cos(a));
    }

    /// <summary>
    /// Rows of view directions, pairs of mesh and impostor per direction.
    /// </summary>
    static void Sheet(Frames frames, ImpostorPreview preview, ImpostorMeshes meshes, string path, Vector3 sun, Func<int, int, Vector3> direction,
            int rows = 7, int columns = 4)
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
            {
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
                    preview.DrawMeshes(cmd, formats, new PixelViewport(col * 2 * cell, y, cell, cell, 0, 1), view * projection, eye, sun, one,
                            coverage: false);
                    preview.DrawImpostors(cmd, formats, new PixelViewport((col * 2 + 1) * cell, y, cell, cell, 0, 1), view * projection, eye,
                            cameraUp, sun, one, coverage: false);
                }
            }
        });
    }

    /// <summary>
    /// Random instances (yaw, scale in the record's range) between <paramref name="near"/> and <paramref name="far"/> units, drawn once as meshes and
    /// once as impostors.
    /// </summary>
    static void Field(Frames frames, ImpostorPreview preview, ImpostorMeshes meshes, FoliageMesh mesh, string stem, Options o, Vector3 sun,
            float near, float far, int count, float fieldScale = 1)
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
        var eye = new Vector3(0, 400 * fieldScale, 0);
        var target = new Vector3(0, (400 - MathF.Tan(4 * MathF.PI / 180) * 2000) * fieldScale, -2000 * fieldScale);
        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, 50 * fieldScale, 20000 * fieldScale);
        var forward = Vector3.Normalize(target - eye);
        var cameraUp = Vector3.Cross(Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY)), forward);
        var all = new PixelViewport(0, 0, o.Width, o.Height, 0, 1);
        Offscreen(frames, o.Width, o.Height, stem + "-mesh.png",
                (cmd, formats) => preview.DrawMeshes(cmd, formats, all, view * projection, eye, sun, ordered, coverage: false));
        Offscreen(frames, o.Width, o.Height, stem + "-impostor.png",
                (cmd, formats) => preview.DrawImpostors(cmd, formats, all, view * projection, eye, cameraUp, sun, ordered, coverage: false));
    }

    #endregion
}
