using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Textures;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// GPU resources for texturing the terrain (docs/formats/terrain.md, "How the terrain is textured"), streamed to
/// follow the camera: the biome layer textures of every biome that <c>blendinfo.dat</c> lists in a cell within the
/// material distance of the eye (two texture arrays whose slots are handed out per distinct texture pair, least
/// recently needed evicted first), a parameter texture with one row per biome, the cell table (what each biome slot
/// of a cell is right now: a row, "still loading" or unused), the world blend map, and windows of the overlay and
/// colour maps that move with the eye. Decoding runs on worker threads; every upload is made on the thread that calls
/// <see cref="Update"/>, in steps through an <see cref="UploadQueue"/>. Native textures (phase 8), sampled as their GL versions were
/// (<see cref="TerrainTexture"/>).
/// </summary>
public sealed unsafe class TerrainTextures : IDisposable
{
    /// <summary>Most texture pairs resident at once (each costs a BC3 and a BC1 layer of <c>layerSize</c>² with mips, 8.4 MB at 2048).</summary>
    public const int MaxLayers = 192;

    /// <summary>Cell table value of a biome slot that is used but not resident yet / of an unused slot.</summary>
    const byte Pending = 254, Unused = 255;

    /// <summary>The native GPU API every texture is made with (phase 8: no GL texture of its own).</summary>
    public GpuContext Gpu { get; }
    readonly GameInstall install;
    readonly AssetLocator assets;
    readonly int layerSize, levelCount;
    readonly BlendInfoFile info;
    readonly List<BiomeTerrain> biomes = [];
    readonly Dictionary<uint, int> rowOf = [];
    readonly BiomeState[] state;
    readonly Dictionary<(string?, string?), Pair> pairs = [];
    readonly Dictionary<Pair, List<int>> usedBy = [];
    readonly Pair?[] owner;
    readonly ConcurrentQueue<Decoded> decoded = new();
    readonly ConcurrentQueue<string> problems = new();
    readonly int maxInFlight = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
    readonly MapWindows? mapWindows;
    readonly List<int> needed = [];
    TerrainTexture? diffuseArray, normalArray, paramTexture, cellTexture, blendTexture, overlayTexture, colourTexture, groundTexture, worldColourTexture;
    int inFlight, nextFree, printed, stamp;
    long frame;
    bool cellsDirty = true, capacityWarned;
    Task<MapWindows.Update>? mapJob;
    bool mapUploading;

    sealed class Pair
    {
        public string? Diffuse, Normal;
        public int Slot = -1;
        public long Used;
        public int Needed;
        public bool Loading, Failed;
    }

    sealed class BiomeState
    {
        public required Pair[] Pairs;
        public bool Resident;
    }

    sealed record Decoded(Pair Pair, byte[][]? Diffuse, byte[][]? Normal);

    TerrainTextures(GpuContext gpu, GameInstall install, AssetLocator assets, BlendInfoFile info, BiomeTerrain[] all, int layerSize, MapWindows? maps)
    {
        Gpu = gpu;
        this.install = install;
        this.assets = assets;
        this.info = info;
        this.layerSize = layerSize;
        mapWindows = maps;
        levelCount = (int)Math.Log2(layerSize) + 1;
        CellsX = info.CellsX;
        CellsZ = info.CellsZ;
        foreach (var b in all)
        {
            if (biomes.Count >= Pending) break;
            rowOf[b.Index] = biomes.Count;
            biomes.Add(b);
        }
        state = new BiomeState[biomes.Count];
        for (int b = 0; b < biomes.Count; b++)
        {
            var layers = new Pair[6];
            for (int l = 0; l < 6; l++)
            {
                var key = (biomes[b].Diffuse[l]?.ToLowerInvariant(), biomes[b].Normal[l]?.ToLowerInvariant());
                if (!pairs.TryGetValue(key, out var pair)) pairs[key] = pair = new Pair { Diffuse = biomes[b].Diffuse[l], Normal = biomes[b].Normal[l] };
                layers[l] = pair;
                if (!usedBy.TryGetValue(pair, out var list)) usedBy[pair] = list = [];
                if (!list.Contains(b)) list.Add(b);
            }
            state[b] = new BiomeState { Pairs = layers };
        }
        Capacity = Math.Min(pairs.Count, MaxLayers);
        owner = new Pair?[Capacity];
    }

    public bool HasBiomes { get; private set; }
    public bool HasGround { get; private set; }
    public bool HasWorldColour { get; private set; }
    public int CellsX { get; }
    public int CellsZ { get; }
    /// <summary>0 no overlay/colour maps (missing), 1 still loading, 2 ready (<see cref="Region"/> is valid).</summary>
    public int MapState { get; private set; }
    /// <summary>x0, z0, 1/width, 1/depth of the overlay and colour windows in world units (the two cover the same square).</summary>
    public Vector4 Region { get; private set; }
    public int Capacity { get; }
    public int TotalBiomes => biomes.Count;
    public int TotalPairs => pairs.Count;
    /// <summary>Texture pairs being decoded plus map windows being cut or uploaded.</summary>
    public int Outstanding => inFlight + (mapJob is null && !mapUploading ? 0 : 1);
    public int ResidentBiomes => state.Count(s => s.Resident);
    public IEnumerable<BiomeTerrain> Resident => biomes.Where((_, i) => state[i].Resident);
    public List<string> Messages { get; } = [];

    /// <summary>No decoding or uploading is outstanding for what the last <see cref="Update"/> needed.</summary>
    public bool Idle => inFlight == 0 && mapJob is null && !mapUploading && needed.All(b => state[b].Pairs.All(p => p.Slot >= 0 || p.Failed)) &&
                        !pairs.Values.Any(p => p.Loading) && (MapState != 1 || mapWindows is null);

    /// <summary>As <see cref="Create(GpuContext, GameInstall, GameDatabase, AssetLocator, int, int)"/>; <paramref name="gl"/> is not used (kept
    /// for the callers until phase 8 removes IGl from them).</summary>
    public static TerrainTextures Create(IGl gl, GpuContext gpu, GameInstall install, GameDatabase db, AssetLocator assets, int layerSize = 512, int worldColourSize = 2048) =>
        Create(gpu, install, db, assets, layerSize, worldColourSize);

