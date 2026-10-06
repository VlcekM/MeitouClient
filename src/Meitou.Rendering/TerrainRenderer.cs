using System.Numerics;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan.Shaders;

namespace Meitou.Rendering;

/// <summary>Switches for drawing the world (keys in <see cref="WorldApp"/>).</summary>
public sealed class WorldRenderOptions
{
    public bool Textures { get; set; } = true;
    public bool NormalMaps { get; set; } = true;
    public bool Objects { get; set; } = true;
    public bool Water { get; set; } = true;
    /// <summary>The water reflects the mirrored scene (<see cref="ReflectionPass"/>) instead of just the sky colour.</summary>
    public bool Reflections { get; set; } = true;
    /// <summary>0 solid, 1 solid + wireframe, 2 wireframe only.</summary>
    public int Wireframe { get; set; }
    /// <summary>0 normal, 1 blend-map slot weights, 2 layer weights (R cliff, G slope, B grass), 3 plain shading.</summary>
    public int Debug { get; set; }
    /// <summary>
    /// Terrain LOD (<see cref="Meitou.Data.World.TerrainLod"/>): the screen-space height error allowed near the eye, in pixels of
    /// the picture rendered (<c>--terrain-error</c>, the Tab slider), and the error it grows to from twice <see cref="TerrainRampStart"/>
    /// on (<c>--terrain-far-error</c>; equal to the near one: no ramp).
    /// </summary>
    public float TerrainPixelError { get; set; } = DefaultTerrainPixelError;
    public float TerrainFarPixelError { get; set; } = DefaultTerrainFarPixelError;
    public float TerrainRampStart { get; set; } = DefaultTerrainRampStart;
    public const float DefaultTerrainPixelError = 10, DefaultTerrainFarPixelError = 10, DefaultTerrainRampStart = 7500;
    /// <summary>
    /// Pixels per world unit at distance 1 in the picture being drawn: the frame sets it from the render height (after the
    /// upscaler's render scale) and the field of view, the reflection pass from its own (half-size) target.
    /// </summary>
    public float TerrainPixelScale { get; set; } = TerrainLod.ProjectionScale(720, 50 * MathF.PI / 180);
    /// <summary>The LOD rule these options give.</summary>
    public TerrainLod TerrainLod => new(TerrainPixelScale, TerrainPixelError, TerrainFarPixelError, TerrainRampStart);
    /// <summary>Beyond this distance the terrain takes the biomes' ground colour (the game's material distance is 30000).</summary>
    public float MaterialDistance { get; set; } = 30000;
}

/// <summary>Sun and sky light for the world shaders (from <see cref="WorldSky"/>).</summary>
public readonly record struct WorldLighting(Vector3 SunDirection, Vector3 SunColour, Vector3 AmbientSky, Vector3 AmbientGround, Vector3 FogColour, float FogDistance)
{
    public static readonly WorldLighting Default = new(Vector3.Normalize(new Vector3(0.45f, 0.75f, 0.3f)), new(1, 0.96f, 0.88f),
        new(0.36f, 0.40f, 0.46f), new(0.20f, 0.19f, 0.17f), new(0.62f, 0.68f, 0.74f), 60000);
}

/// <summary>
/// Draws the terrain of the whole world with continuous LOD (<see cref="TerrainQuadtree"/>): one shared grid patch
/// placed per selected quadtree node, heights read in the vertex shader from a coarse whole-world height texture and
/// the loaded region's fine one, normals from the height field per pixel. Nodes outside the view frustum are skipped.
/// </summary>
public sealed unsafe class TerrainRenderer : IDisposable
{
    readonly IGl gl;
    readonly uint patchProgram;
    readonly Dictionary<(uint, string), int> uniforms = [];
    readonly uint gridVao, gridVbo, gridEbo, coarseTexture;
    uint fineTexture;
    readonly int[] indexOffsets = new int[5], indexCounts = new int[5], firstIndices = new int[5];
    readonly TerrainHeightBounds bounds;
    readonly List<TerrainNode> nodes = [];
    HeightWindow fine;
    readonly ushort[] coarse;
    readonly int coarseSize;
    float fineBand;
    readonly TerrainQuadtree quadtree;
    readonly TerrainQuadtree spare;   // the reflection's own tree (its own, smaller picture), so the two passes do not reset each other's ranges every frame
    TerrainQuadtree current = null!;   // the tree the running Draw selects with
    TerrainTextures? textures;
    Frame frame;

    const int GridCells = 64;

    /// <summary><c>MEITOU_TERRAIN_LOD_LOG=1</c>: print the level ranges when they change and the drawn nodes per level now and then.</summary>
    static readonly bool LodLog = Environment.GetEnvironmentVariable("MEITOU_TERRAIN_LOD_LOG") == "1";

    record struct Frame(Matrix4x4 ViewProjection, Vector3 Eye, WorldRenderOptions Options, WorldLighting Light);

