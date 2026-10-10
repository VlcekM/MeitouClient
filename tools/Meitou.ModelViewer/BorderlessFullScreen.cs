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
        // The monitor the window is on: its origin in desktop coordinates, which is what Position takes. The size is the monitor's video
        // mode: GLFW's Bounds is the work area (1920 x 1032 over a 48 px taskbar, Observed 2026-10-10), and Windows keeps the taskbar on
        // top of a window that does not cover the whole monitor.
        var monitor = window.Monitor ?? Silk.NET.Windowing.Monitor.GetMainMonitor(window);
        var origin = monitor.Bounds.Origin;
        var size = monitor.VideoMode.Resolution ?? monitor.Bounds.Size;
        window.WindowState = WindowState.Normal;
        window.WindowBorder = WindowBorder.Hidden;
        window.Position = origin;
        window.Size = size;
        Console.WriteLine($"window    borderless full screen {size.X} x {size.Y} (monitor bounds {monitor.Bounds.Size.X} x {monitor.Bounds.Size.Y})");
    }
}
