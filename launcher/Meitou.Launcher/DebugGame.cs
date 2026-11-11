#if DEBUG
using System.Diagnostics;

namespace Meitou.Launcher;

/// <summary>
/// Debug builds only: Play starts the checkout's own Debug build of the game, and its output shows in the launcher's console.
/// </summary>
static class DebugGame
{
    /// <summary>
    /// The checkout's Debug game (<c>src/Meitou.Game/bin/Debug/net11.0/MeitouClient.exe</c>) above the working directory or the
    /// launcher, or null.
    /// </summary>
    public static string? Find()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            for (; directory is not null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "src", "Meitou.Game", "bin", "Debug", "net11.0", "MeitouClient.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Forwards the started game's redirected output and error lines to the launcher's console.
    /// </summary>
    public static void ForwardOutput(Process game)
    {
        game.OutputDataReceived += (_, data) => ForwardLine(data, Console.Out);
        game.ErrorDataReceived += (_, data) => ForwardLine(data, Console.Error);
        game.BeginOutputReadLine();
        game.BeginErrorReadLine();
    }

    /// <summary>
    /// A line of the game's output, tagged [Meitou], to the launcher's console and the debugger.
    /// </summary>
    static void ForwardLine(DataReceivedEventArgs data, TextWriter console)
    {
        if (data.Data is not { } line)
            return;

        console.WriteLine($"[Meitou] {line}");
        Debug.WriteLine($"[Meitou] {line}");
    }
}
#endif