    /// <param name="coarse">Whole-world raw heights, (<paramref name="coarseSize"/>)² samples (2^n + 1 per side).</param>
    /// <param name="fine">The loaded region at its own step.</param>
    public TerrainRenderer(IGl gl, GpuContext gpu, ushort[] coarse, int coarseSize, HeightWindow fine)
    {
        this.gl = gl;
        this.gpu = gpu;
        this.fine = fine;
        this.coarse = coarse;
        this.coarseSize = coarseSize;
        bounds = new TerrainHeightBounds(coarse, coarseSize, fine);
        quadtree = new TerrainQuadtree(fine.Spacing, GridCells, new WorldRenderOptions().TerrainLod);
        spare = new TerrainQuadtree(fine.Spacing, GridCells, new WorldRenderOptions().TerrainLod);
        fineBand = BandOf(fine);
        // The GL patch program draws only the debug outline now; making it also assigns the atmosphere's sampler units and publishes the
        // shadow globals (WorldGl.Program), which the native frame set reads.
        patchProgram = WorldGl.Program(gl, TerrainShaders.PatchVertex, TerrainShaders.Fragment);
        // The native programs (docs/renderer-native.md 7.1, step O), all made here so a draw never compiles.
        nativeFrame = new NativeFrame(gpu);
        uniformAlign = Math.Max(gpu.Device.Limits.MinUniformBufferOffsetAlignment, 16);
        constantsLayout = gpu.Shaders.CreateSetLayout(
            [new Silk.NET.Vulkan.DescriptorSetLayoutBinding(0, Silk.NET.Vulkan.DescriptorType.UniformBufferDynamic, 1,
                Silk.NET.Vulkan.ShaderStageFlags.VertexBit | Silk.NET.Vulkan.ShaderStageFlags.FragmentBit)], 0);
        Silk.NET.Vulkan.DescriptorSetLayout[] sets = [nativeFrame.SetLayout, gpu.Bindless.Layout, constantsLayout];
        patchColour = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexNative(), TerrainShaders.FragmentNative(), "terrain");
        patchDepth = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexNative(), TerrainShaders.DepthFragmentNative(), "terrain depth");
        meshColour = new TerrainProgram(gpu, sets, TerrainShaders.MeshVertexNative(), TerrainShaders.MeshFragmentNative(), "terrain meshes");
        meshDepth = new TerrainProgram(gpu, sets, TerrainShaders.MeshInstancedDepthVertexNative(), TerrainShaders.DepthFragmentNative(), "terrain mesh depth");
        // GL's stand-ins for an absent texture, in the array each sampler indexes (IGlInterop.Bindless(0) gives the float 2D one only).
        standIn2D = gpu.Bindless.Register(BindlessKind.Texture2D, gpu.Dummy(StandInInfo(false, ScalarKind.Float)));
        standInArray = gpu.Bindless.Register(BindlessKind.Texture2DArray, gpu.Dummy(StandInInfo(true, ScalarKind.Float)));
        standInUInt = gpu.Bindless.Register(BindlessKind.UTexture2D, gpu.Dummy(StandInInfo(false, ScalarKind.UInt)));

        coarseTexture = HeightTexture(coarse, coarseSize, coarseSize);
        fineTexture = HeightTexture(fine.Raw, fine.Columns, fine.Rows);
        PublishGlobals();

        // The patch grid and its index ranges: the whole grid, then the four quarters.
        var grid = new float[(GridCells + 1) * (GridCells + 1) * 2];
        for (int j = 0; j <= GridCells; j++)
            for (int i = 0; i <= GridCells; i++)
                (grid[(j * (GridCells + 1) + i) * 2], grid[(j * (GridCells + 1) + i) * 2 + 1]) = (i, j);
        var indices = new List<uint>();
        for (int q = -1; q < 4; q++)
        {
            var part = TerrainQuadtree.GridIndices(GridCells, q);
            indexOffsets[q + 1] = indices.Count * sizeof(uint);
            firstIndices[q + 1] = indices.Count;
            indexCounts[q + 1] = part.Length;
            indices.AddRange(part);
        }
        gridVao = gl.GenVertexArray();
        gl.BindVertexArray(gridVao);
        gridVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, gridVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, grid.AsSpan(), BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 8, (void*)0);
        gridEbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, gridEbo);
        gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, indices.ToArray().AsSpan(), BufferUsageARB.StaticDraw);
        gl.BindVertexArray(0);
    }

    /// <summary>Width of the band at the fine window's border where it fades into the coarse grid.</summary>
    static float BandOf(HeightWindow w) => Math.Clamp((w.Columns - 1) * w.Spacing * 0.1f, w.Spacing * 4, 3000);

    uint HeightTexture(ushort[] raw, int width, int height) => HeightTexture(raw, width, height, upload: true);

    uint HeightTexture(ushort[]? raw, int width, int height, bool upload)
    {
        uint id = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, id);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 2);
        if (upload) gl.TexImage2D<ushort>(TextureTarget.Texture2D, 0, InternalFormat.R16, (uint)width, (uint)height, 0, PixelFormat.Red, PixelType.UnsignedShort, raw!.AsSpan());
        else gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R16, (uint)width, (uint)height, 0, PixelFormat.Red, PixelType.UnsignedShort, (void*)0);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        return id;
    }

    public int LevelCount => quadtree.LevelCount;
    public double FinestSpacing => quadtree.Spacing(0);
    public int DrawnChunks { get; private set; }
    public long DrawnTriangles { get; private set; }

    /// <summary>Starts counting drawn nodes and triangles for a new frame (several depth slices add up).</summary>
    public void BeginFrame()
    {
        (DrawnChunks, DrawnTriangles) = (0, 0);
        frameNumber++;
        // A replaced window is deleted a few frames after the swap: deleting a texture the GPU still has queued draws for makes the driver wait for them.
        for (int i = retired.Count - 1; i >= 0; i--)
            if (frameNumber - retired[i].Frame >= 4) { gl.DeleteTexture(retired[i].Texture); retired.RemoveAt(i); }
    }

    int frameNumber;
    readonly List<(uint Texture, int Frame)> retired = [];

    /// <summary>Uses biome textures and land maps from now on (null: untextured).</summary>
    public void SetTextures(TerrainTextures? t) => textures = t;

    /// <summary>The textures the TERRAIN-mode meshes' biome rows come from (null: none, every row -1): a caller keeping
    /// <see cref="FeatureBiomeRowAny"/> per placement computes it again when this object changes.</summary>
    public object? FeatureBiomes => textures;

    /// <summary>The biome row <see cref="DrawMeshes"/> gives a placement at (x, z) in colour: the biome map's row there when resident, else -1.</summary>
    public int FeatureBiomeRow(float x, float z) => textures?.FeatureBiomeRow(x, z) ?? -1;

    /// <summary>The biome map's row at (x, z) whatever its residency (-1: none); <see cref="FeatureBiomeRow"/> once <see cref="FeatureResidentBiomes"/> has its bit.</summary>
    public int FeatureBiomeRowAny(float x, float z) => textures?.FeatureBiomeRowAny(x, z) ?? -1;

    /// <summary>A bit per resident biome row (row r: word r / 32, bit r % 32), as <see cref="FeatureBiomeRow"/> tests them now.</summary>
    public void FeatureResidentBiomes(Span<uint> bits)
    {
        if (textures is null) bits.Clear();
        else textures.ResidentBiomeBits(bits);
    }

    /// <summary>The window the fine height texture currently holds.</summary>
    public HeightWindow Fine => fine;

    /// <summary>Bounds of a window against the quadtree's cell grid; safe on another thread.</summary>
    public TerrainHeightBounds.Patch MeasureBounds(HeightWindow w) => bounds.Measure(w);

    /// <summary>
    /// Replaces the fine window: a new texture is filled in slabs through <paramref name="uploads"/> while the old
    /// one stays in use, then swapped in (so no frame waits for the whole upload).
    /// </summary>
    public void BeginFineUpload(HeightWindow window, TerrainHeightBounds.Patch patch, UploadQueue uploads, Action? done = null)
    {
        const int slabRows = 192;
        uint texture = 0;
        uploads.Add(() => texture = HeightTexture(null, window.Columns, window.Rows, upload: false), "height texture");
        for (int row = 0; row < window.Rows; row += slabRows)
        {
            int r0 = row, n = Math.Min(slabRows, window.Rows - row);
            uploads.Add(() =>
            {
                gl.BindTexture(TextureTarget.Texture2D, texture);
                gl.PixelStore(PixelStoreParameter.UnpackAlignment, 2);
                gl.TexSubImage2D<ushort>(TextureTarget.Texture2D, 0, 0, r0, (uint)window.Columns, (uint)n, PixelFormat.Red, PixelType.UnsignedShort, window.Raw.AsSpan(r0 * window.Columns, n * window.Columns));
                gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
            }, "height slab");
        }
        uploads.Add(() =>
        {
            retired.Add((fineTexture, frameNumber));
            fineTexture = texture;
            fine = window;
            fineBand = BandOf(window);
            bounds.Apply(patch);
            done?.Invoke();
        }, "height swap");
    }

    /// <summary>Height at a world point from the same grids the shaders read (bilinear, blended at the region's edge).</summary>
    public float HeightAt(float x, float z)
    {
        float h = WorldLayout.HalfWorldSize;
        float c = Bilinear(coarse, coarseSize, coarseSize, x + h, z + h, WorldLayout.WorldSize / (float)(coarseSize - 1));
        var (x0, z0) = fine.WorldOf(0, 0);
        float x1 = (float)x0 + (fine.Columns - 1) * fine.Spacing, z1 = (float)z0 + (fine.Rows - 1) * fine.Spacing;
        float w = Math.Clamp(Math.Min(Math.Min(x - (float)x0, x1 - x), Math.Min(z - (float)z0, z1 - z)) / fineBand, 0, 1);
        if (w <= 0) return c;
        return c + (Bilinear(fine.Raw, fine.Columns, fine.Rows, x - (float)x0, z - (float)z0, fine.Spacing) - c) * w;
    }

    static float Bilinear(ushort[] raw, int cols, int rows, float x, float z, float spacing)
    {
        float fx = Math.Clamp(x / spacing, 0, cols - 1), fz = Math.Clamp(z / spacing, 0, rows - 1);
        int i = Math.Clamp((int)fx, 0, Math.Max(cols - 2, 0)), j = Math.Clamp((int)fz, 0, Math.Max(rows - 2, 0));
        int i1 = Math.Min(i + 1, cols - 1), j1 = Math.Min(j + 1, rows - 1);
        float tx = fx - i, tz = fz - j;
        float v = raw[j * cols + i] * (1 - tx) * (1 - tz) + raw[j * cols + i1] * tx * (1 - tz) + raw[j1 * cols + i] * (1 - tx) * tz + raw[j1 * cols + i1] * tx * tz;
        return v * (WorldLayout.MaxHeight / ushort.MaxValue);
    }

    /// <summary>Draws the terrain; nodes whose highest point is under <paramref name="cullBelow"/> are skipped (the reflection pass clips everything below the water). <paramref name="secondary"/>: the reflection's call, with its own quadtree for its own LOD distance.</summary>
    public void Draw(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, WorldLighting light, float cullBelow = float.NegativeInfinity, bool secondary = false)
    {
        long timing = StepTiming.Now();
        current = secondary ? spare : quadtree;
        if (current.SetRanges(options.TerrainLod) && LodLog)
            Console.WriteLine($"terrain   lod {(secondary ? "reflection" : "main")} {options.TerrainLod}: ranges {string.Join(" ", current.Ranges.SkipLast(1).Select(r => r.ToString("0")))}");
        frame = new Frame(viewProjection, eye, options, light);
        current.Select(eye, bounds, (min, max) => max.Y >= cullBelow && WorldCamera.Intersects(frustum, min, max), nodes);
        if (LodLog && !secondary && (frameNumber < 4 || frameNumber % 60 == 1))
            Console.WriteLine($"terrain   frame {frameNumber} nodes per level (whole + quarters): {string.Join(" ", Enumerable.Range(0, current.LevelCount).Select(l => $"{nodes.Count(n => n.Level == l && n.Quadrant < 0)}+{nodes.Count(n => n.Level == l && n.Quadrant >= 0)}"))}");

        gl.Enable(EnableCap.DepthTest);
        gl.Enable(EnableCap.CullFace);
        gl.CullFace(TriangleFace.Back);
        gl.FrontFace(FrontFaceDirection.Ccw);
        if (options.Wireframe != 2) DrawPatches();
        if (options.Wireframe != 0)
        {
            // The debug outline stays on the GL path (its own program and GL's line state).
            Apply(patchProgram, heightNormals: true);
            gl.BindVertexArray(gridVao);
            gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
            gl.Enable(EnableCap.PolygonOffsetLine);
            gl.PolygonOffset(-1, -1);
            DrawNodes(wire: true);
            gl.Disable(EnableCap.PolygonOffsetLine);
            gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
            gl.BindVertexArray(0);
        }
        StepTiming.Add(StepTiming.PatchColour, timing, nodes.Count);
    }


    /// <summary>The debug outline (<c>Wireframe</c> 1 and 2): the GL path, as before the port.</summary>
    void DrawNodes(bool wire)
    {
        gl.Uniform1(U(patchProgram, "uWireframe"), wire ? 1 : 0);
        int uNode = U(patchProgram, "uNode"), uMorph = U(patchProgram, "uMorph");
        foreach (var n in nodes)
        {
            gl.Uniform4(uNode, (float)n.X0, (float)n.Z0, (float)n.Size, GridCells);
            float start = current.MorphStart[n.Level], end = current.MorphEnd[n.Level];
            gl.Uniform2(uMorph, start == float.MaxValue ? 1e31f : start, end == float.MaxValue ? 2e31f : end);
            int part = n.Quadrant + 1;
            gl.DrawElements(PrimitiveType.Triangles, (uint)indexCounts[part], DrawElementsType.UnsignedInt, (void*)indexOffsets[part]);
        }
    }

    // ---- the native model (docs/renderer-native.md 7.1, wave 3b agent B: step O) ----
    // Native programs (TerrainShaders' native variants: the same maths; the camera in ViewConstants, the terrain's values and the bindless
    // indices of its textures in TerrainConstants at set 2, a patch's node in push constants), recorded into the pass VkGl has open. Per
    // segment: the frame set (NativeFrame), the terrain set, the dynamic state; per draw only commands (a patch: a push and a draw; a mesh
    // group: its pipeline, buffers and draw, kept per mesh while the vertex-array stamp holds).

    readonly NativeFrame nativeFrame;
    readonly Silk.NET.Vulkan.DescriptorSetLayout constantsLayout;
    readonly TerrainProgram patchColour, patchDepth, meshColour, meshDepth;
    readonly ulong uniformAlign;
    readonly uint standIn2D, standInArray, standInUInt;

    static SamplerInfo StandInInfo(bool arrayed, ScalarKind kind) => new("", 0, 0, SamplerDimension.Dim2D, arrayed, false, false, kind, 0);

    /// <summary>
    /// A native program of the terrain with its vertex inputs from the reflection: the layout and buffers a GL vertex array feeds it, as VkGl
    /// feeds a GL program (<see cref="LegacyProgram.VertexLayout"/>: a disabled attribute reads GL's constant through a stride-0 binding).
    /// </summary>
    sealed class TerrainProgram : IDisposable
    {
        readonly GpuContext ctx;
        public readonly ShaderProgram P;
        readonly int[] locations;
        readonly ScalarKind[] kinds;
        /// <summary>The program's own input locations (below the placements' rows): 0 .. Own − 1.</summary>
        public readonly int Own;
        VertexLayout? last;

        public TerrainProgram(GpuContext ctx, Silk.NET.Vulkan.DescriptorSetLayout[] sets, string vertex, string fragment, string name)
        {
            this.ctx = ctx;
            P = ctx.Shaders.Native(vertex, fragment, name, sets, NativeShaders.PushBytes);
            var inputs = P.VertexReflection!.Inputs;
            locations = [.. inputs.SelectMany(i => Enumerable.Range(i.Location, i.Slots))];
            kinds = [.. inputs.SelectMany(i => Enumerable.Repeat(i.Kind, i.Slots))];
            Own = locations.Where(l => l < TerrainShaders.MeshInstanceLocation).DefaultIfEmpty(-1).Max() + 1;
            if (Own > 2) throw new NotSupportedException($"{name}: more than two own vertex inputs");
        }

        public Silk.NET.Vulkan.PipelineLayout Layout => P.Layout;

        public VertexLayout VertexLayout(ReadOnlySpan<LegacyProgram.Attribute?> byLocation)
        {
            Span<VertexInput> inputs = stackalloc VertexInput[locations.Length];
            for (int i = 0; i < inputs.Length; i++)
            {
                int loc = locations[i];
                inputs[i] = loc < byLocation.Length && byLocation[loc] is { } a
                    ? new VertexInput((uint)loc, a.Format, a.Stride, a.PerInstance)
                    : new VertexInput((uint)loc, GlConventions.DummyVertexFormat(kinds[i]), 0, false);
            }
            if (last is { } l && inputs.SequenceEqual(l.Inputs)) return l;
            return last = new VertexLayout(inputs.ToArray());
        }

        /// <summary>Locations 0 .. <see cref="Own"/> − 1 into <paramref name="result"/>: each attribute's buffer, GL's constant where none.</summary>
        public void Buffers(ReadOnlySpan<LegacyProgram.Attribute?> byLocation, Span<BufferBinding> result)
        {
            for (int loc = 0; loc < Own; loc++)
            {
                int input = Array.IndexOf(locations, loc);
                result[loc] = loc < byLocation.Length && byLocation[loc] is { } a
                    ? a.Buffer
                    : new BufferBinding(ctx.Defaults.DummyVertex.Buffer, GlConventions.DummyVertexOffset(input >= 0 ? kinds[input] : ScalarKind.Float));
            }
        }

        public void Dispose()
        {
            ctx.Pipelines.Forget(P);
            P.Dispose();
        }
    }

    // The terrain set (set 2): this segment's TerrainConstants in the frame's constants, bound with a dynamic offset into a set made once per
    // constants chunk (LegacyProgram's pattern for its default blocks).
    TerrainConstants constants, written;
    long writtenFrame = -1;
    Transient constantsSlice;
    readonly Dictionary<ulong, Silk.NET.Vulkan.DescriptorSet> constantSets = [];
    ulong lastSetKey;
    Silk.NET.Vulkan.DescriptorSet lastSet;
    Vector4 lastRegion;
    Vector2 lastCellGrid;

    /// <summary>The bindless index of a GL texture of the terrain's (<see cref="IGlInterop.Bindless"/>: what VkGl would sample now), or the
    /// stand-in of <paramref name="kind"/>'s array when there is none or it is not of that kind (no storage yet: VkGl's float 2D stand-in).</summary>
    uint Index(IGlInterop interop, uint glTexture, BindlessKind kind, uint standIn)
    {
        if (glTexture == 0) return standIn;
        var h = interop.Bindless(glTexture);
        return h.Kind == kind ? h.Index : standIn;
    }

    /// <summary>
    /// <see cref="constants"/> for the next segment: <see cref="BindHeights"/>' values and the height textures; with <paramref name="material"/>
    /// also <see cref="Apply"/>'s (<paramref name="patches"/>: the height field's normals, no feature; else <c>uFeature</c> 1) and the material
    /// textures. A value <see cref="Apply"/> sets only with textures (<c>uRegion</c>, <c>uCellGrid</c>) keeps its last one without, as a GL
    /// program's uniform does. Before the segment (the bindless exports are IGl-side calls).
    /// </summary>
    void PrepareConstants(bool material, bool patches)
    {
        var interop = gpu.Interop!;
        var c = new TerrainConstants
        {
            CoarseRect = CoarseRect, CoarseCells = new Vector2(coarseSize - 1f, coarseSize - 1f), FineRect = FineRect,
            FineCells = new Vector2(fine.Columns - 1f, fine.Rows - 1f), FineBand = fineBand, HasFine = 1,
            HeightCoarse = Index(interop, coarseTexture, BindlessKind.Texture2D, standIn2D),
            HeightFine = Index(interop, fineTexture, BindlessKind.Texture2D, standIn2D),
            Diffuse = standInArray, Normal = standInArray, Params = standIn2D, Cells = standInUInt, BlendMap = standIn2D, Overlay = standIn2D,
            Colour = standIn2D, Ground = standIn2D, WorldColour = standIn2D, Region = lastRegion, CellGrid = lastCellGrid,
        };
        if (material)
        {
            var (options, light) = (frame.Options, frame.Light);
            c.HeightNormals = patches ? 1u : 0u;
            c.Feature = patches ? 0u : 1u;
            c.FeatureBiome = -1;
            c.SunColour = light.SunColour;
            c.AmbientSky = light.AmbientSky;
            c.AmbientGround = light.AmbientGround;
            c.WaterHeight = options.Water ? WorldWater.Height : -1e6f;
            c.HalfWorld = (float)WorldLayout.HalfWorldSize;
            c.Debug = options.Debug;
            c.FarStart = options.MaterialDistance * 0.8f;
            c.FarEnd = options.MaterialDistance;
            var t = textures;
            bool textured = options.Textures && t is { HasBiomes: true };
            c.Textured = textured ? 1u : 0u;
            c.NormalMaps = textured && options.NormalMaps ? 1u : 0u;
            c.HasMaps = options.Textures && t is { MapState: 2 } ? 1u : 0u;
            c.MapState = t?.MapState ?? 0;
            c.HasGround = t is { HasGround: true } ? 1u : 0u;
            c.HasWorldColour = t is { HasWorldColour: true } ? 1u : 0u;
            if (t is not null)
            {
                c.Region = lastRegion = t.Region;
                c.CellGrid = lastCellGrid = new Vector2(t.CellsX, t.CellsZ);
                var ids = t.Ids;
                c.Diffuse = Index(interop, ids.Diffuse, BindlessKind.Texture2DArray, standInArray);
                c.Normal = Index(interop, ids.Normal, BindlessKind.Texture2DArray, standInArray);
                c.Params = Index(interop, ids.Params, BindlessKind.Texture2D, standIn2D);
                c.Cells = Index(interop, ids.Cells, BindlessKind.UTexture2D, standInUInt);
                c.BlendMap = Index(interop, ids.BlendMap, BindlessKind.Texture2D, standIn2D);
                c.Overlay = Index(interop, ids.Overlay, BindlessKind.Texture2D, standIn2D);
                c.Colour = Index(interop, ids.Colour, BindlessKind.Texture2D, standIn2D);
                c.Ground = Index(interop, ids.Ground, BindlessKind.Texture2D, standIn2D);
                c.WorldColour = Index(interop, ids.WorldColour, BindlessKind.Texture2D, standIn2D);
            }
        }
        constants = c;
    }

    /// <summary>The camera of the frame (<see cref="Draw"/>'s or <see cref="DrawDepth"/>'s) as <see cref="ViewConstants"/>.</summary>
    ViewConstants View()
    {
        var light = frame.Light;
        return new ViewConstants
        {
            ViewProjection = frame.ViewProjection, Eye = frame.Eye, LightDir = light.SunDirection, FogColour = light.FogColour, FogDistance = light.FogDistance,
        };
    }

    /// <summary>Once per segment, in Prepare (wave 4): <see cref="constants"/> into the frame's constants (again only when it changed within the
    /// frame), and the set and dynamic offset the segment's job binds as set 2.</summary>
    (Silk.NET.Vulkan.DescriptorSet Set, uint Offset) PrepareConstantsBinding()
    {
        var f = gpu.Frame;
        if (writtenFrame != f.Number || !System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<TerrainConstants>(in constants))
                .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<TerrainConstants>(in written))))
        {
            constantsSlice = f.Constants.Write<TerrainConstants>(new ReadOnlySpan<TerrainConstants>(in constants), uniformAlign);
            (written, writtenFrame) = (constants, f.Number);
            f.Stats.ConstantBytes += sizeof(TerrainConstants);
        }
        ulong key = constantsSlice.Handle.Handle;
        if (key != lastSetKey || lastSet.Handle == 0)
        {
            if (!constantSets.TryGetValue(key, out var made)) constantSets[key] = made = ConstantSet(constantsSlice.Handle);
            (lastSetKey, lastSet) = (key, made);
        }
        return (lastSet, (uint)constantsSlice.Offset);
    }

    Silk.NET.Vulkan.DescriptorSet ConstantSet(Silk.NET.Vulkan.Buffer buffer)
    {
        var set = gpu.AllocatePersistentSet(constantsLayout);
        var info = new Silk.NET.Vulkan.DescriptorBufferInfo(buffer, 0, (ulong)sizeof(TerrainConstants));
        var write = new Silk.NET.Vulkan.WriteDescriptorSet
        {
            SType = Silk.NET.Vulkan.StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0, DescriptorCount = 1,
            DescriptorType = Silk.NET.Vulkan.DescriptorType.UniformBufferDynamic, PBufferInfo = &info,
        };
        gpu.Device.Vk.UpdateDescriptorSets(gpu.Device.Device, 1, &write, 0, null);
        return set;
    }

    // ---- the terrain patches ----

    /// <summary>One selected node as its draw needs it (Prepare fills the list, Record walks it): the push constants (<c>uNode</c>,
    /// <c>uMorph</c>) and the index range (<c>Quadrant + 1</c>).</summary>
    struct PatchDraw
    {
        public TerrainPush Push;
        public int Part;
    }

    PatchDraw[] patchDraws = new PatchDraw[512];

    /// <summary>A patch program resolved for one pass state: the pipeline, the grid's vertex buffer and its index buffer.</summary>
    sealed class PatchPipeline
    {
        public SegmentPipeline Segment;
        public VertexArrayBindings Source = null!;
        public GraphicsPipeline Pipeline = null!;
        public BufferBinding Vertex;
        public BufferBinding Elements;
    }

    readonly List<PatchPipeline> patchPipelines = [];
    VertexArrayBindings? gridExport;
    long gridStamp = -1;

    /// <summary>
    /// Fills <see cref="patchDraws"/> from <see cref="nodes"/> (the tree's morph ranges, the index range per node) and counts them.
    /// </summary>
    void PreparePatches(TerrainQuadtree tree)
    {
        int n = nodes.Count;
        if (patchDraws.Length < n) patchDraws = new PatchDraw[Math.Max(n, patchDraws.Length * 2)];
        long triangles = 0;
        for (int i = 0; i < n; i++)
        {
            var node = nodes[i];
            float start = tree.MorphStart[node.Level], end = tree.MorphEnd[node.Level];
            int part = node.Quadrant + 1;
            patchDraws[i] = new PatchDraw
            {
                Push = new TerrainPush
                {
                    Node = new Vector4((float)node.X0, (float)node.Z0, (float)node.Size, GridCells),
                    Morph = new Vector2(start == float.MaxValue ? 1e31f : start, end == float.MaxValue ? 2e31f : end),
                },
                Part = part,
            };
            triangles += indexCounts[part] / 3;
        }
        patchTriangles = triangles;
    }

    long patchTriangles;

    /// <summary>The solid patches of <see cref="Draw"/>: the GL state the GL version left (the program, the units bound: the frame set reads the
    /// atmosphere's there), the constants, then one native segment in the pass VkGl has open.</summary>
    void DrawPatches()
    {
        PreparePatches(current);
        gl.UseProgram(patchProgram);
        SkyRenderer.Active?.BindUnits();
        BindHeightUnits();
        textures?.Bind();
        if (nodes.Count == 0) return;
        PrepareConstants(material: true, patches: true);
        RecordPatches(patchColour, "terrain", StepTiming.PatchColour);
        DrawnChunks += nodes.Count;
        DrawnTriangles += patchTriangles;
    }

    /// <summary>
    /// Records <see cref="patchDraws"/> into one native segment of the pass VkGl is drawing into (back faces culled as GL has it set): the
    /// dynamic state, the sets, the pipeline, the grid's vertex and index buffers once; per node its push constants and the draw of its index
    /// range.
    /// </summary>
    void RecordPatches(TerrainProgram p, string label, int timingKind)
    {
        long t0 = StepTiming.Now();
        // Prepare (wave 4, docs/renderer-native.md 6.2): the pass state as GL has it now, the pipeline, the sets, the draws, into a job.
        var interop = gpu.Interop!;
        var targets = interop.CurrentTargets();
        var state = interop.CurrentState();
        var r = ResolvePatches(p, interop, state, targets, label);
        var job = patchJobs.Rent();
        job.Owner = this;
        (job.Targets, job.State, job.Layout, job.Pipeline, job.Vertex, job.Elements) = (targets, state, p.Layout, r.Pipeline, r.Vertex, r.Elements);
        var view = View();
        job.Frame = nativeFrame.Prepare(in view);
        (job.ConstantSet, job.ConstantOffset) = PrepareConstantsBinding();
        int count = nodes.Count;
        if (job.Draws.Length < count) job.Draws = new PatchDraw[Math.Max(count, job.Draws.Length * 2)];
        patchDraws.AsSpan(0, count).CopyTo(job.Draws);
        job.Count = count;
        gpu.Record(label, job);
        StepTiming.Segment(timingKind, t0);
    }

    readonly JobPool<PatchJob> patchJobs = new();

    /// <summary>The patches of one view as prepared (<see cref="RecordPatches"/>): recorded on any thread, the same commands as before wave 4.</summary>
    sealed class PatchJob : RecordJob
    {
        public TerrainRenderer Owner = null!;
        public PassTargets Targets = null!;
        public DrawState State = null!;
        public Silk.NET.Vulkan.PipelineLayout Layout;
        public GraphicsPipeline Pipeline = null!;
        public BufferBinding Vertex, Elements;
        public FrameBinding Frame;
        public Silk.NET.Vulkan.DescriptorSet ConstantSet;
        public uint ConstantOffset;
        public PatchDraw[] Draws = new PatchDraw[256];
        public int Count;
        public override int Size => Count;

        public override void Record(CommandList cmd)
        {
            State.Record(cmd, Targets);
            NativeFrame.Record(cmd, Layout, in Frame);
            uint offset = ConstantOffset;
            cmd.BindSets(Layout, TerrainShaders.ConstantsSet, new ReadOnlySpan<Silk.NET.Vulkan.DescriptorSet>(in ConstantSet), new ReadOnlySpan<uint>(in offset));
            cmd.BindPipeline(Pipeline);
            cmd.BindVertexBuffers(0, new ReadOnlySpan<BufferBinding>(in Vertex));
            cmd.BindIndexBuffer(Elements, Silk.NET.Vulkan.IndexType.Uint32);
            var layout = Layout;
            const Silk.NET.Vulkan.ShaderStageFlags stages = Silk.NET.Vulkan.ShaderStageFlags.VertexBit | Silk.NET.Vulkan.ShaderStageFlags.FragmentBit;
            var (counts, firsts) = (Owner.indexCounts, Owner.firstIndices);
            for (int i = 0; i < Count; i++)
            {
                ref readonly var d = ref Draws[i];
                cmd.PushConstants(layout, stages, in d.Push);
                cmd.DrawIndexed((uint)counts[d.Part], 1, (uint)firsts[d.Part]);
            }
        }

        public override void Release() => Owner.patchJobs.Return(this);
    }

    /// <summary>The pipeline and bindings for <paramref name="p"/> in this pass state, kept while the grid's vertex-array export and the state
    /// stay the same (the reflection's multisampled target alternates with the scene's: a few entries). The export is fetched again only when
    /// <see cref="IGlInterop.VertexArrayStamp"/> moved.</summary>
    PatchPipeline ResolvePatches(TerrainProgram p, IGlInterop interop, DrawState state, PassTargets targets, string label)
    {
        if (gridExport is null || gridStamp != interop.VertexArrayStamp)
        {
            gridExport = interop.VertexArray(gridVao);
            gridStamp = interop.VertexArrayStamp;
        }
        var va = gridExport;
        var segment = new SegmentPipeline(p.P, targets.Formats, state.Blend, state.ColourMask, state.Polygon, state.AlphaToCoverage, state.DepthClamp);
        foreach (var e in patchPipelines)
            if (ReferenceEquals(e.Source, va) && e.Segment == segment) return e;
        if (patchPipelines.Count >= 16) patchPipelines.Clear();
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        va.Attributes.AsSpan().CopyTo(attributes);
        Span<BufferBinding> vertex = stackalloc BufferBinding[1];
        p.Buffers(attributes, vertex);
        var entry = new PatchPipeline
        {
            Segment = segment, Source = va,
            Pipeline = gpu.Pipelines.Get(state.Pipeline(p.P, p.VertexLayout(attributes), Silk.NET.Vulkan.PrimitiveTopology.TriangleList, targets.Formats, label)),
            Vertex = vertex[0], Elements = va.Elements,
        };
        patchPipelines.Add(entry);
        return entry;
    }

    /// <summary>Sets the frame's uniforms and textures on one of the two programs.</summary>
    void Apply(uint program, bool heightNormals)
    {
        gl.UseProgram(program);
        var (vp, eye, options, light) = (frame.ViewProjection, frame.Eye, frame.Options, frame.Light);
        WorldGl.Matrix(gl, U(program, "uViewProjection"), vp);
        gl.Uniform1(U(program, "uHeightNormals"), heightNormals ? 1 : 0);
        gl.Uniform1(U(program, "uFeature"), 0);
        gl.Uniform1(U(program, "uFeatureBiome"), -1);
        gl.Uniform1(U(program, "uWireframe"), 0);
        gl.Uniform3(U(program, "uEye"), eye.X, eye.Y, eye.Z);
        var s = light.SunDirection;
        gl.Uniform3(U(program, "uLightDir"), s.X, s.Y, s.Z);
        gl.Uniform3(U(program, "uSunColour"), light.SunColour.X, light.SunColour.Y, light.SunColour.Z);
        gl.Uniform3(U(program, "uAmbientSky"), light.AmbientSky.X, light.AmbientSky.Y, light.AmbientSky.Z);
        gl.Uniform3(U(program, "uAmbientGround"), light.AmbientGround.X, light.AmbientGround.Y, light.AmbientGround.Z);
        gl.Uniform3(U(program, "uFogColour"), light.FogColour.X, light.FogColour.Y, light.FogColour.Z);
        gl.Uniform1(U(program, "uFogDistance"), light.FogDistance);
        SkyRenderer.Active?.Apply(program);   // the atmosphere's uniforms (aerial perspective)
        gl.Uniform1(U(program, "uWaterHeight"), options.Water ? WorldWater.Height : -1e6f);
        gl.Uniform1(U(program, "uHalfWorld"), (float)WorldLayout.HalfWorldSize);
        gl.Uniform1(U(program, "uDebug"), options.Debug);
        gl.Uniform1(U(program, "uFarStart"), options.MaterialDistance * 0.8f);
        gl.Uniform1(U(program, "uFarEnd"), options.MaterialDistance);
        BindHeights(program);
        var t = textures;
        bool textured = options.Textures && t is { HasBiomes: true };
        gl.Uniform1(U(program, "uTextured"), textured ? 1 : 0);
        gl.Uniform1(U(program, "uNormalMaps"), textured && options.NormalMaps ? 1 : 0);
        gl.Uniform1(U(program, "uHasMaps"), options.Textures && t is { MapState: 2 } ? 1 : 0);
        gl.Uniform1(U(program, "uMapState"), t?.MapState ?? 0);
        gl.Uniform1(U(program, "uHasGround"), t is { HasGround: true } ? 1 : 0);
        gl.Uniform1(U(program, "uHasWorldColour"), t is { HasWorldColour: true } ? 1 : 0);
        string[] samplers = ["uDiffuse", "uNormal", "uParams", "uCells", "uBlendMap", "uOverlay", "uColour"];
        for (int i = 0; i < samplers.Length; i++) gl.Uniform1(U(program, samplers[i]), i);
        gl.Uniform1(U(program, "uGround"), TerrainShaders.GroundUnit);
        gl.Uniform1(U(program, "uWorldColour"), TerrainShaders.WorldColourUnit);
        if (t is not null)
        {
            t.Bind();
            gl.Uniform4(U(program, "uRegion"), t.Region.X, t.Region.Y, t.Region.Z, t.Region.W);
            gl.Uniform2(U(program, "uCellGrid"), (float)t.CellsX, t.CellsZ);
        }
    }

    /// <summary>Binds the height textures and sets the <see cref="TerrainShaders.HeightFunctions"/> uniforms of a program.</summary>
    public void BindHeights(uint program)
    {
        BindHeightUnits();
        gl.Uniform1(U(program, "uHeightCoarse"), TerrainShaders.HeightCoarseUnit);
        gl.Uniform1(U(program, "uHeightFine"), TerrainShaders.HeightFineUnit);
        var coarseRect = CoarseRect;
        gl.Uniform4(U(program, "uCoarseRect"), coarseRect.X, coarseRect.Y, coarseRect.Z, coarseRect.W);
        gl.Uniform2(U(program, "uCoarseCells"), coarseSize - 1f, coarseSize - 1f);
        var fineRect = FineRect;
        gl.Uniform4(U(program, "uFineRect"), fineRect.X, fineRect.Y, fineRect.Z, fineRect.W);
        gl.Uniform2(U(program, "uFineCells"), fine.Columns - 1f, fine.Rows - 1f);
        gl.Uniform1(U(program, "uFineBand"), fineBand);
        gl.Uniform1(U(program, "uHasFine"), 1);
    }

    /// <summary>The height textures on their units (GL state; <see cref="BindHeights"/> without its uniforms).</summary>
    void BindHeightUnits()
    {
        gl.ActiveTexture(TextureUnit.Texture0 + TerrainShaders.HeightCoarseUnit);
        gl.BindTexture(TextureTarget.Texture2D, coarseTexture);
        gl.ActiveTexture(TextureUnit.Texture0 + TerrainShaders.HeightFineUnit);
        gl.BindTexture(TextureTarget.Texture2D, fineTexture);
        gl.ActiveTexture(TextureUnit.Texture0);
    }

    static Vector4 CoarseRect => new(-WorldLayout.HalfWorldSize, -WorldLayout.HalfWorldSize, WorldLayout.HalfWorldSize, WorldLayout.HalfWorldSize);

    Vector4 FineRect
    {
        get
        {
            var (x0, z0) = fine.WorldOf(0, 0);
            return new((float)x0, (float)z0, (float)x0 + (fine.Columns - 1) * fine.Spacing, (float)z0 + (fine.Rows - 1) * fine.Spacing);
        }
    }

    /// <summary>
    /// The heights as frame globals (docs/renderer-native.md 4.3): the two textures and the <see cref="TerrainShaders.HeightFunctions"/>
    /// uniforms <see cref="BindHeights"/> sets, read when a consumer draws or applies its globals. No GL call.
    /// </summary>
    void PublishGlobals()
    {
        if (gpu is not { Interop: { } interop } ctx) return;
        var g = ctx.Globals;
        g.Publish("uHeightCoarse", () => interop.Sampled(coarseTexture, shadowSampler: false));
        g.Publish("uHeightFine", () => interop.Sampled(fineTexture, shadowSampler: false));
        g.PublishUniform("uCoarseRect", () => CoarseRect);
        g.PublishUniform("uCoarseCells", () => new Vector2(coarseSize - 1f, coarseSize - 1f));
        g.PublishUniform("uFineRect", () => FineRect);
        g.PublishUniform("uFineCells", () => new Vector2(fine.Columns - 1f, fine.Rows - 1f));
        g.PublishUniform("uFineBand", () => fineBand);
        g.PublishUniform("uHasFine", () => 1);
    }

    /// <summary>
    /// Draws other meshes with the terrain material (TERRAIN-mode map features), after <see cref="Draw"/> set the
    /// frame. Each item: a vertex array with position at attribute 0 and normal at 1, its index count, its transform.
    /// As the game's <c>Feature_Terrain_DX11</c> (docs/formats/foliage.md, "TERRAIN-mode meshes"): one biome per mesh,
    /// the one of <c>biomemap.png</c> at the mesh's origin, back faces culled. Drawn instanced (<see cref="GroupMeshes"/>); returns
    /// the number of draw calls.
    /// </summary>
    public int DrawMeshes(List<(uint Vao, int IndexCount, Matrix4x4 Model)> meshes, bool depth = false)
    {
        long t0 = MeshTiming > 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        int draws = DrawMeshesCore(meshes, depth);
        if (MeshTiming > 0)
        {
            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            int k = depth ? 1 : 0;
            if (meshSeen[k]++ < StepTiming.WarmCalls) return draws;   // the first calls: pipelines, registrations, cold caches
            meshTicks[k] += dt; meshCalls[k]++; meshDraws[k] += draws;
            if (MeshTiming == 2)
            {
                // The same call again at once, timed alone: the cost with this path's code and data warm in the caches (diagnostic only:
                // the pictures of such a run are not for comparison).
                long w0 = System.Diagnostics.Stopwatch.GetTimestamp();
                DrawMeshesCore(meshes, depth);
                meshWarmTicks[k] += System.Diagnostics.Stopwatch.GetTimestamp() - w0;
            }
        }
        return draws;
    }

    /// <summary>
    /// <c>MEITOU_MESH_TIMING=1</c>: the CPU time of <see cref="DrawMeshes"/> (colour and depth), printed when the renderer is disposed.
    /// <c>=2</c> also records each call a second time and reports that one as "warm" (docs/renderer-native.md 7.1, cold and warm cost).
    /// </summary>
    static readonly int MeshTiming = int.TryParse(Environment.GetEnvironmentVariable("MEITOU_MESH_TIMING"), out int mode) ? mode : 0;
    readonly long[] meshTicks = new long[2], meshWarmTicks = new long[2], meshCalls = new long[2], meshDraws = new long[2], meshSeen = new long[2];

    void ReportMeshTiming()
    {
        StepTiming.Report();
        if (MeshTiming == 0) return;
        double ms(long t) => t * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        string Kind(int k) =>
            FormattableString.Invariant($"{meshCalls[k]} calls {ms(meshTicks[k]):F2} ms ({(meshCalls[k] > 0 ? ms(meshTicks[k]) * 1000 / meshCalls[k] : 0):F1} us/call)") +
            (MeshTiming == 2 ? FormattableString.Invariant($" warm {(meshCalls[k] > 0 ? ms(meshWarmTicks[k]) * 1000 / meshCalls[k] : 0):F1} us/call") : "");
        Console.WriteLine($"terrain meshes  colour {Kind(0)}, depth {Kind(1)}, {meshDraws[0] + meshDraws[1]} draws ({meshDraws[0]} colour, {meshDraws[1]} depth)");
    }

    int DrawMeshesCore(List<(uint Vao, int IndexCount, Matrix4x4 Model)> meshes, bool depth)
    {
        if (depth) return DrawMeshesDepth(meshes);
        long t0 = StepTiming.Now();
        if (!GroupMeshes(meshes, biomes: true)) return 0;
        StepTiming.Group(StepTiming.MeshColour, t0);
        // GL state as Apply left it for the GL code that follows (the units bound: the frame set reads the atmosphere's there).
        SkyRenderer.Active?.BindUnits();
        BindHeightUnits();
        textures?.Bind();
        PrepareConstants(material: true, patches: false);
        int draws = DrawGroups(meshColour, Colour, "terrain meshes", StepTiming.MeshColour);
        StepTiming.Add(StepTiming.MeshColour, t0, draws);
        return draws;
    }

    /// <summary>This call's placements (each group's contiguous, <see cref="MeshGroup.Offset"/> instances in), in the frame's constants.</summary>
    Transient placements;
    const int Colour = 0, Depth = 1;
    readonly GpuContext gpu;

    // ---- depth only (the sun's shadow map, ShadowPass) ----
    bool depthReady;

    /// <summary>
    /// Draws the terrain's depth for a shadow cascade (<see cref="ShadowShaders.DepthFragment"/>, the caster bias): the main tree's
    /// patches chosen by the camera's <paramref name="eye"/> (so the casters' detail matches what the picture shows), culled by
    /// <paramref name="frustum"/>. Sets the frame's matrix for <see cref="DrawMeshes"/> with <c>depth</c>.
    /// </summary>
    public void DrawDepth(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options)
    {
        long timing = StepTiming.Now();
        depthReady = true;
        if (quadtree.SetRanges(options.TerrainLod) && LodLog) Console.WriteLine($"terrain   lod main {options.TerrainLod}: ranges {string.Join(" ", quadtree.Ranges.SkipLast(1).Select(r => r.ToString("0")))}");
        frame = new Frame(viewProjection, eye, options, frame.Light);
        quadtree.Select(eye, bounds, (min, max) => WorldCamera.Intersects(frustum, min, max), nodes);
        PreparePatches(quadtree);
        BindHeightUnits();
        gl.Enable(EnableCap.CullFace);
        gl.CullFace(TriangleFace.Back);
        gl.FrontFace(FrontFaceDirection.Ccw);
        if (nodes.Count > 0)
        {
            PrepareConstants(material: false, patches: true);
            RecordPatches(patchDepth, "terrain depth", StepTiming.PatchDepth);
            DepthTriangles += patchTriangles;
        }
        StepTiming.Add(StepTiming.PatchDepth, timing, nodes.Count);
    }

    /// <summary>Terrain triangles the depth draws have drawn since the counter was last reset (by the caller).</summary>
    public long DepthTriangles { get; set; }

    int DrawMeshesDepth(List<(uint Vao, int IndexCount, Matrix4x4 Model)> meshes)
    {
        long t0 = StepTiming.Now();
        if (!depthReady || !GroupMeshes(meshes, biomes: false)) return 0;   // the frame's matrix is the cascade's once DrawDepth has run
        StepTiming.Group(StepTiming.MeshDepth, t0);
        PrepareConstants(material: false, patches: false);
        int draws = DrawGroups(meshDepth, Depth, "terrain mesh depth", StepTiming.MeshDepth);
        StepTiming.Add(StepTiming.MeshDepth, t0, draws);
        return draws;
    }

    // ---- the TERRAIN-mode meshes, instanced ----
    // One instanced draw per (vertex array, index count, winding) instead of one draw per placement (a draw costs ~4 µs of CPU
    // through VkGl, and a forest has hundreds of TERRAIN-mode rocks on screen and thousands in a shadow cascade). The placements keep
    // their order within a group; the groups follow the first placement of each. The pictures are the same as with one draw each
    // (docs/viewer.md, "Shadow pass cost"): the shaders do the uniform form's arithmetic, a depth-only pass keeps the nearest fragment
    // whatever the order, and in colour only exactly equal depths of two different placements could tell the order apart.

    sealed class MeshGroup
    {
        public uint Vao;
        public int IndexCount, Count, Offset, Index;
        public bool Mirrored;
        /// <summary>What a native draw of this mesh needs, per program: kept while the vertex-array stamp holds and the export is the same.
        /// Inline (no object of its own), so a draw reads what grouping has just touched.</summary>
        public NativeMesh ColourNative, DepthNative;
        public ref NativeMesh Native(int kind) => ref kind == Colour ? ref ColourNative : ref DepthNative;
    }

    /// <summary>The pipeline state a segment's draws share (everything of <see cref="GraphicsPipelineDesc"/> but the vertex layout).</summary>
    readonly record struct SegmentPipeline(ShaderProgram Program, AttachmentFormats Formats, BlendState Blend, Silk.NET.Vulkan.ColorComponentFlags Mask,
        Silk.NET.Vulkan.PolygonMode Polygon, bool AlphaToCoverage, bool DepthClamp);

    /// <summary>A mesh's own vertex buffers (locations 0 and 1: position, normal), bound with one call.</summary>
    [System.Runtime.CompilerServices.InlineArray(2)]
    struct OwnVertices { BufferBinding first; }

    /// <summary>A mesh resolved for one program: the export it came from and the <see cref="IGlInterop.VertexArrayStamp"/> it was current at,
    /// its vertex layout and own vertex buffers (locations 0 up to the placements), its indices, and its pipelines for the last two segment
    /// states (the reflection's multisampled target alternates with the scene's).</summary>
    struct NativeMesh
    {
        public VertexArrayBindings? Source;
        public long Stamp;
        public VertexLayout? Layout;
        public int VertexCount;
        public OwnVertices Vertices;
        public BufferBinding Elements;
        public int SegA, SegB;
        public GraphicsPipeline? PipeA, PipeB;
    }

    // Per program kind: the last segment state and its number; numbers are stable per state (a mesh compares one int per draw).
    readonly Dictionary<(int Kind, SegmentPipeline State), int> segmentByState = [];
    readonly SegmentPipeline[] lastSegment = new SegmentPipeline[2];
    readonly int[] segmentIds = new int[2];
    int segmentCount;

    int SegmentId(int kind, ShaderProgram p, PassTargets t, DrawState s)
    {
        var segment = new SegmentPipeline(p, t.Formats, s.Blend, s.ColourMask, s.Polygon, s.AlphaToCoverage, s.DepthClamp);
        if (segmentIds[kind] != 0 && segment == lastSegment[kind]) return segmentIds[kind];
        if (!segmentByState.TryGetValue((kind, segment), out int id)) segmentByState[(kind, segment)] = id = ++segmentCount;
        lastSegment[kind] = segment;
        return segmentIds[kind] = id;
    }

    readonly Dictionary<(uint, int, bool), MeshGroup> meshGroups = [];
    readonly List<MeshGroup> meshGroupList = [];

    /// <summary>
    /// Sorts the placements into <see cref="meshGroupList"/> and writes them into this frame's constants (<see cref="placements"/>; each one's
    /// matrix as its four rows; with <paramref name="biomes"/> row 0's w, which only feeds the position's unused w, carries the biome row).
    /// False when there is none.
    /// </summary>
    bool GroupMeshes(List<(uint Vao, int IndexCount, Matrix4x4 Model)> meshes, bool biomes)
    {
        foreach (var g in meshGroupList) g.Count = 0;
        meshGroupList.Clear();
        if (meshGroups.Count > 4096) meshGroups.Clear();   // vertex arrays come and go with streaming
        var items = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(meshes);
        if (groupOf.Length < items.Length) groupOf = new int[Math.Max(items.Length, groupOf.Length * 2)];
        // Pass 1: each placement's group (the callers list a mesh's placements in runs, so the last group usually answers without a lookup).
        MeshGroup? last = null;
        for (int i = 0; i < items.Length; i++)
        {
            ref readonly var item = ref items[i];
            bool mirrored = item.Model.GetDeterminant() < 0;
            var g = last;
            if (g is null || g.Vao != item.Vao || g.IndexCount != item.IndexCount || g.Mirrored != mirrored)
            {
                var key = (item.Vao, item.IndexCount, mirrored);
                if (!meshGroups.TryGetValue(key, out g)) meshGroups[key] = g = new MeshGroup { Vao = item.Vao, IndexCount = item.IndexCount, Mirrored = mirrored };
                if (g.Count == 0) { g.Index = meshGroupList.Count; meshGroupList.Add(g); }
                last = g;
            }
            g.Count++;
            groupOf[i] = g.Index;
        }
        if (meshGroupList.Count == 0) return false;
        int total = 0;
        if (cursors.Length < meshGroupList.Count) cursors = new int[Math.Max(meshGroupList.Count, cursors.Length * 2)];
        for (int k = 0; k < meshGroupList.Count; k++) { var g = meshGroupList[k]; cursors[k] = g.Offset = total; total += g.Count; }
        // Pass 2: each placement straight into its group's place in the frame's (write-combined) constants, one 64-byte line each.
        placements = gpu.Frame.Constants.Allocate((ulong)total * 64, 64);
        var target = (Matrix4x4*)placements.Pointer;
        var t = biomes ? textures : null;
        for (int i = 0; i < items.Length; i++)
        {
            var placed = items[i].Model;
            // Not resident yet (or no textures): -1, blend the biomes as the terrain does.
            if (biomes) placed.M14 = t?.FeatureBiomeRow(placed.M41, placed.M43) ?? -1;
            target[cursors[groupOf[i]]++] = placed;
        }
        return true;
    }

    int[] groupOf = new int[1024], cursors = new int[64];
    /// <summary>
    /// Draws <see cref="meshGroupList"/> natively with <paramref name="p"/> inside the pass VkGl is drawing (its targets and GL's state at this
    /// point), back faces culled, a mirroring placement turning the winding round; the placements from this frame's constants. Leaves GL's
    /// state as the GL version did (culling off with back faces selected, counter-clockwise, no vertex array).
    /// <para>
    /// The step-O hot path (docs/renderer-native.md 7.5): one segment inside VkGl's pass; what all draws share once (dynamic state, the frame
    /// and terrain sets, the placements at locations 7 to 10); per draw only commands, from what each mesh keeps (<see cref="NativeMesh"/>):
    /// the pipeline, the mesh's own vertex buffers, the front face when it flips, the indices, and the draw, whose firstInstance reaches the
    /// group's placements. The vertex array is exported again only when the stamp moved.
    /// </para>
    /// </summary>
    int DrawGroups(TerrainProgram p, int kind, string label, int timingKind)
    {
        long t0 = StepTiming.Now();
        var job = OpenMeshSegment(p, kind, label, placements.Handle, placements.Offset, out var s);
        foreach (var g in meshGroupList)
        {
            ref var n = ref g.Native(kind);
            if (n.Stamp != s.Stamp) Current(ref n, s.Interop, g.Vao, p, s.Stamp);
            var pipeline = n.SegA == s.Segment ? n.PipeA! : PipelineFor(ref n, p, s.Segment, s.State, s.Targets.Formats, label);
            job.Add(new MeshJob.Draw
            {
                Pipeline = pipeline, Front = g.Mirrored ? s.Cw : s.Ccw, Vertices = n.Vertices, VertexCount = n.VertexCount, Elements = n.Elements,
                IndexCount = (uint)g.IndexCount, Instances = (uint)g.Count, FirstInstance = (uint)g.Offset,
            });
        }
        CloseMeshSegment(job, label, timingKind, t0);
        return meshGroupList.Count;
    }

    /// <summary>What the draws of a mesh segment share (<see cref="OpenMeshSegment"/>).</summary>
    struct MeshSegment
    {
        public IGlInterop Interop;
        public PassTargets Targets;
        public DrawState State;
        public int Segment;
        public long Stamp;
        public Silk.NET.Vulkan.FrontFace Ccw, Cw;
    }

    /// <summary>Opens a native segment for TERRAIN-mode meshes inside the pass VkGl is drawing: dynamic state (back faces culled,
    /// counter-clockwise), the frame and terrain sets, and the placements' rows from <paramref name="rows"/> at locations 7 to 10.</summary>
    MeshJob OpenMeshSegment(TerrainProgram p, int kind, string label, Silk.NET.Vulkan.Buffer rows, ulong rowsOffset, out MeshSegment s)
    {
        // Prepare (wave 4, docs/renderer-native.md 6.2): what the segment's commands need, into a job recorded by CloseMeshSegment.
        var interop = gpu.Interop!;
        var targets = interop.CurrentTargets();
        var state = interop.CurrentState();
        s = new MeshSegment
        {
            Interop = interop, Targets = targets, State = state, Segment = SegmentId(kind, p.P, targets, state), Stamp = interop.VertexArrayStamp,
            Ccw = GlConventions.FrontFace(FrontFaceDirection.Ccw), Cw = GlConventions.FrontFace(FrontFaceDirection.CW),
        };
        var job = meshJobs.Rent();
        (job.Owner, job.Targets, job.State, job.Layout, job.Ccw, job.Count) = (this, targets, state, p.Layout, s.Ccw, 0);
        var view = View();
        job.Frame = nativeFrame.Prepare(in view);
        (job.ConstantSet, job.ConstantOffset) = PrepareConstantsBinding();
        (job.Rows, job.RowsOffset) = (rows, rowsOffset);
        return job;
    }

    readonly JobPool<MeshJob> meshJobs = new();

    /// <summary>A TERRAIN-mode mesh segment as prepared (<see cref="DrawGroups"/>, <see cref="DrawIndirect"/>): recorded on any thread, the same
    /// commands as before wave 4.</summary>
    sealed class MeshJob : RecordJob
    {
        public struct Draw
        {
            public GraphicsPipeline Pipeline;
            public Silk.NET.Vulkan.FrontFace Front;
            public OwnVertices Vertices;
            public int VertexCount;
            public BufferBinding Elements;
            public uint IndexCount, Instances, FirstInstance;
            /// <summary>Indirect: the arguments' buffer (else a plain indexed draw).</summary>
            public Silk.NET.Vulkan.Buffer Args;
            public ulong ArgsOffset;
        }

        public TerrainRenderer Owner = null!;
        public PassTargets Targets = null!;
        public DrawState State = null!;
        public Silk.NET.Vulkan.PipelineLayout Layout;
        public Silk.NET.Vulkan.FrontFace Ccw;
        public FrameBinding Frame;
        public Silk.NET.Vulkan.DescriptorSet ConstantSet;
        public uint ConstantOffset;
        public Silk.NET.Vulkan.Buffer Rows;
        public ulong RowsOffset;
        public Draw[] Draws = new Draw[64];
        public int Count;
        public override int Size => Count;

        public void Add(in Draw d)
        {
            if (Count == Draws.Length) Array.Resize(ref Draws, Draws.Length * 2);
            Draws[Count++] = d;
        }

        public override void Record(CommandList cmd)
        {
            var (targets, state) = (Targets, State);
            cmd.SetViewport(targets.Viewport);
            cmd.SetScissor(targets.Scissor);
            cmd.SetRaster(Silk.NET.Vulkan.CullModeFlags.BackBit, Ccw);
            cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
            cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
            NativeFrame.Record(cmd, Layout, in Frame);
            uint offset = ConstantOffset;
            cmd.BindSets(Layout, TerrainShaders.ConstantsSet, new ReadOnlySpan<Silk.NET.Vulkan.DescriptorSet>(in ConstantSet), new ReadOnlySpan<uint>(in offset));
            // The placement's rows at locations the meshes' vertex arrays leave free (their other programs do not read them).
            Span<BufferBinding> bindings = stackalloc BufferBinding[4];
            for (int a = 0; a < 4; a++) bindings[a] = new BufferBinding(Rows, RowsOffset + (ulong)(16 * a));
            cmd.BindVertexBuffers(TerrainShaders.MeshInstanceLocation, bindings);
            var front = Ccw;
            for (int i = 0; i < Count; i++)
            {
                ref readonly var d = ref Draws[i];
                cmd.BindPipeline(d.Pipeline);
                if (d.Front != front) { cmd.SetFrontFace(d.Front); front = d.Front; }
                cmd.BindVertexBuffers(0, ((ReadOnlySpan<BufferBinding>)d.Vertices)[..d.VertexCount]);
                cmd.BindIndexBuffer(d.Elements, Silk.NET.Vulkan.IndexType.Uint32);
                if (d.Args.Handle != 0) cmd.DrawIndexedIndirect(d.Args, d.ArgsOffset, 1);
                else cmd.DrawIndexed(d.IndexCount, d.Instances, 0, 0, d.FirstInstance);
            }
        }

        public override void Release()
        {
            Array.Clear(Draws, 0, Count);   // no pipelines kept alive by a pooled job
            Count = 0;
            Owner.meshJobs.Return(this);
        }
    }

    /// <summary>Records a mesh segment and leaves GL's state as the GL version did (culling off with back faces selected, counter-clockwise, no vertex array).</summary>
    void CloseMeshSegment(MeshJob job, string label, int timingKind, long t0)
    {
        gpu.Record(label, job);
        StepTiming.Segment(timingKind, t0);
        gl.CullFace(TriangleFace.Back);
        gl.FrontFace(FrontFaceDirection.Ccw);
        gl.Disable(EnableCap.CullFace);
        gl.BindVertexArray(0);
    }

    /// <summary>A TERRAIN-mode mesh drawn indirect (<see cref="DrawMeshesIndirect"/>): the vertex array (position at 0, normal at 1), its index
    /// count, and whether its placements mirror (the winding turns round), as <see cref="DrawMeshes"/> groups them.</summary>
    public readonly record struct IndirectMesh(uint Vao, int IndexCount, bool Mirrored);

    /// <summary>
    /// <see cref="DrawMeshes"/> with placements and draw arguments made on the GPU (the foliage's GPU cull, docs/renderer-native.md 5.6.1): mesh
    /// <c>i</c> is drawn by the <c>VkDrawIndexedIndirectCommand</c> at <paramref name="argsOffset"/> + 20 i, its instances read from
    /// <paramref name="rows"/> (64 bytes each, the four rows of the matrix, row 0's w the biome row in colour, as <see cref="DrawMeshes"/>
    /// writes them) from <paramref name="rowsOffset"/>, with the argument's firstInstance. Same programs, state, and GL state left behind as
    /// <see cref="DrawMeshes"/>; the caller orders the meshes. Returns the number of draw calls.
    /// </summary>
    public int DrawMeshesIndirect(ReadOnlySpan<IndirectMesh> meshes, Silk.NET.Vulkan.Buffer rows, ulong rowsOffset, Silk.NET.Vulkan.Buffer args, ulong argsOffset, bool depth = false)
    {
        if (meshes.Length == 0) return 0;
        long t0 = MeshTiming > 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        int draws;
        long timing = StepTiming.Now();
        if (depth)
        {
            if (!depthReady) return 0;   // the frame's matrix is the cascade's once DrawDepth has run
            PrepareConstants(material: false, patches: false);
            draws = DrawIndirect(meshes, rows, rowsOffset, args, argsOffset, meshDepth, Depth, "terrain mesh depth", StepTiming.MeshDepth);
            StepTiming.Add(StepTiming.MeshDepth, timing, draws);
        }
        else
        {
            // GL state as Apply left it for the GL code that follows (the units bound: the frame set reads the atmosphere's there).
            SkyRenderer.Active?.BindUnits();
            BindHeightUnits();
            textures?.Bind();
            PrepareConstants(material: true, patches: false);
            draws = DrawIndirect(meshes, rows, rowsOffset, args, argsOffset, meshColour, Colour, "terrain meshes", StepTiming.MeshColour);
            StepTiming.Add(StepTiming.MeshColour, timing, draws);
        }
        if (MeshTiming > 0)
        {
            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            int k = depth ? 1 : 0;
            if (meshSeen[k]++ >= StepTiming.WarmCalls) { meshTicks[k] += dt; meshCalls[k]++; meshDraws[k] += draws; }
        }
        return draws;
    }

    int DrawIndirect(ReadOnlySpan<IndirectMesh> meshes, Silk.NET.Vulkan.Buffer rows, ulong rowsOffset, Silk.NET.Vulkan.Buffer args, ulong argsOffset, TerrainProgram p, int kind, string label, int timingKind)
    {
        long t0 = StepTiming.Now();
        var job = OpenMeshSegment(p, kind, label, rows, rowsOffset, out var s);
        if (meshGroups.Count > 4096) meshGroups.Clear();   // vertex arrays come and go with streaming
        for (int i = 0; i < meshes.Length; i++)
        {
            var m = meshes[i];
            // The mesh's native state lives with its group (shared with DrawMeshes' draws of the same mesh).
            var key = (m.Vao, m.IndexCount, m.Mirrored);
            if (!meshGroups.TryGetValue(key, out var g)) meshGroups[key] = g = new MeshGroup { Vao = m.Vao, IndexCount = m.IndexCount, Mirrored = m.Mirrored };
            ref var n = ref g.Native(kind);
            if (n.Stamp != s.Stamp) Current(ref n, s.Interop, g.Vao, p, s.Stamp);
            var pipeline = n.SegA == s.Segment ? n.PipeA! : PipelineFor(ref n, p, s.Segment, s.State, s.Targets.Formats, label);
            job.Add(new MeshJob.Draw
            {
                Pipeline = pipeline, Front = m.Mirrored ? s.Cw : s.Ccw, Vertices = n.Vertices, VertexCount = n.VertexCount, Elements = n.Elements,
                Args = args, ArgsOffset = argsOffset + (ulong)i * 20,
            });
        }
        CloseMeshSegment(job, label, timingKind, t0);
        return meshes.Length;
    }

    /// <summary>A mesh's state for <paramref name="p"/>, current at <paramref name="stamp"/>: the export fetched again (the stamp moved), the
    /// layout and own vertex buffers rebuilt only when the export is another object; the placements as per-instance rows of 64 bytes at
    /// locations 7 to 10.</summary>
    static void Current(ref NativeMesh n, IGlInterop interop, uint vao, TerrainProgram p, long stamp)
    {
        var va = interop.VertexArray(vao);
        n.Stamp = stamp;
        if (ReferenceEquals(n.Source, va)) return;
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        va.Attributes.AsSpan().CopyTo(attributes);
        for (int a = 0; a < 4; a++)
            attributes[TerrainShaders.MeshInstanceLocation + a] = new LegacyProgram.Attribute(default, Silk.NET.Vulkan.Format.R32G32B32A32Sfloat, 64, true);
        n.Layout = p.VertexLayout(attributes);
        n.VertexCount = p.Own;
        p.Buffers(attributes, n.Vertices);
        n.Elements = va.Elements;
        n.Source = va;
        n.SegA = n.SegB = 0;
        n.PipeA = n.PipeB = null;
    }

    GraphicsPipeline PipelineFor(ref NativeMesh n, TerrainProgram p, int segment, DrawState state, AttachmentFormats formats, string label)
    {
        if (n.SegA == segment) return n.PipeA!;
        if (n.SegB == segment) { (n.SegA, n.PipeA, n.SegB, n.PipeB) = (n.SegB, n.PipeB, n.SegA, n.PipeA); return n.PipeA!; }
        var pipeline = gpu.Pipelines.Get(state.Pipeline(p.P, n.Layout!, Silk.NET.Vulkan.PrimitiveTopology.TriangleList, formats, label));
        (n.SegB, n.PipeB) = (n.SegA, n.PipeA);
        (n.SegA, n.PipeA) = (segment, pipeline);
        return pipeline;
    }

    int U(uint program, string name)
    {
        if (!uniforms.TryGetValue((program, name), out int location)) uniforms[(program, name)] = location = gl.GetUniformLocation(program, name);
        return location;
    }

    public void Dispose()
    {
        ReportMeshTiming();
        gl.DeleteVertexArray(gridVao);
        gl.DeleteBuffer(gridVbo);
        gl.DeleteBuffer(gridEbo);
        gl.DeleteTexture(coarseTexture);
        gl.DeleteTexture(fineTexture);
        foreach (var r in retired) gl.DeleteTexture(r.Texture);
        gl.DeleteProgram(patchProgram);
        textures?.Dispose();
        foreach (var p in new[] { patchColour, patchDepth, meshColour, meshDepth }) p.Dispose();
        gpu.Bindless.Free(BindlessKind.Texture2D, standIn2D);
        gpu.Bindless.Free(BindlessKind.Texture2DArray, standInArray);
        gpu.Bindless.Free(BindlessKind.UTexture2D, standInUInt);
        nativeFrame.Dispose();
        var (vk, device, layout) = (gpu.Device.Vk, gpu.Device.Device, constantsLayout);
        gpu.Device.Frames.DeferDelete(() => vk.DestroyDescriptorSetLayout(device, layout, null));
    }
}

