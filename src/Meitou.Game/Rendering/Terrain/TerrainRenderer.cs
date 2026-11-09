using System.Numerics;
using Meitou.Data.World;

using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Shaders;
using Meitou.Game.Rendering.Impostors;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Shadows;
using Meitou.Game.Rendering.Sky;

namespace Meitou.Game.Rendering.Terrain;

/// <summary>
/// Switches for drawing the world (keys in <see cref="WorldApp"/>).
/// </summary>
public sealed class WorldRenderOptions
{
    /// <summary>
    /// The terrain and the objects take their textures (false: plain shading).
    /// </summary>
    public bool Textures { get; set; } = true;

    /// <summary>
    /// Normal maps are sampled (with <see cref="Textures"/>).
    /// </summary>
    public bool NormalMaps { get; set; } = true;

    /// <summary>
    /// The world's objects are drawn.
    /// </summary>
    public bool Objects { get; set; } = true;

    /// <summary>
    /// The water is drawn.
    /// </summary>
    public bool Water { get; set; } = true;

    /// <summary>
    /// The water reflects the mirrored scene (<see cref="ReflectionPass"/>) instead of just the sky colour.
    /// </summary>
    public bool Reflections { get; set; } = true;

    /// <summary>
    /// 0 solid, 1 solid + wireframe, 2 wireframe only.
    /// </summary>
    public int Wireframe { get; set; }

    /// <summary>
    /// The sandbox's floor grid drawn into the terrain (depth-tested, so characters hide it).
    /// </summary>
    public bool Grid { get; set; }

    /// <summary>
    /// 0 normal, 1 blend-map slot weights, 2 layer weights (R cliff, G slope, B grass), 3 plain shading.
    /// </summary>
    public int Debug { get; set; }

    /// <summary>
    /// Terrain LOD (<see cref="Meitou.Data.World.TerrainLod"/>): the screen-space height error allowed near the eye, in pixels of the picture
    /// rendered (<c>--terrain-error</c>, the Tab slider), the same at every distance (no far ramp: it made pond outlines blocky).
    /// </summary>
    public float TerrainPixelError { get; set; } = DefaultTerrainPixelError;

    /// <summary>
    /// The default <see cref="TerrainPixelError"/> (pixels).
    /// </summary>
    public const float DefaultTerrainPixelError = 10;

    /// <summary>
    /// Pixels per world unit at distance 1 in the picture being drawn: the frame sets it from the render height (after the upscaler's render scale)
    /// and the field of view, the reflection pass from its own (half-size) target.
    /// </summary>
    public float TerrainPixelScale { get; set; } = TerrainLod.ProjectionScale(720, 50 * MathF.PI / 180);

    /// <summary>
    /// The LOD rule these options give.
    /// </summary>
    public TerrainLod TerrainLod => new(TerrainPixelScale, TerrainPixelError, TerrainPixelError, 0);

    /// <summary>
    /// Beyond this distance the terrain takes the biomes' ground colour (the game's material distance is 30000).
    /// </summary>
    public float MaterialDistance { get; set; } = 30000;
}

/// <summary>
/// Sun and sky light for the world shaders (from <see cref="WorldSky"/>).
/// </summary>
public readonly record struct WorldLighting(Vector3 SunDirection, Vector3 SunColour, Vector3 AmbientSky, Vector3 AmbientGround, Vector3 FogColour,
    float FogDistance,
    Vector3 GlintDirection = default, Vector3 GlintColour = default)
{
    /// <summary>
    /// What the water's specular glint is lit by: the light's own direction and colour (<see cref="SunDirection"/>, <see cref="SunColour"/>) unless a
    /// separate glint light was given (<see cref="GlintDirection"/> not zero): at night under planetshine, the sun's radiance plus the planet's own
    /// (unboosted) disc radiance, where <see cref="SunColour"/> is the strength-scaled light that lights the land.
    /// </summary>
    public (Vector3 Direction, Vector3 Colour) Glint => GlintDirection == default ? (SunDirection, SunColour) : (GlintDirection, GlintColour);

    /// <summary>
    /// A midday sun and sky for when no sky is running.
    /// </summary>
    public static readonly WorldLighting Default = new(Vector3.Normalize(new Vector3(0.45f, 0.75f, 0.3f)), new(1, 0.96f, 0.88f),
        new(0.36f, 0.40f, 0.46f), new(0.20f, 0.19f, 0.17f), new(0.62f, 0.68f, 0.74f), 60000);
}

/// <summary>
/// Draws the terrain of the whole world with continuous LOD (<see cref="TerrainQuadtree"/>): one shared grid patch placed per selected quadtree node,
/// heights read in the vertex shader from a coarse whole-world height texture and the loaded region's fine one, normals from the height field per
/// pixel. Nodes outside the view frustum are skipped.
/// </summary>
public sealed unsafe class TerrainRenderer : IDisposable
{
    #region Construction

    /// <summary>
    /// <c>MEITOU_METAL_PRIVATE_STATIC=1</c>: the patch grid's buffers are made private to the GPU.
    /// </summary>
    static readonly bool PrivateStatics = Environment.GetEnvironmentVariable("MEITOU_METAL_PRIVATE_STATIC") == "1";

    /// <summary>
    /// Cells along a patch's side.
    /// </summary>
    const int GridCells = 64;

    /// <summary>
    /// The context every buffer, texture and program is made on.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// The shared patch grid's vertex and index buffers.
    /// </summary>
    readonly DeviceBuffer _gridVertices, _gridIndices;

    /// <summary>
    /// The index ranges of the whole grid (0) and its four quarters (1..4): byte offsets, counts and first indices.
    /// </summary>
    readonly int[] _indexOffsets = new int[5], _indexCounts = new int[5], _firstIndices = new int[5];

    /// <summary>
    /// The grid's vertex attribute at location 0 and its index buffer, as VkGl fed them from the GL vertex array.
    /// </summary>
    readonly LegacyProgram.Attribute _gridVertex;

    /// <summary>
    /// The grid's index buffer as a binding.
    /// </summary>
    readonly GpuBufferBinding _gridElements;

    /// <summary>
    /// The frame set the terrain's programs bind as set 0.
    /// </summary>
    readonly NativeFrame _nativeFrame;

    /// <summary>
    /// The terrain constants set's layout (set 2: one uniform buffer, its offset given at bind).
    /// </summary>
    readonly GpuSetLayout _constantsLayout;

    /// <summary>
    /// The native programs the patches and meshes draw with.
    /// </summary>
    readonly TerrainPrograms _programs;

    /// <summary>
    /// The alignment of a constants slice in the frame's buffer (the device's, at least 16).
    /// </summary>
    readonly ulong _uniformAlign;

    /// <summary>
    /// The bindless stand-ins for an absent texture: 2D float, 2D array float and 2D unsigned integer.
    /// </summary>
    readonly uint _standIn2D, _standInArray, _standInUInt;

    /// <param name="coarse">Whole-world raw heights, (<paramref name="coarseSize"/>)² samples (2^n + 1 per side).</param>
    /// <param name="fine">The loaded region at its own step.</param>
    public TerrainRenderer(IGpuContext gpu, ushort[] coarse, int coarseSize, HeightWindow fine)
    {
        this._gpu = gpu;
        this._fine = fine;
        this._coarse = coarse;
        this._coarseSize = coarseSize;
        _bounds = new TerrainHeightBounds(coarse, coarseSize, fine);
        _quadtree = new TerrainQuadtree(fine.Spacing, GridCells, new WorldRenderOptions().TerrainLod);
        _spare = new TerrainQuadtree(fine.Spacing, GridCells, new WorldRenderOptions().TerrainLod);
        _fineBand = BandOf(fine);
        // The native programs (docs/renderer-native.md 7.1, step O), all made here so a draw never compiles.
        _nativeFrame = new NativeFrame(gpu);
        _uniformAlign = Math.Max(gpu.UniformBufferAlign, 16);
        _constantsLayout = GpuSetLayout.Fixed([new GpuSlot(0, GpuSlotKind.UniformBuffer, GpuStage.All, 1, OffsetAtBind: true)]);
        GpuSetLayout[] sets = [_nativeFrame.SetLayout, GpuSetLayout.Bindless, _constantsLayout];
        _programs = new TerrainPrograms(gpu, sets);
        _meshGroups = new TerrainMeshGroups(gpu);
        _materialCache = new MaterialCache(this, gpu, _nativeFrame, _constantsLayout, _uniformAlign);
        _rockBake = new TerrainRockBake(this, gpu, _nativeFrame, _constantsLayout, _uniformAlign);
        // GL's stand-ins for an absent texture, in the array each sampler indexes (the bindless stand-in is the float 2D one only).
        _standIn2D = gpu.Bindless.Register(BindlessKind.Texture2D, gpu.Dummy(StandInInfo(false, ScalarKind.Float)));
        _standInArray = gpu.Bindless.Register(BindlessKind.Texture2DArray, gpu.Dummy(StandInInfo(true, ScalarKind.Float)));
        _standInUInt = gpu.Bindless.Register(BindlessKind.UintTexture2D, gpu.Dummy(StandInInfo(false, ScalarKind.UInt)));

        using (var batch = gpu.Uploads.Begin())
        {
            _coarseTexture = HeightTexture(batch, coarse, coarseSize, coarseSize);
            _fineTexture = HeightTexture(batch, fine.Raw, fine.Columns, fine.Rows);
        }
        Activate();
        var grid = PatchGridPoints();
        var gridIndexData = PatchIndexRanges();
        BufferUse staticUse = PrivateStatics ? BufferUse.GpuOnly : 0;
        _gridVertices = DeviceBuffer.Create(gpu, (ulong)grid.Length * sizeof(float), BufferUse.Vertex | staticUse, "terrain patches vertices");
        _gridIndices = DeviceBuffer.Create(gpu, (ulong)gridIndexData.Length * sizeof(uint), BufferUse.Index | staticUse, "terrain patches indices");
        UploadPatchGrid(grid, gridIndexData);
        _gridVertex = new LegacyProgram.Attribute(new GpuBufferBinding(_gridVertices.Handle, 0, _gridVertices.Size), InternalFormat.Rg32F, 8, false);
        _gridElements = new GpuBufferBinding(_gridIndices.Handle, 0, _gridIndices.Size);
    }

    /// <summary>
    /// Frees the buffers, the height textures, the textures it owns, the programs and the stand-ins.
    /// </summary>
    public void Dispose()
    {
        _materialCache.Dispose();
        _gridVertices.Dispose();
        _gridIndices.Dispose();
        _coarseTexture.Dispose();
        _fineTexture.Dispose();
        foreach (var r in _retired) r.Texture.Dispose();
        if (_ownsTextures) _textures?.Dispose();
        _programs.Dispose();
        _rockBake.Dispose();
        _gpu.Bindless.Free(BindlessKind.Texture2D, _standIn2D);
        _gpu.Bindless.Free(BindlessKind.Texture2DArray, _standInArray);
        _gpu.Bindless.Free(BindlessKind.UintTexture2D, _standInUInt);
        _nativeFrame.Dispose();
    }

    /// <summary>
    /// The patch grid's points (location 0: two floats each), (<see cref="GridCells"/> + 1)² of them.
    /// </summary>
    static float[] PatchGridPoints()
    {
        var grid = new float[(GridCells + 1) * (GridCells + 1) * 2];
        for (int j = 0; j <= GridCells; j++)
            for (int i = 0; i <= GridCells; i++)
                (grid[(j * (GridCells + 1) + i) * 2], grid[(j * (GridCells + 1) + i) * 2 + 1]) = (i, j);
        return grid;
    }

    /// <summary>
    /// The index ranges of the whole grid (index 0) and of its four quarters, into <see cref="_indexOffsets"/>, <see cref="_firstIndices"/> and
    /// <see cref="_indexCounts"/>; returns all the indices (the patch grid as one index buffer, GL's vertex array made native).
    /// </summary>
    uint[] PatchIndexRanges()
    {
        var indices = new List<uint>();
        for (int q = -1; q < 4; q++)
        {
            var part = TerrainQuadtree.GridIndices(GridCells, q);
            _indexOffsets[q + 1] = indices.Count * sizeof(uint);
            _firstIndices[q + 1] = indices.Count;
            _indexCounts[q + 1] = part.Length;
            indices.AddRange(part);
        }
        return indices.ToArray();
    }

