using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Ogre;
using Meitou.Rendering.Gpu;

using Meitou.Rendering.Gpu.Core;

namespace Meitou.Rendering;

/// <summary>
/// The game's <c>debug_marker</c> entities, in the material's two passes: the visible part lit as <c>Basic_Lit_VP</c> lights it (contrast 0.5)
/// and alpha blended, then the part behind the scene in the pass's own colour. Two kinds: the movement marker (docs/game/ui-input.md "Movement
/// marker"), <c>position.mesh</c> scaled 8 at <see cref="At"/>, nothing drawn at <see cref="Strength"/> 0; and the stealth arrows
/// (docs/game/perception.md "Spotting a sneaker"), one <c>arrow.mesh</c> per entry of <see cref="Arrows"/>. The game interface sets both.
/// </summary>
public sealed unsafe class MarkerRenderer : IDisposable
{
    const float MarkerScale = 8, Contrast = 0.5f;
    static readonly Vector4 Visible = new(0.612f, 0.694f, 0.529f, 1), Hidden = new(0.306f, 0.347f, 0.2645f, 0.6f);
    /// <summary>The alpha of an arrow's part behind the scene (the stealth call's second pass, <c>FUN_1409b7830</c>).</summary>
    const float ArrowHiddenAlpha = 0.2f;

    /// <summary>A stealth arrow: its tail at <see cref="At"/>, pointing along the unit <see cref="Direction"/>, in <see cref="Colour"/>.</summary>
    public readonly record struct Arrow(Vector3 At, Vector3 Direction, Vector3 Colour);

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

    // The scene's near-slice depth decides which pass a fragment belongs to (the game's depth tests, against the scene only).
    const string Fragment = """
        #version 330 core
        in vec4 vColour;
        out vec4 fragColour;
        uniform sampler2D uSceneDepth;
        uniform vec2 uDepthScale;
        uniform int uHidden;
        void main()
        {
            float scene = texelFetch(uSceneDepth, ivec2(gl_FragCoord.xy * uDepthScale), 0).r;
            if ((gl_FragCoord.z > scene) != (uHidden != 0)) discard;
            fragColour = vec4(vColour.rgb * vColour.a, vColour.a);   // premultiplied, over a clear target
        }
        """;

    // The resolved markers over the picture (a full-screen triangle).
    const string CompositeVertex = """
        #version 330 core
        void main() { gl_Position = vec4(vec2((gl_VertexID << 1) & 2, gl_VertexID & 2) * 2.0 - 1.0, 0.0, 1.0); }
        """;

    const string CompositeFragment = """
        #version 330 core
        out vec4 fragColour;
        uniform sampler2D uMarker;
        void main() { fragColour = texelFetch(uMarker, ivec2(gl_FragCoord.xy), 0); }
        """;

    /// <summary>Samples per pixel of the markers (anti-aliasing: the meshes have many triangles under a pixel; at most what the device allows). 4, not 8: 8 took 127 MB at 1080p.</summary>
    const int WantedSamples = 4;

    readonly IGpuContext gpu;
    readonly LegacyProgram program;
    readonly NativeSegment segment;
    readonly DeviceBuffer vertices;
    readonly VertexArrayBindings vertexSource;
    readonly GpuBufferBinding[] bindings = new GpuBufferBinding[2];
    readonly UniformHandle viewProjection, rotation, offset, sun, colour, depthScale, hiddenPass;
    readonly SamplerSlot sceneDepth;
    readonly LegacyProgram composite;
    readonly NativeSegment compositeSegment;
    readonly SamplerSlot markerImage;
    // The markers drawn multisampled with a depth of their own, at the picture's size, and resolved.
    GpuTexture? msColour, msDepth, resolved;
    int samples;
    /// <summary>Milliseconds without a marker before the targets are freed (about 70 MB multisampled at 1080p; markers show only for a while after an order).</summary>
    const long IdleReleaseMs = 10_000;
    long idleSince;
    // The vertex ranges of the two meshes in the buffer (count 0: the mesh is missing).
    readonly (int First, int Count) marker, arrow, ring;

