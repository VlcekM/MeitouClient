using System.Text;

namespace Meitou.Game.DevTools;

/// <summary>One console command: <see cref="Run"/> gets the arguments (quotes already removed) and prints through the console. <see cref="Complete"/> lists what Tab may fill in for an argument.</summary>
/// <param name="Name">What is typed first, lower case.</param>
/// <param name="Usage">E.g. <c>give "&lt;item record&gt;" [qty] [quality]</c>.</param>
/// <param name="Help">One line for <c>help</c>.</param>
public sealed record ConsoleCommand(string Name, string Usage, string Help, Action<IReadOnlyList<string>, DevConsole> Run)
{
    /// <summary>The candidates for argument <c>index</c> (0 is the first after the name) beginning with, or else containing, the text typed so far; null: none are offered.</summary>
    public Func<int, string, IEnumerable<string>>? Complete { get; init; }

    /// <summary>The <c>--flags</c> the usage text names, in order (what Tab offers for a word starting with a dash, and what help lists).</summary>
    public IReadOnlyList<string> Flags => System.Text.RegularExpressions.Regex.Matches(Usage, @"(?<![\w-])--[a-z][a-z0-9-]*").Select(m => m.Value).Distinct().ToList();
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

    readonly Dictionary<string, ConsoleCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Line> _lines = [];
    readonly List<string> _history = [];
    int _historyAt = -1;
    string _draft = "";

    public bool Open { get; set; }
    public string Input { get; private set; } = "";
    public int Caret { get; private set; }
    /// <summary>How many visual lines the scrollback is scrolled up from the newest.</summary>
    public int Scroll { get; set; }
    public IReadOnlyList<Line> Lines => _lines;
    public IEnumerable<ConsoleCommand> Commands => _commands.Values.OrderBy(c => c.Name, StringComparer.Ordinal);

    /// <summary>Called with every non-input line printed (the agent control channel, <c>--control-dir</c>, collects a command's output through it).</summary>
    public Action<string, Kind>? Tap { get; set; }

    public void Register(ConsoleCommand command) => _commands[command.Name] = command;

    /// <summary>Gives an already registered command an argument completer (the registration helpers of the control commands have none).</summary>
    public void SetComplete(string name, Func<int, string, IEnumerable<string>> complete)
    {
        if (_commands.TryGetValue(name, out var c)) _commands[name] = c with { Complete = complete };
    }

    /// <summary>Candidates for the value after a flag (<c>--id</c> takes character ids); the host sets it. Gets the flag and the text typed so far.</summary>
    public Func<string, string, IEnumerable<string>>? FlagValues { get; set; }

    /// <summary>While a completer runs: the arguments before the word being completed (so a completer can tell <c>order attack</c> from <c>order move</c>).</summary>
    public IReadOnlyList<string> CompletionArgs { get; private set; } = [];

    /// <summary>Adds a line to the scrollback and to log.txt.</summary>
    public void Print(string text, Kind kind = Kind.Info)
    {
        foreach (string part in text.ReplaceLineEndings("\n").Split('\n'))
        {
            _lines.Add(new Line(part, kind));
            if (kind != Kind.Input) Tap?.Invoke(part, kind);
            Console.WriteLine($"console  {(kind == Kind.Error ? "error: " : kind == Kind.Input ? "> " : "")}{part}");
        }
        if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
        Scroll = 0;
    }

    public void Error(string text) => Print(text, Kind.Error);

    public void Clear() { _lines.Clear(); Scroll = 0; }

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
        if (_history.Count == 0) return;
        if (_historyAt < 0)
        {
            if (direction > 0) return;
            _draft = Input;
            _historyAt = _history.Count;
        }
        _historyAt += direction;
        if (_historyAt >= _history.Count) { _historyAt = -1; SetInput(_draft); return; }
        _historyAt = Math.Max(_historyAt, 0);
        SetInput(_history[_historyAt]);
    }

    void SetInput(string text) => (Input, Caret) = (text, text.Length);

    /// <summary>Runs the input line and clears it.</summary>
    public void Submit()
    {
        string line = Input.Trim();
        SetInput("");
        _historyAt = -1;
        if (line.Length == 0) return;
        if (_history.Count == 0 || _history[^1] != line) _history.Add(line);
        if (_history.Count > MaxHistory) _history.RemoveAt(0);
        Execute(line);
    }

    // ---- commands

    /// <summary>Runs one command line. An unknown command, a bad argument or an exception in a handler prints an error line; nothing is thrown.</summary>
    public void Execute(string line)
    {
        Print(line, Kind.Input);
        var tokens = Tokenize(line, out _);
        if (tokens.Count == 0) return;
        if (!_commands.TryGetValue(tokens[0].Text, out var command)) { Error(UnknownCommand(tokens[0].Text)); return; }
        try { command.Run([.. tokens.Skip(1).Select(t => t.Text)], this); }
        catch (Exception e) { Error($"{command.Name} failed: {e.Message}"); }
    }

    /// <summary>The error line for an unknown command, with the closest names (prefix, substring, or an edit distance of at most 2).</summary>
    string UnknownCommand(string typed)
    {
        string low = typed.ToLowerInvariant();
        var close = _commands.Keys.Where(n => n.StartsWith(low, StringComparison.OrdinalIgnoreCase) || n.Contains(low, StringComparison.OrdinalIgnoreCase) || EditDistance(n, low) <= 2)
            .OrderBy(n => EditDistance(n, low)).ThenBy(n => n, StringComparer.Ordinal).Take(5).ToList();
        return $"unknown command '{typed}'" + (close.Count > 0 ? "; did you mean: " + string.Join(", ", close) + "?" : " (help lists them)");
    }

    /// <summary>Edit distance between two names.</summary>
    public static int EditDistance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int diagonal = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }

    public ConsoleCommand? Find(string name) => _commands.GetValueOrDefault(name);

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
        if (index == 0) found = _commands.Keys.Where(k => k.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal);
        else
        {
            var command = _commands.GetValueOrDefault(tokens[0].Text);
            CompletionArgs = [.. tokens.Skip(1).Take(index - 1).Select(t => t.Text)];
            string previous = index >= 2 ? tokens[index - 1].Text : "";
            if (command is null) found = [];
            else if (typed.StartsWith('-')) found = command.Flags.Where(f => f.StartsWith(typed, StringComparison.OrdinalIgnoreCase));
            else if (previous.StartsWith("--", StringComparison.Ordinal) && FlagValues?.Invoke(previous, typed) is { } values && values.Any()) found = values;
            else found = command.Complete?.Invoke(index - 1, typed) ?? [];
            found = found.ToList();
            CompletionArgs = [];
        }
        var candidates = found.Distinct().Take(500).ToList();
        if (candidates.Count == 0) { Print(typed.Length == 0 ? "no completions here" : $"no matches for '{typed}'"); return; }
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