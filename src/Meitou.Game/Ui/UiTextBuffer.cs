using System.Text;

namespace Meitou.Ui;

/// <summary>The keys an edit box answers (MyGUI 3.2 EditBox); <see cref="Copy"/> and later go through the host's clipboard.</summary>
public enum UiEditKey { Left, Right, Home, End, Backspace, Delete, SelectAll, Up, Down, PageUp, PageDown, DocumentStart, DocumentEnd, Undo, Redo, Copy, Cut, Paste }

/// <summary>
/// Selection, caret and undo history of one edit box, independent of windowing or rendering APIs. Follows MyGUI 3.2.2's <c>EditBox</c> (<b>Verified</b> against its source, docs/game/ui-input.md "Edit boxes"):
/// no word jumps, Ctrl+Home/End to the ends, Up/Down in a single-line box to the ends, 128 undo steps, typed text and pastes truncated from the end at <see cref="MaxLength"/>.
/// </summary>
public sealed class UiTextBuffer(string text = "")
{
    const int MaxUndo = 128;
    readonly record struct State(string Text, int Caret);
    readonly List<State> undo = [], redo = [];
    public string Text { get; private set; } = text;
    public int Caret { get; private set; } = text.Length;
    int anchor = text.Length;
    /// <summary>The longest text typing and pasting build; 512 here, the host passes the box's <c>MaxTextLength</c> or MyGUI's default 2048.</summary>
    public int MaxLength { get; init; } = 512;
    /// <summary>Enter adds a line and Up/Down move between lines (the box's <c>MultiLine</c>).</summary>
    public bool MultiLine { get; init; }
    /// <summary>The box wraps its text; with <see cref="MultiLine"/> it keeps the line breaks of what is pasted.</summary>
    public bool WordWrap { get; init; }
    /// <summary>Copying and cutting give nothing away.</summary>
    public bool Password { get; init; }
    /// <summary>Selecting and copying only.</summary>
    public bool ReadOnly { get; init; }
    public int SelectionStart => Math.Min(anchor, Caret);
    public int SelectionLength => Math.Abs(anchor - Caret);
    public string SelectedText => Text.Substring(SelectionStart, SelectionLength);
    int Previous(int at) => at <= 0 ? 0 : at > 1 && char.IsLowSurrogate(Text[at - 1]) && char.IsHighSurrogate(Text[at - 2]) ? at - 2 : at - 1;
    int Next(int at) => at >= Text.Length ? Text.Length : at + 1 < Text.Length && char.IsHighSurrogate(Text[at]) && char.IsLowSurrogate(Text[at + 1]) ? at + 2 : at + 1;
    int LineStart(int at) => MultiLine && at > 0 ? Text.LastIndexOf('\n', at - 1) + 1 : 0;
    int LineEnd(int at) => MultiLine && Text.IndexOf('\n', at) is var end and >= 0 ? end : Text.Length;

    /// <summary>The text replaced from outside (not typed): the caret goes to the end and, when the text changed, the history is gone (MyGUI's <c>setText</c>).</summary>
    public void Set(string value)
    {
        if (value != Text) { undo.Clear(); redo.Clear(); }
        Text = value; Caret = anchor = value.Length;
    }

    /// <summary>Typed text or a paste: the selection goes, the text goes in, one undo step.</summary>
    public void Insert(string value)
    {
        if (ReadOnly) return;
        var before = new State(Text, Caret);
        RemoveSelection();
        value = Filter(value);
        if (value.Length > 0 && Text.Length < MaxLength)
        {
            Text = Text[..Caret] + value + Text[Caret..]; Caret += value.Length;
            if (Text.Length > MaxLength)
            {
                int cut = char.IsHighSurrogate(Text[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
                Text = Text[..cut]; Caret = Math.Min(Caret, cut);
            }
        }
        anchor = Caret;
        Commit(before);
    }

    /// <summary>Removes the selection (Cut).</summary>
    public void DeleteSelection()
    {
        if (ReadOnly) return;
        var before = new State(Text, Caret);
        RemoveSelection();
        Commit(before);
    }

    /// <summary>Puts the caret at <paramref name="index"/> (clamped), keeping the selection's other end when <paramref name="extend"/> (a pointer drag), else clearing it (a click).</summary>
    public void Place(int index, bool extend = false)
    {
        Caret = Math.Clamp(index, 0, Text.Length);
        if (!extend) anchor = Caret;
    }

    public void Key(UiEditKey key, bool extend = false)
    {
        switch (key)
        {
            case UiEditKey.SelectAll: anchor = 0; Caret = Text.Length; return;
            case UiEditKey.Undo: Undo(); return;
            case UiEditKey.Redo: Redo(); return;
            case UiEditKey.Backspace or UiEditKey.Delete: Erase(key == UiEditKey.Backspace); return;
        }
        Caret = key switch
        {
            UiEditKey.Left => Previous(Caret), UiEditKey.Right => Next(Caret),
            UiEditKey.Home => LineStart(Caret), UiEditKey.End => LineEnd(Caret),
            UiEditKey.DocumentStart => 0, UiEditKey.DocumentEnd => Text.Length,
            UiEditKey.Up or UiEditKey.PageUp => MultiLine ? LineAbove() : 0,
            UiEditKey.Down or UiEditKey.PageDown => MultiLine ? LineBelow() : Text.Length, _ => Caret,
        };
        if (!extend) anchor = Caret;
    }

    int LineAbove()
    {
        int start = LineStart(Caret);
        if (start == 0) return 0;
        int previous = LineStart(start - 1);
        return previous + Math.Min(Caret - start, start - 1 - previous);
    }

    int LineBelow()
    {
        int end = LineEnd(Caret);
        if (end >= Text.Length) return Text.Length;
        int next = end + 1;
        return next + Math.Min(Caret - LineStart(Caret), LineEnd(next) - next);
    }

    void Erase(bool backwards)
    {
        if (ReadOnly) return;
        var before = new State(Text, Caret);
        if (!RemoveSelection())
        {
            if (backwards) { int start = Previous(Caret); Text = Text[..start] + Text[Caret..]; Caret = anchor = start; }
            else Text = Text[..Caret] + Text[Next(Caret)..];
        }
        Commit(before);
    }

    bool RemoveSelection()
    {
        int start = SelectionStart, length = SelectionLength;
        if (length == 0) return false;
        Text = Text[..start] + Text[(start + length)..]; Caret = anchor = start;
        return true;
    }

    /// <summary>An edit that changed the text is one undo step; every edit, changed or not, ends the redo (MyGUI resets it for Backspace, Delete, typing and paste).</summary>
    void Commit(State before)
    {
        redo.Clear();
        if (Text == before.Text) return;
        undo.Add(before);
        if (undo.Count > MaxUndo) undo.RemoveAt(0);
    }

    void Undo()
    {
        if (undo.Count == 0) return;
        var prior = undo[^1]; undo.RemoveAt(undo.Count - 1);
        redo.Add(new(Text, Caret));
        Text = prior.Text; Caret = anchor = Math.Min(prior.Caret, Text.Length);
    }

    void Redo()
    {
        if (redo.Count == 0) return;
        var next = redo[^1]; redo.RemoveAt(redo.Count - 1);
        undo.Add(new(Text, Caret));
        Text = next.Text; Caret = anchor = Math.Min(next.Caret, Text.Length);
    }

    /// <summary>
    /// MyGUI's <c>TextIterator</c> on inserted text: CR+LF is one line break, and a box without lines turns each break into a space. Meitou drops the other control characters and turns
    /// a tab into a space (MyGUI lets both through; Meitou's font draws neither).
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
