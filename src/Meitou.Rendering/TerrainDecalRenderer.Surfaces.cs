using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The <c>bloodsurfaces</c> switch (docs/game/blood-decals.md "Blood on surfaces"): the blood marks also land on walls, buildings, floors and other static objects. Each mark is
/// kept with its 3D place (<see cref="AddSurface"/>, <see cref="BleedSurface"/>) and drawn after the opaque scene, before the characters, as a screen-space decal: a sphere
/// round the mark, every scene pixel in it rebuilt from the depth with a normal from the depth's slope, and the decal's texture projected along that normal. The result
/// multiplies the scene colour (so light, shadow and haze stay the surface's own). The terrain keeps the blood the cells give it: <see cref="CaptureTerrainDepth"/> keeps the depth
/// the terrain alone wrote, and a pixel whose depth is still that one is terrain.
/// </summary>
public sealed unsafe partial class TerrainDecalRenderer
{
    /// <summary>The most decals kept, and the most drawn in a frame (the nearest).</summary>
    const int MaxSurfaces = 1024, MaxSurfaceDraws = 512;

    /// <summary>How high above the feet a splash is centred (a blow lands at the body, not the soles).</summary>
    const float SplashLift = 0.9f;

    /// <summary>Marks above the terrain by more than this stand on a floor of a building: a splash is centred on that floor, not on the ground.</summary>
    const float FloorHeight = 3;

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

        // The depth along the view axis from a stored depth: the scene's depth is the projection's 0..1 depth moved to the clip space's 0.5..1 range (2d - 1 undoes it, as
        // the fog volumes and the GI resolve read it).
        float linearDepth(float stored) { float zd = 2.0 * stored - 1.0; return pc.planes.x * pc.planes.y / (pc.planes.y - zd * (pc.planes.y - pc.planes.x)); }

        // The surface point under a depth texel, relative to the eye, and its linear depth. The depth was drawn with the projection moved by the jitter: the texel's ray
        // is the unjittered one at its centre less that move.
        vec3 posAt(ivec2 t, vec2 size, out float z)
        {
            t = clamp(t, ivec2(0), ivec2(size) - 1);
            z = linearDepth(texelFetch(textures2D[pc.depth], t, 0).r);
            vec2 uvn = (vec2(t) + 0.5) / size * 2.0 - 1.0 - pc.jitter;
            vec2 xy = uvn * vec2(pc.tanX, pc.tanY) * z;
            return xy.x * pc.right + xy.y * pc.up - z * pc.back;
        }

