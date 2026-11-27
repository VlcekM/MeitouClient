using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Host.Input;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Settings;
using Meitou.Game.Ui;

namespace Meitou.Game.Host.Photo;

/// <summary>
/// Photo mode's panel, a bar along the bottom of the picture with a tab for each kind of setting, and the guides over the picture. The panel is
/// drawn as the settings window is: the interface's painter, its window, button and paper surfaces (the game's skins in the Faithful interface), its
/// font and inks, with sliders, switches and buttons the mouse works (the same settings as <c>photo set</c>). The panel, the grid and the message are
/// left out of a saved picture; the frame bars are not.
/// </summary>
sealed class PhotoPanel(PhotoMode mode)
{
    #region State

    /// <summary>
    /// The ink of text on a paper surface (the primary button and the name field in the Meitou interface).
    /// </summary>
    Vector4 OnPaperInk => PaperInk.On(mode.Host.Player);

    /// <summary>
    /// A slider's empty track.
    /// </summary>
    static readonly Vector4 Groove = new(0.10f, 0.08f, 0.07f, 1);

    /// <summary>
    /// The outline of the character the Pose tab changes.
    /// </summary>
    internal static readonly Vector4 ChosenOutline = new(1, 0.82f, 0.45f, 1);

    /// <summary>
    /// The presets tab's last line.
    /// </summary>
    const string PresetHints = "Click a preset to use it    Right click to rename it    Wheel or the arrows to see the others";

    /// <summary>
    /// The last line while a slider's value is typed.
    /// </summary>
    const string EntryHints = "Type a value    Enter applies it    Esc cancels    Time of day as 13:30";

    /// <summary>
    /// The Pose tab's last line.
    /// </summary>
    const string PoseHints = "Click a character to choose it    Drag the chosen one to move it    Ctrl + click sets the focus point    "
        + "The arrows step through those in view    Drag an arrow to move, a ring to turn";

    /// <summary>
    /// A row of settings' height, when a tab holds more than <see cref="Columns"/> of them.
    /// </summary>
    const float RowStep = 46;

    /// <summary>
    /// The pose list's width and row height.
    /// </summary>
    const float ListWidth = 330, ListRow = 22;

    /// <summary>
    /// The bar's widest width, in the settings window's units (pixels at a 900-line picture).
    /// </summary>
    const float BarWidth = 1200;

    /// <summary>
    /// The bar's height.
    /// </summary>
    const float BarHeight = 138;

    /// <summary>
    /// The gap under the bar, above the picture's bottom.
    /// </summary>
    const float BarBottom = 26;

    /// <summary>
    /// The bar's inner margin left and right.
    /// </summary>
    const float Pad = 14;

    /// <summary>
    /// A tab's width.
    /// </summary>
    const float TabWidth = 96;

    /// <summary>
    /// The gap between tabs.
    /// </summary>
    const float TabGap = 6;

    /// <summary>
    /// The gap between settings side by side.
    /// </summary>
    const float CellGap = 24;

    /// <summary>
    /// A choice's button width.
    /// </summary>
    const float ChoiceWidth = 104;

    /// <summary>
    /// The settings side by side under the tabs, at most.
    /// </summary>
    const int Columns = 5;

    /// <summary>
    /// What the last drawn frame laid out for the mouse: a rectangle and what a press on it does; a slider also keeps its setting and the track's
    /// ends, so a drag follows the pointer.
    /// </summary>
    readonly record struct Hit(UiRect Area, Action Press, PhotoMode.Param? Slider, float TrackLeft, float TrackRight, Action<int>? Step = null,
        Action? Back = null, PhotoMode.Param? Value = null);

    /// <summary>
    /// The last drawn frame's hit areas.
    /// </summary>
    readonly List<Hit> _hits = [];

    /// <summary>
    /// The controls the painter draws this frame.
    /// </summary>
    readonly List<UiControl> _controls = [];

    /// <summary>
    /// The settings window's surfaces.
    /// </summary>
    readonly SettingsMenuArt _art = new();

    /// <summary>
    /// The drawn panel's area with its window margin (empty when none is drawn).
    /// </summary>
    UiRect _panel;

    /// <summary>
    /// The drawn pose list's area with its window margin (empty when it is closed).
    /// </summary>
    UiRect _list;

    /// <summary>
    /// The slider whose value text was last pressed: a second press on it within <see cref="DoubleClickMs"/> types its value.
    /// </summary>
    PhotoMode.Param? _lastValue;

    /// <summary>
    /// When <see cref="_lastValue"/> was pressed (<see cref="Environment.TickCount64"/>).
    /// </summary>
    long _lastPress;

    /// <summary>
    /// The longest gap between the two presses of a double click, in milliseconds.
    /// </summary>
    const long DoubleClickMs = 400;

    /// <summary>
    /// The pointer's last place.
    /// </summary>
    Vector2 _pointer;

