using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Ogre;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;
using Silk.NET.Vulkan;
using GpuTexture = Meitou.Game.Rendering.Gpu.GpuTexture;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;

namespace Meitou.Game.Rendering.Impostors;

/// <summary>
/// The decoded meshes of a source and the sphere its frames cover.
/// </summary>
public sealed class ImpostorMeshes
{
    /// <summary>
    /// The main mesh, decoded.
    /// </summary>
    public required Model Main { get; init; }
    /// <summary>
    /// The leaves mesh, decoded (null: none).
    /// </summary>
    public Model? Leaves { get; init; }
    /// <summary>
    /// The bounding sphere in object space: the bounding box's centre and the farthest vertex from it.
    /// </summary>
    public required Vector3 Centre { get; init; }
    /// <summary>
    /// The bounding sphere's radius (see <see cref="Centre"/>), grown by 1 % and at least 0.001.
    /// </summary>
    public required float Radius { get; init; }

    /// <summary>
    /// The triangles of the main mesh and the leaves mesh (the first level of detail, what a foliage instance draws).
    /// </summary>
    public int Triangles
    {
        get
        {
            long n = 0;

            foreach (var model in (ReadOnlySpan<Model?>)[Main, Leaves])
            {
                if (model is not null)
                    foreach (var part in model.Parts) n += part.Indices.Length / 3;
            }

            return (int)Math.Min(n, int.MaxValue);
        }
    }

    /// <summary>
    /// The area of the main mesh's triangles as a share of the bounding sphere's surface (4 pi r²): about 0.05 to 1 for a rock, a few thousandths for
    /// a stick that is a hundred times longer than thick, which no impostor can show (a billboard of thin parts loses them; docs/impostors.md section
    /// 13).
    /// </summary>
    public float SurfaceShare
    {
        get
        {
            double area = 0;

            foreach (var part in Main.Parts)
            {
                for (int i = 0; i + 2 < part.Indices.Length; i += 3)
                {
                    var a = part.Vertices[part.Indices[i]].Position;
                    area += 0.5 * Vector3.Cross(part.Vertices[part.Indices[i + 1]].Position - a,
                        part.Vertices[part.Indices[i + 2]].Position - a).Length();
                }
            }

            return (float)(area / (4 * Math.PI * (double)Radius * Radius));
        }
    }

    /// <summary>
    /// Decodes the source's meshes (any thread); null when the main mesh cannot be read.
    /// </summary>
    public static ImpostorMeshes? Load(ImpostorSource source, List<string>? messages = null)
    {
        Model? Read(string? path)
        {
            if (path is null) return null;

            try
            {
                return Model.Build(OgreMeshReader.ReadFile(path), null);
            }
            catch (Exception e) when (e is OgreFormatException or EndOfStreamException or IOException or InvalidDataException)
            {
                if (messages is not null)
                    lock (messages) messages.Add($"impostor {source.Name}: {Path.GetFileName(path)}: {e.Message}");

                return null;
            }
        }

        var main = Read(source.MeshPath);
        if (main is null || main.Parts.Count == 0) return null;

        return From(main, Read(source.LeavesPath));
    }

    /// <summary>
    /// The meshes with their bounding sphere (from the vertices of both).
    /// </summary>
    public static ImpostorMeshes From(Model main, Model? leaves)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var model in (ReadOnlySpan<Model?>)[main, leaves])
        {
            if (model is not null)
            {
                foreach (var part in model.Parts)
                {
                    foreach (var v in part.Vertices)
                    {
                        min = Vector3.Min(min, v.Position);
                        max = Vector3.Max(max, v.Position);
                    }
                }
            }
        }

        var centre = (min + max) / 2;
        float r2 = 0;

        foreach (var model in (ReadOnlySpan<Model?>)[main, leaves])
        {
            if (model is not null)
            {
                foreach (var part in model.Parts)
                    foreach (var v in part.Vertices) r2 = MathF.Max(r2, Vector3.DistanceSquared(v.Position, centre));
            }
        }

        return new ImpostorMeshes { Main = main, Leaves = leaves, Centre = centre, Radius = MathF.Max(MathF.Sqrt(r2) * 1.01f, 1e-3f) };
    }
}