    /// <param name="layerSize">Edge length every layer texture is brought to (the arrays need one size).</param>
    public static TerrainTextures Create(GpuContext gpu, GameInstall install, GameDatabase db, AssetLocator assets, int layerSize = 512, int worldColourSize = 2048)
    {
        var info = BlendInfoFile.Open(install);
        var all = BiomeTerrain.ByIndex(db);
        var messages = new List<string>();
        MapWindows? maps = null;
        try { maps = MapWindows.Open(install, messages); }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            messages.Add($"overlay maps: {e.Message}");
        }
        var t = new TerrainTextures(gpu, install, assets, info, [.. all.Values], layerSize, maps);
        t.Messages.AddRange(messages);
        if (maps is null) t.MapState = 0;
        else
        {
            t.MapState = 1;
            t.AllocateMaps();
        }
        try { t.BuildBiomes(); }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            t.Messages.Add($"biome textures: {e.Message}");
        }
        try { t.BuildWorld(install, db, all, worldColourSize); }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        {
            t.Messages.Add($"world maps: {e.Message}");
        }
        try { t.biomeMap = FoliageBiomeMap.Open(install); }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        {
            t.Messages.Add($"biome map: {e.Message}");
        }
        return t;
    }

    FoliageBiomeMap? biomeMap;

    /// <summary>
    /// Parameter row of the biome a TERRAIN-mode map feature at (x, z) is textured with: the one of <c>biomemap.png</c>
    /// at that point (docs/formats/foliage.md, "TERRAIN-mode meshes"), or -1 when it is not resident.
    /// </summary>
    public int FeatureBiomeRow(float x, float z) =>
        biomeMap is not null && rowOf.TryGetValue(biomeMap.At(x, z), out int row) && state[row].Resident ? row : -1;

    /// <summary><see cref="FeatureBiomeRow"/> before the residency test: the row of the biome at (x, z), or -1 when it has none. Fixed per
    /// position; <see cref="FeatureBiomeRow"/> is this when <see cref="ResidentBiomeBits"/> has the row's bit, else -1.</summary>
    public int FeatureBiomeRowAny(float x, float z) =>
        biomeMap is not null && rowOf.TryGetValue(biomeMap.At(x, z), out int row) ? row : -1;

    /// <summary>A bit per resident biome row (row r: word r / 32, bit r % 32); rows beyond the span are left out (there are fewer than 254).</summary>
    public void ResidentBiomeBits(Span<uint> bits)
    {
        bits.Clear();
        for (int r = 0; r < state.Length && r < bits.Length * 32; r++)
            if (state[r].Resident) bits[r >> 5] |= 1u << (r & 31);
    }

    void BuildBiomes()
    {
        if (biomes.Count == 0) return;
        // The cell table (GL RGBA8UI, nearest, clamped), written whole by UploadCells.
        cellTexture = Make(GlConventions.VkFormat(InternalFormat.Rgba8ui), CellsX * 2, CellsZ, 1, 1, TextureMinFilter.Nearest, TextureMagFilter.Nearest,
            TextureWrapMode.ClampToEdge, "terrain cells");
        UploadCells();

        // Layers are handed out as texture pairs become resident; the arrays are allocated whole, with every mip level.
        diffuseArray = AllocateArray(true);
        normalArray = AllocateArray(false);

        // One parameter row per biome; its layer indices are written when the biome becomes resident.
        paramTexture = Make(GlConventions.VkFormat(InternalFormat.Rgba32f), TerrainShaders.ParamTexels, biomes.Count, 1, 1, TextureMinFilter.Nearest, TextureMagFilter.Nearest,
            TextureWrapMode.ClampToEdge, "terrain biome parameters");
        using (var batch = Gpu.Uploads.Begin())
            batch.Write(paramTexture.Texture, 0, 0, Rect(0, 0, TerrainShaders.ParamTexels, biomes.Count), new byte[biomes.Count * TerrainShaders.ParamTexels * 16]);

        var blend = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install.DataDirectory, TerrainMaps.BlendMap)));
        blendTexture = Rgba8(blend.Width, blend.Height, blend.Pixels, mipmaps: false, "terrain blend map");
        HasBiomes = true;
    }

    static Silk.NET.Vulkan.Rect2D Rect(int x, int y, int width, int height) => new(new(x, y), new((uint)width, (uint)height));

    /// <summary>A native texture with the GL sampler state the GL one had (nothing written yet; its transition goes with the next uploads).</summary>
    TerrainTexture Make(Silk.NET.Vulkan.Format format, int width, int height, int levels, int layers, TextureMinFilter min, TextureMagFilter mag,
        TextureWrapMode wrap, string name, float anisotropy = 1, TextureKind kind = TextureKind.Texture2D)
    {
        using var batch = Gpu.Uploads.Begin();
        var t = batch.Create(new TextureDesc(format, width, height, levels, layers, Kind: kind, Name: name));
        return new TerrainTexture(Gpu, t, min, mag, wrap, anisotropy);
    }

    /// <summary>
    /// A 2D RGBA8 texture from top-first rows, as <c>WorldGl.Texture2D</c> made it: with <paramref name="mipmaps"/> the whole chain made on the
    /// GPU as GL's GenerateMipmap made it in VkGl (<see cref="CommandList.GenerateMips"/>) and trilinear filtering, else one level, linear;
    /// clamped to the edge.
    /// </summary>
    TerrainTexture Rgba8(int width, int height, byte[] rgba, bool mipmaps, string name)
    {
        int levels = mipmaps ? 1 + (int)Math.Floor(Math.Log2(Math.Max(Math.Max(width, height), 1))) : 1;
        using var batch = Gpu.Uploads.Begin();
        var t = batch.Create(new TextureDesc(GlConventions.VkFormat(InternalFormat.Rgba8), width, height, levels,
            Use: TextureUse.Sampled | TextureUse.TransferDst | TextureUse.TransferSrc, Name: name));
        batch.Write(t, 0, 0, Rect(0, 0, width, height), rgba.AsSpan(0, width * height * 4));
        if (mipmaps) batch.Commands.GenerateMips(t);
        return new TerrainTexture(Gpu, t, mipmaps ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge);
    }

    /// <summary>Diffuse layers are stored as BC3 (colour + gloss in alpha), normal layers as BC1 (their alpha is not used): see docs/viewer.md.</summary>
    static InternalFormat FormatOf(bool diffuse) => diffuse ? InternalFormat.CompressedRgbaS3TCDxt5Ext : InternalFormat.CompressedRgbS3TCDxt1Ext;
    static int BlockBytes(bool diffuse) => diffuse ? 16 : 8;

    /// <summary>Bytes of one array slice at a mip level.</summary>
    int SliceBytes(bool diffuse, int level)
    {
        int s = Math.Max(layerSize >> level, 1);
        return (s + 3) / 4 * ((s + 3) / 4) * BlockBytes(diffuse);
    }

    /// <summary>GPU memory of both layer arrays (all slots, all mip levels).</summary>
    public long ArrayBytes => Enumerable.Range(0, levelCount).Sum(l => (long)(SliceBytes(true, l) + SliceBytes(false, l))) * Math.Max(Capacity, 1);

    /// <summary>One layer array, every slot and mip level, trilinear and 8x anisotropic, repeating (the slices are written as pairs arrive).</summary>
    TerrainTexture? AllocateArray(bool diffuse)
    {
        try
        {
            return Make(GlConventions.VkFormat(FormatOf(diffuse)), layerSize, layerSize, levelCount, Math.Max(Capacity, 1), TextureMinFilter.LinearMipmapLinear,
                TextureMagFilter.Linear, TextureWrapMode.Repeat, diffuse ? "terrain textures diffuse" : "terrain textures normal", anisotropy: 8, kind: TextureKind.Texture2DArray);
        }
        catch (Meitou.Rendering.Vulkan.Core.VulkanException e)
        {
            Messages.Add($"terrain layer array allocation: {e.Message}; try a smaller --layer-size");
            return null;
        }
    }

    // ---- per-frame streaming -------------------------------------------------------------------------------

    /// <summary>
    /// Call once per frame from the GL thread: finds the biomes the cells within <paramref name="materialDistance"/> of
    /// the eye use, starts decoding the ones not resident (nearest first), queues the uploads of what finished, and
    /// moves the overlay/colour window when the eye has left the middle of it.
    /// </summary>
    public void Update(Vector3 eye, float materialDistance, UploadQueue uploads)
    {
        frame++;
        while (problems.TryDequeue(out var m))
            if (printed++ < 20) Console.WriteLine($"warning   {m}");
        if (HasBiomes)
        {
            while (decoded.TryDequeue(out var d)) Install(d, uploads);
            UpdateBiomes(eye, materialDistance);
        }
        UpdateMaps(eye, uploads);
        if (cellsDirty) UploadCells();
    }

    void UpdateBiomes(Vector3 eye, float materialDistance)
    {
        stamp++;
        needed.Clear();
        double cs = info.CellSize, half = WorldLayout.HalfWorldSize;
        double radius = materialDistance + cs / 2;
        int cx0 = (int)Math.Clamp(Math.Floor((eye.X - radius + half) / cs), 0, CellsX - 1), cx1 = (int)Math.Clamp(Math.Floor((eye.X + radius + half) / cs), 0, CellsX - 1);
        int cz0 = (int)Math.Clamp(Math.Floor((eye.Z - radius + half) / cs), 0, CellsZ - 1), cz1 = (int)Math.Clamp(Math.Floor((eye.Z + radius + half) / cs), 0, CellsZ - 1);
        var nearest = new Dictionary<int, double>();
        for (int cz = cz0; cz <= cz1; cz++)
            for (int cx = cx0; cx <= cx1; cx++)
            {
                double x0 = cx * cs - half, z0 = cz * cs - half;
                double dx = Math.Max(Math.Max(x0 - eye.X, eye.X - (x0 + cs)), 0), dz = Math.Max(Math.Max(z0 - eye.Z, eye.Z - (z0 + cs)), 0);
                double d = Math.Sqrt(dx * dx + dz * dz);
                if (d > radius) continue;
                for (int k = 0; k < BlendInfoFile.SlotsPerCell; k++)
                    if (rowOf.TryGetValue(info.Slot(cx, cz, k), out int row) && (!nearest.TryGetValue(row, out double best) || d < best)) nearest[row] = d;
            }
        needed.AddRange(nearest.OrderBy(p => p.Value).Select(p => p.Key));
        foreach (int b in needed)
            foreach (var p in state[b].Pairs)
            {
                p.Needed = stamp;
                p.Used = frame;
            }
        foreach (int b in needed)
            foreach (var p in state[b].Pairs)
            {
                if (inFlight >= maxInFlight) return;
                if (p.Slot >= 0 || p.Loading || p.Failed) continue;
                Start(p);
            }
    }

    void Start(Pair pair)
    {
        pair.Loading = true;
        inFlight++;
        BackgroundWork.Run(() =>
        {
            Decoded result;
            try
            {
                var local = new List<string>();
                var d = LoadLayer(assets, pair.Diffuse, layerSize, true, [128, 128, 128, 60], local);
                var n = LoadLayer(assets, pair.Normal, layerSize, false, [128, 128, 255, 255], local);
                foreach (var m in local) problems.Enqueue(m);
                result = new Decoded(pair, d, n);
            }
            catch (Exception e)
            {
                problems.Enqueue($"terrain texture {pair.Diffuse}: {e.Message}");
                result = new Decoded(pair, null, null);
            }
            decoded.Enqueue(result);
        });
    }

    /// <summary>
    /// Upload steps of a decoded pair: the first takes a free (or the least recently needed) slot of both arrays, then each large
    /// mip level of each array is one step, and the last makes the pair resident. (The pair stays "loading" until then.)
    /// </summary>
    void Install(Decoded d, UploadQueue uploads)
    {
        var pair = d.Pair;
        if (d.Diffuse is null || d.Normal is null) { pair.Loading = false; inFlight--; pair.Failed = true; return; }
        int slot = AllocateSlot();
        if (slot < 0)
        {
            pair.Loading = false;
            inFlight--;
            pair.Failed = true;
            if (!capacityWarned)
            {
                capacityWarned = true;
                Console.WriteLine($"warning   terrain layer arrays full ({Capacity} texture pairs): some biomes stay in their ground colour");
            }
            return;
        }
        foreach (var (diffuse, levels) in new[] { (true, d.Diffuse), (false, d.Normal) })
        {
            int from = 0;
            for (int level = 0; level < levels.Length; level++)
            {
                // Small levels go together with the next large one; each step is at most about one level of 2 to 4 MB.
                bool last = level == levels.Length - 1;
                if (levels[level].Length < 256 << 10 && !last) continue;
                int a = from, b = level;
                from = level + 1;
                uploads.Add(() => UploadLevels(diffuse, levels, slot, a, b), "layer upload");
            }
        }
        uploads.Add(() =>
        {
            inFlight--;
            pair.Loading = false;
            pair.Slot = slot;
            owner[slot] = pair;
            foreach (int b in usedBy[pair]) Refresh(b);
        }, "pair resident");
    }

    void UploadLevels(bool diffuse, byte[][] levels, int slot, int from, int to)
    {
        if ((diffuse ? diffuseArray : normalArray) is not { } array) return;
        using var batch = Gpu.Uploads.Begin();
        for (int level = from; level <= to; level++)
        {
            int s = Math.Max(layerSize >> level, 1);
            batch.Write(array.Texture, level, slot, Rect(0, 0, s, s), levels[level]);
        }
    }

    int AllocateSlot()
    {
        if (nextFree < Capacity) return nextFree++;
        Pair? victim = null;
        foreach (var p in owner)
            if (p is not null && p.Needed != stamp && (victim is null || p.Used < victim.Used)) victim = p;
        if (victim is null) return -1;
        int slot = victim.Slot;
        victim.Slot = -1;
        owner[slot] = null;
        foreach (int b in usedBy[victim])
            if (state[b].Resident) { state[b].Resident = false; cellsDirty = true; }
        return slot;
    }

    /// <summary>A biome becomes resident once all its texture pairs are: its parameter row is written then.</summary>
    void Refresh(int b)
    {
        var s = state[b];
        if (s.Resident || s.Pairs.Any(p => p.Slot < 0)) return;
        var bt = biomes[b];
        var tl = bt.Tiling;
        var l = s.Pairs.Select(p => (float)p.Slot).ToArray();
        float k = bt.DistortWavelength > 0 ? 1 / bt.DistortWavelength : 0;
        Vector4[] row =
        [
            new(l[0], l[1], l[2], l[3]),
            new(l[4], l[5], bt.BrightnessFix, 0),
            new(tl[1].X, tl[1].Y, tl[2].X, tl[2].Y),   // slope, cliff
            new(tl[0].X, tl[0].Y, tl[3].X, tl[3].Y),   // base, grass
            new(tl[4].X, tl[4].Y, tl[5].X, tl[5].Y),   // dirt, road
            bt.SlopeMin, bt.SlopeMax, bt.SlopeBlend, bt.OverlayMult,
            new(bt.GroundColour, bt.FadeDistance > 0 ? 1 / bt.FadeDistance : 0),
            new(k, bt.DistortAmplitude * 0.01f, 0, 0),
        ];
        var data = new float[row.Length * 4];
        for (int i = 0; i < row.Length; i++) (data[i * 4], data[i * 4 + 1], data[i * 4 + 2], data[i * 4 + 3]) = (row[i].X, row[i].Y, row[i].Z, row[i].W);
        using (var batch = Gpu.Uploads.Begin())
            batch.Write(paramTexture!.Texture, 0, 0, Rect(0, b, row.Length, 1), System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan()));
        s.Resident = true;
        cellsDirty = true;
    }

    /// <summary>Cell table, two texels per cell: slots 0..3, then slot 4 in R; a resident biome's row, <see cref="Pending"/> or <see cref="Unused"/>.</summary>
    void UploadCells()
    {
        cellsDirty = false;
        var cells = new byte[CellsX * 2 * CellsZ * 4];
        Array.Fill(cells, Unused);
        for (int cz = 0; cz < CellsZ; cz++)
            for (int cx = 0; cx < CellsX; cx++)
                for (int k = 0; k < BlendInfoFile.SlotsPerCell; k++)
                {
                    uint colour = info.Slot(cx, cz, k);
                    byte v = colour == 0 ? Unused : rowOf.TryGetValue(colour, out int row) ? (state[row].Resident ? (byte)row : Pending) : Unused;
                    cells[(cz * CellsX * 2 + cx * 2) * 4 + k] = v;
                }
        if (cellTexture is null) return;
        using var batch = Gpu.Uploads.Begin();
        batch.Write(cellTexture.Texture, 0, 0, Rect(0, 0, CellsX * 2, CellsZ), cells);
    }

    /// <summary>
    /// One layer texture as the blocks of its array slice: every mip level down to 1 × 1, BC3 (<paramref name="diffuse"/>) or BC1.
    /// A square power-of-two DDS in BC1 or BC3 that is at least <paramref name="size"/> wide and has the mips is taken as stored
    /// (levels from <paramref name="size"/> down; the other of BC1/BC3 by a block conversion); anything else is decoded, scaled to
    /// size × size, given a mip chain and encoded again (<see cref="BlockCompression"/>). A missing texture is a constant colour.
    /// </summary>
    static byte[][] LoadLayer(AssetLocator assets, string? name, int size, bool diffuse, byte[] fallback, List<string> problems)
    {
        int levelCount = (int)Math.Log2(size) + 1;
        RgbaImage? image = null;
        if (name is not null)
        {
            var path = assets.Find(name) ?? assets.Find(Path.GetFileName(name.Replace('\\', '/')));
            if (path is null) problems.Add($"terrain texture not found: {name}");
            else
                try
                {
                    var bytes = File.ReadAllBytes(path);
                    if (bytes.Length >= 4 && BitConverter.ToUInt32(bytes, 0) == DdsReader.Magic)
                    {
                        var dds = DdsReader.Read(bytes);
                        if (AsStored(dds, size, levelCount, diffuse) is { } stored) return stored;
                        int level = 0;
                        while (level + 1 < dds.MipCount && Math.Max(dds.Width >> (level + 1), 1) >= size) level++;
                        image = DdsDecoder.Decode(dds, 0, level);
                    }
                    else image = TextureLoader.LoadImage(bytes);
                }
                catch (Exception e) when (e is DdsFormatException or InvalidOperationException or IOException or ArgumentException)
                {
                    problems.Add($"terrain texture {name}: {e.Message}");
                }
        }
        if (image is null)
        {
            var colour = new byte[64];
            for (int i = 0; i < colour.Length; i += 4) fallback.CopyTo(colour, i);
            var block = diffuse ? BlockCompression.EncodeBc3(colour, 4, 4) : BlockCompression.EncodeBc1(colour, 4, 4);
            var constant = new byte[levelCount][];
            for (int l = 0; l < levelCount; l++)
            {
                int s = Math.Max(size >> l, 1), blocks = (s + 3) / 4 * ((s + 3) / 4);
                constant[l] = new byte[blocks * block.Length];
                for (int b = 0; b < blocks; b++) block.CopyTo(constant[l], b * block.Length);
            }
            return constant;
        }
        var result = new byte[size * size * 4];
        // Box filter when shrinking, nearest when growing.
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int sx0 = x * image.Width / size, sx1 = Math.Max(sx0 + 1, (x + 1) * image.Width / size);
                int sy0 = y * image.Height / size, sy1 = Math.Max(sy0 + 1, (y + 1) * image.Height / size);
                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (int sy = sy0; sy < sy1; sy++)
                    for (int sx = sx0; sx < sx1; sx++)
                    {
                        int o = (sy * image.Width + sx) * 4;
                        r += image.Pixels[o]; g += image.Pixels[o + 1]; b += image.Pixels[o + 2]; a += image.Pixels[o + 3]; n++;
                    }
                int d = (y * size + x) * 4;
                (result[d], result[d + 1], result[d + 2], result[d + 3]) = ((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
            }
        var mips = Mips(result, size, size);
        var encoded = new byte[mips.Length][];
        for (int l = 0; l < mips.Length; l++)
        {
            int s = Math.Max(size >> l, 1);
            encoded[l] = diffuse ? BlockCompression.EncodeBc3(mips[l], s, s) : BlockCompression.EncodeBc1(mips[l], s, s);
        }
        return encoded;
    }

    /// <summary>The levels of a DDS that can go into the array as stored (see <see cref="LoadLayer"/>), or null.</summary>
    static byte[][]? AsStored(DdsFile dds, int size, int levelCount, bool diffuse)
    {
        if (dds.Format is not (DdsFormat.Bc1 or DdsFormat.Bc3) || dds.IsCubemap || dds.IsVolume || dds.ImageCount != 1) return null;
        if (dds.Width != dds.Height || dds.Width < size || !int.IsPow2(dds.Width)) return null;
        int shift = (int)Math.Log2(dds.Width / size);
        if (shift + levelCount > dds.MipCount || dds.Surfaces.Count < dds.MipCount) return null;
        var levels = new byte[levelCount][];
        for (int l = 0; l < levelCount; l++)
        {
            var surface = dds.Surface(0, shift + l);
            int expected = (dds.Format == DdsFormat.Bc1 ? 8 : 16) * ((surface.Width + 3) / 4) * ((surface.Height + 3) / 4);
            if (surface.Length < expected) return null;
            var data = dds.Data.AsSpan(surface.Offset, expected);
            levels[l] = (dds.Format, diffuse) switch
            {
                (DdsFormat.Bc3, true) or (DdsFormat.Bc1, false) => data.ToArray(),
                (DdsFormat.Bc1, true) => BlockCompression.Bc1ToBc3(data),
                _ => BlockCompression.Bc3ToBc1(data),
            };
        }
        return levels;
    }

    /// <summary>The full mip chain of an RGBA8 image (power-of-two sides) by 2 Ã— 2 box filtering; level 0 is the image itself.</summary>
    internal static byte[][] Mips(byte[] baseLevel, int width, int height, int maxLevels = int.MaxValue, bool pooled = false)
    {
        int count = Math.Min((int)Math.Log2(Math.Max(width, height)) + 1, maxLevels);
        var levels = new byte[count][];
        levels[0] = baseLevel;
        for (int l = 1; l < count; l++)
        {
            int w = Math.Max(width >> l, 1), h = Math.Max(height >> l, 1), pw = Math.Max(width >> (l - 1), 1), ph = Math.Max(height >> (l - 1), 1);
            var src = levels[l - 1];
            var dst = pooled ? ArrayPool<byte>.Shared.Rent(w * h * 4) : new byte[w * h * 4];
            Parallel.For(0, h, y =>
            {
                int y1 = Math.Min(2 * y + 1, ph - 1);
                for (int x = 0; x < w; x++)
                {
                    int x1 = Math.Min(2 * x + 1, pw - 1);
                    for (int c = 0; c < 4; c++)
                        dst[(y * w + x) * 4 + c] = (byte)((src[(2 * y * pw + 2 * x) * 4 + c] + src[(2 * y * pw + x1) * 4 + c] +
                            src[(y1 * pw + 2 * x) * 4 + c] + src[(y1 * pw + x1) * 4 + c] + 2) / 4);
                }
            });
            levels[l] = dst;
        }
        return levels;
    }

    internal static byte[][] Mips(byte[] baseLevel, int size) => Mips(baseLevel, size, size);

    // ---- overlay and colour windows -----------------------------------------------------------------------
    // Two textures addressed toroidally: texel (X mod W) holds pixel X of the window, so moving the window rewrites
    // only the strips that came into view, in place. (A fresh texture per window stalled the driver for ~50 ms at
    // its first use.) Mips stop at level 6, where a texel is 64 map pixels: the window origin is aligned to that.

    /// <summary>Mip levels of the window textures (0..6).</summary>
    const int MapLevels = 7;

    int validPx = int.MinValue, validPz = int.MinValue;

    void AllocateMaps()
    {
        overlayTexture = EmptyMap(mapWindows!.OverlayPixels, MapLevels, "terrain overlay map");
        colourTexture = EmptyMap(mapWindows.ColourPixels, MapLevels, "terrain colour map");
    }

    void UpdateMaps(Vector3 eye, UploadQueue uploads)
    {
        if (mapWindows is null || MapState == 0) return;
        if (mapJob is { IsCompleted: true } job)
        {
            mapJob = null;
            MapWindows.Update? result = null;
            try { result = job.Result; }
            catch (AggregateException e) { Console.WriteLine($"warning   overlay maps: {e.InnerException?.Message ?? e.Message}"); }
            if (result is null) { if (MapState == 1) MapState = 0; }
            else BeginMapUpload(result, uploads);
        }
        if (mapJob is not null || mapUploading) return;
        var (px, pz) = mapWindows.DesiredOrigin(eye.X, eye.Z);
        bool far = Math.Abs(px - validPx) > mapWindows.RecentreOverlayPixels || Math.Abs(pz - validPz) > mapWindows.RecentreOverlayPixels;
        if (MapState == 2 && !far) return;
        (int, int)? old = MapState == 2 ? (validPx, validPz) : null;
        mapJob = BackgroundWork.Run(() => mapWindows.Compose(px, pz, old));
    }

    void BeginMapUpload(MapWindows.Update update, UploadQueue uploads)
    {
        mapUploading = true;
        // Every piece comes with its mip levels from the worker and goes up in slabs of about 2 MB.
        foreach (var piece in update.Pieces)
            for (int level = 0; level < piece.Levels.Length; level++)
            {
                var (p, lv) = (piece, level);
                int w = Math.Max(p.Width >> lv, 1), h = Math.Max(p.Height >> lv, 1), x = p.X >> lv, z = p.Z >> lv;
                int rows = Math.Max(1, (2 << 20) / (w * 4));
                for (int row = 0; row < h; row += rows)
                {
                    int r0 = row, n = Math.Min(rows, h - row);
                    uploads.Add(() =>
                    {
                        using var batch = Gpu.Uploads.Begin();
                        batch.Write((p.Colour ? colourTexture : overlayTexture)!.Texture, lv, 0, Rect(x, z + r0, w, n), p.Levels[lv].AsSpan(r0 * w * 4, n * w * 4));
                    }, "map slab");
                }
            }
        uploads.Add(() =>
        {
            Region = mapWindows!.RegionOf(update.Px0, update.Pz0);
            (validPx, validPz) = (update.Px0, update.Pz0);
            MapState = 2;
            mapUploading = false;
            update.Release();
        }, "map swap");
    }

    /// <summary>A window texture: <paramref name="levels"/> levels (the GL one had more allocated but sampled only these), trilinear, repeating.</summary>
    TerrainTexture EmptyMap(int size, int levels, string name) =>
        Make(GlConventions.VkFormat(InternalFormat.Rgba8), size, size, levels, 1, TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear, TextureWrapMode.Repeat, name);

    // ---- whole-world maps ----------------------------------------------------------------------------------

    /// <summary>
    /// Whole-world maps for the far terrain: the biomes' ground colour × brightness fix blended by the blend map
    /// (what the textured layers fade to, and finer than the game's 128² <c>distant.png</c>), and the colour map
    /// shrunk to <paramref name="colourSize"/>² (0: none).
    /// </summary>
    void BuildWorld(GameInstall install, GameDatabase db, Dictionary<uint, BiomeTerrain> all, int colourSize)
    {
        var blend = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install.DataDirectory, TerrainMaps.BlendMap)));
        var field = BiomeField.Bake(info, blend.Width, blend.Height, blend.Pixels,
            c => all.TryGetValue(c, out var b) ? new Vector4(b.GroundColour * b.BrightnessFix, 1) : null, new Vector4(0.6f, 0.55f, 0.45f, 1));
        var rgba = new byte[field.Length * 4];
        for (int i = 0; i < field.Length; i++)
        {
            var v = Vector4.Clamp(field[i], Vector4.Zero, Vector4.One) * 255;
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = ((byte)v.X, (byte)v.Y, (byte)v.Z, 255);
        }
        groundTexture = Rgba8(blend.Width, blend.Height, rgba, mipmaps: true, "terrain ground colour");
        HasGround = true;
        if (colourSize <= 0) return;

        // 8 x 8 colour tiles, each box-filtered down to colourSize / 8.
        int part = colourSize / TerrainMaps.OverlayTiles;
        var colour = new byte[colourSize * colourSize * 4];
        Parallel.For(0, TerrainMaps.OverlayTiles * TerrainMaps.OverlayTiles, t =>
        {
            int tx = t % TerrainMaps.OverlayTiles, tz = t / TerrainMaps.OverlayTiles;
            var path = TerrainMaps.OverlayTile(install, "colour", tx, tz);
            if (!File.Exists(path)) return;
            var img = TextureLoader.LoadImage(File.ReadAllBytes(path));
            int f = Math.Max(1, img.Width / part);
            for (int y = 0; y < part; y++)
                for (int x = 0; x < part; x++)
                {
                    int r = 0, g = 0, b = 0, n = 0;
                    for (int sy = y * f; sy < Math.Min((y + 1) * f, img.Height); sy++)
                        for (int sx = x * f; sx < Math.Min((x + 1) * f, img.Width); sx++)
                        {
                            int o = (sy * img.Width + sx) * 4;
                            r += img.Pixels[o]; g += img.Pixels[o + 1]; b += img.Pixels[o + 2]; n++;
                        }
                    if (n == 0) continue;
                    int d = ((tz * part + y) * colourSize + tx * part + x) * 4;
                    (colour[d], colour[d + 1], colour[d + 2], colour[d + 3]) = ((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
                }
        });
        worldColourTexture = Rgba8(colourSize, colourSize, colour, mipmaps: true, "terrain world colour");
        HasWorldColour = true;
    }

    /// <summary>The terrain material's textures (null where one does not exist yet), for the native draws that read them by bindless index.</summary>
    internal readonly record struct TextureSet(TerrainTexture? Diffuse, TerrainTexture? Normal, TerrainTexture? Params, TerrainTexture? Cells, TerrainTexture? BlendMap,
        TerrainTexture? Overlay, TerrainTexture? Colour, TerrainTexture? Ground, TerrainTexture? WorldColour);

    internal TextureSet Textures => new(diffuseArray, normalArray, paramTexture, cellTexture, blendTexture, overlayTexture, colourTexture, groundTexture, worldColourTexture);

    public void Dispose()
    {
        foreach (var t in new[] { diffuseArray, normalArray, paramTexture, cellTexture, blendTexture, overlayTexture, colourTexture, groundTexture, worldColourTexture })
            t?.Dispose();
    }
}

