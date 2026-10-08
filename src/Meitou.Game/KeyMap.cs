using EngineKey = Meitou.Engine.Input.Key;
using EngineButton = Meitou.Engine.Input.MouseButton;
using SilkKey = Silk.NET.Input.Key;
using SilkButton = Silk.NET.Input.MouseButton;

namespace Meitou.Game;

/// <summary>The windowing library's keys and buttons as the engine's (null for the ones the engine has no name for).</summary>
static class KeyMap
{
    public static EngineButton? Map(SilkButton b) => b switch
    {
        SilkButton.Left => EngineButton.Left,
        SilkButton.Right => EngineButton.Right,
        SilkButton.Middle => EngineButton.Middle,
        SilkButton.Button4 => EngineButton.Button4,
        SilkButton.Button5 => EngineButton.Button5,
        _ => null,
    };

    public static EngineKey? Map(SilkKey k) => k switch
    {
        >= SilkKey.A and <= SilkKey.Z => EngineKey.A + (k - SilkKey.A),
        >= SilkKey.Number0 and <= SilkKey.Number9 => EngineKey.D0 + (k - SilkKey.Number0),
        >= SilkKey.F1 and <= SilkKey.F12 => EngineKey.F1 + (k - SilkKey.F1),
        SilkKey.Left => EngineKey.Left,
        SilkKey.Right => EngineKey.Right,
        SilkKey.Up => EngineKey.Up,
        SilkKey.Down => EngineKey.Down,
        SilkKey.Space => EngineKey.Space,
        SilkKey.Enter => EngineKey.Enter,
        SilkKey.Escape => EngineKey.Escape,
        SilkKey.Tab => EngineKey.Tab,
        SilkKey.PrintScreen => EngineKey.PrintScreen,
        SilkKey.Backspace => EngineKey.Backspace,
        SilkKey.PageUp => EngineKey.PageUp,
        SilkKey.PageDown => EngineKey.PageDown,
        SilkKey.Home => EngineKey.Home,
        SilkKey.End => EngineKey.End,
        SilkKey.Insert => EngineKey.Insert,
        SilkKey.Delete => EngineKey.Delete,
        SilkKey.ShiftLeft => EngineKey.LeftShift,
        SilkKey.ShiftRight => EngineKey.RightShift,
        SilkKey.ControlLeft => EngineKey.LeftControl,
        SilkKey.ControlRight => EngineKey.RightControl,
        SilkKey.AltLeft => EngineKey.LeftAlt,
        SilkKey.AltRight => EngineKey.RightAlt,
        SilkKey.Semicolon => EngineKey.Semicolon,
        SilkKey.Comma => EngineKey.Comma,
        SilkKey.Period => EngineKey.Period,
        SilkKey.Minus => EngineKey.Minus,
        SilkKey.Equal => EngineKey.Equal,
        SilkKey.Slash => EngineKey.Slash,
        SilkKey.BackSlash => EngineKey.Backslash,
        SilkKey.GraveAccent => EngineKey.Grave,
        SilkKey.LeftBracket => EngineKey.LeftBracket,
        SilkKey.RightBracket => EngineKey.RightBracket,
        SilkKey.Apostrophe => EngineKey.Apostrophe,
        _ => null,
    };
}
