using Silk.NET.GLFW;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Meitou.Rendering.Display;

/// <summary>
/// Borderless full screen for the viewer and the game (Alt+Enter, the Tab panel, the viewer's F2 editor button): the window loses its border and covers its monitor, so
/// nothing of the desktop shows and switching away is instant (no exclusive mode change). Toggling again gives the bordered, maximized window back.
/// Off Windows the window manager is asked for full screen instead, through GLFW on the primary monitor: KDE and other desktops keep their panel above a window that
/// merely covers the monitor, and Wayland compositors ignore the position a client asks for. That path keeps the windowed place itself and asks GLFW
/// whether the window is full screen (see <see cref="FullScreen"/>).
/// </summary>
public static class BorderlessFullScreen
{
    /// <summary>The window's place before full screen off Windows, put back when it leaves (one window per process: the game or the viewer).</summary>
    static Vector2D<int> windowedPosition, windowedSize;

    public static bool IsOn(IWindow window) => window.WindowBorder == WindowBorder.Hidden || FullScreen(window);

    /// <summary>Whether the window is full screen on a monitor, asked of GLFW: on KDE (Wayland and XWayland) a window maximized before going full screen
    /// stays maximized in it (Observed 2026-10-25), and Silk.NET's WindowState, which checks Maximized first, then says Maximized (Verified: Silk.NET
    /// 2.23 GlfwWindow.CoreWindowState).</summary>
    static unsafe bool FullScreen(IWindow window) => window.Native?.Glfw is { } handle
        ? GlfwProvider.GLFW.Value.GetWindowMonitor((WindowHandle*)handle) != null
        : window.WindowState == WindowState.Fullscreen;

    /// <summary>Full screen on the primary monitor at its current video mode, keeping the windowed place to go back to.
    /// Auto-iconify off on Linux: GLFW otherwise minimizes a full-screen window when it loses focus (Alt+Tab), which borderless full screen on Windows never
    /// does, and the window manager keeps only the focused full-screen window on top. Not on macOS, where GLFW raises a full-screen window above the menu
    /// bar and minimizing is what gets it out of the way.</summary>
    static unsafe Vector2D<int>? EnterFullScreen(IWindow window, nint handle)
    {
        var glfw = GlfwProvider.GLFW.Value;
        var monitor = glfw.GetPrimaryMonitor();
        if (monitor == null) return null;
        var mode = glfw.GetVideoMode(monitor);
        if (mode == null) return null;
        windowedPosition = window.Position;
        windowedSize = window.Size;
        if (OperatingSystem.IsLinux()) glfw.SetWindowAttrib((WindowHandle*)handle, WindowAttributeSetter.AutoIconify, false);
        glfw.SetWindowMonitor((WindowHandle*)handle, monitor, 0, 0, mode->Width, mode->Height, mode->RefreshRate);
        return new Vector2D<int>(mode->Width, mode->Height);
    }

    /// <summary>Out of full screen through GLFW, back to the windowed place kept on entering (X11 and macOS honour the position; Wayland ignores it),
    /// then maximized.</summary>
    static unsafe void LeaveFullScreen(IWindow window, nint handle)
    {
        var glfw = GlfwProvider.GLFW.Value;
        if (windowedSize.X <= 0 || windowedSize.Y <= 0) windowedSize = window.Size;
        glfw.SetWindowMonitor((WindowHandle*)handle, null, windowedPosition.X, windowedPosition.Y, windowedSize.X, windowedSize.Y, 0);
        glfw.MaximizeWindow((WindowHandle*)handle);
    }

    /// <summary>How far in from a monitor's corner a window is put, as Silk.NET's default 50,50: on X11 the position is the content's, and the title
    /// bar goes above it.</summary>
    const int Inset = 50;

    /// <summary>Puts the window near the corner of the primary monitor's work area (X11 honours it; Wayland places windows itself).</summary>
    public static unsafe void MoveToPrimaryMonitor(IWindow window)
    {
        var glfw = GlfwProvider.GLFW.Value;
        var primary = glfw.GetPrimaryMonitor();
        if (primary == null) return;
        glfw.GetMonitorWorkarea(primary, out int x, out int y, out _, out _);
        window.Position = new Vector2D<int>(x + Inset, y + Inset);
    }

    public static void Toggle(IWindow window)
    {
        if (IsOn(window))
        {
            if (FullScreen(window) && window.Native?.Glfw is { } glfwWindow) LeaveFullScreen(window, glfwWindow);
            else
            {
                // Windows' borderless window.
                window.WindowBorder = WindowBorder.Resizable;
                window.WindowState = WindowState.Maximized;
            }
            Console.WriteLine("window    bordered");
            return;
        }
        // The monitor the window is on: its origin in desktop coordinates, which is what Position takes. The size is the monitor's video
        // mode: GLFW's Bounds is the work area (1920 x 1032 over a 48 px taskbar, Observed 2026-10-10), and Windows keeps the taskbar on
        // top of a window that does not cover the whole monitor.
        var monitor = window.Monitor ?? Silk.NET.Windowing.Monitor.GetMainMonitor(window);
        var size = monitor.VideoMode.Resolution ?? monitor.Bounds.Size;
        if (!OperatingSystem.IsWindows())
        {
            // Shown first on Wayland: GLFW ignores showing a full-screen window, and there going full screen doesn't show it either (X11 maps it itself).
            if (window.Native?.Wayland is not null) window.IsVisible = true;
            if (window.Native?.Glfw is { } handle) size = EnterFullScreen(window, handle) ?? size;
            else window.WindowState = WindowState.Fullscreen;
            Console.WriteLine($"window    full screen {size.X} x {size.Y}, asked of the window manager");
            return;
        }
        // Bounds is the work area, so its origin is below a taskbar or panel at the top (or right of one on the left): the corner of the monitor itself is
        // read from GLFW (under Wine too, where a panel at the top left the window 34 px down with its bottom cut off).
        var origin = MonitorCorner(window) ?? monitor.Bounds.Origin;
        // Only when it is not: setting it restores the window, which shows one still hidden (the game sizes its window before showing it).
        if (window.WindowState != WindowState.Normal) window.WindowState = WindowState.Normal;
        window.WindowBorder = WindowBorder.Hidden;
        window.Position = origin;
        window.Size = size;
        Console.WriteLine($"window    borderless full screen {size.X} x {size.Y} at {origin.X},{origin.Y} (monitor work area {monitor.Bounds.Size.X} x {monitor.Bounds.Size.Y} at {monitor.Bounds.Origin.X},{monitor.Bounds.Origin.Y})");
    }

    /// <summary>The top-left corner of the monitor the window is on (the one under its centre, else the primary), or null when GLFW cannot say.</summary>
    static unsafe Vector2D<int>? MonitorCorner(IWindow window)
    {
        try
        {
            var glfw = Glfw.GetApi();
            var monitors = glfw.GetMonitors(out int count);
            var centre = window.Position + window.Size / 2;
            Silk.NET.GLFW.Monitor* found = null;
            for (int i = 0; i < count; i++)
            {
                glfw.GetMonitorWorkarea(monitors[i], out int x, out int y, out int w, out int h);
                if (centre.X >= x && centre.X < x + w && centre.Y >= y && centre.Y < y + h) { found = monitors[i]; break; }
            }
            if (found == null) found = glfw.GetPrimaryMonitor();
            if (found == null) return null;
            glfw.GetMonitorPos(found, out int px, out int py);
            return new Vector2D<int>(px, py);
        }
        catch (Exception e) when (e is NotSupportedException or DllNotFoundException or EntryPointNotFoundException) { return null; }
    }
}
