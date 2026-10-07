using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Ogre;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;
using PolygonMode = Silk.NET.Vulkan.PolygonMode;
using Buffer = Silk.NET.Vulkan.Buffer;
using Texture = Meitou.Rendering.Gpu.Texture;

namespace Meitou.Rendering.Impostors;

/// <summary>The decoded meshes of a source and the sphere its frames cover.</summary>
public sealed class ImpostorMeshes
{
    public required Model Main { get; init; }
    public Model? Leaves { get; init; }
    /// <summary>The bounding sphere in object space: the bounding box's centre and the farthest vertex from it.</summary>
    public required Vector3 Centre { get; init; }
    public required float Radius { get; init; }

    /// <summary>Decodes the source's meshes (any thread); null when the main mesh cannot be read.</summary>
    public static ImpostorMeshes? Load(ImpostorSource source, List<string>? messages = null)
    {
        Model? Read(string? path)
        {
            if (path is null) return null;
            try { return Model.Build(OgreMeshReader.ReadFile(path), null); }
            catch (Exception e) when (e is OgreFormatException or EndOfStreamException or IOException or InvalidDataException)
            {
                if (messages is not null) lock (messages) messages.Add($"impostor {source.Name}: {Path.GetFileName(path)}: {e.Message}");
                return null;
            }
        }
        var main = Read(source.MeshPath);
        if (main is null || main.Parts.Count == 0) return null;
        return From(main, Read(source.LeavesPath));
    }

    /// <summary>The meshes with their bounding sphere (from the vertices of both).</summary>
    public static ImpostorMeshes From(Model main, Model? leaves)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var model in (ReadOnlySpan<Model?>)[main, leaves])
            if (model is not null)
                foreach (var part in model.Parts)
                    foreach (var v in part.Vertices) { min = Vector3.Min(min, v.Position); max = Vector3.Max(max, v.Position); }
        var centre = (min + max) / 2;
        float r2 = 0;
        foreach (var model in (ReadOnlySpan<Model?>)[main, leaves])
            if (model is not null)
                foreach (var part in model.Parts)
                    foreach (var v in part.Vertices) r2 = MathF.Max(r2, Vector3.DistanceSquared(v.Position, centre));
        return new ImpostorMeshes { Main = main, Leaves = leaves, Centre = centre, Radius = MathF.Max(MathF.Sqrt(r2) * 1.01f, 1e-3f) };
    }
}

/// <summary>How long the last bake took, by step (milliseconds; wall time on the render thread, and the CPU's filtering and encoding).</summary>
public readonly record struct ImpostorBakeTimes(double Upload, double Render, double Filter, double Encode)
{
    public double Total => Upload + Render + Filter + Encode;
}

/// <summary>
/// Bakes impostor atlases natively (phase 8 stage 2; docs/impostors.md, "The bake"): every frame of the grid is rendered orthographically along
/// its direction with the shared mesh shader (<see cref="ImpostorShaders.BakeFragmentNative"/>, the materials as <see cref="FoliageRenderer"/>
/// sets them), at twice the frame size, one row of frames and one map at a time into an RGBA8 target, halved by a linear blit and copied into a
/// readback buffer; <see cref="ImpostorAssembler"/> filters and encodes on the CPU. The GPU work of a row is recorded into the open frame's
/// <see cref="GpuFrame.PreFrame"/> (after the frame's uploads, so textures and meshes uploaded in the same frame are there) and read back once
/// that frame has completed: a bake is a <see cref="ImpostorBakeJob"/> stepped over frames (the viewer), or <see cref="Bake"/> driving the frames
/// itself (tools and tests). Render thread only.
/// </summary>
public sealed unsafe class ImpostorBaker : IDisposable
{
    readonly GpuContext gpu;
    readonly WorldTextureCache textures;
    readonly bool ownsTextures;
    readonly NativeFrame frame;
    readonly NativeProg program;
    uint standIn;

