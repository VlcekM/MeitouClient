using System.Text.RegularExpressions;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Shaders;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using Silk.NET.SPIRV;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// The MSL of one stage's descriptor set as a Metal argument buffer: one struct per set, its members in
/// declaration order (a member's <c>[[id(N)]]</c> names its binding, the position is its entry).
/// </summary>
internal sealed class MetalSetLayout
{
    /// <summary>
    /// The member ids of each set's struct, by set number, in declaration order.
    /// </summary>
    readonly Dictionary<uint, uint[]> _members = [];

    /// <summary>
    /// The set structs (<c>spvDescriptorSetBuffer</c>N) of a stage's MSL and their members' <c>[[id]]</c>s.
    /// </summary>
    public static MetalSetLayout Parse(string msl)
    {
        var layout = new MetalSetLayout();
        foreach (Match set in Regex.Matches(msl, @"struct\s+spvDescriptorSetBuffer(?<set>\d+)\s*\{(?<body>[^}]*)\}"))
        {
            layout._members[uint.Parse(set.Groups["set"].Value)] =
            [
                .. Regex.Matches(set.Groups["body"].Value, @"\[\[id\((?<id>\d+)\)\]\]").Select(m => uint.Parse(m.Groups["id"].Value)),
            ];
        }

        return layout;
    }

    /// <summary>
    /// The empty layout (a graphics program has no compute structs).
    /// </summary>
    public static readonly MetalSetLayout Empty = new();

    /// <summary>
    /// The set's member ids in declaration order (empty when the stage does not read the set).
    /// </summary>
    public uint[] Members(uint set) => _members.TryGetValue(set, out var ids) ? ids : [];

    public override string ToString() => string.Join("; ", _members.OrderBy(m => m.Key).Select(m => $"{m.Key}: [{string.Join(",", m.Value)}]"));
}

/// <summary>
/// The backend's own artifacts of a program (the twin of <c>VulkanProgram</c>): the MSL libraries and
/// functions. The engine's compile result (<see cref="ProgramInfo"/>) lives on the handle.
/// </summary>
internal sealed class MetalProgram : IDisposable
{
    /// <summary>
    /// A graphics program: the two stages' libraries, functions and MSL, with the table and set layouts read from the MSL.
    /// </summary>
    public MetalProgram(ProgramInfo info, MTLLibrary vertexLibrary, MTLFunction vertexFunction, string vertexMsl,
        MTLLibrary fragmentLibrary, MTLFunction fragmentFunction, string fragmentMsl, bool argumentBuffers)
    {
        ArgumentBuffers = argumentBuffers;
        Info = info;
        (VertexLibrary, VertexFunction, VertexMsl) = (vertexLibrary, vertexFunction, vertexMsl);
        (FragmentLibrary, FragmentFunction, FragmentMsl) = (fragmentLibrary, fragmentFunction, fragmentMsl);
        (VertexTable, FragmentTable) = (MetalTableLayout.Parse(vertexMsl), MetalTableLayout.Parse(fragmentMsl));
        (VertexSets, FragmentSets) = (MetalSetLayout.Parse(vertexMsl), MetalSetLayout.Parse(fragmentMsl));

    }

    /// <summary>
    /// A compute program: one entry point, its set structs and the threadgroup size the MSL was compiled with.
    /// </summary>
    public MetalProgram(ProgramInfo info, MTLLibrary computeLibrary, MTLFunction computeFunction, string computeMsl, (uint X, uint Y, uint Z) threads)
    {
        ArgumentBuffers = true;
        Info = info;
        (ComputeLibrary, ComputeFunction, ComputeMsl, Threads) = (computeLibrary, computeFunction, computeMsl, threads);
        ComputeTable = MetalTableLayout.Parse(computeMsl);
        ComputeSets = MetalSetLayout.Parse(computeMsl);
    }

