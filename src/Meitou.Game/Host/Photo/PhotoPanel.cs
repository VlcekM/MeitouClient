using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Settings;

namespace Meitou.Game.Host.Photo;

/// <summary>
/// Photo mode's panel at the left of the picture and the guides over it. The panel is drawn as the settings window is: the interface's painter,
/// its window, button and paper surfaces (the game's skins in the Faithful interface), its font and inks, with sliders, switches and buttons the
/// mouse works (the same settings as <c>photo set</c>). The panel, the grid and the message are left out of a saved picture; the frame bars are not.
/// </summary>
sealed class PhotoPanel(PhotoMode mode)
{
    #region State

    static readonly Vector4 DarkInk = new(20 / 255f, 8 / 255f, 6 / 255f, 1);
    static readonly Vector4 Groove = new(0.10f, 0.08f, 0.07f, 1);

    static readonly string[] Hints =
    [
        "WASD move, R / F up and down, right mouse looks",
        "Wheel fly speed, Shift faster, Alt slower",
        "Z / X roll, V level, PageUp / PageDown lens",
        "F7 hides this panel, Enter or F8 takes the picture",
        "P or Esc leaves",
    ];

    /// <summary>
    /// The panel's place, width and rows' heights, in the settings window's units (pixels at a 900-line picture).
    /// </summary>
    const float Left = 30, Top = 30, Width = 320, Pad = 14, SliderRow = 38, ChoiceRow = 32, ChoiceWidth = 150;

    /// <summary>
    /// What the last drawn frame laid out for the mouse: a rectangle and what a press on it does; a slider also keeps its setting and the track's
    /// ends, so a drag follows the pointer.
    /// </summary>
    readonly record struct Hit(UiRect Area, Action Press, PhotoMode.Param? Slider, float TrackLeft, float TrackRight);

    readonly List<Hit> _hits = [];
    readonly List<UiControl> _controls = [];
    readonly SettingsMenuArt _art = new();
    UiRect _panel;
    Vector2 _pointer;
    Hit? _drag;
    float _scale = 1;
    bool _down, _meitou;

    #endregion

    #region Mouse

    /// <summary>
    /// The pointer is over the drawn panel (it takes the wheel and the clicks).
    /// </summary>
    public bool Contains(Vector2 p) => mode.PanelShown && _panel.Width > 0 && _panel.Contains(p.X, p.Y);

