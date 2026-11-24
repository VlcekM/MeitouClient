using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Host.Input;
using Meitou.Game.Rendering.Frame;
using Meitou.Modding;

namespace Meitou.Game.Settings;

/// <summary>
/// The MODS page: the loaded mods in load order (filterable by name, author or setting, and sortable), each with its code's settings under it
/// (<see cref="ModSettingsRows"/>), and the selected mod's author, description and data problems.
/// </summary>
sealed class ModsPage(SettingsMenu menu)
{
    #region State

    /// <summary>
    /// A mod row's height, in the menu's units.
    /// </summary>
    const float ModRowHeight = 26;

    /// <summary>
    /// Most issue lines the text area shows; Copy takes them all.
    /// </summary>
    const int ModIssueCap = 200;

    /// <summary>
    /// The text area's height, about eight lines (it scrolls), and its top over the page's bottom (666), in the menu's units; the list takes the
    /// page above the Copy buttons.
    /// </summary>
    const float DetailHeight = 130, DetailTop = 666 - DetailHeight;

    /// <summary>
    /// The toolbar's top, the Copy buttons' top, and the list's top and bottom, in the menu's units.
    /// </summary>
    const float ToolbarTop = 96, ButtonsTop = DetailTop - 38, ListTop = 130, ListBottom = ButtonsTop - 8;

    /// <summary>
    /// The filter text typed, at most 40 characters.
    /// </summary>
    readonly TextField _filter = new(40);

    /// <summary>
    /// The selected mod's row; -1 for none.
    /// </summary>
    int _modsSelected = -1;

    /// <summary>
    /// Which subset of mods the list shows.
    /// </summary>
    ModListFilter _filterMode = ModListFilter.All;

    /// <summary>
    /// How the mod list is ordered.
    /// </summary>
    ModListSort _sort = ModListSort.LoadOrder;

    /// <summary>
    /// When set, the next draw scrolls to show this mod row, then clears it.
    /// </summary>
    int? _pendingScrollRow;

    /// <summary>
    /// Whether the mod filter box has the keyboard focus.
    /// </summary>
    internal bool FilterFocused;

    /// <summary>
    /// How far the text area is scrolled, in the menu's units.
    /// </summary>
    internal float ModsDetailScroll;

    /// <summary>
    /// The settings its code registered under each mod's row.
    /// </summary>
    internal readonly ModSettingsRows Settings = new(menu);

    /// <summary>
    /// The text area under the list (and its Copy buttons), in pixels.
    /// </summary>
    internal UiRect ModsDetail => menu.Canvas.R(212, DetailTop, 888, DetailHeight);

    #endregion

    #region Filter input

    /// <summary>
    /// Focuses the mod filter box with its text selected.
    /// </summary>
    internal void BeginFilter()
    {
        FilterFocused = true;
        _filter.SelectAll();
        menu.ActiveChoice = null;
        menu.Focus = -1;
    }

    /// <summary>
    /// A key while the mod filter box is focused: Esc clears or leaves it, Enter / Tab / Up / Down leave it, Page Up / Down scroll, the rest edit
    /// the filter text.
    /// </summary>
    internal bool OnFilterKey(Key key, bool shift, bool ctrl)
    {
        string before = _filter.Text;

        if (key == Key.Escape)
        {
            if (_filter.Text.Length == 0) FilterFocused = false;
            else _filter.Set("");
        }
        else if (key is Key.Enter or Key.Tab or Key.Up or Key.Down) FilterFocused = false;
        else if (key is Key.PageUp or Key.PageDown) menu.Scroll(key == Key.PageUp ? 5 : -5);
        else _filter.OnKey(key, shift, ctrl, menu.Player.ClipboardRead, menu.Player.ClipboardWrite);

        if (_filter.Text != before) ResetScroll();

        return true;
    }

    /// <summary>
    /// A character typed into the mod filter box.
    /// </summary>
    internal void OnFilterText(char value)
    {
        if (char.IsControl(value)) return;

        if (_filter.Insert(value.ToString())) ResetScroll();
    }

