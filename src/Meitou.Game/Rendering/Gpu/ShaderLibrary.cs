using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Gpu.Shaders;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// The descriptor model a <see cref="GpuProgram"/> follows (docs/renderer-native.md 3).
/// </summary>
public enum ShaderModel
{
    /// <summary>
    /// GL-shaped: loose uniforms in default blocks (set 1, dynamic offsets), blocks and samplers at glslang's bindings in set 0.
    /// Byte-identical to what VkGl builds (step P).
    /// </summary>
    Legacy,

    /// <summary>
    /// Explicit sets, push constants, bindless (step O and new code).
    /// </summary>
    Native,
    /// <summary>
    /// A compute program: set 0 from the reflection (pushed when it fits) and a push-constant range.
    /// </summary>
    Compute,
}

/// <summary>
/// Patches applied to compiled SPIR-V (shared by VkGl and the native API).
/// </summary>
public static class SpirvPatch
{
    /// <summary>
    /// A copy of <paramref name="spirv"/> with <c>gl_DefaultUniformBlock</c>'s variable decorated for set 1 at <paramref name="binding"/>
    /// (the words are patched in place: same size). VkGl's <c>LinkProgram</c> step, moved here.
    /// </summary>
    public static byte[] MoveDefaultBlock(byte[] spirv, uint binding)
    {
        var copy = (byte[])spirv.Clone();
        var w = MemoryMarshal.Cast<byte, uint>(copy.AsSpan());
        uint structId = 0;
        var pointers = new HashSet<uint>();
        var variables = new HashSet<uint>();
        for (int pass = 0; pass < 3; pass++)
        {
            for (int i = 5; i < w.Length;)
            {
                uint op = w[i] & 0xFFFF, count = w[i] >> 16;
                if (count == 0) break;
                var a = w.Slice(i + 1, (int)count - 1);
                if (pass == 0 && op == 5 && IsDefaultBlockName(a))
                    structId = a[0];
                else if (pass == 1 && op == 32 && a[2] == structId) pointers.Add(a[0]);
                else if (pass == 1 && op == 59 && pointers.Contains(a[0])) variables.Add(a[1]);
                else if (pass == 2 && op == 71 && variables.Contains(a[0]))
                {
                    if (a[1] == 34) a[2] = 1;              // DescriptorSet
                    else if (a[1] == 33) a[2] = binding;   // Binding
                }

                i += (int)count;
            }
        }

        if (structId == 0 || variables.Count == 0)
            throw new InvalidOperationException("SPIR-V: gl_DefaultUniformBlock not found");
        return copy;
    }

    /// <summary>
    /// Whether an OpName's words name <c>gl_DefaultUniformBlock</c> (the name is the string after the id).
    /// </summary>
    static bool IsDefaultBlockName(Span<uint> words) =>
        System.Text.Encoding.ASCII.GetString(MemoryMarshal.AsBytes(words[1..])).TrimEnd('\0') == "gl_DefaultUniformBlock";

    /// <summary>
    /// Flattens a module's (set, binding) pairs onto one binding table, as Metal's stages have (docs/metal-backend.md): every
    /// resource's binding decoration becomes its base + set × <paramref name="stride"/> + binding, set included. Metal maps a
    /// resource to its MSL index by the binding decoration alone (<c>MslEnableDecorationBinding</c>), so two sets with the same
    /// binding number would otherwise land on one <c>[[buffer(n)]]</c>. The bases are per kind: Metal's stages have one table per
    /// kind (<c>[[buffer]]</c>, <c>[[texture]]</c>, <c>[[sampler]]</c>), and only the buffer tables carry the vertex buffers — so a
    /// vertex module's buffer bindings shift by <paramref name="bufferBase"/> while its images and samplers stay at
    /// <paramref name="otherBase"/> (0). Push constants carry no binding and stay untouched (they take
    /// <see cref="Metal.MslTranslator.PushConstantBinding"/>).
    /// </summary>
    public static byte[] RemapBindings(byte[] spirv, uint bufferBase, uint otherBase, uint stride)
    {
        var copy = (byte[])spirv.Clone();
        var w = MemoryMarshal.Cast<byte, uint>(copy.AsSpan());
        var sets = new Dictionary<uint, uint>();
        var storage = new Dictionary<uint, uint>();
        var bindings = new List<(int At, uint Variable)>();

        // Two passes: SPIR-V's annotations (the decorations) come before the global variables in the binary, so the
        // variables' storage classes must be known before the first decoration is remapped.
        for (int i = 5; i < w.Length;)
        {
            uint op = w[i] & 0xFFFF, count = w[i] >> 16;
            if (count == 0) break;
            var a = w.Slice(i + 1, (int)count - 1);
            if (op == 71 && count == 4)
            {
                if (a[1] == 34) sets[a[0]] = a[2];                // DescriptorSet
                else if (a[1] == 33) bindings.Add((i + 3, a[0])); // Binding
            }
            else if (op == 59 && count >= 4) storage[a[1]] = a[2]; // Variable: result type, id, storage class
            i += (int)count;
        }
        foreach (var (at, variable) in bindings)
        {
            // A decorated variable is Uniform (2) or StorageBuffer (12) for buffers, UniformConstant (0) for images and samplers.
            uint sc = storage.GetValueOrDefault(variable);
            uint baseBinding = sc is 2 or 12 ? bufferBase : otherBase;
            w[at] = baseBinding + sets.GetValueOrDefault(variable) * stride + w[at];
        }
        return copy;
    }

