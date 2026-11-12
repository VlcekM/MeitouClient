#if MEITOU_METAL
// Metal exists on macOS only; the file is compiled only there (the csproj's MEITOU_METAL constant).
#pragma warning disable CA1416
using Meitou.Game.Rendering.Metal;
using Meitou.Game.Rendering.Overlays;
using Silk.NET.Windowing;

namespace Meitou.Game.Host;

/// <summary>
/// The Metal window's loading frames: the context's frame (the layer's next drawable), its backbuffer the overlay's target, copied into the
/// drawable at the present.
/// </summary>
sealed class MetalLoadingCanvas(MetalContext context, IWindow window, DebugOverlay overlay) : ILoadingCanvas
{
    /// <inheritdoc/>
    public IWindow Window => window;

    /// <inheritdoc/>
    public bool BeginLoadingFrame(int width, int height)
    {
        if (width <= 0 || height <= 0 || !context.BeginFrame()) return false;
        overlay.Target = context.Backbuffer;
        return true;
    }

    /// <inheritdoc/>
    public void PresentLoadingFrame() => context.EndFrame();
}
#endif
