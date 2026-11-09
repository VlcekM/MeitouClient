using System.Numerics;

namespace Meitou.Game.Host.Input;

/// <summary>
/// What the host fed since the previous tick, read by one tick: which keys and buttons are down, which went down or up since the
/// previous tick, the mouse movement and wheel notches summed over the same time and the mouse position.
/// </summary>
public sealed class TickInput
{
    /// <summary>
    /// How many keys there are (<see cref="Key"/>'s values).
    /// </summary>
    static readonly int KeyCount = Enum.GetValues<Key>().Length;
    /// <summary>
    /// How many mouse buttons there are (<see cref="MouseButton"/>'s values).
    /// </summary>
    static readonly int ButtonCount = Enum.GetValues<MouseButton>().Length;

    /// <summary>
    /// Each key's state at the tick: down.
    /// </summary>
    internal readonly bool[] KeyDown = new bool[KeyCount];
    /// <summary>
    /// Each key went down since the previous tick.
    /// </summary>
    internal readonly bool[] KeyPressed = new bool[KeyCount];
    /// <summary>
    /// Each key went up since the previous tick.
    /// </summary>
    internal readonly bool[] KeyReleased = new bool[KeyCount];
    /// <summary>
    /// Each mouse button's state at the tick: down.
    /// </summary>
    internal readonly bool[] ButtonDown = new bool[ButtonCount];
    /// <summary>
    /// Each mouse button went down since the previous tick.
    /// </summary>
    internal readonly bool[] ButtonPressed = new bool[ButtonCount];
    /// <summary>
    /// Each mouse button went up since the previous tick.
    /// </summary>
    internal readonly bool[] ButtonReleased = new bool[ButtonCount];

    /// <summary>
    /// Mouse movement in pixels since the previous tick (x right, y down).
    /// </summary>
    public Vector2 MouseDelta { get; internal set; }
    /// <summary>
    /// Wheel notches since the previous tick, positive away from the user (zoom in).
    /// </summary>
    public float Wheel { get; internal set; }
    /// <summary>
    /// The mouse position at the tick, in window pixels.
    /// </summary>
    public Vector2 MousePosition { get; internal set; }

    /// <summary>
    /// Whether the key is down at the tick.
    /// </summary>
    public bool IsDown(Key key) => KeyDown[(int)key];
    /// <summary>
    /// Whether the key went down since the previous tick.
    /// </summary>
    public bool WasPressed(Key key) => KeyPressed[(int)key];
    /// <summary>
    /// Whether the key went up since the previous tick.
    /// </summary>
    public bool WasReleased(Key key) => KeyReleased[(int)key];
    /// <summary>
    /// Whether the mouse button is down at the tick.
    /// </summary>
    public bool IsDown(MouseButton button) => ButtonDown[(int)button];
    /// <summary>
    /// Whether the mouse button went down since the previous tick.
    /// </summary>
    public bool WasPressed(MouseButton button) => ButtonPressed[(int)button];
    /// <summary>
    /// Whether the mouse button went up since the previous tick.
    /// </summary>
    public bool WasReleased(MouseButton button) => ButtonReleased[(int)button];

    /// <summary>
    /// Whether the binding's key or button is down at the tick.
    /// </summary>
    public bool IsDown(Binding b) => b.IsMouse ? IsDown(b.Button) : IsDown(b.Key);
    /// <summary>
    /// Whether the binding's key or button went down since the previous tick.
    /// </summary>
    public bool WasPressed(Binding b) => b.IsMouse ? WasPressed(b.Button) : WasPressed(b.Key);
    /// <summary>
    /// Whether the binding's key or button went up since the previous tick.
    /// </summary>
    public bool WasReleased(Binding b) => b.IsMouse ? WasReleased(b.Button) : WasReleased(b.Key);

    /// <summary>
    /// An empty input (nothing down, nothing moved): for ticks driven without a host.
    /// </summary>
    public static TickInput None { get; } = new();
}

/// <summary>
/// The host's side of input: it reports key, button, mouse and wheel events as they come (any number of times per render frame,
/// or none), and each simulation tick calls <see cref="Consume"/> once. Edges are kept until a tick consumes them, so a press that
/// happened in a frame is seen as pressed by exactly one tick: when several ticks run in one frame the first one sees it and the
/// rest see the key held; when a frame runs no tick the press waits for the next tick. A press and release inside one tick's
/// window show as both pressed and released with the key up. Mouse movement and wheel are summed the same way and handed to the
/// first tick only. A repeated "down" for a held key (OS key repeat) is not a new press.
/// </summary>
public sealed class InputState
{
    /// <summary>
    /// The edges and sums gathered for the next tick.
    /// </summary>
    TickInput _pending = new();
    /// <summary>
    /// Each key's state as the host has reported so far.
    /// </summary>
    readonly bool[] _keyHeld = new bool[Enum.GetValues<Key>().Length];
    /// <summary>
    /// Each mouse button's state as the host has reported so far.
    /// </summary>
    readonly bool[] _buttonHeld = new bool[Enum.GetValues<MouseButton>().Length];
    /// <summary>
    /// The mouse position last reported.
    /// </summary>
    Vector2 _mousePosition;

    /// <summary>
    /// A key went down or up; a repeated report of the same state (OS key repeat) is no edge.
    /// </summary>
    public void SetKey(Key key, bool down)
    {
        int i = (int)key;
        if (_keyHeld[i] == down) return;
        _keyHeld[i] = down;
        (down ? _pending.KeyPressed : _pending.KeyReleased)[i] = true;
    }

    /// <summary>
    /// A mouse button went down or up; a repeated report of the same state is no edge.
    /// </summary>
    public void SetMouseButton(MouseButton button, bool down)
    {
        int i = (int)button;
        if (_buttonHeld[i] == down) return;
        _buttonHeld[i] = down;
        (down ? _pending.ButtonPressed : _pending.ButtonReleased)[i] = true;
    }

    /// <summary>
    /// The binding's key or button is down as the host has reported so far (not as the last tick saw it).
    /// </summary>
    public bool IsHeld(Binding b) => b.IsMouse ? _buttonHeld[(int)b.Button] : _keyHeld[(int)b.Key];

    /// <summary>
    /// Adds mouse movement since the last report.
    /// </summary>
    public void AddMouseDelta(float dx, float dy) => _pending.MouseDelta += new Vector2(dx, dy);

    /// <summary>
    /// Adds wheel notches since the last report (positive away from the user).
    /// </summary>
    public void AddWheel(float notches) => _pending.Wheel += notches;

    /// <summary>
    /// The mouse position now, in window pixels.
    /// </summary>
    public void SetMousePosition(float x, float y) => _mousePosition = new Vector2(x, y);

    /// <summary>
    /// Releases everything held (the window lost focus): the tick sees the releases.
    /// </summary>
    public void ReleaseAll()
    {
        foreach (var k in Enum.GetValues<Key>()) SetKey(k, false);
        foreach (var b in Enum.GetValues<MouseButton>()) SetMouseButton(b, false);
    }

    /// <summary>
    /// The snapshot for the next tick; the edges and sums start over.
    /// </summary>
    public TickInput Consume()
    {
        var snapshot = _pending;
        Array.Copy(_keyHeld, snapshot.KeyDown, _keyHeld.Length);
        Array.Copy(_buttonHeld, snapshot.ButtonDown, _buttonHeld.Length);
        snapshot.MousePosition = _mousePosition;
        _pending = new TickInput();
        return snapshot;
    }
}