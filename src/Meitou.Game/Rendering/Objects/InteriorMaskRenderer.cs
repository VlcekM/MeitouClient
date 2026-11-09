using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Ogre;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Sky;

namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// The interior cut (docs/render-interior-cut.md): the <c>interior mask</c> meshes of the buildings near the eye drawn into a depth range of the main
/// view, nearest front face (R) and farthest back face (A) as view depth + 1, which the terrain, the foliage and the map features read to leave out
/// their fragments inside a building (the game's <c>InteriorMask</c> compositor node and <c>rt_interiormask</c>). Drawn before the scene, at the
/// scene's size, with its camera.
/// </summary>
public sealed unsafe class InteriorMaskRenderer : IDisposable
{
    #region Setup

    /// <summary>
    /// The clear of the front channel: no front face here (half float's largest is 65504).
    /// </summary>
    public const float NoFront = 65000;

    /// <summary>
    /// How far from the eye (X/Z, to the building's footprint) masks are drawn.
    /// </summary>
    const float Range = 3000;

    /// <summary>
    /// The vertex stage: a mask mesh's position through <c>uMvp</c>.
    /// </summary>
    const string Vertex = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        uniform mat4 uMvp;
        void main() { gl_Position = uMvp * vec4(aPos, 1.0); }
        """;

    /// <summary>
    /// Every face writes its view depth + 1 (the game's value, interiormask.hlsl) to R (min blend: the nearest) and A (max blend: the farthest). The
    /// game splits the faces by facing instead; for a closed mesh seen from outside the nearest face is a front face and the farthest a back face,
    /// the same. With the eye inside the building's hull (<c>uInside</c>) every face it sees is a back face: the game's R stays 0, so here R = 0.
    /// </summary>
    const string Fragment = """
        #version 330 core
        out vec4 fragColour;
        uniform int uInside;
        void main()
        {
            float w = 1.0 / gl_FragCoord.w + 1.0;
            fragColour = vec4(uInside != 0 ? 0.0 : w, 0.0, 0.0, w);
        }
        """;

    /// <summary>
    /// The context the target, meshes and draws go through.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// The objects renderer, which lists the loaded buildings' masks near the eye and finds the mask mesh files.
    /// </summary>
    readonly WorldObjectRenderer _objects;

    /// <summary>
    /// The mask program (GL source, recorded natively through <see cref="_segment"/>).
    /// </summary>
    readonly LegacyProgram _program;

    /// <summary>
    /// The native segment the mask draws are recorded in.
    /// </summary>
    readonly NativeSegment _segment;

    /// <summary>
    /// The program's <c>uMvp</c> and <c>uInside</c> uniforms.
    /// </summary>
    readonly UniformHandle _mvp, _inside;

    /// <summary>
    /// The mask meshes' vertex input: one position stream, 12 bytes a vertex.
    /// </summary>
    readonly VertexArrayBindings _vertexSource;

    /// <summary>
    /// This frame's buildings with masks near the eye.
    /// </summary>
    readonly List<InteriorVolume> _near = [];

    /// <summary>
    /// This frame's drawn masks' X/Z boxes (<see cref="Boxes"/>).
    /// </summary>
    readonly List<(Vector2 Min, Vector2 Max)> _boxes = [];

    /// <summary>
    /// The building of each box in <see cref="_boxes"/> (its placement id).
    /// </summary>
    readonly List<string> _boxOwners = [];

    /// <summary>
    /// The buildings whose masks this frame drew (<see cref="Drew"/>).
    /// </summary>
    readonly HashSet<string> _drawnOwners = new(StringComparer.Ordinal);

    /// <summary>
    /// The target (RGBA16F, the scene's size), kept while the size holds.
    /// </summary>
    GpuTexture? _target;

    /// <summary>
    /// The target's bindless entry, made when first read and freed when the target is remade.
    /// </summary>
    (GpuTexture Texture, uint Index)? _bindless;

    /// <summary>
    /// Makes the mask program, its segment and its vertex input.
    /// </summary>
    internal InteriorMaskRenderer(IGpuContext gpu, WorldObjectRenderer objects)
    {
        this._gpu = gpu;
        this._objects = objects;
        _program = LegacyProgram.Create(gpu, Vertex, Fragment, "interior mask");
        _segment = new NativeSegment(gpu, _program, PrimitiveType.Triangles, "interior mask");
        _mvp = _program.Uniform("uMvp");
        _inside = _program.Uniform("uInside");
        _vertexSource = new VertexArrayBindings([new LegacyProgram.Attribute(default, InternalFormat.Rgb32F, 12, false)], default);
    }

    #endregion

    #region Public state

    /// <summary>
    /// Whether the last <see cref="Record"/> drew a mask: only then do <see cref="Index"/> and <see cref="Boxes"/> apply.
    /// </summary>
    public bool Active { get; private set; }

    /// <summary>
    /// The depth range's bindless 2D index (valid while <see cref="Active"/>).
    /// </summary>
    public uint Index => _bindless?.Index ?? 0;

    /// <summary>
    /// The drawn buildings' mask meshes' X/Z bounds: terrain patches outside all of them need no cut.
    /// </summary>
    public IReadOnlyList<(Vector2 Min, Vector2 Max)> Boxes => _boxes;

    /// <summary>
    /// The meshes drawn by the last <see cref="Record"/>.
    /// </summary>
    public int Drawn { get; private set; }

    /// <summary>
    /// Whether the X/Z rectangle [<paramref name="min"/>, <paramref name="max"/>] overlaps a drawn mask's box (false while not <see cref="Active"/>).
    /// </summary>
    public bool Overlaps(Vector2 min, Vector2 max)
    {
        if (!Active) return false;
        foreach (var (lo, hi) in _boxes)
            if (min.X <= hi.X && max.X >= lo.X && min.Y <= hi.Y && max.Y >= lo.Y) return true;
        return false;
    }

    /// <summary>
    /// Whether a sphere's X/Z square overlaps a drawn mask's box.
    /// </summary>
    public bool Overlaps(Vector3 centre, float radius) =>
        Overlaps(new Vector2(centre.X - radius, centre.Z - radius), new Vector2(centre.X + radius, centre.Z + radius));

    /// <summary>
    /// Whether a sphere's X/Z square overlaps the drawn mask box of a building in <paramref name="buildings"/> (by placement id).
    /// </summary>
    public bool Overlaps(Vector3 centre, float radius, IReadOnlyDictionary<string, int> buildings)
    {
        if (!Active) return false;
        for (int i = 0; i < _boxes.Count; i++)
        {
            var (lo, hi) = _boxes[i];
            if (centre.X - radius <= hi.X && centre.X + radius >= lo.X
                && centre.Z - radius <= hi.Y && centre.Z + radius >= lo.Y && buildings.ContainsKey(_boxOwners[i])) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether the last <see cref="Record"/> drew a mask of the building <paramref name="placementId"/>.
    /// </summary>
    public bool Drew(string placementId) => _drawnOwners.Contains(placementId);

    #endregion

    #region Cut

    /// <summary>
    /// Turns the cut on in <paramref name="view"/> (programs made with <see cref="WithCut"/> on <see cref="ViewConstants"/>) when
    /// <paramref name="cut"/> drew a mask.
    /// </summary>
    internal static void Apply(ref ViewConstants view, InteriorMaskRenderer? cut)
    {
        if (cut is not { Active: true }) return;
        view.InteriorMask = cut.Index;
        view.InteriorCut = 1;
    }

    /// <summary>
    /// The cut a fragment program runs first (docs/render-interior-cut.md): while <c>uInteriorCut</c>, a fragment whose view depth lies between the
    /// nearest and the farthest interior-mask face at its pixel (depth + 1) is left out, as the game's shaders <c>clip</c> against
    /// <c>rt_interiormask</c>.
    /// </summary>
    const string Cut = """
            if (uInteriorCut)
            {
                vec4 interiorRange = texelFetch(uInteriorMask, ivec2(gl_FragCoord.xy), 0);
                float interiorDepth = 1.0 / gl_FragCoord.w;
                if (interiorDepth < interiorRange.a && interiorDepth > interiorRange.r) discard;
            }
        """;

    /// <summary>
    /// <paramref name="fragment"/> (a legacy GLSL text) with <see cref="Cut"/> at the top of its <c>main</c> and its two uniforms declared, for
    /// <see cref="NativeShaders.Port"/> to map (<c>uInteriorMask</c> a 2D texture, <c>uInteriorCut</c> a bool expression).
    /// </summary>
    internal static string WithCut(string fragment)
    {
        var main = System.Text.RegularExpressions.Regex.Match(fragment, @"void\s+main\s*\(\s*\)\s*\{",
            System.Text.RegularExpressions.RegexOptions.RightToLeft);
        if (!main.Success) throw new InvalidOperationException("InteriorMaskRenderer.WithCut: no main() in the fragment text.");
        return fragment.Insert(main.Index + main.Length, "\n" + Cut)
            .Insert(main.Index, "uniform sampler2D uInteriorMask;\nuniform bool uInteriorCut;\n");
    }

    #endregion

    #region Recording

    /// <summary>
    /// Draws the masks of the buildings near <paramref name="eye"/> with the main view's <paramref name="viewProjection"/> into a target of the
    /// scene's size.
    /// </summary>
    public void Record(Matrix4x4 viewProjection, Vector3 eye, int width, int height)
    {
        Active = false;
        Drawn = 0;
        _boxes.Clear();
        _boxOwners.Clear();
        _drawnOwners.Clear();
        if (width <= 0 || height <= 0) return;
        _objects.InteriorMasks(eye, Range, _near);
        if (_near.Count == 0) return;
        var target = EnsureTarget(width, height);
        var cmd = _gpu.BeginNative("interior mask");
        var viewport = new PixelViewport(0, 0, width, height, 0, 1);
        var t = PassTargets.Of(target, null).At(viewport);
        t = t with { Colour = t.Colour with { Load = LoadOp.Clear, Clear = new ClearColour(NoFront, 0f, 0f, 0f) } };
        cmd.BeginRendering(t.Rendering);

        // Min on the colour (the nearest face), max on the alpha (the farthest); every face drawn, no depth test (the game's target has no depth).
        var blend = new BlendState(true, BlendingFactor.One, BlendingFactor.One, BlendOperation.Min, BlendOperation.Max);
        var state = DrawState.For(t.Formats, _gpu.DepthClamp, blend: blend, mask: ColorMask.R | ColorMask.A);
        state.Record(cmd, t);
        cmd.BindPipeline(_segment.Get(state, t.Formats, _vertexSource));

        // camera-relative placement keeps the precision far from the origin
        var relative = Matrix4x4.CreateTranslation(eye) * viewProjection;
        foreach (var v in _near) DrawVolume(cmd, v, eye, relative);
        cmd.EndRendering();
        cmd.Order();
        _gpu.EndNative(cmd);
        if (Drawn == 0) return;
        RegisterBindless(target);
        Active = true;
    }

    /// <summary>
    /// The target of the scene's size: kept while the size holds, remade (and its bindless index freed) when it does not.
    /// </summary>
    GpuTexture EnsureTarget(int width, int height)
    {
        if (_target is { } kept && kept.Desc.Width == width && kept.Desc.Height == height) return kept;

        if (_bindless is { } old) _gpu.Bindless.Free(BindlessKind.Texture2D, old.Index);
        _bindless = null;
        _target?.Dispose();
        _target = _gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba16F, width, height, Use: TextureUse.ColourTarget | TextureUse.Sampled,
            Name: "interior mask"));
        return _target;
    }

    /// <summary>
    /// Draws the mask meshes of one building volume (placed by <paramref name="relative"/>) and keeps the X/Z box they cover.
    /// </summary>
    void DrawVolume(IGpuCommandList cmd, InteriorVolume v, Vector3 eye, Matrix4x4 relative)
    {
        var node = v.Node;
        node.Translation -= eye;
        var m = node * relative;
        bool any = false, eyeInside = v.Contains(eye);
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var path in v.MaskMeshes)
        {
            if (Mesh(path) is not { Vertices: { } buffer } mesh) continue;
            _program.Set(_mvp, in m);
            _program.Set(_inside, eyeInside ? 1 : 0);
            _program.Flush(cmd);
            cmd.BindVertexBuffers(0, [new GpuBufferBinding(buffer.Handle, 0)]);
            cmd.Draw((uint)mesh.Count);
            Drawn++;
            any = true;
            for (int c = 0; c < 8; c++)
            {
                var corner = Vector3.Transform(new Vector3((c & 1) == 0 ? mesh.Min.X : mesh.Max.X, (c & 2) == 0 ? mesh.Min.Y : mesh.Max.Y,
                    (c & 4) == 0 ? mesh.Min.Z : mesh.Max.Z), v.Node);
                min = Vector2.Min(min, new Vector2(corner.X, corner.Z));
                max = Vector2.Max(max, new Vector2(corner.X, corner.Z));
            }
        }

        if (!any) return;
        _boxes.Add((min, max));
        _boxOwners.Add(v.PlacementId);
        _drawnOwners.Add(v.PlacementId);
    }

    /// <summary>
    /// Registers the drawn target as a bindless texture, once per target (the fragment programs read the cut through it).
    /// </summary>
    void RegisterBindless(GpuTexture target)
    {
        if (_bindless is not { } known || known.Texture != target)
            _bindless = (target, _gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(Nearest(), target.View())));
    }

    #endregion

    #region Meshes and sampler

    /// <summary>
    /// A building's interior mask mesh, loaded once from the record's file (no vertices when not found or unreadable).
    /// </summary>
    sealed class MaskMesh
    {
        /// <summary>
        /// The positions (three floats a vertex; null: none).
        /// </summary>
        public DeviceBuffer? Vertices;

        /// <summary>
        /// The vertices to draw.
        /// </summary>
        public int Count;

        /// <summary>
        /// The bounds of the positions, in building space.
        /// </summary>
        public Vector3 Min, Max;
    }

    /// <summary>
    /// The mask meshes by the record's path (case-insensitive).
    /// </summary>
    readonly Dictionary<string, MaskMesh> _meshes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A mask mesh's triangles (positions, unindexed) on the GPU, read the first time it is asked for; null when it cannot be read.
    /// </summary>
    MaskMesh? Mesh(string recordPath)
    {
        if (_meshes.TryGetValue(recordPath, out var known)) return known;
        var mesh = new MaskMesh();
        _meshes[recordPath] = mesh;
        try
        {
            if (_objects.FindRecordFile(recordPath) is not { } path) return mesh;
            var ogre = OgreMeshReader.ReadFile(path);
            var data = new List<float>();
            foreach (var sub in ogre.SubMeshes)
            {
                if (sub.Operation != OgreOperationType.TriangleList
                    || (sub.VertexData ?? ogre.SharedVertexData) is not { } v
                    || v.ReadPositions() is not { } positions) continue;
                var indices = sub.Indices.Indices;
                for (int i = 0; i + 2 < indices.Length; i += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        var p = positions[indices[i + k]];
                        (mesh.Min, mesh.Max) = data.Count == 0 ? (p, p) : (Vector3.Min(mesh.Min, p), Vector3.Max(mesh.Max, p));
                        data.AddRange([p.X, p.Y, p.Z]);
                    }
            }
            if (data.Count == 0) return mesh;
            mesh.Vertices = DeviceBuffer.Create(_gpu, sizeof(float) * (ulong)data.Count, BufferUse.Vertex, "interior mask mesh");
            using (var batch = _gpu.Uploads.Begin()) batch.Write(mesh.Vertices, 0, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(data)));
            mesh.Count = data.Count / 3;
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"interior mask {recordPath} skipped: {e.Message}");
        }
        return mesh;
    }

    /// <summary>
    /// The nearest-filtering, edge-clamped sampler the target is read with.
    /// </summary>
    GpuSampler Nearest() => _gpu.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Nearest, TextureMagFilter.Nearest));

    #endregion

    /// <summary>
    /// Frees the target's bindless entry, the target and the mask meshes.
    /// </summary>
    public void Dispose()
    {
        if (_bindless is { } b) _gpu.Bindless.Free(BindlessKind.Texture2D, b.Index);
        _target?.Dispose();
        foreach (var m in _meshes.Values) m.Vertices?.Dispose();
    }
}