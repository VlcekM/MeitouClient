namespace Meitou.Game.Host.Input;

/// <summary>
/// What the player can ask for; keys and buttons are bound to these (<see cref="InputBindings"/>).
/// </summary>
public enum InputAction
{
    /// <summary>
    /// Moves the camera forward (default W and Up, the game's camera keys).
    /// </summary>
    MoveForward,
    /// <summary>
    /// Moves the camera back (default S and Down).
    /// </summary>
    MoveBack,
    /// <summary>
    /// Moves the camera left (default A and Left).
    /// </summary>
    MoveLeft,
    /// <summary>
    /// Moves the camera right (default D and Right).
    /// </summary>
    MoveRight,
    /// <summary>
    /// Free camera only: straight up (default R).
    /// </summary>
    MoveUp,
    /// <summary>
    /// Free camera only: straight down (default F).
    /// </summary>
    MoveDown,
    /// <summary>
    /// Turns the camera left (default Q, the game's).
    /// </summary>
    RotateLeft,
    /// <summary>
    /// Turns the camera right (default E, the game's).
    /// </summary>
    RotateRight,
    /// <summary>
    /// Tilts the view upward, the camera coming down (default Period, the game's <c>camera_tilt-</c>, docs/formats/camera.md).
    /// </summary>
    PitchUp,
    /// <summary>
    /// Tilts the view downward, the camera rising (default Comma, the game's <c>camera_tilt+</c>).
    /// </summary>
    PitchDown,
    /// <summary>
    /// Zooms the camera in (default Home, the game's <c>camera_zoom_in</c>).
    /// </summary>
    ZoomIn,
    /// <summary>
    /// Zooms the camera out (default End, the game's <c>camera_zoom_out</c>).
    /// </summary>
    ZoomOut,
    /// <summary>
    /// Held, the mouse movement turns the camera (default the middle button and the left Ctrl).
    /// </summary>
    Orbit,
    /// <summary>
    /// The game view's left button: picks characters and presses the interface.
    /// </summary>
    Select,
    /// <summary>
    /// The game view's right button: gives orders and opens menus.
    /// </summary>
    Command,
    /// <summary>
    /// The game's <c>toggle_fps_camera</c> (docs/formats/camera.md; default ;).
    /// </summary>
    ToggleFreeCamera,
    /// <summary>
    /// Pauses and resumes the game (default Space, the game's).
    /// </summary>
    Pause,
    /// <summary>
    /// Steps the game speed up; unbound by default (the game has no such key).
    /// </summary>
    TimeFaster,
    /// <summary>
    /// Steps the game speed down; unbound by default (the game has no such key).
    /// </summary>
    TimeSlower,
    /// <summary>
    /// The game's <c>speed_1</c> action: 1x (default F2).
    /// </summary>
    Speed1,
    /// <summary>
    /// The game's <c>speed_2</c> action: 2x (default F3).
    /// </summary>
    Speed2,
    /// <summary>
    /// The game's <c>speed_3</c> action: 5x (default F4).
    /// </summary>
    Speed3,
    /// <summary>
    /// Saves a screenshot (default F8 and Print Screen, the game's own keys).
    /// </summary>
    Screenshot,
    /// <summary>
    /// Opens or closes the settings; unbound by default (Tab is the game's change_squad; the settings open from the Esc menu's Options).
    /// </summary>
    ToggleSettings,
    /// <summary>
    /// The debug menu (the game's cheats; default F10).
    /// </summary>
    ToggleDebugMenu,
    /// <summary>
    /// The frame statistics (default F11).
    /// </summary>
    ToggleStats,
    /// <summary>
    /// Cycles the profiler chart: gpu, cpu, off (default F12).
    /// </summary>
    CycleProfiler,
    /// <summary>
    /// The small frame-rate counter at the top left; unbound by default (Shift+F9 in the game, whose plain F9 is the quickload).
    /// </summary>
    ToggleFps,
    /// <summary>
    /// The game's quicksave (F5, docs/game/ui-input.md): the host saves to its own "quicksave" slot.
    /// </summary>
    QuickSave,
    /// <summary>
    /// The game's quickload (F9, docs/game/ui-input.md): the host loads from its own "quicksave" slot.
    /// </summary>
    QuickLoad,
    /// <summary>
    /// The game's <c>floor_up</c> (default Page Up): the viewed floor of the buildings with a player character inside goes up (docs/formats/zones.md
    /// "Interior view").
    /// </summary>
    FloorUp,
    /// <summary>
    /// The game's <c>floor_down</c> (default Page Down): the viewed floor of the buildings with a player character inside goes down.
    /// </summary>
    FloorDown,
    /// <summary>
    /// Held: highlight building signs in yellow (default either Alt).
    /// </summary>
    Highlight,
    /// <summary>
    /// The game's <c>toggle_bar</c> (F7, event 8): hides or shows the whole interface.
    /// </summary>
    ToggleInterface,
    /// <summary>
    /// The game's <c>toggle_inventory</c> (event 1, default I): the inventory window.
    /// </summary>
    ToggleInventory,
    /// <summary>
    /// The game's <c>toggle_stats</c> (event 2, default C): the character stats window.
    /// </summary>
    ToggleCharacterStats,
    /// <summary>
    /// The game's <c>toggle_map</c> (event 3, default M): the map window.
    /// </summary>
    ToggleMap,
    /// <summary>
    /// The game's <c>toggle_faction</c> (event 4): the faction window; unbound by default, as in the game.
    /// </summary>
    ToggleFaction,
    /// <summary>
    /// The game's <c>toggle_research</c> (event 5, default T): the research window.
    /// </summary>
    ToggleResearch,
    /// <summary>
    /// The game's <c>toggle_crafting</c> (event 6, default Y): the crafting window.
    /// </summary>
    ToggleCrafting,
    /// <summary>
    /// The game's <c>toggle_help</c> (default F1): the Tutorials window.
    /// </summary>
    ToggleHelp,
    /// <summary>
    /// The game's <c>change_squad</c> (0x23, default Tab).
    /// </summary>
    ChangeSquad,
    /// <summary>
    /// The game's <c>select_all</c> (0x22, default `).
    /// </summary>
    SelectAll,
    /// <summary>
    /// The game's <c>character_next</c> (0x26, default ]).
    /// </summary>
    NextCharacter,
    /// <summary>
    /// The game's <c>character_prev</c> (0x27, default [).
    /// </summary>
    PreviousCharacter,
    /// <summary>
    /// The game's <c>focus_char</c> (0x11, default F).
    /// </summary>
    FocusCharacter,
    /// <summary>
    /// The game's <c>select_0</c> (event 100, key 1): the active squad's first member.
    /// </summary>
    SelectMember1,
    /// <summary>
    /// The game's <c>select_1</c> (event 101, key 2): the active squad's second member.
    /// </summary>
    SelectMember2,
    /// <summary>
    /// The game's <c>select_2</c> (event 102, key 3): the active squad's third member.
    /// </summary>
    SelectMember3,
    /// <summary>
    /// The game's <c>select_3</c> (event 103, key 4): the active squad's fourth member.
    /// </summary>
    SelectMember4,
    /// <summary>
    /// The game's <c>select_4</c> (event 104, key 5): the active squad's fifth member.
    /// </summary>
    SelectMember5,
    /// <summary>
    /// The game's <c>select_5</c> (event 105, key 6): the active squad's sixth member.
    /// </summary>
    SelectMember6,
    /// <summary>
    /// The game's <c>select_6</c> (event 106, key 7): the active squad's seventh member.
    /// </summary>
    SelectMember7,
    /// <summary>
    /// The game's <c>select_7</c> (event 107, key 8): the active squad's eighth member.
    /// </summary>
    SelectMember8,
    /// <summary>
    /// The game's <c>select_8</c> (event 108, key 9): the active squad's ninth member.
    /// </summary>
    SelectMember9,
    /// <summary>
    /// The game's <c>select_9</c> (event 109, key 0): the active squad's tenth member.
    /// </summary>
    SelectMember10,
    /// <summary>
    /// The game's <c>stop_movement</c> (0x16, default R).
    /// </summary>
    StopMovement,
    /// <summary>
    /// The game's <c>toggle_build</c> (10, default B).
    /// </summary>
    ToggleBuild,
    /// <summary>
    /// Build map: the game's <c>build_rotate_left</c> (default <c>,</c>), turns the held building left.
    /// </summary>
    BuildRotateLeft,
    /// <summary>
    /// Build map: the game's <c>build_rotate_right</c> (default <c>.</c>), turns the held building right.
    /// </summary>
    BuildRotateRight,
    /// <summary>
    /// Build map: the game's <c>build_move_up</c> (default <c>=</c>), raises the held building.
    /// </summary>
    BuildMoveUp,
    /// <summary>
    /// Build map: the game's <c>build_move_down</c> (default <c>-</c>), lowers the held building.
    /// </summary>
    BuildMoveDown,
    /// <summary>
    /// Build map: the game's <c>build_tilt_increase</c> (default <c>]</c> and NUM+), tilts the held building more to the slope.
    /// </summary>
    BuildTiltIncrease,
    /// <summary>
    /// Build map: the game's <c>build_tilt_decrease</c> (default <c>[</c> and NUM-), tilts the held building less.
    /// </summary>
    BuildTiltDecrease,
    /// <summary>
    /// Build map: the game's <c>build_undo</c> (event 0xb, default Backspace), drops the wall line being laid, else the last queued building.
    /// </summary>
    BuildUndo,
    /// <summary>
    /// Build map: the game's <c>build_apply</c> (event 0xc, default Space), queues the held building where it stands.
    /// </summary>
    BuildApply,
    /// <summary>
    /// Orders panel hotkey <c>toggle_block</c> (events 0x17..0x21 run the panel button's own handler; default numpad 0).
    /// </summary>
    ToggleBlock,
    /// <summary>
    /// Orders panel hotkey <c>toggle_hold</c> (default numpad 1).
    /// </summary>
    ToggleHold,
    /// <summary>
    /// Orders panel hotkey <c>toggle_passive</c> (default numpad 2).
    /// </summary>
    TogglePassive,
    /// <summary>
    /// Orders panel hotkey <c>toggle_jobs</c>; unbound by default, as in the game.
    /// </summary>
    ToggleJobs,
    /// <summary>
    /// Orders panel hotkey <c>toggle_ranged</c> (default numpad 3).
    /// </summary>
    ToggleRanged,
    /// <summary>
    /// Orders panel hotkey <c>toggle_sneak</c> (default numpad 4).
    /// </summary>
    ToggleSneak,
    /// <summary>
    /// Orders panel hotkey <c>toggle_taunt</c> (default numpad 5).
    /// </summary>
    ToggleTaunt,
    /// <summary>
    /// Orders panel hotkey <c>cycle_run_speed</c> (default numpad 6).
    /// </summary>
    CycleRunSpeed,
    /// <summary>
    /// Orders panel hotkey <c>medic</c> (default numpad 7 and Meitou's H).
    /// </summary>
    Medic,
    /// <summary>
    /// Orders panel hotkey <c>rescue</c> (default numpad 8 and Meitou's J).
    /// </summary>
    Rescue,
    /// <summary>
    /// Orders panel hotkey <c>prospect</c>; unbound by default, as in the game.
    /// </summary>
    Prospect,
    /// <summary>
    /// Meitou's own: the selection sits down to rest (default L; no binding in the game).
    /// </summary>
    Rest,
    /// <summary>
    /// Meitou's own: the carriers put their bodies down (default K; no binding in the game).
    /// </summary>
    PutDown,
    /// <summary>
    /// Meitou's own: photo mode on and off (default P; docs/viewer.md "Photo mode").
    /// </summary>
    TogglePhotoMode,
    /// <summary>
    /// Meitou's own: turns the held inventory item, or the one under the pointer, a quarter (default the middle button; only with the Item rotation
    /// setting, docs/game/ui-screens.md "Rotating an item").
    /// </summary>
    RotateItem,
    /// <summary>
    /// Escape's action (default Escape).
    /// </summary>
    Quit,
}

