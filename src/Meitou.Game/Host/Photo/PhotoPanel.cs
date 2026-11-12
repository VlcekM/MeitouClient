using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Settings;

namespace Meitou.Game.Host.Photo;

/// <summary>
/// Photo mode's panel, a bar along the bottom of the picture with a tab for each kind of setting, and the guides over the picture. The panel is drawn as the settings window is: the interface's painter,
/// its window, button and paper surfaces (the game's skins in the Faithful interface), its font and inks, with sliders, switches and buttons the
/// mouse works (the same settings as <c>photo set</c>). The panel, the grid and the message are left out of a saved picture; the frame bars are not.
/// </summary>
sealed class PhotoPanel(PhotoMode mode)
{
    #region State

    static readonly Vector4 DarkInk = new(20 / 255f, 8 / 255f, 6 / 255f, 1);
    static readonly Vector4 Groove = new(0.10f, 0.08f, 0.07f, 1);

    const string Hints = "WASD move    Q / E down, up    Right mouse look    Left click focus    Wheel zoom    Ctrl + wheel fly speed    Shift fast    "
        + "Ctrl slow    Z / X roll    V level    Space pause    Tab hide    Enter picture    P / Esc leave";

    /// <summary>
    /// The panel's measures in the settings window's units (pixels at a 900-line picture): a bar along the bottom of the picture, its tabs over one row
    /// of five settings at most.
    /// </summary>
    const float BarWidth = 1200, BarHeight = 138, BarBottom = 26, Pad = 14, TabWidth = 104, TabGap = 6, CellGap = 24, ChoiceWidth = 104;
    const int Columns = 5;

    /// <summary>
    /// What the last drawn frame laid out for the mouse: a rectangle and what a press on it does; a slider also keeps its setting and the track's
    /// ends, so a drag follows the pointer.
    /// </summary>
    readonly record struct Hit(UiRect Area, Action Press, PhotoMode.Param? Slider, float TrackLeft, float TrackRight, Action<int>? Step = null);

    readonly List<Hit> _hits = [];
    readonly List<UiControl> _controls = [];
    readonly SettingsMenuArt _art = new();
    UiRect _panel;
    Vector2 _pointer;
    Hit? _drag;
    float _scale = 1;
    bool _down, _meitou;
    /// <summary>
    /// The tab shown (an index into <see cref="PhotoMode.Groups"/>).
    /// </summary>
    int _tab;

    #endregion

    #region Mouse

    /// <summary>
    /// The pointer is over the drawn panel (it takes the wheel and the clicks).
    /// </summary>
    public bool Contains(Vector2 p) => mode.PanelShown && _panel.Width > 0 && _panel.Contains(p.X, p.Y);