    /// <summary>
    /// The engine's compile result.
    /// </summary>
    public ProgramInfo Info { get; }
    // A compute program has no graphics stage (the handles stay default); the graphics constructor fills them.
    /// <summary>
    /// The vertex stage's library.
    /// </summary>
    public MTLLibrary VertexLibrary { get; }
    /// <summary>
    /// The vertex stage's entry point.
    /// </summary>
    public MTLFunction VertexFunction { get; }
    /// <summary>
    /// The vertex stage's MSL (diagnostics: what SPIR-V became).
    /// </summary>
    public string VertexMsl { get; } = "";
    /// <summary>
    /// The fragment stage's library.
    /// </summary>
    public MTLLibrary FragmentLibrary { get; }
    /// <summary>
    /// The fragment stage's entry point.
    /// </summary>
    public MTLFunction FragmentFunction { get; }
    /// <summary>
    /// The fragment stage's MSL (diagnostics: what SPIR-V became).
    /// </summary>
    public string FragmentMsl { get; } = "";
    /// <summary>
    /// The stage's argument buffer layout (empty for a stage that does not read the table).
    /// </summary>
    public MetalTableLayout VertexTable { get; } = MetalTableLayout.Empty;
    /// <summary>
    /// The fragment stage's argument buffer layout (empty when it does not read the table).
    /// </summary>
    public MetalTableLayout FragmentTable { get; } = MetalTableLayout.Empty;
    /// <summary>
    /// The stage's descriptor-set member ids, in declaration order (Metal lays a struct's members out by
    /// declaration, not by their <c>[[id]]</c>: the position is what the runtime writes).
    /// </summary>
    public MetalSetLayout VertexSets { get; } = MetalSetLayout.Empty;
    /// <summary>
    /// The fragment stage's descriptor-set member ids, in declaration order.
    /// </summary>
    public MetalSetLayout FragmentSets { get; } = MetalSetLayout.Empty;
    /// <summary>
    /// Whether the program's descriptor sets are Metal argument buffers (every native program with the bindless table).
    /// </summary>
    public bool ArgumentBuffers { get; }
    /// <summary>
    /// The compute stage's library (default for a graphics program).
    /// </summary>
    public MTLLibrary ComputeLibrary { get; }
    /// <summary>
    /// The compute stage's entry point (default for a graphics program).
    /// </summary>
    public MTLFunction ComputeFunction { get; }
    /// <summary>
    /// The compute stage's MSL (diagnostics).
    /// </summary>
    public string ComputeMsl { get; } = "";
    /// <summary>
    /// The threadgroup size the MSL declares (the GLSL's <c>local_size_x/y/z</c>), for a dispatch.
    /// </summary>
    public (uint X, uint Y, uint Z) Threads { get; }
    /// <summary>
    /// The compute stage's argument buffer layout.
    /// </summary>
    public MetalTableLayout ComputeTable { get; } = MetalTableLayout.Empty;
    /// <summary>
    /// The compute stage's descriptor-set member ids, in declaration order.
    /// </summary>
    public MetalSetLayout ComputeSets { get; } = MetalSetLayout.Empty;
    /// <summary>
    /// Whether this is a compute program.
    /// </summary>
    public bool IsCompute => ComputeFunction.NativePtr != IntPtr.Zero;

    /// <summary>
    /// Releases the libraries and functions the program has.
    /// </summary>
    public void Dispose()
    {
        if (VertexFunction.NativePtr != IntPtr.Zero) VertexFunction.Dispose();
        if (VertexLibrary.NativePtr != IntPtr.Zero) VertexLibrary.Dispose();
        if (FragmentFunction.NativePtr != IntPtr.Zero) FragmentFunction.Dispose();
        if (FragmentLibrary.NativePtr != IntPtr.Zero) FragmentLibrary.Dispose();
        if (ComputeFunction.NativePtr != IntPtr.Zero) ComputeFunction.Dispose();
        if (ComputeLibrary.NativePtr != IntPtr.Zero) ComputeLibrary.Dispose();
    }
}

/// <summary>
/// The programs the engine's shaders compile to, on Metal: the engine's own compiler (shaderc) makes the
/// SPIR-V (the fragment stage's bindings at 0 — Metal's stages have their own binding tables, so the
/// Vulkan scheme's 32 offset is not needed), spirv-cross translates it to MSL in process, and Metal
/// compiles the source.
/// </summary>
public sealed class MetalShaders : IGpuShaders
{
    #region Binding layout