    /// <summary>
    /// Writes the patch grid's points and indices into their buffers (<see cref="_gridVertices"/>, <see cref="_gridIndices"/>).
    /// </summary>
    void UploadPatchGrid(float[] grid, uint[] gridIndexData)
    {
        using var batch = _gpu.Uploads.Begin();
        batch.Write(_gridVertices, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(grid.AsSpan()));
        batch.Write(_gridIndices, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(gridIndexData.AsSpan()));
    }

    /// <summary>
    /// A height texture (GL's R16, one level, linear, clamped) through <paramref name="batch"/>, with <paramref name="raw"/> written when given.
    /// </summary>
    TerrainTexture HeightTexture(IGpuUploadBatch batch, ushort[]? raw, int width, int height)
    {
        var t = batch.Create(new TextureDesc(InternalFormat.R16, width, height, Name: "terrain heights"));
        if (raw is not null)
            batch.Write(t, 0, 0, Rect(0, 0, width, height), System.Runtime.InteropServices.MemoryMarshal.AsBytes(raw.AsSpan(0, width * height)));
        return new TerrainTexture(_gpu, t, TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge);
    }

    /// <summary>
    /// A pixel rectangle from signed sizes.
    /// </summary>
    static PixelRect Rect(int x, int y, int width, int height) => new(x, y, (uint)width, (uint)height);

    /// <summary>
    /// The reflection a stand-in is made for: a 2D (or arrayed) sampler of <paramref name="kind"/>.
    /// </summary>
    static SamplerInfo StandInInfo(bool arrayed, ScalarKind kind) => new("", 0, 0, SamplerDimension.Dim2D, arrayed, false, false, kind, 0);

    #endregion

    #region Heights

    /// <summary>
    /// The whole-world coarse heights (R16).
    /// </summary>
    readonly TerrainTexture _coarseTexture;

    /// <summary>
    /// The drawn fine window's heights.
    /// </summary>
    TerrainTexture _fineTexture;

    /// <summary>
    /// The patches' height bounds for culling, widened by each window drawn.
    /// </summary>
    readonly TerrainHeightBounds _bounds;

    /// <summary>
    /// The drawn fine window.
    /// </summary>
    HeightWindow _fine;

    /// <summary>
    /// The whole-world raw heights.
    /// </summary>
    readonly ushort[] _coarse;

    /// <summary>
    /// The coarse grid's side in samples.
    /// </summary>
    readonly int _coarseSize;

    /// <summary>
    /// The fine window's fade band (<see cref="BandOf"/>).
    /// </summary>
    float _fineBand;

    /// <summary>
    /// Frames counted by <see cref="BeginFrame"/>.
    /// </summary>
    int _frameNumber;

    /// <summary>
    /// Height textures replaced, with the frame they were replaced in; freed four frames later.
    /// </summary>
    readonly List<(TerrainTexture Texture, int Frame)> _retired = [];

    /// <summary>
    /// Starts counting drawn nodes and triangles for a new frame (several views add up).
    /// </summary>
    public void BeginFrame()
    {
        (DrawnChunks, DrawnTriangles) = (0, 0);
        _frameNumber++;
        // A replaced window is let go a few frames after the swap (its image and bindless entry are freed after the frames in flight).
        for (int i = _retired.Count - 1; i >= 0; i--)
            if (_frameNumber - _retired[i].Frame >= 4)
            {
                _retired[i].Texture.Dispose();
                _retired.RemoveAt(i);
            }
    }

    /// <summary>
    /// The window the fine height texture currently holds.
    /// </summary>
    public HeightWindow Fine => _fine;

    /// <summary>
    /// Bounds of a window against the quadtree's cell grid; safe on another thread.
    /// </summary>
    public TerrainHeightBounds.Patch MeasureBounds(HeightWindow w) => _bounds.Measure(w);

    /// <summary>
    /// Replaces the fine window: a new texture is filled in slabs through <paramref name="uploads"/> while the old one stays in use, then swapped in
    /// (so no frame waits for the whole upload).
    /// </summary>
    public void BeginFineUpload(HeightWindow window, TerrainHeightBounds.Patch patch, UploadQueue uploads, Action? done = null) =>
        UploadFine(window, patch, uploads,
            texture =>
            {
                _retired.Add((_fineTexture, _frameNumber));
                Swap(new FineStandby(texture, window, patch));
            },
            done);

    /// <summary>
    /// A fine window uploaded but not drawn ("Keep textures at characters": one round a character far from the eye), ready for <see
    /// cref="SwapFine"/>.
    /// </summary>
    public sealed class FineStandby
    {
        /// <summary>
        /// A standby of <paramref name="window"/> in <paramref name="texture"/>, with the bounds still to apply.
        /// </summary>
        internal FineStandby(TerrainTexture texture, HeightWindow window, TerrainHeightBounds.Patch? patch) => (Texture, Window,
            Patch) = (texture, window, patch);

        /// <summary>
        /// The window's heights.
        /// </summary>
        internal TerrainTexture Texture { get; }

        /// <summary>
        /// The window.
        /// </summary>
        public HeightWindow Window { get; }

        /// <summary>
        /// Applied to the bounds at the first swap (null once applied: the bounds only widen, so a window once drawn stays covered).
        /// </summary>
        internal TerrainHeightBounds.Patch? Patch { get; set; }
    }

    /// <summary>
    /// Uploads a fine window as <see cref="BeginFineUpload"/> does, but hands it to <paramref name="ready"/> instead of drawing it.
    /// </summary>
    public void BeginFineStandby(HeightWindow window, TerrainHeightBounds.Patch patch, UploadQueue uploads, Action<FineStandby> ready) =>
        UploadFine(window, patch, uploads, texture => ready(new FineStandby(texture, window, patch)), null);

    /// <summary>
    /// Draws <paramref name="next"/> from now on (no upload: it is resident) and returns the window it replaces as a standby.
    /// </summary>
    public FineStandby SwapFine(FineStandby next)
    {
        var old = new FineStandby(_fineTexture, _fine, null);
        Swap(next);
        return old;
    }

    /// <summary>
    /// Lets a standby go (its texture is freed after the frames in flight).
    /// </summary>
    public void DropFine(FineStandby standby) => _retired.Add((standby.Texture, _frameNumber));

    /// <summary>
    /// Height at a world point from the same grids the shaders read (bilinear, blended at the region's edge).
    /// </summary>
    public float HeightAt(float x, float z) => Snapshot().HeightAt(x, z);

    /// <summary>
    /// The height sources as they are now, for a worker thread (render thread only: the fine window is swapped there).
    /// </summary>
    internal HeightSnapshot Snapshot() => new(_coarse, _coarseSize, _fine, _fineBand);

    /// <summary>
    /// Draws <paramref name="next"/>'s window from now on and widens the bounds by its patch (once).
    /// </summary>
    void Swap(FineStandby next)
    {
        _fineTexture = next.Texture;
        _fine = next.Window;
        _fineBand = BandOf(next.Window);
        if (next.Patch is { } patch) _bounds.Apply(patch);
        next.Patch = null;
    }

    /// <summary>
    /// Queues a window's upload: its texture, then slabs of 192 rows, then <paramref name="finish"/> and <paramref name="done"/>.
    /// </summary>
    void UploadFine(HeightWindow window, TerrainHeightBounds.Patch patch, UploadQueue uploads, Action<TerrainTexture> finish, Action? done)
    {
        const int slabRows = 192;
        TerrainTexture? texture = null;
        uploads.Add(() =>
        {
            using var batch = _gpu.Uploads.Begin();
            texture = HeightTexture(batch, null, window.Columns, window.Rows);
        }, "height texture");
        for (int row = 0; row < window.Rows; row += slabRows)
        {
            int r0 = row, n = Math.Min(slabRows, window.Rows - row);
            uploads.Add(() =>
            {
                using var batch = _gpu.Uploads.Begin();
                batch.Write(texture!.Texture, 0, 0, Rect(0, r0, window.Columns, n),
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(window.Raw.AsSpan(r0 * window.Columns, n * window.Columns)));
            }, "height slab");
        }
        uploads.Add(() =>
        {
            finish(texture!);
            done?.Invoke();
        }, "height swap");
    }

    /// <summary>
    /// Width of the band at the fine window's border where it fades into the coarse grid.
    /// </summary>
    static float BandOf(HeightWindow w) => Math.Clamp((w.Columns - 1) * w.Spacing * 0.1f, w.Spacing * 4, 3000);

    /// <summary>
    /// The coarse grid's world rectangle (the whole world).
    /// </summary>
    static Vector4 CoarseRect => new(-WorldLayout.HalfWorldSize, -WorldLayout.HalfWorldSize, WorldLayout.HalfWorldSize, WorldLayout.HalfWorldSize);

    /// <summary>
    /// The fine window's world rectangle (first to last sample).
    /// </summary>
    Vector4 FineRect
    {
        get
        {
            var (x0, z0) = _fine.WorldOf(0, 0);
            return new((float)x0, (float)z0, (float)x0 + (_fine.Columns - 1) * _fine.Spacing, (float)z0 + (_fine.Rows - 1) * _fine.Spacing);
        }
    }

    #endregion

    #region Frame globals

    /// <summary>
    /// The terrain whose heights each context's frame globals publish.
    /// </summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IGpuContext, ActiveTerrain> ActiveTerrains = new();

    /// <summary>
    /// The heights as frame globals (docs/renderer-native.md 4.3): the two textures (native, with the linear, clamped sampler their GL versions had)
    /// and the <see cref="TerrainShaders.HeightFunctions"/> uniforms, read when a consumer draws or applies its globals.
    /// </summary>
    sealed class ActiveTerrain
    {
        /// <summary>
        /// The terrain whose heights are published (null: none yet).
        /// </summary>
        public TerrainRenderer? Owner;
    }

    /// <summary>
    /// Publishes this terrain's heights as the context's frame globals, unless it already does.
    /// </summary>
    internal void Activate()
    {
        var binding = ActiveTerrains.GetValue(_gpu, static _ => new());
        if (ReferenceEquals(binding.Owner, this)) return;
        binding.Owner = this;
        PublishGlobals();
    }

    /// <summary>
    /// Publishes the height textures and uniforms, and the ground colour as the weather's dust colour.
    /// </summary>
    void PublishGlobals()
    {
        var g = _gpu.Globals;
        g.Publish("uHeightCoarse", () => _coarseTexture.Sampled());
        g.Publish("uHeightFine", () => _fineTexture.Sampled());
        g.PublishUniform("uCoarseRect", () => CoarseRect);
        g.PublishUniform("uCoarseCells", () => new Vector2(_coarseSize - 1f, _coarseSize - 1f));
        g.PublishUniform("uFineRect", () => FineRect);
        g.PublishUniform("uFineCells", () => new Vector2(_fine.Columns - 1f, _fine.Rows - 1f));
        g.PublishUniform("uFineBand", () => _fineBand);
        g.PublishUniform("uHasFine", () => 1);
        // The whole-world ground colour: the weather's dust takes its colour from it (docs/formats/weather.md "Dust").
        g.Publish("uWeatherGround", () => _textures?.Textures.Ground is { } ground ? ground.Sampled() : default);
    }

    #endregion

    #region Biome textures

    /// <summary>
    /// The biome layers, maps and whole-world colours (null: untextured).
    /// </summary>
    TerrainTextures? _textures;

    /// <summary>
    /// <see cref="Dispose"/> frees <see cref="_textures"/>.
    /// </summary>
    bool _ownsTextures = true;

    /// <summary>
    /// <see cref="_textures"/>.
    /// </summary>
    internal TerrainTextures? TextureSet => _textures;