    /// <summary>
    /// The bindless set's combined arrays become plain image arrays with a sampler array beside each (the integer kinds have no sampler: their
    /// fetches never read one) — what spirv-cross's Metal argument buffer needs and GLSL cannot express (no separate integer or depth texture
    /// types). Every access chain, load and function that takes a sampler along is rewritten: a sample loads the image and its sampler from the
    /// parallel arrays with the same index and pairs them again, a function gains one sampler parameter, a call one argument.
    /// <paramref name="set"/> is the table's set; <paramref name="samplerOffset"/> is where the sampler arrays' bindings begin above the
    /// texture arrays' (the backend's table total).
    /// </summary>
    public static byte[] BindlessFetchImages(byte[] spirv, uint set, uint samplerOffset) =>
        new BindlessImageSplit(spirv, set, samplerOffset).Run();
}

/// <summary>
/// Compiled shader modules with their reflection and pipeline layout. Legacy programs: the modules VkGl would create (same SPIR-V, default
/// blocks moved to set 1) and VkGl's layout (set 0 pushed when it fits, set 1 dynamic uniform buffers). Compute programs: set 0 from the
/// reflection (pushed when it fits) and a push-constant range. Native programs: the layout the caller's model gives.
/// </summary>
internal sealed unsafe class VulkanProgram : IDisposable
{
    #region State

    /// <summary>
    /// The device the modules and layouts are made on.
    /// </summary>
    readonly VulkanDevice _device;
    /// <summary>
    /// The set layouts made for this program alone, destroyed with it (shared ones are the library's).
    /// </summary>
    readonly DescriptorSetLayout[] _ownedSetLayouts;

    #endregion

    #region Construction

    /// <summary>
    /// Wraps the layouts and makes a shader module for each stage the program has.
    /// </summary>
    internal VulkanProgram(VulkanDevice device, ProgramInfo info, DescriptorSetLayout[] setLayouts, DescriptorSetLayout[] ownedSetLayouts,
        PipelineLayout layout, bool pushDescriptors, ShaderStageFlags pushConstantStages)
    {
        this._device = device;
        Info = info;
        SetLayouts = setLayouts;
        this._ownedSetLayouts = ownedSetLayouts;
        Layout = layout;
        PushDescriptors = pushDescriptors;
        PushConstantStages = pushConstantStages;
        if (info.VertexCode is not null) VertexModule = CreateModule(info.VertexCode);
        if (info.FragmentCode is not null) FragmentModule = CreateModule(info.FragmentCode);
        if (info.TessControlCode is not null) TessControlModule = CreateModule(info.TessControlCode);
        if (info.TessEvaluationCode is not null) TessEvaluationModule = CreateModule(info.TessEvaluationCode);
        if (info.ComputeCode is not null) ComputeModule = CreateModule(info.ComputeCode);
    }

    #endregion

    #region Properties



    /// <summary>
    /// The engine's compile result.
    /// </summary>
    public ProgramInfo Info { get; }
    /// <summary>
    /// <see cref="ProgramInfo.Index"/>.
    /// </summary>
    public int Index => Info.Index;
    /// <summary>
    /// <see cref="ProgramInfo.Name"/>.
    /// </summary>
    public string Name => Info.Name;
    /// <summary>
    /// <see cref="ProgramInfo.Model"/>.
    /// </summary>
    public ShaderModel Model => Info.Model;
    /// <summary>
    /// <see cref="ProgramInfo.VertexCode"/>.
    /// </summary>
    public byte[]? VertexCode => Info.VertexCode;
    /// <summary>
    /// <see cref="ProgramInfo.FragmentCode"/>.
    /// </summary>
    public byte[]? FragmentCode => Info.FragmentCode;
    /// <summary>
    /// <see cref="ProgramInfo.ComputeCode"/>.
    /// </summary>
    public byte[]? ComputeCode => Info.ComputeCode;
    /// <summary>
    /// The vertex stage's module (0 without the stage).
    /// </summary>
    public ShaderModule VertexModule { get; }
    /// <summary>
    /// The fragment stage's module (0 without the stage).
    /// </summary>
    public ShaderModule FragmentModule { get; }
    /// <summary>
    /// The compute stage's module (0 for a graphics program).
    /// </summary>
    public ShaderModule ComputeModule { get; }
    #endregion

