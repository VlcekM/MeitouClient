namespace Meitou.Rendering.Vulkan.Shaders;

/// <summary>A linked-by-name vertex and fragment pair as SPIR-V, with reflection of both stages.</summary>
public sealed class CompiledProgram
{
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

    public byte[] VertexSpirv { get; }
    public byte[] FragmentSpirv { get; }
    public ShaderReflection Vertex { get; }
    public ShaderReflection Fragment { get; }

    /// <summary>The GLSL actually compiled (locations injected, depth remap appended), for diagnostics.</summary>
    public string VertexSource { get; }
    public string FragmentSource { get; }

    /// <summary>Compiled now, or taken from the memory or disk cache.</summary>
    public ProgramSource Source { get; }

    internal CompiledProgram WithSource(ProgramSource source) => new(VertexSpirv, FragmentSpirv, VertexSource, FragmentSource, source);

    /// <summary>The uniform in each stage that declares it (null where a stage does not).</summary>
    public (UniformLookup? Vertex, UniformLookup? Fragment) FindUniform(string glName) => (Vertex.FindUniform(glName), Fragment.FindUniform(glName));
}
