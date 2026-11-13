using Meitou.Game.Rendering.Frame;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Terrain;

/// <summary>
/// The <c>trails</c> switch (docs/render-terrain.md "Ground trails"): off, none of the trails' code runs or is in the terrain's programs.
/// </summary>
public static class TerrainTrails
{
    /// <summary>
    /// Whether the ground trails are on.
    /// </summary>
    public static bool Enabled { get; set; }
}

/// <summary>
/// What pressed a mark: its shape in <see cref="TerrainTrailRenderer.StampFragment"/>. <see cref="Imprint"/> is a planted foot, paw or hoof: the
/// shape of the body's own posed mesh where it touches the ground (<see cref="ITrailImprints"/>). <see cref="Press"/> is a stadium (a body part
/// resting on the ground: <see cref="TrailStamp.Length"/> and <see cref="TrailStamp.Width"/> its whole size), <see cref="Plough"/> the same swept
/// along a slide, feathered at its rear so the next sweep joins it. Their values leave room for the rim and core passes (kind and kind + 1), drawn
/// after the other marks so a body's overlapping parts leave one imprint, not ridges.
/// </summary>
public enum TrailKind
{
    /// <summary>
    /// A planted foot, paw or hoof: the body's own mesh where it touches the ground.
    /// </summary>
    Imprint,

    /// <summary>
    /// A furrow: only its section, feathered at both ends so the next one joins it.
    /// </summary>
    Drag = 2,

    /// <summary>
    /// A stadium: a body part resting on the ground.
    /// </summary>
    Press = 4,

    /// <summary>
    /// A stadium swept along a slide, feathered at its rear.
    /// </summary>
    Plough = 6,
}

/// <summary>
/// A planted part's mark to render from the body: the character's draw key, the middle of the tile (world x, z), half its side and the height the
/// band starts at.
/// </summary>
internal readonly record struct TrailImprint(long Key, Vector2 Centre, float Half, float Floor);

/// <summary>
/// What renders the bodies' contact with the ground into an atlas of depth tiles, one per mark, for <see cref="TerrainTrailRenderer"/>.
/// </summary>
internal interface ITrailImprints
{
    /// <summary>
    /// The atlas's bindless index, valid after a <see cref="RenderImprints"/> that returned more than 0.
    /// </summary>
    uint ImprintAtlas { get; }

    /// <summary>
    /// Renders the frame's marks, tile i for request i; how many got a tile. Outside any pass.
    /// </summary>
    int RenderImprints(IReadOnlyList<TrailImprint> requests);
}

/// <summary>
/// One mark on the ground: <paramref name="Centre"/> in world x, z, <paramref name="Along"/> the unit direction it points in, its size in world
/// units, and how deep it goes (0 to 1 of <see cref="TerrainTrailRenderer.Depth"/>).
/// </summary>
public readonly record struct TrailStamp(Vector2 Centre, Vector2 Along, float Length, float Width, float Strength, TrailKind Kind)
{
    /// <summary>
    /// An <see cref="TrailKind.Imprint"/>'s body: its draw key.
    /// </summary>
    public long Body { get; init; }

    /// <summary>
    /// An <see cref="TrailKind.Imprint"/>'s half the tile's side.
    /// </summary>
    public float Half { get; init; }

    /// <summary>
    /// An <see cref="TrailKind.Imprint"/>'s height the contact band starts at.
    /// </summary>
    public float Floor { get; init; }

    /// <summary>
    /// An <see cref="TrailKind.Imprint"/>'s tile: uv of its first texel in the atlas, its size in uv, the atlas's bindless index.
    /// </summary>
    internal Vector4 Tile { get; init; }
}

/// <summary>
/// The ground trails (docs/render-terrain.md "Ground trails"): a height in world-anchored images round the camera's target (not the eye, which
/// zooming moves), addressed toroidally (texel = world mod side, as the material cache's levels), which the terrain's colour programs read as a
/// normal and a height offset. Two levels: a coarse one (<see cref="Texel"/>, reaching 240 units) and a fine one (<see cref="FineTexel"/>, 60 units)
/// whose texels also say whether a mark was recorded there; the programs blend the fine over the coarse. As the target moves a window keeps its
/// texels and clears the strips it gains; marks are stamped as instanced quads into both, and the wind and rain wear them all down by one quad
/// blended over the images, only when enough has worn off to count, so a calm frame with nobody walking records nothing. Render side only: nothing
/// here is saved.
/// </summary>
public sealed unsafe class TerrainTrailRenderer : IDisposable
{
    #region Recording

