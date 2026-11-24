using Meitou.Desktop;

namespace Meitou.Launcher;

/// <summary>
/// The launcher's entry in a Linux desktop's app menu: a desktop entry and icon in the user's applications folder that start the AppImage the
/// launcher runs from. Added by the first-start dialog or Settings; while it exists, each start points it at the AppImage's current path.
/// </summary>
static class AppMenuEntry
{
    /// <summary>
    /// The entry's file name (without <c>.desktop</c>) and its icon's.
    /// </summary>
    const string Id = "wiki.kenshi.meitou.launcher";

    /// <summary>
    /// The launcher window's X11 class (<c>WM_CLASS</c>), which ties the window to the entry. It is Velopack's packId, the class the
    /// AppImage's own embedded entry names, so desktops that integrate the AppImage from it match the window too.
    /// </summary>
    public const string WindowClass = "MeitouLauncher";

    /// <summary>
    /// Whether the launcher runs from an AppImage on Linux, where the entry can be added.
    /// </summary>
    public static bool IsAvailable => AppImage() is not null && XdgDesktopEntry.ApplicationsDirectory() is not null;

    /// <summary>
    /// Whether the entry exists.
    /// </summary>
    public static bool IsAdded => EntryPath() is { } path && File.Exists(path);

    /// <summary>
    /// Writes the entry and its icon. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when they cannot be written.
    /// </summary>
    public static void Add()
    {
        if (AppImage() is not { } appImage || XdgDesktopEntry.ApplicationsDirectory() is not { } applications) return;

        string icon = Path.Combine(applications, Id + ".png");
        using (var resource = typeof(AppMenuEntry).Assembly.GetManifestResourceStream("meitou-128.png"))
            XdgDesktopEntry.WriteIcon(icon, resource);

        XdgDesktopEntry.WriteEntry(Path.Combine(applications, Id + ".desktop"), Entry(appImage, icon));
    }

    /// <summary>
    /// Deletes the entry and its icon. Throws as <see cref="Add"/> does.
    /// </summary>
    public static void Remove()
    {
        if (XdgDesktopEntry.ApplicationsDirectory() is not { } applications) return;

        File.Delete(Path.Combine(applications, Id + ".desktop"));
        File.Delete(Path.Combine(applications, Id + ".png"));
    }

    /// <summary>
    /// At startup: an existing entry is rewritten for the AppImage's current path, so a moved AppImage still starts from the menu.
    /// </summary>
    public static void Refresh()
    {
        if (!IsAvailable || !IsAdded) return;

        try
        {
            Add();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"app menu entry  {e.Message}");
        }
    }

    /// <summary>
    /// The entry's text. TryExec lets the menu hide it once the AppImage is deleted.
    /// </summary>
    static string Entry(string appImage, string icon)
    {
        string exec = XdgDesktopEntry.QuoteArgument(appImage);
        string tryExec = XdgDesktopEntry.EscapeValue(appImage);
        string iconValue = XdgDesktopEntry.EscapeValue(icon);

        return "[Desktop Entry]\nType=Application\nName=Meitou Launcher\nCategories=Game;\n"
            + $"Exec={exec}\nTryExec={tryExec}\nIcon={iconValue}\nStartupWMClass={WindowClass}\n";
    }

    /// <summary>
    /// The entry's path, null without an applications folder.
    /// </summary>
    static string? EntryPath() => XdgDesktopEntry.ApplicationsDirectory() is { } applications ? Path.Combine(applications, Id + ".desktop") : null;

    /// <summary>
    /// The AppImage the launcher runs from (its runtime sets <c>APPIMAGE</c>), null off Linux or outside one.
    /// </summary>
    static string? AppImage()
    {
        if (!OperatingSystem.IsLinux()) return null;

        string? path = Environment.GetEnvironmentVariable("APPIMAGE");
        return path is not null && Path.IsPathFullyQualified(path) && File.Exists(path) ? path : null;
    }
}
