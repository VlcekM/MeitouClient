using Meitou.Game.Rendering.Frame;
using Silk.NET.GLFW;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Meitou.Game.Rendering.Display;

/// <summary>
/// Full screen off Windows (<see cref="BorderlessFullScreen"/>): asked of the window manager through GLFW, on the monitor
/// <see cref="GlfwMonitors.Target"/> picks, keeping the windowed place to go back to.
/// </summary>
static class WindowManagerFullScreen
{
    /// <summary>
    /// The window's place before full screen, put back when it leaves (one window per process: the game or the viewer).
    /// </summary>
    static Vector2D<int> s_windowedPosition, s_windowedSize;

    /// <summary>
    /// Whether the window is full screen on a monitor, asked of GLFW: on KDE (Wayland and XWayland) a window maximized before going full screen stays
    /// maximized in it (Observed 2026-10-25), and Silk.NET's WindowState, which checks Maximized first, then says Maximized (Verified: Silk.NET 2.23
    /// GlfwWindow.CoreWindowState).
    /// </summary>
    public static unsafe bool IsOn(IWindow window) => window.Native?.Glfw is { } handle
        ? GlfwProvider.GLFW.Value.GetWindowMonitor((WindowHandle*)handle) != null
        : window.WindowState == WindowState.Fullscreen;

    /// <summary>
    /// Full screen on monitor <paramref name="monitorIndex"/>, else (with <paramref name="followWindow"/>) the window's, else the primary; through
    /// Silk.NET without a GLFW window.
    /// </summary>
    public static void Enter(IWindow window, int? monitorIndex, bool followWindow)
    {
        var monitor = window.Monitor ?? Silk.NET.Windowing.Monitor.GetMainMonitor(window);
        var size = monitor.VideoMode.Resolution ?? monitor.Bounds.Size;

        // Shown first on Wayland: GLFW ignores showing a full-screen window, and there going full screen doesn't show it either (X11 maps it
        // itself).
        if (window.Native?.Wayland is not null) window.IsVisible = true;
        if (window.Native?.Glfw is { } handle) size = EnterGlfw(window, handle, monitorIndex, followWindow) ?? size;
        else window.WindowState = WindowState.Fullscreen;
        Console.WriteLine($"window    full screen {size.X} x {size.Y}, asked of the window manager");
    }

    /// <summary>
    /// Moves the full-screen window to monitor <paramref name="monitorIndex"/>, or to the one it is on when that is null.
    /// </summary>
    public static unsafe void MoveTo(IWindow window, int? monitorIndex)
    {
        if (window.Native?.Glfw is not { } handle) return;

        var glfw = GlfwProvider.GLFW.Value;
        var monitor = GlfwMonitors.Target(glfw, window, monitorIndex, followWindow: true);
        if (monitor == null || monitor == glfw.GetWindowMonitor((WindowHandle*)handle)) return;

        var size = Cover(glfw, handle, monitor);
        Console.WriteLine($"window    full screen {size?.X} x {size?.Y} on {glfw.GetMonitorName(monitor)}, asked of the window manager");
    }

    /// <summary>
    /// Out of full screen through GLFW, back to the windowed place kept on entering (X11 and macOS honour the position; Wayland ignores it), then
    /// maximized.
    /// </summary>
    public static unsafe void Leave(IWindow window, nint handle)
    {
        var glfw = GlfwProvider.GLFW.Value;
        if (s_windowedSize.X <= 0 || s_windowedSize.Y <= 0) s_windowedSize = window.Size;
        glfw.SetWindowMonitor((WindowHandle*)handle, null, s_windowedPosition.X, s_windowedPosition.Y, s_windowedSize.X, s_windowedSize.Y, 0);
        glfw.MaximizeWindow((WindowHandle*)handle);
    }

    /// <summary>
    /// Full screen on the monitor <see cref="GlfwMonitors.Target"/> picks, at its current video mode, keeping the windowed place to go back to.
    /// Auto-iconify off on Linux: GLFW otherwise minimizes a full-screen window when it loses focus (Alt+Tab), which borderless full screen on
    /// Windows never does, and the window manager keeps only the focused full-screen window on top. Not on macOS, where GLFW raises a full-screen
    /// window above the menu bar and minimizing is what gets it out of the way.
    /// </summary>
    static unsafe Vector2D<int>? EnterGlfw(IWindow window, nint handle, int? monitorIndex, bool followWindow)
    {
        var glfw = GlfwProvider.GLFW.Value;
        var monitor = GlfwMonitors.Target(glfw, window, monitorIndex, followWindow);
        if (monitor == null) return null;

        s_windowedPosition = window.Position;
        s_windowedSize = window.Size;
        if (OperatingSystem.IsLinux())
            glfw.SetWindowAttrib((WindowHandle*)handle, WindowAttributeSetter.AutoIconify, false);

        return Cover(glfw, handle, monitor);
    }

    /// <summary>
    /// Makes the window full screen on <paramref name="monitor"/> at its current video mode; its size, or null when GLFW has no mode for it.
    /// </summary>
    static unsafe Vector2D<int>? Cover(Glfw glfw, nint handle, Silk.NET.GLFW.Monitor* monitor)
    {
        var mode = glfw.GetVideoMode(monitor);
        if (mode == null) return null;

        glfw.SetWindowMonitor((WindowHandle*)handle, monitor, 0, 0, mode->Width, mode->Height, mode->RefreshRate);
        return new Vector2D<int>(mode->Width, mode->Height);
    }
}