    /// <summary>
    /// Metal's stages have one binding table per kind (<c>[[buffer(n)]]</c>, <c>[[texture(n)]]</c>), and its mapping is by
    /// the binding decoration alone — set included, since sets are not namespaced (docs/metal-backend.md). Every resource's
    /// binding flattens to base + set × stride + binding, both in the SPIR-V before the translation and in the runtime
    /// binds (<see cref="MetalCommandList"/>), so the two agree. The vertex stage's base leaves the indices below it to the
    /// vertex buffers (the pipeline's slots, Vulkan's own numbers: <see cref="MetalPipelines.VertexDescriptor"/>); its top
    /// index belongs to the push constants (<see cref="MslTranslator.PushConstantBinding"/>).
    /// </summary>
    internal const uint SetStride = 8;
    /// <summary>
    /// The vertex stage's first buffer index for blocks; the indices below it are the vertex buffers'.
    /// </summary>
    internal const uint VertexBlockBase = 16;

    /// <summary>
    /// A slot's index in its stage's MSL table. The bases are per kind (Metal has one table per kind and only the
    /// buffer tables carry the vertex buffers): a vertex module's buffers shift by <see cref="VertexBlockBase"/>, its images
    /// and samplers — and the fragment stage's everything — flatten from 0.
    /// </summary>
    internal static uint Flatten(GpuSlotKind kind, GpuStage stages, uint set, uint binding) =>
        (stages.HasFlag(GpuStage.Vertex) && kind is GpuSlotKind.UniformBuffer or GpuSlotKind.StorageBuffer ? VertexBlockBase : 0)
        + set * SetStride + binding;

    #endregion

    #region Bindless table (docs/metal-backend.md, "the bindless table")

    /// <summary>
    /// The Metal table's capacities per kind (<see cref="BindlessKind"/> order). The rewrite sizes the argument
    /// buffer's arrays with them so the ids stay in order and within the argument buffer's limits; the runtime's table
    /// mirrors them (<see cref="MetalBindlessTable"/>). 2D textures get most of the room, as the Vulkan table's shares do.
    /// </summary>
    internal static readonly uint[] TableCapacity = [2048, 512, 128, 128, 128, 128, 64];

    /// <summary>
    /// The argument buffer's members' offsets, in entries (8 bytes each), by kind: the arrays follow one another in
    /// binding order (every texture array, then every sampler array). Metal lays the members out sequentially from 0 — the
    /// <c>[[id]]</c> decorations the MSL carries do not move them (verified with the pipeline reflection).
    /// </summary>
    static readonly uint[] TablePrefix = Prefix();

    /// <summary>
    /// All the texture arrays' total: where the sampler arrays begin.
    /// </summary>
    internal static readonly uint TableTotal = Total();

    /// <summary>
    /// The argument buffer entry of kind <paramref name="kind"/>'s first texture (an entry is 8 bytes).
    /// </summary>
    internal static uint TableTextureId(int kind) => TablePrefix[kind];

    /// <summary>
    /// The argument buffer's id of kind <paramref name="kind"/>'s first sampler (the sampler arrays come after all the textures).
    /// </summary>
    internal static uint TableSamplerId(int kind) => TableTotal + TableTextureId(kind);

    /// <summary>
    /// The MSL's <c>[[id]]</c> of a kind's first texture array and first sampler array (the set's base plus the binding).
    /// </summary>
    internal static uint TableTextureBinding(int kind) => (uint)(SetStride * Frame.NativeShaders.BindlessSet) + TablePrefix[kind];
    /// <summary>
    /// The MSL's <c>[[id]]</c> of a kind's first sampler array: its texture array's plus <see cref="TableTotal"/>.
    /// </summary>
    internal static uint TableSamplerBinding(int kind) => TableTextureBinding(kind) + TableTotal;

    /// <summary>
    /// Which array a member at an argument buffer id is: its kind and whether it is the sampler array (null: not the table's).
    /// </summary>
    internal static (int Kind, bool Sampler)? TableKindOf(uint id)
    {
        for (int k = 0; k < TableCapacity.Length; k++)
        {
            if (id == TableTextureBinding(k)) return (k, false);
            if (id == TableSamplerBinding(k)) return (k, true);
        }
        return null;
    }

