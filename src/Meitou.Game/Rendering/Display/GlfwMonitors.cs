using Meitou.Game.Rendering.Frame;
using Silk.NET.GLFW;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Meitou.Game.Rendering.Display;

/// <summary>
/// The monitors as GLFW lists them, numbered from 1 in its order (<c>--monitor</c>, <see cref="Host.UserConfig.Monitor"/>), and the one full screen
/// goes to (<see cref="Target"/>).
/// </summary>
static class GlfwMonitors
{
    /// <summary>
    /// How far in from a monitor's corner a window is put, as Silk.NET's default 50,50: on X11 the position is the content's, and the title bar goes
    /// above it.
    /// </summary>
    const int Inset = 50;

    /// <summary>
    /// The names of the monitors in GLFW's order, the one the 1-based monitor numbers count in (GLFW's primary is the first); empty without GLFW.
    /// </summary>
    public static unsafe IReadOnlyList<string> Names()
    {
        try
        {
            var glfw = GlfwProvider.GLFW.Value;
            var monitors = glfw.GetMonitors(out int count);
            var names = new string[count];
            for (int i = 0; i < count; i++) names[i] = glfw.GetMonitorName(monitors[i]) ?? "";
            return names;
        }
        catch (Exception e) when (e is NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    /// <summary>
    /// Puts the window near the corner of the work area of monitor <paramref name="monitorIndex"/> (1-based), or of the primary monitor when that
    /// does not exist (X11, Windows and macOS honour it; Wayland places windows itself).
    /// </summary>
    public static unsafe void MoveWindowTo(IWindow window, int? monitorIndex)
    {
        var glfw = GlfwProvider.GLFW.Value;
        var monitor = Requested(glfw, monitorIndex);
        if (monitor == null) monitor = glfw.GetPrimaryMonitor();
        if (monitor == null) return;

        glfw.GetMonitorWorkarea(monitor, out int x, out int y, out _, out _);
        window.Position = new Vector2D<int>(x + Inset, y + Inset);
    }

    /// <summary>
    /// The monitor full screen goes to: the requested one, else (with <paramref name="followWindow"/>) the one under the window's centre, else the
    /// primary. Not the window's on Wayland, where a client is never told where its window is (GLFW 3.4: "The platform does not provide the window
    /// position"; it reads 0,0). GLFW's primary is the first monitor of its list (Verified: glfwGetPrimaryMonitor in the bundled libglfw 3.4);
    /// Wayland's outputs carry no primary flag.
    /// </summary>
    public static unsafe Silk.NET.GLFW.Monitor* Target(Glfw glfw, IWindow window, int? monitorIndex, bool followWindow)
    {
        var monitor = Requested(glfw, monitorIndex);
        if (monitor == null && followWindow && window.Native?.Wayland is null) monitor = Under(glfw, window);
        if (monitor == null) monitor = glfw.GetPrimaryMonitor();

        return monitor;
    }

    /// <summary>
    /// A monitor's corner in desktop coordinates and its video mode's size, or null when GLFW has no mode for it.
    /// </summary>
    public static unsafe (Vector2D<int> Origin, Vector2D<int> Size)? Rect(Glfw glfw, Silk.NET.GLFW.Monitor* monitor)
    {
        var mode = glfw.GetVideoMode(monitor);
        if (mode == null) return null;

        glfw.GetMonitorPos(monitor, out int x, out int y);
        return (new Vector2D<int>(x, y), new Vector2D<int>(mode->Width, mode->Height));
    }

    /// <summary>
    /// The 1-based requested GLFW monitor, or null when <paramref name="monitorIndex"/> is absent or outside the current monitor list.
    /// </summary>
    static unsafe Silk.NET.GLFW.Monitor* Requested(Glfw glfw, int? monitorIndex)
    {
        if (monitorIndex is not { } index || index < 1) return null;

        var monitors = glfw.GetMonitors(out int count);
        return index <= count ? monitors[index - 1] : null;
    }

    /// <summary>
    /// The monitor whose whole area holds the window's centre, or null when none does.
    /// </summary>
    static unsafe Silk.NET.GLFW.Monitor* Under(Glfw glfw, IWindow window)
    {
        var monitors = glfw.GetMonitors(out int count);
        var centre = window.Position + window.Size / 2;
        for (int i = 0; i < count; i++)
        {
            if (Rect(glfw, monitors[i]) is not { } rect) continue;

            var (origin, size) = rect;
            bool inside = centre.X >= origin.X && centre.X < origin.X + size.X && centre.Y >= origin.Y && centre.Y < origin.Y + size.Y;
            if (inside) return monitors[i];
        }

        return null;
    }
}
