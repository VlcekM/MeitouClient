using System.Numerics;

namespace Meitou.Rendering;

/// <summary>One slider: a value between <see cref="Min"/> and <see cref="Max"/>, read and written through the callbacks.</summary>
public sealed record Slider(string Label, float Min, float Max, Func<float> Get, Action<float> Set, string Format = "0.##", bool Logarithmic = false,
    Func<float, string>? Text = null)
{
    /// <summary>The value as the panel shows it (<see cref="Text"/> when given, else <see cref="Format"/>).</summary>
    public string Display => Text?.Invoke(Get()) ?? Get().ToString(Format, System.Globalization.CultureInfo.InvariantCulture);

    public float Fraction
    {
        get
        {
            float v = Math.Clamp(Get(), Min, Max);
            return Logarithmic ? MathF.Log(v / Min) / MathF.Log(Max / Min) : (v - Min) / (Max - Min);
        }
    }

    public void SetFraction(float t)
    {
        t = Math.Clamp(t, 0, 1);
        Set(Logarithmic ? Min * MathF.Pow(Max / Min, t) : Min + (Max - Min) * t);
    }
}

/// <summary>
/// A panel of sliders drawn with the <see cref="DebugOverlay"/> in two columns in the top-left corner, over the other panels. Drag a slider with the left mouse
/// button; while the pointer is on the panel, the camera ignores the mouse. The button at the bottom puts every slider back to
/// the value it had when the panel was made (before a saved config or a drag changed it).
/// </summary>
public sealed class SettingsPanel(DebugOverlay overlay, string title, IReadOnlyList<Slider> sliders)
{
    const float Margin = 16, Pad = 12, TrackHeight = 6, RowGap = 10, ColumnGap = 28, MinTrackWidth = 220;
    const int Columns = 2;
    /// <summary>Nearly opaque: the panel is drawn over the statistics and the profiler.</summary>
    static readonly Vector4 Background = new(0.05f, 0.05f, 0.06f, 0.95f);
    const string ResetLabel = "Reset to defaults";

    readonly float[] defaults = sliders.Select(s => s.Get()).ToArray();

    public bool Visible { get; set; }
    /// <summary>The sliders (the game saves their values in its user config, by label).</summary>
    public IReadOnlyList<Slider> Sliders => sliders;

    /// <summary>Sets every slider back to its value when the panel was made.</summary>
    public void Reset()
    {
        for (int i = 0; i < sliders.Count; i++) sliders[i].Set(defaults[i]);
    }

    float ButtonTop => panelY0 + Pad + overlay.LineHeight * 1.5f + Rows * rowHeight;
    float ButtonBottom => ButtonTop + overlay.LineHeight + 8;

    int dragging = -1;
    float panelX0, panelY0, panelX1, panelY1, columnWidth;
    float rowHeight => overlay.LineHeight + TrackHeight + RowGap;

    float ButtonWidth => ResetLabel.Length * overlay.CharWidth + 16;

    float LabelWidth => sliders.Max(s => s.Label.Length + 9) * overlay.CharWidth;

    /// <summary>The sliders run down the first column, then the second.</summary>
    int Rows => (sliders.Count + Columns - 1) / Columns;

    void Layout(int width)
    {
        columnWidth = Math.Max(LabelWidth, MinTrackWidth);
        panelX0 = Margin;
        panelX1 = panelX0 + 2 * Pad + Columns * columnWidth + (Columns - 1) * ColumnGap;
        panelY0 = Margin;
        panelY1 = panelY0 + Pad + overlay.LineHeight * 1.5f + Rows * rowHeight + overlay.LineHeight + 8 + Pad;
    }

    float TrackX(int i) => panelX0 + Pad + i / Rows * (columnWidth + ColumnGap);
    float TrackTop(int i) => panelY0 + Pad + overlay.LineHeight * 1.5f + i % Rows * rowHeight + overlay.LineHeight + 2;

    /// <summary>Whether a point (window pixels) is on the panel.</summary>
    public bool Contains(Vector2 p) => Visible && p.X >= panelX0 && p.X <= panelX1 && p.Y >= panelY0 && p.Y <= panelY1;

    /// <summary>Starts dragging the slider under the pointer; true when the panel took the click.</summary>
    public bool MouseDown(Vector2 p)
    {
        if (!Contains(p)) return false;
        if (p.Y >= ButtonTop && p.Y <= ButtonBottom && p.X >= panelX0 + Pad && p.X <= panelX0 + Pad + ButtonWidth)
        {
            Reset();
            return true;
        }
        for (int i = 0; i < sliders.Count; i++)
        {
            float top = TrackTop(i), x = TrackX(i);
            if (p.Y >= top - overlay.LineHeight - 2 && p.Y <= top + TrackHeight + RowGap * 0.5f && p.X >= x - Pad / 2 && p.X <= x + columnWidth + Pad / 2)
            {
                dragging = i;
                MouseMove(p);
                break;
            }
        }
        return true;
    }

    /// <summary>Moves the dragged slider; true while one is being dragged.</summary>
    public bool MouseMove(Vector2 p)
    {
        if (dragging < 0) return false;
        sliders[dragging].SetFraction((p.X - TrackX(dragging)) / columnWidth);
        return true;
    }

    /// <summary>Ends a drag; true when one was in progress.</summary>
    public bool MouseUp()
    {
        bool was = dragging >= 0;
        dragging = -1;
        return was;
    }

    public void Draw(int width, int height)
    {
        if (!Visible) return;
        Layout(width);
        Vector4 track = new(0.3f, 0.3f, 0.32f, 1), fill = new(0.85f, 0.65f, 0.35f, 1), dim = new(0.7f, 0.7f, 0.68f, 1);
        overlay.Rect(panelX0, panelY0, panelX1, panelY1, Background);
        overlay.Text(title, panelX0 + Pad, panelY0 + Pad, DebugOverlay.TextColour);
        for (int i = 0; i < sliders.Count; i++)
        {
            var s = sliders[i];
            float top = TrackTop(i), x = TrackX(i);
            overlay.Text(s.Label, x, top - overlay.LineHeight - 2, i == dragging ? fill : DebugOverlay.TextColour);
            string value = s.Display;
            overlay.Text(value, x + columnWidth - value.Length * overlay.CharWidth, top - overlay.LineHeight - 2, dim);
            float t = s.Fraction;
            overlay.Rect(x, top, x + columnWidth, top + TrackHeight, track);
            overlay.Rect(x, top, x + columnWidth * t, top + TrackHeight, fill);
            float knob = x + columnWidth * t;
            overlay.Rect(knob - 3, top - 4, knob + 3, top + TrackHeight + 4, DebugOverlay.TextColour);
        }
        overlay.Rect(panelX0 + Pad, ButtonTop, panelX0 + Pad + ButtonWidth, ButtonBottom, track);
        overlay.Text(ResetLabel, panelX0 + Pad + 8, ButtonTop + 4, DebugOverlay.TextColour);
        overlay.Flush(width, height);
    }
}
