using System.Globalization;
using System.Text;
using Meitou.Game.DevTools;

namespace Meitou.Game;

/// <summary>The <c>ask</c> and <c>task</c> console commands (agent-playtest): the player types a question or an instruction in the dev console and an AI agent reads the queue file (docs/procedures/playtest/agent-play-api.md, "Ask and task").</summary>
sealed partial class GameHost
{
    const int AskMaxLength = 300;
    readonly object askLock = new();

    void RegisterControlAskCommands(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add)
    {
        add("ask", "ask <text>", "queue a question for the AI agent: one line `<iso time><TAB>ask<TAB><text>` is appended to asks.log in the control directory (else next to the game's log.txt); the agent answers in game with say; text at most 300 characters; the world is not changed, the game is not paused; replies `queued ask #<n>`", (args, con) => AskQueue("ask", args, con));
        add("task", "task <text>", "queue an instruction for the AI agent: one line `<iso time><TAB>task<TAB><text>` is appended to asks.log in the control directory (else next to the game's log.txt); the agent plans, does it with the normal commands and reports with say; text at most 300 characters; the world is not changed, the game is not paused; replies `queued task #<n>`", (args, con) => AskQueue("task", args, con));
    }

    void AskQueue(string kind, IReadOnlyList<string> args, DevConsole con)
    {
        string text = string.Join(' ', args).Trim().Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        if (text.Length == 0) { con.Error($"usage: {kind} <text>"); return; }
        if (text.Length > AskMaxLength) { con.Error($"{kind}: text is {text.Length} characters, at most {AskMaxLength}"); return; }
        string dir = controlDir ?? AppContext.BaseDirectory;
        string path = Path.Combine(dir, "asks.log");
        try
        {
            int n;
            lock (askLock)
            {
                n = (File.Exists(path) ? File.ReadAllLines(path).Count(l => l.Length > 0) : 0) + 1;
                File.AppendAllText(path, $"{DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)}\t{kind}\t{text}\n", new UTF8Encoding(false));
            }
            con.Print($"queued {kind} #{n}" + (controlDir is null ? $" (no control dir: {path})" : ""));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { con.Error($"{kind}: cannot write {path}: {e.Message}"); }
    }
}
