using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.Textures;
using Meitou.Data.World;

using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Terrain;

/// <summary>
/// GPU resources for texturing the terrain (docs/formats/terrain.md, "How the terrain is textured"), streamed to
/// follow the camera: the biome layer textures of every biome that <c>blendinfo.dat</c> lists in a cell within the
/// material distance of the eye (far arrays holding every such texture pair at a quarter size, and near arrays whose few slots go to the pairs used
/// near the eye at full size, least recently needed evicted first), a parameter texture with one row per biome, the cell table (what each biome slot
/// of a cell is right now: a row, "still loading" or unused), the world blend map, and windows of the overlay and
/// colour maps that move with the eye. Decoding runs on worker threads; every upload is made on the thread that calls
/// <see cref="Update"/>, in steps through an <see cref="UploadQueue"/>. Native textures (phase 8), sampled as their GL versions were
/// (<see cref="TerrainTexture"/>).
/// </summary>
public sealed unsafe class TerrainTextures : IDisposable
{
    #region Construction

    /// <summary>
    /// <c>MEITOU_METAL_PRIVATE_STATIC=1</c>: the static textures are made private to the GPU.
    /// </summary>
    static readonly bool PrivateStatics = Environment.GetEnvironmentVariable("MEITOU_METAL_PRIVATE_STATIC") == "1";

    /// <summary>
    /// The native GPU API every texture is made with (phase 8: no GL texture of its own).
    /// </summary>
    public IGpuContext Gpu { get; }

    /// <summary>
    /// The install the layers and maps are read from.
    /// </summary>
    readonly GameInstall _install;

    /// <summary>
    /// Finds the layer images by name.
    /// </summary>
    readonly AssetLocator _assets;

    /// <summary>
    /// The near layers' side and mip levels, and the far layers'.
    /// </summary>
    readonly int _layerSize, _levelCount, _farSize, _farLevels;

    /// <summary>
    /// The blend information: the cells and their biome slots.
    /// </summary>
    readonly BlendInfoFile _info;

    /// <summary>
    /// The textures: the near and far layer arrays, the heights, the relief, the parameters, the cell table, the blend map, the overlay and colour
    /// windows, the ground colour and the world colour (null until made, or when they could not be).
    /// </summary>
    TerrainTexture? _diffuseArray, _normalArray, _diffuseFar, _normalFar, _heightArray, _reliefTexture, _paramTexture, _cellTexture, _blendTexture,
        _overlayTexture, _colourTexture, _groundTexture, _worldColourTexture;

    /// <summary>
    /// Problems for the log (missing files, failed allocations).
    /// </summary>
    public List<string> Messages { get; } = [];

    /// <summary>
    /// Pairs the biomes' layers and sizes the arrays; the textures are made by <see cref="Create"/>.
    /// </summary>
    TerrainTextures(IGpuContext gpu, GameInstall install, AssetLocator assets, BlendInfoFile info, BiomeTerrain[] all, int layerSize,
        MapWindows? maps)
    {
        Gpu = gpu;
        this._install = install;
        this._assets = assets;
        this._info = info;
        this._layerSize = layerSize;
        _mapWindows = maps;
        _levelCount = (int)Math.Log2(layerSize) + 1;
        _farSize = Math.Max(layerSize >> FarShift, 4);
        _farLevels = (int)Math.Log2(_farSize) + 1;
        CellsX = info.CellsX;
        CellsZ = info.CellsZ;
        AddBiomes(all);
        _state = new BiomeState[_biomes.Count];
        PairBiomeLayers();
        FarSlots = Math.Min(_pairs.Count, MaxFarLayers);
        Capacity = Math.Min(_pairs.Count, NearLayers);
        _owner = new Pair?[Capacity];
    }

    /// <summary>
    /// Gives each biome of <paramref name="all"/> its parameter row, up to <see cref="Pending"/> of them.
    /// </summary>
    void AddBiomes(BiomeTerrain[] all)
    {
        foreach (var b in all)
        {
            if (_biomes.Count >= Pending) break;
            _rowOf[b.Index] = _biomes.Count;
            _biomes.Add(b);
        }
    }

    /// <summary>
    /// Each biome's six layers as texture pairs, a pair shared by every layer with the same two textures (its far index in order of first use, its
    /// tile the largest of its uses), and which biomes use each pair.
    /// </summary>
    void PairBiomeLayers()
    {
        for (int b = 0; b < _biomes.Count; b++)
        {
            var layers = new Pair[6];
            for (int l = 0; l < 6; l++)
            {
                var key = (_biomes[b].Diffuse[l]?.ToLowerInvariant(), _biomes[b].Normal[l]?.ToLowerInvariant());
                if (!_pairs.TryGetValue(key, out var pair))
                    _pairs[key] = pair = new Pair { Diffuse = _biomes[b].Diffuse[l], Normal = _biomes[b].Normal[l], Far = _pairs.Count };
                pair.Tile = Math.Max(pair.Tile, 5000 / Math.Max((_biomes[b].Tiling[l].X + _biomes[b].Tiling[l].Y) / 2, 1e-3f));
                if (pair.Far >= MaxFarLayers) pair.Failed = true;
                layers[l] = pair;
                if (!_usedBy.TryGetValue(pair, out var list)) _usedBy[pair] = list = [];
                if (!list.Contains(b)) list.Add(b);
            }
            _state[b] = new BiomeState { Pairs = layers };
        }
    }

