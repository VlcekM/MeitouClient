using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu.Core;
using Meitou.Rendering.Gpu.Shaders;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu;

/// <summary>The descriptor model a <see cref="GpuProgram"/> follows (docs/renderer-native.md 3).</summary>
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
    /// The bindless set's combined arrays become plain image arrays with a sampler array beside each (the integer kinds have no
    /// sampler: their fetches never read one) — what spirv-cross's Metal argument buffer needs and GLSL cannot express (no separate
    /// integer or depth texture types). Every access chain, load and function that takes a sampler along is rewritten: a sample
    /// loads the image and its sampler from the parallel arrays with the same index and pairs them again, a function gains one
    /// sampler parameter, a call one argument. <paramref name="set"/> is the table's set; <paramref name="samplerOffset"/> is where
    /// the sampler arrays' bindings begin above the texture arrays' (the backend's table total).
    /// </summary>
    public static byte[] BindlessFetchImages(byte[] spirv, uint set, uint samplerOffset)
    {
        var copy = (byte[])spirv.Clone();
        var w = MemoryMarshal.Cast<byte, uint>(copy.AsSpan());
        var sets = new Dictionary<uint, uint>();                  // decorations
        var bindings = new Dictionary<uint, uint>();
        var intScalars = new HashSet<uint>();                     // OpTypeInt ids
        var images = new Dictionary<uint, (uint Scalar, uint Dim, uint Depth)>(); // OpTypeImage: sampled type, dim, depth
        var sampledImages = new Dictionary<uint, uint>();         // sampled image id -> image
        var arrayElem = new Dictionary<uint, (uint Elem, uint Len)>(); // array id -> element, length (a constant id, copied verbatim)
        var pointerAt = new Dictionary<uint, uint>();             // pointer id -> pointee
        var variables = new Dictionary<uint, uint>();             // variable id -> pointer
        var chains = new Dictionary<uint, uint>();                // access chain result -> base variable
        var chainIndex = new Dictionary<uint, uint>();            // access chain result -> its index operand
        for (int i = 5; i < w.Length;)
        {
            uint op = w[i] & 0xFFFF, count = w[i] >> 16;
            if (count == 0) break;
            var a = w.Slice(i + 1, (int)count - 1);
            if (op == 71 && count == 4) { if (a[1] == 34) sets[a[0]] = a[2]; else if (a[1] == 33) bindings[a[0]] = a[2]; }
            else if (op == 21) intScalars.Add(a[0]);
            else if (op == 25 && count >= 8) images[a[0]] = (a[1], a[2], a[3]);
            else if (op == 27 && count == 3) sampledImages[a[0]] = a[1];
            else if (op == 28 && count == 4) arrayElem[a[0]] = (a[1], a[2]);
            else if (op == 32 && count == 4) pointerAt[a[0]] = a[2];
            else if (op == 59) variables[a[1]] = a[0];
            else if (op == 65 && count >= 5) { chains[a[1]] = a[2]; chainIndex[a[1]] = a[3]; }
            i += (int)count;
        }
        // The arrays to split: the set's array variables whose element is a sampled image. The integer kinds' images get no sampler.
        uint next = w[3];                                     // fresh ids above the module's bound (word 3 = max id + 1)
        uint samplerType = 0;
        var ours = new Dictionary<uint, Made>();                 // old array pointer -> the new ids
        var oursByElem = new Dictionary<uint, Made>();           // the old sampled image type -> the new ids
        foreach (var (id, ptr) in variables)
        {
            if (sets.GetValueOrDefault(id) != set) continue;
            if (!pointerAt.TryGetValue(ptr, out uint arr) || !arrayElem.TryGetValue(arr, out var shape)) continue;
            if (!sampledImages.TryGetValue(shape.Elem, out uint image) || !images.TryGetValue(image, out var format)) continue;
            bool integer = intScalars.Contains(format.Scalar);
            if (integer) { }
            else if (samplerType == 0) samplerType = next++;
            var made = new Made
            {
                OldPtr = ptr, ArrayPtr = next++, ElemPtr = next++, Array = next++, Image = image, Sampled = shape.Elem, Length = shape.Len,
                Integer = integer, Binding = bindings.GetValueOrDefault(id) + samplerOffset,
            };
            if (!integer) { made.SamplerArray = next++; made.SamplerArrayPtr = next++; made.SamplerElemPtr = next++; made.SamplerVar = next++; }
            ours[ptr] = made;
            oursByElem[shape.Elem] = made;
        }
        if (ours.Count == 0) return copy;
        var oursVars = new Dictionary<uint, uint>();              // ours' variable id -> old array pointer
        foreach (var (id, ptr) in variables)
            if (ours.ContainsKey(ptr)) oursVars[id] = ptr;
        var oursPointers = new Dictionary<uint, Made>();          // pointer to one of ours' sampled images -> the array
        foreach (var (ptr, pointee) in pointerAt)
            if (oursByElem.TryGetValue(pointee, out var pointed)) oursPointers[ptr] = pointed;
        var pointerParams = new Dictionary<uint, uint>();         // a function parameter of pointer type -> its sampler parameter
        var pointerMade = new Dictionary<uint, Made>();           // the same -> the array it points into
        var chainMade = new Dictionary<uint, Made>();             // access chain result -> the array it indexes
        var imagesDone = new Dictionary<uint, (Made Array, uint Sampler)>(); // image value (load, parameter) -> its array, its sampler value
        var loadIndex = new Dictionary<uint, uint>();             // image load result -> the index operand it used
        bool decorated = false;
        // Pass 2: the rewrite and the rebuild — the new types go in right after each old array pointer, before every use.
        var list = new List<uint>(w.Length + 128) { w[0], w[1], w[2], w[3], w[4] };
        for (int i = 5; i < w.Length;)
        {
            uint op = w[i] & 0xFFFF, count = w[i] >> 16;
            if (count == 0) break;
            var a = w.Slice(i + 1, (int)count - 1);
            // The new sampler type and the sampler variables' decorations go into the annotations section, before the first type.
            if (!decorated && op is >= 19 and <= 39)
            {
                decorated = true;
                foreach (var made2 in ours.Values)
                {
                    if (made2.Integer) continue;
                    list.AddRange([71u | (4u << 16), made2.SamplerVar, 34u, set]);          // DescriptorSet
                    list.AddRange([71u | (4u << 16), made2.SamplerVar, 33u, made2.Binding]);  // Binding
                }
                if (samplerType != 0) list.AddRange([26u | (2u << 16), samplerType]);          // OpTypeSampler
            }
            if (op == 15)
            {
                // OpEntryPoint lists every global variable the entry point uses: the new sampler variables are such.
                var entry = w.Slice(i, (int)count).ToArray().ToList();
                foreach (var extra in ours.Values)
                    if (!extra.Integer) entry.Add(extra.SamplerVar);
                entry[0] = 15u | ((uint)entry.Count << 16);
                list.AddRange(entry);
            }
            else if (op == 32 && ours.TryGetValue(a[0], out var declared))
            {
                // The old array pointer as-is, then the array of the image (the length's constant id copied), its pointer, the
                // element's pointer, and (every kind but the integer ones) its sampler array, pointer and variable.
                list.AddRange(w.Slice(i, (int)count).ToArray());
                list.AddRange([28u | (4u << 16), declared.Array, declared.Image, declared.Length]);
                list.AddRange([32u | (4u << 16), declared.ArrayPtr, 0u, declared.Array]);
                list.AddRange([32u | (4u << 16), declared.ElemPtr, 0u, declared.Image]);
                if (!declared.Integer)
                {
                    list.AddRange([28u | (4u << 16), declared.SamplerArray, samplerType, declared.Length]);
                    list.AddRange([32u | (4u << 16), declared.SamplerArrayPtr, 0u, declared.SamplerArray]);
                    list.AddRange([32u | (4u << 16), declared.SamplerElemPtr, 0u, samplerType]);
                    list.AddRange([59u | (4u << 16), declared.SamplerArrayPtr, declared.SamplerVar, 0u]);   // OpVariable, UniformConstant
                }
            }
            else if (op == 33)
            {
                // OpTypeFunction: a parameter of a split sampled image type takes the image and its sampler.
                var types = new List<uint> { 33u, a[0], a[1] };
                for (int k = 2; k < a.Length; k++)
                {
                    // A sampled image parameter becomes the image (and its sampler); a pointer to one becomes a pointer to the image.
                    if (oursByElem.TryGetValue(a[k], out var byValue)) { types.Add(byValue.Image); types.Add(samplerType); }
                    else if (oursPointers.TryGetValue(a[k], out var byPointer)) { types.Add(byPointer.ElemPtr); types.Add(samplerType); }
                    else types.Add(a[k]);
                }
                types[0] = 33u | ((uint)types.Count << 16);
                list.AddRange(types);
            }
            else if (op == 55 && oursByElem.TryGetValue(a[0], out var parameter))
            {
                uint sampler = next++;
                imagesDone[a[1]] = (parameter, sampler);
                list.AddRange([55u | (3u << 16), parameter.Image, a[1]]);                     // OpFunctionParameter: the image
                list.AddRange([55u | (3u << 16), samplerType, sampler]);                       // and its sampler
            }
            else if (op == 55 && oursPointers.TryGetValue(a[0], out var pointed))
            {
                // A sampler the GLSL passes to a function comes as a pointer to the sampled image; the callee loads it. It takes
                // the image's pointer and the sampler itself.
                uint sampler = next++;
                pointerParams[a[1]] = sampler;
                pointerMade[a[1]] = pointed;
                list.AddRange([55u | (3u << 16), pointed.ElemPtr, a[1]]);
                list.AddRange([55u | (3u << 16), samplerType, sampler]);
            }
            else if (op == 57)
            {
                // OpFunctionCall: every image argument brings its sampler right after it (a load's from the parallel array here, a
                // parameter's own otherwise).
                var args = new List<uint> { 57u, a[0], a[1], a[2] };
                var before = new List<uint>();
                var samplers = new uint[a.Length];
                for (int k = 3; k < a.Length; k++)
                {
                    Made value;
                    uint index, beside = 0;
                    if (imagesDone.TryGetValue(a[k], out var loaded)) { value = loaded.Array; index = loadIndex.GetValueOrDefault(a[k]); beside = loaded.Sampler; }
                    else if (chainMade.TryGetValue(a[k], out var chainArray)) { value = chainArray; index = chainIndex.GetValueOrDefault(a[k]); }
                    else continue;
                    if (beside != 0) { samplers[k] = beside; continue; }
                    uint chain = next++, load = next++;
                    before.AddRange([65u | (5u << 16), value.SamplerElemPtr, chain, value.SamplerVar, index]);
                    before.AddRange([61u | (4u << 16), samplerType, load, chain]);
                    samplers[k] = load;
                }
                list.AddRange(before);
                for (int k = 3; k < a.Length; k++)
                {
                    args.Add(a[k]);
                    if (samplers[k] != 0) args.Add(samplers[k]);
                }
                args[0] = 57u | ((uint)args.Count << 16);
                list.AddRange(args);
            }
            else if (op == 59 && oursVars.TryGetValue(a[1], out uint varPtr))
            {
                var words = w.Slice(i, (int)count).ToArray();
                words[1] = ours[varPtr].ArrayPtr;                  // the variable's declared type
                list.AddRange(words);
            }
            else
            {
                var words = w.Slice(i, (int)count).ToArray();
                if (op == 65 && pointerAt.TryGetValue(a[0], out uint pointee) && oursByElem.TryGetValue(pointee, out var chainArray))
                {
                    words[1] = chainArray.ElemPtr;                 // the chain's result type: pointer to the plain image
                    chainMade[a[1]] = chainArray;
                }
                else if (op == 61 && a[2] != 0 && chainMade.TryGetValue(a[2], out var loadArray))
                {
                    words[1] = loadArray.Image;                    // the load dereferences the pointer: the image itself
                    imagesDone[a[1]] = (loadArray, 0);
                    loadIndex[a[1]] = chainIndex.GetValueOrDefault(a[2]);
                }
                else if (op == 61 && a[2] != 0 && pointerParams.TryGetValue(a[2], out uint samplerParam) && pointerMade.TryGetValue(a[2], out var loadMade))
                {
                    words[1] = loadMade.Image;
                    imagesDone[a[1]] = (loadMade, samplerParam);
                }
                else if (op == 100 && a.Length > 2 && imagesDone.TryGetValue(a[2], out var imageOnly) && imageOnly.Array.Integer)
                    words[0] = 83u | (count << 16);                // OpImage -> OpCopyObject: the operand is already the image
                else if (a.Length > 2 && imagesDone.TryGetValue(a[2], out var sampled) && !sampled.Array.Integer)
                {
                    // A sample needs the pair: the image is at hand, the sampler is loaded (from the parallel array with the
                    // same index) or is the function's own parameter.
                    uint pair = next++, sampler;
                    if (sampled.Sampler != 0) sampler = sampled.Sampler;
                    else
                    {
                        uint index = loadIndex.GetValueOrDefault(a[2]), chain = next++, load = next++;
                        list.AddRange([65u | (5u << 16), sampled.Array.SamplerElemPtr, chain, sampled.Array.SamplerVar, index]);
                        list.AddRange([61u | (4u << 16), samplerType, load, chain]);
                        sampler = load;
                    }
                    list.AddRange([86u | (5u << 16), sampled.Array.Sampled, pair, a[2], sampler]);   // OpSampledImage
                    words[3] = pair;
                }
                list.AddRange(words);
            }
            i += (int)count;
        }
        uint[] outWords = [.. list];
        outWords[3] = next;                                    // the id bound grew with what the rewrite made
        return MemoryMarshal.AsBytes(outWords.AsSpan()).ToArray();
    }

    /// <summary>One combined array's split: the old array pointer, the plain image array's ids, and (not for the integer kinds) its sampler array's.</summary>
    sealed class Made
    {
        public uint OldPtr, ArrayPtr, ElemPtr, Array, Image, Sampled, Length, Binding;
        public bool Integer;
        public uint SamplerArray, SamplerArrayPtr, SamplerElemPtr, SamplerVar;
    }
}

