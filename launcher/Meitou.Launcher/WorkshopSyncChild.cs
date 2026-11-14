using Meitou.Steam;

namespace Meitou.Launcher;

/// <summary>
/// The process that does a Workshop sync: the launcher started again with <see cref="Argument"/>, without a window. Steam ties "Kenshi is running"
/// to the process that initialised Steamworks as Kenshi, and <c>SteamAPI_Shutdown</c> does not end that (docs/launcher.md "Workshop sync"), so the
/// sync lives in a process of its own that exits when it is done. It talks to <see cref="WorkshopSyncRunner"/> in lines on its standard output.
/// </summary>
static class WorkshopSyncChild
{
    /// <summary>
    /// The command-line argument that makes the launcher run a sync instead of opening its window.
    /// </summary>
    public const string Argument = "--macos-workshop-sync";

    /// <summary>
    /// Starts every line meant for the runner; Steam's own library prints lines of its own to the same output.
    /// </summary>
    public const string Prefix = "meitou-sync:";

    /// <summary>
    /// Runs the sync and prints <c>progress &lt;installed&gt; &lt;total&gt;</c> lines, then one <c>result &lt;total&gt; &lt;installed&gt; &lt;refused&gt;</c>
    /// or <c>problem &lt;text&gt;</c> line. It ends when the runner kills it (a stop) or when every item is installed.
    /// </summary>
    public static void Run()
    {
        var result = WorkshopSync.Run((installed, total) => Console.WriteLine($"{Prefix}progress {installed} {total}"), CancellationToken.None);
        Console.WriteLine(result.Problem is { } problem
            ? $"{Prefix}problem {problem.ReplaceLineEndings(" ")}"
            : $"{Prefix}result {result.Total} {result.Installed} {result.Refused}");
    }
}
