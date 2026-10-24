using System.Numerics;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Shaders;

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
    /// <summary>The sandbox's floor grid drawn into the terrain (depth-tested, so characters hide it).</summary>
    public bool Grid { get; set; }
    /// <summary>0 normal, 1 blend-map slot weights, 2 layer weights (R cliff, G slope, B grass), 3 plain shading.</summary>
    public int Debug { get; set; }
    /// <summary>
    /// Terrain LOD (<see cref="Meitou.Data.World.TerrainLod"/>): the screen-space height error allowed near the eye, in pixels of
    /// the picture rendered (<c>--terrain-error</c>, the Tab slider), the same at every distance (no far ramp: it made pond outlines blocky).
    /// </summary>
    public float TerrainPixelError { get; set; } = DefaultTerrainPixelError;
    public const float DefaultTerrainPixelError = 10;
    /// <summary>
    /// Pixels per world unit at distance 1 in the picture being drawn: the frame sets it from the render height (after the
    /// upscaler's render scale) and the field of view, the reflection pass from its own (half-size) target.
    /// </summary>
    public float TerrainPixelScale { get; set; } = TerrainLod.ProjectionScale(720, 50 * MathF.PI / 180);
    /// <summary>The LOD rule these options give.</summary>
    public TerrainLod TerrainLod => new(TerrainPixelScale, TerrainPixelError, TerrainPixelError, 0);
    /// <summary>Beyond this distance the terrain takes the biomes' ground colour (the game's material distance is 30000).</summary>
    public float MaterialDistance { get; set; } = 30000;
}

