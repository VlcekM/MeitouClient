using System.Runtime.InteropServices;
using Meitou.Rendering.Vulkan.Core;
using Meitou.Rendering.Vulkan.Shaders;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu;

/// <summary>The descriptor model a <see cref="ShaderProgram"/> follows (docs/renderer-native.md 3).</summary>
public enum ShaderModel
{
    /// <summary>GL-shaped: loose uniforms in default blocks (set 1, dynamic offsets), blocks and samplers at glslang's bindings in set 0.
    /// Byte-identical to what VkGl builds (step P).</summary>
    Legacy,
    /// <summary>Explicit sets, push constants, bindless (step O and new code).</summary>
    Native,
    Compute,
}

/// <summary>Patches applied to compiled SPIR-V (shared by VkGl and the native API).</summary>
public static class SpirvPatch
{
    /// <summary>A copy of <paramref name="spirv"/> with <c>gl_DefaultUniformBlock</c>'s variable decorated for set 1 at
    /// <paramref name="binding"/> (the words are patched in place: same size). VkGl's <c>LinkProgram</c> step, moved here.</summary>
    public static byte[] MoveDefaultBlock(byte[] spirv, uint binding)
    {
        var copy = (byte[])spirv.Clone();
        var w = MemoryMarshal.Cast<byte, uint>(copy.AsSpan());
        uint structId = 0;
        var pointers = new HashSet<uint>();
        var variables = new HashSet<uint>();
        for (int pass = 0; pass < 3; pass++)
            for (int i = 5; i < w.Length;)
            {
                uint op = w[i] & 0xFFFF, count = w[i] >> 16;
                if (count == 0) break;
                var a = w.Slice(i + 1, (int)count - 1);
                if (pass == 0 && op == 5 && System.Text.Encoding.ASCII.GetString(MemoryMarshal.AsBytes(a[1..])).TrimEnd('\0') == "gl_DefaultUniformBlock") structId = a[0];
                else if (pass == 1 && op == 32 && a[2] == structId) pointers.Add(a[0]);
                else if (pass == 1 && op == 59 && pointers.Contains(a[0])) variables.Add(a[1]);
                else if (pass == 2 && op == 71 && variables.Contains(a[0]))
                {
                    if (a[1] == 34) a[2] = 1;              // DescriptorSet
                    else if (a[1] == 33) a[2] = binding;   // Binding
                }
                i += (int)count;
            }
        if (structId == 0 || variables.Count == 0) throw new InvalidOperationException("SPIR-V: gl_DefaultUniformBlock not found");
        return copy;
    }
}

/// <summary>
/// Compiled shader modules with their reflection and pipeline layout. Legacy programs: the modules VkGl would create (same SPIR-V, default
/// blocks moved to set 1) and VkGl's layout (set 0 pushed when it fits, set 1 dynamic uniform buffers). Compute programs: set 0 from the
/// reflection (pushed when it fits) and a push-constant range. Native programs: the layout the caller's model gives.
/// </summary>
public sealed unsafe class ShaderProgram : IDisposable
{
    readonly VulkanDevice device;
    readonly DescriptorSetLayout[] ownedSetLayouts;

    internal ShaderProgram(VulkanDevice device, string name, ShaderModel model, byte[]? vertexCode, byte[]? fragmentCode, byte[]? computeCode,
        CompiledProgram? compiled, DescriptorSetLayout[] setLayouts, DescriptorSetLayout[] ownedSetLayouts, PipelineLayout layout,
        bool pushDescriptors, uint pushConstantBytes, ShaderStageFlags pushConstantStages)
    {
        this.device = device;
        Name = name;
        Model = model;
        VertexCode = vertexCode;
        FragmentCode = fragmentCode;
        ComputeCode = computeCode;
        Compiled = compiled;
        SetLayouts = setLayouts;
        this.ownedSetLayouts = ownedSetLayouts;
        Layout = layout;
        PushDescriptors = pushDescriptors;
        PushConstantBytes = pushConstantBytes;
        PushConstantStages = pushConstantStages;
        if (vertexCode is not null) VertexModule = CreateModule(vertexCode);
        if (fragmentCode is not null) FragmentModule = CreateModule(fragmentCode);
        if (computeCode is not null) ComputeModule = CreateModule(computeCode);
        VertexReflection = vertexCode is null ? null : compiled?.Vertex ?? SpirvReflection.Parse(vertexCode);
        FragmentReflection = fragmentCode is null ? null : compiled?.Fragment ?? SpirvReflection.Parse(fragmentCode);
        ComputeReflection = computeCode is null ? null : SpirvReflection.Parse(computeCode);
        // A stable identity for logs: the hash of the code handed to the driver.
        CodeHash = DrawLog.ProgramHash(vertexCode ?? computeCode ?? [], fragmentCode ?? []);
    }