/// <summary>
/// The actions of one tick: for each, held, pressed or released this tick, plus the mouse movement and wheel.
/// </summary>
public sealed class ActionState
{
    /// <summary>
    /// How many actions there are.
    /// </summary>
    static readonly int Count = Enum.GetValues<InputAction>().Length;
    /// <summary>
    /// Each action is held at this tick: the game's by number, then the mods' (<see cref="Index"/>).
    /// </summary>
    readonly bool[] _held;
    /// <summary>
    /// Each action went down this tick.
    /// </summary>
    readonly bool[] _pressed;
    /// <summary>
    /// Each action went up this tick.
    /// </summary>
    readonly bool[] _released;

    /// <summary>
    /// An empty state for <paramref name="input"/>'s tick with room for <paramref name="modActions"/> mod actions (<see cref="InputBindings.Resolve"/>
    /// fills it).
    /// </summary>
    internal ActionState(TickInput input, int modActions = 0)
    {
        Input = input;
        _held = new bool[Count + modActions];
        _pressed = new bool[Count + modActions];
        _released = new bool[Count + modActions];
    }

    /// <summary>
    /// The raw input of the tick.
    /// </summary>
    public TickInput Input { get; }
    /// <summary>
    /// Mouse movement in pixels since the previous tick (x right, y down).
    /// </summary>
    public System.Numerics.Vector2 MouseDelta => Input.MouseDelta;
    /// <summary>
    /// Wheel notches since the previous tick, positive away from the user.
    /// </summary>
    public float Wheel => Input.Wheel;

