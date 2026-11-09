using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Gpu.Shaders;

/// <summary>
/// Options for <see cref="GlslProgramCompiler.Compile"/>. They are part of the cache key.
/// </summary>
public sealed record ShaderCompileOptions
{
    /// <summary>
    /// Defines <c>MEITOU_DEPTH_REVERSED</c> in every stage, which selects the reversed branch of <see cref="DepthConvention.Glsl"/>. Set by the
    /// Vulkan backend (its scene depth is reversed), unset on Metal.
    /// </summary>
    public bool DepthReversed { get; init; }

    /// <summary>
    /// Directory of the disk cache; null is <c>%LOCALAPPDATA%\Meitou\shader-cache</c>.
    /// </summary>
    public string? CacheDirectory { get; init; }

    /// <summary>
    /// Read and write the disk cache.
    /// </summary>
    public bool UseDiskCache { get; init; } = true;

    /// <summary>
    /// Keep compiled programs in memory by the same key.
    /// </summary>
    public bool UseMemoryCache { get; init; } = true;

    /// <summary>
    /// First binding of every resource kind in the vertex stage.
    /// </summary>
    public uint VertexBindingBase { get; init; }

    /// <summary>
    /// First binding of every resource kind in the fragment stage.
    /// </summary>
    public uint FragmentBindingBase { get; init; } = 32;
}

/// <summary>
/// A shader failed to compile.
/// </summary>
public sealed class ShaderCompileException(string message, string stage, string log) : Exception(message)
{
    /// <summary>
    /// "vertex" or "fragment".
    /// </summary>
    public string Stage { get; } = stage;

    /// <summary>
    /// shaderc's own message.
    /// </summary>
    public string Log { get; } = log;
}

/// <summary>
/// Where a <see cref="CompiledProgram"/> came from.
/// </summary>
public enum ProgramSource
{
    /// <summary>
    /// Compiled now.
    /// </summary>
    Compiled,
    /// <summary>
    /// From the in-memory cache.
    /// </summary>
    Memory,
    /// <summary>
    /// From the on-disk cache.
    /// </summary>
    Disk
}
