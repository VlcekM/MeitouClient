using System.Numerics;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Modding;

/// <summary>
/// Where in the frame's post chain a mod's <see cref="PostPass"/> runs (docs/modding.md "Post passes").
/// </summary>
public enum PostStage
{
    /// <summary>
    /// The scene in linear HDR at the display size, after the upscaler and the depth of field, before the exposure's measure, the bloom and the
    /// tone map: what a pass writes is exposed, bloomed and tone mapped as the scene is.
    /// </summary>
    Scene,
    /// <summary>
    /// The finished picture, after the tone map, the grade, the anti-aliasing and the heat haze, before the interface is drawn over it: 0..1
    /// display values (above 1 in HDR output, <see cref="PostPassFrame.Hdr"/>).
    /// </summary>
    Picture,
}

/// <summary>
/// A full-screen pass a mod adds to the post chain with <see cref="ModContext.AddPostPass"/> (docs/modding.md "Post passes"): a fragment shader
/// that reads the picture so far and writes the picture the next pass, or the screen, gets. The game compiles it, makes its targets, binds what it
/// declares and times it; the mod sets its own uniforms in <see cref="Prepare"/>.
/// </summary>
/// <remarks>
/// The fragment is whole GLSL as the game's own post shaders: <c>#version 330 core</c>, <c>in vec2 vUv;</c> (0..1 over the picture) and one
/// <c>out vec4</c>. Declared by these names, the game binds <c>uniform sampler2D uImage</c> (the picture so far, linear, clamped),
/// <c>uniform sampler2D uDepth</c> (the scene's depth at the render size, nearest; read it with <see cref="DepthGlsl"/>),
/// <c>uniform sampler2D uNormal</c> (the lit surfaces' normals, read with <see cref="NormalGlsl"/>), and sets <c>uniform vec2 uViewport</c> (the
/// picture's size in pixels), <c>uniform vec2 uPlanes</c> (the scene's near and far planes) and <c>uniform float uTime</c> (seconds, wrapped
/// every hour).
/// </remarks>
public sealed class PostPass
{
    /// <summary>
    /// The longest <see cref="Name"/>.
    /// </summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// The longest <see cref="Fragment"/>, in characters.
    /// </summary>
    public const int MaxFragmentLength = 256 * 1024;

    /// <summary>
    /// The most passes one mod registers.
    /// </summary>
    public const int MaxPerMod = 32;

    /// <summary>
    /// GLSL to paste into a fragment after its uniforms to read <c>uDepth</c>: the game's depth convention (<c>depthIsSky(d)</c>,
    /// <c>depthViewZ(d, uPlanes)</c> the distance along the view in world units, and the rest of <see cref="DepthConvention.Glsl"/>), which
    /// differs between Vulkan (reversed) and Metal.
    /// </summary>
    public const string DepthGlsl = DepthConvention.Glsl;

    /// <summary>
    /// GLSL to paste into a fragment after <c>uniform sampler2D uNormal;</c>: <c>vec3 meitouNormal(vec2 uv)</c>, the lit surface's shading normal
    /// in world space at a place of the picture, or <c>vec3(0.0)</c> where there is none (the sky, a surface that stores none, or a frame
    /// without normals: <see cref="PostPassFrame.HasNormals"/> false). The game owns the packing, so a pass reads it through this only.
    /// </summary>
    public const string NormalGlsl = Meitou.Game.Rendering.Post.PostProcessShaders.SurfaceNormal;

    /// <summary>
    /// A pass named <paramref name="name"/> (1 to <see cref="MaxNameLength"/> characters on one line: its log lines and its GPU time) drawing
    /// <paramref name="fragment"/> (whole GLSL, at most <see cref="MaxFragmentLength"/> characters); throws for a bad name or fragment.
    /// </summary>
    public PostPass(string name, string fragment)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(fragment);
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength || name.Any(char.IsControl))
            throw new ArgumentException($"A post pass's name is 1 to {MaxNameLength} characters on one line.", nameof(name));
        if (string.IsNullOrWhiteSpace(fragment) || fragment.Length > MaxFragmentLength)
            throw new ArgumentException($"A post pass's fragment is GLSL of at most {MaxFragmentLength} characters.", nameof(fragment));

        Name = name;
        Fragment = fragment;
    }

    /// <summary>
    /// The pass's name: its log lines and its GPU time's section.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The fragment shader's GLSL.
    /// </summary>
    public string Fragment { get; }

    /// <summary>
    /// Where in the chain it runs.
    /// </summary>
    public PostStage Stage { get; init; } = PostStage.Picture;

    /// <summary>
    /// Its place among every mod's passes of the stage: lower first; equal ones in registration order.
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// Called on the render thread each frame the pass is on, before the stage draws: sets the pass's own uniforms
    /// (<see cref="PostPassFrame.Set(string, float)"/>) and may leave the frame out (<see cref="PostPassFrame.Skip"/>). It must be cheap and must
    /// not touch the world. A throw turns the pass off (<see cref="Fault"/>).
    /// </summary>
    public Action<PostPassFrame>? Prepare { get; init; }

    /// <summary>
    /// Whether the pass runs; set it at any time, from any thread. A pass that is off costs nothing: no target is made and the chain draws as
    /// without it.
    /// </summary>
    public bool Enabled
    {
        get => Volatile.Read(ref _enabled);
        set => Volatile.Write(ref _enabled, value);
    }

    /// <summary>
    /// Why the game turned the pass off for the session (its shader did not compile, <see cref="Prepare"/> threw); null while it is sound.
    /// </summary>
    public string? Fault { get; internal set; }

    /// <summary>
    /// The id of the mod that registered it (set by <see cref="ModContext.AddPostPass"/>).
    /// </summary>
    internal string Owner { get; set; } = "";

    /// <summary>
    /// <see cref="Enabled"/>'s value.
    /// </summary>
    bool _enabled = true;
}