    /// <summary>
    /// The texture field's tile at world (<paramref name="x"/>, <paramref name="z"/>) (<see cref="GroundRelief.FieldCentre"/>): its label, or null.
    /// </summary>
    public string? FieldLabelAt(float x, float z) => _textures?.FieldLabel(x, z);

    /// <summary>
    /// Uses biome textures and land maps from now on (null: untextured).
    /// </summary>
    public void SetTextures(TerrainTextures? t, bool owns = true) { _textures = t; _ownsTextures = owns; }

    /// <summary>
    /// The textures the TERRAIN-mode meshes' biome rows come from (null: none, every row -1): a caller keeping
    /// <see cref="FeatureBiomeRowAny"/> per placement computes it again when this object changes.
    /// </summary>
    public object? FeatureBiomes => _textures;

    /// <summary>
    /// The biome row <see cref="DrawMeshes"/> gives a placement at (x, z) in colour: the biome map's row there when resident, else -1.
    /// </summary>
    public int FeatureBiomeRow(float x, float z) => _textures?.FeatureBiomeRow(x, z) ?? -1;

    /// <summary>
    /// The biome map's row at (x, z) whatever its residency (-1: none); <see cref="FeatureBiomeRow"/> once <see cref="FeatureResidentBiomes"/> has
    /// its bit.
    /// </summary>
    public int FeatureBiomeRowAny(float x, float z) => _textures?.FeatureBiomeRowAny(x, z) ?? -1;

    /// <summary>
    /// A bit per resident biome row (row r: word r / 32, bit r % 32), as <see cref="FeatureBiomeRow"/> tests them now.
    /// </summary>
    public void FeatureResidentBiomes(Span<uint> bits)
    {
        if (_textures is null) bits.Clear();
        else _textures.ResidentBiomeBits(bits);
    }

    #endregion

    #region Patches

    /// <summary>
    /// The nodes the last selection chose, nearest first once sorted.
    /// </summary>
    readonly List<TerrainNode> _nodes = [];

    /// <summary>
    /// The main view's (and the shadow casters') quadtree.
    /// </summary>
    readonly TerrainQuadtree _quadtree;

    /// <summary>
    /// The reflection's own tree (its own, smaller picture), so the two passes do not reset each other's ranges every frame.
    /// </summary>
    readonly TerrainQuadtree _spare;

    /// <summary>
    /// The tree the running <see cref="Draw"/> selects with.
    /// </summary>
    TerrainQuadtree _current = null!;

    /// <summary>
    /// The view being drawn.
    /// </summary>
    Frame _frame;

    /// <summary>
    /// A deferred <see cref="Draw"/> waits for its colour (<see cref="DrawDeferred"/>).
    /// </summary>
    bool _latePending;

    /// <summary>
    /// That draw's view.
    /// </summary>
    Frame _lateFrame;

    /// <summary>
    /// The patches that overlap a building of the interior cut, nearest first.
    /// </summary>
    PatchDraw[] _cutDraws = new PatchDraw[64];

    /// <summary>
    /// The patches drawn without the cut (the first of <see cref="_patchDraws"/>), and those with it.
    /// </summary>
    int _plainCount, _cutCount;

    /// <summary>
    /// The interior mask's bindless index for the cut patches.
    /// </summary>
    uint _cutMask;

    /// <summary>
    /// The relief patches (tessellated, near the eye), without and with the cut.
    /// </summary>
    PatchDraw[] _reliefDraws = new PatchDraw[16], _cutReliefDraws = new PatchDraw[16];

    /// <summary>
    /// Their counts.
    /// </summary>
    int _reliefCount, _cutReliefCount;

    /// <summary>
    /// The nodes' sort keys (squared distance to the eye).
    /// </summary>
    double[] _nodeKeys = [];

    /// <summary>
    /// The pass state the patch states were made from, and the patch states made from it: filled, outline, prepass, late colour, late colour at
    /// equal depth.
    /// </summary>
    DrawState? _lastGlState, _lastPatchState, _lastWireState, _lastPrepassState, _lastLateState, _lastLateEqualState;

    /// <summary>
    /// The selected nodes' draws.
    /// </summary>
    PatchDraw[] _patchDraws = new PatchDraw[512];

    /// <summary>
    /// The patch pipelines by program and pass state.
    /// </summary>
    readonly List<PatchPipeline> _patchPipelines = [];

    /// <summary>
    /// Triangles of the selected nodes.
    /// </summary>
    long _patchTriangles;

    /// <summary>
    /// Pooled patch record jobs.
    /// </summary>
    readonly JobPool<PatchJob> _patchJobs = new();

    /// <summary>
    /// <see cref="DrawDepth"/> has set the frame's matrix for the meshes' depth.
    /// </summary>
    bool _depthReady;

    /// <summary>
    /// A view's matrix, eye, options and lighting.
    /// </summary>
    record struct Frame(Matrix4x4 ViewProjection, Vector3 Eye, WorldRenderOptions Options, WorldLighting Light);

    /// <summary>
    /// Levels of the quadtree.
    /// </summary>
    public int LevelCount => _quadtree.LevelCount;

    /// <summary>
    /// The finest level's grid spacing (world units).
    /// </summary>
    public double FinestSpacing => _quadtree.Spacing(0);

    /// <summary>
    /// Nodes drawn since <see cref="BeginFrame"/>.
    /// </summary>
    public int DrawnChunks { get; private set; }

    /// <summary>
    /// Triangles drawn since <see cref="BeginFrame"/>.
    /// </summary>
    public long DrawnTriangles { get; private set; }

    /// <summary>
    /// Terrain triangles the depth draws have drawn since the counter was last reset (by the caller).
    /// </summary>
    public long DepthTriangles { get; set; }

    /// <summary>
    /// The fog cull (set by the world frame around the main camera's draw only): nodes wholly hidden by the fog the eye is in are skipped.
    /// </summary>
    internal FogVolumes? FogCull { get; set; }

    /// <summary>
    /// The interior cut of the main view (docs/render-interior-cut.md; set by the world frame around its scene's terrain draws, null otherwise): the
    /// mask's depth range, read by the cut programs, which only the patches overlapping a drawn building take (a <c>discard</c> costs the early depth
    /// test).
    /// </summary>
    internal InteriorMaskRenderer? InteriorCut { get; set; }

    /// <summary>
    /// The blood on the ground the patches lay over their albedo (docs/game/blood-decals.md; null: none). The host records it before the scene.
    /// </summary>
    public TerrainDecalRenderer? Decals { get; set; }

    /// <summary>
    /// Draws the terrain; nodes whose highest point is under <paramref name="cullBelow"/> are skipped (the reflection pass clips everything below the
    /// water). <paramref name="secondary"/>: the reflection's call, with its own quadtree for its own LOD distance.
    /// </summary>
    public void Draw(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, WorldLighting light,
        float cullBelow = float.NegativeInfinity, bool secondary = false, bool defer = false)
    {
        _programs.UseColour(GroundRelief.ParallaxActive, GroundRelief.Quality);
        _current = secondary ? _spare : _quadtree;
        _current.SetRanges(options.TerrainLod);
        _frame = new Frame(viewProjection, eye, options, light);
        var fog = FogCull;
        _current.Select(eye, _bounds,
            (min, max) => max.Y >= cullBelow && WorldCamera.Intersects(frustum, min, max) &&
            (fog is null || !fog.Hidden(min, max, FogVolumes.CullKind.Terrain)), _nodes);

        SortNearestFirst(eye);
        PreparePatches(_current);
        SplitInteriorCut(secondary ? null : InteriorCut);
        SplitRelief(secondary, options);
        if (_nodes.Count == 0) return;

        if (options.Wireframe != 2) RecordFilled(deferred: defer && !secondary && options.Wireframe == 0);
        if (options.Wireframe != 0) RecordWireframe();
    }

    /// <summary>
    /// The terrain colour of a <see cref="Draw"/> that was called with <c>defer</c> (its depth is in the buffer): the same patches and material with
    /// the depth test on and no depth write, so only the fragments the objects and foliage left uncovered are shaded (docs/render-terrain.md,
    /// "Terrain colour after the occluders"). Nothing between the two calls may select other patches (the shadows and the reflection draw before the
    /// scene).
    /// </summary>
    public void DrawDeferred()
    {
        if (!_latePending) return;
        _latePending = false;
        _frame = _lateFrame;
        PrepareConstants(material: true, patches: true);
        RecordPatches(PatchColour, "terrain", PatchMode.Late, _plainCount);
        if (_reliefCount > 0)
            RecordPatches(_programs.PatchColourTess!, "terrain relief", PatchMode.Late, _reliefCount, _reliefDraws);
        // The cut patches' colour without the cut: their prepass discarded the cut fragments, so an equal depth test leaves them out early. The cut's
        // discard here turned the early depth test off, and the GPU drained between these draws (1.4 ms for 19 of them; docs/render-terrain.md
        // "Registers"). Both passes run the same vertex and tessellation stages (invariant positions): a kept fragment meets its own depth.
        if (_cutCount > 0)
            RecordPatches(PatchColour, "terrain interior cut", PatchMode.LateEqual, _cutCount, _cutDraws);
        if (_cutReliefCount > 0)
            RecordPatches(_programs.PatchColourTess!, "terrain relief interior cut", PatchMode.LateEqual, _cutReliefCount, _cutReliefDraws);
    }

    /// <summary>
    /// Draws the terrain's depth for a shadow cascade (<see cref="ShadowShaders.DepthFragment"/>, the caster bias): the main tree's patches chosen by
    /// the camera's <paramref name="eye"/> (so the casters' detail matches what the picture shows), culled by
    /// <paramref name="frustum"/>. Sets the frame's matrix for <see cref="DrawMeshes"/> with <c>depth</c>. <paramref name="coarseTexel"/>: the texel
    /// of a far cascade (world units; 0: not coarsened), which keeps the nodes from being split finer than <see cref="ShadowLod.TerrainFloorLevel"/>.
    /// </summary>
    public void DrawDepth(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, float coarseTexel = 0)
    {
        _depthReady = true;
        _quadtree.SetRanges(options.TerrainLod);
        _frame = new Frame(viewProjection, eye, options, _frame.Light);
        _quadtree.FloorLevel = ShadowLod.TerrainFloorLevel(coarseTexel, _quadtree.Spacing(0), TerrainLod.Roughness, _quadtree.LevelCount);
        try
        {
            _quadtree.Select(eye, _bounds, (min, max) => WorldCamera.Intersects(frustum, min, max), _nodes);
        }
        finally
        {
            _quadtree.FloorLevel = 0;
        }
        PreparePatches(_quadtree);
        if (_nodes.Count > 0)
        {
            PrepareConstants(material: false, patches: true);
            RecordPatches(_programs.PatchDepth, "terrain depth");
            DepthTriangles += _patchTriangles;
        }
    }

    /// <summary>
    /// The filled patches of a <see cref="Draw"/>: their depth only when <paramref name="deferred"/> (the colour follows in
    /// <see cref="DrawDeferred"/>), else their colour; counted in <see cref="DrawnChunks"/> and <see cref="DrawnTriangles"/>.
    /// </summary>
    void RecordFilled(bool deferred)
    {
        PrepareConstants(material: true, patches: true);
        if (deferred) RecordPrepass();
        else RecordSolid();

        DrawnChunks += _nodes.Count;
        DrawnTriangles += _patchTriangles;
    }

    /// <summary>
    /// Depth only now, the colour after the objects and foliage (DrawDeferred): the colour program then runs only where the terrain is what is seen.
    /// </summary>
    void RecordPrepass()
    {
        RecordPatches(_programs.PatchPrepass, "terrain prepass", PatchMode.Prepass, _plainCount);
        if (_reliefCount > 0)
            RecordPatches(_programs.PatchPrepassTess!, "terrain relief prepass", PatchMode.Prepass, _reliefCount, _reliefDraws);
        if (_cutCount > 0)
            RecordPatches(_programs.PatchPrepassCut, "terrain prepass interior cut", PatchMode.Prepass, _cutCount, _cutDraws);
        if (_cutReliefCount > 0)
            RecordPatches(_programs.PatchPrepassCutTess!, "terrain relief prepass interior cut", PatchMode.Prepass, _cutReliefCount, _cutReliefDraws);
        (_latePending, _lateFrame) = (true, _frame);
    }

