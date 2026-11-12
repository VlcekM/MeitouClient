using System.Text;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Gpu.Shaders;

/// <summary>
/// A small SPIR-V parser: just enough of the word stream to describe a shader's resources and interface.
/// </summary>
public static class SpirvReflection
{
    /// <summary>
    /// The SPIR-V magic number, the module's first word.
    /// </summary>
    const uint MagicNumber = 0x07230203;

    /// <summary>
    /// The SPIR-V opcodes the parser reads, with the SPIR-V specification's values.
    /// </summary>
    const int OpName = 5, OpMemberName = 6, OpTypeVoid = 19, OpTypeBool = 20, OpTypeInt = 21, OpTypeFloat = 22, OpTypeVector = 23,
        OpTypeMatrix = 24, OpTypeImage = 25, OpTypeSampler = 26, OpTypeSampledImage = 27, OpTypeArray = 28, OpTypeRuntimeArray = 29,
        OpTypeStruct = 30, OpTypePointer = 32, OpConstant = 43, OpFunction = 54, OpFunctionCall = 57, OpImageTexelPointer = 60, OpLoad = 61,
        OpStore = 62, OpAccessChain = 65, OpInBoundsAccessChain = 66, OpCopyObject = 83, OpVariable = 59, OpDecorate = 71, OpMemberDecorate = 72;

    /// <summary>
    /// The SPIR-V decorations the parser reads, with the SPIR-V specification's values.
    /// </summary>
    const int DecBufferBlock = 3, DecRowMajor = 4, DecArrayStride = 6, DecMatrixStride = 7, DecBuiltIn = 11,
        DecLocation = 30, DecBinding = 33, DecDescriptorSet = 34, DecOffset = 35;

    /// <summary>
    /// The SPIR-V storage classes the parser reads, with the SPIR-V specification's values.
    /// </summary>
    const int ScUniformConstant = 0, ScInput = 1, ScUniform = 2, ScOutput = 3, ScPushConstant = 9, ScStorageBuffer = 12;

    /// <summary>
    /// A SPIR-V type by its declaring instruction (the parser's model of <c>OpType*</c>).
    /// </summary>
    abstract record SType;

    /// <summary>
    /// <c>OpTypeVoid</c>.
    /// </summary>
    sealed record Void : SType;

    /// <summary>
    /// <c>OpTypeBool</c>.
    /// </summary>
    sealed record Bool : SType;

    /// <summary>
    /// <c>OpTypeInt</c>: the width in bits and the signedness.
    /// </summary>
    sealed record Int(int Width, bool Signed) : SType;

    /// <summary>
    /// <c>OpTypeFloat</c>: the width in bits.
    /// </summary>
    sealed record Float(int Width) : SType;

    /// <summary>
    /// <c>OpTypeVector</c>: the component type's id and the component count.
    /// </summary>
    sealed record Vector(uint Component, int Count) : SType;

    /// <summary>
    /// <c>OpTypeMatrix</c>: the column type's id and the column count.
    /// </summary>
    sealed record Matrix(uint Column, int Columns) : SType;

    /// <summary>
    /// <c>OpTypeImage</c>. Sampled: the sampled type's id; Dim, Depth, Arrayed and Ms as the instruction gives them; Usage: 1 sampled, 2 storage.
    /// </summary>
    sealed record Image(uint Sampled, int Dim, int Depth, bool Arrayed, bool Ms, uint Usage = 0) : SType;

    /// <summary>
    /// <c>OpTypeSampler</c>.
    /// </summary>
    sealed record Sampler : SType;

    /// <summary>
    /// <c>OpTypeSampledImage</c>: the image type's id.
    /// </summary>
    sealed record SampledImage(uint Image) : SType;

    /// <summary>
    /// <c>OpTypeArray</c>: the element type's id and the id of the constant holding the length.
    /// </summary>
    sealed record Array(uint Element, uint LengthId) : SType;

    /// <summary>
    /// <c>OpTypeRuntimeArray</c>: the element type's id.
    /// </summary>
    sealed record RuntimeArray(uint Element) : SType;

    /// <summary>
    /// <c>OpTypeStruct</c>: the member types' ids.
    /// </summary>
    sealed record Struct(uint[] Members) : SType;

    /// <summary>
    /// <c>OpTypePointer</c>: the storage class and the pointee type's id.
    /// </summary>
    sealed record Pointer(int Storage, uint Pointee) : SType;

