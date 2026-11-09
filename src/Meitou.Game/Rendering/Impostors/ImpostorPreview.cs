using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Ogre;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Sky;
using Silk.NET.Vulkan;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;

namespace Meitou.Game.Rendering.Impostors;

/// <summary>
/// The impostor check (docs/impostors.md, "Preview"), native (phase 8 stage 2): draws instances of one mesh either as meshes, with the world's
/// foliage program (<see cref="FoliageShaders.MeshVertexNative"/>, instanced, the same material values as <see cref="FoliageRenderer"/>), or as
/// impostors with the impostor program (<see cref="ImpostorDraw"/>) on the same instance rows, so the two can be compared picture for picture.
/// Lighting is the mesh shader's direct path (no world sky): both go through the same formula from the same inputs. The caller records into a
/// rendering of its own (<see cref="Begin"/>); a frame must be open. Render thread only.
/// </summary>
internal sealed class ImpostorPreview : IDisposable
{
    /// <summary>
    /// The meshes' draw state: back faces culled, depth tested and written, no blending.
    /// </summary>
    static readonly DrawState MeshState = new(CullMode.Back, FrontFaceDirection.Ccw, true, true, DepthFunction.Less, false, 0, 0, BlendState.Off,
        ColorMask.R | ColorMask.G | ColorMask.B | ColorMask.A, PolygonKind.Fill, false, false);

    /// <summary>
    /// The device the preview draws on.
    /// </summary>
    readonly GpuContext _gpu;
    /// <summary>
    /// The meshes' textures, never unloaded while the preview lives.
    /// </summary>
    readonly WorldTextureCache _textures;
    /// <summary>
    /// The native sets both programs are made with.
    /// </summary>
    readonly NativeFrame _frame;
    /// <summary>
    /// The world's foliage mesh program.
    /// </summary>
    readonly NativeProg _meshProgram;
    /// <summary>
    /// The impostor programs.
    /// </summary>
    readonly ImpostorDraw _impostors;
    /// <summary>
    /// The uploaded mesh parts (main and leaves) with their materials.
    /// </summary>
    readonly List<(ImpostorBaker.Part[] Parts, Material Material)> _meshes = [];
    /// <summary>
    /// The atlas on the GPU (<see cref="SetAtlas"/>); null before.
    /// </summary>
    ImpostorTextures? _atlas;
    /// <summary>
    /// The bindless index of the stand-in texture, made on first use (0: not yet).
    /// </summary>
    uint _standIn;

    /// <summary>
    /// Makes the texture cache, the native sets and both programs.
    /// </summary>
    public ImpostorPreview(GpuContext gpu, AssetLocator assets)
    {
        this._gpu = gpu;
        _textures = new WorldTextureCache(gpu, assets, "impostor textures") { IdleSeconds = double.MaxValue, HighWaterMb = double.MaxValue };
        _frame = new NativeFrame(gpu);
        _meshProgram = new NativeProg(gpu, _frame, FoliageShaders.MeshVertexNative(), FoliageShaders.MeshFragmentNative(), "impostor preview meshes");
        _impostors = new ImpostorDraw(gpu, _frame);
    }

    /// <summary>
    /// A mesh part's textures and settings.
    /// </summary>
    sealed record Material(WorldTexture? Diffuse, WorldTexture? Normal, WorldTexture? Diffuse2, WorldTexture? Normal2, ImpostorMaterial Settings);

    /// <summary>
    /// Blend the three frames instead of picking one per pixel.
    /// </summary>
    public bool Blend { get; set; }

    /// <summary>
    /// Unlit debug output of the impostor: 1 albedo, 2 normal, 3 coverage (0 lit).
    /// </summary>
    public int Debug { get; set; }

    /// <summary>
    /// The mesh (and leaves) to draw: buffers written through the open frame, textures loaded and uploaded (waits for them).
    /// </summary>
    public void SetMesh(ImpostorSource source, ImpostorMeshes model)
    {
        Material Resolve(ImpostorMaterial m) =>
            new(_textures.Get(m.Diffuse, false), _textures.Get(m.Normal, false), _textures.Get(m.Diffuse2, false), _textures.Get(m.Normal2, false), m);

        _meshes.Add((Upload(model.Main), Resolve(source.Main)));
        if (model.Leaves is not null && source.Leaves is not null) _meshes.Add((Upload(model.Leaves), Resolve(source.Leaves)));
        while (_textures.PendingCount > 0) _textures.Pump(wait: true, max: 64);
    }

    /// <summary>
    /// Uploads <paramref name="baked"/> (all its levels at once), replacing the atlas shown before.
    /// </summary>
    public void SetAtlas(ImpostorAtlas baked)
    {
        _atlas?.Dispose();
        using var batch = _gpu.Uploads.Begin();
        _atlas = new ImpostorTextures(_gpu, baked, batch);
        _atlas.Upload(batch);
    }

