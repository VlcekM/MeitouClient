namespace Meitou.Game.Host.Input;

/// <summary>What the player can ask for; keys and buttons are bound to these (<see cref="InputBindings"/>).</summary>
public enum InputAction
{
    MoveForward, MoveBack, MoveLeft, MoveRight,
    /// <summary>Free camera only: straight up and down.</summary>
    MoveUp, MoveDown,
    RotateLeft, RotateRight,
    PitchUp, PitchDown,
    ZoomIn, ZoomOut,
    /// <summary>Held, the mouse movement turns the camera.</summary>
    Orbit,
    /// <summary>The game view's mouse buttons: Select (the game's left button) picks characters and presses the interface; Command (the right button) gives orders and opens menus.</summary>
    Select, Command,
    /// <summary>The game's <c>toggle_fps_camera</c> (docs/formats/camera.md).</summary>
    ToggleFreeCamera,
    Pause, TimeFaster, TimeSlower,
    /// <summary>The game speed_1, speed_2, speed_3 actions: 1x, 2x, 5x.</summary>
    Speed1, Speed2, Speed3,
    Screenshot, ToggleSettings,
    /// <summary>The debug menu (the game's cheats), the frame statistics, the profiler chart (gpu, cpu, off).</summary>
    ToggleDebugMenu, ToggleStats, CycleProfiler,
    /// <summary>The small frame-rate counter at the top left.</summary>
    ToggleFps,
    /// <summary>The game's quicksave and quickload (F5 and F9, docs/game/ui-input.md): the host saves to and loads from its own "quicksave" slot.</summary>
    QuickSave, QuickLoad,
    /// <summary>The game's <c>floor_up</c> / <c>floor_down</c>: the viewed floor of the buildings with a player character inside (docs/formats/zones.md "Interior view").</summary>
    FloorUp, FloorDown,
    /// <summary>Held: highlight building signs in yellow.</summary>
    Highlight,
    /// <summary>The game's <c>toggle_bar</c> (F7, event 8): hides or shows the whole interface.</summary>
    ToggleInterface,
    /// <summary>The game's <c>toggle_inventory</c>, <c>toggle_stats</c>, <c>toggle_map</c>, <c>toggle_faction</c>, <c>toggle_research</c> and <c>toggle_crafting</c> (events 1..6): the windows.</summary>
    ToggleInventory, ToggleCharacterStats, ToggleMap, ToggleFaction, ToggleResearch, ToggleCrafting, ToggleHelp,
    /// <summary>The game's <c>change_squad</c> (0x23), <c>select_all</c> (0x22), <c>character_next</c> / <c>character_prev</c> (0x26 / 0x27) and <c>focus_char</c> (0x11).</summary>
    ChangeSquad, SelectAll, NextCharacter, PreviousCharacter, FocusCharacter,
    /// <summary>The game's <c>select_0</c> .. <c>select_9</c> (events 100..109, keys 1..9 and 0): the active squad's members by position.</summary>
    SelectMember1, SelectMember2, SelectMember3, SelectMember4, SelectMember5, SelectMember6, SelectMember7, SelectMember8, SelectMember9, SelectMember10,
    /// <summary>The game's <c>stop_movement</c> (0x16) and <c>toggle_build</c> (10).</summary>
    StopMovement, ToggleBuild,
    /// <summary>
    /// The orders panel's hotkeys (events 0x17..0x21, each the panel button's own handler): <c>toggle_block</c>, <c>toggle_hold</c>, <c>toggle_passive</c>, <c>toggle_jobs</c>,
    /// <c>toggle_ranged</c>, <c>toggle_sneak</c>, <c>toggle_taunt</c>, <c>cycle_run_speed</c>, <c>medic</c>, <c>rescue</c>, <c>prospect</c>.
    /// </summary>
    ToggleBlock, ToggleHold, TogglePassive, ToggleJobs, ToggleRanged, ToggleSneak, ToggleTaunt, CycleRunSpeed, Medic, Rescue, Prospect,
    /// <summary>Meitou's own: the selection sits down to rest, and the carriers put their bodies down (no binding in the game).</summary>
    Rest, PutDown,
    Quit,
}

/// <summary>The actions of one tick: for each, held, pressed or released this tick, plus the mouse movement and wheel.</summary>
public sealed class ActionState
{
    static readonly int Count = Enum.GetValues<InputAction>().Length;
    readonly bool[] held = new bool[Count];
    readonly bool[] pressed = new bool[Count];
    readonly bool[] released = new bool[Count];