    /// <summary>
    /// What one pass over the word stream gathers: types, names, constants, decorations, variables and the variables the functions use.
    /// </summary>
    sealed class Module
    {
        /// <summary>
        /// The types by result id.
        /// </summary>
        public readonly Dictionary<uint, SType> Types = [];
        /// <summary>
        /// The <c>OpName</c> names by id.
        /// </summary>
        public readonly Dictionary<uint, string> Names = [];
        /// <summary>
        /// The <c>OpMemberName</c> names by struct id and member index.
        /// </summary>
        public readonly Dictionary<(uint, int), string> MemberNames = [];
        /// <summary>
        /// The values of 32-bit <c>OpConstant</c>s by result id (array lengths).
        /// </summary>
        public readonly Dictionary<uint, uint> Constants = [];

        /// <summary>
        /// The decorations by id: decoration to its first operand (0 when it has none).
        /// </summary>
        public readonly Dictionary<uint, Dictionary<int, uint>> Decorations = [];

        /// <summary>
        /// The member decorations by struct id and member index: decoration to its first operand (0 when it has none).
        /// </summary>
        public readonly Dictionary<(uint, int), Dictionary<int, uint>> MemberDecorations = [];

        /// <summary>
        /// The global <c>OpVariable</c>s: the result id, the pointer type's id and the storage class.
        /// </summary>
        public readonly List<(uint Id, uint Type, int Storage)> Variables = [];

        /// <summary>
        /// The variables a function body loads, stores, indexes or passes on (the rest are inactive).
        /// </summary>
        public readonly HashSet<uint> Used = [];

        /// <summary>
        /// The stream has reached the first <c>OpFunction</c> (uses are counted from there).
        /// </summary>
        public bool InFunction;
    }

    /// <summary>
    /// Reflects a SPIR-V module: its uniform, push-constant and storage blocks, samplers, storage images and stage interface; throws when the bytes
    /// are not a whole module.
    /// </summary>
    public static ShaderReflection Parse(byte[] spirv)
    {
        ArgumentNullException.ThrowIfNull(spirv);
        if (spirv.Length < 20 || spirv.Length % 4 != 0) throw new ArgumentException("Not a SPIR-V module (size).", nameof(spirv));

        var w = new uint[spirv.Length / 4];
        Buffer.BlockCopy(spirv, 0, w, 0, spirv.Length);
        if (w[0] != MagicNumber) throw new ArgumentException("Not a SPIR-V module (magic).", nameof(spirv));

        var m = new Module();
        for (int i = 5; i < w.Length;)
        {
            int count = (int)(w[i] >> 16), op = (int)(w[i] & 0xFFFF);
            if (count == 0 || i + count > w.Length) throw new ArgumentException("Truncated SPIR-V instruction.", nameof(spirv));

            ReadInstruction(m, op, w.AsSpan(i + 1, count - 1));
            i += count;
        }

        return Build(m);
    }

    /// <summary>
    /// Reads one instruction: a function body's uses and the names; everything else goes to <see cref="ReadType"/>.
    /// </summary>
    static void ReadInstruction(Module m, int op, ReadOnlySpan<uint> a)
    {
        switch (op)
        {
            case OpFunction:
                m.InFunction = true;
                break;
            case OpLoad or OpAccessChain or OpInBoundsAccessChain or OpCopyObject or OpImageTexelPointer when m.InFunction && a.Length > 2:
                m.Used.Add(a[2]);
                break;
            case OpStore when m.InFunction:
                m.Used.Add(a[0]);
                break;
            case OpFunctionCall when m.InFunction:
                for (int i = 3; i < a.Length; i++) m.Used.Add(a[i]);
                break;
            case OpName:
                m.Names[a[0]] = Str(a[1..]);
                break;
            case OpMemberName:
                m.MemberNames[(a[0], (int)a[1])] = Str(a[2..]);
                break;
            default:
                ReadType(m, op, a);
                break;
        }
    }