    /// <summary>
    /// The slider being dragged, if any.
    /// </summary>
    Hit? _drag;

    /// <summary>
    /// Pixels per settings-window unit this frame.
    /// </summary>
    float _scale = 1;

    /// <summary>
    /// The left button is down.
    /// </summary>
    bool _down;

    /// <summary>
    /// The Meitou interface is drawn (the settings window's art, not the game's skins).
    /// </summary>
    bool _meitou;

    /// <summary>
    /// The tab shown (an index into <see cref="PhotoMode.Groups"/>).
    /// </summary>
    int _tab;

    /// <summary>
    /// The first preset shown in the presets' row (the wheel and the row's arrows move it when not all fit).
    /// </summary>
    int _presetFirst;

    #endregion

    #region Mouse

    /// <summary>
    /// The tab shown.
    /// </summary>
    public string Group
    {
        get => PhotoMode.Groups[Math.Clamp(_tab, 0, PhotoMode.Groups.Length - 1)];
        set => _tab = Math.Max(Array.IndexOf(PhotoMode.Groups, value), 0);
    }

    /// <summary>
    /// The pointer is over the drawn panel or the open pose list (they take the wheel and the clicks).
    /// </summary>
    public bool Contains(Vector2 p) =>
        mode.PanelShown && (_panel.Width > 0 && _panel.Contains(p.X, p.Y) || _list.Width > 0 && _list.Contains(p.X, p.Y));

    /// <summary>
    /// A press at <paramref name="p"/>: what the panel does with it (<paramref name="back"/>, the right button: a choice steps back); false when it
    /// is outside.
    /// </summary>
    public bool MouseDown(Vector2 p, bool back = false)
    {
        _pointer = p;
        if (!Contains(p)) return false;

        _down = !back;
        if (mode.Typing is not null)
        {
            // A press while a value is typed ends the entry (a press on the typed value itself keeps it open).
            if (!_hits.Any(h => h.Value == mode.Typing && h.Area.Contains(p.X, p.Y))) mode.CancelEntry();
            return true;
        }

        foreach (var hit in _hits)
        {
            if (!hit.Area.Contains(p.X, p.Y)) continue;
            if (back)
            {
                // A right click on a slider's value types it; on a choice it steps back.
                if (hit.Value is { } typed) mode.BeginEntry(typed);
                else if (hit.Back is { } alternative) alternative();
                else hit.Step?.Invoke(-1);
                break;
            }

            hit.Press();
            if (hit.Value is not null) PressValue(hit.Value);
            else if (hit.Slider is not null)
            {
                _drag = hit;
                Slide(hit, p.X);
            }

            break;
        }

        return true;
    }

