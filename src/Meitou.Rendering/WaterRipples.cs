using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.World;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>One posed part of a body near the water: a capsule from <paramref name="A"/> to <paramref name="B"/> (world) of <paramref name="Radius"/>.</summary>
public readonly record struct WaterBody(Vector3 A, Vector3 B, float Radius);

/// <summary>
/// The <c>contactfoam</c> switch's interactive water (docs/render-water.md "Wakes and ripples"): a damped wave equation on the GPU in a world-anchored window round the
/// camera's target, addressed toroidally as the ground trails are (texel = world mod side; the strips the window gains are wiped, all of it after a jump). The waves run at
/// the shallow-water speed of the depth under them, stop at the shore and die out in a sponge at the window's edge. The posed bodies in the water push it: the water a
/// capsule takes from a column (its chord through the top <see cref="BodyDepth"/> units) rises round it, what it leaves falls back, so a walking leg makes a bow wave and a
/// hollow behind, and the V of the wake comes out of the waves. Foam is renewed where the bodies churn the water and where the waves steepen, and fades. Fixed steps of
/// game time (none while paused). Recorded into the frame's pre-frame commands; render thread only. Display only: nothing here is saved.
/// </summary>
public sealed unsafe class WaterRipples : IDisposable
{
    /// <summary>The window's side in texels and a texel in world units (5 cm), the snap it moves by, and the sponge round its edge (texels).</summary>
    public const int Side = 512, Snap = 32, Sponge = 24;
    public const float Texel = 0.5f;

    /// <summary>How far from the window's centre the waves show: the window's half side less its snap and the sponge.</summary>
    public const float Reach = (Side / 2 - Snap - Sponge) * Texel;

    /// <summary>How deep under the surface a body still moves the water (units): the top of the column, where the surface waves are made.</summary>
    public const float BodyDepth = 3;

    /// <summary>The depth map's side (texels of <see cref="DepthTexel"/> units over the same window).</summary>
    const int DepthSide = Side / 4;
    const float DepthTexel = Texel * 4;

    /// <summary>One step of game time (s), at most this many a frame (the rest is dropped: slow frames slow the water down).</summary>
    const float StepSeconds = 1f / 90;
    const int MaxSteps = 6;

    /// <summary>After this many game seconds with no body in the water the field has died out: no more steps until one comes.</summary>
    const float QuietSeconds = 20;

    /// <summary>The bodies one frame takes (capsules) and the characters they belong to.</summary>
    public const int MaxCapsules = 192, MaxGroups = 32;

    [StructLayout(LayoutKind.Sequential)]
    struct Push
    {
        public int OriginX, OriginZ;   // the window's first world texel
        public int PreviousX, PreviousZ;
        public int Groups, Mode, Sources, Pad;
        public float Dt, SourceDt, Water, Pad2;
    }

    readonly IGpuContext ctx;
    readonly GpuProgram program;
    readonly GpuComputePipeline pipeline;
    readonly DeviceBuffer depth;
    DeviceBuffer now, next;   // the state, swapped after each step
    readonly GpuTexture surface;
    readonly GpuSampler sampler;
    readonly float[] depths = new float[DepthSide * DepthSide];
    readonly Vector4[] bodies = new Vector4[MaxGroups * 2 + MaxCapsules * 2];
    readonly List<(QuerySlot Begin, QuerySlot Mid, QuerySlot End, int Steps)> pendingTimes = [];
    int groups, capsules, groupStart;
    Vector3 focus;
    bool focused;
    (int X, int Z)? origin, depthOrigin;
    bool anyWater, current;
    double? lastHours;
    int sinceBake;

    /// <summary>Frames after which the depth map is baked again where the window stands.</summary>
    const int RebakeFrames = 120;
    float owed, sourceOwed, quiet = QuietSeconds;

