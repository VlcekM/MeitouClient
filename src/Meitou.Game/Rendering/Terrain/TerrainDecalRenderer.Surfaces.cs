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
    const int MaxSurfaces = 32768;

    /// <summary>
    /// The most instance rows one draw of the surface decals takes.
    /// </summary>
    const int DrawChunk = 4096;

    /// <summary>
    /// The camera's eye as the frame last gave it (<see cref="SurfacesNear"/>): the decals within <see cref="Range"/> of it are the drawn ones, which
    /// do not run out (<see cref="AgeSurfaces"/>).
    /// </summary>
    Vector3 _surfaceEye;

    /// <summary>
    /// Whether <see cref="_surfaceEye"/> holds a frame's eye (false until the first frame of a world).
    /// </summary>
    bool _surfaceEyeKnown;

    /// <summary>
    /// The range and default of <see cref="SurfaceDraws"/>.
    /// </summary>
    public const int MinSurfaceDraws = 64, MaxSurfaceDraws = 2048, DefaultSurfaceDraws = 2048;

    /// <summary>
    /// The most surface decals and footprints drawn in a frame, the ones that look largest (the Tab panel's <c>Blood decals drawn</c> slider).
    /// </summary>
    public static int SurfaceDraws { get; set; } = DefaultSurfaceDraws;

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
        layout(location = 1) in vec4 aShape;    // half width, half height, angle, 0: floors / 1: any object surface / 2: terrain
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
            vec2 jitter; float rowSign;
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
    /// The surface decal fragment stage: rebuilds the surface point from the depth, separates terrain from static objects, and projects the
    /// decal mask onto the matching surface.
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
            vec2 jitter; float rowSign;
        } pc;
        """ + DepthConvention.Glsl + """

        // The surface point under a depth texel, relative to the eye, and its linear depth. The depth was drawn with the projection moved by the jitter: the texel's ray
        // is the unjittered one at its centre less that move. A top-down target (Metal) has NDC +1 at row 0: rowSign -1 turns the row into
        // the projection's NDC.
        vec3 posAt(ivec2 t, vec2 size, out float z)
        {
            t = clamp(t, ivec2(0), ivec2(size) - 1);
            z = depthViewZ(texelFetch(textures2D[pc.depth], t, 0).r, pc.planes);
            vec2 uvn = ((vec2(t) + 0.5) / size * 2.0 - 1.0) * vec2(1.0, pc.rowSign) - pc.jitter;
            vec2 xy = uvn * vec2(pc.tanX, pc.tanY) * z;
            return xy.x * pc.right + xy.y * pc.up - z * pc.back;
        }

        float inkHash(vec2 p)
        {
            vec3 q = fract(p.xyx * 0.1031);
            q += dot(q, q.yzx + 33.33);
            return fract((q.x + q.y) * q.z);
        }

        float inkNoise(vec2 p)
        {
            vec2 tile = floor(p);
            vec2 f = fract(p);
            f = f * f * (3.0 - 2.0 * f);
            return mix(mix(inkHash(tile), inkHash(tile + vec2(1, 0)), f.x),
                       mix(inkHash(tile + vec2(0, 1)), inkHash(tile + vec2(1, 1)), f.x), f.y);
        }

        float transfer(vec2 uv, float seed, float wet)
        {
            vec2 offset = vec2(seed * 59.0, seed * 31.0);
            float broad = inkNoise(uv * vec2(5.0, 9.0) + offset);
            float grain = inkNoise(uv * vec2(14.0, 24.0) - offset);
            float pressure = (seed - 0.40) * (uv.y - 0.5);
            float ink = 0.70 * broad + 0.30 * grain + pressure;
            return mix(0.14, 1.0, smoothstep(0.16 + (1.0 - wet) * 0.39, 0.63, ink));
        }

        // The mark's texture coordinates of a place on its plane (relative to its centre), and whether they are off its square.
        vec2 uvOf(vec2 plane)
        {
            float s = sin(vShape.z), c = cos(vShape.z);
            return vec2(dot(plane, vec2(s, c)) / vShape.x, dot(plane, vec2(c, -s)) / vShape.y) * 0.5 + 0.5;
        }

        bool offSquare(vec2 uv) { return any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))); }

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
            bool footprint = vShape.w > 1.5;
            bool laid = vShape.w < 0.5;   // a pool, drip or smear: on a floor at the mark's height
            vec2 plane = rel.xz;
            float fade = 1.0;
            float height = abs(rel.y);
            // What is laid from above ends here for most of its sphere's pixels, before any other texel is read: off its height, or off its square.
            if (laid && height > 0.4) discard;
            if ((laid || footprint) && offSquare(uvOf(plane))) discard;
            float terrainDepth = texelFetch(textures2D[pc.terrainDepth], t, 0).r;
            if (footprint)
            {
                // Exactly the terrain's pixel. Floors, rocks, foliage and debris cannot take a ground print.
                if (d0 != terrainDepth) discard;
            }
            else
            {
                float staticDepth = texelFetch(textures2D[pc.staticDepth], t, 0).r;
                // The terrain carries its own blood (the cells' decals): a pixel the static objects did not draw over (its depth the same bits after them as after the
                // terrain alone) is left out. An exact test: a floor or rock a few centimetres above the ground is an object's pixel, which a depth tolerance took for terrain.
                if (terrainDepth == staticDepth) discard;
                // Foliage, debris and anything else drawn after the static objects is nearer than the static depth: no blood on what sways.
                if (abs(depthViewZ(staticDepth, pc.planes) - z0) > 0.02 + 0.0005 * z0) discard;

                float zl, zr, zu, zb;
                vec3 l = posAt(t - ivec2(1, 0), size, zl), r = posAt(t + ivec2(1, 0), size, zr);
                vec3 u = posAt(t + ivec2(0, 1), size, zu), b = posAt(t - ivec2(0, 1), size, zb);
                vec3 dx = abs(zr - z0) < abs(z0 - zl) ? r - p : p - l;
                vec3 dy = abs(zu - z0) < abs(z0 - zb) ? u - p : p - b;
                vec3 n = normalize(cross(dx, dy));
                if (dot(n, p) > 0.0) n = -n;   // towards the eye

                if (laid)
                {
                    // A pool, drip or smear: only on a floor (at the mark's height, above).
                    if (n.y < 0.8) discard;
                    fade = 1.0 - smoothstep(0.2, 0.4, height);
                }
                else if (n.y > 0.8)
                {
                    // A splash on a floor: the centre keeps the victim's height, so on a sloped floor it lands up to 1.5 below the floor or 2.2 above
                    // it. Tested as a wall, a centre a little above the floor cut a hard disc (radius 20 × its height) and one under it drew nothing.
                    float above = -rel.y;
                    if (above < -1.5) discard;
                    float reach = max(vShape.x, vShape.y);
                    fade = 1.0 - smoothstep(max(0.5 * reach, 2.5), max(reach, 3.5), above);
                }
                else
                {
                    // A splash on a wall: any surface that faces the mark, within the mark's half size of it along the surface's normal.
                    if (dot(n, -rel) < 0.05 * dist) discard;
                    float reach = max(vShape.x, vShape.y);
                    fade = 1.0 - smoothstep(0.5 * reach, reach, abs(dot(rel, n)));
                }
                if (abs(n.y) <= 0.8)
                {
                    vec3 tx = normalize(cross(vec3(0.0, 1.0, 0.0), n));
                    plane = vec2(dot(rel, tx), dot(rel, cross(n, tx)));
                }
            }
            vec2 uv = uvOf(plane);
            if (offSquare(uv)) discard;
            float pixel = z0 * 2.0 * pc.tanY / size.y;
            float lod = max(log2(max(pixel * float(textureSize(textures2D[nonuniformEXT(vSprite)], 0).x) / (2.0 * vShape.x), 1e-4)), 0.0);
            vec4 texel = textureLod(textures2D[nonuniformEXT(vSprite)], uv, lod);
            float a = clamp(texel.a * vColour.a * fade, 0.0, 1.0);
            if (!laid && !footprint)
            {
                // A splash's drops grow fewer from its middle out, to nothing at a reach that differs round the mark (0.55 to 1 of its half
                // size, by a noise of the direction, the mark's angle its seed). Which drops go is a noise a few drops across with a finer one:
                // whole drops, in patches. Its texture is an even field of drops to the square's sides: cut by a fade of the radius, however
                // uneven the rim, it read as a disc, and the gaps between neighbours as shapes with arcs for sides.
                vec2 c = uv * 2.0 - 1.0, seed = vec2(vShape.z * 17.0, vShape.z * 29.0);
                float r = length(c), reach = 0.55 + 0.45 * inkNoise(c / max(r, 1e-4) * 1.6 + seed);
                float thin = smoothstep(0.15, 1.0, r / reach) * 1.16 - 0.08;
                float kept = clamp((0.5 * inkNoise(uv * 14.0 + seed) + 0.5 * inkNoise(uv * 37.0 - seed) - 0.5) * 2.2 + 0.5, 0.0, 1.0);
                a *= smoothstep(thin - 0.07, thin + 0.07, kept);
            }
            // Multiplied into the lit surface: the blood's colour against a mid-grey albedo.
            vec3 ink = texel.rgb * vColour.rgb * 1.6 + 0.03;
            if (footprint)
            {
                a *= transfer(uv, fract(vShape.w), vColour.a);
                ink = min(texel.rgb * vColour.rgb * 0.82 + 0.025, vec3(1.0));
            }
            fragColour = vec4(mix(vec3(1.0), ink, a), 1.0);
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
        /// Centre: eye-relative, w the bounding sphere's radius; Shape: half width, half height, angle, w 0 floors, 1 any object surface, 2 terrain; Colour: the
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

        /// <summary>
        /// 1 when the targets' row 0 is the bottom (Vulkan), −1 when it is the top (Metal): the sign from a texel row to the projection's
        /// NDC Y.
        /// </summary>
        public float RowSign;
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
        /// Boot silhouette and transfer seed for a terrain imprint.
        /// </summary>
        public FootprintSoles.Sprite? Sole;
        /// <summary>
        /// How pressure and ink break up on this imprint, in [0, 1).
        /// </summary>
        public float Transfer;

        /// <summary>
        /// Where the mark was made (X, Z): a pool grows while its bleeder stays within √10 units of it.
        /// </summary>
        public Vector2 At;

        /// <summary>
        /// What it adds to each cell of the cover grid under it, and the radius it was added over (<see cref="Count"/>); 0 while it is not counted.
        /// </summary>
        public float Cover, Reach;

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
    /// Ground prints are temporary and have their own cap, independent of blood on objects.
    /// </summary>
    const int MaxFootprints = 512;

    /// <summary>
    /// Bounding radius of a sole including headroom for sloped terrain.
    /// </summary>
    const float FootprintRadius = 3.3f;

    /// <summary>
    /// Ground prints in time order, excluded from the surface decal save.
    /// </summary>
    readonly List<SurfaceDecal> _footprints = [];

    /// <summary>
    /// Cached model-derived sole masks.
    /// </summary>
    readonly FootprintSoles _soles;

    /// <summary>
    /// Serial used to give each stamp a different transfer pattern.
    /// </summary>
    uint _footprintSerial;

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
    /// This frame's near decals, each with the square of its distance to the eye over its radius (the smaller, the larger it looks).
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
    /// Milliseconds without a depth capture before the terrain or static depth copy is released.
    /// </summary>
    const long DepthCopyIdleMs = 5_000;

    /// <summary>
    /// The frame the terrain depth was copied in (-1: never).
    /// </summary>
    long _terrainDepthFrame = -1;

    /// <summary>
    /// When the terrain and static depth copies were last captured (<see cref="Environment.TickCount64"/>).
    /// </summary>
    long _terrainDepthUsedAt, _staticDepthUsedAt;

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
            if (!value)
            {
                ClearSurfaces();
                FreeStaticDepth();
                if (_footprints.Count == 0) FreeTerrainDepth();
            }
        }
    }

    /// <summary>
    /// The terrain's height at (X, Z) (the host gives <c>TerrainRenderer.HeightAt</c>): a splash on the ground is centred above it.
    /// </summary>
    public Func<float, float, float>? GroundHeight { get; set; }

    /// <summary>
    /// Number of decals on static objects.
    /// </summary>
    public int SurfaceCount => _surfaces.Count;

    /// <summary>
    /// The decal's circle round its square is within <see cref="Range"/> of the eye (the terrain's cells within the range are the ones on a
    /// texture).
    /// </summary>
    bool InRange(SurfaceDecal d)
    {
        if (!_surfaceEyeKnown) return true;   // no frame yet (a world just loaded): nothing runs out before the camera is known

        var (hw, hh) = Half(d);
        return Vector3.Distance(d.Centre, _surfaceEye) - MathF.Sqrt(hw * hw + hh * hh) <= MathF.Max(Range, 0);
    }

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
    /// Whether a ground print can be seen at this range, so its depth copy runs only when needed.
    /// </summary>
    internal bool FootprintsNear(Vector3 eye, Vector4[] frustum)
    {
        long now = Environment.TickCount64;
        if (_staticDepth is not null && now - _staticDepthUsedAt >= DepthCopyIdleMs) FreeStaticDepth();

        foreach (var d in _footprints)
        {
            float radius = SoleRadius(d);
            float reach = MathF.Max(Range, 0) + radius;
            if (Vector3.DistanceSquared(d.Centre, eye) <= reach * reach && FrustumTests.SphereVisible(frustum, d.Centre, radius))
                return true;
        }

        if (_terrainDepth is not null && now - _terrainDepthUsedAt >= DepthCopyIdleMs) FreeTerrainDepth();
        return false;
    }

    /// <summary>
    /// Drops the temporary ground prints when changing worlds.
    /// </summary>
    internal void ClearFootprints() => _footprints.Clear();

    /// <summary>
    /// Starts preparing the sole mask (the equipped footwear's, or a <paramref name="bare"/> body's feet) while the foot is still inside the pool.
    /// </summary>
    internal void PrepareFootprint(string mesh, bool bare = false) => _soles.Prepare(mesh, bare);

    /// <summary>
    /// Records a ground imprint at the terrain's height with an individual pressure pattern. A <paramref name="skin"/> mesh with no foot of its own
    /// (a fitted leg's cap) gives way to <paramref name="body"/>'s.
    /// </summary>
    internal void AddFootprint(Vector3 at, Vector2 across, uint abgr, float life, byte variant, string? mesh, bool skin, string? body = null)
    {
        if (_footprints.Count >= MaxFootprints) return;

        bool right = (variant & 1) != 0;
        var sole = mesh is null ? null : _soles.For(mesh, right, bare: skin);
        if (sole is null && skin && body is not null && mesh is not null && _soles.Footless(mesh)) sole = _soles.For(body, right, bare: true);
        if (sole is null) EnsureGenericFootprints();

        uint sample = unchecked(++_footprintSerial * 2654435761u);
        _footprints.Add(new SurfaceDecal
        {
            Type = TerrainDecalType.Footprint,
            Variant = variant,
            Centre = at,
            Angle = MathF.Atan2(across.X, across.Y),
            Colour = abgr,
            Expires = _surfaceClock + life,
            Sole = sole,
            Transfer = (sample >> 9) * (1f / 8388608),
        });
    }

    /// <summary>
    /// Creates fallback sole textures on the first print that needs one.
    /// </summary>
    void EnsureGenericFootprints()
    {
        const int type = (int)TerrainDecalType.Footprint;
        if (_sprites[type].Length != 0) return;

        var masks = FootprintTexture.Create(_gpu);
        _sprites[type] = masks;
        _spriteIndex[type] = new uint[masks.Length];
        Array.Fill(_spriteIndex[type], uint.MaxValue);
        _spriteBias = float.NaN;
    }

    /// <summary>
    /// Drops every decal kept for the surfaces (a new world).
    /// </summary>
    public void ClearSurfaces()
    {
        _surfaces.Clear();
        _cover.Clear();
        _surfacePools.Clear();
        _surfaceEyeKnown = false;
    }

    /// <summary>
    /// The decals kept for the surfaces as a save holds them (<see cref="SaveSurfaceDecals"/>), oldest first, each with its life left.
    /// </summary>
    public IEnumerable<SurfaceBloodDecal> SavedSurfaces =>
        _surfaces.Select(SavedSurface);

    /// <summary>
    /// A kept decal as a save holds it; a life that ran out in view (see <see cref="AgeSurfaces"/>) is saved as 0: it stays while the camera is near.
    /// </summary>
    SurfaceBloodDecal SavedSurface(SurfaceDecal d) =>
        new(d.Type, (byte)d.Variant, d.Centre, d.Angle, d.Size, MathF.Max(d.Expires - _surfaceClock, 0), d.Colour, d.Floor);

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
            if (s.Type > TerrainDecalType.Smear || s.Variant >= _sprites[(int)s.Type].Length || s.Life < 0 || s.Size <= 0)
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
    /// Ages the decals by <paramref name="dt"/> seconds of game time. As the terrain's (<c>TerrainDecalStore.Age</c>, the game's rule: a decal at
    /// life 0 is removed unless its cell is on a texture now), a decal whose life ran out goes only while it is out of the camera's range: within it,
    /// it stays as drawn, so blood in view never vanishes by itself (it used to fade out in its last second while the terrain's blood beside it
    /// stayed).
    /// </summary>
    public void AgeSurfaces(float dt)
    {
        if (dt <= 0 || (_surfaces.Count == 0 && _footprints.Count == 0)) return;

        _surfaceClock += dt;
        _footprints.RemoveAll(d => d.Expires <= _surfaceClock);
        if (_surfaces.RemoveAll(Gone) == 0) return;

        var gone = _surfacePools
            .Where(p => p.Value.Expires <= _surfaceClock && !InRange(p.Value))
            .Select(p => p.Key)
            .ToList();
        foreach (var key in gone) _surfacePools.Remove(key);
    }

    /// <summary>
    /// A decal whose life ran out away from the camera: no longer counted in the cover, and to be removed.
    /// </summary>
    bool Gone(SurfaceDecal d)
    {
        if (d.Expires > _surfaceClock || InRange(d)) return false;
        Uncount(d);
        return true;
    }

    /// <summary>
    /// A drip, smear or splash at <paramref name="at"/> (the mark's place, its Y the character's feet) turned to <paramref name="direction"/> (X/Z),
    /// of the game's
    /// <paramref name="size"/>. A splash lands on any surface round a point at the body's height, the others only on floors at the mark's height.
    /// </summary>
    public void AddSurface(TerrainDecalType type, Vector3 at, Vector2 direction, float size, uint abgr, Random random)
    {
        if (!_surfacesOn || type is TerrainDecalType.Line or TerrainDecalType.Footprint) return;
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
            if (MathF.Abs(ReachOf(pool) - pool.Reach) >= CoverCell * 0.5f)
            {
                Uncount(pool);
                Count(pool);
            }
            return;
        }
        // At the cap with every decal a pool still bleeding (more bleeders than decals): this one bleeds on the terrain only, rather than each new
        // pool pushing out another bleeder's, which made one again the next tick (thousands a frame, none growing).
        if (_surfaces.Count >= MaxSurfaces && !MakeRoom()) return;
        pool = Make(TerrainDecalType.Pool, at, random.Next(variants), random.NextSingle() * MathF.Tau, grow, abgr);
        (pool.Who, pool.Bled) = (who, _surfaceClock);
        Keep(pool, always: true);
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
            Uncount(d);
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
    /// Adds a decal, making room at the cap: the oldest that is not a pool still bleeding, else the oldest. Not where the surface is soaked
    /// already (<see cref="Soaked"/>) unless <paramref name="always"/> (a bleeder's pool, which grows past what is there).
    /// </summary>
    void Keep(SurfaceDecal decal, bool always = false)
    {
        if (!always && Soaked(decal)) return;
        if (_surfaces.Count >= MaxSurfaces && !MakeRoom())
        {
            var oldest = _surfaces[0];
            // Its bleeder's pool by the key it keeps: a scan of the pools per new decal took most of a frame with thousands bleeding at the cap.
            if (oldest.Who is long who && _surfacePools.TryGetValue(who, out var pool) && pool == oldest)
                _surfacePools.Remove(who);
            Uncount(oldest);
            _surfaces.RemoveAt(0);
        }
        _surfaces.Add(decal);
        Count(decal);
    }

    /// <summary>
    /// The side of a cell of the cover grid (X, Z) and the height of one of its bands, in world units.
    /// </summary>
    const float CoverCell = 4, CoverBand = 8;

    /// <summary>
    /// The cover at which a place takes no new mark: the sum of the inked shares of the marks lying over it (<see cref="Inked"/>). Marks falling
    /// at random leave about 80 % of the surface inked by then (1 less e to the power of minus 1.6), and each one more is a whole square drawn
    /// for little new blood: a long fight in one room stacked hundreds of them on every pixel (docs/game/blood-decals.md "Blood on surfaces").
    /// </summary>
    const float CoverFull = 1.6f;

    /// <summary>
    /// The cover (<see cref="CoverFull"/>) the kept marks give each cell of <see cref="CoverCell"/> under them, by band of height.
    /// </summary>
    readonly Dictionary<(int X, int Z, int Band), float> _cover = [];

    /// <summary>
    /// The share of its square a mark of this type inks: the mean alpha of the type's textures (docs/game/blood-decals.md "Blood on surfaces").
    /// </summary>
    static float Inked(TerrainDecalType type) => type switch
    {
        TerrainDecalType.Pool => 0.35f,
        TerrainDecalType.Smear => 0.25f,
        _ => 0.1f,
    };

    /// <summary>
    /// The radius a mark is counted over: of a circle of four fifths of its square's area.
    /// </summary>
    float ReachOf(SurfaceDecal d)
    {
        var (hw, hh) = Half(d);
        return MathF.Sqrt(hw * hh);
    }

    /// <summary>
    /// The cover grid's cell along an axis.
    /// </summary>
    static int CoverIndex(float along) => (int)MathF.Floor(along / CoverCell);

    /// <summary>
    /// Counts a kept mark in the cover of the cells under it: its inked share times its alpha, less for one smaller than a cell.
    /// </summary>
    void Count(SurfaceDecal d)
    {
        float reach = ReachOf(d);
        float share = MathF.Min(1, MathF.PI * reach * reach / (CoverCell * CoverCell));
        (d.Cover, d.Reach) = (Inked(d.Type) * ((d.Colour >> 24) / 255f) * share, reach);
        AddCover(d, d.Cover);
    }

    /// <summary>
    /// Takes a mark out of the cover (it is removed, or counted again at a new size).
    /// </summary>
    void Uncount(SurfaceDecal d)
    {
        if (d.Cover <= 0) return;
        AddCover(d, -d.Cover);
        d.Cover = 0;
    }

    /// <summary>
    /// Adds <paramref name="amount"/> to the cells whose middle is within the mark's <see cref="SurfaceDecal.Reach"/>, and to the one it is in.
    /// </summary>
    void AddCover(SurfaceDecal d, float amount)
    {
        int band = (int)MathF.Floor(d.Centre.Y / CoverBand);
        int ownX = CoverIndex(d.Centre.X), ownZ = CoverIndex(d.Centre.Z);
        for (int z = CoverIndex(d.Centre.Z - d.Reach); z <= CoverIndex(d.Centre.Z + d.Reach); z++)
        {
            for (int x = CoverIndex(d.Centre.X - d.Reach); x <= CoverIndex(d.Centre.X + d.Reach); x++)
            {
                var middle = new Vector2((x + 0.5f) * CoverCell, (z + 0.5f) * CoverCell);
                bool under = Vector2.DistanceSquared(middle, new Vector2(d.Centre.X, d.Centre.Z)) <= d.Reach * d.Reach;
                if (!under && (x != ownX || z != ownZ)) continue;
                float cover = _cover.GetValueOrDefault((x, z, band)) + amount;
                if (cover > 1e-4f) _cover[(x, z, band)] = cover;
                else _cover.Remove((x, z, band));
            }
        }
    }

    /// <summary>
    /// The cover at a place: of the marks within half a band above and below it.
    /// </summary>
    float CoverAt(float x, float y, float z)
    {
        int low = (int)MathF.Floor((y - CoverBand * 0.5f) / CoverBand), high = (int)MathF.Floor((y + CoverBand * 0.5f) / CoverBand);
        float cover = _cover.GetValueOrDefault((CoverIndex(x), CoverIndex(z), low));
        return high == low ? cover : cover + _cover.GetValueOrDefault((CoverIndex(x), CoverIndex(z), high));
    }

    /// <summary>
    /// The surface under a new mark is soaked: at its centre and half-way to its edge on four sides the cover is <see cref="CoverFull"/>. A mark
    /// reaching past the soaked place is kept.
    /// </summary>
    bool Soaked(SurfaceDecal d)
    {
        float half = ReachOf(d) * 0.5f;
        var c = d.Centre;
        return CoverAt(c.X, c.Y, c.Z) >= CoverFull && CoverAt(c.X - half, c.Y, c.Z) >= CoverFull && CoverAt(c.X + half, c.Y, c.Z) >= CoverFull
            && CoverAt(c.X, c.Y, c.Z - half) >= CoverFull && CoverAt(c.X, c.Y, c.Z + half) >= CoverFull;
    }

    /// <summary>
    /// The half sizes of a decal in world units (the terrain's rule: size × texels × 288 / 4096).
    /// </summary>
    (float Width, float Height) Half(SurfaceDecal d)
    {
        if (d.Type == TerrainDecalType.Footprint)
            return d.Sole is { } sole ? (sole.Width, sole.Length) : (FootprintTexture.HalfWidth, FootprintTexture.HalfLength);
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
    /// notes the frame.
    /// </summary>
    void Capture(GpuTexture sceneDepth, ref GpuTexture? target, ref uint index, string name, bool first)
    {
        if (target is null || target.Desc.Width != sceneDepth.Desc.Width || target.Desc.Height != sceneDepth.Desc.Height
            || target.Desc.Format != sceneDepth.Desc.Format)
        {
            if (index != uint.MaxValue) _gpu.Bindless.Free(BindlessKind.Texture2D, index);
            target?.Dispose();
            target = _gpu.CreateTexture(new TextureDesc(sceneDepth.Desc.Format, sceneDepth.Desc.Width, sceneDepth.Desc.Height,
                Use: TextureUse.Sampled | TextureUse.TransferDst | TextureUse.DepthTarget, Name: name)
            {
                Transient = TransientLifetime.Single(TransientPhase.OpaqueScene),
            });
            index = _gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(NearestSampler(), target.View()));
        }
        _gpu.BeginTransientPhase(TransientPhase.OpaqueScene);
        var cmd = _gpu.BeginNative(name);
        cmd.Order();
        cmd.Blit(sceneDepth, target, TextureMinFilter.Nearest);
        cmd.Order();
        _gpu.EndNative(cmd);
        long now = Environment.TickCount64;
        if (first) (_terrainDepthFrame, _terrainDepthUsedAt) = (_gpu.Frame.Number, now);
        else (_staticDepthFrame, _staticDepthUsedAt) = (_gpu.Frame.Number, now);
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
        Vector3 eye, Vector2 jitterNdc, Vector4[] frustum)
    {
        if (_terrainDepthFrame != _gpu.Frame.Number) return;

        bool drawObjects = _surfacesOn && _staticDepthFrame == _gpu.Frame.Number;
        RegisterSprites();
        var back = ViewWindow.Back(rotation, tanX, tanY);
        (tanX, tanY) = (ViewWindow.Tan(tanX, tanY).X, ViewWindow.Tan(tanX, tanY).Y);
        CollectSurfaceNear(eye, back, frustum, drawObjects);
        if (_surfaceNear.Count == 0) return;
        int n = FillSurfaceInstances(eye);
        if (n == 0) return;

        var program = _surfaceProgram ??= _frame.Program(SurfaceVertex, SurfaceFragment, "blood surfaces");
        BindSurfaceDepth(depth);
        DrawSurfaces(colour, program, n, near, far, rotation, tanX, tanY, back, jitterNdc);
    }

    /// <summary>
    /// The decals within the range of the eye and in front of it (<paramref name="back"/> is the view's back), at most
    /// <see cref="SurfaceDraws"/> of them, the ones that look largest, into <see cref="_surfaceNear"/>.
    /// </summary>
    void CollectSurfaceNear(Vector3 eye, Vector3 back, Vector4[] frustum, bool drawObjects)
    {
        _surfaceNear.Clear();
        float range = MathF.Max(Range, 0);
        if (drawObjects)
        {
            foreach (var d in _surfaces)
            {
                var (hw, hh) = Half(d);
                float radius = SphereOf(d, hw, hh);
                var rel = d.Centre - eye;
                // Past the range, or behind the eye.
                if (radius <= 0 || rel.Length() - radius > range || Vector3.Dot(rel, back) > radius) continue;
                if (!FrustumTests.SphereVisible(frustum, d.Centre, radius * 1.01f + 0.5f)) continue;
                _surfaceNear.Add((rel.LengthSquared() / (radius * radius), d));
            }
        }

        foreach (var d in _footprints)
        {
            float radius = SoleRadius(d);
            float reach = range + radius;
            float distance = Vector3.DistanceSquared(d.Centre, eye);
            if (distance > reach * reach || !FrustumTests.SphereVisible(frustum, d.Centre, radius)) continue;
            _surfaceNear.Add((distance / (radius * radius), d));
        }

        int draws = SurfaceDraws;
        if (_surfaceNear.Count > draws)
        {
            // More in view than the slider lets through: the ones that look smallest go (the square of distance over radius), wherever they are.
            // The nearest to the eye were kept before, the marks lying on the terrain among them, which this pass never draws: a room's blood went
            // and came back as the camera turned or zoomed, once a fight outside had left more than the cap in view.
            _surfaceNear.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            _surfaceNear.RemoveRange(draws, _surfaceNear.Count - draws);
        }
    }

    /// <summary>
    /// The radius of the sphere a mark is drawn in: round its square; a splash's also holds the surfaces up to its half size off its plane.
    /// </summary>
    static float SphereOf(SurfaceDecal d, float hw, float hh) => d.Floor ? MathF.Sqrt(hw * hw + hh * hh) : 1.55f * MathF.Max(hw, hh);

    /// <summary>
    /// A sole's bounding sphere includes room for the terrain's slope.
    /// </summary>
    static float SoleRadius(SurfaceDecal decal) => decal.Sole is { } sole
        ? MathF.Sqrt(sole.Width * sole.Width + sole.Length * sole.Length + 4) : FootprintRadius;

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
            uint index = d.Sole is { } sole ? _soles.Index(sole) : _spriteIndex[(int)d.Type][d.Variant];
            var (hw, hh) = Half(d);
            if (index == uint.MaxValue || hw <= 0) continue;
            uint abgr = d.Colour;
            // A decal on a surface is as one drawn on a cell's texture: its alpha at the start, no fade (docs/game/blood-decals.md); a footprint
            // fades with its life.
            bool footprint = d.Type == TerrainDecalType.Footprint;
            float alpha = (abgr >> 24) / 255f;
            if (footprint) alpha = MathF.Min(alpha, d.Expires - _surfaceClock);
            _surfaceInstances[n++] = new SurfaceInstance
            {
                Centre = new Vector4(d.Centre - eye, footprint ? SoleRadius(d) : SphereOf(d, hw, hh)),
                Shape = new Vector4(hw, hh, d.Angle, footprint ? 2 + d.Transfer : d.Floor ? 0 : 1),
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
            StaticDepth = _staticDepthFrame == _gpu.Frame.Number ? _staticDepthIndex : _terrainDepthIndex,
            Jitter = jitterNdc,
            RowSign = _gpu.RenderTargetsBottomUp ? 1 : -1,
        };
        cmd.PushConstants(program, GpuStage.Vertex | GpuStage.Fragment, in push);
        for (int first = 0; first < n; first += DrawChunk)
        {
            int count = Math.Min(DrawChunk, n - first);
            var rows = _gpu.Frame.AllocateConstants((ulong)(count * SurfaceInstance.Size), 16);
            MemoryMarshal.AsBytes(_surfaceInstances.AsSpan(first, count)).CopyTo(new Span<byte>(rows.Pointer, count * SurfaceInstance.Size));
            Span<GpuBufferBinding> buffers =
            [
                new(rows.Handle, rows.Offset),
                new(rows.Handle, rows.Offset + 16),
                new(rows.Handle, rows.Offset + 32),
                new(rows.Handle, rows.Offset + 48),
            ];

            cmd.BindVertexBuffers(0, buffers);
            cmd.Draw(4, (uint)count);
        }
        cmd.EndRendering();
        cmd.Order();
        _gpu.EndNative(cmd);
    }

    /// <summary>
    /// Releases the terrain depth copy and its bindless entry.
    /// </summary>
    void FreeTerrainDepth()
    {
        if (_terrainDepthIndex != uint.MaxValue) _gpu.Bindless.Free(BindlessKind.Texture2D, _terrainDepthIndex);
        _terrainDepth?.Dispose();
        (_terrainDepth, _terrainDepthIndex, _terrainDepthFrame) = (null, uint.MaxValue, -1);
    }

    /// <summary>
    /// Releases the static depth copy and its bindless entry.
    /// </summary>
    void FreeStaticDepth()
    {
        if (_staticDepthIndex != uint.MaxValue) _gpu.Bindless.Free(BindlessKind.Texture2D, _staticDepthIndex);
        _staticDepth?.Dispose();
        (_staticDepth, _staticDepthIndex, _staticDepthFrame) = (null, uint.MaxValue, -1);
    }

    /// <summary>
    /// Frees the depth copies, their bindless entries and the surface program.
    /// </summary>
    void DisposeSurfaces()
    {
        _soles.Dispose();
        if (_surfaceDepthIndex != uint.MaxValue) _gpu.Bindless.Free(BindlessKind.Texture2D, _surfaceDepthIndex);
        FreeTerrainDepth();
        FreeStaticDepth();
        if (_surfaceProgram is { } program)
        {
            _gpu.Pipelines.Forget(program);
            _gpu.Shaders.Dispose(program);
        }
    }
}