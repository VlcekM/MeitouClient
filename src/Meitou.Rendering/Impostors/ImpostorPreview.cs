using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;
using PolygonMode = Silk.NET.Vulkan.PolygonMode;

namespace Meitou.Rendering.Impostors;

/// <summary>
/// The impostor check (docs/impostors.md, "Preview"), native (phase 8 stage 2): draws instances of one mesh either as meshes, with the world's
/// foliage program (<see cref="FoliageShaders.MeshVertexNative"/>, instanced, the same material values as <see cref="FoliageRenderer"/>), or as
/// impostors with the impostor program (<see cref="ImpostorDraw"/>) on the same instance rows, so the two can be compared picture for picture.
/// Lighting is the mesh shader's direct path (no world sky): both go through the same formula from the same inputs. The caller records into
/// a rendering of its own (<see cref="Begin"/>); a frame must be open. Render thread only.
/// </summary>
internal sealed class ImpostorPreview : IDisposable
{
    readonly GpuContext gpu;
    readonly WorldTextureCache textures;
    readonly NativeFrame frame;
    readonly NativeProg meshProgram;
    readonly ImpostorDraw impostors;
    readonly List<(ImpostorBaker.Part[] Parts, Material Material)> meshes = [];
    ImpostorTextures? atlas;
    uint standIn;

    sealed record Material(WorldTexture? Diffuse, WorldTexture? Normal, WorldTexture? Diffuse2, WorldTexture? Normal2, ImpostorMaterial Settings);

    public ImpostorPreview(GpuContext gpu, AssetLocator assets)
    {
        this.gpu = gpu;
        textures = new WorldTextureCache(gpu, assets, "impostor textures") { IdleSeconds = double.MaxValue, HighWaterMb = double.MaxValue };
        frame = new NativeFrame(gpu);
        meshProgram = new NativeProg(gpu, frame, FoliageShaders.MeshVertexNative(), FoliageShaders.MeshFragmentNative(), "impostor preview meshes");
        impostors = new ImpostorDraw(gpu, frame);
    }

    /// <summary>Parallax step in the impostor sampling.</summary>
    public bool Parallax { get; set; }
    /// <summary>Blend the three frames instead of picking one per pixel.</summary>
    public bool Blend { get; set; }
    /// <summary>Unlit debug output of the impostor: 1 albedo, 2 normal, 3 coverage (0 lit).</summary>
    public int Debug { get; set; }
    /// <summary>Write the impostor's blended depth (<see cref="ImpostorShaders.FragmentWithDepth"/>).</summary>
    public bool DepthWrite { get; set; }

    uint StandIn()
    {
        if (standIn == 0) standIn = gpu.Bindless.Register(BindlessKind.Texture2D, gpu.Dummy(FrameGlobals.Sampler2D("impostor stand-in")));
        return standIn;
    }

    /// <summary>The mesh (and leaves) to draw: buffers written through the open frame, textures loaded and uploaded (waits for them).</summary>
    public void SetMesh(ImpostorSource source, ImpostorMeshes model)
    {
        Material Resolve(ImpostorMaterial m) => new(textures.Get(m.Diffuse, false), textures.Get(m.Normal, false), textures.Get(m.Diffuse2, false), textures.Get(m.Normal2, false), m);
        meshes.Add((Upload(model.Main), Resolve(source.Main)));
        if (model.Leaves is not null && source.Leaves is not null) meshes.Add((Upload(model.Leaves), Resolve(source.Leaves)));
        while (textures.PendingCount > 0) textures.Pump(wait: true, max: 64);
    }

