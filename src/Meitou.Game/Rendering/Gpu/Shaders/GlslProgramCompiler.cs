using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Meitou.Game.Rendering.Frame;
using Silk.NET.Core.Contexts;
using Silk.NET.Shaderc;

namespace Meitou.Game.Rendering.Gpu.Shaders;

/// <summary>
/// Compiles the renderers' GLSL 3.30 programs (a vertex and a fragment shader linked by name, loose uniforms) to SPIR-V for Vulkan 1.3 with shaderc:
/// interface locations injected so the stages agree, the stages' resource bindings kept apart (vertex 0.., fragment 32.., descriptor set 0), the
/// depth convention's define, in-memory and on-disk caches. Names are kept in the SPIR-V for <see cref="SpirvReflection"/>.
/// </summary>
public sealed partial class GlslProgramCompiler
{
    /// <summary>
    /// The shaderc this assembly ships (NuGet <c>Silk.NET.Shaderc.Native</c>), not whichever <c>shaderc_shared</c> comes first on the library search
    /// path: a Vulkan SDK's <c>Bin</c> on PATH carries a newer one, and a different glslang can change the output.
    /// </summary>
    static readonly Lazy<Shaderc> Api = new(LoadApi);

    /// <summary>
    /// Part of the cache key; bump when the output for the same source and options can change.
    /// </summary>
    public const int CacheVersion = 4;   // 4: clip depth remap removed, MEITOU_DEPTH_REVERSED; 3: bindings shifted after compiling, shaderc pinned

    /// <summary>
    /// Cache version of the native model and compute shaders (docs/renderer-native.md 3.4); the legacy one is <see cref="CacheVersion"/>.
    /// </summary>
    public const int NativeCacheVersion = 5;   // 5: clip depth remap removed, MEITOU_DEPTH_REVERSED

    /// <summary>
    /// The compiled legacy programs by cache key (when the options use the memory cache).
    /// </summary>
    readonly ConcurrentDictionary<string, CompiledProgram> _memory = new();
    /// <summary>
    /// The compiled native stages' SPIR-V by cache key (when the options use the memory cache).
    /// </summary>
    readonly ConcurrentDictionary<string, byte[]> _nativeMemory = new();

    /// <summary>
    /// A compiler with <paramref name="defaults"/> (or the default options) for calls given none.
    /// </summary>
    public GlslProgramCompiler(ShaderCompileOptions? defaults = null) => DefaultOptions = defaults ?? new ShaderCompileOptions();

    /// <summary>
    /// Options used when <see cref="Compile"/> is given none.
    /// </summary>
    public ShaderCompileOptions DefaultOptions { get; }

    /// <summary>
    /// The program's two stages as SPIR-V: from the memory cache, the disk cache or compiled now (locations injected, the depth define added,
    /// bindings shifted).
    /// </summary>
    public CompiledProgram Compile(string vertexGlsl, string fragmentGlsl, ShaderCompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(vertexGlsl);
        ArgumentNullException.ThrowIfNull(fragmentGlsl);
        options ??= DefaultOptions;

        string key = ShaderCache.Key(vertexGlsl, fragmentGlsl, options, CacheVersion);
        if (options.UseMemoryCache && _memory.TryGetValue(key, out var cached)) return cached.WithSource(ProgramSource.Memory);

        var (vs, fs) = Preprocess(vertexGlsl, fragmentGlsl, options);
        string dir = options.CacheDirectory ?? ShaderCache.DefaultDirectory;
        var program = LoadOrCompile(vs, fs, options, dir, key);
        if (options.UseMemoryCache) _memory[key] = program;

        return program;
    }

    /// <summary>
    /// The program from the disk cache when it has a readable pair (unreadable SPIR-V is compiled again), else compiled now.
    /// </summary>
    CompiledProgram LoadOrCompile(string vs, string fs, ShaderCompileOptions options, string dir, string key)
    {
        if (options.UseDiskCache && ShaderCache.TryRead(dir, key, CacheVersion, out var v, out var f))
        {
            try
            {
                return new CompiledProgram(v, f, vs, fs, ProgramSource.Disk);
            }
            catch (ArgumentException)
            {
                return CompileFresh(vs, fs, options, dir, key); // unreadable SPIR-V: rewrite
            }
        }

        return CompileFresh(vs, fs, options, dir, key);
    }

