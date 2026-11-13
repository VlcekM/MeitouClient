using Meitou.Game.Rendering.Objects;
using System.Numerics;
using System.Runtime.InteropServices;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Meitou.Data.World;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// The lamps' visibility cache for what stands still (docs/render-lights.md "Lamp tiles"): each lamp around the eye holds a tile of one atlas, the
/// distance to the first thing its rays hit in every direction, traced once from the lamp and again only when what stands in its reach changes
/// (<see cref="GiScene.CastersAround"/>); a carried lamp's tile is traced whole each frame. <c>LampShaders.TileVisibility</c> reads them, so every
/// lamp of a pixel is shadowed for a few lookups. Characters are not in the tiles: <see cref="LampShadowPass"/> traces them per pixel. Render thread
/// only.
/// </summary>
sealed unsafe class LampTiles : IDisposable
{
    #region Atlas

    /// <summary>
    /// The atlas's side in texels.
    /// </summary>
    public const int AtlasSize = 8192;

    /// <summary>
    /// The smallest tile's side: the unit tile places are given in.
    /// </summary>
    public const int MinTile = 64;

    /// <summary>
    /// The largest tile's side.
    /// </summary>
    public const int MaxTile = 2048;

    /// <summary>
    /// The device the atlas and the kernel live on.
    /// </summary>
    readonly GpuContext _ctx;

    /// <summary>
    /// The tiles: per texel the distance to the first hit over the lamp's reach (1: nothing).
    /// </summary>
    readonly GpuTexture _atlas;

    /// <summary>
    /// The atlas's free squares.
    /// </summary>
    readonly Squares _squares = new(AtlasSize);

