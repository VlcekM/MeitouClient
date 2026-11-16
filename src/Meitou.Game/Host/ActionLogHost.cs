using Meitou.Simulation.Ai;

namespace Meitou.Game.Host;

/// <summary>
/// The host's side of the action log (<see cref="ActionLog"/>; docs/launch-options.md <c>--action-log</c>, the F10 "Action log" row): the log the
/// session's worlds are told of, and its file, log_actions.txt beside log.txt (kept like log.txt, <see cref="GameLog"/>), opened when the
/// log is first switched on. Also the dialogue log's file, log_dialogue.txt.
/// </summary>
sealed class ActionLogHost : IDisposable
{
    /// <summary>
    /// log_dialogue.txt (<see cref="Simulation.Dialogue.DialogueLog"/>), kept like log.txt and opened at its first line; null until then, or when it
    /// cannot be opened.
    /// </summary>
    TextWriter? _dialogue;
    /// <summary>
    /// The dialogue log could not be opened: it is not tried again.
    /// </summary>
    bool _dialogueFailed;

    /// <summary>
    /// The log each world of the session follows its characters with.
    /// </summary>
    public ActionLog Log { get; } = new();

    /// <summary>
    /// Which characters are followed; switching it on opens the file (logged in log.txt when it cannot be).
    /// </summary>
    public ActionLogScope Scope
    {
        get => Log.Scope;
        set
        {
            if (value != ActionLogScope.Off) Open();
            Log.Scope = value;
        }
    }

    /// <summary>
    /// The F10 row's step: off, the squad, everyone, then off again.
    /// </summary>
    public void Cycle() => Scope = (ActionLogScope)(((int)Scope + 1) % 3);

    /// <summary>
    /// Opens log_actions.txt on first use.
    /// </summary>
    void Open()
    {
        if (Log.Writer is not null) return;
        Log.Writer = GameLog.Open("log_actions.txt");
        Console.WriteLine(Log.Writer is null
            ? "actions   log_actions.txt could not be opened (another game has it, or the folder is read-only)"
            : "actions   writing log_actions.txt");
    }

    /// <summary>
    /// Writes the world's dialogue lines taken between ticks to log_dialogue.txt (always on: it is its own file, apart from log.txt).
    /// </summary>
    public void WriteDialogue(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0 || _dialogueFailed) return;
        if (_dialogue is null)
        {
            _dialogue = GameLog.Open("log_dialogue.txt");
            _dialogueFailed = _dialogue is null;
            Console.WriteLine(_dialogueFailed
                ? "dialogue  log_dialogue.txt could not be opened (another game has it, or the folder is read-only)"
                : "dialogue  writing log_dialogue.txt");
            if (_dialogue is null) return;
        }

        foreach (string line in lines) _dialogue.WriteLine(line);
        _dialogue.Flush();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Log.Scope = ActionLogScope.Off;
        Log.Writer?.Dispose();
        _dialogue?.Dispose();
    }
}
