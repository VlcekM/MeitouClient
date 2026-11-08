using System.Numerics;
using System.Globalization;
using Meitou.Data.Ui;
using Meitou.Localisation;
using Meitou.Modding;

namespace Meitou.Game;

/// <summary>
/// The MODS page: the loaded mods in load order, each with its code's settings under it (SettingsMenu.ModSettings.cs), and the selected mod's author,
/// description and data problems.
/// </summary>
sealed partial class SettingsMenu
{
    const float ModRowHeight = 26;
    /// <summary>Most issue lines the text area shows; Copy takes them all.</summary>
    const int ModIssueCap = 200;

    int modsSelected = -1;
    float modsDetailScroll;

    /// <summary>The text area under the list (and its Copy buttons), in pixels.</summary>
    UiRect ModsDetail => R(212, 440, 888, 226);

    /// <summary>The summary line under the version text, left of the page buttons' divider; a click opens the MODS page.</summary>
    void DrawModsSummary()
    {
        if (player.Mods is not { } mods) return;
        var at = R(28, 646, 160, 16);
        var ink = at.Contains(pointer.X, pointer.Y) || page == ModsPage ? LightInk : mods.Problems > 0 ? Brass : MutedInk;
        Text(mods.Summary, at, 10, ink, translate: false);
        hits.Add(new(at, () => Navigate(ModsPage)));
    }

    void DrawMods()
    {
        var report = player.Mods;
        var rows = report?.Rows ?? [];
        var lines = ModsLines(rows);
        viewport = R(212, 98, 888, 296);
        maxScroll = Math.Max(0, lines.Sum(LineHeight) * scale - viewport.Height);
        AdvanceScroll();
        float y = 98 - scroll / scale;
        foreach (var line in lines)
        {
            float height = LineHeight(line);
            if (y + height >= 98 && y <= 394) DrawModsLine(rows, line, y);
            y += height;
        }

        Button("Copy", R(212, 402, 120, 28), CopyMod);
        Button("Copy all problems", R(340, 402, 200, 28), CopyProblems);
        DrawModsDetail(report);
    }

    float LineHeight(ModsLine line) => line.Entry is { } entry ? EntryHeight(entry) : ModRowHeight;

    void DrawModsLine(IReadOnlyList<ModRow> rows, ModsLine line, float y)
    {
        if (line.Entry is not { } entry)
        {
            DrawModRow(rows[line.Row], line.Row, y);
            return;
        }

        // By key: every mod's group header is labelled Settings.
        animationScope = entry.Raw ?? entry.Label;
        DrawEntry(entry, y);
    }

    void DrawModRow(ModRow row, int index, float y)
    {
        var at = R(212, y, 888, ModRowHeight);
        if (row.Separator)
        {
            Rect(at, MainInk with { W = 0.06f }, viewport);
            Text(row.Name, R(224, y, 864, ModRowHeight), 12, Brass, clip: viewport, translate: false);
            return;
        }

        bool hovered = at.Contains(pointer.X, pointer.Y) && viewport.Contains(pointer.X, pointer.Y);
        if (index == modsSelected) Rect(at, Brass with { W = 0.18f }, viewport);
        else if (hovered) Rect(at, MainInk with { W = 0.06f }, viewport);
        Rect(R(212, y + ModRowHeight - 1, 888, 1), MainInk with { W = 0.075f }, viewport);
        Text(row.Name, R(224, y, 520, ModRowHeight), 14, index == modsSelected || hovered ? LightInk : MainInk, clip: viewport, translate: false);
        if (row.Issues.Count > 0)
        {
            string issues = Loc.Plural("{1} issue", "{1} issues", row.Issues.Count, row.Issues.Count);
            Text(issues, R(750, y, 200, ModRowHeight), 12, Brass, "Right VCenter", viewport, translate: false);
        }

        if (row.Version is { } version)
        {
            string text = $"{Loc.Tr("Version")}: {version.ToString(CultureInfo.InvariantCulture)}";
            Text(text, R(960, y, 128, ModRowHeight), 12, MutedInk, "Right VCenter", viewport, translate: false);
        }

        if (at.Intersect(viewport).Height >= at.Height - 1)
            hits.Add(new(at, () => { modsSelected = modsSelected == index ? -1 : index; modsDetailScroll = 0; }));
    }

    void DrawModsDetail(ModReport? report)
    {
        var area = ModsDetail;
        Rect(area, new(0.07f, 0.06f, 0.05f, 0.6f));
        string text = SelectedMod(report) is { } row ? ModText(row, ModIssueCap) : SummaryText(report);
        float height = TextHeight(text, 864, 12);
        float max = Math.Max(0, height + 12 - 226);
        modsDetailScroll = Math.Clamp(modsDetailScroll, 0, max);
        Text(text, R(224, 446 - modsDetailScroll, 864, height + 2), 12, MainInk, "Left Top", area, wrap: true, translate: false);
        if (max <= 0) return;

        float thumb = Math.Max(24, 226 * 226 / (226 + max));
        Rect(R(1092, 440, 3, 226), MainInk with { W = 0.1f });
        Rect(R(1092, 440 + (226 - thumb) * modsDetailScroll / max, 3, thumb), Brass with { W = 0.75f });
    }

    ModRow? SelectedMod(ModReport? report) =>
        report is not null && modsSelected >= 0 && modsSelected < report.Rows.Count ? report.Rows[modsSelected] : null;

    /// <summary>A mod's name, version, author, description and issue lines (the first <paramref name="cap"/>, then "... N more").</summary>
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

    /// <summary>The text with no mod selected: the summary, the list file when it is not data/mods.cfg, and the problems no mod owns.</summary>
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

    static void AddCapped(List<string> lines, IReadOnlyList<string> issues, int cap)
    {
        lines.AddRange(issues.Take(cap));
        if (issues.Count > cap) lines.Add($"... {issues.Count - cap} more");
    }

    /// <summary>Copy: the selected mod in full, else the summary.</summary>
    void CopyMod()
    {
        string text = SelectedMod(player.Mods) is { } row ? ModText(row, int.MaxValue) : SummaryText(player.Mods);
        player.ClipboardWrite?.Invoke(text);
        notice = "Copied to the clipboard.";
    }

    /// <summary>Copy all problems: every mod with issues and its lines, then the ones no mod owns.</summary>
    void CopyProblems()
    {
        if (player.Mods is not { Problems: > 0 } report) { notice = "There are no problems to copy."; return; }

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
        player.ClipboardWrite?.Invoke(string.Join('\n', lines));
        notice = "Copied to the clipboard.";
    }
}
