using System.Numerics;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Settings;

namespace Meitou.Game.Host.Photo;

/// <summary>
/// Photo mode's panel at the left of the picture and the guides over it: drawn through the overlay in the settings window's inks, with sliders,
/// switches and buttons the mouse works (the same settings as <c>photo set</c>). The panel, the grid and the message are left out of a saved
/// picture; the frame bars are not.
/// </summary>
sealed class PhotoPanel(PhotoMode mode)
{
    #region State

    static readonly Vector4 Window = new(0.12f, 0.11f, 0.09f, 0.9f);
    static readonly Vector4 Track = new(0.26f, 0.24f, 0.19f, 1);
    static readonly Vector4 Button = new(0.2f, 0.18f, 0.14f, 1);
    static readonly Vector4 ButtonHover = new(0.3f, 0.27f, 0.2f, 1);
    static readonly Vector4 Disabled = new(0.34f, 0.32f, 0.28f, 1);

    const string Hints = "WASD move, R / F down and up, right mouse look, wheel fly speed, Shift faster, Alt slower, Z / X roll, PageUp / PageDown lens, "
        + "F7 hides this panel, Enter or F8 takes the picture, P or Esc leaves.";

    /// <summary>
    /// What the last drawn frame laid out for the mouse: a rectangle and what a press on it does; a slider also keeps its setting and the track's
    /// ends, so a drag follows the pointer.
    /// </summary>
    readonly record struct Hit(Vector4 Area, Action Press, PhotoMode.Param? Slider, float TrackLeft, float TrackRight);

    readonly List<Hit> _hits = [];
    Vector4 _panel;
    Vector2 _pointer;
    Hit? _drag;

    #endregion

    #region Mouse

    /// <summary>
    /// The pointer is over the drawn panel (it takes the wheel and the clicks).
    /// </summary>
    public bool Contains(Vector2 p) => mode.PanelShown && p.X >= _panel.X && p.X <= _panel.Z && p.Y >= _panel.Y && p.Y <= _panel.W;

    /// <summary>
    /// A press at <paramref name="p"/>: what the panel does with it; false when it is outside.
    /// </summary>
    public bool MouseDown(Vector2 p)
    {
        _pointer = p;
        if (!Contains(p)) return false;
        foreach (var hit in _hits)
        {
            if (!Inside(hit.Area, p)) continue;
            hit.Press();
            if (hit.Slider is not null)
            {
                _drag = hit;
                Slide(hit, p.X);
            }
            break;
        }
        return true;
    }

    /// <summary>
    /// The pointer moved: a slider being dragged follows it; true while one is, or over the panel.
    /// </summary>
    public bool MouseMove(Vector2 p)
    {
        _pointer = p;
        if (_drag is { } hit)
        {
            Slide(hit, p.X);
            return true;
        }
        return Contains(p);
    }

    /// <summary>
    /// The button was released.
    /// </summary>
    public void MouseUp() => _drag = null;

    static bool Inside(Vector4 a, Vector2 p) => p.X >= a.X && p.X <= a.Z && p.Y >= a.Y && p.Y <= a.W;

    static void Slide(Hit hit, float x)
    {
        var p = hit.Slider!;
        float t = Math.Clamp((x - hit.TrackLeft) / Math.Max(hit.TrackRight - hit.TrackLeft, 1), 0, 1);
        p.Set(p.Log ? p.Min * MathF.Pow(p.Max / p.Min, t) : p.Min + (p.Max - p.Min) * t);
    }

    #endregion

    #region Drawing

    /// <summary>
    /// The frame's frame bars, grid, panel and message over the finished picture; <paramref name="saving"/> leaves out all but the bars.
    /// </summary>
    public void Draw(DebugOverlay o, int width, int height, bool saving)
    {
        PhotoGuides.DrawFrame(o, mode.Frame, width, height);
        _hits.Clear();
        _panel = default;
        if (!mode.GuidesDrawn(saving)) return;
        float u = Math.Max(0.75f, height / 1080f);
        PhotoGuides.DrawGrid(o, mode.Grid, PhotoGuides.Window(mode.Frame, width, height), Math.Max(1.5f, 1.5f * u));
        if (mode.PanelShown) DrawPanel(o, u);
        DrawMessage(o, width, height, u);
    }

    void DrawPanel(DebugOverlay o, float u)
    {
        float lh = o.LineHeight * u, x0 = 16 * u, y0 = 16 * u, w = 340 * u, pad = 12 * u, gap = 6 * u;
        var hints = o.WrapText(Hints, w - 2 * pad, u);
        float rows = mode.Params.Sum(p => RowHeight(p, lh, u) + gap);
        float inner = lh + 8 * u + rows + 2 * (lh + 14 * u) + hints.Count * lh + 2 * pad;
        _panel = new Vector4(x0, y0, x0 + w, y0 + inner);
        o.Rect(x0 - 1, y0 - 1, x0 + w + 1, y0 + inner + 1, SettingsCanvas.Brass * new Vector4(1, 1, 1, 0.55f));
        o.Rect(x0, y0, x0 + w, y0 + inner, Window);

        float x = x0 + pad, right = x0 + w - pad, y = y0 + pad;
        o.TextScaled("Photo mode", x, y, SettingsCanvas.Brass, u);
        y += lh + 8 * u;
        foreach (var p in mode.Params)
        {
            y += DrawRow(o, p, x, right, y, lh, u) + gap;
        }
        y = DrawButtons(o, x, right, y, lh, u);
        foreach (string line in hints)
        {
            o.TextScaled(line, x, y, SettingsCanvas.MutedInk, u);
            y += lh;
        }
    }