    /// <summary>
    /// A left press on a slider's value text: a second press on it within <see cref="DoubleClickMs"/> opens its typed entry; one alone does nothing.
    /// </summary>
    void PressValue(PhotoMode.Param value)
    {
        long now = Environment.TickCount64;
        bool second = _lastValue == value && now - _lastPress <= DoubleClickMs;
        (_lastValue, _lastPress) = (second ? null : value, now);
        if (second) mode.BeginEntry(value);
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
    /// The wheel over the panel steps the setting under the pointer: a slider by a fiftieth of its track, a choice by one. True when the pointer is
    /// over the panel (the wheel is then not the camera's).
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

    /// <summary>
    /// A dragged slider's setting at the pointer's place along its track.
    /// </summary>
    static void Slide(Hit hit, float x) => SetFraction(hit.Slider!, (x - hit.TrackLeft) / Math.Max(hit.TrackRight - hit.TrackLeft, 1));

    /// <summary>
    /// A slider's setting at fraction <paramref name="t"/> of its track (by the ratio for a <see cref="PhotoMode.Param.Log"/> one).
    /// </summary>
    static void SetFraction(PhotoMode.Param p, float t)
    {
        t = Math.Clamp(t, 0, 1);
        p.Set(p.Log ? p.Min * MathF.Pow(p.Max / p.Min, t) : p.Min + (p.Max - p.Min) * t);
    }

    #endregion

    #region Drawing

    /// <summary>
    /// The frame's frame bars, grid, panel and message over the finished picture; <paramref name="saving"/> leaves out all but the bars. With a
    /// <paramref name="pass"/> (Metal's one render encoder for the overlays) everything goes into it, in order.
    /// </summary>
    public void Draw(DebugOverlay o, int width, int height, bool saving, DebugOverlay.OverlayPass? pass = null)
    {
        PhotoGuides.DrawFrame(o, mode.Frame, width, height);
        _hits.Clear();
        (_panel, _list) = (default, default);
        if (!mode.GuidesDrawn(saving)) return;

        float u = Math.Max(0.75f, height / 1080f);
        PhotoGuides.DrawGrid(o, mode.Grid, PhotoGuides.Window(mode.Frame, width, height), Math.Max(1.5f, 1.5f * u));
        if (mode.FocusSpot is { } spot)
            PhotoGuides.DrawReticle(o, new Vector2(spot.X * width, spot.Y * height), 16 * u, Math.Max(1.5f, 1.5f * u));
        if (mode.PanelShown) mode.Lights.DrawGizmos(o, mode.Host.Player, u, Group == PhotoMode.LightGroup);
        if (mode.PanelShown) mode.Gizmo.Draw(o, mode.Host.Player, mode.GizmoSubject, _pointer);
        if (mode.Host.Player.SettingsPainter is not { } painter) return;

        // The bars and the grid go under the panel: the painter draws at once, the overlay when it is flushed.
        o.Flush(width, height, null, pass);
        _meitou = mode.Host.Player.UiMode == UiMode.Meitou;
        _art.Ensure(painter, _meitou, mode.Host.Player.UiAtlas);
        _scale = Math.Max(0.6f, Math.Min(height / 900f, width / 1280f));

        _controls.Clear();
        PhotoIcons.Ensure(painter);
        if (mode.PanelShown) DrawIcons();
        if (mode.PanelShown) DrawPanel(width, height);
        DrawMessage(width);
        if (mode.Busy) DrawBusy(width, height);
        if (_controls.Count > 0) painter.Draw(_controls, width, height, _pointer, null, pass: pass);
    }

    /// <summary>
    /// The light marks' kind icons: the body in neutral grey on its soft outline, then the glow tinted by the light's colour, both dimmed when the
    /// light is off.
    /// </summary>
    void DrawIcons()
    {
        foreach (var icon in mode.Lights.Icons)
        {
            var area = new UiRect(MathF.Round(icon.Centre.X - icon.Side / 2), MathF.Round(icon.Centre.Y - icon.Side / 2), MathF.Round(icon.Side),
                MathF.Round(icon.Side));
            string alpha = icon.Alpha.ToString(CultureInfo.InvariantCulture);
            foreach (bool glow in new[] { false, true })
            {
                var properties = new Dictionary<string, string> { ["ImageTexture"] = PhotoIcons.Name(icon.Kind, glow, icon.Side), ["Alpha"] = alpha };
                if (glow)
                    properties["Colour"] = FormattableString.Invariant($"{icon.Colour.X} {icon.Colour.Y} {icon.Colour.Z} 1");
                _controls.Add(new("photo-icon", "ImageBox", area, "", "", "", true, properties, Vector4.Zero, Pickable: false));
            }
        }
    }

    /// <summary>
    /// A rectangle in the settings window's units, in whole pixels at this frame's scale.
    /// </summary>
    UiRect R(float x, float y, float w, float h) =>
        new(MathF.Round(x * _scale), MathF.Round(y * _scale), MathF.Round(w * _scale), MathF.Round(h * _scale));

    /// <summary>
    /// The bar: the tabs at the left of its top row, Gallery, Reset and Take picture at the right, the shown tab's settings under them in rows of
    /// <see cref="Columns"/> (the bar grows a row for each further one), the keys on its last line.
    /// </summary>
    void DrawPanel(int width, int height)
    {
        string group = PhotoMode.Groups[Math.Clamp(_tab, 0, PhotoMode.Groups.Length - 1)];
        var cells = Placed(Cells(group));
        int rows = cells.Count == 0 ? 1 : cells[^1].Row + 1;
        float barHeight = BarHeight + (rows - 1) * RowStep;
        float barWidth = Math.Min(BarWidth, width / _scale - 48);
        float left = (width / _scale - barWidth) / 2;
        float top = height / _scale - barHeight - BarBottom;
        var window = R(left, top, barWidth, barHeight);
        Surface(window, "window");
        float margin = _meitou ? 16 * _scale : 0;
        _panel = new(window.X - margin, window.Y - margin, window.Width + 2 * margin, window.Height + 2 * margin);

        float x = left + Pad;
        float inner = barWidth - 2 * Pad;
        float y = top + 12;
        _tab = Math.Clamp(_tab, 0, PhotoMode.Groups.Length - 1);
        for (int i = 0; i < PhotoMode.Groups.Length; i++)
        {
            int index = i;
            Button(PhotoMode.Groups[i], R(x + i * (TabWidth + TabGap), y, TabWidth, 28), () => _tab = index, selected: i == _tab);
        }

        Button("Take picture", R(x + inner - 160, y - 1, 160, 30), () => mode.TakePicture(), primary: true);
        Button("Reset", R(x + inner - 160 - 8 - 90, y, 90, 28), mode.Reset);
        Button("Gallery", R(x + inner - 160 - 8 - 90 - 8 - 90, y, 90, 28), mode.OpenGallery);
        Fill(R(x, y + 36, inner, 1), SettingsCanvas.MainInk with { W = 0.14f });

        y += 46;
        float cell = (inner - (Columns - 1) * CellGap) / Columns;
        foreach (var (draw, column, row) in cells) draw(x + column * (cell + CellGap), y + row * RowStep, cell);

        if (group == PhotoMode.PresetGroup) DrawPresets(x, y, x + inner);
        string hints = mode.Typing is not null ? EntryHints : group switch
        {
            PhotoMode.PresetGroup => PresetHints,
            PhotoMode.PoseGroup => PoseHints,
            _ => KeyHints(),
        };
        Text(hints, R(x, top + barHeight - 28, inner, 16), 11, SettingsCanvas.MutedInk, "Center",
            translate: mode.Typing is not null || group is PhotoMode.PresetGroup or PhotoMode.PoseGroup);
        if (group == PhotoMode.PoseGroup && mode.Poses.ListOpen) DrawPoseList(x + cell + CellGap, top);
    }

    /// <summary>
    /// The shown tab's cells in order, each drawn at (x, y) with a width (null: the next cell starts a row): its settings, then its buttons. The
    /// Light tab has the exposure, the hour and Game time, then on a row of their own the chosen light, Place here and that light's settings; the
    /// Pose tab starts with the character and its pose and ends with Reset character.
    /// </summary>
    List<Action<float, float, float>?> Cells(string group)
    {
        var cells = new List<Action<float, float, float>?>();
        void Setting(PhotoMode.Param p)
        {
            if (p.Choices is { } choices) cells.Add((x, y, w) => DrawChoice(p, choices, x, w, y));
            else cells.Add((x, y, w) => DrawSlider(p, x, w, y));
        }

        var settings = mode.Params.Where(p => p.Group == group).ToList();
        if (group == PhotoMode.LightGroup)
        {
            foreach (var p in settings.Where(p => !IsLight(p))) Setting(p);
            cells.Add((x, y, _) => Button("Game time", R(x, y + 6, ChoiceWidth, 26), mode.UseGameTime));
            cells.Add(null);
            cells.Add(DrawLightChoice);
            cells.Add((x, y, w) => Button("Place here", R(x, y + 6, w, 26), mode.PlaceLight));
            foreach (var p in settings.Where(IsLight)) Setting(p);
            return cells;
        }

        if (group == PhotoMode.PoseGroup)
        {
            cells.Add(DrawCharacter);
            cells.Add(DrawPose);
        }

        foreach (var p in settings) Setting(p);
        if (group == PhotoMode.PoseGroup)
            cells.Add((x, y, w) => Button("Reset character", R(x, y + 6, w, 26), mode.Poses.ResetSelected));
        return cells;
    }

    /// <summary>
    /// A setting of the chosen light (the Light tab's second row).
    /// </summary>
    static bool IsLight(PhotoMode.Param p) => p.Name.StartsWith("light", StringComparison.Ordinal);

    /// <summary>
    /// Where each cell stands: <see cref="Columns"/> to a row, a null cell starting the next row.
    /// </summary>
    static List<(Action<float, float, float> Draw, int Column, int Row)> Placed(List<Action<float, float, float>?> cells)
    {
        var placed = new List<(Action<float, float, float>, int, int)>();
        int column = 0, row = 0;
        foreach (var cell in cells)
        {
            if (cell is null)
            {
                if (column > 0) (column, row) = (0, row + 1);
                continue;
            }

            if (column == Columns) (column, row) = (0, row + 1);
            placed.Add((cell, column++, row));
        }

        return placed;
    }

    /// <summary>
    /// The Light tab's chosen light: the arrows step through the lights, its name between them.
    /// </summary>
    void DrawLightChoice(float x, float y, float width)
    {
        Button("<", R(x, y + 6, 26, 26), () => mode.Lights.Step(-1), step: d => mode.Lights.Step(-d), translate: false);
        Button(mode.Lights.ChosenName, R(x + 30, y + 6, width - 60, 26), () => mode.Lights.Step(1), step: d => mode.Lights.Step(-d),
            translate: false, selected: mode.Lights.On);
        Button(">", R(x + width - 26, y + 6, 26, 26), () => mode.Lights.Step(1), step: d => mode.Lights.Step(-d), translate: false);
    }

    /// <summary>
    /// The keys, on the bar's last line: photo mode's own, and the game's bindings where it follows them (pause, the interface key, the screenshot
    /// key, the photo mode key).
    /// </summary>
    string KeyHints()
    {
        var bindings = mode.Host.Session.Bindings;
        string Keys(string own, InputAction action) =>
            string.Join(" / ", new[] { own }.Concat(bindings.Get(action).Select(b => b.ToString())).Where(k => k.Length > 0).Distinct());
        string pause = Keys("", InputAction.Pause);
        return "WASD move    Q / E down, up    Right mouse look    Ctrl + left click focus    Wheel zoom    Alt + wheel fly speed    Shift fast    "
            + "Ctrl slow    Z / X roll    V level    "
            + (pause.Length > 0 ? $"{pause} pause    " : "")
            + $"{Keys("Tab / H", InputAction.ToggleInterface)} hide    {Keys("Enter", InputAction.Screenshot)} picture    "
            + $"{Keys("", InputAction.TogglePhotoMode)} / Esc leave";
    }

    /// <summary>
    /// The Pose tab's character: the arrows step through the characters in view (nearest first), the name between them (the wheel steps too).
    /// </summary>
    void DrawCharacter(float x, float y, float width)
    {
        Button("<", R(x, y + 6, 26, 26), () => mode.Poses.Step(-1), step: d => mode.Poses.Step(-d), translate: false);
        string name = mode.Poses.Selected is null ? "Choose a character" : mode.Poses.SelectedName;
        Button(name, R(x + 30, y + 6, width - 60, 26), () => mode.Poses.Step(1), step: d => mode.Poses.Step(-d),
            translate: mode.Poses.Selected is null, selected: mode.Poses.Selected is not null);
        Button(">", R(x + width - 26, y + 6, 26, 26), () => mode.Poses.Step(1), step: d => mode.Poses.Step(-d), translate: false);
    }

    /// <summary>
    /// The Pose tab's pose: the name and a button with the chosen character's pose, which opens the list of the poses it can take.
    /// </summary>
    void DrawPose(float x, float y, float width)
    {
        bool chosen = mode.Poses.Selected is not null;
        Text("Pose", R(x, y + 6, 44, 26), 13, chosen ? SettingsCanvas.MainInk : SettingsCanvas.MutedInk);
        string pose = mode.Poses.Pose ?? Loc.Tr(PhotoPoses.OwnAnimation);
        Button(pose + "  v", R(x + 48, y + 6, width - 48, 26), mode.TogglePoseList, translate: false, selected: mode.Poses.ListOpen);
    }

    /// <summary>
    /// The open pose list, over the bar from <paramref name="x"/>: the typed filter on its first line, then <see cref="PhotoPoses.ListRows"/> rows
    /// (the chosen character's own animation first, then the records its skeleton has); a click picks one, the wheel scrolls.
    /// </summary>
    void DrawPoseList(float x, float barTop)
    {
        var poses = mode.Poses;
        int rows = Math.Min(PhotoPoses.ListRows, Math.Max(poses.Shown.Count, 1));
        float height = 36 + rows * ListRow + 8;
        float top = barTop - height - 22;
        var window = R(x, top, ListWidth, height);
        Surface(window, "window");
        float margin = _meitou ? 16 * _scale : 0;
        _list = new(window.X - margin, window.Y - margin, window.Width + 2 * margin, window.Height + 2 * margin);

        bool caret = Environment.TickCount64 / 500 % 2 == 0;
        string filter = poses.Filter.Length > 0 ? poses.Filter : Loc.Tr("type to search");

        // The count has its own right-aligned box, so the blinking caret never moves it.
        Text($"{filter}{(caret ? "|" : "")}", R(x + 10, top + 6, ListWidth - 80, 24), 13,
            poses.Filter.Length > 0 ? SettingsCanvas.LightInk : SettingsCanvas.MutedInk, translate: false);
        Text($"({poses.Shown.Count - 1})", R(x + ListWidth - 70, top + 6, 60, 24), 13, SettingsCanvas.MutedInk, "Right VCenter", translate: false);
        Fill(R(x + 10, top + 32, ListWidth - 20, 1), SettingsCanvas.MainInk with { W = 0.14f });

        for (int row = 0; row < rows && poses.Scroll + row < poses.Shown.Count; row++)
        {
            int index = poses.Scroll + row;
            var at = R(x + 6, top + 36 + row * ListRow, ListWidth - 12, ListRow);
            bool current = poses.Shown[index] == (poses.Pose ?? PhotoPoses.OwnAnimation);
            if (index == poses.Highlight || at.Contains(_pointer.X, _pointer.Y)) Fill(at, SettingsCanvas.Brass with { W = 0.22f });
            string label = index == 0 ? Loc.Tr(PhotoPoses.OwnAnimation) : poses.Shown[index];
            Text(label, at with { X = at.X + 6 * _scale, Width = at.Width - 12 * _scale }, 13,
                current ? SettingsCanvas.LightInk : SettingsCanvas.MainInk, translate: false);
            _hits.Add(new Hit(at, () => poses.Pick(index), null, 0, 0, d => poses.ListWheel(d)));
        }
    }

    /// <summary>
    /// The presets' tab: Save preset keeps every tab's settings under a name typed in the field that takes the row's place (Enter or OK keeps it, Esc
    /// or Cancel drops it). Each preset is a button that takes its settings (a right click renames it), with a small one beside it that deletes it.
    /// When not all fit, the wheel over the row and the arrows at its ends move through them.
    /// </summary>
    void DrawPresets(float x, float y, float right)
    {
        if (mode.Naming is { } typed)
        {
            DrawPresetNaming(typed, x, y, right);
            return;
        }

        Button("Save preset", R(x, y + 6, 124, 26), () => mode.BeginNaming());
        x += 124 + CellGap;
        var presets = mode.Presets;
        if (presets.Count == 0)
        {
            Text("No preset yet. Save preset keeps the settings of every tab.", R(x, y + 6, right - x, 26), 13, SettingsCanvas.MutedInk);
            return;
        }

        const float slot = 156 + 10;
        const float arrow = 26;
        int fit = Math.Max(1, (int)((right - x + 10) / slot));
        bool scrolls = presets.Count > fit;
        if (scrolls) fit = Math.Max(1, (int)((right - x - 2 * (arrow + 8) + 10) / slot));
        _presetFirst = Math.Clamp(_presetFirst, 0, Math.Max(presets.Count - fit, 0));
        void Scroll(int by) => _presetFirst = Math.Clamp(_presetFirst + by, 0, Math.Max(mode.Presets.Count - fit, 0));

        if (scrolls)
        {
            Button("<", R(x, y + 6, arrow, 26), () => Scroll(-1), step: d => Scroll(-d), translate: false);
            x += arrow + 8;
        }

        for (int i = _presetFirst; i < presets.Count && i < _presetFirst + fit; i++)
        {
            int index = i;
            Button(presets[i].Name, R(x, y + 6, 128, 26), () => mode.LoadPreset(index), step: d => Scroll(-d), translate: false,
                back: () => mode.BeginNaming(index));
            Button("x", R(x + 130, y + 6, 26, 26), () => mode.DeletePreset(index), step: d => Scroll(-d), translate: false);
            x += slot;
        }

        if (scrolls)
            Button(">", R(right - arrow, y + 6, arrow, 26), () => Scroll(1), step: d => Scroll(-d), translate: false);
    }

    /// <summary>
    /// The field a preset's name is typed in (with a blinking caret), OK and Cancel, and how to keep it.
    /// </summary>
    void DrawPresetNaming(string typed, float x, float y, float right)
    {
        Text("Name", R(x, y + 6, 52, 26), 13, SettingsCanvas.MainInk);
        var field = R(x + 56, y + 6, 300, 26);
        Surface(field, "primary");
        bool caret = Environment.TickCount64 / 500 % 2 == 0;
        Text(typed + (caret ? "|" : ""), new(field.X + 8 * _scale, field.Y, field.Width - 16 * _scale, field.Height), 14,
            _meitou ? OnPaperInk : SettingsCanvas.LightInk, translate: false);

        Button("OK", R(x + 56 + 300 + 10, y + 6, 70, 26), mode.CommitNaming);
        Button("Cancel", R(x + 56 + 300 + 10 + 70 + 8, y + 6, 90, 26), mode.CancelNaming);
        Text("Type the preset's name, Enter keeps it, Esc cancels.", R(x + 56 + 300 + 10 + 70 + 8 + 90 + CellGap, y + 6, right - x - 560, 26), 12,
            SettingsCanvas.MutedInk);
    }

    /// <summary>
    /// A slider as the settings window's: the name, the value at its right, the bar under them.
    /// </summary>
    void DrawSlider(PhotoMode.Param p, float x, float width, float y)
    {
        bool enabled = p.Enabled?.Invoke() ?? true;
        Text(p.Label, R(x, y, width * 0.62f, 20), 13, enabled ? SettingsCanvas.MainInk : SettingsCanvas.MutedInk);
        if (mode.Typing == p) DrawEntry(x + width * 0.45f, y - 2, width * 0.55f);
        else if (enabled) DrawValue(p, x + width * 0.5f, y, width * 0.5f);
        else
            Text(PhotoMode.ValueText(p), R(x + width * 0.5f, y, width * 0.5f, 20), 13, SettingsCanvas.MutedInk, "Right VCenter", translate: false);

        var track = R(x, y + 29, width, 3);
        float t = Fraction(p);
        Fill(track, Groove);
        Fill(track with { Width = track.Width * t }, enabled ? SettingsCanvas.Brass with { W = 0.75f } : SettingsCanvas.MutedInk with { W = 0.5f });
        Fill(new(track.X + t * track.Width - 3 * _scale, track.Y - 4 * _scale, 6 * _scale, 11 * _scale),
            enabled ? SettingsCanvas.LightInk : SettingsCanvas.MutedInk);

        if (enabled) _hits.Add(new Hit(R(x, y + 18, width, 22), () => { }, p, track.X, track.X + track.Width));
    }

    /// <summary>
    /// A slider's value text, which a double click turns into a field: on hover a light box and an underline show it is editable.
    /// </summary>
    void DrawValue(PhotoMode.Param p, float x, float y, float width)
    {
        var area = R(x, y, width, 20);
        string text = PhotoMode.ValueText(p);
        if (area.Contains(_pointer.X, _pointer.Y) && mode.Typing is null)
        {
            // The box hugs the text (about 7.5 units a character at this size), grown a little.
            float w = Math.Min(text.Length * 7.5f + 12, width) * _scale;
            var box = new UiRect(area.X + area.Width - w, area.Y - 1 * _scale, w, area.Height + 2 * _scale);
            Fill(box, SettingsCanvas.Brass with { W = 0.2f });
            Fill(box with { Y = box.Y + box.Height - MathF.Max(1, MathF.Round(_scale)), Height = MathF.Max(1, MathF.Round(_scale)) },
                SettingsCanvas.Brass with { W = 0.85f });
        }

        Text(text, area with { Width = area.Width - 6 * _scale }, 13, SettingsCanvas.LightInk, "Right VCenter", translate: false);
        _hits.Add(new Hit(area, () => { }, null, 0, 0, Value: p));
    }

    /// <summary>
    /// The field a slider's value is typed in (with a blinking caret), over the value.
    /// </summary>
    void DrawEntry(float x, float y, float width)
    {
        var field = R(x, y, width, 22);
        Surface(field, "primary");
        var inner = new UiRect(field.X + 6 * _scale, field.Y, field.Width - 12 * _scale, field.Height);
        var ink = _meitou ? OnPaperInk : SettingsCanvas.LightInk;
        bool caret = Environment.TickCount64 / 500 % 2 == 0;

        // The number starts selected (typing replaces it): a band behind it, as wide as the text.
        if (mode.EntryFresh && mode.Entry.Length > 0)
        {
            float w = (mode.Entry.Length * 7.5f + 4) * _scale;
            Fill(new UiRect(inner.X + inner.Width - w, inner.Y + 3 * _scale, w, inner.Height - 6 * _scale), SettingsCanvas.Brass with { W = 0.55f });
        }

        Text(mode.Entry + (caret && !mode.EntryFresh ? "|" : ""), inner, 13, ink, "Right VCenter", translate: false);
    }

    /// <summary>
    /// Where a slider's setting sits along its track, 0..1 (by the ratio for a <see cref="PhotoMode.Param.Log"/> one).
    /// </summary>
    static float Fraction(PhotoMode.Param p)
    {
        float v = p.Get();
        float t = p.Log ? MathF.Log(Math.Max(v, p.Min) / p.Min) / MathF.Log(p.Max / p.Min) : (v - p.Min) / (p.Max - p.Min);
        return Math.Clamp(t, 0, 1);
    }

    /// <summary>
    /// A choice: the name and a button showing the current choice, which a press steps on (greyed and inert while the setting does not apply).
    /// </summary>
    void DrawChoice(PhotoMode.Param p, string[] choices, float x, float width, float y)
    {
        bool enabled = p.Enabled?.Invoke() ?? true;
        Text(p.Label, R(x, y + 6, width - ChoiceWidth - 8, 26), 13, enabled ? SettingsCanvas.MainInk : SettingsCanvas.MutedInk);
        if (!enabled)
        {
            var off = R(x + width - ChoiceWidth, y + 6, ChoiceWidth, 26);
            Surface(off, "button");
            Text(PhotoMode.ValueText(p), new(off.X + 8 * _scale, off.Y, off.Width - 16 * _scale, off.Height), 13, SettingsCanvas.MutedInk, "Center");
            return;
        }

        int count = choices.Length;
        Button(PhotoMode.ValueText(p), R(x + width - ChoiceWidth, y + 6, ChoiceWidth, 26), () => p.Set((MathF.Round(p.Get()) + 1) % count),
            step: d => p.Set(((MathF.Round(p.Get()) + d) % count + count) % count));
    }

    /// <summary>
    /// A button: its surface (pressed down a pixel while held), its label, and its hit area with what a press, the wheel (<paramref name="step"/>)
    /// and the right button (<paramref name="back"/>) do.
    /// </summary>
    void Button(string label, UiRect at, Action action, bool primary = false, bool selected = false, Action<int>? step = null, bool translate = true,
        Action? back = null)
    {
        bool hovered = at.Contains(_pointer.X, _pointer.Y);
        bool pressed = hovered && _down;
        var drawn = at with { Y = at.Y + (pressed ? _scale : 0) };
        Surface(drawn, primary ? "primary" : "button", selected || pressed, hovered && !selected);

        var ink = primary && _meitou ? OnPaperInk
            : hovered || selected ? SettingsCanvas.LightInk
            : SettingsCanvas.MainInk;
        Text(label, new(drawn.X + 8 * _scale, drawn.Y, drawn.Width - 16 * _scale, drawn.Height), primary ? 15 : 13, ink, "Center", translate);
        _hits.Add(new Hit(at, action, null, 0, 0, step, back));
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
                _ => selected ? "button-selected"
                    : hover ? "button-hover"
                    : "button",
            };
            float margin = window ? 16 * _scale : 0;
            var properties = new Dictionary<string, string>
            {
                ["ImageTexture"] = SettingsMenuArt.SurfacePrefix + texture,
                ["ImageBorder"] = window ? SettingsMenuArt.WindowBorder.ToString(CultureInfo.InvariantCulture) : "12",
                ["ImageBorderScale"] = (window ? _scale / SettingsMenuArt.WindowDensity : _scale).ToString(CultureInfo.InvariantCulture),
            };
            var area = new UiRect(at.X - margin, at.Y - margin, at.Width + margin * 2, at.Height + margin * 2);
            _controls.Add(new("settings-surface", "ImageBox", area, "", "", "", true, properties, Vector4.Zero, Pickable: false));
        }
        else
        {
            string skin = window ? "Kenshi_GenericWindowSkin"
                : kind == "primary" ? "Kenshi_GenericTextBoxSkin"
                : "Kenshi_Button1Skin";
            string state = selected ? "pushed"
                : hover ? "highlighted"
                : "normal";
            _controls.Add(new("settings-surface", "Panel", at, "", "", skin, true, new Dictionary<string, string> { ["State"] = state },
                window ? new(0.12f, 0.11f, 0.09f, 1) : Vector4.Zero, Pickable: false));
        }
    }

    /// <summary>
    /// A flat rectangle of <paramref name="colour"/>.
    /// </summary>
    void Fill(UiRect at, Vector4 colour) =>
        _controls.Add(new("settings-fill", "Panel", at, "", "", "", true, new Dictionary<string, string>(), colour, Pickable: false));

    /// <summary>
    /// A line of text in the interface's font at <paramref name="size"/> (settings-window units), translated unless <paramref name="translate"/> is
    /// false.
    /// </summary>
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
    /// The saving indicator while a picture is being saved: a ring of dots turning and the size being drawn, on a dark band at the top of the picture
    /// (the saved picture leaves the panel out, see <see cref="PhotoMode.GuidesDrawn"/>).
    /// </summary>
    void DrawBusy(int width, int height)
    {
        float w = Math.Min(width - 40 * _scale, 420 * _scale);
        float h = 44 * _scale;
        var at = new UiRect(MathF.Round((width - w) / 2), MathF.Round(28 * _scale), MathF.Round(w), MathF.Round(h));
        Fill(at, new(8 / 255f, 6 / 255f, 5 / 255f, 0.86f));
        Fill(at with { Y = at.Y + at.Height - MathF.Max(1, MathF.Round(_scale)), Height = MathF.Max(1, MathF.Round(_scale)) },
            SettingsCanvas.Brass with { W = 0.55f });

        const int dots = 8;
        float centreX = at.X + 26 * _scale, centreY = at.Y + at.Height / 2, radius = 9 * _scale, size = 3.5f * _scale;
        int head = (int)(Environment.TickCount64 / 90 % dots);
        for (int i = 0; i < dots; i++)
        {
            float angle = i * MathF.Tau / dots;
            float fade = 1 - (head - i + dots) % dots / (float)dots;
            Fill(new(MathF.Round(centreX + MathF.Sin(angle) * radius - size / 2), MathF.Round(centreY - MathF.Cos(angle) * radius - size / 2),
                MathF.Round(size), MathF.Round(size)), SettingsCanvas.LightInk with { W = 0.15f + 0.85f * fade });
        }

        var (pictureWidth, pictureHeight) = mode.PictureSize(width, height);
        Text($"Saving picture, {pictureWidth} x {pictureHeight} ...", new(at.X + 50 * _scale, at.Y, at.Width - 62 * _scale, at.Height), 14,
            SettingsCanvas.LightInk, translate: false);
    }

    /// <summary>
    /// The mode's last message (a saved picture's path), on a dark band at the top of the picture.
    /// </summary>
    void DrawMessage(int width)
    {
        if (mode.Message is not { } message) return;

        float w = Math.Min(width - 40 * _scale, 900 * _scale);
        float h = 40 * _scale;
        float line = MathF.Max(1, MathF.Round(_scale));
        var at = new UiRect(MathF.Round((width - w) / 2), MathF.Round(28 * _scale), MathF.Round(w), MathF.Round(h));
        Fill(at, new(8 / 255f, 6 / 255f, 5 / 255f, 0.78f));
        Fill(at with { Y = at.Y + at.Height - line, Height = line }, SettingsCanvas.Brass with { W = 0.55f });
        Text(message, new(at.X + 12 * _scale, at.Y, at.Width - 24 * _scale, at.Height), 13, SettingsCanvas.LightInk, "Center", translate: false,
            wrap: true);
    }

    #endregion
}
