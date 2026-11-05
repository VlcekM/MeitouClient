using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>The <c>trails</c> switch (docs/render-terrain.md "Ground trails"): off, none of the trails' code runs or is in the terrain's programs.</summary>
public static class TerrainTrails
{
    public static bool Enabled { get; set; }
}

/// <summary>What pressed a mark: its shape in <see cref="TerrainTrailRenderer.StampFragment"/>.</summary>
public enum TrailKind { Foot, Paw, Drag }

/// <summary>
/// One mark on the ground: <paramref name="Centre"/> in world x, z, <paramref name="Along"/> the unit direction it points in, its size in world units,
/// and how deep it goes (0 to 1 of <see cref="TerrainTrailRenderer.Depth"/>).
/// </summary>
public readonly record struct TrailStamp(Vector2 Centre, Vector2 Along, float Length, float Width, float Strength, TrailKind Kind);

/// <summary>
/// The ground trails (docs/render-terrain.md "Ground trails"): a height in a world-anchored R16F image round the eye, addressed toroidally (texel = world mod
/// side, as the material cache's levels), which the terrain's colour programs read as a normal and a height offset. As the eye moves the window keeps its
/// texels and clears the strips it gains; marks are stamped as instanced quads, and the wind and rain wear them all down by one quad blended over the
/// image, only when enough has worn off to count, so a calm frame with nobody walking records nothing. Render side only: nothing here is saved.
/// </summary>
public sealed unsafe class TerrainTrailRenderer : IDisposable
{
    /// <summary>The image's side in texels, a texel in world units (2.5 cm: a sole 26 cm long is ten texels), and the window's snap in texels.</summary>
    public const int Side = 2048, Snap = 64;
    public const float Texel = 0.25f;

    /// <summary>A full mark's depth in world units (6 cm).</summary>
    public const float Depth = 0.6f;

    /// <summary>How far from the eye marks are kept: the window's half side, less the snap it can lag by.</summary>
    public const float Reach = (Side / 2 - Snap) * Texel;

    // The wearing: per game second, 1 / the time a mark takes to fall to 37%: still air, per unit of wind speed (60 units/s, the default weather's top: 150 s),
    // full rain. Worn off in steps of at least 1% (a smaller step is lost in R16F's precision near 1).
    const float StillLife = 900, WindLifeScale = 9000, RainLife = 40, WearStep = 0.01f;

    /// <summary>A quad's half size over the shape's: the room for its rim (<see cref="StampFragment"/> fits the shape in 0.7 of the quad).</summary>
    const float QuadPerShape = 1 / 0.7f;

    /// <summary>The kind that wipes: the quad blended towards 0 by its strength, for the strips the window gains and the wearing.</summary>
    const float WipeKind = 3;

    const string Vertex = """
        #version 450
        layout(location = 0) in vec4 aQuad;   // the centre and half the along axis, in clip space
        layout(location = 1) in vec4 aSide;   // half the across axis, strength, kind
        layout(location = 0) out vec2 vUv;    // -1..1: x across, y along
        layout(location = 1) flat out vec2 vStamp;
        void main()
        {
            vec2 q = vec2(gl_VertexIndex & 1, gl_VertexIndex >> 1) * 2.0 - 1.0;
            vUv = q;
            vStamp = aSide.zw;
            gl_Position = vec4(aQuad.xy + q.y * aQuad.zw + q.x * aSide.xy, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// A mark as a height (out: x) and how much of it replaces what is there (a). A shape from its signed distance in quad units: pressed in below 0, a low rim
    /// of the ground pushed aside just outside it. A new mark presses what it lands on flat, so it replaces the old one rather than adding to it.
    /// </summary>
    internal const string StampFragment = """
        #version 450
        layout(location = 0) in vec2 vUv;
        layout(location = 1) flat in vec2 vStamp;   // strength, kind
        layout(location = 0) out vec4 fragColour;

        // A sole, toes towards +y: a narrow heel widening to the ball (a capsule whose radius grows along it).
        float sole(vec2 q)
        {
            float t = clamp((q.y + 0.42) / 0.8, 0.0, 1.0);
            return length(q - vec2(0.0, mix(-0.42, 0.38, t))) - mix(0.17, 0.26, t);
        }

        void shape(float d, float strength)
        {
            float press = smoothstep(0.02, -0.12, d);
            float rim = exp(-pow((d - 0.07) / 0.07, 2.0)) * 0.3;
            fragColour = vec4((rim - press) * strength, 0.0, 0.0, smoothstep(0.26, 0.14, d));
        }