    #region Modules

    /// <summary>
    /// The tessellation stages of a native program made by <see cref="ShaderLibrary.NativeTessellated"/> (0 otherwise): its pipelines
    /// draw patch lists of three control points.
    /// </summary>
    public ShaderModule TessControlModule { get; }
    /// <summary>
    /// The tessellation evaluation stage's module (0 without tessellation).
    /// </summary>
    public ShaderModule TessEvaluationModule { get; }
    /// <summary>
    /// Whether the program has tessellation stages.
    /// </summary>
    public bool Tessellated => TessEvaluationModule.Handle != 0;

    /// <summary>
    /// The legacy compile result (reflection of both stages, default blocks), null for other models.
    /// </summary>
    public CompiledProgram? Compiled => Info.Compiled;
    /// <summary>
    /// <see cref="ProgramInfo.VertexReflection"/>.
    /// </summary>
    public ShaderReflection? VertexReflection => Info.VertexReflection;
    /// <summary>
    /// <see cref="ProgramInfo.FragmentReflection"/>.
    /// </summary>
    public ShaderReflection? FragmentReflection => Info.FragmentReflection;
    /// <summary>
    /// <see cref="ProgramInfo.ComputeReflection"/>.
    /// </summary>
    public ShaderReflection? ComputeReflection => Info.ComputeReflection;
    /// <summary>
    /// The Vulkan set layouts, one per set.
    /// </summary>
    public DescriptorSetLayout[] SetLayouts { get; }

    /// <summary>
    /// The program's sets in the engine's words (the layout the backend made).
    /// </summary>
    public GpuSetLayout[] NeutralSets => Info.NeutralSets;
    /// <summary>
    /// The pipeline layout.
    /// </summary>
    public PipelineLayout Layout { get; }

    /// <summary>
    /// Set 0 is a push-descriptor set.
    /// </summary>
    public bool PushDescriptors { get; }
    /// <summary>
    /// <see cref="ProgramInfo.PushConstantBytes"/>.
    /// </summary>
    public uint PushConstantBytes => Info.PushConstantBytes;
    /// <summary>
    /// The stages the push-constant range covers.
    /// </summary>
    public ShaderStageFlags PushConstantStages { get; }

    /// <summary>
    /// The GLSL of the fragment stage as compiled, kept for the native model only (the bench's instrumented variant is made from it).
    /// </summary>
    public string? FragmentSource { get => Info.FragmentSource; set => Info.FragmentSource = value; }

    #endregion

    #region Lifetime

    /// <summary>
    /// A shader module of this device from <paramref name="spirv"/>; the caller destroys it.
    /// </summary>
    internal ShaderModule CreateModule(byte[] spirv)
    {
        fixed (byte* code = spirv)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code,
            };
            VulkanException.Check(_device.Vk.CreateShaderModule(_device.Device, &info, null, out var module), "vkCreateShaderModule");
            return module;
        }
    }

    /// <summary>
    /// Destroys the modules, the pipeline layout and the program's own set layouts once the frames in flight are done.
    /// </summary>
    public void Dispose()
    {
        var (vk, dev) = (_device.Vk, _device.Device);
        var (vm, fm, cm, layout, sets) = (VertexModule, FragmentModule, ComputeModule, Layout, _ownedSetLayouts);
        var (tcm, tem) = (TessControlModule, TessEvaluationModule);
        _device.Frames.DeferDelete(() =>
        {
            if (tcm.Handle != 0) vk.DestroyShaderModule(dev, tcm, null);
            if (tem.Handle != 0) vk.DestroyShaderModule(dev, tem, null);
            if (vm.Handle != 0) vk.DestroyShaderModule(dev, vm, null);
            if (fm.Handle != 0) vk.DestroyShaderModule(dev, fm, null);
            if (cm.Handle != 0) vk.DestroyShaderModule(dev, cm, null);
            if (layout.Handle != 0) vk.DestroyPipelineLayout(dev, layout, null);
            foreach (var s in sets) vk.DestroyDescriptorSetLayout(dev, s, null);
        });
    }

    #endregion
}

