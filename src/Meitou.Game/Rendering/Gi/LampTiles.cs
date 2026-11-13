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
/// The lamps' visibility cache for what stands still (docs/render-lights.md "Lamp tiles"): each lamp around the eye holds a tile of one atlas, the distance to the first thing its rays hit in every
/// direction, traced once from the lamp and again only when what stands in its reach changes (<see cref="GiScene.CastersAround"/>); a carried lamp's tile is traced whole each frame.
/// <c>LampShaders.TileVisibility</c> reads them, so every lamp of a pixel is shadowed for a few lookups. Characters are not in the tiles: <see cref="LampShadowPass"/> traces them per pixel. Render thread only.
/// </summary>
sealed unsafe class LampTiles : IDisposable
{
    public const int AtlasSize = 8192, MinTile = 64, MaxTile = 2048;
    const int Bindings = 4, JobLimit = 80;
    /// <summary>Rays spent on tiles in a frame (at least one tile is filled): what a town costs to come in, spread over a few frames.</summary>
    const long RaysPerFrame = 3_000_000;
    /// <summary>World units a tile's texel covers at the end of the lamp's reach, near the eye.</summary>
    const float TexelAtReach = 1.25f;
    /// <summary>Past this distance from the eye (to the lamp's reach) a lamp has no tile: the traced scene ends about here.</summary>
    const float TileRange = 5500;
    /// <summary>Entries unused for this many frames give their place back.</summary>
    const int KeepFrames = 900;
    /// <summary>The carried lamps with a tile: the first this many within <see cref="MovingRange"/> of the eye, and the side of their tiles.</summary>
    const int MovingSlots = 16, MovingSide = 256;
    const float MovingRange = 2000;
    static readonly float[] BandEdges = [1500, 3500];
    const float NearRefillRange = 600;
    const int NearRefillFrames = 2, FarRefillFrames = 16;

    /// <summary>Debug (console <c>lamps tiles</c>): off goes back to the per-pixel rays for everything.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>The free squares of the atlas, a quadtree: a freed square joins its three siblings when they are free.</summary>
    sealed class Squares
    {
        readonly HashSet<(int X, int Y)>[] _free;

        public Squares(int size)
        {
            int levels = Level(size) + 1;
            _free = new HashSet<(int, int)>[levels];
            for (int i = 0; i < levels; i++) _free[i] = [];
            _free[levels - 1].Add((0, 0));
        }

        static int Level(int size) => BitOperations.Log2((uint)(size / MinTile));

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
    }

    sealed class Entry
    {
        public required WorldLight Light;
        public int X, Y, Size, Band;
        public bool Ready, Dirty = true, Oct;
        public ulong Signature;
        public long Version = -1, LastUsed, LastFilled = long.MinValue / 2;
        public int Wanted;
        public float EyeDistance;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Job
    {
        public Vector4 Position, Axis, Tile, More;
    }

    readonly GpuContext _ctx;
    readonly GpuProgram _program;
    readonly GpuComputePipeline _pipeline;
    readonly GpuTexture _atlas;
    readonly Squares _squares = new(AtlasSize);
    readonly Dictionary<WorldLight, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    readonly List<Entry> _queue = [], _stale = [];
    readonly (int X, int Y)[] _movingTiles = new (int, int)[MovingSlots];
    readonly List<(WorldLight Light, int Slot)> _movingNow = [];
    readonly Job[] _jobs = new Job[JobLimit];
    readonly byte[] _block = new byte[16 + JobLimit * 4 * 16];
    long _frame;
    Vector3 _eye;
    GiScene? _scene;

    public GpuTexture Atlas => _atlas;
    public int FilledLast { get; private set; }
    public int WaitingLast { get; private set; }
    public long RaysLast { get; private set; }

    public LampTiles(GpuContext ctx)
    {
        this._ctx = ctx;
        _program = ctx.Shaders.Compute(LampShadowShaders.TileFill, "lamp tile fill",
        [
            (0, DescriptorType.AccelerationStructureKhr), (1, DescriptorType.StorageBuffer), (2, DescriptorType.UniformBuffer), (3, DescriptorType.StorageImage),
        ], GpuSetLayout.Bindless);
        _pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_program, "lamp tile fill"));
        _atlas = ctx.CreateTexture(new TextureDesc(InternalFormat.R16, AtlasSize, AtlasSize, Use: TextureUse.Storage | TextureUse.Sampled | TextureUse.TransferDst, Name: "lamp tiles"));
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

    void Clear(Entry e)
    {
        if (e.Size > 0) _squares.Release(e.X, e.Y, e.Size);
        (e.Size, e.Ready, e.Dirty) = (0, false, true);
    }

    // ---- which lamps have a tile, and how large ----

    public void BeginFrame(Vector3 eyeNow, GiScene? sceneNow)
    {
        _frame++;
        (_eye, _scene) = (eyeNow, sceneNow);
        _movingNow.Clear();
    }

