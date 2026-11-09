using System.Numerics;
using Meitou.Game.Rendering.Overlays;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// One slider: a value between <see cref="Min"/> and <see cref="Max"/>, read and written through the callbacks. <see cref="Faithful"/> and
/// <see cref="Meitou"/>, when known, are the game's value and Meitou's (the settings menu marks them on the track and offers a quick switch between
/// them). <see cref="Reversed"/>: the value falls as the setting improves (terrain detail's error in pixels), so the track runs from
/// <see cref="Max"/> on the left to <see cref="Min"/> on the right.
/// <see cref="Scale"/>: what <see cref="Text"/> multiplies the value by (100 for a percentage), so a typed number is in the units shown.
/// </summary>
public sealed record Slider(string Label, float Min, float Max, Func<float> Get, Action<float> Set, string Format = "0.##", bool Logarithmic = false,
    Func<float, string>? Text = null, float? Faithful = null, float? Meitou = null, bool Reversed = false, float Scale = 1, float Step = 0)
{
    /// <summary>
    /// One arrow-key step as a fraction of the track: <see cref="Step"/> when given, else 1 on a range of 4 or less, else 2%.
    /// </summary>
    public float KeyStep => Step > 0 ? Step / (Max - Min) : Max - Min <= 4 ? 1 / (Max - Min) : 0.02f;

    /// <summary>
    /// The value as the panel shows it (<see cref="Text"/> when given, else <see cref="Format"/>).
    /// </summary>
    public string Display => Text?.Invoke(Get()) ?? Get().ToString(Format, System.Globalization.CultureInfo.InvariantCulture);

    public float Fraction => FractionOf(Get());

    /// <summary>
    /// Where <paramref name="value"/> sits on the track (0 to 1).
    /// </summary>
    public float FractionOf(float value)
    {
        float v = Math.Clamp(value, Min, Max);
        float t = Logarithmic ? MathF.Log(v / Min) / MathF.Log(Max / Min) : (v - Min) / (Max - Min);
        return Reversed ? 1 - t : t;
    }

    public void SetFraction(float t)
    {
        t = Math.Clamp(t, 0, 1);
        if (Reversed) t = 1 - t;
        Set(Logarithmic ? Min * MathF.Pow(Max / Min, t) : Min + (Max - Min) * t);
    }
}

/// <summary>
/// One checkbox: on or off, read and written through the callbacks; <see cref="Text"/> describes the state beside it. <see cref="Remaster"/>: a
/// Faithful / Meitou switch (on: Meitou); <see cref="OnOff"/>: one whose Faithful side is the feature off (the game has none), so its sides also read
/// off / on. <see cref="Optional"/>: an optional effect, in neither the game nor the Meitou look (bloom): it reads Off / On, both looks have it off
/// and a look preset may turn it on.
/// </summary>
public sealed record Toggle(string Label, Func<bool> Get, Action<bool> Set, Func<string>? Text = null, bool Remaster = false, bool OnOff = false,
    bool Optional = false);

/// <summary>
/// A button beside the reset one that asks first: <see cref="Question"/> is shown with Yes / No, Yes runs <see cref="Run"/>, whose text is shown
/// after.
/// </summary>
public sealed record PanelAction(string Label, Func<string> Question, Func<string> Run);

/// <summary>
/// A drop-down list after the action buttons: <see cref="Label"/> on the button, a click opens <see cref="Options"/> below it with the
/// <see cref="Current"/> one marked "x", and a click on an option runs <see cref="Choose"/>, whose text is shown after.
/// </summary>
public sealed record PanelChoice(Func<string> Label, Func<IReadOnlyList<string>> Options, Func<int> Current, Func<int, string> Choose);

