using System.Globalization;
using Meitou.Data.Ui;
using Meitou.Game.Rendering.Frame;
using Meitou.Modding;

namespace Meitou.Game.Settings;

/// <summary>
/// The MODS page: the loaded mods in load order, each with its code's settings under it, and the selected mod's author,
/// description and data problems.
/// </summary>
sealed class ModsPage(SettingsMenu menu)
{
    const float ModRowHeight = 26;
    /// <summary>Most issue lines the text area shows; Copy takes them all.</summary>
    const int ModIssueCap = 200;

    int modsSelected = -1;
    internal float modsDetailScroll;

    /// <summary>The text area under the list (and its Copy buttons), in pixels.</summary>
    internal UiRect ModsDetail => menu.canvas.R(212, 440, 888, 226);

    /// <summary>The summary line under the version text, left of the page buttons' divider; a click opens the MODS page.</summary>
    internal void DrawModsSummary()
    {
        if (menu.player.titleMenu.Mods is not { } mods) return;
        var at = menu.canvas.R(28, 646, 160, 16);
        bool lit = at.Contains(menu.pointer.X, menu.pointer.Y) || menu.page == SettingsMenu.ModsPageIndex;
        var ink = lit ? SettingsCanvas.LightInk : mods.Problems > 0 ? SettingsCanvas.Brass : SettingsCanvas.MutedInk;
        menu.canvas.Text(mods.Summary, at, 10, ink, translate: false);
        menu.hits.Add(new(at, () => menu.Navigate(SettingsMenu.ModsPageIndex)));
    }

    internal void DrawMods()
    {
        var report = menu.player.titleMenu.Mods;
        var rows = report?.Rows ?? [];
        var lines = ModsLines(rows);
        menu.viewport = menu.canvas.R(212, 98, 888, 296);
        menu.maxScroll = Math.Max(0, lines.Sum(LineHeight) * menu.scale - menu.viewport.Height);
        menu.motion.AdvanceScroll();
        float y = 98 - menu.scroll / menu.scale;
        foreach (var line in lines)
        {
            float height = LineHeight(line);
            if (y + height >= 98 && y <= 394) DrawModsLine(rows, line, y);
            y += height;
        }

        menu.canvas.Button("Copy", menu.canvas.R(212, 402, 120, 28), CopyMod);
        menu.canvas.Button("Copy all problems", menu.canvas.R(340, 402, 200, 28), CopyProblems);
        DrawModsDetail(report);
    }

    float LineHeight(ModsLine line) => line.Entry is { } entry ? menu.canvas.EntryHeight(entry) : ModRowHeight;

    void DrawModsLine(IReadOnlyList<ModRow> rows, ModsLine line, float y)
    {
        if (line.Entry is not { } entry)
        {
            DrawModRow(rows[line.Row], line.Row, y);
            return;
        }

        // By key: every mod's group header is labelled Settings.
        menu.motion.animationScope = entry.Raw ?? entry.Label;
        menu.canvas.DrawEntry(entry, y);
    }