    /// <summary>
    /// The images' side in texels.
    /// </summary>
    public const int Side = 2048;

    /// <summary>
    /// The windows' snap in texels.
    /// </summary>
    public const int Snap = 64;

    /// <summary>
    /// The coarse texel in world units (a sole 26 cm long is ten texels).
    /// </summary>
    public const float Texel = 0.25f;

    /// <summary>
    /// The fine texel in world units (a sole 26 cm long is forty texels).
    /// </summary>
    public const float FineTexel = 0.0625f;

    /// <summary>
    /// A full mark's depth in world units (12 cm).
    /// </summary>
    public const float Depth = 1.2f;

    /// <summary>
    /// How far from the camera's target marks are kept: the window's half side, less the snap it can lag by.
    /// </summary>
    public const float Reach = (Side / 2 - Snap) * Texel;

    /// <summary>
    /// The game seconds a mark takes to fall to 37% in still air: the wearing per game second is 1 / it, plus the wind's and the rain's.
    /// </summary>
    const float StillLife = 900;

    /// <summary>
    /// The same per unit of wind speed: the wearing adds wind / it (60 units/s, the default weather's top, gives 150 s).
    /// </summary>
    const float WindLifeScale = 9000;

    /// <summary>
    /// The same in full rain.
    /// </summary>
    const float RainLife = 40;

    /// <summary>
    /// The least share worn off in one step (1%): a smaller step is lost in R16F's precision near 1.
    /// </summary>
    const float WearStep = 0.01f;

    /// <summary>
    /// A quad's half size over the shape's: the room for its rim (<see cref="StampFragment"/> fits the shape in 0.7 of the quad).
    /// </summary>
    const float QuadPerShape = 1 / 0.7f;

    /// <summary>
    /// The room a body's stadium leaves round it for its rim, in coarse texels (<see cref="StampFragment"/> reads the same 12, scaled by the level).
    /// </summary>
    const float BodyMargin = 12;

    /// <summary>
    /// The kind that wipes: the quad blended towards 0 by its strength, for the strips the window gains and the wearing.
    /// </summary>
    const float WipeKind = 3;

    /// <summary>
    /// The stamps' vertex program: one instanced quad per <see cref="Row"/>, placed in the image's clip space.
    /// </summary>
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
    /// A mark as a height (out: x), whether a mark was recorded here (y: the fine image blends over the coarse by it) and how much of it replaces
    /// what is there (a). A shape from its signed distance in quad units: pressed in below 0, a low rim of the ground pushed aside just outside it. A
    /// new mark presses what it lands on flat, so it replaces the old one rather than adding to it. Sizes given in texels are for the coarse texel
    /// and scaled by <c>K</c> (the level's texels per coarse texel) so both levels draw the same mark.
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

        // Tile uv per world unit.
        float uvPerUnit() { return 1.0 / (2.0 * vHalf.x * 0.25 / K); }

        // The same over a disc of 0.4 world units: a mesh's outline is a cliff a texel wide, which the terrain's relief cannot show without stairs; sand slumps to a slope anyway. The mean is
        // then narrowed back round its middle, so the slope lies on the outline and the print does not grow by the disc.
        float imprintPress(vec2 uv)
        {
            float r = (K < 2.0 ? 0.9 : 0.4) * uvPerUnit();   // the coarse image (K 1, a texel of 0.25) needs the wider disc: an outline under three of its texels turns to stairs
            float sum = imprintAt(uv) * 2.0, total = 2.0;
            for (int ring = 1; ring <= 2; ring++)
                for (int k = 0; k < 8; k++)
                {
                    float a = (float(k) + 0.5 * float(ring)) * 0.7853982, w = ring == 1 ? 1.0 : 0.6;
                    sum += imprintAt(uv + vec2(cos(a), sin(a)) * r * float(ring) * 0.5) * w;
                    total += w;
                }
            float mean = sum / total;
            return smoothstep(0.38, 0.72, mean) * max(mean, 0.72);
        }