    /// <summary>
    /// The radius of the lamp's source, in world units, for its penumbra: the LIGHT record has no size (docs/formats/lights.md), so it comes from what the record does give, its reach: a torch's flame
    /// about 20 cm across, a long-throw projector's bright part about 50 cm.
    /// </summary>
    public static float SourceRadius(WorldLight l) => Math.Clamp(l.Radius * SourcePerReach, MinSource, MaxSource);
    const float SourcePerReach = 0.005f, MinSource = 1, MaxSource = 2.5f;
    /// <summary>World units a ray from the lamp leaves out at its start, and that many of the source's radii on a large lamp: the lamp's own housing and mast. Standing that near the source a blocker's
    /// penumbra would cover everything the lamp lights.</summary>
    const float NearSkip = 15, FixtureSources = 8;

    /// <summary>Whether the lamp's tile is a direction-mapped sphere: a point lamp, or a cone too wide for one plane.</summary>
    static bool Sphere(WorldLight l) => l.Type != WorldLightType.Spot || MathF.Max(l.InnerAngle, l.OuterAngle) > MathF.PI * 5 / 6;

    /// <summary>The tile's side for a lamp: its reach over the texel wanted, halved per band of distance from the eye; a band changes only 15 % past its edge.</summary>
    static int SizeFor(WorldLight l, float distance, ref int band)
    {
        int b = 0;
        while (b < BandEdges.Length && distance > BandEdges[b] * (b < band ? 1 : 1.15f)) b++;
        band = b;
        float span = Sphere(l) ? 3.5f * l.Radius : 2 * MathF.Tan(MathF.Max(l.InnerAngle, l.OuterAngle) * 0.5f) * l.Radius;
        int size = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(span / TexelAtReach, 1));
        return Math.Clamp(size >> b, MinTile, MaxTile);
    }

    static float Packed(int x, int y) => x / MinTile + y / MinTile * 128;

    /// <summary>A placed lamp's tile for this frame's lamp data: x its place (in units of <see cref="MinTile"/>, x + 128 y), y its side, negative for a sphere (0: no tile yet).</summary>
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
        if (e.Wanted > 0 && (e.Dirty || e.Wanted != e.Size) && (!e.Ready || _frame - e.LastFilled >= (e.EyeDistance < NearRefillRange ? NearRefillFrames : FarRefillFrames))) _queue.Add(e);
        if (!e.Ready) return default;
        return new Vector2(Packed(e.X, e.Y), e.Oct ? -e.Size : e.Size);
    }

    /// <summary>A carried lamp's tile: one of the slots traced whole this frame, first come (none past <see cref="MovingRange"/> or the slots).</summary>
    public Vector2 PlaceMoving(WorldLight l)
    {
        if (_movingNow.Count == MovingSlots || Vector3.Distance(_eye, l.Position) - l.Radius > MovingRange) return default;
        var (x, y) = _movingTiles[_movingNow.Count];
        _movingNow.Add((l, _movingNow.Count));
        return new Vector2(Packed(x, y), Sphere(l) ? -MovingSide : MovingSide);
    }

    // ---- the fill ----

    /// <summary>Traces the carried lamps' tiles and those waiting, what has no tile yet first and then nearest the eye, within the frame's rays. <paramref name="scene"/> holds this frame's top level.</summary>
    public void Fill(GiScene scene, WorldLamps lamps)
    {
        (FilledLast, RaysLast, WaitingLast) = (0, 0, _queue.Count);
        Forget();
        if (scene.Top is null) { _queue.Clear(); return; }
        _queue.Sort((a, b) => a.Ready != b.Ready ? a.Ready.CompareTo(b.Ready) : a.EyeDistance.CompareTo(b.EyeDistance));
        var done = new List<(Entry E, int X, int Y, int Size)>();
        int count = 0;
        uint groups = 0;
        long rays = 0;
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
        _queue.Clear();
        if (count == 0) return;

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

        foreach (var (e, x, y, size) in done)
        {
            if (e.Size > 0 && size != e.Size) _squares.Release(e.X, e.Y, e.Size);
            (e.X, e.Y, e.Size, e.Ready, e.Dirty, e.LastFilled) = (x, y, size, true, false, _frame);
        }
        FilledLast = done.Count;
        RaysLast = rays;
    }

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

    /// <summary>A place for a tile of <paramref name="size"/>, taken from the tiles no lamp of this frame uses when the atlas is full.</summary>
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

    void Push(CommandList cmd, GiScene scene, Transient constants, ulong bytes)
    {
        var storage = new DescriptorImageInfo(default, _ctx.Device.ViewAt(_atlas.View().Index), ImageLayout.General);
        var top = scene.Top!.Handle;
        var asWrite = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr, AccelerationStructureCount = 1, PAccelerationStructures = &top,
        };
        var records = new DescriptorBufferInfo(_ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
        var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, bytes);
        var writes = stackalloc WriteDescriptorSet[Bindings];
        writes[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.AccelerationStructureKhr };
        writes[1] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 1, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &records };
        writes[2] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 2, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = &uniform };
        writes[3] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 3, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &storage };
        cmd.PushDescriptors(_program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, Bindings), GpuBindPoint.Compute);
    }

    public string Describe() => $"{_entries.Values.Count(e => e.Ready)} tiles of {_entries.Count} lamps, last frame {FilledLast} filled ({RaysLast} rays), {WaitingLast} waiting; {_scene?.MovingObjects} objects moving: {_scene?.MovingDescribe}";

    public void Dispose()
    {
        _ctx.Shaders.Dispose(_program);
        _atlas.Dispose();
    }
}