    /// <summary>Where the movement marker stands (the ground point of the move order).</summary>
    public Vector3 At { get; set; }
    /// <summary>1 when the order is given, down to 0 (gone).</summary>
    public float Strength { get; set; }
    /// <summary>This frame's stealth arrows, replaced whole each frame (empty: none; ignored without <c>arrow.mesh</c>).</summary>
    public Arrow[] Arrows { get; set; } = [];
    /// <summary>Where the rotation marker (<c>rotate.mesh</c>, docs/game/ui-input.md) lies flat while the camera turns; null: not shown.</summary>
    public Vector3? RotationAt { get; set; }
    /// <summary>The rotation marker's scale (max(3, 0.06 x the camera's distance)).</summary>
    public float RotationScale { get; set; } = 3;

    MarkerRenderer(IGpuContext gpu, float[] data, (int, int) marker, (int, int) arrow, (int, int) ring)
    {
        this.gpu = gpu;
        this.marker = marker;
        this.arrow = arrow;
        this.ring = ring;
        program = LegacyProgram.Create(gpu, Vertex, Fragment, "markers");
        segment = new NativeSegment(gpu, program, PrimitiveType.Triangles, "markers");
        viewProjection = program.Uniform("uViewProjection");
        rotation = program.Uniform("uRotation");
        offset = program.Uniform("uOffset");
        sun = program.Uniform("uSun");
        colour = program.Uniform("uColour");
        depthScale = program.Uniform("uDepthScale");
        hiddenPass = program.Uniform("uHidden");
        sceneDepth = program.Sampler("uSceneDepth");
        composite = LegacyProgram.Create(gpu, CompositeVertex, CompositeFragment, "markers composite");
        compositeSegment = new NativeSegment(gpu, composite, PrimitiveType.Triangles, "markers composite");
        markerImage = composite.Sampler("uMarker");
        samples = 1;
        if (gpu is GpuContext vk)
        {
            var l = vk.Device.Limits;
            uint both = (uint)(l.FramebufferColorSampleCounts & l.FramebufferDepthSampleCounts);
            while (samples < WantedSamples && (both & (uint)(samples * 2)) != 0) samples *= 2;
        }
        vertices = DeviceBuffer.Create(gpu, sizeof(float) * (ulong)data.Length, BufferUse.Vertex, "markers");
        using (var batch = gpu.Uploads.Begin()) batch.Write(vertices, 0, MemoryMarshal.AsBytes(data.AsSpan()));
        vertexSource = new VertexArrayBindings(
        [
            new LegacyProgram.Attribute(default, InternalFormat.Rgb32f, 24, false),
            new LegacyProgram.Attribute(default, InternalFormat.Rgb32f, 24, false),
        ], default);
    }

    /// <summary>The markers from <c>position.mesh</c> and <c>arrow.mesh</c> (null when neither is readable; a missing one is not drawn).</summary>
    public static MarkerRenderer? Create(IGpuContext gpu, Func<string, string?> find)
    {
        var data = new List<float>();
        var marker = Append(data, find, "position.mesh", MarkerScale);
        var arrow = Append(data, find, "arrow.mesh", 1);
        var ring = Append(data, find, "rotate.mesh", 1);
        return data.Count == 0 ? null : new MarkerRenderer(gpu, [.. data], marker, arrow, ring);
    }

