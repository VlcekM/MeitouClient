using System.Numerics;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Shaders;

namespace Meitou.Metal;

/// <summary>
/// A uniform block on the CPU: the engine's default uniform block, written with the engine's own scatter (the std140
/// offsets and strides the engine's reflection reports), so the bytes are the ones the Vulkan path uploads.
/// </summary>
public sealed unsafe class MetalUniformBlock
{
    readonly byte[] bytes;
    readonly ShaderReflection reflection;

    public MetalUniformBlock(ShaderReflection reflection, int size)
    {
        this.reflection = reflection;
        bytes = new byte[size];
    }

    /// <summary>The block's size in bytes.</summary>
    public int Size => bytes.Length;

    /// <summary>The block's bytes, in the std140 layout the engine's reflection reports.</summary>
    public byte[] Bytes => bytes;

    public void Set(string name, in Matrix4x4 value) { fixed (Matrix4x4* p = &value) Scatter(name, (byte*)p, 4, 4); }

    public void Set(string name, in Vector3 value) { fixed (Vector3* p = &value) Scatter(name, (byte*)p, 3, 1); }

    public void Set(string name, in Vector4 value) { fixed (Vector4* p = &value) Scatter(name, (byte*)p, 4, 1); }

    public void Set(string name, in float value) { fixed (float* p = &value) Scatter(name, (byte*)p, 1, 1); }

    void Scatter(string name, byte* source, int rows, int columns)
    {
        var at = reflection.FindUniform(name) ?? throw new InvalidOperationException($"the shader has no uniform named {name}");
        GlUniforms.Scatter(bytes, at, source, rows, columns, 1, false);
    }
}
