using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Gpu.Shaders;

/// <summary>
/// The scalar type of a reflected member, sampler result or interface variable.
/// </summary>
public enum ScalarKind
{
    /// <summary>
    /// 32-bit float (also anything the parser does not know).
    /// </summary>
    Float,
    /// <summary>
    /// Signed integer.
    /// </summary>
    Int,
    /// <summary>
    /// Unsigned integer (a bool in a block reads as this).
    /// </summary>
    UInt,
    /// <summary>
    /// Boolean.
    /// </summary>
    Bool
}

/// <summary>
/// A sampled image's dimensionality (SPIR-V's <c>Dim</c>).
/// </summary>
public enum SamplerDimension
{
    /// <summary>
    /// One-dimensional.
    /// </summary>
    Dim1D,
    /// <summary>
    /// Two-dimensional.
    /// </summary>
    Dim2D,
    /// <summary>
    /// Three-dimensional.
    /// </summary>
    Dim3D,
    /// <summary>
    /// A cube map.
    /// </summary>
    Cube,
    /// <summary>
    /// Any other SPIR-V dimensionality.
    /// </summary>
    Other
}

/// <summary>
/// What kind of block a <see cref="UniformBlockInfo"/> is.
/// </summary>
public enum BlockKind
{
    /// <summary>
    /// A uniform buffer block (the default block included).
    /// </summary>
    Uniform,
    /// <summary>
    /// The push-constant block.
    /// </summary>
    PushConstant,
    /// <summary>
    /// A shader storage buffer block.
    /// </summary>
    StorageBuffer
}

/// <summary>
/// A member of a uniform block (a loose GLSL uniform for the default block).
/// </summary>
/// <param name="Name">GLSL name; nested structs are flattened as <c>a.b</c> (<c>a[1].b</c> for struct arrays).</param>
/// <param name="Offset">Absolute byte offset from the start of the block.</param>
/// <param name="Size">Bytes the member occupies (whole array included).</param>
/// <param name="Kind">Scalar kind. A GLSL <c>bool</c> in a block is stored as a 32-bit uint, so it reads as UInt.</param>
/// <param name="Columns">Matrix columns; 1 for scalars and vectors.</param>
/// <param name="Rows">Vector components or matrix rows; 1 for scalars.</param>
/// <param name="ArrayLength">0 when the member is not an array.</param>
public sealed record BlockMember(string Name, int Offset, int Size, ScalarKind Kind, int Columns, int Rows, int ArrayLength, int ArrayStride,
    int MatrixStride, bool RowMajor)
{
    /// <summary>
    /// Whether the member is a matrix (more than one column).
    /// </summary>
    public bool IsMatrix => Columns > 1;
}

/// <summary>
/// A uniform, push constant or storage block. Binding is -1 for push constants.
/// </summary>
public sealed class UniformBlockInfo
{
    /// <summary>
    /// The block's type name: <c>gl_DefaultUniformBlock</c> or the GLSL block name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The block variable's name (empty when the SPIR-V names none).
    /// </summary>
    public required string InstanceName { get; init; }

    /// <summary>
    /// Uniform, push constant or storage block.
    /// </summary>
    public required BlockKind Kind { get; init; }

    /// <summary>
    /// The descriptor set (0 when undecorated, as push constants are).
    /// </summary>
    public required int Set { get; init; }

    /// <summary>
    /// The binding; -1 for push constants.
    /// </summary>
    public required int Binding { get; init; }

    /// <summary>
    /// Bytes to bind: the last member's end, rounded up to 16 for uniform blocks (std140).
    /// </summary>
    public required int Size { get; init; }

    /// <summary>
    /// The members, flattened.
    /// </summary>
    public required IReadOnlyList<BlockMember> Members { get; init; }

    /// <summary>
    /// Whether this is GL's default block (the loose uniforms).
    /// </summary>
    public bool IsDefault => Name == "gl_DefaultUniformBlock";
}

/// <summary>
/// A sampled image (a GLSL sampler). <see cref="ArrayLength"/>: 0 for a single sampler, the length of a sized array, -1 for a runtime-sized array
/// (the bindless arrays of the native model).
/// </summary>
public sealed record SamplerInfo(string Name, int Set, int Binding, SamplerDimension Dimension, bool Arrayed, bool Multisampled, bool Depth,
    ScalarKind SampledKind, int ArrayLength);