    public string Name { get; }
    public ShaderModel Model { get; }
    /// <summary>The exact bytes given to <c>vkCreateShaderModule</c>.</summary>
    public byte[]? VertexCode { get; }
    public byte[]? FragmentCode { get; }
    public byte[]? ComputeCode { get; }
    public ShaderModule VertexModule { get; }
    public ShaderModule FragmentModule { get; }
    public ShaderModule ComputeModule { get; }
    /// <summary>The legacy compile result (reflection of both stages, default blocks), null for other models.</summary>
    public CompiledProgram? Compiled { get; }
    public ShaderReflection? VertexReflection { get; }
    public ShaderReflection? FragmentReflection { get; }
    public ShaderReflection? ComputeReflection { get; }
    public DescriptorSetLayout[] SetLayouts { get; }
    public PipelineLayout Layout { get; }
    /// <summary>Set 0 is a push-descriptor set.</summary>
    public bool PushDescriptors { get; }
    public uint PushConstantBytes { get; }
    public ShaderStageFlags PushConstantStages { get; }
    public ulong CodeHash { get; }

    ShaderModule CreateModule(byte[] spirv)
    {
        fixed (byte* code = spirv)
        {
            var info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)code };
            VulkanException.Check(device.Vk.CreateShaderModule(device.Device, &info, null, out var module), "vkCreateShaderModule");
            return module;
        }
    }

    public void Dispose()
    {
        var (vk, dev) = (device.Vk, device.Device);
        var (vm, fm, cm, layout, sets) = (VertexModule, FragmentModule, ComputeModule, Layout, ownedSetLayouts);
        device.Frames.DeferDelete(() =>
        {
            if (vm.Handle != 0) vk.DestroyShaderModule(dev, vm, null);
            if (fm.Handle != 0) vk.DestroyShaderModule(dev, fm, null);
            if (cm.Handle != 0) vk.DestroyShaderModule(dev, cm, null);
            if (layout.Handle != 0) vk.DestroyPipelineLayout(dev, layout, null);
            foreach (var s in sets) vk.DestroyDescriptorSetLayout(dev, s, null);
        });
    }
}

/// <summary>
/// Compiles programs for the three models (docs/renderer-native.md 3). The legacy path is the one VkGl uses (VkGl compiles through
/// <see cref="Compiler"/> too), so a legacy program's modules are byte-identical to VkGl's.
/// </summary>
public sealed unsafe class ShaderLibrary
{
    readonly VulkanDevice device;

    public ShaderLibrary(VulkanDevice device)
    {
        this.device = device;
        Compiler = new GlslProgramCompiler(new ShaderCompileOptions { RemapClipDepth = !device.HasDepthClipControl });
    }

    /// <summary>The compiler shared with VkGl (same options, same memory cache).</summary>
    public GlslProgramCompiler Compiler { get; }

    /// <summary>The modules VkGl would make for this source pair: compiled, then each default block moved to set 1 (vertex binding 0,
    /// fragment binding 1).</summary>
    public (CompiledProgram Compiled, byte[] Vertex, byte[] Fragment) LegacyCode(string vertexGlsl, string fragmentGlsl)
    {
        var c = Compiler.Compile(vertexGlsl, fragmentGlsl);
        byte[] v = c.Vertex.DefaultBlock is null ? c.VertexSpirv : SpirvPatch.MoveDefaultBlock(c.VertexSpirv, 0);
        byte[] f = c.Fragment.DefaultBlock is null ? c.FragmentSpirv : SpirvPatch.MoveDefaultBlock(c.FragmentSpirv, 1);
        return (c, v, f);
    }

