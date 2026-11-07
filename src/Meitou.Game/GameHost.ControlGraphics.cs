using System.Globalization;
using System.Text.Json;
using Meitou.Rendering;

namespace Meitou.Game;

/// <summary>
/// The agent control channel's graphics layer (docs/agent-playtest.md): <c>gfx</c> lists the settings window's graphics switches and sliders with
/// their values; <c>gfx &lt;label&gt; &lt;value&gt;</c> sets one through the window's own setter, as a click or a drag would (so an A/B of an effect
/// needs no restart). Not saved: a label set here is left out of the settings file the run writes (<see cref="gfxRunOnly"/>).
/// </summary>
sealed partial class GameHost
{
    /// <summary>The settings window labels <c>gfx</c> changed this run, which the settings save leaves as they were saved.</summary>
    readonly HashSet<string> gfxRunOnly = new(StringComparer.Ordinal);

    void RegisterControlGraphicsCommands(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add)
    {
        add("gfx", "gfx [label [value]]", "graphics switches and sliders of the settings window: none lists them with their values (a substring filters); a label (exact, the saved key \"Meitou: X\", or a unique substring) and a value (on/off/1/0 for a switch, a number for a slider) sets one as the window would", (args, con) =>
        {
            if (settingsPanel is not { } panel) { con.Error("gfx: no settings window in this mode"); return; }
            if (args.Count > 2) { con.Error("usage: gfx [label [value]]"); return; }
            string? filter = args.Count >= 1 ? args[0] : null;
            if (args.Count < 2)
            {
                var toggles = panel.Toggles.Where(t => filter is null || t.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)).Select(t => new { label = t.Label, on = t.Get() }).ToList();
                var sliders = panel.Sliders.Where(s => filter is null || s.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)).Select(s => new { label = s.Label, value = s.Get(), min = s.Min, max = s.Max }).ToList();
                controlData = JsonSerializer.Serialize(new { toggles, sliders });
                foreach (var t in toggles) con.Print($"{(t.on ? "on " : "off")}  {t.label}");
                foreach (var row in sliders) con.Print(FormattableString.Invariant($"{row.value,10:0.###}  {row.label}  [{row.min} .. {row.max}]"));
                return;
            }
            string label = args[0].StartsWith(SettingsMenu.SwitchKeyPrefix, StringComparison.Ordinal) ? args[0][SettingsMenu.SwitchKeyPrefix.Length..] : args[0], value = args[1];
            var toggleHits = Matches(panel.Toggles, t => t.Label, label);
            var sliderHits = Matches(panel.Sliders, s => s.Label, label);
            // An exact label wins over substrings in the other list ("Light shafts" the switch, not its sliders).
            bool exactToggle = toggleHits.Any(t => t.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
            bool exactSlider = sliderHits.Any(s => s.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
            if (exactToggle && !exactSlider) sliderHits.Clear();
            if (exactSlider && !exactToggle) toggleHits.Clear();
            if (toggleHits.Count + sliderHits.Count != 1)
            {
                con.Error(toggleHits.Count + sliderHits.Count == 0 ? $"gfx: no switch or slider matches \"{label}\""
                    : $"gfx: \"{label}\" matches {string.Join(", ", toggleHits.Select(t => t.Label).Concat(sliderHits.Select(s => s.Label)))}");
                return;
            }
            if (toggleHits.Count == 1)
            {
                var t = toggleHits[0];
                bool? on = value.ToLowerInvariant() switch { "on" or "1" or "true" or "meitou" => true, "off" or "0" or "false" or "faithful" => false, _ => null };
                if (on is not { } v) { con.Error("gfx: a switch takes on or off"); return; }
                t.Set(v);
                gfxRunOnly.Add(t.Label);
                con.Print($"{t.Label}: {(t.Get() ? "on" : "off")}");
                controlData = JsonSerializer.Serialize(new { label = t.Label, on = t.Get() });
                return;
            }
            var s = sliderHits[0];
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) { con.Error("gfx: a slider takes a number"); return; }
            s.Set(Math.Clamp(f, s.Min, s.Max));
            gfxRunOnly.Add(s.Label);
            con.Print(FormattableString.Invariant($"{s.Label}: {s.Get():0.###}"));
            controlData = JsonSerializer.Serialize(new { label = s.Label, value = s.Get() });
        });

        add("debug-menu", "debug-menu open|close|key <Up|Down|Left|Right|Enter|Space|Escape|Backspace> [count]", "drive the F10 debug menu as its keys do (a key repeated count times); replies with the highlighted row and its value", (args, con) =>
        {
            if (args.Count == 0) { con.Error("usage: debug-menu open|close|key <name> [count]"); return; }
            switch (args[0].ToLowerInvariant())
            {
                case "open": debugMenuOpen = true; break;
                case "close": debugMenuOpen = false; break;
                case "key" when args.Count >= 2 && Enum.TryParse<Meitou.Engine.Input.Key>(args[1], true, out var key):
                    int count = args.Count >= 3 && int.TryParse(args[2], out int n) ? Math.Clamp(n, 1, 200) : 1;
                    if (!debugMenuOpen) { con.Error("debug-menu: open it first"); return; }
                    for (int i = 0; i < count; i++) DebugMenuKey(key);
                    break;
                default: con.Error("usage: debug-menu open|close|key <name> [count]"); return;
            }
            string label = DebugIsTool(debugRow) ? DebugTools[debugRow - DebugRowCount].Label : debugRow < DebugLabels.Length ? DebugLabels[debugRow] : $"mod row {debugRow}";
            string value = DebugIsTool(debugRow) ? DebugTools[debugRow - DebugRowCount].State() : "";
            con.Print($"{(debugMenuOpen ? "open" : "closed")}: {label} = {value}  {debugStatus}");
            controlData = JsonSerializer.Serialize(new { open = debugMenuOpen, row = debugRow, label, value, status = debugStatus });
        });

        // An exact label wins over substrings, so "Shadows" is not ambiguous with "Cloud shadows".
        static List<T> Matches<T>(IEnumerable<T> items, Func<T, string> labelOf, string label)
        {
            var exact = items.Where(i => labelOf(i).Equals(label, StringComparison.OrdinalIgnoreCase)).ToList();
            return exact.Count > 0 ? exact : items.Where(i => labelOf(i).Contains(label, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }
}