    /// <summary>Appends a mesh's triangles (position and normal, scaled) to <paramref name="data"/>: its vertex range there.</summary>
    static (int First, int Count) Append(List<float> data, Func<string, string?> find, string name, float scale)
    {
        int first = data.Count / 6;
        if (find(name) is not { } path) return (first, 0);
        try
        {
            var mesh = OgreMeshReader.ReadFile(path);
            foreach (var sub in mesh.SubMeshes)
            {
                if (sub.Operation != OgreOperationType.TriangleList || (sub.VertexData ?? mesh.SharedVertexData) is not { } v || v.ReadPositions() is not { } positions) continue;
                float[]? normals = v.Find(OgreVertexSemantic.Normal) is { } n ? v.ReadFloats(n) : null;
                var indices = sub.Indices.Indices;
                for (int t = 0; t + 2 < indices.Length; t += 3)
                {
                    // Without normals in the file, the face's own.
                    var face = Vector3.Normalize(Vector3.Cross(positions[indices[t + 1]] - positions[indices[t]], positions[indices[t + 2]] - positions[indices[t]]));
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

    /// <summary>Whether anything is to be drawn this frame.</summary>
    public bool Active => Strength > 0 && marker.Count > 0 || Arrows.Length > 0 && arrow.Count > 0 || RotationAt is not null && ring.Count > 0;

    /// <summary>
    /// Draws over the finished <paramref name="picture"/> (after the tone map, so the colours come out as the material gives them, whatever the
    /// exposure): the visible parts, then the parts behind the scene, each fragment sorted by the near slice's <paramref name="nearDepth"/>
    /// (<paramref name="renderWidth"/> × <paramref name="renderHeight"/> of it used). The markers are drawn multisampled into targets of their own
    /// (their own depth hides their far sides), resolved, and blended over the picture. Depth is clamped, so a marker beyond the near slice is drawn whole.
    /// </summary>
    public void Record(GpuTexture picture, int width, int height, GpuTexture nearDepth, int renderWidth, int renderHeight, Matrix4x4 nearViewProjection, Vector3 eye, Vector3 sunDirection)
    {
        if (!Active || width <= 0 || height <= 0) return;
        idleSince = 0;
        if (msColour is null || msColour.Desc.Width != width || msColour.Desc.Height != height)
        {
            ReleaseTargets();
            var sampled = samples > 1 ? TextureUse.TransferSrc : TextureUse.Sampled;
            msColour = gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, width, height, Samples: samples, Use: TextureUse.ColourTarget | sampled, Name: "markers colour"));
            msDepth = gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, width, height, Samples: samples, Use: TextureUse.DepthTarget, Name: "markers depth"));
            resolved = samples > 1 ? gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, width, height, Use: TextureUse.Sampled | TextureUse.TransferDst, Name: "markers resolved")) : msColour;
        }
        var arrows = Arrows;
        var cmd = gpu.BeginNative("markers");
        var viewport = new PixelViewport(0, 0, width, height, 0, 1);
        var t = PassTargets.Of(msColour, msDepth).At(viewport);
        t = t with
        {
            Colour = t.Colour with { Load = LoadOp.Clear, Clear = new ClearColour(0f, 0f, 0f, 0f) },
            Depth = t.Depth with { Load = LoadOp.Clear, DepthClear = 1 },
        };
        cmd.BeginRendering(t.Rendering);
        var vp = Matrix4x4.CreateTranslation(eye) * nearViewProjection;
        program.Set(viewProjection, in vp);
        program.Set(sun, sunDirection);
        program.Set(depthScale, renderWidth / (float)width, renderHeight / (float)height);
        program.Bind(sceneDepth, new SampledTexture(Nearest(), nearDepth.View()));
        var premultiplied = new BlendState(true, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        for (int pass = 0; pass < 2; pass++)
        {
            bool hidden = pass == 1;
            var state = DrawState.For(t.Formats, gpu.DepthClamp, cull: CullMode.Back, depthTest: true, depthWrite: !hidden,
                compare: DepthFunction.Lequal, blend: premultiplied, depthClamp: gpu.DepthClamp, front: FrontFaceDirection.Ccw);
            program.Set(hiddenPass, hidden ? 1 : 0);
            state.Record(cmd, t);
            cmd.BindPipeline(segment.Get(state, t.Formats, vertexSource));
            bindings[0] = new GpuBufferBinding(vertices.Handle, 0);
            bindings[1] = new GpuBufferBinding(vertices.Handle, 12);
            cmd.BindVertexBuffers(0, bindings);
            if (Strength > 0 && marker.Count > 0)
            {
                var c = hidden ? Hidden : Visible;
                Draw(cmd, marker, At - eye, Matrix4x4.Identity, new Vector4(c.X, c.Y, c.Z, c.W * Strength));
            }
            // The rotation marker's colour has alpha 0: only its opaque visible pass shows.
            if (!hidden && RotationAt is { } pivot && ring.Count > 0)
                Draw(cmd, ring, pivot - eye, Matrix4x4.CreateScale(RotationScale), Visible);
            if (arrow.Count > 0)
                foreach (var a in arrows)
                    Draw(cmd, arrow, a.At - eye, ArrowScale * PointAlong(a.Direction), new Vector4(a.Colour, hidden ? ArrowHiddenAlpha : 1));
        }
        cmd.EndRendering();
        cmd.Order();
        if (samples > 1)
        {
            cmd.Resolve(msColour, resolved!);
            cmd.Order();
        }
        var over = PassTargets.Of(picture, null).At(viewport);
        cmd.BeginRendering(over.Rendering);
        composite.Bind(markerImage, new SampledTexture(Nearest(), resolved!.View()));
        var overState = DrawState.For(over.Formats, gpu.DepthClamp, blend: premultiplied);
        overState.Record(cmd, over);
        cmd.BindPipeline(compositeSegment.Get(overState, over.Formats, null));
        composite.Flush(cmd);
        cmd.Draw(3);
        cmd.EndRendering();
        gpu.EndNative(cmd);
    }

    void Draw(IGpuCommandList cmd, (int First, int Count) mesh, Vector3 relative, Matrix4x4 turn, Vector4 c)
    {
        program.Set(offset, relative);
        program.Set(rotation, in turn);
        program.Set(colour, c);
        program.Flush(cmd);
        cmd.Draw((uint)mesh.Count, 1, (uint)mesh.First, 0);
    }

    /// <summary>
    /// The sneak arrow's node scale in its own frame, before the turn: half as long (its X) and 0.35 as thick, about 5 units long with a head of ±0.42
    /// (<c>FUN_1405dc7d0</c> <c>setScale(0.5, 0.35, 0.35)</c>; docs/game/perception.md).
    /// </summary>
    static readonly Matrix4x4 ArrowScale = Matrix4x4.CreateScale(0.5f, 0.35f, 0.35f);

    /// <summary>The turn taking the arrow mesh's −X (its tip) onto <paramref name="direction"/> (Ogre's <c>getRotationTo</c>; opposite: half a turn about Y).</summary>
    static Matrix4x4 PointAlong(Vector3 direction)
    {
        var from = -Vector3.UnitX;
        float dot = Vector3.Dot(from, direction);
        if (dot >= 1 - 1e-6f) return Matrix4x4.Identity;
        if (dot <= -1 + 1e-6f) return Matrix4x4.CreateRotationY(MathF.PI);
        return Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new Quaternion(Vector3.Cross(from, direction), 1 + dot)));
    }

    GpuSampler Nearest() => gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge,
        TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));

    /// <summary>A frame that draws no marker: the targets are freed once none has been drawn for <see cref="IdleReleaseMs"/>.</summary>
    public void Idle()
    {
        if (msColour is null) return;
        long now = Environment.TickCount64;
        if (idleSince == 0) idleSince = now;
        else if (now - idleSince >= IdleReleaseMs) ReleaseTargets();
    }

    void ReleaseTargets()
    {
        if (resolved != msColour) resolved?.Dispose();
        msColour?.Dispose();
        msDepth?.Dispose();
        msColour = msDepth = resolved = null;
        idleSince = 0;
    }

    public void Dispose()
    {
        vertices.Dispose();
        if (resolved != msColour) resolved?.Dispose();
        msColour?.Dispose();
        msDepth?.Dispose();
    }
}
