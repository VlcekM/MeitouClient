using System.Text;

namespace Meitou.Game.Ui;

/// <summary>
/// The keys an edit box answers (MyGUI 3.2 EditBox); <see cref="UiEditKey.Copy"/> and later go through the host's clipboard.
/// </summary>
public enum UiEditKey
{
    /// <summary>
    /// One character left.
    /// </summary>
    Left,

    /// <summary>
    /// One character right.
    /// </summary>
    Right,

    /// <summary>
    /// The line's start.
    /// </summary>
    Home,

    /// <summary>
    /// The line's end.
    /// </summary>
    End,

    /// <summary>
    /// Erases the selection or the character before the caret.
    /// </summary>
    Backspace,

    /// <summary>
    /// Erases the selection or the character after the caret.
    /// </summary>
    Delete,

    /// <summary>
    /// Selects the whole text.
    /// </summary>
    SelectAll,

    /// <summary>
    /// One line up (a single-line box: the start).
    /// </summary>
    Up,

    /// <summary>
    /// One line down (a single-line box: the end).
    /// </summary>
    Down,

    /// <summary>
    /// As Up.
    /// </summary>
    PageUp,

    /// <summary>
    /// As Down.
    /// </summary>
    PageDown,

    /// <summary>
    /// The text's start (Ctrl+Home).
    /// </summary>
    DocumentStart,

    /// <summary>
    /// The text's end (Ctrl+End).
    /// </summary>
    DocumentEnd,

    /// <summary>
    /// Goes back one edit.
    /// </summary>
    Undo,

    /// <summary>
    /// Goes forward one undone edit.
    /// </summary>
    Redo,

    /// <summary>
    /// Copies the selection (nothing in a password box).
    /// </summary>
    Copy,

    /// <summary>
    /// Copies and removes the selection.
    /// </summary>
    Cut,

    /// <summary>
    /// Inserts the clipboard's text.
    /// </summary>
    Paste,
}

/// <summary>
/// Selection, caret and undo history of one edit box, independent of windowing or rendering APIs. Follows MyGUI 3.2.2's <c>EditBox</c>
/// (<b>Verified</b> against its source, docs/game/ui-input.md "Edit boxes"): no word jumps, Ctrl+Home/End to the ends, Up/Down in a single-line box
/// to the ends, 128 undo steps, typed text and pastes truncated from the end at <see cref="MaxLength"/>.
/// </summary>
public sealed class UiTextBuffer(string text = "")
{
    /// <summary>
    /// The undo steps kept.
    /// </summary>
    const int MaxUndo = 128;

    /// <summary>
    /// The text and caret an undo or redo step goes back to.
    /// </summary>
    readonly record struct State(string Text, int Caret);

    /// <summary>
    /// The undo and redo steps, oldest first.
    /// </summary>
    readonly List<State> _undo = [], _redo = [];

    /// <summary>
    /// The selection's other end (the caret's when nothing is selected).
    /// </summary>
    int _anchor = text.Length;

    /// <summary>
    /// The box's text.
    /// </summary>
    public string Text { get; private set; } = text;

    /// <summary>
    /// The caret's index in the text.
    /// </summary>
    public int Caret { get; private set; } = text.Length;

    /// <summary>
    /// The longest text typing and pasting build; 512 here, the host passes the box's <c>MaxTextLength</c> or MyGUI's default 2048.
    /// </summary>
    public int MaxLength { get; init; } = 512;

    /// <summary>
    /// Enter adds a line and Up/Down move between lines (the box's <c>MultiLine</c>).
    /// </summary>
    public bool MultiLine { get; init; }

    /// <summary>
    /// The box wraps its text; with <see cref="MultiLine"/> it keeps the line breaks of what is pasted.
    /// </summary>
    public bool WordWrap { get; init; }

    /// <summary>
    /// Copying and cutting give nothing away.
    /// </summary>
    public bool Password { get; init; }

