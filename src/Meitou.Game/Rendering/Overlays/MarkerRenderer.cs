using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Ogre;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;

using Meitou.Game.Rendering.Sky;

namespace Meitou.Game.Rendering.Overlays;

/// <summary>
/// The game's <c>debug_marker</c> entities, in the material's two passes: the visible part lit as <c>Basic_Lit_VP</c> lights it (contrast 0.5)
/// and alpha blended, then the part behind the scene in the pass's own colour. Two kinds: the movement marker (docs/game/ui-input.md "Movement
/// marker"), <c>position.mesh</c> scaled 8 at <see cref="At"/>, nothing drawn at <see cref="Strength"/> 0; and the stealth arrows
/// (docs/game/perception.md "Spotting a sneaker"), one <c>arrow.mesh</c> per entry of <see cref="Arrows"/>. The game interface sets both.
/// </summary>
public sealed unsafe class MarkerRenderer : IDisposable
{
    /// <summary>
    /// The movement marker's scale (<c>position.mesh</c> scaled 8) and the lighting's contrast (as <c>Basic_Lit_VP</c> lights it, 0.5).
    /// </summary>
    const float MarkerScale = 8, Contrast = 0.5f;

    /// <summary>
    /// The colours of the material's two passes: the visible part (lit, alpha blended) and the part behind the scene.
    /// </summary>
    static readonly Vector4 Visible = new(0.612f, 0.694f, 0.529f, 1), Hidden = new(0.306f, 0.347f, 0.2645f, 0.6f);
    /// <summary>
    /// The alpha of an arrow's part behind the scene (the stealth call's second pass, <c>FUN_1409b7830</c>).
    /// </summary>
    const float ArrowHiddenAlpha = 0.2f;

    /// <summary>
    /// A stealth arrow: its tail at <see cref="At"/>, pointing along the unit <see cref="Direction"/>, in <see cref="Colour"/>.
    /// </summary>
    public readonly record struct Arrow(Vector3 At, Vector3 Direction, Vector3 Colour);