    /// <summary>
    /// Makes the terrain's textures from <paramref name="source"/> (made here when null): the biome layer arrays, the map windows and the
    /// whole-world maps.
    /// </summary>
    /// <param name="layerSize">Edge length every layer texture is brought to (the arrays need one size).</param>
    public static TerrainTextures Create(IGpuContext gpu, GameInstall install, GameDatabase db, AssetLocator assets, int layerSize = 512,
        int worldColourSize = 2048, Source? source = null)
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
        t._biomeMap = source.BiomeMap;
        source.ReleaseUploaded();
        return t;
    }

    /// <summary>
    /// The CPU part of <see cref="Create"/>, on any thread (it makes no GPU resource).
    /// </summary>
    public static Source Prepare(GameInstall install, GameDatabase db, int worldColourSize = 2048)
    {
        var s = new Source { Info = BlendInfoFile.Open(install), All = BiomeTerrain.ByIndex(db) };
        try
        {
            s.Maps = MapWindows.Open(install, s.Messages);
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            s.Messages.Add($"overlay maps: {e.Message}");
        }
        try
        {
            s.Blend = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install.DataDirectory, TerrainMaps.BlendMap)));
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            s.Messages.Add($"biome textures: {e.Message}");
        }
        try
        {
            PrepareWorld(install, s, worldColourSize);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        {
            s.Messages.Add($"world maps: {e.Message}");
        }
        try
        {
            s.BiomeMap = FoliageBiomeMap.Open(install);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        {
            s.Messages.Add($"biome map: {e.Message}");
        }
        return s;
    }

    /// <summary>
    /// What <see cref="Create"/> reads and works out on the CPU (about 2.5 s at a cold start): the blend information, the biomes, the overlay map
    /// windows, the decoded blend map, the ground colour baked from it and the world colour map box-filtered from the 64 colour tiles, the foliage
    /// biome map. Made by <see cref="Prepare"/> on any thread, so a game can make it on a worker while its window keeps drawing.
    /// </summary>
    public sealed class Source
    {
        /// <summary>
        /// The blend information.
        /// </summary>
        internal BlendInfoFile Info = null!;

        /// <summary>
        /// Every biome by its blend map colour.
        /// </summary>
        internal Dictionary<uint, BiomeTerrain> All = null!;

        /// <summary>
        /// The overlay and colour windows' sources (null when they could not be opened).
        /// </summary>
        internal MapWindows? Maps;

        /// <summary>
        /// The decoded blend map.
        /// </summary>
        internal RgbaImage? Blend;

        /// <summary>
        /// The ground colour baked from the blend map, and the world colour map box-filtered from the colour tiles.
        /// </summary>
        internal byte[]? Ground, WorldColour;

        /// <summary>
        /// The world colour map's side.
        /// </summary>
        internal int WorldColourSize;

        /// <summary>
        /// The foliage biome map.
        /// </summary>
        internal FoliageBiomeMap? BiomeMap;

        /// <summary>
        /// Problems for the log.
        /// </summary>
        internal readonly List<string> Messages = [];

        /// <summary>
        /// Drops the CPU images after <see cref="Create"/> has uploaded them. They are the largest LOH objects
        /// of a cold start (the blend, ground and world-colour buffers); keeping the source alive kept them rooted.
        /// </summary>
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

    /// <summary>
    /// Frees every texture and the map standbys.
    /// </summary>
    public void Dispose()
    {
        foreach (var t in new[] { _diffuseArray, _normalArray, _diffuseFar, _normalFar, _heightArray, _reliefTexture, _paramTexture, _cellTexture,
            _blendTexture, _overlayTexture, _colourTexture, _groundTexture, _worldColourTexture, _fieldTexture })
            t?.Dispose();
        foreach (var m in _mapStandbys)
        {
            m.Overlay.Dispose();
            m.Colour.Dispose();
        }
    }

    /// <summary>
    /// Makes the cell table, the layer arrays, the heights and the relief, and the parameter rows of <paramref name="blend"/>'s biomes.
    /// </summary>
    void BuildBiomes(RgbaImage blend)
    {
        if (_biomes.Count == 0) return;
        // The cell table (GL RGBA8UI, nearest, clamped), written whole by UploadCells.
        _cellTexture = Make(InternalFormat.Rgba8Ui, CellsX * 2, CellsZ, 1, 1, TextureMinFilter.Nearest, TextureMagFilter.Nearest,
            TextureWrapMode.ClampToEdge, "terrain cells");
        UploadCells();

        // Layers are written as texture pairs arrive; the arrays are allocated whole, with every mip level: the near ones' slots handed out per pair
        // near the eye, the far ones (and the heights) at each pair's fixed index.
        _diffuseArray = AllocateArray(true, Capacity, _layerSize, _levelCount, "terrain textures diffuse");
        _normalArray = AllocateArray(false, Capacity, _layerSize, _levelCount, "terrain textures normal");
        _diffuseFar = AllocateArray(true, FarSlots, _farSize, _farLevels, "terrain textures diffuse far");
        _normalFar = AllocateArray(false, FarSlots, _farSize, _farLevels, "terrain textures normal far");
        if (GroundRelief.Enabled)
        {
            try
            {
                _heightArray = MakeArray(InternalFormat.R8, HeightSize, (int)Math.Log2(HeightSize) + 1, FarSlots, 1, "terrain textures height");
            }
            catch (Exception e)
            {
                Messages.Add($"terrain height array allocation: {e.Message}; the ground has no relief");
            }
        }

        // Each pair's steepness factor (1 / (side × its height's steepness); 0: flat), read by its far index: its depth (GroundRelief.LayerDepthCm).
        if (GroundRelief.Enabled)
        {
            _reliefTexture = Make(InternalFormat.R32F, Math.Max(FarSlots, 1), 1, 1, 1, TextureMinFilter.Nearest, TextureMagFilter.Nearest,
                TextureWrapMode.ClampToEdge, "terrain relief depths");
            using var batch = Gpu.Uploads.Begin();
            batch.Write(_reliefTexture.Texture, 0, 0, Rect(0, 0, Math.Max(FarSlots, 1), 1), new byte[Math.Max(FarSlots, 1) * 4]);
        }

        // One parameter row per biome; its layer indices are written when the biome becomes resident.
        _paramTexture = Make(InternalFormat.Rgba32F, TerrainShaders.ParamTexels, _biomes.Count, 1, 1, TextureMinFilter.Nearest,
            TextureMagFilter.Nearest,
            TextureWrapMode.ClampToEdge, "terrain biome parameters");
        using (var batch = Gpu.Uploads.Begin())
            batch.Write(_paramTexture.Texture, 0, 0, Rect(0, 0, TerrainShaders.ParamTexels, _biomes.Count),
                new byte[_biomes.Count * TerrainShaders.ParamTexels * 16]);

        _blendTexture = Rgba8(blend.Width, blend.Height, blend.Pixels, mipmaps: false, "terrain blend map");
        HasBiomes = true;
    }

    /// <summary>
    /// A pixel rectangle from signed sizes.
    /// </summary>
    static PixelRect Rect(int x, int y, int width, int height) => new(x, y, (uint)width, (uint)height);

    /// <summary>
    /// A native texture with the GL sampler state the GL one had (nothing written yet; its transition goes with the next uploads).
    /// </summary>
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
        return new TerrainTexture(Gpu, t, mipmaps ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear, TextureMagFilter.Linear,
            TextureWrapMode.ClampToEdge);
    }

    /// <summary>
    /// Diffuse layers are stored as BC3 (colour + gloss in alpha), normal layers as BC1 (their alpha is not used): see docs/render-terrain.md.
    /// </summary>
    static InternalFormat FormatOf(bool diffuse) => diffuse ? InternalFormat.CompressedRgbaS3TcDxt5Ext : InternalFormat.CompressedRgbS3TcDxt1Ext;

    /// <summary>
    /// One layer array of <paramref name="slots"/> slots of <paramref name="size"/>² with every mip level, trilinear and 8x anisotropic, repeating
    /// (the slices are written as pairs arrive).
    /// </summary>
    TerrainTexture? AllocateArray(bool diffuse, int slots, int size, int levels, string name)
    {
        try
        {
            return MakeArray(FormatOf(diffuse), size, levels, slots, 8, name);
        }
        catch (Exception e)
        {
            Messages.Add($"terrain layer array allocation: {e.Message}; try a lower texture quality");
            return null;
        }
    }

    /// <summary>
    /// A layer array of <paramref name="slots"/> layers with mips, trilinear and repeating.
    /// </summary>
    TerrainTexture MakeArray(InternalFormat format, int size, int levels, int slots, float anisotropy, string name)
    {
        using var batch = Gpu.Uploads.Begin();
        var t = batch.Create(new TextureDesc(format, size, size, levels, Math.Max(slots, 1), Kind: TextureKind.Texture2DArray,
            Use: TextureUse.Sampled | TextureUse.TransferDst, Name: name, Private: PrivateStatics));
        return new TerrainTexture(Gpu, t, TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear, TextureWrapMode.Repeat, anisotropy);
    }

    #endregion

    #region Layer streaming

    /// <summary>
    /// Slots of the near arrays: the pairs used near the eye at full size (each a BC3 and a BC1 layer of <c>layerSize</c>² with mips, 8.4 MB at
    /// 2048), least recently needed evicted first. A pair without one is drawn from the far arrays.
    /// </summary>
    public const int NearLayers = 32;

    /// <summary>
    /// The far arrays hold every pair at the layer size over 2^FarShift (512² at 2048, 0.5 MB a pair): their levels are the near arrays' from
    /// FarShift down, so a sample whose level is at least FarShift reads the same texels from either (docs/render-terrain.md, "Layer
    /// arrays").
    /// </summary>
    public const int FarShift = 2;

    /// <summary>
    /// Most texture pairs the far arrays hold, the array layers every Vulkan device has (a pair past it stays in its ground colour; the base game has
    /// 133).
    /// </summary>
    const int MaxFarLayers = 256;

    /// <summary>
    /// A pair gets a near slot within this many of its tile's size (world units per repeat) times the pixels per radian over the layer size: there a
    /// pixel covers fewer than 2^FarShift near texels across (2^(FarShift + 0.5) with half a level of margin), doubled for the screen's corners (a
    /// pixel's footprint there is about cos² of the angle to the axis smaller) and the derivative estimate.
    /// </summary>
    const double NearReach = 2 * 5.657;

    /// <summary>
    /// Cell table value of a biome slot that is used but not resident yet / of an unused slot.
    /// </summary>
    const byte Pending = 254, Unused = 255;

    /// <summary>
    /// The most near slots <see cref="Areas"/> may grow the arrays to (the 128 a full-size slot per pair took before the two tiers, ~1.07 GB at
    /// 2048²).
    /// </summary>
    const int MaxNearSlots = 128;

    /// <summary>
    /// The biomes, by parameter row.
    /// </summary>
    readonly List<BiomeTerrain> _biomes = [];

    /// <summary>
    /// Each biome's parameter row by its blend map colour.
    /// </summary>
    readonly Dictionary<uint, int> _rowOf = [];

    /// <summary>
    /// Per biome row: its pairs and whether it is resident.
    /// </summary>
    readonly BiomeState[] _state;

    /// <summary>
    /// The texture pairs by their diffuse and normal names (several layers may share one).
    /// </summary>
    readonly Dictionary<(string?, string?), Pair> _pairs = [];

    /// <summary>
    /// The biome rows that use each pair.
    /// </summary>
    readonly Dictionary<Pair, List<int>> _usedBy = [];

    /// <summary>
    /// The pair in each near slot (null: free).
    /// </summary>
    Pair?[] _owner;

    /// <summary>
    /// Decodes the workers finished, for <see cref="Update"/> to upload.
    /// </summary>
    readonly ConcurrentQueue<Decoded> _decoded = new();

    /// <summary>
    /// Problems the workers met, for the log.
    /// </summary>
    readonly ConcurrentQueue<string> _problems = new();

    /// <summary>
    /// The most decodes in flight at once.
    /// </summary>
    readonly int _maxInFlight = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

    /// <summary>
    /// The biomes wanted round the eye, nearest first.
    /// </summary>
    readonly List<int> _needed = [];

    /// <summary>
    /// Decodes in flight, near decodes in flight, near pairs waiting for a slot or a decode, the next never-used near slot, warnings printed (at most
    /// 20) and the stamp of the last <see cref="UpdateBiomes"/>.
    /// </summary>
    int _inFlight, _nearLoading, _nearPending, _nextFree, _printed, _stamp;

    /// <summary>
    /// Frames counted by <see cref="Update"/>.
    /// </summary>
    long _frame;

    /// <summary>
    /// The cell table needs uploading.
    /// </summary>
    bool _cellsDirty = true;

    /// <summary>
    /// The slot count the guard last refused (not asked again for as many or more).
    /// </summary>
    int _nearRefused = int.MaxValue;

    /// <summary>
    /// One diffuse and normal map pair of a layer, with where it lives in the arrays.
    /// </summary>
    sealed class Pair
    {
        /// <summary>
        /// The diffuse and normal maps' names.
        /// </summary>
        public string? Diffuse, Normal;

        /// <summary>
        /// Its index in the far arrays, the height array and the relief texture (every pair has one).
        /// </summary>
        public int Far;

        /// <summary>
        /// Its slot in the near arrays, -1 when it has none.
        /// </summary>
        public int Slot = -1;

        /// <summary>
        /// The largest tile (world units per repeat) of its uses: its near range scales with it.
        /// </summary>
        public float Tile;

        /// <summary>
        /// Its height's steepness factor once decoded (the relief texture's value: 1 / (side × steepness), 0 flat), for the field's labels.
        /// </summary>
        public float Steep;

        /// <summary>
        /// The last frame and <see cref="UpdateBiomes"/> stamp it was within its near range.
        /// </summary>
        public long Used;

        /// <summary>
        /// The <see cref="UpdateBiomes"/> stamp it was last wanted at full size.
        /// </summary>
        public int Needed;

        /// <summary>
        /// Its far layers are uploaded; a decode is under way; its far decode failed (or it is past <see cref="MaxFarLayers"/>).
        /// </summary>
        public bool FarReady, Loading, Failed;
    }

    /// <summary>
    /// A biome's pairs and residency.
    /// </summary>
    sealed class BiomeState
    {
        /// <summary>
        /// Its layers' pairs, in slot order.
        /// </summary>
        public required Pair[] Pairs;

        /// <summary>
        /// Every pair's far layers are uploaded and its parameter row is written.
        /// </summary>
        public bool Resident;
    }

    /// <summary>
    /// A pair's layers decoded for the near arrays (<paramref name="Near"/>) or the far ones (with its height).
    /// </summary>
    sealed record Decoded(Pair Pair, bool Near, byte[][]? Diffuse, byte[][]? Normal, byte[][]? Height, float Steep = 0);

    /// <summary>
    /// There are biomes to draw (set once the arrays are made).
    /// </summary>
    public bool HasBiomes { get; private set; }

    /// <summary>
    /// The blend cells along X.
    /// </summary>
    public int CellsX { get; }

    /// <summary>
    /// The blend cells along Z.
    /// </summary>
    public int CellsZ { get; }

    /// <summary>
    /// Slots of the near arrays (<see cref="NearLayers"/>, fewer when the world has fewer pairs), and of the far arrays (one per pair).
    /// </summary>
    public int Capacity { get; private set; }

    /// <summary>
    /// Slots of the far arrays: one per pair, at most <see cref="MaxFarLayers"/>.
    /// </summary>
    public int FarSlots { get; }

    /// <summary>
    /// Side of the far arrays' layers.
    /// </summary>
    public int FarSize => _farSize;

    /// <summary>
    /// Pixels per radian of the picture (<see cref="WorldRenderOptions.TerrainPixelScale"/>, at the render size): the pairs' near ranges scale with
    /// it.
    /// </summary>
    public float PixelScale { get; set; } = 2400;

    /// <summary>
    /// The biomes with a parameter row.
    /// </summary>
    public int TotalBiomes => _biomes.Count;

    /// <summary>
    /// The texture pairs.
    /// </summary>
    public int TotalPairs => _pairs.Count;

    /// <summary>
    /// Texture pairs being decoded plus map windows being cut or uploaded.
    /// </summary>
    public int Outstanding => _inFlight + (_mapJob is null && !_mapUploading ? 0 : 1);

    /// <summary>
    /// The biomes resident.
    /// </summary>
    public int ResidentBiomes => _state.Count(s => s.Resident);

    /// <summary>
    /// The resident biomes.
    /// </summary>
    public IEnumerable<BiomeTerrain> Resident => _biomes.Where((_, i) => _state[i].Resident);

    /// <summary>
    /// No decoding or uploading is outstanding for what the last <see cref="Update"/> needed.
    /// </summary>
    public bool Idle => _inFlight == 0 && _nearPending == 0 && (_mapJob is null && !_mapUploading || _mapTarget is not null)
        && _needed.All(b => _state[b].Pairs.All(p => p.FarReady || p.Failed)) && !_pairs.Values.Any(p => p.Loading)
        && (MapState != 1 || _mapWindows is null);

    /// <summary>
    /// GPU memory of the near and far layer arrays (all slots, all mip levels; a BC3 and a BC1 block are 24 bytes).
    /// </summary>
    public long ArrayBytes => Bytes(_layerSize, _levelCount, Capacity) + Bytes(_farSize, _farLevels, FarSlots);

    /// <summary>
    /// The ground's height under the eye (set each frame; unknown: none): the eye's cells count its height over it beyond the game's cameras
    /// (<see cref="EyeLift"/>), so a camera high above the land loads neither all the biomes under it nor their full sizes.
    /// </summary>
    public float GroundUnderEye { get; set; } = float.NegativeInfinity;

    /// <summary>
    /// Counts the writes to the biomes' parameter rows (a biome made resident, a pair's near slot): the terrain material cache bakes again on a
    /// change.
    /// </summary>
    public int MaterialVersion { get; private set; }

    /// <summary>
    /// Side of the height array's layers (<see cref="GroundRelief"/>): the layer size, at most 512.
    /// </summary>
    int HeightSize => Math.Min(_layerSize, 512);

    /// <summary>
    /// Call once per frame from the GL thread: finds the biomes the cells within <paramref name="materialDistance"/> of
    /// the eye use, starts decoding the ones not resident (nearest first), queues the uploads of what finished, and
    /// moves the overlay/colour window when the eye has left the middle of it.
    /// </summary>
    public void Update(Vector3 eye, float materialDistance, UploadQueue uploads, Vector3? ahead = null, IReadOnlyList<Vector3>? kept = null)
    {
        _frame++;
        while (_problems.TryDequeue(out var m))
            if (_printed++ < 20) Console.WriteLine($"warning   {m}");
        if (HasBiomes)
        {
            if (GroundRelief.FieldCentre is { } fieldCentre && _tiles.Length == 0)
                SetField(fieldCentre, GroundRelief.FieldCliffs ? GroundRelief.FieldTile / 2 : GroundRelief.FieldTile);
            while (_decoded.TryDequeue(out var d)) Install(d, uploads);
            GrowNear();
            UpdateBiomes(eye, materialDistance, ahead, kept);
        }
        UpdateMaps(eye, uploads, kept);
        if (_cellsDirty)
        {
            UploadCells();
            _fieldDirty = true;
        }
        if (_fieldDirty) UploadField();
    }

    /// <summary>
    /// Ranks the biomes wanted round <paramref name="eye"/> (<see cref="_needed"/>, nearest first), marks the pairs wanted at full size, and starts
    /// the loads they need: the biomes of the cells near <paramref name="eye"/> and, during a character flight, near its arrival eye <paramref
    /// name="ahead"/> (kept and loaded alike).
    /// </summary>
    void UpdateBiomes(Vector3 eye, float materialDistance, Vector3? ahead, IReadOnlyList<Vector3>? kept)
    {
        _stamp++;
        _needed.Clear();
        var (nearest, keptNear) = GatherBiomes(eye, materialDistance, ahead, kept);
        _needed.AddRange(nearest.OrderBy(p => p.Value).Select(p => p.Key));
        WantNearPairs(nearest, keptNear);
        StartPairs();
    }

    /// <summary>
    /// The biome rows within <paramref name="materialDistance"/> of the arrival, the eye and the kept points, each with its distance (plus the lead
    /// that orders them), and the kept points' own distances.
    /// </summary>
    (Dictionary<int, double> Nearest, Dictionary<int, double> KeptNear) GatherBiomes(Vector3 eye, float materialDistance, Vector3? ahead,
        IReadOnlyList<Vector3>? kept)
    {
        double cs = _info.CellSize, half = WorldLayout.HalfWorldSize;
        double radius = materialDistance + cs / 2;
        var nearest = new Dictionary<int, double>();
        foreach (int b in _pinned) nearest[b] = -1;   // the texture field's biomes first, wherever the eye is
        // During a flight the arrival's cells come first: the eye's (the flight path) count as farther than any of them.
        var keptNear = new Dictionary<int, double>();   // the kept points' own distances (without their lead), for the near slots
        void Gather(Vector3 at, double lead, float lift = 0, bool isKept = false)
        {
            int cx0 = (int)Math.Clamp(Math.Floor((at.X - radius + half) / cs), 0, CellsX - 1);
            int cx1 = (int)Math.Clamp(Math.Floor((at.X + radius + half) / cs), 0, CellsX - 1);
            int cz0 = (int)Math.Clamp(Math.Floor((at.Z - radius + half) / cs), 0, CellsZ - 1);
            int cz1 = (int)Math.Clamp(Math.Floor((at.Z + radius + half) / cs), 0, CellsZ - 1);
            for (int cz = cz0; cz <= cz1; cz++)
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    double x0 = cx * cs - half, z0 = cz * cs - half;
                    double dx = Math.Max(Math.Max(x0 - at.X, at.X - (x0 + cs)), 0), dz = Math.Max(Math.Max(z0 - at.Z, at.Z - (z0 + cs)), 0);
                    double d = Math.Sqrt(dx * dx + dz * dz + (double)lift * lift);
                    if (d > radius) continue;
                    for (int k = 0; k < BlendInfoFile.SlotsPerCell; k++)
                    {
                        if (!_rowOf.TryGetValue(_info.Slot(cx, cz, k), out int row)) continue;
                        if (!nearest.TryGetValue(row, out double best) || d + lead < best) nearest[row] = d + lead;
                        if (isKept && (!keptNear.TryGetValue(row, out double own) || d < own)) keptNear[row] = d;
                    }
                }
        }
        if (ahead is { } arrival) Gather(arrival, 0);
        Gather(eye, ahead is null ? 0 : radius, EyeLift.Of(eye, GroundUnderEye));
        // Kept points (the player's characters) come after the eye's own cells.
        if (kept is not null) foreach (var k in kept) Gather(k, radius * 2, isKept: true);
        return (nearest, keptNear);
    }

    /// <summary>
    /// Marks the pairs wanted at full size this frame (<see cref="Pair.Needed"/>): the eye's near ones, then the kept points' while slots last.
    /// </summary>
    void WantNearPairs(Dictionary<int, double> nearest, Dictionary<int, double> keptNear)
    {
        // A pair within its near range of the eye (the arrival's, the field's) is wanted at full size; the pixels per radian at the sampler's level
        // (an upscaler's negative bias reads finer levels).
        double pixels = PixelScale * Math.Pow(2, -Gpu.LodBias);
        int wantedNear = 0;
        foreach (int b in _needed)
            foreach (var p in _state[b].Pairs)
            {
                if (p.Needed != _stamp && nearest[b] < pixels * p.Tile / _layerSize * NearReach)
                {
                    (p.Needed, p.Used) = (_stamp, _frame);
                    wantedNear++;
                }
            }
        // Then a pair within its near range of a kept point, measured from the point, while slots are left over (the eye's come first), so a jump
        // to that character lands on full-size ground textures. Without the cap the kept pairs would hold slots the eye's need.
        if (keptNear.Count > 0)
            foreach (int b in _needed)
                if (keptNear.TryGetValue(b, out double own))
                    foreach (var p in _state[b].Pairs)
                    {
                        if (wantedNear < Capacity && p.Needed != _stamp && own < pixels * p.Tile / _layerSize * NearReach)
                        {
                            (p.Needed, p.Used) = (_stamp, _frame);
                            wantedNear++;
                        }
                    }
    }

    /// <summary>
    /// Starts the loads the needed pairs lack, nearest biome first: a far decode, or a near one while near slots are free and decodes in flight
    /// fewer than the most.
    /// </summary>
    void StartPairs()
    {
        // Near slots free or held by a pair not wanted now, less the near decodes that will take one.
        int room = Capacity - _nextFree - _nearLoading;
        foreach (var p in _owner) if (p is not null && p.Needed != _stamp) room++;
        _nearPending = 0;
        foreach (int b in _needed)
            foreach (var p in _state[b].Pairs)
            {
                if (p.Loading || p.Failed) continue;
                bool near = p.FarReady;
                if (near && (p.Needed != _stamp || p.Slot >= 0)) continue;
                if (near && room <= 0) continue;
                if (near) _nearPending++;
                if (_inFlight >= _maxInFlight) continue;
                if (near) room--;
                Start(p, near);
            }
    }

    /// <summary>
    /// Decodes <paramref name="pair"/> on a worker: its near layers (<paramref name="near"/>), or its far layers with its height.
    /// </summary>
    void Start(Pair pair, bool near)
    {
        pair.Loading = true;
        _inFlight++;
        if (near) _nearLoading++;
        int size = near ? _layerSize : _farSize;
        BackgroundWork.Run(() =>
        {
            Decoded result;
            try
            {
                var local = new List<string>();
                var d = LoadLayer(_assets, pair.Diffuse, size, true, [128, 128, 128, 60], local);
                var n = LoadLayer(_assets, pair.Normal, size, false, [128, 128, 255, 255], local);
                float steep = 0;
                var h = !near && GroundRelief.Enabled ? LoadHeight(_assets, pair.Normal, HeightSize, out steep) : null;
                foreach (var m in local) _problems.Enqueue(m);
                result = new Decoded(pair, near, d, n, h, steep);
            }
            catch (Exception e)
            {
                _problems.Enqueue($"terrain texture {pair.Diffuse}: {e.Message}");
                result = new Decoded(pair, near, null, null, null);
            }
            _decoded.Enqueue(result);
        });
    }

    /// <summary>
    /// Upload steps of a decoded pair: each large mip level of each array is one step, and the last makes it resident there. A far one is written at
    /// the pair's index (and its height, and its biomes' rows once all their pairs are in); a near one takes a free (or the least recently needed)
    /// near slot, or is dropped when none is left (the pair stays far). (The pair stays "loading" until the last step.)
    /// </summary>
    void Install(Decoded d, UploadQueue uploads)
    {
        var pair = d.Pair;
        if (d.Near) _nearLoading--;
        if (d.Diffuse is null || d.Normal is null)
        {
            pair.Loading = false;
            _inFlight--;
            pair.Failed = !d.Near;
            return;
        }

        int slot = d.Near ? AllocateSlot() : pair.Far;
        if (slot < 0)
        {
            pair.Loading = false;
            _inFlight--;
            return;
        }

        if (!d.Near) pair.Steep = d.Height is null ? 0 : d.Steep;
        bool far = !d.Near;
        QueueLayers(far, slot, d.Diffuse, d.Normal, uploads);
        if (far && _heightArray is not null) QueueHeight(d, slot, uploads);
        QueueResident(pair, far, slot, uploads);
    }

    /// <summary>
    /// The upload steps of a pair's <paramref name="diffuseLevels"/> and <paramref name="normalLevels"/> into its <paramref name="slot"/> of the
    /// far or the near arrays.
    /// </summary>
    void QueueLayers(bool far, int slot, byte[][] diffuseLevels, byte[][] normalLevels, UploadQueue uploads)
    {
        foreach (var (diffuse, levels) in new[] { (true, diffuseLevels), (false, normalLevels) })
        {
            int from = 0;
            for (int level = 0; level < levels.Length; level++)
            {
                // Small levels go together with the next large one; each step is at most about one level of 2 to 4 MB.
                bool last = level == levels.Length - 1;
                if (levels[level].Length < 256 << 10 && !last) continue;
                int a = from, b = level;
                from = level + 1;
                uploads.Add(() => UploadLevels(far, diffuse, levels, slot, a, b), "layer upload");
            }
        }
    }

    /// <summary>
    /// The upload step of a far pair's height levels (flat without one) and its relief factor at its <paramref name="slot"/>.
    /// </summary>
    void QueueHeight(Decoded d, int slot, UploadQueue uploads)
    {
        var height = d.Height ?? FlatHeight(HeightSize);
        uploads.Add(() =>
        {
            if (_heightArray is null) return;
            using var batch = Gpu.Uploads.Begin();
            for (int level = 0; level < height.Length; level++)
            {
                int s = Math.Max(HeightSize >> level, 1);
                batch.Write(_heightArray.Texture, level, slot, Rect(0, 0, s, s), height[level]);
            }
            if (_reliefTexture is not null)
                batch.Write(_reliefTexture.Texture, 0, 0, Rect(slot, 0, 1, 1), BitConverter.GetBytes(d.Height is null ? 0f : d.Steep));
        }, "height upload");
    }

    /// <summary>
    /// The last upload step of a pair: resident in the far arrays (its biomes' rows refreshed) or in its near <paramref name="slot"/>.
    /// </summary>
    void QueueResident(Pair pair, bool far, int slot, UploadQueue uploads)
    {
        uploads.Add(() =>
        {
            _inFlight--;
            pair.Loading = false;
            if (far)
            {
                pair.FarReady = true;
                foreach (int b in _usedBy[pair]) Refresh(b);
                return;
            }
            pair.Slot = slot;
            _owner[slot] = pair;
            foreach (int b in _usedBy[pair]) WriteNear(b);
        }, "pair resident");
    }

    /// <summary>
    /// Writes mip levels <paramref name="from"/> to <paramref name="to"/> of a pair's diffuse or normal into slot <paramref name="slot"/> of the near
    /// or far array.
    /// </summary>
    void UploadLevels(bool far, bool diffuse, byte[][] levels, int slot, int from, int to)
    {
        if ((far ? diffuse ? _diffuseFar : _normalFar : diffuse ? _diffuseArray : _normalArray) is not { } array) return;
        int size = far ? _farSize : _layerSize;
        using var batch = Gpu.Uploads.Begin();
        for (int level = from; level <= to; level++)
        {
            int s = Math.Max(size >> level, 1);
            batch.Write(array.Texture, level, slot, Rect(0, 0, s, s), levels[level]);
        }
    }

    /// <summary>
    /// Grows the near arrays to <see cref="NearLayers"/> slots per kept place (<see cref="Areas"/>, at most <see cref="MaxNearSlots"/>) when no layer
    /// is in flight and the guard allows it: there is no image copy, so every pair loses its slot (its biomes read the far arrays) and loads near
    /// again, nearest first. The arrays never shrink while running.
    /// </summary>
    void GrowNear()
    {
        int want = Math.Min(Math.Min(_pairs.Count, NearLayers * Math.Max(Areas, 1)), MaxNearSlots);
        if (GpuContext.DiagNoGrow || want <= Capacity || want >= _nearRefused || _inFlight > 0 || _nearLoading > 0 || _diffuseArray is null
            || _normalArray is null)
            return;
        if (Guard is { } guard && !guard.Allows((ulong)Bytes(_layerSize, _levelCount, want)))
        {
            _nearRefused = want;
            Console.WriteLine($"terrain   near layer slots not grown to {want}: memory budget (VramGuard)");
            return;
        }
        var diffuse = AllocateArray(true, want, _layerSize, _levelCount, "terrain textures diffuse");
        var normal = AllocateArray(false, want, _layerSize, _levelCount, "terrain textures normal");
        if (diffuse is null || normal is null)
        {
            diffuse?.Dispose();
            normal?.Dispose();
            _nearRefused = want;
            return;
        }
        var (oldDiffuse, oldNormal) = (_diffuseArray, _normalArray);
        (_diffuseArray, _normalArray) = (diffuse, normal);
        Gpu.DeferDelete(() =>
        {
            oldDiffuse.Dispose();
            oldNormal.Dispose();
        });
        foreach (var p in _owner) if (p is not null) p.Slot = -1;
        _owner = new Pair?[want];
        _nextFree = 0;
        Capacity = want;
        for (int b = 0; b < _state.Length; b++) WriteNear(b);
        Console.WriteLine($"terrain   near layer slots grown to {want} for {Areas} kept places ({Bytes(_layerSize, _levelCount, want) >> 20} MB)");
    }

    /// <summary>
    /// A free near slot, else the least recently needed one's of a pair not wanted near now (its biomes' rows drop it at once: they read the far
    /// arrays before the new pair's levels are written), else -1.
    /// </summary>
    int AllocateSlot()
    {
        if (_nextFree < Capacity) return _nextFree++;
        Pair? victim = null;
        foreach (var p in _owner)
            if (p is not null && p.Needed != _stamp && (victim is null || p.Used < victim.Used)) victim = p;
        if (victim is null) return -1;
        int slot = victim.Slot;
        victim.Slot = -1;
        _owner[slot] = null;
        foreach (int b in _usedBy[victim]) WriteNear(b);
        return slot;
    }

    /// <summary>
    /// A biome becomes resident once all its texture pairs are in the far arrays: its parameter row is written then (texels 0 and 1 the far
    /// indices, 1's W the relief factor GroundRelief.BiomeFactor, 13 and 14 the near slots, -1 for none).
    /// </summary>
    void Refresh(int b)
    {
        var s = _state[b];
        if (s.Resident || s.Pairs.Any(p => !p.FarReady)) return;
        var bt = _biomes[b];
        var tl = bt.Tiling;
        var l = s.Pairs.Select(p => (float)p.Far).ToArray();
        var n = s.Pairs.Select(p => (float)p.Slot).ToArray();
        float k = bt.DistortWavelength > 0 ? 1 / bt.DistortWavelength : 0;
        Vector4[] row =
        [
            new(l[0], l[1], l[2], l[3]),
            new(l[4], l[5], bt.BrightnessFix, GroundRelief.BiomeFactor(bt.StringId)),
            new(tl[1].X, tl[1].Y, tl[2].X, tl[2].Y),   // slope, cliff
            new(tl[0].X, tl[0].Y, tl[3].X, tl[3].Y),   // base, grass
            new(tl[4].X, tl[4].Y, tl[5].X, tl[5].Y),   // dirt, road
            bt.SlopeMin, bt.SlopeMax, bt.SlopeBlend, bt.OverlayMult,
            new(bt.GroundColour, bt.FadeDistance > 0 ? 1 / bt.FadeDistance : 0),
            new(k, bt.DistortAmplitude * 0.01f, 0, 0),
            new(bt.Absorbance[0], bt.Absorbance[1], bt.Absorbance[2], bt.Absorbance[3]),   // base, slope, cliff, grass
            new(bt.Absorbance[4], bt.Absorbance[5], 0, 0),                                 // dirt, road
            new(n[0], n[1], n[2], n[3]),
            new(n[4], n[5], 0, 0),
        ];
        WriteRow(b, 0, row);
        s.Resident = true;
        _cellsDirty = true;
    }

    /// <summary>
    /// A resident biome's near slots (texels 13 and 14 of its row) after one of its pairs got or lost one.
    /// </summary>
    void WriteNear(int b)
    {
        var s = _state[b];
        if (!s.Resident) return;
        var n = s.Pairs.Select(p => (float)p.Slot).ToArray();
        WriteRow(b, 13, [new(n[0], n[1], n[2], n[3]), new(n[4], n[5], 0, 0)]);
    }

    /// <summary>
    /// Writes <paramref name="values"/> into biome <paramref name="b"/>'s parameter row from texel <paramref name="texel"/>.
    /// </summary>
    void WriteRow(int b, int texel, Vector4[] values)
    {
        MaterialVersion++;
        var data = new float[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
            (data[i * 4], data[i * 4 + 1], data[i * 4 + 2], data[i * 4 + 3]) = (values[i].X, values[i].Y, values[i].Z, values[i].W);
        using var batch = Gpu.Uploads.Begin();
        batch.Write(_paramTexture!.Texture, 0, 0, Rect(texel, b, values.Length, 1),
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan()));
    }

    /// <summary>
    /// Cell table, two texels per cell: slots 0..3, then slot 4 in R; a resident biome's row, <see cref="Pending"/> or <see cref="Unused"/>.
    /// </summary>
    void UploadCells()
    {
        _cellsDirty = false;
        var cells = new byte[CellsX * 2 * CellsZ * 4];
        Array.Fill(cells, Unused);
        for (int cz = 0; cz < CellsZ; cz++)
            for (int cx = 0; cx < CellsX; cx++)
                for (int k = 0; k < BlendInfoFile.SlotsPerCell; k++)
                {
                    uint colour = _info.Slot(cx, cz, k);
                    byte v = colour == 0 ? Unused : _rowOf.TryGetValue(colour, out int row) ? (_state[row].Resident ? (byte)row : Pending) : Unused;
                    cells[(cz * CellsX * 2 + cx * 2) * 4 + k] = v;
                }
        if (_cellTexture is null) return;
        using var batch = Gpu.Uploads.Begin();
        batch.Write(_cellTexture.Texture, 0, 0, Rect(0, 0, CellsX * 2, CellsZ), cells);
    }

    /// <summary>
    /// The bytes of a BC3 and a BC1 array of <paramref name="slots"/> layers of <paramref name="size"/>² with <paramref name="levels"/> levels.
    /// </summary>
    static long Bytes(int size, int levels, int slots) =>
        Enumerable.Range(0, levels).Sum(l =>
        {
            long s = Math.Max(size >> l, 1);
            return (s + 3) / 4 * ((s + 3) / 4) * 24;
        }) * Math.Max(slots, 1);

    #endregion

    #region Biome lookups

    /// <summary>
    /// <c>biomemap.png</c> (null: none).
    /// </summary>
    FoliageBiomeMap? _biomeMap;

    /// <summary>
    /// Parameter row of the biome a TERRAIN-mode map feature at (x, z) is textured with: the one of <c>biomemap.png</c>
    /// at that point (docs/formats/foliage.md, "TERRAIN-mode meshes"), or -1 when it is not resident.
    /// </summary>
    public int FeatureBiomeRow(float x, float z) =>
        _biomeMap is not null && _rowOf.TryGetValue(_biomeMap.At(x, z), out int row) && _state[row].Resident ? row : -1;

    /// <summary>
    /// <see cref="FeatureBiomeRow"/> before the residency test: the row of the biome at (x, z), or -1 when it has none. Fixed per
    /// position; <see cref="FeatureBiomeRow"/> is this when <see cref="ResidentBiomeBits"/> has the row's bit, else -1.
    /// </summary>
    public int FeatureBiomeRowAny(float x, float z) =>
        _biomeMap is not null && _rowOf.TryGetValue(_biomeMap.At(x, z), out int row) ? row : -1;

    /// <summary>
    /// The biome of parameter row <paramref name="row"/> has all its layer textures resident (its parameter row is written).
    /// </summary>
    public bool IsResident(int row) => (uint)row < (uint)_state.Length && _state[row].Resident;

    /// <summary>
    /// What makes the biome's material what it is, as text for a cache key (docs/impostors.md section 13): its record's id, the six layers' texture
    /// names with the length and write time of the files they resolve to, tiling, slope ranges, overlay strengths, brightness, distortion and the
    /// layer size the textures are brought to. Null for a row outside the table.
    /// </summary>
    public string? BiomeKey(int row)
    {
        if ((uint)row >= (uint)_biomes.Count) return null;
        var b = _biomes[row];
        var sb = new System.Text.StringBuilder();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        sb.Append(inv, $"{b.StringId}|{_layerSize}|{b.BrightnessFix:R}|{b.DistortWavelength:R}|{b.DistortAmplitude:R}|{b.SlopeMin}|" +
            $"{b.SlopeMax}|{b.SlopeBlend}|{b.OverlayMult}|{b.GroundColour}|{b.FadeDistance:R}\n");
        for (int l = 0; l < 6; l++)
        {
            sb.Append(inv, $"{b.Tiling[l]}");
            foreach (var name in (ReadOnlySpan<string?>)[b.Diffuse[l], b.Normal[l]])
            {
                string? path = name is null ? null : _assets.Find(name) ?? _assets.Find(Path.GetFileName(name.Replace('\\', '/')));
                var info = path is null ? null : new FileInfo(path);
                sb.Append(inv, $"|{name}|{(info is { Exists: true } ? info.Length : -1)}|" +
                    $"{(info is { Exists: true } ? info.LastWriteTimeUtc.Ticks : 0)}");
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// A bit per resident biome row (row r: word r / 32, bit r % 32); rows beyond the span are left out (there are fewer than 254).
    /// </summary>
    public void ResidentBiomeBits(Span<uint> bits)
    {
        bits.Clear();
        for (int r = 0; r < _state.Length && r < bits.Length * 32; r++)
            if (_state[r].Resident) bits[r >> 5] |= 1u << (r & 31);
    }

    #endregion

    #region Layer decoding

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
            {
                try
                {
                    if (ReadLayerFile(path, size, levelCount, diffuse, out image) is { } stored) return stored;
                }
                catch (Exception e) when (e is DdsFormatException or InvalidOperationException or IOException or ArgumentException)
                {
                    problems.Add($"terrain texture {name}: {e.Message}");
                }
            }
        }
        if (image is null) return ConstantLayer(fallback, size, levelCount, diffuse);
        return EncodeLayer(image, size, diffuse);
    }

    /// <summary>
    /// The layer file at <paramref name="path"/>: its levels when <see cref="AsStored"/> takes it as stored (<paramref name="image"/> null), else
    /// null and the image decoded at its first level no smaller than <paramref name="size"/>.
    /// </summary>
    static byte[][]? ReadLayerFile(string path, int size, int levelCount, bool diffuse, out RgbaImage? image)
    {
        image = null;
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        Span<byte> magic = stackalloc byte[4];
        if (RandomAccess.Read(file, magic, 0) == 4
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(magic) == DdsReader.Magic)
        {
            // A DDS that is taken as stored is read from the level the layer starts at (the levels above are three quarters or more
            // of the file).
            var dds = DdsReader.ReadKept(file, w => TextureQuality.DropTopMips(w, StoredShift(w, size, levelCount)), out _);
            if (AsStored(dds, size, levelCount, diffuse) is { } stored) return stored;
            int level = 0;
            while (level + 1 < dds.MipCount && Math.Max(dds.Width >> (level + 1), 1) >= size) level++;
            image = DdsDecoder.Decode(dds, 0, level);
        }
        else image = TextureLoader.LoadImage(File.ReadAllBytes(path));
        return null;
    }

    /// <summary>
    /// A missing layer texture: every level of <paramref name="size"/>² a constant <paramref name="fallback"/> colour, BC3
    /// (<paramref name="diffuse"/>) or BC1.
    /// </summary>
    static byte[][] ConstantLayer(byte[] fallback, int size, int levelCount, bool diffuse)
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

    /// <summary>
    /// A decoded layer <paramref name="image"/> scaled to <paramref name="size"/>², given a mip chain and encoded as BC3 (<paramref
    /// name="diffuse"/>) or BC1.
    /// </summary>
    static byte[][] EncodeLayer(RgbaImage image, int size, bool diffuse)
    {
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
    /// (<see cref="NormalHeight.FromNormals"/>, in the frame the game lights it in), and <paramref name="steep"/> 1 / (side × the height's
    /// steepness): the depth, as a fraction of the tile, that gives a root-mean-square slope of 1. Null for a normal map that is missing or cannot be
    /// read (the layer stays flat).
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
                {
                    levels[l][y * s + x] = (byte)((src[2 * y * p + 2 * x] + src[2 * y * p + 2 * x + 1] + src[(2 * y + 1) * p + 2 * x]
                        + src[(2 * y + 1) * p + 2 * x + 1] + 2) / 4);
                }
        }
        return levels;
    }

    /// <summary>
    /// A layer texture decoded at its first level no smaller than <paramref name="size"/>; null when it is missing or cannot be read.
    /// </summary>
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
        catch (Exception e) when (e is DdsFormatException or InvalidOperationException or IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The height of a layer without one: 255 (the top) everywhere (its steepness factor 0 keeps it out of the relief).
    /// </summary>
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

    /// <summary>
    /// An RGBA8 image at <paramref name="size"/>²: box filter when shrinking, nearest when growing.
    /// </summary>
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
                        r += image.Pixels[o];
                        g += image.Pixels[o + 1];
                        b += image.Pixels[o + 2];
                        a += image.Pixels[o + 3];
                        n++;
                    }
                int d = (y * size + x) * 4;
                (result[d], result[d + 1], result[d + 2], result[d + 3]) = ((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
            }
        return result;
    }

    /// <summary>
    /// The top levels <see cref="AsStored"/> would not use of a DDS (the same conditions): how many to drop on reading, 0 when it is not taken as
    /// stored.
    /// </summary>
    static int StoredShift(DdsFile dds, int size, int levelCount)
    {
        if (dds.Format is not (DdsFormat.Bc1 or DdsFormat.Bc3) || dds.IsCubemap || dds.IsVolume || dds.ImageCount != 1) return 0;
        if (dds.Width != dds.Height || dds.Width < size || !int.IsPow2(dds.Width)) return 0;
        int shift = (int)Math.Log2(dds.Width / size);
        return shift + levelCount > dds.MipCount || dds.Surfaces.Count < dds.MipCount ? 0 : shift;
    }

    /// <summary>
    /// The levels of a DDS that can go into the array as stored (see <see cref="LoadLayer"/>), or null.
    /// </summary>
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

    /// <summary>
    /// The full mip chain of an RGBA8 image (power-of-two sides) by 2 Ã— 2 box filtering; level 0 is the image itself.
    /// </summary>
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
                    {
                        dst[(y * w + x) * 4 + c] = (byte)((src[(2 * y * pw + 2 * x) * 4 + c] + src[(2 * y * pw + x1) * 4 + c] +
                            src[(y1 * pw + 2 * x) * 4 + c] + src[(y1 * pw + x1) * 4 + c] + 2) / 4);
                    }
                }
            });
            levels[l] = dst;
        }
        return levels;
    }

    /// <summary>
    /// Every mip level of a square RGBA8 image (see <see cref="Mips(byte[], int, int, int, bool)"/>).
    /// </summary>
    internal static byte[][] Mips(byte[] baseLevel, int size) => Mips(baseLevel, size, size);

    #endregion

    #region Overlay and colour windows

    // Two textures addressed toroidally: texel (X mod W) holds pixel X of the window, so moving the window rewrites
    // only the strips that came into view, in place. (A fresh texture per window stalled the driver for ~50 ms at
    // its first use.) Mips stop at level 6, where a texel is 64 map pixels: the window origin is aligned to that.

    /// <summary>
    /// Mip levels of the window textures (0..6).
    /// </summary>
    const int MapLevels = 7;

    /// <summary>
    /// The overlay and colour maps' tiles and window cutting (null: the maps are missing).
    /// </summary>
    readonly MapWindows? _mapWindows;

    /// <summary>
    /// The window compose running on a worker (null: none).
    /// </summary>
    Task<MapWindows.Update>? _mapJob;

    /// <summary>
    /// A compose's upload is under way.
    /// </summary>
    bool _mapUploading;

    /// <summary>
    /// The drawn windows' origin in overlay pixels (<see cref="int.MinValue"/>: none yet).
    /// </summary>
    int _validPx = int.MinValue, _validPz = int.MinValue;

    /// <summary>
    /// The standby window pairs.
    /// </summary>
    readonly List<MapStandby> _mapStandbys = [];

    /// <summary>
    /// The standby the running compose and its upload write, or null: the drawn pair.
    /// </summary>
    MapStandby? _mapTarget;

    /// <summary>
    /// 0 no overlay/colour maps (missing), 1 still loading, 2 ready (<see cref="Region"/> is valid).
    /// </summary>
    public int MapState { get; private set; }

    /// <summary>
    /// x0, z0, 1/width, 1/depth of the overlay and colour windows in world units (the two cover the same square).
    /// </summary>
    public Vector4 Region { get; private set; }

    /// <summary>
    /// Places kept loaded (<see cref="WorldTextureCache.Areas"/>, set by <see cref="TerrainStreamer"/>).
    /// </summary>
    internal int Areas { get; set; } = 1;

    /// <summary>
    /// Video memory pressure: while it is on no standby pair is made or written.
    /// </summary>
    internal VramGuard? Guard { get; set; }

    /// <summary>
    /// A window pair uploaded but not drawn, round a kept point outside the drawn window ("Keep textures at characters"): when the eye arrives there
    /// it is swapped with the drawn pair (the shaders read whichever is current), and the pair it replaces stays as the standby for where the eye
    /// was.
    /// </summary>
    sealed class MapStandby
    {
        /// <summary>
        /// The pair's overlay and colour textures.
        /// </summary>
        public TerrainTexture Overlay = null!, Colour = null!;

        /// <summary>
        /// Its windows' origin in overlay pixels (<see cref="int.MinValue"/>: none yet).
        /// </summary>
        public int Px = int.MinValue, Pz = int.MinValue;

        /// <summary>
        /// Its windows are whole (false while a compose writes them).
        /// </summary>
        public bool Valid;
    }

    /// <summary>
    /// At most this many standby pairs (about 45 MB of video memory each: the 2048² RGBA8 overlay and the 4096² BC3 colour with their levels):
    /// one per kept place (<see cref="Areas"/> less the eye's), at least 2, at most 6.
    /// </summary>
    int MaxMapStandbys => Math.Clamp(Areas - 1, 2, 6);

    /// <summary>
    /// Makes the drawn overlay and colour window textures (the colour BC3 when the colour tiles allow blocks).
    /// </summary>
    void AllocateMaps()
    {
        _overlayTexture = EmptyMap(InternalFormat.Rgba8, _mapWindows!.OverlayPixels, MapLevels, "terrain overlay map");
        _colourTexture = EmptyMap(_mapWindows.CompressedColour ? FormatOf(diffuse: true) : InternalFormat.Rgba8, _mapWindows.ColourPixels, MapLevels,
            "terrain colour map");
    }

    /// <summary>
    /// Takes a finished compose into its upload; then, with no compose or upload under way, swaps in a standby round the eye, or composes the drawn
    /// window again when the eye is past the recentre distance, else a standby for a kept point.
    /// </summary>
    void UpdateMaps(Vector3 eye, UploadQueue uploads, IReadOnlyList<Vector3>? kept)
    {
        if (_mapWindows is null || MapState == 0) return;
        if (_mapJob is { IsCompleted: true } job)
        {
            _mapJob = null;
            MapWindows.Update? result = null;
            try
            {
                result = job.Result;
            }
            catch (AggregateException e)
            {
                Console.WriteLine($"warning   overlay maps: {e.InnerException?.Message ?? e.Message}");
            }
            if (result is null) { if (MapState == 1 && _mapTarget is null) MapState = 0; }
            else BeginMapUpload(result, uploads, _mapTarget);
        }
        if (_mapJob is not null || _mapUploading) return;
        var (px, pz) = _mapWindows.DesiredOrigin(eye.X, eye.Z);
        // In 64 bits: the unset origin is int.MinValue, and 0 - int.MinValue wraps back to it in 32 (Math.Abs then throws).
        static bool Near(int ax, int az, int bx, int bz, int reach) => Math.Abs((long)ax - bx) <= reach && Math.Abs((long)az - bz) <= reach;
        int recentre = _mapWindows.RecentreOverlayPixels;
        bool far = !Near(px, pz, _validPx, _validPz, recentre);
        if (MapState == 2 && far && _mapStandbys.Find(s => s.Valid && Near(px, pz, s.Px, s.Pz, recentre)) is { } ready)
        {
            // The eye jumped to a kept character: its standby is drawn at once (a move within the recentre distance follows as usual).
            (_overlayTexture, ready.Overlay) = (ready.Overlay, _overlayTexture!);
            (_colourTexture, ready.Colour) = (ready.Colour, _colourTexture!);
            (_validPx, ready.Px) = (ready.Px, _validPx);
            (_validPz, ready.Pz) = (ready.Pz, _validPz);
            Region = _mapWindows.RegionOf(_validPx, _validPz);
            return;
        }
        if (MapState == 2 && !far)
        {
            PrepareMapStandby(kept);
            return;
        }
        (int, int)? old = MapState == 2 ? (_validPx, _validPz) : null;
        _mapTarget = null;
        _mapJob = BackgroundWork.Run(() => _mapWindows.Compose(px, pz, old));
    }

    /// <summary>
    /// Starts composing a standby pair for the first kept point that is neither well inside the drawn window (a quarter of its width from its middle)
    /// nor near a standby's origin, in place of a standby no kept point is near (or a new one, up to <see cref="MaxMapStandbys"/>).
    /// </summary>
    void PrepareMapStandby(IReadOnlyList<Vector3>? kept)
    {
        if (kept is null || kept.Count == 0 || _mapWindows is not { } windows || Guard is { Pressure: true }) return;
        int recentre = windows.RecentreOverlayPixels, cover = windows.OverlayPixels / 4;
        static bool Near(int ax, int az, int bx, int bz, int reach) => Math.Abs((long)ax - bx) <= reach && Math.Abs((long)az - bz) <= reach;
        foreach (var point in kept)
        {
            var (kx, kz) = windows.DesiredOrigin(point.X, point.Z);
            if (Near(kx, kz, _validPx, _validPz, cover) || _mapStandbys.Any(s => s.Valid && Near(kx, kz, s.Px, s.Pz, recentre))) continue;
            var slot = _mapStandbys.Find(s => !kept.Any(k =>
            {
                var (x, z) = windows.DesiredOrigin(k.X, k.Z);
                return s.Valid && Near(x, z, s.Px, s.Pz, recentre);
            }));
            if (slot is null)
            {
                if (_mapStandbys.Count >= MaxMapStandbys) return;
                slot = new MapStandby
                {
                    Overlay = EmptyMap(InternalFormat.Rgba8, windows.OverlayPixels, MapLevels, "terrain overlay map standby"),
                    Colour = EmptyMap(windows.CompressedColour ? FormatOf(diffuse: true) : InternalFormat.Rgba8, windows.ColourPixels, MapLevels,
                        "terrain colour map standby"),
                };
                _mapStandbys.Add(slot);
            }
            (int, int)? old = slot.Valid ? (slot.Px, slot.Pz) : null;
            slot.Valid = false;   // written from now on
            _mapTarget = slot;
            _mapJob = BackgroundWork.Run(() => windows.Compose(kx, kz, old));
            return;
        }
    }

    /// <summary>
    /// Queues the upload of a compose's pieces, in slabs of about 2 MB, into the drawn pair or <paramref name="target"/>; the last step moves the
    /// drawn region (or marks the standby whole).
    /// </summary>
    void BeginMapUpload(MapWindows.Update update, UploadQueue uploads, MapStandby? target)
    {
        var (overlayTarget, colourTarget) = target is null ? (_overlayTexture, _colourTexture) : (target.Overlay, target.Colour);
        _mapUploading = true;
        // Every piece comes with its mip levels from the worker and goes up in slabs of about 2 MB: whole rows of pixels, of 4 × 4 blocks for BC3
        // colour.
        foreach (var piece in update.Pieces)
            for (int level = 0; level < piece.Levels.Length; level++)
            {
                var (p, lv) = (piece, level);
                int w = Math.Max(p.Width >> lv, 1), h = Math.Max(p.Height >> lv, 1), x = p.X >> lv, z = p.Z >> lv;
                bool blocks = p.Colour && _mapWindows!.CompressedColour;
                int unit = blocks ? 4 : 1, rowBytes = blocks ? w / 4 * 16 : w * 4, rowCount = h / unit;
                int rows = Math.Max(1, (2 << 20) / rowBytes);
                for (int row = 0; row < rowCount; row += rows)
                {
                    int r0 = row, n = Math.Min(rows, rowCount - row);
                    uploads.Add(() =>
                    {
                        using var batch = Gpu.Uploads.Begin();
                        batch.Write((p.Colour ? colourTarget : overlayTarget)!.Texture, lv, 0, Rect(x, z + r0 * unit, w, n * unit),
                            p.Levels[lv].AsSpan(r0 * rowBytes, n * rowBytes));
                    }, "map slab");
                }
            }
        uploads.Add(() =>
        {
            if (target is not null) (target.Px, target.Pz, target.Valid) = (update.Px0, update.Pz0, true);
            else
            {
                Region = _mapWindows!.RegionOf(update.Px0, update.Pz0);
                (_validPx, _validPz) = (update.Px0, update.Pz0);
                MapState = 2;
            }
            _mapUploading = false;
            update.Release();
        }, "map swap");
    }

    /// <summary>
    /// A window texture: <paramref name="levels"/> levels (the GL one had more allocated but sampled only these), trilinear, repeating.
    /// </summary>
    TerrainTexture EmptyMap(InternalFormat format, int size, int levels, string name) =>
        Make(format, size, size, levels, 1, TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear, TextureWrapMode.Repeat, name);

    #endregion

    #region Whole-world maps

    /// <summary>
    /// The whole-world ground colour texture is made.
    /// </summary>
    public bool HasGround { get; private set; }

    /// <summary>
    /// The whole-world colour map texture is made.
    /// </summary>
    public bool HasWorldColour { get; private set; }

    /// <summary>
    /// The ground colour and world colour textures from what <see cref="PrepareWorld"/> made.
    /// </summary>
    void BuildWorld(Source s)
    {
        if (s.Ground is { } ground && s.Blend is { } blend)
        {
            _groundTexture = Rgba8(blend.Width, blend.Height, ground, mipmaps: true, "terrain ground colour");
            HasGround = true;
        }
        if (s.WorldColour is { } colour)
        {
            _worldColourTexture = Rgba8(s.WorldColourSize, s.WorldColourSize, colour, mipmaps: true, "terrain world colour");
            HasWorldColour = true;
        }
    }

    /// <summary>
    /// Whole-world maps for the far terrain (CPU, any thread): the biomes' ground colour × brightness fix blended by the blend map (what the
    /// textured layers fade to, and finer than the game's 128² <c>distant.png</c>), and the colour map box-filtered from the colour tiles to
    /// <paramref name="colourSize"/>² (0: none).
    /// </summary>
    static void PrepareWorld(GameInstall install, Source s, int colourSize)
    {
        if (s.Blend is not { } blend) return;
        s.Ground = BakeGround(s, blend);
        if (colourSize <= 0) return;

        // 8 x 8 colour tiles, each box-filtered down to colourSize / 8.
        var tiles = Enumerable.Range(0, TerrainMaps.OverlayTiles * TerrainMaps.OverlayTiles)
            .Select(t => TerrainMaps.OverlayTile(install, "colour", t % TerrainMaps.OverlayTiles, t / TerrainMaps.OverlayTiles)).ToArray();
        string? cache = LoadCaches.Disabled ? null : WorldColourCache(tiles, colourSize);
        if (cache is not null && ReadWorldColour(cache, colourSize * colourSize * 4) is { } cached)
        {
            (s.WorldColour, s.WorldColourSize) = (cached, colourSize);
            return;
        }

        var colour = ShrinkColourTiles(tiles, colourSize);
        (s.WorldColour, s.WorldColourSize) = (colour, colourSize);
        if (cache is not null) WriteWorldColour(cache, colour);
    }

    /// <summary>
    /// The ground colour (RGBA8 at the blend map's size): the biomes' ground colour × brightness fix blended by <paramref name="blend"/>.
    /// </summary>
    static byte[] BakeGround(Source s, RgbaImage blend)
    {
        var all = s.All;
        var field = BiomeField.Bake(s.Info, blend.Width, blend.Height, blend.Pixels,
            c => all.TryGetValue(c, out var b) ? new Vector4(b.GroundColour * b.BrightnessFix, 1) : null, new Vector4(0.6f, 0.55f, 0.45f, 1));
        var rgba = new byte[field.Length * 4];
        for (int i = 0; i < field.Length; i++)
        {
            var v = Vector4.Clamp(field[i], Vector4.Zero, Vector4.One) * 255;
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = ((byte)v.X, (byte)v.Y, (byte)v.Z, 255);
        }
        return rgba;
    }

    /// <summary>
    /// The colour tiles (<paramref name="tiles"/>, 8 × 8, a missing one left black) box-filtered into one RGBA8 map of
    /// <paramref name="colourSize"/>².
    /// </summary>
    static byte[] ShrinkColourTiles(string[] tiles, int colourSize)
    {
        int part = colourSize / TerrainMaps.OverlayTiles;
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
                            r += img.Pixels[o];
                            g += img.Pixels[o + 1];
                            b += img.Pixels[o + 2];
                            n++;
                        }
                    if (n == 0) continue;
                    int d = ((tz * part + y) * colourSize + tx * part + x) * 4;
                    (colour[d], colour[d + 1], colour[d + 2], colour[d + 3]) = ((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
                }
        });
        return colour;
    }

    /// <summary>
    /// The world colour map's disk cache, <c>%LOCALAPPDATA%\Meitou\terrain</c> (neither read nor written with <c>--no-load-cache</c>): decoding the
    /// 64 colour tiles (2048² PNGs) to shrink them took over a second of every start. Named by a hash of the tiles' paths, sizes and times and the
    /// map's size, so a mod's tile counts.
    /// </summary>
    static string WorldColourCache(string[] tiles, int size)
    {
        var key = new System.Text.StringBuilder(FormattableString.Invariant($"world colour v1 {size}"));
        foreach (var path in tiles)
        {
            var file = new FileInfo(path);
            key.Append('|').Append(path).Append(file.Exists ? FormattableString.Invariant($" {file.Length} {file.LastWriteTimeUtc.Ticks}") : " none");
        }
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key.ToString()));
        var hash = Convert.ToHexString(digest)[..24].ToLowerInvariant();
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "terrain",
            $"worldcolour_{hash}.bin");
    }

    /// <summary>
    /// The cached world colour map at <paramref name="path"/>, or null when it is missing, unreadable or not <paramref name="length"/> bytes.
    /// </summary>
    static byte[]? ReadWorldColour(string path, int length)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            return bytes.Length == length ? bytes : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the world colour map cache through a temporary file and a move (an I/O failure leaves no file).
    /// </summary>
    static void WriteWorldColour(string path, byte[] colour)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllBytes(temporary, colour);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    #endregion

    #region Texture set

    /// <summary>
    /// The terrain material's textures (null where one does not exist yet), for the native draws that read them by bindless index; <c>FarLod</c> the
    /// near arrays' level from which a sample reads the far arrays (<see cref="FarShift"/> less the sampler's bias, which the far arrays' sampler
    /// adds too).
    /// </summary>
    internal readonly record struct TextureSet(TerrainTexture? Diffuse, TerrainTexture? Normal, TerrainTexture? Params, TerrainTexture? Cells,
        TerrainTexture? BlendMap, TerrainTexture? Overlay, TerrainTexture? Colour, TerrainTexture? Ground, TerrainTexture? WorldColour,
        TerrainTexture? Height, TerrainTexture? Relief, TerrainTexture? Field, TerrainTexture? DiffuseFar, TerrainTexture? NormalFar, float FarLod);

    /// <summary>
    /// The current textures, with the far arrays' level offset.
    /// </summary>
    internal TextureSet Textures => new(_diffuseArray, _normalArray, _paramTexture, _cellTexture, _blendTexture, _overlayTexture, _colourTexture,
        _groundTexture, _worldColourTexture, _heightArray,
        _reliefTexture, _fieldTexture, _diffuseFar, _normalFar, FarShift - Gpu.LodBias);

    #endregion

    #region Texture field

    /// <summary>
    /// The field's tiles, one texel each: biome row, layer, resident (null until <see cref="SetField"/>).
    /// </summary>
    TerrainTexture? _fieldTexture;

    /// <summary>
    /// The field's tiles, in layout order.
    /// </summary>
    FieldEntry[] _tiles = [];

    /// <summary>
    /// The biome rows the field uses, kept resident wherever the eye is.
    /// </summary>
    int[] _pinned = [];

    /// <summary>
    /// The field texture needs uploading.
    /// </summary>
    bool _fieldDirty;

    /// <summary>
    /// The texture field's lower left corner (world X, Z), tile side, tiles a row and tile count (0: no field).
    /// </summary>
    public Vector2 FieldOrigin { get; private set; }

    /// <summary>
    /// See <see cref="FieldOrigin"/>.
    /// </summary>
    public float FieldTile { get; private set; }

    /// <summary>
    /// See <see cref="FieldOrigin"/>.
    /// </summary>
    public int FieldColumns { get; private set; }

    /// <summary>
    /// See <see cref="FieldOrigin"/>.
    /// </summary>
    public int FieldCount => _tiles.Length;

    /// <summary>
    /// One tile of the texture field: the biome row it is drawn with, the layer it shows, and what to call it.
    /// </summary>
    internal readonly record struct FieldEntry(int Row, TerrainLayer Layer, string Label);

    /// <summary>
    /// Lays the texture field out round <paramref name="centre"/>: one square tile of side <paramref name="tile"/> for each distinct ground texture
    /// pair (the base, slope, grass, dirt and road layers of every biome; with <see cref="GroundRelief.FieldCliffs"/> the cliff layers instead, whose
    /// vertical projections need a steep face), ordered by layer then name, from the first biome that uses it; those biomes are kept resident
    /// wherever the eye is.
    /// </summary>
    public void SetField(Vector2 centre, float tile)
    {
        var seen = new HashSet<(string?, string?)>();
        var entries = new List<FieldEntry>();
        foreach (var layer in GroundRelief.FieldCliffs ? [TerrainLayer.Cliff] : new[] { TerrainLayer.Base, TerrainLayer.Slope, TerrainLayer.Grass,
            TerrainLayer.Dirt, TerrainLayer.Road })
            for (int b = 0; b < _biomes.Count; b++)
            {
                var pair = _state[b].Pairs[(int)layer];
                if (pair.Normal is null || !seen.Add((pair.Diffuse?.ToLowerInvariant(), pair.Normal.ToLowerInvariant()))) continue;
                entries.Add(new FieldEntry(b, layer,
                    $"{Path.GetFileNameWithoutExtension(pair.Normal.Replace('\\', '/'))} ({_biomes[b].Name}: {layer})"));
            }
        _tiles = [.. entries.OrderBy(e => e.Layer).ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)];
        FieldColumns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(_tiles.Length)));
        int rows = (_tiles.Length + FieldColumns - 1) / FieldColumns;
        FieldTile = tile;
        FieldOrigin = centre - new Vector2(FieldColumns, rows) * tile / 2;
        _pinned = [.. _tiles.Select(e => e.Row).Distinct()];
        _fieldTexture ??= Make(InternalFormat.Rgba8Ui, Math.Max(_tiles.Length, 1), 1, 1, 1, TextureMinFilter.Nearest, TextureMagFilter.Nearest,
            TextureWrapMode.ClampToEdge, "terrain texture field");
        _fieldDirty = true;
        int needed = _pinned.SelectMany(b => _state[b].Pairs).Distinct().Count();
        Console.WriteLine($"field     {_tiles.Length} ground textures from {_pinned.Length} biomes ({needed} texture pairs kept resident, " +
            $"{Capacity} near slots), {FieldColumns} a row, tiles {tile:0} units, from {FieldOrigin.X:0}, {FieldOrigin.Y:0}");
    }

    /// <summary>
    /// The texture field's tile at world (<paramref name="x"/>, <paramref name="z"/>): its label, or null outside the field.
    /// </summary>
    public string? FieldLabel(float x, float z)
    {
        if (_tiles.Length == 0) return null;
        int cx = (int)MathF.Floor((x - FieldOrigin.X) / FieldTile), cz = (int)MathF.Floor((z - FieldOrigin.Y) / FieldTile);
        int i = cz * FieldColumns + cx;
        if (cx < 0 || cz < 0 || cx >= FieldColumns || i >= _tiles.Length) return null;
        var e = _tiles[i];
        var pair = _state[e.Row].Pairs[(int)e.Layer];
        float tile = 5000 / Math.Max((_biomes[e.Row].Tiling[(int)e.Layer].X + _biomes[e.Row].Tiling[(int)e.Layer].Y) / 2, 1e-3f);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{i + 1}/{_tiles.Length}  {e.Label}  tile {tile / 10:0.#} m, depth {GroundRelief.LayerDepthCm(pair.Steep, tile)
                * GroundRelief.BiomeFactor(_biomes[e.Row].StringId) * (e.Layer == TerrainLayer.Cliff ? GroundRelief.CliffScale : 1):0.#} cm");
    }

    /// <summary>
    /// The field's tiles, one texel each: R biome row, G layer, B 1 once the biome is resident (until then the tile shows the world).
    /// </summary>
    void UploadField()
    {
        _fieldDirty = false;
        if (_fieldTexture is null || _tiles.Length == 0) return;
        var texels = new byte[_tiles.Length * 4];
        for (int i = 0; i < _tiles.Length; i++)
        {
            (texels[i * 4], texels[i * 4 + 1], texels[i * 4 + 2]) = ((byte)_tiles[i].Row, (byte)_tiles[i].Layer,
                (byte)(_state[_tiles[i].Row].Resident ? 1 : 0));
        }
        using var batch = Gpu.Uploads.Begin();
        batch.Write(_fieldTexture.Texture, 0, 0, Rect(0, 0, _tiles.Length, 1), texels);
    }

    #endregion
}

