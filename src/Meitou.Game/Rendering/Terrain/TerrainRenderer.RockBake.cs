using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Impostors;
using Silk.NET.Vulkan;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;

namespace Meitou.Game.Rendering.Terrain;

/// <summary>
/// The impostor bake of TERRAIN-mode rocks (docs/impostors.md section 13): the terrain's own mesh material, drawn into the baker's targets. The
/// rock's biome is a row of the terrain's parameter texture; the program is the terrain mesh program with an output switch (<see
/// cref="TerrainShaders.RockBakeFragmentNative"/>), the rock is one identity-scaled placement whose row 0 w is the biome row, as the foliage's GPU
/// cull writes them.
/// </summary>
public sealed unsafe partial class TerrainRenderer
{
    TerrainProgram? _rockBake;

    TerrainProgram RockBakeProgram()
    {
        if (_rockBake is not null) return _rockBake;
        GpuSetLayout[] sets = [_nativeFrame.SetLayout, GpuSetLayout.Bindless, _constantsLayout];
        return _rockBake = new TerrainProgram(_gpu, sets, TerrainShaders.RockBakeVertexNative(), TerrainShaders.RockBakeFragmentNative(),
            "terrain rock bake");
    }

    /// <summary>
    /// True when the biome of parameter row <paramref name="row"/> has its layer textures resident now (the bake waits for it).
    /// </summary>
    internal bool CanBakeRock(int row) => _textures is { HasBiomes: true } t && t.IsResident(row);

    /// <summary>
    /// The identity of the biome's material for the atlas cache key (null: none). See <see cref="TerrainTextures.BiomeKey"/>.
    /// </summary>
    internal string? RockBiomeKey(int row) => _textures?.BiomeKey(row);

    /// <summary>
    /// The vertex layout and buffers of a rock's mesh part for the bake program (positions at 0, normals at 1, the placement's rows at 7 to 10).
    /// </summary>
    internal (VertexLayout Layout, GpuBufferBinding[] Bindings) RockBakeVertices(DeviceBuffer vertices)
    {
        var p = RockBakeProgram();
        var attributes = new LegacyProgram.Attribute?[TerrainShaders.MeshInstanceLocation + 4];
        FoliageRenderer.VertexAttributes(vertices).AsSpan().CopyTo(attributes);
        for (int a = 0; a < 4; a++)
            attributes[TerrainShaders.MeshInstanceLocation + a] = new LegacyProgram.Attribute(default, InternalFormat.Rgba32F, 64, true);
        var bindings = new GpuBufferBinding[p.Own];
        p.Buffers(attributes, bindings);
        return (p.VertexLayout(attributes), bindings);
    }

    /// <summary>
    /// The terrain's constants for a bake: the mesh material of the feature's biome <paramref name="row"/>, textured with normal maps, without the
    /// overlay and colour maps, the window or the far fade, nothing underwater.
    /// </summary>
    TerrainConstants BakeConstants(int row)
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
            if (t.HasGround) { flags |= 1; ground = Index(set.Ground, BindlessKind.Texture2D, _standIn2D); }
            if (t.HasWorldColour) { flags |= 2; worldColour = Index(set.WorldColour, BindlessKind.Texture2D, _standIn2D); }
            if (t.MapState == 2) { flags |= 4; colour = Index(set.Colour, BindlessKind.Texture2D, _standIn2D); region = t.Region; }
        }
        return (region, ground, worldColour, colour, (float)WorldLayout.HalfWorldSize, materialDistance * 0.8f, materialDistance, flags);
    }

    /// <summary>
    /// The placement of a bake: <paramref name="scale"/> uniformly, the biome row in row 0 w (the layout of the foliage cull's compact kernel).
    /// </summary>
    static Matrix4x4 BakePlacement(float scale, int row) => new(scale, 0, 0, row, 0, scale, 0, 0, 0, 0, scale, 0, 0, 0, 0, 1);

    /// <summary>
    /// Records one frame of a rock bake into <paramref name="cmd"/> (inside the baker's open rendering): the rock's parts drawn orthographically along
    /// <paramref name="dir"/> with the biome's material, writing the output of <paramref name="pass"/> (0 albedo, 1 normal in the frame's basis, 2
    /// gloss). <paramref name="distance"/> is the distance the material is shaded for (its fade with distance), <paramref name="scale"/> the placement's.
    /// </summary>
    internal void RecordRockBake(IGpuCommandList cmd, AttachmentFormats formats, DrawState state, int row, float scale, float distance,
        in Matrix4x4 viewProjection, Vector3 eye, Vector3 dir, Vector3 right, Vector3 up, int pass, ImpostorBaker.Part[] parts)
    {
        var p = RockBakeProgram();
        var view = new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = Vector3.UnitY, FogDistance = 0 };
        var binding = _nativeFrame.Prepare(in view);
        NativeFrame.Record(cmd, p.P, in binding);
        var constants = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<TerrainConstants>([BakeConstants(row)]));
        var constantsSlice = _gpu.Frame.WriteConstants(constants, _uniformAlign);
        ulong key = (ulong)constantsSlice.Handle.Index;
        if (!_constantSets.TryGetValue(key, out var set)) _constantSets[key] = set = ConstantSet(constantsSlice.Handle);
        uint offset = (uint)constantsSlice.Offset;
        cmd.BindSets(p.P, TerrainShaders.ConstantsSet, new ReadOnlySpan<GpuSet>(in set), new ReadOnlySpan<uint>(in offset));
        var rows = _gpu.Frame.WriteConstants(new byte[64], 64);
        *(Matrix4x4*)rows.Pointer = BakePlacement(scale, row);
        Span<GpuBufferBinding> instance = stackalloc GpuBufferBinding[4];
        for (int a = 0; a < 4; a++) instance[a] = new GpuBufferBinding(rows.Handle, rows.Offset + (ulong)(16 * a));
        cmd.SetRaster(state.Cull, state.Front);
        cmd.BindVertexBuffers(TerrainShaders.MeshInstanceLocation, instance);
        var push = new TerrainBakePush { Dir = new Vector4(dir, distance), Right = new Vector4(right, 0), Up = new Vector4(up, 0), Pass = pass };
        cmd.PushConstants(p.P, GpuStage.Vertex | GpuStage.Fragment, in push);
        foreach (var part in parts)
        {
            cmd.BindPipeline(_gpu.Pipelines.Get(state.Pipeline(p.P, part.Layout, PrimitiveType.Triangles, formats, "impostor rock bake")));
            cmd.BindVertexBuffers(0, part.Bindings);
            cmd.BindIndexBuffer(new GpuBufferBinding(part.Indices.Handle, 0, part.Indices.Size), IndexKind.U32);
            cmd.DrawIndexed((uint)part.Count);
        }
    }
}