/// <summary>
/// A panel of sliders drawn with the <see cref="DebugOverlay"/> in three columns in the top-left corner (the main sliders, then <c>sideSliders</c> at
/// the end, down the first column, then the next), over the other panels. Drag a slider with the left mouse button; while the pointer is on the
/// panel, the camera ignores the mouse. The button at the bottom puts every slider back to the value it had when the panel was made (before a saved
/// config or a drag changed it). Below the sliders, optional checkboxes under their own heading (the Faithful / Meitou switches), toggled with a
/// click and reset with the sliders. Beside the reset button, optional
/// <see cref="PanelAction"/> buttons: a click shows the action's question with Yes / No on a row below, and the result there after Yes; then optional
/// <see cref="PanelChoice"/> drop-down lists.
/// </summary>
public sealed class SettingsPanel(DebugOverlay overlay, string title, IReadOnlyList<Slider> mainSliders, IReadOnlyList<Toggle>? toggles = null,
    string togglesTitle = "",
    IReadOnlyList<PanelAction>? actions = null, IReadOnlyList<Slider>? sideSliders = null, IReadOnlyList<PanelChoice>? choices = null)
{
    const float Margin = 16, Pad = 12, TrackHeight = 6, RowGap = 10, ColumnGap = 28, MinTrackWidth = 220;

    /// <summary>
    /// The sliders' and the checkboxes' columns.
    /// </summary>
    const int Columns = 3;

    /// <summary>
    /// The main sliders, then the side ones.
    /// </summary>
    readonly IReadOnlyList<Slider> _sliders = [.. mainSliders, .. sideSliders ?? []];

    /// <summary>
    /// Nearly opaque: the panel is drawn over the statistics and the profiler.
    /// </summary>
    static readonly Vector4 Background = new(0.05f, 0.05f, 0.06f, 0.95f);
    const string ResetLabel = "Reset to defaults";

    readonly float[] _defaults = [.. mainSliders.Concat(sideSliders ?? []).Select(s => s.Get())];
    readonly IReadOnlyList<Toggle> _toggles = toggles ?? [];
    readonly bool[] _toggleDefaults = (toggles ?? []).Select(t => t.Get()).ToArray();

    readonly IReadOnlyList<PanelAction> _actions = actions ?? [];

    /// <summary>
    /// The action waiting for Yes / No (-1 none), its question, and the last action's result (shown until the next click on the panel).
    /// </summary>
    int _confirming = -1;
    string? _question, _message;
    readonly IReadOnlyList<PanelChoice> _choices = choices ?? [];

    /// <summary>
    /// The open drop-down list (-1 none) and its options as they were when it opened.
    /// </summary>
    int _open = -1;
    IReadOnlyList<string> _openOptions = [];

    /// <summary>
    /// Hiding the panel drops a pending question and the last result.
    /// </summary>
    public bool Visible
    {
        get => _visible;
        set { _visible = value; if (!value) { _confirming = -1; _open = -1; _question = _message = null; } }
    }
    bool _visible;
    public IReadOnlyList<PanelAction> Actions => _actions;

    /// <summary>
    /// The sliders (the game saves their values in its user config, by label).
    /// </summary>
    public IReadOnlyList<Slider> Sliders => _sliders;

    /// <summary>
    /// The checkboxes (saved like the sliders, by label).
    /// </summary>
    public IReadOnlyList<Toggle> Toggles => _toggles;
    public IReadOnlyList<PanelChoice> Choices => _choices;

    /// <summary>
    /// Sets every slider back to its value when the panel was made.
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i < _sliders.Count; i++) _sliders[i].Set(_defaults[i]);
        for (int i = 0; i < _toggles.Count; i++) if (_toggles[i].Get() != _toggleDefaults[i]) _toggles[i].Set(_toggleDefaults[i]);
    }

    float TogglesTop => _panelY0 + Pad + overlay.LineHeight * 1.5f + Rows * RowHeight;

    /// <summary>
    /// The checkboxes' heading, then their rows (down the first column, then the next, as the sliders).
    /// </summary>
    int ToggleRows => (_toggles.Count + Columns - 1) / Columns;
    float ToggleHeight => overlay.LineHeight + 8;
    float TogglesHeight => _toggles.Count == 0 ? 0 : overlay.LineHeight * 1.5f + ToggleRows * ToggleHeight + RowGap;
    float ToggleTop(int i) => TogglesTop + overlay.LineHeight * 1.5f + i % ToggleRows * ToggleHeight;
    float ToggleX(int i) => _panelX0 + Pad + i / ToggleRows * (_columnWidth + ColumnGap);
    float ButtonTop => TogglesTop + TogglesHeight;
    float ButtonBottom => ButtonTop + overlay.LineHeight + 8;

    /// <summary>
    /// The question with Yes / No, or the last result: a row under the buttons while there is one.
    /// </summary>
    string? Notice => _confirming >= 0 ? _question : _message;

    /// <summary>
    /// The open list's rows, under the buttons.
    /// </summary>
    float ListTop => ButtonBottom + 2;
    float ListHeight => _open < 0 ? 0 : _openOptions.Count * ToggleHeight + 2;
    float ListWidth => _open < 0 ? 0 : ListWidthFor(_open);
    float NoticeTop => ButtonBottom + RowGap + ListHeight;
    float ListWidthFor(int i) => Math.Max(BoxWidth(ChoiceText(_choices[i])), _openOptions.Select(o => BoxWidth("x " + o)).DefaultIfEmpty(0).Max());
    float NoticeBottom => Notice is null ? ListTop + ListHeight : NoticeTop + overlay.LineHeight + 8;
    const string Yes = "Yes", No = "No";
    float BoxWidth(string label) => label.Length * overlay.CharWidth + 16;
    float ActionX(int i) => _panelX0 + Pad + ButtonWidth + 8 + _actions.Take(i).Sum(a => BoxWidth(a.Label) + 8);
    float ChoiceX(int i) => ActionX(_actions.Count) + _choices.Take(i).Sum(c => BoxWidth(ChoiceText(c)) + 8);
    static string ChoiceText(PanelChoice c) => c.Label() + " v";
    float YesX => _panelX0 + Pad + (_question?.Length ?? 0) * overlay.CharWidth + 16;
    float NoX => YesX + BoxWidth(Yes) + 8;
    static bool In(Vector2 p, float x0, float y0, float x1, float y1) => p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1;

    int _dragging = -1;
    float _panelX0, _panelY0, _panelX1, _panelY1, _columnWidth;
    float RowHeight => overlay.LineHeight + TrackHeight + RowGap;

    float ButtonWidth => ResetLabel.Length * overlay.CharWidth + 16;

    float LabelWidth => Math.Max(_sliders.Max(s => s.Label.Length + 9),
        _toggles.Count == 0 ? 0 : _toggles.Max(t => t.Label.Length + 4 + (t.Text?.Invoke().Length ?? 0) + 2)) * overlay.CharWidth;

    /// <summary>
    /// The sliders run down the first column, then the second and the third.
    /// </summary>
    int Rows => (_sliders.Count + Columns - 1) / Columns;

    void Layout(int width)
    {
        _columnWidth = Math.Max(LabelWidth, MinTrackWidth);
        _panelX0 = Margin;
        _panelX1 = _panelX0 + 2 * Pad + Columns * _columnWidth + (Columns - 1) * ColumnGap;
        _panelY0 = Margin;
        _panelY1 = NoticeBottom + Pad;
        if (_open >= 0) _panelX1 = Math.Max(_panelX1, ChoiceX(_open) + ListWidth + Pad);
        if (_choices.Count > 0) _panelX1 = Math.Max(_panelX1, ChoiceX(_choices.Count) + Pad);
        // A long question or result widens the panel.
        if (Notice is { } notice)
            _panelX1 = Math.Max(_panelX1, (_confirming >= 0 ? NoX + BoxWidth(No) : _panelX0 + Pad + notice.Length * overlay.CharWidth) + Pad);
    }

    float TrackX(int i) => _panelX0 + Pad + i / Rows * (_columnWidth + ColumnGap);
    float TrackTop(int i) => _panelY0 + Pad + overlay.LineHeight * 1.5f + i % Rows * RowHeight + overlay.LineHeight + 2;

    /// <summary>
    /// Whether a point (window pixels) is on the panel.
    /// </summary>
    public bool Contains(Vector2 p) => Visible && p.X >= _panelX0 && p.X <= _panelX1 && p.Y >= _panelY0 && p.Y <= _panelY1;

    /// <summary>
    /// Starts dragging the slider under the pointer; true when the panel took the click.
    /// </summary>
    public bool MouseDown(Vector2 p)
    {
        if (!Contains(p)) return false;
        if (_confirming >= 0)
        {
            // Only Yes runs the action; any other click on the panel answers No.
            if (In(p, YesX, NoticeTop, YesX + BoxWidth(Yes), NoticeBottom)) _message = _actions[_confirming].Run();
            _confirming = -1;
            _question = null;
            return true;
        }
        _message = null;
        if (_open >= 0)
        {
            // A click on an option chooses it; any other click closes the list (and goes on to what it hit, unless that was the list's own button).
            int was = _open;
            _open = -1;
            float x = ChoiceX(was);
            for (int k = 0; k < _openOptions.Count; k++)
                if (In(p, x, ListTop + k * ToggleHeight, x + ListWidthFor(was), ListTop + (k + 1) * ToggleHeight))
                {
                    _message = _choices[was].Choose(k);
                    return true;
                }
            if (In(p, x, ButtonTop, x + BoxWidth(ChoiceText(_choices[was])), ButtonBottom)) return true;
        }
        for (int i = 0; i < _choices.Count; i++)
            if (In(p, ChoiceX(i), ButtonTop, ChoiceX(i) + BoxWidth(ChoiceText(_choices[i])), ButtonBottom))
            {
                (_open, _openOptions) = (i, _choices[i].Options());
                return true;
            }
        if (p.Y >= ButtonTop && p.Y <= ButtonBottom && p.X >= _panelX0 + Pad && p.X <= _panelX0 + Pad + ButtonWidth)
        {
            Reset();
            return true;
        }
        for (int i = 0; i < _actions.Count; i++)
            if (In(p, ActionX(i), ButtonTop, ActionX(i) + BoxWidth(_actions[i].Label), ButtonBottom))
            {
                (_confirming, _question) = (i, _actions[i].Question());
                return true;
            }
        for (int i = 0; i < _toggles.Count; i++)
        {
            float top = ToggleTop(i), x = ToggleX(i);
            if (p.Y >= top - 2 && p.Y < top + ToggleHeight - 2 && p.X >= x - Pad / 2 && p.X <= x + _columnWidth + Pad / 2)
            {
                _toggles[i].Set(!_toggles[i].Get());
                return true;
            }
        }
        for (int i = 0; i < _sliders.Count; i++)
        {
            float top = TrackTop(i), x = TrackX(i);
            if (p.Y >= top - overlay.LineHeight - 2 && p.Y <= top + TrackHeight + RowGap * 0.5f
                && p.X >= x - Pad / 2 && p.X <= x + _columnWidth + Pad / 2)
            {
                _dragging = i;
                MouseMove(p);
                break;
            }
        }
        return true;
    }

    /// <summary>
    /// Moves the dragged slider; true while one is being dragged.
    /// </summary>
    public bool MouseMove(Vector2 p)
    {
        if (_dragging < 0) return false;
        _sliders[_dragging].SetFraction((p.X - TrackX(_dragging)) / _columnWidth);
        return true;
    }

    /// <summary>
    /// Ends a drag; true when one was in progress.
    /// </summary>
    public bool MouseUp()
    {
        bool was = _dragging >= 0;
        _dragging = -1;
        return was;
    }

    public void Draw(int width, int height)
    {
        if (!Visible) return;
        Layout(width);
        Vector4 track = new(0.3f, 0.3f, 0.32f, 1), fill = new(0.85f, 0.65f, 0.35f, 1), dim = new(0.7f, 0.7f, 0.68f, 1);
        overlay.Rect(_panelX0, _panelY0, _panelX1, _panelY1, Background);
        overlay.Text(title, _panelX0 + Pad, _panelY0 + Pad, DebugOverlay.TextColour);
        DrawSliders(track, fill, dim);
        DrawToggles(track, fill, dim);
        DrawButtons(track, fill);
        DrawChoices(track, fill);
        DrawOpenList(fill);
        DrawNotice(track, fill, dim);
        overlay.Flush(width, height);
    }

    void DrawSliders(Vector4 track, Vector4 fill, Vector4 dim)
    {
        for (int i = 0; i < _sliders.Count; i++)
        {
            var s = _sliders[i];
            float top = TrackTop(i), x = TrackX(i);
            overlay.Text(s.Label, x, top - overlay.LineHeight - 2, i == _dragging ? fill : DebugOverlay.TextColour);
            string value = s.Display;
            overlay.Text(value, x + _columnWidth - value.Length * overlay.CharWidth, top - overlay.LineHeight - 2, dim);
            float t = s.Fraction;
            overlay.Rect(x, top, x + _columnWidth, top + TrackHeight, track);
            overlay.Rect(x, top, x + _columnWidth * t, top + TrackHeight, fill);
            float knob = x + _columnWidth * t;
            overlay.Rect(knob - 3, top - 4, knob + 3, top + TrackHeight + 4, DebugOverlay.TextColour);
        }
    }

    void DrawToggles(Vector4 track, Vector4 fill, Vector4 dim)
    {
        if (_toggles.Count > 0) overlay.Text(togglesTitle, _panelX0 + Pad, TogglesTop, DebugOverlay.TextColour);
        for (int i = 0; i < _toggles.Count; i++)
        {
            var t = _toggles[i];
            float top = ToggleTop(i), x = ToggleX(i), box = overlay.LineHeight - 2;
            overlay.Rect(x, top, x + box, top + box, track);
            if (t.Get()) overlay.Rect(x + 3, top + 3, x + box - 3, top + box - 3, fill);
            overlay.Text(t.Label, x + box + overlay.CharWidth, top, DebugOverlay.TextColour);
            if (t.Text?.Invoke() is { } text)
                overlay.Text(text, x + _columnWidth - text.Length * overlay.CharWidth, top, dim);
        }
    }

    void DrawButtons(Vector4 track, Vector4 fill)
    {
        overlay.Rect(_panelX0 + Pad, ButtonTop, _panelX0 + Pad + ButtonWidth, ButtonBottom, track);
        overlay.Text(ResetLabel, _panelX0 + Pad + 8, ButtonTop + 4, DebugOverlay.TextColour);
        for (int i = 0; i < _actions.Count; i++)
        {
            float x = ActionX(i);
            overlay.Rect(x, ButtonTop, x + BoxWidth(_actions[i].Label), ButtonBottom, i == _confirming ? fill * new Vector4(0.6f, 0.6f, 0.6f,
                1) : track);
            overlay.Text(_actions[i].Label, x + 8, ButtonTop + 4, DebugOverlay.TextColour);
        }
    }

    void DrawChoices(Vector4 track, Vector4 fill)
    {
        for (int i = 0; i < _choices.Count; i++)
        {
            float x = ChoiceX(i);
            string label = ChoiceText(_choices[i]);
            overlay.Rect(x, ButtonTop, x + BoxWidth(label), ButtonBottom, i == _open ? fill * new Vector4(0.6f, 0.6f, 0.6f, 1) : track);
            overlay.Text(label, x + 8, ButtonTop + 4, DebugOverlay.TextColour);
        }
    }

    void DrawOpenList(Vector4 fill)
    {
        if (_open >= 0)
        {
            float x = ChoiceX(_open), w = ListWidthFor(_open);
            int current = _choices[_open].Current();
            overlay.Rect(x, ListTop, x + w, ListTop + ListHeight, new Vector4(0.12f, 0.12f, 0.13f, 1));
            for (int k = 0; k < _openOptions.Count; k++)
                overlay.Text((k == current ? "x " : "  ") + _openOptions[k], x + 8, ListTop + k * ToggleHeight + 4,
                        k == current ? fill : DebugOverlay.TextColour);
        }
    }

    void DrawNotice(Vector4 track, Vector4 fill, Vector4 dim)
    {
        if (Notice is { } notice)
        {
            overlay.Text(notice, _panelX0 + Pad, NoticeTop + 4, _confirming >= 0 ? fill : dim);
            if (_confirming >= 0)
            {
                overlay.Rect(YesX, NoticeTop, YesX + BoxWidth(Yes), NoticeBottom, track);
                overlay.Text(Yes, YesX + 8, NoticeTop + 4, DebugOverlay.TextColour);
                overlay.Rect(NoX, NoticeTop, NoX + BoxWidth(No), NoticeBottom, track);
                overlay.Text(No, NoX + 8, NoticeTop + 4, DebugOverlay.TextColour);
            }
        }
    }
}