    /// <summary>
    /// Opens the Mods page with <paramref name="modName"/> selected and scrolled into view (used from global search results).
    /// </summary>
    internal void OpenMod(string modName)
    {
        _filter.Set("");
        FilterFocused = false;
        _filterMode = ModListFilter.All;
        _sort = ModListSort.LoadOrder;
        menu.Navigate(SettingsMenu.ModsPageIndex);

        if (menu.Player.TitleMenu.Mods is not { } report) return;

        for (int i = 0; i < report.Rows.Count; i++)
        {
            if (report.Rows[i].Separator || !report.Rows[i].Name.Equals(modName, StringComparison.OrdinalIgnoreCase)) continue;

            _modsSelected = i;
            ModsDetailScroll = 0;
            _pendingScrollRow = i;
            return;
        }
    }

    /// <summary>
    /// Scrolls the mod list back to its top.
    /// </summary>
    void ResetScroll()
    {
        menu.ScrollOffset = menu.ScrollTarget = 0;
        menu.Focus = -1;
    }

    #endregion

    #region Drawing

    /// <summary>
    /// The summary line under the version text, left of the page buttons' divider; a click opens the MODS page.
    /// </summary>
    internal void DrawModsSummary()
    {
        if (menu.Player.TitleMenu.Mods is not { } mods) return;

        var at = menu.Canvas.R(28, 646, 160, 16);
        bool lit = at.Contains(menu.Pointer.X, menu.Pointer.Y) || menu.Page == SettingsMenu.ModsPageIndex;
        var ink = lit ? SettingsCanvas.LightInk : mods.Problems > 0 ? SettingsCanvas.Brass : SettingsCanvas.MutedInk;
        menu.Canvas.Text(mods.Summary, at, 10, ink, translate: false);
        menu.Hits.Add(new(at, () => menu.Navigate(SettingsMenu.ModsPageIndex)));
    }

    /// <summary>
    /// The page: the filter/sort toolbar, the scrolled list of mods and their settings, the Copy buttons and the text area.
    /// </summary>
    internal void DrawMods()
    {
        var report = menu.Player.TitleMenu.Mods;
        var rows = report?.Rows ?? [];
        var lines = Settings.ModsLines(rows, _filter.Text, _filterMode, _sort, out int matched, out int total);
        DrawToolbar(matched, total);

        menu.Viewport = menu.Canvas.R(212, ListTop, 888, ListBottom - ListTop);
        menu.MaxScroll = Math.Max(0, lines.Sum(LineHeight) * menu.Scale - menu.Viewport.Height);
        ApplyPendingScroll(lines);
        menu.Motion.AdvanceScroll();

        if (lines.Count == 0)
        {
            menu.Canvas.Text("No mod matches the filter.", menu.Canvas.R(224, ListTop + 8, 864, 24), 13, SettingsCanvas.MutedInk);
        }
        else
        {
            float y = ListTop - menu.ScrollOffset / menu.Scale;
            foreach (var line in lines)
            {
                float height = LineHeight(line);
                if (y + height >= ListTop && y <= ListBottom) DrawModsLine(rows, line, y);
                y += height;
            }
        }

        menu.Canvas.Button("Copy", menu.Canvas.R(212, ButtonsTop, 120, 28), CopyMod);
        menu.Canvas.Button("Copy all problems", menu.Canvas.R(340, ButtonsTop, 200, 28), CopyProblems);
        DrawFoldButtons();
        DrawModsDetail(report);
    }

    /// <summary>
    /// Scrolls the list once to bring <see cref="_pendingScrollRow"/> near the top of the viewport.
    /// </summary>
    void ApplyPendingScroll(List<ModsLine> lines)
    {
        if (_pendingScrollRow is not { } targetRow) return;

        _pendingScrollRow = null;
        float offset = 0;
        foreach (var line in lines)
        {
            if (line.Entry is null && line.Row == targetRow)
            {
                menu.ScrollOffset = menu.ScrollTarget = Math.Clamp(offset * menu.Scale, 0, menu.MaxScroll);
                return;
            }

            offset += LineHeight(line);
        }
    }

