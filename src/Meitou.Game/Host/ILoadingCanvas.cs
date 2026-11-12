using Silk.NET.Windowing;

namespace Meitou.Game.Host;

/// <summary>
/// The window the start's loading frames are drawn in (<see cref="GameHost.LoadingFrame"/>): the backend's frame begun at the window's size with
/// the loading screen's overlay drawing into it, then presented.
/// </summary>
interface ILoadingCanvas
{
    /// <summary>
    /// The window whose events are handled before each frame.
    /// </summary>
    IWindow Window { get; }

    /// <summary>
    /// Begins a frame of <paramref name="width"/> × <paramref name="height"/> pixels with the loading screen's overlay drawing into it; false when
    /// nothing can be shown now (a minimised window, no drawable): the frame is skipped.
    /// </summary>
    bool BeginLoadingFrame(int width, int height);

    /// <summary>
    /// Presents the frame <see cref="BeginLoadingFrame"/> began.
    /// </summary>
    void PresentLoadingFrame();
}
