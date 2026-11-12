using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Post;

namespace Meitou.Game.Rendering.Terrain;

/// <summary>
/// The <c>bloodsurfaces</c> switch (docs/game/blood-decals.md "Blood on surfaces"): the blood marks also land on walls, buildings, floors and other
/// static objects. Each mark is kept with its 3D place (<see cref="AddSurface"/>, <see cref="BleedSurface"/>) and drawn after the opaque scene,
/// before the characters, as a screen-space decal: a sphere round the mark, every scene pixel in it rebuilt from the depth with a normal from the
/// depth's slope, and the decal's texture projected along that normal. The result multiplies the scene colour (so light, shadow and haze stay the
/// surface's own). The terrain keeps the blood the cells give it: <see cref="CaptureTerrainDepth"/> keeps the depth the terrain alone wrote, and a
/// pixel whose depth is still that one is terrain.
/// </summary>
public sealed unsafe partial class TerrainDecalRenderer
{
    /// <summary>
    /// The most decals kept: only a guard against a runaway source (the oldest goes first when it is reached). A decal's own life bounds the number
    /// in play, and every kept decal within range and in view is drawn (docs/game/blood-decals.md "Blood on surfaces"): a lower cap made live decals
    /// vanish in a big fight. <see cref="DrawChunk"/> is the rows of one draw.
    /// </summary>
    const int MaxSurfaces = 32768, DrawChunk = 4096;

    /// <summary>
    /// The camera's eye as the frame last gave it (<see cref="SurfacesNear"/>): the decals within <see cref="Range"/> of it are the drawn ones, which
    /// do not run out (<see cref="AgeSurfaces"/>).
    /// </summary>
    Vector3 _surfaceEye;
    bool _surfaceEyeKnown;

    /// <summary>
    /// The decal's circle round its square is within <see cref="Range"/> of the eye (the terrain's cells within the range are the ones on a texture).
    /// </summary>
    bool InRange(SurfaceDecal d)
    {
        if (!_surfaceEyeKnown) return true;   // no frame yet (a world just loaded): nothing runs out before the camera is known
        var (hw, hh) = Half(d);
        return Vector3.Distance(d.Centre, _surfaceEye) - MathF.Sqrt(hw * hw + hh * hh) <= MathF.Max(Range, 0);
    }

    /// <summary>

    /// <summary>
    /// How high above the feet a splash is centred (a blow lands at the body, not the soles).
    /// </summary>
    const float SplashLift = 0.9f;

    /// <summary>
    /// Marks above the terrain by more than this stand on a floor of a building: a splash is centred on that floor, not on the ground.
    /// </summary>
    const float FloorHeight = 3;

    /// <summary>
    /// The surface decal vertex stage: one instanced quad per decal, the screen rectangle bounding its sphere (the whole screen when the eye is near
    /// it or inside).
    /// </summary>
    const string SurfaceVertex = """
        #version 450
        layout(location = 0) in vec4 aCentre;   // eye-relative centre, radius
        layout(location = 1) in vec4 aShape;    // half width, half height, angle, 0: floors only / 1: any surface
        layout(location = 2) in vec4 aColour;
        layout(location = 3) in uint aSprite;
        layout(location = 0) flat out vec4 vCentre;
        layout(location = 1) flat out vec4 vShape;
        layout(location = 2) flat out vec4 vColour;
        layout(location = 3) flat out uint vSprite;
        layout(push_constant) uniform Push
        {
            vec3 right; float tanX;
            vec3 up; float tanY;
            vec3 back; uint depth;
            vec2 planes; uint terrainDepth; uint staticDepth;
            vec2 jitter;
        } pc;
        void main()
        {
            vec2 q = vec2(gl_VertexIndex & 1, gl_VertexIndex >> 1);
            vCentre = aCentre; vShape = aShape; vColour = aColour; vSprite = aSprite;
            vec3 c = aCentre.xyz;
            float R = aCentre.w;
            vec3 v = vec3(dot(c, pc.right), dot(c, pc.up), dot(c, pc.back));   // view space, looking down -z
            float z = -v.z;
            vec2 lo = vec2(-1.0), hi = vec2(1.0);
            if (z - R > 0.2)
            {
                vec2 tn = vec2(pc.tanX, pc.tanY);
                vec2 a = (v.xy - R) / (z - R), b = (v.xy - R) / (z + R), d = (v.xy + R) / (z - R), e = (v.xy + R) / (z + R);
                lo = max(min(min(a, b), min(d, e)) / tn, vec2(-1.0));
                hi = min(max(max(a, b), max(d, e)) / tn, vec2(1.0));
            }
            gl_Position = vec4(mix(lo, hi, q) + pc.jitter, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// The surface decal fragment stage: rebuilds the surface point and its normal from the depth, skips terrain and moving pixels, and projects the
    /// decal's texture along the normal.
    /// </summary>
    static readonly string SurfaceFragment = "#version 450\n" + BindlessTable.Declarations(NativeShaders.BindlessSet) + """
        layout(location = 0) flat in vec4 vCentre;
        layout(location = 1) flat in vec4 vShape;
        layout(location = 2) flat in vec4 vColour;
        layout(location = 3) flat in uint vSprite;
        layout(location = 0) out vec4 fragColour;
        layout(push_constant) uniform Push
        {
            vec3 right; float tanX;
            vec3 up; float tanY;
            vec3 back; uint depth;
            vec2 planes; uint terrainDepth; uint staticDepth;
            vec2 jitter;
        } pc;
        """ + DepthConvention.Glsl + """