    void DrawModRow(ModRow row, int index, float y)
    {
        var at = menu.canvas.R(212, y, 888, ModRowHeight);
        if (row.Separator)
        {
            menu.canvas.Rect(at, SettingsCanvas.MainInk with { W = 0.06f }, menu.viewport);
            menu.canvas.Text(row.Name, menu.canvas.R(224, y, 864, ModRowHeight), 12, SettingsCanvas.Brass, clip: menu.viewport, translate: false);
            return;
        }

        bool hovered = at.Contains(menu.pointer.X, menu.pointer.Y) && menu.viewport.Contains(menu.pointer.X, menu.pointer.Y);
        if (index == modsSelected) menu.canvas.Rect(at, SettingsCanvas.Brass with { W = 0.18f }, menu.viewport);
        else if (hovered) menu.canvas.Rect(at, SettingsCanvas.MainInk with { W = 0.06f }, menu.viewport);
        menu.canvas.Rect(menu.canvas.R(212, y + ModRowHeight - 1, 888, 1), SettingsCanvas.MainInk with { W = 0.075f }, menu.viewport);
        var nameInk = index == modsSelected || hovered ? SettingsCanvas.LightInk : SettingsCanvas.MainInk;
        menu.canvas.Text(row.Name, menu.canvas.R(224, y, 520, ModRowHeight), 14, nameInk, clip: menu.viewport, translate: false);
        if (row.Issues.Count > 0)
        {
            string issues = Loc.Plural("{1} issue", "{1} issues", row.Issues.Count, row.Issues.Count);
            var issuesAt = menu.canvas.R(750, y, 200, ModRowHeight);
            menu.canvas.Text(issues, issuesAt, 12, SettingsCanvas.Brass, "Right VCenter", menu.viewport, translate: false);
        }

        if (row.Version is { } version)
        {
            string text = $"{Loc.Tr("Version")}: {version.ToString(CultureInfo.InvariantCulture)}";
            var versionAt = menu.canvas.R(960, y, 128, ModRowHeight);
            menu.canvas.Text(text, versionAt, 12, SettingsCanvas.MutedInk, "Right VCenter", menu.viewport, translate: false);
        }

        if (at.Intersect(menu.viewport).Height >= at.Height - 1)
            menu.hits.Add(new(at, () => { modsSelected = modsSelected == index ? -1 : index; modsDetailScroll = 0; }));
    }

    void DrawModsDetail(ModReport? report)
    {
        var area = ModsDetail;
        menu.canvas.Rect(area, new(0.07f, 0.06f, 0.05f, 0.6f));
        string text = SelectedMod(report) is { } row ? ModText(row, ModIssueCap) : SummaryText(report);
        float height = menu.canvas.TextHeight(text, 864, 12);
        float max = Math.Max(0, height + 12 - 226);
        modsDetailScroll = Math.Clamp(modsDetailScroll, 0, max);
        var textAt = menu.canvas.R(224, 446 - modsDetailScroll, 864, height + 2);
        menu.canvas.Text(text, textAt, 12, SettingsCanvas.MainInk, "Left Top", area, wrap: true, translate: false);
        if (max <= 0) return;

        float thumb = Math.Max(24, 226 * 226 / (226 + max));
        menu.canvas.Rect(menu.canvas.R(1092, 440, 3, 226), SettingsCanvas.MainInk with { W = 0.1f });
        menu.canvas.Rect(menu.canvas.R(1092, 440 + (226 - thumb) * modsDetailScroll / max, 3, thumb), SettingsCanvas.Brass with { W = 0.75f });
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
        string text = SelectedMod(menu.player.titleMenu.Mods) is { } row ? ModText(row, int.MaxValue) : SummaryText(menu.player.titleMenu.Mods);
        menu.player.ClipboardWrite?.Invoke(text);
        menu.notice = "Copied to the clipboard.";
    }

    /// <summary>Copy all problems: every mod with issues and its lines, then the ones no mod owns.</summary>
    void CopyProblems()
    {
        if (menu.player.titleMenu.Mods is not { Problems: > 0 } report) { menu.notice = "There are no problems to copy."; return; }

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
        menu.player.ClipboardWrite?.Invoke(string.Join('\n', lines));
        menu.notice = "Copied to the clipboard.";
    }

    // The settings code mods registered with <c>ModContext.AddSettings</c> (docs/modding.md "Settings"), on the MODS page: a group under its mod's row,
    // drawn with the page's own rows. A change sets the mod's property at once and is kept in UserConfig.ModSettings.

    /// <summary>Mod groups shown once already: a group starts open the first time it is shown.</summary>
    readonly HashSet<string> modGroupsSeen = [];

    /// <summary>A line of the MODS list: the mod row <see cref="Row"/>, or a settings row (<see cref="Entry"/>).</summary>
    readonly record struct ModsLine(int Row, SettingsMenu.Entry? Entry);