/// <summary>
/// A native texture of the terrain's (phase 8, docs/renderer-native.md 8) sampled as it was as a GL texture through VkGl: the GL sampler
/// state it was given (<see cref="SamplerDesc.FromGl"/>, the upscaler's LOD bias on mipmapped filters, as VkGl's <c>SamplerFor</c>) and the
/// view of all its levels (it is made with exactly the levels VkGl's view covered). <see cref="Bindless"/> mirrors <c>IGlInterop.Bindless</c>:
/// the same index while the sampler is unchanged, a new one (the old freed after the frames in flight) when the LOD bias moved. Render thread only.
/// </summary>
internal sealed class TerrainTexture : IDisposable
{
    readonly GpuContext ctx;
    readonly TextureMinFilter min;
    readonly TextureMagFilter mag;
    readonly TextureWrapMode wrap;
    readonly float anisotropy;
    readonly bool integer;
    float cachedBias = float.NaN;
    SampledTexture cached;
    BindlessHandle handle;
    SampledTexture registered;
    bool hasEntry;

    public TerrainTexture(GpuContext ctx, Texture texture, TextureMinFilter min, TextureMagFilter mag, TextureWrapMode wrap, float anisotropy = 1)
    {
        this.ctx = ctx;
        Texture = texture;
        (this.min, this.mag, this.wrap, this.anisotropy) = (min, mag, wrap, anisotropy);
        integer = GlConventions.IsIntegerFormat(texture.Desc.Format);
    }