/// <summary>
/// Compiled shader modules with their reflection and pipeline layout. Legacy programs: the modules VkGl would create (same SPIR-V, default
/// blocks moved to set 1) and VkGl's layout (set 0 pushed when it fits, set 1 dynamic uniform buffers). Compute programs: set 0 from the
/// reflection (pushed when it fits) and a push-constant range. Native programs: the layout the caller's model gives.
/// </summary>
internal sealed unsafe class VulkanProgram : IDisposable
{
    readonly VulkanDevice device;
    readonly DescriptorSetLayout[] ownedSetLayouts;

    internal VulkanProgram(VulkanDevice device, ProgramInfo info, DescriptorSetLayout[] setLayouts, DescriptorSetLayout[] ownedSetLayouts,
        PipelineLayout layout, bool pushDescriptors, ShaderStageFlags pushConstantStages)
    {
        this.device = device;
        Info = info;
        SetLayouts = setLayouts;
        this.ownedSetLayouts = ownedSetLayouts;
        Layout = layout;
        PushDescriptors = pushDescriptors;
        PushConstantStages = pushConstantStages;
        if (info.VertexCode is not null) VertexModule = CreateModule(info.VertexCode);
        if (info.FragmentCode is not null) FragmentModule = CreateModule(info.FragmentCode);
        if (info.TessControlCode is not null) TessControlModule = CreateModule(info.TessControlCode);
        if (info.TessEvaluationCode is not null) TessEvaluationModule = CreateModule(info.TessEvaluationCode);
        if (info.ComputeCode is not null) ComputeModule = CreateModule(info.ComputeCode);
    }