/// <summary>
/// A native texture of the terrain's (phase 8, docs/renderer-native.md 8) sampled as it was as a GL texture through VkGl: the GL sampler
/// state it was given (<see cref="SamplerDesc.FromGl"/>, the upscaler's LOD bias on mipmapped filters, as VkGl's <c>SamplerFor</c>) and the
/// view of all its levels (it is made with exactly the levels VkGl's view covered). <see cref="Bindless"/> keeps the former GL layer's rule:
/// the same index while the sampler is unchanged, a new one (the old freed after the frames in flight) when the LOD bias moved. Render thread only.
/// </summary>
internal sealed class TerrainTexture : IDisposable
{
    /// <summary>
    /// The context the texture, its samplers and its bindless entry belong to.
    /// </summary>
    readonly IGpuContext _ctx;

    /// <summary>
    /// The GL minification filter it was given.
    /// </summary>
    readonly TextureMinFilter _min;

    /// <summary>
    /// The GL magnification filter.
    /// </summary>
    readonly TextureMagFilter _mag;

    /// <summary>
    /// The GL wrap of S and T.
    /// </summary>
    readonly TextureWrapMode _wrap;

    /// <summary>
    /// The anisotropy (1: none).
    /// </summary>
    readonly float _anisotropy;

    /// <summary>
    /// An integer format (sampled without filtering).
    /// </summary>
    readonly bool _integer;