    public Texture Texture { get; }

    /// <summary>The sampler and view a draw samples it with now (what VkGl's <c>Sampled</c> gave for the GL texture).</summary>
    public SampledTexture Sampled()
    {
        float bias = ctx.LodBias();
        if (!(bias == cachedBias))
        {
            var sampler = ctx.Samplers.Get(SamplerDesc.FromGl(min, mag, wrap, wrap, TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, anisotropy, integer, bias));
            (cached, cachedBias) = (new SampledTexture(sampler, Texture.View(), Texture.Image), bias);
        }
        return cached;
    }

    /// <summary>Its bindless entry for <see cref="Sampled"/> now (usable in the current frame). In Prepare, on the render thread.</summary>
    public BindlessHandle Bindless()
    {
        var s = Sampled();
        if (hasEntry && registered == s) return handle;
        if (hasEntry) ctx.Bindless.Free(handle);
        var kind = BindlessTable.KindFor(Texture.Desc.Format, Texture.Desc.Kind);
        (handle, registered, hasEntry) = (new BindlessHandle(kind, ctx.Bindless.Register(kind, s)), s, true);
        return handle;
    }

    public void Dispose()
    {
        if (hasEntry) ctx.Bindless.Free(handle);
        hasEntry = false;
        Texture.Dispose();
    }
}