    /// <summary>
    /// The instances as meshes into the open rendering of <paramref name="cmd"/> (formats <paramref name="formats"/>), in
    /// <paramref name="viewport"/>.
    /// </summary>
    public void DrawMeshes(IGpuCommandList cmd, AttachmentFormats formats, PixelViewport viewport, Matrix4x4 viewProjection, Vector3 eye, Vector3 light,
        ReadOnlySpan<Matrix4x4> instances, bool coverage)
    {
        var rows = Rows(instances);
        var view = new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = light, FogDistance = 0 };
        cmd.SetViewport(viewport);
        cmd.SetDepth(true, true, DepthFunction.Less);
        cmd.SetDepthBias(false, 0, 0);
        _frame.Bind(cmd, _meshProgram.P, in view);
        cmd.BindVertexBuffers(FoliageShaders.InstanceLocation, rows);
        uint stand = StandIn();
        foreach (var (parts, m) in _meshes) DrawMesh(cmd, formats, parts, m, stand, coverage, instances.Length);
    }

    /// <summary>
    /// The instances as impostors: one billboard each, the camera's up axis for the billboards' roll.
    /// </summary>
    public void DrawImpostors(IGpuCommandList cmd, AttachmentFormats formats, PixelViewport viewport, Matrix4x4 viewProjection, Vector3 eye,
        Vector3 cameraUp, Vector3 light, ReadOnlySpan<Matrix4x4> instances, bool coverage)
    {
        if (_atlas is null) return;

        var rows = Rows(instances);
        var view = new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = light, FogDistance = 0 };
        var state = MeshState with { Cull = CullMode.None, AlphaToCoverage = coverage };
        cmd.SetViewport(viewport);
        cmd.SetRaster(state.Cull, state.Front);
        cmd.SetDepth(true, true, DepthFunction.Less);
        cmd.SetDepthBias(false, 0, 0);
        _frame.Bind(cmd, _impostors.Program, in view);
        var a = _atlas.Atlas;
        var pc = new ImpostorPush
        {
            Sphere = new Vector4(a.Centre, a.Radius),
            CameraUp = cameraUp,
            Grid = a.Grid,
            Albedo = _atlas.Index(0, 0),
            Normal = _atlas.Index(1, 0),
            Gloss = a.Gloss,
            Blend = Blend ? 1u : 0u,
            Debug = Debug,
            Coverage = coverage ? 1u : 0u,
        };
        cmd.BindPipeline(_impostors.Pipeline(ImpostorProgram.Plain, state, formats));
        cmd.BindVertexBuffers(FoliageShaders.InstanceLocation, rows);
        cmd.BindIndexBuffer(_impostors.Quad, IndexKind.U32);
        cmd.PushConstants(_impostors.Program, GpuStage.Vertex | GpuStage.Fragment, in pc);
        cmd.DrawIndexed(6, (uint)instances.Length);
    }

    /// <summary>
    /// Frees the atlas, the mesh buffers, the stand-in, the programs, the sets and the texture cache.
    /// </summary>
    public void Dispose()
    {
        _atlas?.Dispose();
        foreach (var (parts, _) in _meshes)
        {
            foreach (var p in parts)
            {
                p.Vertices.Dispose();
                p.Indices.Dispose();
            }
        }

        if (_standIn != 0) _gpu.Bindless.Free(BindlessKind.Texture2D, _standIn);
        _meshProgram.Dispose();
        _impostors.Dispose();
        _frame.Dispose();
        _textures.Dispose();
    }

    /// <summary>
    /// The stand-in texture's bindless index, registered on first use.
    /// </summary>
    uint StandIn()
    {
        if (_standIn == 0) _standIn = _gpu.Bindless.Register(BindlessKind.Texture2D, _gpu.Dummy(FrameGlobals.Sampler2D("impostor stand-in")));
        return _standIn;
    }

    /// <summary>
    /// Uploads a model's non-empty parts with their vertex layout for the mesh program (instance rows at locations 7 to 10).
    /// </summary>
    ImpostorBaker.Part[] Upload(Model model)
    {
        var parts = new List<ImpostorBaker.Part>();
        foreach (var mp in model.Parts)
        {
            if (mp.Indices.Length == 0) continue;

            var packed = GpuVertex.PackObject(mp.Vertices);
            var vertices = DeviceBuffer.Create(_gpu, (ulong)packed.Length, BufferUse.Vertex, "impostor preview meshes");
            var indices = DeviceBuffer.Create(_gpu, (ulong)(mp.Indices.Length * sizeof(uint)), BufferUse.Index, "impostor preview meshes");
            _gpu.Uploads.Write(vertices, 0, packed);
            _gpu.Uploads.Write(indices, 0, MemoryMarshal.AsBytes(mp.Indices.AsSpan()));
            Span<LegacyProgram.Attribute?> attributes = new LegacyProgram.Attribute?[16];
            FoliageRenderer.VertexAttributes(vertices).AsSpan().CopyTo(attributes);
            for (int a = 0; a < 4; a++)
                attributes[FoliageShaders.InstanceLocation + a] = new LegacyProgram.Attribute(default, InternalFormat.Rgba32F, 64, true);

            parts.Add(new ImpostorBaker.Part
            {
                Vertices = vertices,
                Indices = indices,
                Count = mp.Indices.Length,
                HasColours = mp.HasColours,
                Layout = _meshProgram.Layout(attributes),
                Bindings = _meshProgram.Buffers(attributes, _meshProgram.Own),
            });
        }

        return [.. parts];
    }

    /// <summary>
    /// The instance rows in the frame's constants, bound at locations 7 to 10 (row 0 w of each matrix: the fade, 2 = whole).
    /// </summary>
    GpuBufferBinding[] Rows(ReadOnlySpan<Matrix4x4> instances)
    {
        var data = _gpu.Frame.Constants.Write(instances, 16);
        return [.. Enumerable.Range(0, 4).Select(a => new GpuBufferBinding(data.Handle, data.Offset + (ulong)(16 * a)))];
    }

    /// <summary>
    /// Draws <paramref name="count"/> instances of one mesh's parts with its material (alpha to coverage when <paramref name="coverage"/>).
    /// </summary>
    void DrawMesh(IGpuCommandList cmd, AttachmentFormats formats, ImpostorBaker.Part[] parts, Material m, uint stand, bool coverage, int count)
    {
        var state = MeshState with { Cull = m.Settings.DoubleSided ? CullMode.None : CullMode.Back, AlphaToCoverage = coverage };
        bool dual = (m.Diffuse?.Key ?? 0) != 0 && (m.Diffuse2?.Key ?? 0) != 0;
        var pc = PushFor(m, stand, coverage);
        cmd.SetRaster(state.Cull, state.Front);
        foreach (var p in parts)
        {
            pc.HasDual = dual && p.HasColours ? 1u : 0u;
            pc.UseVertexColour = p.HasColours ? 1u : 0u;
            cmd.BindPipeline(_gpu.Pipelines.Get(state.Pipeline(_meshProgram.P, p.Layout, PrimitiveType.Triangles, formats, "impostor preview meshes")));
            cmd.BindVertexBuffers(0, p.Bindings);
            cmd.BindIndexBuffer(new GpuBufferBinding(p.Indices.Handle, 0, p.Indices.Size), IndexKind.U32);
            cmd.PushConstants(_meshProgram.P, GpuStage.Vertex | GpuStage.Fragment, in pc);
            cmd.DrawIndexed((uint)p.Count, (uint)count);
        }
    }

    /// <summary>
    /// The mesh shader's push constants for a material (the texture indices resolved through the bindless table, the stand-in where nothing is
    /// bound).
    /// </summary>
    MeshPush PushFor(Material m, uint stand, bool coverage)
    {
        var s = m.Settings;
        uint diffuseKey = m.Diffuse?.Key ?? 0, normalKey = m.Normal?.Key ?? 0, diffuse2Key = m.Diffuse2?.Key ?? 0, normal2Key = m.Normal2?.Key ?? 0;
        bool textured = diffuseKey != 0;
        bool normal = textured && normalKey != 0;
        bool dual = textured && diffuse2Key != 0;
        bool cut = normal && s.AlphaThreshold > 0;
        return new MeshPush
        {
            Tint = Vector3.One,
            TriplanarScale = 1f / 5000,
            AlphaChannel = 3,
            GreyChannel = -1,
            HeadDiffuse = stand,
            HeadNormal = stand,
            Diffuse = textured ? IndexOf(diffuseKey, stand) : stand,
            Normal = normal ? IndexOf(normalKey, stand) : stand,
            Diffuse2 = dual ? IndexOf(diffuse2Key, stand) : stand,
            Normal2 = dual && normal2Key != 0 ? IndexOf(normal2Key, stand) : stand,
            NormalSwizzled = normal && m.Normal!.Swizzled ? 1u : 0u,
            HasDiffuse = textured ? 1u : 0u,
            Triplanar = textured && s.Triplanar ? 1u : 0u,
            Tile = s.Tile,
            AlphaSource = cut ? 2 : 0,
            AlphaThreshold = cut ? s.AlphaThreshold : 0f,
            Specular = textured ? s.Specular : 0.3f,
            HasNormal = normal || cut ? 1u : 0u,
            Coverage = coverage ? 1u : 0u,
            Spare = MeshSurface.NoWeather,
        };
    }

    /// <summary>
    /// A texture key's bindless index (no LOD bias), the stand-in for no key.
    /// </summary>
    uint IndexOf(uint key, uint stand) => key == 0 ? stand : _textures.Index(key, 0, stand);
}