    /// <summary>
    /// The patches' colour, depth written.
    /// </summary>
    void RecordSolid()
    {
        RecordPatches(PatchColour, "terrain", PatchMode.Solid, _plainCount);
        if (_reliefCount > 0)
            RecordPatches(_programs.PatchColourTess!, "terrain relief", PatchMode.Solid, _reliefCount, _reliefDraws);
        if (_cutCount > 0)
            RecordPatches(PatchColourCut, "terrain interior cut", PatchMode.Solid, _cutCount, _cutDraws);
        if (_cutReliefCount > 0)
            RecordPatches(_programs.PatchColourCutTess!, "terrain relief interior cut", PatchMode.Solid, _cutReliefCount, _cutReliefDraws);
    }

    /// <summary>
    /// The debug outline (Wireframe 1 and 2): the same patches as lines in a dark grey, pulled towards the eye as GL's polygon offset (-1, -1) did.
    /// </summary>
    void RecordWireframe()
    {
        PrepareConstants(material: true, patches: true);
        _constants.Wireframe = 1;
        RecordPatches(PatchColour, "terrain wireframe", PatchMode.Wireframe, _plainCount);
        RecordPatches(PatchColour, "terrain wireframe", PatchMode.Wireframe, _cutCount, _cutDraws);
    }

    /// <summary>
    /// Moves the patches whose square overlaps a building of <paramref name="cut"/>'s (when it drew any) out of <see cref="_patchDraws"/> into
    /// <see cref="_cutDraws"/>, both kept nearest first: <see cref="_plainCount"/> and <see cref="_cutCount"/>.
    /// </summary>
    void SplitInteriorCut(InteriorMaskRenderer? cut)
    {
        int n = _nodes.Count;
        (_plainCount, _cutCount) = (n, 0);
        if (cut is not { Active: true }) return;
        _cutMask = cut.Index;
        var boxes = cut.Boxes;
        int kept = 0;
        for (int i = 0; i < n; i++)
        {
            var d = _patchDraws[i];
            float x0 = d.Push.Node.X, z0 = d.Push.Node.Y, s = d.Push.Node.Z;
            bool overlaps = false;
            foreach (var (min, max) in boxes)
                if (x0 <= max.X && x0 + s >= min.X && z0 <= max.Y && z0 + s >= min.Y)
                {
                    overlaps = true;
                    break;
                }
            if (!overlaps)
            {
                _patchDraws[kept++] = d;
                continue;
            }
            if (_cutCount == _cutDraws.Length) Array.Resize(ref _cutDraws, _cutDraws.Length * 2);
            _cutDraws[_cutCount++] = d;
        }
        _plainCount = kept;
    }

    /// <summary>
    /// Moves the plain and the cut patches whose square comes within <see cref="GroundRelief.TessellatedRadius"/> of the eye (horizontally) into
    /// <see cref="_reliefDraws"/> and <see cref="_cutReliefDraws"/>, drawn by the tessellated programs: the main view only, with the displacement on.
    /// The displacement and the split are nil beyond its end, so these patches meet the plain ones on unraised, unsplit edges.
    /// </summary>
    void SplitRelief(bool secondary, WorldRenderOptions options)
    {
        (_reliefCount, _cutReliefCount) = (0, 0);
        if (secondary || _programs.PatchColourTess is null || !GroundRelief.DisplaceActive && GroundRelief.RefinePixels <= 0 || options.Wireframe != 0
            || !options.Textures || _textures is not { HasBiomes: true })
            return;
        float r2 = GroundRelief.TessellatedRadius >= float.MaxValue ? float.MaxValue
            : GroundRelief.TessellatedRadius * GroundRelief.TessellatedRadius;
        _plainCount = SplitNear(_patchDraws, _plainCount, ref _reliefDraws, ref _reliefCount, _frame.Eye, r2);
        _cutCount = SplitNear(_cutDraws, _cutCount, ref _cutReliefDraws, ref _cutReliefCount, _frame.Eye, r2);
    }

    /// <summary>
    /// Moves the draws of <paramref name="draws"/>[..<paramref name="count"/>] whose square lies within sqrt(<paramref name="r2"/>) of the eye
    /// (horizontally) to <paramref name="near"/>, both kept in order; returns how many stay.
    /// </summary>
    static int SplitNear(PatchDraw[] draws, int count, ref PatchDraw[] near, ref int nearCount, Vector3 eye, float r2)
    {
        int kept = 0;
        for (int i = 0; i < count; i++)
        {
            var d = draws[i];
            float x0 = d.Push.Node.X, z0 = d.Push.Node.Y, s = d.Push.Node.Z;
            float dx = MathF.Max(MathF.Max(x0 - eye.X, eye.X - (x0 + s)), 0), dz = MathF.Max(MathF.Max(z0 - eye.Z, eye.Z - (z0 + s)), 0);
            if (dx * dx + dz * dz > r2)
            {
                draws[kept++] = d;
                continue;
            }
            if (nearCount == near.Length) Array.Resize(ref near, near.Length * 2);
            near[nearCount++] = d;
        }
        return kept;
    }

