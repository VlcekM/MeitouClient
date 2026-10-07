using System.Text;

namespace Meitou.Rendering.Gpu.Shaders;

/// <summary>A small SPIR-V parser: just enough of the word stream to describe a shader's resources and interface.</summary>
public static class SpirvReflection
{
    const uint MagicNumber = 0x07230203;

    // opcodes
    const int OpName = 5, OpMemberName = 6, OpTypeVoid = 19, OpTypeBool = 20, OpTypeInt = 21, OpTypeFloat = 22, OpTypeVector = 23,
        OpTypeMatrix = 24, OpTypeImage = 25, OpTypeSampler = 26, OpTypeSampledImage = 27, OpTypeArray = 28, OpTypeRuntimeArray = 29,
        OpTypeStruct = 30, OpTypePointer = 32, OpConstant = 43, OpFunction = 54, OpFunctionCall = 57, OpImageTexelPointer = 60, OpLoad = 61, OpStore = 62, OpAccessChain = 65, OpInBoundsAccessChain = 66, OpCopyObject = 83, OpVariable = 59, OpDecorate = 71, OpMemberDecorate = 72;

    // decorations
    const int DecBufferBlock = 3, DecRowMajor = 4, DecArrayStride = 6, DecMatrixStride = 7, DecBuiltIn = 11,
        DecLocation = 30, DecBinding = 33, DecDescriptorSet = 34, DecOffset = 35;

    // storage classes
    const int ScUniformConstant = 0, ScInput = 1, ScUniform = 2, ScOutput = 3, ScPushConstant = 9, ScStorageBuffer = 12;

    abstract record SType;
    sealed record TVoid : SType;
    sealed record TBool : SType;
    sealed record TInt(int Width, bool Signed) : SType;
    sealed record TFloat(int Width) : SType;
    sealed record TVector(uint Component, int Count) : SType;
    sealed record TMatrix(uint Column, int Columns) : SType;
    sealed record TImage(uint Sampled, int Dim, int Depth, bool Arrayed, bool Ms) : SType;
    sealed record TSampler : SType;
    sealed record TSampledImage(uint Image) : SType;
    sealed record TArray(uint Element, uint LengthId) : SType;
    sealed record TRuntimeArray(uint Element) : SType;
    sealed record TStruct(uint[] Members) : SType;
    sealed record TPointer(int Storage, uint Pointee) : SType;

    sealed class Module
    {
        public readonly Dictionary<uint, SType> Types = [];
        public readonly Dictionary<uint, string> Names = [];
        public readonly Dictionary<(uint, int), string> MemberNames = [];
        public readonly Dictionary<uint, uint> Constants = [];
        public readonly Dictionary<uint, Dictionary<int, uint>> Decorations = [];                // id -> decoration -> first operand
        public readonly Dictionary<(uint, int), Dictionary<int, uint>> MemberDecorations = [];    // (struct, member) -> decoration -> first operand
        public readonly List<(uint Id, uint Type, int Storage)> Variables = [];
        public readonly HashSet<uint> Used = [];   // variables a function body loads, stores, indexes or passes on
        public bool InFunction;
    }

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

