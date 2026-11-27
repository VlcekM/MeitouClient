using Meitou.Modding;

namespace Meitou.Game.Settings;

/// <summary>
/// The code mods' settings that belong to the look (<c>[Setting(Look = true)]</c>, docs/modding.md "Settings"): a look preset holds their
/// values beside the game's (<c>mods</c> in its file: mod id, setting key, text), sets them when chosen, and counts one that differs as a change
/// from the preset. A preset is the defaults plus what it holds, as for the game's settings; values of a mod that is not running are kept.
/// <c>groups</c>: the running mods' settings, read each time (mods load after the menu may be made).
/// </summary>
sealed class LookModSettings(Func<IReadOnlyList<ModSettingsGroup>> groups)
{
    /// <summary>
    /// The running mods' look settings (buttons have no value).
    /// </summary>
    IEnumerable<ModSetting> Settings() => groups().SelectMany(g => g.Settings).Where(s => s.Look && s.Kind != ModSettingKind.Action);

    /// <summary>
    /// A look setting's key among the look's changes: the mod and the setting, apart from every game setting's key.
    /// </summary>
    static string KeyOf(ModSetting setting) => string.Concat("mod:", setting.Group.ModId, ":", setting.Key);

    /// <summary>
    /// What <paramref name="mods"/> holds for a setting; null when it leaves it at its default.
    /// </summary>
    static string? Held(Dictionary<string, Dictionary<string, string>> mods, ModSetting setting) =>
        mods.TryGetValue(setting.Group.ModId, out var values) && values.TryGetValue(setting.Key, out string? text) ? text : null;

    /// <summary>
    /// The look settings that differ from what a preset's <paramref name="mods"/> sets (its value, else the default), by key.
    /// </summary>
    internal IEnumerable<string> Changes(Dictionary<string, Dictionary<string, string>> mods) =>
        Settings().Where(s => Held(mods, s) is { } text ? !s.Holds(text) : !s.IsDefault).Select(KeyOf);

    /// <summary>
    /// Sets every look setting to what a preset's <paramref name="mods"/> holds, the others (and one whose held value is no longer valid) back
    /// to their default. A setter that throws is logged and the rest go on.
    /// </summary>
    internal void Apply(Dictionary<string, Dictionary<string, string>> mods)
    {
        foreach (var setting in Settings())
        {
            try
            {
                if (Held(mods, setting) is not { } text || !setting.Restore(text)) setting.Reset();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"mod       {setting.Group.ModId}: look setting '{setting.Key}' not applied: {e.GetType().Name}: {e.Message}");
                Content.CodeModFaults.Tell(setting.Group.ModId, e);
            }
        }
    }

    /// <summary>
    /// What a preset saved now holds for the mods: each running mod's look settings that are not at their default, and, from
    /// <paramref name="before"/> (the preset saved over or from), the values of mods that are not running.
    /// </summary>
    internal Dictionary<string, Dictionary<string, string>> Capture(Dictionary<string, Dictionary<string, string>> before)
    {
        var running = groups().Select(g => g.ModId).ToHashSet(StringComparer.Ordinal);
        var mods = before.Where(m => !running.Contains(m.Key)).ToDictionary(m => m.Key, m => new Dictionary<string, string>(m.Value));
        foreach (var setting in Settings().Where(s => !s.IsDefault))
        {
            if (!mods.TryGetValue(setting.Group.ModId, out var values)) mods[setting.Group.ModId] = values = [];
            values[setting.Key] = setting.Text;
        }

        return mods;
    }
}
