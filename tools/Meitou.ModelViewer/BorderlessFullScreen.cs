using Silk.NET.Windowing;

namespace Meitou.ModelViewer;

/// <summary>
/// Borderless full screen for the viewer (Alt+Enter, the F2 editor's button): the window loses its border and covers its monitor, so
/// nothing of the desktop shows and switching away is instant (no exclusive mode change). Toggling again gives the bordered, maximized window back.
/// </summary>
static class BorderlessFullScreen
{
    public static bool IsOn(IWindow window) => window.WindowBorder == WindowBorder.Hidden;

    public static void Toggle(IWindow window)
    {
        if (IsOn(window))
        {
            window.WindowBorder = WindowBorder.Resizable;
            window.WindowState = WindowState.Maximized;
            Console.WriteLine("window    bordered");
            return;
        }
        // The monitor the window is on; its bounds are in desktop coordinates, which is what Position takes.
        var bounds = (window.Monitor ?? Silk.NET.Windowing.Monitor.GetMainMonitor(window)).Bounds;
        window.WindowState = WindowState.Normal;
        window.WindowBorder = WindowBorder.Hidden;
        window.Position = bounds.Origin;
        window.Size = bounds.Size;
        Console.WriteLine($"window    borderless full screen {bounds.Size.X} x {bounds.Size.Y}");
    }
}
