using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// A debug view of the navigation mesh (Meitou only, docs/game/pathfinding.md "Meitou debug view"): translucent polygons and their edges over the finished picture,
/// fainter where the scene hides them (sorted against the near slice's depth like <see cref="MarkerRenderer"/>). The host hands it the geometry with
/// <see cref="SetMesh"/>, camera-independent, relative to an origin; nothing is drawn until then or while <see cref="Visible"/> is false.
/// </summary>
public sealed unsafe class NavmeshOverlayRenderer : IDisposable
{
    /// <summary>Floats per vertex: position relative to the origin, then RGBA.</summary>
    public const int Stride = 7;

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

    const string Fragment = """
        #version 330 core
        in vec4 vColour;
        out vec4 fragColour;
        uniform sampler2D uSceneDepth;
        uniform vec2 uDepthScale;
        void main()
        {
            float scene = texelFetch(uSceneDepth, ivec2(gl_FragCoord.xy * uDepthScale), 0).r;
            float a = gl_FragCoord.z > scene ? vColour.a * 0.25 : vColour.a;
            fragColour = vec4(vColour.rgb * a, a);   // premultiplied
        }
        """;

    readonly IGpuContext gpu;
    readonly LegacyProgram program;
    readonly NativeSegment triangles, lines;
    readonly VertexArrayBindings vertexSource;
    readonly GpuBufferBinding[] bindings = new GpuBufferBinding[2];
    readonly UniformHandle viewProjection, offset, depthScale;
    readonly SamplerSlot sceneDepth;
    DeviceBuffer? fill, edges;
    int fillCount, edgeCount;
    Vector3 origin;

    /// <summary>Drawn while true (and a mesh was set).</summary>
    public bool Visible { get; set; }

    public NavmeshOverlayRenderer(IGpuContext gpu)
    {
        this.gpu = gpu;
        program = LegacyProgram.Create(gpu, Vertex, Fragment, "navmesh overlay");
        triangles = new NativeSegment(gpu, program, PrimitiveType.Triangles, "navmesh overlay fill");
        lines = new NativeSegment(gpu, program, PrimitiveType.Lines, "navmesh overlay edges");
        viewProjection = program.Uniform("uViewProjection");
        offset = program.Uniform("uOffset");
        depthScale = program.Uniform("uDepthScale");
        sceneDepth = program.Sampler("uSceneDepth");
        vertexSource = new VertexArrayBindings(
        [
            new LegacyProgram.Attribute(default, InternalFormat.Rgb32f, Stride * sizeof(float), false),
            new LegacyProgram.Attribute(default, InternalFormat.Rgba32f, Stride * sizeof(float), false),
        ], default);
    }

    /// <summary>The mesh to show: <paramref name="fillData"/> a triangle list and <paramref name="edgeData"/> a line list, <see cref="Stride"/> floats a vertex, positions relative to <paramref name="at"/>.</summary>
    public void SetMesh(Vector3 at, float[] fillData, float[] edgeData)
    {
        fill?.Dispose(); edges?.Dispose();
        (fill, fillCount) = Upload(fillData, "navmesh overlay fill");
        (edges, edgeCount) = Upload(edgeData, "navmesh overlay edges");
        origin = at;
    }

    /// <summary>Frees the geometry (the view was hidden: tens of MB over a town); nothing is drawn until the next <see cref="SetMesh"/>.</summary>
    public void Clear()
    {
        fill?.Dispose(); edges?.Dispose();
        (fill, fillCount, edges, edgeCount) = (null, 0, null, 0);
    }

    (DeviceBuffer?, int) Upload(float[] data, string name)
    {
        if (data.Length == 0) return (null, 0);
        var buffer = DeviceBuffer.Create(gpu, sizeof(float) * (ulong)data.Length, BufferUse.Vertex, name);
        using (var batch = gpu.Uploads.Begin()) batch.Write(buffer, 0, MemoryMarshal.AsBytes(data.AsSpan()));
        return (buffer, data.Length / Stride);
    }

    /// <summary>Draws over the finished <paramref name="picture"/>, each fragment sorted against the near slice's <paramref name="nearDepth"/> (<paramref name="renderWidth"/> × <paramref name="renderHeight"/> of it used).</summary>
    public void Record(GpuTexture picture, int width, int height, GpuTexture nearDepth, int renderWidth, int renderHeight, Matrix4x4 nearViewProjection, Vector3 eye)
    {
        if (!Visible || fillCount + edgeCount == 0 || width <= 0 || height <= 0) return;
        var cmd = gpu.BeginNative("navmesh overlay");
        var t = PassTargets.Of(picture, null).At(new PixelViewport(0, 0, width, height, 0, 1));
        cmd.BeginRendering(t.Rendering);
        var vp = Matrix4x4.CreateTranslation(eye) * nearViewProjection;
        program.Set(viewProjection, in vp);
        program.Set(offset, origin - eye);
        program.Set(depthScale, renderWidth / (float)width, renderHeight / (float)height);
        program.Bind(sceneDepth, new SampledTexture(gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0)), nearDepth.View()));
        var state = DrawState.For(t.Formats, gpu.DepthClamp, blend: new BlendState(true, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha),
            depthClamp: gpu.DepthClamp);
        state.Record(cmd, t);
        Draw(cmd, state, t, triangles, fill, fillCount);
        Draw(cmd, state, t, lines, edges, edgeCount);
        cmd.EndRendering();
        gpu.EndNative(cmd);
    }

    void Draw(IGpuCommandList cmd, DrawState state, PassTargets t, NativeSegment segment, DeviceBuffer? buffer, int count)
    {
        if (buffer is null || count == 0) return;
        cmd.BindPipeline(segment.Get(state, t.Formats, vertexSource));
        bindings[0] = new GpuBufferBinding(buffer.Handle, 0);
        bindings[1] = new GpuBufferBinding(buffer.Handle, 3 * sizeof(float));
        cmd.BindVertexBuffers(0, bindings);
        program.Flush(cmd);
        cmd.Draw((uint)count, 1, 0, 0);
    }

    public void Dispose()
    {
        fill?.Dispose();
        edges?.Dispose();
    }
}