    /// <summary>
    /// The filter box, filter-mode buttons (All / Settings / Issues), sort toggle and mod count above the list.
    /// </summary>
    void DrawToolbar(int matched, int total)
    {
        DrawFilterBox();
        menu.Canvas.Button("All", menu.Canvas.R(480, ToolbarTop, 56, 26), () => SetFilterMode(ModListFilter.All),
            selected: _filterMode == ModListFilter.All);
        menu.Canvas.Button("Settings", menu.Canvas.R(540, ToolbarTop, 88, 26), () => SetFilterMode(ModListFilter.WithSettings),
            selected: _filterMode == ModListFilter.WithSettings);
        menu.Canvas.Button("Issues", menu.Canvas.R(632, ToolbarTop, 74, 26), () => SetFilterMode(ModListFilter.WithIssues),
            selected: _filterMode == ModListFilter.WithIssues);

        string sortLabel = _sort == ModListSort.LoadOrder ? "Load order" : "Name A-Z";
        menu.Canvas.Button(sortLabel, menu.Canvas.R(714, ToolbarTop, 114, 26), ToggleSort);

        string count = matched == total
            ? Loc.Plural("{1} mod", "{1} mods", total, total)
            : Loc.Tr("{1} of {2}", matched, total);
        menu.Canvas.Text(count, menu.Canvas.R(836, ToolbarTop, 264, 26), 12, SettingsCanvas.MutedInk, "Right VCenter", translate: false);
    }

    /// <summary>
    /// The mod filter text box at the toolbar's left.
    /// </summary>
    void DrawFilterBox()
    {
        var canvas = menu.Canvas;
        var box = canvas.R(212, ToolbarTop, 260, 26);
        bool lit = FilterFocused || box.Contains(menu.Pointer.X, menu.Pointer.Y);
        canvas.Rect(box, new(0.06f, 0.05f, 0.04f, 0.95f));
        canvas.Rect(box with { Y = box.Y + box.Height - menu.Scale, Height = menu.Scale },
            SettingsCanvas.Brass with { W = lit ? 0.9f : 0.45f });
        menu.Hits.Add(new(box, BeginFilter, Label: "Filter mods"));

        var textAt = box with { X = box.X + 8 * menu.Scale, Width = box.Width - 16 * menu.Scale };
        if (FilterFocused) DrawFilterEdit(textAt);
        else if (_filter.Text.Length > 0) canvas.Text(_filter.Text, textAt, 13, SettingsCanvas.LightInk, translate: false);
        else canvas.Text("Filter mods", textAt, 13, SettingsCanvas.MutedInk);
    }

    /// <summary>
    /// The focused mod filter box's text, caret and selection.
    /// </summary>
    void DrawFilterEdit(UiRect at)
    {
        menu.Controls.Add(new("mods-filter", "EditBox", at, _filter.Text, "", "", true,
            new Dictionary<string, string>
            {
                ["Focused"] = "true",
                ["TextAlign"] = "Left VCenter",
                ["FontPixels"] = ((int)MathF.Round(13 * menu.Scale)).ToString(CultureInfo.InvariantCulture),
                ["CaretIndex"] = _filter.Caret.ToString(CultureInfo.InvariantCulture),
                ["SelectionStart"] = _filter.SelectionStart.ToString(CultureInfo.InvariantCulture),
                ["SelectionLength"] = _filter.SelectionLength.ToString(CultureInfo.InvariantCulture),
            }, Vector4.Zero, SettingsCanvas.LightInk, Pickable: false));
    }

    /// <summary>
    /// Switches the mod list's filter mode and resets the scroll.
    /// </summary>
    void SetFilterMode(ModListFilter mode)
    {
        if (_filterMode == mode) return;

        _filterMode = mode;
        ResetScroll();
    }

    /// <summary>
    /// Toggles the mod list's ordering between load order and alphabetical by name.
    /// </summary>
    void ToggleSort()
    {
        _sort = _sort == ModListSort.LoadOrder ? ModListSort.Name : ModListSort.LoadOrder;
        ResetScroll();
    }

    /// <summary>
    /// Collapse all and Expand all at the title's right end, while a mod has settings.
    /// </summary>
    void DrawFoldButtons()
    {
        if (!Settings.AnyGroups) return;

        menu.Canvas.FoldButtons(CollapseAll, () => Settings.FoldAll(true));
    }

    /// <summary>
    /// Closes every mod's settings and shows the list from its top.
    /// </summary>
    void CollapseAll()
    {
        Settings.FoldAll(false);
        menu.ScrollOffset = menu.ScrollTarget = 0;
    }