    /// <summary>
    /// Whether a kind's entries have a sampler array in the MSL (every kind but the integer ones: their fetches never read one).
    /// </summary>
    internal static bool HasSamplers(int kind) => kind != (int)BindlessKind.UintTexture2D && kind != (int)BindlessKind.IntTexture2D;

    /// <summary>
    /// The Metal form of a native program's bindless set: every array the shared text declares unsized (<see cref="BindlessTable.Declarations"/>)
    /// gets the table's capacity. spirv-cross has no place for runtime-sized combined arrays in an argument buffer, and GLSL has no
    /// separate integer or depth texture type (so the float kinds could only be split at their uses, where GLSL's "a sampler constructor
    /// stands at the point of use" rule breaks the functions that take one as a parameter). Instead the SPIR-V patch
    /// (<see cref="SpirvPatch.BindlessFetchImages"/>) splits every combined array into a plain image array and a parallel sampler array
    /// (the integer kinds have no sampler: their fetches never read one): the MSL gets separate arrays and per-entry samplers, and the
    /// use sites — loads, compares, the functions that pass a sampler on — take the two members they need.
    /// </summary>
    static string RewriteBindless(string glsl)
    {
        uint[] cap = TableCapacity, prefix = TablePrefix;
        for (int k = 0; k < cap.Length; k++)
        {
            string decl = $"layout(set = {Frame.NativeShaders.BindlessSet}, binding = {k}) uniform " +
                $"{BindlessTable.GlslTypes[k]} {BindlessTable.GlslArrays[k]}[];";

            // A stage that does not include the table's text (the host's test programs) has nothing to rewrite.
            if (!glsl.Contains(decl)) continue;
            string sized = $"layout(set = {Frame.NativeShaders.BindlessSet}, binding = {prefix[k]}) uniform " +
                $"{BindlessTable.GlslTypes[k]} {BindlessTable.GlslArrays[k]}[{cap[k]}];";
            glsl = glsl.Replace(decl, sized);
        }
        return glsl;
    }

    /// <summary>
    /// Each kind's first entry in the argument buffer: the running sum of <see cref="TableCapacity"/>.
    /// </summary>
    static uint[] Prefix()
    {
        var prefix = new uint[TableCapacity.Length];
        uint sum = 0;
        for (int k = 0; k < prefix.Length; k++)
        {
            prefix[k] = sum;
            sum += TableCapacity[k];
        }

        return prefix;
    }

    /// <summary>
    /// The sum of <see cref="TableCapacity"/>.
    /// </summary>
    static uint Total()
    {
        uint total = 0;
        foreach (uint c in TableCapacity) total += c;
        return total;
    }

    #endregion

    #region Programs

