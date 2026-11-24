using System.Globalization;
using Meitou.Game.Rendering.Frame;
using Meitou.Modding;

namespace Meitou.Game.Settings;

/// <summary>
/// A line of the MODS list: the mod row <see cref="Row"/>, or a settings row (<see cref="Entry"/>).
/// </summary>
readonly record struct ModsLine(int Row, SettingsMenu.Entry? Entry);

/// <summary>
/// Which subset of mods the MODS page shows.
/// </summary>
enum ModListFilter
{
    /// <summary>
    /// Every loaded mod and code mod.
    /// </summary>
    All,
    /// <summary>
    /// Only mods that registered in-game settings.
    /// </summary>
    WithSettings,
    /// <summary>
    /// Only mods with reported data or load issues.
    /// </summary>
    WithIssues,
}

/// <summary>
/// How the MODS page orders its rows.
/// </summary>
enum ModListSort
{
    /// <summary>
    /// Load order from <c>mods.cfg</c>.
    /// </summary>
    LoadOrder,
    /// <summary>
    /// Alphabetical by mod name.
    /// </summary>
    Name,
}

/// <summary>
/// The settings code mods registered with <c>ModContext.AddSettings</c> (docs/modding.md "Settings"), on the MODS page: a group under its mod's row,
/// drawn with the page's own rows. A change sets the mod's property at once and is kept in UserConfig.ModSettings.
/// </summary>
sealed class ModSettingsRows(SettingsMenu menu)
{
    /// <summary>
    /// The separator of a setting's key: a unit separator, which no property name holds.
    /// </summary>
    const char KeySeparator = '\u001F';

    /// <summary>
    /// The separator of a sub-group's id: a record separator, which no property name holds, so no setting's key looks like one.
    /// </summary>
    const char SubGroupSeparator = '\u001E';

    /// <summary>
    /// Mod groups and sub-groups shown once already: one starts open the first time it is shown.
    /// </summary>
    readonly HashSet<string> _modGroupsSeen = [];

    /// <summary>
    /// Restore defaults: every mod's settings back to their defaults, and no saved value kept (also of mods not loaded now).
    /// </summary>
    internal void ResetModSettings()
    {
        foreach (var group in menu.ModSettings()) ResetModGroup(group);
        menu.Config.ModSettings.Clear();
    }

    /// <summary>
    /// Whether any loaded mod registered settings (the page's Collapse all and Expand all show then).
    /// </summary>
    internal bool AnyGroups => menu.ModSettings().Count > 0;

    /// <summary>
    /// Opens or closes every mod's settings group and sub-group (Expand all, Collapse all).
    /// </summary>
    internal void FoldAll(bool open)
    {
        foreach (var group in menu.ModSettings())
        {
            string id = GroupId(group);
            _modGroupsSeen.Add(id);

            if (open) menu.Grouping.OpenGroups.Add(id);
            else menu.Grouping.OpenGroups.Remove(id);

            foreach (var subGroup in group.Settings.Select(s => s.SubGroup).Where(name => name.Length > 0).Distinct())
            {
                string subGroupId = SubGroupId(group, subGroup);
                _modGroupsSeen.Add(subGroupId);

                if (open) menu.Grouping.OpenGroups.Add(subGroupId);
                else menu.Grouping.OpenGroups.Remove(subGroupId);
            }
        }

        menu.Focus = -1;
    }

    /// <summary>
    /// A mod's settings group's id among the open groups.
    /// </summary>
    internal static string GroupId(ModSettingsGroup group) => "mod:" + group.ModId;

    /// <summary>
    /// A sub-group's id among the open groups: the mod's group's id, a record separator and the sub-group's name.
    /// </summary>
    static string SubGroupId(ModSettingsGroup group, string subGroup) => $"{GroupId(group)}{SubGroupSeparator}{subGroup}";