    static void ReadInstruction(Module m, int op, ReadOnlySpan<uint> a)
    {
        switch (op)
        {
            case OpFunction: m.InFunction = true; break;
            case OpLoad or OpAccessChain or OpInBoundsAccessChain or OpCopyObject or OpImageTexelPointer when m.InFunction && a.Length > 2: m.Used.Add(a[2]); break;
            case OpStore when m.InFunction: m.Used.Add(a[0]); break;
            case OpFunctionCall when m.InFunction: for (int i = 3; i < a.Length; i++) m.Used.Add(a[i]); break;
            case OpName: m.Names[a[0]] = Str(a[1..]); break;
            case OpMemberName: m.MemberNames[(a[0], (int)a[1])] = Str(a[2..]); break;
            case OpTypeVoid: m.Types[a[0]] = new TVoid(); break;
            case OpTypeBool: m.Types[a[0]] = new TBool(); break;
            case OpTypeInt: m.Types[a[0]] = new TInt((int)a[1], a[2] != 0); break;
            case OpTypeFloat: m.Types[a[0]] = new TFloat((int)a[1]); break;
            case OpTypeVector: m.Types[a[0]] = new TVector(a[1], (int)a[2]); break;
            case OpTypeMatrix: m.Types[a[0]] = new TMatrix(a[1], (int)a[2]); break;
            case OpTypeImage: m.Types[a[0]] = new TImage(a[1], (int)a[2], (int)a[3], a[4] != 0, a[5] != 0); break;
            case OpTypeSampler: m.Types[a[0]] = new TSampler(); break;
            case OpTypeSampledImage: m.Types[a[0]] = new TSampledImage(a[1]); break;
            case OpTypeArray: m.Types[a[0]] = new TArray(a[1], a[2]); break;
            case OpTypeRuntimeArray: m.Types[a[0]] = new TRuntimeArray(a[1]); break;
            case OpTypeStruct: m.Types[a[0]] = new TStruct(a[1..].ToArray()); break;
            case OpTypePointer: m.Types[a[0]] = new TPointer((int)a[1], a[2]); break;
            case OpConstant: if (a.Length >= 3) m.Constants[a[1]] = a[2]; break;
            case OpVariable: m.Variables.Add((a[1], a[0], (int)a[2])); break;
            case OpDecorate:
            {
                if (!m.Decorations.TryGetValue(a[0], out var d)) m.Decorations[a[0]] = d = [];
                d[(int)a[1]] = a.Length > 2 ? a[2] : 0;
                break;
            }
            case OpMemberDecorate:
            {
                var key = (a[0], (int)a[1]);
                if (!m.MemberDecorations.TryGetValue(key, out var d)) m.MemberDecorations[key] = d = [];
                d[(int)a[2]] = a.Length > 3 ? a[3] : 0;
                break;
            }
        }
    }