/// <summary>
/// How long the last bake took, by step (milliseconds; wall time on the render thread, and the CPU's filtering and encoding).
/// </summary>
public readonly record struct ImpostorBakeTimes(double Upload, double Render, double Filter, double Encode)
{
    /// <summary>
    /// The four steps summed, in milliseconds.
    /// </summary>
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
    #region Baking

    /// <summary>
    /// The device the bake renders on.
    /// </summary>
    readonly IGpuContext _gpu;
    /// <summary>
    /// The cache the materials' textures come from (the baker's own or the foliage's).
    /// </summary>
    readonly WorldTextureCache _textures;
    /// <summary>
    /// Whether <see cref="_textures"/> is the baker's own, freed with it.
    /// </summary>
    readonly bool _ownsTextures;
    /// <summary>
    /// The native sets the bake program is made with (one extra storage buffer).
    /// </summary>
    readonly NativeFrame _frame;
    /// <summary>
    /// The bake program (<see cref="ImpostorShaders.BakeVertexNative"/>, <see cref="ImpostorShaders.BakeFragmentNative"/>).
    /// </summary>
    readonly NativeProg _program;
    /// <summary>
    /// The stand-in texture's bindless index (<see cref="StandIn"/>; 0: not made yet).
    /// </summary>
    uint _standIn;

    /// <summary>
    /// A baker with its own world texture cache over <paramref name="assets"/> (never trimmed while it lives).
    /// </summary>
    /// <param name="assets">Where texture names are looked up; null only when no source names a texture (tests).</param>
    public ImpostorBaker(IGpuContext gpu, AssetLocator? assets)
        : this(gpu, new WorldTextureCache(gpu, assets!, "impostor textures") { IdleSeconds = double.MaxValue, HighWaterMb = double.MaxValue },
            owns: true)
    {
    }

    /// <summary>
    /// A baker that reads the materials' textures from <paramref name="textures"/> (the foliage's own cache: already resident).
    /// </summary>
    public ImpostorBaker(IGpuContext gpu, WorldTextureCache textures) : this(gpu, textures, owns: false)
    {
    }

    /// <summary>
    /// Makes the native sets and the bake program; <paramref name="owns"/>: <paramref name="textures"/> is freed with the baker.
    /// </summary>
    ImpostorBaker(IGpuContext gpu, WorldTextureCache textures, bool owns)
    {
        this._gpu = gpu;
        this._textures = textures;
        _ownsTextures = owns;
        _frame = new NativeFrame(gpu, extraStorage: 1);
        _program = new NativeProg(gpu, _frame, ImpostorShaders.BakeVertexNative(), ImpostorShaders.BakeFragmentNative(), "impostor bake");
    }

    /// <summary>
    /// The terrain whose mesh material TERRAIN-mode rocks are baked with (<see cref="ImpostorSource.IsRock"/>; docs/impostors.md section 13); null in
    /// the tools.
    /// </summary>
    public TerrainRenderer? RockTerrain { get; set; }

    /// <summary>
    /// Problems met loading the materials' textures.
    /// </summary>
    public List<string> Messages => _textures.Messages;
    /// <summary>
    /// The texture cache the materials resolve through.
    /// </summary>
    internal WorldTextureCache Textures => _textures;
    /// <summary>
    /// The device the bake renders on.
    /// </summary>
    internal IGpuContext Gpu => _gpu;
    /// <summary>
    /// How long the last finished bake took, by step (set by its job).
    /// </summary>
    public ImpostorBakeTimes LastTimes { get; internal set; }
    /// <summary>
    /// Encode the maps (BC3 / BC5); false keeps them RGBA8 (debugging).
    /// </summary>
    public bool Compress { get; set; } = true;

    /// <summary>
    /// A bake to step (<see cref="ImpostorBakeJob.Step"/>): the materials' textures are asked for now.
    /// </summary>
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
            _textures.Pump(wait: true, max: 64);
            if (job.Step()) break;
            if (job.NeedsFrame) nextFrame();
            else Thread.Sleep(1);   // the workers are filtering or encoding
        }

        return job.Result!;
    }

    /// <summary>
    /// Frees the kept targets and readback buffers, the stand-in's bindless entry, the program and the frame (and the texture cache when it is
    /// the baker's own).
    /// </summary>
    public void Dispose()
    {
        _targetsClosed = true;
        Trim();
        if (_standIn != 0) _gpu.Bindless.Free(BindlessKind.Texture2D, _standIn);

        _program.Dispose();
        _frame.Dispose();
        if (_ownsTextures) _textures.Dispose();
    }

    #endregion

    #region Recording (ImpostorBakeJob)

    /// <summary>
    /// One mesh part uploaded for the bake: its buffers, vertex bindings and layout for the program.
    /// </summary>
    internal struct Part
    {
        /// <summary>
        /// The part's vertex and index buffers.
        /// </summary>
        public DeviceBuffer Vertices, Indices;
        /// <summary>
        /// The vertex buffer bindings for the program's layout.
        /// </summary>
        public GpuBufferBinding[] Bindings;
        /// <summary>
        /// The program's vertex layout with the instance rows.
        /// </summary>
        public VertexLayout Layout;
        /// <summary>
        /// The part's index count.
        /// </summary>
        public int Count;
        /// <summary>
        /// Whether the mesh has vertex colours.
        /// </summary>
        public bool HasColours;
    }

    /// <summary>
    /// A mesh's textures, resolved through the cache, and its settings.
    /// </summary>
    internal sealed record Material(WorldTexture? Diffuse, WorldTexture? Normal, WorldTexture? Diffuse2, WorldTexture? Normal2,
        ImpostorMaterial Settings)
    {
        /// <summary>
        /// The four textures (null for those not named).
        /// </summary>
        public IEnumerable<WorldTexture?> All => [Diffuse, Normal, Diffuse2, Normal2];
    }

    /// <summary>
    /// <paramref name="m"/>'s textures from the cache (loaded unless deferred: <see cref="WorldTextureCache.Get"/>).
    /// </summary>
    internal Material Resolve(ImpostorMaterial m) =>
        new(_textures.Get(m.Diffuse, false), _textures.Get(m.Normal, false), _textures.Get(m.Diffuse2, false), _textures.Get(m.Normal2, false), m);

    /// <summary>
    /// A model's parts in device buffers, written through the open frame's uploads.
    /// </summary>
    internal Part[] Upload(Model model, bool rock = false)
    {
        var parts = new List<Part>();

        foreach (var mp in model.Parts)
        {
            if (mp.Indices.Length == 0) continue;

            var packed = GpuVertex.PackObject(mp.Vertices);
            var vertices = DeviceBuffer.Create(_gpu, (ulong)packed.Length, BufferUse.Vertex, "impostor bake meshes");
            var indices = DeviceBuffer.Create(_gpu, (ulong)(mp.Indices.Length * sizeof(uint)), BufferUse.Index, "impostor bake meshes");

            using (var batch = _gpu.Uploads.Begin())
            {
                batch.Write(vertices, 0, packed);
                batch.Write(indices, 0, MemoryMarshal.AsBytes(mp.Indices.AsSpan()));
            }

            if (rock)
            {
                // A rock is drawn by the terrain's mesh program: its own vertex layout and buffers.
                var (rockLayout, rockBindings) = RockTerrain!.RockBakeVertices(vertices);
                parts.Add(new Part
                {
                    Vertices = vertices, Indices = indices, Count = mp.Indices.Length, HasColours = mp.HasColours, Layout = rockLayout,
                    Bindings = rockBindings,
                });
                continue;
            }

            var attributes = FoliageRenderer.VertexAttributes(vertices);
            parts.Add(new Part
            {
                Vertices = vertices,
                Indices = indices,
                Count = mp.Indices.Length,
                HasColours = mp.HasColours,
                Layout = _program.Layout(attributes),
                Bindings = _program.Buffers(attributes, _program.Own),
            });
        }

        return [.. parts];
    }

    /// <summary>
    /// The bake's draw state: back faces culled, depth tested and written, no blending.
    /// </summary>
    static readonly DrawState BakeState = new(CullMode.Back, FrontFaceDirection.Ccw, true, true, DepthFunction.Less, false, 0, 0, BlendState.Off,
        ColorMask.R | ColorMask.G | ColorMask.B | ColorMask.A, PolygonKind.Fill, false, false);

    /// <summary>
    /// GL's texture 0 (what the GL baker bound for a missing map) as a bindless index.
    /// </summary>
    uint StandIn()
    {
        if (_standIn == 0)
            _standIn = _gpu.Bindless.Register(BindlessKind.Texture2D, _gpu.Dummy(FrameGlobals.Sampler2D("impostor stand-in")));

        return _standIn;
    }

    /// <summary>
    /// Whether the frame globals have been evaluated for this run (see <see cref="PrepareFrameGlobals"/>).
    /// </summary>
    bool _globalsPrepared;

    /// <summary>
    /// Evaluates the frame globals once, outside any rendering, before the first row. The first <see cref="NativeFrame.Prepare"/> of a run that has
    /// drawn no world frame yet (an offscreen run settling with a cold cache) makes the globals' owners upload their lazy textures (the shadow
    /// noise): recorded between a row's BeginRendering and EndRendering, that copy and its barrier were invalid commands (6 sync-validation errors).
    /// </summary>
    void PrepareFrameGlobals()
    {
        if (_globalsPrepared) return;

        _globalsPrepared = true;
        var block = _gpu.Frame.WriteConstants(MemoryMarshal.AsBytes(new ImpostorShaders.BakeBlock[1]), 256);
        _frame.Prepare(new ViewConstants { LightDir = Vector3.UnitY }, [block.Binding]);
    }

    /// <summary>
    /// Records one row of frames for the three passes into <paramref name="cmd"/>: per pass the row at twice the frame size into
    /// <paramref name="large"/> (and <paramref name="depth"/>), halved into <paramref name="small"/> by a linear blit and copied into
    /// <paramref name="readback"/> at pass × the row's bytes.
    /// </summary>
    internal void RecordRow(IGpuCommandList cmd, int row, ImpostorClass size, ImpostorMeshes meshes, Part[] mainParts, Material main,
        Part[] leavesParts, Material? leaves, GpuTexture large, GpuTexture depth, GpuTexture small, GpuBuffer readback, float lodBias,
        ImpostorSource? rock = null)
    {
        int grid = size.Grid, f = size.FramePixels, samples = 2 * f, width = grid * samples, smallWidth = grid * f;
        PrepareFrameGlobals();
        var c = meshes.Centre;
        float r = meshes.Radius;

        // A rock is baked at the mean scale of its instances (the terrain's material is mapped in world units, so its texture density follows the
        // scale): the cameras cover the scaled sphere; the atlas keeps the unscaled one (the runtime applies the instance's matrix).
        float rockScale = rock is null ? 1 : rock.MeanScale;
        (c, r) = (c * rockScale, r * rockScale);
        var formats = new AttachmentFormats(large.Desc.Format, depth.Desc.Format);
        var full = new PixelRect(0, 0, (uint)width, (uint)samples);
        ulong rowBytes = (ulong)(smallWidth * f * 4);

        for (int pass = 0; pass < 3; pass++)
        {
            cmd.Order();
            cmd.BeginRendering(new RenderingDesc(
                new RenderTarget(large.Attachment(), LoadOp.Clear, new ClearColour(0f, 0f, 0f, 0f)),
                new RenderTarget(depth.Attachment(), LoadOp.Clear, default, DepthClear: 1f),
                width, samples));
            cmd.SetScissor(full);
            cmd.SetDepth(true, true, DepthFunction.Less);
            cmd.SetDepthBias(false, 0, 0);

            for (int column = 0; column < grid; column++)
            {
                var d = ImpostorLayout.FrameDirection(column, row, grid);
                ImpostorLayout.Basis(d, out var right, out var up);
                var eye = c + d * (2 * r);
                var view = Matrix4x4.CreateLookAt(eye, c, up);
                var projection = Matrix4x4.CreateOrthographic(2 * r, 2 * r, 0.5f * r, 3.5f * r);
                cmd.SetViewport(new PixelViewport(column * samples, 0, samples, samples, 0, 1));

                if (rock is not null)
                {
                    RockTerrain!.RecordRockBake(cmd, formats, BakeState, rock.RockBiome, rockScale, size.BakeDistance, view * projection, eye, d,
                        right, up, pass, mainParts);
                    continue;
                }

                var block = _gpu.Frame.WriteConstants(MemoryMarshal.AsBytes(new ImpostorShaders.BakeBlock[1]
                {
                    new() { Sphere = new Vector4(c, r), Dir = new Vector4(d, 0), Right = new Vector4(right, 0), Up = new Vector4(up, 0), Pass = pass },
                }), 256);
                var constants = new ViewConstants { ViewProjection = view * projection, Eye = eye, LightDir = Vector3.UnitY, FogDistance = 0 };
                _frame.Bind(cmd, _program.P, in constants, [block.Binding]);
                Draw(cmd, mainParts, main, formats, lodBias);
                if (leaves is not null) Draw(cmd, leavesParts, leaves, formats, lodBias);
            }

            cmd.EndRendering();
            cmd.Order();
            cmd.Blit(large, small, TextureMinFilter.Linear);
            cmd.Order();
            cmd.CopyTextureToBuffer(small, 0, 0, new PixelRect(0, 0, (uint)smallWidth, (uint)f), readback, (ulong)pass * rowBytes);
        }

        cmd.Barrier([new GpuDependency(Access.TransferDst, Access.Any)]);
    }

    /// <summary>
    /// The material as the GL baker set it (<c>FoliageRenderer.DrawMesh</c>'s uniforms: textures and normal maps on), one draw per part.
    /// </summary>
    void Draw(IGpuCommandList cmd, Part[] parts, Material m, AttachmentFormats formats, float lodBias)
    {
        var s = m.Settings;
        uint stand = StandIn();
        uint diffuseKey = m.Diffuse?.Key ?? 0, normalKey = m.Normal?.Key ?? 0, diffuse2Key = m.Diffuse2?.Key ?? 0, normal2Key = m.Normal2?.Key ?? 0;
        bool textured = diffuseKey != 0;
        bool normal = textured && normalKey != 0;
        bool dual = textured && diffuse2Key != 0;
        bool cut = normal && s.AlphaThreshold > 0;

        // The textures at the detail the screen shows the mesh with at the transition (ImpostorClass.LodBias): its leaf alpha and colours as they
        // thin out.
        uint Index(uint key) => key == 0 ? stand : _textures.Index(key, lodBias, stand);

        var pc = new MeshPush
        {
            Tint = Vector3.One,
            TriplanarScale = 1f / 5000,
            AlphaChannel = 3,
            GreyChannel = -1,
            HeadDiffuse = stand,
            HeadNormal = stand,
            Diffuse = textured ? Index(diffuseKey) : stand,
            Normal = normal ? Index(normalKey) : stand,
            Diffuse2 = dual ? Index(diffuse2Key) : stand,
            Normal2 = dual && normal2Key != 0 ? Index(normal2Key) : stand,
            NormalSwizzled = normal && m.Normal!.Swizzled ? 1u : 0u,
            HasDiffuse = textured ? 1u : 0u,
            Triplanar = textured && s.Triplanar ? 1u : 0u,
            Tile = s.Tile,
            AlphaSource = cut ? 2 : 0,
            AlphaThreshold = cut ? s.AlphaThreshold : 0f,
            Specular = textured ? s.Specular : 0.3f,
            HasNormal = normal || cut ? 1u : 0u,
            Spare = MeshSurface.NoWeather,
        };
        var state = BakeState with { Cull = s.DoubleSided ? CullMode.None : CullMode.Back };
        cmd.SetRaster(state.Cull, state.Front);

        foreach (var p in parts)
        {
            pc.HasDual = dual && p.HasColours ? 1u : 0u;
            pc.UseVertexColour = p.HasColours ? 1u : 0u;
            cmd.BindPipeline(_gpu.Pipelines.Get(state.Pipeline(_program.P, p.Layout, PrimitiveType.Triangles, formats, "impostor bake")));
            cmd.BindVertexBuffers(0, p.Bindings);
            cmd.BindIndexBuffer(new GpuBufferBinding(p.Indices.Handle, 0, p.Indices.Size), IndexKind.U32);
            cmd.PushConstants(_program.P, GpuStage.Vertex | GpuStage.Fragment, in pc);
            cmd.DrawIndexed((uint)p.Count);
        }
    }

    #endregion

    #region Readback buffers

    // A row's three pictures, reused from row to row and bake to bake (render thread, except ReturnReadback).

    /// <summary>
    /// The idle readback buffers by size.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, System.Collections.Concurrent.ConcurrentBag<IGpuReadback>> _readbacks = new();

    /// <summary>
    /// An idle readback buffer of <paramref name="size"/> bytes, or a new one.
    /// </summary>
    internal IGpuReadback RentReadback(ulong size)
    {
        var bag = _readbacks.GetOrAdd(size, _ => new());
        return bag.TryTake(out var buffer) ? buffer : _gpu.CreateReadback(size, "impostor bake readback");
    }

    /// <summary>
    /// Hands a buffer whose picture has been read back to the pool (any thread: the GPU is done with it).
    /// </summary>
    internal void ReturnReadback(IGpuReadback buffer) => _readbacks.GetOrAdd(buffer.Size, _ => new()).Add(buffer);

    #endregion

    #region Bake targets

    // Kept from bake to bake: a bake takes a set of the atlas's size and gives it back (render thread). Making and freeing the
    // three images of every bake (367 bakes in a minute's fast flight) cost 0.5 to 3 ms of the render thread in a bake's first step and, with the
    // driver busy, 11 to 50 ms (2026-10-10, docs/renderer-native.md "Upload steps"). Every row clears them, so a set from an earlier bake is as good
    // as a new one.

    /// <summary>
    /// Sets kept idle at most (the largest, 6144 x 512, is 28 MB); the oldest goes when another comes back.
    /// </summary>
    const int KeptTargetSets = 3;

    /// <summary>
    /// The idle target sets by (grid, frame): the double-size picture, its depth and the halved picture.
    /// </summary>
    readonly List<((int Grid, int Frame) Key, GpuTexture Large, GpuTexture Depth, GpuTexture Small)> _targets = [];

    /// <summary>
    /// The baker is disposed: a set given back is freed, not kept.
    /// </summary>
    bool _targetsClosed;

    /// <summary>
    /// A colour target at twice the frame size, its depth and the half-size picture for the atlas size; made when none is kept.
    /// </summary>
    internal (GpuTexture Large, GpuTexture Depth, GpuTexture Small) RentTargets(ImpostorClass size, IGpuCommandList pre)
    {
        var key = (size.Grid, size.FramePixels);
        int at = _targets.FindIndex(t => t.Key == key);

        if (at >= 0)
        {
            var kept = _targets[at];
            _targets.RemoveAt(at);
            return (kept.Large, kept.Depth, kept.Small);
        }

        int f = size.FramePixels, samples = 2 * f, width = size.Grid * samples;
        return (pre.CreateTexture(new TextureDesc(InternalFormat.Rgba8, width, samples,
                Use: TextureUse.ColourTarget | TextureUse.Sampled | TextureUse.TransferSrc, Name: "impostor bake target")),
            pre.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, width, samples, Use: TextureUse.DepthTarget,
                Name: "impostor bake target")),

            // Metal halves the rows with a sampling render pass (BlitScaled), so the half-size atlas is a colour target as well as a copy source.
            pre.CreateTexture(new TextureDesc(InternalFormat.Rgba8, size.Grid * f, f,
                Use: TextureUse.ColourTarget | TextureUse.TransferDst | TextureUse.TransferSrc, Name: "impostor bake target")));
    }

    /// <summary>
    /// Takes a bake's set back (the rows recorded into it are ahead of any later use in the queue).
    /// </summary>
    internal void ReturnTargets(ImpostorClass size, GpuTexture large, GpuTexture depth, GpuTexture small)
    {
        if (_targetsClosed)
        {
            large.Dispose();
            depth.Dispose();
            small.Dispose();
            return;
        }

        _targets.Add(((size.Grid, size.FramePixels), large, depth, small));

        while (_targets.Count > KeptTargetSets)
        {
            var oldest = _targets[0];
            _targets.RemoveAt(0);
            oldest.Large.Dispose();
            oldest.Depth.Dispose();
            oldest.Small.Dispose();
        }
    }

    /// <summary>
    /// Frees the pooled readback buffers and the kept bake targets (render thread, when no bake is running); the next bake makes new ones.
    /// </summary>
    public void Trim()
    {
        foreach (var bag in _readbacks.Values)
            while (bag.TryTake(out var buffer)) buffer.Dispose();

        foreach (var t in _targets)
        {
            t.Large.Dispose();
            t.Depth.Dispose();
            t.Small.Dispose();
        }

        _targets.Clear();
    }

    #endregion
}

