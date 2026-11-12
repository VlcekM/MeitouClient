using Meitou.Game.Rendering.Display;
using Meitou.Game.Rendering.Overlays;
using Silk.NET.Windowing;

namespace Meitou.Game.Host;

/// <summary>
/// The Vulkan window's loading frames: the display's swapchain frame, its backbuffer the overlay's target.
/// </summary>
sealed class VulkanLoadingCanvas(VulkanDisplay display, IWindow window, DebugOverlay overlay) : ILoadingCanvas
{
    /// <inheritdoc/>
    public IWindow Window => window;

    /// <inheritdoc/>
    public bool BeginLoadingFrame(int width, int height)
    {
        if (!display.BeginFrame(width, height)) return false;
        overlay.Target = display.Backbuffer;
        return true;
    }

    /// <inheritdoc/>
    public void PresentLoadingFrame() => display.Present();
}
