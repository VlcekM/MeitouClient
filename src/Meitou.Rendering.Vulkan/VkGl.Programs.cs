using Meitou.Rendering.Vulkan.Shaders;
using Silk.NET.OpenGL;
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
        public DescriptorSetLayout SetLayout;
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
    }

    static GlslProgramCompiler? sharedCompiler;
    GlslProgramCompiler Compiler => sharedCompiler ??= new GlslProgramCompiler(new ShaderCompileOptions { RemapClipDepth = !device.HasDepthClipControl });
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
        p.VertexModule = CreateModule(c.VertexSpirv);
        p.FragmentModule = CreateModule(c.FragmentSpirv);
        p.VertexDefaultBlock = c.Vertex.DefaultBlock;
        p.FragmentDefaultBlock = c.Fragment.DefaultBlock;
        p.VertexDefault = new byte[p.VertexDefaultBlock?.Size ?? 0];
        p.FragmentDefault = new byte[p.FragmentDefaultBlock?.Size ?? 0];
        foreach (var b in c.Vertex.Blocks.Concat(c.Fragment.Blocks))
            if (!b.IsDefault && b.Kind == BlockKind.Uniform && !p.BlockNames.Contains(b.Name)) p.BlockNames.Add(b.Name);
        p.InputLocations = [.. c.Vertex.Inputs.SelectMany(i => Enumerable.Range(i.Location, i.Slots))];
        p.InputKinds = [.. c.Vertex.Inputs.SelectMany(i => Enumerable.Repeat(i.Kind, i.Slots))];
        CreateLayouts(p);
        p.Linked = true;
    }

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
        var bindings = new List<DescriptorSetLayoutBinding>();
        void Add(ShaderReflection r, ShaderStageFlags stage)
        {
            foreach (var b in r.Blocks)
                if (b.Kind == BlockKind.Uniform)
                    bindings.Add(new DescriptorSetLayoutBinding((uint)b.Binding, DescriptorType.UniformBuffer, 1, stage));
            foreach (var s in r.Samplers)
                bindings.Add(new DescriptorSetLayoutBinding((uint)s.Binding, DescriptorType.CombinedImageSampler, (uint)Math.Max(1, s.ArrayLength), stage));
        }
        Add(p.Compiled!.Vertex, ShaderStageFlags.VertexBit);
        Add(p.Compiled.Fragment, ShaderStageFlags.FragmentBit);
        p.DescriptorCount = bindings.Sum(b => (int)b.DescriptorCount);
        p.PushDescriptors = device.HasPushDescriptor && p.DescriptorCount <= device.MaxPushDescriptors;
        var arr = bindings.ToArray();
        fixed (DescriptorSetLayoutBinding* pb = arr)
        {
            var info = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                Flags = p.PushDescriptors ? DescriptorSetLayoutCreateFlags.PushDescriptorBitKhr : 0,
                BindingCount = (uint)arr.Length,
                PBindings = pb,
            };
            Check(vk.CreateDescriptorSetLayout(dev, &info, null, out p.SetLayout));
        }
        var layout = p.SetLayout;
        var li = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &layout };
        Check(vk.CreatePipelineLayout(dev, &li, null, out p.Layout));
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
        var (vm, fm, sl, pl) = (p.VertexModule, p.FragmentModule, p.SetLayout, p.Layout);
        ForgetPipelines(p);
        device.Frames.DeferDelete(() =>
        {
            if (vm.Handle != 0) vk.DestroyShaderModule(dev, vm, null);
            if (fm.Handle != 0) vk.DestroyShaderModule(dev, fm, null);
            if (pl.Handle != 0) vk.DestroyPipelineLayout(dev, pl, null);
            if (sl.Handle != 0) vk.DestroyDescriptorSetLayout(dev, sl, null);
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
        var slot = new UniformSlot
        {
            Vertex = Loose(c.Vertex.FindUniform(name)),
            Fragment = Loose(c.Fragment.FindUniform(name)),
            VertexSampler = c.Vertex.FindSampler(name),
            FragmentSampler = c.Fragment.FindSampler(name),
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

    static void Scatter(byte[] block, UniformLookup at, byte* src, int rows, int columns, int count, bool isInt)
    {
        var m = at.Member;
        int elements = m.ArrayLength > 0 ? Math.Min(count, m.ArrayLength - at.Element) : Math.Min(count, 1);
        int colStride = m.IsMatrix ? m.MatrixStride : 16;
        int elemStride = m.ArrayLength > 0 ? m.ArrayStride : 0;
        rows = Math.Min(rows, m.Rows);
        columns = Math.Min(columns, m.Columns);
        fixed (byte* dst = block)
            for (int e = 0; e < elements; e++)
                for (int c = 0; c < columns; c++)
                    for (int r = 0; r < rows; r++)
                    {
                        int srcIndex = (e * columns + c) * rows + r;
                        uint bits = ((uint*)src)[srcIndex];
                        // A float written to an int/bool member (glUniform1f on a bool) and an int to a float member convert as GL does.
                        if (m.Kind == ScalarKind.Float && isInt) { float fv = (int)bits; bits = *(uint*)&fv; }
                        else if (m.Kind != ScalarKind.Float && !isInt) bits = (uint)(int)*(float*)&bits;
                        if (m.Kind == ScalarKind.Bool || (m.Kind == ScalarKind.UInt && !isInt)) bits = bits != 0 ? 1u : 0u;
                        *(uint*)(dst + at.Offset + e * elemStride + c * colStride + r * 4) = bits;
                    }
    }
}
