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

    /// <summary>The triangles of the main mesh and the leaves mesh (the first level of detail, what a foliage instance draws).</summary>
    public int Triangles
    {
        get
        {
            long n = 0;
            foreach (var model in (ReadOnlySpan<Model?>)[Main, Leaves])
                if (model is not null)
                    foreach (var part in model.Parts) n += part.Indices.Length / 3;
            return (int)Math.Min(n, int.MaxValue);
        }
    }

    /// <summary>
    /// The area of the main mesh's triangles as a share of the bounding sphere's surface (4 pi r²): about 0.05 to 1 for a rock, a few thousandths for a stick
    /// that is a hundred times longer than thick, which no impostor can show (a billboard of thin parts loses them; docs/impostors.md section 13).
    /// </summary>
    public float SurfaceShare
    {
        get
        {
            double area = 0;
            foreach (var part in Main.Parts)
                for (int i = 0; i + 2 < part.Indices.Length; i += 3)
                {
                    var a = part.Vertices[part.Indices[i]].Position;
                    area += 0.5 * Vector3.Cross(part.Vertices[part.Indices[i + 1]].Position - a, part.Vertices[part.Indices[i + 2]].Position - a).Length();
                }
            return (float)(area / (4 * Math.PI * (double)Radius * Radius));
        }
    }

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

    /// <summary>The terrain whose mesh material TERRAIN-mode rocks are baked with (<see cref="ImpostorSource.IsRock"/>; docs/impostors.md section 13); null in the tools.</summary>
    public TerrainRenderer? RockTerrain { get; set; }

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
    internal Part[] Upload(Model model, bool rock = false)
    {
        var parts = new List<Part>();
        foreach (var mp in model.Parts)
        {
            if (mp.Indices.Length == 0) continue;
            var vertices = DeviceBuffer.Create(gpu, (ulong)(mp.Vertices.Length * Vertex.Size), BufferUse.Vertex, "impostor bake meshes");
            var indices = DeviceBuffer.Create(gpu, (ulong)(mp.Indices.Length * sizeof(uint)), BufferUse.Index, "impostor bake meshes");
            gpu.Uploads.Write(vertices, 0, MemoryMarshal.AsBytes(mp.Vertices.AsSpan()));
            gpu.Uploads.Write(indices, 0, MemoryMarshal.AsBytes(mp.Indices.AsSpan()));
            if (rock)
            {
                // A rock is drawn by the terrain's mesh program: its own vertex layout and buffers.
                var (rockLayout, rockBindings) = RockTerrain!.RockBakeVertices(vertices);
                parts.Add(new Part { Vertices = vertices, Indices = indices, Count = mp.Indices.Length, HasColours = mp.HasColours, Layout = rockLayout, Bindings = rockBindings });
                continue;
            }
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

    bool globalsPrepared;

    /// <summary>
    /// Evaluates the frame globals once, outside any rendering, before the first row. The first <see cref="NativeFrame.Prepare"/> of a run that has
    /// drawn no world frame yet (an offscreen run settling with a cold cache) makes the globals' owners upload their lazy textures (the shadow
    /// noise): recorded between a row's BeginRendering and EndRendering, that copy and its barrier were invalid commands (6 sync-validation errors).
    /// </summary>
    void PrepareFrameGlobals()
    {
        if (globalsPrepared) return;
        globalsPrepared = true;
        var block = gpu.Frame.Constants.Write<ImpostorShaders.BakeBlock>([default], 256);
        frame.Prepare(new ViewConstants { LightDir = Vector3.UnitY }, [block.Binding]);
    }

    /// <summary>
    /// Records one row of frames for the three passes into <paramref name="cmd"/>: per pass the row at twice the frame size into
    /// <paramref name="large"/> (and <paramref name="depth"/>), halved into <paramref name="small"/> by a linear blit and copied into
    /// <paramref name="readback"/> at pass × the row's bytes.
    /// </summary>
    internal void RecordRow(CommandList cmd, int row, ImpostorClass size, ImpostorMeshes meshes, Part[] mainParts, Material main, Part[] leavesParts, Material? leaves,
        Texture large, Texture depth, Texture small, Buffer readback, float lodBias, ImpostorSource? rock = null)
    {
        int grid = size.Grid, f = size.FramePixels, samples = 2 * f, width = grid * samples, smallWidth = grid * f;
        PrepareFrameGlobals();
        var c = meshes.Centre;
        float r = meshes.Radius;
        // A rock is baked at the mean scale of its instances (the terrain's material is mapped in world units, so its texture density follows the scale):
        // the cameras cover the scaled sphere; the atlas keeps the unscaled one (the runtime applies the instance's matrix).
        float rockScale = rock is null ? 1 : rock.MeanScale;
        (c, r) = (c * rockScale, r * rockScale);
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
                if (rock is not null)
                {
                    RockTerrain!.RecordRockBake(cmd, formats, BakeState, rock.RockBiome, rockScale, size.BakeDistance, view * projection, eye, d, right, up, pass, mainParts);
                    continue;
                }
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
            HasNormal = normal || cut ? 1u : 0u, Spare = MeshSurface.NoWeather,
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

    // ---- readback buffers: a row's three pictures, reused from row to row and bake to bake (render thread, except ReturnReadback) ----

    readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, System.Collections.Concurrent.ConcurrentBag<ReadbackBuffer>> readbacks = new();

    internal ReadbackBuffer RentReadback(ulong size)
    {
        var bag = readbacks.GetOrAdd(size, _ => new());
        return bag.TryTake(out var buffer) ? buffer : ReadbackBuffer.Create(gpu, size, "impostor bake readback");
    }

    /// <summary>Hands a buffer whose picture has been read back to the pool (any thread: the GPU is done with it).</summary>
    internal void ReturnReadback(ReadbackBuffer buffer) => readbacks.GetOrAdd(buffer.Size, _ => new()).Add(buffer);

    // ---- bake targets, kept from bake to bake: a bake takes a set of the atlas's size and gives it back (render thread) ----
    // Making and freeing the three images of every bake (367 bakes in a minute's fast flight) cost 0.5 to 3 ms of the render thread in a bake's first
    // step and, with the driver busy, 11 to 50 ms (2026-10-10, docs/renderer-native.md "Upload steps"). Every row clears them, so a set from an earlier bake is as good as a new one.

    /// <summary>Sets kept idle at most (the largest, 6144 x 512, is 28 MB); the oldest goes when another comes back.</summary>
    const int KeptTargetSets = 3;

    readonly List<((int Grid, int Frame) Key, Texture Large, Texture Depth, Texture Small)> targets = [];

    /// <summary>A colour target at twice the frame size, its depth and the half-size picture for the atlas size; made when none is kept.</summary>
    internal (Texture Large, Texture Depth, Texture Small) RentTargets(ImpostorClass size, CommandBuffer pre)
    {
        var key = (size.Grid, size.FramePixels);
        int at = targets.FindIndex(t => t.Key == key);
        if (at >= 0)
        {
            var kept = targets[at];
            targets.RemoveAt(at);
            return (kept.Large, kept.Depth, kept.Small);
        }
        int f = size.FramePixels, samples = 2 * f, width = size.Grid * samples;
        return (Texture.Create(gpu, new TextureDesc(Format.R8G8B8A8Unorm, width, samples, Use: TextureUse.ColourTarget | TextureUse.TransferSrc, Name: "impostor bake target"), pre),
            Texture.Create(gpu, new TextureDesc(Format.D32Sfloat, width, samples, Use: TextureUse.DepthTarget, Name: "impostor bake target"), pre),
            Texture.Create(gpu, new TextureDesc(Format.R8G8B8A8Unorm, size.Grid * f, f, Use: TextureUse.TransferDst | TextureUse.TransferSrc, Name: "impostor bake target"), pre));
    }

    /// <summary>Takes a bake's set back (the rows recorded into it are ahead of any later use in the queue).</summary>
    internal void ReturnTargets(ImpostorClass size, Texture large, Texture depth, Texture small)
    {
        if (targetsClosed)
        {
            large.Dispose();
            depth.Dispose();
            small.Dispose();
            return;
        }
        targets.Add(((size.Grid, size.FramePixels), large, depth, small));
        while (targets.Count > KeptTargetSets)
        {
            var oldest = targets[0];
            targets.RemoveAt(0);
            oldest.Large.Dispose();
            oldest.Depth.Dispose();
            oldest.Small.Dispose();
        }
    }

    /// <summary>Frees the pooled readback buffers and the kept bake targets (render thread, when no bake is running); the next bake makes new ones.</summary>
    public void Trim()
    {
        foreach (var bag in readbacks.Values)
            while (bag.TryTake(out var buffer)) buffer.Dispose();
        foreach (var t in targets)
        {
            t.Large.Dispose();
            t.Depth.Dispose();
            t.Small.Dispose();
        }
        targets.Clear();
    }

    bool targetsClosed;

    public void Dispose()
    {
        targetsClosed = true;
        Trim();
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
    long rockWaitSince;

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
        if (source.IsRock && recorded < size.Grid)
        {
            // A rock is baked with the terrain's material of its biome: no terrain, no atlas. Every row needs the biome resident, and a slot of the terrain's
            // layer arrays can be given to another biome meanwhile: the bake waits a moment for it and then gives up (the caller asks again later), so a
            // biome that is gone does not hold the one bake slot.
            if (baker.RockTerrain is null) { Error = new InvalidOperationException("no terrain to bake a TERRAIN-mode rock with"); return true; }
            if (!baker.RockTerrain.CanBakeRock(source.RockBiome))
            {
                long now = Environment.TickCount64;
                if (rockWaitSince == 0) rockWaitSince = now;
                if (now - rockWaitSince > 3000) { Error = new InvalidOperationException($"the biome of terrain row {source.RockBiome} is not resident"); return true; }
                return false;
            }
            rockWaitSince = 0;
        }
        if (!started)
        {
            if (!TexturesReady()) return false;
            var t0 = watch.Elapsed.TotalMilliseconds;
            if (UploadProfile.On) UploadProfile.Take();
            mainParts = baker.Upload(meshes.Main, source.IsRock);
            leavesParts = leaves is not null ? baker.Upload(meshes.Leaves!) : [];
            double t1 = watch.Elapsed.TotalMilliseconds;
            (large, depth, small) = baker.RentTargets(size, gpu.Frame.PreFrame.Handle);
            started = true;
            upload += watch.Elapsed.TotalMilliseconds - t0;
            SlowNote("start", $"meshes {t1 - t0:0.0} ms, targets {watch.Elapsed.TotalMilliseconds - t1:0.0} ms", watch.Elapsed.TotalMilliseconds - t0);
        }
        // Rows whose frame has completed go to the assembler, in order, one after the other on a worker.
        double tRead = watch.Elapsed.TotalMilliseconds;
        if (UploadProfile.On) UploadProfile.Take();
        while (rows.Count > 0 && ReadbackBuffer.Completed(gpu, rows[0].Frame))
        {
            var (row, _, buffer) = rows[0];
            rows.RemoveAt(0);
            int bytes = size.Grid * size.FramePixels * size.FramePixels * 4;
            // The worker reads the readback buffer's mapping in place (no 3 x 3 MB copy a row on the render thread, which also allocated); the
            // buffer goes back to the baker's pool when the worker is done with it.
            nint pointer;
            unsafe
            {
                var all = buffer.Read(0, 3ul * (ulong)bytes);   // makes the memory visible to the host
                pointer = (nint)System.Runtime.CompilerServices.Unsafe.AsPointer(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(all));
            }
            var previous = chain;
            chain = Task.Run(() =>
            {
                previous?.Wait();
                var t = Stopwatch.GetTimestamp();
                assembler.AddRow(row, pointer, pointer + bytes, pointer + 2 * bytes);
                baker.ReturnReadback(buffer);
                lock (this) filter += Stopwatch.GetElapsedTime(t).TotalMilliseconds;
            });
            assembled++;
        }
        SlowNote("read back", $"rows handed to the assembler {assembled}", watch.Elapsed.TotalMilliseconds - tRead);
        if (recorded < size.Grid)
        {
            var t0 = watch.Elapsed.TotalMilliseconds;
            if (UploadProfile.On) UploadProfile.Take();
            var cmd = gpu.Frame.PreFrame;
            FrameProfiler.PreStamp(cmd, StageClock.Uploads);
            cmd.BeginLabel("impostor bake");
            for (int k = 0; k < RowsPerStep && recorded < size.Grid; k++, recorded++)
            {
                ulong rowBytes = (ulong)(size.Grid * size.FramePixels * size.FramePixels * 4);
                var buffer = baker.RentReadback(3 * rowBytes);
                baker.RecordRow(cmd, recorded, size, meshes, mainParts, main, leavesParts, leaves, large!, depth!, small!, buffer.Handle, lodBias, source.IsRock ? source : null);
                rows.Add((recorded, gpu.Frame.Number, buffer));
            }
            cmd.EndLabel();
            FrameProfiler.PreStamp(cmd, StageClock.Bake);
            cmd.Invalidate();   // the baker bound its own pipelines and sets
            render += watch.Elapsed.TotalMilliseconds - t0;
            SlowNote("record", $"rows up to {recorded} of {size.Grid}", watch.Elapsed.TotalMilliseconds - t0);
            return false;
        }
        if (assembled < size.Grid) return false;
        if (finish is null)
        {
            double tFin = watch.Elapsed.TotalMilliseconds;
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
            SlowNote("finish", "targets given back, encode started", watch.Elapsed.TotalMilliseconds - tFin);
        }
        if (!finish.IsCompleted) return false;
        if (finish.IsCompletedSuccessfully) Result = finish.Result;
        else Error = finish.Exception?.InnerException ?? new InvalidOperationException("impostor bake failed");
        lock (this) baker.LastTimes = new ImpostorBakeTimes(upload, render, filter, encode);
        return true;
    }

    /// <summary>MEITOU_STREAM_LOG=1: a line for a part of a bake step that took 3 ms or more, with what the profile saw meanwhile (the driver calls, the collector).</summary>
    void SlowNote(string part, string what, double ms)
    {
        if (!UploadProfile.On) return;
        string parts = UploadProfile.Take();
        if (ms >= 3) Console.WriteLine($"slow bake {part} {source.Name}: {ms:0.0} ms, {what} [{parts}]");
    }

    /// <summary>Waits for the workers (offline: <see cref="ImpostorBaker.Bake"/> steps until done instead).</summary>
    void ReleaseGpu()
    {
        foreach (var p in mainParts.Concat(leavesParts)) { p.Vertices.Dispose(); p.Indices.Dispose(); }
        (mainParts, leavesParts) = ([], []);
        if (large is not null && depth is not null && small is not null) baker.ReturnTargets(size, large, depth, small);
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