    public ProgramInfo Info { get; }
    public int Index => Info.Index;
    public string Name => Info.Name;
    public ShaderModel Model => Info.Model;
    public byte[]? VertexCode => Info.VertexCode;
    public byte[]? FragmentCode => Info.FragmentCode;
    public byte[]? ComputeCode => Info.ComputeCode;
    public ShaderModule VertexModule { get; }
    public ShaderModule FragmentModule { get; }
    public ShaderModule ComputeModule { get; }
    /// <summary>The tessellation stages of a native program made by <see cref="ShaderLibrary.NativeTessellated"/> (0 otherwise): its pipelines
    /// draw patch lists of three control points.</summary>
    public ShaderModule TessControlModule { get; }
    public ShaderModule TessEvaluationModule { get; }
    public bool Tessellated => TessEvaluationModule.Handle != 0;
    /// <summary>The legacy compile result (reflection of both stages, default blocks), null for other models.</summary>
    public CompiledProgram? Compiled => Info.Compiled;
    public ShaderReflection? VertexReflection => Info.VertexReflection;
    public ShaderReflection? FragmentReflection => Info.FragmentReflection;
    public ShaderReflection? ComputeReflection => Info.ComputeReflection;
    public DescriptorSetLayout[] SetLayouts { get; }
    /// <summary>The program's sets in the engine's words (the layout the backend made).</summary>
    public GpuSetLayout[] NeutralSets => Info.NeutralSets;
    public PipelineLayout Layout { get; }
    /// <summary>Set 0 is a push-descriptor set.</summary>
    public bool PushDescriptors { get; }
    public uint PushConstantBytes => Info.PushConstantBytes;
    public ShaderStageFlags PushConstantStages { get; }