    /// <summary>
    /// Whether any binding of the action is down.
    /// </summary>
    public bool Held(InputAction a) => Index(a) is var i && i < _held.Length && _held[i];
    /// <summary>
    /// Went down this tick: true in exactly one tick per press.
    /// </summary>
    public bool Pressed(InputAction a) => Index(a) is var i && i < _pressed.Length && _pressed[i];
    /// <summary>
    /// Went up this tick (and no other key bound to it is still down).
    /// </summary>
    public bool Released(InputAction a) => Index(a) is var i && i < _released.Length && _released[i];

    /// <summary>
    /// Sets an action's three states.
    /// </summary>
    internal void Set(InputAction a, bool isHeld, bool isPressed, bool isReleased)
    {
        int i = Index(a);
        _held[i] = isHeld;
        _pressed[i] = isPressed;
        _released[i] = isReleased;
    }

    /// <summary>
    /// An action's place in the arrays: a game action's number, a mod action's after the game's (<see cref="InputBindings.FirstModAction"/>).
    /// </summary>
    static int Index(InputAction a) => a < InputBindings.FirstModAction ? (int)a : Count + (a - InputBindings.FirstModAction);

    /// <summary>
    /// A state with nothing held, for ticks driven without a host.
    /// </summary>
    public static ActionState None { get; } = new(TickInput.None);
}