    ImpostorBaker.Part[] Upload(Model model)
    {
        var parts = new List<ImpostorBaker.Part>();
        foreach (var mp in model.Parts)
        {
            if (mp.Indices.Length == 0) continue;
            var vertices = DeviceBuffer.Create(gpu, (ulong)(mp.Vertices.Length * Vertex.Size), BufferUse.Vertex, "impostor preview meshes");
            var indices = DeviceBuffer.Create(gpu, (ulong)(mp.Indices.Length * sizeof(uint)), BufferUse.Index, "impostor preview meshes");
            gpu.Uploads.Write(vertices, 0, MemoryMarshal.AsBytes(mp.Vertices.AsSpan()));
            gpu.Uploads.Write(indices, 0, MemoryMarshal.AsBytes(mp.Indices.AsSpan()));
            Span<LegacyProgram.Attribute?> attributes = new LegacyProgram.Attribute?[16];
            FoliageRenderer.VertexAttributes(vertices).AsSpan().CopyTo(attributes);
            for (int a = 0; a < 4; a++)
                attributes[FoliageShaders.InstanceLocation + a] = new LegacyProgram.Attribute(default, Format.R32G32B32A32Sfloat, 64, true);
            parts.Add(new ImpostorBaker.Part
            {
                Vertices = vertices, Indices = indices, Count = mp.Indices.Length, HasColours = mp.HasColours,
                Layout = meshProgram.Layout(attributes), Bindings = meshProgram.Buffers(attributes, meshProgram.Own),
            });
        }
        return [.. parts];
    }

    public void SetAtlas(ImpostorAtlas baked)
    {
        atlas?.Dispose();
        using var batch = gpu.Uploads.Begin();
        atlas = new ImpostorTextures(gpu, baked, batch);
        atlas.Upload(batch);
    }

    /// <summary>The instance rows in the frame's constants, bound at locations 7 to 10 (row 0 w of each matrix: the fade, 2 = whole).</summary>
    BufferBinding[] Rows(ReadOnlySpan<Matrix4x4> instances)
    {
        var data = gpu.Frame.Constants.Write(instances, 16);
        return [.. Enumerable.Range(0, 4).Select(a => new BufferBinding(data.Handle, data.Offset + (ulong)(16 * a)))];
    }

    static readonly DrawState MeshState = new(CullModeFlags.BackBit, FrontFace.Clockwise, true, true, CompareOp.Less, false, 0, 0, BlendState.Off,
        ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit, PolygonMode.Fill, false, false);