    /// <summary>
    /// A GLSL 450 compute shader for Vulkan (strict rules: explicit sets and bindings, no loose uniforms; no interface location injection, no binding
    /// shift). Cached under <see cref="NativeCacheVersion"/>, so legacy entries are untouched.
    /// </summary>
    public byte[] CompileCompute(string glsl, ShaderCompileOptions? options = null) =>
        CompileNativeStage(glsl, ShaderKind.ComputeShader, "compute", options);

    /// <summary>
    /// A GLSL 450 vertex or fragment shader of the native model (strict rules, explicit layout). Cached under <see cref="NativeCacheVersion"/>.
    /// </summary>
    public byte[] CompileNative(string glsl, bool fragment, ShaderCompileOptions? options = null)
    {
        return CompileNativeStage(glsl, fragment ? ShaderKind.FragmentShader : ShaderKind.VertexShader, fragment ? "fragment" : "vertex", options);
    }

    /// <summary>
    /// A GLSL 450 tessellation control or evaluation shader of the native model. Cached under <see cref="NativeCacheVersion"/>.
    /// </summary>
    public byte[] CompileNativeTessellation(string glsl, bool evaluation, ShaderCompileOptions? options = null)
    {
        return CompileNativeStage(glsl, evaluation ? ShaderKind.TessEvaluationShader : ShaderKind.TessControlShader,
            evaluation ? "tess evaluation" : "tess control", options);
    }

    /// <summary>
    /// The GLSL that is compiled: locations injected, and the depth convention's define when the options ask for it.
    /// </summary>
    public static (string Vertex, string Fragment) Preprocess(string vertexGlsl, string fragmentGlsl, ShaderCompileOptions? options = null)
    {
        var (vs, fs) = InterfaceLocations.Apply(vertexGlsl, fragmentGlsl);
        if (options is null) return (vs, fs);

        return (Define(vs, options), Define(fs, options));
    }

    /// <summary>
    /// Inserts <c>#define MEITOU_DEPTH_REVERSED 1</c> after the first <c>#version</c> line (at the top when there is none) when
    /// <see cref="ShaderCompileOptions.DepthReversed"/> is set: it selects the reversed branch of <see cref="DepthConvention.Glsl"/>.
    /// </summary>
    static string Define(string glsl, ShaderCompileOptions o)
    {
        if (!o.DepthReversed)
        {
            return glsl;
        }

        const string define = "#define MEITOU_DEPTH_REVERSED 1";
        var version = VersionRegex().Match(glsl);
        if (!version.Success)
        {
            return define + "\n" + glsl;
        }

        int at = version.Index + version.Length;
        return glsl[..at] + "\n" + define + glsl[at..];
    }

    /// <summary>
    /// Adds <paramref name="offset"/> to every <c>OpDecorate … Binding</c> in the module, in place.
    /// </summary>
    internal static void ShiftBindings(byte[] spirv, uint offset)
    {
        if (offset == 0) return;

        var w = MemoryMarshal.Cast<byte, uint>(spirv.AsSpan());
        if (w.Length < 5 || w[0] != 0x07230203) throw new ArgumentException("Not a SPIR-V module.", nameof(spirv));

        const uint opDecorate = 71, decorationBinding = 33;
        for (int i = 5; i < w.Length;)
        {
            int count = (int)(w[i] >> 16);
            if (count == 0 || i + count > w.Length) throw new ArgumentException("Truncated SPIR-V instruction.", nameof(spirv));

            if ((w[i] & 0xFFFF) == opDecorate && count == 4 && w[i + 2] == decorationBinding) w[i + 3] += offset;
            i += count;
        }
    }

    /// <summary>
    /// The first <c>#version</c> line.
    /// </summary>
    [GeneratedRegex(@"^[ \t]*#[ \t]*version[^\r\n]*", RegexOptions.Multiline)]
    private static partial Regex VersionRegex();