/// <summary>
/// Compiles programs for the three models (docs/renderer-native.md 3). The legacy path is the one VkGl uses (VkGl compiles through
/// <see cref="Compiler"/> too), so a legacy program's modules are byte-identical to VkGl's.
/// </summary>
public sealed unsafe class ShaderLibrary : IGpuShaders
{
    #region State

    /// <summary>
    /// The device the programs are made on.
    /// </summary>
    readonly VulkanDevice _device;
    /// <summary>
    /// The programs by handle index.
    /// </summary>
    readonly List<VulkanProgram> _programs = [];

    /// <summary>
    /// The layouts the engine's sets were made into (one per set, shared by every program that uses it).
    /// </summary>
    readonly Dictionary<GpuSetLayout, DescriptorSetLayout> _setLayouts = [];

    #endregion

    #region Programs

    /// <summary>
    /// A library on the device, compiling with reversed scene depth (<c>DepthReversed</c>).
    /// </summary>
    public ShaderLibrary(VulkanDevice device)
    {
        this._device = device;
        Compiler = new GlslProgramCompiler(new ShaderCompileOptions { DepthReversed = true });
    }

    /// <summary>
    /// The backend's identity of a program handle (the Vulkan-only internals resolve through this).
    /// </summary>
    internal VulkanProgram Backend(GpuProgram program) => _programs[program.Index];

    /// <summary>
    /// Releases a program (its backend artifacts go once the frames in flight are done); the index is never reused.
    /// </summary>
    public void Dispose(GpuProgram program) => Backend(program).Dispose();

    /// <summary>
    /// The compiler shared with VkGl (same options, same memory cache).
    /// </summary>
    public GlslProgramCompiler Compiler { get; }

    #endregion

    #region Legacy

    /// <summary>
    /// The modules VkGl would make for this source pair: compiled, then each default block moved to set 1 (vertex binding 0, fragment binding 1).
    /// </summary>
    public (CompiledProgram Compiled, byte[] Vertex, byte[] Fragment) LegacyCode(string vertexGlsl, string fragmentGlsl)
    {
        var c = Compiler.Compile(vertexGlsl, fragmentGlsl);
        byte[] v = c.Vertex.DefaultBlock is null ? c.VertexSpirv : SpirvPatch.MoveDefaultBlock(c.VertexSpirv, 0);
        byte[] f = c.Fragment.DefaultBlock is null ? c.FragmentSpirv : SpirvPatch.MoveDefaultBlock(c.FragmentSpirv, 1);
        return (c, v, f);
    }

