using Meitou.Content;

namespace Meitou.Steam;

/// <summary>
/// The Steam API library the launcher can talk to the running Steam client through, from what the user already has: nothing of Steam is
/// shipped (docs/launcher.md "Workshop subscribe").
/// </summary>
public static class SteamLibrary
{
    /// <summary>
    /// The library's path: the Steam client's own on macOS, the Kenshi install's <c>steam_api64.dll</c> on Windows; null when there is none
    /// (a Kenshi without Steam, no install set) and on Linux, where neither is a library the launcher can load.
    /// </summary>
    public static string? Locate(GameInstall? install)
    {
        if (OperatingSystem.IsMacOS()) return MacSteam.CanSync ? MacSteam.LibraryPath : null;
        if (!OperatingSystem.IsWindows() || install is null) return null;

        string path = Path.Combine(install.Root, "steam_api64.dll");
        return File.Exists(path) ? path : null;
    }
}