/// <summary>
/// One bake in progress (<see cref="ImpostorBaker.Begin"/>), stepped on the render thread with a frame open (<see cref="Step"/>): it waits for
/// the materials' textures, uploads the meshes and makes its targets, records <see cref="RowsPerStep"/> rows a frame into the frame's
/// <see cref="GpuFrame.PreFrame"/>, hands each row to the <see cref="ImpostorAssembler"/> on a worker once its frame has completed, and encodes
/// the atlas on a worker (<see cref="Result"/>).
/// </summary>
public sealed class ImpostorBakeJob : IDisposable
{
    /// <summary>
    /// The baker that records the rows.
    /// </summary>
    readonly ImpostorBaker _baker;
    /// <summary>
    /// What is being baked.
    /// </summary>
    readonly ImpostorSource _source;
    /// <summary>
    /// The source's decoded meshes.
    /// </summary>
    readonly ImpostorMeshes _meshes;
    /// <summary>
    /// The size class baked at.
    /// </summary>
    readonly ImpostorClass _size;
    /// <summary>
    /// The main mesh's material.
    /// </summary>
    readonly ImpostorBaker.Material _main;
    /// <summary>
    /// The leaves mesh's material (null: none).
    /// </summary>
    readonly ImpostorBaker.Material? _leaves;
    /// <summary>
    /// The CPU filtering and encoding of the rows.
    /// </summary>
    readonly ImpostorAssembler _assembler;
    /// <summary>
    /// The bake's wall clock, for the step times.
    /// </summary>
    readonly Stopwatch _watch = Stopwatch.StartNew();
    /// <summary>
    /// The meshes' uploaded parts (empty before <see cref="Step"/> starts the bake).
    /// </summary>
    ImpostorBaker.Part[] _mainParts = [], _leavesParts = [];
    /// <summary>
    /// The target set: the double-size picture, its depth and the halved picture (null before the start and after release).
    /// </summary>
    GpuTexture? _large, _depth, _small;
    /// <summary>
    /// The rows recorded and not yet read back, with their GPU frame and readback buffer.
    /// </summary>
    readonly List<(int Row, long Frame, IGpuReadback Buffer)> _rows = [];
    /// <summary>
    /// How many rows are recorded, and how many have been handed to the assembler.
    /// </summary>
    int _recorded, _assembled;
    /// <summary>
    /// The last row's filtering on a worker; each row's waits for the one before.
    /// </summary>
    Task? _chain;
    /// <summary>
    /// The encoding of the atlas on a worker, once every row is assembled.
    /// </summary>
    Task<ImpostorAtlas>? _finish;
    /// <summary>
    /// Time spent uploading, recording, filtering and encoding, in milliseconds (<see cref="ImpostorBakeTimes"/>).
    /// </summary>
    double _upload, _render, _filter, _encode;
    /// <summary>
    /// <c>_started</c>: the meshes are uploaded and the targets made; <c>_disposed</c>: the job is disposed.
    /// </summary>
    bool _started, _disposed;
    /// <summary>
    /// <see cref="Environment.TickCount64"/> since a rock has waited for its biome to be resident (0: not waiting); it gives up after 3 s.
    /// </summary>
    long _rockWaitSince;