    /// <summary>
    /// The shaderc API from the library this assembly ships, or the default lookup when it is not found there.
    /// </summary>
    static Shaderc LoadApi()
    {
        // With the assembly given, .NET resolves the name through deps.json (runtimes/<rid>/native) or beside the assembly; PATH is not searched.
        if (NativeLibrary.TryLoad("shaderc_shared", typeof(Shaderc).Assembly, DllImportSearchPath.AssemblyDirectory, out nint lib))
            return new Shaderc(new LamdaNativeContext(name => NativeLibrary.TryGetExport(lib, name, out nint p) ? p : 0));

        return Shaderc.GetApi();
    }

    /// <summary>
    /// Compiles both stages, then stores the pair in the disk cache when it is on.
    /// </summary>
    CompiledProgram CompileFresh(string vs, string fs, ShaderCompileOptions options, string dir, string key)
    {
        byte[] v = CompileStage(vs, ShaderKind.VertexShader, "vertex", options.VertexBindingBase);
        byte[] f = CompileStage(fs, ShaderKind.FragmentShader, "fragment", options.FragmentBindingBase);
        var program = new CompiledProgram(v, f, vs, fs, ProgramSource.Compiled);
        if (options.UseDiskCache) ShaderCache.Write(dir, key, CacheVersion, v, f);

        return program;
    }

    /// <summary>
    /// One native stage's SPIR-V: from the memory cache, the disk cache or compiled now (strict rules, no binding shift).
    /// </summary>
    byte[] CompileNativeStage(string glsl, ShaderKind kind, string stage, ShaderCompileOptions? options)
    {
        ArgumentNullException.ThrowIfNull(glsl);
        options ??= DefaultOptions;
        string key = ShaderCache.Key(stage + ":native", glsl, options, NativeCacheVersion);
        if (options.UseMemoryCache && _nativeMemory.TryGetValue(key, out var hit)) return hit;

        string dir = options.CacheDirectory ?? ShaderCache.DefaultDirectory;
        byte[] spirv;
        if (options.UseDiskCache && ShaderCache.TryRead(dir, key, NativeCacheVersion, out var cached, out _))
        {
            spirv = cached;
        }
        else
        {
            spirv = CompileStage(Define(glsl, options), kind, stage, 0, legacy: false);

            // The cache file holds a pair; a single stage is stored twice.
            if (options.UseDiskCache) ShaderCache.Write(dir, key, NativeCacheVersion, spirv, spirv);
        }

        if (options.UseMemoryCache) _nativeMemory[key] = spirv;
        return spirv;
    }

    /// <summary>
    /// Compiles one stage with shaderc for Vulkan 1.3 (relaxed rules, auto-bound uniforms and a forced GLSL 450 core profile when
    /// <paramref name="legacy"/>), unoptimised so names survive.
    /// </summary>
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

            return CompileToSpirv(api, compiler, opts, source, kind, stage, bindingBase);
        }
        finally
        {
            api.CompileOptionsRelease(opts);
            api.CompilerRelease(compiler);
        }
    }

    /// <summary>
    /// Compiles the source into SPIR-V and shifts its bindings by <paramref name="bindingBase"/>; throws a <see cref="ShaderCompileException"/> with
    /// shaderc's log and the numbered source lines on failure.
    /// </summary>
    static unsafe byte[] CompileToSpirv(Shaderc api, Compiler* compiler, CompileOptions* opts, string source, ShaderKind kind, string stage,
        uint bindingBase)
    {
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
        finally
        {
            api.ResultRelease(result);
        }
    }

    /// <summary>
    /// A UTF-8 C string as a string ("" for null).
    /// </summary>
    static unsafe string Marshal(byte* text) => System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)text) ?? "";

    /// <summary>
    /// The source with line numbers: the lines shaderc complained about (plus two either side), or all of it when none are named.
    /// </summary>
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

    /// <summary>
    /// The line number in a shaderc error or warning (<c>:N: error</c>).
    /// </summary>
    [GeneratedRegex(@":(\d+): (?:error|warning)")]
    private static partial Regex ErrorLineRegex();
}
