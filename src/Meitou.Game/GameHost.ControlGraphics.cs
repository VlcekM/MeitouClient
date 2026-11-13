using EngineKey = Meitou.Game.Host.Input.Key;
using Meitou.Game.DevTools;
using System.Globalization;
using System.Text.Json;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gi;
using Meitou.Game.Rendering.Impostors;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Particles;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Shadows;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;
using Meitou.Game.Rendering.Ui;
using Meitou.Game.Rendering.Upscalers;
using Meitou.Game.Rendering.Water;

namespace Meitou.Game;

/// <summary>
/// The agent control channel's graphics layer (docs/agent-playtest.md): <c>gfx</c> lists the settings window's graphics switches and sliders with
/// their values; <c>gfx &lt;label&gt; &lt;value&gt;</c> sets one through the window's own setter, as a click or a drag would (so an A/B of an effect
/// needs no restart). Not saved: a label set here is left out of the settings file the run writes (<see cref="GfxRunOnly"/>).
/// </summary>
sealed partial class GameHost
{
    /// <summary>
    /// The settings window labels <c>gfx</c> changed this run, which the settings save leaves as they were saved.
    /// </summary>
    internal readonly HashSet<string> GfxRunOnly = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers <c>gfx</c> and <c>debug-menu</c> with the control channel.
    /// </summary>
    internal void RegisterControlGraphicsCommands(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add)
    {
        add("gfx", "gfx [label [value]]", "graphics switches and sliders of the settings window: none lists them with their values (a substring "
            + "filters); a label (exact, the saved key \"Meitou: X\", or a unique substring) and a value (on/off/1/0 for a switch, a number for a "
            + "slider) sets one as the window would", ControlGfx);

        add("debug-menu", "debug-menu open|close|key <Up|Down|Left|Right|Enter|Space|Escape|Backspace> [count]", "drive the F10 debug menu as its "
            + "keys do (a key repeated count times); replies with the highlighted row and its value", ControlDebugMenu);
    }

