namespace Meitou.Engine.Input;

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
    /// <summary>The game's <c>toggle_fps_camera</c> (docs/formats/camera.md).</summary>
    ToggleFreeCamera,
    Pause, TimeFaster, TimeSlower,
    /// <summary>The game speed_1, speed_2, speed_3 actions: 1x, 2x, 5x.</summary>
    Speed1, Speed2, Speed3,
    Screenshot, ToggleSettings,
    /// <summary>The debug overlays: the key list, the frame statistics, the profiler chart (gpu, cpu, off).</summary>
    ToggleKeys, ToggleStats, CycleProfiler,
    /// <summary>The small frame-rate counter at the top left.</summary>
    ToggleFps,
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
/// defaults are an engine choice except the time controls and <c>;</c> for the free camera, which are the game's (docs/game/ui-input.md: pause Space, speed_1..3 F2..F4; the keys . and , step through the speeds as well).
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
        Set(InputAction.MoveForward, Key.W);
        Set(InputAction.MoveBack, Key.S);
        Set(InputAction.MoveLeft, Key.A);
        Set(InputAction.MoveRight, Key.D);
        Set(InputAction.MoveUp, Key.R);
        Set(InputAction.MoveDown, Key.F);
        Set(InputAction.RotateLeft, Key.Q, Key.Left);
        Set(InputAction.RotateRight, Key.E, Key.Right);
        Set(InputAction.PitchUp, Key.Up);
        Set(InputAction.PitchDown, Key.Down);
        Set(InputAction.ZoomIn, Key.PageUp);
        Set(InputAction.ZoomOut, Key.PageDown);
        Set(InputAction.Orbit, MouseButton.Middle);   // right is the command button (move order)
        Set(InputAction.ToggleFreeCamera, Key.Semicolon);
        Set(InputAction.Pause, Key.Space);
        Set(InputAction.TimeFaster, Key.Period);
        Set(InputAction.TimeSlower, Key.Comma);
        Set(InputAction.Speed1, Key.F2);
        Set(InputAction.Speed2, Key.F3);
        Set(InputAction.Speed3, Key.F4);
        Set(InputAction.Screenshot, Key.F8, Key.PrintScreen);   // the game's own screenshot keys (docs/game/ui-input.md)
        Set(InputAction.ToggleSettings, Key.Tab);
        Set(InputAction.ToggleFps, Key.F9);
        Set(InputAction.ToggleKeys, Key.F10);
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