    /// <param name="assets">Where texture names are looked up; null only when no source names a texture (tests).</param>
    public ImpostorBaker(GpuContext gpu, AssetLocator? assets)
        : this(gpu, new WorldTextureCache(gpu, assets!, "impostor textures") { IdleSeconds = double.MaxValue, HighWaterMb = double.MaxValue }, owns: true)
    {
    }

    /// <summary>A baker that reads the materials' textures from <paramref name="textures"/> (the foliage's own cache: already resident).</summary>
    public ImpostorBaker(GpuContext gpu, WorldTextureCache textures) : this(gpu, textures, owns: false)
    {
    }

    ImpostorBaker(GpuContext gpu, WorldTextureCache textures, bool owns)
    {
        this.gpu = gpu;
        this.textures = textures;
        ownsTextures = owns;
        frame = new NativeFrame(gpu, extraStorage: 1);
        program = new NativeProg(gpu, frame, ImpostorShaders.BakeVertexNative(), ImpostorShaders.BakeFragmentNative(), "impostor bake");
    }

    public List<string> Messages => textures.Messages;
    internal WorldTextureCache Textures => textures;
    internal GpuContext Gpu => gpu;
    public ImpostorBakeTimes LastTimes { get; internal set; }
    /// <summary>Encode the maps (BC3 / BC5); false keeps them RGBA8 (debugging).</summary>
    public bool Compress { get; set; } = true;

    /// <summary>A bake to step (<see cref="ImpostorBakeJob.Step"/>): the materials' textures are asked for now.</summary>
    public ImpostorBakeJob Begin(ImpostorSource source, ImpostorMeshes meshes, ImpostorClass size) => new(this, source, meshes, size);

    /// <summary>
    /// Bakes <paramref name="source"/> at the size class <paramref name="size"/> (<see cref="ImpostorClass.For"/>), driving the frames itself:
    /// <paramref name="nextFrame"/> must submit the open frame, wait for it, and open the next (a frame must be open when this is called).
    /// </summary>
    public ImpostorAtlas Bake(ImpostorSource source, ImpostorMeshes meshes, ImpostorClass size, Action nextFrame)
    {
        var job = Begin(source, meshes, size);
        job.RowsPerStep = size.Grid;   // offline: every row in one frame
        while (true)
        {
            textures.Pump(wait: true, max: 64);
            if (job.Step()) break;
            if (job.NeedsFrame) nextFrame();
            else Thread.Sleep(1);   // the workers are filtering or encoding
        }
        return job.Result!;
    }

    // ---- recording (ImpostorBakeJob) ----

    internal struct Part
    {
        public DeviceBuffer Vertices, Indices;
        public BufferBinding[] Bindings;
        public VertexLayout Layout;
        public int Count;
        public bool HasColours;
    }

    internal sealed record Material(WorldTexture? Diffuse, WorldTexture? Normal, WorldTexture? Diffuse2, WorldTexture? Normal2, ImpostorMaterial Settings)
    {
        public IEnumerable<WorldTexture?> All => [Diffuse, Normal, Diffuse2, Normal2];
    }

    internal Material Resolve(ImpostorMaterial m) =>
        new(textures.Get(m.Diffuse, false), textures.Get(m.Normal, false), textures.Get(m.Diffuse2, false), textures.Get(m.Normal2, false), m);

    /// <summary>A model's parts in device buffers, written through the open frame's uploads.</summary>
    internal Part[] Upload(Model model)
    {
        var parts = new List<Part>();
        foreach (var mp in model.Parts)
        {
            if (mp.Indices.Length == 0) continue;
            var vertices = DeviceBuffer.Create(gpu, (ulong)(mp.Vertices.Length * Vertex.Size), BufferUse.Vertex, "impostor bake meshes");
            var indices = DeviceBuffer.Create(gpu, (ulong)(mp.Indices.Length * sizeof(uint)), BufferUse.Index, "impostor bake meshes");
            gpu.Uploads.Write(vertices, 0, MemoryMarshal.AsBytes(mp.Vertices.AsSpan()));
            gpu.Uploads.Write(indices, 0, MemoryMarshal.AsBytes(mp.Indices.AsSpan()));
            var attributes = FoliageRenderer.VertexAttributes(vertices);
            parts.Add(new Part
            {
                Vertices = vertices, Indices = indices, Count = mp.Indices.Length, HasColours = mp.HasColours,
                Layout = program.Layout(attributes), Bindings = program.Buffers(attributes, program.Own),
            });
        }
        return [.. parts];
    }