/// <summary>
/// Maps actions to keys and mouse buttons: each action has any number of bindings and counts as held while any is down. The defaults are an engine
/// choice except the time controls and <c>;</c> for the free camera, which are the game's (docs/game/ui-input.md: pause Space, speed_1..3 F2..F4) and
/// the camera keys (arrows and WASD move, Q / E rotate, , and . tilt), and the game view's hotkeys (the windows, squad keys and the orders on the
/// numpad) and the build map, which are the game's too. The install's <c>controls.cfg</c> replaces the defaults of the actions it names
/// (<see cref="UseGameControls"/>), and a config file overrides them through <see cref="Apply"/> with strings such as <c>"W,Up"</c> or
/// <c>"Mouse:Middle"</c>.
/// </summary>
public sealed class InputBindings
{
    /// <summary>
    /// The first code mods' action number (docs/modding.md "Key actions"): theirs follow the game's from here, in load order.
    /// </summary>
    public const InputAction FirstModAction = (InputAction)1000;

    /// <summary>
    /// Each action's bindings.
    /// </summary>
    readonly Dictionary<InputAction, Binding[]> _map = [];
    /// <summary>
    /// The install's <c>controls.cfg</c> bindings (<see cref="GameControls"/>), laid over the engine's defaults; empty until set.
    /// </summary>
    IReadOnlyDictionary<InputAction, Binding[]> _game = new Dictionary<InputAction, Binding[]>();
    /// <summary>
    /// The second keys Meitou gives the game's actions besides the game's own, kept when <c>controls.cfg</c> names the action and the key is free.
    /// </summary>
    static readonly (InputAction Action, Binding Alias)[] MeitouAliases =
        [(InputAction.Medic, Key.H), (InputAction.Rescue, Key.J), (InputAction.Highlight, Key.RightAlt)];
    /// <summary>
    /// The code mods' key actions and their defaults; null without any, so the bindings are the game's alone.
    /// </summary>
    ModKeyBindings? _mod;

