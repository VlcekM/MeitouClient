namespace Meitou.Rendering.Vulkan.Upscalers;

/// <summary>The vendor upscalers on <see cref="VkGl"/>, for <see cref="PostProcess.UpscalerFactory"/>: null (and a note why) when one is not available.</summary>
public static class VendorUpscalers
{
    /// <param name="streamline">Streamline, when it was initialised before the device (DLSS); null otherwise.</param>
    public static Func<UpscalerKind, IUpscaler?> Factory(VkGl gl, Streamline? streamline) => kind =>
    {
        IUpscaler? upscaler = null;
        string? reason = null;
        if (kind == UpscalerKind.Fsr) upscaler = FsrUpscaler.TryCreate(gl, out reason);
        else if (kind == UpscalerKind.Dlss) upscaler = DlssUpscaler.TryCreate(streamline, gl, out reason);
        if (upscaler is null) Console.WriteLine($"upscaler  {reason}");
        return upscaler;
    };
}