    /// <summary>The GLSL of the fragment stage as compiled, kept for the native model only (the bench's instrumented variant is made from it).</summary>
    public string? FragmentSource { get => Info.FragmentSource; set => Info.FragmentSource = value; }

    /// <summary>A shader module of this device from <paramref name="spirv"/>; the caller destroys it.</summary>
    internal ShaderModule CreateModule(byte[] spirv)
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
        var (tcm, tem) = (TessControlModule, TessEvaluationModule);
        device.Frames.DeferDelete(() =>
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
}

/// <summary>
/// Compiles programs for the three models (docs/renderer-native.md 3). The legacy path is the one VkGl uses (VkGl compiles through
/// <see cref="Compiler"/> too), so a legacy program's modules are byte-identical to VkGl's.
/// </summary>
public sealed unsafe class ShaderLibrary : IGpuShaders
{
    readonly VulkanDevice device;
    readonly List<VulkanProgram> programs = [];

    public ShaderLibrary(VulkanDevice device)
    {
        this.device = device;
        Compiler = new GlslProgramCompiler(new ShaderCompileOptions { DepthReversed = true });
    }

    /// <summary>The backend's identity of a program handle (the Vulkan-only internals resolve through this).</summary>
    internal VulkanProgram Backend(GpuProgram program) => programs[program.Index];