        // The surface point under a depth texel, relative to the eye, and its linear depth. The depth was drawn with the projection moved by the jitter: the texel's ray
        // is the unjittered one at its centre less that move.
        vec3 posAt(ivec2 t, vec2 size, out float z)
        {
            t = clamp(t, ivec2(0), ivec2(size) - 1);
            z = depthViewZ(texelFetch(textures2D[pc.depth], t, 0).r, pc.planes);
            vec2 uvn = (vec2(t) + 0.5) / size * 2.0 - 1.0 - pc.jitter;
            vec2 xy = uvn * vec2(pc.tanX, pc.tanY) * z;
            return xy.x * pc.right + xy.y * pc.up - z * pc.back;
        }

        void main()
        {
            vec2 size = vec2(textureSize(textures2D[pc.depth], 0));
            ivec2 t = ivec2(gl_FragCoord.xy);
            float d0 = texelFetch(textures2D[pc.depth], t, 0).r;
            if (depthIsSky(d0)) discard;
            float z0;
            vec3 p = posAt(t, size, z0);
            vec3 rel = p - vCentre.xyz;
            float dist = length(rel);
            float radius = vCentre.w;
            if (dist > radius) discard;
            // The terrain carries its own blood (the cells' decals): a pixel the static objects did not draw over (its depth the same bits after them as after the
            // terrain alone) is left out. An exact test: a floor or rock a few centimetres above the ground is an object's pixel, which a depth tolerance took for terrain.
            if (texelFetch(textures2D[pc.terrainDepth], t, 0).r == texelFetch(textures2D[pc.staticDepth], t, 0).r) discard;
            // Foliage, debris and anything else drawn after the static objects is nearer than the static depth: no blood on what sways.
            if (abs(depthViewZ(texelFetch(textures2D[pc.staticDepth], t, 0).r, pc.planes) - z0) > 0.02 + 0.0005 * z0) discard;
            float zl, zr, zu, zb;
            vec3 l = posAt(t - ivec2(1, 0), size, zl), r = posAt(t + ivec2(1, 0), size, zr);
            vec3 u = posAt(t + ivec2(0, 1), size, zu), b = posAt(t - ivec2(0, 1), size, zb);
            vec3 dx = abs(zr - z0) < abs(z0 - zl) ? r - p : p - l;
            vec3 dy = abs(zu - z0) < abs(z0 - zb) ? u - p : p - b;
            vec3 n = normalize(cross(dx, dy));
            if (dot(n, p) > 0.0) n = -n;   // towards the eye

            float fade;
            if (vShape.w < 0.5)
            {
                // A pool, drip or smear: only on a floor at the mark's height.
                float height = abs(rel.y);
                if (n.y < 0.8 || height > 0.4) discard;
                fade = 1.0 - smoothstep(0.2, 0.4, height);
            }
            else
            {
                // A splash: any surface that faces the mark, fainter towards the edge of its reach.
                if (dot(n, -rel) < 0.05 * dist) discard;
                fade = 1.0 - smoothstep(0.55 * radius, radius, dist);
            }
            vec2 plane;
            if (abs(n.y) > 0.8) plane = rel.xz;
            else
            {
                vec3 tx = normalize(cross(vec3(0.0, 1.0, 0.0), n));
                plane = vec2(dot(rel, tx), dot(rel, cross(n, tx)));
            }
            float s = sin(vShape.z), c = cos(vShape.z);
            vec2 uv = vec2(dot(plane, vec2(s, c)) / vShape.x, dot(plane, vec2(c, -s)) / vShape.y) * 0.5 + 0.5;
            if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) discard;
            float pixel = z0 * 2.0 * pc.tanY / size.y;
            float lod = max(log2(max(pixel * float(textureSize(textures2D[nonuniformEXT(vSprite)], 0).x) / (2.0 * vShape.x), 1e-4)), 0.0);
            vec4 texel = textureLod(textures2D[nonuniformEXT(vSprite)], uv, lod);
            float a = clamp(texel.a * vColour.a * fade, 0.0, 1.0);
            // Multiplied into the lit surface: the blood's colour against a mid-grey albedo.
            fragColour = vec4(mix(vec3(1.0), texel.rgb * vColour.rgb * 1.6 + 0.03, a), 1.0);
        }
        """;

    /// <summary>
    /// One surface decal's instance row (<see cref="SurfaceVertex"/>'s inputs).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct SurfaceInstance
    {
        /// <summary>
        /// The row's bytes.
        /// </summary>
        public const int Size = 64;

        /// <summary>
        /// Centre: eye-relative, w the bounding sphere's radius; Shape: half width, half height, angle, w 0 floors only, 1 any surface; Colour: the
        /// decal's colour, its alpha at most the life.
        /// </summary>
        public Vector4 Centre, Shape, Colour;

        /// <summary>
        /// The texture's bindless index, and padding to 64 bytes.
        /// </summary>
        public uint Sprite, P0, P1, P2;
    }

    /// <summary>
    /// The surface programs' push block (both stages).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct SurfacePush
    {
        /// <summary>
        /// The view's right axis.
        /// </summary>
        public Vector3 Right;

        /// <summary>
        /// The tangent of half the horizontal field of view.
        /// </summary>
        public float TanX;

        /// <summary>
        /// The view's up axis.
        /// </summary>
        public Vector3 Up;

        /// <summary>
        /// The tangent of half the vertical field of view.
        /// </summary>
        public float TanY;

        /// <summary>
        /// The view's back axis.
        /// </summary>
        public Vector3 Back;

        /// <summary>
        /// The scene depth's bindless index.
        /// </summary>
        public uint Depth;

        /// <summary>
        /// The scene's near and far planes.
        /// </summary>
        public Vector2 Planes;

        /// <summary>
        /// The bindless indices of the depth the terrain alone left and the depth the static objects left.
        /// </summary>
        public uint TerrainDepth, StaticDepth;

        /// <summary>
        /// The projection's jitter (NDC).
        /// </summary>
        public Vector2 Jitter;
    }

    /// <summary>
    /// The instance rows' layout (one binding, per instance).
    /// </summary>
    static readonly VertexLayout SurfaceRows = new(
        new VertexInput(0, InternalFormat.Rgba32F, SurfaceInstance.Size, true),
        new VertexInput(1, InternalFormat.Rgba32F, SurfaceInstance.Size, true),
        new VertexInput(2, InternalFormat.Rgba32F, SurfaceInstance.Size, true),
        new VertexInput(3, InternalFormat.R32Ui, SurfaceInstance.Size, true));

    /// <summary>
    /// The scene colour times the decal's colour (red, green and blue; the alpha is the characters' mask).
    /// </summary>
    static readonly BlendState Multiply = new(true, BlendingFactor.DstColor, BlendingFactor.Zero);

    /// <summary>
    /// One decal on the surfaces: where it is in 3D, how it is turned and how big.
    /// </summary>
    sealed class SurfaceDecal
    {
        /// <summary>
        /// The game's decal type.
        /// </summary>
        public TerrainDecalType Type;

        /// <summary>
        /// The texture variant of the type.
        /// </summary>
        public int Variant;

        /// <summary>
        /// The decal's centre (world).
        /// </summary>
        public Vector3 Centre;

        /// <summary>
        /// The turn about its normal (radians), the game's size, and the surface clock it is gone at.
        /// </summary>
        public float Angle, Size, Expires;

        /// <summary>
        /// Its colour (ABGR).
        /// </summary>
        public uint Colour;

        /// <summary>
        /// It lands on floors only (all but a splash).
        /// </summary>
        public bool Floor;

        /// <summary>
        /// Where the mark was made (X, Z): a pool grows while its bleeder stays within √10 units of it.
        /// </summary>
        public Vector2 At;

        /// <summary>
        /// The bleeder whose growing pool it is (<see cref="BleedSurface"/>), else null, and the surface clock it last bled into it at.
        /// </summary>
        public long? Who;

        /// <summary>
        /// See <see cref="Who"/>.
        /// </summary>
        public float Bled;
    }

    /// <summary>
    /// The decals kept, oldest first (at most <see cref="MaxSurfaces"/>).
    /// </summary>
    readonly List<SurfaceDecal> _surfaces = [];

    /// <summary>
    /// Each bleeder's growing pool by its key.
    /// </summary>
    readonly Dictionary<long, SurfaceDecal> _surfacePools = [];

    /// <summary>
    /// A pool bled into this recently (seconds of game time) is not given up for a new decal (engine choice: a bleeder marks every tick).
    /// </summary>
    const float StillBleeding = 1;

    /// <summary>
    /// The surface clock at which every decal was found a pool still bleeding (no room for a new pool until the clock moves), or −1.
    /// </summary>
    float _fullOfBleeding = -1;

    /// <summary>
    /// This frame's near decals with their squared distance to the eye, nearest first (at most <see cref="MaxSurfaceDraws"/>).
    /// </summary>
    readonly List<(float Distance, SurfaceDecal Decal)> _surfaceNear = [];

    /// <summary>
    /// Game seconds the surfaces have aged (<see cref="AgeSurfaces"/>); a decal's expiry is on this clock.
    /// </summary>
    float _surfaceClock;

    /// <summary>
    /// The surface decal program (made on the first draw).
    /// </summary>
    GpuProgram? _surfaceProgram;

    /// <summary>
    /// Its pipeline.
    /// </summary>
    GpuPipeline? _surfacePipeline;

    /// <summary>
    /// The scene depth the last draw read, and the copy of the depth the terrain alone left.
    /// </summary>
    GpuTexture? _surfaceDepth, _terrainDepth;

    /// <summary>
    /// Their bindless indices (<see cref="uint.MaxValue"/>: none).
    /// </summary>
    uint _surfaceDepthIndex = uint.MaxValue, _terrainDepthIndex = uint.MaxValue;

    /// <summary>
    /// The instance rows of the draw (grown as needed).
    /// </summary>
    SurfaceInstance[] _surfaceInstances = new SurfaceInstance[64];

    /// <summary>
    /// The frame the static depth was copied in (-1: never).
    /// </summary>
    long _staticDepthFrame = -1;

    /// <summary>
    /// The copy of the depth the static objects left.
    /// </summary>
    GpuTexture? _staticDepth;

    /// <summary>
    /// Its bindless index (<see cref="uint.MaxValue"/>: none).
    /// </summary>
    uint _staticDepthIndex = uint.MaxValue;

    /// <summary>
    /// The frame the terrain depth was copied in (-1: never).
    /// </summary>
    long _terrainDepthFrame = -1;

    /// <summary>
    /// The <see cref="Surfaces"/> switch.
    /// </summary>
    bool _surfacesOn;

    /// <summary>
    /// The <c>bloodsurfaces</c> switch: marks are kept and drawn on the other surfaces as well as the terrain; off (Faithful), none are, and the ones
    /// kept are dropped.
    /// </summary>
    public bool Surfaces
    {
        get => _surfacesOn;
        set
        {
            if (value == _surfacesOn) return;
            _surfacesOn = value;
            if (!value) ClearSurfaces();
        }
    }

    /// <summary>
    /// The terrain's height at (X, Z) (the host gives <c>TerrainRenderer.HeightAt</c>): a splash on the ground is centred above it.
    /// </summary>
    public Func<float, float, float>? GroundHeight { get; set; }

    /// <summary>
    /// The decals kept for the surfaces now.
    /// </summary>
    public int SurfaceCount => _surfaces.Count;

    /// <summary>
    /// Whether any decal kept for the surfaces is within <see cref="TerrainDecalRenderer.Range"/> of <paramref name="eye"/>: the frame copies the
    /// depth and splits its passes for them only then (<see cref="RecordSurfaces"/> culls the rest per decal).
    /// </summary>
    public bool SurfacesNear(Vector3 eye)
    {
        if (!_surfacesOn) return false;
        (_surfaceEye, _surfaceEyeKnown) = (eye, true);
        float range = MathF.Max(Range, 0);
        foreach (var d in _surfaces)
        {
            var (hw, hh) = Half(d);
            float radius = MathF.Sqrt(hw * hw + hh * hh);
            if (radius > 0 && Vector3.Distance(d.Centre, eye) - radius <= range) return true;
        }
        return false;
    }

    /// <summary>
    /// Drops every decal kept for the surfaces (a new world).
    /// </summary>
    public void ClearSurfaces()
    {
        _surfaces.Clear();
        _surfacePools.Clear();
        _surfaceEyeKnown = false;
    }

    /// <summary>
    /// The decals kept for the surfaces as a save holds them (<see cref="SaveSurfaceDecals"/>), oldest first, each with its life left.
    /// </summary>
    public IEnumerable<SurfaceBloodDecal> SavedSurfaces =>
        _surfaces.Select(d => new SurfaceBloodDecal(d.Type, (byte)d.Variant, d.Centre, d.Angle, d.Size, MathF.Max(d.Expires - _surfaceClock, 0),
            d.Colour, d.Floor));   // a life that ran out in view (see AgeSurfaces) is saved as 0: it stays while the camera is near

    /// <summary>
    /// Puts back the decals a save held (after <see cref="ClearSurfaces"/>, a new world): each with its life left, the newest kept when over <see
    /// cref="MaxSurfaces"/>. None while the switch is off; one whose texture is not loaded is skipped. A pool no longer grows (the bleeding character
    /// is not kept).
    /// </summary>
    public void LoadSurfaces(IReadOnlyList<SurfaceBloodDecal> saved)
    {
        if (!_surfacesOn) return;
        for (int i = Math.Max(0, saved.Count - MaxSurfaces); i < saved.Count; i++)
        {
            var s = saved[i];
            if ((int)s.Type >= _sprites.Length || s.Variant >= _sprites[(int)s.Type].Length || s.Life < 0 || s.Size <= 0)
                continue;
            Keep(new SurfaceDecal
            {
                Type = s.Type,
                Variant = s.Variant,
                Centre = s.Centre,
                Angle = s.Angle,
                Size = s.Size,
                Colour = s.Colour,
                Floor = s.Floor,
                Expires = _surfaceClock + s.Life,
                At = new Vector2(s.Centre.X, s.Centre.Z),
            });
        }
    }

    /// <summary>
    /// Ages the decals by <paramref name="dt"/> seconds of game time. As the terrain's (<c>TerrainDecalStore.Age</c>, the game's rule: a decal at life 0 is removed
    /// unless its cell is on a texture now), a decal whose life ran out goes only while it is out of the camera's range: within it, it stays as drawn, so
    /// blood in view never vanishes by itself (it used to fade out in its last second while the terrain's blood beside it stayed).
    /// </summary>
    public void AgeSurfaces(float dt)
    {
        if (dt <= 0 || _surfaces.Count == 0) return;
        _surfaceClock += dt;
        if (_surfaces.RemoveAll(d => d.Expires <= _surfaceClock && !InRange(d)) > 0)
            foreach (var key in _surfacePools.Where(p => p.Value.Expires <= _surfaceClock && !InRange(p.Value)).Select(p => p.Key).ToList())
                _surfacePools.Remove(key);
    }

    /// <summary>
    /// A drip, smear or splash at <paramref name="at"/> (the mark's place, its Y the character's feet) turned to <paramref name="direction"/> (X/Z),
    /// of the game's
    /// <paramref name="size"/>. A splash lands on any surface round a point at the body's height, the others only on floors at the mark's height.
    /// </summary>
    public void AddSurface(TerrainDecalType type, Vector3 at, Vector2 direction, float size, uint abgr, Random random)
    {
        if (!_surfacesOn || type == TerrainDecalType.Line) return;
        int variants = _sprites[(int)type].Length;
        if (variants == 0) return;
        Keep(Make(type, at, random.Next(variants), MathF.Atan2(direction.X, direction.Y), size, abgr));
    }

    /// <summary>
    /// A bleeding character that stands still, as <see cref="TerrainDecalStore.Bleed"/> (the same growth rule, kept apart so a pool on an upper floor
    /// grows too):
    /// <paramref name="bleeding"/> × <paramref name="dt"/> × 0.2 grows the pool at its feet, within √10 units of where it started, else a new pool.
    /// </summary>
    public void BleedSurface(long who, Vector3 at, float bleeding, float dt, uint abgr, Random random)
    {
        if (!_surfacesOn) return;
        int variants = _sprites[(int)TerrainDecalType.Pool].Length;
        if (variants == 0) return;
        float grow = bleeding * dt * 0.2f;
        if (_surfacePools.TryGetValue(who, out var pool) && Vector2.DistanceSquared(pool.At, new Vector2(at.X, at.Z)) <= 10)
        {
            pool.Size = MathF.Min(pool.Size + grow / MathF.Max(1, pool.Size * 10), TerrainDecalStore.PoolMax);
            pool.Bled = _surfaceClock;
            return;
        }
        // At the cap with every decal a pool still bleeding (more bleeders than decals): this one bleeds on the terrain only, rather than each new
        // pool pushing out another bleeder's, which made one again the next tick (thousands a frame, none growing).
        if (_surfaces.Count >= MaxSurfaces && !MakeRoom()) return;
        pool = Make(TerrainDecalType.Pool, at, random.Next(variants), random.NextSingle() * MathF.Tau, grow, abgr);
        (pool.Who, pool.Bled) = (who, _surfaceClock);
        Keep(pool);
        _surfacePools[who] = pool;
    }

    /// <summary>
    /// <paramref name="d"/> is its bleeder's pool and was bled into within <see cref="StillBleeding"/> seconds.
    /// </summary>
    bool Bleeding(SurfaceDecal d) => d.Who is long who && _surfacePools.TryGetValue(who, out var pool) && pool == d &&
        _surfaceClock - d.Bled <= StillBleeding;

    /// <summary>
    /// Removes the oldest decal that is not a pool still bleeding; false when there is none (remembered until the surface clock moves).
    /// </summary>
    bool MakeRoom()
    {
        if (_fullOfBleeding == _surfaceClock) return false;
        for (int i = 0; i < _surfaces.Count; i++)
        {
            var d = _surfaces[i];
            if (Bleeding(d)) continue;
            if (d.Who is long who && _surfacePools.TryGetValue(who, out var pool) && pool == d)
                _surfacePools.Remove(who);
            _surfaces.RemoveAt(i);
            return true;
        }
        _fullOfBleeding = _surfaceClock;
        return false;
    }

    /// <summary>
    /// A new decal at <paramref name="at"/>, living <see cref="TerrainDecalStore.NewLife"/>; a splash is centred at the body's height above the
    /// ground or the floor under it.
    /// </summary>
    SurfaceDecal Make(TerrainDecalType type, Vector3 at, int variant, float angle, float size, uint abgr)
    {
        float ground = GroundHeight?.Invoke(at.X, at.Z) ?? at.Y;
        bool floor = type != TerrainDecalType.Splash;
        // A splash on the ground is centred above the ground at its place (the mark carries the victim's height, which a slope or a flight of up to
        // 20 units may leave behind).
        float y = floor ? at.Y : (at.Y - ground > FloorHeight ? at.Y : ground) + SplashLift;
        return new SurfaceDecal
        {
            Type = type,
            Variant = variant,
            Centre = new Vector3(at.X, y, at.Z),
            Angle = angle,
            Size = size,
            Colour = abgr,
            Expires = _surfaceClock + TerrainDecalStore.NewLife,
            Floor = floor,
            At = new Vector2(at.X, at.Z),
        };
    }

    /// <summary>
    /// Adds a decal, making room at the cap: the oldest that is not a pool still bleeding, else the oldest.
    /// </summary>
    void Keep(SurfaceDecal decal)
    {
        if (_surfaces.Count >= MaxSurfaces && !MakeRoom())
        {
            var oldest = _surfaces[0];
            // Its bleeder's pool by the key it keeps: a scan of the pools per new decal took most of a frame with thousands bleeding at the cap.
            if (oldest.Who is long who && _surfacePools.TryGetValue(who, out var pool) && pool == oldest)
                _surfacePools.Remove(who);
            _surfaces.RemoveAt(0);
        }
        _surfaces.Add(decal);
    }

    /// <summary>
    /// The half sizes of a decal in world units (the terrain's rule: size × texels × 288 / 4096).
    /// </summary>
    (float Width, float Height) Half(SurfaceDecal d)
    {
        var image = _sprites[(int)d.Type][d.Variant];
        if (image is null) return (0, 0);
        float k = d.Size * TerrainDecalCells.Size / 4096;
        return (image.Texture.Desc.Width * k, image.Texture.Desc.Height * k);
    }

    /// <summary>
    /// Keeps the depth buffer as the terrain alone left it: between the terrain's draw and the objects' (the host is closed), so <see
    /// cref="RecordSurfaces"/> can tell terrain pixels, which the cells already carry blood on, from the rest.
    /// </summary>
    internal void CaptureTerrainDepth(GpuTexture sceneDepth) => Capture(sceneDepth, ref _terrainDepth, ref _terrainDepthIndex,
        "blood terrain depth", true);

    /// <summary>
    /// Keeps the depth buffer as the static objects left it, before the foliage and everything else that sways or moves is drawn: a pixel nearer than
    /// this is not static, and takes no blood.
    /// </summary>
    internal void CaptureStaticDepth(GpuTexture sceneDepth) => Capture(sceneDepth, ref _staticDepth, ref _staticDepthIndex, "blood static depth",
        false);

    /// <summary>
    /// Copies <paramref name="sceneDepth"/> into <paramref name="target"/> (made again, with its bindless entry, when its size or format changed) and
    /// notes the frame; nothing while no surface decal is kept.
    /// </summary>
    void Capture(GpuTexture sceneDepth, ref GpuTexture? target, ref uint index, string name, bool first)
    {
        if (!_surfacesOn || _surfaces.Count == 0) return;
        if (target is null || target.Desc.Width != sceneDepth.Desc.Width || target.Desc.Height != sceneDepth.Desc.Height
            || target.Desc.Format != sceneDepth.Desc.Format)
        {
            if (index != uint.MaxValue) _gpu.Bindless.Free(BindlessKind.Texture2D, index);
            target?.Dispose();
            target = _gpu.CreateTexture(new TextureDesc(sceneDepth.Desc.Format, sceneDepth.Desc.Width, sceneDepth.Desc.Height,
                Use: TextureUse.Sampled | TextureUse.TransferDst | TextureUse.DepthTarget, Name: name));
            index = _gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(NearestSampler(), target.View()));
        }
        var cmd = _gpu.BeginNative(name);
        cmd.Order();
        cmd.Blit(sceneDepth, target, TextureMinFilter.Nearest);
        cmd.Order();
        _gpu.EndNative(cmd);
        if (first) _terrainDepthFrame = _gpu.Frame.Number;
        else _staticDepthFrame = _gpu.Frame.Number;
    }

    /// <summary>
    /// The nearest, edge-clamped sampler the depths are read with.
    /// </summary>
    GpuSampler NearestSampler() => _gpu.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Nearest, TextureMagFilter.Nearest));

    /// <summary>
    /// Draws the decals near <paramref name="eye"/> over <paramref name="colour"/> from <paramref name="depth"/> (the scene's, planes <paramref
    /// name="near"/> and
    /// <paramref name="far"/>). <paramref name="rotation"/> is the view without its translation. Between the scene's passes: the host is closed, and
    /// <see cref="CaptureTerrainDepth"/> ran this frame.
    /// </summary>
    internal void RecordSurfaces(GpuTexture colour, GpuTexture depth, float near, float far, Matrix4x4 rotation, float tanX, float tanY,
        Vector3 eye, Vector2 jitterNdc)
    {
        if (!_surfacesOn || _surfaces.Count == 0 || _terrainDepth is null
            || _terrainDepthFrame != _gpu.Frame.Number || _staticDepthFrame != _gpu.Frame.Number)
            return;
        RegisterSprites();
        var back = ViewWindow.Back(rotation, tanX, tanY);
        (tanX, tanY) = (ViewWindow.Tan(tanX, tanY).X, ViewWindow.Tan(tanX, tanY).Y);
        CollectSurfaceNear(eye, back, rotation, tanX, tanY);
        if (_surfaceNear.Count == 0) return;
        int n = FillSurfaceInstances(eye);
        if (n == 0) return;

        var program = _surfaceProgram ??= _frame.Program(SurfaceVertex, SurfaceFragment, "blood surfaces");
        BindSurfaceDepth(depth);
        DrawSurfaces(colour, program, n, near, far, rotation, tanX, tanY, back, jitterNdc);
    }

    /// <summary>The decals within the range of the eye and in the view (<paramref name="back"/> is the view's back; <paramref name="tanX"/>, <paramref name="tanY"/> the
    /// half-angles' tangents), all of them, into <see cref="_surfaceNear"/>.</summary>
    void CollectSurfaceNear(Vector3 eye, Vector3 back, Matrix4x4 rotation, float tanX, float tanY)
    {
        _surfaceNear.Clear();
        float range = MathF.Max(Range, 0);
        var right = new Vector3(rotation.M11, rotation.M21, rotation.M31);
        var up = new Vector3(rotation.M12, rotation.M22, rotation.M32);
        float sideX = MathF.Sqrt(1 + tanX * tanX), sideY = MathF.Sqrt(1 + tanY * tanY);
        foreach (var d in _surfaces)
        {
            var (hw, hh) = Half(d);
            float radius = MathF.Sqrt(hw * hw + hh * hh);
            var rel = d.Centre - eye;
            // Past the range, or behind the eye.
            if (radius <= 0 || rel.Length() - radius > range || Vector3.Dot(rel, back) > radius) continue;
            // Outside the side planes of the view (a margin of the jitter and the sphere's radius).
            float z = -Vector3.Dot(rel, back);
            if ((MathF.Abs(Vector3.Dot(rel, right)) - tanX * z) / sideX > radius * 1.01f + 0.5f
                || (MathF.Abs(Vector3.Dot(rel, up)) - tanY * z) / sideY > radius * 1.01f + 0.5f) continue;
            _surfaceNear.Add((rel.LengthSquared(), d));
        }
    }

    /// <summary>
    /// Writes the instance row of each near decal that has a sprite and width into <see cref="_surfaceInstances"/>; returns how many.
    /// </summary>
    int FillSurfaceInstances(Vector3 eye)
    {
        if (_surfaceInstances.Length < _surfaceNear.Count)
            _surfaceInstances = new SurfaceInstance[Math.Max(_surfaceNear.Count, _surfaceInstances.Length * 2)];
        int n = 0;
        foreach (var (_, d) in _surfaceNear)
        {
            uint index = _spriteIndex[(int)d.Type][d.Variant];
            var (hw, hh) = Half(d);
            if (index == uint.MaxValue || hw <= 0) continue;
            uint abgr = d.Colour;
            float alpha = (abgr >> 24) / 255f;   // as a decal drawn on a cell's texture: its alpha at the start, no fade (docs/game/blood-decals.md)
            _surfaceInstances[n++] = new SurfaceInstance
            {
                Centre = new Vector4(d.Centre - eye, MathF.Sqrt(hw * hw + hh * hh)),   // the sphere round the decal's square
                Shape = new Vector4(hw, hh, d.Angle, d.Floor ? 0 : 1),
                Colour = new Vector4((abgr & 0xFF) / 255f, (abgr >> 8 & 0xFF) / 255f, (abgr >> 16 & 0xFF) / 255f, alpha),
                Sprite = index,
            };
        }
        return n;
    }

    /// <summary>
    /// Registers the scene's depth as the bindless texture the surfaces read, again when the depth texture changed.
    /// </summary>
    void BindSurfaceDepth(GpuTexture depth)
    {
        if (_surfaceDepth == depth) return;
        if (_surfaceDepthIndex != uint.MaxValue) _gpu.Bindless.Free(BindlessKind.Texture2D, _surfaceDepthIndex);
        _surfaceDepthIndex = _gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(NearestSampler(), depth.View()));
        _surfaceDepth = depth;
    }

    /// <summary>
    /// Draws the first <paramref name="n"/> instance rows over <paramref name="colour"/> in one native pass.
    /// </summary>
    void DrawSurfaces(GpuTexture colour, GpuProgram program, int n, float near, float far, Matrix4x4 rotation, float tanX, float tanY,
        Vector3 back, Vector2 jitterNdc)
    {
        var cmd = _gpu.BeginNative("blood surfaces");
        cmd.Order();
        var t = PassTargets.Of(colour, null);
        cmd.BeginRendering(t.Rendering);
        var state = DrawState.For(t.Formats, _gpu.DepthClamp, blend: Multiply, mask: DrawState.Rgb);
        state.Record(cmd, t);
        var pipeline = _surfacePipeline ??=
            _gpu.Pipelines.Get(state.Pipeline(program, SurfaceRows, PrimitiveType.TriangleStrip, t.Formats, "blood surfaces"));
        cmd.BindPipeline(pipeline);
        _frame.Bind(cmd, program, default);
        var push = new SurfacePush
        {
            Right = new Vector3(rotation.M11, rotation.M21, rotation.M31),
            TanX = tanX,
            Up = new Vector3(rotation.M12, rotation.M22, rotation.M32),
            TanY = tanY,
            Back = back,
            Depth = _surfaceDepthIndex,
            Planes = new Vector2(near, far),
            TerrainDepth = _terrainDepthIndex,
            StaticDepth = _staticDepthIndex,
            Jitter = jitterNdc,
        };
        cmd.PushConstants(program, GpuStage.Vertex | GpuStage.Fragment, in push);
        for (int first = 0; first < n; first += DrawChunk)
        {
            int count = Math.Min(DrawChunk, n - first);
            var rows = _gpu.Frame.AllocateConstants((ulong)(count * SurfaceInstance.Size), 16);
            MemoryMarshal.AsBytes(_surfaceInstances.AsSpan(first, count)).CopyTo(new Span<byte>(rows.Pointer, count * SurfaceInstance.Size));
            Span<GpuBufferBinding> buffers =
            [
                new(rows.Handle, rows.Offset), new(rows.Handle, rows.Offset + 16), new(rows.Handle, rows.Offset + 32), new(rows.Handle, rows.Offset + 48),
            ];

            cmd.BindVertexBuffers(0, buffers);
            cmd.Draw(4, (uint)count);
        }
        cmd.EndRendering();
        cmd.Order();
        _gpu.EndNative(cmd);
    }

    /// <summary>
    /// Frees the depth copies, their bindless entries and the surface program.
    /// </summary>
    void DisposeSurfaces()
    {
        if (_surfaceDepthIndex != uint.MaxValue) _gpu.Bindless.Free(BindlessKind.Texture2D, _surfaceDepthIndex);
        if (_terrainDepthIndex != uint.MaxValue) _gpu.Bindless.Free(BindlessKind.Texture2D, _terrainDepthIndex);
        _terrainDepth?.Dispose();
        _staticDepth?.Dispose();
        if (_staticDepthIndex != uint.MaxValue) _gpu.Bindless.Free(BindlessKind.Texture2D, _staticDepthIndex);
        if (_surfaceProgram is { } program)
        {
            _gpu.Pipelines.Forget(program);
            _gpu.Shaders.Dispose(program);
        }
    }
}