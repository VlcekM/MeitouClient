using System.Globalization;
using Meitou.Data.Ui;
using Meitou.Game.Rendering.Frame;
using Meitou.Modding;

namespace Meitou.Game.Settings;

/// <summary>
/// The MODS page: the loaded mods in load order, each with its code's settings under it (<see cref="ModSettingsRows"/>), and the selected mod's
/// author, description and data problems.
/// </summary>
sealed class ModsPage(SettingsMenu menu)
{
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
    /// The Copy buttons' top, and the list's top and bottom, in the menu's units.
    /// </summary>
    const float ButtonsTop = DetailTop - 38, ListTop = 98, ListBottom = ButtonsTop - 8;

    /// <summary>
    /// The selected mod's row; -1 for none.
    /// </summary>
    int _modsSelected = -1;

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
    /// The page: the scrolled list of mods and their settings, the Copy buttons and the text area.
    /// </summary>
    internal void DrawMods()
    {
        var report = menu.Player.TitleMenu.Mods;
        var rows = report?.Rows ?? [];
        var lines = Settings.ModsLines(rows);
        menu.Viewport = menu.Canvas.R(212, ListTop, 888, ListBottom - ListTop);
        menu.MaxScroll = Math.Max(0, lines.Sum(LineHeight) * menu.Scale - menu.Viewport.Height);
        menu.Motion.AdvanceScroll();
        float y = ListTop - menu.ScrollOffset / menu.Scale;
        foreach (var line in lines)
        {
            float height = LineHeight(line);
            if (y + height >= ListTop && y <= ListBottom) DrawModsLine(rows, line, y);
            y += height;
        }

        menu.Canvas.Button("Copy", menu.Canvas.R(212, ButtonsTop, 120, 28), CopyMod);
        menu.Canvas.Button("Copy all problems", menu.Canvas.R(340, ButtonsTop, 200, 28), CopyProblems);
        DrawFoldButtons();
        DrawModsDetail(report);
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
}
