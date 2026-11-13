using Meitou.Game.Rendering.Post;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Shaders;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// The motion pass's program for loose and animated meshes (<see cref="IWorldPostChain.ObjectMotion"/>, <see cref="MotionCamera"/>): a mesh of
/// the object vertex layout drawn with its transform now and last frame, both relative to this frame's eye, into the motion target (red and
/// green) where it is what the scene depth shows. Shared by the debris (birds, bolts, bones, limbs) and the world objects' animated instances
/// (rotors, doors, turrets).
/// </summary>
internal sealed class MotionMeshes : IDisposable
{
    /// <summary>
    /// The vertex stage: the mesh's position now (jittered, as drawn) and last frame (unjittered), both relative to this frame's eye.
    /// </summary>
    const string Vertex = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 2) in vec2 aUv;
        uniform mat4 uViewProjection;            // this frame's from the camera-relative world, jittered (as the meshes were drawn)
        uniform mat4 uPreviousViewProjection;    // last frame's, unjittered, for positions relative to this frame's eye
        uniform mat4 uModel;
        uniform vec4 uPrev0, uPrev1, uPrev2;     // last frame's model, the rows of its transform (x = dot(uPrev0, vec4(p, 1)))
        out vec2 vUv;
        out vec4 vNow;
        out vec4 vPrevious;
        void main()
        {
            vec4 p = vec4(aPos, 1.0);
            vUv = aUv;
            gl_Position = uViewProjection * (uModel * p);
            vNow = gl_Position;
            vPrevious = uPreviousViewProjection * vec4(dot(uPrev0, p), dot(uPrev1, p), dot(uPrev2, p), 1.0);
        }
        """;

    /// <summary>
    /// The mesh's motion as the characters write theirs (UV units, this frame minus last, jitter removed), where it is what the scene depth shows.
    /// </summary>
    static readonly string Fragment = """
        #version 330 core
        in vec2 vUv;
        in vec4 vNow;
        in vec4 vPrevious;
        uniform sampler2D uDiffuse;
        uniform sampler2D uNearDepth;
        uniform float uAlphaThreshold;
        uniform vec2 uNearPlanes;
        uniform vec2 uJitterNdc;
        out vec4 fragColour;
        """ + DepthConvention.Glsl + """
        void main()
        {
            if (uAlphaThreshold > 0.0 && texture(uDiffuse, vUv).a < uAlphaThreshold) discard;
            float stored = texelFetch(uNearDepth, ivec2(gl_FragCoord.xy), 0).r;
            if (depthIsSky(stored)) discard;
            float zs = depthViewZ(stored, uNearPlanes), zf = depthViewZ(gl_FragCoord.z, uNearPlanes);
            if (abs(zs - zf) > 0.002 * zs + 0.05) discard;
            vec2 now = vNow.xy / vNow.w - uJitterNdc, previous = vPrevious.xy / vPrevious.w;
            fragColour = vec4((now - previous) * 0.5, 0.0, 0.0);
        }
        """;

    /// <summary>
    /// The push block (std430, 128 bytes): the model, last frame's as three rows, the depth and diffuse bindless indices and the cut-out.
    /// </summary>
    const string PushMembers = """
        mat4 model;
        vec4 prev0;
        vec4 prev1;
        vec4 prev2;
        uint depth;
        uint diffuse;
        float alphaThreshold;
        uint pad;
        """;

    /// <summary>
    /// The programs' own uniforms and samplers, as the push block and the bindless tables hold them.
    /// </summary>
    static readonly Dictionary<string, string> Own = new()
    {
        ["uModel"] = "pc.model", ["uPrev0"] = "pc.prev0", ["uPrev1"] = "pc.prev1", ["uPrev2"] = "pc.prev2", ["uAlphaThreshold"] = "pc.alphaThreshold",
        ["uNearDepth"] = "textures2D[pc.depth]", ["uDiffuse"] = "textures2D[pc.diffuse]",
    };

    /// <summary>
    /// The device the pass records on.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// The frame constants the program binds.
    /// </summary>
    readonly NativeFrame _frame;

    /// <summary>
    /// The motion program.
    /// </summary>
    readonly NativeProg _program;

    /// <summary>
    /// The program's vertex layout: the object layout's position (location 0) and uv (location 2).
    /// </summary>
    readonly VertexLayout _layout;

    /// <summary>
    /// The scene depth's bindless index, with the texture it was registered for (null: none yet).
    /// </summary>
    (SampledTexture Texture, uint Index)? _depthIndex;

    /// <summary>
    /// The open guest pass's commands, between <see cref="Begin"/> and <see cref="End"/>.
    /// </summary>
    IGpuCommandList? _cmd;

    /// <summary>
    /// Scratch: the vertex buffers <see cref="Mesh"/> binds (the position twice, the second for the unused location 1, then the uv).
    /// </summary>
    readonly GpuBufferBinding[] _bindings = new GpuBufferBinding[3];

    /// <summary>
    /// The program over <paramref name="frame"/>'s constants, for meshes of the object layout with <paramref name="stride"/> bytes a vertex.
    /// </summary>
    public MotionMeshes(IGpuContext gpu, NativeFrame frame, uint stride)
    {
        _gpu = gpu;
        _frame = frame;
        string vertex = NativeShaders.Port(Vertex, NativeShaders.Map(Own), PushMembers);
        string fragment = NativeShaders.Port(Fragment, NativeShaders.Map(Own), PushMembers);
        _program = new NativeProg(gpu, frame, vertex, fragment, "mesh motion");
        _layout = _program.Layout(
        [
            new LegacyProgram.Attribute(default, InternalFormat.Rgb32F, stride, false),
            null,
            new LegacyProgram.Attribute(default, InternalFormat.Rg32F, stride, false),
        ]);
    }

    /// <summary>
    /// The last frame's transform as the push block's three rows (a System.Numerics matrix, row vectors).
    /// </summary>
    public static (Vector4, Vector4, Vector4) Rows(in Matrix4x4 p) =>
        (new Vector4(p.M11, p.M21, p.M31, p.M41), new Vector4(p.M12, p.M22, p.M32, p.M42), new Vector4(p.M13, p.M23, p.M33, p.M43));

    /// <summary>
    /// Opens the guest pass for the draws that follow (<see cref="Mesh"/>, <see cref="Draw"/>), then <see cref="End"/>.
    /// </summary>
    public void Begin(MotionTargets targets, MotionCamera camera)
    {
        _cmd = _gpu.BeginGuest("mesh motion");
        var pass = _gpu.CurrentTargets();
        var state = _gpu.CurrentState() with { Cull = CullMode.None, DepthTest = false, DepthWrite = false, Blend = BlendState.Off };
        _cmd.SetViewport(pass.Viewport);
        _cmd.SetScissor(pass.Scissor);
        _cmd.SetRaster(state.Cull, state.Front);
        _cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
        _cmd.SetDepthBias(false, 0, 0);
        _cmd.BindPipeline(_gpu.Pipelines.Get(state.Pipeline(_program.P, _layout, PrimitiveType.Triangles, pass.Formats, "mesh motion")));

        if (_depthIndex is not { } known || known.Texture != targets.Depth)
        {
            if (_depthIndex is { } old) _gpu.Bindless.Free(BindlessKind.Texture2D, old.Index);
            _depthIndex = (targets.Depth, _gpu.Bindless.Register(BindlessKind.Texture2D, targets.Depth));
        }

        var view = new ViewConstants
        {
            ViewProjection = camera.Relative,
            PreviousViewProjection = camera.PreviousRelative,
            Eye = camera.Eye,
            NearPlanes = targets.Planes,
            JitterNdc = targets.JitterNdc,
        };
        _frame.Bind(_cmd, _program.P, in view);
    }

    /// <summary>
    /// Binds a mesh's position and uv (the object layout's locations 0 and 2: buffers with their offsets) and its indices.
    /// </summary>
    public void Mesh(GpuBufferBinding position, GpuBufferBinding uv, GpuBufferBinding indices)
    {
        _bindings[0] = position;
        _bindings[1] = position;
        _bindings[2] = uv;
        _cmd!.BindVertexBuffers(0, _bindings);
        _cmd.BindIndexBuffer(indices, IndexKind.U32);
    }

    /// <summary>
    /// One draw of the bound mesh: <paramref name="model"/> and <paramref name="previous"/> relative to the frame's eye.
    /// </summary>
    public void Draw(uint count, uint first, int vertexOffset, in Matrix4x4 model, in Matrix4x4 previous, uint diffuse, float alphaThreshold)
    {
        var (p0, p1, p2) = Rows(previous);
        var push = new MotionPush
        {
            Model = model,
            Prev0 = p0,
            Prev1 = p1,
            Prev2 = p2,
            Depth = _depthIndex!.Value.Index,
            Diffuse = diffuse,
            AlphaThreshold = alphaThreshold,
        };
        _cmd!.PushConstants(_program.P, GpuStage.Vertex | GpuStage.Fragment, in push);
        _cmd.DrawIndexed(count, 1, first, vertexOffset);
    }

    /// <summary>
    /// Ends the guest pass <see cref="Begin"/> opened.
    /// </summary>
    public void End()
    {
        _gpu.EndGuest(_cmd!);
        _cmd = null;
    }

    /// <summary>
    /// Frees the program and the scene depth's bindless index.
    /// </summary>
    public void Dispose()
    {
        _program.Dispose();
        if (_depthIndex is { } depth) _gpu.Bindless.Free(BindlessKind.Texture2D, depth.Index);
    }
}

/// <summary>
/// The C# side of <see cref="MotionMeshes"/>'s push block (std430 push constants, 128 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
struct MotionPush
{
    /// <summary>
    /// This frame's model transform, relative to the eye.
    /// </summary>
    public Matrix4x4 Model;

    /// <summary>
    /// Last frame's model transform as three rows (<see cref="MotionMeshes.Rows"/>).
    /// </summary>
    public Vector4 Prev0, Prev1, Prev2;

    /// <summary>
    /// The bindless indices of the scene depth and the diffuse texture.
    /// </summary>
    public uint Depth, Diffuse;

    /// <summary>
    /// The cut-out's alpha threshold (0: none).
    /// </summary>
    public float AlphaThreshold;

    /// <summary>
    /// Padding to the block's size.
    /// </summary>
    public uint Pad;
}

/// <summary>
/// The camera of the motion pass for meshes placed relative to this frame's eye: this frame's rotation-only view-projection as drawn
/// (jittered), and last frame's (unjittered) after the move between the eyes.
/// </summary>
internal readonly record struct MotionCamera(Matrix4x4 Relative, Matrix4x4 PreviousRelative, Vector3 Eye);
