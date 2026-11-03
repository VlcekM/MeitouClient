using System.Text;

namespace Meitou.Game;

/// <summary>One console command: <see cref="Run"/> gets the arguments (quotes already removed) and prints through the console. <see cref="Complete"/> lists what Tab may fill in for an argument.</summary>
/// <param name="Name">What is typed first, lower case.</param>
/// <param name="Usage">E.g. <c>give "&lt;item record&gt;" [qty] [quality]</c>.</param>
/// <param name="Help">One line for <c>help</c>.</param>
public sealed record ConsoleCommand(string Name, string Usage, string Help, Action<IReadOnlyList<string>, DevConsole> Run)
{
    /// <summary>The candidates for argument <c>index</c> (0 is the first after the name) beginning with, or else containing, the text typed so far; null: none are offered.</summary>
    public Func<int, string, IEnumerable<string>>? Complete { get; init; }
}

/// <summary>
/// The text half of the dev console (Meitou only, docs/game/ui-input.md "Dev console"): the input line with its caret, history, scrollback, parsing and Tab completion, and the table of
/// <see cref="ConsoleCommand"/>s. It knows nothing of the world; the host registers commands that enqueue simulation commands. A later mod API can register into the same table.
/// </summary>
public sealed class DevConsole
{
    public enum Kind { Input, Info, Error }

    public readonly record struct Line(string Text, Kind Kind);

    const int MaxLines = 100, MaxHistory = 50;

    readonly Dictionary<string, ConsoleCommand> commands = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Line> lines = [];
    readonly List<string> history = [];
    int historyAt = -1;
    string draft = "";

    public bool Open { get; set; }
    public string Input { get; private set; } = "";
    public int Caret { get; private set; }
    /// <summary>How many visual lines the scrollback is scrolled up from the newest.</summary>
    public int Scroll { get; set; }
    public IReadOnlyList<Line> Lines => lines;
    public IEnumerable<ConsoleCommand> Commands => commands.Values.OrderBy(c => c.Name, StringComparer.Ordinal);

    public void Register(ConsoleCommand command) => commands[command.Name] = command;

    /// <summary>Adds a line to the scrollback and to log.txt.</summary>
    public void Print(string text, Kind kind = Kind.Info)
    {
        foreach (string part in text.ReplaceLineEndings("\n").Split('\n'))
        {
            lines.Add(new Line(part, kind));
            Console.WriteLine($"console  {(kind == Kind.Error ? "error: " : kind == Kind.Input ? "> " : "")}{part}");
        }
        if (lines.Count > MaxLines) lines.RemoveRange(0, lines.Count - MaxLines);
        Scroll = 0;
    }

    public void Error(string text) => Print(text, Kind.Error);

    public void Clear() { lines.Clear(); Scroll = 0; }

    // ---- editing

    public void Type(char c)
    {
        if (char.IsControl(c) || c == '`') return;   // the toggle key's own character
        Input = Input.Insert(Caret++, c.ToString());
    }

    public void Paste(string text)
    {
        foreach (char c in text.ReplaceLineEndings(" ")) Type(c);
    }

    public void Backspace()
    {
        if (Caret > 0) Input = Input.Remove(--Caret, 1);
    }

    public void Delete()
    {
        if (Caret < Input.Length) Input = Input.Remove(Caret, 1);
    }

    public void MoveCaret(int to) => Caret = Math.Clamp(to, 0, Input.Length);

    public void HistoryStep(int direction)
    {
        if (history.Count == 0) return;
        if (historyAt < 0)
        {
            if (direction > 0) return;
            draft = Input;
            historyAt = history.Count;
        }
        historyAt += direction;
        if (historyAt >= history.Count) { historyAt = -1; SetInput(draft); return; }
        historyAt = Math.Max(historyAt, 0);
        SetInput(history[historyAt]);
    }

    void SetInput(string text) => (Input, Caret) = (text, text.Length);

    /// <summary>Runs the input line and clears it.</summary>
    public void Submit()
    {
        string line = Input.Trim();
        SetInput("");
        historyAt = -1;
        if (line.Length == 0) return;
        if (history.Count == 0 || history[^1] != line) history.Add(line);
        if (history.Count > MaxHistory) history.RemoveAt(0);
        Execute(line);
    }

    // ---- commands

    /// <summary>Runs one command line. An unknown command, a bad argument or an exception in a handler prints an error line; nothing is thrown.</summary>
    public void Execute(string line)
    {
        Print(line, Kind.Input);
        var tokens = Tokenize(line, out _);
        if (tokens.Count == 0) return;
        if (!commands.TryGetValue(tokens[0].Text, out var command)) { Error($"unknown command '{tokens[0].Text}' (help lists them)"); return; }
        try { command.Run([.. tokens.Skip(1).Select(t => t.Text)], this); }
        catch (Exception e) { Error($"{command.Name} failed: {e.Message}"); }
    }

    public ConsoleCommand? Find(string name) => commands.GetValueOrDefault(name);

    /// <summary>Words split at spaces outside double quotes, the quotes removed; each with its start in <paramref name="text"/>. <paramref name="fresh"/>: the text ends where a new word would begin.</summary>
    public static List<(string Text, int Start)> Tokenize(string text, out bool fresh)
    {
        var list = new List<(string, int)>();
        var word = new StringBuilder();
        int start = -1;
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') { if (start < 0) start = i; quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (start >= 0) { list.Add((word.ToString(), start)); word.Clear(); start = -1; }
                continue;
            }
            if (start < 0) start = i;
            word.Append(c);
        }
        if (start >= 0) list.Add((word.ToString(), start));
        fresh = start < 0;
        return list;
    }

    // ---- Tab

    /// <summary>Completes the word before the caret: a command name, or an argument the command offers. One candidate is filled in whole, several as far as they agree (and listed).</summary>
    public void Complete()
    {
        string head = Input[..Caret], tail = Input[Caret..];
        var tokens = Tokenize(head, out bool fresh);
        if (fresh) tokens.Add(("", head.Length));
        int index = tokens.Count - 1;
        var (typed, start) = tokens[index];
        IEnumerable<string> found;
        if (index == 0) found = commands.Keys.Where(k => k.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal);
        else found = commands.GetValueOrDefault(tokens[0].Text)?.Complete?.Invoke(index - 1, typed) ?? [];
        var candidates = found.Distinct().Take(500).ToList();
        if (candidates.Count == 0) return;
        string fill;
        if (candidates.Count == 1) fill = Quote(candidates[0], close: true) + " ";
        else
        {
            string common = candidates.Aggregate(CommonPrefix);
            fill = common.Length > typed.Length ? Quote(common, close: false) : head[start..];
            Print(string.Join("   ", candidates.Take(12)) + (candidates.Count > 12 ? $"   ... {candidates.Count - 12} more" : ""));
        }
        Input = head[..start] + fill + tail;
        Caret = start + fill.Length;
    }

    static string Quote(string word, bool close) => word.Contains(' ') ? $"\"{word}{(close ? "\"" : "")}" : word;

    static string CommonPrefix(string a, string b)
    {
        int n = 0;
        while (n < a.Length && n < b.Length && char.ToLowerInvariant(a[n]) == char.ToLowerInvariant(b[n])) n++;
        return a[..n];
    }
}