    internal ActionState(TickInput input) => Input = input;

    public TickInput Input { get; }
    public System.Numerics.Vector2 MouseDelta => Input.MouseDelta;
    public float Wheel => Input.Wheel;

    public bool Held(InputAction a) => held[(int)a];
    /// <summary>Went down this tick: true in exactly one tick per press.</summary>
    public bool Pressed(InputAction a) => pressed[(int)a];
    /// <summary>Went up this tick (and no other key bound to it is still down).</summary>
    public bool Released(InputAction a) => released[(int)a];

    internal void Set(InputAction a, bool isHeld, bool isPressed, bool isReleased)
    {
        held[(int)a] = isHeld;
        pressed[(int)a] = isPressed;
        released[(int)a] = isReleased;
    }

    /// <summary>A state with nothing held, for ticks driven without a host.</summary>
    public static ActionState None { get; } = new(TickInput.None);
}

/// <summary>
/// Maps actions to keys and mouse buttons: each action has any number of bindings and counts as held while any is down. The
/// defaults are an engine choice except the time controls and <c>;</c> for the free camera, which are the game's (docs/game/ui-input.md: pause Space, speed_1..3 F2..F4) and the camera keys (arrows and WASD move, Q / E rotate, , and . tilt),
/// and the game view's hotkeys (the windows, squad keys and the orders on the numpad), which are the game's too.
/// A config file can override them through <see cref="Apply"/> with strings such as <c>"W,Up"</c> or <c>"Mouse:Middle"</c>.
/// </summary>
public sealed class InputBindings
{
    readonly Dictionary<InputAction, Binding[]> map = [];

    public InputBindings() => ResetToDefaults();

    public IReadOnlyList<Binding> Get(InputAction action) => map.TryGetValue(action, out var b) ? b : [];

    public void Set(InputAction action, params Binding[] bindings) => map[action] = bindings;

