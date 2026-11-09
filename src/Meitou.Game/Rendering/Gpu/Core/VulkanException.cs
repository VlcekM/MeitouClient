using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gpu.Core;

/// <summary>
/// A Vulkan call that did not succeed, with its result.
/// </summary>
public sealed class VulkanException(string message, Result result = Result.ErrorUnknown) : Exception(message)
{
    /// <summary>
    /// The call's result.
    /// </summary>
    public Result Result { get; } = result;

    /// <summary>
    /// Throws a <see cref="VulkanException"/> naming <paramref name="what"/> unless <paramref name="r"/> is success.
    /// </summary>
    public static void Check(Result r, string what)
    {
        if (r != Result.Success) throw new VulkanException($"{what} failed: {r}", r);
    }
}
