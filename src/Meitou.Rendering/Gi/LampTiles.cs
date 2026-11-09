using System.Numerics;
using System.Runtime.InteropServices;
using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using Meitou.Data.World;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gi;

/// <summary>
/// The lamps' visibility cache (docs/render-lights.md "Lamp tiles"): each lamp around the eye holds a tile of one atlas, the distance to the first thing its rays hit in every direction,
/// traced once from the lamp and again only when what stands in its reach changes (<see cref="GiScene.CastersAround"/>). What moves has a second layer, retraced each frame only where a
/// character is or just was; a carried lamp's tile is traced whole each frame. <c>LampShaders.TileVisibility</c> reads them, so every lamp of a pixel is shadowed for a few lookups. Render thread only.
/// </summary>
sealed unsafe class LampTiles : IDisposable
{
    public const int AtlasSize = 8192, MoverAtlasSize = 8192, MinTile = 64, MaxTile = 2048, MaxMoverSide = 1024;
    const int Bindings = 4, JobLimit = 350, JobVectors = 5;
    /// <summary>Rays spent on still tiles in a frame (at least one tile is filled): what a town costs to come in, spread over a few frames.</summary>
    const long RaysPerFrame = 3_000_000;
    /// <summary>Rays spent on what moves in a frame: past it the lamps furthest from the eye have no moving layer (a stale one would leave a shadow behind).</summary>
    const long MoverRaysPerFrame = 4_000_000;
    /// <summary>World units a tile's texel covers at the end of the lamp's reach, near the eye.</summary>
    const float TexelAtReach = 1.25f;
    /// <summary>Past this distance from the eye (to the lamp's reach) a lamp has no tile: the traced scene ends about here.</summary>
    const float TileRange = 5500;
    /// <summary>Entries unused for this many frames give their place back; a lamp no character came near for this many frames gives back its moving layer.</summary>
    const int KeepFrames = 900, MoverKeepFrames = 120;
    /// <summary>The carried lamps with a tile: the nearest the eye within <see cref="MovingRange"/>, the side of their tiles.</summary>
    const int MovingSlots = 16, MovingSide = 256;
    const float MovingRange = 2000;
    static readonly float[] BandEdges = [1500, 3500];
    const float NearRefillRange = 600;
    const int NearRefillFrames = 2, FarRefillFrames = 16;

    /// <summary>Debug (console <c>lamps tiles</c>): off goes back to the per-pixel rays.</summary>
    public static bool Enabled { get; set; }

    /// <summary>What a tile texel's rays see: a still tile, the characters alone, everything (a carried lamp), or nothing (a layer made ready).</summary>
    enum Mode { Still = 0, Characters = 1, Everything = 2, Clear = 3 }

    /// <summary>The free squares of an atlas, a quadtree: a freed square joins its three siblings when they are free.</summary>
    sealed class Squares
    {
        readonly HashSet<(int X, int Y)>[] free;

        public Squares(int size)
        {
            int levels = Level(size) + 1;
            free = new HashSet<(int, int)>[levels];
            for (int i = 0; i < levels; i++) free[i] = [];
            free[levels - 1].Add((0, 0));
        }

        static int Level(int size) => BitOperations.Log2((uint)(size / MinTile));

        public bool Allocate(int size, out int x, out int y)
        {
            (x, y) = (0, 0);
            int want = Level(size), at = want;
            while (at < free.Length && free[at].Count == 0) at++;
            if (at == free.Length) return false;
            var pick = free[at].First();
            free[at].Remove(pick);
            for (; at > want; at--)
            {
                int half = (MinTile << at) / 2;
                free[at - 1].Add((pick.X + half, pick.Y));
                free[at - 1].Add((pick.X, pick.Y + half));
                free[at - 1].Add((pick.X + half, pick.Y + half));
            }
            (x, y) = pick;
            return true;
        }

        public void Release(int x, int y, int size)
        {
            int level = Level(size);
            while (level + 1 < free.Length)
            {
                int parent = MinTile << (level + 1), px = x & ~(parent - 1), py = y & ~(parent - 1), s = MinTile << level;
                (int, int)[] siblings = [(px, py), (px + s, py), (px, py + s), (px + s, py + s)];
                bool all = true;
                foreach (var c in siblings) all &= c == (x, y) || free[level].Contains(c);
                if (!all) break;
                foreach (var c in siblings) free[level].Remove(c);
                (x, y) = (px, py);
                level++;
            }
            free[level].Add((x, y));
        }
    }