/// <summary>
/// <c>MEITOU_TERRAIN_TIMING=1</c>: CPU time of the terrain patches (colour, shadow depth), the TERRAIN-mode meshes (colour, depth), the Meitou
/// blocker map and the terrain shadow sweep, per call and per draw, printed when the terrain renderer is disposed (docs/renderer-native.md
/// 7.1, wave 3 agent B and wave 3b step O). For the native segments also the segment alone (<c>BeginNativeInPass</c> to <c>EndNative</c>)
/// and its draw loop alone, two stamps each; for the meshes the grouping of the placements. The first <see cref="WarmCalls"/> calls of the
/// patches and the meshes are left out.
/// </summary>
internal static class StepTiming
{
    public static readonly bool On = Environment.GetEnvironmentVariable("MEITOU_TERRAIN_TIMING") == "1";
    public const int PatchColour = 0, PatchDepth = 1, Blocker = 2, Sweep = 3, MeshColour = 4, MeshDepth = 5;
    const int Kinds = 6;
    static readonly string[] Names = ["patches colour", "patches depth", "blocker map", "terrain sweep", "meshes colour", "meshes depth"];
    static readonly long[] ticks = new long[Kinds], calls = new long[Kinds], draws = new long[Kinds], segment = new long[Kinds], loop = new long[Kinds], group = new long[Kinds], seen = new long[Kinds];
    /// <summary>Calls left out at the start per kind (pipeline creation, first registrations, cold caches of the first frames); none for the
    /// blocker map and the sweep (the sweep runs once).</summary>
    static readonly int[] warm = [WarmCalls, WarmCalls, 0, 0, WarmCalls, WarmCalls];
    public const int WarmCalls = 100;
    static bool Counted(int kind) => seen[kind] >= warm[kind];