    /// <summary>A legacy program with VkGl's layout (<c>VkGl.CreateLayouts</c>): set 0 the named blocks and samplers of both stages (pushed
    /// when the device's push-descriptor limit allows), set 1 the default blocks as dynamic uniform buffers.</summary>
    public ShaderProgram Legacy(string vertexGlsl, string fragmentGlsl, string name)
    {
        var (c, v, f) = LegacyCode(vertexGlsl, fragmentGlsl);
        var bindings = new List<DescriptorSetLayoutBinding>();
        void Add(ShaderReflection r, ShaderStageFlags stage)
        {
            foreach (var b in r.Blocks)
                if (b.Kind == BlockKind.Uniform && !b.IsDefault)
                    bindings.Add(new DescriptorSetLayoutBinding((uint)b.Binding, DescriptorType.UniformBuffer, 1, stage));
            foreach (var s in r.Samplers)
                bindings.Add(new DescriptorSetLayoutBinding((uint)s.Binding, DescriptorType.CombinedImageSampler, (uint)Math.Max(1, s.ArrayLength), stage));
        }
        Add(c.Vertex, ShaderStageFlags.VertexBit);
        Add(c.Fragment, ShaderStageFlags.FragmentBit);
        int count = bindings.Sum(b => (int)b.DescriptorCount);
        bool push = device.HasPushDescriptor && count <= device.MaxPushDescriptors;
        var set0 = CreateSetLayout([.. bindings], push ? DescriptorSetLayoutCreateFlags.PushDescriptorBitKhr : 0);
        var dynamic = new List<DescriptorSetLayoutBinding>();
        if (c.Vertex.DefaultBlock is not null) dynamic.Add(new DescriptorSetLayoutBinding(0, DescriptorType.UniformBufferDynamic, 1, ShaderStageFlags.VertexBit));
        if (c.Fragment.DefaultBlock is not null) dynamic.Add(new DescriptorSetLayoutBinding(1, DescriptorType.UniformBufferDynamic, 1, ShaderStageFlags.FragmentBit));
        var set1 = CreateSetLayout([.. dynamic], 0);
        var layout = CreateLayout([set0, set1], 0, 0);
        return new ShaderProgram(device, name, ShaderModel.Legacy, v, f, null, c, [set0, set1], [set0, set1], layout, push, 0, 0);
    }

    /// <summary>A compute program: set 0 from the reflection (uniform and storage blocks, samplers; pushed when it fits), other sets
    /// from <paramref name="extraSets"/> (sets 1.., not owned), push constants as the shader declares them.</summary>
    public ShaderProgram Compute(string glsl, string name, params DescriptorSetLayout[] extraSets)
    {
        var code = Compiler.CompileCompute(glsl);
        var r = SpirvReflection.Parse(code);
        var bindings = new List<DescriptorSetLayoutBinding>();
        foreach (var b in r.Blocks)
            if (b.Kind != BlockKind.PushConstant && b.Set == 0)
                bindings.Add(new DescriptorSetLayoutBinding((uint)b.Binding, b.Kind == BlockKind.StorageBuffer ? DescriptorType.StorageBuffer : DescriptorType.UniformBuffer, 1, ShaderStageFlags.ComputeBit));
        foreach (var s in r.Samplers)
            if (s.Set == 0 && s.ArrayLength < 0)
                throw new InvalidOperationException($"{name}: runtime-sized sampler array '{s.Name}' in set 0; declare the bindless table at an extra set (BindlessTable.Declarations(1))");
            else if (s.Set == 0) bindings.Add(new DescriptorSetLayoutBinding((uint)s.Binding, DescriptorType.CombinedImageSampler, (uint)Math.Max(1, s.ArrayLength), ShaderStageFlags.ComputeBit));
        bool push = device.HasPushDescriptor && bindings.Sum(b => (int)b.DescriptorCount) <= device.MaxPushDescriptors;
        var set0 = CreateSetLayout([.. bindings], push ? DescriptorSetLayoutCreateFlags.PushDescriptorBitKhr : 0);
        uint pc = (uint)(r.Blocks.FirstOrDefault(b => b.Kind == BlockKind.PushConstant)?.Size ?? 0);
        DescriptorSetLayout[] sets = [set0, .. extraSets];
        var layout = CreateLayout(sets, pc, ShaderStageFlags.ComputeBit);
        return new ShaderProgram(device, name, ShaderModel.Compute, null, null, code, null, sets, [set0], layout, push, pc, pc > 0 ? ShaderStageFlags.ComputeBit : 0);
    }

