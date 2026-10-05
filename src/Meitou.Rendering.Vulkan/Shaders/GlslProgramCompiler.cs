using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Silk.NET.Shaderc;

namespace Meitou.Rendering.Vulkan.Shaders;

/// <summary>
/// Compiles the renderers' GLSL 3.30 programs (a vertex and a fragment shader linked by name, loose uniforms) to SPIR-V for Vulkan 1.3
/// with shaderc: interface locations injected so the stages agree, the stages' resource bindings kept apart (vertex 0.., fragment 32..,
/// descriptor set 0), optional clip depth remap, in-memory and on-disk caches. Names are kept in the SPIR-V for <see cref="SpirvReflection"/>.
/// </summary>
public sealed partial class GlslProgramCompiler
{
    /// <summary>Part of the cache key; bump when the output for the same source and options can change.</summary>
    public const int CacheVersion = 1;

    readonly ConcurrentDictionary<string, CompiledProgram> memory = new();

    /// <summary>Options used when <see cref="Compile"/> is given none.</summary>
    public ShaderCompileOptions DefaultOptions { get; }

    public GlslProgramCompiler(ShaderCompileOptions? defaults = null) => DefaultOptions = defaults ?? new ShaderCompileOptions();

    public CompiledProgram Compile(string vertexGlsl, string fragmentGlsl, ShaderCompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(vertexGlsl);
        ArgumentNullException.ThrowIfNull(fragmentGlsl);
        options ??= DefaultOptions;

        string key = ShaderCache.Key(vertexGlsl, fragmentGlsl, options, CacheVersion);
        if (options.UseMemoryCache && memory.TryGetValue(key, out var cached)) return cached.WithSource(ProgramSource.Memory);

        var (vs, fs) = Preprocess(vertexGlsl, fragmentGlsl, options);

        CompiledProgram program;
        string dir = options.CacheDirectory ?? ShaderCache.DefaultDirectory;
        if (options.UseDiskCache && ShaderCache.TryRead(dir, key, CacheVersion, out var v, out var f))
        {
            try { program = new CompiledProgram(v, f, vs, fs, ProgramSource.Disk); }
            catch (ArgumentException) { program = CompileFresh(vs, fs, options, dir, key); } // unreadable SPIR-V: rewrite
        }
        else program = CompileFresh(vs, fs, options, dir, key);

        if (options.UseMemoryCache) memory[key] = program;
        return program;
    }

    CompiledProgram CompileFresh(string vs, string fs, ShaderCompileOptions options, string dir, string key)
    {
        byte[] v = CompileStage(vs, ShaderKind.VertexShader, "vertex", options.VertexBindingBase);
        byte[] f = CompileStage(fs, ShaderKind.FragmentShader, "fragment", options.FragmentBindingBase);
        var program = new CompiledProgram(v, f, vs, fs, ProgramSource.Compiled);
        if (options.UseDiskCache) ShaderCache.Write(dir, key, CacheVersion, v, f);
        return program;
    }

    /// <summary>The GLSL that is compiled: locations injected, and the depth remap appended to the vertex stage when asked for.</summary>
    public static (string Vertex, string Fragment) Preprocess(string vertexGlsl, string fragmentGlsl, ShaderCompileOptions? options = null)
    {
        var (vs, fs) = InterfaceLocations.Apply(vertexGlsl, fragmentGlsl);
        if (options?.RemapClipDepth == true) vs = AppendDepthRemap(vs);
        return (vs, fs);
    }

    static string AppendDepthRemap(string vertex)
    {
        var main = MainRegex();
        if (!main.IsMatch(vertex)) throw new ShaderCompileException("The vertex shader has no main().", "vertex", "");
        string renamed = main.Replace(vertex, "void meitou_original_main()", 1);
        return renamed + """

            void main()
            {
                meitou_original_main();
                gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;
            }

            """;
    }

    [GeneratedRegex(@"\bvoid\s+main\s*\(\s*(?:void)?\s*\)")]
    private static partial Regex MainRegex();

    static unsafe byte[] CompileStage(string source, ShaderKind kind, string stage, uint bindingBase)
    {
        var api = Shaderc.GetApi();
        Compiler* compiler = api.CompilerInitialize();
        CompileOptions* opts = api.CompileOptionsInitialize();
        try
        {
            api.CompileOptionsSetSourceLanguage(opts, SourceLanguage.Glsl);
            api.CompileOptionsSetTargetEnv(opts, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan13);
            api.CompileOptionsSetVulkanRulesRelaxed(opts, true);
            api.CompileOptionsSetAutoBindUniforms(opts, true);
            api.CompileOptionsSetAutoMapLocations(opts, true);
            api.CompileOptionsSetForcedVersionProfile(opts, 450, Profile.Core);
            api.CompileOptionsSetOptimizationLevel(opts, OptimizationLevel.Zero); // keeps OpName / OpMemberName
            foreach (UniformKind uk in Enum.GetValues<UniformKind>())
                api.CompileOptionsSetBindingBaseForStage(opts, kind, uk, bindingBase);

            byte[] bytes = Encoding.UTF8.GetBytes(source);
            CompilationResult* result;
            fixed (byte* p = bytes)
                result = api.CompileIntoSpv(compiler, p, (nuint)bytes.Length, kind, stage + ".glsl", "main", opts);
            try
            {
                if (api.ResultGetCompilationStatus(result) != CompilationStatus.Success)
                {
                    string log = Marshal(api.ResultGetErrorMessage(result));
                    throw new ShaderCompileException($"The {stage} shader failed to compile:\n{log}\n{NumberedLines(source, log)}", stage, log);
                }
                int length = (int)api.ResultGetLength(result);
                var spirv = new byte[length];
                System.Runtime.InteropServices.Marshal.Copy((nint)api.ResultGetBytes(result), spirv, 0, length);
                return spirv;
            }
            finally { api.ResultRelease(result); }
        }
        finally
        {
            api.CompileOptionsRelease(opts);
            api.CompilerRelease(compiler);
        }
    }

    static unsafe string Marshal(byte* text) => System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)text) ?? "";

    /// <summary>The source with line numbers: the lines shaderc complained about (plus two either side), or all of it when none are named.</summary>
    static string NumberedLines(string source, string log)
    {
        string[] lines = source.Split('\n');
        var wanted = new SortedSet<int>();
        foreach (Match m in ErrorLineRegex().Matches(log))
        {
            int n = int.Parse(m.Groups[1].Value);
            for (int i = Math.Max(1, n - 2); i <= Math.Min(lines.Length, n + 2); i++) wanted.Add(i);
        }
        if (wanted.Count == 0) wanted.UnionWith(Enumerable.Range(1, lines.Length));
        var sb = new StringBuilder();
        foreach (int n in wanted) sb.Append($"{n,5}: {lines[n - 1].TrimEnd('\r')}\n");
        return sb.ToString();
    }

    [GeneratedRegex(@":(\d+): (?:error|warning)")]
    private static partial Regex ErrorLineRegex();
}