    /// <summary>
    /// A job for <paramref name="source"/> at <paramref name="size"/>: resolves the materials and makes the assembler.
    /// </summary>
    internal ImpostorBakeJob(ImpostorBaker baker, ImpostorSource source, ImpostorMeshes meshes, ImpostorClass size)
    {
        (this._baker, this._source, this._meshes, this._size) = (baker, source, meshes, size);
        _main = baker.Resolve(source.Main);
        _leaves = source.Leaves is { } l && meshes.Leaves is not null ? baker.Resolve(l) : null;
        _assembler = new ImpostorAssembler(size.Grid, size.FramePixels, size.Levels);
        _lodBias = size.LodBias(meshes.Radius * source.MeanScale);
    }

    /// <summary>
    /// The texture LOD bias the bake draws with (<see cref="ImpostorClass.LodBias"/>).
    /// </summary>
    public float LodBias => _lodBias;
    /// <summary>
    /// Backing field of <see cref="LodBias"/>.
    /// </summary>
    readonly float _lodBias;

    /// <summary>
    /// Rows recorded per <see cref="Step"/> (frame): 1 in the viewer (a few milliseconds of GPU time a frame), all of them offline.
    /// </summary>
    public int RowsPerStep { get; set; } = 1;
    /// <summary>
    /// What is being baked.
    /// </summary>
    public ImpostorSource Source => _source;
    /// <summary>
    /// The size class baked at.
    /// </summary>
    public ImpostorClass Size => _size;
    /// <summary>
    /// The atlas, once <see cref="Step"/> returned true.
    /// </summary>
    public ImpostorAtlas? Result { get; private set; }
    /// <summary>
    /// A failure on a worker (the atlas is then null).
    /// </summary>
    public Exception? Error { get; private set; }

