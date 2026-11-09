using System.Numerics;
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
sealed unsafe class TerrainRockBake : IDisposable
{
    /// <summary>
    /// The renderer whose material the rocks take: its constants and constant sets.
    /// </summary>
    readonly TerrainRenderer _terrain;

    /// <summary>
    /// The context the program is made on.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// The terrain's frame set, which the bake binds as set 0.
    /// </summary>
    readonly NativeFrame _nativeFrame;

    /// <summary>
    /// The terrain set's layout (set 2), for the bake program.
    /// </summary>
    readonly GpuSetLayout _constantsLayout;

    /// <summary>
    /// The alignment of a constants slice.
    /// </summary>
    readonly ulong _uniformAlign;

    /// <summary>
    /// The bake program, made at the first bake.
    /// </summary>
    TerrainProgram? _rockBake;

    /// <summary>
    /// The bake for <paramref name="terrain"/>'s material; the program is made on first use.
    /// </summary>
    public TerrainRockBake(TerrainRenderer terrain, IGpuContext gpu, NativeFrame nativeFrame, GpuSetLayout constantsLayout, ulong uniformAlign)
    {
        _terrain = terrain;
        _gpu = gpu;
        _nativeFrame = nativeFrame;
        _constantsLayout = constantsLayout;
        _uniformAlign = uniformAlign;
    }

    /// <summary>
    /// The bake program, made the first time.
    /// </summary>
    TerrainProgram RockBakeProgram()
    {
        if (_rockBake is not null) return _rockBake;
        GpuSetLayout[] sets = [_nativeFrame.SetLayout, GpuSetLayout.Bindless, _constantsLayout];
        return _rockBake = new TerrainProgram(_gpu, sets, TerrainShaders.RockBakeVertexNative(), TerrainShaders.RockBakeFragmentNative(),
            "terrain rock bake");
    }

    /// <summary>
    /// The vertex layout and buffers of a rock's mesh part for the bake program (positions at 0, normals at 1, the placement's rows at 7 to 10).
    /// </summary>
    public (VertexLayout Layout, GpuBufferBinding[] Bindings) Vertices(DeviceBuffer vertices)
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
    /// The placement of a bake: <paramref name="scale"/> uniformly, the biome row in row 0 w (the layout of the foliage cull's compact kernel).
    /// </summary>
    static Matrix4x4 BakePlacement(float scale, int row) => new(scale, 0, 0, row, 0, scale, 0, 0, 0, 0, scale, 0, 0, 0, 0, 1);

    /// <summary>
    /// Records one frame of a rock bake into <paramref name="cmd"/> (inside the baker's open rendering): the rock's parts drawn orthographically along
    /// <paramref name="dir"/> with the biome's material, writing the output of <paramref name="pass"/> (0 albedo, 1 normal in the frame's basis, 2
    /// gloss). <paramref name="distance"/> is the distance the material is shaded for (its fade with distance), <paramref name="scale"/> the
    /// placement's.
    /// </summary>
    public void Record(IGpuCommandList cmd, AttachmentFormats formats, DrawState state, int row, float scale, float distance,
        in Matrix4x4 viewProjection, Vector3 eye, Vector3 dir, Vector3 right, Vector3 up, int pass, ImpostorBaker.Part[] parts)
    {
        var p = RockBakeProgram();
        var view = new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = Vector3.UnitY, FogDistance = 0 };
        var binding = _nativeFrame.Prepare(in view);
        NativeFrame.Record(cmd, p.P, in binding);
        var constants = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<TerrainConstants>([_terrain.BakeConstants(row)]));
        var constantsSlice = _gpu.Frame.WriteConstants(constants, _uniformAlign);
        var set = _terrain.ConstantSetOf(constantsSlice.Handle);
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

    /// <summary>
    /// Frees the program.
    /// </summary>
    public void Dispose() => _rockBake?.Dispose();
}
