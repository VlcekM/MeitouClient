namespace Meitou.Game.Host.Input;

/// <summary>
/// The install's <c>controls.cfg</c>, the original's key bindings (docs/game/ui-input.md, Binding encoding): a version line, then
/// <c>action=KeyName</c> lines, one per binding. Read only; Meitou keeps its own changes in its config.
/// </summary>
public static class GameControls
{
    /// <summary>
    /// The original's action names that have a Meitou action; the build, editor and developer actions have none (the build map is fixed).
    /// </summary>
    static readonly Dictionary<string, InputAction> Actions = new(StringComparer.Ordinal)
    {
        ["mouse_select"] = InputAction.Select,
        ["mouse_command"] = InputAction.Command,
        ["mouse_rotate"] = InputAction.Orbit,
        ["camera_forward"] = InputAction.MoveForward,
        ["camera_back"] = InputAction.MoveBack,
        ["camera_left"] = InputAction.MoveLeft,
        ["camera_right"] = InputAction.MoveRight,
        ["camera_rotate_left"] = InputAction.RotateLeft,
        ["camera_rotate_right"] = InputAction.RotateRight,
        ["camera_tilt+"] = InputAction.PitchDown,   // steeper: the camera rises (docs/formats/camera.md, Tilt)
        ["camera_tilt-"] = InputAction.PitchUp,
        ["camera_zoom_in"] = InputAction.ZoomIn,
        ["camera_zoom_out"] = InputAction.ZoomOut,
        ["highlight"] = InputAction.Highlight,
        ["toggle_fps_camera"] = InputAction.ToggleFreeCamera,
        ["floor_up"] = InputAction.FloorUp,
        ["floor_down"] = InputAction.FloorDown,
        ["speed_1"] = InputAction.Speed1,
        ["speed_2"] = InputAction.Speed2,
        ["speed_3"] = InputAction.Speed3,
        ["stop_movement"] = InputAction.StopMovement,
        ["pause"] = InputAction.Pause,
        ["toggle_block"] = InputAction.ToggleBlock,
        ["toggle_hold"] = InputAction.ToggleHold,
        ["toggle_passive"] = InputAction.TogglePassive,
        ["toggle_jobs"] = InputAction.ToggleJobs,
        ["toggle_ranged"] = InputAction.ToggleRanged,
        ["toggle_sneak"] = InputAction.ToggleSneak,
        ["toggle_taunt"] = InputAction.ToggleTaunt,
        ["cycle_run_speed"] = InputAction.CycleRunSpeed,
        ["medic"] = InputAction.Medic,
        ["rescue"] = InputAction.Rescue,
        ["prospect"] = InputAction.Prospect,
        ["screenshot"] = InputAction.Screenshot,
        ["toggle_bar"] = InputAction.ToggleInterface,
        ["toggle_inventory"] = InputAction.ToggleInventory,
        ["toggle_stats"] = InputAction.ToggleCharacterStats,
        ["toggle_map"] = InputAction.ToggleMap,
        ["toggle_faction"] = InputAction.ToggleFaction,
        ["toggle_research"] = InputAction.ToggleResearch,
        ["toggle_crafting"] = InputAction.ToggleCrafting,
        ["toggle_help"] = InputAction.ToggleHelp,
        ["quicksave"] = InputAction.QuickSave,
        ["quickload"] = InputAction.QuickLoad,
        ["toggle_build"] = InputAction.ToggleBuild,
        ["focus_char"] = InputAction.FocusCharacter,
        ["select_0"] = InputAction.SelectMember1,
        ["select_1"] = InputAction.SelectMember1 + 1,
        ["select_2"] = InputAction.SelectMember1 + 2,
        ["select_3"] = InputAction.SelectMember1 + 3,
        ["select_4"] = InputAction.SelectMember1 + 4,
        ["select_5"] = InputAction.SelectMember1 + 5,
        ["select_6"] = InputAction.SelectMember1 + 6,
        ["select_7"] = InputAction.SelectMember1 + 7,
        ["select_8"] = InputAction.SelectMember1 + 8,
        ["select_9"] = InputAction.SelectMember1 + 9,
        ["select_all"] = InputAction.SelectAll,
        ["change_squad"] = InputAction.ChangeSquad,
        ["character_next"] = InputAction.NextCharacter,
        ["character_prev"] = InputAction.PreviousCharacter,
    };