    /// <summary>
    /// Waiting for the GPU (a row to record or one whose frame has not completed), not for the workers.
    /// </summary>
    public bool NeedsFrame => !_started || _recorded < _size.Grid || _rows.Count > 0;

    /// <summary>
    /// All textures decoded (resident or missing): the bake can render.
    /// </summary>
    bool TexturesReady()
    {
        foreach (var m in (ReadOnlySpan<ImpostorBaker.Material?>)[_main, _leaves])
        {
            if (m is not null)
            {
                foreach (var t in m.All)
                {
                    if (t is { State: WorldTexture.Residency.Loading or WorldTexture.Residency.Unloaded })
                    {
                        _ = t.Key;
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    /// One frame's work (a frame must be open); true when finished (<see cref="Result"/> or <see cref="Error"/>).
    /// </summary>
    public bool Step()
    {
        if (Result is not null || Error is not null) return true;

        var gpu = _baker.Gpu;
        if (_source.IsRock && _recorded < _size.Grid && WaitsForRockBiome(out bool finished)) return finished;
        if (!_started && !Start(gpu)) return false;

        HandRowsOver(gpu);
        if (_recorded < _size.Grid)
        {
            RecordRows(gpu);
            return false;
        }

        if (_assembled < _size.Grid) return false;
        if (_finish is null) StartFinish();
        return Finished();
    }

    /// <summary>
    /// A rock's bake needs its biome resident: true when the step ends here, waiting for the biome (<paramref name="finished"/> false) or failed
    /// (true, with <see cref="Error"/>).
    /// </summary>
    bool WaitsForRockBiome(out bool finished)
    {
        // A rock is baked with the terrain's material of its biome: no terrain, no atlas. Every row needs the biome resident, and a slot of the
        // terrain's layer arrays can be given to another biome meanwhile: the bake waits a moment for it and then gives up (the caller asks again
        // later), so a biome that is gone does not hold the one bake slot.
        if (_baker.RockTerrain is null)
        {
            Error = new InvalidOperationException("no terrain to bake a TERRAIN-mode rock with");
            finished = true;
            return true;
        }

        if (!_baker.RockTerrain.CanBakeRock(_source.RockBiome))
        {
            long now = Environment.TickCount64;
            if (_rockWaitSince == 0) _rockWaitSince = now;

            if (now - _rockWaitSince > 3000)
            {
                Error = new InvalidOperationException($"the biome of terrain row {_source.RockBiome} is not resident");
                finished = true;
                return true;
            }

            finished = false;
            return true;
        }

        _rockWaitSince = 0;
        finished = false;
        return false;
    }

    /// <summary>
    /// Uploads the meshes and takes the targets once every texture is decoded; false while they are not.
    /// </summary>
    bool Start(IGpuContext gpu)
    {
        if (!TexturesReady()) return false;

        var t0 = _watch.Elapsed.TotalMilliseconds;
        _mainParts = _baker.Upload(_meshes.Main, _source.IsRock);
        _leavesParts = _leaves is not null ? _baker.Upload(_meshes.Leaves!) : [];
        double t1 = _watch.Elapsed.TotalMilliseconds;
        (_large, _depth, _small) = _baker.RentTargets(_size, gpu.Frame.PreFrame);
        _started = true;
        _upload += _watch.Elapsed.TotalMilliseconds - t0;
        SlowNote("start", $"meshes {t1 - t0:0.0} ms, targets {_watch.Elapsed.TotalMilliseconds - t1:0.0} ms", _watch.Elapsed.TotalMilliseconds - t0);
        return true;
    }

    /// <summary>
    /// Hands the rows whose frame has completed to the assembler, in order, one after the other on a worker.
    /// </summary>
    void HandRowsOver(IGpuContext gpu)
    {
        double tRead = _watch.Elapsed.TotalMilliseconds;

        while (_rows.Count > 0 && gpu.FrameCompleted(_rows[0].Frame))
        {
            var (row, _, buffer) = _rows[0];
            _rows.RemoveAt(0);
            int bytes = _size.Grid * _size.FramePixels * _size.FramePixels * 4;

            // The worker reads the readback buffer's mapping in place (no 3 x 3 MB copy a row on the render thread, which also allocated); the
            // buffer goes back to the baker's pool when the worker is done with it.
            nint pointer;

            unsafe
            {
                var all = buffer.Read(0, 3ul * (ulong)bytes);   // makes the memory visible to the host
                pointer = (nint)System.Runtime.CompilerServices.Unsafe.AsPointer(ref MemoryMarshal.GetReference(all));
            }

            var previous = _chain;
            _chain = Task.Run(() =>
            {
                previous?.Wait();
                var t = Stopwatch.GetTimestamp();
                _assembler.AddRow(row, pointer, pointer + bytes, pointer + 2 * bytes);
                _baker.ReturnReadback(buffer);
                lock (this) _filter += Stopwatch.GetElapsedTime(t).TotalMilliseconds;
            });
            _assembled++;
        }

        SlowNote("read back", $"rows handed to the assembler {_assembled}", _watch.Elapsed.TotalMilliseconds - tRead);
    }

    /// <summary>
    /// Records the next <see cref="RowsPerStep"/> rows into the frame's pre-frame commands.
    /// </summary>
    void RecordRows(IGpuContext gpu)
    {
        var t0 = _watch.Elapsed.TotalMilliseconds;
        var cmd = gpu.Frame.PreFrame;
        FrameProfiler.PreStamp(cmd, StageClock.Uploads);
        cmd.BeginLabel("impostor bake");

        for (int k = 0; k < RowsPerStep && _recorded < _size.Grid; k++, _recorded++)
        {
            ulong rowBytes = (ulong)(_size.Grid * _size.FramePixels * _size.FramePixels * 4);
            var buffer = _baker.RentReadback(3 * rowBytes);
            _baker.RecordRow(cmd, _recorded, _size, _meshes, _mainParts, _main, _leavesParts, _leaves, _large!, _depth!, _small!, buffer.Handle,
                _lodBias, _source.IsRock ? _source : null);
            _rows.Add((_recorded, gpu.Frame.Number, buffer));
        }

        cmd.EndLabel();
        FrameProfiler.PreStamp(cmd, StageClock.Bake);
        cmd.Invalidate();   // the baker bound its own pipelines and sets
        _render += _watch.Elapsed.TotalMilliseconds - t0;
        SlowNote("record", $"rows up to {_recorded} of {_size.Grid}", _watch.Elapsed.TotalMilliseconds - t0);
    }

    /// <summary>
    /// Every row assembled: gives the GPU's part back and starts the encode on a worker, after the last row.
    /// </summary>
    void StartFinish()
    {
        double tFin = _watch.Elapsed.TotalMilliseconds;
        ReleaseGpu();
        var previous = _chain;
        bool compress = _baker.Compress;
        string name = $"{_source.Name} ({Path.GetFileName(_source.MeshPath)})";
        _finish = Task.Run(() =>
        {
            previous?.Wait();
            var t = Stopwatch.GetTimestamp();
            var atlas = _assembler.Finish(name, _meshes.Centre, _meshes.Radius, compress);
            lock (this) _encode += Stopwatch.GetElapsedTime(t).TotalMilliseconds;

            return atlas;
        });
        SlowNote("finish", "targets given back, encode started", _watch.Elapsed.TotalMilliseconds - tFin);
    }

    /// <summary>
    /// Whether the encode is done: then the result or the error, and the bake's times.
    /// </summary>
    bool Finished()
    {
        if (!_finish!.IsCompleted) return false;
        if (_finish.IsCompletedSuccessfully) Result = _finish.Result;
        else Error = _finish.Exception?.InnerException ?? new InvalidOperationException("impostor bake failed");
        lock (this) _baker.LastTimes = new ImpostorBakeTimes(_upload, _render, _filter, _encode);

        return true;
    }

    /// <summary>
    /// With <c>--log-spikes</c>: a note for a part of a bake step that took 3 ms or more.
    /// </summary>
    void SlowNote(string part, string what, double ms)
    {
        if (ms >= 3) SpikeLog.Note($"slow bake {part} {_source.Name}", ms: ms);
    }

    /// <summary>
    /// Waits for the workers (offline: <see cref="ImpostorBaker.Bake"/> steps until done instead).
    /// </summary>
    void ReleaseGpu()
    {
        foreach (var p in _mainParts.Concat(_leavesParts))
        {
            p.Vertices.Dispose();
            p.Indices.Dispose();
        }

        (_mainParts, _leavesParts) = ([], []);
        if (_large is not null && _depth is not null && _small is not null)
            _baker.ReturnTargets(_size, _large, _depth, _small);

        (_large, _depth, _small) = (null, null, null);
    }

    /// <summary>
    /// Releases the targets and the pending readbacks, then waits for the workers.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        ReleaseGpu();
        foreach (var (_, _, b) in _rows) b.Dispose();

        _rows.Clear();

        try
        {
            _finish?.Wait();
            _chain?.Wait();
        }
        catch (AggregateException)
        {
        }
    }
}