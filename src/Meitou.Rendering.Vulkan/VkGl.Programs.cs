using Meitou.Rendering.Vulkan.Shaders;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Vulkan;

public sealed unsafe partial class VkGl
{
    /// <summary>A GL shader: only its source is kept; compiling happens at link, where both stages are known.</summary>
    internal sealed class GlShaderObj(ShaderType type)
    {
        public readonly ShaderType Type = type;
        public string Source = "";
    }

    /// <summary>A uniform location: where its value lives in each stage's default block, or the sampler it names.</summary>
    internal sealed class UniformSlot
    {
        public UniformLookup? Vertex, Fragment;
        public SamplerInfo? VertexSampler, FragmentSampler;
        public int Unit;   // sampler uniforms: the texture unit (glUniform1i)
        public bool IsSampler => VertexSampler is not null || FragmentSampler is not null;
    }

    /// <summary>
    /// A linked program: SPIR-V modules, a push-descriptor set layout with every block and sampler both stages use, the uniform
    /// locations handed out by <see cref="GetUniformLocation"/>, and a CPU copy of each stage's default block (loose uniforms),
    /// copied into the frame ring at a draw after it changed.
    /// </summary>
    internal sealed class GlProgramObj(uint id)
    {
        public readonly uint Id = id;
        public uint VertexShader, FragmentShader;
        public bool Linked;
        public string Log = "";
        public CompiledProgram? Compiled;
        public ShaderModule VertexModule, FragmentModule;
        public byte[] VertexCode = [], FragmentCode = [];   // what the modules were made from
        public DescriptorSetLayout SetLayout, DynamicSetLayout;
        public readonly Dictionary<(ulong Vertex, ulong Fragment), DescriptorSet> DynamicSets = [];   // set 1 per pair of uniform ring chunks
        public long LastPushEpoch = -1;
        public ulong[] LastPush = [];   // set 0 as last pushed in this command buffer (handles and offsets), to skip identical pushes
        public PipelineLayout Layout;
        public bool PushDescriptors;
        public readonly List<UniformSlot> Uniforms = [];
        public readonly Dictionary<string, int> Locations = [];
        public readonly List<string> BlockNames = [];
        public readonly Dictionary<string, uint> BlockBindings = [];   // named block -> GL binding point
        public byte[] VertexDefault = [], FragmentDefault = [];
        public UniformBlockInfo? VertexDefaultBlock, FragmentDefaultBlock;
        public bool VertexDirty = true, FragmentDirty = true;
        public RingSlice VertexSlice, FragmentSlice;
        public long SliceFrame = -1;
        public int DescriptorCount;
        public int[] InputLocations = [];
        public ScalarKind[] InputKinds = [];
        public BlockBinding[] BlockList = [];       // every uniform block of both stages, in descriptor order
        public SamplerBinding[] SamplerList = [];   // every sampler of both stages, with the uniform slot holding its unit
        public readonly Dictionary<string, UniformSlot> SamplerSlots = [];
    }

    internal readonly record struct BlockBinding(uint Binding, int Size, bool Default, bool Vertex, string Name);
    internal readonly record struct SamplerBinding(uint Binding, SamplerInfo Info, UniformSlot Slot);

    GlslProgramCompiler Compiler => Context.Shaders.Compiler;   // the native API's compiler: same options, same caches
    uint currentProgram;
    GlProgramObj? program;

    public uint CreateShader(ShaderType type)
    {
        uint id = NewId();
        shaders[id] = new GlShaderObj(type);
        return id;
    }

    public void ShaderSource(uint shader, string source) => shaders[shader].Source = source;
    public void CompileShader(uint shader) { }

    public void GetShader(uint shader, ShaderParameterName pname, out int @params) =>
        @params = pname == ShaderParameterName.CompileStatus ? 1 : 0;

    public string GetShaderInfoLog(uint shader) => "";
    public void DeleteShader(uint shader) => shaders.Remove(shader);

    public uint CreateProgram()
    {
        uint id = NewId();
        programs[id] = new GlProgramObj(id);
        return id;
    }

    public void AttachShader(uint program, uint shader)
    {
        var p = programs[program];
        if (shaders[shader].Type == ShaderType.VertexShader) p.VertexShader = shader;
        else p.FragmentShader = shader;
    }

