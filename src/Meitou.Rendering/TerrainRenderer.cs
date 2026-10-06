using System.Numerics;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;

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
    readonly uint patchProgram, meshProgram;
    readonly Dictionary<(uint, string), int> uniforms = [];
    readonly uint gridVao, gridVbo, gridEbo, coarseTexture;
    uint fineTexture;
    readonly int[] indexOffsets = new int[5], indexCounts = new int[5];
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
        patchProgram = WorldGl.Program(gl, TerrainShaders.PatchVertex, TerrainShaders.Fragment);
        meshProgram = WorldGl.Program(gl, TerrainShaders.MeshVertex, TerrainShaders.MeshFragment);
        nativeMesh = NativeMeshProgram();   // with the GL program it replaces, so a draw never compiles
        nativePatch = NativePatchProgram();

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

    // ---- the terrain patches, native (docs/renderer-native.md 7.1, wave 3 agent B: step P, VkGl's SPIR-V and layout) ----

    /// <summary>One selected node as its draw needs it (Prepare fills the list, Record walks it): the <c>uNode</c> and <c>uMorph</c> values
    /// and the index range (<c>Quadrant + 1</c>).</summary>
    readonly record struct PatchDraw(float X, float Z, float Size, float MorphStart, float MorphEnd, int Part);

    PatchDraw[] patchDraws = new PatchDraw[512];
    LegacyProgram? nativePatch, nativePatchDepth;
    MeshUniforms patchMu;
    UniformHandle patchNode, patchMorph, depthPatchViewProjection, depthPatchEye, depthPatchNode, depthPatchMorph;
    UnitSamplers? patchUnitSamplers;


    /// <summary>A patch program resolved for one pass state: the pipeline, the grid's vertex buffer, and the five index ranges as bindings.</summary>
    sealed class PatchPipeline
    {
        public SegmentPipeline Segment;
        public VertexArrayBindings Source = null!;
        public GraphicsPipeline Pipeline = null!;
        public BufferBinding Vertex;
        public readonly BufferBinding[] Indices = new BufferBinding[5];
    }

    readonly List<PatchPipeline> patchPipelines = [];

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
            patchDraws[i] = new PatchDraw((float)node.X0, (float)node.Z0, (float)node.Size,
                start == float.MaxValue ? 1e31f : start, end == float.MaxValue ? 2e31f : end, part);
            triangles += indexCounts[part] / 3;
        }
        patchTriangles = triangles;
    }

    long patchTriangles;

    /// <summary>The solid patches of <see cref="Draw"/>: <see cref="Apply"/>'s uniforms on the native program, the GL state the GL version
    /// left, then one native segment in the pass VkGl has open.</summary>
    void DrawPatches()
    {
        var p = nativePatch!;
        PreparePatches(current);
        ApplyNative(p, in patchMu, patches: true);
        // GL state as Apply left it for the GL code that follows (the program in use, the units bound); no uniforms.
        gl.UseProgram(patchProgram);
        SkyRenderer.Active?.BindUnits();
        BindHeightUnits();
        textures?.Bind();
        BindUnitSamplers(p, ref patchUnitSamplers);
        if (nodes.Count == 0) return;
        RecordPatches(p, "terrain", patchNode, patchMorph);
        DrawnChunks += nodes.Count;
        DrawnTriangles += patchTriangles;
    }

    /// <summary>
    /// Records <see cref="patchDraws"/> into one native segment of the pass VkGl is drawing into (back faces culled as GL has it set): the
    /// pipeline, the grid's vertex buffer and the dynamic state once; per node its two uniforms, <see cref="LegacyProgram.Flush"/>, the index range
    /// (bound at its offset, as VkGl binds it) and the draw.
    /// </summary>
    void RecordPatches(LegacyProgram p, string label, UniformHandle uNode, UniformHandle uMorph)
    {
        var interop = gpu.Interop!;
        var cmd = interop.BeginNativeInPass(label);
        var targets = interop.CurrentTargets();
        var state = interop.CurrentState();
        var r = ResolvePatches(p, interop, state, targets, label);
        state.Record(cmd, targets);
        cmd.BindPipeline(r.Pipeline);
        cmd.BindVertexBuffers(0, new ReadOnlySpan<BufferBinding>(in r.Vertex));
        var indices = r.Indices;
        int count = nodes.Count;
        for (int i = 0; i < count; i++)
        {
            ref readonly var d = ref patchDraws[i];
            p.Set(uNode, d.X, d.Z, d.Size, GridCells);
            p.Set(uMorph, d.MorphStart, d.MorphEnd);
            p.Flush(cmd);
            cmd.BindIndexBuffer(indices[d.Part], Silk.NET.Vulkan.IndexType.Uint32);
            cmd.DrawIndexed((uint)indexCounts[d.Part]);
        }
        interop.EndNative(cmd);
    }

    /// <summary>The pipeline and bindings for <paramref name="p"/> in this pass state, kept while the grid's vertex-array export and the state
    /// stay the same (the reflection's multisampled target alternates with the scene's: a few entries).</summary>
    PatchPipeline ResolvePatches(LegacyProgram p, IGlInterop interop, DrawState state, PassTargets targets, string label)
    {
        var va = interop.VertexArray(gridVao);
        var segment = new SegmentPipeline(p, targets.Formats, state.Blend, state.ColourMask, state.Polygon, state.AlphaToCoverage, state.DepthClamp);
        foreach (var e in patchPipelines)
            if (ReferenceEquals(e.Source, va) && e.Segment == segment) return e;
        if (patchPipelines.Count >= 16) patchPipelines.Clear();
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        va.Attributes.AsSpan().CopyTo(attributes);
        var entry = new PatchPipeline
        {
            Segment = segment, Source = va,
            Pipeline = gpu.Pipelines.Get(state.Pipeline(p.Program, p.VertexLayout(attributes), Silk.NET.Vulkan.PrimitiveTopology.TriangleList, targets.Formats, label)),
            Vertex = p.VertexBuffers(attributes, 0, 1)[0],
        };
        for (int part = 0; part < 5; part++)
            entry.Indices[part] = new BufferBinding(va.Elements.Buffer, va.Elements.Offset + (ulong)indexOffsets[part]);
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
    readonly long[] meshTicks = new long[2], meshWarmTicks = new long[2], meshCalls = new long[2], meshDraws = new long[2];

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
        if (!GroupMeshes(meshes, biomes: true)) return 0;
        var p = nativeMesh!;
        ApplyNative(p, in mu);
        // GL state as Apply left it for the GL code that follows (the program in use, the units bound); no uniforms.
        gl.UseProgram(meshProgram);
        SkyRenderer.Active?.BindUnits();
        BindHeightUnits();
        textures?.Bind();
        BindUnitSamplers(p, ref meshUnitSamplers);
        return DrawGroups(p, Colour, "terrain meshes");
    }

    // ---- the native port of the TERRAIN-mode meshes (docs/renderer-native.md 7.1, step 7: step P, VkGl's SPIR-V and layout) ----

    readonly GpuContext gpu;
    LegacyProgram? nativeMesh, nativeDepth;
    MeshUniforms mu;
    UniformHandle depthViewProjection;
    /// <summary>This call's placements (each group's contiguous, <see cref="MeshGroup.Offset"/> instances in), in the frame's constants.</summary>
    Transient placements;
    const int Colour = 0, Depth = 1;

    /// <summary>The handles of what <see cref="Apply"/> sets, resolved once.</summary>
    readonly record struct MeshUniforms(UniformHandle ViewProjection, UniformHandle HeightNormals, UniformHandle Feature, UniformHandle FeatureBiome,
        UniformHandle Wireframe, UniformHandle Eye, UniformHandle LightDir, UniformHandle SunColour, UniformHandle AmbientSky, UniformHandle AmbientGround,
        UniformHandle FogColour, UniformHandle FogDistance, UniformHandle WaterHeight, UniformHandle HalfWorld, UniformHandle Debug, UniformHandle FarStart,
        UniformHandle FarEnd, UniformHandle Textured, UniformHandle NormalMaps, UniformHandle HasMaps, UniformHandle MapState, UniformHandle HasGround,
        UniformHandle HasWorldColour, UniformHandle Region, UniformHandle CellGrid);

    static MeshUniforms ResolveUniforms(LegacyProgram p) =>
        new(p.Uniform("uViewProjection"), p.Uniform("uHeightNormals"), p.Uniform("uFeature"), p.Uniform("uFeatureBiome"),
            p.Uniform("uWireframe"), p.Uniform("uEye"), p.Uniform("uLightDir"), p.Uniform("uSunColour"), p.Uniform("uAmbientSky"), p.Uniform("uAmbientGround"),
            p.Uniform("uFogColour"), p.Uniform("uFogDistance"), p.Uniform("uWaterHeight"), p.Uniform("uHalfWorld"), p.Uniform("uDebug"), p.Uniform("uFarStart"),
            p.Uniform("uFarEnd"), p.Uniform("uTextured"), p.Uniform("uNormalMaps"), p.Uniform("uHasMaps"), p.Uniform("uMapState"), p.Uniform("uHasGround"),
            p.Uniform("uHasWorldColour"), p.Uniform("uRegion"), p.Uniform("uCellGrid"));

    LegacyProgram NativeMeshProgram()
    {
        var p = LegacyProgram.Create(gpu, TerrainShaders.MeshVertex, TerrainShaders.MeshFragment, "terrain meshes");
        mu = ResolveUniforms(p);
        return p;
    }

    /// <summary>The patch program (<see cref="TerrainShaders.PatchVertex"/>, the colour fragment), made with the GL program it replaces (the
    /// outline still uses that one), so a draw never compiles.</summary>
    LegacyProgram NativePatchProgram()
    {
        var p = LegacyProgram.Create(gpu, TerrainShaders.PatchVertex, TerrainShaders.Fragment, "terrain");
        patchMu = ResolveUniforms(p);
        (patchNode, patchMorph) = (p.Uniform("uNode"), p.Uniform("uMorph"));
        return p;
    }

    /// <summary><see cref="Apply"/>'s uniforms for the meshes (then <c>uFeature</c> 1) or the patches (<paramref name="patches"/>: the height
    /// field's normals), with the same int and float forms; the atmosphere and height uniforms from the frame globals (published by their
    /// owners, the values their GL calls set).</summary>
    void ApplyNative(LegacyProgram p, in MeshUniforms mu, bool patches = false)
    {
        var (vp, eye, options, light) = (frame.ViewProjection, frame.Eye, frame.Options, frame.Light);
        p.Set(mu.ViewProjection, in vp);
        p.Set(mu.HeightNormals, patches ? 1 : 0);
        p.Set(mu.Feature, patches ? 0 : 1);
        p.Set(mu.FeatureBiome, -1);
        p.Set(mu.Wireframe, 0);
        p.Set(mu.Eye, eye.X, eye.Y, eye.Z);
        var s = light.SunDirection;
        p.Set(mu.LightDir, s.X, s.Y, s.Z);
        p.Set(mu.SunColour, light.SunColour.X, light.SunColour.Y, light.SunColour.Z);
        p.Set(mu.AmbientSky, light.AmbientSky.X, light.AmbientSky.Y, light.AmbientSky.Z);
        p.Set(mu.AmbientGround, light.AmbientGround.X, light.AmbientGround.Y, light.AmbientGround.Z);
        p.Set(mu.FogColour, light.FogColour.X, light.FogColour.Y, light.FogColour.Z);
        p.Set(mu.FogDistance, light.FogDistance);
        p.ApplyGlobals();   // SkyRenderer.Apply's and BindHeights' uniforms
        p.Set(mu.WaterHeight, options.Water ? WorldWater.Height : -1e6f);
        p.Set(mu.HalfWorld, (float)WorldLayout.HalfWorldSize);
        p.Set(mu.Debug, options.Debug);
        p.Set(mu.FarStart, options.MaterialDistance * 0.8f);
        p.Set(mu.FarEnd, options.MaterialDistance);
        var t = textures;
        bool textured = options.Textures && t is { HasBiomes: true };
        p.Set(mu.Textured, textured ? 1 : 0);
        p.Set(mu.NormalMaps, textured && options.NormalMaps ? 1 : 0);
        p.Set(mu.HasMaps, options.Textures && t is { MapState: 2 } ? 1 : 0);
        p.Set(mu.MapState, t?.MapState ?? 0);
        p.Set(mu.HasGround, t is { HasGround: true } ? 1 : 0);
        p.Set(mu.HasWorldColour, t is { HasWorldColour: true } ? 1 : 0);
        if (t is not null)
        {
            p.Set(mu.Region, t.Region.X, t.Region.Y, t.Region.Z, t.Region.W);
            p.Set(mu.CellGrid, (float)t.CellsX, t.CellsZ);
        }
    }

    /// <summary>The units <see cref="Apply"/> points the material's samplers at (the rest come from the frame globals, or unit 0 as in GL).</summary>
    static readonly Dictionary<string, int> SamplerUnits = new()
    {
        ["uDiffuse"] = 0, ["uNormal"] = 1, ["uParams"] = 2, ["uCells"] = 3, ["uBlendMap"] = 4, ["uOverlay"] = 5, ["uColour"] = 6,
        ["uGround"] = TerrainShaders.GroundUnit, ["uWorldColour"] = TerrainShaders.WorldColourUnit,
    };

    /// <summary>The samplers a program reads from GL units (not published as frame globals), resolved once per <see cref="FrameGlobals.Version"/>.</summary>
    sealed record UnitSamplers(int GlobalsVersion, (SamplerSlot Slot, int Unit, Meitou.Rendering.Vulkan.Shaders.SamplerInfo Info)[] Samplers);
    UnitSamplers? meshUnitSamplers, depthUnitSamplers;

    /// <summary>Each sampler reads what VkGl would sample for the GL program: the texture on its unit now (after the GL binds above).</summary>
    void BindUnitSamplers(LegacyProgram p, ref UnitSamplers? resolved)
    {
        var interop = gpu.Interop!;
        if (resolved is null || resolved.GlobalsVersion != gpu.Globals.Version)
            resolved = new UnitSamplers(gpu.Globals.Version, [.. p.SamplerNames.Where(n => gpu.Globals.Texture(n) is null)
                .Select(n => (p.Sampler(n), SamplerUnits.GetValueOrDefault(n, 0), p.SamplerInfo(p.Sampler(n))))]);
        foreach (var (slot, unit, info) in resolved.Samplers) p.Bind(slot, interop.SampledUnit(unit, info));
    }

    // ---- depth only (the sun's shadow map, ShadowPass) ----
    uint depthMeshProgram;

    /// <summary>
    /// Draws the terrain's depth for a shadow cascade (<see cref="ShadowShaders.DepthFragment"/>, the caster bias): the main tree's
    /// patches chosen by the camera's <paramref name="eye"/> (so the casters' detail matches what the picture shows), culled by
    /// <paramref name="frustum"/>. Sets the frame's matrix for <see cref="DrawMeshes"/> with <c>depth</c>.
    /// </summary>
    public void DrawDepth(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options)
    {
        long timing = StepTiming.Now();
        if (nativePatchDepth is null)
        {
            depthMeshProgram = WorldGl.Program(gl, TerrainShaders.MeshInstancedDepthVertex, ShadowShaders.DepthFragment);
            nativeDepth = LegacyProgram.Create(gpu, TerrainShaders.MeshInstancedDepthVertex, ShadowShaders.DepthFragment, "terrain mesh depth");
            depthViewProjection = nativeDepth.Uniform("uViewProjection");
            var d = nativePatchDepth = LegacyProgram.Create(gpu, TerrainShaders.PatchVertex, ShadowShaders.DepthFragment, "terrain depth");
            (depthPatchViewProjection, depthPatchEye, depthPatchNode, depthPatchMorph) =
                (d.Uniform("uViewProjection"), d.Uniform("uEye"), d.Uniform("uNode"), d.Uniform("uMorph"));
        }
        if (quadtree.SetRanges(options.TerrainLod) && LodLog) Console.WriteLine($"terrain   lod main {options.TerrainLod}: ranges {string.Join(" ", quadtree.Ranges.SkipLast(1).Select(r => r.ToString("0")))}");
        frame = new Frame(viewProjection, eye, options, frame.Light);
        quadtree.Select(eye, bounds, (min, max) => WorldCamera.Intersects(frustum, min, max), nodes);
        PreparePatches(quadtree);
        var p = nativePatchDepth;
        p.Set(depthPatchViewProjection, in viewProjection);
        p.Set(depthPatchEye, eye.X, eye.Y, eye.Z);
        p.ApplyGlobals();   // BindHeights' uniforms
        BindHeightUnits();
        gl.Enable(EnableCap.CullFace);
        gl.CullFace(TriangleFace.Back);
        gl.FrontFace(FrontFaceDirection.Ccw);
        if (nodes.Count > 0)
        {
            RecordPatches(p, "terrain depth", depthPatchNode, depthPatchMorph);
            DepthTriangles += patchTriangles;
        }
        StepTiming.Add(StepTiming.PatchDepth, timing, nodes.Count);
    }

    /// <summary>Terrain triangles the depth draws have drawn since the counter was last reset (by the caller).</summary>
    public long DepthTriangles { get; set; }

    int DrawMeshesDepth(List<(uint Vao, int IndexCount, Matrix4x4 Model)> meshes)
    {
        if (depthMeshProgram == 0 || !GroupMeshes(meshes, biomes: false)) return 0;
        var nativeDepth = this.nativeDepth!;   // made with depthMeshProgram (DrawDepth)
        var vp = frame.ViewProjection;
        nativeDepth.Set(depthViewProjection, in vp);
        gl.UseProgram(depthMeshProgram);   // GL state as before: the program in use
        BindUnitSamplers(nativeDepth, ref depthUnitSamplers);
        return DrawGroups(nativeDepth, Depth, "terrain mesh depth");
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
        public int IndexCount, Count, Offset;
        public bool Mirrored;
        public Matrix4x4[] Models = new Matrix4x4[16];
        /// <summary>What a native draw of this mesh needs, per program: kept while the interop hands out the same vertex-array export and the
        /// segment's pipeline state is the same. Inline (no object of its own), so a draw reads what grouping has just touched.</summary>
        public NativeMesh ColourNative, DepthNative;
        public ref NativeMesh Native(int kind) => ref kind == Colour ? ref ColourNative : ref DepthNative;
    }

    /// <summary>The pipeline state a segment's draws share (everything of <see cref="GraphicsPipelineDesc"/> but the vertex layout).</summary>
    readonly record struct SegmentPipeline(LegacyProgram Program, AttachmentFormats Formats, BlendState Blend, Silk.NET.Vulkan.ColorComponentFlags Mask,
        Silk.NET.Vulkan.PolygonMode Polygon, bool AlphaToCoverage, bool DepthClamp);

    /// <summary>A mesh's own vertex buffers (locations 0 and 1: position, normal), bound with one call.</summary>
    [System.Runtime.CompilerServices.InlineArray(2)]
    struct OwnVertices { BufferBinding first; }

    /// <summary>A mesh resolved for one program: the export it came from, its pipeline, its own vertex buffers (locations 0 up to the
    /// placements) and its indices.</summary>
    struct NativeMesh
    {
        public VertexArrayBindings? Source;
        public int Segment;                       // the SegmentPipeline it was resolved with, by number (segmentIds)
        public int VertexCount;
        public GraphicsPipeline Pipeline;
        public OwnVertices Vertices;
        public BufferBinding Elements;
    }

    // Per program kind: the last segment state and its number (a new number when it changes, so a mesh compares one int per draw).
    readonly SegmentPipeline[] lastSegment = new SegmentPipeline[2];
    readonly int[] segmentIds = new int[2];
    int segmentCount;

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
        foreach (var (vao, count, m) in meshes)
        {
            var key = (vao, count, m.GetDeterminant() < 0);
            if (!meshGroups.TryGetValue(key, out var g)) meshGroups[key] = g = new MeshGroup { Vao = vao, IndexCount = count, Mirrored = key.Item3 };
            if (g.Count == 0) meshGroupList.Add(g);
            if (g.Count == g.Models.Length) Array.Resize(ref g.Models, g.Count * 2);
            var placed = m;
            // Not resident yet (or no textures): -1, blend the biomes as the terrain does.
            if (biomes) placed.M14 = textures?.FeatureBiomeRow(m.Translation.X, m.Translation.Z) ?? -1;
            g.Models[g.Count++] = placed;
        }
        if (meshGroupList.Count == 0) return false;
        int total = 0;
        foreach (var g in meshGroupList) { g.Offset = total; total += g.Count; }
        // One copy: each group's placements straight into the frame's (write-combined) constants, in order.
        placements = gpu.Frame.Constants.Allocate((ulong)total * 64, 16);
        var target = new Span<Matrix4x4>(placements.Pointer, total);
        foreach (var g in meshGroupList) g.Models.AsSpan(0, g.Count).CopyTo(target[g.Offset..]);
        return true;
    }

    /// <summary>
    /// Draws <see cref="meshGroupList"/> natively with <paramref name="p"/> inside the pass VkGl is drawing (its targets and GL's state at this
    /// point), back faces culled, a mirroring placement turning the winding round; the placements from this frame's constants. Leaves GL's
    /// state as the GL version did (culling off with back faces selected, counter-clockwise, no vertex array).
    /// <para>
    /// The hot-path pattern (docs/renderer-native.md 7.5): one segment inside VkGl's pass; what all draws share once (dynamic state, sets 0
    /// and 1, the placements at locations 7 to 10); per draw only what differs, resolved once per mesh (<see cref="NativeMesh"/>): the
    /// pipeline, the mesh's own vertex buffers, the front face when it flips, the indices, and the draw, whose firstInstance reaches the
    /// group's placements.
    /// </para>
    /// </summary>
    int DrawGroups(LegacyProgram p, int kind, string label)
    {
        var interop = gpu.Interop!;
        var cmd = interop.BeginNativeInPass(label);
        var targets = interop.CurrentTargets();
        var state = interop.CurrentState();
        var segment = new SegmentPipeline(p, targets.Formats, state.Blend, state.ColourMask, state.Polygon, state.AlphaToCoverage, state.DepthClamp);
        if (segmentIds[kind] == 0 || segment != lastSegment[kind]) (lastSegment[kind], segmentIds[kind]) = (segment, ++segmentCount);
        int segmentId = segmentIds[kind];
        var ccw = GlConventions.FrontFace(FrontFaceDirection.Ccw);
        var cw = GlConventions.FrontFace(FrontFaceDirection.CW);
        cmd.SetViewport(targets.Viewport);
        cmd.SetScissor(targets.Scissor);
        cmd.SetRaster(Silk.NET.Vulkan.CullModeFlags.BackBit, ccw);
        var front = ccw;
        cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
        cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
        p.Flush(cmd);
        // The placement's rows at locations the meshes' vertex arrays leave free (their other programs do not read them).
        Span<BufferBinding> rows = stackalloc BufferBinding[4];
        for (int a = 0; a < 4; a++) rows[a] = new BufferBinding(placements.Handle, placements.Offset + (ulong)(16 * a));
        cmd.BindVertexBuffers(TerrainShaders.MeshInstanceLocation, rows);
        foreach (var g in meshGroupList)
        {
            var va = interop.VertexArray(g.Vao);
            ref var n = ref g.Native(kind);
            if (!ReferenceEquals(n.Source, va) || n.Segment != segmentId) Resolve(ref n, p, va, segmentId, state, targets.Formats, label);
            cmd.BindPipeline(n.Pipeline);
            var f = g.Mirrored ? cw : ccw;
            if (f != front) { cmd.SetFrontFace(f); front = f; }
            cmd.BindVertexBuffers(0, ((ReadOnlySpan<BufferBinding>)n.Vertices)[..n.VertexCount]);
            cmd.BindIndexBuffer(n.Elements, Silk.NET.Vulkan.IndexType.Uint32);
            cmd.DrawIndexed((uint)g.IndexCount, (uint)g.Count, 0, 0, (uint)g.Offset);
        }
        interop.EndNative(cmd);
        gl.CullFace(TriangleFace.Back);
        gl.FrontFace(FrontFaceDirection.Ccw);
        gl.Disable(EnableCap.CullFace);
        gl.BindVertexArray(0);
        return meshGroupList.Count;
    }

    /// <summary>A mesh's pipeline and own vertex buffers for <paramref name="p"/>, the placements as per-instance rows of 64 bytes at locations 7 to 10.</summary>
    void Resolve(ref NativeMesh n, LegacyProgram p, VertexArrayBindings va, int segment, DrawState state, AttachmentFormats formats, string label)
    {
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        va.Attributes.AsSpan().CopyTo(attributes);
        for (int a = 0; a < 4; a++)
            attributes[TerrainShaders.MeshInstanceLocation + a] = new LegacyProgram.Attribute(default, Silk.NET.Vulkan.Format.R32G32B32A32Sfloat, 64, true);
        var pipeline = gpu.Pipelines.Get(state.Pipeline(p.Program, p.VertexLayout(attributes), Silk.NET.Vulkan.PrimitiveTopology.TriangleList, formats, label));
        int own = p.InputLocations.Where(l => l < TerrainShaders.MeshInstanceLocation).DefaultIfEmpty(-1).Max() + 1;
        if (own > 2) throw new NotSupportedException($"{p.Name}: more than two own vertex inputs");
        n = new NativeMesh { Source = va, Segment = segment, Pipeline = pipeline, VertexCount = own, Elements = va.Elements };
        p.VertexBuffers(attributes, 0, own).CopyTo(n.Vertices);
    }

    int U(uint program, string name)
    {
        if (!uniforms.TryGetValue((program, name), out int location)) uniforms[(program, name)] = location = gl.GetUniformLocation(program, name);
        return location;
    }

    public void Dispose()
    {
        ReportMeshTiming();
        if (depthMeshProgram != 0) gl.DeleteProgram(depthMeshProgram);
        nativePatch?.Dispose();
        nativePatchDepth?.Dispose();
        gl.DeleteVertexArray(gridVao);
        gl.DeleteBuffer(gridVbo);
        gl.DeleteBuffer(gridEbo);
        gl.DeleteTexture(coarseTexture);
        gl.DeleteTexture(fineTexture);
        foreach (var r in retired) gl.DeleteTexture(r.Texture);
        gl.DeleteProgram(patchProgram);
        gl.DeleteProgram(meshProgram);
        textures?.Dispose();
        nativeMesh?.Dispose();
        nativeDepth?.Dispose();
    }
}