    /// <summary>
    /// Orders <see cref="_nodes"/> nearest first (by the horizontal distance from the eye to the patch's square), so nearer hills fill the depth
    /// buffer before the land behind them is shaded: the material's pixels behind a hill fail the early depth test. Patches do not overlap, so the
    /// picture does not depend on the order (docs/formats/terrain.md, "In the viewer").
    /// </summary>
    void SortNearestFirst(Vector3 eye)
    {
        int n = _nodes.Count;
        if (_nodeKeys.Length < n) _nodeKeys = new double[Math.Max(n, _nodeKeys.Length * 2)];
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_nodes);
        for (int i = 0; i < n; i++)
        {
            var node = span[i];
            // A quadrant node draws one quarter of its square (TerrainQuadtree.Select).
            double s = node.Quadrant < 0 ? node.Size : node.Size / 2;
            double x0 = node.X0 + (node.Quadrant < 0 ? 0 : (node.Quadrant & 1) * s), z0 = node.Z0 +
                (node.Quadrant < 0 ? 0 : (node.Quadrant >> 1) * s);
            double dx = Math.Max(Math.Max(x0 - eye.X, eye.X - (x0 + s)), 0), dz = Math.Max(Math.Max(z0 - eye.Z, eye.Z - (z0 + s)), 0);
            _nodeKeys[i] = dx * dx + dz * dz;
        }
        _nodeKeys.AsSpan(0, n).Sort(span);
    }

    /// <summary>
    /// How <see cref="RecordPatches"/> draws: filled (colour or depth), or the debug outline.
    /// </summary>
    enum PatchMode
    {
        /// <summary>
        /// Filled, with colour (or depth for the casters).
        /// </summary>
        Solid,

        /// <summary>
        /// The debug outline: lines, biased toward the viewer.
        /// </summary>
        Wireframe,

        /// <summary>
        /// Depth only, before the occluders (the deferred colour's prepass).
        /// </summary>
        Prepass,

        /// <summary>
        /// The colour after the occluders: depth tested, not written.
        /// </summary>
        Late,

        /// <summary>
        /// The late colour where the prepass's own depth stayed (equal depth): the cut patches without the cut.
        /// </summary>
        LateEqual
    }

    /// <summary>
    /// The state the patches draw with: the pass's (its targets' depth test, write and compare, blending, colour mask, depth clamp, bias, as GL has
    /// them for the host) with the terrain's own culling: back faces, counter-clockwise front (what the GL version set before every draw). The
    /// outline draws lines with a depth bias of (-1, -1). Kept per input state (records compare by value: no allocation in the steady state).
    /// </summary>
    DrawState PatchState(DrawState pass, PatchMode mode)
    {
        if (_lastGlState is null || pass != _lastGlState)
        {
            _lastGlState = pass;
            _lastPatchState = pass with { Cull = CullMode.Back, Front = FrontFaceDirection.Ccw };
            var (biasConstant, biasSlope) = DepthConvention.TowardViewer(1);   // GL's PolygonOffset(-1, -1), in the scene's convention
            _lastWireState = _lastPatchState with
            {
                Polygon = PolygonKind.Line, BiasEnable = true, BiasConstant = biasConstant, BiasSlope = biasSlope,
            };
            _lastPrepassState = _lastPatchState with { ColourMask = 0 };   // depth only
            _lastLateState = _lastPatchState with { DepthWrite = false };   // the prepass wrote it; equal depths pass (less-or-equal)
            _lastLateEqualState = _lastLateState with { Compare = DepthFunction.Equal };   // only where the prepass's own depth stayed
        }
        return mode switch
        {
            PatchMode.Wireframe => _lastWireState!,
            PatchMode.Prepass => _lastPrepassState!,
            PatchMode.Late => _lastLateState!,
            PatchMode.LateEqual => _lastLateEqualState!,
            _ => _lastPatchState!,
        };
    }

    /// <summary>
    /// One selected node as its draw needs it (Prepare fills the list, Record walks it): the push constants (<c>uNode</c>,
    /// <c>uMorph</c>) and the index range (<c>Quadrant + 1</c>).
    /// </summary>
    struct PatchDraw
    {
        /// <summary>
        /// The node's push constants.
        /// </summary>
        public TerrainPush Push;

        /// <summary>
        /// The index range: 0 the whole grid, 1..4 a quarter.
        /// </summary>
        public int Part;
    }

    /// <summary>
    /// A patch program resolved for one pass state: the pipeline, the grid's vertex buffer and its index buffer.
    /// </summary>
    sealed class PatchPipeline
    {
        /// <summary>
        /// The segment state the pipeline was made for.
        /// </summary>
        public SegmentPipeline Segment;

        /// <summary>
        /// The pipeline.
        /// </summary>
        public GpuPipeline Pipeline = default;

        /// <summary>
        /// The grid's vertex buffer.
        /// </summary>
        public GpuBufferBinding Vertex;

        /// <summary>
        /// The grid's index buffer.
        /// </summary>
        public GpuBufferBinding Elements;
    }

    /// <summary>
    /// Fills <see cref="_patchDraws"/> from <see cref="_nodes"/> (the tree's morph ranges, the index range per node) and counts them.
    /// </summary>
    void PreparePatches(TerrainQuadtree tree)
    {
        int n = _nodes.Count;
        if (_patchDraws.Length < n) _patchDraws = new PatchDraw[Math.Max(n, _patchDraws.Length * 2)];
        long triangles = 0;
        for (int i = 0; i < n; i++)
        {
            var node = _nodes[i];
            float start = tree.MorphStart[node.Level], end = tree.MorphEnd[node.Level];
            int part = node.Quadrant + 1;
            _patchDraws[i] = new PatchDraw
            {
                Push = new TerrainPush
                {
                    Node = new Vector4((float)node.X0, (float)node.Z0, (float)node.Size, GridCells),
                    Morph = new Vector2(start == float.MaxValue ? 1e31f : start, end == float.MaxValue ? 2e31f : end),
                },
                Part = part,
            };
            triangles += _indexCounts[part] / 3;
        }
        _patchTriangles = triangles;
    }

    /// <summary>
    /// Records <see cref="_patchDraws"/> into one native segment of the pass being drawn into (<see cref="PatchState"/>: back faces culled): the
    /// dynamic state, the sets, the pipeline, the grid's vertex and index buffers once; per node its push constants and the draw of its index range.
    /// </summary>
    void RecordPatches(TerrainProgram p, string label, PatchMode mode = PatchMode.Solid, int count = -1, PatchDraw[]? draws = null)
    {
        if (count < 0) count = _nodes.Count;
        if (count == 0) return;
        draws ??= _patchDraws;
        // Prepare (wave 4, docs/renderer-native.md 6.2): the pass's targets and state now, the pipeline, the sets, the draws, into a job.
        var targets = _gpu.CurrentTargets();
        var state = PatchState(_gpu.CurrentState(), mode);
        if (p.P.Tessellated) state = state with { Cull = CullMode.None };   // the raised triangles' winding is not checked: draw both sides
        var r = ResolvePatches(p, state, targets, label);
        var job = _patchJobs.Rent();
        job.Owner = this;
        (job.Targets, job.State, job.Program, job.Pipeline, job.Vertex, job.Elements) = (targets, state, p.P, r.Pipeline, r.Vertex, r.Elements);
        job.Stages = GpuStage.Graphics;
        var view = View();
        job.Frame = _nativeFrame.Prepare(in view);
        (job.ConstantSet, job.ConstantOffset) = PrepareConstantsBinding();
        if (job.Draws.Length < count) job.Draws = new PatchDraw[Math.Max(count, job.Draws.Length * 2)];
        draws.AsSpan(0, count).CopyTo(job.Draws);
        job.Count = count;
        _gpu.Record(label, job);
    }

    /// <summary>
    /// The patches of one view as prepared (<see cref="RecordPatches"/>): recorded on any thread, the same commands as before wave 4.
    /// </summary>
    sealed class PatchJob : RecordJob
    {
        /// <summary>
        /// The renderer whose index ranges the draws use.
        /// </summary>
        public TerrainRenderer Owner = null!;

        /// <summary>
        /// The pass's targets.
        /// </summary>
        public PassTargets Targets = null!;

        /// <summary>
        /// The patch state.
        /// </summary>
        public DrawState State = null!;

        /// <summary>
        /// The program.
        /// </summary>
        public GpuProgram Program = default;

        /// <summary>
        /// Its pipeline.
        /// </summary>
        public GpuPipeline Pipeline = default;

        /// <summary>
        /// The grid's vertex and index buffers.
        /// </summary>
        public GpuBufferBinding Vertex, Elements;

        /// <summary>
        /// The view's frame binding.
        /// </summary>
        public FrameBinding Frame;

        /// <summary>
        /// The terrain constants set.
        /// </summary>
        public GpuSet ConstantSet;

        /// <summary>
        /// Its offset in the frame's buffer.
        /// </summary>
        public uint ConstantOffset;

        /// <summary>
        /// The program's push range: a push names every stage of it.
        /// </summary>
        public GpuStage Stages;

        /// <summary>
        /// The draws (grown as needed).
        /// </summary>
        public PatchDraw[] Draws = new PatchDraw[256];

        /// <summary>
        /// The draws in <see cref="Draws"/>.
        /// </summary>
        public int Count;
        /// <inheritdoc/>
        public override int Size => Count;

        public override void Record(IGpuCommandList cmd)
        {
            State.Record(cmd, Targets);
            NativeFrame.Record(cmd, Program, in Frame);
            uint offset = ConstantOffset;
            cmd.BindSets(Program, TerrainShaders.ConstantsSet, new ReadOnlySpan<GpuSet>(in ConstantSet), new ReadOnlySpan<uint>(in offset));
            cmd.BindPipeline(Pipeline);
            cmd.BindVertexBuffers(0, new ReadOnlySpan<GpuBufferBinding>(in Vertex));
            cmd.BindIndexBuffer(Elements, IndexKind.U32);
            var (counts, firsts) = (Owner._indexCounts, Owner._firstIndices);
            for (int i = 0; i < Count; i++)
            {
                ref readonly var d = ref Draws[i];
                cmd.PushConstants(Program, Stages, in d.Push);
                cmd.DrawIndexed((uint)counts[d.Part], 1, (uint)firsts[d.Part]);
            }
        }

        public override void Release() => Owner._patchJobs.Return(this);
    }

    /// <summary>
    /// The pipeline and bindings for <paramref name="p"/> in this pass state, kept while the state stays the same (the reflection's multisampled
    /// target alternates with the scene's: a few entries). The grid is the terrain's own vertex and index buffer.
    /// </summary>
    PatchPipeline ResolvePatches(TerrainProgram p, DrawState state, PassTargets targets, string label)
    {
        var segment = new SegmentPipeline(p.P, targets.Formats, state.Blend, state.ColourMask, state.Polygon, state.AlphaToCoverage,
            state.DepthClamp);
        foreach (var e in _patchPipelines)
            if (e.Segment == segment) return e;
        if (_patchPipelines.Count >= 16) _patchPipelines.Clear();
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        attributes[0] = _gridVertex;
        Span<GpuBufferBinding> vertex = stackalloc GpuBufferBinding[1];
        p.Buffers(attributes, vertex);
        var entry = new PatchPipeline
        {
            Segment = segment,
            Pipeline = _gpu.Pipelines.Get(state.Pipeline(p.P, p.VertexLayout(attributes),
                p.P.Tessellated ? PrimitiveType.PatchList : PrimitiveType.Triangles, targets.Formats, label)),
            Vertex = vertex[0],
            Elements = _gridElements,
        };
        _patchPipelines.Add(entry);
        return entry;
    }

    #endregion

    #region Material and constants

    /// <summary>
    /// The terrain set (set 2): this segment's <see cref="TerrainConstants"/> in the frame's constants, bound with a dynamic offset into a set made
    /// once per constants chunk (<see cref="LegacyProgram"/>'s pattern for its default blocks). The constants being prepared, and the last
    /// ones written.
    /// </summary>
    TerrainConstants _constants, _written;

    /// <summary>
    /// The frame the last constants were written in (-1: none); equal constants within a frame reuse their slice.
    /// </summary>
    long _writtenFrame = -1;

    /// <summary>
    /// The slice the last constants went into.
    /// </summary>
    Transient _constantsSlice;

    /// <summary>
    /// The terrain set per constants chunk, by the chunk's buffer index.
    /// </summary>
    readonly Dictionary<ulong, GpuSet> _constantSets = [];

    /// <summary>
    /// The chunk of <see cref="_lastSet"/>.
    /// </summary>
    ulong _lastSetKey;

    /// <summary>
    /// The set of the last chunk bound.
    /// </summary>
    GpuSet _lastSet;

    /// <summary>
    /// The last <c>uRegion</c> the textures gave: without textures it keeps this value, as a GL program's uniform did.
    /// </summary>
    Vector4 _lastRegion;

    /// <summary>
    /// The last <c>uCellGrid</c>, kept the same way.
    /// </summary>
    Vector2 _lastCellGrid;

    /// <summary>
    /// The layered material baked round the eye (docs/render-terrain.md "Material cache").
    /// </summary>
    readonly MaterialCache _materialCache;

    /// <summary>
    /// Bakes what the material cache's levels need this frame round <paramref name="eye"/> (before the scene, outside a pass;
    /// <see cref="MaterialCache.Record"/>).
    /// </summary>
    public void RecordMaterialCache(Vector3 eye) => _materialCache.Record(eye);

    /// <summary>
    /// The terrain set of the constants chunk <paramref name="buffer"/>, made the first time (the bakes' own constants).
    /// </summary>
    internal GpuSet ConstantSetOf(GpuBuffer buffer)
    {
        ulong key = (ulong)buffer.Index;
        if (!_constantSets.TryGetValue(key, out var set)) _constantSets[key] = set = ConstantSet(buffer);
        return set;
    }

    /// <summary>
    /// <see cref="_constants"/> prepared for the patches' material (<see cref="PrepareConstants"/>), for the material cache's bake.
    /// </summary>
    internal TerrainConstants MaterialConstants()
    {
        PrepareConstants(material: true, patches: true);
        return _constants;
    }

    /// <summary>
    /// The options of the frame being drawn.
    /// </summary>
    internal WorldRenderOptions FrameOptions => _frame.Options;

    // The native model (docs/renderer-native.md 7.1, step O). Native programs (TerrainShaders' native variants: the same maths; the camera in
    // ViewConstants, the terrain's values and the bindless indices of its textures in TerrainConstants at set 2, a patch's node in push
    // constants), recorded into the pass VkGl has open. Per
    // segment: the frame set (NativeFrame), the terrain set, the dynamic state; per draw only commands (a patch: a push and a draw; a mesh
    // group: its pipeline, buffers and draw, kept per mesh while the vertex-array stamp holds).

    /// <summary>
    /// Whether the terrain colour programs may sample their normal-map arrays this frame: the option is on and the biome layers are resident. The
    /// no-normal programs are compile-time variants, so this must select the matching program.
    /// </summary>
    bool NormalMapsOn => _frame.Options.Textures && _frame.Options.NormalMaps && _textures is { HasBiomes: true };

    /// <summary>
    /// The patches' colour program: with normal maps when <see cref="NormalMapsOn"/>.
    /// </summary>
    TerrainProgram PatchColour => NormalMapsOn ? _programs.PatchColour : _programs.PatchColourNoNormal;

    /// <summary>
    /// The cut patches' colour program.
    /// </summary>
    TerrainProgram PatchColourCut => NormalMapsOn ? _programs.PatchColourCut : _programs.PatchColourCutNoNormal;

    /// <summary>
    /// The TERRAIN-mode meshes' colour program (with the cross-fade's discard and the interior cut).
    /// </summary>
    TerrainProgram MeshColour => NormalMapsOn ? _programs.MeshColour : _programs.MeshColourNoNormal;

    /// <summary>
    /// The TERRAIN-mode meshes' colour program for placements not mid-fade.
    /// </summary>
    TerrainProgram MeshSolid => NormalMapsOn ? _programs.MeshSolid : _programs.MeshSolidNoNormal;

    /// <summary>
    /// A mesh program, or its tessellated twin while the rocks are smoothed (<see cref="RockSmoothing.TessellateActive"/>).
    /// </summary>
    TerrainProgram Smoothed(TerrainProgram p, TerrainProgram? tessellated) => RockSmoothing.TessellateActive && tessellated is not null
        ? tessellated : p;

    /// <summary>
    /// The bindless index of a native texture of the terrain's (<see cref="TerrainTexture.Bindless"/>: a new index when its sampler changed), or the
    /// stand-in of <paramref name="kind"/>'s array when there is none.
    /// </summary>
    static uint Index(TerrainTexture? texture, BindlessKind kind, uint standIn)
    {
        if (texture is null) return standIn;
        var h = texture.Bindless();
        return h.Kind == kind ? h.Index : standIn;
    }

    /// <summary>
    /// <see cref="_constants"/> for the next segment: the height functions' values and the height textures; with <paramref name="material"/>
    /// also the material's (<paramref name="patches"/>: the height field's normals, no feature; else <c>uFeature</c> 1) and the material textures. A
    /// value the GL version set only with textures (<c>uRegion</c>, <c>uCellGrid</c>) keeps its last one without, as a GL program's uniform did. In
    /// Prepare, on the render thread (bindless registrations).
    /// </summary>
    void PrepareConstants(bool material, bool patches)
    {
        var c = HeightConstants();
        if (material) ApplyMaterial(ref c, patches);
        _constants = c;
    }

    /// <summary>
    /// The height functions' values and the height textures, every other texture the stand-in of its array (<see cref="PrepareConstants"/>).
    /// </summary>
    TerrainConstants HeightConstants()
    {
        return new TerrainConstants
        {
            CoarseRect = CoarseRect,
            CoarseCells = new Vector2(_coarseSize - 1f, _coarseSize - 1f),
            FineRect = FineRect,
            FineCells = new Vector2(_fine.Columns - 1f, _fine.Rows - 1f),
            FineBand = _fineBand,
            HasFine = 1,
            HeightCoarse = Index(_coarseTexture, BindlessKind.Texture2D, _standIn2D),
            HeightFine = Index(_fineTexture, BindlessKind.Texture2D, _standIn2D),
            Diffuse = _standInArray,
            Normal = _standInArray,
            Height = _standInArray,
            Relief = _standIn2D,
            Blood = _standInArray,
            Params = _standIn2D,
            Cells = _standInUInt,
            BlendMap = _standIn2D,
            Overlay = _standIn2D,
            Colour = _standIn2D,
            Ground = _standIn2D,
            WorldColour = _standIn2D,
            Region = _lastRegion,
            CellGrid = _lastCellGrid,
            TessDetail = GroundRelief.TessDetail,
            RockRound = RockSmoothing.Roundness,   // the tessellation stages, the casters' too
            ReliefQuality = GroundRelief.Quality,
            FieldTiles = _standInUInt,
        };
    }

    /// <summary>
    /// The material's values into <paramref name="c"/> (<paramref name="patches"/>: the height field's normals, no feature; else <c>uFeature</c> 1),
    /// and with textures the material textures (<see cref="ApplyTextureSet"/>).
    /// </summary>
    void ApplyMaterial(ref TerrainConstants c, bool patches)
    {
        var (options, light) = (_frame.Options, _frame.Light);
        c.HeightNormals = patches ? 1u : 0u;
        c.Feature = patches ? 0u : 1u;
        c.FeatureBiome = -1;
        c.SunColour = light.SunColour;
        c.AmbientSky = light.AmbientSky;
        c.AmbientGround = light.AmbientGround;
        c.WaterHeight = options.Water ? WorldWater.Height : -1e6f;
        c.HalfWorld = (float)WorldLayout.HalfWorldSize;
        c.Debug = options.Debug;
        c.Grid = options.Grid ? 1u : 0u;
        if (patches && TerrainLayerSkip.Enabled) c.CliffEps = TerrainLayerSkip.CliffEps;
        // The interior cut: the patches' cut variants, or a mesh draw given one (DrawMeshes, DrawMeshesIndirect).
        bool cut = patches ? _cutCount + _cutReliefCount > 0 : _meshCut is { Active: true };
        c.InteriorMask = !cut ? _standIn2D : patches ? _cutMask : _meshCut!.Index;
        c.InteriorCut = cut ? 1u : 0u;
        if (patches) Decals?.Apply(ref c);
        if (patches) _materialCache.Apply(ref c);
        c.FarStart = options.MaterialDistance * 0.8f;
        c.FarEnd = options.MaterialDistance;
        var t = _textures;
        bool textured = options.Textures && t is { HasBiomes: true };
        c.Textured = textured ? 1u : 0u;
        c.NormalMaps = textured && options.NormalMaps ? 1u : 0u;
        c.HasMaps = options.Textures && t is { MapState: 2 } ? 1u : 0u;
        c.MapState = t?.MapState ?? 0;
        c.HasGround = t is { HasGround: true } ? 1u : 0u;
        c.HasWorldColour = t is { HasWorldColour: true } ? 1u : 0u;
        if (t is not null) ApplyTextureSet(ref c, t, textured, patches, options);
    }

    /// <summary>
    /// The material textures of <paramref name="t"/> and the relief's values into <paramref name="c"/> (<see cref="ApplyMaterial"/>).
    /// </summary>
    void ApplyTextureSet(ref TerrainConstants c, TerrainTextures t, bool textured, bool patches, WorldRenderOptions options)
    {
        c.Region = _lastRegion = t.Region;
        c.CellGrid = _lastCellGrid = new Vector2(t.CellsX, t.CellsZ);
        var set = t.Textures;
        c.Diffuse = Index(set.Diffuse, BindlessKind.Texture2DArray, _standInArray);
        c.Normal = Index(set.Normal, BindlessKind.Texture2DArray, _standInArray);
        c.DiffuseFar = Index(set.DiffuseFar, BindlessKind.Texture2DArray, _standInArray);
        c.NormalFar = Index(set.NormalFar, BindlessKind.Texture2DArray, _standInArray);
        c.FarLod = set.FarLod;
        c.Height = Index(set.Height, BindlessKind.Texture2DArray, _standInArray);
        c.Relief = Index(set.Relief, BindlessKind.Texture2D, _standIn2D);
        c.Parallax = textured && GroundRelief.ParallaxActive && set.Height is not null ? GroundRelief.Depth : 0;
        c.Displace = patches && textured && GroundRelief.DisplaceActive && _programs.PatchColourTess is not null && set.Height is not null
            ? GroundRelief.Depth : 0;
        (c.DisplaceEnd, c.ParallaxEnd) = (GroundRelief.DisplaceDistance, GroundRelief.ParallaxDistance);
        (c.OverlayRelief, c.SelfShadow, c.TargetSlope) = (GroundRelief.OverlayRelief, GroundRelief.SelfShadow, GroundRelief.TargetSlope);
        (c.MaxDepth, c.ScaleFollow,
            c.CliffScale) = (GroundRelief.MaxDepth / GroundRelief.ReferenceDepth, GroundRelief.ScaleFollow, GroundRelief.CliffScale);
        (c.Refine, c.PixelScale, c.BroadCut) = (patches && _programs.PatchColourTess is not null ? GroundRelief.RefinePixels : 0,
            options.TerrainPixelScale, GroundRelief.BroadCut);
        c.FieldTiles = Index(set.Field, BindlessKind.UintTexture2D, _standInUInt);
        if (patches && textured && t.FieldCount > 0)
            (c.FieldOrigin, c.FieldTile, c.FieldColumns, c.FieldCount) = (t.FieldOrigin, t.FieldTile, t.FieldColumns, t.FieldCount);
        c.Params = Index(set.Params, BindlessKind.Texture2D, _standIn2D);
        c.Cells = Index(set.Cells, BindlessKind.UintTexture2D, _standInUInt);
        c.BlendMap = Index(set.BlendMap, BindlessKind.Texture2D, _standIn2D);
        c.Overlay = Index(set.Overlay, BindlessKind.Texture2D, _standIn2D);
        c.Colour = Index(set.Colour, BindlessKind.Texture2D, _standIn2D);
        c.Ground = Index(set.Ground, BindlessKind.Texture2D, _standIn2D);
        c.WorldColour = Index(set.WorldColour, BindlessKind.Texture2D, _standIn2D);
    }

    /// <summary>
    /// The camera of the frame (<see cref="Draw"/>'s or <see cref="DrawDepth"/>'s) as <see cref="ViewConstants"/>.
    /// </summary>
    ViewConstants View()
    {
        var light = _frame.Light;
        return new ViewConstants
        {
            ViewProjection = _frame.ViewProjection,
            Eye = _frame.Eye,
            LightDir = light.SunDirection,
            FogColour = light.FogColour,
            FogDistance = light.FogDistance,
        };
    }

    /// <summary>
    /// Once per segment, in Prepare (wave 4): <see cref="_constants"/> into the frame's constants (again only when it changed within the frame), and
    /// the set and dynamic offset the segment's job binds as set 2.
    /// </summary>
    (GpuSet Set, uint Offset) PrepareConstantsBinding()
    {
        var f = _gpu.Frame;
        if (_writtenFrame != f.Number || !System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<TerrainConstants>(in _constants))
                .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<TerrainConstants>(in _written))))
        {
            var constants = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<TerrainConstants>(in _constants));
            _constantsSlice = f.WriteConstants(constants, _uniformAlign);
            (_written, _writtenFrame) = (_constants, f.Number);
            f.Stats.ConstantBytes += sizeof(TerrainConstants);
        }
        ulong key = (ulong)_constantsSlice.Handle.Index;
        if (key != _lastSetKey || _lastSet.IsNull)
        {
            if (!_constantSets.TryGetValue(key, out var made))
                _constantSets[key] = made = ConstantSet(_constantsSlice.Handle);
            (_lastSetKey, _lastSet) = (key, made);
        }
        return (_lastSet, (uint)_constantsSlice.Offset);
    }

    /// <summary>
    /// A terrain set over <paramref name="buffer"/> (one <see cref="TerrainConstants"/>, its offset given at bind).
    /// </summary>
    GpuSet ConstantSet(GpuBuffer buffer) => _gpu.AllocateConstantSet(_constantsLayout, buffer, (ulong)sizeof(TerrainConstants));

    #endregion

    #region Meshes

    /// <summary>
    /// The mesh program kinds: colour, depth (the casters) and solid (colour without the cross-fade's discard).
    /// </summary>
    internal const int Colour = 0, Depth = 1, Solid = 2;

    /// <summary>
    /// The interior cut of the mesh draw being prepared (<see cref="DrawMeshes"/>, <see cref="DrawMeshesIndirect"/>; docs/render-interior-cut.md),
    /// null otherwise.
    /// </summary>
    InteriorMaskRenderer? _meshCut;

    /// <summary>
    /// Per program kind: the last segment state and its number; numbers are stable per state (a mesh compares one int per draw). The numbers by kind
    /// and state.
    /// </summary>
    readonly Dictionary<(int Kind, SegmentPipeline State), int> _segmentByState = [];

    /// <summary>
    /// The last segment state per kind.
    /// </summary>
    readonly SegmentPipeline[] _lastSegment = new SegmentPipeline[3];

    /// <summary>
    /// Its number per kind (0: none yet).
    /// </summary>
    readonly int[] _segmentIds = new int[3];

    /// <summary>
    /// Segment numbers handed out.
    /// </summary>
    int _segmentCount;

    /// <summary>
    /// The TERRAIN-mode meshes' instanced groups and this call's placements.
    /// </summary>
    readonly TerrainMeshGroups _meshGroups;

    /// <summary>
    /// Pooled mesh record jobs.
    /// </summary>
    readonly JobPool<MeshJob> _meshJobs = new();

    /// <summary>
    /// A TERRAIN-mode mesh drawn indirect (<see cref="DrawMeshesIndirect"/>): the vertex array (position at 0, normal at 1), its index count, and
    /// whether its placements mirror (the winding turns round), as <see cref="DrawMeshes"/> groups them.
    /// </summary>
    public readonly record struct IndirectMesh(MeshBindings Mesh, int IndexCount, bool Mirrored);

    /// <summary>
    /// Draws other meshes with the terrain material (TERRAIN-mode map features), after <see cref="Draw"/> set the frame. Each item: a mesh with
    /// position at location 0 and normal at 1, its index count, its transform. As the game's <c>Feature_Terrain_DX11</c> (docs/formats/foliage.md,
    /// "TERRAIN-mode meshes"): one biome per mesh, the one of <c>biomemap.png</c> at the mesh's origin, back faces culled. Drawn instanced (<see
    /// cref="TerrainMeshGroups.Group"/>); returns the number of draw calls.
    /// </summary>
    public int DrawMeshes(List<(MeshBindings Mesh, int IndexCount, Matrix4x4 Model)> meshes, bool depth = false, InteriorMaskRenderer? cut = null)
    {
        if (depth) return DrawMeshesDepth(meshes);
        if (!_meshGroups.Group(meshes, biomes: true, _textures)) return 0;
        if (cut is { Active: true })
        {
            // Placements over an interior mask (the caller splits them out): the program with the cut, on for this draw.
            _meshCut = cut;
            try
            {
                PrepareConstants(material: true, patches: false);
            }
            finally
            {
                _meshCut = null;
            }
            return DrawGroups(Smoothed(MeshColour, _programs.MeshColourTess), Colour, "terrain meshes interior cut");
        }
        PrepareConstants(material: true, patches: false);
        // No placement of this path fades (row 1's w is 0): the program without the discard, so the depth test runs first (EarlyDepth).
        return EarlyDepth.Enabled ? DrawGroups(Smoothed(MeshSolid, _programs.MeshSolidTess), Solid, "terrain meshes solid")
            : DrawGroups(Smoothed(MeshColour, _programs.MeshColourTess), Colour, "terrain meshes");
    }

    /// <summary>
    /// <see cref="DrawMeshes"/> with placements and draw arguments made on the GPU (the foliage's GPU cull, docs/renderer-native.md 5.6.1): mesh
    /// <c>i</c> is drawn by the <c>VkDrawIndexedIndirectCommand</c> at <paramref name="argsOffset"/> + 20 i, its instances read from
    /// <paramref name="rows"/> (64 bytes each, the four rows of the matrix, row 0's w the biome row in colour, as <see cref="DrawMeshes"/>
    /// writes them) from <paramref name="rowsOffset"/>, with the argument's firstInstance. Same programs, state, and GL state left behind as
    /// <see cref="DrawMeshes"/>; the caller orders the meshes. Returns the number of draw calls.
    /// </summary>
    public int DrawMeshesIndirect(ReadOnlySpan<IndirectMesh> meshes, GpuBuffer rows, ulong rowsOffset, GpuBuffer args, ulong argsOffset,
        bool depth = false, bool solid = false,
        InteriorMaskRenderer? cut = null)
    {
        if (meshes.Length == 0) return 0;
        if (depth)
        {
            if (!_depthReady) return 0;   // the frame's matrix is the cascade's once DrawDepth has run
            PrepareConstants(material: false, patches: false);
            var program = Smoothed(_programs.MeshDepth, _programs.MeshDepthTess);
            return DrawIndirect(meshes, rows, rowsOffset, args, argsOffset, program, Depth, "terrain mesh depth");
        }
        bool solidDraw = solid && EarlyDepth.Enabled;
        // cut: the interior cut on in the program with the discard (the caller's cull lays the placements over a mask out with the fading ones).
        _meshCut = solidDraw ? null : cut;
        try
        {
            PrepareConstants(material: true, patches: false);
        }
        finally
        {
            _meshCut = null;
        }
        // solid: none of these placements is mid cross-fade (the caller splits them), so the program without the discard draws them (EarlyDepth).
        return solidDraw
            ? DrawIndirect(meshes, rows, rowsOffset, args, argsOffset, Smoothed(MeshSolid, _programs.MeshSolidTess), Solid, "terrain meshes solid")
            : DrawIndirect(meshes, rows, rowsOffset, args, argsOffset, Smoothed(MeshColour, _programs.MeshColourTess), Colour, "terrain meshes");
    }

    /// <summary>
    /// Draws the TERRAIN-mode meshes' depth for the cascade <see cref="DrawDepth"/> set up, grouped by mesh; returns the draws (0 before it ran).
    /// </summary>
    int DrawMeshesDepth(List<(MeshBindings Mesh, int IndexCount, Matrix4x4 Model)> meshes)
    {
        // The frame's matrix is the cascade's once DrawDepth has run.
        if (!_depthReady || !_meshGroups.Group(meshes, biomes: false, _textures)) return 0;
        PrepareConstants(material: false, patches: false);
        return DrawGroups(Smoothed(_programs.MeshDepth, _programs.MeshDepthTess), Depth, "terrain mesh depth");
    }

    // One instanced draw per (vertex array, index count, winding) instead of one draw per placement (a draw costs ~4 µs of CPU
    // through VkGl, and a forest has hundreds of TERRAIN-mode rocks on screen and thousands in a shadow cascade). The placements keep
    // their order within a group; the groups follow the first placement of each. The pictures are the same as with one draw each
    // (docs/render-shadows.md, "Shadow pass cost"): the shaders do the uniform form's arithmetic, a depth-only pass keeps the nearest fragment
    // whatever the order, and in colour only exactly equal depths of two different placements could tell the order apart.

    /// <summary>
    /// The pipeline state a segment's draws share (everything of <see cref="GraphicsPipelineDesc"/> but the vertex layout).
    /// </summary>
    readonly record struct SegmentPipeline(GpuProgram Program, AttachmentFormats Formats, BlendState Blend, ColorMask Mask,
        PolygonKind Polygon, bool AlphaToCoverage, bool DepthClamp);

    /// <summary>
    /// A mesh's own vertex buffers (locations 0 to 3: position, normal, and for the smoothed programs the tangent slot), bound with one call.
    /// </summary>
    [System.Runtime.CompilerServices.InlineArray(4)]
    internal struct OwnVertices
    {
        /// <summary>
        /// The first binding (location 0).
        /// </summary>
        GpuBufferBinding _first;
    }

    /// <summary>
    /// A mesh resolved for one program: the mesh it came from, its vertex layout and own vertex buffers (locations 0 up to the placements), its
    /// indices, and its pipelines for the last two segment states (the reflection's multisampled target alternates with the scene's).
    /// </summary>
    internal struct NativeMesh
    {
        /// <summary>
        /// The mesh the state was made for.
        /// </summary>
        public MeshBindings? Source;

        /// <summary>
        /// The program the layout and buffers are for (the smoothed and the flat ones read different locations).
        /// </summary>
        public TerrainProgram? Program;

        /// <summary>
        /// The vertex layout for <see cref="Program"/>.
        /// </summary>
        public VertexLayout? Layout;

        /// <summary>
        /// The own vertex buffers bound (the program's <see cref="TerrainProgram.Own"/>).
        /// </summary>
        public int VertexCount;

        /// <summary>
        /// The own vertex buffers.
        /// </summary>
        public OwnVertices Vertices;

        /// <summary>
        /// The index buffer.
        /// </summary>
        public GpuBufferBinding Elements;

        /// <summary>
        /// The segment numbers <see cref="PipeA"/> and <see cref="PipeB"/> were made for (A the newest; 0: none).
        /// </summary>
        public int SegA, SegB;

        /// <summary>
        /// The pipelines for the last two segment states.
        /// </summary>
        public GpuPipeline? PipeA, PipeB;
    }

    /// <summary>
    /// The number of segment state of <paramref name="p"/> in <paramref name="t"/> and <paramref name="s"/> for program kind <paramref name="kind"/>
    /// (the last one's again at once).
    /// </summary>
    int SegmentId(int kind, GpuProgram p, PassTargets t, DrawState s)
    {
        var segment = new SegmentPipeline(p, t.Formats, s.Blend, s.ColourMask, s.Polygon, s.AlphaToCoverage, s.DepthClamp);
        if (_segmentIds[kind] != 0 && segment == _lastSegment[kind]) return _segmentIds[kind];
        if (!_segmentByState.TryGetValue((kind, segment), out int id))
            _segmentByState[(kind, segment)] = id = ++_segmentCount;
        _lastSegment[kind] = segment;
        return _segmentIds[kind] = id;
    }

    /// <summary>
    /// Draws <see cref="TerrainMeshGroups.Groups"/> natively with <paramref name="p"/> inside the pass VkGl is drawing (its targets and GL's state at
    /// this point), back faces culled, a mirroring placement turning the winding round; the placements from this frame's constants. Leaves GL's state
    /// as the GL version did (culling off with back faces selected, counter-clockwise, no vertex array).
    /// <para>
    /// The step-O hot path (docs/renderer-native.md 7.5): one segment inside VkGl's pass; what all draws share once (dynamic state, the frame and
    /// terrain sets, the placements at locations 7 to 10); per draw only commands, from what each mesh keeps (<see cref="NativeMesh"/>): the
    /// pipeline, the mesh's own vertex buffers, the front face when it flips, the indices, and the draw, whose firstInstance reaches the group's
    /// placements. The vertex array is exported again only when the stamp moved.
    /// </para>
    /// </summary>
    int DrawGroups(TerrainProgram p, int kind, string label)
    {
        var placements = _meshGroups.Placements;
        var job = OpenMeshSegment(p, kind, label, placements.Handle, placements.Offset, out var s);
        foreach (var g in _meshGroups.Groups)
        {
            ref var n = ref g.Native(kind);
            if (n.Source != g.Mesh || n.Program != p) Current(ref n, g.Mesh, p);
            var pipeline = n.SegA == s.Segment ? n.PipeA!.Value : PipelineFor(ref n, p, s.Segment, s.State, s.Targets.Formats, label);
            job.Add(new MeshJob.Draw
            {
                Pipeline = pipeline,
                Front = g.Mirrored ? s.Cw : s.Ccw,
                Vertices = n.Vertices,
                VertexCount = n.VertexCount,
                Elements = n.Elements,
                IndexCount = (uint)g.IndexCount,
                Instances = (uint)g.Count,
                FirstInstance = (uint)g.Offset,
            });
        }
        _gpu.Record(label, job);
        return _meshGroups.Groups.Count;
    }

    /// <summary>
    /// What the draws of a mesh segment share (<see cref="OpenMeshSegment"/>).
    /// </summary>
    struct MeshSegment
    {
        /// <summary>
        /// The pass's targets.
        /// </summary>
        public PassTargets Targets;

        /// <summary>
        /// The pass state.
        /// </summary>
        public DrawState State;

        /// <summary>
        /// The segment state's number.
        /// </summary>
        public int Segment;

        /// <summary>
        /// The front faces of a placement as it is and mirrored.
        /// </summary>
        public FrontFaceDirection Ccw, Cw;
    }

    /// <summary>
    /// Opens a native segment for TERRAIN-mode meshes inside the host's pass: dynamic state (back faces culled, counter-clockwise), the frame and
    /// terrain sets, and the placements' rows from <paramref name="rows"/> at locations 7 to 10.
    /// </summary>
    MeshJob OpenMeshSegment(TerrainProgram p, int kind, string label, GpuBuffer rows, ulong rowsOffset, out MeshSegment s)
    {
        // Prepare (wave 4, docs/renderer-native.md 6.2): what the segment's commands need, into a job recorded by the caller.
        var targets = _gpu.CurrentTargets();
        var state = _gpu.CurrentState();
        s = new MeshSegment
        {
            Targets = targets,
            State = state,
            Segment = SegmentId(kind, p.P, targets, state),
            Ccw = FrontFaceDirection.Ccw,
            Cw = FrontFaceDirection.Cw,
        };
        var job = _meshJobs.Rent();
        (job.Owner, job.Targets, job.State, job.Program, job.Ccw, job.Count) = (this, targets, state, p.P, s.Ccw, 0);
        var view = View();
        job.Frame = _nativeFrame.Prepare(in view);
        (job.ConstantSet, job.ConstantOffset) = PrepareConstantsBinding();
        (job.Rows, job.RowsOffset) = (rows, rowsOffset);
        return job;
    }

    /// <summary>
    /// A TERRAIN-mode mesh segment as prepared (<see cref="DrawGroups"/>, <see cref="DrawIndirect"/>): recorded on any thread, the same commands as
    /// before wave 4.
    /// </summary>
    sealed class MeshJob : RecordJob
    {
        /// <summary>
        /// One mesh draw of the segment.
        /// </summary>
        public struct Draw
        {
            /// <summary>
            /// The mesh's pipeline.
            /// </summary>
            public GpuPipeline Pipeline;

            /// <summary>
            /// The front face (clockwise for a mirroring placement).
            /// </summary>
            public FrontFaceDirection Front;

            /// <summary>
            /// The mesh's own vertex buffers.
            /// </summary>
            public OwnVertices Vertices;

            /// <summary>
            /// The own vertex buffers bound.
            /// </summary>
            public int VertexCount;

            /// <summary>
            /// The index buffer.
            /// </summary>
            public GpuBufferBinding Elements;

            /// <summary>
            /// Indices, instances and the first instance (the group's placements) of a plain draw.
            /// </summary>
            public uint IndexCount, Instances, FirstInstance;

            /// <summary>
            /// Indirect: the arguments' buffer (else a plain indexed draw).
            /// </summary>
            public GpuBuffer Args;

            /// <summary>
            /// The arguments' offset in <see cref="Args"/> (20 bytes a draw).
            /// </summary>
            public ulong ArgsOffset;
        }

        /// <summary>
        /// The renderer whose job pool the job goes back to.
        /// </summary>
        public TerrainRenderer Owner = null!;

        /// <summary>
        /// The pass's targets.
        /// </summary>
        public PassTargets Targets = null!;

        /// <summary>
        /// The pass state.
        /// </summary>
        public DrawState State = null!;

        /// <summary>
        /// The program.
        /// </summary>
        public GpuProgram Program = default;

        /// <summary>
        /// The front face the segment starts with.
        /// </summary>
        public FrontFaceDirection Ccw;

        /// <summary>
        /// The view's frame binding.
        /// </summary>
        public FrameBinding Frame;

        /// <summary>
        /// The terrain constants set.
        /// </summary>
        public GpuSet ConstantSet;

        /// <summary>
        /// Its offset in the frame's buffer.
        /// </summary>
        public uint ConstantOffset;

        /// <summary>
        /// The buffer of the placements' rows.
        /// </summary>
        public GpuBuffer Rows;

        /// <summary>
        /// Their offset in it.
        /// </summary>
        public ulong RowsOffset;

        /// <summary>
        /// The draws (grown as needed).
        /// </summary>
        public Draw[] Draws = new Draw[64];

        /// <summary>
        /// The draws in <see cref="Draws"/>.
        /// </summary>
        public int Count;
        /// <inheritdoc/>
        public override int Size => Count;

        /// <summary>
        /// Adds a draw.
        /// </summary>
        public void Add(in Draw d)
        {
            if (Count == Draws.Length) Array.Resize(ref Draws, Draws.Length * 2);
            Draws[Count++] = d;
        }

        public override void Record(IGpuCommandList cmd)
        {
            var (targets, state) = (Targets, State);
            cmd.SetViewport(targets.Viewport);
            cmd.SetScissor(targets.Scissor);
            cmd.SetRaster(CullMode.Back, Ccw);
            cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
            cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
            NativeFrame.Record(cmd, Program, in Frame);
            uint offset = ConstantOffset;
            cmd.BindSets(Program, TerrainShaders.ConstantsSet, new ReadOnlySpan<GpuSet>(in ConstantSet), new ReadOnlySpan<uint>(in offset));
            // The placement's rows at locations the meshes' vertex arrays leave free (their other programs do not read them).
            Span<GpuBufferBinding> bindings = stackalloc GpuBufferBinding[4];
            for (int a = 0; a < 4; a++) bindings[a] = new GpuBufferBinding(Rows, RowsOffset + (ulong)(16 * a));
            cmd.BindVertexBuffers(TerrainShaders.MeshInstanceLocation, bindings);
            var front = Ccw;
            for (int i = 0; i < Count; i++)
            {
                ref readonly var d = ref Draws[i];
                cmd.BindPipeline(d.Pipeline);
                if (d.Front != front)
                {
                    cmd.SetFrontFace(d.Front);
                    front = d.Front;
                }
                cmd.BindVertexBuffers(0, ((ReadOnlySpan<GpuBufferBinding>)d.Vertices)[..d.VertexCount]);
                cmd.BindIndexBuffer(d.Elements, IndexKind.U32);
                if (!d.Args.IsNull) cmd.DrawIndexedIndirect(d.Args, d.ArgsOffset, 1);
                else cmd.DrawIndexed(d.IndexCount, d.Instances, 0, 0, d.FirstInstance);
            }
        }

        public override void Release()
        {
            Array.Clear(Draws, 0, Count);   // no pipelines kept alive by a pooled job
            Count = 0;
            Owner._meshJobs.Return(this);
        }
    }

    /// <summary>
    /// Draws each mesh of <paramref name="meshes"/> with its indirect arguments (20 bytes each from <paramref name="argsOffset"/>) and the placements
    /// of <paramref name="rows"/>; returns the draws.
    /// </summary>
    int DrawIndirect(ReadOnlySpan<IndirectMesh> meshes, GpuBuffer rows, ulong rowsOffset, GpuBuffer args, ulong argsOffset, TerrainProgram p,
        int kind, string label)
    {
        var job = OpenMeshSegment(p, kind, label, rows, rowsOffset, out var s);
        _meshGroups.Trim();
        for (int i = 0; i < meshes.Length; i++)
        {
            var m = meshes[i];
            // The mesh's native state lives with its group (shared with DrawMeshes' draws of the same mesh).
            var g = _meshGroups.For(m.Mesh, m.IndexCount, m.Mirrored);
            ref var n = ref g.Native(kind);
            if (n.Source != g.Mesh || n.Program != p) Current(ref n, g.Mesh, p);
            var pipeline = n.SegA == s.Segment ? n.PipeA!.Value : PipelineFor(ref n, p, s.Segment, s.State, s.Targets.Formats, label);
            job.Add(new MeshJob.Draw
            {
                Pipeline = pipeline,
                Front = m.Mirrored ? s.Cw : s.Ccw,
                Vertices = n.Vertices,
                VertexCount = n.VertexCount,
                Elements = n.Elements,
                Args = args,
                ArgsOffset = argsOffset + (ulong)i * 20,
            });
        }
        _gpu.Record(label, job);
        return meshes.Length;
    }

    /// <summary>
    /// A mesh's state for <paramref name="p"/>, made when the group meets another mesh: the layout and own vertex buffers, the placements as
    /// per-instance rows of 64 bytes at locations 7 to 10.
    /// </summary>
    static void Current(ref NativeMesh n, MeshBindings va, TerrainProgram p)
    {
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        va.Attributes.AsSpan().CopyTo(attributes);
        for (int a = 0; a < 4; a++)
            attributes[TerrainShaders.MeshInstanceLocation + a] = new LegacyProgram.Attribute(default, InternalFormat.Rgba32F, 64, true);
        n.Layout = p.VertexLayout(attributes);
        n.VertexCount = p.Own;
        p.Buffers(attributes, n.Vertices);
        n.Elements = va.Elements;
        n.Source = va;
        n.Program = p;
        n.SegA = n.SegB = 0;
        n.PipeA = n.PipeB = null;
    }

    /// <summary>
    /// The pipeline for <paramref name="n"/> in segment state <paramref name="segment"/>: one of the last two, else made and kept as the newest.
    /// </summary>
    GpuPipeline PipelineFor(ref NativeMesh n, TerrainProgram p, int segment, DrawState state, AttachmentFormats formats, string label)
    {
        if (n.SegA == segment) return n.PipeA!.Value;
        if (n.SegB == segment)
        {
            (n.SegA, n.PipeA, n.SegB, n.PipeB) = (n.SegB, n.PipeB, n.SegA, n.PipeA);
            return n.PipeA!.Value;
        }
        var topology = p.P.Tessellated ? PrimitiveType.PatchList : PrimitiveType.Triangles;
        var pipeline = _gpu.Pipelines.Get(state.Pipeline(p.P, n.Layout!, topology, formats, label));
        (n.SegB, n.PipeB) = (n.SegA, n.PipeA);
        (n.SegA, n.PipeA) = (segment, pipeline);
        return pipeline;
    }

    #endregion

    #region Rock bake

    /// <summary>
    /// The impostor bake of TERRAIN-mode rocks.
    /// </summary>
    readonly TerrainRockBake _rockBake;

    /// <summary>
    /// The vertex layout and buffers of a rock's mesh part for the bake program (<see cref="TerrainRockBake.Vertices"/>).
    /// </summary>
    internal (VertexLayout Layout, GpuBufferBinding[] Bindings) RockBakeVertices(DeviceBuffer vertices) => _rockBake.Vertices(vertices);

    /// <summary>
    /// Records one frame of a rock bake into <paramref name="cmd"/> (<see cref="TerrainRockBake.Record"/>).
    /// </summary>
    internal void RecordRockBake(IGpuCommandList cmd, AttachmentFormats formats, DrawState state, int row, float scale, float distance,
        in Matrix4x4 viewProjection, Vector3 eye, Vector3 dir, Vector3 right, Vector3 up, int pass, ImpostorBaker.Part[] parts) =>
        _rockBake.Record(cmd, formats, state, row, scale, distance, in viewProjection, eye, dir, right, up, pass, parts);

    /// <summary>
    /// True when the biome of parameter row <paramref name="row"/> has its layer textures resident now (the bake waits for it).
    /// </summary>
    internal bool CanBakeRock(int row) => _textures is { HasBiomes: true } t && t.IsResident(row);

    /// <summary>
    /// The identity of the biome's material for the atlas cache key (null: none). See <see cref="TerrainTextures.BiomeKey"/>.
    /// </summary>
    internal string? RockBiomeKey(int row) => _textures?.BiomeKey(row);

    /// <summary>
    /// The terrain's constants for a bake: the mesh material of the feature's biome <paramref name="row"/>, textured with normal maps, without the
    /// overlay and colour maps, the window or the far fade, nothing underwater.
    /// </summary>
    internal TerrainConstants BakeConstants(int row)
    {
        var c = BakeDefaults(row);
        if (_textures is { } t)
        {
            c.CellGrid = new Vector2(t.CellsX, t.CellsZ);
            var set = t.Textures;
            c.Diffuse = Index(set.Diffuse, BindlessKind.Texture2DArray, _standInArray);
            c.Normal = Index(set.Normal, BindlessKind.Texture2DArray, _standInArray);
            c.DiffuseFar = Index(set.DiffuseFar, BindlessKind.Texture2DArray, _standInArray);
            c.NormalFar = Index(set.NormalFar, BindlessKind.Texture2DArray, _standInArray);
            c.FarLod = set.FarLod;
            c.Params = Index(set.Params, BindlessKind.Texture2D, _standIn2D);
            c.Cells = Index(set.Cells, BindlessKind.UintTexture2D, _standInUInt);
            c.BlendMap = Index(set.BlendMap, BindlessKind.Texture2D, _standIn2D);
        }
        return c;
    }

    /// <summary>
    /// The bake's constants before the terrain's textures are bound: the stand-ins, the rock's biome row and the bake's switches.
    /// </summary>
    TerrainConstants BakeDefaults(int row)
    {
        return new TerrainConstants
        {
            CoarseRect = CoarseRect,
            CoarseCells = new Vector2(_coarseSize - 1f, _coarseSize - 1f),
            FineRect = FineRect,
            FineCells = new Vector2(_fine.Columns - 1f, _fine.Rows - 1f),
            FineBand = _fineBand,
            HasFine = 1,
            HeightCoarse = Index(_coarseTexture, BindlessKind.Texture2D, _standIn2D),
            HeightFine = Index(_fineTexture, BindlessKind.Texture2D, _standIn2D),
            Diffuse = _standInArray,
            Normal = _standInArray,
            DiffuseFar = _standInArray,
            NormalFar = _standInArray,
            Height = _standInArray,
            Relief = _standIn2D,
            Params = _standIn2D,
            Cells = _standInUInt,
            BlendMap = _standIn2D,
            Overlay = _standIn2D,
            Colour = _standIn2D,
            Ground = _standIn2D,
            WorldColour = _standIn2D,
            Region = _lastRegion,
            CellGrid = _lastCellGrid,
            HeightNormals = 0,
            Feature = 1,
            FeatureBiome = row,
            SunColour = Vector3.One,
            AmbientSky = Vector3.One,
            AmbientGround = Vector3.One,
            WaterHeight = -1e6f,
            HalfWorld = (float)WorldLayout.HalfWorldSize,
            FarStart = 1e9f,
            FarEnd = 2e9f,
            Textured = 1,
            NormalMaps = 1,
            HasMaps = 0,
            MapState = 0,
            ReliefQuality = GroundRelief.Quality,
            FieldTiles = _standInUInt,
        };
    }

    /// <summary>
    /// What a rock's impostor needs of the terrain to fade as the terrain's mesh shader fades (<see cref="ImpostorRockPush"/>): the maps'
    /// bindless indices, the colour window, the world's half size and the two distances (80% and 100% of <paramref name="materialDistance"/>).
    /// <c>Flags</c> bit 0 ground map, 1 whole-world colour, 2 colour map.
    /// </summary>
    internal (Vector4 Region, uint Ground, uint WorldColour, uint Colour, float HalfWorld, float FarStart, float FarEnd,
        uint Flags) RockFarView(float materialDistance)
    {
        uint flags = 0, ground = _standIn2D, worldColour = _standIn2D, colour = _standIn2D;
        var region = Vector4.Zero;
        if (_textures is { HasBiomes: true } t)
        {
            var set = t.Textures;
            if (t.HasGround)
            {
                flags |= 1;
                ground = Index(set.Ground, BindlessKind.Texture2D, _standIn2D);
            }
            if (t.HasWorldColour)
            {
                flags |= 2;
                worldColour = Index(set.WorldColour, BindlessKind.Texture2D, _standIn2D);
            }
            if (t.MapState == 2)
            {
                flags |= 4;
                colour = Index(set.Colour, BindlessKind.Texture2D, _standIn2D);
                region = t.Region;
            }
        }
        return (region, ground, worldColour, colour, (float)WorldLayout.HalfWorldSize, materialDistance * 0.8f, materialDistance, flags);
    }

    #endregion
}