        void main()
        {
            vec2 size = vec2(textureSize(textures2D[pc.depth], 0));
            ivec2 t = ivec2(gl_FragCoord.xy);
            float d0 = texelFetch(textures2D[pc.depth], t, 0).r;
            if (d0 >= 0.99999) discard;
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
            if (abs(linearDepth(texelFetch(textures2D[pc.staticDepth], t, 0).r) - z0) > 0.02 + 0.0005 * z0) discard;
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

    [StructLayout(LayoutKind.Sequential)]
    struct SurfaceInstance
    {
        public const int Size = 64;
        public Vector4 Centre, Shape, Colour;
        public uint Sprite, P0, P1, P2;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SurfacePush
    {
        public Vector3 Right;
        public float TanX;
        public Vector3 Up;
        public float TanY;
        public Vector3 Back;
        public uint Depth;
        public Vector2 Planes;
        public uint TerrainDepth, StaticDepth;
        public Vector2 Jitter;
    }

    static readonly VertexLayout SurfaceRows = new(
        new VertexInput(0, InternalFormat.Rgba32f, SurfaceInstance.Size, true),
        new VertexInput(1, InternalFormat.Rgba32f, SurfaceInstance.Size, true),
        new VertexInput(2, InternalFormat.Rgba32f, SurfaceInstance.Size, true),
        new VertexInput(3, InternalFormat.R32ui, SurfaceInstance.Size, true));

    // The scene colour times the decal's colour (red, green and blue; the alpha is the characters' mask).
    static readonly BlendState Multiply = new(true, BlendingFactor.DstColor, BlendingFactor.Zero);

    /// <summary>One decal on the surfaces: where it is in 3D, how it is turned and how big.</summary>
    sealed class SurfaceDecal
    {
        public TerrainDecalType Type;
        public int Variant;
        public Vector3 Centre;
        public float Angle, Size, Expires;
        public uint Colour;
        public bool Floor;
        public Vector2 At;
        /// <summary>The bleeder whose growing pool it is (<see cref="BleedSurface"/>), else null.</summary>
        public long? Who;
    }

    readonly List<SurfaceDecal> surfaces = [];
    readonly Dictionary<long, SurfaceDecal> surfacePools = [];
    readonly List<(float Distance, SurfaceDecal Decal)> surfaceNear = [];
    float surfaceClock;
    GpuProgram? surfaceProgram;
    GpuPipeline? surfacePipeline;
    GpuTexture? surfaceDepth, terrainDepth;
    uint surfaceDepthIndex = uint.MaxValue, terrainDepthIndex = uint.MaxValue;
    SurfaceInstance[] surfaceInstances = new SurfaceInstance[64];

    bool surfacesOn;

    /// <summary>
    /// The <c>bloodsurfaces</c> switch: marks are kept and drawn on the other surfaces as well as the terrain; off (Faithful), none are, and the ones kept are dropped.
    /// </summary>
    public bool Surfaces
    {
        get => surfacesOn;
        set
        {
            if (value == surfacesOn) return;
            surfacesOn = value;
            if (!value) ClearSurfaces();
        }
    }

    /// <summary>The terrain's height at (X, Z) (the host gives <c>TerrainRenderer.HeightAt</c>): a splash on the ground is centred above it.</summary>
    public Func<float, float, float>? GroundHeight { get; set; }

    /// <summary>The decals kept for the surfaces now.</summary>
    public int SurfaceCount => surfaces.Count;

    /// <summary>
    /// Whether any decal kept for the surfaces is within <see cref="TerrainDecalRenderer.Range"/> of <paramref name="eye"/>: the frame copies the depth and splits its passes for them only then
    /// (<see cref="RecordSurfaces"/> culls the rest per decal).
    /// </summary>
    public bool SurfacesNear(Vector3 eye)
    {
        if (!surfacesOn) return false;
        float range = MathF.Max(Range, 0);
        foreach (var d in surfaces)
        {
            var (hw, hh) = Half(d);
            float radius = MathF.Sqrt(hw * hw + hh * hh);
            if (radius > 0 && Vector3.Distance(d.Centre, eye) - radius <= range) return true;
        }
        return false;
    }

    /// <summary>Drops every decal kept for the surfaces (a new world).</summary>
    public void ClearSurfaces()
    {
        surfaces.Clear();
        surfacePools.Clear();
    }

    /// <summary>The decals kept for the surfaces as a save holds them (<see cref="SaveSurfaceDecals"/>), oldest first, each with its life left.</summary>
    public IEnumerable<SurfaceBloodDecal> SavedSurfaces =>
        surfaces.Select(d => new SurfaceBloodDecal(d.Type, (byte)d.Variant, d.Centre, d.Angle, d.Size, d.Expires - surfaceClock, d.Colour, d.Floor));

    /// <summary>
    /// Puts back the decals a save held (after <see cref="ClearSurfaces"/>, a new world): each with its life left, the newest kept when over <see cref="MaxSurfaces"/>.
    /// None while the switch is off; one whose texture is not loaded is skipped. A pool no longer grows (the bleeding character is not kept).
    /// </summary>
    public void LoadSurfaces(IReadOnlyList<SurfaceBloodDecal> saved)
    {
        if (!surfacesOn) return;
        for (int i = Math.Max(0, saved.Count - MaxSurfaces); i < saved.Count; i++)
        {
            var s = saved[i];
            if ((int)s.Type >= sprites.Length || s.Variant >= sprites[(int)s.Type].Length || s.Life <= 0 || s.Size <= 0) continue;
            Keep(new SurfaceDecal
            {
                Type = s.Type, Variant = s.Variant, Centre = s.Centre, Angle = s.Angle, Size = s.Size, Colour = s.Colour, Floor = s.Floor,
                Expires = surfaceClock + s.Life, At = new Vector2(s.Centre.X, s.Centre.Z),
            });
        }
    }

    /// <summary>Ages the decals by <paramref name="dt"/> seconds of game time; those whose life ran out go.</summary>
    public void AgeSurfaces(float dt)
    {
        if (dt <= 0 || surfaces.Count == 0) return;
        surfaceClock += dt;
        if (surfaces.RemoveAll(d => d.Expires <= surfaceClock) > 0)
            foreach (var key in surfacePools.Where(p => p.Value.Expires <= surfaceClock).Select(p => p.Key).ToList()) surfacePools.Remove(key);
    }

    /// <summary>
    /// A drip, smear or splash at <paramref name="at"/> (the mark's place, its Y the character's feet) turned to <paramref name="direction"/> (X/Z), of the game's
    /// <paramref name="size"/>. A splash lands on any surface round a point at the body's height, the others only on floors at the mark's height.
    /// </summary>
    public void AddSurface(TerrainDecalType type, Vector3 at, Vector2 direction, float size, uint abgr, Random random)
    {
        if (!surfacesOn || type == TerrainDecalType.Line) return;
        int variants = sprites[(int)type].Length;
        if (variants == 0) return;
        Keep(Make(type, at, random.Next(variants), MathF.Atan2(direction.X, direction.Y), size, abgr));
    }

    /// <summary>
    /// A bleeding character that stands still, as <see cref="TerrainDecalStore.Bleed"/> (the same growth rule, kept apart so a pool on an upper floor grows too):
    /// <paramref name="bleeding"/> × <paramref name="dt"/> × 0.2 grows the pool at its feet, within √10 units of where it started, else a new pool.
    /// </summary>
    public void BleedSurface(long who, Vector3 at, float bleeding, float dt, uint abgr, Random random)
    {
        if (!surfacesOn) return;
        int variants = sprites[(int)TerrainDecalType.Pool].Length;
        if (variants == 0) return;
        float grow = bleeding * dt * 0.2f;
        if (surfacePools.TryGetValue(who, out var pool) && Vector2.DistanceSquared(pool.At, new Vector2(at.X, at.Z)) <= 10)
        {
            pool.Size = MathF.Min(pool.Size + grow / MathF.Max(1, pool.Size * 10), TerrainDecalStore.PoolMax);
            return;
        }
        pool = Make(TerrainDecalType.Pool, at, random.Next(variants), random.NextSingle() * MathF.Tau, grow, abgr);
        pool.Who = who;
        Keep(pool);
        surfacePools[who] = pool;
    }

    SurfaceDecal Make(TerrainDecalType type, Vector3 at, int variant, float angle, float size, uint abgr)
    {
        float ground = GroundHeight?.Invoke(at.X, at.Z) ?? at.Y;
        bool floor = type != TerrainDecalType.Splash;
        // A splash on the ground is centred above the ground at its place (the mark carries the victim's height, which a slope or a flight of up to 20 units may leave behind).
        float y = floor ? at.Y : (at.Y - ground > FloorHeight ? at.Y : ground) + SplashLift;
        return new SurfaceDecal
        {
            Type = type,
            Variant = variant,
            Centre = new Vector3(at.X, y, at.Z),
            Angle = angle,
            Size = size,
            Colour = abgr,
            Expires = surfaceClock + TerrainDecalStore.NewLife,
            Floor = floor,
            At = new Vector2(at.X, at.Z),
        };
    }

    void Keep(SurfaceDecal decal)
    {
        if (surfaces.Count >= MaxSurfaces)
        {
            var oldest = surfaces[0];
            // Its bleeder's pool by the key it keeps: a scan of the pools per new decal took most of a frame with thousands bleeding at the cap.
            if (oldest.Who is long who && surfacePools.TryGetValue(who, out var pool) && pool == oldest) surfacePools.Remove(who);
            surfaces.RemoveAt(0);
        }
        surfaces.Add(decal);
    }

    /// <summary>The half sizes of a decal in world units (the terrain's rule: size × texels × 288 / 4096).</summary>
    (float Width, float Height) Half(SurfaceDecal d)
    {
        var image = sprites[(int)d.Type][d.Variant];
        if (image is null) return (0, 0);
        float k = d.Size * TerrainDecalCells.Size / 4096;
        return (image.Texture.Desc.Width * k, image.Texture.Desc.Height * k);
    }

    /// <summary>
    /// Keeps the depth buffer as the terrain alone left it: between the terrain's draw and the objects' (the host is closed), so <see cref="RecordSurfaces"/> can tell terrain
    /// pixels, which the cells already carry blood on, from the rest.
    /// </summary>
    internal void CaptureTerrainDepth(GpuTexture sceneDepth) => Capture(sceneDepth, ref terrainDepth, ref terrainDepthIndex, "blood terrain depth", true);

    /// <summary>
    /// Keeps the depth buffer as the static objects left it, before the foliage and everything else that sways or moves is drawn: a pixel nearer than this
    /// is not static, and takes no blood.
    /// </summary>
    internal void CaptureStaticDepth(GpuTexture sceneDepth) => Capture(sceneDepth, ref staticDepth, ref staticDepthIndex, "blood static depth", false);

    void Capture(GpuTexture sceneDepth, ref GpuTexture? target, ref uint index, string name, bool first)
    {
        if (!surfacesOn || surfaces.Count == 0) return;
        if (target is null || target.Desc.Width != sceneDepth.Desc.Width || target.Desc.Height != sceneDepth.Desc.Height || target.Desc.Format != sceneDepth.Desc.Format)
        {
            if (index != uint.MaxValue) gpu.Bindless.Free(BindlessKind.Texture2D, index);
            target?.Dispose();
            target = gpu.CreateTexture(new TextureDesc(sceneDepth.Desc.Format, sceneDepth.Desc.Width, sceneDepth.Desc.Height,
                Use: TextureUse.Sampled | TextureUse.TransferDst | TextureUse.DepthTarget, Name: name));
            index = gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(NearestSampler(), target.View()));
        }
        var cmd = gpu.BeginNative(name);
        cmd.Order();
        cmd.Blit(sceneDepth, target, TextureMinFilter.Nearest);
        cmd.Order();
        gpu.EndNative(cmd);
        if (first) terrainDepthFrame = gpu.Frame.Number; else staticDepthFrame = gpu.Frame.Number;
    }

    long staticDepthFrame = -1;
    GpuTexture? staticDepth;
    uint staticDepthIndex = uint.MaxValue;

    long terrainDepthFrame = -1;

    GpuSampler NearestSampler() => gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge,
        TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));

    /// <summary>
    /// Draws the decals near <paramref name="eye"/> over <paramref name="colour"/> from <paramref name="depth"/> (the near slice's, planes <paramref name="near"/> and
    /// <paramref name="far"/>). <paramref name="rotation"/> is the view without its translation. Between the scene's passes: the host is closed, and
    /// <see cref="CaptureTerrainDepth"/> ran this frame.
    /// </summary>
    internal void RecordSurfaces(GpuTexture colour, GpuTexture depth, float near, float far, Matrix4x4 rotation, float tanX, float tanY, Vector3 eye, Vector2 jitterNdc)
    {
        if (!surfacesOn || surfaces.Count == 0 || terrainDepth is null || terrainDepthFrame != gpu.Frame.Number || staticDepthFrame != gpu.Frame.Number) return;
        RegisterSprites();
        var back = new Vector3(rotation.M13, rotation.M23, rotation.M33);
        surfaceNear.Clear();
        float range = MathF.Max(Range, 0);
        foreach (var d in surfaces)
        {
            var (hw, hh) = Half(d);
            float radius = MathF.Sqrt(hw * hw + hh * hh);
            var rel = d.Centre - eye;
            // Past the range, or behind the eye.
            if (radius <= 0 || rel.Length() - radius > range || Vector3.Dot(rel, back) > radius) continue;
            surfaceNear.Add((rel.LengthSquared(), d));
        }
        if (surfaceNear.Count == 0) return;
        if (surfaceNear.Count > MaxSurfaceDraws)
        {
            surfaceNear.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            surfaceNear.RemoveRange(MaxSurfaceDraws, surfaceNear.Count - MaxSurfaceDraws);
        }
        if (surfaceInstances.Length < surfaceNear.Count) surfaceInstances = new SurfaceInstance[Math.Max(surfaceNear.Count, surfaceInstances.Length * 2)];
        int n = 0;
        foreach (var (_, d) in surfaceNear)
        {
            uint index = spriteIndex[(int)d.Type][d.Variant];
            var (hw, hh) = Half(d);
            if (index == uint.MaxValue || hw <= 0) continue;
            float life = d.Expires - surfaceClock;
            uint abgr = d.Colour;
            surfaceInstances[n++] = new SurfaceInstance
            {
                Centre = new Vector4(d.Centre - eye, MathF.Sqrt(hw * hw + hh * hh)),   // the sphere round the decal's square
                Shape = new Vector4(hw, hh, d.Angle, d.Floor ? 0 : 1),
                Colour = new Vector4((abgr & 0xFF) / 255f, (abgr >> 8 & 0xFF) / 255f, (abgr >> 16 & 0xFF) / 255f, MathF.Min((abgr >> 24) / 255f, life)),
                Sprite = index,
            };
        }
        if (n == 0) return;

        var program = surfaceProgram ??= frame.Program(SurfaceVertex, SurfaceFragment, "blood surfaces");
        if (surfaceDepth != depth)
        {
            if (surfaceDepthIndex != uint.MaxValue) gpu.Bindless.Free(BindlessKind.Texture2D, surfaceDepthIndex);
            surfaceDepthIndex = gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(NearestSampler(), depth.View()));
            surfaceDepth = depth;
        }

        var cmd = gpu.BeginNative("blood surfaces");
        cmd.Order();
        var t = PassTargets.Of(colour, null);
        cmd.BeginRendering(t.Rendering);
        var rows = gpu.Frame.AllocateConstants((ulong)(n * SurfaceInstance.Size), 16);
        MemoryMarshal.AsBytes(surfaceInstances.AsSpan(0, n)).CopyTo(new Span<byte>(rows.Pointer, n * SurfaceInstance.Size));
        var state = DrawState.For(t.Formats, gpu.DepthClamp, blend: Multiply, mask: DrawState.Rgb);
        state.Record(cmd, t);
        cmd.BindPipeline(surfacePipeline ??= gpu.Pipelines.Get(state.Pipeline(program, SurfaceRows, PrimitiveType.TriangleStrip, t.Formats, "blood surfaces")));
        frame.Bind(cmd, program, default);
        var push = new SurfacePush
        {
            Right = new Vector3(rotation.M11, rotation.M21, rotation.M31), TanX = tanX, Up = new Vector3(rotation.M12, rotation.M22, rotation.M32), TanY = tanY, Back = back,
            Depth = surfaceDepthIndex, Planes = new Vector2(near, far), TerrainDepth = terrainDepthIndex, StaticDepth = staticDepthIndex, Jitter = jitterNdc,
        };
        cmd.PushConstants(program, GpuStage.Vertex | GpuStage.Fragment, in push);
        Span<GpuBufferBinding> buffers = [new(rows.Handle, rows.Offset), new(rows.Handle, rows.Offset + 16), new(rows.Handle, rows.Offset + 32), new(rows.Handle, rows.Offset + 48)];
        cmd.BindVertexBuffers(0, buffers);
        cmd.Draw(4, (uint)n);
        cmd.EndRendering();
        cmd.Order();
        gpu.EndNative(cmd);
    }

    void DisposeSurfaces()
    {
        if (surfaceDepthIndex != uint.MaxValue) gpu.Bindless.Free(BindlessKind.Texture2D, surfaceDepthIndex);
        if (terrainDepthIndex != uint.MaxValue) gpu.Bindless.Free(BindlessKind.Texture2D, terrainDepthIndex);
        terrainDepth?.Dispose();
        staticDepth?.Dispose();
        if (staticDepthIndex != uint.MaxValue) gpu.Bindless.Free(BindlessKind.Texture2D, staticDepthIndex);
        if (surfaceProgram is { } program)
        {
            gpu.Pipelines.Forget(program);
            gpu.Shaders.Dispose(program);
        }
    }
}