    /// <summary>
    /// The LOD bias <see cref="_cached"/> was made with (NaN: none yet).
    /// </summary>
    float _cachedBias = float.NaN;

    /// <summary>
    /// The sampler cache generation <see cref="_cached"/> was made in.
    /// </summary>
    int _cachedGeneration;

    /// <summary>
    /// The sampler and view at <see cref="_cachedBias"/>.
    /// </summary>
    SampledTexture _cached;

    /// <summary>
    /// The bindless entry.
    /// </summary>
    BindlessHandle _handle;

    /// <summary>
    /// What the entry holds.
    /// </summary>
    SampledTexture _registered;

    /// <summary>
    /// <see cref="_handle"/> is registered.
    /// </summary>
    bool _hasEntry;

    /// <summary>
    /// A texture with the GL sampler state it stands for; <paramref name="anisotropy"/> 1 is none.
    /// </summary>
    public TerrainTexture(IGpuContext ctx, GpuTexture texture, TextureMinFilter min, TextureMagFilter mag, TextureWrapMode wrap, float anisotropy = 1)
    {
        this._ctx = ctx;
        Texture = texture;
        (this._min, this._mag, this._wrap, this._anisotropy) = (min, mag, wrap, anisotropy);
        _integer = GlConventions.IsIntegerFormat(texture.Desc.Format);
    }

