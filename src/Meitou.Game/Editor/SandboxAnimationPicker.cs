using System.Diagnostics;
using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Rendering;
using EngineKey = Meitou.Engine.Input.Key;

namespace Meitou.Game;

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
    readonly string[] names = [Simulation, .. library.Definitions.Select(d => d.Name).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
    string filter = "";
    List<string> shown = [];
    int scroll, highlight;
    string? current;
    float length = 1;
    long pickedAt;
    // The button and the open list, as last drawn (pixels).
    float buttonX0, buttonY0, buttonX1, buttonY1, listX0, listY0, listX1, listY1, rowHeight = 20;

    public bool Open { get; private set; }

    /// <summary>The forced animation's record name, null when the simulation animates the hero.</summary>
    public string? Current => current;

    public string ButtonText => $"animation: {current ?? Simulation} v";

    /// <summary>The hero's pose this frame: the picked clip looping by the wall clock, or null for the simulation's.</summary>
    public CharacterPose[]? Pose() => current is null ? null : [new CharacterPose(current, (float)(Stopwatch.GetElapsedTime(pickedAt).TotalSeconds % length), 1)];

    public string Status => current is null ? "" : $"loops {length:0.00} s";

    void Pick(string name)
    {
        Open = false;
        if (name == Simulation) { current = null; return; }
        current = name;
        int at = library.IndexOfAny(name);
        length = lengths?.Of(at >= 0 ? library.Definitions[at].Clip : name) ?? 1;
        pickedAt = Stopwatch.GetTimestamp();
    }

    void Refilter()
    {
        shown = [.. names.Where(n => n == Simulation || n.Contains(filter, StringComparison.OrdinalIgnoreCase))];
        scroll = 0;
        highlight = 0;
    }

    void ClampScroll()
    {
        highlight = Math.Clamp(highlight, 0, Math.Max(0, shown.Count - 1));
        scroll = Math.Clamp(scroll, Math.Max(0, highlight - VisibleRows + 1), Math.Min(highlight, Math.Max(0, shown.Count - VisibleRows)));
    }

    static bool In(Vector2 p, float x0, float y0, float x1, float y1) => p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1;

    /// <summary>True when the click was the picker's (the button, or the list while open: a click elsewhere just closes it and is still the game's).</summary>
    public bool MouseDown(Vector2 p)
    {
        if (Open)
        {
            Open = false;
            if (In(p, listX0, listY0, listX1, listY1))
            {
                int k = scroll + (int)((p.Y - listY0) / rowHeight);
                if (k >= 0 && k < shown.Count) Pick(shown[k]);
                return true;
            }
            if (In(p, buttonX0, buttonY0, buttonX1, buttonY1)) return true;
            return false;
        }
        if (!In(p, buttonX0, buttonY0, buttonX1, buttonY1)) return false;
        filter = "";
        Refilter();
        Open = true;
        return true;
    }

    public bool Scroll(float wheel)
    {
        if (!Open) return false;
        highlight = Math.Clamp(highlight - (int)MathF.Round(wheel) * 3, 0, Math.Max(0, shown.Count - 1));
        ClampScroll();
        return true;
    }

    /// <summary>A key while the list is open (every key is the picker's then): Esc closes, Enter picks the highlighted row, Up / Down move, Backspace edits the filter.</summary>
    public void KeyDown(EngineKey key)
    {
        switch (key)
        {
            case EngineKey.Escape: Open = false; break;
            case EngineKey.Enter: if (shown.Count > 0) Pick(shown[highlight]); break;
            case EngineKey.Up: highlight--; ClampScroll(); break;
            case EngineKey.Down: highlight++; ClampScroll(); break;
            case EngineKey.PageUp: highlight -= VisibleRows; ClampScroll(); break;
            case EngineKey.PageDown: highlight += VisibleRows; ClampScroll(); break;
            case EngineKey.Backspace when filter.Length > 0: filter = filter[..^1]; Refilter(); break;
        }
    }

    public void TextInput(char c)
    {
        if (!Open || char.IsControl(c)) return;
        filter += c;
        Refilter();
    }

    /// <summary>Draws the button at (<paramref name="x"/>, <paramref name="y"/>) over <paramref name="width"/> pixels and, when open, the list under it.</summary>
    public void Draw(DebugOverlay o, float x, float y, float width)
    {
        rowHeight = o.LineHeight + 4;
        (buttonX0, buttonY0, buttonX1, buttonY1) = (x, y, x + width, y + rowHeight);
        o.Rect(buttonX0, buttonY0, buttonX1, buttonY1, Open ? new Vector4(0.3f, 0.3f, 0.32f, 1) : new Vector4(0.2f, 0.2f, 0.22f, 1));
        o.Text(o.FitText(ButtonText, width - 8), x + 4, y + 2, DebugOverlay.TextColour);
        if (!Open) return;
        int rows = Math.Min(VisibleRows, shown.Count);
        (listX0, listY0, listX1, listY1) = (x, buttonY1 + 2, x + width, buttonY1 + 2 + (rows + 1) * rowHeight);
        o.Rect(listX0, listY0, listX1, listY1, new Vector4(0.1f, 0.1f, 0.11f, 0.97f));
        o.Text(filter.Length > 0 ? $"filter: {filter}_   {shown.Count} of {names.Length}" : $"type to filter   {shown.Count}", x + 4, listY0 + 2, new Vector4(0.7f, 0.75f, 0.8f, 1));
        for (int i = 0; i < rows; i++)
        {
            int k = scroll + i;
            float top = listY0 + (i + 1) * rowHeight;
            if (k == highlight) o.Rect(listX0, top, listX1, top + rowHeight, new Vector4(0.25f, 0.3f, 0.4f, 1));
            bool chosen = shown[k] == (current ?? Simulation);
            o.Text(o.FitText((chosen ? "x " : "  ") + shown[k], width - 8), x + 4, top + 2, chosen ? new Vector4(1f, 0.9f, 0.4f, 1) : DebugOverlay.TextColour);
        }
    }
}