    public void LinkProgram(uint id)
    {
        var p = programs[id];
        try
        {
            p.Compiled = Compiler.Compile(shaders[p.VertexShader].Source, shaders[p.FragmentShader].Source);
        }
        catch (ShaderCompileException e)
        {
            p.Linked = false;
            p.Log = $"{e.Message}\n{e.Log}";
            return;
        }
        var c = p.Compiled;
        p.VertexDefaultBlock = c.Vertex.DefaultBlock;
        p.FragmentDefaultBlock = c.Fragment.DefaultBlock;
        // Loose uniforms go to set 1 as dynamic uniform buffers (vertex binding 0, fragment binding 1): a draw that only
        // changes uniforms binds new offsets instead of pushing descriptors again.
        p.VertexCode = p.VertexDefaultBlock is null ? c.VertexSpirv : MoveDefaultBlock(c.VertexSpirv, 0);
        p.FragmentCode = p.FragmentDefaultBlock is null ? c.FragmentSpirv : MoveDefaultBlock(c.FragmentSpirv, 1);
        p.VertexModule = CreateModule(p.VertexCode);
        p.FragmentModule = CreateModule(p.FragmentCode);
        p.VertexDefault = new byte[p.VertexDefaultBlock?.Size ?? 0];
        p.FragmentDefault = new byte[p.FragmentDefaultBlock?.Size ?? 0];
        foreach (var b in c.Vertex.Blocks.Concat(c.Fragment.Blocks))
            if (!b.IsDefault && b.Kind == BlockKind.Uniform && !p.BlockNames.Contains(b.Name)) p.BlockNames.Add(b.Name);
        p.InputLocations = [.. c.Vertex.Inputs.SelectMany(i => Enumerable.Range(i.Location, i.Slots))];
        p.InputKinds = [.. c.Vertex.Inputs.SelectMany(i => Enumerable.Repeat(i.Kind, i.Slots))];
        foreach (var s in c.Vertex.Samplers.Concat(c.Fragment.Samplers))
            if (!p.SamplerSlots.ContainsKey(s.Name))
                p.SamplerSlots[s.Name] = new UniformSlot { VertexSampler = c.Vertex.FindSampler(s.Name), FragmentSampler = c.Fragment.FindSampler(s.Name) };
        p.BlockList = [
            .. c.Vertex.Blocks.Where(b => b.Kind == BlockKind.Uniform && !b.IsDefault).Select(b => new BlockBinding((uint)b.Binding, b.Size, false, true, b.Name)),
            .. c.Fragment.Blocks.Where(b => b.Kind == BlockKind.Uniform && !b.IsDefault).Select(b => new BlockBinding((uint)b.Binding, b.Size, false, false, b.Name))];
        p.SamplerList = [.. c.Vertex.Samplers.Concat(c.Fragment.Samplers).Select(s => new SamplerBinding((uint)s.Binding, s, p.SamplerSlots[s.Name]))];
        p.LastPush = new ulong[(p.BlockList.Length + p.SamplerList.Length) * 3];
        CreateLayouts(p);
        p.Linked = true;
    }

    /// <summary>A copy of <paramref name="spirv"/> with <c>gl_DefaultUniformBlock</c>'s variable decorated for set 1 at
    /// <paramref name="binding"/> (the words are patched in place: same size).</summary>
    static byte[] MoveDefaultBlock(byte[] spirv, uint binding) => SpirvPatch.MoveDefaultBlock(spirv, binding);

    /// <summary>The SPIR-V a linked program handed to <c>vkCreateShaderModule</c> (tests: the native legacy path must produce the same bytes).</summary>
    internal (byte[] Vertex, byte[] Fragment) ModuleCode(uint program) => (programs[program].VertexCode, programs[program].FragmentCode);

