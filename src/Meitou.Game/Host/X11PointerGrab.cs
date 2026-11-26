using System.Runtime.InteropServices;
using Silk.NET.Windowing;

namespace Meitou.Game.Host;

/// <summary>
/// Temporarily gives the game pointer events ahead of X11 window-manager shortcuts, without confining or warping the pointer.
/// Uses GLFW's X11 connection so the events still go through its mouse callbacks.
/// </summary>
sealed partial class X11PointerGrab(nint display, nuint window)
{
    /// <summary>
    /// X11 ButtonPressMask, ButtonReleaseMask and PointerMotionMask.
    /// </summary>
    const uint PointerEvents = (1u << 2) | (1u << 3) | (1u << 6);

    /// <summary>
    /// X11 GrabModeAsync, for both the keyboard and pointer.
    /// </summary>
    const int Async = 1;

    /// <summary>
    /// Whether the game has acquired the pointer grab.
    /// </summary>
    internal bool Active { get; private set; }

    /// <summary>
    /// The window's X11 connection and ID, when GLFW is using X11 on Linux.
    /// </summary>
    internal static X11PointerGrab? ForWindow(IWindow? window)
    {
        if (!OperatingSystem.IsLinux() || window?.Native?.X11 is not { } x11) return null;

        var (display, id) = x11;
        return new X11PointerGrab(display, (nuint)id);
    }

    /// <summary>
    /// Keep the grab only while gameplay needs it. A failed grab can be retried on the next pointer update.
    /// </summary>
    internal void Update(bool wanted)
    {
        if (wanted == Active) return;

        if (wanted)
        {
            Active = XGrabPointer(display, window, 1, PointerEvents, Async, Async, 0, 0, 0) == 0;
            return;
        }

        XUngrabPointer(display, 0);
        XFlush(display);
        Active = false;
    }

    /// <summary>
    /// Starts an active pointer grab on the game's X11 window and returns the X11 grab status.
    /// </summary>
    [LibraryImport("libX11.so.6")]
    private static partial int XGrabPointer(nint display, nuint window, int ownerEvents, uint eventMask, int pointerMode, int keyboardMode,
        nuint confineTo, nuint cursor, nuint time);

    /// <summary>
    /// Ends the active pointer grab on the supplied X11 connection.
    /// </summary>
    [LibraryImport("libX11.so.6")]
    private static partial int XUngrabPointer(nint display, nuint time);

    /// <summary>
    /// Sends queued X11 requests to the server so the grab is released immediately.
    /// </summary>
    [LibraryImport("libX11.so.6")]
    private static partial int XFlush(nint display);
}