/// <summary>
/// A storage image of a compute program (set, binding).
/// </summary>
public sealed record StorageImageInfo(string Name, int Set, int Binding);

/// <summary>
/// A stage input or output variable (built-ins excluded).
/// </summary>
public sealed record InterfaceVariable(string Name, int Location, ScalarKind Kind, int Columns, int Rows, int ArrayLength)
{
    /// <summary>
    /// Consecutive locations taken: columns times array length.
    /// </summary>
    public int Slots => Columns * Math.Max(1, ArrayLength);
}

/// <summary>
/// A uniform found by its GL-style name: the block, the member, and the element for <c>uName[3]</c>.
/// </summary>
public readonly record struct UniformLookup(UniformBlockInfo Block, BlockMember Member, int Element)
{
    /// <summary>
    /// Byte offset of the element inside the block.
    /// </summary>
    public int Offset => Member.Offset + Element * Member.ArrayStride;
}

/// <summary>
/// What a SPIR-V module declares: blocks, samplers and stage interface.
/// </summary>
public sealed class ShaderReflection
{
    /// <summary>
    /// The uniform, push constant and storage blocks that are used.
    /// </summary>
    public required IReadOnlyList<UniformBlockInfo> Blocks { get; init; }

    /// <summary>
    /// The samplers that are used.
    /// </summary>
    public required IReadOnlyList<SamplerInfo> Samplers { get; init; }

    /// <summary>
    /// The storage images (compute programs): <c>image2D</c> and the like, declared with a format layout.
    /// </summary>
    public IReadOnlyList<StorageImageInfo> StorageImages { get; init; } = [];

    /// <summary>
    /// The stage inputs, by location.
    /// </summary>
    public required IReadOnlyList<InterfaceVariable> Inputs { get; init; }

    /// <summary>
    /// The stage outputs, by location.
    /// </summary>
    public required IReadOnlyList<InterfaceVariable> Outputs { get; init; }

    /// <summary>
    /// Names of blocks and samplers the module declares but never uses: glslang gives them no binding, so they are left out of <see cref="Blocks"/>
    /// and <see cref="Samplers"/>. GL calls them inactive.
    /// </summary>
    public IReadOnlyList<string> Inactive { get; init; } = [];

    /// <summary>
    /// The default block (the loose uniforms); null when the module has none.
    /// </summary>
    public UniformBlockInfo? DefaultBlock => Blocks.FirstOrDefault(b => b.IsDefault);

    /// <summary>
    /// A used sampler by GLSL name; null when there is none.
    /// </summary>
    public SamplerInfo? FindSampler(string name) => Samplers.FirstOrDefault(s => s.Name == name);

    /// <summary>
    /// Finds a uniform by the name GL would use: <c>uName</c>, <c>uName[0]</c>, <c>uName[3]</c>, a member of a named block or <c>Block.member</c>.
    /// Null when absent or the element is out of range.
    /// </summary>
    public UniformLookup? FindUniform(string glName)
    {
        if (string.IsNullOrEmpty(glName)) return null;

        foreach (var block in Blocks)
        {
            if (Match(block, glName) is { } hit) return hit;

            string prefix = block.Name + ".";
            if (glName.StartsWith(prefix, StringComparison.Ordinal) && Match(block, glName[prefix.Length..]) is { } hit2) return hit2;
        }

        return null;
    }

    /// <summary>
    /// A member of a block by name, or an element of an array member by <c>name[i]</c>; null when absent or out of range.
    /// </summary>
    static UniformLookup? Match(UniformBlockInfo block, string name)
    {
        foreach (var m in block.Members)
            if (m.Name == name) return new UniformLookup(block, m, 0);

        // name[index] and name[index].rest where the member is an array
        int open = name.IndexOf('[');
        if (open <= 0) return null;

        int close = name.IndexOf(']', open);
        if (close < 0 || !int.TryParse(name.AsSpan(open + 1, close - open - 1), out int index) || index < 0) return null;

        string baseName = name[..open] + name[(close + 1)..];
        foreach (var m in block.Members)
            if (m.ArrayLength > 0 && m.Name == baseName && index < m.ArrayLength) return new UniformLookup(block, m, index);

        return null;
    }
}