    /// <summary>
    /// A press at <paramref name="p"/>: what the panel does with it; false when it is outside.
    /// </summary>
    public bool MouseDown(Vector2 p)
    {
        _pointer = p;
        if (!Contains(p)) return false;
        _down = true;
        foreach (var hit in _hits)
        {
            if (!hit.Area.Contains(p.X, p.Y)) continue;
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
        if (mode.Host.Player.SettingsPainter is not { } painter) return;

        // The bars and the grid go under the panel: the painter draws at once, the overlay when it is flushed.
        o.Flush(width, height);
        _meitou = mode.Host.Player.UiMode == UiMode.Meitou;
        _art.Ensure(painter, _meitou, mode.Host.Player.UiAtlas);
        _scale = Math.Max(0.6f, Math.Min(1.25f, Math.Min(height / 900f, width / 1280f)));
        _controls.Clear();
        if (mode.PanelShown) DrawPanel();
        DrawMessage(width, height);
        if (_controls.Count > 0) painter.Draw(_controls, width, height, _pointer, null);
    }

    UiRect R(float x, float y, float w, float h) => new(MathF.Round(x * _scale), MathF.Round(y * _scale), MathF.Round(w * _scale), MathF.Round(h * _scale));

    void DrawPanel()
    {
        float rows = mode.Params.Sum(p => p.Choices is null ? SliderRow : ChoiceRow);
        float height = Pad + 34 + rows + 6 + 32 + 8 + 28 + 12 + Hints.Length * 16 + Pad;
        var window = R(Left, Top, Width, height);
        Surface(window, "window");
        float margin = _meitou ? 16 * _scale : 0;
        _panel = new(window.X - margin, window.Y - margin, window.Width + 2 * margin, window.Height + 2 * margin);

        float x = Left + Pad, inner = Width - 2 * Pad, y = Top + Pad;
        Text("Photo mode", R(x, y, inner, 26), 20, SettingsCanvas.LightInk);
        Fill(R(x, y + 29, inner, 1), SettingsCanvas.MainInk with { W = 0.14f });
        y += 34;
        foreach (var p in mode.Params)
        {
            if (p.Choices is { } choices) DrawChoice(p, choices, x, inner, y);
            else DrawSlider(p, x, inner, y);
            y += p.Choices is null ? SliderRow : ChoiceRow;
        }

        y += 6;
        Button("Take picture", R(x, y, inner, 32), () => mode.TakePicture(), primary: true);
        y += 40;
        float half = (inner - 8) / 2;
        Button("Game time", R(x, y, half, 28), mode.UseGameTime);
        Button("Reset", R(x + half + 8, y, half, 28), mode.Reset);
        y += 40;
        foreach (string line in Hints)
        {
            Text(line, R(x, y, inner, 16), 11, SettingsCanvas.MutedInk);
            y += 16;
        }
    }

    /// <summary>
    /// A slider row as the settings window's: the name, the value at the right, the bar under them.
    /// </summary>
    void DrawSlider(PhotoMode.Param p, float x, float inner, float y)
    {
        bool enabled = p.Enabled?.Invoke() ?? true;
        Text(p.Label, R(x, y, inner * 0.6f, 20), 13, enabled ? SettingsCanvas.MainInk : SettingsCanvas.MutedInk);
        Text(PhotoMode.ValueText(p), R(x + inner * 0.5f, y, inner * 0.5f, 20), 13, enabled ? SettingsCanvas.LightInk : SettingsCanvas.MutedInk,
            "Right VCenter", translate: false);
        var track = R(x, y + 26, inner, 3);
        float t = Fraction(p);
        Fill(track, Groove);
        Fill(track with { Width = track.Width * t }, enabled ? SettingsCanvas.Brass with { W = 0.75f } : SettingsCanvas.MutedInk with { W = 0.5f });
        Fill(new(track.X + t * track.Width - 3 * _scale, track.Y - 4 * _scale, 6 * _scale, 11 * _scale), enabled ? SettingsCanvas.LightInk : SettingsCanvas.MutedInk);
        if (enabled) _hits.Add(new Hit(R(x, y + 16, inner, 20), () => { }, p, track.X, track.X + track.Width));
    }

    static float Fraction(PhotoMode.Param p)
    {
        float v = p.Get();
        float t = p.Log ? MathF.Log(Math.Max(v, p.Min) / p.Min) / MathF.Log(p.Max / p.Min) : (v - p.Min) / (p.Max - p.Min);
        return Math.Clamp(t, 0, 1);
    }

    /// <summary>
    /// A choice row: the name and a button showing the current choice, which a press steps on.
    /// </summary>
    void DrawChoice(PhotoMode.Param p, string[] choices, float x, float inner, float y)
    {
        Text(p.Label, R(x, y, inner - ChoiceWidth - 8, 26), 13, SettingsCanvas.MainInk);
        int count = choices.Length;
        Button(PhotoMode.ValueText(p), R(x + inner - ChoiceWidth, y, ChoiceWidth, 26), () => p.Set((MathF.Round(p.Get()) + 1) % count));
    }

    void Button(string label, UiRect at, Action action, bool primary = false)
    {
        bool hovered = at.Contains(_pointer.X, _pointer.Y), pressed = hovered && _down;
        var drawn = at with { Y = at.Y + (pressed ? _scale : 0) };
        Surface(drawn, primary ? "primary" : "button", pressed, hovered);
        var ink = primary && _meitou ? DarkInk : hovered ? SettingsCanvas.LightInk : SettingsCanvas.MainInk;
        Text(label, new(drawn.X + 8 * _scale, drawn.Y, drawn.Width - 16 * _scale, drawn.Height), primary ? 15 : 13, ink, "Center");
        _hits.Add(new Hit(at, action, null, 0, 0));
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
    /// The mode's last message (a saved picture's path), on a dark band at the bottom of the picture.
    /// </summary>
    void DrawMessage(int width, int height)
    {
        if (mode.Message is not { } message) return;
        float w = Math.Min(width - 40 * _scale, 900 * _scale), h = 40 * _scale;
        var at = new UiRect(MathF.Round((width - w) / 2), MathF.Round(height - h - 44 * _scale), MathF.Round(w), MathF.Round(h));
        Fill(at, new(8 / 255f, 6 / 255f, 5 / 255f, 0.78f));
        Fill(at with { Height = MathF.Max(1, MathF.Round(_scale)) }, SettingsCanvas.Brass with { W = 0.55f });
        Text(message, new(at.X + 12 * _scale, at.Y, at.Width - 24 * _scale, at.Height), 13, SettingsCanvas.LightInk, "Center", translate: false, wrap: true);
    }

    #endregion
}
