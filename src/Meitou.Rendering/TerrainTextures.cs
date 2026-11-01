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
    public IGpuContext Gpu { get; }
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
    static readonly bool PrivateStatics = Environment.GetEnvironmentVariable("MEITOU_METAL_PRIVATE_STATIC") == "1";
    TerrainTexture? diffuseArray, normalArray, heightArray, reliefTexture, paramTexture, cellTexture, blendTexture, overlayTexture, colourTexture, groundTexture, worldColourTexture;
    int inFlight, nextFree, printed, stamp;
    long frame;
    bool cellsDirty = true, capacityWarned;
    Task<MapWindows.Update>? mapJob;
    bool mapUploading;

    sealed class Pair
    {
        public string? Diffuse, Normal;
        public int Slot = -1;
        /// <summary>Its height's steepness factor once decoded (the relief texture's value: 1 / (side × steepness), 0 flat), for the field's labels.</summary>
        public float Steep;
        public long Used;
        public int Needed;
        public bool Loading, Failed;
    }

    sealed class BiomeState
    {
        public required Pair[] Pairs;
        public bool Resident;
    }

    sealed record Decoded(Pair Pair, byte[][]? Diffuse, byte[][]? Normal, byte[][]? Height, float Steep = 0);

    /// <summary>Side of the height array's layers (<see cref="GroundRelief"/>): the layer size, at most 512.</summary>
    int HeightSize => Math.Min(layerSize, 512);

    TerrainTextures(IGpuContext gpu, GameInstall install, AssetLocator assets, BlendInfoFile info, BiomeTerrain[] all, int layerSize, MapWindows? maps)
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


    /// <param name="layerSize">Edge length every layer texture is brought to (the arrays need one size).</param>
    /// <summary>
    /// What <see cref="Create"/> reads and works out on the CPU (about 2.5 s at a cold start): the blend information, the biomes, the overlay map windows, the decoded
    /// blend map, the ground colour baked from it and the world colour map box-filtered from the 64 colour tiles, the foliage biome map. Made by <see cref="Prepare"/>
    /// on any thread, so a game can make it on a worker while its window keeps drawing.
    /// </summary>
    public sealed class Source
    {
        internal BlendInfoFile Info = null!;
        internal Dictionary<uint, BiomeTerrain> All = null!;
        internal MapWindows? Maps;
        internal RgbaImage? Blend;
        internal byte[]? Ground, WorldColour;
        internal int WorldColourSize;
        internal FoliageBiomeMap? BiomeMap;
        internal readonly List<string> Messages = [];

        /// <summary>Drops the CPU images after <see cref="Create"/> has uploaded them. They are the largest LOH objects
        /// of a cold start (the blend, ground and world-colour buffers); keeping the source alive kept them rooted.</summary>
        internal void ReleaseUploaded()
        {
            Blend = null;
            Ground = null;
            WorldColour = null;
            BiomeMap = null;
            All = null!;
            Info = null!;
            // Maps stays: the TerrainTextures owns the live map windows.
        }
    }

    /// <summary>The CPU part of <see cref="Create"/>, on any thread (it makes no GPU resource).</summary>
    public static Source Prepare(GameInstall install, GameDatabase db, int worldColourSize = 2048)
    {
        var s = new Source { Info = BlendInfoFile.Open(install), All = BiomeTerrain.ByIndex(db) };
        try { s.Maps = MapWindows.Open(install, s.Messages); }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            s.Messages.Add($"overlay maps: {e.Message}");
        }
        try { s.Blend = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install.DataDirectory, TerrainMaps.BlendMap))); }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            s.Messages.Add($"biome textures: {e.Message}");
        }
        try { PrepareWorld(install, s, worldColourSize); }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        {
            s.Messages.Add($"world maps: {e.Message}");
        }
        try { s.BiomeMap = FoliageBiomeMap.Open(install); }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        {
            s.Messages.Add($"biome map: {e.Message}");
        }
        return s;
    }

    public static TerrainTextures Create(IGpuContext gpu, GameInstall install, GameDatabase db, AssetLocator assets, int layerSize = 512, int worldColourSize = 2048, Source? source = null)
    {
        source ??= Prepare(install, db, worldColourSize);
        var t = new TerrainTextures(gpu, install, assets, source.Info, [.. source.All.Values], layerSize, source.Maps);
        t.Messages.AddRange(source.Messages);
        if (source.Maps is null) t.MapState = 0;
        else
        {
            t.MapState = 1;
            t.AllocateMaps();
        }
        if (source.Blend is { } blend) t.BuildBiomes(blend);
        t.BuildWorld(source);
        t.biomeMap = source.BiomeMap;
        source.ReleaseUploaded();
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

    /// <summary>The biome of parameter row <paramref name="row"/> has all its layer textures resident (its parameter row is written).</summary>
    public bool IsResident(int row) => (uint)row < (uint)state.Length && state[row].Resident;

    /// <summary>
    /// What makes the biome's material what it is, as text for a cache key (docs/impostors.md section 13): its record's id, the six layers' texture names with
    /// the length and write time of the files they resolve to, tiling, slope ranges, overlay strengths, brightness, distortion and the layer size the textures
    /// are brought to. Null for a row outside the table.
    /// </summary>
    public string? BiomeKey(int row)
    {
        if ((uint)row >= (uint)biomes.Count) return null;
        var b = biomes[row];
        var sb = new System.Text.StringBuilder();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        sb.Append(inv, $"{b.StringId}|{layerSize}|{b.BrightnessFix:R}|{b.DistortWavelength:R}|{b.DistortAmplitude:R}|{b.SlopeMin}|{b.SlopeMax}|{b.SlopeBlend}|{b.OverlayMult}|{b.GroundColour}|{b.FadeDistance:R}\n");
        for (int l = 0; l < 6; l++)
        {
            sb.Append(inv, $"{b.Tiling[l]}");
            foreach (var name in (ReadOnlySpan<string?>)[b.Diffuse[l], b.Normal[l]])
            {
                string? path = name is null ? null : assets.Find(name) ?? assets.Find(Path.GetFileName(name.Replace('\\', '/')));
                var info = path is null ? null : new FileInfo(path);
                sb.Append(inv, $"|{name}|{(info is { Exists: true } ? info.Length : -1)}|{(info is { Exists: true } ? info.LastWriteTimeUtc.Ticks : 0)}");
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>A bit per resident biome row (row r: word r / 32, bit r % 32); rows beyond the span are left out (there are fewer than 254).</summary>
    public void ResidentBiomeBits(Span<uint> bits)
    {
        bits.Clear();
        for (int r = 0; r < state.Length && r < bits.Length * 32; r++)
            if (state[r].Resident) bits[r >> 5] |= 1u << (r & 31);
    }

    void BuildBiomes(RgbaImage blend)
    {
        if (biomes.Count == 0) return;
        // The cell table (GL RGBA8UI, nearest, clamped), written whole by UploadCells.
        cellTexture = Make(InternalFormat.Rgba8ui, CellsX * 2, CellsZ, 1, 1, TextureMinFilter.Nearest, TextureMagFilter.Nearest,
            TextureWrapMode.ClampToEdge, "terrain cells");
        UploadCells();

        // Layers are handed out as texture pairs become resident; the arrays are allocated whole, with every mip level.
        diffuseArray = AllocateArray(true);
        normalArray = AllocateArray(false);
        if (GroundRelief.Enabled)
            heightArray = Make(InternalFormat.R8, HeightSize, HeightSize, (int)Math.Log2(HeightSize) + 1, Math.Max(Capacity, 1),
                TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear, TextureWrapMode.Repeat, "terrain textures height", kind: TextureKind.Texture2DArray);
        // Each slot's steepness factor (1 / (side × its height's steepness); 0: flat), read by the slot's index: its depth (GroundRelief.LayerDepthCm).
        if (GroundRelief.Enabled)
        {
            reliefTexture = Make(InternalFormat.R32f, Math.Max(Capacity, 1), 1, 1, 1, TextureMinFilter.Nearest, TextureMagFilter.Nearest,
                TextureWrapMode.ClampToEdge, "terrain relief depths");
            using var batch = Gpu.Uploads.Begin();
            batch.Write(reliefTexture.Texture, 0, 0, Rect(0, 0, Math.Max(Capacity, 1), 1), new byte[Math.Max(Capacity, 1) * 4]);
        }

        // One parameter row per biome; its layer indices are written when the biome becomes resident.
        paramTexture = Make(InternalFormat.Rgba32f, TerrainShaders.ParamTexels, biomes.Count, 1, 1, TextureMinFilter.Nearest, TextureMagFilter.Nearest,
            TextureWrapMode.ClampToEdge, "terrain biome parameters");
        using (var batch = Gpu.Uploads.Begin())
            batch.Write(paramTexture.Texture, 0, 0, Rect(0, 0, TerrainShaders.ParamTexels, biomes.Count), new byte[biomes.Count * TerrainShaders.ParamTexels * 16]);

        blendTexture = Rgba8(blend.Width, blend.Height, blend.Pixels, mipmaps: false, "terrain blend map");
        HasBiomes = true;
    }

    static PixelRect Rect(int x, int y, int width, int height) => new(x, y, (uint)width, (uint)height);

    /// <summary>A native texture with the GL sampler state the GL one had (nothing written yet; its transition goes with the next uploads).</summary>
    TerrainTexture Make(InternalFormat format, int width, int height, int levels, int layers, TextureMinFilter min, TextureMagFilter mag,
        TextureWrapMode wrap, string name, float anisotropy = 1, TextureKind kind = TextureKind.Texture2D)
    {
        using var batch = Gpu.Uploads.Begin();
        var t = batch.Create(new TextureDesc(format, width, height, levels, layers, Kind: kind, Name: name, Private: PrivateStatics));
        return new TerrainTexture(Gpu, t, min, mag, wrap, anisotropy);
    }

    /// <summary>
    /// A 2D RGBA8 texture from top-first rows, as <c>WorldGl.Texture2D</c> made it: with <paramref name="mipmaps"/> the whole chain made on the
    /// GPU as GL's GenerateMipmap made it in VkGl (<see cref="IGpuCommandList.GenerateMips"/>) and trilinear filtering, else one level, linear;
    /// clamped to the edge.
    /// </summary>
    TerrainTexture Rgba8(int width, int height, byte[] rgba, bool mipmaps, string name)
    {
        int levels = mipmaps ? 1 + (int)Math.Floor(Math.Log2(Math.Max(Math.Max(width, height), 1))) : 1;
        using var batch = Gpu.Uploads.Begin();
        var t = batch.Create(new TextureDesc(InternalFormat.Rgba8, width, height, levels,
            Use: TextureUse.Sampled | TextureUse.TransferDst | TextureUse.TransferSrc, Name: name, Private: PrivateStatics));
        batch.Write(t, 0, 0, Rect(0, 0, width, height), rgba.AsSpan(0, width * height * 4));
        if (mipmaps) batch.Commands.GenerateMips(t);
        return new TerrainTexture(Gpu, t, mipmaps ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge);
    }

    /// <summary>Diffuse layers are stored as BC3 (colour + gloss in alpha), normal layers as BC1 (their alpha is not used): see docs/render-terrain.md.</summary>
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
            return Make(FormatOf(diffuse), layerSize, layerSize, levelCount, Math.Max(Capacity, 1), TextureMinFilter.LinearMipmapLinear,
                TextureMagFilter.Linear, TextureWrapMode.Repeat, diffuse ? "terrain textures diffuse" : "terrain textures normal", anisotropy: 8, kind: TextureKind.Texture2DArray);
        }
        catch (Exception e)
        {
            Messages.Add($"terrain layer array allocation: {e.Message}; try a higher --texture-quality");
            return null;
        }
    }

    // ---- per-frame streaming -------------------------------------------------------------------------------

    /// <summary>
    /// Call once per frame from the GL thread: finds the biomes the cells within <paramref name="materialDistance"/> of
    /// the eye use, starts decoding the ones not resident (nearest first), queues the uploads of what finished, and
    /// moves the overlay/colour window when the eye has left the middle of it.
    /// </summary>
    public void Update(Vector3 eye, float materialDistance, UploadQueue uploads, Vector3? ahead = null, IReadOnlyList<Vector3>? kept = null)
    {
        frame++;
        while (problems.TryDequeue(out var m))
            if (printed++ < 20) Console.WriteLine($"warning   {m}");
        if (HasBiomes)
        {
            if (GroundRelief.FieldCentre is { } fieldCentre && tiles.Length == 0) SetField(fieldCentre, GroundRelief.FieldCliffs ? GroundRelief.FieldTile / 2 : GroundRelief.FieldTile);
            while (decoded.TryDequeue(out var d)) Install(d, uploads);
            UpdateBiomes(eye, materialDistance, ahead, kept);
        }
        UpdateMaps(eye, uploads);
        if (cellsDirty) { UploadCells(); fieldDirty = true; }
        if (fieldDirty) UploadField();
    }

    /// <summary>The biomes of the cells near <paramref name="eye"/> and, during a character flight, near its arrival eye <paramref name="ahead"/> (kept and loaded alike).</summary>
    void UpdateBiomes(Vector3 eye, float materialDistance, Vector3? ahead, IReadOnlyList<Vector3>? kept)
    {
        stamp++;
        needed.Clear();
        double cs = info.CellSize, half = WorldLayout.HalfWorldSize;
        double radius = materialDistance + cs / 2;
        var nearest = new Dictionary<int, double>();
        foreach (int b in pinned) nearest[b] = -1;   // the texture field's biomes first, wherever the eye is
        // During a flight the arrival's cells come first: the eye's (the flight path) count as farther than any of them.
        void Gather(Vector3 at, double lead)
        {
            int cx0 = (int)Math.Clamp(Math.Floor((at.X - radius + half) / cs), 0, CellsX - 1), cx1 = (int)Math.Clamp(Math.Floor((at.X + radius + half) / cs), 0, CellsX - 1);
            int cz0 = (int)Math.Clamp(Math.Floor((at.Z - radius + half) / cs), 0, CellsZ - 1), cz1 = (int)Math.Clamp(Math.Floor((at.Z + radius + half) / cs), 0, CellsZ - 1);
            for (int cz = cz0; cz <= cz1; cz++)
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    double x0 = cx * cs - half, z0 = cz * cs - half;
                    double dx = Math.Max(Math.Max(x0 - at.X, at.X - (x0 + cs)), 0), dz = Math.Max(Math.Max(z0 - at.Z, at.Z - (z0 + cs)), 0);
                    double d = Math.Sqrt(dx * dx + dz * dz);
                    if (d > radius) continue;
                    d += lead;
                    for (int k = 0; k < BlendInfoFile.SlotsPerCell; k++)
                        if (rowOf.TryGetValue(info.Slot(cx, cz, k), out int row) && (!nearest.TryGetValue(row, out double best) || d < best)) nearest[row] = d;
                }
        }
        if (ahead is { } arrival) Gather(arrival, 0);
        Gather(eye, ahead is null ? 0 : radius);
        // Kept points (the player's characters) come after the eye's own cells.
        if (kept is not null) foreach (var k in kept) Gather(k, radius * 2);
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
                float steep = 0;
                var h = GroundRelief.Enabled ? LoadHeight(assets, pair.Normal, HeightSize, out steep) : null;
                foreach (var m in local) problems.Enqueue(m);
                result = new Decoded(pair, d, n, h, steep);
            }
            catch (Exception e)
            {
                problems.Enqueue($"terrain texture {pair.Diffuse}: {e.Message}");
                result = new Decoded(pair, null, null, null);
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
        pair.Steep = d.Height is null ? 0 : d.Steep;
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
        if (heightArray is not null)
        {
            var height = d.Height ?? FlatHeight(HeightSize);
            uploads.Add(() =>
            {
                if (heightArray is null) return;
                using var batch = Gpu.Uploads.Begin();
                for (int level = 0; level < height.Length; level++)
                {
                    int s = Math.Max(HeightSize >> level, 1);
                    batch.Write(heightArray.Texture, level, slot, Rect(0, 0, s, s), height[level]);
                }
                if (reliefTexture is not null) batch.Write(reliefTexture.Texture, 0, 0, Rect(slot, 0, 1, 1), BitConverter.GetBytes(d.Height is null ? 0f : d.Steep));
            }, "height upload");
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
            new(bt.Absorbance[0], bt.Absorbance[1], bt.Absorbance[2], bt.Absorbance[3]),   // base, slope, cliff, grass
            new(bt.Absorbance[4], bt.Absorbance[5], 0, 0),                                 // dirt, road
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
                    using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
                    Span<byte> magic = stackalloc byte[4];
                    if (RandomAccess.Read(file, magic, 0) == 4 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(magic) == DdsReader.Magic)
                    {
                        // A DDS that is taken as stored is read from the level the layer starts at (the levels above are three quarters or more of the file).
                        var dds = DdsReader.ReadKept(file, w => TextureQuality.DropTopMips(w, StoredShift(w, size, levelCount)), out _);
                        if (AsStored(dds, size, levelCount, diffuse) is { } stored) return stored;
                        int level = 0;
                        while (level + 1 < dds.MipCount && Math.Max(dds.Width >> (level + 1), 1) >= size) level++;
                        image = DdsDecoder.Decode(dds, 0, level);
                    }
                    else image = TextureLoader.LoadImage(File.ReadAllBytes(path));
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
        var mips = Mips(Resize(image, size), size, size);
        var encoded = new byte[mips.Length][];
        for (int l = 0; l < mips.Length; l++)
        {
            int s = Math.Max(size >> l, 1);
            encoded[l] = diffuse ? BlockCompression.EncodeBc3(mips[l], s, s) : BlockCompression.EncodeBc1(mips[l], s, s);
        }
        return encoded;
    }

    /// <summary>
    /// The height levels of a layer for <see cref="GroundRelief"/>, R8 with mips: its normal map decoded at <paramref name="size"/>² and integrated
    /// (<see cref="NormalHeight.FromNormals"/>, in the frame the game lights it in), and <paramref name="steep"/> 1 / (side × the height's steepness):
    /// the depth, as a fraction of the tile, that gives a root-mean-square slope of 1. Null for a normal map that is missing or cannot be read (the layer
    /// stays flat).
    /// </summary>
    static byte[][]? LoadHeight(AssetLocator assets, string? normalName, int size, out float steep)
    {
        steep = 0;
        if (ReadImage(assets, normalName, size) is not { } normals) return null;
        var height = NormalHeight.FromNormals(Resize(normals, size), size, out float steepness, GroundRelief.Tilt);
        steep = steepness > 0 ? 1 / (size * steepness) : 0;
        var levels = new byte[(int)Math.Log2(size) + 1][];
        levels[0] = height;
        for (int l = 1; l < levels.Length; l++)
        {
            int s = Math.Max(size >> l, 1), p = s * 2;
            var src = levels[l - 1];
            levels[l] = new byte[s * s];
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                    levels[l][y * s + x] = (byte)((src[2 * y * p + 2 * x] + src[2 * y * p + 2 * x + 1] + src[(2 * y + 1) * p + 2 * x] + src[(2 * y + 1) * p + 2 * x + 1] + 2) / 4);
        }
        return levels;
    }

    /// <summary>A layer texture decoded at its first level no smaller than <paramref name="size"/>; null when it is missing or cannot be read.</summary>
    static RgbaImage? ReadImage(AssetLocator assets, string? name, int size)
    {
        if (name is null) return null;
        var path = assets.Find(name) ?? assets.Find(Path.GetFileName(name.Replace('\\', '/')));
        if (path is null) return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 4 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes) == DdsReader.Magic)
            {
                var dds = DdsReader.Read(bytes);
                int level = 0;
                while (level + 1 < dds.MipCount && Math.Max(dds.Width >> (level + 1), 1) >= size) level++;
                return DdsDecoder.Decode(dds, 0, level);
            }
            return TextureLoader.LoadImage(bytes);
        }
        catch (Exception e) when (e is DdsFormatException or InvalidOperationException or IOException or ArgumentException) { return null; }
    }

    /// <summary>The height of a layer without one: 255 (the top) everywhere (its steepness factor 0 keeps it out of the relief).</summary>
    static byte[][] FlatHeight(int size)
    {
        var levels = new byte[(int)Math.Log2(size) + 1][];
        for (int l = 0; l < levels.Length; l++)
        {
            int s = Math.Max(size >> l, 1);
            levels[l] = new byte[s * s];
            Array.Fill(levels[l], (byte)255);
        }
        return levels;
    }

    /// <summary>An RGBA8 image at <paramref name="size"/>²: box filter when shrinking, nearest when growing.</summary>
    static byte[] Resize(RgbaImage image, int size)
    {
        var result = new byte[size * size * 4];
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
        return result;
    }

    /// <summary>The top levels <see cref="AsStored"/> would not use of a DDS (the same conditions): how many to drop on reading, 0 when it is not taken as stored.</summary>
    static int StoredShift(DdsFile dds, int size, int levelCount)
    {
        if (dds.Format is not (DdsFormat.Bc1 or DdsFormat.Bc3) || dds.IsCubemap || dds.IsVolume || dds.ImageCount != 1) return 0;
        if (dds.Width != dds.Height || dds.Width < size || !int.IsPow2(dds.Width)) return 0;
        int shift = (int)Math.Log2(dds.Width / size);
        return shift + levelCount > dds.MipCount || dds.Surfaces.Count < dds.MipCount ? 0 : shift;
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
        overlayTexture = EmptyMap(InternalFormat.Rgba8, mapWindows!.OverlayPixels, MapLevels, "terrain overlay map");
        colourTexture = EmptyMap(mapWindows.CompressedColour ? FormatOf(diffuse: true) : InternalFormat.Rgba8, mapWindows.ColourPixels, MapLevels, "terrain colour map");
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
        // In 64 bits: the unset origin is int.MinValue, and 0 - int.MinValue wraps back to it in 32 (Math.Abs then throws).
        bool far = Math.Abs((long)px - validPx) > mapWindows.RecentreOverlayPixels || Math.Abs((long)pz - validPz) > mapWindows.RecentreOverlayPixels;
        if (MapState == 2 && !far) return;
        (int, int)? old = MapState == 2 ? (validPx, validPz) : null;
        mapJob = BackgroundWork.Run(() => mapWindows.Compose(px, pz, old));
    }

    void BeginMapUpload(MapWindows.Update update, UploadQueue uploads)
    {
        mapUploading = true;
        // Every piece comes with its mip levels from the worker and goes up in slabs of about 2 MB: whole rows of pixels, of 4 × 4 blocks for BC3 colour.
        foreach (var piece in update.Pieces)
            for (int level = 0; level < piece.Levels.Length; level++)
            {
                var (p, lv) = (piece, level);
                int w = Math.Max(p.Width >> lv, 1), h = Math.Max(p.Height >> lv, 1), x = p.X >> lv, z = p.Z >> lv;
                bool blocks = p.Colour && mapWindows!.CompressedColour;
                int unit = blocks ? 4 : 1, rowBytes = blocks ? w / 4 * 16 : w * 4, rowCount = h / unit;
                int rows = Math.Max(1, (2 << 20) / rowBytes);
                for (int row = 0; row < rowCount; row += rows)
                {
                    int r0 = row, n = Math.Min(rows, rowCount - row);
                    uploads.Add(() =>
                    {
                        using var batch = Gpu.Uploads.Begin();
                        batch.Write((p.Colour ? colourTexture : overlayTexture)!.Texture, lv, 0, Rect(x, z + r0 * unit, w, n * unit), p.Levels[lv].AsSpan(r0 * rowBytes, n * rowBytes));
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
    TerrainTexture EmptyMap(InternalFormat format, int size, int levels, string name) =>
        Make(format, size, size, levels, 1, TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear, TextureWrapMode.Repeat, name);

    // ---- whole-world maps ----------------------------------------------------------------------------------

    /// <summary>
    /// Whole-world maps for the far terrain: the biomes' ground colour × brightness fix blended by the blend map
    /// (what the textured layers fade to, and finer than the game's 128² <c>distant.png</c>), and the colour map
    /// shrunk to <paramref name="colourSize"/>² (0: none).
    /// </summary>
    /// <summary>The ground colour and world colour textures from what <see cref="PrepareWorld"/> made.</summary>
    void BuildWorld(Source s)
    {
        if (s.Ground is { } ground && s.Blend is { } blend)
        {
            groundTexture = Rgba8(blend.Width, blend.Height, ground, mipmaps: true, "terrain ground colour");
            HasGround = true;
        }
        if (s.WorldColour is { } colour)
        {
            worldColourTexture = Rgba8(s.WorldColourSize, s.WorldColourSize, colour, mipmaps: true, "terrain world colour");
            HasWorldColour = true;
        }
    }

    /// <summary>The ground colour baked from the blend map and the world colour map box-filtered from the colour tiles (CPU, any thread).</summary>
    static void PrepareWorld(GameInstall install, Source s, int colourSize)
    {
        if (s.Blend is not { } blend) return;
        var all = s.All;
        var field = BiomeField.Bake(s.Info, blend.Width, blend.Height, blend.Pixels,
            c => all.TryGetValue(c, out var b) ? new Vector4(b.GroundColour * b.BrightnessFix, 1) : null, new Vector4(0.6f, 0.55f, 0.45f, 1));
        var rgba = new byte[field.Length * 4];
        for (int i = 0; i < field.Length; i++)
        {
            var v = Vector4.Clamp(field[i], Vector4.Zero, Vector4.One) * 255;
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = ((byte)v.X, (byte)v.Y, (byte)v.Z, 255);
        }
        s.Ground = rgba;
        if (colourSize <= 0) return;

        // 8 x 8 colour tiles, each box-filtered down to colourSize / 8.
        int part = colourSize / TerrainMaps.OverlayTiles;
        var tiles = Enumerable.Range(0, TerrainMaps.OverlayTiles * TerrainMaps.OverlayTiles)
            .Select(t => TerrainMaps.OverlayTile(install, "colour", t % TerrainMaps.OverlayTiles, t / TerrainMaps.OverlayTiles)).ToArray();
        string? cache = LoadCaches.Disabled ? null : WorldColourCache(tiles, colourSize);
        if (cache is not null && ReadWorldColour(cache, colourSize * colourSize * 4) is { } cached)
        {
            (s.WorldColour, s.WorldColourSize) = (cached, colourSize);
            return;
        }
        var colour = new byte[colourSize * colourSize * 4];
        Parallel.For(0, tiles.Length, t =>
        {
            int tx = t % TerrainMaps.OverlayTiles, tz = t / TerrainMaps.OverlayTiles;
            var path = tiles[t];
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
        (s.WorldColour, s.WorldColourSize) = (colour, colourSize);
        if (cache is not null) WriteWorldColour(cache, colour);
    }

    /// <summary>
    /// The world colour map's disk cache, <c>%LOCALAPPDATA%\Meitou\terrain</c> (neither read nor written with <c>--no-load-cache</c>): decoding the 64 colour tiles
    /// (2048² PNGs) to shrink them took over a second of every start. Named by a hash of the tiles' paths, sizes and times and the map's size, so a mod's tile counts.
    /// </summary>
    static string WorldColourCache(string[] tiles, int size)
    {
        var key = new System.Text.StringBuilder(FormattableString.Invariant($"world colour v1 {size}"));
        foreach (var path in tiles)
        {
            var file = new FileInfo(path);
            key.Append('|').Append(path).Append(file.Exists ? FormattableString.Invariant($" {file.Length} {file.LastWriteTimeUtc.Ticks}") : " none");
        }
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key.ToString())))[..24].ToLowerInvariant();
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "terrain", $"worldcolour_{hash}.bin");
    }

    static byte[]? ReadWorldColour(string path, int length)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            return bytes.Length == length ? bytes : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static void WriteWorldColour(string path, byte[] colour)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllBytes(temporary, colour);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The terrain material's textures (null where one does not exist yet), for the native draws that read them by bindless index.</summary>
    internal readonly record struct TextureSet(TerrainTexture? Diffuse, TerrainTexture? Normal, TerrainTexture? Params, TerrainTexture? Cells, TerrainTexture? BlendMap,
        TerrainTexture? Overlay, TerrainTexture? Colour, TerrainTexture? Ground, TerrainTexture? WorldColour, TerrainTexture? Height, TerrainTexture? Relief,
        TerrainTexture? Field);

    internal TextureSet Textures => new(diffuseArray, normalArray, paramTexture, cellTexture, blendTexture, overlayTexture, colourTexture, groundTexture, worldColourTexture, heightArray,
        reliefTexture, fieldTexture);

    // ---- the texture field (GroundRelief.FieldCentre: the terrain sandbox) ----

    /// <summary>One tile of the texture field: the biome row it is drawn with, the layer it shows, and what to call it.</summary>
    internal readonly record struct FieldEntry(int Row, TerrainLayer Layer, string Label);

    TerrainTexture? fieldTexture;
    FieldEntry[] tiles = [];
    int[] pinned = [];
    bool fieldDirty;

    /// <summary>The texture field's lower left corner (world X, Z), tile side, tiles a row and tile count (0: no field).</summary>
    public Vector2 FieldOrigin { get; private set; }
    public float FieldTile { get; private set; }
    public int FieldColumns { get; private set; }
    public int FieldCount => tiles.Length;

    /// <summary>
    /// Lays the texture field out round <paramref name="centre"/>: one square tile of side <paramref name="tile"/> for each distinct ground texture pair
    /// (the base, slope, grass, dirt and road layers of every biome; with <see cref="GroundRelief.FieldCliffs"/> the cliff layers instead, whose vertical
    /// projections need a steep face), ordered by layer then name, from the first biome that uses it; those biomes are kept resident wherever the eye is.
    /// </summary>
    public void SetField(Vector2 centre, float tile)
    {
        var seen = new HashSet<(string?, string?)>();
        var entries = new List<FieldEntry>();
        foreach (var layer in GroundRelief.FieldCliffs ? [TerrainLayer.Cliff] : new[] { TerrainLayer.Base, TerrainLayer.Slope, TerrainLayer.Grass, TerrainLayer.Dirt, TerrainLayer.Road })
            for (int b = 0; b < biomes.Count; b++)
            {
                var pair = state[b].Pairs[(int)layer];
                if (pair.Normal is null || !seen.Add((pair.Diffuse?.ToLowerInvariant(), pair.Normal.ToLowerInvariant()))) continue;
                entries.Add(new FieldEntry(b, layer, $"{Path.GetFileNameWithoutExtension(pair.Normal.Replace('\\', '/'))} ({biomes[b].Name}: {layer})"));
            }
        tiles = [.. entries.OrderBy(e => e.Layer).ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)];
        FieldColumns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(tiles.Length)));
        int rows = (tiles.Length + FieldColumns - 1) / FieldColumns;
        FieldTile = tile;
        FieldOrigin = centre - new Vector2(FieldColumns, rows) * tile / 2;
        pinned = [.. tiles.Select(e => e.Row).Distinct()];
        fieldTexture ??= Make(InternalFormat.Rgba8ui, Math.Max(tiles.Length, 1), 1, 1, 1, TextureMinFilter.Nearest, TextureMagFilter.Nearest,
            TextureWrapMode.ClampToEdge, "terrain texture field");
        fieldDirty = true;
        int needed = pinned.SelectMany(b => state[b].Pairs).Distinct().Count();
        Console.WriteLine($"field     {tiles.Length} ground textures from {pinned.Length} biomes ({needed} texture pairs kept resident, room for {Capacity}), {FieldColumns} a row, tiles {tile:0} units, " +
            $"from {FieldOrigin.X:0}, {FieldOrigin.Y:0}");
    }

    /// <summary>The texture field's tile at world (<paramref name="x"/>, <paramref name="z"/>): its label, or null outside the field.</summary>
    public string? FieldLabel(float x, float z)
    {
        if (tiles.Length == 0) return null;
        int cx = (int)MathF.Floor((x - FieldOrigin.X) / FieldTile), cz = (int)MathF.Floor((z - FieldOrigin.Y) / FieldTile), i = cz * FieldColumns + cx;
        if (cx < 0 || cz < 0 || cx >= FieldColumns || i >= tiles.Length) return null;
        var e = tiles[i];
        var pair = state[e.Row].Pairs[(int)e.Layer];
        float tile = 5000 / Math.Max((biomes[e.Row].Tiling[(int)e.Layer].X + biomes[e.Row].Tiling[(int)e.Layer].Y) / 2, 1e-3f);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{i + 1}/{tiles.Length}  {e.Label}  tile {tile / 10:0.#} m, depth {GroundRelief.LayerDepthCm(pair.Steep, tile) * (e.Layer == TerrainLayer.Cliff ? GroundRelief.CliffScale : 1):0.#} cm");
    }

    /// <summary>The field's tiles, one texel each: R biome row, G layer, B 1 once the biome is resident (until then the tile shows the world).</summary>
    void UploadField()
    {
        fieldDirty = false;
        if (fieldTexture is null || tiles.Length == 0) return;
        var texels = new byte[tiles.Length * 4];
        for (int i = 0; i < tiles.Length; i++)
            (texels[i * 4], texels[i * 4 + 1], texels[i * 4 + 2]) = ((byte)tiles[i].Row, (byte)tiles[i].Layer, (byte)(state[tiles[i].Row].Resident ? 1 : 0));
        using var batch = Gpu.Uploads.Begin();
        batch.Write(fieldTexture.Texture, 0, 0, Rect(0, 0, tiles.Length, 1), texels);
    }

    public void Dispose()
    {
        foreach (var t in new[] { diffuseArray, normalArray, heightArray, reliefTexture, paramTexture, cellTexture, blendTexture, overlayTexture, colourTexture, groundTexture, worldColourTexture, fieldTexture })
            t?.Dispose();
    }
}

