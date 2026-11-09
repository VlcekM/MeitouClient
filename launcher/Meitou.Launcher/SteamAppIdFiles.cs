using System.Diagnostics;
using Meitou.Content;

namespace Meitou.Launcher;

/// <summary>
/// Steamworks reads steam_appid.txt from the working directory when the game is started outside Steam. Keep it only for the lifetime of
/// this launch; never ship it or overwrite a user's file. A file that already holds Kenshi's ID is one an earlier launch left (the
/// launcher closed before the game did), so this launch takes it over and removes it.
/// </summary>
static class SteamAppIdFiles
{
    /// <summary>
    /// Kenshi's Steam app ID.
    /// </summary>
    const string KenshiAppId = "233860";

    /// <summary>
    /// Writes Kenshi's ID for the game about to start (or, with <paramref name="launchAsSteamKenshi"/> off, removes every trace of it);
    /// answers the files written.
    /// </summary>
    public static List<string> SetUp(ProcessStartInfo start, string exe, GameInstall gameInstall, bool launchAsSteamKenshi)
    {
        var written = new List<string>();
        string besideExe = Path.Combine(Path.GetDirectoryName(exe)!, "steam_appid.txt");
        string[] appIdFiles =
            [.. new[] { Path.Combine(start.WorkingDirectory, "steam_appid.txt"), besideExe }.Distinct(StringComparer.OrdinalIgnoreCase)];
        if (!launchAsSteamKenshi)
        {
            // Off: no Kenshi ID anywhere (not one inherited from Steam, nor a file an earlier launch left), so the game's Steamworks init skips.
            start.Environment.Remove("SteamAppId");
            start.Environment.Remove("SteamGameId");
            foreach (string file in appIdFiles)
            {
                try
                {
                    if (File.Exists(file) && File.ReadAllText(file).Trim() == KenshiAppId) TryDelete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        else if (OperatingSystem.IsWindows() && File.Exists(Path.Combine(gameInstall.Root, "steam_api64.dll")) && !Conflict(appIdFiles))
        {
            foreach (string file in appIdFiles)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, KenshiAppId);
                    written.Add(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            start.Environment["SteamAppId"] = KenshiAppId;
            start.Environment["SteamGameId"] = KenshiAppId;
        }
        return written;
    }

    /// <summary>
    /// Removes the files <see cref="SetUp"/> wrote.
    /// </summary>
    public static void RemoveAll(List<string> files) { foreach (string file in files) TryDelete(file); }

    /// <summary>
    /// Any other ID in either place is the user's: write nothing then, so both places keep agreeing.
    /// </summary>
    static bool Conflict(string[] appIdFiles)
    {
        bool conflict = false;
        foreach (string file in appIdFiles)
        {
            try
            {
                if (File.Exists(file) && File.ReadAllText(file).Trim() != KenshiAppId) conflict = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                conflict = true;
            }
        }
        return conflict;
    }

    /// <summary>
    /// Deletes the file, ignoring a failure.
    /// </summary>
    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
