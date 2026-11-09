using System.Numerics;
using Meitou.Data.World;

using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Shaders;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Shadows;
using Meitou.Game.Rendering.Sky;

namespace Meitou.Game.Rendering.Terrain;

/// <summary>
/// Switches for drawing the world (keys in <see cref="WorldApp"/>).
/// </summary>
public sealed class WorldRenderOptions
{
    public bool Textures { get; set; } = true;
    public bool NormalMaps { get; set; } = true;
    public bool Objects { get; set; } = true;
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

    public static readonly WorldLighting Default = new(Vector3.Normalize(new Vector3(0.45f, 0.75f, 0.3f)), new(1, 0.96f, 0.88f),
        new(0.36f, 0.40f, 0.46f), new(0.20f, 0.19f, 0.17f), new(0.62f, 0.68f, 0.74f), 60000);
}

/// <summary>
/// Draws the terrain of the whole world with continuous LOD (<see cref="TerrainQuadtree"/>): one shared grid patch placed per selected quadtree node,
/// heights read in the vertex shader from a coarse whole-world height texture and the loaded region's fine one, normals from the height field per
/// pixel. Nodes outside the view frustum are skipped.
/// </summary>
public sealed unsafe partial class TerrainRenderer : IDisposable
{
    static readonly bool PrivateStatics = Environment.GetEnvironmentVariable("MEITOU_METAL_PRIVATE_STATIC") == "1";
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IGpuContext, ActiveTerrain> ActiveTerrains = new();
    readonly DeviceBuffer _gridVertices, _gridIndices;
    readonly TerrainTexture _coarseTexture;
    TerrainTexture _fineTexture;
    readonly int[] _indexOffsets = new int[5], _indexCounts = new int[5], _firstIndices = new int[5];
    readonly TerrainHeightBounds _bounds;
    readonly List<TerrainNode> _nodes = [];
    HeightWindow _fine;
    readonly ushort[] _coarse;
    readonly int _coarseSize;
    float _fineBand;
    readonly TerrainQuadtree _quadtree;
    // The reflection's own tree (its own, smaller picture), so the two passes do not reset each other's ranges every frame.
    readonly TerrainQuadtree _spare;
    TerrainQuadtree _current = null!;   // the tree the running Draw selects with
    TerrainTextures? _textures;
    bool _ownsTextures = true;
    internal TerrainTextures? TextureSet => _textures;

    /// <summary>
    /// The texture field's tile at world (<paramref name="x"/>, <paramref name="z"/>) (<see cref="GroundRelief.FieldCentre"/>): its label, or null.
    /// </summary>
    public string? FieldLabelAt(float x, float z) => _textures?.FieldLabel(x, z);
    Frame _frame;

    const int GridCells = 64;

    record struct Frame(Matrix4x4 ViewProjection, Vector3 Eye, WorldRenderOptions Options, WorldLighting Light, Matrix4x4? Relative = null);

    /// <summary>
    /// The grid's vertex attribute at location 0 and its index buffer, as VkGl fed them from the GL vertex array.
    /// </summary>
    readonly LegacyProgram.Attribute _gridVertex;
    readonly GpuBufferBinding _gridElements;
    const int Colour = 0, Depth = 1, Solid = 2;
    readonly IGpuContext _gpu;
    int _frameNumber;
    readonly List<(TerrainTexture Texture, int Frame)> _retired = [];
    bool _latePending;
    Frame _lateFrame;
    PatchDraw[] _cutDraws = new PatchDraw[64];
    int _plainCount, _cutCount;
    uint _cutMask;
    PatchDraw[] _reliefDraws = new PatchDraw[16], _cutReliefDraws = new PatchDraw[16];
    int _reliefCount, _cutReliefCount;
    double[] _nodeKeys = [];
    DrawState? _lastGlState, _lastPatchState, _lastWireState, _lastPrepassState, _lastLateState, _lastLateEqualState;
    readonly NativeFrame _nativeFrame;
    readonly GpuSetLayout _constantsLayout;
    readonly TerrainProgram _patchDepth, _patchPrepass, _patchPrepassCut, _meshDepth;
    readonly TerrainProgram? _patchPrepassTess, _patchPrepassCutTess, _meshDepthTess;   // null without tessellation shaders
    // The colour programs, from the set the parallax setting asks for (UseColour): meshSolid is meshColour without the cross-fade discard
    // (EarlyDepth).
    TerrainProgram _patchColour, _patchColourNoNormal, _patchColourCut, _patchColourCutNoNormal, _meshColour, _meshColourNoNormal, _meshSolid,
        _meshSolidNoNormal;
    TerrainProgram? _patchColourTess, _patchColourCutTess, _meshColourTess, _meshSolidTess;
    readonly GpuSetLayout[] _sets;
    readonly ulong _uniformAlign;
    readonly uint _standIn2D, _standInArray, _standInUInt;

    // The terrain set (set 2): this segment's TerrainConstants in the frame's constants, bound with a dynamic offset into a set made once per
    // constants chunk (LegacyProgram's pattern for its default blocks).
    TerrainConstants _constants, _written;
    long _writtenFrame = -1;
    Transient _constantsSlice;
    readonly Dictionary<ulong, GpuSet> _constantSets = [];
    ulong _lastSetKey;
    GpuSet _lastSet;
    Vector4 _lastRegion;
    Vector2 _lastCellGrid;
    PatchDraw[] _patchDraws = new PatchDraw[512];
    readonly List<PatchPipeline> _patchPipelines = [];
    long _patchTriangles;
    readonly JobPool<PatchJob> _patchJobs = new();

    /// <summary>
    /// The interior cut of the mesh draw being prepared (<see cref="DrawMeshes"/>, <see cref="DrawMeshesIndirect"/>; docs/render-interior-cut.md),
    /// null otherwise.
    /// </summary>
    InteriorMaskRenderer? _meshCut;

    /// <summary>
    /// This call's placements (each group's contiguous, <see cref="MeshGroup.Offset"/> instances in), in the frame's constants.
    /// </summary>
    Transient _placements;
    bool _depthReady;