    /// <summary>Releases a program (its backend artifacts go once the frames in flight are done); the index is never reused.</summary>
    public void Dispose(GpuProgram program) => Backend(program).Dispose();

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
    public GpuProgram Legacy(string vertexGlsl, string fragmentGlsl, string name)
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
        // The engine's words for the same two sets: set 0 the named blocks and samplers, set 1 the default blocks (their offset is given when the set is bound).
        var slots = new List<GpuSlot>();
        void AddSlots(ShaderReflection r, GpuStage stage)
        {
            foreach (var b in r.Blocks)
                if (b.Kind == BlockKind.Uniform && !b.IsDefault) slots.Add(new GpuSlot((uint)b.Binding, GpuSlotKind.UniformBuffer, stage));
            foreach (var s in r.Samplers) slots.Add(new GpuSlot((uint)s.Binding, GpuSlotKind.CombinedSampler, stage, (uint)Math.Max(1, s.ArrayLength)));
        }
        AddSlots(c.Vertex, GpuStage.Vertex);
        AddSlots(c.Fragment, GpuStage.Fragment);
        var defaultSlots = new List<GpuSlot>();
        if (c.Vertex.DefaultBlock is not null) defaultSlots.Add(new GpuSlot(0, GpuSlotKind.UniformBuffer, GpuStage.Vertex, 1, OffsetAtBind: true));
        if (c.Fragment.DefaultBlock is not null) defaultSlots.Add(new GpuSlot(1, GpuSlotKind.UniformBuffer, GpuStage.Fragment, 1, OffsetAtBind: true));
        var info = new ProgramInfo { Index = programs.Count, Name = name, Model = ShaderModel.Legacy, VertexCode = v, FragmentCode = f, Compiled = c,
            VertexReflection = c.Vertex, FragmentReflection = c.Fragment,
            NeutralSets = [GpuSetLayout.Written([.. slots]), GpuSetLayout.Fixed([.. defaultSlots])] };
        programs.Add(new VulkanProgram(device, info, [set0, set1], [set0, set1], layout, push, 0));
        return new GpuProgram(info.Index, info);
    }

