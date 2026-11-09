namespace Meitou.Game.Host.Input;

/// <summary>
/// Keyboard keys as the engine names them (the host maps its windowing library's keys to these).
/// </summary>
public enum Key
{
    /// <summary>
    /// The letter key A.
    /// </summary>
    A,
    /// <summary>
    /// The letter key B.
    /// </summary>
    B,
    /// <summary>
    /// The letter key C.
    /// </summary>
    C,
    /// <summary>
    /// The letter key D.
    /// </summary>
    D,
    /// <summary>
    /// The letter key E.
    /// </summary>
    E,
    /// <summary>
    /// The letter key F.
    /// </summary>
    F,
    /// <summary>
    /// The letter key G.
    /// </summary>
    G,
    /// <summary>
    /// The letter key H.
    /// </summary>
    H,
    /// <summary>
    /// The letter key I.
    /// </summary>
    I,
    /// <summary>
    /// The letter key J.
    /// </summary>
    J,
    /// <summary>
    /// The letter key K.
    /// </summary>
    K,
    /// <summary>
    /// The letter key L.
    /// </summary>
    L,
    /// <summary>
    /// The letter key M.
    /// </summary>
    M,
    /// <summary>
    /// The letter key N.
    /// </summary>
    N,
    /// <summary>
    /// The letter key O.
    /// </summary>
    O,
    /// <summary>
    /// The letter key P.
    /// </summary>
    P,
    /// <summary>
    /// The letter key Q.
    /// </summary>
    Q,
    /// <summary>
    /// The letter key R.
    /// </summary>
    R,
    /// <summary>
    /// The letter key S.
    /// </summary>
    S,
    /// <summary>
    /// The letter key T.
    /// </summary>
    T,
    /// <summary>
    /// The letter key U.
    /// </summary>
    U,
    /// <summary>
    /// The letter key V.
    /// </summary>
    V,
    /// <summary>
    /// The letter key W.
    /// </summary>
    W,
    /// <summary>
    /// The letter key X.
    /// </summary>
    X,
    /// <summary>
    /// The letter key Y.
    /// </summary>
    Y,
    /// <summary>
    /// The letter key Z.
    /// </summary>
    Z,
    /// <summary>
    /// The digit 0 on the main keyboard row (not the keypad).
    /// </summary>
    D0,
    /// <summary>
    /// The digit 1 on the main keyboard row (not the keypad).
    /// </summary>
    D1,
    /// <summary>
    /// The digit 2 on the main keyboard row (not the keypad).
    /// </summary>
    D2,
    /// <summary>
    /// The digit 3 on the main keyboard row (not the keypad).
    /// </summary>
    D3,
    /// <summary>
    /// The digit 4 on the main keyboard row (not the keypad).
    /// </summary>
    D4,
    /// <summary>
    /// The digit 5 on the main keyboard row (not the keypad).
    /// </summary>
    D5,
    /// <summary>
    /// The digit 6 on the main keyboard row (not the keypad).
    /// </summary>
    D6,
    /// <summary>
    /// The digit 7 on the main keyboard row (not the keypad).
    /// </summary>
    D7,
    /// <summary>
    /// The digit 8 on the main keyboard row (not the keypad).
    /// </summary>
    D8,
    /// <summary>
    /// The digit 9 on the main keyboard row (not the keypad).
    /// </summary>
    D9,
    /// <summary>
    /// The function key F1.
    /// </summary>
    F1,
    /// <summary>
    /// The function key F2.
    /// </summary>
    F2,
    /// <summary>
    /// The function key F3.
    /// </summary>
    F3,
    /// <summary>
    /// The function key F4.
    /// </summary>
    F4,
    /// <summary>
    /// The function key F5.
    /// </summary>
    F5,
    /// <summary>
    /// The function key F6.
    /// </summary>
    F6,
    /// <summary>
    /// The function key F7.
    /// </summary>
    F7,
    /// <summary>
    /// The function key F8.
    /// </summary>
    F8,
    /// <summary>
    /// The function key F9.
    /// </summary>
    F9,
    /// <summary>
    /// The function key F10.
    /// </summary>
    F10,
    /// <summary>
    /// The function key F11.
    /// </summary>
    F11,
    /// <summary>
    /// The function key F12.
    /// </summary>
    F12,
    /// <summary>
    /// The left arrow key.
    /// </summary>
    Left,
    /// <summary>
    /// The right arrow key.
    /// </summary>
    Right,
    /// <summary>
    /// The up arrow key.
    /// </summary>
    Up,
    /// <summary>
    /// The down arrow key.
    /// </summary>
    Down,
    /// <summary>
    /// The space bar.
    /// </summary>
    Space,
    /// <summary>
    /// Enter (Return).
    /// </summary>
    Enter,
    /// <summary>
    /// Escape.
    /// </summary>
    Escape,
    /// <summary>
    /// Tab.
    /// </summary>
    Tab,
    /// <summary>
    /// Backspace.
    /// </summary>
    Backspace,
    /// <summary>
    /// Page Up.
    /// </summary>
    PageUp,
    /// <summary>
    /// Page Down.
    /// </summary>
    PageDown,
    /// <summary>
    /// Home.
    /// </summary>
    Home,
    /// <summary>
    /// End.
    /// </summary>
    End,
    /// <summary>
    /// Insert.
    /// </summary>
    Insert,
    /// <summary>
    /// Delete.
    /// </summary>
    Delete,
    /// <summary>
    /// The left Shift.
    /// </summary>
    LeftShift,
    /// <summary>
    /// The right Shift.
    /// </summary>
    RightShift,
    /// <summary>
    /// The left Ctrl.
    /// </summary>
    LeftControl,
    /// <summary>
    /// The right Ctrl.
    /// </summary>
    RightControl,
    /// <summary>
    /// The left Alt.
    /// </summary>
    LeftAlt,
    /// <summary>
    /// The right Alt.
    /// </summary>
    RightAlt,
    /// <summary>
    /// The ; key (US layout).
    /// </summary>
    Semicolon,
    /// <summary>
    /// The , key.
    /// </summary>
    Comma,
    /// <summary>
    /// The . key.
    /// </summary>
    Period,
    /// <summary>
    /// The - key on the main row.
    /// </summary>
    Minus,
    /// <summary>
    /// The = key (US layout).
    /// </summary>
    Equal,
    /// <summary>
    /// The / key (US layout).
    /// </summary>
    Slash,
    /// <summary>
    /// The \ key (US layout).
    /// </summary>
    Backslash,
    /// <summary>
    /// The ` key left of 1 (US layout).
    /// </summary>
    Grave,
    /// <summary>
    /// The [ key (US layout).
    /// </summary>
    LeftBracket,
    /// <summary>
    /// The ] key (US layout).
    /// </summary>
    RightBracket,
    /// <summary>
    /// The ' key (US layout).
    /// </summary>
    Apostrophe,
    /// <summary>
    /// Print Screen (SYSRQ in Kenshi's key names).
    /// </summary>
    PrintScreen,
    /// <summary>
    /// The numeric keypad's 0 (NUM0 in Kenshi's key names: the orders hotkeys, docs/game/ui-input.md section 4).
    /// </summary>
    Keypad0,
    /// <summary>
    /// The numeric keypad's 1 (NUM1 in Kenshi's key names: the orders hotkeys, docs/game/ui-input.md section 4).
    /// </summary>
    Keypad1,
    /// <summary>
    /// The numeric keypad's 2 (NUM2 in Kenshi's key names: the orders hotkeys, docs/game/ui-input.md section 4).
    /// </summary>
    Keypad2,
    /// <summary>
    /// The numeric keypad's 3 (NUM3 in Kenshi's key names: the orders hotkeys, docs/game/ui-input.md section 4).
    /// </summary>
    Keypad3,
    /// <summary>
    /// The numeric keypad's 4 (NUM4 in Kenshi's key names: the orders hotkeys, docs/game/ui-input.md section 4).
    /// </summary>
    Keypad4,
    /// <summary>
    /// The numeric keypad's 5 (NUM5 in Kenshi's key names: the orders hotkeys, docs/game/ui-input.md section 4).
    /// </summary>
    Keypad5,
    /// <summary>
    /// The numeric keypad's 6 (NUM6 in Kenshi's key names: the orders hotkeys, docs/game/ui-input.md section 4).
    /// </summary>
    Keypad6,
    /// <summary>
    /// The numeric keypad's 7 (NUM7 in Kenshi's key names: the orders hotkeys, docs/game/ui-input.md section 4).
    /// </summary>
    Keypad7,
    /// <summary>
    /// The numeric keypad's 8 (NUM8 in Kenshi's key names: the orders hotkeys, docs/game/ui-input.md section 4).
    /// </summary>
    Keypad8,
    /// <summary>
    /// The numeric keypad's 9 (NUM9 in Kenshi's key names: the orders hotkeys, docs/game/ui-input.md section 4).
    /// </summary>
    Keypad9,
    /// <summary>
    /// The numeric keypad's + (NUM+, the build map's second key for tilt, docs/game/ui-input.md section 4).
    /// </summary>
    KeypadAdd,
    /// <summary>
    /// The numeric keypad's - (NUM-, the build map's second key for tilt, docs/game/ui-input.md section 4).
    /// </summary>
    KeypadSubtract,
}