    readonly record struct Rect(int X, int Y, int W, int H)
    {
        public Rect Union(Rect o)
        {
            int x = Math.Min(X, o.X), y = Math.Min(Y, o.Y);
            return new Rect(x, y, Math.Max(X + W, o.X + o.W) - x, Math.Max(Y + H, o.Y + o.H) - y);
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
        // The moving layer: its place in the second atlas (side 0: none), the rectangle each character covered last, when one was last in reach.
        public int MoverX, MoverY, MoverSide;
        public long MoverSeen;
        public readonly Dictionary<long, Rect> Rects = [];
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Job
    {
        public Vector4 Position, Axis, Tile, Area, More;
    }

    readonly GpuContext ctx;
    readonly GpuProgram program;
    readonly GpuComputePipeline pipeline;
    readonly GpuTexture atlas, movers;
    readonly Squares squares = new(AtlasSize), moverSquares = new(MoverAtlasSize);
    readonly Dictionary<WorldLight, Entry> entries = new(ReferenceEqualityComparer.Instance);
    readonly List<Entry> queue = [], stale = [], lit = [];
    readonly (int X, int Y)[] movingTiles = new (int, int)[MovingSlots];
    readonly List<(WorldLight Light, int Slot)> movingNow = [];
    readonly List<(long Key, Vector3 Centre, float Radius)> characters = [];
    readonly List<long> gone = [];
    readonly Job[] jobs = new Job[JobLimit], clearJobs = new Job[JobLimit], savedJobs = new Job[JobLimit];
    readonly byte[] block = new byte[16 + JobLimit * JobVectors * 16];
    long frame;
    Vector3 eye;
    GiScene? scene;

    public GpuTexture Atlas => atlas;
    public GpuTexture Movers => movers;
    public int FilledLast { get; private set; }
    public int WaitingLast { get; private set; }
    public long RaysLast { get; private set; }
    public long MoverRaysLast { get; private set; }

    public LampTiles(GpuContext ctx)
    {
        this.ctx = ctx;
        program = ctx.Shaders.Compute(LampShadowShaders.TileFill, "lamp tile fill",
        [
            (0, DescriptorType.AccelerationStructureKhr), (1, DescriptorType.StorageBuffer), (2, DescriptorType.UniformBuffer), (3, DescriptorType.StorageImage),
        ], GpuSetLayout.Bindless);
        pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(program, "lamp tile fill"));
        const TextureUse use = TextureUse.Storage | TextureUse.Sampled | TextureUse.TransferDst;
        atlas = ctx.CreateTexture(new TextureDesc(InternalFormat.R16, AtlasSize, AtlasSize, Use: use, Name: "lamp tiles"));
        movers = ctx.CreateTexture(new TextureDesc(InternalFormat.R16, MoverAtlasSize, MoverAtlasSize, Use: use, Name: "lamp tiles moving"));
        var cmd = ctx.Device.BeginImmediate();
        var clear = new ClearColorValue(1f, 1f, 1f, 1f);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        ctx.Device.Vk.CmdClearColorImage(cmd, ctx.Device.NativeImage(atlas), ImageLayout.General, &clear, 1, &range);
        ctx.Device.Vk.CmdClearColorImage(cmd, ctx.Device.NativeImage(movers), ImageLayout.General, &clear, 1, &range);
        ctx.Device.EndImmediate(cmd);
        for (int i = 0; i < MovingSlots; i++)
        {
            squares.Allocate(MovingSide, out int x, out int y);
            movingTiles[i] = (x, y);
        }
    }

    void Clear(Entry e)
    {
        if (e.Size > 0) squares.Release(e.X, e.Y, e.Size);
        (e.Size, e.Ready, e.Dirty) = (0, false, true);
        DropMovers(e);
    }

    void DropMovers(Entry e)
    {
        if (e.MoverSide > 0) moverSquares.Release(e.MoverX, e.MoverY, e.MoverSide);
        e.MoverSide = 0;
        e.Rects.Clear();
    }

    // ---- which lamps have a tile, and how large ----

    public void BeginFrame(Vector3 eyeNow, GiScene? sceneNow)
    {
        frame++;
        (eye, scene) = (eyeNow, sceneNow);
        movingNow.Clear();
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

    /// <summary>A placed lamp's tile for this frame's lamp data: x its place (in units of <see cref="MinTile"/>, x + 128 y), y its side, negative for a sphere (0: no tile yet); z and w the moving layer's place and side.</summary>
    public Vector4 Place(WorldLight l, float visibleRange)
    {
        if (!entries.TryGetValue(l, out var e)) entries[l] = e = new Entry { Light = l, Oct = Sphere(l) };
        e.LastUsed = frame;
        e.EyeDistance = MathF.Max(Vector3.Distance(eye, l.Position) - l.Radius, 0);
        e.Wanted = e.EyeDistance > MathF.Min(TileRange, visibleRange) ? 0 : SizeFor(l, e.EyeDistance, ref e.Band);
        if (scene is { } s && e.Version != s.CasterVersion)
        {
            e.Version = s.CasterVersion;
            ulong signature = s.CastersAround(l.Position, l.Radius);
            if (signature != e.Signature) (e.Signature, e.Dirty) = (signature, true);
        }
        // What keeps moving near a lamp (a rotor, a lift) must not retrace it every frame and starve the others: a tile that holds something waits its turn, shorter near the eye.
        if (e.Wanted > 0 && (e.Dirty || e.Wanted != e.Size) && (!e.Ready || frame - e.LastFilled >= (e.EyeDistance < NearRefillRange ? NearRefillFrames : FarRefillFrames))) queue.Add(e);
        if (!e.Ready) return default;
        lit.Add(e);
        return new Vector4(Packed(e.X, e.Y), e.Oct ? -e.Size : e.Size, e.MoverSide > 0 ? Packed(e.MoverX, e.MoverY) : 0, e.MoverSide);
    }

    /// <summary>A carried lamp's tile: one of the slots traced whole this frame, the lamps nearest the eye first come (none past <see cref="MovingRange"/> or the slots).</summary>
    public Vector4 PlaceMoving(WorldLight l)
    {
        if (movingNow.Count == MovingSlots || Vector3.Distance(eye, l.Position) - l.Radius > MovingRange) return default;
        var (x, y) = movingTiles[movingNow.Count];
        movingNow.Add((l, movingNow.Count));
        return new Vector4(Packed(x, y), Sphere(l) ? -MovingSide : MovingSide, 0, 0);
    }

    // ---- the fill ----

    /// <summary>Traces what waits, nearest the eye first, within the frame's rays. <paramref name="scene"/> holds this frame's top level.</summary>
    public void Fill(GiScene scene, WorldLamps lamps)
    {
        (FilledLast, RaysLast, MoverRaysLast, WaitingLast) = (0, 0, 0, queue.Count);
        Forget();
        if (scene.Top is null) { queue.Clear(); lit.Clear(); return; }
        var cmd = (CommandList)ctx.BeginNative("lamp tiles");
        cmd.BeginLabel("lamp tiles");
        cmd.Barrier(BarrierBatch.Full);
        FillStill(cmd, scene, lamps);
        FillMoving(cmd, scene);
        cmd.EndLabel();
        ctx.EndNative(cmd);
        queue.Clear();
        lit.Clear();
    }

    /// <summary>The still tiles waiting and the carried lamps' tiles.</summary>
    void FillStill(CommandList cmd, GiScene scene, WorldLamps lamps)
    {
        queue.Sort((a, b) => a.Ready != b.Ready ? a.Ready.CompareTo(b.Ready) : a.EyeDistance.CompareTo(b.EyeDistance));   // what has no tile yet first
        var done = new List<(Entry E, int X, int Y, int Size)>();
        int count = 0;
        uint groups = 0;
        long rays = 0;
        foreach (var (l, slot) in movingNow)
        {
            var (x, y) = movingTiles[slot];
            jobs[count++] = JobFor(scene, l, x, y, MovingSide, Sphere(l), new Rect(0, 0, MovingSide, MovingSide), Mode.Everything, lamps.Emitter(l.PlacementId), ref groups);
        }
        foreach (var e in queue)
        {
            if (count == JobLimit) break;
            long cost = (long)e.Wanted * e.Wanted;
            if (done.Count > 0 && rays + cost > RaysPerFrame) break;
            int x, y, size = e.Wanted;
            if (size == e.Size) (x, y) = (e.X, e.Y);
            else if (!Reserve(size, e, out x, out y)) continue;
            jobs[count++] = JobFor(scene, e.Light, x, y, size, e.Oct, new Rect(0, 0, size, size), Mode.Still, lamps.Emitter(e.Light.PlacementId), ref groups);
            done.Add((e, x, y, size));
            rays += cost;
        }
        if (count == 0) return;
        Run(cmd, scene, atlas, count, groups);
        foreach (var (e, x, y, size) in done)
        {
            if (e.Size > 0 && size != e.Size)
            {
                squares.Release(e.X, e.Y, e.Size);
                DropMovers(e);
            }
            (e.X, e.Y, e.Size, e.Ready, e.Dirty, e.LastFilled) = (x, y, size, true, false, frame);
        }
        FilledLast = done.Count;
        RaysLast = rays;
    }

    /// <summary>The moving layers: for each lamp with a tile, nearest the eye first, the texels each character in reach covers or covered last frame. Twice the still tile's resolution, at most <see cref="MaxMoverSide"/>.</summary>
    void FillMoving(CommandList cmd, GiScene scene)
    {
        characters.Clear();
        scene.CollectMovers(characters);
        lit.Sort((a, b) => a.EyeDistance.CompareTo(b.EyeDistance));
        int count = 0, clears = 0;
        uint groups = 0, clearGroups = 0;
        long rays = 0;
        bool spent = false;
        foreach (var e in lit)
        {
            spent |= rays > MoverRaysPerFrame || count + clears + e.Rects.Count + 16 > JobLimit;
            if (spent)
            {
                DropMovers(e);
                continue;
            }
            var l = e.Light;
            int side = Math.Min(e.Size * 2, MaxMoverSide);
            if (e.MoverSide != 0 && e.MoverSide != side) DropMovers(e);
            bool any = false;
            gone.Clear();
            foreach (var key in e.Rects.Keys) gone.Add(key);
            foreach (var (key, centre, radius) in characters)
            {
                if (Vector3.DistanceSquared(centre, l.Position) > (l.Radius + radius) * (l.Radius + radius)) continue;
                int found = AreasOf(e, side, centre, radius, areas);
                if (found == 0) continue;
                if (e.MoverSide == 0)
                {
                    if (!moverSquares.Allocate(side, out int mx, out int my)) break;
                    (e.MoverX, e.MoverY, e.MoverSide) = (mx, my, side);
                    // A fresh place may hold another lamp's layer: made ready in a dispatch of its own, before the traces (run side by side, it wrote over them in blocks).
                    clearJobs[clears++] = JobFor(scene, l, mx, my, side, e.Oct, new Rect(0, 0, side, side), Mode.Clear, 0, ref clearGroups);
                }
                any = true;
                for (int a = 0; a < found && count < JobLimit; a++)
                {
                    long part = key * 8 + areas[a].Part;
                    var traced = e.Rects.TryGetValue(part, out var last) ? areas[a].Area.Union(last) : areas[a].Area;
                    e.Rects[part] = areas[a].Area;
                    gone.Remove(part);
                    jobs[count++] = JobFor(scene, l, e.MoverX, e.MoverY, e.MoverSide, e.Oct, traced, Mode.Characters, 0, ref groups);
                    rays += traced.W * traced.H;
                }
                if (count + 16 > JobLimit) break;
            }
            foreach (var key in gone)
            {
                if (count == JobLimit) break;
                // Who left: its rectangle once more, now empty of it.
                jobs[count++] = JobFor(scene, l, e.MoverX, e.MoverY, e.MoverSide, e.Oct, e.Rects[key], Mode.Characters, 0, ref groups);
                rays += e.Rects[key].W * e.Rects[key].H;
                e.Rects.Remove(key);
            }
            if (any) e.MoverSeen = frame;
            else if (e.MoverSide > 0 && e.Rects.Count == 0 && frame - e.MoverSeen > MoverKeepFrames) DropMovers(e);
        }
        MoverRaysLast = rays;
        if (clears > 0)
        {
            Array.Copy(jobs, savedJobs, count);
            Array.Copy(clearJobs, jobs, clears);
            Run(cmd, scene, movers, clears, clearGroups);
            Array.Copy(savedJobs, jobs, count);
        }
        if (count > 0) Run(cmd, scene, movers, count, groups);
    }

    readonly (int Part, Rect Area)[] areas = new (int, Rect)[8];
    readonly bool[] octantUsed = new bool[8];
    readonly (float U0, float V0, float U1, float V1)[] octantBox = new (float, float, float, float)[8];

    static int Octant(Vector3 d) => (d.X >= 0 ? 1 : 0) | (d.Y >= 0 ? 2 : 0) | (d.Z >= 0 ? 4 : 0);

    /// <summary>
    /// The areas of the tile <paramref name="centre"/>'s sphere covers, with a margin of two texels: one on a cone's plane; on a sphere's map one per octant it reaches, as the map folds between them. The whole
    /// tile when it stands at the lamp. Returns how many.
    /// </summary>
    int AreasOf(Entry e, int side, Vector3 centre, float radius, (int Part, Rect Area)[] into)
    {
        var l = e.Light;
        var v = centre - l.Position;
        float dist = v.Length();
        if (dist <= radius + 2)
        {
            into[0] = (0, new Rect(0, 0, side, side));
            return 1;
        }
        var dir = v / dist;
        if (e.Oct)
        {
            Array.Clear(octantUsed);
            float sine = Math.Min(radius / dist, 0.999f), cosine = MathF.Sqrt(1 - sine * sine);
            Basis(dir, out var bx, out var by);
            void Note(Vector3 d)
            {
                int o = Octant(d);
                var (u, w) = OctUv(d);
                ref var box = ref octantBox[o];
                box = octantUsed[o] ? (MathF.Min(box.U0, u), MathF.Min(box.V0, w), MathF.Max(box.U1, u), MathF.Max(box.V1, w)) : (u, w, u, w);
                octantUsed[o] = true;
            }
            Note(dir);
            for (int k = 0; k < 16; k++)
            {
                float a = k * MathF.PI / 8;
                var ring = bx * MathF.Cos(a) + by * MathF.Sin(a);
                Note(Vector3.Normalize(dir * cosine + ring * sine));
                Note(Vector3.Normalize(dir + ring * sine * 0.6f));
            }
            int found = 0;
            for (int o = 0; o < 8; o++)
                if (octantUsed[o] && Area(octantBox[o].U0, octantBox[o].V0, octantBox[o].U1, octantBox[o].V1, side, out var area)) into[found++] = (o, area);
            return found;
        }
        var axis = l.Direction;
        float along = Vector3.Dot(v, axis), halfOuter = MathF.Max(l.InnerAngle, l.OuterAngle) * 0.5f, cosOuter = MathF.Cos(halfOuter);
        // Outside the cone: nothing there falls in the tile (beside a lamp a character stands near its plane, and would otherwise take the whole tile).
        if (MathF.Acos(Math.Clamp(along / dist, -1, 1)) - MathF.Asin(Math.Min(radius / dist, 1)) > halfOuter * 1.04f) return 0;
        if (along <= radius)
        {
            if (along + radius <= 0) return 0;
            into[0] = (0, new Rect(0, 0, side, side));
            return 1;
        }
        float span = MathF.Sqrt(MathF.Max(1 - cosOuter * cosOuter, 0)) / MathF.Max(cosOuter, 0.05f) * 1.04f;
        Basis(axis, out var ax, out var ay);
        float cu = Vector3.Dot(v, ax) / along / span * 0.5f + 0.5f, cv = Vector3.Dot(v, ay) / along / span * 0.5f + 0.5f, extent = radius / (along - radius) / span * 0.5f;
        if (!Area(cu - extent, cv - extent, cu + extent, cv + extent, side, out var flat)) return 0;
        into[0] = (0, flat);
        return 1;
    }

    /// <summary>A box of the tile's coordinates in texels, grown by two; false when none of it is in the tile.</summary>
    static bool Area(float u0, float v0, float u1, float v1, int side, out Rect rect)
    {
        int x0 = Math.Clamp((int)MathF.Floor(u0 * side) - 2, 0, side), y0 = Math.Clamp((int)MathF.Floor(v0 * side) - 2, 0, side);
        int x1 = Math.Clamp((int)MathF.Ceiling(u1 * side) + 2, 0, side), y1 = Math.Clamp((int)MathF.Ceiling(v1 * side) + 2, 0, side);
        rect = new Rect(x0, y0, x1 - x0, y1 - y0);
        return x1 > x0 && y1 > y0;
    }

    // The mapping of LampShaders.TileMapping.
    static void Basis(Vector3 a, out Vector3 x, out Vector3 y)
    {
        float s = a.Z >= 0 ? 1 : -1, e = -1 / (s + a.Z), f = a.X * a.Y * e;
        x = new Vector3(1 + s * a.X * a.X * e, s * f, -s * a.X);
        y = new Vector3(f, s + a.Y * a.Y * e, -a.Y);
    }

    static (float U, float V) OctUv(Vector3 d)
    {
        float sum = MathF.Abs(d.X) + MathF.Abs(d.Y) + MathF.Abs(d.Z), px = d.X / sum, py = d.Z / sum;
        if (d.Y < 0) (px, py) = ((1 - MathF.Abs(py)) * (px >= 0 ? 1 : -1), (1 - MathF.Abs(px)) * (py >= 0 ? 1 : -1));
        return (px * 0.5f + 0.5f, py * 0.5f + 0.5f);
    }

    static Job JobFor(GiScene scene, WorldLight l, int x, int y, int side, bool sphere, Rect area, Mode mode, uint emitter, ref uint groups)
    {
        float outer = MathF.Max(l.InnerAngle, l.OuterAngle);
        var job = new Job
        {
            Position = new Vector4(l.Position - scene.Origin, l.Radius),
            Axis = new Vector4(l.Direction, MathF.Cos(outer * 0.5f)),
            Tile = new Vector4(x, y, side, sphere ? 1 : 0),
            Area = new Vector4(area.X, area.Y, area.W, area.H),
            More = new Vector4(emitter, groups, mode == Mode.Characters ? 1 : MathF.Max(NearSkip, FixtureSources * SourceRadius(l)), (float)mode),
        };
        groups += (uint)((area.W + 7) / 8 * ((area.H + 7) / 8));
        return job;
    }

    void Run(CommandList cmd, GiScene scene, GpuTexture target, int count, uint groups)
    {
        var header = new Vector4(count, 0, 0, 0);
        MemoryMarshal.Write(block, in header);
        MemoryMarshal.AsBytes(jobs.AsSpan(0, count)).CopyTo(block.AsSpan(16));
        var constants = ctx.Frame.Constants.Write<byte>(block, 256);
        Push(cmd, scene, target, constants, (ulong)block.Length);
        cmd.BindPipeline(pipeline);
        cmd.BindBindlessTable(program, 1, GpuBindPoint.Compute);
        cmd.Dispatch(groups, 1);
        cmd.Barrier(BarrierBatch.Full);
    }

    /// <summary>A place for a tile of <paramref name="size"/>, taken from the tiles no lamp of this frame uses when the atlas is full.</summary>
    bool Reserve(int size, Entry forEntry, out int x, out int y)
    {
        if (squares.Allocate(size, out x, out y)) return true;
        stale.Clear();
        foreach (var e in entries.Values)
            if (e.Size > 0 && e.LastUsed != frame && !ReferenceEquals(e, forEntry)) stale.Add(e);
        stale.Sort((a, b) => a.LastUsed.CompareTo(b.LastUsed));
        foreach (var e in stale)
        {
            Clear(e);
            if (squares.Allocate(size, out x, out y)) return true;
        }
        return false;
    }

    void Forget()
    {
        stale.Clear();
        foreach (var e in entries.Values)
            if (frame - e.LastUsed > KeepFrames) stale.Add(e);
        foreach (var e in stale)
        {
            Clear(e);
            entries.Remove(e.Light);
        }
    }

    void Push(CommandList cmd, GiScene scene, GpuTexture target, Transient constants, ulong bytes)
    {
        var storage = new DescriptorImageInfo(default, ctx.Device.ViewAt(target.View().Index), ImageLayout.General);
        var top = scene.Top!.Handle;
        var asWrite = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr, AccelerationStructureCount = 1, PAccelerationStructures = &top,
        };
        var records = new DescriptorBufferInfo(ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
        var uniform = new DescriptorBufferInfo(ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, bytes);
        var writes = stackalloc WriteDescriptorSet[Bindings];
        writes[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.AccelerationStructureKhr };
        writes[1] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 1, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &records };
        writes[2] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 2, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = &uniform };
        writes[3] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 3, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &storage };
        cmd.PushDescriptors(program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, Bindings), GpuBindPoint.Compute);
    }

    public string Describe() => $"{entries.Values.Count(e => e.Ready)} tiles of {entries.Count} lamps, {entries.Values.Count(e => e.MoverSide > 0)} moving layers, last frame {FilledLast} filled ({RaysLast} rays), moving {MoverRaysLast} rays, {WaitingLast} waiting";

    public void Dispose()
    {
        ctx.Shaders.Dispose(program);
        atlas.Dispose();
        movers.Dispose();
    }
}