/// <summary>
/// The overlay (36 units per pixel) and colour (18) maps of <c>overlaymaps/</c>, 8 × 8 tiles each, cut into windows
/// of the same world square on a worker thread, with a small cache of decoded tiles so a moving window re-reads little.
/// </summary>
sealed class MapWindows
{
    /// <summary>Window edge in overlay pixels (36 units each) and the alignment of its origin (the mip level 6 texel).</summary>
    const int WindowPixels = 2048, Align = 64;

    readonly TileCache overlay, colour;
    readonly int overlayTile, colourTile, ratio;
    readonly double overlayUnit;

    MapWindows(TileCache overlay, TileCache colour, int overlayTile, int colourTile)
    {
        this.overlay = overlay;
        this.colour = colour;
        this.overlayTile = overlayTile;
        this.colourTile = colourTile;
        overlayUnit = (double)WorldLayout.WorldSize / (overlayTile * TerrainMaps.OverlayTiles);
        ratio = Math.Max((int)Math.Round((double)colourTile / overlayTile), 1);
        if (ratio * overlayTile != colourTile) throw new InvalidDataException($"colour tiles ({colourTile}) are not a whole multiple of overlay tiles ({overlayTile})");
    }

    public static MapWindows Open(GameInstall install, List<string> messages)
    {
        TileCache Cache(string kind, int max, out int size)
        {
            var first = TerrainMaps.OverlayTile(install, kind, 0, 0);
            if (!File.Exists(first)) throw new FileNotFoundException($"missing {first}");
            var cache = new TileCache(install, kind, max);
            size = cache.Get(0, 0)?.Width ?? throw new InvalidDataException($"cannot read {first}");
            return cache;
        }
        var o = Cache("new_overlay", 24, out int ot);
        var c = Cache("colour", 12, out int ct);
        return new MapWindows(o, c, ot, ct);
    }