    // Per program kind: the last segment state and its number; numbers are stable per state (a mesh compares one int per draw).
    readonly Dictionary<(int Kind, SegmentPipeline State), int> _segmentByState = [];
    readonly SegmentPipeline[] _lastSegment = new SegmentPipeline[3];
    readonly int[] _segmentIds = new int[3];
    int _segmentCount;
    readonly Dictionary<(MeshBindings, int, bool), MeshGroup> _meshGroups = [];
    readonly List<MeshGroup> _meshGroupList = [];
    int[] _groupOf = new int[1024], _cursors = new int[64];
    readonly JobPool<MeshJob> _meshJobs = new();

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
        this._sets = sets;
        UseColour(GroundRelief.ParallaxActive, GroundRelief.Quality);
        _patchDepth = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexNative(), TerrainShaders.DepthFragmentNative(), "terrain depth");
        _patchPrepass = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexNative(), TerrainShaders.PrepassFragmentNative(), "terrain prepass");
        _patchPrepassCut = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexNative(),
            TerrainShaders.PrepassFragmentNative(interiorCut: true), "terrain prepass interior cut");
        // The ground displacement (GroundRelief): the colour and prepass programs tessellated, for the patches near the eye (the same stages in both,
        // so the late colour's depth test meets the prepass's depths).
        if (gpu.HasTessellation)
        {
            _patchPrepassTess = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexTessNative(), TerrainShaders.PrepassFragmentNative(),
                "terrain relief prepass",
                TerrainShaders.PatchControlNative(), TerrainShaders.PatchEvaluationNative());
            _patchPrepassCutTess = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexTessNative(),
                TerrainShaders.PrepassFragmentNative(interiorCut: true), "terrain relief prepass interior cut",
                TerrainShaders.PatchControlNative(), TerrainShaders.PatchEvaluationNative());
        }
        _meshDepth = new TerrainProgram(gpu, sets, TerrainShaders.MeshInstancedDepthVertexNative(), TerrainShaders.DepthFragmentNative(),
            "terrain mesh depth");
        // The rocks' smoothing (RockSmoothing): the mesh programs tessellated, the casters with the same curve.
        if (gpu.HasTessellation)
        {
            _meshDepthTess = new TerrainProgram(gpu, sets, TerrainShaders.MeshVertexTessNative(), TerrainShaders.DepthFragmentNative(),
                "terrain mesh depth smoothed",
                TerrainShaders.MeshControlNative(), TerrainShaders.MeshEvaluationDepthNative());
        }
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
    /// Width of the band at the fine window's border where it fades into the coarse grid.
    /// </summary>
    static float BandOf(HeightWindow w) => Math.Clamp((w.Columns - 1) * w.Spacing * 0.1f, w.Spacing * 4, 3000);

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

    static PixelRect Rect(int x, int y, int width, int height) => new(x, y, (uint)width, (uint)height);

    public int LevelCount => _quadtree.LevelCount;
    public double FinestSpacing => _quadtree.Spacing(0);
    public int DrawnChunks { get; private set; }
    public long DrawnTriangles { get; private set; }

    /// <summary>
    /// Starts counting drawn nodes and triangles for a new frame (several views add up).
    /// </summary>
    public void BeginFrame()
    {
        (DrawnChunks, DrawnTriangles) = (0, 0);
        _frameNumber++;
        // A replaced window is let go a few frames after the swap (its image and bindless entry are freed after the frames in flight).
        for (int i = _retired.Count - 1; i >= 0; i--)
            if (_frameNumber - _retired[i].Frame >= 4) { _retired[i].Texture.Dispose(); _retired.RemoveAt(i); }
    }

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
                texture => { _retired.Add((_fineTexture, _frameNumber)); Swap(new FineStandby(texture, window, patch)); }, done);

    /// <summary>
    /// A fine window uploaded but not drawn ("Keep textures at characters": one round a character far from the eye), ready for <see
    /// cref="SwapFine"/>.
    /// </summary>
    public sealed class FineStandby
    {
        internal FineStandby(TerrainTexture texture, HeightWindow window, TerrainHeightBounds.Patch? patch) => (Texture, Window,
            Patch) = (texture, window, patch);
        internal TerrainTexture Texture { get; }
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

    void Swap(FineStandby next)
    {
        _fineTexture = next.Texture;
        _fine = next.Window;
        _fineBand = BandOf(next.Window);
        if (next.Patch is { } patch) _bounds.Apply(patch);
        next.Patch = null;
    }

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
    /// Height at a world point from the same grids the shaders read (bilinear, blended at the region's edge).
    /// </summary>
    public float HeightAt(float x, float z) => Snapshot().HeightAt(x, z);

    /// <summary>
    /// The height sources as they are now, for a worker thread (render thread only: the fine window is swapped there).
    /// </summary>
    internal HeightSnapshot Snapshot() => new(_coarse, _coarseSize, _fine, _fineBand);

    /// <summary>
    /// The fog cull (set by the world frame around the main camera's draw only): nodes wholly hidden by the fog the eye is in are skipped.
    /// </summary>
    internal FogVolumes? FogCull { get; set; }

    /// <summary>
    /// Draws the terrain; nodes whose highest point is under <paramref name="cullBelow"/> are skipped (the reflection pass clips everything below the
    /// water). <paramref name="secondary"/>: the reflection's call, with its own quadtree for its own LOD distance.
    /// </summary>
    public void Draw(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, WorldLighting light,
        float cullBelow = float.NegativeInfinity, bool secondary = false, bool defer = false, Matrix4x4? relative = null)
    {
        UseColour(GroundRelief.ParallaxActive, GroundRelief.Quality, Trails is not null);
        _current = secondary ? _spare : _quadtree;
        _current.SetRanges(options.TerrainLod);
        _frame = new Frame(viewProjection, eye, options, light, relative);
        var fog = FogCull;
        _current.Select(eye, _bounds,
            (min, max) => max.Y >= cullBelow && WorldCamera.Intersects(frustum, min, max) &&
            (fog is null || !fog.Hidden(min, max, FogVolumes.CullKind.Terrain)), _nodes);

        SortNearestFirst(eye);
        PreparePatches(_current);
        SplitInteriorCut(secondary ? null : InteriorCut);
        SplitRelief(secondary, options);
        if (_nodes.Count > 0)
        {
            if (options.Wireframe != 2)
            {
                PrepareConstants(material: true, patches: true);
                if (defer && !secondary && options.Wireframe == 0)
                {
                    // Depth only now, the colour after the objects and foliage (DrawDeferred): the colour program then runs only where the terrain is
                    // what is seen.
                    RecordPatches(_patchPrepass, "terrain prepass", PatchMode.Prepass, _plainCount);
                    if (_reliefCount > 0)
                        RecordPatches(_patchPrepassTess!, "terrain relief prepass", PatchMode.Prepass, _reliefCount, _reliefDraws);
                    if (_cutCount > 0)
                        RecordPatches(_patchPrepassCut, "terrain prepass interior cut", PatchMode.Prepass, _cutCount, _cutDraws);
                    if (_cutReliefCount > 0)
                        RecordPatches(_patchPrepassCutTess!, "terrain relief prepass interior cut", PatchMode.Prepass, _cutReliefCount,
                                _cutReliefDraws);
                    (_latePending, _lateFrame) = (true, _frame);
                }
                else
                {
                    RecordPatches(PatchColour, "terrain", PatchMode.Solid, _plainCount);
                    if (_reliefCount > 0)
                        RecordPatches(_patchColourTess!, "terrain relief", PatchMode.Solid, _reliefCount, _reliefDraws);
                    if (_cutCount > 0)
                        RecordPatches(PatchColourCut, "terrain interior cut", PatchMode.Solid, _cutCount, _cutDraws);
                    if (_cutReliefCount > 0)
                        RecordPatches(_patchColourCutTess!, "terrain relief interior cut", PatchMode.Solid, _cutReliefCount, _cutReliefDraws);
                }
                DrawnChunks += _nodes.Count;
                DrawnTriangles += _patchTriangles;
            }
            if (options.Wireframe != 0)
            {
                // The debug outline (Wireframe 1 and 2): the same patches as lines in a dark grey, pulled towards the eye as GL's polygon offset (-1,
                // -1) did.
                PrepareConstants(material: true, patches: true);
                _constants.Wireframe = 1;
                RecordPatches(PatchColour, "terrain wireframe", PatchMode.Wireframe, _plainCount);
                RecordPatches(PatchColour, "terrain wireframe", PatchMode.Wireframe, _cutCount, _cutDraws);
            }
        }
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
            RecordPatches(_patchColourTess!, "terrain relief", PatchMode.Late, _reliefCount, _reliefDraws);
        // The cut patches' colour without the cut: their prepass discarded the cut fragments, so an equal depth test leaves them out early. The cut's
        // discard here turned the early depth test off, and the GPU drained between these draws (1.4 ms for 19 of them; docs/render-terrain.md
        // "Registers"). Both passes run the same vertex and tessellation stages (invariant positions): a kept fragment meets its own depth.
        if (_cutCount > 0)
            RecordPatches(PatchColour, "terrain interior cut", PatchMode.LateEqual, _cutCount, _cutDraws);
        if (_cutReliefCount > 0)
            RecordPatches(_patchColourTess!, "terrain relief interior cut", PatchMode.LateEqual, _cutReliefCount, _cutReliefDraws);
    }

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
    /// The ground trails the patches read (docs/render-terrain.md "Ground trails"; null: none, and the colour programs carry none of their code). Set before the scene.
    /// </summary>
    public TerrainTrailRenderer? Trails { get; set; }

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
                if (x0 <= max.X && x0 + s >= min.X && z0 <= max.Y && z0 + s >= min.Y) { overlaps = true; break; }
            if (!overlaps) { _patchDraws[kept++] = d; continue; }
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
        if (secondary || _patchColourTess is null || !GroundRelief.DisplaceActive && GroundRelief.RefinePixels <= 0 || options.Wireframe != 0
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
            if (dx * dx + dz * dz > r2) { draws[kept++] = d; continue; }
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
    enum PatchMode { Solid, Wireframe, Prepass, Late, LateEqual }

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

    // ---- the native model (docs/renderer-native.md 7.1, wave 3b agent B: step O) ----
    // Native programs (TerrainShaders' native variants: the same maths; the camera in ViewConstants, the terrain's values and the bindless
    // indices of its textures in TerrainConstants at set 2, a patch's node in push constants), recorded into the pass VkGl has open. Per
    // segment: the frame set (NativeFrame), the terrain set, the dynamic state; per draw only commands (a patch: a push and a draw; a mesh
    // group: its pipeline, buffers and draw, kept per mesh while the vertex-array stamp holds).

    /// <summary>
    /// The colour programs, with the parallax's march (<see cref="GroundRelief.ParallaxActive"/>) or without it
    /// (<see cref="TerrainShaders.FragmentNative"/>), and with the normal-map samples or without them (the no-normal variants are compile-time
    /// programs for Metal's shader compiler).
    /// </summary>
    sealed record ColourSet(bool March, TerrainProgram Patch, TerrainProgram PatchNoNormal, TerrainProgram PatchCut, TerrainProgram PatchCutNoNormal,
        TerrainProgram Mesh, TerrainProgram MeshNoNormal, TerrainProgram MeshSolid, TerrainProgram MeshSolidNoNormal,
        TerrainProgram? PatchTess, TerrainProgram? PatchCutTess, TerrainProgram? MeshTess, TerrainProgram? MeshSolidTess) : IDisposable
    {
        /// <summary>The trails' set: its patch programs are its own, its mesh programs the plain set's (<see cref="MakeTrailed"/>).</summary>
        public bool SharesMeshes { get; init; }

        public void Dispose()
        {
            foreach (var p in new[] { Patch, PatchNoNormal, PatchCut, PatchCutNoNormal, PatchTess, PatchCutTess }) p?.Dispose();
            if (SharesMeshes) return;
            foreach (var p in new[] { Mesh, MeshNoNormal, MeshSolid, MeshSolidNoNormal, MeshTess, MeshSolidTess }) p?.Dispose();
        }
    }

    readonly ColourSet?[] _colourSets = new ColourSet?[12];

    /// <summary>
    /// Draws with the colour set of <paramref name="march"/>, the relief <paramref name="quality"/> and <paramref name="trails"/> (compiled in), made the first
    /// time it is asked for (at load the settings'; a change in play makes the other once, and keeps it: switching back costs nothing).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_patchColour), nameof(_patchColourNoNormal), nameof(_patchColourCut),
        nameof(_patchColourCutNoNormal), nameof(_meshColour), nameof(_meshColourNoNormal), nameof(_meshSolid), nameof(_meshSolidNoNormal))]
    void UseColour(bool march, int quality, bool trails = false)
    {
        quality = Math.Clamp(quality, 0, 2);
        var set = trails ? _colourSets[6 + (march ? 3 : 0) + quality] ??= MakeTrailed(march, quality) : Plain(march, quality);
        (_patchColour, _patchColourNoNormal, _patchColourCut,
            _patchColourCutNoNormal) = (set.Patch, set.PatchNoNormal, set.PatchCut, set.PatchCutNoNormal);
        (_meshColour, _meshColourNoNormal, _meshSolid, _meshSolidNoNormal) = (set.Mesh, set.MeshNoNormal, set.MeshSolid, set.MeshSolidNoNormal);
        (_patchColourTess, _patchColourCutTess, _meshColourTess, _meshSolidTess) = (set.PatchTess, set.PatchCutTess, set.MeshTess, set.MeshSolidTess);
    }

    ColourSet Plain(bool march, int quality) => _colourSets[(march ? 3 : 0) + quality] ??= MakeColour(march, quality);

    /// <summary>
    /// The plain set with its patch programs built with the ground trails; the map features' meshes take no trails.
    /// </summary>
    ColourSet MakeTrailed(bool march, int quality)
    {
        TerrainProgram Patch(string fragment, string name) => new(_gpu, _sets, TerrainShaders.PatchVertexNative(), fragment, name);
        TerrainProgram? PatchTess(string fragment, string name) => _gpu.HasTessellation
            ? new(_gpu, _sets, TerrainShaders.PatchVertexTessNative(), fragment, name, TerrainShaders.PatchControlNative(),
                TerrainShaders.PatchEvaluationNative())
            : null;
        return Plain(march, quality) with
        {
            Patch = Patch(TerrainShaders.FragmentTrails(false, march, true, quality), "terrain trails"),
            PatchNoNormal = Patch(TerrainShaders.FragmentTrails(false, march, false, quality), "terrain trails no normal"),
            PatchCut = Patch(TerrainShaders.FragmentTrails(true, march, true, quality), "terrain trails interior cut"),
            PatchCutNoNormal = Patch(TerrainShaders.FragmentTrails(true, march, false, quality), "terrain trails interior cut no normal"),
            PatchTess = PatchTess(TerrainShaders.FragmentTrails(false, march, true, quality), "terrain trails relief"),
            PatchCutTess = PatchTess(TerrainShaders.FragmentTrails(true, march, true, quality), "terrain trails relief interior cut"),
            SharesMeshes = true,
        };
    }

    ColourSet MakeColour(bool march, int quality)
    {
        bool tess = _gpu.HasTessellation;
        TerrainProgram Patch(string fragment, string name) => new(_gpu, _sets, TerrainShaders.PatchVertexNative(), fragment, name);
        TerrainProgram? PatchTess(string fragment, string name) => tess
            ? new(_gpu, _sets, TerrainShaders.PatchVertexTessNative(), fragment, name, TerrainShaders.PatchControlNative(),
                TerrainShaders.PatchEvaluationNative())
            : null;
        TerrainProgram Mesh(string fragment, string name) => new(_gpu, _sets, TerrainShaders.MeshVertexNative(), fragment, name);
        TerrainProgram? MeshTess(string fragment, string name) => tess
            ? new(_gpu, _sets, TerrainShaders.MeshVertexTessNative(), fragment, name, TerrainShaders.MeshControlNative(),
                TerrainShaders.MeshEvaluationNative())
            : null;
        return new ColourSet(march,
            Patch(TerrainShaders.FragmentNative(march: march, reliefQuality: quality), "terrain"),
            Patch(TerrainShaders.FragmentNative(march: march, normalMaps: false, reliefQuality: quality), "terrain no normal"),
            Patch(TerrainShaders.FragmentNative(interiorCut: true, march: march, reliefQuality: quality), "terrain interior cut"),
            Patch(TerrainShaders.FragmentNative(interiorCut: true, march: march, normalMaps: false, reliefQuality: quality),
                    "terrain interior cut no normal"),
            Mesh(TerrainShaders.MeshFragmentNative(march, reliefQuality: quality), "terrain meshes"),
            Mesh(TerrainShaders.MeshFragmentNative(march, normalMaps: false, reliefQuality: quality), "terrain meshes no normal"),
            Mesh(TerrainShaders.MeshFragmentSolidNative(march, reliefQuality: quality), "terrain meshes solid"),
            Mesh(TerrainShaders.MeshFragmentSolidNative(march, normalMaps: false, reliefQuality: quality), "terrain meshes solid no normal"),
            PatchTess(TerrainShaders.FragmentNative(march: march, reliefQuality: quality), "terrain relief"),
            PatchTess(TerrainShaders.FragmentNative(interiorCut: true, march: march, reliefQuality: quality), "terrain relief interior cut"),
            MeshTess(TerrainShaders.MeshFragmentNative(march, reliefQuality: quality), "terrain meshes smoothed"),
            MeshTess(TerrainShaders.MeshFragmentSolidNative(march, reliefQuality: quality), "terrain meshes solid smoothed"));
    }

    /// <summary>
    /// Whether the terrain colour programs may sample their normal-map arrays this frame: the option is on and the biome layers are resident. The
    /// no-normal programs are compile-time variants, so this must select the matching program.
    /// </summary>
    bool NormalMapsOn => _frame.Options.Textures && _frame.Options.NormalMaps && _textures is { HasBiomes: true };

    TerrainProgram PatchColour => NormalMapsOn ? _patchColour : _patchColourNoNormal;
    TerrainProgram PatchColourCut => NormalMapsOn ? _patchColourCut : _patchColourCutNoNormal;
    TerrainProgram MeshColour => NormalMapsOn ? _meshColour : _meshColourNoNormal;
    TerrainProgram MeshSolid => NormalMapsOn ? _meshSolid : _meshSolidNoNormal;

    /// <summary>
    /// A mesh program, or its tessellated twin while the rocks are smoothed (<see cref="RockSmoothing.TessellateActive"/>).
    /// </summary>
    TerrainProgram Smoothed(TerrainProgram p, TerrainProgram? tessellated) => RockSmoothing.TessellateActive && tessellated is not null
        ? tessellated : p;
    static SamplerInfo StandInInfo(bool arrayed, ScalarKind kind) => new("", 0, 0, SamplerDimension.Dim2D, arrayed, false, false, kind, 0);

    /// <summary>
    /// A native program of the terrain with its vertex inputs from the reflection: the layout and buffers a GL vertex array feeds it, as VkGl feeds a
    /// GL program (<see cref="LegacyProgram.VertexLayout"/>: a disabled attribute reads GL's constant through a stride-0 binding).
    /// </summary>
    sealed class TerrainProgram : IDisposable
    {
        readonly IGpuContext _ctx;
        public readonly GpuProgram P;
        readonly int[] _locations;
        readonly ScalarKind[] _kinds;
        /// <summary>
        /// The program's own input locations (below the placements' rows): 0 .. Own − 1.
        /// </summary>
        public readonly int Own;
        VertexLayout? _last;

        public TerrainProgram(IGpuContext ctx, GpuSetLayout[] sets, string vertex, string fragment, string name,
            string? control = null, string? evaluation = null)
        {
            this._ctx = ctx;
            P = control is null || evaluation is null
                ? ctx.Shaders.Native(vertex, fragment, name, sets, NativeShaders.PushBytes)
                : ctx.Shaders.NativeTessellated(vertex, control, evaluation, fragment, name, sets, NativeShaders.PushBytes);
            var inputs = P.Info.VertexReflection!.Inputs;
            _locations = [.. inputs.SelectMany(i => Enumerable.Range(i.Location, i.Slots))];
            _kinds = [.. inputs.SelectMany(i => Enumerable.Repeat(i.Kind, i.Slots))];
            Own = _locations.Where(l => l < TerrainShaders.MeshInstanceLocation).DefaultIfEmpty(-1).Max() + 1;
            if (Own > 4) throw new NotSupportedException($"{name}: more than four own vertex inputs");
        }


        public VertexLayout VertexLayout(ReadOnlySpan<LegacyProgram.Attribute?> byLocation)
        {
            Span<VertexInput> inputs = stackalloc VertexInput[_locations.Length];
            for (int i = 0; i < inputs.Length; i++)
            {
                int loc = _locations[i];
                inputs[i] = loc < byLocation.Length && byLocation[loc] is { } a
                    ? new VertexInput((uint)loc, a.Format, a.Stride, a.PerInstance)
                    : new VertexInput((uint)loc, GlConventions.DummyVertexFormat(_kinds[i]), _ctx.ConstantVertexAttributes ? 0u : 16u, false);
            }
            if (_last is { } l && inputs.SequenceEqual(l.Inputs)) return l;
            return _last = new VertexLayout(inputs.ToArray());
        }

        /// <summary>
        /// Locations 0 .. <see cref="Own"/> − 1 into <paramref name="result"/>: each attribute's buffer, GL's constant where none.
        /// </summary>
        public void Buffers(ReadOnlySpan<LegacyProgram.Attribute?> byLocation, Span<GpuBufferBinding> result)
        {
            for (int loc = 0; loc < Own; loc++)
            {
                int input = Array.IndexOf(_locations, loc);
                result[loc] = loc < byLocation.Length && byLocation[loc] is { } a
                    ? a.Buffer
                    : _ctx.ConstantVertexAttributes
                        ? new GpuBufferBinding(_ctx.DummyVertex, GlConventions.DummyVertexOffset(input >= 0 ? _kinds[input] : ScalarKind.Float))
                        : new GpuBufferBinding(_ctx.DummyVertexArray(input >= 0 ? _kinds[input] : ScalarKind.Float), 0);
            }
        }

        public void Dispose()
        {
            _ctx.Pipelines.Forget(P);
            _ctx.Shaders.Dispose(P);
        }
    }

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
        var c = new TerrainConstants
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
            Trails = _standIn2D,
            TrailFine = _standIn2D,
        };
        if (material)
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
            if (patches) ApplyMaterialCache(ref c);
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
            if (t is not null)
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
                c.Displace = patches && textured && GroundRelief.DisplaceActive && _patchColourTess is not null && set.Height is not null
                    ? GroundRelief.Depth : 0;
                (c.DisplaceEnd, c.ParallaxEnd) = (GroundRelief.DisplaceDistance, GroundRelief.ParallaxDistance);
                (c.OverlayRelief, c.SelfShadow, c.TargetSlope) = (GroundRelief.OverlayRelief, GroundRelief.SelfShadow, GroundRelief.TargetSlope);
                (c.MaxDepth, c.ScaleFollow,
                    c.CliffScale) = (GroundRelief.MaxDepth / GroundRelief.ReferenceDepth, GroundRelief.ScaleFollow, GroundRelief.CliffScale);
                (c.Refine, c.PixelScale, c.BroadCut) = (patches && _patchColourTess is not null ? GroundRelief.RefinePixels : 0,
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
            if (patches) Trails?.Apply(ref c);
        }
        _constants = c;
    }

    /// <summary>
    /// The camera of the frame (<see cref="Draw"/>'s or <see cref="DrawDepth"/>'s) as <see cref="ViewConstants"/>.
    /// </summary>
    ViewConstants View()
    {
        var light = _frame.Light;
        return new ViewConstants
        {
            // The vertex stages project relative to the eye (RenderOrigin): the main view's precise matrix, else translation(eye) × the pass's.
            ViewProjection = RenderOrigin.ViewProjection(_frame.ViewProjection, _frame.Eye, _frame.Relative),
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

    GpuSet ConstantSet(GpuBuffer buffer) => _gpu.AllocateConstantSet(_constantsLayout, buffer, (ulong)sizeof(TerrainConstants));

    // ---- the terrain patches ----

    /// <summary>
    /// One selected node as its draw needs it (Prepare fills the list, Record walks it): the push constants (<c>uNode</c>,
    /// <c>uMorph</c>) and the index range (<c>Quadrant + 1</c>).
    /// </summary>
    struct PatchDraw
    {
        public TerrainPush Push;
        public int Part;
    }

    /// <summary>
    /// A patch program resolved for one pass state: the pipeline, the grid's vertex buffer and its index buffer.
    /// </summary>
    sealed class PatchPipeline
    {
        public SegmentPipeline Segment;
        public GpuPipeline Pipeline = default;
        public GpuBufferBinding Vertex;
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
        public TerrainRenderer Owner = null!;
        public PassTargets Targets = null!;
        public DrawState State = null!;
        public GpuProgram Program = default;
        public GpuPipeline Pipeline = default;
        public GpuBufferBinding Vertex, Elements;
        public FrameBinding Frame;
        public GpuSet ConstantSet;
        public uint ConstantOffset;
        public GpuStage Stages;   // the program's push range: a push names every stage of it
        public PatchDraw[] Draws = new PatchDraw[256];
        public int Count;
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

    static Vector4 CoarseRect => new(-WorldLayout.HalfWorldSize, -WorldLayout.HalfWorldSize, WorldLayout.HalfWorldSize, WorldLayout.HalfWorldSize);

    Vector4 FineRect
    {
        get
        {
            var (x0, z0) = _fine.WorldOf(0, 0);
            return new((float)x0, (float)z0, (float)x0 + (_fine.Columns - 1) * _fine.Spacing, (float)z0 + (_fine.Rows - 1) * _fine.Spacing);
        }
    }

    /// <summary>
    /// The heights as frame globals (docs/renderer-native.md 4.3): the two textures (native, with the linear, clamped sampler their GL versions had)
    /// and the <see cref="TerrainShaders.HeightFunctions"/> uniforms, read when a consumer draws or applies its globals.
    /// </summary>
    sealed class ActiveTerrain { public TerrainRenderer? Owner; }
    internal void Activate()
    {
        var binding = ActiveTerrains.GetValue(_gpu, static _ => new());
        if (ReferenceEquals(binding.Owner, this)) return;
        binding.Owner = this;
        PublishGlobals();
    }

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

    /// <summary>
    /// Draws other meshes with the terrain material (TERRAIN-mode map features), after <see cref="Draw"/> set the frame. Each item: a mesh with
    /// position at location 0 and normal at 1, its index count, its transform. As the game's <c>Feature_Terrain_DX11</c> (docs/formats/foliage.md,
    /// "TERRAIN-mode meshes"): one biome per mesh, the one of <c>biomemap.png</c> at the mesh's origin, back faces culled. Drawn instanced (<see
    /// cref="GroupMeshes"/>); returns the number of draw calls.
    /// </summary>
    public int DrawMeshes(List<(MeshBindings Mesh, int IndexCount, Matrix4x4 Model)> meshes, bool depth = false, InteriorMaskRenderer? cut = null)
    {
        if (depth) return DrawMeshesDepth(meshes);
        if (!GroupMeshes(meshes, biomes: true)) return 0;
        if (cut is { Active: true })
        {
            // Placements over an interior mask (the caller splits them out): the program with the cut, on for this draw.
            _meshCut = cut;
            try { PrepareConstants(material: true, patches: false); }
            finally { _meshCut = null; }
            return DrawGroups(Smoothed(MeshColour, _meshColourTess), Colour, "terrain meshes interior cut");
        }
        PrepareConstants(material: true, patches: false);
        // No placement of this path fades (row 1's w is 0): the program without the discard, so the depth test runs first (EarlyDepth).
        return EarlyDepth.Enabled ? DrawGroups(Smoothed(MeshSolid, _meshSolidTess), Solid, "terrain meshes solid")
            : DrawGroups(Smoothed(MeshColour, _meshColourTess), Colour, "terrain meshes");
    }

    // ---- depth only (the sun's shadow map, ShadowPass) ----

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
        try { _quadtree.Select(eye, _bounds, (min, max) => WorldCamera.Intersects(frustum, min, max), _nodes); }
        finally { _quadtree.FloorLevel = 0; }
        PreparePatches(_quadtree);
        if (_nodes.Count > 0)
        {
            PrepareConstants(material: false, patches: true);
            RecordPatches(_patchDepth, "terrain depth");
            DepthTriangles += _patchTriangles;
        }
    }

    /// <summary>
    /// Terrain triangles the depth draws have drawn since the counter was last reset (by the caller).
    /// </summary>
    public long DepthTriangles { get; set; }

    int DrawMeshesDepth(List<(MeshBindings Mesh, int IndexCount, Matrix4x4 Model)> meshes)
    {
        if (!_depthReady || !GroupMeshes(meshes, biomes: false)) return 0;   // the frame's matrix is the cascade's once DrawDepth has run
        PrepareConstants(material: false, patches: false);
        return DrawGroups(Smoothed(_meshDepth, _meshDepthTess), Depth, "terrain mesh depth");
    }

    // ---- the TERRAIN-mode meshes, instanced ----
    // One instanced draw per (vertex array, index count, winding) instead of one draw per placement (a draw costs ~4 µs of CPU
    // through VkGl, and a forest has hundreds of TERRAIN-mode rocks on screen and thousands in a shadow cascade). The placements keep
    // their order within a group; the groups follow the first placement of each. The pictures are the same as with one draw each
    // (docs/render-shadows.md, "Shadow pass cost"): the shaders do the uniform form's arithmetic, a depth-only pass keeps the nearest fragment
    // whatever the order, and in colour only exactly equal depths of two different placements could tell the order apart.

    sealed class MeshGroup
    {
        public MeshBindings Mesh = null!;
        public int IndexCount, Count, Offset, Index;
        public bool Mirrored;
        /// <summary>
        /// What a native draw of this mesh needs, per program. Inline (no object of its own), so a draw reads what grouping has just touched.
        /// </summary>
        public NativeMesh ColourNative, DepthNative, SolidNative;
        public ref NativeMesh Native(int kind) => ref kind == Colour ? ref ColourNative : ref (kind == Depth ? ref DepthNative : ref SolidNative);
    }

    /// <summary>
    /// The pipeline state a segment's draws share (everything of <see cref="GraphicsPipelineDesc"/> but the vertex layout).
    /// </summary>
    readonly record struct SegmentPipeline(GpuProgram Program, AttachmentFormats Formats, BlendState Blend, ColorMask Mask,
        PolygonKind Polygon, bool AlphaToCoverage, bool DepthClamp);

    /// <summary>
    /// A mesh's own vertex buffers (locations 0 to 3: position, normal, and for the smoothed programs the tangent slot), bound with one call.
    /// </summary>
    [System.Runtime.CompilerServices.InlineArray(4)]
    struct OwnVertices { GpuBufferBinding _first; }

    /// <summary>
    /// A mesh resolved for one program: the mesh it came from, its vertex layout and own vertex buffers (locations 0 up to the placements), its
    /// indices, and its pipelines for the last two segment states (the reflection's multisampled target alternates with the scene's).
    /// </summary>
    struct NativeMesh
    {
        public MeshBindings? Source;
        /// <summary>
        /// The program the layout and buffers are for (the smoothed and the flat ones read different locations).
        /// </summary>
        public TerrainProgram? Program;
        public VertexLayout? Layout;
        public int VertexCount;
        public OwnVertices Vertices;
        public GpuBufferBinding Elements;
        public int SegA, SegB;
        public GpuPipeline? PipeA, PipeB;
    }

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
    /// Sorts the placements into <see cref="_meshGroupList"/> and writes them into this frame's constants (<see cref="_placements"/>; each one's
    /// matrix as its four rows; with <paramref name="biomes"/> row 0's w, which only feeds the position's unused w, carries the biome row). False
    /// when there is none.
    /// </summary>
    bool GroupMeshes(List<(MeshBindings Mesh, int IndexCount, Matrix4x4 Model)> meshes, bool biomes)
    {
        foreach (var g in _meshGroupList) g.Count = 0;
        _meshGroupList.Clear();
        if (_meshGroups.Count > 4096) _meshGroups.Clear();   // meshes come and go with streaming
        var items = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(meshes);
        if (_groupOf.Length < items.Length) _groupOf = new int[Math.Max(items.Length, _groupOf.Length * 2)];
        // Pass 1: each placement's group (the callers list a mesh's placements in runs, so the last group usually answers without a lookup).
        MeshGroup? last = null;
        for (int i = 0; i < items.Length; i++)
        {
            ref readonly var item = ref items[i];
            bool mirrored = item.Model.GetDeterminant() < 0;
            var g = last;
            if (g is null || g.Mesh != item.Mesh || g.IndexCount != item.IndexCount || g.Mirrored != mirrored)
            {
                var key = (item.Mesh, item.IndexCount, mirrored);
                if (!_meshGroups.TryGetValue(key, out g))
                    _meshGroups[key] = g = new MeshGroup { Mesh = item.Mesh, IndexCount = item.IndexCount, Mirrored = mirrored };
                if (g.Count == 0) { g.Index = _meshGroupList.Count; _meshGroupList.Add(g); }
                last = g;
            }
            g.Count++;
            _groupOf[i] = g.Index;
        }
        if (_meshGroupList.Count == 0) return false;
        int total = 0;
        if (_cursors.Length < _meshGroupList.Count)
            _cursors = new int[Math.Max(_meshGroupList.Count, _cursors.Length * 2)];
        for (int k = 0; k < _meshGroupList.Count; k++) { var g = _meshGroupList[k]; _cursors[k] = g.Offset = total; total += g.Count; }
        // Pass 2: each placement straight into its group's place in the frame's (write-combined) constants, one 64-byte line each.
        _placements = _gpu.Frame.AllocateConstants((ulong)(total * 64), 64);
        var target = (Matrix4x4*)_placements.Pointer;
        var t = biomes ? _textures : null;
        for (int i = 0; i < items.Length; i++)
        {
            var placed = items[i].Model;
            // Not resident yet (or no textures): -1, blend the biomes as the terrain does.
            if (biomes) placed.M14 = t?.FeatureBiomeRow(placed.M41, placed.M43) ?? -1;
            target[_cursors[_groupOf[i]]++] = placed;
        }
        return true;
    }

    /// <summary>
    /// Draws <see cref="_meshGroupList"/> natively with <paramref name="p"/> inside the pass VkGl is drawing (its targets and GL's state at this
    /// point), back faces culled, a mirroring placement turning the winding round; the placements from this frame's constants. Leaves GL's state as
    /// the GL version did (culling off with back faces selected, counter-clockwise, no vertex array).
    /// <para>
    /// The step-O hot path (docs/renderer-native.md 7.5): one segment inside VkGl's pass; what all draws share once (dynamic state, the frame and
    /// terrain sets, the placements at locations 7 to 10); per draw only commands, from what each mesh keeps (<see cref="NativeMesh"/>): the
    /// pipeline, the mesh's own vertex buffers, the front face when it flips, the indices, and the draw, whose firstInstance reaches the group's
    /// placements. The vertex array is exported again only when the stamp moved.
    /// </para>
    /// </summary>
    int DrawGroups(TerrainProgram p, int kind, string label)
    {
        var job = OpenMeshSegment(p, kind, label, _placements.Handle, _placements.Offset, out var s);
        foreach (var g in _meshGroupList)
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
        return _meshGroupList.Count;
    }

    /// <summary>
    /// What the draws of a mesh segment share (<see cref="OpenMeshSegment"/>).
    /// </summary>
    struct MeshSegment
    {
        public PassTargets Targets;
        public DrawState State;
        public int Segment;
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
        public struct Draw
        {
            public GpuPipeline Pipeline;
            public FrontFaceDirection Front;
            public OwnVertices Vertices;
            public int VertexCount;
            public GpuBufferBinding Elements;
            public uint IndexCount, Instances, FirstInstance;
            /// <summary>
            /// Indirect: the arguments' buffer (else a plain indexed draw).
            /// </summary>
            public GpuBuffer Args;
            public ulong ArgsOffset;
        }

        public TerrainRenderer Owner = null!;
        public PassTargets Targets = null!;
        public DrawState State = null!;
        public GpuProgram Program = default;
        public FrontFaceDirection Ccw;
        public FrameBinding Frame;
        public GpuSet ConstantSet;
        public uint ConstantOffset;
        public GpuBuffer Rows;
        public ulong RowsOffset;
        public Draw[] Draws = new Draw[64];
        public int Count;
        public override int Size => Count;

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
                if (d.Front != front) { cmd.SetFrontFace(d.Front); front = d.Front; }
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
    /// A TERRAIN-mode mesh drawn indirect (<see cref="DrawMeshesIndirect"/>): the vertex array (position at 0, normal at 1), its index count, and
    /// whether its placements mirror (the winding turns round), as <see cref="DrawMeshes"/> groups them.
    /// </summary>
    public readonly record struct IndirectMesh(MeshBindings Mesh, int IndexCount, bool Mirrored);

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
            return DrawIndirect(meshes, rows, rowsOffset, args, argsOffset, Smoothed(_meshDepth, _meshDepthTess), Depth, "terrain mesh depth");
        }
        bool solidDraw = solid && EarlyDepth.Enabled;
        // cut: the interior cut on in the program with the discard (the caller's cull lays the placements over a mask out with the fading ones).
        _meshCut = solidDraw ? null : cut;
        try { PrepareConstants(material: true, patches: false); }
        finally { _meshCut = null; }
        // solid: none of these placements is mid cross-fade (the caller splits them), so the program without the discard draws them (EarlyDepth).
        return solidDraw
            ? DrawIndirect(meshes, rows, rowsOffset, args, argsOffset, Smoothed(MeshSolid, _meshSolidTess), Solid, "terrain meshes solid")
            : DrawIndirect(meshes, rows, rowsOffset, args, argsOffset, Smoothed(MeshColour, _meshColourTess), Colour, "terrain meshes");
    }

    int DrawIndirect(ReadOnlySpan<IndirectMesh> meshes, GpuBuffer rows, ulong rowsOffset, GpuBuffer args, ulong argsOffset, TerrainProgram p,
        int kind, string label)
    {
        var job = OpenMeshSegment(p, kind, label, rows, rowsOffset, out var s);
        if (_meshGroups.Count > 4096) _meshGroups.Clear();   // meshes come and go with streaming
        for (int i = 0; i < meshes.Length; i++)
        {
            var m = meshes[i];
            // The mesh's native state lives with its group (shared with DrawMeshes' draws of the same mesh).
            var key = (m.Mesh, m.IndexCount, m.Mirrored);
            if (!_meshGroups.TryGetValue(key, out var g))
                _meshGroups[key] = g = new MeshGroup { Mesh = m.Mesh, IndexCount = m.IndexCount, Mirrored = m.Mirrored };
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

    GpuPipeline PipelineFor(ref NativeMesh n, TerrainProgram p, int segment, DrawState state, AttachmentFormats formats, string label)
    {
        if (n.SegA == segment) return n.PipeA!.Value;
        if (n.SegB == segment) { (n.SegA, n.PipeA, n.SegB, n.PipeB) = (n.SegB, n.PipeB, n.SegA, n.PipeA); return n.PipeA!.Value; }
        var topology = p.P.Tessellated ? PrimitiveType.PatchList : PrimitiveType.Triangles;
        var pipeline = _gpu.Pipelines.Get(state.Pipeline(p.P, n.Layout!, topology, formats, label));
        (n.SegB, n.PipeB) = (n.SegA, n.PipeA);
        (n.SegA, n.PipeA) = (segment, pipeline);
        return pipeline;
    }

    public void Dispose()
    {
        DisposeMaterialCache();
        _gridVertices.Dispose();
        _gridIndices.Dispose();
        _coarseTexture.Dispose();
        _fineTexture.Dispose();
        foreach (var r in _retired) r.Texture.Dispose();
        if (_ownsTextures) _textures?.Dispose();
        foreach (var p in new[] { _patchDepth, _patchPrepass, _patchPrepassCut, _meshDepth }) p.Dispose();
        foreach (var set in _colourSets) set?.Dispose();
        _patchPrepassTess?.Dispose();
        _patchPrepassCutTess?.Dispose();
        _meshDepthTess?.Dispose();
        _rockBake?.Dispose();
        _gpu.Bindless.Free(BindlessKind.Texture2D, _standIn2D);
        _gpu.Bindless.Free(BindlessKind.Texture2DArray, _standInArray);
        _gpu.Bindless.Free(BindlessKind.UintTexture2D, _standInUInt);
        _nativeFrame.Dispose();
    }
}