    /// <summary>A native-model program: strict GLSL 450, the layout given by the model (<paramref name="setLayouts"/>, not owned) and a
    /// push-constant block of <paramref name="pushConstantBytes"/> (at most 128) visible to both stages.</summary>
    public ShaderProgram Native(string vertexGlsl, string fragmentGlsl, string name, DescriptorSetLayout[] setLayouts, uint pushConstantBytes)
    {
        if (pushConstantBytes > 128) throw new ArgumentOutOfRangeException(nameof(pushConstantBytes), "push constants are limited to 128 bytes (the guaranteed minimum)");
        // The stages are linked by name as GL does (docs/renderer-native.md 3.4: InterfaceLocations stays for both models): varyings without
        // an explicit location get the same one in both stages, fragment outputs 0, 1, ... (explicit locations are kept).
        var (vs, fs) = InterfaceLocations.Apply(vertexGlsl, fragmentGlsl);
        var v = Compiler.CompileNative(vs, fragment: false);
        var f = Compiler.CompileNative(fs, fragment: true);
        var stages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;
        var layout = CreateLayout(setLayouts, pushConstantBytes, stages);
        var program = new ShaderProgram(device, name, ShaderModel.Native, v, f, null, null, setLayouts, [], layout, false, pushConstantBytes, pushConstantBytes > 0 ? stages : 0);
        foreach (var block in program.VertexReflection!.Blocks.Concat(program.FragmentReflection!.Blocks))
            if (block.Kind == BlockKind.PushConstant && block.Size > pushConstantBytes)
            {
                program.Dispose();
                throw new InvalidOperationException($"{name}: push-constant block '{block.Name}' is {block.Size} bytes, the layout's range {pushConstantBytes}");
            }
        // The samplers declared in the bindless set must be the table's arrays (a float view read through a usampler is undefined).
        int bindless = Array.FindIndex(setLayouts, l => l.Handle != 0 && l.Handle == BindlessLayout.Handle);
        if (bindless >= 0)
            foreach (var s in program.VertexReflection!.Samplers.Concat(program.FragmentReflection!.Samplers))
                if (s.Set == bindless)
                {
                    try { BindlessTable.Check(s, name); }
                    catch { program.Dispose(); throw; }
                }
        return program;
    }

    /// <summary>The bindless table's set layout (set by <see cref="GpuContext"/>), so <see cref="Native"/> can check a program's
    /// declarations of it.</summary>
    internal DescriptorSetLayout BindlessLayout { get; set; }

    internal DescriptorSetLayout CreateSetLayout(DescriptorSetLayoutBinding[] bindings, DescriptorSetLayoutCreateFlags flags)
    {
        fixed (DescriptorSetLayoutBinding* pb = bindings)
        {
            var info = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo, Flags = flags, BindingCount = (uint)bindings.Length, PBindings = pb,
            };
            VulkanException.Check(device.Vk.CreateDescriptorSetLayout(device.Device, &info, null, out var layout), "vkCreateDescriptorSetLayout");
            return layout;
        }
    }

    PipelineLayout CreateLayout(DescriptorSetLayout[] sets, uint pushBytes, ShaderStageFlags pushStages)
    {
        var range = new PushConstantRange(pushStages, 0, pushBytes);
        fixed (DescriptorSetLayout* ps = sets)
        {
            var li = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = (uint)sets.Length, PSetLayouts = ps,
                PushConstantRangeCount = pushBytes > 0 ? 1u : 0, PPushConstantRanges = pushBytes > 0 ? &range : null,
            };
            VulkanException.Check(device.Vk.CreatePipelineLayout(device.Device, &li, null, out var layout), "vkCreatePipelineLayout");
            return layout;
        }
    }
}