    /// <summary>
    /// A legacy program with VkGl's layout (<c>VkGl.CreateLayouts</c>): set 0 the named blocks and samplers of both stages (pushed when the
    /// device's push-descriptor limit allows), set 1 the default blocks as dynamic uniform buffers.
    /// </summary>
    public GpuProgram Legacy(string vertexGlsl, string fragmentGlsl, string name)
    {
        var (c, v, f) = LegacyCode(vertexGlsl, fragmentGlsl);
        var bindings = new List<DescriptorSetLayoutBinding>();
        void Add(ShaderReflection r, ShaderStageFlags stage)
        {
            foreach (var b in r.Blocks)
            {
                if (b.Kind == BlockKind.Uniform && !b.IsDefault)
                    bindings.Add(new DescriptorSetLayoutBinding((uint)b.Binding, DescriptorType.UniformBuffer, 1, stage));
            }

            foreach (var s in r.Samplers)
            {
                var descriptorCount = (uint)Math.Max(1, s.ArrayLength);
                bindings.Add(new DescriptorSetLayoutBinding((uint)s.Binding, DescriptorType.CombinedImageSampler, descriptorCount, stage));
            }
        }

        Add(c.Vertex, ShaderStageFlags.VertexBit);
        Add(c.Fragment, ShaderStageFlags.FragmentBit);
        int count = bindings.Sum(b => (int)b.DescriptorCount);
        bool push = _device.HasPushDescriptor && count <= _device.MaxPushDescriptors;
        var set0 = CreateSetLayout([.. bindings], push ? DescriptorSetLayoutCreateFlags.PushDescriptorBitKhr : 0);
        var dynamic = new List<DescriptorSetLayoutBinding>();
        if (c.Vertex.DefaultBlock is not null)
            dynamic.Add(new DescriptorSetLayoutBinding(0, DescriptorType.UniformBufferDynamic, 1, ShaderStageFlags.VertexBit));
        if (c.Fragment.DefaultBlock is not null)
            dynamic.Add(new DescriptorSetLayoutBinding(1, DescriptorType.UniformBufferDynamic, 1, ShaderStageFlags.FragmentBit));
        var set1 = CreateSetLayout([.. dynamic], 0);
        var layout = CreateLayout([set0, set1], 0, 0);

        // The engine's words for the same two sets: set 0 the named blocks and samplers, set 1 the default blocks (their offset is given
        // when the set is bound).
        var slots = new List<GpuSlot>();
        void AddSlots(ShaderReflection r, GpuStage stage)
        {
            foreach (var b in r.Blocks)
            {
                if (b.Kind == BlockKind.Uniform && !b.IsDefault)
                    slots.Add(new GpuSlot((uint)b.Binding, GpuSlotKind.UniformBuffer, stage));
            }

            foreach (var s in r.Samplers)
                slots.Add(new GpuSlot((uint)s.Binding, GpuSlotKind.CombinedSampler, stage, (uint)Math.Max(1, s.ArrayLength)));
        }

        AddSlots(c.Vertex, GpuStage.Vertex);
        AddSlots(c.Fragment, GpuStage.Fragment);
        var defaultSlots = new List<GpuSlot>();
        if (c.Vertex.DefaultBlock is not null)
            defaultSlots.Add(new GpuSlot(0, GpuSlotKind.UniformBuffer, GpuStage.Vertex, 1, OffsetAtBind: true));
        if (c.Fragment.DefaultBlock is not null)
            defaultSlots.Add(new GpuSlot(1, GpuSlotKind.UniformBuffer, GpuStage.Fragment, 1, OffsetAtBind: true));
        var info = new ProgramInfo
        {
            Index = _programs.Count,
            Name = name,
            Model = ShaderModel.Legacy,
            VertexCode = v,
            FragmentCode = f,
            Compiled = c,
            VertexReflection = c.Vertex,
            FragmentReflection = c.Fragment,
            NeutralSets = [GpuSetLayout.Written([.. slots]), GpuSetLayout.Fixed([.. defaultSlots])]
        };
        _programs.Add(new VulkanProgram(_device, info, [set0, set1], [set0, set1], layout, push, 0));
        return new GpuProgram(info.Index, info);
    }

    #endregion

    #region Compute

    /// <summary>
    /// A compute program: set 0 from the reflection (uniform and storage blocks, samplers; pushed when it fits), other sets from
    /// <paramref name="extraSets"/> (sets 1.., not owned), push constants as the shader declares them.
    /// </summary>
    public GpuProgram Compute(string glsl, string name, params GpuSetLayout[] extraSets)
    {
        var code = Compiler.CompileCompute(glsl);
        var r = SpirvReflection.Parse(code);
        var bindings = new List<DescriptorSetLayoutBinding>();
        foreach (var b in r.Blocks)
        {
            if (b.Kind != BlockKind.PushConstant && b.Set == 0)
            {
                var type = b.Kind == BlockKind.StorageBuffer ? DescriptorType.StorageBuffer : DescriptorType.UniformBuffer;
                bindings.Add(new DescriptorSetLayoutBinding((uint)b.Binding, type, 1, ShaderStageFlags.ComputeBit));
            }
        }

        foreach (var s in r.StorageImages)
        {
            if (s.Set == 0)
                bindings.Add(new DescriptorSetLayoutBinding((uint)s.Binding, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit));
        }

        foreach (var s in r.Samplers)
        {
            if (s.Set == 0 && s.ArrayLength < 0)
            {
                throw new InvalidOperationException(
                    $"{name}: runtime-sized sampler array '{s.Name}' in set 0; " +
                    "declare the bindless table at an extra set (BindlessTable.Declarations(1))");
            }

            if (s.Set == 0)
            {
                var descriptorCount = (uint)Math.Max(1, s.ArrayLength);
                bindings.Add(new DescriptorSetLayoutBinding((uint)s.Binding, DescriptorType.CombinedImageSampler, descriptorCount,
                    ShaderStageFlags.ComputeBit));
            }
        }

        bool push = _device.HasPushDescriptor && bindings.Sum(b => (int)b.DescriptorCount) <= _device.MaxPushDescriptors;
        var set0 = CreateSetLayout([.. bindings], push ? DescriptorSetLayoutCreateFlags.PushDescriptorBitKhr : 0);
        uint pc = (uint)(r.Blocks.FirstOrDefault(b => b.Kind == BlockKind.PushConstant)?.Size ?? 0);
        var sets = new[] { set0 }.Concat(extraSets.Select(VkLayout)).ToArray();
        var layout = CreateLayout(sets, pc, ShaderStageFlags.ComputeBit);
        var info = new ProgramInfo
        {
            Index = _programs.Count,
            Name = name,
            Model = ShaderModel.Compute,
            ComputeCode = code,
            ComputeReflection = SpirvReflection.Parse(code),
            PushConstantBytes = pc,
            NeutralSets = [GpuSetLayout.Written([]), .. extraSets]
        };
        _programs.Add(new VulkanProgram(_device, info, sets, [set0], layout, push, pc > 0 ? ShaderStageFlags.ComputeBit : 0));
        return new GpuProgram(info.Index, info);
    }

