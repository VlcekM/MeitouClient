using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>The <c>trails</c> switch (docs/render-terrain.md "Ground trails"): off, none of the trails' code runs or is in the terrain's programs.</summary>
public static class TerrainTrails
{
    public static bool Enabled { get; set; }
}

/// <summary>
/// What pressed a mark: its shape in <see cref="TerrainTrailRenderer.StampFragment"/>. <see cref="Imprint"/> is a planted foot, paw or hoof: the shape of the body's own posed mesh where it
/// touches the ground (<see cref="ITrailImprints"/>). <see cref="Press"/> is a stadium (a body part resting on the ground: <see cref="TrailStamp.Length"/> and
/// <see cref="TrailStamp.Width"/> its whole size), <see cref="Plough"/> the same swept along a slide, feathered at its rear so the next sweep joins it. Their values leave room for the rim
/// and core passes (kind and kind + 1), drawn after the other marks so a body's overlapping parts leave one imprint, not ridges.
/// </summary>
public enum TrailKind { Imprint, Drag = 2, Press = 4, Plough = 6 }

/// <summary>A planted part's mark to render from the body: the character's draw key, the middle of the tile (world x, z), half its side and the height the band starts at.</summary>
internal readonly record struct TrailImprint(long Key, Vector2 Centre, float Half, float Floor);

/// <summary>What renders the bodies' contact with the ground into an atlas of depth tiles, one per mark, for <see cref="TerrainTrailRenderer"/>.</summary>
internal interface ITrailImprints
{
    /// <summary>The atlas's bindless index, valid after a <see cref="RenderImprints"/> that returned more than 0.</summary>
    uint ImprintAtlas { get; }
    /// <summary>Renders the frame's marks, tile i for request i; how many got a tile. Outside any pass.</summary>
    int RenderImprints(IReadOnlyList<TrailImprint> requests);
}

/// <summary>
/// One mark on the ground: <paramref name="Centre"/> in world x, z, <paramref name="Along"/> the unit direction it points in, its size in world units,
/// and how deep it goes (0 to 1 of <see cref="TerrainTrailRenderer.Depth"/>).
/// </summary>
public readonly record struct TrailStamp(Vector2 Centre, Vector2 Along, float Length, float Width, float Strength, TrailKind Kind)
{
    /// <summary>An <see cref="TrailKind.Imprint"/>: the body's draw key, half the tile's side and the height the contact band starts at.</summary>
    public long Body { get; init; }
    public float Half { get; init; }
    public float Floor { get; init; }
    internal Vector4 Tile { get; init; }
}

/// <summary>
/// The ground trails (docs/render-terrain.md "Ground trails"): a height in world-anchored images round the camera's target (not the eye, which zooming moves), addressed toroidally (texel = world mod
/// side, as the material cache's levels), which the terrain's colour programs read as a normal and a height offset. Two levels: a coarse one (<see cref="Texel"/>, reaching 240 units) and a fine one
/// (<see cref="FineTexel"/>, 60 units) whose texels also say whether a mark was recorded there; the programs blend the fine over the coarse. As the target moves a window keeps its
/// texels and clears the strips it gains; marks are stamped as instanced quads into both, and the wind and rain wear them all down by one quad blended over the
/// images, only when enough has worn off to count, so a calm frame with nobody walking records nothing. Render side only: nothing here is saved.
/// </summary>
public sealed unsafe class TerrainTrailRenderer : IDisposable
{
    /// <summary>The images' side in texels, the coarse texel and the fine one in world units (a sole 26 cm long is ten and forty texels), and the windows' snap in texels.</summary>
    public const int Side = 2048, Snap = 64;
    public const float Texel = 0.25f, FineTexel = 0.0625f;

    /// <summary>A full mark's depth in world units (12 cm).</summary>
    public const float Depth = 1.2f;

    /// <summary>How far from the camera's target marks are kept: the window's half side, less the snap it can lag by.</summary>
    public const float Reach = (Side / 2 - Snap) * Texel;

