using System.Diagnostics;
using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Overlays;
using EngineKey = Meitou.Game.Host.Input.Key;

namespace Meitou.Game.Editor;

/// <summary>
/// The sandbox's animation drop-down (docs/simulation.md "Sandbox"): a button on the sandbox panel with the hero's forced animation ("simulation" when none);
/// a click opens the list of the database's animation and technique records under it, typed letters filter it, the wheel and Up / Down scroll it, a click or
/// Enter picks one and Esc (or a click elsewhere) closes it. The picked clip plays on the hero in a loop from the moment it was picked (<see cref="Pose"/>);
/// "simulation" hands the hero back to its own animation state.
/// </summary>
sealed class SandboxAnimationPicker(AnimationLibrary library, AnimationLengths? lengths)
{
    const string Simulation = "simulation";
    const int VisibleRows = 24;
    /// <summary>The record names, techniques included, each once.</summary>
    readonly string[] _names = [Simulation, .. library.Definitions.Select(d => d.Name).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
    string _filter = "";
    List<string> _shown = [];
    int _scroll, _highlight;
    string? _current;
    float _length = 1;
    long _pickedAt;
    // The button and the open list, as last drawn (pixels).
    float _buttonX0, _buttonY0, _buttonX1, _buttonY1, _listX0, _listY0, _listX1, _listY1, _rowHeight = 20;

    public bool Open { get; private set; }

    /// <summary>The forced animation's record name, null when the simulation animates the hero.</summary>
    public string? Current => _current;

    public string ButtonText => $"animation: {_current ?? Simulation} v";

    /// <summary>The hero's pose this frame: the picked clip looping by the wall clock, or null for the simulation's.</summary>
    public CharacterPose[]? Pose() => _current is null ? null : [new CharacterPose(_current, (float)(Stopwatch.GetElapsedTime(_pickedAt).TotalSeconds % _length), 1)];

    public string Status => _current is null ? "" : $"loops {_length:0.00} s";

    void Pick(string name)
    {
        Open = false;
        if (name == Simulation) { _current = null; return; }
        _current = name;
        int at = library.IndexOfAny(name);
        _length = lengths?.Of(at >= 0 ? library.Definitions[at].Clip : name) ?? 1;
        _pickedAt = Stopwatch.GetTimestamp();
    }

    void Refilter()
    {
        _shown = [.. _names.Where(n => n == Simulation || n.Contains(_filter, StringComparison.OrdinalIgnoreCase))];
        _scroll = 0;
        _highlight = 0;
    }

    void ClampScroll()
    {
        _highlight = Math.Clamp(_highlight, 0, Math.Max(0, _shown.Count - 1));
        _scroll = Math.Clamp(_scroll, Math.Max(0, _highlight - VisibleRows + 1), Math.Min(_highlight, Math.Max(0, _shown.Count - VisibleRows)));
    }

    static bool In(Vector2 p, float x0, float y0, float x1, float y1) => p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1;

    /// <summary>True when the click was the picker's (the button, or the list while open: a click elsewhere just closes it and is still the game's).</summary>
    public bool MouseDown(Vector2 p)
    {
        if (Open)
        {
            Open = false;
            if (In(p, _listX0, _listY0, _listX1, _listY1))
            {
                int k = _scroll + (int)((p.Y - _listY0) / _rowHeight);
                if (k >= 0 && k < _shown.Count) Pick(_shown[k]);
                return true;
            }
            if (In(p, _buttonX0, _buttonY0, _buttonX1, _buttonY1)) return true;
            return false;
        }
        if (!In(p, _buttonX0, _buttonY0, _buttonX1, _buttonY1)) return false;
        _filter = "";
        Refilter();
        Open = true;
        return true;
    }

    public bool Scroll(float wheel)
    {
        if (!Open) return false;
        _highlight = Math.Clamp(_highlight - (int)MathF.Round(wheel) * 3, 0, Math.Max(0, _shown.Count - 1));
        ClampScroll();
        return true;
    }

    /// <summary>A key while the list is open (every key is the picker's then): Esc closes, Enter picks the highlighted row, Up / Down move, Backspace edits the filter.</summary>
    public void KeyDown(EngineKey key)
    {
        switch (key)
        {
            case EngineKey.Escape: Open = false; break;
            case EngineKey.Enter: if (_shown.Count > 0) Pick(_shown[_highlight]); break;
            case EngineKey.Up: _highlight--; ClampScroll(); break;
            case EngineKey.Down: _highlight++; ClampScroll(); break;
            case EngineKey.PageUp: _highlight -= VisibleRows; ClampScroll(); break;
            case EngineKey.PageDown: _highlight += VisibleRows; ClampScroll(); break;
            case EngineKey.Backspace when _filter.Length > 0: _filter = _filter[..^1]; Refilter(); break;
        }
    }

    public void TextInput(char c)
    {
        if (!Open || char.IsControl(c)) return;
        _filter += c;
        Refilter();
    }

    /// <summary>Draws the button at (<paramref name="x"/>, <paramref name="y"/>) over <paramref name="width"/> pixels and, when open, the list under it.</summary>
    public void Draw(DebugOverlay o, float x, float y, float width)
    {
        _rowHeight = o.LineHeight + 4;
        (_buttonX0, _buttonY0, _buttonX1, _buttonY1) = (x, y, x + width, y + _rowHeight);
        o.Rect(_buttonX0, _buttonY0, _buttonX1, _buttonY1, Open ? new Vector4(0.3f, 0.3f, 0.32f, 1) : new Vector4(0.2f, 0.2f, 0.22f, 1));
        o.Text(o.FitText(ButtonText, width - 8), x + 4, y + 2, DebugOverlay.TextColour);
        if (!Open) return;
        int rows = Math.Min(VisibleRows, _shown.Count);
        (_listX0, _listY0, _listX1, _listY1) = (x, _buttonY1 + 2, x + width, _buttonY1 + 2 + (rows + 1) * _rowHeight);
        o.Rect(_listX0, _listY0, _listX1, _listY1, new Vector4(0.1f, 0.1f, 0.11f, 0.97f));
        o.Text(_filter.Length > 0 ? $"filter: {_filter}_   {_shown.Count} of {_names.Length}" : $"type to filter   {_shown.Count}", x + 4, _listY0 + 2, new Vector4(0.7f, 0.75f, 0.8f, 1));
        for (int i = 0; i < rows; i++)
        {
            int k = _scroll + i;
            float top = _listY0 + (i + 1) * _rowHeight;
            if (k == _highlight) o.Rect(_listX0, top, _listX1, top + _rowHeight, new Vector4(0.25f, 0.3f, 0.4f, 1));
            bool chosen = _shown[k] == (_current ?? Simulation);
            o.Text(o.FitText((chosen ? "x " : "  ") + _shown[k], width - 8), x + 4, top + 2, chosen ? new Vector4(1f, 0.9f, 0.4f, 1) : DebugOverlay.TextColour);
        }
    }
}