    /// <summary>A compute program: set 0 from the reflection (uniform and storage blocks, samplers; pushed when it fits), other sets
    /// from <paramref name="extraSets"/> (sets 1.., not owned), push constants as the shader declares them.</summary>
    public GpuProgram Compute(string glsl, string name, params GpuSetLayout[] extraSets)
    {
        var code = Compiler.CompileCompute(glsl);
        var r = SpirvReflection.Parse(code);
        var bindings = new List<DescriptorSetLayoutBinding>();
        foreach (var b in r.Blocks)
            if (b.Kind != BlockKind.PushConstant && b.Set == 0)
                bindings.Add(new DescriptorSetLayoutBinding((uint)b.Binding, b.Kind == BlockKind.StorageBuffer ? DescriptorType.StorageBuffer : DescriptorType.UniformBuffer, 1, ShaderStageFlags.ComputeBit));
        foreach (var s in r.StorageImages)
            if (s.Set == 0) bindings.Add(new DescriptorSetLayoutBinding((uint)s.Binding, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit));
        foreach (var s in r.Samplers)
            if (s.Set == 0 && s.ArrayLength < 0)
                throw new InvalidOperationException($"{name}: runtime-sized sampler array '{s.Name}' in set 0; declare the bindless table at an extra set (BindlessTable.Declarations(1))");
            else if (s.Set == 0) bindings.Add(new DescriptorSetLayoutBinding((uint)s.Binding, DescriptorType.CombinedImageSampler, (uint)Math.Max(1, s.ArrayLength), ShaderStageFlags.ComputeBit));
        bool push = device.HasPushDescriptor && bindings.Sum(b => (int)b.DescriptorCount) <= device.MaxPushDescriptors;
        var set0 = CreateSetLayout([.. bindings], push ? DescriptorSetLayoutCreateFlags.PushDescriptorBitKhr : 0);
        uint pc = (uint)(r.Blocks.FirstOrDefault(b => b.Kind == BlockKind.PushConstant)?.Size ?? 0);
        var sets = new[] { set0 }.Concat(extraSets.Select(VkLayout)).ToArray();
        var layout = CreateLayout(sets, pc, ShaderStageFlags.ComputeBit);
        var info = new ProgramInfo { Index = programs.Count, Name = name, Model = ShaderModel.Compute, ComputeCode = code,
            ComputeReflection = SpirvReflection.Parse(code), PushConstantBytes = pc,
            NeutralSets = [GpuSetLayout.Written([]), .. extraSets] };
        programs.Add(new VulkanProgram(device, info, sets, [set0], layout, push, pc > 0 ? ShaderStageFlags.ComputeBit : 0));
        return new GpuProgram(info.Index, info);
    }

    /// <summary>A compute program whose set 0 is given (<paramref name="set0"/>, always pushed: it needs push descriptors) instead of reflected:
    /// for descriptor types the reflection does not read (storage images, acceleration structures). Push constants as the shader declares them.</summary>
    public GpuProgram Compute(string glsl, string name, ReadOnlySpan<(uint Binding, DescriptorType Type)> set0, params GpuSetLayout[] extraSets)
    {
        if (!device.HasPushDescriptor) throw new NotSupportedException($"{name}: needs push descriptors");
        var code = Compiler.CompileCompute(glsl);
        var r = SpirvReflection.Parse(code);
        var bindings = new DescriptorSetLayoutBinding[set0.Length];
        for (int i = 0; i < set0.Length; i++) bindings[i] = new DescriptorSetLayoutBinding(set0[i].Binding, set0[i].Type, 1, ShaderStageFlags.ComputeBit);
        var layout0 = CreateSetLayout(bindings, DescriptorSetLayoutCreateFlags.PushDescriptorBitKhr);
        uint pc = (uint)(r.Blocks.FirstOrDefault(b => b.Kind == BlockKind.PushConstant)?.Size ?? 0);
        var sets = new[] { layout0 }.Concat(extraSets.Select(VkLayout)).ToArray();
        var layout = CreateLayout(sets, pc, ShaderStageFlags.ComputeBit);
        var info = new ProgramInfo { Index = programs.Count, Name = name, Model = ShaderModel.Compute, ComputeCode = code,
            ComputeReflection = SpirvReflection.Parse(code), PushConstantBytes = pc,
            NeutralSets = [GpuSetLayout.Written([]), .. extraSets] };
        programs.Add(new VulkanProgram(device, info, sets, [layout0], layout, true, pc > 0 ? ShaderStageFlags.ComputeBit : 0));
        return new GpuProgram(info.Index, info);
    }

