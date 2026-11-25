using Silk.NET.GLFW;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Meitou.Game.Rendering.Display;

/// <summary>
/// Borderless full screen for the viewer and the game (Alt+Enter, the Tab panel, the viewer's F2 editor button): the window loses its border and
/// covers its monitor, so nothing of the desktop shows and switching away is instant (no exclusive mode change). Toggling again gives the bordered,
/// maximized window back. Off Windows the window manager is asked for full screen instead (<see cref="WindowManagerFullScreen"/>): KDE and other
/// desktops keep their panel above a window that merely covers the monitor, and Wayland compositors ignore the position a client asks for. The
/// monitor is the chosen one, else the one the window is on, else the primary (<see cref="GlfwMonitors.Target"/>).
/// </summary>
public static class BorderlessFullScreen
{
    #region Switching

    /// <summary>
    /// Whether the window is in full screen: borderless (Windows) or full screen on a monitor (GLFW).
    /// </summary>
    public static bool IsOn(IWindow window) => window.WindowBorder == WindowBorder.Hidden || WindowManagerFullScreen.IsOn(window);

    /// <summary>
    /// Switches the window between borderless full screen and the bordered, maximized window. Full screen goes to monitor
    /// <paramref name="monitorIndex"/> (1-based) when it exists, else, with <paramref name="followWindow"/>, to the one the window is on; false at
    /// start-up, where the window has not been placed by the player.
    /// </summary>
    public static void Toggle(IWindow window, int? monitorIndex = null, bool followWindow = true)
    {
        if (IsOn(window))
        {
            Leave(window);
            Console.WriteLine("window    bordered");
            return;
        }

        if (OperatingSystem.IsWindows()) EnterBorderless(window, monitorIndex, followWindow);
        else WindowManagerFullScreen.Enter(window, monitorIndex, followWindow);
    }

    /// <summary>
    /// Moves a full-screen window to monitor <paramref name="monitorIndex"/>, or to the one it is on when that is null (a changed Monitor setting);
    /// a window not in full screen is left where it is.
    /// </summary>
    public static void MoveTo(IWindow window, int? monitorIndex)
    {
        if (!IsOn(window)) return;

        if (OperatingSystem.IsWindows()) PlaceBorderless(window, monitorIndex, followWindow: true);
        else WindowManagerFullScreen.MoveTo(window, monitorIndex);
    }

    /// <summary>
    /// Back to the bordered, maximized window.
    /// </summary>
    static void Leave(IWindow window)
    {
        if (WindowManagerFullScreen.IsOn(window) && window.Native?.Glfw is { } glfwWindow)
        {
            WindowManagerFullScreen.Leave(window, glfwWindow);
            return;
        }

        // Windows' borderless window.
        window.WindowBorder = WindowBorder.Resizable;
        window.WindowState = WindowState.Maximized;
    }

    #endregion

    #region Borderless window (Windows)

    /// <summary>
    /// The borderless window over the monitor <see cref="GlfwMonitors.Target"/> picks.
    /// </summary>
    static void EnterBorderless(IWindow window, int? monitorIndex, bool followWindow)
    {
        // Picked before the window changes: restoring it below moves it.
        var (origin, size) = BorderlessPlace(window, monitorIndex, followWindow);

        // Only when it is not: setting it restores the window, which shows one still hidden (the game sizes its window before showing it).
        if (window.WindowState != WindowState.Normal) window.WindowState = WindowState.Normal;
        window.WindowBorder = WindowBorder.Hidden;
        Cover(window, origin, size);
    }

    /// <summary>
    /// Puts the borderless window over the monitor <see cref="GlfwMonitors.Target"/> picks.
    /// </summary>
    static void PlaceBorderless(IWindow window, int? monitorIndex, bool followWindow)
    {
        var (origin, size) = BorderlessPlace(window, monitorIndex, followWindow);
        Cover(window, origin, size);
    }

    /// <summary>
    /// Gives the window the place and size of a monitor.
    /// </summary>
    static void Cover(IWindow window, Vector2D<int> origin, Vector2D<int> size)
    {
        window.Position = origin;
        window.Size = size;
        Console.WriteLine($"window    borderless full screen {size.X} x {size.Y} at {origin.X},{origin.Y}");
    }

    /// <summary>
    /// The corner and size of the monitor the borderless window covers, from GLFW, else Silk.NET's monitor of the window. The size is the monitor's
    /// video mode: GLFW's Bounds is the work area (1920 x 1032 over a 48 px taskbar, Observed 2026-10-10), and Windows keeps the taskbar on top of a
    /// window that does not cover the whole monitor. The corner is the monitor's own, not the work area's, which is below a taskbar or panel at the
    /// top (or right of one on the left; under Wine too, where a panel at the top left the window 34 px down with its bottom cut off).
    /// </summary>
    static (Vector2D<int> Origin, Vector2D<int> Size) BorderlessPlace(IWindow window, int? monitorIndex, bool followWindow)
    {
        if (GlfwMonitorRect(window, monitorIndex, followWindow) is { } rect) return rect;

        var monitor = window.Monitor ?? Silk.NET.Windowing.Monitor.GetMainMonitor(window);
        return (monitor.Bounds.Origin, monitor.VideoMode.Resolution ?? monitor.Bounds.Size);
    }

    /// <summary>
    /// The corner and video mode size of the monitor <see cref="GlfwMonitors.Target"/> picks, or null when GLFW cannot say.
    /// </summary>
    static unsafe (Vector2D<int> Origin, Vector2D<int> Size)? GlfwMonitorRect(IWindow window, int? monitorIndex, bool followWindow)
    {
        try
        {
            var glfw = Glfw.GetApi();
            var monitor = GlfwMonitors.Target(glfw, window, monitorIndex, followWindow);
            if (monitor == null) return null;

            return GlfwMonitors.Rect(glfw, monitor);
        }
        catch (Exception e) when (e is NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    #endregion
}
