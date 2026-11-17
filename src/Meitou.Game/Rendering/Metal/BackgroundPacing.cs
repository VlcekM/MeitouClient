namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// The frame pace of a window that is not in front (macOS: another app or Space is active, or the window is covered or minimised). AppKit stops
/// pacing the presents of such a window, so neither vsync nor the frame limit holds and the loop would run flat out; the loop paces itself instead,
/// near <see cref="TargetFps"/>.
/// </summary>
static class BackgroundPacing
{
    /// <summary>
    /// The frame rate a background window aims for.
    /// </summary>
    public const double TargetFps = 30;

    /// <summary>
    /// The refresh rate assumed when the display's is not known.
    /// </summary>
    const double DefaultRefreshRate = 60;

    /// <summary>
    /// The seconds one frame of a background window takes. With vsync on, a whole number of refreshes (the divisor that lands nearest
    /// <see cref="TargetFps"/>: 2 at 60 Hz, 4 at 120 Hz), so the pace stays a vsync multiple; with it off, <see cref="TargetFps"/>, or the frame
    /// limit when that is lower.
    /// </summary>
    public static double Period(bool vsync, int fpsLimit, double refreshRate)
    {
        if (!vsync) return 1.0 / (fpsLimit > 0 ? Math.Min(fpsLimit, TargetFps) : TargetFps);

        double refresh = refreshRate > 0 ? refreshRate : DefaultRefreshRate;
        double divisor = Math.Max(1, Math.Round(refresh / TargetFps, MidpointRounding.AwayFromZero));
        return divisor / refresh;
    }
}