    /// <summary>
    /// Starts with the default bindings.
    /// </summary>
    public InputBindings() => ResetToDefaults();

    /// <summary>
    /// An action's bindings; none when it has none.
    /// </summary>
    public IReadOnlyList<Binding> Get(InputAction action) => _map.TryGetValue(action, out var b) ? b : [];

    /// <summary>
    /// Replaces an action's bindings (none unbinds it).
    /// </summary>
    public void Set(InputAction action, params Binding[] bindings)
    {
        _map[action] = bindings;
        if (action >= FirstModAction) _mod?.Touch(action);
    }

    /// <summary>
    /// Every action: the game's in order, then the code mods' (<see cref="FirstModAction"/> and up).
    /// </summary>
    public IEnumerable<InputAction> Actions => _mod is null ? Enum.GetValues<InputAction>() : Enum.GetValues<InputAction>().Concat(_mod.Numbers);

    /// <summary>
    /// The code mod's key action numbered <paramref name="action"/>; null for a game action.
    /// </summary>
    public ModKeyAction? ModAction(InputAction action) => _mod?.Find(action);

    /// <summary>
    /// The code mods' key actions, in load order (none without mods).
    /// </summary>
    public IReadOnlyList<ModKeyAction> ModActions => _mod?.Actions ?? [];

    /// <summary>
    /// Whether the settings keep <paramref name="binding"/> for themselves (Escape cancels a rebinding, F6 is the cinema's, the modifiers modify): no
    /// action may take it there, and no code mod's action asks for it.
    /// </summary>
    public static bool Reserved(Binding binding) =>
        !binding.IsMouse
            && binding.Key is Key.Escape or Key.F6 or Key.LeftAlt or Key.RightAlt or Key.LeftControl or Key.RightControl or Key.LeftShift
                or Key.RightShift;

    /// <summary>
    /// Whether the game takes <paramref name="binding"/> before the bindings see it, or the Controls page keeps it: Insert opens the console, Delete
    /// and Backspace unbind a row there (Backspace also restarts a duel or battle). A code mod's action never gets one by default or from a saved
    /// entry. (The numpad divide key, the squad tour's, has no <see cref="Key"/>, so no binding can name it.)
    /// </summary>
    public static bool HostKey(Binding binding) => !binding.IsMouse && binding.Key is Key.Insert or Key.Delete or Key.Backspace;

