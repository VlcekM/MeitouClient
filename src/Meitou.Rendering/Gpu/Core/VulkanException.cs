using Silk.NET.Vulkan;

namespace Meitou.Rendering.Vulkan.Core;

public sealed class VulkanException(string message, Result result = Result.ErrorUnknown) : Exception(message)
{
    public Result Result { get; } = result;

    public static void Check(Result r, string what)
    {
        if (r != Result.Success)
        {
            throw new VulkanException($"{what} failed: {r}", r);
        }
    }
}