    // The wearing: per game second, 1 / the time a mark takes to fall to 37%: still air, per unit of wind speed (60 units/s, the default weather's top: 150 s),
    // full rain. Worn off in steps of at least 1% (a smaller step is lost in R16F's precision near 1).
    const float StillLife = 900, WindLifeScale = 9000, RainLife = 40, WearStep = 0.01f;

    /// <summary>A quad's half size over the shape's: the room for its rim (<see cref="StampFragment"/> fits the shape in 0.7 of the quad).</summary>
    const float QuadPerShape = 1 / 0.7f;

    /// <summary>The room a body's stadium leaves round it for its rim, in coarse texels (<see cref="StampFragment"/> reads the same 12, scaled by the level).</summary>
    const float BodyMargin = 12;

    /// <summary>The kind that wipes: the quad blended towards 0 by its strength, for the strips the window gains and the wearing.</summary>
    const float WipeKind = 3;

    const string Vertex = """
        #version 450
        layout(location = 0) in vec4 aQuad;   // the centre and half the along axis, in clip space
        layout(location = 1) in vec4 aSide;   // half the across axis, strength, kind
        layout(location = 2) in vec4 aTile;   // an imprint's tile: uv of its first texel in the atlas, its size in uv, the atlas's bindless index
        layout(location = 0) out vec2 vUv;    // -1..1: x across, y along
        layout(location = 1) flat out vec2 vStamp;
        layout(location = 2) out vec2 vImg;    // the position in the image, texels
        layout(location = 3) flat out vec2 vHalf;   // the quad's half sides in texels: across, along (1 clip unit = 1024 texels)
        layout(location = 4) flat out vec4 vTile;
        void main()
        {
            vec2 q = vec2(gl_VertexIndex & 1, gl_VertexIndex >> 1) * 2.0 - 1.0;
            vUv = q;
            vStamp = aSide.zw;
            vTile = aTile;
            gl_Position = vec4(aQuad.xy + q.y * aQuad.zw + q.x * aSide.xy, 0.0, 1.0);
            vImg = (gl_Position.xy * 0.5 + 0.5) * 2048.0;
            vHalf = vec2(length(aSide.xy), length(aQuad.zw)) * 1024.0;
        }
        """;

