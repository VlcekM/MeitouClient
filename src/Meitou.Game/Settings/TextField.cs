using Meitou.Game.Host.Input;

namespace Meitou.Game.Settings;

/// <summary>
/// A one-line text being typed: its caret and selection, moved and edited by the usual keys (arrows and Home / End, with Shift to select; Ctrl+A,
/// Ctrl+C, Ctrl+X, Ctrl+V; Backspace and Delete).
/// </summary>
sealed class TextField(int maxLength)
{
    #region Text

    /// <summary>
    /// The text typed.
    /// </summary>
    internal string Text { get; private set; } = "";

    /// <summary>
    /// The caret and the other end of the selection, in characters.
    /// </summary>
    internal int Caret, Anchor;

    /// <summary>
    /// The selection's start, in characters.
    /// </summary>
    internal int SelectionStart => Math.Min(Caret, Anchor);

    /// <summary>
    /// The selection's length, in characters; 0 for none.
    /// </summary>
    internal int SelectionLength => Math.Abs(Caret - Anchor);

    /// <summary>
    /// Sets the text, the caret at its end and nothing selected.
    /// </summary>
    internal void Set(string text)
    {
        Text = text.Length > maxLength ? text[..maxLength] : text;
        Caret = Anchor = Text.Length;
    }

    /// <summary>
    /// Selects the whole text, the caret at its end.
    /// </summary>
    internal void SelectAll()
    {
        Anchor = 0;
        Caret = Text.Length;
    }

    /// <summary>
    /// Replaces the selection with <paramref name="value"/> (control characters dropped); false when the text would be too long.
    /// </summary>
    internal bool Insert(string value)
    {
        value = new([.. value.Where(c => !char.IsControl(c))]);
        if (Text.Length - SelectionLength + value.Length > maxLength) return false;

        int start = SelectionStart;
        Text = Text[..start] + value + Text[(start + SelectionLength)..];
        Caret = Anchor = start + value.Length;
        return true;
    }

    #endregion

    #region Keys

    /// <summary>
    /// An editing key: true when it was one (the text or the caret may have changed). <paramref name="readClipboard"/> and
    /// <paramref name="writeClipboard"/> reach the clipboard.
    /// </summary>
    internal bool OnKey(Key key, bool shift, bool ctrl, Func<string?>? readClipboard, Action<string>? writeClipboard)
    {
        if (ctrl && OnShortcut(key, readClipboard, writeClipboard)) return true;

        switch (key)
        {
            case Key.Home:
                MoveCaret(0, shift);
                return true;
            case Key.End:
                MoveCaret(Text.Length, shift);
                return true;
            case Key.Left:
                MoveCaret(!shift && SelectionLength > 0 ? SelectionStart : Math.Max(0, Caret - 1), shift);
                return true;
            case Key.Right:
                MoveCaret(!shift && SelectionLength > 0 ? SelectionStart + SelectionLength : Math.Min(Text.Length, Caret + 1), shift);
                return true;
            case Key.Backspace:
            case Key.Delete:
                Erase(key == Key.Backspace);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Ctrl+A selects all, Ctrl+C copies the selection, Ctrl+X cuts it, Ctrl+V pastes over it; false for another key.
    /// </summary>
    bool OnShortcut(Key key, Func<string?>? readClipboard, Action<string>? writeClipboard)
    {
        string selected = Text.Substring(SelectionStart, SelectionLength);

        switch (key)
        {
            case Key.A:
                SelectAll();
                return true;
            case Key.C:
                if (selected.Length > 0) writeClipboard?.Invoke(selected);
                return true;
            case Key.X:
                if (selected.Length == 0) return true;

                writeClipboard?.Invoke(selected);
                Insert("");
                return true;
            case Key.V:
                if (readClipboard?.Invoke() is { } pasted) Insert(pasted.ReplaceLineEndings(" "));
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Puts the caret at <paramref name="at"/>, keeping the selection's other end when <paramref name="select"/>.
    /// </summary>
    void MoveCaret(int at, bool select)
    {
        Caret = at;
        if (!select) Anchor = at;
    }

    /// <summary>
    /// Deletes the selection, or the character before the caret (<paramref name="back"/>) or after it.
    /// </summary>
    void Erase(bool back)
    {
        if (SelectionLength == 0)
        {
            if (back && Caret > 0) Anchor = Caret - 1;
            if (!back && Caret < Text.Length) Anchor = Caret + 1;
        }

        Insert("");
    }

    #endregion
}