    /// <summary>
    /// The native texture.
    /// </summary>
    public GpuTexture Texture { get; }

    /// <summary>
    /// The sampler and view a draw samples it with now (what VkGl's <c>Sampled</c> gave for the GL texture).
    /// </summary>
    public SampledTexture Sampled()
    {
        float bias = _ctx.LodBias;
        if (!(bias == _cachedBias) || _cachedGeneration != _ctx.Samplers.Generation)
        {
            var sampler = _ctx.Samplers.Get(SamplerDesc.FromGl(_min, _mag, _wrap, _wrap, TextureWrapMode.Repeat, false, DepthFunction.Lequal, false,
                _anisotropy, _integer, bias));
            (_cached, _cachedBias, _cachedGeneration) = (new SampledTexture(sampler, Texture.View()), bias, _ctx.Samplers.Generation);
        }
        return _cached;
    }

    /// <summary>
    /// Its bindless entry for <see cref="Sampled"/> now (usable in the current frame). In Prepare, on the render thread.
    /// </summary>
    public BindlessHandle Bindless()
    {
        var s = Sampled();
        if (_hasEntry && _registered == s) return _handle;
        if (_hasEntry) _ctx.Bindless.Free(_handle);
        var kind = BindlessTable.KindFor(Texture.Desc.Format, Texture.Desc.Kind);
        (_handle, _registered, _hasEntry) = (new BindlessHandle(kind, _ctx.Bindless.Register(kind, s)), s, true);
        return _handle;
    }