    /// <summary>A native-model program: strict GLSL 450, the layout given by the model (<paramref name="setLayouts"/>, not owned) and a
    /// push-constant block of <paramref name="pushConstantBytes"/> (at most 128) visible to both stages.</summary>
    public GpuProgram Native(string vertexGlsl, string fragmentGlsl, string name, GpuSetLayout[] setLayouts, uint pushConstantBytes)
    {
        if (pushConstantBytes > 128) throw new ArgumentOutOfRangeException(nameof(pushConstantBytes), "push constants are limited to 128 bytes (the guaranteed minimum)");
        // The stages are linked by name as GL does (docs/renderer-native.md 3.4: InterfaceLocations stays for both models): varyings without
        // an explicit location get the same one in both stages, fragment outputs 0, 1, ... (explicit locations are kept).
        var (vs, fs) = InterfaceLocations.Apply(vertexGlsl, fragmentGlsl);
        var v = Compiler.CompileNative(vs, fragment: false);
        var f = Compiler.CompileNative(fs, fragment: true);
        var stages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;
        var layouts = setLayouts.Select(VkLayout).ToArray();
        var layout = CreateLayout(layouts, pushConstantBytes, stages);
        var info = new ProgramInfo { Index = programs.Count, Name = name, Model = ShaderModel.Native, VertexCode = v, FragmentCode = f,
            VertexReflection = SpirvReflection.Parse(v), FragmentReflection = SpirvReflection.Parse(f),
            PushConstantBytes = pushConstantBytes, NeutralSets = setLayouts };
        var program = new VulkanProgram(device, info, layouts, [], layout, false, pushConstantBytes > 0 ? stages : 0);
        programs.Add(program);
        var made = new GpuProgram(info.Index, info);
        program.FragmentSource = fs;
        foreach (var block in info.VertexReflection!.Blocks.Concat(info.FragmentReflection!.Blocks))
            if (block.Kind == BlockKind.PushConstant && block.Size > pushConstantBytes)
            {
                program.Dispose();
                throw new InvalidOperationException($"{name}: push-constant block '{block.Name}' is {block.Size} bytes, the layout's range {pushConstantBytes}");
            }
        // The samplers declared in the bindless set must be the table's arrays (a float view read through a usampler is undefined).
        int bindless = Array.FindIndex(setLayouts, l => l == GpuSetLayout.Bindless);
        if (bindless >= 0)
            foreach (var s in info.VertexReflection!.Samplers.Concat(info.FragmentReflection!.Samplers))
                if (s.Set == bindless)
                {
                    try { BindlessTable.Check(s, name); }
                    catch { program.Dispose(); throw; }
                }
        return made;
    }

    /// <summary>The layouts the engine's sets were made into (one per set, shared by every program that uses it).</summary>
    readonly Dictionary<GpuSetLayout, DescriptorSetLayout> setLayouts = [];

    /// <summary>Destroys the layouts the engine's sets were made into (once every frame that could use them has finished).</summary>
    public void Dispose()
    {
        var (vk, dev) = (device.Vk, device.Device);
        var layouts = setLayouts.Values.ToArray();
        setLayouts.Clear();
        device.Frames.DeferDelete(() => { foreach (var l in layouts) vk.DestroyDescriptorSetLayout(dev, l, null); });
    }

    /// <summary>The Vulkan layout of a set the engine described: the bindless table's is the table's own, the rest are made here.</summary>
    internal DescriptorSetLayout VkLayout(GpuSetLayout set)
    {
        if (set.IsBindless) return BindlessLayout;
        if (setLayouts.TryGetValue(set, out var made)) return made;
        var bindings = new DescriptorSetLayoutBinding[set.Slots.Length];
        for (int i = 0; i < bindings.Length; i++)
        {
            var s = set.Slots[i];
            bindings[i] = new DescriptorSetLayoutBinding(s.Binding, VkDescriptorType(s), 1);
            bindings[i].StageFlags = VkStageFlags(s.Stages);
        }
        var layout = CreateSetLayout(bindings, CanPush(set) ? DescriptorSetLayoutCreateFlags.PushDescriptorBitKhr : 0);
        setLayouts[set] = layout;
        return layout;
    }

