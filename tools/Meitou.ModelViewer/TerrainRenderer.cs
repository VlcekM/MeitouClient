using System.Numerics;
using Meitou.Data.World;
using Silk.NET.OpenGL;

namespace Meitou.ModelViewer;

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
    /// Range of a terrain LOD level in multiples of its node size (TerrainQuadtree; at least 2). Fixed at the useful
    /// maximum: the finest level (64 cells of 18 units) then covers 8 × 1152 = 9216 units, about what the streamed
    /// fine-height window (1536 cells, re-centred after 15% of its width) always holds around the eye; beyond it the
    /// heights are the coarse 144-unit ones, so a larger value only adds triangles. A graphics option later.
    /// </summary>
    public float LodDistance { get; set; } = 8f;
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
    readonly GL gl;
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
    TerrainQuadtree quadtree;
    TerrainQuadtree? spare;   // the reflection's own tree (coarser), so the two passes do not rebuild each other's every frame
    TerrainQuadtree current = null!;   // the tree the running Draw selects with
    TerrainTextures? textures;
    Frame frame;

    const int GridCells = 64;

    record struct Frame(Matrix4x4 ViewProjection, Vector3 Eye, WorldRenderOptions Options, WorldLighting Light);

    /// <param name="coarse">Whole-world raw heights, (<paramref name="coarseSize"/>)² samples (2^n + 1 per side).</param>
    /// <param name="fine">The loaded region at its own step.</param>
    public TerrainRenderer(GL gl, ushort[] coarse, int coarseSize, HeightWindow fine, float lodDistance)
    {
        this.gl = gl;
        this.fine = fine;
        this.coarse = coarse;
        this.coarseSize = coarseSize;
        bounds = new TerrainHeightBounds(coarse, coarseSize, fine);
        quadtree = new TerrainQuadtree(fine.Spacing, GridCells, lodDistance);
        fineBand = BandOf(fine);
        patchProgram = WorldGl.Program(gl, TerrainShaders.PatchVertex, TerrainShaders.Fragment);
        meshProgram = WorldGl.Program(gl, TerrainShaders.MeshVertex, TerrainShaders.Fragment);

        coarseTexture = HeightTexture(coarse, coarseSize, coarseSize);
        fineTexture = HeightTexture(fine.Raw, fine.Columns, fine.Rows);

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
        if (secondary)
        {
            if (spare is null || Math.Abs(options.LodDistance - (float)(spare.Ranges[0] / spare.NodeSize(0))) > 1e-4f)
                spare = new TerrainQuadtree((float)quadtree.Spacing(0), GridCells, Math.Max(options.LodDistance, 2f));
            current = spare;
        }
        else
        {
            if (Math.Abs(options.LodDistance - LodDistanceInUse) > 1e-4f)
                quadtree = new TerrainQuadtree((float)quadtree.Spacing(0), GridCells, Math.Max(options.LodDistance, 2f));
            current = quadtree;
        }
        frame = new Frame(viewProjection, eye, options, light);
        Apply(patchProgram, heightNormals: true);
        current.Select(eye, bounds, (min, max) => max.Y >= cullBelow && WorldCamera.Intersects(frustum, min, max), nodes);

        gl.Enable(EnableCap.DepthTest);
        gl.Enable(EnableCap.CullFace);
        gl.CullFace(TriangleFace.Back);
        gl.FrontFace(FrontFaceDirection.Ccw);
        gl.BindVertexArray(gridVao);
        if (options.Wireframe != 2) DrawNodes(wire: false);
        if (options.Wireframe != 0)
        {
            gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
            gl.Enable(EnableCap.PolygonOffsetLine);
            gl.PolygonOffset(-1, -1);
            DrawNodes(wire: true);
            gl.Disable(EnableCap.PolygonOffsetLine);
            gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
        }
        gl.BindVertexArray(0);
    }

    float LodDistanceInUse => (float)(quadtree.Ranges[0] / quadtree.NodeSize(0));

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
            if (!wire)
            {
                DrawnChunks++;
                DrawnTriangles += indexCounts[part] / 3;
            }
        }
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
        gl.ActiveTexture(TextureUnit.Texture0 + TerrainShaders.HeightCoarseUnit);
        gl.BindTexture(TextureTarget.Texture2D, coarseTexture);
        gl.ActiveTexture(TextureUnit.Texture0 + TerrainShaders.HeightFineUnit);
        gl.BindTexture(TextureTarget.Texture2D, fineTexture);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.Uniform1(U(program, "uHeightCoarse"), TerrainShaders.HeightCoarseUnit);
        gl.Uniform1(U(program, "uHeightFine"), TerrainShaders.HeightFineUnit);
        float h = WorldLayout.HalfWorldSize;
        gl.Uniform4(U(program, "uCoarseRect"), -h, -h, h, h);
        gl.Uniform2(U(program, "uCoarseCells"), coarseSize - 1f, coarseSize - 1f);
        var (x0, z0) = fine.WorldOf(0, 0);
        gl.Uniform4(U(program, "uFineRect"), (float)x0, (float)z0, (float)x0 + (fine.Columns - 1) * fine.Spacing, (float)z0 + (fine.Rows - 1) * fine.Spacing);
        gl.Uniform2(U(program, "uFineCells"), fine.Columns - 1f, fine.Rows - 1f);
        gl.Uniform1(U(program, "uFineBand"), fineBand);
        gl.Uniform1(U(program, "uHasFine"), 1);
    }

    /// <summary>
    /// Draws other meshes with the terrain material (TERRAIN-mode map features), after <see cref="Draw"/> set the
    /// frame. Each item: a vertex array with position at attribute 0 and normal at 1, its index count, its transform.
    /// As the game's <c>Feature_Terrain_DX11</c> (docs/formats/foliage.md, "TERRAIN-mode meshes"): one biome per mesh,
    /// the one of <c>biomemap.png</c> at the mesh's origin, back faces culled.
    /// </summary>
    public void DrawMeshes(IEnumerable<(uint Vao, int IndexCount, Matrix4x4 Model)> meshes)
    {
        Apply(meshProgram, heightNormals: false);
        gl.Uniform1(U(meshProgram, "uFeature"), 1);
        gl.Enable(EnableCap.CullFace);
        gl.CullFace(TriangleFace.Back);
        int model = U(meshProgram, "uModel"), biome = U(meshProgram, "uFeatureBiome");
        foreach (var (vao, count, m) in meshes)
        {
            WorldGl.Matrix(gl, model, m);
            // Not resident yet (or no textures): blend the biomes as the terrain does.
            gl.Uniform1(biome, textures?.FeatureBiomeRow(m.Translation.X, m.Translation.Z) ?? -1);
            // A mirroring placement turns the winding round.
            gl.FrontFace(m.GetDeterminant() < 0 ? FrontFaceDirection.CW : FrontFaceDirection.Ccw);
            gl.BindVertexArray(vao);
            gl.DrawElements(PrimitiveType.Triangles, (uint)count, DrawElementsType.UnsignedInt, (void*)0);
        }
        gl.FrontFace(FrontFaceDirection.Ccw);
        gl.Disable(EnableCap.CullFace);
        gl.BindVertexArray(0);
    }

    int U(uint program, string name)
    {
        if (!uniforms.TryGetValue((program, name), out int location)) uniforms[(program, name)] = location = gl.GetUniformLocation(program, name);
        return location;
    }

    public void Dispose()
    {
        gl.DeleteVertexArray(gridVao);
        gl.DeleteBuffer(gridVbo);
        gl.DeleteBuffer(gridEbo);
        gl.DeleteTexture(coarseTexture);
        gl.DeleteTexture(fineTexture);
        foreach (var r in retired) gl.DeleteTexture(r.Texture);
        gl.DeleteProgram(patchProgram);
        gl.DeleteProgram(meshProgram);
        textures?.Dispose();
    }
}