    /// <summary>
    /// Frees the bindless entry and the texture.
    /// </summary>
    public void Dispose()
    {
        if (_hasEntry) _ctx.Bindless.Free(_handle);
        _hasEntry = false;
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
    /// <summary>
    /// Window edge in overlay pixels (36 units each).
    /// </summary>
    const int WindowPixels = 2048;

    /// <summary>
    /// The decoded overlay tiles.
    /// </summary>
    readonly TileCache _overlay;

    /// <summary>
    /// The decoded colour tiles (null: the colour window is BC3 from <see cref="_colourBlocks"/>).
    /// </summary>
    readonly TileCache? _colour;

    /// <summary>
    /// The colour tiles' BC3 blocks (null: the colour window is RGBA8).
    /// </summary>
    readonly BlockTiles? _colourBlocks;

    /// <summary>
    /// The overlay tiles' side and the colour tiles' (pixels), and colour pixels per overlay pixel.
    /// </summary>
    readonly int _overlayTile, _colourTile, _ratio;

    /// <summary>
    /// The alignment of the window's origin in overlay pixels: the mip level 6 texel (64), and for BC3 colour whole blocks at every colour level.
    /// </summary>
    readonly int _align;

    /// <summary>
    /// World units per overlay pixel.
    /// </summary>
    readonly double _overlayUnit;

    /// <summary>
    /// Windows over the given tiles; the colour tiles must be a whole multiple of the overlay tiles, and BC3 needs a window aligned to whole blocks.
    /// </summary>
    MapWindows(TileCache overlay, TileCache? colour, BlockTiles? colourBlocks, int overlayTile, int colourTile)
    {
        this._overlay = overlay;
        this._colour = colour;
        this._colourBlocks = colourBlocks;
        this._overlayTile = overlayTile;
        this._colourTile = colourTile;
        _overlayUnit = (double)WorldLayout.WorldSize / (overlayTile * TerrainMaps.OverlayTiles);
        _ratio = Math.Max((int)Math.Round((double)colourTile / overlayTile), 1);
        if (_ratio * overlayTile != colourTile)
            throw new InvalidDataException($"colour tiles ({colourTile}) are not a whole multiple of overlay tiles ({overlayTile})");
        _align = 64;
        if (colourBlocks is null) return;
        while (_align * _ratio % TerrainColourCache.Granule != 0) _align += 64;
        if (OverlayPixels % _align != 0 || overlayTile * TerrainMaps.OverlayTiles % _align != 0)
        {
            throw new InvalidDataException($"the window ({OverlayPixels}) or the overlay map ({overlayTile * TerrainMaps.OverlayTiles}) " +
                $"is not a multiple of {_align}");
        }
    }

    /// <summary>
    /// Opens the overlay and colour tiles (the colour as BC3 when its tiles allow it, else RGBA8, noted in <paramref name="messages"/>).
    /// </summary>
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
        // The game's overlay tiles are 1024² (4 MB decoded) and the 2048-pixel window spans 3 x 3 of them: those nine stay between composes
        // (Compose), the rest are dropped, instead of pinning every decoded map for the session.
        var o = Cache("new_overlay", 9, out int ot);
        var firstColour = TerrainMaps.OverlayTile(install, "colour", 0, 0);
        try
        {
            int size = TerrainColourCache.Locate(firstColour)?.Size
                ?? TerrainColourCache.Get(firstColour)?.Size
                ?? throw new FileNotFoundException($"missing {firstColour}");
            return new MapWindows(o, null, new BlockTiles(install), ot, size);
        }
        catch (InvalidDataException e)
        {
            messages.Add($"colour map kept uncompressed: {e.Message}");
        }
        var c = Cache("colour", 2, out int ct);
        return new MapWindows(o, c, null, ot, ct);
    }