    static readonly DrawState BakeState = new(CullModeFlags.BackBit, FrontFace.Clockwise, true, true, CompareOp.Less, false, 0, 0, BlendState.Off,
        ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit, PolygonMode.Fill, false, false);

    /// <summary>GL's texture 0 (what the GL baker bound for a missing map) as a bindless index.</summary>
    uint StandIn()
    {
        if (standIn == 0) standIn = gpu.Bindless.Register(BindlessKind.Texture2D, gpu.Dummy(FrameGlobals.Sampler2D("impostor stand-in")));
        return standIn;
    }

    /// <summary>
    /// Records one row of frames for the three passes into <paramref name="cmd"/>: per pass the row at twice the frame size into
    /// <paramref name="large"/> (and <paramref name="depth"/>), halved into <paramref name="small"/> by a linear blit and copied into
    /// <paramref name="readback"/> at pass × the row's bytes.
    /// </summary>
    internal void RecordRow(CommandList cmd, int row, ImpostorClass size, ImpostorMeshes meshes, Part[] mainParts, Material main, Part[] leavesParts, Material? leaves,
        Texture large, Texture depth, Texture small, Buffer readback, float lodBias)
    {
        int grid = size.Grid, f = size.FramePixels, samples = 2 * f, width = grid * samples, smallWidth = grid * f;
        var c = meshes.Centre;
        float r = meshes.Radius;
        var formats = new AttachmentFormats(large.Desc.Format, depth.Desc.Format);
        var full = new Rect2D(default, new Extent2D((uint)width, (uint)samples));
        ulong rowBytes = (ulong)(smallWidth * f * 4);
        for (int pass = 0; pass < 3; pass++)
        {
            cmd.Barrier(BarrierBatch.Full);
            cmd.BeginRendering(new RenderingDesc(
                new RenderTarget(large.Attachment(), AttachmentLoadOp.Clear, new ClearValue(new ClearColorValue(0f, 0f, 0f, 0f)), large.Image),
                new RenderTarget(depth.Attachment(), AttachmentLoadOp.Clear, new ClearValue(depthStencil: new ClearDepthStencilValue(1f, 0)), depth.Image),
                width, samples));
            cmd.SetScissor(full);
            cmd.SetDepth(true, true, CompareOp.Less);
            cmd.SetDepthBias(false, 0, 0);
            for (int column = 0; column < grid; column++)
            {
                var d = ImpostorLayout.FrameDirection(column, row, grid);
                ImpostorLayout.Basis(d, out var right, out var up);
                var eye = c + d * (2 * r);
                var view = Matrix4x4.CreateLookAt(eye, c, up);
                var projection = Matrix4x4.CreateOrthographic(2 * r, 2 * r, 0.5f * r, 3.5f * r);
                cmd.SetViewport(new Viewport(column * samples, 0, samples, samples, 0, 1));
                var block = gpu.Frame.Constants.Write<ImpostorShaders.BakeBlock>([new ImpostorShaders.BakeBlock
                {
                    Sphere = new Vector4(c, r), Dir = new Vector4(d, 0), Right = new Vector4(right, 0), Up = new Vector4(up, 0), Pass = pass,
                }], 256);
                var constants = new ViewConstants { ViewProjection = view * projection, Eye = eye, LightDir = Vector3.UnitY, FogDistance = 0 };
                frame.Bind(cmd, program.P.Layout, in constants, [block.Binding]);
                Draw(cmd, mainParts, main, formats, lodBias);
                if (leaves is not null) Draw(cmd, leavesParts, leaves, formats, lodBias);
            }
            cmd.EndRendering();
            cmd.Barrier(BarrierBatch.Full);
            cmd.Blit(large, small, Filter.Linear);
            cmd.Barrier(BarrierBatch.Full);
            var region = new BufferImageCopy
            {
                BufferOffset = (ulong)pass * rowBytes,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D((uint)smallWidth, (uint)f, 1),
            };
            gpu.Device.Vk.CmdCopyImageToBuffer(cmd.Handle, small.Image, ImageLayout.General, readback, 1, &region);
        }
        var host = new BarrierBatch();
        host.Add(PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit, PipelineStageFlags2.HostBit, AccessFlags2.HostReadBit);
        cmd.Barrier(in host);
    }