    /// <summary>
    /// The <c>gfx</c> command (usage and help at its registration in <see cref="RegisterControlGraphicsCommands"/>).
    /// </summary>
    void ControlGfx(IReadOnlyList<string> args, DevConsole con)
    {
        if (GameLoop.Panel is not { } panel)
        {
            con.Error("gfx: no settings window in this mode");
            return;
        }

        if (args.Count > 2)
        {
            con.Error("usage: gfx [label [value]]");
            return;
        }

        string? filter = args.Count >= 1 ? args[0] : null;
        if (args.Count < 2)
        {
            GfxList(panel, filter, con);
            return;
        }

        string label = args[0].StartsWith(SettingsMenu.SwitchKeyPrefix, StringComparison.Ordinal)
            ? args[0][SettingsMenu.SwitchKeyPrefix.Length..]
            : args[0];
        string value = args[1];
        var toggleHits = GfxMatches(panel.Toggles, t => t.Label, label);
        var sliderHits = GfxMatches(panel.Sliders, s => s.Label, label);

        // An exact label wins over substrings in the other list ("Light shafts" the switch, not its sliders).
        bool exactToggle = toggleHits.Any(t => t.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
        bool exactSlider = sliderHits.Any(s => s.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
        if (exactToggle && !exactSlider) sliderHits.Clear();
        if (exactSlider && !exactToggle) toggleHits.Clear();
        if (toggleHits.Count + sliderHits.Count != 1)
        {
            con.Error(toggleHits.Count + sliderHits.Count == 0
                ? $"gfx: no switch or slider matches \"{label}\""
                : $"gfx: \"{label}\" matches {string.Join(", ", toggleHits.Select(t => t.Label).Concat(sliderHits.Select(s => s.Label)))}");
            return;
        }

        if (toggleHits.Count == 1) GfxSetToggle(toggleHits[0], value, con);
        else GfxSetSlider(sliderHits[0], value, con);
    }

    /// <summary>
    /// <c>gfx [filter]</c>: prints the switches and sliders whose label contains the filter (all without one) and hands them over as JSON.
    /// </summary>
    void GfxList(SettingsMenu panel, string? filter, DevConsole con)
    {
        var toggles = panel.Toggles
            .Where(t => filter is null || t.Label.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(t => new { label = t.Label, on = t.Get() })
            .ToList();
        var sliders = panel.Sliders
            .Where(s => filter is null || s.Label.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(s => new { label = s.Label, value = s.Get(), min = s.Min, max = s.Max })
            .ToList();
        AgentChannel.ControlData = JsonSerializer.Serialize(new { toggles, sliders });
        foreach (var t in toggles) con.Print($"{(t.on ? "on " : "off")}  {t.label}");
        foreach (var row in sliders) con.Print(FormattableString.Invariant($"{row.value,10:0.###}  {row.label}  [{row.min} .. {row.max}]"));
    }

    /// <summary>
    /// <c>gfx label value</c> for a switch: on/1/true/meitou or off/0/false/faithful, set through the window's setter and kept for this run only.
    /// </summary>
    void GfxSetToggle(Toggle t, string value, DevConsole con)
    {
        bool? on = value.ToLowerInvariant() switch
        {
            "on" or "1" or "true" or "meitou" => true,
            "off" or "0" or "false" or "faithful" => false,
            _ => null,
        };
        if (on is not { } v)
        {
            con.Error("gfx: a switch takes on or off");
            return;
        }

        t.Set(v);
        GfxRunOnly.Add(t.Label);
        con.Print($"{t.Label}: {(t.Get() ? "on" : "off")}");
        AgentChannel.ControlData = JsonSerializer.Serialize(new { label = t.Label, on = t.Get() });
    }

    /// <summary>
    /// <c>gfx label value</c> for a slider: the number clamped to its range, set through the window's setter and kept for this run only.
    /// </summary>
    void GfxSetSlider(Slider s, string value, DevConsole con)
    {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
        {
            con.Error("gfx: a slider takes a number");
            return;
        }

        s.Set(Math.Clamp(f, s.Min, s.Max));
        GfxRunOnly.Add(s.Label);
        con.Print(FormattableString.Invariant($"{s.Label}: {s.Get():0.###}"));
        AgentChannel.ControlData = JsonSerializer.Serialize(new { label = s.Label, value = s.Get() });
    }

    /// <summary>
    /// The items whose label is <paramref name="label"/>, else those whose label contains it: an exact label wins over substrings, so "Shadows" is
    /// not ambiguous with "Cloud shadows".
    /// </summary>
    static List<T> GfxMatches<T>(IEnumerable<T> items, Func<T, string> labelOf, string label)
    {
        var exact = items.Where(i => labelOf(i).Equals(label, StringComparison.OrdinalIgnoreCase)).ToList();
        return exact.Count > 0 ? exact : items.Where(i => labelOf(i).Contains(label, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// The <c>debug-menu</c> command (usage and help at its registration in <see cref="RegisterControlGraphicsCommands"/>).
    /// </summary>
    void ControlDebugMenu(IReadOnlyList<string> args, DevConsole con)
    {
        if (args.Count == 0)
        {
            con.Error("usage: debug-menu open|close|key <name> [count]");
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "open":
                DebugMenu.DebugMenuOpen = true;
                break;
            case "close":
                DebugMenu.DebugMenuOpen = false;
                break;
            case "key" when args.Count >= 2 && Enum.TryParse<EngineKey>(args[1], true, out var key):
                int count = args.Count >= 3 && int.TryParse(args[2], out int n) ? Math.Clamp(n, 1, 200) : 1;
                if (!DebugMenu.DebugMenuOpen)
                {
                    con.Error("debug-menu: open it first");
                    return;
                }

                for (int i = 0; i < count; i++) DebugMenu.DebugMenuKey(key);
                break;
            default:
                con.Error("usage: debug-menu open|close|key <name> [count]");
                return;
        }

        string label = DebugMenuRowLabel();
        string value = DebugMenu.DebugIsTool(DebugMenu.RowIndex) ? DebugMenu.DebugTools[DebugMenu.RowIndex - Modding.DebugRowCount].State() : "";
        con.Print($"{(DebugMenu.DebugMenuOpen ? "open" : "closed")}: {label} = {value}  {DebugMenu.DebugStatus}");
        var reply = new { open = DebugMenu.DebugMenuOpen, row = DebugMenu.RowIndex, label, value, status = DebugMenu.DebugStatus };
        AgentChannel.ControlData = JsonSerializer.Serialize(reply);
    }

    /// <summary>
    /// The label of the debug menu's highlighted row: a tool's, a built-in row's, or the mod row's number.
    /// </summary>
    string DebugMenuRowLabel() =>
        DebugMenu.DebugIsTool(DebugMenu.RowIndex)
            ? DebugMenu.DebugTools[DebugMenu.RowIndex - Modding.DebugRowCount].Label
            : DebugMenu.RowIndex < DebugMenu.DebugLabels.Length
                ? DebugMenu.DebugLabels[DebugMenu.RowIndex]
                : $"mod row {DebugMenu.RowIndex}";
}
