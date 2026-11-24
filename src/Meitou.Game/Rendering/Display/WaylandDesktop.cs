using System.Reflection;
using Meitou.Desktop;
using Silk.NET.GLFW;
using Silk.NET.Windowing;

namespace Meitou.Game.Rendering.Display;

/// <summary>
/// Gives the game a Wayland app ID and makes its icon available through the matching XDG desktop entry.
/// </summary>
static class WaylandDesktop
{
    /// <summary>
    /// The Wayland app ID and the basename of its desktop entry.
    /// </summary>
    const string AppId = "wiki.kenshi.meitou";

    /// <summary>
    /// GLFW_WAYLAND_APP_ID, introduced in GLFW 3.4 and absent from Silk.NET's window hint enum.
    /// </summary>
    const int WaylandAppIdHint = 0x00026001;

    /// <summary>
    /// Initializes the game's window with its Wayland identity. The desktop entry is registered before the game shows its window.
    /// </summary>
    public static void Initialize(IWindow window)
    {
        if (!OperatingSystem.IsLinux() || Assembly.GetEntryAssembly() != typeof(WaylandDesktop).Assembly)
        {
            window.Initialize();
            return;
        }

        var glfw = GlfwProvider.GLFW.Value;
        glfw.GetVersion(out int major, out int minor, out _);
        bool hasAppIdHint = major > 3 || (major == 3 && minor >= 4);
        if (hasAppIdHint) glfw.WindowHintString(WaylandAppIdHint, AppId);

        try
        {
            window.Initialize();
        }
        finally
        {
            if (hasAppIdHint) glfw.WindowHintString(WaylandAppIdHint, "");
        }

        if (hasAppIdHint && window.Native?.Wayland is not null) Register();
    }

    /// <summary>
    /// Registers the desktop entry for this user. A read-only data directory only costs the window its icon.
    /// </summary>
    static void Register()
    {
        string? executable = Environment.ProcessPath;
        if (executable is null) return;

        string? applications = XdgDesktopEntry.ApplicationsDirectory();
        if (applications is null) return;

        try
        {
            // The icon sits in a stable location shared by installed game versions.
            string icon = Path.Combine(applications, AppId + ".png");
            using (var resource = typeof(WaylandDesktop).Assembly.GetManifestResourceStream("meitou-128.png"))
                XdgDesktopEntry.WriteIcon(icon, resource);

            string command = XdgDesktopEntry.QuoteArgument(executable);
            if (Path.GetFileName(executable) == "dotnet")
                command += " " + XdgDesktopEntry.QuoteArgument(typeof(WaylandDesktop).Assembly.Location);

            string iconValue = XdgDesktopEntry.EscapeValue(icon);
            string desktop = $"[Desktop Entry]\nType=Application\nName=Meitou\nNoDisplay=true\nExec={command}\nIcon={iconValue}\n";
            XdgDesktopEntry.WriteEntry(Path.Combine(applications, AppId + ".desktop"), desktop);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"desktop icon  {e.Message}");
        }
    }
}