/// <summary>
/// A native texture of the terrain's (phase 8, docs/renderer-native.md 8) sampled as it was as a GL texture through VkGl: the GL sampler
/// state it was given (<see cref="SamplerDesc.FromGl"/>, the upscaler's LOD bias on mipmapped filters, as VkGl's <c>SamplerFor</c>) and the
/// view of all its levels (it is made with exactly the levels VkGl's view covered). <see cref="Bindless"/> keeps the former GL layer's rule:
/// the same index while the sampler is unchanged, a new one (the old freed after the frames in flight) when the LOD bias moved. Render thread only.
/// </summary>
internal sealed class TerrainTexture : IDisposable
{
    readonly IGpuContext ctx;
    readonly TextureMinFilter min;
    readonly TextureMagFilter mag;
    readonly TextureWrapMode wrap;
    readonly float anisotropy;
    readonly bool integer;
    float cachedBias = float.NaN;
    int cachedGeneration;
    SampledTexture cached;
    BindlessHandle handle;
    SampledTexture registered;
    bool hasEntry;

    public TerrainTexture(IGpuContext ctx, GpuTexture texture, TextureMinFilter min, TextureMagFilter mag, TextureWrapMode wrap, float anisotropy = 1)
    {
        this.ctx = ctx;
        Texture = texture;
        (this.min, this.mag, this.wrap, this.anisotropy) = (min, mag, wrap, anisotropy);
        integer = GlConventions.IsIntegerFormat(texture.Desc.Format);
    }

