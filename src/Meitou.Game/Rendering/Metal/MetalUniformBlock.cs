using System.Numerics;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Shaders;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// A uniform block on the CPU: the engine's default uniform block, written with the engine's own scatter (the std140
/// offsets and strides the engine's reflection reports), so the bytes are the ones the Vulkan path uploads.
/// </summary>
public sealed unsafe class MetalUniformBlock
{
    /// <summary>
    /// The block's bytes.
    /// </summary>
    readonly byte[] _bytes;
    /// <summary>
    /// The reflection the uniforms' offsets and strides come from.
    /// </summary>
    readonly ShaderReflection _reflection;

    /// <summary>
    /// A zeroed block of <paramref name="size"/> bytes laid out as <paramref name="reflection"/> reports.
    /// </summary>
    public MetalUniformBlock(ShaderReflection reflection, int size)
    {
        this._reflection = reflection;
        _bytes = new byte[size];
    }

    /// <summary>
    /// The block's size in bytes.
    /// </summary>
    public int Size => _bytes.Length;

    /// <summary>
    /// The block's bytes, in the std140 layout the engine's reflection reports.
    /// </summary>
    public byte[] Bytes => _bytes;

    /// <summary>
    /// Writes a mat4 uniform.
    /// </summary>
    public void Set(string name, in Matrix4x4 value) { fixed (Matrix4x4* p = &value) Scatter(name, (byte*)p, 4, 4); }

    /// <summary>
    /// Writes a vec3 uniform.
    /// </summary>
    public void Set(string name, in Vector3 value) { fixed (Vector3* p = &value) Scatter(name, (byte*)p, 3, 1); }

    /// <summary>
    /// Writes a vec4 uniform.
    /// </summary>
    public void Set(string name, in Vector4 value) { fixed (Vector4* p = &value) Scatter(name, (byte*)p, 4, 1); }

    /// <summary>
    /// Writes a float uniform.
    /// </summary>
    public void Set(string name, in float value) { fixed (float* p = &value) Scatter(name, (byte*)p, 1, 1); }

    /// <summary>
    /// Scatters <paramref name="rows"/> x <paramref name="columns"/> floats from <paramref name="source"/> to the named uniform's offsets; throws
    /// when the shader has no such uniform.
    /// </summary>
    void Scatter(string name, byte* source, int rows, int columns)
    {
        var at = _reflection.FindUniform(name) ?? throw new InvalidOperationException($"the shader has no uniform named {name}");
        GlUniforms.Scatter(_bytes, at, source, rows, columns, 1, false);
    }
}