    /// <summary>
    /// A mark as a height (out: x), whether a mark was recorded here (y: the fine image blends over the coarse by it) and how much of it replaces what is there (a). A shape from its signed distance in quad units: pressed in below 0, a low rim
    /// of the ground pushed aside just outside it. A new mark presses what it lands on flat, so it replaces the old one rather than adding to it. Sizes given in texels are for the coarse
    /// texel and scaled by <c>K</c> (the level's texels per coarse texel) so both levels draw the same mark.
    /// </summary>
    internal const string StampFragment = """
        #version 450
        BINDLESS_DECLARATIONS
        layout(location = 0) in vec2 vUv;
        layout(location = 1) flat in vec2 vStamp;   // strength, kind
        layout(location = 2) in vec2 vImg;
        layout(location = 3) flat in vec2 vHalf;
        layout(location = 4) flat in vec4 vTile;
        layout(location = 0) out vec4 fragColour;
        const float K = TEXEL_SCALE;

        // Value noise fixed to the image's texels (a cell of cs texels, cs dividing 2048, so it wraps with the image): the same ground gets the same irregularity from every stamp.
        float lattice(vec2 c, float period)
        {
            c = mod(c, period);
            return fract(sin(dot(c, vec2(127.1, 311.7))) * 43758.5453);
        }

        float vnoise(float cs)
        {
            vec2 x = vImg / cs;
            vec2 i = floor(x);
            vec2 f = fract(x);
            f = f * f * (3.0 - 2.0 * f);
            float period = 2048.0 / cs;
            return mix(mix(lattice(i, period), lattice(i + vec2(1.0, 0.0), period), f.x),
                       mix(lattice(i + vec2(0.0, 1.0), period), lattice(i + vec2(1.0, 1.0), period), f.x), f.y);
        }

        // A body part's stadium (the quad leaves 12 coarse texels round it for the rim): kinds 4 and 6 draw the rim (a ridge of pushed-up ground, ragged), 5 and 7 the pressed core, in a later
        // pass so that parts of one body overlapping leave one bowl. A sweep (6, 7) heaps its front and its sides higher and fades out at its rear.
        void body(int k, float strength)
        {
            bool core = (k & 1) == 1, sweep = k >= 6;
            vec2 p = vUv * vHalf;
            float margin = 12.0 * K;
            float hw = vHalf.x - margin, seg = max(vHalf.y - margin - hw, 0.0);
            float d = length(vec2(p.x, p.y - clamp(p.y, -seg, seg))) - hw;
            float n1 = vnoise(4.0 * K), n2 = vnoise(16.0 * K), n3 = vnoise(64.0 * K);
            d += ((n1 - 0.5) * 3.0 + (n2 - 0.5) * 4.0) * K;
            float rear = sweep ? smoothstep(-seg - hw, -seg, p.y) : 1.0;
            if (core)
            {
                float bowl = mix(0.85, 1.0, smoothstep(0.0, -hw, d)) * (0.88 + 0.12 * n2);
                fragColour = vec4(-bowl * strength, 1.0, 0.0, smoothstep(1.5 * K, -1.5 * K, d) * rear);
                return;
            }
            float heap = sweep ? 1.3 + smoothstep(0.0, hw, p.y - seg) : 1.0;
            float rim = exp(-pow((d - 2.5 * K) / (2.5 * K), 2.0)) * (0.2 + 0.2 * n3) * heap;
            fragColour = vec4(rim * strength, 1.0, 0.0, smoothstep(6.0 * K, 3.5 * K, d) * rear);
        }

        // The body's depth in its tile at uv (0 to 1 across the tile): 1 where nothing of the body is, read bilinearly.
        float imprintDepth(vec2 uv)
        {
            uint atlas = uint(vTile.w + 0.5);
            float size = float(textureSize(textures2D[nonuniformEXT(atlas)], 0).x), tile = vTile.z * size;
            vec2 at = vTile.xy * size + clamp(uv, 0.0, 1.0) * (tile - 1.0);
            ivec2 b = ivec2(floor(at));
            vec2 f = fract(at);
            ivec2 last = ivec2(vTile.xy * size + tile - 1.0);
            float d = 0.0;
            for (int k = 0; k < 4; k++)
            {
                ivec2 o = ivec2(k & 1, k >> 1);
                d += texelFetch(textures2D[nonuniformEXT(atlas)], min(b + o, last), 0).r * (o.x == 0 ? 1.0 - f.x : f.x) * (o.y == 0 ? 1.0 - f.y : f.y);
            }
            return d;
        }

        // How much of the body presses at one point of its tile: 0 outside it, towards 1 where it reaches lowest.
        float imprintAt(vec2 uv)
        {
            float d = imprintDepth(uv);
            return smoothstep(1.0, 0.92, d) * mix(1.0, 0.72, d);
        }

        // The same over a small disc of the tile (its texels are coarser than the image's), so the outline is a slope and not a staircase.
        float imprintPress(vec2 uv)
        {
            float r = 1.6 * vTile.z / (vTile.z * float(textureSize(textures2D[nonuniformEXT(uint(vTile.w + 0.5))], 0).x));
            float sum = imprintAt(uv) * 2.0;
            for (int k = 0; k < 8; k++)
            {
                float a = float(k) * 0.7853982;
                sum += imprintAt(uv + vec2(cos(a), sin(a)) * r);
            }
            return sum / 10.0;
        }

        // A planted foot, paw or hoof: the shape of the body itself where it meets the ground (its lowest parts deepest), edge and depth carrying the image's noise, and the ground pushed aside
        // round it (where the shape is near but not under).
        void imprint(float strength)
        {
            float n = vnoise(16.0 * K), m = vnoise(48.0 * K);
            vec2 uv = vUv * 0.5 + 0.5 + (vec2(n, m) - 0.5) * 0.012;
            float press = imprintPress(uv);
            float near = 0.0;
            for (int k = 0; k < 8; k++)
            {
                float a = float(k) * 0.7853982;
                near = max(near, imprintAt(uv + vec2(cos(a), sin(a)) * 0.035));
            }
            float rim = max(near - press, 0.0) * 0.3 * (0.75 + 0.5 * m);
            fragColour = vec4((rim - press * (0.88 + 0.12 * n)) * strength, 1.0, 0.0, clamp(max(press * 4.0, near * 1.5), 0.0, 1.0));
        }

        void main()
        {
            float strength = vStamp.x;
            int kind = int(vStamp.y + 0.5);
            if (kind == 3) fragColour = vec4(0.0, 0.0, 0.0, strength);
            else if (kind >= 4) body(kind, strength);
            else if (kind == 2)
            {
                // A furrow: only its section, feathered at both ends so the next one joins it.
                float across = abs(vUv.x);
                float rim = exp(-pow((across - 0.8) / 0.12, 2.0)) * 0.3;
                float ends = smoothstep(1.0, 0.6, abs(vUv.y));
                fragColour = vec4((rim - smoothstep(0.72, 0.35, across)) * strength, 1.0, 0.0, ends * smoothstep(1.0, 0.88, across));
            }
            else imprint(strength);
        }
        """;