    /// <summary>
    /// A compute program whose set 0 is given (<paramref name="set0"/>, always pushed: it needs push descriptors) instead of reflected: for
    /// descriptor types the reflection does not read (storage images, acceleration structures). Push constants as the shader declares them.
    /// </summary>
    public GpuProgram Compute(string glsl, string name, ReadOnlySpan<(uint Binding, DescriptorType Type)> set0, params GpuSetLayout[] extraSets)
    {
        if (!_device.HasPushDescriptor) throw new NotSupportedException($"{name}: needs push descriptors");
        var code = Compiler.CompileCompute(glsl);
        var r = SpirvReflection.Parse(code);
        var bindings = new DescriptorSetLayoutBinding[set0.Length];
        for (int i = 0; i < set0.Length; i++)
            bindings[i] = new DescriptorSetLayoutBinding(set0[i].Binding, set0[i].Type, 1, ShaderStageFlags.ComputeBit);
        var layout0 = CreateSetLayout(bindings, DescriptorSetLayoutCreateFlags.PushDescriptorBitKhr);
        uint pc = (uint)(r.Blocks.FirstOrDefault(b => b.Kind == BlockKind.PushConstant)?.Size ?? 0);
        var sets = new[] { layout0 }.Concat(extraSets.Select(VkLayout)).ToArray();
        var layout = CreateLayout(sets, pc, ShaderStageFlags.ComputeBit);
        var info = new ProgramInfo
        {
            Index = _programs.Count,
            Name = name,
            Model = ShaderModel.Compute,
            ComputeCode = code,
            ComputeReflection = SpirvReflection.Parse(code),
            PushConstantBytes = pc,
            NeutralSets = [GpuSetLayout.Written([]), .. extraSets]
        };
        _programs.Add(new VulkanProgram(_device, info, sets, [layout0], layout, true, pc > 0 ? ShaderStageFlags.ComputeBit : 0));
        return new GpuProgram(info.Index, info);
    }

    #endregion

    #region Native

    /// <summary>
    /// A native-model program: strict GLSL 450, the layout given by the model (<paramref name="setLayouts"/>, not owned) and a push-constant
    /// block of <paramref name="pushConstantBytes"/> (at most 128) visible to both stages.
    /// </summary>
    public GpuProgram Native(string vertexGlsl, string fragmentGlsl, string name, GpuSetLayout[] setLayouts, uint pushConstantBytes)
    {
        if (pushConstantBytes > 128)
            throw new ArgumentOutOfRangeException(nameof(pushConstantBytes), "push constants are limited to 128 bytes (the guaranteed minimum)");

        // The stages are linked by name as GL does (docs/renderer-native.md 3.4: InterfaceLocations stays for both models): varyings without
        // an explicit location get the same one in both stages, fragment outputs 0, 1, ... (explicit locations are kept).
        var (vs, fs) = InterfaceLocations.Apply(vertexGlsl, fragmentGlsl);
        var v = Compiler.CompileNative(vs, fragment: false);
        var f = Compiler.CompileNative(fs, fragment: true);
        var stages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;
        var layouts = setLayouts.Select(VkLayout).ToArray();
        var layout = CreateLayout(layouts, pushConstantBytes, stages);
        var info = new ProgramInfo
        {
            Index = _programs.Count,
            Name = name,
            Model = ShaderModel.Native,
            VertexCode = v,
            FragmentCode = f,
            VertexReflection = SpirvReflection.Parse(v),
            FragmentReflection = SpirvReflection.Parse(f),
            PushConstantBytes = pushConstantBytes,
            NeutralSets = setLayouts
        };
        var program = new VulkanProgram(_device, info, layouts, [], layout, false, pushConstantBytes > 0 ? stages : 0);
        _programs.Add(program);
        var made = new GpuProgram(info.Index, info);
        program.FragmentSource = fs;
        foreach (var block in info.VertexReflection!.Blocks.Concat(info.FragmentReflection!.Blocks))
        {
            if (block.Kind == BlockKind.PushConstant && block.Size > pushConstantBytes)
            {
                program.Dispose();
                throw new InvalidOperationException(
                    $"{name}: push-constant block '{block.Name}' is {block.Size} bytes, the layout's range {pushConstantBytes}");
            }
        }

        // The samplers declared in the bindless set must be the table's arrays (a float view read through a usampler is undefined).
        int bindless = Array.FindIndex(setLayouts, l => l == GpuSetLayout.Bindless);
        if (bindless >= 0)
        {
            foreach (var s in info.VertexReflection!.Samplers.Concat(info.FragmentReflection!.Samplers))
            {
                if (s.Set != bindless) continue;
                try
                {
                    BindlessTable.Check(s, name);
                }
                catch
                {
                    program.Dispose();
                    throw;
                }
            }
        }

        return made;
    }

