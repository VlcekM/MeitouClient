using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Ogre;
using Meitou.Data.Textures;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Shaders;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;

namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// One loose mesh in the world: its file, its size and where it is (rotation and translation, the mesh's middle at the translation); <paramref name="Stretch"/> lengthens
/// it along its own z (a bolt in flight).
/// </summary>
public readonly record struct DebrisPiece(string Mesh, float Scale, Quaternion Rotation, Vector3 Position, float Stretch = 1, LimbLook? Limb = null)
{
    /// <summary>A mesh named <c>file</c> + this + <c>texture</c> is drawn with that diffuse texture on every part (the birds' <c>HWBirds</c> material clone, docs/game/birds.md).</summary>
    public const char TextureMark = '?';
}

/// <summary>
/// How a severed limb is drawn (the original's <c>SeveredLimb</c> material, docs/game/blood-decals.md "On the body and on severed limbs"): the item's MATERIAL record's texture map and
/// skin-tone mask (bare file names), the skin tone (<c>severed_limb_fs</c>: albedo x (1 - tone x mask), 0 = untinted) and the race's blood colour. Equal diffuse maps share
/// a loaded mesh.
/// </summary>
public sealed record LimbLook(string Diffuse, string? Mask, Vector3 SkinTone, Vector3 BloodColour, string? Race = null, bool SkinDefault = true, string? Normal = null);