    public int OverlayPixels => Math.Min(WindowPixels, overlayTile * TerrainMaps.OverlayTiles);
    public int ColourPixels => OverlayPixels * ratio;

    /// <summary>When the window's origin (overlay pixels) differs by more than this from the desired one, it moves (4608 units).</summary>
    public int RecentreOverlayPixels => Math.Max((int)(4608 / overlayUnit), Align);

    /// <summary>Origin (overlay pixels) of a window centred on a point, aligned and kept inside the maps.</summary>
    public (int X, int Z) DesiredOrigin(double x, double z)
    {
        int full = overlayTile * TerrainMaps.OverlayTiles, size = OverlayPixels;
        int Axis(double v) => Math.Clamp((int)Math.Round(((v + WorldLayout.HalfWorldSize) / overlayUnit - size / 2.0) / Align) * Align, 0, full - size);
        return (Axis(x), Axis(z));
    }

    /// <summary>x0, z0, 1/width, 1/depth in world units of the window at an origin.</summary>
    public Vector4 RegionOf(int px, int pz) =>
        new((float)(px * overlayUnit - WorldLayout.HalfWorldSize), (float)(pz * overlayUnit - WorldLayout.HalfWorldSize),
            (float)(1 / (OverlayPixels * overlayUnit)), (float)(1 / (OverlayPixels * overlayUnit)));