    public WaterRipples(IGpuContext ctx)
    {
        this.ctx = ctx;
        program = ctx.Shaders.Compute(Kernel, "water ripples");
        pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(program, "water ripples"));
        ulong texels = (ulong)Side * Side;
        now = DeviceBuffer.Create(ctx, texels * 8, BufferUse.Storage | BufferUse.TransferDst, "water ripples a");
        next = DeviceBuffer.Create(ctx, texels * 8, BufferUse.Storage | BufferUse.TransferDst, "water ripples b");
        depth = DeviceBuffer.Create(ctx, DepthSide * DepthSide * 4, BufferUse.Storage | BufferUse.TransferDst, "water ripples depth");
        // One level: the water fades the waves out where a pixel covers several texels (they are smaller than it), so no mips are made.
        using (var batch = ctx.Uploads.Begin())
            surface = batch.Create(new TextureDesc(InternalFormat.Rgba16f, Side, Side, Use: TextureUse.Sampled | TextureUse.Storage, Name: "water ripples"));
        sampler = ctx.Samplers.Get(new SamplerDesc(TextureMinFilter.Linear, TextureMagFilter.Linear, false, TextureWrapMode.Repeat,
            TextureWrapMode.Repeat, TextureWrapMode.Repeat, false, DepthFunction.Always, false, false, 1, 0));
    }

    /// <summary>The field (rgba: height, its slopes along x and z, foam), once written: before that it must not be read.</summary>
    public SampledTexture Surface => new(sampler, surface.View());

    /// <summary>Whether <see cref="Surface"/> holds this window's field (false: none yet, or no water in the window).</summary>
    public bool Ready => current && origin is not null;

    /// <summary>
    /// The shader's window: xy its centre (world), zw that centre's place in the image (0 to 1, worked out in double so the far-out world does not round it);
    /// and the half side the waves show over.
    /// </summary>
    public (Vector4 Window, float Half) Window
    {
        get
        {
            var (x, z) = origin ?? (0, 0);
            double cx = (x + Side / 2.0) * Texel, cz = (z + Side / 2.0) * Texel, span = Side * (double)Texel;
            return (new Vector4((float)cx, (float)cz, (float)(cx / span - Math.Floor(cx / span)), (float)(cz / span - Math.Floor(cz / span))), Reach);
        }
    }

    /// <summary>GPU time of the timed frames (microseconds, summed: all of it and the steps alone), their count and their steps, read a few frames late.</summary>
    public double GpuMicroseconds { get; private set; }
    public double StepMicroseconds { get; private set; }
    public long GpuTimed { get; private set; }
    public long StepsTimed { get; private set; }

    /// <summary>Starts this frame's bodies: the window follows <paramref name="centre"/> (the camera's target).</summary>
    public void Begin(Vector3 centre)
    {
        (focus, focused, groups, capsules) = (centre, true, 0, 0);
    }

    /// <summary>One character's capsules (those near the water); the ones past <see cref="MaxCapsules"/> are dropped.</summary>
    public void AddBody(ReadOnlySpan<WaterBody> parts)
    {
        if (groups == MaxGroups || parts.IsEmpty) return;
        groupStart = capsules;
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var c in parts)
        {
            // A pose with a broken bone (not finite, or absurd) pushes nothing: one bad value would spread over the whole field.
            if (!Sane(c))
            {
                if (dropped++ == 0) Console.WriteLine($"water     ripples: dropped a body capsule ({c.A} to {c.B}, radius {c.Radius}): not finite or absurd");
                continue;
            }
            if (capsules == MaxCapsules) break;
            int at = MaxGroups * 2 + capsules * 2;
            bodies[at] = new Vector4(c.A, c.Radius);
            bodies[at + 1] = new Vector4(c.B, 0);
            capsules++;
            min = Vector2.Min(min, Vector2.Min(new Vector2(c.A.X, c.A.Z), new Vector2(c.B.X, c.B.Z)) - new Vector2(c.Radius));
            max = Vector2.Max(max, Vector2.Max(new Vector2(c.A.X, c.A.Z), new Vector2(c.B.X, c.B.Z)) + new Vector2(c.Radius));
        }
        if (capsules == groupStart) return;
        bodies[groups * 2] = new Vector4(min.X - 1, min.Y - 1, max.X + 1, max.Y + 1);
        bodies[groups * 2 + 1] = new Vector4(groupStart, capsules - groupStart, 0, 0);
        groups++;
    }

    /// <summary>A capsule the field can take: finite, a radius over 0 and under <see cref="MaxRadius"/>, and within the window's reach of its centre.</summary>
    bool Sane(in WaterBody c) =>
        float.IsFinite(c.A.X) && float.IsFinite(c.A.Y) && float.IsFinite(c.A.Z) && float.IsFinite(c.B.X) && float.IsFinite(c.B.Y) && float.IsFinite(c.B.Z)
        && c.Radius > 0 && c.Radius < MaxRadius && Vector3.DistanceSquared(c.A, focus) < 4 * Reach * Reach && Vector3.DistanceSquared(c.A, c.B) < MaxRadius * MaxRadius * 16;

    /// <summary>Capsules dropped by <see cref="Sane"/> (reported at the end).</summary>
    long dropped;

    /// <summary>The thickest capsule a body may have (units): a box larger than this is a broken pose or mesh, not a limb.</summary>
    const float MaxRadius = 30;

    /// <summary>
    /// The frame's work, before the scene: the window moved to the focus (the depth map baked again from <paramref name="heights"/> when it moved), the steps of game time
    /// since the last frame (<paramref name="gameHours"/>, still while paused), then the field written out for the water's shaders. Nothing with no water in the window.
    /// </summary>
    internal void Record(double gameHours, HeightSnapshot heights)
    {
        CollectTimes();
        float seconds = lastHours is { } last ? (float)Math.Clamp((gameHours - last) * WaveSet.SecondsPerGameHour, 0, 1) : 0;
        lastHours = gameHours;
        if (!focused) return;
        focused = false;
        var (x, z) = (Snapped(focus.X), Snapped(focus.Z));
        var previous = origin;
        bool jump = origin is not { } was || Math.Abs(x - was.X) >= Side || Math.Abs(z - was.Z) >= Side;
        origin = (x, z);
        // Again now and then where it stands: the terrain's finer heights stream in after the window has moved.
        if (depthOrigin != origin || ++sinceBake >= RebakeFrames) Bake(heights);
        if (!anyWater)
        {
            current = false;
            return;
        }
        quiet = groups > 0 ? 0 : quiet + seconds;
        if (quiet >= QuietSeconds && current && !jump && previous == origin) return;
        owed += seconds;
        sourceOwed += seconds;
        int steps = (int)(owed / StepSeconds);
        owed -= steps * StepSeconds;
        if (steps > MaxSteps) (steps, owed) = (MaxSteps, 0);
        if (current && steps == 0 && !jump && previous == origin) return;
        Dispatch(steps, jump, previous ?? origin.Value);
    }

    static int Snapped(float world) => (int)MathF.Floor((world / Texel - Side / 2f) / Snap) * Snap;

    /// <summary>The water's depth over the window (units, 0 on land) at <see cref="DepthTexel"/>, from this frame's terrain heights.</summary>
    void Bake(HeightSnapshot heights)
    {
        var (x, z) = origin!.Value;
        int dx = x / 4, dz = z / 4;
        anyWater = false;
        for (int j = 0; j < DepthSide; j++)
            for (int i = 0; i < DepthSide; i++)
            {
                float wx = (dx + i + 0.5f) * DepthTexel, wz = (dz + j + 0.5f) * DepthTexel;
                float d = WorldWater.Height - heights.HeightAt(wx, wz);
                d = float.IsFinite(d) ? Math.Max(d, 0) : 0;
                anyWater |= d > 0;
                depths[Wrap(dz + j, DepthSide) * DepthSide + Wrap(dx + i, DepthSide)] = d;
            }
        Wall(dx, dz);
        (depthOrigin, sinceBake) = (origin, 0);
        using var batch = ctx.Uploads.Begin();
        batch.Write(depth, 0, MemoryMarshal.AsBytes(depths.AsSpan()));
    }

    static int Wrap(int v, int n) => ((v % n) + n) % n;

    /// <summary>The walls and posts standing in the water (<see cref="WaterRenderer.Walls"/>); the waves do not pass them.</summary>
    public Func<Vector2, float, IReadOnlyList<(Vector2 A, Vector2 B)>>? Walls { get; set; }

    Task<IReadOnlyList<(Vector2 A, Vector2 B)>>? wallJob;
    IReadOnlyList<(Vector2 A, Vector2 B)>? wallSegments;
    Vector2 wallCentre;

    /// <summary>How far round the window's centre the walls are read (on a worker), and how far it may move before they are read again.</summary>
    const float WallReach = 600, WallRefresh = 250;

    /// <summary>The depth map's texels the walls cross are land (depth 0): the waves stop at them as at a shore. The walls are read on a worker and kept while the window stays near.</summary>
    void Wall(int dx, int dz)
    {
        var centre = new Vector2((dx + DepthSide / 2) * DepthTexel, (dz + DepthSide / 2) * DepthTexel);
        if (wallJob is { IsCompleted: true } done)
        {
            wallSegments = done.IsCompletedSuccessfully ? done.Result : null;
            wallJob = null;
        }
        if (Walls is { } read && wallJob is null && (wallSegments is null || Vector2.Distance(centre, wallCentre) > WallRefresh))
        {
            wallCentre = centre;
            wallJob = Task.Run(() => read(centre, WallReach));
        }
        if (wallSegments is null) return;
        foreach (var (a, b) in wallSegments)
        {
            var ta = a / DepthTexel;
            var tb = b / DepthTexel;
            int steps = (int)MathF.Ceiling(Vector2.Distance(ta, tb) * 4) + 1;
            for (int k = 0; k <= steps; k++)
            {
                var t = Vector2.Lerp(ta, tb, k / (float)steps);
                int i = (int)MathF.Floor(t.X), j = (int)MathF.Floor(t.Y);
                if (i < dx || j < dz || i >= dx + DepthSide || j >= dz + DepthSide) continue;
                depths[Wrap(j, DepthSide) * DepthSide + Wrap(i, DepthSide)] = 0;
            }
        }
    }

    void Dispatch(int steps, bool jump, (int X, int Z) previous)
    {
        var cmd = ctx.Frame.PreFrame;
        cmd.BeginLabel("water ripples");
        cmd.Order();
        var stamps = (Begin: ctx.Frame.Timestamps.Allocate(), Mid: ctx.Frame.Timestamps.Allocate(), End: ctx.Frame.Timestamps.Allocate());
        cmd.Timestamp(ctx.Frame.Timestamps, stamps.Begin, GpuStage.Compute);
        if (jump)
        {
            cmd.FillBuffer(now.Handle, 0, now.Size, 0);
            cmd.Order();
        }
        var slice = ctx.Frame.WriteConstants(MemoryMarshal.AsBytes(bodies.AsSpan()), 256);
        var o = origin!.Value;
        var push = new Push
        {
            OriginX = o.X, OriginZ = o.Z, PreviousX = previous.X, PreviousZ = previous.Z, Groups = groups, Dt = StepSeconds, SourceDt = Math.Max(sourceOwed, 1e-3f),
            Water = WorldWater.Height,
        };
        uint g = Side / 16;
        if (!jump && previous != o)
        {
            Run(cmd, slice, push with { Mode = 1 }, g);   // the strips the window gained: wiped, in place
            Compute(cmd);
        }
        for (int i = 0; i < steps; i++)
        {
            Run(cmd, slice, push with { Mode = 0, Sources = i == 0 ? 1 : 0 }, g);
            Compute(cmd);
            (now, next) = (next, now);
        }
        if (steps > 0) sourceOwed = 0;
        cmd.Timestamp(ctx.Frame.Timestamps, stamps.Mid, GpuStage.Compute);
        Run(cmd, slice, push with { Mode = 2 }, g);
        cmd.Barrier([new GpuDependency(Access.StorageWrite, Access.Sampled)]);
        cmd.Timestamp(ctx.Frame.Timestamps, stamps.End, GpuStage.All);
        if (stamps.Begin.IsValid && stamps.Mid.IsValid && stamps.End.IsValid) pendingTimes.Add((stamps.Begin, stamps.Mid, stamps.End, steps));
        cmd.EndLabel();
        current = true;
    }

    static void Compute(IGpuCommandList cmd) => cmd.Barrier([new GpuDependency(Access.StorageWrite, Access.StorageRead | Access.StorageWrite)]);

    void Run(IGpuCommandList cmd, in Transient slice, in Push push, uint groupsPerSide)
    {
        cmd.BindPipeline(pipeline);
        Span<GpuBinding> b =
        [
            GpuBinding.Storage(0, new GpuBufferBinding(now.Handle, 0, now.Size)),
            GpuBinding.Storage(1, new GpuBufferBinding(next.Handle, 0, next.Size)),
            GpuBinding.Storage(2, new GpuBufferBinding(depth.Handle, 0, depth.Size)),
            GpuBinding.Storage(3, new GpuBufferBinding(slice.Handle, slice.Offset, (ulong)(bodies.Length * 16))),
            GpuBinding.StorageImage(4, new SampledTexture(default, surface.View())),
        ];
        cmd.BindProgramResources(program, 0, b);
        cmd.PushConstants(program, GpuStage.Compute, in push);
        cmd.Dispatch(groupsPerSide, groupsPerSide, 1);
    }

    void CollectTimes()
    {
        var q = ctx.Frame.Timestamps;
        for (int i = 0; i < pendingTimes.Count; i++)
        {
            var (b, m, e, steps) = pendingTimes[i];
            if (b.Frame + ctx.FramesInFlight + 2 < ctx.Frame.Number) { pendingTimes.RemoveAt(i--); continue; }
            if (!q.TryRead(b, out ulong tb) || !q.TryRead(m, out ulong tm) || !q.TryRead(e, out ulong te)) continue;
            GpuMicroseconds += (te - tb) / 1000.0;
            StepMicroseconds += (tm - tb) / 1000.0;
            GpuTimed++;
            StepsTimed += steps;
            pendingTimes.RemoveAt(i--);
        }
    }

    /// <summary>
    /// Modes: 0 one step (A to B; with Sources the bodies' push first), 1 the texels the window gained wiped in A, 2 the field written out (height,
    /// slopes, foam). State per texel: height, its rate, foam, and the water the bodies took from the column at the last push.
    /// </summary>
    internal static readonly string Kernel = string.Create(System.Globalization.CultureInfo.InvariantCulture, $$"""
        #version 450
        layout(local_size_x = 16, local_size_y = 16) in;
        layout(std430, set = 0, binding = 0) buffer From { uvec2 a[]; };   // halves: height, rate; foam, water taken
        layout(std430, set = 0, binding = 1) buffer To { uvec2 b[]; };
        layout(std430, set = 0, binding = 2) readonly buffer Depth { float depth[]; };
        layout(std430, set = 0, binding = 3) readonly buffer Bodies { vec4 bodies[]; };
        layout(set = 0, binding = 4, rgba16f) writeonly uniform image2D field;
        layout(push_constant) uniform Push { ivec2 origin; ivec2 previous; int groups; int mode; int sources; int pad; float dt; float sourceDt; float water; float pad2; } pc;

        const int S = {{Side}};
        const int DS = {{DepthSide}};
        const float Texel = {{Texel}};
        const float Gravity = 98.1;          // units/s^2
        const float SlowDepth = 0.4, FastDepth = 3.0;   // the wave speed is the shallow water's, sqrt(g d), d clamped to these
        const float Damping = 0.5;           // per second, on the surface's rate
        const float Viscosity = 0.12;        // units^2/s: the short waves die first
        const float SpongeRate = 40.0;       // per second at the window's edge
        const float BodyDepth = {{BodyDepth}};
        const float Push_ = 0.4;             // the share of the water a body takes or leaves that shows on the surface round it
        const float FoamLife = 2.5;          // seconds to a third
        const int Groups = {{MaxGroups}};

        int wrap(int v) { return ((v % S) + S) % S; }
        int at(ivec2 i) { return wrap(i.y) * S + wrap(i.x); }
        // The world texel image texel i holds in the window starting at o.
        ivec2 worldOf(ivec2 i, ivec2 o) { return o + ivec2(wrap(i.x - o.x), wrap(i.y - o.y)); }

        float depthTexel(ivec2 d) { return depth[(((d.y % DS) + DS) % DS) * DS + (((d.x % DS) + DS) % DS)]; }
        // The water's depth at the world point p (bilinear over the depth map's texels).
        float depthAt(vec2 p)
        {
            vec2 t = p / (Texel * 4.0) - 0.5;
            ivec2 i = ivec2(floor(t));
            vec2 f = t - vec2(i);
            return mix(mix(depthTexel(i), depthTexel(i + ivec2(1, 0)), f.x), mix(depthTexel(i + ivec2(0, 1)), depthTexel(i + ivec2(1, 1)), f.x), f.y);
        }

        float segment(vec3 p, vec3 a, vec3 b)
        {
            vec3 ab = b - a;
            float t = clamp(dot(p - a, ab) / max(dot(ab, ab), 1e-6), 0.0, 1.0);
            return length(p - a - ab * t);
        }

        // The water the bodies take from the column at p: their chord through its top (BodyDepth, or the whole column where it is shallower), sampled at four depths,
        // with a soft edge a texel wide. ring: how close a body's skin passes the surface here (1 on it).
        float taken(vec2 p, float column, out float ring)
        {
            ring = 0.0;
            float top = min(column, BodyDepth);
            if (top <= 0.0) return 0.0;
            float sum = 0.0;
            for (int g = 0; g < pc.groups; g++)
            {
                vec4 box = bodies[g * 2];
                if (p.x < box.x || p.y < box.y || p.x > box.z || p.y > box.w) continue;
                ivec2 range = ivec2(bodies[g * 2 + 1].xy);
                for (int k = range.x; k < range.x + range.y; k++)
                {
                    vec4 ca = bodies[Groups * 2 + k * 2], cb = bodies[Groups * 2 + k * 2 + 1];
                    float skin = segment(vec3(p.x, pc.water, p.y), ca.xyz, cb.xyz) - ca.w;
                    ring = max(ring, 1.0 - smoothstep(0.0, 1.2, abs(skin + 0.3)));
                    for (int s = 0; s < 4; s++)
                    {
                        float y = pc.water - (float(s) + 0.5) * 0.25 * top;
                        float d = segment(vec3(p.x, y, p.y), ca.xyz, cb.xyz) - ca.w;
                        sum += clamp(0.5 - d / Texel, 0.0, 1.0) * 0.25 * top;
                    }
                }
            }
            return min(sum, top);
        }

        // Bounds of the state (height, rate, foam, water taken): a value outside them, or not finite, is a fault, and is dropped to 0 (one bad cell
        // spread through the Laplacian would take the whole field, and the water drawn from it, with it).
        const vec4 Low = vec4(-4.0, -200.0, 0.0, 0.0), High = vec4(4.0, 200.0, 1.0, BodyDepth);
        vec4 sane(vec4 x)
        {
            bvec4 bad = bvec4(isnan(x.x) || isinf(x.x), isnan(x.y) || isinf(x.y), isnan(x.z) || isinf(x.z), isnan(x.w) || isinf(x.w));
            return any(bad) ? vec4(0.0) : clamp(x, Low, High);
        }
        vec4 load(ivec2 i) { uvec2 u = a[at(i)]; return sane(vec4(unpackHalf2x16(u.x), unpackHalf2x16(u.y))); }
        uvec2 pack(vec4 s) { return uvec2(packHalf2x16(s.xy), packHalf2x16(s.zw)); }

        void advance(ivec2 i, ivec2 w)
        {
            vec4 s = load(i);
            vec2 p = (vec2(w) + 0.5) * Texel;
            float column = depthAt(p);
            if (isnan(column) || isinf(column)) column = 0.0;
            if (column <= 0.0)
            {
                b[at(i)] = uvec2(0u);   // land: the waves stop at the shore
                return;
            }
            vec4 n = load(i + ivec2(0, 1)), so = load(i - ivec2(0, 1)), e = load(i + ivec2(1, 0)), we = load(i - ivec2(1, 0));
            vec4 ne = load(i + ivec2(1, 1)), nw = load(i + ivec2(-1, 1)), se = load(i + ivec2(1, -1)), sw = load(i + ivec2(-1, -1));
            // The isotropic nine-point Laplacian (stable to a Courant number of 0.87).
            vec4 lap = (4.0 * (n + so + e + we) + (ne + nw + se + sw) - 20.0 * s) / (6.0 * Texel * Texel);
            float h = s.x, v = s.y, foam = s.z, took = s.w;
            if (pc.sources != 0)
            {
                float ring;
                float now = taken(p, column, ring);
                float change = clamp(now - took, -BodyDepth, BodyDepth);
                // In shallow water the same step is a far larger share of the water there: its splash and rings are as visible as in deep water.
                float shallow = sqrt(BodyDepth / max(min(column, BodyDepth), 0.5));
                h += Push_ * change * shallow;
                // Churned in a thin ring where a moving body cuts the surface, the more the faster it pushes the water aside.
                float churn = abs(change) / pc.sourceDt;
                foam = max(foam, clamp(churn * 0.03 * shallow, 0.0, 0.9) * max(ring, 0.6 * step(0.02, abs(change))));
                took = now;
            }
            float c2 = Gravity * clamp(column, SlowDepth, FastDepth);
            ivec2 o = pc.origin;
            ivec2 edge = min(w - o, o + S - 1 - w);
            float sponge = 1.0 - smoothstep(0.0, float({{Sponge}}), float(min(edge.x, edge.y)));
            v += pc.dt * (c2 * lap.x + Viscosity * lap.y - Damping * v);
            v *= exp(-pc.dt * SpongeRate * sponge);
            h += pc.dt * v;
            h *= exp(-pc.dt * SpongeRate * 0.25 * sponge);
            // Foam where the waves steepen, fading, spreading a little.
            vec2 slope = vec2(e.x - we.x, n.x - so.x) / (2.0 * Texel);
            foam = max(foam, 0.7 * smoothstep(0.45, 0.9, length(slope)) * smoothstep(0.15, 0.8, column));   // none where the film is too thin to carry a crest
            foam = mix(foam, (n.z + so.z + e.z + we.z) * 0.25, min(pc.dt * 2.0, 1.0)) * exp(-pc.dt * 1.1 / FoamLife);
            b[at(i)] = pack(sane(vec4(h, v, foam, took)));
        }

        void main()
        {
            ivec2 i = ivec2(gl_GlobalInvocationID.xy);
            ivec2 w = worldOf(i, pc.origin);
            if (pc.mode == 0) advance(i, w);
            else if (pc.mode == 1)
            {
                if (w != worldOf(i, pc.previous)) a[at(i)] = uvec2(0u);
            }
            else
            {
                vec4 s = load(i);
                vec2 slope = vec2(load(i + ivec2(1, 0)).x - load(i - ivec2(1, 0)).x, load(i + ivec2(0, 1)).x - load(i - ivec2(0, 1)).x) / (2.0 * Texel);
                imageStore(field, i, vec4(s.x, slope, s.z));
            }
        }
        """);

    public void Dispose()
    {
        if (dropped > 0) Console.WriteLine($"water     ripples: {dropped} body capsules dropped (not finite or absurd)");
        if (GpuTimed > 0)
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"water     ripples: gpu us per frame {GpuMicroseconds / GpuTimed:F1}, of which steps {StepMicroseconds / GpuTimed:F1} ({(double)StepsTimed / GpuTimed:F2} a frame; {GpuTimed} timed)"));
        now.Dispose();
        next.Dispose();
        depth.Dispose();
        surface.Dispose();
        ctx.Shaders.Dispose(program);
    }
}