/// <summary>
/// Loose meshes thrown about by the host (luckyren Mode's bones and limbs, docs/game/luckyren.md): each a static mesh, centred on its bounds, textured with
/// its first resolved diffuse map and lit as the world's objects (<c>kenshiLight</c> under the game sky, the plain light otherwise, then the haze). The same
/// fragment runs on Vulkan and Metal: every atmosphere/shadow texture and the debris diffuse map come through the bindless table, so Metal's 16 direct
/// fragment samplers are no longer a constraint. Drawn in the scene pass before the characters (colour only, leaving the alpha to them); they
/// cast no shadow. A mesh is loaded the first time its <see cref="Extent"/> is asked for.
/// </summary>
public sealed unsafe class DebrisRenderer : IDisposable
{
    const string Vertex = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;
        layout(location = 3) in vec4 aColour;   // z: the curvature a limb's skin takes (CharacterContent.MarkCurvature)
        layout(location = 4) in vec4 aTangent;  // a limb's normal map (xyz, w the handedness: Model.Build tangentSign, the game's severed_limb_vs)
        uniform mat4 uViewProjection;   // from the camera-relative world
        uniform mat4 uModel;            // to the camera-relative world
        uniform vec3 uEye;
        out vec3 vWorld;
        out vec3 vNormal;
        out vec2 vUv;
        out vec3 vLocal;       // the mesh's own position and normal (a severed limb's blood mapping)
        out vec3 vObjNormal;
        out float vCurvature;
        out vec4 vTangent;
        out vec3 vColour;
        void main()
        {
            vec4 p = uModel * vec4(aPos, 1.0);
            vWorld = p.xyz + uEye;
            vNormal = mat3(uModel) * aNormal;
            vUv = aUv;
            vLocal = aPos;
            vObjNormal = aNormal;
            vCurvature = aColour.z;
            vColour = aColour.rgb;
            vTangent = vec4(mat3(uModel) * aTangent.xyz, aTangent.w);
            gl_Position = uViewProjection * p;
        }
        """;

    static readonly string Fragment = "#version 330 core\n" + AtmosphereShaders.Functions + """
        in vec3 vWorld;
        in vec3 vNormal;
        in vec2 vUv;
        in vec3 vLocal;
        in vec3 vObjNormal;
        in float vCurvature;
        in vec4 vTangent;
        in vec3 vColour;
        out vec4 fragColour;
        uniform sampler2D uDiffuse;
        uniform sampler2D uLimbMask;     // a severed limb (uTextured 2; bit 4 set: it has a normal map): its skin-tone mask (an index in the bits of uAlphaThreshold),
        uniform sampler2D uLimbNormal;   // its normal map (DXT5: x in alpha),
        uniform sampler2D uBlood;        // the blood splatter map,
        uniform sampler2D uSkinTable;    // the Meitou skin scattering table (SkinScattering; the push word 0: the skin is off),
        uniform uint uLimbSkin;          // its bindless index,
        uniform uint uLimbBlood;         // the blood colour as 8-bit RGB (above the low byte of uTextured)
        uniform uint uLimbTone;          // the cut character's skin tone as 8-bit RGB
        uniform int uTextured;           // 1: textured (+ 8: times the vertex colour), 2: a limb
        uniform float uAlphaThreshold;   // > 0: the diffuse alpha is a cut-out, and the gloss fixed
        uniform vec3 uEye;
        uniform vec3 uLightDir;
        uniform vec3 uSunColour;
        uniform vec3 uAmbientSky;
        uniform vec3 uAmbientGround;
        // The Meitou skin's sun term (CharacterShaders.skinDiffuse, docs/character-renderer.md "Skin"): the pre-integrated table at the surface's curvature, read per channel,
        // the light it adds over plain N.L in the skin's own saturated hue. A limb is all bare skin (share 1); the table is 64 x 64 (SkinScattering), 1 / radius in world units
        // times 0.3 (SkinScatterScale / (100 mm x MaxInverseRadiusMm)).
        vec3 skinDiffuse(vec3 n, vec3 geometric, vec3 l, float curvature, vec3 albedo)
        {
            float lambert = clamp(dot(n, l), 0.0, 1.0);
            vec3 nl = vec3(dot(normalize(mix(n, geometric, 0.6)), l), dot(normalize(mix(n, geometric, 0.3)), l), dot(n, l));
            vec3 u = (nl * 0.5 + 0.5) * 0.984375 + 0.0078125;
            float v = sqrt(clamp(curvature * 0.3, 0.0, 1.0)) * 0.984375 + 0.0078125;
            vec3 s = vec3(textureLod(uSkinTable, vec2(u.r, v), 0.0).r, textureLod(uSkinTable, vec2(u.g, v), 0.0).g, textureLod(uSkinTable, vec2(u.b, v), 0.0).b);
            float extra = max(max(s.r, max(s.g, s.b)) - lambert, 0.0);
            vec3 hue = albedo / max(max(albedo.r, max(albedo.g, albedo.b)), 1e-3);
            return vec3(lambert) + extra * hue * hue;
        }
        void main()
        {
            bool limb = (uTextured & 3) == 2;
            bool hasNormal = limb && (uTextured & 4) != 0;
            float cut = limb ? 0.0 : uAlphaThreshold;
            vec4 base = uTextured != 0 ? texture(uDiffuse, vUv) : vec4(0.78, 0.72, 0.62, 0.3);
            if (cut > 0.0 && base.a < cut) discard;
            if (!limb && (uTextured & 8) != 0) base.rgb *= vColour;
            float gloss = cut > 0.0 ? 0.3 : clamp(base.a, 0.0, 1.0);
            if (limb)
            {
                // character.hlsl severed_limb_fs: the skin tone through the mask (colour map red), the blood (bloody.png x the race's colour, the cut end, at negative x,
                // solid), then the body's darkening.
                vec3 tone = vec3(float((uLimbTone >> 16) & 255u), float((uLimbTone >> 8) & 255u), float(uLimbTone & 255u)) / 255.0;
                base.rgb *= 1.0 - tone * texture(uLimbMask, vUv).r;
                vec3 bloodColour = vec3(float((uLimbBlood >> 16) & 255u), float((uLimbBlood >> 8) & 255u), float(uLimbBlood & 255u)) / 255.0;
                float x = vLocal.x * 0.3;
                float turn = atan(vObjNormal.y, vObjNormal.z) / 3.14159265359 * 0.5;
                if (vObjNormal.z < 0.0) turn = turn > 0.0 ? turn - 0.5 : turn + 0.5;
                vec4 blood = texture(uBlood, vec2(x, turn)) * vec4(bloodColour, 1.0);
                blood.a += max(-x * 2.0, 0.0);
                base.rgb = mix(base.rgb, blood.rgb, min(blood.a, 1.0)) * 0.7;
            }
            vec3 n = normalize(vNormal);
            if (hasNormal)
            {
                // severed_limb_fs: the DXT5 normal map (x in alpha) on the frame (tangent, cross(tangent, normal) x w, normal).
                vec4 bump = texture(uLimbNormal, vUv);
                vec2 xy = vec2(bump.a, bump.g) * 2.0 - 1.0;
                vec3 t = normalize(vTangent.xyz), b = normalize(cross(vTangent.xyz, n)) * vTangent.w;
                n = normalize(t * xy.x + b * xy.y + n * sqrt(max(1.0 - dot(xy, xy), 0.0)));
            }
            if (!gl_FrontFacing) n = -n;
            vec3 v = normalize(uEye - vWorld);
            vec3 colour;
            if (uAtmoParams.x > 0.5) colour = limb && uLimbSkin != 0u ? kenshiLight(base.rgb, n, v, gloss, vWorld, skinDiffuse(n, n, uAtmoLight.xyz, vCurvature, base.rgb)) : kenshiLight(base.rgb, n, v, gloss, vWorld);
            else
            {
                vec3 ambient = mix(uAmbientGround, uAmbientSky, 0.5 + 0.5 * n.y);
                colour = base.rgb * (ambient + max(dot(n, normalize(uLightDir)), 0.0) * uSunColour);
            }
            fragColour = vec4(atmoApply(colour, uEye, vWorld), 1.0);
        }
        """;

    /// <summary>The native push block shared by the vertex and fragment stages (std430, 128 bytes): the model, the simple-sky fallback light, and the
    /// per-part bindless diffuse index and cut-out state.</summary>
    const string PushMembers = """
        mat4 model;
        vec3 sunColour;
        uint limbMask;
        vec3 ambientSky;
        uint limbBlood;
        vec3 ambientGround;
        uint diffuse;
        int textured;
        float alphaThreshold;
        uint limbSkin;
        uint limbTone;
        """;

    /// <summary>The uniforms whose native form is not in <see cref="NativeShaders.Map"/>: the model and the simple-sky fallback.</summary>
    static readonly Dictionary<string, string> NativeOwn = new()
    {
        ["uModel"] = "pc.model",
        ["uTextured"] = "(pc.textured & 255)",
        ["uSunColour"] = "pc.sunColour",
        ["uAmbientSky"] = "pc.ambientSky",
        ["uAmbientGround"] = "pc.ambientGround",
        ["uLimbMask"] = "textures2D[pc.limbMask]",
        ["uBlood"] = "textures2D[pc.limbBlood]",
        ["uLimbSkin"] = "pc.limbSkin",
        ["uSkinTable"] = "textures2D[pc.limbSkin]",
        ["uLimbBlood"] = "(uint(pc.textured) >> 8)",
        ["uLimbTone"] = "pc.limbTone",
        ["uLimbNormal"] = "textures2D[floatBitsToUint(pc.alphaThreshold)]",
    };

    /// <summary>A loaded diffuse texture and its stable bindless entry. The sampler can change with the upscaler's LOD bias; the index
    /// stays, so a draw recorded earlier in the frame keeps its view (the table's update-after-bind rules). Or a texture of a shared cache
    /// (<see cref="SharedTextures"/>: the characters' own image, kept while drawn), whose index is the cache's.</summary>
    sealed class DebrisTexture : IDisposable
    {
        readonly IGpuContext _gpu;
        readonly SampledImage? _image;
        readonly WorldTextureCache? _cache;
        readonly WorldTexture? _shared;
        BindlessHandle _handle;
        SampledTexture _registered;
        bool _hasEntry;

        public DebrisTexture(IGpuContext gpu, SampledImage image)
        {
            this._gpu = gpu;
            this._image = image;
        }

        public DebrisTexture(IGpuContext gpu, WorldTextureCache cache, WorldTexture shared)
        {
            this._gpu = gpu;
            this._cache = cache;
            this._shared = shared;
        }

        /// <summary>The bindless index for <see cref="SampledImage.Sampled"/> now, registering or updating the entry (a shared texture's: the
        /// cache's entry, the stand-in until it is resident; reading it counts as use).</summary>
        public uint Index
        {
            get
            {
                if (_image is null)
                {
                    CachedIndex = _cache!.Index(_shared!.Key, _gpu.LodBias, _gpu.StandIn2D);
                    return CachedIndex;
                }
                var sampled = _image.Sampled();
                if (!_hasEntry)
                {
                    _handle = new BindlessHandle(BindlessKind.Texture2D, _gpu.Bindless.Register(BindlessKind.Texture2D, sampled));
                    _hasEntry = true;
                }
                else if (!sampled.Equals(_registered))
                {
                    _gpu.Bindless.Update(_handle, sampled);
                }
                _registered = sampled;
                CachedIndex = _handle.Index;
                return CachedIndex;
            }
        }

        /// <summary>The index <see cref="Index"/> last registered; safe to use after the frame's bindless table has been bound.</summary>
        public uint CachedIndex { get; private set; }

        public void Dispose()
        {
            if (_hasEntry) _gpu.Bindless.Free(_handle);
            _image?.Dispose();
        }
    }

    /// <summary>A loaded mesh: one buffer of vertices and one of indices, a draw per part.</summary>
    sealed class Mesh(DeviceBuffer vertices, DeviceBuffer indices, (uint First, uint Count, int VertexOffset, DebrisTexture? Diffuse, float AlphaThreshold, bool Coloured)[] parts, Vector3 extent)
    {
        public readonly DeviceBuffer Vertices = vertices, Indices = indices;
        public readonly (uint First, uint Count, int VertexOffset, DebrisTexture? Diffuse, float AlphaThreshold, bool Coloured)[] Parts = parts;
        public readonly Vector3 Extent = extent;
        /// <summary>A severed limb's: its skin-tone mask, the mesh's bounds middle in its own space (the vertices are moved by it).</summary>
        public DebrisTexture? Mask, Normal;
        public bool Limb;
        public Vector3 Centre;
    }

    readonly IGpuContext _gpu;
    readonly MaterialResolver? _resolver;
    readonly Func<string, string?> _find;
    readonly NativeFrame _frame;
    readonly NativeProg _program;
    readonly VertexLayout _layout;
    readonly GpuBufferBinding[] _bindings = new GpuBufferBinding[5];
    readonly Dictionary<string, Mesh?> _meshes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DebrisTexture?> _textures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The cache whose images the debris textures share (the characters', which hold the same skins and items), when there is one by
    /// the time a texture is first asked for; else each is loaded here and kept.</summary>
    public Func<WorldTextureCache?>? SharedTextures { get; init; }

    /// <param name="resolver">Finds a mesh's textures (null: every mesh untextured).</param>
    /// <param name="find">A file name to its path in the install (the configured resource locations).</param>
    public DebrisRenderer(IGpuContext gpu, MaterialResolver? resolver, Func<string, string?> find)
    {
        this._gpu = gpu;
        this._resolver = resolver;
        this._find = find;
        _frame = new NativeFrame(gpu);
        string vertex = NativeShaders.Port(Vertex, NativeShaders.Map(NativeOwn), PushMembers);
        string fragment = Gi.GiResolveShaders.WithAlbedo(NativeShaders.Port(Fragment, NativeShaders.Map(NativeOwn), PushMembers));
        _program = new NativeProg(gpu, _frame, vertex, fragment, "debris");
        // The object layout (GpuVertex), its colour at 3 and tangent at 4.
        uint stride = GpuVertex.ObjectSize;
        _layout = _program.Layout(
        [
            new LegacyProgram.Attribute(default, InternalFormat.Rgb32F, stride, false),
            new LegacyProgram.Attribute(default, InternalFormat.Rgba16Snorm, stride, false),
            new LegacyProgram.Attribute(default, InternalFormat.Rg32F, stride, false),
            new LegacyProgram.Attribute(default, InternalFormat.Rgba16F, stride, false),
            new LegacyProgram.Attribute(default, InternalFormat.Rgba16Snorm, stride, false),
        ]);
        // The native model's bindless table is what keeps the full atmosphere inside Metal's direct-sampler budget.
        if (_program.P.Info.FragmentReflection is { } reflection)
            SamplerBudget.Require("debris", reflection.Samplers, NativeShaders.BindlessSet);
    }

    /// <summary>
    /// Half the size of <paramref name="mesh"/>'s bounds along its own axes, loading it (null when it is missing or unreadable). Textured as
    /// <paramref name="materialOf"/> when given (a mesh no record names, sharing another's material), else as itself; the first load decides.
    /// </summary>
    public Vector3? Extent(string mesh, string? materialOf = null, LimbLook? limb = null) => Load(mesh, materialOf, limb)?.Extent;

    /// <summary>The middle of <paramref name="mesh"/>'s bounds in its own space (where its origin lies from the middle, which is what a piece's position names); loads it like <see cref="Extent"/>.</summary>
    public Vector3? Centre(string mesh, LimbLook? limb = null) => Load(mesh, null, limb)?.Centre;

    DebrisTexture? _bloodMap;

    static uint Pack(Vector3 c) => (uint)(Math.Clamp((int)MathF.Round(c.X * 255), 0, 255) << 16 | Math.Clamp((int)MathF.Round(c.Y * 255), 0, 255) << 8 | Math.Clamp((int)MathF.Round(c.Z * 255), 0, 255));

    static string Key(string mesh, LimbLook? limb) => limb is null ? mesh : mesh + "|" + limb.Diffuse;

    /// <summary>
    /// Draws <paramref name="pieces"/> in the open scene pass (a guest): depth tested and written, back faces culled, colour only.
    /// <paramref name="sliceViewProjection"/> is the slice's (absolute world to clip), <paramref name="frustum"/> its planes.
    /// </summary>
    public void Draw(ReadOnlySpan<DebrisPiece> pieces, Matrix4x4 sliceViewProjection, Vector3 eye, Vector4[] frustum, in WorldLighting light, Func<LimbLook, uint>? skinTable = null)
    {
        if (pieces.IsEmpty) return;
        var cmd = _gpu.BeginGuest("debris");
        var targets = _gpu.CurrentTargets();
        var state = _gpu.CurrentState() with
        {
            Cull = CullMode.Back,
            Front = FrontFaceDirection.Ccw,
            DepthTest = true,
            DepthWrite = true,
            Blend = BlendState.Off,
            ColourMask = DrawState.Rgb,
        };
        cmd.SetViewport(targets.Viewport);
        cmd.SetScissor(targets.Scissor);
        cmd.SetRaster(state.Cull, state.Front);
        cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
        cmd.SetDepthBias(false, 0, 0);
        cmd.BindPipeline(_gpu.Pipelines.Get(state.Pipeline(_program.P, _layout, PrimitiveType.Triangles, targets.Formats, "debris")));

        // The bindless table is bound once per segment: register/update the debris textures before that bind. On Metal the
        // argument buffer must already name them when the encoder takes it.
        var standIn = _gpu.StandIn2D;
        // Pieces of one mesh come in runs (a flock of birds): the lookup is made once per run.
        string? runName = null;
        Mesh? runMesh = null;
        foreach (var piece in pieces)
        {
            if (piece.Limb is null && ReferenceEquals(piece.Mesh, runName)) continue;
            if (_meshes.TryGetValue(Key(piece.Mesh, piece.Limb), out var known) && known is not null)
            {
                if (piece.Limb is null) runName = piece.Mesh;
                foreach (var part in known.Parts) _ = part.Diffuse?.Index;
                if (known.Limb) { _ = known.Mask?.Index; _ = known.Normal?.Index; _ = (_bloodMap ??= Texture("bloody.png"))?.Index; if (piece.Limb is { } first) _ = skinTable?.Invoke(first); }
            }
        }

        // The view block carries the camera-relative projection/eye and the light direction; the frame block carries the atmosphere.
        var view = new ViewConstants
        {
            // The model matrices are camera-relative (as the legacy path set them), so the view projection is too.
            ViewProjection = Matrix4x4.CreateTranslation(eye) * sliceViewProjection,
            Eye = eye,
            LightDir = light.SunDirection,
            FogColour = light.FogColour,
            FogDistance = light.FogDistance,
        };
        _frame.Bind(cmd, _program.P, in view);

        var basePush = new DebrisPush
        {
            SunColour = light.SunColour,
            AmbientSky = light.AmbientSky,
            AmbientGround = light.AmbientGround,
        };
        (runName, runMesh) = (null, null);
        foreach (var piece in pieces)
        {
            // Only meshes loaded before (Extent): nothing is uploaded inside the pass.
            Mesh? mesh;
            if (piece.Limb is null && ReferenceEquals(piece.Mesh, runName) && runMesh is not null) mesh = runMesh;
            else
            {
                if (!_meshes.TryGetValue(Key(piece.Mesh, piece.Limb), out mesh) || mesh is null) continue;
                if (piece.Limb is null) (runName, runMesh) = (piece.Mesh, mesh);
            }
            // A limb's piece is at its mesh's origin (its blood mapping reads the mesh's own x); the others are centred on their bounds.
            var middle = mesh.Limb ? piece.Position + Vector3.Transform(mesh.Centre * piece.Scale, piece.Rotation) : piece.Position;
            if (!FrustumTests.SphereVisible(frustum, middle, mesh.Extent.Length() * piece.Scale * MathF.Max(piece.Stretch, 1))) continue;
            var m = Matrix4x4.CreateScale(piece.Scale, piece.Scale, piece.Scale * piece.Stretch) * Matrix4x4.CreateFromQuaternion(piece.Rotation) * Matrix4x4.CreateTranslation(piece.Position - eye);
            for (int k = 0; k < 5; k++) _bindings[k] = new GpuBufferBinding(mesh.Vertices.Handle, (ulong)(k switch { 0 => 0, 1 => 12, 2 => 20, 3 => 36, _ => 28 }));
            cmd.BindVertexBuffers(0, _bindings);
            cmd.BindIndexBuffer(new GpuBufferBinding(mesh.Indices.Handle, 0), IndexKind.U32);
            foreach (var part in mesh.Parts)
            {
                var push = basePush;
                push.Model = m;
                push.Diffuse = part.Diffuse is { } diffuse ? diffuse.CachedIndex : standIn;
                push.Textured = part.Diffuse is null ? 0 : part.Coloured ? 1 | 8 : 1;   // 8: the diffuse times the vertex colour (COLOURING)
                push.AlphaThreshold = part.AlphaThreshold;
                if (mesh.Limb && piece.Limb is { } look)
                {
                    // A severed limb (see the fragment): the diffuse map, the mask and the blood map, the colours packed, the mesh's centre x in place of the cut-out.
                    // Textured: the low byte 2 (+ 4 with a normal map), the blood colour above it; the normal map's index in the bits of the cut-out word.
                    push.Textured = (int)(2 | (mesh.Normal is not null ? 4u : 0) | Pack(look.BloodColour) << 8);
                    push.AlphaThreshold = BitConverter.UInt32BitsToSingle(mesh.Normal?.CachedIndex ?? standIn);
                    push.LimbMask = mesh.Mask?.CachedIndex ?? standIn;
                    push.LimbBlood = _bloodMap?.CachedIndex ?? standIn;
                    push.LimbSkin = skinTable?.Invoke(look) ?? 0;
                    push.LimbTone = mesh.Mask is null ? 0 : Pack(look.SkinTone);
                }
                cmd.PushConstants(_program.P, GpuStage.Vertex | GpuStage.Fragment, in push);
                cmd.DrawIndexed(part.Count, 1, part.First, part.VertexOffset);
            }
        }
        _gpu.EndGuest(cmd);
    }

    Mesh? Load(string file, string? materialOf, LimbLook? limb = null)
    {
        string key = Key(file, limb);
        if (_meshes.TryGetValue(key, out var known)) return known;
        Mesh? mesh = null;
        string? diffuse = null;
        if (file.IndexOf(DebrisPiece.TextureMark) is >= 0 and var mark) (file, diffuse) = (file[..mark], file[(mark + 1)..]);
        try
        {
            if (_find(Path.GetFileName(file.Replace('\\', '/'))) is { } path)
            {
                var ogre = OgreMeshReader.ReadFile(path);
                if (MeshWeld.Enabled) OgreMeshWeld.Apply(ogre);
                mesh = Build(file, materialOf ?? file, ogre, limb, diffuse);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
        {
            Console.Error.WriteLine($"debris mesh skipped: {file}: {e.Message}");
        }
        _meshes[key] = mesh;
        return mesh;
    }

    Mesh? Build(string file, string materialOf, OgreMesh ogre, LimbLook? limb, string? diffuse = null)
    {
        var built = Model.Build(ogre, null, tangentSign: limb is not null);   // a limb: the tangent's own w, the game's severed_limb_vs
        if (limb is not null) Characters.CharacterContent.MarkCurvature(built);   // a limb's skin scatters light by its curvature
        if (built.Parts.Count == 0) return null;
        var centre = built.Center;
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        var parts = new List<(uint, uint, int, DebrisTexture?, float, bool)>();
        foreach (var part in built.Parts)
        {
            var material = limb is null ? _resolver?.Candidates(materialOf, new OgreSubMesh { MaterialName = part.MaterialName }).FirstOrDefault() : null;
            float threshold = material is { Alpha: AlphaSource.DiffuseAlpha } ? MathF.Max(material.AlphaThreshold, 0.5f) : 0;
            parts.Add(((uint)indices.Count, (uint)part.Indices.Length, vertices.Count, Texture(diffuse ?? (limb is null ? material?.Diffuse : limb.Diffuse)), threshold,
                material?.VertexColours ?? false));
            foreach (var v in part.Vertices) vertices.Add(limb is null ? v with { Position = v.Position - centre } : v);   // a limb keeps the mesh's own space (its blood mapping reads x)
            indices.AddRange(part.Indices);
        }
        var packed = GpuVertex.PackObject(CollectionsMarshal.AsSpan(vertices));
        var vb = DeviceBuffer.Create(_gpu, (ulong)packed.Length, BufferUse.Vertex, "debris " + file);
        var ib = DeviceBuffer.Create(_gpu, (ulong)indices.Count * sizeof(uint), BufferUse.Index, "debris " + file);
        using (var batch = _gpu.Uploads.Begin())
        {
            batch.Write(vb, 0, packed);
            batch.Write(ib, 0, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(indices)));
        }
        var result = new Mesh(vb, ib, [.. parts], (built.Max - built.Min) / 2) { Centre = centre };
        if (limb is not null) (result.Limb, result.Mask, result.Normal) = (true, limb.Mask is null ? null : Texture(limb.Mask), limb.Normal is null ? null : Texture(limb.Normal));
        return result;
    }

    DebrisTexture? Texture(string? name)
    {
        if (name is null) return null;
        if (_textures.TryGetValue(name, out var known)) return known;
        DebrisTexture? texture = null;
        if (SharedTextures?.Invoke() is { } cache)
        {
            if (cache.Get(name, border: false) is { } t && t.State != WorldTexture.Residency.Missing) texture = new DebrisTexture(_gpu, cache, t);
            _textures[name] = texture;
            return texture;
        }
        try
        {
            if (_find(name) is { } path)
                texture = new DebrisTexture(_gpu, path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    ? SampledImage.Rgba8(_gpu, TextureLoader.LoadFile(path, false).Levels[0], repeat: true, mipmaps: true, "debris " + name)
                    : SampledImage.FromDds(_gpu, DdsReader.ReadFile(path), repeat: true, "debris " + name, bc3Blocks: true));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
        {
            Console.Error.WriteLine($"debris texture skipped: {name}: {e.Message}");
        }
        _textures[name] = texture;
        return texture;
    }

    public void Dispose()
    {
        foreach (var mesh in _meshes.Values)
        {
            mesh?.Vertices.Dispose();
            mesh?.Indices.Dispose();
        }
        foreach (var image in _textures.Values) image?.Dispose();
        _program.Dispose();
        _frame.Dispose();
    }
}

/// <summary>The C# side of <see cref="DebrisRenderer.PushMembers"/> (std430 push constants, 128 bytes): each vec3 starts on 16 bytes, a scalar may
/// fill the fourth word after one.</summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
struct DebrisPush
{
    [FieldOffset(0)] public Matrix4x4 Model;
    [FieldOffset(64)] public Vector3 SunColour;
    [FieldOffset(80)] public Vector3 AmbientSky;
    [FieldOffset(96)] public Vector3 AmbientGround;
    [FieldOffset(108)] public uint Diffuse;
    [FieldOffset(112)] public int Textured;
    [FieldOffset(116)] public float AlphaThreshold;
    [FieldOffset(76)] public uint LimbMask;
    [FieldOffset(92)] public uint LimbBlood;
    [FieldOffset(120)] public uint LimbSkin;
    [FieldOffset(124)] public uint LimbTone;
}