using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Sky;

namespace Meitou.Game.Rendering.Overlays;

/// <summary>
/// A debug view of the navigation mesh (Meitou only, docs/game/pathfinding.md "Meitou debug view"): translucent polygons and their edges over the
/// finished picture, fainter where the scene hides them (sorted against the scene's depth like <see cref="MarkerRenderer"/>). The host hands it the
/// geometry with <see cref="SetMesh"/>, camera-independent, relative to an origin; nothing is drawn until then or while <see cref="Visible"/> is
/// false.
/// </summary>
public sealed unsafe class NavmeshOverlayRenderer : IDisposable
{
    /// <summary>
    /// Floats per vertex: position relative to the origin, then RGBA.
    /// </summary>
    public const int Stride = 7;

    /// <summary>
    /// The vertex shader: the vertex placed relative to the eye, with its colour.
    /// </summary>
    const string Vertex = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec4 aColour;
        uniform mat4 uViewProjection;   // world-from-camera-relative included: the offset is relative to the eye
        uniform vec3 uOffset;
        out vec4 vColour;
        void main()
        {
            gl_Position = uViewProjection * vec4(uOffset + aPos, 1.0);
            vColour = aColour;
        }
        """;

    /// <summary>
    /// The fragment shader: the colour premultiplied, a quarter as strong where the scene's depth hides it.
    /// </summary>
    const string Fragment = """
        #version 330 core
        in vec4 vColour;
        out vec4 fragColour;
        uniform sampler2D uSceneDepth;
        uniform vec2 uDepthScale;
        """ + DepthConvention.Glsl + """
        void main()
        {
            float scene = texelFetch(uSceneDepth, ivec2(gl_FragCoord.xy * uDepthScale), 0).r;
            float a = depthNearerThan(scene, gl_FragCoord.z) ? vColour.a * 0.25 : vColour.a;
            fragColour = vec4(vColour.rgb * a, a);   // premultiplied
        }
        """;

    /// <summary>
    /// The device the overlay is drawn on.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// The overlay's program (<see cref="Vertex"/>, <see cref="Fragment"/>).
    /// </summary>
    readonly LegacyProgram _program;

    /// <summary>
    /// The pipelines of the polygons (triangles) and of their edges (lines).
    /// </summary>
    readonly NativeSegment _triangles, _lines;

    /// <summary>
    /// The vertex layout: position and RGBA, <see cref="Stride"/> floats a vertex.
    /// </summary>
    readonly VertexArrayBindings _vertexSource;

    /// <summary>
    /// The vertex buffer at the position's and the colour's offset, bound for the draws.
    /// </summary>
    readonly GpuBufferBinding[] _bindings = new GpuBufferBinding[2];

    /// <summary>
    /// The program's uniforms: the view-projection, the origin's offset from the eye and the scene depth's scale.
    /// </summary>
    readonly UniformHandle _viewProjection, _offset, _depthScale;

    /// <summary>
    /// The program's scene depth slot.
    /// </summary>
    readonly SamplerSlot _sceneDepth;

    /// <summary>
    /// The polygons' and the edges' vertices (null: none).
    /// </summary>
    DeviceBuffer? _fill, _edges;

    /// <summary>
    /// The vertex counts of the polygons and of the edges.
    /// </summary>
    int _fillCount, _edgeCount;

    /// <summary>
    /// The world point the mesh's positions are relative to.
    /// </summary>
    Vector3 _origin;

    /// <summary>
    /// Drawn while true (and a mesh was set).
    /// </summary>
    public bool Visible { get; set; }

    /// <summary>
    /// Makes the program, its two pipelines and the vertex layout; the geometry comes with <see cref="SetMesh"/>.
    /// </summary>
    public NavmeshOverlayRenderer(IGpuContext gpu)
    {
        this._gpu = gpu;
        _program = LegacyProgram.Create(gpu, Vertex, Fragment, "navmesh overlay");
        _triangles = new NativeSegment(gpu, _program, PrimitiveType.Triangles, "navmesh overlay fill");
        _lines = new NativeSegment(gpu, _program, PrimitiveType.Lines, "navmesh overlay edges");
        _viewProjection = _program.Uniform("uViewProjection");
        _offset = _program.Uniform("uOffset");
        _depthScale = _program.Uniform("uDepthScale");
        _sceneDepth = _program.Sampler("uSceneDepth");
        _vertexSource = new VertexArrayBindings(
        [
            new LegacyProgram.Attribute(default, InternalFormat.Rgb32F, Stride * sizeof(float), false),
            new LegacyProgram.Attribute(default, InternalFormat.Rgba32F, Stride * sizeof(float), false),
        ], default);
    }

    /// <summary>
    /// The mesh to show: <paramref name="fillData"/> a triangle list and <paramref name="edgeData"/> a line list, <see cref="Stride"/> floats a
    /// vertex, positions relative to <paramref name="at"/>.
    /// </summary>
    public void SetMesh(Vector3 at, float[] fillData, float[] edgeData)
    {
        _fill?.Dispose(); _edges?.Dispose();
        (_fill, _fillCount) = Upload(fillData, "navmesh overlay fill");
        (_edges, _edgeCount) = Upload(edgeData, "navmesh overlay edges");
        _origin = at;
    }

    /// <summary>
    /// Frees the geometry (the view was hidden: tens of MB over a town); nothing is drawn until the next <see cref="SetMesh"/>.
    /// </summary>
    public void Clear()
    {
        _fill?.Dispose(); _edges?.Dispose();
        (_fill, _fillCount, _edges, _edgeCount) = (null, 0, null, 0);
    }

    /// <summary>
    /// <paramref name="data"/> uploaded into a new vertex buffer named <paramref name="name"/>, with its vertex count; null and 0 when empty.
    /// </summary>
    (DeviceBuffer?, int) Upload(float[] data, string name)
    {
        if (data.Length == 0) return (null, 0);
        var buffer = DeviceBuffer.Create(_gpu, sizeof(float) * (ulong)data.Length, BufferUse.Vertex, name);
        using (var batch = _gpu.Uploads.Begin()) batch.Write(buffer, 0, MemoryMarshal.AsBytes(data.AsSpan()));
        return (buffer, data.Length / Stride);
    }

    /// <summary>
    /// Draws over the finished <paramref name="picture"/>, each fragment sorted against the scene's <paramref name="sceneDepthTexture"/>
    /// (<paramref name="renderWidth"/> × <paramref name="renderHeight"/> of it used; <paramref name="sceneViewProjection"/> eye-relative
    /// (RenderOrigin), in <see cref="DepthConvention"/>).
    /// </summary>
    public void Record(GpuTexture picture, int width, int height, GpuTexture sceneDepthTexture, int renderWidth, int renderHeight,
        Matrix4x4 sceneViewProjection, Vector3 eye)
    {
        if (!Visible || _fillCount + _edgeCount == 0 || width <= 0 || height <= 0) return;
        var cmd = _gpu.BeginNative("navmesh overlay");
        var t = PassTargets.Of(picture, null).At(new PixelViewport(0, 0, width, height, 0, 1));
        cmd.BeginRendering(t.Rendering);
        var vp = sceneViewProjection;
        _program.Set(_viewProjection, in vp);
        _program.Set(_offset, _origin - eye);
        _program.Set(_depthScale, renderWidth / (float)width, renderHeight / (float)height);
        _program.Bind(_sceneDepth,
            new SampledTexture(_gpu.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Nearest, TextureMagFilter.Nearest)), sceneDepthTexture.View()));
        var state = DrawState.For(t.Formats, _gpu.DepthClamp, blend: new BlendState(true, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha),
            depthClamp: _gpu.DepthClamp);
        state.Record(cmd, t);
        Draw(cmd, state, t, _triangles, _fill, _fillCount);
        Draw(cmd, state, t, _lines, _edges, _edgeCount);
        cmd.EndRendering();
        _gpu.EndNative(cmd);
    }

    /// <summary>
    /// Draws <paramref name="count"/> vertices of <paramref name="buffer"/> with <paramref name="segment"/>'s pipeline; nothing when there are none.
    /// </summary>
    void Draw(IGpuCommandList cmd, DrawState state, PassTargets t, NativeSegment segment, DeviceBuffer? buffer, int count)
    {
        if (buffer is null || count == 0) return;
        cmd.BindPipeline(segment.Get(state, t.Formats, _vertexSource));
        _bindings[0] = new GpuBufferBinding(buffer.Handle, 0);
        _bindings[1] = new GpuBufferBinding(buffer.Handle, 3 * sizeof(float));
        cmd.BindVertexBuffers(0, _bindings);
        _program.Flush(cmd);
        cmd.Draw((uint)count, 1, 0, 0);
    }

    /// <summary>
    /// Frees the geometry.
    /// </summary>
    public void Dispose()
    {
        _fill?.Dispose();
        _edges?.Dispose();
    }
}