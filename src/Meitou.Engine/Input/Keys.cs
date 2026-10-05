namespace Meitou.Engine.Input;

/// <summary>Keyboard keys as the engine names them (the host maps its windowing library's keys to these).</summary>
public enum Key
{
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Left, Right, Up, Down,
    Space, Enter, Escape, Tab, Backspace,
    PageUp, PageDown, Home, End, Insert, Delete,
    LeftShift, RightShift, LeftControl, RightControl, LeftAlt, RightAlt,
    Semicolon, Comma, Period, Minus, Equal, Slash, Backslash, Grave,
    LeftBracket, RightBracket, Apostrophe,
}

/// <summary>Mouse buttons.</summary>
public enum MouseButton
{
    Left,
    Right,
    Middle,
    Button4,
    Button5,
}

/// <summary>One key or one mouse button an action can be bound to.</summary>
public readonly record struct Binding
{
    readonly int code; // < 0: mouse button (-1 - button), >= 0: key

    Binding(int code) => this.code = code;

    public static Binding Of(Key key) => new((int)key);
    public static Binding Of(MouseButton button) => new(-1 - (int)button);
    public static implicit operator Binding(Key key) => Of(key);
    public static implicit operator Binding(MouseButton button) => Of(button);

    public bool IsMouse => code < 0;
    public Key Key => IsMouse ? throw new InvalidOperationException("A mouse binding") : (Key)code;
    public MouseButton Button => IsMouse ? (MouseButton)(-1 - code) : throw new InvalidOperationException("A key binding");

    /// <summary>"W", or "Mouse:Right" for a button (the form <see cref="InputBindings"/> reads from a config).</summary>
    public override string ToString() => IsMouse ? "Mouse:" + Button : Key.ToString();

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