    /// <summary>One quad's instance row (<see cref="Vertex"/>'s inputs).</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Row
    {
        public const int Size = 48;
        public Vector4 Quad, Across, Tile;
    }

    static readonly VertexLayout Rows = new(
        new VertexInput(0, InternalFormat.Rgba32f, Row.Size, true),
        new VertexInput(1, InternalFormat.Rgba32f, Row.Size, true),
        new VertexInput(2, InternalFormat.Rgba32f, Row.Size, true));

    static readonly BlendState Blend = new(true, BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

    readonly IGpuContext gpu;
    readonly NativeFrame frame;
    readonly List<TrailStamp> stamps = [];
    readonly Level coarse, fine;
    float worn;               // the wearing owed, as -ln of what is left

    public TerrainTrailRenderer(IGpuContext gpu)
    {
        this.gpu = gpu;
        frame = new NativeFrame(gpu);
        coarse = new Level(this, Texel, InternalFormat.R16f, "terrain trails");
        fine = new Level(this, FineTexel, InternalFormat.RG16f, "terrain trails fine");
    }

    /// <summary>Renders the imprints' shapes (the characters); without it an imprint leaves nothing.</summary>
    internal ITrailImprints? Imprints { get; set; }

    readonly List<TrailImprint> imprintRequests = [];

    /// <summary>The frame's imprints rendered, each stamp told its tile; those that got none are dropped.</summary>
    void RenderImprints()
    {
        imprintRequests.Clear();
        foreach (var s in stamps)
            if (s.Kind == TrailKind.Imprint) imprintRequests.Add(new TrailImprint(s.Body, s.Centre, s.Half, s.Floor));
        int done = imprintRequests.Count > 0 && Imprints is { } source ? source.RenderImprints(imprintRequests) : 0;
        int next = 0;
        const int Columns = Characters.CharacterRenderer.ImprintColumns;
        for (int i = stamps.Count - 1; i >= 0; i--)
            if (stamps[i].Kind == TrailKind.Imprint && imprintRequests.Count - 1 - next++ >= done) stamps.RemoveAt(i);
        next = 0;
        for (int i = 0; i < stamps.Count; i++)
        {
            if (stamps[i].Kind != TrailKind.Imprint) continue;
            int k = next++;
            stamps[i] = stamps[i] with { Tile = new Vector4(k % Columns / (float)Columns, k / Columns / (float)Columns, 1f / Columns, Imprints!.ImprintAtlas) };
        }
    }

    /// <summary>Queues a mark; <see cref="Record"/> draws it. One outside a window is dropped from that level.</summary>
    public void Stamp(in TrailStamp stamp) => stamps.Add(stamp);

    /// <summary>Forgets every mark (a new world).</summary>
    public void Clear()
    {
        coarse.Forget();
        fine.Forget();
        stamps.Clear();
    }

    /// <summary>
    /// The frame's marks: the windows moved to <paramref name="centre"/>, the wearing of <paramref name="gameSeconds"/> at <paramref name="rain"/> (0 to 1) and
    /// <paramref name="wind"/> (units/s), then the queued stamps. Before the scene (outside a pass).
    /// </summary>
    public void Record(Vector3 centre, float gameSeconds, float rain, float wind)
    {
        worn += gameSeconds * (1 / StillLife + wind / WindLifeScale + rain / RainLife);
        float share = 1 - MathF.Exp(-worn);
        bool wear = share >= WearStep;
        if (wear) worn = 0;
        RenderImprints();
        foreach (var level in new[] { coarse, fine })
        {
            level.Begin(centre, wear ? share : 0);
            foreach (var s in stamps) level.AddStamp(s);
            level.Finish();
        }
        stamps.Clear();
    }

    /// <summary>The images for the patches' colour programs and, displaced with no parallax to march the marks, their mesh (<see cref="TerrainShaders.PatchEvaluation"/>).</summary>
    internal void Apply(ref TerrainConstants c)
    {
        if (coarse.Index == uint.MaxValue || coarse.Origin is not { } o) return;
        float half = Side * Texel / 2;
        c.Trails = coarse.Index;
        c.TrailDepth = Depth;
        c.TrailMesh = c.Displace > 0 && c.Parallax <= 0 ? 1 : 0;
        c.TrailWindow = new Vector4(o.X * Texel + half, o.Z * Texel + half, half, 1 / (Side * Texel));
        if (fine.Index != uint.MaxValue && fine.Origin is { } f)
        {
            float fineHalf = Side * FineTexel / 2;
            c.TrailFine = fine.Index;
            c.TrailFineWindow = new Vector4(f.X * FineTexel + fineHalf, f.Z * FineTexel + fineHalf, fineHalf, 1 / (Side * FineTexel));
        }
    }

    public void Dispose()
    {
        coarse.Dispose();
        fine.Dispose();
        frame.Dispose();
    }

    /// <summary>One image of the trails: its texel, its window, its wipes and the stamps of the frame.</summary>
    sealed class Level(TerrainTrailRenderer owner, float texel, InternalFormat format, string name)
    {
        readonly GpuProgram program = owner.frame.Program(Vertex, StampFragment.Replace("TEXEL_SCALE", (Texel / texel).ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture)).Replace("BINDLESS_DECLARATIONS", BindlessTable.Declarations(1)), name);
        readonly List<Row> cores = [];
        GpuPipeline? pipeline;
        GpuTexture? target;
        Row[] rows = new Row[256];
        int count;
        bool fresh;

        public uint Index { get; private set; } = uint.MaxValue;

        /// <summary>The window's first texel in world texels, null until it has one (a jump or a new world).</summary>
        public (int X, int Z)? Origin { get; private set; }

        public void Forget() => Origin = null;

        /// <summary>The window moved to <paramref name="centre"/> and the wearing <paramref name="wearShare"/> (0: none) queued.</summary>
        public void Begin(Vector3 centre, float wearShare)
        {
            Ensure();
            count = 0;
            cores.Clear();
            Move(centre);
            if (wearShare > 0) WipeRange(0, Side, 0, Side, wearShare);
        }

        /// <summary>Draws what was queued, the stamps' cores after their rims.</summary>
        public void Finish()
        {
            foreach (var core in cores) Add(core);
            if (count > 0 || fresh) Draw();
        }

        void Ensure()
        {
            if (target is not null) return;
            var gpu = owner.gpu;
            target = gpu.CreateTexture(new TextureDesc(format, Side, Side, Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: name));
            var sampler = gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.Repeat,
                TextureWrapMode.Repeat, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
            Index = gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(sampler, target.View()));
            fresh = true;
        }