    /// <summary>
    /// A native-model program with tessellation (the device's <see cref="VulkanDevice.HasTessellation"/>): the vertex stage feeds the control
    /// stage and the control stage the evaluation stage by explicit locations; the evaluation stage's outputs reach the fragment stage by name
    /// as in <see cref="Native"/>. The push-constant range is visible to all four stages (a push must name them all:
    /// <see cref="ShaderProgram.PushConstantStages"/>).
    /// </summary>
    public GpuProgram NativeTessellated(string vertexGlsl, string controlGlsl, string evaluationGlsl, string fragmentGlsl, string name,
        GpuSetLayout[] setLayouts, uint pushConstantBytes)
    {
        if (!_device.HasTessellation) throw new NotSupportedException($"{name}: the device has no tessellation shaders");
        if (pushConstantBytes > 128)
            throw new ArgumentOutOfRangeException(nameof(pushConstantBytes), "push constants are limited to 128 bytes (the guaranteed minimum)");

        var (es, fs) = InterfaceLocations.Apply(evaluationGlsl, fragmentGlsl);
        var v = Compiler.CompileNative(vertexGlsl, fragment: false);
        var tc = Compiler.CompileNativeTessellation(controlGlsl, evaluation: false);
        var te = Compiler.CompileNativeTessellation(es, evaluation: true);
        var f = Compiler.CompileNative(fs, fragment: true);
        var stages = ShaderStageFlags.VertexBit | ShaderStageFlags.TessellationControlBit | ShaderStageFlags.TessellationEvaluationBit
            | ShaderStageFlags.FragmentBit;
        var layouts = setLayouts.Select(VkLayout).ToArray();
        var layout = CreateLayout(layouts, pushConstantBytes, stages);
        var info = new ProgramInfo
        {
            Index = _programs.Count,
            Name = name,
            Model = ShaderModel.Native,
            VertexCode = v,
            FragmentCode = f,
            TessControlCode = tc,
            TessEvaluationCode = te,
            VertexReflection = SpirvReflection.Parse(v),
            FragmentReflection = SpirvReflection.Parse(f),
            PushConstantBytes = pushConstantBytes,
            NeutralSets = setLayouts,
        };
        var program = new VulkanProgram(_device, info, layouts, [], layout, false, pushConstantBytes > 0 ? stages : 0);
        _programs.Add(program);
        program.FragmentSource = fs;
        return new GpuProgram(info.Index, info);
    }

    #endregion

    #region Set layouts

    /// <summary>
    /// The stages the native model's shared descriptor sets are visible to: vertex and fragment, and the tessellation stages where the device
    /// has them (so a tessellated program can share the sets of the plain ones).
    /// </summary>
    public ShaderStageFlags GraphicsStages => ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit |
        (_device.HasTessellation ? ShaderStageFlags.TessellationControlBit | ShaderStageFlags.TessellationEvaluationBit : 0);

    /// <summary>
    /// The bindless table's set layout (set by <see cref="GpuContext"/>), so <see cref="Native"/> can check a program's declarations of it.
    /// </summary>
    internal DescriptorSetLayout BindlessLayout { get; set; }

    /// <summary>
    /// The Vulkan layout of a set the engine described: the bindless table's is the table's own, the rest are made here.
    /// </summary>
    internal DescriptorSetLayout VkLayout(GpuSetLayout set)
    {
        if (set.IsBindless) return BindlessLayout;
        if (_setLayouts.TryGetValue(set, out var made)) return made;
        var bindings = new DescriptorSetLayoutBinding[set.Slots.Length];
        for (int i = 0; i < bindings.Length; i++)
        {
            var s = set.Slots[i];
            bindings[i] = new DescriptorSetLayoutBinding(s.Binding, VkDescriptorType(s), 1);
            bindings[i].StageFlags = VkStageFlags(s.Stages);
        }

        var layout = CreateSetLayout(bindings, CanPush(set) ? DescriptorSetLayoutCreateFlags.PushDescriptorBitKhr : 0);
        _setLayouts[set] = layout;
        return layout;
    }

