using Meitou.Modding;
using Meitou.Rendering;

namespace Meitou.Game;

/// <summary>
/// The Mod settings page: the settings code mods registered with <c>ModContext.AddSettings</c> (docs/modding.md "Settings"), one group per mod in
/// load order, drawn with the page's own rows. A change sets the mod's property at once and is kept in <see cref="UserConfig.ModSettings"/>.
/// </summary>
sealed partial class SettingsMenu
{
    /// <summary>The registered settings; read each time the page is drawn, since mods load after the menu may be made.</summary>
    public Func<IReadOnlyList<ModSettingsGroup>> ModSettings { get; init; } = () => [];

    /// <summary>Mod groups shown once already: a group starts open the first time it is shown.</summary>
    readonly HashSet<string> modGroupsSeen = [];

    List<Entry> ModSettingsEntries()
    {
        var groups = ModSettings();
        if (groups.Count == 0)
            return [new("No mod settings", "A code mod's settings appear here when it registers them (docs/modding.md, \"Settings\").")];

        var result = new List<Entry>();
        foreach (var group in groups)
            AddModGroup(result, group);
        return result;
    }

    void AddModGroup(List<Entry> result, ModSettingsGroup group)
    {
        string id = "mod:" + group.ModId;
        if (modGroupsSeen.Add(id)) openGroups.Add(id);
        bool open = openGroups.Contains(id);
        bool changed = group.Settings.Any(s => !s.IsDefault);
        Action? reset = changed ? () => ResetModGroup(group) : null;
        result.Add(new(group.Name, "", Changed: changed, Raw: id, Open: open, GroupId: id, Reset: reset));
        if (!open) return;

        foreach (var setting in group.Settings)
            result.Add(ModSettingEntry(setting, id));
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