    /// <summary>
    /// The action of <paramref name="except"/>'s key map (the build map or the general one, <see cref="IsBuildMap"/>) that holds
    /// <paramref name="binding"/>, other than <paramref name="except"/>; null when none. A key of the other map is no conflict, as on the Controls
    /// page.
    /// </summary>
    internal InputAction? Holder(Binding binding, InputAction except)
    {
        foreach (var (action, list) in _map)
            if (action != except && IsBuildMap(action) == IsBuildMap(except) && list.Contains(binding)) return action;
        return null;
    }

    /// <summary>
    /// Adds the code mods' key actions after the game's bindings are set (the install's and the player's): first the player's saved keys
    /// (<paramref name="saved"/>: mod id to action name to bindings, as <see cref="Apply"/> reads them), then, for the actions without a saved
    /// entry, the default keys no other action holds. Nothing happens without actions. Returns the skipped entries and the keys left out.
    /// </summary>
    public IReadOnlyList<string> UseModActions(IReadOnlyList<ModKeyAction> actions, IReadOnlyDictionary<string, Dictionary<string, string>>? saved)
    {
        if (actions.Count == 0) return [];

        _mod = new ModKeyBindings(actions);
        return _mod.Start(this, saved);
    }

    /// <summary>
    /// <paramref name="saved"/> with the running mods' key actions as they are now (<see cref="ModKeyBindings.Changes"/>: an action the player did
    /// not change keeps its entry as it was); the entries of mods not running stay. Returns <paramref name="saved"/> itself without mod actions, and
    /// null when nothing is left to keep.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>>? ModChanges(Dictionary<string, Dictionary<string, string>>? saved) =>
        _mod is null ? saved : _mod.Changes(this, saved);

    /// <summary>
    /// Whether a code mod's key action has <paramref name="key"/>.
    /// </summary>
    public bool IsModBound(Key key) => _mod is not null && _mod.Actions.Any(a => Get(a.Action).Contains(Binding.Of(key)));

    /// <summary>
    /// Takes the install's <c>controls.cfg</c> bindings as the defaults of the actions they name, and resets to the defaults.
    /// </summary>
    public void UseGameControls(IReadOnlyDictionary<InputAction, Binding[]> game)
    {
        _game = game;
        ResetToDefaults();
    }

    /// <summary>
    /// The default bindings: the engine's (<see cref="SetEngineDefaults"/>), then the install's <c>controls.cfg</c> over them.
    /// </summary>
    public void ResetToDefaults()
    {
        SetEngineDefaults();
        OverlayGameControls();
        foreach (var problem in _mod?.Reset(this) ?? []) Console.WriteLine($"mods      key action: {problem}");
    }

    /// <summary>
    /// Each action <c>controls.cfg</c> names takes its bindings there, as in the original (its loader clears an action's defaults at the
    /// action's first line, docs/game/ui-input.md), plus Meitou's alias of the action while no action of its map has that key.
    /// </summary>
    void OverlayGameControls()
    {
        foreach (var (action, bindings) in _game) _map[action] = bindings;

        foreach (var (action, alias) in MeitouAliases)
            if (_game.ContainsKey(action) && !SameMap(action).Any(a => Get(a).Contains(alias))) _map[action] = [.. _map[action], alias];
    }

    /// <summary>
    /// The actions of <paramref name="action"/>'s key map: the build map's while building, else the general map's. The two maps are separate in
    /// the original (docs/game/ui-input.md section 4), so a key can be in both.
    /// </summary>
    public static IEnumerable<InputAction> SameMap(InputAction action) =>
        Enum.GetValues<InputAction>().Where(a => IsBuildMap(a) == IsBuildMap(action));

    /// <summary>
    /// Whether <paramref name="action"/> is one of the build map's (the game's <c>build_</c> actions, checked first in build mode).
    /// </summary>
    public static bool IsBuildMap(InputAction action) => action is >= InputAction.BuildRotateLeft and <= InputAction.BuildApply;