    /// <summary>
    /// Reads a type, constant, variable or decoration instruction; other opcodes are ignored.
    /// </summary>
    static void ReadType(Module m, int op, ReadOnlySpan<uint> a)
    {
        switch (op)
        {
            case OpTypeVoid: m.Types[a[0]] = new Void(); break;
            case OpTypeBool: m.Types[a[0]] = new Bool(); break;
            case OpTypeInt: m.Types[a[0]] = new Int((int)a[1], a[2] != 0); break;
            case OpTypeFloat: m.Types[a[0]] = new Float((int)a[1]); break;
            case OpTypeVector: m.Types[a[0]] = new Vector(a[1], (int)a[2]); break;
            case OpTypeMatrix: m.Types[a[0]] = new Matrix(a[1], (int)a[2]); break;
            case OpTypeImage: m.Types[a[0]] = new Image(a[1], (int)a[2], (int)a[3], a[4] != 0, a[5] != 0, a.Length > 6 ? a[6] : 0); break;
            case OpTypeSampler: m.Types[a[0]] = new Sampler(); break;
            case OpTypeSampledImage: m.Types[a[0]] = new SampledImage(a[1]); break;
            case OpTypeArray: m.Types[a[0]] = new Array(a[1], a[2]); break;
            case OpTypeRuntimeArray: m.Types[a[0]] = new RuntimeArray(a[1]); break;
            case OpTypeStruct: m.Types[a[0]] = new Struct(a[1..].ToArray()); break;
            case OpTypePointer: m.Types[a[0]] = new Pointer((int)a[1], a[2]); break;
            case OpConstant:
                if (a.Length >= 3) m.Constants[a[1]] = a[2];
                break;
            case OpVariable: m.Variables.Add((a[1], a[0], (int)a[2])); break;
            case OpDecorate: ReadDecoration(m, a); break;
            case OpMemberDecorate: ReadMemberDecoration(m, a); break;
        }
    }

    /// <summary>
    /// Records a decoration of an id.
    /// </summary>
    static void ReadDecoration(Module m, ReadOnlySpan<uint> a)
    {
        if (!m.Decorations.TryGetValue(a[0], out var d)) m.Decorations[a[0]] = d = [];

        d[(int)a[1]] = a.Length > 2 ? a[2] : 0;
    }

    /// <summary>
    /// Records a decoration of a struct member.
    /// </summary>
    static void ReadMemberDecoration(Module m, ReadOnlySpan<uint> a)
    {
        var key = (a[0], (int)a[1]);
        if (!m.MemberDecorations.TryGetValue(key, out var d)) m.MemberDecorations[key] = d = [];

        d[(int)a[2]] = a.Length > 3 ? a[3] : 0;
    }

