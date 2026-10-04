using System.Numerics;

namespace Meitou.ModelViewer;

/// <summary>One slider: a value between <see cref="Min"/> and <see cref="Max"/>, read and written through the callbacks.</summary>
public sealed record Slider(string Label, float Min, float Max, Func<float> Get, Action<float> Set, string Format = "0.##", bool Logarithmic = false)
{
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
/// A panel of sliders drawn with the <see cref="DebugOverlay"/> in the top-right corner. Drag a slider with the left mouse
/// button; while the pointer is on the panel, the camera ignores the mouse.
/// </summary>
public sealed class SettingsPanel(DebugOverlay overlay, string title, IReadOnlyList<Slider> sliders)
{
    const float Margin = 16, Pad = 12, TrackWidth = 220, TrackHeight = 6, RowGap = 10;

    public bool Visible { get; set; }

    int dragging = -1;
    float panelX0, panelY0, panelX1, panelY1, trackX0;
    float rowHeight => overlay.LineHeight + TrackHeight + RowGap;

    float LabelWidth => sliders.Max(s => s.Label.Length + 12) * overlay.CharWidth;

    void Layout(int width)
    {
        float w = Math.Max(LabelWidth, TrackWidth) + 2 * Pad;
        panelX1 = width - Margin;
        panelX0 = panelX1 - w;
        panelY0 = Margin;
        panelY1 = panelY0 + Pad + overlay.LineHeight * 1.5f + sliders.Count * rowHeight + Pad;
        trackX0 = panelX0 + Pad;
    }

    float TrackTop(int i) => panelY0 + Pad + overlay.LineHeight * 1.5f + i * rowHeight + overlay.LineHeight + 2;

    /// <summary>Whether a point (window pixels) is on the panel.</summary>
    public bool Contains(Vector2 p) => Visible && p.X >= panelX0 && p.X <= panelX1 && p.Y >= panelY0 && p.Y <= panelY1;

    /// <summary>Starts dragging the slider under the pointer; true when the panel took the click.</summary>
    public bool MouseDown(Vector2 p)
    {
        if (!Contains(p)) return false;
        for (int i = 0; i < sliders.Count; i++)
        {
            float top = TrackTop(i);
            if (p.Y >= top - overlay.LineHeight - 2 && p.Y <= top + TrackHeight + RowGap * 0.5f)
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
        sliders[dragging].SetFraction((p.X - trackX0) / TrackWidth);
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
        overlay.Rect(panelX0, panelY0, panelX1, panelY1, DebugOverlay.PanelColour);
        overlay.Text(title, panelX0 + Pad, panelY0 + Pad, DebugOverlay.TextColour);
        for (int i = 0; i < sliders.Count; i++)
        {
            var s = sliders[i];
            float top = TrackTop(i);
            overlay.Text(s.Label, trackX0, top - overlay.LineHeight - 2, i == dragging ? fill : DebugOverlay.TextColour);
            string value = s.Get().ToString(s.Format, System.Globalization.CultureInfo.InvariantCulture);
            overlay.Text(value, panelX1 - Pad - value.Length * overlay.CharWidth, top - overlay.LineHeight - 2, dim);
            float t = s.Fraction;
            overlay.Rect(trackX0, top, trackX0 + TrackWidth, top + TrackHeight, track);
            overlay.Rect(trackX0, top, trackX0 + TrackWidth * t, top + TrackHeight, fill);
            float knob = trackX0 + TrackWidth * t;
            overlay.Rect(knob - 3, top - 4, knob + 3, top + TrackHeight + 4, DebugOverlay.TextColour);
        }
        overlay.Flush(width, height);
    }
}