    /// <summary>The material as the GL baker set it (<c>FoliageRenderer.DrawMesh</c>'s uniforms: textures and normal maps on), one draw per part.</summary>
    void Draw(CommandList cmd, Part[] parts, Material m, AttachmentFormats formats, float lodBias)
    {
        var s = m.Settings;
        uint stand = StandIn();
        uint diffuseKey = m.Diffuse?.Key ?? 0, normalKey = m.Normal?.Key ?? 0, diffuse2Key = m.Diffuse2?.Key ?? 0, normal2Key = m.Normal2?.Key ?? 0;
        bool textured = diffuseKey != 0;
        bool normal = textured && normalKey != 0;
        bool dual = textured && diffuse2Key != 0;
        bool cut = normal && s.AlphaThreshold > 0;
        // The textures at the detail the screen shows the mesh with at the transition (ImpostorClass.LodBias): its leaf alpha and colours as they thin out.
        uint Index(uint key) => key == 0 ? stand : textures.Index(key, lodBias, stand);
        var pc = new MeshPush
        {
            Tint = Vector3.One, TriplanarScale = 1f / 5000, AlphaChannel = 3, GreyChannel = -1, HeadDiffuse = stand, HeadNormal = stand,
            Diffuse = textured ? Index(diffuseKey) : stand, Normal = normal ? Index(normalKey) : stand,
            Diffuse2 = dual ? Index(diffuse2Key) : stand, Normal2 = dual && normal2Key != 0 ? Index(normal2Key) : stand,
            NormalSwizzled = normal && m.Normal!.Swizzled ? 1u : 0u, HasDiffuse = textured ? 1u : 0u, Triplanar = textured && s.Triplanar ? 1u : 0u,
            Tile = s.Tile, AlphaSource = cut ? 2 : 0, AlphaThreshold = cut ? s.AlphaThreshold : 0f, Specular = textured ? s.Specular : 0.3f,
            HasNormal = normal || cut ? 1u : 0u,
        };
        var state = BakeState with { Cull = s.DoubleSided ? CullModeFlags.None : CullModeFlags.BackBit };
        cmd.SetRaster(state.Cull, state.Front);
        foreach (var p in parts)
        {
            pc.HasDual = dual && p.HasColours ? 1u : 0u;
            pc.UseVertexColour = p.HasColours ? 1u : 0u;
            cmd.BindPipeline(gpu.Pipelines.Get(state.Pipeline(program.P, p.Layout, PrimitiveTopology.TriangleList, formats, "impostor bake")));
            cmd.BindVertexBuffers(0, p.Bindings);
            cmd.BindIndexBuffer(new BufferBinding(p.Indices.Handle, 0, p.Indices.Size), IndexType.Uint32);
            cmd.PushConstants(program.P.Layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, in pc);
            cmd.DrawIndexed((uint)p.Count);
        }
    }

    public void Dispose()
    {
        if (standIn != 0) gpu.Bindless.Free(BindlessKind.Texture2D, standIn);
        program.Dispose();
        frame.Dispose();
        if (ownsTextures) textures.Dispose();
    }
}