    /// <summary>The instances as meshes into the open rendering of <paramref name="cmd"/> (formats <paramref name="formats"/>), in <paramref name="viewport"/>.</summary>
    public void DrawMeshes(CommandList cmd, AttachmentFormats formats, Viewport viewport, Matrix4x4 viewProjection, Vector3 eye, Vector3 light, ReadOnlySpan<Matrix4x4> instances, bool coverage)
    {
        var rows = Rows(instances);
        var view = new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = light, FogDistance = 0 };
        cmd.SetViewport(viewport);
        cmd.SetDepth(true, true, CompareOp.Less);
        cmd.SetDepthBias(false, 0, 0);
        frame.Bind(cmd, meshProgram.P.Layout, in view);
        cmd.BindVertexBuffers(FoliageShaders.InstanceLocation, rows);
        uint stand = StandIn();
        uint Index(uint key) => key == 0 ? stand : textures.Index(key, 0, stand);
        foreach (var (parts, m) in meshes)
        {
            var s = m.Settings;
            uint diffuseKey = m.Diffuse?.Key ?? 0, normalKey = m.Normal?.Key ?? 0, diffuse2Key = m.Diffuse2?.Key ?? 0, normal2Key = m.Normal2?.Key ?? 0;
            bool textured = diffuseKey != 0;
            bool normal = textured && normalKey != 0;
            bool dual = textured && diffuse2Key != 0;
            bool cut = normal && s.AlphaThreshold > 0;
            var pc = new MeshPush
            {
                Tint = Vector3.One, TriplanarScale = 1f / 5000, AlphaChannel = 3, GreyChannel = -1, HeadDiffuse = stand, HeadNormal = stand,
                Diffuse = textured ? Index(diffuseKey) : stand, Normal = normal ? Index(normalKey) : stand,
                Diffuse2 = dual ? Index(diffuse2Key) : stand, Normal2 = dual && normal2Key != 0 ? Index(normal2Key) : stand,
                NormalSwizzled = normal && m.Normal!.Swizzled ? 1u : 0u, HasDiffuse = textured ? 1u : 0u, Triplanar = textured && s.Triplanar ? 1u : 0u,
                Tile = s.Tile, AlphaSource = cut ? 2 : 0, AlphaThreshold = cut ? s.AlphaThreshold : 0f, Specular = textured ? s.Specular : 0.3f,
                HasNormal = normal || cut ? 1u : 0u, Coverage = coverage ? 1u : 0u,
            };
            var state = MeshState with { Cull = s.DoubleSided ? CullModeFlags.None : CullModeFlags.BackBit, AlphaToCoverage = coverage };
            cmd.SetRaster(state.Cull, state.Front);
            foreach (var p in parts)
            {
                pc.HasDual = dual && p.HasColours ? 1u : 0u;
                pc.UseVertexColour = p.HasColours ? 1u : 0u;
                cmd.BindPipeline(gpu.Pipelines.Get(state.Pipeline(meshProgram.P, p.Layout, PrimitiveTopology.TriangleList, formats, "impostor preview meshes")));
                cmd.BindVertexBuffers(0, p.Bindings);
                cmd.BindIndexBuffer(new BufferBinding(p.Indices.Handle, 0, p.Indices.Size), IndexType.Uint32);
                cmd.PushConstants(meshProgram.P.Layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, in pc);
                cmd.DrawIndexed((uint)p.Count, (uint)instances.Length);
            }
        }
    }

    /// <summary>The instances as impostors: one billboard each, the camera's up axis for the billboards' roll.</summary>
    public void DrawImpostors(CommandList cmd, AttachmentFormats formats, Viewport viewport, Matrix4x4 viewProjection, Vector3 eye, Vector3 cameraUp, Vector3 light,
        ReadOnlySpan<Matrix4x4> instances, bool coverage)
    {
        if (atlas is null) return;
        var rows = Rows(instances);
        var view = new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = light, FogDistance = 0 };
        var state = MeshState with { Cull = CullModeFlags.None, AlphaToCoverage = coverage };
        cmd.SetViewport(viewport);
        cmd.SetRaster(state.Cull, state.Front);
        cmd.SetDepth(true, true, CompareOp.Less);
        cmd.SetDepthBias(false, 0, 0);
        frame.Bind(cmd, impostors.Layout, in view);
        var a = atlas.Atlas;
        var pc = new ImpostorPush
        {
            Sphere = new Vector4(a.Centre, a.Radius), CameraUp = cameraUp, Grid = a.Grid,
            Albedo = atlas.Index(0, 0), Normal = atlas.Index(1, 0), Depth = atlas.Index(2, 0),
            Parallax = Parallax ? 1u : 0u, Blend = Blend ? 1u : 0u, Debug = Debug, Coverage = coverage ? 1u : 0u,
        };
        cmd.BindPipeline(impostors.Pipeline(DepthWrite ? ImpostorProgram.DepthWrite : ImpostorProgram.Plain, state, formats));
        cmd.BindVertexBuffers(FoliageShaders.InstanceLocation, rows);
        cmd.BindIndexBuffer(impostors.Quad, IndexType.Uint32);
        cmd.PushConstants(impostors.Layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, in pc);
        cmd.DrawIndexed(6, (uint)instances.Length);
    }

    public void Dispose()
    {
        atlas?.Dispose();
        foreach (var (parts, _) in meshes)
            foreach (var p in parts) { p.Vertices.Dispose(); p.Indices.Dispose(); }
        if (standIn != 0) gpu.Bindless.Free(BindlessKind.Texture2D, standIn);
        meshProgram.Dispose();
        impostors.Dispose();
        frame.Dispose();
        textures.Dispose();
    }
}