    /// <summary>A rectangle of one texture: its first texel (already wrapped) and the mip levels of its pixels.</summary>
    public sealed record Piece(bool Colour, int X, int Z, int Width, int Height, byte[][] Levels);

    /// <summary>What to write to the textures to show the window at (<see cref="Px0"/>, <see cref="Pz0"/>).</summary>
    public sealed record Update(int Px0, int Pz0, List<Piece> Pieces)
    {
        /// <summary>Returns the levels (rented from the shared pool, sized at least as needed) once they are uploaded.</summary>
        public void Release() { foreach (var p in Pieces) foreach (var l in p.Levels) ArrayPool<byte>.Shared.Return(l); }
    }

    /// <summary>
    /// The pixels of the window at (<paramref name="px0"/>, <paramref name="pz0"/>) that the window at
    /// <paramref name="old"/> (if any) does not hold, cut from the tiles with their mips (worker thread).
    /// </summary>
    public Update Compose(int px0, int pz0, (int X, int Z)? old)
    {
        int size = OverlayPixels;
        var rects = new List<(int X, int Z, int W, int H)>();
        if (old is not { } o || Math.Abs(px0 - o.X) >= size || Math.Abs(pz0 - o.Z) >= size) rects.Add((px0, pz0, size, size));
        else
        {
            int dx = px0 - o.X, dz = pz0 - o.Z;
            if (dx > 0) rects.Add((o.X + size, pz0, dx, size));
            else if (dx < 0) rects.Add((px0, pz0, -dx, size));
            int cx0 = Math.Max(px0, o.X), cx1 = Math.Min(px0, o.X) + size;   // columns both windows share
            if (dz > 0) rects.Add((cx0, o.Z + size, cx1 - cx0, dz));
            else if (dz < 0) rects.Add((cx0, pz0, cx1 - cx0, -dz));
        }
        var pieces = new List<Piece>();
        int cs = size * ratio;
        foreach (var (rx, rz, rw, rh) in rects)
            foreach (var (sx, sw) in Wrap(rx, rw, size))
                foreach (var (sz, sh) in Wrap(rz, rh, size))
                {
                    var data = Cut(overlay, overlayTile, sx, sz, sw, sh);
                    pieces.Add(new Piece(false, sx % size, sz % size, sw, sh, TerrainTextures.Mips(data, sw, sh, 7, pooled: true)));
                    var cdata = Cut(colour, colourTile, sx * ratio, sz * ratio, sw * ratio, sh * ratio);
                    pieces.Add(new Piece(true, sx * ratio % cs, sz * ratio % cs, sw * ratio, sh * ratio, TerrainTextures.Mips(cdata, sw * ratio, sh * ratio, 7, pooled: true)));
                }
        return new Update(px0, pz0, pieces);
    }