        // A planted foot, paw or hoof: the shape of the body itself where it meets the ground (its lowest parts deepest), edge and depth carrying the image's noise, and the ground pushed aside
        // round it (where the shape is near but not under).
        void imprint(float strength)
        {
            float n = vnoise(16.0 * K), m = vnoise(48.0 * K);
            vec2 uv = vUv * 0.5 + 0.5 + (vec2(n, m) - 0.5) * 0.08 * uvPerUnit();
            float press = imprintPress(uv);
            float near = 0.0;
            for (int k = 0; k < 8; k++)
            {
                float a = float(k) * 0.7853982;
                near = max(near, imprintAt(uv + vec2(cos(a), sin(a)) * 0.22 * uvPerUnit()));
            }
            float rim = max(near - press, 0.0) * 0.3 * (0.75 + 0.5 * m);
            // Written whole out to a margin round the shape: where the fine image has only part of a mark, the coarse one (four times coarser) shows through as a row of beads along its outline.
            float around = near;
            for (int k = 0; k < 8; k++)
            {
                float a = float(k) * 0.7853982;
                around = max(around, imprintAt(uv + vec2(cos(a), sin(a)) * 0.7 * uvPerUnit()));
            }
            fragColour = vec4((rim - press * (0.88 + 0.12 * n)) * strength, 1.0, 0.0, smoothstep(0.0, 0.05, around));
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

    /// <summary>
    /// The instance rows' vertex inputs: <see cref="Row.Quad"/>, <see cref="Row.Across"/> and <see cref="Row.Tile"/>.
    /// </summary>
    static readonly VertexLayout Rows = new(
        new VertexInput(0, InternalFormat.Rgba32F, Row.Size, true),
        new VertexInput(1, InternalFormat.Rgba32F, Row.Size, true),
        new VertexInput(2, InternalFormat.Rgba32F, Row.Size, true));

    /// <summary>
    /// The stamps' blend: each quad over what is there by its alpha.
    /// </summary>
    static readonly BlendState Blend = new(true, BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

    /// <summary>
    /// The device the images live on.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// The native frame the stamp programs are made and bound from.
    /// </summary>
    readonly NativeFrame _frame;

    /// <summary>
    /// The marks queued for the next <see cref="Record"/>.
    /// </summary>
    readonly List<TrailStamp> _stamps = [];

    /// <summary>
    /// The coarse image (<see cref="Texel"/>).
    /// </summary>
    readonly Level _coarse;

    /// <summary>
    /// The fine image (<see cref="FineTexel"/>).
    /// </summary>
    readonly Level _fine;

    /// <summary>
    /// The frame's imprints to render, reused each frame.
    /// </summary>
    readonly List<TrailImprint> _imprintRequests = [];

    /// <summary>
    /// The wearing owed, as -ln of what is left.
    /// </summary>
    float _worn;

    /// <summary>
    /// Makes the two images' levels; their textures are made on the first <see cref="Record"/>.
    /// </summary>
    public TerrainTrailRenderer(IGpuContext gpu)
    {
        this._gpu = gpu;
        _frame = new NativeFrame(gpu);
        _coarse = new Level(this, Texel, InternalFormat.R16F, "terrain trails");
        _fine = new Level(this, FineTexel, InternalFormat.Rg16F, "terrain trails fine");
    }

    /// <summary>
    /// Renders the imprints' shapes (the characters); without it an imprint leaves nothing.
    /// </summary>
    internal ITrailImprints? Imprints { get; set; }

    /// <summary>
    /// Queues a mark; <see cref="Record"/> draws it. One outside a window is dropped from that level.
    /// </summary>
    public void Stamp(in TrailStamp stamp) => _stamps.Add(stamp);

    /// <summary>
    /// Forgets every mark (a new world).
    /// </summary>
    public void Clear()
    {
        _coarse.Forget();
        _fine.Forget();
        _stamps.Clear();
        Array.Fill(_markedAt, -1.0);
        (_markOrigin, _marksDirty) = (null, true);
    }

    /// <summary>
    /// The frame's marks: the windows moved to <paramref name="centre"/>, the wearing of <paramref name="gameSeconds"/> at <paramref name="rain"/>
    /// (0 to 1) and <paramref name="wind"/> (units/s), then the queued stamps. Before the scene (outside a pass).
    /// </summary>
    public void Record(Vector3 centre, float gameSeconds, float rain, float wind)
    {
        _worn += gameSeconds * (1 / StillLife + wind / WindLifeScale + rain / RainLife);
        float share = 1 - MathF.Exp(-_worn);
        bool wear = share >= WearStep;
        if (wear) (_wornTotal, _worn) = (_wornTotal + _worn, 0);

        RenderImprints();
        foreach (var level in new[] { _coarse, _fine })
        {
            level.Begin(centre, wear ? share : 0);
            foreach (var s in _stamps) level.AddStamp(s);
            level.Finish();
        }

        UpdateMarks(wear);
        _stamps.Clear();
    }

    /// <summary>
    /// The images for the patches' colour programs and, displaced with no parallax to march the marks, their mesh
    /// (<see cref="TerrainShaders.PatchEvaluation"/>).
    /// </summary>
    internal void Apply(ref TerrainConstants c)
    {
        if (_coarse.Index == uint.MaxValue || _coarse.Origin is not { } o) return;

        float half = Side * Texel / 2;
        c.Trails = _coarse.Index;
        c.TrailDepth = Depth;
        c.TrailMesh = c.Displace > 0 && c.Parallax <= 0 ? 1 : 0;
        c.TrailWindow = new Vector4(o.X * Texel + half, o.Z * Texel + half, half, 1 / (Side * Texel));
        if (_marksIndex != uint.MaxValue) c.TrailCells = _marksIndex;

        if (_fine.Index != uint.MaxValue && _fine.Origin is { } f)
        {
            float fineHalf = Side * FineTexel / 2;
            c.TrailFine = _fine.Index;
            c.TrailFineWindow = new Vector4(f.X * FineTexel + fineHalf, f.Z * FineTexel + fineHalf, fineHalf, 1 / (Side * FineTexel));
        }
    }

    /// <summary>
    /// Frees both images, the frame and the cells' texture.
    /// </summary>
    public void Dispose()
    {
        _coarse.Dispose();
        _fine.Dispose();
        _frame.Dispose();
        if (_marksIndex != uint.MaxValue) _gpu.Bindless.Free(new BindlessHandle(BindlessKind.Texture2D, _marksIndex));
        _marksTexture?.Dispose();
    }

    /// <summary>
    /// The frame's imprints rendered, each stamp told its tile; those that got none are dropped.
    /// </summary>
    void RenderImprints()
    {
        _imprintRequests.Clear();
        foreach (var s in _stamps)
            if (s.Kind == TrailKind.Imprint) _imprintRequests.Add(new TrailImprint(s.Body, s.Centre, s.Half, s.Floor));

        int done = _imprintRequests.Count > 0 && Imprints is { } source ? source.RenderImprints(_imprintRequests) : 0;
        int next = 0;
        for (int i = _stamps.Count - 1; i >= 0; i--)
            if (_stamps[i].Kind == TrailKind.Imprint && _imprintRequests.Count - 1 - next++ >= done) _stamps.RemoveAt(i);

        AssignImprintTiles();
    }

    /// <summary>
    /// Tells each imprint left in the queue its tile in the atlas, in queue order.
    /// </summary>
    void AssignImprintTiles()
    {
        const int columns = Characters.CharacterRenderer.ImprintColumns;
        int next = 0;
        for (int i = 0; i < _stamps.Count; i++)
        {
            if (_stamps[i].Kind != TrailKind.Imprint) continue;

            int k = next++;
            var tile = new Vector4(k % columns / (float)columns, k / columns / (float)columns, 1f / columns, Imprints!.ImprintAtlas);
            _stamps[i] = _stamps[i] with { Tile = tile };
        }
    }

    #endregion

    #region Marked cells

    // Where the marks are (docs/render-terrain.md "Ground trails"). The colour programs read the images at every step of the ground's march, where
    // nearly all of the ground holds no mark. A grid over the coarse window says which cells a stamp wrote to and has not worn away since, kept on
    // the CPU from the stamps' bounds (no readback), with coarser levels holding the largest of their four cells: one read at the level whose cells
    // are as wide as a pixel's march reaches tells whether any mark is within it. Conservative: a cell stays marked until its marks are under a
    // hundred-thousandth of a unit deep.

    /// <summary>
    /// The grid's cells a side (<see cref="CellSize"/> units each).
    /// </summary>
    public const int CellsSide = 256;

    /// <summary>
    /// The grid's levels (the last is one cell).
    /// </summary>
    public const int CellLevels = 9;

    /// <summary>
    /// A cell's side in world units.
    /// </summary>
    public const float CellSize = Side * Texel / CellsSide;

    /// <summary>
    /// The wearing (as -ln of what is left) after which a full mark is under a hundred-thousandth of a unit deep: its cell counts as bare.
    /// </summary>
    const float MarkLife = 12;

    /// <summary>
    /// How far from a body's stadium its rim is written, in coarse texels (<see cref="StampFragment"/>: the blend's alpha falls to 0 at 6, on a
    /// distance its noise moves by up to 3.5).
    /// </summary>
    const float BodyWritten = 10;

    /// <summary>
    /// Per cell, the total wearing when the cell last took a stamp; negative: none.
    /// </summary>
    readonly double[] _markedAt = Filled(CellsSide * CellsSide);

    /// <summary>
    /// The grid's levels as uploaded: 255 where a cell (or any of the cells under it) is marked.
    /// </summary>
    readonly byte[][] _markLevels = MarkLevels();

    /// <summary>
    /// The coarse window's first cell.
    /// </summary>
    (int X, int Z)? _markOrigin;

    /// <summary>
    /// Whether the grid changed since it was last uploaded.
    /// </summary>
    bool _marksDirty = true;

    /// <summary>
    /// The total wearing so far, as -ln of what is left.
    /// </summary>
    double _wornTotal;

    /// <summary>
    /// The grid's texture, made on its first upload.
    /// </summary>
    GpuTexture? _marksTexture;

    /// <summary>
    /// The grid texture's bindless index; <see cref="uint.MaxValue"/> until it has one.
    /// </summary>
    uint _marksIndex = uint.MaxValue;

    /// <summary>
    /// The grid for this frame's window, stamps and wearing; uploaded when it changed.
    /// </summary>
    void UpdateMarks(bool wore)
    {
        if (_coarse.Origin is not { } o) return;

        const int texels = Side / CellsSide;
        var origin = (X: o.X / texels, Z: o.Z / texels);   // the window's origin is a multiple of Snap texels
        ClearCellsLeftBehind(origin);
        _markOrigin = origin;
        foreach (var s in _stamps) MarkStampCells(s, origin);

        // Worn away: only a wearing step can make a cell bare.
        if (wore) WearCells();

        if (!_marksDirty && _marksTexture is not null) return;

        _marksDirty = false;
        BuildMarkLevels();
        UploadMarks();
    }

    /// <summary>
    /// Makes an array of <paramref name="n"/> cells, all unmarked (-1).
    /// </summary>
    static double[] Filled(int n)
    {
        var a = new double[n];
        Array.Fill(a, -1.0);
        return a;
    }

    /// <summary>
    /// Makes the grid's levels, each a quarter of the one before.
    /// </summary>
    static byte[][] MarkLevels()
    {
        var levels = new byte[CellLevels][];
        for (int l = 0; l < CellLevels; l++) levels[l] = new byte[(CellsSide >> l) * (CellsSide >> l)];
        return levels;
    }

    /// <summary>
    /// A cell coordinate wrapped into the grid.
    /// </summary>
    static int WrapCell(int v) => ((v % CellsSide) + CellsSide) % CellsSide;

    /// <summary>
    /// The window moved: the cells it gains held what it left behind, as the images' wiped strips.
    /// </summary>
    void ClearCellsLeftBehind((int X, int Z) origin)
    {
        if (_markOrigin is not { } was || was == origin) return;

        for (int z = 0; z < CellsSide; z++)
            for (int x = 0; x < CellsSide; x++)
            {
                int cx = origin.X + x, cz = origin.Z + z;
                if (cx >= was.X && cx < was.X + CellsSide && cz >= was.Z && cz < was.Z + CellsSide) continue;

                int i = WrapCell(cz) * CellsSide + WrapCell(cx);
                if (_markedAt[i] >= 0) (_markedAt[i], _marksDirty) = (-1, true);
            }
    }

    /// <summary>
    /// Marks the cells a stamp writes to as stamped at the current total wearing.
    /// </summary>
    void MarkStampCells(in TrailStamp s, (int X, int Z) origin)
    {
        // What the stamp writes, as the box round Level.AddStamp's quad in world units: a body's rim out to BodyWritten texels of its stadium, the
        // other kinds over their whole quad; a texel for the coarse image's rasterising and three for what a bicubic read two texels off takes of it.
        bool body = s.Kind >= TrailKind.Press, imprint = s.Kind == TrailKind.Imprint;
        float along = imprint ? s.Half : body ? s.Length * 0.5f + BodyWritten * Texel : s.Length * 0.5f * QuadPerShape;
        float across = imprint ? s.Half : body ? s.Width * 0.5f + BodyWritten * Texel : s.Width * 0.5f;
        Vector2 a = s.Along * along, b = new Vector2(s.Along.Y, -s.Along.X) * across;
        float rx = MathF.Abs(a.X) + MathF.Abs(b.X) + 4 * Texel, rz = MathF.Abs(a.Y) + MathF.Abs(b.Y) + 4 * Texel;
        int x0 = Math.Max((int)MathF.Floor((s.Centre.X - rx) / CellSize), origin.X);
        int x1 = Math.Min((int)MathF.Floor((s.Centre.X + rx) / CellSize), origin.X + CellsSide - 1);
        int z0 = Math.Max((int)MathF.Floor((s.Centre.Y - rz) / CellSize), origin.Z);
        int z1 = Math.Min((int)MathF.Floor((s.Centre.Y + rz) / CellSize), origin.Z + CellsSide - 1);

        for (int cz = z0; cz <= z1; cz++)
            for (int cx = x0; cx <= x1; cx++)
            {
                int i = WrapCell(cz) * CellsSide + WrapCell(cx);
                if (_markedAt[i] < 0) _marksDirty = true;
                _markedAt[i] = _wornTotal;
            }
    }

    /// <summary>
    /// Clears the cells whose marks have worn under <see cref="MarkLife"/>.
    /// </summary>
    void WearCells()
    {
        for (int i = 0; i < _markedAt.Length; i++)
            if (_markedAt[i] >= 0 && _wornTotal - _markedAt[i] > MarkLife) (_markedAt[i], _marksDirty) = (-1, true);
    }

    /// <summary>
    /// The finest level from the cells, each coarser one the largest of its four cells.
    /// </summary>
    void BuildMarkLevels()
    {
        var finest = _markLevels[0];
        for (int i = 0; i < finest.Length; i++) finest[i] = _markedAt[i] >= 0 ? (byte)255 : (byte)0;

        for (int l = 1; l < CellLevels; l++)
        {
            int side = CellsSide >> l, above = side * 2;
            byte[] from = _markLevels[l - 1], to = _markLevels[l];
            for (int z = 0; z < side; z++)
                for (int x = 0; x < side; x++)
                {
                    int i = z * 2 * above + x * 2;
                    to[z * side + x] = (byte)(from[i] | from[i + 1] | from[i + above] | from[i + above + 1]);
                }
        }
    }

    /// <summary>
    /// Uploads every level of the grid, making its texture the first time.
    /// </summary>
    void UploadMarks()
    {
        using var batch = _gpu.Uploads.Begin();
        if (_marksTexture is null)
        {
            _marksTexture = batch.Create(new TextureDesc(InternalFormat.R8, CellsSide, CellsSide, Levels: CellLevels, Name: "terrain trails cells"));
            var sampler = _gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.LinearMipmapNearest, TextureMagFilter.Linear, TextureWrapMode.Repeat,
                TextureWrapMode.Repeat, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
            _marksIndex = _gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(sampler, _marksTexture.View()));
        }

        for (int l = 0; l < CellLevels; l++)
            batch.Write(_marksTexture, l, 0, new PixelRect(0, 0, (uint)(CellsSide >> l), (uint)(CellsSide >> l)), _markLevels[l]);
    }

    #endregion

    /// <summary>
    /// One quad's instance row (<see cref="Vertex"/>'s inputs).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Row
    {
        /// <summary>
        /// The row's size in bytes.
        /// </summary>
        public const int Size = 48;

        /// <summary>
        /// The centre and half the along axis, in clip space.
        /// </summary>
        public Vector4 Quad;

        /// <summary>
        /// Half the across axis, strength, kind.
        /// </summary>
        public Vector4 Across;

        /// <summary>
        /// An imprint's tile (<see cref="TrailStamp.Tile"/>).
        /// </summary>
        public Vector4 Tile;
    }

    /// <summary>
    /// One image of the trails: its texel, its window, its wipes and the stamps of the frame.
    /// </summary>
    sealed class Level(TerrainTrailRenderer owner, float texel, InternalFormat format, string name)
    {
        /// <summary>
        /// The stamp program, its sizes scaled to this level's texel.
        /// </summary>
        readonly GpuProgram _program = owner._frame.Program(Vertex, StampSource(texel), name);

        /// <summary>
        /// The frame's body stamps' core passes, drawn after every rim.
        /// </summary>
        readonly List<Row> _cores = [];

        /// <summary>
        /// The stamps' pipeline, made on the first draw.
        /// </summary>
        GpuPipeline? _pipeline;

        /// <summary>
        /// The image, made on the first <see cref="Begin"/>.
        /// </summary>
        GpuTexture? _target;

        /// <summary>
        /// The frame's quads; grows by doubling.
        /// </summary>
        Row[] _rows = new Row[256];

        /// <summary>
        /// How many of <see cref="_rows"/> the frame uses.
        /// </summary>
        int _count;

        /// <summary>
        /// Whether the image was just made and must be cleared on its first draw.
        /// </summary>
        bool _fresh;

        /// <summary>
        /// The image's bindless index; <see cref="uint.MaxValue"/> until it is made.
        /// </summary>
        public uint Index { get; private set; } = uint.MaxValue;

        /// <summary>
        /// The window's first texel in world texels, null until it has one (a jump or a new world).
        /// </summary>
        public (int X, int Z)? Origin { get; private set; }

        /// <summary>
        /// Drops the window: the next <see cref="Begin"/> wipes the whole image.
        /// </summary>
        public void Forget() => Origin = null;

        /// <summary>
        /// The window moved to <paramref name="centre"/> and the wearing <paramref name="wearShare"/> (0: none) queued.
        /// </summary>
        public void Begin(Vector3 centre, float wearShare)
        {
            Ensure();
            _count = 0;
            _cores.Clear();
            Move(centre);
            if (wearShare > 0) WipeRange(0, Side, 0, Side, wearShare);
        }

        /// <summary>
        /// A mark as a quad, and again on the far side of the image where it crosses the wrap.
        /// </summary>
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
                    var row = new Row
                    {
                        Quad = new Vector4(centre, a.X * 2 / Side, a.Y * 2 / Side),
                        Across = new Vector4(b.X * 2 / Side, b.Y * 2 / Side, s.Strength, (float)s.Kind),
                        Tile = s.Tile,
                    };
                    Add(row);
                    if (body) _cores.Add(row with { Across = row.Across with { W = row.Across.W + 1 } });
                }
        }