/// <summary>
/// One bake in progress (<see cref="ImpostorBaker.Begin"/>), stepped on the render thread with a frame open (<see cref="Step"/>): it waits for
/// the materials' textures, uploads the meshes and makes its targets, records <see cref="RowsPerStep"/> rows a frame into the frame's
/// <see cref="GpuFrame.PreFrame"/>, hands each row to the <see cref="ImpostorAssembler"/> on a worker once its frame has completed, and encodes
/// the atlas on a worker (<see cref="Result"/>).
/// </summary>
public sealed class ImpostorBakeJob : IDisposable
{
    readonly ImpostorBaker baker;
    readonly ImpostorSource source;
    readonly ImpostorMeshes meshes;
    readonly ImpostorClass size;
    readonly ImpostorBaker.Material main;
    readonly ImpostorBaker.Material? leaves;
    readonly ImpostorAssembler assembler;
    readonly Stopwatch watch = Stopwatch.StartNew();
    ImpostorBaker.Part[] mainParts = [], leavesParts = [];
    Texture? large, depth, small;
    readonly List<(int Row, long Frame, ReadbackBuffer Buffer)> rows = [];
    int recorded, assembled;
    Task? chain;
    Task<ImpostorAtlas>? finish;
    double upload, render, filter, encode;
    bool started, disposed;

    internal ImpostorBakeJob(ImpostorBaker baker, ImpostorSource source, ImpostorMeshes meshes, ImpostorClass size)
    {
        (this.baker, this.source, this.meshes, this.size) = (baker, source, meshes, size);
        main = baker.Resolve(source.Main);
        leaves = source.Leaves is { } l && meshes.Leaves is not null ? baker.Resolve(l) : null;
        assembler = new ImpostorAssembler(size.Grid, size.FramePixels, size.Levels);
        lodBias = size.LodBias(meshes.Radius * source.MeanScale);
    }

    /// <summary>The texture LOD bias the bake draws with (<see cref="ImpostorClass.LodBias"/>).</summary>
    public float LodBias => lodBias;
    readonly float lodBias;

    /// <summary>Rows recorded per <see cref="Step"/> (frame): 1 in the viewer (a few milliseconds of GPU time a frame), all of them offline.</summary>
    public int RowsPerStep { get; set; } = 1;
    public ImpostorSource Source => source;
    public ImpostorClass Size => size;
    /// <summary>The atlas, once <see cref="Step"/> returned true.</summary>
    public ImpostorAtlas? Result { get; private set; }
    /// <summary>A failure on a worker (the atlas is then null).</summary>
    public Exception? Error { get; private set; }

    /// <summary>Waiting for the GPU (a row to record or one whose frame has not completed), not for the workers.</summary>
    public bool NeedsFrame => !started || recorded < size.Grid || rows.Count > 0;

    /// <summary>All textures decoded (resident or missing): the bake can render.</summary>
    bool TexturesReady()
    {
        foreach (var m in (ReadOnlySpan<ImpostorBaker.Material?>)[main, leaves])
            if (m is not null)
                foreach (var t in m.All)
                    if (t is { State: WorldTexture.Residency.Loading or WorldTexture.Residency.Unloaded }) { _ = t.Key; return false; }
        return true;
    }