    static string Str(ReadOnlySpan<uint> words)
    {
        var bytes = new List<byte>();
        foreach (uint word in words)
            for (int s = 0; s < 32; s += 8)
            {
                byte b = (byte)(word >> s);
                if (b == 0) return Encoding.UTF8.GetString(bytes.ToArray());
                bytes.Add(b);
            }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    static ShaderReflection Build(Module m)
    {
        var blocks = new List<UniformBlockInfo>();
        var samplers = new List<SamplerInfo>();
        var inputs = new List<InterfaceVariable>();
        var outputs = new List<InterfaceVariable>();
        var inactive = new List<string>();

        foreach (var (id, typeId, storage) in m.Variables)
        {
            if (m.Types[typeId] is not TPointer ptr) continue;
            m.Decorations.TryGetValue(id, out var dec);
            dec ??= [];
            string name = m.Names.GetValueOrDefault(id, "");
            uint pointee = ptr.Pointee;

            switch (storage)
            {
                case ScUniform or ScPushConstant or ScStorageBuffer:
                {
                    uint structId = StripArrays(m, pointee);
                    if (m.Types.GetValueOrDefault(structId) is not TStruct st) break;
                    var members = new List<BlockMember>();
                    int size = 0;
                    for (int i = 0; i < st.Members.Length; i++)
                        size = Math.Max(size, AddMember(m, structId, i, st.Members[i], "", 0, members));
                    if (storage != ScPushConstant && !m.Used.Contains(id)) { inactive.Add(m.Names.GetValueOrDefault(structId, name)); break; }
                    bool bufferBlock = m.Decorations.TryGetValue(structId, out var sd) && sd.ContainsKey(DecBufferBlock);
                    var kind = storage == ScPushConstant ? BlockKind.PushConstant : storage == ScStorageBuffer || bufferBlock ? BlockKind.StorageBuffer : BlockKind.Uniform;
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
                    break;
                }
                case ScUniformConstant:
                {
                    int len = 0;
                    uint t = pointee;
                    if (m.Types.GetValueOrDefault(t) is TArray arr) { len = ArrayLength(m, arr); t = arr.Element; }
                    else if (m.Types.GetValueOrDefault(t) is TRuntimeArray rarr) { len = -1; t = rarr.Element; }   // a bindless array
                    if (m.Types.GetValueOrDefault(t) is not TSampledImage si || m.Types[si.Image] is not TImage img) break;
                    if (!m.Used.Contains(id)) { inactive.Add(name); break; }
                    var dim = img.Dim switch { 0 => SamplerDimension.Dim1D, 1 => SamplerDimension.Dim2D, 2 => SamplerDimension.Dim3D, 3 => SamplerDimension.Cube, _ => SamplerDimension.Other };
                    samplers.Add(new SamplerInfo(name, (int)dec.GetValueOrDefault(DecDescriptorSet), (int)dec.GetValueOrDefault(DecBinding), dim, img.Arrayed, img.Ms, img.Depth == 1, ScalarOf(m, img.Sampled), len));
                    break;
                }
                case ScInput or ScOutput:
                {
                    if (dec.ContainsKey(DecBuiltIn) || !dec.TryGetValue(DecLocation, out uint loc)) break;
                    int len = 0;
                    uint t = pointee;
                    if (m.Types.GetValueOrDefault(t) is TArray arr) { len = ArrayLength(m, arr); t = arr.Element; }
                    if (m.Types.GetValueOrDefault(t) is TStruct) break;
                    var (kind, cols, rows) = Shape(m, t);
                    (storage == ScInput ? inputs : outputs).Add(new InterfaceVariable(name, (int)loc, kind, cols, rows, len));
                    break;
                }
            }
        }

        inputs.Sort((a, b) => a.Location.CompareTo(b.Location));
        outputs.Sort((a, b) => a.Location.CompareTo(b.Location));
        return new ShaderReflection { Blocks = blocks, Samplers = samplers, Inputs = inputs, Outputs = outputs, Inactive = inactive };
    }

    static uint StripArrays(Module m, uint t)
    {
        while (m.Types.GetValueOrDefault(t) is TArray a) t = a.Element;
        return t;
    }

    static int ArrayLength(Module m, TArray a) => m.Constants.TryGetValue(a.LengthId, out uint n) ? (int)n : 0;

    static ScalarKind ScalarOf(Module m, uint t) => m.Types.GetValueOrDefault(t) switch
    {
        TFloat => ScalarKind.Float,
        TInt { Signed: false } => ScalarKind.UInt,
        TInt => ScalarKind.Int,
        TBool => ScalarKind.Bool,
        TVector v => ScalarOf(m, v.Component),
        TMatrix mx => ScalarOf(m, mx.Column),
        TArray a => ScalarOf(m, a.Element),
        _ => ScalarKind.Float,
    };

    static (ScalarKind Kind, int Cols, int Rows) Shape(Module m, uint t) => m.Types.GetValueOrDefault(t) switch
    {
        TVector v => (ScalarOf(m, v.Component), 1, v.Count),
        TMatrix mx => (ScalarOf(m, mx.Column), mx.Columns, ((TVector)m.Types[mx.Column]).Count),
        _ => (ScalarOf(m, t), 1, 1),
    };

    static int ScalarBytes(Module m, uint t) => m.Types.GetValueOrDefault(t) switch
    {
        TFloat f => f.Width / 8,
        TInt i => i.Width / 8,
        TBool => 4,
        TVector v => ScalarBytes(m, v.Component),
        TMatrix mx => ScalarBytes(m, mx.Column),
        TArray a => ScalarBytes(m, a.Element),
        _ => 4,
    };

    /// <summary>Adds a member (flattening structs) and returns the byte just past it.</summary>
    static int AddMember(Module m, uint structId, int index, uint typeId, string prefix, int baseOffset, List<BlockMember> into)
    {
        m.MemberDecorations.TryGetValue((structId, index), out var dec);
        dec ??= [];
        string name = prefix + m.MemberNames.GetValueOrDefault((structId, index), "member" + index);
        int offset = baseOffset + (int)dec.GetValueOrDefault(DecOffset);
        return AddTyped(m, typeId, name, offset, dec, into);
    }

    static int AddTyped(Module m, uint typeId, string name, int offset, Dictionary<int, uint> dec, List<BlockMember> into)
    {
        int arrayLen = 0, arrayStride = 0;
        uint t = typeId;
        if (m.Types.GetValueOrDefault(t) is TArray arr)
        {
            arrayLen = ArrayLength(m, arr);
            arrayStride = (int)dec.GetValueOrDefault(DecArrayStride);
            t = arr.Element;
            // Arrays of arrays: reported as one flat run of the innermost element (the outer stride is the inner run's size).
            if (m.Types.GetValueOrDefault(t) is TArray inner)
            {
                int innerLen = 1;
                while (m.Types.GetValueOrDefault(t) is TArray i2) { innerLen *= ArrayLength(m, i2); t = i2.Element; }
                arrayStride = innerLen > 0 ? arrayStride / innerLen : arrayStride;
                arrayLen *= innerLen;
                _ = inner;
            }
        }

        if (m.Types.GetValueOrDefault(t) is TStruct st)
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
}
