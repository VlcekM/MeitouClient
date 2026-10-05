using System.Numerics;

namespace Meitou.Engine.Input;

/// <summary>
/// What the host fed since the previous tick, read by one tick: which keys and buttons are down, which went down or up since the
/// previous tick, the mouse movement and wheel notches summed over the same time and the mouse position.
/// </summary>
public sealed class TickInput
{
    static readonly int KeyCount = Enum.GetValues<Key>().Length;
    static readonly int ButtonCount = Enum.GetValues<MouseButton>().Length;

    internal readonly bool[] KeyDown = new bool[KeyCount];
    internal readonly bool[] KeyPressed = new bool[KeyCount];
    internal readonly bool[] KeyReleased = new bool[KeyCount];
    internal readonly bool[] ButtonDown = new bool[ButtonCount];
    internal readonly bool[] ButtonPressed = new bool[ButtonCount];
    internal readonly bool[] ButtonReleased = new bool[ButtonCount];

    /// <summary>Mouse movement in pixels since the previous tick (x right, y down).</summary>
    public Vector2 MouseDelta { get; internal set; }
    /// <summary>Wheel notches since the previous tick, positive away from the user (zoom in).</summary>
    public float Wheel { get; internal set; }
    public Vector2 MousePosition { get; internal set; }

    public bool IsDown(Key key) => KeyDown[(int)key];
    public bool WasPressed(Key key) => KeyPressed[(int)key];
    public bool WasReleased(Key key) => KeyReleased[(int)key];
    public bool IsDown(MouseButton button) => ButtonDown[(int)button];
    public bool WasPressed(MouseButton button) => ButtonPressed[(int)button];
    public bool WasReleased(MouseButton button) => ButtonReleased[(int)button];

    public bool IsDown(Binding b) => b.IsMouse ? IsDown(b.Button) : IsDown(b.Key);
    public bool WasPressed(Binding b) => b.IsMouse ? WasPressed(b.Button) : WasPressed(b.Key);
    public bool WasReleased(Binding b) => b.IsMouse ? WasReleased(b.Button) : WasReleased(b.Key);

    /// <summary>An empty input (nothing down, nothing moved): for ticks driven without a host.</summary>
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
    TickInput pending = new();
    readonly bool[] keyHeld = new bool[Enum.GetValues<Key>().Length];
    readonly bool[] buttonHeld = new bool[Enum.GetValues<MouseButton>().Length];
    Vector2 mousePosition;

    public void SetKey(Key key, bool down)
    {
        int i = (int)key;
        if (keyHeld[i] == down) return;
        keyHeld[i] = down;
        (down ? pending.KeyPressed : pending.KeyReleased)[i] = true;
    }

    public void SetMouseButton(MouseButton button, bool down)
    {
        int i = (int)button;
        if (buttonHeld[i] == down) return;
        buttonHeld[i] = down;
        (down ? pending.ButtonPressed : pending.ButtonReleased)[i] = true;
    }

    /// <summary>Adds mouse movement since the last report.</summary>
    public void AddMouseDelta(float dx, float dy) => pending.MouseDelta += new Vector2(dx, dy);

    public void AddWheel(float notches) => pending.Wheel += notches;

    public void SetMousePosition(float x, float y) => mousePosition = new Vector2(x, y);

    /// <summary>Releases everything held (the window lost focus): the tick sees the releases.</summary>
    public void ReleaseAll()
    {
        foreach (var k in Enum.GetValues<Key>()) SetKey(k, false);
        foreach (var b in Enum.GetValues<MouseButton>()) SetMouseButton(b, false);
    }

    /// <summary>The snapshot for the next tick; the edges and sums start over.</summary>
    public TickInput Consume()
    {
        var snapshot = pending;
        Array.Copy(keyHeld, snapshot.KeyDown, keyHeld.Length);
        Array.Copy(buttonHeld, snapshot.ButtonDown, buttonHeld.Length);
        snapshot.MousePosition = mousePosition;
        pending = new TickInput();
        return snapshot;
    }
}
