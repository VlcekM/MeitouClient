using Meitou.Game.Rendering.Frame;
using Meitou.Settings;

namespace Meitou.Game.Settings;

/// <summary>A slider with named values in the catalogue (render scale's quality modes): a choice of them and Custom, the slider under it locked to the mode's value but under Custom.</summary>
sealed class SettingModes
{
    /// <summary>The sliders whose Custom was picked: the slider stays shown even when its value matches a mode.</summary>
    internal readonly HashSet<string> customModes = [];

    /// <summary>The rows replacing a slider row with modes: the mode choice, then the slider (locked but under Custom); any other row as it is.</summary>
    internal IEnumerable<SettingsMenu.Entry> ModeEntries(SettingsMenu.Entry row)
    {
        if (row is not { Slider: { } slider, Raw: { } key } || LauncherSchema.Find(key) is not { Modes: { Count: > 0 } modes } d)
        {
            yield return row;
            yield break;
        }
        string[] names = [.. modes.Select(m => m.Name), "Custom"];
        yield return row with { Slider = null, Choice = new(() => names, () => ModeIndex(key, slider, modes, d.Step), i => ChooseMode(key, slider, modes, i)) };
        yield return row with { Label = "Custom value", Depth = 1, Locked = ModeIndex(key, slider, modes, d.Step) < modes.Count };
    }

    int ModeIndex(string key, Slider slider, List<SettingMode> modes, float step)
    {
        if (customModes.Contains(key)) return modes.Count;
        float value = slider.Get();
        int mode = modes.FindIndex(m => m.Matches(value, step));
        return mode < 0 ? modes.Count : mode;
    }

    void ChooseMode(string key, Slider slider, List<SettingMode> modes, int index)
    {
        if (index >= modes.Count)
        {
            customModes.Add(key);
            return;
        }
        customModes.Remove(key);
        slider.Set(modes[index].Value);
    }
}