        void main()
        {
            float strength = vStamp.x;
            if (vStamp.y > 2.5) fragColour = vec4(0.0, 0.0, 0.0, strength);
            else if (vStamp.y > 1.5)
            {
                // A furrow: only its section, feathered at both ends so the next one joins it.
                float across = abs(vUv.x);
                float rim = exp(-pow((across - 0.8) / 0.12, 2.0)) * 0.3;
                float ends = smoothstep(1.0, 0.6, abs(vUv.y));
                fragColour = vec4((rim - smoothstep(0.72, 0.35, across)) * strength, 0.0, 0.0, ends * smoothstep(1.0, 0.88, across));
            }
            else if (vStamp.y > 0.5) shape(length(vUv) - 0.6, strength);
            else shape(sole(vUv), strength);
        }
        """;

    /// <summary>One quad's instance row (<see cref="Vertex"/>'s inputs).</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Row
    {
        public const int Size = 32;
        public Vector4 Quad, Across;
    }

    static readonly VertexLayout Rows = new(
        new VertexInput(0, InternalFormat.Rgba32f, Row.Size, true),
        new VertexInput(1, InternalFormat.Rgba32f, Row.Size, true));

    static readonly BlendState Blend = new(true, BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

    readonly IGpuContext gpu;
    readonly NativeFrame frame;
    readonly GpuProgram program;
    readonly List<TrailStamp> stamps = [];
    GpuPipeline? pipeline;
    GpuTexture? target;
    uint index = uint.MaxValue;
    Row[] rows = new Row[256];
    int count;
    (int X, int Z)? origin;   // the window's first texel in world texels, null until it has one (a jump or a new world)
    float worn;               // the wearing owed, as -ln of what is left
    bool fresh;

    public TerrainTrailRenderer(IGpuContext gpu)
    {
        this.gpu = gpu;
        frame = new NativeFrame(gpu);
        program = frame.Program(Vertex, StampFragment, "terrain trails");
    }

    /// <summary>Queues a mark; <see cref="Record"/> draws it. One outside the window is dropped.</summary>
    public void Stamp(in TrailStamp stamp) => stamps.Add(stamp);

    /// <summary>Forgets every mark (a new world).</summary>
    public void Clear()
    {
        origin = null;
        stamps.Clear();
    }

    /// <summary>
    /// The frame's marks: the window moved to <paramref name="eye"/>, the wearing of <paramref name="gameSeconds"/> at <paramref name="rain"/> (0 to 1) and
    /// <paramref name="wind"/> (units/s), then the queued stamps. Before the scene (outside a pass).
    /// </summary>
    public void Record(Vector3 eye, float gameSeconds, float rain, float wind)
    {
        Ensure();
        count = 0;
        Move(eye);
        Wear(gameSeconds, rain, wind);
        foreach (var s in stamps) AddStamp(s);
        stamps.Clear();
        if (count > 0 || fresh) Draw();
    }

    void Ensure()
    {
        if (target is not null) return;
        target = gpu.CreateTexture(new TextureDesc(InternalFormat.R16f, Side, Side, Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "terrain trails"));
        var sampler = gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.Repeat,
            TextureWrapMode.Repeat, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
        index = gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(sampler, target.View()));
        fresh = true;
    }

    /// <summary>The window to <paramref name="eye"/>: the columns and rows it gains are wiped (their texels held what it left behind); all of it after a jump.</summary>
    void Move(Vector3 eye)
    {
        int x = Snapped(eye.X), z = Snapped(eye.Z);
        if (origin is not { } was || Math.Abs(x - was.X) >= Side || Math.Abs(z - was.Z) >= Side) WipeRange(0, Side, 0, Side, 1);
        else
        {
            if (x > was.X) WipeWrapped(was.X + Side, x + Side, 0, Side);
            if (x < was.X) WipeWrapped(x, was.X, 0, Side);
            if (z > was.Z) WipeWrapped(0, Side, was.Z + Side, z + Side);
            if (z < was.Z) WipeWrapped(0, Side, z, was.Z);
        }
        origin = (x, z);
    }

    static int Snapped(float world) => (int)MathF.Floor((world / Texel - Side / 2f) / Snap) * Snap;

    /// <summary>The world texel range [x0, x1) × [z0, z1), under a side each, wiped in the wrapped image.</summary>
    void WipeWrapped(int x0, int x1, int z0, int z1)
    {
        foreach (var (ax, bx) in Unwrapped(x0, x1))
            foreach (var (az, bz) in Unwrapped(z0, z1))
                WipeRange(ax, bx, az, bz, 1);
    }

    /// <summary>A range of world texels as one or two ranges of the image.</summary>
    static (int, int)[] Unwrapped(int from, int to)
    {
        int start = ((from % Side) + Side) % Side, length = to - from;
        return start + length <= Side ? [(start, start + length)] : [(start, Side), (0, start + length - Side)];
    }

    void WipeRange(int x0, int x1, int z0, int z1, float share)
    {
        var centre = new Vector2(x0 + x1, z0 + z1) / Side - Vector2.One;
        Add(new Row { Quad = new Vector4(centre, 0, (z1 - z0) / (float)Side), Across = new Vector4((x1 - x0) / (float)Side, 0, share, WipeKind) });
    }

    void Wear(float gameSeconds, float rain, float wind)
    {
        worn += gameSeconds * (1 / StillLife + wind / WindLifeScale + rain / RainLife);
        float share = 1 - MathF.Exp(-worn);
        if (share < WearStep) return;
        WipeRange(0, Side, 0, Side, share);
        worn = 0;
    }

    /// <summary>A mark as a quad, and again on the far side of the image where it crosses the wrap.</summary>
    void AddStamp(in TrailStamp s)
    {
        var (x, z) = origin!.Value;
        float tx = s.Centre.X / Texel, tz = s.Centre.Y / Texel;
        if (tx < x || tz < z || tx >= x + Side || tz >= z + Side) return;
        bool square = s.Kind != TrailKind.Drag;
        float along = s.Length * 0.5f * QuadPerShape / Texel, across = (square ? s.Length * 0.5f * QuadPerShape : s.Width * 0.5f) / Texel;
        var a = s.Along * along;
        var b = new Vector2(s.Along.Y, -s.Along.X) * across;
        float reach = a.Length() + b.Length();
        float ix = ((tx % Side) + Side) % Side, iz = ((tz % Side) + Side) % Side;
        for (int i = ix < reach ? 1 : 0; i >= (ix > Side - reach ? -1 : 0); i--)
            for (int j = iz < reach ? 1 : 0; j >= (iz > Side - reach ? -1 : 0); j--)
            {
                var centre = new Vector2(ix + i * Side, iz + j * Side) / Side * 2 - Vector2.One;
                Add(new Row { Quad = new Vector4(centre, a.X * 2 / Side, a.Y * 2 / Side), Across = new Vector4(b.X * 2 / Side, b.Y * 2 / Side, s.Strength, (float)s.Kind) });
            }
    }

    void Add(in Row row)
    {
        if (count == rows.Length) Array.Resize(ref rows, count * 2);
        rows[count++] = row;
    }

    /// <summary>The frame's quads, in order, in one pass: the wipes, then the stamps over them.</summary>
    void Draw()
    {
        var cmd = gpu.BeginNative("terrain trails");
        var t = PassTargets.Of(target, null);
        t = t with { Colour = new RenderTarget(target!.Attachment(), fresh ? LoadOp.Clear : LoadOp.Load, fresh ? new ClearColour(0f, 0f, 0f, 0f) : default) };
        fresh = false;
        cmd.BeginRendering(t.Rendering);
        if (count > 0)
        {
            var slice = gpu.Frame.AllocateConstants((ulong)(count * Row.Size), 16);
            MemoryMarshal.AsBytes(rows.AsSpan(0, count)).CopyTo(new Span<byte>(slice.Pointer, count * Row.Size));
            var state = DrawState.For(t.Formats, gpu.DepthClamp, blend: Blend);
            state.Record(cmd, t);
            cmd.BindPipeline(pipeline ??= gpu.Pipelines.Get(state.Pipeline(program, Rows, PrimitiveType.TriangleStrip, t.Formats, "terrain trails")));
            frame.Bind(cmd, program, default);
            Span<GpuBufferBinding> buffers = [new(slice.Handle, slice.Offset), new(slice.Handle, slice.Offset + 16)];
            cmd.BindVertexBuffers(0, buffers);
            cmd.Draw(4, (uint)count);
        }
        cmd.EndRendering();
        cmd.Order();
        gpu.EndNative(cmd);
    }

    /// <summary>The image for the patches' colour programs and, displaced with no parallax to march the marks, their mesh (<see cref="TerrainShaders.PatchEvaluation"/>).</summary>
    internal void Apply(ref TerrainConstants c)
    {
        if (index == uint.MaxValue || origin is not { } o) return;
        float half = Side * Texel / 2;
        c.Trails = index;
        c.TrailDepth = Depth;
        c.TrailMesh = c.Displace > 0 && c.Parallax <= 0 ? 1 : 0;
        c.TrailWindow = new Vector4(o.X * Texel + half, o.Z * Texel + half, half, 1 / (Side * Texel));
    }

    public void Dispose()
    {
        if (index != uint.MaxValue) gpu.Bindless.Free(BindlessKind.Texture2D, index);
        target?.Dispose();
        gpu.Pipelines.Forget(program);
        gpu.Shaders.Dispose(program);
        frame.Dispose();
    }
}
