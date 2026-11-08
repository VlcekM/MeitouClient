using Meitou.Game.Host;
using Meitou.Game.Rendering.Frame;
using Meitou.Modding;

namespace Meitou.Game;

/// <summary>
/// The settings code mods registered with <c>ModContext.AddSettings</c> (docs/modding.md "Settings"), on the MODS page: a group under its mod's row,
/// drawn with the page's own rows. A change sets the mod's property at once and is kept in <see cref="UserConfig.ModSettings"/>.
/// </summary>
sealed partial class SettingsMenu
{
    /// <summary>The registered settings; read each time the page is drawn, since mods load after the menu may be made.</summary>
    public Func<IReadOnlyList<ModSettingsGroup>> ModSettings { get; init; } = () => [];

    /// <summary>Mod groups shown once already: a group starts open the first time it is shown.</summary>
    readonly HashSet<string> modGroupsSeen = [];

    /// <summary>A line of the MODS list: the mod row <see cref="Row"/>, or a settings row (<see cref="Entry"/>).</summary>
    readonly record struct ModsLine(int Row, Entry? Entry);

    /// <summary>Each mod's row with its settings group under it; a group whose mod has no row (not in the list file) comes last, under its name.</summary>
    List<ModsLine> ModsLines(IReadOnlyList<ModRow> rows)
    {
        var groups = ModSettings();
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
        if (modGroupsSeen.Add(id)) openGroups.Add(id);
        bool open = openGroups.Contains(id);
        bool changed = group.Settings.Any(s => !s.IsDefault);
        Action? reset = changed ? () => ResetModGroup(group) : null;
        lines.Add(new(-1, new(label, "", Changed: changed, Raw: id, Open: open, GroupId: id, Reset: reset)));
        if (!open) return;

        foreach (var setting in group.Settings)
            lines.Add(new(-1, ModSettingEntry(setting, id)));
    }

    Entry ModSettingEntry(ModSetting setting, string group)
    {
        string raw = $"{group}/{setting.Key}";
        var entry = new Entry(setting.Label, setting.Hint, Changed: !setting.IsDefault, Raw: raw, Depth: 1,
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
    void ResetModSettings()
    {
        foreach (var group in ModSettings())
            ResetModGroup(group);
        config.ModSettings.Clear();
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
            notice = $"{setting.Group.Name}: {e.GetBaseException().Message}";
        }
        KeepModSetting(setting);
    }

    void KeepModSetting(ModSetting setting)
    {
        string mod = setting.Group.ModId;
        if (!config.ModSettings.TryGetValue(mod, out var saved)) config.ModSettings[mod] = saved = [];
        if (setting.IsDefault) saved.Remove(setting.Key);
        else saved[setting.Key] = setting.Text;
        if (saved.Count == 0) config.ModSettings.Remove(mod);
    }
}