    /// <summary>
    /// The vertex shader: the mesh turned and placed relative to the eye, lit by the sun at <see cref="Contrast"/>.
    /// </summary>
    const string Vertex = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        uniform mat4 uViewProjection;   // world-from-camera-relative included: the offset is relative to the eye
        uniform mat4 uRotation;
        uniform vec3 uOffset;
        uniform vec3 uSun;
        uniform vec4 uColour;
        out vec4 vColour;
        void main()
        {
            gl_Position = uViewProjection * vec4(uOffset + (uRotation * vec4(aPos, 0.0)).xyz, 1.0);
            vec3 normal = normalize((uRotation * vec4(aNormal, 0.0)).xyz);
            vColour = vec4(uColour.rgb * (clamp(dot(normal, uSun), 0.0, 1.0) * 0.5 + 0.5), uColour.a);
        }
        """;

    /// <summary>
    /// The fragment shader: the scene's depth decides which pass a fragment belongs to (the game's depth tests, against the scene only);
    /// premultiplied, over a clear target.
    /// </summary>
    const string Fragment = """
        #version 330 core
        in vec4 vColour;
        out vec4 fragColour;
        uniform sampler2D uSceneDepth;
        uniform vec2 uDepthScale;
        uniform int uHidden;
        """ + DepthConvention.Glsl + """
        void main()
        {
            float scene = texelFetch(uSceneDepth, ivec2(gl_FragCoord.xy * uDepthScale), 0).r;
            bool behind = depthNearerThan(scene, gl_FragCoord.z);
            if (behind != (uHidden != 0)) discard;
            fragColour = vec4(vColour.rgb * vColour.a, vColour.a);   // premultiplied, over a clear target
        }
        """;

    /// <summary>
    /// The composite's vertex shader: the resolved markers over the picture (a full-screen triangle).
    /// </summary>
    const string CompositeVertex = """
        #version 330 core
        void main() { gl_Position = vec4(vec2((gl_VertexID << 1) & 2, gl_VertexID & 2) * 2.0 - 1.0, 0.0, 1.0); }
        """;

    /// <summary>
    /// The composite's fragment shader: the resolved markers' texel at the pixel.
    /// </summary>
    const string CompositeFragment = """
        #version 330 core
        out vec4 fragColour;
        uniform sampler2D uMarker;
        void main() { fragColour = texelFetch(uMarker, ivec2(gl_FragCoord.xy), 0); }
        """;

    /// <summary>
    /// Samples per pixel of the markers (anti-aliasing: the meshes have many triangles under a pixel; at most what the device allows). 4, not 8: 8
    /// took 127 MB at 1080p.
    /// </summary>
    const int WantedSamples = 4;

    /// <summary>
    /// The device the markers are drawn on.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// The markers' program (<see cref="Vertex"/>, <see cref="Fragment"/>).
    /// </summary>
    readonly LegacyProgram _program;

    /// <summary>
    /// The markers' pipelines.
    /// </summary>
    readonly NativeSegment _segment;

    /// <summary>
    /// The meshes' vertices: position and normal.
    /// </summary>
    readonly DeviceBuffer _vertices;

    /// <summary>
    /// The vertex layout: position and normal, three floats each, 24 bytes a vertex.
    /// </summary>
    readonly VertexArrayBindings _vertexSource;

    /// <summary>
    /// The vertex buffer at the position's and the normal's offset, bound for the draws.
    /// </summary>
    readonly GpuBufferBinding[] _bindings = new GpuBufferBinding[2];

    /// <summary>
    /// The markers program's uniforms: the view-projection, the mesh's turn and offset, the sun, the colour, the scene depth's scale and the pass.
    /// </summary>
    readonly UniformHandle _viewProjection, _rotation, _offset, _sun, _colour, _depthScale, _hiddenPass;

    /// <summary>
    /// The markers program's scene depth slot.
    /// </summary>
    readonly SamplerSlot _sceneDepth;

    /// <summary>
    /// The composite program (<see cref="CompositeVertex"/>, <see cref="CompositeFragment"/>).
    /// </summary>
    readonly LegacyProgram _composite;

    /// <summary>
    /// The composite's pipelines.
    /// </summary>
    readonly NativeSegment _compositeSegment;

    /// <summary>
    /// The composite program's marker picture slot.
    /// </summary>
    readonly SamplerSlot _markerImage;

    /// <summary>
    /// The markers drawn multisampled with a depth of their own, at the picture's size, and resolved (the colour itself without multisampling).
    /// </summary>
    GpuTexture? _msColour, _msDepth, _resolved;

    /// <summary>
    /// The samples per pixel the markers are drawn with: <see cref="WantedSamples"/>, at most what the device allows.
    /// </summary>
    readonly int _samples;
    /// <summary>
    /// Milliseconds without a marker before the targets are freed (about 70 MB multisampled at 1080p; markers show only for a while after an order).
    /// </summary>
    const long IdleReleaseMs = 10_000;

    /// <summary>
    /// When the frames without a marker began (<see cref="Environment.TickCount64"/>; 0: a marker was drawn last).
    /// </summary>
    long _idleSince;

    /// <summary>
    /// The vertex ranges of the marker, arrow and rotation ring meshes in the buffer (count 0: the mesh is missing).
    /// </summary>
    readonly (int First, int Count) _marker, _arrow, _ring;

    /// <summary>
    /// Premultiplied alpha blending: the colour over the picture by one minus its alpha.
    /// </summary>
    static readonly BlendState Premultiplied = new(true, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);

    /// <summary>
    /// The sneak arrow's node scale in its own frame, before the turn: half as long (its X) and 0.35 as thick, about 5 units long with a head of
    /// ±0.42 (<c>FUN_1405dc7d0</c> <c>setScale(0.5, 0.35, 0.35)</c>; docs/game/perception.md).
    /// </summary>
    static readonly Matrix4x4 ArrowScale = Matrix4x4.CreateScale(0.5f, 0.35f, 0.35f);

    /// <summary>
    /// Where the movement marker stands (the ground point of the move order).
    /// </summary>
    public Vector3 At { get; set; }
    /// <summary>
    /// 1 when the order is given, down to 0 (gone).
    /// </summary>
    public float Strength { get; set; }
    /// <summary>
    /// This frame's stealth arrows, replaced whole each frame (empty: none; ignored without <c>arrow.mesh</c>).
    /// </summary>
    public Arrow[] Arrows { get; set; } = [];
    /// <summary>
    /// Whether the rotation marker (<c>rotate.mesh</c>, docs/game/ui-input.md) shows while the camera turns: flat, 1 unit over the pivot of the
    /// camera the frame is drawn with, scaled max(3, 0.06 x its distance), so it keeps with the view (a followed character moves the pivot every
    /// frame, between the rig's ticks).
    /// </summary>
    public bool Rotation { get; set; }

    /// <summary>
    /// Makes the programs and pipelines, uploads the meshes' vertices and picks the sample count.
    /// </summary>
    MarkerRenderer(IGpuContext gpu, float[] data, (int, int) marker, (int, int) arrow, (int, int) ring)
    {
        this._gpu = gpu;
        this._marker = marker;
        this._arrow = arrow;
        this._ring = ring;
        _program = LegacyProgram.Create(gpu, Vertex, Fragment, "markers");
        _segment = new NativeSegment(gpu, _program, PrimitiveType.Triangles, "markers");
        _viewProjection = _program.Uniform("uViewProjection");
        _rotation = _program.Uniform("uRotation");
        _offset = _program.Uniform("uOffset");
        _sun = _program.Uniform("uSun");
        _colour = _program.Uniform("uColour");
        _depthScale = _program.Uniform("uDepthScale");
        _hiddenPass = _program.Uniform("uHidden");
        _sceneDepth = _program.Sampler("uSceneDepth");
        _composite = LegacyProgram.Create(gpu, CompositeVertex, CompositeFragment, "markers composite");
        _compositeSegment = new NativeSegment(gpu, _composite, PrimitiveType.Triangles, "markers composite");
        _markerImage = _composite.Sampler("uMarker");
        _samples = 1;
        if (gpu is GpuContext vk)
        {
            var l = vk.Device.Limits;
            uint both = (uint)(l.FramebufferColorSampleCounts & l.FramebufferDepthSampleCounts);
            while (_samples < WantedSamples && (both & (uint)(_samples * 2)) != 0) _samples *= 2;
        }
        _vertices = DeviceBuffer.Create(gpu, sizeof(float) * (ulong)data.Length, BufferUse.Vertex, "markers");
        using (var batch = gpu.Uploads.Begin()) batch.Write(_vertices, 0, MemoryMarshal.AsBytes(data.AsSpan()));
        _vertexSource = new VertexArrayBindings(
        [
            new LegacyProgram.Attribute(default, InternalFormat.Rgb32F, 24, false),
            new LegacyProgram.Attribute(default, InternalFormat.Rgb32F, 24, false),
        ], default);
    }

    /// <summary>
    /// The markers from <c>position.mesh</c> and <c>arrow.mesh</c> (null when neither is readable; a missing one is not drawn).
    /// </summary>
    public static MarkerRenderer? Create(IGpuContext gpu, Func<string, string?> find)
    {
        var data = new List<float>();
        var marker = Append(data, find, "position.mesh", MarkerScale);
        var arrow = Append(data, find, "arrow.mesh", 1);
        var ring = Append(data, find, "rotate.mesh", 1);
        return data.Count == 0 ? null : new MarkerRenderer(gpu, [.. data], marker, arrow, ring);
    }

    /// <summary>
    /// Appends a mesh's triangles (position and normal, scaled) to <paramref name="data"/>: its vertex range there.
    /// </summary>
    static (int First, int Count) Append(List<float> data, Func<string, string?> find, string name, float scale)
    {
        int first = data.Count / 6;
        if (find(name) is not { } path) return (first, 0);
        try
        {
            var mesh = OgreMeshReader.ReadFile(path);
            foreach (var sub in mesh.SubMeshes)
            {
                if (sub.Operation != OgreOperationType.TriangleList || (sub.VertexData ?? mesh.SharedVertexData) is not { } v
                    || v.ReadPositions() is not { } positions)
                {
                    continue;
                }
                float[]? normals = v.Find(OgreVertexSemantic.Normal) is { } n ? v.ReadFloats(n) : null;
                var indices = sub.Indices.Indices;
                for (int t = 0; t + 2 < indices.Length; t += 3)
                {
                    // Without normals in the file, the face's own.
                    var face = Vector3.Normalize(Vector3.Cross(positions[indices[t + 1]] - positions[indices[t]],
                        positions[indices[t + 2]] - positions[indices[t]]));
                    for (int k = 0; k < 3; k++)
                    {
                        uint i = indices[t + k];
                        var p = positions[i] * scale;
                        var normal = normals is null ? face : Vector3.Normalize(new Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]));
                        data.AddRange([p.X, p.Y, p.Z, normal.X, normal.Y, normal.Z]);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"{name} skipped: {e.Message}");
            data.RemoveRange(first * 6, data.Count - first * 6);
        }
        return (first, data.Count / 6 - first);
    }

    /// <summary>
    /// Whether anything is to be drawn this frame.
    /// </summary>
    public bool Active => Strength > 0 && _marker.Count > 0 || Arrows.Length > 0 && _arrow.Count > 0 || Rotation && _ring.Count > 0;

    /// <summary>
    /// Draws over the finished <paramref name="picture"/> (after the tone map, so the colours come out as the material gives them, whatever the
    /// exposure): the visible parts, then the parts behind the scene, each fragment sorted by the scene's <paramref name="sceneDepthTexture"/>
    /// (<paramref name="renderWidth"/> × <paramref name="renderHeight"/> of it used). The markers are drawn multisampled into targets of their own
    /// (their own depth hides their far sides), resolved, and blended over the picture. Depth is clamped, so a marker beyond the far plane is drawn
    /// whole.
    /// <paramref name="camera"/> is the view the scene was drawn from.
    /// </summary>
    public void Record(GpuTexture picture, int width, int height, GpuTexture sceneDepthTexture, int renderWidth, int renderHeight,
        Matrix4x4 sceneViewProjection, WorldCamera camera, Vector3 sunDirection)
    {
        if (!Active || width <= 0 || height <= 0) return;
        _idleSince = 0;
        EnsureTargets(width, height);
        var arrows = Arrows;
        var cmd = _gpu.BeginNative("markers");
        var viewport = new PixelViewport(0, 0, width, height, 0, 1);
        var t = PassTargets.Of(_msColour, _msDepth).At(viewport);
        t = t with
        {
            Colour = t.Colour with { Load = LoadOp.Clear, Clear = new ClearColour(0f, 0f, 0f, 0f) },
            Depth = t.Depth with { Load = LoadOp.Clear, DepthClear = DepthConvention.Clear },
        };
        cmd.BeginRendering(t.Rendering);
        var vp = sceneViewProjection;   // eye-relative (RenderOrigin)
        _program.Set(_viewProjection, in vp);
        _program.Set(_sun, sunDirection);
        _program.Set(_depthScale, renderWidth / (float)width, renderHeight / (float)height);
        _program.Bind(_sceneDepth, new SampledTexture(Nearest(), sceneDepthTexture.View()));
        for (int pass = 0; pass < 2; pass++) RecordPass(cmd, t, camera, arrows, hidden: pass == 1);
        cmd.EndRendering();
        cmd.Order();
        if (_samples > 1)
        {
            cmd.Resolve(_msColour, _resolved);
            cmd.Order();
        }
        Composite(cmd, picture, viewport);
        _gpu.EndNative(cmd);
    }

    /// <summary>
    /// The multisampled targets and the resolved picture at <paramref name="width"/> × <paramref name="height"/>, made again when the size changes.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_msColour), nameof(_msDepth), nameof(_resolved))]
    void EnsureTargets(int width, int height)
    {
        if (_msColour is not null && _msDepth is not null && _resolved is not null &&
            _msColour.Desc.Width == width && _msColour.Desc.Height == height) return;
        ReleaseTargets();
        var sampled = _samples > 1 ? TextureUse.TransferSrc : TextureUse.Sampled;
        _msColour = _gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, width, height, Samples: _samples, Use: TextureUse.ColourTarget | sampled,
            Name: "markers colour"));
        _msDepth = _gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, width, height, Samples: _samples, Use: TextureUse.DepthTarget,
            Name: "markers depth"));
        _resolved = _samples > 1 ? _gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, width, height,
            Use: TextureUse.Sampled | TextureUse.TransferDst, Name: "markers resolved")) : _msColour;
    }

    /// <summary>
    /// One of the two passes: the parts in front of the scene, or (<paramref name="hidden"/>) those behind it.
    /// </summary>
    void RecordPass(IGpuCommandList cmd, PassTargets t, WorldCamera camera, Arrow[] arrows, bool hidden)
    {
        var eye = camera.Eye;
        var state = DrawState.For(t.Formats, _gpu.DepthClamp, cull: CullMode.Back, depthTest: true, depthWrite: !hidden,
            compare: DepthConvention.SceneCompare, blend: Premultiplied, depthClamp: _gpu.DepthClamp, front: FrontFaceDirection.Ccw);
        _program.Set(_hiddenPass, hidden ? 1 : 0);
        state.Record(cmd, t);
        cmd.BindPipeline(_segment.Get(state, t.Formats, _vertexSource));
        _bindings[0] = new GpuBufferBinding(_vertices.Handle, 0);
        _bindings[1] = new GpuBufferBinding(_vertices.Handle, 12);
        cmd.BindVertexBuffers(0, _bindings);
        if (Strength > 0 && _marker.Count > 0)
        {
            var c = hidden ? Hidden : Visible;
            Draw(cmd, _marker, At - eye, Matrix4x4.Identity, new Vector4(c.X, c.Y, c.Z, c.W * Strength));
        }

        // The rotation marker's colour has alpha 0: only its opaque visible pass shows. It rides the drawn pivot, not the rig's last tick
        // (FUN_1406b0f90's marker is a child of the camera's pivot node).
        if (!hidden && Rotation && _ring.Count > 0)
            Draw(cmd, _ring, camera.Target + Vector3.UnitY - eye, Matrix4x4.CreateScale(MathF.Max(3, camera.Distance * 0.06f)), Visible);
        if (_arrow.Count > 0)
        {
            foreach (var a in arrows)
                Draw(cmd, _arrow, a.At - eye, ArrowScale * PointAlong(a.Direction), new Vector4(a.Colour, hidden ? ArrowHiddenAlpha : 1));
        }
    }

    /// <summary>
    /// The resolved markers blended over <paramref name="picture"/>.
    /// </summary>
    void Composite(IGpuCommandList cmd, GpuTexture picture, PixelViewport viewport)
    {
        var over = PassTargets.Of(picture, null).At(viewport);
        cmd.BeginRendering(over.Rendering);
        _composite.Bind(_markerImage, new SampledTexture(Nearest(), _resolved!.View()));
        var overState = DrawState.For(over.Formats, _gpu.DepthClamp, blend: Premultiplied);
        overState.Record(cmd, over);
        cmd.BindPipeline(_compositeSegment.Get(overState, over.Formats, null));
        _composite.Flush(cmd);
        cmd.Draw(3);
        cmd.EndRendering();
    }

    /// <summary>
    /// Draws <paramref name="mesh"/> turned by <paramref name="turn"/> at <paramref name="relative"/> to the eye, in colour <paramref name="c"/>.
    /// </summary>
    void Draw(IGpuCommandList cmd, (int First, int Count) mesh, Vector3 relative, Matrix4x4 turn, Vector4 c)
    {
        _program.Set(_offset, relative);
        _program.Set(_rotation, in turn);
        _program.Set(_colour, c);
        _program.Flush(cmd);
        cmd.Draw((uint)mesh.Count, 1, (uint)mesh.First, 0);
    }

    /// <summary>
    /// The turn taking the arrow mesh's −X (its tip) onto <paramref name="direction"/> (Ogre's <c>getRotationTo</c>; opposite: half a turn about Y).
    /// </summary>
    static Matrix4x4 PointAlong(Vector3 direction)
    {
        var from = -Vector3.UnitX;
        float dot = Vector3.Dot(from, direction);
        if (dot >= 1 - 1e-6f) return Matrix4x4.Identity;
        if (dot <= -1 + 1e-6f) return Matrix4x4.CreateRotationY(MathF.PI);
        return Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new Quaternion(Vector3.Cross(from, direction), 1 + dot)));
    }

    /// <summary>
    /// The nearest-filtered, edge-clamped sampler the composite reads the markers with.
    /// </summary>
    GpuSampler Nearest() => _gpu.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Nearest, TextureMagFilter.Nearest));

    /// <summary>
    /// A frame that draws no marker: the targets are freed once none has been drawn for <see cref="IdleReleaseMs"/>.
    /// </summary>
    public void Idle()
    {
        if (_msColour is null) return;
        long now = Environment.TickCount64;
        if (_idleSince == 0) _idleSince = now;
        else if (now - _idleSince >= IdleReleaseMs) ReleaseTargets();
    }

    /// <summary>
    /// Frees the multisampled targets and the resolved picture.
    /// </summary>
    void ReleaseTargets()
    {
        if (_resolved != _msColour) _resolved?.Dispose();
        _msColour?.Dispose();
        _msDepth?.Dispose();
        _msColour = _msDepth = _resolved = null;
        _idleSince = 0;
    }

    /// <summary>
    /// Releases the vertices and the targets.
    /// </summary>
    public void Dispose()
    {
        _vertices.Dispose();
        if (_resolved != _msColour) _resolved?.Dispose();
        _msColour?.Dispose();
        _msDepth?.Dispose();
    }
}