    /// <summary>
    /// A press at <paramref name="p"/>: what the panel does with it (<paramref name="back"/>, the right button: a choice steps back); false when it is
    /// outside.
    /// </summary>
    public bool MouseDown(Vector2 p, bool back = false)
    {
        _pointer = p;
        if (!Contains(p)) return false;
        _down = !back;
        foreach (var hit in _hits)
        {
            if (!hit.Area.Contains(p.X, p.Y)) continue;
            if (back)
            {
                hit.Step?.Invoke(-1);
                break;
            }

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
    public void MouseUp() => (_drag, _down) = (null, false);

    /// <summary>
    /// The wheel over the panel steps the setting under the pointer: a slider by a fiftieth of its track, a choice by one. True when the pointer is over
    /// the panel (the wheel is then not the camera's).
    /// </summary>
    public bool Wheel(Vector2 p, float notches)
    {
        _pointer = p;
        if (!Contains(p)) return false;
        int steps = notches > 0 ? 1 : -1;
        foreach (var hit in _hits)
        {
            if (!hit.Area.Contains(p.X, p.Y)) continue;
            if (hit.Slider is { } slider) SetFraction(slider, Fraction(slider) + steps * 0.02f);
            else hit.Step?.Invoke(steps);
            break;
        }

        return true;
    }

    static void Slide(Hit hit, float x) => SetFraction(hit.Slider!, (x - hit.TrackLeft) / Math.Max(hit.TrackRight - hit.TrackLeft, 1));

    static void SetFraction(PhotoMode.Param p, float t)
    {
        t = Math.Clamp(t, 0, 1);
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
        if (mode.FocusSpot is { } spot) PhotoGuides.DrawReticle(o, new Vector2(spot.X * width, spot.Y * height), 16 * u, Math.Max(1.5f, 1.5f * u));
        if (mode.Host.Player.SettingsPainter is not { } painter) return;

        // The bars and the grid go under the panel: the painter draws at once, the overlay when it is flushed.
        o.Flush(width, height);
        _meitou = mode.Host.Player.UiMode == UiMode.Meitou;
        _art.Ensure(painter, _meitou, mode.Host.Player.UiAtlas);
        _scale = Math.Max(0.6f, Math.Min(1.25f, Math.Min(height / 900f, width / 1280f)));
        _controls.Clear();
        if (mode.PanelShown) DrawPanel(width, height);
        DrawMessage(width);
        if (_controls.Count > 0) painter.Draw(_controls, width, height, _pointer, null);
    }

    UiRect R(float x, float y, float w, float h) => new(MathF.Round(x * _scale), MathF.Round(y * _scale), MathF.Round(w * _scale), MathF.Round(h * _scale));

    /// <summary>
    /// The bar: the tabs at the left of its top row, Reset and Take picture at the right, the shown tab's settings side by side under them, the keys
    /// on its last line.
    /// </summary>
    void DrawPanel(int width, int height)
    {
        float barWidth = Math.Min(BarWidth, width / _scale - 48), left = (width / _scale - barWidth) / 2, top = height / _scale - BarHeight - BarBottom;
        var window = R(left, top, barWidth, BarHeight);
        Surface(window, "window");
        float margin = _meitou ? 16 * _scale : 0;
        _panel = new(window.X - margin, window.Y - margin, window.Width + 2 * margin, window.Height + 2 * margin);

        float x = left + Pad, inner = barWidth - 2 * Pad, y = top + 12;
        _tab = Math.Clamp(_tab, 0, PhotoMode.Groups.Length - 1);
        for (int i = 0; i < PhotoMode.Groups.Length; i++)
        {
            int index = i;
            Button(PhotoMode.Groups[i], R(x + i * (TabWidth + TabGap), y, TabWidth, 28), () => _tab = index, selected: i == _tab);
        }

        Button("Take picture", R(x + inner - 160, y - 1, 160, 30), () => mode.TakePicture(), primary: true);
        Button("Reset", R(x + inner - 160 - 8 - 90, y, 90, 28), mode.Reset);
        Fill(R(x, y + 36, inner, 1), SettingsCanvas.MainInk with { W = 0.14f });

        y += 46;
        float cell = (inner - (Columns - 1) * CellGap) / Columns, at = x;
        foreach (var p in mode.Params.Where(p => p.Group == PhotoMode.Groups[_tab]))
        {
            if (p.Choices is { } choices) DrawChoice(p, choices, at, cell, y);
            else DrawSlider(p, at, cell, y);
            at += cell + CellGap;
        }

        if (PhotoMode.Groups[_tab] == PhotoMode.LightGroup) Button("Game time", R(at, y + 6, ChoiceWidth, 26), mode.UseGameTime);
        if (PhotoMode.Groups[_tab] == PhotoMode.PresetGroup) DrawPresets(x, y, x + inner);
        Text(Hints, R(x, top + BarHeight - 28, inner, 16), 11, SettingsCanvas.MutedInk, "Center");
    }

    /// <summary>
    /// The presets' tab: Save preset keeps every tab's settings under the next free name; each preset is a button that takes its settings, with a small
    /// one beside it that deletes it.
    /// </summary>
    void DrawPresets(float x, float y, float right)
    {
        Button("Save preset", R(x, y + 6, 124, 26), () => mode.SavePreset());
        x += 124 + CellGap;
        var presets = mode.Presets;
        if (presets.Count == 0)
        {
            Text("No preset yet. Save preset keeps the settings of every tab.", R(x, y + 6, right - x, 26), 13, SettingsCanvas.MutedInk);
            return;
        }

        for (int i = 0; i < presets.Count && x + 146 <= right; i++)
        {
            int index = i;
            Button(presets[i].Name, R(x, y + 6, 118, 26), () => mode.LoadPreset(index), translate: false);
            Button("x", R(x + 120, y + 6, 26, 26), () => mode.DeletePreset(index), translate: false);
            x += 146 + 10;
        }
    }

    /// <summary>
    /// A slider as the settings window's: the name, the value at its right, the bar under them.
    /// </summary>
    void DrawSlider(PhotoMode.Param p, float x, float width, float y)
    {
        bool enabled = p.Enabled?.Invoke() ?? true;
        Text(p.Label, R(x, y, width * 0.62f, 20), 13, enabled ? SettingsCanvas.MainInk : SettingsCanvas.MutedInk);
        Text(PhotoMode.ValueText(p), R(x + width * 0.5f, y, width * 0.5f, 20), 13, enabled ? SettingsCanvas.LightInk : SettingsCanvas.MutedInk,
            "Right VCenter", translate: false);
        var track = R(x, y + 29, width, 3);
        float t = Fraction(p);
        Fill(track, Groove);
        Fill(track with { Width = track.Width * t }, enabled ? SettingsCanvas.Brass with { W = 0.75f } : SettingsCanvas.MutedInk with { W = 0.5f });
        Fill(new(track.X + t * track.Width - 3 * _scale, track.Y - 4 * _scale, 6 * _scale, 11 * _scale), enabled ? SettingsCanvas.LightInk : SettingsCanvas.MutedInk);
        if (enabled) _hits.Add(new Hit(R(x, y + 18, width, 22), () => { }, p, track.X, track.X + track.Width));
    }

    static float Fraction(PhotoMode.Param p)
    {
        float v = p.Get();
        float t = p.Log ? MathF.Log(Math.Max(v, p.Min) / p.Min) / MathF.Log(p.Max / p.Min) : (v - p.Min) / (p.Max - p.Min);
        return Math.Clamp(t, 0, 1);
    }

    /// <summary>
    /// A choice: the name and a button showing the current choice, which a press steps on.
    /// </summary>
    void DrawChoice(PhotoMode.Param p, string[] choices, float x, float width, float y)
    {
        Text(p.Label, R(x, y + 6, width - ChoiceWidth - 8, 26), 13, SettingsCanvas.MainInk);
        int count = choices.Length;
        Button(PhotoMode.ValueText(p), R(x + width - ChoiceWidth, y + 6, ChoiceWidth, 26), () => p.Set((MathF.Round(p.Get()) + 1) % count),
            step: d => p.Set(((MathF.Round(p.Get()) + d) % count + count) % count));
    }

    void Button(string label, UiRect at, Action action, bool primary = false, bool selected = false, Action<int>? step = null, bool translate = true)
    {
        bool hovered = at.Contains(_pointer.X, _pointer.Y), pressed = hovered && _down;
        var drawn = at with { Y = at.Y + (pressed ? _scale : 0) };
        Surface(drawn, primary ? "primary" : "button", selected || pressed, hovered && !selected);
        var ink = primary && _meitou ? DarkInk : hovered || selected ? SettingsCanvas.LightInk : SettingsCanvas.MainInk;
        Text(label, new(drawn.X + 8 * _scale, drawn.Y, drawn.Width - 16 * _scale, drawn.Height), primary ? 15 : 13, ink, "Center", translate);
        _hits.Add(new Hit(at, action, null, 0, 0, step));
    }

    /// <summary>
    /// A surface of the settings window's art (Meitou interface) or the game's skin of the same kind (Faithful): window, primary or button.
    /// </summary>
    void Surface(UiRect at, string kind, bool selected = false, bool hover = false)
    {
        bool window = kind == "window";
        if (_meitou)
        {
            string texture = kind switch
            {
                "window" => "window",
                "primary" => hover ? "paper-hover" : "paper",
                _ => selected ? "button-selected" : hover ? "button-hover" : "button",
            };
            float margin = window ? 16 * _scale : 0;
            var properties = new Dictionary<string, string>
            {
                ["ImageTexture"] = SettingsMenuArt.SurfacePrefix + texture,
                ["ImageBorder"] = window ? SettingsMenuArt.WindowBorder.ToString(CultureInfo.InvariantCulture) : "12",
                ["ImageBorderScale"] = (window ? _scale / SettingsMenuArt.WindowDensity : _scale).ToString(CultureInfo.InvariantCulture),
            };
            _controls.Add(new("settings-surface", "ImageBox", new(at.X - margin, at.Y - margin, at.Width + margin * 2, at.Height + margin * 2), "", "", "",
                true, properties, Vector4.Zero, Pickable: false));
        }
        else
        {
            string skin = window ? "Kenshi_GenericWindowSkin" : kind == "primary" ? "Kenshi_GenericTextBoxSkin" : "Kenshi_Button1Skin";
            _controls.Add(new("settings-surface", "Panel", at, "", "", skin, true,
                new Dictionary<string, string> { ["State"] = selected ? "pushed" : hover ? "highlighted" : "normal" },
                window ? new(0.12f, 0.11f, 0.09f, 1) : Vector4.Zero, Pickable: false));
        }
    }

    void Fill(UiRect at, Vector4 colour) =>
        _controls.Add(new("settings-fill", "Panel", at, "", "", "", true, new Dictionary<string, string>(), colour, Pickable: false));

    void Text(string caption, UiRect at, float size, Vector4 colour, string align = "Left VCenter", bool translate = true, bool wrap = false)
    {
        var properties = new Dictionary<string, string>
        {
            ["TextAlign"] = align,
            ["FontName"] = "Kenshi_StandardFont",
            ["FontPixels"] = ((int)MathF.Round(size * _scale)).ToString(CultureInfo.InvariantCulture),
        };
        if (wrap) properties["WordWrap"] = "true";
        _controls.Add(new("settings-text", "TextBox", at, translate ? Loc.Tr(caption) : caption, "", "", true, properties, Vector4.Zero, colour,
            Pickable: false, TextScale: size * _scale / 16));
    }

    /// <summary>
    /// The mode's last message (a saved picture's path), on a dark band at the top of the picture.
    /// </summary>
    void DrawMessage(int width)
    {
        if (mode.Message is not { } message) return;
        float w = Math.Min(width - 40 * _scale, 900 * _scale), h = 40 * _scale, line = MathF.Max(1, MathF.Round(_scale));
        var at = new UiRect(MathF.Round((width - w) / 2), MathF.Round(28 * _scale), MathF.Round(w), MathF.Round(h));
        Fill(at, new(8 / 255f, 6 / 255f, 5 / 255f, 0.78f));
        Fill(at with { Y = at.Y + at.Height - line, Height = line }, SettingsCanvas.Brass with { W = 0.55f });
        Text(message, new(at.X + 12 * _scale, at.Y, at.Width - 24 * _scale, at.Height), 13, SettingsCanvas.LightInk, "Center", translate: false, wrap: true);
    }

    #endregion
}
