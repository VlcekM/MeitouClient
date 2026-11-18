using System.Globalization;
using Meitou.Steam;

namespace Meitou.Launcher;

/// <summary>
/// The process that subscribes to Workshop items: the launcher started again with <see cref="Argument"/>, the Steam API library's path and the
/// item ids, without a window. A process of its own for the reason <see cref="WorkshopSyncChild"/> is one (Steam counts the account as playing
/// Kenshi until the process that started Steamworks exits), and so that a library that misbehaves cannot take the launcher down. It talks to
/// <see cref="WorkshopSubscribeRunner"/> in lines on its standard output.
/// </summary>
static class WorkshopSubscribeChild
{
    /// <summary>
    /// The command-line argument that makes the launcher subscribe instead of opening its window.
    /// </summary>
    public const string Argument = "--workshop-subscribe";

    /// <summary>
    /// Subscribes and prints one <c>item &lt;id&gt; &lt;installed 0|1&gt; [problem]</c> line per item, or one <c>problem &lt;text&gt;</c> line
    /// when Steam could not be asked. <paramref name="arguments"/> are the library's path, then the ids.
    /// </summary>
    public static void Run(string[] arguments)
    {
        ulong[] items = [.. arguments.Skip(1).Select(a => ulong.Parse(a, CultureInfo.InvariantCulture))];
        var result = WorkshopSubscribe.Run(arguments[0], items);
        if (result.Problem is { } problem) Print($"problem {problem}");

        foreach (var item in result.Items) Print($"item {item.Id} {(item.Installed ? 1 : 0)} {item.Problem}");
    }

    /// <summary>
    /// Prints one line for the runner.
    /// </summary>
    static void Print(string line) => Console.WriteLine(WorkshopSyncChild.Prefix + line.ReplaceLineEndings(" ").TrimEnd());
}