/// <summary>Sun and sky light for the world shaders (from <see cref="WorldSky"/>).</summary>
public readonly record struct WorldLighting(Vector3 SunDirection, Vector3 SunColour, Vector3 AmbientSky, Vector3 AmbientGround, Vector3 FogColour, float FogDistance,
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
/// Draws the terrain of the whole world with continuous LOD (<see cref="TerrainQuadtree"/>): one shared grid patch
/// placed per selected quadtree node, heights read in the vertex shader from a coarse whole-world height texture and
/// the loaded region's fine one, normals from the height field per pixel. Nodes outside the view frustum are skipped.
/// </summary>
public sealed unsafe partial class TerrainRenderer : IDisposable
{
    readonly DeviceBuffer gridVertices, gridIndices;
    readonly TerrainTexture coarseTexture;
    TerrainTexture fineTexture;
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
    bool ownsTextures = true;
    internal TerrainTextures? TextureSet => textures;

    /// <summary>The texture field's tile at world (<paramref name="x"/>, <paramref name="z"/>) (<see cref="GroundRelief.FieldCentre"/>): its label, or null.</summary>
    public string? FieldLabelAt(float x, float z) => textures?.FieldLabel(x, z);
    Frame frame;

    const int GridCells = 64;

    record struct Frame(Matrix4x4 ViewProjection, Vector3 Eye, WorldRenderOptions Options, WorldLighting Light);

    /// <param name="coarse">Whole-world raw heights, (<paramref name="coarseSize"/>)² samples (2^n + 1 per side).</param>
    /// <param name="fine">The loaded region at its own step.</param>
    public TerrainRenderer(GpuContext gpu, ushort[] coarse, int coarseSize, HeightWindow fine)
    {
        this.gpu = gpu;
        this.fine = fine;
        this.coarse = coarse;
        this.coarseSize = coarseSize;
        bounds = new TerrainHeightBounds(coarse, coarseSize, fine);
        quadtree = new TerrainQuadtree(fine.Spacing, GridCells, new WorldRenderOptions().TerrainLod);
        spare = new TerrainQuadtree(fine.Spacing, GridCells, new WorldRenderOptions().TerrainLod);
        fineBand = BandOf(fine);
        // The native programs (docs/renderer-native.md 7.1, step O), all made here so a draw never compiles.
        nativeFrame = new NativeFrame(gpu);
        uniformAlign = Math.Max(gpu.Device.Limits.MinUniformBufferOffsetAlignment, 16);
        constantsLayout = gpu.Shaders.CreateSetLayout(
            [new Silk.NET.Vulkan.DescriptorSetLayoutBinding(0, Silk.NET.Vulkan.DescriptorType.UniformBufferDynamic, 1,
                gpu.Shaders.GraphicsStages)], 0);
        Silk.NET.Vulkan.DescriptorSetLayout[] sets = [nativeFrame.SetLayout, gpu.Bindless.Layout, constantsLayout];
        this.sets = sets;
        UseColour(GroundRelief.ParallaxActive);
        patchDepth = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexNative(), TerrainShaders.DepthFragmentNative(), "terrain depth");
        patchPrepass = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexNative(), TerrainShaders.PrepassFragmentNative(), "terrain prepass");
        patchPrepassCut = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexNative(), TerrainShaders.PrepassFragmentNative(interiorCut: true), "terrain prepass interior cut");
        // The ground displacement (GroundRelief): the colour and prepass programs tessellated, for the patches near the eye (the same stages in both, so the
        // late colour's depth test meets the prepass's depths).
        if (gpu.Device.HasTessellation)
        {
            patchPrepassTess = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexTessNative(), TerrainShaders.PrepassFragmentNative(), "terrain relief prepass",
                TerrainShaders.PatchControlNative(), TerrainShaders.PatchEvaluationNative());
            patchPrepassCutTess = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexTessNative(), TerrainShaders.PrepassFragmentNative(interiorCut: true), "terrain relief prepass interior cut",
                TerrainShaders.PatchControlNative(), TerrainShaders.PatchEvaluationNative());
        }
        meshDepth = new TerrainProgram(gpu, sets, TerrainShaders.MeshInstancedDepthVertexNative(), TerrainShaders.DepthFragmentNative(), "terrain mesh depth");
        // The rocks' smoothing (RockSmoothing): the mesh programs tessellated, the casters with the same curve.
        if (gpu.Device.HasTessellation)
        {
            meshDepthTess = new TerrainProgram(gpu, sets, TerrainShaders.MeshVertexTessNative(), TerrainShaders.DepthFragmentNative(), "terrain mesh depth smoothed",
                TerrainShaders.MeshControlNative(), TerrainShaders.MeshEvaluationDepthNative());
        }
        // GL's stand-ins for an absent texture, in the array each sampler indexes (the bindless stand-in is the float 2D one only).
        standIn2D = gpu.Bindless.Register(BindlessKind.Texture2D, gpu.Dummy(StandInInfo(false, ScalarKind.Float)));
        standInArray = gpu.Bindless.Register(BindlessKind.Texture2DArray, gpu.Dummy(StandInInfo(true, ScalarKind.Float)));
        standInUInt = gpu.Bindless.Register(BindlessKind.UTexture2D, gpu.Dummy(StandInInfo(false, ScalarKind.UInt)));

        using (var batch = gpu.Uploads.Begin())
        {
            coarseTexture = HeightTexture(batch, coarse, coarseSize, coarseSize);
            fineTexture = HeightTexture(batch, fine.Raw, fine.Columns, fine.Rows);
        }
        Activate();

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
        // The patch grid as one vertex buffer (location 0: the grid point, two floats) and one index buffer (GL's vertex array, made native).
        var gridIndexData = indices.ToArray();
        gridVertices = DeviceBuffer.Create(gpu, (ulong)grid.Length * sizeof(float), BufferUse.Vertex, "terrain patches vertices");
        gridIndices = DeviceBuffer.Create(gpu, (ulong)gridIndexData.Length * sizeof(uint), BufferUse.Index, "terrain patches indices");
        using (var batch = gpu.Uploads.Begin())
        {
            batch.Write(gridVertices, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(grid.AsSpan()));
            batch.Write(gridIndices, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(gridIndexData.AsSpan()));
        }
        gridVertex = new LegacyProgram.Attribute(new BufferBinding(gridVertices.Handle, 0, gridVertices.Size), Silk.NET.Vulkan.Format.R32G32Sfloat, 8, false);
        gridElements = new BufferBinding(gridIndices.Handle, 0, gridIndices.Size);
    }

    /// <summary>The grid's vertex attribute at location 0 and its index buffer, as VkGl fed them from the GL vertex array.</summary>
    readonly LegacyProgram.Attribute gridVertex;
    readonly BufferBinding gridElements;

    /// <summary>Width of the band at the fine window's border where it fades into the coarse grid.</summary>
    static float BandOf(HeightWindow w) => Math.Clamp((w.Columns - 1) * w.Spacing * 0.1f, w.Spacing * 4, 3000);

    /// <summary>A height texture (GL's R16, one level, linear, clamped) through <paramref name="batch"/>, with <paramref name="raw"/> written when given.</summary>
    TerrainTexture HeightTexture(UploadBatch batch, ushort[]? raw, int width, int height)
    {
        var t = batch.Create(new TextureDesc(GlConventions.VkFormat(InternalFormat.R16), width, height, Name: "terrain heights"));
        if (raw is not null) batch.Write(t, 0, 0, Rect(0, 0, width, height), System.Runtime.InteropServices.MemoryMarshal.AsBytes(raw.AsSpan(0, width * height)));
        return new TerrainTexture(gpu, t, TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge);
    }

    static Silk.NET.Vulkan.Rect2D Rect(int x, int y, int width, int height) => new(new(x, y), new((uint)width, (uint)height));

    public int LevelCount => quadtree.LevelCount;
    public double FinestSpacing => quadtree.Spacing(0);
    public int DrawnChunks { get; private set; }
    public long DrawnTriangles { get; private set; }

    /// <summary>Starts counting drawn nodes and triangles for a new frame (several depth slices add up).</summary>
    public void BeginFrame()
    {
        (DrawnChunks, DrawnTriangles) = (0, 0);
        frameNumber++;
        // A replaced window is let go a few frames after the swap (its image and bindless entry are freed after the frames in flight).
        for (int i = retired.Count - 1; i >= 0; i--)
            if (frameNumber - retired[i].Frame >= 4) { retired[i].Texture.Dispose(); retired.RemoveAt(i); }
    }

    int frameNumber;
    readonly List<(TerrainTexture Texture, int Frame)> retired = [];

    /// <summary>Uses biome textures and land maps from now on (null: untextured).</summary>
    public void SetTextures(TerrainTextures? t, bool owns = true) { textures = t; ownsTextures = owns; }

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
        TerrainTexture? texture = null;
        uploads.Add(() =>
        {
            using var batch = gpu.Uploads.Begin();
            texture = HeightTexture(batch, null, window.Columns, window.Rows);
        }, "height texture");
        for (int row = 0; row < window.Rows; row += slabRows)
        {
            int r0 = row, n = Math.Min(slabRows, window.Rows - row);
            uploads.Add(() =>
            {
                using var batch = gpu.Uploads.Begin();
                batch.Write(texture!.Texture, 0, 0, Rect(0, r0, window.Columns, n),
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(window.Raw.AsSpan(r0 * window.Columns, n * window.Columns)));
            }, "height slab");
        }
        uploads.Add(() =>
        {
            retired.Add((fineTexture, frameNumber));
            fineTexture = texture!;
            fine = window;
            fineBand = BandOf(window);
            bounds.Apply(patch);
            done?.Invoke();
        }, "height swap");
    }

    /// <summary>Height at a world point from the same grids the shaders read (bilinear, blended at the region's edge).</summary>
    public float HeightAt(float x, float z) => Snapshot().HeightAt(x, z);

    /// <summary>The height sources as they are now, for a worker thread (render thread only: the fine window is swapped there).</summary>
    internal HeightSnapshot Snapshot() => new(coarse, coarseSize, fine, fineBand);

    /// <summary>The fog cull (set by the world frame around the main camera's draw only): nodes wholly hidden by the fog the eye is in are skipped.</summary>
    internal FogVolumes? FogCull { get; set; }

    /// <summary>Draws the terrain; nodes whose highest point is under <paramref name="cullBelow"/> are skipped (the reflection pass clips everything below the water). <paramref name="secondary"/>: the reflection's call, with its own quadtree for its own LOD distance.</summary>
    public void Draw(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, WorldLighting light, float cullBelow = float.NegativeInfinity, bool secondary = false, bool defer = false)
    {
        UseColour(GroundRelief.ParallaxActive);
        current = secondary ? spare : quadtree;
        current.SetRanges(options.TerrainLod);
        frame = new Frame(viewProjection, eye, options, light);
        var fog = FogCull;
        current.Select(eye, bounds, (min, max) => max.Y >= cullBelow && WorldCamera.Intersects(frustum, min, max) && (fog is null || !fog.Hidden(min, max, FogVolumes.CullKind.Terrain)), nodes);

        SortNearestFirst(eye);
        PreparePatches(current);
        SplitInteriorCut(secondary ? null : InteriorCut);
        SplitRelief(secondary, options);
        if (nodes.Count > 0)
        {
            if (options.Wireframe != 2)
            {
                PrepareConstants(material: true, patches: true);
                if (defer && !secondary && options.Wireframe == 0)
                {
                    // Depth only now, the colour after the objects and foliage (DrawDeferred): the colour program then runs only where the terrain is what is seen.
                    RecordPatches(patchPrepass, "terrain prepass", PatchMode.Prepass, plainCount);
                    if (reliefCount > 0) RecordPatches(patchPrepassTess!, "terrain relief prepass", PatchMode.Prepass, reliefCount, reliefDraws);
                    if (cutCount > 0) RecordPatches(patchPrepassCut, "terrain prepass interior cut", PatchMode.Prepass, cutCount, cutDraws);
                    if (cutReliefCount > 0) RecordPatches(patchPrepassCutTess!, "terrain relief prepass interior cut", PatchMode.Prepass, cutReliefCount, cutReliefDraws);
                    (latePending, lateFrame) = (true, frame);
                }
                else
                {
                    RecordPatches(patchColour, "terrain", PatchMode.Solid, plainCount);
                    if (reliefCount > 0) RecordPatches(patchColourTess!, "terrain relief", PatchMode.Solid, reliefCount, reliefDraws);
                    if (cutCount > 0) RecordPatches(patchColourCut, "terrain interior cut", PatchMode.Solid, cutCount, cutDraws);
                    if (cutReliefCount > 0) RecordPatches(patchColourCutTess!, "terrain relief interior cut", PatchMode.Solid, cutReliefCount, cutReliefDraws);
                }
                DrawnChunks += nodes.Count;
                DrawnTriangles += patchTriangles;
            }
            if (options.Wireframe != 0)
            {
                // The debug outline (Wireframe 1 and 2): the same patches as lines in a dark grey, pulled towards the eye as GL's polygon offset (-1, -1) did.
                PrepareConstants(material: true, patches: true);
                constants.Wireframe = 1;
                RecordPatches(patchColour, "terrain wireframe", PatchMode.Wireframe, plainCount);
                RecordPatches(patchColour, "terrain wireframe", PatchMode.Wireframe, cutCount, cutDraws);
            }
        }
    }

    bool latePending;
    Frame lateFrame;

    /// <summary>
    /// The terrain colour of a <see cref="Draw"/> that was called with <c>defer</c> (its depth is in the buffer): the same patches and material with the depth
    /// test on and no depth write, so only the fragments the objects and foliage left uncovered are shaded (docs/render-terrain.md, "Terrain colour after the occluders").
    /// Nothing between the two calls may select other patches (the shadows and the reflection draw before the scene).
    /// </summary>
    public void DrawDeferred()
    {
        if (!latePending) return;
        latePending = false;
        frame = lateFrame;
        PrepareConstants(material: true, patches: true);
        RecordPatches(patchColour, "terrain", PatchMode.Late, plainCount);
        if (reliefCount > 0) RecordPatches(patchColourTess!, "terrain relief", PatchMode.Late, reliefCount, reliefDraws);
        // The cut patches' colour without the cut: their prepass discarded the cut fragments, so an equal depth test leaves them out early. The cut's
        // discard here turned the early depth test off, and the GPU drained between these draws (1.4 ms for 19 of them; docs/render-terrain.md "Registers").
        // Both passes run the same vertex and tessellation stages (invariant positions): a kept fragment meets its own depth.
        if (cutCount > 0) RecordPatches(patchColour, "terrain interior cut", PatchMode.LateEqual, cutCount, cutDraws);
        if (cutReliefCount > 0) RecordPatches(patchColourTess!, "terrain relief interior cut", PatchMode.LateEqual, cutReliefCount, cutReliefDraws);
    }

    /// <summary>
    /// The interior cut of the main view (docs/render-interior-cut.md; set by the world frame around its near slice's terrain draws, null otherwise): the
    /// mask's depth range, read by the cut programs, which only the patches overlapping a drawn building take (a <c>discard</c> costs the early depth test).
    /// </summary>
    internal InteriorMaskRenderer? InteriorCut { get; set; }

    /// <summary>The blood on the ground the patches lay over their albedo (docs/game/blood-decals.md; null: none). The host records it before the scene.</summary>
    public TerrainDecalRenderer? Decals { get; set; }

    PatchDraw[] cutDraws = new PatchDraw[64];
    int plainCount, cutCount;
    uint cutMask;

    /// <summary>Moves the patches whose square overlaps a building of <paramref name="cut"/>'s (when it drew any) out of <see cref="patchDraws"/>
    /// into <see cref="cutDraws"/>, both kept nearest first: <see cref="plainCount"/> and <see cref="cutCount"/>.</summary>
    void SplitInteriorCut(InteriorMaskRenderer? cut)
    {
        int n = nodes.Count;
        (plainCount, cutCount) = (n, 0);
        if (cut is not { Active: true }) return;
        cutMask = cut.Index;
        var boxes = cut.Boxes;
        int kept = 0;
        for (int i = 0; i < n; i++)
        {
            var d = patchDraws[i];
            float x0 = d.Push.Node.X, z0 = d.Push.Node.Y, s = d.Push.Node.Z;
            bool overlaps = false;
            foreach (var (min, max) in boxes)
                if (x0 <= max.X && x0 + s >= min.X && z0 <= max.Y && z0 + s >= min.Y) { overlaps = true; break; }
            if (!overlaps) { patchDraws[kept++] = d; continue; }
            if (cutCount == cutDraws.Length) Array.Resize(ref cutDraws, cutDraws.Length * 2);
            cutDraws[cutCount++] = d;
        }
        plainCount = kept;
    }

    PatchDraw[] reliefDraws = new PatchDraw[16], cutReliefDraws = new PatchDraw[16];
    int reliefCount, cutReliefCount;

    /// <summary>
    /// Moves the plain and the cut patches whose square comes within <see cref="GroundRelief.TessellatedRadius"/> of the eye (horizontally) into
    /// <see cref="reliefDraws"/> and <see cref="cutReliefDraws"/>, drawn by the tessellated programs: the main view only, with the displacement on.
    /// The displacement and the split are nil beyond its end, so these patches meet the plain ones on unraised, unsplit edges.
    /// </summary>
    void SplitRelief(bool secondary, WorldRenderOptions options)
    {
        (reliefCount, cutReliefCount) = (0, 0);
        if (secondary || patchColourTess is null || !GroundRelief.DisplaceActive && GroundRelief.RefinePixels <= 0 || options.Wireframe != 0 || !options.Textures || textures is not { HasBiomes: true }) return;
        float r2 = GroundRelief.TessellatedRadius >= float.MaxValue ? float.MaxValue : GroundRelief.TessellatedRadius * GroundRelief.TessellatedRadius;
        plainCount = SplitNear(patchDraws, plainCount, ref reliefDraws, ref reliefCount, frame.Eye, r2);
        cutCount = SplitNear(cutDraws, cutCount, ref cutReliefDraws, ref cutReliefCount, frame.Eye, r2);
    }

    /// <summary>Moves the draws of <paramref name="draws"/>[..<paramref name="count"/>] whose square lies within sqrt(<paramref name="r2"/>) of the eye
    /// (horizontally) to <paramref name="near"/>, both kept in order; returns how many stay.</summary>
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

    double[] nodeKeys = [];

    /// <summary>
    /// Orders <see cref="nodes"/> nearest first (by the horizontal distance from the eye to the patch's square), so nearer hills fill the depth
    /// buffer before the land behind them is shaded: the material's pixels behind a hill fail the early depth test. Patches do not overlap, so
    /// the picture does not depend on the order (docs/formats/terrain.md, "In the viewer").
    /// </summary>
    void SortNearestFirst(Vector3 eye)
    {
        int n = nodes.Count;
        if (nodeKeys.Length < n) nodeKeys = new double[Math.Max(n, nodeKeys.Length * 2)];
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(nodes);
        for (int i = 0; i < n; i++)
        {
            var node = span[i];
            // A quadrant node draws one quarter of its square (TerrainQuadtree.Select).
            double s = node.Quadrant < 0 ? node.Size : node.Size / 2;
            double x0 = node.X0 + (node.Quadrant < 0 ? 0 : (node.Quadrant & 1) * s), z0 = node.Z0 + (node.Quadrant < 0 ? 0 : (node.Quadrant >> 1) * s);
            double dx = Math.Max(Math.Max(x0 - eye.X, eye.X - (x0 + s)), 0), dz = Math.Max(Math.Max(z0 - eye.Z, eye.Z - (z0 + s)), 0);
            nodeKeys[i] = dx * dx + dz * dz;
        }
        nodeKeys.AsSpan(0, n).Sort(span);
    }

    /// <summary>How <see cref="RecordPatches"/> draws: filled (colour or depth), or the debug outline.</summary>
    enum PatchMode { Solid, Wireframe, Prepass, Late, LateEqual }

    DrawState? lastGlState, lastPatchState, lastWireState, lastPrepassState, lastLateState, lastLateEqualState;

    /// <summary>
    /// The state the patches draw with: the pass's (its targets' depth test, write and compare, blending, colour mask, depth clamp, bias, as GL
    /// has them for the host) with the terrain's own culling: back faces, counter-clockwise front (what the GL version set before every draw).
    /// The outline draws lines with a depth bias of (-1, -1). Kept per input state (records compare by value: no allocation in the steady state).
    /// </summary>
    DrawState PatchState(DrawState pass, PatchMode mode)
    {
        if (lastGlState is null || pass != lastGlState)
        {
            lastGlState = pass;
            lastPatchState = pass with { Cull = Silk.NET.Vulkan.CullModeFlags.BackBit, Front = GlConventions.FrontFace(FrontFaceDirection.Ccw) };
            lastWireState = lastPatchState with { Polygon = Silk.NET.Vulkan.PolygonMode.Line, BiasEnable = true, BiasConstant = -1, BiasSlope = -1 };
            lastPrepassState = lastPatchState with { ColourMask = 0 };   // depth only
            lastLateState = lastPatchState with { DepthWrite = false };   // the prepass wrote it; equal depths pass (less-or-equal)
            lastLateEqualState = lastLateState with { Compare = Silk.NET.Vulkan.CompareOp.Equal };   // only where the prepass's own depth stayed
        }
        return mode switch
        {
            PatchMode.Wireframe => lastWireState!, PatchMode.Prepass => lastPrepassState!, PatchMode.Late => lastLateState!, PatchMode.LateEqual => lastLateEqualState!,
            _ => lastPatchState!,
        };
    }

    // ---- the native model (docs/renderer-native.md 7.1, wave 3b agent B: step O) ----
    // Native programs (TerrainShaders' native variants: the same maths; the camera in ViewConstants, the terrain's values and the bindless
    // indices of its textures in TerrainConstants at set 2, a patch's node in push constants), recorded into the pass VkGl has open. Per
    // segment: the frame set (NativeFrame), the terrain set, the dynamic state; per draw only commands (a patch: a push and a draw; a mesh
    // group: its pipeline, buffers and draw, kept per mesh while the vertex-array stamp holds).

    readonly NativeFrame nativeFrame;
    readonly Silk.NET.Vulkan.DescriptorSetLayout constantsLayout;
    readonly TerrainProgram patchDepth, patchPrepass, patchPrepassCut, meshDepth;
    readonly TerrainProgram? patchPrepassTess, patchPrepassCutTess, meshDepthTess;   // null without tessellation shaders
    // The colour programs, from the set the parallax setting asks for (UseColour): meshSolid is meshColour without the cross-fade discard (EarlyDepth).
    TerrainProgram patchColour, patchColourCut, meshColour, meshSolid;
    TerrainProgram? patchColourTess, patchColourCutTess, meshColourTess, meshSolidTess;
    readonly Silk.NET.Vulkan.DescriptorSetLayout[] sets;

    /// <summary>The colour programs, with the parallax's march (<see cref="GroundRelief.ParallaxActive"/>) or without it (<see cref="TerrainShaders.FragmentNative"/>).</summary>
    sealed record ColourSet(bool March, TerrainProgram Patch, TerrainProgram PatchCut, TerrainProgram Mesh, TerrainProgram MeshSolid,
        TerrainProgram? PatchTess, TerrainProgram? PatchCutTess, TerrainProgram? MeshTess, TerrainProgram? MeshSolidTess) : IDisposable
    {
        public void Dispose()
        {
            foreach (var p in new[] { Patch, PatchCut, Mesh, MeshSolid, PatchTess, PatchCutTess, MeshTess, MeshSolidTess }) p?.Dispose();
        }
    }

    readonly ColourSet?[] colourSets = new ColourSet?[2];

    /// <summary>
    /// Draws with the colour set of <paramref name="march"/>, made the first time it is asked for (at load the setting's; a toggle in play makes the other once, and
    /// keeps both: switching back costs nothing).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(patchColour), nameof(patchColourCut), nameof(meshColour), nameof(meshSolid))]
    void UseColour(bool march)
    {
        var set = colourSets[march ? 1 : 0] ??= MakeColour(march);
        (patchColour, patchColourCut, meshColour, meshSolid) = (set.Patch, set.PatchCut, set.Mesh, set.MeshSolid);
        (patchColourTess, patchColourCutTess, meshColourTess, meshSolidTess) = (set.PatchTess, set.PatchCutTess, set.MeshTess, set.MeshSolidTess);
    }

    ColourSet MakeColour(bool march)
    {
        bool tess = gpu.Device.HasTessellation;
        TerrainProgram Patch(string fragment, string name) => new(gpu, sets, TerrainShaders.PatchVertexNative(), fragment, name);
        TerrainProgram? PatchTess(string fragment, string name) => tess
            ? new(gpu, sets, TerrainShaders.PatchVertexTessNative(), fragment, name, TerrainShaders.PatchControlNative(), TerrainShaders.PatchEvaluationNative()) : null;
        TerrainProgram Mesh(string fragment, string name) => new(gpu, sets, TerrainShaders.MeshVertexNative(), fragment, name);
        TerrainProgram? MeshTess(string fragment, string name) => tess
            ? new(gpu, sets, TerrainShaders.MeshVertexTessNative(), fragment, name, TerrainShaders.MeshControlNative(), TerrainShaders.MeshEvaluationNative()) : null;
        return new ColourSet(march,
            Patch(TerrainShaders.FragmentNative(march: march), "terrain"),
            Patch(TerrainShaders.FragmentNative(interiorCut: true, march: march), "terrain interior cut"),
            Mesh(TerrainShaders.MeshFragmentNative(march), "terrain meshes"),
            Mesh(TerrainShaders.MeshFragmentSolidNative(march), "terrain meshes solid"),
            PatchTess(TerrainShaders.FragmentNative(march: march), "terrain relief"),
            PatchTess(TerrainShaders.FragmentNative(interiorCut: true, march: march), "terrain relief interior cut"),
            MeshTess(TerrainShaders.MeshFragmentNative(march), "terrain meshes smoothed"),
            MeshTess(TerrainShaders.MeshFragmentSolidNative(march), "terrain meshes solid smoothed"));
    }

    /// <summary>A mesh program, or its tessellated twin while the rocks are smoothed (<see cref="RockSmoothing.TessellateActive"/>).</summary>
    TerrainProgram Smoothed(TerrainProgram p, TerrainProgram? tessellated) => RockSmoothing.TessellateActive && tessellated is not null ? tessellated : p;
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

        public TerrainProgram(GpuContext ctx, Silk.NET.Vulkan.DescriptorSetLayout[] sets, string vertex, string fragment, string name,
            string? control = null, string? evaluation = null)
        {
            this.ctx = ctx;
            P = control is null || evaluation is null
                ? ctx.Shaders.Native(vertex, fragment, name, sets, NativeShaders.PushBytes)
                : ctx.Shaders.NativeTessellated(vertex, control, evaluation, fragment, name, sets, NativeShaders.PushBytes);
            var inputs = P.VertexReflection!.Inputs;
            locations = [.. inputs.SelectMany(i => Enumerable.Range(i.Location, i.Slots))];
            kinds = [.. inputs.SelectMany(i => Enumerable.Repeat(i.Kind, i.Slots))];
            Own = locations.Where(l => l < TerrainShaders.MeshInstanceLocation).DefaultIfEmpty(-1).Max() + 1;
            if (Own > 4) throw new NotSupportedException($"{name}: more than four own vertex inputs");
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

    /// <summary>The bindless index of a native texture of the terrain's (<see cref="TerrainTexture.Bindless"/>: a new index when its sampler
    /// changed), or the stand-in of <paramref name="kind"/>'s array when there is none.</summary>
    static uint Index(TerrainTexture? texture, BindlessKind kind, uint standIn)
    {
        if (texture is null) return standIn;
        var h = texture.Bindless();
        return h.Kind == kind ? h.Index : standIn;
    }

    /// <summary>
    /// <see cref="constants"/> for the next segment: the height functions' values and the height textures; with <paramref name="material"/>
    /// also the material's (<paramref name="patches"/>: the height field's normals, no feature; else <c>uFeature</c> 1) and the material
    /// textures. A value the GL version set only with textures (<c>uRegion</c>, <c>uCellGrid</c>) keeps its last one without, as a GL
    /// program's uniform did. In Prepare, on the render thread (bindless registrations).
    /// </summary>
    void PrepareConstants(bool material, bool patches)
    {
        var c = new TerrainConstants
        {
            CoarseRect = CoarseRect, CoarseCells = new Vector2(coarseSize - 1f, coarseSize - 1f), FineRect = FineRect,
            FineCells = new Vector2(fine.Columns - 1f, fine.Rows - 1f), FineBand = fineBand, HasFine = 1,
            HeightCoarse = Index(coarseTexture, BindlessKind.Texture2D, standIn2D),
            HeightFine = Index(fineTexture, BindlessKind.Texture2D, standIn2D),
            Diffuse = standInArray, Normal = standInArray, Height = standInArray, Relief = standIn2D, Blood = standInArray, Params = standIn2D, Cells = standInUInt, BlendMap = standIn2D, Overlay = standIn2D,
            Colour = standIn2D, Ground = standIn2D, WorldColour = standIn2D, Region = lastRegion, CellGrid = lastCellGrid,
            TessDetail = GroundRelief.TessDetail, RockRound = RockSmoothing.Roundness,   // the tessellation stages, the casters' too
            FieldTiles = standInUInt,
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
            c.Grid = options.Grid ? 1u : 0u;
            if (patches && TerrainLayerSkip.Enabled) c.CliffEps = TerrainLayerSkip.CliffEps;
            // The interior cut: the patches' cut variants, or a mesh draw given one (DrawMeshes, DrawMeshesIndirect).
            bool cut = patches ? cutCount + cutReliefCount > 0 : meshCut is { Active: true };
            c.InteriorMask = !cut ? standIn2D : patches ? cutMask : meshCut!.Index;
            c.InteriorCut = cut ? 1u : 0u;
            if (patches) Decals?.Apply(ref c);
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
                var set = t.Textures;
                c.Diffuse = Index(set.Diffuse, BindlessKind.Texture2DArray, standInArray);
                c.Normal = Index(set.Normal, BindlessKind.Texture2DArray, standInArray);
                c.Height = Index(set.Height, BindlessKind.Texture2DArray, standInArray);
                c.Relief = Index(set.Relief, BindlessKind.Texture2D, standIn2D);
                c.Parallax = textured && GroundRelief.ParallaxActive && set.Height is not null ? GroundRelief.Depth : 0;
                c.Displace = patches && textured && GroundRelief.DisplaceActive && patchColourTess is not null && set.Height is not null ? GroundRelief.Depth : 0;
                (c.DisplaceEnd, c.ParallaxEnd) = (GroundRelief.DisplaceDistance, GroundRelief.ParallaxDistance);
                (c.OverlayRelief, c.SelfShadow, c.TargetSlope) = (GroundRelief.OverlayRelief, GroundRelief.SelfShadow, GroundRelief.TargetSlope);
                (c.MaxDepth, c.ScaleFollow, c.CliffScale) = (GroundRelief.MaxDepth / GroundRelief.ReferenceDepth, GroundRelief.ScaleFollow, GroundRelief.CliffScale);
                (c.Refine, c.PixelScale, c.BroadCut) = (patches && patchColourTess is not null ? GroundRelief.RefinePixels : 0, options.TerrainPixelScale, GroundRelief.BroadCut);
                c.FieldTiles = Index(set.Field, BindlessKind.UTexture2D, standInUInt);
                if (patches && textured && t.FieldCount > 0) (c.FieldOrigin, c.FieldTile, c.FieldColumns, c.FieldCount) = (t.FieldOrigin, t.FieldTile, t.FieldColumns, t.FieldCount);
                c.Params = Index(set.Params, BindlessKind.Texture2D, standIn2D);
                c.Cells = Index(set.Cells, BindlessKind.UTexture2D, standInUInt);
                c.BlendMap = Index(set.BlendMap, BindlessKind.Texture2D, standIn2D);
                c.Overlay = Index(set.Overlay, BindlessKind.Texture2D, standIn2D);
                c.Colour = Index(set.Colour, BindlessKind.Texture2D, standIn2D);
                c.Ground = Index(set.Ground, BindlessKind.Texture2D, standIn2D);
                c.WorldColour = Index(set.WorldColour, BindlessKind.Texture2D, standIn2D);
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
        public GraphicsPipeline Pipeline = null!;
        public BufferBinding Vertex;
        public BufferBinding Elements;
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

    /// <summary>
    /// Records <see cref="patchDraws"/> into one native segment of the pass being drawn into (<see cref="PatchState"/>: back faces culled): the
    /// dynamic state, the sets, the pipeline, the grid's vertex and index buffers once; per node its push constants and the draw of its index
    /// range.
    /// </summary>
    void RecordPatches(TerrainProgram p, string label, PatchMode mode = PatchMode.Solid, int count = -1, PatchDraw[]? draws = null)
    {
        if (count < 0) count = nodes.Count;
        if (count == 0) return;
        draws ??= patchDraws;
        // Prepare (wave 4, docs/renderer-native.md 6.2): the pass's targets and state now, the pipeline, the sets, the draws, into a job.
        var targets = gpu.CurrentTargets();
        var state = PatchState(gpu.CurrentState(), mode);
        if (p.P.Tessellated) state = state with { Cull = Silk.NET.Vulkan.CullModeFlags.None };   // the raised triangles' winding is not checked: draw both sides
        var r = ResolvePatches(p, state, targets, label);
        var job = patchJobs.Rent();
        job.Owner = this;
        (job.Targets, job.State, job.Layout, job.Pipeline, job.Vertex, job.Elements) = (targets, state, p.Layout, r.Pipeline, r.Vertex, r.Elements);
        job.Stages = p.P.PushConstantStages;
        var view = View();
        job.Frame = nativeFrame.Prepare(in view);
        (job.ConstantSet, job.ConstantOffset) = PrepareConstantsBinding();
        if (job.Draws.Length < count) job.Draws = new PatchDraw[Math.Max(count, job.Draws.Length * 2)];
        draws.AsSpan(0, count).CopyTo(job.Draws);
        job.Count = count;
        gpu.Record(label, job);
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
        public Silk.NET.Vulkan.ShaderStageFlags Stages;   // the program's push range: a push names every stage of it
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
            var stages = Stages;
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

    /// <summary>The pipeline and bindings for <paramref name="p"/> in this pass state, kept while the state stays the same (the reflection's
    /// multisampled target alternates with the scene's: a few entries). The grid is the terrain's own vertex and index buffer.</summary>
    PatchPipeline ResolvePatches(TerrainProgram p, DrawState state, PassTargets targets, string label)
    {
        var segment = new SegmentPipeline(p.P, targets.Formats, state.Blend, state.ColourMask, state.Polygon, state.AlphaToCoverage, state.DepthClamp);
        foreach (var e in patchPipelines)
            if (e.Segment == segment) return e;
        if (patchPipelines.Count >= 16) patchPipelines.Clear();
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        attributes[0] = gridVertex;
        Span<BufferBinding> vertex = stackalloc BufferBinding[1];
        p.Buffers(attributes, vertex);
        var entry = new PatchPipeline
        {
            Segment = segment,
            Pipeline = gpu.Pipelines.Get(state.Pipeline(p.P, p.VertexLayout(attributes),
                p.P.Tessellated ? Silk.NET.Vulkan.PrimitiveTopology.PatchList : Silk.NET.Vulkan.PrimitiveTopology.TriangleList, targets.Formats, label)),
            Vertex = vertex[0], Elements = gridElements,
        };
        patchPipelines.Add(entry);
        return entry;
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
    /// The heights as frame globals (docs/renderer-native.md 4.3): the two textures (native, with the linear, clamped sampler their GL versions
    /// had) and the <see cref="TerrainShaders.HeightFunctions"/> uniforms, read when a consumer draws or applies its globals.
    /// </summary>
    sealed class ActiveTerrain { public TerrainRenderer? Owner; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GpuContext, ActiveTerrain> activeTerrain = new();

    internal void Activate()
    {
        var binding = activeTerrain.GetValue(gpu, static _ => new());
        if (ReferenceEquals(binding.Owner, this)) return;
        binding.Owner = this;
        PublishGlobals();
    }

    void PublishGlobals()
    {
        var g = gpu.Globals;
        g.Publish("uHeightCoarse", () => coarseTexture.Sampled());
        g.Publish("uHeightFine", () => fineTexture.Sampled());
        g.PublishUniform("uCoarseRect", () => CoarseRect);
        g.PublishUniform("uCoarseCells", () => new Vector2(coarseSize - 1f, coarseSize - 1f));
        g.PublishUniform("uFineRect", () => FineRect);
        g.PublishUniform("uFineCells", () => new Vector2(fine.Columns - 1f, fine.Rows - 1f));
        g.PublishUniform("uFineBand", () => fineBand);
        g.PublishUniform("uHasFine", () => 1);
        // The whole-world ground colour: the weather's dust takes its colour from it (docs/formats/weather.md "Dust").
        g.Publish("uWeatherGround", () => textures?.Textures.Ground is { } ground ? ground.Sampled() : default);
    }

    /// <summary>
    /// Draws other meshes with the terrain material (TERRAIN-mode map features), after <see cref="Draw"/> set the
    /// frame. Each item: a mesh with position at location 0 and normal at 1, its index count, its transform.
    /// As the game's <c>Feature_Terrain_DX11</c> (docs/formats/foliage.md, "TERRAIN-mode meshes"): one biome per mesh,
    /// the one of <c>biomemap.png</c> at the mesh's origin, back faces culled. Drawn instanced (<see cref="GroupMeshes"/>); returns
    /// the number of draw calls.
    /// </summary>
    public int DrawMeshes(List<(MeshBindings Mesh, int IndexCount, Matrix4x4 Model)> meshes, bool depth = false, InteriorMaskRenderer? cut = null)
    {
        if (depth) return DrawMeshesDepth(meshes);
        if (!GroupMeshes(meshes, biomes: true)) return 0;
        if (cut is { Active: true })
        {
            // Placements over an interior mask (the caller splits them out): the program with the cut, on for this draw.
            meshCut = cut;
            try { PrepareConstants(material: true, patches: false); }
            finally { meshCut = null; }
            return DrawGroups(Smoothed(meshColour, meshColourTess), Colour, "terrain meshes interior cut");
        }
        PrepareConstants(material: true, patches: false);
        // No placement of this path fades (row 1's w is 0): the program without the discard, so the depth test runs first (EarlyDepth).
        return EarlyDepth.Enabled ? DrawGroups(Smoothed(meshSolid, meshSolidTess), Solid, "terrain meshes solid")
            : DrawGroups(Smoothed(meshColour, meshColourTess), Colour, "terrain meshes");
    }

    /// <summary>The interior cut of the mesh draw being prepared (<see cref="DrawMeshes"/>, <see cref="DrawMeshesIndirect"/>; docs/render-interior-cut.md), null otherwise.</summary>
    InteriorMaskRenderer? meshCut;

    /// <summary>This call's placements (each group's contiguous, <see cref="MeshGroup.Offset"/> instances in), in the frame's constants.</summary>
    Transient placements;
    const int Colour = 0, Depth = 1, Solid = 2;
    readonly GpuContext gpu;

    // ---- depth only (the sun's shadow map, ShadowPass) ----
    bool depthReady;

    /// <summary>
    /// Draws the terrain's depth for a shadow cascade (<see cref="ShadowShaders.DepthFragment"/>, the caster bias): the main tree's
    /// patches chosen by the camera's <paramref name="eye"/> (so the casters' detail matches what the picture shows), culled by
    /// <paramref name="frustum"/>. Sets the frame's matrix for <see cref="DrawMeshes"/> with <c>depth</c>. <paramref name="coarseTexel"/>: the texel of a far cascade (world units; 0: not coarsened), which keeps the nodes from being split finer than <see cref="ShadowLod.TerrainFloorLevel"/>.
    /// </summary>
    public void DrawDepth(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, float coarseTexel = 0)
    {
        depthReady = true;
        quadtree.SetRanges(options.TerrainLod);
        frame = new Frame(viewProjection, eye, options, frame.Light);
        quadtree.FloorLevel = ShadowLod.TerrainFloorLevel(coarseTexel, quadtree.Spacing(0), TerrainLod.Roughness, quadtree.LevelCount);
        try { quadtree.Select(eye, bounds, (min, max) => WorldCamera.Intersects(frustum, min, max), nodes); }
        finally { quadtree.FloorLevel = 0; }
        PreparePatches(quadtree);
        if (nodes.Count > 0)
        {
            PrepareConstants(material: false, patches: true);
            RecordPatches(patchDepth, "terrain depth");
            DepthTriangles += patchTriangles;
        }
    }

    /// <summary>Terrain triangles the depth draws have drawn since the counter was last reset (by the caller).</summary>
    public long DepthTriangles { get; set; }

    int DrawMeshesDepth(List<(MeshBindings Mesh, int IndexCount, Matrix4x4 Model)> meshes)
    {
        if (!depthReady || !GroupMeshes(meshes, biomes: false)) return 0;   // the frame's matrix is the cascade's once DrawDepth has run
        PrepareConstants(material: false, patches: false);
        return DrawGroups(Smoothed(meshDepth, meshDepthTess), Depth, "terrain mesh depth");
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
        /// <summary>What a native draw of this mesh needs, per program.
        /// Inline (no object of its own), so a draw reads what grouping has just touched.</summary>
        public NativeMesh ColourNative, DepthNative, SolidNative;
        public ref NativeMesh Native(int kind) => ref kind == Colour ? ref ColourNative : ref (kind == Depth ? ref DepthNative : ref SolidNative);
    }

    /// <summary>The pipeline state a segment's draws share (everything of <see cref="GraphicsPipelineDesc"/> but the vertex layout).</summary>
    readonly record struct SegmentPipeline(ShaderProgram Program, AttachmentFormats Formats, BlendState Blend, Silk.NET.Vulkan.ColorComponentFlags Mask,
        Silk.NET.Vulkan.PolygonMode Polygon, bool AlphaToCoverage, bool DepthClamp);

    /// <summary>A mesh's own vertex buffers (locations 0 to 3: position, normal, and for the smoothed programs the tangent slot), bound with one call.</summary>
    [System.Runtime.CompilerServices.InlineArray(4)]
    struct OwnVertices { BufferBinding first; }

    /// <summary>A mesh resolved for one program: the mesh it came from, its vertex layout and own vertex buffers (locations 0 up to the
    /// placements), its indices, and its pipelines for the last two segment states (the reflection's multisampled target alternates with the
    /// scene's).</summary>
    struct NativeMesh
    {
        public MeshBindings? Source;
        /// <summary>The program the layout and buffers are for (the smoothed and the flat ones read different locations).</summary>
        public TerrainProgram? Program;
        public VertexLayout? Layout;
        public int VertexCount;
        public OwnVertices Vertices;
        public BufferBinding Elements;
        public int SegA, SegB;
        public GraphicsPipeline? PipeA, PipeB;
    }

    // Per program kind: the last segment state and its number; numbers are stable per state (a mesh compares one int per draw).
    readonly Dictionary<(int Kind, SegmentPipeline State), int> segmentByState = [];
    readonly SegmentPipeline[] lastSegment = new SegmentPipeline[3];
    readonly int[] segmentIds = new int[3];
    int segmentCount;

    int SegmentId(int kind, ShaderProgram p, PassTargets t, DrawState s)
    {
        var segment = new SegmentPipeline(p, t.Formats, s.Blend, s.ColourMask, s.Polygon, s.AlphaToCoverage, s.DepthClamp);
        if (segmentIds[kind] != 0 && segment == lastSegment[kind]) return segmentIds[kind];
        if (!segmentByState.TryGetValue((kind, segment), out int id)) segmentByState[(kind, segment)] = id = ++segmentCount;
        lastSegment[kind] = segment;
        return segmentIds[kind] = id;
    }

    readonly Dictionary<(MeshBindings, int, bool), MeshGroup> meshGroups = [];
    readonly List<MeshGroup> meshGroupList = [];

    /// <summary>
    /// Sorts the placements into <see cref="meshGroupList"/> and writes them into this frame's constants (<see cref="placements"/>; each one's
    /// matrix as its four rows; with <paramref name="biomes"/> row 0's w, which only feeds the position's unused w, carries the biome row).
    /// False when there is none.
    /// </summary>
    bool GroupMeshes(List<(MeshBindings Mesh, int IndexCount, Matrix4x4 Model)> meshes, bool biomes)
    {
        foreach (var g in meshGroupList) g.Count = 0;
        meshGroupList.Clear();
        if (meshGroups.Count > 4096) meshGroups.Clear();   // meshes come and go with streaming
        var items = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(meshes);
        if (groupOf.Length < items.Length) groupOf = new int[Math.Max(items.Length, groupOf.Length * 2)];
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
                if (!meshGroups.TryGetValue(key, out g)) meshGroups[key] = g = new MeshGroup { Mesh = item.Mesh, IndexCount = item.IndexCount, Mirrored = mirrored };
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
    int DrawGroups(TerrainProgram p, int kind, string label)
    {
        var job = OpenMeshSegment(p, kind, label, placements.Handle, placements.Offset, out var s);
        foreach (var g in meshGroupList)
        {
            ref var n = ref g.Native(kind);
            if (n.Source != g.Mesh || n.Program != p) Current(ref n, g.Mesh, p);
            var pipeline = n.SegA == s.Segment ? n.PipeA! : PipelineFor(ref n, p, s.Segment, s.State, s.Targets.Formats, label);
            job.Add(new MeshJob.Draw
            {
                Pipeline = pipeline, Front = g.Mirrored ? s.Cw : s.Ccw, Vertices = n.Vertices, VertexCount = n.VertexCount, Elements = n.Elements,
                IndexCount = (uint)g.IndexCount, Instances = (uint)g.Count, FirstInstance = (uint)g.Offset,
            });
        }
        gpu.Record(label, job);
        return meshGroupList.Count;
    }

    /// <summary>What the draws of a mesh segment share (<see cref="OpenMeshSegment"/>).</summary>
    struct MeshSegment
    {
        public PassTargets Targets;
        public DrawState State;
        public int Segment;
        public Silk.NET.Vulkan.FrontFace Ccw, Cw;
    }

    /// <summary>Opens a native segment for TERRAIN-mode meshes inside the host's pass: dynamic state (back faces culled, counter-clockwise),
    /// the frame and terrain sets, and the placements' rows from <paramref name="rows"/> at locations 7 to 10.</summary>
    MeshJob OpenMeshSegment(TerrainProgram p, int kind, string label, Silk.NET.Vulkan.Buffer rows, ulong rowsOffset, out MeshSegment s)
    {
        // Prepare (wave 4, docs/renderer-native.md 6.2): what the segment's commands need, into a job recorded by the caller.
        var targets = gpu.CurrentTargets();
        var state = gpu.CurrentState();
        s = new MeshSegment
        {
            Targets = targets, State = state, Segment = SegmentId(kind, p.P, targets, state),
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

    /// <summary>A TERRAIN-mode mesh drawn indirect (<see cref="DrawMeshesIndirect"/>): the vertex array (position at 0, normal at 1), its index
    /// count, and whether its placements mirror (the winding turns round), as <see cref="DrawMeshes"/> groups them.</summary>
    public readonly record struct IndirectMesh(MeshBindings Mesh, int IndexCount, bool Mirrored);

    /// <summary>
    /// <see cref="DrawMeshes"/> with placements and draw arguments made on the GPU (the foliage's GPU cull, docs/renderer-native.md 5.6.1): mesh
    /// <c>i</c> is drawn by the <c>VkDrawIndexedIndirectCommand</c> at <paramref name="argsOffset"/> + 20 i, its instances read from
    /// <paramref name="rows"/> (64 bytes each, the four rows of the matrix, row 0's w the biome row in colour, as <see cref="DrawMeshes"/>
    /// writes them) from <paramref name="rowsOffset"/>, with the argument's firstInstance. Same programs, state, and GL state left behind as
    /// <see cref="DrawMeshes"/>; the caller orders the meshes. Returns the number of draw calls.
    /// </summary>
    public int DrawMeshesIndirect(ReadOnlySpan<IndirectMesh> meshes, Silk.NET.Vulkan.Buffer rows, ulong rowsOffset, Silk.NET.Vulkan.Buffer args, ulong argsOffset, bool depth = false, bool solid = false,
        InteriorMaskRenderer? cut = null)
    {
        if (meshes.Length == 0) return 0;
        if (depth)
        {
            if (!depthReady) return 0;   // the frame's matrix is the cascade's once DrawDepth has run
            PrepareConstants(material: false, patches: false);
            return DrawIndirect(meshes, rows, rowsOffset, args, argsOffset, Smoothed(meshDepth, meshDepthTess), Depth, "terrain mesh depth");
        }
        bool solidDraw = solid && EarlyDepth.Enabled;
        // cut: the interior cut on in the program with the discard (the caller's cull lays the placements over a mask out with the fading ones).
        meshCut = solidDraw ? null : cut;
        try { PrepareConstants(material: true, patches: false); }
        finally { meshCut = null; }
        // solid: none of these placements is mid cross-fade (the caller splits them), so the program without the discard draws them (EarlyDepth).
        return solidDraw
            ? DrawIndirect(meshes, rows, rowsOffset, args, argsOffset, Smoothed(meshSolid, meshSolidTess), Solid, "terrain meshes solid")
            : DrawIndirect(meshes, rows, rowsOffset, args, argsOffset, Smoothed(meshColour, meshColourTess), Colour, "terrain meshes");
    }

    int DrawIndirect(ReadOnlySpan<IndirectMesh> meshes, Silk.NET.Vulkan.Buffer rows, ulong rowsOffset, Silk.NET.Vulkan.Buffer args, ulong argsOffset, TerrainProgram p, int kind, string label)
    {
        var job = OpenMeshSegment(p, kind, label, rows, rowsOffset, out var s);
        if (meshGroups.Count > 4096) meshGroups.Clear();   // meshes come and go with streaming
        for (int i = 0; i < meshes.Length; i++)
        {
            var m = meshes[i];
            // The mesh's native state lives with its group (shared with DrawMeshes' draws of the same mesh).
            var key = (m.Mesh, m.IndexCount, m.Mirrored);
            if (!meshGroups.TryGetValue(key, out var g)) meshGroups[key] = g = new MeshGroup { Mesh = m.Mesh, IndexCount = m.IndexCount, Mirrored = m.Mirrored };
            ref var n = ref g.Native(kind);
            if (n.Source != g.Mesh || n.Program != p) Current(ref n, g.Mesh, p);
            var pipeline = n.SegA == s.Segment ? n.PipeA! : PipelineFor(ref n, p, s.Segment, s.State, s.Targets.Formats, label);
            job.Add(new MeshJob.Draw
            {
                Pipeline = pipeline, Front = m.Mirrored ? s.Cw : s.Ccw, Vertices = n.Vertices, VertexCount = n.VertexCount, Elements = n.Elements,
                Args = args, ArgsOffset = argsOffset + (ulong)i * 20,
            });
        }
        gpu.Record(label, job);
        return meshes.Length;
    }

    /// <summary>A mesh's state for <paramref name="p"/>, made when the group meets another mesh: the layout and own vertex buffers, the
    /// placements as per-instance rows of 64 bytes at locations 7 to 10.</summary>
    static void Current(ref NativeMesh n, MeshBindings va, TerrainProgram p)
    {
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        va.Attributes.AsSpan().CopyTo(attributes);
        for (int a = 0; a < 4; a++)
            attributes[TerrainShaders.MeshInstanceLocation + a] = new LegacyProgram.Attribute(default, Silk.NET.Vulkan.Format.R32G32B32A32Sfloat, 64, true);
        n.Layout = p.VertexLayout(attributes);
        n.VertexCount = p.Own;
        p.Buffers(attributes, n.Vertices);
        n.Elements = va.Elements;
        n.Source = va;
        n.Program = p;
        n.SegA = n.SegB = 0;
        n.PipeA = n.PipeB = null;
    }

    GraphicsPipeline PipelineFor(ref NativeMesh n, TerrainProgram p, int segment, DrawState state, AttachmentFormats formats, string label)
    {
        if (n.SegA == segment) return n.PipeA!;
        if (n.SegB == segment) { (n.SegA, n.PipeA, n.SegB, n.PipeB) = (n.SegB, n.PipeB, n.SegA, n.PipeA); return n.PipeA!; }
        var topology = p.P.Tessellated ? Silk.NET.Vulkan.PrimitiveTopology.PatchList : Silk.NET.Vulkan.PrimitiveTopology.TriangleList;
        var pipeline = gpu.Pipelines.Get(state.Pipeline(p.P, n.Layout!, topology, formats, label));
        (n.SegB, n.PipeB) = (n.SegA, n.PipeA);
        (n.SegA, n.PipeA) = (segment, pipeline);
        return pipeline;
    }

    public void Dispose()
    {
        gridVertices.Dispose();
        gridIndices.Dispose();
        coarseTexture.Dispose();
        fineTexture.Dispose();
        foreach (var r in retired) r.Texture.Dispose();
        if (ownsTextures) textures?.Dispose();
        foreach (var p in new[] { patchDepth, patchPrepass, patchPrepassCut, meshDepth }) p.Dispose();
        foreach (var set in colourSets) set?.Dispose();
        patchPrepassTess?.Dispose();
        patchPrepassCutTess?.Dispose();
        meshDepthTess?.Dispose();
        rockBake?.Dispose();
        gpu.Bindless.Free(BindlessKind.Texture2D, standIn2D);
        gpu.Bindless.Free(BindlessKind.Texture2DArray, standInArray);
        gpu.Bindless.Free(BindlessKind.UTexture2D, standInUInt);
        nativeFrame.Dispose();
        var (vk, device, layout) = (gpu.Device.Vk, gpu.Device.Device, constantsLayout);
        gpu.Device.Frames.DeferDelete(() => vk.DestroyDescriptorSetLayout(device, layout, null));
    }
}