/// <summary>
/// What a <see cref="PostPass.Prepare"/> gets each frame: the picture and the camera the pass draws for, and the pass's uniforms. Valid during
/// the call only.
/// </summary>
public sealed class PostPassFrame
{
    /// <summary>
    /// The program the uniforms are set on; null outside a call.
    /// </summary>
    LegacyProgram? _program;

    /// <summary>
    /// The stage the pass runs in.
    /// </summary>
    public PostStage Stage { get; internal set; }

    /// <summary>
    /// The picture's size in pixels (the pass's target and <c>uImage</c>).
    /// </summary>
    public int Width { get; internal set; }

    /// <summary>
    /// See <see cref="Width"/>.
    /// </summary>
    public int Height { get; internal set; }

    /// <summary>
    /// The scene camera's vertical field of view in radians.
    /// </summary>
    public float FieldOfView { get; internal set; }

    /// <summary>
    /// The scene camera's aspect (width over height).
    /// </summary>
    public float Aspect { get; internal set; }

    /// <summary>
    /// The scene projection's near plane, in world units.
    /// </summary>
    public float Near { get; internal set; }

    /// <summary>
    /// The scene projection's far plane, in world units.
    /// </summary>
    public float Far { get; internal set; }

    /// <summary>
    /// The scene camera's rotation (world to view, no translation: the view is eye-relative).
    /// </summary>
    public Matrix4x4 View { get; internal set; }

    /// <summary>
    /// The scene camera's place in the world.
    /// </summary>
    public Vector3 Eye { get; internal set; }

    /// <summary>
    /// Seconds since the game started, on the real clock (pause does not hold it).
    /// </summary>
    public double Seconds { get; internal set; }

    /// <summary>
    /// Whether a world scene was drawn this frame: false leaves <c>uDepth</c> without a scene (a loading screen).
    /// </summary>
    public bool HasScene { get; internal set; }

    /// <summary>
    /// Whether <c>uNormal</c> holds this frame's normals (<see cref="PostPass.NormalGlsl"/>): only while the scene stores its surfaces (the
    /// indirect light or the reflections on, Vulkan) and from the frame after a pass that reads them first ran, else every lookup is none.
    /// </summary>
    public bool HasNormals { get; internal set; }

    /// <summary>
    /// Whether the picture carries values above 1 (HDR output): a <see cref="PostStage.Picture"/> pass then must not clamp to 1.
    /// </summary>
    public bool Hdr { get; internal set; }

    /// <summary>
    /// Whether <c>vUv.y</c> 0 is the picture's top row (Metal) rather than its bottom row (Vulkan): for a pass whose pattern has an up and a down.
    /// </summary>
    public bool TopDown { get; internal set; }

    /// <summary>
    /// Set by <see cref="PostPass.Prepare"/> to leave the pass out of this frame (the chain draws as if it were off).
    /// </summary>
    public bool Skip { get; set; }

    /// <summary>
    /// Sets the pass's <c>uniform float</c> (or <c>int</c>, <c>bool</c>) <paramref name="uniform"/>; a name the shader does not use is ignored, as
    /// for every overload.
    /// </summary>
    public void Set(string uniform, float value) => _program?.Set(Handle(uniform), value);

    /// <summary>
    /// Sets the pass's <c>uniform int</c> (or <c>bool</c>) <paramref name="uniform"/>.
    /// </summary>
    public void Set(string uniform, int value) => _program?.Set(Handle(uniform), value);

    /// <summary>
    /// Sets the pass's <c>uniform vec2</c> <paramref name="uniform"/>.
    /// </summary>
    public void Set(string uniform, Vector2 value) => _program?.Set(Handle(uniform), value);

    /// <summary>
    /// Sets the pass's <c>uniform vec3</c> <paramref name="uniform"/>.
    /// </summary>
    public void Set(string uniform, Vector3 value) => _program?.Set(Handle(uniform), value);

    /// <summary>
    /// Sets the pass's <c>uniform vec4</c> <paramref name="uniform"/>.
    /// </summary>
    public void Set(string uniform, Vector4 value) => _program?.Set(Handle(uniform), value);

    /// <summary>
    /// Sets the pass's <c>uniform mat4</c> <paramref name="uniform"/>.
    /// </summary>
    public void Set(string uniform, Matrix4x4 value) => _program?.Set(Handle(uniform), in value);

    /// <summary>
    /// Opens the frame for one pass's <see cref="PostPass.Prepare"/>: the program its uniforms are set on.
    /// </summary>
    internal void Open(LegacyProgram program)
    {
        _program = program;
        Skip = false;
    }

    /// <summary>
    /// Closes the frame after the call: a kept reference sets nothing.
    /// </summary>
    internal void Close() => _program = null;

    /// <summary>
    /// The handle of a uniform of the open program (the program keeps each name's lookup).
    /// </summary>
    UniformHandle Handle(string uniform)
    {
        ArgumentNullException.ThrowIfNull(uniform);

        return _program?.Uniform(uniform) ?? UniformHandle.None;
    }
}