/// <summary>
/// <c>MEITOU_TERRAIN_TIMING=1</c>: CPU time of the terrain patches (colour, shadow depth), the Meitou blocker map and the terrain shadow
/// sweep, per call and per draw, printed when the terrain renderer is disposed (docs/renderer-native.md 7.1, wave 3 agent B).
/// </summary>
internal static class StepTiming
{
    public static readonly bool On = Environment.GetEnvironmentVariable("MEITOU_TERRAIN_TIMING") == "1";
    public const int PatchColour = 0, PatchDepth = 1, Blocker = 2, Sweep = 3;
    static readonly string[] Names = ["patches colour", "patches depth", "blocker map", "terrain sweep"];
    static readonly long[] ticks = new long[4], calls = new long[4], draws = new long[4];

    public static long Now() => On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

    public static void Add(int kind, long start, int count)
    {
        if (!On) return;
        ticks[kind] += System.Diagnostics.Stopwatch.GetTimestamp() - start;
        calls[kind]++;
        draws[kind] += count;
    }

    public static void Report()
    {
        if (!On) return;
        for (int k = 0; k < 4; k++)
        {
            if (calls[k] == 0) continue;
            double us = ticks[k] * 1e6 / System.Diagnostics.Stopwatch.Frequency;
            Console.WriteLine(FormattableString.Invariant(
                $"terrain timing  {Names[k],-15} {calls[k]} calls, {draws[k]} draws, {us / calls[k]:F1} us/call, {(draws[k] > 0 ? us / draws[k] : 0):F2} us/draw"));
            ticks[k] = calls[k] = draws[k] = 0;
        }
    }
}
