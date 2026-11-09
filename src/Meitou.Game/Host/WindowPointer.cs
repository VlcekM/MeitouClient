using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.GLFW;
using Silk.NET.Windowing;

namespace Meitou.Game.Host;

/// <summary>
/// Where the mouse pointer is in the game window right now, asked of the OS rather than taken from the last mouse event, so a pointer that has
/// left the window (no more events) or a window that is not in front reads as nothing. Edge scrolling uses it.
/// </summary>
static partial class WindowPointer
{
    /// <summary>
    /// A Win32 <c>POINT</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Point
    {
        /// <summary>
        /// The point's coordinates, pixels.
        /// </summary>
        public int X, Y;
    }

    /// <summary>
    /// A Win32 <c>RECT</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        /// <summary>
        /// The rectangle's edges, pixels.
        /// </summary>
        public int Left, Top, Right, Bottom;
    }

    /// <summary>
    /// user32 <c>GetCursorPos</c>: the pointer's screen position; 0 on failure.
    /// </summary>
    [LibraryImport("user32.dll")] private static partial int GetCursorPos(out Point point);
    /// <summary>
    /// user32 <c>ScreenToClient</c>: a screen point to the window's client coordinates; 0 on failure.
    /// </summary>
    [LibraryImport("user32.dll")] private static partial int ScreenToClient(nint hwnd, ref Point point);
    /// <summary>
    /// user32 <c>GetClientRect</c>: the window's client area; 0 on failure.
    /// </summary>
    [LibraryImport("user32.dll")] private static partial int GetClientRect(nint hwnd, out Rect rect);
    /// <summary>
    /// user32 <c>GetForegroundWindow</c>: the window in front.
    /// </summary>
    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();

    /// <summary>
    /// The GLFW API, got once: Glfw.GetApi() creates a native context on every call, and the edge-scroll check runs every frame.
    /// </summary>
    static Glfw? s_glfwApi;

    /// <summary>
    /// The pointer as a fraction of the window's client area (0 to 1 on both axes), or null when the window is not in front or the pointer is outside
    /// it.
    /// </summary>
    public static unsafe Vector2? InWindow(IWindow window)
    {
        if (OperatingSystem.IsWindows() && window.Native?.Win32 is { } native)
        {
            nint hwnd = native.Hwnd;
            if (GetForegroundWindow() != hwnd || GetCursorPos(out var point) == 0 || ScreenToClient(hwnd, ref point) == 0
                    || GetClientRect(hwnd, out var client) == 0)
                return null;
            int width = client.Right - client.Left, height = client.Bottom - client.Top;
            if (width <= 0 || height <= 0 || point.X < 0 || point.Y < 0 || point.X >= width || point.Y >= height) return null;
            return new Vector2(point.X / (float)width, point.Y / (float)height);
        }
        // macOS/Linux through GLFW: the OS-level cursor as the Win32 path asks it, so the same contract holds — a pointer that has left
        // the window, or an unfocused window, reads as nothing. GLFW's screen coordinates match the window's logical size.
        if (!OperatingSystem.IsMacOS() || window.Native?.Glfw is not { } glfwWindow) return null;
        var handle = (WindowHandle*)glfwWindow;
        var api = s_glfwApi ??= Glfw.GetApi();
        if (!api.GetWindowAttrib(handle, WindowAttributeGetter.Focused)) return null;
        api.GetCursorPos(handle, out double x, out double y);
        var size = window.Size;
        if (size.X <= 0 || size.Y <= 0) return null;
        // The cursor rests past the content edges in the window chrome (macOS's menu bar above, Dock below), while the
        // edge scroll wants the *edge*: clamp a small overshoot into the client area (the fraction pins to 0 / 1), and
        // still read as nothing when the pointer is genuinely elsewhere (another display is further than this).
        const double overshoot = 128;
        if (x < -overshoot || y < -overshoot || x > size.X + overshoot || y > size.Y + overshoot) return null;
        if (x < 0) x = 0; else if (x > size.X) x = size.X;
        if (y < 0) y = 0; else if (y > size.Y) y = size.Y;
        return new Vector2((float)(x / size.X), (float)(y / size.Y));
    }
}