    /// <summary>Each mod's row with its settings group under it; a group whose mod has no row (not in the list file) comes last, under its name.</summary>
    List<ModsLine> ModsLines(IReadOnlyList<ModRow> rows)
    {
        var groups = menu.ModSettings();
        var placed = new HashSet<ModSettingsGroup>();
        var lines = new List<ModsLine>();
        for (int i = 0; i < rows.Count; i++)
        {
            lines.Add(new(i, null));
            if (rows[i].Separator) continue;

            foreach (var group in groups.Where(g => g.Name == rows[i].Name && placed.Add(g)))
                AddModGroup(lines, group, "Settings");
        }

        foreach (var group in groups.Where(placed.Add))
            AddModGroup(lines, group, group.Name);
        return lines;
    }

    void AddModGroup(List<ModsLine> lines, ModSettingsGroup group, string label)
    {
        string id = "mod:" + group.ModId;
        if (modGroupsSeen.Add(id)) menu.grouping.openGroups.Add(id);
        bool open = menu.grouping.openGroups.Contains(id);
        bool changed = group.Settings.Any(s => !s.IsDefault);
        Action? reset = changed ? () => ResetModGroup(group) : null;
        lines.Add(new(-1, new(label, "", Changed: changed, Raw: id, Open: open, GroupId: id, Reset: reset)));
        if (!open) return;

        foreach (var setting in group.Settings)
            lines.Add(new(-1, ModSettingEntry(setting, id)));
    }

    SettingsMenu.Entry ModSettingEntry(ModSetting setting, string group)
    {
        string raw = $"{group}/{setting.Key}";
        var entry = new SettingsMenu.Entry(setting.Label, setting.Hint, Changed: !setting.IsDefault, Raw: raw, Depth: 1,
            Reset: () => ChangeModSetting(setting, setting.Reset));
        return setting.Kind switch
        {
            ModSettingKind.Switch => entry with { Toggle = new Toggle(raw, () => setting.Get() is true, v => SetModSetting(setting, v)) },
            ModSettingKind.Slider => entry with { Slider = ModSlider(setting, raw) },
            _ => entry with { Choice = new(() => setting.Options, () => setting.Choice, i => SetModSetting(setting, setting.Options[i])) },
        };
    }

    /// <summary>A number setting's slider; its label is the row's key, so two mods' sliders of one name stay apart.</summary>
    Slider ModSlider(ModSetting setting, string raw)
    {
        string format = setting.Whole ? "0" : "0.##";
        float Get() => System.Convert.ToSingle(setting.Get(), System.Globalization.CultureInfo.InvariantCulture);
        return new Slider(raw, (float)setting.Min, (float)setting.Max, Get, v => SetModSetting(setting, (double)v), format,
            Faithful: System.Convert.ToSingle(setting.Default, System.Globalization.CultureInfo.InvariantCulture), Step: (float)setting.Step);
    }

    void ResetModGroup(ModSettingsGroup group)
    {
        foreach (var setting in group.Settings)
            ChangeModSetting(setting, setting.Reset);
    }

    /// <summary>Restore defaults: every mod's settings back to their defaults, and no saved value kept (also of mods not loaded now).</summary>
    internal void ResetModSettings()
    {
        foreach (var group in menu.ModSettings())
            ResetModGroup(group);
        menu.config.ModSettings.Clear();
    }

    void SetModSetting(ModSetting setting, object value) => ChangeModSetting(setting, () => setting.Set(value));

    /// <summary>
    /// Runs a change of the mod's property and keeps the value it has then in the config (a default is not kept). The mod's setter and its
    /// <see cref="ModSettingsGroup.Changed"/> handlers are its own code: a throw is logged and shown as the page's notice.
    /// </summary>
    void ChangeModSetting(ModSetting setting, Action change)
    {
        try
        {
            change();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"mod       {setting.Group.ModId}: setting '{setting.Key}' failed: {e}");
            menu.notice = $"{setting.Group.Name}: {e.GetBaseException().Message}";
        }
        KeepModSetting(setting);
    }

    void KeepModSetting(ModSetting setting)
    {
        string mod = setting.Group.ModId;
        if (!menu.config.ModSettings.TryGetValue(mod, out var saved)) menu.config.ModSettings[mod] = saved = [];
        if (setting.IsDefault) saved.Remove(setting.Key);
        else saved[setting.Key] = setting.Text;
        if (saved.Count == 0) menu.config.ModSettings.Remove(mod);
    }
}
