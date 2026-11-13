namespace Meitou.Game.Host;

/// <summary>
/// The share of the screen's pixels the Metal path renders the scene at without the upscaler (docs/metal-backend.md "No upscaler and the render
/// resolution"); pure, so it is tested without a GPU.
/// </summary>
static class MetalRenderFraction
{
    /// <summary>
    /// In full screen 1 over <paramref name="divisor"/> (the Render resolution row; the Retina switch is not used there). In a window 1, or with
    /// <paramref name="retinaResolution"/> off 1 over <paramref name="backingScale"/>, the window's pixels per point (virtual Retina pixels; a
    /// display with one pixel per point stays at 1).
    /// </summary>
    internal static float Of(bool fullScreen, int divisor, bool retinaResolution, float backingScale)
    {
        if (fullScreen) return 1f / Math.Max(divisor, 1);
        if (retinaResolution) return 1f;

        return 1f / MathF.Max(MathF.Round(backingScale * 100) / 100, 1f);
    }
}