    /// <summary>One frame's work (a frame must be open); true when finished (<see cref="Result"/> or <see cref="Error"/>).</summary>
    public bool Step()
    {
        if (Result is not null || Error is not null) return true;
        var gpu = baker.Gpu;
        if (!started)
        {
            if (!TexturesReady()) return false;
            var t0 = watch.Elapsed.TotalMilliseconds;
            mainParts = baker.Upload(meshes.Main);
            leavesParts = leaves is not null ? baker.Upload(meshes.Leaves!) : [];
            int f = size.FramePixels, samples = 2 * f, width = size.Grid * samples;
            var pre = gpu.Frame.PreFrame.Handle;
            large = Texture.Create(gpu, new TextureDesc(Format.R8G8B8A8Unorm, width, samples, Use: TextureUse.ColourTarget | TextureUse.TransferSrc, Name: "impostor bake target"), pre);
            depth = Texture.Create(gpu, new TextureDesc(Format.D32Sfloat, width, samples, Use: TextureUse.DepthTarget, Name: "impostor bake target"), pre);
            small = Texture.Create(gpu, new TextureDesc(Format.R8G8B8A8Unorm, size.Grid * f, f, Use: TextureUse.TransferDst | TextureUse.TransferSrc, Name: "impostor bake target"), pre);
            started = true;
            upload += watch.Elapsed.TotalMilliseconds - t0;
        }
        // Rows whose frame has completed go to the assembler, in order, one after the other on a worker.
        while (rows.Count > 0 && ReadbackBuffer.Completed(gpu, rows[0].Frame))
        {
            var (row, _, buffer) = rows[0];
            rows.RemoveAt(0);
            int bytes = size.Grid * size.FramePixels * size.FramePixels * 4;
            var albedo = buffer.Read(0, (ulong)bytes).ToArray();
            var normal = buffer.Read((ulong)bytes, (ulong)bytes).ToArray();
            var depthMap = buffer.Read(2ul * (ulong)bytes, (ulong)bytes).ToArray();
            buffer.Dispose();
            var previous = chain;
            chain = Task.Run(() =>
            {
                previous?.Wait();
                var t = Stopwatch.GetTimestamp();
                assembler.AddRow(row, albedo, normal, depthMap);
                lock (this) filter += Stopwatch.GetElapsedTime(t).TotalMilliseconds;
            });
            assembled++;
        }
        if (recorded < size.Grid)
        {
            var t0 = watch.Elapsed.TotalMilliseconds;
            var cmd = gpu.Frame.PreFrame;
            cmd.BeginLabel("impostor bake");
            for (int k = 0; k < RowsPerStep && recorded < size.Grid; k++, recorded++)
            {
                ulong rowBytes = (ulong)(size.Grid * size.FramePixels * size.FramePixels * 4);
                var buffer = ReadbackBuffer.Create(gpu, 3 * rowBytes, "impostor bake readback");
                baker.RecordRow(cmd, recorded, size, meshes, mainParts, main, leavesParts, leaves, large!, depth!, small!, buffer.Handle, lodBias);
                rows.Add((recorded, gpu.Frame.Number, buffer));
            }
            cmd.EndLabel();
            cmd.Invalidate();   // the baker bound its own pipelines and sets
            render += watch.Elapsed.TotalMilliseconds - t0;
            return false;
        }
        if (assembled < size.Grid) return false;
        if (finish is null)
        {
            ReleaseGpu();
            var previous = chain;
            bool compress = baker.Compress;
            string name = $"{source.Name} ({Path.GetFileName(source.MeshPath)})";
            finish = Task.Run(() =>
            {
                previous?.Wait();
                var t = Stopwatch.GetTimestamp();
                var atlas = assembler.Finish(name, meshes.Centre, meshes.Radius, compress);
                lock (this) encode += Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                return atlas;
            });
        }
        if (!finish.IsCompleted) return false;
        if (finish.IsCompletedSuccessfully) Result = finish.Result;
        else Error = finish.Exception?.InnerException ?? new InvalidOperationException("impostor bake failed");
        lock (this) baker.LastTimes = new ImpostorBakeTimes(upload, render, filter, encode);
        return true;
    }

    /// <summary>Waits for the workers (offline: <see cref="ImpostorBaker.Bake"/> steps until done instead).</summary>
    void ReleaseGpu()
    {
        foreach (var p in mainParts.Concat(leavesParts)) { p.Vertices.Dispose(); p.Indices.Dispose(); }
        (mainParts, leavesParts) = ([], []);
        large?.Dispose();
        depth?.Dispose();
        small?.Dispose();
        (large, depth, small) = (null, null, null);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ReleaseGpu();
        foreach (var (_, _, b) in rows) b.Dispose();
        rows.Clear();
        try { finish?.Wait(); chain?.Wait(); } catch (AggregateException) { }
    }
}