    /// <summary>
    /// A NUL-terminated UTF-8 literal string packed in words.
    /// </summary>
    static string Str(ReadOnlySpan<uint> words)
    {
        var bytes = new List<byte>();
        foreach (uint word in words)
        {
            for (int s = 0; s < 32; s += 8)
            {
                byte b = (byte)(word >> s);
                if (b == 0) return Encoding.UTF8.GetString(bytes.ToArray());

                bytes.Add(b);
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>
    /// Turns the gathered module into the reflection: each variable by its storage class, interface variables sorted by location.
    /// </summary>
    static ShaderReflection Build(Module m)
    {
        var blocks = new List<UniformBlockInfo>();
        var samplers = new List<SamplerInfo>();
        var storageImages = new List<StorageImageInfo>();
        var inputs = new List<InterfaceVariable>();
        var outputs = new List<InterfaceVariable>();
        var inactive = new List<string>();

        foreach (var (id, typeId, storage) in m.Variables)
        {
            if (m.Types[typeId] is not Pointer ptr) continue;

            m.Decorations.TryGetValue(id, out var dec);
            dec ??= [];
            string name = m.Names.GetValueOrDefault(id, "");
            uint pointee = ptr.Pointee;

            switch (storage)
            {
                case ScUniform or ScPushConstant or ScStorageBuffer:
                    AddBlock(m, id, storage, pointee, name, dec, blocks, inactive);
                    break;
                case ScUniformConstant:
                    AddUniformConstant(m, id, pointee, name, dec, samplers, storageImages, inactive);
                    break;
                case ScInput or ScOutput:
                    AddInterface(m, storage, pointee, name, dec, inputs, outputs);
                    break;
            }
        }

        inputs.Sort((a, b) => a.Location.CompareTo(b.Location));
        outputs.Sort((a, b) => a.Location.CompareTo(b.Location));
        return new ShaderReflection
        {
            Blocks = blocks, Samplers = samplers, StorageImages = storageImages, Inputs = inputs, Outputs = outputs, Inactive = inactive,
        };
    }

    /// <summary>
    /// A uniform, push constant or storage block variable: its members, unless the block is unused (then it is inactive).
    /// </summary>
    static void AddBlock(Module m, uint id, int storage, uint pointee, string name, Dictionary<int, uint> dec, List<UniformBlockInfo> blocks,
        List<string> inactive)
    {
        uint structId = StripArrays(m, pointee);
        if (m.Types.GetValueOrDefault(structId) is not Struct st) return;

        var members = new List<BlockMember>();
        int size = 0;
        for (int i = 0; i < st.Members.Length; i++)
            size = Math.Max(size, AddMember(m, structId, i, st.Members[i], "", 0, members));

        if (storage != ScPushConstant && !m.Used.Contains(id))
        {
            inactive.Add(m.Names.GetValueOrDefault(structId, name));
            return;
        }

        bool bufferBlock = m.Decorations.TryGetValue(structId, out var sd) && sd.ContainsKey(DecBufferBlock);
        var kind = storage == ScPushConstant ? BlockKind.PushConstant
            : storage == ScStorageBuffer || bufferBlock ? BlockKind.StorageBuffer
            : BlockKind.Uniform;
        if (kind == BlockKind.Uniform) size = (size + 15) & ~15;

        blocks.Add(new UniformBlockInfo
        {
            Name = m.Names.GetValueOrDefault(structId, ""),
            InstanceName = name,
            Kind = kind,
            Set = (int)dec.GetValueOrDefault(DecDescriptorSet),
            Binding = kind == BlockKind.PushConstant ? -1 : (int)dec.GetValueOrDefault(DecBinding),
            Size = size,
            Members = members,
        });
    }

    /// <summary>
    /// A sampler, a sampled image array or a storage image.
    /// </summary>
    static void AddUniformConstant(Module m, uint id, uint pointee, string name, Dictionary<int, uint> dec, List<SamplerInfo> samplers,
        List<StorageImageInfo> storageImages, List<string> inactive)
    {
        int len = 0;
        uint t = pointee;
        if (m.Types.GetValueOrDefault(t) is Array arr)
        {
            len = ArrayLength(m, arr);
            t = arr.Element;
        }
        else if (m.Types.GetValueOrDefault(t) is RuntimeArray rarr)
        {
            // a bindless array
            len = -1;
            t = rarr.Element;
        }

        // a storage image
        if (m.Types.GetValueOrDefault(t) is Image { Usage: 2 } && len == 0)
        {
            if (m.Used.Contains(id))
                storageImages.Add(new StorageImageInfo(name, (int)dec.GetValueOrDefault(DecDescriptorSet), (int)dec.GetValueOrDefault(DecBinding)));
            else inactive.Add(name);
            return;
        }

        if (m.Types.GetValueOrDefault(t) is not SampledImage si || m.Types[si.Image] is not Image img) return;

        if (!m.Used.Contains(id))
        {
            inactive.Add(name);
            return;
        }

        var dim = img.Dim switch
        {
            0 => SamplerDimension.Dim1D,
            1 => SamplerDimension.Dim2D,
            2 => SamplerDimension.Dim3D,
            3 => SamplerDimension.Cube,
            _ => SamplerDimension.Other,
        };
        samplers.Add(new SamplerInfo(name, (int)dec.GetValueOrDefault(DecDescriptorSet), (int)dec.GetValueOrDefault(DecBinding), dim, img.Arrayed,
            img.Ms, img.Depth == 1, ScalarOf(m, img.Sampled), len));
    }

    /// <summary>
    /// A stage input or output with a location (built-ins, and structs, are left out).
    /// </summary>
    static void AddInterface(Module m, int storage, uint pointee, string name, Dictionary<int, uint> dec, List<InterfaceVariable> inputs,
        List<InterfaceVariable> outputs)
    {
        if (dec.ContainsKey(DecBuiltIn) || !dec.TryGetValue(DecLocation, out uint loc)) return;

        int len = 0;
        uint t = pointee;
        if (m.Types.GetValueOrDefault(t) is Array arr)
        {
            len = ArrayLength(m, arr);
            t = arr.Element;
        }

        if (m.Types.GetValueOrDefault(t) is Struct) return;

        var (kind, cols, rows) = Shape(m, t);
        (storage == ScInput ? inputs : outputs).Add(new InterfaceVariable(name, (int)loc, kind, cols, rows, len));
    }

    /// <summary>
    /// The innermost element type of a (possibly nested) array type.
    /// </summary>
    static uint StripArrays(Module m, uint t)
    {
        while (m.Types.GetValueOrDefault(t) is Array a) t = a.Element;

        return t;
    }

    /// <summary>
    /// An array type's length from its length constant; 0 when the constant is unknown.
    /// </summary>
    static int ArrayLength(Module m, Array a) => m.Constants.TryGetValue(a.LengthId, out uint n) ? (int)n : 0;

    /// <summary>
    /// The scalar kind of a type (of its components or elements); float for anything else.
    /// </summary>
    static ScalarKind ScalarOf(Module m, uint t) => m.Types.GetValueOrDefault(t) switch
    {
        Float => ScalarKind.Float,
        Int { Signed: false } => ScalarKind.UInt,
        Int => ScalarKind.Int,
        Bool => ScalarKind.Bool,
        Vector v => ScalarOf(m, v.Component),
        Matrix mx => ScalarOf(m, mx.Column),
        Array a => ScalarOf(m, a.Element),
        _ => ScalarKind.Float,
    };

    /// <summary>
    /// A type's scalar kind, columns and rows (a vector is one column; a scalar 1×1).
    /// </summary>
    static (ScalarKind Kind, int Cols, int Rows) Shape(Module m, uint t) => m.Types.GetValueOrDefault(t) switch
    {
        Vector v => (ScalarOf(m, v.Component), 1, v.Count),
        Matrix mx => (ScalarOf(m, mx.Column), mx.Columns, ((Vector)m.Types[mx.Column]).Count),
        _ => (ScalarOf(m, t), 1, 1),
    };

    /// <summary>
    /// The bytes of a type's scalar (bool 4); 4 for anything else.
    /// </summary>
    static int ScalarBytes(Module m, uint t) => m.Types.GetValueOrDefault(t) switch
    {
        Float f => f.Width / 8,
        Int i => i.Width / 8,
        Bool => 4,
        Vector v => ScalarBytes(m, v.Component),
        Matrix mx => ScalarBytes(m, mx.Column),
        Array a => ScalarBytes(m, a.Element),
        _ => 4,
    };

    /// <summary>
    /// Adds a member (flattening structs) and returns the byte just past it.
    /// </summary>
    static int AddMember(Module m, uint structId, int index, uint typeId, string prefix, int baseOffset, List<BlockMember> into)
    {
        m.MemberDecorations.TryGetValue((structId, index), out var dec);
        dec ??= [];
        string name = prefix + m.MemberNames.GetValueOrDefault((structId, index), "member" + index);
        int offset = baseOffset + (int)dec.GetValueOrDefault(DecOffset);
        return AddTyped(m, typeId, name, offset, dec, into);
    }

    /// <summary>
    /// Adds a member of a type at an offset (flattening structs and arrays of arrays, sized from its strides) and returns the byte just past it.
    /// </summary>
    static int AddTyped(Module m, uint typeId, string name, int offset, Dictionary<int, uint> dec, List<BlockMember> into)
    {
        int arrayLen = 0, arrayStride = 0;
        uint t = typeId;
        if (m.Types.GetValueOrDefault(t) is Array arr)
        {
            arrayLen = ArrayLength(m, arr);
            arrayStride = (int)dec.GetValueOrDefault(DecArrayStride);
            t = arr.Element;

            // Arrays of arrays: reported as one flat run of the innermost element (the outer stride is the inner run's size).
            if (m.Types.GetValueOrDefault(t) is Array)
            {
                int innerLen = 1;
                while (m.Types.GetValueOrDefault(t) is Array i2)
                {
                    innerLen *= ArrayLength(m, i2);
                    t = i2.Element;
                }

                arrayStride = innerLen > 0 ? arrayStride / innerLen : arrayStride;
                arrayLen *= innerLen;
            }
        }

        if (m.Types.GetValueOrDefault(t) is Struct st)
            return AddStructMembers(m, st, t, name, offset, arrayLen, arrayStride, into);

        var (kind, cols, rows) = Shape(m, t);
        int matrixStride = (int)dec.GetValueOrDefault(DecMatrixStride);
        bool rowMajor = dec.ContainsKey(DecRowMajor);
        int scalar = ScalarBytes(m, t);
        int size = cols > 1 ? (rowMajor ? rows : cols) * matrixStride : rows * scalar;
        if (arrayLen > 0)
        {
            if (arrayStride == 0) arrayStride = size;

            size = arrayStride * arrayLen;
        }

        into.Add(new BlockMember(name, offset, size, kind, cols, rows, arrayLen, arrayStride, matrixStride, rowMajor));
        return offset + size;
    }

    /// <summary>
    /// The members of a struct member (every element of an array of structs), and the byte just past them (rounded to 16 for a lone struct).
    /// </summary>
    static int AddStructMembers(Module m, Struct st, uint t, string name, int offset, int arrayLen, int arrayStride, List<BlockMember> into)
    {
        int end = offset;
        int n = Math.Max(1, arrayLen);
        for (int e = 0; e < n; e++)
        {
            string en = arrayLen > 0 ? $"{name}[{e}]" : name;
            int elemBase = offset + e * arrayStride;
            for (int i = 0; i < st.Members.Length; i++)
                end = Math.Max(end, AddMember(m, t, i, st.Members[i], en + ".", elemBase, into));
        }

        return arrayLen > 0 ? offset + arrayLen * arrayStride : (end + 15) & ~15;
    }
}