    /// <summary>
    /// A line's height: its settings row's, or a mod row's.
    /// </summary>
    float LineHeight(ModsLine line) => line.Entry is { } entry ? menu.Canvas.EntryHeight(entry) : ModRowHeight;

    /// <summary>
    /// One line of the list at <paramref name="y"/>: a mod row, or a settings row under its mod.
    /// </summary>
    void DrawModsLine(IReadOnlyList<ModRow> rows, ModsLine line, float y)
    {
        if (line.Entry is not { } entry)
        {
            DrawModRow(rows[line.Row], line.Row, y);
            return;
        }

        // By key: every mod's group header is labelled Settings.
        menu.Motion.AnimationScope = entry.Raw ?? entry.Label;
        menu.Canvas.DrawEntry(entry, y);
    }

    /// <summary>
    /// A mod's row (or a separator): its name, issue count and version, lit when hovered or selected; a click selects it.
    /// </summary>
    void DrawModRow(ModRow row, int index, float y)
    {
        var at = menu.Canvas.R(212, y, 888, ModRowHeight);
        if (row.Separator)
        {
            menu.Canvas.Rect(at, SettingsCanvas.MainInk with { W = 0.06f }, menu.Viewport);
            menu.Canvas.Text(row.Name, menu.Canvas.R(224, y, 864, ModRowHeight), 12, SettingsCanvas.Brass, clip: menu.Viewport, translate: false);
            return;
        }

        bool hovered = at.Contains(menu.Pointer.X, menu.Pointer.Y) && menu.Viewport.Contains(menu.Pointer.X, menu.Pointer.Y);
        if (index == _modsSelected)
        {
            menu.Canvas.Rect(at, SettingsCanvas.Brass with { W = 0.18f }, menu.Viewport);
        }
        else if (hovered)
        {
            menu.Canvas.Rect(at, SettingsCanvas.MainInk with { W = 0.06f }, menu.Viewport);
        }

        menu.Canvas.Rect(menu.Canvas.R(212, y + ModRowHeight - 1, 888, 1), SettingsCanvas.MainInk with { W = 0.075f }, menu.Viewport);
        var nameInk = index == _modsSelected || hovered ? SettingsCanvas.LightInk : SettingsCanvas.MainInk;
        menu.Canvas.Text(row.Name, menu.Canvas.R(224, y, 520, ModRowHeight), 14, nameInk, clip: menu.Viewport, translate: false);
        if (row.Issues.Count > 0)
        {
            string issues = Loc.Plural("{1} issue", "{1} issues", row.Issues.Count, row.Issues.Count);
            var issuesAt = menu.Canvas.R(750, y, 200, ModRowHeight);
            menu.Canvas.Text(issues, issuesAt, 12, SettingsCanvas.Brass, "Right VCenter", menu.Viewport, translate: false);
        }

        if (row.Version is { } version)
        {
            string text = $"{Loc.Tr("Version")}: {version.ToString(CultureInfo.InvariantCulture)}";
            var versionAt = menu.Canvas.R(960, y, 128, ModRowHeight);
            menu.Canvas.Text(text, versionAt, 12, SettingsCanvas.MutedInk, "Right VCenter", menu.Viewport, translate: false);
        }

        if (at.Intersect(menu.Viewport).Height >= at.Height - 1)
        {
            menu.Hits.Add(new(at, () =>
            {
                _modsSelected = _modsSelected == index ? -1 : index;
                ModsDetailScroll = 0;
            }));
        }
    }