    /// <summary>
    /// Each mod's row with its settings group under it, filtered by <paramref name="filterText"/> and <paramref name="filter"/> and ordered by
    /// <paramref name="sort"/>; a group whose mod has no row (not in the list file) comes last, under its name.
    /// </summary>
    internal List<ModsLine> ModsLines(IReadOnlyList<ModRow> rows, string filterText, ModListFilter filter, ModListSort sort,
        out int matchedMods, out int totalMods)
    {
        var groups = menu.ModSettings();
        var placed = new HashSet<ModSettingsGroup>();
        string[] words = filterText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool hideSeparators = words.Length > 0 || filter != ModListFilter.All || sort != ModListSort.LoadOrder;

        var items = new List<(int RowIndex, ModRow Row, List<ModSettingsGroup> Groups)>();
        totalMods = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Separator)
            {
                if (!hideSeparators) items.Add((i, rows[i], []));
                continue;
            }

            totalMods++;
            var modGroups = groups.Where(g => g.Name == rows[i].Name && placed.Add(g)).ToList();
            if (!MatchesFilter(rows[i], modGroups, words, filter)) continue;

            items.Add((i, rows[i], modGroups));
        }

        var standalone = new List<ModSettingsGroup>();
        foreach (var group in groups.Where(placed.Add))
        {
            totalMods++;
            if (filter == ModListFilter.WithIssues) continue;
            if (!MatchesGroupWords(group, "", words)) continue;

            standalone.Add(group);
        }

        if (sort == ModListSort.Name)
        {
            items.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Row.Name, b.Row.Name));
            standalone.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        }

        var lines = new List<ModsLine>();
        matchedMods = standalone.Count;
        foreach (var (rowIndex, row, modGroups) in items)
        {
            lines.Add(new(rowIndex, null));
            if (row.Separator) continue;

            matchedMods++;
            foreach (var group in modGroups) AddModGroup(lines, group, "Settings");
        }

        foreach (var group in standalone) AddModGroup(lines, group, group.Name);
        return lines;
    }

    /// <summary>
    /// Whether a mod row passes the selected filter mode and every word of the filter text (matching name, author, mod id or its settings).
    /// </summary>
    static bool MatchesFilter(ModRow row, List<ModSettingsGroup> groups, string[] words, ModListFilter filter)
    {
        if (filter == ModListFilter.WithSettings && groups.Count == 0) return false;
        if (filter == ModListFilter.WithIssues && row.Issues.Count == 0) return false;
        if (words.Length == 0) return true;

        string baseText = $"{row.Name}\n{row.Author}";
        if (AllWordsIn(baseText, words)) return true;

        return groups.Any(g => MatchesGroupWords(g, row.Author, words));
    }

    /// <summary>
    /// Whether every word of <paramref name="words"/> appears in <paramref name="group"/>'s name, id, author or visible settings.
    /// </summary>
    static bool MatchesGroupWords(ModSettingsGroup group, string author, string[] words)
    {
        if (words.Length == 0) return true;

        string header = $"{group.Name}\n{group.ModId}\n{author}";
        if (AllWordsIn(header, words)) return true;

        foreach (var setting in group.Settings)
        {
            if (!setting.Visible) continue;

            string text = $"{header}\n{setting.SubGroup}\n{setting.Label}\n{setting.Hint}";
            if (AllWordsIn(text, words)) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether every word in <paramref name="words"/> appears in <paramref name="text"/> (ignoring case).
    /// </summary>
    static bool AllWordsIn(string text, string[] words) =>
        words.All(word => text.Contains(word, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>
    /// A mod's settings group header under <paramref name="label"/>, its settings under it while open (a group starts open the first time): a
    /// setting its VisibleWhen hides is left out, and a sub-group's settings sit under its own header.
    /// </summary>
    void AddModGroup(List<ModsLine> lines, ModSettingsGroup group, string label)
    {
        string id = GroupId(group);
        if (_modGroupsSeen.Add(id)) menu.Grouping.OpenGroups.Add(id);
        bool open = menu.Grouping.OpenGroups.Contains(id);
        bool changed = group.Settings.Any(s => !s.IsDefault);
        Action? reset = changed ? () => ResetModGroup(group) : null;
        lines.Add(new(-1, new(label, "", Changed: changed, Raw: id, Open: open, GroupId: id, Reset: reset)));
        if (!open) return;

        var visible = group.Settings.Where(s => s.Visible).ToList();
        var subGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setting in visible)
        {
            if (setting.SubGroup.Length == 0)
            {
                lines.Add(new(-1, ModSettingEntry(setting, id, depth: 1)));
                continue;
            }

            if (subGroups.Add(setting.SubGroup)) AddModSubGroup(lines, group, setting.SubGroup);
        }
    }

    /// <summary>
    /// A sub-group's header under the mod's group, its visible settings under it while it is open (it starts open the first time).
    /// </summary>
    void AddModSubGroup(List<ModsLine> lines, ModSettingsGroup group, string subGroup)
    {
        string id = SubGroupId(group, subGroup);
        if (_modGroupsSeen.Add(id)) menu.Grouping.OpenGroups.Add(id);
        bool open = menu.Grouping.OpenGroups.Contains(id);
        var members = group.Settings.Where(s => s.SubGroup == subGroup).ToList();
        bool changed = members.Any(s => !s.IsDefault);
        Action? reset = changed ? () => ResetModSubGroup(members) : null;
        lines.Add(new(-1, new(group.Shown(subGroup), "", Changed: changed, Raw: id, Open: open, GroupId: id, Reset: reset, Depth: 1)));
        if (!open) return;

        foreach (var member in members.Where(s => s.Visible)) lines.Add(new(-1, ModSettingEntry(member, id, depth: 2)));
    }

    /// <summary>
    /// A setting's row: a switch, a slider, a free text, a button or a choice by its kind, its reset back to the default; its label, hint, button
    /// and choices in the mod's translation (the page's own lookup follows).
    /// </summary>
    internal SettingsMenu.Entry ModSettingEntry(ModSetting setting, string group, int depth)
    {
        string raw = $"{group}{KeySeparator}{setting.Key}";
        var shown = setting.Group;
        var entry = new SettingsMenu.Entry(shown.Shown(setting.Label), shown.Shown(setting.Hint), Changed: !setting.IsDefault, Raw: raw,
            Depth: depth, Reset: () => ChangeModSetting(setting, setting.Reset));
        return setting.Kind switch
        {
            ModSettingKind.Switch => entry with { Toggle = new Toggle(raw, () => setting.Get() is true, v => SetModSetting(setting, v)) },
            ModSettingKind.Slider => entry with { Slider = ModSlider(setting, raw) },
            ModSettingKind.Text => entry with { Text = new(raw, () => setting.Text, v => SetModSetting(setting, v), setting.MaxLength) },
            ModSettingKind.Action => entry with { Run = () => ChangeModSetting(setting, setting.Press), RunLabel = shown.Shown(setting.Button) },
            _ => entry with { Choice = new(() => ShownOptions(setting), () => setting.Choice, i => SetModSetting(setting, setting.Options[i])) },
        };
    }

    /// <summary>
    /// A list's choices as the page shows them: in the mod's translation (the saved value stays the choice's own).
    /// </summary>
    static IReadOnlyList<string> ShownOptions(ModSetting setting) =>
        setting.Group.Texts is { Any: true } ? [.. setting.Options.Select(setting.Group.Shown)] : setting.Options;

    /// <summary>
    /// A number setting's slider; its label is the row's key, so two mods' sliders of one name stay apart.
    /// </summary>
    Slider ModSlider(ModSetting setting, string raw)
    {
        string format = setting.Whole ? "0" : "0.##";
        float Get() => Convert.ToSingle(setting.Get(), CultureInfo.InvariantCulture);
        return new Slider(raw, (float)setting.Min, (float)setting.Max, Get, v => SetModSetting(setting, (double)v), format,
            Faithful: Convert.ToSingle(setting.Default, CultureInfo.InvariantCulture), Step: (float)setting.Step);
    }

    /// <summary>
    /// Every setting of the group back to its default.
    /// </summary>
    void ResetModGroup(ModSettingsGroup group)
    {
        foreach (var setting in group.Settings) ChangeModSetting(setting, setting.Reset);
    }

    /// <summary>
    /// A sub-group's settings back to their defaults.
    /// </summary>
    void ResetModSubGroup(IReadOnlyList<ModSetting> members)
    {
        foreach (var member in members) ChangeModSetting(member, member.Reset);
    }

    /// <summary>
    /// Sets the mod's property to <paramref name="value"/>.
    /// </summary>
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
            Content.CodeModFaults.Tell(setting.Group.ModId, e);
            menu.Notice = $"{setting.Group.Name}: {e.GetBaseException().Message}";
        }

        menu.Config.KeepModSetting(setting);
    }
}
