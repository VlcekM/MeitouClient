using System.Diagnostics;

namespace Meitou.Steam;

/// <summary>
/// The macOS Steam client as far as Meitou uses it: where it keeps the Kenshi Workshop items it downloads, the Steam API library it ships and
/// whether it is running. Kenshi has no Mac build, so the client never installs the game, but it still downloads the Workshop items of an
/// account that owns it (docs/launcher.md "Workshop sync"). Nothing of Steam is copied: the library is loaded from the user's own install.
/// </summary>
public static class MacSteam
{
    /// <summary>
    /// Kenshi's Steam App ID.
    /// </summary>
    public const string KenshiAppId = "233860";

    /// <summary>
    /// The client's process name.
    /// </summary>
    const string ProcessName = "steam_osx";

    /// <summary>
    /// The client's data folder (<c>~/Library/Application Support/Steam</c>).
    /// </summary>
    static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Steam");

    /// <summary>
    /// The universal (x86_64 and arm64) <c>libsteam_api.dylib</c> inside the client's app bundle, the one its helper uses.
    /// </summary>
    public static string LibraryPath => Path.Combine(DataDirectory, "Steam.AppBundle", "Steam", "Contents", "MacOS", "Frameworks",
        "Steam Helper.app", "Contents", "MacOS", "libsteam_api.dylib");

    /// <summary>
    /// Where the client puts the downloaded Kenshi Workshop items, one folder per item id; null off macOS. The folder may not exist.
    /// </summary>
    public static string? WorkshopContentDirectory =>
        OperatingSystem.IsMacOS() ? Path.Combine(DataDirectory, "steamapps", "workshop", "content", KenshiAppId) : null;

    /// <summary>
    /// True on macOS when the client's Steam API library is there, so a sync can be tried.
    /// </summary>
    public static bool CanSync => OperatingSystem.IsMacOS() && File.Exists(LibraryPath);

    /// <summary>
    /// True when the Steam client is running.
    /// </summary>
    public static bool IsRunning
    {
        get
        {
            var processes = Process.GetProcessesByName(ProcessName);
            foreach (var process in processes) process.Dispose();
            return processes.Length > 0;
        }
    }
}