    /// <summary>
    /// Selecting and copying only.
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// Where the selection starts.
    /// </summary>
    public int SelectionStart => Math.Min(_anchor, Caret);

    /// <summary>
    /// How many characters are selected.
    /// </summary>
    public int SelectionLength => Math.Abs(_anchor - Caret);

    /// <summary>
    /// The selected text.
    /// </summary>
    public string SelectedText => Text.Substring(SelectionStart, SelectionLength);

    /// <summary>
    /// The index one character before <paramref name="at"/> (a surrogate pair as one).
    /// </summary>
    int Previous(int at)
    {
        if (at <= 0) return 0;

        bool pair = at > 1 && char.IsLowSurrogate(Text[at - 1]) && char.IsHighSurrogate(Text[at - 2]);
        return pair ? at - 2 : at - 1;
    }

    /// <summary>
    /// The index one character after <paramref name="at"/> (a surrogate pair as one).
    /// </summary>
    int Next(int at)
    {
        if (at >= Text.Length) return Text.Length;

        bool pair = at + 1 < Text.Length && char.IsHighSurrogate(Text[at]) && char.IsLowSurrogate(Text[at + 1]);
        return pair ? at + 2 : at + 1;
    }

    /// <summary>
    /// The start of the line holding <paramref name="at"/> (0 in a single-line box).
    /// </summary>
    int LineStart(int at) => MultiLine && at > 0 ? Text.LastIndexOf('\n', at - 1) + 1 : 0;

    /// <summary>
    /// The end of the line holding <paramref name="at"/> (the text's end in a single-line box).
    /// </summary>
    int LineEnd(int at) => MultiLine && Text.IndexOf('\n', at) is var end and >= 0 ? end : Text.Length;

    /// <summary>
    /// The text replaced from outside (not typed): the caret goes to the end and, when the text changed, the history is gone (MyGUI's
    /// <c>setText</c>).
    /// </summary>
    public void Set(string value)
    {
        if (value != Text)
        {
            _undo.Clear();
            _redo.Clear();
        }

        Text = value;
        Caret = _anchor = value.Length;
    }