    /// <summary>Whether the device can push this set's descriptors (the renderers write it for every draw).</summary>
    internal bool CanPush(GpuSetLayout set) => device.HasPushDescriptor && set.PerDraw && set.Slots.Sum(s => (int)s.Count) <= device.MaxPushDescriptors;

    /// <summary>Whether set <paramref name="set"/> of <paramref name="program"/> was made as a push-descriptor set: a legacy or compute
    /// program's set 0 as it was compiled (it fit), any other set as <see cref="CanPush"/> made its layout.</summary>
    internal bool Pushes(VulkanProgram program, uint set) =>
        set == 0 && program.Model is ShaderModel.Legacy or ShaderModel.Compute ? program.PushDescriptors
            : set < program.NeutralSets.Length && !program.NeutralSets[set].IsBindless && CanPush(program.NeutralSets[set]);

    static DescriptorType VkDescriptorType(GpuSlot slot) => slot.Kind switch
    {
        GpuSlotKind.UniformBuffer => slot.OffsetAtBind ? DescriptorType.UniformBufferDynamic : DescriptorType.UniformBuffer,
        GpuSlotKind.StorageBuffer => DescriptorType.StorageBuffer,
        GpuSlotKind.CombinedSampler => DescriptorType.CombinedImageSampler,
        _ => DescriptorType.StorageImage,
    };

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

    /// <summary>
    /// A native-model program with tessellation (the device's <see cref="VulkanDevice.HasTessellation"/>): the vertex stage feeds the control stage
    /// and the control stage the evaluation stage by explicit locations; the evaluation stage's outputs reach the fragment stage by name as in
    /// <see cref="Native"/>. The push-constant range is visible to all four stages (a push must name them all: <see cref="ShaderProgram.PushConstantStages"/>).
    /// </summary>
    public GpuProgram NativeTessellated(string vertexGlsl, string controlGlsl, string evaluationGlsl, string fragmentGlsl, string name,
        GpuSetLayout[] setLayouts, uint pushConstantBytes)
    {
        if (!device.HasTessellation) throw new NotSupportedException($"{name}: the device has no tessellation shaders");
        if (pushConstantBytes > 128) throw new ArgumentOutOfRangeException(nameof(pushConstantBytes), "push constants are limited to 128 bytes (the guaranteed minimum)");
        var (es, fs) = InterfaceLocations.Apply(evaluationGlsl, fragmentGlsl);
        var v = Compiler.CompileNative(vertexGlsl, fragment: false);
        var tc = Compiler.CompileNativeTessellation(controlGlsl, evaluation: false);
        var te = Compiler.CompileNativeTessellation(es, evaluation: true);
        var f = Compiler.CompileNative(fs, fragment: true);
        var stages = ShaderStageFlags.VertexBit | ShaderStageFlags.TessellationControlBit | ShaderStageFlags.TessellationEvaluationBit | ShaderStageFlags.FragmentBit;
        var layouts = setLayouts.Select(VkLayout).ToArray();
        var layout = CreateLayout(layouts, pushConstantBytes, stages);
        var info = new ProgramInfo
        {
            Index = programs.Count, Name = name, Model = ShaderModel.Native, VertexCode = v, FragmentCode = f,
            TessControlCode = tc, TessEvaluationCode = te,
            VertexReflection = SpirvReflection.Parse(v), FragmentReflection = SpirvReflection.Parse(f),
            PushConstantBytes = pushConstantBytes, NeutralSets = setLayouts,
        };
        var program = new VulkanProgram(device, info, layouts, [], layout, false, pushConstantBytes > 0 ? stages : 0);
        programs.Add(program);
        program.FragmentSource = fs;
        return new GpuProgram(info.Index, info);
    }

    /// <summary>The stages the native model's shared descriptor sets are visible to: vertex and fragment, and the tessellation stages where the
    /// device has them (so a tessellated program can share the sets of the plain ones).</summary>
    public ShaderStageFlags GraphicsStages => ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit |
        (device.HasTessellation ? ShaderStageFlags.TessellationControlBit | ShaderStageFlags.TessellationEvaluationBit : 0);

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
