using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Silk.NET.Core.Contexts;
using Silk.NET.Shaderc;

namespace Meitou.Rendering.Gpu.Shaders;

/// <summary>
/// Compiles the renderers' GLSL 3.30 programs (a vertex and a fragment shader linked by name, loose uniforms) to SPIR-V for Vulkan 1.3
/// with shaderc: interface locations injected so the stages agree, the stages' resource bindings kept apart (vertex 0.., fragment 32..,
/// descriptor set 0), optional clip depth remap, in-memory and on-disk caches. Names are kept in the SPIR-V for <see cref="SpirvReflection"/>.
/// </summary>
public sealed partial class GlslProgramCompiler
{
    /// <summary>Part of the cache key; bump when the output for the same source and options can change.</summary>
    public const int CacheVersion = 3;   // 3: bindings shifted after compiling, bundled shaderc pinned (2 was used by experiments)

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

    /// <summary>Cache version of the native model and compute shaders (docs/renderer-native.md 3.4); the legacy version stays 3.</summary>
    public const int NativeCacheVersion = 4;

    readonly ConcurrentDictionary<string, byte[]> nativeMemory = new();

    /// <summary>
    /// A GLSL 450 compute shader for Vulkan (strict rules: explicit sets and bindings, no loose uniforms; no interface location injection,
    /// no binding shift). Cached under <see cref="NativeCacheVersion"/>, so legacy entries are untouched.
    /// </summary>
    public byte[] CompileCompute(string glsl, ShaderCompileOptions? options = null) => CompileNativeStage(glsl, ShaderKind.ComputeShader, "compute", options);

    /// <summary>A GLSL 450 vertex or fragment shader of the native model (strict rules, explicit layout; the clip depth remap still applies
    /// when the options ask for it). Cached under <see cref="NativeCacheVersion"/>.</summary>
    public byte[] CompileNative(string glsl, bool fragment, ShaderCompileOptions? options = null)
    {
        options ??= DefaultOptions;
        if (!fragment && options.RemapClipDepth) glsl = AppendDepthRemap(glsl);
        return CompileNativeStage(glsl, fragment ? ShaderKind.FragmentShader : ShaderKind.VertexShader, fragment ? "fragment" : "vertex", options);
    }

    byte[] CompileNativeStage(string glsl, ShaderKind kind, string stage, ShaderCompileOptions? options)
    {
        ArgumentNullException.ThrowIfNull(glsl);
        options ??= DefaultOptions;
        string key = ShaderCache.Key(stage + ":native", glsl, options, NativeCacheVersion);
        if (options.UseMemoryCache && nativeMemory.TryGetValue(key, out var hit)) return hit;
        string dir = options.CacheDirectory ?? ShaderCache.DefaultDirectory;
        byte[] spirv;
        if (options.UseDiskCache && ShaderCache.TryRead(dir, key, NativeCacheVersion, out var cached, out _)) spirv = cached;
        else
        {
            spirv = CompileStage(glsl, kind, stage, 0, legacy: false);
            // The cache file holds a pair; a single stage is stored twice.
            if (options.UseDiskCache) ShaderCache.Write(dir, key, NativeCacheVersion, spirv, spirv);
        }
        if (options.UseMemoryCache) nativeMemory[key] = spirv;
        return spirv;
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

    /// <summary>
    /// The shaderc this assembly ships (NuGet <c>Silk.NET.Shaderc.Native</c>), not whichever <c>shaderc_shared</c> comes first on the
    /// library search path: a Vulkan SDK's <c>Bin</c> on PATH carries a newer one, and a different glslang can change the output.
    /// </summary>
    static readonly Lazy<Shaderc> Api = new(LoadApi);

    static Shaderc LoadApi()
    {
        // With the assembly given, .NET resolves the name through deps.json (runtimes/<rid>/native) or beside the assembly; PATH is not searched.
        if (NativeLibrary.TryLoad("shaderc_shared", typeof(Shaderc).Assembly, DllImportSearchPath.AssemblyDirectory, out nint lib))
            return new Shaderc(new LamdaNativeContext(name => NativeLibrary.TryGetExport(lib, name, out nint p) ? p : 0));
        return Shaderc.GetApi();
    }

    static unsafe byte[] CompileStage(string source, ShaderKind kind, string stage, uint bindingBase, bool legacy = true)
    {
        var api = Api.Value;
        Compiler* compiler = api.CompilerInitialize();
        CompileOptions* opts = api.CompileOptionsInitialize();
        try
        {
            api.CompileOptionsSetSourceLanguage(opts, SourceLanguage.Glsl);
            api.CompileOptionsSetTargetEnv(opts, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan13);
            if (legacy) api.CompileOptionsSetVulkanRulesRelaxed(opts, true);
            if (legacy) api.CompileOptionsSetAutoBindUniforms(opts, true);
            api.CompileOptionsSetAutoMapLocations(opts, true);
            if (legacy) api.CompileOptionsSetForcedVersionProfile(opts, 450, Profile.Core);
            api.CompileOptionsSetOptimizationLevel(opts, OptimizationLevel.Zero); // keeps OpName / OpMemberName
            // No per-kind binding bases: newer glslang has a resource kind for combined image samplers (EResCombinedSampler) that
            // shaderc's uniform kinds cannot reach, so sampler2D & co. stayed at 0 in the fragment stage. Every kind starts at 0 (the
            // auto-binder hands out distinct slots per set across kinds) and ShiftBindings moves the whole stage afterwards.
            // GLSL has no stage macro; code shared by both stages (the shadow receiver) needs one for gl_FragCoord and derivatives.
            if (kind == ShaderKind.FragmentShader) api.CompileOptionsAddMacroDefinition(opts, "MEITOU_FRAGMENT", 15, "1", 1);

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
                ShiftBindings(spirv, bindingBase);
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

    /// <summary>Adds <paramref name="offset"/> to every <c>OpDecorate … Binding</c> in the module, in place.</summary>
    internal static void ShiftBindings(byte[] spirv, uint offset)
    {
        if (offset == 0) return;
        var w = MemoryMarshal.Cast<byte, uint>(spirv.AsSpan());
        if (w.Length < 5 || w[0] != 0x07230203) throw new ArgumentException("Not a SPIR-V module.", nameof(spirv));
        const uint OpDecorate = 71, DecorationBinding = 33;
        for (int i = 5; i < w.Length;)
        {
            int count = (int)(w[i] >> 16);
            if (count == 0 || i + count > w.Length) throw new ArgumentException("Truncated SPIR-V instruction.", nameof(spirv));
            if ((w[i] & 0xFFFF) == OpDecorate && count == 4 && w[i + 2] == DecorationBinding) w[i + 3] += offset;
            i += count;
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