    /// <summary>
    /// Diagnostic A/B: Metal's fast-math compile option (<c>MEITOU_METAL_FAST_MATH</c>). Unset (null) leaves Metal's default (which is fast math on);
    /// 1 and 0 force it on or off. The default must not be changed until the terrain and scene pictures have been compared.
    /// </summary>
    static readonly bool? FastMath = Environment.GetEnvironmentVariable("MEITOU_METAL_FAST_MATH") switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => null,
    };
    /// <summary>
    /// The device the libraries are compiled on.
    /// </summary>
    readonly MTLDevice _device;
    /// <summary>
    /// The programs by handle index.
    /// </summary>
    readonly List<MetalProgram> _programs = [];

    /// <summary>
    /// The shaded character cut-outs must reject alpha before committing depth (docs/metal-backend.md).
    /// </summary>
    public bool DiscardNeedsLateDepth => true;

    /// <summary>
    /// Shaders compiled on <paramref name="device"/>; logs the fast-math override when one is set.
    /// </summary>
    public MetalShaders(MTLDevice device)
    {
        this._device = device;
        if (FastMath is { } fast) Console.WriteLine($"metal     shaders fast math {fast.ToString().ToLowerInvariant()} (MEITOU_METAL_FAST_MATH)");
    }

    /// <summary>
    /// The compiler the programs go through: Metal's own options (per-stage bindings at 0, no cache shared with Vulkan's; no
    /// <c>DepthReversed</c>: Metal's scene depth is conventional).
    /// </summary>
    public GlslProgramCompiler Compiler { get; } = new(new ShaderCompileOptions { FragmentBindingBase = 0, UseDiskCache = false });

    /// <summary>
    /// The backend's identity of a program handle (the internals resolve through this).
    /// </summary>
    internal MetalProgram Backend(GpuProgram program) => _programs[program.Index];

    /// <inheritdoc/>
    public GpuProgram Legacy(string vertexGlsl, string fragmentGlsl, string name)
    {
        var c = Compiler.Compile(vertexGlsl, fragmentGlsl);

        // The default blocks move to set 1 (vertex binding 0, fragment binding 1), as the Vulkan path's do (SpirvPatch, VkGl's step);
        // then both stages' bindings flatten onto Metal's one table per stage (the vertex stage's blocks above the vertex buffers).
        byte[] v = SpirvPatch.RemapBindings(c.Vertex.DefaultBlock is null ? c.VertexSpirv : SpirvPatch.MoveDefaultBlock(c.VertexSpirv, 0),
            VertexBlockBase, 0, SetStride);
        byte[] f = SpirvPatch.RemapBindings(c.Fragment.DefaultBlock is null ? c.FragmentSpirv : SpirvPatch.MoveDefaultBlock(c.FragmentSpirv, 1), 0, 0,
            SetStride);

        if (Environment.GetEnvironmentVariable("MEITOU_METAL_DUMP") is { } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"{name}.vert.spv"), v);
            File.WriteAllBytes(Path.Combine(dir, $"{name}.frag.spv"), f);
        }

        // The program's sets in the engine's words, as ShaderLibrary.Legacy builds them for Vulkan — off Metal's own reflection.
        // The slots carry the engine's numbers (the guests bind by them); MetalCommandList flattens at the binds.
        var slots = new List<GpuSlot>();
        void AddSlots(ShaderReflection r, GpuStage stage)
        {
            foreach (var b in r.Blocks)
                if (b.Kind == BlockKind.Uniform && !b.IsDefault) slots.Add(new GpuSlot((uint)b.Binding, GpuSlotKind.UniformBuffer, stage));
            foreach (var s in r.Samplers) slots.Add(new GpuSlot((uint)s.Binding, GpuSlotKind.CombinedSampler, stage,
                    (uint)Math.Max(1, s.ArrayLength)));
        }
        AddSlots(c.Vertex, GpuStage.Vertex);
        AddSlots(c.Fragment, GpuStage.Fragment);
        var defaultSlots = new List<GpuSlot>();
        if (c.Vertex.DefaultBlock is not null) defaultSlots.Add(new GpuSlot(0, GpuSlotKind.UniformBuffer, GpuStage.Vertex, 1, OffsetAtBind: true));
        if (c.Fragment.DefaultBlock is not null) defaultSlots.Add(new GpuSlot(1, GpuSlotKind.UniformBuffer, GpuStage.Fragment, 1,
                OffsetAtBind: true));
        var info = new ProgramInfo
        {
            Index = _programs.Count, Name = name, Model = ShaderModel.Legacy, VertexCode = v, FragmentCode = f, Compiled = c,
            VertexReflection = c.Vertex, FragmentReflection = c.Fragment,
            NeutralSets = [GpuSetLayout.Written([.. slots]), GpuSetLayout.Fixed([.. defaultSlots])],
        };

        // One library per stage: spirv-cross names both entry points the same, so two stages in one source collide (docs/metal-backend.md).
        var vertexMsl = MslTranslator.Translate(v, "vertex_main", ExecutionModel.Vertex);
        var fragmentMsl = MslTranslator.Translate(f, "fragment_main", ExecutionModel.Fragment);
        if (Environment.GetEnvironmentVariable("MEITOU_METAL_DUMP") is { } dump)
        {
            Directory.CreateDirectory(dump);
            File.WriteAllText(Path.Combine(dump, $"{name}.vert.msl"), vertexMsl);
            File.WriteAllText(Path.Combine(dump, $"{name}.frag.msl"), fragmentMsl);
        }
        var vertexLibrary = Library(vertexMsl, name);
        var fragmentLibrary = Library(fragmentMsl, name);
        _programs.Add(new MetalProgram(info, vertexLibrary, Function(vertexLibrary, "vertex_main", name), vertexMsl,
            fragmentLibrary, Function(fragmentLibrary, "fragment_main", name), fragmentMsl, false));
        return new GpuProgram(info.Index, info);
    }

    /// <summary>
    /// A compute program: the engine's compiler makes the SPIR-V, spirv-cross the MSL (with every set an argument buffer, as the
    /// native model's), and the threadgroup size comes from the source's <c>local_size_x/y/z</c> (the dispatch passes it to Metal).
    /// </summary>
    public GpuProgram Compute(string glsl, string name, params GpuSetLayout[] extraSets)
    {
        var code = Compiler.CompileCompute(glsl);
        var r = SpirvReflection.Parse(code);
        uint pc = (uint)(r.Blocks.FirstOrDefault(b => b.Kind == BlockKind.PushConstant)?.Size ?? 0);
        var info = new ProgramInfo
        {
            Index = _programs.Count, Name = name, Model = ShaderModel.Compute, ComputeCode = code, ComputeReflection = r,
            PushConstantBytes = pc, NeutralSets = [GpuSetLayout.Written([]), .. extraSets],
        };
        var threads = LocalSize(glsl);
        var msl = MslTranslator.Translate(code, "compute_main", ExecutionModel.GLCompute, argumentBufferSet: Frame.NativeShaders.BindlessSet);
        if (Environment.GetEnvironmentVariable("MEITOU_METAL_DUMP") is { } dump)
        {
            Directory.CreateDirectory(dump);
            File.WriteAllBytes(Path.Combine(dump, $"{name}.comp.spv"), code);
            File.WriteAllText(Path.Combine(dump, $"{name}.comp.msl"), msl);
        }
        var library = Library(msl, name);
        _programs.Add(new MetalProgram(info, library, Function(library, "compute_main", name), msl, threads));
        return new GpuProgram(info.Index, info);
    }

    /// <summary>
    /// A native-model program: strict GLSL 450, the layout given by the caller (<paramref name="setLayouts"/>, not owned) and a
    /// push-constant block of <paramref name="pushConstantBytes"/> bytes, as ShaderLibrary.Native builds it for Vulkan. The
    /// stages' bindings flatten onto Metal's one table per stage before the translation, so the runtime binds at the same
    /// indices (<see cref="Flatten"/>, <see cref="MetalCommandList.BindSets"/>).
    /// </summary>
    public GpuProgram Native(string vertexGlsl, string fragmentGlsl, string name, GpuSetLayout[] setLayouts, uint pushConstantBytes)
    {
        if (pushConstantBytes > 128)
            throw new ArgumentOutOfRangeException(nameof(pushConstantBytes), "push constants are limited to 128 bytes (the guaranteed minimum)");

        var (vs, fs) = InterfaceLocations.Apply(vertexGlsl, fragmentGlsl);

        // The bindless set's runtime arrays are Metal 3 tier-2 argument buffers (docs/metal-backend.md); only a program
        // whose layout includes the table is translated with them, and only its shared text needs the rewrite.
        bool bindless = Array.Exists(setLayouts, l => ReferenceEquals(l, GpuSetLayout.Bindless));
        if (bindless)
        {
            vs = RewriteBindless(vs);
            fs = RewriteBindless(fs);
        }

        var v = SpirvPatch.RemapBindings(Compiler.CompileNative(vs, fragment: false), VertexBlockBase, 0, SetStride);
        if (bindless) v = SpirvPatch.BindlessFetchImages(v, Frame.NativeShaders.BindlessSet, TableTotal);
        var f = SpirvPatch.RemapBindings(Compiler.CompileNative(fs, fragment: true), 0, 0, SetStride);
        if (bindless) f = SpirvPatch.BindlessFetchImages(f, Frame.NativeShaders.BindlessSet, TableTotal);
        if (Environment.GetEnvironmentVariable("MEITOU_METAL_DUMP") is { } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"{name}.vert.spv"), v);
            File.WriteAllBytes(Path.Combine(dir, $"{name}.frag.spv"), f);
        }
        var rv = SpirvReflection.Parse(v);
        var rf = SpirvReflection.Parse(f);
        foreach (var block in rv.Blocks.Concat(rf.Blocks))
        {
            if (block.Kind == BlockKind.PushConstant && block.Size > pushConstantBytes)
            {
                throw new InvalidOperationException(
                    $"{name}: push-constant block '{block.Name}' is {block.Size} bytes, the layout's range {pushConstantBytes}");
            }
        }

        var info = new ProgramInfo
        {
            Index = _programs.Count, Name = name, Model = ShaderModel.Native, VertexCode = v, FragmentCode = f,
            VertexReflection = rv, FragmentReflection = rf, PushConstantBytes = pushConstantBytes, NeutralSets = setLayouts,
        };
        uint? tableSet = bindless ? Frame.NativeShaders.BindlessSet : null;
        var vertexMsl = MslTranslator.Translate(v, "vertex_main", ExecutionModel.Vertex, argumentBufferSet: tableSet);
        var fragmentMsl = MslTranslator.Translate(f, "fragment_main", ExecutionModel.Fragment, argumentBufferSet: tableSet);
        if (Environment.GetEnvironmentVariable("MEITOU_METAL_DUMP") is { } dump)
        {
            Directory.CreateDirectory(dump);
            File.WriteAllText(Path.Combine(dump, $"{name}.vert.msl"), vertexMsl);
            File.WriteAllText(Path.Combine(dump, $"{name}.frag.msl"), fragmentMsl);
        }
        var vertexLibrary = Library(vertexMsl, name);
        var fragmentLibrary = Library(fragmentMsl, name);
        _programs.Add(new MetalProgram(info, vertexLibrary, Function(vertexLibrary, "vertex_main", name), vertexMsl,
            fragmentLibrary, Function(fragmentLibrary, "fragment_main", name), fragmentMsl, bindless));
        return new GpuProgram(info.Index, info);
    }

    /// <summary>
    /// Throws: Metal has no tessellation shader stages.
    /// </summary>
    public GpuProgram NativeTessellated(string vertexGlsl, string controlGlsl, string evaluationGlsl, string fragmentGlsl, string name,
        GpuSetLayout[] setLayouts, uint pushConstantBytes) =>
        throw new NotSupportedException($"{name}: Metal has no tessellation shader stages");

    /// <summary>
    /// Releases a program's libraries and functions.
    /// </summary>
    public void Dispose(GpuProgram program) => Backend(program).Dispose();

    /// <summary>
    /// The GLSL's threadgroup size (GLSL makes it mandatory; 1 for an axis the source does not name).
    /// </summary>
    static (uint X, uint Y, uint Z) LocalSize(string glsl)
    {
        static uint Of(string source, string axis)
        {
            var m = Regex.Match(source, axis + @"\s*=\s*(\d+)");
            return m.Success ? uint.Parse(m.Groups[1].Value) : 1;
        }
        return (Of(glsl, "local_size_x"), Of(glsl, "local_size_y"), Of(glsl, "local_size_z"));
    }

    /// <summary>
    /// Compiles MSL into a library (with the fast-math override); throws with Metal's error when it does not compile.
    /// </summary>
    MTLLibrary Library(string msl, string name)
    {
        NSError error = default;
        var options = new MTLCompileOptions();
        if (FastMath is { } fast) options.FastMathEnabled = fast;
        var library = _device.NewLibrary((NSString)msl, options, ref error);
        if (error.NativePtr != IntPtr.Zero)
            throw new InvalidOperationException($"{name}: the MSL did not compile: {error.LocalizedDescription}");
        return library;
    }

    /// <summary>
    /// An entry point of a library; throws when it has none of that name.
    /// </summary>
    static MTLFunction Function(MTLLibrary library, string entry, string name)
    {
        var function = library.NewFunction((NSString)entry);
        if (function.NativePtr == IntPtr.Zero) throw new InvalidOperationException($"{name}: no function {entry} in the MSL");
        return function;
    }

    #endregion
}