    /// <summary>
    /// The engine's defaults (the game's keys where it has them, docs/game/ui-input.md, Meitou's otherwise).
    /// </summary>
    void SetEngineDefaults()
    {
        _map.Clear();
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
        // The build map (docs/game/ui-input.md section 4): its own key map, so its keys may also be the general map's (, . [ ] Space).
        Set(InputAction.BuildRotateLeft, Key.Comma);
        Set(InputAction.BuildRotateRight, Key.Period);
        Set(InputAction.BuildMoveUp, Key.Equal);
        Set(InputAction.BuildMoveDown, Key.Minus);
        Set(InputAction.BuildTiltIncrease, Key.RightBracket, Key.KeypadAdd);
        Set(InputAction.BuildTiltDecrease, Key.LeftBracket, Key.KeypadSubtract);
        Set(InputAction.BuildUndo, Key.Backspace);
        Set(InputAction.BuildApply, Key.Space);
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
        Set(InputAction.TogglePhotoMode, Key.P);
        Set(InputAction.RotateItem, MouseButton.Middle);   // over an item only: elsewhere the button is still Orbit's
        Set(InputAction.Orbit, MouseButton.Middle, Key.LeftControl);
        // the game's buttons, rebindable: orbit with the right button and select with the middle one, say
        Set(InputAction.Select, MouseButton.Left);
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
        Set(InputAction.ToggleFps);   // Shift+F9 in the game (plain F9 is the game's quickload), so unbound here
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
            if (ok) _map[action] = [.. list];
        }
        return problems;
    }

    /// <summary>
    /// Applies a saved config's bindings (<see cref="Apply"/>). A config holding (nearly) every action was written before
    /// <see cref="ToDictionary"/> kept only changes: its entries equal to the engine's defaults are skipped, so they do not hide <c>controls.cfg</c>.
    /// </summary>
    public IReadOnlyList<string> ApplySaved(IReadOnlyDictionary<string, string> saved)
    {
        if (saved.Count < Enum.GetValues<InputAction>().Length * 3 / 4) return Apply(saved);

        var engine = new InputBindings().ToStrings();
        var changed = saved
            .Where(p => !engine.TryGetValue(p.Key.Trim(), out var value) || !string.Equals(value, p.Value.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Key, p => p.Value);
        return Apply(changed);
    }

    /// <summary>
    /// The bindings that differ from the defaults, as strings in the form <see cref="Apply"/> reads (to write a config file); an unbound action
    /// is an empty string.
    /// </summary>
    public Dictionary<string, string> ToDictionary()
    {
        var defaults = new InputBindings();
        defaults.UseGameControls(_game);
        var defaultStrings = defaults.ToStrings();
        return ToStrings()
            .Where(p => !defaultStrings.TryGetValue(p.Key, out var value) || value != p.Value)
            .ToDictionary(p => p.Key, p => p.Value);
    }

    /// <summary>
    /// Every action's bindings as strings in the form <see cref="Apply"/> reads.
    /// </summary>
    Dictionary<string, string> ToStrings() =>
        _map
            .Where(p => p.Key < FirstModAction)
            .ToDictionary(p => p.Key.ToString(), p => Text(p.Value));

    /// <summary>
    /// Bindings as one string in the form <see cref="Apply"/> reads.
    /// </summary>
    internal static string Text(IEnumerable<Binding> bindings) => string.Join(",", bindings.Select(b => b.ToString()));

    /// <summary>
    /// Resolves one tick's input into actions.
    /// </summary>
    public ActionState Resolve(TickInput input)
    {
        var state = new ActionState(input, _mod?.Actions.Count ?? 0);
        foreach (var action in Enum.GetValues<InputAction>()) ResolveOne(state, input, action);
        if (_mod is null)
        {
            return state;
        }

        {
            foreach (var action in _mod.Actions) ResolveOne(state, input, action.Action);
        }
        return state;
    }

    /// <summary>
    /// One action's held, pressed and released from its bindings.
    /// </summary>
    void ResolveOne(ActionState state, TickInput input, InputAction action)
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
}