    /// <summary>Splits [start, start + length) at multiples of <paramref name="period"/> (the texture wraps there).</summary>
    static IEnumerable<(int Start, int Length)> Wrap(int start, int length, int period)
    {
        while (length > 0)
        {
            int n = Math.Min(length, period - start % period);
            yield return (start, n);
            start += n;
            length -= n;
        }
    }

    static byte[] Cut(TileCache cache, int tile, int x0, int z0, int w, int h)
    {
        var result = ArrayPool<byte>.Shared.Rent(w * h * 4);   // returned with the update's other levels once uploaded
        Array.Clear(result, 0, w * h * 4);
        int tx0 = x0 / tile, tx1 = (x0 + w - 1) / tile, tz0 = z0 / tile, tz1 = (z0 + h - 1) / tile;
        var keys = new List<(int, int)>();
        for (int tz = tz0; tz <= tz1; tz++)
            for (int tx = tx0; tx <= tx1; tx++) keys.Add((tx, tz));
        var images = new Dictionary<(int, int), RgbaImage?>();
        Parallel.ForEach(keys, key =>
        {
            var img = cache.Get(key.Item1, key.Item2);
            lock (images) images[key] = img;
        });
        foreach (var (tx, tz) in keys)
        {
            var img = images[(tx, tz)];
            if (img is null) continue;
            int ax0 = Math.Max(x0, tx * tile), ax1 = Math.Min(x0 + w, (tx + 1) * tile);
            int az0 = Math.Max(z0, tz * tile), az1 = Math.Min(z0 + h, (tz + 1) * tile);
            for (int z = az0; z < az1; z++)
                Array.Copy(img.Pixels, ((z - tz * tile) * tile + (ax0 - tx * tile)) * 4, result, ((z - z0) * w + (ax0 - x0)) * 4, (ax1 - ax0) * 4);
        }
        cache.Trim(keys);
        return result;
    }

    sealed class TileCache(GameInstall install, string kind, int max)
    {
        readonly Dictionary<(int, int), (RgbaImage? Image, long Used)> tiles = [];
        long clock;

        public RgbaImage? Get(int tx, int tz)
        {
            lock (tiles)
                if (tiles.TryGetValue((tx, tz), out var hit))
                {
                    tiles[(tx, tz)] = (hit.Image, ++clock);
                    return hit.Image;
                }
            var path = TerrainMaps.OverlayTile(install, kind, tx, tz);
            var img = File.Exists(path) ? TextureLoader.LoadImage(File.ReadAllBytes(path)) : null;
            lock (tiles) tiles[(tx, tz)] = (img, ++clock);
            return img;
        }

        /// <summary>Drops the least recently used tiles beyond the cache size, keeping the ones just used.</summary>
        public void Trim(List<(int, int)> keep)
        {
            lock (tiles)
                while (tiles.Count > Math.Max(max, keep.Count))
                {
                    var victim = tiles.Where(p => !keep.Contains(p.Key)).OrderBy(p => p.Value.Used).Select(p => ((int, int)?)p.Key).FirstOrDefault();
                    if (victim is null) break;
                    tiles.Remove(victim.Value);
                }
        }
    }
}