    public GpuTexture Texture { get; }

    /// <summary>The sampler and view a draw samples it with now (what VkGl's <c>Sampled</c> gave for the GL texture).</summary>
    public SampledTexture Sampled()
    {
        float bias = ctx.LodBias;
        if (!(bias == cachedBias) || cachedGeneration != ctx.Samplers.Generation)
        {
            var sampler = ctx.Samplers.Get(SamplerDesc.FromGl(min, mag, wrap, wrap, TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, anisotropy, integer, bias));
            (cached, cachedBias, cachedGeneration) = (new SampledTexture(sampler, Texture.View()), bias, ctx.Samplers.Generation);
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
/// The colour window is BC3 cut from <see cref="TerrainColourCache"/>'s encoded tiles; RGBA8 from the PNGs when a tile's side does not allow blocks.
/// </summary>
sealed class MapWindows
{
    /// <summary>Window edge in overlay pixels (36 units each).</summary>
    const int WindowPixels = 2048;

    readonly TileCache overlay;
    readonly TileCache? colour;
    readonly BlockTiles? colourBlocks;
    readonly int overlayTile, colourTile, ratio;
    /// <summary>The alignment of the window's origin in overlay pixels: the mip level 6 texel (64), and for BC3 colour whole blocks at every colour level.</summary>
    readonly int align;
    readonly double overlayUnit;

    MapWindows(TileCache overlay, TileCache? colour, BlockTiles? colourBlocks, int overlayTile, int colourTile)
    {
        this.overlay = overlay;
        this.colour = colour;
        this.colourBlocks = colourBlocks;
        this.overlayTile = overlayTile;
        this.colourTile = colourTile;
        overlayUnit = (double)WorldLayout.WorldSize / (overlayTile * TerrainMaps.OverlayTiles);
        ratio = Math.Max((int)Math.Round((double)colourTile / overlayTile), 1);
        if (ratio * overlayTile != colourTile) throw new InvalidDataException($"colour tiles ({colourTile}) are not a whole multiple of overlay tiles ({overlayTile})");
        align = 64;
        if (colourBlocks is null) return;
        while (align * ratio % TerrainColourCache.Granule != 0) align += 64;
        if (OverlayPixels % align != 0 || overlayTile * TerrainMaps.OverlayTiles % align != 0)
            throw new InvalidDataException($"the window ({OverlayPixels}) or the overlay map ({overlayTile * TerrainMaps.OverlayTiles}) is not a multiple of {align}");
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
        // A 2048² overlay tile is 16 MB and a colour tile can be larger. The window spans only a few tiles; keep
        // enough for one compose and clear them below, instead of pinning 24 + 12 full decoded maps for the session.
        var o = Cache("new_overlay", 4, out int ot);
        var firstColour = TerrainMaps.OverlayTile(install, "colour", 0, 0);
        try
        {
            var tile = TerrainColourCache.Get(firstColour) ?? throw new FileNotFoundException($"missing {firstColour}");
            return new MapWindows(o, null, new BlockTiles(install), ot, tile.Size);
        }
        catch (InvalidDataException e)
        {
            messages.Add($"colour map kept uncompressed: {e.Message}");
        }
        var c = Cache("colour", 2, out int ct);
        return new MapWindows(o, c, null, ot, ct);
    }

    /// <summary>Whether the colour window is BC3 (its pieces hold blocks).</summary>
    public bool CompressedColour => colourBlocks is not null;


    public int OverlayPixels => Math.Min(WindowPixels, overlayTile * TerrainMaps.OverlayTiles);
    public int ColourPixels => OverlayPixels * ratio;

    /// <summary>When the window's origin (overlay pixels) differs by more than this from the desired one, it moves (4608 units).</summary>
    public int RecentreOverlayPixels => Math.Max((int)(4608 / overlayUnit), align);

    /// <summary>Origin (overlay pixels) of a window centred on a point, aligned and kept inside the maps.</summary>
    public (int X, int Z) DesiredOrigin(double x, double z)
    {
        int full = overlayTile * TerrainMaps.OverlayTiles, size = OverlayPixels;
        int Axis(double v) => Math.Clamp((int)Math.Round(((v + WorldLayout.HalfWorldSize) / overlayUnit - size / 2.0) / align) * align, 0, full - size);
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
                    var clevels = colourBlocks is not null
                        ? CutBlocks(colourBlocks, colourTile, sx * ratio, sz * ratio, sw * ratio, sh * ratio)
                        : TerrainTextures.Mips(Cut(colour!, colourTile, sx * ratio, sz * ratio, sw * ratio, sh * ratio), sw * ratio, sh * ratio, 7, pooled: true);
                    pieces.Add(new Piece(true, sx * ratio % cs, sz * ratio % cs, sw * ratio, sh * ratio, clevels));
                }
        var update = new Update(px0, pz0, pieces);
        // The pieces own pooled copies of what they need; the decoded tiles are large and a window moves only after
        // 4,608 units, so retaining them for the next compose is not worth hundreds of MB of LOH.
        overlay.Clear();
        colour?.Clear();
        colourBlocks?.Clear();
        return update;
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

    /// <summary>
    /// A piece's BC3 levels cut from the encoded colour tiles: rows of whole blocks (the alignment keeps every edge on a block at every level),
    /// pooled like <see cref="Cut"/>'s; a missing tile leaves zero blocks.
    /// </summary>
    static byte[][] CutBlocks(BlockTiles cache, int tile, int x0, int z0, int w, int h)
    {
        int tx0 = x0 / tile, tx1 = (x0 + w - 1) / tile, tz0 = z0 / tile, tz1 = (z0 + h - 1) / tile;
        var keys = new List<(int, int)>();
        for (int tz = tz0; tz <= tz1; tz++)
            for (int tx = tx0; tx <= tx1; tx++) keys.Add((tx, tz));
        var tiles = new Dictionary<(int, int), TerrainColourCache.Tile?>();
        Parallel.ForEach(keys, key =>
        {
            var t = cache.Get(key.Item1, key.Item2);
            lock (tiles) tiles[key] = t?.Size == tile ? t : null;
        });
        var levels = new byte[TerrainColourCache.Levels][];
        for (int l = 0; l < levels.Length; l++)
        {
            // In blocks at this level.
            int bx0 = (x0 >> l) / 4, bz0 = (z0 >> l) / 4, bw = (w >> l) / 4, bh = (h >> l) / 4, tb = (tile >> l) / 4;
            var result = ArrayPool<byte>.Shared.Rent(bw * bh * 16);
            Array.Clear(result, 0, bw * bh * 16);
            foreach (var (tx, tz) in keys)
            {
                if (tiles[(tx, tz)] is not { } t) continue;
                int ax0 = Math.Max(bx0, tx * tb), ax1 = Math.Min(bx0 + bw, (tx + 1) * tb);
                int az0 = Math.Max(bz0, tz * tb), az1 = Math.Min(bz0 + bh, (tz + 1) * tb);
                for (int z = az0; z < az1; z++)
                    Array.Copy(t.Levels[l], ((z - tz * tb) * tb + (ax0 - tx * tb)) * 16, result, ((z - bz0) * bw + (ax0 - bx0)) * 16, (ax1 - ax0) * 16);
            }
            levels[l] = result;
        }
        return levels;
    }

    /// <summary>The encoded colour tiles one compose uses (<see cref="TerrainColourCache"/>), cleared after it like the decoded ones.</summary>
    sealed class BlockTiles(GameInstall install)
    {
        readonly Dictionary<(int, int), TerrainColourCache.Tile?> tiles = [];

        public TerrainColourCache.Tile? Get(int tx, int tz)
        {
            lock (tiles)
                if (tiles.TryGetValue((tx, tz), out var hit)) return hit;
            TerrainColourCache.Tile? tile;
            try { tile = TerrainColourCache.Get(TerrainMaps.OverlayTile(install, "colour", tx, tz)); }
            catch (InvalidDataException) { tile = null; }   // a tile of another size than the first: left out, as a missing one
            lock (tiles) tiles[(tx, tz)] = tile;
            return tile;
        }

        public void Clear()
        {
            lock (tiles) tiles.Clear();
        }
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

        /// <summary>Drops every decoded tile after a compose has copied what it needs.</summary>
        public void Clear()
        {
            lock (tiles) tiles.Clear();
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