    /// <summary>
    /// Whether the colour window is BC3 (its pieces hold blocks).
    /// </summary>
    public bool CompressedColour => _colourBlocks is not null;

    /// <summary>
    /// The window's side in overlay pixels (at most the whole map's).
    /// </summary>
    public int OverlayPixels => Math.Min(WindowPixels, _overlayTile * TerrainMaps.OverlayTiles);

    /// <summary>
    /// The window's side in colour pixels.
    /// </summary>
    public int ColourPixels => OverlayPixels * _ratio;

    /// <summary>
    /// When the window's origin (overlay pixels) differs by more than this from the desired one, it moves (4608 units).
    /// </summary>
    public int RecentreOverlayPixels => Math.Max((int)(4608 / _overlayUnit), _align);

    /// <summary>
    /// Origin (overlay pixels) of a window centred on a point, aligned and kept inside the maps.
    /// </summary>
    public (int X, int Z) DesiredOrigin(double x, double z)
    {
        int full = _overlayTile * TerrainMaps.OverlayTiles, size = OverlayPixels;
        int Axis(double v) => Math.Clamp((int)Math.Round(((v + WorldLayout.HalfWorldSize) / _overlayUnit - size / 2.0) / _align) * _align, 0,
            full - size);
        return (Axis(x), Axis(z));
    }