    /// <summary>
    /// Whether the device can push this set's descriptors (the renderers write it for every draw).
    /// </summary>
    internal bool CanPush(GpuSetLayout set) =>
        _device.HasPushDescriptor && set.PerDraw && set.Slots.Sum(s => (int)s.Count) <= _device.MaxPushDescriptors;

    /// <summary>
    /// Whether set <paramref name="set"/> of <paramref name="program"/> was made as a push-descriptor set: a legacy or compute program's set 0
    /// as it was compiled (it fit), any other set as <see cref="CanPush"/> made its layout.
    /// </summary>
    internal bool Pushes(VulkanProgram program, uint set) =>
        set == 0 && program.Model is ShaderModel.Legacy or ShaderModel.Compute ? program.PushDescriptors
            : set < program.NeutralSets.Length && !program.NeutralSets[set].IsBindless && CanPush(program.NeutralSets[set]);

    /// <summary>
    /// The Vulkan descriptor type of a slot (a uniform buffer offset at bind is a dynamic one).
    /// </summary>
    static DescriptorType VkDescriptorType(GpuSlot slot) => slot.Kind switch
    {
        GpuSlotKind.UniformBuffer => slot.OffsetAtBind ? DescriptorType.UniformBufferDynamic : DescriptorType.UniformBuffer,
        GpuSlotKind.StorageBuffer => DescriptorType.StorageBuffer,
        GpuSlotKind.CombinedSampler => DescriptorType.CombinedImageSampler,
        _ => DescriptorType.StorageImage,
    };

    /// <summary>
    /// The Vulkan shader stage flags of engine stages.
    /// </summary>
    static ShaderStageFlags VkStageFlags(GpuStage stages)
    {
        var f = ShaderStageFlags.None;
        if (stages.HasFlag(GpuStage.Vertex)) f |= ShaderStageFlags.VertexBit;
        if (stages.HasFlag(GpuStage.Fragment)) f |= ShaderStageFlags.FragmentBit;
        if (stages.HasFlag(GpuStage.Compute)) f |= ShaderStageFlags.ComputeBit;
        if (stages.HasFlag(GpuStage.TessControl)) f |= ShaderStageFlags.TessellationControlBit;
        if (stages.HasFlag(GpuStage.TessEvaluation)) f |= ShaderStageFlags.TessellationEvaluationBit;
        return f;
    }

    #endregion

    #region Lifetime

    /// <summary>
    /// Destroys the layouts the engine's sets were made into (once every frame that could use them has finished).
    /// </summary>
    public void Dispose()
    {
        var (vk, dev) = (_device.Vk, _device.Device);
        var layouts = _setLayouts.Values.ToArray();
        _setLayouts.Clear();
        _device.Frames.DeferDelete(() => { foreach (var l in layouts) vk.DestroyDescriptorSetLayout(dev, l, null); });
    }

    #endregion

    #region Layouts

    /// <summary>
    /// A descriptor set layout of the bindings with the creation flags.
    /// </summary>
    internal DescriptorSetLayout CreateSetLayout(DescriptorSetLayoutBinding[] bindings, DescriptorSetLayoutCreateFlags flags)
    {
        fixed (DescriptorSetLayoutBinding* pb = bindings)
        {
            var info = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                Flags = flags,
                BindingCount = (uint)bindings.Length,
                PBindings = pb,
            };
            VulkanException.Check(_device.Vk.CreateDescriptorSetLayout(_device.Device, &info, null, out var layout), "vkCreateDescriptorSetLayout");
            return layout;
        }
    }

    /// <summary>
    /// A pipeline layout of the sets and one push-constant range (none when <paramref name="pushBytes"/> is 0).
    /// </summary>
    PipelineLayout CreateLayout(DescriptorSetLayout[] sets, uint pushBytes, ShaderStageFlags pushStages)
    {
        var range = new PushConstantRange(pushStages, 0, pushBytes);
        fixed (DescriptorSetLayout* ps = sets)
        {
            var li = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)sets.Length,
                PSetLayouts = ps,
                PushConstantRangeCount = pushBytes > 0 ? 1u : 0,
                PPushConstantRanges = pushBytes > 0 ? &range : null,
            };
            VulkanException.Check(_device.Vk.CreatePipelineLayout(_device.Device, &li, null, out var layout), "vkCreatePipelineLayout");
            return layout;
        }
    }

    #endregion
}