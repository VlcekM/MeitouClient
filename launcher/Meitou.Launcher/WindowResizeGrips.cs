using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Meitou.Launcher;

/// <summary>
/// The frameless window's resize grips. Windows does not start a resize drag (BeginResizeDrag) on a window without its frame, so there
/// the grips size the window themselves: the pixel rectangle at the press, its dragged edges following the pointer they capture
/// (Avalonia's Width / Height on this frameless window add a title bar's height). Elsewhere the platform does it (Wayland lets no
/// window place itself).
/// </summary>
sealed class WindowResizeGrips(Window window)
{
    /// <summary>
    /// The edge being dragged on Windows, null between drags.
    /// </summary>
    WindowEdge? _resizeEdge;

    /// <summary>
    /// The pointer's screen position at the press.
    /// </summary>
    PixelPoint _resizeStart;

    /// <summary>
    /// The window's pixel rectangle at the press.
    /// </summary>
    Win32Window.Rect _resizeRect;

    /// <summary>
    /// A press on a grip (its Tag names the <see cref="WindowEdge"/>) starts resizing from that edge.
    /// </summary>
    public void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: string tag } grip || !e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
            return;
        var edge = Enum.Parse<WindowEdge>(tag);
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle()?.Handle is not { } hwnd
            || !Win32Window.GetWindowRect(hwnd, out _resizeRect))
        {
            window.BeginResizeDrag(edge, e);
            return;
        }
        _resizeEdge = edge;
        _resizeStart = window.PointToScreen(e.GetPosition(window));
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    /// <summary>
    /// Moves the dragged edges with the pointer, keeping the window at least its minimum size.
    /// </summary>
    public void Moved(object? sender, PointerEventArgs e)
    {
        if (_resizeEdge is not { } edge || window.TryGetPlatformHandle()?.Handle is not { } hwnd) return;
        var now = window.PointToScreen(e.GetPosition(window));
        int dx = now.X - _resizeStart.X, dy = now.Y - _resizeStart.Y;
        int minWidth = (int)Math.Ceiling(window.MinWidth * window.DesktopScaling);
        int minHeight = (int)Math.Ceiling(window.MinHeight * window.DesktopScaling);
        var (left, top, right, bottom) = (_resizeRect.Left, _resizeRect.Top, _resizeRect.Right, _resizeRect.Bottom);
        if (edge is WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest)
            left = Math.Min(left + dx, right - minWidth);
        if (edge is WindowEdge.East or WindowEdge.NorthEast or WindowEdge.SouthEast)
            right = Math.Max(right + dx, left + minWidth);
        if (edge is WindowEdge.North or WindowEdge.NorthWest or WindowEdge.NorthEast)
            top = Math.Min(top + dy, bottom - minHeight);
        if (edge is WindowEdge.South or WindowEdge.SouthWest or WindowEdge.SouthEast)
            bottom = Math.Max(bottom + dy, top + minHeight);
        Win32Window.SetWindowPos(hwnd, IntPtr.Zero, left, top, right - left, bottom - top, Win32Window.NoZOrder | Win32Window.NoActivate);
    }

    /// <summary>
    /// Releasing the button ends the resize and frees the pointer.
    /// </summary>
    public void Released(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizeEdge is null) return;
        _resizeEdge = null;
        e.Pointer.Capture(null);
    }

    /// <summary>
    /// A lost capture ends the resize.
    /// </summary>
    public void CaptureLost(object? sender, PointerCaptureLostEventArgs e) => _resizeEdge = null;
}

/// <summary>
/// The Win32 calls the resize grips use on Windows (<see cref="WindowResizeGrips.Pressed"/>).
/// </summary>
static partial class Win32Window
{
    /// <summary>
    /// A window's rectangle in screen pixels (Win32 <c>RECT</c>).
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct Rect
    {
        /// <summary>
        /// The edges, in screen pixels.
        /// </summary>
        public int Left, Top, Right, Bottom;
    }

    /// <summary>
    /// <c>SetWindowPos</c> flags: keep the Z order, don't activate.
    /// </summary>
    internal const uint NoZOrder = 0x0004, NoActivate = 0x0010;

    /// <summary>
    /// The window's rectangle in screen pixels.
    /// </summary>
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr window, out Rect rect);

    /// <summary>
    /// Places and sizes the window in screen pixels.
    /// </summary>
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
