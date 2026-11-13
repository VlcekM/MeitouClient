using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Host.Input;

namespace Meitou.Game.Settings;

/// <summary>
/// The search box in the settings header: while it holds a query, the page shows every row of the other pages whose name or hint matches it, under
/// its page's name, each with its own control.
/// </summary>
sealed class SettingsSearch(SettingsMenu menu)
{
    #region Query

    /// <summary>
    /// The text typed, at most 40 characters.
    /// </summary>
    readonly TextField _field = new(40);

    /// <summary>
    /// The box takes the keys and the text typed.
    /// </summary>
    internal bool Focused;

    /// <summary>
    /// The text typed.
    /// </summary>
    internal string Query => _field.Text;

    /// <summary>
    /// Whether the page shows the search's results: the query holds more than spaces.
    /// </summary>
    internal bool Active => Query.Trim().Length > 0;

    /// <summary>
    /// Focuses the box with its text selected, closing any drop-down and dropping the row focus; true (the key is taken).
    /// </summary>
    internal bool Begin()
    {
        Focused = true;
        _field.SelectAll();
        menu.ActiveChoice = null;
        menu.Focus = -1;
        return true;
    }

    /// <summary>
    /// Empties and leaves the box.
    /// </summary>
    internal void Clear()
    {
        _field.Set("");
        Focused = false;
    }

    /// <summary>
    /// A key while the box is focused: Esc empties it (or leaves an empty one), Enter, Tab and Up / Down leave it, Page Up / Down scroll the
    /// results, the rest edit the text and its selection (<see cref="TextField"/>); every key is taken.
    /// </summary>
    internal bool OnKey(Key key, bool shift, bool ctrl)
    {
        string before = Query;

        if (key == Key.Escape)
        {
            if (Query.Length == 0) Focused = false;
            else _field.Set("");
        }
        else if (key is Key.Enter or Key.Tab or Key.Up or Key.Down) Focused = false;
        else if (key is Key.PageUp or Key.PageDown) menu.Scroll(key == Key.PageUp ? 5 : -5);
        else _field.OnKey(key, shift, ctrl, menu.Player.ClipboardRead, menu.Player.ClipboardWrite);

        if (Query != before) ShowFromTop();

        return true;
    }

    /// <summary>
    /// A character typed: replaces the selection unless it is a control character or the query is full.
    /// </summary>
    internal void OnText(char value)
    {
        if (char.IsControl(value)) return;

        if (_field.Insert(value.ToString())) ShowFromTop();
    }

    /// <summary>
    /// Shows the changed query's results from their top.
    /// </summary>
    void ShowFromTop()
    {
        menu.ScrollOffset = menu.ScrollTarget = 0;
        menu.Focus = -1;
    }

    #endregion

    #region Results

    /// <summary>
    /// The rows of every page but Mods that match each word of the query, under their page's name (a Graphics tab's as Graphics &gt; tab); one line
    /// saying so when none does.
    /// </summary>
    internal List<SettingsMenu.Entry> Results()
    {
        string[] words = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var results = new List<SettingsMenu.Entry>();

        foreach (var (name, rows) in Sections())
        {
            var found = Found(rows, words);
            if (found.Count == 0) continue;

            results.Add(new(name));
            results.AddRange(found.Select(Flat));
        }

        if (results.Count == 0) results.Add(new("No setting matches the search."));

        return results;
    }

    /// <summary>
    /// The rows that match, each with the rows under it (a mode's custom value, <see cref="SettingModes"/>).
    /// </summary>
    static List<SettingsMenu.Entry> Found(List<SettingsMenu.Entry> rows, string[] words)
    {
        var found = new List<SettingsMenu.Entry>();
        bool parentFound = false;

        foreach (var row in rows)
        {
            bool match = Matches(row, words) || row.Depth > 0 && parentFound;
            if (row.Depth == 0) parentFound = match;
            if (match) found.Add(row);
        }

        return found;
    }

    /// <summary>
    /// Each page's name with its rows, unfolded: the Graphics page as its tabs.
    /// </summary>
    IEnumerable<(string Name, List<SettingsMenu.Entry> Rows)> Sections()
    {
        for (int page = 0; page < SettingsMenu.ModsPageIndex; page++)
        {
            if (page != SettingsMenu.GraphicsPageIndex)
            {
                yield return (SettingsMenu.Pages[page], menu.PageRows(page, 0));
                continue;
            }

            for (int tab = 0; tab < SettingsMenu.GraphicsPages.Length; tab++)
            {
                string name = Loc.Tr(SettingsMenu.Pages[page]) + " > " + Loc.Tr(SettingsMenu.GraphicsPages[tab]);
                yield return (name, menu.PageRows(page, tab));
            }
        }
    }

    /// <summary>
    /// Whether every word is in the row's name or hint, as written or as shown in the language chosen (case ignored).
    /// </summary>
    static bool Matches(SettingsMenu.Entry row, string[] words)
    {
        string text = $"{row.Label}\n{row.Hint}\n{Loc.Tr(row.Label)}\n{Loc.Tr(row.Hint)}";

        return words.All(word => text.Contains(word, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>
    /// A result row as a plain row of the list: no feature fold, band or indent.
    /// </summary>
    static SettingsMenu.Entry Flat(SettingsMenu.Entry row) => row with { Depth = 0, Open = null, GroupId = null, Raw = null };

    #endregion

    #region Drawing

    /// <summary>
    /// The box at the header's right, before the close button: the query with its caret while focused, else the query or a muted prompt; a click
    /// focuses it.
    /// </summary>
    internal void DrawBox()
    {
        var canvas = menu.Canvas;
        var box = canvas.R(812, 9, 248, 26);
        bool lit = Focused || box.Contains(menu.Pointer.X, menu.Pointer.Y);
        canvas.Rect(box, new(0.06f, 0.05f, 0.04f, 0.95f));
        canvas.Rect(box with { Y = box.Y + box.Height - menu.Scale, Height = menu.Scale }, SettingsCanvas.Brass with { W = lit ? 0.9f : 0.45f });
        menu.Hits.Add(new(box, () => Begin(), Label: "Search"));


        var textAt = box with { X = box.X + 8 * menu.Scale, Width = box.Width - 16 * menu.Scale };
        if (Focused) DrawEdit(textAt);
        else if (Query.Length > 0) canvas.Text(Query, textAt, 13, SettingsCanvas.LightInk, translate: false);
        else canvas.Text("Search settings", textAt, 13, SettingsCanvas.MutedInk);
    }

    /// <summary>
    /// The focused box's text, caret and selection.
    /// </summary>
    void DrawEdit(UiRect at)
    {
        menu.Controls.Add(new("settings-search", "EditBox", at, Query, "", "", true,
            new Dictionary<string, string>
            {
                ["Focused"] = "true",
                ["TextAlign"] = "Left VCenter",
                ["FontPixels"] = ((int)MathF.Round(13 * menu.Scale)).ToString(CultureInfo.InvariantCulture),
                ["CaretIndex"] = _field.Caret.ToString(CultureInfo.InvariantCulture),
                ["SelectionStart"] = _field.SelectionStart.ToString(CultureInfo.InvariantCulture),
                ["SelectionLength"] = _field.SelectionLength.ToString(CultureInfo.InvariantCulture),
            }, Vector4.Zero, SettingsCanvas.LightInk, Pickable: false));
    }

    #endregion
}
