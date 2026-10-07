using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan.Core;
namespace Meitou.Rendering.Upscalers;

/// <summary>The vendor upscalers on a <see cref="GpuContext"/>, for <see cref="PostProcess.UpscalerFactory"/>: null (and a note why) when one is not available.</summary>
public static class VendorUpscalers
{
    /// <param name="streamline">Streamline, when it was initialised before the device (DLSS); null otherwise.</param>
    public static Func<UpscalerKind, IUpscaler?> Factory(GpuContext ctx, Streamline? streamline) => kind =>
    {
        IUpscaler? upscaler = null;
        string? reason = null;
        if (kind == UpscalerKind.Fsr) upscaler = FsrUpscaler.TryCreate(ctx, out reason);
        else if (kind == UpscalerKind.Dlss) upscaler = DlssUpscaler.TryCreate(streamline, ctx, out reason);
        if (upscaler is null) Console.WriteLine($"upscaler  {reason}");
        return upscaler;
    };
}