    ShaderModule CreateModule(byte[] spirv)
    {
        fixed (byte* code = spirv)
        {
            var info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)code };
            Check(vk.CreateShaderModule(dev, &info, null, out var module));
            return module;
        }
    }

    void CreateLayouts(GlProgramObj p)
    {
        // Set 0: named uniform blocks and samplers (pushed); set 1: the stages' loose uniforms (dynamic offsets).
        var bindings = new List<DescriptorSetLayoutBinding>();
        void Add(ShaderReflection r, ShaderStageFlags stage)
        {
            foreach (var b in r.Blocks)
                if (b.Kind == BlockKind.Uniform && !b.IsDefault)
                    bindings.Add(new DescriptorSetLayoutBinding((uint)b.Binding, DescriptorType.UniformBuffer, 1, stage));
            foreach (var s in r.Samplers)
                bindings.Add(new DescriptorSetLayoutBinding((uint)s.Binding, DescriptorType.CombinedImageSampler, (uint)Math.Max(1, s.ArrayLength), stage));
        }
        Add(p.Compiled!.Vertex, ShaderStageFlags.VertexBit);
        Add(p.Compiled.Fragment, ShaderStageFlags.FragmentBit);
        p.DescriptorCount = bindings.Sum(b => (int)b.DescriptorCount);
        p.PushDescriptors = device.HasPushDescriptor && p.DescriptorCount <= device.MaxPushDescriptors;
        p.SetLayout = CreateSetLayout([.. bindings], p.PushDescriptors);
        var dynamic = new List<DescriptorSetLayoutBinding>();
        if (p.VertexDefaultBlock is not null) dynamic.Add(new DescriptorSetLayoutBinding(0, DescriptorType.UniformBufferDynamic, 1, ShaderStageFlags.VertexBit));
        if (p.FragmentDefaultBlock is not null) dynamic.Add(new DescriptorSetLayoutBinding(1, DescriptorType.UniformBufferDynamic, 1, ShaderStageFlags.FragmentBit));
        p.DynamicSetLayout = CreateSetLayout([.. dynamic], push: false);
        var layouts = stackalloc DescriptorSetLayout[2] { p.SetLayout, p.DynamicSetLayout };
        var li = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 2, PSetLayouts = layouts };
        Check(vk.CreatePipelineLayout(dev, &li, null, out p.Layout));
    }

    DescriptorSetLayout CreateSetLayout(DescriptorSetLayoutBinding[] bindings, bool push)
    {
        fixed (DescriptorSetLayoutBinding* pb = bindings)
        {
            var info = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                Flags = push ? DescriptorSetLayoutCreateFlags.PushDescriptorBitKhr : 0,
                BindingCount = (uint)bindings.Length,
                PBindings = pb,
            };
            Check(vk.CreateDescriptorSetLayout(dev, &info, null, out var layout));
            return layout;
        }
    }

    public void GetProgram(uint program, ProgramPropertyARB pname, out int @params) =>
        @params = pname == ProgramPropertyARB.LinkStatus ? (programs[program].Linked ? 1 : 0) : 0;

    public string GetProgramInfoLog(uint program) => programs[program].Log;

    public void DeleteProgram(uint id)
    {
        if (!programs.Remove(id, out var p)) return;
        DestroyProgram(p);
        if (currentProgram == id) { currentProgram = 0; program = null; }
    }

    void DestroyProgram(GlProgramObj p)
    {
        var (vm, fm, sl, dl, pl) = (p.VertexModule, p.FragmentModule, p.SetLayout, p.DynamicSetLayout, p.Layout);
        ForgetPipelines(p);
        device.Frames.DeferDelete(() =>
        {
            if (vm.Handle != 0) vk.DestroyShaderModule(dev, vm, null);
            if (fm.Handle != 0) vk.DestroyShaderModule(dev, fm, null);
            if (pl.Handle != 0) vk.DestroyPipelineLayout(dev, pl, null);
            if (sl.Handle != 0) vk.DestroyDescriptorSetLayout(dev, sl, null);
            if (dl.Handle != 0) vk.DestroyDescriptorSetLayout(dev, dl, null);
        });
    }

    public void UseProgram(uint id)
    {
        currentProgram = id;
        program = id != 0 ? programs[id] : null;
    }

    public int GetUniformLocation(uint id, string name)
    {
        var p = programs[id];
        if (!p.Linked) return -1;
        if (p.Locations.TryGetValue(name, out int loc)) return loc;
        var c = p.Compiled!;
        var slot = p.SamplerSlots.TryGetValue(name, out var samplerSlot) ? samplerSlot : new UniformSlot
        {
            Vertex = Loose(c.Vertex.FindUniform(name)),
            Fragment = Loose(c.Fragment.FindUniform(name)),
        };
        if (slot.Vertex is null && slot.Fragment is null && !slot.IsSampler) loc = -1;
        else
        {
            loc = p.Uniforms.Count;
            p.Uniforms.Add(slot);
        }
        p.Locations[name] = loc;
        return loc;
    }

    // Only loose uniforms are set through locations; members of named blocks come from buffers.
    static UniformLookup? Loose(UniformLookup? l) => l is { } u && u.Block.IsDefault ? u : null;

    public uint GetUniformBlockIndex(uint id, string uniformBlockName)
    {
        int i = programs[id].BlockNames.IndexOf(uniformBlockName);
        return i < 0 ? uint.MaxValue : (uint)i;
    }

    public void UniformBlockBinding(uint id, uint uniformBlockIndex, uint uniformBlockBinding)
    {
        var p = programs[id];
        p.BlockBindings[p.BlockNames[(int)uniformBlockIndex]] = uniformBlockBinding;
    }

    UniformSlot? Slot(int location) =>
        location >= 0 && program is { } p && location < p.Uniforms.Count ? p.Uniforms[location] : null;

    public void Uniform1(int location, int v0)
    {
        if (Slot(location) is not { } s) return;
        if (s.IsSampler) { s.Unit = v0; return; }
        int* v = stackalloc int[1] { v0 };
        WriteUniform(s, (byte*)v, 1, 1, 1, isInt: true);
    }

    public void Uniform1(int location, float v0)
    {
        float* v = stackalloc float[1] { v0 };
        if (Slot(location) is { } s) WriteUniform(s, (byte*)v, 1, 1, 1, isInt: false);
    }

    public void Uniform2(int location, float v0, float v1)
    {
        float* v = stackalloc float[2] { v0, v1 };
        if (Slot(location) is { } s) WriteUniform(s, (byte*)v, 2, 1, 1, isInt: false);
    }

    public void Uniform3(int location, float v0, float v1, float v2)
    {
        float* v = stackalloc float[3] { v0, v1, v2 };
        if (Slot(location) is { } s) WriteUniform(s, (byte*)v, 3, 1, 1, isInt: false);
    }

    public void Uniform4(int location, float v0, float v1, float v2, float v3)
    {
        float* v = stackalloc float[4] { v0, v1, v2, v3 };
        if (Slot(location) is { } s) WriteUniform(s, (byte*)v, 4, 1, 1, isInt: false);
    }

    public void Uniform1(int location, uint count, float* value) { if (Slot(location) is { } s) WriteUniform(s, (byte*)value, 1, 1, (int)count, false); }
    public void Uniform2(int location, uint count, float* value) { if (Slot(location) is { } s) WriteUniform(s, (byte*)value, 2, 1, (int)count, false); }
    public void Uniform3(int location, uint count, float* value) { if (Slot(location) is { } s) WriteUniform(s, (byte*)value, 3, 1, (int)count, false); }
    public void Uniform4(int location, uint count, float* value) { if (Slot(location) is { } s) WriteUniform(s, (byte*)value, 4, 1, (int)count, false); }

    public void UniformMatrix4(int location, uint count, bool transpose, float* value)
    {
        if (transpose) throw new NotSupportedException("transposed matrix uniforms");
        if (Slot(location) is { } s) WriteUniform(s, (byte*)value, 4, 4, (int)count, false);
    }

    /// <summary>Copies tightly packed GL values (<paramref name="count"/> elements of <paramref name="columns"/> × <paramref name="rows"/>
    /// 4-byte components) into each stage's default block at the reflected offsets and strides.</summary>
    void WriteUniform(UniformSlot s, byte* src, int rows, int columns, int count, bool isInt)
    {
        var p = program!;
        if (s.Vertex is { } v) { Scatter(p.VertexDefault, v, src, rows, columns, count, isInt); p.VertexDirty = true; }
        if (s.Fragment is { } f) { Scatter(p.FragmentDefault, f, src, rows, columns, count, isInt); p.FragmentDirty = true; }
    }

    static void Scatter(byte[] block, UniformLookup at, byte* src, int rows, int columns, int count, bool isInt) => GlUniforms.Scatter(block, at, src, rows, columns, count, isInt);
}