    public static long Now() => On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

    public static void Add(int kind, long start, int count)
    {
        if (!On || seen[kind]++ < warm[kind]) return;
        ticks[kind] += System.Diagnostics.Stopwatch.GetTimestamp() - start;
        calls[kind]++;
        draws[kind] += count;
    }

    public static void Segment(int kind, long start) { if (On && Counted(kind)) segment[kind] += System.Diagnostics.Stopwatch.GetTimestamp() - start; }
    public static void Loop(int kind, long start) { if (On && Counted(kind)) loop[kind] += System.Diagnostics.Stopwatch.GetTimestamp() - start; }
    public static void Group(int kind, long start) { if (On && Counted(kind)) group[kind] += System.Diagnostics.Stopwatch.GetTimestamp() - start; }

    public static void Report()
    {
        if (!On) return;
        for (int k = 0; k < Kinds; k++)
        {
            if (calls[k] == 0) continue;
            double f = 1e6 / System.Diagnostics.Stopwatch.Frequency, us = ticks[k] * f;
            double perDraw(long t) => draws[k] > 0 ? t * f / draws[k] : 0;
            Console.WriteLine(FormattableString.Invariant(
                $"terrain timing  {Names[k],-15} {calls[k]} calls, {draws[k]} draws, {us / calls[k]:F1} us/call, {perDraw(ticks[k]):F2} us/draw") +
                (segment[k] > 0 ? FormattableString.Invariant($", segment {segment[k] * f / calls[k]:F1} us/call {perDraw(segment[k]):F3} us/draw, loop {perDraw(loop[k]):F3} us/draw") : "") +
                (group[k] > 0 ? FormattableString.Invariant($", grouping {group[k] * f / calls[k]:F1} us/call") : ""));
            ticks[k] = calls[k] = draws[k] = segment[k] = loop[k] = group[k] = seen[k] = 0;
        }
    }
}