/// <summary>
/// Mouse buttons.
/// </summary>
public enum MouseButton
{
    /// <summary>
    /// The left button.
    /// </summary>
    Left,
    /// <summary>
    /// The right button.
    /// </summary>
    Right,
    /// <summary>
    /// The middle button (the wheel).
    /// </summary>
    Middle,
    /// <summary>
    /// The fourth button.
    /// </summary>
    Button4,
    /// <summary>
    /// The fifth button.
    /// </summary>
    Button5,
}

/// <summary>
/// One key or one mouse button an action can be bound to.
/// </summary>
public readonly record struct Binding
{
    /// <summary>
    /// The key or mouse button: below 0 a mouse button (-1 - button), 0 and up a key.
    /// </summary>
    readonly int _code;

    /// <summary>
    /// The binding of an encoded key or button.
    /// </summary>
    Binding(int code) => this._code = code;

    /// <summary>
    /// The binding of a key.
    /// </summary>
    public static Binding Of(Key key) => new((int)key);
    /// <summary>
    /// The binding of a mouse button.
    /// </summary>
    public static Binding Of(MouseButton button) => new(-1 - (int)button);
    /// <summary>
    /// A key as a binding.
    /// </summary>
    public static implicit operator Binding(Key key) => Of(key);
    /// <summary>
    /// A mouse button as a binding.
    /// </summary>
    public static implicit operator Binding(MouseButton button) => Of(button);

    /// <summary>
    /// Whether the binding is a mouse button.
    /// </summary>
    public bool IsMouse => _code < 0;
    /// <summary>
    /// The key; throws for a mouse binding.
    /// </summary>
    public Key Key => IsMouse ? throw new InvalidOperationException("A mouse binding") : (Key)_code;
    /// <summary>
    /// The mouse button; throws for a key binding.
    /// </summary>
    public MouseButton Button => IsMouse ? (MouseButton)(-1 - _code) : throw new InvalidOperationException("A key binding");

    /// <summary>
    /// "W", or "Mouse:Right" for a button (the form <see cref="InputBindings"/> reads from a config).
    /// </summary>
    public override string ToString() => IsMouse ? "Mouse:" + Button : Key.ToString();

    /// <summary>
    /// Reads a binding in the form <see cref="ToString"/> writes ("W", "Mouse:Right"; case ignored); false for an unknown key or button.
    /// </summary>
    public static bool TryParse(string text, out Binding binding)
    {
        text = text.Trim();
        const string prefix = "Mouse:";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            if (Enum.TryParse<MouseButton>(text[prefix.Length..], true, out var b) && Enum.IsDefined(b))
            {
                binding = Of(b);
                return true;
            }
        }
        else if (Enum.TryParse<Key>(text, true, out var k) && Enum.IsDefined(k))
        {
            binding = Of(k);
            return true;
        }
        binding = default;
        return false;
    }
}