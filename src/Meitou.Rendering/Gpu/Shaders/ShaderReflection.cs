namespace Meitou.Rendering.Vulkan.Shaders;

public enum ScalarKind { Float, Int, UInt, Bool }

public enum SamplerDimension { Dim1D, Dim2D, Dim3D, Cube, Other }

public enum BlockKind { Uniform, PushConstant, StorageBuffer }

/// <summary>A member of a uniform block (a loose GLSL uniform for the default block).</summary>
/// <param name="Name">GLSL name; nested structs are flattened as <c>a.b</c> (<c>a[1].b</c> for struct arrays).</param>
/// <param name="Offset">Absolute byte offset from the start of the block.</param>
/// <param name="Size">Bytes the member occupies (whole array included).</param>
/// <param name="Kind">Scalar kind. A GLSL <c>bool</c> in a block is stored as a 32-bit uint, so it reads as UInt.</param>
/// <param name="Columns">Matrix columns; 1 for scalars and vectors.</param>
/// <param name="Rows">Vector components or matrix rows; 1 for scalars.</param>
/// <param name="ArrayLength">0 when the member is not an array.</param>
public sealed record BlockMember(string Name, int Offset, int Size, ScalarKind Kind, int Columns, int Rows, int ArrayLength, int ArrayStride, int MatrixStride, bool RowMajor)
{
    public bool IsMatrix => Columns > 1;
}

/// <summary>A uniform, push constant or storage block. Binding is -1 for push constants.</summary>
public sealed class UniformBlockInfo
{
    /// <summary>The block's type name: <c>gl_DefaultUniformBlock</c> or the GLSL block name.</summary>
    public required string Name { get; init; }
    public required string InstanceName { get; init; }
    public required BlockKind Kind { get; init; }
    public required int Set { get; init; }
    public required int Binding { get; init; }

    /// <summary>Bytes to bind: the last member's end, rounded up to 16 for uniform blocks (std140).</summary>
    public required int Size { get; init; }
    public required IReadOnlyList<BlockMember> Members { get; init; }
    public bool IsDefault => Name == "gl_DefaultUniformBlock";
}

/// <summary>A sampled image (a GLSL sampler). <see cref="ArrayLength"/>: 0 for a single sampler, the length of a sized array, -1 for a
/// runtime-sized array (the bindless arrays of the native model).</summary>
public sealed record SamplerInfo(string Name, int Set, int Binding, SamplerDimension Dimension, bool Arrayed, bool Multisampled, bool Depth, ScalarKind SampledKind, int ArrayLength);

/// <summary>A stage input or output variable (built-ins excluded).</summary>
public sealed record InterfaceVariable(string Name, int Location, ScalarKind Kind, int Columns, int Rows, int ArrayLength)
{
    /// <summary>Consecutive locations taken: columns times array length.</summary>
    public int Slots => Columns * Math.Max(1, ArrayLength);
}

/// <summary>A uniform found by its GL-style name: the block, the member, and the element for <c>uName[3]</c>.</summary>
public readonly record struct UniformLookup(UniformBlockInfo Block, BlockMember Member, int Element)
{
    /// <summary>Byte offset of the element inside the block.</summary>
    public int Offset => Member.Offset + Element * Member.ArrayStride;
}

/// <summary>What a SPIR-V module declares: blocks, samplers and stage interface.</summary>
public sealed class ShaderReflection
{
    public required IReadOnlyList<UniformBlockInfo> Blocks { get; init; }
    public required IReadOnlyList<SamplerInfo> Samplers { get; init; }
    public required IReadOnlyList<InterfaceVariable> Inputs { get; init; }
    public required IReadOnlyList<InterfaceVariable> Outputs { get; init; }

    /// <summary>Names of blocks and samplers the module declares but never uses: glslang gives them no binding, so they are left out of <see cref="Blocks"/> and <see cref="Samplers"/>. GL calls them inactive.</summary>
    public IReadOnlyList<string> Inactive { get; init; } = [];

    public UniformBlockInfo? DefaultBlock => Blocks.FirstOrDefault(b => b.IsDefault);

    public SamplerInfo? FindSampler(string name) => Samplers.FirstOrDefault(s => s.Name == name);

    /// <summary>
    /// Finds a uniform by the name GL would use: <c>uName</c>, <c>uName[0]</c>, <c>uName[3]</c>, a member of a named block
    /// or <c>Block.member</c>. Null when absent or the element is out of range.
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