    /// <summary>
    /// Typed text or a paste: the selection goes, the text goes in, one undo step.
    /// </summary>
    public void Insert(string value)
    {
        if (ReadOnly) return;

        var before = new State(Text, Caret);
        RemoveSelection();
        value = Filter(value);
        if (value.Length > 0 && Text.Length < MaxLength)
        {
            Text = Text[..Caret] + value + Text[Caret..];
            Caret += value.Length;
            if (Text.Length > MaxLength)
            {
                int cut = char.IsHighSurrogate(Text[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
                Text = Text[..cut];
                Caret = Math.Min(Caret, cut);
            }
        }

        _anchor = Caret;
        Commit(before);
    }

    /// <summary>
    /// Removes the selection (Cut).
    /// </summary>
    public void DeleteSelection()
    {
        if (ReadOnly) return;

        var before = new State(Text, Caret);
        RemoveSelection();
        Commit(before);
    }

    /// <summary>
    /// Puts the caret at <paramref name="index"/> (clamped), keeping the selection's other end when <paramref name="extend"/> (a pointer drag), else
    /// clearing it (a click).
    /// </summary>
    public void Place(int index, bool extend = false)
    {
        Caret = Math.Clamp(index, 0, Text.Length);
        if (!extend) _anchor = Caret;
    }

    /// <summary>
    /// A caret or editing key: select all, undo, redo, erase, or a caret move (selecting with <paramref name="extend"/>); Up and Down go to the ends
    /// of a single-line box.
    /// </summary>
    public void Key(UiEditKey key, bool extend = false)
    {
        switch (key)
        {
            case UiEditKey.SelectAll:
                _anchor = 0;
                Caret = Text.Length;
                return;
            case UiEditKey.Undo: Undo(); return;
            case UiEditKey.Redo: Redo(); return;
            case UiEditKey.Backspace or UiEditKey.Delete: Erase(key == UiEditKey.Backspace); return;
        }

        Caret = key switch
        {
            UiEditKey.Left => Previous(Caret),
            UiEditKey.Right => Next(Caret),
            UiEditKey.Home => LineStart(Caret),
            UiEditKey.End => LineEnd(Caret),
            UiEditKey.DocumentStart => 0,
            UiEditKey.DocumentEnd => Text.Length,
            UiEditKey.Up or UiEditKey.PageUp => MultiLine ? LineAbove() : 0,
            UiEditKey.Down or UiEditKey.PageDown => MultiLine ? LineBelow() : Text.Length,
            _ => Caret,
        };
        if (!extend) _anchor = Caret;
    }

    /// <summary>
    /// The caret's place one line up, at most the line's length in; 0 on the first line.
    /// </summary>
    int LineAbove()
    {
        int start = LineStart(Caret);
        if (start == 0) return 0;

        int previous = LineStart(start - 1);
        return previous + Math.Min(Caret - start, start - 1 - previous);
    }

    /// <summary>
    /// The caret's place one line down, at most the line's length in; the end on the last line.
    /// </summary>
    int LineBelow()
    {
        int end = LineEnd(Caret);
        if (end >= Text.Length) return Text.Length;

        int next = end + 1;
        return next + Math.Min(Caret - LineStart(Caret), LineEnd(next) - next);
    }

    /// <summary>
    /// Backspace (<paramref name="backwards"/>) or Delete: removes the selection, else one character; one undo step.
    /// </summary>
    void Erase(bool backwards)
    {
        if (ReadOnly) return;

        var before = new State(Text, Caret);
        if (!RemoveSelection())
        {
            if (backwards)
            {
                int start = Previous(Caret);
                Text = Text[..start] + Text[Caret..];
                Caret = _anchor = start;
            }
            else Text = Text[..Caret] + Text[Next(Caret)..];
        }

        Commit(before);
    }

    /// <summary>
    /// Removes the selected text (the caret at its start); false when nothing was selected.
    /// </summary>
    bool RemoveSelection()
    {
        int start = SelectionStart, length = SelectionLength;
        if (length == 0) return false;

        Text = Text[..start] + Text[(start + length)..];
        Caret = _anchor = start;
        return true;
    }

    /// <summary>
    /// An edit that changed the text is one undo step; every edit, changed or not, ends the redo (MyGUI resets it for Backspace, Delete, typing and
    /// paste).
    /// </summary>
    void Commit(State before)
    {
        _redo.Clear();
        if (Text == before.Text) return;

        _undo.Add(before);
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
    }

    /// <summary>
    /// Goes back one undo step, keeping the present for redo.
    /// </summary>
    void Undo()
    {
        if (_undo.Count == 0) return;

        var prior = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(new(Text, Caret));
        Text = prior.Text;
        Caret = _anchor = Math.Min(prior.Caret, Text.Length);
    }

    /// <summary>
    /// Goes forward one redo step, keeping the present for undo.
    /// </summary>
    void Redo()
    {
        if (_redo.Count == 0) return;

        var next = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(new(Text, Caret));
        Text = next.Text;
        Caret = _anchor = Math.Min(next.Caret, Text.Length);
    }

    /// <summary>
    /// MyGUI's <c>TextIterator</c> on inserted text: CR+LF is one line break, and a box without lines turns each break into a space. Meitou drops the
    /// other control characters and turns a tab into a space (MyGUI lets both through; Meitou's font draws neither).
    /// </summary>
    string Filter(string value)
    {
        bool lines = MultiLine || WordWrap;
        var result = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\r' && i + 1 < value.Length && value[i + 1] == '\n') continue;
            if (c is '\n' or '\r' or '\u0085') result.Append(lines ? '\n' : ' ');
            else if (c == '\t') result.Append(' ');
            else if (!char.IsControl(c)) result.Append(c);
        }

        return result.ToString();
    }
}