        /// <summary>
        /// Draws what was queued, the stamps' cores after their rims.
        /// </summary>
        public void Finish()
        {
            foreach (var core in _cores) Add(core);
            if (_count > 0 || _fresh) Draw();
        }

        /// <summary>
        /// Frees the image, its bindless slot and the program.
        /// </summary>
        public void Dispose()
        {
            var gpu = owner._gpu;
            if (Index != uint.MaxValue) gpu.Bindless.Free(BindlessKind.Texture2D, Index);
            _target?.Dispose();
            gpu.Pipelines.Forget(_program);
            gpu.Shaders.Dispose(_program);
        }

        /// <summary>
        /// <see cref="StampFragment"/> with <c>K</c> for a level of <paramref name="texel"/> and the bindless table declared.
        /// </summary>
        static string StampSource(float texel)
        {
            string scale = (Texel / texel).ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);
            return StampFragment
                .Replace("TEXEL_SCALE", scale)
                .Replace("BINDLESS_DECLARATIONS", BindlessTable.Declarations(1));
        }

        /// <summary>
        /// Makes the image and registers it, the first time.
        /// </summary>
        void Ensure()
        {
            if (_target is not null) return;

            var gpu = owner._gpu;
            _target = gpu.CreateTexture(new TextureDesc(format, Side, Side, Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: name));
            var sampler = gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.Repeat,
                TextureWrapMode.Repeat, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
            Index = gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(sampler, _target.View()));
            _fresh = true;
        }

        /// <summary>
        /// The window to <paramref name="centre"/>: the columns and rows it gains are wiped (their texels held what it left behind); all of it after
        /// a jump.
        /// </summary>
        void Move(Vector3 centre)
        {
            int x = Snapped(centre.X), z = Snapped(centre.Z);
            if (Origin is not { } was || Math.Abs(x - was.X) >= Side || Math.Abs(z - was.Z) >= Side)
            {
                WipeRange(0, Side, 0, Side, 1);
            }
            else
            {
                if (x > was.X) WipeWrapped(was.X + Side, x + Side, 0, Side);
                if (x < was.X) WipeWrapped(x, was.X, 0, Side);
                if (z > was.Z) WipeWrapped(0, Side, was.Z + Side, z + Side);
                if (z < was.Z) WipeWrapped(0, Side, z, was.Z);
            }

            Origin = (x, z);
        }

        /// <summary>
        /// The window's first texel on one axis for a centre at <paramref name="world"/>, snapped to <see cref="Snap"/> texels.
        /// </summary>
        int Snapped(float world) => (int)MathF.Floor((world / texel - Side / 2f) / Snap) * Snap;

        /// <summary>
        /// The world texel range [x0, x1) × [z0, z1), under a side each, wiped in the wrapped image.
        /// </summary>
        void WipeWrapped(int x0, int x1, int z0, int z1)
        {
            foreach (var (ax, bx) in Unwrapped(x0, x1))
                foreach (var (az, bz) in Unwrapped(z0, z1))
                    WipeRange(ax, bx, az, bz, 1);
        }

        /// <summary>
        /// A range of world texels as one or two ranges of the image.
        /// </summary>
        static (int, int)[] Unwrapped(int from, int to)
        {
            int start = ((from % Side) + Side) % Side, length = to - from;
            return start + length <= Side ? [(start, start + length)] : [(start, Side), (0, start + length - Side)];
        }

        /// <summary>
        /// Queues a wipe of the image texels [x0, x1) × [z0, z1) towards 0 by <paramref name="share"/>.
        /// </summary>
        void WipeRange(int x0, int x1, int z0, int z1, float share)
        {
            var centre = new Vector2(x0 + x1, z0 + z1) / Side - Vector2.One;
            var quad = new Vector4(centre, 0, (z1 - z0) / (float)Side);
            Add(new Row { Quad = quad, Across = new Vector4((x1 - x0) / (float)Side, 0, share, WipeKind) });
        }

        /// <summary>
        /// Appends a quad, doubling the rows when full.
        /// </summary>
        void Add(in Row row)
        {
            if (_count == _rows.Length) Array.Resize(ref _rows, _count * 2);
            _rows[_count++] = row;
        }

        /// <summary>
        /// The frame's quads, in order, in one pass: the wipes, then the stamps over them.
        /// </summary>
        void Draw()
        {
            var gpu = owner._gpu;
            var cmd = gpu.BeginNative(name);
            var t = PassTargets.Of(_target, null);
            var load = _fresh ? LoadOp.Clear : LoadOp.Load;
            t = t with { Colour = new RenderTarget(_target!.Attachment(), load, _fresh ? new ClearColour(0f, 0f, 0f, 0f) : default) };
            _fresh = false;

            cmd.BeginRendering(t.Rendering);
            if (_count > 0)
            {
                var slice = gpu.Frame.AllocateConstants((ulong)(_count * Row.Size), 16);
                MemoryMarshal.AsBytes(_rows.AsSpan(0, _count)).CopyTo(new Span<byte>(slice.Pointer, _count * Row.Size));
                var state = DrawState.For(t.Formats, gpu.DepthClamp, blend: Blend);
                state.Record(cmd, t);
                cmd.BindPipeline(_pipeline ??= gpu.Pipelines.Get(state.Pipeline(_program, Rows, PrimitiveType.TriangleStrip, t.Formats, name)));
                owner._frame.Bind(cmd, _program, default);
                Span<GpuBufferBinding> buffers =
                [
                    new(slice.Handle, slice.Offset),
                    new(slice.Handle, slice.Offset + 16),
                    new(slice.Handle, slice.Offset + 32),
                ];
                cmd.BindVertexBuffers(0, buffers);
                cmd.Draw(4, (uint)_count);
            }

            cmd.EndRendering();
            cmd.Order();
            gpu.EndNative(cmd);
        }
    }
}
