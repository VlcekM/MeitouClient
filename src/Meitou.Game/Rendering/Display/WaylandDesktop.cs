using System.Reflection;
using Meitou.Content;
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

        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? "";
        if (!Path.IsPathFullyQualified(dataHome))
            dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        if (!Path.IsPathFullyQualified(dataHome)) return;

        try
        {
            string applications = Path.Combine(dataHome, "applications");
            Directory.CreateDirectory(applications);
            string icon = Path.Combine(applications, AppId + ".png");
            string entry = Path.Combine(applications, AppId + ".desktop");
            InstallIcon(icon);

            string command = QuoteArgument(executable);
            if (Path.GetFileName(executable) == "dotnet")
                command += " " + QuoteArgument(typeof(WaylandDesktop).Assembly.Location);

            string desktop = $"[Desktop Entry]\nType=Application\nName=Meitou\nNoDisplay=true\nExec={command}\nIcon={EscapeValue(icon)}\n";
            if (!File.Exists(entry) || File.ReadAllText(entry) != desktop) AtomicFile.WriteAllText(entry, desktop);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"desktop icon  {e.Message}");
        }
    }

    /// <summary>
    /// Copies the embedded icon into a stable location shared by installed game versions.
    /// </summary>
    static void InstallIcon(string path)
    {
        using var icon = typeof(WaylandDesktop).Assembly.GetManifestResourceStream("meitou-128.png");
        if (icon is null) return;

        using var buffer = new MemoryStream();
        icon.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();

        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) AtomicFile.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// Quotes a literal argument for the desktop entry's Exec key, including escaping its field-code marker.
    /// </summary>
    static string QuoteArgument(string argument)
    {
        string quoted = argument.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%");

        return "\"" + EscapeValue(quoted) + "\"";
    }

    /// <summary>
    /// Escapes the characters handled by the desktop entry value parser.
    /// </summary>
    static string EscapeValue(string value) => value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
}