    /// <summary>
    /// The original's key names (the exe's 256-entry name table) of the keys Meitou has; letters, digits and F-keys are their own names.
    /// </summary>
    static readonly Dictionary<string, Binding> Keys = new(StringComparer.Ordinal)
    {
        ["Escape"] = Key.Escape,
        ["Backspace"] = Key.Backspace,
        ["Tab"] = Key.Tab,
        ["Return"] = Key.Enter,
        ["Space"] = Key.Space,
        ["Left Control"] = Key.LeftControl,
        ["Right Control"] = Key.RightControl,
        ["Left Shift"] = Key.LeftShift,
        ["Right shift"] = Key.RightShift,
        ["Left Alt"] = Key.LeftAlt,
        ["Right Alt"] = Key.RightAlt,
        ["GRAVE"] = Key.Grave,
        ["SYSRQ"] = Key.PrintScreen,
        ["Home"] = Key.Home,
        ["End"] = Key.End,
        ["PgUp"] = Key.PageUp,
        ["PgDn"] = Key.PageDown,
        ["Insert"] = Key.Insert,
        ["Delete"] = Key.Delete,
        ["Up"] = Key.Up,
        ["Down"] = Key.Down,
        ["Left"] = Key.Left,
        ["Right"] = Key.Right,
        ["-"] = Key.Minus,
        ["="] = Key.Equal,
        ["["] = Key.LeftBracket,
        ["]"] = Key.RightBracket,
        [";"] = Key.Semicolon,
        ["'"] = Key.Apostrophe,
        ["\\"] = Key.Backslash,
        [","] = Key.Comma,
        ["."] = Key.Period,
        ["/"] = Key.Slash,
        ["NUM+"] = Key.KeypadAdd,
        ["NUM-"] = Key.KeypadSubtract,
        ["Mouse1"] = MouseButton.Left,
        ["Mouse2"] = MouseButton.Right,
        ["Mouse3"] = MouseButton.Middle,
        ["Mouse4"] = MouseButton.Button4,
        ["Mouse5"] = MouseButton.Button5,
    };

    /// <summary>
    /// Reads <c>controls.cfg</c> in <paramref name="installRoot"/>: each action it names with the bindings it lists, in file order. Empty when
    /// the file is missing. Lines with an unknown key name are reported in <paramref name="problems"/> and skipped; the actions Meitou does not
    /// have and the bindings with a modifier (Meitou's have none) are skipped silently.
    /// </summary>
    public static Dictionary<InputAction, Binding[]> Read(string installRoot, out List<string> problems)
    {
        problems = [];
        var path = Path.Combine(installRoot, "controls.cfg");
        if (!File.Exists(path)) return [];

        // The first line is the file's version; the original treats a few camera keys specially below 1 (Observed), Meitou ignores it.
        var bound = new Dictionary<InputAction, List<Binding>>();
        foreach (var line in File.ReadLines(path).Skip(1))
            ReadLine(line, bound, problems);

        return bound.ToDictionary(p => p.Key, p => p.Value.ToArray());
    }

    /// <summary>
    /// One <c>action=KeyName</c> line into <paramref name="bound"/>. Split at the first <c>=</c>: the action names hold <c>+</c> and <c>-</c>
    /// and the key can be <c>=</c> itself (<c>build_move_up==</c>).
    /// </summary>
    static void ReadLine(string line, Dictionary<InputAction, List<Binding>> bound, List<string> problems)
    {
        int split = line.IndexOf('=');
        if (split <= 0) return;

        var name = line[..split].Trim();
        var keyName = line[(split + 1)..].Trim();
        if (!Actions.TryGetValue(name, out var action)) return;
        if (HasModifier(keyName)) return;

        if (!TryKey(keyName, out var binding))
        {
            problems.Add($"Unknown key '{keyName}' for {name}");
            return;
        }

        if (!bound.TryGetValue(action, out var list)) bound[action] = list = [];
        if (!list.Contains(binding)) list.Add(binding);
    }

    /// <summary>
    /// Whether the name carries the formatter's <c>Ctrl+</c>, <c>Alt+</c> or <c>Shift+</c> prefix (<c>NUM+</c> is a key of its own).
    /// </summary>
    static bool HasModifier(string keyName) =>
        keyName.Length > 1 && (keyName.StartsWith("Ctrl+", StringComparison.Ordinal)
            || keyName.StartsWith("Alt+", StringComparison.Ordinal)
            || keyName.StartsWith("Shift+", StringComparison.Ordinal));

    /// <summary>
    /// The binding of one of the original's key names: the table above, a letter, a digit, <c>F1</c>..<c>F12</c> or <c>NUM0</c>..<c>NUM9</c>.
    /// </summary>
    static bool TryKey(string keyName, out Binding binding)
    {
        if (Keys.TryGetValue(keyName, out binding)) return true;

        if (keyName.Length == 1 && keyName[0] is >= 'A' and <= 'Z')
            return Found(Key.A + (keyName[0] - 'A'), out binding);
        if (keyName.Length == 1 && keyName[0] is >= '0' and <= '9')
            return Found(Key.D0 + (keyName[0] - '0'), out binding);
        if (keyName.Length == 4 && keyName.StartsWith("NUM", StringComparison.Ordinal) && char.IsAsciiDigit(keyName[3]))
            return Found(Key.Keypad0 + (keyName[3] - '0'), out binding);
        if (keyName.Length is 2 or 3 && keyName[0] == 'F' && int.TryParse(keyName[1..], out int f) && f is >= 1 and <= 12)
            return Found(Key.F1 + (f - 1), out binding);

        return false;
    }

    /// <summary>
    /// <paramref name="key"/> as a binding, true.
    /// </summary>
    static bool Found(Key key, out Binding binding)
    {
        binding = key;
        return true;
    }
}