    public void ResetToDefaults()
    {
        map.Clear();
        // The game's camera keys (controls.cfg): the arrows move too; Q / E rotate; , and . tilt (camera_tilt+ steeper, camera_tilt- shallower).
        Set(InputAction.MoveForward, Key.W, Key.Up);
        Set(InputAction.MoveBack, Key.S, Key.Down);
        Set(InputAction.MoveLeft, Key.A, Key.Left);
        Set(InputAction.MoveRight, Key.D, Key.Right);
        Set(InputAction.MoveUp, Key.R);
        Set(InputAction.MoveDown, Key.F);
        Set(InputAction.RotateLeft, Key.Q);
        Set(InputAction.RotateRight, Key.E);
        Set(InputAction.PitchUp, Key.Period);
        Set(InputAction.PitchDown, Key.Comma);
        Set(InputAction.ZoomIn, Key.Home);   // the game's camera_zoom_in / out and floor_up / down keys (docs/game/ui-input.md)
        Set(InputAction.ZoomOut, Key.End);
        Set(InputAction.FloorUp, Key.PageUp);
        Set(InputAction.FloorDown, Key.PageDown);
        Set(InputAction.Highlight, Key.LeftAlt, Key.RightAlt);
        // The game's interface keys (docs/game/ui-input.md section 4). R and F are also the free camera's up and down keys, which win while it flies.
        Set(InputAction.ToggleInterface, Key.F7);
        Set(InputAction.ToggleInventory, Key.I);
        Set(InputAction.ToggleCharacterStats, Key.C);
        Set(InputAction.ToggleMap, Key.M);
        Set(InputAction.ToggleFaction);   // unbound in the game too
        Set(InputAction.ToggleResearch, Key.T);
        Set(InputAction.ToggleCrafting, Key.Y);
        Set(InputAction.ToggleHelp, Key.F1);   // toggle_help: the Tutorials window
        Set(InputAction.ChangeSquad, Key.Tab);
        Set(InputAction.SelectAll, Key.Grave);
        Set(InputAction.NextCharacter, Key.RightBracket);
        Set(InputAction.PreviousCharacter, Key.LeftBracket);
        Set(InputAction.FocusCharacter, Key.F);
        for (int i = 0; i < 10; i++) Set(InputAction.SelectMember1 + i, i < 9 ? Key.D1 + i : Key.D0);   // select_0..8 on 1..9, select_9 on 0
        Set(InputAction.StopMovement, Key.R);
        Set(InputAction.ToggleBuild, Key.B);
        // The orders on the numpad as in the game; Medic and Rescue keep Meitou's H and J besides, Jobs and Prospect are unbound in the game too.
        Set(InputAction.ToggleBlock, Key.Keypad0);
        Set(InputAction.ToggleHold, Key.Keypad1);
        Set(InputAction.TogglePassive, Key.Keypad2);
        Set(InputAction.ToggleJobs);
        Set(InputAction.ToggleRanged, Key.Keypad3);
        Set(InputAction.ToggleSneak, Key.Keypad4);
        Set(InputAction.ToggleTaunt, Key.Keypad5);
        Set(InputAction.CycleRunSpeed, Key.Keypad6);
        Set(InputAction.Medic, Key.Keypad7, Key.H);
        Set(InputAction.Rescue, Key.Keypad8, Key.J);
        Set(InputAction.Prospect);
        Set(InputAction.Rest, Key.L);
        Set(InputAction.PutDown, Key.K);
        Set(InputAction.Orbit, MouseButton.Middle, Key.LeftControl);
        Set(InputAction.Select, MouseButton.Left);   // the game's buttons, rebindable: orbit with the right button and select with the middle one, say
        Set(InputAction.Command, MouseButton.Right);
        Set(InputAction.ToggleFreeCamera, Key.Semicolon);
        Set(InputAction.Pause, Key.Space);
        Set(InputAction.TimeFaster);   // the game has no such keys (. and , are its camera tilt); F2..F4 set the speed
        Set(InputAction.TimeSlower);
        Set(InputAction.Speed1, Key.F2);
        Set(InputAction.Speed2, Key.F3);
        Set(InputAction.Speed3, Key.F4);
        Set(InputAction.Screenshot, Key.F8, Key.PrintScreen);   // the game's own screenshot keys (docs/game/ui-input.md)
        Set(InputAction.ToggleSettings);   // Tab is the game's change_squad (docs/game/ui-input.md); the settings open from the Esc menu's Options
        Set(InputAction.ToggleFps);   // Ctrl+F9 in the game (plain F9 is the game's quickload), so unbound here
        Set(InputAction.QuickSave, Key.F5);
        Set(InputAction.QuickLoad, Key.F9);
        Set(InputAction.ToggleDebugMenu, Key.F10);
        Set(InputAction.ToggleStats, Key.F11);
        Set(InputAction.CycleProfiler, Key.F12);
        Set(InputAction.Quit, Key.Escape);
    }

    /// <summary>
    /// Overrides bindings from <c>action name -> comma separated bindings</c> (names as in <see cref="InputAction"/>, keys as in
    /// <see cref="Key"/>, buttons as <c>Mouse:Right</c>; case does not matter; an empty value unbinds the action). Valid entries
    /// apply, the rest are skipped and described in the returned problems.
    /// </summary>
    public IReadOnlyList<string> Apply(IReadOnlyDictionary<string, string> overrides)
    {
        var problems = new List<string>();
        foreach (var (name, value) in overrides)
        {
            if (!Enum.TryParse<InputAction>(name.Trim(), true, out var action) || !Enum.IsDefined(action))
            {
                problems.Add($"Unknown action '{name}'");
                continue;
            }
            var list = new List<Binding>();
            bool ok = true;
            foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Binding.TryParse(token, out var b)) list.Add(b);
                else
                {
                    problems.Add($"Unknown binding '{token}' for {action}");
                    ok = false;
                }
            }
            if (ok) map[action] = [.. list];
        }
        return problems;
    }

    /// <summary>The bindings as strings in the form <see cref="Apply"/> reads (to write a config file).</summary>
    public Dictionary<string, string> ToDictionary() =>
        map.ToDictionary(p => p.Key.ToString(), p => string.Join(",", p.Value.Select(b => b.ToString())));

    /// <summary>Resolves one tick's input into actions.</summary>
    public ActionState Resolve(TickInput input)
    {
        var state = new ActionState(input);
        foreach (var action in Enum.GetValues<InputAction>())
        {
            bool held = false, pressed = false, released = false;
            foreach (var b in Get(action))
            {
                held |= input.IsDown(b);
                pressed |= input.WasPressed(b);
                released |= input.WasReleased(b);
            }
            state.Set(action, held, pressed, released && !held);
        }
        return state;
    }
}
