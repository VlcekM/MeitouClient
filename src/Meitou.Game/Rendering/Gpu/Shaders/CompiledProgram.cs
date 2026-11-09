using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Gpu.Shaders;

/// <summary>
/// A linked-by-name vertex and fragment pair as SPIR-V, with reflection of both stages.
/// </summary>
public sealed class CompiledProgram
{
    /// <summary>
    /// A program of the two stages' SPIR-V (reflected now) and the GLSL they were compiled from.
    /// </summary>
    internal CompiledProgram(byte[] vertex, byte[] fragment, string vertexSource, string fragmentSource, ProgramSource source)
    {
        VertexSpirv = vertex;
        FragmentSpirv = fragment;
        Vertex = SpirvReflection.Parse(vertex);
        Fragment = SpirvReflection.Parse(fragment);
        VertexSource = vertexSource;
        FragmentSource = fragmentSource;
        Source = source;
    }

    /// <summary>
    /// The vertex stage's SPIR-V.
    /// </summary>
    public byte[] VertexSpirv { get; }

    /// <summary>
    /// The fragment stage's SPIR-V.
    /// </summary>
    public byte[] FragmentSpirv { get; }

    /// <summary>
    /// The vertex stage's reflection.
    /// </summary>
    public ShaderReflection Vertex { get; }

    /// <summary>
    /// The fragment stage's reflection.
    /// </summary>
    public ShaderReflection Fragment { get; }

    /// <summary>
    /// The vertex GLSL actually compiled (locations injected, the depth convention's define added), for diagnostics.
    /// </summary>
    public string VertexSource { get; }

    /// <summary>
    /// The fragment GLSL actually compiled, for diagnostics.
    /// </summary>
    public string FragmentSource { get; }

    /// <summary>
    /// Compiled now, or taken from the memory or disk cache.
    /// </summary>
    public ProgramSource Source { get; }

    /// <summary>
    /// The same program marked as from <paramref name="source"/> (both stages reflected again).
    /// </summary>
    internal CompiledProgram WithSource(ProgramSource source) => new(VertexSpirv, FragmentSpirv, VertexSource, FragmentSource, source);

    /// <summary>
    /// The uniform in each stage that declares it (null where a stage does not).
    /// </summary>
    public (UniformLookup? Vertex, UniformLookup? Fragment) FindUniform(string glName) => (Vertex.FindUniform(glName), Fragment.FindUniform(glName));
}