    /// <summary>
    /// The fill kernel, the atlas cleared to nothing hit and the carried lamps' slots taken.
    /// </summary>
    public LampTiles(GpuContext ctx)
    {
        this._ctx = ctx;
        _program = ctx.Shaders.Compute(LampShadowShaders.TileFill, "lamp tile fill",
        [
            (0, DescriptorType.AccelerationStructureKhr),
            (1, DescriptorType.StorageBuffer),
            (2, DescriptorType.UniformBuffer),
            (3, DescriptorType.StorageImage),
        ], GpuSetLayout.Bindless);
        _pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_program, "lamp tile fill"));
        var use = TextureUse.Storage | TextureUse.Sampled | TextureUse.TransferDst;
        _atlas = ctx.CreateTexture(new TextureDesc(InternalFormat.R16, AtlasSize, AtlasSize, Use: use, Name: "lamp tiles"));

        var cmd = ctx.Device.BeginImmediate();
        var clear = new ClearColorValue(1f, 1f, 1f, 1f);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        ctx.Device.Vk.CmdClearColorImage(cmd, ctx.Device.NativeImage(_atlas), ImageLayout.General, &clear, 1, &range);
        ctx.Device.EndImmediate(cmd);

        for (int i = 0; i < MovingSlots; i++)
        {
            _squares.Allocate(MovingSide, out int x, out int y);
            _movingTiles[i] = (x, y);
        }
    }

    /// <summary>
    /// The tiles' atlas.
    /// </summary>
    public GpuTexture Atlas => _atlas;

    /// <summary>
    /// The tiles held and last frame's fill, for the console.
    /// </summary>
    public string Describe()
    {
        string tiles = $"{_entries.Values.Count(e => e.Ready)} tiles of {_entries.Count} lamps";
        string fill = $"last frame {FilledLast} filled ({RaysLast} rays), {WaitingLast} waiting";
        string moving = $"{_scene?.MovingObjects} objects moving: {_scene?.MovingDescribe}";

        return tiles + ", " + fill + "; " + moving;
    }

    /// <summary>
    /// The kernel and the atlas go.
    /// </summary>
    public void Dispose()
    {
        _ctx.Shaders.Dispose(_program);
        _atlas.Dispose();
    }

    /// <summary>
    /// The free squares of the atlas, a quadtree: a freed square joins its three siblings when they are free.
    /// </summary>
    sealed class Squares
    {
        /// <summary>
        /// Per level (side <see cref="MinTile"/> times its power of two) the free squares' corners.
        /// </summary>
        readonly HashSet<(int X, int Y)>[] _free;

        /// <summary>
        /// One free square of <paramref name="size"/>.
        /// </summary>
        public Squares(int size)
        {
            int levels = Level(size) + 1;
            _free = new HashSet<(int, int)>[levels];
            for (int i = 0; i < levels; i++) _free[i] = [];
            _free[levels - 1].Add((0, 0));
        }

        /// <summary>
        /// A free square of <paramref name="size"/>, a larger one split for it; false when none is left.
        /// </summary>
        public bool Allocate(int size, out int x, out int y)
        {
            (x, y) = (0, 0);
            int want = Level(size), at = want;
            while (at < _free.Length && _free[at].Count == 0) at++;
            if (at == _free.Length) return false;

            var pick = _free[at].First();
            _free[at].Remove(pick);
            for (; at > want; at--)
            {
                int half = (MinTile << at) / 2;
                _free[at - 1].Add((pick.X + half, pick.Y));
                _free[at - 1].Add((pick.X, pick.Y + half));
                _free[at - 1].Add((pick.X + half, pick.Y + half));
            }

            (x, y) = pick;
            return true;
        }

        /// <summary>
        /// The square at <paramref name="x"/>, <paramref name="y"/> free again, joined with its siblings up the levels while they are all free.
        /// </summary>
        public void Release(int x, int y, int size)
        {
            int level = Level(size);
            while (level + 1 < _free.Length)
            {
                int parent = MinTile << (level + 1), px = x & ~(parent - 1), py = y & ~(parent - 1), s = MinTile << level;
                (int, int)[] siblings = [(px, py), (px + s, py), (px, py + s), (px + s, py + s)];
                bool all = true;
                foreach (var c in siblings) all &= c == (x, y) || _free[level].Contains(c);
                if (!all) break;

                foreach (var c in siblings) _free[level].Remove(c);
                (x, y) = (px, py);
                level++;
            }

            _free[level].Add((x, y));
        }

        /// <summary>
        /// The level of a square of <paramref name="size"/>.
        /// </summary>
        static int Level(int size) => BitOperations.Log2((uint)(size / MinTile));
    }

    #endregion

    #region Placement

    /// <summary>
    /// World units a tile's texel covers at the end of the lamp's reach, near the eye.
    /// </summary>
    const float TexelAtReach = 1.25f;

    /// <summary>
    /// Past this distance from the eye (to the lamp's reach) a lamp has no tile: the traced scene ends about here.
    /// </summary>
    const float TileRange = 5500;

    /// <summary>
    /// The carried lamps with a tile: the first this many within <see cref="MovingRange"/> of the eye.
    /// </summary>
    const int MovingSlots = 16;

    /// <summary>
    /// The side of the carried lamps' tiles.
    /// </summary>
    const int MovingSide = 256;

    /// <summary>
    /// How far from the eye a carried lamp has a tile.
    /// </summary>
    const float MovingRange = 2000;

    /// <summary>
    /// The distances from the eye where a tile's side halves.
    /// </summary>
    static readonly float[] BandEdges = [1500, 3500];

    /// <summary>
    /// Within this distance from the eye a tile waits <see cref="NearRefillFrames"/> before being traced again, past it <see cref="FarRefillFrames"/>.
    /// </summary>
    const float NearRefillRange = 600;

    /// <summary>
    /// Frames a tile near the eye waits before being traced again.
    /// </summary>
    const int NearRefillFrames = 2;

    /// <summary>
    /// Frames a tile far from the eye waits before being traced again.
    /// </summary>
    const int FarRefillFrames = 16;

    /// <summary>
    /// A lamp's source radius per world unit of its reach.
    /// </summary>
    const float SourcePerReach = 0.005f;

    /// <summary>
    /// The smallest source radius, in world units.
    /// </summary>
    const float MinSource = 1;

    /// <summary>
    /// The largest source radius, in world units.
    /// </summary>
    const float MaxSource = 2.5f;

    /// <summary>
    /// Each placed lamp's tile.
    /// </summary>
    readonly Dictionary<WorldLight, Entry> _entries = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The tiles waiting to be traced this frame.
    /// </summary>
    readonly List<Entry> _queue = [];

    /// <summary>
    /// The carried lamps' slots in the atlas.
    /// </summary>
    readonly (int X, int Y)[] _movingTiles = new (int, int)[MovingSlots];

    /// <summary>
    /// This frame's carried lamps with a tile, and their slots.
    /// </summary>
    readonly List<(WorldLight Light, int Slot)> _movingNow = [];

    /// <summary>
    /// This frame's number.
    /// </summary>
    long _frame;

    /// <summary>
    /// This frame's eye.
    /// </summary>
    Vector3 _eye;

    /// <summary>
    /// This frame's traced scene (null: none).
    /// </summary>
    GiScene? _scene;

    /// <summary>
    /// Debug (console <c>lamps tiles</c>): off goes back to the per-pixel rays for everything.
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// A new frame: its eye and its traced scene.
    /// </summary>
    public void BeginFrame(Vector3 eyeNow, GiScene? sceneNow)
    {
        _frame++;
        (_eye, _scene) = (eyeNow, sceneNow);
        _movingNow.Clear();
    }

    /// <summary>
    /// The radius of the lamp's source, in world units, for its penumbra: the LIGHT record has no size (docs/formats/lights.md), so it comes from
    /// what the record does give, its reach: a torch's flame about 20 cm across, a long-throw projector's bright part about 50 cm.
    /// </summary>
    public static float SourceRadius(WorldLight l) => Math.Clamp(l.Radius * SourcePerReach, MinSource, MaxSource);

    /// <summary>
    /// A placed lamp's tile for this frame's lamp data: x its place (in units of <see cref="MinTile"/>, x + 128 y), y its side, negative for a
    /// sphere (0: no tile yet).
    /// </summary>
    public Vector2 Place(WorldLight l, float visibleRange)
    {
        if (!_entries.TryGetValue(l, out var e)) _entries[l] = e = new Entry { Light = l, Oct = Sphere(l) };

        e.LastUsed = _frame;
        e.EyeDistance = MathF.Max(Vector3.Distance(_eye, l.Position) - l.Radius, 0);
        e.Wanted = e.EyeDistance > MathF.Min(TileRange, visibleRange) ? 0 : SizeFor(l, e.EyeDistance, ref e.Band);
        if (_scene is { } s && e.Version != s.CasterVersion)
        {
            e.Version = s.CasterVersion;
            ulong signature = s.CastersAround(l.Position, l.Radius);
            if (signature != e.Signature) (e.Signature, e.Dirty) = (signature, true);
        }

        // A tile that holds something waits its turn before being traced again, shorter near the eye.
        if (e.Wanted > 0 && (e.Dirty || e.Wanted != e.Size)
            && (!e.Ready || _frame - e.LastFilled >= (e.EyeDistance < NearRefillRange ? NearRefillFrames : FarRefillFrames)))
            _queue.Add(e);

        if (!e.Ready) return default;

        return new Vector2(Packed(e.X, e.Y), e.Oct ? -e.Size : e.Size);
    }

    /// <summary>
    /// A carried lamp's tile: one of the slots traced whole this frame, first come (none past <see cref="MovingRange"/> or the slots).
    /// </summary>
    public Vector2 PlaceMoving(WorldLight l)
    {
        if (_movingNow.Count == MovingSlots || Vector3.Distance(_eye, l.Position) - l.Radius > MovingRange) return default;

        var (x, y) = _movingTiles[_movingNow.Count];
        _movingNow.Add((l, _movingNow.Count));

        return new Vector2(Packed(x, y), Sphere(l) ? -MovingSide : MovingSide);
    }

    /// <summary>
    /// Whether the lamp's tile is a direction-mapped sphere: a point lamp, or a cone too wide for one plane.
    /// </summary>
    static bool Sphere(WorldLight l) => l.Type != WorldLightType.Spot || MathF.Max(l.InnerAngle, l.OuterAngle) > MathF.PI * 5 / 6;

    /// <summary>
    /// The tile's side for a lamp: its reach over the texel wanted, halved per band of distance from the eye; a band changes only 15 % past its edge.
    /// </summary>
    static int SizeFor(WorldLight l, float distance, ref int band)
    {
        int b = 0;
        while (b < BandEdges.Length && distance > BandEdges[b] * (b < band ? 1 : 1.15f)) b++;
        band = b;

        float span = Sphere(l) ? 3.5f * l.Radius : 2 * MathF.Tan(MathF.Max(l.InnerAngle, l.OuterAngle) * 0.5f) * l.Radius;
        int size = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(span / TexelAtReach, 1));

        return Math.Clamp(size >> b, MinTile, MaxTile);
    }

    /// <summary>
    /// A tile's place as the lamp data holds it: in units of <see cref="MinTile"/>, x + 128 y.
    /// </summary>
    static float Packed(int x, int y) => x / MinTile + y / MinTile * 128;

    /// <summary>
    /// A placed lamp's tile and when it was traced.
    /// </summary>
    sealed class Entry
    {
        /// <summary>
        /// The lamp.
        /// </summary>
        public required WorldLight Light;

        /// <summary>
        /// The tile's corner in the atlas, x.
        /// </summary>
        public int X;

        /// <summary>
        /// The tile's corner in the atlas, y.
        /// </summary>
        public int Y;

        /// <summary>
        /// The tile's side (0: no square held).
        /// </summary>
        public int Size;

        /// <summary>
        /// The band of distance from the eye the tile's side was last sized for.
        /// </summary>
        public int Band;

        /// <summary>
        /// Whether the tile has been traced and can be read.
        /// </summary>
        public bool Ready;

        /// <summary>
        /// Whether what stands in the lamp's reach changed since the tile was traced.
        /// </summary>
        public bool Dirty = true;

        /// <summary>
        /// Whether the tile is a direction-mapped sphere (<see cref="Sphere"/>).
        /// </summary>
        public bool Oct;

        /// <summary>
        /// The signature of what stands in the lamp's reach (<see cref="GiScene.CastersAround"/>).
        /// </summary>
        public ulong Signature;

        /// <summary>
        /// The scene's caster version the signature was taken at.
        /// </summary>
        public long Version = -1;

        /// <summary>
        /// The frame the lamp was last placed.
        /// </summary>
        public long LastUsed;

        /// <summary>
        /// The frame the tile was last traced.
        /// </summary>
        public long LastFilled = long.MinValue / 2;

        /// <summary>
        /// The side this frame wants (0: no tile).
        /// </summary>
        public int Wanted;

        /// <summary>
        /// The distance from the eye to the lamp's reach.
        /// </summary>
        public float EyeDistance;
    }

    #endregion

    #region Fill

    /// <summary>
    /// The fill kernel's descriptor bindings.
    /// </summary>
    const int Bindings = 4;

    /// <summary>
    /// Most tiles a frame's fill traces: the kernel's job array.
    /// </summary>
    const int JobLimit = 80;

    /// <summary>
    /// Rays spent on tiles in a frame (at least one tile is filled): what a town costs to come in, spread over a few frames.
    /// </summary>
    const long RaysPerFrame = 3_000_000;

    /// <summary>
    /// Entries unused for this many frames give their place back.
    /// </summary>
    const int KeepFrames = 900;

    /// <summary>
    /// World units a ray from the lamp leaves out at its start: the lamp's own housing and mast. Standing that near the source a blocker's penumbra
    /// would cover everything the lamp lights.
    /// </summary>
    const float NearSkip = 15;

    /// <summary>
    /// On a large lamp a ray leaves out this many of the source's radii instead, when that is more than <see cref="NearSkip"/>.
    /// </summary>
    const float FixtureSources = 8;

    /// <summary>
    /// The fill kernel's program.
    /// </summary>
    readonly GpuProgram _program;

    /// <summary>
    /// The fill kernel's pipeline.
    /// </summary>
    readonly GpuComputePipeline _pipeline;

    /// <summary>
    /// The entries to clear or forget.
    /// </summary>
    readonly List<Entry> _stale = [];

    /// <summary>
    /// This frame's jobs.
    /// </summary>
    readonly Job[] _jobs = new Job[JobLimit];

    /// <summary>
    /// The kernel's constants: a header and the jobs.
    /// </summary>
    readonly byte[] _block = new byte[16 + JobLimit * 4 * 16];

    /// <summary>
    /// Tiles traced last frame.
    /// </summary>
    public int FilledLast { get; private set; }

    /// <summary>
    /// Tiles waiting last frame.
    /// </summary>
    public int WaitingLast { get; private set; }

    /// <summary>
    /// Rays spent on tiles last frame.
    /// </summary>
    public long RaysLast { get; private set; }

    /// <summary>
    /// Traces the carried lamps' tiles and those waiting, what has no tile yet first and then nearest the eye, within the frame's rays.
    /// <paramref name="scene"/> holds this frame's top level.
    /// </summary>
    public void Fill(GiScene scene, WorldLamps lamps)
    {
        (FilledLast, RaysLast, WaitingLast) = (0, 0, _queue.Count);
        Forget();
        if (scene.Top is null)
        {
            _queue.Clear();
            return;
        }

        _queue.Sort((a, b) => a.Ready != b.Ready ? a.Ready.CompareTo(b.Ready) : a.EyeDistance.CompareTo(b.EyeDistance));
        var done = new List<(Entry E, int X, int Y, int Size)>();
        uint groups = 0;
        int count = AddJobs(scene, lamps, done, ref groups, out long rays);
        _queue.Clear();
        if (count == 0) return;

        RecordFill(scene, count, groups);
        foreach (var (e, x, y, size) in done)
        {
            if (e.Size > 0 && size != e.Size) _squares.Release(e.X, e.Y, e.Size);
            (e.X, e.Y, e.Size, e.Ready, e.Dirty, e.LastFilled) = (x, y, size, true, false, _frame);
        }

        FilledLast = done.Count;
        RaysLast = rays;
    }

    /// <summary>
    /// The carried lamps' jobs, then the waiting tiles' within the frame's jobs and rays, a place reserved for each; returns the jobs.
    /// </summary>
    int AddJobs(GiScene scene, WorldLamps lamps, List<(Entry E, int X, int Y, int Size)> done, ref uint groups, out long rays)
    {
        int count = 0;
        rays = 0;
        foreach (var (l, slot) in _movingNow)
        {
            var (x, y) = _movingTiles[slot];
            _jobs[count++] = JobFor(scene, l, x, y, MovingSide, Sphere(l), lamps.Emitter(l.PlacementId), ref groups);
        }

        foreach (var e in _queue)
        {
            if (count == JobLimit) break;

            long cost = (long)e.Wanted * e.Wanted;
            if (done.Count > 0 && rays + cost > RaysPerFrame) break;

            int x, y, size = e.Wanted;
            if (size == e.Size) (x, y) = (e.X, e.Y);
            else if (!Reserve(size, e, out x, out y)) continue;

            _jobs[count++] = JobFor(scene, e.Light, x, y, size, e.Oct, lamps.Emitter(e.Light.PlacementId), ref groups);
            done.Add((e, x, y, size));
            rays += cost;
        }

        return count;
    }

    /// <summary>
    /// The jobs into the frame's constants and the kernel over them, one workgroup per 8 texels square.
    /// </summary>
    void RecordFill(GiScene scene, int count, uint groups)
    {
        var header = new Vector4(count, 0, 0, 0);
        MemoryMarshal.Write(_block, in header);
        MemoryMarshal.AsBytes(_jobs.AsSpan(0, count)).CopyTo(_block.AsSpan(16));
        var constants = _ctx.Frame.Constants.Write<byte>(_block, 256);

        var cmd = (CommandList)_ctx.BeginNative("lamp tiles");
        cmd.BeginLabel("lamp tiles");
        cmd.Barrier(BarrierBatch.Full);
        Push(cmd, scene, constants, (ulong)_block.Length);
        cmd.BindPipeline(_pipeline);
        cmd.BindBindlessTable(_program, 1, GpuBindPoint.Compute);
        cmd.Dispatch(groups, 1);
        cmd.Barrier(BarrierBatch.Full);
        cmd.EndLabel();
        _ctx.EndNative(cmd);
    }

    /// <summary>
    /// A lamp's job, its first workgroup <paramref name="groups"/>, which then counts its workgroups.
    /// </summary>
    static Job JobFor(GiScene scene, WorldLight l, int x, int y, int side, bool sphere, uint emitter, ref uint groups)
    {
        float outer = MathF.Max(l.InnerAngle, l.OuterAngle);
        var job = new Job
        {
            Position = new Vector4(l.Position - scene.Origin, l.Radius),
            Axis = new Vector4(l.Direction, MathF.Cos(outer * 0.5f)),
            Tile = new Vector4(x, y, side, sphere ? 1 : 0),
            More = new Vector4(emitter, groups, side / 8, MathF.Max(NearSkip, FixtureSources * SourceRadius(l))),
        };
        groups += (uint)(side / 8 * (side / 8));

        return job;
    }

    /// <summary>
    /// A place for a tile of <paramref name="size"/>, taken from the tiles no lamp of this frame uses when the atlas is full.
    /// </summary>
    bool Reserve(int size, Entry forEntry, out int x, out int y)
    {
        if (_squares.Allocate(size, out x, out y)) return true;

        _stale.Clear();
        foreach (var e in _entries.Values)
            if (e.Size > 0 && e.LastUsed != _frame && !ReferenceEquals(e, forEntry)) _stale.Add(e);

        _stale.Sort((a, b) => a.LastUsed.CompareTo(b.LastUsed));
        foreach (var e in _stale)
        {
            Clear(e);
            if (_squares.Allocate(size, out x, out y)) return true;
        }

        return false;
    }

    /// <summary>
    /// The entries unused for more than <see cref="KeepFrames"/> give their squares back and go.
    /// </summary>
    void Forget()
    {
        _stale.Clear();
        foreach (var e in _entries.Values)
            if (_frame - e.LastUsed > KeepFrames) _stale.Add(e);

        foreach (var e in _stale)
        {
            Clear(e);
            _entries.Remove(e.Light);
        }
    }

    /// <summary>
    /// The entry's square given back: it has no tile and waits to be traced.
    /// </summary>
    void Clear(Entry e)
    {
        if (e.Size > 0) _squares.Release(e.X, e.Y, e.Size);
        (e.Size, e.Ready, e.Dirty) = (0, false, true);
    }

    /// <summary>
    /// The kernel's descriptors: the top level, the geometry records, the jobs and the atlas.
    /// </summary>
    void Push(CommandList cmd, GiScene scene, Transient constants, ulong bytes)
    {
        var storage = new DescriptorImageInfo(default, _ctx.Device.ViewAt(_atlas.View().Index), ImageLayout.General);
        var top = scene.Top!.Handle;
        var asWrite = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1,
            PAccelerationStructures = &top,
        };
        var records = new DescriptorBufferInfo(_ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
        var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, bytes);

        var writes = stackalloc WriteDescriptorSet[Bindings];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstBinding = 0, DescriptorCount = 1,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstBinding = 1, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &records,
        };
        writes[2] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstBinding = 2, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBuffer,
            PBufferInfo = &uniform,
        };
        writes[3] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstBinding = 3, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &storage,
        };
        cmd.PushDescriptors(_program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, Bindings), GpuBindPoint.Compute);
    }

    /// <summary>
    /// One tile's job as the kernel's <c>uJob</c> reads it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Job
    {
        /// <summary>
        /// The lamp's position relative to the scene's origin, and its reach.
        /// </summary>
        public Vector4 Position;

        /// <summary>
        /// The lamp's axis and the cosine of its outer half angle.
        /// </summary>
        public Vector4 Axis;

        /// <summary>
        /// The tile's corner, its side and whether it is a sphere.
        /// </summary>
        public Vector4 Tile;

        /// <summary>
        /// The lamp's emitter, the first workgroup, the workgroups per row and what a ray leaves out at its start.
        /// </summary>
        public Vector4 More;
    }

    #endregion
}
