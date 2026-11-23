using Meitou.Data.Ui;
using Meitou.Game.Host.Input;

namespace Meitou.Game.Settings;

/// <summary>
/// A free-text row of the settings (a mod's <c>string</c> setting without options, docs/modding.md "Settings"): what it shows and keeps, and its
/// longest text.
/// </summary>
sealed record TextEntry(string Key, Func<string> Get, Action<string> Set, int MaxLength);

/// <summary>
/// The text row being typed in: a click on the row's box starts it with the text selected; Enter, Tab or a click elsewhere keeps the text, Esc drops
/// it. While it types, the keys and the text go to it.
/// </summary>
sealed class SettingsTextEdit(SettingsMenu menu)
{
    /// <summary>
    /// The row typed in; null when none.
    /// </summary>
    internal TextEntry? Editing { get; private set; }

    /// <summary>
    /// The text typed, its caret and selection.
    /// </summary>
    internal TextField Field { get; private set; } = new(0);

    /// <summary>
    /// Where the box typed in is.
    /// </summary>
    internal UiRect Bounds;

    /// <summary>
    /// Whether row <paramref name="entry"/> is the one typed in (by key: the rows are built again each frame).
    /// </summary>
    internal bool Edits(TextEntry entry) => Editing?.Key == entry.Key;

    /// <summary>
    /// Starts typing in <paramref name="entry"/>'s box at <paramref name="at"/>, its text selected.
    /// </summary>
    internal void Begin(TextEntry entry, UiRect at)
    {
        Editing = entry;
        Field = new TextField(entry.MaxLength);
        Field.Set(entry.Get());
        Field.SelectAll();
        Bounds = at;
        menu.ActiveChoice = null;
        menu.Notice = "";
    }

    /// <summary>
    /// Keeps the typed text (the row's setter runs) and stops typing.
    /// </summary>
    internal void Commit()
    {
        if (Editing is not { } entry) return;

        Editing = null;
        entry.Set(Field.Text);
    }

    /// <summary>
    /// Stops typing without keeping the text.
    /// </summary>
    internal void Cancel() => Editing = null;

    /// <summary>
    /// A key while typing: Esc drops the text, Enter and Tab keep it, the rest edit it (<see cref="TextField"/>); every key is taken.
    /// </summary>
    internal bool OnKey(Key key, bool shift, bool ctrl)
    {
        if (key == Key.Escape) Cancel();
        else if (key is Key.Enter or Key.Tab) Commit();
        else Field.OnKey(key, shift, ctrl, menu.Player.ClipboardRead, menu.Player.ClipboardWrite);

        return true;
    }

    /// <summary>
    /// A character typed: replaces the selection unless it is a control character or the text is full.
    /// </summary>
    internal void OnText(char value)
    {
        if (!char.IsControl(value)) Field.Insert(value.ToString());
    }
}