    /// <summary>
    /// The text area: the selected mod's text or the summary, scrolled, with its scroll bar.
    /// </summary>
    void DrawModsDetail(ModReport? report)
    {
        var area = ModsDetail;
        menu.Canvas.Rect(area, new(0.07f, 0.06f, 0.05f, 0.6f));
        string text = SelectedMod(report) is { } row ? ModText(row, ModIssueCap) : SummaryText(report);
        float height = menu.Canvas.TextHeight(text, 864, 12);
        float max = Math.Max(0, height + 12 - DetailHeight);
        ModsDetailScroll = Math.Clamp(ModsDetailScroll, 0, max);
        var textAt = menu.Canvas.R(224, DetailTop + 6 - ModsDetailScroll, 864, height + 2);
        menu.Canvas.Text(text, textAt, 12, SettingsCanvas.MainInk, "Left Top", area, wrap: true, translate: false);
        if (max <= 0) return;

        float thumb = Math.Max(24, DetailHeight * DetailHeight / (DetailHeight + max));
        menu.Canvas.Rect(menu.Canvas.R(1092, DetailTop, 3, DetailHeight), SettingsCanvas.MainInk with { W = 0.1f });
        menu.Canvas.Rect(menu.Canvas.R(1092, DetailTop + (DetailHeight - thumb) * ModsDetailScroll / max, 3, thumb), SettingsCanvas.Brass with { W = 0.75f });
    }

    /// <summary>
    /// The selected mod's row; null when none is.
    /// </summary>
    ModRow? SelectedMod(ModReport? report) =>
        report is not null && _modsSelected >= 0 && _modsSelected < report.Rows.Count ? report.Rows[_modsSelected] : null;

    /// <summary>
    /// A mod's name, version, author, description and issue lines (the first <paramref name="cap"/>, then "... N more").
    /// </summary>
    static string ModText(ModRow row, int cap)
    {
        var lines = new List<string> { row.Name };
        if (row.Version is { } version) lines.Add($"{Loc.Tr("Version")}: {version.ToString(CultureInfo.InvariantCulture)}");
        if (row.Author.Length > 0) lines.Add($"{Loc.Tr("Author")}: {row.Author}");
        if (row.Description.Length > 0) lines.Add(row.Description);
        if (row.Issues.Count > 0)
        {
            lines.Add("");
            lines.Add(Loc.Plural("{1} issue", "{1} issues", row.Issues.Count, row.Issues.Count) + ":");
            AddCapped(lines, row.Issues, cap);
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// The text with no mod selected: the summary, the list file when it is not data/mods.cfg, and the problems no mod owns.
    /// </summary>
    static string SummaryText(ModReport? report)
    {
        if (report is null) return Loc.Tr("No game data loaded.");

        var lines = new List<string> { report.Summary };
        if (report.CustomList is { } custom) lines.Add($"{Loc.Tr("Custom list")}: {custom}");
        lines.Add(Loc.Tr("Select a mod to see its author, description and problems."));
        if (report.OtherIssues.Count > 0)
        {
            lines.Add("");
            lines.Add(Loc.Tr("Problems in no mod's file:"));
            AddCapped(lines, report.OtherIssues, ModIssueCap);
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// The first <paramref name="cap"/> issue lines, then "... N more".
    /// </summary>
    static void AddCapped(List<string> lines, IReadOnlyList<string> issues, int cap)
    {
        lines.AddRange(issues.Take(cap));
        if (issues.Count > cap) lines.Add($"... {issues.Count - cap} more");
    }

    /// <summary>
    /// Copy: the selected mod in full, else the summary.
    /// </summary>
    void CopyMod()
    {
        string text = SelectedMod(menu.Player.TitleMenu.Mods) is { } row ? ModText(row, int.MaxValue) : SummaryText(menu.Player.TitleMenu.Mods);
        menu.Player.ClipboardWrite?.Invoke(text);
        menu.Notice = "Copied to the clipboard.";
    }

    /// <summary>
    /// Copy all problems: every mod with issues and its lines, then the ones no mod owns.
    /// </summary>
    void CopyProblems()
    {
        if (menu.Player.TitleMenu.Mods is not { Problems: > 0 } report)
        {
            menu.Notice = "There are no problems to copy.";
            return;
        }

        var lines = new List<string> { report.Summary };
        foreach (var row in report.Rows.Where(r => r.Issues.Count > 0))
        {
            lines.Add("");
            lines.Add(row.Name + (row.Version is { } v ? $" ({Loc.Tr("Version")}: {v.ToString(CultureInfo.InvariantCulture)})" : ""));
            lines.AddRange(row.Issues);
        }

        if (report.OtherIssues.Count > 0)
        {
            lines.Add("");
            lines.AddRange(report.OtherIssues);
        }

        menu.Player.ClipboardWrite?.Invoke(string.Join('\n', lines));
        menu.Notice = "Copied to the clipboard.";
    }

    #endregion
}