    float RowHeight(PhotoMode.Param p, float lh, float u) => p.Choices is null ? lh + 16 * u : lh + 8 * u;

    float DrawRow(DebugOverlay o, PhotoMode.Param p, float x, float right, float y, float lh, float u)
    {
        bool enabled = p.Enabled?.Invoke() ?? true;
        var ink = enabled ? SettingsCanvas.MainInk : Disabled;
        o.TextScaled(p.Label, x, y + 4 * u, ink, u);
        if (p.Choices is { } choices) return DrawChoice(o, p, choices, right, y, lh, u);
        string text = PhotoMode.ValueText(p);
        o.TextScaled(text, right - o.MeasureText(text, u), y, enabled ? SettingsCanvas.LightInk : Disabled, u);
        float trackY = y + lh + 4 * u, trackH = 6 * u;
        o.Rect(x, trackY, right, trackY + trackH, Track);
        float t = Fraction(p);
        float handle = x + (right - x) * t;
        o.Rect(x, trackY, handle, trackY + trackH, enabled ? SettingsCanvas.Brass * new Vector4(1, 1, 1, 0.7f) : Disabled);
        o.Rect(handle - 3 * u, trackY - 3 * u, handle + 3 * u, trackY + trackH + 3 * u, enabled ? SettingsCanvas.LightInk : Disabled);
        if (enabled)
            _hits.Add(new Hit(new Vector4(x, y, right, trackY + trackH + 4 * u), () => { }, p, x, right));
        return RowHeight(p, lh, u);
    }

    static float Fraction(PhotoMode.Param p)
    {
        float v = p.Get();
        float t = p.Log ? MathF.Log(Math.Max(v, p.Min) / p.Min) / MathF.Log(p.Max / p.Min) : (v - p.Min) / (p.Max - p.Min);
        return Math.Clamp(t, 0, 1);
    }

    float DrawChoice(DebugOverlay o, PhotoMode.Param p, string[] choices, float right, float y, float lh, float u)
    {
        float width = 150 * u, height = lh + 6 * u;
        var area = new Vector4(right - width, y, right, y + height);
        string text = PhotoMode.ValueText(p);
        DrawButton(o, area, text, lh, u);
        int count = choices.Length;
        _hits.Add(new Hit(area, () => p.Set((MathF.Round(p.Get()) + 1) % count), null, 0, 0));
        return RowHeight(p, lh, u);
    }

    float DrawButtons(DebugOverlay o, float x, float right, float y, float lh, float u)
    {
        float h = lh + 8 * u, half = (right - x - 6 * u) / 2;
        var take = new Vector4(x, y, right, y + h);
        DrawButton(o, take, "Take picture", lh, u);
        _hits.Add(new Hit(take, () => mode.TakePicture(), null, 0, 0));
        y += h + 6 * u;
        (string Label, Action Press)[] buttons = [("Game time", mode.UseGameTime), ("Reset", mode.Reset)];
        for (int i = 0; i < buttons.Length; i++)
        {
            float left = x + i * (half + 6 * u);
            var area = new Vector4(left, y, left + half, y + h);
            DrawButton(o, area, buttons[i].Label, lh, u);
            _hits.Add(new Hit(area, buttons[i].Press, null, 0, 0));
        }
        return y + h + 8 * u;
    }

    void DrawButton(DebugOverlay o, Vector4 area, string text, float lh, float u)
    {
        o.Rect(area.X, area.Y, area.Z, area.W, Inside(area, _pointer) ? ButtonHover : Button);
        string fitted = o.FitText(text, area.Z - area.X - 8 * u, u);
        float tx = area.X + (area.Z - area.X - o.MeasureText(fitted, u)) / 2;
        o.TextScaled(fitted, tx, area.Y + (area.W - area.Y - lh) / 2, SettingsCanvas.LightInk, u);
    }

    void DrawMessage(DebugOverlay o, int width, int height, float u)
    {
        if (mode.Message is not { } message) return;
        float lh = o.LineHeight * u;
        string text = o.FitText(message, width - 60 * u, u);
        float w = o.MeasureText(text, u), x = (width - w) / 2, y = height - lh - 40 * u;
        o.Rect(x - 12 * u, y - 6 * u, x + w + 12 * u, y + lh + 6 * u, Window);
        o.TextScaled(text, x, y, SettingsCanvas.LightInk, u);
    }

    #endregion
}
