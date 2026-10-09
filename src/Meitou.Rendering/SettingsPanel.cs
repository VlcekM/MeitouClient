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

/// <summary>One checkbox: on or off, read and written through the callbacks; <see cref="Text"/> describes the state beside it.</summary>
public sealed record Toggle(string Label, Func<bool> Get, Action<bool> Set, Func<string>? Text = null);

/// <summary>A button beside the reset one that asks first: <see cref="Question"/> is shown with Yes / No, Yes runs <see cref="Run"/>, whose text is shown after.</summary>
public sealed record PanelAction(string Label, Func<string> Question, Func<string> Run);

/// <summary>
/// A panel of sliders drawn with the <see cref="DebugOverlay"/> in two columns in the top-left corner (and a third when <c>sideSliders</c> are given: they
/// run down it on their own), over the other panels. Drag a slider with the left mouse
/// button; while the pointer is on the panel, the camera ignores the mouse. The button at the bottom puts every slider back to
/// the value it had when the panel was made (before a saved config or a drag changed it). Below the sliders, optional checkboxes under their own
/// heading (the Faithful / Meitou switches), toggled with a click and reset with the sliders. Beside the reset button, optional
/// <see cref="PanelAction"/> buttons: a click shows the action's question with Yes / No on a row below, and the result there after Yes.
/// </summary>
public sealed class SettingsPanel(DebugOverlay overlay, string title, IReadOnlyList<Slider> mainSliders, IReadOnlyList<Toggle>? toggles = null, string togglesTitle = "",
    IReadOnlyList<PanelAction>? actions = null, IReadOnlyList<Slider>? sideSliders = null)
{
    const float Margin = 16, Pad = 12, TrackHeight = 6, RowGap = 10, ColumnGap = 28, MinTrackWidth = 220;
    /// <summary>The main sliders' and the checkboxes' columns; the side sliders add one to the panel's width.</summary>
    const int Columns = 2;
    /// <summary>The main sliders, then the side ones (indices from <see cref="mainCount"/> are in the third column).</summary>
    readonly IReadOnlyList<Slider> sliders = [.. mainSliders, .. sideSliders ?? []];
    readonly int mainCount = mainSliders.Count;
    int PanelColumns => sliders.Count > mainCount ? Columns + 1 : Columns;
    /// <summary>Nearly opaque: the panel is drawn over the statistics and the profiler.</summary>
    static readonly Vector4 Background = new(0.05f, 0.05f, 0.06f, 0.95f);
    const string ResetLabel = "Reset to defaults";

    readonly float[] defaults = [.. mainSliders.Concat(sideSliders ?? []).Select(s => s.Get())];
    readonly IReadOnlyList<Toggle> toggles = toggles ?? [];
    readonly bool[] toggleDefaults = (toggles ?? []).Select(t => t.Get()).ToArray();

    readonly IReadOnlyList<PanelAction> actions = actions ?? [];
    /// <summary>The action waiting for Yes / No (-1 none), its question, and the last action's result (shown until the next click on the panel).</summary>
    int confirming = -1;
    string? question, message;

    /// <summary>Hiding the panel drops a pending question and the last result.</summary>
    public bool Visible
    {
        get => visible;
        set { visible = value; if (!value) { confirming = -1; question = message = null; } }
    }
    bool visible;
    public IReadOnlyList<PanelAction> Actions => actions;
    /// <summary>The sliders (the game saves their values in its user config, by label).</summary>
    public IReadOnlyList<Slider> Sliders => sliders;
    /// <summary>The checkboxes (saved like the sliders, by label).</summary>
    public IReadOnlyList<Toggle> Toggles => toggles;

    /// <summary>Sets every slider back to its value when the panel was made.</summary>
    public void Reset()
    {
        for (int i = 0; i < sliders.Count; i++) sliders[i].Set(defaults[i]);
        for (int i = 0; i < toggles.Count; i++) if (toggles[i].Get() != toggleDefaults[i]) toggles[i].Set(toggleDefaults[i]);
    }

    float TogglesTop => panelY0 + Pad + overlay.LineHeight * 1.5f + Math.Max(Rows, sliders.Count - mainCount) * rowHeight;
    /// <summary>The checkboxes' heading, then their rows (down the first column, then the second, as the sliders).</summary>
    int ToggleRows => (toggles.Count + Columns - 1) / Columns;
    float ToggleHeight => overlay.LineHeight + 8;
    float TogglesHeight => toggles.Count == 0 ? 0 : overlay.LineHeight * 1.5f + ToggleRows * ToggleHeight + RowGap;
    float ToggleTop(int i) => TogglesTop + overlay.LineHeight * 1.5f + i % ToggleRows * ToggleHeight;
    float ToggleX(int i) => panelX0 + Pad + i / ToggleRows * (columnWidth + ColumnGap);
    float ButtonTop => TogglesTop + TogglesHeight;
    float ButtonBottom => ButtonTop + overlay.LineHeight + 8;
    /// <summary>The question with Yes / No, or the last result: a row under the buttons while there is one.</summary>
    string? Notice => confirming >= 0 ? question : message;
    float NoticeTop => ButtonBottom + RowGap;
    float NoticeBottom => Notice is null ? ButtonBottom : NoticeTop + overlay.LineHeight + 8;
    const string Yes = "Yes", No = "No";
    float BoxWidth(string label) => label.Length * overlay.CharWidth + 16;
    float ActionX(int i) => panelX0 + Pad + ButtonWidth + 8 + actions.Take(i).Sum(a => BoxWidth(a.Label) + 8);
    float YesX => panelX0 + Pad + (question?.Length ?? 0) * overlay.CharWidth + 16;
    float NoX => YesX + BoxWidth(Yes) + 8;
    static bool In(Vector2 p, float x0, float y0, float x1, float y1) => p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1;

    int dragging = -1;
    float panelX0, panelY0, panelX1, panelY1, columnWidth;
    float rowHeight => overlay.LineHeight + TrackHeight + RowGap;

    float ButtonWidth => ResetLabel.Length * overlay.CharWidth + 16;

    float LabelWidth => Math.Max(sliders.Max(s => s.Label.Length + 9), toggles.Count == 0 ? 0 : toggles.Max(t => t.Label.Length + 4 + (t.Text?.Invoke().Length ?? 0) + 2)) * overlay.CharWidth;

    /// <summary>The main sliders run down the first column, then the second.</summary>
    int Rows => (mainCount + Columns - 1) / Columns;

    void Layout(int width)
    {
        columnWidth = Math.Max(LabelWidth, MinTrackWidth);
        panelX0 = Margin;
        panelX1 = panelX0 + 2 * Pad + PanelColumns * columnWidth + (PanelColumns - 1) * ColumnGap;
        panelY0 = Margin;
        panelY1 = NoticeBottom + Pad;
        // A long question or result widens the panel.
        if (Notice is { } notice) panelX1 = Math.Max(panelX1, (confirming >= 0 ? NoX + BoxWidth(No) : panelX0 + Pad + notice.Length * overlay.CharWidth) + Pad);
    }

    float TrackX(int i) => panelX0 + Pad + (i < mainCount ? i / Rows : Columns) * (columnWidth + ColumnGap);
    float TrackTop(int i) => panelY0 + Pad + overlay.LineHeight * 1.5f + (i < mainCount ? i % Rows : i - mainCount) * rowHeight + overlay.LineHeight + 2;

    /// <summary>Whether a point (window pixels) is on the panel.</summary>
    public bool Contains(Vector2 p) => Visible && p.X >= panelX0 && p.X <= panelX1 && p.Y >= panelY0 && p.Y <= panelY1;

    /// <summary>Starts dragging the slider under the pointer; true when the panel took the click.</summary>
    public bool MouseDown(Vector2 p)
    {
        if (!Contains(p)) return false;
        if (confirming >= 0)
        {
            // Only Yes runs the action; any other click on the panel answers No.
            if (In(p, YesX, NoticeTop, YesX + BoxWidth(Yes), NoticeBottom)) message = actions[confirming].Run();
            confirming = -1;
            question = null;
            return true;
        }
        message = null;
        if (p.Y >= ButtonTop && p.Y <= ButtonBottom && p.X >= panelX0 + Pad && p.X <= panelX0 + Pad + ButtonWidth)
        {
            Reset();
            return true;
        }
        for (int i = 0; i < actions.Count; i++)
            if (In(p, ActionX(i), ButtonTop, ActionX(i) + BoxWidth(actions[i].Label), ButtonBottom))
            {
                (confirming, question) = (i, actions[i].Question());
                return true;
            }
        for (int i = 0; i < toggles.Count; i++)
        {
            float top = ToggleTop(i), x = ToggleX(i);
            if (p.Y >= top - 2 && p.Y < top + ToggleHeight - 2 && p.X >= x - Pad / 2 && p.X <= x + columnWidth + Pad / 2)
            {
                toggles[i].Set(!toggles[i].Get());
                return true;
            }
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
        if (toggles.Count > 0) overlay.Text(togglesTitle, panelX0 + Pad, TogglesTop, DebugOverlay.TextColour);
        for (int i = 0; i < toggles.Count; i++)
        {
            var t = toggles[i];
            float top = ToggleTop(i), x = ToggleX(i), box = overlay.LineHeight - 2;
            overlay.Rect(x, top, x + box, top + box, track);
            if (t.Get()) overlay.Rect(x + 3, top + 3, x + box - 3, top + box - 3, fill);
            overlay.Text(t.Label, x + box + overlay.CharWidth, top, DebugOverlay.TextColour);
            if (t.Text?.Invoke() is { } text) overlay.Text(text, x + columnWidth - text.Length * overlay.CharWidth, top, dim);
        }
        overlay.Rect(panelX0 + Pad, ButtonTop, panelX0 + Pad + ButtonWidth, ButtonBottom, track);
        overlay.Text(ResetLabel, panelX0 + Pad + 8, ButtonTop + 4, DebugOverlay.TextColour);
        for (int i = 0; i < actions.Count; i++)
        {
            float x = ActionX(i);
            overlay.Rect(x, ButtonTop, x + BoxWidth(actions[i].Label), ButtonBottom, i == confirming ? fill * new Vector4(0.6f, 0.6f, 0.6f, 1) : track);
            overlay.Text(actions[i].Label, x + 8, ButtonTop + 4, DebugOverlay.TextColour);
        }
        if (Notice is { } notice)
        {
            overlay.Text(notice, panelX0 + Pad, NoticeTop + 4, confirming >= 0 ? fill : dim);
            if (confirming >= 0)
            {
                overlay.Rect(YesX, NoticeTop, YesX + BoxWidth(Yes), NoticeBottom, track);
                overlay.Text(Yes, YesX + 8, NoticeTop + 4, DebugOverlay.TextColour);
                overlay.Rect(NoX, NoticeTop, NoX + BoxWidth(No), NoticeBottom, track);
                overlay.Text(No, NoX + 8, NoticeTop + 4, DebugOverlay.TextColour);
            }
        }
        overlay.Flush(width, height);
    }
}