    /// <summary>
    /// x0, z0, 1/width, 1/depth in world units of the window at an origin.
    /// </summary>
    public Vector4 RegionOf(int px, int pz) =>
        new((float)(px * _overlayUnit - WorldLayout.HalfWorldSize), (float)(pz * _overlayUnit - WorldLayout.HalfWorldSize),
            (float)(1 / (OverlayPixels * _overlayUnit)), (float)(1 / (OverlayPixels * _overlayUnit)));

    /// <summary>
    /// A rectangle of one texture: its first texel (already wrapped) and the mip levels of its pixels.
    /// </summary>
    public sealed record Piece(bool Colour, int X, int Z, int Width, int Height, byte[][] Levels);

    /// <summary>
    /// What to write to the textures to show the window at (<see cref="Px0"/>, <see cref="Pz0"/>).
    /// </summary>
    public sealed record Update(int Px0, int Pz0, List<Piece> Pieces)
    {
        /// <summary>
        /// Returns the levels (rented from the shared pool, sized at least as needed) once they are uploaded.
        /// </summary>
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
        int cs = size * _ratio;
        foreach (var (rx, rz, rw, rh) in rects)
            foreach (var (sx, sw) in Wrap(rx, rw, size))
                foreach (var (sz, sh) in Wrap(rz, rh, size))
                {
                    var data = Cut(_overlay, _overlayTile, sx, sz, sw, sh);
                    pieces.Add(new Piece(false, sx % size, sz % size, sw, sh, TerrainTextures.Mips(data, sw, sh, 7, pooled: true)));
                    var clevels = _colourBlocks is not null
                        ? CutBlocks(_colourBlocks, _colourTile, sx * _ratio, sz * _ratio, sw * _ratio, sh * _ratio)
                        : TerrainTextures.Mips(Cut(_colour!, _colourTile, sx * _ratio, sz * _ratio, sw * _ratio, sh * _ratio), sw * _ratio,
                            sh * _ratio, 7, pooled: true);
                    pieces.Add(new Piece(true, sx * _ratio % cs, sz * _ratio % cs, sw * _ratio, sh * _ratio, clevels));
                }
        var update = new Update(px0, pz0, pieces);
        // The pieces own pooled copies of what they need. The overlay tiles stay (the window's 3 x 3, Open): the window moves every 4,608 units, a
        // tile spans ~37,000, so the next compose cuts the same ones, and decoding their PNGs again was 6-29 MB of fresh arrays a move. The
        // uncompressed colour tiles are larger and go.
        _colour?.Clear();
        _colourBlocks?.Clear();
        return update;
    }

    /// <summary>
    /// Splits [start, start + length) at multiples of <paramref name="period"/> (the texture wraps there).
    /// </summary>
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

    /// <summary>
    /// Cuts a w × h RGBA8 rectangle at (<paramref name="x0"/>, <paramref name="z0"/>) out of the map of <paramref name="cache"/>'s tiles (missing
    /// tiles stay zero); the array is rented from <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
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
        var levels = new byte[TerrainColourCache.Levels][];
        for (int l = 0; l < levels.Length; l++)
        {
            int bw = (w >> l) / 4, bh = (h >> l) / 4;
            levels[l] = ArrayPool<byte>.Shared.Rent(bw * bh * 16);
            Array.Clear(levels[l], 0, bw * bh * 16);
        }
        foreach (var (tx, tz) in keys)
        {
            var (stored, memory) = cache.Get(tx, tz);
            if ((stored?.Size ?? memory?.Size) != tile) continue;   // missing, or of another size than the first: zero blocks
            using var file = stored is not null ? File.OpenHandle(stored.Path) : null;
            for (int l = 0; l < levels.Length; l++)
            {
                // In blocks at this level.
                int bx0 = (x0 >> l) / 4, bz0 = (z0 >> l) / 4, bw = (w >> l) / 4, bh = (h >> l) / 4, tb = (tile >> l) / 4;
                int ax0 = Math.Max(bx0, tx * tb), ax1 = Math.Min(bx0 + bw, (tx + 1) * tb);
                int az0 = Math.Max(bz0, tz * tb), az1 = Math.Min(bz0 + bh, (tz + 1) * tb);
                for (int z = az0; z < az1; z++)
                {
                    int from = ((z - tz * tb) * tb + (ax0 - tx * tb)) * 16, length = (ax1 - ax0) * 16;
                    var into = levels[l].AsSpan(((z - bz0) * bw + (ax0 - bx0)) * 16, length);
                    if (file is not null) TerrainColourCache.ReadBlocks(file, stored!, l, from, into);
                    else memory!.Levels[l].AsSpan(from, length).CopyTo(into);
                }
            }
        }
        return levels;
    }

    /// <summary>
    /// The colour tiles' cache files (<see cref="TerrainColourCache.Locate"/>), kept for the session: a compose reads the block rows it needs from
    /// them. Without a cache file (the caches off, a failed write) a tile's levels are read whole and dropped after the compose like the decoded
    /// tiles.
    /// </summary>
    sealed class BlockTiles(GameInstall install)
    {
        /// <summary>
        /// The tiles' cache files (null: none could be made).
        /// </summary>
        readonly Dictionary<(int, int), TerrainColourCache.Stored?> _stored = [];

        /// <summary>
        /// Tiles read whole without a cache file (null: unfit for blocks).
        /// </summary>
        readonly Dictionary<(int, int), TerrainColourCache.Tile?> _memory = [];

        /// <summary>
        /// A tile's cache file, or its levels when it has none (both null: missing or unfit for blocks).
        /// </summary>
        public (TerrainColourCache.Stored? Stored, TerrainColourCache.Tile? Memory) Get(int tx, int tz)
        {
            lock (_stored)
            {
                if (_stored.TryGetValue((tx, tz), out var file) && file is not null) return (file, null);
                if (_memory.TryGetValue((tx, tz), out var hit)) return (null, hit);
            }
            var path = TerrainMaps.OverlayTile(install, "colour", tx, tz);
            try
            {
                if (TerrainColourCache.Locate(path) is { } found)
                {
                    lock (_stored) _stored[(tx, tz)] = found;
                    return (found, null);
                }
                var tile = TerrainColourCache.Get(path);
                lock (_stored) _memory[(tx, tz)] = tile;
                return (null, tile);
            }
            catch (InvalidDataException)
            {
                // A tile unfit for blocks: left out, as a missing one.
                lock (_stored) _memory[(tx, tz)] = null;
                return (null, null);
            }
        }

        /// <summary>
        /// Drops the tiles read whole.
        /// </summary>
        public void Clear()
        {
            lock (_stored) _memory.Clear();
        }
    }

    /// <summary>
    /// The decoded tiles of one map (<paramref name="kind"/>), at most <paramref name="max"/> kept, least recently used dropped first.
    /// </summary>
    sealed class TileCache(GameInstall install, string kind, int max)
    {
        /// <summary>
        /// The decoded tiles (null: missing) and the clock they were last used at.
        /// </summary>
        readonly Dictionary<(int, int), (RgbaImage? Image, long Used)> _tiles = [];

        /// <summary>
        /// Counts the uses.
        /// </summary>
        long _clock;

        /// <summary>
        /// A decoded tile (null when its file is missing).
        /// </summary>
        public RgbaImage? Get(int tx, int tz)
        {
            lock (_tiles)
                if (_tiles.TryGetValue((tx, tz), out var hit))
                {
                    _tiles[(tx, tz)] = (hit.Image, ++_clock);
                    return hit.Image;
                }
            var path = TerrainMaps.OverlayTile(install, kind, tx, tz);
            var img = File.Exists(path) ? TextureLoader.LoadImage(File.ReadAllBytes(path)) : null;
            lock (_tiles) _tiles[(tx, tz)] = (img, ++_clock);
            return img;
        }

        /// <summary>
        /// Drops every decoded tile after a compose has copied what it needs.
        /// </summary>
        public void Clear()
        {
            lock (_tiles) _tiles.Clear();
        }

        /// <summary>
        /// Drops the least recently used tiles beyond the cache size, keeping the ones just used.
        /// </summary>
        public void Trim(List<(int, int)> keep)
        {
            lock (_tiles)
                while (_tiles.Count > Math.Max(max, keep.Count))
                {
                    var victim = _tiles.Where(p => !keep.Contains(p.Key)).OrderBy(p => p.Value.Used).Select(p => ((int, int)?)p.Key).FirstOrDefault();
                    if (victim is null) break;
                    _tiles.Remove(victim.Value);
                }
        }
    }
}