        /// <summary>The window to <paramref name="centre"/>: the columns and rows it gains are wiped (their texels held what it left behind); all of it after a jump.</summary>
        void Move(Vector3 centre)
        {
            int x = Snapped(centre.X), z = Snapped(centre.Z);
            if (Origin is not { } was || Math.Abs(x - was.X) >= Side || Math.Abs(z - was.Z) >= Side) WipeRange(0, Side, 0, Side, 1);
            else
            {
                if (x > was.X) WipeWrapped(was.X + Side, x + Side, 0, Side);
                if (x < was.X) WipeWrapped(x, was.X, 0, Side);
                if (z > was.Z) WipeWrapped(0, Side, was.Z + Side, z + Side);
                if (z < was.Z) WipeWrapped(0, Side, z, was.Z);
            }
            Origin = (x, z);
        }

        int Snapped(float world) => (int)MathF.Floor((world / texel - Side / 2f) / Snap) * Snap;

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

        /// <summary>A mark as a quad, and again on the far side of the image where it crosses the wrap.</summary>
        public void AddStamp(in TrailStamp s)
        {
            if (Origin is not { } o) return;
            var (x, z) = o;
            float tx = s.Centre.X / texel, tz = s.Centre.Y / texel;
            if (tx < x || tz < z || tx >= x + Side || tz >= z + Side) return;
            bool body = s.Kind >= TrailKind.Press, imprint = s.Kind == TrailKind.Imprint;
            float margin = BodyMargin * Texel / texel;
            float along = imprint ? s.Half / texel : body ? s.Length * 0.5f / texel + margin : s.Length * 0.5f * QuadPerShape / texel;
            float across = imprint ? s.Half / texel : body ? s.Width * 0.5f / texel + margin : s.Width * 0.5f / texel;
            var a = s.Along * along;
            var b = new Vector2(s.Along.Y, -s.Along.X) * across;
            float reach = a.Length() + b.Length();
            float ix = ((tx % Side) + Side) % Side, iz = ((tz % Side) + Side) % Side;
            for (int i = ix < reach ? 1 : 0; i >= (ix > Side - reach ? -1 : 0); i--)
                for (int j = iz < reach ? 1 : 0; j >= (iz > Side - reach ? -1 : 0); j--)
                {
                    var centre = new Vector2(ix + i * Side, iz + j * Side) / Side * 2 - Vector2.One;
                    var row = new Row { Quad = new Vector4(centre, a.X * 2 / Side, a.Y * 2 / Side), Across = new Vector4(b.X * 2 / Side, b.Y * 2 / Side, s.Strength, (float)s.Kind), Tile = s.Tile };
                    Add(row);
                    if (body) cores.Add(row with { Across = row.Across with { W = row.Across.W + 1 } });
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
            var gpu = owner.gpu;
            var cmd = gpu.BeginNative(name);
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
                cmd.BindPipeline(pipeline ??= gpu.Pipelines.Get(state.Pipeline(program, Rows, PrimitiveType.TriangleStrip, t.Formats, name)));
                owner.frame.Bind(cmd, program, default);
                Span<GpuBufferBinding> buffers = [new(slice.Handle, slice.Offset), new(slice.Handle, slice.Offset + 16), new(slice.Handle, slice.Offset + 32)];
                cmd.BindVertexBuffers(0, buffers);
                cmd.Draw(4, (uint)count);
            }
            cmd.EndRendering();
            cmd.Order();
            gpu.EndNative(cmd);
        }

        public void Dispose()
        {
            var gpu = owner.gpu;
            if (Index != uint.MaxValue) gpu.Bindless.Free(BindlessKind.Texture2D, Index);
            target?.Dispose();
            gpu.Pipelines.Forget(program);
            gpu.Shaders.Dispose(program);
        }
    }
}
